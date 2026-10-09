namespace Primordium;

// The world's laws: sizes, costs, rates and thresholds. Nothing here describes a behaviour.
// The tunables are mutable static fields: the player experiments with them at runtime (ParamRegistry
// lists them with ranges and descriptions; World applies a change between ticks and invalidates what
// was derived from it). P is static, so all worlds in one process share these values.
// The `const` ones are world structure — array sizes and geometry: changing them needs a new build
// and a new world.
public static class P
{
    // World structure (const): requires a new world. The world's size is its own (WorldSettings.Width,
    // Height, Levels: 256×160×192 by default — ground ~72 deep (World.Crust), ~100 of air above).
    public const float BlockH = 1.0f;          // height of one soil block relative to a cell's width (the view's geometry)

    // Planet
    public static int DayLen = 1200;            // ticks per day
    public static int YearDays = 10;            // days per year
    public static float Tilt = 0.33f;           // axial tilt (rad): seasons, polar day and night
    public static int LightEvery = 8, EnvEvery = 4, ErodeEvery = 16;

    // Coarse mechanics, in simulation units (not SI).
    // A monolithic roof spans a tunnel and carries several blocks; mixed contacts are 4–15× weaker.
    // A full block holds a voxel's worth of molecules by volume (hundreds), hence the small gravity.
    public static float Gravity = 0.0008f, CompressionK = 300f, TensionK = 100f;
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
    public static float BiteRate = 0.005f, BiteCap = 3f, FaceBarrier = 1f, FaceWork = 9f;
    // How firmly rock holds its molecules (scales Matter barriers): an unaided bite of average rock
    // succeeds about one try in four, dense ordered rock almost never; a protein for its bond makes
    // it routine. Rock is eaten slowly.
    public static float RockBarrier = 4f;
    public static float CompactionPressure = 8f;
    // Strength under confinement (Mohr–Coulomb, World.Strength): a block bears its uniaxial strength plus
    // FrictionQ × the least horizontal stress its neighbours press on it with; a neighbour presses with
    // LateralK × its own vertical stress (as well as their contact passes it on). FrictionQ ≈ 4 is a
    // friction angle of ~37°; LateralK ≈ 0.4 is the at-rest earth pressure of a rock with Poisson ~0.3.
    public static float FrictionQ = 4f, LateralK = 0.4f;
    // Porosity (World.PackedVolume): a molecule in a block takes Volume × (1 + Bulking × looseness ×
    // (1 − order)). A crushed, disordered heap takes up to (1 + Bulking) times the room of the same
    // molecules in an ordered lattice (rock swells when broken, ~1.3–1.5 in nature). Pressure packs it:
    // a block anneals (order +1) only above CompactionPressure × e^(DensifyK × order), so every step of
    // packing takes exponentially more pressure — a dense lattice hardly packs further.
    public static float Bulking = 0.4f, DensifyK = 3f;
    // Uniaxial strength grows with order squared (cement between grains): LooseStrength at order 0, 1.5 at
    // full order (World.CompressionCapacity). A disordered heap stands only by friction under confinement.
    public static float LooseStrength = 0.001f;
    // Stiffness (World.Settle): a block's modulus is its uniaxial strength × ModulusRatio (E/UCS ≈ 100–500
    // for rock), so it strains σ / E under load and at most 1/ModulusRatio before it fails. Squeezed and
    // packed room in a column is taken up by the column above (it settles); unloaded, it springs back.
    public static float ModulusRatio = 300f;
    public static int StructureEvery = 4, MetamorphEvery = 64;
    // Time resolution of the support solver (World.Step): one more StructureEvery between passes per
    // StructureVoxels hanging voxels the last pass visited, at most StructureStretch times. Cost, not physics.
    public static int StructureVoxels = 5000, StructureStretch = 16;

    // Virtual machine
    public static int BaseCycles = 8, MaxCycles = 32;
    public const int StackSize = 16, MemSize = 16, CallDepth = 8;   // world structure: VM array sizes
    public static float CostInstr = 0.001f;     // energy per executed instruction

