using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Primordium;

public sealed partial class World
{
    // Every block is counted in molecules and is as full as their volume (Chemistry.MatCap of a kind
    // fill a voxel). Pristine rock is all one kind (Mat − 2); mixed blocks — deposits, buildings, rock
    // that got something added — keep a count per kind. Burial stores fractional molecules at depth.
    // Concurrent: agents in different tiles add and read entries at the same time (never the same key).
    readonly ConcurrentDictionary<int, ushort[]> mixtures = new();
    // Per voxel: does it have a mixture / a burial? Most rock has neither, so hot paths check this
    // byte instead of hashing into the dictionaries.
    const byte HasMix = 1, HasBurial = 2;
    readonly byte[] sparse = new byte[N * Z];
    bool Mixed(int v, out ushort[] counts) { counts = null; return (sparse[v] & HasMix) != 0 && mixtures.TryGetValue(v, out counts); }
    bool BurialOf(int v, out Burial b) { b = null; return (sparse[v] & HasBurial) != 0 && Buried.TryGetValue(v, out b); }
    void SetMixture(int v, ushort[] counts) { mixtures[v] = counts; sparse[v] |= HasMix; }
    ushort[] TakeMixture(int v) { sparse[v] &= unchecked((byte)~HasMix); return mixtures.TryRemove(v, out var counts) ? counts : null; }
    public sealed class Burial
    {
        public readonly Qty[] Matter = new Qty[Chemistry.S];
        public float Order, Pressure;
        internal bool Dirty = true;
        internal float Mass, Units, Bonds;
    }
    public readonly ConcurrentDictionary<int, Burial> Buried = new();
    public readonly double[] InteriorInput = new double[Chemistry.ElementCount];
    public long Sediments, Metamorphoses;

    // The rock face of each cell is worked loose by everybody gnawing there — each try adds its
    // effort/P.FaceWork, and the work stays in the face — and slowly by time (P.BiteRate a tick:
    // weathering). A molecule comes out when the face holds e^(barrier − P.FaceBarrier) of work,
    // `barrier` being what holds it in its lattice after the gnawer's protein took its share (bond
    // strength, lattice order, how badly a mix fits — VoxelBarrier): loose deposits and organic mixes
    // give way to little work, ordered crystals of strong bonds to a great deal of it.
    // The progress is kept in molecules of the face being worked (Bite[c], usually below 1): work at
    // one barrier can't be spent at another. Weathering keeps at most P.BiteCap molecules in reserve.
    // The face is one voxel: when the work moves to another level or the block there is another one,
    // the progress starts over, so loosening the surface does not pay for a gnawer deep below.
    // Lazily refilled: only cells being eaten cost anything.
    public readonly float[] Bite = new float[N];
    readonly long[] biteAt = new long[N];
    readonly int[] biteFace = InitFaces();
    readonly byte[] biteMat = new byte[N];
    static int[] InitFaces() { var f = new int[N]; Array.Fill(f, -1); return f; }

    bool TakeBite(int v, float barrier, float effort = 0)
    {
        int c = v / Z;
        if (biteFace[c] != v || biteMat[c] != Mat[v]) { biteFace[c] = v; biteMat[c] = Mat[v]; Bite[c] = 0; biteAt[c] = Tick; }
        float need = MathF.Exp(barrier - P.FaceBarrier), b = Bite[c];
        if (b < P.BiteCap) b = Math.Min(P.BiteCap, b + P.BiteRate * (Tick - biteAt[c]) / need);   // weathering loosens a face only so far
        b += effort / P.FaceWork / need;                                                       // work always counts
        biteAt[c] = Tick;
        if (b < 1) { Bite[c] = b; return false; }
        Bite[c] = b - 1;
        return true;
    }   // Metamorphoses: reactions in buried matter driven by pressure
    readonly float[] cohesionCache = new float[N * Z];

