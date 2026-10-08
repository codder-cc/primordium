using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Primordium;

// A piece of a world cut out to be pasted elsewhere (World.Region.cs): a rectangle of columns over a
// range of levels, with everything the save keeps of them — every block (kind, molecule count, lattice
// order, pressure, mixture counts), burials, the loose matter, water, ice and snow on the surface when
// the surface lies in the range, and optionally the bodies standing in it (their whole state, in the
// save's own body record). It is plain data: no reference to the world it came from. It knows the
// chemistry it was made in (RegionChemistry): a world with another chemistry maps its molecules to its
// own nearest ones (World.MapChemistry), never inventing any.
//
// Files (user://regions in the game): binary `.region` — an uncompressed header (RegionInfo, for the
// list) and a Brotli body, little-endian; or JSON `.region.json` for exchange and reading (the same
// fields, body state as base64). Both load with Region.Load.

// The chemistry a region was made in: the seed and what each molecule kind is (formula, mass, bond
// energy, bond strength, packing, volume, colour, names).
public sealed class RegionChemistry
{
    public int Seed, Gas;
    public int[] Atoms = new int[Chemistry.S * Chemistry.ElementCount];
    public int[] E = new int[Chemistry.S];
    public float[] Mass = new float[Chemistry.S], Bond = new float[Chemistry.S], Packing = new float[Chemistry.S], Volume = new float[Chemistry.S];
    public float[] AtomicMass = new float[Chemistry.ElementCount];
    public float[] Colour = new float[Chemistry.S * 3];
    public string[] NameEn = new string[Chemistry.S], NameRu = new string[Chemistry.S];

    public static RegionChemistry Of(Chemistry c, int seed)
    {
        var r = new RegionChemistry { Seed = seed, Gas = c.Gas };
        for (int s = 0; s < Chemistry.S; s++)
        {
            for (int e = 0; e < Chemistry.ElementCount; e++) r.Atoms[s * Chemistry.ElementCount + e] = c.Atoms[s, e];
            r.E[s] = c.E[s]; r.Mass[s] = c.Mass[s]; r.Bond[s] = c.Bond[s]; r.Packing[s] = c.Packing[s]; r.Volume[s] = c.Volume[s];
            r.Colour[s * 3] = c.Col[s].R; r.Colour[s * 3 + 1] = c.Col[s].G; r.Colour[s * 3 + 2] = c.Col[s].B;
            r.NameEn[s] = c.NameEn[s]; r.NameRu[s] = c.NameRu[s];
        }
        for (int e = 0; e < Chemistry.ElementCount; e++) r.AtomicMass[e] = c.AtomicMass[e];
        return r;
    }

    // The same molecules as this chemistry (formulas, energies, masses, bonds): a region of it needs no mapping.
    public bool SameAs(Chemistry c)
    {
        if (Gas != c.Gas) return false;
        for (int e = 0; e < Chemistry.ElementCount; e++) if (AtomicMass[e] != c.AtomicMass[e]) return false;
        for (int s = 0; s < Chemistry.S; s++)
        {
            if (E[s] != c.E[s] || Mass[s] != c.Mass[s] || Bond[s] != c.Bond[s] || Packing[s] != c.Packing[s]) return false;
            for (int e = 0; e < Chemistry.ElementCount; e++) if (Atoms[s * Chemistry.ElementCount + e] != c.Atoms[s, e]) return false;
        }
        return true;
    }

    public string Name(int s) => Loc.En ? NameEn[s] : NameRu[s];

    internal void Write(BinaryWriter w)
    {
        w.Write(Seed); w.Write(Gas);
        foreach (var x in Atoms) w.Write(x);
        foreach (var x in E) w.Write(x);
        foreach (var a in new[] { Mass, Bond, Packing, Volume, AtomicMass, Colour }) foreach (var x in a) w.Write(x);
        foreach (var x in NameEn) w.Write(x ?? "");
        foreach (var x in NameRu) w.Write(x ?? "");
    }

