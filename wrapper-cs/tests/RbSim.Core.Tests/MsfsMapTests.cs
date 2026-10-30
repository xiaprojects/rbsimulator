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
// MsfsMapTests.cs: units, MSFS mapping, data layout and detector tests.

using RbSim.Core;

namespace RbSim.Core.Tests;

/// <summary>A cruise frame in request units. The signs here are the documented ones; L2 captures replace them.</summary>
internal static class Frames
{
    public static double[] Cruise()
    {
        var r = new double[MsfsDatums.User.Length];
        r[(int)U.Lat] = 43.18528; r[(int)U.Lon] = 12.136674; r[(int)U.AltMsl] = 914.4;
        r[(int)U.GroundSpeed] = 51.444; r[(int)U.GpsTrack] = 271.4;
        r[(int)U.VelWorldX] = -51.4; r[(int)U.VelWorldY] = -1.28; r[(int)U.VelWorldZ] = 1.2;
        r[(int)U.VerticalSpeed] = -1.27; r[(int)U.Magvar] = 2.8;
        r[(int)U.Pitch] = -2.1; r[(int)U.Bank] = 15.3; // MSFS: + nose down, + left wing down
        r[(int)U.HdgTrue] = 271.4; r[(int)U.HdgMag] = 268.6; r[(int)U.TurnRate] = -3.0;
        r[(int)U.Ball] = 12.7; r[(int)U.GForce] = 1.03;
        r[(int)U.Ias] = 50.6; r[(int)U.Tas] = 53.0; r[(int)U.PressureAlt] = 908.3;
        r[(int)U.AmbientPressure] = 908.0; r[(int)U.AmbientTemp] = 12; r[(int)U.WindDir] = 250; r[(int)U.WindVel] = 5;
        r[(int)U.Kohlsman] = 1017.0; r[(int)U.OnGround] = 0; r[(int)U.SlewActive] = 0;
        r[(int)U.ApMaster] = 1; r[(int)U.ApHdgLock] = 1; r[(int)U.ApHdgLockDir] = 268; r[(int)U.ApAltLockVar] = 3500;
        r[(int)U.EngRpm] = 2350; r[(int)U.EngMap] = 24; r[(int)U.EngOilTemp] = 92; r[(int)U.EngOilPress] = 58;
        r[(int)U.EngFuelPress] = 4; r[(int)U.EngCht] = 165; r[(int)U.EngEgt] = 700; r[(int)U.EngFuelFlow] = 8;
        r[(int)U.FuelLeft] = 12; r[(int)U.FuelRight] = 11.5; r[(int)U.BusVolts] = 28.1;
        r[(int)U.ZuluTime] = 10 * 3600 + 15 * 60 + 30; r[(int)U.ZuluDay] = 26; r[(int)U.ZuluMonth] = 10; r[(int)U.ZuluYear] = 2026;
        r[(int)U.SimulationTime] = 1234.5;
        return r;
    }

    public static SimState Mapped(double now = 100, double[]? raw = null)
    {
        var s = new SimState();
        s.Sim.Source = "msfs";
        s.Sim.Connected = true;
        MsfsMap.ApplyUser(s, raw ?? Cruise(), now, new MsfsProfile());
        return s;
    }
}

public class UnitsTests
{
    // Covers: RB-SIM-MOD-010
    [Theory]
    [InlineData(1.0, 0.3048)]
    [InlineData(3000.0, 914.4)]
    public void FeetToMetres(double ft, double m) => Assert.Equal(m, ft * Units.FtToM, 9);

