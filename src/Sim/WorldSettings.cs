using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Primordium;

// How a new world is made: its seed, who lives there at first, which outside influences are on, and
// the laws it starts with. World(WorldSettings), SimRunner.NewWorld and save files use it.
public sealed class WorldSettings
{
    public int Seed { get; set; } = 1;
    // The world's size: columns around the cylinder (x wraps), rows from pole to pole, levels of every
    // column. Width and height are multiples of RegionSide (the 32×32 squares regions, tiles and the
    // statistics are counted in); see Validate for the limits. Files before save version 16 do not name
    // a size: they were 256×160×192, the defaults.
    public int Width { get; set; } = DefaultWidth;
    public int Height { get; set; } = DefaultHeight;
    public int Levels { get; set; } = DefaultLevels;
    public const int DefaultWidth = 256, DefaultHeight = 160, DefaultLevels = 192;
    public const int MinSide = 32, MaxWidth = 1024, MaxHeight = 512, SideStep = World.RegionSide;
    // Levels: at least room for crust, relief and air; at most 255 (a column's extent is a byte in saves).
    public const int MinLevels = 32, MaxLevels = 255;
    public const long MaxVoxels = 1L << 25;   // ~4× the default world (~18 bytes a voxel: ~600 MB)
    public int InitialPop { get; set; } = -1;        // random genomes scattered at the start; < 0: P.InitialPop (by area: World.PerArea)
    // Off by default: the player starts life (N, designs) and strikes (X) when they want; both can be
    // toggled at runtime (World.Abiogenesis, World.AutoStrikes; SimRunner.SetAbiogenesis/SetStrikes).
    public bool Abiogenesis { get; set; } = false;   // now and then a random newcomer from local matter
    public bool Strikes { get; set; } = false;       // mutagenic strikes from space now and then
    // ≠ 0: the same planet (terrain, chemistry, vents, water, litter of Seed) with other first bodies and
    // other random streams for the agents — repeats of one seed that differ only by chance (World).
    public int LifeSeed { get; set; } = 0;
    // Laws to start with: null keeps the current values of P; otherwise every law takes the value
    // named here or its default (a ParamPreset's Values).
    public Dictionary<string, double> Params { get; set; }
    public string PresetName { get; set; }           // for display only

    public WorldSettings Clone() => new()
    {
        Seed = Seed, Width = Width, Height = Height, Levels = Levels, InitialPop = InitialPop, Abiogenesis = Abiogenesis, Strikes = Strikes, LifeSeed = LifeSeed,
        Params = Params == null ? null : new Dictionary<string, double>(Params), PresetName = PresetName,
    };

    [JsonIgnore] public bool DefaultSize => Width == DefaultWidth && Height == DefaultHeight && Levels == DefaultLevels;
    [JsonIgnore] public string SizeText => $"{Width}×{Height}×{Levels}";

    // Null if the size can be built, otherwise why not (in the language of the interface).
    public string SizeProblem()
    {
        if (Width < MinSide || Width > MaxWidth || Width % SideStep != 0)
            return Loc.T($"width {Width}: {MinSide}…{MaxWidth}, a multiple of {SideStep}", $"ширина {Width}: {MinSide}…{MaxWidth}, кратно {SideStep}");
        if (Height < MinSide || Height > MaxHeight || Height % SideStep != 0)
            return Loc.T($"height {Height}: {MinSide}…{MaxHeight}, a multiple of {SideStep}", $"высота {Height}: {MinSide}…{MaxHeight}, кратно {SideStep}");
        if (Levels < MinLevels || Levels > MaxLevels)
            return Loc.T($"levels {Levels}: {MinLevels}…{MaxLevels}", $"уровней {Levels}: {MinLevels}…{MaxLevels}");
        if ((long)Width * Height * Levels > MaxVoxels)
            return Loc.T($"{SizeText} is {(long)Width * Height * Levels / 1e6:0.#} million voxels, at most {MaxVoxels / 1e6:0.#}",
                         $"{SizeText} — {(long)Width * Height * Levels / 1e6:0.#} млн вокселей, не больше {MaxVoxels / 1e6:0.#}");
        return null;
    }

    public void Validate()
    {
        var problem = SizeProblem();
        if (problem != null) throw new ArgumentException(Loc.T("world size: ", "размер мира: ") + problem);
    }

    // "WxHxL" (x, × or *): the size of a world from the command line; false if it does not parse.
    public static bool TryParseSize(string text, out int width, out int height, out int levels)
    {
        width = height = levels = 0;
        var parts = (text ?? "").Split('x', 'X', '×', '*');
        return parts.Length == 3 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height) && int.TryParse(parts[2], out levels);
    }

    static readonly JsonSerializerOptions json = new() { WriteIndented = true };
    public string ToJson() => JsonSerializer.Serialize(this, json);
    public static WorldSettings FromJson(string text) => JsonSerializer.Deserialize<WorldSettings>(text) ?? new WorldSettings();
    public void Save(string path) => File.WriteAllText(path, ToJson());
    public static WorldSettings Load(string path) => FromJson(File.ReadAllText(path));
}
