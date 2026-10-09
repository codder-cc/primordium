using System;
using System.Collections.Generic;
using System.Threading;

namespace Primordium;

// Water as a fluid: where it goes and what it carries. Nothing here is a zone or a scripted flow — only
// the water's level, gravity and the room there is.
// - Currents (P.Currents). Water runs between columns by the difference of their surfaces (World.Flow);
//   what runs through a column per env step, over its depth, is the water's speed there (CurX, CurY,
//   cells a tick). A body off the bottom is dragged to the speed of the water around it within a few
//   ticks (WaterFriction), so it moves with it: the current's displacement accumulates in
//   Agent.DriftX/DriftY and every whole cell is a free step to the neighbour, at the same absolute
//   height (SetLift), if there is water there to swim in and room by volume. A body on the bottom holds
//   to it by its weight, a spread-out body by its footprint; out of water nothing drifts.
// - Caves (P.CaveWater). A run of air under a roof (a void: air voxels on a solid floor, up to the rock
//   above) holds water at its bottom: `cave` maps the run's floor voxel to the depth there. Each env
//   step, after the surface flow, water passes through every open face by the same law as between
//   columns: a share of the difference in water level, from the higher to the lower, if the water on the
//   higher side reaches the opening at all — from the surface water of a neighbouring column into a
//   cave mouth beside it, between neighbouring runs whose openings overlap, and back out. Then it stands
//   at the floor of the run it went into. A run filled to its roof does not take more: the air under a
//   roof has nowhere to go but the way the water came (air pockets stay). When rock changes, the
//   water follows: a run opened to the sky gives its water to the column's surface water, a void filled
//   with rock or a run grown too small for its water pushes it up into the next run above (and at the
//   top out onto the surface). The total is booked in WaterTotal like all other water.
// Bodies read all of it in their phase (InWater, WaterOver: World.Water); it is written only between
// ticks (CaveFlow in the climate step), except the body's own drift.
public sealed partial class World
{
    public readonly float[] CurX, CurY;   // the water's speed, cells a tick (east, south)
    bool currentsOn = true;                                              // whether CurX/CurY may hold anything (cleared once the law is off)
    public long Drifted;                                                 // steps bodies were carried by a current (diagnostics)
    public double CaveFlowMs;                                            // time spent in CaveFlow (diagnostics, not saved)
    const float FlowShare = 0.2f;                                        // of the difference in level that runs through a face per env step (World.Out)

    // ---- currents ----

    // In Move, after the body's own motion: the current carries it if it swims off the bottom.
    void Drift(Agent a, ref int cell)
    {
        if (a.Cells > 1 || OnFloor(a) || a.Z < Height[cell] || !InWater(cell, a.Z)) { a.DriftX = a.DriftY = 0; return; }
        a.DriftX += CurX[cell]; a.DriftY += CurY[cell];
        if (MathF.Abs(a.DriftX) < 1 && MathF.Abs(a.DriftY) < 1) return;
        int dir;
        if (MathF.Abs(a.DriftX) >= MathF.Abs(a.DriftY)) { dir = a.DriftX > 0 ? 0 : 2; a.DriftX -= MathF.Sign(a.DriftX); }
        else { dir = a.DriftY > 0 ? 1 : 3; a.DriftY -= MathF.Sign(a.DriftY); }
        int to = nb[cell * 4 + dir];
        float level = Level(a);
        int targetZ = to != cell ? WalkLevel(to, (int)level) : -1;
        // Carried only through open water it can swim in, never up a wall or onto land, and only where it fits.
        if (targetZ < Height[to] || targetZ > level || !InWater(to, targetZ) || !Fits(to, targetZ, Share(a), a))
        {
            if (dir % 2 == 0) a.DriftX = 0; else a.DriftY = 0;
            return;
        }
        Unplace(a, cell);
        Place(a, to);
        a.Z = targetZ;
        SetLift(a, to, targetZ, level);
        cell = to;
        Interlocked.Increment(ref Drifted);
    }

    // ---- water in caves ----

    readonly Dictionary<int, float> cave = new();   // floor voxel of a run of air under a roof → depth of water standing on it
    public (int voxel, float depth)[] CaveWaterView = Array.Empty<(int, float)>();   // for the view: a copy made each env step

