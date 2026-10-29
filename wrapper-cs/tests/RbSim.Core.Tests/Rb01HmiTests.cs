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
// Rb01HmiTests.cs: RB-01 HMI socket frames: daemon shapes, invalid markers, staleness, clock.

using System.Text.Json.Nodes;
using RbSim.Core;

namespace RbSim.Core.Tests;

public class Rb01HmiTests
{
    static JsonObject State(SimState s, double now) => RbSimIf.State(s, 7, now, new EmsUnits(), "test", includeSim: false);

    // As a client reads it: the JSON number, whether the frame built it from an int or a double
    static double D(JsonNode? n) => double.Parse(n!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    static readonly string[] AhrsAngles =
        ["AHRSPitch", "AHRSRoll", "AHRSGyroHeading", "AHRSMagHeading", "AHRSSlipSkid", "AHRSTurnRate", "AHRSGLoad"];

    // Covers: RB-SIM-HMI-040
    [Fact]
    public void FreshSituationIsTheDaemonsShapeInRbUnits()
    {
        var f = new Rb01Hmi().Situation(State(Frames.Mapped(now: 100), 100.1), 100.1);
        Assert.Equal(43.18528, D(f["GPSLatitude"]), 5);
        Assert.Equal(3000.0, D(f["GPSAltitudeMSL"]), 0);
        Assert.Equal(100.0, D(f["GPSGroundSpeed"]), 0);
        Assert.Equal(1, (int)f["GPSFixQuality"]!);
        Assert.Equal(8, (int)f["GPSSatellites"]!);
        Assert.True(D(f["GPSHorizontalAccuracy"]) < 19999); // the radar's "valid GPS" test
        Assert.Equal(2.1, D(f["AHRSPitch"]), 1);
        Assert.Equal(-15.3, D(f["AHRSRoll"]), 1);
        Assert.Equal(2980.0, D(f["BaroPressureAltitude"]), 0);
        Assert.Equal(1, (int)f["BaroSourceType"]!);
        Assert.Equal(98.4, D(f["IndicatedAirSpeed"]), 1);
        Assert.Equal(7, (int)f["AHRSStatus"]!);
        Assert.Equal(10 * 3600 + 15 * 60 + 30, D(f["GPSLastFixSinceMidnightUTC"]), 0);
        Assert.StartsWith("2026-10-26T10:15:30", (string)f["GPSTime"]!);
        // every field of the HMI's own initial window.situation is present
        foreach (var key in new[] { "GPSSatellitesTracked", "GPSNACp", "GPSVerticalAccuracy", "GPSLastFixLocalTime",
                     "GPSTurnRate", "GPSLastGroundTrackTime", "GPSLastValidNMEAMessage", "GPSPositionSampleRate",
                     "BaroTemperature", "BaroVerticalSpeed", "AHRSGLoadMin", "AHRSGLoadMax", "AHRSLastAttitudeTime" })
            Assert.True(f.ContainsKey(key), key);
    }

    // Covers: RB-SIM-SAF-050, RB-SIM-HMI-040
    [Fact]
    public void StaleGroupsCarryTheDaemonsInvalidMarkersNeverTheLastValue()
    {
        var f = new Rb01Hmi().Situation(State(Frames.Mapped(now: 100), 103), 103); // every group past its timeout
        foreach (var a in AhrsAngles) Assert.Equal(Rb01Hmi.AhrsInvalid, D(f[a]));
        Assert.Equal(Rb01Hmi.AhrsInvalid, D(f["AHRSGLoadMin"]));
        Assert.Equal(0, D(f["AHRSGLoadMax"]));
        Assert.Equal(0, (int)f["GPSFixQuality"]!);
        Assert.Equal(0, (int)f["GPSSatellites"]!);
        Assert.Equal(Rb01Hmi.NoFixAccuracyM, D(f["GPSHorizontalAccuracy"]));
        Assert.Equal(0, (int)f["BaroSourceType"]!);
        Assert.Equal(0, D(f["IndicatedAirSpeed"]));
        Assert.Equal(0, (int)f["AHRSStatus"]!);
        // The tapes read these without a validity check: they must not hold the last value.
        foreach (var k in new[] { "GPSAltitudeMSL", "GPSGroundSpeed", "GPSTrueCourse", "GPSLatitude", "GPSLongitude",
                     "BaroPressureAltitude", "BaroVerticalSpeed", "GPSVerticalSpeed" })
            Assert.Equal(0, D(f[k]));
        Assert.Equal(Rb01Hmi.ZeroTime, (string)f["GPSTime"]!);
    }

    // Covers: RB-SIM-SAF-050
    [Fact]
    public void AttitudeGoesInvalidAloneAfterItsOwnTimeout()
    {
        var f = new Rb01Hmi().Situation(State(Frames.Mapped(now: 100), 100.6), 100.6); // att 0.5 s passed, air 1 s not
        Assert.Equal(Rb01Hmi.AhrsInvalid, D(f["AHRSPitch"]));
        Assert.Equal(1, (int)f["GPSFixQuality"]!);
        Assert.Equal(2980.0, D(f["BaroPressureAltitude"]), 0);
        Assert.Equal(1 | 4, (int)f["AHRSStatus"]!);
    }

    // Covers: RB-SIM-SAF-050
    [Fact]
    public void DisconnectedIsInvalidEvenWhenPaused()
    {
        var s = Frames.Mapped(now: 100);
        s.Sim.Connected = false;
        s.Sim.Paused = true;
        var f = new Rb01Hmi().Situation(State(s, 100.01), 100.01);
        Assert.Equal(Rb01Hmi.AhrsInvalid, D(f["AHRSRoll"]));
        Assert.Equal(0, (int)f["GPSFixQuality"]!);
        Assert.Equal(0, D(f["GPSAltitudeMSL"]));
    }

    // Covers: RB-SIM-MOD-040
    [Fact]
    public void PausedFreezesTheSituationAsValid()
    {
        var s = Frames.Mapped(now: 100);
        s.Sim.Paused = true;
        var f = new Rb01Hmi().Situation(State(s, 160), 160);
        Assert.Equal(2.1, D(f["AHRSPitch"]), 1);
        Assert.Equal(1, (int)f["GPSFixQuality"]!);
    }

    [Fact]
    public void MissingAhrsFieldIsInvalidNotZero()
    {
        var raw = Frames.Cruise();
        raw[(int)U.GForce] = double.NaN;
        var f = new Rb01Hmi().Situation(State(Frames.Mapped(now: 100, raw: raw), 100.1), 100.1);
        Assert.Equal(Rb01Hmi.AhrsInvalid, D(f["AHRSGLoad"]));
        Assert.Equal(2.1, D(f["AHRSPitch"]), 1);
    }

    [Fact]
    public void GMeterTracksMinMaxAndResetsWhenTheAttitudeIsLost()
    {
        var hmi = new Rb01Hmi();
        var s = Frames.Mapped(now: 100); // 1.03 g
        hmi.Situation(State(s, 100.1), 100.1);
        var raw = Frames.Cruise();
        raw[(int)U.GForce] = 1.8;
        var f = hmi.Situation(State(Frames.Mapped(now: 101, raw: raw), 101.1), 101.1);
        Assert.Equal(1.03, D(f["AHRSGLoadMin"]), 2);
        Assert.Equal(1.8, D(f["AHRSGLoadMax"]), 2);

        hmi.Situation(State(s, 105), 105); // attitude lost
        f = hmi.Situation(State(Frames.Mapped(now: 106), 106.1), 106.1);
        Assert.Equal(1.03, D(f["AHRSGLoadMin"]), 2);
        Assert.Equal(1.03, D(f["AHRSGLoadMax"]), 2);
    }

    // The radar plate trusts the baro when GPSLastGPSTimeStratuxTime - BaroLastMeasurementTime < 1 s,
    // the autopilot plate the AHRS the same way against AHRSLastAttitudeTime.
    // Covers: RB-SIM-SAF-050
    [Fact]
    public void ClockFieldsMakeThePlatesFreshnessTestsTrue()
    {
        static double Ms(JsonNode? t) => DateTime.Parse((string)t!, null, System.Globalization.DateTimeStyles.AdjustToUniversal).Ticks / 1e4;
        var hmi = new Rb01Hmi();
        var fresh = hmi.Situation(State(Frames.Mapped(now: 100), 100.1), 100.1);
        Assert.True(Ms(fresh["GPSLastGPSTimeStratuxTime"]) - Ms(fresh["BaroLastMeasurementTime"]) < 1000);
        Assert.True(Ms(fresh["GPSLastGPSTimeStratuxTime"]) - Ms(fresh["AHRSLastAttitudeTime"]) < 1000);

        var stale = hmi.Situation(State(Frames.Mapped(now: 100), 103), 103);
        Assert.False(Ms(stale["GPSLastGPSTimeStratuxTime"]) - Ms(stale["BaroLastMeasurementTime"]) < 1000);
        Assert.False(Ms(stale["GPSLastGPSTimeStratuxTime"]) - Ms(stale["AHRSLastAttitudeTime"]) < 1000);
        Assert.Equal(Rb01Hmi.ZeroTime, (string)stale["BaroLastMeasurementTime"]!);
        Assert.Equal(Rb01Hmi.Clock(103), (string)stale["GPSLastGPSTimeStratuxTime"]!);
    }

    // Covers: RB-SIM-HMI-040
    [Fact]
    public void TrafficFramesAreTrafficInfoWithAgeAndBearing()
    {
        var s = Frames.Mapped(now: 100);
        s.Traffic =
        [
            new TrafficTarget { Id24 = 0xF00001, Callsign = "AI0001", LatDeg = 43.20528, LonDeg = 12.136674, AltMslM = 1000, GsMps = 51.4, TrackTrueDeg = 90, TMono = 99.5 },
            new TrafficTarget { Id24 = 0xF00002, LatDeg = 43.3, LonDeg = 12.2, TMono = 85 }, // 16 s old
        ];
        var frames = Rb01Hmi.Traffic(s, 101, new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc)).ToList();
        var t = Assert.Single(frames);
        Assert.Equal(0xF00001, (int)t["Icao_addr"]!);
        Assert.True((bool)t["Position_valid"]!);
        Assert.True((bool)t["Speed_valid"]!);
        Assert.Equal(100, (int)D(t["Speed"]));
        Assert.Equal(3281, (int)D(t["Alt"]));
        Assert.Equal(1.5, D(t["Age"]), 1);
        Assert.True((bool)t["BearingDist_valid"]!);
        Assert.Equal(0, D(t["Bearing"]), 0); // due north
        Assert.InRange(D(t["Distance"]), 2215, 2235); // 0.02° of latitude
        Assert.Equal("41", $"{(int)t["Last_source"]!}{(int)t["TargetType"]!}"); // the HMI's colour key

        s.Sim.Connected = false;
        Assert.Empty(Rb01Hmi.Traffic(s, 101, DateTime.UtcNow));
    }

