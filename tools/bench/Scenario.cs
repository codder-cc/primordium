using System;

namespace Primordium;

// Small worlds for tests (headless runner only). A test that needs a cave, a lake, a cliff or two bodies
// side by side builds them, instead of running a whole planet or searching seeds for the situation:
//
//   Tiny(seed, pop, …)  a generated world of a small size (64×64×64 by default): terrain, water, vents,
//                       litter and first bodies of its seed, in proportion to its area (World.PerArea);
//   Blank(…)            the same size flattened: two levels of bedrock everywhere, no vents, water, ice,
//                       snow, currents or litter (what Fixture() is on the full planet);
// and builders on any world: Floor, Column, Strata, Cave, Lake, Cliff, Pair. They change geometry through
// the world's own methods (PutMixture/TerrainChanged) and settle the support solver, so a test starts
// from a consistent state. Coordinates are cells; the world wraps in x as always.
public sealed partial class World
{
    public const int TinySide = 64, TinyLevels = 64;

    public static WorldSettings TinySettings(int seed = 1, int pop = 0, bool abio = false, bool strikes = false, int width = TinySide, int height = TinySide, int levels = TinyLevels, int life = 0) =>
        new() { Seed = seed, InitialPop = pop, Abiogenesis = abio, Strikes = strikes, LifeSeed = life, Width = width, Height = height, Levels = levels };

    // A small world with life: 96×96×64 (a ninth of the default world's area, a third of its levels),
    // strikes as World(seed, pop, abio) has them. For what life does on any planet (probes, balances,
    // the chronicle, the course of evolution) without latitude or a whole planet's population.
    public const int SmallSide = 96, SmallLevels = 64;
    public static WorldSettings SmallSettings(int seed, int pop = 0, bool abio = false) =>
        TinySettings(seed, pop, abio, !FlareLaw, SmallSide, SmallSide, SmallLevels);

    // A strip of the planet from pole to pole: every row (latitude) of the default world on a quarter of its
    // width and half its levels — for what depends on latitude (climate, ice, the sun). Strikes as World(seed,
    // pop, abio) has them (on unless solar flares replace them).
    public const int StripWidth = 64, StripLevels = 96;
    public static WorldSettings StripSettings(int seed, int pop = 0, bool abio = false) =>
        TinySettings(seed, pop, abio, !FlareLaw, StripWidth, WorldSettings.DefaultHeight, StripLevels);

    // A generated world of a small size. pop < 0: P.InitialPop by area.
    public static World Tiny(int seed = 1, int pop = 0, bool abio = false, bool strikes = false, int width = TinySide, int height = TinySide, int levels = TinyLevels) =>
        new(TinySettings(seed, pop, abio, strikes, width, height, levels));

    // A flat world: two levels of bedrock in every column and nothing else (no strikes).
    public static World Blank(int width = TinySide, int height = TinySide, int levels = TinyLevels, int seed = 1)
    {
        var w = Tiny(seed, 0, false, false, width, height, levels);
        w.Flatten();
        return w;
    }

    // Everything down to two levels of bedrock: no vents, water, ice, snow, currents or loose matter.
    void Flatten()
    {
        AutoStrikes = false;
        Vents.Clear();
        Array.Clear(Mat); Array.Clear(Units); Array.Clear(Order);
        foreach (var c in C) Array.Clear(c);
        Array.Clear(Water); Array.Clear(Ice); Array.Clear(Snow);
        Array.Clear(CurX); Array.Clear(CurY);   // still water (World.Waterways)
        for (int c = 0; c < N; c++)
        {
            Height[c] = 2;
            Mat[c * Z] = Mat[c * Z + 1] = Chemistry.Bedrock;
            Order[c * Z] = Order[c * Z + 1] = 255;
        }
        StepStructure();
    }

    // ---- builders ----

    // Full blocks of species s from the top of a column up to level `top` (exclusive).
    public void Column(int cell, int top, int s, byte order = 255)
    {
        top = Math.Min(top, Z - 1);
        for (int z = Height[cell]; z < top; z++) TestBlock(cell, z, s, order);
    }

    // Every column raised to `top` with species s (a level floor).
    public void Floor(int top, int s, byte order = 255)
    {
        for (int c = 0; c < N; c++) Column(c, top, s, order);
        StepStructure();
    }

    // A column of strata from its top up: (species, levels) bottom first.
    public void Strata(int cell, params (int s, int levels)[] layers)
    {
        foreach (var (s, n) in layers) Column(cell, Height[cell] + n, s);
        StepStructure();
    }

    // A cave in the columns of a w×h rectangle from (x, y): levels floor…floor+height−1 emptied, the rock
    // above left as its roof (the support solver decides whether it holds). Returns the cell at its corner.
    public int Cave(int x, int y, int w, int h, int floor, int height)
    {
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int c = Math.Clamp(y + j, 0, H - 1) * W + ((x + i) % W + W) % W;
                for (int z = floor; z < floor + height && z < Height[c] - 1; z++)
                    if (Mat[c * Z + z] != Chemistry.Air) RemoveVoxel(c, z);
            }
        StepStructure();
        return Math.Clamp(y, 0, H - 1) * W + ((x % W) + W) % W;
    }

    // A lake: a pit of radius r (levels down to `bottom`) in the floor around (cx, cy), filled with water
    // `depth` deep over its bottom.
    public void Lake(int cx, int cy, int r, int bottom, float depth)
    {
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -AroundX(r); dx <= AroundX(r); dx++)
            {
                int y = cy + dy;
                if (y < 0 || y >= H || dx * dx + dy * dy > r * r) continue;
                int c = y * W + ((cx + dx) % W + W) % W;
                while (Height[c] > Math.Max(2, bottom)) RemoveVoxel(c, Height[c] - 1);
                Water[c] = depth;
            }
        StepStructure();
    }

    // A cliff: the columns x0 ≤ x < x1 (all rows) raised to `top` with species s.
    public void Cliff(int x0, int x1, int top, int s)
    {
        for (int y = 0; y < H; y++) for (int x = x0; x < x1; x++) Column(y * W + ((x % W) + W) % W, top, s);
        StepStructure();
    }

    // Two bodies side by side (cells `cell` and its east neighbour), each of `count` molecules of s, standing on the floor.
    public (Agent a, Agent b) Pair(int cell, int s, int count)
    {
        int next = Nb(cell, 0);
        return (TestAgent(cell, Height[cell], s, count), TestAgent(next, Height[next], s, count));
    }
}
