using System;
using System.Linq;

namespace Primordium;

// Geochemistry by depth (ROADMAP 3). A law of the generator, not of behaviour: every element gets a
// depth bias from the seed, and the strata, veins and vents are drawn from it. Nothing is assigned
// to bodies or strategies; whether a body ever needs what lies deep is up to the chemistry it runs.
//
// - DepthBias[e] ∈ [−1, 1] (hash of the seed): negative — the element keeps to the surface, positive —
//   to the depth. At least one element is deep (≥ P.DeepElementMin): the generator guarantees it.
// - A molecule's depth tendency is the bias of its atoms, prof(s) = Σ_e DepthBias[e]·Atoms[s,e]/atoms(s).
//   A stratum `d` levels under the top of its column draws its molecule with the weight
//   base(s, rank)·exp(prof(s)·(d − DepthMid)/DepthScale): base is the old choice by bond strength
//   (pressure and correlated domains — now a soft window instead of one species), the exponent is
//   the depth profile. The draw uses a correlated 3-D field, so strata stay seams and lenses.
// - Veins: where another correlated field (thin in z: bands) exceeds VeinThreshold — a higher
//   threshold near the surface, so outcrops are rare — the deep element's molecules get another
//   e^(VeinGain·share) weight.
// - Vents bring up the interior: their ejecta are drawn by the profile at the crust's depth among the
//   energetic molecules (the old VentHigh) and the deepest ones.
// Conservation: strata are the initial endowment (budget(0)); vents are the external interior
// reservoir, counted in InteriorInput and the `vent` energy flow as before.
//
// A world keeps what it was made with (GeoOn, the biases and the scalars below; saved, version 6):
// changing the laws later shapes the next world only. GeoProfile 0 (or a save older than version 6):
// strata and vents exactly as before.
public sealed partial class World
{
    public readonly float[] DepthBias = new float[Chemistry.ElementCount];
    // Each column's height as the world was made (from the seed; derived, not saved): a stratum's depth
    // is counted from it, so is the depth a molecule was mined from.
    public readonly int[] Height0;
    public int DeepElement;   // the element with the largest bias (also defined with the profile off: the metrics compare it)
    public bool GeoOn;
    float geoScale = 4f, geoMid = 12f, geoVeinThr = 0.72f, geoVeinGain = 4f;
    float[] geoProf;          // per molecule: its depth tendency (derived)
    int[] ventCand;           // the vents' candidates (derived)
    float[] ventWeight;

    // Observation (bench, metrics): molecules torn out of rock by depth under the top of their column
    // (0, 1–2, 3–9, 10–29, 30+), the summed depth, the atoms mined and of them the deep element's.
    public readonly long[] GeoMined = new long[8];
    public const int GeoMinedDepthSum = 5, GeoMinedAtoms = 6, GeoMinedDeep = 7;

    // The biases from the seed alone (a hash: no random stream of the world is touched).
    void InitGeochem()
    {
        System.Threading.Tasks.Parallel.For(0, H, y => { for (int x = 0; x < W; x++) Height0[y * W + x] = GenHeight(x, y); });
        GeoOn = P.GeoProfile != 0;
        geoScale = P.DepthScale; geoMid = P.DepthMid; geoVeinThr = P.VeinThreshold; geoVeinGain = P.VeinGain;
        for (int e = 0; e < Chemistry.ElementCount; e++) DepthBias[e] = 2 * Hash32.F(Seed, e, 0x6E0C) - 1;
        // The volatile one — the atmosphere's gas — keeps its elements up: they are surface elements.
        for (int e = 0; e < Chemistry.ElementCount; e++) if (Chem.Atoms[Chem.Gas, e] > 0) DepthBias[e] = -MathF.Abs(DepthBias[e]);
        // The guaranteed deep element: of the others, the one the chemistry stores most of its releasable
        // energy in (energy each exothermic split frees, shared by its atoms) — reduced, energy-rich matter
        // lies deep, the surface is what is left once it has given its energy away. Ties: lower index.
        int deep = -1;
        double best = -1;
        for (int e = 0; e < Chemistry.ElementCount; e++)
        {
            if (Chem.Atoms[Chem.Gas, e] > 0) continue;
            double stored = 0;
            for (int s = 0; s < Chemistry.S; s++)
                if (Chem.SplitExo[s]) stored += (double)Chem.SplitEnergy(s) * Chem.Atoms[s, e] / Chem.AtomCount(s);
            if (stored > best) { best = stored; deep = e; }
        }
        float min = P.DeepElementMin;
        if (deep >= 0 && DepthBias[deep] < min) DepthBias[deep] = min + (1 - min) * Hash32.F(Seed, 17, 0x6E0D);
        // ... and the deepest: if another element drew a larger bias, they trade.
        for (int e = 0; deep >= 0 && e < Chemistry.ElementCount; e++)
            if (DepthBias[e] > DepthBias[deep]) (DepthBias[e], DepthBias[deep]) = (DepthBias[deep], DepthBias[e]);
        DeriveGeochem();
    }

