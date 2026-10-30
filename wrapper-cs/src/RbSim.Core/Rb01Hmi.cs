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
// Rb01Hmi.cs: the frames the RB-01 daemon pushes to its HMI sockets, built from an RB-SIM-IF/1 state message.

using System.Globalization;
using System.Text.Json.Nodes;

namespace RbSim.Core;

/// <summary>
/// The frames the RB-01 daemon sends on its HMI WebSockets (roastbeef
/// main/managementinterface.go): <c>/situation</c> (SituationData), <c>/traffic</c> and
/// <c>/radar</c> (TrafficInfo), <c>/ems</c> (the EMS map) and <c>/status</c> (globalStatus).
/// They are built from the RB-SIM-IF/1 state message, as an RB-01 hub would after
/// <c>POST /sim/state</c>, so units and staleness come from <see cref="RbSimIf"/>.
///
/// The HMI believes every frame: the altitude and speed tapes read their fields without a
/// validity check. So each frame is built from scratch, never from the previous one. A stale or
/// invalid group is sent as the daemon's no-data state (zeros, as in the HMI's initial
/// <c>window.situation</c>) plus the daemon's own invalid markers, and a last value is never
/// resent (RB-SIM-SAF-050).
/// </summary>
public sealed class Rb01Hmi
{
    /// <summary>roastbeef <c>common.AHRSInvalid</c>, the HMI's <c>AHRS_INVALID</c>.</summary>
    public const double AhrsInvalid = 3276.7;
    /// <summary>The accuracy the daemon reports without a fix (<c>isGPSValid</c>).</summary>
    public const double NoFixAccuracyM = 999999;

    // A simulated receiver has no real quality figures: these are synthetic while the position is
    // fresh. The satellite count is the one $GPGGA carries.
    const int SimSatellites = 8;
    const double SimHorizontalAccuracyM = 3.0, SimVerticalAccuracyM = 4.0;
    const int SimNacp = 10;
    // BARO_TYPE_BMP280, the value every HMI path treats as a working baro. The RB-01 SIM hub may
    // introduce its own BARO_TYPE_SIM (handoff §4.5, T3.1); the HMI only tests for 0 and 4 (verify).
    const int BaroTypeSim = 1;
    // What RB-01's FLARM path gives external traffic: TRAFFIC_SOURCE_OGN, TARGET_TYPE_ADSB. Light
    // aircraft emitter category, and a self-assigned address type for the pseudo-ICAO addresses.
    const int TrafficSourceOgn = 4, TargetTypeAdsb = 1, EmitterLight = 1, AddrTypeSelfAssigned = 1;

    /// <summary>The keys of every <c>/ems</c> frame, those of the contract's ems group.</summary>
    public static IReadOnlyList<string> EmsKeys => RbSimIf.StateGroupKeys["ems"];

    /// <summary>The daemon's time.Time zero value, which it gives a sample it no longer has.</summary>
    public static readonly string ZeroTime = Time(DateTime.MinValue);

    readonly object _lock = new();
    double? _gMin, _gMax;

    /// <summary>
    /// The daemon's stratuxClock as JSON: monotonic seconds counted from year 1. The HMI compares
    /// the <c>*Time</c> fields with each other and with <c>UptimeClock</c> to judge freshness.
    /// </summary>
    public static string Clock(double monoS) => Time(DateTime.MinValue.AddSeconds(Math.Max(0, monoS)));

