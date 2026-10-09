using System;
using System.Diagnostics;
using System.Linq;

namespace Primordium;

public sealed partial class World
{
    // Geochemistry by depth (World.Geochem, ROADMAP 3). Worlds without life, seeds 1–12:
    // - every world has a deep element (bias ≥ DeepElementMin, the largest); the gas's elements are not deep;
    // - the deep element's share of rock atoms grows from the surface to 30 levels down and is well above it
//   60 levels down (≥ 1.5×: bound in a compound with a surface element it comes up too), and
    //   above it again in veins; vents bring it up (its share of the ejecta is above the surface's);
    // - the profile off: no biases are used — the strata are the old choice by bond (a world made with the
    //   law off has the same rock as one made by the old generator: checked against main by hashes, see
    //   CHANGELOG), and generating twice gives the same rock.
    public static void GeochemRegression()
    {
        ParamRegistry.ResetDefaults();
        double worstRatio = double.MaxValue, veinRatio = double.MaxValue, ventRatio = double.MaxValue;
        var watch = Stopwatch.StartNew();
        for (int seed = 1; seed <= 12; seed++)
        {
            var w = new World(seed, 0, false);
            Require(w.GeoOn, "profile on by default");
            int d = w.DeepElement;
            Require(w.DepthBias[d] >= P.DeepElementMin - 1e-6 && w.DepthBias.All(b => b <= w.DepthBias[d]), $"seed {seed}: no deep element");
            for (int e = 0; e < Chemistry.ElementCount; e++)
                if (w.Chem.Atoms[w.Chem.Gas, e] > 0) Require(w.DepthBias[e] <= 0, $"seed {seed}: the gas's element is deep");
            double Share(double[] at) => at[d] / Math.Max(1e-9, at.Sum());
            double top = Share(w.StrataAtoms(0)), mid = Share(w.StrataAtoms(30)), deep = Share(w.StrataAtoms(60));
            worstRatio = Math.Min(worstRatio, deep / Math.Max(top, 1e-3));
            Require(mid > top && deep > 1.5 * top + 0.05, $"seed {seed}: deep element {top:P1} at the surface, {mid:P1} 30 and {deep:P1} 60 levels down");
            // Veins: rock in veins 5–15 levels down against the rest at the same depths.
            double vIn = 0, vAll = 0, oIn = 0, oAll = 0;
            for (int c = 0; c < w.N; c += 7)
                for (int k = 5; k <= 15; k++)
                {
                    int z = w.Height0[c] - 1 - k, v = c * w.Z + z;
                    if (z < 2 || w.Mat[v] < 2) continue;
                    int s = w.Mat[v] - 2;
                    bool vein = w.InVein(c % w.W, c / w.W, z, k);
                    double n = w.Chem.Atoms[s, d], all = w.Chem.AtomCount(s);
                    if (vein) { vIn += n; vAll += all; } else { oIn += n; oAll += all; }
                }
            Require(vAll > 0, $"seed {seed}: no veins 5–15 levels down");
            veinRatio = Math.Min(veinRatio, (vIn / vAll) / Math.Max(1e-3, oIn / oAll));
            Require(vIn / vAll > oIn / oAll, $"seed {seed}: veins no richer in the deep element ({vIn / vAll:P1} vs {oIn / oAll:P1})");
            double ventDeep = 0, ventAll = 0;
            for (int k = 0; k < 2000; k++)
            {
                int s = w.VentMolecule(Hash32.F(k, seed));
                ventDeep += w.Chem.Atoms[s, d]; ventAll += w.Chem.AtomCount(s);
            }
            ventRatio = Math.Min(ventRatio, ventDeep / ventAll / Math.Max(top, 1e-3));
            Require(ventDeep / ventAll > top, $"seed {seed}: vents bring less of the deep element than the surface has");
        }
        double onMs = watch.Elapsed.TotalMilliseconds / 12;
        // The law off: the biases are still drawn (metrics) but unused; the same rock twice.
        P.GeoProfile = 0;
        watch.Restart();
        var a = new World(5, 0, false);
        double offMs = watch.Elapsed.TotalMilliseconds;
        var b = new World(5, 0, false);
        Require(!a.GeoOn && a.Mat.SequenceEqual(b.Mat) && a.Units.SequenceEqual(b.Units), "profile off: generation not reproducible");
        P.GeoProfile = 1;
        var c1 = new World(5, 0, false);
        Require(!c1.Mat.SequenceEqual(a.Mat), "profile on and off give the same rock");
        ParamRegistry.ResetDefaults();
        Console.WriteLine($"PASS geochem: 12 worlds have a deep element; 60 levels down it is ≥ {worstRatio:F1}× its surface share, veins ≥ {veinRatio:F1}× the rock around, vents ≥ {ventRatio:F1}× the surface; a world takes {onMs:F0} ms with the profile, {offMs:F0} without");
    }
}
