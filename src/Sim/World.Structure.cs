using System;
using System.Collections.Generic;
using System.Threading;

namespace Primordium;

public sealed partial class World
{
    readonly HashSet<int> structuralDirty = new();
    // Solid voxels above a cavity (not resting on a solid column from the bottom), and how many each
    // column has: columns without any are skipped when looking for hanging rock.
    readonly bool[] overhang = new bool[N * Z];
    readonly int[] overhangCount = new int[N];
    int overhangTotal;
    readonly int[] grounded = new int[N], parent = new int[N * Z];
    readonly float[] support = new float[N * Z], carried = new float[N * Z], edgeCapacity = new float[N * Z];
    public readonly float[] Pressure = new float[N * Z];
    Dictionary<int, float> bodyLoad = new(), nextBodyLoad = new();
    readonly Dictionary<int, float> rootLoad = new();
    readonly float[] compressionCache = new float[N * Z];
    readonly int[] topologyVersion = new int[N];
    public readonly bool[] HasCavity = new bool[N];
    readonly PriorityQueue<int, (float, int)> supportQueue = new();
    readonly List<int> supportOrder = new(), dirtyWork = new(), failures = new();
    readonly HashSet<int> activeHanging = new(), activeColumns = new();
    readonly Queue<int> hangingQueue = new();
    public long CollapsedBlocks, CrushedBlocks;
    public int DeathsBuried;
    public int LastStructureColumns, LastStructureVoxels;
    public long StructureVisits;
    public readonly long[] DirtBy = new long[4];
    public readonly long[] BuriedBy = new long[4];   // diagnostics: entombed in place, roof collapse, slope creep, dumped by dig
    [ThreadStatic] static int impactSource;   // diagnostics: columns marked by geometry, weakening, body loads, deaths/matter
    public int UnsupportedCount => overhangTotal;

    public bool IsSolid(int c, int z) => z >= 0 && z < Z && Mat[c * Z + z] != Chemistry.Air;
    public bool InCave(Agent a) => a.Z < Height[a.Y * W + a.X];
    // Light where the body is: a swimmer has less water over it than the floor (World.Water).
    public float AgentLight(Agent a)
    {
        int c = a.Y * W + a.X;
        if (InCave(a)) return 0;
        return a.Lift > 0 ? Light[c] * MathF.Exp(P.WaterDim * Math.Min(a.Lift, Water[c])) : Light[c];
    }

    // Agents occupy a free voxel immediately above a floor; the roof need not be the surface.
    int WalkLevel(int c, int from)
    {
        if (from < Z && !IsSolid(c, from))
        {
            int floor = from;
            while (floor > 0 && !IsSolid(c, floor - 1)) floor--;
            return floor;
        }
        if (from + 1 < Z && !IsSolid(c, from + 1)) return from + 1;
        return -1;
    }

    void SettleAgent(Agent a)
    {
        int c = a.Y * W + a.X;
        if (IsSolid(c, a.Z)) { Interlocked.Increment(ref BuriedBy[0]); Die(a, c, CauseBuried, Math.Max(c * Z + 1, c * Z + a.Z - 1)); return; }
        int level = WalkLevel(c, a.Z);
        if (level >= 0 && level < a.Z)
        {
            // Under water the floor giving way leaves the body where it was, to sink at its own pace.
            if (InWater(c, level)) a.Lift += a.Z - level;
            else Dissipate(a, P.CostFall * Math.Max(0, a.Z - level - 1) * (1 + a.Mass * P.Gravity));
            a.Z = level;
            if (a.Energy <= 0) Die(a, c, CauseBroken);
        }
    }

    bool LoadMatters(int v, float delta)
    {
        return InSupportPath(v) || MathF.Abs(delta) > 0.05f * CompressionCapacity(v)
            || Pressure[v] + Math.Max(0, delta) > 0.7f * CompressionCapacity(v);
    }

    float OwnLoad(int v) => VoxelMass(v) * P.Gravity + (bodyLoad.TryGetValue(v, out float load) ? load : 0);
    public float CompressionCapacity(int v)
    {
        if (Mat[v] == Chemistry.Bedrock) return 1e9f;
        if (compressionCache[v] > 0) return compressionCache[v];
        float fill = Fill(v);
        return compressionCache[v] = P.CompressionK * (0.3f + VoxelCohesion(v)) * (0.5f + Order[v] / 255f) * fill * fill;
    }

