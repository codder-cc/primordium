using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Primordium;

// What the view needs of one living body, copied on the simulation thread between ticks.
public struct AgentSnap
{
    public Agent Ref;                       // the body itself: only its view-owned fields (VX, VY, VH, SeenAt…) are written by the view
    public long Id, Lineage;
    public int X, Y, Z, Cells, Shape, Foot, Crowd;   // Foot: where its cells start in SimFrame.Feet
    public float Lift, Mass, Sx, Sy, Sz, Hue, Sat, Val, EnergyK;
    public float EmaPhoto, EmaChem, EmaMine, EmaAttack;
    public float EnzPhoto, EnzChem, EnzMotor, EnzTotal;
    public int Act, ActDir;
    public long ActTick;
    public byte Mark;                       // 2: planted by the player (Agent.Designed), 1: of a planted design's lineage
}

// One published picture of the world for the view and the panel. Three of them rotate (SimRunner):
// the simulation fills one while the view reads another, so neither waits and nothing is allocated
// in steady state. Terrain fields are not copied: the view reads those arrays directly (racy reads of
// numbers are only cosmetic).
public sealed class SimFrame
{
    public long Tick;
    public AgentSnap[] Agents = new AgentSnap[1024];
    public int Count;
    public int[] Feet = new int[1024];
    public Agent[] LinkA = new Agent[256], LinkB = new Agent[256];
    public int LinkN;
    public readonly Flash[] Flashes = new Flash[World.FlashCap];
    public readonly List<Strike> Strikes = new();
    public readonly List<(int X, int Y)> Vents = new();
    // The selected body and the one under the cursor: what can only be asked of the world between ticks.
    public Agent Sel, Hover;
    public float SelFloorFill, HoverFloorFill, SelPressure, SelCapacity, SelOrder;
    public readonly float[] SelHist = new float[SimRunner.SelHistCap];
    public int SelHistN;
    public readonly int[] Hist = new int[SimRunner.HistCap * 5];
    public int HistN;
    public readonly long[] EvRate = new long[(int)EvKind.Count];
    public readonly float[] SelMass = new float[SimRunner.SelHistCap];   // mass of the selected body, alongside SelHist
    // The selected body's biography (oldest first), its parent and children alive, its tracked ancestors.
    public readonly BioEntry[] SelBio = new BioEntry[Chronicle.BioCap];
    public int SelBioN;
    public bool SelTracked;
    public Agent SelParent;
    public readonly List<Agent> SelKids = new();
    public int SelKidsDead;   // children it had that are no longer alive
}

// The chronicle as the game may read it on any thread: a copy made by the simulation thread whenever
// the chronicle changed (at most a few times a second). Events and fossils are never changed after
// they were added; ancestry nodes are copied.
public sealed class ChronicleView
{
    public static readonly ChronicleView Empty = new();
    public ChronicleEvent[] Events = Array.Empty<ChronicleEvent>();   // important and recent, in order
    public long[] Counts = new long[(int)EvType.Count];               // all ever added, by type
    public Fossil[] Fossils = Array.Empty<Fossil>();
    public Dictionary<long, Fossil> FossilByAgent = new();
    public Dictionary<long, AncestryNode> Ancestry = new();
    public long Version, Tick;
    public int WorldGeneration;
    public string[] Molecules = Array.Empty<string>();                 // the world's molecule names (for fossils)

    public Fossil FossilOf(long id) => FossilByAgent.TryGetValue(id, out var f) ? f : null;

    // A fossil for an event's body: the kept one, or one made from the genome the event remembered.
    public Fossil FossilFor(ChronicleEvent e)
    {
        if (e.AgentId != 0 && FossilByAgent.TryGetValue(e.AgentId, out var f)) return f;
        if (e.Genome == null) return null;
        return new Fossil { AgentId = e.AgentId, Lineage = e.Lineage, BornTick = e.Tick, DiedTick = -1, Genome = e.Genome, EventSeq = e.Seq, Energy = 40 };
    }
}

// The course of evolution (World.Progress) as the game may read it on any thread: a copy of the
// history made by the simulation thread when a new sample arrived. Sample arrays are never changed
// after they were made, so sharing them is safe.
public sealed class EvolutionView
{
    public static readonly EvolutionView Empty = new();
    public double[][] Samples = Array.Empty<double[]>();   // oldest first (EvolutionHistory.Names)
    public double[] Latest;                                 // the newest measurement (may not be kept in Samples)
    public long Version = -1, Tick;
    public int WorldGeneration, Seed, Stride = 1, Every = 200, Window = 10000;
    public double Index;
    public double[] Trends = Array.Empty<double>();
    public string Verdict = "";
    public double Mb;                                       // memory of the shadows, tree and history
}

