using System;
using System.IO;
using System.Linq;

namespace Primordium;

// Regressions of the infrastructure around the world: tunable laws, save/load, genome text and
// player-designed creatures. Compiled only into the headless runner.
public sealed partial class World
{
    static string TestDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "primordium-selftest-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Only these (tools/bench --self-test-infra): quicker while working on them.
    public static void RunInfraRegression()
    {
        ParamRegistry.ResetDefaults();
        LawsRegression();
        SaveLoadRegression();
        AsmRegression();
        DesignRegression();
        ParamRegistry.ResetDefaults();
    }

    // The registry covers every tunable of P with a sane range; set/get/reset/snapshot/JSON round
    // trip; a law the world derived tables from (strength, block size, gas bubbles) refreshes them.
    static void LawsRegression()
    {
        foreach (var p in ParamRegistry.All)
        {
            Require(p.Min <= p.Default && p.Default <= p.Max, $"law {p.Name}: default {p.Default} outside [{p.Min}, {p.Max}]");
            Require(ParamRegistry.Normalize(p, p.Default) == p.Default && p.Step > 0, $"law {p.Name}: default not representable or no step");
            Require(!string.IsNullOrWhiteSpace(p.Description) && !string.IsNullOrWhiteSpace(p.Group), $"law {p.Name}: no description");
        }
        int changes = 0;
        void Count(ParamInfo p, double was, double now) => changes++;
        ParamRegistry.Changed += Count;
        try
        {
            Require(!ParamRegistry.Set("NoSuchLaw", 1), "an unknown law was set");
            ParamRegistry.Set("FaceWork", 4.5);
            Require(P.FaceWork == 4.5f && ParamRegistry.Get("facework") == 4.5, "set/get by name");
            ParamRegistry.Set("EnvEvery", 0.2);
            Require(P.EnvEvery == 1, $"integer law below its range: {P.EnvEvery}");
            ParamRegistry.Set("DayLen", 777.4);
            Require(P.DayLen == 750 || P.DayLen == 777, $"integer law rounding: {P.DayLen}");
            Require(changes == 3, $"change notifications: {changes}");
            var snap = ParamRegistry.Snapshot();
            var preset = ParamRegistry.Capture("тест", "проверка");
            Require(preset.Values.Count == 3, $"preset holds only the changes: {preset.Values.Count}");
            string path = Path.Combine(TestDir(), "laws.json");
            ParamRegistry.SavePreset(path, preset);
            ParamRegistry.ResetDefaults();
            Require(ParamRegistry.All.All(p => p.IsDefault) && P.FaceWork == 9f, "reset to defaults");
            var back = ParamRegistry.LoadPreset(path);
            Require(back.Name == "тест" && ParamRegistry.Restore(back.Values).Count == 0, "preset JSON round trip");
            Require(ParamRegistry.Snapshot().All(kv => snap[kv.Key] == kv.Value), "preset restores the same laws");
            ParamRegistry.ResetDefaults();
        }
        finally { ParamRegistry.Changed -= Count; }

        // Strength: pressures follow gravity once the solver has looked at every column again.
        var w = Fixture(); int c = 60 * W + 60;
        for (int z = 2; z < 8; z++) w.TestBlock(c, z, 0);
        w.StepStructure();
        float before = w.Pressure[c * Z + 2];
        w.SetParam("Gravity", P.Gravity * 2);
        Require(w.structuralDirty.Count == N && w.ParamLog.Count == 1, "a stronger gravity did not wake the support solver everywhere");
        w.StepStructure();
        Require(MathF.Abs(w.Pressure[c * Z + 2] - 2 * before) < 1e-3f * before, $"pressure did not follow gravity: {before} -> {w.Pressure[c * Z + 2]}");
        // Block size: a full block's count follows the voxel's room.
        int cap = w.Chem.MatCap[2];
        w.SetParam("VoxelSpace", P.VoxelSpace * 2);
        Require(Math.Abs(w.Chem.MatCap[2] - 2 * cap) <= 1, $"MatCap did not follow VoxelSpace: {cap} -> {w.Chem.MatCap[2]}");
        // Gas bubbles: every body's room is recomputed.
        var a = w.TestAgent(c + 3, 2, w.Chem.Gas, 10);
        float vol = a.Volume;
        w.SetParam("GasExpand", P.GasExpand / 2);
        Require(MathF.Abs(a.Volume - vol / 2) < 1e-3f && a.Volume == 10 * w.Chem.BodyVolume[w.Chem.Gas], $"body volume did not follow GasExpand: {vol} -> {a.Volume}");
        w.ResetParams();
        Require(ParamRegistry.All.All(p => p.IsDefault) && w.Chem.MatCap[2] == cap, "world reset of the laws");
        Console.WriteLine($"PASS laws: {ParamRegistry.All.Count} tunables in {ParamRegistry.Groups.Count} groups, presets round trip, gravity/block size/bubbles refresh what derives from them");
    }

