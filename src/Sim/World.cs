using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Primordium;

public struct Flash
{
    public int X, Y, Kind, Dir, Spec;   // Dir: from the actor towards (X, Y), -1 if none
    public long T;
}

public enum EvKind { Move, Intake, Expel, Bind, Split, Photo, Express, Divide, Mate, Attack, Kill, Take, Give, Share, Link, Inject, Cut, Dig, Pile, Grow, Mine, Push, Look, Sediment, Count }

// A cylinder planet: x wraps around (longitude — the sun runs along it, giving day and night),
// y runs from the north pole (0) to the south pole (H-1). Every cell is a column of blocks; on top
// lies whatever bodies left behind (the dead, what they threw out) and, in the air, the gas. Agents
// live on exposed floors, including inside excavated cavities.
public sealed partial class World
{
    public const int W = P.W, H = P.H, N = W * H, Z = P.ZMax;
    public static readonly int[] DX = { 1, 0, -1, 0 }, DY = { 0, 1, 0, -1 };
    public const int FlashAttack = 0, FlashKill = 1, FlashInject = 2, FlashDig = 3, FlashPile = 4, FlashLink = 5, FlashExpel = 6, FlashDeath = 7,
        FlashGrow = 8, FlashStrike = 9;
    public const int FlashCap = 4096;

    // The most notable thing an agent did this tick (the view animates it). Higher wins.
    public const int ActMove = 1, ActEat = 2, ActExpel = 3, ActSocial = 4, ActDig = 5, ActDivide = 6, ActInject = 7, ActAttack = 8;

    public readonly int Seed;
    public readonly Chemistry Chem;
    // Agents are stepped in squares of about TileSize cells, coloured with a period that keeps squares
    // of one colour SafeGap cells apart (2×2 for 32): all squares of one colour in parallel, then the
    // next colour. Everything an agent touches in one tick lies within ~7
    // cells of where it started (its reach, a partner's reach and footprint, one step of movement) and
    // vision reads ≤16 cells, so squares stepped together (a whole square apart) never share a cell.
    // What is global is either thread-safe (concurrent sparse stores, Interlocked counters) or
    // collected per square and merged afterwards in square order (structural dirt, newborns,
    // discoveries, ids) — the result does not depend on thread timing.
    sealed class Ctx
    {
        public SimRng Rng;
        public int Slot;
        public long IdCount;
        public readonly long[] Ev = new long[(int)EvKind.Count];
        public readonly long[] Mined = new long[6], MinedCat = new long[6];   // molecules torn out of rock by grade (with a protein's help)
        public readonly List<int> Dirty = new();
        public readonly List<Agent> Newborn = new();
        public readonly List<Discovery> Firsts = new();
        public readonly List<(Agent from, Agent partner)> Unlinks = new();   // links broken with a partner out of reach
        // The chronicle (World.Chronicle.cs): proposed "firsts", the deepest body, tracked bodies that died.
        public readonly List<ChronCand> Chron = new();
        public readonly List<Agent> TrackedDeaths = new();
        public Agent DepthAgent;
        public int DepthBest;
        public double Busy;   // ms spent stepping this tile's agents (diagnostics)
        public readonly long[] OpTicks = new long[Genome.OpSlots + 2];   // with ProfileOps: time per instruction kind, + VM-less rest, + births
    }
    public static bool ProfileOps;   // tools/bench --ops: time every instruction kind (slows the run a little)
    public long[] OpTicks()
    {
        var t = new long[Genome.OpSlots + 2];
        foreach (var c in ctxs) for (int k = 0; k < t.Length; k++) { t[k] += c.OpTicks[k]; c.OpTicks[k] = 0; }
        return t;
    }
    public double AgentBusy, AgentLongest;   // diagnostics: summed tile work, and the slowest tile per colour, ms
    [ThreadStatic] static Ctx cur;
    readonly Ctx[] ctxs;
    readonly SimRng mainRng;
    public SimRng Rng => cur?.Rng ?? mainRng;

    // Loose matter lying on each cell, per species: remains of the dead and whatever bodies threw
    // out. It stays where it fell (it rots, and compacts into aggregates); only the gas spreads, as air.
    public readonly float[][] C = new float[Chemistry.S][];
    float[] back = new float[N];
    public readonly float[] Light = new float[N], Temp = new float[N], Ash = new float[N];
    public readonly float[] Photon = new float[N];               // light caught in a cell, shared by all bodies there
    readonly float[] ventHeat = new float[N], tmp = new float[N];

