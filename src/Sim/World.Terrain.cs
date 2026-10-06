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
    public int X, Y, High, Mid, Toxic;
    public float Strength;
    public long Life, Age;
}

public sealed partial class World
{
    // Terrain: column heights and voxels (material + mineral units still locked in the block).
    public readonly int[] Height = new int[N];
    public readonly byte[] Mat = new byte[N * Z];
    public readonly ushort[] Units = new ushort[N * Z];   // molecules in each block (a full one fills its voxel by volume)
    public readonly byte[] Order = new byte[N * Z];
    public readonly int[] ColumnVersion = new int[N];
    public int TerrainVersion;
    public readonly List<Vent> Vents = new();

    readonly float[] diffW = new float[N * 4];
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

    void GenerateTerrain()
    {
        int ns = unchecked(Seed * 31 + 7);
        float asp = H / (float)W;
        var species = Enumerable.Range(0, Chemistry.S).OrderBy(s => Chem.Bond[s]).ToArray();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                float u = x / (float)W, v = y / (float)H;
                float bas = Math.Clamp((Noise.Fbm(ns, u, v, 5, 3, asp) - 0.28f) / 0.44f, 0, 1);
                float rdg = 1 - MathF.Abs(Noise.Fbm(ns + 1, u, v, 4, 5, asp) * 2 - 1);
                float mnt = Smooth(0.35f, 0.7f, Noise.Fbm(ns + 2, u, v, 2, 2, asp));
                int h = Math.Clamp((int)MathF.Round(3 + 13 * bas + 15 * rdg * rdg * rdg * mnt) + Crust, 2 + Crust, Z - 14);
                float domain = Noise.Fbm(ns + 3, u, v, 3, 4, asp);
                for (int z = 0; z < h; z++)
                {
                    int q = i * Z + z;
                    if (z < 2) { Mat[q] = Chemistry.Bedrock; Order[q] = 255; continue; }
                    float depth = (h - z - 1f) / h;
                    float vein = Noise.Value3(ns + 5, x / 10f, y / 10f, z / 4f, W / 10);
                    // Pressure selects denser, more cohesive aggregates; correlated domains make seams.
                    float rank = Math.Clamp(depth * 0.65f + domain * 0.35f + (vein - 0.5f) * 0.5f, 0, 0.999f);
                    int molecule = species[(int)(rank * species.Length)];
                    Mat[q] = Chem.BuiltMat[molecule]; Units[q] = (ushort)Chem.MatCap[Chem.BuiltMat[molecule]];
                    Order[q] = (byte)(255 * Math.Clamp(0.12f + depth * 0.75f + Chem.Packing[molecule] * 0.15f, 0, 1));
                }
                Height[i] = h;
                structuralDirty.Add(i);
            }
    }

    // Ground under the lowest surface (above the two levels of bedrock). The relief on top of it is
    // what it always was (valleys to mountains ~25 levels: bodies climb one level a step, a steeper
    // world would be walls); the ground beneath is four times as thick as in the first worlds —
    // columns ~72 levels deep on average, room for caves, shafts and deep strata.
    public const int Crust = 60;

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
        for (int k = 0; k < 128; k++)
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

    void SpawnVent()
    {
        for (int t = 0; t < 300; t++)
        {
            int x = Rng.Next(W), y = Rng.Next(H / 8, H - H / 8), i = y * W + x;
            if (Height[i] > Crust + 9 || Count[i] > 0) continue;   // in the lowlands (up to 9 levels above the lowest ground)
            bool far = true;
            foreach (var o in Vents)
            {
                int dx = Math.Abs(o.X - x);
                dx = Math.Min(dx, W - dx);
                if (dx * dx + (o.Y - y) * (o.Y - y) < 45 * 45) far = false;
            }
            if (!far) continue;
            Vents.Add(new Vent
            {
                X = x, Y = y,
                Strength = 0.6f + 0.4f * (float)Rng.NextDouble(),
                Life = Rng.Next(60000, 160000),
                High = Chem.VentHigh[Rng.Next(Chem.VentHigh.Length)],
                Mid = Chem.VentMid[Rng.Next(Chem.VentMid.Length)],
                Toxic = Chem.Toxic.Length > 0 ? Chem.Toxic[Rng.Next(Chem.Toxic.Length)] : Chem.Gas,
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
        if (Vents.Count < P.VentCount && Tick >= nextVentAt)
        {
            SpawnVent();
            nextVentAt = Tick + Rng.Next(3000, 15000);
        }
        if (ventsDirty) { RecomputeVentFields(); ventsDirty = false; }
    }

    void RecomputeVentFields()
    {
        Array.Clear(Ash);
        Array.Clear(ventHeat);
        foreach (var v in Vents)
            for (int dy = -26; dy <= 26; dy++)
            {
                int y = v.Y + dy;
                if (y < 0 || y >= H) continue;
                for (int dx = -26; dx <= 26; dx++)
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
        int molecule = Chem.VentHigh[Rng.Next(Chem.VentHigh.Length)];
        byte m = Chem.BuiltMat[molecule];
        // The interior delivers matter at a steady rate: a block of many small molecules takes longer.
        if (Rng.NextDouble() >= 300.0 / Chem.MatCap[m]) return;
        DisplaceOccupants(best, Height[best]);
        Mat[w] = m; Units[w] = (ushort)Chem.MatCap[m]; Order[w] = 35;
        for (int e = 0; e < Chemistry.ElementCount; e++) InteriorInput[e] += Chem.MatCap[m] * Chem.Atoms[molecule, e];
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
