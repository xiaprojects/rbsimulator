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
// GuidanceTests.cs: guidance geometry, the $GPRMB/$GPAPB/$GPBOD/$GPXTE lines, the UDP datagram and the HMI autopilot frames (D25).

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using RbSim.Core;

namespace RbSim.Core.Tests;

public class GuidanceTests
{
    const double Now = 100;
    const double Lat0 = 43.0, Lon0 = 12.0;
    static double NorthDeg(double m) => m / Guidance.EarthRadiusM * 180 / Math.PI;
    static double EastDeg(double m, double lat) => NorthDeg(m) / Math.Cos(lat * Math.PI / 180);

    /// <summary>
    /// A northbound leg along 12°E from 43°N to 10 NM north, the aircraft at
    /// <paramref name="northM"/>, <paramref name="eastM"/> from the origin, flying north at 100 kt.
    /// </summary>
    static SimState Leg(double northM, double eastM, bool withOrigin = true, double? magvar = 2.5)
    {
        var s = new SimState();
        s.Sim.Connected = true;
        double lat = Lat0 + NorthDeg(northM);
        s.Pos = new PosGroup
        {
            Valid = true,
            TMono = Now,
            LatDeg = lat,
            LonDeg = Lon0 + EastDeg(eastM, lat),
            AltMslM = 914.4,
            TrackTrueDeg = 0,
            GsMps = 100 * Units.KtToMps,
            MagvarDeg = magvar,
        };
        s.Nav = new NavGroup
        {
            Valid = true,
            TMono = Now,
            FromId = "WPT01",
            FromLatDeg = withOrigin ? Lat0 : null,
            FromLonDeg = withOrigin ? Lon0 : null,
            ToId = "WPT02",
            ToLatDeg = Lat0 + NorthDeg(10 * Units.NmToM),
            ToLonDeg = Lon0,
            ToAltMslM = 1524,
        };
        return s;
    }

    static string[] Fields(string? line)
    {
        Assert.NotNull(line);
        Assert.EndsWith("\r\n", line);
        int star = line.LastIndexOf('*');
        string body = line[1..star];
        byte sum = 0;
        foreach (char c in body) sum ^= (byte)c;
        Assert.Equal(sum.ToString("X2"), line[(star + 1)..^2]);
        return body.Split(',');
    }

    /// <summary>roastbeef autopilotudp.go parseLatLon, transcribed: fixed 2 or 3 degree digits.</summary>
    static double RoastbeefLatLon(string value, string hemi, bool isLat)
    {
        int degLen = isLat ? 2 : 3;
        double d = double.Parse(value[..degLen], CultureInfo.InvariantCulture) + double.Parse(value[degLen..], CultureInfo.InvariantCulture) / 60.0;
        return hemi is "S" or "W" ? -d : d;
    }

    // Covers: RB-SIM-ESP-100
    [Theory]
    [InlineData(1852.0, "L")]  // 1 NM right of a northbound course: steer left
    [InlineData(-1852.0, "R")]
    public void CrossTrackIsPositiveRightOfCourseAsRoastbeef(double eastM, string steer)
    {
        var g = Guidance.Compute(Leg(5 * Units.NmToM, eastM), Now)!;
        Assert.Equal(eastM, g.XtkM, 1.0);
        Assert.Equal(steer, g.Steer);
        Assert.Equal(1.0, g.XtkNm, 3);
        Assert.Equal(0.0, Guidance.AngleDiff(g.LegCourseTrueDeg, 0), 6);
    }

    // Covers: RB-SIM-ESP-100
    [Fact]
    public void DistanceBearingAndClosingSpeedPointAtTheWaypoint()
    {
        var g = Guidance.Compute(Leg(5 * Units.NmToM, 0), Now)!;
        Assert.Equal(5 * Units.NmToM, g.DistanceM, 5.0);
        Assert.Equal(0.0, Guidance.AngleDiff(g.BearingTrueDeg, 0), 3);
        Assert.Equal(100.0, Units.MpsToKt(g.ClosingMps!.Value), 2);
        Assert.False(g.Arrived);
        Assert.False(g.PerpendicularPassed);
    }

