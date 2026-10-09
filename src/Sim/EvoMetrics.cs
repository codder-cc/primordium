using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Primordium;

// Measures of evolution itself, not of how many bodies there are. Computed on demand from the living
// bodies (a few tens of ms for 20 000 of them: fine every ~1000 ticks). Call between ticks. The
// tracker remembers what earlier samples saw, for turnover and novelty; one tracker per world.
//
// Definitions (all over living bodies at the moment of the sample):
// - genomes: distinct genomes (Agent.Hash);
// - kin clusters: groups of genome fingerprints (Agent.Tag, SimHash) whose members are within
//   KinRadius bits of the group's first member, groups formed greedily from the most common
//   fingerprint down — the number of "kinds" a kin-recognising body could tell apart;
// - lineages: founders still represented (Agent.Lineage — the abiogenesis newcomer every body
//   descends from); entropy H = −Σ p ln p over their shares, effective lineages e^H;
// - dominant share / age: the biggest lineage's share, and ticks since it was first seen (at its
//   first sample its age is taken as its oldest member's age: a lower bound);
// - lost / new: lineages seen at the previous sample and gone now / not seen before;
// - generation: mean and max Agent.Gen;
// - genome length (mean) and used code: mean share of genome bytes with protection > 20 (Prot
//   grows on bytes whose instruction or protein did something; the census's "protected");
// - protein repertoire: distinct protein specs (kind, targets) held with amount ≥ 0.5 — "useful"
//   ones also name a reaction that exists (a legal bind, a split, an excitation) — and the share of
//   bodies holding each kind of catalysis;
// - diet mix: shares of World.Diet categories (by what actually fed them lately);
// - novelty: useful protein specs never seen at an earlier sample (and how many have been seen in
//   all), and the materials first broken with a protein's help (World.Firsts);
// - where bodies live (ROADMAP 7): roofed_share — share with ≥ 1 solid block above them (exact: voids
//   are skipped, World.Roof); body depth = levels below the top of their column (0 on the surface), its
//   mean, 90th percentile and maximum over all bodies, and depth_levels_eff = e^H over the occupied
//   depth levels (1: everybody at one depth; it grows as bodies spread over more levels); mine_depth —
//   the mean depth below the column's top of the molecules torn out of rock since the previous sample
//   (World.GeoMined; NaN if nothing was mined), mined_n — how many;
// - spatial heterogeneity over 32×32-cell regions (8 × 5 of them; x wraps, y does not; rook neighbours):
//   pop_moran — Moran's I of bodies per region over all regions (≈ −1/39 for a random layout, > 0
//   clumped into neighbouring regions, < 0 a checkerboard); pop_regions_eff — e^H of the bodies over
//   regions (how many regions hold them, effectively); diet_moran — Moran's I of each diet's share among
//   the populated regions (≥ 5 bodies), averaged with the diets' planet shares as weights (NaN with
//   fewer than 3 such regions); diet_beta_rel — (H(planet) − Σ_r w_r H(r)) / H(planet), the share of the
//   diet entropy that the region explains (0: every region eats the same mix, 1: one diet per region);
// - oscillation of diet shares over the last ≤ OscWindow equally spaced samples (the history starts
//   again when the spacing changes): for every pair of diets (a, b) and lag k = 1..OscLags samples,
//   S = corr(Δa(t), Δb(t+k)) − corr(Δb(t), Δa(t+k)) on the first differences of the shares — positive
//   when a's changes are followed by b's and b's by the opposite of a's (prey up → predators up →
//   prey down, a Lotka–Volterra cycle), zero for a common driver (which moves both at once). osc_score
//   is the largest |S| over pairs and lags, osc_lead / osc_follow the diets (World.Diet codes: 0 idle,
//   1 light, 2 chemistry, 3 soil, 4 hunting) and osc_lag the lag in ticks; osc_p — the share of
//   OscSurrogates surrogates (every series of changes shuffled in time on its own, which destroys
//   the timing between them) whose best |S| (the same search over pairs and lags) reaches the observed
//   one ((1 + k) / (1 + n)): the chance of so strong a directed lag between changes that are noise.
//   This null is lenient: it also destroys each series' own rhythm and bursts (a boom or crash moves
//   all shares in a few big steps), so in default worlds it rejects in about half the samples.
//   osc_p_circ / hunt_p_circ — the strict null: every series circularly shifted by its own offset,
//   which keeps its rhythm and bursts and destroys only the timing between series; it cannot tell a
//   coupled cycle from two independent ones with the same period (both show a lag), so a clean
//   periodic pair is not rejected by it either. Trust a cycle when both are small and osc_lag and the
//   leader stay the same over successive windows. hunt_score / hunt_p — the same restricted to
//   pairs with the hunters. NaN with fewer than OscMin differences (sample often: --every 100–200 for cycles of a few
//   thousand ticks). The surrogates use their own fixed-seed generator: the world's random stream is
//   never touched.
public sealed class EvoMetrics
{
    public const int KinRadius = 10;   // bits of 64: Kinship ≥ 54

