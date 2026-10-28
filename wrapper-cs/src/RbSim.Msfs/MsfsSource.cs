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
// MsfsSource.cs: the SimConnect transport; connects, subscribes, decodes and feeds MsfsMap into the StateStore.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using RbSim.Core;
using static RbSim.Msfs.SimConnectNative;

namespace RbSim.Msfs;

public sealed class MsfsSource(StateStore store, WrapperOptions options, ActuatorManager actuators, ILogger log) : ISource
{
    enum Def : uint { User = 1, Title = 2, Traffic = 3 }
    enum Req : uint { User = 1, Title = 2, Traffic = 3 }
    enum Ev : uint { Sim = 1, PauseEx1 = 2, FlightLoaded = 3, AircraftLoaded = 4, PositionChanged = 5, Crashed = 6 }

    static readonly (Ev id, string name)[] SystemEvents =
    [
        (Ev.Sim, "Sim"), (Ev.PauseEx1, "Pause_EX1"), (Ev.FlightLoaded, "FlightLoaded"),
        (Ev.AircraftLoaded, "AircraftLoaded"), (Ev.PositionChanged, "PositionChanged"), (Ev.Crashed, "Crashed"),
    ];

    const int RecvDataOffset = 40; // SIMCONNECT_RECV_SIMOBJECT_DATA.dwData
    const double RetryS = 2.0;     // reconnect well within 5 s (RB-SIM-ADP-030)
    const double ReopenAfterSilenceS = 10.0;
    const uint ClientEventBase = 100; // client event IDs; below are the system events above

    readonly AutoResetEvent _evt = new(false);
    readonly Dictionary<uint, (Def def, int index)> _sendIds = [];
    readonly Dictionary<string, uint> _eventIds = [];
    readonly Dictionary<uint, string> _eventSendIds = [];
    readonly Dictionary<Def, DataLayout> _layouts = new()
    {
        [Def.User] = new DataLayout(MsfsDatums.User),
        [Def.Title] = new DataLayout(MsfsDatums.Title),
        [Def.Traffic] = new DataLayout(MsfsDatums.Traffic),
    };
    readonly HashSet<Def> _dirty = [];
    readonly PauseDetector _pause = new();
    readonly LinkMonitor _link = new();
    readonly TeleportDetector _teleport = new();

    Thread? _thread;
    volatile bool _stop;
    IntPtr _h;
    uint _userObjectId = uint.MaxValue;
    List<TrafficTarget> _trafficBuf = [];
    double _lastPublish, _lastTrafficPoll, _lastHousekeep, _lastFrame, _openedAt;

    SourceStatus Status => store.Status;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "simconnect" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _evt.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    void Run()
    {
        // Keep looking, so installing the SDK while the wrapper runs needs no restart.
        string lastErr = "";
        while (!Load(options.SimConnectDll, out var err))
        {
            Status.LastError = err;
            if (err != lastErr) log.LogError("{Error}", err);
            lastErr = err;
            _evt.WaitOne(TimeSpan.FromSeconds(5));
            if (_stop) return;
        }
        Status.DllPath = LoadedPath;
        log.LogInformation("Using {Dll}", LoadedPath);

        while (!_stop)
        {
            if (!TryOpen())
            {
                _evt.WaitOne(TimeSpan.FromSeconds(RetryS));
                continue;
            }
            try
            {
                Pump();
            }
            catch (Exception ex)
            {
                Status.LastError = ex.Message;
                log.LogError(ex, "SimConnect session failed");
            }
            Close();
            if (!_stop) _evt.WaitOne(TimeSpan.FromSeconds(1));
        }
    }

    bool TryOpen()
    {
        int hr = SimConnect_Open(out _h, "RB Simulator wrapper", IntPtr.Zero, 0, _evt.SafeWaitHandle.DangerousGetHandle(), 0);
        if (hr < 0)
        {
            _h = IntPtr.Zero;
            Status.LastError = $"SimConnect_Open failed 0x{hr:X8}: is MSFS running?";
            return false;
        }
        _openedAt = _lastFrame = StateStore.Now;
        _link.OnOpen();
        _pause.Reset();
        _userObjectId = uint.MaxValue;
        Status.LinkOpen = true;
        Status.LastError = null;

        foreach (var def in _layouts.Keys) Define(def);
        foreach (var (id, name) in SystemEvents) Check(SimConnect_SubscribeToSystemEvent(_h, (uint)id, name), "SubscribeToSystemEvent " + name);
        MapActuatorEvents();
        RequestUser();
        RequestTitle();
        return true;
    }

