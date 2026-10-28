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
// StateStore.cs: the latest SimState snapshot shared between the source thread and the web server.

using System.Diagnostics;
using RbSim.Core;

namespace RbSim.Msfs;

/// <summary>Source health for <c>/api/status</c> (RB-SIM-MSF-120).</summary>
public sealed class SourceStatus
{
    public string? DllPath { get; set; }
    public bool LinkOpen { get; set; }
    public bool SimRunning { get; set; }
    public uint PauseFlags { get; set; }
    public string[] RejectedDatums { get; set; } = [];
    public string? LastException { get; set; }
    public string? LastError { get; set; }
    public RateMeter Frames { get; } = new();
    public RateMeter Published { get; } = new();
}

/// <summary>Counts events in a sliding 2 s window.</summary>
public sealed class RateMeter
{
    const double WindowS = 2.0;
    readonly Queue<double> _t = new();
    readonly object _lock = new();

    public void Tick(double now)
    {
        lock (_lock)
        {
            _t.Enqueue(now);
            Trim(now);
        }
    }

    public double Hz(double now)
    {
        lock (_lock)
        {
            Trim(now);
            return _t.Count / WindowS;
        }
    }

    void Trim(double now)
    {
        while (_t.Count > 0 && now - _t.Peek() > WindowS) _t.Dequeue();
    }
}

public sealed class StateStore
{
    SimState _state;
    readonly object _write = new();
    long _seq;

    public StateStore(string source)
    {
        _state = new SimState();
        _state.Sim.Source = source;
    }

    public SourceStatus Status { get; } = new();

    /// <summary>Monotonic seconds, the t_mono clock of every group.</summary>
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public SimState Snapshot => Volatile.Read(ref _state);

    /// <summary>Increments with every published user-aircraft frame.</summary>
    public long Seq => Interlocked.Read(ref _seq);

    /// <summary>
    /// Publishes a new snapshot. <paramref name="mutate"/> gets a shallow copy: it must replace
    /// groups and lists, never modify the ones it finds, because readers may hold them.
    /// </summary>
    public void Update(Action<SimState> mutate, bool newFrame = false)
    {
        lock (_write)
        {
            var next = _state.ShallowCopy();
            mutate(next);
            Volatile.Write(ref _state, next);
            if (newFrame) Interlocked.Increment(ref _seq);
        }
    }
}
