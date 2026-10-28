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
// NmeaTests.cs: device-line format, FLARM geometry and schedule tests (protocol §4.4).

using RbSim.Core;

namespace RbSim.Core.Tests;

public class NmeaTests
{
    static string Body(string line)
    {
        Assert.StartsWith("$", line);
        Assert.EndsWith("\r\n", line);
        int star = line.LastIndexOf('*');
        string body = line[1..star];
        byte sum = 0;
        foreach (char c in body) sum ^= (byte)c;
        Assert.Equal(sum.ToString("X2"), line[(star + 1)..^2]);
        Assert.True(line.Length - 2 <= Nmea.MaxLineChars);
        Assert.All(line[..^2], c => Assert.InRange(c, (char)0x20, (char)0x7E));
        return body;
    }

    // Covers: RB-SIM-ESP-010
    [Fact]
    public void ChecksumMatchesTheClassicNmeaExample() =>
        Assert.Equal("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47\r\n",
            Nmea.Line("GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,"));

    // Covers: RB-SIM-ESP-010
    [Fact]
    public void LineRejectsReservedCharactersAndOverlongLines()
    {
        Assert.Throws<ArgumentException>(() => Nmea.Line("GPRMC,a*b"));
        Assert.Throws<ArgumentException>(() => Nmea.Line("GPRMC,è"));
        Assert.Throws<ArgumentException>(() => Nmea.Line("X," + new string('1', 250)));
    }

    [Theory]
    [InlineData(-0.04, 1, "0.0")]
    [InlineData(2.05, 1, "2.1")]
    [InlineData(-250.4, 0, "-250")]
    [InlineData(double.NaN, 1, "")]
    public void NumberFormatting(double v, int decimals, string expected) => Assert.Equal(expected, Nmea.Num(v, decimals));

    [Theory]
    [InlineData(43.18528, true, "4311.11680", "N")]
    [InlineData(-33.5, true, "3330.00000", "S")]
    [InlineData(12.136674, false, "01208.20044", "E")]
    [InlineData(-0.5, false, "00030.00000", "W")]
    [InlineData(10.9999999999, true, "1100.00000", "N")] // minutes round to 60 → carry
    public void LatLonFormat(double deg, bool isLat, string value, string hemi) =>
        Assert.Equal((value, hemi), Nmea.LatLon(deg, isLat));

    // Covers: RB-SIM-ESP-070
    [Fact]
    public void RmcFromCruiseFrame()
    {
        var line = Nmea.Rmc(Frames.Mapped(now: 100), 100.1)!;
        Assert.Equal("GPRMC,101530.00,A,4311.11680,N,01208.20044,E,100.0,271.4,261026,2.8,E,A", Body(line));
    }

    [Fact]
    public void GgaFromCruiseFrame()
    {
        var line = Nmea.Gga(Frames.Mapped(now: 100), 100.1)!;
        Assert.Equal("GPGGA,101530.00,4311.11680,N,01208.20044,E,1,08,1.0,914.4,M,,M,,", Body(line));
    }

    // Covers: RB-SIM-ESP-050
    [Fact]
    public void GpsSentencesStopWhenPositionIsStaleOrDisconnected()
    {
        var s = Frames.Mapped(now: 100);
        Assert.Null(Nmea.Rmc(s, 102.5));
        Assert.Null(Nmea.Gga(s, 102.5));
        s.Sim.Connected = false;
        Assert.Null(Nmea.Rmc(s, 100.1));
    }

    // Covers: RB-SIM-ESP-020
    [Fact]
    public void RbattValidAndInvalid()
    {
        var s = Frames.Mapped(now: 100);
        Assert.Equal("RBATT,2.1,-15.3,271.4,-3.0,1.0,1.03,1", Body(Nmea.Rbatt(s, 100.1)));
        Assert.Equal("RBATT,,,,,,,0", Body(Nmea.Rbatt(s, 100.6)));
    }

    // Covers: RB-SIM-ESP-020
    [Fact]
    public void RbattPegsOutOfRangeValuesInsteadOfOverflowingInt16()
    {
        var raw = Frames.Cruise();
        raw[(int)U.TurnRate] = 150;
        raw[(int)U.GForce] = 12;
        raw[(int)U.HdgTrue] = 359.97;
        var body = Body(Nmea.Rbatt(Frames.Mapped(now: 100, raw: raw), 100.1));
        Assert.Equal("RBATT,2.1,-15.3,359.9,99.9,1.0,9.99,1", body);
    }

