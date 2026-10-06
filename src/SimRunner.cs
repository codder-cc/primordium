using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Primordium;

// What the view needs of one living body, copied on the simulation thread between ticks.
public struct AgentSnap
{
    public Agent Ref;                       // the body itself: only its view-owned fields (VX, VY, VH, SeenAt…) are written by the view
    public int Id, X, Y, Z, Cells, Shape, Lineage, Foot, Crowd;   // Foot: where its cells start in SimFrame.Feet
    public float Lift, Mass, Sx, Sy, Sz, Hue, Sat, Val, EnergyK;
    public float EmaPhoto, EmaChem, EmaMine, EmaAttack;
    public float EnzPhoto, EnzChem, EnzMotor, EnzTotal;
    public int Act, ActDir;
    public long ActTick;
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
}

// Runs World.Step on its own thread. The thread owns the world: everything that changes it (the hand,
// spawning, strikes, a new pace) arrives as a command and is applied between ticks, so the trajectory
// is the same as when the main thread stepped it. After ticks it publishes a SimFrame when the view
// has taken the previous one, and about once a second the census, records and lineages (SimStats).
public sealed class SimRunner
{
    public const int SelHistCap = 300, HistCap = 400;

    public readonly World World;
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
    readonly float[] selHist = new float[SelHistCap];
    int selHistN;
    Agent histOf;
    readonly int[] hist = new int[HistCap * 5];
    int histN;
    readonly long[] evRate = new long[(int)EvKind.Count], evPrev = new long[(int)EvKind.Count];

    public SimRunner(World world)
    {
        World = world;
        Array.Copy(world.Ev, evPrev, evPrev.Length);
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
        w.Step();
        var s = Selected;
        if (s != histOf) { histOf = s; selHistN = 0; }
        if (s is { Dead: false } && w.Tick % 5 == 0)
        {
            if (selHistN == SelHistCap) { Array.Copy(selHist, 1, selHist, 0, SelHistCap - 1); selHistN--; }
            selHist[selHistN++] = s.Energy;
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
                s.EnergyK = Math.Clamp(a.Energy / a.Store, 0, 1);
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
        if (sel == histOf) { Array.Copy(selHist, f.SelHist, selHistN); f.SelHistN = selHistN; }
        else f.SelHistN = 0;
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
}
