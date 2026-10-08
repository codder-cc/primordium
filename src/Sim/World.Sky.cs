using System;
using System.Threading;
using System.Threading.Tasks;

namespace Primordium;

// The sky (ROADMAP 1): laws of the environment, none of behaviour.
// - Insolation (1.1): power = max(0, sin elevation)^InsolExp — the cosine law — with a soft terminator;
//   the climate of a latitude follows the day's insolation sum, held back by the year's mean.
// - Transparency (1.2): a slowly drifting field over the latitudinal profile of the wet and dry belts
//   (the one the clouds follow), clearer high up; clear air evaporates more and rains less.
// - Shading (1.3): in a cell, bodies higher up — or, on one floor, bigger — catch the light first.
// - Eclipses (1.4): a moon on an inclined orbit from the seed; its shadow crosses the day side at some
//   new moons. A pure function of the tick: predictable.
// - Solar flares (1.5): the sun's activity swings over a cycle from the seed; flares come more often at
//   high activity, their power Pareto-distributed. The schedule is a pure function of the seed and the
//   tick (hashes, no random stream: switching flares on or off does not disturb any other draw). A flare
//   dose mutates (the UV path), wears proteins, costs energy and heats the body — heat from outside,
//   booked as the ledger's `flare` input.
// Every piece has its switch (P.Insolation, P.Transparency, P.ShadeK, P.Eclipses, P.Flares); with all of
// them off (and the old PhotonK) the world runs the old expressions bit for bit.
// In the agent phase only the body's own cell is read (Sun, Height, the cell's list for shading);
// flare counters are Interlocked; the rest runs between ticks.
public sealed partial class World
{
    public const int CauseFlare = 7;
    public int DeathsFlare;

    public static bool InsolLaw => P.Insolation != 0;
    public static bool TranspLaw => P.Transparency != 0;
    public static bool EclipseLaw => P.Eclipses != 0;
    public static bool FlareLaw => P.Flares != 0;

    // Direct light at the surface of each cell (the water's surface where there is water): the sun's
    // power after the sky, mountain shadows, ash, clouds, ice and snow — what photons accrue from and what
    // a flare hits. Light[i] is what is left of it at the floor. Saved (version 8).
    public readonly float[] Sun = new float[N];
    // Clear-sky transparency, TranspMin … 1 (1 everywhere with the law off). Saved (version 8): it depends
    // on the heights when it was last computed.
    public readonly float[] Transp = new float[N];
    public float TranspMean = 1;
    bool transpValid;

    public static float Latitude(int y) => (0.5f - (y + 0.5f) / H) * MathF.PI * 0.92f;
    public float DeclinationAt(double tick) => (float)(TiltAt(tick) * Math.Sin(2 * Math.PI * tick / P.DayLen / P.YearDays));   // the tilt of the climate cycles

    // ---- 1.1 insolation ----

    // The sun's power at a sine of elevation se (0 … 1).
    public static float Insol(float se)
    {
        if (!InsolLaw) return Smooth(-0.04f, 0.3f, se);
        float lo = P.TwilightLo, hi = Math.Max(P.TwilightHi, lo + 1e-3f), k = P.InsolExp;
        if (se >= hi) return k == 1 ? se : MathF.Pow(se, k);
        if (se <= lo) return 0;
        return Smooth(lo, hi, se) * (k == 1 ? hi : MathF.Pow(hi, k));
    }

    const int DayHours = 48, YearSeasons = 24;

    // The day's mean power at a latitude and declination (48 sun positions).
    public static float DailyInsol(float lat, float decl)
    {
        float sl = MathF.Sin(lat), cl = MathF.Cos(lat), sd = MathF.Sin(decl), cd = MathF.Cos(decl), sum = 0;
        for (int h = 0; h < DayHours; h++) sum += Insol(sl * sd + cl * cd * MathF.Cos(2 * MathF.PI * (h + 0.5f) / DayHours));
        return sum / DayHours;
    }

    // Share of the day with the sun above the horizon (exact: cos H0 = −tan φ·tan δ).
    public static float DayShare(float lat, float decl)
    {
        double c = -Math.Tan(lat) * Math.Tan(decl);
        if (c <= -1) return 1;
        if (c >= 1) return 0;
        return (float)(Math.Acos(c) / Math.PI);
    }

