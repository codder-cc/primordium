using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Primordium;

// What the game watches over time and the bench does not need: the evolution metrics (EvoMetrics, the
// `evo:` columns of the bench log), the size of every lineage, and the tree of living clades (from
// World.Phylo). Observation only: it runs on the simulation thread between ticks, reads the world and
// writes nothing in it (no random numbers, no Compact of the tree), so the trajectory and the state hash
// are the same with it as without it. The views it publishes are replaced, never changed.
//
// The history lives in this session only (it is not in the save): it starts when the world is created
// or loaded. A sample is taken every Every ticks (500 at first); past Cap samples every other one is
// dropped and the period doubles, so the memory stays bounded however long the world runs.
public sealed class SimObserver
{
    public const int FirstEvery = 500, Cap = 2048, MinPeak = 3, MaxLineageRows = 60;

    World world;
    EvoMetrics evo = new();
    int every = FirstEvery;
    readonly List<double[]> rows = new();
    readonly List<long> linTicks = new();
    readonly List<Dictionary<long, int>> linCounts = new();
    Dictionary<long, long> linRep = new();
    long version;

    public volatile MetricsView Metrics = MetricsView.Empty;
    public volatile LineageView Lineages = LineageView.Empty;
    public volatile TreeView Tree = TreeView.Empty;

    // The tree is built only while someone looks at it (the window or the range overlay), at most once a
    // second and only after the world moved on (or the threshold changed).
    public volatile bool WantTree;
    public volatile float TreeShare = 0.01f;
    long treeTick = -1;
    float treeShare = -1;
    double treeAt = -10;
    World treeWorld;
    public double TreeMs, SampleMs;   // diagnostics: the last build and the last sample, ms

    // After every tick (SimRunner.TickOnce).
    public void AfterTick(World w)
    {
        if (w != world) Reset(w);
        if (w.Tick % every == 0) Sample(w);
    }

    // Between ticks in the simulation loop (also while paused).
    public void Between(World w, double now)
    {
        if (w != world) Reset(w);
        if (!WantTree) return;
        float share = TreeShare;
        bool due = treeWorld != w || treeShare != share || (w.Tick != treeTick && now - treeAt >= 1.0);
        if (!due) return;
        treeAt = now; treeTick = w.Tick; treeShare = share; treeWorld = w;
        BuildTree(w, share);
    }

    void Reset(World w)
    {
        world = w;
        evo = new EvoMetrics();
        every = FirstEvery;
        rows.Clear(); linTicks.Clear(); linCounts.Clear(); linRep = new Dictionary<long, long>();
        version++;
        Metrics = new MetricsView { Version = version, Every = every, Seed = w.Seed, Since = w.Tick };
        Lineages = new LineageView { Version = version, Every = every, Since = w.Tick, Tick = w.Tick };
        Tree = TreeView.Empty;
        treeWorld = null;
    }