    void UpdateBurialStats(Burial b)
    {
        if (!b.Dirty) return;
        b.Mass = b.Units = b.Bonds = 0;
        for (int s = 0; s < Chemistry.S; s++)
        {
            float m = b.Matter[s].F;
            b.Mass += m * Chem.Mass[s];
            b.Units += m;
            b.Bonds += m * Chem.Bond[s];
        }
        b.Dirty = false;
    }

    // How many molecules of kind s the block holds.
    public int VoxelCount(int v, int s) => Mat[v] < 2 ? 0 : Mixed(v, out var counts) ? counts[s] : s == Mat[v] - 2 ? Units[v] : 0;

    // Room its molecules take, and how full that makes the voxel (1 = full).
    public float VoxelVolume(int v)
    {
        if (Mat[v] < 2) return Mat[v] == Chemistry.Bedrock ? P.VoxelSpace : 0;
        if (!Mixed(v, out var counts)) return Units[v] * Chem.Volume[Mat[v] - 2];
        float vol = 0;
        for (int s = 0; s < Chemistry.S; s++) if (counts[s] > 0) vol += counts[s] * Chem.Volume[s];
        return vol;
    }
    public float Fill(int v) => Math.Min(1f, VoxelVolume(v) / P.VoxelSpace);

    public float VoxelMass(int v)
    {
        float mass = 0;
        if (Mat[v] >= 2)
        {
            if (Mixed(v, out var counts)) { for (int s = 0; s < Chemistry.S; s++) if (counts[s] > 0) mass += Chem.Mass[s] * counts[s]; }
            else mass = Chem.Mass[Mat[v] - 2] * Units[v];
        }
        if (BurialOf(v, out var burial)) { UpdateBurialStats(burial); mass += burial.Mass; }
        return mass;
    }

    float CoreCohesion(int v)
    {
        if (Mat[v] < 2) return Mat[v] == Chemistry.Bedrock ? 1e6f : 0;
        if (cohesionCache[v] > 0) return cohesionCache[v];
        if (!Mixed(v, out var counts)) return Chem.MatCohesion[Mat[v]];
        // Mean bond of its molecules × how well they fit together (pairwise contact over the mix).
        float bond = 0, purity = 0;
        for (int a = 0; a < Chemistry.S; a++)
        {
            if (counts[a] == 0) continue;
            bond += Chem.Bond[a] * counts[a];
            for (int b = 0; b < Chemistry.S; b++)
                if (counts[b] > 0) purity += (float)counts[a] * counts[b] * Chem.Contact[a + 2, b + 2];
        }
        float n = Math.Max(1, (int)Units[v]);
        return cohesionCache[v] = Math.Max(1e-4f, bond / n * purity / (n * n));
    }

    public float VoxelCohesion(int v)
    {
        float core = CoreCohesion(v);
        if (Mat[v] < 2 || !BurialOf(v, out var b)) return core;
        UpdateBurialStats(b);
        return (core * Units[v] + b.Bonds * b.Order * b.Order) / Math.Max(1, Units[v] + b.Units);
    }

    // A block lost part of its cross-section. Its column needs the support solver only if that can
    // matter: the block hangs, holds up a hanging block, or carries a good share of what it can bear.
    void Weakened(int v)
    {
        int c = v / Z;
        Interlocked.Increment(ref TerrainVersion);
        ColumnVersion[c]++;
        compressionCache[v] = 0;
        if (InSupportPath(v) || Pressure[v] > 0.5f * CompressionCapacity(v)) { MarkDirty(c); Interlocked.Increment(ref DirtBy[1]); }
    }

    // Hanging itself, or a block a hanging one rests on (from the side at the same level or from below).
    bool InSupportPath(int v)
    {
        if (overhang[v]) return true;
        int c = v / Z, z = v % Z;
        if (z + 1 < Z && overhang[v + 1]) return true;
        for (int d = 0; d < 4; d++)
        {
            int n = nb[c * 4 + d];
            if (n != c && overhang[n * Z + z]) return true;
        }
        return false;
    }

