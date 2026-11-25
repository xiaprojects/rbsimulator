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
// Rb01Radio.cs: the RB-01 radio plate on the sim's COM radios: the daemon's radio list, and the plate's edits as control commands.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RbSim.Core;

/// <summary>
/// The RB-01 radio plate on the sim's COM1 and COM2 (D26). <see cref="List"/> is the daemon's
/// radio list (roastbeef main/radio.go <c>RadioStatus</c>): the answer to <c>GET /radio</c> and the
/// <c>/radioStatus</c> frame. <see cref="Commands"/> turns the plate's <c>POST /radio/{index}</c>
/// into control API commands, so a frequency set on the plate is gated, rate-limited and confirmed
/// by read-back like any other write (D20).
///
/// COM only: the plate is a COM radio, and it lifts any frequency below 118 MHz to 118.
/// </summary>
public static partial class Rb01Radio
{
    /// <summary>
    /// The plate's frequency when the sim gives none, or none fresh. Never empty: the plate would
    /// show "118.000" for an empty one.
    /// </summary>
    public const string NoFrequency = "---.---";

    sealed record Com(string Name, string Active, string Standby,
        Func<RadioGroup, double?> ActiveHz, Func<RadioGroup, double?> StandbyHz, Func<RadioGroup, bool?> Transmit);

    static readonly Com[] Coms =
    [
        new("COM1", "com1_active_hz", "com1_standby_hz", r => r.Com1ActiveHz, r => r.Com1StandbyHz, r => r.Com1Transmit),
        new("COM2", "com2_active_hz", "com2_standby_hz", r => r.Com2ActiveHz, r => r.Com2StandbyHz, r => r.Com2Transmit),
    ];

    /// <summary>The radios the plate lists, indexed 0 (COM1) and 1 (COM2).</summary>
    public static int Count => Coms.Length;

    /// <summary>
    /// One <c>RadioStatus</c> per COM radio, with the daemon's field names. The frequencies are the
    /// sim's read-back, or <see cref="NoFrequency"/> while the radio group is stale or a frequency is
    /// missing. <c>Enabled</c> is the radio the audio panel transmits on. Dual watch is never on: the
    /// sim has no standby monitoring.
    /// </summary>
    public static JsonArray List(SimState s, double now)
    {
        // The radios come in the user frame, with the position: the same timeout.
        var r = Staleness.Fresh(s.Radio, Staleness.PosS, now, s.Sim) ? s.Radio : null;
        var list = new JsonArray();
        foreach (var c in Coms)
        {
            list.Add(new JsonObject
            {
                ["Name"] = c.Name,
                ["SquelchLevel"] = 0,
                ["VolumeLevel"] = 0,
                ["FrequencyActive"] = Mhz(r is null ? null : c.ActiveHz(r)),
                ["FrequencyStandby"] = Mhz(r is null ? null : c.StandbyHz(r)),
                ["Enabled"] = r is not null && c.Transmit(r) == true,
                ["Dual"] = false,
                ["Driver"] = "SIM",
                ["Path"] = "",
                // The plate names a frequency from its own database, whatever the daemon stored.
                ["LabelActive"] = "",
                ["LabelStandby"] = "",
            });
        }
        return list;
    }

    /// <summary>
    /// The control commands, as a <c>POST /api/control</c> body, for the plate's post of radio
    /// <paramref name="index"/>. The plate posts the radio whole, so only a frequency that differs
    /// from the read-back by more than the actuator's tolerance becomes a command; its swap is
    /// therefore two sets. <c>Enabled</c> and <c>Dual</c> are not written: the transmit selection
    /// belongs to the sim's audio panel, and the sim has no dual watch. The next list shows the
    /// sim's values again. Returns why the post is refused, or null.
    /// </summary>
    public static string? Commands(int index, JsonNode? body, SimState s, out JsonObject commands)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Coms.Length);
        commands = [];
        if (body is not JsonObject o) return "expected the plate's radio object";
        var c = Coms[index];
        foreach (var (key, name, readBack) in new[] { ("FrequencyActive", c.Active, c.ActiveHz), ("FrequencyStandby", c.Standby, c.StandbyHz) })
        {
            if (!o.TryGetPropertyValue(key, out var node) || node is null) continue;
            if (node is not JsonValue v || !v.TryGetValue(out string? text) || ParseMhz(text) is not double hz)
            {
                commands = []; // all or nothing, as for POST /api/control
                return $"{key}: expected a frequency in MHz, such as \"121.500\"";
            }
            if (!Actuators.ByName[name].Matches(hz, readBack(s.Radio))) commands[name] = hz;
        }
        return null;
    }

    /// <summary>
    /// A frequency as the plate writes it, "121.500" or "121.5", in Hz; null for anything else,
    /// such as "1--.---" while the plate's keypad is half typed. The range is the actuator's to check.
    /// </summary>
    public static double? ParseMhz(string? text)
    {
        var m = MhzPattern().Match(text?.Trim() ?? "");
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1_000_000.0
            + int.Parse(m.Groups[2].Value.PadRight(3, '0'), CultureInfo.InvariantCulture) * 1_000.0;
    }

    static string Mhz(double? hz) => hz is double v ? RadioView.MhzText(v) : NoFrequency;

    [GeneratedRegex("^([0-9]{3})\\.([0-9]{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex MhzPattern();
}
