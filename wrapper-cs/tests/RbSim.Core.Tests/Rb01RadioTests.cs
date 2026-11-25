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
// Rb01RadioTests.cs: the RB-01 radio plate on the sim's COM radios: the radio list, staleness, and the plate's posts as commands.

using System.Text.Json.Nodes;
using RbSim.Core;

namespace RbSim.Core.Tests;

public class Rb01RadioTests
{
    static SimState Radios(double now = 100)
    {
        var s = new SimState();
        s.Sim.Connected = true;
        s.Radio = new RadioGroup
        {
            Valid = true,
            TMono = now,
            Com1ActiveHz = 118_100_000,
            Com1StandbyHz = 121_500_000,
            Com2ActiveHz = 122_800_000,
            Com2StandbyHz = 119_000_000,
            Com1Transmit = true,
            Com2Transmit = false,
        };
        return s;
    }

    static JsonObject Post(string active, string standby) => new()
    {
        // what radioApply() in the plate sends: the radio whole, Enabled always true
        ["Enabled"] = true,
        ["FrequencyActive"] = active,
        ["FrequencyStandby"] = standby,
        ["LabelActive"] = "Perugia Approach",
        ["LabelStandby"] = "---",
        ["Dual"] = true,
    };

    // Covers: RB-SIM-HMI-070
    [Fact]
    public void ListIsTheDaemonsRadioStatusForCom1AndCom2()
    {
        var list = Rb01Radio.List(Radios(), 100.5);
        Assert.Equal(2, list.Count);
        var com1 = list[0]!.AsObject();
        Assert.Equal("COM1", (string?)com1["Name"]);
        Assert.Equal("118.100", (string?)com1["FrequencyActive"]);
        Assert.Equal("121.500", (string?)com1["FrequencyStandby"]);
        Assert.True((bool)com1["Enabled"]!);
        Assert.False((bool)com1["Dual"]!);
        var com2 = list[1]!.AsObject();
        Assert.Equal("COM2", (string?)com2["Name"]);
        Assert.Equal("122.800", (string?)com2["FrequencyActive"]);
        Assert.False((bool)com2["Enabled"]!);
        // every field of roastbeef's RadioStatus as encoding/json writes it
        string[] fields = ["Name", "SquelchLevel", "VolumeLevel", "FrequencyActive", "FrequencyStandby", "Enabled", "Dual", "Driver", "Path", "LabelActive", "LabelStandby"];
        Assert.Equal(fields, com1.Select(kv => kv.Key));
    }

    // Covers: RB-SIM-HMI-070, RB-SIM-SAF-050
    [Fact]
    public void StaleOrMissingFrequenciesAreShownAsNoneNeverTheLastValue()
    {
        static void None(JsonArray list)
        {
            foreach (var r in list)
            {
                Assert.Equal(Rb01Radio.NoFrequency, (string?)r!["FrequencyActive"]);
                Assert.Equal(Rb01Radio.NoFrequency, (string?)r["FrequencyStandby"]);
                Assert.False((bool)r["Enabled"]!);
            }
        }
        None(Rb01Radio.List(Radios(now: 100), 100 + Staleness.PosS + 0.1));
        var gone = Radios();
        gone.Sim.Connected = false;
        None(Rb01Radio.List(gone, 100.1));
        var invalid = Radios();
        invalid.Radio.Valid = false;
        None(Rb01Radio.List(invalid, 100.1));

        // a pause freezes the values (RB-SIM-MOD-040)
        var paused = Radios();
        paused.Sim.Paused = true;
        Assert.Equal("118.100", (string?)Rb01Radio.List(paused, 200)[0]!["FrequencyActive"]);

        // one rejected datum blanks only its own frequency (RB-SIM-MSF-130)
        var partial = Radios();
        partial.Radio.Com2StandbyHz = null;
        var list = Rb01Radio.List(partial, 100.1);
        Assert.Equal("122.800", (string?)list[1]!["FrequencyActive"]);
        Assert.Equal(Rb01Radio.NoFrequency, (string?)list[1]!["FrequencyStandby"]);
    }

    // Covers: RB-SIM-HMI-070
    [Theory]
    [InlineData("121.500", 121_500_000)]
    [InlineData("121.5", 121_500_000)]
    [InlineData(" 118.025 ", 118_025_000)]
    [InlineData("118.010", 118_010_000)]  // an 8.33 kHz channel name, as the keypad builds it
    [InlineData("136.990", 136_990_000)]
    [InlineData("140.000", 140_000_000)] // the actuator, not the parser, checks the range
    public void FrequenciesParseAsThePlateWritesThem(string text, double hz) => Assert.Equal(hz, Rb01Radio.ParseMhz(text));

