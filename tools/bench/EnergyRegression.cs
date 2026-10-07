using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Primordium;

// The energy ledger (World.Energy): targeted probes of every path that moves chemical energy, and the
// long regression (`--long-test`).
public sealed partial class World
{
    // Stock change equals inputs − outputs, and the heat the flows name is the heat that reached the cells.
    void EnergyBalanced(EnergyAudit before, string what, params int[] flows)
    {
        // Bodies born outside a tick wait in `newborn` (Step merges them); count them like Step would.
        Agents.AddRange(newborn); newborn.Clear();
        var after = AuditEnergy();
        double drift = EnergyAudit.Drift(before, after), heat = EnergyAudit.HeatMismatch(before, after);
        double tol = 0.01 + 1e-4 * EnergyAudit.Gross(before, after);   // float energies round at ~1e-7 of their size per step
        Require(Math.Abs(drift) <= tol, $"energy {what}: {drift:R} appeared beyond the flows ({after.Describe(before)})");
        Require(Math.Abs(heat) <= tol, $"energy {what}: heat in the cells differs from the heat flows by {heat:R}");
        foreach (int f in flows) Require(after.Flows[f] != before.Flows[f], $"energy {what}: no {FlowNames[f]} flow");
    }

    EnergyAudit EnergyStart() { TrackHeat = true; return AuditEnergy(); }

    static (int a, int b, int p, int de) FindBind(Chemistry ch, Func<int, bool> want)
    {
        for (int a = 0; a < Chemistry.S; a++)
            for (int b = a + 1; b < Chemistry.S; b++)
            {
                int p = ch.Combine[a, b];
                if (p < 0) continue;
                int de = ch.E[a] + ch.E[b] - ch.E[p];
                if (want(de)) return (a, b, p, de);
            }
        throw new Exception("no such reaction in this chemistry");
    }

