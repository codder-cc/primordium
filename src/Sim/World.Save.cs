using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Primordium;

// What a save file says about itself without being decompressed (for a list of saves).
public sealed class SaveInfo
{
    public int Version;
    public long Tick;
    public int Seed, Population;
    public DateTime SavedAt;   // UTC
    public string Note;
    // The world's size (version 16; older files were all 256×160×192).
    public int Width = WorldSettings.DefaultWidth, Height = WorldSettings.DefaultHeight, Levels = WorldSettings.DefaultLevels;
    public bool DefaultSize => Width == WorldSettings.DefaultWidth && Height == WorldSettings.DefaultHeight && Levels == WorldSettings.DefaultLevels;
    public override string ToString() => $"seed {Seed}, tick {Tick}, {Population} bodies{(DefaultSize ? "" : $", {Width}×{Height}×{Levels}")}, {SavedAt:yyyy-MM-dd HH:mm} UTC{(string.IsNullOrEmpty(Note) ? "" : " — " + Note)}";
}

// Saving and loading the whole world. A loaded world continues exactly where the saved one was:
// stepping it gives the same states (StateHash) as stepping the original would. The file is a
// small uncompressed header (SaveInfo) and a Brotli body: settings and laws, every counter, the random
// streams (main and per tile), dense cell fields, the voxel arrays up to the highest non-empty level
// of each column, mixtures and burials (sorted by voxel), the support solver's pending state, vents,
// strikes, discoveries and every body with all its fields (links, targets and cell lists by index).
// The chemistry is regenerated from the seed. Numbers are written little-endian as in memory.
// Call between ticks only (on the thread that steps the world).
public sealed partial class World
{
    // 2: the energy ledger (World.Energy) and WorldSettings.LifeSeed. 3: the chronicle block (SyncChronicle)
    // at the end of the body. 4: fractional amounts of matter (loose, burials, Pend, protein substrate) in
    // fixed point (Qty) instead of float. Older files still load: a version 1 ledger starts at zero at the
    // load (balances are differences, so they close from there); before version 3 the chronicle starts
    // empty; before version 4 a float is converted exactly from 2⁻⁹ molecule up, smaller ones to the
    // nearest 2⁻³². Writing an older version rounds the amounts back to float.
    // 5: the cave climate block (SyncCaveClimate) after the chronicle. 6: the course of evolution
    // (SyncEvolution, World.Evolution.cs) after the cave climate; before it the progress history starts
    // empty and every living body joins the shadows and the family tree as a founder.
    // 7: the geochemistry block (SyncGeochem, World.Geochem) after the course of evolution; before it
    // the depth profile is off (those worlds were made without it).
    // 8: the sky block (SyncSky, World.Sky) after the geochemistry; the energy ledger gains the `flare`
    // input (a version 7 ledger is mapped: the flows after it move up by one).
    // 9: the energy a body holds (Agent.Energy, Agent.HeatHeld) as double instead of float, so the energy
    // ledger closes like the atoms (ROADMAP 10.8); older files hold floats and are read into doubles exactly.
    // 10: the climate cycles block (SyncClimateCycles, World.ClimateCycles) after the sky.
    // 11: the sky block ends with every body's canopy store (Agent.LightQuota, World.Sky); before it the
    // stores start empty.
    // 12: the waterways block (SyncWaterways, World.Waterways) after the climate cycles: currents, the
    // water standing in caves, the drift of every body. Older files load with still water and dry caves.
    // 13: the chronicle block gains the ParasiteSpread event type (its count), the parasite counters and
    // every body's foreign piece of code (Agent.Foreign, World.Predation).
    // 14: every body record (SyncAgent, also in regions) ends with its life model (Agent.Model, LifeModels)
    // and that model's own state (ILifeModel.SyncState; model 1 has none). Older bodies are model 1.
    // 15: a body's pressing against a ledge (Agent.Climb, ClimbDir) at the end of its record, after its
    // life model's state; the mechanics block (SyncMechanics: the world's relief scale, settling state,
    // World.Settle) after the waterways. Before it the relief scale is 1 (worlds were made so), no body
    // presses against a ledge and settling starts fresh.
    // 16: the world's size (WorldSettings.Width/Height/Levels) — in the settings and, for the list of saves,
    // at the end of the header (width, height, levels). A file before it is 256×160×192 whatever its
    // settings say; a world of another size cannot be written in an older format.
    public const int SaveVersion = 16, OldestSaveVersion = 1;
    static readonly byte[] SaveMagic = Encoding.ASCII.GetBytes("PRIMSAVE");
    const int EndMarker = 0x21444E45;   // "END!"

    // Symmetric field visitor: the same list of fields is walked for writing and for reading, so the
    // two cannot disagree on order.
    public abstract class Sync
    {
        public int Version = SaveVersion;   // of the file being read (writing: always the current one)
        public abstract bool Reading { get; }
        public abstract void V(ref int x);
        public abstract void V(ref long x);
        public abstract void V(ref float x);
        public abstract void V(ref double x);
        public abstract void V(ref bool x);
        public abstract void V(ref byte x);
        public abstract void V(ref string x);
        public abstract void A<T>(Span<T> data) where T : unmanaged;
        public abstract void Rng(SimRng r);

