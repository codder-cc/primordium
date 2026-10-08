using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Primordium;

public enum PasteMode
{
    Replace,       // the copied levels of every column are taken out and the region's put in their place
    AboveGround,   // nothing is taken out: the region's blocks fill only the air above the ground there
}

public sealed class PasteOptions
{
    public int Rotation;                    // quarter turns clockwise, seen from above (north up)
    public PasteMode Mode = PasteMode.Replace;
    public bool Bodies = true;              // bring the region's bodies (Replace: and take away the bodies in the pasted levels)
    public int Dz;                          // shift in levels (up +)
}

// How the molecules of a region's chemistry are taken in this world's: identity for the same
// chemistry, else each kind to this world's nearest kind by its properties. Never a new molecule.
public sealed class RegionMapping
{
    public bool Identity = true;
    public readonly int[] Map = Enumerable.Range(0, Chemistry.S).ToArray();
    public readonly List<string> Lines = new();   // "Kazu → Mire (…)", in the language of the moment
    public int Changed;                          // kinds that became another kind (by name or properties)
}

public sealed class PasteResult
{
    public string Error;
    public bool Ok => Error == null;
    public int X, Y, SizeX, SizeY;   // the pasted rectangle (after rotation)
    public int Columns, ColumnsSame, ColumnsClipped, VoxelsIn, VoxelsOut, VoxelsClipped;
    public int BodiesIn, BodiesOut, BodiesSkipped, BodiesSeated;
    public readonly double[] AtomsIn = new double[Chemistry.ElementCount], AtomsOut = new double[Chemistry.ElementCount];
    public double EnergyIn, EnergyOut, WaterIn, WaterOut;
    public RegionMapping Mapping;
    public readonly List<string> Warnings = new();

    public override string ToString() => Ok
        ? Loc.T($"pasted {SizeX}×{SizeY} at ({X}, {Y}): {Columns} columns ({ColumnsSame} already the same), {VoxelsIn} blocks in, {VoxelsOut} out, bodies {BodiesIn} in, {BodiesOut} out",
                $"вставлено {SizeX}×{SizeY} в ({X}, {Y}): столбцов {Columns} (уже совпадали {ColumnsSame}), блоков внесено {VoxelsIn}, вынесено {VoxelsOut}, существ внесено {BodiesIn}, вынесено {BodiesOut}")
          + (Warnings.Count > 0 ? " — " + string.Join("; ", Warnings) : "")
        : Loc.T("cannot paste: ", "не вставить: ") + Error;
}

// Regions (Region.cs): copying a rectangle of columns over a range of levels and pasting it elsewhere,
// in this world or another. Between ticks only (SimRunner commands), never in the agent phase.
//
// Nothing is made for free. Pasting is the hand at work: what is taken out of the target (blocks,
// burials, the surface's loose matter, water, ice and snow, bodies) leaves the world and is booked as
// taken by the hand — atoms in HandInput, bond energy and the bodies' free energy in the energy
// ledger's `hand` flow, water in WaterHand —, and what the region brings is booked as brought the same
// way. The element, energy and water budgets stay exact. Geometry changes go through the same
// invalidation as any other (TerrainChanged, the strength caches, the support solver's dirty columns):
// a pasted overhang can cave in at the next structure pass, which is physics.
public sealed partial class World
{
    // ---- copy ----

    // A region of sx × sy columns from (x0, y0) (x wraps, y must lie in the world), levels z0 ≤ z < z1.
    public Region CopyRegion(int x0, int y0, int sx, int sy, int z0 = 0, int z1 = Z, bool bodies = true, string name = null)
    {
        if (sx < 1 || sy < 1 || sx > W || sy > H) throw new ArgumentException($"region size {sx}×{sy} must lie within 1…{W} × 1…{H}");
        if (y0 < 0 || y0 + sy > H) throw new ArgumentException($"rows {y0}…{y0 + sy - 1} leave the world (0…{H - 1})");
        x0 = ((x0 % W) + W) % W;
        z0 = Math.Clamp(z0, 0, Z - 1);
        z1 = Math.Clamp(z1, z0 + 1, Z);
        var r = new Region
        {
            Name = name ?? $"{sx}x{sy} ({x0}, {y0})", SizeX = sx, SizeY = sy, Z0 = z0, Z1 = z1, WorldW = W, WorldH = H, WorldZ = Z,
            SaveVersion = SaveVersion, SrcX = x0, SrcY = y0, SrcSeed = Seed, SrcTick = Tick, SavedAt = DateTime.UtcNow,
            Chem = RegionChemistry.Of(Chem, Seed), Cols = new RegionColumn[sx * sy],
        };
        for (int j = 0; j < sy; j++)
            for (int i = 0; i < sx; i++)
                r.Cols[j * sx + i] = CopyColumn((y0 + j) * W + (x0 + i) % W, z0, z1);
        if (bodies)
            foreach (var a in Agents)
            {
                if (a.Dead || a.Z < z0 || a.Z >= z1) continue;
                int dx = ((a.X - x0) % W + W) % W, dy = a.Y - y0;
                if (dx < sx && dy >= 0 && dy < sy) r.Bodies.Add(CopyBody(a, dx, dy));
            }
        return r;
    }

