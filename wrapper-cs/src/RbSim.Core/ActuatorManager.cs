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
// ActuatorManager.cs: validates commands, rate-limits the writes and verifies each one by read-back.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RbSim.Core;

public enum CommandStatus { Pending, Sent, Confirmed, Mismatch, Unsupported, Failed }

public readonly record struct ActuatorWrite(Actuator Actuator, string Event, uint Data);

/// <summary>
/// Desired state per actuator, level-style: a new command replaces the previous one. The
/// source thread calls <see cref="Due"/> on every pass; it returns the writes to transmit
/// and settles each command against the read-back. Thread-safe. Times are monotonic seconds.
/// </summary>
public sealed class ActuatorManager(IEnumerable<ActuatorGroup> allowed)
{
    /// <summary>At most 2 Hz per actuator (protocol §5).</summary>
    public const double MinWriteIntervalS = 0.5;
    /// <summary>A write not seen in the read-back within 2 s is a mismatch (RB-SIM-MSF-100).</summary>
    public const double VerifyTimeoutS = 2.0;

    sealed class Slot
    {
        public double Desired;
        public double SubmittedAt;
        public double SentAt = double.NegativeInfinity;
        public double LastWriteAt = double.NegativeInfinity;
        public CommandStatus Status;
        public string? Detail;
    }

    readonly HashSet<ActuatorGroup> _allowed = [.. allowed];
    readonly Dictionary<string, Slot> _slots = [];
    readonly HashSet<string> _unsupported = [];
    readonly object _lock = new();

    public IReadOnlyCollection<ActuatorGroup> Allowed => _allowed;

    public bool IsAllowed(Actuator a) => _allowed.Contains(a.Group);

    public bool IsSupported(Actuator a)
    {
        lock (_lock) return !a.Events.Any(_unsupported.Contains);
    }

    /// <summary>The sim rejected the event name (NAME_UNRECOGNIZED): its actuators become unsupported.</summary>
    public void MarkUnsupported(string eventName)
    {
        lock (_lock) _unsupported.Add(eventName);
    }

    /// <summary>A new SimConnect session maps every event again.</summary>
    public void ResetSupport()
    {
        lock (_lock) _unsupported.Clear();
    }

    /// <summary>
    /// Validates a command object such as <c>{"com1_standby_hz": 121500000, "ap_master": true}</c>.
    /// Returns the errors; when there are any, nothing should be submitted.
    /// </summary>
    public List<string> Parse(JsonObject body, SimState snap, out List<(Actuator Actuator, double Value)> commands)
    {
        var errors = new List<string>();
        commands = [];
        foreach (var (key, node) in body)
        {
            if (Actuators.Swaps.TryGetValue(key, out var swap))
            {
                if (!IsTrue(node))
                {
                    errors.Add($"{key}: only true is accepted");
                    continue;
                }
                var active = Actuators.ByName[swap.Active];
                var standby = Actuators.ByName[swap.Standby];
                if (Refused(key, active, errors) || Refused(key, standby, errors)) continue;
                if (active.ReadBack(snap) is not double a || standby.ReadBack(snap) is not double s)
                {
                    errors.Add($"{key}: no read-back of the frequencies to swap");
                    continue;
                }
                commands.Add((active, s));
                commands.Add((standby, a));
                continue;
            }
            if (!Actuators.ByName.TryGetValue(key, out var act))
            {
                errors.Add($"{key}: unknown control");
                continue;
            }
            if (Refused(key, act, errors)) continue;
            if (Value(act, node, out string? why) is double v) commands.Add((act, v));
            else errors.Add($"{key}: {why}");
        }
        return errors;
    }

    bool Refused(string key, Actuator a, List<string> errors)
    {
        if (!IsAllowed(a))
        {
            errors.Add($"{key}: control group {a.Group.ToString().ToLowerInvariant()} is disabled in the configuration");
            return true;
        }
        if (!IsSupported(a))
        {
            errors.Add($"{key}: the simulator does not recognise {string.Join("/", a.Events)}");
            return true;
        }
        return false;
    }

    static bool IsTrue(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) && b;

    static double? Value(Actuator a, JsonNode? node, out string? why)
    {
        why = null;
        if (node is not JsonValue jv)
        {
            why = "expected a value";
            return null;
        }
        double v;
        if (a.Kind == ValueKind.Bool)
        {
            if (jv.TryGetValue(out bool b)) v = b ? 1 : 0;
            else if (jv.TryGetValue(out double d) && d is 0 or 1) v = d;
            else
            {
                why = "expected true or false";
                return null;
            }
            if (a.OnlyTrue && v == 0)
            {
                why = "only true is accepted";
                return null;
            }
            return v;
        }
        if (a.Kind == ValueKind.Squawk)
        {
            int code = -1;
            if (jv.TryGetValue(out string? str)) int.TryParse(str, NumberStyles.None, CultureInfo.InvariantCulture, out code);
            else if (jv.TryGetValue(out double d) && d == Math.Floor(d)) code = (int)d;
            if (!Squawk.IsValid(code))
            {
                why = "expected four octal digits, for example \"7000\"";
                return null;
            }
            return code;
        }
        if (jv.GetValueKind() != JsonValueKind.Number || !jv.TryGetValue(out v) || !double.IsFinite(v))
        {
            why = $"expected a number in {a.Unit}";
            return null;
        }
        if (v < a.Min || v > a.Max)
        {
            why = $"{v.ToString(CultureInfo.InvariantCulture)} is outside {a.Min.ToString(CultureInfo.InvariantCulture)}…{a.Max.ToString(CultureInfo.InvariantCulture)} {a.Unit}";
            return null;
        }
        return v;
    }

