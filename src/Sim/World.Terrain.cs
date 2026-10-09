using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Primordium;

// A hot spot heats the crust and builds aggregates from the external interior reservoir.
// Every injected element is recorded in InteriorInput. Vents die out and open elsewhere.
public sealed class Vent
{
    // High, Mid, Toxic: species drawn when the vent opened, kept in the save format (version ≤ current)
    // but read by no law (the cone is built from VentMolecule / VentHigh). Toxic is now
    // an excited species drawn at random (there is no poison class any more; the draw keeps the stream).
    public int X, Y, High, Mid, Toxic;
    public float Strength;
    public long Life, Age;
}

public sealed partial class World
{
    // Terrain: column heights and voxels (material + mineral units still locked in the block).
    public readonly int[] Height;
    public readonly byte[] Mat;
    public readonly ushort[] Units;   // molecules in each block (a full one fills its voxel by volume)
    public readonly byte[] Order;
    public readonly int[] ColumnVersion;
    public int TerrainVersion;
    public readonly List<Vent> Vents = new();

    readonly float[] diffW;
    bool flowDirty = true, ventsDirty;
    long nextVentAt;

    public byte TopMat(int i) => Height[i] > 0 ? Mat[i * Z + Height[i] - 1] : Chemistry.Air;

    void TerrainChanged(int cell)
    {
        Interlocked.Increment(ref TerrainVersion);
        ColumnVersion[cell]++;
        topologyVersion[cell]++;
        annealable[cell] = true;
        int floor = 0;
        while (floor < Height[cell] && IsSolid(cell, floor)) floor++;
        HasCavity[cell] = floor < Height[cell];
        flowDirty = true;
        MarkDirty(cell);
        for (int d = 0; d < 4; d++) MarkDirty(nb[cell * 4 + d]);
        Interlocked.Increment(ref DirtBy[0]);
    }

    // The height a column is made with (the relief from the seed): valleys, ridges, mountains.
    int GenHeight(int x, int y)
    {
        int ns = unchecked(Seed * 31 + 7);
        float asp = H / (float)W, u = x / (float)W, v = y / (float)H;
        float bas = Math.Clamp((Noise.Fbm(ns, u, v, 5, 3, asp) - 0.28f) / 0.44f, 0, 1);
        float rdg = 1 - MathF.Abs(Noise.Fbm(ns + 1, u, v, 4, 5, asp) * 2 - 1);
        float mnt = Smooth(0.35f, 0.7f, Noise.Fbm(ns + 2, u, v, 2, 2, asp));
        float k = reliefScale;   // the relief is stretched as a whole (P.ReliefScale): the same map, k times the rise
        return Math.Clamp((int)MathF.Round(3 + 13 * k * bas + 15 * k * rdg * rdg * rdg * mnt) + Crust, 2 + Crust, Z - 14);
    }

    // The relief scale this world was made with (P.ReliefScale when it was made; 1 for worlds saved
    // before it existed). Lowlands and the reference height of the altitude climate follow it.
    float reliefScale = 1;
    // Top of the lowlands: 9 levels over the lowest ground at the first relief (hot springs open there).
    int LowlandTop => Crust + 3 + (int)MathF.Round(6 * reliefScale);
    // The height the altitude climate is measured from (11 over the lowest ground at the first relief).
    float LapseBase => Crust + 3 + 8 * reliefScale;
    // Cooling per level: P.TLapse is per level of the first relief, so a stretched relief keeps its span.
    float Lapse => P.TLapse / reliefScale;