    [Fact]
    public void TrafficWithoutOwnshipPositionHasNoBearing()
    {
        var s = Frames.Mapped(now: 100);
        s.Traffic = [new TrafficTarget { Id24 = 1, LatDeg = 43.2, LonDeg = 12.1, TMono = 104 }];
        var t = Assert.Single(Rb01Hmi.Traffic(s, 104.5, DateTime.UtcNow)); // ownship 4.5 s old
        Assert.False((bool)t["BearingDist_valid"]!);
        Assert.False((bool)t["Speed_valid"]!);
        Assert.Equal(0, D(t["Alt"]));
    }

    // Covers: RB-SIM-SAF-050, RB-SIM-HMI-040
    [Fact]
    public void EmsFrameHasEveryKeyAndZerosWhenStale()
    {
        var fresh = Rb01Hmi.Ems(State(Frames.Mapped(now: 100), 100.1));
        Assert.Equal(Rb01Hmi.EmsKeys.Count, fresh.Count);
        Assert.Equal(2350, D(fresh["enginerpm"]), 0);

        var stale = Rb01Hmi.Ems(State(Frames.Mapped(now: 100), 104));
        Assert.Equal(Rb01Hmi.EmsKeys.Count, stale.Count);
        foreach (var k in Rb01Hmi.EmsKeys) Assert.Equal(0, D(stale[k]));
    }

