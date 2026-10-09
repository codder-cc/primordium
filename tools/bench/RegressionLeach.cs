using System;

namespace Primordium;

public sealed partial class World
{
    // Leaching (World.Leach, ROADMAP 4.1):
    // - wet ground: a share of the surface litter moves into the burial under the top block, exactly
    //   (atoms, Qty), by mobility (lighter molecules faster), every species alike; a body on the surface
    //   no longer reaches it; dry ground under a clear sky keeps its litter;
    // - under a top block with a void beneath it, the litter drips onto the cavity's floor;
    // - when the top block goes, its burial below becomes the floor: the matter is reachable again;
    // - in a living world with the law switched on mid-run, atoms and the energy ledger close.
    public static void LeachRegression()
    {
        ParamRegistry.ResetDefaults();
        var w = Fixture(); var ch = w.Chem;
        int food = 0, heavy = 0;   // the lightest and the heaviest molecule of the chemistry
        for (int s = 0; s < Chemistry.S; s++)
        {
            if (ch.Diff[s] > ch.Diff[food]) food = s;
            if (ch.Diff[s] < ch.Diff[heavy]) heavy = s;
        }
        int other = -1;
        for (int s = 0; s < Chemistry.S && other < 0; s++) if (s != food && s != heavy) other = s;   // what test bodies hold
        int c = 50 * w.W + 50;
        for (int z = 2; z < 6; z++) { w.Mat[c * w.Z + z] = Chemistry.Bedrock; w.Order[c * w.Z + z] = 255; }
        w.Height[c] = 6; w.TerrainChanged(c);
        w.StepStructure();
        w.C[food][c] = 10; w.C[heavy][c] = 10;
        Array.Clear(w.Rain);
        var a = w.TestAgent(c, 6, other, 5);
        var before = w.ElementBudget();
        Require(w.LooseAmount(a, c, food) == 10, "litter before leaching");
        double k = 0.5;   // the lightest molecule: half of it
        w.LeachPass();
        w.Water[c] = 0;
        w.Leach(c, k);
        Require(w.C[food][c] == 10 && w.C[heavy][c] == 10, "dry ground under a clear sky leached");
        w.Water[c] = 1;
        w.Leach(c, k);
        BudgetEqual(before, w.ElementBudget(), "leaching", 0);
        double left = w.C[food][c].D, leftHeavy = w.C[heavy][c].D;
        Require(Math.Abs(left - 5) < 1e-6, $"leaching took {10 - left} of the lightest litter (want 5)");
        double wantHeavy = 10 * (1 - 0.5 * ch.Diff[heavy] / ch.Diff[food]);
        Require(Math.Abs(leftHeavy - wantHeavy) < 1e-6 && leftHeavy > left, $"the heaviest litter: {leftHeavy:F4} left (want {wantHeavy:F4}, more than the lightest {left:F4})");
        Require(w.BurialOf(c * w.Z + 4, out var b) && b.Matter[food] + w.C[food][c] == 10, "the litter is not in the burial under the top block");
        Require(Math.Abs(w.LooseAmount(a, c, food) - left) < 1e-6, "a body on the surface still reaches the leached litter");
        // The top block goes: its burial below becomes the floor.
        w.Die(a, c, CauseHand); w.RemoveDead();
        w.RemoveVoxel(c, 5);
        Require(w.Height[c] == 5, "top block not removed");
        BudgetEqual(before, w.ElementBudget(), "leaching, then the top block removed", 0);
        var a2 = w.TestAgent(c, 5, other, 5);
        Require(Math.Abs(w.LooseAmount(a2, c, food) - 10) < 1e-6, $"after the top block went, the floor holds {w.LooseAmount(a2, c, food)} (want 10: lying + leached)");
        // A void under the top block: the litter drips onto the cavity's floor.
        int d = 60 * w.W + 60;
        for (int z = 2; z < 8; z++) if (z != 5 && z != 6) { w.Mat[d * w.Z + z] = Chemistry.Bedrock; w.Order[d * w.Z + z] = 255; }
        w.Height[d] = 8; w.TerrainChanged(d);
        w.C[food][d] = 8; w.Water[d] = 1;
        w.Leach(d, k);
        Require(w.BurialOf(d * w.Z + 4, out var cf) && Math.Abs(cf.Matter[food].D - 4) < 1e-6, "the litter did not drip onto the cavity's floor");

        // A living world: the law mid-run keeps atoms and energy; off it changes nothing.
        var w1 = new World(2, 800, true); var w2 = new World(2, 800, true);
        for (int t = 0; t < 200; t++) { w1.Step(); w2.Step(); }
        Require(w1.StateHash() == w2.StateHash(), "two identical worlds diverged");
        var w3 = new World(2, 800, true) { TrackHeat = true };
        var atoms0 = w3.ElementBudget(); var e0 = w3.AuditEnergy();
        string note = "";
        double litter0 = 0;
        for (int t = 1; t <= 900; t++)
        {
            if (t == 100)
            {
                for (int s = 0; s < Chemistry.S; s++) for (int i = 0; i < w.N; i++) litter0 += w3.C[s][i];
                Require(w3.SetParam("LeachK", 0.001), "law LeachK");
            }
            w3.Step();
            if (t % 300 != 0) continue;
            var atoms = w3.ElementBudget();
            for (int e = 0; e < atoms.Length; e++)
            {
                double dd = atoms[e] - w3.InteriorInput[e] - w3.HandInput[e] - atoms0[e];
                Require(Math.Abs(dd) <= 1e-6, $"leaching, tick {t}: element {e} drifted by {dd}");
            }
            note = EnergyWorldCheck(w3, e0, $"leaching, tick {t}");
        }
        double litter = 0;
        for (int s = 0; s < Chemistry.S; s++) for (int i = 0; i < w.N; i++) litter += w3.C[s][i];
        Require(w3.Leached > 0 && litter < litter0, $"leaching 0.001 for 800 ticks: litter {litter0:F0} -> {litter:F0}, leached {w3.Leached:F0}");
        ParamRegistry.ResetDefaults();
        Console.WriteLine($"PASS leach: wet ground: half the lightest litter under the top block exactly, the heaviest less by mobility, dry ground keeps it, out of reach until the block goes; drips into a cavity; living world LeachK 0.001 for 800 ticks: litter {litter0:F0} -> {litter:F0} (leached {w3.Leached:F0}), atoms exact, {note}");
    }
}