    // Covers: RB-SIM-MOD-010
    [Fact]
    public void GoldenValues()
    {
        Assert.Equal(51.444444, 100 * Units.KtToMps, 5);
        Assert.Equal(100.0, Units.MpsToKt(100 * Units.KtToMps), 9);
        Assert.Equal(185.2, Units.MpsToKmh(100 * Units.KtToMps), 9);
        Assert.Equal(3.785411784, Units.UsGalToL, 12);
        Assert.Equal(101320.76, 29.92 * Units.InHgToPa, 1);
        Assert.Equal(6894.757, Units.PsiToPa, 3);
        Assert.Equal(196.850394, Units.MpsToFpm(1.0), 5);
        Assert.Equal(3.280840, Units.MpsToFps(1.0), 5);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(360, 0)]
    [InlineData(-90, 270)]
    [InlineData(725, 5)]
    [InlineData(-1e-15, 0)]
    public void Wrap360(double input, double expected) => Assert.Equal(expected, Units.Wrap360(input), 9);
}

public class MsfsMapTests
{
    [Fact]
    public void DatumTablesMatchTheirEnums()
    {
        Assert.Equal(Enum.GetValues<U>().Length, MsfsDatums.User.Length);
        Assert.Equal(Enum.GetValues<T>().Length, MsfsDatums.Traffic.Length);
        Assert.Equal("SIMULATION TIME", MsfsDatums.User[(int)U.SimulationTime].Name);
        Assert.Equal("ATC FLIGHT NUMBER", MsfsDatums.Traffic[(int)T.AtcFlightNumber].Name);
    }

    // Covers: RB-SIM-ADP-020
    [Fact]
    public void EveryNumericDatumNamesAUnit()
    {
        foreach (var d in MsfsDatums.User.Concat(MsfsDatums.Traffic).Where(d => d.Type == DatumType.Float64))
            Assert.False(string.IsNullOrWhiteSpace(d.Unit), d.Name);
    }

    // Covers: RB-SIM-MOD-030, RB-SIM-MSF-030
    [Fact]
    public void AttitudeSignsFollowProtocolConventions()
    {
        var s = Frames.Mapped();
        Assert.Equal(2.1, s.Att.PitchDeg!.Value, 9);   // MSFS -2.1 (nose down +) → nose up 2.1
        Assert.Equal(-15.3, s.Att.RollDeg!.Value, 9);  // MSFS 15.3 left wing down → roll -15.3
        Assert.Equal(-3.0, s.Att.TurnRateDps!.Value, 9);
        Assert.Equal(1.0, s.Att.SlipDeg!.Value, 9);    // 12.7 / 127 × 10°
        Assert.Equal(271.4, s.Att.HdgTrueDeg!.Value, 9);
        Assert.True(s.Att.Valid);
    }

    // Covers: RB-SIM-MOD-010
    [Fact]
    public void ConvertsToSi()
    {
        var s = Frames.Mapped();
        Assert.Equal(90_800, s.Air.StaticPa!.Value, 6);
        Assert.Equal(101_700, s.Air.SimQnhPa!.Value, 6);
        Assert.Equal(24 * 3386.389, s.Eng[0].MapPa!.Value, 6);
        Assert.Equal(58 * 6894.757293168, s.Eng[0].OilPressPa!.Value, 6);
        Assert.Equal(8 * 3.785411784, s.Eng[0].FuelFlowLph!.Value, 9);
        Assert.Equal(12 * 3.785411784, s.Fuel.LeftL!.Value, 9);
        Assert.Equal(3500 * 0.3048, s.Ap.AltSelM!.Value, 9);
        Assert.Equal(-1.28, s.Pos.VsGpsMps!.Value, 9);     // geometric
        Assert.Equal(-1.27, s.Air.VsBaroMps!.Value, 9);    // indicated
    }

    // Covers: RB-SIM-MSF-040
    [Fact]
    public void UtcComesFromZuluVariables()
    {
        var s = Frames.Mapped();
        Assert.Equal(new DateTime(2026, 10, 26, 10, 15, 30, DateTimeKind.Utc), s.Sim.Utc);
        var raw = Frames.Cruise();
        raw[(int)U.ZuluYear] = double.NaN;
        Assert.Null(Frames.Mapped(raw: raw).Sim.Utc);
    }

