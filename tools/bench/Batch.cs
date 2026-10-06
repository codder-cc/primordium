using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Primordium;

// Many runs instead of one: a change of a constant is judged against how much worlds vary anyway.
//
//   --batch --seeds 1-16 --reps 3 --ticks 6000 --every 1000 [--pop N] [--noabio] [--tile N] [--audit]
//           [--jobs J] [--out dir] [--extinct N] [--boom N]
//           [--preset path.json] [--set Name=value ...] [--param-at TICK:Name=value ...]   (laws: ParamHook)
// Every (seed, rep) is its own process (`--run-one`) and gets the batch's law flags as given: laws are
// process-wide, so all runs of a batch see the same values and nothing is shared between worlds. Rep 0 is the seed's own world;
// rep r > 0 perturbs only life (World lifeSeed = r: the first bodies and the agents' random
// streams; terrain, chemistry, vents, water stay). Writes dir/runs.csv (a row per run and
// checkpoint), dir/summary.csv (per seed and overall: n, median, quartiles, min, max of every
// column at every checkpoint, plus extinction and boom rates) and a table. Runs share the machine,
// so ms/tick is for comparing batches run the same way, not for profiling.
//
//   --compare a/runs.csv b/runs.csv
// Distributions side by side at the last common checkpoint (population also at the quartiles of
// the run): medians and quartiles, the difference of medians with a bootstrap 95% interval,
// Mann–Whitney U (two-sided, normal approximation with ties) and, over seeds present in both, a
// sign test of per-seed medians (b > a). Rates: Fisher's exact test.
public static class Batch
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string F(double v, string f = "F4") => double.IsNaN(v) ? "" : v.ToString(f, Inv);

    static string Arg(string[] args, string name, string def)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
    }

    public static List<int> ParseSeeds(string s)
    {
        var list = new List<int>();
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = part.IndexOf('-', 1);
            if (dash > 0) { int a = int.Parse(part[..dash]), b = int.Parse(part[(dash + 1)..]); for (int k = a; k <= b; k++) list.Add(k); }
            else list.Add(int.Parse(part));
        }
        return list;
    }

    public static readonly string[] RunColumns = new[] { "seed", "rep", "tick", "pop", "births", "deaths", "ms_tick", "mean_temp" }
        .Concat(EvoMetrics.Names.Skip(1)).Concat(EvolutionHistory.Names.Skip(1)).Concat(new[] { "falls", "crushed", "buried_bodies", "pressure_reactions", "sediments" }).Concat(World.CaveNames).Concat(World.GeoNames).Concat(new[] { "energy_drift", "energy_tolerance", "atom_drift", "hash", "params" }).ToArray();

    // The latest sample of the course of evolution (World.Evolution), without its tick; zeros before the first.
    public static double[] ProgressRow(World w) =>
        (w.Progress.Latest ?? new double[EvolutionHistory.Names.Length]).Skip(1).ToArray();

    public static string FormatProgress(World w)
    {
        var v = w.Progress.Latest;
        if (v == null) return "no sample yet";
        double V(string n) => v[EvolutionHistory.Col(n)];
        var (index, trends) = EvolutionHistory.Index(w.Progress.Samples, P.ProgressWindow, v);
        return string.Create(Inv, $"@{V("tick"):F0} adaptive {V("adaptive"):F0} shadow {V("adaptive_shadow"):F0} novelty {V("novelty"):F0} (now above θ={V("theta"):F0}: {V("adaptive_now"):F0} of {V("comps"):F0}, shadow comps {V("comps_shadow"):F0}; per body {V("carry"):F1} vs {V("carry_shadow"):F1})")
            + string.Create(Inv, $" | code {V("code_used"):F1} proteins {V("body_proteins"):F2} reactions {V("body_reactions"):F2} mass {V("body_mass"):F0} cells {V("body_cells"):F2}")
            + string.Create(Inv, $" | niches {V("niches"):F0} diets {V("diet_div"):F2} under {V("under_share"):P0} water {V("water_share"):P0} lineages≥10 {V("lineages_big"):F0}")
            + string.Create(Inv, $" | dom changes {V("dom_changes"):F0} spec {V("speciations"):F0} ext {V("extinctions"):F0} drift {V("dom_drift"):F0}")
            + string.Create(Inv, $" | mrca age {V("mrca_age"):F0} pair dist {V("pair_dist"):F1} PD {V("phylo_div"):F0} taxa {V("phylo_taxa"):F0} branches {V("phylo_branches"):F0} roots {V("phylo_roots"):F0} dom depth {V("dom_depth"):F0} muts {V("dom_muts"):F0} nodes {V("phylo_nodes"):F0}")
            + string.Create(Inv, $" | index {index:F2} ({EvolutionHistory.Verdict(index)}: {string.Join(" ", trends.Select(t => t.ToString("F2", Inv)))}) mem {w.EvolutionBytes() / 1048576.0:F1} MB")
            + " | ms births/deaths, compact, measure, tree, bodies, pairs: " + string.Join(" ", w.EvoMs.Select(x => x.ToString("F1", Inv)));
    }

    // One run: a world, checkpoints written as rows of the runs CSV.
    public static void RunOne(string[] args, ParamHook.Laws laws)
    {
        int seed = int.Parse(Arg(args, "--seed", "1")), rep = int.Parse(Arg(args, "--rep", "0"));
        int ticks = int.Parse(Arg(args, "--ticks", "6000")), every = int.Parse(Arg(args, "--every", "1000"));
        int pop = int.Parse(Arg(args, "--pop", P.InitialPop.ToString()));
        bool abio = Array.IndexOf(args, "--noabio") < 0, audit = Array.IndexOf(args, "--audit") >= 0;
        string csv = Arg(args, "--csv", $"run_s{seed}_r{rep}.csv");
        string paramText = laws.Describe();   // already applied (Program)
        string tile = Arg(args, "--tile", null);
        if (tile != null) World.TileSize = int.Parse(tile);

        var w = new World(seed, pop, abio, rep);
        w.TrackHeat = audit;
        var e0 = audit ? w.AuditEnergy() : null;
        var atoms0 = audit ? w.ElementBudget() : null;
        var evo = new EvoMetrics();
        using var o = new StreamWriter(csv);
        o.WriteLine(string.Join(",", RunColumns));
        var sw = Stopwatch.StartNew();
        double last = 0;
        int births = 0, deaths = 0;
        for (int t = 1; t <= ticks; t++)
        {
            foreach (var line in ParamHook.ApplyDue(laws, w)) Console.WriteLine(line);
            w.Step();
            if (t % every != 0 && t != ticks) continue;
            double wall = sw.Elapsed.TotalMilliseconds;
            int span = t % every == 0 ? every : t % every;
            double ms = (wall - last) / span;
            var v = evo.Sample(w);
            string drift = "", tol = "", atomDrift = "";
            if (audit)
            {
                var e = w.AuditEnergy();
                drift = F(EnergyAudit.Drift(e0, e)); tol = F(EnergyAudit.Tolerance(e0, e));
                var atoms = w.ElementBudget();
                double worst = 0;
                for (int k = 0; k < atoms.Length; k++) worst = Math.Max(worst, Math.Abs(atoms[k] - w.InteriorInput[k] - w.HandInput[k] - atoms0[k]));
                atomDrift = F(worst, "F6");
            }
            var cl = w.TakeClimate();
            var row = new List<string> { seed.ToString(), rep.ToString(), t.ToString(), w.Agents.Count.ToString(), (w.Births - births).ToString(), (w.Deaths - deaths).ToString(), F(ms), F(cl.MeanT) };
            row.AddRange(v.Skip(1).Select(x => F(x)));
            row.AddRange(ProgressRow(w).Select(x => F(x)));
            row.AddRange(new[] { w.CollapsedBlocks, w.CrushedBlocks, w.DeathsBuried, w.Metamorphoses, w.Sediments }.Select(x => x.ToString()));
            var cave = w.CaveCensus();
            row.AddRange(cave.Select(x => F(x)));
            row.AddRange(w.GeoCensus().Select(x => F(x)));
            row.AddRange(new[] { drift, tol, atomDrift, w.StateHash().ToString("x16"), paramText });
            o.WriteLine(string.Join(",", row));
            o.Flush();
            Console.WriteLine($"t={t} pop={w.Agents.Count} ms/tick {ms:F2} T {cl.MeanT:F1} | {EvoMetrics.Format(v)} | cave {cave[0]:P1} (≥3: {cave[1]:P1}), depth {cave[2]:F2} max {cave[3]:F0}, voids {cave[4]:F0}" + (audit ? $" | energy drift {drift} (tol {tol}), atom drift {atomDrift}" : ""));
            births = w.Births; deaths = w.Deaths;
            last = sw.Elapsed.TotalMilliseconds;   // sampling is not the simulation's time
        }
    }

    public static void Run(string[] args, ParamHook.Laws laws)
    {
        var seeds = ParseSeeds(Arg(args, "--seeds", "1-8"));
        int reps = int.Parse(Arg(args, "--reps", "1"));
        int ticks = int.Parse(Arg(args, "--ticks", "6000")), every = int.Parse(Arg(args, "--every", "1000"));
        int jobs = int.Parse(Arg(args, "--jobs", Math.Max(1, Environment.ProcessorCount / 3).ToString()));
        double extinct = double.Parse(Arg(args, "--extinct", "10"), Inv), boom = double.Parse(Arg(args, "--boom", "20000"), Inv);
        string dir = Arg(args, "--out", "batch_out");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "runs"));
        // The laws were parsed and applied here already (Program): a bad name fails before any run starts.
        var pass = new List<string> { "--ticks", ticks.ToString(), "--every", every.ToString() };
        string popArg = Arg(args, "--pop", null);
        if (popArg != null) pass.AddRange(new[] { "--pop", popArg });
        string tileArg = Arg(args, "--tile", null);
        if (tileArg != null) pass.AddRange(new[] { "--tile", tileArg });
        if (Array.IndexOf(args, "--noabio") >= 0) pass.Add("--noabio");
        if (Array.IndexOf(args, "--audit") >= 0) pass.Add("--audit");
        pass.AddRange(laws.Forward);

        var runs = seeds.SelectMany(s => Enumerable.Range(0, reps).Select(r => (seed: s, rep: r))).ToList();
        Console.WriteLine($"batch: {runs.Count} runs (seeds {string.Join(",", seeds)} × {reps} reps), {ticks} ticks, every {every}, {jobs} at a time → {dir}; laws {laws.Describe()}");
        string host = Environment.ProcessPath, dll = typeof(Batch).Assembly.Location;
        bool viaDotnet = Path.GetFileNameWithoutExtension(host) == "dotnet";
        var total = Stopwatch.StartNew();
        int done = 0, failed = 0;
        var gate = new SemaphoreSlim(jobs);
        var tasks = runs.Select(async run =>
        {
            await gate.WaitAsync();
            try
            {
                string csv = Path.Combine(dir, "runs", $"s{run.seed}_r{run.rep}.csv"), logPath = Path.Combine(dir, "runs", $"s{run.seed}_r{run.rep}.log");
                var psi = new ProcessStartInfo { FileName = host, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                if (viaDotnet) psi.ArgumentList.Add(dll);
                foreach (var a in new[] { "--run-one", "--seed", run.seed.ToString(), "--rep", run.rep.ToString(), "--csv", csv }.Concat(pass)) psi.ArgumentList.Add(a);
                var t0 = Stopwatch.StartNew();
                using var p = Process.Start(psi);
                var outText = p.StandardOutput.ReadToEndAsync();
                var errText = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                File.WriteAllText(logPath, await outText + await errText);
                int n = Interlocked.Increment(ref done);
                if (p.ExitCode != 0) { Interlocked.Increment(ref failed); Console.WriteLine($"  [{n}/{runs.Count}] seed {run.seed} rep {run.rep} FAILED (exit {p.ExitCode}), see {logPath}"); }
                else Console.WriteLine($"  [{n}/{runs.Count}] seed {run.seed} rep {run.rep} done in {t0.Elapsed.TotalSeconds:F0} s");
            }
            finally { gate.Release(); }
        }).ToArray();
        Task.WaitAll(tasks);
        Console.WriteLine($"batch finished in {total.Elapsed.TotalMinutes:F1} min, {failed} failed");

        var rows = new List<string[]>();
        foreach (var run in runs)
        {
            string csv = Path.Combine(dir, "runs", $"s{run.seed}_r{run.rep}.csv");
            if (!File.Exists(csv)) continue;
            rows.AddRange(File.ReadAllLines(csv).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(',')));
        }
        string runsPath = Path.Combine(dir, "runs.csv");
        File.WriteAllLines(runsPath, new[] { string.Join(",", RunColumns) }.Concat(rows.Select(r => string.Join(",", r))));
        var table = RunTable.From(RunColumns, rows);
        WriteSummary(table, Path.Combine(dir, "summary.csv"), extinct, boom);
        PrintTable(table, extinct, boom);
        Console.WriteLine($"wrote {runsPath} and {Path.Combine(dir, "summary.csv")}");
    }

    // ---- the runs table ----

    sealed class RunTable
    {
        public string[] Columns;
        public List<(int seed, int rep, int tick, double[] v, string param)> Rows = new();
        public int Col(string name) => Array.IndexOf(Columns, name);

        public static RunTable From(string[] columns, IEnumerable<string[]> rows)
        {
            var t = new RunTable { Columns = columns };
            int ps = Array.IndexOf(columns, "params");
            foreach (var r in rows)
            {
                var v = new double[columns.Length];
                for (int k = 0; k < columns.Length && k < r.Length; k++)
                    v[k] = double.TryParse(r[k], NumberStyles.Float, Inv, out var x) ? x : double.NaN;
                t.Rows.Add(((int)v[0], (int)v[1], (int)v[2], v, ps >= 0 && ps < r.Length ? r[ps] : ""));
            }
            return t;
        }

        public static RunTable Read(string path)
        {
            var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
            var cols = lines[0].Split(',');
            if (cols.Length < 4 || cols[0] != "seed" || cols[2] != "tick") throw new ArgumentException($"{path}: not a runs.csv of --batch");
            return From(cols, lines.Skip(1).Select(l => l.Split(',')));
        }

        public List<int> Ticks => Rows.Select(r => r.tick).Distinct().OrderBy(x => x).ToList();
        public List<int> Seeds => Rows.Select(r => r.seed).Distinct().OrderBy(x => x).ToList();
        public List<(int seed, int rep)> Runs => Rows.Select(r => (r.seed, r.rep)).Distinct().OrderBy(r => r).ToList();

        public double[] At(int tick, string col, int seed = int.MinValue)
        {
            int c = Col(col);
            return Rows.Where(r => r.tick == tick && (seed == int.MinValue || r.seed == seed)).Select(r => r.v[c]).Where(x => !double.IsNaN(x)).ToArray();
        }

        // Per run: the largest population seen at any checkpoint, and the population at the end.
        public double[] RunMax(string col, int seed = int.MinValue)
        {
            int c = Col(col);
            return Runs.Where(r => seed == int.MinValue || r.seed == seed)
                .Select(run => Rows.Where(r => r.seed == run.seed && r.rep == run.rep).Select(r => r.v[c]).DefaultIfEmpty(0).Max()).ToArray();
        }
        public double[] RunLast(string col, int seed = int.MinValue)
        {
            int c = Col(col);
            return Runs.Where(r => seed == int.MinValue || r.seed == seed)
                .Select(run => Rows.Where(r => r.seed == run.seed && r.rep == run.rep).OrderBy(r => r.tick).Select(r => r.v[c]).DefaultIfEmpty(double.NaN).Last()).ToArray();
        }
    }

    // ---- statistics ----

    static double Quantile(double[] x, double q)
    {
        if (x.Length == 0) return double.NaN;
        var s = x.OrderBy(v => v).ToArray();
        double pos = q * (s.Length - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }
    static double Median(double[] x) => Quantile(x, 0.5);

    // Mann–Whitney U, two-sided p (normal approximation, tie correction, continuity correction).
    public static double MannWhitney(double[] a, double[] b)
    {
        int n1 = a.Length, n2 = b.Length;
        if (n1 == 0 || n2 == 0) return double.NaN;
        var all = a.Select(v => (v, g: 0)).Concat(b.Select(v => (v, g: 1))).OrderBy(p => p.v).ToArray();
        int n = all.Length;
        var rank = new double[n];
        double ties = 0;
        for (int i = 0; i < n;)
        {
            int j = i;
            while (j + 1 < n && all[j + 1].v == all[i].v) j++;
            double r = (i + j) / 2.0 + 1;
            for (int k = i; k <= j; k++) rank[k] = r;
            double t = j - i + 1;
            ties += t * t * t - t;
            i = j + 1;
        }
        double r1 = 0;
        for (int k = 0; k < n; k++) if (all[k].g == 0) r1 += rank[k];
        double u = r1 - n1 * (n1 + 1) / 2.0, mu = n1 * n2 / 2.0;
        double sigma = Math.Sqrt(n1 * n2 / 12.0 * ((n + 1) - ties / (n * (double)(n - 1))));
        if (sigma == 0) return 1;
        double z = (Math.Abs(u - mu) - 0.5) / sigma;
        return Math.Min(1, Erfc(Math.Max(0, z) / Math.Sqrt(2)));
    }

    // Complementary error function (Numerical Recipes erfcc, fractional error < 1.2e-7).
    static double Erfc(double x)
    {
        double z = Math.Abs(x), t = 1 / (1 + 0.5 * z);
        double r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? r : 2 - r;
    }

    // Bootstrap 95% interval of median(b) − median(a); deterministic resampling.
    static (double lo, double hi) BootstrapMedianDiff(double[] a, double[] b, int resamples = 2000)
    {
        if (a.Length == 0 || b.Length == 0) return (double.NaN, double.NaN);
        var rng = new SimRng(12345);
        var d = new double[resamples];
        var ra = new double[a.Length]; var rb = new double[b.Length];
        for (int k = 0; k < resamples; k++)
        {
            for (int i = 0; i < a.Length; i++) ra[i] = a[rng.Next(a.Length)];
            for (int i = 0; i < b.Length; i++) rb[i] = b[rng.Next(b.Length)];
            d[k] = Median(rb) - Median(ra);
        }
        return (Quantile(d, 0.025), Quantile(d, 0.975));
    }

    static double LogFact(int n) { double s = 0; for (int k = 2; k <= n; k++) s += Math.Log(k); return s; }

    // Two-sided binomial sign test: k of n differences positive.
    static double SignTest(int pos, int n)
    {
        if (n == 0) return double.NaN;
        int k = Math.Min(pos, n - pos);
        double p = 0;
        for (int i = 0; i <= k; i++) p += Math.Exp(LogFact(n) - LogFact(i) - LogFact(n - i) - n * Math.Log(2));
        return Math.Min(1, 2 * p);
    }

    // Fisher's exact test, two-sided, for x1 of n1 against x2 of n2.
    static double Fisher(int x1, int n1, int x2, int n2)
    {
        int k = x1 + x2, n = n1 + n2;
        double P(int x) => Math.Exp(LogFact(k) + LogFact(n - k) + LogFact(n1) + LogFact(n2) - LogFact(n) - LogFact(x) - LogFact(k - x) - LogFact(n1 - x) - LogFact(n2 - k + x));
        double p0 = P(x1), p = 0;
        for (int x = Math.Max(0, k - n2); x <= Math.Min(k, n1); x++) { double q = P(x); if (q <= p0 * (1 + 1e-7)) p += q; }
        return Math.Min(1, p);
    }

    // ---- summary ----

    static readonly string[] SummaryColumns = new[] { "pop", "births", "deaths", "ms_tick", "mean_temp" }.Concat(EvoMetrics.Names.Skip(1)).Concat(EvolutionHistory.Names.Skip(1))
        .Concat(new[] { "falls", "crushed", "buried_bodies", "pressure_reactions", "sediments" }).Concat(World.CaveNames).Concat(World.GeoNames).Concat(new[] { "energy_drift", "atom_drift" }).ToArray();

    static void WriteSummary(RunTable t, string path, double extinct, double boom)
    {
        using var o = new StreamWriter(path);
        o.WriteLine("scope,tick,metric,n,median,q1,q3,min,max,mean");
        var scopes = new List<(string name, int seed)> { ("all", int.MinValue) };
        scopes.AddRange(t.Seeds.Select(s => ($"seed={s}", s)));
        foreach (var (name, seed) in scopes)
        {
            foreach (int tick in t.Ticks)
                foreach (var col in SummaryColumns)
                {
                    if (t.Col(col) < 0) continue;
                    var x = t.At(tick, col, seed);
                    if (x.Length == 0) continue;
                    o.WriteLine($"{name},{tick},{col},{x.Length},{F(Median(x))},{F(Quantile(x, 0.25))},{F(Quantile(x, 0.75))},{F(x.Min())},{F(x.Max())},{F(x.Average())}");
                }
            var last = t.RunLast("pop", seed);
            var max = t.RunMax("pop", seed);
            int lastTick = t.Ticks.Count > 0 ? t.Ticks[^1] : 0;
            o.WriteLine($"{name},{lastTick},extinct_rate,{last.Length},{F(last.Count(p => p <= extinct) / (double)Math.Max(1, last.Length))},,,,,");
            o.WriteLine($"{name},{lastTick},boom_rate,{max.Length},{F(max.Count(p => p > boom) / (double)Math.Max(1, max.Length))},,,,,");
        }
    }

    static List<int> Checkpoints(List<int> ticks)
    {
        if (ticks.Count <= 4) return ticks;
        return new[] { ticks.Count / 4 - 1, ticks.Count / 2 - 1, ticks.Count * 3 / 4 - 1, ticks.Count - 1 }.Select(k => ticks[Math.Max(0, k)]).Distinct().ToList();
    }

    static void PrintTable(RunTable t, double extinct, double boom)
    {
        var ticks = t.Ticks;
        if (ticks.Count == 0) { Console.WriteLine("no results"); return; }
        var cps = Checkpoints(ticks);
        int end = ticks[^1];
        string Cell(double[] x, string f = "F0") => x.Length == 0 ? "-" : x.Length == 1 ? Median(x).ToString(f, Inv) : $"{Median(x).ToString(f, Inv)} [{x.Min().ToString(f, Inv)}–{x.Max().ToString(f, Inv)}]";
        var head = new List<string> { "seed" };
        head.AddRange(cps.Select(c => $"pop@{c}"));
        head.AddRange(new[] { "extinct", "boom", "T °C", "lineages", "kin cl.", "H", "used", "gen", "useful sp.", "novelty", "ms/tick" });
        var lines = new List<List<string>> { head };
        foreach (int s in t.Seeds)
        {
            var row = new List<string> { s.ToString() };
            row.AddRange(cps.Select(c => Cell(t.At(c, "pop", s))));
            var last = t.RunLast("pop", s); var max = t.RunMax("pop", s);
            row.Add($"{last.Count(p => p <= extinct)}/{last.Length}");
            row.Add($"{max.Count(p => p > boom)}/{max.Length}");
            row.Add(Cell(t.At(end, "mean_temp", s), "F1"));
            row.Add(Cell(t.At(end, "lineages", s)));
            row.Add(Cell(t.At(end, "kin_clusters", s)));
            row.Add(Cell(t.At(end, "lineage_entropy", s), "F2"));
            row.Add(Cell(t.At(end, "used_code", s), "F3"));
            row.Add(Cell(t.At(end, "gen_mean", s), "F1"));
            row.Add(Cell(t.At(end, "useful_specs", s)));
            row.Add(Cell(t.At(end, "novelty", s)));
            row.Add(Median(t.Ticks.SelectMany(c => t.At(c, "ms_tick", s)).ToArray()).ToString("F2", Inv));
            lines.Add(row);
        }
        string Overall(double[] x, string f = "F0") => x.Length == 0 ? "-" : $"{Median(x).ToString(f, Inv)} ({Quantile(x, 0.25).ToString(f, Inv)}–{Quantile(x, 0.75).ToString(f, Inv)})";
        {
            var row = new List<string> { "all: median (IQR)" };
            row.AddRange(cps.Select(c => Overall(t.At(c, "pop"))));
            var last = t.RunLast("pop"); var max = t.RunMax("pop");
            row.Add($"{last.Count(p => p <= extinct)}/{last.Length}");
            row.Add($"{max.Count(p => p > boom)}/{max.Length}");
            row.Add(Overall(t.At(end, "mean_temp"), "F1"));
            row.Add(Overall(t.At(end, "lineages")));
            row.Add(Overall(t.At(end, "kin_clusters")));
            row.Add(Overall(t.At(end, "lineage_entropy"), "F2"));
            row.Add(Overall(t.At(end, "used_code"), "F3"));
            row.Add(Overall(t.At(end, "gen_mean"), "F1"));
            row.Add(Overall(t.At(end, "useful_specs")));
            row.Add(Overall(t.At(end, "novelty")));
            row.Add(Median(t.Ticks.SelectMany(c => t.At(c, "ms_tick")).ToArray()).ToString("F2", Inv));
            lines.Add(row);
            var mm = new List<string> { "all: min–max" };
            mm.AddRange(cps.Select(c => { var x = t.At(c, "pop"); return x.Length == 0 ? "-" : $"{x.Min():F0}–{x.Max():F0}"; }));
            lines.Add(mm);
        }
        PrintAligned(lines);
        Console.WriteLine($"extinct: population ≤ {extinct} at the last checkpoint; boom: more than {boom} at any checkpoint. Per seed: median [min–max] over reps.");
        var drift = t.At(end, "energy_drift");
        if (drift.Length > 0) Console.WriteLine($"energy drift at the end: max |drift| {drift.Max(Math.Abs):F3}; atom drift max {t.At(end, "atom_drift").DefaultIfEmpty(0).Max():F6}");
    }

    static void PrintAligned(List<List<string>> lines)
    {
        int cols = lines.Max(l => l.Count);
        var width = new int[cols];
        foreach (var l in lines) for (int k = 0; k < l.Count; k++) width[k] = Math.Max(width[k], l[k].Length);
        for (int i = 0; i < lines.Count; i++)
        {
            Console.WriteLine("| " + string.Join(" | ", Enumerable.Range(0, cols).Select(k => (k < lines[i].Count ? lines[i][k] : "").PadRight(width[k]))) + " |");
            if (i == 0) Console.WriteLine("|" + string.Join("|", width.Select(wd => new string('-', wd + 2))) + "|");
        }
    }

    // ---- comparison ----

    public static void Compare(string[] args)
    {
        int i = Array.IndexOf(args, "--compare");
        if (i + 2 >= args.Length) throw new ArgumentException("--compare a/runs.csv b/runs.csv");
        var a = RunTable.Read(args[i + 1]);
        var b = RunTable.Read(args[i + 2]);
        double extinct = double.Parse(Arg(args, "--extinct", "10"), Inv), boom = double.Parse(Arg(args, "--boom", "20000"), Inv);
        var common = a.Ticks.Intersect(b.Ticks).OrderBy(x => x).ToList();
        if (common.Count == 0) throw new ArgumentException("the two batches share no checkpoint");
        int end = common[^1];
        Console.WriteLine($"a: {args[i + 1]} — {a.Runs.Count} runs, seeds {string.Join(",", a.Seeds)}, params {string.Join(" | ", a.Rows.Select(r => r.param).Distinct())}");
        Console.WriteLine($"b: {args[i + 2]} — {b.Runs.Count} runs, seeds {string.Join(",", b.Seeds)}, params {string.Join(" | ", b.Rows.Select(r => r.param).Distinct())}");
        var seeds = a.Seeds.Intersect(b.Seeds).ToList();
        var lines = new List<List<string>> { new() { "metric", "a median [q1–q3]", "b median [q1–q3]", "Δ median", "95% CI (bootstrap)", "MWU p", "seeds b>a (sign p)", "" } };
        string Q(double[] x, string f) => x.Length == 0 ? "-" : $"{Median(x).ToString(f, Inv)} [{Quantile(x, 0.25).ToString(f, Inv)}–{Quantile(x, 0.75).ToString(f, Inv)}]";
        void Line(string label, int tick, string col, string f)
        {
            if (a.Col(col) < 0 || b.Col(col) < 0) return;
            var xa = a.At(tick, col); var xb = b.At(tick, col);
            if (xa.Length == 0 || xb.Length == 0) return;
            double p = MannWhitney(xa, xb);
            var (lo, hi) = BootstrapMedianDiff(xa, xb);
            int pos = 0, n = 0;
            foreach (int s in seeds)
            {
                var sa = a.At(tick, col, s); var sb = b.At(tick, col, s);
                if (sa.Length == 0 || sb.Length == 0) continue;
                double d = Median(sb) - Median(sa);
                if (d == 0) continue;
                n++; if (d > 0) pos++;
            }
            double sp = SignTest(pos, n);
            bool ci = lo > 0 || hi < 0;
            string flag = p < 0.01 && ci ? "** differs" : p < 0.05 && ci ? "* differs" : "~ noise";
            lines.Add(new() { label, Q(xa, f), Q(xb, f), (Median(xb) - Median(xa)).ToString(f, Inv), $"{lo.ToString(f, Inv)} … {hi.ToString(f, Inv)}", p.ToString("F3", Inv), n == 0 ? "-" : $"{pos}/{n} ({sp:F3})", flag });
        }
        foreach (int c in Checkpoints(common)) Line($"pop @{c}", c, "pop", "F0");
        foreach (var col in new[] { "mean_temp", "ms_tick", "births", "deaths" }) Line($"{col} @{end}", end, col, "F2");
        foreach (var col in EvoMetrics.Names.Skip(1).Concat(EvolutionHistory.Names.Skip(1)).Concat(new[] { "falls", "crushed", "buried_bodies", "pressure_reactions", "sediments" }).Concat(World.CaveNames).Concat(World.GeoNames)) Line($"{col} @{end}", end, col, "F3");
        PrintAligned(lines);
        int Count(RunTable t, Func<double, bool> f, bool max) => (max ? t.RunMax("pop") : t.RunLast("pop")).Count(f);
        int na = a.Runs.Count, nb = b.Runs.Count;
        int ea = Count(a, p => p <= extinct, false), eb = Count(b, p => p <= extinct, false);
        int ba = Count(a, p => p > boom, true), bb = Count(b, p => p > boom, true);
        Console.WriteLine($"extinct (≤ {extinct} at the end): a {ea}/{na}, b {eb}/{nb}, Fisher p {Fisher(ea, na, eb, nb):F3}");
        Console.WriteLine($"boom (> {boom} at any checkpoint): a {ba}/{na}, b {bb}/{nb}, Fisher p {Fisher(ba, na, bb, nb):F3}");
        Console.WriteLine("'* differs': MWU p < 0.05 and the bootstrap interval of Δ median excludes 0 ('**': p < 0.01); '~ noise' otherwise. Many metrics are tested at once: expect about one in twenty to pass p < 0.05 by chance.");
    }
}
