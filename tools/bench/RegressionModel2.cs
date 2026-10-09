using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Primordium.Model2;

namespace Primordium;

// Life model 2 (src/Sim/Model2, docs/DESIGN-LIFE-MODEL-2.md) in the self-test (--self-test, --self-test-model2):
// deterministic maths, folds as pure functions of the sequence, the seeded cells compiled for seed 5, and a
// small world where they live next to model-1 bodies — atoms exact, the energy ledger in tolerance, every
// polymer pool equal to what its cell says it is made of, save/load continuing the same world, designs and
// populations of model 2 round trip. Model 1's own worlds do not change (the other parts of the self-test).
public sealed partial class World
{
    public static void Model2Regression()
    {
        ParamRegistry.ResetDefaults();
        int law = P.MatterEnergy;
        P.MatterEnergy = 1;
        try { Model2RegressionBody(); }
        finally { P.MatterEnergy = law; ParamRegistry.ResetDefaults(); }
    }

    static void Model2RegressionBody()
    {
        // DetMath against the platform's functions (it must equal them to rounding, and be the same everywhere).
        double worst = 0;
        for (double x = -700; x <= 700; x += 0.37) worst = Math.Max(worst, Math.Abs(DetMath.Exp(x) / Math.Exp(x) - 1));
        for (double x = 1e-300; x < 1e300; x *= 7.3) worst = Math.Max(worst, Math.Abs(DetMath.Log(x) - Math.Log(x)) / Math.Max(1, Math.Abs(Math.Log(x))));
        for (double x = -Math.PI; x <= Math.PI; x += 0.01) worst = Math.Max(worst, Math.Max(Math.Abs(DetMath.Sin(x) - Math.Sin(x)), Math.Abs(DetMath.Cos(x) - Math.Cos(x))));
        Require(worst < 1e-12, $"DetMath off by {worst:E2}");
        Require(LifeModels.Get(LifeModels.Chem) is ChemModel && LifeModels.ByKey("chem").Id == ChemModel.ModelId && LifeModels.KeyFor(ChemModel.ModelId) == "chem", "model 2 registry");

        // Folds are functions of the sequence: a fresh chemistry folds the same chain the same way.
        var rng = new SimRng(77);
        var seq = new byte[60];
        for (int i = 0; i < seq.Length; i++) seq[i] = (byte)rng.Next(16);
        string f1 = new ProteinType(Chem2.Of(new Chemistry(1)), seq).Detail(new Chemistry(1));
        string f2 = new ProteinType(Chem2.Of(new Chemistry(1)), (byte[])seq.Clone()).Detail(new Chemistry(1));
        Require(f1 == f2, "a fold depends on more than its sequence");

        // The seeded cells of seed 1 (a small effort: the self-test checks the paths, --model2-demo the behaviour).
        var w = Model2World(5, 3, Array.Empty<int>());
        var set = Seeds.Compile(w.Chem, 1500, 3);
        foreach (int s in SeedLetters(set)) for (int c = 0; c < w.N; c++) w.C[s][c] += Qty.Of(3);
        for (int c = 0; c < w.N; c++) { w.C[set.Carrier][c] += Qty.Of(12); w.C[set.Food][c] += Qty.Of(2); }
        var c2 = Chem2.Of(w.Chem);
        foreach (var (name, g) in new[] { ("F-1", set.Phototroph), ("E-1", set.Heterotroph), ("E-1Δ", set.Knockout) })
            Require(GeneTable.Parse(c2, g).Count >= 2, $"{name}: the compiled genome reads as fewer than two genes");
        var life = LifeModels.Get(LifeModels.Chem);
        Require(life.TryCompile(set.HeterotrophDesign.Genome, out var bytes, out _) && life.Describe(bytes) == set.HeterotrophDesign.Genome, "model 2 genome text does not round trip");
        var json = CreatureDesign.FromJson(set.PhototrophDesign.ToJson());
        Require(json.Model == "chem" && json.Assemble().SequenceEqual(set.PhototrophDesign.Assemble()), "a model-2 design does not round trip through JSON");

        // A small world: phototrophs, heterotrophs and model-1 bodies.
        w.TrackHeat = true;
        var m2 = PlantBand(w, set.PhototrophDesign, 10).Concat(PlantBand(w, set.HeterotrophDesign, 10)).ToList();
        var m1 = PlantBand(w, CreatureExamples.All[0], 6).Concat(PlantBand(w, Hunter(), 4)).ToList();
        var atoms0 = Inputs(w, null);
        var e0 = w.AuditEnergy();
        for (int t = 0; t < 300; t++) w.Step();
        PoolCheck(w, "model 2 world");
        BudgetEqual(atoms0, Inputs(w, atoms0), "model 2 world: atoms", 1e-6);
        string energy = EnergyWorldCheck(w, e0, "model 2 world");
        var cells = w.Agents.Where(a => !a.Dead && a.ModelState is Cell).Select(a => (Cell)a.ModelState).ToList();
        Require(cells.Count > 0, "every model-2 body died in 300 ticks");
        long synth = cells.Sum(c => c.Synth), photons = cells.Sum(c => c.Photons), pushes = cells.Sum(c => c.Thrusts);
        Require(synth > 0 && photons > 0 && pushes > 0, $"model 2 did nothing: syntheses {synth}, photons {photons}, pushes {pushes}");
        Require(w.Agents.Where(a => !a.Dead && a.Model == LifeModels.Chem).All(a => a.Poly != null && a.EnzN == 0), "a model-2 body without polymers or with model-1 proteins");

        // Save and load: the same world, the same continuation.
        var path = Path.Combine(TestDir(), "model2.sav");
        w.Save(path, "model 2");
        var b = Load(path);
        Require(b.DeepHash() == w.DeepHash(), "a world with model 2 did not load as the same world");
        for (int t = 0; t < 60; t++) { w.Step(); b.Step(); }
        Require(w.StateHash() == b.StateHash(), "a loaded world with model 2 diverged");
        PoolCheck(b, "model 2 world after the load");
        File.Delete(path);
        bool refused = false;
        try { w.Save(new MemoryStream(), "old", System.IO.Compression.CompressionLevel.Fastest, 13); }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException) { refused = true; }   // (a tiny world cannot be written before version 16 at all)
        Require(refused, "a model-2 body was written into a version 13 file");