    // The sun's height at noon, degrees.
    public static float NoonElevation(float lat, float decl) => 90f - MathF.Abs(lat - decl) * 180f / MathF.PI;

    // The year's mean insolation per row and the equator's equinox day (the climate's yardstick):
    // derived from the laws, rebuilt when they change (and, with the climate cycles, when the tilt moved
    // by a step of 0.002 rad: the light update passes the tilt rounded to that).
    readonly float[] yearInsol = new float[H];
    float insolEq = 1;
    (float, float, float, float, int) insolKey = (float.NaN, 0, 0, 0, -1);
    void EnsureInsolNorms() => EnsureInsolNorms(P.Tilt);
    void EnsureInsolNorms(float tilt)
    {
        var key = (tilt, P.InsolExp, P.TwilightLo, P.TwilightHi, P.Insolation);
        if (key == insolKey) return;
        insolKey = key;
        insolEq = Math.Max(1e-4f, DailyInsol(0, 0));
        Parallel.For(0, H, y =>
        {
            float lat = Latitude(y), sum = 0;
            for (int j = 0; j < YearSeasons; j++) sum += DailyInsol(lat, tilt * MathF.Sin(2 * MathF.PI * (j + 0.5f) / YearSeasons));
            yearInsol[y] = sum / YearSeasons;
        });
    }

    // The climate (°C, before altitude, daylight and the rest of TempEq) of row y at declination decl.
    // With the law: from the day's insolation sum, held back by the year's mean (ClimInertia), relative to
    // the equator's equinox day, to the power ClimPow. Without: from the noon height, as before.
    float ClimateOf(int y, float decl)
    {
        float lat = Latitude(y);
        if (!InsolLaw) return P.TPole + (P.TEquator - P.TPole) * MathF.Pow(Math.Max(0f, MathF.Cos(lat - decl)), 1.3f);
        float q = ((1 - P.ClimInertia) * DailyInsol(lat, decl) + P.ClimInertia * yearInsol[y]) / insolEq;
        return P.TPole + (P.TEquator - P.TPole) * MathF.Pow(Math.Max(0f, q), P.ClimPow);
    }

    // ---- 1.2 transparency ----

    // The wet and dry belts by latitude (v = y/H): wet near the equator and around 60°, dry around 30°.
    // The clouds (UpdateClouds) and the transparency share this profile.
    static float Wet(float v)
    {
        float lat = MathF.Abs(v - 0.5f) * 2;
        float b1 = lat / 0.22f, b2 = (lat - 0.38f) / 0.12f, b3 = (lat - 0.66f) / 0.12f;
        return Math.Clamp(0.8f + 0.4f * MathF.Exp(-b1 * b1) - 0.45f * MathF.Exp(-b2 * b2) + 0.15f * MathF.Exp(-b3 * b3), 0.1f, 1.3f);
    }