    // A little loose matter came or went at v (remains in a cave, adsorbed gas). Like Weakened: the
    // solver is woken only where the extra weight can matter.
    void MassChanged(int v)
    {
        if (BurialOf(v, out var burial)) burial.Dirty = true;
        compressionCache[v] = 0;
        if (InSupportPath(v) || Pressure[v] > 0.5f * CompressionCapacity(v)) MarkDirty(v / Z);
    }

    void MatterChanged(int v)
    {
        if (BurialOf(v, out var burial)) burial.Dirty = true;
        compressionCache[v] = 0;
        MarkDirty(v / Z);
        Interlocked.Increment(ref DirtBy[3]);
    }

    float ContactFactor(int a, int b)
    {
        if (Mat[b] == Chemistry.Bedrock) return 0.5f;
        bool mixedA = Mixed(a, out var ca), mixedB = Mixed(b, out var cb);
        if (!mixedA && !mixedB) return Chem.Contact[Mat[a], Mat[b]];
        float sum = 0;
        for (int i = 0; i < Chemistry.S; i++)
        {
            int ni = mixedA ? ca[i] : i == Mat[a] - 2 ? Units[a] : 0;
            if (ni == 0) continue;
            for (int j = 0; j < Chemistry.S; j++)
            {
                int nj = mixedB ? cb[j] : j == Mat[b] - 2 ? Units[b] : 0;
                if (nj > 0) sum += (float)ni * nj * Chem.Contact[i + 2, j + 2];
            }
        }
        return sum / Math.Max(1f, (float)Units[a] * Units[b]);
    }

    // The barrier of a pristine block of aggregate m with lattice order (0..1) — for the panels.
    public float TypicalBarrier(int m, float order) => m < 2 ? Chem.MatBarrier[m]
        : P.RockBarrier * (0.15f + Chem.MatCohesion[m] * Chem.MatCohesion[m]) * (0.35f + order);

    public float VoxelBarrier(int v) => Mat[v] < 2 ? Chem.MatBarrier[Mat[v]]
        : P.RockBarrier * (0.15f + VoxelCohesion(v) * VoxelCohesion(v)) * (0.35f + Order[v] / 255f);

    static int Dominant(ushort[] counts)
    {
        int d = 0;
        for (int s = 1; s < Chemistry.S; s++) if (counts[s] > counts[d]) d = s;
        return d;
    }

    // Lay a block of these molecules into an empty voxel.
    void PutMixture(int v, ushort[] counts, byte order)
    {
        int total = 0;
        foreach (int n in counts) total += n;
        if (total > ushort.MaxValue) throw new InvalidOperationException($"block of {total} molecules does not fit a voxel");
        compressionCache[v] = cohesionCache[v] = 0;
        Mat[v] = Chem.BuiltMat[Dominant(counts)]; Units[v] = (ushort)total; Order[v] = order;
        SetMixture(v, counts);
        int c = v / Z;
        Height[c] = Math.Max(Height[c], v % Z + 1);
        TerrainChanged(c);
    }

