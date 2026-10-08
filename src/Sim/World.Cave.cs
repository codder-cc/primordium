using System;
using System.Threading.Tasks;

namespace Primordium;

// Climate of caves and depth (ROADMAP 2.1). A law of the environment, not of behaviour: rock above a
// body stands between it and the sky.
// - Roof: solid blocks above the body's level in its column (Height is only an upper bound: voids
//   are not counted). 0 on the surface and for swimmers (water lies on top of a column).
// - Cover f = 1 − e^(−roof/CaveDepthK): under 1 block ~28% (CaveDepthK 3), under 5 ~81%, under 10 ~96%.
// - Tcave = Tmean[cell] + GeoGrad·(levels below the column's top) + CaveWarm[cell]: the column's slow
//   mean temperature (an EMA of Temp over TmeanTau ticks, about a year), the warmth of the interior,
//   and what bodies under a roof there have shed.
// - Felt temperature T = lerp(Temp[cell], Tcave, f). Body temperature (FootTemp), the `temp` sensor
//   and the orbital beam (scaled by 1 − f) use it. Light — and with it UV — is stopped by any roof
//   already (AgentLight); snow acts on bodies only through the surface temperature and light.
// - Body heat under a roof: the share f of what a body sheds goes into its column's cave air
//   (caveHeatIn → CaveWarm, relaxing at TRelax like dry land) instead of the surface temperature.
//   heatIn still receives all of it, so the energy ledger is unchanged (heat is a sink).
// P.CaveClimate = 0 switches all of it off; the code then runs the old expressions bit for bit.
// Everything here is read in the agent phase (cells of the body itself) or written between ticks,
// except caveHeatIn, which a body writes only in its own cells, like heatIn.
public sealed partial class World
{
    public readonly float[] Tmean = new float[N];      // slow mean of Temp (°C), the base of the cave climate
    public readonly float[] CaveWarm = new float[N];   // °C the cave air of a column is warmed by bodies under its roof
    readonly float[] caveHeatIn = new float[N];        // of heatIn: what bodies under a roof shed since the last env step (× their cover)

    public static bool CaveLaw => P.CaveClimate != 0;

    // Solid blocks above level z in column c (0 at or above the top). Exact: voids are skipped.
    public int Roof(int c, int z)
    {
        int h = Height[c], n = 0, b = c * Z;
        for (int k = Math.Max(0, z + 1); k < h; k++) if (Mat[b + k] != Chemistry.Air) n++;
        return n;
    }

    // How much of the cave climate a body at level z of column c feels (0 on the surface).
    public float Cover(int c, int z)
    {
        if (z >= Height[c]) return 0;
        int roof = Roof(c, z);
        return roof == 0 ? 0 : 1 - MathF.Exp(-roof / P.CaveDepthK);
    }

    // The climate deep under a roof at level z of column c.
    public float CaveTemp(int c, int z) => Tmean[c] + P.GeoGrad * Math.Max(0, Height[c] - 1 - z) + CaveWarm[c];

    // What a body at level z of column c feels with the law on (the surface temperature with it off).
    public float LocalTemp(int c, int z)
    {
        float surf = Temp[c];
        if (!CaveLaw || z >= Height[c]) return surf;
        int roof = Roof(c, z);
        if (roof == 0) return surf;
        float f = 1 - MathF.Exp(-roof / P.CaveDepthK);
        return surf + (CaveTemp(c, z) - surf) * f;
    }

    // Between ticks, in UpdateClimate after Temp moved: the cave air keeps or loses the bodies' warmth,
    // the mean follows Temp (an EMA over TmeanTau ticks).
    void UpdateCaveClimate()
    {
        float k = Math.Min(1f, P.EnvEvery / (float)Math.Max(1, P.TmeanTau));
        float toTemp = P.HeatToTemp, relax = P.TRelax;
        Parallel.For(0, H, y =>
        {
            for (int i = y * W, end = i + W; i < end; i++)
            {
                Tmean[i] += (Temp[i] - Tmean[i]) * k;
                CaveWarm[i] += caveHeatIn[i] * toTemp - CaveWarm[i] * relax;
                caveHeatIn[i] = 0;
            }
        });
    }