// Runs World.Step on its own thread. The thread owns the world: everything that changes it (the hand,
// spawning, strikes, a new pace) arrives as a command and is applied between ticks, so the trajectory
// is the same as when the main thread stepped it. After ticks it publishes a SimFrame when the view
// has taken the previous one, and about once a second the census, records and lineages (SimStats).
public sealed class SimRunner
{
    public const int SelHistCap = 300, HistCap = 400;

    // The world this thread steps. It is replaced (NewWorld, Load) only by this thread, between ticks;
    // WorldGeneration counts replacements so the view can notice and call View.SetWorld.
    volatile World world;
    public World World => world;
    public volatile int WorldGeneration;
    readonly ConcurrentQueue<Action<World>> commands = new();
    readonly AutoResetEvent wake = new(false);
    Thread thread;
    volatile bool stop;

    // Pace: Tpf ticks per 1/60 s (the old "ticks per frame" at 60 fps); FastTo: run flat out to that tick.
    public volatile bool Paused;
    public volatile int Tpf = 8;
    long fastTo = -1;
    int steps;
    public long FastTo { get => Interlocked.Read(ref fastTo); set { Interlocked.Exchange(ref fastTo, value); wake.Set(); } }
    public long FastFrom;

    public volatile Agent Selected, Hover;
    public volatile bool WantStress;                 // the load overlay is on: keep Stress fresh
    public readonly float[] Stress = new float[World.N];
    int stressRow;

    public volatile SimStats Stats = new();
    public volatile string Error;
    public volatile ChronicleView Chronicle = ChronicleView.Empty;   // replaced, never changed (see ChronicleView)
    public volatile EvolutionView Evolution = EvolutionView.Empty;   // replaced, never changed (see EvolutionView)
    public readonly SimObserver Obs = new();                          // metrics, lineages and the clade tree over time (observation only)
    long evolutionSeen = -1;
    long chronicleSeen = -1;
    double chronicleAt;
    Agent trackedSel, kidsOf;
    double kidsAt;
    readonly List<Agent> kids = new();
    int kidsDead;
    Agent parentOf, parent;

    // Diagnostics (written by the simulation thread, read by the perf overlay).
    public double Tps, SimMs;                        // ticks per second, ms per tick (smoothed)
    public long TotalTicks;
    public double TotalTickMs, PublishMs, StatsMs;
    public int Publishes, StatsRuns;

    // Rotating frames: the simulation writes `wi`, the newest finished one is `ri`, the view holds `fi`.
    readonly SimFrame[] frames = { new(), new(), new() };
    int wi = 0, ri = 1, fi = 2;
    bool fresh;
    readonly object swap = new();
    volatile bool want = true;

    // Kept on the simulation thread, copied into frames.
    readonly float[] selHist = new float[SelHistCap], selMass = new float[SelHistCap];
    int selHistN;
    Agent histOf;
    readonly int[] hist = new int[HistCap * 5];
    int histN;
    readonly long[] evRate = new long[(int)EvKind.Count], evPrev = new long[(int)EvKind.Count];

    public SimRunner(World world)
    {
        this.world = world;
        Array.Copy(world.Ev, evPrev, evPrev.Length);
        PublishDesigned(world);
    }

    public long Tick => Interlocked.Read(ref World.Tick);
    public bool FastForward => FastTo > Tick;

    // Queue a change of the world; it is applied between ticks.
    public void Do(Action<World> command)
    {
        commands.Enqueue(command);
        wake.Set();
    }

    public void Step() { Interlocked.Increment(ref steps); wake.Set(); }

    // The newest published frame (the same one again if nothing new was published).
    public SimFrame Acquire()
    {
        lock (swap)
        {
            if (fresh) { (fi, ri) = (ri, fi); fresh = false; }
        }
        want = true;
        return frames[fi];
    }

    public void Start()
    {
        // Before the thread starts the caller may step and read the world directly (warm-up).
        RefreshStats();
        PublishChronicle(World, true);
        PublishEvolution(World, true);
        Publish();
        thread = new Thread(Run) { Name = "simulation", IsBackground = true };
        thread.Start();
    }

    public void Stop()
    {
        stop = true;
        wake.Set();
        thread?.Join();
        thread = null;
    }

