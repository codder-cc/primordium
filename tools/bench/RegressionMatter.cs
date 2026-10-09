using System;
using System.Linq;

namespace Primordium;

// Body energy as matter (P.MatterEnergy 1, World.Charge): probes on a small blank world. Each probe checks
// atoms (exact, ElementBudget), the ledger (World.Energy) and what the law says should happen.
public sealed partial class World
{
    public static void MatterEnergyRegression()
    {
        // The probes switch MatterEnergy alone, from the old default world (the other laws whose defaults
        // changed with it, 2026-10-09 (11), at their old values: a motor push without the organ law, decay by
        // the old laws); the designs are then also planted in today's default world.
        var oldLaws = OldDefaults();
        int law = P.MatterEnergy;
        int chem = P.ChemEnergyModel;
        try
        {
            ShiftProbe();
            P.MatterEnergy = 1;
            P.ChemEnergyModel = 0;   // the legacy chemistry has every kind of reaction (uphill binds, fuel)
            MatterLawProbes();
            P.ChemEnergyModel = chem;
            MatterLawProbes();       // and the chemistry from bonds, the default
            MatterWorldProbe();
            P.ChemEnergyModel = 0;
            MatterDesignProbe();
            P.ChemEnergyModel = chem;
            MatterDesignProbe();
        }
        finally { P.MatterEnergy = law; P.ChemEnergyModel = chem; oldLaws.Dispose(); }
        MatterDesignProbe();   // the default world (every changed law at its new value)
    }

    // Runs `act` as if inside the body's own tick (its costs are owed until settled, World.Charge).
    void InTick(Agent a, Action act)
    {
        var ctx = ctxs[0];
        cur = ctx; ctx.Body = a;
        try { act(); }
        finally { ctx.Body = null; cur = null; }
    }

    // A body of n ground molecules g and k of its excited state, with no legacy remainder.
    Agent ChargedAgent(int c, int g, int n, int k)
    {
        var a = TestAgent(c, 2, g, n);
        a.Energy = 0;
        for (int j = 0; j < k; j++) AddMol(a, Chem.PhotoUp[g]);
        return a;
    }

    void AtomsSame(double[] before, string what)
    {
        Agents.AddRange(newborn); newborn.Clear();
        var now = ElementBudget();
        for (int e = 0; e < now.Length; e++) Require(Math.Abs(now[e] - before[e]) < 1e-6, $"{what}: element {e} {before[e]:R} -> {now[e]:R}");
    }

    void NoDebtFlows(EnergyAudit before, string what)
    {
        var now = AuditEnergy();
        foreach (int f in new[] { FUnpaid, FWriteOff })
            Require(now.Flows[f] == before.Flows[f], $"{what}: {FlowNames[f]} moved by {now.Flows[f] - before.Flows[f]:R} with the law on");
    }