    void GenerateTerrain()
    {
        int ns = unchecked(Seed * 31 + 7);
        float asp = H / (float)W;
        var species = Enumerable.Range(0, Chemistry.S).OrderBy(s => Chem.Bond[s]).ToArray();
        geoTables = GeoOn ? BuildGeoTables(species, ns) : null;   // the depth profile (World.Geochem)
        // Columns are independent: rows in parallel (pure functions of the seed), the solver's list after.
        Parallel.For(0, H, () => new float[Chemistry.S], (y, _, weights) =>
        {
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                float u = x / (float)W, v = y / (float)H;
                int h = Height0[i];
                float domain = Noise.Fbm(ns + 3, u, v, 3, 4, asp);
                for (int z = 0; z < h; z++)
                {
                    int q = i * Z + z;
                    if (z < 2) { Mat[q] = Chemistry.Bedrock; Order[q] = 255; continue; }
                    float depth = (h - z - 1f) / h;
                    float vein = Noise.Value3(ns + 5, x / 10f, y / 10f, z / 4f, W / 10);
                    // Pressure selects denser, more cohesive aggregates; correlated domains make seams.
                    float rank = Math.Clamp(depth * 0.65f + domain * 0.35f + (vein - 0.5f) * 0.5f, 0, 0.999f);
                    int molecule = GeoOn ? GeoPick(x, y, z, h - z - 1, rank, weights) : species[(int)(rank * species.Length)];
                    // The chemistry from bonds (Chemistry.Model 1): rock is old, relaxed matter — a stratum
                    // drawn as an excited state is laid as its ground state (same atoms; the excitation was
                    // shed before the world began). The legacy chemistry keeps its excited strata.
                    if (Chem.Model != 0) molecule = Chemistry.Ground(molecule);
                    Order[q] = (byte)(255 * Math.Clamp(0.12f + depth * 0.75f + Chem.Packing[molecule] * 0.15f, 0, 1));
                    Mat[q] = Chem.BuiltMat[molecule]; Units[q] = (ushort)BlockCapacity(molecule, Order[q]);   // full at its packing
                }
                Height[i] = h;
            }
            return weights;
        }, _ => { });
        for (int i = 0; i < N; i++) structuralDirty.Add(i);
    }

    // Ground under the lowest surface (above the two levels of bedrock). The relief on top of it is
    // ~28 levels from valleys to mountains times P.ReliefScale (4: ~115 levels; bodies climb ledges by
    // their momentum, World.Move); the ground beneath is four times as thick as in the first worlds —
    // room for caves, shafts and deep strata. 60 in a world of 192 levels; the same share (5/16) of a
    // world of other height.
    public readonly int Crust;
    public static int CrustFor(int levels) => levels * 5 / 16;

    // Removing a voxel leaves a real cavity. Only the support solver can move its roof.
    void RemoveVoxel(int c, int z)
    {
        int v = c * Z + z;
        compressionCache[v] = cohesionCache[v] = 0;
        Mat[v] = 0; Units[v] = 0; Order[v] = 0;
        TakeMixture(v);
        if ((sparse[v] & HasBurial) != 0)
        {
            int floor = Math.Max(1, z - 1);
            while (floor > 1 && !IsSolid(c, floor)) floor--;
            MoveBurial(v, c * Z + floor);
        }
        TrimColumn(c);
        TerrainChanged(c);
    }

    void TrimColumn(int c)
    {
        while (Height[c] > 0 && Mat[c * Z + Height[c] - 1] == Chemistry.Air) Height[c]--;
    }

    // Gas spreads freely between columns of similar height and barely across a wall.
    void RecomputeFlow()
    {
        flowDirty = false;
        Parallel.For(0, H, y =>
        {
            for (int i = y * W, end = i + W; i < end; i++)
                for (int d = 0; d < 4; d++)
                {
                    int j = nb[i * 4 + d], k = i * 4 + d;
                    diffW[k] = j == i ? 0 : Math.Abs(Height[i] - Height[j]) <= 1 ? 1f : 0.15f;
                }
        });
    }

    // Surface creep is also a transfer, never deletion. Cohesion/order set the angle of repose.
    void Erode()
    {
        for (int k = 0, tries = ErodeTries(); k < tries; k++)
        {
            int i = Rng.Next(N), h = Height[i];
            if (h <= 2) continue;
            int v = i * Z + h - 1;
            float cohesion = VoxelCohesion(v) * (0.2f + Order[v] / 255f);
            int best = -1, bh = h - 2 - (int)(cohesion * 3);
            for (int d = 0; d < 4; d++)
            {
                int j = nb[i * 4 + d];
                if (j != i && Height[j] <= bh) { bh = Height[j]; best = j; }
            }
            if (best >= 0) { impactSource = 2; DropVoxel(v, best * Z + Height[best]); }
        }
    }

    // 128 columns a pass on the default world, in proportion to the area on another (a fraction is
    // a chance, so the rate per column is the same).
    int ErodeTries()
    {
        if (N == RefN) return 128;
        long q = 128L * N;
        int k = (int)(q / RefN);
        return q % RefN != 0 && Rng.Next(RefN) < q % RefN ? k + 1 : k;
    }

    // How many volcanoes the world keeps: P.VentCount on the default world, by area on another.
    int VentTarget => PerArea(P.VentCount);
    // Volcanoes open at least this far from each other: 45 columns, less on a world too small for it.
    int VentGap => Math.Min(45, Math.Min(W, H) * 45 / WorldSettings.DefaultHeight);

    void SpawnVent()
    {
        for (int t = 0; t < 300; t++)
        {
            int x = Rng.Next(W), y = Rng.Next(H / 8, H - H / 8), i = y * W + x;
            if (Height[i] > LowlandTop || Count[i] > 0) continue;   // in the lowlands (9 levels above the lowest ground at the first relief)
            bool far = true;
            foreach (var o in Vents)
            {
                int dx = Math.Abs(o.X - x);
                dx = Math.Min(dx, W - dx);
                if (dx * dx + (o.Y - y) * (o.Y - y) < VentGap * VentGap) far = false;
            }
            if (!far) continue;
            Vents.Add(new Vent
            {
                X = x, Y = y,
                Strength = 0.6f + 0.4f * (float)Rng.NextDouble(),
                Life = Rng.Next(60000, 160000),
                High = Chem.VentHigh[Rng.Next(Chem.VentHigh.Length)],
                Mid = Chem.VentMid[Rng.Next(Chem.VentMid.Length)],
                Toxic = Chem.Excited[Rng.Next(Chem.Excited.Length)],   // kept for the save format; no law reads it (see Vent)
            });
            ventsDirty = true;
            return;
        }
    }

    void StepVents()
    {
        for (int k = Vents.Count - 1; k >= 0; k--)
        {
            var v = Vents[k];
            v.Age++;
            if (--v.Life > 0) continue;
            Vents.RemoveAt(k);
            ventsDirty = true;
            nextVentAt = Tick + Rng.Next(3000, 15000);
        }
        if (Vents.Count < VentTarget && Tick >= nextVentAt)
        {
            int vents = Vents.Count;
            SpawnVent();
            nextVentAt = Tick + Rng.Next(3000, 15000);
            if (Vents.Count > vents) { var v = Vents[^1]; Add(EvType.Climate, Loc.Both($"a volcano awoke at ({v.X}, {v.Y}), strength {v.Strength:0.00}", $"проснулся вулкан ({v.X}, {v.Y}), сила {v.Strength:0.00}"), null, v.Strength, false, null, v.X, v.Y); }
        }
        if (ventsDirty) { RecomputeVentFields(); ventsDirty = false; }
        MaybeMegaEruption();   // the climate cycles' volcanic winters (World.ClimateCycles; nothing with the law off)
    }

    void RecomputeVentFields()
    {
        Array.Clear(Ash);
        Array.Clear(ventHeat);
        foreach (var v in Vents)
            for (int dy = -26, rx = AroundX(26); dy <= 26; dy++)
            {
                int y = v.Y + dy;
                if (y < 0 || y >= H) continue;
                for (int dx = -rx; dx <= rx; dx++)
                {
                    int i = y * W + ((v.X + dx) % W + W) % W;
                    float d2 = dx * dx + dy * dy;
                    Ash[i] += 0.92f * v.Strength * MathF.Exp(-d2 / (2 * 9f * 9f));
                    ventHeat[i] += 0.9f * v.Strength * MathF.Exp(-d2 / (2 * 3.5f * 3.5f));
                }
            }
        for (int i = 0; i < N; i++) Ash[i] = Math.Min(0.97f, Ash[i]);
    }

    // Energetic molecular aggregates from the interior; there are no special volcanic rock types.
    void BuildCone(Vent v)
    {
        int c = v.Y * W + v.X, best = c;
        for (int d = 0; d < 4; d++)
        {
            int j = nb[c * 4 + d];
            if (Height[j] < Height[best] - 1) best = j;
        }
        if (Height[best] >= Z - 3) return;
        int w = best * Z + Height[best];
        int molecule = GeoOn ? VentMolecule(Rng.NextDouble()) : Chem.VentHigh[Rng.Next(Chem.VentHigh.Length)];   // the interior's makeup (World.Geochem)
        byte m = Chem.BuiltMat[molecule];
        // The interior delivers matter at a steady rate: a block of many small molecules takes longer.
        if (Rng.NextDouble() >= 300.0 / Chem.MatCap[m]) return;
        DisplaceOccupants(best, Height[best]);
        int n = BlockCapacity(molecule, 35);   // fresh, poorly packed lava rock fills its voxel
        Mat[w] = m; Units[w] = (ushort)n; Order[w] = 35;
        for (int e = 0; e < Chemistry.ElementCount; e++) InteriorInput[e] += (double)n * Chem.Atoms[molecule, e];
        Flows[FVent] += (double)n * Chem.E[molecule];
        Height[best]++;
        TerrainChanged(best);
        Repose(best);
    }

    // Angle of repose: the top block of a column slides onto a neighbour lying lower than it can
    // stand above — the same rule as surface creep (Erode): steeper for cohesive, well-ordered rock,
    // almost flat for loose fresh deposits. Whoever stands where it lands is lifted onto it. Called
    // after a block is laid on top; at most a few steps (during the agent phase writes must stay near).
    void Repose(int c)
    {
        for (int step = 0; step < 4; step++)
        {
            int h = Height[c];
            if (h <= 3 || !IsSolid(c, h - 2)) return;   // a block over a cavity is the support solver's business
            int v = c * Z + h - 1;
            if (Mat[v] < 2) return;
            float hold = VoxelCohesion(v) * (0.2f + Order[v] / 255f);
            int best = -1, bh = h - 2 - (int)(hold * 3);
            for (int d = 0; d < 4; d++)
            {
                int j = nb[c * 4 + d];
                if (j != c && Height[j] <= bh) { bh = Height[j]; best = j; }
            }
            if (best < 0) return;
            int level = Height[best];
            DisplaceOccupants(best, level);
            TransferVoxel(v, best * Z + level, false);
            c = best;
        }
    }
}
