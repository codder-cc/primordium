using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Primordium;

// "Ход эволюции" (ROADMAP 7.1, 9.1): how far evolution has got, measured against a neutral model.
// Observation only: nothing here is read by the simulation, and its random numbers come from its own
// streams (the shadows' SimRng), never from the world's.
//
// Components (Bedau & Packard's evolutionary activity). A body's components are fixed at its birth
// and come from the code that has proven useful in its line: bytes with Agent.Prot > 0 at birth (a
// byte gains protection each time its instruction or protein works; a child inherits its parent's
// protection × 0.9 rounded down, so "used" means used by its parent or grandparents, not long ago):
// - a protein spec: a used `enzyme` instruction and the (kind, target molecules) its gene names;
// - a code motif: the hash of a used byte and the K − 1 = 3 bytes after it (its operands and what
//   it feeds), wherever it stands (a duplicated or moved block is the same component).
// Activity of a component = Σ over ticks of the number of living bodies carrying it.
//
// The neutral shadow (Bedau's null model) has the same births and deaths, tick by tick, as the world,
// but its parent and its victim are picked at random, and nothing is simulated: a shadow child copies
// a random shadow parent's components, then changes as much as the real child did against its real
// parent — it loses each component with the real loss rate per component (over the last ~1000
// births), gains as many brand-new ones as the real child got components the world had never had,
// and as many existing ones (copied from a random shadow body) as the real child got that others
// already had (recurrent use of the same code is common here: siblings start using the same bytes).
// Births and deaths are taken in the order the world merges them.
//
// There are two independent shadows with the same rules. The reference shadow sets the threshold θ
// at each sample: the 95th percentile of its living components' activities. The control shadow and the
// world are both counted against θ: a component whose activity exceeds θ for the first time counts
// once. Novelty = real − control of these cumulative counts. A shadow counted against its own
// percentile would be biased low (its top 5 % define θ, while an independent population gains every
// time θ wobbles down), hence the second shadow: under neutrality real and control are alike (≈ 0),
// selection keeps useful components common for long (> 0). Components that die out are forgotten
// (memory stays bounded); one that comes back later counts as new.
public sealed class NeutralShadow
{
    public const int UsedProt = 0, K = 4;
    public const double Percentile = 0.95;

    // Real components by id (interned from their hash when a body first brings them; slots reused when
    // one dies out): living carriers, activity up to RLast, whether it ever passed θ, its hash.
    public CompSlot[] R = new CompSlot[1024];   // one record per component: a birth or death touches one cache line
    public ulong[] RHash = new ulong[1024];
    public int RHigh, RLiving;
    public readonly List<int> RFree = new();
    public readonly Dictionary<ulong, int> Index = new();   // hash → id of the living ones
    public readonly ShadowPop Ref, Ctrl;
    public long RealCum;                       // real components that ever passed θ
    public double Theta;
    public int RealAbove;                      // living real components above θ at the last sample
    public long RealCarry;                     // Σ carriers over living real components
    public int Births;                         // births seen (a rough check against the world)
    public double LossHad, LossLost;           // decaying sums: components the real parents had, and lost

    public NeutralShadow(long seed = 0x5EED_5AD0)
    {
        Ref = new ShadowPop(new SimRng(seed, 1));
        Ctrl = new ShadowPop(new SimRng(seed, 2));
    }

    public long Novelty => RealCum - Ctrl.Cum;

    // What the shadows still have to do, in order (applied by Flush: both shadows at once, each in order).
    public struct ShadowOp { public bool Birth, HasParent; public int Fresh, Recurrent; public double Rate; public long Tick; }
    List<ShadowOp> pending = new();
    public int Pending => pending.Count;

    List<ShadowOp> spare = new();

    // Applies what is pending now, on this thread.
    public void Flush()
    {
        ApplyBoth(pending);
        pending.Clear();
    }