    void DeriveGeochem()
    {
        DeepElement = 0;
        for (int e = 1; e < Chemistry.ElementCount; e++) if (DepthBias[e] > DepthBias[DeepElement]) DeepElement = e;
        geoProf = new float[Chemistry.S];
        for (int s = 0; s < Chemistry.S; s++)
        {
            float p = 0;
            for (int e = 0; e < Chemistry.ElementCount; e++) p += DepthBias[e] * Chem.Atoms[s, e];
            geoProf[s] = p / Chem.AtomCount(s);
        }
        // Vents: the energetic molecules and the deepest ones, weighed by the profile at the crust's depth.
        var cand = Chem.VentHigh.Concat(Enumerable.Range(0, Chemistry.S).OrderByDescending(s => geoProf[s]).ThenBy(s => s).Take(8)).Distinct().ToArray();
        ventCand = cand;
        ventWeight = new float[cand.Length];
        float total = 0;
        for (int k = 0; k < cand.Length; k++) total += ventWeight[k] = MathF.Exp(Math.Clamp(geoProf[cand[k]] * (Crust - geoMid) / geoScale, -30f, 30f));
        for (int k = 0; k < cand.Length; k++) ventWeight[k] /= total;
    }

    public float GeoProf(int s) => geoProf[s];

    // The molecule a vent lays, from a uniform number u ∈ [0, 1).
    public int VentMolecule(double u)
    {
        if (!GeoOn) return Chem.VentHigh[Math.Min(Chem.VentHigh.Length - 1, (int)(u * Chem.VentHigh.Length))];
        for (int k = 0; k < ventCand.Length - 1; k++)
        {
            u -= ventWeight[k];
            if (u < 0) return ventCand[k];
        }
        return ventCand[^1];
    }

    // The strata with the profile (GenerateTerrain calls it per column when GeoOn).
    sealed class GeoTables
    {
        public const int Bins = 64;
        public float[] Base;    // [bin * S + s]: the old choice by bond as a soft window over the sorted species
        public float[] Depth;   // [d * S + s]
        public float[] Vein;    // [s]
        public int Ns;
    }
    GeoTables geoTables;

    GeoTables BuildGeoTables(int[] species, int ns)
    {
        int S = Chemistry.S;
        var t = new GeoTables { Base = new float[GeoTables.Bins * S], Depth = new float[Z * S], Vein = new float[S], Ns = ns };
        var pos = new int[S];
        for (int k = 0; k < S; k++) pos[species[k]] = k;
        for (int b = 0; b < GeoTables.Bins; b++)
        {
            float at = (b + 0.5f) / GeoTables.Bins * S;
            for (int s = 0; s < S; s++)
            {
                float x = (pos[s] + 0.5f - at) / 2f;
                t.Base[b * S + s] = MathF.Exp(-0.5f * x * x);
            }
        }
        for (int d = 0; d < Z; d++)
            for (int s = 0; s < S; s++)
                t.Depth[d * S + s] = MathF.Exp(Math.Clamp(geoProf[s] * (d - geoMid) / geoScale, -30f, 30f));
        for (int s = 0; s < S; s++)
            t.Vein[s] = MathF.Exp(geoVeinGain * Chem.Atoms[s, DeepElement] / (float)Chem.AtomCount(s));
        return t;
    }

    // Is (x, y, z) `d` levels under its column's top inside a vein? Thin bands (stretched in x, y);
    // the threshold rises towards the surface, so a vein reaches it only now and then.
    public bool InVein(int x, int y, int z, int d)
    {
        int ns = unchecked(Seed * 31 + 7);
        float thr = geoVeinThr + (1 - geoVeinThr) * 0.6f * Math.Max(0f, 1 - d / Math.Max(1f, geoMid));
        return Noise.Value3(ns + 8, x / 8f, y / 8f, z / 1.5f, W / 8) > thr;
    }

