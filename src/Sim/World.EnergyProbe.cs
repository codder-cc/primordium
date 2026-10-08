using System;

namespace Primordium;

// Observation: the energy-economy probe (World.EnergyProbe, null = off) books, for the energy audit
// (bench `--energy-audit`, docs/ENERGY-AUDIT.md), what each way of getting energy costs and brings:
// uptake of loose matter, soaking and mining rock, digging, attacks and thefts, photons, reactions in
// bodies (exothermic and uphill), what bodies throw out and what they leave when they die, what
// weathering moves from rock to the ground and what compaction presses back; and, by diet
// (World.Diet), how a body's energy changes per tick. Counters only: the simulation never reads them,
// uses no randomness for them and they are not saved. Each tile of the agent phase books into its own
// slot (Ctx.Slot), the main thread into the last one: no locks, and the sums do not depend on threads.
//
// Every molecule is booked with its bond energy E and with R, the energy its downhill splits release
// (EnergyEconomyProbe.Downhill): what a body gets from it without a protein or any energy spent.
public sealed class EnergyEconomyProbe
{
    public const int IntakeCalls = 0, IntakeCost = 1, IntakeMol = 2, IntakeE = 3, IntakeR = 4,
        SoakCalls = 5, SoakCost = 6, SoakMol = 7, SoakE = 8, SoakR = 9,
        MineTries = 10, MineCost = 11, MineMol = 12, MineE = 13, MineR = 14,
        DigCalls = 15, DigCost = 16,
        AttackCalls = 17, AttackCost = 18, TornMol = 19, TornE = 20, TornR = 21, TornStore = 22,
        TakeCalls = 23, TakeCost = 24, TakenMol = 25, TakenE = 26, TakenR = 27, TakenStore = 28,
        PhotoCaught = 29, PhotoGain = 30,
        SplitExoN = 31, SplitExoE = 32, SplitUpN = 33, SplitUpE = 34, BindExoN = 35, BindExoE = 36, BindUpN = 37, BindUpE = 38,
        ExpelMol = 39, ExpelE = 40, ExpelR = 41,
        DeathN = 42, DeathStore = 43, DeathMatterE = 44, DeathMatterR = 45, DeathMol = 46, DeathMass = 47,
        KilledN = 48, KilledStore = 49, KilledMatterE = 50, KilledMol = 51,
        WeatherMol = 52, WeatherE = 53, WeatherR = 54, SettleMol = 55, SettleE = 56, SettleR = 57,
        DietBase = 58, DietKeys = 7;   // per diet: body-ticks, net, given to offspring, photo, chemistry, mined E, upkeep
    public const int DTicks = 0, DNet = 1, DKids = 2, DPhoto = 3, DChem = 4, DMine = 5, DUpkeep = 6;
    public const int Keys = DietBase + 5 * DietKeys;

    public readonly double[] R;          // per species: energy released by its downhill splits (≥ 0)
    readonly double[][] slots;

    public EnergyEconomyProbe(World w)
    {
        R = Downhill(w.Chem);
        slots = new double[w.Tiles + 1][];
        for (int k = 0; k < slots.Length; k++) slots[k] = new double[Keys];
    }

    // R[s]: split s if that releases energy, then its products likewise, all the way down; 0 if its
    // split takes energy (or it has none). Products are always lower species (Chemistry builds every
    // formula from smaller ones), so one pass upwards fills it.
    public static double[] Downhill(Chemistry ch)
    {
        var r = new double[Chemistry.S];
        for (int s = 0; s < Chemistry.S; s++)
            if (ch.SplitExo[s]) r[s] = ch.SplitEnergy(s) + r[ch.SplitA[s]] + (ch.SplitB[s] >= 0 ? r[ch.SplitB[s]] : 0);
        return r;
    }

    internal double[] Slot(int k) => slots[k];

    public double[] Sum()
    {
        var t = new double[Keys];
        foreach (var s in slots) for (int k = 0; k < Keys; k++) t[k] += s[k];
        return t;
    }

    public void Clear() { foreach (var s in slots) Array.Clear(s); }
}

public sealed partial class World
{
    public EnergyEconomyProbe EnergyProbe;   // observation only (see EnergyEconomyProbe); null = off

    double[] EpSlot => EnergyProbe.Slot(cur?.Slot ?? Tiles);

    void EpAdd(int k, double v) { if (EnergyProbe != null) EpSlot[k] += v; }

