using System;
using System.Linq;

namespace Primordium;

public sealed partial class World
{
    // Spontaneous decay by one Arrhenius law (P.ArrheniusDecay 1, World.Decay), on small built worlds:
    // - every path conserves atoms and releases E[s] − E[products] > 0; of a split and a relaxation the one
    //   that releases more;
    // - the rate grows with temperature and wetness ((1 + DecayWetK) fully wet), falls with the bond and in a lattice;
    // - loose litter: warm goes faster than cold, wet faster than dry, as the law says (e^(−k·t) within 1e-3);
    // - a burial: deep and dry slower than the same matter on wet ground; a hot one at depth faster than a cold one;
    // - a block loses whole molecules into its burial, an ordered block slower than a disordered one;
    // - a body: warm loses more molecules than cold;
    // - atoms exact and the ledger closed (loose decay, body decay) at every step; a small living world under
    //   the law closes too and a save loads into the same continuation.
    public static void DecayRegression()
    {
        ParamRegistry.ResetDefaults();
        try
        {
            P.ArrheniusDecay = 1;
            var w = Blank(); var ch = w.Chem; var d = w.Decay;
            w.RefreshDecayA();
            Require(d.List.Length > 0, "no species with a downhill path");
            foreach (int s in d.List)
            {
                for (int e = 0; e < Chemistry.ElementCount; e++)
                    Require(ch.Atoms[s, e] == ch.Atoms[d.To1[s], e] + (d.To2[s] < 0 ? 0 : ch.Atoms[d.To2[s], e]), $"decay path of {s} changes atoms");
                int heat = ch.E[s] - ch.E[d.To1[s]] - (d.To2[s] < 0 ? 0 : ch.E[d.To2[s]]);
                Require(heat == d.Heat[s] && heat > 0, $"decay path of {s}: heat {d.Heat[s]}, energies say {heat}");
                int split = ch.SplitExo[s] ? ch.SplitEnergy(s) : 0;
                Require(d.Heat[s] >= split, $"decay path of {s} releases less than its split");
            }
            int sp = d.List.OrderBy(s => d.Barrier[s]).ElementAt(d.List.Length / 2);   // a middling species
            double k15 = w.DecayRate(sp, 15, 0);
            Require(w.DecayRate(sp, 35, 0) > 2 * k15 && w.DecayRate(sp, -15, 0) < k15 / 2, $"decay hardly depends on temperature ({w.DecayRate(sp, -15, 0):E2} / {k15:E2} / {w.DecayRate(sp, 35, 0):E2})");
            Require(Math.Abs(w.DecayRate(sp, 15, 1) / k15 - (1 + P.DecayWetK)) < 1e-9, "wet ground does not decay (1 + DecayWetK) times faster");
            Require(w.DecayRate(sp, 15, 0, d.MeanBond) < k15 * 1e-3, "an ordered lattice of ordinary bonds does not slow decay by orders of magnitude");
            int weak = d.List.OrderBy(s => d.Barrier[s]).First(), strong = d.List.OrderBy(s => d.Barrier[s]).Last();
            if (d.Barrier[strong] > d.Barrier[weak]) Require(w.DecayRate(strong, 15, 0) < w.DecayRate(weak, 15, 0), "a strong molecule decays as fast as a weak one");

            // Loose litter in four cells: warm/cold × wet/dry, 1000 ticks of environment steps.
            w.TrackHeat = true;
            int[] cells = { 10 * w.W + 10, 10 * w.W + 20, 20 * w.W + 10, 20 * w.W + 20 };
            float[] temp = { 30, 0, 30, 0 }, wet = { 0, 0, 1, 1 };
            foreach (int c in cells) w.C[sp][c] = 100;
            var atoms0 = w.ElementBudget();
            var e0 = w.AuditEnergy();
            int steps = 1000 / P.EnvEvery;
            for (int k = 0; k < steps; k++)
                for (int j = 0; j < 4; j++)
                {
                    int c = cells[j];
                    w.Temp[c] = temp[j]; w.Water[c] = wet[j]; w.Rain[c] = 0;
                    w.LooseDecayArrhenius(c, c / w.W);
                }
            var left = cells.Select(c => w.C[sp][c].D).ToArray();
            Require(left[0] < left[1] && left[2] < left[3], $"warm litter did not decay faster than cold ({left[0]:F2} vs {left[1]:F2})");
            Require(left[2] < left[0] && left[3] < left[1], $"wet litter did not decay faster than dry ({left[2]:F2} vs {left[0]:F2})");
            for (int j = 0; j < 4; j++)
            {
                double want = 100 * Math.Pow(1 - DecayShare(w.DecayRate(sp, temp[j], wet[j]), P.EnvEvery), steps);
                Require(Math.Abs(left[j] - want) <= 1e-3 * 100, $"litter at {temp[j]} °C, wet {wet[j]}: {left[j]:F4} left, the law says {want:F4}");
            }
            BudgetEqual(atoms0, w.ElementBudget(), "loose decay", 0);
            w.EnergyBalanced(e0, "loose decay (Arrhenius)", FLooseDecay);

            // Burials: under a column of stable rock, deep (dry) and near the top (wet), cold and hot at depth.
            int rock = Enumerable.Range(0, Chemistry.S).First(s => !d.Decays[s] && ch.Gap[s] == 0 && s != ch.Gas);
            int[] cols = { 30 * w.W + 30, 30 * w.W + 40, 40 * w.W + 30 };
            foreach (int c in cols) w.Column(c, 30, rock);
            w.StepStructure();
            int surface = 40 * w.W + 40;
            w.Temp[surface] = 15; w.Water[surface] = 1; w.C[sp][surface] = 100;
            for (int j = 0; j < 3; j++)
            {
                int c = cols[j];
                w.Temp[c] = 15; w.Water[c] = 0; w.Rain[c] = 0;
                w.Tmean[c] = j == 2 ? 60 : 15; w.CaveWarm[c] = 0;
                var b = w.BurialAt(c * w.Z + 6);
                b.Matter[sp] = 100;
            }
            atoms0 = w.ElementBudget();
            e0 = w.AuditEnergy();
            for (int k = 0; k < 1000 / P.MetamorphEvery; k++)
            {
                foreach (int c in cols) { w.Buried[c * w.Z + 6].Order = 0; w.Temp[c] = 15; }
                w.Tick += P.MetamorphEvery;   // passes take burials and rows in turn
                w.DeepDecay();
                for (int q = 0; q < P.MetamorphEvery / P.EnvEvery; q++) { w.Temp[surface] = 15; w.Water[surface] = 1; w.LooseDecayArrhenius(surface, surface / w.W); }
            }
            double deepCold = w.Buried[cols[0] * w.Z + 6].Matter[sp].D, deepHot = w.Buried[cols[2] * w.Z + 6].Matter[sp].D, wetTop = w.C[sp][surface].D;
            float tDeep = w.LocalTemp(cols[0], 6), tHot = w.LocalTemp(cols[2], 6);
            Require(deepCold > wetTop, $"buried matter decayed as fast as on wet ground ({deepCold:F2} vs {wetTop:F2} left)");
            Require(tHot > tDeep && deepHot < deepCold, $"a hot burial ({tHot:F1} °C) did not decay faster than a cold one ({tDeep:F1} °C): {deepHot:F2} vs {deepCold:F2}");
            BudgetEqual(atoms0, w.ElementBudget(), "burial decay", 0);
            w.EnergyBalanced(e0, "burial decay (Arrhenius)", FLooseDecay);

            // A block of a decaying species: ordered against disordered, under a cover so it is not the top.
            int blockSp = sp;
            int bo = 50 * w.W + 10, bd = 50 * w.W + 20;
            w.TestBlock(bo, 2, blockSp, 255); w.TestBlock(bd, 2, blockSp, 0);
            w.Column(bo, 4, rock); w.Column(bd, 4, rock);
            w.StepStructure();
            int n0 = w.VoxelCount(bo * w.Z + 2, blockSp), n0d = w.VoxelCount(bd * w.Z + 2, blockSp);
            float logA = P.DecayLogA;
            // Fast enough that the disordered block loses about a third in 8 passes (whole molecules can be counted).
            P.DecayLogA = logA + (float)Math.Log10(0.4 / (8.0 * P.MetamorphEvery) / w.DecayRate(blockSp, 25, 0)); w.RefreshDecayA();
            atoms0 = w.ElementBudget();
            e0 = w.AuditEnergy();
            for (int k = 0; k < 8; k++) { w.Temp[bo] = w.Temp[bd] = 25; w.Tick += P.MetamorphEvery; w.DeepDecay(); }
            int ordered = w.VoxelCount(bo * w.Z + 2, blockSp), loose = w.VoxelCount(bd * w.Z + 2, blockSp);
            Require(loose < n0d && n0 - ordered < n0d - loose, $"a disordered block did not lose more than an ordered one ({n0d} → {loose}, {n0} → {ordered})");
            Require(w.BurialOf(bd * w.Z + 2, out var bb) && bb.Matter[d.To1[blockSp]] >= n0d - loose, "a block's decay products are not in its burial");
            BudgetEqual(atoms0, w.ElementBudget(), "block decay", 0);
            w.EnergyBalanced(e0, "block decay (Arrhenius)", FLooseDecay);
            P.DecayLogA = logA; w.RefreshDecayA();

            // Bodies: the same molecules, warm and cold.
            P.DecayLogA = logA + 1; w.RefreshDecayA();
            var warm = w.TestAgent(60 * w.W + 10, 2, sp, 200); var cold = w.TestAgent(60 * w.W + 20, 2, sp, 200);
            e0 = w.AuditEnergy();
            atoms0 = w.ElementBudget();
            for (int k = 0; k < 2000; k++)
            {
                warm.Tb = 35; cold.Tb = -10;
                w.BodyDecayArrhenius(warm, warm.Y * w.W + warm.X);
                w.BodyDecayArrhenius(cold, cold.Y * w.W + cold.X);
            }
            Require(warm.Inv[sp] < cold.Inv[sp] && warm.Inv[sp] < 200, $"a warm body did not lose more than a cold one ({warm.Inv[sp]} vs {cold.Inv[sp]} of 200 left)");
            BodyConsistent(w, warm, "decay in a warm body"); BodyConsistent(w, cold, "decay in a cold body");
            BudgetEqual(atoms0, w.ElementBudget(), "body decay", 0);
            w.EnergyBalanced(e0, "body decay (Arrhenius)", FBodyDecay);
            P.DecayLogA = logA; w.RefreshDecayA();
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"PASS Arrhenius decay: {d.List.Length} species with a path; litter left of 100 after 1000 ticks warm/cold dry {left[0]:F1}/{left[1]:F1}, wet {left[2]:F1}/{left[3]:F1}; buried deep {deepCold:F1} (hot {deepHot:F1}) vs wet ground {wetTop:F1}; block ordered {ordered} of {n0}, disordered {loose} of {n0d}; body warm/cold {warm.Inv[sp]}/{cold.Inv[sp]} of 200; atoms exact, ledger closed"));

