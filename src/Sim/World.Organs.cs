using System;
using System.Collections.Generic;

namespace Primordium;

// Organs of sense and the motor (P.Organs 1). A sense is not an ability a body has: it is a protein its
// genome made (a kind-3 gene, Genome.Decode → Enzyme.Transducer) from the body's own molecules, and what it
// reads, how well and how far follows from that protein — its spec, its amount, its best temperature and
// the molecule folded into it — not from a table:
//
//   receptor        binds molecule A outside. A reading of `sense m`/`smell m` (OutsideAmount, Gradient) is
//                   answered by the receptor whose target best matches m; it reports everything that binds
//                   to it, by how alike the molecules are (shared atoms, Affinity) — a receptor for a
//                   formula of its own is specific, one for a common pattern is not.
//   photoreceptor   a pigment A (a ground state with an excited one, Chemistry.PhotoUp) held in the protein:
//                   MakeProtein folds a molecule of A into it if the body has one. Only copies holding their
//                   pigment see; each sees by its pigment's excitation (Gap of A*, relative to the chemistry's
//                   largest). light, photons, uv and look read through it; look needs LookMin of it and
//                   reaches LookPerUnit cells per unit (≤ 16), and its cost grows with range and strength.
//   mechanoreceptor touch: count, hurt (Alarm), ground, feel, listen.
//   thermoreceptor  temp and btemp: a protein unfolds near its best temperature, so it reads well only
//                   near Topt (its window at the temperature read).
//   motor           every motor copy pulls (not only the best one): a push is full when their strength
//                   reaches MotorLoad·(1 + mass), and both the push and its cost scale with the share.
//
// Strength of an organ = Σ amount × quality × its temperature window (as a catalyst's drive, World.Chance).
// A reading is scaled by min(1, strength) (a weak organ catches less) and is noisy: relative noise
// SenseNoise/(1 + strength), from the body's random stream (Rng: deterministic). Discrete readings (a
// contact, an alarm, the ground, a partner's signal) are caught with the chance min(1, strength) instead.
// Without the organ a reading gives 0 (or "nothing": −1 / here) and still costs SenseTry; with it it costs
// SenseTry + SenseDrive × strength. Every unit of organ protein costs OrganUpkeep a tick (LiveBody), and
// wears (EnzDecay) like any protein: an organ the genome stops making fades away. All costs go through
// Dissipate, the same booking as every other cost (with P.MatterEnergy 1 a debt for the tick, World.Charge).
// The body's own state (energy, have, mass, age) is not a sense of the world and stays free.
public sealed partial class World
{
    public static bool OrganLaw => P.Organs != 0;

    // ---- observation (tools/bench census; never read by the simulation, not saved) ----

    // Reads by sense (index kind − Motor: motor, receptor, photoreceptor, mechanoreceptor, thermoreceptor),
    // reads with the organ there, the energy of readings, of organ upkeep, body ticks.
    public const int OrganStatN = 13;
    const int OsReads = 0, OsHits = 5, OsUse = 10, OsUpkeep = 11, OsBodies = 12;
    readonly double[] organStats = new double[OrganStatN];

    void OrganNote(int stat, double v)
    {
        var c = cur;
        (c != null ? c.Organ : organStats)[stat] += v;
    }

    void MergeOrganStats(double[] from)
    {
        for (int k = 0; k < OrganStatN; k++) { organStats[k] += from[k]; from[k] = 0; }
    }

    // ---- strength ----

    static float Window(in Enzyme e, float t)
    {
        float d = (t - e.Topt) / P.EnzWidth;
        return MathF.Exp(-d * d);
    }

    // All copies of one kind of organ (motor, mechanoreceptor) at the body's temperature.
    public float OrganStrength(Agent a, int kind)
    {
        float g = 0;
        for (int k = 0; k < a.EnzN; k++)
        {
            ref var e = ref a.Enz[k];
            if (e.Kind == kind) g += e.Amount * e.Eff * Window(e, a.Tb);
        }
        return g;
    }

    // Photoreceptors that hold their pigment, each by how strongly its pigment is excited by light.
    public float PigmentStrength(Agent a)
    {
        float g = 0;
        for (int k = 0; k < a.EnzN; k++)
        {
            ref var e = ref a.Enz[k];
            if (e.Kind == Enzyme.Photoreceptor && e.Material == e.A) g += e.Amount * e.Eff * Window(e, a.Tb) * Absorb(e.A);
        }
        return g;
    }

