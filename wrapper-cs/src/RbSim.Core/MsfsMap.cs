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
// MsfsMap.cs: pure MSFS SimVar → SimState mapping (protocol §3.1); no SimConnect here (RB-SIM-ADP-010).

namespace RbSim.Core;

public enum DatumType { Float64, String8, String32, String64, String256 }

/// <summary>One SimConnect data-definition entry. Every numeric datum names its unit (RB-SIM-ADP-020).</summary>
public sealed record Datum(string Name, string? Unit, DatumType Type = DatumType.Float64)
{
    public int Size => Type switch
    {
        DatumType.Float64 => 8,
        DatumType.String8 => 8,
        DatumType.String32 => 32,
        DatumType.String64 => 64,
        DatumType.String256 => 256,
        _ => throw new ArgumentOutOfRangeException(nameof(Type)),
    };
}

/// <summary>Indexes into <see cref="MsfsDatums.User"/>; the order is the definition order.</summary>
public enum U
{
    Lat, Lon, AltMsl, GroundSpeed, GpsTrack, VelWorldX, VelWorldY, VelWorldZ, VerticalSpeed, Magvar,
    Pitch, Bank, HdgTrue, HdgMag, TurnRate, Ball, GForce,
    Ias, Tas, PressureAlt, AmbientPressure, AmbientTemp, WindDir, WindVel, Kohlsman,
    OnGround, SlewActive,
    ApMaster, ApHdgLock, ApHdgLockDir, ApAltLockVar, ApAltLock, ApVsHold, ApVsVar, ApNav1Lock,
    EngRpm, EngMap, EngOilTemp, EngOilPress, EngFuelPress, EngCht, EngEgt, EngFuelFlow,
    FuelLeft, FuelRight, BusVolts,
    Com1Active, Com1Standby, Com2Active, Com2Standby, Nav1Active, Nav1Standby, Nav2Active, Nav2Standby,
    Nav1Radial, Nav1HasNav, XpdrCode, XpdrState, XpdrIdent,
    FlapsPct, ElevTrimPct, GearHandle, ParkingBrake,
    ZuluTime, ZuluDay, ZuluMonth, ZuluYear, SimulationTime,
}

/// <summary>Indexes into <see cref="MsfsDatums.Traffic"/>.</summary>
public enum T
{
    Lat, Lon, AltMsl, GroundSpeed, HdgTrue, VerticalSpeed, OnGround, VelWorldX, VelWorldZ,
    AtcId, AtcAirline, AtcFlightNumber,
}

