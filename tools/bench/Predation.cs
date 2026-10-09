using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Primordium;

// --predation [--seeds 1-2 | --load path.sav] [--ticks 6000] [--every 2000] [--sample 100] [--pop N] [--noabio]
//   (laws as usual, e.g. --set BodyHold=1)
// The predation audit (ROADMAP 5). Observation only (World.PredProbe; the state hash is printed to check
// against a run without it). Every `every` ticks, for the window:
//   attack — attacks on a partner, the energy they cost (power + CostSocial), molecules torn out and their
//     bond energy (and the split energy of the exothermic ones), the share torn from close kin (Kinship ≥
//     54 bits, a "kind" a body can tell apart) and from the own lineage, energy returned per energy paid;
//     the victims' injury (energy lost); kills, their remains left on the floor and how much of those was
//     taken in again in the same cells (scavenging, an upper bound);
//   take — tries, molecules pulled, cost, kin and lineage shares;
//   hunting share — of the energy brought into bodies (photons caught + bond energy of every molecule
//     entering a body), the part torn or pulled out of other bodies;
//   defence — by the victim's body cohesion (World.BodyCohesion): attacks, molecules torn per attack and
//     per energy paid, the victims' mean mass;
//   parasites — injections (into another lineage), those that wrote code, foreign code inherited by a
//     child, foreign code copied on by a host (spread);
//   territory — bodies sampled at the window's start by local crowding (bodies in the 3×3 cells around),
//     in quintiles: deaths during the window and children per body; Spearman ρ of crowding against
//     each (positive for deaths, negative for births: density limits).
// At the end, the hunter series (sampled every `sample` ticks): see Oscillation.
public sealed partial class World
{
    static readonly CultureInfo PredInv = CultureInfo.InvariantCulture;

    public static void PredationReport(string[] args)
    {
        string Arg(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        int ticks = int.Parse(Arg("--ticks", "6000")), every = int.Parse(Arg("--every", "2000")), sample = int.Parse(Arg("--sample", "100"));
        int pop = int.Parse(Arg("--pop", "-1"));   // < 0: P.InitialPop by area
        bool abio = Array.IndexOf(args, "--noabio") < 0;
        string load = Arg("--load", null);
        var seeds = load != null ? new List<int> { 0 } : Batch.ParseSeeds(Arg("--seeds", "1-2"));
        Console.WriteLine(Loc.T($"predation audit: {(load ?? "seeds " + string.Join(",", seeds))}, {ticks} ticks, windows of {every}, BodyHold {P.BodyHold}",
                                $"аудит хищничества: {(load ?? "сиды " + string.Join(",", seeds))}, {ticks} тиков, окна по {every}, BodyHold {P.BodyHold}"));
        foreach (int seed0 in seeds)
        {
            var w = load != null ? Load(load) : new World(Batch.Settings(args, seed0, pop, abio, 0));   // --size WxHxL: a small world
            if (load != null) foreach (var line in ParamHook.Apply(ParamHook.Parse(args))) Console.WriteLine("set after load " + line);   // a save brings its own laws
            var probe = w.PredProbe = new PredationProbe();
            {
                // Is a shell heavy? Over the seed's molecules: bond against mass and against volume.
                var ch = w.Chem;
                double[] bond = Enumerable.Range(0, Chemistry.S).Select(k => (double)ch.Bond[k]).ToArray();
                Console.WriteLine(string.Create(PredInv, $"seed {w.Seed} chemistry: ρ(bond, mass) {Oscillation.Spearman(bond, Enumerable.Range(0, Chemistry.S).Select(k => (double)ch.Mass[k]).ToArray()):F2}, ρ(bond, volume) {Oscillation.Spearman(bond, Enumerable.Range(0, Chemistry.S).Select(k => (double)ch.Volume[k]).ToArray()):F2}; solid (bond ≥ 0.85) mean mass {Enumerable.Range(0, Chemistry.S).Where(k => ch.Solid[k]).Select(k => ch.Mass[k]).DefaultIfEmpty(0).Average():F2} vs others {Enumerable.Range(0, Chemistry.S).Where(k => !ch.Solid[k]).Select(k => ch.Mass[k]).DefaultIfEmpty(0).Average():F2}"));
            }
            var crowd = w.CrowdSnapshot();
            var series = new List<(int tick, int pop, int hunters)>();
            var remains = new RemainsLife();
            for (int t = 1; t <= ticks; t++)
            {
                w.Step();
                if (t % RemainsLife.Every == 0) remains.Update(w);
                if (t % sample == 0)
                {
                    int n = 0, h = 0;
                    foreach (var a in w.Agents) { if (a.Dead) continue; n++; if (Diet(a) == DietHunter) h++; }
                    series.Add(((int)w.Tick, n, h));
                }
                if (t % every != 0 && t != ticks) continue;
                Console.WriteLine($"seed {w.Seed} tick {w.Tick} pop {w.Agents.Count} hash {w.StateHash():x16}");
                foreach (var line in probe.Report()) Console.WriteLine("  " + line);
                foreach (var line in remains.Report()) Console.WriteLine("  " + line);
                foreach (var line in CrowdReport(w, crowd, w.Agents)) Console.WriteLine("  " + line);
                probe.Clear();
                crowd = w.CrowdSnapshot();
            }
            foreach (var line in Oscillation.Describe(series.Select(x => (double)x.tick).ToArray(), series.Select(x => (double)x.pop).ToArray(), series.Select(x => (double)x.hunters).ToArray()))
                Console.WriteLine("  " + line);
        }
    }

    // Every living body with how crowded it is (bodies in the 3×3 cells around it, itself included) and
    // its children so far.
    List<(Agent a, int crowd, int kids, int a0X, int a0Y)> CrowdSnapshot()
    {
        var list = new List<(Agent, int, int, int, int)>();
        foreach (var a in Agents)
        {
            if (a.Dead) continue;
            int n = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int c = Offset(a.Y * W + a.X, dx, dy);
                    if (c >= 0) n += Count[c];
                }
            list.Add((a, n, a.NChildren, a.X, a.Y));
        }
        return list;
    }