    // Covers: RB-SIM-ESP-030
    [Fact]
    public void RbadcValidAndInvalid()
    {
        var s = Frames.Mapped(now: 100);
        Assert.Equal("RBADC,98.4,908.0,-250,12.0,1", Body(Nmea.Rbadc(s, 100.1)));
        Assert.Equal("RBADC,,,,,0", Body(Nmea.Rbadc(s, 101.5)));
    }

    // Covers: RB-SIM-ESP-030
    [Fact]
    public void RbadcFallsBackToIsaStaticPressure()
    {
        var raw = Frames.Cruise();
        raw[(int)U.AmbientPressure] = double.NaN;
        raw[(int)U.PressureAlt] = 0;
        Assert.Equal("RBADC,98.4,1013.3,-250,12.0,1", Body(Nmea.Rbadc(Frames.Mapped(now: 100, raw: raw), 100.1)));
    }

    static SimState WithTraffic(params (double north, double east, double altM, int id)[] targets)
    {
        var s = Frames.Mapped(now: 100);
        double lat = s.Pos.LatDeg!.Value, lon = s.Pos.LonDeg!.Value;
        const double R = 6371008.8;
        s.Traffic = [.. targets.Select(t => new TrafficTarget
        {
            Id24 = t.id,
            LatDeg = lat + t.north / R * 180 / Math.PI,
            LonDeg = lon + t.east / (R * Math.Cos(lat * Math.PI / 180)) * 180 / Math.PI,
            AltMslM = t.altM, TrackTrueDeg = 90, GsMps = 57.2, VsMps = -1.04, TMono = 100,
        })];
        return s;
    }

    // Covers: RB-SIM-ESP-080
    [Fact]
    public void FlarmTargetsNearestFirstCappedAndInt16Safe()
    {
        var list = new List<(double, double, double, int)> { (40_000, 0, 914.4, 999) }; // beyond int16 metres
        for (int i = 1; i <= 12; i++) list.Add((i * 1000.0, 0, 914.4, i));
        var s = WithTraffic([.. list]);
        var targets = Nmea.FlarmTargets(s, 100.1);
        Assert.Equal(10, targets.Count);
        Assert.Equal(Enumerable.Range(1, 10), targets.Select(t => t.Target.Id24));
        Assert.DoesNotContain(targets, t => t.Target.Id24 == 999);
    }

    [Fact]
    public void FlarmNeedsFreshOwnship()
    {
        var s = WithTraffic((1000, 0, 914.4, 1));
        Assert.Empty(Nmea.FlarmTargets(s, 103));
        Assert.Equal("PFLAU,0,1,0,1,0,,0,,,", Body(Nmea.Pflau(s, 103, 0)));
    }

    [Fact]
    public void PflauAndPflaaFormat()
    {
        var s = WithTraffic((1000, -500, 914.4 + 150, 0xF0ABCD));
        var t = Nmea.FlarmTargets(s, 100.1).Single();
        Assert.Equal("PFLAU,1,1,2,1,0,,0,,,", Body(Nmea.Pflau(s, 100.1, 1)));
        Assert.Equal("PFLAA,0,1000,-500,150,2,F0ABCD,90,0,57,-1.0,8", Body(Nmea.Pflaa(t)));
    }

