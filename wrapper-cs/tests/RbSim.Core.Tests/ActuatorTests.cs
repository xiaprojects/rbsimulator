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
// ActuatorTests.cs: radio read-back mapping, key-event encoding and the write/read-back state machine.

using System.Text.Json.Nodes;
using RbSim.Core;

namespace RbSim.Core.Tests;

public class SquawkTests
{
    [Theory]
    [InlineData(7000, 0x7000u)]
    [InlineData(1200, 0x1200u)]
    [InlineData(7777, 0x7777u)]
    [InlineData(0, 0u)]
    [InlineData(17, 0x0017u)]
    public void Bco16RoundTrip(int code, uint bco)
    {
        Assert.Equal(bco, Squawk.ToBco16(code));
        Assert.Equal(code, Squawk.FromBco16(bco));
    }

    [Theory]
    [InlineData(7800)]
    [InlineData(8000)]
    [InlineData(-1)]
    [InlineData(10000)]
    public void InvalidCodes(int code)
    {
        Assert.False(Squawk.IsValid(code));
        Assert.Throws<ArgumentOutOfRangeException>(() => Squawk.ToBco16(code));
    }

    [Fact]
    public void ReadBackWithANonOctalNibbleIsInvalid()
    {
        Assert.Null(Squawk.FromBco16(0x7080));
        Assert.Null(Squawk.FromBco16(double.NaN));
    }
}

public class RadioMapTests
{
    // Covers: RB-SIM-MSF-130
    [Fact]
    public void RadiosAndControlsMap()
    {
        var raw = Frames.Cruise();
        raw[(int)U.Com1Active] = 118_100_000; raw[(int)U.Com1Standby] = 121_500_000;
        raw[(int)U.Nav1Active] = 110_500_000; raw[(int)U.Nav1Radial] = 365; raw[(int)U.Nav1HasNav] = 1;
        raw[(int)U.XpdrCode] = 0x7000; raw[(int)U.XpdrState] = 4; raw[(int)U.XpdrIdent] = 0;
        raw[(int)U.FlapsPct] = 25; raw[(int)U.ElevTrimPct] = -3; raw[(int)U.GearHandle] = 1; raw[(int)U.ParkingBrake] = 0;
        raw[(int)U.ApVsVar] = -500; raw[(int)U.ApAltLock] = 1;
        var s = Frames.Mapped(raw: raw);

        Assert.True(s.Radio.Valid);
        Assert.Equal(118_100_000, s.Radio.Com1ActiveHz);
        Assert.Equal(5, s.Radio.Nav1RadialDeg!.Value, 9);
        Assert.Equal(7000, s.Radio.XpdrCode);
        Assert.Equal(4, s.Radio.XpdrState);
        Assert.Equal(25, s.Controls.FlapsPct);
        Assert.True(s.Controls.GearDown);
        Assert.Equal(-500, Units.MpsToFpm(s.Ap.VsSelMps!.Value), 6);
        Assert.True(s.Ap.AltHold);

        var view = RadioView.Build(s);
        Assert.Equal("118.100", (string?)view["com1"]!["active_mhz"]);
        Assert.Equal("7000", (string?)view["xpdr"]!["code"]);
        Assert.Equal("alt", (string?)view["xpdr"]!["state"]);
    }

    // Covers: RB-SIM-MSF-130
    [Fact]
    public void RejectedRadioDatumsAreNullNotZero()
    {
        var raw = Frames.Cruise();
        raw[(int)U.Com1Active] = double.NaN;
        raw[(int)U.XpdrState] = double.NaN;
        var s = Frames.Mapped(raw: raw);
        Assert.False(s.Radio.Valid);
        Assert.Null(s.Radio.Com1ActiveHz);
        Assert.Null(s.Radio.XpdrState);
        Assert.Null(RadioView.Build(s)["xpdr"]!["state"]);
    }

    [Fact]
    public void DatumTableMatchesTheEnum() => Assert.Equal(Enum.GetValues<U>().Length, MsfsDatums.User.Length);
}

public class ActuatorEncodingTests
{
    static Actuator A(string n) => Actuators.ByName[n];

    // Covers: RB-SIM-MSF-090
    [Fact]
    public void BooleansUseExplicitEventsNeverToggles()
    {
        Assert.Equal(("AUTOPILOT_ON", 0u), A("ap_master").Write(1));
        Assert.Equal(("AUTOPILOT_OFF", 0u), A("ap_master").Write(0));
        Assert.Equal(("AP_HDG_HOLD_OFF", 0u), A("hdg_mode").Write(0));
        Assert.Equal(("PARKING_BRAKE_SET", 1u), A("parking_brake").Write(1));
        string[] toggles = ["AP_MASTER", "AP_HDG_HOLD", "AP_PANEL_HEADING_HOLD", "GEAR_TOGGLE", "PARKING_BRAKES", "COM_STBY_RADIO_SWAP", "XPNDR_IDENT_TOGGLE"];
        Assert.Empty(Actuators.EventNames.Intersect(toggles));
    }

