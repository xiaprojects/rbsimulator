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
// SerialOutput.cs: feeds one USB-connected RB device (RB-02/05/07) with NMEA, FLARM and RB lines, reconnecting on unplug.

using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Logging;
using RbSim.Core;

namespace RbSim.Msfs;

/// <summary>Per-device health for <c>/api/status</c>.</summary>
public sealed class SerialStatus
{
    public required string Name { get; init; }
    public required string Port { get; init; }
    public required string Profile { get; init; }
    public bool Open { get; set; }
    public long LinesSent { get; set; }
    public long BytesSent { get; set; }
    public long WriteTimeouts { get; set; }
    public RateMeter Lines { get; } = new();
    public string? LastError { get; set; }
    public string? LastDeviceLine { get; set; }
}

public sealed class SerialOutput : ISource
{
    const double RetryS = 2.0;
    const int TickMs = 10;
    const int MaxLoggedDeviceLinesPerS = 10;

    readonly StateStore _store;
    readonly SerialOutputConfig _cfg;
    readonly ILogger _log;
    readonly DeviceLineGenerator _gen;
    readonly ManualResetEventSlim _stopEvt = new(false);
    Thread? _thread;
    volatile bool _stop;

    public SerialStatus Status { get; }

    public SerialOutput(StateStore store, SerialOutputConfig cfg, EmsUnits ems, ILogger log)
    {
        _store = store;
        _cfg = cfg;
        _log = log;
        _gen = new DeviceLineGenerator(DeviceProfiles.Resolve(cfg.Profile, cfg.Rates), cfg.MaxTraffic, ems);
        Status = new SerialStatus { Name = cfg.DisplayName, Port = cfg.Port, Profile = cfg.Profile };
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "serial " + _cfg.Port };
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
        _log.LogInformation("{Name}: {Port} profile {Profile} ({Rates})", Status.Name, _cfg.Port, _cfg.Profile, rates);
        string lastErr = "";
        while (!_stop)
        {
            try
            {
                if (_cfg.Port.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    RunFile(_cfg.Port[5..]);
                else
                    RunSerial();
            }
            // DllNotFoundException: on Linux, libSystem.IO.Ports.Native.so is missing beside the
            // executable. One device failing must not take the web service down with it.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                or DllNotFoundException)
            {
                Status.LastError = ex.Message;
                // Log a failure once, not every retry while the device stays unplugged.
                if (ex.Message != lastErr) _log.LogWarning("{Name}: {Error}; retrying every {Retry} s", Status.Name, ex.Message, RetryS);
                lastErr = ex.Message;
            }
            Status.Open = false;
            _stopEvt.Wait(TimeSpan.FromSeconds(RetryS));
        }
    }

    void RunSerial()
    {
        using var port = new SerialPort(_cfg.Port, _cfg.Baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = _cfg.Dtr,
            RtsEnable = _cfg.Rts,
            WriteTimeout = 500,
            Encoding = Encoding.ASCII,
        };
        port.Open();
        Opened();
        using var readerStop = new CancellationTokenSource();
        var reader = Task.Run(() => ReadDevice(port.BaseStream, readerStop.Token));
        try
        {
            Pump(bytes =>
            {
                try
                {
                    port.BaseStream.Write(bytes);
                    return true;
                }
                catch (TimeoutException)
                {
                    // The device is not draining its USB buffer: drop these lines, send the latest next.
                    Status.WriteTimeouts++;
                    return false;
                }
            });
        }
        finally
        {
            readerStop.Cancel();
        }
    }

    void RunFile(string path)
    {
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        Opened();
        Pump(bytes =>
        {
            file.Write(bytes);
            file.Flush();
            return true;
        });
    }

    void Opened()
    {
        Status.Open = true;
        Status.LastError = null;
        _log.LogInformation("{Name}: {Port} open", Status.Name, _cfg.Port);
    }

    void Pump(Func<byte[], bool> write)
    {
        var sb = new StringBuilder(512);
        while (!_stop)
        {
            double now = StateStore.Now;
            var lines = _gen.Due(_store.Snapshot, now);
            if (lines.Count > 0)
            {
                sb.Clear();
                foreach (var l in lines) sb.Append(l);
                var bytes = Encoding.ASCII.GetBytes(sb.ToString());
                if (write(bytes))
                {
                    Status.LinesSent += lines.Count;
                    Status.BytesSent += bytes.Length;
                    for (int i = 0; i < lines.Count; i++) Status.Lines.Tick(now);
                }
            }
            if (_stopEvt.Wait(TickMs)) return;
        }
    }

    /// <summary>
    /// Drains what the device prints so its USB buffer never fills, and logs it: the boot
    /// banner tells a SIM firmware from a flight one (RB-SIM-ESP-060).
    /// </summary>
    async Task ReadDevice(Stream s, CancellationToken ct)
    {
        var buf = new byte[1024];
        var line = new StringBuilder();
        double windowStart = 0;
        int logged = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await s.ReadAsync(buf, ct);
                if (n <= 0) return;
                for (int i = 0; i < n; i++)
                {
                    char c = (char)buf[i];
                    if (c == '\n' || line.Length >= 512)
                    {
                        string text = line.ToString().TrimEnd('\r');
                        line.Clear();
                        if (text.Length == 0) continue;
                        Status.LastDeviceLine = text;
                        if (!_cfg.LogDeviceOutput) continue;
                        double now = StateStore.Now;
                        if (now - windowStart >= 1) { windowStart = now; logged = 0; }
                        if (logged++ < MaxLoggedDeviceLinesPerS) _log.LogInformation("{Name} < {Line}", Status.Name, text);
                    }
                    else if (c >= 0x20 && c <= 0x7E)
                    {
                        line.Append(c);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // port closed or unplugged; the writer notices and reconnects
        }
    }
}
