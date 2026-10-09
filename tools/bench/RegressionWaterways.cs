using System;
using System.IO;

namespace Primordium;

public sealed partial class World
{
    // Water as a fluid (World.Waterways), on the flat bedrock fixture:
    // - currents: a step in a channel's water makes Flow give the water a speed down the step, none across
    //   it; a body afloat in moving water goes with it a cell at a time at the same height and for free,
    //   one on the bottom stays, the current does not carry anybody onto the bank; the law off — nothing drifts;
    // - floating ice: a lake at −20 °C for half a year keeps liquid water under its ice (the old law
    //   freezes it through), the water is conserved, a swimmer under the ice is cut off from the air;
    // - caves: a tunnel under a hill with its mouth under the sea fills to its roof, a higher chamber
    //   behind it only to the tunnel's roof (its air has nowhere to go), the water conserved; a body
    //   floats in it; the chamber opened to the sky gives its water to the surface, rock laid into a
    //   flooded tunnel pushes the water out, still conserved; save and load keep the cave water and the
    //   continuation; with the sea gone the caves drain; the law off gives the water back to the surface.
    public static void WaterwaysRegression()
    {
        ParamRegistry.ResetDefaults();
        string currents = CurrentsTest(), ice = FloatingIceTest(), caves = CaveWaterTest();
        ParamRegistry.ResetDefaults();
        Console.WriteLine($"PASS waterways: {currents}; {ice}; {caves}");
    }

    static string CurrentsTest()
    {
        var w = Fixture();
        int row = 80 * w.W;
        // A step in the water of a channel: the deep end runs towards the shallow one.
        for (int y = 76; y <= 84; y++) for (int x = 90; x < 120; x++) w.Water[y * w.W + x] = x < 105 ? 4 : 1;
        w.Flow();
        float u = w.CurX[row + 104], v = w.CurY[row + 104];
        Require(u > 0.01f && Math.Abs(v) < 1e-6f && w.CurX[row + 95] == 0, $"a step in the water gave no current down it: east {u}, north {v}, upstream {w.CurX[row + 95]}");
        string flow = $"step 4→1 block: {u:F3} cells/tick down it";

        w = Fixture();
        var ch = w.Chem;
        int heavy = -1;
        for (int s = 0; s < Chemistry.S; s++)
            if (s != ch.Gas && (heavy < 0 || ch.Mass[s] / ch.Volume[s] > ch.Mass[heavy] / ch.Volume[heavy])) heavy = s;
        for (int x = 100; x < 110; x++) w.Water[row + x] = 6;
        for (int z = 2; z < 8; z++) w.TestBlock(row + 110, z, heavy);   // a bank above the surface
        void Run(Agent a, int ticks) { for (int t = 0; t < ticks; t++) { int cell = a.Y * w.W + a.X; w.Move(a, ref cell); } }
        Agent Floater(int cell)
        {
            var f = w.TestAgent(cell, 2, heavy, 20);
            while (f.Density >= P.WaterDensity) w.AddMol(f, ch.Gas);
            return f;
        }
        var fish = Floater(row + 101);
        var stone = w.TestAgent(row + 102, 2, heavy, 20);
        Run(fish, 400); Run(stone, 100);
        Require(fish.Lift == 6 && OnFloor(stone), $"the swimmers did not settle: fish x {fish.X} lift {fish.Lift} density {fish.Density}, stone lift {stone.Lift}");
        for (int x = 100; x < 110; x++) w.CurX[row + x] = 0.3f;
        double e0 = fish.Energy;
        float level = Level(fish);
        Run(fish, 12); Run(stone, 12);
        Require(fish.X == 104 && Level(fish) == level && fish.Energy == e0, $"a swimmer was not carried 3.6 cells for free at its height: x {fish.X}, level {Level(fish)} (was {level}), energy {fish.Energy - e0:+0.000;-0.000}");
        Require(stone.X == 102, "the current moved a body lying on the bottom");
        Run(fish, 30);
        Require(fish.X == 109 && w.InWater(fish), $"the current carried a swimmer onto the bank: x {fish.X}");
        P.Currents = 0;
        var still = Floater(row + 101);
        Run(still, 100);
        Require(still.X == 101 && still.DriftX == 0, "the law off still drifts");
        P.Currents = 1;
        w.CheckCellLists();
        return $"currents: {flow}, a swimmer carried 3 cells in 12 ticks at 0.3 at its height for free, the bottom and the bank hold";
    }