public static class MsfsDatums
{
    // Request units per protocol §3.1. SimConnect converts (several "DEGREES" SimVars are
    // radians by default, temperatures are Rankine).
    public static readonly Datum[] User =
    [
        new("PLANE LATITUDE", "degrees"),
        new("PLANE LONGITUDE", "degrees"),
        new("PLANE ALTITUDE", "meters"),
        new("GROUND VELOCITY", "meters per second"),
        new("GPS GROUND TRUE TRACK", "degrees"),
        new("VELOCITY WORLD X", "meters per second"),
        new("VELOCITY WORLD Y", "meters per second"),
        new("VELOCITY WORLD Z", "meters per second"),
        new("VERTICAL SPEED", "meters per second"),
        new("MAGVAR", "degrees"),
        new("PLANE PITCH DEGREES", "degrees"),
        new("PLANE BANK DEGREES", "degrees"),
        new("PLANE HEADING DEGREES TRUE", "degrees"),
        new("PLANE HEADING DEGREES MAGNETIC", "degrees"),
        new("DELTA HEADING RATE", "degrees per second"),
        new("TURN COORDINATOR BALL", "position 128"),
        new("G FORCE", "GForce"),
        new("AIRSPEED INDICATED", "meters per second"),
        new("AIRSPEED TRUE", "meters per second"),
        new("PRESSURE ALTITUDE", "meters"),
        new("AMBIENT PRESSURE", "millibars"),
        new("AMBIENT TEMPERATURE", "celsius"),
        new("AMBIENT WIND DIRECTION", "degrees"),
        new("AMBIENT WIND VELOCITY", "meters per second"),
        new("KOHLSMAN SETTING MB:1", "millibars"),
        new("SIM ON GROUND", "bool"),
        new("IS SLEW ACTIVE", "bool"),
        new("AUTOPILOT MASTER", "bool"),
        new("AUTOPILOT HEADING LOCK", "bool"),
        new("AUTOPILOT HEADING LOCK DIR", "degrees"),
        new("AUTOPILOT ALTITUDE LOCK VAR", "feet"),
        new("AUTOPILOT ALTITUDE LOCK", "bool"),
        new("AUTOPILOT VERTICAL HOLD", "bool"),
        new("AUTOPILOT VERTICAL HOLD VAR", "feet per minute"),
        new("AUTOPILOT NAV1 LOCK", "bool"),
        new("GENERAL ENG RPM:1", "rpm"),
        new("ENG MANIFOLD PRESSURE:1", "inHg"),
        new("GENERAL ENG OIL TEMPERATURE:1", "celsius"),
        new("GENERAL ENG OIL PRESSURE:1", "psi"),
        new("GENERAL ENG FUEL PRESSURE:1", "psi"),
        new("RECIP ENG CYLINDER HEAD TEMPERATURE:1", "celsius"),
        new("GENERAL ENG EXHAUST GAS TEMPERATURE:1", "celsius"),
        new("ENG FUEL FLOW GPH:1", "gallons per hour"),
        // Legacy fallbacks until aircraft profiles name tanks and buses (RB-SIM-MSF-080).
        new("FUEL LEFT QUANTITY", "gallons"),
        new("FUEL RIGHT QUANTITY", "gallons"),
        new("ELECTRICAL MAIN BUS VOLTAGE", "volts"),
        // Radios. The doc unit is Frequency BCD16; asking for hertz relies on SimConnect
        // converting it (verify, L1). TRANSPONDER STATE and IDENT are missing on older
        // MSFS 2020 builds; a rejected datum only blanks its own field (RB-SIM-MSF-130).
        new("COM ACTIVE FREQUENCY:1", "hertz"),
        new("COM STANDBY FREQUENCY:1", "hertz"),
        new("COM ACTIVE FREQUENCY:2", "hertz"),
        new("COM STANDBY FREQUENCY:2", "hertz"),
        new("NAV ACTIVE FREQUENCY:1", "hertz"),
        new("NAV STANDBY FREQUENCY:1", "hertz"),
        new("NAV ACTIVE FREQUENCY:2", "hertz"),
        new("NAV STANDBY FREQUENCY:2", "hertz"),
        new("NAV RADIAL:1", "degrees"),
        new("NAV HAS NAV:1", "bool"),
        new("TRANSPONDER CODE:1", "Bco16"),
        new("TRANSPONDER STATE:1", "enum"),
        new("TRANSPONDER IDENT:1", "bool"),
        // Cockpit actuators.
        new("FLAPS HANDLE PERCENT", "percent"),
        new("ELEVATOR TRIM PCT", "percent"),
        new("GEAR HANDLE POSITION", "bool"),
        new("BRAKE PARKING POSITION", "bool"),
        // Environment variables; whether they need the E: prefix is checked on L1 (verify).
        new("ZULU TIME", "seconds"),
        new("ZULU DAY OF MONTH", "number"),
        new("ZULU MONTH OF YEAR", "number"),
        new("ZULU YEAR", "number"),
        new("SIMULATION TIME", "seconds"),
    ];

