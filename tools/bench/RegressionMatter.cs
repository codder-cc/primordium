using System;
using System.Linq;

namespace Primordium;

// Body energy as matter (P.MatterEnergy 1, World.Charge): probes on a small blank world. Each probe checks
// atoms (exact, ElementBudget), the ledger (World.Energy) and what the law says should happen.
public sealed partial class World
{
    public static void MatterEnergyRegression()
    {
        int law = P.MatterEnergy;
        try
        {
            ShiftProbe();
        }
        finally { P.MatterEnergy = law; }
    }

    // The mass, room and counts of a body recomputed from what it holds (Inv, Pend, protein substrate).
    static void BodyConsistent(World w, Agent a, string what)
    {
        float mass = 0, room = 0;
        int total = 0, unstable = 0, solids = 0;
        for (int s = 0; s < Chemistry.S; s++)
        {
            Require(a.Pend[s].Raw >= 0 && a.Pend[s].Raw < 1L << Qty.Bits, $"{what}: Pend[{s}] = {a.Pend[s]} outside [0, 1)");
            Require(a.Inv[s] >= 0, $"{what}: Inv[{s}] = {a.Inv[s]}");
            double n = a.Inv[s] + a.Pend[s].D;
            mass += (float)(n * w.Chem.Mass[s]); room += (float)(n * w.Chem.BodyVolume[s]);
            total += a.Inv[s];
            if (w.Chem.SplitExo[s]) unstable += a.Inv[s];
            if (w.Chem.Solid[s]) solids += a.Inv[s];
        }
        for (int k = 0; k < a.EnzN; k++) { mass += a.Enz[k].Matter.F * w.Chem.Mass[a.Enz[k].Material]; room += a.Enz[k].Matter.F * w.Chem.Volume[a.Enz[k].Material]; }
        Require(total == a.InvTotal && unstable == a.Unstable && solids == a.Solids, $"{what}: counts {a.InvTotal}/{a.Unstable}/{a.Solids}, recounted {total}/{unstable}/{solids}");
        Require(MathF.Abs(mass - a.Mass) <= 1e-3f * (1 + mass) && MathF.Abs(room - a.Volume) <= 1e-3f * (1 + room), $"{what}: mass {a.Mass} / room {a.Volume}, recomputed {mass} / {room}");
    }

    // Shift: a fraction of one kind becomes another inside the body, whole molecules broken into the fraction
    // as needed; atoms, the ledger (an excitation moved by hand here is booked as energy-k-free: the stock
    // changes by exactly the energy difference), mass and room exact.
    static void ShiftProbe()
    {
        var w = Blank();
        var ch = w.Chem;
        int c = 20 * w.W + 20;
        int exc = ch.Excited.First(s => ch.Gap[s] > 0 && ch.SplitA[s] == Chemistry.Ground(s));   // an excited monomer
        int ground = Chemistry.Ground(exc);
        int split = ch.Fuel.Length > 0 ? ch.Fuel[0] : ch.Unstable.First(s => ch.SplitA[s] >= 0 && ch.SplitB[s] >= 0);
        var a = w.TestAgent(c, 2, exc, 3);
        for (int k = 0; k < 4; k++) w.AddMol(a, split);
        var atoms0 = w.ElementBudget();
        var e0 = w.AuditEnergy();
        w.Shift(a, exc, ground, -1, Qty.Of(0.3));    // relax 0.3 of a molecule: a whole one is broken into the fraction
        Require(a.Inv[exc] == 2 && Math.Abs(a.Pend[exc].D - 0.7) < 1e-9 && Math.Abs(a.Pend[ground].D - 0.3) < 1e-9, $"shift: relaxation 0.3 left {a.Inv[exc]} + {a.Pend[exc]} excited, {a.Pend[ground]} ground");
        BodyConsistent(w, a, "shift relax");
        w.Shift(a, exc, ground, -1, Qty.Of(0.9));    // more than the fraction: the next whole one
        Require(a.Inv[exc] == 1 && Math.Abs(a.Pend[exc].D - 0.8) < 1e-9 && a.Inv[ground] == 1 && Math.Abs(a.Pend[ground].D - 0.2) < 1e-9, "shift: borrowing a whole molecule and completing one");
        BodyConsistent(w, a, "shift borrow");
        int sa = ch.SplitA[split], sb = ch.SplitB[split];
        w.Shift(a, split, sa, sb, Qty.Of(0.7));     // a split of 0.7 molecule
        BodyConsistent(w, a, "shift split");
        Require(a.Inv[split] == 3 && Math.Abs(a.Pend[split].D - 0.3) < 1e-9, "shift: split of 0.7");
        var atoms1 = w.ElementBudget();
        for (int e = 0; e < atoms0.Length; e++) Require(atoms0[e] == atoms1[e], $"shift: element {e} {atoms0[e]:R} -> {atoms1[e]:R}");
        var e1 = w.AuditEnergy();
        double expect = -1.2 * ch.Gap[exc] - 0.7 * ch.SplitEnergy(split);
        Require(Math.Abs((e1.Stock - e0.Stock) - expect) < 1e-6, $"shift: stock changed by {e1.Stock - e0.Stock:R}, the reactions by {expect:R}");
        Console.WriteLine($"PASS matter energy: shift (relax 0.3 and 0.9 of species {exc}, split 0.7 of {split}): atoms exact, Pend in [0, 1), mass and room follow");
    }
}
