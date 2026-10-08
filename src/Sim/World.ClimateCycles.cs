using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Primordium;

// Non-stationary climate (ROADMAP 2.2) and the player's catastrophes (ROADMAP 9.4). Laws of the
// environment, none of behaviour.
// - Orbit (pure functions of the seed, the tick and the laws): the axial tilt swings around P.Tilt; the
//   eccentricity grows and shrinks while the perihelion turns through the year (precession): the sun at
//   any moment × 1 + 2e·cos(year angle − perihelion), so the hemisphere whose summer meets the perihelion
//   has the hotter summer; the sun's output drifts slowly (× the activity cycle of World.Sky). The cycles
//   count from ClimT0 — 0 for a new world, the load tick for a file written before them (version 10) —
//   where every one of them is at its neutral phase (tilt = Tilt, circular orbit, output 1).
// - Climate: a relative change of sunlight (output, eccentricity, ash) shifts a latitude's climate by
//   ClimSens °C per unit (climRow, so TempEq and everything after it follow); the light itself — the
//   photons — changes by the same factor.
// - Ice ages: each hemisphere's glaciation G (state, saved) grows towards 1 while its summer insolation at
//   65° is below IceAgeThreshold of the norm (tilt, eccentricity, precession and drift together), and
//   retreats otherwise, with the time constant IceAgeTau; a row is IceAgeDT·(0.4 + 0.6·|sin lat|)·(0.7·G
//   of its hemisphere + 0.3·G of the other) colder. Snow and ice then spread by the existing water cycle:
//   the same water — the sea gives it up as snow on land and ice on itself (WaterTotal is conserved).
// - Volcanic winters: now and then (a hash of the seed and the day, MegaEruptionRate a day) an existing
//   vent throws out MegaEruptionBlocks blocks of the interior — laid like its cone, booked in
//   InteriorInput and the `vent` energy input — and ash into the stratosphere (Veil: optical depth per
//   cell, saved): it mixes along the latitude within hours, spreads pole-wards in about AshSpreadDays
//   and falls out with the time constant AshTau. Light × e^(−veil); the climate by ClimSens.
// - Catastrophes (between ticks, World.Catastrophe): an ice age for N days (the glaciation forced to grow),
//   a flood (sea +K blocks: water from outside, booked in WaterHand), a volcanic winter (a mega-eruption
//   now), a strong solar flare (World.Sky's flare path: dose, mutations, the `flare` input), a drought (a
//   blocking high over a region: no clouds or rain there — the moisture falls elsewhere — and warmer,
//   so it dries; the same water), a poisoning (a toxic molecule of the world's chemistry poured loose by
//   the hand: HandInput and the `hand` input). Each is chronicled (Player) and logged in ParamLog as
//   "catastrophe <spec>": the trajectory is the seed plus that timeline.
// P.ClimateCycles = 0 switches the cycles off; with no catastrophe running either, nothing here runs and
// the world follows the old expressions bit for bit. Everything runs between ticks on the world thread.
public sealed partial class World
{
    public static bool CyclesLaw => P.ClimateCycles != 0;

    long climT0;                 // the tick the cycles count from (their neutral phase)
    bool cyclesSeen;             // the law was on at the last step (switching it on restarts the cycles from neutral)
    bool climOn;                 // this tick: the cycles or something of theirs (glaciation, ash, a drought, a flare) is running
    public float GlaciN, GlaciS; // glaciation of the northern and southern hemisphere, 0 … 1 (saved)
    long forcedIceUntil = -1;    // a catastrophe: the glaciation grows until this tick

    // The ash veil: optical depth over each cell (saved), its row means of transmission (derived).
    public readonly float[] Veil = new float[N];
    bool veilOn;
    readonly float[] veilRowTrans = new float[H];
    readonly float[] veilT = new float[N];       // e^(−veil) per cell for the light (derived)
    readonly float[] climShift = new float[H];   // °C added to climRow by the cycles (derived at each light update)
    public float VeilTransMean = 1;

    // Observation (HUD, chronicle, bench): the orbit and the sun now, the summer insolation at 65°.
    public float TiltNow, EccNow, PeriNow, SunOutNow = 1, SummerN = 1, SummerS = 1;
    public int IceAges, VolcanicWinters, MegaEruptions, CatastropheCount;
    public bool IceAgeNow => iceAgeNow;
    public bool VolcanicWinterNow => winterNow;
    bool iceAgeNow, winterNow;
    long iceStart, winterStart;
    float iceSnowMax, winterTransMin, winterT0, winterTMin;
    double iceLiquid0, iceLiquidMin, iceFrozen0, iceFrozenMax;
    int iceSea0;

    // Water brought from outside (+) by the hand (pouring) and by floods: WaterTotal() − its start == this.
    public double WaterHand;

    // A strong flare the player sent (World.Sky adds them to the flares of the law).
    struct ForcedFlare { public long Start; public float Len, Power; }
    readonly List<ForcedFlare> forcedFlares = new();
    // A drought: a blocking high over a disk (clear sky, no rain, warmer) until a tick.
    sealed class Drought { public int X, Y; public float R, DT; public long Start, Until; }
    readonly List<Drought> droughts = new();
    readonly float[] dryW = new float[N], dryT = new float[N];   // derived from the droughts
    bool dryOn;

    // ---- the orbit and the sun: pure functions of the tick ----

