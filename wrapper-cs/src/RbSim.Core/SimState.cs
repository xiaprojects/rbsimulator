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
// SimState.cs: the canonical simulator state (protocol §2.2), SI units, angles in degrees.

namespace RbSim.Core;

// Signs (protocol §2.1): pitch + nose up, roll + right wing down, turn rate + right,
// slip + ball right, magvar + east. Headings are true unless the name says mag.
// A null field is invalid (the aircraft does not implement it); it is never zero.

/// <summary>Every group carries valid and t_mono (RB-SIM-MOD-020).</summary>
public abstract class Group
{
    public bool Valid { get; set; }
    /// <summary>Monotonic receive time in seconds.</summary>
    public double TMono { get; set; }
}

public sealed class SimInfo
{
    public string Source { get; set; } = "";
    public bool Connected { get; set; }
    public bool Paused { get; set; }
    public bool OnGround { get; set; }
    public DateTime? Utc { get; set; }
    public int TeleportSeq { get; set; }
    public string Aircraft { get; set; } = "";
    public string SimName { get; set; } = "";
    public string SimVersion { get; set; } = "";

    public SimInfo Clone() => (SimInfo)MemberwiseClone();
}

public sealed class PosGroup : Group
{
    public double? LatDeg { get; set; }
    public double? LonDeg { get; set; }
    public double? AltMslM { get; set; }
    public double? GeoidSepM { get; set; }
    public double? TrackTrueDeg { get; set; }
    public double? GsMps { get; set; }
    public double? VsGpsMps { get; set; }
    public double? MagvarDeg { get; set; }
}

public sealed class AttGroup : Group
{
    public double? PitchDeg { get; set; }
    public double? RollDeg { get; set; }
    public double? HdgTrueDeg { get; set; }
    public double? HdgMagDeg { get; set; }
    public double? TurnRateDps { get; set; }
    public double? SlipDeg { get; set; }
    public double? GLoad { get; set; }
}

public sealed class AirGroup : Group
{
    public double? IasMps { get; set; }
    public double? TasMps { get; set; }
    public double? PressureAltM { get; set; }
    public double? StaticPa { get; set; }
    public double? VsBaroMps { get; set; }
    public double? OatC { get; set; }
    /// <summary>
    /// The sim's own altimeter setting; information only, never pushed to RB knobs. The one
    /// exception is RB-09's $RBAIR: a remote display follows its master's QNH (D24).
    /// </summary>
    public double? SimQnhPa { get; set; }
}

/// <summary>Test oracle only; RB devices compute their own wind.</summary>
public sealed class WindGroup : Group
{
    public double? FromTrueDeg { get; set; }
    public double? SpeedMps { get; set; }
}

public sealed class EngGroup : Group
{
    public double? Rpm { get; set; }
    public double? MapPa { get; set; }
    public double? OilPressPa { get; set; }
    public double? FuelPressPa { get; set; }
    public double? OilTempC { get; set; }
    public double? CoolantTempC { get; set; }
    public double?[] ChtC { get; set; } = new double?[4];
    public double?[] EgtC { get; set; } = new double?[4];
    /// <summary>CHT/EGT per cylinder are synthesised from one sim value (RB-SIM-MOD-060).</summary>
    public bool CylindersSynthetic { get; set; }
    public double? FuelFlowLph { get; set; }
}

public sealed class FuelGroup : Group
{
    public double? LeftL { get; set; }
    public double? RightL { get; set; }
}

public sealed class ElecGroup : Group
{
    public double? BusV { get; set; }
}

/// <summary>The sim's own autopilot state, used as read-back.</summary>
public sealed class ApGroup : Group
{
    public bool? Master { get; set; }
    public double? HdgBugMagDeg { get; set; }
    public double? AltSelM { get; set; }
    public bool? HdgMode { get; set; }
    public bool? AltHold { get; set; }
    public bool? VsHold { get; set; }
    public double? VsSelMps { get; set; }
    public bool? NavMode { get; set; }
}

