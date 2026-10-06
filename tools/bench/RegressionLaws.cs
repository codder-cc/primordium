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
}