    // More of the state than StateHash: pressures, climate fields and much of every body.
    ulong DeepHash()
    {
        ulong h = StateHash();
        void Mix(ulong x) { h ^= x; h *= 1099511628211UL; h ^= h >> 29; }
        void F(float x) => Mix((uint)BitConverter.SingleToInt32Bits(x));
        for (int v = 0; v < N * Z; v++) if (Pressure[v] != 0) { Mix((ulong)v); F(Pressure[v]); }
        for (int i = 0; i < N; i++) { F(Temp[i]); F(Water[i]); F(Ice[i]); F(Photon[i]); F(Bite[i]); }
        foreach (var kv in Buried.OrderBy(kv => kv.Key)) { Mix((ulong)kv.Key); foreach (var m in kv.Value.Matter) F(m); F(kv.Value.Order); }
        foreach (var a in Agents)
        {
            F(a.Mass); F(a.Volume); F(a.Tb); F(a.Lift); F(a.Vx); F(a.HeatHeld);
            Mix((ulong)a.Ip << 32 ^ (ulong)a.Sp << 16 ^ (ulong)a.InvTotal ^ (ulong)a.EnzN << 48 ^ (ulong)a.Links.Count << 56);
            for (int k = 0; k < a.EnzN; k++) F(a.Enz[k].Amount);
        }
        var (r0, r1, r2, r3) = mainRng.State;
        Mix((ulong)nextId); Mix((ulong)Tick); Mix(r0); Mix(r1); Mix(r2); Mix(r3);
        foreach (var c in ctxs) { var (t0, _, _, t3) = c.Rng.State; Mix(t0 ^ t3); Mix((ulong)c.IdCount); }
        return h;
    }

    // Save at tick T, load, run both K more ticks: the loaded world must follow the original exactly
    // (same state, same atoms). Exercised with the hand (bodies killed between ticks stay listed until
    // the next tick), a law changed mid-run and a file on disk.
    static void SaveLoadRegression()
    {
        foreach (int seed in new[] { 1, 3 })
        {
            var a = new World(seed, 800, true);
            for (int t = 0; t < 250; t++) a.Step();
            a.SetParam("FaceWork", 7.5);
            for (int t = 0; t < 50; t++) a.Step();
            var busy = a.Agents.First(x => !x.Dead);
            a.KillIn(busy.X, busy.Y, 2);
            a.Pour(busy.X + 10, busy.Y, 3, a.RandomPourable(), 0.3f);
            string path = Path.Combine(TestDir(), $"world-{seed}.sav");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            a.Save(path, "self-test");
            double saveMs = watch.Elapsed.TotalMilliseconds;
            long size = new FileInfo(path).Length;
            var info = ReadInfo(path);
            Require(info.Tick == a.Tick && info.Seed == seed && info.Note == "self-test" && info.Population == a.Agents.Count(x => !x.Dead), "save header");
            ParamRegistry.ResetDefaults();   // the load must bring the saved laws back
            watch.Restart();
            var b = Load(path);
            double loadMs = watch.Elapsed.TotalMilliseconds;
            Require(P.FaceWork == 7.5f && b.ParamLog.Count == 1 && b.ParamLog[0].Value == 7.5, "laws not restored by the load");
            Require(b.DeepHash() == a.DeepHash(), $"seed {seed}: loaded state differs at tick {a.Tick}");
            BudgetEqual(a.ElementBudget(), b.ElementBudget(), "loaded atoms", 0);
            b.CheckCellLists();
            for (int t = 0; t < 300; t++)
            {
                a.Step(); b.Step();
                if (t % 100 == 99) Require(a.DeepHash() == b.DeepHash(), $"seed {seed}: loaded world diverged at tick {a.Tick}");
            }
            Require(a.StateHash() == b.StateHash(), "state hash after the run");
            var atomsA = a.ElementBudget(); var atomsB = b.ElementBudget();
            BudgetEqual(atomsA, atomsB, "atoms after the run", 0);
            for (int e = 0; e < atomsA.Length; e++) Require(a.HandInput[e] == b.HandInput[e] && a.InteriorInput[e] == b.InteriorInput[e], "inputs differ");
            Require(a.Agents.Select(x => (x.Id, x.X, x.Y, x.Z, x.Energy, x.Mass, x.Hash)).SequenceEqual(b.Agents.Select(x => (x.Id, x.X, x.Y, x.Z, x.Energy, x.Mass, x.Hash))), "bodies differ after the run");
            Console.WriteLine($"PASS save/load seed {seed}: tick {a.Tick - 300} → +300 identical ({a.Agents.Count} bodies, hash {a.StateHash():x16}); file {size / 1024} KB, save {saveMs:F0} ms, load {loadMs:F0} ms");
            File.Delete(path);
            ParamRegistry.ResetDefaults();
        }
    }

