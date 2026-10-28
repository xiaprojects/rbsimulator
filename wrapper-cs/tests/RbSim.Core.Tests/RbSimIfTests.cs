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
// RbSimIfTests.cs: RB-SIM-IF/1 message shape, units and staleness tests.

using System.Text.Json.Nodes;
using RbSim.Core;

namespace RbSim.Core.Tests;

public class RbSimIfTests
{
    static JsonObject State(SimState s, double now) => RbSimIf.State(s, 7, now, new EmsUnits(), "test", includeSim: true);

    // Covers: RB-SIM-IF-020, RB-SIM-IF-030
    [Fact]
    public void FreshStateCarriesAllRequiredGroupsInRbUnits()
    {
        var m = State(Frames.Mapped(now: 100), 100.1);
        Assert.Equal(1, (int)m["v"]!);
        Assert.Equal("msfs", (string)m["src"]!);
        Assert.Equal(7, (long)m["seq"]!);
        Assert.False((bool)m["paused"]!);

        var gps = m["gps"]!.AsObject();
        Assert.Equal("2026-10-26T10:15:30.00Z", (string)gps["GPSTime"]!);
        Assert.Equal(3000.0, (double)gps["GPSAltitudeMSL"]!, 1);   // ft
        Assert.Equal(100.0, (double)gps["GPSGroundSpeed"]!, 0);    // kt
        Assert.Equal(-4.2, (double)gps["GPSVerticalSpeed"]!, 1);   // ft/s

        var ahrs = m["ahrs"]!.AsObject();
        Assert.Equal(2.1, (double)ahrs["AHRSPitch"]!, 2);
        Assert.Equal(-15.3, (double)ahrs["AHRSRoll"]!, 2);
        Assert.Equal(271.4, (double)ahrs["AHRSGyroHeading"]!, 1);
        Assert.Equal(268.6, (double)ahrs["AHRSMagHeading"]!, 1);

        Assert.Equal(-250.0, (double)m["baro"]!["BaroVerticalSpeed"]!, 0); // ft/min
        Assert.Equal(98.4, (double)m["airspeed"]!["IndicatedAirSpeed"]!, 1);

        var ems = m["ems"]!.AsObject();
        Assert.Equal(24.0, (double)ems["manifoldpressure"]!, 1);   // inHg
        Assert.Equal(4.0, (double)ems["oilpressure"]!, 1);         // bar
        Assert.Equal(45.4, (double)ems["fuel1"]!, 1);              // L
        Assert.Equal(12.0, (double)ems["outsidetemperature"]!, 0);

        Assert.Equal(3500.0, (double)m["ap"]!["alt_sel_ft"]!, 0);
        Assert.Equal("test", (string)m["sim"]!["wrapper"]!);
    }

    // Covers: RB-SIM-IF-040
    [Fact]
    public void OnlyKnownKeysAreSent()
    {
        var m = State(Frames.Mapped(now: 100), 100.1);
        string[] top = ["v", "src", "seq", "paused", "gps", "ahrs", "baro", "airspeed", "ems", "ap", "sim"];
        foreach (var (key, value) in m)
        {
            Assert.Contains(key, top);
            if (value is JsonObject group)
                foreach (var (field, _) in group)
                    Assert.Contains(field, RbSimIf.StateGroupKeys[key]);
        }
    }

    // Covers: RB-SIM-MOD-020, RB-SIM-IF-030
    [Fact]
    public void StaleGroupsAreNullNeverTheLastValue()
    {
        var s = Frames.Mapped(now: 100);
        var m = State(s, 100.6); // att 0.5 s timeout passed, air 1 s not
        Assert.True(m.ContainsKey("ahrs"));
        Assert.Null(m["ahrs"]);
        Assert.NotNull(m["baro"]);
        Assert.NotNull(m["gps"]);

        m = State(s, 102.5);
        Assert.Null(m["gps"]);
        Assert.Null(m["baro"]);
        Assert.Null(m["airspeed"]);
        Assert.NotNull(m["ems"]); // 3 s
    }

    // Covers: RB-SIM-MOD-040
    [Fact]
    public void PausedFreezesValuesAsValid()
    {
        var s = Frames.Mapped(now: 100);
        s.Sim.Paused = true;
        var m = State(s, 160);
        Assert.True((bool)m["paused"]!);
        Assert.NotNull(m["ahrs"]);
        Assert.NotNull(m["gps"]);
    }

    // Covers: RB-SIM-MOD-040
    [Fact]
    public void DisconnectedInvalidatesEverythingEvenWhenPaused()
    {
        var s = Frames.Mapped(now: 100);
        s.Sim.Connected = false;
        s.Sim.Paused = true;
        var m = State(s, 100.01);
        Assert.False((bool)m["paused"]!);
        foreach (var g in new[] { "gps", "ahrs", "baro", "airspeed" })
        {
            Assert.True(m.ContainsKey(g));
            Assert.Null(m[g]);
        }
        Assert.False(m.ContainsKey("ems"));
    }

    [Fact]
    public void MissingFieldIsOmittedNotZero()
    {
        var raw = Frames.Cruise();
        raw[(int)U.GForce] = double.NaN;
        var m = State(Frames.Mapped(now: 100, raw: raw), 100.1);
        Assert.False(m["ahrs"]!.AsObject().ContainsKey("AHRSGLoad"));
    }

    [Fact]
    public void EmsUnitsAreConfigurable()
    {
        var u = new EmsUnits { OilPressure = PressureUnit.Psi, Fuel = VolumeUnit.UsGallons };
        var m = RbSimIf.State(Frames.Mapped(now: 100), 1, 100.1, u, null, false);
        Assert.Equal(58.0, (double)m["ems"]!["oilpressure"]!, 1);
        Assert.Equal(12.0, (double)m["ems"]!["fuel1"]!, 1);
        Assert.False(m.ContainsKey("sim"));
    }

    [Fact]
    public void TrafficIsNearestFirstFreshAndCapped()
    {
        var s = Frames.Mapped(now: 100);
        var targets = new List<TrafficTarget>();
        for (int i = 0; i < 60; i++)
            targets.Add(new TrafficTarget { Id24 = i, Callsign = $"T{i}", LatDeg = 43.18528 + (60 - i) * 0.001, LonDeg = 12.136674, AltMslM = 1000, TMono = 100 });
        targets.Add(new TrafficTarget { Id24 = 999, LatDeg = 43.18528, LonDeg = 12.136674, TMono = 80 }); // 20 s old
        s.Traffic = targets;

        var m = RbSimIf.Traffic(s, 101);
        var arr = m["targets"]!.AsArray();
        Assert.Equal(RbSimIf.MaxTrafficTargets, arr.Count);
        Assert.Equal(59, (int)arr[0]!["Icao_addr"]!);
        Assert.DoesNotContain(arr, t => (int)t!["Icao_addr"]! == 999);
        Assert.Equal(3281.0, (double)arr[0]!["Alt"]!, 0);
    }
}
