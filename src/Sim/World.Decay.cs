using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Primordium;

// Spontaneous decay by one Arrhenius law (P.ArrheniusDecay 1; 0 — the separate old laws, bit for bit:
// LooseDecayK on loose matter, DecayK in bodies, nothing in burials and blocks).
//
// Every molecule that has a downhill path of its own relaxes or falls apart at the rate, per tick,
//     k_s = A · exp(−Ea_s / (R·T)) · (1 + DecayWetK · wet)          A = 10^DecayLogA
//     Ea_s / R = (DecayEa + DecayBondEa · Bond[s] / ⟨Bond⟩) · (1 + order · lattice bond / ⟨Bond⟩)
// in whatever pool it lies — loose on the ground (World.CellChem), in a burial, in a block, held in a body:
//  - the path (DecayPaths): of a molecule's two possible downhill moves, its split into the parts it was
//    built from (exothermic, Chemistry.SplitExo) and, for an excited compound, relaxation to its ground
//    state (releasing its Gap), the one that releases more; an excited monomer's split is its relaxation.
//    So an excited compound whose ground state is stable relaxes and keeps its atoms together, fuel
//    (a ground compound with an exothermic split) and an excited fuel fall apart. Nothing else moves.
//  - the barrier is derived from the molecule: a part common to every molecule (DecayEa: what any move
//    of a molecule's atoms costs) and a part by its bond strength (Chemistry.Bond — from the affinities,
//    valences and number of its atoms; an excited state's is weaker) relative to the mean bond of the
//    world's chemistry (Bond is an index without a unit, and worlds' chemistries differ by a factor of
//    three in it; the temperature scale is the same in every world). The common part sets how strongly
//    temperature acts, the bond part how much strong molecules outlive weak ones (all of the barrier from
//    the bond made weak molecules — the air's gas among them — vanish within ticks). In an ordered block
//    or packed burial the molecule is also held by its lattice: the barrier is raised by order (block
//    Order/255, burial Order 0…1) × the lattice's bond (the block's core cohesion, the burial's mean bond)
//    relative to the mean — an ordered block of ordinary bonds doubles it, so rock relaxes many orders of
//    magnitude slower than litter. Loose matter and bodies have no lattice.
//  - T: the cell's surface temperature for loose matter and top blocks; for a burial or a block, the
//    temperature at its depth (World.LocalTemp: the cave climate — the column's mean plus the geothermal
//    gradient — under a roof); for a body its own Tb.
//  - wet = min(1, water + rain) of the column, where water reaches: loose matter, a body, the top block
//    and the burials of the two top levels (where percolating water goes, World.Leach); deeper is dry.
// The products stay in the same pool (a block's go to the burial of that voxel, or to the loose matter
// on top if it was the top block); the energy released is heat in the cell, booked as `loose decay`
// (ground pools) or `body decay` (bodies). Atoms: the same Qty leaves a kind and enters its products;
// blocks and bodies lose whole molecules.
// Rates: loose matter every environment step (World.CellChem, share 1 − e^(−k·EnvEvery)); burials every
// MetamorphEvery ticks, blocks every 8 × MetamorphEvery by rows in turn (DeepDecay, main thread, voxel order; blocks lose whole molecules,
// drawn from Hash32 of the tick and voxel: no random stream); bodies on their own tick (a Poisson count
// of whole molecules from the body's stream).
public sealed partial class World
{
    public static bool ArrheniusLaw => P.ArrheniusDecay != 0;

    // A species' downhill path: its products (To2 −1 for one), the energy released, and its own barrier
    // (Bond / mean Bond); Decays: it has a path.
    public sealed class DecayPaths
    {
        public readonly int[] To1 = new int[Chemistry.S], To2 = new int[Chemistry.S], Heat = new int[Chemistry.S];
        public readonly float[] Barrier = new float[Chemistry.S];
        public readonly bool[] Decays = new bool[Chemistry.S];
        public readonly bool[] DecaysMat = new bool[Chemistry.S + 2];   // by block material (Mat), pristine blocks
        public readonly int[] List;
        public readonly float MeanBond;

