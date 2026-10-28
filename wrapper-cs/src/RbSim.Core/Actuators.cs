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
// Actuators.cs: the writable radio, autopilot and cockpit controls, their MSFS key events and read-back.

using System.Globalization;
using System.Text.Json.Nodes;

namespace RbSim.Core;

public enum ActuatorGroup { Radio, Autopilot, Altimeter, Controls }

public enum ValueKind { Hz, Bool, HeadingDeg, Ft, Fpm, Hpa, Pct, Squawk }

/// <summary>
/// One writable value. Explicit-state events only, never toggles (RB-SIM-MSF-090, D15):
/// a boolean has an on and an off event, or one set event taking 0/1. Every write is
/// confirmed by reading <see cref="ReadBack"/> back from the sim (RB-SIM-MSF-100).
/// </summary>
public sealed record Actuator(
    string Name,
    ActuatorGroup Group,
    ValueKind Kind,
    string Unit,
    double Min,
    double Max,
    double Tolerance,
    Func<SimState, Group> Source,
    Func<SimState, double?> ReadBack,
    string? SetEvent = null,
    string? OnEvent = null,
    string? OffEvent = null)
{
    public IEnumerable<string> Events => new[] { SetEvent, OnEvent, OffEvent }.OfType<string>();

    /// <summary>The key event and its DWORD parameter for <paramref name="value"/> (already validated).</summary>
    public (string Event, uint Data) Write(double value)
    {
        if (Kind == ValueKind.Bool && SetEvent is null)
            return (value > 0.5 ? OnEvent! : OffEvent!, 0);
        return (SetEvent!, Encode(value));
    }

    public uint Encode(double v) => Kind switch
    {
        ValueKind.Bool => v > 0.5 ? 1u : 0u,
        ValueKind.Hz => (uint)Math.Round(v),
        // HEADING_BUG_SET: integer degrees 0–359.
        ValueKind.HeadingDeg => (uint)Math.Round(Units.Wrap360(v)) % 360,
        ValueKind.Ft => (uint)Math.Round(v),
        // A negative vertical speed travels as its two's complement (verify, L1).
        ValueKind.Fpm => unchecked((uint)(int)Math.Round(v)),
        // KOHLSMAN_SET: millibars × 16.
        ValueKind.Hpa => (uint)Math.Round(v * 16),
        // FLAPS_SET 0…16383; ELEVATOR_TRIM_SET −16383…16383 (verify, L1).
        ValueKind.Pct => unchecked((uint)(int)Math.Round(v / 100.0 * 16383)),
        ValueKind.Squawk => Squawk.ToBco16((int)v),
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    /// <summary>Whether a read-back satisfies the desired value.</summary>
    public bool Matches(double desired, double? readBack)
    {
        if (readBack is not double rb) return false;
        double d = Kind == ValueKind.HeadingDeg
            ? Math.Abs(((desired - rb) % 360 + 540) % 360 - 180)
            : Math.Abs(desired - rb);
        return d <= Tolerance;
    }

    /// <summary>A boolean that only has an on event (transponder ident) accepts only true.</summary>
    public bool OnlyTrue => Kind == ValueKind.Bool && SetEvent is null && OffEvent is null;
}

/// <summary>Transponder codes: four octal digits, sent and read as BCO16 (one digit per nibble).</summary>
public static class Squawk
{
    public static bool IsValid(int code) =>
        code is >= 0 and <= 7777 && code.ToString(System.Globalization.CultureInfo.InvariantCulture).All(c => c <= '7');

    public static uint ToBco16(int code)
    {
        if (!IsValid(code)) throw new ArgumentOutOfRangeException(nameof(code), $"not a squawk: {code}");
        uint r = 0;
        for (int shift = 0; code > 0; shift += 4, code /= 10) r |= (uint)(code % 10) << shift;
        return r;
    }

    /// <summary>The code from a BCO16 read-back; null when missing or not four octal digits.</summary>
    public static int? FromBco16(double raw)
    {
        if (!double.IsFinite(raw) || raw < 0 || raw > 0xFFFF) return null;
        int v = (int)raw, code = 0, scale = 1;
        for (int i = 0; i < 4; i++, v >>= 4, scale *= 10)
        {
            int digit = v & 0xF;
            if (digit > 7) return null;
            code += digit * scale;
        }
        return code;
    }
}

public static class Actuators
{
    const double ComMin = 118_000_000, ComMax = 136_990_000, NavMin = 108_000_000, NavMax = 117_950_000;

    static double? B(bool? b) => b is bool v ? (v ? 1 : 0) : null;

    static Actuator Com(string name, string ev, Func<RadioGroup, double?> rb, bool nav = false) =>
        new(name, ActuatorGroup.Radio, ValueKind.Hz, "Hz", nav ? NavMin : ComMin, nav ? NavMax : ComMax, 1_000,
            s => s.Radio, s => rb(s.Radio), SetEvent: ev);

    /// <summary>
    /// The whitelist. Event names are from the MSFS key-event reference; the ones marked
    /// (verify) are not documented for MSFS 2020 and may come back NAME_UNRECOGNIZED, which
    /// makes that actuator "unsupported" instead of failing the session.
    /// </summary>
    public static readonly Actuator[] All =
    [
        Com("com1_active_hz", "COM_RADIO_SET_HZ", r => r.Com1ActiveHz),
        Com("com1_standby_hz", "COM_STBY_RADIO_SET_HZ", r => r.Com1StandbyHz),
        Com("com2_active_hz", "COM2_RADIO_SET_HZ", r => r.Com2ActiveHz),
        Com("com2_standby_hz", "COM2_STBY_RADIO_SET_HZ", r => r.Com2StandbyHz),
        Com("nav1_active_hz", "NAV1_RADIO_SET_HZ", r => r.Nav1ActiveHz, nav: true),
        Com("nav1_standby_hz", "NAV1_STBY_SET_HZ", r => r.Nav1StandbyHz, nav: true),
        Com("nav2_active_hz", "NAV2_RADIO_SET_HZ", r => r.Nav2ActiveHz, nav: true),
        Com("nav2_standby_hz", "NAV2_STBY_SET_HZ", r => r.Nav2StandbyHz, nav: true),
        new("xpdr_code", ActuatorGroup.Radio, ValueKind.Squawk, "octal", 0, 7777, 0,
            s => s.Radio, s => s.Radio.XpdrCode, SetEvent: "XPNDR_SET"),
        new("xpdr_ident", ActuatorGroup.Radio, ValueKind.Bool, "bool", 0, 1, 0.5,
            s => s.Radio, s => B(s.Radio.XpdrIdent), OnEvent: "XPNDR_IDENT_ON"),

        new("ap_master", ActuatorGroup.Autopilot, ValueKind.Bool, "bool", 0, 1, 0.5,
            s => s.Ap, s => B(s.Ap.Master), OnEvent: "AUTOPILOT_ON", OffEvent: "AUTOPILOT_OFF"),
        new("hdg_mode", ActuatorGroup.Autopilot, ValueKind.Bool, "bool", 0, 1, 0.5,
            s => s.Ap, s => B(s.Ap.HdgMode), OnEvent: "AP_HDG_HOLD_ON", OffEvent: "AP_HDG_HOLD_OFF"),
        new("hdg_bug_mag_deg", ActuatorGroup.Autopilot, ValueKind.HeadingDeg, "deg", 0, 360, 1,
            s => s.Ap, s => s.Ap.HdgBugMagDeg, SetEvent: "HEADING_BUG_SET"),
        new("nav_mode", ActuatorGroup.Autopilot, ValueKind.Bool, "bool", 0, 1, 0.5,
            s => s.Ap, s => B(s.Ap.NavMode), OnEvent: "AP_NAV1_HOLD_ON", OffEvent: "AP_NAV1_HOLD_OFF"),
        new("alt_hold", ActuatorGroup.Autopilot, ValueKind.Bool, "bool", 0, 1, 0.5,
            s => s.Ap, s => B(s.Ap.AltHold), OnEvent: "AP_ALT_HOLD_ON", OffEvent: "AP_ALT_HOLD_OFF"),
        new("alt_sel_ft", ActuatorGroup.Autopilot, ValueKind.Ft, "ft", 0, 60_000, 10,
            s => s.Ap, s => s.Ap.AltSelM is double m ? Units.MToFt(m) : null, SetEvent: "AP_ALT_VAR_SET_ENGLISH"),
        new("vs_hold", ActuatorGroup.Autopilot, ValueKind.Bool, "bool", 0, 1, 0.5,
            s => s.Ap, s => B(s.Ap.VsHold), OnEvent: "AP_VS_ON", OffEvent: "AP_VS_OFF"), // (verify)
        new("vs_sel_fpm", ActuatorGroup.Autopilot, ValueKind.Fpm, "ft/min", -6_000, 6_000, 50,
            s => s.Ap, s => s.Ap.VsSelMps is double v ? Units.MpsToFpm(v) : null, SetEvent: "AP_VS_VAR_SET_ENGLISH"),

        // The sim's own altimeter. It is never pushed to RB knobs (protocol §2.2); this only
        // sets the simulator, for example from the instructor page.
        new("qnh_hpa", ActuatorGroup.Altimeter, ValueKind.Hpa, "hPa", 900, 1_100, 0.1,
            s => s.Air, s => s.Air.SimQnhPa is double p ? p / Units.HPaToPa : null, SetEvent: "KOHLSMAN_SET"),

        new("flaps_pct", ActuatorGroup.Controls, ValueKind.Pct, "%", 0, 100, 2,
            s => s.Controls, s => s.Controls.FlapsPct, SetEvent: "FLAPS_SET"),
        new("elevator_trim_pct", ActuatorGroup.Controls, ValueKind.Pct, "%", -100, 100, 2,
            s => s.Controls, s => s.Controls.ElevatorTrimPct, SetEvent: "ELEVATOR_TRIM_SET"),
        new("gear_down", ActuatorGroup.Controls, ValueKind.Bool, "bool", 0, 1, 0.5,
            s => s.Controls, s => B(s.Controls.GearDown), OnEvent: "GEAR_DOWN", OffEvent: "GEAR_UP"),
        new("parking_brake", ActuatorGroup.Controls, ValueKind.Bool, "bool", 0, 1, 0.5,
            s => s.Controls, s => B(s.Controls.ParkingBrake), SetEvent: "PARKING_BRAKE_SET"), // (verify)
    ];

    public static readonly IReadOnlyDictionary<string, Actuator> ByName = All.ToDictionary(a => a.Name);

    /// <summary>
    /// Swaps are two explicit sets (active ← standby, standby ← active) built from the current
    /// read-back, not the sim's swap toggle, so each half is verified like any other write.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Active, string Standby)> Swaps =
        new Dictionary<string, (string, string)>
        {
            ["com1_swap"] = ("com1_active_hz", "com1_standby_hz"),
            ["com2_swap"] = ("com2_active_hz", "com2_standby_hz"),
            ["nav1_swap"] = ("nav1_active_hz", "nav1_standby_hz"),
            ["nav2_swap"] = ("nav2_active_hz", "nav2_standby_hz"),
        };

    public static IEnumerable<string> EventNames => All.SelectMany(a => a.Events).Distinct();

    public static ActuatorGroup ParseGroup(string s) => s.ToLowerInvariant() switch
    {
        "radio" => ActuatorGroup.Radio,
        "autopilot" => ActuatorGroup.Autopilot,
        "altimeter" => ActuatorGroup.Altimeter,
        "controls" => ActuatorGroup.Controls,
        _ => throw new ArgumentException($"unknown control group {s} (radio, autopilot, altimeter, controls)"),
    };
}

/// <summary>The radio stack in display form, for <c>GET /api/radio</c>.</summary>
public static class RadioView
{
    static readonly string[] XpdrStates = ["off", "standby", "test", "on", "alt"];

    public static JsonObject Build(SimState s)
    {
        var r = s.Radio;
        JsonObject Pair(double? active, double? standby) => new()
        {
            ["active_hz"] = Hz(active),
            ["standby_hz"] = Hz(standby),
            ["active_mhz"] = Mhz(active),
            ["standby_mhz"] = Mhz(standby),
        };
        var nav1 = Pair(r.Nav1ActiveHz, r.Nav1StandbyHz);
        nav1["radial_deg"] = r.Nav1RadialDeg is double rad ? Math.Round(rad, 1) : null;
        nav1["has_nav"] = r.Nav1Valid;
        return new JsonObject
        {
            ["valid"] = r.Valid,
            ["com1"] = Pair(r.Com1ActiveHz, r.Com1StandbyHz),
            ["com2"] = Pair(r.Com2ActiveHz, r.Com2StandbyHz),
            ["nav1"] = nav1,
            ["nav2"] = Pair(r.Nav2ActiveHz, r.Nav2StandbyHz),
            ["xpdr"] = new JsonObject
            {
                ["code"] = r.XpdrCode?.ToString("0000", CultureInfo.InvariantCulture),
                ["state"] = r.XpdrState is int st && st >= 0 && st < XpdrStates.Length ? XpdrStates[st] : null,
                ["ident"] = r.XpdrIdent,
            },
        };
    }

    static JsonNode? Hz(double? hz) => hz is double v ? Math.Round(v) : null;
    // Frequency, not the 8.33 kHz channel name: 118.0083 MHz shows as "118.008" (channel 118.010).
    static JsonNode? Mhz(double? hz) => hz is double v ? (v / 1e6).ToString("0.000", CultureInfo.InvariantCulture) : null;
}
