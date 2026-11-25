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
// Rb01ChecklistTests.cs: the RB-01 checklist plate's list: the file, the plate's posts, refusals and reset.

using System.Text.Json.Nodes;
using RbSim.Core;

namespace RbSim.Core.Tests;

public class Rb01ChecklistTests
{
    // The format of roastbeef main/checklist.go's header comment.
    const string File = """
        [
          {
            "isselected": "", "value": "todo", "id": 0, "name": "BEFORE START", "text": "EXAMPLE",
            "date": "", "dateParsed": "__:__",
            "items": [
              { "isselected": "", "value": "todo", "id": 0, "date": "", "dateParsed": "__:__", "name": "SEATBELTS", "text": "FASTENED" },
              { "isselected": "", "value": "todo", "id": 1, "date": "", "dateParsed": "__:__", "name": "FUEL", "text": "CHECKED" }
            ]
          }
        ]
        """;

    static string Value(JsonNode list, int group, int item) => (string)list[group]!["items"]![item]!["value"]!;

    // Covers: RB-SIM-HMI-080
    [Fact]
    public void WithoutAFileTheListIsTheDaemonsEmptyOne()
    {
        Assert.Equal("[]", new Rb01Checklist().Snapshot().ToJsonString());
        Assert.Equal("[]", new Rb01Checklist("[]").Snapshot().ToJsonString());
    }

    // Covers: RB-SIM-HMI-080
    [Fact]
    public void APostReplacesTheListAndAResetRestoresTheFile()
    {
        var c = new Rb01Checklist(File);
        Assert.Equal("todo", Value(c.Snapshot(), 0, 0));

        // what updateStatusForItem() posts: the whole list, one item changed and dated
        var posted = c.Snapshot();
        posted[0]!["items"]![0]!["value"] = "pass";
        posted[0]!["items"]![0]!["date"] = "2026-10-30T09:12:00.000Z";
        Assert.Null(c.Post(posted.ToJsonString()));
        var now = c.Snapshot();
        Assert.Equal("pass", Value(now, 0, 0));
        Assert.Equal("todo", Value(now, 0, 1));

        c.Reset();
        Assert.Equal("todo", Value(c.Snapshot(), 0, 0));
        Assert.Equal(JsonNode.Parse(File)!.ToJsonString(), c.Snapshot().ToJsonString());
    }

    // Covers: RB-SIM-HMI-080
    [Fact]
    public void ASnapshotIsACopy()
    {
        var c = new Rb01Checklist(File);
        var mine = c.Snapshot();
        mine[0]!["items"]![0]!["value"] = "fail";
        Assert.Equal("todo", Value(c.Snapshot(), 0, 0));
    }

    // Covers: RB-SIM-HMI-080
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"group\": 0, \"check\": 0, \"value\": 1}")] // the plate's unused storeChoice()
    [InlineData("[1]")]
    [InlineData("[{}]")]
    [InlineData("[{\"items\": {}}]")]
    [InlineData("[{\"items\": [1]}]")]
    [InlineData("[{\"items\": []}")]
    public void AnInvalidPostStoresNothing(string body)
    {
        var c = new Rb01Checklist(File);
        string before = c.Snapshot().ToJsonString();
        Assert.NotNull(c.Post(body));
        Assert.Equal(before, c.Snapshot().ToJsonString());
        Assert.Throws<ArgumentException>(() => new Rb01Checklist(body));
    }

    // Covers: RB-SIM-HMI-080
    [Fact]
    public void AListIsBounded()
    {
        var c = new Rb01Checklist();
        string big = "[{\"items\": [], \"text\": \"" + new string('x', Rb01Checklist.MaxChars) + "\"}]";
        Assert.NotNull(c.Post(big));
        Assert.Equal("[]", c.Snapshot().ToJsonString());
    }
}