    // Hands the pending operations over (to be applied with ApplyBoth, possibly on another thread);
    // new ones collect in the other list. The caller applies them before taking again.
    public List<ShadowOp> TakePending()
    {
        var t = pending;
        pending = spare;
        spare = t;
        pending.Clear();
        return t;
    }

    public void ApplyBoth(List<ShadowOp> ops) { Ref.Apply(ops); Ctrl.Apply(ops); }

    static readonly ulong[] NoComp = Array.Empty<ulong>();

    // ---- a body's components ----

    [ThreadStatic] static ulong[] scratch;

    // Its components from its genome and protection; the parent's array itself if they are the same
    // (most children: shared, no allocation). Safe on any thread (reads the body's own arrays).
    public static ulong[] Components(byte[] g, byte[] prot, ulong[] parent)
    {
        int n = g.Length;
        var buf = scratch ??= new ulong[2 * Genome.MaxLen + 16];
        if (buf.Length < 2 * n + 4) buf = scratch = new ulong[2 * n + 16];
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            if (prot[i] <= UsedProt) continue;
            if ((g[i] & 63) == Genome.EnzymeOp && i + 3 < n)
            {
                var e = Genome.Decode(g[i + 1], g[i + 2], g[i + 3]);
                int a = e.Kind == Enzyme.Motor ? 0 : e.A, b = e.Kind == Enzyme.Bind ? e.B : 0;
                if (e.Kind == Enzyme.Bind && b < a) (a, b) = (b, a);
                buf[m++] = 1UL << 63 | (ulong)e.Kind << 16 | (ulong)a << 8 | (ulong)b;
            }
            // The motif: this used byte and the K − 1 after it (its operands and what it feeds).
            ulong h = 0x9E3779B97F4A7C15UL;
            for (int k = i; k < i + K && k < n; k++) { h ^= g[k]; h *= 0x100000001B3UL; h ^= h >> 31; }
            buf[m++] = h & ~(1UL << 63);
        }
        if (m == 0) return parent is { Length: 0 } ? parent : NoComp;
        Array.Sort(buf, 0, m);
        int u = 1;
        for (int i = 1; i < m; i++) if (buf[i] != buf[u - 1]) buf[u++] = buf[i];
        if (parent != null && parent.Length == u && buf.AsSpan(0, u).SequenceEqual(parent)) return parent;
        return buf.AsSpan(0, u).ToArray();
    }


    // ---- births and deaths (between ticks, in the world's merge order) ----

    // A body was born with components `child` (sorted hashes); its parent had `parent` with ids
    // `parentIds` (null: a founder, or a parent never registered). Returns the child's ids — the
    // parent's array itself when nothing changed (most births: no dictionary work at all).
    public int[] Born(ulong[] child, ulong[] parent, int[] parentIds, long tick)
    {
        child ??= NoComp;
        if (parentIds == null) parent = null;
        int fresh = 0, recurrent = 0, lost = 0;
        int[] ids;
        var p = parent ?? NoComp;
        if (parent != null && ReferenceEquals(child, parent)) ids = parentIds;
        else
        {
            ids = child.Length == 0 ? NoIds : new int[child.Length];
            int i = 0, j = 0;
            while (i < child.Length || j < p.Length)   // both sorted
            {
                if (j >= p.Length || (i < child.Length && child[i] < p[j]))
                {
                    if (Index.TryGetValue(child[i], out int id)) recurrent++;
                    else { id = NewReal(child[i], tick); fresh++; }
                    ids[i++] = id;
                }
                else if (i >= child.Length || p[j] < child[i]) { lost++; j++; }
                else ids[i++] = parentIds[j++];
            }
        }
        foreach (int id in ids) RChange(id, tick, +1);
        // The loss rate per component, over the last ~1000 births with a parent: a shadow parent loses each
        // of its components with this chance (losing the real count instead would depend on how big the
        // shadow parent happens to be, and either way bias the shadow's size).
        if (parent != null) { LossHad = LossHad * 0.999 + p.Length; LossLost = LossLost * 0.999 + lost; }
        double rate = LossHad > 0 ? LossLost / LossHad : 0;
        pending.Add(new ShadowOp { Birth = true, HasParent = parent != null, Rate = rate, Fresh = fresh, Recurrent = recurrent, Tick = tick });
        Births++;
        return ids;
    }

    public void Died(int[] ids, long tick)
    {
        if (ids == null) return;   // never registered (made outside the world's paths)
        foreach (int id in ids) RChange(id, tick, -1);
        pending.Add(new ShadowOp { Tick = tick });
    }

    static readonly int[] NoIds = Array.Empty<int>();

    int NewReal(ulong hash, long tick)
    {
        int id;
        if (RFree.Count > 0) { id = RFree[^1]; RFree.RemoveAt(RFree.Count - 1); }
        else
        {
            if (RHigh == R.Length)
            {
                int n = R.Length * 2;
                Array.Resize(ref R, n); Array.Resize(ref RHash, n);
            }
            id = RHigh++;
        }
        R[id].N = 0; R[id].Last = tick; R[id].Act = 0; R[id].Crossed = false; RHash[id] = hash;
        Index[hash] = id;
        return id;
    }

    void RChange(int id, long tick, int d)
    {
        ref var c = ref R[id];
        c.Act += (double)c.N * (tick - c.Last);
        c.Last = tick;
        if (c.N == 0 && d > 0) RLiving++;
        c.N += d;
        if (c.N == 0) { RLiving--; RFree.Add(id); Index.Remove(RHash[id]); }
    }

    // After a load: the hash index from the living ids.
    public void Reindex()
    {
        Index.Clear();
        for (int id = 0; id < RHigh; id++) if (R[id].N > 0) Index[RHash[id]] = id;
    }

    // ---- the sample ----

    public void Measure(long tick)
    {
        Flush();
        Theta = Ref.Percentile(tick, Percentile);
        Ref.Count(tick, Theta);
        Ctrl.Count(tick, Theta);
        RealAbove = 0; RealCarry = 0;
        for (int id = 0; id < RHigh; id++)
        {
            if (R[id].N <= 0) continue;
            RealCarry += R[id].N;
            double a = R[id].Act + (double)R[id].N * (tick - R[id].Last);
            if (a <= Theta) continue;
            RealAbove++;
            if (!R[id].Crossed) { R[id].Crossed = true; RealCum++; }
        }
    }

    public long Bytes => RHigh * 29L + Index.Count * 24L + Ref.Bytes + Ctrl.Bytes;   // rough, without the bodies' arrays
}