    public float TiltPeriodDays => P.TiltPeriod > 0 ? P.TiltPeriod : 40 + 80 * Hash32.F(Seed, 9401);
    public float PrecPeriodDays => P.PrecPeriod > 0 ? P.PrecPeriod : 20 + 30 * Hash32.F(Seed, 9402);
    public float EccPeriodDays => P.EccPeriod > 0 ? P.EccPeriod : 100 + 200 * Hash32.F(Seed, 9403);
    public float SunDriftDays => P.SunDriftPeriod > 0 ? P.SunDriftPeriod : 200 + 400 * Hash32.F(Seed, 9404);
    int TiltSign => Hash32.F(Seed, 9405) < 0.5f ? 1 : -1;
    int DriftSign => Hash32.F(Seed, 9406) < 0.5f ? 1 : -1;

    double CycleDays(double tick) => (tick - climT0) / P.DayLen;

    // The axial tilt at a tick (P.Tilt with the law off).
    public float TiltAt(double tick)
    {
        if (!CyclesLaw) return P.Tilt;
        return P.Tilt + TiltSign * P.TiltAmp * (float)Math.Sin(2 * Math.PI * CycleDays(tick) / TiltPeriodDays);
    }

    // Eccentricity 0 … EccAmp (0 at the neutral phase) and the perihelion's angle in the year (radians;
    // the northern summer solstice is at π/2).
    public float EccAt(double tick) => !CyclesLaw ? 0 : P.EccAmp * 0.5f * (1 - (float)Math.Cos(2 * Math.PI * CycleDays(tick) / EccPeriodDays));
    public float PerihelionAt(double tick) => (float)(2 * Math.PI * (CycleDays(tick) / PrecPeriodDays + Hash32.F(Seed, 9407)) % (2 * Math.PI));

    // The sun's brightness factor from the orbit at a point of the year (year angle 2π·yearFrac).
    float EccFactor(double tick, double yearAngle) => 1 + 2 * EccAt(tick) * (float)Math.Cos(yearAngle - PerihelionAt(tick));
    double YearAngle(double tick) => 2 * Math.PI * (tick / ((double)P.DayLen * P.YearDays) % 1.0);

    // The slow drift of the sun's output (1 at the neutral phase).
    public float SunDrift(double tick) => !CyclesLaw ? 1 : 1 + DriftSign * P.SunDriftAmp * (float)Math.Sin(2 * Math.PI * CycleDays(tick) / SunDriftDays);

    // Summer insolation at 65° of a hemisphere (+1 north, −1 south) relative to its norm (Tilt, circular
    // orbit, output 1): the day's mean power at the solstice × the orbit's factor there × the drift.
    public float SummerInsol(int hemi, double tick)
    {
        float lat = 65 * MathF.PI / 180, norm = DailyInsol(lat, P.Tilt);
        if (norm <= 0) return 1;
        float tilt = TiltAt(tick);
        double solstice = hemi > 0 ? Math.PI / 2 : 3 * Math.PI / 2;
        return DailyInsol(lat, tilt) * EccFactor(tick, solstice) * SunDrift(tick) / norm;
    }

    // ---- each tick (Step, before the light) ----

    void StepClimateCycles()
    {
        bool law = CyclesLaw;
        if (law && !cyclesSeen) climT0 = Tick;   // switched on: the cycles start from their neutral phase
        cyclesSeen = law;
        climOn = law || GlaciN > 0 || GlaciS > 0 || forcedIceUntil >= Tick || veilOn || dryOn || forcedFlares.Count > 0;
    }

    // The glaciation, the ash, the droughts and the chronicle of epochs: every env step (UpdateClimate).
    void StepClimateState()
    {
        if (!climOn) return;
        bool law = CyclesLaw;
        TiltNow = TiltAt(Tick); EccNow = EccAt(Tick); PeriNow = PerihelionAt(Tick); SunOutNow = SunDrift(Tick);
        SummerN = law ? SummerInsol(1, Tick) : 1; SummerS = law ? SummerInsol(-1, Tick) : 1;
        bool forced = forcedIceUntil >= Tick;
        float k = 1 - MathF.Exp(-P.EnvEvery / (Math.Max(0.01f, P.IceAgeTau) * P.DayLen)), thr = P.IceAgeThreshold;
        float Target(float q) => forced ? 1 : law ? Smooth(thr + 0.015f, thr - 0.015f, q) : 0;
        float tn = Target(SummerN), ts = Target(SummerS);
        GlaciN += (tn - GlaciN) * k; if (tn == 0 && GlaciN < 1e-3f) GlaciN = 0;
        GlaciS += (ts - GlaciS) * k; if (ts == 0 && GlaciS < 1e-3f) GlaciS = 0;
        if (veilOn) StepVeil();
        if (dryOn && droughts.RemoveAll(d => d.Until <= Tick) > 0) RebuildDroughts();
        ChronEpochs();
    }

    // The climate shift of each row for the light update (lum: the sun's factor now, output × orbit).
    void PrepareClimShift(float lum)
    {
        float sens = P.ClimSens, dt = P.IceAgeDT, gn = GlaciN, gs = GlaciS;
        for (int y = 0; y < H; y++)
        {
            float lat = Latitude(y), own = lat >= 0 ? gn : gs, other = lat >= 0 ? gs : gn;
            float sun = lum * (veilOn ? veilRowTrans[y] : 1);
            climShift[y] = sens * (sun - 1) - dt * (0.4f + 0.6f * MathF.Abs(MathF.Sin(lat))) * (0.7f * own + 0.3f * other);
        }
    }

    // The sun's factor for the light now: the drift and the orbit (the activity is World.Sky's).
    float CycleLum() => CyclesLaw ? SunDrift(Tick) * EccFactor(Tick, YearAngle(Tick)) : 1;

    // ---- the ash veil ----

    readonly float[] veilRow = new float[H], veilNext = new float[H];

