using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Primordium;

// "Ход эволюции" (ROADMAP 7.1, 9.1, 9.2): the neutral shadow (Evolution.cs), the family tree of the
// living (Phylogeny.cs) and the progress tracks sampled every P.ProgressEvery ticks (EvolutionHistory).
// Observation only, like the chronicle: nothing here is read by the simulation, no random number is
// drawn from the world's streams (the shadow has its own), and the state hash is the same without it.
//
// - In the agent phase a newborn only gets its parent's components and taxon and its own components
//   (Born → EvoBirth: written into the child, which nobody else sees yet).
// - After the phase the births are taken in the world's merge order (tile by tile, then the main
//   thread's), then the deaths in the order RemoveDead finds them: O(births + deaths) per tick.
// - Founders (self-assembly, N, the first bodies, planted designs) join as roots when they are made.
public sealed partial class World
{
    public readonly NeutralShadow Shadow = new();
    public readonly Phylogeny Phylo = new();
    public readonly EvolutionHistory Progress = new();
    readonly List<Agent> evoBorn = new(), evoDied = new();
    readonly SimRng evoRng = new(0x5A3D1E, 3);   // sampled pairs (its own stream)
    long evoDomLineage, evoDomChanges, evoDomTag;
    bool evoDomTagSet;
    const int NicheMin = 5, BigLineage = 10, PairSamples = 128;

    // Agent phase (any thread): only the child is written.
    static void EvoBirth(Agent parent, Agent child)
    {
        child.EvoParentComp = parent.EvoComp;
        child.EvoParent = parent;
        child.EvoParentIds = parent.EvoIds;
        child.EvoMut = EditSize(parent.G, child.G);
        child.EvoComp = NeutralShadow.Components(child.G, child.Prot, parent.EvoComp);
    }

    // How many bytes differ: the Hamming distance for equal lengths, else the changed middle once the
    // common start and end are taken away (exact for one point change, insertion or deletion).
    public static int EditSize(byte[] a, byte[] b)
    {
        int na = a.Length, nb = b.Length;
        if (na == nb)
        {
            int d = 0;
            for (int i = 0; i < na; i++) if (a[i] != b[i]) d++;
            return d;
        }
        int min = Math.Min(na, nb), pre = 0, suf = 0;
        while (pre < min && a[pre] == b[pre]) pre++;
        while (suf < min - pre && a[na - 1 - suf] == b[nb - 1 - suf]) suf++;
        return Math.Max(na, nb) - pre - suf;
    }

    // Between ticks: a body joins the shadow and the tree (a founder if it has no registered parent).
    void EvoRegister(Agent a)
    {
        EvoJoin();
        EvoRealBorn(a);
        a.Taxon = Phylo.Born(-1, a.Hash, 0, Tick, a.Lineage);
        a.EvoParent = null;
    }

    void EvoRealBorn(Agent a)
    {
        a.EvoComp ??= NeutralShadow.Components(a.G, a.Prot, null);
        a.EvoIds = Shadow.Born(a.EvoComp, a.EvoParentComp, a.EvoParentIds, Tick);
        a.EvoParentComp = null; a.EvoParentIds = null;
    }

    // A birth for the tree, with what it needs captured at the merge (the body's genome may change later).
    struct TreeBirth { public Agent A, Parent; public ulong Hash; public int Mut; public long Lineage, Tick; }
    List<TreeBirth> treeBorn = new(), jobBorn = new();
    List<Agent> treeDied = new(), jobDied = new();
    System.Threading.Tasks.Task evoJob;