        // Amounts of matter: exact fixed point from version 3, floats before.
        public void Q(Span<Qty> data)
        {
            if (Version >= 4) { A(data); return; }
            var f = new float[data.Length];
            if (!Reading) for (int i = 0; i < f.Length; i++) f[i] = data[i].F;
            A<float>(f);
            if (Reading) for (int i = 0; i < f.Length; i++) data[i] = f[i];
        }
        // The energy of a body: double from version 9, float before (read exactly, written rounded).
        public void E(ref double x)
        {
            if (Version >= 9) { V(ref x); return; }
            float f = (float)x;
            V(ref f);
            if (Reading) x = f;
        }
        public void Q(ref Qty x)
        {
            if (Version >= 4) { long raw = x.Raw; V(ref raw); x = Qty.FromRaw(raw); return; }
            float f = x.F;
            V(ref f);
            if (Reading) x = f;
        }
    }

    sealed class Writer : Sync
    {
        readonly BinaryWriter w;
        public Writer(BinaryWriter w) => this.w = w;
        public BinaryWriter Out => w;
        public override bool Reading => false;
        public override void V(ref int x) => w.Write(x);
        public override void V(ref long x) => w.Write(x);
        public override void V(ref float x) => w.Write(x);
        public override void V(ref double x) => w.Write(x);
        public override void V(ref bool x) => w.Write(x);
        public override void V(ref byte x) => w.Write(x);
        public override void V(ref string x) { w.Write(x != null); if (x != null) w.Write(x); }
        public override void A<T>(Span<T> data) => w.Write(MemoryMarshal.AsBytes(data));
        public override void Rng(SimRng r) => r.Write(w);
    }

    sealed class Reader : Sync
    {
        readonly BinaryReader r;
        public Reader(BinaryReader r) => this.r = r;
        public override bool Reading => true;
        public override void V(ref int x) => x = r.ReadInt32();
        public override void V(ref long x) => x = r.ReadInt64();
        public override void V(ref float x) => x = r.ReadSingle();
        public override void V(ref double x) => x = r.ReadDouble();
        public override void V(ref bool x) => x = r.ReadBoolean();
        public override void V(ref byte x) => x = r.ReadByte();
        public override void V(ref string x) => x = r.ReadBoolean() ? r.ReadString() : null;
        public override void A<T>(Span<T> data)
        {
            var bytes = MemoryMarshal.AsBytes(data);
            while (bytes.Length > 0)
            {
                int n = r.Read(bytes);
                if (n <= 0) throw new EndOfStreamException("save file ends early");
                bytes = bytes[n..];
            }
        }
        public override void Rng(SimRng rng) => rng.Read(r);
    }

    // ---- public API ----

    // Saves to a file (written next to it first, then moved into place: a crash never leaves half a save).
    public void Save(string path, string note = null)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmpPath = full + ".tmp";
        using (var f = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            Save(f, note);
        File.Move(tmpPath, full, true);
    }

    public void Save(Stream stream, string note = null, CompressionLevel level = CompressionLevel.Fastest) => Save(stream, note, level, SaveVersion);

    // An older format (what an older build wrote: the self-test checks that such files still load).
    internal void Save(Stream stream, string note, CompressionLevel level, int version)
    {
        if (version < OldestSaveVersion || version > SaveVersion) throw new ArgumentOutOfRangeException(nameof(version));
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("save files are little-endian");
        if (version < 16 && !Settings.DefaultSize) throw new ArgumentException($"a {Settings.SizeText} world needs save version 16 or later", nameof(version));
        using (var head = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            head.Write(SaveMagic);
            head.Write(version);
            head.Write(Tick);
            head.Write(Seed);
            head.Write(Agents.Count(a => !a.Dead));
            head.Write(DateTime.UtcNow.Ticks);
            head.Write(note ?? "");
            if (version >= 16) { head.Write(W); head.Write(H); head.Write(Z); }
        }
        using var z = new BrotliStream(stream, level, true);   // Fastest: quality 1, ~3× faster than GZip and smaller
        using var buf = new BufferedStream(z, 1 << 20);
        using var bw = new BinaryWriter(buf, Encoding.UTF8, true);
        SyncAll(new Writer(bw) { Version = version });
        bw.Write(EndMarker);
    }

    public static SaveInfo ReadInfo(string path)
    {
        using var f = File.OpenRead(path);
        return ReadInfo(f);
    }

