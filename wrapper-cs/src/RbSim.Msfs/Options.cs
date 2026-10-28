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
// Options.cs: rbsim-msfs options from the JSON configuration file and the command line, and the source interface.

using System.Globalization;
using System.Text.Json;
using RbSim.Core;

namespace RbSim.Msfs;

public interface ISource
{
    void Start();
    void Stop();
}

/// <summary>One USB serial device fed with §4.4 device lines.</summary>
public sealed class SerialOutputConfig
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>COM port (e.g. COM5), or <c>file:PATH</c> to capture the lines to a file.</summary>
    public string Port { get; set; } = "";
    /// <summary>Irrelevant for the ESP32-S3 USB CDC console; used by real UARTs.</summary>
    public int Baud { get; set; } = 115200;
    /// <summary>rb02, rb04, rb05, rb07 or nmea-flarm (protocol §4.4).</summary>
    public string Profile { get; set; } = "nmea-flarm";
    /// <summary>Per-sentence rate overrides in Hz (gprmc, gpgga, pflau, pflaa, rbatt, rbadc, rbems, rbcyl); 0 removes one.</summary>
    public Dictionary<string, double>? Rates { get; set; }
    /// <summary>At most 10, the RB-05 store size (RB-SIM-ESP-080).</summary>
    public int MaxTraffic { get; set; } = Nmea.MaxFlarmTargets;
    /// <summary>Control lines; the ESP32 auto-reset circuit reacts to them, so both default off (verify per board).</summary>
    public bool Dtr { get; set; }
    public bool Rts { get; set; }
    /// <summary>Log what the device prints (boot banner, NMEA echoes, firmware logs).</summary>
    public bool LogDeviceOutput { get; set; } = true;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Port : Name;
}

/// <summary>Who may write to the simulator through <c>POST /api/control</c> and the WebSocket.</summary>
public sealed class ControlOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>False: only clients on this PC may send commands; reading stays open to the LAN.</summary>
    public bool AllowRemote { get; set; }
    public List<ActuatorGroup> Groups { get; set; } = [.. Enum.GetValues<ActuatorGroup>()];
}

public sealed class WrapperOptions
{
    public const string DefaultConfigName = "rbsim-msfs.json";

