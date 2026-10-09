using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Primordium;

// The body pass (P.BodyPass 1; 0 — as before, bit for bit). A body's tick is its turn in its tile (World.LiveBody:
// settling, thinking and acting, moving, links, reactive damage from its floor) and then what it does to itself
// (World.LiveRest: body temperature, the heat it held, a flare, reactive damage from what it holds, upkeep and
// harm, protein wear, settling its costs, decay, UV, wear, starving). With the law on the second part runs after
// the whole tile phase, for every body that had its turn, in parallel: in a boom the tiles are stepped by a
// chain of adjacent hot tiles one after another (the checkerboard), and this part of every body in them leaves
// that chain.
//
// Determinism. The bodies are taken in the order they had their turns (tile by tile, each tile's shuffled order)
// and cut into fixed chunks of BodyChunk, so what a chunk does never depends on threads. A body's random numbers
// come from its own stream, seeded by the world, the body's id and the tick (nothing to save: the pass ends
// inside the tick). The pass reads only the body itself and world fields the tile phase is done with; what it
// does to the world — heat into cells and cave air, molecules it sheds onto its floor, its death — is collected
// per chunk and applied after the pass, chunk by chunk, in the same order; its energy flows are summed per chunk
// and added to the main slot in chunk order. With an observation probe on (energy, predation) the chunks run
// one after another (the probes are summed without locks), with the same result.
//
// What changes against law 0 (by design): a body's own costs, decay and death come after everybody's turn
// instead of right after its own (another body may meet it still owing its upkeep, or alive although it is
// about to starve); its random draws for these come from its own stream; heat reaches the cells in another
// order. Statistics of evolution: CHANGELOG 2026-10-09 (14).
public sealed partial class World
{
    const int BodyChunk = 256;
    bool bodyPassOn;   // the law, latched for this tick's agent phase
    bool deferring;    // the pass is running: effects on the world are collected (Ctx.Defer)
    public static bool BodyPassSerial;   // tests: the chunks one after another on this thread (the same world)
    readonly List<Agent> passList = new();
    readonly List<Ctx> passCtx = new();

    // heatIn[cell] += h (deferred inside the body pass).
    void AddHeat(int cell, float h)
    {
        if (deferring) { var c = cur; if (c != null && c.Defer) { c.Heat.Add((cell, h, false)); return; } }
        heatIn[cell] += h;
    }

    // caveHeatIn[cell] += h (deferred inside the body pass).
    void AddCaveHeat(int cell, float h)
    {
        if (deferring) { var c = cur; if (c != null && c.Defer) { c.Heat.Add((cell, h, true)); return; } }
        caveHeatIn[cell] += h;
    }

    // ChangeLoose(a, cell, s, amount) of something the body sheds (amount > 0: it can always be added later).
    void AddLoose(Agent a, int cell, int s, Qty amount)
    {
        if (deferring) { var c = cur; if (c != null && c.Defer) { c.Loose.Add((cell, a.Z, s, amount)); return; } }
        ChangeLoose(a, cell, s, amount);
    }

    // The end of LiveRest: dies now, or after the body pass.
    void DieAfter(Agent a, int cell, int cause)
    {
        if (deferring) { var c = cur; if (c != null && c.Defer) { c.Deaths.Add((a, cause)); return; } }
        Die(a, cell, cause);
    }

    // After the tile phase (P.BodyPass 1): the rest of the tick of every body that had its turn.
    void BodyPass()
    {
        passList.Clear();
        long tick = Tick;
        for (int t = 0; t < Tiles; t++)
            foreach (var a in tiles[t]) if (a.PassTick == tick && !a.Dead) passList.Add(a);
        int n = passList.Count, chunks = (n + BodyChunk - 1) / BodyChunk;
        while (passCtx.Count < chunks)
            passCtx.Add(new Ctx { Rng = new SimRng(0), Slot = Tiles, Flow = new double[FlowCount], Defer = true });
        long salt = (long)Hash32.U((uint)Seed * 2654435761u ^ (uint)Settings.LifeSeed * 40503u) << 32 ^ 0x5EED_B0D1;
        bool probes = EnergyProbe != null || PredProbe != null || FoodProbe != null || ResProbe != null;
        deferring = true;
        try
        {
            if (probes || BodyPassSerial) for (int ch = 0; ch < chunks; ch++) PassChunk(ch, n, tick, salt);
            else Parallel.For(0, chunks, ch => PassChunk(ch, n, tick, salt));
        }
        finally { deferring = false; }
        // What the chunks did to the world, in chunk order.
        var main = Flows;   // (main thread: the main slot)
        for (int ch = 0; ch < chunks; ch++)
        {
            var ctx = passCtx[ch];
            foreach (var (cell, heat, cave) in ctx.Heat)
                if (cave) caveHeatIn[cell] += heat; else heatIn[cell] += heat;
            ctx.Heat.Clear();
            foreach (var (cell, level, s, amount) in ctx.Loose) ChangeLooseAt(cell, level, s, amount);
            ctx.Loose.Clear();
            var f = ctx.Flow;
            for (int k = 0; k < f.Length; k++) { main[k] += f[k]; f[k] = 0; }
            foreach (var (a, cause) in ctx.Deaths) Die(a, a.Y * W + a.X, cause);
            ctx.Deaths.Clear();
            foreach (int c in ctx.Dirty) structuralDirty.Add(c);
            ctx.Dirty.Clear();
            for (int k = 0; k < ctx.Ev.Length; k++) { Ev[k] += ctx.Ev[k]; ctx.Ev[k] = 0; }
            MergeOrganStats(ctx.Organ);
            ChronMerge(ctx);
        }
        passList.Clear();
    }

    void PassChunk(int ch, int n, long tick, long salt)
    {
        var ctx = passCtx[ch];
        cur = ctx;
        try
        {
            for (int k = ch * BodyChunk, end = Math.Min(n, k + BodyChunk); k < end; k++)
            {
                var a = passList[k];
                if (a.Dead || a.PassTick != tick) continue;   // killed after its turn
                ctx.Rng.Reseed(salt ^ (long)Mix((ulong)a.Id * 0x9E3779B97F4A7C15UL + (ulong)tick));
                ctx.Body = a;
                LiveRest(a, a.PassCell, a.PassE0, a.PassKids0, a.PassSpent0, true);
                ctx.Body = null;
            }
        }
        finally { cur = null; }
    }

    static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
