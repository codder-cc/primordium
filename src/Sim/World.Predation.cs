using System;
using System.Threading;

namespace Primordium;

// Observation: the predation probe (World.PredProbe, null = off) books what attacks, thefts and gene
// injections cost and bring, and whom they hit — kin, the own lineage, the rest — for the predation
// audit (ROADMAP 5, bench `--predation`). Counters only: the simulation never reads them, uses no
// randomness for them and they are not saved. Thread-safe: called from the agent phase (Interlocked;
// sums of doubles may differ in the last bits between runs — observation only).
//
// Energy brought into bodies: photons caught (an excitation), and the bond energy (Chemistry.E) of every
// molecule that enters a body — from the environment (intake, mining, soaking) or from another body
// (torn out by `attack`, pulled by `take`). The hunting share is the prey's part of that sum.
public sealed class PredationProbe
{
    public const int Bins = 8;   // victims' body cohesion: [0, 0.25), [0.25, 0.5) … [1.75, ∞)

    public long Attacks, Missed, Torn, TornKin, TornLineage, Kills, KillsKin, KillsLineage, Remains;
    public double AttackWork, TornE, TornSplit, Injury, RemainsE;
    public long Takes, Taken, TakenKin, TakenLineage;
    public double TakeWork, TakenE;
    public long EnvMols;
    public double EnvE, Photo, Store;
    // Excitation (with P.MatterEnergy 1: charge, what a body can spend) brought in: by molecules from the
    // environment, torn out, pulled; and captured from reactions in bodies (World.Charge).
    public double EnvX, TornX, TakenX, Captured;
    public long Injects, InjectsForeign, InjectsDone, Inherited, Spread;
    public readonly long[] BinAttacks = new long[Bins], BinTorn = new long[Bins];
    public readonly double[] BinWork = new double[Bins], BinCohesion = new double[Bins], BinMass = new double[Bins];

    public static int Bin(float cohesion) => Math.Clamp((int)(cohesion * 4), 0, Bins - 1);

    public static void Add(ref double target, double v)
    {
        double seen = target;
        while (true)
        {
            double was = Interlocked.CompareExchange(ref target, seen + v, seen);
            if (was == seen) return;
            seen = was;
        }
    }

    public void Clear()
    {
        Attacks = Missed = Torn = TornKin = TornLineage = Kills = KillsKin = KillsLineage = Remains = 0;
        AttackWork = TornE = TornSplit = Injury = RemainsE = 0;
        Takes = Taken = TakenKin = TakenLineage = 0;
        TakeWork = TakenE = 0;
        EnvMols = 0; EnvE = Photo = Store = 0;
        EnvX = TornX = TakenX = Captured = 0;
        Injects = InjectsForeign = InjectsDone = Inherited = Spread = 0;
        Scavenged = 0; ScavengedE = ScavengedX = 0;
        Deaths = 0; DeathE = DeathX = 0;
        Array.Clear(BinAttacks); Array.Clear(BinTorn); Array.Clear(BinWork); Array.Clear(BinCohesion); Array.Clear(BinMass);
    }

    // Remains of dead bodies (killed or not) still lying in a cell (molecules): intake there is counted as
    // scavenging (an upper bound: whatever molecule is taken in there, as long as remains are left).
    public readonly System.Collections.Concurrent.ConcurrentDictionary<int, long[]> Carcass = new();
    public long Scavenged;
    public double ScavengedE, ScavengedX;   // bond energy and excitation (the charge, with P.MatterEnergy 1) taken back

    // Every death on open ground: what its remains hold (molecules, bond energy, excitation), and where, for
    // the remains' lifetime (Deceased: drained between ticks by the report).
    public long Deaths;
    public double DeathE, DeathX;
    public readonly System.Collections.Concurrent.ConcurrentQueue<(int cell, long tick, float temp, float wet)> Deceased = new();

    public void Death(int cell, long n, double e, double x, long tick, float temp, float wet)
    {
        Interlocked.Increment(ref Deaths);
        Add(ref DeathE, e);
        Add(ref DeathX, x);
        Interlocked.Add(ref Carcass.GetOrAdd(cell, _ => new long[1])[0], n);
        Deceased.Enqueue((cell, tick, temp, wet));
    }

    public void EnvGain(int e, int cell = -1, int x = 0)
    {
        Interlocked.Increment(ref EnvMols);
        Add(ref EnvE, e);
        if (x > 0) Add(ref EnvX, x);
        if (cell < 0 || Carcass.IsEmpty || !Carcass.TryGetValue(cell, out var left)) return;
        if (Interlocked.Decrement(ref left[0]) < 0) { Interlocked.Increment(ref left[0]); return; }
        Interlocked.Increment(ref Scavenged);
        Add(ref ScavengedE, e);
        if (x > 0) Add(ref ScavengedX, x);
    }