    void StepVeil()
    {
        float dt = P.EnvEvery;
        float kz = 1 - MathF.Exp(-dt / (0.3f * P.DayLen));                                  // along the latitude: hours
        float steps = Math.Max(1, P.AshSpreadDays * P.DayLen / dt);
        float kTotal = (H / 2f) * (H / 2f) / (2 * steps);                                    // pole-wards: σ² = 2·K·steps
        int sub = Math.Max(1, (int)MathF.Ceiling(kTotal / 0.4f));
        float km = kTotal / sub, decay = MathF.Exp(-dt / (Math.Max(0.01f, P.AshTau) * P.DayLen));
        // Sequential: a few passes over the planet, cheaper than handing rows to threads.
        for (int y = 0; y < H; y++)
        {
            double s = 0;
            for (int i = y * W, end = i + W; i < end; i++) s += Veil[i];
            veilRow[y] = (float)(s / W);
        }
        Array.Copy(veilRow, veilNext, H);
        for (int r = 0; r < sub; r++)
        {
            for (int y = 0; y < H; y++)
            {
                float up = veilRow[Math.Max(0, y - 1)], dn = veilRow[Math.Min(H - 1, y + 1)];
                veilNext[y] = veilRow[y] + km * (up + dn - 2 * veilRow[y]);
            }
            Array.Copy(veilNext, veilRow, H);
        }
        // veilNext: the new row means; each row is mixed along the latitude, scaled to its new mean and decays.
        float max = 0;
        double trans = 0;
        for (int y = 0; y < H; y++)
        {
            double s = 0, ts = 0;
            for (int i = y * W, end = i + W; i < end; i++) s += Veil[i];
            float m0 = (float)(s / W), m1 = veilNext[y];
            for (int i = y * W, end = i + W; i < end; i++)
            {
                float v = Veil[i] + (m0 - Veil[i]) * kz;
                v = m0 > 0 ? v * (m1 / m0) : m1;
                v *= decay;
                Veil[i] = v;
                if (v > max) max = v;
                float t = MathF.Exp(-v);   // the light's transmission (as VeilRowTransmission)
                veilT[i] = t; ts += t;
            }
            veilRowTrans[y] = (float)(ts / W);
            trans += veilRowTrans[y];
        }
        VeilTransMean = (float)(trans / H);
        if (max < 1e-4f) { Array.Clear(Veil); veilOn = false; VeilRowTransmission(); }
    }

    void VeilRowTransmission()
    {
        if (!veilOn) { Array.Fill(veilRowTrans, 1f); VeilTransMean = 1; return; }
        for (int y = 0; y < H; y++)
        {
            double s = 0;
            for (int i = y * W, end = i + W; i < end; i++) { float e = MathF.Exp(-Veil[i]); veilT[i] = e; s += e; }
            veilRowTrans[y] = (float)(s / W);
        }
        double t = 0;
        for (int y = 0; y < H; y++) t += veilRowTrans[y];
        VeilTransMean = (float)(t / H);
    }

    // ---- mega-eruptions ----

    // The day's mega-eruption, if it has one: a hash of the seed and the day (no random stream is drawn).
    public bool MegaEruptionOfDay(long day, out long tick)
    {
        tick = -1;
        if (!CyclesLaw || P.MegaEruptionRate <= 0) return false;
        int seed = Seed * 37 + 9501, d = (int)day;
        if (Hash32.F(seed, d, 1) >= P.MegaEruptionRate) return false;
        tick = day * P.DayLen + (long)(Hash32.F(seed, d, 2) * P.DayLen);
        return true;
    }

    // The next scheduled mega-eruption at or after `from` within `days` (−1: none).
    public long NextMegaEruption(long from, int days = 400)
    {
        for (long d = from / P.DayLen, end = d + days; d < end; d++)
            if (MegaEruptionOfDay(d, out long t) && t >= from) return t;
        return -1;
    }

    // In StepVents: the law's eruption of this tick.
    void MaybeMegaEruption()
    {
        if (!CyclesLaw || P.MegaEruptionRate <= 0) return;
        if (!MegaEruptionOfDay(Tick / P.DayLen, out long t) || t != Tick) return;
        var (x, y) = EruptionSite(Hash32.F(Seed * 37 + 9502, (int)(Tick / P.DayLen)));
        Erupt(x, y, P.MegaEruptionBlocks, P.MegaAsh, false);
    }

    // Where a mega-eruption breaks out: one of the existing vents (u picks it), or with none a lowland column.
    (int x, int y) EruptionSite(float u)
    {
        if (Vents.Count > 0) { var v = Vents[Math.Min(Vents.Count - 1, (int)(u * Vents.Count))]; return (v.X, v.Y); }
        int best = 0;
        for (int k = 0; k < 64; k++)
        {
            int i = (int)(Hash32.U(Seed * 41 + 9503, (int)(Tick / P.DayLen), k) % N);
            if (Height[i] < Height[best] || k == 0) best = i;
        }
        return (best % W, best / W);
    }