    static void EnergyRegression()
    {
        var w = Fixture(); var ch = w.Chem;
        int c = 80 * W + 120;
        var e0 = w.EnergyStart();
        Require(Math.Abs(e0.Stock) < 1e-9, "empty fixture holds energy");

        // Reactions in a body: bind (exo and endo), split, digest, photo, motor.
        var (x, y, _, deX) = FindBind(ch, de => de > 0);
        var a = w.TestAgent(c, 2, x, 30);
        for (int k = 0; k < 30; k++) w.AddMol(a, y);
        a.Enz[0] = new Enzyme { Kind = Enzyme.Bind, A = (byte)x, B = (byte)y, Amount = 4, Eff = 1, Topt = 15 }; a.EnzN = 1;
        var before = w.AuditEnergy();
        w.Bind(a, 0, x, y);
        Require(a.NBind >= 4 && a.HeatHeld > 0, "probe bind did not go");
        w.EnergyBalanced(before, "exothermic bind");
        var (u, v, _, deU) = FindBind(ch, de => de < 0);
        var b = w.TestAgent(c, 2, u, 20);
        for (int k = 0; k < 20; k++) w.AddMol(b, v);
        b.Enz[0] = new Enzyme { Kind = Enzyme.Bind, A = (byte)u, B = (byte)v, Amount = 3, Eff = 1, Topt = 15 }; b.EnzN = 1;
        double eb = b.Energy;
        before = w.AuditEnergy();
        w.Bind(b, 0, u, v);
        Require(b.NBind > 0 && b.Energy < eb, "probe endothermic bind did not go");
        w.EnergyBalanced(before, "endothermic bind");
        int sx = ch.Unstable[0];
        b.Enz[1] = new Enzyme { Kind = Enzyme.Split, A = (byte)sx, Amount = 3, Eff = 1, Topt = 15 }; b.EnzN = 2;
        for (int k = 0; k < 10; k++) w.AddMol(b, sx);
        before = w.AuditEnergy();
        w.SplitMol(b, 0, sx);
        for (int k = 0; k < 20; k++) if (b.InvTotal > 0) w.SplitMol(b, 0, w.RandomMol(b));   // `digest`: whatever comes
        Require(b.NSplit > 0, "probe split did not go");
        w.EnergyBalanced(before, "split and digest");
        int ground = Enumerable.Range(0, Chemistry.S).First(s => ch.PhotoUp[s] >= 0);
        var plant = w.TestAgent(c + 2, 2, ground, 20);
        plant.Enz[0] = new Enzyme { Kind = Enzyme.Photo, A = (byte)ground, Amount = 3, Eff = 1, Topt = 15 }; plant.EnzN = 1;
        w.Photon[c + 2] = 3;
        before = w.AuditEnergy();
        w.Photo(plant, c + 2, 0, ground);
        Require(plant.NPhoto > 0, "probe photo caught nothing");
        w.EnergyBalanced(before, "photo", FPhoto);
        plant.Enz[1] = new Enzyme { Kind = Enzyme.Motor, Amount = 2, Eff = 1, Topt = 15 }; plant.EnzN = 2;
        before = w.AuditEnergy();
        w.Motor(plant, 0, 1);
        w.EnergyBalanced(before, "motor", FDissipate);

        // Eating: uptake (whole and partial), drinking, soaking an aggregate, gnawing rock.
        int food = ch.Low.First(s => ch.E[s] > 0);
        w.C[food][c] = 3.4f;
        var eater = w.TestAgent(c, 2, food, 10);
        before = w.AuditEnergy();
        for (int k = 0; k < 4; k++) w.Intake(eater, c, food);
        w.Drink(eater, c);
        w.EnergyBalanced(before, "uptake", FDissipate);
        int m = 120 * W + 30;
        int soft = Enumerable.Range(0, Chemistry.S).Where(s => ch.E[s] > 0).OrderBy(s => ch.MatBarrier[s + 2]).First();
        w.TestBlock(m, 2, soft, 0); w.TestBlock(m, 3, soft, 0);
        var miner = w.TestAgent(m, 4, food, 10);
        w.Tick = 100000;
        before = w.AuditEnergy();
        for (int k = 0; k < 4000 && miner.NMines < 3; k++) { w.Mine(miner, m, 0, 4); w.SoakAggregate(miner, m); }
        Require(miner.NMines >= 3, "probe miner got nothing out of the rock");
        w.EnergyBalanced(before, "mining", FDissipate);

        // Spending beyond what a body has: only what it had becomes heat; the rest is unpaid, and
        // written off if it dies in debt.
        a.Energy = 1;
        before = w.AuditEnergy();
        w.Dissipate(a, 3);
        Require(a.Energy < 0, "probe overdraft");
        w.EnergyBalanced(before, "overdraft", FUnpaid);
        before = w.AuditEnergy();
        w.Die(a, c, CauseStarve);
        w.EnergyBalanced(before, "death in debt", FWriteOff, FDeath);

        // Death with energy, held heat and protein; division and mating; decay in a body.
        b.HeatHeld = 5;
        before = w.AuditEnergy();
        w.Die(b, c, CauseStarve);
        w.EnergyBalanced(before, "death", FDeath);
        var parent = w.TestAgent(c + W, 2, food, 40);
        before = w.AuditEnergy();
        w.Divide(parent, c + W, 0, 4);
        Require(parent.NChildren == 1, "probe division failed");
        w.EnergyBalanced(before, "division", FDissipate);
        var mate = w.TestAgent(c + W, 2, food, 40);
        mate.MateTick = w.Tick; parent.Target = mate;
        before = w.AuditEnergy();
        w.Mate(parent, c + W);
        Require(parent.NMates == 1, "probe mating failed");
        w.EnergyBalanced(before, "mating", FDissipate);
        var hot = w.TestAgent(c + 2 * W, 2, sx, 200);
        hot.HeatHeld = 10; hot.Tb = 40;
        before = w.AuditEnergy();
        for (int k = 0; k < 400; k++) w.Live(hot);
        Require(!hot.Dead, "probe body died living");
        w.EnergyBalanced(before, "living (upkeep, heat shed, decay)", FShed, FBodyDecay, FDissipate);

        // Burial: entombed where it stands, and under a collapsing slab (impact heat is gravity's).
        int t = 30 * W + 200;
        var tomb = w.TestAgent(t, 2, food, 12);
        tomb.HeatHeld = 2;
        w.TestBlock(t, 2, food);
        before = w.AuditEnergy();
        w.SettleAgent(tomb);
        Require(tomb.Dead && tomb.Cause == CauseBuried, "probe body was not entombed");
        w.EnergyBalanced(before, "entombed", FDeath);
        int f = 40 * W + 200;
        var victim = w.TestAgent(f, 2, food, 12);
        w.TestBlock(f, 6, food);
        before = w.AuditEnergy();
        w.StepStructure();
        Require(victim.Dead && w.Mat[f * Z + 2] != 0, "probe slab did not fall on the victim");
        w.EnergyBalanced(before, "collapse", FImpact, FDeath);

        // Pressure chemistry in a burial under a tall stack.
        var (px, py, pp, _) = FindBind(ch, de => de > 0);
        int q = 140 * W + 100;
        for (int z = 2; z < 60; z++) w.TestBlock(q, z, ch.Solids.OrderByDescending(s => ch.Mass[s]).First());
        var burial = w.BurialAt(q * Z + 2);
        burial.Matter[px] = 20; burial.Matter[py] = 20;
        w.MatterChanged(q * Z + 2); w.StepStructure();
        long meta = w.Metamorphoses;
        before = w.AuditEnergy();
        for (int k = 0; k < 8; k++) w.Metamorphose();
        string pressureNote = w.Metamorphoses > meta ? $"{w.Metamorphoses - meta} pressure reactions" : "no pressure reaction at this load";
        w.EnergyBalanced(before, "pressure chemistry");

        // Ground and outside: loose decay, settling, weathering, strikes, vents, the hand, abiogenesis.
        int g = 100 * W + 100;
        for (int s = 0; s < Chemistry.S; s++) w.C[s][g] += 30.5f;
        w.TestBlock(g, 2, food, 0);
        before = w.AuditEnergy();
        w.Tick = 4000;
        w.EnvChem();
        w.Settle(g, 1e4f);
        w.StrikeAt(g % W, g / W, 4);
        w.EnergyBalanced(before, "ground chemistry and strike", FLooseDecay, FStrike);
        before = w.AuditEnergy();
        var vent = new Vent { X = 10, Y = 100, Strength = 1, Life = 100 };
        for (int k = 0; k < 40; k++) w.BuildCone(vent);
        w.Pour(60, 60, 3, food, 0.2f);
        w.DigOut(60, 60, 3, 1f);
        w.EnergyBalanced(before, "vent and hand", FVent, FHand);
        int abio = 20 * W + 20;
        for (int s = 0; s < Chemistry.S; s++) w.C[s][abio] += 12;
        w.TestBlock(abio, 2, food, 0);
        before = w.AuditEnergy();
        Require(w.SpawnAt(abio, true), "probe abiogenesis failed");
        w.EnergyBalanced(before, "abiogenesis", FAbio);
        Console.WriteLine($"PASS energy probes: bind (+{deX}/{deU}), split, digest, photo, motor, uptake, mining, overdraft, death (also in debt), division, mating, living, entombed, collapse, {pressureNote}, ground decay, strike, vent, hand, abiogenesis");
    }

