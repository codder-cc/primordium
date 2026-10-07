using System;
using System.Linq;

namespace Primordium;

// `--climate [--seeds 1-8] [--days N]` (laws from --set): the climate cycles' schedule of each seed — the
// periods from the seed, the days a hemisphere's summer at 65° is below the ice-age threshold (when the
// glaciation would grow), and the days of the mega-eruptions. Pure functions of the seed and the tick: no
// world is stepped (World.ClimateCycles).
public sealed partial class World
{
    public static void ClimateReport(string[] args)
    {
        int i = Array.IndexOf(args, "--seeds"), d = Array.IndexOf(args, "--days");
        var seeds = i >= 0 ? Batch.ParseSeeds(args[i + 1]) : Enumerable.Range(1, 8).ToList();
        int days = d >= 0 ? int.Parse(args[d + 1]) : 40;
        Console.WriteLine($"climate cycles {(CyclesLaw ? "on" : "OFF")}: tilt {P.Tilt:F3}±{P.TiltAmp:F3} rad, ecc ≤ {P.EccAmp:F3}, drift ±{P.SunDriftAmp:P1}, threshold {P.IceAgeThreshold:P0}, eruptions {P.MegaEruptionRate:F3}/day, {days} days");
        foreach (int seed in seeds)
        {
            var w = new World(seed, 0, false) { AutoStrikes = false };
            float minN = 9, minS = 9, maxN = 0;
            int cold = 0;
            string spans = "";
            bool inCold = false;
            int coldFrom = 0;
            for (int k = 0; k <= days * 8; k++)
            {
                long t = (long)k * P.DayLen / 8;
                float n = w.SummerInsol(1, t), s = w.SummerInsol(-1, t);
                minN = Math.Min(minN, n); minS = Math.Min(minS, s); maxN = Math.Max(maxN, n);
                bool c = Math.Min(n, s) < P.IceAgeThreshold;
                if (c) cold++;
                if (c && !inCold) { inCold = true; coldFrom = k; }
                if ((!c || k == days * 8) && inCold) { inCold = false; spans += $" {coldFrom / 8.0:0.#}–{k / 8.0:0.#}"; }
            }
            var eruptions = Enumerable.Range(0, days).Where(day => w.MegaEruptionOfDay(day, out _)).Select(day => w.MegaEruptionOfDay(day, out long t) ? (t / (double)P.DayLen).ToString("0.0") : "").ToList();
            Console.WriteLine($"seed {seed}: tilt period {w.TiltPeriodDays:0} d (sign {(w.TiltAt(P.DayLen) >= P.Tilt ? "+" : "−")}), precession {w.PrecPeriodDays:0} d, eccentricity {w.EccPeriodDays:0} d, drift {w.SunDriftDays:0} d | " +
                              $"summer 65° N {minN:P0}…{maxN:P0}, S min {minS:P0}; below threshold {cold / 8.0:0.#} d:{(spans.Length > 0 ? spans : " none")} | mega-eruptions: {(eruptions.Count > 0 ? string.Join(", ", eruptions) : "none")}");
        }
    }
}