    [Fact]
    public void Encodings()
    {
        Assert.Equal(("COM_STBY_RADIO_SET_HZ", 121_500_000u), A("com1_standby_hz").Write(121_500_000));
        Assert.Equal(("XPNDR_SET", 0x7000u), A("xpdr_code").Write(7000));
        Assert.Equal(("HEADING_BUG_SET", 0u), A("hdg_bug_mag_deg").Write(360));
        Assert.Equal(("HEADING_BUG_SET", 271u), A("hdg_bug_mag_deg").Write(270.6));
        Assert.Equal(("AP_VS_VAR_SET_ENGLISH", unchecked((uint)-500)), A("vs_sel_fpm").Write(-500));
        Assert.Equal(("KOHLSMAN_SET", 16272u), A("qnh_hpa").Write(1017));
        Assert.Equal(("FLAPS_SET", 16383u), A("flaps_pct").Write(100));
        Assert.Equal(("ELEVATOR_TRIM_SET", unchecked((uint)-8192)), A("elevator_trim_pct").Write(-50));
    }

    [Fact]
    public void HeadingMatchWrapsAround()
    {
        Assert.True(A("hdg_bug_mag_deg").Matches(359.6, 0.2));
        Assert.False(A("hdg_bug_mag_deg").Matches(350, 0.2));
    }
}

public class ActuatorManagerTests
{
    static readonly ActuatorGroup[] AllGroups = Enum.GetValues<ActuatorGroup>();

    static SimState Cockpit()
    {
        var s = Frames.Mapped();
        s.Radio = new RadioGroup { Valid = true, Com1ActiveHz = 118_100_000, Com1StandbyHz = 121_500_000, XpdrCode = 7000 };
        s.Controls = new ControlsGroup { Valid = true, FlapsPct = 0, GearDown = true };
        return s;
    }

    static List<string> Parse(ActuatorManager m, string json, SimState s, out List<(Actuator Actuator, double Value)> cmds) =>
        m.Parse(JsonNode.Parse(json)!.AsObject(), s, out cmds);

    static string? Status(ActuatorManager m, SimState s, string name) =>
        (string?)m.View(s, 0)[name]!["command"]?["status"];

    // Covers: RB-SIM-MSF-100
    [Fact]
    public void WriteThenConfirmByReadBack()
    {
        var m = new ActuatorManager(AllGroups);
        var s = Cockpit();
        Assert.Empty(Parse(m, """{"com1_standby_hz": 122800000}""", s, out var cmds));
        m.Submit(cmds, 10);

        var w = Assert.Single(m.Due(s, 10));
        Assert.Equal("COM_STBY_RADIO_SET_HZ", w.Event);
        Assert.Equal(122_800_000u, w.Data);
        Assert.Empty(m.Due(s, 10.1)); // sent once, not repeated while waiting
        Assert.Equal("sent", Status(m, s, "com1_standby_hz"));

        s.Radio = new RadioGroup { Valid = true, Com1ActiveHz = 118_100_000, Com1StandbyHz = 122_800_000 };
        Assert.Empty(m.Due(s, 10.3));
        Assert.Equal("confirmed", Status(m, s, "com1_standby_hz"));
    }

    // Covers: RB-SIM-MSF-100
    [Fact]
    public void IgnoredWriteBecomesMismatchAfterTwoSecondsAndIsNotRetried()
    {
        var m = new ActuatorManager(AllGroups);
        var s = Cockpit();
        Parse(m, """{"flaps_pct": 30}""", s, out var cmds);
        m.Submit(cmds, 10);
        Assert.Single(m.Due(s, 10));
        Assert.Empty(m.Due(s, 11.9));
        Assert.Equal("sent", Status(m, s, "flaps_pct"));
        Assert.Empty(m.Due(s, 12.0));
        Assert.Equal("mismatch", Status(m, s, "flaps_pct"));
        Assert.Empty(m.Due(s, 20));
    }

