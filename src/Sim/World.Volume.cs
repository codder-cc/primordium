using System;
using System.Collections.Generic;
using System.Threading;

namespace Primordium;

// Volume — the third basic quantity of the world, next to mass and energy. Every molecule takes up
// room (Chemistry.Volume: its mass over how tightly it packs); a body takes the room of its
// molecules, loose remains lying on a floor are bulkier (P.LooseBulk). The free space above a floor
// holds P.VoxelSpace. Nothing counts bodies: when a floor is overfull the smallest bodies are pushed
// out onto neighbouring floors at the same level or lower — never up — so the big and heavy hold
// their ground and squeeze the small away, while a crowd of tiny ones fits where a few big ones
// would. Loose matter beyond P.CompactShare of a floor runs downhill (Settle), and where it can't,
// is pressed into rock. Rock that is laid onto a floor (sediment, building, a slid or dropped
// block) takes the whole voxel and pushes out whatever was there the same way.
public sealed partial class World
{
    public readonly float[] LooseVolume = new float[N];   // loose remains on each surface floor (refreshed with the soil chemistry)
    public long Pushed;                                     // bodies pushed off overfull floors (diagnostics)

    // How much of a body takes room in one of its cells (a big body spreads over its footprint).
    static float Share(Agent a) => a.Volume / a.Cells;

    float LooseAt(int cell, int level)
    {
        if (level >= Height[cell]) return LooseVolume[cell];
        if (!BurialOf(LooseVoxel(cell, level), out var b)) return 0;   // remains on a cave floor
        float v = 0;
        for (int s = 0; s < Chemistry.S; s++) v += b.Matter[s].F * Chem.Volume[s];
        return v * P.LooseBulk;
    }

    // Room taken on the floor at `level` of `cell`, counting only bodies of at least `minShare` (the
    // ones a newcomer of that size could not push aside), plus the loose remains there.
    float FloorVolume(int cell, int level, float minShare = 0, Agent except = null)
    {
        float v = LooseAt(cell, level);
        for (var o = Head[cell]; o != null; o = o.NextInCell)
            if (o != except && o.Z == level && Share(o) >= minShare) v += Share(o);
        var big = Big[cell];
        if (big != null && big != except && big.Z == level && Share(big) >= minShare) v += Share(big);
        return v;
    }

    // How full a floor is (1 = no room left), for the panels.
    public float FloorFill(int cell, int level) => FloorVolume(cell, level) / Space(cell, level);

    // Can `a` (or a body of `share`) take its place on that floor, pushing aside anybody smaller?
    // A flooded floor has the water over it too: swimmers count towards their floor (World.Space).
    bool Fits(int cell, int level, float share, Agent a = null) =>
        FloorVolume(cell, level, share, a) + share <= Space(cell, level);

    // Where a body squeezed off a floor can go: a neighbouring floor at the same level or lower (the
    // lowest first), with room for it.
    // `arriving`: room already promised to bodies that will move there (Relieve).
    int Outlet(int cell, int level, float share, out int toLevel, Dictionary<int, float> arriving = null)
    {
        int best = -1;
        toLevel = -1;
        for (int d = 0; d < 4; d++)
        {
            int n = nb[cell * 4 + d];
            if (n == cell) continue;
            int lvl = WalkLevel(n, level);
            if (lvl < 0 || lvl > level) continue;
            float promised = arriving != null && arriving.TryGetValue(n * Z + lvl, out float p) ? p : 0;
            if (FloorVolume(n, lvl) + promised + share > Space(n, lvl)) continue;
            if (best < 0 || lvl < toLevel) { best = n; toLevel = lvl; }
        }
        return best;
    }

    void Shift(Agent a, int from, int to, int level)
    {
        float was = Level(a);
        Unplace(a, from);
        Place(a, to);
        a.Z = level;
        SetLift(a, to, level, was);
        // Pushed over an edge it falls like any body (World.Move): more than a block down hurts.
        float drop = was - Level(a);
        if (drop > 1 && !InWater(to, level)) Dissipate(a, P.CostFall * (drop - 1) * (1 + a.Mass * P.Gravity));
        Interlocked.Increment(ref Pushed);
    }

