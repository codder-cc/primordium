using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Primordium;

// Life models (LifeModels.cs, docs/LIFE-MODELS.md): a second model lives in the same world with the same
// physics — it acts only through the body physics, the ledger closes, it divides into its own kind, does
// not mate with model 1, is saved and loaded (version 14) and cannot be written into an older format.
public sealed partial class World
{
    // A test model: its "genome" is read as nothing; every tick it pays a little for thinking, digests
    // something and, when rich, divides. Genome operations borrow model 1's byte operations.
    sealed class TestLifeModel : ILifeModel
    {
        public const byte TestId = 250;
        public byte Id => TestId;
        public string Key => "test-digest";
        public string Name => "test: digest and divide";
        public int Thinks;
        public void Think(World w, Agent a, int cell)
        {
            System.Threading.Interlocked.Increment(ref Thinks);
            w.Spend(a, 0.02);
            w.DigestAny(a, -1);
            if (a.Energy > 150 && a.InvTotal > 2 * P.DivMinBody) w.Divide(a, cell, 0, 4);
        }
        public void GenomeChanged(Agent a) { }
        public byte[] RandomGenome(SimRng rng) => Genome.Random(rng);
        public (byte[] g, byte[] p) Mutate(byte[] g, byte[] prot, SimRng rng) => ((byte[])g.Clone(), (byte[])prot.Clone());
        public (byte[] g, byte[] p) Cross(Agent a, Agent b, SimRng rng) => ((byte[])a.G.Clone(), (byte[])a.Prot.Clone());
        public void InheritState(Agent parent, Agent child) => child.ModelState = parent.ModelState is int n ? n + 1 : 1;
        public int SpliceSite(Agent a) => 0;
        public void BeforeSplice(Agent a, int at, int removed, int inserted) { }
        public void SyncState(Sync s, Agent a)
        {
            int n = a.ModelState is int k ? k : 0;
            s.V(ref n);
            if (s.Reading) a.ModelState = n;
        }
        public string Describe(byte[] g) => Convert.ToBase64String(g);
        public bool TryCompile(string text, out byte[] genome, out List<string> errors)
        {
            errors = new List<string>();
            try { genome = Convert.FromBase64String(text ?? ""); return genome.Length >= Genome.MinLen; }
            catch (FormatException) { genome = null; errors.Add("not base64"); return false; }
        }
        public IReadOnlyList<CreatureDesign> DefaultDesigns => Array.Empty<CreatureDesign>();
    }

    static TestLifeModel testLife;

    public static void LifeModelRegression()
    {
        ParamRegistry.ResetDefaults();
        if (testLife == null) LifeModels.Register(testLife = new TestLifeModel());
        Require(LifeModels.Get(LifeModels.Vm) is VmModel && LifeModels.ByKey(null).Id == LifeModels.Vm && LifeModels.ByKey("test-digest") == testLife, "life model registry");
        Require(LifeModels.KeyFor(LifeModels.Vm) == null && LifeModels.KeyFor(TestLifeModel.TestId) == "test-digest", "life model keys");

        var w = Fixture();
        w.TrackHeat = true;
        int c = 80 * W + 120;
        int food = Enumerable.Range(0, Chemistry.S).First(s => s != w.Chem.Gas && w.Chem.SplitA[s] >= 0 && w.Chem.SplitExo[s]);
        var vm = w.TestAgent(c, 2, food, 40);
        long id = w.NewId();
        var other = new Agent(id, id, 0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, null, TestLifeModel.TestId) { Energy = 200, Z = 2, Tb = 15 };
        for (int k = 0; k < 60; k++) w.AddMol(other, food);
        w.Place(other, c); w.Agents.Add(other);
        Require(other.Model == TestLifeModel.TestId && vm.Model == LifeModels.Vm, "a body keeps its life model");
        var atoms0 = w.ElementBudget();
        var e0 = w.AuditEnergy();
        for (int t = 0; t < 60; t++) w.Step();
        Require(testLife.Thinks > 0, "the test model never thought");
        BudgetEqual(atoms0, w.ElementBudget(), "two life models: atoms", 0.001);
        string energy = EnergyWorldCheck(w, e0, "two life models");
        Require(w.Agents.Where(a => !a.Dead).All(a => a.Model == LifeModels.Vm || a.Model == TestLifeModel.TestId), "a body of no model");

        // Mating and code injection across models: refused, the genomes stay.
        if (!other.Dead && !vm.Dead)
        {
            var g = other.G;
            other.Target = vm; vm.Target = other;
            vm.MateTick = w.Tick;
            int births = w.Births;
            w.Mate(other, other.Y * W + other.X);
            Require(w.Births == births, "bodies of two life models mated");
            w.Inject(vm, vm.Y * W + vm.X, 0, 8);
            Require(ReferenceEquals(other.G, g), "model 1 code was injected into another model's genome");
        }

        // Save (version 14) and load: the model and its state come back, the continuation is the same.
        var path = Path.Combine(TestDir(), "life-models.sav");
        w.Save(path, "life models");
        var b = Load(path);
        Require(b.DeepHash() == w.DeepHash(), "a world with two life models did not load as the same world");
        Require(w.Agents.Select(a => (a.Id, a.Model, a.ModelState as int? ?? 0)).SequenceEqual(b.Agents.Select(a => (a.Id, a.Model, a.ModelState as int? ?? 0))), "life models or their state lost by the load");
        for (int t = 0; t < 40; t++) { w.Step(); b.Step(); }
        Require(w.StateHash() == b.StateHash(), "a loaded world with two life models diverged");
        File.Delete(path);
        bool refused = false;
        if (w.Agents.Any(a => !a.Dead && a.Model != LifeModels.Vm))
        {
            try { w.Save(new MemoryStream(), "old", System.IO.Compression.CompressionLevel.Fastest, 13); }
            catch (InvalidOperationException) { refused = true; }
            Require(refused, "a body of another life model was written into a version 13 file");
        }
        int kinds = w.Agents.Count(a => !a.Dead && a.Model == TestLifeModel.TestId);
        Console.WriteLine($"PASS life models: model 1 and a test model in one world, {kinds} test bodies alive ({other.NSplit} digested by the first), atoms exact, {energy}; no cross-model mating or injection; save/load v14 keeps model and state{(refused ? ", v13 refuses them" : "")}");
        ParamRegistry.ResetDefaults();
    }
}
