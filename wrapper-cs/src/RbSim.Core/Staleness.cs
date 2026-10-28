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
// Staleness.cs: group timeouts and the freshness rule of protocol §2.3.

namespace RbSim.Core;

public static class Staleness
{
    public const double AttS = 0.5;
    public const double AirS = 1.0;
    public const double PosS = 2.0;
    public const double EngS = 3.0;
    public const double TrafficTargetS = 10.0;

    /// <summary>
    /// A group is fresh when valid and younger than its timeout. While the sim is connected
    /// and paused the values are frozen: they stay valid (RB-SIM-MOD-040). Disconnection is
    /// a failure and never freezes.
    /// </summary>
    public static bool Fresh(Group g, double timeoutS, double now, SimInfo sim)
    {
        if (!sim.Connected || !g.Valid) return false;
        if (sim.Paused) return true;
        return now - g.TMono <= timeoutS;
    }
}
