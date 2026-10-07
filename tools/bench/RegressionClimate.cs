using System;
using System.IO;
using System.Linq;

namespace Primordium;

// Climate cycles and catastrophes (World.ClimateCycles, ROADMAP 2.2, 9.4): `--self-test` and `--self-test-climate`.
// - the orbit: neutral at ClimT0 (tilt = Tilt, circular orbit, output 1), periodic, the summer insolation
//   at 65° follows tilt and perihelion (the hemisphere whose summer meets the perihelion is above the other);
// - the law off: nothing of it runs (no climate state, the tilt and the light as before);
// - determinism: two worlds of one seed with fast cycles and frequent mega-eruptions are the same;
// - water: a world without life through a forced ice age — the glaciation grows, snow and ice spread, the
//   sea gives up water, and the world's water (ground, ice, snow, air) stays what it was; a flood is
//   booked as water from outside;
// - a mega-eruption under the balances: atoms close with InteriorInput, energy with the `vent` input,
//   the ash dims the light and cools the planet, the chronicle has the eruption and the volcanic winter;
// - catastrophes: all six in one world with life, saved while they run and loaded — the loaded world goes
//   on the same; a fresh world of the seed replaying the law log (laws and catastrophes) is the same world.
public sealed partial class World
{
    public static void ClimateCyclesRegression()
    {
        ParamRegistry.ResetDefaults();

        // The orbit: neutral at the origin, then moving.
        var o = new World(1, 0, false) { AutoStrikes = false };
        Require(o.TiltAt(0) == P.Tilt && o.EccAt(0) == 0 && o.SunDrift(0) == 1 && Math.Abs(o.SummerInsol(1, 0) - 1) < 1e-4, "the cycles are not neutral at their origin");
        P.TiltPeriod = 30; P.EccPeriod = 60; P.PrecPeriod = 20;
        long quarter = 30 * P.DayLen / 4;
        float tq = o.TiltAt(quarter);
        Require(Math.Abs(Math.Abs(tq - P.Tilt) - P.TiltAmp) < 1e-4, $"tilt at a quarter period {tq}");
        Require(Math.Abs(o.TiltAt(30 * P.DayLen) - P.Tilt) < 1e-4, "the tilt is not periodic");
        // Lower tilt — colder high-latitude summers in both hemispheres; higher — warmer.
        long low = o.TiltAt(quarter) < P.Tilt ? quarter : 3 * quarter, high = 4 * quarter - low;
        Require(o.SummerInsol(1, low) < 0.97f && o.SummerInsol(1, high) > 1.03f, $"summer at 65°: low tilt {o.SummerInsol(1, low):F3}, high {o.SummerInsol(1, high):F3}");
        // With an eccentric orbit the hemisphere whose summer meets the perihelion has the warmer summer.
        long ecc = 30 * P.DayLen;   // half the eccentricity period: the most eccentric
        double peri = o.PerihelionAt(ecc), toN = Math.Cos(peri - Math.PI / 2);
        float n = o.SummerInsol(1, ecc), s = o.SummerInsol(-1, ecc);
        Require(o.EccAt(ecc) > 0.9f * P.EccAmp && (toN > 0.2 ? n > s : toN < -0.2 ? s > n : true), $"perihelion {peri:F2}: summers north {n:F3}, south {s:F3}");
        string orbit = $"tilt {P.Tilt * 180 / MathF.PI:0.0}±{P.TiltAmp * 180 / MathF.PI:0.0}°, summer at 65° {o.SummerInsol(1, low):P0}…{o.SummerInsol(1, high):P0}, eccentric orbit: north {n:P0} / south {s:P0}";
        ParamRegistry.ResetDefaults();

        // The law off: nothing runs.
        P.ClimateCycles = 0;
        var off = new World(3, 300, true) { AutoStrikes = false };
        for (int t = 0; t < 400; t++) off.Step();
        Require(!off.climOn && off.GlaciN == 0 && off.GlaciS == 0 && !off.veilOn && off.TiltAt(off.Tick) == P.Tilt && off.climT0 == 0, "the law off: something of the cycles ran");
        Require(off.MegaEruptionOfDay(3, out _) == false, "the law off still schedules eruptions");
        ParamRegistry.ResetDefaults();

        // Determinism with fast cycles and frequent eruptions.
        P.TiltPeriod = 8; P.PrecPeriod = 5; P.EccPeriod = 10; P.MegaEruptionRate = 1; P.IceAgeTau = 0.3f;
        var d1 = new World(2, 300, true); var d2 = new World(2, 300, true);
        for (int t = 0; t < 1800; t++) { d1.Step(); d2.Step(); }
        Require(d1.MegaEruptions >= 1 && d1.DeepHash() == d2.DeepHash() && d1.GlaciN == d2.GlaciN && d1.Veil.SequenceEqual(d2.Veil), $"two worlds of one seed differ ({d1.MegaEruptions} eruptions)");
        string determinism = $"1.5 days: {d1.MegaEruptions} eruptions, glaciation {d1.GlaciN:F2}/{d1.GlaciS:F2}, same world";
        ParamRegistry.ResetDefaults();

        // Water through an ice age (no life: only the water cycle moves water).
        P.IceAgeTau = 0.3f; P.IceAgeDT = 14;
        var iw = new World(4, 0, false) { AutoStrikes = false };
        double w0 = iw.WaterTotal();
        var (liquid0, ice0, snow0, _) = iw.WaterParts();
        int sea0 = iw.SeaCells();
        iw.Catastrophe(new Catastrophe { Kind = CatastropheKind.IceAge, Days = 4 }, out _);
        float tempBefore = iw.MeanTemp();
        for (int t = 0; t < 4 * P.DayLen; t++) iw.Step();
        double w1 = iw.WaterTotal();
        var (liquid1, ice1, snow1, _) = iw.WaterParts();
        int sea1 = iw.SeaCells();
        float tempAfter = iw.MeanTemp();
        Require(iw.GlaciN > 0.9f && iw.GlaciS > 0.9f && iw.IceAges == 1, $"the forced ice age did not grow: {iw.GlaciN:F2}/{iw.GlaciS:F2}");
        Require(tempAfter < tempBefore - 3, $"the ice age did not cool: {tempBefore:F1} → {tempAfter:F1} °C");
        Require(ice1 + snow1 > 1.3 * (ice0 + snow0) + 10 && liquid1 < liquid0, $"snow and ice did not spread: ice {ice0:F0}→{ice1:F0}, snow {snow0:F0}→{snow1:F0}, liquid {liquid0:F0}→{liquid1:F0}");
        Require(Math.Abs(w1 - w0) < 2e-5 * w0, $"water not conserved through the ice age: {w0:F2} → {w1:F2} ({(w1 - w0) / w0:E2})");
        string iceText = $"forced ice age 4 days: {tempBefore:F1} → {tempAfter:F1} °C, snow+ice {ice0 + snow0:F0} → {ice1 + snow1:F0}, liquid {liquid0:F0} → {liquid1:F0} (sea cells {sea0} → {sea1}), water {w0:F1} → {w1:F1} ({(w1 - w0) / w0:+0.0E0;-0.0E0})";
        // A flood: water from outside, booked.
        iw.Catastrophe(new Catastrophe { Kind = CatastropheKind.Flood, Amount = 1.5f }, out _);
        for (int t = 0; t < P.DayLen; t++) iw.Step();
        double w2 = iw.WaterTotal();
        Require(iw.WaterHand > 1000 && Math.Abs(w2 - w0 - iw.WaterHand) < 2e-5 * w2, $"flood not booked: total {w2:F1}, start {w0:F1}, from outside {iw.WaterHand:F1}");
        // The ice age ends: the glaciation retreats with the forcing gone and the law's summers (the cycles start neutral: above the threshold).
        for (int t = 0; t < 3 * P.DayLen && iw.IceAgeNow; t++) iw.Step();
        Require(!iw.IceAgeNow && iw.Chronicle.All().Count(e => e.Type == EvType.Climate && e.Text.Contains("ледниковье")) == 2, "the ice age did not end in the chronicle");
        ParamRegistry.ResetDefaults();

        // A mega-eruption under the balances.
        var m = new World(1, 400, true) { TrackHeat = true, AutoStrikes = false };
        var atoms0 = m.ElementBudget(); var e0 = m.AuditEnergy();
        for (int t = 0; t < 300; t++) m.Step();
        double vent0 = m.EnergyFlows()[FVent];
        float sun0 = m.Sun.Average();
        float temp0 = m.MeanTemp();
        long ev0 = m.Chronicle.NextSeq;
        m.Catastrophe(new Catastrophe { Kind = CatastropheKind.VolcanicWinter, Amount = 80, R = 0.6f }, out string err);
        Require(err == null && m.MegaEruptions == 1 && m.veilOn, "no eruption: " + err);
        for (int t = 0; t < 1500; t++) m.Step();
        var atoms1 = m.ElementBudget();
        for (int e = 0; e < atoms1.Length; e++) atoms1[e] -= m.InteriorInput[e] + m.HandInput[e];
        BudgetEqual(atoms0, atoms1, "mega-eruption", 0.001);
        string energy = EnergyWorldCheck(m, e0, "mega-eruption");
        double vent = m.EnergyFlows()[FVent] - vent0;
        float sun1 = m.Sun.Average(), temp1 = m.MeanTemp();
        Require(vent > 0 && m.VeilTransMean < 0.8f && m.VolcanicWinterNow, $"the eruption: vent input {vent:F0}, light through the ash {m.VeilTransMean:P0}");
        Require(temp1 < temp0 - 2, $"the volcanic winter did not cool: {temp0:F1} → {temp1:F1} °C");
        Require(m.Chronicle.Since(ev0 - 1).Any(x => x.Text.Contains("мегаизвержение")) && m.Chronicle.Since(ev0 - 1).Any(x => x.Text.Contains("вулканическая зима")), "the eruption or the winter is not in the chronicle");
        string eruption = $"mega-eruption: vent input {vent:F0}, after 1.25 days light through the ash {m.VeilTransMean:P0}, sun at the surface {sun0:F3} → {sun1:F3}, {temp0:F1} → {temp1:F1} °C, atoms exact, {energy}";
        ParamRegistry.ResetDefaults();

        // Catastrophes, saved while they run, loaded, and replayed from the law log.
        var a = new World(3, 400, true) { TrackHeat = true, AutoStrikes = false };
        var ea = a.AuditEnergy();
        var plan = new (long tick, Catastrophe c)[]
        {
            (100, new Catastrophe { Kind = CatastropheKind.Drought, X = 60, Y = 50, R = 20, Days = 2, Amount = 8 }),
            (150, new Catastrophe { Kind = CatastropheKind.SolarFlare, Amount = 15, Days = 0.3f }),
            (200, new Catastrophe { Kind = CatastropheKind.Poison, X = 100, Y = 80, R = 6, Amount = 25 }),
            (250, new Catastrophe { Kind = CatastropheKind.IceAge, Days = 2 }),
            (300, new Catastrophe { Kind = CatastropheKind.Flood, Amount = 0.5f }),
            (350, new Catastrophe { Kind = CatastropheKind.VolcanicWinter }),
        };
        bool poison = a.Chem.Toxic.Length > 0;
        var atomsA = a.ElementBudget();
        for (int t = 1; t <= 420; t++)
        {
            foreach (var (tick, c) in plan) if (tick == a.Tick) { var done = a.Catastrophe(c, out string error); Require(done != null || (c.Kind == CatastropheKind.Poison && !poison), $"{c.Spec()}: {error}"); }
            if (a.Tick == 330) a.SetParam("ClimSens", 30);
            a.Step();
        }
        Require(a.CatastropheCount == (poison ? 6 : 5) && a.ParamLog.Count(p => p.Name.StartsWith("catastrophe ")) == a.CatastropheCount, "catastrophes not logged");
        Require(a.Chronicle.All().Count(e => e.Type == EvType.Player && e.Text.StartsWith("катастрофа игрока")) == a.CatastropheCount, "catastrophes not chronicled");
        string path = Path.Combine(TestDir(), "climate.sav");
        a.Save(path, "climate self-test");
        ParamRegistry.ResetDefaults();
        var b = Load(path);
        File.Delete(path);
        Require(P.ClimSens == 30 && b.DeepHash() == a.DeepHash() && b.Veil.SequenceEqual(a.Veil) && b.GlaciN == a.GlaciN && b.dryOn == a.dryOn && b.forcedFlares.Count == a.forcedFlares.Count, "the loaded world differs");
        for (int t = 0; t < 600; t++)
        {
            a.Step(); b.Step();
            if (t % 200 == 199) Require(a.DeepHash() == b.DeepHash(), $"the loaded world diverged at tick {a.Tick}");
        }
        Require(a.WaterHand == b.WaterHand && a.IceAges == b.IceAges && a.VolcanicWinters == b.VolcanicWinters, "epochs differ after the load");
        var atomsEnd = a.ElementBudget();
        for (int e = 0; e < atomsEnd.Length; e++) atomsEnd[e] -= a.InteriorInput[e] + a.HandInput[e];
        BudgetEqual(atomsA, atomsEnd, "catastrophes", 0.001);
        string energyA = EnergyWorldCheck(a, ea, "catastrophes");
        // Replay: the seed plus the law log.
        ParamRegistry.ResetDefaults();
        var r = new World(3, 400, true) { AutoStrikes = false };
        var log = a.ParamLog.ToList();
        while (r.Tick < a.Tick)
        {
            foreach (var entry in log)
                if (entry.Tick == r.Tick)
                {
                    if (entry.Name.StartsWith("catastrophe ")) r.Catastrophe(Primordium.Catastrophe.Parse(entry.Name["catastrophe ".Length..]), out _);
                    else r.SetParam(entry.Name, entry.Value);
                }
            r.Step();
        }
        Require(r.DeepHash() == a.DeepHash(), "replaying the law log gave another world");
        ParamRegistry.ResetDefaults();
        Console.WriteLine($"PASS climate cycles: {orbit}; law off — nothing runs; {determinism}; {iceText}; flood +{iw.WaterHand:F0} booked; {eruption}; " +
                          $"{a.CatastropheCount} catastrophes (drought, flare, {(poison ? "poison, " : "")}ice age, flood, volcano) saved mid-way, loaded and replayed from the law log — same world, atoms exact, {energyA}");
    }
}