    // One tick and the bookkeeping that follows it.
    public void TickOnce()
    {
        long t0 = Stopwatch.GetTimestamp();
        var w = World;
        var s = Selected;
        // A body the player looks at keeps a biography from now on (observation only, between ticks).
        if (s != trackedSel) { trackedSel = s; if (s is { Dead: false }) w.Track(s, Primordium.Chronicle.WhyPlayer); }
        w.Step();
        if (s != histOf) { histOf = s; selHistN = 0; }
        if (s is { Dead: false } && w.Tick % 5 == 0)
        {
            if (selHistN == SelHistCap) { Array.Copy(selHist, 1, selHist, 0, SelHistCap - 1); Array.Copy(selMass, 1, selMass, 0, SelHistCap - 1); selHistN--; }
            selMass[selHistN] = s.Mass;
            selHist[selHistN++] = (float)s.Energy;
        }
        if (w.Tick % 100 == 0)
        {
            // Only what the population graph needs: one cheap pass instead of a full census.
            int pop = 0, plants = 0, eaters = 0, miners = 0, hunters = 0;
            foreach (var a in w.Agents)
            {
                if (a.Dead) continue;
                pop++;
                switch (World.Diet(a))
                {
                    case World.DietPlant: plants++; break;
                    case World.DietEater: eaters++; break;
                    case World.DietMiner: miners++; break;
                    case World.DietHunter: hunters++; break;
                }
            }
            if (histN == HistCap) { Array.Copy(hist, 5, hist, 0, (HistCap - 1) * 5); histN--; }
            int o = histN++ * 5;
            hist[o] = pop; hist[o + 1] = plants; hist[o + 2] = eaters; hist[o + 3] = miners; hist[o + 4] = hunters;
        }
        if (w.Tick % 1000 == 0)
        {
            for (int k = 0; k < evRate.Length; k++) evRate[k] = w.Ev[k] - evPrev[k];
            Array.Copy(w.Ev, evPrev, evPrev.Length);
        }
        Obs.AfterTick(w);
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        TotalTickMs += ms;
        TotalTicks++;
        SimMs = SimMs == 0 ? ms : SimMs * 0.95 + ms * 0.05;
    }

    public void RefreshStats()
    {
        long t0 = Stopwatch.GetTimestamp();
        Stats = SimStats.Compute(World, Selected);
        StatsMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        StatsRuns++;
    }

    void Run()
    {
        var clock = Stopwatch.StartNew();
        double last = 0, budget = 0, lastStats = 0, lastPublish = 0, tpsFrom = 0;
        long tpsTicks = 0;
        bool changed = false, statsDue = false;
        Agent statsSel = Selected;
        while (!stop)
        {
            try
            {
                bool acted = false;
                while (commands.TryDequeue(out var c)) { c(World); acted = true; }
                Obs.Between(World, clock.Elapsed.TotalSeconds);
                if (!FastForward) MaybeAutosave(clock.Elapsed.TotalSeconds);
                bool ticked = false;
                double now = clock.Elapsed.TotalSeconds;
                long ft = FastTo;
                if (ft >= 0 && World.Tick < ft)
                {
                    // Fast-forward: nothing is drawn, tick flat out (commands are still taken every ~0.1 s).
                    double until = now + 0.1;
                    while (World.Tick < ft && clock.Elapsed.TotalSeconds < until && !stop && FastTo == ft) { TickOnce(); tpsTicks++; }
                    if (World.Tick >= ft) { FastTo = -1; statsDue = true; lastStats = -10; }
                    ticked = true;
                    budget = 0;
                }
                else if (Interlocked.CompareExchange(ref steps, 0, 0) > 0)
                {
                    Interlocked.Decrement(ref steps);
                    TickOnce(); tpsTicks++;
                    ticked = true;
                }
                else if (!Paused)
                {
                    double rate = Tpf * 60.0;
                    budget = Math.Min(budget + (now - last) * rate, Math.Max(2, rate * 0.1));
                    if (budget >= 1) { TickOnce(); tpsTicks++; budget -= 1; ticked = true; }
                }
                else budget = 0;
                last = now;
                changed |= ticked || acted;
                statsDue |= ticked || acted;

                now = clock.Elapsed.TotalSeconds;
                if (now - tpsFrom >= 0.5)
                {
                    double tps = tpsTicks / (now - tpsFrom);
                    Tps = Paused && !FastForward ? 0 : Tps * 0.4 + tps * 0.6;
                    tpsFrom = now; tpsTicks = 0;
                }
                if (FastForward) continue;

                var sel = Selected;
                if ((statsDue && now - lastStats >= 1.0) || (sel != statsSel && now - lastStats >= 0.2))
                {
                    RefreshStats();
                    lastStats = now; statsDue = false; statsSel = sel;
                }
                if (want && (changed || now - lastPublish > 0.25))
                {
                    Publish();
                    lastPublish = now; changed = false;
                }
                if (!ticked && commands.IsEmpty)
                {
                    // Wait for the next tick due (or a command, a step, a new pace).
                    double rate = Tpf * 60.0;
                    int ms = Paused ? 20 : (int)Math.Clamp((1 - budget) / rate * 1000, 0, 20);
                    if (ms > 0) wake.WaitOne(ms); else Thread.Yield();
                }
            }
            catch (Exception e)
            {
                Error = e.ToString();
                Paused = true;
                FastTo = -1;
            }
        }
    }

    void Publish()
    {
        long t0 = Stopwatch.GetTimestamp();
        Fill(frames[wi]);
        lock (swap)
        {
            (wi, ri) = (ri, wi);
            fresh = true;
        }
        want = false;
        PublishMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        Publishes++;
    }