    // Matter laid down on a floor (excretion, building material, compacted remains) fills the block
    // the floor is made of until it is full by volume; only what does not fit starts a new block on
    // top (pushing out or lifting whoever stands there; a builder rides up on it). So a few molecules
    // make a thin skin, never a whole block. `floorLevel` is the level one stands on.
    void Deposit(int c, int floorLevel, ushort[] add, byte order, Agent rider = null)
    {
        int total = 0;
        foreach (int n in add) total += n;
        if (total == 0) return;
        int fv = c * Z + floorLevel - 1;
        if (floorLevel >= 3 && Mat[fv] >= 2)
        {
            float room = P.VoxelSpace - VoxelVolume(fv);
            if (room > 0)
            {
                if (!Mixed(fv, out var counts))
                {
                    counts = new ushort[Chemistry.S];
                    counts[Mat[fv] - 2] = Units[fv];
                    SetMixture(fv, counts);
                }
                for (int s = 0; s < Chemistry.S && room > 0; s++)
                {
                    if (add[s] == 0) continue;
                    int fit = Math.Min(add[s], (int)(room / Chem.Volume[s]));
                    fit = Math.Min(fit, ushort.MaxValue - Units[fv]);
                    if (fit <= 0) continue;
                    counts[s] += (ushort)fit; Units[fv] += (ushort)fit; add[s] -= (ushort)fit; total -= fit;
                    room -= fit * Chem.Volume[s];
                }
                Mat[fv] = Chem.BuiltMat[Dominant(counts)];
                compressionCache[fv] = cohesionCache[fv] = 0;
                MassChanged(fv);
                Interlocked.Increment(ref TerrainVersion);
                ColumnVersion[c]++;
            }
        }
        if (total <= 0) return;
        if (floorLevel >= Z - 2 || IsSolid(c, floorLevel) || IsSolid(c, floorLevel + 1))
        {
            SpillLoose(c, floorLevel, add);   // nowhere to lay it: it stays loose where it is (nothing is lost)
            return;
        }
        // A new block takes what fills one voxel by volume; whatever is left lies loose on top of it.
        var block = new ushort[Chemistry.S];
        float space = P.VoxelSpace;
        int laid = 0;
        for (int s = 0; s < Chemistry.S; s++)
        {
            if (add[s] == 0) continue;
            int fit = Math.Min(add[s], (int)(space / Chem.Volume[s]));
            if (fit <= 0) continue;
            block[s] = (ushort)fit; add[s] -= (ushort)fit; laid += fit;
            space -= fit * Chem.Volume[s];
        }
        if (laid == 0) { SpillLoose(c, floorLevel, add); return; }
        bool surface = floorLevel >= Height[c];
        DisplaceOccupants(c, floorLevel, rider);
        PutMixture(c * Z + floorLevel, block, order);
        SpillLoose(c, floorLevel + 1, add);
        if (surface) Repose(c);   // only a new top of the column can slide off; a cave floor is held by its walls
    }

    void SpillLoose(int c, int floorLevel, ushort[] add)
    {
        bool cave = floorLevel < Height[c];
        Qty[] spill = null;
        for (int s = 0; s < Chemistry.S; s++)
        {
            if (add[s] == 0) continue;
            if (!cave) { C[s][c] += add[s]; continue; }
            spill ??= BurialAt(LooseVoxel(c, floorLevel)).Matter;
            spill[s] += add[s];
        }
        if (spill != null) MassChanged(LooseVoxel(c, floorLevel));
    }

    int TakeVoxelMolecule(int v)
    {
        if (Units[v] == 0 || Mat[v] < 2) return -1;
        compressionCache[v] = cohesionCache[v] = 0;
        int s = Mat[v] - 2;
        if (Mixed(v, out var counts))
        {
            int r = Rng.Next(Units[v]);   // a random molecule of the mix
            for (s = 0; s < Chemistry.S - 1 && r >= counts[s]; s++) r -= counts[s];
            counts[s]--;
            if (counts[s] == 0 && Units[v] > 1) Mat[v] = Chem.BuiltMat[Dominant(counts)];
        }
        Units[v]--;
        if (Units[v] == 0) RemoveVoxel(v / Z, v % Z);
        else Weakened(v); // partial mining weakens the remaining cross-section too
        return s;
    }

    void SpillVoxel(int v, Qty[] into = null)
    {
        for (int s = 0; s < Chemistry.S; s++)
        {
            int n = VoxelCount(v, s);
            if (n == 0) continue;
            if (into == null) C[s][v / Z] += n; else into[s] += n;
        }
        RemoveVoxel(v / Z, v % Z);
    }

