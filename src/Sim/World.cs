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
    // The world's size (WorldSettings.Width/Height/Levels; 256×160×192 by default): W columns around
    // (x wraps), H rows from pole to pole, N = W·H cells, Z levels in every column. Fixed for the world's
    // life; hot loops read them into locals.
    public readonly int W, H, N, Z;
    // What is given per planet (the first bodies, volcanoes, strikes, slope creep a tick) was set for the
    // default world's area; a world of another size gets it in proportion to its own (PerArea). The default
    // world takes the numbers as they are.
    public const int RefN = WorldSettings.DefaultWidth * WorldSettings.DefaultHeight;
    public int PerArea(int perDefaultWorld) => N == RefN ? perDefaultWorld : (int)Math.Round((double)perDefaultWorld * N / RefN, MidpointRounding.AwayFromZero);
    // Half-width in x of a disk of radius r that touches no column twice (x wraps): r itself unless the world
    // is narrower than the disk.
    int AroundX(int r) => Math.Min(r, (W - 1) / 2);
    // x wrapped around the planet; no division when it is already inside (the usual case).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public int WrapX(int x) => (uint)x < (uint)W ? x : ((x % W) + W) % W;
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
        public readonly long[] GeoMined = new long[8];                        // ... by depth (World.Geochem, observation)
        public readonly double[] Organ = new double[OrganStatN];             // sense readings and organ costs (World.Organs, observation)
        public readonly double[] Fid = new double[FidStatN];                 // copies, proofreading passes and their cost (World.Fidelity, observation)
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
        public Agent Body;    // the body whose tick this is (P.MatterEnergy 1: its costs are settled at the end, World.Charge)
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
    public SimRng Rng => cur?.Rng ?? lifeRng ?? mainRng;
    SimRng lifeRng;   // only while the first bodies are made, if a life seed is given (see the constructor)
    public int LifeSeed => Settings.LifeSeed;

    // Loose matter lying on each cell, per species: remains of the dead and whatever bodies threw
    // out. It stays where it fell (it rots, and compacts into aggregates); only the gas spreads, as air.
    // In fixed point (Qty): moving matter between cells and pools conserves atoms exactly.
    public readonly Qty[][] C = new Qty[Chemistry.S][];
    Qty[] back;
    public readonly float[] Light, Temp, Ash;
    public readonly float[] Photon;               // light caught in a cell, shared by all bodies there
    readonly float[] ventHeat, tmp;

    public readonly List<Agent> Agents = new();

    public long Tick;
    public float SunX, SunDecl;
    public int Births, Spawns, MaxGen, DeathsStarve, DeathsKilled, DeathsBroken, DeathsClimate;
    public int Deaths => DeathsStarve + DeathsKilled + DeathsBroken + DeathsClimate + DeathsBuried + DeathsHand + DeathsFlare;
    public bool Abiogenesis;   // now and then a random newcomer (very rarely)
    public readonly long[] Ev = new long[(int)EvKind.Count];
    public readonly long[] Mined = new long[6], MinedCat = new long[6];
    public readonly Discovery[] Firsts;   // per material: who first broke it with a protein's help
    public readonly Flash[] Flashes = new Flash[FlashCap];
    public int FlashHead;

    readonly int[] nb;
    long nextId = 1;
    long nextStructure;
    // Tile layout (see the comment on Ctx and docs/SIMULATION.md). TileSize is the nominal side; tiles
    // of one colour are Period−1 tiles apart in x and in y, i.e. at least SafeGap cells.
    public static int TileSize = 32;   // tools/bench --tile N compares layouts (set before creating a world)
    public const int SafeGap = 32;     // ≥ reach 7 of one agent + look 16 of another, with margin
    public readonly int TilesX, TilesY, Tiles, Colours, PeriodX, PeriodY;
    readonly byte[] tileCol, tileRow;
    readonly List<Agent>[] tiles;
    readonly int[][] colour;          // tile indices of each colour
    readonly int[] phaseOrder;        // scratch: the tiles of a colour, most populous first
    int phaseNext;
    readonly List<Agent> newborn = new();

    public World(int seed, int initialPop = -1, bool abiogenesis = true, int lifeSeed = 0)   // initialPop < 0: P.InitialPop
        // Strikes on, as before (bench, self-test) — unless solar flares are on: they replace the strikes.
        : this(new WorldSettings { Seed = seed, InitialPop = initialPop, Abiogenesis = abiogenesis, Strikes = !FlareLaw, LifeSeed = lifeSeed }) { }

    // A new world from its settings: a preset of laws in the settings replaces the current laws
    // (defaults for those it does not name) before anything is generated.
    public World(WorldSettings settings) : this(settings, TileSize, true) { }

    public readonly WorldSettings Settings;                 // what it was created with (a copy)
    public Dictionary<string, double> InitialLaws { get; private set; }   // every law's value when it was created
    public readonly int TileSide;                            // the nominal tile side it was built with (World.TileSize then)

    // generate = false: only the skeleton (tile layout, chemistry, neighbours) for a world that is
    // about to be filled from a save file.
    //
    // Settings.LifeSeed ≠ 0 perturbs life only: the first bodies (genomes, places) and the agents'
    // random streams. Elements, terrain, vents, water and the primordial litter stay those of the
    // seed, so repeats of one seed with different life seeds show how much of an outcome is chance.
    // 0 is the original world of the seed.
    // chemModel: the chemistry's energy model (Chemistry.Model); < 0 — the current law, P.ChemEnergyModel.
    World(WorldSettings settings, int tileSize, bool generate, int chemModel = -1)
    {
        Settings = settings = settings?.Clone() ?? new WorldSettings();
        settings.Validate();
        W = settings.Width; H = settings.Height; N = W * H; Z = settings.Levels;
        Crust = CrustFor(Z);
        RegionsX = W / RegionSide; RegionsY = H / RegionSide; Regions = RegionsX * RegionsY;
        // Every per-cell and per-voxel array, sized by the world (the fields are declared next to the
        // code that uses them, by file; readonly, so they are made here).
        // World.Body.cs
        Big = new Agent[N];
        // World.BodyOps.cs
        lastAttacker = new Agent[N]; lastAttack = new long[N];
        // World.Cave.cs
        Tmean = new float[N]; CaveWarm = new float[N]; caveHeatIn = new float[N];
        // World.Climate.cs
        Water = new float[N]; Ice = new float[N]; Snow = new float[N]; Cloud = new float[N]; Rain = new float[N]; climRow = new float[H];
        flowOut = new float[N * 4]; rowSum = new float[H]; heatIn = new float[N]; BodyHeat = new float[N]; DeathMap = new float[N];
        // World.ClimateCycles.cs
        Veil = new float[N]; veilRowTrans = new float[H]; veilT = new float[N]; climShift = new float[H]; dryW = new float[N]; dryT = new float[N];
        veilRow = new float[H]; veilNext = new float[H];
        // World.Energy.cs
        rowLooseDecay = new double[H];
        // World.Geochem.cs
        Height0 = new int[N];
        // World.Life.cs
        Head = new Agent[N]; Count = new int[N];
        // World.Matter.cs
        sparse = new byte[N * Z]; Bite = new float[N]; biteAt = new long[N]; biteFace = InitFaces(); biteMat = new byte[N];
        cohesionCache = new float[N * Z]; annealable = InitAnnealable();
        // World.Settle.cs
        settleDebt = new float[N]; elasticSeen = InitElasticSeen(); settleCheck = new bool[N]; settleDue = new bool[N];
        // World.Sky.cs
        Sun = new float[N]; Transp = new float[N]; yearInsol = new float[H]; bigCaught = new float[N];
        // World.Structure.cs
        overhang = new bool[N * Z]; overhangCount = new int[N]; grounded = new int[N]; Pressure = new float[N * Z];
        compressionCache = new float[N * Z]; topologySeen = new int[N]; topologyVersion = new int[N]; HasCavity = new bool[N];
        bodyLoad = new LoadMap(N, Z); nextBodyLoad = new LoadMap(N, Z);
        // World.Resources.cs
        resGas = new long[Regions]; resFood = new long[Regions];
        // World.Terrain.cs
        Height = new int[N]; Mat = new byte[N * Z]; Units = new ushort[N * Z]; Order = new byte[N * Z]; ColumnVersion = new int[N];
        diffW = new float[N * 4];
        // World.Volume.cs
        LooseVolume = new float[N]; overfull = new bool[N];
        // World.Waterways.cs
        CurX = new float[N]; CurY = new float[N]; colFirst = new int[N]; colRuns = new int[N];
        // World.cs
        back = new Qty[N]; Light = new float[N]; Temp = new float[N]; Ash = new float[N]; Photon = new float[N]; ventHeat = new float[N];
        tmp = new float[N]; nb = new int[N * 4]; tileCol = new byte[W]; tileRow = new byte[H]; weathering = new bool[N];
        if (generate && settings.Params != null) ParamRegistry.Restore(settings.Params);
        InitialLaws = ParamRegistry.Snapshot();
        int seed = settings.Seed, initialPop = settings.InitialPop < 0 ? PerArea(P.InitialPop) : settings.InitialPop;
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
        // A world too narrow for one period of tiles around it is stepped as a single column of tiles
        // (one colour across: nothing in parallel along x).
        if (W / size < PeriodX) TilesX = PeriodX = 1;
        TilesY = Math.Max(1, H / size);
        Tiles = TilesX * TilesY;
        for (int x = 0; x < W; x++) tileCol[x] = (byte)(x * TilesX / W);
        for (int y = 0; y < H; y++) tileRow[y] = (byte)(y * TilesY / H);
        if ((TilesX > 1 && (PeriodX - 1) * (W / TilesX) < SafeGap) || (TilesY >= PeriodY && (PeriodY - 1) * (H / TilesY) < SafeGap))
            throw new InvalidOperationException($"tile layout {TilesX}×{TilesY} leaves same-colour tiles closer than {SafeGap} cells");
        Colours = PeriodX * PeriodY;
        PhaseBusy = new double[Colours]; PhaseLongest = new double[Colours]; TileBusy = new double[Tiles];
        colour = new int[Colours][];
        for (int q = 0; q < Colours; q++)
            colour[q] = Enumerable.Range(0, Tiles).Where(t => (t % TilesX) % PeriodX == q % PeriodX && (t / TilesX) % PeriodY == q / PeriodX).ToArray();
        phaseOrder = new int[Tiles];
        ctxs = new Ctx[Tiles];
        // A life seed moves the tiles' streams to others (and gives the first bodies their own, below).
        long life = settings.LifeSeed == 0 ? 0 : (long)Hash32.U((uint)settings.LifeSeed * 2654435761u) << 32 | 1;
        for (int k = 0; k < Tiles; k++) ctxs[k] = new Ctx { Rng = new SimRng(seed ^ life, 1 + k), Slot = k };
        paramsSeen = ParamRegistry.Version;   // the tables built below use the laws as they are now
        Chem = new Chemistry(seed, chemModel >= 0 ? chemModel : P.ChemEnergyModel);
        Decay = new DecayPaths(Chem);   // World.Decay: each species' downhill path and barrier (P.ArrheniusDecay)
        decayRows = new List<int>[H];
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
        reliefScale = generate ? P.ReliefScale : 1;   // a loaded world takes its own (World.Save, version 11)
        InitGeochem();   // depth biases from the seed (a loaded world then takes its own, World.Geochem)
        for (int s = 0; s < Chemistry.S; s++) C[s] = new Qty[N];
        if (!generate) return;

        Array.Fill(topologySeen, -1);
        GenerateTerrain();   // (after InitGeochem above: the strata follow the depth profile)
        for (int k = 0; k < VentTarget; k++) SpawnVent();
        RecomputeVentFields();

        // The primordial remains: a one-time endowment of loose matter like the ground it lies on. In the
        // chemistry from bonds (Chemistry.Model 1) the rock is relaxed (ground states) and the primordial
        // soup is the energised part: the grains lie in their excited state (the surface under the young
        // sun), the only fuel the first bodies find besides light. The legacy chemistry: as the rock.
        for (int i = 0; i < N; i++)
        {
            int top = TopMat(i);   // a handful of loose grains of the ground it lies on
            if (top < 2) continue;
            int s = top - 2;
            if (Chem.Model != 0 && Chem.PhotoUp[s] >= 0) s = Chem.PhotoUp[s];
            C[s][i] += 8 * P.InitLitter * (0.5f + (float)Rng.NextDouble());
        }

        RecomputeFlow();
        InitWater();
        if (TranspLaw) UpdateTransparency(); else Array.Fill(Transp, 1f);
        UpdateClouds();
        UpdateLight();
        for (int i = 0; i < N; i++) Temp[i] = TempEq(i);
        InitCaveClimate();
        ClimateFromOldFile();   // the climate cycles (World.ClimateCycles) start at their neutral phase at tick 0
        if (life != 0) lifeRng = new SimRng(seed ^ life, -7349);
        for (int k = 0, tries = 0; k < initialPop && tries < initialPop * 20; tries++)
            if (SpawnRandom()) k++;
        lifeRng = null;
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
        "chronicle", "evolution",
    };
    public const int DClimate = 0, DCellChem = 1, DSettle = 2, DBodyLoads = 3, DRegion = 4, DSolve = 5, DFailures = 6, DSettleAgents = 7,
        DSky = 8, DDiffusion = 9, DErosion = 10, DVents = 11, DStrikes = 12, DMetamorph = 13, DTileSort = 14, DAgents = 15, DMerge = 16,
        DRelieve = 17, DAlarms = 18, DAbio = 19, DBurials = 20, DChronicle = 21, DEvolution = 22;
    // Per colour of the agent phase: summed tile work and the slowest tile, ms (AgentBusy/AgentLongest are their totals).
    public readonly double[] PhaseBusy, PhaseLongest;
    public readonly double[] TileBusy;   // per tile: its work in the agent phase, ms (diagnostics, summed like PhaseBusy)
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
        if (ArrheniusLaw) RefreshDecayA();   // World.Decay: A = 10^DecayLogA for the parallel phases
        ResMark(-1);   // the resource probe (World.Resources, observation): start of the tick
        StepClimateCycles();   // World.ClimateCycles: is anything of the non-stationary climate running
        if (TranspLaw && (!transpValid || Tick % (P.LightEvery * 8) == 0)) UpdateTransparency();
        else if (!TranspLaw && transpValid) { transpValid = false; Array.Fill(Transp, 1f); TranspMean = 1; }
        if (Tick % (P.LightEvery * 2) == 0) UpdateClouds();
        if (Tick % P.LightEvery == 0) UpdateLight();
        StepSky();   // solar activity and flares now (World.Sky)
        Lap(DSky);
        double checkpoint = prof.Elapsed.TotalMilliseconds;
        Prof[4] += checkpoint;
        if (flowDirty && Tick % 16 == 0) RecomputeFlow();   // terrain changes often; flow weights can lag a little
        ResMark(ResourceProbe.POther);
        Diffuse();
        ResMark(ResourceProbe.PDiffusion);
        Lap(DDiffusion);
        Prof[5] += prof.Elapsed.TotalMilliseconds - checkpoint;
        checkpoint = prof.Elapsed.TotalMilliseconds;
        if (Tick % P.EnvEvery == 0) EnvChem();
        ResMark(ResourceProbe.PEnv);
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
        // (every StructureEvery ticks with little, up to StructureStretch times as rarely with a planet
        // full of caves). A resolution of the solver in time, not a law of matter: P.StructureVoxels and
        // P.StructureStretch (ParamRegistry, Mechanics).
        if (Tick >= nextStructure)
        {
            StepStructure();
            nextStructure = Tick + P.StructureEvery * Math.Clamp(1 + LastStructureVoxels / Math.Max(1, P.StructureVoxels), 1, Math.Max(1, P.StructureStretch));
        }
        LapStart();
        if (Tick % P.MetamorphEvery == 0) Metamorphose();   // laps DMetamorph itself
        if (ArrheniusLaw && Tick % P.MetamorphEvery == 0) DeepDecay();   // World.Decay: burials and blocks
        Lap(DBurials);
        Prof[7] += prof.Elapsed.TotalMilliseconds - checkpoint;
        Prof[0] += prof.Elapsed.TotalMilliseconds; prof.Restart();
        ResMark(ResourceProbe.POther);

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
            for (int k = 0; k < n; k++) { var c = ctxs[phaseOrder[k]]; busy += c.Busy; longest = Math.Max(longest, c.Busy); TileBusy[phaseOrder[k]] += c.Busy; }
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
            evoBorn.AddRange(ctx.Newborn);   // World.Evolution: births in merge order
            ctx.Newborn.Clear();
            foreach (var f in ctx.Firsts) Firsts[f.Mat] ??= f;
            ctx.Firsts.Clear();
            foreach (var (from, partner) in ctx.Unlinks) from.Links.Remove(partner);
            ctx.Unlinks.Clear();
        }
        Agents.AddRange(newborn);
        evoBorn.AddRange(newborn);
        newborn.Clear();
        ChronDiscoveries(firstsBefore);   // (its finder may have died this tick: still listed)
        RemoveDead();
        Lap(DMerge);
        if (Tick % SurveyEvery == 0) ChronSurvey();
        Lap(DChronicle);
        EvoTick();   // the neutral shadow, the family tree, the progress tracks (observation only)
        Lap(DEvolution);
        Relieve();   // overfull floors let their smallest bodies go (see World.Volume)
        ResMark(ResourceProbe.PBio);
        Lap(DRelieve);
        int alarmStart = (int)(Tick % 16) * (N / 16);
        for (int c = alarmStart; c < alarmStart + N / 16; c++)
            if (lastAttacker[c] != null && (lastAttacker[c].Dead || Tick - lastAttack[c] > 16)) lastAttacker[c] = null;
        foreach (var ctx in ctxs)
        {
            for (int k = 0; k < ctx.Ev.Length; k++) { Ev[k] += ctx.Ev[k]; ctx.Ev[k] = 0; }
            for (int k = 0; k < 6; k++) { Mined[k] += ctx.Mined[k]; MinedCat[k] += ctx.MinedCat[k]; ctx.Mined[k] = ctx.MinedCat[k] = 0; }
            for (int k = 0; k < GeoMined.Length; k++) { GeoMined[k] += ctx.GeoMined[k]; ctx.GeoMined[k] = 0; }
            MergeOrganStats(ctx.Organ);
        }
        Lap(DAlarms);
        Prof[2] += prof.Elapsed.TotalMilliseconds;
        // Abiogenesis. P.AbioModel 1: from each cell's own chemistry (World.Life, AbioLocal). 0 (legacy):
        // very rare on a living planet; on a nearly empty one the untouched primordial soup tries far more often.
        if (Abiogenesis && P.AbioModel != 0) AbioLocal();
        else
        {
            float barren = Math.Max(0, 1 - Agents.Count / 200f);
            if (Abiogenesis && mainRng.NextDouble() < P.AbioChance * (1 + 160 * barren)) SpawnRandom();
        }
        ResMark(ResourceProbe.POther);
        ResTick();
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
        for (int i = 0; i < total; i++)
            if (agentTile[i] == 0) everyone[alive++] = everyone[i];
            else evoDied.Add(everyone[i]);   // World.Evolution: deaths in the order of Agents
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
    // water swallows light with depth, snow buries what grows under it. With the laws of World.Sky:
    // the cosine law, the sky's transparency, the moon's shadow and the sun's brightness by its activity.
    void UpdateLight()
    {
        double day = (double)Tick / P.DayLen;
        SunX = (float)(day % 1.0 * W);
        float tilt = climOn ? TiltAt(Tick) : P.Tilt;   // the tilt swings with the climate cycles (World.ClimateCycles)
        SunDecl = (float)(tilt * Math.Sin(2 * Math.PI * day / P.YearDays));
        float sd = MathF.Sin(SunDecl), cd = MathF.Cos(SunDecl), sunX = SunX;
        int top = 0;   // nothing stands higher than the highest column: shadow rays stop there
        for (int i = 0; i < N; i++) if (Height[i] > top) top = Height[i];
        bool insol = InsolLaw, sky = TranspLaw;
        int rayLen = Math.Min(P.ShadowReach, W - 1);   // how far a shadow ray looks towards the sun
        if (insol) EnsureInsolNorms(climOn ? MathF.Round(tilt / 0.002f) * 0.002f : P.Tilt);
        StepEclipse();
        bool ecl = EclipseNow;
        float ex = EclipseX, ey = EclipseY, reach = 1.3f * P.EclipseR + 1;
        float lum = FlareLaw ? 1 + P.SolarLumAmp * (2 * ActivityAt(Tick) - 1) : 1;
        // The climate cycles: the sun's drift and the orbit dim or brighten the light, the ash veil too, and
        // shift each latitude's climate (with the ice ages); nothing of it with them off.
        bool clim = climOn, veil = veilOn;
        if (clim) { lum *= CycleLum(); PrepareClimShift(lum); }
        Parallel.For(0, H, y =>
        {
            int W = this.W, H = this.H;   // (hot loops read the size into locals)
            float lat = (0.5f - (y + 0.5f) / H) * MathF.PI * 0.92f;
            float sl = MathF.Sin(lat), cl = MathF.Cos(lat);
            // Climate of this latitude today: how high the sun climbs at noon — with the cosine law, the
            // day's insolation sum held back by the year's (World.Sky).
            climRow[y] = insol ? ClimateOf(y, SunDecl) : P.TPole + (P.TEquator - P.TPole) * MathF.Pow(Math.Max(0f, MathF.Cos(lat - SunDecl)), 1.3f);
            if (clim) climRow[y] += climShift[y];
            bool shadowRow = ecl && MathF.Abs(y + 0.5f - ey) < reach;
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                float ha = 2 * MathF.PI * (x + 0.5f - sunX) / W;
                float ch = MathF.Cos(ha), sh = MathF.Sin(ha);
                float se = sl * sd + cl * cd * ch;              // sine of the sun's elevation
                float l = insol ? Insol(se) : Smooth(-0.04f, 0.3f, se);
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
                        for (int k = 1; k <= rayLen && maxRise >= k * tanE; k++)
                        {
                            int py = (int)MathF.Round(y - dn * k);
                            if (py < 0 || py >= H) break;
                            int px = (int)MathF.Round(x + de * k);   // k ≤ rayLen < W: one wrap at most
                            if (px < 0) px += W; else if (px >= W) px -= W;
                            if (Height[py * W + px] * P.BlockH - h0 > k * tanE) { l *= P.ShadowLight; break; }
                        }
                    }
                    if (sky) l *= Transp[i];
                    if (shadowRow) l *= EclipseShade(x, y, ex, ey);
                    if (lum != 1) l *= lum;
                    if (veil) l *= veilT[i];
                }
                // Photons are counted where they reach the water's surface (a body catches them only as deep as
                // they get, see Photo); Light is what is left of it at the floor.
                float lit = l * (1 - Ash[i]) * (1 - P.CloudDim * Cloud[i]) * MathF.Exp(-P.WaterDim * Ice[i]) * (1 - P.SnowDim * Math.Min(1f, Snow[i] * P.SnowCover));
                Sun[i] = lit;
                Light[i] = lit * MathF.Exp(-P.WaterDim * Water[i]);
                Photon[i] = Math.Min(P.PhotonCap, Photon[i] + lit * P.PhotonK * P.LightEvery);
            }
        });
        if (CanopyLaw) DistributeCanopy();   // variant B of shading: the photons go to the bodies' own stores (World.Sky)
    }

    // Only what is in the air moves on its own: it spreads through the air (hardly over walls). With
    // Volatility 0 that is the gas alone; with 1 every volatile species, at Diff × its share in the air.
    void Diffuse()
    {
        int m = 0;
        if (P.Volatility == 0) { diffSpec[m] = Chem.Gas; diffD[m++] = Chem.Diff[Chem.Gas]; }
        else foreach (int s in Chem.Volatiles) { diffSpec[m] = s; diffD[m++] = Chem.Diff[s] * Chem.Volatile[s]; }
        Diffuse(m);
    }

    readonly int[] diffSpec = new int[Chemistry.S];
    readonly float[] diffD = new float[Chemistry.S];
    readonly Qty[][] diffSrc = new Qty[Chemistry.S][], diffDst = new Qty[Chemistry.S][];
    Qty[][] diffBack;   // a spare buffer per species beyond the first (the first uses `back`)

    // All m species of diffSpec in one pass over the rows (each species exactly as alone: its own buffers,
    // the same arithmetic per cell), so the rows' neighbour and weight arrays are read once per chunk.
    void Diffuse(int m)
    {
        if (m == 0) return;
        if (m > 1) diffBack ??= new Qty[Chemistry.S][];
        for (int j = 0; j < m; j++)
        {
            int s = diffSpec[j];
            float d = diffD[j];
            if (P.GasDiffK != 1) d *= P.GasDiffK;   // World.Resources: the same integer flow per edge, both ways
            diffD[j] = d;
            diffSrc[j] = C[s];
            diffDst[j] = j == 0 ? back : diffBack[j] ??= new Qty[N];
        }
        Parallel.For(0, H / 8, chunk =>   // each cell reads the old buffer only: rows are independent
        {
            for (int j = 0; j < m; j++)
                for (int y = chunk * 8; y < chunk * 8 + 8; y++) DiffuseRow(diffSrc[j], diffDst[j], diffD[j], y);
        });
        for (int j = 0; j < m; j++)
        {
            var c = diffSrc[j];
            var next = diffDst[j];
            if (ResProbe != null)
            {
                double gross = 0;
                for (int i = 0; i < N; i++) gross += Math.Abs(next[i].Raw - c[i].Raw);
                ResProbe.DiffusionGross += gross / 2;
            }
            C[diffSpec[j]] = next;   // swap buffers instead of copying a planet every tick
            if (j == 0) back = c; else diffBack[j] = c;
            diffSrc[j] = diffDst[j] = null;
        }
    }

    // One row of one species. Per edge, in fixed point: the flow i→j is computed from the same difference and
    // weight as j→i with the opposite sign, and truncated toward zero (symmetric), so what one cell gives the
    // other receives exactly. An edge of weight 0 gives (long)(x · 0.0) = 0: no branch; the integer sum does
    // not depend on the order of its terms. Inside the map (not the polar rows, not the seam of x) the
    // neighbours are i ± 1 and i ± W: two cells at a time with the same operations per lane (arm64: long →
    // double, ×, truncation are IEEE / exact, as the scalar code), bit for bit the scalar result.
    void DiffuseRow(Qty[] cq, Qty[] nextq, float d, int y)
    {
        int W = this.W;
        var nb = this.nb;
        var diffW = this.diffW;
        var c = System.Runtime.InteropServices.MemoryMarshal.Cast<Qty, long>(cq.AsSpan());
        var next = System.Runtime.InteropServices.MemoryMarshal.Cast<Qty, long>(nextq.AsSpan());
        int i = y * W, end = i + W;
        if (System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported && y > 0 && y < H - 1 && W >= 4)
        {
            DiffuseCell(c, next, nb, diffW, d, i);
            int x = 1;
            ref long cr = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(c);
            ref long nr = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(next);
            ref float wr = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(diffW);
            var dv = System.Runtime.Intrinsics.Vector128.Create(d);
            for (; x + 1 < W - 1; x += 2)
            {
                int k = i + x;
                nuint at = (nuint)k;
                var ci = System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref cr, at);
                var east = System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref cr, at + 1);
                var west = System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref cr, at - 1);
                var south = System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref cr, at + (nuint)W);
                var north = System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref cr, at - (nuint)W);
                // weights of the two cells: [e s w n] of k and of k + 1, times d in float (as the scalar d · w)
                var wa = System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref wr, (nuint)(k * 4)) * dv;
                var wb = System.Runtime.Intrinsics.Vector128.LoadUnsafe(ref wr, (nuint)(k * 4 + 4)) * dv;
                var a01 = System.Runtime.Intrinsics.Vector128.WidenLower(wa); var a23 = System.Runtime.Intrinsics.Vector128.WidenUpper(wa);
                var b01 = System.Runtime.Intrinsics.Vector128.WidenLower(wb); var b23 = System.Runtime.Intrinsics.Vector128.WidenUpper(wb);
                var w0 = System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.ZipLow(a01, b01);
                var w1 = System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.ZipHigh(a01, b01);
                var w2 = System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.ZipLow(a23, b23);
                var w3 = System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.ZipHigh(a23, b23);
                var f0 = System.Runtime.Intrinsics.Vector128.ConvertToInt64(System.Runtime.Intrinsics.Vector128.ConvertToDouble(east - ci) * w0);
                var f1 = System.Runtime.Intrinsics.Vector128.ConvertToInt64(System.Runtime.Intrinsics.Vector128.ConvertToDouble(south - ci) * w1);
                var f2 = System.Runtime.Intrinsics.Vector128.ConvertToInt64(System.Runtime.Intrinsics.Vector128.ConvertToDouble(west - ci) * w2);
                var f3 = System.Runtime.Intrinsics.Vector128.ConvertToInt64(System.Runtime.Intrinsics.Vector128.ConvertToDouble(north - ci) * w3);
                System.Runtime.Intrinsics.Vector128.StoreUnsafe(ci + f0 + f1 + f2 + f3, ref nr, at);
            }
            for (; x < W; x++) DiffuseCell(c, next, nb, diffW, d, i + x);
            return;
        }
        for (; i < end; i++) DiffuseCell(c, next, nb, diffW, d, i);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    static void DiffuseCell(Span<long> c, Span<long> next, int[] nb, float[] diffW, float d, int i)
    {
        int b = i * 4;
        long ci = c[i];
        long f0 = (long)((c[nb[b]] - ci) * (double)(d * diffW[b]));
        long f1 = (long)((c[nb[b + 1]] - ci) * (double)(d * diffW[b + 1]));
        long f2 = (long)((c[nb[b + 2]] - ci) * (double)(d * diffW[b + 2]));
        long f3 = (long)((c[nb[b + 3]] - ci) * (double)(d * diffW[b + 3]));
        next[i] = ci + f0 + f1 + f2 + f3;
    }

    readonly bool[] weathering;

    // Rain carries `captured` of species s out of a column's air onto its top block.
    void RainOut(int i, int s, Qty captured)
    {
        if (captured <= Qty.Of(0.00001)) return;
        int v = i * Z + Height[i] - 1;
        C[s][i] -= captured; BurialAt(v).Matter[s] += captured; MassChanged(v);
        if (ResProbe != null) ResProbe.Rain[RegionOf(i)] += captured.Raw;
    }

    void EnvChem()
    {
        LapStart();
        UpdateClimate();
        Lap(0);
        // Each worker owns distinct cells; structural mutations are committed after the barrier.
        if (ArrheniusLaw) Parallel.For(0, H / 8, chunk => CellChemArrheniusRows(chunk * 8, 8, (int)Tick));   // World.Decay: species by species
        else Parallel.For(0, H / 8, chunk =>
        {
            int W = this.W, tick = (int)Tick;
            for (int y = chunk * 8; y < chunk * 8 + 8; y++)
                for (int i = y * W, end = i + W; i < end; i++) CellChem(i, tick, y);
        });
        Lap(1);
        bool leach = LeachLaw && Tick % P.MetamorphEvery == 0;   // World.Leach: litter soaks into the ground
        double leachK = Math.Min(1.0, (double)P.LeachK * P.MetamorphEvery);
        if (leach) LeachPass();
        for (int i = 0; i < N; i++)
        {
            if (LooseVolume[i] > P.CompactShare * P.VoxelSpace) Settle(i, LooseVolume[i]);
            if (leach) Leach(i, leachK);
            // Precipitation adsorbs existing atmospheric molecules onto the exposed aggregate.
            if (Tick % P.MetamorphEvery == 0 && Rain[i] > 0 && Height[i] > 2 && RainSum > 0)
            {
                float rain = Math.Min(P.RainCapture, Rain[i] * Moisture * P.RainShare / RainSum * (P.MetamorphEvery / P.EnvEvery));
                if (P.Volatility == 0) RainOut(i, Chem.Gas, C[Chem.Gas][i] * rain);
                else foreach (int s in Chem.Volatiles) RainOut(i, s, C[s][i] * (rain * Chem.Volatile[s]));   // what is in the air of each
            }
            if (weathering[i])
            {
                int molecule = TakeVoxelMolecule(i * Z + Height[i] - 1);
                if (molecule >= 0) { C[molecule][i] += 1; EpMol(EnergyEconomyProbe.WeatherMol, molecule); }
            }
        }
        foreach (var v in Vents)
            if (Rng.NextDouble() < 0.006 * v.Strength) BuildCone(v);
        Lap(2);
    }

    void CellChem(int i, int tick, int row)
    {
        if (ArrheniusLaw) { CellChemArrhenius(i, row, tick); return; }   // World.Decay: one law for every pool instead of LooseDecayK
        float f = TempFactor(Temp[i]), total = 0;
        int gas = Chem.Gas;
        bool vol0 = P.Volatility == 0;
        for (int s = 0; s < Chemistry.S; s++)
        {
            float amount = C[s][i].F;
            if (amount <= 0) continue;
            if (s != gas) total += vol0 ? amount * Chem.Volume[s] : amount * Chem.Volume[s] * Chem.Lying(s);
            if (Chem.SplitExo[s])
            {
                Qty m = amount * P.LooseDecayK * f;   // one molecule s → A + B, exactly the same amount each
                C[s][i] -= m; C[Chem.SplitA[s]][i] += m;
                if (Chem.SplitB[s] >= 0) C[Chem.SplitB[s]][i] += m;
                heatIn[i] += (float)(m * Chem.SplitEnergy(s));
                rowLooseDecay[row] += m * Chem.SplitEnergy(s);   // rows are owned by one worker
            }
        }
        // Atmospheric material remains in the budget even over water. Rain deposits existing
        // molecules; it is not a source of elements. Loose deposits compact their actual inventory.
        CellVolumeAndWeather(i, tick, total, f);
    }

    void CellVolumeAndWeather(int i, int tick, float total, float f)
    {
        LooseVolume[i] = total * P.LooseBulk; weathering[i] = false;
        int h = Height[i];
        if (h <= 2) return;
        Weather(i, tick, h, f);
    }

    // The same with the temperature factor computed only where there is a block to weather.
    void CellVolumeAndWeatherAt(int i, int tick, float total, float tempC)
    {
        LooseVolume[i] = total * P.LooseBulk; weathering[i] = false;
        int h = Height[i];
        if (h <= 2) return;
        Weather(i, tick, h, TempFactor(tempC));
    }

    void Weather(int i, int tick, int h, float f)
    {
        int v = i * Z + h - 1;
        float weather = P.WeatherK * f * (1 + Water[i] + Rain[i]) / (0.1f + VoxelCohesion(v));
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
            if (P.Volatility != 0) { double lying = Chem.Lying(s), sum = 0; for (int i = 0; i < N; i++) sum += c[i]; t += sum * lying; continue; }   // the share in the air is not litter
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