    RegionColumn CopyColumn(int c, int z0, int z1)
    {
        int h = Height[c], lo = Math.Max(z0, 2), hi = Math.Min(z1, h);
        var col = new RegionColumn { Height = h, Lo = lo };
        if (hi > lo)
        {
            int n = hi - lo, b = c * Z + lo;
            col.Mat = Mat.AsSpan(b, n).ToArray(); col.Order = Order.AsSpan(b, n).ToArray();
            col.Units = Units.AsSpan(b, n).ToArray(); col.Pressure = Pressure.AsSpan(b, n).ToArray();
            for (int z = lo; z < hi; z++)
                if (Mixed(c * Z + z, out var counts)) col.Mix.Add(new RegionMix { Z = z, Counts = (ushort[])counts.Clone() });
        }
        for (int z = z0; z < z1; z++)
        {
            if (!BurialOf(c * Z + z, out var burial)) continue;
            var rb = new RegionBurial { Z = z, Order = burial.Order, Pressure = burial.Pressure };
            for (int s = 0; s < Chemistry.S; s++) rb.Matter[s] = burial.Matter[s].Raw;
            col.Burials.Add(rb);
        }
        col.Top = z0 <= h && h < z1;
        if (col.Top)
        {
            col.Loose = new long[Chemistry.S];
            for (int s = 0; s < Chemistry.S; s++) col.Loose[s] = C[s][c].Raw;
            col.LooseVolume = LooseVolume[c]; col.Water = Water[c]; col.Ice = Ice[c]; col.Snow = Snow[c];
        }
        return col;
    }