    // How well a pigment sees: its excitation (A → A*) relative to the largest in this chemistry; 0 for a
    // molecule light does not excite.
    public float Absorb(int s)
    {
        int up = Chem.PhotoUp[s];
        if (up < 0) return 0;
        float max = maxGap;
        if (max <= 0)
        {
            foreach (int x in Chem.Excited) max = Math.Max(max, Chem.Gap[x]);
            maxGap = max = Math.Max(1, max);   // the same value from every thread
        }
        return Chem.Gap[up] / max;
    }
    float maxGap;

    // The receptors a reading for molecule s is answered by: the target whose receptors (all copies)
    // bind s best; their strength (0: none binds s).
    public float ReceptorStrength(Agent a, int s, out int target)
    {
        target = -1;
        float best = 0, strength = 0;
        for (int k = 0; k < a.EnzN; k++)
        {
            ref var e = ref a.Enz[k];
            if (e.Kind != Enzyme.Receptor) continue;
            float aff = Affinity(e.A, s);
            if (aff <= 0) continue;
            float g = 0;   // all receptors for that target
            for (int j = 0; j < a.EnzN; j++)
            {
                ref var f = ref a.Enz[j];
                if (f.Kind == Enzyme.Receptor && f.A == e.A) g += f.Amount * f.Eff * Window(f, a.Tb);
            }
            if (g * aff > best) { best = g * aff; strength = g; target = e.A; }
        }
        return strength;
    }

    // Any chemoreceptor at all (kin and genes of a partner are read through the chemistry of contact).
    float AnyReceptor(Agent a) => OrganStrength(a, Enzyme.Receptor);

    // Thermoreceptors at the temperature t read.
    public float ThermoStrength(Agent a, float t)
    {
        float g = 0;
        for (int k = 0; k < a.EnzN; k++)
        {
            ref var e = ref a.Enz[k];
            if (e.Kind == Enzyme.Thermo) g += e.Amount * e.Eff * Window(e, t);
        }
        return g;
    }

    // How much a receptor for t binds q: (shared atoms / atoms of either)², 1 for the same formula (an
    // excited molecule is bound like its ground state: the receptor sees the shape, not the energy).
    public float Affinity(int t, int q) => Affinities().Aff[t * Chemistry.S + q];

    sealed class AffinityTable { public float[] Aff; public int[][] Partners; }
    AffinityTable affinity;

    // Built on first use, the same from every thread (one reference published when complete).
    AffinityTable Affinities()
    {
        var table = affinity;
        if (table != null) return table;
        var aff = new float[Chemistry.S * Chemistry.S];
        var partners = new int[Chemistry.S][];
        var list = new List<int>();
        for (int t = 0; t < Chemistry.S; t++)
        {
            list.Clear();
            for (int q = 0; q < Chemistry.S; q++)
            {
                int lo = 0, hi = 0;
                for (int e = 0; e < Chemistry.ElementCount; e++)
                {
                    lo += Math.Min(Chem.Atoms[t, e], Chem.Atoms[q, e]);
                    hi += Math.Max(Chem.Atoms[t, e], Chem.Atoms[q, e]);
                }
                float o = hi > 0 ? (float)lo / hi : 0;
                float v = o * o;
                if (v < 0.05f) v = 0;
                aff[t * Chemistry.S + q] = v;
                if (v > 0) list.Add(q);
            }
            partners[t] = list.ToArray();
        }
        table = new AffinityTable { Aff = aff, Partners = partners };
        System.Threading.Volatile.Write(ref affinity, table);
        return table;
    }

    // What a receptor for `target` reports at a cell: every loose molecule it binds, by its affinity.
    float Bound(Agent a, int cell, int target)
    {
        var table = Affinities();
        float v = 0;
        foreach (int q in table.Partners[target]) v += table.Aff[target * Chemistry.S + q] * LooseAmount(a, cell, q);
        return v;
    }

    // ---- reading ----