    public readonly List<Agent> Agents = new();

    public long Tick;
    public float SunX, SunDecl;
    public int Births, Spawns, MaxGen, DeathsStarve, DeathsKilled, DeathsBroken, DeathsClimate;
    public int Deaths => DeathsStarve + DeathsKilled + DeathsBroken + DeathsClimate + DeathsBuried + DeathsHand;
    public bool Abiogenesis;   // now and then a random newcomer (very rarely)
    public readonly long[] Ev = new long[(int)EvKind.Count];
    public readonly long[] Mined = new long[6], MinedCat = new long[6];
    public readonly Discovery[] Firsts;   // per material: who first broke it with a protein's help
    public readonly Flash[] Flashes = new Flash[FlashCap];
    public int FlashHead;

    readonly int[] nb = new int[N * 4];
    long nextId = 1;
    long nextStructure;
    // Tile layout (see the comment on Ctx and docs/SIMULATION.md). TileSize is the nominal side; tiles
    // of one colour are Period−1 tiles apart in x and in y, i.e. at least SafeGap cells.
    public static int TileSize = 32;   // tools/bench --tile N compares layouts (set before creating a world)
    public const int SafeGap = 32;     // ≥ reach 7 of one agent + look 16 of another, with margin
    public readonly int TilesX, TilesY, Tiles, Colours, PeriodX, PeriodY;
    readonly byte[] tileCol = new byte[W], tileRow = new byte[H];
    readonly List<Agent>[] tiles;
    readonly int[][] colour;          // tile indices of each colour
    readonly int[] phaseOrder;        // scratch: the tiles of a colour, most populous first
    int phaseNext;
    readonly List<Agent> newborn = new();

    public World(int seed, int initialPop = -1, bool abiogenesis = true)   // initialPop < 0: P.InitialPop
        : this(new WorldSettings { Seed = seed, InitialPop = initialPop, Abiogenesis = abiogenesis, Strikes = true }) { }   // strikes on, as before (bench, self-test)

    // A new world from its settings: a preset of laws in the settings replaces the current laws
    // (defaults for those it does not name) before anything is generated.
    public World(WorldSettings settings) : this(settings, TileSize, true) { }

    public readonly WorldSettings Settings;                 // what it was created with (a copy)
    public Dictionary<string, double> InitialLaws { get; private set; }   // every law's value when it was created
    public readonly int TileSide;                            // the nominal tile side it was built with (World.TileSize then)

