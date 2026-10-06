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
}