    float BondCapacity(int from, int to)
    {
        if (Mat[to] == 0) return 0;
        if (to == from - 1) return CompressionCapacity(to);
        float contact = ContactFactor(from, to);
        float order = Math.Min(Order[from], Order[to]) / 255f;
        float fill = Math.Min(Fill(from), Mat[to] == Chemistry.Bedrock ? 1 : Fill(to));
        return P.TensionK * Math.Min(VoxelCohesion(from), VoxelCohesion(to)) * contact * (0.08f + order * order) * fill * fill;
    }

    void RefreshColumn(int c)
    {
        bool topologyChanged = topologyVersion[c] != ColumnVersion[c];
        int g = grounded[c];
        if (topologyChanged)
        {
            for (int z = g; z < Z && overhangCount[c] > 0; z++)
                if (overhang[c * Z + z]) { overhang[c * Z + z] = false; overhangCount[c]--; overhangTotal--; }
            g = 0;
            while (g < Height[c] && IsSolid(c, g)) g++;
            grounded[c] = g;
            HasCavity[c] = g < Height[c];
            topologyVersion[c] = ColumnVersion[c];
        }
        float load = 0;
        for (int z = Height[c] - 1; z >= 0; z--)
        {
            int v = c * Z + z;
            if (!IsSolid(c, z)) { load = 0; Pressure[v] = 0; continue; }
            load += OwnLoad(v);
            Pressure[v] = load;
            if (topologyChanged && z >= g && !overhang[v]) { overhang[v] = true; overhangCount[c]++; overhangTotal++; }
        }
    }

    void OfferSupport(int v, int from)
    {
        if (Mat[from] == Chemistry.Air) return;
        bool root = from % Z < grounded[from / Z];
        float available = root ? Math.Max(0, CompressionCapacity(from) - Pressure[from]) : support[from];
        float capacity = BondCapacity(v, from);
        float score = Math.Min(available, capacity) - OwnLoad(v);
        if (score <= support[v]) return;
        support[v] = score; parent[v] = from; edgeCapacity[v] = capacity;
        supportQueue.Enqueue(v, (-score, v));
    }

    void ActivateHanging(int v)
    {
        if (overhang[v] && activeHanging.Add(v)) hangingQueue.Enqueue(v);
    }

    void SeedHangingColumn(int c)
    {
        SeedHanging(c);
        for (int d = 0; d < 4; d++) SeedHanging(nb[c * 4 + d]);
    }

    void SeedHanging(int c)
    {
        if (overhangCount[c] == 0) return;
        for (int z = grounded[c]; z < Height[c]; z++) ActivateHanging(c * Z + z);
    }

    void PrepareStructureRegion()
    {
        dirtyWork.Clear(); dirtyWork.AddRange(structuralDirty); dirtyWork.Sort(); structuralDirty.Clear();
        activeHanging.Clear(); activeColumns.Clear(); hangingQueue.Clear();
        // Apply every geometry change before traversing, including newly cut connections.
        foreach (int c in dirtyWork) { RefreshColumn(c); activeColumns.Add(c); }
        foreach (int c in dirtyWork) SeedHangingColumn(c);
        while (hangingQueue.TryDequeue(out int v))
        {
            int c = v / Z, z = v % Z;
            for (int d = 0; d < 4; d++)
            {
                int n = nb[c * 4 + d];
                if (n == c) continue;
                ActivateHanging(n * Z + z);
                if (z < grounded[n] && activeColumns.Add(n))
                {
                    // Include all other roofs sharing this pier. Independent regions must never
                    // spend the same anchor capacity twice or forget a previous lateral load.
                    RefreshColumn(n); dirtyWork.Add(n); SeedHangingColumn(n);
                }
            }
            if (z > 0) ActivateHanging(v - 1);
            if (z + 1 < Z) ActivateHanging(v + 1);
        }
        dirtyWork.Sort();
    }