    // The world's side of the births and deaths, in order, on this thread (it tells the shadows what
    // changed); then the two shadows and the tree in one background job, in the same order, while the
    // next tick runs. Nothing the simulation reads is touched by the job: the shadows, the tree and the
    // Taxon fields (the view and the simulation never read them). Whatever reads them first joins the
    // job (EvoJoin): the next merge, a sample, a founder made between ticks, a save.
    void EvoTick0()
    {
        EvoJoin();
        foreach (var a in evoBorn)
        {
            EvoRealBorn(a);
            treeBorn.Add(new TreeBirth { A = a, Parent = a.EvoParent, Hash = a.Hash, Mut = a.EvoMut, Lineage = a.Lineage, Tick = Tick });
            a.EvoParent = null;
        }
        foreach (var a in evoDied)
        {
            if (a.EvoIds == null) continue;
            Shadow.Died(a.EvoIds, Tick);
            a.EvoComp = null; a.EvoIds = null;
            treeDied.Add(a);
        }
        evoBorn.Clear(); evoDied.Clear();
        var ops = Shadow.TakePending();
        (jobBorn, treeBorn) = (treeBorn, jobBorn);
        (jobDied, treeDied) = (treeDied, jobDied);
        treeBorn.Clear(); treeDied.Clear();
        if (ops.Count == 0 && jobBorn.Count == 0 && jobDied.Count == 0) return;
        var born = jobBorn; var died = jobDied;
        evoJob = System.Threading.Tasks.Task.Run(() =>
        {
            Shadow.ApplyBoth(ops);
            foreach (var b in born) b.A.Taxon = Phylo.Born(b.Parent?.Taxon ?? -1, b.Hash, b.Mut, b.Tick, b.Lineage);
            foreach (var a in died) { Phylo.Died(a.Taxon); a.Taxon = -1; }
        });
    }

    void EvoJoin()
    {
        var job = evoJob;
        if (job == null) return;
        job.Wait();
        evoJob = null;
    }

    // Everything applied: the shadows, the tree and every body's taxon are up to date (between ticks).
    public void EvoSettle()
    {
        EvoJoin();
        Shadow.Flush();
    }

    // After the agent phase and RemoveDead: births, then deaths, then (every P.ProgressEvery) a sample.
    // Diagnostics: ms spent on births and deaths, Compact, Measure, the tree pass, the body pass, the pairs (summed; bench clears).
    public readonly double[] EvoMs = new double[6];
    long evoAt;
    void EvoLap(int k) { long now = System.Diagnostics.Stopwatch.GetTimestamp(); EvoMs[k] += (now - evoAt) * MsPerStamp; evoAt = now; }

    void EvoTick()
    {
        evoAt = System.Diagnostics.Stopwatch.GetTimestamp();
        EvoTick0();
        EvoLap(0);
        if (P.ProgressEvery > 0 && Tick % P.ProgressEvery == 0) EvoSample();
    }

