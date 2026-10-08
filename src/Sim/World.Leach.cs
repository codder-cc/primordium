using System;

namespace Primordium;

// A sink for loose matter (ROADMAP 4.1, 4.3): water percolating through the ground carries litter down.
//
// Why. Wear (World.Wear) makes bodies lose matter, but what they lose lands at their feet and is taken
// back in; the remains of the dead and the primordial litter lie on the surface for years. Nothing
// leaves the reachable pool, so no region is ever eaten out and no resource limits anybody
// (CHANGELOG (13), (16)).
//
// Law (P.LeachK, 0 — off, the world goes exactly as before). Once every MetamorphEvery ticks, of every
// loose species s lying on the surface of a column (World.C) the share
//     LeachK · MetamorphEvery · wet · Diff[s] / max Diff
// soaks into the ground:
//  - wet = min(1, Water + Rain) of the column: standing water (a block or more — saturated ground) or
//    rain at full strength percolate fully, dry ground under a clear sky not at all;
//  - Diff[s] / max Diff is the molecule's mobility from its mass (Chemistry.Diff = 0.13/√mass, the
//    same law that moves the gas), relative to the lightest molecule of the world's chemistry: light
//    molecules are carried away first, heavy ones stay.
// Nothing about what a molecule is good for enters the law, and no species is singled out: every pool
// of C is treated alike, the air's gas too (it is the lightest, so wet ground draws it in fastest — the
// gas dissolved in percolating water; rain's adsorption onto the top block, EnvChem, is unchanged).
// The matter moves into the burial of the block under the top block (World.Buried), where no body
// standing on the surface reaches it. If that level is a void (a cave right under the top block), the
// matter drips through to the cavity's floor and lies there, reachable from the cave. Nothing is made
// or lost: the same Qty leaves C and enters the burial (atoms exact; bond energy moves between two
// reservoirs of the ledger, no flow). It comes back by the ordinary paths only: when the top block goes
// (mined through, dug, weathered, crushed), its burial below becomes the floor (World.RemoveVoxel moves
// burials down to the next floor), and pressure reactions in burials go on as before.
//
// Only in the environment phase, on the main thread (World.EnvChem): no contract with the agent tiles.
public sealed partial class World
{
    public static bool LeachLaw => P.LeachK > 0;

    // Loose matter that soaked into the ground since the world was made or loaded, molecules
    // (observation only: never read by the world, not saved).
    public double Leached;

    // 1 / the largest Diff of the world's chemistry (the lightest molecule): mobility is relative to it.
    double leachMobile;
    void LeachPass() { float d = 0; foreach (float x in Chem.Diff) d = Math.Max(d, x); leachMobile = d > 0 ? 1.0 / d : 0; }

    // One column: the share k · wet · mobility of each species of its surface litter soaks below the top block.
    void Leach(int i, double k)
    {
        int h = Height[i];
        if (h <= 3) return;
        k *= Math.Min(1f, Water[i] + Rain[i]);
        if (k <= 0) return;
        int z = h - 2;
        while (z > 1 && !IsSolid(i, z)) z--;   // through a cavity under the roof: onto its floor
        int v = i * Z + z;
        var diff = Chem.Diff;
        double mobile = leachMobile;
        Burial b = null;
        long moved = 0;
        for (int s = 0; s < Chemistry.S; s++)
        {
            long raw = C[s][i].Raw;
            if (raw <= 0) continue;
            long m = (long)(raw * Math.Min(1.0, k * diff[s] * mobile));   // truncated toward zero: never more than lies there
            if (m <= 0) continue;
            C[s][i] = Qty.FromRaw(raw - m);
            (b ??= BurialAt(v)).Matter[s] += Qty.FromRaw(m);
            moved += m;
        }
        if (b == null) return;
        MassChanged(v);
        Leached += moved / Qty.One;
        if (ResProbe != null) ResProbe.Leach[RegionOf(i)] += moved;
    }
}