    static IEnumerable<string> CrowdReport(World w, List<(Agent a, int crowd, int kids, int a0X, int a0Y)> snap, List<Agent> now)
    {
        if (snap.Count < 20) yield break;
        var sorted = snap.OrderBy(x => x.crowd).ThenBy(x => x.a.Id).ToList();
        var parts = new List<string>();
        int q = 5;
        for (int k = 0; k < q; k++)
        {
            var part = sorted.Skip(k * sorted.Count / q).Take((k + 1) * sorted.Count / q - k * sorted.Count / q).ToList();
            double died = part.Count(x => x.a.Dead) / (double)part.Count, kids = part.Average(x => x.a.NChildren - x.kids);
            parts.Add(string.Create(PredInv, $"{part.Min(x => x.crowd)}–{part.Max(x => x.crowd)}: died {died:P0}, kids {kids:F2}"));
        }
        double[] cr = snap.Select(x => (double)x.crowd).ToArray();
        double rd = Oscillation.Spearman(cr, snap.Select(x => x.a.Dead ? 1.0 : 0.0).ToArray());
        double rb = Oscillation.Spearman(cr, snap.Select(x => (double)(x.a.NChildren - x.kids)).ToArray());
        yield return Loc.T("territory (bodies in 3×3 around, quintiles): ", "территория (тел в 3×3 вокруг, квинтили): ") + string.Join("; ", parts)
            + string.Create(PredInv, $" | ρ(crowd, death) {rd:F3}, ρ(crowd, kids) {rb:F3}, n {snap.Count}");
        // Regions of 16×16 cells with ≥ 5 bodies at the start: per-capita deaths and children of those
        // bodies, and the growth ln(N1/N0), against N0. Density-dependent regulation: deaths up, children
        // and growth down with N0.
        const int B = 16;
        int bx = w.W / B, nb = bx * (w.H / B);
        var n0 = new double[nb]; var dead = new double[nb]; var kids2 = new double[nb]; var n1 = new double[nb];
        foreach (var x in snap) { int b = x.a0Y / B * bx + x.a0X / B; n0[b]++; if (x.a.Dead) dead[b]++; kids2[b] += x.a.NChildren - x.kids; }
        foreach (var a in now) { if (a.Dead) continue; n1[a.Y / B * bx + a.X / B]++; }
        var use = Enumerable.Range(0, nb).Where(b => n0[b] >= 5).ToArray();
        if (use.Length >= 8)
        {
            double[] N0 = use.Select(b => n0[b]).ToArray();
            double rD = Oscillation.Spearman(N0, use.Select(b => dead[b] / n0[b]).ToArray());
            double rK = Oscillation.Spearman(N0, use.Select(b => kids2[b] / n0[b]).ToArray());
            double rG = Oscillation.Spearman(N0, use.Select(b => Math.Log((n1[b] + 1) / (n0[b] + 1))).ToArray());
            yield return string.Create(PredInv, $"regions 16×16 with ≥ 5 bodies: {use.Length}, N0 median {N0.OrderBy(v => v).ElementAt(N0.Length / 2):F0} max {N0.Max():F0}; ρ(N0, deaths per body) {rD:F3}, ρ(N0, children per body) {rK:F3}, ρ(N0, growth ln N1/N0) {rG:F3}");
        }
    }
}

