using System;
using System.IO;
using System.Linq;

namespace Primordium;

// The world's size is a setting (WorldSettings.Width/Height/Levels): sizes out of the limits are refused,
// a small or narrow world is made and stepped (one column of tiles if it is too narrow for parallel
// tiles), what is given per planet comes by area, the save keeps the size (version 16) and a loaded world
// of another size continues identically; an older format refuses a world of another size.
public sealed partial class World
{
    public static void RunSizeRegression() { ParamRegistry.ResetDefaults(); SizeRegression(); }

    static void SizeRegression()
    {
        foreach (var (w, h, l) in new[] { (31, 64, 64), (64, 48, 64), (64, 64, 31), (64, 64, 256), (2048, 64, 64), (1024, 512, 255) })
            Require(new WorldSettings { Width = w, Height = h, Levels = l }.SizeProblem() != null, $"size {w}×{h}×{l} accepted");
        Require(new WorldSettings().SizeProblem() == null && new WorldSettings().DefaultSize, "the default size refused");
        Require(WorldSettings.TryParseSize("64x96x80", out int pw, out int ph, out int pl) && (pw, ph, pl) == (64, 96, 80) && !WorldSettings.TryParseSize("64x96", out _, out _, out _), "size text");

        // A narrow world: a single column of tiles; it steps and keeps its atoms.
        var narrow = new World(new WorldSettings { Seed = 5, InitialPop = 60, Abiogenesis = true, Width = 32, Height = 64, Levels = 48 });
        Require(narrow.TilesX == 1 && narrow.N == 32 * 64 && narrow.Z == 48 && narrow.Crust == CrustFor(48), $"narrow world layout: {narrow.TilesX}×{narrow.TilesY} tiles");
        Require(narrow.Height.Min() >= narrow.Crust + 2 && narrow.Height.Max() <= narrow.Z - 14, "relief outside a small world");
        var atoms0 = narrow.ElementBudget();
        for (int t = 0; t < 300; t++) narrow.Step();
        var atoms1 = narrow.ElementBudget();
        for (int e = 0; e < atoms1.Length; e++) Require(Math.Abs(atoms1[e] - narrow.InteriorInput[e] - narrow.HandInput[e] - atoms0[e]) <= 0.25, $"narrow world: element {e} drifted");
        narrow.CheckCellLists();

        // By area: the default world's first bodies and volcanoes scaled to 0.3 of the area.
        var settings = new WorldSettings { Seed = 3, Abiogenesis = true, Strikes = true, Width = 128, Height = 96, Levels = 96 };
        var a = new World(settings) { TrackHeat = true };
        Require(a.Agents.Count <= a.PerArea(P.InitialPop) && a.PerArea(P.InitialPop) == (int)Math.Round(P.InitialPop * 0.3, MidpointRounding.AwayFromZero) && a.Vents.Count <= a.PerArea(P.VentCount), $"by area: {a.Agents.Count} bodies, {a.Vents.Count} vents");
        Require(Math.Abs(a.Latitude(0) + a.Latitude(a.H - 1)) < 1e-5f && a.Latitude(0) > 1.4f, "latitude by row fraction");
        var e0 = a.AuditEnergy();
        for (int t = 0; t < 200; t++) a.Step();
        string path = Path.Combine(TestDir(), "small.sav");
        a.Save(path, "small");
        var info = ReadInfo(path);
        Require(info.Version == SaveVersion && (info.Width, info.Height, info.Levels) == (128, 96, 96) && !info.DefaultSize, "save header size");
        bool refused = false;
        try { a.Save(new MemoryStream(), null, System.IO.Compression.CompressionLevel.Fastest, 15); } catch (ArgumentException) { refused = true; }
        Require(refused, "a world of another size written in format 15");
        var b = Load(path);
        Require(b.W == 128 && b.H == 96 && b.Z == 96 && b.Settings.Width == 128 && b.DeepHash() == a.DeepHash(), "loaded small world differs");
        for (int t = 0; t < 200; t++) { a.Step(); b.Step(); }
        Require(a.StateHash() == b.StateHash(), "loaded small world diverged");
        string note = EnergyWorldCheck(b, e0, "small world save/load");
        // A default world loads after it: the size comes from each file.
        var d = new World(new WorldSettings { Seed = 1, InitialPop = 0 });
        var ms = new MemoryStream(); d.Save(ms); ms.Position = 0;
        var d2 = Load(ms);
        Require(d2.W == WorldSettings.DefaultWidth && d2.Z == WorldSettings.DefaultLevels && d2.StateHash() == d.StateHash(), "a default world after a small one");
        Console.WriteLine($"PASS world size: limits, a 32-wide world on {narrow.Tiles} tile(s) keeps its atoms, by area ({a.PerArea(P.InitialPop)} first bodies, {a.Vents.Count} vents on 128×96), save {info.Width}×{info.Height}×{info.Levels} (version {info.Version}) → load → +200 identical ({note})");
    }
}