    // Upkeep
    public static float CostBase = 0.008f;      // per tick for being alive
    public static float CostMass = 0.0004f;     // per unit of body mass per tick (more when packed, see Room)
    public static float CostLen = 0.00008f;     // per genome byte per tick

    // Proteins
    // No limit on how many proteins or how much of each: every unit carries its substrate (mass to
    // keep), decays and has to be made again.
    public static float CostExpress = 0.25f;    // energy to make one unit (plus one molecule of material)
    public static float EnzDecay = 0.002f;      // fraction lost per tick: proteins must be remade
    public static float Spont = 0.5f;           // chance a reaction goes without its enzyme (at 15 °C)
    public static float EnzWidth = 14f;         // °C around its best temperature where an enzyme works

    // Light: each cell catches a trickle of photons that everybody in it shares.
    public static float PhotonK = 0.13f;        // photons per tick at full light (0.06 before the cosine law and the transparency: see World.Sky)
    public static float PhotonCap = 3f;         // a cell can't hoard more than this
    // Insolation (World.Sky): power = max(0, sin elevation)^InsolExp — the cosine law: the noon sun at the
    // equator gives 1, at 60° in the equinox 0.5, the polar winter 0. A soft terminator (twilight) between
    // TwilightLo and TwilightHi. The climate of a latitude follows the day's insolation sum, held back by
    // the year's mean (ClimInertia: oceans and ground keep warmth), shaped by ClimPow. 0 switches the law
    // off: the old curve that saturates at a sun ~17° high, and the climate from the noon height.
    public static int Insolation = 1;
    public static float InsolExp = 1f, TwilightLo = -0.04f, TwilightHi = 0.05f;
    public static float ClimInertia = 0.5f, ClimPow = 1.35f;
    // Atmospheric transparency (World.Sky): a slow drifting field over the latitudinal profile of the
    // wet and dry belts (the same profile the clouds follow), TranspMin … 1, clearer high up. Clear air
    // evaporates more and rains less (TranspHydro). 0 switches it off: the sky clear everywhere.
    public static int Transparency = 1;
    public static float TranspMin = 0.3f, TranspNoise = 0.8f, TranspScale = 0.25f, TranspDrift = 1f, TranspAlt = 0.01f, TranspHydro = 0.5f;
    // Shading (World.Sky): in a cell, bodies higher up (or, on one floor, bigger) catch the light first. A
    // body gets a photon with chance e^(−ShadeK·Σ cover of those above it), cover = (volume per cell /
    // VoxelSpace)^(2/3); a photon it misses stays for them. 0 switches it off.
    public static float ShadeK = 1.5f;
    // How the light of a cell is shared (World.Sky, ROADMAP 1.3). 0 — variant A: the cell keeps one pool of
    // photons, and the chance above decides who of those in it catches one. 1 — variant B, the canopy: every
    // LightEvery the photons falling on the cell go down through its bodies, highest first; each body stops
    // 1 − e^(−ShadeK·cover) of what reaches it (its own store of photons, up to PhotonCap per cell it covers),
    // the water between them swallows its share, the rest reaches the ground and is gone. With ShadeK 0 the
    // bodies stop nothing: the shared pool of A.
    public static int Canopy = 0;

    // Temperature: comfortable band; outside it harm grows exponentially.
    public static float ComfortLo = 2f, ComfortHi = 26f, TempTau = 7f;
    public static float FreezeK = 0.004f;       // energy per tick at 1·(e−1) below the band
    public static float HarmExpMax = 20f;       // harm exponent cap: e^20·0.004 ≈ 2·10⁶ a tick is death anyway, but finite
    public static float HeatK = 0.004f;
    public static float Antifreeze = 8f;        // °C a body packed full of molecules can go below the band