    // Every LightEvery·8 ticks (and when the law is switched on): the belts, drifting coherent noise
    // (three octaves of value noise, moving east TranspDrift cells a day and slowly changing shape) and
    // the altitude above the planet's mean ground (so that the planet's mean does not depend on how high
    // a seed's land happens to lie).
    void UpdateTransparency()
    {
        transpValid = true;
        double day = (double)Tick / P.DayLen;
        int p = Math.Max(1, (int)MathF.Round(1 / P.TranspScale));
        float shift = (float)(P.TranspDrift * day / W % 1.0);
        float zt = (float)(P.TranspDrift * day / (W * P.TranspScale));
        float asp = H / (float)W, min = P.TranspMin, noise = P.TranspNoise, alt = P.TranspAlt;
        int seed = Seed * 53 + 977;
        float ground = MeanHeight();   // the altitude bonus counts from the planet's mean ground, not a fixed level
        // First the noise alone (into Transp), then centred on its planet mean: a few large patches
        // would otherwise make one seed's sky clearer than another's on the whole.
        Parallel.For(0, H, y =>
        {
            float v = y / (float)H;
            double row = 0;
            for (int x = 0; x < W; x++)
            {
                float u = x / (float)W - shift;
                u -= MathF.Floor(u);
                float n = 0, amp = 1, norm = 0;
                for (int o = 0; o < 3; o++)
                {
                    int q = p << o;
                    n += amp * Noise.Value3(seed + o * 101, u * q, v * q * asp, zt * (1 << o), q);
                    norm += amp; amp *= 0.5f;
                }
                Transp[y * W + x] = n / norm;
                row += n / norm;
            }
            rowSum[y] = (float)row;
        });
        double nsum = 0;
        for (int y = 0; y < H; y++) nsum += rowSum[y];
        float nmean = (float)(nsum / N);
        Parallel.For(0, H, y =>
        {
            float bas = 1 - 0.45f * Math.Clamp((Wet(y / (float)H) - 0.4f) / 0.8f, 0f, 1f);
            double row = 0;
            for (int i = y * W, end = i + W; i < end; i++)
            {
                float raw = bas + noise * (Transp[i] - nmean) * 2 + alt * (Height[i] - ground);
                Transp[i] = min + (1 - min) * Math.Clamp(raw, 0f, 1f);
                row += Transp[i];
            }
            rowSum[y] = (float)row;
        });
        double sum = 0;
        for (int y = 0; y < H; y++) sum += rowSum[y];
        TranspMean = (float)Math.Max(1e-3, sum / N);
    }

    // How much clearer than average the sky over a cell is (the water cycle's coupling): 1 with the law off.
    float TranspRel(int i) => TranspLaw ? Transp[i] / TranspMean : 1;

    // ---- 1.4 eclipses ----

    float MoonPeriodDays => P.MoonPeriod > 0 ? P.MoonPeriod : 3 + 6 * Hash32.F(Seed, 9101);
    float MoonTiltRad => P.MoonTilt > 0 ? P.MoonTilt : 0.2f + 0.25f * Hash32.F(Seed, 9102);
    // The nodes of the orbit turn: eclipse seasons come every half of NodeDays.
    float NodeDays => MoonPeriodDays * (3 + 3 * Hash32.F(Seed, 9103));

    // Where the moon's shadow falls at a tick (cell coordinates, x may be fractional), if it does. The sun
    // direction S and the moon's M (synodic phase φ behind the sun, latitude β = tilt·sin(φ + ν)); the
    // shadow axis through the moon, parallel to S, meets the unit sphere at q + √(1 − |q|²)·S, with
    // q = MoonDist·(M − (M·S)S). Only on the day side; |q| ≥ 1 — no eclipse.
    public bool EclipseAt(double tick, out float cx, out float cy)
    {
        cx = cy = -1;
        if (!EclipseLaw) return false;
        double day = tick / P.DayLen;
        double ls = 2 * Math.PI * (day % 1.0), decl = DeclinationAt(tick);
        double phi = 2 * Math.PI * (day / MoonPeriodDays + Hash32.F(Seed, 9104));
        double nu = 2 * Math.PI * (day / NodeDays + Hash32.F(Seed, 9105));
        double lm = ls - phi, dm = decl + MoonTiltRad * Math.Sin(phi + nu);
        double sx = Math.Cos(decl) * Math.Cos(ls), sy = Math.Cos(decl) * Math.Sin(ls), sz = Math.Sin(decl);
        double mx = Math.Cos(dm) * Math.Cos(lm), my = Math.Cos(dm) * Math.Sin(lm), mz = Math.Sin(dm);
        double ms = mx * sx + my * sy + mz * sz;
        if (ms <= 0) return false;
        double k = P.MoonDist, qx = k * (mx - ms * sx), qy = k * (my - ms * sy), qz = k * (mz - ms * sz);
        double q2 = qx * qx + qy * qy + qz * qz;
        if (q2 >= 1) return false;
        double r = Math.Sqrt(1 - q2), px = qx + r * sx, py = qy + r * sy, pz = qz + r * sz;
        double lat = Math.Asin(Math.Clamp(pz, -1, 1)), lon = Math.Atan2(py, px);
        cx = (float)(((lon / (2 * Math.PI) * W) % W + W) % W);   // the subsolar point is at x = SunX (cell x + 0.5)
        cy = (float)((0.5 - lat / (0.92 * Math.PI)) * H);
        return true;
    }