    // Covers: RB-SIM-HMI-070
    [Theory]
    [InlineData("1--.---")]  // the keypad half typed
    [InlineData("118.-50")]  // the plate's ▼ from 118.000 (khz -5, padded)
    [InlineData("---.---")]
    [InlineData("")]
    [InlineData("121,500")]
    [InlineData("121")]
    [InlineData("121.5000")]
    [InlineData("12.500")]
    [InlineData("١٢١.٥٠٠")] // digits other than 0-9
    public void AnythingElseIsNoFrequency(string text) => Assert.Null(Rb01Radio.ParseMhz(text));

    // Covers: RB-SIM-HMI-070
    [Fact]
    public void APostWritesOnlyTheFrequencyThePlateChanged()
    {
        Assert.Null(Rb01Radio.Commands(0, Post("118.100", "122.800"), Radios(), out var cmd));
        Assert.Equal("{\"com1_standby_hz\":122800000}", cmd.ToJsonString());

        // the plate's own post of what it shows writes nothing; Enabled, Dual and the labels never do
        Assert.Null(Rb01Radio.Commands(0, Post("118.100", "121.500"), Radios(), out cmd));
        Assert.Empty(cmd);

        // a read-back within the actuator's 1 kHz tolerance is the same frequency
        var s = Radios();
        s.Radio.Com1StandbyHz = 118_008_333; // shown as "118.008"
        Assert.Null(Rb01Radio.Commands(0, Post("118.100", "118.008"), s, out cmd));
        Assert.Empty(cmd);
    }

    // Covers: RB-SIM-HMI-070
    [Fact]
    public void ThePlatesSwapIsTwoSetsOnTheRadioItNames()
    {
        Assert.Null(Rb01Radio.Commands(1, Post("119.000", "122.800"), Radios(), out var cmd));
        Assert.Equal(119_000_000, (double)cmd["com2_active_hz"]!);
        Assert.Equal(122_800_000, (double)cmd["com2_standby_hz"]!);
        Assert.Equal(2, cmd.Count);
    }

    // Covers: RB-SIM-HMI-070
    [Fact]
    public void ABadFrequencyRefusesThePostWhole()
    {
        Assert.NotNull(Rb01Radio.Commands(0, Post("119.000", "118.-50"), Radios(), out var cmd));
        Assert.Empty(cmd);
        Assert.NotNull(Rb01Radio.Commands(0, new JsonObject { ["FrequencyActive"] = 119.0 }, Radios(), out cmd));
        Assert.NotNull(Rb01Radio.Commands(0, new JsonArray(), Radios(), out cmd));
        Assert.NotNull(Rb01Radio.Commands(0, null, Radios(), out cmd));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rb01Radio.Commands(Rb01Radio.Count, Post("118.100", "121.500"), Radios(), out _));
    }

    // Covers: RB-SIM-HMI-070, RB-SIM-MSF-100
    [Fact]
    public void TheCommandsGoThroughTheControlRules()
    {
        var s = Radios();
        Assert.Null(Rb01Radio.Commands(0, Post("118.100", "124.350"), s, out var cmd));
        var radio = new ActuatorManager([ActuatorGroup.Radio]);
        Assert.Empty(radio.Parse(cmd, s, out var commands));
        var (a, v) = Assert.Single(commands);
        Assert.Equal(("COM_STBY_RADIO_SET_HZ", 124_350_000u), a.Write(v));

        // the actuator's range and the configured groups still apply
        Assert.Null(Rb01Radio.Commands(0, Post("118.100", "140.000"), s, out cmd));
        Assert.NotEmpty(radio.Parse(cmd, s, out _));
        Assert.Null(Rb01Radio.Commands(0, Post("118.100", "124.350"), s, out cmd));
        Assert.NotEmpty(new ActuatorManager([ActuatorGroup.Autopilot]).Parse(cmd, s, out _));
    }

    // Covers: RB-SIM-HMI-070
    [Fact]
    public void ACommandIsSettledOnceConfirmedOrReported()
    {
        var s = Radios();
        var m = new ActuatorManager([ActuatorGroup.Radio]);
        Assert.Null(Rb01Radio.Commands(0, Post("118.100", "124.350"), s, out var cmd));
        Assert.Empty(m.Parse(cmd, s, out var commands));
        string[] names = ["com1_standby_hz"];
        Assert.True(m.Settled(names)); // nothing submitted yet
        m.Submit(commands, 100);
        Assert.False(m.Settled(names));
        Assert.Single(m.Due(s, 100)); // sent
        Assert.False(m.Settled(names));
        s.Radio = new RadioGroup { Valid = true, TMono = 100.1, Com1ActiveHz = 118_100_000, Com1StandbyHz = 124_350_000 };
        Assert.Empty(m.Due(s, 100.1)); // confirmed by read-back
        Assert.True(m.Settled(names));
    }
}