    // A real world: the ledger must close over a run (part of the self-test's world runs).
    static string EnergyWorldCheck(World w, EnergyAudit start, string stage)
    {
        var now = w.AuditEnergy();
        double drift = EnergyAudit.Drift(start, now), tol = EnergyAudit.Tolerance(start, now), heat = EnergyAudit.HeatMismatch(start, now);
        Require(Math.Abs(drift) <= tol, $"{stage}: energy drift {drift:F3} beyond {tol:F3}: {now.Describe(start)}");
        Require(Math.Abs(heat) <= tol, $"{stage}: heat in the cells off the flows by {heat:F3}: {now.Describe(start)}");
        return $"energy drift {drift:F3} of {EnergyAudit.Gross(start, now):F0} moved";
    }

    // Pressure reactions need matter that can still bind exothermically buried deep (~10–30 blocks of
    // rock on it). In 20 000 ticks of a natural world that hardly happens: bodies die in shallow caves,
    // crushed rock is of one kind, and living bodies have mostly used up exactly those reactions. So
    // the long test entombs a few bodies that still hold such a pair 30 blocks deep, as a roof would,
    // and lets the world go on: the pressure path then runs inside a real world, under the balances.
    // Deterministic (the twin gets the same).
    // Holds a pair of molecules with an exothermic bind (what Metamorphose looks for). Most bodies
    // don't: life has already used exactly those reactions.
    bool CanReactUnderPressure(Agent a)
    {
        for (int x = 0; x < Chemistry.S; x++)
            for (int y = x; y < Chemistry.S; y++)
            {
                int p = Chem.Combine[x, y];
                if (p >= 0 && a.Inv[x] >= 1 && a.Inv[y] >= (x == y ? 2 : 1) && Chem.E[x] + Chem.E[y] >= Chem.E[p]) return true;
            }
        return false;
    }

    int EntombDeep(int count, int depth)
    {
        int done = 0;
        foreach (var a in Agents.ToList())
        {
            if (done == count) break;
            if (a.Dead || a.Cells > 1 || !CanReactUnderPressure(a)) continue;
            int c = a.Y * W + a.X, z = Height[c] - depth;
            if (z < 2 || Mat[c * Z + z] < 2) continue;
            Die(a, c, CauseBuried, c * Z + z);
            done++;
        }
        RemoveDead();
        return done;
    }