        public DecayPaths(Chemistry ch)
        {
            double mean = 0;
            for (int s = 0; s < Chemistry.S; s++) mean += ch.Bond[s];
            MeanBond = (float)Math.Max(1e-6, mean / Chemistry.S);
            var list = new List<int>();
            for (int s = 0; s < Chemistry.S; s++)
            {
                To1[s] = To2[s] = -1;
                int split = ch.SplitExo[s] ? ch.SplitEnergy(s) : 0;
                bool compound = ch.SplitA[s] != Chemistry.Ground(s);
                int relax = ch.Gap[s] > 0 && compound ? ch.Gap[s] : 0;
                if (split <= 0 && relax <= 0) continue;
                if (split >= relax) { To1[s] = ch.SplitA[s]; To2[s] = ch.SplitB[s]; Heat[s] = split; }
                else { To1[s] = Chemistry.Ground(s); Heat[s] = relax; }
                Decays[s] = true; DecaysMat[s + 2] = true;
                Barrier[s] = ch.Bond[s] / MeanBond;
                list.Add(s);
            }
            List = list.ToArray();
        }
    }

    public readonly DecayPaths Decay;
    double decayA = 1, decayLogA = 0;

    // The law's dry rate without a lattice, tabulated per species over temperature (TabStep °C from TabLo,
    // linear between points: relative error ~10⁻⁵, far below anything the batches resolve) — the hot paths
    // (every loose pool every environment step, every body every tick) read it instead of an exponential.
    // Rebuilt at the start of a tick (main thread) when a law of it changed; the parallel phases only read it.
    const float TabLo = -150f, TabStep = 0.125f;
    const int TabN = 3201;   // −150 … +250 °C
    double[][] decayTab;
    float tabLogA = float.NaN, tabEa = float.NaN, tabBondEa = float.NaN;

    void RefreshDecayA()
    {
        if (decayLogA != P.DecayLogA) { decayA = Math.Pow(10, P.DecayLogA); decayLogA = P.DecayLogA; }
        if (decayTab != null && tabLogA == P.DecayLogA && tabEa == P.DecayEa && tabBondEa == P.DecayBondEa) return;
        decayTab ??= new double[Chemistry.S][];
        foreach (int s in Decay.List)
        {
            var t = decayTab[s] ??= new double[TabN];
            for (int j = 0; j < TabN; j++) t[j] = DecayRateWith(decayA, s, TabLo + j * TabStep, 0, 0);
        }
        tabLogA = P.DecayLogA; tabEa = P.DecayEa; tabBondEa = P.DecayBondEa;
    }

    // The tabulated rate (dry, no lattice) at tC; outside the table, the law itself.
    double DryRate(int s, float tC)
    {
        float x = (tC - TabLo) * (1f / TabStep);
        if (!(x >= 0) || x >= TabN - 1) return DecayRateWith(decayA, s, tC, 0, 0);
        int j = (int)x;
        var t = decayTab[s];
        return t[j] + (t[j + 1] - t[j]) * (x - j);
    }

    // The law's rate per tick for species s at tC °C, wetness wet, with `lattice` added to its barrier
    // (order × the lattice's bond, in Bond units). The species must have a path.
    public double DecayRate(int s, float tC, float wet, float lattice = 0) => DecayRateWith(Math.Pow(10, P.DecayLogA), s, tC, wet, lattice);

    double DecayRateWith(double a, int s, float tC, float wet, float lattice)
    {
        double ea = (P.DecayEa + P.DecayBondEa * Decay.Barrier[s]) * (1 + lattice / Decay.MeanBond);
        double kelvin = Math.Max(1.0, tC + 273.15);
        return a * Math.Exp(-ea / kelvin) * WetFactor(wet);
    }

    static double WetFactor(float wet) => 1 + P.DecayWetK * Math.Clamp(wet, 0f, 1f);

    // The share of a pool that decays in dt ticks at rate k (never more than all of it): 1 − e^(−k·dt), by its
    // series while k·dt is small (relative error < 10⁻¹¹).
    static double DecayShare(double k, double dt)
    {
        double x = k * dt;
        return x < 1e-3 ? x * (1 - x * (0.5 - x / 6)) : 1 - Math.Exp(-x);
    }

    float CellWet(int c) => Math.Min(1f, Water[c] + Rain[c]);

    // ---- loose matter (World.CellChem, by rows in parallel: the row's own cells and accumulator) ----

