using System;
using System.Linq;

namespace Primordium;

// `--sun [--seeds 1,2,3]`: the sky's tables (World.Sky, ROADMAP 1.1) — day length by latitude and season,
// the climate of a latitude through the year with the law and without, and the planet's mean photon
// supply over a year of a lifeless world (the PhotonK calibration), with the current laws against the
// old ones (Insolation, Transparency, Eclipses, Flares off and PhotonK 0.06).
public sealed partial class World
{
    public static void SunReport(string[] args)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int si = Array.IndexOf(args, "--seeds");
        var seeds = si >= 0 ? Batch.ParseSeeds(args[si + 1]) : new System.Collections.Generic.List<int> { 1, 2, 3 };
        var laws = ParamRegistry.Snapshot();
        float[] lats = { 0, 15, 30, 45, 60, 67, 75, 82 };
        string[] seasonNames = { "equinox", "N summer", "N winter" };
        float[] decls = { 0, P.Tilt, -P.Tilt };
        Console.WriteLine($"day length — share of the day with the sun up (tilt {P.Tilt} rad = {P.Tilt * 180 / MathF.PI:F1}°; the map reaches ±{0.46f * 180:F1}°), and with any light (twilight on):");
        Console.WriteLine("  latitude   " + string.Join("  ", seasonNames.Select(n => n.PadLeft(18))));
        foreach (float latDeg in lats)
        {
            float lat = latDeg * MathF.PI / 180;
            var cells = decls.Select(d =>
            {
                int lit = 0;
                for (int h = 0; h < 480; h++)
                    if (Insol(MathF.Sin(lat) * MathF.Sin(d) + MathF.Cos(lat) * MathF.Cos(d) * MathF.Cos(2 * MathF.PI * (h + 0.5f) / 480)) > 0) lit++;
                return $"{DayShare(lat, d),6:P0} ({lit / 480f,5:P0})".PadLeft(18);
            });
            Console.WriteLine($"  {latDeg,5:F0}°     " + string.Join("  ", cells));
        }
        // The climate of a latitude through a year: mean, coldest and warmest day, with the law and without.
        var w0 = new World(seeds[0], 0, false);
        Console.WriteLine("climate of a latitude (°C, before altitude and daylight): year mean / min / max — old law | cosine law");
        int[] rows = { 80, 70, 60, 50, 40, 30, 20, 10, 2 };
        foreach (int y in rows)
        {
            string Row(int law)
            {
                P.Insolation = law; w0.insolKey.Item1 = float.NaN;
                if (law != 0) w0.EnsureInsolNorms();
                var t = Enumerable.Range(0, 48).Select(j => w0.ClimateOf(y, P.Tilt * MathF.Sin(2 * MathF.PI * (j + 0.5f) / 48))).ToArray();
                return $"{t.Average(),6:F1} {t.Min(),6:F1} {t.Max(),6:F1}";
            }
            string o = Row(0), n = Row(1);
            ParamRegistry.Restore(laws);
            Console.WriteLine($"  row {y,3} ({Latitude(y) * 180 / MathF.PI,5:F1}°): {o} | {n}");
        }
        // The schedule of the first 12 000 ticks (pure functions of the seed and the tick).
        Console.WriteLine("schedule of the first 12 000 ticks: moon, eclipses (start ticks), solar cycle, activity, flares (start: peak power, length)");
        foreach (int seed in seeds.Count > 1 ? seeds : Enumerable.Range(1, 8).ToList())
        {
            var w = seed == seeds[0] ? w0 : new World(seed, 0, false);
            var ecl = new System.Collections.Generic.List<long>();
            bool was = false;
            for (long t = 0; t < 12000; t += 8) { bool on = w.EclipseAt(t, out _, out _); if (on && !was) ecl.Add(t); was = on; }
            var fl = new System.Collections.Generic.List<string>();
            double act = 0; float peak = 0; long start = -1;
            for (long t = 1; t <= 12000; t++)
            {
                float f = w.FlareAt(t);
                if (t % 100 == 0) act += w.ActivityAt(t) / 120;
                if (f > 0) { if (start < 0) { start = t; peak = 0; } peak = Math.Max(peak, f); }
                else if (start >= 0) { fl.Add($"{start}: {peak:F1}×{t - start}"); start = -1; }
            }
            Console.WriteLine(string.Create(inv, $"  seed {seed}: moon {w.MoonPeriodDays:F1} d, tilt {w.MoonTiltRad:F2}, eclipses [{string.Join(", ", ecl)}], next after 12000: {w.NextEclipse(12000)}; cycle {w.SolarCycleDays:F0} d, activity mean {act:P0}; flares [{string.Join("; ", fl)}]"));
        }
        // Photon supply over a year of a lifeless world (the planet's mean of what accrues, before PhotonCap).
        (double supply, double temp, double transp, double daylit) Year(int seed)
        {
            var w = new World(seed, 0, false) { AutoStrikes = false };
            double sum = 0, temp = 0, lit = 0; int samples = 0;
            int ticks = P.DayLen * P.YearDays;
            for (int t = 0; t < ticks; t++)
            {
                w.Step();
                if (w.Tick % P.LightEvery != 0) continue;
                double s = 0; int on = 0;
                for (int i = 0; i < N; i++) { s += w.Sun[i]; if (w.Sun[i] > 0) on++; }
                sum += s / N * P.PhotonK; lit += on / (double)N;
                if (w.Tick % 200 == 0) { temp += w.Temp.Average(); samples++; }
            }
            int updates = ticks / P.LightEvery;
            return (sum / updates, temp / samples, TranspLaw ? w.TranspMean : 1, lit / updates);
        }
        foreach (int seed in seeds)
        {
            ParamRegistry.Restore(new System.Collections.Generic.Dictionary<string, double>
                { ["Insolation"] = 0, ["Transparency"] = 0, ["Eclipses"] = 0, ["Flares"] = 0, ["ShadeK"] = 0, ["PhotonK"] = 0.06 });
            var old = Year(seed);
            ParamRegistry.Restore(laws);
            var now = Year(seed);
            Console.WriteLine(string.Create(inv, $"seed {seed}: photons per cell per tick over a year: old {old.supply:F5} (PhotonK 0.06), now {now.supply:F5} (PhotonK {P.PhotonK}) — {now.supply / old.supply - 1:+0.0%;-0.0%}; PhotonK for equal supply {P.PhotonK * old.supply / now.supply:F4} | mean T {old.temp:F1} → {now.temp:F1} °C | lit share {old.daylit:P1} → {now.daylit:P1} | transparency mean {now.transp:F3}"));
        }
        ParamRegistry.Restore(laws);
    }
}