    // A reading through an organ of strength g: its cost and the record.
    void SenseCost(Agent a, int kind, float g)
    {
        double cost = P.SenseTry + P.SenseDrive * g;
        Dissipate(a, cost);
        OrganNote(OsReads + kind - Enzyme.Motor, 1);
        if (g > 0) OrganNote(OsHits + kind - Enzyme.Motor, 1);
        OrganNote(OsUse, cost);
    }

    // A free reading with the law off (counted only, for the census).
    void SenseFree(int kind) => OrganNote(OsReads + kind - Enzyme.Motor, 1);

    // The factor a reading is multiplied by: 1 ± SenseNoise/(1 + g) (a triangle, from the body's stream).
    float Noisy(float g) => 1 + P.SenseNoise * (float)(Rng.NextDouble() + Rng.NextDouble() - 1) / (1 + g);

    // Caught, for a discrete reading: with the chance min(1, g).
    bool Caught(float g) => g >= 1 || (g > 0 && Rng.NextDouble() < g);

    float ReadAmount(Agent a, int cell, int s)
    {
        float g = ReceptorStrength(a, s, out int t);
        SenseCost(a, Enzyme.Receptor, g);
        if (g <= 0) return 0;
        return Math.Max(0, Bound(a, cell, t) * Math.Min(1, g) * Noisy(g));
    }

    int ReadGradient(Agent a, int cell, int s)
    {
        float g = ReceptorStrength(a, s, out int t);
        SenseCost(a, Enzyme.Receptor, g);
        if (g <= 0) return 4;   // nothing smelled: no way is better than here
        float best = Bound(a, cell, t) * Noisy(g);
        int bd = 4;
        for (int d = 0; d < 4; d++)
        {
            float here = Bound(a, nb[cell * 4 + d], t) * Noisy(g);
            if (here > best) { best = here; bd = d; }
        }
        return bd;
    }

    float ReadLight(Agent a, float truth)
    {
        float g = PigmentStrength(a);
        SenseCost(a, Enzyme.Photoreceptor, g);
        if (g <= 0) return 0;
        return Math.Max(0, truth * Math.Min(1, g) * Noisy(g));
    }

    float ReadTemp(Agent a, float t)
    {
        float g = ThermoStrength(a, t);
        SenseCost(a, Enzyme.Thermo, g);
        if (g <= 0) return 0;
        return t + P.SenseNoise * P.EnzWidth * (float)(Rng.NextDouble() + Rng.NextDouble() - 1) / (1 + g);
    }

    // Touch: true if the mechanoreceptors catch it (paid either way).
    bool Touch(Agent a)
    {
        float g = OrganStrength(a, Enzyme.Mechano);
        SenseCost(a, Enzyme.Mechano, g);
        return Caught(g);
    }

    // ---- what a partner tells by contact (model 1: feel, kin, ngene, listen) ----

    // Its energy as touch feels it (−1: nobody, or not felt).
    public int FeelEnergy(Agent a, Agent o)
    {
        if (!OrganLaw) { SenseFree(Enzyme.Mechano); return o == null ? -1 : (int)Avail(o); }
        float g = OrganStrength(a, Enzyme.Mechano);
        SenseCost(a, Enzyme.Mechano, g);
        if (o == null || g <= 0) return -1;
        return (int)Math.Max(0, Avail(o) * Noisy(g));
    }

    // How related it is (0–64), by the chemistry of its surface (−1: nobody, or no receptor).
    public int KinSense(Agent a, Agent o)
    {
        if (!OrganLaw) { SenseFree(Enzyme.Receptor); return o == null ? -1 : Kinship(a, o); }
        float g = AnyReceptor(a);
        SenseCost(a, Enzyme.Receptor, g);
        if (o == null || g <= 0) return -1;
        return Math.Clamp((int)MathF.Round(Kinship(a, o) * Noisy(g)), 0, 64);
    }

    // A byte of its genome (−1: nobody, or not read).
    public int GeneSense(Agent a, Agent o, int i)
    {
        if (!OrganLaw) { SenseFree(Enzyme.Receptor); return o == null ? -1 : o.G[((i % o.G.Length) + o.G.Length) % o.G.Length]; }
        float g = AnyReceptor(a);
        SenseCost(a, Enzyme.Receptor, g);
        if (o == null || !Caught(g)) return -1;
        return o.G[((i % o.G.Length) + o.G.Length) % o.G.Length];
    }