    // A body's whole state in the save's own body record (genome, protection, then SyncAgent).
    RegionBody CopyBody(Agent a, int dx, int dy)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            var s = new Writer(bw);
            int len = a.G.Length;
            s.V(ref len); s.A<byte>(a.G); s.A<byte>(a.Prot);
            SyncAgent(s, a);
        }
        return new RegionBody
        {
            X = dx, Y = dy, Z = a.Z, SrcId = a.Id, Lineage = a.Lineage, Gen = a.Gen, Design = DesignOf(a.Lineage),
            Energy = a.Energy + a.HeatHeld, Molecules = a.InvTotal, State = ms.ToArray(),
        };
    }

    Agent ReadBody(RegionBody b, int version, long id, long lineage)
    {
        using var ms = new MemoryStream(b.State);
        using var br = new BinaryReader(ms, Encoding.UTF8);
        var s = new Reader(br) { Version = version };
        int len = 0;
        s.V(ref len);
        if (len < 1 || len > 1 << 20) throw new InvalidDataException("body record is damaged");
        var g = new byte[len]; var p = new byte[len];
        s.A<byte>(g); s.A<byte>(p);
        var a = new Agent(id, lineage, b.Gen, g, p);
        SyncAgent(s, a);
        return a;
    }

    // ---- chemistry ----

    // How a region's molecules are taken here. The same chemistry: as they are. Another: each kind to the
    // nearest kind of this world by ground/excited state, mass, bond energy, bond strength, packing and
    // size (the region's gas to this world's gas: it is the air's reservoir).
    public RegionMapping MapChemistry(RegionChemistry src)
    {
        var m = new RegionMapping();
        if (src == null) throw new ArgumentNullException(nameof(src));
        if (src.SameAs(Chem)) return m;
        m.Identity = false;
        float Spread(Func<int, float> f) { float lo = float.MaxValue, hi = float.MinValue; for (int s = 0; s < Chemistry.S; s++) { lo = Math.Min(lo, f(s)); hi = Math.Max(hi, f(s)); } return Math.Max(1e-3f, hi - lo); }
        float sMass = Spread(s => MathF.Log(Chem.Mass[s])), sE = Spread(s => Chem.E[s]), sBond = Spread(s => Chem.Bond[s]), sPack = Spread(s => Chem.Packing[s]), sSize = Spread(s => Chem.AtomCount(s));
        for (int s = 0; s < Chemistry.S; s++)
        {
            int atoms = 0;
            for (int e = 0; e < Chemistry.ElementCount; e++) atoms += src.Atoms[s * Chemistry.ElementCount + e];
            int best = Chem.Gas;
            if (s != src.Gas)
            {
                float bestD = float.MaxValue;
                for (int t = 0; t < Chemistry.S; t++)
                {
                    if (t == Chem.Gas) continue;
                    float d = (s % 2 != t % 2 ? 1f : 0f)
                        + MathF.Abs(MathF.Log(Math.Max(1e-3f, src.Mass[s])) - MathF.Log(Chem.Mass[t])) / sMass
                        + MathF.Abs(src.E[s] - Chem.E[t]) / sE
                        + 1.5f * MathF.Abs(src.Bond[s] - Chem.Bond[t]) / sBond
                        + 0.5f * MathF.Abs(src.Packing[s] - Chem.Packing[t]) / sPack
                        + 0.3f * MathF.Abs(atoms - Chem.AtomCount(t)) / sSize;
                    if (d < bestD) { bestD = d; best = t; }
                }
            }
            m.Map[s] = best;
            bool same = src.NameEn[s] == Chem.NameEn[best] && src.E[s] == Chem.E[best] && src.Mass[s] == Chem.Mass[best];
            if (!same) m.Changed++;
            m.Lines.Add(Loc.T($"{src.NameEn[s]} (mass {src.Mass[s]:0.##}, energy {src.E[s]}, bond {src.Bond[s]:0.##}) → {Chem.NameEn[best]} (mass {Chem.Mass[best]:0.##}, energy {Chem.E[best]}, bond {Chem.Bond[best]:0.##})",
                              $"{src.NameRu[s]} (масса {src.Mass[s]:0.##}, энергия {src.E[s]}, связь {src.Bond[s]:0.##}) → {Chem.NameRu[best]} (масса {Chem.Mass[best]:0.##}, энергия {Chem.E[best]}, связь {Chem.Bond[best]:0.##})"));
        }
        return m;
    }

    // ---- paste ----

    // What a paste moved, summed exactly (whole molecules and Qty units per kind), booked at the end.
    sealed class PasteBook
    {
        public readonly long[] WholeIn = new long[Chemistry.S], WholeOut = new long[Chemistry.S];
        public readonly long[] FracIn = new long[Chemistry.S], FracOut = new long[Chemistry.S];   // Qty.Raw
        public double BodyIn, BodyOut, WaterIn, WaterOut;
    }

    // Where region column (i, j) of an sx × sy region lands after k quarter turns clockwise.
    static (int i, int j) Rotate(int i, int j, int sx, int sy, int k) => k switch
    {
        1 => (sy - 1 - j, i),
        2 => (sx - 1 - i, sy - 1 - j),
        3 => (j, sx - 1 - i),
        _ => (i, j),
    };

    // Paste a region with its corner (after rotation) at (x, y): x wraps; rows beyond the poles are
    // clipped, so are levels beyond the world's height. Fails without touching anything if the region
    // does not fit the world at all.
    public PasteResult PasteRegion(Region r, int x, int y, PasteOptions o = null)
    {
        o ??= new PasteOptions();
        var res = new PasteResult();
        int rot = ((o.Rotation % 4) + 4) % 4;
        int rw = rot % 2 == 0 ? r.SizeX : r.SizeY, rh = rot % 2 == 0 ? r.SizeY : r.SizeX;
        x = ((x % W) + W) % W;
        res.X = x; res.Y = y; res.SizeX = rw; res.SizeY = rh;
        if (r.Cols == null || r.Cols.Length != r.SizeX * r.SizeY || r.Chem == null) { res.Error = Loc.T("the region is incomplete", "участок неполный"); return res; }
        if (rw > W || rh > H) { res.Error = Loc.T($"the region ({rw}×{rh}) is larger than this world ({W}×{H})", $"участок ({rw}×{rh}) больше этого мира ({W}×{H})"); return res; }
        if (y >= H || y + rh <= 0) { res.Error = Loc.T($"rows {y}…{y + rh - 1} lie outside the world (0…{H - 1})", $"строки {y}…{y + rh - 1} вне мира (0…{H - 1})"); return res; }
        if (r.Z0 + o.Dz >= Z || r.Z1 + o.Dz <= 2) { res.Error = Loc.T($"levels {r.Z0 + o.Dz}…{r.Z1 + o.Dz - 1} lie outside this world (2…{Z - 1})", $"уровни {r.Z0 + o.Dz}…{r.Z1 + o.Dz - 1} вне этого мира (2…{Z - 1})"); return res; }
        if (r.WorldZ != Z) res.Warnings.Add(Loc.T($"the region comes from a world {r.WorldZ} levels tall, this one has {Z}: what lies above is clipped",
                                                   $"участок из мира высотой {r.WorldZ} уровней, в этом {Z}: всё выше обрезается"));
        var map = res.Mapping = MapChemistry(r.Chem);
        if (!map.Identity)
            res.Warnings.Add(Loc.T($"another chemistry (seed {r.Chem.Seed}, here {Seed}): {map.Changed} of {Chemistry.S} molecule kinds taken as this world's nearest",
                                   $"другая химия (seed {r.Chem.Seed}, здесь {Seed}): {map.Changed} из {Chemistry.S} видов молекул взяты как ближайшие здешние"));
        bool bodies = o.Bodies;
        if (bodies && r.Bodies.Count > 0 && r.SaveVersion > SaveVersion)
        {
            res.Warnings.Add(Loc.T($"its bodies were written by a newer build (format {r.SaveVersion}): left out", $"существа записаны более новой сборкой (формат {r.SaveVersion}): не вносятся"));
            bodies = false;
        }
        if (bodies && !map.Identity && r.Bodies.Count > 0)
            res.Warnings.Add(Loc.T("genomes name molecules by number: in this chemistry their genes mean other kinds", "геномы называют молекулы по номеру: в этой химии их гены означают другие виды"));
        var book = new PasteBook();
        pastedColumns.Clear();
        int zlo = r.Z0 + o.Dz, zhi = r.Z1 + o.Dz;
        bool InRect(int cx, int cy) { int dx = ((cx - x) % W + W) % W, dy = cy - y; return dx < rw && dy >= 0 && dy < rh; }

        // 1. The bodies standing in the pasted levels go with the hand (Replace with bodies).
        if (bodies && o.Mode == PasteMode.Replace)
            foreach (var a in Agents)
                if (!a.Dead && a.Z >= zlo && a.Z < zhi && InRect(a.X, a.Y)) { HandTake(a, book); res.BodiesOut++; }

        // 2. The columns.
        var oldHeight = new Dictionary<int, int>();
        for (int j = 0; j < rh; j++)
        {
            int ty = y + j;
            if (ty < 0 || ty >= H) { res.ColumnsClipped += rw; continue; }
            for (int i = 0; i < rw; i++)
            {
                var (si, sj) = rot switch { 1 => (j, r.SizeY - 1 - i), 2 => (r.SizeX - 1 - i, r.SizeY - 1 - j), 3 => (r.SizeX - 1 - j, i), _ => (i, j) };
                int tc = ty * W + (x + i) % W;
                oldHeight[tc] = Height[tc];
                res.Columns++;
                if (o.Mode == PasteMode.Replace && map.Identity && o.Dz == 0 && SameColumn(r.Col(si, sj), CopyColumn(tc, r.Z0, r.Z1))) { res.ColumnsSame++; continue; }
                PasteColumn(r, r.Col(si, sj), tc, o, map, book, res);
            }
        }

        // 3. The region's bodies, new bodies with new ids (the same lineage in the same world).
        if (bodies)
        {
            var lineages = new Dictionary<long, long>();
            bool sameWorld = r.SrcSeed == Seed && map.Identity;
            foreach (var b in r.Bodies)
            {
                var (ti, tj) = Rotate(b.X, b.Y, r.SizeX, r.SizeY, rot);
                int ty = y + tj, z = b.Z + o.Dz;
                if (ty < 0 || ty >= H || z < 2 || z >= Z - 1) { res.BodiesSkipped++; continue; }
                int tc = ty * W + (x + ti) % W;
                long id = NewId();
                long lineage = sameWorld ? b.Lineage : lineages.TryGetValue(b.Lineage, out long l) ? l : lineages[b.Lineage] = id;
                Agent a;
                try { a = ReadBody(b, r.SaveVersion, id, lineage); }
                catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException) { res.BodiesSkipped++; res.Warnings.Add(Loc.T("a body record could not be read: ", "запись существа не читается: ") + e.Message); continue; }
                if (!map.Identity) RemapBody(a, map.Map, r.Chem);
                for (int k = 0; k < rot; k++) (a.Vx, a.Vy) = (-a.Vy, a.Vx);
                float level = z + a.Lift;
                while (z < Z - 2 && IsSolid(tc, z)) z++;
                a.Dead = false; a.Cause = 0; a.Cells = 1; a.Foot[0] = tc; a.Z = z;
                SetLift(a, tc, z, level);
                Place(a, tc);
                Agents.Add(a);
                EvoRegister(a);   // a founder here: a root of the family tree
                if (b.Design != null && !DesignedLineages.ContainsKey(lineage)) DesignedLineages[lineage] = b.Design;
                for (int s = 0; s < Chemistry.S; s++) { book.WholeIn[s] += a.Inv[s]; book.FracIn[s] += a.Pend[s].Raw; }
                for (int k = 0; k < a.EnzN; k++) book.FracIn[a.Enz[k].Material] += a.Enz[k].Matter.Raw;
                book.BodyIn += a.Energy + a.HeatHeld;
                res.BodiesIn++;
            }
        }

        // 4. Bodies that were already there and stay: a body the new ground has filled in is lifted onto it
        // (it falls from there as any body would, SettleAgent); big bodies draw their feet back and
        // spread anew when they live.
        foreach (int tc in oldHeight.Keys)
        {
            if (!pastedColumns.Contains(tc)) continue;
            if (Big[tc] is { Dead: false } big && big.Y * W + big.X != tc) DropFoot(big, tc);
            for (var a = Head[tc]; a != null; a = a.NextInCell)
            {
                if (a.Cells > 1) ReleaseFoot(a);
                if (!IsSolid(tc, a.Z)) continue;
                float level = a.Z + a.Lift;
                int z = a.Z;
                while (z < Z - 2 && IsSolid(tc, z)) z++;
                a.Z = z;
                SetLift(a, tc, z, level);
                res.BodiesSeated++;
            }
        }

        pastedColumns.Clear();

        // 5. The books: atoms, bond energy and free energy, water.
        var flows = Flows;
        for (int s = 0; s < Chemistry.S; s++)
        {
            double nIn = book.WholeIn[s] + book.FracIn[s] / Qty.One, nOut = book.WholeOut[s] + book.FracOut[s] / Qty.One;
            if (nIn == 0 && nOut == 0) continue;
            for (int e = 0; e < Chemistry.ElementCount; e++)
            {
                res.AtomsIn[e] += nIn * Chem.Atoms[s, e];
                res.AtomsOut[e] += nOut * Chem.Atoms[s, e];
            }
            res.EnergyIn += nIn * Chem.E[s];
            res.EnergyOut += nOut * Chem.E[s];
        }
        for (int e = 0; e < Chemistry.ElementCount; e++) HandInput[e] += res.AtomsIn[e] - res.AtomsOut[e];
        res.EnergyIn += book.BodyIn; res.EnergyOut += book.BodyOut;
        flows[FHand] += res.EnergyIn - res.EnergyOut;
        HandEnergy += book.BodyIn - book.BodyOut;
        res.WaterIn = book.WaterIn; res.WaterOut = book.WaterOut;
        WaterHand += book.WaterIn - book.WaterOut;
        if (res.ColumnsClipped > 0) res.Warnings.Add(Loc.T($"{res.ColumnsClipped} columns beyond the poles clipped", $"{res.ColumnsClipped} столбцов за полюсами обрезано"));
        if (res.VoxelsClipped > 0) res.Warnings.Add(Loc.T($"{res.VoxelsClipped} blocks beyond the world's levels clipped", $"{res.VoxelsClipped} блоков за пределами уровней мира обрезано"));
        if (res.BodiesSkipped > 0) res.Warnings.Add(Loc.T($"{res.BodiesSkipped} bodies left out (outside the world)", $"{res.BodiesSkipped} существ не внесено (вне мира)"));

        // 6. The chronicle.
        string where = $"({x}, {y})", size = $"{rw}×{rh}";
        Add(EvType.Player, Loc.Both(
            $"region “{r.Name}” {size} pasted at {where}{(rot > 0 ? $", turned {rot * 90}°" : "")}: {res.VoxelsIn} blocks in, {res.VoxelsOut} out, bodies {res.BodiesIn} in, {res.BodiesOut} out{(map.Identity ? "" : $", chemistry of seed {r.Chem.Seed} mapped")}",
            $"участок «{r.Name}» {size} вставлен в {where}{(rot > 0 ? $", повёрнут на {rot * 90}°" : "")}: блоков внесено {res.VoxelsIn}, вынесено {res.VoxelsOut}, существ внесено {res.BodiesIn}, вынесено {res.BodiesOut}{(map.Identity ? "" : $", химия seed {r.Chem.Seed} сопоставлена")}"),
            null, res.VoxelsIn, false, null, (x + rw / 2) % W, Math.Clamp(y + rh / 2, 0, H - 1));
        return res;
    }

    // Columns PasteColumn changed in the current paste (step 4 looks only at those).
    readonly HashSet<int> pastedColumns = new();

    // The same cells (as SameCells compares them) in a column of a region and a fresh copy of the target.
    static bool SameColumn(RegionColumn a, RegionColumn b)
    {
        if (a.Height != b.Height || a.Lo != b.Lo || a.Mat.Length != b.Mat.Length || a.Top != b.Top) return false;
        if (!a.Mat.AsSpan().SequenceEqual(b.Mat) || !a.Order.AsSpan().SequenceEqual(b.Order) || !a.Units.AsSpan().SequenceEqual(b.Units) || !a.Pressure.AsSpan().SequenceEqual(b.Pressure)) return false;
        if (a.Mix.Count != b.Mix.Count || a.Burials.Count != b.Burials.Count) return false;
        for (int k = 0; k < a.Mix.Count; k++) if (a.Mix[k].Z != b.Mix[k].Z || !a.Mix[k].Counts.AsSpan().SequenceEqual(b.Mix[k].Counts)) return false;
        for (int k = 0; k < a.Burials.Count; k++)
        {
            RegionBurial p = a.Burials[k], q = b.Burials[k];
            if (p.Z != q.Z || p.Order != q.Order || p.Pressure != q.Pressure || !p.Matter.AsSpan().SequenceEqual(q.Matter)) return false;
        }
        return !a.Top || (a.Loose.AsSpan().SequenceEqual(b.Loose) && a.Water == b.Water && a.Ice == b.Ice && a.Snow == b.Snow && a.LooseVolume == b.LooseVolume);
    }

    void PasteColumn(Region r, RegionColumn col, int tc, PasteOptions o, RegionMapping map, PasteBook book, PasteResult res)
    {
        int dz = o.Dz, oldH = Height[tc], top = -1;
        bool replace = o.Mode == PasteMode.Replace, changed = false;
        int face = biteFace[tc];
        byte faceMat = face >= 0 ? Mat[face] : (byte)0, faceOrder = face >= 0 ? Order[face] : (byte)0;
        ushort faceUnits = face >= 0 ? Units[face] : (ushort)0;
        int lo = Math.Max(r.Z0 + dz, 0), hi = Math.Min(r.Z1 + dz, Z);
        if (replace)
        {
            // Out with the hand: the blocks and burials of the pasted levels, and the surface if the region brings one.
            for (int z = lo; z < hi; z++)
            {
                int v = tc * Z + z;
                if (BurialOf(v, out var burial))
                {
                    for (int s = 0; s < Chemistry.S; s++) book.FracOut[s] += burial.Matter[s].Raw;
                    Buried.TryRemove(v, out _);
                    sparse[v] &= unchecked((byte)~HasBurial);
                    changed = true;
                }
                if (z < 2 || z >= oldH || Mat[v] < 2) continue;
                if (Mixed(v, out var counts)) { for (int s = 0; s < Chemistry.S; s++) book.WholeOut[s] += counts[s]; }
                else book.WholeOut[Mat[v] - 2] += Units[v];
                TakeMixture(v);
                Mat[v] = 0; Units[v] = 0; Order[v] = 0; Pressure[v] = 0;
                compressionCache[v] = cohesionCache[v] = 0;
                res.VoxelsOut++;
                changed = true;
            }
            if (col.Top)
            {
                for (int s = 0; s < Chemistry.S; s++) { book.FracOut[s] += C[s][tc].Raw; C[s][tc] = Qty.Zero; }
                book.WaterOut += (double)Water[tc] + Ice[tc] + Snow[tc];
                Water[tc] = Ice[tc] = Snow[tc] = 0;
                LooseVolume[tc] = 0;
                changed = true;
            }
        }
        // In: the region's blocks (above the ground only, if so asked), mapped to this chemistry.
        int floor = replace ? 2 : oldH;
        Qty[] spill = null;
        int mixAt = 0;
        for (int k = 0; k < col.Mat.Length; k++)
        {
            int zr = col.Lo + k;
            while (mixAt < col.Mix.Count && col.Mix[mixAt].Z < zr) mixAt++;
            var counts = mixAt < col.Mix.Count && col.Mix[mixAt].Z == zr ? col.Mix[mixAt].Counts : null;
            byte m = col.Mat[k];
            if (m < 2) continue;
            int tz = zr + dz;
            if (tz < 2 || tz >= Z) { res.VoxelsClipped++; continue; }
            int v = tc * Z + tz;
            if (tz < floor || Mat[v] != Chemistry.Air) continue;   // above the ground only: what is there stays (nothing brought)
            if (!LayBlock(v, m, col.Units[k], col.Order[k], counts, map, book, ref spill)) continue;
            Pressure[v] = col.Pressure[k];
            top = Math.Max(top, tz);
            res.VoxelsIn++;
            changed = true;
        }
        foreach (var b in col.Burials)
        {
            int tz = b.Z + dz;
            if (tz < 0 || tz >= Z) continue;
            if (!replace && tz < oldH - 1) continue;   // above the ground only (a burial lies in its floor block)
            int v = tc * Z + tz;
            bool fresh = !BurialOf(v, out _);
            var dest = BurialAt(v);
            for (int s = 0; s < Chemistry.S; s++)
            {
                if (b.Matter[s] == 0) continue;
                dest.Matter[map.Map[s]] += Qty.FromRaw(b.Matter[s]);
                book.FracIn[map.Map[s]] += b.Matter[s];
            }
            if (fresh) { dest.Order = b.Order; dest.Pressure = b.Pressure; }
            else dest.Order = Math.Max(dest.Order, b.Order);
            MatterChanged(v);
            changed = true;
        }
        // The surface: its loose matter, water, ice and snow (above the ground only: if it lies above it).
        if (col.Top && (replace || col.Height + dz >= oldH))
        {
            for (int s = 0; s < Chemistry.S; s++)
            {
                if (col.Loose[s] == 0) continue;
                C[map.Map[s]][tc] += Qty.FromRaw(col.Loose[s]);
                book.FracIn[map.Map[s]] += col.Loose[s];
            }
            double was = (double)Water[tc] + Ice[tc] + Snow[tc];
            Water[tc] += col.Water; Ice[tc] += col.Ice; Snow[tc] += col.Snow;
            book.WaterIn += (double)Water[tc] + Ice[tc] + Snow[tc] - was;   // what the floats actually took in
            LooseVolume[tc] += col.LooseVolume;
            changed = true;
        }
        if (spill != null)
            for (int s = 0; s < Chemistry.S; s++)
                if (spill[s] != 0) { C[s][tc] += spill[s]; book.FracIn[s] += spill[s].Raw; }
        if (!changed) return;
        int h = Math.Max(oldH, top + 1);
        while (h > 0 && Mat[tc * Z + h - 1] == Chemistry.Air) h--;
        Height[tc] = h;
        TerrainChanged(tc);   // versions, cavities, gas flow, the support solver for it and its neighbours
        pastedColumns.Add(tc);
        // A face being worked: its progress stays only if it is still the same block.
        if (face >= 0 && (Mat[face] != faceMat || Units[face] != faceUnits || Order[face] != faceOrder)) { biteFace[tc] = -1; Bite[tc] = 0; }
    }

    // Lay one block of the region into an empty voxel. In another chemistry its molecules become this
    // world's nearest kinds; what then does not fit the voxel by volume lies loose on the column (spill).
    bool LayBlock(int v, byte m, ushort units, byte order, ushort[] counts, RegionMapping map, PasteBook book, ref Qty[] spill)
    {
        compressionCache[v] = cohesionCache[v] = 0;
        if (map.Identity)
        {
            Mat[v] = m; Units[v] = units; Order[v] = order;
            if (counts != null) { SetMixture(v, (ushort[])counts.Clone()); for (int s = 0; s < Chemistry.S; s++) book.WholeIn[s] += counts[s]; }
            else book.WholeIn[m - 2] += units;
            return true;
        }
        var mc = new int[Chemistry.S];
        if (counts != null) for (int s = 0; s < Chemistry.S; s++) mc[map.Map[s]] += counts[s];
        else mc[map.Map[m - 2]] += units;
        float vol = 0;
        for (int t = 0; t < Chemistry.S; t++) if (mc[t] > 0) vol += mc[t] * PackedVolume(t, order);
        if (vol > P.VoxelSpace)
        {
            float keep = P.VoxelSpace / vol;
            spill ??= new Qty[Chemistry.S];
            for (int t = 0; t < Chemistry.S; t++)
            {
                if (mc[t] == 0) continue;
                int k = (int)(mc[t] * keep);
                spill[t] += mc[t] - k;
                mc[t] = k;
            }
        }
        int total = 0, kinds = 0, only = 0;
        for (int t = 0; t < Chemistry.S; t++) if (mc[t] > 0) { total += mc[t]; kinds++; only = t; }
        if (total == 0) return false;
        for (int t = 0; t < Chemistry.S; t++) book.WholeIn[t] += mc[t];
        Order[v] = order;
        Units[v] = (ushort)total;
        if (counts == null && kinds == 1) { Mat[v] = Chem.BuiltMat[only]; return true; }
        var block = new ushort[Chemistry.S];
        for (int t = 0; t < Chemistry.S; t++) block[t] = (ushort)mc[t];
        Mat[v] = Chem.BuiltMat[Dominant(block)];
        SetMixture(v, block);
        return true;
    }

    // A body of another chemistry: its molecules, partly absorbed matter and protein substrate become
    // this world's nearest kinds (their count kept), its mass and room follow; proteins target the
    // mapped kinds.
    void RemapBody(Agent a, int[] map, RegionChemistry src)
    {
        var inv = (int[])a.Inv.Clone();
        var pend = (Qty[])a.Pend.Clone();
        Array.Clear(a.Inv); Array.Clear(a.Pend);
        a.InvTotal = a.Unstable = a.Solids = 0;
        double mass = a.Mass, volume = a.Volume;
        float SrcBodyVolume(int s) => s == src.Gas ? src.Volume[s] * P.GasExpand : src.Volume[s];
        for (int s = 0; s < Chemistry.S; s++)
        {
            int t = map[s];
            if (inv[s] > 0)
            {
                a.Inv[t] += inv[s]; a.InvTotal += inv[s];
                mass += inv[s] * ((double)Chem.Mass[t] - src.Mass[s]);
                volume += inv[s] * ((double)Chem.BodyVolume[t] - SrcBodyVolume(s));
            }
            if (pend[s] != 0)
            {
                a.Pend[t] += pend[s];
                mass += pend[s].D * ((double)Chem.Mass[t] - src.Mass[s]);
                volume += pend[s].D * ((double)Chem.BodyVolume[t] - SrcBodyVolume(s));
            }
        }
        for (int s = 0; s < Chemistry.S; s++)
        {
            if (Chem.SplitExo[s]) a.Unstable += a.Inv[s];
            if (Chem.Solid[s]) a.Solids += a.Inv[s];
        }
        for (int k = 0; k < a.EnzN; k++)
        {
            ref var e = ref a.Enz[k];
            int t = map[e.Material % Chemistry.S];
            mass += e.Matter.D * ((double)Chem.Mass[t] - src.Mass[e.Material % Chemistry.S]);
            volume += e.Matter.D * ((double)Chem.Volume[t] - src.Volume[e.Material % Chemistry.S]);
            e.Material = (byte)t; e.A = (byte)map[e.A % Chemistry.S]; e.B = (byte)map[e.B % Chemistry.S];
        }
        a.Mass = (float)Math.Max(1e-3, mass);
        a.Volume = (float)Math.Max(1e-3, volume);
    }

    // The hand takes a body away whole: its matter and its energy leave the world (booked by the caller).
    // Bookkeeping as for a death by the hand, but nothing stays behind.
    void HandTake(Agent a, PasteBook book)
    {
        int cell = a.Y * W + a.X;
        for (int s = 0; s < Chemistry.S; s++) { book.WholeOut[s] += a.Inv[s]; book.FracOut[s] += a.Pend[s].Raw; }
        for (int k = 0; k < a.EnzN; k++) book.FracOut[a.Enz[k].Material] += a.Enz[k].Matter.Raw;
        book.BodyOut += a.Energy + a.HeatHeld;
        foreach (var b in a.Links) Unlink(b, a);
        a.Links.Clear();
        a.Target = a.LinkWant = null;
        int cells = a.Cells;
        ReleaseFoot(a); a.Cells = cells;
        Unplace(a, cell);
        a.Dead = true; a.Cause = CauseHand;
        if (a.Tracked) ChronDeath(a);
        DeathsHand++;
        MarkDirty(cell);
    }
}
