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
// - the canopy (variant B, Canopy 1): photons are conserved cell by cell (stopped + swallowed by water +
//   beyond a store + reaching the ground = what fell); the top body stops more than the same body lower
//   down, a lower one gets exactly what passes the upper ones; a swimmer stops light before the bottom
//   dweller and the water between them takes its share; a big body gets its share in every cell of its
//   footprint; stores fill up to PhotonCap per cell; `photo` spends only the body's own store; ShadeK 0 is
//   the shared pool again; a canopy world closes atoms and energy and saves and loads with its stores;
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
        float lat40 = (0.5f - 40.5f / 160) * MathF.PI * 0.92f;   // row 40 of 160
        Require(DayShare(lat40, tilt) > 0.55f && DayShare(lat40, -tilt) < 0.45f, "mid-latitude summer days are not longer");
        Require(MathF.Abs(Insol(1) - 1) < 1e-6f && MathF.Abs(Insol(0.5f) - 0.5f) < 1e-6f && Insol(-0.05f) == 0 && Insol(0.02f) > 0, "the cosine law and its twilight");

        // Annual photon supply: the sun's curve over a year at every row, × the mean transparency × PhotonK.
        var w = new World(1, 0, false) { AutoStrikes = false };
        double Supply(bool law)
        {
            P.Insolation = law ? 1 : 0;
            double sum = 0;
            for (int y = 0; y < w.H; y++)
            {
                float lat = w.Latitude(y);
                for (int j = 0; j < 48; j++) sum += DailyInsol(lat, tilt * MathF.Sin(2 * MathF.PI * (j + 0.5f) / 48));
            }
            return sum / (w.H * 48);
        }
        double before = Supply(false) * 0.06, now = Supply(true) * w.TranspMean * P.PhotonK;
        ParamRegistry.ResetDefaults();
        Require(Math.Abs(now / before - 1) < 0.1, $"annual photon supply {now:F5} vs {before:F5} with the old law ({now / before - 1:+0%;-0%})");
        string supply = $"photon supply {now / before - 1:+0.0%;-0.0%} (transparency {w.TranspMean:F2}, PhotonK {P.PhotonK})";

        // Shading.
        var f = Fixture(); var ch = f.Chem;
        int ground = Enumerable.Range(0, Chemistry.S).First(s => ch.PhotoUp[s] >= 0);
        int c = 70 * w.W + 100;
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
        int lake = 70 * w.W + 140;
        f.Water[lake] = 4;
        var top = f.TestAgent(lake, 2, ground, 10); var bottom = f.TestAgent(lake, 2, ground, 10);
        top.Lift = 3.5f;
        Require(f.ShadeOf(top, lake) == 1 && f.ShadeOf(bottom, lake) < 1, "a swimmer above does not shade the one below");
        string shade = $"shade: small under big {shadeSmall:F2} caught {gotSmall} photons against {offSmall} without shading (big {gotBig} / {offBig})";
        string canopy = CanopyRegression();

        // Eclipses: predicted, deterministic, seen in the light, chronicled.
        var e1 = new World(1, 0, false) { AutoStrikes = false }; var e2 = new World(1, 0, false) { AutoStrikes = false };
        long next = e1.NextEclipse(0, 400);
        Require(next > 0 && next == e2.NextEclipse(0, 400), $"eclipse not predicted alike: {next}");
        long t1 = next + 200 - (next + 200) % P.LightEvery;   // a little into it
        if (!e1.EclipseAt(t1, out _, out _)) t1 = next;
        Require(e1.EclipseAt(t1, out float ex, out float ey) && e2.EclipseAt(t1, out float ex2, out float ey2) && ex == ex2 && ey == ey2, "eclipse not deterministic");
        int cx = (int)ex, cy = Math.Clamp((int)ey, 0, w.H - 1), cc = cy * w.W + cx;
        long events = e1.Chronicle.NextSeq;
        e1.Tick = t1 - 1; e1.Step();
        float dark = e1.Sun[cc];
        P.Eclipses = 0;
        e2.Tick = t1 - 1; e2.Step();
        float bright = e2.Sun[cc];
        ParamRegistry.ResetDefaults();
        Require(bright > 0.05f && dark < (P.EclipseDepth + 0.01f) * bright, $"no shadow at the predicted centre: {dark:F4} vs {bright:F4}");
        Require(e1.EclipseNow && e1.Chronicle.Since(events - 1).Any(x => x.Type == EvType.Climate && Ru(x.Text).Contains("затмение")), "the eclipse start is not in the chronicle");
        long end = t1;
        while (e1.EclipseAt(end, out _, out _)) end += P.LightEvery;
        e1.Tick = end - 1; e1.Step();
        Require(!e1.EclipseNow && e1.EclipseCount == 1 && e1.Chronicle.Since(events - 1).Count(x => Ru(x.Text).Contains("затмение")) == 2, "the eclipse end is not in the chronicle");
        string eclipse = $"eclipse predicted at tick {next} (moon {e1.MoonPeriodDays:F1} d), lasts {end - next} ticks, centre light {dark / bright:P1}";

        // Flares on bodies: noon over x = 128.
        var g = Fixture();
        g.Tick = P.DayLen / 2 / P.LightEvery * P.LightEvery - 1;   // the next step is a light update at about noon over x = W/2
        int y0 = 80, sunny = y0 * w.W + (int)(w.W * ((g.Tick + 1) % P.DayLen) / (float)P.DayLen);
        int cave = sunny + 6, deep = sunny + 12, shielded = sunny - 6;
        for (int z = 3; z < 13; z++) { g.Mat[cave * w.Z + z] = Chemistry.Bedrock; g.Order[cave * w.Z + z] = 255; }
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
        float tb = open.Tb, amount = open.Enz[0].Amount;
        double en = open.Energy;
        float dose = g.Flare(open, out float harm);
        Require(dose == dOpen && open.Tb > tb && open.Energy < en && open.Enz[0].Amount < amount && open.HeatHeld > 0 && harm > 0, "a flare dose did nothing");
        g.EnergyBalanced(eb, "flare", FFlare, FDissipate);
        var probe = new Agent(g.NewId(), 0, 0, GenomeAsm.Assemble("uv\nyield\nnop\nnop\nnop\nnop\nnop\nnop")) { Energy = 10, Z = 2, Tb = 15, X = sunny % w.W, Y = y0 };
        g.Exec(probe, sunny);
        int uv = probe.Stack[0];
        probe.Sp = 0; probe.Ip = 0; probe.X = cave % w.W; probe.Z = 2;
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
        Console.WriteLine($"PASS sun: day length (equator 50%, 80° summer 100% / winter 0%); {supply}; {shade}; {canopy}; {eclipse}; {flare}; flare world: {r.FlareCount} flares, up to {hitBodies} bodies hit, {r.FlareMutations} mutations, {r.DeathsFlare} deaths, flare input {flows[FFlare]:F1}, {energy}");
    }

    // Variant B of shading (P.Canopy 1): the canopy pass of one cell, the stores, `photo` on them, a world.
    static string CanopyRegression()
    {
        ParamRegistry.ResetDefaults();
        P.Canopy = 1;
        var f = Fixture(); var ch = f.Chem;
        int ground = Enumerable.Range(0, Chemistry.S).First(s => ch.PhotoUp[s] >= 0);
        var buf = new Agent[2];   // small on purpose: the pass grows it
        Agent Body(int cell, int count)
        {
            var a = f.TestAgent(cell, 2, ground, count);
            a.Enz[0] = new Enzyme { Kind = Enzyme.Photo, A = (byte)ground, Amount = 3, Eff = 1, Topt = 15 };
            a.EnzN = 1;
            return a;
        }
        float T(Agent a) => MathF.Exp(-P.ShadeK * Cover2(a));
        // A small body alone, then under a big one on the same floor, with a third, middle one.
        int c = 60 * f.W + 100;
        var small = Body(c, 10);
        float inflow = 1;
        float ground1 = f.CanopyCell(c, inflow, ref buf, out float w1, out float l1);
        float alone = small.LightQuota;
        Require(MathF.Abs(alone - inflow * (1 - T(small))) < 1e-6f && MathF.Abs(alone + ground1 + w1 + l1 - inflow) < 1e-6f, $"a lone body stopped {alone:F4} of {inflow} (transmission {T(small):F4}), ground {ground1:F4}");
        var big = Body(c, 200); var mid = Body(c, 60);
        foreach (var a in new[] { small, big, mid }) a.LightQuota = 0;
        float g = f.CanopyCell(c, inflow, ref buf, out float wat, out float lost);
        float sum = small.LightQuota + big.LightQuota + mid.LightQuota;
        Require(MathF.Abs(sum + g + wat + lost - inflow) < 1e-6f && wat == 0 && lost == 0, $"photons not conserved in a cell of three: stopped {sum:R}, ground {g:R}, water {wat:R}, lost {lost:R} of {inflow}");
        Require(big.LightQuota > mid.LightQuota && mid.LightQuota > small.LightQuota, $"on one floor the bigger does not stop more: {big.LightQuota:F4} {mid.LightQuota:F4} {small.LightQuota:F4}");
        float expectSmall = inflow * T(big) * T(mid) * (1 - T(small));
        Require(MathF.Abs(small.LightQuota - expectSmall) < 1e-6f && small.LightQuota < alone, $"layering: the small body under two got {small.LightQuota:F5}, expected {expectSmall:F5} (alone {alone:F5})");
        Require(MathF.Abs(g - inflow * T(big) * T(mid) * T(small)) < 1e-6f, "the ground does not get what passes all three");
        // The same body stops more on top than at the bottom: lift the small one above the others.
        foreach (var a in new[] { small, big, mid }) a.LightQuota = 0;
        small.Lift = 0.5f;
        f.CanopyCell(c, inflow, ref buf, out _, out _);
        float onTop = small.LightQuota;
        small.Lift = 0;
        Require(MathF.Abs(onTop - alone) < 1e-6f && onTop > 2 * expectSmall, $"the small body on top stopped {onTop:F4}, at the bottom {expectSmall:F4}");
        // Stores fill up to PhotonCap and the rest is counted as lost.
        double fell = 0, kept0 = big.LightQuota + mid.LightQuota + small.LightQuota, rest = 0;
        for (int k = 0; k < 200; k++)
        {
            fell += 1;
            rest += f.CanopyCell(c, 1, ref buf, out float wk, out float lk) + wk + lk;
        }
        double kept = big.LightQuota + mid.LightQuota + small.LightQuota - kept0;
        Require(big.LightQuota <= P.PhotonCap && MathF.Abs(big.LightQuota - P.PhotonCap) < 1e-4f && Math.Abs(kept + rest - fell) < 1e-3, $"stores: big {big.LightQuota:F3} of cap {P.PhotonCap}, kept {kept:F3} + passed/lost {rest:F3} of {fell}");
        // A swimmer stops light before the one on the bottom; the water between them takes its share.
        int lake = 60 * f.W + 140;
        f.Water[lake] = 4;
        var top = Body(lake, 10); var bottom = Body(lake, 10);
        top.Lift = 3.5f;
        float gl = f.CanopyCell(lake, inflow, ref buf, out float wl, out float ll);
        float atTop = inflow * MathF.Exp(-P.WaterDim * 0.5f);
        Require(MathF.Abs(top.LightQuota - atTop * (1 - T(top))) < 1e-6f && bottom.LightQuota < top.LightQuota
                && MathF.Abs(bottom.LightQuota - atTop * T(top) * MathF.Exp(-P.WaterDim * 3.5f) * (1 - T(bottom))) < 1e-6f
                && MathF.Abs(top.LightQuota + bottom.LightQuota + gl + wl + ll - inflow) < 1e-6f && wl > 0,
            $"water canopy: swimmer {top.LightQuota:F4}, bottom {bottom.LightQuota:F4}, water {wl:F4}, ground {gl:F4}");
        float swim = top.LightQuota, deep = bottom.LightQuota;
        // A big body gets its share in every cell of its footprint (added after the pass).
        int bc = 60 * f.W + 180;
        var giant = Body(bc, 400);
        f.SpreadBody(giant, bc);
        Require(giant.Cells > 1, $"the test body did not spread ({giant.Cells} cells)");
        var under = Body(giant.Foot[1], 10);
        foreach (var a in f.Agents) a.LightQuota = 0;
        Array.Fill(f.Sun, 0f);
        f.Sun[bc] = f.Sun[giant.Foot[1]] = 1;
        f.DistributeCanopy();
        float per = P.PhotonK * P.LightEvery;
        Require(MathF.Abs(giant.LightQuota - 2 * per * (1 - T(giant))) < 1e-5f && MathF.Abs(under.LightQuota - per * T(giant) * (1 - T(under))) < 1e-6f,
            $"big body: {giant.LightQuota:F4} over two lit cells (expected {2 * per * (1 - T(giant)):F4}), the one under it {under.LightQuota:F5}");
        // `photo` spends the body's own store and nothing of the cell; with ShadeK 0 the shared pool again.
        foreach (var a in new[] { small, big, mid }) { while (a.Inv[ch.PhotoUp[ground]] > 0) { f.RemoveMol(a, ch.PhotoUp[ground]); f.AddMol(a, ground); } }
        small.LightQuota = 2.5f; f.Photon[c] = 3;
        int n0 = small.NPhoto;
        for (int k = 0; k < 20; k++) f.Photo(small, c, 0, ground);
        Require(small.NPhoto - n0 == 2 && small.LightQuota < 1 && f.Photon[c] == 3, $"photo on the store: caught {small.NPhoto - n0} of 2.5 stored, store {small.LightQuota:F2}, cell pool {f.Photon[c]}");
        small.LightQuota = 0; n0 = small.NPhoto;
        f.Photo(small, c, 0, ground);
        Require(small.NPhoto == n0, "photo caught light with an empty store");
        P.ShadeK = 0;
        Require(!CanopyLaw, "the canopy runs with ShadeK 0");
        f.Photo(small, c, 0, ground);
        Require(small.NPhoto > n0 && f.Photon[c] < 3, "with ShadeK 0 the shared pool is not used");
        ParamRegistry.ResetDefaults();

        // A canopy world: atoms and energy close; saved and loaded with its stores, it goes on the same way.
        P.Canopy = 1;
        var wld = new World(3, 800, false) { AutoStrikes = false, TrackHeat = true };
        var atoms = wld.ElementBudget(); var e0 = wld.AuditEnergy();
        for (int t = 0; t < 600; t++) wld.Step();
        int stored = wld.Agents.Count(a => a.LightQuota > 0);
        double photo = wld.EnergyFlows()[FPhoto];
        var after = wld.ElementBudget();
        for (int e = 0; e < after.Length; e++) after[e] -= wld.InteriorInput[e] + wld.HandInput[e];
        BudgetEqual(atoms, after, "canopy world", 0.001);
        string energy = EnergyWorldCheck(wld, e0, "canopy world");
        Require(stored > 0 && photo > 0, $"canopy world: {stored} bodies with a store, photo flow {photo:F1}");
        var ms = new System.IO.MemoryStream();
        wld.Save(ms, "canopy");
        ms.Position = 0;
        var back = Load(ms);
        Require(back.DeepHash() == wld.DeepHash(), "a canopy world did not load as the same world");
        for (int t = 0; t < 200; t++) { wld.Step(); back.Step(); }
        Require(back.DeepHash() == wld.DeepHash(), "a loaded canopy world diverged");
        ParamRegistry.ResetDefaults();
        return $"canopy: lone small body stops {alone:P1}, under two {expectSmall:P2}, on top {onTop:P1}; three bodies + ground = inflow (|Δ| < 1e-6), stores capped at {P.PhotonCap}; swimmer {swim:F4} / bottom {deep:F4} / water {wl:F3} of 1; big body over {giant.Cells} cells {giant.LightQuota:F3}; world 600 ticks: {stored} stores, photo {photo:F0}, {energy}, save/load same";
    }
}