    public static SaveInfo ReadInfo(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, true);
        var magic = r.ReadBytes(SaveMagic.Length);
        if (!magic.AsSpan().SequenceEqual(SaveMagic)) throw new InvalidDataException("not a Primordium save file");
        var info = new SaveInfo { Version = r.ReadInt32() };
        if (info.Version < OldestSaveVersion || info.Version > SaveVersion)
            throw new InvalidDataException($"save format {info.Version}, this build reads {OldestSaveVersion}…{SaveVersion}");
        info.Tick = r.ReadInt64();
        info.Seed = r.ReadInt32();
        info.Population = r.ReadInt32();
        info.SavedAt = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
        info.Note = r.ReadString();
        if (info.Version >= 16) { info.Width = r.ReadInt32(); info.Height = r.ReadInt32(); info.Levels = r.ReadInt32(); }
        return info;
    }

    // Loads a world. The laws (P) become those saved with it — they are shared by the process.
    public static World Load(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Load(f);
    }

    public static World Load(Stream stream)
    {
        var info = ReadInfo(stream);
        using var z = new BrotliStream(stream, CompressionMode.Decompress, true);
        using var buf = new BufferedStream(z, 1 << 20);
        using var br = new BinaryReader(buf, Encoding.UTF8, true);
        var reader = new Reader(br) { Version = info.Version };
        // Settings, laws and tile layout come first: the skeleton is built with them.
        string settingsJson = null;
        reader.V(ref settingsJson);
        var settings = WorldSettings.FromJson(settingsJson);
        // The size: the header's (version 16); an older file is of the one size there was.
        settings.Width = info.Width; settings.Height = info.Height; settings.Levels = info.Levels;
        var initialLaws = ReadLaws(br);
        var laws = ReadLaws(br);
        int tileSide = br.ReadInt32();
        // Laws added later than a file are its legacy behaviour, not today's default: a world saved
        // before them was made and run without them (its chemistry is regenerated from the seed, so
        // its energies must come out as they were).
        foreach (var (name, legacy) in LegacyLaws)
        {
            initialLaws.TryAdd(name, legacy);
            laws.TryAdd(name, legacy);
        }
        ParamRegistry.Restore(laws);
        var w = new World(settings, tileSide, false, (int)initialLaws[nameof(P.ChemEnergyModel)]) { InitialLaws = initialLaws };
        w.SyncBody(reader);
        if (br.ReadInt32() != EndMarker) throw new InvalidDataException("save file is damaged (no end marker)");
        return w;
    }

    // Laws whose absence from a save file means their legacy value (they were added after the file).
    static readonly (string name, double legacy)[] LegacyLaws =
    {
        (nameof(P.ChemEnergyModel), 0),
        (nameof(P.AbioModel), 0),
        (nameof(P.MatterEnergy), 0),
    };

    static void WriteLaws(BinaryWriter w, Dictionary<string, double> laws)
    {
        w.Write(laws.Count);
        foreach (var (name, value) in laws.OrderBy(kv => kv.Key, StringComparer.Ordinal)) { w.Write(name); w.Write(value); }
    }

    static Dictionary<string, double> ReadLaws(BinaryReader r)
    {
        int n = r.ReadInt32();
        var d = new Dictionary<string, double>(n);
        for (int k = 0; k < n; k++) { string name = r.ReadString(); d[name] = r.ReadDouble(); }
        return d;
    }

    // ---- the whole state ----

    void SyncAll(Writer s)
    {
        string settingsJson = Settings.ToJson();
        s.V(ref settingsJson);
        var bw = s.Out;
        WriteLaws(bw, InitialLaws);
        WriteLaws(bw, ParamRegistry.Snapshot());
        bw.Write(TileSide);
        SyncBody(s);
    }

    void SyncBody(Sync s)
    {
        SyncLawLog(s);
        SyncScalars(s);
        SyncRandom(s);
        SyncCells(s);
        SyncVoxels(s);
        SyncSparse(s);
        SyncStructure(s);
        SyncLists(s);
        SyncAgents(s);
        if (s.Version >= 2) SyncEnergy(s);
        if (s.Version >= 3) SyncChronicle(s);   // a separate block (see the comment there)
        SyncCaveClimate(s);                       // version 5: its own block after the chronicle
        if (s.Version >= 6) SyncEvolution(s);   // version 6: its own block, last (World.Evolution.cs)
        else if (s.Reading) EvoRegisterAll();
        SyncGeochem(s);                           // version 7: its own block after the course of evolution
        SyncSky(s);                               // version 8: its own block after the geochemistry (World.Sky)
        SyncClimateCycles(s);                     // version 10: its own block after the sky (World.ClimateCycles)
        SyncWaterways(s);                         // version 12: its own block after the climate cycles (World.Waterways)
        SyncMechanics(s);                         // version 15: relief scale and settling after the waterways (World.Settle)
    }

    // ---- mechanics (save version 15) ----
    // The relief scale the world was made with (the altitude climate and the lowlands follow it, and the
    // reference relief Height0 is made with it) and the settling state of every column (World.Settle).
    // Older worlds were made with the first relief; their columns are looked at afresh (the first look
    // only records their strain).
    void SyncMechanics(Sync s)
    {
        if (s.Version < 15) return;
        s.V(ref reliefScale);
        s.A<float>(settleDebt); s.A<float>(elasticSeen); s.A<bool>(settleCheck);
        s.V(ref SettledMolecules); s.V(ref ReboundMolecules); s.V(ref SettleEvents); s.V(ref LedgeClimbs);
        if (s.Reading) System.Threading.Tasks.Parallel.For(0, H, y => { for (int x = 0; x < W; x++) Height0[y * W + x] = GenHeight(x, y); });
    }

    // ---- cave climate (save version 5, World.Cave) ----
    // A block of its own after the chronicle: the slow mean temperature, the warmth of cave air and
    // what bodies under a roof shed since the last env step. Before version 5 the mean starts as the
    // climate's year-round normal, as in a new world (InitCaveClimate), and the caves start without
    // bodies' warmth.
    void SyncCaveClimate(Sync s)
    {
        if (s.Version < 5)
        {
            if (s.Reading) InitCaveClimate();
            return;
        }
        s.A<float>(Tmean); s.A<float>(CaveWarm); s.A<float>(caveHeatIn);
    }

    // The energy ledger: every accumulator as it is (per tile and per row, so the sums read after the
    // load are bit for bit those of the original), the heat seen through heatIn and whether it is tracked.
    void SyncEnergy(Sync s)
    {
        System.Threading.LazyInitializer.EnsureInitialized(ref tileFlow, InitFlows);
        // Before version 8 there was no `flare` input (index FFlare): the flows after it sat one lower.
        int count = s.Version >= 8 ? FlowCount : FlowCountV6;
        int slots = tileFlow.Length, flows = count;
        s.V(ref slots); s.V(ref flows);
        if (slots != tileFlow.Length || flows != count)
            throw new InvalidDataException($"energy ledger layout differs: {slots}×{flows} saved, {tileFlow.Length}×{count} built");
        if (count == FlowCount) foreach (var f in tileFlow) s.A<double>(f);
        else
        {
            var old = new double[count];
            foreach (var f in tileFlow)
            {
                if (!s.Reading) { Array.Copy(f, old, FFlare); Array.Copy(f, FFlare + 1, old, FFlare, count - FFlare); }
                s.A<double>(old);
                if (s.Reading) { Array.Clear(f); Array.Copy(old, f, FFlare); Array.Copy(old, FFlare, f, FFlare + 1, count - FFlare); }
            }
        }
        s.A<double>(rowLooseDecay);
        s.V(ref pressureHeat); s.V(ref heatFlushed); s.V(ref TrackHeat);
    }

    void SyncLawLog(Sync s)
    {
        int n = ParamLog.Count;
        s.V(ref n);
        if (s.Reading) { ParamLog.Clear(); for (int k = 0; k < n; k++) ParamLog.Add(default); }
        for (int k = 0; k < n; k++)
        {
            var c = ParamLog[k];
            s.V(ref c.Tick); s.V(ref c.Name); s.V(ref c.Value);
            ParamLog[k] = c;
        }
    }

    void SyncScalars(Sync s)
    {
        s.V(ref Tick); s.V(ref SunX); s.V(ref SunDecl);
        s.V(ref Births); s.V(ref Spawns); s.V(ref MaxGen);
        s.V(ref DeathsStarve); s.V(ref DeathsKilled); s.V(ref DeathsBroken); s.V(ref DeathsClimate); s.V(ref DeathsBuried); s.V(ref DeathsHand);
        s.V(ref Abiogenesis); s.V(ref AutoStrikes);
        s.A<long>(Ev); s.A<long>(Mined); s.A<long>(MinedCat);
        s.V(ref nextId); s.V(ref nextStructure);
        s.V(ref Moisture); s.V(ref RainSum); s.V(ref StrikeCount); s.V(ref nextStrike);
        s.V(ref nextVentAt); s.V(ref flowDirty); s.V(ref ventsDirty); s.V(ref TerrainVersion);
        s.V(ref overhangTotal); s.V(ref CollapsedBlocks); s.V(ref CrushedBlocks); s.V(ref StructureVisits);
        s.V(ref LastStructureColumns); s.V(ref LastStructureVoxels);
        s.A<long>(DirtBy); s.A<long>(BuriedBy);
        s.V(ref Sediments); s.V(ref Metamorphoses); s.V(ref Pushed);
        s.A<long>(DivFail);
        s.A<double>(InteriorInput); s.A<double>(HandInput); s.V(ref HandEnergy); s.V(ref DesignSpawns);
    }

    void SyncRandom(Sync s)
    {
        s.Rng(mainRng);
        int tiles = Tiles;
        s.V(ref tiles);
        if (tiles != Tiles) throw new InvalidDataException($"tile layout differs: {tiles} tiles saved, {Tiles} built");
        foreach (var c in ctxs) { s.Rng(c.Rng); s.V(ref c.IdCount); }
    }

    void SyncCells(Sync s)
    {
        s.A<int>(Height);
        for (int k = 0; k < Chemistry.S; k++) s.Q(C[k]);
        s.A<float>(Light); s.A<float>(Temp); s.A<float>(Ash); s.A<float>(Photon); s.A<float>(ventHeat);
        s.A<float>(Water); s.A<float>(Ice); s.A<float>(Snow); s.A<float>(Cloud); s.A<float>(Rain);
        s.A<float>(heatIn); s.A<float>(BodyHeat); s.A<float>(DeathMap); s.A<float>(LooseVolume);
        s.A<float>(climRow); s.A<float>(diffW);
        s.A<float>(Bite); s.A<long>(biteAt); s.A<int>(biteFace); s.A<byte>(biteMat);
        s.A<int>(overhangCount); s.A<int>(grounded); s.A<int>(topologySeen); s.A<int>(topologyVersion);
        s.A<bool>(HasCavity); s.A<bool>(annealable); s.A<int>(ColumnVersion);
        s.A<long>(lastAttack);
    }

    // Voxel arrays up to each column's highest level holding anything (most of the planet is air).
    void SyncVoxels(Sync s)
    {
        var extent = new byte[N];
        if (!s.Reading)
            for (int c = 0; c < N; c++)
            {
                int top = Z;
                while (top > 0)
                {
                    int v = c * Z + top - 1;
                    if (Mat[v] != 0 || Units[v] != 0 || Order[v] != 0 || Pressure[v] != 0 || overhang[v]) break;
                    top--;
                }
                extent[c] = (byte)top;
            }
        s.A<byte>(extent);
        void Column<T>(T[] data) where T : unmanaged
        {
            for (int c = 0; c < N; c++) if (extent[c] > 0) s.A(data.AsSpan(c * Z, extent[c]));
        }
        Column(Mat); Column(Units); Column(Order); Column(overhang); Column(Pressure);
    }

    // Mixtures and burials, by voxel; the per-voxel flags follow from them.
    void SyncSparse(Sync s)
    {
        var mixKeys = s.Reading ? null : mixtures.Keys.OrderBy(k => k).ToArray();
        int n = mixKeys?.Length ?? 0;
        s.V(ref n);
        for (int k = 0; k < n; k++)
        {
            int v = s.Reading ? 0 : mixKeys[k];
            s.V(ref v);
            var counts = s.Reading ? new ushort[Chemistry.S] : mixtures[v];
            s.A<ushort>(counts);
            if (s.Reading) SetMixture(v, counts);
        }
        var burialKeys = s.Reading ? null : Buried.Keys.OrderBy(k => k).ToArray();
        n = burialKeys?.Length ?? 0;
        s.V(ref n);
        for (int k = 0; k < n; k++)
        {
            int v = s.Reading ? 0 : burialKeys[k];
            s.V(ref v);
            var b = s.Reading ? new Burial() : Buried[v];
            s.Q(b.Matter); s.V(ref b.Order); s.V(ref b.Pressure);
            if (s.Reading) { Buried[v] = b; sparse[v] |= HasBurial; }
        }
    }

    // What the support solver carries from one pass to the next: columns waiting for it and the
    // body loads it last saw (in the order they were first added).
    void SyncStructure(Sync s)
    {
        var dirty = s.Reading ? null : structuralDirty.OrderBy(c => c).ToArray();
        int n = dirty?.Length ?? 0;
        s.V(ref n);
        for (int k = 0; k < n; k++)
        {
            int c = s.Reading ? 0 : dirty[k];
            s.V(ref c);
            if (s.Reading) structuralDirty.Add(c);
        }
        n = bodyLoad.Keys.Count;
        s.V(ref n);
        if (s.Reading) bodyLoad.Clear();
        var keys = s.Reading ? null : bodyLoad.Keys.ToArray();
        for (int k = 0; k < n; k++)
        {
            int v = s.Reading ? 0 : keys[k];
            float load = s.Reading ? 0 : bodyLoad.Get(v);
            s.V(ref v); s.V(ref load);
            if (s.Reading) bodyLoad.Add(v, load);
        }
    }

    void SyncLists(Sync s)
    {
        int n = Vents.Count;
        s.V(ref n);
        if (s.Reading) { Vents.Clear(); for (int k = 0; k < n; k++) Vents.Add(new Vent()); }
        foreach (var v in Vents)
        {
            s.V(ref v.X); s.V(ref v.Y); s.V(ref v.High); s.V(ref v.Mid); s.V(ref v.Toxic);
            s.V(ref v.Strength); s.V(ref v.Life); s.V(ref v.Age);
        }
        n = Strikes.Count;
        s.V(ref n);
        if (s.Reading) { Strikes.Clear(); for (int k = 0; k < n; k++) Strikes.Add(new Strike()); }
        foreach (var t in Strikes) { s.V(ref t.X); s.V(ref t.Y); s.V(ref t.R); s.V(ref t.T); }
        for (int m = 0; m < Firsts.Length; m++)
        {
            bool has = Firsts[m] != null;
            s.V(ref has);
            if (!has) continue;
            var f = s.Reading ? new Discovery() : Firsts[m];
            s.V(ref f.Tick); s.V(ref f.Lineage); s.V(ref f.AgentId); s.V(ref f.Mat);
            Firsts[m] = f;
        }
        n = DesignedLineages.Count;
        s.V(ref n);
        var lineages = s.Reading ? null : DesignedLineages.OrderBy(kv => kv.Key).ToArray();
        if (s.Reading) DesignedLineages.Clear();
        for (int k = 0; k < n; k++)
        {
            long lin = s.Reading ? 0 : lineages[k].Key;
            string name = s.Reading ? null : lineages[k].Value;
            s.V(ref lin); s.V(ref name);
            if (s.Reading) DesignedLineages[lin] = name;
        }
    }

    // Bodies in the order of Agents (the order matters: it is the order of every sequential pass).
    // References between bodies are indices into that list; a reference to a body that is no longer
    // in it (dead and removed) is equivalent to none and saved as -1.
    void SyncAgents(Sync s)
    {
        int n = Agents.Count;
        s.V(ref n);
        Dictionary<Agent, int> index = null;
        if (!s.Reading)
        {
            index = new Dictionary<Agent, int>(n, ReferenceEqualityComparer.Instance);
            for (int k = 0; k < n; k++) index[Agents[k]] = k;
        }
        int Ref(Agent a) => a != null && index.TryGetValue(a, out int k) ? k : -1;
        var refs = new int[n * 3];   // reading: Target, LinkWant, NextInCell per body, resolved at the end
        var links = new int[n][];
        if (s.Reading) Agents.Clear();
        for (int k = 0; k < n; k++)
        {
            Agent a;
            long id = 0, lineage = 0; int gen = 0;
            byte[] g = null, prot = null;
            if (!s.Reading) { a = Agents[k]; id = a.Id; lineage = a.Lineage; gen = a.Gen; g = a.G; prot = a.Prot; }
            else a = null;
            s.V(ref id); s.V(ref lineage); s.V(ref gen);
            int len = g?.Length ?? 0;
            s.V(ref len);
            if (s.Reading) { g = new byte[len]; prot = new byte[len]; }
            s.A<byte>(g); s.A<byte>(prot);
            if (s.Reading) { a = new Agent(id, lineage, gen, g, prot); Agents.Add(a); }
            SyncAgent(s, a);
            int target = s.Reading ? 0 : Ref(a.Target), want = s.Reading ? 0 : Ref(a.LinkWant), next = s.Reading ? 0 : Ref(a.NextInCell);
            s.V(ref target); s.V(ref want); s.V(ref next);
            refs[k * 3] = target; refs[k * 3 + 1] = want; refs[k * 3 + 2] = next;
            int nl = a.Links.Count;
            s.V(ref nl);
            links[k] = new int[nl];
            for (int j = 0; j < nl; j++)
            {
                int l = s.Reading ? 0 : Ref(a.Links[j]);
                s.V(ref l);
                links[k][j] = l;
            }
        }
        // Cell lists, big bodies' cells and alarms, by index.
        var head = new int[N]; var big = new int[N]; var attacker = new int[N];
        if (!s.Reading)
            for (int c = 0; c < N; c++) { head[c] = Ref(Head[c]); big[c] = Ref(Big[c]); attacker[c] = Ref(lastAttacker[c]); }
        s.A<int>(head); s.A<int>(big); s.A<int>(attacker);
        if (!s.Reading) return;
        Agent At(int k) => k >= 0 && k < n ? Agents[k] : (k < 0 ? null : throw new InvalidDataException($"body reference {k} out of range"));
        for (int k = 0; k < n; k++)
        {
            var a = Agents[k];
            a.Target = At(refs[k * 3]); a.LinkWant = At(refs[k * 3 + 1]); a.NextInCell = At(refs[k * 3 + 2]);
            if (a.NextInCell != null) a.NextInCell.PrevInCell = a;
            foreach (int l in links[k]) if (l >= 0) a.Links.Add(Agents[l]);
        }
        for (int c = 0; c < N; c++)
        {
            Head[c] = At(head[c]); Big[c] = At(big[c]); lastAttacker[c] = At(attacker[c]);
            int count = 0;
            for (var a = Head[c]; a != null; a = a.NextInCell) count++;
            Count[c] = count;
        }
    }

    static void SyncAgent(Sync s, Agent a)
    {
        s.V(ref a.Designed);
        s.V(ref a.X); s.V(ref a.Y); s.V(ref a.Z); s.V(ref a.Vx); s.V(ref a.Vy); s.V(ref a.Lift); s.V(ref a.Vz);
        s.E(ref a.Energy); s.V(ref a.Tb); s.V(ref a.Age); s.V(ref a.Dead); s.V(ref a.Cause);
        s.A<int>(a.Inv); s.Q(a.Pend);
        s.V(ref a.InvTotal); s.V(ref a.Unstable); s.V(ref a.Solids); s.V(ref a.Mass); s.V(ref a.Volume);
        int cap = a.Enz.Length;
        s.V(ref cap);
        if (s.Reading) a.Enz = new Enzyme[Math.Max(1, cap)];
        s.V(ref a.EnzN);
        for (int k = 0; k < a.EnzN; k++) SyncEnzyme(s, ref a.Enz[k]);
        s.V(ref a.Ip); s.V(ref a.Sp); s.V(ref a.Cp); s.V(ref a.Signal); s.V(ref a.LastCycles);
        s.A<int>(a.Stack); s.A<int>(a.Mem); s.A<int>(a.Calls);
        s.V(ref a.MateTick); s.V(ref a.LinkTick);
        s.V(ref a.Hue); s.V(ref a.Sat); s.V(ref a.Val); s.V(ref a.Sx); s.V(ref a.Sy); s.V(ref a.Sz); s.V(ref a.Shape);
        s.V(ref a.Act); s.V(ref a.ActDir); s.V(ref a.ActTick);
        s.V(ref a.GainPhoto); s.V(ref a.GainChem); s.V(ref a.GainMine);
        s.V(ref a.TickPhoto); s.V(ref a.TickChem); s.V(ref a.TickMine); s.V(ref a.TickAttack); s.V(ref a.TickHeat);
        s.E(ref a.HeatHeld);
        s.V(ref a.EmaPhoto); s.V(ref a.EmaChem); s.V(ref a.EmaMine); s.V(ref a.EmaAttack);
        s.V(ref a.NChildren); s.V(ref a.NMates); s.V(ref a.NMoves); s.V(ref a.NAttacks); s.V(ref a.NKills); s.V(ref a.NInjects);
        s.V(ref a.NInfected); s.V(ref a.NCuts); s.V(ref a.NDigs); s.V(ref a.NPiles); s.V(ref a.NMines); s.V(ref a.NTakes);
        s.V(ref a.NGives); s.V(ref a.NGrows); s.V(ref a.NStruck); s.V(ref a.NExpress);
        s.V(ref a.NPhoto); s.V(ref a.NSplit); s.V(ref a.NBind); s.V(ref a.NIntake); s.V(ref a.NExpel);
        s.V(ref a.TickGot); s.V(ref a.EmaGot); s.V(ref a.EmaUpkeep); s.V(ref a.EmaHarm); s.V(ref a.EmaNet);
        s.A<int>(a.OpCount);
        s.V(ref a.LifeStart); s.V(ref a.LifeGot); s.V(ref a.LifeKids); s.V(ref a.LifeUpkeep); s.V(ref a.LifeHarm);
        s.V(ref a.LifeSpill); s.V(ref a.LifeUphill); s.V(ref a.LifeMineCost);
        s.A<int>(a.NMinedTier); s.V(ref a.NCatMined); s.V(ref a.LastMeal);
        s.V(ref a.Cells); s.A<int>(a.Foot);
        if (s.Version >= 14)
        {
            byte model = a.Model;
            s.V(ref model);
            if (s.Reading)
            {
                if (!LifeModels.Known(model)) throw new InvalidDataException($"a body of life model {model}, unknown to this build");
                a.SetModel(model);
            }
            LifeModels.Get(model).SyncState(s, a);
        }
        else if (!s.Reading && a.Model != LifeModels.Vm) throw new InvalidOperationException($"save format {s.Version} holds only life model {LifeModels.Vm}");
        if (s.Version >= 15)
        {
            s.V(ref a.Climb);
            byte dir = (byte)(a.ClimbDir + 1);
            s.V(ref dir);
            a.ClimbDir = (sbyte)(dir - 1);
        }
    }

    // ---- the chronicle (save version 2) ----
    // A block of its own after the bodies: events, fossils, ancestry, what the surveys remember and the
    // chronicle's fields of every body (in the order of Agents). None of it changes the trajectory, but
    // a loaded world continues its chronicle exactly as the original would.

    void SyncChronicle(Sync s)
    {
        var c = Chronicle;
        s.V(ref c.NextSeq);
        s.A<long>(s.Version >= 13 ? c.Counts : c.Counts.AsSpan(0, CountsV12));   // version 13 added ParasiteSpread
        s.A<bool>(c.Seen);
        if (s.Version >= 13) { s.V(ref ParasiteInherited); s.V(ref ParasiteSpreads); }
        s.V(ref c.DepthBest); s.V(ref c.DepthShown); s.V(ref c.DominantLineage); s.V(ref c.DominantCount);
        s.A<float>(c.RecordBest);
        SyncEvents(s, c.Important);
        SyncEvents(s, c.Recent);
        int n = c.Fossils.Count;
        s.V(ref n);
        if (s.Reading) { c.Fossils.Clear(); c.FossilByAgent.Clear(); }
        for (int k = 0; k < n; k++)
        {
            var f = s.Reading ? new Fossil() : c.Fossils[k];
            SyncFossil(s, f);
            if (s.Reading) { c.Fossils.Add(f); c.FossilByAgent[f.AgentId] = f; }
        }
        var nodes = s.Reading ? null : c.Ancestry.Values.OrderBy(x => x.Id).ToArray();
        n = nodes?.Length ?? 0;
        s.V(ref n);
        if (s.Reading) c.Ancestry.Clear();
        for (int k = 0; k < n; k++)
        {
            var x = s.Reading ? new AncestryNode() : nodes[k];
            s.V(ref x.Id); s.V(ref x.ParentId); s.V(ref x.TrackedParent); s.V(ref x.Lineage); s.V(ref x.Gen);
            s.V(ref x.Born); s.V(ref x.Died); s.V(ref x.Cause); s.V(ref x.Why);
            if (s.Reading) c.Ancestry[x.Id] = x;
        }
        var infos = s.Reading ? null : c.Lineages.Values.OrderBy(x => x.Id).ToArray();
        n = infos?.Length ?? 0;
        s.V(ref n);
        if (s.Reading) c.Lineages.Clear();
        for (int k = 0; k < n; k++)
        {
            var x = s.Reading ? new LineageInfo() : infos[k];
            s.V(ref x.Id); s.V(ref x.Peak); s.V(ref x.Last); s.V(ref x.FirstTick); s.V(ref x.PeakTick);
            byte diet = (byte)x.Diet, pending = (byte)x.PendingDiet;
            s.V(ref diet); s.V(ref pending); s.V(ref x.PendingN); s.V(ref x.WasDominant);
            x.Diet = (sbyte)diet; x.PendingDiet = (sbyte)pending;
            int centers = x.Centers.Count;
            s.V(ref centers);
            for (int j = 0; j < centers; j++)
            {
                long v = s.Reading ? 0 : (long)x.Centers[j];
                s.V(ref v);
                if (s.Reading) x.Centers.Add((ulong)v);
            }
            bool rep = x.Rep != null;
            s.V(ref rep);
            if (rep) { if (s.Reading) x.Rep = new Fossil(); SyncFossil(s, x.Rep); }
            if (s.Reading) c.Lineages[x.Id] = x;
        }
        // Every body's chronicle fields.
        foreach (var a in Agents)
        {
            s.V(ref a.ParentId); s.V(ref a.TrackedAncestor); s.V(ref a.InfectedBy); s.V(ref a.CaveAge);
            if (s.Version >= 13) SyncForeign(s, a);
            s.V(ref a.Tracked); s.V(ref a.TrackWhy); s.V(ref a.BioSeen); s.V(ref a.BioN);
            bool bio = a.Bio != null;
            s.V(ref bio);
            if (!bio) continue;
            if (s.Reading) a.Bio = new BioEntry[Chronicle.BioCap];
            SyncBio(s, a.Bio);
        }
        if (s.Reading) { MarkEstablished(); c.Version++; }
    }

    // A body's foreign piece of code (World.Predation): bodies that share one piece share the array, but
    // each is written on its own (it is short and rare).
    static void SyncForeign(Sync s, Agent a)
    {
        int n = a.Foreign?.Length ?? -1;
        s.V(ref n);
        if (n < 0) return;
        if (s.Reading) a.Foreign = new byte[n];
        s.A<byte>(a.Foreign);
        s.V(ref a.ForeignFrom);
    }

    const int CountsV12 = (int)EvType.ParasiteSpread;   // event types before version 13

    // What a file before version 13 holds: no ParasiteSpread count, no parasite counters, no foreign
    // code (a loaded world starts them empty; the old-version save tests bring the original to the same).
    internal void PressureFromOldFile()
    {
        Chronicle.Counts[(int)EvType.ParasiteSpread] = 0;
        ParasiteInherited = ParasiteSpreads = 0;
        foreach (var a in Agents) { a.Foreign = null; a.ForeignFrom = 0; }
    }

    static void SyncEvents(Sync s, List<ChronicleEvent> list)
    {
        int n = list.Count;
        s.V(ref n);
        if (s.Reading) list.Clear();
        for (int k = 0; k < n; k++)
        {
            var e = s.Reading ? new ChronicleEvent() : list[k];
            s.V(ref e.Seq); s.V(ref e.Tick);
            byte type = (byte)e.Type;
            s.V(ref type);
            e.Type = (EvType)type;
            s.V(ref e.AgentId); s.V(ref e.Lineage); s.V(ref e.X); s.V(ref e.Y); s.V(ref e.Z);
            s.V(ref e.Text); s.V(ref e.Value); s.V(ref e.Important);
            e.Genome = SyncBytes(s, e.Genome);
            if (s.Reading) list.Add(e);
        }
    }

    static byte[] SyncBytes(Sync s, byte[] data)
    {
        int len = data?.Length ?? -1;
        s.V(ref len);
        if (len < 0) return null;
        if (s.Reading) data = new byte[len];
        s.A<byte>(data);
        return data;
    }

    // Structs go field by field, never as raw memory: their padding bytes hold garbage, and a save must be
    // the same bytes for the same world.
    static void SyncEnzyme(Sync s, ref Enzyme e)
    {
        s.V(ref e.Kind); s.V(ref e.A); s.V(ref e.B); s.V(ref e.Topt); s.V(ref e.Eff); s.V(ref e.Amount);
        s.V(ref e.Material); s.Q(ref e.Matter); s.V(ref e.Src);
    }

    static void SyncBio(Sync s, BioEntry[] bio)
    {
        for (int k = 0; k < bio.Length; k++)
        {
            ref var b = ref bio[k];
            byte kind = (byte)b.Kind;
            s.V(ref b.Tick); s.V(ref b.Other); s.V(ref b.Value); s.V(ref kind); s.V(ref b.Arg);
            b.Kind = (BioKind)kind;
        }
    }

    static void SyncFossil(Sync s, Fossil f)
    {
        s.V(ref f.AgentId); s.V(ref f.Lineage); s.V(ref f.ParentId); s.V(ref f.Gen);
        s.V(ref f.BornTick); s.V(ref f.DiedTick); s.V(ref f.Cause); s.V(ref f.EventSeq);
        f.Genome = SyncBytes(s, f.Genome);
        s.A<int>(f.Body);
        int n = f.Proteins.Length;
        s.V(ref n);
        if (s.Reading) f.Proteins = new Enzyme[n];
        for (int k = 0; k < n; k++) SyncEnzyme(s, ref f.Proteins[k]);
        n = f.Bio.Length;
        s.V(ref n);
        if (s.Reading) f.Bio = new BioEntry[n];
        SyncBio(s, f.Bio);
        s.V(ref f.Hue); s.V(ref f.Sat); s.V(ref f.Val); s.V(ref f.Energy); s.V(ref f.Mass);
        s.V(ref f.Shape); s.V(ref f.Children); s.V(ref f.Age); s.V(ref f.Designed); s.V(ref f.Why); s.V(ref f.Importance);
    }
}