    public static readonly Datum[] Traffic =
    [
        new("PLANE LATITUDE", "degrees"),
        new("PLANE LONGITUDE", "degrees"),
        new("PLANE ALTITUDE", "meters"),
        new("GROUND VELOCITY", "meters per second"),
        new("PLANE HEADING DEGREES TRUE", "degrees"),
        new("VERTICAL SPEED", "meters per second"),
        new("SIM ON GROUND", "bool"),
        new("VELOCITY WORLD X", "meters per second"),
        new("VELOCITY WORLD Z", "meters per second"),
        new("ATC ID", null, DatumType.String32),
        new("ATC AIRLINE", null, DatumType.String64),
        new("ATC FLIGHT NUMBER", null, DatumType.String8),
    ];

    public static readonly Datum[] Title = [new("TITLE", null, DatumType.String256)];
}

/// <summary>Per-aircraft values the SimVars do not give. Defaults are placeholders (verify per aircraft).</summary>
public sealed class MsfsProfile
{
    /// <summary>Ball deflection in degrees at TURN COORDINATOR BALL = ±127 (verify, L2).</summary>
    public double BallFullScaleDeg { get; set; } = 10.0;
    /// <summary>Offsets added to the single sim CHT for cylinders 1–4 (RB-SIM-MOD-060).</summary>
    public double[] ChtSpreadC { get; set; } = [0, 0, 0, 0];
    public double[] EgtSpreadC { get; set; } = [0, 0, 0, 0];
}

public static class MsfsMap
{
    // Sign rules of protocol §3.1. Each one is (verify) until the L2 captures settle it
    // (RB-SIM-MSF-030): MSFS pitch is + nose down and bank + left wing down per the MS
    // avionics framework; DELTA HEADING RATE, TURN COORDINATOR BALL, MAGVAR and the
    // VELOCITY WORLD axes are taken as already matching §2.1.
    public const double PitchSign = -1.0;
    public const double BankSign = -1.0;
    public const double TurnRateSign = 1.0;
    public const double BallSign = 1.0;
    public const double MagvarSign = 1.0;

