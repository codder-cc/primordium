using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Primordium;

public sealed class Strike
{
    public int X, Y, R;
    public long T;
}

// Climate: temperature in °C with inertia (climate by latitude and season plus the daily swing),
// clouds drifting with the wind, a closed water cycle — evaporation → moisture in the air → rain or
// snow → runoff into lakes and seas → ice in the cold — and now and then a mutagenic strike from space.
public sealed partial class World
{
    public readonly float[] Water, Ice, Snow, Cloud, Rain;
    public float Moisture, RainSum;
    public bool AutoStrikes = true;
    public readonly List<Strike> Strikes = new();
    public int StrikeCount;
    readonly float[] climRow, flowOut, rowSum;
    readonly float[] heatIn;   // energy dissipated by bodies in each cell since the last env step
    public readonly float[] BodyHeat;   // for the view: what bodies dissipated per env step, smoothed
    public readonly float[] DeathMap;   // for the view: recent deaths per cell, fading
    long nextStrike;

    public bool Submerged(int i) => Water[i] >= P.SwimDepth;

    void InitWater()
    {
        var hs = (int[])Height.Clone();
        Array.Sort(hs);
        int sea = hs[(int)(N * P.SeaShare)];
        double tot = 0;
        for (int i = 0; i < N; i++)
        {
            Water[i] = Math.Max(0, sea + 0.5f - Height[i]);
            tot += Water[i];
        }
        Moisture = (float)(tot * 0.01);
        nextStrike = StrikeGap(P.StrikeMin + mainRng.Next(P.StrikeMax - P.StrikeMin));
    }

    // Ticks to the next strike: as drawn on the default world, longer on a smaller one (the same strikes per area).
    long StrikeGap(int ticks) => N == RefN ? ticks : (long)Math.Round((double)ticks * RefN / N);