    public static readonly string[] Names =
    {
        "pop", "genomes", "kin_clusters", "lineages", "lineage_entropy", "lineages_eff", "dom_share", "dom_age",
        "lineages_lost", "new_lineages", "gen_mean", "gen_max", "genome_len", "used_code",
        "specs", "useful_specs", "bind_share", "split_share", "photo_share", "motor_share",
        "diet_plant", "diet_eater", "diet_miner", "diet_hunter", "diet_idle",
        "new_specs", "specs_ever", "firsts",
        "roofed_share", "body_depth_mean", "body_depth_p90", "body_depth_max", "depth_levels_eff", "mine_depth", "mined_n",
        "pop_moran", "pop_regions_eff", "diet_moran", "diet_beta_rel",
        "osc_score", "osc_p", "osc_lag", "osc_lead", "osc_follow", "hunt_score", "hunt_p", "osc_p_circ", "hunt_p_circ",
    };
    public static int Col(string name) => Array.IndexOf(Names, name);
    static readonly int CRoofed = Col("roofed_share"), CPopMoran = Col("pop_moran"), COsc = Col("osc_score");

    public const int Region = 32, OscWindow = 48, OscLags = 8, OscMin = 16, OscSurrogates = 199;
    const int Diets = 5;

    readonly List<(long tick, double[] shares)> dietHistory = new();
    readonly long[] minedSeen = new long[8];

    readonly Dictionary<long, long> lineageSeen = new();   // lineage → tick it was (estimated to be) founded
    HashSet<long> lastLineages = new();
    readonly HashSet<int> specsEver = new();
    bool first = true;