    // Law 1 probes, each on the ledger and atoms: paying costs by relaxation, starving, reactions charging and
    // discharging, capture saturation, the pay order, fuel with and without its protein, sharing and links,
    // division and mating, attack, death, the legacy remainder, living ticks and abiogenesis.
    static void MatterLawProbes()
    {
        var w = Blank();
        var ch = w.Chem;
        int c = 20 * w.W + 20;
        w.EnergyStart();
        int g = Enumerable.Range(0, Chemistry.S / 2).Select(f => 2 * f).Where(s => ch.PhotoUp[s] >= 0 && ch.Gap[s + 1] > 0).OrderBy(s => ch.Gap[s + 1]).First();
        int x = g + 1, gx = ch.Gap[x];

        // Paying: a motor push is relaxation of carriers, booked as dissipate; the charge drops by exactly the heat.
        var a = w.ChargedAgent(c, g, 10, 6);
        a.Enz[0] = new Enzyme { Kind = Enzyme.Motor, Amount = 2, Eff = 1, Topt = 15 }; a.EnzN = 1;
        var before = w.AuditEnergy();
        long q0 = w.ChargeRaw(a);
        w.Motor(a, 0, 1);
        var mid = w.AuditEnergy();
        double paid = (q0 - w.ChargeRaw(a)) / Qty.One, heat = mid.Flows[FDissipate] - before.Flows[FDissipate];
        float cost = P.CostPush * (1 + a.Mass);
        Require(a.Due == 0 && paid == heat && paid >= cost && paid - cost < gx / Qty.One * 2, $"law 1 motor: charge fell {paid:R}, heat {heat:R}, cost {cost:R}");
        w.EnergyBalanced(before, "law 1 motor", FDissipate);
        w.NoDebtFlows(before, "law 1 motor");
        // Uptake: the same (the molecule taken in is not a carrier).
        int food = ch.Low.First(s => ch.Gap[s] == 0);
        w.C[food][c] = 2.5f;
        before = w.AuditEnergy();
        for (int k = 0; k < 3; k++) w.Intake(a, c, food);
        w.EnergyBalanced(before, "law 1 uptake", FDissipate);

        // Starving: a cost beyond the charge is owed; nothing is unpaid or written off; it dies of it.
        var poor = w.ChargedAgent(c + 1, g, 6, 1);
        poor.HeatHeld = 0.75;
        before = w.AuditEnergy();
        w.Dissipate(poor, gx + 2.5);
        Require(w.ChargeRaw(poor) == 0 && Math.Abs(poor.Due - 2.5) < 1e-9 && w.Starved(poor), $"law 1 starving: charge {w.Charge(poor)}, owed {poor.Due}");
        w.Die(poor, c + 1, CauseStarve);
        var after = w.AuditEnergy();
        Require(Math.Abs(after.Flows[FDeath] - before.Flows[FDeath] - 0.75) < 1e-12, "law 1 death: only the held heat is the death flow");
        w.EnergyBalanced(before, "law 1 starvation", FDissipate, FDeath);
        w.NoDebtFlows(before, "law 1 starvation");

        // An exothermic bind of two ground molecules charges the body: CaptureHeat of it is held heat, the rest
        // excitation (what finds no molecule to excite is heat too).
        var (bx, by, bp, bde) = FindGroundBind(ch, de => de > 0);
        var eater = w.ChargedAgent(c + 2, g, 12, 0);
        for (int k = 0; k < 6; k++) { w.AddMol(eater, bx); w.AddMol(eater, by); }
        eater.Enz[0] = new Enzyme { Kind = Enzyme.Bind, A = (byte)bx, B = (byte)by, Amount = 4, Eff = 1, Topt = 15 }; eater.EnzN = 1;
        before = w.AuditEnergy();
        q0 = w.ChargeRaw(eater);
        double h0 = eater.HeatHeld;
        var atoms = w.ElementBudget();
        w.Bind(eater, 0, bx, by);
        w.AtomsSame(atoms, "law 1 exothermic bind");
        int done = eater.NBind;
        double charged = (w.ChargeRaw(eater) - q0) / Qty.One, held = eater.HeatHeld - h0;
        Require(done > 0 && charged > 0 && held >= P.CaptureHeat * bde * done - 1e-9 && Math.Abs(charged + held - (double)bde * done) < 1e-6,
            $"law 1 exothermic bind: {done} binds of {bde}: charge +{charged:R}, held heat +{held:R}");
        w.EnergyBalanced(before, "law 1 exothermic bind");
        // An uphill bind: carriers pay the product's bonds, no heat beyond the grid's overshoot.
        bool uphill = TryGroundBind(ch, de => de < 0, out var up), fuel = ch.Fuel.Length > 0;   // the chemistry from bonds may have neither
        if (uphill)
        {
            var (ux, uy, _, ude) = up;
            var builder = w.ChargedAgent(c + 3, g, 4, 20);
            for (int k = 0; k < 5; k++) { w.AddMol(builder, ux); w.AddMol(builder, uy); }
            builder.Enz[0] = new Enzyme { Kind = Enzyme.Bind, A = (byte)ux, B = (byte)uy, Amount = 3, Eff = 1, Topt = 15 }; builder.EnzN = 1;
            before = w.AuditEnergy();
            q0 = w.ChargeRaw(builder);
            w.Bind(builder, 0, ux, uy);
            double spent = (q0 - w.ChargeRaw(builder)) / Qty.One;
            Require(builder.NBind > 0 && spent >= -ude * builder.NBind && spent + ude * builder.NBind < 1e-6, $"law 1 uphill bind: {builder.NBind} of {ude}, charge −{spent:R}");
            after = w.AuditEnergy();
            Require(after.Flows[FDissipate] - before.Flows[FDissipate] < 1e-6, "law 1 uphill bind: its energy went to heat");
            w.EnergyBalanced(before, "law 1 uphill bind");
        }

        // Capture (called directly, as a reaction would: no ledger here): a fully charged body takes no more;
        // the energy becomes held heat.
        var full = w.ChargedAgent(c + 4, g, 0, 8);
        before = w.AuditEnergy();
        h0 = full.HeatHeld;
        q0 = w.ChargeRaw(full);
        w.Capture(full, 7.5);
        Require(w.ChargeRaw(full) == q0 && Math.Abs(full.HeatHeld - h0 - 7.5) < 1e-12, "law 1 capture: a full body charged further");
        // ... and a body with room charges its light-capture protein's molecule first.
        int g2 = Enumerable.Range(0, Chemistry.S / 2).Select(f => 2 * f).First(s => s != g && ch.PhotoUp[s] >= 0 && ch.Gap[s + 1] > 0);
        var cap = w.ChargedAgent(c + 4, g, 10, 0);
        for (int k = 0; k < 3; k++) w.AddMol(cap, g2);
        cap.Enz[0] = new Enzyme { Kind = Enzyme.Photo, A = (byte)g2, Amount = 1, Eff = 1, Topt = 15 }; cap.EnzN = 1;
        w.Capture(cap, ch.Gap[g2 + 1] * 2.0);
        Require(cap.Inv[g2 + 1] == 2 && cap.Inv[x] == 0, $"law 1 capture order: the protein's molecule {g2} was not charged first ({cap.Inv[g2 + 1]}, {cap.Inv[x]})");

        // Pay order: a splitting protein for a carrier spends that carrier first; without one, the weaker held.
        var two = w.ChargedAgent(c + 5, g, 2, 3);
        for (int k = 0; k < 3; k++) w.AddMol(two, g2 + 1);
        int weak = ch.Bond[x] < ch.Bond[g2 + 1] || (ch.Bond[x] == ch.Bond[g2 + 1] && x < g2 + 1) ? x : g2 + 1, strong = weak == x ? g2 + 1 : x;
        w.Dissipate(two, 0.5);
        Require(two.Inv[weak] + two.Pend[weak].D < 3 && two.Inv[strong] == 3 && two.Pend[strong].Raw == 0, "law 1 pay order: without a protein the weaker held carrier is spent first");
        two.Enz[0] = new Enzyme { Kind = Enzyme.Split, A = (byte)strong, Amount = 1, Eff = 1, Topt = 15 }; two.EnzN = 1;
        double wk = two.Inv[weak] + two.Pend[weak].D;
        w.Dissipate(two, 0.5);
        Require(two.Inv[weak] + two.Pend[weak].D == wk && two.Inv[strong] + two.Pend[strong].D < 3, "law 1 pay order: the protein's carrier is not spent first");

        // Fuel: a ground compound with an exothermic split burns by itself only with a protein for it.
        if (fuel)
        {
            int f = ch.Fuel.OrderByDescending(s => ch.SplitEnergy(s)).First();
            var burner = w.TestAgent(c + 6, 2, f, 6);
            burner.Energy = 0;
            for (int k = 0; k < 6; k++) w.AddMol(burner, g);   // room for the charge
            before = w.AuditEnergy();
            w.Dissipate(burner, 1.5);
            Require(burner.Due == 1.5 && burner.Inv[f] == 6, "law 1 fuel: burnt without a protein");
            burner.Enz[0] = new Enzyme { Kind = Enzyme.Split, A = (byte)f, Amount = 1, Eff = 1, Topt = 15 }; burner.EnzN = 1;
            Require(w.Avail(burner) > 0, "law 1 fuel: a body with a protein for its fuel has nothing available");
            atoms = w.ElementBudget();
            w.Settle(burner);
            w.AtomsSame(atoms, "law 1 fuel");
            Require(burner.Due == 0 && burner.Inv[f] + burner.Pend[f].D < 6, $"law 1 fuel: not burnt with a protein (owed {burner.Due})");
            w.EnergyBalanced(before, "law 1 fuel", FDissipate);
        }

        // Share and links: charged molecules move, mass with them; the ledger only sees the social cost.
        var giver = w.ChargedAgent(c + 7, g, 4, 30);
        giver.Energy = 50;   // a legacy remainder pays the costs, so only the molecules change the charges
        var taker = w.ChargedAgent(c + 7, g, 4, 0);
        giver.Target = taker;
        before = w.AuditEnergy();
        long gq = w.ChargeRaw(giver), tq = w.ChargeRaw(taker);
        float gm = giver.Mass;
        atoms = w.ElementBudget();
        w.Share(giver, c + 7, 40);
        w.AtomsSame(atoms, "law 1 share");
        long moved = gq - w.ChargeRaw(giver);
        Require(moved > 0 && w.ChargeRaw(taker) - tq == moved && giver.Mass < gm && moved / Qty.One >= 40 * P.ShareUnit, $"law 1 share: {moved / Qty.One:R} of charge moved");
        BodyConsistent(w, giver, "law 1 share (giver)"); BodyConsistent(w, taker, "law 1 share (taker)");
        w.EnergyBalanced(before, "law 1 share", FDissipate);
        giver.Links.Add(taker); taker.Links.Add(giver);
        gq = w.ChargeRaw(giver); tq = w.ChargeRaw(taker);
        before = w.AuditEnergy();
        w.TendLinks(giver.Id < taker.Id ? giver : taker);
        Require(w.ChargeRaw(giver) < gq && w.ChargeRaw(taker) - tq == gq - w.ChargeRaw(giver), "law 1 link: the richer did not feed the poorer");
        w.EnergyBalanced(before, "law 1 link");
        giver.Links.Clear(); taker.Links.Clear();

        // Division and mating: the child gets molecules and with them charge; no double moves.
        var parent = w.ChargedAgent(c + w.W, g, 20, 20);
        before = w.AuditEnergy();
        atoms = w.ElementBudget();
        w.Divide(parent, c + w.W, 0, 4);
        w.AtomsSame(atoms, "law 1 division");
        Require(parent.NChildren == 1, "law 1 division failed");
        var child = w.Agents[^1];   // AtomsSame took it in from the newborn
        Require(w.ChargeRaw(child) > 0 && child.Energy == 0, "law 1 division: the child holds no charge (or a store)");
        after = w.AuditEnergy();
        Require(after.Flows[FRounding] == before.Flows[FRounding], "law 1 division moved a double");
        w.EnergyBalanced(before, "law 1 division", FDissipate);
        var mate = w.ChargedAgent(c + w.W, g, 20, 20);
        mate.MateTick = w.Tick; parent.Target = mate;
        before = w.AuditEnergy();
        w.Mate(parent, c + w.W);
        Require(parent.NMates == 1, "law 1 mating failed");
        w.EnergyBalanced(before, "law 1 mating", FDissipate);

        // Attack: torn molecules carry their charge to the attacker (its costs paid from a legacy remainder).
        var hunter = w.ChargedAgent(c + 2 * w.W, g, 6, 0);
        hunter.Energy = 1e4;
        var prey = w.ChargedAgent(c + 2 * w.W, g, 2, 12);
        hunter.Target = prey;
        before = w.AuditEnergy();
        long hq = w.ChargeRaw(hunter), pq = w.ChargeRaw(prey);
        atoms = w.ElementBudget();
        for (int k = 0; k < 20 && !prey.Dead && hunter.NAttacks < 20; k++) w.Attack(hunter, c + 2 * w.W, 255);
        w.AtomsSame(atoms, "law 1 attack");
        Require(w.ChargeRaw(hunter) > hq && (prey.Dead || w.ChargeRaw(hunter) - hq == pq - w.ChargeRaw(prey)),
            $"law 1 attack: the charge torn out did not reach the attacker ({(w.ChargeRaw(hunter) - hq) / Qty.One:R}, prey {(pq - w.ChargeRaw(prey)) / Qty.One:R}, dead {prey.Dead})");
        w.EnergyBalanced(before, "law 1 attack", FDissipate);
        w.NoDebtFlows(before, "law 1 attack");

        // Death: the molecules go to the ground with their charge; only held heat is the death flow.
        var dying = w.ChargedAgent(c + 3 * w.W, g, 5, 5);
        dying.HeatHeld = 2;
        before = w.AuditEnergy();
        w.Die(dying, c + 3 * w.W, CauseStarve);
        after = w.AuditEnergy();
        Require(Math.Abs(after.Flows[FDeath] - before.Flows[FDeath] - 2) < 1e-12 && after.Loose - before.Loose > 5 * ch.E[x] - 1e-6, "law 1 death: the charge did not stay in the remains");
        w.EnergyBalanced(before, "law 1 death", FDeath);

        // Inside the body's own tick (costs owed, not yet settled): an act that takes a molecule out settles first,
        // so a body whose only carrier pays its debt has nothing left to expel, give or fold, and never a
        // negative count.
        foreach (int act in new[] { 0, 1, 2 })
        {
            var lone = w.ChargedAgent(c + 6 * w.W + act, g, 0, 1);
            for (int k = 0; k < 3; k++) w.AddMol(lone, g2);
            var other = w.ChargedAgent(c + 6 * w.W + act, g, 3, 0);
            lone.Target = other;
            w.InTick(lone, () =>
            {
                w.Dissipate(lone, gx * 0.75);   // owed: three quarters of its only carrier
                if (act == 0) w.Expel(lone, c + 6 * w.W + act, x, 1);
                else if (act == 1) w.Give(lone, c + 6 * w.W + act, x);
                else { for (int k = 0; k < 3; k++) w.AddMol(lone, x); w.Dissipate(lone, 3.6 * gx); w.MakeProtein(lone, new Enzyme { Kind = Enzyme.Photo, A = (byte)g, Eff = 1, Topt = 15 }, -1); }
            });
            w.Settle(lone);
            BodyConsistent(w, lone, $"law 1 act {act} after settling inside the tick");
            Require(lone.Inv[x] >= 0 && lone.Due >= 0, $"law 1 act {act}: a molecule left that settling had relaxed");
        }

        // The legacy remainder is spent first and never refilled.
        var old = w.ChargedAgent(c + 4 * w.W, g, 4, 4);
        old.Energy = 3;
        q0 = w.ChargeRaw(old);
        w.Dissipate(old, 2);
        Require(Math.Abs(old.Energy - 1) < 1e-12 && w.ChargeRaw(old) == q0, "law 1 remainder: not spent first");
        w.Dissipate(old, 2);
        Require(old.Energy == 0 && w.ChargeRaw(old) < q0, "law 1 remainder: carriers did not take over");

        // Living: upkeep, heat shed, decay of carriers; a body that runs out of charge starves.
        var liver = w.ChargedAgent(c + 5 * w.W, g, 6, 3);
        before = w.AuditEnergy();
        for (int k = 0; k < 3000 && !liver.Dead; k++) w.Live(liver);
        Require(liver.Dead && liver.Cause == CauseStarve, $"law 1 living: a body with {3 * gx} of charge did not starve ({liver.Dead}, cause {liver.Cause})");
        w.EnergyBalanced(before, "law 1 living and starving", FDissipate);
        w.NoDebtFlows(before, "law 1 living");

        // Abiogenesis: the founder's molecules are charged by the cell's downhill reactions.
        int cell = 40 * w.W + 40;
        for (int s = 0; s < Chemistry.S; s++) w.C[s][cell] += 12;
        before = w.AuditEnergy();
        Require(w.SpawnAt(cell, true), "law 1 abiogenesis failed");
        var founder = w.Agents[^1];
        Require(founder.Energy == 0 && w.ChargeRaw(founder) > 0, $"law 1 abiogenesis: founder with store {founder.Energy} and charge {w.Charge(founder)}");
        w.EnergyBalanced(before, "law 1 abiogenesis", FAbio);
        Console.WriteLine($"PASS matter energy law (chemistry model {ch.Model}): paying by relaxation, starving without debt flows, exothermic{(uphill ? " and uphill" : "")} binds, capture saturation and order, pay order, {(fuel ? "fuel only with its protein" : "(no fuel in this chemistry)")}, share, links, division, mating, attack carrying charge, death keeping it in the remains, legacy remainder first, living to starvation, abiogenesis; atoms exact");
    }

