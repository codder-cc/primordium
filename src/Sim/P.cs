namespace Primordium;

// Tunable constants of the world's "physics". Nothing here describes a behaviour —
// only sizes, costs, rates and thresholds.
public static class P
{
    // Planet
    public const int W = 256, H = 160, ZMax = 192;   // 192 levels: ground ~72 deep (World.Crust), ~100 of air above
    public const float BlockH = 1.0f;          // height of one soil block relative to a cell's width
    public const int DayLen = 1200;            // ticks per day
    public const int YearDays = 10;            // days per year
    public const float Tilt = 0.33f;           // axial tilt (rad): seasons, polar day and night
    public const int LightEvery = 8, EnvEvery = 4, ErodeEvery = 16;

    // Coarse mechanics, in simulation units (not SI).
    // A monolithic roof spans a tunnel and carries several blocks; mixed contacts are 4–15× weaker.
    // A full block holds a voxel's worth of molecules by volume (hundreds), hence the small gravity.
    public const float Gravity = 0.0008f, CompressionK = 300f, TensionK = 100f;
    // Gnawing rock is a process, not a lucky blow. Every try works the face of its cell loose, and
    // the work stays there: a molecule comes out once the face holds e^(barrier·(1 − catalysis) −
    // FaceBarrier) of loosening. A try's effort loosens effort/FaceWork; five gnawers loosen it five
    // times as fast, and even the hardest lattice gives way to enough work — at a price: a molecule
    // costs about FaceWork·e^(barrier − FaceBarrier) energy: a loose deposit or a disordered mix
    // (barrier ~0.3–0.6) about what an average molecule holds — rock is food for growth first, energy
    // only where a molecule is rich — while an ordered monolith of strong bonds (barrier 4–5) costs
    // hundreds, unless a protein for its bond takes the barrier away. (At FaceWork 3 soft rock paid
    // for itself three times over and worlds boomed to 65–90 thousand bodies.)
    // Time loosens an exposed face too (weathering): BiteRate a tick, kept up to BiteCap molecules' worth.
    public const float BiteRate = 0.005f, BiteCap = 3f, FaceBarrier = 1f, FaceWork = 9f;
    // How firmly rock holds its molecules (scales Matter barriers): an unaided bite of average rock
    // succeeds about one try in four, dense ordered rock almost never; a protein for its bond makes
    // it routine. Rock is eaten slowly.
    public const float RockBarrier = 4f;
    public const float CompactionPressure = 8f;
    public const int StructureEvery = 4, MetamorphEvery = 64;

    // Virtual machine
    public const int BaseCycles = 8, MaxCycles = 32;
    public const int StackSize = 16, MemSize = 16, CallDepth = 8;
    public const float CostInstr = 0.001f;     // energy per executed instruction

    // Upkeep
    public const float CostBase = 0.008f;      // per tick for being alive
    public const float CostMass = 0.0004f;     // per unit of body mass per tick (more when packed, see Room)
    public const float CostLen = 0.00008f;     // per genome byte per tick

    // Proteins
    // No limit on how many proteins or how much of each: every unit carries its substrate (mass to
    // keep), decays and has to be made again.
    public const float CostExpress = 0.25f;    // energy to make one unit (plus one molecule of material)
    public const float EnzDecay = 0.002f;      // fraction lost per tick: proteins must be remade
    public const float Spont = 0.5f;           // chance a reaction goes without its enzyme (at 15 °C)
    public const float EnzWidth = 14f;         // °C around its best temperature where an enzyme works

    // Light: each cell catches a trickle of photons that everybody in it shares.
    public const float PhotonK = 0.06f;        // photons per tick at full light
    public const float PhotonCap = 3f;         // a cell can't hoard more than this

    // Temperature: comfortable band; outside it harm grows exponentially.
    public const float ComfortLo = 2f, ComfortHi = 26f, TempTau = 7f;
    public const float FreezeK = 0.004f;       // energy per tick at 1·(e−1) below the band
    public const float HeatK = 0.004f;
    public const float Antifreeze = 8f;        // °C a body packed full of molecules can go below the band

    // Actions
    public const float CostIntake = 0.004f, CostExpel = 0.004f;
    public const float CostClimb = 0.01f;      // per unit of body mass per block climbed
    public const float CostSocial = 0.01f;     // take / give / share / link / mate
    public const float CostMine = 0.01f, CostDig = 1.5f, CostPile = 1f;
    // Tearing a molecule out of a block succeeds with chance e^-barrier (Chemistry.MatBarrier); a
    // protein aimed at the material's bond (with its local lattice barrier)
    // takes up to this share of the barrier away.
    public const float CatalysisMax = 0.92f;
    public const float CostInjectBase = 0.05f, CostInjectByte = 0.02f;
    public const float CostCutBase = 0.1f, CostCutByte = 0.08f;
    public const float CostGrow = 0.2f, CostPush = 0.015f, CostFall = 0.3f, CostLook = 0.0015f;
    public const int PileUnits = 4;            // solid molecules per built block