    [Fact]
    public void NoWriteWhenTheReadBackAlreadyMatches()
    {
        var m = new ActuatorManager(AllGroups);
        var s = Cockpit();
        Parse(m, """{"gear_down": true, "xpdr_code": "7000"}""", s, out var cmds);
        m.Submit(cmds, 10);
        Assert.Empty(m.Due(s, 10));
        Assert.Equal("confirmed", Status(m, s, "gear_down"));
        Assert.Equal("already set", (string?)m.View(s, 10)["xpdr_code"]!["command"]!["detail"]);
    }

    [Fact]
    public void AtMostTwoWritesPerSecondPerActuator()
    {
        var m = new ActuatorManager(AllGroups);
        var s = Cockpit();
        Parse(m, """{"hdg_bug_mag_deg": 100}""", s, out var c1);
        m.Submit(c1, 10);
        Assert.Single(m.Due(s, 10));
        Parse(m, """{"hdg_bug_mag_deg": 110}""", s, out var c2);
        m.Submit(c2, 10.2);
        Assert.Empty(m.Due(s, 10.2));
        var w = Assert.Single(m.Due(s, 10.5));
        Assert.Equal(110u, w.Data);
    }

    [Fact]
    public void SwapIsTwoExplicitSetsFromTheReadBack()
    {
        var m = new ActuatorManager(AllGroups);
        var s = Cockpit();
        Assert.Empty(Parse(m, """{"com1_swap": true}""", s, out var cmds));
        Assert.Contains(cmds, c => c.Actuator.Name == "com1_active_hz" && c.Value == 121_500_000);
        Assert.Contains(cmds, c => c.Actuator.Name == "com1_standby_hz" && c.Value == 118_100_000);
        Assert.NotEmpty(Parse(m, """{"com2_swap": true}""", s, out _)); // no COM2 read-back
    }

    [Theory]
    [InlineData("""{"com1_standby_hz": 150000000}""", "outside")]
    [InlineData("""{"nav1_active_hz": 118000000}""", "outside")]
    [InlineData("""{"xpdr_code": "7800"}""", "octal")]
    [InlineData("""{"xpdr_code": 78.5}""", "octal")]
    [InlineData("""{"ap_master": "yes"}""", "true or false")]
    [InlineData("""{"xpdr_ident": false}""", "only true")]
    [InlineData("""{"com1_swap": false}""", "only true")]
    [InlineData("""{"hdg_bug_mag_deg": "270"}""", "number")]
    [InlineData("""{"throttle_pct": 100}""", "unknown control")]
    public void InvalidCommandsAreRejected(string json, string expect)
    {
        var m = new ActuatorManager(AllGroups);
        var errors = Parse(m, json, Cockpit(), out _);
        Assert.Contains(expect, Assert.Single(errors));
    }

    [Fact]
    public void DisabledGroupIsRefused()
    {
        var m = new ActuatorManager([ActuatorGroup.Radio]);
        var errors = Parse(m, """{"gear_down": false, "com1_standby_hz": 121500000}""", Cockpit(), out _);
        Assert.Contains("disabled", Assert.Single(errors));
    }

    [Fact]
    public void UnrecognisedEventMakesOnlyItsActuatorUnsupported()
    {
        var m = new ActuatorManager(AllGroups);
        var s = Cockpit();
        Parse(m, """{"parking_brake": true, "gear_down": false}""", s, out var cmds);
        m.Submit(cmds, 10);
        m.MarkUnsupported("PARKING_BRAKE_SET");
        var w = Assert.Single(m.Due(s, 10));
        Assert.Equal("GEAR_UP", w.Event);
        Assert.Equal("unsupported", Status(m, s, "parking_brake"));
        Assert.Contains("does not recognise", Assert.Single(Parse(m, """{"parking_brake": true}""", s, out _)));
        m.ResetSupport();
        Assert.Empty(Parse(m, """{"parking_brake": true}""", s, out _));
    }

    [Fact]
    public void DisconnectFailsOpenCommands()
    {
        var m = new ActuatorManager(AllGroups);
        var s = Cockpit();
        Parse(m, """{"com1_active_hz": 119000000}""", s, out var cmds);
        m.Submit(cmds, 10);
        s.Sim.Connected = false;
        Assert.Empty(m.Due(s, 10));
        Assert.Equal("failed", Status(m, s, "com1_active_hz"));
    }

    [Fact]
    public void InvalidReadBackGroupIsNotAMatch()
    {
        var m = new ActuatorManager(AllGroups);
        var s = Cockpit();
        s.Controls = new ControlsGroup { Valid = false, GearDown = true };
        Parse(m, """{"gear_down": true}""", s, out var cmds);
        m.Submit(cmds, 10);
        Assert.Single(m.Due(s, 10));
    }
}