    // Actions
    public static float CostIntake = 0.004f, CostExpel = 0.004f;
    public static float CostClimb = 0.01f;      // per unit of body mass per block climbed
    public static float CostSocial = 0.01f;     // take / give / share / link / mate
    public static float CostMine = 0.01f, CostDig = 1.5f, CostPile = 1f;
    // Tearing a molecule out of a block succeeds with chance e^-barrier (Chemistry.MatBarrier); a
    // protein aimed at the material's bond (with its local lattice barrier)
    // takes up to this share of the barrier away.
    public static float CatalysisMax = 0.92f;
    // A living body holds its molecules like a disordered aggregate of the same molecules (World.Predation):
    // tearing one out (attack, take) is work against that hold, BodyHold times the work of gnawing it out
    // of a face of that aggregate (FaceWork·e^(barrier − FaceBarrier)).
    // Must be > 0 (a body with no hold at all would be torn apart by any touch).
    public static float BodyHold = 1f;
    // What an argument of a social instruction is worth: attack strikes with argument × StrikeUnit of
    // work, share hands over argument × ShareUnit of energy. Signals live AlarmTicks (an attack in a
    // cell, read by `hurt`) and HandshakeTicks (mate and link want both sides within it). Mating
    // takes MateShare of each parent's energy and of every kind of molecule.
    public static float StrikeUnit = 0.1f, ShareUnit = 0.125f, MateShare = 0.25f;
    public static int AlarmTicks = 16, HandshakeTicks = 8;
    // A body's free energy is held in its matter: molecules torn or pulled out of it carry this share of
    // their part of its store (store × share × molecules taken / molecules it had) to the body that took
    // them. 0: the store stays behind whatever is torn out.
    public static float TornStore = 1f;
    public static float CostInjectBase = 0.05f, CostInjectByte = 0.02f;
    public static float CostCutBase = 0.1f, CostCutByte = 0.08f;
    public static float CostGrow = 0.2f, CostPush = 0.015f, CostFall = 0.3f, CostLook = 0.0015f;
    public static int PileUnits = 4;            // solid molecules per built block

    // Body
    // No hard limits on what a body holds — holding costs. A body has room for about InvPerCell
    // molecules per cell it covers; packed beyond that, keeping each unit of mass costs (1 + packing²)
    // times more and taking more in costs as much more. Stored energy leaks as heat, HoldK·E·(E/store)²
    // per tick: little in a modest store, steeply more the more is hoarded beyond it.
    public static int MinBody = 3;              // fewer units and the body falls apart
    public static float StoreBase = 30f, StorePerMass = 1.5f;   // comfortable energy store: 30 + 1.5·mass
    public static float HoldK = 0.0005f;
    public static float CostLink = 0.002f;      // per link per tick: holding on to a partner
    public static float DecayK = 1.5e-4f;       // chance per unstable molecule per tick (at 15 °C)
    // Wear of body matter (World.Wear): a molecule that loses its hold on the body leaves it — along its
    // exothermic breakdown (products to the floor, the energy as heat) or whole. Photodamage: a caught
    // photon breaks the excited molecule with chance PhotoDamage·e^(−PhotoHold·hold/x), x its excitation
    // energy, hold its cohesion (Bond) + PhotoCage × the body's matrix (mean Bond × packing up to 1).
    // Wear: every held molecule, WearK·TempFactor(Tb)·e^(−WearHold·Bond) per tick. 0 switches a law off.
    public static float PhotoDamage = 0f, PhotoHold = 10f, PhotoCage = 1f;
    public static float WearK = 0f, WearHold = 3f;
    // Reactive damage (World.Life), one law for every species: a molecule a body holds reacts with its
    // protein substrate with chance ReactK·Reactivity·TempFactor(Tb) per tick (Reactivity = mean atom
    // affinity × excitation energy, Chemistry); one lying in the cell it stands on, ReactContact times
    // that. A reaction wears one protein by ReactWear (its substrate returns to the body, atoms kept)
    // and spends the molecule's excitation as heat (it drops to its ground state). ReactK 0 switches it off.
    public static float ReactK = 3e-4f, ReactContact = 0.2f, ReactWear = 0.5f;
    public static float UvK = 3e-6f;            // somatic mutation chance per genome byte per tick in full light
    public static float HeatToTemp = 0.12f;     // °C a cell warms per unit of energy its bodies dissipate (a quarter of that in water)
    // Bodies that outgrow one cell: covering k+1 cells needs mass GrowMass·k^GrowPow (80, ~211, ~373 … ~1720 for 10).
    public static float GrowMass = 80f, GrowPow = 1.4f;
    public const int MaxCells = 10;            // world structure: size of Agent.Foot
    public static int InvPerCell = 64;          // comfortable room per covered cell (not a limit: see above)
    public static float CostCell = 0.005f;      // per extra cell per tick: holding a spread-out body together
    // Volume: the free space above a floor holds VoxelSpace of molecular volume (Chemistry.Volume) —
    // bodies and the loose remains lying there together. Loose remains are bulkier than the same
    // matter packed in a body (LooseBulk). No count of bodies is limited: a thousand tiny ones fit
    // where a few big ones would. Overfull floors push the smallest out sideways or down; loose
    // matter beyond CompactShare of the space runs downhill, or where it can't, is pressed into rock.
    public static float VoxelSpace = 1200f, LooseBulk = 2f, CompactShare = 0.3f;