    // Covers: RB-SIM-MSF-130
    [Fact]
    public void RejectedDatumMakesOnlyThatFieldInvalid()
    {
        var raw = Frames.Cruise();
        raw[(int)U.GForce] = double.NaN;
        raw[(int)U.BusVolts] = double.NaN;
        var s = Frames.Mapped(raw: raw);
        Assert.Null(s.Att.GLoad);
        Assert.True(s.Att.Valid);
        Assert.False(s.Elec.Valid);
        Assert.Null(s.Elec.BusV);
        Assert.Equal(2350, s.Eng[0].Rpm);
    }

    [Fact]
    public void MissingKeyFieldInvalidatesGroup()
    {
        var raw = Frames.Cruise();
        raw[(int)U.Pitch] = double.NaN;
        raw[(int)U.Lat] = double.NaN;
        var s = Frames.Mapped(raw: raw);
        Assert.False(s.Att.Valid);
        Assert.False(s.Pos.Valid);
    }

    [Theory]
    [InlineData(double.NaN, 1.0, 0.0, 90.0)]   // east
    [InlineData(double.NaN, 0.0, -1.0, 180.0)] // south
    [InlineData(double.NaN, -1.0, 1.0, 315.0)]
    [InlineData(10.0, -1.0, 0.0, 10.0)]        // GPS track wins
    public void TrackFallsBackToWorldVelocity(double gps, double east, double north, double expected) =>
        Assert.Equal(expected, MsfsMap.Track(gps, east, north)!.Value, 9);

    [Fact]
    public void TrackIsInvalidWhenStationaryWithoutGps() => Assert.Null(MsfsMap.Track(double.NaN, 0, 0));

    // Covers: RB-SIM-MOD-060
    [Fact]
    public void CylindersAreSynthesisedFromProfileSpread()
    {
        var p = new MsfsProfile { ChtSpreadC = [0, 7, -5, 3] };
        var s = new SimState();
        MsfsMap.ApplyUser(s, Frames.Cruise(), 1, p);
        Assert.Equal([165, 172, 160, 168], s.Eng[0].ChtC.Select(c => c!.Value));
        Assert.True(s.Eng[0].CylindersSynthetic);
    }

    [Fact]
    public void TrafficCallsignAndPseudoIcao()
    {
        var num = new double[MsfsDatums.Traffic.Length];
        num[(int)T.Lat] = 43.2; num[(int)T.Lon] = 12.1; num[(int)T.AltMsl] = 1000;
        num[(int)T.VelWorldX] = 10; num[(int)T.VelWorldZ] = 0; num[(int)T.HdgTrue] = 45;
        var str = new string?[MsfsDatums.Traffic.Length];
        str[(int)T.AtcId] = "X-ABCD";
        var t = MsfsMap.MapTraffic(num, str, 7, 5)!;
        Assert.Equal("X-ABCD", t.Callsign);
        Assert.Equal(90.0, t.TrackTrueDeg!.Value, 9);
        Assert.Equal(MsfsMap.PseudoIcao(7), t.Id24);

        str[(int)T.AtcAirline] = "RBX"; str[(int)T.AtcFlightNumber] = "123";
        Assert.Equal("RBX123", MsfsMap.MapTraffic(num, str, 7, 5)!.Callsign);
    }

    [Fact]
    public void PseudoIcaoIsStableAndInReservedRange()
    {
        var ids = Enumerable.Range(1, 2000).Select(i => MsfsMap.PseudoIcao((uint)i)).ToList();
        Assert.All(ids, id => Assert.Equal(MsfsMap.PseudoIcaoBase, id & ~MsfsMap.PseudoIcaoMask));
        Assert.Equal(MsfsMap.PseudoIcao(42), MsfsMap.PseudoIcao(42));
        Assert.True(ids.Distinct().Count() > 1990);
    }
}