    public double[] Sample(World w)
    {
        var v = new double[Names.Length];
        var live = w.Agents.Where(a => !a.Dead).ToList();
        int pop = live.Count;
        v[0] = pop;
        if (pop == 0)
        {
            v[8] = lastLineages.Count;
            lastLineages = new HashSet<long>();
            v[26] = specsEver.Count;
            v[27] = w.Firsts.Count(f => f != null);
            for (int k = CRoofed; k < Names.Length; k++) v[k] = double.NaN;
            Mining(w, v);
            NoteDiets(w.Tick, null, v);
            return v;
        }
        v[1] = live.Select(a => a.Hash).Distinct().Count();
        v[2] = KinClusters(live);

        var lineages = new Dictionary<long, (int n, int oldest)>();
        foreach (var a in live)
        {
            lineages.TryGetValue(a.Lineage, out var l);
            lineages[a.Lineage] = (l.n + 1, Math.Max(l.oldest, a.Age));
        }
        double h = 0;
        long dom = -1; int domN = 0;
        foreach (var (lin, l) in lineages.OrderBy(p => p.Key))
        {
            double p = l.n / (double)pop;
            h -= p * Math.Log(p);
            if (l.n > domN) { domN = l.n; dom = lin; }
            if (!lineageSeen.ContainsKey(lin)) lineageSeen[lin] = w.Tick - l.oldest;
        }
        v[3] = lineages.Count;
        v[4] = h;
        v[5] = Math.Exp(h);
        v[6] = domN / (double)pop;
        v[7] = w.Tick - lineageSeen[dom];
        var now = new HashSet<long>(lineages.Keys);
        v[8] = first ? 0 : lastLineages.Count(l => !now.Contains(l));
        v[9] = first ? 0 : now.Count(l => !lastLineages.Contains(l));
        lastLineages = now;

        double gen = 0, len = 0, used = 0;
        int genMax = 0;
        var specs = new HashSet<int>();
        var useful = new HashSet<int>();
        var kinds = new int[4];
        var diet = new int[5];
        Span<bool> has = stackalloc bool[4];
        var chem = w.Chem;
        foreach (var a in live)
        {
            gen += a.Gen; genMax = Math.Max(genMax, a.Gen);
            len += a.G.Length;
            int prot = 0;
            foreach (var b in a.Prot) if (b > 20) prot++;
            used += prot / (double)Math.Max(1, a.Prot.Length);
            has.Clear();
            for (int k = 0; k < a.EnzN; k++)
            {
                ref var e = ref a.Enz[k];
                if (e.Amount < 0.5f) continue;
                has[e.Kind] = true;
                int key = e.Kind switch
                {
                    Enzyme.Motor => Enzyme.Motor << 16,
                    Enzyme.Bind => Enzyme.Bind << 16 | Math.Min(e.A, e.B) << 8 | Math.Max(e.A, e.B),
                    _ => e.Kind << 16 | e.A << 8,
                };
                specs.Add(key);
                bool works = e.Kind switch
                {
                    Enzyme.Bind => chem.Combine[e.A, e.B] >= 0,
                    Enzyme.Split => chem.SplitA[e.A] >= 0,
                    Enzyme.Photo => chem.PhotoUp[e.A] >= 0,
                    _ => true,
                };
                if (works) useful.Add(key);
            }
            for (int k = 0; k < 4; k++) if (has[k]) kinds[k]++;
            diet[World.Diet(a)]++;
        }
        v[10] = gen / pop; v[11] = genMax;
        v[12] = len / pop; v[13] = used / pop;
        v[14] = specs.Count; v[15] = useful.Count;
        for (int k = 0; k < 4; k++) v[16 + k] = kinds[k] / (double)pop;
        v[20] = diet[World.DietPlant] / (double)pop;
        v[21] = diet[World.DietEater] / (double)pop;
        v[22] = diet[World.DietMiner] / (double)pop;
        v[23] = diet[World.DietHunter] / (double)pop;
        v[24] = diet[World.DietIdle] / (double)pop;
        int fresh = 0;
        foreach (int k in useful) if (specsEver.Add(k)) fresh++;
        v[25] = first ? 0 : fresh;
        v[26] = specsEver.Count;
        v[27] = w.Firsts.Count(f => f != null);
        first = false;
        Depths(w, live, v);
        Mining(w, v);
        Spatial(w, live, v);
        var shares = new double[Diets];
        for (int k = 0; k < Diets; k++) shares[k] = diet[k] / (double)pop;
        NoteDiets(w.Tick, shares, v);
        return v;
    }

    // ---- where bodies live ----

    void Depths(World w, List<Agent> live, double[] v)
    {
        var depth = new int[live.Count];
        int roofed = 0, deepest = 0;
        double sum = 0;
        var levels = new Dictionary<int, int>();
        for (int i = 0; i < live.Count; i++)
        {
            var a = live[i];
            int c = a.Y * w.W + a.X;
            if (w.Roof(c, a.Z) >= 1) roofed++;
            int d = Math.Max(0, w.Height[c] - 1 - a.Z);
            depth[i] = d; sum += d; deepest = Math.Max(deepest, d);
            levels[d] = levels.GetValueOrDefault(d) + 1;
        }
        Array.Sort(depth);
        int n = live.Count;
        v[CRoofed] = roofed / (double)n;
        v[CRoofed + 1] = sum / n;
        v[CRoofed + 2] = depth[Math.Clamp((int)Math.Ceiling(0.9 * n) - 1, 0, n - 1)];
        v[CRoofed + 3] = deepest;
        v[CRoofed + 4] = Math.Exp(Entropy(levels.Values, n));
    }

    // Molecules mined since the previous sample, and their mean depth (World.GeoMined bins 0–4 count them).
    void Mining(World w, double[] v)
    {
        var g = w.GeoMined;
        long n = 0;
        for (int k = 0; k < 5; k++) n += g[k] - minedSeen[k];
        long depthSum = g[World.GeoMinedDepthSum] - minedSeen[World.GeoMinedDepthSum];
        if (n < 0 || depthSum < 0) { n = 0; for (int k = 0; k < 5; k++) n += g[k]; depthSum = g[World.GeoMinedDepthSum]; }   // a reset world: count from zero
        Array.Copy(g, minedSeen, minedSeen.Length);
        v[CRoofed + 6] = n;
        v[CRoofed + 5] = n > 0 ? depthSum / (double)n : double.NaN;
    }

