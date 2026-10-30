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
// Nmea.cs: device lines for the RB ESP32 instruments: GPS NMEA, FLARM and the RB sentences (protocol §4.4).

using System.Globalization;
using System.Text;

namespace RbSim.Core;

/// <summary>
/// Builders for the §4.4 device lines. Each returns a complete line with checksum and CRLF,
/// or null when the data is not fresh and the sentence must not be sent.
/// Line rules (RB-SIM-ESP-010): ASCII 0x20–0x7E, at most 250 characters before CRLF,
/// numeric fields without exponent, an empty field means unknown.
/// </summary>
public static class Nmea
{
    public const int MaxLineChars = 250;
    public const int Int16Max = 32767;
    /// <summary>The RB-05 store holds 10 targets (RB-SIM-ESP-080).</summary>
    public const int MaxFlarmTargets = 10;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Wraps a body ("GPRMC,...") as $body*hh\r\n with the NMEA XOR checksum.</summary>
    public static string Line(string body)
    {
        byte sum = 0;
        foreach (char c in body)
        {
            if (c < 0x20 || c > 0x7E || c == '$' || c == '*')
                throw new ArgumentException($"invalid NMEA character 0x{(int)c:X2} in {body}");
            sum ^= (byte)c;
        }
        string line = $"${body}*{sum:X2}";
        if (line.Length > MaxLineChars) throw new ArgumentException($"NMEA line longer than {MaxLineChars}: {line}");
        return line + "\r\n";
    }

    /// <summary>Fixed decimals, invariant, never "-0.0". Null or NaN gives an empty field.</summary>
    public static string Num(double? v, int decimals)
    {
        if (v is not double d || !double.IsFinite(d)) return "";
        double r = Math.Round(d, decimals, MidpointRounding.AwayFromZero);
        if (r == 0) r = 0; // drops the sign of -0
        return r.ToString("F" + decimals, Inv);
    }

    /// <summary>
    /// A field the RB firmware parses into an int16 once the point is removed:
    /// clamped to <paramref name="min"/>…<paramref name="max"/> (the instrument pegs) and
    /// checked against |value| × 10^decimals ≤ 32767.
    /// </summary>
    public static string Int16Field(double? v, int decimals, double min, double max)
    {
        if (v is not double d || !double.IsFinite(d)) return "";
        d = Math.Clamp(d, min, max);
        double scaled = Math.Abs(Math.Round(d, decimals, MidpointRounding.AwayFromZero)) * Math.Pow(10, decimals);
        if (scaled > Int16Max) throw new ArgumentOutOfRangeException(nameof(v), $"{d} does not fit int16 at {decimals} decimals");
        return Num(d, decimals);
    }

    /// <summary>
    /// ddmm.mmmmm / dddmm.mmmmm plus hemisphere: the widths roastbeef makeGPRMCString uses
    /// (%010.5f / %011.5f). RB-02's parser accumulates the digits into an int32, which
    /// 179°59.99999' still fits.
    /// </summary>
    public static (string value, string hemi) LatLon(double deg, bool isLat)
    {
        double a = Math.Abs(deg);
        int d = (int)Math.Floor(a);
        double m = Math.Round((a - d) * 60.0, 5, MidpointRounding.AwayFromZero);
        if (m >= 60.0) { d += 1; m = 0; }
        string dd = isLat ? d.ToString("00", Inv) : d.ToString("000", Inv);
        string hemi = isLat ? (deg < 0 ? "S" : "N") : (deg < 0 ? "W" : "E");
        return (dd + m.ToString("00.00000", Inv), hemi);
    }

    static bool GpsUsable(SimState s, double now, out DateTime utc)
    {
        utc = default;
        if (!Staleness.Fresh(s.Pos, Staleness.PosS, now, s.Sim)) return false;
        if (s.Pos.LatDeg is null || s.Pos.LonDeg is null || s.Sim.Utc is null) return false;
        utc = s.Sim.Utc.Value;
        return true;
    }