    // generate = false: only the skeleton (tile layout, chemistry, neighbours) for a world that is
    // about to be filled from a save file.
    World(WorldSettings settings, int tileSize, bool generate)
    {
        Settings = settings = settings?.Clone() ?? new WorldSettings();
        if (generate && settings.Params != null) ParamRegistry.Restore(settings.Params);
        InitialLaws = ParamRegistry.Snapshot();
        int seed = settings.Seed, initialPop = settings.InitialPop < 0 ? P.InitialPop : settings.InitialPop;
        Seed = seed;
        Abiogenesis = settings.Abiogenesis;
        AutoStrikes = settings.Strikes;
        mainRng = new SimRng(seed);
        // Same-colour tiles must be SafeGap cells apart: Period − 1 tiles of at least the nominal size
        // between them. x wraps, so the number of tile columns is a multiple of the period; the widths
        // are spread evenly (some a cell wider).
        TileSide = tileSize;
        int size = Math.Clamp(tileSize, 8, 64);
        PeriodX = PeriodY = 1 + (SafeGap + size - 1) / size;
        TilesX = Math.Max(PeriodX, W / size / PeriodX * PeriodX);
        TilesY = Math.Max(1, H / size);
        Tiles = TilesX * TilesY;
        for (int x = 0; x < W; x++) tileCol[x] = (byte)(x * TilesX / W);
        for (int y = 0; y < H; y++) tileRow[y] = (byte)(y * TilesY / H);
        if ((PeriodX - 1) * (W / TilesX) < SafeGap || (TilesY >= PeriodY && (PeriodY - 1) * (H / TilesY) < SafeGap))
            throw new InvalidOperationException($"tile layout {TilesX}×{TilesY} leaves same-colour tiles closer than {SafeGap} cells");
        Colours = PeriodX * PeriodY;
        PhaseBusy = new double[Colours]; PhaseLongest = new double[Colours];
        colour = new int[Colours][];
        for (int q = 0; q < Colours; q++)
            colour[q] = Enumerable.Range(0, Tiles).Where(t => (t % TilesX) % PeriodX == q % PeriodX && (t / TilesX) % PeriodY == q / PeriodX).ToArray();
        phaseOrder = new int[Tiles];
        ctxs = new Ctx[Tiles];
        for (int k = 0; k < Tiles; k++) ctxs[k] = new Ctx { Rng = new SimRng(seed, 1 + k), Slot = k };
        paramsSeen = ParamRegistry.Version;   // the tables built below use the laws as they are now
        Chem = new Chemistry(seed);
        tiles = new List<Agent>[Tiles];
        for (int k = 0; k < Tiles; k++) tiles[k] = new List<Agent>();
        for (int i = 0; i < N; i++)
        {
            int x = i % W, y = i / W;
            for (int d = 0; d < 4; d++)
            {
                int ny = y + DY[d];
                nb[i * 4 + d] = ny < 0 || ny >= H ? i : ny * W + (x + DX[d] + W) % W;
            }
        }

        Firsts = new Discovery[Chem.MatCount];
        for (int s = 0; s < Chemistry.S; s++) C[s] = new float[N];
        if (!generate) return;

        Array.Fill(topologySeen, -1);
        GenerateTerrain();
        for (int k = 0; k < P.VentCount; k++) SpawnVent();
        RecomputeVentFields();

        // The primordial remains: a one-time endowment of loose matter like the ground it lies on.
        for (int i = 0; i < N; i++)
        {
            int top = TopMat(i);   // a handful of loose grains of the ground it lies on
            if (top >= 2) C[top - 2][i] += 8 * P.InitLitter * (0.5f + (float)Rng.NextDouble());
        }

        RecomputeFlow();
        InitWater();
        UpdateClouds();
        UpdateLight();
        for (int i = 0; i < N; i++) Temp[i] = TempEq(i);
        for (int k = 0, tries = 0; k < initialPop && tries < initialPop * 20; tries++)
            if (SpawnRandom()) k++;
    }

    public int Nb(int i, int d) => nb[i * 4 + d];
    public int Day => (int)(Tick / P.DayLen);
    public float DayFrac => (float)((double)Tick / P.DayLen % 1.0);
    public float YearFrac => (float)((double)Tick / ((double)P.DayLen * P.YearDays) % 1.0);