// A component's record: living carriers, activity up to Last, whether it ever passed θ.
public struct CompSlot { public int N; public bool Crossed; public long Last; public double Act; }

// One neutral shadow population: components by id (slots reused when a component dies out), bodies as
// arrays of ids (shared between a parent and an unchanged child).
public sealed class ShadowPop
{
    public SimRng Rng;
    public CompSlot[] S = new CompSlot[1024];
    public int High, Living, Above;
    public long Cum, Carry;
    public readonly List<int> Free = new();
    public readonly List<int[]> Pop = new();
    static readonly int[] None = Array.Empty<int>();
    double[] acts = new double[1024];
    readonly List<int> scratch = new();

    public ShadowPop(SimRng rng) => Rng = rng;

    public void Apply(List<NeutralShadow.ShadowOp> ops)
    {
        foreach (var op in ops)
            if (op.Birth) Born(op.HasParent, op.Rate, op.Fresh, op.Recurrent, op.Tick);
            else Died(op.Tick);
    }

    public void Born(bool hasParent, double lossRate, int fresh, int recurrent, long tick)
    {
        int[] src = hasParent && Pop.Count > 0 ? Pop[Rng.Next(Pop.Count)] : None;
        int[] kid;
        var list = scratch;
        list.Clear();
        list.AddRange(src);
        // Each component is lost with the chance lossRate: the gaps between losses are geometric, one
        // random number per loss (+1) instead of one per component.
        if (lossRate > 0 && list.Count > 0)
        {
            double lq = Math.Log(1 - Math.Min(lossRate, 0.999999));
            int write = 0;
            long next = (long)(Math.Log(1 - Rng.NextDouble()) / lq);
            for (int k = 0; k < list.Count; k++)
            {
                if (k == next) { next = k + 1 + (long)(Math.Log(1 - Rng.NextDouble()) / lq); continue; }
                list[write++] = list[k];
            }
            list.RemoveRange(write, list.Count - write);
        }
        if (fresh == 0 && recurrent == 0 && list.Count == src.Length) kid = src;   // unchanged: shared
        else
        {
            for (int r = 0; r < recurrent; r++)
            {
                // A component other shadow bodies carry already: from a random body.
                for (int tries = 0; Pop.Count > 0 && tries < 8; tries++)
                {
                    var donor = Pop[Rng.Next(Pop.Count)];
                    if (donor.Length == 0) continue;
                    int got = donor[Rng.Next(donor.Length)];
                    if (list.Contains(got)) continue;
                    list.Add(got);
                    break;
                }
            }
            for (int r = 0; r < fresh; r++) list.Add(New(tick));
            kid = list.Count == 0 ? None : list.ToArray();
        }
        foreach (int id in kid) Change(id, tick, +1);
        Pop.Add(kid);
    }

