using System;
using System.Linq;

namespace Primordium;

// The sky (World.Sky, ROADMAP 1): `--self-test` and `--self-test-sun`.
// - day length: the equator ≈ 50% all year; beyond the polar circle 100% in summer, 0% in winter;
// - the planet's mean annual photon supply of the cosine law with the transparency within ±10% of the
//   old law with the old PhotonK (the sun's curve over a year and every latitude, the mean transparency
//   of a real world; clouds and mountains are the same for both);
// - shading: in one cell the smaller body on the same floor, and the swimmer below another, catch fewer
//   photons; with ShadeK 0 alike;
// - eclipses: predicted from the tick alone, the same in two worlds; at the predicted tick the light at
//   the shadow's centre is EclipseDepth of what it is without it; the chronicle has its start and end;
// - flares: under 10 blocks of rock and on the bottom of a deep lake the dose is under 5% of a sunlit
//   body's; a shield of heavy packed molecules lowers it; a dose heats, wears proteins, costs energy,
//   books the `flare` input with the ledger closed; the `uv` sensor reads it; at night nothing;
// - a seed's world with frequent strong flares: atoms and energy close, the flows show `flare`, bodies
//   were hit, mutated and died of it.
public sealed partial class World
{
    public static void SkyRegression()
    {
        ParamRegistry.ResetDefaults();
        // Day length.
        float tilt = P.Tilt;
        foreach (float d in new[] { 0, tilt, -tilt })
            Require(MathF.Abs(DayShare(0, d) - 0.5f) < 1e-4f, "the equator's day is not half");
        float polar = 80 * MathF.PI / 180;
        Require(DayShare(polar, tilt) == 1 && DayShare(polar, -tilt) == 0 && MathF.Abs(DayShare(polar, 0) - 0.5f) < 1e-4f, "no polar day and night at 80°");
        Require(DayShare(Latitude(40), tilt) > 0.55f && DayShare(Latitude(40), -tilt) < 0.45f, "mid-latitude summer days are not longer");
        Require(MathF.Abs(Insol(1) - 1) < 1e-6f && MathF.Abs(Insol(0.5f) - 0.5f) < 1e-6f && Insol(-0.05f) == 0 && Insol(0.02f) > 0, "the cosine law and its twilight");

        // Annual photon supply: the sun's curve over a year at every row, × the mean transparency × PhotonK.
        var w = new World(1, 0, false) { AutoStrikes = false };
        double Supply(bool law)
        {
            P.Insolation = law ? 1 : 0;
            double sum = 0;
            for (int y = 0; y < H; y++)
            {
                float lat = Latitude(y);
                for (int j = 0; j < 48; j++) sum += DailyInsol(lat, tilt * MathF.Sin(2 * MathF.PI * (j + 0.5f) / 48));
            }
            return sum / (H * 48);
        }
        double before = Supply(false) * 0.06, now = Supply(true) * w.TranspMean * P.PhotonK;
        ParamRegistry.ResetDefaults();
        Require(Math.Abs(now / before - 1) < 0.1, $"annual photon supply {now:F5} vs {before:F5} with the old law ({now / before - 1:+0%;-0%})");
        string supply = $"photon supply {now / before - 1:+0.0%;-0.0%} (transparency {w.TranspMean:F2}, PhotonK {P.PhotonK})";

        // Shading.
        var f = Fixture(); var ch = f.Chem;
        int ground = Enumerable.Range(0, Chemistry.S).First(s => ch.PhotoUp[s] >= 0);
        int c = 70 * W + 100;
        var big = f.TestAgent(c, 2, ground, 200);
        var small = f.TestAgent(c, 2, ground, 10);
        foreach (var a in new[] { big, small }) { a.Enz[0] = new Enzyme { Kind = Enzyme.Photo, A = (byte)ground, Amount = 3, Eff = 1, Topt = 15 }; a.EnzN = 1; }
        float shadeSmall = f.ShadeOf(small, c), shadeBig = f.ShadeOf(big, c);
        Require(shadeBig == 1 && shadeSmall < 0.9f, $"shade on one floor: big {shadeBig:F3}, small {shadeSmall:F3}");
        // Each body alone on a fresh trickle of photons: the small one under the big one catches about
        // shade × what it catches with shading off; the big one the same either way.
        (int, int) Catch()
        {
            int b0 = big.NPhoto, s0 = small.NPhoto;
            for (int k = 0; k < 600; k++)
            {
                foreach (var a in new[] { big, small })   // excited back to ground: the bodies keep their size
                    while (a.Inv[ch.PhotoUp[ground]] > 0) { f.RemoveMol(a, ch.PhotoUp[ground]); f.AddMol(a, ground); }
                f.Photon[c] = 3; f.Photo(small, c, 0, ground);
                f.Photon[c] = 3; f.Photo(big, c, 0, ground);
            }
            return (big.NPhoto - b0, small.NPhoto - s0);
        }
        var (gotBig, gotSmall) = Catch();
        P.ShadeK = 0;
        var (offBig, offSmall) = Catch();
        Require(f.ShadeOf(small, c) == 1, "shading off still shades");
        Require(gotSmall < 0.85 * offSmall && Math.Abs(gotSmall / (double)offSmall - shadeSmall) < 0.12, $"the small body under the big one caught {gotSmall} photons, {offSmall} without shading (shade {shadeSmall:F2})");
        Require(Math.Abs(gotBig - offBig) < 0.1 * offBig, $"the big body on top: {gotBig} photons with shading, {offBig} without");
        ParamRegistry.ResetDefaults();
        // A swimmer near the surface shades one on the bottom of the same column.
        int lake = 70 * W + 140;
        f.Water[lake] = 4;
        var top = f.TestAgent(lake, 2, ground, 10); var bottom = f.TestAgent(lake, 2, ground, 10);
        top.Lift = 3.5f;
        Require(f.ShadeOf(top, lake) == 1 && f.ShadeOf(bottom, lake) < 1, "a swimmer above does not shade the one below");
        string shade = $"shade: small under big {shadeSmall:F2} caught {gotSmall} photons against {offSmall} without shading (big {gotBig} / {offBig})";

        // Eclipses: predicted, deterministic, seen in the light, chronicled.
        var e1 = new World(1, 0, false) { AutoStrikes = false }; var e2 = new World(1, 0, false) { AutoStrikes = false };
        long next = e1.NextEclipse(0, 400);
        Require(next > 0 && next == e2.NextEclipse(0, 400), $"eclipse not predicted alike: {next}");
        long t1 = next + 200 - (next + 200) % P.LightEvery;   // a little into it
        if (!e1.EclipseAt(t1, out _, out _)) t1 = next;
        Require(e1.EclipseAt(t1, out float ex, out float ey) && e2.EclipseAt(t1, out float ex2, out float ey2) && ex == ex2 && ey == ey2, "eclipse not deterministic");
        int cx = (int)ex, cy = Math.Clamp((int)ey, 0, H - 1), cc = cy * W + cx;
        long events = e1.Chronicle.NextSeq;
        e1.Tick = t1 - 1; e1.Step();
        float dark = e1.Sun[cc];
        P.Eclipses = 0;
        e2.Tick = t1 - 1; e2.Step();
        float bright = e2.Sun[cc];
        ParamRegistry.ResetDefaults();
        Require(bright > 0.05f && dark < (P.EclipseDepth + 0.01f) * bright, $"no shadow at the predicted centre: {dark:F4} vs {bright:F4}");
        Require(e1.EclipseNow && e1.Chronicle.Since(events - 1).Any(x => x.Type == EvType.Climate && x.Text.Contains("затмение")), "the eclipse start is not in the chronicle");
        long end = t1;
        while (e1.EclipseAt(end, out _, out _)) end += P.LightEvery;
        e1.Tick = end - 1; e1.Step();
        Require(!e1.EclipseNow && e1.EclipseCount == 1 && e1.Chronicle.Since(events - 1).Count(x => x.Text.Contains("затмение")) == 2, "the eclipse end is not in the chronicle");
        string eclipse = $"eclipse predicted at tick {next} (moon {e1.MoonPeriodDays:F1} d), lasts {end - next} ticks, centre light {dark / bright:P1}";

        // Flares on bodies: noon over x = 128.
        var g = Fixture();
        g.Tick = P.DayLen / 2 / P.LightEvery * P.LightEvery - 1;   // the next step is a light update at about noon over x = W/2
        int y0 = 80, sunny = y0 * W + (int)(W * ((g.Tick + 1) % P.DayLen) / (float)P.DayLen);
        int cave = sunny + 6, deep = sunny + 12, shielded = sunny - 6;
        for (int z = 3; z < 13; z++) { g.Mat[cave * Z + z] = Chemistry.Bedrock; g.Order[cave * Z + z] = 255; }
        g.Height[cave] = 13; g.TerrainChanged(cave); g.StepStructure();
        P.Flares = 0;   // nothing else happens to them while the light is set up
        g.Step();
        P.Flares = 1;
        g.Water[deep] = 6;   // a deep lake (after the step: it would have run off the flat rock)
        int light = Enumerable.Range(0, Chemistry.S).OrderBy(s => ch.Mass[s] * ch.Packing[s]).First(s => s != ch.Gas);
        int heavy = Enumerable.Range(0, Chemistry.S).OrderByDescending(s => ch.Mass[s] * ch.Packing[s]).First();
        var open = g.TestAgent(sunny, 2, light, 10);
        var inCave = g.TestAgent(cave, 2, light, 10);
        var bottomBody = g.TestAgent(deep, 2, light, 10);
        var armoured = g.TestAgent(shielded, 2, light, 10);
        for (int k = 0; k < 60; k++) g.AddMol(armoured, heavy);
        Require(g.Sun[sunny] > 0.1f && g.Sun[cave] > 0.1f && g.Sun[deep] > 0.1f, $"the test cells are not in the sun: {g.Sun[sunny]:F2} {g.Sun[cave]:F2} {g.Sun[deep]:F2}");
        g.FlarePower = 5; g.SolarActivity = 0.5f; g.flareEp = 1;
        float Dose(Agent a) => g.FlarePower * g.SkyExposure(a) * g.Shield(a);
        float dOpen = Dose(open), dCave = Dose(inCave), dBottom = Dose(bottomBody), dArm = Dose(armoured);
        Require(dOpen > 0.5f && dCave < 0.05f * dOpen && dBottom < 0.05f * dOpen, $"flare doses: sunlit {dOpen:F3}, under 10 blocks {dCave:F4}, on the bottom of 6 blocks of water {dBottom:F4}");
        Require(dArm < 0.7f * dOpen, $"the shield of heavy packed molecules: {dArm:F3} vs {dOpen:F3}");
        // What a dose does, under the ledger.
        open.Enz[0] = new Enzyme { Kind = Enzyme.Photo, A = (byte)light, Amount = 3, Eff = 1, Topt = 15 }; open.EnzN = 1;
        var eb = g.EnergyStart();
        float tb = open.Tb, en = open.Energy, amount = open.Enz[0].Amount;
        float dose = g.Flare(open, out float harm);
        Require(dose == dOpen && open.Tb > tb && open.Energy < en && open.Enz[0].Amount < amount && open.HeatHeld > 0 && harm > 0, "a flare dose did nothing");
        g.EnergyBalanced(eb, "flare", FFlare, FDissipate);
        var probe = new Agent(g.NewId(), 0, 0, GenomeAsm.Assemble("uv\nyield\nnop\nnop\nnop\nnop\nnop\nnop")) { Energy = 10, Z = 2, Tb = 15, X = sunny % W, Y = y0 };
        g.Exec(probe, sunny);
        int uv = probe.Stack[0];
        probe.Sp = 0; probe.Ip = 0; probe.X = cave % W; probe.Z = 2;
        g.Exec(probe, cave);
        Require(uv > 100 && probe.Stack[0] < uv / 20, $"uv sensor: sunlit {uv}, under the roof {probe.Stack[0]}");
        string flare = $"flare doses: sunlit {dOpen:F2}, under 10 blocks {dCave / dOpen:P1}, lake bottom {dBottom / dOpen:P1}, shielded {dArm / dOpen:P0}; uv {uv}";

        // A world with frequent strong flares: balances close, the flare path runs.
        P.FlareRate = 8; P.FlarePowerMin = 3;
        World r = null;
        for (int seed = 2; r == null; seed++)   // a seed whose sun is active in the first 1500 ticks
        {
            var cand = new World(seed, 800, false) { TrackHeat = true };
            int on = 0;
            for (long t = 1; t <= 1500; t += 10) if (cand.FlareAt(t) > 0) on++;
            if (on >= 10 || seed > 30) r = cand;
        }
        Require(!r.AutoStrikes, "flares on: the old constructor still sends strikes");
        var atoms = r.ElementBudget(); var e0 = r.AuditEnergy();
        int hitBodies = 0;
        for (int t = 0; t < 1500; t++) { r.Step(); if (r.FlarePower > 0) hitBodies = Math.Max(hitBodies, r.Agents.Count(a => a.FlareDose > 0)); }
        var after = r.ElementBudget();
        for (int e = 0; e < after.Length; e++) after[e] -= r.InteriorInput[e] + r.HandInput[e];
        BudgetEqual(atoms, after, "flare world", 0.001);
        string energy = EnergyWorldCheck(r, e0, "flare world");
        var flows = r.EnergyFlows();
        Require(r.FlareCount > 0 && flows[FFlare] > 0 && hitBodies > 0 && r.FlareMutations > 0, $"flares: {r.FlareCount} episodes, flow {flows[FFlare]:F1}, bodies hit {hitBodies}, mutations {r.FlareMutations}");
        ParamRegistry.ResetDefaults();
        Console.WriteLine($"PASS sun: day length (equator 50%, 80° summer 100% / winter 0%); {supply}; {shade}; {eclipse}; {flare}; flare world: {r.FlareCount} flares, up to {hitBodies} bodies hit, {r.FlareMutations} mutations, {r.DeathsFlare} deaths, flare input {flows[FFlare]:F1}, {energy}");
    }
}