    // Per chunk of the agent list: the links it found (merged in order afterwards).
    readonly List<(Agent, Agent)>[] chunkLinks = new List<(Agent, Agent)>[16];

    void Fill(SimFrame f)
    {
        var w = World;
        var list = w.Agents;
        int n = list.Count;
        if (f.Agents.Length < n) f.Agents = new AgentSnap[Math.Max(n + n / 4, f.Agents.Length * 2)];
        if (f.Feet.Length < f.Agents.Length * P.MaxCells) f.Feet = new int[f.Agents.Length * P.MaxCells];
        var snaps = f.Agents;
        var feet = f.Feet;
        var designed = w.DesignedLineages;   // only read here (the world is not stepping)
        // Snapshot i is body i of the list (a body killed by the hand since the tick gets Ref = null).
        // Chunks in parallel: the world is not stepping while this runs.
        int chunks = Math.Clamp(n / 4096, 1, chunkLinks.Length);
        System.Threading.Tasks.Parallel.For(0, chunks, ch =>
        {
            var lk = chunkLinks[ch] ??= new List<(Agent, Agent)>();
            lk.Clear();
            int from = (int)((long)n * ch / chunks), to = (int)((long)n * (ch + 1) / chunks);
            for (int i = from; i < to; i++)
            {
                var a = list[i];
                ref var s = ref snaps[i];
                if (a.Dead) { s.Ref = null; continue; }
                s.Ref = a;
                s.Id = a.Id; s.X = a.X; s.Y = a.Y; s.Z = a.Z; s.Cells = a.Cells; s.Shape = a.Shape; s.Lineage = a.Lineage;
                s.Lift = a.Lift; s.Mass = a.Mass; s.Sx = a.Sx; s.Sy = a.Sy; s.Sz = a.Sz; s.Hue = a.Hue; s.Sat = a.Sat; s.Val = a.Val;
                s.EnergyK = (float)Math.Clamp(a.Energy / a.Store, 0, 1);
                s.EmaPhoto = a.EmaPhoto; s.EmaChem = a.EmaChem; s.EmaMine = a.EmaMine; s.EmaAttack = a.EmaAttack;
                float ph = 0, chem = 0, mo = 0, tot = 0;
                var enz = a.Enz;
                for (int e = 0; e < a.EnzN; e++)
                {
                    float am = enz[e].Amount;
                    tot += am;
                    switch (enz[e].Kind)
                    {
                        case Enzyme.Photo: ph += am; break;
                        case Enzyme.Split: case Enzyme.Bind: chem += am; break;
                        case Enzyme.Motor: mo += am; break;
                    }
                }
                s.EnzPhoto = ph; s.EnzChem = chem; s.EnzMotor = mo; s.EnzTotal = tot;
                s.Act = a.Act; s.ActDir = a.ActDir; s.ActTick = a.ActTick;
                s.Mark = a.Designed ? (byte)2 : designed.Count > 0 && designed.ContainsKey(a.Lineage) ? (byte)1 : (byte)0;
                int cell = a.Y * World.W + a.X;
                s.Crowd = w.Count[cell];
                s.Foot = i * P.MaxCells;
                if (a.Cells > 1)
                    for (int c = 0; c < a.Cells; c++) feet[s.Foot + c] = c == 0 ? cell : a.Foot[c];
                if (a.Links.Count > 0)
                    foreach (var b in a.Links)
                        if (!b.Dead && a.Id < b.Id) lk.Add((a, b));
            }
        });
        int links = 0;
        for (int ch = 0; ch < chunks; ch++) links += chunkLinks[ch].Count;
        if (f.LinkA.Length < links) { f.LinkA = new Agent[links + links / 4]; f.LinkB = new Agent[f.LinkA.Length]; }
        int k = 0;
        for (int ch = 0; ch < chunks; ch++)
            foreach (var (a, b) in chunkLinks[ch]) { f.LinkA[k] = a; f.LinkB[k] = b; k++; }
        // Drop references to bodies that are gone (the arrays are reused).
        for (int i = n; i < f.Count; i++) snaps[i].Ref = null;
        for (int i = links; i < f.LinkN; i++) f.LinkA[i] = f.LinkB[i] = null;
        f.Count = n; f.LinkN = links;
        f.Tick = w.Tick;
        Array.Copy(w.Flashes, f.Flashes, World.FlashCap);
        f.Strikes.Clear();
        f.Strikes.AddRange(w.Strikes);
        f.Vents.Clear();
        foreach (var v in w.Vents) f.Vents.Add((v.X, v.Y));

        var sel = Selected;
        f.Sel = sel;
        if (sel != null)
        {
            int cell = sel.Y * World.W + sel.X;
            f.SelFloorFill = w.FloorFill(cell, sel.Z);
            int sv = cell * World.Z + Math.Clamp(sel.Z - 1, 0, World.Z - 1);
            f.SelPressure = w.Pressure[sv];
            f.SelCapacity = w.CompressionCapacity(sv);
            f.SelOrder = w.Order[sv] / 255f;
        }
        var hov = Hover;
        f.Hover = hov;
        if (hov != null) f.HoverFloorFill = w.FloorFill(hov.Y * World.W + hov.X, hov.Z);
        if (sel == histOf) { Array.Copy(selHist, f.SelHist, selHistN); Array.Copy(selMass, f.SelMass, selHistN); f.SelHistN = selHistN; }
        else f.SelHistN = 0;
        FillBiography(f, w, sel);
        PublishChronicle(w, false);
        PublishEvolution(w, false);
        Array.Copy(hist, f.Hist, histN * 5);
        f.HistN = histN;
        Array.Copy(evRate, f.EvRate, evRate.Length);

        // The load overlay: an eighth of the rows per frame (CompressionCapacity fills a cache of the
        // world, so it is only ever called here, between ticks).
        if (WantStress)
        {
            for (int r = 0; r < World.H / 8; r++, stressRow = (stressRow + 1) % World.H)
                for (int x = 0; x < World.W; x++)
                {
                    int i = stressRow * World.W + x, h = w.Height[i];
                    float stress = 0;
                    for (int z = 2; z < h; z++)
                    {
                        int v = i * World.Z + z;
                        if (w.Mat[v] >= 2) stress = Math.Max(stress, w.Pressure[v] / Math.Max(0.001f, w.CompressionCapacity(v)));
                    }
                    Stress[i] = stress;
                }
        }
    }