    static double Entropy(IEnumerable<int> counts, int total)
    {
        if (total <= 0) return 0;
        double h = 0;
        foreach (int x in counts) if (x > 0) { double p = x / (double)total; h -= p * Math.Log(p); }
        return h;
    }

    // ---- spatial heterogeneity over 32×32 regions ----

    // Rook neighbours of region r in rows of RX regions (x wraps around the planet, y stops at the poles).
    static IEnumerable<int> Neighbours(int r, int RX, int RY)
    {
        int x = r % RX, y = r / RX;
        yield return y * RX + (x + 1) % RX;
        yield return y * RX + (x + RX - 1) % RX;
        if (y > 0) yield return (y - 1) * RX + x;
        if (y < RY - 1) yield return (y + 1) * RX + x;
    }

    // Moran's I of x over the regions where use[r] (rows of rx regions), binary weights between used rook neighbours.
    public static double Moran(double[] x, bool[] use, int rx)
    {
        int ry = x.Length / rx;
        int n = 0; double mean = 0;
        for (int r = 0; r < x.Length; r++) if (use[r]) { n++; mean += x[r]; }
        if (n < 3) return double.NaN;
        mean /= n;
        double den = 0, num = 0, wsum = 0;
        for (int r = 0; r < x.Length; r++)
        {
            if (!use[r]) continue;
            double dr = x[r] - mean;
            den += dr * dr;
            foreach (int q in Neighbours(r, rx, ry))
            {
                if (q == r || !use[q]) continue;
                num += dr * (x[q] - mean); wsum++;
            }
        }
        if (den <= 1e-18 || wsum == 0) return double.NaN;
        return n / wsum * num / den;
    }

    static void Spatial(World w, List<Agent> live, double[] v)
    {
        int RX = w.W / Region, Regions = RX * (w.H / Region);
        var pop = new double[Regions];
        var dietIn = new int[Regions, Diets];
        var all = new int[Diets];
        foreach (var a in live)
        {
            int r = (a.Y / Region) * RX + a.X / Region, d = World.Diet(a);
            pop[r]++; dietIn[r, d]++; all[d]++;
        }
        int n = live.Count;
        var every = new bool[Regions];
        Array.Fill(every, true);
        v[CPopMoran] = Moran(pop, every, RX);
        v[CPopMoran + 1] = Math.Exp(Entropy(pop.Select(p => (int)p), n));

        var used = new bool[Regions];
        for (int r = 0; r < Regions; r++) used[r] = pop[r] >= 5;
        double moran = 0, weight = 0;
        var x = new double[Regions];
        for (int d = 0; d < Diets; d++)
        {
            double share = all[d] / (double)n;
            if (share <= 0) continue;
            for (int r = 0; r < Regions; r++) x[r] = used[r] ? dietIn[r, d] / pop[r] : 0;
            double m = Moran(x, used, RX);
            if (double.IsNaN(m)) continue;
            moran += share * m; weight += share;
        }
        v[CPopMoran + 2] = weight > 0 ? moran / weight : double.NaN;

        double hAll = Entropy(all, n), hIn = 0;
        var row = new int[Diets];
        for (int r = 0; r < Regions; r++)
        {
            if (pop[r] == 0) continue;
            for (int d = 0; d < Diets; d++) row[d] = dietIn[r, d];
            hIn += pop[r] / n * Entropy(row, (int)pop[r]);
        }
        v[CPopMoran + 3] = hAll > 1e-12 ? Math.Max(0, hAll - hIn) / hAll : 0;
    }

    // ---- oscillation of diet shares ----

