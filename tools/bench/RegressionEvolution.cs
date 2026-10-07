using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Primordium;

// The course of evolution (World.Evolution.cs): it only watches; the neutral shadow says "nothing
// adaptive" where nothing is selected and "something" where something is; the family tree stays
// small; all of it survives save/load exactly.
public sealed partial class World
{
    // A world of sets of components without a world: births and deaths at random (neutral) or with
    // parents picked by how many "good" components they carry (selection). Returns the shadow.
    static NeutralShadow SyntheticEvolution(int seed, bool select, int steps = 4000)
    {
        var r = new SimRng(seed, 99);
        var sh = new NeutralShadow(seed * 7919L);
        var pop = new List<ulong[]>();
        var ids = new List<int[]>();
        ulong next = 1;
        for (int k = 0; k < 300; k++) { var c = new[] { next++ }; pop.Add(c); ids.Add(sh.Born(c, null, null, 0)); }
        static bool Good(ulong c) => c % 5 == 0;
        for (int t = 1; t <= steps; t++)
        {
            for (int b = 0; b < 6; b++)
            {
                int pi;
                if (!select) pi = r.Next(pop.Count);
                else
                {
                    // Tournament of three: the one with the most good components.
                    pi = -1; int best = -1;
                    for (int k = 0; k < 3; k++) { int q = r.Next(pop.Count); int g = pop[q].Count(Good); if (g > best) { best = g; pi = q; } }
                }
                var parent = pop[pi];
                var child = new SortedSet<ulong>(parent);
                if (r.NextDouble() < 0.3 && child.Count > 0) child.Remove(child.ElementAt(r.Next(child.Count)));
                if (r.NextDouble() < 0.3) child.Add(next++);
                if (r.NextDouble() < 0.1) { var d = pop[r.Next(pop.Count)]; if (d.Length > 0) child.Add(d[r.Next(d.Length)]); }
                var arr = child.ToArray();
                if (arr.AsSpan().SequenceEqual(parent)) arr = parent;
                ids.Add(sh.Born(arr, parent, ids[pi], t));
                pop.Add(arr);
            }
            for (int d = 0; d < 6; d++)
            {
                int at = r.Next(pop.Count);
                sh.Died(ids[at], t);
                pop[at] = pop[^1]; pop.RemoveAt(pop.Count - 1);
                ids[at] = ids[^1]; ids.RemoveAt(ids.Count - 1);
            }
            if (t % 50 == 0) sh.Measure(t);
        }
        return sh;
    }