    public void Tear(Agent a, Agent t, int s, Chemistry chem)
    {
        Interlocked.Increment(ref Torn);
        Add(ref TornE, chem.E[s]);
        if (chem.Gap[s] > 0) Add(ref TornX, chem.Gap[s]);
        if (chem.SplitExo[s]) Add(ref TornSplit, chem.SplitEnergy(s));
        if (a.Lineage == t.Lineage) Interlocked.Increment(ref TornLineage);
        if (Kin(a, t)) Interlocked.Increment(ref TornKin);
    }

    public void Attack(Agent a, Agent t, float work, float injury, float cohesion, int torn)
    {
        Interlocked.Increment(ref Attacks);
        if (torn == 0) Interlocked.Increment(ref Missed);
        Add(ref AttackWork, work);
        Add(ref Injury, injury);
        int b = Bin(cohesion);
        Interlocked.Increment(ref BinAttacks[b]);
        Interlocked.Add(ref BinTorn[b], torn);
        Add(ref BinWork[b], work);
        Add(ref BinCohesion[b], cohesion);
        Add(ref BinMass[b], t.Mass);
    }

    public void Kill(Agent a, Agent t, int cell, Chemistry chem)
    {
        Interlocked.Increment(ref Kills);
        if (a.Lineage == t.Lineage) Interlocked.Increment(ref KillsLineage);
        if (Kin(a, t)) Interlocked.Increment(ref KillsKin);
        long n = 0;
        double e = 0;
        for (int s = 0; s < Chemistry.S; s++) { long k = t.Inv[s] + (long)t.Pend[s].F; n += k; e += k * chem.E[s]; }
        Interlocked.Add(ref Remains, n);
        Add(ref RemainsE, e);   // (its molecules join the cell's remains in Death, as every death's do)
    }

    public void Take(Agent a, Agent t, int s, float work, bool got, Chemistry chem)
    {
        Interlocked.Increment(ref Takes);
        Add(ref TakeWork, work);
        if (!got) return;
        Interlocked.Increment(ref Taken);
        Add(ref TakenE, chem.E[s]);
        if (chem.Gap[s] > 0) Add(ref TakenX, chem.Gap[s]);
        if (a.Lineage == t.Lineage) Interlocked.Increment(ref TakenLineage);
        if (Kin(a, t)) Interlocked.Increment(ref TakenKin);
    }

    public void Inject(Agent a, Agent t)
    {
        Interlocked.Increment(ref Injects);
        if (a.Lineage != t.Lineage) Interlocked.Increment(ref InjectsForeign);
    }

    // Close kin: within EvoMetrics.KinRadius bits of the genome fingerprint (a "kind" a body could tell apart).
    public static bool Kin(Agent a, Agent b) => World.Kinship(a, b) >= 64 - EvoMetrics.KinRadius;
}

public sealed partial class World
{
    public PredationProbe PredProbe;   // observation only (see PredationProbe); null = off

    // The excitation lying loose on a cell's surface, Σ C·Gap (observation: the remains' charge, RemainsLife).
    public double LooseExcitation(int cell)
    {
        double x = 0;
        foreach (int s in Chem.Excited) x += C[s][cell].D * Chem.Gap[s];
        return x;
    }

    // ---- parasites: following injected code (observation only; saved with the chronicle) ----

    // Children that inherited a piece of another lineage's code (still whole in their genome), and pieces
    // a host copied on into a third body with its own `inject`: the parasite's code replicating through
    // the host. Since the world began.
    public long ParasiteInherited, ParasiteSpreads;
    const int MinForeign = 8;   // shorter pieces match a genome by chance and say nothing about descent

    // `a` wrote `seg` into `t` (a successful push of `inject`).
    void ForeignCode(Agent a, Agent t, ReadOnlySpan<byte> seg)
    {
        if (a.Foreign != null && a.ForeignFrom != a.Lineage && seg.IndexOf(a.Foreign) >= 0)
        {
            t.Foreign = a.Foreign;
            t.ForeignFrom = a.ForeignFrom;
            Interlocked.Increment(ref ParasiteSpreads);
            if (PredProbe != null) Interlocked.Increment(ref PredProbe.Spread);
            Propose(EvType.ParasiteSpread, Chronicle.OnceKey(EvType.ParasiteSpread), t, a, a.ForeignFrom);
            return;
        }
        if (a.Lineage == t.Lineage || seg.Length < MinForeign) return;
        t.Foreign = seg.ToArray();
        t.ForeignFrom = a.Lineage;
        if (PredProbe != null) Interlocked.Increment(ref PredProbe.InjectsDone);
    }