public class DataLayoutTests
{
    static byte[] Block(params object[] values)
    {
        var ms = new MemoryStream();
        foreach (var v in values)
        {
            if (v is double d) ms.Write(BitConverter.GetBytes(d));
            else if (v is (string s, int size))
            {
                var b = new byte[size];
                System.Text.Encoding.Latin1.GetBytes(s).CopyTo(b, 0);
                ms.Write(b);
            }
        }
        return ms.ToArray();
    }

    // Covers: RB-SIM-MSF-130
    [Fact]
    public void RejectedDatumsAreSkippedAndReturnInvalid()
    {
        var layout = new DataLayout([new("A", "x"), new("B", "x"), new("S", null, DatumType.String8), new("C", "x")]);
        Assert.True(layout.Reject(1));
        Assert.False(layout.Reject(1));
        Assert.Equal(24, layout.BlockSize);
        layout.Decode(Block(1.5, ("ABC", 8), 3.5), out var num, out var str);
        Assert.Equal(1.5, num[0]);
        Assert.True(double.IsNaN(num[1]));
        Assert.Equal("ABC", str[2]);
        Assert.Equal(3.5, num[3]);
    }

    [Fact]
    public void ShortBlockLeavesTheRestInvalid()
    {
        var layout = new DataLayout([new("A", "x"), new("B", "x")]);
        layout.Decode(Block(1.0), out var num, out _);
        Assert.Equal(1.0, num[0]);
        Assert.True(double.IsNaN(num[1]));
    }
}

public class DetectorTests
{
    // Covers: RB-SIM-MSF-050
    [Fact]
    public void PauseFromEventFlags()
    {
        var p = new PauseDetector();
        p.OnPauseEvent(4); // Active Pause
        Assert.True(p.Paused(0));
        p.OnPauseEvent(0);
        Assert.False(p.Paused(0));
    }

    // Covers: RB-SIM-MSF-050
    [Fact]
    public void PauseInferredWhenSimulationTimeStopsWhileFramesArrive()
    {
        var p = new PauseDetector();
        for (int i = 0; i <= 20; i++) p.OnFrame(100 + i * 0.05, i * 0.05);
        Assert.False(p.Paused(1.0));
        for (int i = 21; i <= 32; i++) p.OnFrame(200, i * 0.05); // advances once at 1.05, then frozen
        Assert.False(p.Paused(1.5));
        Assert.True(p.Paused(1.6));
        // Frames stopping is not a pause: the link monitor and staleness handle that.
        Assert.False(p.Paused(3.0));
    }

    // Covers: RB-SIM-MSF-060
    [Fact]
    public void LinkLostWithoutFramesForTwoSeconds()
    {
        var l = new LinkMonitor();
        Assert.False(l.Connected(0, false));
        l.OnOpen();
        l.OnFrame(10);
        Assert.True(l.Connected(11.9, false));
        Assert.False(l.Connected(12.1, false));
        Assert.True(l.Connected(12.1, pauseEvent: true));
        l.OnSimEvent(0);
        Assert.False(l.Connected(10.1, false));
        l.OnSimEvent(1);
        l.OnClose();
        Assert.False(l.Connected(10.1, false));
    }

    // Covers: RB-SIM-MOD-050
    [Fact]
    public void TeleportOnSlewEventOrJump()
    {
        var d = new TeleportDetector();
        d.OnFrame(43.0, 12.0, false, 0);
        d.OnFrame(43.0001, 12.0, false, 0.05);
        Assert.Equal(0, d.Seq);
        d.OnFrame(44.0, 12.0, false, 0.1); // ~111 km in 50 ms
        Assert.Equal(1, d.Seq);
        d.OnFrame(44.0, 12.0, true, 0.15);
        d.OnFrame(44.0, 12.0, true, 0.2);
        Assert.Equal(2, d.Seq);
        d.OnEvent();
        Assert.Equal(3, d.Seq);
    }
}