    Burial BurialAt(int v)
    {
        if (!Buried.TryGetValue(v, out var b)) { b = Buried.GetOrAdd(v, new Burial()); sparse[v] |= HasBurial; }
        b.Dirty = true;
        return b;
    }

    void MoveBurial(int from, int to)
    {
        if (!Buried.TryRemove(from, out var b)) return;
        sparse[from] &= unchecked((byte)~HasBurial);
        var dest = BurialAt(to);
        for (int s = 0; s < Chemistry.S; s++) dest.Matter[s] += b.Matter[s];
        dest.Order = Math.Max(dest.Order, b.Order);
        MatterChanged(from); MatterChanged(to);
    }

    int LooseVoxel(int cell, int level) => cell * Z + Math.Clamp(level - 1, 1, Z - 1);
    float LooseAmount(Agent a, int cell, int s) => Loose(a, cell, FloorBurial(a, cell), s);

    // For loops over species: look the floor's burial up once, then read amounts cheaply.
    Burial FloorBurial(Agent a, int cell) => BurialOf(LooseVoxel(cell, a.Z), out var b) ? b : null;
    float Loose(Agent a, int cell, Burial b, int s)
    {
        float lying = (float)((a.Z >= Height[cell] ? C[s][cell] : Qty.Zero) + (b != null ? b.Matter[s] : Qty.Zero));
        if (s == Chem.Gas && P.CaveGasK > 0 && a.Z < Height[cell]) lying += (float)CaveGas(cell, a.Z);   // the column's air under a roof (World.Resources)
        return lying > 0 && InWater(cell, a.Z) ? lying * Exposure(a, cell, s) : lying;   // in water: only what it reaches (World.Water)
    }

    void ChangeLoose(Agent a, int cell, int s, Qty amount) => ChangeLooseAt(cell, a.Z, s, amount);

    // Loose matter on the floor at `level` of `cell` (the surface or a cave floor).
    void ChangeLooseAt(int cell, int level, int s, Qty amount)
    {
        if (level >= Height[cell])
        {
            if (amount >= 0) { C[s][cell] += amount; return; }
            Qty take = Qty.Min(-amount, C[s][cell]);
            C[s][cell] -= take; amount += take;
            if (amount == 0) return;
        }
        int v = LooseVoxel(cell, level);
        // Under a roof the air's gas is reached through the column (World.Resources): what lies on the
        // cave floor first, the rest from the column's air.
        if (amount < 0 && s == Chem.Gas && P.CaveGasK > 0)
        {
            Qty floor = BurialOf(v, out var fb) ? Qty.Max(Qty.Zero, fb.Matter[s]) : Qty.Zero;
            Qty air = Qty.Min(Qty.Max(Qty.Zero, -amount - floor), C[s][cell]);
            if (air > 0) { C[s][cell] -= air; amount += air; }
            if (amount == 0) return;
        }
        BurialAt(v).Matter[s] += amount;
        MassChanged(v);
    }


