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
// Guidance.cs: lateral guidance along the sim GPS's active leg, as RB-01's autopilot computes it (D25).

namespace RbSim.Core;

/// <summary>
/// Guidance along the sim GPS's active leg: the quantities roastbeef autopilot.go puts in
/// GPRMBData, computed the same way. Distance and bearing are great-circle from the ownship to
/// the waypoint, the leg course runs origin → waypoint, and the cross-track is the distance
/// from that great circle, + right of course. Only positions are used, so no sim cross-track or
/// desired-track SimVar, with their unverified signs and magnetic references, is involved.
/// </summary>
public sealed record Guidance(
    string FromId, string ToId,
    double? FromLatDeg, double? FromLonDeg,
    double ToLatDeg, double ToLonDeg, double? ToAltMslM,
    double DistanceM, double BearingTrueDeg, double LegCourseTrueDeg, double XtkM,
    double? ClosingMps, bool Arrived, bool PerpendicularPassed, double? MagvarDeg)
{
    /// <summary>roastbeef AUTOPILOT_ARRIVAL_RADIUS_METERS: the arrival alarm radius.</summary>
    public const double ArrivalRadiusM = 500.0;
    /// <summary>roastbeef AUTOPILOT_XTRK_MAX_NM: beyond it the NMEA field, and the needle, is pegged.</summary>
    public const double XtkMaxNm = 9.99;
    /// <summary>The mean radius roastbeef common.Distance uses.</summary>
    public const double EarthRadiusM = 6371008.8;

    /// <summary>Right of course means steer left to regain it, as in roastbeef.</summary>
    public string Steer => XtkM < 0 ? "R" : "L";

    /// <summary>|cross-track| in NM, clamped to <see cref="XtkMaxNm"/>.</summary>
    public double XtkNm => Math.Min(Units.MToNm(Math.Abs(XtkM)), XtkMaxNm);

    /// <summary>
    /// The guidance for this state, or null when there is none to give: no active waypoint, or
    /// the position or the leg is not fresh. Null means nothing is sent, never a last value.
    /// A leg without an origin (or with one on the waypoint) is flown as a direct-to from where
    /// the aircraft is: the course is the bearing and the cross-track 0.
    /// </summary>
    public static Guidance? Compute(SimState s, double now)
    {
        var p = s.Pos;
        var n = s.Nav;
        if (!Staleness.Fresh(p, Staleness.PosS, now, s.Sim) || !Staleness.Fresh(n, Staleness.PosS, now, s.Sim)) return null;
        if (p.LatDeg is not double lat || p.LonDeg is not double lon
            || n.ToLatDeg is not double toLat || n.ToLonDeg is not double toLon) return null;

        double dist = GreatCircleM(lat, lon, toLat, toLon);
        double bearing = BearingDeg(lat, lon, toLat, toLon);
        double course = bearing, xtk = 0;
        double? fromLat = n.FromLatDeg, fromLon = n.FromLonDeg;
        if (fromLat is double fl && fromLon is double fo && GreatCircleM(fl, fo, toLat, toLon) > 1.0)
        {
            course = BearingDeg(fl, fo, toLat, toLon);
            xtk = CrossTrackM(lat, lon, fl, fo, toLat, toLon);
        }
        else
        {
            fromLat = fromLon = null;
        }
        // roastbeef's RMB field 12: the closing speed, the ground speed along the bearing.
        double? closing = p.GsMps is double gs && p.TrackTrueDeg is double trk
            ? gs * Math.Cos((bearing - trk) * Math.PI / 180.0) : null;
        return new Guidance(n.FromId, n.ToId, fromLat, fromLon, toLat, toLon, n.ToAltMslM,
            dist, bearing, course, xtk, closing,
            Arrived: dist < ArrivalRadiusM,
            PerpendicularPassed: Math.Abs(AngleDiff(bearing, course)) > 90.0,
            MagvarDeg: p.MagvarDeg);
    }

    /// <summary>Great-circle distance (haversine), metres.</summary>
    public static double GreatCircleM(double lat1, double lon1, double lat2, double lon2)
    {
        double rad = Math.PI / 180.0;
        double dp = (lat2 - lat1) * rad, dl = (lon2 - lon1) * rad;
        double a = Math.Sin(dp / 2) * Math.Sin(dp / 2)
            + Math.Cos(lat1 * rad) * Math.Cos(lat2 * rad) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * EarthRadiusM * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    /// <summary>Initial great-circle bearing, degrees true 0–360.</summary>
    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2) =>
        Rb01Hmi.BearingDeg(lat1, lon1, lat2, lon2);

    /// <summary>
    /// Distance from the great circle origin → destination, + right of course: roastbeef
    /// crossTrackNM, in metres.
    /// </summary>
    public static double CrossTrackM(double lat, double lon, double originLat, double originLon, double destLat, double destLon)
    {
        double d13 = GreatCircleM(originLat, originLon, lat, lon) / EarthRadiusM;
        double off = (BearingDeg(originLat, originLon, lat, lon) - BearingDeg(originLat, originLon, destLat, destLon)) * Math.PI / 180.0;
        return Math.Asin(Math.Sin(d13) * Math.Sin(off)) * EarthRadiusM;
    }

    /// <summary>a − b folded into −180 … 180.</summary>
    public static double AngleDiff(double a, double b) => Units.Wrap360(a - b + 180.0) - 180.0;
}
