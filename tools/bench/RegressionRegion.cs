using System;
using System.IO;
using System.Linq;

namespace Primordium;

// Regions (World.Region.cs, Region.cs): copy → file → load → paste → copy again gives the same cells,
// in both formats and turned; atoms, energy and water balance exactly against the hand's books; pasting
// a region back where it came from restores the world bit for bit; another chemistry is mapped, never
// invented; what does not fit the world is clipped or refused.
public sealed partial class World
{
    // The cells' state beyond StateHash: burials, water, ice, snow, loose volume, pressures.
    ulong RegionStateHash()
    {
        ulong h = StateHash();
        void Mix(ulong x) { h ^= x; h *= 1099511628211UL; h ^= h >> 29; }
        void F(float x) => Mix((uint)BitConverter.SingleToInt32Bits(x));
        foreach (var kv in Buried.OrderBy(kv => kv.Key)) { Mix((ulong)kv.Key); foreach (var m in kv.Value.Matter) Mix((ulong)m.Raw); F(kv.Value.Order); F(kv.Value.Pressure); }
        foreach (var kv in mixtures.OrderBy(kv => kv.Key)) { Mix((ulong)kv.Key); foreach (var n in kv.Value) Mix(n); }
        for (int i = 0; i < N; i++) { F(Water[i]); F(Ice[i]); F(Snow[i]); F(LooseVolume[i]); }
        for (int v = 0; v < N * Z; v++) if (Pressure[v] != 0) { Mix((ulong)v); F(Pressure[v]); }
        return h;
    }

    // Everything the books must explain, read before and after an operation.
    sealed class RegionLedger
    {
        public double[] Atoms, Hand;
        public EnergyAudit Energy;
        public double Water, WaterHand;
        public static RegionLedger Of(World w) => new()
        {
            Atoms = w.ElementBudget(), Hand = (double[])w.HandInput.Clone(), Energy = w.AuditEnergy(), Water = w.WaterTotal(), WaterHand = w.WaterHand,
        };
    }

    static void Balanced(World w, RegionLedger before, string stage)
    {
        var after = RegionLedger.Of(w);
        for (int e = 0; e < Chemistry.ElementCount; e++)
        {
            double world = after.Atoms[e] - before.Atoms[e], hand = after.Hand[e] - before.Hand[e];
            Require(Math.Abs(world - hand) < 1e-6, $"{stage}: element {e} changed by {world:R}, the hand booked {hand:R}");
        }
        double drift = EnergyAudit.Drift(before.Energy, after.Energy);
        Require(Math.Abs(drift) < 1e-3, $"{stage}: energy drift {drift:R} ({after.Energy.Describe(before.Energy)})");
        double water = after.Water - before.Water, booked = after.WaterHand - before.WaterHand;
        Require(Math.Abs(water - booked) < 1e-6, $"{stage}: water changed by {water:R}, the hand booked {booked:R}");
    }

    static Region RoundTrip(Region r, bool json)
    {
        if (json) return Region.FromJson(r.ToJson());
        var ms = new MemoryStream();
        r.Write(ms);
        ms.Position = 0;
        var info = Region.ReadInfo(ms);
        Require(info.SizeX == r.SizeX && info.Bodies == r.Bodies.Count && info.Voxels == r.Voxels, "region header");
        ms.Position = 0;
        return Region.Read(ms);
    }

    static World Twin(World w)
    {
        var ms = new MemoryStream();
        w.Save(ms);
        ms.Position = 0;
        return Load(ms);
    }

