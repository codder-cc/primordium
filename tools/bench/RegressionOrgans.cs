using System;
using System.IO;
using System.Linq;

namespace Primordium;

// Organs of sense (P.Organs 1, World.Organs): a body without a photoreceptor reads no light, one with it
// reads light in proportion to its pigment's excitation and amount; readings and organs cost what they
// should and the ledger closes; an organ the genome stops making fades and its reading with it; sight
// needs a decoding apparatus; receptors are as specific as their target; the motor pulls with all its
// copies; kind-3 genes decode by the law and the genome text round-trips under it; a world with organs
// saves and goes on the same. With the law off every reading is what it was and costs nothing.
public sealed partial class World
{
    public static void RunOrganRegression() { ParamRegistry.ResetDefaults(); OrganRegression(); }

    static void OrganRegression()
    {
        try { OrganProbes(); }
        finally { ParamRegistry.ResetDefaults(); }
    }

    static void OrganProbes()
    {
        // ---- genes: a kind-3 gene is a motor with the law off, the transducer its B names with it on ----
        P.Organs = 0;
        byte g1 = (byte)(Enzyme.Motor | 20 << 2);
        Require(Genome.Decode(g1, 4, 5).Kind == Enzyme.Motor, "law off: a kind-3 gene is not a motor");
        P.Organs = 1;
        Require(Genome.Decode(g1, 4, 0).Kind == Enzyme.Motor && Genome.Decode(g1, 4, 3).Kind == Enzyme.Receptor && Genome.Decode(g1, 4, 5).Kind == Enzyme.Photoreceptor
                && Genome.Decode(g1, 4, 6).Kind == Enzyme.Mechano && Genome.Decode(g1, 4, 7 | 3 << 5).Kind == Enzyme.Thermo, "law on: kind-3 genes do not decode by their B");
        // Genome text under the law: random genomes round-trip byte for byte, the organ names parse in both languages.
        var rng = new Random(11);
        for (int k = 0; k < 2000; k++)
        {
            var g = new byte[rng.Next(Genome.MinLen, 200)];
            rng.NextBytes(g);
            for (int i = 0; i + 3 < g.Length; i += 9) { g[i] = Genome.EnzymeOp; g[i + 1] = (byte)(g[i + 1] | 3); }   // many kind-3 genes
            var back = GenomeAsm.Assemble(GenomeAsm.Disassemble(g));
            Require(back.AsSpan().SequenceEqual(g), "law on: genome text round trip changed the bytes");
        }
        foreach (var (en, ru, kind) in new[] { ("receptor 9", "рецептор 9", Enzyme.Receptor), ("photoreceptor 0", "фоторецептор 0", Enzyme.Photoreceptor),
                                               ("mechanoreceptor", "механорецептор", Enzyme.Mechano), ("thermoreceptor t=20", "терморецептор t=20", Enzyme.Thermo) })
        {
            var be = GenomeAsm.Assemble($"enzyme {en} q=0.8\nnop\nnop\nnop\nnop");
            var br = GenomeAsm.Assemble($"enzyme {ru} q=0.8\nnop\nnop\nnop\nnop");
            var e = Genome.Decode(be[1], be[2], be[3]);
            Require(be.AsSpan().SequenceEqual(br) && e.Kind == kind && (kind is Enzyme.Mechano or Enzyme.Thermo || e.A == (kind == Enzyme.Receptor ? 9 : 0)), $"organ gene '{en}' / '{ru}'");
        }
        Require(!GenomeAsm.TryAssemble("enzyme receptor 4 b=5\nnop\nnop\nnop\nnop", out _, out _), "a receptor written with a photoreceptor's B assembled");
        P.Organs = 0;
        foreach (var d in CreatureExamples.All) { var bytes = d.Assemble(); Require(GenomeAsm.Assemble(GenomeAsm.Disassemble(bytes)).AsSpan().SequenceEqual(bytes), $"{d.Name}: law off round trip"); }
        P.Organs = 1;
        foreach (var d in CreatureExamples.All) { var bytes = d.Assemble(); Require(GenomeAsm.Assemble(GenomeAsm.Disassemble(bytes)).AsSpan().SequenceEqual(bytes), $"{d.Name}: law on round trip"); }

        // ---- light: none without a photoreceptor; with one, by its pigment ----
        var w = Blank();
        var ch = w.Chem;
        var pigments = Enumerable.Range(0, Chemistry.S).Where(s => s % 2 == 0 && ch.PhotoUp[s] >= 0).OrderBy(s => ch.Gap[ch.PhotoUp[s]]).ToList();
        Require(pigments.Count >= 2 && ch.Gap[ch.PhotoUp[pigments[0]]] < ch.Gap[ch.PhotoUp[pigments[^1]]], "no two pigments of different excitation");
        int weak = pigments[0], strong = pigments[^1];
        int cell = 20 * w.W + 20;
        var blind = w.TestAgent(cell, w.Height[cell], weak, 20);
        w.Light[cell] = 0.8f;
        P.SenseNoise = 0;
        double e0 = blind.Energy;
        Require(w.LightAt(blind) == 0, "a body without a photoreceptor reads light");
        Require(Math.Abs(e0 - blind.Energy - P.SenseTry) < 1e-9, $"trying to see without an organ cost {e0 - blind.Energy:R}, not SenseTry");
        // A photoreceptor without its pigment (the body had none when it was folded) sees nothing.
        var noPigment = w.TestAgent(cell + 2, w.Height[cell + 2], weak, 20);
        w.Light[cell + 2] = 0.8f;
        w.MakeProtein(noPigment, new Enzyme { Kind = Enzyme.Photoreceptor, A = (byte)strong, Eff = 1, Topt = 15 }, -1);
        Require(noPigment.EnzN == 1 && noPigment.Enz[0].Material != strong && w.LightAt(noPigment) == 0, "a photoreceptor without its pigment sees");
        // Two eyes of the same amount with pigments of different excitation: readings in proportion to it.
        var eyeW = w.TestAgent(cell + 4, w.Height[cell + 4], weak, 20);
        var eyeS = w.TestAgent(cell + 6, w.Height[cell + 6], strong, 20);
        w.Light[cell + 4] = w.Light[cell + 6] = 0.8f;
        w.MakeProtein(eyeW, new Enzyme { Kind = Enzyme.Photoreceptor, A = (byte)weak, Eff = 1, Topt = 15 }, -1);
        w.MakeProtein(eyeS, new Enzyme { Kind = Enzyme.Photoreceptor, A = (byte)strong, Eff = 1, Topt = 15 }, -1);
        foreach (var x in new[] { eyeW, eyeS }) { x.Enz[0].Amount = 0.4f; }
        Require(eyeW.Enz[0].Material == weak && eyeS.Enz[0].Material == strong, "a photoreceptor did not fold its pigment");
        float gW = w.PigmentStrength(eyeW), gS = w.PigmentStrength(eyeS);
        e0 = eyeS.Energy;
        float lW = w.LightAt(eyeW), lS = w.LightAt(eyeS);
        Require(Math.Abs(e0 - eyeS.Energy - (P.SenseTry + P.SenseDrive * gS)) < 1e-9, "a reading's cost is not SenseTry + SenseDrive × strength");
        float want = w.Absorb(weak) / w.Absorb(strong);
        Require(lS > 0 && Math.Abs(lW / lS - want) < 1e-4 && Math.Abs(lS - 0.8f * Math.Min(1, gS)) < 1e-5, $"light read {lW:0.###} / {lS:0.###}, pigments' excitation ratio {want:0.###}");
        // More copies: more signal, up to the light itself.
        eyeS.Enz[0].Amount = 8;
        Require(Math.Abs(w.LightAt(eyeS) - 0.8f) < 1e-5, "a strong eye does not read the light itself");
        // Noise falls with the organ's strength (the body's random stream).
        P.SenseNoise = 0.5f;
        double Spread(Agent x) { double s = 0, s2 = 0; for (int k = 0; k < 400; k++) { double v = w.LightAt(x); s += v; s2 += v * v; } double m = s / 400; return Math.Sqrt(Math.Max(0, s2 / 400 - m * m)) / m; }
        eyeS.Enz[0].Amount = 1; double spread1 = Spread(eyeS);
        eyeS.Enz[0].Amount = 8; double spread8 = Spread(eyeS);
        Require(spread8 < spread1 * 0.75 && spread1 > 0.05, $"noise did not fall with the organ: {spread1:0.###} → {spread8:0.###}");
        P.SenseNoise = 0;

        // ---- sight needs a decoding apparatus ----
        e0 = eyeW.Energy;
        eyeW.Enz[0].Amount = 0.5f;
        var seen = w.Look(eyeW, cell + 4, 0, 16);
        Require(seen.What == 0 && seen.Dist == 0 && Math.Abs(e0 - eyeW.Energy - P.SenseTry) < 1e-9, "a weak eye saw, or paid more than trying");
        eyeS.Enz[0].Amount = 20;
        e0 = eyeS.Energy;
        float gLook = w.PigmentStrength(eyeS);
        int range = Math.Clamp((int)(gLook * P.LookPerUnit), 1, 16);
        w.Look(eyeS, cell + 6, 0, 16);
        Require(gLook >= P.LookMin && Math.Abs(e0 - eyeS.Energy - (P.SenseTry + P.SenseDrive * gLook + P.CostLook * range)) < 1e-6, "sight's cost is not try + drive + range");

        // ---- receptors: no smell without one, specific to their target ----
        var nose = w.TestAgent(cell + 8, w.Height[cell + 8], weak, 20);
        int food = Enumerable.Range(0, Chemistry.S).First(s => s % 2 == 0 && ch.AtomCount(s) >= 2);
        int alien = Enumerable.Range(0, Chemistry.S).First(s => w.Affinity(food, s) == 0);
        w.C[food][cell + 8] += 10;
        Require(w.OutsideAmount(nose, cell + 8, food) == 0 && w.Gradient(nose, cell + 8, food) == 4, "a body without a receptor smells");
        w.MakeProtein(nose, new Enzyme { Kind = Enzyme.Receptor, A = (byte)food, Eff = 1, Topt = 15 }, -1);
        nose.Enz[0].Amount = 4;
        float smelled = w.OutsideAmount(nose, cell + 8, food), truth = w.LooseAmount(nose, cell + 8, food);
        Require(Math.Abs(smelled - truth) < 1e-3 && w.OutsideAmount(nose, cell + 8, alien) == 0, $"a receptor read {smelled} of {truth}, or a molecule it does not bind");
        w.C[food][w.nb[(cell + 8) * 4 + 1]] += 40;
        Require(w.Gradient(nose, cell + 8, food) == 1, "a receptor does not smell the way to more");

        // ---- temperature, touch ----
        var skin = w.TestAgent(cell + 10, w.Height[cell + 10], weak, 20);
        Require(w.AmbientTemp(skin, cell + 10) == 0 && w.ContactCount(skin) == 0 && w.Alarm(skin, cell + 10) == -1, "a body without thermo- or mechanoreceptors feels");
        w.MakeProtein(skin, new Enzyme { Kind = Enzyme.Thermo, Eff = 1, Topt = w.Temp[cell + 10] }, -1);
        w.MakeProtein(skin, new Enzyme { Kind = Enzyme.Mechano, Eff = 1, Topt = 15 }, -1);
        foreach (int k in new[] { 0, 1 }) skin.Enz[k].Amount = 3;
        float tr = w.AmbientTemp(skin, cell + 10), tt = CaveLaw ? w.LocalTemp(cell + 10, skin.Z) : w.Temp[cell + 10];
        var friend = w.TestAgent(cell + 10, w.Height[cell + 10], weak, 20);
        Require(Math.Abs(tr - tt) < 1e-4 && w.ContactCount(skin) == w.Candidates(skin) && w.Candidates(skin) >= 1, $"a thermoreceptor read {tr} of {tt}, or touch missed a neighbour");

        // ---- the motor: all copies pull, by the body's mass ----
        var mover = w.TestAgent(cell + 30 * w.W, w.Height[cell + 30 * w.W], weak, 20);
        w.MakeProtein(mover, new Enzyme { Kind = Enzyme.Motor, Eff = 1, Topt = 15 }, -1);
        w.MakeProtein(mover, new Enzyme { Kind = Enzyme.Motor, Eff = 1, Topt = 25 }, -1);   // a second copy (another best temperature)
        float need = P.MotorLoad * (1 + mover.Mass);
        mover.Enz[0].Amount = mover.Enz[1].Amount = 0.3f * need;
        float strength = w.OrganStrength(mover, Enzyme.Motor), share = Math.Min(1, strength / (need + 1e-6f));
        mover.Vx = mover.Vy = 0;
        w.Motor(mover, -1, 0);
        float dv = MathF.Abs(mover.Vx) + MathF.Abs(mover.Vy);
        Require(share < 1 && share > 0.1f && Math.Abs(dv - 1.05f * share) < 1e-4, $"a weak motor pushed {dv:0.####}, its copies give {1.05f * share:0.####}");

        // ---- upkeep is booked, an organ unmade fades ----
        var keep = w.TestAgent(cell + 40 * w.W, w.Height[cell + 40 * w.W], strong, 30);
        w.Light[cell + 40 * w.W] = 0.8f;
        for (int k = 0; k < 10; k++) w.MakeProtein(keep, new Enzyme { Kind = Enzyme.Photoreceptor, A = (byte)strong, Eff = 1, Topt = 15 }, -1);
        // Its twin next door, the same in everything, lives the same tick with the upkeep law at 0.
        var twin = w.TestAgent(cell + 40 * w.W + 1, w.Height[cell + 40 * w.W + 1], strong, 30);
        w.Light[cell + 40 * w.W + 1] = 0.8f;
        for (int k = 0; k < 10; k++) w.MakeProtein(twin, new Enzyme { Kind = Enzyme.Photoreceptor, A = (byte)strong, Eff = 1, Topt = 15 }, -1);
        float amount0 = keep.Enz[0].Amount, read0 = w.LightAt(keep);
        w.LightAt(twin);
        double k0 = keep.Energy, t0 = twin.Energy;
        w.Live(keep);
        float upkeepLaw = P.OrganUpkeep;
        P.OrganUpkeep = 0;
        w.Live(twin);
        P.OrganUpkeep = upkeepLaw;
        double upkeep = (k0 - keep.Energy) - (t0 - twin.Energy);
        var start = w.EnergyStart();
        for (int t = 0; t < 600; t++) w.Step();
        Require(!keep.Dead && keep.Enz[0].Amount < amount0 * 0.4f && w.LightAt(keep) < read0, $"an unmade photoreceptor did not fade: {amount0} → {keep.Enz[0].Amount}");
        w.EnergyBalanced(start, "organs living");

        // ---- the law off: readings as before, free ----
        P.Organs = 0;
        e0 = blind.Energy;
        Require(w.LightAt(blind) == w.AgentLight(blind) && w.ContactCount(blind) == w.Candidates(blind) && blind.Energy == e0, "law off: readings changed or cost");

        // ---- a world with organs saves and goes on the same ----
        P.Organs = 1;
        var a = Tiny(4, -1, true, true);
        foreach (var d in CreatureExamples.All)
        {
            int at = Enumerable.Range(0, a.N).First(c => !a.Submerged(c) && a.Count[c] == 0 && Math.Abs(a.Temp[c] - 15) < 10);
            a.SpawnDesign(d, at % a.W, at / a.W, new SpawnOptions { Matter = MatterSource.Import, Energy = EnergySource.Import, Count = 3, Radius = 2 });
        }
        for (int t = 0; t < 300; t++) a.Step();
        int organs = a.Agents.Where(x => !x.Dead).Sum(x => Enumerable.Range(0, x.EnzN).Count(k => x.Enz[k].Kind > Enzyme.Motor));
        string path = Path.Combine(TestDir(), "organs.sav");
        a.Save(path, "organs");
        P.Organs = 0;
        var b = Load(path);
        Require(P.Organs == 1 && b.DeepHash() == a.DeepHash(), "a world with organs did not load the same (or its law)");
        for (int t = 0; t < 200; t++) { a.Step(); b.Step(); }
        Require(a.DeepHash() == b.DeepHash(), "a loaded world with organs diverged");
        File.Delete(path);
        // Two worlds of one seed with the law on step the same (organs read the body's own random stream).
        var d1 = Tiny(6, -1, true, true); var d2 = Tiny(6, -1, true, true);
        for (int t = 0; t < 150; t++) { d1.Step(); d2.Step(); }
        Require(d1.DeepHash() == d2.DeepHash() && d1.Agents.Count > 0, "law on: two worlds of one seed differ");
        // The law switched off and on mid-run: organs left over stay as proteins and fade; atoms and energy close.
        var atoms0 = d1.ElementBudget();
        var switched = d1.EnergyStart();
        foreach (int law in new[] { 0, 1, 0, 1 })
        {
            d1.SetParam("Organs", law);
            for (int t = 0; t < 100; t++) d1.Step();
        }
        d1.EnergyBalanced(switched, "organs switched mid-run");
        var atoms1 = d1.ElementBudget();
        for (int e = 0; e < atoms1.Length; e++) Require(Math.Abs(atoms1[e] - d1.InteriorInput[e] - d1.HandInput[e] - atoms0[e]) < 1e-6, $"organs switched mid-run: element {e} drifted");
        var census = a.OrganCensus();
        Console.WriteLine($"PASS organs: kind-3 genes by the law, text round trip in both languages; no light without a photoreceptor, light by the pigment's excitation ({lW:0.###}/{lS:0.###} = {want:0.###}), noise {spread1:0.##} → {spread8:0.##} with 8× the organ; sight needs {P.LookMin} of it; receptors specific; touch, temperature; a motor of share {share:0.##} pushes {dv:0.###}; upkeep {upkeep:0.####} for {amount0:0.#} units booked, the organ fades {amount0:0.#} → {keep.Enz[0].Amount:0.##}; ledger closed; save/load with {organs} organ proteins in {a.Agents.Count} bodies (census: {census[0]:0} bodies, {census[OrganNames.Length - 3]:P0} of readings with an organ)");
    }
}