    // Covers: RB-SIM-ESP-100
    [Fact]
    public void ALegWithoutOriginIsADirectToFromTheAircraft()
    {
        var g = Guidance.Compute(Leg(5 * Units.NmToM, 1852, withOrigin: false), Now)!;
        Assert.Equal(0.0, g.XtkM);
        Assert.Equal(g.BearingTrueDeg, g.LegCourseTrueDeg);
        Assert.Null(g.FromLatDeg);
    }

    // Covers: RB-SIM-ESP-100
    [Fact]
    public void ArrivalInside500mAndPerpendicularPassedBeyondTheWaypoint()
    {
        var near = Guidance.Compute(Leg(10 * Units.NmToM - 400, 0), Now)!;
        Assert.True(near.Arrived);
        Assert.False(near.PerpendicularPassed);
        var beyond = Guidance.Compute(Leg(10 * Units.NmToM + 300, 0), Now)!;
        Assert.True(beyond.Arrived);
        Assert.True(beyond.PerpendicularPassed);
    }

    // Covers: RB-SIM-ESP-100, RB-SIM-SAF-050
    [Fact]
    public void NoGuidanceWithoutAnActiveFreshLegOrPosition()
    {
        var s = Leg(0, 0);
        s.Nav.Valid = false;
        Assert.Null(Guidance.Compute(s, Now));
        s = Leg(0, 0);
        Assert.Null(Guidance.Compute(s, Now + Staleness.PosS + 0.1)); // both stale
        s.Nav.TMono = Now + 3;
        Assert.Null(Guidance.Compute(s, Now + 3)); // position stale, leg fresh
        s.Sim.Connected = false;
        Assert.Null(Guidance.Compute(s, Now));
        foreach (var line in new[] { Nmea.Gprmb(null), Nmea.Gpapb(null), Nmea.Gpbod(null), Nmea.Gpxte(null) })
            Assert.Null(line);
    }

    // Covers: RB-SIM-ESP-100
    [Fact]
    public void GprmbHasRoastbeefsFieldsAndItsParserReadsItBack()
    {
        var g = Guidance.Compute(Leg(5 * Units.NmToM, 1852), Now)!;
        var f = Fields(Nmea.Gprmb(g));
        // roastbeef ParseGPRMB needs at least 14 fields, the sentence name included.
        Assert.Equal(14, f.Length);
        Assert.Equal(["GPRMB", "A", "1.00", "L", "WPT01", "WPT02"], f[..6]);
        Assert.Equal(Lat0 + NorthDeg(10 * Units.NmToM), RoastbeefLatLon(f[6], f[7], true), 6);
        Assert.Equal(Lon0, RoastbeefLatLon(f[8], f[9], false), 6);
        // 1 NM right with 5 NM to go: √26 NM away, atan(1/5) = 11.3° left, closing 100 kt × cos 11.3°.
        Assert.Equal("5.1", f[10]);
        Assert.Equal("348.7", f[11]);
        Assert.Equal("98.1", f[12]);
        Assert.Equal("V", f[13]);
    }

    // Covers: RB-SIM-ESP-100
    [Fact]
    public void GprmbPegsTheCrossTrackAndWritesWesternSouthernHemispheres()
    {
        var s = Leg(5 * Units.NmToM, 30 * Units.NmToM);
        s.Nav.ToLatDeg = -33.5;
        s.Nav.ToLonDeg = -70.25;
        var f = Fields(Nmea.Gprmb(Guidance.Compute(s, Now)));
        Assert.Equal("9.99", f[2]);
        Assert.Equal(("3330.00000", "S"), (f[6], f[7]));
        Assert.Equal(("07015.00000", "W"), (f[8], f[9]));
        Assert.Equal(-70.25, RoastbeefLatLon(f[8], f[9], false), 9);
    }

    // Covers: RB-SIM-ESP-100
    [Fact]
    public void GpapbGpbodAndGpxteCarryTheLegAsRoastbeef()
    {
        var g = Guidance.Compute(Leg(5 * Units.NmToM, -926), Now)!;
        var apb = Fields(Nmea.Gpapb(g));
        // Leg course 000; from 0.5 NM left of it the waypoint bears atan(0.5/5) = 006.
        Assert.Equal(["GPAPB", "A", "A", "0.50", "R", "N", "V", "V", "000", "T", "WPT02", "006", "T", "006", "T"], apb);
        var bod = Fields(Nmea.Gpbod(g));
        Assert.Equal(["GPBOD", "0.0", "T", "357.5", "M", "WPT02", "WPT01"], bod); // magnetic = true − easterly variation
        Assert.Equal(["GPXTE", "A", "A", "0.50", "R", "N"], Fields(Nmea.Gpxte(g)));
        var noVar = Guidance.Compute(Leg(5 * Units.NmToM, 0, magvar: null), Now)!;
        Assert.Equal("", Fields(Nmea.Gpbod(noVar))[3]);
    }