    public static void RegionRegression()
    {
        var w = new World(5, 0, false) { AutoStrikes = false };
        for (int t = 0; t < 30; t++) w.Step();

        // Bodies, a mixed heap, a burial and water in the region to copy.
        int px = 100, py = 60, sx = 24, sy = 18;
        for (int k = 0; k < 6; k++)
        {
            int c = (py + 2 + 2 * k) * W + px + 3 + 3 * k;
            w.TestAgent(c, w.Height[c], k % Chemistry.S == w.Chem.Gas ? 1 : k % Chemistry.S, 30);
        }
        w.Pour(px + 8, py + 8, 3, 3 == w.Chem.Gas ? 4 : 3, 0.6f);
        w.PourWater(px + 15, py + 6, 3, 1.5f);
        {
            int c = (py + 10) * W + px + 12, bv = c * Z + w.Height[c] - 4;
            w.BurialAt(bv).Matter[2] += Qty.Of(7.25);
            w.MatterChanged(bv);
        }
        var r = w.CopyRegion(px, py, sx, sy, 0, Z, true, "probe");
        Require(r.Bodies.Count == 6, $"copied {r.Bodies.Count} of 6 bodies");
        Require(r.Voxels > 0 && r.Cols.Any(c => c.Mix.Count > 0) && r.Cols.Any(c => c.Burials.Count > 0) && r.Cols.Any(c => c.Top && c.Water > 0), "the region misses mixtures, burials or water");

        // Files: binary and JSON give the same region.
        foreach (bool json in new[] { false, true })
        {
            var back = RoundTrip(r, json);
            Require(r.SameCells(back, out string why), $"{(json ? "JSON" : "binary")} round trip: {why}");
            Require(back.Bodies.Count == r.Bodies.Count && back.Bodies.Zip(r.Bodies).All(p => p.First.State.AsSpan().SequenceEqual(p.Second.State) && p.First.X == p.Second.X && p.First.Lineage == p.Second.Lineage),
                $"{(json ? "JSON" : "binary")} round trip: bodies differ");
            Require(back.Chem.SameAs(w.Chem), "chemistry signature round trip");
        }
        var loaded = RoundTrip(r, false);

        // Paste elsewhere, as it is: the books balance, a copy of the place gives the same cells.
        int qx = 180, qy = 20;
        var before = RegionLedger.Of(w);
        int alive = w.Agents.Count(a => !a.Dead);
        var res = w.PasteRegion(loaded, qx, qy, new RegionPasteOptions());
        Require(res.Ok && res.BodiesIn == 6 && res.Mapping.Identity && res.VoxelsIn > 0 && res.VoxelsOut > 0, "paste: " + res);
        Balanced(w, before, "paste");
        Require(w.Agents.Count(a => !a.Dead) == alive + res.BodiesIn - res.BodiesOut, "paste: bodies counted");
        w.CheckCellLists();
        var again = w.CopyRegion(qx, qy, sx, sy, 0, Z, true);
        Require(r.SameCells(again, out string why2), "copy after paste: " + why2);
        Require(again.Bodies.Count == 6 && again.Bodies.Sum(b => b.Molecules) == r.Bodies.Sum(b => b.Molecules)
            && Math.Abs(again.Bodies.Sum(b => b.Energy) - r.Bodies.Sum(b => b.Energy)) < 1e-9, "copy after paste: bodies differ");

        // Turned a quarter: every column lands where the turn puts it.
        int tx = 30, ty = 100;
        before = RegionLedger.Of(w);
        res = w.PasteRegion(r, tx, ty, new RegionPasteOptions { Rotation = 1 });
        Require(res.Ok && res.SizeX == sy && res.SizeY == sx, "turned paste: " + res);
        Balanced(w, before, "turned paste");
        var turned = w.CopyRegion(tx, ty, sy, sx, 0, Z, false);
        for (int j = 0; j < sy; j++)
            for (int i = 0; i < sx; i++)
            {
                var (ti, tj) = Rotate(i, j, sx, sy, 1);
                Require(SameColumn(r.Col(i, j), turned.Col(ti, tj)), $"turned paste: column ({i}, {j}) is not at ({ti}, {tj})");
            }

        // Matter only: the bodies standing there stay, nothing of life comes or goes.
        before = RegionLedger.Of(w);
        alive = w.Agents.Count(a => !a.Dead);
        res = w.PasteRegion(r, qx, qy, new RegionPasteOptions { Bodies = false, Rotation = 2 });
        Require(res.Ok && res.BodiesIn == 0 && res.BodiesOut == 0 && w.Agents.Count(a => !a.Dead) == alive, "matter only: " + res);
        Balanced(w, before, "matter only");
        w.CheckCellLists();

        // Above the ground only: nothing is taken out.
        before = RegionLedger.Of(w);
        res = w.PasteRegion(r, 60, 120, new RegionPasteOptions { Mode = PasteMode.AboveGround, Bodies = false, Dz = 6 });
        Require(res.Ok && res.VoxelsOut == 0 && res.AtomsOut.All(a => a == 0) && res.VoxelsIn > 0, "above ground: " + res);
        Balanced(w, before, "above ground");

        // Clipped at the pole; refused when it cannot fit (nothing changes).
        before = RegionLedger.Of(w);
        res = w.PasteRegion(r, 200, H - 5, new RegionPasteOptions());
        Require(res.Ok && res.ColumnsClipped == (sy - 5) * sx && res.Warnings.Count > 0, "clipped: " + res);
        Balanced(w, before, "clipped");
        ulong hash = w.RegionStateHash();
        var huge = new Region { SizeX = W + 1, SizeY = 1, Chem = r.Chem, Cols = new RegionColumn[W + 1], WorldZ = Z };
        for (int k = 0; k < huge.Cols.Length; k++) huge.Cols[k] = new RegionColumn();
        Require(!w.PasteRegion(huge, 0, 0).Ok, "a region wider than the world was pasted");
        Require(!w.PasteRegion(r, 0, 0, new RegionPasteOptions { Dz = Z }).Ok, "a region above the world's levels was pasted");
        Require(w.RegionStateHash() == hash, "a refused paste changed the world");

        // Paste back: a place overwritten by another region and then given its own region back is the
        // original world again, bit for bit, and goes on as the original would.
        var v = new World(7, 0, false) { AutoStrikes = false };
        for (int t = 0; t < 20; t++) v.Step();
        int ax = 40, ay = 50;
        var own = v.CopyRegion(ax, ay, 24, 20, 0, Z, false);
        var other = v.CopyRegion(150, 90, 20, 24, 0, Z, false);
        var twin = Twin(v);
        Require(v.RegionStateHash() == twin.RegionStateHash(), "twin");
        res = v.PasteRegion(other, ax, ay, new RegionPasteOptions { Rotation = 1, Bodies = false });
        Require(res.Ok && res.Columns == 480 && res.ColumnsSame < 480, "overwrite: " + res);
        Require(v.RegionStateHash() != twin.RegionStateHash(), "overwriting changed nothing");
        before = RegionLedger.Of(v);
        res = v.PasteRegion(own, ax, ay, new RegionPasteOptions { Bodies = false });
        Require(res.Ok, "paste back: " + res);
        Balanced(v, before, "paste back");
        Require(v.RegionStateHash() == twin.RegionStateHash(), "paste back: the world is not the original again");
        var same = v.PasteRegion(own, ax, ay, new RegionPasteOptions { Bodies = false });
        Require(same.Ok && same.ColumnsSame == same.Columns && same.VoxelsIn == 0, "pasting the same cells again moved something: " + same);
        bool goesOn = true;
        // (The support solver re-solves the pasted columns and their neighbours: their pressures come out
        // as a fresh solve gives them, not as the original's older passes left them — the trajectory of
        // terrain, matter and bodies is what must not change.)
        for (int t = 0; t < 200 && goesOn; t++) { v.Step(); twin.Step(); goesOn = v.StateHash() == twin.StateHash(); }
        if (!goesOn)
        {
            int diffP = 0, diffV = 0, diffC = 0, firstP = -1;
            for (int q = 0; q < N * Z; q++) { if (v.Pressure[q] != twin.Pressure[q]) { diffP++; if (firstP < 0) firstP = q; } if (v.Mat[q] != twin.Mat[q] || v.Units[q] != twin.Units[q] || v.Order[q] != twin.Order[q]) diffV++; }
            for (int s = 0; s < Chemistry.S; s++) for (int q = 0; q < N; q++) if (v.C[s][q] != twin.C[s][q]) diffC++;
            Console.WriteLine($"diverged at tick {v.Tick}: pressure {diffP} (first col {(firstP < 0 ? -1 : firstP / Z)} z {firstP % Z}), voxels {diffV}, loose {diffC}, state {v.StateHash() == twin.StateHash()}");
        }
        Require(goesOn, "paste back: the restored world went another way than the original");

        // Another chemistry: mapped to this world's nearest kinds, booked in its atoms.
        var u = new World(11, 0, false) { AutoStrikes = false };
        u.Step();
        before = RegionLedger.Of(u);
        res = u.PasteRegion(r, 70, 70, new RegionPasteOptions());
        Require(res.Ok && !res.Mapping.Identity && res.Mapping.Map[r.Chem.Gas] == u.Chem.Gas && res.Warnings.Count >= 2 && res.BodiesIn == 6, "another chemistry: " + res);
        Require(res.Mapping.Map.All(t => t >= 0 && t < Chemistry.S) && res.Mapping.Lines.Count == Chemistry.S, "another chemistry: mapping");
        Balanced(u, before, "another chemistry");
        u.CheckCellLists();
        for (int c = 0; c < N; c++)
            for (int z = 0; z < u.Height[c]; z++) Require(u.Mat[c * Z + z] < Chemistry.S + 2, "another chemistry: a block of no kind");
        before = RegionLedger.Of(u);
        for (int t = 0; t < 60; t++) u.Step();
        Require(Math.Abs(EnergyAudit.Drift(before.Energy, u.AuditEnergy())) < EnergyAudit.Tolerance(before.Energy, u.AuditEnergy()), "another chemistry: the ledger drifts after the paste");

        Console.WriteLine($"PASS regions: {sx}×{sy} columns with {r.Voxels} blocks and {r.Bodies.Count} bodies: binary and JSON round trip, paste/copy identical (also turned), " +
                          $"atoms, energy and water booked exactly, paste back restores the world bit for bit and it goes on the same; another chemistry: {res.Mapping.Changed} kinds mapped, none invented");
    }
}
