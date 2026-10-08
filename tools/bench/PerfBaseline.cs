using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Primordium;

// Speed regressions on one fixed workload (ROADMAP 10.7).
//
//   --perf-baseline [--fixture path.sav] [--ticks 2000] [--warmup 200] [--repeat 1] [--csv perf.csv] [--label text] [--set Name=value ...]
//   --perf-baseline --make [--fixture path.sav] [--seed 2] [--target 15000] [--max-ticks 30000] [--step 1000]
// The reference "boom" save is made, not stored: --make runs the default world of `seed` until it holds
// `target` bodies (checked every `step` ticks; or until max-ticks) and saves it. The save keeps the whole
// state, so every build continues the same world from it (as long as it reads the save format). Without
// --make the fixture is loaded `repeat` times; each time `warmup` ticks run unmeasured (JIT, tiered PGO),
// then `ticks` ticks are measured. Printed in a fixed format (one key per line, invariant numbers; the
// median over repeats): wall and CPU ms/tick, every World.Prof stage and World.Detail lap, agent tiles'
// work and slowest tile, allocation and collections, population, the state hash at the end (the same hash
// before and after an optimisation proves the same trajectory), the machine and the load average.
// --csv appends one row per repeat. Compare builds on the same fixture, Release, an idle machine.
public static class PerfBaseline
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string DefaultFixture = "tools/bench/fixtures/boom.sav";

    [DllImport("libc", EntryPoint = "getloadavg")]
    static extern int GetLoadAvg([Out] double[] loadavg, int nelem);

    static string LoadAverage()
    {
        try
        {
            var l = new double[3];
            return GetLoadAvg(l, 3) == 3 ? string.Join(" ", l.Select(x => x.ToString("F2", Inv))) : "-";
        }
        catch (Exception) { return "-"; }
    }

    static string Arg(string[] args, string name, string def)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
    }

    public static void Run(string[] args)
    {
        string fixture = Arg(args, "--fixture", DefaultFixture);
        if (Array.IndexOf(args, "--make") >= 0) { Make(args, fixture); return; }
        if (!File.Exists(fixture))
        {
            Console.Error.WriteLine(Loc.T($"no fixture {fixture}: make it once with --perf-baseline --make (see README)",
                                          $"нет эталона {fixture}: сделайте его один раз: --perf-baseline --make (см. README)"));
            Environment.Exit(2);
        }
        int ticks = int.Parse(Arg(args, "--ticks", "2000")), warmup = int.Parse(Arg(args, "--warmup", "200")), repeat = int.Parse(Arg(args, "--repeat", "1"));
        string csv = Arg(args, "--csv", null), label = Arg(args, "--label", "");
        var info = World.ReadInfo(fixture);
        var runs = new List<Dictionary<string, double>>();
        var hashes = new List<string>();
        int popStart = 0, popEnd = 0;
        for (int r = 0; r < repeat; r++)
        {
            var w = World.Load(fixture);
            ParamHook.Apply(ParamHook.Parse(args));   // --set / --preset over the laws the fixture restored (none: the fixture's own)
            popStart = w.Agents.Count;
            for (int t = 0; t < warmup; t++) w.Step();
            Array.Clear(w.Prof); Array.Clear(w.Detail);
            w.AgentBusy = w.AgentLongest = 0;
            GC.Collect();
            var proc = Process.GetCurrentProcess();
            var cpu0 = proc.TotalProcessorTime;
            long alloc0 = GC.GetTotalAllocatedBytes(false);
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            double pause0 = GC.GetTotalPauseDuration().TotalMilliseconds;
            var sw = Stopwatch.StartNew();
            for (int t = 0; t < ticks; t++) w.Step();
            double wall = sw.Elapsed.TotalMilliseconds;
            proc.Refresh();
            double cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
            var m = new Dictionary<string, double>
            {
                ["wall_ms_tick"] = wall / ticks,
                ["cpu_ms_tick"] = cpu / ticks,
                ["env"] = w.Prof[0] / ticks, ["agents"] = w.Prof[1] / ticks, ["bookkeeping"] = w.Prof[2] / ticks,
                ["sky"] = w.Prof[4] / ticks, ["diffusion"] = w.Prof[5] / ticks, ["chemistry_climate"] = w.Prof[6] / ticks, ["structure"] = w.Prof[7] / ticks,
                ["agent_busy"] = w.AgentBusy / ticks, ["agent_longest"] = w.AgentLongest / ticks,
                ["alloc_mb_tick"] = (GC.GetTotalAllocatedBytes(false) - alloc0) / 1048576.0 / ticks,
                ["gen0"] = GC.CollectionCount(0) - g0, ["gen1"] = GC.CollectionCount(1) - g1, ["gen2"] = GC.CollectionCount(2) - g2,
                ["gc_pause_ms_tick"] = (GC.GetTotalPauseDuration().TotalMilliseconds - pause0) / ticks,
                ["heap_mb"] = GC.GetTotalMemory(false) / 1048576.0,
                ["peak_working_set_mb"] = Batch.PeakRssMb(),
            };
            for (int k = 0; k < World.DetailNames.Length; k++) m["detail_" + World.DetailNames[k].Replace(' ', '_').Replace('/', '_')] = w.Detail[k] / ticks;
            popEnd = w.Agents.Count;
            string hash = w.StateHash().ToString("x16");
            hashes.Add(hash);
            runs.Add(m);
            Console.WriteLine(Loc.T($"  repeat {r + 1}/{repeat}: wall {m["wall_ms_tick"]:F2} ms/tick, cpu {m["cpu_ms_tick"]:F2}, agents {m["agents"]:F2}, pop {popStart} → {popEnd}, hash {hash}",
                                    $"  повтор {r + 1}/{repeat}: стена {m["wall_ms_tick"]:F2} мс/тик, процессор {m["cpu_ms_tick"]:F2}, существа {m["agents"]:F2}, тел {popStart} → {popEnd}, хеш {hash}"));
            if (csv != null)
            {
                bool head = !File.Exists(csv);
                using var o = new StreamWriter(csv, append: true);
                if (head) o.WriteLine("label,fixture,fixture_seed,fixture_tick,warmup,ticks,pop_start,pop_end,hash,gc_server,cores,load_avg," + string.Join(",", m.Keys));
                o.WriteLine(string.Join(",", new[] { label.Replace(',', ';'), fixture, info.Seed.ToString(), info.Tick.ToString(), warmup.ToString(), ticks.ToString(),
                    popStart.ToString(), popEnd.ToString(), hash, System.Runtime.GCSettings.IsServerGC ? "1" : "0", Environment.ProcessorCount.ToString(), LoadAverage().Replace(' ', ';') }
                    .Concat(m.Values.Select(v => v.ToString("F4", Inv)))));
            }
        }
        // The report: a fixed list of keys, medians over the repeats.
        double Median(string key)
        {
            var v = runs.Select(x => x[key]).OrderBy(x => x).ToArray();
            return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
        }
        Console.WriteLine("perf-baseline");
        void Line(string key, string value) => Console.WriteLine($"  {key,-26} {value}");
        Line("label", label);
        Line("fixture", $"{fixture} (seed {info.Seed}, tick {info.Tick}, {info.Population} bodies)");
        Line("ticks", $"{ticks} after {warmup} warm-up, {repeat} repeat(s), median");
        Line("population", $"{popStart} -> {popEnd}");
        Line("hash_end", string.Join(" ", hashes.Distinct()) + (hashes.Distinct().Count() > 1 ? "  NONDETERMINISTIC" : ""));
        Line("build", $".NET {Environment.Version}, {(Debugger.IsAttached ? "debugger" : "no debugger")}, {(IsOptimized() ? "Release" : "DEBUG (not comparable)")}, gc {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")} ({System.Runtime.GCSettings.LatencyMode})");
        Line("machine", $"{RuntimeInformation.OSDescription.Trim()}, {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} cores, load avg {LoadAverage()}");
        foreach (var key in runs[0].Keys) Line(key, Median(key).ToString(key.StartsWith("gen") ? "F0" : "F3", Inv));
    }

    static bool IsOptimized()
    {
        var a = typeof(World).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false).OfType<DebuggableAttribute>().FirstOrDefault();
        return a == null || !a.IsJITOptimizerDisabled;
    }

    static void Make(string[] args, string fixture)
    {
        int seed = int.Parse(Arg(args, "--seed", "2")), target = int.Parse(Arg(args, "--target", "15000"));
        int maxTicks = int.Parse(Arg(args, "--max-ticks", "30000")), step = int.Parse(Arg(args, "--step", "1000"));
        var w = new World(seed, P.InitialPop, true);
        var sw = Stopwatch.StartNew();
        Console.WriteLine(Loc.T($"making the fixture: seed {seed}, until {target} bodies (every {step} ticks, at most {maxTicks})",
                                $"делаю эталон: сид {seed}, до {target} тел (проверка каждые {step} тиков, не больше {maxTicks})"));
        while (w.Tick < maxTicks)
        {
            for (int t = 0; t < step; t++) w.Step();
            Console.WriteLine($"  t={w.Tick} pop={w.Agents.Count} ({sw.Elapsed.TotalSeconds:F0} s)");
            if (w.Agents.Count >= target) break;
        }
        w.Save(fixture, $"perf baseline: seed {seed}, tick {w.Tick}");
        Console.WriteLine(Loc.T($"saved {fixture}: seed {seed}, tick {w.Tick}, {w.Agents.Count} bodies, hash {w.StateHash():x16}, {new FileInfo(fixture).Length / 1048576.0:F1} MB",
                                $"сохранено {fixture}: сид {seed}, тик {w.Tick}, {w.Agents.Count} тел, хеш {w.StateHash():x16}, {new FileInfo(fixture).Length / 1048576.0:F1} МБ"));
    }
}
