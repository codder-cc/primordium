using System;

namespace Primordium;

// Paid copy fidelity (P.PaidFidelity 1): how faithfully a model-1 genome is copied is a heritable, paid,
// physical property of the body that copies it, not a credit the engine gives to "useful" bytes.
//
//   temperature  A copy error is a wrong byte accepted: a Boltzmann factor e^(−ΔG/kT) of one discrimination
//                energy. MutPoint is that factor at TempRef, so at body temperature T (kelvin) every polymerase
//                error (point change, insertion, deletion) is × θ(T) = MutPoint^(T_ref/T − 1): ~0.77 at 0 °C,
//                1 at TempRef (15 °C), ~1.27 at 30 °C, ~1.58 at 45 °C. No new constant.
//   proofreader  A protein the genome makes (a kind-3 gene with B & 7 = Enzyme.ProofB, Genome.Decode) rechecks
//                the copy. Its strength g = Σ amount × quality × temperature window (as every organ's, World.Organs)
//                is the number of passes; each pass (Hopfield's kinetic proofreading) multiplies the errors by
//                e^(−FidelityDE·T_ref/T) — it discriminates less well when warm — and costs FidelityCost per copied
//                byte (it rechecks every byte, the right ones too). A fraction of a pass works in proportion.
//   cost         FidelityCost × g × bytes copied, at division (with the division's cost: the body must afford
//                both) and at mating (the one who mates copies), booked by Dissipate like every cost (with
//                P.MatterEnergy 1 owed and settled from the body's charge, World.Charge).
//
// The error the copy is made with is scale = θ(T) × e^(−FidelityDE·(T_ref/T)·g) times the base rates
// (Genome.Mutate; duplication keeps its rate — a slipped strand, not a misread byte). UV and flare damage
// (World.LiveBody) hit the genome as before: they are not copy errors and no proofreader repairs them.
// A proofreader is a protein like any other: it costs a molecule and CostExpress to make, wears (EnzDecay)
// and must be remade; a line that stops making it copies at the base rate again. Selection decides how much
// accuracy is worth. With the law off a ProofB motor gene makes a motor, and copies are as before.
public sealed partial class World
{
    public static bool FidelityLaw => P.PaidFidelity != 0;

    // Proofreading strength: all proofreader copies at the body's temperature (passes per copied byte).
    public float ProofStrength(Agent a)
    {
        float g = 0;
        for (int k = 0; k < a.EnzN; k++)
        {
            ref var e = ref a.Enz[k];
            if (e.Kind == Enzyme.Proofread) g += e.Amount * e.Eff * Window(e, a.Tb);
        }
        return g;
    }

    // T_ref/T in kelvin.
    static double RefOverT(float tb) => (273.15 + P.TempRef) / Math.Max(1.0, 273.15 + tb);

    // The thermal factor of the base errors at body temperature tb: MutPoint^(T_ref/T − 1) (1 if MutPoint is
    // 0 or ≥ 1: no discrimination energy to scale).
    public static double ThermalErrorFactor(float tb)
    {
        double p0 = P.Dec(P.MutPoint);
        if (!(p0 > 0 && p0 < 1)) return 1;
        return Math.Exp(Math.Log(p0) * (RefOverT(tb) - 1));
    }

    // The error factor of g proofreading passes at body temperature tb: e^(−FidelityDE·(T_ref/T)·g).
    public static double ProofFactor(float tb, double g) => Math.Exp(-P.Dec(P.FidelityDE) * RefOverT(tb) * g);

    // How many times the base rates this body errs when it copies a genome now, and its passes.
    public double CopyErrorScale(Agent a, out double passes)
    {
        passes = ProofStrength(a);
        return ThermalErrorFactor(a.Tb) * ProofFactor(a.Tb, passes);
    }

    // The energy of proofreading `bytes` copied bytes with g passes.
    public static double ProofCost(double passes, int bytes) => P.Dec(P.FidelityCost) * passes * bytes;

    // ---- observation (tools/bench census; never read by the simulation, not saved) ----

    public const int FidStatN = 7;
    const int FsCopies = 0, FsProofed = 1, FsPasses = 2, FsScale = 3, FsThermal = 4, FsCost = 5, FsBytes = 6;
    readonly double[] fidStats = new double[FidStatN];
    public double FidelityEnergy;   // all energy spent on proofreading so far (observation)

    // A copy made with error scale `scale` (thermal part `thermal`), `passes` and `cost` for `bytes` bytes.
    void FidNote(double scale, double thermal, double passes, double cost, int bytes)
    {
        var s = cur?.Fid ?? fidStats;
        s[FsCopies] += 1;
        if (passes > 0) s[FsProofed] += 1;
        s[FsPasses] += passes; s[FsScale] += scale; s[FsThermal] += thermal; s[FsCost] += cost; s[FsBytes] += bytes;
    }

    // Copy errors and proofreading for a copy of `bytes` bytes by body a (law on): the scale for Mutate and
    // the cost (not yet booked: the caller dissipates it where the copy is paid).
    (double scale, double cost) Fidelity(Agent a, int bytes)
    {
        double thermal = ThermalErrorFactor(a.Tb), passes = ProofStrength(a);
        double scale = thermal * ProofFactor(a.Tb, passes), cost = ProofCost(passes, bytes);
        FidNote(scale, thermal, passes, cost, bytes);
        return (scale, cost);
    }

    public static readonly string[] FidelityNames =
    {
        "proof_share", "proof_strength", "proof_strength_carriers", "fid_copies", "fid_proofed_share",
        "fid_passes", "fid_error_scale", "fid_thermal", "fid_energy", "fid_energy_per_copy", "fid_energy_total",
    };

    // Shares of living bodies with a proofreader (≥ 0.5 units), the mean strength over all and over those;
    // copies since the last call, the share of them proofread, mean passes, error scale and its thermal part
    // per copy, energy of proofreading since the last call, per copy, and in all. Resets the copy counters.
    public double[] FidelityCensus()
    {
        var v = new double[FidelityNames.Length];
        int pop = 0, has = 0;
        double strength = 0, carriers = 0;
        foreach (var a in Agents)
        {
            if (a.Dead) continue;
            pop++;
            float amount = 0;
            for (int k = 0; k < a.EnzN; k++) if (a.Enz[k].Kind == Enzyme.Proofread) amount += a.Enz[k].Amount;
            float g = ProofStrength(a);
            strength += g;
            if (amount >= 0.5f) { has++; carriers += g; }
        }
        foreach (var c in ctxs) { for (int k = 0; k < FidStatN; k++) { fidStats[k] += c.Fid[k]; c.Fid[k] = 0; } }
        FidelityEnergy += fidStats[FsCost];
        double copies = fidStats[FsCopies];
        v[0] = pop > 0 ? (double)has / pop : 0;
        v[1] = pop > 0 ? strength / pop : 0;
        v[2] = has > 0 ? carriers / has : 0;
        v[3] = copies;
        v[4] = copies > 0 ? fidStats[FsProofed] / copies : 0;
        v[5] = copies > 0 ? fidStats[FsPasses] / copies : 0;
        v[6] = copies > 0 ? fidStats[FsScale] / copies : 0;
        v[7] = copies > 0 ? fidStats[FsThermal] / copies : 0;
        v[8] = fidStats[FsCost];
        v[9] = copies > 0 ? fidStats[FsCost] / copies : 0;
        v[10] = FidelityEnergy;
        Array.Clear(fidStats);
        return v;
    }
}
