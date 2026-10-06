using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Primordium;

// How a new world is made: its seed, who lives there at first, which outside influences are on, and
// the laws it starts with. World(WorldSettings), SimRunner.NewWorld and save files use it.
public sealed class WorldSettings
{
    public int Seed { get; set; } = 1;
    public int InitialPop { get; set; } = -1;        // random genomes scattered at the start; < 0: P.InitialPop
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
        Seed = Seed, InitialPop = InitialPop, Abiogenesis = Abiogenesis, Strikes = Strikes, LifeSeed = LifeSeed,
        Params = Params == null ? null : new Dictionary<string, double>(Params), PresetName = PresetName,
    };

    static readonly JsonSerializerOptions json = new() { WriteIndented = true };
    public string ToJson() => JsonSerializer.Serialize(this, json);
    public static WorldSettings FromJson(string text) => JsonSerializer.Deserialize<WorldSettings>(text) ?? new WorldSettings();
    public void Save(string path) => File.WriteAllText(path, ToJson());
    public static WorldSettings Load(string path) => FromJson(File.ReadAllText(path));
}