    public void Died(long tick)
    {
        if (Pop.Count == 0) return;
        int at = Rng.Next(Pop.Count);
        var victim = Pop[at];
        Pop[at] = Pop[^1];
        Pop.RemoveAt(Pop.Count - 1);
        foreach (int id in victim) Change(id, tick, -1);
    }

    int New(long tick)
    {
        int id;
        if (Free.Count > 0) { id = Free[^1]; Free.RemoveAt(Free.Count - 1); }
        else
        {
            if (High == S.Length) Array.Resize(ref S, S.Length * 2);
            id = High++;
        }
        S[id].N = 0; S[id].Last = tick; S[id].Act = 0; S[id].Crossed = false;
        return id;
    }

    void Change(int id, long tick, int d)
    {
        ref var c = ref S[id];
        c.Act += (double)c.N * (tick - c.Last);
        c.Last = tick;
        if (c.N == 0 && d > 0) Living++;
        c.N += d;
        if (c.N == 0) { Living--; Free.Add(id); }
    }

    public double Percentile(long tick, double q)
    {
        int n = 0;
        if (acts.Length < High) acts = new double[High];
        for (int id = 0; id < High; id++)
            if (S[id].N > 0) acts[n++] = S[id].Act + (double)S[id].N * (tick - S[id].Last);
        if (n == 0) return 0;
        Array.Sort(acts, 0, n);
        return acts[Math.Min(n - 1, (int)(q * n))];
    }

    // Its living components above θ, and those passing it for the first time.
    public void Count(long tick, double theta)
    {
        Above = 0; Carry = 0;
        for (int id = 0; id < High; id++)
        {
            if (S[id].N <= 0) continue;
            Carry += S[id].N;
            double a = S[id].Act + (double)S[id].N * (tick - S[id].Last);
            if (a <= theta) continue;
            Above++;
            if (!S[id].Crossed) { S[id].Crossed = true; Cum++; }
        }
    }

    public long Bytes => High * 21L + Pop.Count * 40L;
}

// The progress tracks over time (ROADMAP 7.1): one sample every P.ProgressEvery ticks into a history
// of at most Cap samples — when it is full every other sample is dropped and samples are kept half as
// often (the whole run stays visible at a coarser grain).
public sealed class EvolutionHistory
{
    public const int Cap = 1024;