    static string FloatingIceTest()
    {
        (float liquid, float ice, double drift, World w) Winter(int law)
        {
            P.IceFloat = law;
            var w = Fixture();
            Array.Fill(w.Water, 6f); Array.Fill(w.Temp, -20f);
            w.Moisture = 0;
            double t0 = w.WaterTotal();
            for (int k = 0; k < 1500; k++) w.Hydro();   // half a year of env steps at −20 °C
            int c = 80 * w.W + 100;
            return (w.Water[c], w.Ice[c], w.WaterTotal() - t0, w);
        }
        var (l0, i0, d0, _) = Winter(0);
        var (l1, i1, d1, w) = Winter(1);
        Require(l0 < 0.05f && i0 > 5.9f, $"the old law did not freeze the lake through: water {l0}, ice {i0}");
        Require(l1 > 1.5f && i1 > 2 && Math.Abs(l1 + i1 - 6) < 1e-3f, $"floating ice did not keep water under it: water {l1}, ice {i1}");
        Require(Math.Abs(d0) < 1e-6 * 6 * w.N && Math.Abs(d1) < 1e-6 * 6 * w.N, $"water not conserved through freezing: {d0:E1} / {d1:E1}");
        // A swimmer under the ice: no air above it.
        int cell = 80 * w.W + 100, gas = w.Chem.Gas;
        var f = w.TestAgent(cell, 2, 0, 20);
        while (f.Density >= P.WaterDensity) w.AddMol(f, gas);
        for (int t = 0; t < 1000; t++) { int c = cell; w.Move(f, ref c); }
        Require(f.Lift == w.Water[cell] && !w.AtSurface(f, cell) && w.Exposure(f, cell, gas) < MathF.Exp(-2), $"a swimmer under {w.Ice[cell]:F1} blocks of ice breathes the air: lift {f.Lift}, exposure {w.Exposure(f, cell, gas)}");
        P.IceFloat = 1;
        return $"ice floats: −20 °C for half a year, a 6-block lake keeps {l1:F1} of water under {i1:F1} of ice (old law: {l0:F2} / {i0:F1}), water conserved ({d1:+0.0E0;-0.0E0}), no air under the ice";
    }