    static void EvolutionRegression()
    {
        // 1. Synthetic: neutral births and deaths leave novelty near zero; selection makes it clearly positive.
        var neutral = Enumerable.Range(1, 6).Select(s => SyntheticEvolution(s, false)).ToList();
        var selected = Enumerable.Range(1, 6).Select(s => SyntheticEvolution(s, true)).ToList();
        double nMean = neutral.Average(s => (double)s.Novelty), nScale = neutral.Average(s => (double)s.RealCum);
        double sMin = selected.Min(s => (double)s.Novelty);
        Console.WriteLine($"   neutral novelty {string.Join(" ", neutral.Select(s => s.Novelty))} (adaptive ~{nScale:F0}); selected {string.Join(" ", selected.Select(s => s.Novelty))}");
        Require(Math.Abs(nMean) <= 0.15 * nScale + 5, $"neutral births and deaths gave novelty {nMean:F1} of {nScale:F0}");
        Require(sMin > Math.Max(20, 3 * neutral.Max(s => Math.Abs((double)s.Novelty))), $"selection gave novelty only {sMin}");

        // 2. Watching changes nothing: sampling every 10 ticks or never — the same world. The shadow and the
        // tree hold exactly the living bodies.
        int every = P.ProgressEvery;
        var a = new World(3, 600, true);
        P.ProgressEvery = 100000;
        var b = new World(3, 600, true);
        for (int t = 1; t <= 2000; t++)
        {
            P.ProgressEvery = 10; a.Step();
            P.ProgressEvery = 100000; b.Step();
            if (t % 500 == 0)
            {
                Require(a.StateHash() == b.StateHash(), $"sampling the course of evolution changed the world at tick {t}");
                a.EvoSettle();   // the shadows and the tree catch up (a background job; a founder made after the phase waits for the next tick)
                Require(a.Shadow.Ref.Pop.Count == a.Agents.Count && a.Shadow.Ctrl.Pop.Count == a.Agents.Count && a.Agents.All(x => x.EvoComp != null && x.Taxon >= 0), $"the shadow or the tree lost track of the living: ref {a.Shadow.Ref.Pop.Count} ctrl {a.Shadow.Ctrl.Pop.Count} world {a.Agents.Count}, unregistered {a.Agents.Count(x => x.EvoComp == null)}, no taxon {a.Agents.Count(x => x.Taxon < 0)}");
                int alive = 0;
                for (int x = 0; x < a.Phylo.High; x++) if (a.Phylo.Used(x)) alive += a.Phylo.Alive[x];
                Require(alive == a.Agents.Count, $"the tree counts {alive} living, the world {a.Agents.Count}");
                // Memory: after Compact the tree is at most about two nodes per living taxon (plus roots).
                a.Phylo.Compact();
                int taxa = 0;
                for (int x = 0; x < a.Phylo.High; x++) if (a.Phylo.Used(x) && a.Phylo.Alive[x] > 0) taxa++;
                Require(a.Phylo.Nodes <= 2 * taxa + 1, $"tree not pruned: {a.Phylo.Nodes} nodes for {taxa} living taxa");
            }
        }
        P.ProgressEvery = every;
        var v = a.Progress.Latest;
        Require(a.Progress.Samples.Count == 200 && v != null, $"samples: {a.Progress.Samples.Count}");
        double novelty = v[EvolutionHistory.CNovelty];
        Require(novelty > 0, $"a living world shows no adaptive components after 2000 ticks (novelty {novelty})");

        // 3. Save/load: the block comes back byte for byte and goes on the same way; an older file starts
        // an empty history with everyone a founder.
        var ms = new MemoryStream();
        a.Save(ms, "evolution"); ms.Position = 0;
        var c = Load(ms);
        Require(c.EvolutionBlock().AsSpan().SequenceEqual(a.EvolutionBlock()), "the course of evolution differs after load");
        for (int t = 0; t < 600; t++)
        {
            a.Step(); c.Step();
            var ba = a.EvolutionBlock(); var bc = c.EvolutionBlock();
            if (!ba.AsSpan().SequenceEqual(bc))
            {
                int at = 0; while (at < Math.Min(ba.Length, bc.Length) && ba[at] == bc[at]) at++;
                Console.WriteLine($"   diverged at tick {a.Tick}: lengths {ba.Length}/{bc.Length}, first difference at byte {at}; real comps {a.Shadow.RLiving}/{c.Shadow.RLiving}, ref high {a.Shadow.Ref.High}/{c.Shadow.Ref.High}, phylo high {a.Phylo.High}/{c.Phylo.High}, samples {a.Progress.Samples.Count}/{c.Progress.Samples.Count}");
                break;
            }
        }
        Require(c.StateHash() == a.StateHash(), "loaded world diverged");
        Require(c.EvolutionBlock().AsSpan().SequenceEqual(a.EvolutionBlock()), "the loaded course of evolution diverged");
        var old = new MemoryStream();
        a.GeoOn = false;   // a version 5 file holds no geochemistry (version 7): it loads with the profile off
        a.SkyFromOldFile();   // nor the sky (version 8): its fields are rebuilt at the load
        a.EnergyToFloat();    // nor energies as doubles (version 9)
        a.Save(old, "v5", System.IO.Compression.CompressionLevel.Fastest, 5); old.Position = 0;
        var o = Load(old);
        Require(o.Progress.Samples.Count == 0 && o.Shadow.Ref.Pop.Count == o.Agents.Count && o.Agents.All(x => x.EvoComp != null), "a version 5 save did not start an empty course of evolution");
        for (int t = 0; t < 200; t++) o.Step();
        Require(o.StateHash() == a.StateHashAfter(200), "a version 5 save diverged");

        Console.WriteLine($"PASS evolution: neutral novelty {nMean:F1} (of ~{nScale:F0} adaptive), selection ≥ {sMin}; sampling changes nothing (2000 ticks); " +
                          $"world novelty {novelty} at tick 2000 ({a.Shadow.RLiving} living components), tree {a.Phylo.Nodes} nodes for {a.Agents.Count} bodies; save/load exact, version 5 loads");
    }

    // The hash this world will have after n more ticks (stepping a copy through a save).
    ulong StateHashAfter(int n)
    {
        var ms = new MemoryStream();
        Save(ms, "copy"); ms.Position = 0;
        var w = Load(ms);
        for (int t = 0; t < n; t++) w.Step();
        return w.StateHash();
    }
}

public sealed partial class World
{
    // tools/bench --self-test-evolution: only this test.
    public static void RunEvolutionRegression()
    {
        ParamRegistry.ResetDefaults();
        EvolutionRegression();
    }
}