    static string Time(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The <c>/situation</c> frame, SituationData. Thread-safe: every socket shares one instance,
    /// which tracks the G-meter minimum and maximum as the daemon does.
    /// </summary>
    public JsonObject Situation(JsonObject state, double monoS)
    {
        var gps = state["gps"] as JsonObject;
        var ahrs = state["ahrs"] as JsonObject;
        var baro = state["baro"] as JsonObject;
        var air = state["airspeed"] as JsonObject;
        string clock = Clock(monoS);
        string Stamp(JsonObject? group) => group is null ? ZeroTime : clock;
        double Ahrs(string key) => Num(ahrs, key, AhrsInvalid);

        bool fix = gps is not null;
        string gpsTime = (string?)gps?["GPSTime"] ?? ZeroTime;
        double sinceMidnight = fix && DateTime.TryParse(gpsTime, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
            ? Math.Round(utc.TimeOfDay.TotalSeconds, 2) : 0;
        double msl = Num(gps, "GPSAltitudeMSL"), geoidSep = Num(gps, "GPSGeoidSep");

        double gLoad = Ahrs("AHRSGLoad"), gMin, gMax;
        lock (_lock)
        {
            if (gLoad == AhrsInvalid)
            {
                _gMin = _gMax = null;
                gMin = AhrsInvalid; // as sensors.go does when the attitude is lost
                gMax = 0;
            }
            else
            {
                gMin = (_gMin = Math.Min(_gMin ?? gLoad, gLoad)).Value;
                gMax = (_gMax = Math.Max(_gMax ?? gLoad, gLoad)).Value;
            }
        }

        return new JsonObject
        {
            ["GPSLastFixSinceMidnightUTC"] = sinceMidnight,
            ["GPSLatitude"] = Num(gps, "GPSLatitude"),
            ["GPSLongitude"] = Num(gps, "GPSLongitude"),
            ["GPSFixQuality"] = fix ? 1 : 0,
            ["GPSHeightAboveEllipsoid"] = fix ? Math.Round(msl + geoidSep, 1) : 0,
            ["GPSGeoidSep"] = geoidSep,
            ["GPSSatellites"] = fix ? SimSatellites : 0,
            ["GPSSatellitesTracked"] = fix ? SimSatellites : 0,
            ["GPSSatellitesSeen"] = fix ? SimSatellites : 0,
            ["GPSHorizontalAccuracy"] = fix ? SimHorizontalAccuracyM : NoFixAccuracyM,
            ["GPSNACp"] = fix ? SimNacp : 0,
            ["GPSAltitudeMSL"] = msl,
            ["GPSVerticalAccuracy"] = fix ? SimVerticalAccuracyM : NoFixAccuracyM,
            ["GPSVerticalSpeed"] = Num(gps, "GPSVerticalSpeed"),
            ["GPSLastFixLocalTime"] = Stamp(gps),
            ["GPSTrueCourse"] = Num(gps, "GPSTrueCourse"),
            // The daemon derives it from the ground track; the HMI only falls back on it when
            // AHRSTurnRate is invalid, which in a simulator means the position is stale too.
            ["GPSTurnRate"] = AhrsInvalid,
            ["GPSGroundSpeed"] = Num(gps, "GPSGroundSpeed"),
            ["GPSLastGroundTrackTime"] = Stamp(gps),
            ["GPSTime"] = fix ? gpsTime : ZeroTime,
            // The frame's own clock, not the GPS age: the radar and autopilot plates use it as
            // "now" against BaroLastMeasurementTime and AHRSLastAttitudeTime.
            ["GPSLastGPSTimeStratuxTime"] = clock,
            ["GPSLastValidNMEAMessageTime"] = Stamp(gps),
            ["GPSLastValidNMEAMessage"] = "",
            ["GPSPositionSampleRate"] = 0,
            ["BaroTemperature"] = 0,
            ["BaroPressureAltitude"] = Num(baro, "BaroPressureAltitude"),
            ["BaroVerticalSpeed"] = Num(baro, "BaroVerticalSpeed"),
            ["BaroLastMeasurementTime"] = Stamp(baro),
            ["BaroSourceType"] = baro is null ? 0 : BaroTypeSim,
            ["AHRSPitch"] = Ahrs("AHRSPitch"),
            ["AHRSRoll"] = Ahrs("AHRSRoll"),
            ["AHRSGyroHeading"] = Ahrs("AHRSGyroHeading"),
            ["AHRSMagHeading"] = Ahrs("AHRSMagHeading"),
            ["AHRSSlipSkid"] = Ahrs("AHRSSlipSkid"),
            ["AHRSTurnRate"] = Ahrs("AHRSTurnRate"),
            ["AHRSGLoad"] = gLoad,
            ["AHRSGLoadMin"] = gMin,
            ["AHRSGLoadMax"] = gMax,
            ["AHRSLastAttitudeTime"] = Stamp(ahrs),
            // updateAHRSStatus bits: 0 ground track valid, 1 IMU in use, 2 baro in use. Never 3 (calibrating).
            ["AHRSStatus"] = (fix ? 1 : 0) | (ahrs is null ? 0 : 2) | (baro is null ? 0 : 4),
            // 0 is the daemon's "no airspeed source": the HMI then shows the ground speed as GS.
            ["IndicatedAirSpeed"] = Num(air, "IndicatedAirSpeed"),
        };
    }

    /// <summary>
    /// The <c>/traffic</c> and <c>/radar</c> frames: one TrafficInfo per target fresher than
    /// 10 s, nearest first. <c>Age</c> is the time since the sim last reported the target; the
    /// HMI fades a target with it and drops it after 59 s, as for any RB-01 traffic.
    /// </summary>
    public static IEnumerable<JsonObject> Traffic(SimState s, double now, DateTime utcNow)
    {
        bool own = Staleness.Fresh(s.Pos, Staleness.PosS, now, s.Sim) && s.Pos.LatDeg is double && s.Pos.LonDeg is double;
        foreach (var t in RbSimIf.FreshTargets(s, now))
        {
            var o = RbSimIf.TrafficInfo(t);
            double age = Math.Round(Math.Max(0, now - t.TMono), 1);
            string seen = Time(utcNow.AddSeconds(-age));
            o["Alt"] ??= 0;
            o["Track"] ??= 0;
            o["Speed_valid"] = o["Speed"] is not null;
            o["Speed"] ??= 0;
            o["Vvel"] ??= 0;
            o["Reg"] = "";
            o["Emitter_category"] = EmitterLight;
            o["SurfaceVehicleType"] = 0;
            o["Addr_type"] = AddrTypeSelfAssigned;
            o["TargetType"] = TargetTypeAdsb;
            o["SignalLevel"] = 0;
            o["Squawk"] = 0;
            o["Position_valid"] = true;
            o["AltIsGNSS"] = true; // Alt is MSL from the sim, not a pressure altitude (verify, T3.1)
            o["NIC"] = 0;
            o["NACp"] = 0;
            o["TurnRate"] = 0;
            o["Timestamp"] = seen;
            o["PriorityStatus"] = 0;
            o["Age"] = age;
            o["AgeLastAlt"] = age;
            o["Last_seen"] = seen;
            o["Last_alt"] = seen;
            o["Last_speed"] = seen;
            o["Last_source"] = TrafficSourceOgn;
            o["ExtrapolatedPosition"] = false;
            o["BearingDist_valid"] = own;
            o["Bearing"] = own ? Math.Round(BearingDeg(s.Pos.LatDeg!.Value, s.Pos.LonDeg!.Value, t.LatDeg, t.LonDeg), 1) : 0;
            o["Distance"] = own ? Math.Round(TeleportDetector.DistanceM(s.Pos.LatDeg!.Value, s.Pos.LonDeg!.Value, t.LatDeg, t.LonDeg), 0) : 0;
            yield return o;
        }
    }

    /// <summary>The first <c>/radar</c> frame, the daemon's default radar settings (gen_gdl90.go).</summary>
    public static JsonObject RadarSettings() => new() { ["RadarLimits"] = 2000, ["RadarRange"] = 10 };

    /// <summary>
    /// The <c>/ems</c> frame: every key of the ems group, in the aircraft's ems.json units. The
    /// HMI keeps an EMS value until a frame replaces it and has no invalid state, so a stale
    /// group or a value the sim lacks is sent as 0, the daemon's starting value for every key.
    /// </summary>
    public static JsonObject Ems(JsonObject state)
    {
        var ems = state["ems"] as JsonObject;
        var o = new JsonObject();
        foreach (var k in EmsKeys) o[k] = Num(ems, k);
        return o;
    }

    /// <summary>
    /// The <c>/status</c> frame: the globalStatus fields the HMI reads. Absent fields, such as
    /// CPUTemp, are left out so the HMI shows them as not available. The version always ends in
    /// <c>-SIM</c>, as for every RB simulator build.
    /// </summary>
    public static JsonObject Status(JsonObject state, string version, double monoS)
    {
        bool fix = state["gps"] is JsonObject;
        return new JsonObject
        {
            ["Version"] = version + "-SIM",
            ["Build"] = (string?)state["src"] ?? "",
            ["GPS_connected"] = fix,
            ["GPS_satellites_locked"] = fix ? SimSatellites : 0,
            ["GPS_satellites_seen"] = fix ? SimSatellites : 0,
            ["GPS_satellites_tracked"] = fix ? SimSatellites : 0,
            ["GPS_position_accuracy"] = fix ? SimHorizontalAccuracyM : NoFixAccuracyM,
            ["GPS_solution"] = fix ? "Simulator" : "No Fix",
            ["IMUConnected"] = state["ahrs"] is JsonObject,
            ["BMPConnected"] = state["baro"] is JsonObject,
            ["Uptime"] = (long)Math.Max(0, monoS * 1000),
            ["UptimeClock"] = Clock(monoS),
            ["Errors"] = new JsonArray(),
            ["NightMode"] = false,
        };
    }

    // roastbeef autopilot.go WAYPOINT_STATUS_*.
    const int WaypointTarget = 1, WaypointPast = 2;

    /// <summary>
    /// The <c>/autopilot</c> frame, AutopilotStatus, from the sim GPS's active leg (D25): what
    /// the daemon sends each second while its autopilot is active. <c>Active</c> is true while
    /// there is guidance, so the HMI shows the steer arrow and the PFD course bug; without it,
    /// the daemon's idle state. GPRMBData's Vnav is left out: no plate reads it.
    /// </summary>
    public static JsonObject Autopilot(SimState s, double now)
    {
        var g = Guidance.Compute(s, now);
        if (g is null)
        {
            return new JsonObject
            {
                ["Active"] = false,
                ["To"] = Waypoint(0, 0, 0, 0, ""),
                ["GPRMB"] = new JsonObject
                {
                    ["Status"] = "V",
                    ["XTRKDistance"] = 0,
                    ["Steer"] = "L",
                    ["WaypointStart"] = "",
                    ["WaypointDest"] = "",
                    ["Lat"] = 0,
                    ["Lon"] = 0,
                    ["Distance"] = 0,
                    ["TrueBearing"] = 0,
                    ["Knots"] = 0,
                    ["Arrived"] = "V",
                    ["LegCourse"] = 0,
                    ["NextLegTrue"] = 0,
                    ["HasNextLeg"] = false,
                    ["IsLastWaypoint"] = false,
                },
            };
        }
        return new JsonObject
        {
            ["Active"] = true,
            ["To"] = Leg(s, g)[^1]!.DeepClone(),
            ["GPRMB"] = new JsonObject
            {
                ["Status"] = "A",
                ["XTRKDistance"] = Math.Round(g.XtkNm, 2),
                ["Steer"] = g.Steer,
                ["WaypointStart"] = Nmea.WaypointId(g.FromId),
                ["WaypointDest"] = Nmea.WaypointId(g.ToId),
                ["Lat"] = g.ToLatDeg,
                ["Lon"] = g.ToLonDeg,
                ["Distance"] = Math.Round(Units.MToNm(g.DistanceM), 2),
                ["TrueBearing"] = Math.Round(g.BearingTrueDeg, 1),
                ["Knots"] = g.ClosingMps is double c ? Math.Round(Units.MpsToKt(c), 1) : 0,
                ["Arrived"] = g.Arrived ? "A" : "V",
                ["LegCourse"] = Math.Round(g.LegCourseTrueDeg, 1),
                // The sim GPS variables give one leg, not the next one.
                ["NextLegTrue"] = 0,
                ["HasNextLeg"] = false,
                ["IsLastWaypoint"] = false,
            },
        };
    }

    /// <summary>
    /// <c>GET /autopilot</c>, the daemon's waypoint list: the leg as RB-01 stores it from a
    /// $GPRMB on its UDP input, origin (past) then destination (target). The HSI plate steers to
    /// the last one, synthview and the map draw the line. Without an origin the leg starts at
    /// the aircraft, as on RB-01. Ele is feet: the plan's altitude, or the aircraft's when the
    /// plan gives none. Empty without guidance.
    /// </summary>
    public static JsonArray Waypoints(SimState s, double now) =>
        Guidance.Compute(s, now) is { } g ? Leg(s, g) : [];

    static JsonArray Leg(SimState s, Guidance g)
    {
        int ownFt = s.Pos.AltMslM is double a ? (int)Math.Round(Units.MToFt(a)) : 0;
        int toFt = g.ToAltMslM is double ta ? (int)Math.Round(Units.MToFt(ta)) : ownFt;
        return
        [
            Waypoint(g.FromLatDeg ?? s.Pos.LatDeg!.Value, g.FromLonDeg ?? s.Pos.LonDeg!.Value, ownFt, WaypointPast, Nmea.WaypointId(g.FromId)),
            Waypoint(g.ToLatDeg, g.ToLonDeg, toFt, WaypointTarget, Nmea.WaypointId(g.ToId)),
        ];
    }

    static JsonObject Waypoint(double lat, double lon, int eleFt, int status, string cmt) =>
        new() { ["Lat"] = lat, ["Lon"] = lon, ["Ele"] = eleFt, ["Status"] = status, ["Cmt"] = cmt };

    /// <summary>Initial great-circle bearing, degrees true 0–360.</summary>
    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180, dl = (lon2 - lon1) * Math.PI / 180;
        double y = Math.Sin(dl) * Math.Cos(p2);
        double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    static double Num(JsonObject? group, string key, double missing = 0) =>
        group?[key] is JsonValue v && v.TryGetValue(out double d) ? d : missing;
}
