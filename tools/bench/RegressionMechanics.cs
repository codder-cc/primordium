using System;
using System.Linq;

namespace Primordium;

public sealed partial class World
{
    public static void MechanicsRegression()
    {
        ParamRegistry.ResetDefaults();
        StructureRegression(); StackRegression(); ConfinementRegression(); ClimbRegression(); SettleRegression(); ImpactRegression(); RubbleRegression(); ReliefRegression();
    }

    // What a file before version 15 cannot hold: the world as such a file loads it (relief scale 1,
    // settling looked at afresh, no body pressing against a ledge).
    void MechanicsFromOldFile()
    {
        reliefScale = 1;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) Height0[y * W + x] = GenHeight(x, y);
        Array.Clear(settleDebt); Array.Fill(elasticSeen, float.NaN); Array.Clear(settleCheck);
        SettledMolecules = ReboundMolecules = SettleEvents = 0;
        foreach (var a in Agents) { a.Climb = 0; a.ClimbDir = -1; }
    }

    // Climbing ledges (World.Move, Lifted): a ledge more than one block up takes momentum — a block of rise
    // per unit of speed. One push is not enough for three blocks; pushing on for a few ticks is (the pressing
    // is kept, bled by friction), and so is a strong push at once; a wall of ten blocks is never climbed by
    // steady full pushes; a roof over its own cell keeps a body from climbing up the face; the work against
    // gravity is paid by mass.
    static void ClimbRegression()
    {
        var w = Fixture();
        int s = 0, row = 30 * W;
        Agent Body(int x, int n) { var a = w.TestAgent(row + x, 2, s, n); a.Energy = 1000; return a; }
        void Ledge(int x, int top) { for (int z = 2; z < top; z++) w.TestBlock(row + x, z, s); }
        // Three blocks up.
        Ledge(21, 5);
        var a = Body(20, 20);
        int cell = a.Y * W + a.X;
        a.Vx = 1.05f; w.Move(a, ref cell);
        Require(a.X == 20 && a.Climb > 0, $"one push climbed a 3-block ledge or kept nothing: x {a.X}, pressing {a.Climb}");
        int ticks = 1;
        double e0 = a.Energy;
        while (a.X == 20 && ticks < 60) { a.Vx += 1.05f; w.Move(a, ref cell); ticks++; }
        Require(a.X == 21 && a.Z == 5, $"steady pushing did not get over a 3-block ledge in {ticks} ticks (x {a.X}, z {a.Z})");
        double paid = e0 - a.Energy, work = P.CostClimb * 3 * a.Mass;
        Require(Math.Abs(paid - work) < 1e-6 * (1 + work), $"the climb's work against gravity: paid {paid}, expected {work}");
        // A strong push (several in one tick) at once.
        Ledge(41, 5);
        var b = Body(40, 20);
        int bc = b.Y * W + b.X;
        b.Vx = 3.2f; w.Move(b, ref bc);
        Require(b.X == 41 && b.Z == 5 && Math.Abs(b.Vx) < 0.85f, $"a push of 3.2 did not lift a body 3 blocks (x {b.X}, z {b.Z}, v {b.Vx})");
        // Ten blocks: never with steady full pushes.
        Ledge(61, 12);
        var d = Body(60, 20);
        int dc = d.Y * W + d.X;
        for (int t = 0; t < 300; t++) { d.Vx += 1.05f; w.Move(d, ref dc); }
        Require(d.X == 60, "a wall of ten blocks was climbed by steady pushes");
        // A roof over its own cell (a tunnel): no climbing up the face through it.
        Ledge(81, 5);
        w.TestBlock(row + 80, 3, s, 255, 1);   // a one-molecule roof slab over the body's floor
        var r = Body(80, 20);
        int rc = r.Y * W + r.X;
        r.Z = 2;
        r.Vx = 5f; w.Move(r, ref rc);
        Require(r.X == 80, "a body climbed up a face through the roof over it");
        // Heavier bodies pay more for the same climb.
        Ledge(101, 5); Ledge(121, 5);
        var light = Body(100, 10); var heavy = Body(120, 40);
        int lc = light.Y * W + light.X, hc = heavy.Y * W + heavy.X;
        double l0 = light.Energy, h0 = heavy.Energy;
        light.Vx = 3.2f; heavy.Vx = 3.2f;
        w.Move(light, ref lc); w.Move(heavy, ref hc);
        Require(light.X == 101 && heavy.X == 121, "strong pushes did not lift both");
        double pl = l0 - light.Energy, ph = h0 - heavy.Energy;
        Require(ph > 3 * pl, $"a heavier body did not pay more: {pl:F3} vs {ph:F3}");
        w.CheckCellLists();
        Console.WriteLine($"PASS climb: a 3-block ledge in {ticks} ticks of steady pushing (not with one), at once with a push of 3.2, never a 10-block wall, not through a roof; work by mass {pl:F3} / {ph:F3}");
    }

    // Settling (World.Settle). With a soft law (ModulusRatio 2, strain at failure 50%): a column loaded on
    // top is squeezed and its matter consolidates downwards (the top comes down), unloaded it springs back
    // (molecules go up); a confined loose stratum compacting under pressure lets the column above settle
    // into it. Atoms exact throughout.
    static void SettleRegression()
    {
        ParamRegistry.ResetDefaults();
        float mr = P.ModulusRatio;
        try
        {
            P.ModulusRatio = 2;
            var w = Fixture();
            int s = Enumerable.Range(0, Chemistry.S).Where(x => x % 2 == 0).OrderByDescending(x => w.Chem.Bond[x]).First();
            int c = 50 * W + 50;
            for (int z = 2; z < 32; z++) w.TestBlock(c, z, s, 100, w.BlockCapacity(s, 100));   // a softer lattice (order 100)
            w.StepStructure();
            w.SettleColumns();   // the first look records its strain: born in equilibrium
            Require(w.SettleEvents == 0, "a column settled at its first look");
            float Fullest() { float m = 0; for (int z = 2; z < w.Height[c]; z++) m = Math.Max(m, w.VoxelVolume(c * Z + z)); return m; }
            for (int z = 32; z < 92; z++) w.TestBlock(c, z, s, 100, w.BlockCapacity(s, 100));
            var before = w.ElementBudget();
            for (int i = 0; i < 6; i++) { w.StepStructure(); w.SettleColumns(); }
            long down = w.SettledMolecules, events = w.SettleEvents;
            int top1 = w.Height[c];
            float full1 = Fullest();
            Require(events > 0 && down > 0 && top1 < 92 && full1 > 1.01f * P.VoxelSpace,
                $"a loaded column did not settle: {events} events, {down} molecules moved, top {top1} of 92, fullest block {full1:F0} of {P.VoxelSpace:F0}");
            BudgetEqual(before, w.ElementBudget(), "settling", 1e-6);
            // Unload: take the load off (down to level 32) and let it spring back.
            for (int z = w.Height[c] - 1; z >= 32; z--) w.RemoveVoxel(c, z);
            var unloaded = w.ElementBudget();
            int top2 = w.Height[c];
            for (int i = 0; i < 6; i++) { w.StepStructure(); w.SettleColumns(); }
            Require(w.ReboundMolecules > 0 && w.Height[c] > top2, $"an unloaded column did not spring back: {w.ReboundMolecules} molecules, top {top2} → {w.Height[c]}");
            BudgetEqual(unloaded, w.ElementBudget(), "rebound", 1e-6);
            float vol2 = Fullest();

            // Compaction: a loose stratum (order 0) in a pit of ordered rock packs under its own weight; the
            // column above it settles into the freed room, the top coming down.
            P.ModulusRatio = mr;
            var k = Fixture();
            int pit = 80 * W + 120;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int q = pit + dy * W + dx;
                    bool wall = Math.Abs(dx) == 2 || Math.Abs(dy) == 2;
                    for (int z = 2; z < 62; z++) k.TestBlock(q, z, s, wall ? (byte)255 : (byte)0, wall ? -1 : k.BlockCapacity(s, 0));
                }
            var b0 = k.ElementBudget();
            k.StepStructure();
            k.SettleColumns();
            for (int t = 0; t < 250; t++) { k.Metamorphose(); k.StepStructure(); }
            int top = k.Height[pit];
            Require(k.SettleEvents > 0 && top < 62, $"a compacting stratum did not let the column settle: {k.SettleEvents} events, top {top} of 62");
            Require(k.Order[pit * Z + 3] > 0, "the loose stratum did not compact");
            BudgetEqual(b0, k.ElementBudget(), "compaction settling", 1e-6);
            Console.WriteLine($"PASS settle: soft law — a column loaded with 60 blocks came down 92 → {top1} ({down} molecules moved, fullest block {full1:F0} of {P.VoxelSpace:F0}), unloaded sprang back {top2} → {w.Height[c]} ({w.ReboundMolecules} molecules, fullest {vol2:F0}); a compacting loose stratum (order → {k.Order[pit * Z + 3]}) let its column settle 62 → {top} ({k.SettleEvents} settling events in the pit); atoms exact");
        }
        finally { P.ModulusRatio = mr; }
    }

    // Relief (P.ReliefScale): the same map with the rise from valleys to mountains stretched — four times as
    // high at 4 (the default is 1), and the altitude climate's span kept (World.Lapse: TLapse / scale per level).
    static void ReliefRegression()
    {
        ParamRegistry.ResetDefaults();
        float scale = P.ReliefScale, lapse = P.TLapse;
        try
        {
            P.ReliefScale = 1;
            var w1 = new World(new WorldSettings { Seed = 3, InitialPop = 0 });
            P.ReliefScale = 4;
            var w4 = new World(new WorldSettings { Seed = 3, InitialPop = 0 });
            int Rise(World w) { var h = w.Height0.OrderBy(x => x).ToArray(); return h[h.Length * 99 / 100] - h[h.Length / 100]; }
            int r1 = Rise(w1), r4 = Rise(w4);
            Require(r4 > 3.4 * r1 && r4 < 4.3 * r1, $"relief not stretched 4×: rise {r1} → {r4}");
            Require(w4.Height.Max() <= Z - 14 && w4.Height.Min() >= Crust + 2, "relief out of the world");
            float span1 = w1.Lapse * r1, span4 = w4.Lapse * r4;
            Require(w1.Lapse == P.TLapse && Math.Abs(w4.Lapse - P.TLapse / 4) < 1e-6f, "lapse rate does not follow the world's relief");
            Require(Math.Abs(span4 - span1) < 0.2f * span1, $"altitude climate span changed: {span1:F1} → {span4:F1} °C");
            Console.WriteLine($"PASS relief: rise p1–p99 {r1} → {r4} levels (×4), highest {w4.Height.Max()}, altitude span {span1:F1} → {span4:F1} °C");
        }
        finally { P.ReliefScale = scale; P.TLapse = lapse; }
    }

    // A falling block's blow (World.Crush): bodies in its path are torn by their share of the block's weight ×
    // the fall, as far as their molecules hold — a body of strongly bonded molecules loses less than one of
    // weak ones; under the world's gravity a fall of a few levels only scratches; survivors end up beside or
    // on the block; atoms exact (torn molecules lie buried where it landed).
    static void ImpactRegression()
    {
        ParamRegistry.ResetDefaults();
        float g = P.Gravity;
        try
        {
            var w = Fixture();
            var ch = w.Chem;
            int strong = Enumerable.Range(0, Chemistry.S).Where(x => x != ch.Gas).OrderByDescending(x => ch.Bond[x]).First();
            int weak = Enumerable.Range(0, Chemistry.S).Where(x => x != ch.Gas).OrderBy(x => ch.Bond[x]).First();
            int rock = Enumerable.Range(0, Chemistry.S).Where(x => x % 2 == 0).OrderByDescending(x => ch.Bond[x]).First();
            (int lost, bool dead) Drop(int c, int s, int fall)
            {
                var a = w.TestAgent(c, 2, s, 60);
                w.TestBlock(c, 2 + fall, rock);
                int before = a.InvTotal;
                var atoms = w.ElementBudget();
                w.TransferVoxel(c * Z + 2 + fall, c * Z + 2, true);
                w.SettleAll();
                BudgetEqual(atoms, w.ElementBudget(), "impact", 1e-6);
                return (before - a.InvTotal, a.Dead);
            }
            var mild = Drop(20 * W + 20, weak, 4);
            Require(!mild.dead, $"a fall of 4 levels killed a body outright under the world's gravity (lost {mild.lost})");
            P.Gravity = g * 400;   // a heavy world: the same blow tears much more
            var weakHit = Drop(20 * W + 40, weak, 6);
            var strongHit = Drop(20 * W + 60, strong, 6);
            Require(weakHit.lost > strongHit.lost, $"a shell did not help: weak lost {weakHit.lost}, strong {strongHit.lost}");
            Require(weakHit.lost > mild.lost, "a heavier blow did not tear more");
            w.Agents.RemoveAll(x => x.Dead);
            w.CheckCellLists();
            Console.WriteLine($"PASS impact: a 4-level fall tears {mild.lost} of 60 weak molecules; with gravity ×400 a 6-level fall tears {weakHit.lost} weak ({(weakHit.dead ? "dead" : "alive")}) vs {strongHit.lost} strongly bonded ({(strongHit.dead ? "dead" : "alive")}); atoms exact");
        }
        finally { P.Gravity = g; }
    }
}