    // Energy
    public static float EnergyK = 1.0f;         // bond energy unit -> agent energy
    public static float HeatShare = 0.3f;       // of released energy that warms the body
    // Where a body's energy is (World.Charge): MatterEnergy 0 — a number (Agent.Energy) fed by reactions in
    // the body, as before; 1 — in its matter: the excitation of the molecules it holds (its charge) pays
    // every cost by relaxing them, an exothermic reaction in the body excites its ground molecules
    // (CaptureHeat of it warms the body instead), and the molecules carry it wherever they go.
    public static int MatterEnergy = 0;
    // Energy a body keeps in hand: an act that would leave it less than this is not done.
    public static float EnergyReserve = 1f;
    // MatterEnergy 1: of the energy an exothermic reaction in a body releases, the share that warms the
    // body instead of exciting its molecules (MatterEnergy 0: HeatShare).
    public static float CaptureHeat = 0.3f;

    // Reproduction
    public static float DivMinEnergy = 8f, DivCostBase = 2f, DivCostByte = 0.02f;
    public static int DivMinBody = 8;           // molecules a body needs before it can split
    public static float MateMinEnergy = 12f;

    // Links between agents
    public static float LinkFlow = 0.02f;       // energy equalisation across a link per tick

    // Motion
    public static float Recoil = 0.6f, Friction = 0.85f;

    // Environment
    public static int InitialPop = 3000;        // random genomes scattered at the start
    public static float AbioChance = 0.003f;    // chance per tick of one random newcomer (if abiogenesis is on)
    // AbioModel 1: abiogenesis from local chemistry — a cell's chance per tick is AbioCellRate ×
    // min(1, energy its loose matter releases by itself / SpawnEnergy) × TempFactor × wetness (World.Life,
    // AbioCellChance); 0 — the legacy global chance AbioChance, raised when bodies are few.
    public static int AbioModel = 0;
    public static float AbioCellRate = 2e-5f;
    public static int SpawnBody = 8;
    public static float SpawnEnergy = 30f;      // burnt out of the cell's own molecules
    public static float InitLitter = 0.5f;       // primordial remains: this many times the top block's makeup (never renewed)
    public static int VentCount = 4;
    // Relief of a new world (World.GenHeight): the rise from valleys to mountains (~28 levels at 1) is
    // stretched this many times. 4: mountains ~115 levels over the lowest ground, as tall in proportion to
    // the 192-level world as the first relief was to 48 levels. Bodies get over ledges by momentum (World.Move).
    // Default 1: at 4 the generated slopes are steeper than their rock holds (generation does not yet limit
    // slopes by strength), mountains slump heavily and the population falls ~3×; ×4 stays available as a law.
    public static float ReliefScale = 1f;

