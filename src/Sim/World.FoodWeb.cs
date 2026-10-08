using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Primordium;

// Observation: the food-web probe (World.FoodProbe, null = off) books where the molecules bodies gain
// come from, lineage by lineage: from the environment (intake of loose matter, mining and soaking
// rock) or from other bodies (molecules torn out by `attack`, stolen by `take`), and who killed whom.
// Only counts; the simulation never reads them, uses no randomness for them and they are not saved.
// Thread-safe: called from the agent phase (counters are Interlocked, the maps concurrent; the sums do
// not depend on the order of the squares).
//
// Trophic level (matter-based, like a tracer isotope): a lineage that takes all its molecules from the
// environment has level 1; one whose share f_ij of molecules came from bodies of lineage j has
// TL_i = 1 + Σ_j f_ij·TL_j (Levine 1980), the environment's share counting 0. Predation inside a
// lineage (cannibalism, kin eating kin) is left out of the levels — with it a lineage living off its own
// kind would have no finite level — and reported apart (OwnLineagePrey). Approximation: energy shared by
// `share` and links is not matter and is not counted; eating remains on the floor is environment.
public sealed class FoodWebProbe
{
    public sealed class Edge { public long Molecules, Energy, Kills; }
    public sealed class Node { public long Env, Prey, Lost; }

    public readonly ConcurrentDictionary<(long eater, long source), Edge> Edges = new();
    public readonly ConcurrentDictionary<long, Node> Nodes = new();
    public long Ticks;

    // Nodes of the web: lineages (founder ids), or with ByGenome exact genomes (Agent.Hash, as the
    // family tree's taxa) — then kin with another genome counts as another node.
    public bool ByGenome;
    long Key(Agent a) => ByGenome ? (long)a.Hash : a.Lineage;
    Node NodeOf(long key) => Nodes.GetOrAdd(key, _ => new Node());

    public void Env(Agent a) => Interlocked.Increment(ref NodeOf(Key(a)).Env);

    public void Prey(Agent a, Agent from, int bondEnergy)
    {
        var e = Edges.GetOrAdd((Key(a), Key(from)), _ => new Edge());
        Interlocked.Increment(ref e.Molecules);
        Interlocked.Add(ref e.Energy, bondEnergy);
        Interlocked.Increment(ref NodeOf(Key(a)).Prey);
        Interlocked.Increment(ref NodeOf(Key(from)).Lost);
    }

    public void Kill(Agent a, Agent victim) =>
        Interlocked.Increment(ref Edges.GetOrAdd((Key(a), Key(victim)), _ => new Edge()).Kills);

    public void Clear() { Edges.Clear(); Nodes.Clear(); Ticks = 0; }

    public const double LevelCap = 10;

    public sealed class Summary
    {
        public long EnvMolecules, PreyMolecules, OwnLineagePrey, Kills;
        public double PreyShare;        // of all molecules bodies gained, the share torn or stolen from bodies (own lineage too)
        public double MeanLevel;        // trophic level weighted by molecules gained (without own-lineage prey)
        public double MaxLevel;         // of lineages that gained at least MinShare of all molecules
        public int Chain;               // lineages on the longest path of significant edges (1: nobody feeds on others)
        public int Lineages, Predators; // lineages that gained molecules; of them, with ≥ EdgeShare from bodies
        public Dictionary<long, double> Level = new();
        public List<(long eater, long source, long molecules, long kills, double share)> Top = new();
    }

