using System;
using System.Linq;

namespace Primordium;

// The energy ledger: what flows into and out of the world's chemical energy, and what it holds.
//
// The stock is everything that can still do chemical work: the free energy of the bodies
// (Agent.Energy), the reaction heat they still hold (Agent.HeatHeld — it left the molecules but is
// not yet in the cells), and the bond energy Chemistry.E of every molecule — in bodies (whole and
// partly absorbed), in protein substrate, lying loose and in the air, in rock blocks and mixtures,
// and buried. Heat in the cells (heatIn → Temp) is outside: it is the sink.
//
// Inputs come from outside the chemistry: photons lifting a molecule to its excited state, strikes
// exciting loose matter, solar flares heating the bodies they hit (World.Sky: the absorbed energy goes
// into the heat a body holds, HeatHeld, and leaves as shed heat like reaction heat), matter the vents bring from the interior, matter the hand pours (or digs
// away: a negative input), creatures planted from designs with matter or energy brought from outside
// (Design: the bond energy of imported molecules plus imported free energy, World.HandEnergy; a body
// made from local matter and local splits only moves energy between reservoirs), the law P.EnergyK
// when it is not 1 (a reaction in a body then gives or takes EnergyK × the bond change: the
// difference to the bond change is created or destroyed by the law, booked as EnergyK — zero at the
// default 1, any sign otherwise), and the debt of bodies that die with negative energy (WriteOff, below).
// Outputs are heat: costs of acting and living (Dissipate), reaction heat a body sheds as it cools,
// what a body still held when it died, spontaneous decay in bodies and on the ground, pressure
// reactions in burials, energy of failed births (stillborn, failed abiogenesis) — and float rounding.
//
// Agent.Energy and HeatHeld are doubles (floats before save version 9: a cost of 0.001 taken from an
// energy of ~30 rounded by a few 1e-7, which summed to a drift of ~0.005–0.02 per 10⁸ moved). Costs and
// reaction gains still book what the double actually changed by, the difference to the nominal amount
// as Rounding (an output; now ~1e-16 of a store), so the balance is exact to the remaining pools.
//
// A cost larger than what the body has makes its energy negative; only what it had reaches the
// cells (Dissipate). The rest is booked as Unpaid (an output that went nowhere); if the body dies
// before earning it back, that negative energy leaves the stock and is booked back as WriteOff (an
// input). Together they are zero for a body that dies in debt; energy it earned back (or was given
// over a link) to pay the debt was destroyed, never created.
//
// Balance: stock(t1) − stock(t0) == inputs − outputs between two completed ticks. Flows are summed
// where they happen: per tile in the agent phase (each tile its own accumulators, summed in tile
// order when read — no locks, no dependence on thread timing), per row in the parallel cell
// chemistry, on the main thread elsewhere. Reading the stock scans the whole crust: on demand only.
public sealed partial class World
{
    public const int FPhoto = 0, FStrike = 1, FVent = 2, FHand = 3, FDesign = 4, FScale = 5, FWriteOff = 6, FFlare = 7;  // inputs
    public const int FDissipate = 8, FUnpaid = 9, FShed = 10, FDeath = 11, FBodyDecay = 12, FLooseDecay = 13,
        FPressure = 14, FStillborn = 15, FRounding = 16;                                                        // outputs
    public const int FImpact = 17, FAbio = 18;                                                                  // not in the balance
    public const int FlowCount = 19, InputsEnd = 8, OutputsEnd = 17;
    // Before save version 8 there was no `flare` input: the ledger had 18 flows (World.Save maps them).
    public const int FlowCountV6 = 18;
    public static readonly string[] FlowNames =
    {
        "photo", "strike", "vent", "hand", "design", "energy-k", "write-off", "flare",
        "dissipate", "unpaid", "shed", "death", "body decay", "loose decay", "pressure", "stillborn", "rounding",
        "impact", "abiogenesis",
    };

    // Per tile (agent phase), and one more slot for the main thread. Read through Flows.
    double[][] tileFlow;
    readonly double[] rowLooseDecay;   // cell chemistry runs in parallel by rows
    long pressureHeat;                                  // integer energies: exact in any order
    double heatFlushed;

    // Sum heat as it leaves heatIn for the climate (a cross-check of the flows: every heat output
    // must have gone through heatIn). Enable before the first step to compare.
    public bool TrackHeat;

    double[] Flows
    {
        get
        {
            var f = tileFlow ?? System.Threading.LazyInitializer.EnsureInitialized(ref tileFlow, InitFlows);
            var c = cur;
            return c != null ? f[c.Slot] : f[Tiles];
        }
    }

    double[][] InitFlows()
    {
        var f = new double[Tiles + 1][];
        for (int k = 0; k <= Tiles; k++) f[k] = new double[FlowCount];
        return f;
    }

    void FlushedHeat()
    {
        if (!TrackHeat) return;
        double t = 0;
        for (int i = 0; i < N; i++) t += heatIn[i];
        heatFlushed += t;
    }

    // Cumulative flows since the world was made, summed in a fixed order.
    public double[] EnergyFlows()
    {
        System.Threading.LazyInitializer.EnsureInitialized(ref tileFlow, InitFlows);
        var total = new double[FlowCount];
        foreach (var f in tileFlow) for (int k = 0; k < FlowCount; k++) total[k] += f[k];
        double loose = 0;
        foreach (var r in rowLooseDecay) loose += r;
        total[FLooseDecay] += loose;
        total[FPressure] += pressureHeat;
        return total;
    }