    // A small living world under the law: the ledger closes, atoms are exact, nobody owes anything between
    // ticks but the starving, and a save (with a debt in it, format 17) loads into the same world and the
    // same continuation.
    static void MatterWorldProbe()
    {
        var a = new World(SmallSettings(4, 300, true)) { TrackHeat = true };
        var e0 = a.AuditEnergy();
        var atoms0 = a.ElementBudget();
        for (int t = 0; t < 400; t++) a.Step();
        Require(a.Agents.Count(x => !x.Dead) > 0, "law 1 world: everybody died in 400 ticks");
        string note = EnergyWorldCheck(a, e0, "law 1 world, 400 ticks");
        var atoms = a.ElementBudget();
        for (int e = 0; e < atoms.Length; e++) Require(Math.Abs(atoms[e] - a.InteriorInput[e] - a.HandInput[e] - atoms0[e]) <= 0.5, $"law 1 world: element {e} drifted");
        Require(a.Agents.All(x => x.Dead || x.Due == 0), "law 1 world: a living body owes something between ticks");
        Require(a.Agents.All(x => x.Dead || x.Energy == 0), "law 1 world: a body born under the law holds a store");
        foreach (var x in a.Agents) if (!x.Dead) BodyConsistent(a, x, $"law 1 world, body #{x.Id}");   // counts, Inv ≥ 0, 0 ≤ Pend < 1, mass and room
        var debtor = a.Agents.First(x => !x.Dead);
        debtor.Due = 0.0625;   // as a body pushed off a ledge with nothing left would owe
        var ms = new System.IO.MemoryStream();
        a.Save(ms, "law 1");
        ms.Position = 0;
        var b = Load(ms);
        Require(b.DeepHash() == a.DeepHash() && b.Agents.First(x => x.Id == debtor.Id).Due == 0.0625, "law 1 save/load: the loaded world differs (or lost the debt)");
        for (int t = 0; t < 200; t++) { a.Step(); b.Step(); }
        Require(a.DeepHash() == b.DeepHash() && a.StateHash() == b.StateHash(), "law 1 save/load: the continuation diverged");
        Console.WriteLine($"PASS matter energy world (law 1, 96×96×64, seed 4): {a.Agents.Count} bodies after 600 ticks, {note}, nobody owes between ticks; save format {SaveVersion} with a debt loads into the same continuation");
    }

