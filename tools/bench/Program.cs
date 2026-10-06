using System;
using System.Diagnostics;
using System.Linq;
using Primordium;

// dotnet run -c Release --project tools/bench -- --seed 1 --ticks 50000 --every 5000 [--pop N] [--noabio] [--ops] [--audit]
//   [--log path.csv]: one row per --every interval with population, births, deaths, every stage's ms/tick,
//   allocation and GC counts, for looking at performance over time.
if (Array.IndexOf(args, "--self-test") >= 0) { World.RunRegression(); return; }
if (Array.IndexOf(args, "--self-test-infra") >= 0) { World.RunInfraRegression(); return; }
if (Array.IndexOf(args, "--list-params") >= 0)
{
    foreach (var p in ParamRegistry.All)
        Console.WriteLine($"{p.Group,-12} {p.Name,-20} {p.Value,10:G6}  [{p.Min:G6} … {p.Max:G6}, step {p.Step:G6}]{(p.Live ? "" : " (new world)")}  {p.Description}");
    return;
}
if (Array.IndexOf(args, "--bites") >= 0) { foreach (int s in new[] { 1, 2, 3, 5, 7 }) World.BiteReport(s); return; }
if (Array.IndexOf(args, "--strength") >= 0) { foreach (int s in new[] { 1, 2, 3, 7 }) { Console.WriteLine($"seed {s}"); World.StrengthReport(s); } return; }

int seed = 1, ticks = 20000, every = 1000, pop = P.InitialPop;
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
}