    // Covers: RB-SIM-ESP-100
    [Theory]
    [InlineData("LIRF", "LIRF")]
    [InlineData("USER WPT", "USERWPT")]
    [InlineData("rw16l", "RW16L")]
    [InlineData("A,B*C$D", "ABCD")]
    [InlineData("ABCDEFGHIJKL", "ABCDEFGHIJ")]
    [InlineData(null, "")]
    public void WaypointIdsAreSafeNmeaFields(string? id, string expected) => Assert.Equal(expected, Nmea.WaypointId(id));

    // Covers: RB-SIM-ESP-070, RB-SIM-ESP-100
    [Fact]
    public void Rb01NavProfileSendsTheFourSentencesInRoastbeefsOrderAt1Hz()
    {
        var gen = new DeviceLineGenerator(DeviceProfiles.Resolve("rb01-nav", null));
        var s = Leg(5 * Units.NmToM, 0);
        var lines = gen.Due(s, Now);
        Assert.Equal(["$GPRMB", "$GPAPB", "$GPBOD", "$GPXTE"], lines.Select(l => l[..6]));
        Assert.Empty(gen.Due(s, Now + 0.5));
        s.Pos.TMono = s.Nav.TMono = Now + 1;
        Assert.Equal(4, gen.Due(s, Now + 1).Count);
        s.Nav.Valid = false;
        s.Pos.TMono = Now + 2;
        Assert.Empty(gen.Due(s, Now + 2));
        Assert.Equal(1, DeviceProfiles.Resolve("rb02", new Dictionary<string, double> { ["gprmb"] = 1 })[Sentence.Gprmb]);
    }

    // Covers: RB-SIM-ESP-100
    [Fact]
    public void DatagramsArePaddedToOneLengthAfterTheLastLine()
    {
        string[] lines = ["$GPXTE,A,A,0.50,R,N*00\r\n", "$GPBOD,0.0,T,,M,B,A*00\r\n"];
        var d = Nmea.Datagram(lines, 1024);
        Assert.Equal(1024, d.Length);
        string text = Encoding.ASCII.GetString(d);
        Assert.StartsWith(string.Concat(lines), text);
        Assert.Equal("", text[string.Concat(lines).Length..].Trim(' '));
        // What RB-01 does with it: split on \n, act on lines with a known prefix.
        Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith('$')));
        Assert.Equal(string.Concat(lines).Length, Nmea.Datagram(lines, 0).Length);
        Assert.Equal(string.Concat(lines).Length, Nmea.Datagram(lines, 10).Length);
    }

    // Covers: RB-SIM-HMI-060
    [Fact]
    public void HmiAutopilotFrameAndWaypointsFollowTheLeg()
    {
        var s = Leg(5 * Units.NmToM, 1852);
        var ap = Rb01Hmi.Autopilot(s, Now);
        Assert.True((bool)ap["Active"]!);
        var rmb = ap["GPRMB"]!.AsObject();
        Assert.Equal("A", (string?)rmb["Status"]);
        Assert.Equal("L", (string?)rmb["Steer"]);
        Assert.Equal(1.0, (double)rmb["XTRKDistance"]!, 2);
        Assert.Equal("WPT02", (string?)rmb["WaypointDest"]);
        Assert.Equal(0.0, Guidance.AngleDiff((double)rmb["LegCourse"]!, 0), 1);

        var wps = Rb01Hmi.Waypoints(s, Now);
        Assert.Equal(2, wps.Count);
        Assert.Equal(Lat0, (double)wps[0]!["Lat"]!);
        Assert.Equal(2, (int)wps[0]!["Status"]!);           // past
        Assert.Equal(1, (int)wps[1]!["Status"]!);           // target
        Assert.Equal(5000, (int)wps[1]!["Ele"]!);           // the plan's altitude, feet
        Assert.Equal("WPT02", (string?)wps[1]!["Cmt"]);
        Assert.True(JsonNode.DeepEquals(wps[1], ap["To"]));
    }

    // Covers: RB-SIM-HMI-060, RB-SIM-SAF-050
    [Fact]
    public void HmiAutopilotIsIdleAndTheListEmptyWithoutGuidance()
    {
        var s = Leg(5 * Units.NmToM, 0, withOrigin: false);
        var wps = Rb01Hmi.Waypoints(s, Now);
        Assert.Equal(s.Pos.LatDeg, (double)wps[0]!["Lat"]!); // no origin: the leg starts at the aircraft
        Assert.Equal(3000, (int)wps[0]!["Ele"]!);
        s.Nav.ToAltMslM = null;
        Assert.Equal(3000, (int)Rb01Hmi.Waypoints(s, Now)[1]!["Ele"]!);

        s.Nav.Valid = false;
        Assert.Empty(Rb01Hmi.Waypoints(s, Now));
        var ap = Rb01Hmi.Autopilot(s, Now);
        Assert.False((bool)ap["Active"]!);
        Assert.Equal("V", (string?)ap["GPRMB"]!["Status"]);
        Assert.Equal(0, (double)ap["To"]!["Lat"]!);
    }
}