    // Call between completed ticks (like ElementBudget). Scans the whole crust.
    public EnergyAudit AuditEnergy()
    {
        var e = new EnergyAudit { Tick = Tick, Flows = EnergyFlows() };
        var E = Chem.E;
        for (int s = 0; s < Chemistry.S; s++)
        {
            double sum = 0;
            var c = C[s];
            for (int i = 0; i < N; i++) sum += c[i].D;
            e.Loose += sum * E[s];
        }
        var rock = new long[Chemistry.S];
        for (int c = 0; c < N; c++)
            for (int z = 2, h = Height[c]; z < h; z++)
            {
                int v = c * Z + z;
                if (Mat[v] < 2) continue;
                if (Mixed(v, out var counts)) { for (int s = 0; s < Chemistry.S; s++) rock[s] += counts[s]; }
                else rock[Mat[v] - 2] += Units[v];
            }
        for (int s = 0; s < Chemistry.S; s++) e.Rock += (double)rock[s] * E[s];
        foreach (int v in Buried.Keys.OrderBy(k => k))
        {
            var m = Buried[v].Matter;
            for (int s = 0; s < Chemistry.S; s++) e.Burial += m[s].D * E[s];
        }
        foreach (var a in Agents)
        {
            if (a.Dead) continue;
            e.Bodies += a.Energy;
            e.Held += a.HeatHeld;
            for (int s = 0; s < Chemistry.S; s++) e.BodyMatter += (a.Inv[s] + a.Pend[s].D) * E[s];
            for (int k = 0; k < a.EnzN; k++) e.Protein += a.Enz[k].Matter.D * E[a.Enz[k].Material];
            if (a.Poly != null)   // polymers (World.Polymer.cs): their residues' bonds as molecules, and the bonds between them
            {
                for (int s = 0; s < Chemistry.S; s++) e.Polymer += a.Poly.M[s].D * E[s];
                e.Polymer += a.Poly.BondEnergy;
            }
        }
        double pending = 0;
        for (int i = 0; i < N; i++) pending += heatIn[i];
        e.HeatSeen = heatFlushed + pending;
        e.HeatTracked = TrackHeat;
        return e;
    }
}

// One reading of the ledger: the stock by reservoir and the cumulative flows (World.FlowNames).
public sealed class EnergyAudit
{
    public long Tick;
    public double Bodies, Held, BodyMatter, Protein, Polymer, Loose, Rock, Burial;   // Polymer: bodies' polymer pools (World.Polymer.cs)
    public double[] Flows;
    public double HeatSeen;     // heat that went through heatIn (only with World.TrackHeat)
    public bool HeatTracked;

    public double Stock => Bodies + Held + BodyMatter + Protein + Polymer + Loose + Rock + Burial;
    public double In { get { double t = 0; for (int k = 0; k < World.InputsEnd; k++) t += Flows[k]; return t; } }
    public double Out { get { double t = 0; for (int k = World.InputsEnd; k < World.OutputsEnd; k++) t += Flows[k]; return t; } }
    // Everything the flows say reached the cells as heat (Unpaid went nowhere; impact heat is
    // gravity's, not the chemistry's, but it goes through heatIn too).
    public double HeatOut => Out - Flows[World.FUnpaid] - Flows[World.FRounding] + Flows[World.FImpact];

    // Energy that appeared (+) or vanished (−) between two readings beyond what the flows explain.
    public static double Drift(EnergyAudit a, EnergyAudit b) => (b.Stock - a.Stock) - ((b.In - a.In) - (b.Out - a.Out));

    // Gross flow between two readings: the scale a float rounding drift is judged against.
    public static double Gross(EnergyAudit a, EnergyAudit b)
    {
        double t = 0;
        for (int k = 0; k < World.OutputsEnd; k++) t += Math.Abs(b.Flows[k] - a.Flows[k]);
        return t;
    }

    // Heat that went through heatIn but no flow accounts for (+), or flows that never got there (−).
    public static double HeatMismatch(EnergyAudit a, EnergyAudit b) => (b.HeatSeen - a.HeatSeen) - (b.HeatOut - a.HeatOut);

    // Tolerance: what is left unbooked is float rounding in the pools (C, Pend, burials) and in energy
    // handed between bodies (links, division) — ~1e-9 of the gross flow in practice; 5e-6 leaves room.
    public static double Tolerance(EnergyAudit a, EnergyAudit b) => 1.0 + 5e-6 * Gross(a, b);

    public string Describe(EnergyAudit from)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string F(double v) => v.ToString("F1", inv);
        var flows = string.Join(" ", Enumerable.Range(0, World.FlowCount)
            .Where(k => Flows[k] != from.Flows[k]).Select(k => $"{World.FlowNames[k]} {F(Flows[k] - from.Flows[k])}"));
        string heat = HeatTracked && from.HeatTracked ? $", heat via cells off by {HeatMismatch(from, this).ToString("F3", inv)}" : "";
        return $"energy drift {Drift(from, this).ToString("F3", inv)} (tolerance {Tolerance(from, this).ToString("F1", inv)}) over in {F(In - from.In)} out {F(Out - from.Out)}{heat} | stock {F(Stock)}: bodies {F(Bodies)} held {F(Held)} body matter {F(BodyMatter)} protein {F(Protein)}{(Polymer != 0 || from.Polymer != 0 ? $" polymer {F(Polymer)}" : "")} loose {F(Loose)} rock {F(Rock)} burial {F(Burial)} | flows: {flows}";
    }
}
