using System;
using System.Linq;

namespace Primordium;

// The small test worlds (Scenario.cs): built as asked, consistent (cell lists, the support solver), and
// the world goes on keeping its atoms; a lake is water, a cave has its roof, a cliff its height.
public sealed partial class World
{
    public static void RunScenarioRegression() { ParamRegistry.ResetDefaults(); ScenarioRegression(); }

    static void ScenarioRegression()
    {
        var w = Blank();
        Require(w.W == TinySide && w.Z == TinyLevels && w.Vents.Count == 0 && w.Height.All(h => h == 2) && w.Water.All(x => x == 0), "a blank world is not flat and dry");
        int s = Enumerable.Range(0, Chemistry.S).OrderByDescending(k => w.Chem.Bond[k] / w.Chem.Mass[k]).First();   // the strongest rock
        w.Floor(12, s);
        Require(w.Height.All(h => h == 12), "the floor is not level");
        // A cave under the floor: 6×4 columns, levels 4…7 emptied, its roof 4 blocks.
        int cave = w.Cave(10, 10, 6, 4, 4, 4);
        Require(w.Roof(cave, 4) == 4 && w.Roof(cave + 2 * w.W + 3, 4) == 4 && w.Height[cave] == 12, $"cave roof {w.Roof(cave, 4)}");
        // A lake: a pit of radius 5 down to level 8, 3 deep.
        int lake = 40 * w.W + 40;
        w.Lake(40, 40, 5, 8, 3);
        Require(w.Height[lake] == 8 && w.Water[lake] == 3 && w.Submerged(lake) && w.Height[lake + 7] == 12 && w.Water[lake + 7] == 0, "the lake");
        // A cliff: columns 50…53 raised to 30.
        w.Cliff(50, 54, 30, s);
        Require(w.Height[20 * w.W + 50] == 30 && w.Height[20 * w.W + 54] == 12, "the cliff");
        // A column of strata: 3 levels of one kind, 2 of another.
        int other = (s + 1) % Chemistry.S, col = 30 * w.W + 20;
        w.Strata(col, (s, 3), (other, 2));
        Require(w.Height[col] == 17 && w.VoxelCount(col * w.Z + 13, s) > 0 && w.VoxelCount(col * w.Z + 15, other) > 0, "the strata");
        // Two bodies side by side, on the floor.
        var (a, b) = w.Pair(25 * w.W + 30, 1, 20);
        Require(a.X + 1 == b.X && a.Y == b.Y && a.Z == 12 && b.Z == 12, "the pair");
        w.CheckCellLists();
        var atoms0 = w.ElementBudget();
        for (int t = 0; t < 100; t++) w.Step();
        var atoms1 = w.ElementBudget();
        for (int e = 0; e < atoms1.Length; e++) Require(Math.Abs(atoms1[e] - w.InteriorInput[e] - w.HandInput[e] - atoms0[e]) < 1e-6, $"scenario world: element {e} drifted");
        Require(w.Roof(cave, 4) == 4, "the cave's roof of strong rock fell in");
        w.CheckCellLists();
        // A generated small world and a strip of the planet.
        var t0 = Tiny(3, -1, true, true);
        var strip = new World(StripSettings(3, 0, false));
        Require(t0.Agents.Count > 0 && t0.Agents.Count <= t0.PerArea(P.InitialPop) && strip.H == WorldSettings.DefaultHeight && Math.Abs(strip.Latitude(40) - new World(new WorldSettings { Seed = 3, InitialPop = 0 }).Latitude(40)) < 1e-6,
            "tiny world or strip");
        Console.WriteLine($"PASS scenarios: blank {w.W}×{w.H}×{w.Z}, floor, cave (roof 4), lake 3 deep, cliff, strata, a pair; 100 ticks, atoms exact; tiny world with {t0.Agents.Count} first bodies, a strip with the planet's latitudes");
    }
}