    // A mega-eruption at (cx, cy): `blocks` blocks of the interior laid around it like a vent's cone
    // (InteriorInput and the `vent` input), and `ash` (the planet's mean optical depth once spread) into
    // the stratosphere around it.
    public int Erupt(int cx, int cy, int blocks, float ash, bool player)
    {
        cx = ((cx % W) + W) % W; cy = Math.Clamp(cy, 0, H - 1);
        // The columns of a disk around the vent, nearest first; blocks go round them from the centre.
        float rad = 1.5f + MathF.Sqrt(Math.Max(1, blocks) / MathF.PI) / 2;
        int ir = (int)MathF.Ceiling(rad);
        var disk = new List<(int cell, float d2)>();
        for (int dy = -ir; dy <= ir; dy++)
            for (int dx = -ir; dx <= ir; dx++)
            {
                int y = cy + dy;
                float d2 = dx * dx + dy * dy;
                if (y < 0 || y >= H || d2 > rad * rad) continue;
                disk.Add((y * W + ((cx + dx) % W + W) % W, d2));
            }
        disk.Sort((a, b) => a.d2 != b.d2 ? a.d2.CompareTo(b.d2) : a.cell.CompareTo(b.cell));
        int laid = 0, seed = Seed * 43 + 9504;
        for (int b = 0; b < blocks && disk.Count > 0; b++)
        {
            int c = disk[b % disk.Count].cell;
            if (Height[c] >= Z - 3) continue;
            int molecule = VentMolecule(Hash32.F(seed, (int)Tick, b));
            byte m = Chem.BuiltMat[molecule];
            int w = c * Z + Height[c];
            DisplaceOccupants(c, Height[c]);
            int n = BlockCapacity(molecule, 35);
            Mat[w] = m; Units[w] = (ushort)n; Order[w] = 35;
            for (int e = 0; e < Chemistry.ElementCount; e++) InteriorInput[e] += (double)n * Chem.Atoms[molecule, e];
            Flows[FVent] += (double)n * Chem.E[molecule];
            Height[c]++;
            TerrainChanged(c);
            Repose(c);
            laid++;
        }
        // Ash: a Gaussian patch (σ 6 cells) whose sum is ash × N.
        if (ash > 0)
        {
            const float sigma = 6;
            int r = (int)(3 * sigma);
            double sum = 0;
            for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++) if (cy + dy >= 0 && cy + dy < H) sum += Math.Exp(-(dx * dx + dy * dy) / (2.0 * sigma * sigma));
            float peak = (float)(ash * N / sum);
            for (int dy = -r; dy <= r; dy++)
            {
                int y = cy + dy;
                if (y < 0 || y >= H) continue;
                for (int dx = -r; dx <= r; dx++)
                    Veil[y * W + ((cx + dx) % W + W) % W] += peak * MathF.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
            }
            veilOn = true;
            climOn = true;
            VeilRowTransmission();
        }
        MegaEruptions++;
        Add(player ? EvType.Player : EvType.Climate,
            Loc.Both($"{(player ? "player catastrophe — " : "")}volcanic megaeruption at ({cx}, {cy}): {laid} {EnPlural(laid, "block", "blocks")} from the depths, ash in the stratosphere (optical depth {ash:0.00} over the planet once it spreads)",
                $"{(player ? "катастрофа игрока — " : "")}мегаизвержение вулкана ({cx}, {cy}): {laid} {Plural(laid, "блок", "блока", "блоков")} из недр, пепел в стратосфере (оптическая толщина {ash:0.00} над планетой, когда разойдётся)"),
            null, laid, true, null, cx, cy);
        return laid;
    }

    // ---- the chronicle of epochs ----

    // English counterpart of Plural (the Russian one, in World.Chronicle.cs): "1 block", "3 blocks".
    static string EnPlural(long n, string one, string many) => Math.Abs(n) == 1 ? one : many;

    void ChronEpochs()
    {
        float g = Math.Max(GlaciN, GlaciS);
        if (!iceAgeNow && g >= 0.5f)
        {
            iceAgeNow = true; iceStart = Tick; IceAges++;
            var (liquid, ice, snow, _) = WaterParts();
            iceLiquid0 = iceLiquidMin = liquid; iceFrozen0 = iceFrozenMax = ice + snow; iceSnowMax = SnowCover();
            iceSea0 = SeaCells();
            bool both = GlaciN >= 0.5f && GlaciS >= 0.5f, north = GlaciN >= GlaciS;
            string where = both ? "в обоих полушариях" : north ? "на севере" : "на юге";
            string whereEn = both ? "in both hemispheres" : north ? "in the north" : "in the south";
            Add(EvType.Climate, forcedIceUntil >= Tick
                ? Loc.Both($"ice age #{IceAges} began {whereEn} (set off by the player): glaciers are growing",
                    $"началось ледниковье №{IceAges} {where} (вызвано игроком): ледники растут")
                : Loc.Both($"ice age #{IceAges} began {whereEn}: summer insolation at 65° is {SummerN:P0} of normal in the north, {SummerS:P0} in the south (threshold {P.IceAgeThreshold:P0}); obliquity {TiltNow * 180 / MathF.PI:0.0}°, eccentricity {EccNow:0.000}",
                    $"началось ледниковье №{IceAges} {where}: летняя инсоляция на 65° — {SummerN:P0} нормы на севере, {SummerS:P0} на юге (порог {P.IceAgeThreshold:P0}); наклон оси {TiltNow * 180 / MathF.PI:0.0}°, эксцентриситет {EccNow:0.000}"),
                null, g, true);
        }
        else if (iceAgeNow)
        {
            if (Tick % (P.EnvEvery * 16) == 0)   // what the chronicle tells of it: sampled every 16 env steps
            {
                var (liquid, ice, snow, _) = WaterParts();
                iceLiquidMin = Math.Min(iceLiquidMin, liquid);
                iceFrozenMax = Math.Max(iceFrozenMax, ice + snow);
                iceSnowMax = Math.Max(iceSnowMax, SnowCover());
            }
            if (g < 0.25f)
            {
                iceAgeNow = false;
                double drop = (iceLiquid0 - iceLiquidMin) / Math.Max(1, iceSea0);
                Add(EvType.Climate, Loc.Both($"ice age #{IceAges} ended: it lasted {(Tick - iceStart) / (float)P.DayLen:0.0} days; snow covered up to {iceSnowMax:P0} of the planet, " +
                    $"up to {iceFrozenMax:0} in snow and ice (was {iceFrozen0:0}), sea level fell by up to {drop:0.00} bl.",
                    $"кончилось ледниковье №{IceAges}: длилось {(Tick - iceStart) / (float)P.DayLen:0.0} сут; снег покрывал до {iceSnowMax:P0} планеты, " +
                    $"в снегу и льду до {iceFrozenMax:0} (было {iceFrozen0:0}), уровень моря опускался на {drop:0.00} бл."),
                    null, (float)drop, true);
            }
        }
        if (!winterNow && veilOn && VeilTransMean < 0.9f)
        {
            winterNow = true; winterStart = Tick; VolcanicWinters++;
            winterTransMin = VeilTransMean; winterT0 = winterTMin = MeanTemp();
            Add(EvType.Climate, Loc.Both($"volcanic winter #{VolcanicWinters}: the ash lets through {VeilTransMean:P0} of the light",
                $"вулканическая зима №{VolcanicWinters}: пепел пропускает {VeilTransMean:P0} света"), null, VeilTransMean, true);
        }
        else if (winterNow)
        {
            winterTransMin = Math.Min(winterTransMin, VeilTransMean);
            if (Tick % (P.EnvEvery * 16) == 0) winterTMin = Math.Min(winterTMin, MeanTemp());
            if (!veilOn || VeilTransMean > 0.97f)
            {
                winterNow = false;
                Add(EvType.Climate, Loc.Both($"volcanic winter #{VolcanicWinters} ended: {(Tick - winterStart) / (float)P.DayLen:0.0} days, light fell to {winterTransMin:P0}, " +
                    $"mean temperature {winterT0:+0.0;-0.0} → {winterTMin:+0.0;-0.0} °C",
                    $"кончилась вулканическая зима №{VolcanicWinters}: {(Tick - winterStart) / (float)P.DayLen:0.0} сут, свет падал до {winterTransMin:P0}, " +
                    $"средняя температура {winterT0:+0.0;-0.0} → {winterTMin:+0.0;-0.0} °C"), null, winterT0 - winterTMin, true);
            }
        }
    }

    float MeanTemp()
    {
        double t = 0;
        for (int i = 0; i < N; i++) t += Temp[i];
        return (float)(t / N);
    }

    float SnowCover()
    {
        int n = 0;
        for (int i = 0; i < N; i++) if (Snow[i] > 0.05f || Ice[i] > 0.1f) n++;
        return n / (float)N;
    }

    int SeaCells()
    {
        int n = 0;
        for (int i = 0; i < N; i++) if (Water[i] >= P.SwimDepth) n++;
        return n;
    }

    // ---- the water budget ----

    // All the water of the world: on the ground and in caves, as ice, as snow and in the air. Closed but for the hand
    // and floods (WaterHand): the water cycle only moves it.
    public double WaterTotal()
    {
        var (a, b, c, d) = WaterParts();
        return a + b + c + d;
    }

    public (double liquid, double ice, double snow, double air) WaterParts()
    {
        double w = 0, ice = 0, snow = 0;
        for (int i = 0; i < N; i++) { w += Water[i]; ice += Ice[i]; snow += Snow[i]; }
        return (w + CaveWaterTotal(), ice, snow, Moisture);   // the liquid in caves too (World.Waterways)
    }

    // ---- droughts ----

    void RebuildDroughts()
    {
        Array.Clear(dryW); Array.Clear(dryT);
        dryOn = droughts.Count > 0;
        foreach (var d in droughts)
        {
            float outer = 1.5f * d.R;
            int ir = (int)MathF.Ceiling(outer);
            for (int dy = -ir; dy <= ir; dy++)
            {
                int y = d.Y + dy;
                if (y < 0 || y >= H) continue;
                for (int dx = -ir; dx <= ir; dx++)
                {
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist > outer) continue;
                    int i = y * W + ((d.X + dx) % W + W) % W;
                    float w = 1 - Smooth(d.R, outer, dist);
                    dryW[i] = Math.Max(dryW[i], w);
                    dryT[i] += d.DT * w;
                }
            }
        }
    }

    // ---- catastrophes ----

    // Applies a catastrophe between ticks: chronicled (Player) and logged ("catastrophe <spec>" in ParamLog).
    // Returns what happened, or null (and `error`) if it cannot be done here.
    public string Catastrophe(Catastrophe c, out string error)
    {
        error = null;
        string text, ru;   // what happened, in English and in Russian
        switch (c.Kind)
        {
            case CatastropheKind.IceAge:
            {
                float days = Math.Clamp(c.Days, 0.1f, 1000);
                forcedIceUntil = Math.Max(forcedIceUntil, Tick + (long)(days * P.DayLen));
                text = $"ice age for {days:0.#} days: glaciers are growing (time constant {P.IceAgeTau:0.#} days, up to {P.IceAgeDT:0.#} °C colder at the poles)";
                ru = $"ледниковье на {days:0.#} сут: ледники растут (постоянная {P.IceAgeTau:0.#} сут, до {P.IceAgeDT:0.#} °C холоднее у полюсов)";
                break;
            }
            case CatastropheKind.Flood:
            {
                float k = Math.Clamp(c.Amount, 0.05f, 50);
                double before = 0, after = 0;
                int cells = 0;
                for (int i = 0; i < N; i++)
                {
                    if (Water[i] < P.SwimDepth) continue;
                    before += Water[i];
                    Water[i] += k;
                    after += Water[i];
                    cells++;
                }
                if (cells == 0) { error = Loc.T("there is no sea in this world — nothing to rise", "в мире нет моря — нечему подниматься"); return null; }
                WaterHand += after - before;
                text = $"flood: the sea rose by {k:0.##} bl. over {cells} {EnPlural(cells, "cell", "cells")} ({after - before:0} water from outside, counted in the water balance)";
                ru = $"потоп: море поднялось на {k:0.##} бл. над {cells} {Plural(cells, "клеткой", "клетками", "клетками")} ({after - before:0} воды извне, водный баланс учитывает её)";
                break;
            }
            case CatastropheKind.VolcanicWinter:
            {
                var (x, y) = c.X >= 0 && c.Y >= 0 ? (c.X, c.Y) : EruptionSite(Hash32.F(Seed * 37 + 9505, (int)Tick));
                int blocks = c.Amount > 0 ? (int)c.Amount : P.MegaEruptionBlocks;
                float ash = c.R > 0 ? c.R : Math.Max(0.05f, P.MegaAsh);
                Erupt(x, y, blocks, ash, true);
                // Logged with what was taken from the laws and the hash: a replay does the same even if they change.
                c = new Catastrophe { Kind = c.Kind, X = x, Y = y, Amount = blocks, R = ash };
                text = $"volcanic winter: megaeruption at ({x}, {y})";
                ru = $"вулканическая зима: мегаизвержение в ({x}, {y})";
                break;
            }
            case CatastropheKind.SolarFlare:
            {
                float power = Math.Clamp(c.Amount, 0.1f, 500), len = Math.Clamp(c.Days > 0 ? c.Days * P.DayLen : 300, 10, 100000);
                forcedFlares.Add(new ForcedFlare { Start = Tick + 1, Len = len, Power = power });
                text = $"strong solar flare: power {power:0.#}, {len:0} ticks (dose, mutations and heating as with the sun's own flares)";
                ru = $"сильная солнечная вспышка: мощность {power:0.#}, {len:0} тиков (доза, мутации и нагрев — как у вспышек солнца)";
                break;
            }
            case CatastropheKind.Drought:
            {
                if (c.X < 0 || c.Y < 0) { error = Loc.T("a drought needs a place", "засухе нужно место"); return null; }
                float days = Math.Clamp(c.Days, 0.1f, 1000), r = Math.Clamp(c.R > 0 ? c.R : 24, 2, 120), dt = c.Amount > 0 ? Math.Min(c.Amount, 40) : 8;
                droughts.Add(new Drought { X = ((c.X % W) + W) % W, Y = Math.Clamp(c.Y, 0, H - 1), R = r, DT = dt, Start = Tick, Until = Tick + (long)(days * P.DayLen) });
                RebuildDroughts();
                text = $"drought at ({c.X}, {c.Y}), radius {r:0}, for {days:0.#} days: no clouds, no rain (the moisture falls elsewhere), {dt:0.#} °C warmer";
                ru = $"засуха в ({c.X}, {c.Y}), радиус {r:0}, на {days:0.#} сут: ни облаков, ни дождя (влага выпадает в других местах), на {dt:0.#} °C теплее";
                break;
            }
            case CatastropheKind.Poison:
            {
                // The hand sprays a substance: a recipe from the matter library (Mix: number fractions by
                // species) or, with none given, the world's most reactive species by the reactive-damage law
                // (World.React), computed from its chemistry. Whether it harms anyone is that law's business.
                if (c.X < 0 || c.Y < 0) { error = Loc.T("poisoning needs a place", "отравлению нужно место"); return null; }
                var mix = new double[Chemistry.S];
                double sum = 0;
                if (c.Mix != null) for (int k = 0; k < Chemistry.S && k < c.Mix.Length; k++) { mix[k] = Math.Max(0, c.Mix[k]); sum += mix[k]; }
                bool law = sum <= 0;
                if (law) { mix[Chem.MostReactive] = 1; sum = 1; }
                float r = Math.Clamp(c.R > 0 ? c.R : 8, 1, 60), amount = Math.Clamp(c.Amount > 0 ? c.Amount : 30, 1, 5000);
                long total = 0;
                var given = new long[Chemistry.S];
                foreach (var (cell, w) in Brush(c.X, c.Y, r))
                    for (int s = 0; s < Chemistry.S; s++)
                    {
                        if (mix[s] <= 0) continue;
                        int n = (int)(amount * w * mix[s] / sum);
                        if (n <= 0) continue;
                        C[s][cell] += n;
                        for (int e = 0; e < Chemistry.ElementCount; e++) HandInput[e] += (double)n * Chem.Atoms[s, e];
                        Flows[FHand] += (double)n * Chem.E[s];
                        total += n; given[s] += n;
                    }
                string label = string.IsNullOrWhiteSpace(c.Label) ? "" : c.Label.Trim();
                text = $"poisoning at ({c.X}, {c.Y}), radius {r:0}: {total} molecules {(law ? "of the most reactive species by the law" : "of a library substance" + (label == "" ? "" : $" '{label}'"))} ({MatterRecipe.CountsText(Chem, given, true)}) scattered by hand (matter from outside, accounted for)";
                ru = $"отравление в ({c.X}, {c.Y}), радиус {r:0}: {total} молекул {(law ? "самого реакционного по закону вида" : "вещества из библиотеки" + (label == "" ? "" : $" «{label}»"))} ({MatterRecipe.CountsText(Chem, given, false)}) рассыпано рукой (вещество извне, учтено)";
                break;
            }
            default: error = Loc.T("unknown catastrophe", "неизвестная катастрофа"); return null;
        }
        CatastropheCount++;
        climOn = true;
        ParamLog.Add(new ParamChange { Tick = Tick, Name = "catastrophe " + c.Spec(), Value = (int)c.Kind });
        if (c.Kind != CatastropheKind.VolcanicWinter)   // the eruption is chronicled by Erupt
            Add(EvType.Player, Loc.Both("player catastrophe — " + text, "катастрофа игрока — " + ru), null, (float)c.Kind, true, null, c.X, c.Y);
        return Loc.T(text, ru);
    }

    // ---- save (version 10): its own block after the sky ----
    // The phase origin, the glaciation and its forcing, the ash veil (if any), the epochs under way with
    // what the chronicle tracks of them, the counters, the water brought from outside, the forced flares
    // and the droughts. Files before version 10 load with the cycles at their neutral phase (ClimT0 = the
    // load tick) and nothing running.
    void SyncClimateCycles(Sync s)
    {
        if (s.Version < 10)
        {
            if (s.Reading) ClimateFromOldFile();
            return;
        }
        s.V(ref climT0); s.V(ref cyclesSeen); s.V(ref GlaciN); s.V(ref GlaciS); s.V(ref forcedIceUntil);
        s.V(ref veilOn);
        if (veilOn) s.A<float>(Veil);
        else if (s.Reading) Array.Clear(Veil);
        s.V(ref iceAgeNow); s.V(ref iceStart); s.V(ref iceSnowMax); s.V(ref iceLiquid0); s.V(ref iceLiquidMin); s.V(ref iceFrozen0); s.V(ref iceFrozenMax); s.V(ref iceSea0);
        s.V(ref winterNow); s.V(ref winterStart); s.V(ref winterTransMin); s.V(ref winterT0); s.V(ref winterTMin);
        s.V(ref IceAges); s.V(ref VolcanicWinters); s.V(ref MegaEruptions); s.V(ref CatastropheCount); s.V(ref WaterHand);
        int n = forcedFlares.Count;
        s.V(ref n);
        if (s.Reading) { forcedFlares.Clear(); for (int k = 0; k < n; k++) forcedFlares.Add(default); }
        for (int k = 0; k < n; k++)
        {
            var f = forcedFlares[k];
            s.V(ref f.Start); s.V(ref f.Len); s.V(ref f.Power);
            forcedFlares[k] = f;
        }
        n = droughts.Count;
        s.V(ref n);
        if (s.Reading) { droughts.Clear(); for (int k = 0; k < n; k++) droughts.Add(new Drought()); }
        foreach (var d in droughts) { s.V(ref d.X); s.V(ref d.Y); s.V(ref d.R); s.V(ref d.DT); s.V(ref d.Start); s.V(ref d.Until); }
        if (s.Reading) { RebuildDroughts(); VeilRowTransmission(); }
    }

    // A file without the block (or a new world): cycles from their neutral phase at this tick, nothing running.
    internal void ClimateFromOldFile()
    {
        climT0 = Tick; cyclesSeen = CyclesLaw; GlaciN = GlaciS = 0; forcedIceUntil = -1;
        veilOn = false; Array.Clear(Veil); VeilRowTransmission();
        iceAgeNow = winterNow = false; iceStart = winterStart = 0; iceSnowMax = winterTransMin = winterT0 = winterTMin = 0;
        iceLiquid0 = iceLiquidMin = iceFrozen0 = iceFrozenMax = 0; iceSea0 = 0;
        IceAges = VolcanicWinters = MegaEruptions = CatastropheCount = 0; WaterHand = 0;
        forcedFlares.Clear(); droughts.Clear(); RebuildDroughts();
    }

    // ---- observation ----

    public static readonly string[] ClimNames = { "tilt_deg", "ecc", "sun_out", "summer65_n", "summer65_s", "glaci_n", "glaci_s", "veil_trans",
        "ice_ages", "volc_winters", "mega_eruptions", "water_liquid", "water_frozen", "sea_cells", "snow_cover" };

    public double[] ClimCensus()
    {
        var (liquid, ice, snow, air) = WaterParts();
        double total = Math.Max(1e-9, liquid + ice + snow + air);
        return new double[]
        {
            TiltAt(Tick) * 180 / Math.PI, EccAt(Tick), SunDrift(Tick), CyclesLaw ? SummerInsol(1, Tick) : 1, CyclesLaw ? SummerInsol(-1, Tick) : 1,
            GlaciN, GlaciS, VeilTransMean, IceAges, VolcanicWinters, MegaEruptions, liquid / total, (ice + snow) / total, SeaCells() / (double)N, SnowCover(),
        };
    }

    // A line for the HUD: the epoch and the phase of the cycles.
    public string EpochLine(bool full)
    {
        if (!climOn && !CyclesLaw) return Loc.T("climate is stationary", "климат стационарен");
        string epoch = iceAgeNow ? Loc.T($"ICE AGE (north {GlaciN:P0}, south {GlaciS:P0})", $"ЛЕДНИКОВЬЕ (север {GlaciN:P0}, юг {GlaciS:P0})")
                     : GlaciN > 0.05f || GlaciS > 0.05f ? Loc.T($"interglacial, glaciers: north {GlaciN:P0}, south {GlaciS:P0}", $"межледниковье, ледники: север {GlaciN:P0}, юг {GlaciS:P0}")
                     : Loc.T("interglacial", "межледниковье");
        if (winterNow) epoch += Loc.T($" · VOLCANIC WINTER: light {VeilTransMean:P0}", $" · ВУЛКАНИЧЕСКАЯ ЗИМА: свет {VeilTransMean:P0}");
        else if (veilOn) epoch += Loc.T($" · ash: light {VeilTransMean:P0}", $" · пепел: свет {VeilTransMean:P0}");
        if (dryOn) epoch += Loc.T($" · droughts {droughts.Count}", $" · засух {droughts.Count}");
        if (!CyclesLaw) return Loc.T("epoch: " + epoch + " (cycles off)", "эпоха: " + epoch + " (циклы выключены)");
        float tiltDeg = TiltNow * 180 / MathF.PI, dir = TiltAt(Tick + P.DayLen) - TiltNow;
        double peri = PeriNow, toN = Math.Cos(peri - Math.PI / 2);
        string periText = EccNow < 0.004f ? Loc.T("orbit nearly circular", "орбита почти круглая")
            : Loc.T($"ecc. {EccNow:0.000}, perihelion {(toN > 0.5 ? "in northern summer" : toN < -0.5 ? "in southern summer" : "between the seasons")}",
                $"эксц. {EccNow:0.000}, перигелий {(toN > 0.5 ? "летом севера" : toN < -0.5 ? "летом юга" : "в межсезонье")}");
        string phase = Loc.T($"obliquity {tiltDeg:0.0}° {(dir >= 0 ? "↑" : "↓")} (phase {(CycleDays(Tick) / TiltPeriodDays % 1.0):0.00} of {TiltPeriodDays:0} days)",
            $"наклон {tiltDeg:0.0}° {(dir >= 0 ? "↑" : "↓")} (фаза {(CycleDays(Tick) / TiltPeriodDays % 1.0):0.00} из {TiltPeriodDays:0} сут)");
        return full
            ? Loc.T($"epoch: {epoch} · {phase} · {periText} · sun {(SunOutNow - 1) * 100:+0.0;-0.0;0.0}% · summer at 65°: N {SummerN:P0}, S {SummerS:P0} (threshold {P.IceAgeThreshold:P0})",
                $"эпоха: {epoch} · {phase} · {periText} · солнце {(SunOutNow - 1) * 100:+0.0;-0.0;0.0}% · лето на 65°: с. {SummerN:P0}, ю. {SummerS:P0} (порог {P.IceAgeThreshold:P0})")
            : Loc.T($"epoch: {epoch} · obliquity {tiltDeg:0.0}° · summer 65°: {SummerN:P0}/{SummerS:P0}",
                $"эпоха: {epoch} · наклон {tiltDeg:0.0}° · лето 65°: {SummerN:P0}/{SummerS:P0}");
    }
}

