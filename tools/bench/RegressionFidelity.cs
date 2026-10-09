using System;
using System.Linq;

namespace Primordium;

// Paid copy fidelity (P.PaidFidelity 1, World.Fidelity): a ProofB motor gene makes a proofreader only with
// the law; the genome text names it in both languages and round-trips; a body's proofreader lowers its copy
// errors by the predicted factor e^(−FidelityDE·(T_ref/T)·g) — point changes, insertions and deletions, as
// counted on thousands of copies — and a warm body errs more (MutPoint^(T_ref/T − 1)); the cost FidelityCost
// × g × bytes is booked at division and mating (and the body must afford it); with the law off a body with
// the same protein copies and pays as before.
public sealed partial class World
{
    public static void RunFidelityRegression() { ParamRegistry.ResetDefaults(); FidelityRegression(); }

    static void FidelityRegression()
    {
        try { FidelityProbes(); }
        finally { ParamRegistry.ResetDefaults(); }
    }

    static void FidelityProbes()
    {
        // ---- genes ----
        byte g1 = (byte)(Enzyme.Motor | 20 << 2);
        foreach (int organs in new[] { 0, 1 })
        {
            P.Organs = organs;
            P.PaidFidelity = 0;
            Require(Genome.Decode(g1, 4, Enzyme.ProofB).Kind == Enzyme.Motor && Genome.Decode(g1, 4, Enzyme.ProofB | 3 << 5).Kind == Enzyme.Motor, $"law off (Organs {organs}): a ProofB gene is not a motor");
            P.PaidFidelity = 1;
            Require(Genome.Decode(g1, 4, Enzyme.ProofB).Kind == Enzyme.Proofread && Genome.Decode(g1, 4, Enzyme.ProofB | 1 << 5).Kind == Enzyme.Proofread
                    && Genome.Decode(g1, 4, 0).Kind == Enzyme.Motor && Genome.Decode(g1, 4, 3).Kind == (organs != 0 ? Enzyme.Receptor : Enzyme.Motor),
                    $"law on (Organs {organs}): kind-3 genes do not decode by their B");
            var be = GenomeAsm.Assemble("enzyme proofreader t=20 q=0.8\nnop\nnop\nnop\nnop");
            var br = GenomeAsm.Assemble("enzyme корректор t=20 q=0.8\nnop\nnop\nnop\nnop");
            var e = Genome.Decode(be[1], be[2], be[3]);
            Require(be.AsSpan().SequenceEqual(br) && e.Kind == Enzyme.Proofread && Math.Abs(e.Topt - 20) < 0.5f, "proofreader gene in English / Russian");
            Require(!GenomeAsm.TryAssemble("enzyme proofreader b=3\nnop\nnop\nnop\nnop", out _, out _), "a proofreader written with a receptor's B assembled");
            var rng = new Random(13);
            for (int k = 0; k < 1000; k++)
            {
                var g = new byte[rng.Next(Genome.MinLen, 200)];
                rng.NextBytes(g);
                for (int i = 0; i + 3 < g.Length; i += 9) { g[i] = Genome.EnzymeOp; g[i + 1] = (byte)(g[i + 1] | 3); }   // many kind-3 genes
                Require(GenomeAsm.Assemble(GenomeAsm.Disassemble(g)).AsSpan().SequenceEqual(g), $"law on (Organs {organs}): genome text round trip changed the bytes");
            }
            foreach (var d in CreatureExamples.All) { var bytes = d.Assemble(); Require(GenomeAsm.Assemble(GenomeAsm.Disassemble(bytes)).AsSpan().SequenceEqual(bytes), $"{d.Name}: round trip with the proofreader law"); }
        }
        P.Organs = 0;
        P.PaidFidelity = 0;
        // Disassembled with the law off, the same gene is a motor (and back to the same bytes).
        var off = GenomeAsm.Assemble("enzyme proofreader q=0.8\nnop\nnop\nnop\nnop");
        Require(GenomeAsm.Disassemble(off).Contains("motor") && GenomeAsm.Assemble(GenomeAsm.Disassemble(off)).AsSpan().SequenceEqual(off), "law off: a proofreader gene is not shown as a motor");

        // ---- the predicted factor, counted on copies ----
        P.PaidFidelity = 1;
        var w = Blank();
        int cell = 20 * w.W + 20;
        var a = w.TestAgent(cell, w.Height[cell], 0, 40);
        var genome = new byte[400];
        new Random(5).NextBytes(genome);
        a.SetGenome(genome, new byte[genome.Length]);
        Require(Math.Abs(w.CopyErrorScale(a, out double none) - 1) < 1e-12 && none == 0, "a body at TempRef without a proofreader does not copy at the base rate");
        w.MakeProtein(a, new Enzyme { Kind = Enzyme.Proofread, Eff = 0.8f, Topt = 15 }, -1);
        a.Enz[0].Amount = 3;
        double passes0 = 3 * 0.8f;
        double scale = w.CopyErrorScale(a, out double passes), want = Math.Exp(-P.Dec(P.FidelityDE) * passes0);
        Require(Math.Abs(passes - passes0) < 1e-5 && Math.Abs(scale - want) < 1e-6, $"proofreader at TempRef: {passes:0.###} passes, scale {scale:0.####} (want {want:0.####})");
        // Point changes only (no insertions, deletions, duplications): changed bytes per copy.
        float ins = P.MutInsert, del = P.MutDelete, delMax = P.MutDeleteMax, dup = P.MutDup;
        P.MutInsert = P.MutDelete = P.MutDeleteMax = P.MutDup = 0;
        double Points(double s)
        {
            long changed = 0;
            for (int k = 0; k < 3000; k++)
            {
                var (c, _) = Genome.Mutate(genome, a.Prot, new SimRng(k + 1), s);
                for (int i = 0; i < c.Length; i++) if (c[i] != genome[i]) changed++;
            }
            return changed / 3000.0;
        }
        double basePoints = Points(1), proofed = Points(scale), expect = genome.Length * P.Dec(P.MutPoint) * 255 / 256.0;
        Require(Math.Abs(basePoints / expect - 1) < 0.04, $"base copy: {basePoints:0.###} changed bytes per copy, expected {expect:0.###}");
        Require(Math.Abs(proofed / basePoints / scale - 1) < 0.06, $"proofread copy: {proofed:0.###} vs {basePoints:0.###} changed bytes, ratio {proofed / basePoints:0.###}, predicted {scale:0.###}");
        // Insertions and deletions fall by the same factor; duplications do not (a slipped strand).
        double Share(double s, int delta)
        {
            int n = 0;
            for (int k = 0; k < 4000; k++) if (Genome.Mutate(genome, a.Prot, new SimRng(k + 1), s).g.Length == genome.Length + delta) n++;
            return n / 4000.0;
        }
        P.MutPoint = 0; P.MutInsert = 0.5f;
        double up1 = Share(1, 1), upS = Share(scale, 1);
        P.MutInsert = 0; P.MutDelete = 0.4f;
        double down1 = Share(1, -1), downS = Share(scale, -1);
        P.MutDelete = 0; P.MutDup = 0.3f; P.MutDupMin = 2; P.MutDupMax = 2;
        double dup1 = Share(1, 2), dupS = Share(scale, 2);
        Require(Math.Abs(up1 - 0.5) < 0.03 && Math.Abs(down1 - 0.4) < 0.03 && Math.Abs(dup1 - 0.3) < 0.03, $"base copy: insertion {up1:0.###}, deletion {down1:0.###}, duplication {dup1:0.###}");
        Require(Math.Abs(upS / up1 / scale - 1) < 0.15 && Math.Abs(downS / down1 / scale - 1) < 0.15 && Math.Abs(dupS / dup1 - 1) < 0.1,
            $"with the proofreader: insertion {upS:0.###}, deletion {downS:0.###} (predicted ×{scale:0.###}), duplication {dupS:0.###} (unchanged)");
        P.MutPoint = 0.008f; P.MutInsert = ins; P.MutDelete = del; P.MutDeleteMax = delMax; P.MutDup = dup; P.MutDupMin = 2; P.MutDupMax = 12;
        // Temperature: a warm body errs more and proofreads less well; the proofreader's own window too.
        a.Tb = 40;
        double thermal = Math.Exp(Math.Log(P.Dec(P.MutPoint)) * ((273.15 + P.TempRef) / (273.15 + 40) - 1));
        float window = MathF.Exp(-MathF.Pow((40 - 15) / P.EnzWidth, 2));
        double hot = w.CopyErrorScale(a, out double hotPasses);
        double wantHot = thermal * Math.Exp(-P.Dec(P.FidelityDE) * (273.15 + P.TempRef) / (273.15 + 40) * passes0 * window);
        Require(thermal > 1.3 && Math.Abs(hotPasses - passes0 * window) < 1e-4 && Math.Abs(hot / wantHot - 1) < 1e-5 && hot > scale, $"warm body: thermal ×{thermal:0.###}, scale {hot:0.####} (want {wantHot:0.####}), at 15 °C {scale:0.####}");
        a.Enz[0].Amount = 0;
        double bare = w.CopyErrorScale(a, out _);
        P.MutInsert = P.MutDelete = P.MutDeleteMax = P.MutDup = 0;
        double warmPoints = Points(bare);
        Require(Math.Abs(warmPoints / basePoints / thermal - 1) < 0.05, $"warm copy: {warmPoints:0.###} vs {basePoints:0.###} changed bytes at 15 °C, predicted ×{thermal:0.###}");
        Require(ThermalErrorFactor(0) < 1 && ThermalErrorFactor(15) == 1, "a cold body does not copy better");
        P.MutInsert = ins; P.MutDelete = del; P.MutDeleteMax = delMax; P.MutDup = dup;
        a.Tb = 15;
        a.Enz[0].Amount = 3;

        // ---- the cost: booked at division, and the body must afford it ----
        w.FidelityCensus();   // reset the counters
        double cost = P.DivCostBase + P.DivCostByte * genome.Length, proof = ProofCost(passes0, genome.Length);
        Require(Math.Abs(proof - P.Dec(P.FidelityCost) * passes0 * genome.Length) < 1e-6 && proof > 10, $"proofreading cost {proof:0.###}");
        a.Energy = 200;
        int kids = a.NChildren;
        w.Divide(a, cell, 0, 4);
        Require(a.NChildren == kids + 1, "the proofreading body did not divide");
        double left = (200 - cost - proof) * 0.5;
        Require(Math.Abs(a.Energy - left) < 1e-4, $"division with the proofreader left {a.Energy:0.####}, expected {left:0.####} (cost {cost:0.##} + proofreading {proof:0.##})");
        var census = w.FidelityCensus();
        double Col(string n) => census[Array.IndexOf(FidelityNames, n)];
        Require(Col("fid_copies") == 1 && Math.Abs(Col("fid_energy") - proof) < 1e-6 && Math.Abs(Col("fid_passes") - passes0) < 1e-5 && Math.Abs(Col("fid_error_scale") - scale) < 1e-6,
            $"census after one copy: {string.Join(" ", FidelityNames.Zip(census, (n, v) => $"{n}={v:0.###}"))}");
        // Too poor for the proofreading: no division.
        a.Energy = P.DivMinEnergy + cost + proof * 0.5;
        a.Enz[0].Amount = 3;
        long noEnergy = w.DivFail[1];
        w.Divide(a, cell, 0, 4);
        Require(w.DivFail[1] == noEnergy + 1, "a body divided that could not pay its proofreading");
        // Mating: the one who mates copies the joined genome and pays.
        var b = w.TestAgent(cell, w.Height[cell], 0, 40);
        b.SetGenome((byte[])genome.Clone(), new byte[genome.Length]);
        a.Energy = 200; b.Energy = 200;
        a.Target = b;
        w.Mate(b, cell);
        double mateBefore = a.Energy;
        w.Mate(a, cell);
        double mated = (mateBefore - P.CostSocial - proof) * (1 - P.MateShare);
        Require(Math.Abs(a.Energy - mated) < 1e-3, $"mating with the proofreader left {a.Energy:0.####}, expected {mated:0.####}");
        census = w.FidelityCensus();
        Require(Col("fid_copies") == 1 && Math.Abs(Col("fid_energy") - proof) < 1e-6, "mating: proofreading not counted");

        // ---- law off: the same protein changes nothing ----
        P.PaidFidelity = 0;
        a.Energy = 200;
        a.Enz[0].Amount = 3;
        w.Divide(a, cell, 0, 4);
        Require(Math.Abs(a.Energy - (200 - cost) * 0.5) < 1e-4 && w.FidelityCensus()[Array.IndexOf(FidelityNames, "fid_copies")] == 0, "law off: a proofreader still costs or counts");
        Console.WriteLine($"PASS paid fidelity: a ProofB gene is a proofreader only with the law (both organ laws, EN/RU, round trip); {passes0:0.#} passes at 15 °C → errors ×{scale:0.###} "
            + $"(counted: point {proofed / basePoints:0.###}, insertions {upS / up1:0.###}, deletions {downS / down1:0.###}; duplications {dupS / dup1:0.###}); at 40 °C ×{thermal:0.##} bare (counted {warmPoints / basePoints:0.###}), ×{hot:0.###} with the proofreader; "
            + $"cost {proof:0.#} booked at division and mating, refused when unaffordable; law off unchanged");
    }
}
