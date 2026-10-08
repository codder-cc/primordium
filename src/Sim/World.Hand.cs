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
            stroke.Molecules += n;
        }
    }

    // Pour a recipe of the matter library (MatterRecipe, already resolved to this world's species):
    // number fractions `mix`, lattice order `order`. As blocks: at the centre `share` of a voxel's room
    // per dab, the molecules counted from the room each takes at that order (World.PackedVolume — an
    // ordered recipe packs more into a block), laid like any deposit (Deposit: tops up the floor block at
    // its own order, then new blocks at the recipe's order). Loose: the same molecules scattered on the
    // surface (the gas joins the air). Every atom is booked in HandInput and its bond energy in the hand's
    // energy input, as Pour does. The order itself has no energy in this engine (Metamorphose orders
    // blocks under pressure without an energy term), so a high-order block is player content: its matter
    // is booked, its order is not paid for. Returns the molecules brought in.
    public long PourMatter(int cx, int cy, float r, double[] mix, int order, bool loose, float share)
    {
        order = Math.Clamp(order, 0, 255);
        double sum = 0, room = 0;
        for (int s = 0; s < Chemistry.S; s++) if (mix[s] > 0) { sum += mix[s]; room += mix[s] * PackedVolume(s, order); }
        if (sum <= 0) return 0;
        double perVoxel = P.VoxelSpace / (room / sum);   // molecules of the mix that fill a voxel at this order
        long total = 0;
        foreach (var (c, w) in Brush(cx, cy, r))
        {
            var add = new ushort[Chemistry.S];
            int laid = 0;
            for (int s = 0; s < Chemistry.S; s++)
            {
                if (mix[s] <= 0) continue;
                int n = Math.Min(ushort.MaxValue - laid, (int)(perVoxel * share * w * mix[s] / sum + mainRng.NextDouble()));
                if (n <= 0) continue;
                add[s] = (ushort)n; laid += n;
                for (int e = 0; e < Chemistry.ElementCount; e++) HandInput[e] += (double)n * Chem.Atoms[s, e];
                Flows[FHand] += (double)n * Chem.E[s];
            }
            if (laid == 0) continue;
            total += laid;
            if (loose) { for (int s = 0; s < Chemistry.S; s++) if (add[s] > 0) C[s][c] += add[s]; }
            else Deposit(c, Height[c], add, (byte)order);
        }
        stroke.Molecules += total;
        return total;
    }

    // ---- the chronicle of the hand ----
    // A stroke (button down to button up) is one entry, written when it ends (SimRunner.EndStroke):
    // what the brush did and how much. Not saved: a stroke cut by a save loses only its summary line.
    struct HandStroke { public int Tool, X, Y; public long Molecules, Killed, Dug; public double Water; public string LabelEn, LabelRu; }
    HandStroke stroke;

    // Called with the first dab of a stroke: which tool, where, and (for pouring) what.
    public void BeginStroke(int tool, int x, int y, string labelEn, string labelRu)
    {
        stroke = new HandStroke { Tool = tool, X = x, Y = y, LabelEn = labelEn, LabelRu = labelRu };
    }

    // Writes the stroke to the chronicle (if it did anything). True if an entry was added.
    public bool EndStroke()
    {
        var s = stroke;
        stroke = default;
        string en, ru;
        switch (s.Tool)
        {
            case 1 when s.Molecules > 0:
                en = $"hand: poured {s.Molecules} molecules of {s.LabelEn} around ({s.X}, {s.Y}) (matter from outside, accounted for)";
                ru = $"рука: насыпано {s.Molecules} молекул — {s.LabelRu} — около ({s.X}, {s.Y}) (вещество извне, учтено)";
                break;
            case 2 when s.Water > 0:
                en = $"hand: poured {s.Water:0.#} water around ({s.X}, {s.Y})";
                ru = $"рука: налито {s.Water:0.#} воды около ({s.X}, {s.Y})";
                break;
            case 3 when s.Killed > 0:
                en = $"hand: killed {s.Killed} {(s.Killed == 1 ? "body" : "bodies")} around ({s.X}, {s.Y})";
                ru = $"рука: убито {s.Killed} {Plural((int)Math.Min(s.Killed, int.MaxValue), "тело", "тела", "тел")} около ({s.X}, {s.Y})";
                break;
            case 4 when s.Dug > 0:
                en = $"hand: dug out {s.Dug} {(s.Dug == 1 ? "block" : "blocks")} around ({s.X}, {s.Y}) (their matter left the world)";
                ru = $"рука: выкопано {s.Dug} {Plural((int)Math.Min(s.Dug, int.MaxValue), "блок", "блока", "блоков")} около ({s.X}, {s.Y}) (вещество ушло из мира)";
                break;
            default: return false;
        }
        Add(EvType.Player, Loc.Both(en, ru), null, s.Tool, false, null, s.X, s.Y);
        return true;
    }

    // Pour water: `depth` blocks of it at the centre per stroke. It then runs, evaporates and rains
    // like any other water.
    public void PourWater(int cx, int cy, float r, float depth)
    {
        foreach (var (c, w) in Brush(cx, cy, r))
        {
            float was = Water[c];
            Water[c] += depth * w;
            WaterHand += (double)Water[c] - was;   // the water budget (World.ClimateCycles): brought from outside
            stroke.Water += (double)Water[c] - was;
        }
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
            stroke.Dug++;
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
        stroke.Killed += killed;
        return killed;
    }
}