    void Close()
    {
        if (_h != IntPtr.Zero) SimConnect_Close(_h);
        _h = IntPtr.Zero;
        _link.OnClose();
        _sendIds.Clear();
        _eventSendIds.Clear();
        Status.LinkOpen = false;
        store.Update(s => { s.Sim.Connected = false; s.Sim.Paused = false; });
        log.LogInformation("SimConnect closed");
    }

    void Pump()
    {
        while (!_stop)
        {
            _evt.WaitOne(20);
            Drain();
            if (!_link.Open) return; // Quit received

            foreach (var def in _dirty) Rebuild(def);
            _dirty.Clear();

            double now = StateStore.Now;
            foreach (var w in actuators.Due(store.Snapshot, now)) Transmit(w);
            if (now - _lastTrafficPoll >= 1.0)
            {
                _lastTrafficPoll = now;
                uint radius = (uint)Math.Clamp(options.TrafficRadiusM, 0, 200_000);
                Check(SimConnect_RequestDataOnSimObjectType(_h, (uint)Req.Traffic, (uint)Def.Traffic, radius, SimobjectTypeAircraft), "RequestDataOnSimObjectType");
            }
            if (now - _lastHousekeep >= 0.1)
            {
                _lastHousekeep = now;
                Housekeep(now);
            }
            // A sim that died without Quit leaves a silent handle: reopen it.
            if (now - Math.Max(_lastFrame, _openedAt) > ReopenAfterSilenceS) return;
        }
    }

    void Drain()
    {
        while (_h != IntPtr.Zero && SimConnect_GetNextDispatch(_h, out var p, out uint cb) == SOk)
        {
            if (cb < 12) continue;
            var buf = new byte[cb];
            Marshal.Copy(p, buf, 0, (int)cb);
            Handle(buf);
        }
    }

    void Handle(byte[] b)
    {
        switch (U32(b, 8))
        {
            case RecvOpen:
                OnOpen(b);
                break;
            case RecvQuit:
                log.LogInformation("SimConnect quit received");
                _link.OnClose();
                break;
            case RecvException:
                OnException(U32(b, 12), U32(b, 16), U32(b, 20));
                break;
            case RecvEvent:
            case RecvEventFilename:
                OnEvent((Ev)U32(b, 16), U32(b, 20));
                break;
            case RecvSimobjectData:
                if (U32(b, 12) == (uint)Req.User) OnUserFrame(U32(b, 16), U32(b, 36), b.AsSpan(RecvDataOffset));
                else if (U32(b, 12) == (uint)Req.Title) OnTitle(b.AsSpan(RecvDataOffset));
                break;
            case RecvSimobjectDataBytype:
                if (U32(b, 12) == (uint)Req.Traffic) OnTraffic(U32(b, 16), U32(b, 28), U32(b, 32), b.AsSpan(RecvDataOffset));
                break;
        }
    }

    void OnOpen(byte[] b)
    {
        // SIMCONNECT_RECV_OPEN: szApplicationName[256], then application and SimConnect versions.
        var nameSpan = b.AsSpan(12, 256);
        int z = nameSpan.IndexOf((byte)0);
        string app = Encoding.Latin1.GetString(z >= 0 ? nameSpan[..z] : nameSpan);
        uint major = U32(b, 268), minor = U32(b, 272), buildMajor = U32(b, 276), buildMinor = U32(b, 280);
        uint scMajor = U32(b, 284), scMinor = U32(b, 288);
        // Major 11 = MSFS 2020, 12 = MSFS 2024 (verify, L1).
        string simName = major switch { 11 => "MSFS 2020", 12 => "MSFS 2024", _ => app };
        string version = $"{major}.{minor}.{buildMajor}.{buildMinor}";
        store.Update(s => { s.Sim.SimName = simName; s.Sim.SimVersion = version; });
        // The sim version is logged with every session (RB-SIM-MSF-040).
        log.LogInformation("Connected to {App} ({Sim}) {Version}, SimConnect {ScMajor}.{ScMinor}", app, simName, version, scMajor, scMinor);
    }