    /// <summary>
    /// $GPRMC (NMEA 2.3, mode A, as roastbeef makeGPRMCString). Not sent while the position
    /// is not fresh: RB-02's nmea_RMC_mini_parser ignores the A/V status field, so a void
    /// fix would not be shown as invalid; its GPS timeout shows INOP instead.
    /// </summary>
    public static string? Rmc(SimState s, double now)
    {
        if (!GpsUsable(s, now, out var utc)) return null;
        var p = s.Pos;
        var (lat, ns) = LatLon(p.LatDeg!.Value, true);
        var (lon, ew) = LatLon(p.LonDeg!.Value, false);
        string magvar = "", magHemi = "";
        if (p.MagvarDeg is double mv)
        {
            magvar = Num(Math.Abs(mv), 1);
            magHemi = mv < 0 ? "W" : "E";
        }
        string body = string.Join(',',
            "GPRMC", utc.ToString("HHmmss.ff", Inv), "A", lat, ns, lon, ew,
            Num(p.GsMps is double gs ? Units.MpsToKt(gs) : null, 1), Num(p.TrackTrueDeg, 1),
            utc.ToString("ddMMyy", Inv), magvar, magHemi, "A");
        return Line(body);
    }

    /// <summary>$GPGGA. Satellites and HDOP are synthetic constants (8, 1.0); the geoid separation is empty when unknown.</summary>
    public static string? Gga(SimState s, double now)
    {
        if (!GpsUsable(s, now, out var utc)) return null;
        var p = s.Pos;
        var (lat, ns) = LatLon(p.LatDeg!.Value, true);
        var (lon, ew) = LatLon(p.LonDeg!.Value, false);
        string body = string.Join(',',
            "GPGGA", utc.ToString("HHmmss.ff", Inv), lat, ns, lon, ew, "1", "08", "1.0",
            Num(p.AltMslM, 1), "M", Num(p.GeoidSepM, 1), "M", "", "");
        return Line(body);
    }

    /// <summary>
    /// $RBATT (RB-SIM-ESP-020). Sent every time: invalid data gives empty fields and status 0,
    /// so the instrument shows INOP at once.
    /// </summary>
    public static string Rbatt(SimState s, double now)
    {
        var a = s.Att;
        if (!Staleness.Fresh(a, Staleness.AttS, now, s.Sim)) return Line("RBATT,,,,,,,0");
        string body = string.Join(',', "RBATT",
            Int16Field(a.PitchDeg, 1, -90, 90),
            Int16Field(a.RollDeg, 1, -180, 180),
            Int16Field(a.HdgTrueDeg is double h ? Math.Min(Units.Wrap360(h), 359.9) : null, 1, 0, 359.9),
            Int16Field(a.TurnRateDps, 1, -99.9, 99.9),
            Int16Field(a.SlipDeg, 1, -20, 20),
            Int16Field(a.GLoad, 2, -9.99, 9.99),
            "1");
        return Line(body);
    }

    /// <summary>$RBADC (RB-SIM-ESP-030): IAS kt, static hPa, baro VS ft/min, OAT °C, status.</summary>
    public static string Rbadc(SimState s, double now)
    {
        var a = s.Air;
        if (!Staleness.Fresh(a, Staleness.AirS, now, s.Sim)) return Line("RBADC,,,,,0");
        double? staticPa = a.StaticPa ?? (a.PressureAltM is double pa ? Units.IsaPressurePa(pa) : null);
        if (a.IasMps is null || staticPa is null) return Line("RBADC,,,,,0");
        string body = string.Join(',', "RBADC",
            Int16Field(Units.MpsToKt(a.IasMps.Value), 1, 0, 999.9),
            Int16Field(staticPa / Units.HPaToPa, 1, 100, 1100),
            Int16Field(a.VsBaroMps is double vs ? Units.MpsToFpm(vs) : null, 0, -9999, 9999),
            Int16Field(a.OatC, 1, -99.9, 99.9),
            "1");
        return Line(body);
    }