    // One pass over the cell's loose pools (World.CellChem with the law on): each species with a path decays by
    // the share 1 − e^(−k·EnvEvery) of what lies there (products may decay further in the same pass, as with
    // the old law), and the room it all takes is summed as it goes; weathering as with the law off.
    void CellChemArrhenius(int i, int row, int tick = 0, bool weather = true)
    {
        var d = Decay;
        var decays = d.Decays;
        var vol = Chem.Volume;
        int gas = Chem.Gas;
        float t = Temp[i], total = 0;
        double dt = P.EnvEvery * WetFactor(CellWet(i)), heat = 0;
        for (int s = 0; s < Chemistry.S; s++)
        {
            var pool = C[s][i];
            if (pool.Raw <= 0) continue;
            if (s != gas) total += pool.F * vol[s];
            if (!decays[s]) continue;
            double share = DecayShare(DryRate(s, t), dt);
            Qty m = Qty.FromRaw(Math.Min(pool.Raw, (long)(pool.Raw * share)));   // truncated: never more than lies there
            if (m.Raw <= 0) continue;
            C[s][i] = pool - m; C[d.To1[s]][i] += m;
            if (d.To2[s] >= 0) C[d.To2[s]][i] += m;
            heat += m * d.Heat[s];
        }
        if (heat != 0)
        {
            heatIn[i] += (float)heat;
            rowLooseDecay[row] += heat;   // rows are owned by one worker
        }
        if (weather) CellVolumeAndWeather(i, tick, total, TempFactor(t));
    }

    // The decay alone (tests: no weathering draw, the cell's room untouched).
    void LooseDecayArrhenius(int i, int row) => CellChemArrhenius(i, row, 0, false);

    // ---- bodies (their own tick, World.LiveBody) ----

    // Whole molecules of the body that decay this tick: a Poisson count (at most 64) from the body's stream,
    // each drawn by the species' share of the rate. Returns the energy released (into the cell, `body decay`).
    void BodyDecayArrhenius(Agent a, int cell)
    {
        var d = Decay;
        double total = 0, wf = WetFactor(CellWet(cell));
        float tb = a.Tb;
        Span<double> rate = stackalloc double[Chemistry.S];
        foreach (int s in d.List)
        {
            if (a.Inv[s] <= 0) continue;
            rate[s] = a.Inv[s] * DryRate(s, tb) * wf;
            total += rate[s];
        }
        if (total <= 0) return;
        // Poisson by inversion (λ is small almost always; capped so a pathological law cannot stall a tick).
        int n = 0;
        double u = Rng.NextDouble(), p = Math.Exp(-total), cum = p;
        while (u > cum && n < 64) { n++; p *= total / n; cum += p; }
        for (int e = 0; e < n; e++)
        {
            double r = Rng.NextDouble() * total;
            int s = -1;
            foreach (int q in d.List)
            {
                if (rate[q] <= 0) continue;
                s = q;
                if (r < rate[q]) break;
                r -= rate[q];
            }
            if (s < 0 || a.Inv[s] <= 0) continue;   // already gone in this tick's earlier events
            RemoveMol(a, s);
            AddOrSpill(a, d.To1[s], cell);
            if (d.To2[s] >= 0) AddOrSpill(a, d.To2[s], cell);
            heatIn[cell] += d.Heat[s];
            Flows[FBodyDecay] += d.Heat[s];
        }
    }

    // ---- burials and blocks (every MetamorphEvery ticks, main thread) ----

    // Ground decay below the litter since the world was made or loaded (observation only, not saved):
    // energy released in burials and in blocks.
    public double BurialDecayHeat, BlockDecayHeat;

    // Temperature and wetness at level z of column c (a burial's or a block's).
    float DepthWet(int c, int z) => z >= Height[c] - 2 ? CellWet(c) : 0f;

    readonly List<int>[] decayRows;
    const int BlockStride = 8;   // per row: block voxels with something that decays (filled in parallel)

