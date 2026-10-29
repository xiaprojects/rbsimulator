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
// RbSimIf.cs: SimState → RB-SIM-IF/1 state and traffic messages, in RB-01's field names and units (protocol §4.1).

using System.Globalization;
using System.Text.Json.Nodes;

namespace RbSim.Core;

public enum PressureUnit { InHg, Psi, Bar }
public enum VolumeUnit { Liters, UsGallons }

/// <summary>
/// EMS values are numbers in the units of the aircraft's ems.json (protocol §4.1). The
/// defaults follow the §4.1 example (MAP 24 inHg, oil 4 bar, fuel in litres); check them
/// against the target aircraft's ems.json (verify).
/// </summary>
public sealed class EmsUnits
{
    public PressureUnit ManifoldPressure { get; set; } = PressureUnit.InHg;
    public PressureUnit OilPressure { get; set; } = PressureUnit.Bar;
    /// <summary>Only in $RBEMS; same default as oil pressure (verify against ems.json).</summary>
    public PressureUnit FuelPressure { get; set; } = PressureUnit.Bar;
    public VolumeUnit Fuel { get; set; } = VolumeUnit.Liters;
}

public static class RbSimIf
{
    public const int Version = 1;
    public const int MaxTrafficTargets = 50;

    /// <summary>Keys each state group may carry; the hub rejects unknown names (RB-SIM-IF-040).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> StateGroupKeys = new Dictionary<string, string[]>
    {
        ["gps"] = ["GPSTime", "GPSLatitude", "GPSLongitude", "GPSAltitudeMSL", "GPSGeoidSep", "GPSTrueCourse", "GPSGroundSpeed", "GPSVerticalSpeed"],
        ["ahrs"] = ["AHRSPitch", "AHRSRoll", "AHRSGyroHeading", "AHRSMagHeading", "AHRSTurnRate", "AHRSSlipSkid", "AHRSGLoad"],
        ["baro"] = ["BaroPressureAltitude", "BaroVerticalSpeed"],
        ["airspeed"] = ["IndicatedAirSpeed"],
        ["ems"] = ["enginerpm", "manifoldpressure", "oiltemperature", "oilpressure", "outsidetemperature", "fuel1", "fuel2",
                   "cht1", "cht2", "cht3", "cht4", "egt1", "egt2", "egt3", "egt4"],
        ["ap"] = ["master", "hdg_mode", "hdg_bug_mag", "alt_sel_ft"],
        ["sim"] = ["name", "version", "aircraft", "wrapper"],
    };

    /// <summary>
    /// The body of <c>POST /sim/state</c>. gps, ahrs, baro and airspeed are always present:
    /// an object when fresh, null when invalid or stale (RB-SIM-IF-030). A field the sim does
    /// not provide is left out of its group rather than sent as zero.
    /// </summary>
    public static JsonObject State(SimState s, long seq, double now, EmsUnits ems, string? wrapper, bool includeSim)
    {
        var sim = s.Sim;
        var msg = new JsonObject
        {
            ["v"] = Version,
            ["src"] = sim.Source,
            ["seq"] = seq,
            ["paused"] = sim.Connected && sim.Paused,
        };

        msg["gps"] = Staleness.Fresh(s.Pos, Staleness.PosS, now, sim) ? Gps(s) : null;
        msg["ahrs"] = Staleness.Fresh(s.Att, Staleness.AttS, now, sim) ? Ahrs(s.Att) : null;
        msg["baro"] = Staleness.Fresh(s.Air, Staleness.AirS, now, sim) ? Baro(s.Air) : null;
        msg["airspeed"] = Staleness.Fresh(s.Air, Staleness.AirS, now, sim) && s.Air.IasMps is double ias
            ? new JsonObject { ["IndicatedAirSpeed"] = R(Units.MpsToKt(ias), 1) }
            : null;

        var eng = s.Eng.Length > 0 ? s.Eng[0] : null;
        if (eng is not null && Staleness.Fresh(eng, Staleness.EngS, now, sim))
            msg["ems"] = Ems(eng, s, now, ems);

        if (Staleness.Fresh(s.Ap, Staleness.PosS, now, sim))
        {
            var ap = new JsonObject();
            Put(ap, "master", s.Ap.Master);
            Put(ap, "hdg_mode", s.Ap.HdgMode);
            Put(ap, "hdg_bug_mag", s.Ap.HdgBugMagDeg, 0);
            Put(ap, "alt_sel_ft", s.Ap.AltSelM is double a ? Units.MToFt(a) : null, 0);
            msg["ap"] = ap;
        }

        if (includeSim)
        {
            var o = new JsonObject { ["name"] = sim.SimName, ["version"] = sim.SimVersion, ["aircraft"] = sim.Aircraft };
            if (wrapper is not null) o["wrapper"] = wrapper;
            msg["sim"] = o;
        }
        return msg;
    }

    static JsonObject Gps(SimState s)
    {
        var p = s.Pos;
        var o = new JsonObject();
        if (s.Sim.Utc is DateTime utc)
            o["GPSTime"] = utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ff'Z'", CultureInfo.InvariantCulture);
        Put(o, "GPSLatitude", p.LatDeg, 7);
        Put(o, "GPSLongitude", p.LonDeg, 7);
        Put(o, "GPSAltitudeMSL", p.AltMslM is double alt ? Units.MToFt(alt) : null, 1);
        Put(o, "GPSGeoidSep", p.GeoidSepM is double g ? Units.MToFt(g) : null, 1);
        Put(o, "GPSTrueCourse", p.TrackTrueDeg, 1);
        Put(o, "GPSGroundSpeed", p.GsMps is double gs ? Units.MpsToKt(gs) : null, 1);
        Put(o, "GPSVerticalSpeed", p.VsGpsMps is double vs ? Units.MpsToFps(vs) : null, 2);
        return o;
    }

