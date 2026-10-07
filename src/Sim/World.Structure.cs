using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Primordium;

public sealed partial class World
{
    readonly HashSet<int> structuralDirty = new();
    // Solid voxels above a cavity (not resting on a solid column from the bottom), and how many each
    // column has: columns without any are skipped when looking for hanging rock.
    readonly bool[] overhang = new bool[N * Z];
    readonly int[] overhangCount = new int[N];
    int overhangTotal;
    readonly int[] grounded = new int[N];
    // Solver state of the hanging voxels in the current pass, indexed by their position in
    // activeHanging (a few thousand at most), not by voxel: dense N·Z arrays cost 126 MB for this.
    int[] hParent = new int[1024], hParentAt = new int[1024];   // supporting voxel, and its index if it is hanging too (else -1)
    float[] hSupport = new float[1024], hCarried = new float[1024], hEdge = new float[1024];
    public readonly float[] Pressure = new float[N * Z];
    LoadMap bodyLoad = new(), nextBodyLoad = new();
    readonly Dictionary<int, float> rootLoad = new();
    readonly float[] compressionCache = new float[N * Z];
    // Geometry version of each column as the solver last saw it. TopologyVersion moves only when
    // solid and air change places (TerrainChanged); ColumnVersion also moves on partial bites and
    // fills, for the view.
    readonly int[] topologySeen = new int[N], topologyVersion = new int[N];
    public readonly bool[] HasCavity = new bool[N];
    readonly PriorityQueue<int, (float, int)> supportQueue = new();   // elements: index in activeHanging
    readonly List<int> supportOrder = new(), dirtyWork = new(), failures = new();
    readonly VoxelSet activeHanging = new();
    readonly HashSet<int> activeColumns = new();
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

    // Would SettleAgent do anything to this body? (Read-only: checked in parallel.)
    bool Unsettled(Agent a)
    {
        if (a.Dead) return false;
        int c = a.Y * W + a.X;
        if (IsSolid(c, a.Z)) return true;
        int level = WalkLevel(c, a.Z);
        return level >= 0 && level < a.Z;
    }

    // Copies Agents into `everyone` (and sizes the per-body scratch arrays); returns the count.
    int SnapshotAgents()
    {
        int pop = Agents.Count;
        if (agentTile.Length < pop) { agentTile = new int[pop * 2]; everyone = new Agent[pop * 2]; }
        if (agentVoxel.Length < pop) { agentVoxel = new int[pop * 2]; agentWeight = new float[pop * 2]; }
        Agents.CopyTo(everyone);
        return pop;
    }
    int[] agentVoxel = new int[1024];
    float[] agentWeight = new float[1024];

    // foreach (var a in Agents) if (!a.Dead) SettleAgent(a): the check in parallel, the settling in order.
    void SettleAll()
    {
        int pop = SnapshotAgents();
        Parallel.For(0, (pop + 2047) / 2048, chunk =>
        {
            for (int i = chunk * 2048, end = Math.Min(pop, i + 2048); i < end; i++) agentTile[i] = Unsettled(everyone[i]) ? 1 : 0;
        });
        for (int i = 0; i < pop; i++) if (agentTile[i] != 0) SettleAgent(everyone[i]);
        Array.Clear(everyone, 0, pop);
    }

    bool LoadMatters(int v, float delta)
    {
        return InSupportPath(v) || MathF.Abs(delta) > 0.05f * CompressionCapacity(v)
            || Pressure[v] + Math.Max(0, delta) > 0.7f * CompressionCapacity(v);   // waking is judged conservatively (without confinement); the solver decides with Strength
    }

    float OwnLoad(int v) => VoxelMass(v) * P.Gravity + bodyLoad.Get(v);
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
        bool topologyChanged = topologySeen[c] != topologyVersion[c];
        int g = grounded[c];
        if (topologyChanged)
        {
            for (int z = g; z < Z && overhangCount[c] > 0; z++)
                if (overhang[c * Z + z]) { overhang[c * Z + z] = false; overhangCount[c]--; overhangTotal--; }
            g = 0;
            while (g < Height[c] && IsSolid(c, g)) g++;
            grounded[c] = g;
            HasCavity[c] = g < Height[c];
            topologySeen[c] = topologyVersion[c];
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
        annealable[c] = true;   // its pressures were rewritten: metamorphism looks at it again
    }