    // Planting under the law: a design's energy is its starting charge (brought in: excitation, booked as
    // the design input; from the place: downhill reactions there, splits and binds, captured), a body that
    // cannot hold it is refused, a population template's store becomes charge. The examples then live.
    static void MatterDesignProbe()
    {
        var w = new World(TinySettings(5, 0, false, false, SmallSide, SmallSide, SmallLevels));
        var ch = w.Chem;
        int Find(Func<int, bool> ok)
        {
            for (int k = 0; k < w.N; k++) { int c = (int)((k * 2654435761L + 12345) % w.N); if (ok(c)) return c; }
            throw new Exception("no cell for the design probe");
        }
        bool Mild(int c) => MathF.Abs(w.Temp[c] - 15) < 8;
        int land = Find(c => Mild(c) && !w.Submerged(c) && w.Count[c] == 0);
        int lake;
        try { lake = Find(c => w.Water[c] > 2 && w.Count[c] == 0); } catch (Exception) { lake = land; }
        var import = new SpawnOptions { Matter = MatterSource.Import, Energy = EnergySource.Import, Count = 3, Radius = 2 };
        var start = w.EnergyStart();
        var planted = new System.Collections.Generic.List<(CreatureDesign d, SpawnResult r)>();
        foreach (var d in CreatureExamples.All)
        {
            int at = d.Name == CreatureExamples.Swimmer.Name ? lake : land;
            var r = w.SpawnDesign(d, at % w.W, at / w.W, import);
            Require(r.Made == 3, $"law 1 design {d.Name}: {r}");
            foreach (var a in r.Agents)
                Require(a.Energy == 0 && w.Charge(a) > d.Energy - 1e-6, $"law 1 design {d.Name}: charge {w.Charge(a):R} for {d.Energy}");   // more if its own molecules are excited
            planted.Add((d, r));
        }
        w.EnergyBalanced(start, "law 1 designs brought in", FDesign);
        // Too much charge for the body: refused, nothing taken.
        var greedy = new CreatureDesign { Name = "жадный", Genome = "label 0\nyield\njmp 0\nnop\nnop\nnop\nnop\nnop", Body = new() { ["0"] = 4 }, Energy = 1000 };
        ulong hash = w.StateHash();
        var no = w.SpawnDesign(greedy, land % w.W, land / w.W, import);
        Require(no.Made == 0 && no.Error != null && w.StateHash() == hash, $"law 1 design: a body too small for its charge was planted ({no})");
        // From the place: the cell's downhill reactions charge it.
        int spot = Find(c => Mild(c) && !w.Submerged(c) && w.Count[c] == 0 && c != land);
        for (int s = 0; s < Chemistry.S; s++) w.C[s][spot] += 4;
        var local = new CreatureDesign { Name = "местный", Genome = "label 0\npush 0\nphoto\nyield\njmp 0\nnop\nnop\nnop", Body = new() { ["0"] = 10 }, Energy = 6 };
        var before = w.AuditEnergy();
        var ok = w.SpawnDesign(local, spot % w.W, spot / w.W, new SpawnOptions { Radius = 0, Matter = MatterSource.Import });
        Require(ok.Made == 1 && Math.Abs(w.Charge(ok.Agents[0]) - 6) < 1e-3 && ok.EnergyLocal > 6, $"law 1 local design: {ok}, charge {(ok.Made > 0 ? w.Charge(ok.Agents[0]) : 0):0.###}");
        w.EnergyBalanced(before, "law 1 design charged by local reactions");
        // A population copied and pasted: molecules (with their charge) and fractions exact.
        var donors = planted[0].r.Agents.ToList();
        var t = w.CopyPopulation(donors, "листья", "probe");
        before = w.AuditEnergy();
        var pasted = w.PastePopulation(t, land % w.W + 6, land / w.W, new PasteOptions { Matter = MatterSource.Import, Energy = EnergySource.Import });
        Require(pasted.Made == donors.Count && pasted.Agents.Select(w.ChargeRaw).SequenceEqual(donors.Select(w.ChargeRaw)), $"law 1 population: {pasted.Made} of {donors.Count}, charges differ");
        w.EnergyBalanced(before, "law 1 population pasted", FDesign);
        // The examples live.
        var living = w.AuditEnergy();
        for (int k = 0; k < 600; k++) w.Step();
        string note = EnergyWorldCheck(w, living, "law 1 designs living");
        var lives = planted.Select(p => $"{CreatureExamples.NameEn(p.d.Name)} {p.r.Agents.Count(a => !a.Dead)}/3 (lineage {w.Agents.Count(a => !a.Dead && a.Lineage == p.r.Lineage)}; deaths by cause {string.Join("", p.r.Agents.Where(a => a.Dead).Select(a => a.Cause))}, ages {string.Join("/", p.r.Agents.Select(a => a.Age))})").ToList();
        Console.WriteLine($"PASS matter energy designs (chemistry model {ch.Model}): charge brought in and from local reactions, a body too small refused, population pasted with its charge; after 600 ticks {string.Join(", ", lives)}; {note}");
    }

