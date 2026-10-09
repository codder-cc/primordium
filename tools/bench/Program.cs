using System;
using System.Diagnostics;
using System.Linq;
using Primordium;

// dotnet run -c Release --project tools/bench -- --seed 1 --ticks 50000 --every 5000 [--size WxHxL] [--pop N] [--noabio] [--ops] [--audit]
//   [--size WxHxL]: a world of that size (default 256x160x192; a small one for cheap screening, see Batch.cs).
//   [--log path.csv]: one row per --every interval with population, births, deaths, every stage's ms/tick,
//   allocation and GC counts, for looking at performance over time.
//   [--life N]: perturb only life (first bodies and agents' random streams) of the seed's world.
//   Laws: [--preset path.json] [--set Name=value ...] (alias --param) [--param-at TICK:Name=value ...] — see ParamHook.
//   [--load path] continue a saved world, [--save path] save it at the end.
//   Batches, comparisons, merging and the long test (--batch, --compare, --merge, --run-one, --long-test): see Batch.cs;
//   punctuated equilibrium after catastrophes (--punctuated): Punctuated.cs.
//   [--chronicle]: print the world's chronicle as events happen (World.Chronicle).
//   [--lang ru]: text from the game (law descriptions, chronicle) in Russian; English by default.
//   Observation and speed modes: --invade [design.json] (Geochem.cs), --export-designs dir, --tournament (Tournament.cs),
//   --food-chain (FoodChain.cs), --predation and --osc runs.csv (Predation.cs), --perf-baseline [--make] (PerfBaseline.cs),
//   --energy-audit (EnergyAudit.cs).
//   Population templates: --plant-population file.json [--at x,y] [--local] [--local-energy], --copy-population out.json (PopulationTool.cs).
//   Regions before the run: --copy-region x,y,w,h file, --paste-region file --at x,y [--rotate k] [--paste-mode above] [--no-bodies] [--dz n] (RegionBench.cs).
{
    int li = Array.IndexOf(args, "--lang");
    if (li >= 0 && li + 1 < args.Length) Loc.Set(args[li + 1]);
}
if (Array.IndexOf(args, "--self-test") >= 0) { World.RunRegression(); return; }
if (Array.IndexOf(args, "--self-test-one") is int oi and >= 0 && oi + 1 < args.Length) { World.RunOneRegression(args[oi + 1]); return; }   // one part of --self-test by name (e.g. RubbleRegression), timed
if (Array.IndexOf(args, "--self-test-mechanics") >= 0) { World.MechanicsRegression(); return; }   // structure, confinement, climbing, settling, relief, impacts (also in --self-test)
if (Array.IndexOf(args, "--relief-report") >= 0) { World.ReliefReport(args); return; }   // relief ×1 against the current ReliefScale (×4 if it is 1): steps, water, light
if (Array.IndexOf(args, "--self-test-infra") >= 0) { World.RunInfraRegression(); return; }
if (Array.IndexOf(args, "--self-test-size") >= 0) { World.RunSizeRegression(); return; }   // just the world size test (also in --self-test)
if (Array.IndexOf(args, "--self-test-scenario") >= 0) { World.RunScenarioRegression(); return; }   // just the small test worlds (also in --self-test)
if (Array.IndexOf(args, "--self-test-sun") >= 0) { World.SkyRegression(); return; }   // just the sky test (also in --self-test)
if (Array.IndexOf(args, "--self-test-cave") >= 0) { World.CaveClimateRegression(); return; }   // just the cave climate test (also in --self-test)
if (Array.IndexOf(args, "--self-test-evolution") >= 0) { World.RunEvolutionRegression(); return; }
if (Array.IndexOf(args, "--self-test-resources") >= 0) { World.ResourcesRegression(); return; }   // just the resources test (also in --self-test)
if (Array.IndexOf(args, "--self-test-predation") >= 0) { World.PredationRegression(); return; }   // just the bodies-against-bodies test (also in --self-test)
if (Array.IndexOf(args, "--self-test-wear") >= 0) { World.WearRegression(); return; }   // just the photodamage and wear test (also in --self-test)
if (Array.IndexOf(args, "--self-test-react") >= 0) { World.ReactRegression(); World.MatterLibraryRegression(); return; }   // just the reactive-damage and matter library tests (also in --self-test)
if (Array.IndexOf(args, "--self-test-leach") >= 0) { World.LeachRegression(); return; }   // just the leaching test (also in --self-test)
if (Array.IndexOf(args, "--self-test-geochem") >= 0) { World.GeochemRegression(); return; }   // just the geochemistry test (also in --self-test)
if (Array.IndexOf(args, "--self-test-population") >= 0) { World.PopulationRegression(); return; }   // just the population templates test (also in --self-test)
if (Array.IndexOf(args, "--self-test-water") >= 0) { World.WaterwaysRegression(); return; }   // just currents, floating ice and cave water (also in --self-test)
if (Array.IndexOf(args, "--self-test-climate") >= 0) { World.ClimateCyclesRegression(); return; }   // just the climate cycles test (also in --self-test)
if (Array.IndexOf(args, "--self-test-region") >= 0) { World.RegionRegression(); return; }   // just the regions test (also in --self-test)
if (Array.IndexOf(args, "--self-test-life") >= 0) { World.LifeModelRegression(); return; }   // just the life models test (also in --self-test)
if (Array.IndexOf(args, "--list-params") >= 0)
{
    foreach (var p in ParamRegistry.All)
        Console.WriteLine($"{p.GroupTitle,-12} {p.Name,-20} {p.Value,10:G6}  [{p.Min:G6} … {p.Max:G6}, step {p.Step:G6}]{(p.Live ? "" : " (new world)")}  {p.Description}");
    return;
}
// Laws of the world (P): presets, then single laws, before the world is made; --param-at during the run
// (the trajectory is the seed plus this timeline).
ParamHook.Laws laws;
try
{
    laws = ParamHook.Parse(args);
    foreach (var line in ParamHook.Apply(laws)) Console.WriteLine("set " + line);
}
catch (Exception e) when (e is ArgumentException || e is System.IO.IOException || e is System.Text.Json.JsonException)
{
    Console.Error.WriteLine("laws: " + e.Message);
    Environment.Exit(2);
    return;
}
if (Array.IndexOf(args, "--long-test") >= 0) { World.RunLongTest(args); return; }
if (Array.IndexOf(args, "--batch") >= 0) { Batch.Run(args, laws); return; }
if (Array.IndexOf(args, "--compare") >= 0) { Batch.Compare(args); return; }
if (Array.IndexOf(args, "--merge") >= 0) { Batch.Merge(args); return; }   // batches from several machines or shards into one (Batch.cs)
if (Array.IndexOf(args, "--punctuated") >= 0) { Punctuated.Run(args); return; }   // tempo of evolution after catastrophes vs outside (Punctuated.cs)
if (Array.IndexOf(args, "--run-one") >= 0) { Batch.RunOne(args, laws); return; }
if (Array.IndexOf(args, "--geochem") >= 0) { World.GeochemReport(args); return; }   // strata by depth, what bodies need (World.Geochem)
if (Array.IndexOf(args, "--resources") >= 0) { World.ResourceReport(args); return; }   // gas and loose food by region (World.Resources)
if (Array.IndexOf(args, "--invade") >= 0) { World.InvasionProbe(args); return; }   // plant a design (example name or design .json), follow its lineage (World.Geochem probe)
{
    // --export-designs dir: the built-in example designs as JSON files (the creature designer's format), to edit and --invade with.
    int ei = Array.IndexOf(args, "--export-designs");
    if (ei >= 0)
    {
        string dir = ei + 1 < args.Length ? args[ei + 1] : "designs";
        int n = CreatureLibrary.ExportExamples(dir, overwrite: true);
        Console.WriteLine(Loc.T($"{n} example designs written to {dir}", $"{n} примеров дизайнов записано в {dir}"));
        return;
    }
}
if (Array.IndexOf(args, "--tournament") >= 0) { World.Tournament(args); return; }   // ancestors against moderns in the same world (Tournament.cs)
if (Array.IndexOf(args, "--food-chain") >= 0) { World.FoodChainReport(args); return; }   // who eats whom, trophic levels (FoodChain.cs)
if (Array.IndexOf(args, "--predation") >= 0) { World.PredationReport(args); return; }
if (Array.IndexOf(args, "--chem-table") >= 0) { World.ChemTable(args); return; }   // the chemistry of seeds under the current laws (EnergyAudit.cs)
if (Array.IndexOf(args, "--energy-audit") >= 0) { World.EnergyAuditReport(args); return; }   // the energy economy, observed (EnergyAudit.cs, docs/ENERGY-AUDIT.md)   // predation audit, defence, parasites, territory (Predation.cs)
if (Array.IndexOf(args, "--osc") >= 0) { Oscillation.FromRuns(args); return; }   // Lotka–Volterra test on a batch's runs.csv (Predation.cs)
if (Array.IndexOf(args, "--perf-baseline") >= 0) { PerfBaseline.Run(args); return; }   // fixed boom save, ms/tick by stage (PerfBaseline.cs)
if (Array.IndexOf(args, "--sun") >= 0) { World.SunReport(args); return; }
if (Array.IndexOf(args, "--climate") >= 0) { World.ClimateReport(args); return; }   // the climate cycles' schedule by seed (World.ClimateCycles)   // the sky: day length, climate by latitude, photon supply (World.Sky)
if (Array.IndexOf(args, "--bites") >= 0) { foreach (int s in new[] { 1, 2, 3, 5, 7 }) World.BiteReport(s); return; }
if (Array.IndexOf(args, "--strength") >= 0) { foreach (int s in new[] { 1, 2, 3, 7 }) { Console.WriteLine($"seed {s}"); World.StrengthReport(s); } return; }