    public bool CaveWaterLaw => P.CaveWater != 0;
    public int CaveRuns => cave.Count;

    // Depth of the water on the floor at `z` of `cell` under a roof (0 if `z` is not a wet cave floor).
    float CaveDepth(int cell, int z) => cave.TryGetValue(cell * Z + z, out float d) ? d : 0;

    // How high above a cave floor a body can swim: up to the water's surface, at most to the highest
    // voxel under the roof.
    float CaveTop(int cell, int z)
    {
        if (!cave.TryGetValue(cell * Z + z, out float d)) return 0;
        int h = 1, b = cell * Z;
        while (z + h < Height[cell] && Mat[b + z + h] == Chemistry.Air) h++;
        return Math.Min(d, h - 1);
    }

    public double CaveWaterTotal()
    {
        double t = 0;
        foreach (var d in cave.Values) t += d;
        return t;
    }

    // Scratch of one CaveFlow: the runs of every column with a void (floor, roof, depth), the edges
    // through open faces. Runs and surfaces are "nodes": a run by its index, a column's surface water
    // as −1 − cell.
    int[] rCol = new int[256], rZ0 = new int[256], rZ1 = new int[256];
    float[] rDepth = new float[256], rOut = new float[256], rIn = new float[256];
    int runCount;
    readonly int[] colFirst, colRuns;
    readonly List<int> runCols = new();
    readonly List<(int from, int to, float q)> caveEdges = new();
    readonly Dictionary<int, float> surfOut = new();
    readonly List<int> caveKeys = new();

    void AddRun(int c, int z0, int z1)
    {
        if (runCount == rCol.Length)
        {
            int n = runCount * 2;
            Array.Resize(ref rCol, n); Array.Resize(ref rZ0, n); Array.Resize(ref rZ1, n);
            Array.Resize(ref rDepth, n); Array.Resize(ref rOut, n); Array.Resize(ref rIn, n);
        }
        rCol[runCount] = c; rZ0[runCount] = z0; rZ1[runCount] = z1; rDepth[runCount] = 0;
        runCount++;
    }

    // The runs of air of a column under its top, bottom up.
    void ListRuns(int c)
    {
        colFirst[c] = runCount;
        int h = Height[c], b = c * Z;
        for (int z = 1; z < h; z++)
        {
            if (Mat[b + z] != Chemistry.Air || Mat[b + z - 1] == Chemistry.Air) continue;
            int top = z + 1;
            while (top < h && Mat[b + top] == Chemistry.Air) top++;
            AddRun(c, z, top);
            z = top;
        }
        colRuns[c] = runCount - colFirst[c];
        if (colRuns[c] > 0) runCols.Add(c);
    }