    public readonly double[] Prof = new double[8];   // ms spent: environment, agents, bookkeeping; [4..7] sky, diffusion, chemistry/climate, structure
    // Finer timings of every stage of the tick, ms summed since the caller last cleared them (tools/bench
    // prints them per tick and logs them with --log). The first eight keep their old meaning.
    public readonly double[] Detail = new double[DetailNames.Length];
    public static readonly string[] DetailNames =
    {
        "climate", "cellchem", "settle/weather", "body loads", "region", "solve", "failures", "settle agents",
        "sky", "diffusion", "erosion", "vents", "strikes", "metamorph", "tile sort", "agents", "merge", "relieve", "alarms", "abiogenesis", "burials",
        "chronicle",
    };
    public const int DClimate = 0, DCellChem = 1, DSettle = 2, DBodyLoads = 3, DRegion = 4, DSolve = 5, DFailures = 6, DSettleAgents = 7,
        DSky = 8, DDiffusion = 9, DErosion = 10, DVents = 11, DStrikes = 12, DMetamorph = 13, DTileSort = 14, DAgents = 15, DMerge = 16,
        DRelieve = 17, DAlarms = 18, DAbio = 19, DBurials = 20, DChronicle = 21;
    // Per colour of the agent phase: summed tile work and the slowest tile, ms (AgentBusy/AgentLongest are their totals).
    public readonly double[] PhaseBusy, PhaseLongest;
    public long AgentAllocated;   // bytes allocated during the agent phase (approximate: all threads)
    static readonly double MsPerStamp = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    long lapAt;
    void LapStart() => lapAt = System.Diagnostics.Stopwatch.GetTimestamp();
    void Lap(int k)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        Detail[k] += (now - lapAt) * MsPerStamp;
        lapAt = now;
    }
    readonly System.Diagnostics.Stopwatch prof = new();

    public void Step()
    {
        ApplyParamChanges();   // a law changed between ticks: refresh what was derived from it
        prof.Restart();
        LapStart();
        Tick++;
        if (Tick % (P.LightEvery * 2) == 0) UpdateClouds();
        if (Tick % P.LightEvery == 0) UpdateLight();
        Lap(DSky);
        double checkpoint = prof.Elapsed.TotalMilliseconds;
        Prof[4] += checkpoint;
        if (flowDirty && Tick % 16 == 0) RecomputeFlow();   // terrain changes often; flow weights can lag a little
        Diffuse();
        Lap(DDiffusion);
        Prof[5] += prof.Elapsed.TotalMilliseconds - checkpoint;
        checkpoint = prof.Elapsed.TotalMilliseconds;
        if (Tick % P.EnvEvery == 0) EnvChem();
        Prof[6] += prof.Elapsed.TotalMilliseconds - checkpoint;
        LapStart();
        if (Tick % P.ErodeEvery == 0) Erode();
        Lap(DErosion);
        StepVents();
        Lap(DVents);
        MaybeStrike();
        Lap(DStrikes);
        checkpoint = prof.Elapsed.TotalMilliseconds;
        // The more hanging rock there is, the more each pass costs; passes then come less often
        // (every 4 ticks with little, up to every 64 with a planet full of caves).
        if (Tick >= nextStructure)
        {
            StepStructure();
            nextStructure = Tick + P.StructureEvery * Math.Clamp(1 + LastStructureVoxels / 5000, 1, 16);
        }
        LapStart();
        if (Tick % P.MetamorphEvery == 0) Metamorphose();   // laps DMetamorph itself
        Lap(DBurials);
        Prof[7] += prof.Elapsed.TotalMilliseconds - checkpoint;
        Prof[0] += prof.Elapsed.TotalMilliseconds; prof.Restart();

        foreach (var b in tiles) b.Clear();
        // Where each body is is read in parallel (that is what costs: touching every body); the lists
        // are then filled in the order of Agents.
        int pop = SnapshotAgents();
        Parallel.For(0, (pop + 4095) / 4096, chunk =>
        {
            for (int i = chunk * 4096, end = Math.Min(pop, i + 4096); i < end; i++)
            {
                var a = everyone[i];
                agentTile[i] = a.Dead ? -1 : tileRow[a.Y] * TilesX + tileCol[a.X];
            }
        });
        for (int i = 0; i < pop; i++) if (agentTile[i] >= 0) tiles[agentTile[i]].Add(everyone[i]);
        Array.Clear(everyone, 0, pop);
        Lap(DTileSort);
        long allocated = GC.GetTotalAllocatedBytes(false);
        int workers = Environment.ProcessorCount;
        for (int q = 0; q < Colours; q++)
        {
            // The most populous tiles first, handed out one at a time to whichever worker is free:
            // a hot spot starts at once instead of after a batch of small tiles. Which thread steps a
            // tile does not matter (each has its own Ctx and Rng).
            var group = colour[q];
            int n = 0;
            foreach (int t in group) if (tiles[t].Count > 0) phaseOrder[n++] = t;
            Array.Sort(phaseOrder, 0, n, tileBySize ??= Comparer<int>.Create((p, r) =>
                tiles[p].Count != tiles[r].Count ? tiles[r].Count.CompareTo(tiles[p].Count) : p.CompareTo(r)));
            phaseNext = 0;
            Parallel.For(0, Math.Min(workers, n), _ =>
            {
                for (int k; (k = Interlocked.Increment(ref phaseNext) - 1) < n;) StepTile(phaseOrder[k]);
            });
            double longest = 0, busy = 0;
            for (int k = 0; k < n; k++) { var c = ctxs[phaseOrder[k]]; busy += c.Busy; longest = Math.Max(longest, c.Busy); }
            AgentBusy += busy; AgentLongest += longest;
            PhaseBusy[q] += busy; PhaseLongest[q] += longest;
        }
        AgentAllocated += GC.GetTotalAllocatedBytes(false) - allocated;
        Lap(DAgents);
        Prof[1] += prof.Elapsed.TotalMilliseconds; prof.Restart();
        Lap(DMerge);
        // The chronicle's proposals, tile by tile in tile order (before the newborns are listed).
        foreach (var ctx in ctxs) ChronMerge(ctx);
        foreach (var a in newborn) if (a.Tracked) Node(a);
        int firstsBefore = 0;
        foreach (var f in Firsts) if (f != null) firstsBefore++;
        Lap(DChronicle);
        foreach (var ctx in ctxs)
        {
            foreach (int c in ctx.Dirty) structuralDirty.Add(c);
            ctx.Dirty.Clear();
            Agents.AddRange(ctx.Newborn);
            ctx.Newborn.Clear();
            foreach (var f in ctx.Firsts) Firsts[f.Mat] ??= f;
            ctx.Firsts.Clear();
            foreach (var (from, partner) in ctx.Unlinks) from.Links.Remove(partner);
            ctx.Unlinks.Clear();
        }
        Agents.AddRange(newborn);
        newborn.Clear();
        ChronDiscoveries(firstsBefore);   // (its finder may have died this tick: still listed)
        RemoveDead();
        Lap(DMerge);
        if (Tick % SurveyEvery == 0) ChronSurvey();
        Lap(DChronicle);
        Relieve();   // overfull floors let their smallest bodies go (see World.Volume)
        Lap(DRelieve);
        int alarmStart = (int)(Tick % 16) * (N / 16);
        for (int c = alarmStart; c < alarmStart + N / 16; c++)
            if (lastAttacker[c] != null && (lastAttacker[c].Dead || Tick - lastAttack[c] > 16)) lastAttacker[c] = null;
        foreach (var ctx in ctxs)
        {
            for (int k = 0; k < ctx.Ev.Length; k++) { Ev[k] += ctx.Ev[k]; ctx.Ev[k] = 0; }
            for (int k = 0; k < 6; k++) { Mined[k] += ctx.Mined[k]; MinedCat[k] += ctx.MinedCat[k]; ctx.Mined[k] = ctx.MinedCat[k] = 0; }
        }
        Lap(DAlarms);
        Prof[2] += prof.Elapsed.TotalMilliseconds;
        // Abiogenesis is very rare on a living planet; on a nearly empty one the untouched primordial
        // soup tries far more often.
        float barren = Math.Max(0, 1 - Agents.Count / 200f);
        if (Abiogenesis && mainRng.NextDouble() < P.AbioChance * (1 + 160 * barren)) SpawnRandom();
        Lap(DAbio);
    }

    IComparer<int> tileBySize;

    // Agents.RemoveAll(a => a.Dead), with the bodies looked at in parallel; the order is kept.
    void RemoveDead()
    {
        int total = SnapshotAgents();
        Parallel.For(0, (total + 4095) / 4096, chunk =>
        {
            for (int i = chunk * 4096, end = Math.Min(total, i + 4096); i < end; i++) agentTile[i] = everyone[i].Dead ? 1 : 0;
        });
        int alive = 0;
        for (int i = 0; i < total; i++) if (agentTile[i] == 0) everyone[alive++] = everyone[i];
        if (alive < total)
        {
            Agents.Clear();
            Agents.AddRange(new ArraySegment<Agent>(everyone, 0, alive));
        }
        Array.Clear(everyone, 0, total);
    }
    int[] agentTile = new int[1024];
    Agent[] everyone = new Agent[1024];

    // One tile's agents, in an order shuffled by the tile's own random stream.
    void StepTile(int t)
    {
        var ctx = ctxs[t];
        cur = ctx;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var list = tiles[t];
            var rng = ctx.Rng;
            for (int k = list.Count - 1; k > 0; k--) { int j = rng.Next(k + 1); (list[k], list[j]) = (list[j], list[k]); }
            foreach (var a in list) if (!a.Dead) Live(a);
        }
        finally { cur = null; }
        ctx.Busy = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * MsPerStamp;
    }

    static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    // One sun. It circles the planet along x; its latitude swings with the seasons. Light fades in
    // and out gradually at the terminator; mountains cast shadows, vent ash and clouds dim the sky,
    // water swallows light with depth, snow buries what grows under it.
    void UpdateLight()
    {
        double day = (double)Tick / P.DayLen;
        SunX = (float)(day % 1.0 * W);
        SunDecl = (float)(P.Tilt * Math.Sin(2 * Math.PI * day / P.YearDays));
        float sd = MathF.Sin(SunDecl), cd = MathF.Cos(SunDecl), sunX = SunX;
        int top = 0;   // nothing stands higher than the highest column: shadow rays stop there
        for (int i = 0; i < N; i++) if (Height[i] > top) top = Height[i];
        Parallel.For(0, H, y =>
        {
            float lat = (0.5f - (y + 0.5f) / H) * MathF.PI * 0.92f;
            float sl = MathF.Sin(lat), cl = MathF.Cos(lat);
            // Climate of this latitude today: how high the sun climbs at noon.
            climRow[y] = P.TPole + (P.TEquator - P.TPole) * MathF.Pow(Math.Max(0f, MathF.Cos(lat - SunDecl)), 1.3f);
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                float ha = 2 * MathF.PI * (x + 0.5f - sunX) / W;
                float ch = MathF.Cos(ha), sh = MathF.Sin(ha);
                float se = sl * sd + cl * cd * ch;              // sine of the sun's elevation
                float l = Smooth(-0.04f, 0.3f, se);
                if (l > 0)
                {
                    float de = -sh * cd, dn = cl * sd - sl * cd * ch; // towards the sun: east, north
                    float len = MathF.Sqrt(de * de + dn * dn);
                    if (len > 1e-4f)
                    {
                        de /= len; dn /= len;
                        float e = MathF.Max(se, 0.02f);
                        float tanE = e / MathF.Sqrt(MathF.Max(1e-4f, 1 - e * e));
                        float h0 = Height[i] * P.BlockH, maxRise = top * P.BlockH - h0;
                        for (int k = 1; k <= 24 && maxRise >= k * tanE; k++)
                        {
                            int py = (int)MathF.Round(y - dn * k);
                            if (py < 0 || py >= H) break;
                            int px = (((int)MathF.Round(x + de * k)) % W + W) % W;
                            if (Height[py * W + px] * P.BlockH - h0 > k * tanE) { l *= 0.15f; break; }
                        }
                    }
                }
                // Photons are counted where they reach the water's surface (a body catches them only as deep as
                // they get, see Photo); Light is what is left of it at the floor.
                float lit = l * (1 - Ash[i]) * (1 - 0.3f * Cloud[i]) * MathF.Exp(-P.WaterDim * Ice[i]) * (1 - 0.7f * Math.Min(1f, Snow[i] * 3));
                Light[i] = lit * MathF.Exp(-P.WaterDim * Water[i]);
                Photon[i] = Math.Min(P.PhotonCap, Photon[i] + lit * P.PhotonK * P.LightEvery);
            }
        });
    }

    // Only the gas moves on its own: it spreads through the air (hardly over walls).
    void Diffuse()
    {
        var c = C[Chem.Gas];
        var next = back;
        float d = Chem.Diff[Chem.Gas];
        Parallel.For(0, H / 8, chunk =>   // each cell reads the old buffer only: rows are independent
        {
            for (int i = chunk * 8 * W, end = i + 8 * W; i < end; i++)
            {
                int b = i * 4;
                float ci = c[i];
                next[i] = ci + d * (diffW[b] * (c[nb[b]] - ci) + diffW[b + 1] * (c[nb[b + 1]] - ci)
                                  + diffW[b + 2] * (c[nb[b + 2]] - ci) + diffW[b + 3] * (c[nb[b + 3]] - ci));
            }
        });
        C[Chem.Gas] = back; back = c; // swap buffers instead of copying a planet every tick
    }

    readonly bool[] weathering = new bool[N];

    void EnvChem()
    {
        LapStart();
        UpdateClimate();
        Lap(0);
        // Each worker owns distinct cells; structural mutations are committed after the barrier.
        Parallel.For(0, H / 8, chunk =>
        {
            for (int i = chunk * 8 * W, end = i + 8 * W; i < end; i++) CellChem(i, (int)Tick);
        });
        Lap(1);
        for (int i = 0; i < N; i++)
        {
            if (LooseVolume[i] > P.CompactShare * P.VoxelSpace) Settle(i, LooseVolume[i]);
            // Precipitation adsorbs existing atmospheric molecules onto the exposed aggregate.
            if (Tick % P.MetamorphEvery == 0 && Rain[i] > 0 && Height[i] > 2 && RainSum > 0)
            {
                float captured = C[Chem.Gas][i] * Math.Min(0.05f, Rain[i] * Moisture * P.RainShare / RainSum * (P.MetamorphEvery / P.EnvEvery));
                if (captured > 0.00001f)
                {
                    int v = i * Z + Height[i] - 1;
                    C[Chem.Gas][i] -= captured; BurialAt(v).Matter[Chem.Gas] += captured; MassChanged(v);
                }
            }
            if (weathering[i])
            {
                int molecule = TakeVoxelMolecule(i * Z + Height[i] - 1);
                if (molecule >= 0) C[molecule][i] += 1;
            }
        }
        foreach (var v in Vents)
            if (Rng.NextDouble() < 0.006 * v.Strength) BuildCone(v);
        Lap(2);
    }

    void CellChem(int i, int tick)
    {
        float f = TempFactor(Temp[i]), total = 0;
        int gas = Chem.Gas;
        for (int s = 0; s < Chemistry.S; s++)
        {
            float amount = C[s][i];
            if (amount <= 0) continue;
            if (s != gas) total += amount * Chem.Volume[s];
            if (Chem.SplitExo[s])
            {
                float m = amount * 0.0005f * f;
                C[s][i] -= m; C[Chem.SplitA[s]][i] += m;
                if (Chem.SplitB[s] >= 0) C[Chem.SplitB[s]][i] += m;
                heatIn[i] += m * Chem.SplitEnergy(s);
            }
        }
        // Atmospheric material remains in the budget even over water. Rain deposits existing
        // molecules; it is not a source of elements. Loose deposits compact their actual inventory.
        LooseVolume[i] = total * P.LooseBulk; weathering[i] = false;
        int h = Height[i];
        if (h <= 2) return;
        int v = i * Z + h - 1;
        float weather = 0.0002f * f * (1 + Water[i] + Rain[i]) / (0.1f + VoxelCohesion(v));
        weathering[i] = Units[v] > 0 && Hash32.F(tick, i) < weather;
    }

    void AddFlash(int x, int y, int kind, int dir = -1, int spec = 0) =>
        Flashes[(Interlocked.Increment(ref FlashHead) - 1) & (FlashCap - 1)] = new Flash { X = x, Y = y, Kind = kind, Dir = dir, Spec = spec, T = Tick };

    void Act(Agent a, int kind, int dir)
    {
        if (a.ActTick == Tick && kind < a.Act) return;
        a.Act = kind; a.ActDir = dir; a.ActTick = Tick;
    }

    // Structural work for later (the support solver runs between agent phases).
    void MarkDirty(int cell)
    {
        var c = cur;
        if (c != null) c.Dirty.Add(cell);
        else structuralDirty.Add(cell);
    }

    // Ids are unique and reproducible: each square numbers its own newborns. 64-bit: a busy square
    // would run past int after ~52 million births.
    long NewId()
    {
        var c = cur;
        if (c == null) return (nextId++) * (Tiles + 1) + Tiles;
        return (++c.IdCount) * (Tiles + 1) + c.Slot;
    }

    void Note(EvKind k)
    {
        var c = cur;
        if (c != null) c.Ev[(int)k]++;
        else Ev[(int)k]++;
    }

    // Loose remains per cell, on average (the gas in the air not counted).
    public float MeanLitter()
    {
        double t = 0;
        for (int s = 0; s < Chemistry.S; s++)
        {
            if (s == Chem.Gas) continue;
            var c = C[s];
            for (int i = 0; i < N; i++) t += c[i];
        }
        return (float)(t / N);
    }

    public float MeanHeight()
    {
        long t = 0;
        foreach (var h in Height) t += h;
        return t / (float)N;
    }
}

// The first time a material was broken with a protein's help (the step that opens a new resource).
public sealed class Discovery
{
    public long Tick;
    public long Lineage, AgentId;
    public int Mat;
}
