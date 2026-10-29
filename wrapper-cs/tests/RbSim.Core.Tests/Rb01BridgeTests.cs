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
// Rb01BridgeTests.cs: the RB-01 addons bridge: merge, refusals, change notifications.

using RbSim.Core;

namespace RbSim.Core.Tests;

public class Rb01BridgeTests
{
    // Covers: RB-SIM-HMI-050
    [Fact]
    public void PostMergesNumbersAndGetReturnsTheMap()
    {
        var b = new Rb01Bridge();
        Assert.Null(b.Post("{\"qnh\": 1013.25, \"autoqnh\": 1}"));
        Assert.Null(b.Post("{\"vorfreq\": 109.4, \"qnh\": 1015}"));
        Assert.Equal("{\"autoqnh\":1,\"qnh\":1015,\"vorfreq\":109.4}", b.Snapshot().ToJsonString());
    }

    // Covers: RB-SIM-HMI-050
    [Theory]
    [InlineData("{\"qnh\": \"1013\"}")]
    [InlineData("{\"qnh\": true}")]
    [InlineData("[1, 2]")]
    [InlineData("{\"qnh\": 1013")]
    [InlineData("{\"qnh\": 1e40}")]
    [InlineData("{\"\": 1}")]
    public void AnInvalidPostStoresNothing(string body)
    {
        var b = new Rb01Bridge();
        Assert.Null(b.Post("{\"qnh\": 1013}"));
        long v = b.Version;
        Assert.NotNull(b.Post(body.Replace("}", ", \"other\": 2}")));
        Assert.Equal(v, b.Version);
        Assert.Equal("{\"qnh\":1013}", b.Snapshot().ToJsonString());
    }

    // Covers: RB-SIM-HMI-050
    [Fact]
    public void KeysAreBounded()
    {
        var b = new Rb01Bridge();
        Assert.NotNull(b.Post($"{{\"{new string('k', Rb01Bridge.MaxKeyLength + 1)}\": 1}}"));
        string full = "{" + string.Join(",", Enumerable.Range(0, Rb01Bridge.MaxKeys).Select(i => $"\"k{i}\": {i}")) + "}";
        Assert.Null(b.Post(full));
        Assert.NotNull(b.Post("{\"one_more\": 1}"));
        Assert.Null(b.Post("{\"k0\": 5}")); // an existing key can still change
        Assert.Equal(Rb01Bridge.MaxKeys, b.Snapshot().Count);
    }

    // Covers: RB-SIM-HMI-050
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("null")]
    [InlineData("{}")]
    public void AnEmptyPostChangesNothing(string body)
    {
        var b = new Rb01Bridge();
        Assert.Null(b.Post(body));
        Assert.Equal(0, b.Version);
        Assert.Empty(b.Snapshot());
    }

    // Covers: RB-SIM-HMI-050
    [Fact]
    public void ClientsGetTheMapOnlyWhenItChanges()
    {
        var b = new Rb01Bridge();
        Assert.Null(b.Post("{\"qnh\": 1013}"));
        long seen = b.Version; // a client connecting now
        Assert.Empty(b.Changes(ref seen));
        Assert.Null(b.Post("{\"qnh\": 1013}")); // the same value is no change, as on the daemon
        Assert.Empty(b.Changes(ref seen));
        Assert.Null(b.Post("{\"qnh\": 1014}"));
        var sent = Assert.Single(b.Changes(ref seen));
        Assert.Equal("{\"qnh\":1014}", sent.ToJsonString());
        Assert.Empty(b.Changes(ref seen));
    }
}