    // Between ticks, after the surface water ran (World.Hydro).
    void CaveFlow()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        CaveFlowStep();
        CaveFlowMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    void CaveFlowStep()
    {
        if (!CaveWaterLaw)
        {
            if (cave.Count > 0) ReleaseCaveWater();
            return;
        }
        foreach (int c in runCols) colRuns[c] = 0;
        runCols.Clear();
        runCount = 0;
        for (int c = 0; c < N; c++) if (HasCavity[c]) ListRuns(c);
        if (runCount == 0 && cave.Count == 0) { CaveWaterView = Array.Empty<(int, float)>(); return; }

        // The water where the rock now is: each amount to the run holding its floor voxel, to the next
        // run above if that voxel is rock now, to the surface if it is open to the sky.
        caveKeys.Clear();
        caveKeys.AddRange(cave.Keys);
        caveKeys.Sort();
        foreach (int v in caveKeys)
        {
            int c = v / Z, z = v % Z;
            float d = cave[v];
            int r = -1;
            if (z < Height[c])
                for (int k = colFirst[c], end = k + colRuns[c]; k < end; k++)
                    if (rZ1[k] > z) { r = k; break; }   // the run holding z, or the first one above it
            if (r >= 0) rDepth[r] += d; else Water[c] += d;
        }
        cave.Clear();
        // A run holds no more than its height: the rest is pushed up into the next one, the top one's onto the surface.
        foreach (int c in runCols)
            for (int k = colFirst[c], end = k + colRuns[c]; k < end; k++)
            {
                float extra = rDepth[k] - (rZ1[k] - rZ0[k]);
                if (extra <= 0) continue;
                rDepth[k] -= extra;
                if (k + 1 < end) rDepth[k + 1] += extra; else Water[c] += extra;
            }

        // Through every open face: from the surface water beside a run, between runs.
        caveEdges.Clear();
        for (int r = 0; r < runCount; r++)
        {
            int c = rCol[r], z0 = rZ0[r], z1 = rZ1[r];
            float la = z0 + rDepth[r];
            bool wet = rDepth[r] > 0, full = rDepth[r] >= z1 - z0;
            for (int d = 0; d < 4; d++)
            {
                int n = nb[c * 4 + d];
                if (n == c) continue;
                int hn = Height[n];
                if (hn < z1)
                {
                    // The side of the run is open above that column's ground: its surface water stands there.
                    float lo = Math.Max(z0, hn), sn = hn + Ice[n] + Water[n];
                    if (sn > la && sn > lo && !full && Water[n] > 0) caveEdges.Add((-1 - n, r, FlowShare * (sn - Math.Max(la, lo))));
                    else if (la > sn && la > lo) caveEdges.Add((r, -1 - n, FlowShare * (la - Math.Max(sn, lo))));
                }
                if (!wet) continue;
                for (int k = colFirst[n], end = k + colRuns[n]; k < end; k++)
                {
                    if (rDepth[k] > 0 && k < r) continue;   // a pair of wet runs is seen once, from the first
                    float lo = Math.Max(z0, rZ0[k]), hi = Math.Min(z1, rZ1[k]);
                    if (hi <= lo) continue;
                    float lb = rZ0[k] + rDepth[k];
                    if (la > lb && la > lo && rDepth[k] < rZ1[k] - rZ0[k]) caveEdges.Add((r, k, FlowShare * (la - Math.Max(lb, lo))));
                    else if (lb > la && lb > lo && !full) caveEdges.Add((k, r, FlowShare * (lb - Math.Max(la, lo))));
                }
            }
        }
        if (caveEdges.Count > 0)
        {
            // No source gives more than it holds, no run takes more than it has room for.
            Array.Clear(rOut, 0, runCount); Array.Clear(rIn, 0, runCount);
            surfOut.Clear();
            foreach (var (from, _, q) in caveEdges)
                if (from >= 0) rOut[from] += q; else surfOut[-1 - from] = surfOut.GetValueOrDefault(-1 - from) + q;
            for (int e = 0; e < caveEdges.Count; e++)
            {
                var (from, to, q) = caveEdges[e];
                float sum = from >= 0 ? rOut[from] : surfOut[-1 - from], has = from >= 0 ? rDepth[from] : Water[-1 - from];
                if (sum > has) q *= has / sum;
                caveEdges[e] = (from, to, q);
                if (to >= 0) rIn[to] += q;
            }
            Array.Clear(rOut, 0, runCount);
            foreach (var (from, _, q) in caveEdges) if (from >= 0) rOut[from] += q;
            for (int e = 0; e < caveEdges.Count; e++)
            {
                var (from, to, q) = caveEdges[e];
                if (to < 0) continue;
                float room = rZ1[to] - rZ0[to] - rDepth[to] + rOut[to];
                if (rIn[to] > room) caveEdges[e] = (from, to, q * Math.Max(0, room) / rIn[to]);
            }
            foreach (var (from, to, q) in caveEdges)
            {
                if (q <= 0) continue;
                if (from >= 0) rDepth[from] -= q; else Water[-1 - from] -= q;
                if (to >= 0) rDepth[to] += q; else Water[-1 - to] += q;
            }
        }

        // Back into the map. A trace too thin to matter seeps up through the rock onto the column's
        // surface (kept, not lost), so emptied caves are dry again.
        var view = new List<(int, float)>();
        for (int r = 0; r < runCount; r++)
        {
            float d = rDepth[r];
            if (d <= 0) continue;
            int c = rCol[r];
            if (d < 1e-5f) { Water[c] += d; continue; }
            cave[c * Z + rZ0[r]] = d;
            view.Add((c * Z + rZ0[r], d));
        }
        CaveWaterView = view.ToArray();
    }