    // One sample of every track (see EvolutionHistory.Names).
    public void EvoSample()
    {
        var v = new double[EvolutionHistory.Names.Length];
        static int C(string name) => EvolutionHistory.Col(name);
        var ph = Phylo;
        evoAt = System.Diagnostics.Stopwatch.GetTimestamp();
        EvoSettle();   // the last tick's job and founders made between ticks
        ph.Compact();
        EvoLap(1);
        Shadow.Measure(Tick);
        EvoLap(2);
        v[C("tick")] = Tick;
        v[C("adaptive")] = Shadow.RealCum; v[C("adaptive_shadow")] = Shadow.Ctrl.Cum; v[C("novelty")] = Shadow.Novelty; v[C("adaptive_now")] = Shadow.RealAbove;
        v[C("comps")] = Shadow.RLiving; v[C("comps_shadow")] = Shadow.Ctrl.Living; v[C("theta")] = Shadow.Theta;
        v[C("carry")] = Shadow.RealCarry / Math.Max(1.0, Agents.Count); v[C("carry_shadow")] = Shadow.Ctrl.Carry / Math.Max(1.0, Shadow.Ctrl.Pop.Count);

        // The tree: living taxa, total branch length, branchings, roots, the most common genome.
        int taxa = 0, branches = 0, roots = 0, dom = -1;
        long pd = 0;
        var rootList = new List<int>();
        for (int x = 0; x < ph.High; x++)
        {
            if (!ph.Used(x)) continue;
            if (ph.Alive[x] > 0) taxa++;
            if (ph.Kids[x] + (ph.Alive[x] > 0 ? 1 : 0) >= 2) branches++;
            int p = ph.Parent[x];
            pd += ph.Depth[x] - (p >= 0 ? ph.Depth[p] : -1);
            if (p < 0) { roots++; rootList.Add(x); }
            if (dom < 0 || ph.Alive[x] > ph.Alive[dom]) dom = x;
        }

        EvoLap(3);
        // The bodies: complexity and ecology.
        int pop = 0, under = 0, water = 0;
        double code = 0, prots = 0, reacts = 0, mass = 0, cells = 0;
        var diet = new int[5];
        var niche = new Dictionary<int, int>();
        var lin = new Dictionary<long, int>();
        var keys = new List<int>(16);
        var sampled = new List<int>();
        long tag = 0; bool tagSet = false;
        foreach (var a in Agents)
        {
            if (a.Dead) continue;
            pop++;
            int used = 0;
            foreach (var b in a.Prot) if (b > NeutralShadow.UsedProt) used++;
            code += used;
            keys.Clear();
            int works = 0;
            for (int k = 0; k < a.EnzN; k++)
            {
                ref var e = ref a.Enz[k];
                if (e.Amount < 0.5f) continue;
                int key = e.Kind switch
                {
                    Enzyme.Motor => Enzyme.Motor << 16,
                    Enzyme.Bind => Enzyme.Bind << 16 | Math.Min(e.A, e.B) << 8 | Math.Max(e.A, e.B),
                    _ => e.Kind << 16 | e.A << 8,
                };
                if (keys.Contains(key)) continue;
                keys.Add(key);
                bool ok = e.Kind switch
                {
                    Enzyme.Bind => Chem.Combine[e.A, e.B] >= 0,
                    Enzyme.Split => Chem.SplitA[e.A] >= 0,
                    Enzyme.Photo => Chem.PhotoUp[e.A] >= 0,
                    _ => true,
                };
                if (ok) works++;
            }
            prots += keys.Count; reacts += works;
            mass += a.Mass; cells += a.Cells;
            int d = Diet(a), cell = a.Y * W + a.X, depth = Height[cell] - a.Z;
            diet[d]++;
            bool wet = InWater(a);
            if (wet) water++;
            if (depth > 0) under++;
            int band = wet ? 3 : depth >= 3 ? 2 : depth > 0 ? 1 : 0;
            int region = tileRow[a.Y] * TilesX + tileCol[a.X];
            int nk = (region * 5 + d) * 4 + band;
            niche[nk] = niche.GetValueOrDefault(nk) + 1;
            lin[a.Lineage] = lin.GetValueOrDefault(a.Lineage) + 1;
            if (a.Taxon >= 0) sampled.Add(a.Taxon);
            if (!tagSet && a.Taxon == dom && dom >= 0) { tag = (long)a.Tag; tagSet = true; }
        }
        EvoLap(4);
        double n = Math.Max(1, pop);
        v[C("code_used")] = code / n; v[C("body_proteins")] = prots / n; v[C("body_reactions")] = reacts / n; v[C("body_mass")] = mass / n; v[C("body_cells")] = cells / n;
        v[C("niches")] = niche.Values.Count(c => c >= NicheMin);
        double h = 0;
        foreach (int c in diet) if (c > 0) { double p = c / n; h -= p * Math.Log(p); }
        v[C("diet_div")] = pop > 0 ? Math.Exp(h) : 0;
        v[C("under_share")] = under / n; v[C("water_share")] = water / n;
        v[C("lineages_big")] = lin.Values.Count(c => c >= BigLineage);

        // Tempo: changes of the largest lineage, speciations and extinctions (cumulative: a rate is a
        // difference of two samples), and how far the most common genome's fingerprint moved since the last sample.
        long domLin = 0; int domN = 0;
        foreach (var (l, c) in lin) if (c > domN || (c == domN && l < domLin)) { domLin = l; domN = c; }
        if (domN > 0 && evoDomLineage != 0 && domLin != evoDomLineage) evoDomChanges++;
        if (domN > 0) evoDomLineage = domLin;
        v[C("dom_changes")] = evoDomChanges;
        v[C("speciations")] = Chronicle.Counts[(int)EvType.Speciation];
        v[C("extinctions")] = Chronicle.Counts[(int)EvType.Extinction];
        v[C("dom_drift")] = tagSet && evoDomTagSet ? BitOperations.PopCount((ulong)(tag ^ evoDomTag)) : 0;
        if (tagSet) { evoDomTag = tag; evoDomTagSet = true; }

        // Phylogeny: the MRCA of the largest lineage (its root after Compact; several roots: no common
        // ancestor since the founding, the oldest founder counts), mean pairwise distance (sampled pairs),
        // diversity (total branch length in steps), the most common genome's lineage length and mutations.
        long oldest = long.MaxValue;
        foreach (int r in rootList) if (ph.Lineage[r] == domLin && ph.Origin[r] < oldest) oldest = ph.Origin[r];
        v[C("mrca_age")] = oldest == long.MaxValue ? 0 : Tick - oldest;
        if (sampled.Count >= 2)
        {
            double sum = 0;
            for (int k = 0; k < PairSamples; k++)
            {
                int i = evoRng.Next(sampled.Count), j = evoRng.Next(sampled.Count - 1);
                if (j >= i) j++;
                sum += ph.Distance(sampled[i], sampled[j]);
            }
            v[C("pair_dist")] = sum / PairSamples;
        }
        EvoLap(5);
        v[C("phylo_div")] = pd; v[C("phylo_taxa")] = taxa; v[C("phylo_branches")] = branches; v[C("phylo_roots")] = roots;
        v[C("dom_depth")] = dom >= 0 ? ph.Depth[dom] : 0; v[C("dom_muts")] = dom >= 0 ? ph.Mut[dom] : 0; v[C("phylo_nodes")] = ph.Nodes;
        v[C("progress_index")] = EvolutionHistory.Index(Progress.Samples, P.ProgressWindow, v).index;
        Progress.Add(v);
    }