    // Clouds are a noise field blown eastwards; there are more of them at the equator and fewer
    // over the dry belts. Where they are thick enough, it rains (or snows).
    void UpdateClouds()
    {
        float t = Tick * 0.00012f, asp = H / (float)W, hydro = P.TranspHydro;
        bool sky = TranspLaw, dry = dryOn;
        int ns = Seed * 31 + 11;
        Parallel.For(0, H, y =>
        {
            float v = y / (float)H, wet = Wet(v);
            float rain = 0;
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                float u = x / (float)W;
                float n = Noise.Fbm(ns, u - t, v + 0.04f * MathF.Sin(t * 3 + u * 6.283f), 3, 4, asp);
                float c = Smooth(0.36f, 0.66f, n * wet);
                if (dry) c *= 1 - dryW[i];   // a drought (World.ClimateCycles): a blocking high clears the sky
                Cloud[i] = c;
                Rain[i] = Math.Max(0, c - 0.55f) * 2.2f;
                // Clear air rains less (World.Sky): the same moisture falls elsewhere.
                if (sky && Rain[i] > 0) Rain[i] *= Math.Max(0, 1 - hydro * (TranspRel(i) - 1));
                rain += Rain[i];
            }
            rowSum[y] = rain;
        });
        float sum = 0;
        for (int y = 0; y < H; y++) sum += rowSum[y];
        RainSum = sum;
    }

    float TempEq(int i) => TempEq(i, i / W);

    float TempEq(int i, int y)   // y: the cell's row
    {
        float t = climRow[y] + P.TDay * (Light[i] - 0.25f) - Lapse * (Height[i] - LapseBase) + 35f * ventHeat[i] - 4f * Math.Min(1f, Snow[i] * 3);
        return dryOn ? t + dryT[i] : t;   // a drought is warmer (World.ClimateCycles)
    }

    // Reaction speed roughly doubles every 15 °C.
    public static float TempFactor(float t) => Math.Clamp(MathF.Pow(2f, (t - 15f) / 15f), 0.25f, 3f);

    void UpdateClimate()
    {
        FlushedHeat();
        if (climOn) StepClimateState();   // glaciation, ash, droughts, the chronicle of epochs (World.ClimateCycles)
        Parallel.For(0, H, y =>
        {
            int W = this.W;   // (hot loops read the size into locals)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                bool wet = Water[i] > 0.5f;
                float k = wet ? P.TRelaxWater : P.TRelax;   // water keeps its warmth
                // What bodies under a roof shed warms the cave air instead (World.Cave; 0 with the law off).
                Temp[i] += (heatIn[i] - caveHeatIn[i]) * P.HeatToTemp * (wet ? 0.25f : 1f) + (TempEq(i, y) - Temp[i]) * k;
                BodyHeat[i] += (heatIn[i] - BodyHeat[i]) * 0.2f;
                DeathMap[i] *= 0.985f;
                heatIn[i] = 0;
            }
        });
        Parallel.For(0, H, y =>
        {
            int W = this.W;
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x, b = i * 4;
                tmp[i] = Temp[i] + 0.1f * (Temp[nb[b]] + Temp[nb[b + 1]] + Temp[nb[b + 2]] + Temp[nb[b + 3]] - 4 * Temp[i]);
            }
        });
        Array.Copy(tmp, Temp, N);
        UpdateCaveClimate();
        Hydro();
    }

    void Hydro()
    {
        float rainNow = RainSum > 1e-3f ? Moisture * P.RainShare : 0, perRain = rainNow / Math.Max(1e-3f, RainSum);
        bool sky = TranspLaw, floats = P.IceFloat != 0;
        float hydro = P.TranspHydro, ins = P.IceInsulation;
        Parallel.For(0, H, y =>
        {
            float ev = 0;
            int W = this.W;
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                float T = Temp[i], add = Rain[i] * perRain;
                if (add > 0) { if (T < 0) Snow[i] += add; else Water[i] += add; }
                if (Snow[i] > 0 && T > 0) { float m = Math.Min(Snow[i], 0.003f * T); Snow[i] -= m; Water[i] += m; }
                if (T < -1 && Water[i] > 0.01f)
                {
                    // Floating ice (IceFloat) freezes from the top: the cold reaches the top `ins` blocks
                    // of open water, and the ice already there keeps it from the water below.
                    float f = floats ? 0.02f * Math.Min(Water[i], ins) * ins / (ins + Ice[i]) : Water[i] * 0.02f;
                    Water[i] -= f; Ice[i] += f;
                }
                else if (T > 1 && Ice[i] > 0) { float m = Math.Min(Ice[i], 0.003f * T); Ice[i] -= m; Water[i] += m; }
                if (Water[i] > 0 && T > -5)
                {
                    float e = Math.Min(Water[i], P.Evap * (T + 5) / 25f * Math.Min(1f, Water[i] / 0.3f + 0.1f));
                    if (sky) e = Math.Min(Water[i], e * Math.Max(0, 1 + hydro * (TranspRel(i) - 1)));   // clear air dries more (World.Sky)
                    Water[i] -= e;
                    ev += e;
                }
            }
            rowSum[y] = ev;
        });
        float evap = 0;
        for (int y = 0; y < H; y++) evap += rowSum[y];
        Moisture = Math.Max(0, Moisture + evap - rainNow);
        Flow();
        CaveFlow();   // into and out of the caves, between them (World.Waterways)
    }

    // Water runs to neighbours whose surface (ground + ice + water) is lower. What runs through a
    // column is its current (World.Waterways): the net flow through its faces over its depth.
    void Flow()
    {
        bool currents = P.Currents != 0;
        if (!currents && currentsOn) { Array.Clear(CurX); Array.Clear(CurY); }
        currentsOn = currents;
        float perTick = 0.5f / P.EnvEvery, deep = P.SwimDepth;
        Parallel.For(0, H, y =>
        {
            int W = this.W;
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x, b = i * 4;
                float w = Water[i];
                if (w < 1e-4f) { flowOut[b] = flowOut[b + 1] = flowOut[b + 2] = flowOut[b + 3] = 0; continue; }
                float s = Height[i] + Ice[i] + w;
                float o0 = Out(s, nb[b], i), o1 = Out(s, nb[b + 1], i), o2 = Out(s, nb[b + 2], i), o3 = Out(s, nb[b + 3], i);
                float sum = o0 + o1 + o2 + o3, k = sum > w ? w / sum : 1;
                flowOut[b] = o0 * k; flowOut[b + 1] = o1 * k; flowOut[b + 2] = o2 * k; flowOut[b + 3] = o3 * k;
            }
        });
        Parallel.For(0, H, y =>
        {
            int W = this.W;
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x, b = i * 4;
                float w0 = Water[i], w = w0 - (flowOut[b] + flowOut[b + 1] + flowOut[b + 2] + flowOut[b + 3]);
                float in0 = 0, in1 = 0, in2 = 0, in3 = 0;
                for (int d = 0; d < 4; d++)
                {
                    int j = nb[b + d];
                    if (j == i) continue;
                    float q = flowOut[j * 4 + ((d + 2) & 3)];
                    w += q;
                    if (d == 0) in0 = q; else if (d == 1) in1 = q; else if (d == 2) in2 = q; else in3 = q;
                }
                Water[i] = Math.Max(0, w);
                if (currents)
                {
                    // Through the east face out minus in, through the west face in minus out: the mean of
                    // the two over the depth is the water's speed east (cells a tick); the same north.
                    float k = perTick / Math.Max(w0, deep);
                    CurX[i] = (flowOut[b] - in0 + in2 - flowOut[b + 2]) * k;
                    CurY[i] = (flowOut[b + 1] - in1 + in3 - flowOut[b + 3]) * k;
                }
            }
        });
    }

    float Out(float s, int j, int i) => j == i ? 0 : Math.Max(0, (s - (Height[j] + Ice[j] + Water[j])) * 0.2f);

    void MaybeStrike()
    {
        if (!AutoStrikes || Tick < nextStrike) return;
        nextStrike = Tick + StrikeGap(mainRng.Next(P.StrikeMin, P.StrikeMax));
        StrikeAt(mainRng.Next(W), mainRng.Next(H / 10, H - H / 10), 5 + mainRng.Next(8));
    }

    // A mutagenic beam from space: genomes in the area get pieces knocked out, scrambled or random
    // code inserted; remains lying around are reshuffled into random molecules; the centre is
    // blasted into a crater (the rock there is vaporised). Survivors may end up with something new.
    public void StrikeAt(int cx, int cy, int r)
    {
        var rng = mainRng;
        Strikes.Add(new Strike { X = cx, Y = cy, R = r, T = Tick });
        if (Strikes.Count > 12) Strikes.RemoveAt(0);
        StrikeCount++;
        int struck = 0;
        for (int dy = -r, rx = AroundX(r); dy <= r; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= H) continue;
            for (int dx = -rx; dx <= rx; dx++)
            {
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d > r) continue;
                int i = y * W + ((cx + dx) % W + W) % W;
                float k = 1 - d / r;
                for (int s = 0; s < Chemistry.S; s++)
                {
                    Qty m = C[s][i] * (0.2f * k);
                    if (m <= 0) continue;
                    C[s][i] -= m;
                    // Irradiation excites the same formula; it cannot transmute elements.
                    int product = Chem.PhotoUp[s];
                    C[product >= 0 ? product : s][i] += m;
                    if (product >= 0) Flows[FStrike] += (double)m * (Chem.E[product] - Chem.E[s]);
                }
                for (var a = Head[i]; a != null; a = a.NextInCell)
                {
                    // Rock above stops the beam as it shelters from the climate (World.Cave).
                    double hit = 0.3 + 0.7 * k;
                    if (CaveLaw) hit *= 1 - Cover(i, a.Z);
                    if (rng.NextDouble() < hit) { Irradiate(a, k, rng); struck++; }
                }
                int h = Height[i];
                if (d <= r / 3f && h > 2 && Mat[i * Z + h - 1] != Chemistry.Bedrock)
                {
                    int v = i * Z + h - 1;
                    SpillVoxel(v);
                }
            }
        }
        // SpillVoxel already invalidated each affected column.
        Add(EvType.Climate, Loc.Both($"strike from orbit at ({cx}, {cy}), radius {r}: {struck} {EnPlural(struck, "body", "bodies")} irradiated",
            $"удар с орбиты ({cx}, {cy}), радиус {r}: облучено {struck} {Plural(struck, "тело", "тела", "тел")}"), null, struck, struck >= 50, null, cx, cy);
    }

    void Irradiate(Agent a, float k, SimRng rng)
    {
        var g = new List<byte>(a.G);
        var p = new List<byte>(a.Prot);
        int len = 1 + rng.Next(8), at = rng.Next(g.Count);
        switch (rng.Next(3))
        {
            case 0:
                if (g.Count - len < Genome.MinLen) break;
                len = Math.Min(len, g.Count - at);
                g.RemoveRange(at, len);
                p.RemoveRange(at, len);
                break;
            case 1:
                for (int j = 0; j < len; j++) { int q = (at + j) % g.Count; g[q] = (byte)rng.Next(256); p[q] = 0; }
                break;
            default:
                if (g.Count + len > Genome.MaxLen) break;
                for (int j = 0; j < len; j++) { g.Insert(at, (byte)rng.Next(256)); p.Insert(at, 0); }
                Dissipate(a, 0.5f * len);
                break;
        }
        Dissipate(a, 4 * k);
        a.SetGenome(g.ToArray(), p.ToArray());
        a.NStruck++;
        BioNote(a, Tick, BioKind.Struck, 0, a.G.Length);
        AddFlash(a.X, a.Y, FlashStrike);
    }

    public sealed class Climate
    {
        public float MeanT, MinT, MaxT, WaterShare, IceShare, SnowShare, RainShare;
    }

    public Climate TakeClimate()
    {
        var c = new Climate { MinT = float.MaxValue, MaxT = float.MinValue };
        double t = 0;
        int water = 0, ice = 0, snow = 0, rain = 0;
        for (int i = 0; i < N; i++)
        {
            float v = Temp[i];
            t += v;
            if (v < c.MinT) c.MinT = v;
            if (v > c.MaxT) c.MaxT = v;
            if (Water[i] >= P.SwimDepth) water++;
            if (Ice[i] > 0.1f) ice++;
            if (Snow[i] > 0.05f) snow++;
            if (Rain[i] > 0) rain++;
        }
        c.MeanT = (float)(t / N);
        c.WaterShare = water / (float)N;
        c.IceShare = ice / (float)N;
        c.SnowShare = snow / (float)N;
        c.RainShare = rain / (float)N;
        return c;
    }
}