    // The selected body's biography, parent and children (the children are looked up a few times a second).
    void FillBiography(SimFrame f, World w, Agent sel)
    {
        f.SelKids.Clear();
        f.SelBioN = 0; f.SelTracked = false; f.SelParent = null; f.SelKidsDead = 0;
        if (sel == null) return;
        f.SelTracked = sel.Tracked;
        var bio = Primordium.Chronicle.BioOf(sel);
        Array.Copy(bio, f.SelBio, bio.Length);
        f.SelBioN = bio.Length;
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (sel != kidsOf || now - kidsAt > 0.5)
        {
            kidsOf = sel; kidsAt = now;
            kids.Clear();
            if (sel != parentOf) { parentOf = sel; parent = null; }
            foreach (var a in w.Agents)
            {
                if (a.Dead) continue;
                if (a.ParentId == sel.Id) kids.Add(a);
                else if (a.Id == sel.ParentId) parent = a;
            }
            if (parent is { Dead: true }) parent = null;
            kidsDead = Math.Max(0, sel.NChildren - kids.Count);
        }
        f.SelKids.AddRange(kids);
        f.SelParent = parent;
        f.SelKidsDead = kidsDead;
    }

    // A fresh ChronicleView when the chronicle changed (at most four times a second unless forced).
    void PublishChronicle(World w, bool force)
    {
        var c = w.Chronicle;
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (!force && (c.Version == chronicleSeen || now - chronicleAt < 0.25)) return;
        chronicleSeen = c.Version; chronicleAt = now;
        var v = new ChronicleView
        {
            Events = c.All().ToArray(), Counts = (long[])c.Counts.Clone(), Fossils = c.Fossils.ToArray(),
            FossilByAgent = new Dictionary<long, Fossil>(c.FossilByAgent), Version = c.Version, Tick = w.Tick, WorldGeneration = WorldGeneration,
            Molecules = (string[])w.Chem.Name.Clone(),
        };
        foreach (var (id, n) in c.Ancestry)
            v.Ancestry[id] = new AncestryNode { Id = n.Id, ParentId = n.ParentId, TrackedParent = n.TrackedParent, Lineage = n.Lineage, Gen = n.Gen, Born = n.Born, Died = n.Died, Cause = n.Cause, Why = n.Why };
        Chronicle = v;
    }

    // A fresh EvolutionView when a new sample of the course of evolution was taken.
    void PublishEvolution(World w, bool force)
    {
        var p = w.Progress;
        if (!force && p.Version == evolutionSeen) return;
        evolutionSeen = p.Version;
        var samples = p.Samples.ToArray();
        var (index, trends) = EvolutionHistory.Index(samples, P.ProgressWindow, p.Latest);
        Evolution = new EvolutionView
        {
            Samples = samples, Latest = p.Latest, Version = p.Version, Tick = w.Tick, WorldGeneration = WorldGeneration, Seed = w.Seed,
            Stride = p.Stride, Every = P.ProgressEvery, Window = P.ProgressWindow, Index = index, Trends = trends,
            Verdict = EvolutionHistory.Verdict(index), Mb = w.EvolutionBytes() / 1048576.0,
        };
    }