    // The budget counts atoms in every reservoir, including partial uptake and enzyme scaffolds.
    // Run only on demand: this diagnostic intentionally scans the whole crust.
    // Summed in integers (whole molecules, and the fractional pools in Qty units), so the reading is
    // exact up to the final conversion to double (~10⁻⁷ of an atom): a drift it shows is real.
    public double[] ElementBudget()
    {
        var whole = new long[Chemistry.S];
        var frac = new long[Chemistry.S];   // Qty.Raw: 2⁻³² molecule
        checked
        {
            for (int s = 0; s < Chemistry.S; s++)
            {
                var cs = C[s];
                for (int c = 0; c < N; c++) frac[s] += cs[c].Raw;
            }
            for (int c = 0; c < N; c++)
                for (int z = 2; z < Height[c]; z++)
                {
                    int v = c * Z + z;
                    if (Mat[v] < 2) continue;
                    if (Mixed(v, out var counts)) { for (int s = 0; s < Chemistry.S; s++) whole[s] += counts[s]; }
                    else whole[Mat[v] - 2] += Units[v];
                }
            foreach (var b in Buried.Values)
                for (int s = 0; s < Chemistry.S; s++) frac[s] += b.Matter[s].Raw;
            foreach (var a in Agents)
                if (!a.Dead)
                {
                    for (int s = 0; s < Chemistry.S; s++) { whole[s] += a.Inv[s]; frac[s] += a.Pend[s].Raw; }
                    for (int k = 0; k < a.EnzN; k++) frac[a.Enz[k].Material] += a.Enz[k].Matter.Raw;
                }
            var atoms = new double[Chemistry.ElementCount];
            for (int e = 0; e < atoms.Length; e++)
            {
                long w = 0, f = 0;
                for (int s = 0; s < Chemistry.S; s++) { w += whole[s] * Chem.Atoms[s, e]; f += frac[s] * Chem.Atoms[s, e]; }
                atoms[e] = (w + (f >> Qty.Bits)) + (f & ((1L << Qty.Bits) - 1)) / Qty.One;
            }
            return atoms;
        }
    }

    // Action/upkeep energy dissipates as heat; energy transfers and endothermic reactions do not.
    void Dissipate(Agent a, float cost)
    {
        float heat = Math.Min(Math.Max(0, a.Energy), Math.Max(0, cost)), before = a.Energy;
        int cell = a.Y * W + a.X;
        heatIn[cell] += heat;
        if (a.Z < Height[cell] && CaveLaw && heat > 0) caveHeatIn[cell] += heat * Cover(cell, a.Z);   // under a roof: into the cave air (World.Cave)
        a.Energy -= cost;
        var f = Flows;
        f[FDissipate] += heat; f[FUnpaid] += cost - heat;   // a cost beyond what it has reaches no cell (see World.Energy)
        f[FRounding] += (double)before - a.Energy - cost;
    }

    // Folded proteins keep their substrate; loss of catalytic activity returns the same substrate.
    void WearProtein(Agent a, int slot, float factor)
    {
        ref var e = ref a.Enz[slot];
        float before = e.Amount;
        e.Amount *= Math.Clamp(factor, 0, 1);
        Qty released = before > 0 ? e.Matter * (1 - e.Amount / before) : e.Matter;
        e.Matter -= released;
        ReturnProteinMatter(a, e.Material, released);
    }

    void ReturnProteinMatter(Agent a, int s, Qty amount)
    {
        // Use the membrane's fractional reservoir; its mass already includes this substrate.
        a.Pend[s] += amount;
        a.Volume += amount.F * (Chem.BodyVolume[s] - Chem.Volume[s]);   // folded it was packed; free, a gas is a bubble again
        while (a.Pend[s] >= 1)
        {
            a.Pend[s] -= 1;
            a.Mass -= Chem.Mass[s];
            a.Volume -= Chem.BodyVolume[s];
            AddOrSpill(a, s, a.Y * W + a.X);
        }
    }