    void OnException(uint code, uint sendId, uint index)
    {
        string text = $"{ExceptionName(code)} (send {sendId}, index {index})";
        if (_eventSendIds.TryGetValue(sendId, out var ev))
        {
            // An event this sim or aircraft does not know: only its actuators go unsupported.
            text += $": event {ev}";
            actuators.MarkUnsupported(ev);
        }
        else if (_sendIds.TryGetValue(sendId, out var e))
        {
            var layout = _layouts[e.def];
            text += $": datum \"{layout.All[e.index].Name}\" in {e.def}";
            // Only the named datum goes invalid; the adapter keeps running (RB-SIM-MSF-130).
            if (layout.Reject(e.index)) _dirty.Add(e.def);
        }
        Status.LastException = text;
        log.LogWarning("SimConnect exception {Text}", text);
    }

    void OnEvent(Ev id, uint data)
    {
        switch (id)
        {
            case Ev.Sim:
                _link.OnSimEvent(data);
                break;
            case Ev.PauseEx1:
                _pause.OnPauseEvent(data);
                break;
            case Ev.FlightLoaded:
            case Ev.PositionChanged:
                _teleport.OnEvent();
                break;
            case Ev.AircraftLoaded:
                _teleport.OnEvent();
                RequestTitle();
                break;
            case Ev.Crashed:
                log.LogInformation("Sim reports a crash");
                break;
        }
    }

    void OnUserFrame(uint objectId, uint defineCount, ReadOnlySpan<byte> block)
    {
        double now = StateStore.Now;
        var layout = _layouts[Def.User];
        if (!BlockMatches(layout, defineCount, block, "user")) return;
        _userObjectId = objectId;
        layout.Decode(block, out var raw, out _);

        _lastFrame = now;
        _link.OnFrame(now);
        _pause.OnFrame(raw[(int)U.SimulationTime], now);
        Status.Frames.Tick(now);
        double? lat = Units.Finite(raw[(int)U.Lat]), lon = Units.Finite(raw[(int)U.Lon]);
        _teleport.OnFrame(lat, lon, raw[(int)U.SlewActive] > 0.5, now);

        // SIM_FRAME follows the sim frame rate; keep delivery at or below the configured maximum (RB-SIM-MSF-020).
        if (now - _lastPublish < 1.0 / options.MaxRateHz - 0.002) return;
        _lastPublish = now;
        bool connected = _link.Connected(now, _pause.EventFlags != 0);
        bool paused = _pause.Paused(now);
        int seq = _teleport.Seq;
        store.Update(s =>
        {
            MsfsMap.ApplyUser(s, raw, now, options.Profile);
            s.Sim.TeleportSeq = seq;
            s.Sim.Connected = connected;
            s.Sim.Paused = paused;
        }, newFrame: true);
        Status.Published.Tick(now);
    }

    void OnTitle(ReadOnlySpan<byte> block)
    {
        _layouts[Def.Title].Decode(block, out _, out var str);
        string title = (str[0] ?? "").Trim();
        store.Update(s => s.Sim.Aircraft = title);
        log.LogInformation("Aircraft: {Title}", title);
    }

    void OnTraffic(uint objectId, uint entry, uint outOf, ReadOnlySpan<byte> block)
    {
        double now = StateStore.Now;
        if (entry <= 1) _trafficBuf = [];
        var layout = _layouts[Def.Traffic];
        if (objectId != _userObjectId && block.Length >= layout.BlockSize)
        {
            layout.Decode(block, out var num, out var str);
            if (MsfsMap.MapTraffic(num, str, objectId, now) is { } t) _trafficBuf.Add(t);
        }
        if (entry >= outOf)
        {
            var set = _trafficBuf;
            _trafficBuf = [];
            store.Update(s => { s.Traffic = set; s.TrafficTMono = now; });
        }
    }

    void Housekeep(double now)
    {
        bool connected = _link.Connected(now, _pause.EventFlags != 0);
        bool paused = _pause.Paused(now);
        var snap = store.Snapshot;
        if (snap.Sim.Connected != connected || snap.Sim.Paused != paused)
        {
            store.Update(s => { s.Sim.Connected = connected; s.Sim.Paused = paused; });
            log.LogInformation("Link {State}{Paused}", connected ? "connected" : "lost", paused ? ", paused" : "");
        }
        Status.SimRunning = _link.SimRunning;
        Status.PauseFlags = _pause.EventFlags;
    }