    void Sample(World w)
    {
        long t0 = Stopwatch.GetTimestamp();
        var v = evo.Sample(w);
        var row = new double[v.Length + 1];
        row[0] = w.Tick;
        Array.Copy(v, 0, row, 1, v.Length);
        rows.Add(row);

        var counts = new Dictionary<long, int>();
        var oldest = new Dictionary<long, Agent>();
        foreach (var a in w.Agents)
        {
            if (a.Dead) continue;
            counts[a.Lineage] = counts.GetValueOrDefault(a.Lineage) + 1;
            if (!oldest.TryGetValue(a.Lineage, out var o) || a.Age > o.Age || (a.Age == o.Age && a.Id < o.Id)) oldest[a.Lineage] = a;
        }
        var kept = new Dictionary<long, int>();
        var reps = new Dictionary<long, long>();
        foreach (var (l, n) in counts)
            if (n >= 2) { kept[l] = n; reps[l] = oldest[l].Id; }
        linTicks.Add(w.Tick);
        linCounts.Add(kept);
        linRep = reps;

        if (rows.Count > Cap)
        {
            Thin(rows); Thin(linTicks); Thin(linCounts);
            every *= 2;
        }
        version++;
        Metrics = new MetricsView { Rows = rows.ToArray(), Version = version, Every = every, Seed = w.Seed, Since = Metrics.Since };
        PublishLineages(w);
        SampleMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    // Keeps every other sample (the newest always stays).
    static void Thin<T>(List<T> list)
    {
        var keep = new List<T>(list.Count / 2 + 1);
        for (int i = list.Count - 1; i >= 0; i -= 2) keep.Add(list[i]);
        keep.Reverse();
        list.Clear();
        list.AddRange(keep);
    }

    void PublishLineages(World w)
    {
        int m = linTicks.Count;
        var peak = new Dictionary<long, int>();
        foreach (var d in linCounts)
            foreach (var (l, n) in d)
                if (n > peak.GetValueOrDefault(l)) peak[l] = n;
        var ids = peak.Where(p => p.Value >= MinPeak).OrderByDescending(p => p.Value).ThenBy(p => p.Key).Take(MaxLineageRows).Select(p => p.Key).ToList();
        var last = linCounts[^1];
        var list = new List<LineageRow>();
        foreach (long id in ids)
        {
            var r = new LineageRow { Id = id, N = new int[m], First = -1, Peak = peak[id] };
            for (int i = 0; i < m; i++)
            {
                int n = linCounts[i].GetValueOrDefault(id);
                r.N[i] = n;
                if (n > 0) { if (r.First < 0) r.First = linTicks[i]; r.Last = linTicks[i]; }
            }
            r.Now = last.GetValueOrDefault(id);
            r.Alive = r.Now > 0;
            r.RepId = linRep.GetValueOrDefault(id);
            r.Design = w.DesignOf(id);
            list.Add(r);
        }
        list.Sort((a, b) => a.First != b.First ? a.First.CompareTo(b.First) : a.Id.CompareTo(b.Id));
        Lineages = new LineageView
        {
            Ticks = linTicks.ToArray(), Rows = list.ToArray(), Version = version, Every = every, Since = Lineages.Since, Tick = w.Tick,
            Tracked = peak.Count,
        };
    }

    // ---- the tree of living clades ----

    public const int MaxNodes = 90;

    // A cladogram of the clades that hold at least `share` of the population (at least 3 bodies): the
    // genotype tree of World.Phylo with every chain of single significant children folded into one
    // branch. A branch starts at the genotype where it split off (its origin tick) and lasts until now
    // (every clade of the tree has living members); its children are the significant clades that split
    // off at its branch point. Too many branches: the threshold is raised until at most MaxNodes remain.
    void BuildTree(World w, float share)
    {
        long t0 = Stopwatch.GetTimestamp();
        w.EvoSettle();   // the tree and every body's taxon up to date (joins the background job; changes nothing else)
        var ph = w.Phylo;
        int n = ph.High;
        var head = new int[n];
        var next = new int[n];
        Array.Fill(head, -1);
        var order = new List<int>(n);
        for (int t = 0; t < n; t++)
        {
            if (!ph.Used(t)) continue;
            order.Add(t);
            int p = ph.Parent[t];
            if (p >= 0) { next[t] = head[p]; head[p] = t; }
        }
        order.Sort((a, b) => ph.Depth[a] != ph.Depth[b] ? ph.Depth[a].CompareTo(ph.Depth[b]) : a.CompareTo(b));
        var size = new long[n];
        var taxa = new int[n];
        for (int i = order.Count - 1; i >= 0; i--)
        {
            int t = order[i];
            size[t] += ph.Alive[t];
            if (ph.Alive[t] > 0) taxa[t]++;
            int p = ph.Parent[t];
            if (p >= 0) { size[p] += size[t]; taxa[p] += taxa[t]; }
        }
        long pop = 0;
        foreach (int t in order) if (ph.Parent[t] < 0) pop += size[t];

        long threshold = Math.Max(3, (long)Math.Ceiling(pop * share));
        List<CladeNode> nodes;
        Dictionary<int, int> startIdx;
        bool raised = false;
        while (true)
        {
            nodes = new List<CladeNode>();
            startIdx = new Dictionary<int, int>();
            var roots = order.Where(t => ph.Parent[t] < 0 && size[t] >= threshold).OrderBy(t => ph.Origin[t]).ThenBy(t => t).ToList();
            bool over = false;
            var kids = new List<int>();
            // Pre-order, iteratively: (taxon where the branch starts, its parent node).
            var stack = new Stack<(int c, int parent)>();
            for (int k = roots.Count - 1; k >= 0; k--) stack.Push((roots[k], -1));
            while (stack.Count > 0)
            {
                var (c, parent) = stack.Pop();
                if (nodes.Count >= MaxNodes) { over = true; break; }
                var node = new CladeNode
                {
                    Parent = parent, Start = c, Origin = ph.Origin[c], Count = (int)size[c], Taxa = taxa[c], Depth = ph.Depth[c],
                    Lineage = ph.Lineage[c], Hash = ph.Hash[c], Muts = ph.Mut[c],
                };
                int idx = nodes.Count;
                nodes.Add(node);
                startIdx[c] = idx;
                if (parent >= 0) nodes[parent].KidList.Add(idx);
                int x = c;
                while (true)
                {
                    kids.Clear();
                    for (int k = head[x]; k >= 0; k = next[k]) if (size[k] >= threshold) kids.Add(k);
                    if (kids.Count != 1) break;
                    x = kids[0];
                }
                node.DepthEnd = ph.Depth[x];
                node.Split = ph.Origin[x];
                kids.Sort((a, b) => ph.Origin[a] != ph.Origin[b] ? ph.Origin[a].CompareTo(ph.Origin[b]) : a.CompareTo(b));
                for (int k = kids.Count - 1; k >= 0; k--) stack.Push((kids[k], idx));
            }
            if (!over) break;
            threshold = threshold * 3 / 2 + 1;
            raised = true;
        }

        // Which branch every genotype belongs to (parents come before children in `order`).
        var disp = new int[n];
        foreach (int t in order)
            disp[t] = startIdx.TryGetValue(t, out int d) ? d : ph.Parent[t] >= 0 ? disp[ph.Parent[t]] : -1;

        // The bodies: own members, the oldest as the representative, and where they live.
        var cellClade = new short[w.N];
        var cellN = new byte[w.N];
        Array.Fill(cellClade, (short)-1);
        var rep = new Agent[nodes.Count];
        foreach (var a in w.Agents)
        {
            if (a.Dead || a.Taxon < 0 || a.Taxon >= n) continue;
            int d = disp[a.Taxon];
            if (d < 0) continue;
            nodes[d].Own++;
            if (rep[d] == null || a.Age > rep[d].Age || (a.Age == rep[d].Age && a.Id < rep[d].Id)) rep[d] = a;
            int cell = a.Y * w.W + a.X;
            cellClade[cell] = (short)d;
            if (cellN[cell] < 255) cellN[cell]++;
        }
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            var nd = nodes[i];
            nd.Kids = nd.KidList.ToArray();
            var best = rep[i];
            foreach (int k in nd.Kids)
                if (rep[k] != null && (best == null || rep[k].Age > best.Age)) best = rep[k];
            rep[i] = best;
            nd.Rep = best;
            nd.RepId = best?.Id ?? 0;
            nd.Row = i;
            nd.Hue = nodes.Count <= 1 ? 0.3f : 0.82f * i / (nodes.Count - 1);
            nd.Design = w.DesignOf(nd.Lineage);
        }
        long minOrigin = nodes.Count > 0 ? nodes.Min(x => x.Origin) : w.Tick;
        version++;
        Tree = new TreeView
        {
            Nodes = nodes.ToArray(), Tick = w.Tick, Pop = (int)pop, Threshold = (int)threshold, Raised = raised, Share = share,
            GenotypesAlive = order.Count(t => ph.Alive[t] > 0), TreeNodes = order.Count, MinOrigin = minOrigin,
            CellClade = cellClade, CellN = cellN, Version = version, Seed = w.Seed,
        };
        TreeMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }
}