    static JsonObject Ahrs(AttGroup a)
    {
        var o = new JsonObject();
        Put(o, "AHRSPitch", a.PitchDeg, 2);
        Put(o, "AHRSRoll", a.RollDeg, 2);
        Put(o, "AHRSGyroHeading", a.HdgTrueDeg, 1);
        Put(o, "AHRSMagHeading", a.HdgMagDeg, 1);
        // Unit and sign as RB-01 defines them are confirmed in T3.1 (verify): deg/s + right, ball deg + right.
        Put(o, "AHRSTurnRate", a.TurnRateDps, 2);
        Put(o, "AHRSSlipSkid", a.SlipDeg, 2);
        Put(o, "AHRSGLoad", a.GLoad, 2);
        return o;
    }

    static JsonObject Baro(AirGroup a)
    {
        var o = new JsonObject();
        Put(o, "BaroPressureAltitude", a.PressureAltM is double pa ? Units.MToFt(pa) : null, 0);
        Put(o, "BaroVerticalSpeed", a.VsBaroMps is double vs ? Units.MpsToFpm(vs) : null, 0);
        return o;
    }

    static JsonObject Ems(EngGroup e, SimState s, double now, EmsUnits u)
    {
        var o = new JsonObject();
        Put(o, "enginerpm", e.Rpm, 0);
        Put(o, "manifoldpressure", e.MapPa is double map ? Pressure(map, u.ManifoldPressure) : null, 1);
        Put(o, "oiltemperature", e.OilTempC, 0);
        Put(o, "oilpressure", e.OilPressPa is double op ? Pressure(op, u.OilPressure) : null, 1);
        if (Staleness.Fresh(s.Air, Staleness.AirS, now, s.Sim))
            Put(o, "outsidetemperature", s.Air.OatC, 0);
        if (Staleness.Fresh(s.Fuel, Staleness.EngS, now, s.Sim))
        {
            Put(o, "fuel1", s.Fuel.LeftL is double l ? Volume(l, u.Fuel) : null, 1);
            Put(o, "fuel2", s.Fuel.RightL is double r ? Volume(r, u.Fuel) : null, 1);
        }
        for (int i = 0; i < 4; i++)
        {
            Put(o, $"cht{i + 1}", i < e.ChtC.Length ? e.ChtC[i] : null, 0);
            Put(o, $"egt{i + 1}", i < e.EgtC.Length ? e.EgtC[i] : null, 0);
        }
        return o;
    }

    /// <summary>
    /// The body of <c>POST /sim/traffic</c>: targets fresher than 10 s, nearest first, at
    /// most 50, in RB-01's TrafficInfo names and units.
    /// </summary>
    public static JsonObject Traffic(SimState s, double now)
    {
        var arr = new JsonArray();
        foreach (var t in FreshTargets(s, now)) arr.Add(TrafficInfo(t));
        return new JsonObject { ["v"] = Version, ["src"] = s.Sim.Source, ["targets"] = arr };
    }

    /// <summary>Targets fresher than 10 s, nearest first, at most 50; none while disconnected.</summary>
    public static IEnumerable<TrafficTarget> FreshTargets(SimState s, double now)
    {
        IEnumerable<TrafficTarget> targets = s.Sim.Connected
            ? s.Traffic.Where(t => now - t.TMono <= Staleness.TrafficTargetS)
            : [];
        if (s.Pos.LatDeg is double lat && s.Pos.LonDeg is double lon)
            targets = targets.OrderBy(t => TeleportDetector.DistanceM(lat, lon, t.LatDeg, t.LonDeg));
        return targets.Take(MaxTrafficTargets);
    }

    /// <summary>One target in RB-01's TrafficInfo names and units; a field the sim lacks is left out.</summary>
    public static JsonObject TrafficInfo(TrafficTarget t)
    {
        var o = new JsonObject
        {
            ["Icao_addr"] = t.Id24,
            ["Tail"] = t.Callsign,
            ["Lat"] = R(t.LatDeg, 6),
            ["Lng"] = R(t.LonDeg, 6),
        };
        // Whether Alt is pressure or GNSS altitude is settled in T3.1 (verify); this is MSL.
        Put(o, "Alt", t.AltMslM is double a ? Units.MToFt(a) : null, 0);
        Put(o, "Track", t.TrackTrueDeg, 0);
        Put(o, "Speed", t.GsMps is double gs ? Units.MpsToKt(gs) : null, 0);
        Put(o, "Vvel", t.VsMps is double vs ? Units.MpsToFpm(vs) : null, 0);
        o["OnGround"] = t.OnGround;
        return o;
    }

    public static double Pressure(double pa, PressureUnit u) => u switch
    {
        PressureUnit.InHg => Units.PaToInHg(pa),
        PressureUnit.Psi => Units.PaToPsi(pa),
        _ => Units.PaToBar(pa),
    };

    public static double Volume(double l, VolumeUnit u) => u == VolumeUnit.UsGallons ? l / Units.UsGalToL : l;

    static double R(double v, int digits) => Math.Round(v, digits, MidpointRounding.AwayFromZero);

    static void Put(JsonObject o, string key, double? v, int digits)
    {
        if (v is double d && double.IsFinite(d)) o[key] = R(d, digits);
    }

    static void Put(JsonObject o, string key, bool? v)
    {
        if (v is bool b) o[key] = b;
    }
}
