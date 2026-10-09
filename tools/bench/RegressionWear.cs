using System;
using System.Linq;

namespace Primordium;

public sealed partial class World
{
    // Wear of body matter (World.Wear):
    // - photodamage: a caught photon breaks the excited molecule with the chance of the formula (its
    //   cohesion and the body's matrix against the excitation energy); a monomer relaxes and leaves, a
    //   composite leaves as its fragments; atoms exact, the heat is the `body decay` output, the photon
    //   the `photo` input, the ledger closes;
    // - wear: every held molecule may leave (by its exothermic breakdown, or whole), weakly bound ones
    //   first, faster when warm; atoms exact, the ledger closes;
    // - a sealed body that never takes anything in, kept alive on light (or just living), loses matter
    //   with the law on and keeps every atom with it off;
    // - both laws switched on in a living world mid-run: atoms exact, energy closes, molecules leave.
    public static void WearRegression()
    {
        ParamRegistry.ResetDefaults();
        var w = Fixture(); var ch = w.Chem;
        int c = 80 * w.W + 120;
        w.EnergyStart();
        // A monomer (relaxes, then leaves) and a composite whose excited state breaks into two fragments.
        int mono = Enumerable.Range(0, Chemistry.S).First(s => s % 2 == 0 && ch.AtomCount(s) == 1 && ch.SplitEnergy(s + 1) > 0);
        int comp = Enumerable.Range(0, Chemistry.S).First(s => s % 2 == 0 && ch.SplitB[s + 1] >= 0 && ch.SplitEnergy(s + 1) > 0);

        // The chance follows the molecule's own hold against its excitation energy.
        P.PhotoDamage = 0.5f; P.PhotoHold = 10; P.PhotoCage = 1;
        var probe = w.TestAgent(c, 2, mono, 10);
        float hold = ch.Bond[mono + 1] + Math.Min(1f, 10f / probe.Room) * ch.Bond[mono];
        float want = 0.5f * MathF.Exp(-10 * hold / (ch.E[mono + 1] - ch.E[mono]));
        float got = w.PhotoDamageChance(probe, mono, mono + 1);
        Require(MathF.Abs(got - want) < 1e-6f, $"photodamage chance {got} (want {want})");
        P.PhotoCage = 0;
        float bare = w.PhotoDamageChance(probe, mono, mono + 1);
        Require(bare > got, "a matrix did not hold the molecule better");
        int strong = Enumerable.Range(0, Chemistry.S).Where(s => s % 2 == 0).OrderByDescending(s => ch.Bond[s + 1] / (ch.E[s + 1] - ch.E[s])).First();
        Require(w.PhotoDamageChance(probe, strong, strong + 1) <= bare, "the most firmly held molecule breaks more easily");
        w.Die(probe, c, CauseHand);

        // Every caught photon breaks its molecule (chance 1).
        P.PhotoDamage = 1; P.PhotoHold = 0; P.PhotoCage = 0;
        string Photolysis(int ground, int count)
        {
            var a = w.TestAgent(c, 2, ground, count);
            a.Enz[0] = new Enzyme { Kind = Enzyme.Photo, A = (byte)ground, Amount = 3, Eff = 1, Topt = 15 }; a.EnzN = 1;
            int x = ch.SplitA[ground + 1], y = ch.SplitB[ground + 1];
            double lx = w.C[x][c], ly = y >= 0 ? w.C[y][c].D : 0;
            w.Photon[c] = 3;
            var atoms = w.ElementBudget(); var before = w.AuditEnergy();
            long lost0 = w.LostPhoto;
            w.Photo(a, c, 0, ground);
            int caught = a.NPhoto;
            Require(caught > 0, "probe photodamage caught nothing");
            Require(a.InvTotal == count - caught && a.Inv[ground + 1] == 0 && w.LostPhoto - lost0 == caught, $"photodamage: {caught} caught, body {a.InvTotal} of {count}");
            Require(Math.Abs(w.C[x][c] - lx - caught * (x == y ? 2 : 1)) < 1e-9 && (y < 0 || x == y || Math.Abs(w.C[y][c] - ly - caught) < 1e-9), "photodamage: the products are not on the floor");
            BudgetEqual(atoms, w.ElementBudget(), "photodamage", 0);
            w.EnergyBalanced(before, "photodamage", FPhoto, FBodyDecay);
            w.Die(a, c, CauseHand);
            return $"{ch.Formula(ground)}* ×{caught} → {ch.Formula(x)}{(y >= 0 ? " + " + ch.Formula(y) : "")}";
        }
        string monoNote = Photolysis(mono, 12), compNote = Photolysis(comp, 12);

        // Wear: weakly bound first, faster when warm; atoms exact, the ledger closes.
        P.PhotoDamage = 0; P.WearK = 0.002f; P.WearHold = 3;
        int weak = Enumerable.Range(0, Chemistry.S).OrderBy(s => ch.Bond[s]).First();
        int firm = Enumerable.Range(0, Chemistry.S).OrderByDescending(s => ch.Bond[s]).First();
        var body = w.TestAgent(c + 2, 2, weak, 100);
        for (int k = 0; k < 100; k++) w.AddMol(body, firm);
        var atoms0 = w.ElementBudget(); var before0 = w.AuditEnergy();
        long worn0 = w.LostWear;
        for (int t = 0; t < 400 && !body.Dead; t++) w.Live(body);
        Require(!body.Dead && w.LostWear > worn0, "wear: nothing left the body");
        Require(100 - body.Inv[weak] > 20 * (100 - body.Inv[firm]), $"wear: weak lost {100 - body.Inv[weak]}, firm {100 - body.Inv[firm]}");
        BudgetEqual(atoms0, w.ElementBudget(), "wear", 0);
        w.EnergyBalanced(before0, "wear", FBodyDecay, FDissipate);
        double Worn(float tb)
        {
            var b = w.TestAgent(c + 4, 2, weak, 200);
            long l0 = w.LostWear;
            for (int t = 0; t < 300; t++) { b.Tb = tb; w.Wear(b, c + 4); }
            w.Die(b, c + 4, CauseHand);
            return w.LostWear - l0;
        }
        double cold = Worn(-5), warm = Worn(40);
        Require(warm > 3 * cold, $"wear: warm {warm} vs cold {cold}");

        // A sealed body: it never takes anything in. With a law on it loses matter; with both off it keeps every atom.
        int BodyAtoms(Agent a) { int n = 0; for (int s = 0; s < Chemistry.S; s++) n += a.Inv[s] * ch.AtomCount(s); return n; }
        (int start, int end) Sealed(float photo, float wear)
        {
            P.PhotoDamage = photo; P.PhotoHold = 10; P.PhotoCage = 1; P.WearK = wear; P.WearHold = 3;
            int cell = c + 6 * w.W;
            var a = w.TestAgent(cell, 2, mono, 30);
            a.Enz[0] = new Enzyme { Kind = Enzyme.Photo, A = (byte)mono, Amount = 3, Eff = 1, Topt = 15 }; a.EnzN = 1;
            int n0 = BodyAtoms(a);
            for (int t = 0; t < 3000 && !a.Dead; t++)
            {
                w.Photon[cell] = 1;
                w.Photo(a, cell, 0, mono);
                if (a.Inv[mono + 1] > 0) w.SplitMol(a, 0, mono + 1);   // the battery: relax and keep the energy
                w.Live(a);
            }
            int n1 = a.Dead ? 0 : BodyAtoms(a);
            if (!a.Dead) w.Die(a, cell, CauseHand);
            return (n0, n1);
        }
        var off = Sealed(0, 0);
        Require(off.end == off.start, $"sealed body with both laws off: {off.start} → {off.end} atoms");
        var photoOn = Sealed(0.3f, 0);
        Require(photoOn.end < photoOn.start, $"sealed body with photodamage: {photoOn.start} → {photoOn.end} atoms");
        var wearOn = Sealed(0, 0.0005f);
        Require(wearOn.end < wearOn.start, $"sealed body with wear: {wearOn.start} → {wearOn.end} atoms");

        // A living world: both laws switched on mid-run.
        ParamRegistry.ResetDefaults();
        var v = new World(2, 800, true) { TrackHeat = true };
        var vAtoms = v.ElementBudget(); var vE = v.AuditEnergy();
        string note = "";
        for (int t = 1; t <= 900; t++)
        {
            if (t == 100) Require(v.SetParam("PhotoDamage", 0.5), "law PhotoDamage");
            if (t == 300) Require(v.SetParam("WearK", 0.0005), "law WearK");
            v.Step();
            if (t % 300 != 0) continue;
            var atoms = v.ElementBudget();
            for (int e = 0; e < atoms.Length; e++)
            {
                double d = atoms[e] - v.InteriorInput[e] - v.HandInput[e] - vAtoms[e];
                Require(Math.Abs(d) <= 1e-6, $"wear laws, tick {t}: element {e} drifted by {d}");
            }
            note = EnergyWorldCheck(v, vE, $"wear laws, tick {t}");
        }
        Require(v.LostPhoto > 0 && v.LostWear > 0, $"wear laws in a living world: photodamage {v.LostPhoto}, wear {v.LostWear}");
        // Saved with the laws on, the world continues identically (the laws travel with the save).
        var ms = new System.IO.MemoryStream();
        v.Save(ms);
        ParamRegistry.ResetDefaults();
        ms.Position = 0;
        var loaded = Load(ms);
        Require(P.PhotoDamage == 0.5f && P.WearK == 0.0005f, "the wear laws did not come back with the save");
        for (int t = 0; t < 200; t++) { v.Step(); loaded.Step(); }
        Require(v.StateHash() == loaded.StateHash(), "wear laws: the loaded world went another way");
        ParamRegistry.ResetDefaults();
        Console.WriteLine($"PASS photodamage: chance {want:F3} (matrix) / {bare:F3} (bare); {monoNote}, {compNote}: atoms exact, photo in, body decay out");
        Console.WriteLine($"PASS wear: weak molecules first ({100 - body.Inv[weak]} vs {100 - body.Inv[firm]} of 100), warm {warm} vs cold {cold}; sealed body atoms off {off.start}→{off.end}, photodamage {photoOn.start}→{photoOn.end}, wear {wearOn.start}→{wearOn.end}; living world: {v.LostPhoto} photolysed, {v.LostWear} worn, atoms exact, {note}; save/load with the laws on continues identically");
    }
}