    /// <summary>
    /// Builds the ownship groups from one user-aircraft frame. <paramref name="raw"/> is indexed
    /// by <see cref="U"/>, in request units, NaN where the datum was rejected (RB-SIM-MSF-130).
    /// </summary>
    public static void ApplyUser(SimState s, double[] raw, double now, MsfsProfile profile)
    {
        double R(U u) => raw[(int)u];
        double? F(U u) => Units.Finite(raw[(int)u]);

        var pos = new PosGroup
        {
            TMono = now,
            LatDeg = F(U.Lat),
            LonDeg = F(U.Lon),
            AltMslM = F(U.AltMsl),
            GsMps = F(U.GroundSpeed),
            TrackTrueDeg = Track(R(U.GpsTrack), R(U.VelWorldX), R(U.VelWorldZ)),
            // The geometric vertical speed feeds GPS VS; VERTICAL SPEED is the indicated one (verify, L2).
            VsGpsMps = F(U.VelWorldY),
            MagvarDeg = Units.Finite(MagvarSign * R(U.Magvar)),
        };
        pos.Valid = pos.LatDeg.HasValue && pos.LonDeg.HasValue && pos.AltMslM.HasValue;

        var att = new AttGroup
        {
            TMono = now,
            PitchDeg = Units.Finite(PitchSign * R(U.Pitch)),
            RollDeg = Units.Finite(BankSign * R(U.Bank)),
            HdgTrueDeg = Wrap(R(U.HdgTrue)),
            HdgMagDeg = Wrap(R(U.HdgMag)),
            TurnRateDps = Units.Finite(TurnRateSign * R(U.TurnRate)),
            SlipDeg = Units.Finite(BallSign * R(U.Ball) / 127.0 * profile.BallFullScaleDeg),
            GLoad = F(U.GForce),
        };
        att.Valid = att.PitchDeg.HasValue && att.RollDeg.HasValue && att.HdgTrueDeg.HasValue;

        var air = new AirGroup
        {
            TMono = now,
            IasMps = F(U.Ias),
            TasMps = F(U.Tas),
            PressureAltM = F(U.PressureAlt),
            StaticPa = Units.Finite(R(U.AmbientPressure) * Units.HPaToPa),
            VsBaroMps = F(U.VerticalSpeed),
            OatC = F(U.AmbientTemp),
            SimQnhPa = Units.Finite(R(U.Kohlsman) * Units.HPaToPa),
        };
        air.Valid = air.IasMps.HasValue && air.PressureAltM.HasValue;

        var wind = new WindGroup { TMono = now, FromTrueDeg = Wrap(R(U.WindDir)), SpeedMps = F(U.WindVel) };
        wind.Valid = wind.FromTrueDeg.HasValue && wind.SpeedMps.HasValue;

        double cht = R(U.EngCht), egt = R(U.EngEgt);
        var eng = new EngGroup
        {
            TMono = now,
            Rpm = F(U.EngRpm),
            MapPa = Units.Finite(R(U.EngMap) * Units.InHgToPa),
            OilTempC = F(U.EngOilTemp),
            OilPressPa = Units.Finite(R(U.EngOilPress) * Units.PsiToPa),
            FuelPressPa = Units.Finite(R(U.EngFuelPress) * Units.PsiToPa),
            // No coolant SimVar exists; liquid-cooled profiles add RECIP ENG RADIATOR TEMPERATURE later.
            CoolantTempC = null,
            ChtC = [.. profile.ChtSpreadC.Take(4).Select(d => Units.Finite(cht + d))],
            EgtC = [.. profile.EgtSpreadC.Take(4).Select(d => Units.Finite(egt + d))],
            CylindersSynthetic = true,
            FuelFlowLph = Units.Finite(R(U.EngFuelFlow) * Units.UsGalToL),
        };
        eng.Valid = eng.Rpm.HasValue;

        var fuel = new FuelGroup
        {
            TMono = now,
            LeftL = Units.Finite(R(U.FuelLeft) * Units.UsGalToL),
            RightL = Units.Finite(R(U.FuelRight) * Units.UsGalToL),
        };
        fuel.Valid = fuel.LeftL.HasValue || fuel.RightL.HasValue;

        var elec = new ElecGroup { TMono = now, BusV = F(U.BusVolts) };
        elec.Valid = elec.BusV.HasValue;

        var ap = new ApGroup
        {
            TMono = now,
            Master = Bool(R(U.ApMaster)),
            HdgMode = Bool(R(U.ApHdgLock)),
            HdgBugMagDeg = Wrap(R(U.ApHdgLockDir)),
            AltSelM = Units.Finite(R(U.ApAltLockVar) * Units.FtToM),
            AltHold = Bool(R(U.ApAltLock)),
            VsHold = Bool(R(U.ApVsHold)),
            VsSelMps = Units.Finite(R(U.ApVsVar) * Units.FtToM / 60.0),
            NavMode = Bool(R(U.ApNav1Lock)),
        };
        ap.Valid = ap.Master.HasValue;

        var radio = new RadioGroup
        {
            TMono = now,
            Com1ActiveHz = F(U.Com1Active),
            Com1StandbyHz = F(U.Com1Standby),
            Com2ActiveHz = F(U.Com2Active),
            Com2StandbyHz = F(U.Com2Standby),
            Nav1ActiveHz = F(U.Nav1Active),
            Nav1StandbyHz = F(U.Nav1Standby),
            Nav2ActiveHz = F(U.Nav2Active),
            Nav2StandbyHz = F(U.Nav2Standby),
            Nav1RadialDeg = Wrap(R(U.Nav1Radial)),
            Nav1Valid = Bool(R(U.Nav1HasNav)),
            XpdrCode = Squawk.FromBco16(R(U.XpdrCode)),
            XpdrState = double.IsFinite(R(U.XpdrState)) ? (int)R(U.XpdrState) : null,
            XpdrIdent = Bool(R(U.XpdrIdent)),
        };
        radio.Valid = radio.Com1ActiveHz.HasValue;

        var controls = new ControlsGroup
        {
            TMono = now,
            FlapsPct = F(U.FlapsPct),
            ElevatorTrimPct = F(U.ElevTrimPct),
            GearDown = Bool(R(U.GearHandle)),
            ParkingBrake = Bool(R(U.ParkingBrake)),
        };
        controls.Valid = controls.FlapsPct.HasValue || controls.GearDown.HasValue;

        s.Pos = pos;
        s.Att = att;
        s.Air = air;
        s.Wind = wind;
        s.Eng = [eng];
        s.Fuel = fuel;
        s.Elec = elec;
        s.Ap = ap;
        s.Radio = radio;
        s.Controls = controls;
        s.Sim.OnGround = Bool(R(U.OnGround)) ?? false;
        s.Sim.Utc = Utc(R(U.ZuluYear), R(U.ZuluMonth), R(U.ZuluDay), R(U.ZuluTime));
    }