    // Start a biography for a body (the player asked to watch it).
    public void Track(Agent a) => Do(w => { if (a != null) { w.Track(a, Primordium.Chronicle.WhyPlayer); PublishChronicle(w, true); } });

    // ---- commands for the UI (all applied by the simulation thread between ticks) ----
    // Results come back through the optional callback (called on the simulation thread: marshal to the
    // main thread before touching Godot) and as a line in Notices.

    // Human-readable outcomes, for toasts. A failure starts with BadNotice (the UI strips it and shows it in red).
    public readonly ConcurrentQueue<string> Notices = new();
    public const string BadNotice = "!";
    void Notice(string text) { Notices.Enqueue(text); while (Notices.Count > 50) Notices.TryDequeue(out _); }
    void Fail(string text) => Notice(BadNotice + text);

    // Lineages of player designs (World.DesignedLineages) as an immutable copy the panel may read on any thread.
    public volatile IReadOnlyDictionary<long, string> DesignedLineages = new Dictionary<long, string>();
    void PublishDesigned(World w) => DesignedLineages = new Dictionary<long, string>(w.DesignedLineages);

    // Laws (P). Read access from any thread: ParamRegistry.All (fixed metadata) and ParamValues (a fresh
    // array after every change, never mutated); ParamVersion changes with every change.
    public static IReadOnlyList<ParamInfo> Params => ParamRegistry.All;
    public static IReadOnlyList<string> ParamGroups => ParamRegistry.Groups;
    public static double[] ParamValues => ParamRegistry.Published;
    public static long ParamVersion => ParamRegistry.Version;

    public void SetParam(string name, double value, Action<bool> done = null) => Do(w =>
    {
        bool ok = w.SetParam(name, value);
        if (ok)
        {
            string v = ParamRegistry.Get(name).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Notice(Loc.T($"law {name} = {v}", $"закон {name} = {v}"));
        }
        else Fail(Loc.T($"no law {name}", $"нет закона {name}"));
        done?.Invoke(ok);
    });

    public void ResetParams() => Do(w => { w.ResetParams(); Notice(Loc.T("world laws reset to defaults", "законы мира — по умолчанию")); });

    public void ApplyPreset(ParamPreset preset) => Do(w =>
    {
        var unknown = w.ApplyParams(preset.Values);
        Notice(Loc.T($"law preset \"{preset.Name}\"" + (unknown.Count > 0 ? $", unknown: {string.Join(", ", unknown)}" : ""),
            $"набор законов «{preset.Name}»" + (unknown.Count > 0 ? $", неизвестны: {string.Join(", ", unknown)}" : "")));
    });

    public void LoadPreset(string path, Action<ParamPreset, string> done = null) => Do(w =>
    {
        try { var p = ParamRegistry.LoadPreset(path); w.ApplyParams(p.Values); Notice(Loc.T($"laws from {path}", $"законы из {path}")); done?.Invoke(p, null); }
        catch (Exception e) { Fail(Loc.T($"cannot read {path}: {e.Message}", $"не прочитать {path}: {e.Message}")); done?.Invoke(null, e.Message); }
    });

    // Saves the current laws as a preset (only those that differ from the defaults unless full).
    public void SavePreset(string path, string name, string description = "", bool full = false) => Do(_ =>
    {
        try { ParamRegistry.SavePreset(path, ParamRegistry.Capture(name, description, full)); Notice(Loc.T($"laws saved: {path}", $"законы сохранены: {path}")); }
        catch (Exception e) { Fail(Loc.T($"cannot save {path}: {e.Message}", $"не сохранить {path}: {e.Message}")); }
    });

    // A catastrophe (World.Catastrophe, ROADMAP 9.4): applied between ticks, chronicled and logged with the laws.
    public void Catastrophe(Catastrophe c, Action<string> done = null) => Do(w =>
    {
        string text = w.Catastrophe(c, out string error);
        if (text != null) { Notice(Loc.T("catastrophe: ", "катастрофа: ") + text); PublishChronicle(w, true); } else Fail($"{Primordium.Catastrophe.Names[(int)c.Kind]}: {error}");
        done?.Invoke(text);
    });

    public void SetAbiogenesis(bool on) => Do(w => w.Abiogenesis = on);
    public void SetStrikes(bool on) => Do(w => w.AutoStrikes = on);

    // A new world from settings (its preset, if any, replaces the laws), made on this thread and
    // swapped in between ticks; `warm` ticks are stepped before it is shown.
    public void NewWorld(WorldSettings settings, int warm = 0, Action<World> done = null) => Do(_ =>
    {
        var w = new World(settings);
        for (int i = 0; i < warm; i++) w.Step();
        ReplaceWorld(w);
        Notice(Loc.T($"new world: seed {w.Seed}", $"новый мир: seed {w.Seed}"));
        done?.Invoke(w);
    });