// How long the excitation the dead leave on open ground lasts (observation, between ticks): the loose
// excitation Σ C·Gap of a death's cell is followed for Horizon ticks after the death (up to Max deaths at once);
// its decay rate k = −ln(x/x0)/age at the last look, and τ = 1/median k, by the cell's temperature and wetness
// (water + rain) at the death. Whatever takes the excitation away counts: relaxation and decay on the ground,
// bodies taking it in, leaching; whatever adds to it (other deaths, vents) slows it. The expectation from the
// decay law alone is EnvEvery/(LooseDecayK·TempFactor(T)), whatever the wetness; with ArrheniusDecay 1, 1/k of the
// law for the excited species of median barrier, dry and fully wet.
public sealed class RemainsLife
{
    public const int Every = 25, Horizon = 3000, Max = 4000;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    readonly List<(int cell, long t0, float temp, float wet, double x0)> live = new();
    readonly List<(float temp, float wet, double k)> rates = new();

    public void Update(World w)
    {
        this.w = w;
        var p = w.PredProbe;
        while (p.Deceased.TryDequeue(out var d))
        {
            double x0 = w.LooseExcitation(d.cell);
            if (live.Count < Max && x0 > 0) live.Add((d.cell, d.tick, d.temp, d.wet, x0));
        }
        for (int i = live.Count - 1; i >= 0; i--)
        {
            var r = live[i];
            long age = w.Tick - r.t0;
            if (age <= 0) continue;
            double x = w.LooseExcitation(r.cell), k = x <= 0 ? 10.0 / age : Math.Max(0, -Math.Log(x / r.x0) / age);
            if (age >= Horizon) { rates.Add((r.temp, r.wet, k)); live.RemoveAt(i); }
            else current[(r.cell, r.t0)] = (r.temp, r.wet, k);
        }
    }
    readonly Dictionary<(int, long), (float temp, float wet, double k)> current = new();

    // ArrheniusDecay 1: the law's lifetime at tC for the excited species of median barrier, dry / fully wet.
    static string ArrheniusExpect(World w, float tC)
    {
        var d = w.Decay;
        var ex = d.List.Where(s => w.Chem.Gap[s] > 0).OrderBy(s => d.Barrier[s]).ToList();
        if (ex.Count == 0) return "-";
        int s = ex[ex.Count / 2];
        return string.Create(Inv, $"{1 / w.DecayRate(s, tC, 0):F0} dry / {1 / w.DecayRate(s, tC, 1):F0} wet");
    }

    World w;
    public IEnumerable<string> Report()
    {
        var all = rates.Concat(current.Values).ToList();
        if (all.Count == 0) yield break;
        string Tau(IEnumerable<double> ks)
        {
            var v = ks.OrderBy(k => k).ToList();
            if (v.Count == 0) return "-";
            double m = v[v.Count / 2];
            return m > 0 ? (1 / m).ToString("F0", Inv) : "∞";
        }
        var tb = new (string name, float lo, float hi)[] { ("<5 °C", -99, 5), ("5–15", 5, 15), ("15–25", 15, 25), ("≥25", 25, 99) };
        var parts = new List<string>();
        foreach (var (name, lo, hi) in tb)
        {
            var bin = all.Where(r => r.temp >= lo && r.temp < hi).ToList();
            if (bin.Count == 0) continue;
            float mid = Math.Clamp((lo + hi) / 2, -5, 30);
            string expect = World.ArrheniusLaw ? ArrheniusExpect(w, mid) : (P.EnvEvery / (P.LooseDecayK * World.TempFactor(mid))).ToString("F0", Inv);
            parts.Add(string.Create(Inv, $"{name}: τ {Tau(bin.Select(r => r.k))} (n {bin.Count}; dry {Tau(bin.Where(r => r.wet <= 0).Select(r => r.k))}, wet {Tau(bin.Where(r => r.wet > 0).Select(r => r.k))}; wet ≥ 0.5 {Tau(bin.Where(r => r.wet >= 0.5f).Select(r => r.k))}; decay law alone {expect})"));
        }
        yield return Loc.T("remains' excitation on open ground, lifetime in ticks by temperature: ", "возбуждение останков на открытой земле, время жизни в тиках по температуре: ") + string.Join("; ", parts);
    }
}

