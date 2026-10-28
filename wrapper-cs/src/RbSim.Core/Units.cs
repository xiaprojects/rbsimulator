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
// Units.cs: the only place where unit conversions live in the C# wrapper (protocol §2.1, RB-SIM-MOD-010).

namespace RbSim.Core;

public static class Units
{
    // Exact definitions; the golden values in the tests are the same as rbsim/units.py.
    public const double FtToM = 0.3048;
    public const double KtToMps = 1852.0 / 3600.0;
    public const double UsGalToL = 3.785411784;
    public const double InHgToPa = 3386.389;
    public const double PsiToPa = 6894.757293168;
    public const double HPaToPa = 100.0;
    public const double BarToPa = 100000.0;

    public static double MToFt(double m) => m / FtToM;
    public static double MpsToKt(double mps) => mps / KtToMps;
    public static double MpsToFps(double mps) => mps / FtToM;
    public static double MpsToFpm(double mps) => mps / FtToM * 60.0;
    public static double PaToInHg(double pa) => pa / InHgToPa;
    public static double PaToPsi(double pa) => pa / PsiToPa;
    public static double PaToBar(double pa) => pa / BarToPa;

    /// <summary>ISA static pressure at a pressure altitude (troposphere, valid below 11 km).</summary>
    public static double IsaPressurePa(double pressureAltM) =>
        101325.0 * Math.Pow(1.0 - 2.25577e-5 * pressureAltM, 5.25588);

    /// <summary>Normalises an angle to 0 ≤ x &lt; 360.</summary>
    public static double Wrap360(double deg)
    {
        double d = deg % 360.0;
        if (d < 0) d += 360.0;
        if (d >= 360.0) d -= 360.0; // -1e-15 + 360 rounds to 360
        return d;
    }

    /// <summary>Converts a NaN (rejected or missing datum) to null.</summary>
    public static double? Finite(double v) => double.IsFinite(v) ? v : null;
}