/// <summary>
/// The sim GPS's active leg (protocol §2.2 <c>nav</c>): the waypoint it is flying to and the
/// one it comes from. The guidance sentences and RB-01's path and HSI are computed from it and
/// the ownship position (<see cref="Guidance"/>). Valid only while a waypoint is active.
/// </summary>
public sealed class NavGroup : Group
{
    public string ToId { get; set; } = "";
    public double? ToLatDeg { get; set; }
    public double? ToLonDeg { get; set; }
    /// <summary>The plan's altitude at the waypoint; null when the plan gives none.</summary>
    public double? ToAltMslM { get; set; }
    public string FromId { get; set; } = "";
    /// <summary>Null when the leg has no origin (a direct-to the sim reports without one).</summary>
    public double? FromLatDeg { get; set; }
    public double? FromLonDeg { get; set; }
}

/// <summary>Radio stack read-back (protocol §2.2 <c>radio</c>, R2). Frequencies in Hz.</summary>
public sealed class RadioGroup : Group
{
    public double? Com1ActiveHz { get; set; }
    public double? Com1StandbyHz { get; set; }
    public double? Com2ActiveHz { get; set; }
    public double? Com2StandbyHz { get; set; }
    public double? Nav1ActiveHz { get; set; }
    public double? Nav1StandbyHz { get; set; }
    public double? Nav2ActiveHz { get; set; }
    public double? Nav2StandbyHz { get; set; }
    public double? Nav1RadialDeg { get; set; }
    public bool? Nav1Valid { get; set; }
    /// <summary>Squawk as its four octal digits read as a decimal number (7000 = "7000").</summary>
    public int? XpdrCode { get; set; }
    /// <summary>TRANSPONDER STATE: 0 off, 1 standby, 2 test, 3 on, 4 alt (verify, L1).</summary>
    public int? XpdrState { get; set; }
    public bool? XpdrIdent { get; set; }
}

/// <summary>Cockpit actuator read-back: flaps, trim, gear, parking brake.</summary>
public sealed class ControlsGroup : Group
{
    public double? FlapsPct { get; set; }
    /// <summary>−100 (full nose down) … +100 (full nose up) (sign verify, L1).</summary>
    public double? ElevatorTrimPct { get; set; }
    public bool? GearDown { get; set; }
    public bool? ParkingBrake { get; set; }
}

public sealed class TrafficTarget
{
    public int Id24 { get; set; }
    public string Callsign { get; set; } = "";
    public double LatDeg { get; set; }
    public double LonDeg { get; set; }
    public double? AltMslM { get; set; }
    public double? TrackTrueDeg { get; set; }
    public double? GsMps { get; set; }
    public double? VsMps { get; set; }
    public bool OnGround { get; set; }
    public string Category { get; set; } = "";
    public double TMono { get; set; }
}

public sealed class SimState
{
    public SimInfo Sim { get; set; } = new();
    public PosGroup Pos { get; set; } = new();
    public AttGroup Att { get; set; } = new();
    public AirGroup Air { get; set; } = new();
    public WindGroup Wind { get; set; } = new();
    public EngGroup[] Eng { get; set; } = [new EngGroup()];
    public FuelGroup Fuel { get; set; } = new();
    public ElecGroup Elec { get; set; } = new();
    public ApGroup Ap { get; set; } = new();
    public NavGroup Nav { get; set; } = new();
    public RadioGroup Radio { get; set; } = new();
    public ControlsGroup Controls { get; set; } = new();
    public List<TrafficTarget> Traffic { get; set; } = [];
    /// <summary>Monotonic time the traffic set was received.</summary>
    public double TrafficTMono { get; set; }

    /// <summary>
    /// A copy that shares the group objects. Writers replace groups, never mutate a
    /// published one, so readers on other threads always see a consistent group.
    /// </summary>
    public SimState ShallowCopy()
    {
        var c = (SimState)MemberwiseClone();
        c.Sim = Sim.Clone();
        return c;
    }
}