    // Covers: RB-SIM-ESP-070
    [Fact]
    public void Rb02ScheduleRates()
    {
        var gen = new DeviceLineGenerator(DeviceProfiles.Resolve("rb02", null));
        var s = Frames.Mapped(now: 100);
        s.Sim.Paused = true; // freeze keeps the frame fresh for the whole second
        var counts = new Dictionary<string, int>();
        for (int ms = 0; ms < 1000; ms += 10)
            foreach (var line in gen.Due(s, 100 + ms / 1000.0))
            {
                string id = line[1..6];
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        Assert.Equal(20, counts["RBATT"]);
        Assert.Equal(10, counts["RBADC"]);
        Assert.Equal(5, counts["GPRMC"]);
        Assert.Equal(5, counts["GPGGA"]);
        Assert.False(counts.ContainsKey("PFLAU"));
    }

    [Fact]
    public void NmeaFlarmTickOrderIsGpsThenPflaaThenPflauAsRoastbeef()
    {
        var gen = new DeviceLineGenerator(DeviceProfiles.Resolve("nmea-flarm", null));
        var s = WithTraffic((1000, 0, 914.4, 1), (2000, 0, 914.4, 2));
        var ids = gen.Due(s, 100.1).Select(l => l[1..6]).ToList();
        Assert.Equal(["GPRMC", "GPGGA", "PFLAA", "PFLAA", "PFLAU"], ids);
    }

    [Fact]
    public void ScheduleSkipsAheadInsteadOfBursting()
    {
        var gen = new DeviceLineGenerator(new Dictionary<Sentence, double> { [Sentence.Rbatt] = 20 });
        var s = Frames.Mapped(now: 100);
        Assert.Single(gen.Due(s, 100.0));
        Assert.Single(gen.Due(s, 105.0)); // 5 s gap: one line, not 100
        Assert.Empty(gen.Due(s, 105.01));
    }

    [Fact]
    public void ProfileOverridesAndErrors()
    {
        var r = DeviceProfiles.Resolve("rb07", new Dictionary<string, double> { ["rbatt"] = 0, ["GPRMC"] = 2 });
        Assert.False(r.ContainsKey(Sentence.Rbatt));
        Assert.Equal(2, r[Sentence.Gprmc]);
        Assert.Throws<ArgumentException>(() => DeviceProfiles.Resolve("rb99", null));
        Assert.Throws<ArgumentException>(() => DeviceProfiles.Resolve("rb02", new Dictionary<string, double> { ["xyz"] = 1 }));
    }

    // Covers: RB-SIM-ESP-010
    // Example lines from roastbeef (flarm-nmea.go, ems.go via protocol §4.4): our checksum agrees with RB-01's.
    [Theory]
    [InlineData("PFLAA,0,-10687,-22561,-10283,1,A4F2EE,136,0,269,0.0,0", "4E")]
    [InlineData("RBEMS,2350,24,92,4,0,12,0,45,44", "58")]
    [InlineData("RBCYL,165,172,160,168,690,712,705,698", "46")]
    public void ChecksumMatchesRoastbeefExamples(string body, string sum) =>
        Assert.Equal($"${body}*{sum}\r\n", Nmea.Line(body));

    [Fact]
    public void PflaaAppendsSanitisedCallsignAndWrapsTrack()
    {
        var s = WithTraffic((1000, 0, 914.4, 0xF0ABCD));
        s.Traffic[0].Callsign = "x-ab,cd*123";
        s.Traffic[0].TrackTrueDeg = 359.7;
        s.Traffic[0].VsMps = 50;
        var t = Nmea.FlarmTargets(s, 100.1).Single();
        Assert.Equal("PFLAA,0,1000,0,0,2,F0ABCD!X-ABCD1,359,0,57,32.7,8", Body(Nmea.Pflaa(t)));
        Assert.Equal("", Nmea.Tail("  "));
    }

    // Covers: RB-SIM-ESP-070
    [Fact]
    public void RbemsAndRbcylInRoastbeefFieldOrder()
    {
        var s = Frames.Mapped(now: 100);
        // rpm, MAP inHg, oil °C, oil bar, coolant (unknown → empty), OAT, fuel pressure bar, fuel L, L
        Assert.Equal("RBEMS,2350,24,92,4,,12,0,45,44", Body(Nmea.Rbems(s, 100.1, new EmsUnits())!));
        Assert.Equal("RBCYL,165,165,165,165,700,700,700,700", Body(Nmea.Rbcyl(s, 100.1)!));
        Assert.Null(Nmea.Rbems(s, 103.5, new EmsUnits()));
        Assert.Null(Nmea.Rbcyl(s, 103.5));
    }

    [Fact]
    public void Rb04ScheduleRates()
    {
        var gen = new DeviceLineGenerator(DeviceProfiles.Resolve("rb04", null));
        var s = Frames.Mapped(now: 100);
        s.Sim.Paused = true;
        var ids = new List<string>();
        for (int ms = 0; ms < 4000; ms += 10) ids.AddRange(gen.Due(s, 100 + ms / 1000.0).Select(l => l[1..6]));
        Assert.Equal(20, ids.Count(i => i == "RBEMS"));
        Assert.Equal(2, ids.Count(i => i == "RBCYL"));
    }
}