    internal static RegionChemistry Read(BinaryReader r)
    {
        var c = new RegionChemistry { Seed = r.ReadInt32(), Gas = r.ReadInt32() };
        for (int k = 0; k < c.Atoms.Length; k++) c.Atoms[k] = r.ReadInt32();
        for (int k = 0; k < c.E.Length; k++) c.E[k] = r.ReadInt32();
        foreach (var a in new[] { c.Mass, c.Bond, c.Packing, c.Volume, c.AtomicMass, c.Colour }) for (int k = 0; k < a.Length; k++) a[k] = r.ReadSingle();
        for (int k = 0; k < Chemistry.S; k++) c.NameEn[k] = r.ReadString();
        for (int k = 0; k < Chemistry.S; k++) c.NameRu[k] = r.ReadString();
        return c;
    }
}

// One column of a region: the levels Lo … Lo + Mat.Length − 1 as the world has them (Mat − 2 is the
// dominant molecule kind, 0 is void), the mixtures among them, burials, and its surface if it lay in the
// copied range.
public sealed class RegionColumn
{
    public int Height;   // the source column's height (absolute level of its top + 1)
    public int Lo;       // first level stored
    public byte[] Mat = Array.Empty<byte>(), Order = Array.Empty<byte>();
    public ushort[] Units = Array.Empty<ushort>();
    public float[] Pressure = Array.Empty<float>();
    public List<RegionMix> Mix = new();
    public List<RegionBurial> Burials = new();
    public bool Top;     // its surface lay in the range: the loose matter, water, ice and snow come with it
    public long[] Loose; // Qty.Raw per molecule kind (only with Top)
    public float LooseVolume, Water, Ice, Snow;

    public int Hi => Lo + Mat.Length;
    public int Solid { get { int n = 0; foreach (var m in Mat) if (m >= 2) n++; return n; } }
}

public sealed class RegionMix
{
    public int Z;
    public ushort[] Counts = new ushort[Chemistry.S];
}

public sealed class RegionBurial
{
    public int Z;
    public long[] Matter = new long[Chemistry.S];   // Qty.Raw
    public float Order, Pressure;
}

// A body in a region: where it stood (relative to the region's corner, before rotation), what it was,
// and its whole state as the save writes it (World.SyncAgent, format World.SaveVersion of the region).
public sealed class RegionBody
{
    public int X, Y, Z;
    public long SrcId, Lineage;
    public int Gen;
    public string Design;   // the name of the player design of its lineage, if any
    public double Energy;   // its free energy plus the heat it held (for the list; the state has both)
    public int Molecules;   // whole molecules it held (for the list)
    public byte[] State = Array.Empty<byte>();
}

// What a region file says about itself without being decompressed (for the list of regions).
public sealed class RegionInfo
{
    public int Format;
    public string Name, Note;
    public int SizeX, SizeY, Z0, Z1, SrcSeed, Bodies;
    public long SrcTick, Voxels;
    public DateTime SavedAt;   // UTC
}

public sealed class Region
{
    public const int FormatVersion = 1;
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("PRIMREGN");
    const int EndMarker = 0x21444E45;   // "END!"
    public const string Extension = ".region", JsonExtension = ".region.json";

    public string Name = "", Note = "";
    public int SizeX, SizeY;          // columns
    public int Z0, Z1;                // levels copied: Z0 ≤ z < Z1
    public int WorldW, WorldH, WorldZ;
    public int SaveVersion;           // of the body states (World.SaveVersion when copied)
    public int SrcX, SrcY, SrcSeed;
    public long SrcTick;
    public DateTime SavedAt = DateTime.UtcNow;
    public RegionChemistry Chem;
    public RegionColumn[] Cols = Array.Empty<RegionColumn>();   // SizeX × SizeY, row by row
    public List<RegionBody> Bodies = new();