    // The signal it sends (0: none heard).
    public int Hear(Agent a, Agent o)
    {
        if (!OrganLaw) { SenseFree(Enzyme.Mechano); return o == null ? 0 : o.Signal; }
        bool heard = Touch(a);
        return o != null && heard ? o.Signal : 0;
    }

    // The body's own temperature, °C (model 1: btemp).
    public float BodyTemp(Agent a)
    {
        if (!OrganLaw) { SenseFree(Enzyme.Thermo); return a.Tb; }
        return ReadTemp(a, a.Tb);
    }

    // ---- upkeep (LiveBody) ----

    // A body lived a tick (the census's denominator, both laws).
    void OrganBodyTick() => OrganNote(OsBodies, 1);

    // Organ proteins (motor and senses) cost OrganUpkeep per unit a tick (law 1; 0 otherwise).
    float OrganUpkeepOf(Agent a)
    {
        float n = 0;
        for (int k = 0; k < a.EnzN; k++) if (Enzyme.IsOrgan(a.Enz[k].Kind)) n += a.Enz[k].Amount;
        float cost = n * P.OrganUpkeep;
        OrganNote(OsUpkeep, cost);
        return cost;
    }

    // ---- census (tools/bench) ----

    static readonly string[] OrganKeys = { "motor", "receptor", "photoreceptor", "mechano", "thermo" };
    static readonly string[] HabitatKeys = { "all", "surface", "cave", "water" };
    public static readonly string[] OrganNames = BuildOrganNames();

    static string[] BuildOrganNames()
    {
        var n = new List<string>();
        foreach (var h in HabitatKeys) n.Add("pop_" + h);
        foreach (var o in OrganKeys) foreach (var h in HabitatKeys) n.Add($"has_{o}_{h}");
        foreach (var h in HabitatKeys) n.Add("photoreceptor_amount_" + h);
        foreach (var o in OrganKeys) n.Add($"reads_{o}");
        n.Add("reads_with_organ"); n.Add("sense_energy"); n.Add("organ_upkeep");
        return n.ToArray();
    }

    // Shares of living bodies with each organ (≥ 0.5 units), overall and by where they live (surface,
    // under a roof, in water); mean photoreceptor amount; readings per body and tick since the last call
    // (by sense), the share of them made with an organ, energy of readings and of organ upkeep per body and
    // tick (law 1). Resets the reading counters.
    public double[] OrganCensus()
    {
        var v = new double[OrganNames.Length];
        Span<int> pop = stackalloc int[4];
        Span<float> amount = stackalloc float[5];
        var has = new int[5, 4];
        var photo = new double[4];
        foreach (var a in Agents)
        {
            if (a.Dead) continue;
            int h = InCave(a) ? 2 : InWater(a) ? 3 : 1;
            pop[0]++; pop[h]++;
            amount.Clear();
            for (int k = 0; k < a.EnzN; k++)
                if (Enzyme.IsOrgan(a.Enz[k].Kind)) amount[a.Enz[k].Kind - Enzyme.Motor] += a.Enz[k].Amount;
            for (int o = 0; o < 5; o++)
                if (amount[o] >= 0.5f) { has[o, 0]++; has[o, h]++; }
            photo[0] += amount[2]; photo[h] += amount[2];
        }
        int i = 0;
        for (int h = 0; h < 4; h++) v[i++] = pop[h];
        for (int o = 0; o < 5; o++) for (int h = 0; h < 4; h++) v[i++] = pop[h] > 0 ? (double)has[o, h] / pop[h] : 0;
        for (int h = 0; h < 4; h++) v[i++] = pop[h] > 0 ? photo[h] / pop[h] : 0;
        foreach (var c in ctxs) MergeOrganStats(c.Organ);
        double bodies = Math.Max(1, organStats[OsBodies]), reads = 0, hits = 0;
        for (int o = 0; o < 5; o++) { v[i++] = organStats[OsReads + o] / bodies; reads += organStats[OsReads + o]; hits += organStats[OsHits + o]; }
        v[i++] = reads > 0 ? hits / reads : 0;
        v[i++] = organStats[OsUse] / bodies;
        v[i++] = organStats[OsUpkeep] / bodies;
        Array.Clear(organStats);
        return v;
    }
}