public enum CatastropheKind { IceAge, Flood, VolcanicWinter, SolarFlare, Drought, Poison }

// A catastrophe the player sets off (World.Catastrophe). Its spec — "iceage days=5", "flood k=1.5",
// "volcano x=10 y=40 blocks=60 ash=0.4", "flare power=20 days=0.25", "drought x=… y=… r=24 days=5 dt=8",
// "poison x=… y=… r=8 n=30 [s5=0.6 s13=0.4]" — is what the law log keeps and the bench replays.
public sealed class Catastrophe
{
    public CatastropheKind Kind;
    public int X = -1, Y = -1;
    public float R, Days, Amount;
    // Poisoning: the substance as number fractions by species ("s5=0.6 s13=0.4" in the spec); none: the
    // world's most reactive species by the reactive-damage law. Label: the library name, for the chronicle
    // only (not in the spec: a replay does not depend on it).
    public float[] Mix;
    public string Label;

    static readonly string[] Words = { "iceage", "flood", "volcano", "flare", "drought", "poison" };
    static readonly string[] NamesEn = { "ice age", "flood", "volcanic winter", "strong solar flare", "drought", "poisoning" };
    static readonly string[] NamesRu = { "ледниковье", "потоп", "вулканическая зима", "сильная солнечная вспышка", "засуха", "отравление" };
    public static string[] Names => Loc.T(NamesEn, NamesRu);