    // Genome text: bytes → text → bytes exactly, canonical text survives the round trip, errors
    // name their lines, and protein genes come out as asked.
    static void AsmRegression()
    {
        var rng = new SimRng(11);
        for (int k = 0; k < 3000; k++)
        {
            var g = new byte[rng.Next(Genome.MinLen, k < 2500 ? 200 : Genome.MaxLen + 1)];
            rng.NextBytes(g);
            if (k % 4 == 0) g[g.Length - 1 - rng.Next(3)] = (byte)(Genome.EnzymeOp | rng.Next(4) << 6);   // a gene cut off by the end
            if (k % 7 == 0) g[^1] = Genome.Lit;
            string text = GenomeAsm.Disassemble(g, k % 2 == 0);
            var back = GenomeAsm.Assemble(text);
            Require(back.AsSpan().SequenceEqual(g), $"genome text round trip changed the bytes: {text}");
            Require(GenomeAsm.Disassemble(back) == GenomeAsm.Disassemble(g), "canonical text not stable");
        }
        foreach (var d in CreatureExamples.All)
        {
            var bytes = d.Assemble();
            Require(GenomeAsm.Assemble(GenomeAsm.Disassemble(bytes)).AsSpan().SequenceEqual(bytes), $"{d.Name}: example does not survive the round trip");
        }
        var (b1, b2, b3) = GenomeAsm.EnzymeBytes(Enzyme.Photo, 4, 17, 12.6f, 0.9);
        var e = Genome.Decode(b1, b2, b3);
        Require(e.Kind == Enzyme.Photo && e.A == 4 && e.B == 17 && MathF.Abs(e.Topt - 12.6f) <= 0.41f && MathF.Abs(e.Eff - 0.9f) < 0.05f, "protein gene not as asked");
        Require(!GenomeAsm.TryAssemble("dup\nfrobnicate\npush 9\nenzyme bind 40 1\nlit 300\nthrust\nswim down\nnop\nnop\nnop", out _, out var errors), "bad text assembled");
        Require(errors.Select(x => x.Line).SequenceEqual(new[] { 2, 3, 4, 5 }), "error lines: " + string.Join(", ", errors));
        Require(!GenomeAsm.TryAssemble("dup", out _, out errors) && errors.Count == 1, "a genome shorter than the minimum assembled");
        Console.WriteLine("PASS genome text: 3000 genomes round trip byte for byte, examples assemble, errors name their lines");
    }