    // An edge is significant when it brings ≥ edgeShare of the eater's molecules; a lineage counts for the
    // maximum when it gained ≥ minShare of all molecules gained (so that one lucky bite does not decide).
    public Summary Summarize(double edgeShare = 0.05, double minShare = 0.005)
    {
        var s = new Summary();
        var nodes = Nodes.ToDictionary(kv => kv.Key, kv => kv.Value);
        var edges = Edges.Where(kv => kv.Value.Molecules > 0 || kv.Value.Kills > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        var own = new Dictionary<long, long>();
        foreach (var ((eater, source), e) in edges) { s.Kills += e.Kills; if (eater == source) { s.OwnLineagePrey += e.Molecules; own[eater] = e.Molecules; } }
        // What each lineage gained, without what it took from its own kind.
        var gained = new Dictionary<long, long>();
        foreach (var (lin, n) in nodes)
        {
            long g = n.Env + n.Prey - own.GetValueOrDefault(lin);
            if (g > 0) gained[lin] = g;
        }
        foreach (var n in nodes.Values) { s.EnvMolecules += n.Env; s.PreyMolecules += n.Prey; }
        s.PreyShare = s.EnvMolecules + s.PreyMolecules > 0 ? s.PreyMolecules / (double)(s.EnvMolecules + s.PreyMolecules) : 0;
        long total = gained.Values.Sum();
        s.Lineages = gained.Count;
        // f_ij by eater; levels by iteration (Jacobi on TL = 1 + F·TL), capped at LevelCap: nodes feeding
        // on each other in a loop that took (almost) nothing from the environment in the window have no
        // finite level — read the cap as "a loop", and the chain (simple paths) as the length.
        var diet = new Dictionary<long, List<(long source, double f)>>();
        foreach (var ((eater, source), e) in edges)
        {
            if (e.Molecules == 0 || eater == source || !gained.TryGetValue(eater, out long g)) continue;
            if (!diet.TryGetValue(eater, out var list)) diet[eater] = list = new();
            list.Add((source, e.Molecules / (double)g));
        }
        var level = gained.Keys.ToDictionary(k => k, _ => 1.0);
        for (int it = 0; it < 500; it++)
        {
            double change = 0;
            var next = new Dictionary<long, double>(level.Count);
            foreach (var k in level.Keys)
            {
                double v = 1;
                if (diet.TryGetValue(k, out var list))
                    foreach (var (src, f) in list) v += f * (level.TryGetValue(src, out double l) ? l : 1);
                v = Math.Min(v, LevelCap);
                change = Math.Max(change, Math.Abs(v - level[k]));
                next[k] = v;
            }
            level = next;
            if (change < 1e-9) break;
        }
        s.Level = level;
        double wsum = 0;
        foreach (var (k, g) in gained)
        {
            wsum += g * level[k];
            if (g >= minShare * total) s.MaxLevel = Math.Max(s.MaxLevel, level[k]);
            if (diet.TryGetValue(k, out var list) && list.Sum(x => x.f) >= edgeShare) s.Predators++;
        }
        s.MeanLevel = total > 0 ? wsum / total : 0;
        // The longest chain: a simple path along significant edges eater → source (self-edges left out),
        // counted in lineages, only through lineages that matter (minShare), depth-limited.
        var next2 = new Dictionary<long, List<long>>();
        foreach (var (eater, list) in diet)
            foreach (var (src, f) in list)
                if (src != eater && f >= edgeShare && gained.TryGetValue(src, out long gs) && gs >= minShare * total && gained[eater] >= minShare * total)
                {
                    if (!next2.TryGetValue(eater, out var l)) next2[eater] = l = new();
                    l.Add(src);
                }
        var onPath = new HashSet<long>();
        int Longest(long k, int depth)
        {
            if (depth >= 12 || !next2.TryGetValue(k, out var l)) return 1;
            onPath.Add(k);
            int best = 1;
            foreach (var src in l) if (!onPath.Contains(src)) best = Math.Max(best, 1 + Longest(src, depth + 1));
            onPath.Remove(k);
            return best;
        }
        s.Chain = gained.Count > 0 ? 1 : 0;
        foreach (var k in next2.Keys) s.Chain = Math.Max(s.Chain, Longest(k, 0));
        // Edges with the share of everything the eater gained (its own kind included).
        double All(long lin) => nodes.TryGetValue(lin, out var n) && n.Env + n.Prey > 0 ? n.Env + n.Prey : double.PositiveInfinity;
        s.Top = edges.Select(kv => (kv.Key.eater, kv.Key.source, kv.Value.Molecules, kv.Value.Kills, kv.Value.Molecules / All(kv.Key.eater)))
            .OrderByDescending(x => x.Molecules).ThenByDescending(x => x.Kills).ThenBy(x => x.eater).ThenBy(x => x.source).ToList();
        return s;
    }
}

public sealed partial class World
{
    public FoodWebProbe FoodProbe;   // observation only (see FoodWebProbe); null = off
}