public static class PredationReportExt
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IEnumerable<string> Report(this PredationProbe p)
    {
        double gainE = p.EnvE + p.TornE + p.TakenE + p.Photo + p.Store;
        double Sh(double x, double of) => of > 0 ? x / of : double.NaN;
        yield return string.Create(Inv, $"attacks {p.Attacks} (no molecule {Sh(p.Missed, p.Attacks):P0}), paid {p.AttackWork:F0}, torn {p.Torn} molecules = {p.TornE:F0} bond energy ({p.TornSplit:F0} as splits), {Sh(p.TornE, p.AttackWork):F2} bond energy per energy paid, {Sh(p.AttackWork, p.Torn):F2} paid per molecule; from close kin {Sh(p.TornKin, p.Torn):P0}, own lineage {Sh(p.TornLineage, p.Torn):P0}; victims' injury {p.Injury:F0}");
        yield return string.Create(Inv, $"kills {p.Kills} (close kin {Sh(p.KillsKin, p.Kills):P0}, own lineage {Sh(p.KillsLineage, p.Kills):P0}), their remains {p.Remains} molecules = {p.RemainsE:F0}");
        yield return string.Create(Inv, $"deaths on open ground {p.Deaths}: remains {p.DeathE:F0} bond energy, {p.DeathX:F1} of it excitation ({Sh(p.DeathX, p.Photo):P1} of the photons caught); scavenged (taken in where remains lie) ≤ {p.Scavenged} molecules = {p.ScavengedE:F0} bond energy ({Sh(p.ScavengedE, p.DeathE):P1} of the remains), {p.ScavengedX:F1} excitation ({Sh(p.ScavengedX, p.DeathX):P1})");
        yield return string.Create(Inv, $"take: tries {p.Takes}, pulled {p.Taken} = {p.TakenE:F0}, paid {p.TakeWork:F1} ({Sh(p.TakeWork, p.Taken):F2} per molecule); close kin {Sh(p.TakenKin, p.Taken):P0}, own lineage {Sh(p.TakenLineage, p.Taken):P0}");
        yield return string.Create(Inv, $"energy into bodies {gainE:F0}: photons {Sh(p.Photo, gainE):P1}, environment molecules {Sh(p.EnvE, gainE):P1} ({p.EnvMols}), from bodies {Sh(p.TornE + p.TakenE + p.Store, gainE):P2} (attack {Sh(p.TornE, gainE):P2}, take {Sh(p.TakenE, gainE):P2}, stores carried {Sh(p.Store, gainE):P2}); from other kin {Sh(p.TornE + p.TakenE + p.Store, gainE) * (1 - Sh(p.TornKin + p.TakenKin, p.Torn + p.Taken)):P2}; scavenged {Sh(p.ScavengedE, gainE):P2}");
        if (World.MatterLaw)
        {
            double x = p.Photo + p.EnvX + p.TornX + p.TakenX + p.Captured;
            yield return string.Create(Inv, $"charge into bodies (MatterEnergy 1: what they can spend) {x:F0}: photons {Sh(p.Photo, x):P1}, excited molecules taken in {Sh(p.EnvX, x):P1}, from bodies {Sh(p.TornX + p.TakenX, x):P2} (attack {Sh(p.TornX, x):P2}, take {Sh(p.TakenX, x):P2}), reactions in bodies captured {Sh(p.Captured, x):P1}; scavenged {Sh(p.ScavengedX, x):P2}; attack: {Sh(p.TornX, p.AttackWork):F2} charge torn per energy paid");
        }
        var bins = new List<string>();
        for (int b = 0; b < PredationProbe.Bins; b++)
        {
            if (p.BinAttacks[b] == 0) continue;
            double n = p.BinAttacks[b];
            bins.Add(string.Create(Inv, $"c {p.BinCohesion[b] / n:F2}: {p.BinAttacks[b]} att, {p.BinTorn[b] / n:F2}/att, {Sh(p.BinTorn[b], p.BinWork[b]):F3}/energy, mass {p.BinMass[b] / n:F1}"));
        }
        if (bins.Count > 0) yield return "defence by victim cohesion: " + string.Join("; ", bins);
        yield return string.Create(Inv, $"parasites: injections {p.Injects} (into another lineage {p.InjectsForeign}, wrote code {p.InjectsDone}); foreign code inherited by children {p.Inherited}; copied on by a host {p.Spread}");
    }
}

