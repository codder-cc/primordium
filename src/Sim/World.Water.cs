using System;

namespace Primordium;

// Water for bodies. In a flooded column a body has a height above its floor (Agent.Lift). Whether
// it rises or sinks is its density against the water's: the molecules it is made of, and any gas it
// holds, which is a bubble there (Chemistry.BodyVolume). Taking gas in, letting it out or binding it
// into something heavier is how a body chooses its depth; a motor protein can also push it up or
// down, at a price. Off the bottom it cannot reach the remains lying there, nor gnaw rock: it finds
// only the thin share dissolved in the water, thinner the higher above the bottom it is, and at the
// surface the air's gas again (not through floating ice). Light comes from above and fades with every
// block of water over the body. Every stroke through water costs drag, more the deeper under the
// surface it is. Water that has run into a cave (World.Waterways) is water like any other: a body on a
// flooded cave floor floats and swims there, under the cave's roof.
public sealed partial class World
{
    // A flooded floor with enough water to swim in: open to the sky, or a cave floor the water has run
    // into (World.Waterways).
    public bool InWater(int cell, int z) => z >= Height[cell] ? Water[cell] >= P.SwimDepth : cave.Count > 0 && CaveDepth(cell, z) >= P.SwimDepth;
    public bool InWater(Agent a) => InWater(a.Y * W + a.X, a.Z);

    // How deep the water over the floor at `z` of `cell` is: the column's water on the surface, the
    // water standing on a cave floor under it.
    public float WaterOver(int cell, int z) => z >= Height[cell] ? Water[cell] : cave.Count > 0 ? CaveDepth(cell, z) : 0;

    // How high above that floor a body can swim: up to the surface, in a cave up to the highest voxel
    // under its roof.
    float WaterTop(int cell, int z) => z >= Height[cell] ? Water[cell] : cave.Count > 0 ? CaveTop(cell, z) : 0;

    // Where a body is, in blocks: its floor plus how high above it it swims.
    static float Level(Agent a) => a.Z + a.Lift;

    // Lying on the bottom (or standing on dry ground): the floor and what lies on it are in reach.
    public static bool OnFloor(Agent a) => a.Lift < 0.5f;

    // The level a body acts at: its floor, or the block of water it swims in.
    static int ActLevel(Agent a) => a.Z + (int)(a.Lift + 0.5f);

    // How far under the surface a body is in `cell`.
    public float Below(Agent a, int cell) => Math.Max(0, WaterOver(cell, a.Z) - a.Lift);

    // How much stands between a body and the air: the water over it, and floating ice on top of that
    // as much again (IceFloat; ice lets no gas through either).
    float AirBelow(Agent a, int cell) => P.IceFloat != 0 && a.Z >= Height[cell] ? Below(a, cell) + Ice[cell] : Below(a, cell);

    // At the surface: in touch with the air (not under a sheet of ice).
    public bool AtSurface(Agent a, int cell) => AirBelow(a, cell) < 0.5f;

    // What a body weighs in water: its mass minus that of the water it pushes aside.
    static float Weight(Agent a) => Math.Max(0, a.Mass - P.WaterDensity * a.Volume);

    // How much of what lies loose in a flooded `cell` the body gets at: on the bottom all the remains
    // there, higher up only their dissolved share; the air's gas dissolves from the surface, less the
    // deeper the body is.
    float Exposure(Agent a, int cell, int s)
    {
        if (P.Volatility != 0)
        {
            // Volatility 1: the share in the air dissolves from the surface, the rest lies on the bottom.
            float v = Chem.Volatile[s];
            float lying = OnFloor(a) ? 1 : P.Solubility * MathF.Exp(-a.Lift);
            return v <= 0 ? lying : v >= 1 ? MathF.Exp(-AirBelow(a, cell)) : v * MathF.Exp(-AirBelow(a, cell)) + (1 - v) * lying;
        }
        if (s == Chem.Gas) return MathF.Exp(-AirBelow(a, cell));
        return OnFloor(a) ? 1 : P.Solubility * MathF.Exp(-a.Lift);
    }

    // Each tick in water: what the water lifts against what the body weighs, slowed by drag. Out of
    // water (dry ground, a cave) it stands on its floor.
    void Float(Agent a, int cell)
    {
        if (!InWater(cell, a.Z)) { a.Lift = a.Vz = 0; return; }
        float rho = a.Density;
        a.Vz = (a.Vz + P.Buoyancy * (P.WaterDensity - rho) / rho) * P.WaterDrag;
        a.Lift += a.Vz;
        float top = WaterTop(cell, a.Z);
        if (a.Lift <= 0) { a.Lift = 0; a.Vz = Math.Max(0, a.Vz); }
        else if (a.Lift >= top) { a.Lift = top; a.Vz = Math.Min(0, a.Vz); }
    }

    // A body arriving on the floor at `z` of `cell` from height `level`: in water it keeps its height
    // (as far as the water reaches), out of it it stands on the floor.
    void SetLift(Agent a, int cell, int z, float level)
    {
        a.Lift = InWater(cell, z) ? Math.Clamp(level - z, 0, WaterTop(cell, z)) : 0;
        if (a.Lift == 0) a.Vz = 0;
    }

    // Energy for one stroke through water: drag by the body's mass, more the deeper it is.
    float Stroke(Agent a, float below) => P.CostSwim * a.Mass * (1 + P.DepthK * below);

    // Room above a floor: a flooded floor also has the water over it to swim in (a puddle too shallow
    // to swim in is no room).
    float Space(int cell, int level) => P.VoxelSpace * (1 + (InWater(cell, level) ? WaterOver(cell, level) : 0));
}