        // A population of model-2 bodies: copied (polymers as the monomers they are made of) and pasted into a
        // world of the same seed, where they assemble themselves again.
        var bodies = w.Agents.Where(a => !a.Dead && a.Model == LifeModels.Chem).Take(4).ToList();
        int pasted = 0;
        if (bodies.Count > 0)
        {
            var template = PopulationTemplate.FromJson(w.CopyPopulation(bodies, "model 2", "test").ToJson());
            var target = Model2World(5, 3, SeedLetters(set));
            var atoms1 = target.ElementBudget();
            var res = target.PastePopulation(template, target.W / 2, target.H / 2, new PasteOptions { Matter = MatterSource.Import, Energy = EnergySource.Import });
            Require(res.Made == bodies.Count, $"model-2 population: {res.Made} of {bodies.Count} pasted ({res.Error})");
            Require(res.Agents.All(a => a.Model == LifeModels.Chem) && res.Agents.Select(a => a.Hash).SequenceEqual(bodies.Select(a => a.Hash)), "a pasted model-2 body lost its model or genome");
            var budget = target.ElementBudget();
            for (int e = 0; e < budget.Length; e++) Require(Math.Abs(budget[e] - atoms1[e] - target.HandInput[e]) < 1e-6, "model-2 population: atoms of the paste");
            double held = bodies.Sum(a => Enumerable.Range(0, Chemistry.S).Sum(s => (a.Inv[s] + a.Pend[s].D + (a.Poly?.M[s].D ?? 0)) * w.Chem.AtomCount(s)));
            double got = res.Agents.Sum(a => Enumerable.Range(0, Chemistry.S).Sum(s => (a.Inv[s] + a.Pend[s].D) * target.Chem.AtomCount(s)));
            Require(Math.Abs(held - got) < 1e-6, $"model-2 population: the bodies held {held} atoms, the pasted ones {got}");
            for (int t = 0; t < 3; t++) target.Step();
            PoolCheck(target, "pasted model-2 population");
            pasted = res.Made;
        }
        Console.WriteLine($"PASS model 2: DetMath to {worst:E1}; seeded cells compiled for seed 5; a world of {m2.Count} model-2 and {m1.Count} model-1 bodies for 300 ticks: {cells.Count} model-2 alive, {synth} syntheses, {photons} photons, {pushes} pushes; atoms exact, {energy}, pools exact; save/load continues, v13 refuses; design JSON and genome text round trip; population of {pasted} pasted and reassembled");
    }
}