    // Covers: RB-SIM-HMI-040
    [Fact]
    public void StatusIsSimLabelledAndFollowsTheGroups()
    {
        var fresh = Rb01Hmi.Status(State(Frames.Mapped(now: 100), 100.1), "rbsim-msfs 0.1.0", 100.1);
        Assert.Equal("rbsim-msfs 0.1.0-SIM", (string)fresh["Version"]!);
        Assert.True((bool)fresh["GPS_connected"]!);
        Assert.Equal(8, (int)fresh["GPS_satellites_locked"]!);
        Assert.False(fresh.ContainsKey("CPUTemp")); // no such sensor: the HMI shows ---
        Assert.Equal(Rb01Hmi.Clock(100.1), (string)fresh["UptimeClock"]!);

        var stale = Rb01Hmi.Status(State(Frames.Mapped(now: 100), 103), "rbsim-msfs 0.1.0", 103);
        Assert.False((bool)stale["GPS_connected"]!);
        Assert.Equal(0, (int)stale["GPS_satellites_locked"]!);
        Assert.False((bool)stale["IMUConnected"]!);
    }

    [Fact]
    public void ClockCountsFromYearOne()
    {
        Assert.Equal("0001-01-01T00:00:00.000Z", Rb01Hmi.ZeroTime);
        Assert.Equal("0001-01-01T01:00:01.500Z", Rb01Hmi.Clock(3601.5));
    }
}