    // A block now fills the voxel at `level` of `cell`: whoever stood there is pushed onto a
    // neighbouring floor (same level or lower) if there is room, otherwise rides up onto the block.
    // A builder laying a block under itself (`rider`) climbs onto it.
    void DisplaceOccupants(int cell, int level, Agent rider = null)
    {
        for (var a = Head[cell]; a != null;)
        {
            var next = a.NextInCell;
            if (a.Z == level && a.Lift >= 1) { a.Z++; a.Lift -= 1; }   // swimming above it: the floor just rises beneath
            else if (a.Z == level)
            {
                int to = -1, lvl = -1;
                if (a != rider && a.Cells == 1) to = Outlet(cell, level, Share(a), out lvl);
                if (to >= 0) Shift(a, cell, to, lvl);
                else { a.Z++; a.Lift = 0; }
            }
            a = next;
        }
        // A big body with a foot on that floor draws the foot back (it spreads anew when it lives).
        var big = Big[cell];
        if (big != null && !big.Dead && big != rider && big.Z == level) DropFoot(big, cell);
    }

    void DropFoot(Agent a, int cell)
    {
        for (int k = 1; k < a.Cells; k++)
        {
            if (a.Foot[k] != cell) continue;
            for (int j = k; j < a.Cells - 1; j++) a.Foot[j] = a.Foot[j + 1];
            a.Cells--;
            if (Big[cell] == a) Big[cell] = null;
            return;
        }
    }

    // After the bodies have acted: every overfull floor lets its smallest bodies go, sideways or
    // down, until it fits (or nobody has anywhere to go — then the crowd stays squeezed and hot).
    readonly List<Agent> crowd = new();
    readonly List<(Agent a, int from, int to, int level)> shifts = new();
    readonly Dictionary<int, float> arriving = new();
    readonly bool[] overfull = new bool[N];

    // Does a floor of this cell hold more than it has room for? Everybody on one floor (nearly always)
    // is one sum; bodies on several floors go to the full sweep.
    bool Overfull(int c)
    {
        var head = Head[c];
        if (head == null) return false;
        if (head.NextInCell == null && Big[c] == null) return Share(head) + LooseAt(c, head.Z) > Space(c, head.Z);
        for (var a = head.NextInCell; a != null; a = a.NextInCell) if (a.Z != head.Z) return true;
        return FloorVolume(c, head.Z) > Space(c, head.Z);
    }

    // The moves are decided first and made afterwards, so a body pushed into a cell further on in
    // the sweep is not pushed again in the same tick (which would drift crowds one way).
    void Relieve()
    {
        shifts.Clear(); arriving.Clear();
        // Which floors are overfull is read in parallel (nothing is moved until all moves are decided);
        // the sweep then visits only those, in cell order.
        System.Threading.Tasks.Parallel.For(0, H, y =>
        {
            for (int c = y * W, end = c + W; c < end; c++) overfull[c] = Overfull(c);
        });
        for (int c = 0; c < N; c++)
        {
            if (!overfull[c]) continue;
            var head = Head[c];
            crowd.Clear();
            for (var a = head; a != null; a = a.NextInCell) crowd.Add(a);
            // By floor, then smallest first (ties by id: the result never depends on list order).
            crowd.Sort((p, q) => p.Z != q.Z ? p.Z.CompareTo(q.Z) : Share(p) != Share(q) ? Share(p).CompareTo(Share(q)) : p.Id.CompareTo(q.Id));
            for (int i = 0; i < crowd.Count;)
            {
                int level = crowd[i].Z, j = i;
                while (j < crowd.Count && crowd[j].Z == level) j++;
                float taken = FloorVolume(c, level), space = Space(c, level);
                for (int k = i; k < j && taken > space; k++)
                {
                    var a = crowd[k];
                    if (a.Cells > 1) continue;   // a spread-out body holds its ground
                    int to = Outlet(c, level, Share(a), out int lvl, arriving);
                    if (to < 0) continue;
                    shifts.Add((a, c, to, lvl));
                    arriving.TryGetValue(to * Z + lvl, out float p);
                    arriving[to * Z + lvl] = p + Share(a);
                    taken -= Share(a);
                }
                i = j;
            }
        }
        foreach (var (a, from, to, level) in shifts) Shift(a, from, to, level);
    }
}