    public void Submit(IEnumerable<(Actuator Actuator, double Value)> commands, double now)
    {
        lock (_lock)
        {
            foreach (var (a, v) in commands)
            {
                if (!_slots.TryGetValue(a.Name, out var slot)) _slots[a.Name] = slot = new Slot();
                slot.Desired = v;
                slot.SubmittedAt = now;
                slot.SentAt = double.NegativeInfinity;
                slot.Status = CommandStatus.Pending;
                slot.Detail = null;
            }
        }
    }

    /// <summary>Settles open commands against <paramref name="snap"/> and returns the writes to transmit now.</summary>
    public List<ActuatorWrite> Due(SimState snap, double now)
    {
        var writes = new List<ActuatorWrite>();
        lock (_lock)
        {
            foreach (var (name, slot) in _slots)
            {
                if (slot.Status is not (CommandStatus.Pending or CommandStatus.Sent)) continue;
                var a = Actuators.ByName[name];
                if (!snap.Sim.Connected)
                {
                    Settle(slot, CommandStatus.Failed, "simulator not connected");
                    continue;
                }
                if (a.Events.Any(_unsupported.Contains))
                {
                    Settle(slot, CommandStatus.Unsupported, $"the simulator does not recognise {string.Join("/", a.Events)}");
                    continue;
                }
                double? rb = a.Source(snap).Valid ? a.ReadBack(snap) : null;
                if (a.Matches(slot.Desired, rb))
                {
                    // Write only when the intent differs from the read-back (protocol §5).
                    Settle(slot, CommandStatus.Confirmed, slot.Status == CommandStatus.Pending ? "already set" : null);
                    continue;
                }
                if (slot.Status == CommandStatus.Pending)
                {
                    if (now - slot.LastWriteAt < MinWriteIntervalS) continue;
                    var (ev, data) = a.Write(slot.Desired);
                    writes.Add(new ActuatorWrite(a, ev, data));
                    slot.SentAt = slot.LastWriteAt = now;
                    slot.Status = CommandStatus.Sent;
                }
                else if (now - slot.SentAt >= VerifyTimeoutS)
                {
                    // The aircraft may run its own logic and ignore standard events: report, never retry.
                    Settle(slot, CommandStatus.Mismatch, rb is double r
                        ? $"read-back {r.ToString("0.###", CultureInfo.InvariantCulture)} after {VerifyTimeoutS:0} s"
                        : $"no read-back after {VerifyTimeoutS:0} s");
                }
            }
        }
        return writes;
    }

    static void Settle(Slot slot, CommandStatus status, string? detail)
    {
        slot.Status = status;
        slot.Detail = detail;
    }

    /// <summary>The <c>actuators</c> object of <c>GET /api/control</c>.</summary>
    public JsonObject View(SimState snap, double now)
    {
        var o = new JsonObject();
        lock (_lock)
        {
            foreach (var a in Actuators.All)
            {
                double? rb = a.Source(snap).Valid ? a.ReadBack(snap) : null;
                var item = new JsonObject
                {
                    ["group"] = a.Group.ToString().ToLowerInvariant(),
                    ["unit"] = a.Unit,
                    ["allowed"] = IsAllowed(a),
                    ["supported"] = !a.Events.Any(_unsupported.Contains),
                    ["read_back"] = Show(a, rb),
                };
                if (_slots.TryGetValue(a.Name, out var slot))
                {
                    item["command"] = new JsonObject
                    {
                        ["value"] = Show(a, slot.Desired),
                        ["status"] = slot.Status.ToString().ToLowerInvariant(),
                        ["detail"] = slot.Detail,
                        ["age_s"] = Math.Round(now - slot.SubmittedAt, 1),
                    };
                }
                o[a.Name] = item;
            }
        }
        return o;
    }

    /// <summary>The value in API form: booleans as true/false, squawks as "0000" strings.</summary>
    public static JsonNode? Show(Actuator a, double? v) => v switch
    {
        null => null,
        double b when a.Kind == ValueKind.Bool => b > 0.5,
        double c when a.Kind == ValueKind.Squawk => ((int)c).ToString("0000", CultureInfo.InvariantCulture),
        double d when a.Kind is ValueKind.Hz or ValueKind.Ft or ValueKind.Fpm => Math.Round(d),
        double d => Math.Round(d, 2),
    };
}