public class MsfsNavMapTests
{
    static (double[] raw, string?[] str) Frame(bool active = true, bool prev = true)
    {
        var raw = Frames.Cruise();
        raw[(int)U.GpsWpActive] = active ? 1 : 0;
        raw[(int)U.GpsWpNextLat] = 43.5; raw[(int)U.GpsWpNextLon] = 12.25; raw[(int)U.GpsWpNextAlt] = 1524;
        raw[(int)U.GpsWpPrevValid] = prev ? 1 : 0;
        raw[(int)U.GpsWpPrevLat] = 43.1; raw[(int)U.GpsWpPrevLon] = 12.1;
        var str = new string?[MsfsDatums.User.Length];
        str[(int)U.GpsWpNextId] = "LIRZ ";
        str[(int)U.GpsWpPrevId] = "LIQQ";
        return (raw, str);
    }

    // Covers: RB-SIM-MSF-150
    [Fact]
    public void TheActiveLegMapsFromTheGpsVariables()
    {
        var (raw, str) = Frame();
        var s = new SimState();
        MsfsMap.ApplyUser(s, raw, 5, new MsfsProfile(), str);
        Assert.True(s.Nav.Valid);
        Assert.Equal(5, s.Nav.TMono);
        Assert.Equal(("LIRZ", 43.5, 12.25, 1524.0), (s.Nav.ToId, s.Nav.ToLatDeg, s.Nav.ToLonDeg, s.Nav.ToAltMslM));
        Assert.Equal(("LIQQ", 43.1, 12.1), (s.Nav.FromId, s.Nav.FromLatDeg, s.Nav.FromLonDeg));
    }

    // Covers: RB-SIM-MSF-150, RB-SIM-MSF-130
    [Fact]
    public void NoActiveWaypointNoPlanOrARejectedDatumLeavesNoLeg()
    {
        var (raw, str) = Frame(active: false);
        Assert.False(MsfsMap.Nav(raw, str, 1).Valid);

        (raw, str) = Frame();
        raw[(int)U.GpsWpNextLat] = 0; raw[(int)U.GpsWpNextLon] = 0; // what the variables read with no plan
        Assert.False(MsfsMap.Nav(raw, str, 1).Valid);

        (raw, str) = Frame();
        raw[(int)U.GpsWpNextLon] = double.NaN;
        Assert.False(MsfsMap.Nav(raw, str, 1).Valid);

        // The cruise frame of the other tests has no GPS variables set: no leg.
        Assert.False(Frames.Mapped().Nav.Valid);
    }

    // Covers: RB-SIM-MSF-150
    [Fact]
    public void AnInvalidOriginAndAZeroAltitudeAreDropped()
    {
        var (raw, str) = Frame(prev: false);
        raw[(int)U.GpsWpNextAlt] = 0;
        var nav = MsfsMap.Nav(raw, str, 1);
        Assert.True(nav.Valid);
        Assert.Null(nav.FromLatDeg);
        Assert.Equal("", nav.FromId);
        Assert.Null(nav.ToAltMslM);
        Assert.Equal("", MsfsMap.Nav(raw, null, 1).ToId); // strings rejected
    }
}