    // Too much loose matter on a floor (by volume, see World.Volume). Off a real slope (a neighbouring
    // floor two or more levels lower) it runs down; on gentle ground it is pressed in place into an
    // aggregate of its actual molecules (no energy turned into matter) — so remains go back to rock.
    void Settle(int c, float volume)
    {
        float limit = P.CompactShare * P.VoxelSpace;
        if (volume <= limit || Height[c] >= Z - 1) return;
        int low = c;
        for (int d = 0; d < 4; d++)
        {
            int j = nb[c * 4 + d];
            if (j != c && Height[j] <= Height[c] - 2 && Height[j] < Height[low]) low = j;
        }
        if (low != c)
        {
            float f = (volume - limit) / volume;
            for (int s = 0; s < Chemistry.S; s++)
            {
                if (s == Chem.Gas) continue;
                Qty m = C[s][c] * f;
                C[s][c] -= m; C[s][low] += m;
            }
            LooseVolume[c] -= volume - limit; LooseVolume[low] += volume - limit;
            return;
        }
        // Press the excess into the floor block: the molecules whose loose bulk is over the limit.
        float share = (volume - limit) / volume;
        var add = new ushort[Chemistry.S];
        int moved = 0;
        for (int s = 0; s < Chemistry.S; s++)
        {
            if (s == Chem.Gas) continue;
            int n = Math.Min((int)(C[s][c] * share), ushort.MaxValue);   // what is not pressed in stays loose
            if (n <= 0) continue;
            C[s][c] -= n; add[s] = (ushort)n; moved += n;
        }
        if (moved == 0) return;
        LooseVolume[c] = Math.Max(0, LooseVolume[c] - (volume - limit));
        Deposit(c, Height[c], add, 12);
        Sediments++; Note(EvKind.Sediment);
    }

    // Columns that may hold a block metamorphism would still anneal (under pressure, order below
    // 245). Set wherever pressure or order can change (RefreshColumn, the solver, TerrainChanged);
    // cleared once a pass finds nothing left to anneal there. Rock nobody touches and that has
    // reached its order is never scanned again.
    readonly bool[] annealable = InitAnnealable();
    static bool[] InitAnnealable() { var a = new bool[N]; Array.Fill(a, true); return a; }

    readonly List<int> burialOrder = new();

    void Metamorphose()
    {
        // Slow lattice annealing has a fixed cadence, independent of whether a body woke a column.
        // Columns are independent: done in parallel.
        System.Threading.Tasks.Parallel.For(0, H, y =>
        {
            for (int c = y * W, end = c + W; c < end; c++)
            {
                if (!annealable[c]) continue;
                bool more = false;
                for (int z = 2, h = Height[c]; z < h; z++)
                {
                    int v = c * Z + z;
                    // Annealing only strengthens a block: refresh its cached strength, nothing can fail from it.
                    if (Mat[v] >= 2 && Pressure[v] > P.CompactionPressure && Order[v] < 245)
                    {
                        Order[v]++; compressionCache[v] = 0;
                        if (Order[v] < 245) more = true;
                    }
                }
                annealable[c] = more;
            }
        });
        Lap(DMetamorph);
        // Burial remains at depth. Pressure changes lattice order and promotes only legal reactions.
        // In voxel order: the concurrent dictionary's own order depends on the history of its table
        // (and on which tile added first), and the heat released adds up per cell.
        burialOrder.Clear();
        foreach (var entry in Buried) burialOrder.Add(entry.Key);
        burialOrder.Sort();
        foreach (int v in burialOrder)
        {
            var b = Buried[v];
            float load = Pressure[v];
            b.Pressure = load;
            float target = load / (load + P.CompactionPressure);
            float delta = Math.Max(0, target - b.Order) * 0.04f * TempFactor(Temp[v / Z]);
            b.Order += delta;
            if (delta > 0) compressionCache[v] = 0;   // more order, more cohesion: stronger, never weaker
            for (int a = 0; a < Chemistry.S; a++)
            {
                if (b.Matter[a] < 1) continue;
                for (int s = a; s < Chemistry.S; s++)
                {
                    int product = Chem.Combine[a, s];
                    if (product < 0 || b.Matter[s] < (a == s ? 2 : 1)) continue;
                    int de = Chem.E[a] + Chem.E[s] - Chem.E[product];
                    if (de < 0 || load < P.CompactionPressure * (0.2f + Chem.Bond[product])) continue;
                    b.Matter[a]--; b.Matter[s]--; b.Matter[product]++;
                    heatIn[v / Z] += de;
                    pressureHeat += de;
                    Metamorphoses++; MatterChanged(v);
                    break;
                }
            }
        }
    }
}
