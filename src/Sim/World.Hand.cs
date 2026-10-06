using System;
using System.Collections.Generic;

namespace Primordium;

// The player's hand: a round brush that pours matter or water, digs or kills. It acts between ticks
// (never during the agent phase). Matter the hand brings in or takes away is booked in HandInput,
// like the interior's in InteriorInput, so the element budget stays exact: the hand is the only way
// matter enters or leaves the world from outside.
public sealed partial class World
{
    public readonly double[] HandInput = new double[Chemistry.ElementCount];   // atoms brought (+) or taken (−) by the hand
    public double HandEnergy;   // energy the player injected from outside (designed creatures brought in with energy)
    public int DeathsHand;

    // The cells of a round brush around (cx, cy) with radius r, each with a weight falling from 1 at
    // the centre to 0 at the rim — so a stroke heaps a mound, not a slab.
    readonly List<(int cell, float w)> brush = new();

    List<(int cell, float w)> Brush(int cx, int cy, float r)
    {
        brush.Clear();
        int ir = (int)MathF.Ceiling(r);
        for (int dy = -ir; dy <= ir; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= H) continue;
            for (int dx = -ir; dx <= ir; dx++)
            {
                float d2 = (dx * dx + dy * dy) / (r * r);
                if (d2 > 1) continue;
                brush.Add((y * W + ((cx + dx) % W + W) % W, 1 - d2));
            }
        }
        return brush;
    }

    // A molecule kind to pour, at random (not the gas: it has no aggregate worth heaping).
    public int RandomPourable()
    {
        int s;
        do s = mainRng.Next(Chemistry.S); while (s == Chem.Gas);
        return s;
    }

    // Heap up loose matter of kind s: at the centre `share` of a full block per stroke, less towards
    // the rim. It is laid like any deposit (fills the top block, then starts a new one, slides off
    // too steep a slope, pushes aside whoever stands there), freshly poured and so disordered.
    public void Pour(int cx, int cy, float r, int s, float share)
    {
        int cap = Chem.MatCap[s + 2];
        foreach (var (c, w) in Brush(cx, cy, r))
        {
            int n = Math.Min(ushort.MaxValue, (int)(cap * share * w + mainRng.NextDouble()));
            if (n <= 0) continue;
            var add = new ushort[Chemistry.S];
            add[s] = (ushort)n;
            for (int e = 0; e < Chemistry.ElementCount; e++) HandInput[e] += (double)n * Chem.Atoms[s, e];
            Flows[FHand] += (double)n * Chem.E[s];
            Deposit(c, Height[c], add, 12);
        }
    }

    // Pour water: `depth` blocks of it at the centre per stroke. It then runs, evaporates and rains
    // like any other water.
    public void PourWater(int cx, int cy, float r, float depth)
    {
        foreach (var (c, w) in Brush(cx, cy, r)) Water[c] += depth * w;
    }

    // Dig: the top block of each column goes with chance `chance` at the centre per stroke (less
    // towards the rim). Its molecules leave the world with the hand; remains lying loose stay, and
    // whatever was buried in the block drops to the floor below. Bodies standing there fall in.
    public void DigOut(int cx, int cy, float r, float chance)
    {
        foreach (var (c, w) in Brush(cx, cy, r))
        {
            int z = Height[c] - 1;
            if (z < 2 || mainRng.NextDouble() >= chance * w) continue;
            int v = c * Z + z;
            if (Mat[v] < 2) continue;   // the boundary of the deep interior is not for digging
            for (int s = 0; s < Chemistry.S; s++)
            {
                int n = VoxelCount(v, s);
                if (n > 0) for (int e = 0; e < Chemistry.ElementCount; e++) HandInput[e] -= (double)n * Chem.Atoms[s, e];
                Flows[FHand] -= (double)n * Chem.E[s];
            }
            RemoveVoxel(c, z);
        }
    }

    // Kill every body in the brush (also big bodies reaching into it). Their remains stay where they
    // lie, as after any death.
    public int KillIn(int cx, int cy, float r)
    {
        int killed = 0;
        foreach (var (c, _) in Brush(cx, cy, r))
        {
            for (var a = Head[c]; a != null;)
            {
                var next = a.NextInCell;
                Die(a, c, CauseHand);
                killed++;
                a = next;
            }
            var big = Big[c];
            if (big is { Dead: false }) { Die(big, big.Y * W + big.X, CauseHand); killed++; }
        }
        return killed;
    }
}
