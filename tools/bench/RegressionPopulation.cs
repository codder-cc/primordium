using System;
using System.IO;
using System.Linq;

namespace Primordium;

public sealed partial class World
{
    // Population templates (Population.cs, World.Population.cs):
    // - copy a lineage of a living world → JSON → file → load: the same JSON;
    // - paste it elsewhere with matter and energy from outside: every body's genome, protein-use bytes,
    //   molecules (whole, partial, folded in proteins), proteins, energy and looks as in the source;
    //   the atoms are booked in HandInput, the energy in HandEnergy and the ledger's design input;
    // - paste one body from local matter and local reactions: no atom or energy from outside, the
    //   ledger closes; on a bare spot nothing is made and nothing changes;
    // - paste into another seed: species mapped (same formula kept where it exists), atoms of this
    //   world booked, the ledger closes; save/load keeps the pasted bodies and their lineages.
    public static void PopulationRegression()
    {
        var w = new World(new WorldSettings { Seed = 1, InitialPop = 300, Abiogenesis = false, Strikes = false });
        {
            // Leaves (protein genes, proteins once they express them) among the random first bodies.
            int c = w.Agents.First(a => !a.Dead).Y * w.W + w.Agents.First(a => !a.Dead).X;
            w.SpawnDesign(CreatureExamples.Leaf, c % w.W, c / w.W, new SpawnOptions { Matter = MatterSource.Import, Energy = EnergySource.Import, Count = 6, Radius = 3 });
        }
        for (int t = 0; t < 300; t++) w.Step();
        var lineage = w.Agents.Where(a => !a.Dead).GroupBy(a => a.Lineage)
            .OrderByDescending(g => g.Count(a => a.EnzN > 0) > 0 ? 1 : 0).ThenByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        var bodies = w.LineageBodies(lineage);
        Require(bodies.Count > 0, "population: no living lineage to copy");
        var tpl = w.CopyPopulation(bodies, "test", "lineage");
        Require(tpl.Bodies.Count == bodies.Count && tpl.Check().Count == 0, "population: copy incomplete: " + string.Join("; ", tpl.Check()));
        string json = tpl.ToJson();
        string dir = Path.Combine(Path.GetTempPath(), "primordium-populations-" + Environment.ProcessId);
        string path = PopulationLibrary.Save(tpl, dir);
        var loaded = PopulationLibrary.Load(path);
        Directory.Delete(dir, true);
        Require(loaded.ToJson() == json, "population: save → load changed the template");
        bool proteins = tpl.Bodies.Any(b => b.Proteins.Count > 0), pend = tpl.Bodies.Any(b => b.Pend.Count > 0);

        // 1. From outside, half a planet away.
        var hand0 = (double[])w.HandInput.Clone();
        double handE0 = w.HandEnergy;
        var budget0 = w.ElementBudget();
        var start = w.EnergyStart();
        int ax = (bodies[0].X + w.W / 2) % w.W, ay = bodies[0].Y;
        var r = w.PastePopulation(loaded, ax, ay, new PasteOptions { Matter = MatterSource.Import, Energy = EnergySource.Import });
        Require(r.Map.Same && r.GenesRemapped == 0, "population: the same world's chemistry was not recognised");
        Require(r.Made >= Math.Max(1, r.Requested * 8 / 10), $"population pasted from outside: {r} ({string.Join(", ", r.Failures.Select(kv => $"{kv.Key} ×{kv.Value}"))})");
        var expectAtoms = new double[Chemistry.ElementCount];
        double expectEnergy = 0;
        for (int k = 0; k < r.Made; k++)
        {
            var a = r.Agents[k];
            var src = bodies.First(b => b.Id == r.SourceIds[k]);
            Require(a.G.SequenceEqual(src.G) && a.Prot.SequenceEqual(src.Prot), $"population: body {k}: genome differs");
            Require(a.Inv.SequenceEqual(src.Inv) && a.Pend.SequenceEqual(src.Pend), $"population: body {k}: molecules differ");
            Require(a.EnzN == src.EnzN, $"population: body {k}: proteins differ");
            for (int j = 0; j < a.EnzN; j++)
            {
                Enzyme x = a.Enz[j], y = src.Enz[j];
                Require(x.Kind == y.Kind && x.A == y.A && x.B == y.B && x.Material == y.Material && x.Matter == y.Matter && x.Amount == y.Amount && x.Eff == y.Eff && x.Topt == y.Topt,
                    $"population: body {k}: protein {j} differs");
            }
            Require(a.Energy == Math.Max(0, src.Energy) && a.Hue == src.Hue && a.Shape == src.Shape && a.Designed, $"population: body {k}: energy or looks differ");
            Require(Math.Abs(a.Mass - src.Mass) < 1e-3 * Math.Max(1, src.Mass) && Math.Abs(a.Volume - src.Volume) < 1e-3 * Math.Max(1, src.Volume), $"population: body {k}: mass {a.Mass} vs {src.Mass}, volume {a.Volume} vs {src.Volume}");
            Require(a.Lineage == r.Lineages[0] && w.DesignOf(a.Lineage) == "test", $"population: body {k}: lineage not kept as one designed lineage");
            for (int s = 0; s < Chemistry.S; s++)
                for (int e = 0; e < Chemistry.ElementCount; e++) expectAtoms[e] += ((double)src.Inv[s] + src.Pend[s].D) * w.Chem.Atoms[s, e];
            for (int j = 0; j < src.EnzN; j++)
                for (int e = 0; e < Chemistry.ElementCount; e++) expectAtoms[e] += src.Enz[j].Matter.D * w.Chem.Atoms[src.Enz[j].Material, e];
            expectEnergy += a.Energy;
        }
        for (int e = 0; e < Chemistry.ElementCount; e++)
        {
            Require(Math.Abs(w.HandInput[e] - hand0[e] - expectAtoms[e]) < 1e-6, $"population: element {e}: booked {w.HandInput[e] - hand0[e]:R}, the bodies hold {expectAtoms[e]:R}");
            Require(Math.Abs(r.AtomsImported[e] - expectAtoms[e]) < 1e-6, "population: result's atoms differ from the booking");
        }
        Require(Math.Abs(w.HandEnergy - handE0 - expectEnergy) < 1e-6 && Math.Abs(r.EnergyImported - expectEnergy) < 1e-6, "population: imported energy not booked");
        var budget1 = w.ElementBudget();
        for (int e = 0; e < Chemistry.ElementCount; e++) budget1[e] -= w.HandInput[e] - hand0[e];
        BudgetEqual(budget0, budget1, "population pasted from outside", 1e-6);
        w.EnergyBalanced(start, "population pasted from outside", FDesign);
        w.CheckCellLists();
        // Relations: a pasted child points at its pasted parent.
        int kids = r.Agents.Count(a => a.ParentId != 0 && r.Agents.Any(p => p.Id == a.ParentId));

        // 2. One body from the place: the spot gets the matter it needs (loose) and something to burn.
        var one = loaded.Clone();
        one.Bodies = one.Bodies.OrderByDescending(b => b.Proteins.Count + b.Pend.Count).Take(1).ToList();
        one.Bodies[0].Dx = one.Bodies[0].Dy = 0;
        var b0 = one.Bodies[0];
        b0.Energy = Math.Min(b0.Energy, 20);
        int spot = -1;
        for (int k = 0; k < w.N && spot < 0; k++)
        {
            int c = (int)((k * 2654435761L + 777) % w.N);
            if (c / w.W > 20 && c / w.W < w.H - 20 && !w.Submerged(c) && w.Count[c] == 0 && w.Big[c] == null && w.Height[c] < w.Z - 4) spot = c;
        }
        Require(spot >= 0, "population: no spot for the local paste");
        foreach (var (k, n) in b0.Body) w.C[int.Parse(k)][spot] += n;
        foreach (var (k, raw) in b0.Pend) w.C[int.Parse(k)][spot] += Qty.FromRaw(raw);
        foreach (var p in b0.Proteins) w.C[p.Material][spot] += Qty.FromRaw(p.Matter);
        foreach (int s in w.Chem.Unstable.Take(2)) w.C[s][spot] += 10;
        var hand1 = (double[])w.HandInput.Clone();
        double handE1 = w.HandEnergy;
        var budget2 = w.ElementBudget();
        var start2 = w.AuditEnergy();
        var rl = w.PastePopulation(one, spot % w.W, spot / w.W, new PasteOptions());
        Require(rl.Made == 1, $"population from local matter: {rl}");
        Require(w.HandInput.SequenceEqual(hand1) && w.HandEnergy == handE1, "population from local matter booked an import");
        Require(Math.Abs(rl.EnergyLocal - b0.Energy) < 1e-3 * Math.Max(1, b0.Energy), $"population: local energy {rl.EnergyLocal} of {b0.Energy}");
        BudgetEqual(budget2, w.ElementBudget(), "population from local matter", 1e-6);
        w.EnergyBalanced(start2, "population from local matter");
        Require(w.AuditEnergy().Flows[FDesign] == start2.Flows[FDesign], "population from local matter booked an outside input");
        var src0 = bodies.First(b => b.Id == b0.Id);
        Require(rl.Agents[0].G.SequenceEqual(src0.G) && rl.Agents[0].Inv.SequenceEqual(src0.Inv) && rl.Agents[0].Pend.SequenceEqual(src0.Pend), "population: the local body differs from its source");
        // A bare spot: nothing made, nothing changed.
        int bare = -1;
        for (int k = 0; k < w.N && bare < 0; k++)
        {
            int c = (int)((k * 2654435761L + 4242) % w.N);
            if (!w.Submerged(c) && w.Count[c] == 0 && w.Height[c] > 2 && w.VoxelBarrier(c * w.Z + w.Height[c] - 1) >= 2) bare = c;
        }
        foreach (int c in new[] { bare, w.Nb(bare, 0), w.Nb(bare, 1), w.Nb(bare, 2), w.Nb(bare, 3) })
            for (int s = 0; s < Chemistry.S; s++) w.C[s][c] = 0;
        ulong hash = w.StateHash();
        var budget3 = w.ElementBudget();
        var rf = w.PastePopulation(one, bare % w.W, bare / w.W, new PasteOptions());
        Require(rf.Made == 0 && rf.Error != null && w.StateHash() == hash, $"population: a bare spot made a body or changed: {rf}");
        BudgetEqual(budget3, w.ElementBudget(), "population: failed paste", 0);

        // Save/load keeps the pasted bodies and their lineages.
        var ms = new MemoryStream();
        w.Save(ms); ms.Position = 0;
        var copy = Load(ms);
        Require(copy.StateHash() == w.StateHash() && copy.DesignOf(r.Lineages[0]) == "test" && copy.Agents.Count(a => a.Designed) == w.Agents.Count(a => a.Designed), "population: lost in save/load");

        // 3. Another chemistry.
        var other = new World(new WorldSettings { Seed = 2, InitialPop = 0, Abiogenesis = false, Strikes = false });
        var oHand = (double[])other.HandInput.Clone();
        var oBudget = other.ElementBudget();
        var oStart = other.EnergyStart();
        var rx = other.PastePopulation(loaded, w.W / 2, w.H / 2, new PasteOptions { Matter = MatterSource.Import, Energy = EnergySource.Import });
        Require(!rx.Map.Same && rx.Map.Changes.Count == Chemistry.S && rx.Made > 0, $"population in another chemistry: {rx}");
        for (int s = 0; s < Chemistry.S; s++)
        {
            int to = rx.Map.To[s];
            bool formulaHere = Enumerable.Range(0, Chemistry.S).Any(t => (t & 1) == (s & 1) && Enumerable.Range(0, Chemistry.ElementCount).All(e => other.Chem.Atoms[t, e] == w.Chem.Atoms[s, e]));
            if (formulaHere) Require((to & 1) == (s & 1) && Enumerable.Range(0, Chemistry.ElementCount).All(e => other.Chem.Atoms[to, e] == w.Chem.Atoms[s, e]), $"population: species {s} has its formula in seed 2 but went to {to}");
        }
        var oBudget1 = other.ElementBudget();
        for (int e = 0; e < Chemistry.ElementCount; e++) oBudget1[e] -= other.HandInput[e] - oHand[e];
        BudgetEqual(oBudget, oBudget1, "population in another chemistry", 1e-6);
        other.EnergyBalanced(oStart, "population in another chemistry", FDesign);
        Console.WriteLine($"population ok: lineage of {bodies.Count} copied (proteins {proteins}, partial matter {pend}), {r.Made}/{r.Requested} pasted from outside with {kids} parent links, " +
                          $"1 from local matter ({rl.EnergyLocal:F1} energy), bare spot refused; seed 2: {rx.Made} pasted, {rx.Map.Changed} species changed, {rx.GenesRemapped} protein genes remapped");
    }
}
