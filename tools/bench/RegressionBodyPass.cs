using System;
using System.IO;
using System.Linq;

namespace Primordium;

// The body pass (P.BodyPass 1, World.BodyPass): the same world whether its chunks run in parallel or one after
// another (nothing depends on threads), a world saved and loaded mid-run goes on the same way, atoms and the
// energy ledger close, body mass and the cell lists stay sound. With the law off the pass never runs (the
// trajectory of law 0 is checked by the hashes of the default world).
public sealed partial class World
{
    public static void RunBodyPassRegression() { ParamRegistry.ResetDefaults(); BodyPassRegression(); }

    static void BodyPassRegression()
    {
        try { BodyPassProbes(); }
        finally { ParamRegistry.ResetDefaults(); BodyPassSerial = false; }
    }

    static void BodyPassProbes()
    {
        P.BodyPass = 1;
        var a = new World(1) { TrackHeat = true };
        var b = new World(1);
        var before = a.ElementBudget();
        var energy = a.AuditEnergy();
        for (int t = 0; t < 300; t++)
        {
            a.Step();
            BodyPassSerial = true; b.Step(); BodyPassSerial = false;
        }
        Require(a.Agents.Count > 500, $"body pass: the crowded world died out ({a.Agents.Count} bodies)");
        Require(a.DeepHash() == b.DeepHash(), "body pass: parallel and one-by-one chunks gave different worlds");
        // Save, load, go on: the loaded world follows the original.
        string path = Path.Combine(TestDir(), "bodypass.sav");
        a.Save(path, "self-test");
        ParamRegistry.ResetDefaults();
        var c = Load(path);
        Require(P.BodyPass == 1, "body pass: the law was not restored by the load");
        for (int t = 0; t < 200; t++) { a.Step(); c.Step(); }
        Require(a.DeepHash() == c.DeepHash(), "body pass: the loaded world diverged");
        File.Delete(path);
        var after = a.ElementBudget();
        for (int e = 0; e < after.Length; e++) after[e] -= a.InteriorInput[e] + a.HandInput[e];
        BudgetEqual(before, after, "body pass, 500 ticks", 0.25);
        string energyNote = EnergyWorldCheck(a, energy, "body pass, 500 ticks");
        foreach (var x in a.Agents)
        {
            double mass = 0;
            for (int s = 0; s < Chemistry.S; s++) mass += (x.Inv[s] + x.Pend[s]) * a.Chem.Mass[s];
            for (int k = 0; k < x.EnzN; k++) mass += x.Enz[k].Matter * a.Chem.Mass[x.Enz[k].Material];
            Require(Math.Abs(x.Mass - mass) < 0.005 + 1e-5 * mass, $"body pass: body mass drift #{x.Id}: {x.Mass} vs {mass}");
            Require(x.Inv.All(n => n >= 0), "body pass: negative body inventory");
        }
        Require(a.C.All(q => q.All(v => v >= 0)), "body pass: negative loose amount");
        a.CheckCellLists();
        Console.WriteLine($"PASS body pass: parallel = one by one ({a.Agents.Count} bodies at tick {a.Tick}), save/load continues identically, {energyNote}");
    }
}
