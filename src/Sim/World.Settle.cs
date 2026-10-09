using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Primordium;

// Settling of a column as a whole: elastic compression of the lattice, consolidation and rebound.
//
// A block under vertical stress σ is squeezed: its lattice takes (1 − ε) of the room it takes unloaded,
// strain ε = σ / E. The stiffness E is not a table: it is the block's uniaxial strength times the
// modulus ratio (P.ModulusRatio, E/UCS ≈ 100–500 for rock), so it comes from the same bonds, lattice
// order and mixture contacts as the strength (World.CompressionCapacity), and a block that bears its
// load is strained at most 1/ModulusRatio — the strain at which rock fails. A disordered heap, which
// bears its load only by friction, is as soft as that limit allows.
//
// What packs a column — compaction of a stratum by pressure (Metamorphose orders it and its molecules
// take less room) or a heavier load squeezing it — makes room inside the grounded stack; unloading takes
// squeezed room away. Each column keeps the sum of both (settlePending, a volume). Sub-voxel shifts are
// below what the grid resolves, so nothing moves until that sum reaches a whole voxel:
// - packed by a voxel or more: the column settles as a whole by one level. The porous block with the
//   most room (one that has room ≥ 2% of a voxel by its squeezed space) gives its molecules to the other
//   porous blocks of the stack, nearest first, and its voxel is closed by lowering everything above it in
//   the grounded stack by one level, block by block (whole voxels: strata keep their makeup). The shifts
//   of the full blocks between the porous ones are rounded away.
// - unloaded by a voxel or more: the column springs back by one level (elastic rebound returns what
//   loading consolidated): over the highest block holding more than its room, everything above is raised
//   by one level, and the new voxel takes the excess of the over-full blocks, nearest first.
// Nothing happens without the room (or the excess) really being there: the pending sum then waits.
// Room left by gnawing is never the reason a column settles (only compaction and strain count), though
// once it settles a gnawed pore can take molecules. Rock made with the world is already in equilibrium
// under its own overburden (the first look only records its strain). Atoms are exact; whatever comes
// down gives its weight × one level to heat, as a falling block does (impact, outside the chemical
// ledger); a rise takes the same from the strain it releases (not booked: elastic energy is not a pool).
public sealed partial class World
{
    readonly float[] settleDebt;           // pending settling of the grounded stack (volume; < 0: rebound)
    readonly float[] elasticSeen;      // elastic room of the grounded stack at the last look (NaN: never looked at)
    readonly bool[] settleCheck;             // pressure or packing changed: look again in the next pass
    readonly bool[] settleDue;
    readonly List<int> settleList = new();
    public long SettledMolecules, ReboundMolecules, SettleEvents;
    public long LedgeClimbs;   // bodies that got onto a ledge more than a block up (World.Move)
    float[] InitElasticSeen() { var a = new float[N]; Array.Fill(a, float.NaN); return a; }

    // Elastic strain of a block: σ / (ModulusRatio × strength), never above 1/ModulusRatio (a block loaded
    // past its strength fails rather than squeezing further).
    public float Strain(int v)
    {
        float p = Pressure[v];
        if (p <= 0 || Mat[v] < 2) return 0;
        return p / (P.ModulusRatio * Math.Max(CompressionCapacity(v), p));
    }

    // What fits in the voxel of a squeezed block: the voxel's room over (1 − strain).
    float SqueezedSpace(int v) => P.VoxelSpace / (1 - Strain(v));

    // Top of the grounded stack as it is now: the first voxel from level 2 up that is not a block (the
    // solver's `grounded` may be a pass old).
    int Stack(int c)
    {
        int z = 2;
        while (z < Z && Mat[c * Z + z] >= 2) z++;
        return z;
    }

    // The room squeezing makes in the column's grounded stack (above the bedrock).
    float ElasticRoom(int c)
    {
        float room = 0;
        for (int z = 2, g = Stack(c); z < g; z++) room += SqueezedSpace(c * Z + z) - P.VoxelSpace;
        return room;
    }