    // A save without this block (version < 4): everyone alive joins as a founder.
    void EvoRegisterAll()
    {
        foreach (var a in Agents) if (!a.Dead && a.EvoComp == null) EvoRegister(a);
        Shadow.Flush();
    }

    // Approximate memory of the shadow, the tree, the history and the bodies' component arrays (bytes).
    public long EvolutionBytes()
    {
        EvoJoin();
        var seen = new HashSet<ulong[]>(ReferenceEqualityComparer.Instance);
        long b = Shadow.Bytes + Phylo.Bytes + Progress.Samples.Count * (EvolutionHistory.Names.Length * 8L + 24);
        foreach (var a in Agents) if (a.EvoComp != null && seen.Add(a.EvoComp)) b += 24 + a.EvoComp.Length * 8L;
        var ids = new HashSet<int[]>(ReferenceEqualityComparer.Instance);
        foreach (var a in Agents) if (a.EvoIds != null && ids.Add(a.EvoIds)) b += 24 + a.EvoIds.Length * 4L;
        var shared = new HashSet<int[]>(ReferenceEqualityComparer.Instance);
        foreach (var sp in new[] { Shadow.Ref, Shadow.Ctrl })
            foreach (var p in sp.Pop) if (shared.Add(p)) b += 24 + p.Length * 4L;
        return b;
    }

    // ---- save (version 4): a block of its own after the chronicle ----