// Laws of the world (P): --preset path.json, then --param Name=value (repeatable) before the world is made;
// --param-at TICK:Name=value changes a law between ticks during the run (the trajectory is the seed plus
// this timeline).
var inv0 = System.Globalization.CultureInfo.InvariantCulture;
var paramAt = new System.Collections.Generic.List<(long tick, string name, double value)>();
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--preset") ParamRegistry.Restore(ParamRegistry.LoadPreset(args[i + 1]).Values);
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--param")
    {
        var kv = args[i + 1].Split('=');
        if (!ParamRegistry.Set(kv[0], double.Parse(kv[1], inv0))) throw new ArgumentException($"unknown parameter {kv[0]}");
    }
    if (args[i] == "--param-at")
    {
        var at = args[i + 1].Split(':', 2);
        var kv = at[1].Split('=');
        if (ParamRegistry.Find(kv[0]) == null) throw new ArgumentException($"unknown parameter {kv[0]}");
        paramAt.Add((long.Parse(at[0]), kv[0], double.Parse(kv[1], inv0)));
    }
}
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
    // The save brings its own laws; --param given on the command line still overrides them (recorded as a change).
    foreach (var (name, value) in changedLaws) w.SetParam(name, value);
}
else w = new World(seed, pop, abio);
bool audit = Array.IndexOf(args, "--audit") >= 0;
var originalAtoms = audit ? w.ElementBudget() : null;
if (Array.IndexOf(args, "--probe") >= 0)
{
    // label0 intake split intake split divide jmp0 nop — empty stacks make every operand 0
    var probe = new byte[] { 19, 47, 50, 47, 50, 51, 20, 0 };
    int gi = Array.IndexOf(args, "--genome");
    if (gi >= 0) probe = args[gi + 1].Split(',').Select(byte.Parse).ToArray();
    Console.WriteLine($"probe planted: {w.Probe(probe, 300)}");
    var ch = w.Chem;
    for (int s = 0; s < 8; s++)
        Console.WriteLine($"  species {s}: E{ch.E[s]} split→{ch.SplitA[s]}+{ch.SplitB[s]} dE={ch.E[s] - ch.E[ch.SplitA[s]] - ch.E[ch.SplitB[s]]} litter {w.C[s].Average():F2}");
}
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
        + ",alloc_mb,agent_alloc_mb,gen0,gen1,gen2,gc_pause_pct,heap_mb,dirty_columns,hanging_voxels");
}
long prevAlloc = GC.GetTotalAllocatedBytes(false);
int prevGen0 = GC.CollectionCount(0), prevGen1 = GC.CollectionCount(1), prevGen2 = GC.CollectionCount(2), prevBirths = 0, prevDeaths = 0;
double prevWall = 0;
for (int t = 1; t <= ticks; t++)
{
    foreach (var (at, name, value) in paramAt) if (at == w.Tick) { w.SetParam(name, value); Console.WriteLine($"   tick {w.Tick}: {name} = {ParamRegistry.Get(name).ToString(inv0)}"); }
    w.Step();
    if (t % every != 0) continue;
    if (audit)
    {
        var atoms = w.ElementBudget();
        Console.WriteLine("   atom drift excluding interior input: " + string.Join(" / ", atoms.Select((value, e) => (value - w.InteriorInput[e] - w.HandInput[e] - originalAtoms[e]).ToString("F6"))));
    }
    w.CheckCellLists();   // throws if any cell list is broken
    var c = w.TakeCensus();
    var cl = w.TakeClimate();
    var ev = string.Join(" ", Enum.GetValues<EvKind>().Where(k => k != EvKind.Count)
        .Select(k => $"{k}={w.Ev[(int)k] - prev[(int)k]}"));
    Array.Copy(w.Ev, prev, prev.Length);
    Console.WriteLine($"   ms/tick: env {w.Prof[0] / every:F2} agents {w.Prof[1] / every:F2} | climate T {cl.MeanT:F1} water {cl.WaterShare:P0} ice {cl.IceShare:P0} snow {cl.SnowShare:P0} strikes {w.StrikeCount}");
    Console.WriteLine($"   stages ms/tick: sky {w.Prof[4] / every:F3} diffusion {w.Prof[5] / every:F3} chemistry/climate {w.Prof[6] / every:F3} structure {w.Prof[7] / every:F3}; falls {w.CollapsedBlocks}, crushed {w.CrushedBlocks}, buried agents {w.DeathsBuried}, pressure reactions {w.Metamorphoses}");
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
            int depth = w.Height[a.Y * World.W + a.X] - a.Z;
            if (depth > 0) inCave++;
            if (depth >= 3) deep3++;
            deepest = Math.Max(deepest, depth);
        }
        int cavities = 0;
        for (int i = 0; i < World.N; i++) if (w.HasCavity[i]) cavities++;
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
                w.LastStructureColumns.ToString(), w.LastStructureVoxels.ToString() })));
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
    Console.WriteLine($"t={t} day={w.Day} pop={c.Pop} born={w.Births} died={w.DeathsStarve}/{w.DeathsKilled}/{w.DeathsBroken}/{w.DeathsClimate}/{w.DeathsBuried} spawn={w.Spawns} " +
                      $"gen={w.MaxGen} len={c.AvgLen:F0} E={c.AvgEnergy:F0} age={c.AvgAge:F0} old={c.OldestAge} " +
                      $"| plant={c.Plants} eat={c.Eaters} mine={c.Miners} hunt={c.Hunters} idle={c.Idle} " +
                      $"| litter={w.MeanLitter():F1} gas={w.C[w.Chem.Gas].Average():F2} h={w.MeanHeight():F2} | {ev} | {t / sw.Elapsed.TotalSeconds:F0} t/s | hash {w.StateHash():x16}");
    Console.WriteLine("   mined by grade (with protein): " + string.Join(" ", Enumerable.Range(0, 5).Select(k => $"{k}:{w.Mined[k] - prevMined[k]}({w.MinedCat[k] - prevCat[k]})")) +
                      " | firsts: " + string.Join(", ", w.Firsts.Where(f => f != null).Select(f => $"{w.Chem.MatName[f.Mat]} day {f.Tick / P.DayLen} #{f.Lineage}")));
    Console.WriteLine($"   divide tries {w.DivFail[0]}: no energy {w.DivFail[1]}, small body {w.DivFail[2]}, no room {w.DivFail[3]}, uneven {w.DivFail[4]}");
    {
        int wetCells = 0; for (int i = 0; i < World.N; i++) if (w.Submerged(i)) wetCells++;
        var wet = w.Agents.Where(a => !a.Dead && w.InWater(a)).ToList();
        int gassy = wet.Count(a => a.Inv[w.Chem.Gas] > 0), photo = wet.Count(a => World.Diet(a) == World.DietPlant);
        Console.WriteLine($"   water: {wetCells * 100.0 / World.N:F0}% of cells, {c.InWater} bodies in it ({c.Afloat} afloat, {c.AtSurface} at the surface, {photo} plants, {gassy} hold gas)" +
                          (wet.Count > 0 ? $", density {wet.Average(a => a.Density):F2}, depth under surface {wet.Average(a => w.Below(a, a.Y * World.W + a.X)):F1}" : ""));
    }
    Array.Copy(w.Mined, prevMined, 6);
    Array.Copy(w.MinedCat, prevCat, 6);
    prevWall = sw.Elapsed.TotalMilliseconds;   // the reports above are not the simulation's time
}
log?.Dispose();
if (savePath != null)
{
    var saveWatch = Stopwatch.StartNew();
    w.Save(savePath, $"bench seed {w.Seed}");
    Console.WriteLine($"saved {savePath} at tick {w.Tick}: {new System.IO.FileInfo(savePath).Length / 1048576.0:F1} MB, {w.Agents.Count} bodies, {saveWatch.ElapsedMilliseconds} ms, hash {w.StateHash():x16}");
}
{
    // What the tall columns are made of, and whether anybody lives on them.
    int towers = 0, looseTowers = 0, inhabited = 0, spikes = 0, depositTops = 0;
    for (int i = 0; i < World.N; i++)
    {
        int h = w.Height[i], maxNb = 0;
        for (int d = 0; d < 4; d++) maxNb = Math.Max(maxNb, w.Height[w.Nb(i, d)]);
        if (h - maxNb >= 2) spikes++;
    }
    for (int i = 0; i < World.N; i++) if (w.Height[i] > 2 && w.Order[i * World.Z + w.Height[i] - 1] <= 25) depositTops++;
    Console.WriteLine($"spikes (2+ above all neighbours): {spikes}; columns topped by a deposit layer: {depositTops}");
    var mats = new int[w.Chem.MatCount];
    for (int i = 0; i < World.N; i++)
    {
        int h = w.Height[i], maxNb = 0;
        for (int d = 0; d < 4; d++) maxNb = Math.Max(maxNb, w.Height[w.Nb(i, d)]);
        if (h - maxNb < 5) continue;
        towers++;
        int top = w.Mat[i * World.Z + h - 1];
        mats[top]++;
        if (w.Order[i * World.Z + h - 1] < 64) looseTowers++;
        if (w.Count[i] > 0) inhabited++;
    }
    var vox = new long[w.Chem.MatCount];
    for (int i = 0; i < World.N; i++) for (int z = 0; z < w.Height[i]; z++) if (w.Mat[i * World.Z + z] != Chemistry.Air) vox[w.Mat[i * World.Z + z]]++;
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
