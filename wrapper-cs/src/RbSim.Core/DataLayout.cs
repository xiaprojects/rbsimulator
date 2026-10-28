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
// DataLayout.cs: decodes a SimConnect data block for a definition with rejected datums removed (RB-SIM-MSF-130).

using System.Buffers.Binary;
using System.Text;

namespace RbSim.Core;

/// <summary>
/// A data definition as sent to SimConnect: the full datum list minus the rejected ones.
/// The block SimConnect returns is the active datums packed back to back (pack 1).
/// Decoding puts every value back at its full-list index, with NaN or null for rejected ones,
/// so a rejected datum makes only that field invalid.
/// </summary>
public sealed class DataLayout(Datum[] all)
{
    readonly HashSet<int> _rejected = [];

    public Datum[] All { get; } = all;
    public IReadOnlyCollection<int> Rejected => _rejected;

    public IEnumerable<int> Active => Enumerable.Range(0, All.Length).Where(i => !_rejected.Contains(i));

    public int BlockSize => Active.Sum(i => All[i].Size);

    /// <summary>Returns true if the datum was newly rejected (the definition must be rebuilt).</summary>
    public bool Reject(int index) => index >= 0 && index < All.Length && _rejected.Add(index);

    public void Decode(ReadOnlySpan<byte> block, out double[] numbers, out string?[] strings)
    {
        numbers = new double[All.Length];
        strings = new string?[All.Length];
        Array.Fill(numbers, double.NaN);
        int off = 0;
        foreach (int i in Active)
        {
            var d = All[i];
            if (off + d.Size > block.Length) break; // short block: the rest stays invalid
            var span = block.Slice(off, d.Size);
            if (d.Type == DatumType.Float64)
            {
                numbers[i] = BinaryPrimitives.ReadDoubleLittleEndian(span);
            }
            else
            {
                int z = span.IndexOf((byte)0);
                strings[i] = Encoding.Latin1.GetString(z >= 0 ? span[..z] : span);
            }
            off += d.Size;
        }
    }
}
