using System;
using System.Linq;

namespace Primordium;

public sealed partial class World
{
    // Resources (World.Resources, ROADMAP 4):
    // - GasDiffK: the gas still moves as an integer flow per edge (atoms exact over uneven ground), and a
    //   puff spreads slower: its mean squared distance after 200 ticks scales with the coefficient;
    // - CaveGasK: under 3 blocks of roof a body reaches e^(−3/K) of its column's air gas (none with the law
    //   off, as before); uptake there takes what lies on the cave floor first, then the column's air,
    //   atoms exact; the column's air is what the surface above drinks from too;
    // - the resource probe is observation only: the same world hash with it on;
    // - with both laws switched on mid-run in a living world, atoms and the energy ledger close.
    public static void ResourcesRegression()
    {
        ParamRegistry.ResetDefaults();
        // Diffusion: exact, and slower by GasDiffK.
        double Spread(float k)
        {
            P.GasDiffK = k;
            var w = Fixture(); int g = w.Chem.Gas;
            var rng = new SimRng(9);
            for (int i = 0; i < N; i++) w.Height[i] = 2 + (i / W < 20 ? rng.Next(3) : 0);
            w.RecomputeFlow();
            int c0 = 120 * W + 128;
            w.C[g][c0] = 100000;
            for (int i = 0; i < 20 * W; i++) w.C[g][i] = (float)(rng.NextDouble() * 2000);
            var before = w.ElementBudget();
            for (int t = 0; t < 200; t++) w.Diffuse();
            BudgetEqual(before, w.ElementBudget(), $"gas diffusion, GasDiffK {k}", 0);
            double m2 = 0, mass = 0;
            for (int y = 85; y < 156; y++)
                for (int x = 93; x < 164; x++) { double q = w.C[g][y * W + x]; m2 += q * ((x - 128) * (x - 128) + (y - 120) * (y - 120)); mass += q; }
            return m2 / mass;
        }
        double s1 = Spread(1f), s03 = Spread(0.3f);
        P.GasDiffK = 1;
        Require(Math.Abs(s03 / s1 - 0.3) < 0.02, $"GasDiffK 0.3: spread {s03:F1} vs {s1:F1} cells² (want ×0.3)");

        // Gas under a roof.
        var v = Fixture(); var ch = v.Chem; int gas = ch.Gas;
        int cave = 40 * W + 40;
        for (int z = 3; z < 6; z++) { v.Mat[cave * Z + z] = Chemistry.Bedrock; v.Order[cave * Z + z] = 255; }
        v.Height[cave] = 6; v.TerrainChanged(cave);
        v.StepStructure();
        Require(v.Roof(cave, 2) == 3, "roof of 3 blocks");
        v.C[gas][cave] = 10;
        var a = v.TestAgent(cave, 2, (gas & ~1) == gas ? gas + 1 : gas - 1, 10);
        P.CaveGasK = 0;
        Require(v.LooseAmount(a, cave, gas) == 0, "law off: the column's air reached under a roof");
        P.CaveGasK = 3;
        float reach = v.LooseAmount(a, cave, gas);
        Require(MathF.Abs(reach - 10 * MathF.Exp(-1)) < 1e-4f, $"under 3 blocks with CaveGasK 3: {reach:F4} of 10 (want {10 * MathF.Exp(-1):F4})");
        v.BurialAt(cave * Z + 1).Matter[gas] += Qty.Of(0.5);   // gas lying on the cave floor goes first
        var before2 = v.ElementBudget();
        int held = a.Inv[gas];
        for (int k = 0; k < 4; k++) v.Intake(a, cave, gas);
        BudgetEqual(before2, v.ElementBudget(), "uptake of gas under a roof", 0);
        Require(v.BurialAt(cave * Z + 1).Matter[gas] == 0, "the cave floor's gas was not taken first");
        double took = 10 - v.C[gas][cave].D;
        Require(a.Inv[gas] == held + 4 && Math.Abs(took - 3.5) < 1e-6, $"took {took:F4} from the column air, body +{a.Inv[gas] - held}");
        double air = v.C[gas][cave].D;
        v.Intake(a, cave, gas);   // one sip takes at most the reachable share of what is left
        Require(air - v.C[gas][cave].D <= air * Math.Exp(-1) + 1e-6, "a sip under the roof took more than its reachable share");
        P.CaveGasK = 0;

        // The probe watches only; the laws switched on mid-run keep atoms and energy.
        var w1 = new World(3, 600, true); var w2 = new World(3, 600, true);
        w2.ResProbe = new ResourceProbe();
        for (int t = 0; t < 300; t++) { w1.Step(); w2.Step(); }
        Require(w1.StateHash() == w2.StateHash(), "the resource probe changed the world");
        Require(w2.ResProbe.Ticks == 300 && w2.ResProbe.SpeciesIntake.Sum() > 0, "the probe booked nothing");
        var w3 = new World(2, 800, true) { TrackHeat = true };
        var atoms0 = w3.ElementBudget(); var e0 = w3.AuditEnergy();
        string note = "";
        for (int t = 1; t <= 900; t++)
        {
            if (t == 100) Require(w3.SetParam("GasDiffK", 0.2), "law GasDiffK");
            if (t == 300) Require(w3.SetParam("CaveGasK", 3), "law CaveGasK");
            w3.Step();
            if (t % 300 != 0) continue;
            var atoms = w3.ElementBudget();
            for (int e = 0; e < atoms.Length; e++)
            {
                double d = atoms[e] - w3.InteriorInput[e] - w3.HandInput[e] - atoms0[e];
                Require(Math.Abs(d) <= 1e-6, $"resource laws, tick {t}: element {e} drifted by {d}");
            }
            note = EnergyWorldCheck(w3, e0, $"resource laws, tick {t}");
        }
        ParamRegistry.ResetDefaults();
        Console.WriteLine($"PASS resources: gas diffusion exact, puff spread after 200 ticks {s1:F1} cells² (GasDiffK 1), {s03:F1} (0.3); under 3 blocks {reach:F2} of 10 reachable, floor first, atoms exact; probe watches only; laws mid-run: atoms exact, {note}");
    }
}