    static (int a, int b, int p, int de) FindGroundBind(Chemistry ch, Func<int, bool> want) =>
        TryGroundBind(ch, want, out var r) ? r : throw new Exception("no such bind of ground states");

    static bool TryGroundBind(Chemistry ch, Func<int, bool> want, out (int a, int b, int p, int de) found)
    {
        for (int a = 0; a < Chemistry.S; a += 2)
            for (int b = a; b < Chemistry.S; b += 2)
            {
                int p = ch.Combine[a, b];
                if (p < 0) continue;
                int de = ch.E[a] + ch.E[b] - ch.E[p];
                if (want(de)) { found = (a, b, p, de); return true; }
            }
        found = default;
        return false;
    }

    // The mass, room and counts of a body recomputed from what it holds (Inv, Pend, protein substrate).
    static void BodyConsistent(World w, Agent a, string what)
    {
        float mass = 0, room = 0;
        int total = 0, unstable = 0, solids = 0;
        for (int s = 0; s < Chemistry.S; s++)
        {
            Require(a.Pend[s].Raw >= 0 && a.Pend[s].Raw < 1L << Qty.Bits, $"{what}: Pend[{s}] = {a.Pend[s]} outside [0, 1)");
            Require(a.Inv[s] >= 0, $"{what}: Inv[{s}] = {a.Inv[s]}");
            double n = a.Inv[s] + a.Pend[s].D;
            mass += (float)(n * w.Chem.Mass[s]); room += (float)(n * w.Chem.BodyVolume[s]);
            total += a.Inv[s];
            if (w.Chem.SplitExo[s]) unstable += a.Inv[s];
            if (w.Chem.Solid[s]) solids += a.Inv[s];
        }
        for (int k = 0; k < a.EnzN; k++) { mass += a.Enz[k].Matter.F * w.Chem.Mass[a.Enz[k].Material]; room += a.Enz[k].Matter.F * w.Chem.Volume[a.Enz[k].Material]; }
        Require(total == a.InvTotal && unstable == a.Unstable && solids == a.Solids, $"{what}: counts {a.InvTotal}/{a.Unstable}/{a.Solids}, recounted {total}/{unstable}/{solids}");
        Require(MathF.Abs(mass - a.Mass) <= 1e-3f * (1 + mass) && MathF.Abs(room - a.Volume) <= 1e-3f * (1 + room), $"{what}: mass {a.Mass} / room {a.Volume}, recomputed {mass} / {room}");
    }