    /// <summary>
    /// $RBATT in RB-09's layout (D24, RB-SIM-ESP-090): the sentence an RB-02 prints for its
    /// remote display and rb-esp RB09_Slave.c parses. Same name as the RB-SIM-IF sentence of
    /// <see cref="Rbatt"/>, different layout. Roll, pitch, yaw (deg), acceleration x y z (g),
    /// gyro x y z (deg/s), then eight optional fields left empty. Mapped onto what RB-09 reads:
    /// yaw is minus the true heading, 0 to −360, because the heading card shows −yaw and the
    /// turn needle differentiates it. The acceleration is the g load at the ball angle, x on the
    /// normal axis and y lateral, negative with the ball right (RB-02 draws the ball at
    /// −120 px × y), z zero, so the G-meter's |accel| is the g load. The gyro is zero: RB-09
    /// reads none and takes the turn rate from the yaw. Pitch and roll keep the §2.1 signs, as
    /// RB-02's advanced attitude screen does. All of these signs were checked on an RB-09 screen
    /// (D24). Numbers follow RB09_Slave.c, which reads floats, not the int16 rule. Not sent while
    /// attitude is stale or a value is missing, so RB-09 shows INOP after its 1 s timeout.
    /// </summary>
    public static string? RbattSlave(SimState s, double now)
    {
        var a = s.Att;
        if (!Staleness.Fresh(a, Staleness.AttS, now, s.Sim)) return null;
        if (a.RollDeg is not double roll || a.PitchDeg is not double pitch || a.HdgTrueDeg is not double hdg
            || a.SlipDeg is not double slip || a.GLoad is not double g) return null;
        g = Math.Clamp(g, -9.99, 9.99);
        double ball = Math.Clamp(slip, -20, 20) * Math.PI / 180.0;
        string body = string.Join(',', "RBATT",
            Num(Math.Clamp(roll, -180, 180), 2),
            Num(Math.Clamp(pitch, -90, 90), 2),
            Num(-Units.Wrap360(hdg), 2),
            Num(g * Math.Cos(ball), 3),
            Num(-g * Math.Sin(ball), 3),
            Num(0, 3),
            Num(0, 2), Num(0, 2), Num(0, 2),
            "", "", "", "", "", "", "", "");
        return Line(body);
    }

    /// <summary>
    /// $RBAIR, RB-09's air data (D24, RB-SIM-ESP-090), as rb-esp RB09_Slave_FormatAir() writes
    /// it: static pressure and QNH in inHg (4 and 5 decimals), OAT °C, baro VS ft/min, IAS km/h,
    /// and the wind left empty (RB devices compute their own). RB-09 adopts this QNH: a remote
    /// display follows its master's setting, and here the sim is the master. Not sent while air
    /// data is stale or the static pressure, QNH or VS is unknown, so RB-09 shows INOP after its
    /// 3 s timeout instead of an altitude on an invented setting.
    /// </summary>
    public static string? Rbair(SimState s, double now)
    {
        var a = s.Air;
        if (!Staleness.Fresh(a, Staleness.AirS, now, s.Sim)) return null;
        double? staticPa = a.StaticPa ?? (a.PressureAltM is double pa ? Units.IsaPressurePa(pa) : null);
        if (staticPa is null || a.SimQnhPa is not double qnh || a.VsBaroMps is not double vs) return null;
        string body = string.Join(',', "RBAIR",
            Num(Units.PaToInHg(staticPa.Value), 4),
            Num(Units.PaToInHg(qnh), 5),
            Num(a.OatC, 1),
            Num(Units.MpsToFpm(vs), 0),
            Num(a.IasMps is double ias ? Units.MpsToKmh(ias) : null, 1),
            "", "");
        return Line(body);
    }

    /// <summary>A traffic target relative to ownship, in the FLARM frame (metres, + north, + east, + above).</summary>
    public sealed record FlarmTarget(TrafficTarget Target, double NorthM, double EastM, double? AboveM, double DistanceM);