    // Columns of a sample. Tracks: novelty, complexity, ecology, tempo, phylogeny; then the hint.
    public static readonly string[] Names =
    {
        "tick", "adaptive", "adaptive_shadow", "novelty", "adaptive_now", "comps", "comps_shadow", "theta", "carry", "carry_shadow",
        "code_used", "body_proteins", "body_reactions", "body_mass", "body_cells",
        "niches", "diet_div", "under_share", "water_share", "lineages_big",
        "dom_changes", "speciations", "extinctions", "dom_drift",
        "mrca_age", "pair_dist", "phylo_div", "phylo_taxa", "phylo_branches", "phylo_roots", "dom_depth", "dom_muts", "phylo_nodes",
        "progress_index",
    };
    public static int Col(string name) => Array.IndexOf(Names, name);
    public static readonly int CTick = 0, CNovelty = Col("novelty"), CCode = Col("code_used"), CNiches = Col("niches"),
        CPairDist = Col("pair_dist"), CIndex = Col("progress_index");

    public readonly List<double[]> Samples = new();
    public int Stride = 1, Skipped;   // a sample is kept every Stride measurements
    public double[] Latest;           // the newest measurement (kept or not)
    public long Version;

    public void Add(double[] v)
    {
        Latest = v;
        Version++;
        if (++Skipped < Stride) return;
        Skipped = 0;
        Samples.Add(v);
        if (Samples.Count < Cap) return;
        for (int i = 0; i < Cap / 2; i++) Samples[i] = Samples[2 * i + 1];
        Samples.RemoveRange(Cap / 2, Samples.Count - Cap / 2);
        Stride *= 2;
    }

    // The summary hint: for each of four tracks (novelty, used code, niches, mean pairwise phylogenetic
    // distance) the change of its least-squares line over the last `window` ticks, relative to its mean
    // size there (clamped to ±1); the median of the four. ≥ +0.05 "идёт", ≤ −0.05 "откат", else
    // "стагнирует". A hint, not a verdict: the tracks are always shown next to it.
    public static readonly int[] IndexTracks = { CNovelty, CCode, CNiches, CPairDist };
    public static readonly string[] IndexTrackNames = { "новизна", "сложность", "ниши", "расхождение" };

    public static (double index, double[] trends) Index(IReadOnlyList<double[]> s, long window, double[] latest = null)
    {
        var trends = new double[IndexTracks.Length];
        if (s.Count == 0) return (0, trends);
        long end = (long)(latest ?? s[^1])[CTick], from = end - window;
        var xs = new List<double[]>();
        foreach (var v in s) if (v[CTick] >= from && v[CTick] <= end) xs.Add(v);
        if (latest != null && (xs.Count == 0 || xs[^1][CTick] < latest[CTick])) xs.Add(latest);
        if (xs.Count < 3) return (0, trends);
        for (int k = 0; k < IndexTracks.Length; k++)
        {
            int c = IndexTracks[k];
            double mt = 0, mv = 0, size = 0;
            foreach (var v in xs) { mt += v[CTick]; mv += v[c]; size += Math.Abs(v[c]); }
            mt /= xs.Count; mv /= xs.Count; size /= xs.Count;
            double sxy = 0, sxx = 0;
            foreach (var v in xs) { sxy += (v[CTick] - mt) * (v[c] - mv); sxx += (v[CTick] - mt) * (v[CTick] - mt); }
            double slope = sxx > 0 ? sxy / sxx : 0, span = xs[^1][CTick] - xs[0][CTick];
            trends[k] = Math.Clamp(slope * span / (size + 1), -1, 1);
        }
        var sorted = (double[])trends.Clone();
        Array.Sort(sorted);
        return ((sorted[1] + sorted[2]) / 2, trends);
    }

    public static string Verdict(double index) => index >= 0.05 ? "идёт" : index <= -0.05 ? "откат" : "стагнирует";
}