    // Several seeds for ~20 000 ticks: atoms and energy balance at every checkpoint, the same seed twice
    // gives the same states, and the collapse, burial, sediment and pressure paths were exercised so
    // the balances went through them.
    public static void RunLongTest(string[] args)
    {
        string Arg(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        var seeds = Batch.ParseSeeds(Arg("--seeds", "1,2,3"));
        int ticks = int.Parse(Arg("--ticks", "20000")), every = int.Parse(Arg("--every", "2000"));
        int twinSeed = seeds[0];
        var clock = Stopwatch.StartNew();
        long falls = 0, crushed = 0, buried = 0, pressure = 0, sediments = 0, deaths = 0;
        var flows = new double[FlowCount];
        foreach (int seed in seeds)
        {
            var w = new World(seed) { TrackHeat = true };
            var twin = seed == twinSeed ? new World(seed) { TrackHeat = true } : null;
            var atoms0 = w.ElementBudget();
            var e0 = w.AuditEnergy();
            double worstAtom = 0, worstEnergy = 0;
            for (int t = 1; t <= ticks; t++)
            {
                w.Step();
                twin?.Step();
                if (t == ticks / 4) { int n = w.EntombDeep(30, 30); twin?.EntombDeep(30, 30); Console.WriteLine($"  seed {seed} t {t}: entombed {n} bodies 30 blocks deep"); }
                if (t % every != 0) continue;
                var atoms = w.ElementBudget();
                for (int e = 0; e < atoms.Length; e++)
                {
                    double d = Math.Abs(atoms[e] - w.InteriorInput[e] - w.HandInput[e] - atoms0[e]);
                    worstAtom = Math.Max(worstAtom, d);
                    Require(d <= 0.5 + 1e-9 * atoms0[e], $"seed {seed} t {t}: element {e} drifted by {d}");
                }
                var now = w.AuditEnergy();
                double drift = EnergyAudit.Drift(e0, now);
                worstEnergy = Math.Max(worstEnergy, Math.Abs(drift));
                EnergyWorldCheck(w, e0, $"seed {seed} t {t}");
                w.CheckCellLists();
                if (twin != null) Require(w.StateHash() == twin.StateHash(), $"seed {seed} t {t}: two runs of one seed diverged");
                Console.WriteLine($"  seed {seed} t {t}: pop {w.Agents.Count}, energy drift {drift:F3} (tol {EnergyAudit.Tolerance(e0, now):F1}), atoms {worstAtom:F4}, falls {w.CollapsedBlocks} crushed {w.CrushedBlocks} buried {w.DeathsBuried} pressure {w.Metamorphoses} sediments {w.Sediments} ({clock.Elapsed.TotalSeconds:F0} s)");
            }
            var end = w.AuditEnergy();
            for (int k = 0; k < FlowCount; k++) flows[k] += end.Flows[k] - e0.Flows[k];
            falls += w.CollapsedBlocks; crushed += w.CrushedBlocks; buried += w.DeathsBuried; pressure += w.Metamorphoses; sediments += w.Sediments; deaths += w.Deaths;
            Console.WriteLine($"PASS seed {seed}, {ticks} ticks: population {w.Agents.Count}, worst atom drift {worstAtom:F4}, worst energy drift {worstEnergy:F3}" + (twin != null ? ", twin run identical" : ""));
        }
        Require(falls > 0, "no block fell in any run: the collapse path was not exercised");
        Require(crushed + buried > 0, "nothing was crushed and nobody buried: the burial paths were not exercised");
        Require(pressure > 0, "no pressure reaction: metamorphism of burials was not exercised");
        Require(sediments > 0, "no sediment: settling was not exercised");
        foreach (int k in new[] { FPhoto, FDissipate, FShed, FDeath, FBodyDecay, FLooseDecay, FPressure, FImpact })
            Require(flows[k] != 0, $"no {FlowNames[k]} flow in any run");
        Console.WriteLine($"PASS long test: seeds {string.Join(",", seeds)}, {ticks} ticks each; falls {falls}, crushed {crushed}, buried bodies {buried}, pressure reactions {pressure}, sediments {sediments}, deaths {deaths}; in {clock.Elapsed.TotalMinutes:F1} min");
        Console.WriteLine("   flows: " + string.Join(", ", Enumerable.Range(0, FlowCount).Select(k => $"{FlowNames[k]} {flows[k]:F0}")));
    }
}