    // Geochemistry (World.Geochem): each element has a depth bias from the seed, DepthBias ∈ [−1, 1].
    // A stratum `d` levels under the top of its column favours a molecule by
    // exp(Σ_e DepthBias[e]·share_e·(d − DepthMid)/DepthScale) (share_e: the element's share of its
    // atoms) on top of the old choice by bond strength; at least one element of every world is deep
    // (bias ≥ DeepElementMin). Veins: where a correlated 3-D field exceeds VeinThreshold (higher near
    // the surface: rare outcrops), the deep element's molecules are favoured another e^VeinGain-fold.
    // Vents bring up what the deep interior is made of. GeoProfile 0: strata and vents as before.
    // All read when a world is made (a world keeps what it was made with, see World.Geochem).
    public static int GeoProfile = 1;
    public static float DepthScale = 4f, DepthMid = 12f, DeepElementMin = 0.7f, VeinThreshold = 0.72f, VeinGain = 4f;

    // Energies of the molecules (Chemistry): ChemEnergyModel 0 — drawn at random, as before (legacy);
    // 1 — from composition and bonds: formation energy −Σ_bonds ChemIonicK·(χi − χj)² (Pauling's ionic
    // resonance, χ = element affinity) plus the cost of atoms no bond can reach; the excitation a photon
    // brings is ChemExciteK × √(mean affinity of the atoms). Read when a world is made (a save keeps the
    // value it was made with; a save without it is legacy).
    public static int ChemEnergyModel = 1;
    public static float ChemIonicK = 6f, ChemExciteK = 6f;

    // Climate (°C): latitude/season climate + daily swing − altitude
    // TLapse: per level of the first relief (~28 levels from valleys to peaks). A world made with the relief
    // stretched (ReliefScale) cools TLapse / scale per level (World.Lapse), so its valleys-to-peaks span stays
    // the same ~18 °C at any scale.
    public static float TEquator = 29f, TPole = -16f, TDay = 12f, TLapse = 0.6f;
    public static float TRelax = 0.008f, TRelaxWater = 0.002f;   // per env step
    // Caves and depth (World.Cave): rock above a body shelters it. With `roof` solid blocks over it in
    // its column, a body feels lerp(surface, Tcave, 1 − e^(−roof/CaveDepthK)), where Tcave is the
    // column's slow mean temperature (an EMA of Temp over TmeanTau ticks, about a year) plus GeoGrad
    // per level below the column's top, plus the warmth bodies under that roof shed into it. The same
    // cover shields from the orbital beam; light (and with it UV) is already stopped by any roof.
    // CaveClimate 0 switches the law off: the surface temperature everywhere, as before.
    public static int CaveClimate = 1;
    public static float GeoGrad = 0.15f, CaveDepthK = 3f;
    public static int TmeanTau = 12000;
    // Non-stationary climate (World.ClimateCycles): slow deterministic cycles of the seed and the tick.
    // The axial tilt swings by ±TiltAmp around Tilt over TiltPeriod days; the orbit's eccentricity grows
    // from 0 to EccAmp and back over EccPeriod days while the perihelion turns through the year over
    // PrecPeriod days (the hemisphere whose summer meets the perihelion gets the hotter summer: the sun
    // there × 1 + 2e); the sun's output drifts by ±SunDriftAmp over SunDriftPeriod days (on top of the
    // activity cycle of the flares). Periods 0 — from the seed. A relative change of sunlight warms or
    // cools a latitude's climate by ClimSens °C per unit. Ice ages: when a hemisphere's high-latitude
    // (65°) summer insolation falls below IceAgeThreshold of its norm, its glaciation grows towards 1
    // over IceAgeTau days (and retreats the same way): up to IceAgeDT °C colder, twice as much at the
    // pole as at the equator; snow and ice then spread by the existing water cycle (the same water).
    // Volcanic winters: MegaEruptionRate a day, an existing vent throws out MegaEruptionBlocks blocks of
    // the interior (booked like any vent) and ash: MegaAsh of the planet's mean optical depth once spread,
    // mixed around the latitude within hours and pole to pole in about AshSpreadDays days, falling out
    // with a time constant of AshTau days. ClimateCycles 0 switches the cycles off (the player's
    // catastrophes still work through the same machinery); the code then runs the old expressions.
    public static int ClimateCycles = 1;
    public static float TiltAmp = 0.05f, TiltPeriod = 0f, EccAmp = 0.04f, EccPeriod = 0f, PrecPeriod = 0f;
    public static float SunDriftAmp = 0.03f, SunDriftPeriod = 0f, ClimSens = 25f;
    public static float IceAgeThreshold = 0.93f, IceAgeDT = 8f, IceAgeTau = 4f;
    public static float MegaEruptionRate = 0.01f, MegaAsh = 0.4f, AshTau = 10f, AshSpreadDays = 4f;
    public static int MegaEruptionBlocks = 60;