int seed = 1, ticks = 20000, every = 1000, pop = -1, life = 0;   // pop < 0: P.InitialPop, by area
string logPath = null;
bool abio = Array.IndexOf(args, "--noabio") < 0;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--seed") seed = int.Parse(args[i + 1]);
    if (args[i] == "--ticks") ticks = int.Parse(args[i + 1]);
    if (args[i] == "--every") every = int.Parse(args[i + 1]);
    if (args[i] == "--pop") pop = int.Parse(args[i + 1]);
    if (args[i] == "--log") logPath = args[i + 1];
    if (args[i] == "--tile") World.TileSize = int.Parse(args[i + 1]);
    if (args[i] == "--life") life = int.Parse(args[i + 1]);
}

var inv0 = System.Globalization.CultureInfo.InvariantCulture;
var changedLaws = ParamRegistry.Changes();
if (changedLaws.Count > 0) Console.WriteLine("laws: " + string.Join(", ", changedLaws.Select(kv => $"{kv.Key}={kv.Value.ToString(inv0)}")));

World.ProfileOps = Array.IndexOf(args, "--ops") >= 0;
// --load path: continue a saved world (its laws come with it) instead of making a new one;
// --save path: save the world at the end of the run.
string loadPath = null, savePath = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--load") loadPath = args[i + 1];
    if (args[i] == "--save") savePath = args[i + 1];
}
World w;
if (loadPath != null)
{
    var loadWatch = Stopwatch.StartNew();
    w = World.Load(loadPath);
    Console.WriteLine($"loaded {loadPath} in {loadWatch.ElapsedMilliseconds} ms: seed {w.Seed}, tick {w.Tick}, {w.Agents.Count} bodies, hash {w.StateHash():x16}");
    // The save brings its own laws; laws given on the command line still override them (recorded as changes).
    foreach (var path in laws.Presets) w.ApplyParams(ParamRegistry.LoadPreset(path).Values);
    foreach (var (name, value) in laws.Set) w.SetParam(name, value);
}
else w = new World(Batch.Settings(args, seed, pop, abio, life));
bool audit = Array.IndexOf(args, "--audit") >= 0;
var originalAtoms = audit ? w.ElementBudget() : null;
w.TrackHeat = audit;
var originalEnergy = audit ? w.AuditEnergy() : null;
var evo = new EvoMetrics();
if (Array.IndexOf(args, "--probe") >= 0)
{
    // label0 intake split intake split divide jmp0 nop — empty stacks make every operand 0
    var probe = new byte[] { 19, 47, 50, 47, 50, 51, 20, 0 };
    int gi = Array.IndexOf(args, "--genome");
    if (gi >= 0) probe = args[gi + 1].Split(',').Select(byte.Parse).ToArray();
    Console.WriteLine($"probe planted: {w.Probe(probe, 300)}");
    var ch = w.Chem;
    for (int s = 0; s < 8; s++)
        Console.WriteLine($"  species {s}: E{ch.E[s]} split→{ch.SplitA[s]}+{ch.SplitB[s]} dE={ch.E[s] - ch.E[ch.SplitA[s]] - ch.E[ch.SplitB[s]]} litter {w.C[s].Average(q => q.D):F2}");
}
PopulationTool.BeforeRun(w, args);   // --plant-population file.json [--at x,y] (PopulationTool.cs)
RegionBench.Apply(w, args);   // --copy-region x,y,w,h file, --paste-region file --at x,y (RegionBench.cs)
Console.WriteLine($"start: {w.Agents.Count} agents, spawns {w.Spawns}");
var sw = Stopwatch.StartNew();
var prev = new long[(int)EvKind.Count];
var prevMined = new long[6];
var prevCat = new long[6];
System.IO.StreamWriter log = null;
if (logPath != null)
{
    log = new System.IO.StreamWriter(logPath);
    log.WriteLine("tick,pop,births,deaths,ms_tick,env,agents,bookkeeping," + string.Join(",", World.DetailNames.Select(n => n.Replace(' ', '_').Replace('/', '_')))
        + ",agent_busy,agent_longest," + string.Join(",", Enumerable.Range(0, w.Colours).Select(q => $"busy{q},longest{q}"))
        + ",alloc_mb,agent_alloc_mb,gen0,gen1,gen2,gc_pause_pct,heap_mb,dirty_columns,hanging_voxels,mean_temp,"
        + string.Join(",", EvoMetrics.Names.Skip(1)) + ",energy_drift,energy_tolerance,chronicle_events,chronicle_important,fossils,"
        + string.Join(",", EvolutionHistory.Names.Skip(1)) + ",evo_mb");
}
long prevAlloc = GC.GetTotalAllocatedBytes(false);
int prevGen0 = GC.CollectionCount(0), prevGen1 = GC.CollectionCount(1), prevGen2 = GC.CollectionCount(2), prevBirths = 0, prevDeaths = 0;
double prevWall = 0;
bool printChronicle = Array.IndexOf(args, "--chronicle") >= 0;
long chronicleSeen = w.Chronicle.NextSeq - 1;
for (int t = 1; t <= ticks; t++)
{
    foreach (var line in ParamHook.ApplyDue(laws, w)) Console.WriteLine("   " + line);
    w.Step();
    if (printChronicle && w.Chronicle.NextSeq - 1 > chronicleSeen)
    {
        foreach (var e in w.Chronicle.Since(chronicleSeen))
            Console.WriteLine($"   chronicle {(e.Important ? "!" : " ")} {e.Tick,7} {Chronicle.TypeNames[(int)e.Type],-16} {Loc.Show(e.Text)}");
        chronicleSeen = w.Chronicle.NextSeq - 1;
    }
    if (t % every != 0) continue;
    if (audit)
    {
        var atoms = w.ElementBudget();
        Console.WriteLine("   atom drift excluding interior input: " + string.Join(" / ", atoms.Select((value, e) => (value - w.InteriorInput[e] - w.HandInput[e] - originalAtoms[e]).ToString("F6"))));
    }
    EnergyAudit energyNow = audit ? w.AuditEnergy() : null;
    if (audit) Console.WriteLine("   " + energyNow.Describe(originalEnergy));
    var evoNow = evo.Sample(w);
    Console.WriteLine("   evo: " + EvoMetrics.Format(evoNow));
    Console.WriteLine("   progress: " + Batch.FormatProgress(w));
    Array.Clear(w.EvoMs);
    w.CheckCellLists();   // throws if any cell list is broken
    var c = w.TakeCensus();
    var cl = w.TakeClimate();
    var ev = string.Join(" ", Enum.GetValues<EvKind>().Where(k => k != EvKind.Count)
        .Select(k => $"{k}={w.Ev[(int)k] - prev[(int)k]}"));
    Array.Copy(w.Ev, prev, prev.Length);
    Console.WriteLine($"   ms/tick: env {w.Prof[0] / every:F2} agents {w.Prof[1] / every:F2} | climate T {cl.MeanT:F1} water {cl.WaterShare:P0} ice {cl.IceShare:P0} snow {cl.SnowShare:P0} strikes {w.StrikeCount}");
    Console.WriteLine($"   stages ms/tick: sky {w.Prof[4] / every:F3} diffusion {w.Prof[5] / every:F3} chemistry/climate {w.Prof[6] / every:F3} structure {w.Prof[7] / every:F3}; falls {w.CollapsedBlocks}, crushed {w.CrushedBlocks}, buried agents {w.DeathsBuried}, pressure reactions {w.Metamorphoses}; ledges climbed {w.LedgeClimbs}, columns settled {w.SettleEvents}");
    Console.WriteLine("   detail ms/tick: " + string.Join(" ", World.DetailNames.Select((n, k) => $"{n} {w.Detail[k] / every:F3}")) + $" | dirty columns last {w.LastStructureColumns}, hanging voxels {w.LastStructureVoxels}; dirt by geometry/weakening/loads/matter {string.Join("/", w.DirtBy)}");
    long alloc = GC.GetTotalAllocatedBytes(false);
    int gen0 = GC.CollectionCount(0) - prevGen0, gen1 = GC.CollectionCount(1) - prevGen1, gen2 = GC.CollectionCount(2) - prevGen2;
    double allocMb = (alloc - prevAlloc) / 1048576.0 / every, agentAllocMb = w.AgentAllocated / 1048576.0 / every, pause = GC.GetGCMemoryInfo().PauseTimePercentage;
    double wall = sw.Elapsed.TotalMilliseconds, msTick = (wall - prevWall) / every;
    Console.WriteLine($"   gc: allocated {allocMb:F3} MB/tick (agent phase {agentAllocMb:F3}), collections gen0/1/2 {gen0}/{gen1}/{gen2}, pause {pause:F1}% | wall {msTick:F2} ms/tick");
    Array.Clear(w.DirtBy);
    Console.WriteLine($"   buried: entombed {w.BuriedBy[0]}, roof collapse {w.BuriedBy[1]}, slope creep {w.BuriedBy[2]}, dumped by dig {w.BuriedBy[3]}");
    Array.Clear(w.BuriedBy);
    {
        var live = w.Agents;
        Console.WriteLine($"   memory {GC.GetTotalMemory(false) / 1048576} MB, working set {System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1048576} MB | burials {w.Buried.Count} | genome max {(live.Count > 0 ? live.Max(a => a.G.Length) : 0)}, protein slots max {(live.Count > 0 ? live.Max(a => a.Enz.Length) : 0)}, protein amount max {(live.Count > 0 ? live.Max(a => a.EnzymeTotal) : 0):F0}, molecules max {(live.Count > 0 ? live.Max(a => a.InvTotal) : 0)}");
    }
    {
        // Who lives inside the rock, and how deep below the surface of their column.
        int inCave = 0, deepest = 0, deep3 = 0;
        foreach (var a in w.Agents)
        {
            if (a.Dead) continue;
            int depth = w.Height[a.Y * w.W + a.X] - a.Z;
            if (depth > 0) inCave++;
            if (depth >= 3) deep3++;
            deepest = Math.Max(deepest, depth);
        }
        int cavities = 0;
        for (int i = 0; i < w.N; i++) if (w.HasCavity[i]) cavities++;
        Console.WriteLine($"   underground: {inCave} bodies in cavities ({deep3} at 3+ below the surface, deepest {deepest}); columns with cavities {cavities}");
    }
    Console.WriteLine($"   agent tiles: work {w.AgentBusy / every:F2} ms/tick, slowest tile per colour {w.AgentLongest / every:F2}, wall {w.Prof[1] / every:F2}; by colour busy/longest "
        + string.Join(" ", Enumerable.Range(0, w.Colours).Select(q => $"{w.PhaseBusy[q] / every:F2}/{w.PhaseLongest[q] / every:F2}")));
    if (log != null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string f(double v) => v.ToString("F4", inv);
        log.WriteLine(string.Join(",", new[] { t.ToString(), w.Agents.Count.ToString(), (w.Births - prevBirths).ToString(), (w.Deaths - prevDeaths).ToString(),
            f(msTick), f(w.Prof[0] / every), f(w.Prof[1] / every), f(w.Prof[2] / every) }
            .Concat(w.Detail.Select(d => f(d / every)))
            .Concat(new[] { f(w.AgentBusy / every), f(w.AgentLongest / every) })
            .Concat(Enumerable.Range(0, w.Colours).SelectMany(q => new[] { f(w.PhaseBusy[q] / every), f(w.PhaseLongest[q] / every) }))
            .Concat(new[] { f(allocMb), f(agentAllocMb), gen0.ToString(), gen1.ToString(), gen2.ToString(), f(pause), (GC.GetTotalMemory(false) / 1048576).ToString(),
                w.LastStructureColumns.ToString(), w.LastStructureVoxels.ToString(), f(cl.MeanT) })
            .Concat(evoNow.Skip(1).Select(f))
            .Concat(new[] { audit ? f(EnergyAudit.Drift(originalEnergy, energyNow)) : "", audit ? f(EnergyAudit.Tolerance(originalEnergy, energyNow)) : "" })
            .Concat(new[] { (w.Chronicle.NextSeq - 1).ToString(), w.Chronicle.Important.Count.ToString(), w.Chronicle.Fossils.Count.ToString() })
            .Concat(Batch.ProgressRow(w).Select(f))
            .Concat(new[] { f(w.EvolutionBytes() / 1048576.0) })));
        log.Flush();
    }
    prevAlloc = alloc; prevGen0 += gen0; prevGen1 += gen1; prevGen2 += gen2; prevBirths = w.Births; prevDeaths = w.Deaths; prevWall = wall;
    w.AgentBusy = w.AgentLongest = 0; w.AgentAllocated = 0;
    Array.Clear(w.PhaseBusy); Array.Clear(w.PhaseLongest);
    if (World.ProfileOps)
    {
        var ops = w.OpTicks();
        double ms(long t) => t * 1000.0 / Stopwatch.Frequency / every;
        Console.WriteLine($"   agent time ms/tick: outside VM {ms(ops[Genome.OpSlots]):F2}; top instructions: " +
            string.Join(" ", Enumerable.Range(0, Genome.OpSlots).OrderByDescending(k => ops[k]).Take(12).Select(k => $"{Genome.SlotName(k)} {ms(ops[k]):F2}")));
    }
    Array.Clear(w.Prof);
    Array.Clear(w.Detail);
    Console.WriteLine($"   enzymes/body {c.AvgEnz:F1}: bind {c.EnzKind[0]:F2} split {c.EnzKind[1]:F2} photo {c.EnzKind[2]:F2} motor {c.EnzKind[3]:F2} | protected {c.AvgProt:P1} | Tb {c.AvgTb:F1} | crowded {c.Crowded} linked {c.Linked} | most in one cell {w.Count.Max()}, pushed off full floors {w.Pushed} | big bodies {c.Big}, largest {c.MaxCells} cells");
    Console.WriteLine($"t={t} day={w.Day} pop={c.Pop} born={w.Births} died={w.DeathsStarve}/{w.DeathsKilled}/{w.DeathsBroken}/{w.DeathsClimate}/{w.DeathsBuried}/{w.DeathsFlare} spawn={w.Spawns} " +
                      $"gen={w.MaxGen} len={c.AvgLen:F0} E={c.AvgEnergy:F0} age={c.AvgAge:F0} old={c.OldestAge} " +
                      $"| plant={c.Plants} eat={c.Eaters} mine={c.Miners} hunt={c.Hunters} idle={c.Idle} " +
                      $"| litter={w.MeanLitter():F1} gas={w.C[w.Chem.Gas].Average(q => q.D):F2} h={w.MeanHeight():F2} | {ev} | {t / sw.Elapsed.TotalSeconds:F0} t/s | hash {w.StateHash():x16}");
    Console.WriteLine("   mined by grade (with protein): " + string.Join(" ", Enumerable.Range(0, 5).Select(k => $"{k}:{w.Mined[k] - prevMined[k]}({w.MinedCat[k] - prevCat[k]})")) +
                      " | firsts: " + string.Join(", ", w.Firsts.Where(f => f != null).Select(f => $"{w.Chem.MatName[f.Mat]} day {f.Tick / P.DayLen} #{f.Lineage}")));
    {
        var cv = w.CaveCensus();
        var gc = w.GeoCensus();
        Console.WriteLine($"   geochem: deep element {w.Chem.ElementName[w.DeepElement]} ({(w.GeoOn ? "profile on" : "profile off")}) {gc[0]:P1} of body atoms | mined {gc[1]:F0}, depth mean {gc[2]:F2}, from 3+ levels {gc[3]:P1}, deep element {gc[4]:P1} of mined atoms; by depth 0/1-2/3-9/10-29/30+ {string.Join("/", w.GeoMined.Take(5))}");
        var rc = w.ResCensus();
        Console.WriteLine($"   resources: gas formula {rc[0]:P1} of body atoms | diets per region e^H {rc[1]:F2}, between regions {rc[2]:F3} nats, bodies per region CV {rc[3]:F2} | air gas {rc[4]:F3}/cell (CV across regions {rc[6]:F2}), loose food {rc[5]:F2}/cell (CV {rc[7]:F2})");
        Console.WriteLine($"   caves: under a roof {cv[0]:P1} (≥3 blocks {cv[1]:P1}), depth mean {cv[2]:F2} max {cv[3]:F0}, voids {cv[4]:F0} | polar {cv[5]:P0} of bodies, {cv[6]:P0} of them under a roof | mountains {cv[7]:P0}, {cv[8]:P0} under a roof");
    }
    {
        var sk = w.SkyCensus();
        Console.WriteLine($"   sky: diets per 32×32 square {sk[0]:F2} (planet/square {sk[1]:F2}) | transparency {sk[3]:F3} | activity {w.SolarActivity:P0}, flare power {w.FlarePower:F2}, mean dose {sk[2]:F4} | flares {sk[4]:F0} (mutations {w.FlareMutations}, deaths {sk[6]:F0}), eclipses {sk[5]:F0}{(w.EclipseNow ? $" (now at {w.EclipseX:F0},{w.EclipseY:F0})" : "")} | strikes {(w.AutoStrikes ? "on" : "off")}");
        Console.WriteLine($"   light competition ({(World.CanopyLaw ? "canopy B" : "shared pool A")}): lit bodies per cell {sk[7]:F2}, light past those above {sk[8]:P0}, light-eater cover {sk[9]:F3} | photo income top/under {sk[10]:F2}, shared/lone {sk[11]:F2} | light eaters among light eaters' mates ×{sk[12]:F2}");
    }
    {
        var cc = w.ClimCensus();
        Console.WriteLine($"   climate cycles: {w.EpochLine(true)} | glaciation N/S {cc[5]:F2}/{cc[6]:F2}, veil light {cc[7]:P0}, ice ages {cc[8]:F0}, volcanic winters {cc[9]:F0}, mega-eruptions {cc[10]:F0} | water liquid {cc[11]:P1} frozen {cc[12]:P1}, sea cells {cc[13]:P1}, snow/ice cover {cc[14]:P1}, total {w.WaterTotal():F1} (hand {w.WaterHand:F1})");
    }
    Console.WriteLine($"   divide tries {w.DivFail[0]}: no energy {w.DivFail[1]}, small body {w.DivFail[2]}, no room {w.DivFail[3]}, uneven {w.DivFail[4]}");
    {
        int wetCells = 0; for (int i = 0; i < w.N; i++) if (w.Submerged(i)) wetCells++;
        var wet = w.Agents.Where(a => !a.Dead && w.InWater(a)).ToList();
        int gassy = wet.Count(a => a.Inv[w.Chem.Gas] > 0), photo = wet.Count(a => World.Diet(a) == World.DietPlant);
        Console.WriteLine($"   water: {wetCells * 100.0 / w.N:F0}% of cells, {c.InWater} bodies in it ({c.Afloat} afloat, {c.AtSurface} at the surface, {photo} plants, {gassy} hold gas)" +
                          (wet.Count > 0 ? $", density {wet.Average(a => a.Density):F2}, depth under surface {wet.Average(a => w.Below(a, a.Y * w.W + a.X)):F1}" : ""));
        var ww = w.WaterCensus();
        Console.WriteLine($"   waterways: current mean {ww[0]:E1} max {ww[1]:F3} cells/tick, drifted {ww[2]:F0}, afloat {ww[3]:P1}; caves {ww[4]:F1} water in {ww[5]:F0} runs, {ww[6]:F0} bodies in it; sea under ice {ww[7]:P1}, {ww[8]:F0} bodies under ice; cave flow {w.CaveFlowMs:F1} ms in all");
    }
    Array.Copy(w.Mined, prevMined, 6);
    Array.Copy(w.MinedCat, prevCat, 6);
    prevWall = sw.Elapsed.TotalMilliseconds;   // the reports above are not the simulation's time
}
log?.Dispose();
PopulationTool.AfterRun(w, args);    // --copy-population out.json
if (savePath != null)
{
    var saveWatch = Stopwatch.StartNew();
    w.Save(savePath, $"bench seed {w.Seed}");
    Console.WriteLine($"saved {savePath} at tick {w.Tick}: {new System.IO.FileInfo(savePath).Length / 1048576.0:F1} MB, {w.Agents.Count} bodies, {saveWatch.ElapsedMilliseconds} ms, hash {w.StateHash():x16}");
}
{
    // What the tall columns are made of, and whether anybody lives on them.
    int towers = 0, looseTowers = 0, inhabited = 0, spikes = 0, depositTops = 0;
    for (int i = 0; i < w.N; i++)
    {
        int h = w.Height[i], maxNb = 0;
        for (int d = 0; d < 4; d++) maxNb = Math.Max(maxNb, w.Height[w.Nb(i, d)]);
        if (h - maxNb >= 2) spikes++;
    }
    for (int i = 0; i < w.N; i++) if (w.Height[i] > 2 && w.Order[i * w.Z + w.Height[i] - 1] <= 25) depositTops++;
    Console.WriteLine($"spikes (2+ above all neighbours): {spikes}; columns topped by a deposit layer: {depositTops}");
    var mats = new int[w.Chem.MatCount];
    for (int i = 0; i < w.N; i++)
    {
        int h = w.Height[i], maxNb = 0;
        for (int d = 0; d < 4; d++) maxNb = Math.Max(maxNb, w.Height[w.Nb(i, d)]);
        if (h - maxNb < 5) continue;
        towers++;
        int top = w.Mat[i * w.Z + h - 1];
        mats[top]++;
        if (w.Order[i * w.Z + h - 1] < 64) looseTowers++;
        if (w.Count[i] > 0) inhabited++;
    }
    var vox = new long[w.Chem.MatCount];
    for (int i = 0; i < w.N; i++) for (int z = 0; z < w.Height[i]; z++) if (w.Mat[i * w.Z + z] != Chemistry.Air) vox[w.Mat[i * w.Z + z]]++;
    Console.WriteLine("all blocks: " + string.Join(", ", Enumerable.Range(0, vox.Length).Where(m => vox[m] > 0).Select(m => $"{w.Chem.MatName[m]} {vox[m]}")));
    Console.WriteLine($"towers (5+ above all neighbours): {towers}, disordered on top: {looseTowers}, inhabited: {inhabited}; tops: " +
        string.Join(", ", Enumerable.Range(0, mats.Length).Where(m => mats[m] > 0).Select(m => $"{w.Chem.MatName[m]} {mats[m]}")));
}
var best = w.Agents.Where(a => !a.Dead).OrderByDescending(a => a.Gen).FirstOrDefault();
if (best != null)
{
    var parts = new System.Collections.Generic.List<string>();
    for (int i = 0; i < best.G.Length;)
    {
        parts.Add(Genome.DisAt(best.G, i, out int len));
        i += len;
    }
    Console.WriteLine($"deepest lineage #{best.Lineage} gen {best.Gen}: " + string.Join(" · ", parts));
}
{
    // No hard caps: see where holding costs actually stop bodies.
    var live = w.Agents.Where(a => !a.Dead).ToList();
    if (live.Count > 0)
        Console.WriteLine($"extremes: molecules max {live.Max(a => a.InvTotal)} (p99 {live.Select(a => a.InvTotal).OrderBy(v => v).ElementAt(live.Count * 99 / 100)}), " +
            $"energy max {live.Max(a => a.Energy):F0} (store p50 {live.Select(a => a.Store).OrderBy(v => v).ElementAt(live.Count / 2):F0}), " +
            $"protein kinds max {live.Max(a => a.EnzN)}, protein amount max {live.Max(a => a.EnzymeTotal):F1}, links max {live.Max(a => a.Links.Count)}, genome max {live.Max(a => a.G.Length)}, cells max {live.Max(a => a.Cells)}");
}
foreach (var a in w.Agents.Where(a => !a.Dead).OrderByDescending(a => a.Age).Take(5))
{
    var parts = new System.Collections.Generic.List<string>();
    for (int i = 0; i < a.G.Length;) { parts.Add(Genome.DisAt(a.G, i, out int len)); i += len; }
    Console.WriteLine($"old #{a.Id} age {a.Age} kids {a.NChildren} E {a.Energy:F0}/{a.Store:F0} mass {a.Mass:F0} enz {a.EnzN} | start {a.LifeStart:F0} chem +{a.GainChem:F0} got +{a.LifeGot:F0} photo +{a.GainPhoto:F0} mine {a.GainMine:F0} minecost {a.LifeMineCost:F0} tiers {string.Join("/", a.NMinedTier.Take(5))} " +
        $"| upkeep {a.LifeUpkeep:F0} kids {a.LifeKids:F0} harm {a.LifeHarm:F0} hold {a.LifeSpill:F0} uphill {a.LifeUphill:F0} | bind {a.NBind} split {a.NSplit} intake {a.NIntake} take {a.NTakes} moves {a.NMoves} | " + string.Join(" ", parts));
}