    public sealed class SaveOutcome
    {
        public string Path, Error;
        public long Tick, Bytes;
        public double Ms;
        public bool Ok => Error == null;
    }

    public void Save(string path, string note = null, Action<SaveOutcome> done = null) => Do(w => done?.Invoke(SaveNow(w, path, note)));

    SaveOutcome SaveNow(World w, string path, string note)
    {
        var o = new SaveOutcome { Path = path, Tick = w.Tick };
        string name = System.IO.Path.GetFileName(path);
        w.Add(EvType.Player, Loc.Both($"{(note == "autosave" ? "autosave" : "saved")}: {name}", $"{(note == "autosave" ? "автосохранение" : "сохранено")}: {name}"));
        var t0 = Stopwatch.GetTimestamp();
        try { w.Save(path, note); o.Bytes = new System.IO.FileInfo(path).Length; }
        catch (Exception e) { o.Error = e.Message; }
        o.Ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        string file = System.IO.Path.GetFileName(path);
        if (o.Ok) Notice(Loc.T($"saved: {file} (tick {o.Tick:N0}, {o.Bytes / 1048576.0:F1} MB, {o.Ms:F0} ms)", $"сохранено: {file} (тик {o.Tick:N0}, {o.Bytes / 1048576.0:F1} МБ, {o.Ms:F0} мс)"));
        else Fail(Loc.T($"cannot save {file}: {o.Error}", $"не сохранить {file}: {o.Error}"));
        return o;
    }

    // Loads a saved world in place of the current one (its laws come with it).
    public void Load(string path, Action<World, string> done = null) => Do(_ =>
    {
        try
        {
            var w = World.Load(path);
            string file = System.IO.Path.GetFileName(path);
            w.Add(EvType.Player, Loc.Both($"loaded: {file} (tick {w.Tick:N0})", $"загружено: {file} (тик {w.Tick:N0})"));
            ReplaceWorld(w);
            Notice(Loc.T($"loaded: {file} (seed {w.Seed}, tick {w.Tick:N0}, {w.Agents.Count:N0} bodies)", $"загружено: {file} (seed {w.Seed}, тик {w.Tick:N0}, особей {w.Agents.Count:N0})"));
            done?.Invoke(w, null);
        }
        catch (Exception e) { Fail(Loc.T($"cannot load {System.IO.Path.GetFileName(path)}: {e.Message}", $"не загрузить {System.IO.Path.GetFileName(path)}: {e.Message}")); done?.Invoke(null, e.Message); }
    });

    public static SaveInfo ReadSaveInfo(string path) => World.ReadInfo(path);

    // Plants a player design at cell (x, y) (see World.SpawnDesign for where matter and energy come from).
    public void SpawnDesign(CreatureDesign design, int x, int y, SpawnOptions options, Action<SpawnResult> done = null)
    {
        var d = design.Clone();   // the UI may keep editing its copy
        Do(w =>
        {
            var r = w.SpawnDesign(d, x, y, options);
            if (r.Made > 0) PublishDesigned(w);
            if (r.Ok) Notice(SpawnText(w, d, r, options)); else Fail(SpawnText(w, d, r, options));
            done?.Invoke(r);
        });
    }

    static string SpawnText(World w, CreatureDesign d, SpawnResult r, SpawnOptions o)
    {
        if (r.Made == 0) return Loc.T($"\"{CreatureExamples.DisplayName(d.Name)}\" not planted: {r.Error ?? "no room"}", $"«{CreatureExamples.DisplayName(d.Name)}» не посажен: {r.Error ?? "нет места"}");
        string atoms = string.Join(", ", Enumerable.Range(0, Chemistry.ElementCount).Where(e => r.AtomsImported[e] > 0).Select(e => $"{w.Chem.ElementName[e]} {r.AtomsImported[e]:0}"));
        string matter = o.Matter == MatterSource.Import
            ? Loc.T($"atoms brought from outside ({atoms})", $"атомы принесены извне ({atoms})")
            : Loc.T("local matter", "вещество местное");
        string energy = o.Energy == EnergySource.Import
            ? Loc.T($"energy from outside {r.EnergyImported:0.#}", $"энергия извне {r.EnergyImported:0.#}")
            : Loc.T($"energy of local reactions {r.EnergyLocal:0.#}", $"энергия местных реакций {r.EnergyLocal:0.#}");
        string s = Loc.T($"\"{CreatureExamples.DisplayName(d.Name)}\": planted {r.Made} of {r.Requested} · {matter} · {energy} · lineage #{r.Lineage}",
            $"«{CreatureExamples.DisplayName(d.Name)}»: посажено {r.Made} из {r.Requested} · {matter} · {energy} · линия #{r.Lineage}");
        if (r.Error != null) s += Loc.T($" (the rest: {r.Error})", $" (остальным: {r.Error})");
        return s;
    }