// The evolution metrics over time: Rows[i] = { tick, EvoMetrics.Names… }, oldest first.
public sealed class MetricsView
{
    public static readonly MetricsView Empty = new();
    public double[][] Rows = Array.Empty<double[]>();
    public long Version = -1, Since;
    public int Every = SimObserver.FirstEvery, Seed;
    public static string[] Columns => columns ??= new[] { "tick" }.Concat(EvoMetrics.Names).ToArray();
    static string[] columns;
    public static int Col(string name) => Array.IndexOf(Columns, name);
}

// A lineage (the founder every body descends from, Agent.Lineage) over the samples: N[i] bodies at Ticks[i].
public sealed class LineageRow
{
    public long Id, First = -1, Last, RepId;
    public int[] N = Array.Empty<int>();
    public int Peak, Now;
    public bool Alive;
    public string Design;   // the player's design it comes from, if any
}

public sealed class LineageView
{
    public static readonly LineageView Empty = new();
    public long[] Ticks = Array.Empty<long>();
    public LineageRow[] Rows = Array.Empty<LineageRow>();   // by first appearance
    public long Version = -1, Since, Tick;
    public int Every = SimObserver.FirstEvery, Tracked;
}

// A branch of the cladogram (SimObserver.BuildTree).
public sealed class CladeNode
{
    public int Parent = -1, Start, Row;
    public int[] Kids = Array.Empty<int>();
    internal readonly List<int> KidList = new();
    public long Origin, Split, Lineage, RepId;   // Origin: the tick its first genotype appeared; Split: where its children branch off
    public int Count, Own, Taxa, Depth, DepthEnd, Muts;
    public ulong Hash;   // the genome where it starts (with Origin: its identity across rebuilds)
    public Agent Rep;    // its oldest living member (or a sub-branch's)
    public float Hue;
    public string Design;
}

public sealed class TreeView
{
    public static readonly TreeView Empty = new();
    public CladeNode[] Nodes = Array.Empty<CladeNode>();   // pre-order: a parent before its children
    public long Tick, MinOrigin, Version = -1;
    public int Pop, Threshold, GenotypesAlive, TreeNodes, Seed;
    public float Share;
    public bool Raised;
    public short[] CellClade;   // per map cell: the branch of (one of) the bodies there, −1 none
    public byte[] CellN;        // per map cell: bodies of displayed branches

    public int Find(ulong hash, long origin)
    {
        for (int i = 0; i < Nodes.Length; i++) if (Nodes[i].Hash == hash && Nodes[i].Origin == origin) return i;
        return -1;
    }

    // Is node `i` the node `root` or below it?
    public bool Under(int i, int root)
    {
        while (i >= 0) { if (i == root) return true; i = Nodes[i].Parent; }
        return false;
    }
}