    // Player designs: planted from outside (atoms booked in HandInput, energy in HandEnergy) or from the
    // place (no atoms change; nothing taken when the place cannot supply a body); the examples live;
    // designs and lineages survive save/load; a living body becomes a design and back.
    static void DesignRegression()
    {
        var w = new World(new WorldSettings { Seed = 1, InitialPop = 0, Abiogenesis = false, Strikes = false });
        var before = w.ElementBudget();
        int Find(Func<int, bool> ok)
        {
            for (int k = 0; k < N; k++)
            {
                int c = (int)((k * 2654435761L + 12345) % N);
                if (ok(c)) return c;
            }
            throw new Exception("no cell for the design fixture");
        }
        bool Mild(int c) => MathF.Abs(w.Temp[c] - 15) < 6 && c / W > 20 && c / W < H - 20;
        int land = Find(c => Mild(c) && !w.Submerged(c) && w.Count[c] == 0);
        int lake = Find(c => Mild(c) && w.Water[c] > 2 && w.Count[c] == 0);
        var import = new SpawnOptions { Matter = MatterSource.Import, Energy = EnergySource.Import, Count = 3, Radius = 2 };
        var planted = new System.Collections.Generic.List<(CreatureDesign d, SpawnResult r)>();
        double energy = 0;
        foreach (var d in CreatureExamples.All)
        {
            int at = d.Name == CreatureExamples.Swimmer.Name ? lake : land;
            var r = w.SpawnDesign(d, at % W, at / W, import);
            Require(r.Made == 3 && r.Agents.All(a => a.Designed && a.Lineage == r.Lineage && a.Energy == d.Energy), $"{d.Name}: {r}");
            Require(w.DesignOf(r.Lineage) == d.Name, "designed lineage not recorded");
            energy += r.EnergyImported;
            planted.Add((d, r));
        }
        Require(Math.Abs(w.HandEnergy - energy) < 1e-6, "imported energy not booked");
        var after = w.ElementBudget();
        for (int e = 0; e < after.Length; e++) after[e] -= w.HandInput[e];
        BudgetEqual(before, after, "designs brought from outside", 1e-6);

        // From the place: an empty spot gives nothing and changes nothing.
        var local = new CreatureDesign { Name = "местный", Genome = "label 0\npush 0\nphoto\ndigest\nyield\njmp 0\nnop\nnop", Body = new() { ["any"] = 8 }, Energy = 3 };
        int bare = Find(c => !w.Submerged(c) && w.Count[c] == 0 && w.Height[c] > 2 && w.VoxelBarrier(c * Z + w.Height[c] - 1) >= 2);
        foreach (int c in new[] { bare, w.Nb(bare, 0), w.Nb(bare, 1), w.Nb(bare, 2), w.Nb(bare, 3) })
            for (int s = 0; s < Chemistry.S; s++) w.C[s][c] = 0;   // swept clean (before the budget is taken)
        ulong hash = w.StateHash(); var budget = w.ElementBudget();
        var fail = w.SpawnDesign(local, bare % W, bare / W, new SpawnOptions { Radius = 0 });
        Require(fail.Made == 0 && fail.Error != null && w.StateHash() == hash, $"a bare spot planted a body or changed: {fail}");
        BudgetEqual(budget, w.ElementBudget(), "failed planting", 0);
        // A spot with loose matter that releases energy: the body is made of it, no atom comes or goes.
        int rich = Find(c => Mild(c) && !w.Submerged(c) && w.Count[c] == 0 && c != land);
        foreach (int s in w.Chem.Unstable.Take(2)) w.C[s][rich] += 6;
        w.C[w.Chem.Low[0]][rich] += 10;
        budget = w.ElementBudget();
        double[] hand = (double[])w.HandInput.Clone();
        var ok = w.SpawnDesign(local, rich % W, rich / W, new SpawnOptions { Radius = 0 });
        Require(ok.Made == 1 && ok.EnergyLocal >= 2.99 && ok.Agents[0].InvTotal == 8, $"local planting: {ok}");
        Require(w.HandInput.SequenceEqual(hand), "local planting booked an import");
        BudgetEqual(budget, w.ElementBudget(), "planted from local matter", 1e-3);

        // Save/load keeps the marks.
        var ms = new MemoryStream();
        w.Save(ms); ms.Position = 0;
        var copy = Load(ms);
        Require(copy.HandEnergy == w.HandEnergy && copy.DesignedLineages.Count == w.DesignedLineages.Count && copy.Agents.Count(a => a.Designed) == w.Agents.Count(a => a.Designed), "designs lost in save/load");

        // The examples live (and the atoms add up) for a while.
        before = w.ElementBudget();
        for (int e = 0; e < before.Length; e++) before[e] -= w.HandInput[e] + w.InteriorInput[e];
        const int ticks = 600;
        for (int t = 0; t < ticks; t++) w.Step();
        after = w.ElementBudget();
        for (int e = 0; e < after.Length; e++) after[e] -= w.HandInput[e] + w.InteriorInput[e];
        BudgetEqual(before, after, "designs living", 0.01);
        var lives = new System.Collections.Generic.List<string>();
        foreach (var (d, r) in planted)
        {
            int alive = r.Agents.Count(a => !a.Dead), kin = w.Agents.Count(a => !a.Dead && a.Lineage == r.Lineage);
            Require(alive > 0, $"{d.Name}: every planted body died within {ticks} ticks (causes {string.Join(",", r.Agents.Select(a => a.Cause))})");
            lives.Add($"{d.Name} {alive}/3 alive, lineage {kin}");
        }
        w.CheckCellLists();

        // A living body as a design: the same genome comes back.
        var donor = w.Agents.First(a => !a.Dead);
        var copied = CreatureDesign.FromJson(CreatureDesign.FromAgent(w, donor, "копия").ToJson());
        Require(copied.Assemble().AsSpan().SequenceEqual(donor.G) && copied.Body.Values.Sum() == donor.InvTotal, "a body copied as a design changed");
        string dir = Path.Combine(TestDir(), "creatures");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Require(CreatureLibrary.ExportExamples(dir) == 3 && CreatureLibrary.Save(copied, dir) != null, "library export");
        var listed = CreatureLibrary.List(dir);
        Require(listed.Count == 4 && listed.All(x => x.Design != null) && listed.Any(x => x.Design.Genome == CreatureExamples.Leaf.Genome), "library listing");
        Console.WriteLine($"PASS designs: brought in and from the place with exact atoms, nothing taken where nothing lies; after {ticks} ticks " + string.Join(", ", lives));
    }
}