    public string? ConfigPath { get; set; }
    public string Source { get; set; } = "msfs";
    public string Listen { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8090;
    public string? SimConnectDll { get; set; }
    /// <summary>SIM_FRAME interval: frames skipped between deliveries (0 = every frame).</summary>
    public int FrameSkip { get; set; }
    public double MaxRateHz { get; set; } = 30;
    public double TrafficRadiusM { get; set; } = 37_040;
    public MsfsProfile Profile { get; } = new();
    public EmsUnits Ems { get; } = new();
    public List<SerialOutputConfig> Serial { get; } = [];
    public ControlOptions Control { get; } = new();
    public bool ListPorts { get; set; }

    public const string Usage = """
        rbsim-msfs: MSFS SimConnect -> RB avionics status over HTTP, WebSocket and USB serial

        Options (they override the configuration file):
          --config PATH               JSON configuration (default: rbsim-msfs.json in the current
                                      folder, then beside the executable; see rbsim-msfs.example.json)
          --source msfs|synthetic     data source (default msfs; synthetic needs no simulator)
          --listen ADDR               address to bind (default 0.0.0.0; use 127.0.0.1 for this PC only)
          --port N                    HTTP port (default 8090)
          --simconnect-dll PATH       SimConnect.dll to load (default: MSFS2024_SDK, MSFS_SDK, C:\MSFS 2024 SDK, C:\MSFS SDK)
          --frame-skip N              SIM_FRAME interval, frames skipped between deliveries (default 0)
          --max-rate HZ               maximum ownship update rate (default 30)
          --traffic-radius-m M        traffic radius, at most 200000 (default 37040 = 20 NM)
          --ball-full-scale-deg D     slip in degrees at full ball deflection (default 10, verify per aircraft)
          --ems-map inhg|psi|bar      EMS manifold pressure unit (default inhg)
          --ems-oil bar|psi|inhg      EMS oil pressure unit (default bar)
          --ems-fuel-press bar|psi|inhg  EMS fuel pressure unit, $RBEMS only (default bar)
          --ems-fuel l|gal            EMS fuel quantity unit (default l)
          --serial PORT[=PROFILE]     add a serial output, e.g. COM5=rb07 or file:out.nmea (default profile nmea-flarm)
          --no-control                refuse all writes to the simulator (read-only)
          --control-remote            accept control commands from other computers too (default: this PC only)
          --list-ports                list the serial ports and exit
          --help                      this text

        Device profiles (serial): rb02 ($RBATT 20 Hz, $RBADC 10 Hz, $GPRMC+$GPGGA 5 Hz),
          rb04 ($RBEMS 5 Hz, $RBCYL 0.5 Hz), rb05 ($GPRMC, $PFLAA, $PFLAU 1 Hz), rb07 (rb02 + rb05),
          nmea-flarm ($GPRMC+$GPGGA 5 Hz, $PFLAU+$PFLAA 1 Hz).

        Endpoints:
          GET /                 live status page
          GET /api/state        RB-SIM-IF/1 state message (the body of POST /sim/state)
          GET /api/traffic      RB-SIM-IF/1 traffic message
          GET /api/simstate     canonical SimState, SI units
          GET /api/status       wrapper, SimConnect and serial health
          GET /api/radio        COM1/2, NAV1/2 and transponder read-back
          GET /api/control      every writable control: read-back and the last command's status
          POST /api/control     JSON {"com1_standby_hz": 121500000, "com1_swap": true, "hdg_bug_mag_deg": 270, ...}
          WS  /ws?rate=HZ       state messages at HZ (1-30, default 10) and traffic at 1 Hz, unchanged;
                                send {"control": {...}} to command, add &control=1 for control status at 2 Hz
        """;

    static readonly JsonSerializerOptions ConfigJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A misspelt key is an error, not a silently ignored setting.
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Defaults, then the configuration file, then the command line.</summary>
    public static WrapperOptions Parse(string[] args, out bool help)
    {
        var o = new WrapperOptions();
        help = false;

        string? cfg = null;
        int ci = Array.IndexOf(args, "--config");
        if (ci >= 0)
        {
            if (ci + 1 >= args.Length) throw new ArgumentException("--config needs a value");
            cfg = args[ci + 1];
            if (!File.Exists(cfg)) throw new ArgumentException($"configuration file not found: {cfg}");
        }
        else
        {
            cfg = new[] { Path.Combine(Environment.CurrentDirectory, DefaultConfigName), Path.Combine(AppContext.BaseDirectory, DefaultConfigName) }
                .FirstOrDefault(File.Exists);
        }
        if (cfg is not null) o.LoadConfig(cfg);

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            double Num() => double.Parse(Next(), CultureInfo.InvariantCulture);
            switch (a)
            {
                case "--help" or "-h": help = true; break;
                case "--config": Next(); break; // already loaded
                case "--source": o.Source = Next().ToLowerInvariant(); break;
                case "--listen": o.Listen = Next(); break;
                case "--port": o.Port = (int)Num(); break;
                case "--simconnect-dll": o.SimConnectDll = Next(); break;
                case "--frame-skip": o.FrameSkip = Math.Max(0, (int)Num()); break;
                case "--max-rate": o.MaxRateHz = Math.Clamp(Num(), 1, 60); break;
                case "--traffic-radius-m": o.TrafficRadiusM = Math.Clamp(Num(), 0, 200_000); break;
                case "--ball-full-scale-deg": o.Profile.BallFullScaleDeg = Num(); break;
                case "--ems-map": o.Ems.ManifoldPressure = Pressure(Next()); break;
                case "--ems-oil": o.Ems.OilPressure = Pressure(Next()); break;
                case "--ems-fuel-press": o.Ems.FuelPressure = Pressure(Next()); break;
                case "--ems-fuel": o.Ems.Fuel = Volume(Next()); break;
                case "--serial": o.Serial.Add(SerialArg(Next())); break;
                case "--no-control": o.Control.Enabled = false; break;
                case "--control-remote": o.Control.AllowRemote = true; break;
                case "--list-ports": o.ListPorts = true; break;
                default: throw new ArgumentException($"unknown option {a}");
            }
        }
        o.Validate();
        return o;
    }