    void SyncEvolution(Sync s)
    {
        var sh = Shadow;
        if (!s.Reading) EvoSettle();   // what founders made between ticks left for the shadows (it would be applied next tick just the same)
        s.Rng(evoRng);
        int n;
        // Real components, by id.
        s.V(ref sh.RHigh); s.V(ref sh.RLiving);
        if (s.Reading)
        {
            int cap = Math.Max(1024, sh.RHigh);
            sh.R = new CompSlot[cap]; sh.RHash = new ulong[cap];
        }
        SyncSlots(s, sh.R, sh.RHigh);
        s.A<ulong>(sh.RHash.AsSpan(0, sh.RHigh));
        SyncInts(s, sh.RFree);
        if (s.Reading) sh.Reindex();
        s.V(ref sh.RealCum); s.V(ref sh.Theta); s.V(ref sh.RealAbove); s.V(ref sh.RealCarry); s.V(ref sh.Births); s.V(ref sh.LossHad); s.V(ref sh.LossLost);
        // The two shadows.
        foreach (var sp in new[] { sh.Ref, sh.Ctrl })
        {
            s.Rng(sp.Rng);
            s.V(ref sp.High); s.V(ref sp.Living); s.V(ref sp.Above); s.V(ref sp.Cum); s.V(ref sp.Carry);
            if (s.Reading)
            {
                int cap = Math.Max(1024, sp.High);
                sp.S = new CompSlot[cap];
            }
            SyncSlots(s, sp.S, sp.High);
            SyncInts(s, sp.Free);
            SyncShared(s, sp.Pop);
        }
        // The tree.
        var ph = Phylo;
        s.V(ref ph.High);
        if (s.Reading)
        {
            int cap = Math.Max(1024, ph.High);
            ph.Parent = new int[cap]; ph.Alive = new int[cap]; ph.Kids = new int[cap]; ph.Depth = new int[cap]; ph.Mut = new int[cap];
            ph.Origin = new long[cap]; ph.Lineage = new long[cap]; ph.Hash = new ulong[cap];
        }
        s.A<int>(ph.Parent.AsSpan(0, ph.High)); s.A<int>(ph.Alive.AsSpan(0, ph.High)); s.A<int>(ph.Kids.AsSpan(0, ph.High));
        s.A<int>(ph.Depth.AsSpan(0, ph.High)); s.A<int>(ph.Mut.AsSpan(0, ph.High));
        s.A<long>(ph.Origin.AsSpan(0, ph.High)); s.A<long>(ph.Lineage.AsSpan(0, ph.High)); s.A<ulong>(ph.Hash.AsSpan(0, ph.High));
        SyncInts(s, ph.Free);
        // The history.
        var hi = Progress;
        s.V(ref hi.Stride); s.V(ref hi.Skipped); s.V(ref hi.Version);
        int cols = EvolutionHistory.Names.Length;
        s.V(ref cols);
        n = hi.Samples.Count;
        s.V(ref n);
        if (s.Reading) hi.Samples.Clear();
        for (int k = 0; k < n; k++)
        {
            var row = s.Reading ? new double[EvolutionHistory.Names.Length] : hi.Samples[k];
            if (s.Reading && cols != row.Length)
            {
                var raw = new double[cols];
                s.A<double>(raw);
                Array.Copy(raw, row, Math.Min(cols, row.Length));
            }
            else s.A<double>(row);
            if (s.Reading) hi.Samples.Add(row);
        }
        bool latest = hi.Latest != null;
        s.V(ref latest);
        if (latest)
        {
            if (s.Reading) { hi.Latest = new double[cols]; s.A<double>(hi.Latest); if (cols != EvolutionHistory.Names.Length) Array.Resize(ref hi.Latest, EvolutionHistory.Names.Length); }
            else s.A<double>(hi.Latest);
        }
        s.V(ref evoDomLineage); s.V(ref evoDomChanges); s.V(ref evoDomTag); s.V(ref evoDomTagSet);
        // Every body's components (shared arrays written once) and taxon, in the order of Agents.
        var table = new List<ulong[]>();
        var index = new Dictionary<ulong[], int>(ReferenceEqualityComparer.Instance);
        if (!s.Reading)
            foreach (var a in Agents)
                if (a.EvoComp != null && !index.ContainsKey(a.EvoComp)) { index[a.EvoComp] = table.Count; table.Add(a.EvoComp); }
        n = table.Count;
        s.V(ref n);
        for (int k = 0; k < n; k++)
        {
            int len = s.Reading ? 0 : table[k].Length;
            s.V(ref len);
            var arr = s.Reading ? (len == 0 ? Array.Empty<ulong>() : new ulong[len]) : table[k];
            s.A<ulong>(arr);
            if (s.Reading) table.Add(arr);
        }
        var idTable = new List<int[]>();
        foreach (var a in Agents) if (!s.Reading) idTable.Add(a.EvoIds);
        SyncSharedNullable(s, idTable);
        int ai = 0;
        foreach (var a in Agents)
        {
            int at = a.EvoComp == null ? -1 : index[a.EvoComp];
            s.V(ref at); s.V(ref a.Taxon);
            if (s.Reading) { a.EvoComp = at < 0 ? null : table[at]; a.EvoIds = idTable[ai]; }
            ai++;
        }
    }