// Lotka–Volterra test on a hunter series (counts sampled at regular ticks). Hunters H (World.Diet = hunting)
// and the rest V = pop − H. Both are detrended (log, minus a centred moving average over `smooth`
// samples, so slow drift and booms do not count as cycles). Reported:
//   share_osc — the standard deviation of the detrended hunter share over its sampling noise
//     (√(p(1 − p)/N) at each sample): above ~1 the share moves more than drawing bodies at random would;
//   cross-correlation of detrended log H and log V at lags −L…L samples (H later than V: positive lag):
//     the lag and value of the strongest positive correlation, and the correlation at lag 0; in LV cycles
//     hunters follow prey (positive correlation at a positive lag, a quarter period) and are in
//     antiphase half a period off;
//   p — the share of circularly shifted surrogates of H (all shifts ≥ L) whose strongest positive
//     correlation at a positive lag is at least as strong: a test against chance alignment.
public static class Oscillation
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static double Spearman(double[] x, double[] y)
    {
        double[] rx = Ranks(x), ry = Ranks(y);
        return Pearson(rx, ry);
    }

    static double[] Ranks(double[] v)
    {
        var idx = Enumerable.Range(0, v.Length).OrderBy(i => v[i]).ToArray();
        var r = new double[v.Length];
        for (int i = 0; i < idx.Length;)
        {
            int j = i;
            while (j + 1 < idx.Length && v[idx[j + 1]] == v[idx[i]]) j++;
            for (int k = i; k <= j; k++) r[idx[k]] = (i + j) / 2.0;
            i = j + 1;
        }
        return r;
    }

    public static double Pearson(double[] x, double[] y)
    {
        int n = x.Length;
        if (n < 3) return double.NaN;
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < n; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); syy += (y[i] - my) * (y[i] - my); }
        return sxx > 0 && syy > 0 ? sxy / Math.Sqrt(sxx * syy) : double.NaN;
    }

    static double[] Detrend(double[] v, int smooth)
    {
        int n = v.Length, h = smooth / 2;
        var r = new double[n];
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - h), b = Math.Min(n - 1, i + h);
            double m = 0;
            for (int k = a; k <= b; k++) m += v[k];
            r[i] = v[i] - m / (b - a + 1);
        }
        return r;
    }

    // Correlation of x[i] with y[i + lag] (y later by lag) over the overlap.
    static double Lagged(double[] x, double[] y, int lag)
    {
        int n = x.Length;
        var xs = new List<double>(); var ys = new List<double>();
        for (int i = 0; i < n; i++) { int j = i + lag; if (j < 0 || j >= n) continue; xs.Add(x[i]); ys.Add(y[j]); }
        return Pearson(xs.ToArray(), ys.ToArray());
    }

    public sealed class Result
    {
        public int Samples;
        public double ShareOsc = double.NaN, Lag0 = double.NaN, Best = double.NaN, P = double.NaN, MeanShare;
        public int BestLag;
    }

    // `skip`: samples left out at the start (the first bodies are random genomes).
    public static Result Measure(double[] pop, double[] hunters, int smooth = 9, int maxLag = 8, int skip = 5)
    {
        var res = new Result();
        int n0 = pop.Length;
        var idx = Enumerable.Range(skip, Math.Max(0, n0 - skip)).Where(i => pop[i] >= 10).ToArray();
        res.Samples = idx.Length;
        if (idx.Length < 2 * smooth) return res;
        double[] N = idx.Select(i => pop[i]).ToArray(), Hc = idx.Select(i => hunters[i]).ToArray();
        double[] share = N.Select((v, i) => Hc[i] / v).ToArray();
        res.MeanShare = share.Average();
        var ds = Detrend(share, smooth);
        double noise = 0;
        for (int i = 0; i < N.Length; i++) noise += share[i] * (1 - share[i]) / N[i];
        noise = Math.Sqrt(noise / N.Length);
        double sd = Math.Sqrt(ds.Select(x => x * x).Average());
        res.ShareOsc = noise > 0 ? sd / noise : double.NaN;
        double[] lh = Detrend(Hc.Select(v => Math.Log(v + 1)).ToArray(), smooth), lv = Detrend(N.Select((v, i) => Math.Log(v - Hc[i] + 1)).ToArray(), smooth);
        res.Lag0 = Lagged(lv, lh, 0);
        (int lag, double c) BestPos(double[] h)
        {
            int bl = 0; double bc = double.NegativeInfinity;
            for (int L = 1; L <= maxLag; L++) { double c = Lagged(lv, h, L); if (!double.IsNaN(c) && c > bc) { bc = c; bl = L; } }
            return (bl, bc);
        }
        (res.BestLag, res.Best) = BestPos(lh);
        int m = lh.Length, beat = 0, tried = 0;
        for (int sh = maxLag + 1; sh <= m - maxLag - 1; sh++)
        {
            var s = new double[m];
            for (int i = 0; i < m; i++) s[i] = lh[(i + sh) % m];
            tried++;
            if (BestPos(s).c >= res.Best) beat++;
        }
        res.P = tried > 0 ? (beat + 1) / (double)(tried + 1) : double.NaN;
        return res;
    }

    public static IEnumerable<string> Describe(double[] ticks, double[] pop, double[] hunters)
    {
        var r = Measure(pop, hunters);
        double step = ticks.Length > 1 ? ticks[1] - ticks[0] : 0;
        yield return string.Create(Inv, $"hunters: mean share {r.MeanShare:P1} over {r.Samples} samples; share_osc {r.ShareOsc:F2} (σ/noise); corr(V, H) lag 0 {r.Lag0:F2}; strongest H-after-V {r.Best:F2} at lag {r.BestLag} ({r.BestLag * step:F0} ticks), surrogate p {r.P:F3}");
    }

    // --osc dir/runs.csv [--from TICK] : the test for every run of a batch (rows by seed, rep, tick), with
    // the diet_hunter and pop columns, and a summary: how many runs oscillate beyond noise (share_osc > 2)
    // with hunters following prey (p < 0.05).
    public static void FromRuns(string[] args)
    {
        int i = Array.IndexOf(args, "--osc");
        string path = args[i + 1];
        int from = int.Parse(args.SkipWhile(a => a != "--from").Skip(1).FirstOrDefault() ?? "0");
        var lines = File.ReadAllLines(path);
        var head = lines[0].Split(',');
        int cs = Array.IndexOf(head, "seed"), cr = Array.IndexOf(head, "rep"), ct = Array.IndexOf(head, "tick"), cp = Array.IndexOf(head, "pop"), ch = Array.IndexOf(head, "diet_hunter");
        var runs = lines.Skip(1).Select(l => l.Split(',')).Where(f => f.Length > ch)
            .Select(f => (seed: int.Parse(f[cs]), rep: int.Parse(f[cr]), tick: int.Parse(f[ct]), pop: double.Parse(f[cp], Inv), share: double.TryParse(f[ch], NumberStyles.Float, Inv, out var v) ? v : 0))
            .Where(x => x.tick >= from)
            .GroupBy(x => (x.seed, x.rep)).OrderBy(g => g.Key.seed).ThenBy(g => g.Key.rep);
        int n = 0, osc = 0, lv = 0, both = 0;
        var oscs = new List<double>();
        Console.WriteLine("seed rep samples share   share_osc lag0   best  lag  p");
        foreach (var g in runs)
        {
            var rows = g.OrderBy(x => x.tick).ToArray();
            double[] pop = rows.Select(x => x.pop).ToArray(), h = rows.Select(x => Math.Round(x.share * x.pop)).ToArray();
            var r = Measure(pop, h, skip: 0);
            if (r.Samples < 18) continue;
            n++;
            oscs.Add(r.ShareOsc);
            bool o = r.ShareOsc > 2, l = r.P < 0.05 && r.Best > 0;
            if (o) osc++; if (l) lv++; if (o && l) both++;
            Console.WriteLine(string.Create(Inv, $"{g.Key.seed,4} {g.Key.rep,3} {r.Samples,7} {r.MeanShare,6:P1} {r.ShareOsc,9:F2} {r.Lag0,6:F2} {r.Best,6:F2} {r.BestLag,3} {r.P,6:F3}"));
        }
        oscs.Sort();
        Console.WriteLine(string.Create(Inv, $"runs {n}: share_osc median {(oscs.Count > 0 ? oscs[oscs.Count / 2] : double.NaN):F2}; share_osc > 2 in {osc}; hunters follow prey (p < 0.05) in {lv}; both in {both} (by chance ≈ {0.05 * n:F1} of {n})"));
    }
}
