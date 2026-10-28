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
// DeviceLines.cs: device profiles and the per-device sentence schedule (protocol §4.4, RB-SIM-ESP-070).

namespace RbSim.Core;

public enum Sentence { Gprmc, Gpgga, Pflau, Pflaa, Rbatt, Rbadc, Rbems, Rbcyl }

public static class DeviceProfiles
{
    /// <summary>
    /// Rates in Hz, protocol §4.4. nmea-flarm is GPS NMEA plus FLARM only, which today's
    /// USB firmware templates already parse ($RBATT/$RBADC need a SIM firmware build).
    /// rb04 follows roastbeef ems.go: $RBEMS every 200 ms, $RBCYL about every 2 s.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<Sentence, double>> All =
        new Dictionary<string, IReadOnlyDictionary<Sentence, double>>(StringComparer.OrdinalIgnoreCase)
        {
            ["rb02"] = new Dictionary<Sentence, double>
            {
                [Sentence.Rbatt] = 20,
                [Sentence.Rbadc] = 10,
                [Sentence.Gprmc] = 5,
                [Sentence.Gpgga] = 5,
            },
            ["rb04"] = new Dictionary<Sentence, double>
            {
                [Sentence.Rbems] = 5,
                [Sentence.Rbcyl] = 0.5,
            },
            ["rb05"] = new Dictionary<Sentence, double>
            {
                [Sentence.Gprmc] = 1,
                [Sentence.Pflau] = 1,
                [Sentence.Pflaa] = 1,
            },
            ["rb07"] = new Dictionary<Sentence, double>
            {
                [Sentence.Rbatt] = 20,
                [Sentence.Rbadc] = 10,
                [Sentence.Gprmc] = 5,
                [Sentence.Gpgga] = 5,
                [Sentence.Pflau] = 1,
                [Sentence.Pflaa] = 1,
            },
            ["nmea-flarm"] = new Dictionary<Sentence, double>
            {
                [Sentence.Gprmc] = 5,
                [Sentence.Gpgga] = 5,
                [Sentence.Pflau] = 1,
                [Sentence.Pflaa] = 1,
            },
        };

    public static Sentence ParseSentence(string name) =>
        Enum.TryParse<Sentence>(name, ignoreCase: true, out var s) && Enum.IsDefined(s)
            ? s
            : throw new ArgumentException($"unknown sentence \"{name}\" (known: {string.Join(", ", Enum.GetNames<Sentence>().Select(n => n.ToLowerInvariant()))})");

    /// <summary>The profile's rates, with per-sentence overrides; a rate of 0 removes a sentence.</summary>
    public static Dictionary<Sentence, double> Resolve(string profile, IReadOnlyDictionary<string, double>? overrides)
    {
        if (!All.TryGetValue(profile, out var baseRates))
            throw new ArgumentException($"unknown device profile \"{profile}\" (known: {string.Join(", ", All.Keys)})");
        var rates = new Dictionary<Sentence, double>(baseRates);
        foreach (var (name, hz) in overrides ?? new Dictionary<string, double>())
        {
            var s = ParseSentence(name);
            if (hz <= 0) rates.Remove(s);
            else rates[s] = Math.Min(hz, 50);
        }
        return rates;
    }
}

/// <summary>
/// Produces the lines due for one device at each tick. Sentence order inside a tick is fixed:
/// GPS first, then the $PFLAA lines followed by one $PFLAU (the order roastbeef traffic.go
/// sends them to RB-05), then the RB sentences.
/// A schedule that falls behind restarts from now instead of sending a burst (latest wins).
/// </summary>
public sealed class DeviceLineGenerator(IReadOnlyDictionary<Sentence, double> rates, int maxFlarmTargets = Nmea.MaxFlarmTargets, EmsUnits? ems = null)
{
    static readonly Sentence[] Order =
        [Sentence.Gprmc, Sentence.Gpgga, Sentence.Pflaa, Sentence.Pflau, Sentence.Rbatt, Sentence.Rbadc, Sentence.Rbems, Sentence.Rbcyl];

    readonly EmsUnits _ems = ems ?? new EmsUnits();

    readonly Dictionary<Sentence, double> _next = [];

    public IReadOnlyDictionary<Sentence, double> Rates { get; } = rates;

    public List<string> Due(SimState s, double now)
    {
        var lines = new List<string>();
        List<Nmea.FlarmTarget>? targets = null;
        foreach (var k in Order)
        {
            if (!Rates.TryGetValue(k, out double hz) || hz <= 0) continue;
            double period = 1.0 / hz;
            bool scheduled = _next.TryGetValue(k, out double next);
            if (scheduled && now < next) continue;
            _next[k] = (!scheduled || now - next > period) ? now + period : next + period;

            switch (k)
            {
                case Sentence.Gprmc:
                    if (Nmea.Rmc(s, now) is { } rmc) lines.Add(rmc);
                    break;
                case Sentence.Gpgga:
                    if (Nmea.Gga(s, now) is { } gga) lines.Add(gga);
                    break;
                case Sentence.Pflau:
                    targets ??= Nmea.FlarmTargets(s, now, maxFlarmTargets);
                    lines.Add(Nmea.Pflau(s, now, targets.Count));
                    break;
                case Sentence.Pflaa:
                    targets ??= Nmea.FlarmTargets(s, now, maxFlarmTargets);
                    lines.AddRange(targets.Select(Nmea.Pflaa));
                    break;
                case Sentence.Rbatt:
                    lines.Add(Nmea.Rbatt(s, now));
                    break;
                case Sentence.Rbadc:
                    lines.Add(Nmea.Rbadc(s, now));
                    break;
                case Sentence.Rbems:
                    if (Nmea.Rbems(s, now, _ems) is { } rbems) lines.Add(rbems);
                    break;
                case Sentence.Rbcyl:
                    if (Nmea.Rbcyl(s, now) is { } rbcyl) lines.Add(rbcyl);
                    break;
            }
        }
        return lines;
    }
}