    // Field by field (never raw struct memory: padding bytes would make saves of one world differ).
    static void SyncSlots(Sync s, CompSlot[] slots, int n)
    {
        for (int k = 0; k < n; k++)
        {
            ref var c = ref slots[k];
            s.V(ref c.N); s.V(ref c.Crossed); s.V(ref c.Last); s.V(ref c.Act);
        }
    }

    static void SyncInts(Sync s, List<int> list)
    {
        int n = list.Count;
        s.V(ref n);
        if (s.Reading) { list.Clear(); for (int k = 0; k < n; k++) { int x = 0; s.V(ref x); list.Add(x); } }
        else for (int k = 0; k < n; k++) { int x = list[k]; s.V(ref x); }
    }

    // As SyncShared, with null entries allowed (bodies never registered).
    static void SyncSharedNullable(Sync s, List<int[]> list)
    {
        int n = list.Count;
        s.V(ref n);
        var mask = new bool[n];
        if (!s.Reading) for (int k = 0; k < n; k++) mask[k] = list[k] != null;
        s.A<bool>(mask);
        var some = new List<int[]>();
        if (!s.Reading) foreach (var x in list) if (x != null) some.Add(x);
        SyncShared(s, some);
        if (!s.Reading) return;
        list.Clear();
        int at = 0;
        for (int k = 0; k < n; k++) list.Add(mask[k] ? some[at++] : null);
    }

    // A list of arrays where many entries are the same array: each distinct array once, then indices.
    static void SyncShared(Sync s, List<int[]> list)
    {
        var table = new List<int[]>();
        var index = new Dictionary<int[], int>(ReferenceEqualityComparer.Instance);
        if (!s.Reading) foreach (var x in list) if (!index.ContainsKey(x)) { index[x] = table.Count; table.Add(x); }
        int n = table.Count;
        s.V(ref n);
        for (int k = 0; k < n; k++)
        {
            int len = s.Reading ? 0 : table[k].Length;
            s.V(ref len);
            var arr = s.Reading ? (len == 0 ? Array.Empty<int>() : new int[len]) : table[k];   // empty: the one shared empty array
            s.A<int>(arr);
            if (s.Reading) table.Add(arr);
        }
        n = list.Count;
        s.V(ref n);
        if (s.Reading) list.Clear();
        for (int k = 0; k < n; k++)
        {
            int at = s.Reading ? 0 : index[list[k]];
            s.V(ref at);
            if (s.Reading) list.Add(table[at]);
        }
    }

    // The evolution block alone (tests compare it before and after a load).
    internal byte[] EvolutionBlock()
    {
        var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) SyncEvolution(new Writer(bw));
        return ms.ToArray();
    }
}