    // A rooted support graph, followed by load accumulation. No unsupported ring can support itself.
    // One strongest path per voxel is a deliberately conservative beam approximation, not FEM.
    void StepStructure()
    {
        detail.Restart();
        nextBodyLoad.Clear();
        foreach (var a in Agents)
        {
            if (a.Dead) continue;
            SettleAgent(a);
            if (a.Dead) continue;
            // In water the bed bears only what the body weighs there (its mass less the water it displaces).
            float weight = (InWater(a) ? Weight(a) : a.Mass) * P.Gravity / a.Cells;
            for (int k = 0; k < a.Cells; k++)
            {
                int c = FootCell(a, k), level = k == 0 ? a.Z : WalkLevel(c, a.Z);
                int v = c * Z + Math.Max(0, level - 1);
                nextBodyLoad.TryGetValue(v, out float load);
                nextBodyLoad[v] = load + weight;
            }
        }
        // A body's weight matters where it can tip the balance: on a roof or ledge, or when the change
        // is a noticeable share of what the floor block can bear. Small shifts on solid ground wait for
        // the next real change of that column (the stored loads are always current).
        foreach (var old in bodyLoad)
        {
            nextBodyLoad.TryGetValue(old.Key, out float load);
            if (load != old.Value && LoadMatters(old.Key, load - old.Value)) { structuralDirty.Add(old.Key / Z); DirtBy[2]++; }
        }
        foreach (var current in nextBodyLoad)
            if (!bodyLoad.ContainsKey(current.Key) && LoadMatters(current.Key, current.Value)) { structuralDirty.Add(current.Key / Z); DirtBy[2]++; }
        (bodyLoad, nextBodyLoad) = (nextBodyLoad, bodyLoad);
        Lap(3);
        if (structuralDirty.Count == 0) return;
        PrepareStructureRegion();
        Lap(4);
        LastStructureColumns = dirtyWork.Count; LastStructureVoxels = activeHanging.Count;
        StructureVisits += activeHanging.Count;
        failures.Clear(); supportOrder.Clear(); supportQueue.Clear(); rootLoad.Clear();
        // Every reachable block gets its best path, even one that cannot bear it (negative residual):
        // its weight must land somewhere, so an overload shows as a failing edge on that path.
        foreach (int v in activeHanging) { support[v] = float.NegativeInfinity; carried[v] = OwnLoad(v); parent[v] = -1; }
        // Seed only from the ground; then grow support outwards through face contacts.
        foreach (int v in activeHanging)
        {
            int c = v / Z, z = v % Z;
            for (int d = 0; d < 4; d++)
            {
                int n = nb[c * 4 + d];
                if (n != c && z < grounded[n]) OfferSupport(v, n * Z + z);
            }
            if (z > 0 && z - 1 < grounded[c]) OfferSupport(v, v - 1);
        }
        while (supportQueue.TryDequeue(out int v, out var priority))
        {
            if (-priority.Item1 != support[v]) continue;
            supportOrder.Add(v);
            int c = v / Z, z = v % Z;
            for (int d = 0; d < 4; d++)
            {
                int n = nb[c * 4 + d] * Z + z;
                if (n != v && activeHanging.Contains(n)) OfferSupport(n, v);
            }
            if (z > 0 && activeHanging.Contains(v - 1)) OfferSupport(v - 1, v);
            if (z + 1 < Z && activeHanging.Contains(v + 1)) OfferSupport(v + 1, v);
        }
        overloaded.Clear();
        for (int k = supportOrder.Count - 1; k >= 0; k--)
        {
            int v = supportOrder[k], p = parent[v];
            if (carried[v] > edgeCapacity[v]) overloaded.Add(v);
            if (activeHanging.Contains(p)) carried[p] += carried[v];
            else { rootLoad.TryGetValue(p, out float load); rootLoad[p] = load + carried[v]; }
            Pressure[v] = carried[v];
        }
        RouteOverloads();
        foreach (int v in activeHanging) if (parent[v] < 0) failures.Add(v);
        foreach (var root in rootLoad)
        {
            // Carry horizontal loads down the grounded pier as well, so anchors cannot bear infinity.
            for (int z = root.Key % Z; z >= 2; z--) Pressure[root.Key / Z * Z + z] += root.Value;
        }
        foreach (int c in dirtyWork)
            for (int z = 2; z < grounded[c]; z++)
            {
                int v = c * Z + z;
                if (Crushes(v)) failures.Add(v);
            }
        foreach (var root in rootLoad)
            for (int z = 2; z <= root.Key % Z; z++)
            {
                int v = root.Key / Z * Z + z;
                if (Crushes(v)) failures.Add(v);
            }
        Lap(5);
        failures.Sort(); // bottom before roof in each column, stable ordering across runs
        int previous = -1;
        foreach (int v in failures)
        {
            if (v == previous || Mat[v] < 2) continue;
            previous = v;
            int c = v / Z, z = v % Z, dest = z;
            while (dest > 2 && !IsSolid(c, dest - 1)) dest--;
            if (dest < z) { impactSource = 1; TransferVoxel(v, c * Z + dest, true); }
            else if (Crushes(v))
            {
                int burial = c * Z + Math.Max(1, z - 1);
                SpillVoxel(v, BurialAt(burial).Matter);
                MoveBurial(v, burial);
                CrushedBlocks++;
            }
        }
        Lap(6);
        foreach (var a in Agents) if (!a.Dead) SettleAgent(a);
        Lap(7);
    }

