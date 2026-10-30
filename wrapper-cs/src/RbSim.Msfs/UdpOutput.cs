// This file is part of RB.
//
// Copyright (C) 2026 XIAPROJECTS SRL
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, version 3.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.
//
// This source is part of the project RB: RB Simulator
// (github.com/xiaprojects/rbsimulator).
//
// Community edition will be free for all builders and personal use as defined by the licensing model
// Dual licensing for commercial agreement is available
// Please join Discord community
//
// UdpOutput.cs: sends device lines as UDP datagrams, e.g. the autopilot guidance to RB-01's UDP autopilot input (D25).

using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RbSim.Core;

namespace RbSim.Msfs;

/// <summary>Per-target health for <c>/api/status</c>.</summary>
public sealed class UdpStatus
{
    public required string Name { get; init; }
    public required string Target { get; init; }
    public required string Profile { get; init; }
    public long Datagrams { get; set; }
    public long LinesSent { get; set; }
    public RateMeter Lines { get; } = new();
    public string? LastError { get; set; }
}

/// <summary>
/// Each tick's due lines go out as one datagram, padded to a constant length (Nmea.Datagram).
/// Nothing is sent while no line is due, e.g. without guidance: RB-01 then keeps the last leg it
/// received, as with an EFB that stops sending (verify on the bench, T3.4).
/// </summary>
public sealed class UdpOutput : ISource
{
    const int TickMs = 10;

    readonly StateStore _store;
    readonly UdpOutputConfig _cfg;
    readonly ILogger _log;
    readonly DeviceLineGenerator _gen;
    readonly ManualResetEventSlim _stopEvt = new(false);
    Thread? _thread;
    volatile bool _stop;

    public UdpStatus Status { get; }

    public UdpOutput(StateStore store, UdpOutputConfig cfg, EmsUnits ems, ILogger log)
    {
        _store = store;
        _cfg = cfg;
        _log = log;
        _gen = new DeviceLineGenerator(DeviceProfiles.Resolve(cfg.Profile, cfg.Rates), ems: ems);
        Status = new UdpStatus { Name = cfg.DisplayName, Target = cfg.Target, Profile = cfg.Profile };
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "udp " + _cfg.Target };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _stopEvt.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    void Run()
    {
        string rates = string.Join(", ", _gen.Rates.Select(r => $"{r.Key.ToString().ToUpperInvariant()} {r.Value:0.#} Hz"));
        _log.LogInformation("{Name}: udp {Target} profile {Profile} ({Rates})", Status.Name, _cfg.Target, _cfg.Profile, rates);
        using var udp = new UdpClient();
        string lastErr = "";
        bool warnedLong = false;
        while (!_stop)
        {
            double now = StateStore.Now;
            var lines = _gen.Due(_store.Snapshot, now);
            if (lines.Count > 0)
            {
                byte[] datagram = Nmea.Datagram(lines, _cfg.PadBytes);
                if (_cfg.PadBytes > 0 && datagram.Length > _cfg.PadBytes && !warnedLong)
                {
                    warnedLong = true;
                    _log.LogWarning("{Name}: a datagram of {Bytes} bytes exceeds pad_bytes {Pad}; raise pad_bytes", Status.Name, datagram.Length, _cfg.PadBytes);
                }
                try
                {
                    udp.Send(datagram, datagram.Length, _cfg.Host, _cfg.Port);
                    Status.Datagrams++;
                    Status.LinesSent += lines.Count;
                    for (int i = 0; i < lines.Count; i++) Status.Lines.Tick(now);
                    Status.LastError = null;
                    lastErr = "";
                }
                // ConnectionRefused is the ICMP port unreachable of an earlier datagram: RB-01 is up
                // but its UDP autopilot input is off. Keep sending; it may be switched on.
                catch (SocketException ex)
                {
                    Status.LastError = ex.Message;
                    if (ex.Message != lastErr) _log.LogWarning("{Name}: {Error}", Status.Name, ex.Message);
                    lastErr = ex.Message;
                }
            }
            if (_stopEvt.Wait(TickMs)) return;
        }
    }
}