    // Light left at a cell centre by a shadow centred at (cx, cy).
    public static float EclipseShade(int x, int y, float cx, float cy)
    {
        float dx = MathF.Abs(x + 0.5f - cx);
        dx = MathF.Min(dx, W - dx);
        float dy = y + 0.5f - cy, R = P.EclipseR, d2 = dx * dx + dy * dy, outer = 1.3f * R;
        if (d2 >= outer * outer) return 1;
        float d = MathF.Sqrt(d2);
        return P.EclipseDepth + (1 - P.EclipseDepth) * Smooth(R, outer, d);
    }

    // The next tick at or after `from` when a shadow touches the planet (−1 within `days`), by steps of 8.
    public long NextEclipse(long from, int days = 400)
    {
        if (!EclipseLaw) return -1;
        for (long t = from, end = from + (long)days * P.DayLen; t < end; t += 8)
            if (EclipseAt(t, out _, out _)) return t;
        return -1;
    }

    public bool EclipseNow;            // the shadow at the last light update (view, HUD)
    public float EclipseX, EclipseY;
    public int EclipseCount;
    long eclipseStart = -1;            // the running eclipse (chronicle): its start and first and last centre
    float eclipseX0, eclipseY0, eclipseX1, eclipseY1;

    // In UpdateLight, between ticks: the shadow now, and the chronicle at its start and end.
    void StepEclipse()
    {
        EclipseNow = EclipseAt(Tick, out EclipseX, out EclipseY);
        if (EclipseNow && eclipseStart < 0)
        {
            eclipseStart = Tick; eclipseX0 = EclipseX; eclipseY0 = EclipseY;
            EclipseCount++;
            Add(EvType.Climate, Loc.Both($"solar eclipse began: the moon's shadow at ({EclipseX:0}, {EclipseY:0}), umbra {P.EclipseR:0} cells",
                $"началось солнечное затмение: тень спутника в ({EclipseX:0}, {EclipseY:0}), полная тень {P.EclipseR:0} клеток"), null, P.EclipseR, false, null, (int)EclipseX, Math.Clamp((int)EclipseY, 0, H - 1));
        }
        if (EclipseNow) { eclipseX1 = EclipseX; eclipseY1 = EclipseY; }
        else if (eclipseStart >= 0)
        {
            long len = Tick - eclipseStart;
            Add(EvType.Climate, Loc.Both($"solar eclipse #{EclipseCount}: the shadow went from ({eclipseX0:0}, {eclipseY0:0}) to ({eclipseX1:0}, {eclipseY1:0}) in {len} ticks",
                $"солнечное затмение №{EclipseCount}: тень прошла от ({eclipseX0:0}, {eclipseY0:0}) до ({eclipseX1:0}, {eclipseY1:0}) за {len} тиков"), null, len, EclipseCount == 1, null, (int)eclipseX1, Math.Clamp((int)eclipseY1, 0, H - 1));
            eclipseStart = -1;
        }
    }

    // ---- 1.5 solar activity and flares ----

    public float SolarCycleDays => P.SolarCycle > 0 ? P.SolarCycle : 30 + 50 * Hash32.F(Seed, 9201);

