using System.Collections.Generic;

namespace Primordium;

// Creatures the player designs (CreatureDesign) and plants into the world.
public sealed partial class World
{
    public int DesignSpawns;   // bodies made from player designs
    // Lineages started (or chosen) for player-designed creatures, with the design's name: their
    // descendants are recognisable as the player's (Agent.Designed marks the planted bodies themselves).
    public readonly Dictionary<long, string> DesignedLineages = new();

    public string DesignOf(long lineage) => DesignedLineages.TryGetValue(lineage, out var name) ? name : null;
}