    static string CaveWaterTest()
    {
        var w = Fixture();
        Array.Fill(w.Water, 5f);   // a sea everywhere: level 7
        int y0 = 60, row = y0 * w.W;
        // A hill (x 105…112, rows 58…62) to level 10. Under it in row 60 a tunnel at levels 2–3 from the
        // sea at x 104 to x 108, then a chamber at levels 2–6 at x 109.
        for (int y = 58; y <= 62; y++)
            for (int x = 105; x <= 112; x++)
            {
                int c = y * w.W + x;
                w.Water[c] = 0;
                int top = y == y0 && x >= 105 && x <= 108 ? 4 : y == y0 && x == 109 ? 7 : 2;
                for (int z = top; z < 10; z++) { w.Mat[c * w.Z + z] = Chemistry.Bedrock; w.Order[c * w.Z + z] = 255; }
                for (int z = 2; z < top; z++) w.Mat[c * w.Z + z] = y == y0 && x <= 109 ? Chemistry.Air : Chemistry.Bedrock;
                w.Height[c] = 10;
                w.TerrainChanged(c);
            }
        double total0 = w.WaterTotal();
        // The surface flow rounds in float over a planet of water; what the caves take and give is
        // checked on its own (caveDrift: the change of the total over CaveFlow alone).
        double caveDrift = 0;
        void Steps(World x, int n)
        {
            for (int k = 0; k < n; k++)
            {
                x.Flow();
                double t = x.WaterTotal();
                x.CaveFlow();
                caveDrift += x.WaterTotal() - t;
            }
        }
        Steps(w, 600);
        float tunnel = w.CaveDepth(row + 106, 2), chamber = w.CaveDepth(row + 109, 2);
        double drift = w.WaterTotal() - total0, through = caveDrift;
        Require(Math.Abs(tunnel - 2) < 1e-4f && w.CaveDepth(row + 105, 2) > 1.9999f && w.CaveDepth(row + 108, 2) > 1.9999f, $"the tunnel did not fill to its roof: {tunnel}");
        Require(chamber > 1.999f && chamber < 2.0001f, $"the chamber behind the tunnel did not stop at the tunnel's roof (its air is trapped): {chamber}");
        Require(Math.Abs(through) < 1e-3 && Math.Abs(drift) < 2e-6 * total0, $"water not conserved flooding the cave: {through:E2} through the caves, {drift:E2} of {total0:F0} in all");
        string fill = $"tunnel full ({tunnel:F3}), the chamber behind it to {chamber:F3} of 5 (an air pocket), drift through the caves {through:+0.0E0;-0.0E0}";

        // A body in the flooded chamber floats up to the water's surface there, under the air pocket.
        var f = w.TestAgent(row + 109, 2, 0, 20);
        while (f.Density >= P.WaterDensity) w.AddMol(f, w.Chem.Gas);
        for (int t = 0; t < 1000; t++) { int c = row + 109; w.Move(f, ref c); }
        Require(w.InWater(f) && f.InvTotal > 0 && Math.Abs(f.Lift - chamber) < 1e-3f && w.AtSurface(f, row + 109), $"a body in cave water did not float to its surface: lift {f.Lift}, depth {chamber}");
        var g = w.TestAgent(row + 107, 2, 0, 20);
        while (g.Density >= P.WaterDensity) w.AddMol(g, w.Chem.Gas);
        for (int t = 0; t < 1000; t++) { int c = row + 107; w.Move(g, ref c); }
        Require(w.InWater(g) && g.Lift == 1 && !w.AtSurface(g, row + 107), $"a body in the full tunnel is not held under its roof: lift {g.Lift}");
        w.Unplace(f, row + 109); w.Unplace(g, row + 107); w.Agents.Remove(f); w.Agents.Remove(g);

        // Save and load in the middle of it: the same cave water and the same continuation.
        var ms = new MemoryStream();
        w.Save(ms, "waterways");
        ms.Position = 0;
        var l = Load(ms);
        Require(l.CaveRuns == w.CaveRuns && l.CaveWaterTotal() == w.CaveWaterTotal(), "the cave water was not saved");
        Steps(w, 20); Steps(l, 20);
        Require(l.DeepHash() == w.DeepHash(), "the loaded world's water diverged");

        // The chamber opened to the sky: its water joins the surface water of its column.
        caveDrift = 0;
        for (int z = 9; z >= 7; z--) w.RemoveVoxel(row + 109, z);
        Steps(w, 1);
        Require(w.Height[row + 109] == 2 && w.CaveDepth(row + 109, 2) == 0 && w.Water[row + 109] > 1, $"the opened chamber kept its water: height {w.Height[row + 109]}, surface water {w.Water[row + 109]}");
        // Rock laid into the flooded tunnel pushes its water up and out.
        w.Mat[(row + 106) * w.Z + 2] = Chemistry.Bedrock; w.Order[(row + 106) * w.Z + 2] = 255; w.TerrainChanged(row + 106);
        Steps(w, 1);
        Require(w.CaveDepth(row + 106, 2) == 0 && w.CaveDepth(row + 106, 3) <= 1, $"rock in the tunnel did not displace its water: {w.CaveDepth(row + 106, 3)}");
        Require(Math.Abs(caveDrift) < 1e-4, $"water not conserved when the rock changed: {caveDrift:E2}");

        // The sea gone: the caves drain through the mouth, all but a pool behind the sill the rock made
        // at x 106 (the tunnel's floor is a level higher there): x 107–108 keep one block.
        for (int i = 0; i < w.N; i++) if (w.Height[i] == 2) w.Water[i] = 0;
        caveDrift = 0;
        float held = (float)w.CaveWaterTotal();
        Steps(w, 600);
                double left = w.CaveWaterTotal();
        float pool = w.CaveDepth(row + 107, 2);
        Require(Math.Abs(pool - 1) < 0.01f && Math.Abs(w.CaveDepth(row + 108, 2) - 1) < 0.01f && w.CaveDepth(row + 105, 2) < 0.01f && w.CaveDepth(row + 106, 3) < 0.01f && Math.Abs(caveDrift) < 1e-3,
            $"the caves did not drain to the sill: {left:F4} left of {held:F2} ({w.CaveRuns} wet runs), behind the sill {pool:F3}");

        // The law off: whatever stands in the caves goes up onto the surface.
        w.SetCaveWater(row + 107, 2, 1.5f);
        double before = w.WaterTotal();
        P.CaveWater = 0;
        w.CaveFlow();
        Require(w.CaveRuns == 0 && Math.Abs(w.WaterTotal() - before) < 1e-4, "the law off kept water in the caves");
        P.CaveWater = 1;
        return $"caves: {fill}; a body floats at the cave water's surface and stays under a full tunnel's roof; saved; opened, filled with rock and drained ({held:F1} → {left:F2}: a pool behind a sill stays), conserved";
    }
}
