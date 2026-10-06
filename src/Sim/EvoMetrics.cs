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
//   all), and the materials first broken with a protein's help (World.Firsts).
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
    };

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
        return v;
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
             + $" | diet plant {v[20]:P0} eat {v[21]:P0} mine {v[22]:P0} hunt {v[23]:P0} idle {v[24]:P0} | firsts {F(27)}";
    }
}