    // The mean starts as the climate's own year-round normal: TempEq with the latitude's climate and
    // daylight averaged over a year of seasons and a day of sun positions (clouds and mountain shadows
    // left out), so a young world's caves are not stuck in the season it was made in. Also for a world
    // loaded from a file without the cave block.
    void InitCaveClimate()
    {
        const int Seasons = 24, Hours = 48;
        if (InsolLaw) EnsureInsolNorms();
        Parallel.For(0, H, y =>
        {
            float lat = (0.5f - (y + 0.5f) / H) * MathF.PI * 0.92f, sl = MathF.Sin(lat), cl = MathF.Cos(lat);
            float clim = 0, light = 0;
            for (int j = 0; j < Seasons; j++)
            {
                float decl = P.Tilt * MathF.Sin(2 * MathF.PI * (j + 0.5f) / Seasons), sd = MathF.Sin(decl), cd = MathF.Cos(decl);
                clim += ClimateOf(y, decl);   // the law's climate of the latitude (World.Sky)
                for (int h = 0; h < Hours; h++) light += Insol(sl * sd + cl * cd * MathF.Cos(2 * MathF.PI * (h + 0.5f) / Hours));
            }
            clim /= Seasons; light /= Seasons * Hours;
            for (int i = y * W, end = i + W; i < end; i++)
                Tmean[i] = clim + P.TDay * (light - 0.25f) - Lapse * (Height[i] - LapseBase) + 35f * ventHeat[i] - 4f * Math.Min(1f, Snow[i] * 3);
        });
        Array.Clear(CaveWarm);
        Array.Clear(caveHeatIn);
    }

    // ---- observation: where bodies live (bench metrics, no effect on the world) ----

    public static readonly string[] CaveNames =
    {
        "cave1", "cave3", "depth_mean", "depth_max", "cave_voids", "polar_share", "polar_cave1", "mount_share", "mount_cave1",
    };

    // cave1/cave3: share of living bodies with ≥ 1 / ≥ 3 solid blocks above them; depth_mean/max: levels
    // below their column's top (0 on the surface); cave_voids: air voxels under a roof anywhere (dug or
    // fallen-in room, thousands); polar_share: bodies in the polar bands (|latitude| beyond 60% of the
    // way to the pole), polar_cave1: of them under a roof; mount_share / mount_cave1: the same for the
    // highest fifth of the columns.
    public double[] CaveCensus()
    {
        var v = new double[CaveNames.Length];
        var hs = (int[])Height.Clone();
        Array.Sort(hs);
        int mountain = hs[(int)(N * 0.8)];
        long voids = 0;
        for (int c = 0; c < N; c++)
        {
            if (!HasCavity[c]) continue;
            int b = c * Z;
            for (int z = 0; z < Height[c]; z++) if (Mat[b + z] == Chemistry.Air) voids++;
        }
        v[4] = voids;
        int pop = 0, c1 = 0, c3 = 0, polar = 0, polarCave = 0, mount = 0, mountCave = 0, deepest = 0;
        double depth = 0;
        foreach (var a in Agents)
        {
            if (a.Dead) continue;
            pop++;
            int c = a.Y * W + a.X, roof = Roof(c, a.Z), d = Math.Max(0, Height[c] - 1 - a.Z);
            if (roof >= 1) c1++;
            if (roof >= 3) c3++;
            if (roof >= 1) { depth += d; deepest = Math.Max(deepest, d); }
            float lat = Math.Abs(a.Y + 0.5f - H / 2f) / (H / 2f);
            if (lat > 0.6f) { polar++; if (roof >= 1) polarCave++; }
            if (Height[c] >= mountain) { mount++; if (roof >= 1) mountCave++; }
        }
        if (pop > 0)
        {
            v[0] = c1 / (double)pop; v[1] = c3 / (double)pop; v[2] = depth / pop; v[3] = deepest;
            v[5] = polar / (double)pop; v[7] = mount / (double)pop;
        }
        v[6] = polar > 0 ? polarCave / (double)polar : 0;
        v[8] = mount > 0 ? mountCave / (double)mount : 0;
        return v;
    }
}