    /// <summary>
    /// Drops a block whose shape does not match the definition, so a datum SimConnect left out
    /// silently can never shift the other values into the wrong fields.
    /// </summary>
    bool BlockMatches(DataLayout layout, uint defineCount, ReadOnlySpan<byte> block, string what)
    {
        int active = layout.Active.Count();
        if (defineCount == active && block.Length >= layout.BlockSize) return true;
        Status.LastError = $"{what} data block mismatch: {defineCount} datums / {block.Length} bytes, expected {active} / {layout.BlockSize}";
        return false;
    }

    void Define(Def def)
    {
        var layout = _layouts[def];
        SimConnect_ClearDataDefinition(_h, (uint)def);
        foreach (int i in layout.Active)
        {
            var d = layout.All[i];
            Check(SimConnect_AddToDataDefinition(_h, (uint)def, d.Name, d.Unit, DataType(d.Type), 0f, Unused), "AddToDataDefinition " + d.Name);
            SimConnect_GetLastSentPacketID(_h, out uint sendId);
            _sendIds[sendId] = (def, i);
        }
    }

    void Rebuild(Def def)
    {
        log.LogWarning("Rebuilding {Def} definition without: {Rejected}", def, string.Join(", ", _layouts[def].Rejected.Select(i => _layouts[def].All[i].Name)));
        if (def == Def.User)
            SimConnect_RequestDataOnSimObject(_h, (uint)Req.User, (uint)Def.User, ObjectIdUser, PeriodNever, 0, 0, 0, 0);
        Define(def);
        if (def == Def.User) RequestUser();
        if (def == Def.Title) RequestTitle();
        Status.RejectedDatums = [.. _layouts.Values.SelectMany(l => l.Rejected.Select(i => l.All[i].Name))];
    }

    void MapActuatorEvents()
    {
        actuators.ResetSupport();
        uint id = ClientEventBase;
        foreach (string name in Actuators.EventNames)
        {
            _eventIds[name] = id;
            Check(SimConnect_MapClientEventToSimEvent(_h, id++, name), "MapClientEventToSimEvent " + name);
            SimConnect_GetLastSentPacketID(_h, out uint sendId);
            _eventSendIds[sendId] = name;
        }
    }

    void Transmit(ActuatorWrite w)
    {
        // The first (and only) parameter goes in dwData; the index parameter of HEADING_BUG_SET
        // and AP_ALT_VAR_SET_ENGLISH needs TransmitClientEvent_EX1 and is left at its default (verify, L7).
        Check(SimConnect_TransmitClientEvent(_h, ObjectIdUser, _eventIds[w.Event], w.Data, GroupPriorityHighest, EventFlagGroupIdIsPriority),
            "TransmitClientEvent " + w.Event);
        SimConnect_GetLastSentPacketID(_h, out uint sendId);
        if (_eventSendIds.Count > 1024) _eventSendIds.Clear(); // only recent send IDs matter
        _eventSendIds[sendId] = w.Event;
        log.LogInformation("Write {Actuator}: {Event} {Data}", w.Actuator.Name, w.Event, w.Data);
    }

    void RequestUser() =>
        Check(SimConnect_RequestDataOnSimObject(_h, (uint)Req.User, (uint)Def.User, ObjectIdUser, PeriodSimFrame, 0, 0, (uint)options.FrameSkip, 0), "RequestDataOnSimObject user");

    void RequestTitle() =>
        Check(SimConnect_RequestDataOnSimObject(_h, (uint)Req.Title, (uint)Def.Title, ObjectIdUser, PeriodSecond, FlagChanged, 0, 0, 0), "RequestDataOnSimObject title");

    static uint DataType(DatumType t) => t switch
    {
        DatumType.Float64 => DatatypeFloat64,
        DatumType.String8 => DatatypeString8,
        DatumType.String32 => DatatypeString32,
        DatumType.String64 => DatatypeString64,
        DatumType.String256 => DatatypeString256,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed 0x{hr:X8}");
    }

    static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset, 4));
}