    /// <summary>
    /// The targets RB-05 can hold: fresh, nearest first, at most 10, and with relative
    /// north/east/vertical that fit the int16 FlarmObstacle fields (beyond ~17.7 NM is dropped).
    /// </summary>
    public static List<FlarmTarget> FlarmTargets(SimState s, double now, int max = MaxFlarmTargets)
    {
        var list = new List<FlarmTarget>();
        if (!Staleness.Fresh(s.Pos, Staleness.PosS, now, s.Sim) || s.Pos.LatDeg is not double lat || s.Pos.LonDeg is not double lon)
            return list;
        const double R = 6371008.8;
        double rad = Math.PI / 180.0;
        foreach (var t in s.Traffic)
        {
            if (now - t.TMono > Staleness.TrafficTargetS) continue;
            double north = (t.LatDeg - lat) * rad * R;
            double east = (t.LonDeg - lon) * rad * R * Math.Cos(lat * rad);
            double? above = t.AltMslM is double ta && s.Pos.AltMslM is double oa ? ta - oa : null;
            if (Math.Abs(north) > Int16Max || Math.Abs(east) > Int16Max) continue;
            if (above is double v && Math.Abs(v) > Int16Max) continue;
            list.Add(new FlarmTarget(t, north, east, above, Math.Sqrt(north * north + east * east)));
        }
        return [.. list.OrderBy(x => x.DistanceM).Take(max)];
    }

    /// <summary>
    /// $PFLAU, FLARM data port spec: RX, TX, GPS, Power, AlarmLevel, RelativeBearing,
    /// AlarmType, RelativeVertical, RelativeDistance, ID. No collision prediction runs here,
    /// so this is always roastbeef's no-alarm form <c>$PFLAU,n,1,gps,1,0,,0,,,</c>.
    /// </summary>
    public static string Pflau(SimState s, double now, int targetCount)
    {
        bool gps = Staleness.Fresh(s.Pos, Staleness.PosS, now, s.Sim);
        string body = string.Join(',', "PFLAU", Math.Min(targetCount, 99).ToString(Inv), "1", gps ? "2" : "0", "1", "0", "", "0", "", "", "");
        return Line(body);
    }

    /// <summary>
    /// $PFLAA, as roastbeef makeFlarmPFLAAString writes it for RB-05: AlarmLevel,
    /// RelativeNorth, RelativeEast, RelativeVertical (m, + above), IDType, ID, Track (integer
    /// deg), TurnRate (0), GroundSpeed (integer m/s), ClimbRate (m/s, one decimal, ±32.7),
    /// AcftType (hex). The ID is 6 hex digits plus <c>!CALLSIGN</c> when a callsign is known
    /// (RB-05's FlarmObstacle has tail[8]). Sim objects carry pseudo addresses, so IDType is
    /// 2 (roastbeef's mapping for non-ICAO addresses); AcftType 8 is a piston aircraft.
    /// </summary>
    public static string Pflaa(FlarmTarget f)
    {
        var t = f.Target;
        string id = (t.Id24 & 0xFFFFFF).ToString("X6", Inv);
        string tail = Tail(t.Callsign);
        if (tail.Length > 0) id += "!" + tail;
        string track = t.TrackTrueDeg is double tr ? ((int)Math.Floor(Units.Wrap360(tr)) % 360).ToString(Inv) : "";
        string body = string.Join(',', "PFLAA", "0",
            Num(f.NorthM, 0), Num(f.EastM, 0), Num(f.AboveM, 0),
            "2", id, track, "0",
            Num(t.GsMps is double gs ? Math.Clamp(gs, 0, Int16Max) : null, 0),
            Num(t.VsMps is double vs ? Math.Clamp(vs, -32.7, 32.7) : null, 1), "8");
        return Line(body);
    }

