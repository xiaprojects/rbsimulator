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
// MsfsDetectors.cs: pause, link and teleport inference from MSFS events and data (RB-SIM-MSF-050/060, MOD-050).

namespace RbSim.Core;

/// <summary>
/// Paused while Pause_EX1 reports any flag, or while SIMULATION TIME has not advanced for
/// 0.5 s although frames still arrive (RB-SIM-MSF-050). The second rule covers the Esc
/// pause, Active Pause and photo mode, which do not reliably send events.
/// </summary>
public sealed class PauseDetector
{
    public const double FrozenS = 0.5;

    uint _eventFlags;
    double _lastSimTime = double.NaN;
    double _lastAdvance = double.NegativeInfinity;
    double _lastFrame = double.NegativeInfinity;

    public uint EventFlags => _eventFlags;

    public void OnPauseEvent(uint flags) => _eventFlags = flags;

    public void OnFrame(double simulationTime, double now)
    {
        _lastFrame = now;
        if (!double.IsFinite(simulationTime)) return;
        if (simulationTime != _lastSimTime)
        {
            _lastSimTime = simulationTime;
            _lastAdvance = now;
        }
    }

    public bool Paused(double now)
    {
        if (_eventFlags != 0) return true;
        bool framesArrive = now - _lastFrame <= FrozenS;
        return framesArrive && double.IsFinite(_lastSimTime) && now - _lastAdvance >= FrozenS;
    }

    public void Reset()
    {
        _eventFlags = 0;
        _lastSimTime = double.NaN;
        _lastAdvance = double.NegativeInfinity;
        _lastFrame = double.NegativeInfinity;
    }
}

/// <summary>
/// Connected while SimConnect is open, the last <c>Sim</c> event was not 0, and a user frame
/// arrived within 2 s (RB-SIM-MSF-060). It never waits for SimStop, which MSFS 2024 omits
/// when leaving to the main menu. A pause reported by Pause_EX1 holds the link even if frames
/// stop, so a full pause freezes instead of failing (RB-SIM-MOD-040; frame delivery during a
/// full pause is checked on L1, verify).
/// </summary>
public sealed class LinkMonitor
{
    public const double FrameTimeoutS = 2.0;

    double _lastFrame = double.NegativeInfinity;

    public bool Open { get; private set; }
    public bool SimRunning { get; private set; } = true;

    public void OnOpen() { Open = true; SimRunning = true; _lastFrame = double.NegativeInfinity; }
    public void OnClose() => Open = false;
    public void OnSimEvent(uint running) => SimRunning = running != 0;
    public void OnFrame(double now) => _lastFrame = now;

    public bool Connected(double now, bool pauseEvent) =>
        Open && SimRunning && (now - _lastFrame <= FrameTimeoutS || (pauseEvent && _lastFrame > double.NegativeInfinity));
}

/// <summary>
/// Counts teleports: slew, reposition or flight load increments teleport_seq (RB-SIM-MOD-050).
/// A position jump faster than <see cref="JumpSpeedMps"/> between frames also counts.
/// </summary>
public sealed class TeleportDetector
{
    public const double JumpSpeedMps = 1000.0;

    double _lat = double.NaN, _lon = double.NaN, _t;
    bool _slew;

    public int Seq { get; private set; }

    public void OnEvent() => Seq++;

    public void OnFrame(double? lat, double? lon, bool slew, double now)
    {
        if (slew && !_slew) Seq++;
        _slew = slew;
        if (lat is null || lon is null) return;
        if (double.IsFinite(_lat) && now > _t)
        {
            double d = DistanceM(_lat, _lon, lat.Value, lon.Value);
            if (d / (now - _t) > JumpSpeedMps && d > 500.0) Seq++;
        }
        _lat = lat.Value;
        _lon = lon.Value;
        _t = now;
    }

    /// <summary>Equirectangular distance; good enough for jumps and traffic ranking.</summary>
    public static double DistanceM(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371008.8;
        double rad = Math.PI / 180.0;
        double x = (lon2 - lon1) * rad * Math.Cos((lat1 + lat2) / 2 * rad);
        double y = (lat2 - lat1) * rad;
        return Math.Sqrt(x * x + y * y) * R;
    }
}