    // Copies living bodies chosen by `select` into a population template (Population.cs); `done` gets
    // it (with no bodies if none was found) on the simulation thread.
    public void CopyPopulation(Func<World, List<Agent>> select, string name, string source, Action<PopulationTemplate> done) => Do(w =>
    {
        var t = w.CopyPopulation(select(w), name, source);
        done?.Invoke(t);
    });

    // Pastes a population template with its anchor at cell (x, y) (World.PastePopulation: matter and
    // energy from the place or brought from outside, another chemistry mapped).
    public void PastePopulation(PopulationTemplate template, int x, int y, PasteOptions options, Action<PasteResult> done = null) => Do(w =>
    {
        var r = w.PastePopulation(template, x, y, options);
        if (r.Made > 0) PublishDesigned(w);
        if (r.Ok) Notice(PasteText(w, template, r, options)); else Fail(PasteText(w, template, r, options));
        done?.Invoke(r);
    });

    static string PasteText(World w, PopulationTemplate t, PasteResult r, PasteOptions o)
    {
        if (r.Made == 0) return Loc.T($"population \"{t.Name}\" not pasted: {r.Error ?? "no room"}", $"популяция «{t.Name}» не вставлена: {r.Error ?? "нет места"}");
        string atoms = string.Join(", ", Enumerable.Range(0, Chemistry.ElementCount).Where(e => r.AtomsImported[e] > 0).Select(e => $"{w.Chem.ElementName[e]} {r.AtomsImported[e]:0}"));
        string matter = o.Matter == MatterSource.Import ? Loc.T($"atoms brought from outside ({atoms})", $"атомы принесены извне ({atoms})") : Loc.T("local matter", "вещество местное");
        string energy = o.Energy == EnergySource.Import
            ? Loc.T($"energy from outside {r.EnergyImported:0.#}", $"энергия извне {r.EnergyImported:0.#}")
            : Loc.T($"energy of local reactions {r.EnergyLocal:0.#}", $"энергия местных реакций {r.EnergyLocal:0.#}");
        string s = Loc.T($"population \"{t.Name}\": pasted {r.Made} of {r.Requested} · {r.Lineages.Count} lineages · {matter} · {energy}",
                         $"популяция «{t.Name}»: вставлено {r.Made} из {r.Requested} · линий {r.Lineages.Count} · {matter} · {energy}");
        if (!r.Map.Same) s += Loc.T($" · another chemistry: {r.Map.Changed} species mapped, {r.GenesRemapped} protein genes remapped", $" · другая химия: заменено видов {r.Map.Changed}, перенацелено генов белков {r.GenesRemapped}");
        if (r.Failures.Count > 0) s += Loc.T($" (not made: {string.Join("; ", r.Failures.Select(kv => $"{kv.Key} ×{kv.Value}"))})", $" (не сделаны: {string.Join("; ", r.Failures.Select(kv => $"{kv.Key} ×{kv.Value}"))})");
        return s;
    }

    // Autosave: every AutosaveMinutes of wall time (0 = off) into AutosaveDir/autosave_K.sav, K
    // rotating through AutosaveSlots. Not while fast-forwarding; skipped while paused with nothing new.
    public volatile float AutosaveMinutes;
    public volatile string AutosaveDir;
    public volatile int AutosaveSlots = 3;
    public volatile SaveOutcome LastAutosave;
    double autosaveAt = -1;
    long autosavedTick = -1;
    int autosaveSlot;

    void MaybeAutosave(double now)
    {
        float minutes = AutosaveMinutes;
        string dir = AutosaveDir;
        if (minutes <= 0 || string.IsNullOrEmpty(dir)) { autosaveAt = -1; return; }
        if (autosaveAt < 0) { autosaveAt = now + minutes * 60; return; }
        if (now < autosaveAt) return;
        autosaveAt = now + minutes * 60;
        var w = World;
        if (w.Tick == autosavedTick) return;
        string path = System.IO.Path.Combine(dir, $"autosave_{autosaveSlot}.sav");
        autosaveSlot = (autosaveSlot + 1) % Math.Max(1, AutosaveSlots);
        LastAutosave = SaveNow(w, path, "autosave");
        autosavedTick = w.Tick;
    }

    // Swaps in another world (on this thread, between ticks) and forgets what belonged to the old one.
    void ReplaceWorld(World w)
    {
        world = w;
        Selected = Hover = null;
        histOf = null; selHistN = 0; histN = 0;
        trackedSel = kidsOf = parentOf = null; parent = null; kids.Clear();
        Array.Copy(w.Ev, evPrev, evPrev.Length);
        Array.Clear(evRate);
        Array.Clear(Stress);
        FastTo = -1;
        autosavedTick = -1;
        PublishDesigned(w);
        WorldGeneration++;
        PublishChronicle(w, true);
        PublishEvolution(w, true);
        RefreshStats();
        Publish();
    }
}