    // The law switched off: the water of the caves goes up onto the surface of their columns.
    void ReleaseCaveWater()
    {
        caveKeys.Clear();
        caveKeys.AddRange(cave.Keys);
        caveKeys.Sort();
        foreach (int v in caveKeys) Water[v / Z] += cave[v];
        cave.Clear();
        CaveWaterView = Array.Empty<(int, float)>();
    }

    // For a test or the hand: water put straight onto a cave floor (the run's floor voxel).
    internal void SetCaveWater(int cell, int z, float depth)
    {
        if (depth > 0) cave[cell * Z + z] = depth; else cave.Remove(cell * Z + z);
    }

    // ---- save (version 12) ----

    void SyncWaterways(Sync s)
    {
        if (s.Version < 12)
        {
            if (s.Reading) WaterwaysFromOldFile();
            return;
        }
        s.A<float>(CurX); s.A<float>(CurY);
        s.V(ref Drifted);
        int n = cave.Count;
        s.V(ref n);
        if (s.Reading)
        {
            cave.Clear();
            for (int k = 0; k < n; k++)
            {
                int v = 0; float d = 0;
                s.V(ref v); s.V(ref d);
                cave[v] = d;
            }
        }
        else
        {
            caveKeys.Clear();
            caveKeys.AddRange(cave.Keys);
            caveKeys.Sort();
            foreach (int key in caveKeys)
            {
                int v = key; float d = cave[key];
                s.V(ref v); s.V(ref d);
            }
        }
        // The drift of every body, in the order of Agents (as SyncAgents wrote them).
        foreach (var a in Agents) { s.V(ref a.DriftX); s.V(ref a.DriftY); }
        if (s.Reading)
        {
            var view = new List<(int, float)>();
            foreach (int key in SortedKeys(cave)) view.Add((key, cave[key]));
            CaveWaterView = view.ToArray();
        }
    }

    // What a file before version 12 holds: no currents, no drift, dry caves.
    internal void WaterwaysFromOldFile()
    {
        Array.Clear(CurX); Array.Clear(CurY);
        cave.Clear();
        CaveWaterView = Array.Empty<(int, float)>();
        Drifted = 0;
        foreach (var a in Agents) a.DriftX = a.DriftY = 0;
    }

    static List<int> SortedKeys(Dictionary<int, float> d)
    {
        var keys = new List<int>(d.Keys);
        keys.Sort();
        return keys;
    }

    // ---- observation (bench metrics, no effect on the world) ----

    public static readonly string[] WaterNames = { "current_mean", "current_max", "drifted", "afloat_share", "cave_water", "cave_wet_runs", "cave_swim", "sea_ice_share", "under_ice" };

    // current_mean/max: the water's speed over swimmable surface water (cells a tick); drifted: steps
    // bodies were carried by currents so far; afloat_share: of all bodies, those in water off the bottom;
    // cave_water: water standing in caves (blocks), cave_wet_runs: how many wet runs; cave_swim: bodies in
    // cave water; sea_ice_share: of swimmable surface water, the share under ice (> 0.1 block);
    // under_ice: bodies in water under ice.
    public double[] WaterCensus()
    {
        var v = new double[WaterNames.Length];
        double sum = 0, max = 0;
        int sea = 0, iced = 0;
        for (int i = 0; i < N; i++)
        {
            if (Water[i] < P.SwimDepth) continue;
            sea++;
            double u = Math.Sqrt(CurX[i] * CurX[i] + CurY[i] * CurY[i]);
            sum += u; max = Math.Max(max, u);
            if (Ice[i] > 0.1f) iced++;
        }
        v[0] = sea > 0 ? sum / sea : 0; v[1] = max; v[2] = Drifted;
        int pop = 0, afloat = 0, caveSwim = 0, underIce = 0;
        foreach (var a in Agents)
        {
            if (a.Dead) continue;
            pop++;
            int c = a.Y * W + a.X;
            if (!InWater(c, a.Z)) continue;
            if (!OnFloor(a)) afloat++;
            if (a.Z < Height[c]) caveSwim++;
            else if (Ice[c] > 0.1f) underIce++;
        }
        v[3] = pop > 0 ? afloat / (double)pop : 0;
        v[4] = CaveWaterTotal(); v[5] = cave.Count; v[6] = caveSwim;
        v[7] = sea > 0 ? iced / (double)sea : 0; v[8] = underIce;
        return v;
    }
}