    // `iv`: v's index among the hanging voxels; `at`: from's index, or -1 for grounded rock.
    void OfferSupport(int iv, int v, int from, int at)
    {
        if (Mat[from] == Chemistry.Air) return;
        bool root = from % Z < grounded[from / Z];
        float available = root ? Math.Max(0, Strength(from) - Pressure[from]) : hSupport[at];
        float capacity = BondCapacity(v, from);
        float score = Math.Min(available, capacity) - OwnLoad(v);
        if (score <= hSupport[iv]) return;
        hSupport[iv] = score; hParent[iv] = from; hParentAt[iv] = root ? -1 : at; hEdge[iv] = capacity;
        supportQueue.Enqueue(iv, (-score, v));
    }

    void EnsureHanging(int n)
    {
        if (hSupport.Length >= n) return;
        int size = Math.Max(n, hSupport.Length * 2);
        hParent = new int[size]; hParentAt = new int[size];
        hSupport = new float[size]; hCarried = new float[size]; hEdge = new float[size];
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
        LapStart();
        nextBodyLoad.Clear();
        // Bodies are looked at in parallel (where each stands, what it weighs); settling, which can
        // kill, and adding up the loads go in the order of Agents as before. Settling one body changes
        // nothing another's check or weight reads (deaths do not change geometry).
        int pop = SnapshotAgents();
        Parallel.For(0, (pop + 2047) / 2048, chunk =>
        {
            for (int i = chunk * 2048, end = Math.Min(pop, i + 2048); i < end; i++) agentTile[i] = Unsettled(everyone[i]) ? 1 : 0;
        });
        for (int i = 0; i < pop; i++) if (agentTile[i] != 0) SettleAgent(everyone[i]);
        Parallel.For(0, (pop + 2047) / 2048, chunk =>
        {
            for (int i = chunk * 2048, end = Math.Min(pop, i + 2048); i < end; i++)
            {
                var a = everyone[i];
                if (a.Dead || a.Cells > 1) { agentVoxel[i] = -1; continue; }
                // In water the bed bears only what the body weighs there (its mass less the water it displaces).
                agentWeight[i] = (InWater(a) ? Weight(a) : a.Mass) * P.Gravity / a.Cells;
                agentVoxel[i] = (a.Y * W + a.X) * Z + Math.Max(0, a.Z - 1);
            }
        });
        for (int i = 0; i < pop; i++)
        {
            if (agentVoxel[i] >= 0) { nextBodyLoad.Add(agentVoxel[i], agentWeight[i]); continue; }
            var a = everyone[i];
            if (a.Dead) continue;
            float weight = (InWater(a) ? Weight(a) : a.Mass) * P.Gravity / a.Cells;
            for (int k = 0; k < a.Cells; k++)
            {
                int c = FootCell(a, k), level = k == 0 ? a.Z : WalkLevel(c, a.Z);
                nextBodyLoad.Add(c * Z + Math.Max(0, level - 1), weight);
            }
        }
        Array.Clear(everyone, 0, pop);
        // A body's weight matters where it can tip the balance: on a roof or ledge, or when the change
        // is a noticeable share of what the floor block can bear. Small shifts on solid ground wait for
        // the next real change of that column (the stored loads are always current).
        foreach (int v in bodyLoad.Keys)
        {
            float load = nextBodyLoad.Get(v), was = bodyLoad.Get(v);
            if (load != was && LoadMatters(v, load - was)) { structuralDirty.Add(v / Z); DirtBy[2]++; }
        }
        foreach (int v in nextBodyLoad.Keys)
            if (!bodyLoad.Contains(v) && LoadMatters(v, nextBodyLoad.Get(v))) { structuralDirty.Add(v / Z); DirtBy[2]++; }
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
        var hanging = activeHanging.Items;
        int count = hanging.Count;
        EnsureHanging(count);
        for (int i = 0; i < count; i++) { hSupport[i] = float.NegativeInfinity; hCarried[i] = OwnLoad(hanging[i]); hParent[i] = hParentAt[i] = -1; }
        // Seed only from the ground; then grow support outwards through face contacts.
        for (int i = 0; i < count; i++)
        {
            int v = hanging[i], c = v / Z, z = v % Z;
            for (int d = 0; d < 4; d++)
            {
                int n = nb[c * 4 + d];
                if (n != c && z < grounded[n]) OfferSupport(i, v, n * Z + z, -1);
            }
            if (z > 0 && z - 1 < grounded[c]) OfferSupport(i, v, v - 1, -1);
        }
        while (supportQueue.TryDequeue(out int iv, out var priority))
        {
            if (-priority.Item1 != hSupport[iv]) continue;
            supportOrder.Add(iv);
            int v = hanging[iv], c = v / Z, z = v % Z, j;
            for (int d = 0; d < 4; d++)
            {
                int n = nb[c * 4 + d] * Z + z;
                if (n != v && (j = activeHanging.IndexOf(n)) >= 0) OfferSupport(j, n, v, iv);
            }
            if (z > 0 && (j = activeHanging.IndexOf(v - 1)) >= 0) OfferSupport(j, v - 1, v, iv);
            if (z + 1 < Z && (j = activeHanging.IndexOf(v + 1)) >= 0) OfferSupport(j, v + 1, v, iv);
        }
        overloaded.Clear();
        for (int k = supportOrder.Count - 1; k >= 0; k--)
        {
            int iv = supportOrder[k], v = hanging[iv], p = hParent[iv], ip = hParentAt[iv];
            if (hCarried[iv] > hEdge[iv]) overloaded.Add(v);
            if (ip >= 0) hCarried[ip] += hCarried[iv];
            else { rootLoad.TryGetValue(p, out float load); rootLoad[p] = load + hCarried[iv]; }
            Pressure[v] = hCarried[iv];
            annealable[v / Z] = true;
        }
        RouteOverloads();
        for (int i = 0; i < count; i++) if (hParent[i] < 0) failures.Add(hanging[i]);
        foreach (var root in rootLoad)
        {
            // Carry horizontal loads down the grounded pier as well, so anchors cannot bear infinity.
            for (int z = root.Key % Z; z >= 2; z--) Pressure[root.Key / Z * Z + z] += root.Value;
            annealable[root.Key / Z] = true;
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
                if (Order[v] > 0) CrushToRubble(v);   // the lattice breaks; the molecules keep their room
                else FlowRubble(v);                   // failed rubble flows where there is room
                CrushedBlocks++;
            }
        }
        Lap(6);
        SettleAll();
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
        if (load <= CompressionCapacity(v) || load <= Strength(v)) return false;   // the neighbours only matter where it would not bear the load alone
        // Rubble that fails can only flow somewhere with room (down, or out of an open side).
        return Order[v] > 0 || RubbleCanFlow(v);
    }

    // Strength against crushing under confinement (Mohr–Coulomb): what the block bears alone (its
    // uniaxial strength, CompressionCapacity) plus FrictionQ × the least horizontal stress σ3 that its
    // neighbours press on it with. A neighbour presses with LateralK × its own vertical stress, passed on
    // as well as the contact allows: the same rock fully (1), a seam of unlike rock poorly (0.2–0.4), a
    // partly emptied block by its fill, air not at all. Stress along an axis needs both sides, and the
    // weaker axis decides. So deep rock inside a stratum bears its overburden; cliff edges, pillars,
    // tunnel walls and a free pile fail at their uniaxial strength — rock of one stratum holds together
    // better than a stack of unlike seams. Nothing here is a table: contact and strength come from the
    // molecules (Chemistry), the two numbers are laws of mechanics.
    public float Strength(int v)
    {
        float ucs = CompressionCapacity(v);
        if (Mat[v] < 2) return ucs;
        float sx = Math.Min(SidePush(v, 0), SidePush(v, 2)), sy = Math.Min(SidePush(v, 1), SidePush(v, 3));
        return ucs + P.FrictionQ * Math.Min(sx, sy);
    }

    float SidePush(int v, int d)
    {
        int c = v / Z, z = v % Z, n = nb[c * 4 + d];
        if (n == c) return P.LateralK * Pressure[v];   // the end of the map in y is a wall
        int u = n * Z + z;
        if (Mat[u] == Chemistry.Bedrock) return P.LateralK * Pressure[v];
        if (Mat[u] < 2) return 0;
        return P.LateralK * Pressure[u] * Chem.Contact[Mat[v], Mat[u]] * Fill(u);
    }

    // Compression failure: the block's lattice is crushed into rubble in place. Same molecules, same
    // room; disorder makes it weaker (it may crush the blocks under it in turn, but never vanish), and
    // pressure orders it again over time (Metamorphose) once it bears its load.
    void CrushToRubble(int v)
    {
        int c = v / Z;
        Order[v] = 0;
        compressionCache[v] = 0;
        Dilate(v);   // broken rock swells: what no longer fits goes up or out, or the lattice stays partly whole
        if (BurialOf(v, out var burial)) burial.Dirty = true;
        Interlocked.Increment(ref TerrainVersion);
        ColumnVersion[c]++;
        annealable[c] = true;
        MarkDirty(c);
    }

    // Dilatancy: a block whose lattice was broken (crushed, or shattered by a fall) takes more room than
    // its voxel has. The excess goes where there is room — up into the voxel above, then out of an open
    // side, level with it or one up — as a new heap of rubble or into a block that is not full. Where it
    // is confined and nothing can give, it cannot swell: its lattice stays as ordered as the room allows.
    void Dilate(int v)
    {
        float over = VoxelVolume(v) - P.VoxelSpace;
        if (over <= 0) return;
        int c = v / Z, z = v % Z;
        if (!Mixed(v, out var from))
        {
            from = new ushort[Chemistry.S];
            from[Mat[v] - 2] = Units[v];
            SetMixture(v, from);
        }
        bool moved = SwellInto(v, v + 1, from);
        for (int d = 0; d < 4 && VoxelVolume(v) > P.VoxelSpace; d++)
        {
            int n = nb[c * 4 + d];
            if (n == c) continue;
            moved |= SwellInto(v, n * Z + z, from);
            if (VoxelVolume(v) > P.VoxelSpace) moved |= SwellInto(v, n * Z + z + 1, from);
        }
        if (moved)
        {
            Mat[v] = Chem.BuiltMat[Dominant(from)];
            cohesionCache[v] = 0;
            Interlocked.Increment(ref TerrainVersion);
            ColumnVersion[c]++;
            MarkDirty(c);
        }
        // Still too much: confinement keeps the lattice partly whole — the order at which it just fits.
        float dense = 0, loose = 0;
        for (int s = 0; s < Chemistry.S; s++)
            if (from[s] > 0) { dense += from[s] * Chem.Volume[s]; loose += from[s] * Chem.Volume[s] * P.Bulking * Chem.Looseness[s]; }
        if (dense + loose * (1 - Order[v] / 255f) > P.VoxelSpace && loose > 0)
        {
            float o = 1 - Math.Max(0, P.VoxelSpace - dense) / loose;
            Order[v] = (byte)Math.Clamp((int)MathF.Ceiling(o * 255), Order[v], 255);
        }
        compressionCache[v] = 0;
    }

    // Moves molecules of v that do not fit its voxel into u (air on top of something solid, or a block
    // that is not full), as many as u has room for. Returns whether anything moved.
    bool SwellInto(int v, int u, ushort[] from)
    {
        if (u % Z < 2 || u % Z >= Z - 1) return false;
        bool air = Mat[u] == Chemistry.Air;
        if (air ? !(Mat[u - 1] >= 2) : !(Mat[u] >= 2 && VoxelVolume(u) < P.VoxelSpace * 0.999f)) return false;
        float excess = VoxelVolume(v) - P.VoxelSpace;
        if (excess <= 0) return false;
        float room = air ? P.VoxelSpace : P.VoxelSpace - VoxelVolume(u);
        var into = air ? new ushort[Chemistry.S] : null;
        if (!air && !Mixed(u, out into))
        {
            into = new ushort[Chemistry.S];
            into[Mat[u] - 2] = Units[u];
            SetMixture(u, into);
        }
        int moved = 0, ov = Order[v];
        for (int sp = Chemistry.S - 1; sp >= 0 && room > 0 && excess > 0; sp--)
        {
            if (from[sp] == 0) continue;
            float leave = PackedVolume(sp, ov), each = PackedVolume(sp, air ? 0 : Order[u]);
            int fit = Math.Min(from[sp], (int)MathF.Ceiling(excess / leave));
            fit = Math.Min(fit, (int)(room / each));
            if (!air) fit = Math.Min(fit, ushort.MaxValue - Units[u]);
            if (fit <= 0) continue;
            from[sp] -= (ushort)fit; into[sp] += (ushort)fit; Units[v] -= (ushort)fit; moved += fit;
            if (!air) Units[u] += (ushort)fit;
            room -= fit * each; excess -= fit * leave;
        }
        if (moved == 0) return false;
        int c = u / Z;
        if (air)
        {
            DisplaceOccupants(c, u % Z, null);
            PutMixture(u, into, 0);
        }
        else
        {
            Mat[u] = Chem.BuiltMat[Dominant(into)];
            compressionCache[u] = cohesionCache[u] = 0;
            MassChanged(u);
            Interlocked.Increment(ref TerrainVersion);
            ColumnVersion[c]++;
        }
        MarkDirty(c);
        return true;
    }

    // Failed rubble (no lattice left to break) behaves as a granular mass: it flows into room — first the
    // pores of the block under it, then out of an open side, one level down or level with it. Molecules
    // move whole and only as many as the target has room for by volume; the voxel empties only when all
    // of it went (the pore closes, whatever stands above settles). Matter never takes less room than it
    // fills. A heap so spreads until its flanks are confined or bear their load: an angle of repose
    // that comes out of strength and confinement, not a set slope.
    bool RubbleCanFlow(int v)
    {
        if (Room(v - 1, v)) return true;
        int c = v / Z, z = v % Z;
        for (int d = 0; d < 4; d++)
        {
            int n = nb[c * 4 + d];
            if (n == c) continue;
            if (Room(n * Z + z - 1, v) || Room(n * Z + z, v)) return true;
        }
        return false;
    }

    // Whether matter from `from` can go into voxel u: air with solid ground under it, or a solid block
    // that is not full. Never the boundary, never below level 2.
    bool Room(int u, int from)
    {
        if (u % Z < 2 || u == from) return false;
        if (Mat[u] == Chemistry.Air) return u % Z > 2 && Mat[u - 1] >= 2 && u % Z <= from % Z;
        return Mat[u] >= 2 && VoxelVolume(u) < P.VoxelSpace * 0.999f;
    }

    void FlowRubble(int v)
    {
        int c = v / Z, z = v % Z;
        if (!Mixed(v, out var from))
        {
            from = new ushort[Chemistry.S];
            from[Mat[v] - 2] = Units[v];
            SetMixture(v, from);
        }
        bool moved = PourInto(v, v - 1, from);
        for (int d = 0; d < 4 && Units[v] > 0; d++)
        {
            int n = nb[c * 4 + d];
            if (n == c) continue;
            moved |= PourInto(v, n * Z + z - 1, from);
            if (Units[v] > 0) moved |= PourInto(v, n * Z + z, from);
        }
        if (!moved) return;
        if (Units[v] == 0) { RemoveVoxel(c, z); return; }   // its burial, if any, goes down to the floor
        Mat[v] = Chem.BuiltMat[Dominant(from)];
        compressionCache[v] = cohesionCache[v] = 0;
        Interlocked.Increment(ref TerrainVersion);
        ColumnVersion[c]++;
        MarkDirty(c);
    }

    // Moves as many of v's molecules into u as fit by volume (species in order). An air voxel becomes a
    // new rubble block. Returns whether anything moved.
    bool PourInto(int v, int u, ushort[] from)
    {
        if (!Room(u, v)) return false;
        bool air = Mat[u] == Chemistry.Air;
        float room = air ? P.VoxelSpace : P.VoxelSpace - VoxelVolume(u);
        var into = air ? new ushort[Chemistry.S] : null;
        if (!air && !Mixed(u, out into))
        {
            into = new ushort[Chemistry.S];
            into[Mat[u] - 2] = Units[u];
            SetMixture(u, into);
        }
        int moved = 0;
        for (int sp = 0; sp < Chemistry.S && room > 0; sp++)
        {
            if (from[sp] == 0) continue;
            float each = PackedVolume(sp, air ? 0 : Order[u]);   // a new heap is rubble
            int fit = Math.Min(from[sp], (int)(room / each));
            if (!air) fit = Math.Min(fit, ushort.MaxValue - Units[u]);
            if (fit <= 0) continue;
            from[sp] -= (ushort)fit; into[sp] += (ushort)fit;
            Units[v] -= (ushort)fit; moved += fit;
            if (!air) Units[u] += (ushort)fit;
            room -= fit * each;
        }
        if (moved == 0) return false;
        int c = u / Z;
        if (air)
        {
            DisplaceOccupants(c, u % Z, null);
            PutMixture(u, into, 0);
        }
        else
        {
            Mat[u] = Chem.BuiltMat[Dominant(into)];
            compressionCache[u] = cohesionCache[u] = 0;
            MassChanged(u);
            Interlocked.Increment(ref TerrainVersion);
            ColumnVersion[c]++;
        }
        MarkDirty(c);
        return true;
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
            int below = v - 1, z = v % Z, iv = activeHanging.IndexOf(v), ib = activeHanging.IndexOf(below);
            // Falls, is crushed, or the block under it is not hanging (grounded rock bears it).
            if (z <= 2 || !IsSolid(v / Z, z - 1) || Crushes(v) || ib < 0 || hParent[ib] < 0)
            {
                failures.Add(v);
                continue;
            }
            if (hParent[iv] == below)
            {
                // Its weight already rests on the block below, which cannot bear it in compression.
                if (failed.Add(below)) rerouted.Enqueue(below);
                continue;
            }
            float load = hCarried[iv];
            for (int n = below, i = ib; ; )
            {
                if (n == v) { if (failed.Add(below)) rerouted.Enqueue(below); break; }   // it hung from the broken block
                hCarried[i] += load; Pressure[n] = hCarried[i];
                if (hCarried[i] > hEdge[i] && failed.Add(n)) rerouted.Enqueue(n);
                int p = hParent[i], ip = hParentAt[i];
                if (ip < 0)
                {
                    rootLoad.TryGetValue(p, out float r); rootLoad[p] = r + load;
                    break;
                }
                n = p; i = ip;
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
            Flows[FImpact] += impactLoad;
            Interlocked.Increment(ref CollapsedBlocks);
        }
        compressionCache[from] = compressionCache[to] = 0;
        cohesionCache[to] = cohesionCache[from]; cohesionCache[from] = 0;
        Mat[to] = Mat[from]; Units[to] = Units[from];
        Order[to] = (byte)(impact ? Order[from] * 0.6f : Order[from]);   // an impact shatters part of the lattice
        var seq = TakeMixture(from);
        if (seq != null) SetMixture(to, seq);
        MoveBurial(from, to);
        Mat[from] = 0; Units[from] = 0; Order[from] = 0;
        Height[c] = Math.Max(Height[c], targetZ + 1);
        TrimColumn(from / Z);
        TerrainChanged(c); TerrainChanged(from / Z);
        if (impact) Dilate(to);   // the shattered part swells
    }

    // Body weight per floor voxel. Nearly every occupied cell has bodies on one floor only: that one
    // lives in per-cell arrays, any other floor of the same cell in a small dictionary. Keys keep the
    // order of first use.
    sealed class LoadMap
    {
        readonly float[] value = new float[N];
        readonly int[] level = InitLevels();
        readonly Dictionary<int, float> extra = new();
        public readonly List<int> Keys = new();
        static int[] InitLevels() { var l = new int[N]; Array.Fill(l, -1); return l; }

        public void Add(int v, float w)
        {
            int c = v / Z, z = v % Z;
            if (level[c] == z) { value[c] += w; return; }
            if (level[c] < 0) { level[c] = z; value[c] = 0f + w; Keys.Add(v); return; }
            if (!extra.TryGetValue(v, out float load)) Keys.Add(v);
            extra[v] = load + w;
        }

        public bool Contains(int v) => level[v / Z] == v % Z || extra.ContainsKey(v);
        public float Get(int v)
        {
            int c = v / Z;
            if (level[c] == v % Z) return value[c];
            return extra.Count > 0 && extra.TryGetValue(v, out float load) ? load : 0;
        }

        public void Clear()
        {
            foreach (int v in Keys) level[v / Z] = -1;
            extra.Clear();
            Keys.Clear();
        }
    }

    // A set of voxels that remembers the order they were added in and gives each its index (open
    // addressing; clearing costs only what was added).
    sealed class VoxelSet
    {
        int[] keys = Empty(1024), index = new int[1024];
        int mask = 1023;
        readonly List<int> slots = new();
        public readonly List<int> Items = new();
        static int[] Empty(int n) { var k = new int[n]; Array.Fill(k, -1); return k; }
        public int Count => Items.Count;

        int Slot(int v)
        {
            int s = (int)((uint)v * 0x9E3779B1u >> 7) & mask;
            while (keys[s] != -1 && keys[s] != v) s = (s + 1) & mask;
            return s;
        }

        public bool Add(int v)
        {
            if ((Items.Count + 1) * 2 > keys.Length) Grow();
            int s = Slot(v);
            if (keys[s] == v) return false;
            keys[s] = v; index[s] = Items.Count;
            Items.Add(v); slots.Add(s);
            return true;
        }

        public int IndexOf(int v)
        {
            int s = Slot(v);
            return keys[s] == v ? index[s] : -1;
        }

        public bool Contains(int v) => IndexOf(v) >= 0;

        void Grow()
        {
            keys = Empty(keys.Length * 2); index = new int[keys.Length]; mask = keys.Length - 1;
            slots.Clear();
            for (int i = 0; i < Items.Count; i++)
            {
                int s = Slot(Items[i]);
                keys[s] = Items[i]; index[s] = i; slots.Add(s);
            }
        }

        public void Clear()
        {
            foreach (int s in slots) keys[s] = -1;
            slots.Clear(); Items.Clear();
        }
    }
}