    // Shift: a fraction of one kind becomes another inside the body, whole molecules broken into the fraction
    // as needed; atoms, the ledger (an excitation moved by hand here is booked as energy-k-free: the stock
    // changes by exactly the energy difference), mass and room exact.
    static void ShiftProbe()
    {
        var w = Blank();
        var ch = w.Chem;
        int c = 20 * w.W + 20;
        int exc = ch.Excited.First(s => ch.Gap[s] > 0 && ch.SplitA[s] == Chemistry.Ground(s));   // an excited monomer
        int ground = Chemistry.Ground(exc);
        int split = ch.Fuel.Length > 0 ? ch.Fuel[0] : ch.Unstable.First(s => ch.SplitA[s] >= 0 && ch.SplitB[s] >= 0);
        var a = w.TestAgent(c, 2, exc, 3);
        for (int k = 0; k < 4; k++) w.AddMol(a, split);
        var atoms0 = w.ElementBudget();
        var e0 = w.AuditEnergy();
        w.Shift(a, exc, ground, -1, Qty.Of(0.3));    // relax 0.3 of a molecule: a whole one is broken into the fraction
        Require(a.Inv[exc] == 2 && Math.Abs(a.Pend[exc].D - 0.7) < 1e-9 && Math.Abs(a.Pend[ground].D - 0.3) < 1e-9, $"shift: relaxation 0.3 left {a.Inv[exc]} + {a.Pend[exc]} excited, {a.Pend[ground]} ground");
        BodyConsistent(w, a, "shift relax");
        w.Shift(a, exc, ground, -1, Qty.Of(0.9));    // more than the fraction: the next whole one
        Require(a.Inv[exc] == 1 && Math.Abs(a.Pend[exc].D - 0.8) < 1e-9 && a.Inv[ground] == 1 && Math.Abs(a.Pend[ground].D - 0.2) < 1e-9, "shift: borrowing a whole molecule and completing one");
        BodyConsistent(w, a, "shift borrow");
        int sa = ch.SplitA[split], sb = ch.SplitB[split];
        w.Shift(a, split, sa, sb, Qty.Of(0.7));     // a split of 0.7 molecule
        BodyConsistent(w, a, "shift split");
        Require(a.Inv[split] == 3 && Math.Abs(a.Pend[split].D - 0.3) < 1e-9, "shift: split of 0.7");
        var atoms1 = w.ElementBudget();
        for (int e = 0; e < atoms0.Length; e++) Require(atoms0[e] == atoms1[e], $"shift: element {e} {atoms0[e]:R} -> {atoms1[e]:R}");
        var e1 = w.AuditEnergy();
        double expect = -1.2 * ch.Gap[exc] - 0.7 * ch.SplitEnergy(split);
        Require(Math.Abs((e1.Stock - e0.Stock) - expect) < 1e-6, $"shift: stock changed by {e1.Stock - e0.Stock:R}, the reactions by {expect:R}");
        Console.WriteLine($"PASS matter energy: shift (relax 0.3 and 0.9 of species {exc}, split 0.7 of {split}): atoms exact, Pend in [0, 1), mass and room follow");
    }
}