    // After metamorphism (every MetamorphEvery ticks): columns whose pressure or packing changed are
    // weighed in parallel (reads, and strength caches of their own voxels only), and those due settle or
    // spring back by a level one after another in column order.
    void SettleColumns()
    {
        float voxel = P.VoxelSpace;
        Parallel.For(0, H, y =>
        {
            for (int c = y * W, end = c + W; c < end; c++)
            {
                settleDue[c] = false;
                if (!settleCheck[c]) continue;
                settleCheck[c] = false;
                float room = ElasticRoom(c);
                if (!float.IsNaN(elasticSeen[c])) settleDebt[c] += room - elasticSeen[c];   // the first look: born in equilibrium
                elasticSeen[c] = room;
                settleDue[c] = MathF.Abs(settleDebt[c]) >= voxel;
            }
        });
        settleList.Clear();
        for (int c = 0; c < N; c++) if (settleDue[c]) settleList.Add(c);
        foreach (int c in settleList)
        {
            bool done = settleDebt[c] > 0 ? Subside(c) : Heave(c);
            if (done) { SettleEvents++; settleDebt[c] -= MathF.Sign(settleDebt[c]) * voxel; }
            else settleDebt[c] = Math.Clamp(settleDebt[c], -voxel, voxel);   // no room (or excess) yet: it waits
            settleCheck[c] = true;   // its pressures change: look again
        }
    }

    // Takes up to `vol` of block `from`'s molecules (by its own packing) in proportion to its makeup, into
    // `into`. Returns how many.
    int Extract(int from, float vol, ushort[] into, int limit = int.MaxValue)
    {
        if (vol <= 0 || Mat[from] < 2 || Units[from] == 0) return 0;
        var src = Counts(from);
        int total = Units[from];
        float each = VoxelVolume(from) / total;
        int want = Math.Min(Math.Min(total, limit), (int)(vol / each));
        if (want <= 0) return 0;
        int moved = 0;
        for (int s = 0; s < Chemistry.S && moved < want; s++)
        {
            if (src[s] == 0) continue;
            int n = Math.Min(src[s], Math.Min(want - moved, (int)((long)src[s] * want / total) + 1));
            src[s] -= (ushort)n; into[s] += (ushort)n; moved += n;
        }
        Units[from] -= (ushort)moved;
        if (Units[from] > 0) Mat[from] = Chem.BuiltMat[Dominant(src)];
        Touched(from);
        return moved;
    }

    // Adds molecules to solid block `to` (they join its lattice).
    void Deposit(int to, ushort[] counts)
    {
        var dst = Counts(to);
        int total = Units[to];
        for (int s = 0; s < Chemistry.S; s++) { dst[s] += counts[s]; total += counts[s]; }
        Units[to] = (ushort)total;
        Mat[to] = Chem.BuiltMat[Dominant(dst)];
        Touched(to);
    }

    void Touched(int v)
    {
        compressionCache[v] = cohesionCache[v] = 0;
        if (BurialOf(v, out var b)) b.Dirty = true;
    }

    // A block's molecule counts as a mixture (a pure block becomes one with a single kind).
    ushort[] Counts(int v)
    {
        if (Mixed(v, out var counts)) return counts;
        counts = new ushort[Chemistry.S];
        counts[Mat[v] - 2] = Units[v];
        SetMixture(v, counts);
        return counts;
    }

    float Room(int v) => SqueezedSpace(v) - VoxelVolume(v);