    /// <summary>GPS track, or atan2(east, north) of the world velocity when the GPS track is missing.</summary>
    public static double? Track(double gpsTrackDeg, double velEast, double velNorth)
    {
        if (double.IsFinite(gpsTrackDeg)) return Units.Wrap360(gpsTrackDeg);
        if (!double.IsFinite(velEast) || !double.IsFinite(velNorth)) return null;
        if (velEast == 0 && velNorth == 0) return null;
        return Units.Wrap360(Math.Atan2(velEast, velNorth) * 180.0 / Math.PI);
    }

    /// <summary>Sim UTC from the ZULU environment variables (RB-SIM-MSF-040); null if any is missing.</summary>
    public static DateTime? Utc(double year, double month, double day, double secondsOfDay)
    {
        if (!double.IsFinite(year) || !double.IsFinite(month) || !double.IsFinite(day) || !double.IsFinite(secondsOfDay))
            return null;
        try
        {
            return new DateTime((int)year, (int)month, (int)day, 0, 0, 0, DateTimeKind.Utc).AddSeconds(secondsOfDay);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static TrafficTarget? MapTraffic(double[] num, string?[] str, uint objectId, double now)
    {
        double? F(T t) => Units.Finite(num[(int)t]);
        double? lat = F(T.Lat), lon = F(T.Lon);
        if (lat is null || lon is null) return null;
        string callsign = (str[(int)T.AtcId] ?? "").Trim();
        string airline = (str[(int)T.AtcAirline] ?? "").Trim();
        string flight = (str[(int)T.AtcFlightNumber] ?? "").Trim();
        if (airline.Length > 0 && flight.Length > 0) callsign = airline + flight;
        return new TrafficTarget
        {
            Id24 = PseudoIcao(objectId),
            Callsign = callsign,
            LatDeg = lat.Value,
            LonDeg = lon.Value,
            AltMslM = F(T.AltMsl),
            TrackTrueDeg = Track(double.NaN, num[(int)T.VelWorldX], num[(int)T.VelWorldZ]) ?? Wrap(num[(int)T.HdgTrue]),
            GsMps = F(T.GroundSpeed),
            VsMps = F(T.VerticalSpeed),
            OnGround = Bool(num[(int)T.OnGround]) ?? false,
            Category = "",
            TMono = now,
        };
    }

    // Provisional reserved range for sim objects without a real ICAO address; T3.1 picks
    // the final range together with roastbeef (protocol §4.1).
    public const int PseudoIcaoBase = 0xF00000;
    public const int PseudoIcaoMask = 0x0FFFFF;

    /// <summary>A stable pseudo ICAO address from the sim object ID (FNV-1a).</summary>
    public static int PseudoIcao(uint objectId)
    {
        uint h = 2166136261;
        for (int i = 0; i < 4; i++)
        {
            h ^= (objectId >> (8 * i)) & 0xFF;
            h *= 16777619;
        }
        return PseudoIcaoBase | (int)(h & PseudoIcaoMask);
    }

    static double? Wrap(double deg) => double.IsFinite(deg) ? Units.Wrap360(deg) : null;
    static bool? Bool(double v) => double.IsFinite(v) ? v > 0.5 : null;
}
