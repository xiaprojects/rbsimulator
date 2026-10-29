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
// Rb01Bridge.cs: the RB-01 daemon's addons bridge (/bridge/float), named numbers the HMI and its addons share.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace RbSim.Core;

/// <summary>
/// The daemon's addons bridge, in memory as on the daemon: a POST merges a JSON object of numbers
/// into the map, a GET returns the map, and each change goes to the /bridge/float/ws clients as the
/// whole map. The HMI publishes the QNH in use here. Unlike the daemon, nothing posted here reaches
/// the flight data: the daemon takes "ias_mps" as the indicated airspeed, the wrapper takes the
/// airspeed from its source only.
/// </summary>
public sealed class Rb01Bridge
{
    public const int MaxKeys = 256;
    public const int MaxKeyLength = 64;

    readonly object gate = new();
    readonly SortedDictionary<string, float> values = new(StringComparer.Ordinal);
    long version;

    public long Version { get { lock (gate) return version; } }

    public JsonObject Snapshot() { lock (gate) return Map(); }

    /// <summary>
    /// Merges the posted object, as the daemon does (float32, last key wins). An empty body or
    /// null changes nothing. Returns null, or why nothing was stored.
    /// </summary>
    public string? Post(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        Dictionary<string, float>? msg;
        try
        {
            msg = JsonSerializer.Deserialize<Dictionary<string, float>>(body);
        }
        catch (JsonException ex)
        {
            return $"expected a JSON object of numbers: {ex.Message}";
        }
        if (msg is null) return null;
        foreach (var (k, v) in msg)
        {
            if (k.Length == 0 || k.Length > MaxKeyLength) return $"key length must be 1 to {MaxKeyLength}";
            if (!float.IsFinite(v)) return $"{k}: not a finite float32";
        }
        lock (gate)
        {
            if (values.Count + msg.Keys.Count(k => !values.ContainsKey(k)) > MaxKeys) return $"the bridge holds at most {MaxKeys} keys";
            foreach (var (k, v) in msg)
            {
                if (values.TryGetValue(k, out float old) && old == v) continue;
                values[k] = v;
                version++;
            }
        }
        return null;
    }

    /// <summary>The map once if it changed since version seen, else nothing; seen moves on.</summary>
    public JsonNode[] Changes(ref long seen)
    {
        lock (gate)
        {
            if (seen == version) return [];
            seen = version;
            return [Map()];
        }
    }

    // The caller holds the gate.
    JsonObject Map()
    {
        var o = new JsonObject();
        foreach (var (k, v) in values) o[k] = v;
        return o;
    }
}