    // The column comes down by a level: see the top of the file.
    bool Subside(int c)
    {
        int g = Stack(c);
        if (g <= 3) return false;
        float least = 0.02f * P.VoxelSpace, total = 0;
        int p = -1;
        for (int z = 2; z < g; z++)
        {
            float r = Room(c * Z + z);
            if (r < least) continue;
            total += r;
            p = z;   // the highest: what is above it settles into the room below
        }
        if (p < 0 || total < P.VoxelSpace) return false;
        int pv = c * Z + p;
        // p's molecules go to the other porous blocks, nearest first.
        var buf = new ushort[Chemistry.S];
        for (int d = 1; d < g && Units[pv] > 0; d++)
            for (int side = -1; side <= 1 && Units[pv] > 0; side += 2)
            {
                int z = p + side * d;
                if (z < 2 || z >= g) continue;
                int q = c * Z + z;
                float r = Room(q);
                if (r < least) continue;
                Array.Clear(buf);
                // what fits in q by q's packing: take from p by count, the same molecules
                float each = 0;
                var src = Counts(pv);
                for (int s = 0; s < Chemistry.S; s++) if (src[s] > 0) each += src[s] * PackedVolume(s, Order[q]);
                each /= Units[pv];
                int n = Extract(pv, float.MaxValue, buf, (int)(r / each));
                if (n == 0) continue;
                Deposit(q, buf);
                Moved(c, n, buf, side < 0 ? d : 0);
            }
        if (Units[pv] > 0 && VoxelVolume(pv) <= least)
        {
            // A few molecules left over (granularity): into the block under it, a sliver over its room.
            int under = p > 2 ? pv - 1 : pv + 1;
            if (under % Z < g)
            {
                Array.Clear(buf);
                int n = Extract(pv, float.MaxValue, buf);
                Deposit(under, buf);
                Moved(c, n, buf, under < pv ? 1 : 0);
            }
        }
        if (Units[pv] > 0) { ColumnSettled(c); return false; }   // it could not give everything: it waits
        // Close p: everything above it in the grounded stack comes down one level.
        TakeMixture(pv);
        if ((sparse[pv] & HasBurial) != 0) MoveBurial(pv, pv - 1);
        Mat[pv] = 0; Units[pv] = 0; Order[pv] = 0; compressionCache[pv] = cohesionCache[pv] = 0;
        double mass = 0;
        for (int z = p + 1; z < g; z++) { mass += VoxelMass(c * Z + z); TransferVoxel(c * Z + z, c * Z + z - 1, false); }
        float heat = (float)(mass * P.Gravity);
        heatIn[c] += heat;
        Flows[FImpact] += heat;
        TrimColumn(c);
        TerrainChanged(c);
        return true;
    }

    // The column springs back by a level: see the top of the file.
    bool Heave(int c)
    {
        int g = Stack(c);
        if (g <= 2 || g >= Z - 1 || Mat[c * Z + g] != Chemistry.Air) return false;
        float least = 0.02f * P.VoxelSpace, total = 0;
        int q = -1;
        for (int z = 2; z < g; z++)
        {
            float over = -Room(c * Z + z);
            if (over < least) continue;
            total += over;
            q = z;   // the highest
        }
        if (q < 0 || total < P.VoxelSpace) return false;
        // Raise everything above q by one level (from the top down), then fill the new voxel at q + 1.
        DisplaceOccupants(c, g);
        for (int z = g - 1; z > q; z--) TransferVoxel(c * Z + z, c * Z + z + 1, false);
        var into = new ushort[Chemistry.S];
        int n = 0;
        for (int d = 0; d < g; d++)
            for (int side = -1; side <= 1; side += 2)
            {
                if (d == 0 && side > 0) continue;
                int z = q + side * d;
                if (z < 2 || z > q) continue;   // the blocks above moved up one: the over-full ones are at q and below
                float over = -Room(c * Z + z);
                if (over >= least) n += Extract(c * Z + z, over, into);
            }
        if (n > 0) PutMixture(c * Z + q + 1, into, Order[c * Z + q]);   // the same lattice, sprung back (TerrainChanged)
        ReboundMolecules += n;
        TerrainChanged(c);
        return true;
    }

    // Molecules moved within column c, `levels` levels down (0: up or level): their weight × that to heat.
    void Moved(int c, int n, ushort[] counts, int levels)
    {
        SettledMolecules += n;
        if (levels <= 0) return;
        double mass = 0;
        for (int s = 0; s < Chemistry.S; s++) mass += counts[s] * (double)Chem.Mass[s];
        float heat = (float)(mass * P.Gravity * levels);
        heatIn[c] += heat;
        Flows[FImpact] += heat;
    }

    void ColumnSettled(int c)
    {
        Interlocked.Increment(ref TerrainVersion);
        ColumnVersion[c]++;
        MarkDirty(c);
    }
}