    void NoteDiets(long tick, double[] shares, double[] v)
    {
        shares ??= new double[Diets];
        if (dietHistory.Count > 0 && tick <= dietHistory[^1].tick) dietHistory.Clear();   // another world or a load
        if (dietHistory.Count >= 2 && tick - dietHistory[^1].tick != dietHistory[^1].tick - dietHistory[^2].tick)
            dietHistory.RemoveRange(0, dietHistory.Count - 1);   // the spacing changed: start again from the last one
        dietHistory.Add((tick, shares));
        if (dietHistory.Count > OscWindow + 1) dietHistory.RemoveAt(0);
        for (int k = COsc; k < COsc + 9; k++) v[k] = double.NaN;
        int n = dietHistory.Count - 1;
        if (n < OscMin) return;
        long step = dietHistory[1].tick - dietHistory[0].tick;
        var diff = new double[Diets][];
        for (int d = 0; d < Diets; d++)
        {
            diff[d] = new double[n];
            for (int t = 0; t < n; t++) diff[d][t] = dietHistory[t + 1].shares[d] - dietHistory[t].shares[d];
        }
        var (score, lag, lead, follow) = Best(diff, -1);
        if (double.IsNaN(score)) return;
        v[COsc] = score; v[COsc + 2] = lag * step; v[COsc + 3] = lead; v[COsc + 4] = follow;
        double hunt = Best(diff, World.DietHunter).score;
        v[COsc + 5] = hunt;
        // Surrogates: each series of changes shuffled in time on its own (deterministic, own generator).
        var rng = new SimRng(0x05C1, tick);
        var shifted = new double[Diets][];
        for (int d = 0; d < Diets; d++) shifted[d] = (double[])diff[d].Clone();
        int above = 0, aboveHunt = 0;
        for (int s = 0; s < OscSurrogates; s++)
        {
            foreach (var x in shifted)
                for (int t = n - 1; t > 0; t--) { int j = rng.Next(t + 1); (x[t], x[j]) = (x[j], x[t]); }
            double b = Best(shifted, -1).score;
            if (!double.IsNaN(b) && b >= score - 1e-12) above++;
            if (!double.IsNaN(hunt)) { double h = Best(shifted, World.DietHunter).score; if (!double.IsNaN(h) && h >= hunt - 1e-12) aboveHunt++; }
        }
        v[COsc + 1] = (1 + above) / (1.0 + OscSurrogates);
        v[COsc + 6] = double.IsNaN(hunt) ? double.NaN : (1 + aboveHunt) / (1.0 + OscSurrogates);
        // The strict null: each series circularly shifted by its own offset (keeps its own rhythm and bursts).
        above = aboveHunt = 0;
        for (int s = 0; s < OscSurrogates; s++)
        {
            for (int d = 0; d < Diets; d++)
            {
                int off = rng.Next(n);
                for (int t = 0; t < n; t++) shifted[d][t] = diff[d][(t + off) % n];
            }
            double b = Best(shifted, -1).score;
            if (!double.IsNaN(b) && b >= score - 1e-12) above++;
            if (!double.IsNaN(hunt)) { double h = Best(shifted, World.DietHunter).score; if (!double.IsNaN(h) && h >= hunt - 1e-12) aboveHunt++; }
        }
        v[COsc + 7] = (1 + above) / (1.0 + OscSurrogates);
        v[COsc + 8] = double.IsNaN(hunt) ? double.NaN : (1 + aboveHunt) / (1.0 + OscSurrogates);
    }

    // The largest |S| over pairs of diets (only pairs with `only`, if ≥ 0) and lags; which leads, which follows.
    static (double score, int lag, int lead, int follow) Best(double[][] diff, int only)
    {
        double best = double.NaN; int bl = 0, la = -1, fo = -1;
        for (int a = 0; a < Diets; a++)
            for (int b = a + 1; b < Diets; b++)
            {
                if (only >= 0 && a != only && b != only) continue;
                for (int k = 1; k <= OscLags; k++)
                {
                    double ab = Corr(diff[a], diff[b], k), ba = Corr(diff[b], diff[a], k);
                    if (double.IsNaN(ab) || double.IsNaN(ba)) continue;
                    double s = ab - ba;
                    if (double.IsNaN(best) || Math.Abs(s) > best) { best = Math.Abs(s); bl = k; la = s >= 0 ? a : b; fo = s >= 0 ? b : a; }
                }
            }
        return (best, bl, la, fo);
    }