    // A child carries its parent's (or its mate's) foreign piece if the piece is still whole in its genome.
    void InheritForeign(Agent parent, Agent child, Agent mate)
    {
        foreach (var p in mate == null ? stackalloc[] { 0 } : stackalloc[] { 0, 1 })
        {
            var from = p == 0 ? parent : mate;
            if (from.Foreign == null || child.G.AsSpan().IndexOf(from.Foreign) < 0) continue;
            child.Foreign = from.Foreign;
            child.ForeignFrom = from.ForeignFrom;
            Interlocked.Increment(ref ParasiteInherited);
            if (PredProbe != null) Interlocked.Increment(ref PredProbe.Inherited);
            return;
        }
    }

    // How firmly a body holds its molecules. A body is a disordered aggregate of what it holds, so its
    // cohesion is that of a mixed block of the same molecules (CoreCohesion: the mean bond times how
    // well they fit together, Chemistry.Contact), with no lattice order. Body molecules are few kinds:
    // the pair sum is over the kinds it holds.
    public float BodyCohesion(Agent t)
    {
        int n = t.InvTotal;
        if (n <= 0) return 0;
        Span<int> kinds = stackalloc int[Chemistry.S];
        int k = 0;
        float bond = 0;
        for (int s = 0; s < Chemistry.S; s++)
            if (t.Inv[s] > 0) { kinds[k++] = s; bond += Chem.Bond[s] * t.Inv[s]; }
        float purity = 0;
        for (int i = 0; i < k; i++)
            for (int j = 0; j < k; j++)
                purity += (float)t.Inv[kinds[i]] * t.Inv[kinds[j]] * Chem.Contact[kinds[i] + 2, kinds[j] + 2];
        return Math.Max(1e-4f, bond / n * purity / ((float)n * n));
    }

    // The barrier of pulling a molecule out of a body: the barrier of a face of the same disordered
    // aggregate (VoxelBarrier with Order 0), lowered by the puller's protein for the body's dominant
    // molecule (Catalysis, as in mining).
    float BodyBarrier(Agent puller, Agent t, out float cohesion)
    {
        cohesion = BodyCohesion(t);
        int dom = 0;
        for (int s = 1; s < Chemistry.S; s++) if (t.Inv[s] > t.Inv[dom]) dom = s;
        float cat = Catalysis(puller, Chem.BuiltMat[dom], out _);
        return P.RockBarrier * (0.15f + cohesion * cohesion) * 0.35f * (1 - cat);
    }

    // `torn` molecules left `from` for `to` (attack, take): they carry P.TornStore of their share of its
    // store. Energy moves between two bodies (no flow); what the doubles round is booked as Rounding.
    void CarryStore(Agent from, Agent to, int torn)
    {
        if (P.TornStore <= 0 || from.Energy <= 0) return;
        double e = from.Energy * P.TornStore * torn / (from.InvTotal + torn), before = from.Energy + to.Energy;
        from.Energy -= e;
        to.Energy += e;
        to.TickGot += (float)e;
        to.LifeGot += (float)e;
        Flows[FRounding] += before - (from.Energy + to.Energy);
        if (PredProbe != null) PredationProbe.Add(ref PredProbe.Store, e);
    }

    // The part of a strike that tore nothing out goes into the victim's body as heat: from the attacker's
    // store into the heat the victim holds (both in the energy stock: no flow), warming it with the same
    // heat capacity as reaction heat (React); it leaves as shed heat when the body cools (LiveBody).
    void StrikeHeat(Agent a, Agent t, double q)
    {
        if (q <= 0) return;
        if (MatterLaw)
        {
            // Law 1: from the attacker's carriers (relaxed, not into its cell) into the victim's held heat.
            double got = Relax(a, q, false, true);
            double h = Math.Min(got, q), h0 = t.HeatHeld;
            t.HeatHeld += h;
            t.Tb += (float)h * 6f / (5f + t.Mass);
            Flows[FRounding] += h - (t.HeatHeld - h0);
            return;
        }
        double before = a.Energy + t.HeatHeld;
        a.Energy -= q;
        t.HeatHeld += q;
        t.Tb += (float)q * 6f / (5f + t.Mass);
        Flows[FRounding] += before - (a.Energy + t.HeatHeld);
    }

    // Work to tear one molecule out of a body of that barrier: what gnawing it out of a face of the same
    // aggregate costs (TakeBite: FaceWork·e^(barrier − FaceBarrier)).
    static float TearWork(float barrier) => P.FaceWork * MathF.Exp(Math.Min(80f, barrier - P.FaceBarrier));   // finite however firm
}