    int GeoPick(int x, int y, int z, int d, float rank, float[] w)
    {
        var t = geoTables;
        int S = Chemistry.S, bin = Math.Min(GeoTables.Bins - 1, (int)(rank * GeoTables.Bins));
        bool vein = InVein(x, y, z, d);
        float total = 0;
        int db = Math.Min(d, Z - 1) * S, bb = bin * S;
        for (int s = 0; s < S; s++)
        {
            float v = t.Base[bb + s] * t.Depth[db + s];
            if (vein) v *= t.Vein[s];
            w[s] = v; total += v;
        }
        // A correlated draw: neighbouring voxels pick alike (seams), stretched to cover [0, 1).
        float u = Math.Clamp((Noise.Value3(t.Ns + 9, x / 8f, y / 8f, z / 3f, W / 8) - 0.5f) * 1.8f + 0.5f, 0f, 0.9999f) * total;
        for (int s = 0; s < S; s++)
        {
            u -= w[s];
            if (u < 0) return s;
        }
        return S - 1;
    }

    // The deep element's share of the atoms in a block (view; 0 for air and bedrock).
    public float VoxelDeepShare(int v)
    {
        byte m = Mat[v];
        if (m < 2) return 0;
        if (!Mixed(v, out var counts)) return Chem.Atoms[m - 2, DeepElement] / (float)Chem.AtomCount(m - 2);
        float deep = 0, all = 0;
        for (int s = 0; s < Chemistry.S; s++)
        {
            int n = counts[s];
            if (n == 0) continue;
            deep += n * Chem.Atoms[s, DeepElement]; all += n * Chem.AtomCount(s);
        }
        return all > 0 ? deep / all : 0;
    }

    // Share of the deep element in the atoms living bodies hold (observation).
    public double DeepShareOfBodies()
    {
        double deep = 0, all = 0;
        foreach (var a in Agents)
        {
            if (a.Dead) continue;
            for (int s = 0; s < Chemistry.S; s++)
            {
                int n = a.Inv[s];
                if (n == 0) continue;
                deep += (double)n * Chem.Atoms[s, DeepElement];
                all += (double)n * Chem.AtomCount(s);
            }
        }
        return all > 0 ? deep / all : 0;
    }

    void NoteMined(int s, int depth)
    {
        var g = cur?.GeoMined ?? GeoMined;
        int bin = depth <= 0 ? 0 : depth < 3 ? 1 : depth < 10 ? 2 : depth < 30 ? 3 : 4;
        g[bin]++;
        g[GeoMinedDepthSum] += Math.Max(0, depth);
        g[GeoMinedAtoms] += Chem.AtomCount(s);
        g[GeoMinedDeep] += Chem.Atoms[s, DeepElement];
    }

    public static readonly string[] GeoNames = { "deep_share", "mined", "mined_depth_mean", "mined_deep3", "mined_deep_atoms" };

    // deep_share: the deep element's share of atoms in living bodies; mined: molecules torn out of rock so
    // far; mined_depth_mean: their mean depth under the column's top; mined_deep3: share of them from 3+
    // levels down; mined_deep_atoms: the deep element's share of the atoms mined.
    public double[] GeoCensus()
    {
        var v = new double[GeoNames.Length];
        v[0] = DeepShareOfBodies();
        long n = 0;
        for (int k = 0; k < 5; k++) n += GeoMined[k];
        v[1] = n;
        if (n > 0)
        {
            v[2] = GeoMined[GeoMinedDepthSum] / (double)n;
            v[3] = (GeoMined[2] + GeoMined[3] + GeoMined[4]) / (double)n;
        }
        v[4] = GeoMined[GeoMinedAtoms] > 0 ? GeoMined[GeoMinedDeep] / (double)GeoMined[GeoMinedAtoms] : 0;
        return v;
    }

    // ---- save (version 6): its own block after the cave climate ----
    // Before version 6 the world was made without the profile: GeoOn false, biases from the seed for the
    // metrics, no counts.
    void SyncGeochem(Sync s)
    {
        if (s.Version < 7)
        {
            if (s.Reading) { InitGeochem(); GeoOn = false; Array.Clear(GeoMined); }
            return;
        }
        s.V(ref GeoOn);
        s.A<float>(DepthBias);
        s.V(ref geoScale); s.V(ref geoMid); s.V(ref geoVeinThr); s.V(ref geoVeinGain);
        s.A<long>(GeoMined);
        if (s.Reading) DeriveGeochem();
    }
}