    // Water
    public static float SeaShare = 0.22f;       // the lowest share of the land starts under water
    public static float SwimDepth = 0.6f;       // deeper than this a body is under water
    // Bodies in water (World.Water). A body rises or sinks by its density (mass over volume) against
    // the water's. Water is lighter than any packed molecule (0.75–1.5): a body without gas sinks and
    // walks the bottom; a gas molecule held in a body is a bubble taking GasExpand times its packed
    // room, so a body floats only on gas it holds (or by swimming strokes) — the code decides.
    // Buoyancy: how fast a 100% density difference accelerates a body (blocks/tick²); WaterDrag: the
    // vertical speed kept per tick. Every stroke through water costs CostSwim per unit of mass, (1 +
    // DepthK per block under the surface) times more — and so does a motor push there.
    public static float WaterDensity = 0.7f, GasExpand = 8f, Buoyancy = 0.1f, WaterDrag = 0.8f;
    public static float CostSwim = 0.002f, DepthK = 0.25f;
    public static float WaterFriction = 0.7f;   // speed kept per tick in water: it drags harder than ground (P.Friction)
    public static float WaterDim = 0.35f;       // light lost per block of water or ice above (e-fold)
    // Off the bottom a body reaches only what is dissolved: this share of the remains lying below,
    // e-fold less for every block it is above them. (The air's gas dissolves from the surface down:
    // e-fold less for every block under it.)
    public static float Solubility = 0.01f;
    public static float Evap = 0.0004f;         // per env step from open water at 20 °C
    public static float RainShare = 0.02f;      // of the air's moisture falls per env step
    // Water as a fluid (World.Waterways). Currents: the water's own flow between columns (runoff, rain,
    // evaporation, sea level) is a velocity; a body off the bottom moves with the water around it (drag
    // brings it to the water's speed within a few ticks), one free step at a time, keeping its height.
    // Currents 0 — off: the water does not carry bodies, as before.
    public static int Currents = 1;
    // Ice floats: it is lighter than water and forms on top of it, so the liquid stays under the ice. The
    // air's cold reaches IceInsulation blocks into open water per env step (the old rate, 2% of that layer),
    // and every IceInsulation blocks of ice already on top slow further freezing as much again (heat
    // leaves through the ice). Bodies under the ice are cut off from the air by it as by as much water.
    // IceFloat 0 — off: water freezes through at 2% of its depth per step, as before.
    public static int IceFloat = 1;
    public static float IceInsulation = 0.5f;
    // Water enters caves: liquid water runs through open faces into the voids under the surface and
    // between them, by the same law as between columns (a share of the difference in water level),
    // falling to each void's floor; caves flood and drain. CaveWater 0 — off: caves stay dry, as before.
    public static int CaveWater = 1;