    static string N(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    public string Spec() => Kind switch
    {
        CatastropheKind.IceAge => $"iceage days={N(Days)}",
        CatastropheKind.Flood => $"flood k={N(Amount)}",
        CatastropheKind.VolcanicWinter => $"volcano x={X} y={Y} blocks={N(Amount)} ash={N(R)}",
        CatastropheKind.SolarFlare => $"flare power={N(Amount)} days={N(Days)}",
        CatastropheKind.Drought => $"drought x={X} y={Y} r={N(R)} days={N(Days)} dt={N(Amount)}",
        _ => $"poison x={X} y={Y} r={N(R)} n={N(Amount)}" + MixSpec(),
    };

    string MixSpec()
    {
        if (Mix == null) return "";
        var sb = new System.Text.StringBuilder();
        for (int s = 0; s < Mix.Length; s++) if (Mix[s] > 0) sb.Append($" s{s}={N(Mix[s])}");
        return sb.ToString();
    }

    public override string ToString() => Spec();

    public static Catastrophe Parse(string spec)
    {
        var parts = spec.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new FormatException("empty catastrophe");
        int kind = Array.IndexOf(Words, parts[0].ToLowerInvariant());
        if (kind < 0) throw new FormatException($"unknown catastrophe '{parts[0]}' ({string.Join(", ", Words)})");
        var c = new Catastrophe { Kind = (CatastropheKind)kind };
        foreach (var p in parts.Skip(1))
        {
            int eq = p.IndexOf('=');
            if (eq <= 0) throw new FormatException($"'{p}': expected key=value");
            string key = p[..eq].ToLowerInvariant();
            float v = float.Parse(p[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture);
            if (key.Length > 1 && key[0] == 's' && int.TryParse(key[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int sp) && sp < Chemistry.S)
            {
                (c.Mix ??= new float[Chemistry.S])[sp] = v;
                continue;
            }
            switch (key)
            {
                case "x": c.X = (int)v; break;
                case "y": c.Y = (int)v; break;
                case "r": case "ash": c.R = v; break;
                case "days": c.Days = v; break;
                case "k": case "blocks": case "power": case "dt": case "n": c.Amount = v; break;
                default: throw new FormatException($"unknown key '{key}'");
            }
        }
        return c;
    }
}