    // Body
    // No hard limits on what a body holds — holding costs. A body has room for about InvPerCell
    // molecules per cell it covers; packed beyond that, keeping each unit of mass costs (1 + packing²)
    // times more and taking more in costs as much more. Stored energy leaks as heat, HoldK·E·(E/store)²
    // per tick: little in a modest store, steeply more the more is hoarded beyond it.
    public const int MinBody = 3;              // fewer units and the body falls apart
    public const float StoreBase = 30f, StorePerMass = 1.5f;   // comfortable energy store: 30 + 1.5·mass
    public const float HoldK = 0.0005f;
    public const float CostLink = 0.002f;      // per link per tick: holding on to a partner
    public const float DecayK = 1.5e-4f;       // chance per unstable molecule per tick (at 15 °C)
    public const float UvK = 3e-6f;            // somatic mutation chance per genome byte per tick in full light
    public const float HeatToTemp = 0.12f;     // °C a cell warms per unit of energy its bodies dissipate (a quarter of that in water)
    // Bodies that outgrow one cell: covering k+1 cells needs mass GrowMass·k^GrowPow (80, ~211, ~373 … ~1720 for 10).
    public const float GrowMass = 80f, GrowPow = 1.4f;
    public const int MaxCells = 10;
    public const int InvPerCell = 64;          // comfortable room per covered cell (not a limit: see above)
    public const float CostCell = 0.005f;      // per extra cell per tick: holding a spread-out body together
    // Volume: the free space above a floor holds VoxelSpace of molecular volume (Chemistry.Volume) —
    // bodies and the loose remains lying there together. Loose remains are bulkier than the same
    // matter packed in a body (LooseBulk). No count of bodies is limited: a thousand tiny ones fit
    // where a few big ones would. Overfull floors push the smallest out sideways or down; loose
    // matter beyond CompactShare of the space runs downhill, or where it can't, is pressed into rock.
    public const float VoxelSpace = 1200f, LooseBulk = 2f, CompactShare = 0.3f;

    // Energy
    public const float EnergyK = 1.0f;         // bond energy unit -> agent energy
    public const float HeatShare = 0.3f;       // of released energy that warms the body

    // Reproduction
    public const float DivMinEnergy = 8f, DivCostBase = 2f, DivCostByte = 0.02f;
    public const int DivMinBody = 8;           // molecules a body needs before it can split
    public const float MateMinEnergy = 12f;

    // Links between agents
    public const float LinkFlow = 0.02f;       // energy equalisation across a link per tick

    // Motion
    public const float Recoil = 0.6f, Friction = 0.85f;

    // Environment
    public const int InitialPop = 3000;        // random genomes scattered at the start
    public const float AbioChance = 0.003f;    // chance per tick of one random newcomer (if abiogenesis is on)
    public const int SpawnBody = 8;
    public const float SpawnEnergy = 30f;      // burnt out of the cell's own molecules
    public const float InitLitter = 0.5f;       // primordial remains: this many times the top block's makeup (never renewed)
    public const int VentCount = 4;

    // Climate (°C): latitude/season climate + daily swing − altitude
    public const float TEquator = 29f, TPole = -16f, TDay = 12f, TLapse = 0.6f;
    public const float TRelax = 0.008f, TRelaxWater = 0.002f;   // per env step

    // Water
    public const float SeaShare = 0.22f;       // the lowest share of the land starts under water
    public const float SwimDepth = 0.6f;       // deeper than this a body is under water
    // Bodies in water (World.Water). A body rises or sinks by its density (mass over volume) against
    // the water's. Water is lighter than any packed molecule (0.75–1.5): a body without gas sinks and
    // walks the bottom; a gas molecule held in a body is a bubble taking GasExpand times its packed
    // room, so a body floats only on gas it holds (or by swimming strokes) — the code decides.
    // Buoyancy: how fast a 100% density difference accelerates a body (blocks/tick²); WaterDrag: the
    // vertical speed kept per tick. Every stroke through water costs CostSwim per unit of mass, (1 +
    // DepthK per block under the surface) times more — and so does a motor push there.
    public const float WaterDensity = 0.7f, GasExpand = 8f, Buoyancy = 0.1f, WaterDrag = 0.8f;
    public const float CostSwim = 0.002f, DepthK = 0.25f;
    public const float WaterFriction = 0.7f;   // speed kept per tick in water: it drags harder than ground (P.Friction)
    public const float WaterDim = 0.35f;       // light lost per block of water or ice above (e-fold)
    // Off the bottom a body reaches only what is dissolved: this share of the remains lying below,
    // e-fold less for every block it is above them. (The air's gas dissolves from the surface down:
    // e-fold less for every block under it.)
    public const float Solubility = 0.01f;
    public const float Evap = 0.0004f;         // per env step from open water at 20 °C
    public const float RainShare = 0.02f;      // of the air's moisture falls per env step

    // Space
    public const int StrikeMin = 2500, StrikeMax = 8000;   // ticks between mutagenic strikes
}
