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
// Rb01Checklist.cs: the RB-01 checklist plate's list, loaded from a file and kept in memory as on the daemon.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace RbSim.Core;

/// <summary>
/// The daemon's checklist (roastbeef main/checklist.go), behind <c>/checklist/default/status</c>:
/// a list of groups, each with its <c>items</c>, loaded from a file at start and kept in memory.
/// The plate posts the whole list, with each item's status, after every change, and reads it back
/// when it opens. So the status survives a reload of the HMI but not a restart, as on the daemon,
/// and the file is never written. Unlike the daemon, a post that isn't a list of groups is refused
/// and stores nothing, and a reset restores the file's list (the daemon's DELETE is a TODO).
/// </summary>
public sealed class Rb01Checklist
{
    public const int MaxChars = 1 << 20;

    readonly object gate = new();
    readonly string initial;
    string current;

    /// <summary>
    /// The file's list, or the daemon's empty list without a file. Throws ArgumentException when
    /// the file is not a list of groups.
    /// </summary>
    public Rb01Checklist(string? json = null)
    {
        initial = "[]";
        if (json is not null && Read(json, out initial) is string why) throw new ArgumentException(why);
        current = initial;
    }

    public JsonNode Snapshot()
    {
        lock (gate) return JsonNode.Parse(current)!;
    }

    /// <summary>Replaces the list with the posted one. Returns null, or why nothing was stored.</summary>
    public string? Post(string body)
    {
        if (Read(body, out string list) is string why) return why;
        lock (gate) current = list;
        return null;
    }

    /// <summary>Back to the file's list, every item as the file has it.</summary>
    public void Reset()
    {
        lock (gate) current = initial;
    }

    static string? Read(string json, out string list)
    {
        list = "";
        if (json.Length > MaxChars) return $"a checklist is at most {MaxChars} characters";
        try
        {
            if (JsonNode.Parse(json) is not JsonArray groups) return "expected a JSON list of checklist groups";
            foreach (var g in groups)
            {
                // The plate walks every group's items when the keypad moves through them.
                if (g is not JsonObject group || group["items"] is not JsonArray items || items.Any(i => i is not JsonObject))
                    return "each checklist group must be an object with an \"items\" list of objects";
            }
            list = groups.ToJsonString();
            return null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return $"invalid JSON: {ex.Message}";
        }
    }
}