    void LoadConfig(string path)
    {
        ConfigFile? f;
        try
        {
            f = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(path), ConfigJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{path}: {ex.Message}");
        }
        if (f is null) return;
        ConfigPath = Path.GetFullPath(path);
        if (f.Source is not null) Source = f.Source.ToLowerInvariant();
        if (f.Listen is not null) Listen = f.Listen;
        if (f.Port is int port) Port = port;
        if (f.SimconnectDll is not null) SimConnectDll = f.SimconnectDll;
        if (f.FrameSkip is int skip) FrameSkip = Math.Max(0, skip);
        if (f.MaxRateHz is double rate) MaxRateHz = Math.Clamp(rate, 1, 60);
        if (f.TrafficRadiusM is double radius) TrafficRadiusM = Math.Clamp(radius, 0, 200_000);
        if (f.Aircraft is { } ac)
        {
            if (ac.BallFullScaleDeg is double ball) Profile.BallFullScaleDeg = ball;
            if (ac.ChtSpreadC is { } cht) Profile.ChtSpreadC = Spread(cht, "aircraft.cht_spread_c");
            if (ac.EgtSpreadC is { } egt) Profile.EgtSpreadC = Spread(egt, "aircraft.egt_spread_c");
        }
        if (f.Ems is { } ems)
        {
            if (ems.ManifoldPressure is not null) Ems.ManifoldPressure = Pressure(ems.ManifoldPressure);
            if (ems.OilPressure is not null) Ems.OilPressure = Pressure(ems.OilPressure);
            if (ems.FuelPressure is not null) Ems.FuelPressure = Pressure(ems.FuelPressure);
            if (ems.Fuel is not null) Ems.Fuel = Volume(ems.Fuel);
        }
        if (f.Serial is not null) Serial.AddRange(f.Serial);
        if (f.Control is { } c)
        {
            if (c.Enabled is bool en) Control.Enabled = en;
            if (c.AllowRemote is bool remote) Control.AllowRemote = remote;
            if (c.Groups is { } groups) Control.Groups = [.. groups.Select(Actuators.ParseGroup).Distinct()];
        }
    }

    void Validate()
    {
        if (Source is not ("msfs" or "synthetic")) throw new ArgumentException($"unknown source {Source}");
        if (Port is < 1 or > 65535) throw new ArgumentException($"port out of range: {Port}");
        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Serial.Count; i++)
        {
            var s = Serial[i];
            if (!s.Enabled) continue;
            string where = $"serial[{i}] ({s.DisplayName})";
            if (string.IsNullOrWhiteSpace(s.Port)) throw new ArgumentException($"{where}: port is required");
            if (!ports.Add(s.Port)) throw new ArgumentException($"{where}: port {s.Port} is used twice");
            try
            {
                DeviceProfiles.Resolve(s.Profile, s.Rates);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"{where}: {ex.Message}");
            }
            s.MaxTraffic = Math.Clamp(s.MaxTraffic, 0, Nmea.MaxFlarmTargets);
        }
    }

    static SerialOutputConfig SerialArg(string v)
    {
        int eq = v.LastIndexOf('=');
        return eq > 0
            ? new SerialOutputConfig { Port = v[..eq], Profile = v[(eq + 1)..] }
            : new SerialOutputConfig { Port = v };
    }

    static double[] Spread(double[] v, string name) =>
        v.Length == 4 ? v : throw new ArgumentException($"{name} needs 4 values");

    static PressureUnit Pressure(string s) => s.ToLowerInvariant() switch
    {
        "inhg" => PressureUnit.InHg,
        "psi" => PressureUnit.Psi,
        "bar" => PressureUnit.Bar,
        _ => throw new ArgumentException($"unknown pressure unit {s}"),
    };

    static VolumeUnit Volume(string s) => s.ToLowerInvariant() switch
    {
        "l" => VolumeUnit.Liters,
        "gal" => VolumeUnit.UsGallons,
        _ => throw new ArgumentException($"unknown volume unit {s}"),
    };

    // The file's shape; keys are snake_case (see rbsim-msfs.example.json).
    sealed class ConfigFile
    {
        public string? Source { get; set; }
        public string? Listen { get; set; }
        public int? Port { get; set; }
        public string? SimconnectDll { get; set; }
        public int? FrameSkip { get; set; }
        public double? MaxRateHz { get; set; }
        public double? TrafficRadiusM { get; set; }
        public AircraftSection? Aircraft { get; set; }
        public EmsSection? Ems { get; set; }
        public List<SerialOutputConfig>? Serial { get; set; }
        public ControlSection? Control { get; set; }
    }

    sealed class ControlSection
    {
        public bool? Enabled { get; set; }
        public bool? AllowRemote { get; set; }
        public List<string>? Groups { get; set; }
    }

    sealed class AircraftSection
    {
        public double? BallFullScaleDeg { get; set; }
        public double[]? ChtSpreadC { get; set; }
        public double[]? EgtSpreadC { get; set; }
    }

    sealed class EmsSection
    {
        public string? ManifoldPressure { get; set; }
        public string? OilPressure { get; set; }
        public string? FuelPressure { get; set; }
        public string? Fuel { get; set; }
    }
}