    // Resources (World.Resources): how far the air's gas travels and where bodies can reach it.
    // GasDiffK multiplies the gas's diffusion (Chemistry.Diff of the gas, from its mass): 1 — as
    // before, below 1 the air mixes slower and what bodies take in is felt locally first.
    // CaveGasK: blocks of roof for an e-fold less of the column's air reaching a body under a roof
    // (0 — off: under a roof only what lies on the cave floor, as before).
    public static float GasDiffK = 1f, CaveGasK = 0f;
    // LeachK (World.Leach): share per tick of the lightest loose molecule (any species, the air's gas too) on wet ground (standing water or
    // full rain) that percolating water carries below the top block, out of reach of bodies on the
    // surface until the top block goes; heavier molecules by their mobility Diff (0.13/√mass). 0 — off.
    public static float LeachK = 0f;
    // Ground chemistry (World.CellChem): an exothermic loose molecule on the ground breaks into its
    // parts with chance LooseDecayK · TempFactor per environment step; the top block of a column loses a
    // molecule to weathering with chance WeatherK · TempFactor · (1 + water + rain) / (0.1 + cohesion).
    public static float LooseDecayK = 0.0005f, WeatherK = 0.0002f;

    // Space
    public static int StrikeMin = 2500, StrikeMax = 8000;   // ticks between mutagenic strikes
    // Eclipses (World.Sky): a moon on an inclined orbit from the seed (MoonPeriod days between new moons,
    // MoonTilt rad; 0 — from the seed), MoonDist planet radii away. Its shadow — EclipseR cells, the light
    // there × EclipseDepth, a soft penumbra 0.3·EclipseR wide — crosses the day side at some new moons.
    // A pure function of the tick: predictable.
    public static int Eclipses = 1;
    public static float MoonPeriod = 0f, MoonTilt = 0f, MoonDist = 6f, EclipseR = 20f, EclipseDepth = 0.02f;
    // Solar flares (World.Sky): the sun's activity swings over SolarCycle days (0 — from the seed, 30…80)
    // and shifts its brightness by ±SolarLumAmp. Flares come FlareRate times a day at full activity,
    // last about FlareLen ticks, their power Pareto-distributed from FlarePowerMin (tail FlarePareto).
    // Dose = power × exposure (the sunlit sky over the body, × 1 − cave cover, e^(−FlareWaterDim·depth)
    // under water) × the body's shield e^(−ShieldK·Σ mass·packing of its molecules / room). A dose
    // mutates the genome (FlareMutK per byte, the UV path), wears proteins (FlareProtK), costs energy
    // (FlareHarmK) and heats the body (FlareHeatK: energy from outside, the ledger's `flare` input).
    // 0 switches them off — the old constructor of bench and self-test then keeps the orbital strikes.
    public static int Flares = 1;
    public static float SolarCycle = 0f, SolarLumAmp = 0.02f, FlareRate = 0.5f, FlareLen = 150f, FlarePowerMin = 1f, FlarePareto = 2f;
    public static float FlareWaterDim = 0.7f, ShieldK = 1f, FlareMutK = 3e-5f, FlareProtK = 0.002f, FlareHarmK = 0.02f, FlareHeatK = 0.01f;

    // Chronicle (observation only: none of these changes what happens in the world)
    public static int ChronicleCap = 10000;     // ordinary events kept (important ones are kept for ever)
    public static int FossilCap = 1000;         // fossils kept; the least important go first
    public static int ChronicleTrackKids = 1;   // 1: the first-generation children of planted designs keep a biography too
    public static float ChronicleCaveDays = 1;  // days under a roof that make a body the first cave dweller
    public static int ProgressEvery = 200;      // ticks between samples of the course of evolution (World.Evolution)
    public static int ProgressWindow = 10000;   // ticks over which the summary hint compares the trends of the tracks

    // The values the fields above start with (captured before anything can change them; declared
    // last, so every initializer above has run). ParamRegistry resets to these.
    internal static readonly System.Collections.Generic.Dictionary<string, double> Defaults = CaptureDefaults();

    static System.Collections.Generic.Dictionary<string, double> CaptureDefaults()
    {
        var d = new System.Collections.Generic.Dictionary<string, double>();
        foreach (var f in typeof(P).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (!f.IsLiteral && !f.IsInitOnly) d[f.Name] = System.Convert.ToDouble(f.GetValue(null));
        return d;
    }
}