    void DeepDecay()
    {
        double A = decayA;
        var d = Decay;
        double dt = P.MetamorphEvery, heatBurial = 0;
        // Burials, in voxel order (Metamorphose sorted them this pass; recount here: it may not have run).
        burialOrder.Clear();
        foreach (var entry in Buried) burialOrder.Add(entry.Key);
        burialOrder.Sort();
        foreach (int v in burialOrder)
        {
            var b = Buried[v];
            int c = v / Z, z = v % Z;
            UpdateBurialStats(b);
            float lattice = b.Units > 0 ? b.Order * b.Bonds / b.Units : 0;
            float t = LocalTempDeep(c, z), wet = DepthWet(c, z);
            bool changed = false;
            foreach (int s in d.List)
            {
                var pool = b.Matter[s];
                if (pool.Raw <= 0) continue;
                double share = DecayShare(DecayRateWith(A, s, t, wet, lattice), dt);
                Qty m = Qty.FromRaw(Math.Min(pool.Raw, (long)(pool.Raw * share)));
                if (m.Raw <= 0) continue;
                b.Matter[s] = pool - m; b.Matter[d.To1[s]] += m;
                if (d.To2[s] >= 0) b.Matter[d.To2[s]] += m;
                double h = m * d.Heat[s];
                heatIn[c] += (float)h;
                heatBurial += h;
                changed = true;
            }
            if (changed) { b.Dirty = true; MassChanged(v); }
        }
        // Blocks: find candidates by rows in parallel (read only), then take whole molecules in voxel order. Rock
        // decays ~10⁷ times slower than litter: each pass looks at every BlockStride-th row (in turn, by the pass
        // number) with BlockStride times the step — the same expectation for a scan of the crust 8 times cheaper.
        int tick = (int)Tick, pass = (int)(Tick / Math.Max(1, P.MetamorphEvery));
        double blockDt = dt * BlockStride;
        Parallel.For(0, H, y =>
        {
            var list = decayRows[y] ??= new List<int>();
            list.Clear();
            if ((y + pass) % BlockStride != 0) return;
            for (int c = y * W, end = c + W; c < end; c++)
                for (int z = 2, h = Height[c]; z < h; z++)
                {
                    int v = c * Z + z, mat = Mat[v];
                    if (mat < 2) continue;
                    if (Mixed(v, out var counts))
                    {
                        foreach (int s in d.List) if (counts[s] > 0) { list.Add(v); break; }
                    }
                    else if (d.DecaysMat[mat]) list.Add(v);
                }
        });
        double heatBlock = 0;
        Span<int> take = stackalloc int[Chemistry.S];
        for (int y = 0; y < H; y++)
            foreach (int v in decayRows[y])
            {
                int c = v / Z, z = v % Z;
                if (Mat[v] < 2 || z >= Height[c]) continue;   // gone with an earlier voxel of its column
                float lattice = Order[v] / 255f * CoreCohesion(v);
                float t = LocalTempDeep(c, z), wet = DepthWet(c, z);
                bool top = z == Height[c] - 1, any = false;
                bool mixed = Mixed(v, out var counts);
                int salt = 0;
                foreach (int s in d.List)
                {
                    take[s] = 0;
                    int n = mixed ? counts[s] : s == Mat[v] - 2 ? Units[v] : 0;
                    if (n <= 0) continue;
                    double x = n * DecayShare(DecayRateWith(A, s, t, wet, lattice), blockDt);
                    int k = (int)x;
                    if (Hash32.F(tick, v, 7919 + salt++) < x - k) k++;
                    take[s] = Math.Min(n, k);
                    any |= take[s] > 0;
                }
                if (!any) continue;
                foreach (int s in d.List)
                {
                    int k = take[s];
                    if (k <= 0) continue;
                    // Products first (the voxel may go when its last molecule does: its burial then moves down).
                    if (top)
                    {
                        C[d.To1[s]][c] += k;
                        if (d.To2[s] >= 0) C[d.To2[s]][c] += k;
                    }
                    else
                    {
                        var b = BurialAt(v);
                        b.Matter[d.To1[s]] += k;
                        if (d.To2[s] >= 0) b.Matter[d.To2[s]] += k;
                    }
                    double h = (double)k * d.Heat[s];
                    heatIn[c] += (float)h;
                    heatBlock += h;
                    for (int j = 0; j < k && Mat[v] >= 2; j++) TakeVoxelSpecies(v, s);   // World.Design
                }
                if (Mat[v] >= 2) MassChanged(v);
            }
        if (heatBurial + heatBlock != 0) Flows[FLooseDecay] += heatBurial + heatBlock;
        BurialDecayHeat += heatBurial; BlockDecayHeat += heatBlock;
    }

    // The temperature at level z of column c: the cave climate's formula for depth (World.LocalTemp) with the
    // law on; the surface temperature at the top and with the cave law off.
    float LocalTempDeep(int c, int z) => LocalTemp(c, z);
}
