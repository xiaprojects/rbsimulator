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
// SyntheticSource.cs: a coordinated right turn at 100 kt with an emulated cockpit, for exercising the server,
// the control API and clients without a simulator.

using RbSim.Core;

namespace RbSim.Msfs;

public sealed class SyntheticSource(StateStore store, ActuatorManager actuators) : ISource
{
    const double Hz = 20;
    const double CenterLat = 43.185280, CenterLon = 12.136674;
    const double TasMps = 100 * Units.KtToMps;
    const double BankDeg = 15;
    const double AltM = 3000 * Units.FtToM;
    const double MagvarDeg = 3.0;

    Thread? _thread;
    volatile bool _stop;

    public void Start()
    {
        store.Update(s =>
        {
            s.Sim.SimName = "synthetic";
            s.Sim.SimVersion = "1";
            s.Sim.Aircraft = "Synthetic single piston";
            s.Sim.Connected = true;
        });
        _thread = new Thread(Run) { IsBackground = true, Name = "synthetic" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(1));
    }

    void Run()
    {
        double t0 = StateStore.Now;
        double omega = 9.80665 * Math.Tan(BankDeg * Math.PI / 180) / TasMps; // rad/s, + right
        double radiusM = TasMps / omega;
        double nextTraffic = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1 / Hz));
        while (!_stop && timer.WaitForNextTickAsync().AsTask().GetAwaiter().GetResult())
        {
            double now = StateStore.Now, t = now - t0;
            double hdg = Units.Wrap360(omega * t * 180 / Math.PI);
            // Clockwise circle: heading 0 at the west point, moving north.
            double ang = omega * t;
            double north = radiusM * Math.Sin(ang), east = -radiusM * Math.Cos(ang);
            var (lat, lon) = Offset(CenterLat, CenterLon, north, east);
            double vs = 1.0 * Math.Sin(t / 20);
            foreach (var w in actuators.Due(store.Snapshot, now)) Apply(w, now);

            store.Update(s =>
            {
                s.Sim.Utc = DateTime.UtcNow;
                s.Sim.Paused = false;
                s.Pos = new PosGroup
                {
                    Valid = true,
                    TMono = now,
                    LatDeg = lat,
                    LonDeg = lon,
                    AltMslM = AltM + 20 * Math.Cos(t / 20),
                    TrackTrueDeg = hdg,
                    GsMps = TasMps,
                    VsGpsMps = vs,
                    MagvarDeg = MagvarDeg,
                };
                s.Att = new AttGroup
                {
                    Valid = true,
                    TMono = now,
                    PitchDeg = 2 + 1.5 * Math.Sin(t / 20),
                    RollDeg = BankDeg,
                    HdgTrueDeg = hdg,
                    HdgMagDeg = Units.Wrap360(hdg - MagvarDeg),
                    TurnRateDps = omega * 180 / Math.PI,
                    SlipDeg = 0,
                    GLoad = 1 / Math.Cos(BankDeg * Math.PI / 180),
                };
                s.Air = new AirGroup
                {
                    Valid = true,
                    TMono = now,
                    IasMps = TasMps * 0.95,
                    TasMps = TasMps,
                    PressureAltM = AltM - 30,
                    StaticPa = 90_800,
                    VsBaroMps = vs,
                    OatC = 9,
                    SimQnhPa = _c.QnhHpa * Units.HPaToPa,
                };
                s.Eng = [new EngGroup
                {
                    Valid = true, TMono = now, Rpm = 2350, MapPa = 24 * Units.InHgToPa, OilTempC = 92,
                    OilPressPa = 4 * Units.BarToPa, FuelPressPa = 0.3 * Units.BarToPa,
                    ChtC = [165, 172, 160, 168], EgtC = [690, 712, 705, 698], CylindersSynthetic = true,
                    FuelFlowLph = 28,
                }];
                s.Fuel = new FuelGroup { Valid = true, TMono = now, LeftL = 45, RightL = 44 };
                s.Elec = new ElecGroup { Valid = true, TMono = now, BusV = 13.8 };
                PublishCockpit(s, now);
            }, newFrame: true);
            store.Status.Frames.Tick(now);
            store.Status.Published.Tick(now);

            if (now >= nextTraffic)
            {
                nextTraffic = now + 1;
                var (tlat, tlon) = Offset(CenterLat, CenterLon, 3000 * Math.Cos(t / 60), 3000 * Math.Sin(t / 60));
                var target = new TrafficTarget
                {
                    Id24 = MsfsMap.PseudoIcao(42),
                    Callsign = "AI0042",
                    LatDeg = tlat,
                    LonDeg = tlon,
                    AltMslM = 3500 * Units.FtToM,
                    TrackTrueDeg = Units.Wrap360(t / 60 * 180 / Math.PI + 90),
                    GsMps = 110 * Units.KtToMps,
                    VsMps = 0,
                    OnGround = false,
                    TMono = now,
                };
                store.Update(s => { s.Traffic = [target]; s.TrafficTMono = now; });
            }
        }
    }


    /// <summary>The emulated cockpit the control API writes to; only the source thread touches it.</summary>
    sealed class Cockpit
    {
        public double Com1A = 118_100_000, Com1S = 121_500_000, Com2A = 122_800_000, Com2S = 119_000_000;
        public double Nav1A = 110_500_000, Nav1S = 113_900_000, Nav2A = 114_200_000, Nav2S = 108_100_000;
        public int SquawkCode = 7000, XpdrState = 4;
        public double IdentUntil = double.NegativeInfinity;
        public bool ApMaster, HdgMode, NavMode, AltHold, VsHold, GearDown = true, ParkingBrake;
        public double HdgBug = 270, AltSelFt = 3500, VsSelFpm, QnhHpa = 1017, FlapsPct, TrimPct;
    }

    readonly Cockpit _c = new();

    /// <summary>Applies a key event the way MSFS would. Flaps snap to 4 detents, so a 30 % request reads back 25 %.</summary>
    void Apply(ActuatorWrite w, double now)
    {
        double d = w.Data;
        int signed = unchecked((int)w.Data);
        switch (w.Event)
        {
            case "COM_RADIO_SET_HZ": _c.Com1A = d; break;
            case "COM_STBY_RADIO_SET_HZ": _c.Com1S = d; break;
            case "COM2_RADIO_SET_HZ": _c.Com2A = d; break;
            case "COM2_STBY_RADIO_SET_HZ": _c.Com2S = d; break;
            case "NAV1_RADIO_SET_HZ": _c.Nav1A = d; break;
            case "NAV1_STBY_SET_HZ": _c.Nav1S = d; break;
            case "NAV2_RADIO_SET_HZ": _c.Nav2A = d; break;
            case "NAV2_STBY_SET_HZ": _c.Nav2S = d; break;
            case "XPNDR_SET": _c.SquawkCode = Squawk.FromBco16(d) ?? _c.SquawkCode; break;
            case "XPNDR_IDENT_ON": _c.IdentUntil = now + 18; break;
            case "AUTOPILOT_ON": _c.ApMaster = true; break;
            case "AUTOPILOT_OFF": _c.ApMaster = false; break;
            case "AP_HDG_HOLD_ON": _c.HdgMode = true; _c.NavMode = false; break;
            case "AP_HDG_HOLD_OFF": _c.HdgMode = false; break;
            case "HEADING_BUG_SET": _c.HdgBug = d % 360; break;
            case "AP_NAV1_HOLD_ON": _c.NavMode = true; _c.HdgMode = false; break;
            case "AP_NAV1_HOLD_OFF": _c.NavMode = false; break;
            case "AP_ALT_HOLD_ON": _c.AltHold = true; _c.VsHold = false; break;
            case "AP_ALT_HOLD_OFF": _c.AltHold = false; break;
            case "AP_ALT_VAR_SET_ENGLISH": _c.AltSelFt = d; break;
            case "AP_VS_ON": _c.VsHold = true; _c.AltHold = false; break;
            case "AP_VS_OFF": _c.VsHold = false; break;
            case "AP_VS_VAR_SET_ENGLISH": _c.VsSelFpm = signed; break;
            case "KOHLSMAN_SET": _c.QnhHpa = d / 16; break;
            case "FLAPS_SET": _c.FlapsPct = Math.Round(d / 16383 * 4) * 25; break;
            case "ELEVATOR_TRIM_SET": _c.TrimPct = signed / 16383.0 * 100; break;
            case "GEAR_DOWN": _c.GearDown = true; break;
            case "GEAR_UP": _c.GearDown = false; break;
            case "PARKING_BRAKE_SET": _c.ParkingBrake = w.Data != 0; break;
        }
    }

    void PublishCockpit(SimState s, double now)
    {
        s.Ap = new ApGroup
        {
            Valid = true,
            TMono = now,
            Master = _c.ApMaster,
            HdgMode = _c.HdgMode,
            HdgBugMagDeg = _c.HdgBug,
            AltSelM = _c.AltSelFt * Units.FtToM,
            AltHold = _c.AltHold,
            VsHold = _c.VsHold,
            VsSelMps = _c.VsSelFpm * Units.FtToM / 60,
            NavMode = _c.NavMode,
        };
        s.Radio = new RadioGroup
        {
            Valid = true,
            TMono = now,
            Com1ActiveHz = _c.Com1A,
            Com1StandbyHz = _c.Com1S,
            Com2ActiveHz = _c.Com2A,
            Com2StandbyHz = _c.Com2S,
            Nav1ActiveHz = _c.Nav1A,
            Nav1StandbyHz = _c.Nav1S,
            Nav2ActiveHz = _c.Nav2A,
            Nav2StandbyHz = _c.Nav2S,
            Nav1RadialDeg = 123,
            Nav1Valid = true,
            XpdrCode = _c.SquawkCode,
            XpdrState = _c.XpdrState,
            XpdrIdent = now < _c.IdentUntil,
        };
        s.Controls = new ControlsGroup
        {
            Valid = true,
            TMono = now,
            FlapsPct = _c.FlapsPct,
            ElevatorTrimPct = _c.TrimPct,
            GearDown = _c.GearDown,
            ParkingBrake = _c.ParkingBrake,
        };
    }
    static (double lat, double lon) Offset(double lat, double lon, double northM, double eastM)
    {
        const double R = 6371008.8;
        double dLat = northM / R * 180 / Math.PI;
        double dLon = eastM / (R * Math.Cos(lat * Math.PI / 180)) * 180 / Math.PI;
        return (lat + dLat, lon + dLon);
    }
}