    void EpMol(int k, int s, int n = 1)   // n molecules of s: count at k, E at k+1, R at k+2
    {
        var p = EnergyProbe;
        if (p == null) return;
        var d = EpSlot;
        d[k] += n; d[k + 1] += (double)n * Chem.E[s]; d[k + 2] += n * p.R[s];
    }

    void EpDeath(Agent a)
    {
        var p = EnergyProbe;
        if (p == null) return;
        var d = EpSlot;
        double e = 0, r = 0, n = 0;
        for (int s = 0; s < Chemistry.S; s++)
        {
            double k = a.Inv[s] + a.Pend[s].D;
            n += k; e += k * Chem.E[s]; r += k * p.R[s];
        }
        for (int k = 0; k < a.EnzN; k++) { double m = a.Enz[k].Matter.D; n += m; e += m * Chem.E[a.Enz[k].Material]; r += m * p.R[a.Enz[k].Material]; }
        double store = Math.Max(0, a.Energy) + a.HeatHeld;
        d[EnergyEconomyProbe.DeathN]++; d[EnergyEconomyProbe.DeathStore] += store; d[EnergyEconomyProbe.DeathMatterE] += e;
        d[EnergyEconomyProbe.DeathMatterR] += r; d[EnergyEconomyProbe.DeathMol] += n; d[EnergyEconomyProbe.DeathMass] += a.Mass;
    }

    void EpKilled(Agent t)
    {
        var p = EnergyProbe;
        if (p == null) return;
        var d = EpSlot;
        double e = 0;
        for (int s = 0; s < Chemistry.S; s++) e += (t.Inv[s] + t.Pend[s].D) * Chem.E[s];
        d[EnergyEconomyProbe.KilledN]++; d[EnergyEconomyProbe.KilledStore] += Math.Max(0, t.Energy) + t.HeatHeld;
        d[EnergyEconomyProbe.KilledMatterE] += e; d[EnergyEconomyProbe.KilledMol] += t.InvTotal;
    }

    // The end of a body's tick (before it may die of hunger): its energy change by its diet. `kids0`
    // and `spent0` are LifeKids and LifeUpkeep + LifeHarm + LifeSpill at the start of the tick.
    void EpLive(Agent a, double e0, float kids0, float spent0)
    {
        var d = EpSlot;
        int b = EnergyEconomyProbe.DietBase + Diet(a) * EnergyEconomyProbe.DietKeys;
        d[b + EnergyEconomyProbe.DTicks]++;
        d[b + EnergyEconomyProbe.DNet] += a.Energy - e0;
        d[b + EnergyEconomyProbe.DKids] += a.LifeKids - kids0;
        d[b + EnergyEconomyProbe.DPhoto] += a.TickPhoto;
        d[b + EnergyEconomyProbe.DChem] += a.TickChem;
        d[b + EnergyEconomyProbe.DMine] += a.TickMine;
        d[b + EnergyEconomyProbe.DUpkeep] += a.LifeUpkeep + a.LifeHarm + a.LifeSpill - spent0;
    }

    // ---- read-outs for the audit (main thread, between ticks; read only) ----

    // How firmly a body holds its molecules against a puller without a protein: the work to tear one
    // out (World.Attack, Take) — TearWork of the body's barrier, times P.BodyHold.
    public float TearCost(Agent t) => TearWork(P.RockBarrier * (0.15f + BodyCohesion(t) * BodyCohesion(t)) * 0.35f) * P.BodyHold;

    // What gnawing one molecule out of block v costs without teeth or a protein: FaceWork·e^(barrier − FaceBarrier).
    public float MineCostPerMolecule(int v) => P.FaceWork * MathF.Exp(Math.Min(80f, VoxelBarrier(v) - P.FaceBarrier));

    // Molecules of each species in block v (false if empty).
    public bool BlockCounts(int v, Span<int> counts)
    {
        counts.Clear();
        if (Mat[v] < 2 || Units[v] == 0) return false;
        if (Mixed(v, out var mix)) { for (int s = 0; s < Chemistry.S; s++) counts[s] = mix[s]; }
        else counts[Mat[v] - 2] = Units[v];
        return true;
    }

    // Lifting block v onto a neighbour level with it (the cheapest `dig`), for a body with no solids beyond one.
    public float DigCost(int v) => P.CostDig * Chem.MatHard[Mat[v]] / 1.25f + VoxelMass(v) * P.Gravity;
}