    // Pearson correlation of x(t) and y(t + k) over the overlap; NaN if either is constant there.
    static double Corr(double[] x, double[] y, int k)
    {
        int m = x.Length - k;
        if (m < 4) return double.NaN;
        double mx = 0, my = 0;
        for (int t = 0; t < m; t++) { mx += x[t]; my += y[t + k]; }
        mx /= m; my /= m;
        double sxy = 0, sxx = 0, syy = 0;
        for (int t = 0; t < m; t++)
        {
            double dx = x[t] - mx, dy = y[t + k] - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        if (sxx < 1e-18 || syy < 1e-18) return double.NaN;
        return sxy / Math.Sqrt(sxx * syy);
    }

    // Self-check of the oscillation statistic on made-up series (no world): a lagged predator–prey pair
    // must score far above its shuffled surrogates, independent noise must not (by either null).
    public static (double cycle, double noise, double noiseCirc) OscSelfCheck()
    {
        var m = new EvoMetrics();
        var rng = new SimRng(77);
        double pc = double.NaN, pn = double.NaN, pnc = double.NaN;
        for (int t = 0; t <= OscWindow; t++)
        {
            double ph = 2 * Math.PI * t / 16.0;
            var s = new double[Diets];
            s[World.DietPlant] = 0.5 + 0.2 * Math.Sin(ph) + 0.01 * (rng.NextDouble() - 0.5);
            s[World.DietHunter] = 0.1 + 0.05 * Math.Sin(ph - Math.PI / 2) + 0.005 * (rng.NextDouble() - 0.5);
            s[World.DietEater] = 1 - s[World.DietPlant] - s[World.DietHunter];
            var v = new double[Names.Length];
            m.NoteDiets(1000 + t * 100, s, v);
            pc = v[COsc + 6];
        }
        var q = new EvoMetrics();
        for (int t = 0; t <= OscWindow; t++)
        {
            var s = new double[Diets];
            s[World.DietPlant] = 0.5 + 0.1 * (rng.NextDouble() - 0.5);
            s[World.DietHunter] = 0.1 + 0.05 * (rng.NextDouble() - 0.5);
            s[World.DietEater] = 1 - s[World.DietPlant] - s[World.DietHunter];
            var v = new double[Names.Length];
            q.NoteDiets(1000 + t * 100, s, v);
            pn = v[COsc + 6]; pnc = v[COsc + 8];
        }
        return (pc, pn, pnc);
    }

    // Greedy clusters of fingerprints, the most common first; each joins the first cluster whose
    // founder is within KinRadius bits.
    static int KinClusters(List<Agent> live)
    {
        var tags = live.GroupBy(a => a.Tag).Select(g => (tag: g.Key, n: g.Count())).OrderByDescending(t => t.n).ThenBy(t => t.tag).ToList();
        var leaders = new List<ulong>();
        foreach (var (tag, _) in tags)
        {
            bool joined = false;
            foreach (var l in leaders) if (BitOperations.PopCount(l ^ tag) <= KinRadius) { joined = true; break; }
            if (!joined) leaders.Add(tag);
        }
        return leaders.Count;
    }

    public static string Format(double[] v)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string F(int k, string f = "F0") => v[k].ToString(f, inv);
        return $"genomes {F(1)} kin clusters {F(2)} | lineages {F(3)} (H {F(4, "F2")}, eff {F(5, "F1")}; dominant {v[6]:P0} for {F(7)} t; lost {F(8)} new {F(9)})"
             + $" | gen {F(10, "F1")}/{F(11)} len {F(12)} used {v[13]:P1}"
             + $" | specs {F(14)} useful {F(15)} (new {F(25)}, ever {F(26)}); with bind {v[16]:P0} split {v[17]:P0} photo {v[18]:P0} motor {v[19]:P0}"
             + $" | diet plant {v[20]:P0} eat {v[21]:P0} mine {v[22]:P0} hunt {v[23]:P0} idle {v[24]:P0} | firsts {F(27)}"
             + $" | roofed {v[CRoofed]:P1} depth {F(CRoofed + 1, "F2")} p90 {F(CRoofed + 2)} max {F(CRoofed + 3)} levels {F(CRoofed + 4, "F2")}; mined {F(CRoofed + 6)} at depth {F(CRoofed + 5, "F2")}"
             + $" | regions: pop Moran {F(CPopMoran, "F2")} eff {F(CPopMoran + 1, "F1")}, diet Moran {F(CPopMoran + 2, "F2")} beta {F(CPopMoran + 3, "F3")}"
             + $" | diet cycles: score {F(COsc, "F2")} p {F(COsc + 1, "F3")} lag {F(COsc + 2)} ({F(COsc + 3)}→{F(COsc + 4)}), hunters {F(COsc + 5, "F2")} p {F(COsc + 6, "F3")}; circular p {F(COsc + 7, "F3")}/{F(COsc + 8, "F3")}";
    }
}