    /// <summary>
    /// A callsign safe inside an NMEA field: A–Z, 0–9 and '-', at most 7 characters so it
    /// fits RB-05's char tail[8] with its NUL (verify once RB05_FlarmParser.c is published).
    /// </summary>
    public static string Tail(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return "";
        var sb = new StringBuilder(7);
        foreach (char c in callsign.ToUpperInvariant())
        {
            if (sb.Length == 7) break;
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-') sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// $RBEMS as roastbeef makeRBEMSString (rb-esp RB04_NMEA_Parser.c): rpm, manifold
    /// pressure, oil temperature, oil pressure, coolant temperature, OAT, fuel pressure,
    /// fuel left, fuel right; integers in the units of the aircraft's ems.json. A value the
    /// sim does not give is an empty field (unknown) rather than roastbeef's 0. Not sent while
    /// the engine group is stale, so RB-04 times out to INOP.
    /// </summary>
    public static string? Rbems(SimState s, double now, EmsUnits u)
    {
        var e = s.Eng.Length > 0 ? s.Eng[0] : null;
        if (e is null || !Staleness.Fresh(e, Staleness.EngS, now, s.Sim)) return null;
        bool air = Staleness.Fresh(s.Air, Staleness.AirS, now, s.Sim);
        bool fuel = Staleness.Fresh(s.Fuel, Staleness.EngS, now, s.Sim);
        string body = string.Join(',', "RBEMS",
            Int16Field(e.Rpm, 0, 0, 9999),
            Int16Field(e.MapPa is double map ? RbSimIf.Pressure(map, u.ManifoldPressure) : null, 0, 0, 999),
            Int16Field(e.OilTempC, 0, -99, 999),
            Int16Field(e.OilPressPa is double op ? RbSimIf.Pressure(op, u.OilPressure) : null, 0, 0, 999),
            Int16Field(e.CoolantTempC, 0, -99, 999),
            Int16Field(air ? s.Air.OatC : null, 0, -99, 99),
            Int16Field(e.FuelPressPa is double fp ? RbSimIf.Pressure(fp, u.FuelPressure) : null, 0, 0, 999),
            Int16Field(fuel && s.Fuel.LeftL is double l ? RbSimIf.Volume(l, u.Fuel) : null, 0, 0, 9999),
            Int16Field(fuel && s.Fuel.RightL is double r ? RbSimIf.Volume(r, u.Fuel) : null, 0, 0, 9999));
        return Line(body);
    }

    /// <summary>$RBCYL as roastbeef makeRBCYLString: CHT 1–4 then EGT 1–4, integer °C.</summary>
    public static string? Rbcyl(SimState s, double now)
    {
        var e = s.Eng.Length > 0 ? s.Eng[0] : null;
        if (e is null || !Staleness.Fresh(e, Staleness.EngS, now, s.Sim)) return null;
        var fields = new List<string> { "RBCYL" };
        for (int i = 0; i < 4; i++) fields.Add(Int16Field(i < e.ChtC.Length ? e.ChtC[i] : null, 0, -99, 999));
        for (int i = 0; i < 4; i++) fields.Add(Int16Field(i < e.EgtC.Length ? e.EgtC[i] : null, 0, -99, 1999));
        return Line(string.Join(',', fields));
    }

    // Guidance sentences (D25): the four RB-01 itself sends while its autopilot is active
    // (roastbeef autopilot.go), in its field layouts, so RB-01's UDP autopilot input
    // (autopilotudp.go, the one an EFB feeds) and serial autopilots read them as from RB-01.
    // None goes out without guidance: RB-01 would draw a path to whatever the last fields said.

    /// <summary>
    /// $GPRMB as roastbeef makeGPRMBString: status, cross-track NM (≤ 9.99), steer L/R, origin
    /// and destination IDs, destination lat/lon, range NM, true bearing, closing speed kt, and
    /// the arrival alarm (A inside 500 m). RB-01's UDP input builds its path and HSI target
    /// from this one alone.
    /// </summary>
    public static string? Gprmb(Guidance? g)
    {
        if (g is null) return null;
        var (lat, ns) = LatLon(g.ToLatDeg, true);
        var (lon, ew) = LatLon(g.ToLonDeg, false);
        string body = string.Join(',', "GPRMB", "A", Num(g.XtkNm, 2), g.Steer,
            WaypointId(g.FromId), WaypointId(g.ToId), lat, ns, lon, ew,
            Num(Units.MToNm(g.DistanceM), 1), Deg(g.BearingTrueDeg, 1),
            Num(g.ClosingMps is double c ? Units.MpsToKt(c) : null, 1), g.Arrived ? "A" : "V");
        return Line(body);
    }

    /// <summary>
    /// $GPAPB as roastbeef makeGPAPBString: cross-track and steer, arrival circle entered,
    /// perpendicular passed, the leg course origin → destination, the destination, then the
    /// bearing to it twice (bearing and heading to steer). Every angle true, tagged T, as
    /// roastbeef does.
    /// </summary>
    public static string? Gpapb(Guidance? g)
    {
        if (g is null) return null;
        string body = string.Join(',', "GPAPB", "A", "A", Num(g.XtkNm, 2), g.Steer, "N",
            g.Arrived ? "A" : "V", g.PerpendicularPassed ? "A" : "V",
            Deg3(g.LegCourseTrueDeg), "T", WaypointId(g.ToId),
            Deg3(g.BearingTrueDeg), "T", Deg3(g.BearingTrueDeg), "T");
        return Line(body);
    }

    /// <summary>
    /// $GPBOD as roastbeef makeGPBODString: the leg course true, and magnetic when the sim gives
    /// the variation (roastbeef leaves it empty: it has none), destination, origin.
    /// </summary>
    public static string? Gpbod(Guidance? g)
    {
        if (g is null) return null;
        string mag = g.MagvarDeg is double mv ? Deg(g.LegCourseTrueDeg - mv, 1) : "";
        string body = string.Join(',', "GPBOD", Deg(g.LegCourseTrueDeg, 1), "T", mag, "M",
            WaypointId(g.ToId), WaypointId(g.FromId));
        return Line(body);
    }

    /// <summary>$GPXTE as roastbeef makeGPXTEString: the cross-track on its own.</summary>
    public static string? Gpxte(Guidance? g) =>
        g is null ? null : Line(string.Join(',', "GPXTE", "A", "A", Num(g.XtkNm, 2), g.Steer, "N"));

    /// <summary>
    /// A waypoint ID safe inside an NMEA field: A–Z, 0–9 and '-', at most 10 characters
    /// (MSFS "USER WPT" becomes "USERWPT").
    /// </summary>
    public static string WaypointId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var sb = new StringBuilder(10);
        foreach (char c in id.ToUpperInvariant())
        {
            if (sb.Length == 10) break;
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-') sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>An angle 0 ≤ x &lt; 360 at fixed decimals: 359.97 gives "0.0", never "360.0".</summary>
    static string Deg(double deg, int decimals)
    {
        double r = Math.Round(Units.Wrap360(deg), decimals, MidpointRounding.AwayFromZero);
        return Num(r >= 360.0 ? 0 : r, decimals);
    }

    /// <summary>roastbeef's %03.0f angle, 000–359.</summary>
    static string Deg3(double deg) =>
        ((int)Math.Round(Units.Wrap360(deg), MidpointRounding.AwayFromZero) % 360).ToString("000", Inv);

    /// <summary>The ASCII bytes of a line.</summary>
    public static byte[] Bytes(string line) => Encoding.ASCII.GetBytes(line);

    /// <summary>
    /// One UDP datagram of lines, padded with spaces to <paramref name="padTo"/> bytes when
    /// shorter. RB-01's UDP autopilot input (roastbeef autopilotudp.go) reads every datagram into
    /// the same 2048-byte buffer and parses the whole buffer, not the bytes received, so a datagram
    /// shorter than the one before would hand it the older one's tail again: stale $GPRMB/$GPAPB.
    /// Datagrams of one constant length overwrite each other completely. The padding follows the
    /// last CRLF, so it reads as one blank line. 0 sends the lines unpadded.
    /// </summary>
    public static byte[] Datagram(IEnumerable<string> lines, int padTo)
    {
        string text = string.Concat(lines);
        if (text.Length < padTo) text = text.PadRight(padTo);
        return Encoding.ASCII.GetBytes(text);
    }
}