            // A small living world under the law: the ledger and atoms close; a save loads into the same continuation.
            foreach (int me in new[] { 0, 1 })
            {
                P.MatterEnergy = me;
                var a = new World(SmallSettings(4, 300, true)) { TrackHeat = true };
                var ea = a.AuditEnergy();
                var at0 = a.ElementBudget();
                for (int t = 0; t < 400; t++) a.Step();
                string note = EnergyWorldCheck(a, ea, $"Arrhenius world (MatterEnergy {me}), 400 ticks");
                var at = a.ElementBudget();
                for (int e = 0; e < at.Length; e++) Require(Math.Abs(at[e] - a.InteriorInput[e] - a.HandInput[e] - at0[e]) <= 0.5, $"Arrhenius world: element {e} drifted");
                Require(a.BurialDecayHeat + a.BlockDecayHeat >= 0, "negative decay heat");
                var ms = new System.IO.MemoryStream();
                a.Save(ms, "arrhenius");
                ms.Position = 0;
                P.ArrheniusDecay = 0;   // the law comes back with the file
                var b = Load(ms);
                Require(P.ArrheniusDecay == 1 && b.DeepHash() == a.DeepHash(), "Arrhenius save/load: the law or the world did not come back");
                for (int t = 0; t < 200; t++) { a.Step(); b.Step(); }
                Require(a.DeepHash() == b.DeepHash() && a.StateHash() == b.StateHash(), "Arrhenius save/load: the continuation diverged");
                Console.WriteLine($"PASS Arrhenius world (MatterEnergy {me}, 96×96×64, seed 4): {a.Agents.Count} bodies after 600 ticks, {note}, ground decay below the litter {a.BurialDecayHeat + a.BlockDecayHeat:F0}; save/load continues the same");
            }
        }
        finally { ParamRegistry.ResetDefaults(); }
    }
}