    // 0 … 1: a sine over the cycle plus slow irregular wandering (value noise over days).
    public float ActivityAt(double tick)
    {
        double day = tick / P.DayLen;
        double a = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (day / SolarCycleDays + Hash32.F(Seed, 9202)));
        a += 0.3 * (Noise.Value(Seed * 7 + 9203, (float)(day / 2 % 1048576.0), 0.5f, 1 << 20) - 0.5);
        return (float)Math.Clamp(a, 0, 1);
    }

    const int FlareSlot = 100;   // ticks: each slot may start one flare

    // Slot k: whether it starts a flare, when, for how long, how strong (Pareto from FlarePowerMin).
    bool FlareOfSlot(long k, out long start, out float len, out float power)
    {
        start = 0; len = 0; power = 0;
        int seed = Seed * 31 + 9301, kk = (int)k;
        double p = P.FlareRate * ActivityAt(k * FlareSlot) * FlareSlot / P.DayLen;
        if (Hash32.F(seed, kk, 1) >= p) return false;
        start = k * FlareSlot + (long)(Hash32.F(seed, kk, 2) * FlareSlot);
        len = P.FlareLen * (1f / 3 + 5f / 3 * Hash32.F(seed, kk, 3));
        power = P.FlarePowerMin * MathF.Pow(1 - 0.999f * Hash32.F(seed, kk, 4), -1 / P.FlarePareto);
        return true;
    }

    // The summed power of the flares under way at a tick (each rises over a tenth of its length, then fades),
    // with the strong flares the player sent (World.ClimateCycles: catastrophes; they come with the law off too).
    public float FlareAt(long tick)
    {
        float forced = 0;
        foreach (var f in forcedFlares)
        {
            if (tick < f.Start || tick >= f.Start + f.Len) continue;
            float u = (tick - f.Start) / f.Len;
            forced += f.Power * (u < 0.1f ? u / 0.1f : (1 - u) / 0.9f);
        }
        if (!FlareLaw || P.FlareRate <= 0) return forced;
        long first = (long)Math.Floor((tick - 2 * P.FlareLen - FlareSlot) / (double)FlareSlot), last = tick / FlareSlot;
        float sum = 0;
        for (long k = Math.Max(0, first); k <= last; k++)
        {
            if (!FlareOfSlot(k, out long start, out float len, out float power) || tick < start || tick >= start + len) continue;
            float u = (tick - start) / len;
            sum += power * (u < 0.1f ? u / 0.1f : (1 - u) / 0.9f);
        }
        return forced > 0 ? sum + forced : sum;
    }

    public float SolarActivity, FlarePower;   // this tick (derived from the tick and the laws)
    public int FlareCount;                     // flare episodes so far (several flares overlapping are one)
    long flareEp;                              // the running episode's number (0: none), and its counts
    long flareStart;
    float flarePeak;
    int flareHit, flareMut, flareDeaths;
    public int FlareMutations;                 // all flare mutations ever

    // Between ticks, before the agent phase: the sun's activity and the flares now; the chronicle of an
    // episode (start, and at the end its peak, the bodies it reached, mutations and deaths).
    void StepSky()
    {
        if (forcedFlares.Count > 0) forcedFlares.RemoveAll(f => f.Start + f.Len < Tick);
        if (!FlareLaw && forcedFlares.Count == 0)
        {
            SolarActivity = 0; FlarePower = 0;
            if (flareEp != 0) EndFlare();
            return;
        }
        SolarActivity = FlareLaw ? ActivityAt(Tick) : 0;
        FlarePower = FlareAt(Tick);
        if (FlarePower > 0)
        {
            if (flareEp == 0)
            {
                flareEp = ++FlareCount; flareStart = Tick; flarePeak = 0; flareHit = flareMut = flareDeaths = 0;
                Add(EvType.Climate, Loc.Both($"solar flare #{FlareCount}: solar activity {SolarActivity:P0}",
                    $"солнечная вспышка №{FlareCount}: активность солнца {SolarActivity:P0}"), null, FlarePower);
            }
            flarePeak = Math.Max(flarePeak, FlarePower);
        }
        else if (flareEp != 0) EndFlare();
    }

    void EndFlare()
    {
        bool big = flareDeaths >= 50 || flarePeak >= 5 * Math.Max(0.1f, P.FlarePowerMin);
        Add(EvType.Climate, Loc.Both($"flare #{flareEp} ended: peak power {flarePeak:0.0}, {Tick - flareStart} ticks; {flareHit} {EnPlural(flareHit, "body", "bodies")} irradiated, {flareMut} mutations, {flareDeaths} deaths",
                $"вспышка №{flareEp} кончилась: пик мощности {flarePeak:0.0}, {Tick - flareStart} тиков; облучено {flareHit} {Plural(flareHit, "тело", "тела", "тел")}, мутаций {flareMut}, погибло {flareDeaths}"),
            null, flarePeak, big);
        flareEp = 0;
    }

    // What reaches a body from the open sky over its cell (0 … 1): the direct light at the surface, less
    // the rock above it (1 − the cave cover, World.Cave) and the water over it.
    public float SkyExposure(Agent a)
    {
        int c = a.Y * W + a.X;
        float e = Sun[c];
        if (e <= 0) return 0;
        if (a.Z < Height[c]) e *= 1 - Cover(c, a.Z);
        else if (Water[c] > 0) e *= MathF.Exp(-P.FlareWaterDim * Below(a, c));
        return e;
    }

    // The body's own shield: e^(−ShieldK·Σ mass·packing of its molecules / its comfortable room) — heavy,
    // densely packing matter stops more, and costs its upkeep (CostMass).
    public float Shield(Agent a)
    {
        float m = 0;
        var mass = Chem.Mass; var pack = Chem.Packing;
        for (int s = 0; s < Chemistry.S; s++) if (a.Inv[s] > 0) m += a.Inv[s] * mass[s] * pack[s];
        return MathF.Exp(-P.ShieldK * m / a.Room);
    }

    // The UV a body reads (`uv`, information only): the sun's activity plus the flares, at the body.
    int UvSense(Agent a) => (int)(100 * (SolarActivity + FlarePower) * SkyExposure(a));

    // In LiveBody (the agent phase, the body's own state): the dose of this tick and what it does short of
    // mutations (those go with the UV path there). Returns the dose; `harm` is the energy it cost.
    float Flare(Agent a, out float harm)
    {
        harm = 0;
        float dose = FlarePower * SkyExposure(a);
        if (dose > 0) dose *= Shield(a);
        a.FlareDose = dose;
        if (dose <= 0) return 0;
        if (a.FlareEp != flareEp && dose > 0.01f)
        {
            a.FlareEp = flareEp;
            Interlocked.Increment(ref flareHit);
            BioNote(a, Tick, BioKind.Flare, 0, dose);
        }
        float wear = Math.Min(0.5f, P.FlareProtK * dose);
        if (wear > 0) for (int k = 0; k < a.EnzN; k++) WearProtein(a, k, 1 - wear);
        harm = P.FlareHarmK * dose;
        if (harm > 0) { Dissipate(a, harm); a.LifeHarm += harm; }
        // Heat from outside: into the heat the body holds (it warms Tb), shed into the cells as it cools.
        float q = P.FlareHeatK * dose;
        if (q > 0)
        {
            double h0 = a.HeatHeld;
            a.HeatHeld += q;
            a.Tb += q * 6f / (5f + a.Mass);
            Flows[FFlare] += a.HeatHeld - h0;
        }
        return dose;
    }

    void FlareMutated() { Interlocked.Increment(ref flareMut); Interlocked.Increment(ref FlareMutations); }
    void FlareKilled() => Interlocked.Increment(ref flareDeaths);   // DeathsFlare: in Die

    // ---- 1.3 shading ----

    // The chance that a photon falling on `cell` reaches body `a` past the bodies above it: e^(−ShadeK·Σ
    // cover), cover = (volume per cell / VoxelSpace)^(2/3) of each body in the cell (and a big body over it)
    // that is higher, or on the same level and bigger. Reads the cell's own list (agent phase: own tile).
    float ShadeOf(Agent a, int cell)
    {
        if (P.ShadeK <= 0) return 1;
        var big = Big[cell];
        if (Count[cell] <= (a.Y * W + a.X == cell ? 1 : 0) && (big == null || big == a)) return 1;
        float la = Level(a), cover = 0;
        for (var o = Head[cell]; o != null; o = o.NextInCell) if (o != a && Above(o, la, a.Volume)) cover += Cover2(o);
        if (big != null && big != a && Above(big, la, a.Volume)) cover += Cover2(big);
        return cover > 0 ? MathF.Exp(-P.ShadeK * cover) : 1;
    }

    static bool Above(Agent o, float level, float volume)
    {
        float lo = Level(o);
        return lo > level + 0.01f || (lo > level - 0.01f && o.Volume > volume);
    }

    static float Cover2(Agent o)
    {
        float v = Math.Max(0, o.Volume) / (o.Cells * P.VoxelSpace);
        float c = MathF.Cbrt(v);
        return c * c;
    }

    // ---- save (version 8): its own block after the geochemistry ----
    // The light and transparency fields, the running eclipse and flare episodes with their counts, the
    // flare deaths, and every body's last flare episode (in the order of Agents). Before version 8 the
    // fields are rebuilt at the load (the surface light from Light and the water over it, the transparency
    // from the tick and the heights) and no episode is running.
    void SyncSky(Sync s)
    {
        if (s.Version < 8)
        {
            if (s.Reading) SkyFromOldFile();
            return;
        }
        s.A<float>(Sun); s.A<float>(Transp); s.V(ref TranspMean); s.V(ref transpValid);
        s.V(ref EclipseCount); s.V(ref eclipseStart); s.V(ref eclipseX0); s.V(ref eclipseY0); s.V(ref eclipseX1); s.V(ref eclipseY1);
        s.V(ref FlareCount); s.V(ref flareEp); s.V(ref flareStart); s.V(ref flarePeak);
        s.V(ref flareHit); s.V(ref flareMut); s.V(ref flareDeaths); s.V(ref FlareMutations); s.V(ref DeathsFlare);
        foreach (var a in Agents) s.V(ref a.FlareEp);
        if (s.Reading) { SolarActivity = FlareLaw ? ActivityAt(Tick) : 0; FlarePower = FlareLaw ? FlareAt(Tick) : 0; }
    }

    // A file without the sky block: the surface light from Light and the water over it, the transparency
    // from the tick and the heights, no eclipse or flare episode running, no counts (the save test sets the
    // original to the same before writing an old version).
    internal void SkyFromOldFile()
    {
        for (int i = 0; i < N; i++) Sun[i] = Light[i] * MathF.Exp(P.WaterDim * Water[i]);
        if (TranspLaw) UpdateTransparency(); else { Array.Fill(Transp, 1f); TranspMean = 1; transpValid = false; }
        EclipseCount = 0; eclipseStart = -1; eclipseX0 = eclipseY0 = eclipseX1 = eclipseY1 = 0;
        FlareCount = 0; flareEp = 0; flareStart = 0; flarePeak = 0; flareHit = flareMut = flareDeaths = 0; FlareMutations = 0; DeathsFlare = 0;
        foreach (var a in Agents) a.FlareEp = 0;
    }

    // ---- observation (bench, HUD) ----

    public static readonly string[] SkyNames = { "diet_region", "diet_ratio", "flare_dose", "transp_mean", "flares", "eclipses", "deaths_flare" };

    // diet_region: the mean e^H of diets within populated 32×32 squares (≥ 5 bodies, unweighted); diet_ratio: the
    // planet's e^H over that mean (how differently squares eat: 1 — alike); flare_dose: the mean dose of
    // the last tick over living bodies; the mean transparency; flare episodes, eclipses and flare deaths.
    public double[] SkyCensus()
    {
        var v = new double[SkyNames.Length];
        var per = new int[Tiles * 5];
        var all = new int[5];
        int pop = 0;
        double dose = 0;
        foreach (var a in Agents)
        {
            if (a.Dead) continue;
            int d = Diet(a), t = tileRow[a.Y] * TilesX + tileCol[a.X];
            per[t * 5 + d]++; all[d]++; pop++;
            dose += a.FlareDose;
        }
        static double ExpH(ReadOnlySpan<int> c)
        {
            int n = 0; foreach (int x in c) n += x;
            if (n == 0) return 0;
            double h = 0;
            foreach (int x in c) if (x > 0) { double p = x / (double)n; h -= p * Math.Log(p); }
            return Math.Exp(h);
        }
        double sum = 0; int regions = 0;
        for (int t = 0; t < Tiles; t++)
        {
            var c = per.AsSpan(t * 5, 5);
            int n = 0; foreach (int x in c) n += x;
            if (n < 5) continue;
            sum += ExpH(c); regions++;
        }
        v[0] = regions > 0 ? sum / regions : 0;
        v[1] = v[0] > 0 ? ExpH(all) / v[0] : 0;
        v[2] = pop > 0 ? dose / pop : 0;
        v[3] = TranspLaw ? TranspMean : 1;
        v[4] = FlareCount; v[5] = EclipseCount; v[6] = DeathsFlare;
        return v;
    }
}
