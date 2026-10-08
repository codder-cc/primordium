using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Primordium;

// Punctuated equilibrium after catastrophes (ROADMAP 9.4): is the tempo of evolution higher in the days
// after an ice age, a volcanic winter or a player's catastrophe begins than at other times?
//
//   --punctuated dir [dir …] [--control dir [dir …]] [--window-days 4] [--burn 2000] [--perms 2000]
//                [--onsets iceage,winter,eruption,player] [--columns new_lineages,lineages_lost]
//
// Reads batch directories (--batch: runs/sS_rR.csv and runs/sS_rR.events.csv). Onsets are taken from
// the events file: the chronicle's "ice age #n began", "volcanic winter #n", megaeruptions, the player's
// catastrophes (and "flare" if asked). After each onset a window of --window-days days; overlapping
// windows merge. Ticks before --burn are left out everywhere (the founding of the world is a burst of
// its own). Tempo: the chronicle's speciations, extinctions, new dominants and their sum, and the per-
// checkpoint runs.csv --columns (counted at the checkpoint's tick).
//
// Within runs: events per day inside the windows against outside them, per run and pooled; tests across
// runs — Wilcoxon signed-rank of per-run differences (exact up to 30 runs), the sign test, and a
// permutation test that shifts all onsets of a run by one random offset (keeping their spacing and
// the run's own clustering of events; p one-sided, "more inside"). With --control: the same windows (from
// the treated run's onsets) laid on the control run of the same seed and rep (same world, no
// catastrophe) — paired differences, Wilcoxon, sign test and an exact (or 20 000-sample) sign-flip test.
public static class Punctuated
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] EventKinds = { "Speciation", "Extinction", "NewDominant" };

    sealed class RunData
    {
        public int Seed, Rep, End;
        public List<(int tick, string kind)> Onsets = new();
        public Dictionary<string, List<(int tick, double w)>> Tempo = new();
    }

    static string Arg(string[] args, string name, string def)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
    }

    static List<string> Dirs(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        if (i < 0) return new List<string>();
        return args.Skip(i + 1).TakeWhile(a => !a.StartsWith("--")).ToList();
    }

    // What kind of onset an event is, or null.
    static string OnsetKind(string type, string text)
    {
        if (type == "Player") return text.StartsWith("player catastrophe", StringComparison.Ordinal) ? "player" : null;
        if (type != "Climate") return null;
        if (text.StartsWith("ice age #", StringComparison.Ordinal) && text.Contains(" began")) return "iceage";
        if (text.StartsWith("volcanic winter #", StringComparison.Ordinal) && !text.Contains(" ended")) return "winter";
        if (text.Contains("megaeruption")) return "eruption";
        if (text.StartsWith("solar flare #", StringComparison.Ordinal)) return "flare";
        return null;
    }

    static List<RunData> Load(IEnumerable<string> dirs, string[] columns)
    {
        var list = new List<RunData>();
        foreach (var dir in dirs)
        {
            string runs = Directory.Exists(Path.Combine(dir, "runs")) ? Path.Combine(dir, "runs") : dir;
            foreach (var csv in Directory.GetFiles(runs, "s*_r*.csv").Where(f => !f.EndsWith(".events.csv")).OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(csv);   // s{seed}_r{rep}
                var parts = name[1..].Split("_r");
                if (parts.Length != 2 || !int.TryParse(parts[0], out int seed) || !int.TryParse(parts[1], out int rep)) continue;
                var lines = File.ReadAllLines(csv).Where(l => l.Length > 0).ToList();
                if (lines.Count < 2) continue;
                var cols = lines[0].Split(',');
                int tc = Array.IndexOf(cols, "tick");
                var run = new RunData { Seed = seed, Rep = rep };
                foreach (var c in columns) run.Tempo[c] = new();
                foreach (var k in EventKinds) run.Tempo[k] = new();
                foreach (var l in lines.Skip(1))
                {
                    var f = l.Split(',');
                    int t = int.Parse(f[tc], Inv);
                    run.End = Math.Max(run.End, t);
                    foreach (var c in columns)
                    {
                        int k = Array.IndexOf(cols, c);
                        if (k >= 0 && k < f.Length && double.TryParse(f[k], NumberStyles.Float, Inv, out double v)) run.Tempo[c].Add((t, v));
                    }
                }
                string evPath = Path.ChangeExtension(csv, ".events.csv");
                if (File.Exists(evPath))
                    foreach (var l in File.ReadLines(evPath).Skip(1))
                    {
                        // tick,type,value,important,"text"
                        var f = l.Split(',', 5);
                        if (f.Length < 5 || !int.TryParse(f[0], out int t)) continue;
                        string type = f[1], text = f[4].Trim('"');
                        if (run.Tempo.TryGetValue(type, out var tl)) tl.Add((t, 1));
                        var kind = OnsetKind(type, text);
                        if (kind != null) run.Onsets.Add((t, kind));
                    }
                list.Add(run);
            }
        }
        return list;
    }

    // The windows [o, o + w) after the onsets, merged, clipped to [burn, end).
    static List<(int a, int b)> Windows(IEnumerable<int> onsets, int w, int burn, int end)
    {
        var res = new List<(int a, int b)>();
        foreach (int o in onsets.OrderBy(x => x))
        {
            int a = Math.Max(o, burn), b = Math.Min(o + w, end);
            if (b <= a) continue;
            if (res.Count > 0 && a <= res[^1].b) res[^1] = (res[^1].a, Math.Max(res[^1].b, b));
            else res.Add((a, b));
        }
        return res;
    }

    static bool Inside(List<(int a, int b)> win, int t)
    {
        foreach (var (a, b) in win) if (t >= a && t < b) return true;
        return false;
    }

    static (double inside, double outside, double inTime, double outTime) Count(List<(int tick, double w)> ev, List<(int a, int b)> win, int burn, int end)
    {
        double inside = 0, outside = 0, inTime = win.Sum(x => (double)(x.b - x.a));
        foreach (var (t, w) in ev)
        {
            if (t < burn || t >= end) continue;
            if (Inside(win, t)) inside += w; else outside += w;
        }
        return (inside, outside, inTime, end - burn - inTime);
    }

    // Wilcoxon signed-rank, two-sided p (zeros dropped; exact up to 30 differences, else normal with ties).
    public static double Wilcoxon(IList<double> d, out double wPlus, out int n)
    {
        var x = d.Where(v => v != 0 && !double.IsNaN(v)).ToArray();
        n = x.Length; wPlus = 0;
        if (n == 0) return double.NaN;
        var idx = Enumerable.Range(0, n).OrderBy(i => Math.Abs(x[i])).ToArray();
        var rank2 = new int[n];   // doubled ranks (midranks stay integers)
        double ties = 0;
        for (int i = 0; i < n;)
        {
            int j = i;
            while (j + 1 < n && Math.Abs(x[idx[j + 1]]) == Math.Abs(x[idx[i]])) j++;
            for (int k = i; k <= j; k++) rank2[idx[k]] = i + j + 2;
            double t = j - i + 1; ties += t * t * t - t;
            i = j + 1;
        }
        int sumPlus2 = 0, total2 = rank2.Sum();
        for (int i = 0; i < n; i++) if (x[i] > 0) sumPlus2 += rank2[i];
        wPlus = sumPlus2 / 2.0;
        if (n <= 30)
        {
            // Distribution of the doubled W+ over all 2^n sign patterns.
            var dist = new double[total2 + 1];
            dist[0] = 1;
            foreach (int r in rank2)
                for (int s = total2; s >= r; s--) dist[s] += dist[s - r];
            double all = Math.Pow(2, n), lo = 0, hi = 0;
            for (int s = 0; s <= total2; s++) { if (s <= sumPlus2) lo += dist[s]; if (s >= sumPlus2) hi += dist[s]; }
            return Math.Min(1, 2 * Math.Min(lo, hi) / all);
        }
        double mu = n * (n + 1) / 4.0, sigma = Math.Sqrt(n * (n + 1) * (2 * n + 1) / 24.0 - ties / 48.0);
        double z = (Math.Abs(wPlus - mu) - 0.5) / sigma;
        return Math.Min(1, Erfc(Math.Max(0, z) / Math.Sqrt(2)));
    }

    static double Erfc(double x)
    {
        double z = Math.Abs(x), t = 1 / (1 + 0.5 * z);
        double r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? r : 2 - r;
    }

    static double Sign(IList<double> d)
    {
        int pos = d.Count(v => v > 0), n = d.Count(v => v != 0 && !double.IsNaN(v));
        if (n == 0) return double.NaN;
        int k = Math.Min(pos, n - pos);
        double p = 0;
        for (int i = 0; i <= k; i++) p += Math.Exp(LogChoose(n, i) - n * Math.Log(2));
        return Math.Min(1, 2 * p);
    }

    static double LogChoose(int n, int k)
    {
        double s = 0;
        for (int i = 1; i <= k; i++) s += Math.Log(n - k + i) - Math.Log(i);
        return s;
    }

    // Paired sign-flip test of Σd, one-sided (more in the treated): exact up to 20 pairs, else sampled.
    static double SignFlip(IList<double> d)
    {
        var x = d.Where(v => !double.IsNaN(v)).ToArray();
        int n = x.Length;
        if (n == 0) return double.NaN;
        double obs = x.Sum();
        long hits = 0, total = 0;
        if (n <= 20)
        {
            for (long m = 0; m < 1L << n; m++)
            {
                double s = 0;
                for (int i = 0; i < n; i++) s += (m >> i & 1) != 0 ? -x[i] : x[i];
                if (s >= obs - 1e-9) hits++;
                total++;
            }
            return hits / (double)total;
        }
        var rng = new SimRng(4242);
        for (int k = 0; k < 20000; k++)
        {
            double s = 0;
            for (int i = 0; i < n; i++) s += (rng.Next(2) == 0) ? -x[i] : x[i];
            if (s >= obs - 1e-9) hits++;
            total++;
        }
        return (hits + 1) / (double)(total + 1);
    }

    static string F(double v, string f = "F3") => double.IsNaN(v) ? "-" : v.ToString(f, Inv);

    public static void Run(string[] args)
    {
        var dirs = Dirs(args, "--punctuated");
        var control = Dirs(args, "--control");
        double days = double.Parse(Arg(args, "--window-days", "4"), Inv);
        int burn = int.Parse(Arg(args, "--burn", "2000")), perms = int.Parse(Arg(args, "--perms", "2000"));
        var kinds = Arg(args, "--onsets", "iceage,winter,eruption,player").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var columns = Arg(args, "--columns", "new_lineages,lineages_lost").Split(',', StringSplitOptions.RemoveEmptyEntries);
        int win = (int)Math.Round(days * P.DayLen);
        double day = P.DayLen;
        var runs = Load(dirs, columns);
        if (runs.Count == 0) throw new ArgumentException("--punctuated: no runs found (give batch directories made by --batch)");
        foreach (var r in runs) r.Onsets = r.Onsets.Where(o => kinds.Contains(o.kind) && o.tick >= burn && o.tick < r.End).ToList();
        var series = EventKinds.Concat(new[] { "tempo_all" }).Concat(columns).ToList();
        foreach (var r in runs) r.Tempo["tempo_all"] = EventKinds.SelectMany(k => r.Tempo[k]).ToList();

        var withOnsets = runs.Where(r => r.Onsets.Count > 0).ToList();
        var byKind = runs.SelectMany(r => r.Onsets).GroupBy(o => o.kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}");
        Console.WriteLine(Loc.T($"punctuated: {runs.Count} runs, {withOnsets.Count} with onsets ({string.Join(", ", byKind)}); window {days:0.##} days ({win} ticks) after each onset, ticks before {burn} left out",
                                $"прерывистое равновесие: прогонов {runs.Count}, с началами катастроф {withOnsets.Count} ({string.Join(", ", byKind)}); окно {days:0.##} сут ({win} тиков) после каждого начала, тики до {burn} не считаются"));
        Console.WriteLine(Loc.T("events per day inside the windows vs outside; per run: median difference; pooled: rate ratio; tests across runs",
                                "событий в сутки в окнах и вне их; по прогонам — медиана разности; в сумме — отношение темпов; тесты по прогонам"));
        var head = new List<string> { Loc.T("tempo", "темп"), Loc.T("inside /day", "в окне /сут"), Loc.T("outside /day", "вне /сут"), Loc.T("ratio", "отношение"),
            Loc.T("runs in>out", "прогонов в>вне"), Loc.T("Wilcoxon p", "Уилкоксон p"), Loc.T("sign p", "знаков p"), Loc.T("shift perm. p (more in)", "сдвиг p (больше в окне)") };
        var lines = new List<List<string>> { head };
        foreach (var s in series)
        {
            double inE = 0, outE = 0, inT = 0, outT = 0;
            var diffs = new List<double>();
            foreach (var r in withOnsets)
            {
                var w = Windows(r.Onsets.Select(o => o.tick), win, burn, r.End);
                var c = Count(r.Tempo[s], w, burn, r.End);
                if (c.inTime <= 0 || c.outTime <= 0) continue;
                inE += c.inside; outE += c.outside; inT += c.inTime; outT += c.outTime;
                diffs.Add(c.inside / c.inTime * day - c.outside / c.outTime * day);
            }
            double rIn = inT > 0 ? inE / inT * day : double.NaN, rOut = outT > 0 ? outE / outT * day : double.NaN;
            double ratio = rOut > 0 ? rIn / rOut : double.NaN;
            double pw = Wilcoxon(diffs, out _, out int nz);
            // Shift permutation of the pooled ratio.
            var rng = new SimRng(9419);
            int hits = 0, done = 0;
            if (!double.IsNaN(ratio))
                for (int k = 0; k < perms; k++)
                {
                    double pi = 0, po = 0, ti = 0, to = 0;
                    foreach (var r in withOnsets)
                    {
                        int len = r.End - burn;
                        int off = rng.Next(len);
                        var shifted = r.Onsets.Select(o => burn + (o.tick - burn + off) % len);
                        var w = Windows(shifted, win, burn, r.End);
                        var c = Count(r.Tempo[s], w, burn, r.End);
                        if (c.inTime <= 0 || c.outTime <= 0) continue;
                        pi += c.inside; po += c.outside; ti += c.inTime; to += c.outTime;
                    }
                    if (ti <= 0 || to <= 0 || po <= 0) continue;
                    done++;
                    if (pi / ti / (po / to) >= ratio - 1e-12) hits++;
                }
            double pp = done > 0 ? (hits + 1) / (done + 1.0) : double.NaN;
            lines.Add(new() { s, F(rIn), F(rOut), F(ratio, "F2"), $"{diffs.Count(d => d > 0)}/{nz}", F(pw), F(Sign(diffs)), F(pp) });
        }
        Print(lines);

        if (control.Count > 0)
        {
            var ctrl = Load(control, columns).ToDictionary(r => (r.Seed, r.Rep));
            foreach (var r in ctrl.Values) r.Tempo["tempo_all"] = EventKinds.SelectMany(k => r.Tempo[k]).ToList();
            var pairs = withOnsets.Where(r => ctrl.ContainsKey((r.Seed, r.Rep))).ToList();
            Console.WriteLine();
            Console.WriteLine(Loc.T($"against the control (the same world without the catastrophe): {pairs.Count} pairs; events per day in the treated run's windows",
                                    $"против контроля (тот же мир без катастрофы): пар {pairs.Count}; событий в сутки в окнах опытного прогона"));
            var lines2 = new List<List<string>> { new() { Loc.T("tempo", "темп"), Loc.T("treated /day", "опыт /сут"), Loc.T("control /day", "контроль /сут"), Loc.T("ratio", "отношение"),
                Loc.T("pairs t>c", "пар о>к"), Loc.T("Wilcoxon p", "Уилкоксон p"), Loc.T("sign p", "знаков p"), Loc.T("sign-flip p (more in treated)", "перестановки p (больше в опыте)") } };
            foreach (var s in series)
            {
                double te = 0, ce = 0, tt = 0;
                var diffs = new List<double>();
                foreach (var r in pairs)
                {
                    var c0 = ctrl[(r.Seed, r.Rep)];
                    int end = Math.Min(r.End, c0.End);
                    var w = Windows(r.Onsets.Select(o => o.tick), win, burn, end);
                    var a = Count(r.Tempo[s], w, burn, end);
                    var b = Count(c0.Tempo[s], w, burn, end);
                    if (a.inTime <= 0) continue;
                    te += a.inside; ce += b.inside; tt += a.inTime;
                    diffs.Add((a.inside - b.inside) / a.inTime * day);
                }
                double rt = tt > 0 ? te / tt * day : double.NaN, rc = tt > 0 ? ce / tt * day : double.NaN;
                double pw = Wilcoxon(diffs, out _, out int nz);
                lines2.Add(new() { s, F(rt), F(rc), rc > 0 ? F(rt / rc, "F2") : "-", $"{diffs.Count(d => d > 0)}/{nz}", F(pw), F(Sign(diffs)), F(SignFlip(diffs)) });
            }
            Print(lines2);
        }
        Console.WriteLine(Loc.T("Wilcoxon and sign tests are two-sided; the permutation tests are one-sided (a burst inside the windows). Several tempo series are tested at once: one in twenty passes p < 0.05 by chance.",
                                "Тесты Уилкоксона и знаков двусторонние; перестановочные — односторонние (всплеск в окне). Проверяется несколько рядов сразу: один из двадцати проходит p < 0,05 случайно."));
    }

    static void Print(List<List<string>> lines)
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
}