    // Compression failure: under what bears down on the block. A block with nothing solid on top
    // is not crushed by its own weight or the bodies standing on it — a nearly gnawed-through remnant
    // is a skin on the block beneath, which bears them; only what hangs from it sideways counts.
    // (Crushing such a skin only turned its last molecules into loose food without the face's work.)
    bool Crushes(int v)
    {
        float load = Pressure[v];
        if (v % Z + 1 < Z && Mat[v + 1] == Chemistry.Air) load -= OwnLoad(v);
        return load > CompressionCapacity(v);
    }

    // A block whose path breaks falls if there is a cavity under it. If it rests on another hanging
    // block, the break only means its weight goes down onto that one: the load is added along the
    // lower block's path, and wherever that path cannot bear it, the failure moves there (and on
    // down). So a stack too heavy for what holds it fails where it is weakest, instead of standing.
    readonly List<int> overloaded = new();
    readonly HashSet<int> failed = new();
    readonly Queue<int> rerouted = new();

    void RouteOverloads()
    {
        failed.Clear(); rerouted.Clear();
        foreach (int v in overloaded) if (failed.Add(v)) rerouted.Enqueue(v);
        while (rerouted.TryDequeue(out int v))
        {
            int below = v - 1, z = v % Z;
            // Falls, is crushed, or the block under it is not hanging (grounded rock bears it).
            if (z <= 2 || !IsSolid(v / Z, z - 1) || Crushes(v) || !activeHanging.Contains(below)
                || parent[below] < 0)
            {
                failures.Add(v);
                continue;
            }
            if (parent[v] == below)
            {
                // Its weight already rests on the block below, which cannot bear it in compression.
                if (failed.Add(below)) rerouted.Enqueue(below);
                continue;
            }
            float load = carried[v];
            for (int n = below; ; n = parent[n])
            {
                if (n == v) { if (failed.Add(below)) rerouted.Enqueue(below); break; }   // it hung from the broken block
                carried[n] += load; Pressure[n] = carried[n];
                if (carried[n] > edgeCapacity[n] && failed.Add(n)) rerouted.Enqueue(n);
                int p = parent[n];
                if (!activeHanging.Contains(p))
                {
                    rootLoad.TryGetValue(p, out float r); rootLoad[p] = r + load;
                    break;
                }
            }
        }
    }

    // A block set down from at most one level higher just lands: whoever stands there climbs onto
    // it. Only a real fall crushes (a collapsing roof always falls onto whoever is under it).
    void DropVoxel(int from, int to)
    {
        if (from % Z - to % Z <= 1)
        {
            DisplaceOccupants(to / Z, to % Z);
            TransferVoxel(from, to, false);
        }
        else TransferVoxel(from, to, true);
    }

    void TransferVoxel(int from, int to, bool impact)
    {
        if (from == to || Mat[from] < 2 || Mat[to] != 0) return;
        int c = to / Z, targetZ = to % Z, fromZ = from % Z;
        if (impact)
        {
            float impactLoad = VoxelMass(from) * P.Gravity * (1 + Math.Max(0, fromZ - targetZ));
            var a = Head[c];
            while (a != null)
            {
                var next = a.NextInCell;
                if (a.Z >= targetZ && a.Z <= Math.Max(fromZ, targetZ))
                {
                    Interlocked.Increment(ref BuriedBy[impactSource]);
                    Die(a, c, CauseBuried, to);
                }
                a = next;
            }
            var big = Big[c];
            if (big != null && !big.Dead && big.Z >= targetZ && big.Z <= Math.Max(fromZ, targetZ))
                Die(big, big.Y * W + big.X, CauseBuried, to);
            heatIn[c] += impactLoad;
            Interlocked.Increment(ref CollapsedBlocks);
        }
        compressionCache[from] = compressionCache[to] = 0;
        cohesionCache[to] = cohesionCache[from]; cohesionCache[from] = 0;
        Mat[to] = Mat[from]; Units[to] = Units[from];
        Order[to] = (byte)(impact ? Order[from] * 0.6f : Order[from]);
        var seq = TakeMixture(from);
        if (seq != null) SetMixture(to, seq);
        MoveBurial(from, to);
        Mat[from] = 0; Units[from] = 0; Order[from] = 0;
        Height[c] = Math.Max(Height[c], targetZ + 1);
        TrimColumn(from / Z);
        TerrainChanged(c); TerrainChanged(from / Z);
    }
}