    public RegionColumn Col(int x, int y) => Cols[y * SizeX + x];
    public long Voxels { get { long n = 0; foreach (var c in Cols) n += c.Solid; return n; } }
    public bool FullHeight => Z0 <= 0 && Z1 >= WorldZ;

    public RegionInfo Info => new()
    {
        Format = FormatVersion, Name = Name, Note = Note, SizeX = SizeX, SizeY = SizeY, Z0 = Z0, Z1 = Z1, SrcSeed = SrcSeed,
        Bodies = Bodies.Count, SrcTick = SrcTick, Voxels = Voxels, SavedAt = SavedAt,
    };

    // ---- files ----

    public static string FileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string((name ?? "").Trim().Select(ch => bad.Contains(ch) || ch == ' ' ? '_' : ch).ToArray());
        return string.IsNullOrEmpty(s) ? "region" : s.Length > 60 ? s[..60] : s;
    }

    // Binary (Brotli) or JSON by the extension; written next to it first, then moved into place.
    public void Save(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = full + ".tmp";
        if (full.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) File.WriteAllText(tmp, ToJson());
        else using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)) Write(f);
        File.Move(tmp, full, true);
    }

    // Either format, told apart by the first bytes.
    public static Region Load(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        int first = f.ReadByte();
        f.Position = 0;
        if (first == Magic[0]) return Read(f);
        using var text = new StreamReader(f, Encoding.UTF8);
        return FromJson(text.ReadToEnd());
    }

    public static RegionInfo ReadInfo(string path)
    {
        using var f = File.OpenRead(path);
        int first = f.ReadByte();
        f.Position = 0;
        if (first == Magic[0]) return ReadInfo(f);
        return Load(path).Info;   // JSON: no separate header
    }

    public void Write(Stream stream)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("region files are little-endian");
        var info = Info;
        using (var head = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            head.Write(Magic); head.Write(FormatVersion);
            head.Write(Name ?? ""); head.Write(Note ?? "");
            head.Write(SizeX); head.Write(SizeY); head.Write(Z0); head.Write(Z1);
            head.Write(SrcSeed); head.Write(SrcTick); head.Write(SavedAt.Ticks);
            head.Write(Bodies.Count); head.Write(info.Voxels);
        }
        using var z = new BrotliStream(stream, CompressionLevel.Optimal, true);
        using var buf = new BufferedStream(z, 1 << 20);
        using var w = new BinaryWriter(buf, Encoding.UTF8, true);
        w.Write(WorldW); w.Write(WorldH); w.Write(WorldZ); w.Write(SaveVersion); w.Write(SrcX); w.Write(SrcY);
        Chem.Write(w);
        foreach (var c in Cols)
        {
            w.Write(c.Height); w.Write(c.Lo); w.Write(c.Mat.Length);
            w.Write(c.Mat); w.Write(c.Order);
            foreach (var u in c.Units) w.Write(u);
            foreach (var p in c.Pressure) w.Write(p);
            w.Write(c.Mix.Count);
            foreach (var m in c.Mix) { w.Write(m.Z); foreach (var n in m.Counts) w.Write(n); }
            w.Write(c.Burials.Count);
            foreach (var b in c.Burials) { w.Write(b.Z); foreach (var q in b.Matter) w.Write(q); w.Write(b.Order); w.Write(b.Pressure); }
            w.Write(c.Top);
            if (c.Top)
            {
                foreach (var q in c.Loose) w.Write(q);
                w.Write(c.LooseVolume); w.Write(c.Water); w.Write(c.Ice); w.Write(c.Snow);
            }
        }
        w.Write(Bodies.Count);
        foreach (var b in Bodies)
        {
            w.Write(b.X); w.Write(b.Y); w.Write(b.Z); w.Write(b.SrcId); w.Write(b.Lineage); w.Write(b.Gen);
            w.Write(b.Design ?? ""); w.Write(b.Energy); w.Write(b.Molecules);
            w.Write(b.State.Length); w.Write(b.State);
        }
        w.Write(EndMarker);
    }

    static RegionInfo ReadHeader(BinaryReader r)
    {
        var magic = r.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("not a Primordium region file");
        var i = new RegionInfo { Format = r.ReadInt32() };
        if (i.Format < 1 || i.Format > FormatVersion) throw new InvalidDataException($"region format {i.Format}, this build reads 1…{FormatVersion}");
        i.Name = r.ReadString(); i.Note = r.ReadString();
        i.SizeX = r.ReadInt32(); i.SizeY = r.ReadInt32(); i.Z0 = r.ReadInt32(); i.Z1 = r.ReadInt32();
        i.SrcSeed = r.ReadInt32(); i.SrcTick = r.ReadInt64(); i.SavedAt = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
        i.Bodies = r.ReadInt32(); i.Voxels = r.ReadInt64();
        return i;
    }

    public static RegionInfo ReadInfo(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, true);
        return ReadHeader(r);
    }

    public static Region Read(Stream stream)
    {
        RegionInfo info;
        using (var head = new BinaryReader(stream, Encoding.UTF8, true)) info = ReadHeader(head);
        using var z = new BrotliStream(stream, CompressionMode.Decompress, true);
        using var buf = new BufferedStream(z, 1 << 20);
        using var r = new BinaryReader(buf, Encoding.UTF8, true);
        var g = new Region
        {
            Name = info.Name, Note = info.Note, SizeX = info.SizeX, SizeY = info.SizeY, Z0 = info.Z0, Z1 = info.Z1,
            SrcSeed = info.SrcSeed, SrcTick = info.SrcTick, SavedAt = info.SavedAt,
        };
        if (g.SizeX <= 0 || g.SizeY <= 0 || (long)g.SizeX * g.SizeY > 1 << 22) throw new InvalidDataException($"region size {g.SizeX}×{g.SizeY} is not valid");
        g.WorldW = r.ReadInt32(); g.WorldH = r.ReadInt32(); g.WorldZ = r.ReadInt32(); g.SaveVersion = r.ReadInt32(); g.SrcX = r.ReadInt32(); g.SrcY = r.ReadInt32();
        g.Chem = RegionChemistry.Read(r);
        g.Cols = new RegionColumn[g.SizeX * g.SizeY];
        for (int k = 0; k < g.Cols.Length; k++)
        {
            var c = g.Cols[k] = new RegionColumn { Height = r.ReadInt32(), Lo = r.ReadInt32() };
            int n = r.ReadInt32();
            if (n < 0 || n > 1 << 16) throw new InvalidDataException("region column is damaged");
            c.Mat = r.ReadBytes(n); c.Order = r.ReadBytes(n);
            c.Units = new ushort[n]; for (int j = 0; j < n; j++) c.Units[j] = r.ReadUInt16();
            c.Pressure = new float[n]; for (int j = 0; j < n; j++) c.Pressure[j] = r.ReadSingle();
            int mixes = r.ReadInt32();
            for (int j = 0; j < mixes; j++)
            {
                var m = new RegionMix { Z = r.ReadInt32() };
                for (int s = 0; s < Chemistry.S; s++) m.Counts[s] = r.ReadUInt16();
                c.Mix.Add(m);
            }
            int burials = r.ReadInt32();
            for (int j = 0; j < burials; j++)
            {
                var b = new RegionBurial { Z = r.ReadInt32() };
                for (int s = 0; s < Chemistry.S; s++) b.Matter[s] = r.ReadInt64();
                b.Order = r.ReadSingle(); b.Pressure = r.ReadSingle();
                c.Burials.Add(b);
            }
            c.Top = r.ReadBoolean();
            if (c.Top)
            {
                c.Loose = new long[Chemistry.S];
                for (int s = 0; s < Chemistry.S; s++) c.Loose[s] = r.ReadInt64();
                c.LooseVolume = r.ReadSingle(); c.Water = r.ReadSingle(); c.Ice = r.ReadSingle(); c.Snow = r.ReadSingle();
            }
        }
        int bodies = r.ReadInt32();
        for (int k = 0; k < bodies; k++)
        {
            var b = new RegionBody
            {
                X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32(), SrcId = r.ReadInt64(), Lineage = r.ReadInt64(), Gen = r.ReadInt32(),
                Design = r.ReadString(), Energy = r.ReadDouble(), Molecules = r.ReadInt32(),
            };
            if (b.Design.Length == 0) b.Design = null;
            b.State = r.ReadBytes(r.ReadInt32());
            g.Bodies.Add(b);
        }
        if (r.ReadInt32() != EndMarker) throw new InvalidDataException("region file is damaged (no end marker)");
        return g;
    }

    // ---- JSON (the same fields; numbers as they are, body state as base64) ----

    sealed class JRegion
    {
        public string format = "primordium-region";
        public int version = FormatVersion;
        public string name, note;
        public int sizeX, sizeY, z0, z1, worldW, worldH, worldZ, saveVersion, srcX, srcY, srcSeed;
        public long srcTick;
        public DateTime savedAt;
        public RegionChemistry chemistry;
        public JColumn[] columns;
        public JBody[] bodies;
    }

    sealed class JColumn
    {
        public int height, lo;
        public int[] mat, units, order;
        public float[] pressure;
        public JMix[] mix;
        public JBurial[] burials;
        public bool top;
        public long[] loose;
        public float looseVolume, water, ice, snow;
    }

    sealed class JMix { public int z; public int[] counts; }
    sealed class JBurial { public int z; public long[] matter; public float order, pressure; }
    sealed class JBody { public int x, y, z, gen, molecules; public long srcId, lineage; public string design, state; public double energy; }

    static readonly JsonSerializerOptions json = new() { IncludeFields = true, WriteIndented = false };

    public string ToJson()
    {
        var j = new JRegion
        {
            name = Name, note = Note, sizeX = SizeX, sizeY = SizeY, z0 = Z0, z1 = Z1, worldW = WorldW, worldH = WorldH, worldZ = WorldZ,
            saveVersion = SaveVersion, srcX = SrcX, srcY = SrcY, srcSeed = SrcSeed, srcTick = SrcTick, savedAt = SavedAt, chemistry = Chem,
            columns = Cols.Select(c => new JColumn
            {
                height = c.Height, lo = c.Lo,
                mat = c.Mat.Select(x => (int)x).ToArray(), units = c.Units.Select(x => (int)x).ToArray(), order = c.Order.Select(x => (int)x).ToArray(),
                pressure = c.Pressure,
                mix = c.Mix.Select(m => new JMix { z = m.Z, counts = m.Counts.Select(x => (int)x).ToArray() }).ToArray(),
                burials = c.Burials.Select(b => new JBurial { z = b.Z, matter = b.Matter, order = b.Order, pressure = b.Pressure }).ToArray(),
                top = c.Top, loose = c.Loose, looseVolume = c.LooseVolume, water = c.Water, ice = c.Ice, snow = c.Snow,
            }).ToArray(),
            bodies = Bodies.Select(b => new JBody
            {
                x = b.X, y = b.Y, z = b.Z, gen = b.Gen, molecules = b.Molecules, srcId = b.SrcId, lineage = b.Lineage, design = b.Design,
                energy = b.Energy, state = Convert.ToBase64String(b.State),
            }).ToArray(),
        };
        return JsonSerializer.Serialize(j, json);
    }

    public static Region FromJson(string text)
    {
        JRegion j;
        try { j = JsonSerializer.Deserialize<JRegion>(text, json); }
        catch (JsonException e) { throw new InvalidDataException("not a Primordium region (JSON): " + e.Message); }
        if (j == null || j.format != "primordium-region") throw new InvalidDataException("not a Primordium region (JSON)");
        if (j.version < 1 || j.version > FormatVersion) throw new InvalidDataException($"region format {j.version}, this build reads 1…{FormatVersion}");
        if (j.chemistry == null || j.columns == null || j.columns.Length != (long)j.sizeX * j.sizeY) throw new InvalidDataException("region (JSON) is incomplete");
        static T[] Need<T>(T[] a, int n, string what) => a != null && a.Length == n ? a : throw new InvalidDataException($"region (JSON): {what} has the wrong length");
        var g = new Region
        {
            Name = j.name ?? "", Note = j.note ?? "", SizeX = j.sizeX, SizeY = j.sizeY, Z0 = j.z0, Z1 = j.z1, WorldW = j.worldW, WorldH = j.worldH, WorldZ = j.worldZ,
            SaveVersion = j.saveVersion, SrcX = j.srcX, SrcY = j.srcY, SrcSeed = j.srcSeed, SrcTick = j.srcTick, SavedAt = j.savedAt, Chem = j.chemistry,
        };
        g.Cols = j.columns.Select(c =>
        {
            int n = c.mat?.Length ?? 0;
            var col = new RegionColumn
            {
                Height = c.height, Lo = c.lo,
                Mat = (c.mat ?? Array.Empty<int>()).Select(x => (byte)x).ToArray(),
                Units = Need(c.units ?? Array.Empty<int>(), n, "units").Select(x => (ushort)x).ToArray(),
                Order = Need(c.order ?? Array.Empty<int>(), n, "order").Select(x => (byte)x).ToArray(),
                Pressure = Need(c.pressure ?? Array.Empty<float>(), n, "pressure"),
                Top = c.top, LooseVolume = c.looseVolume, Water = c.water, Ice = c.ice, Snow = c.snow,
                Loose = c.top ? Need(c.loose, Chemistry.S, "loose") : null,
            };
            foreach (var m in c.mix ?? Array.Empty<JMix>())
            {
                var mix = new RegionMix { Z = m.z };
                var counts = Need(m.counts, Chemistry.S, "mix");
                for (int s = 0; s < Chemistry.S; s++) mix.Counts[s] = (ushort)counts[s];
                col.Mix.Add(mix);
            }
            foreach (var b in c.burials ?? Array.Empty<JBurial>())
                col.Burials.Add(new RegionBurial { Z = b.z, Matter = Need(b.matter, Chemistry.S, "burial"), Order = b.order, Pressure = b.pressure });
            return col;
        }).ToArray();
        foreach (var b in j.bodies ?? Array.Empty<JBody>())
            g.Bodies.Add(new RegionBody
            {
                X = b.x, Y = b.y, Z = b.z, Gen = b.gen, Molecules = b.molecules, SrcId = b.srcId, Lineage = b.lineage, Design = b.design,
                Energy = b.energy, State = Convert.FromBase64String(b.state ?? ""),
            });
        return g;
    }

    // ---- comparison and a picture ----

    // Do two regions hold the same cells (every level, mixture, burial, surface)? Bodies are not compared
    // (a pasted body is a new body with a new id); `why` names the first difference.
    public bool SameCells(Region o, out string why)
    {
        why = null;
        if (SizeX != o.SizeX || SizeY != o.SizeY || Z0 != o.Z0 || Z1 != o.Z1) { why = $"size {SizeX}×{SizeY} [{Z0},{Z1}) vs {o.SizeX}×{o.SizeY} [{o.Z0},{o.Z1})"; return false; }
        for (int k = 0; k < Cols.Length; k++)
        {
            RegionColumn a = Cols[k], b = o.Cols[k];
            string at = $"column ({k % SizeX}, {k / SizeX})";
            if (a.Height != b.Height || a.Lo != b.Lo || a.Mat.Length != b.Mat.Length) { why = $"{at}: height {a.Height}/{b.Height}, levels {a.Lo}+{a.Mat.Length} vs {b.Lo}+{b.Mat.Length}"; return false; }
            if (!a.Mat.AsSpan().SequenceEqual(b.Mat) || !a.Order.AsSpan().SequenceEqual(b.Order) || !a.Units.AsSpan().SequenceEqual(b.Units)) { why = $"{at}: blocks differ"; return false; }
            if (!a.Pressure.AsSpan().SequenceEqual(b.Pressure)) { why = $"{at}: pressures differ"; return false; }
            if (a.Mix.Count != b.Mix.Count) { why = $"{at}: {a.Mix.Count} vs {b.Mix.Count} mixtures"; return false; }
            for (int j = 0; j < a.Mix.Count; j++)
                if (a.Mix[j].Z != b.Mix[j].Z || !a.Mix[j].Counts.AsSpan().SequenceEqual(b.Mix[j].Counts)) { why = $"{at}: mixture at level {a.Mix[j].Z} differs"; return false; }
            if (a.Burials.Count != b.Burials.Count) { why = $"{at}: {a.Burials.Count} vs {b.Burials.Count} burials"; return false; }
            for (int j = 0; j < a.Burials.Count; j++)
            {
                RegionBurial p = a.Burials[j], q = b.Burials[j];
                if (p.Z != q.Z || p.Order != q.Order || p.Pressure != q.Pressure || !p.Matter.AsSpan().SequenceEqual(q.Matter)) { why = $"{at}: burial at level {p.Z} differs"; return false; }
            }
            if (a.Top != b.Top) { why = $"{at}: surface in one only"; return false; }
            if (a.Top && (!a.Loose.AsSpan().SequenceEqual(b.Loose) || a.Water != b.Water || a.Ice != b.Ice || a.Snow != b.Snow || a.LooseVolume != b.LooseVolume)) { why = $"{at}: surface (loose matter, water, ice, snow) differs"; return false; }
        }
        return true;
    }

    // A map of the region from above (row by row, SizeX × SizeY): the colour of each column's top
    // block, lighter the higher it is; water tints it blue, ice and snow white.
    public Rgb[] Preview()
    {
        var px = new Rgb[SizeX * SizeY];
        int lo = int.MaxValue, hi = int.MinValue;
        foreach (var c in Cols) { int t = TopOf(c); if (t < 0) continue; lo = Math.Min(lo, t); hi = Math.Max(hi, t); }
        for (int k = 0; k < Cols.Length; k++)
        {
            var c = Cols[k];
            int t = TopOf(c);
            if (t < 0) { px[k] = new Rgb(0.08f, 0.09f, 0.11f); continue; }
            int m = c.Mat[t - c.Lo];
            float r = 0.4f, g = 0.4f, b = 0.4f;
            if (m >= 2) { int s = m - 2; r = Chem.Colour[s * 3]; g = Chem.Colour[s * 3 + 1]; b = Chem.Colour[s * 3 + 2]; }
            float light = 0.55f + 0.6f * (hi > lo ? (t - lo) / (float)(hi - lo) : 0.5f);
            r *= light; g *= light; b *= light;
            if (c.Top && c.Water > 0.3f) { float wk = Math.Min(0.75f, 0.25f + c.Water * 0.08f); r += (0.12f - r) * wk; g += (0.35f - g) * wk; b += (0.75f - b) * wk; }
            if (c.Top && c.Ice + c.Snow > 0.3f) { r += (0.9f - r) * 0.6f; g += (0.93f - g) * 0.6f; b += (0.97f - b) * 0.6f; }
            px[k] = new Rgb(Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
        }
        return px;
    }

    static int TopOf(RegionColumn c)
    {
        for (int j = c.Mat.Length - 1; j >= 0; j--) if (c.Mat[j] >= 2) return c.Lo + j;
        return -1;
    }
}
