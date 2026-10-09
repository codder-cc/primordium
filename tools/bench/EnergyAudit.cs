using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Primordium;

// --energy-audit [--seeds 1-6 | --load path.sav] [--ticks 6000] [--every 2000] [--series 500] [--pop N]
//   [--noabio] [--noabio-at T] [--no-probe]   (laws as usual, e.g. --set TornStore=0)
// The energy economy, observed (docs/ENERGY-AUDIT.md). World.EnergyProbe and World.PredProbe watch only:
// the state hash printed at every window is the same as with --no-probe (which prints just the hashes and
// the population series). Per seed:
//   chem — molecule energies against composition: Pearson/Spearman of E with bond, mass, atom count and
//     affinity per atom over the compound species; how many splits release energy; R, the energy downhill
//     splits release (EnergyEconomyProbe.Downhill), against E;
//   ground (at the start and at every window) — bond energy E and downhill energy R lying loose on the
//     surface, in burials (top blocks, cave floors, deep), in the top block of every column and in the whole
//     crust; the share of rock mass that is "fuel" (R > 0); per cell percentiles; what gnawing a molecule out
//     of the top block costs against what it holds; the block under it and what digging the top one away costs;
//   bodies — store, held heat, matter E and R; per molecule of a body: E, R, the store a torn molecule carries
//     (P.TornStore), the work to tear it out; the loose matter in the same cell per molecule, and its uptake cost;
//   window — ledger flows per day; transfers between ground and bodies per day; costs and yields per action
//     (intake, soak, mine, dig, attack, take, photo, reactions); energy change per body-tick by diet;
//     deaths (store to heat, matter to the remains); births against abiogenesis.
//   --noabio-at T switches abiogenesis off at tick T (H5: does it floor the population?).
public sealed partial class World
{
    static readonly CultureInfo EaInv = CultureInfo.InvariantCulture;
    static string F(double v, int d = 2) => double.IsNaN(v) || double.IsInfinity(v) ? "-" : v.ToString("F" + d, EaInv);
    static string G(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "-" : Math.Abs(v) >= 1e5 ? v.ToString("0.00e0", EaInv) : v.ToString("F1", EaInv);

    sealed class GroundSnap
    {
        public double LooseMol, LooseE, LooseR, GasE, BurTopE, BurTopR, BurCaveE, BurCaveR, BurDeepE, BurDeepR;
        public double TopMol, TopE, TopR, TopMass, TopFuelMass, CrustMol, CrustE, CrustR, CrustMass, CrustFuelMass;
        public double[] LooseRCell, TopRCell, MineCost, TopRPerMol, TopEPerMol;
        public double PayR, PayE, CellsTop, AccTopR;
        public double BelowRPerMol, BelowEPerMol, DigCostMed, DigPerMolBelowMed, BelowMineMed, BelowPayR;
        public readonly double[] LooseSp = new double[Chemistry.S], TopSp = new double[Chemistry.S], CrustSp = new double[Chemistry.S];
    }

    static double Pct(double[] v, double q)
    {
        if (v.Length == 0) return double.NaN;
        var s = (double[])v.Clone();
        Array.Sort(s);
        return s[Math.Clamp((int)(q * (s.Length - 1)), 0, s.Length - 1)];
    }

    GroundSnap Ground(double[] r)
    {
        var g = new GroundSnap();
        var E = Chem.E;
        var looseCell = new double[N];
        for (int s = 0; s < Chemistry.S; s++)
        {
            var c = C[s];
            for (int i = 0; i < N; i++)
            {
                double m = c[i].D;
                if (m <= 0) continue;
                if (s == Chem.Gas) { g.GasE += m * E[s]; continue; }
                g.LooseMol += m; g.LooseE += m * E[s]; g.LooseR += m * r[s]; g.LooseSp[s] += m;
                looseCell[i] += m * r[s];
            }
        }
        foreach (var kv in Buried)
        {
            int c = kv.Key / Z, z = kv.Key % Z;
            double e = 0, rr = 0;
            for (int s = 0; s < Chemistry.S; s++) { double m = kv.Value.Matter[s].D; e += m * E[s]; rr += m * r[s]; }
            if (z == Height[c] - 1) { g.BurTopE += e; g.BurTopR += rr; }
            else if (z >= Height[c] - 8 && Mat[kv.Key] < 2) { g.BurCaveE += e; g.BurCaveR += rr; }
            else { g.BurDeepE += e; g.BurDeepR += rr; }
        }
        Span<int> counts = stackalloc int[Chemistry.S];
        var topCell = new double[N];
        var mineCost = new List<double>(); var topRm = new List<double>(); var topEm = new List<double>();
        var digCost = new List<double>(); var digPerMol = new List<double>(); var belowMine = new List<double>();
        double belowR = 0, belowE = 0, belowN = 0, belowPay = 0, belowCells = 0;
        for (int c = 0; c < N; c++)
        {
            int h = Height[c];
            for (int z = 2; z < h; z++)
            {
                int v = c * Z + z;
                if (!BlockCounts(v, counts)) continue;
                double n = 0, e = 0, rr = 0, mass = 0, fuel = 0;
                for (int s = 0; s < Chemistry.S; s++)
                {
                    if (counts[s] == 0) continue;
                    n += counts[s]; e += (double)counts[s] * E[s]; rr += counts[s] * r[s];
                    g.CrustSp[s] += counts[s];
                    if (z == h - 1) g.TopSp[s] += counts[s];
                    mass += counts[s] * Chem.Mass[s];
                    if (r[s] > 0) fuel += counts[s] * Chem.Mass[s];
                }
                g.CrustMol += n; g.CrustE += e; g.CrustR += rr; g.CrustMass += mass; g.CrustFuelMass += fuel;
                if (z == h - 1)
                {
                    g.TopMol += n; g.TopE += e; g.TopR += rr; g.TopMass += mass; g.TopFuelMass += fuel;
                    topCell[c] = rr;
                    double cost = MineCostPerMolecule(v);
                    mineCost.Add(cost); topRm.Add(rr / n); topEm.Add(e / n);
                    g.CellsTop++;
                    if (rr / n > cost) { g.PayR++; g.AccTopR += rr; }
                    if (e / n > cost) g.PayE++;
                    // The block under it: what digging the top one away opens.
                    int u = v - 1;
                    if (z - 1 >= 2 && BlockCounts(u, counts))
                    {
                        double un = 0, ue = 0, ur = 0;
                        for (int s = 0; s < Chemistry.S; s++) { un += counts[s]; ue += (double)counts[s] * E[s]; ur += counts[s] * r[s]; }
                        belowN += un; belowE += ue; belowR += ur; belowCells++;
                        double bc = MineCostPerMolecule(u), dc = DigCost(v);
                        digCost.Add(dc); digPerMol.Add(dc / un); belowMine.Add(bc);
                        if (ur / un > bc + dc / un) belowPay++;
                    }
                }
            }
        }
        g.LooseRCell = looseCell; g.TopRCell = topCell;
        g.MineCost = mineCost.ToArray(); g.TopRPerMol = topRm.ToArray(); g.TopEPerMol = topEm.ToArray();
        g.BelowRPerMol = belowR / Math.Max(1, belowN); g.BelowEPerMol = belowE / Math.Max(1, belowN);
        g.DigCostMed = Pct(digCost.ToArray(), 0.5); g.DigPerMolBelowMed = Pct(digPerMol.ToArray(), 0.5); g.BelowMineMed = Pct(belowMine.ToArray(), 0.5);
        g.BelowPayR = belowPay / Math.Max(1, belowCells);
        return g;
    }

    // What a pool would release if all of its matter could meet: every downhill reaction (Chemistry.Downhill,
    // splits and binds), the most energetic first, each run until a partner is used up. An upper bound on
    // the pool's chemical energy that also counts binds (R counts splits only).
    static double AllDownhill(Chemistry ch, double[] amounts)
    {
        var a = (double[])amounts.Clone();
        double energy = 0;
        for (int step = 0; step < 4096; step++)
        {
            int k = 0;
            for (; k < ch.Downhill.Length; k++)
            {
                var q = ch.Downhill[k];
                if (q.B < 0 ? a[q.A] > 1e-9 : q.A == q.B ? a[q.A] > 2e-9 : a[q.A] > 1e-9 && a[q.B] > 1e-9) break;
            }
            if (k == ch.Downhill.Length) break;
            var r = ch.Downhill[k];
            double take = r.B < 0 ? a[r.A] : r.A == r.B ? a[r.A] / 2 : Math.Min(a[r.A], a[r.B]);
            a[r.A] -= take;
            if (r.B < 0) { a[ch.SplitA[r.A]] += take; if (ch.SplitB[r.A] >= 0) a[ch.SplitB[r.A]] += take; }
            else { a[r.B] -= take; a[r.P] += take; }
            energy += take * r.Energy;
        }
        return energy;
    }

    IEnumerable<string> GroundLines(GroundSnap g)
    {
        double ml = AllDownhill(Chem, g.LooseSp), mt = AllDownhill(Chem, g.TopSp), mc = AllDownhill(Chem, g.CrustSp);
        yield return Loc.T($"all downhill if mixed (splits + binds): loose {G(ml)} ({F(ml / g.LooseMol)}/mol), top blocks {G(mt)} ({F(mt / g.TopMol)}/mol), crust {G(mc)} ({F(mc / g.CrustMol)}/mol)",
                           $"всё под гору при смешении (распады + связывания): россыпь {G(ml)} ({F(ml / g.LooseMol)}/мол), верхние блоки {G(mt)} ({F(mt / g.TopMol)}/мол), кора {G(mc)} ({F(mc / g.CrustMol)}/мол)");
        yield return Loc.T(
            $"ground: loose (surface, no gas) {G(g.LooseMol)} mol, E {G(g.LooseE)}, R {G(g.LooseR)} (R/E {F(g.LooseR / g.LooseE)}); gas E {G(g.GasE)}; burials top E {G(g.BurTopE)} R {G(g.BurTopR)}, cave floors E {G(g.BurCaveE)} R {G(g.BurCaveR)}, deep E {G(g.BurDeepE)} R {G(g.BurDeepR)}",
            $"грунт: россыпь (поверхность, без газа) {G(g.LooseMol)} мол, E {G(g.LooseE)}, R {G(g.LooseR)} (R/E {F(g.LooseR / g.LooseE)}); газ E {G(g.GasE)}; захоронения верх E {G(g.BurTopE)} R {G(g.BurTopR)}, полы пещер E {G(g.BurCaveE)} R {G(g.BurCaveR)}, глубина E {G(g.BurDeepE)} R {G(g.BurDeepR)}");
        yield return Loc.T(
            $"top blocks: {G(g.TopMol)} mol, E {G(g.TopE)} ({F(g.TopE / g.TopMol)}/mol), R {G(g.TopR)} ({F(g.TopR / g.TopMol)}/mol), fuel mass share {F(g.TopFuelMass / g.TopMass, 3)}; crust: {G(g.CrustMol)} mol, E {G(g.CrustE)}, R {G(g.CrustR)} (R/E {F(g.CrustR / g.CrustE, 3)}), fuel mass share {F(g.CrustFuelMass / g.CrustMass, 3)}",
            $"верхние блоки: {G(g.TopMol)} мол, E {G(g.TopE)} ({F(g.TopE / g.TopMol)}/мол), R {G(g.TopR)} ({F(g.TopR / g.TopMol)}/мол), доля массы-топлива {F(g.TopFuelMass / g.TopMass, 3)}; кора: {G(g.CrustMol)} мол, E {G(g.CrustE)}, R {G(g.CrustR)} (R/E {F(g.CrustR / g.CrustE, 3)}), доля массы-топлива {F(g.CrustFuelMass / g.CrustMass, 3)}");
        yield return Loc.T(
            $"per cell: loose R p50 {F(Pct(g.LooseRCell, 0.5))} p90 {F(Pct(g.LooseRCell, 0.9))} p99 {F(Pct(g.LooseRCell, 0.99))} max {F(Pct(g.LooseRCell, 1))}; top-block R p50 {G(Pct(g.TopRCell, 0.5))} p90 {G(Pct(g.TopRCell, 0.9))} max {G(Pct(g.TopRCell, 1))}",
            $"на клетку: россыпь R p50 {F(Pct(g.LooseRCell, 0.5))} p90 {F(Pct(g.LooseRCell, 0.9))} p99 {F(Pct(g.LooseRCell, 0.99))} max {F(Pct(g.LooseRCell, 1))}; верхний блок R p50 {G(Pct(g.TopRCell, 0.5))} p90 {G(Pct(g.TopRCell, 0.9))} max {G(Pct(g.TopRCell, 1))}");
        yield return Loc.T(
            $"mining the top block (no teeth, no protein): cost/mol p10 {F(Pct(g.MineCost, 0.1))} p50 {F(Pct(g.MineCost, 0.5))} p90 {G(Pct(g.MineCost, 0.9))}; R/mol p50 {F(Pct(g.TopRPerMol, 0.5))} p90 {F(Pct(g.TopRPerMol, 0.9))}, E/mol p50 {F(Pct(g.TopEPerMol, 0.5))}; pays in R in {F(g.PayR / g.CellsTop, 3)} of cells (holding R {G(g.AccTopR)}), in E in {F(g.PayE / g.CellsTop, 3)}",
            $"добыча из верхнего блока (без зубов и белка): цена/мол p10 {F(Pct(g.MineCost, 0.1))} p50 {F(Pct(g.MineCost, 0.5))} p90 {G(Pct(g.MineCost, 0.9))}; R/мол p50 {F(Pct(g.TopRPerMol, 0.5))} p90 {F(Pct(g.TopRPerMol, 0.9))}, E/мол p50 {F(Pct(g.TopEPerMol, 0.5))}; окупается по R в {F(g.PayR / g.CellsTop, 3)} клеток (в них R {G(g.AccTopR)}), по E в {F(g.PayE / g.CellsTop, 3)}");
        yield return Loc.T(
            $"one level down: block below R/mol {F(g.BelowRPerMol)} E/mol {F(g.BelowEPerMol)}; dig the top block away p50 {F(g.DigCostMed)} (= {F(g.DigPerMolBelowMed, 3)} per molecule below), mining below p50 {F(g.BelowMineMed)}/mol; pays in R in {F(g.BelowPayR, 3)} of columns",
            $"на уровень ниже: блок под верхним R/мол {F(g.BelowRPerMol)} E/мол {F(g.BelowEPerMol)}; снести верхний блок p50 {F(g.DigCostMed)} (= {F(g.DigPerMolBelowMed, 3)} на молекулу ниже), добыча ниже p50 {F(g.BelowMineMed)}/мол; окупается по R в {F(g.BelowPayR, 3)} колонок");
    }

    IEnumerable<string> BodyLines(double[] r)
    {
        double store = 0, held = 0, me = 0, mr = 0, mol = 0, mass = 0;
        var perE = new List<double>(); var perR = new List<double>(); var perStore = new List<double>(); var tear = new List<double>();
        var cellE = new List<double>(); var cellR = new List<double>(); var cellIntake = new List<double>();
        var foodRatio = new List<double>(); var underR = new List<double>(); var underCost = new List<double>();
        Span<int> counts = stackalloc int[Chemistry.S];
        int pop = 0;
        foreach (var a in Agents)
        {
            if (a.Dead || a.InvTotal <= 0) continue;
            pop++;
            double e = 0, rr = 0;
            for (int s = 0; s < Chemistry.S; s++) { e += (double)a.Inv[s] * Chem.E[s]; rr += a.Inv[s] * r[s]; }
            store += Held(a); held += a.HeatHeld; me += e; mr += rr; mol += a.InvTotal; mass += a.Mass;   // store, law 1: the charge (also in E and R of its molecules)
            double n = a.InvTotal;
            double carried = MatterLaw ? 0 : P.TornStore * Math.Max(0, a.Energy) / n;   // one torn molecule's share (CarryStore); law 1: its excitation is in its R
            perE.Add(e / n); perR.Add(rr / n); perStore.Add(carried); tear.Add(TearCost(a));
            int cell = a.Y * W + a.X;
            if (a.Z >= 3 && OnFloor(a) && BlockCounts(cell * Z + a.Z - 1, counts))
            {
                double un = 0, ur = 0;
                for (int s = 0; s < Chemistry.S; s++) { un += counts[s]; ur += counts[s] * r[s]; }
                underR.Add(ur / un); underCost.Add(MineCostPerMolecule(cell * Z + a.Z - 1));
            }
            double le = 0, lr = 0, ln = 0;
            for (int s = 0; s < Chemistry.S; s++)
            {
                if (s == Chem.Gas) continue;
                double m = C[s][cell].D;
                ln += m; le += m * Chem.E[s]; lr += m * r[s];
            }
            if (ln >= 1)
            {
                cellE.Add(le / ln); cellR.Add(lr / ln);
                // Its uptake: one whole molecule per call at most, at the eater's own crowding.
                cellIntake.Add(P.CostIntake * (1 + a.Packing * a.Packing));
                // A body's molecule (R + store − tear) against a loose one beside it (R − uptake).
                foodRatio.Add((rr / n + carried - TearCost(a)) - (lr / ln - P.CostIntake));
            }
        }
        if (pop == 0) { yield return Loc.T("bodies: none", "тел нет"); yield break; }
        yield return Loc.T(
            $"bodies: {pop}, store {G(store)} ({F(store / pop)}/body), held heat {G(held)}, matter E {G(me)} R {G(mr)}; {F(mol / pop, 1)} mol and mass {F(mass / pop, 1)} per body; store per molecule {F(store / mol, 3)}, per mass {F(store / mass, 3)}",
            $"тела: {pop}, запас {G(store)} ({F(store / pop)}/тело), тепло в теле {G(held)}, вещество E {G(me)} R {G(mr)}; {F(mol / pop, 1)} мол и масса {F(mass / pop, 1)} на тело; запас на молекулу {F(store / mol, 3)}, на массу {F(store / mass, 3)}");
        double[] pe = perE.ToArray(), pr = perR.ToArray(), ps = perStore.ToArray(), tc = tear.ToArray();
        yield return Loc.T(
            $"eating a body, per molecule (p50 / mean): E {F(Pct(pe, 0.5))}/{F(pe.Average())}, R {F(Pct(pr, 0.5))}/{F(pr.Average())}, store carried {F(Pct(ps, 0.5), 3)}/{F(ps.Average(), 3)}, tear work {F(Pct(tc, 0.5), 3)}/{F(tc.Average(), 3)} (p10 {F(Pct(tc, 0.1), 3)} p90 {F(Pct(tc, 0.9), 3)})",
            $"поедание тела, на молекулу (p50 / среднее): E {F(Pct(pe, 0.5))}/{F(pe.Average())}, R {F(Pct(pr, 0.5))}/{F(pr.Average())}, переносимый запас {F(Pct(ps, 0.5), 3)}/{F(ps.Average(), 3)}, работа отрыва {F(Pct(tc, 0.5), 3)}/{F(tc.Average(), 3)} (p10 {F(Pct(tc, 0.1), 3)} p90 {F(Pct(tc, 0.9), 3)})");
        if (cellE.Count > 0)
        {
            double[] ce = cellE.ToArray(), cr = cellR.ToArray(), ci = cellIntake.ToArray(), fr = foodRatio.ToArray();
            yield return Loc.T(
                $"loose matter where a body stands (cells with ≥ 1 mol: {cellE.Count} bodies), per molecule: E {F(Pct(ce, 0.5))}/{F(ce.Average())}, R {F(Pct(cr, 0.5))}/{F(cr.Average())}, uptake cost {F(Pct(ci, 0.5), 4)}; body molecule minus loose molecule, net: p50 {F(Pct(fr, 0.5))} mean {F(fr.Average())}, body better in {F(fr.Count(x => x > 0) / (double)fr.Length, 3)}",
                $"россыпь там, где стоит тело (клетки с ≥ 1 мол: {cellE.Count} тел), на молекулу: E {F(Pct(ce, 0.5))}/{F(ce.Average())}, R {F(Pct(cr, 0.5))}/{F(cr.Average())}, цена поглощения {F(Pct(ci, 0.5), 4)}; молекула тела минус молекула россыпи, нетто: p50 {F(Pct(fr, 0.5))} среднее {F(fr.Average())}, тело выгоднее в {F(fr.Count(x => x > 0) / (double)fr.Length, 3)}");
        }
        if (underR.Count > 0)
        {
            double[] ur = underR.ToArray(), uc = underCost.ToArray();
            yield return Loc.T(
                $"the block under a body ({underR.Count} bodies on a floor): R/mol p50 {F(Pct(ur, 0.5))} mean {F(ur.Average())}, gnawing cost/mol p10 {F(Pct(uc, 0.1))} p50 {F(Pct(uc, 0.5))} p90 {G(Pct(uc, 0.9))}; pays for {F(ur.Zip(uc).Count(x => x.First > x.Second) / (double)ur.Length, 3)} of them",
                $"блок под телом ({underR.Count} тел на полу): R/мол p50 {F(Pct(ur, 0.5))} среднее {F(ur.Average())}, цена выгрызания/мол p10 {F(Pct(uc, 0.1))} p50 {F(Pct(uc, 0.5))} p90 {G(Pct(uc, 0.9))}; окупается для {F(ur.Zip(uc).Count(x => x.First > x.Second) / (double)ur.Length, 3)} из них");
        }
    }

    static string Per(double a, double b, int d = 3) => b > 0 ? F(a / b, d) : "-";

    IEnumerable<string> WindowLines(double[] f0, double[] f1, double[] q, long ticks, int births, int spawns, int deaths)
    {
        double days = ticks / (double)P.DayLen;
        string Fl(int k) => G((f1[k] - f0[k]) / days);
        yield return Loc.T(
            $"ledger per day: photo {Fl(FPhoto)}, strike {Fl(FStrike)}, vent {Fl(FVent)}, hand {Fl(FHand)}; dissipate {Fl(FDissipate)}, shed {Fl(FShed)}, death {Fl(FDeath)}, body decay {Fl(FBodyDecay)}, loose decay {Fl(FLooseDecay)}, stillborn {Fl(FStillborn)}; abiogenesis (internal) {Fl(FAbio)}",
            $"бюджет за день: фото {Fl(FPhoto)}, удар {Fl(FStrike)}, вулкан {Fl(FVent)}, рука {Fl(FHand)}; траты {Fl(FDissipate)}, сброс тепла {Fl(FShed)}, смерть {Fl(FDeath)}, распад в телах {Fl(FBodyDecay)}, распад на грунте {Fl(FLooseDecay)}, мертворождённые {Fl(FStillborn)}; абиогенез (внутр.) {Fl(FAbio)}");
        string D(int k) => G(q[k] / days);
        yield return Loc.T(
            $"ground↔bodies per day (E / R): intake {D(EnergyEconomyProbe.IntakeE)}/{D(EnergyEconomyProbe.IntakeR)}, soak {D(EnergyEconomyProbe.SoakE)}/{D(EnergyEconomyProbe.SoakR)}, mine {D(EnergyEconomyProbe.MineE)}/{D(EnergyEconomyProbe.MineR)}; expel {D(EnergyEconomyProbe.ExpelE)}/{D(EnergyEconomyProbe.ExpelR)}, remains {D(EnergyEconomyProbe.DeathMatterE)}/{D(EnergyEconomyProbe.DeathMatterR)}; weathering rock→loose {D(EnergyEconomyProbe.WeatherE)}/{D(EnergyEconomyProbe.WeatherR)}, compaction loose→rock {D(EnergyEconomyProbe.SettleE)}/{D(EnergyEconomyProbe.SettleR)}",
            $"грунт↔тела за день (E / R): поглощение {D(EnergyEconomyProbe.IntakeE)}/{D(EnergyEconomyProbe.IntakeR)}, впитывание {D(EnergyEconomyProbe.SoakE)}/{D(EnergyEconomyProbe.SoakR)}, добыча {D(EnergyEconomyProbe.MineE)}/{D(EnergyEconomyProbe.MineR)}; выброс {D(EnergyEconomyProbe.ExpelE)}/{D(EnergyEconomyProbe.ExpelR)}, останки {D(EnergyEconomyProbe.DeathMatterE)}/{D(EnergyEconomyProbe.DeathMatterR)}; выветривание скала→россыпь {D(EnergyEconomyProbe.WeatherE)}/{D(EnergyEconomyProbe.WeatherR)}, уплотнение россыпь→скала {D(EnergyEconomyProbe.SettleE)}/{D(EnergyEconomyProbe.SettleR)}");
        yield return Loc.T(
            $"reactions in bodies per day: split +{D(EnergyEconomyProbe.SplitExoE)} ({D(EnergyEconomyProbe.SplitExoN)}×) uphill {D(EnergyEconomyProbe.SplitUpE)} ({D(EnergyEconomyProbe.SplitUpN)}×); bind +{D(EnergyEconomyProbe.BindExoE)} ({D(EnergyEconomyProbe.BindExoN)}×) uphill {D(EnergyEconomyProbe.BindUpE)} ({D(EnergyEconomyProbe.BindUpN)}×); photons {D(EnergyEconomyProbe.PhotoGain)} ({D(EnergyEconomyProbe.PhotoCaught)} caught)",
            $"реакции в телах за день: распад +{D(EnergyEconomyProbe.SplitExoE)} ({D(EnergyEconomyProbe.SplitExoN)}×) в гору {D(EnergyEconomyProbe.SplitUpE)} ({D(EnergyEconomyProbe.SplitUpN)}×); связывание +{D(EnergyEconomyProbe.BindExoE)} ({D(EnergyEconomyProbe.BindExoN)}×) в гору {D(EnergyEconomyProbe.BindUpE)} ({D(EnergyEconomyProbe.BindUpN)}×); фотоны {D(EnergyEconomyProbe.PhotoGain)} ({D(EnergyEconomyProbe.PhotoCaught)} поймано)");
        // Per action: calls, cost per call, molecules per call, E and R per molecule, net R per call.
        string Act(string en, string ru, int calls, int cost, int mol, double extra = 0)
        {
            double n = q[calls], c = q[cost], m = q[mol], e = q[mol + 1], rr = q[mol + 2];
            string net = n > 0 ? F((rr + extra - c) / n, 4) : "-";
            return $"{Loc.T(en, ru)} {G(n / days)}/d, {Loc.T("cost", "цена")} {Per(c, n, 4)}, {Loc.T("mol", "мол")} {Per(m, n, 4)}, E/{Loc.T("mol", "мол")} {Per(e, m, 2)}, R/{Loc.T("mol", "мол")} {Per(rr, m, 2)}, {Loc.T("net R", "нетто R")} {net}";
        }
        yield return Act("intake", "поглощение", EnergyEconomyProbe.IntakeCalls, EnergyEconomyProbe.IntakeCost, EnergyEconomyProbe.IntakeMol);
        yield return Act("soak", "впитывание", EnergyEconomyProbe.SoakCalls, EnergyEconomyProbe.SoakCost, EnergyEconomyProbe.SoakMol);
        yield return Act("mine try", "попытка добычи", EnergyEconomyProbe.MineTries, EnergyEconomyProbe.MineCost, EnergyEconomyProbe.MineMol)
            + $", {Loc.T("cost/mol", "цена/мол")} {Per(q[EnergyEconomyProbe.MineCost], q[EnergyEconomyProbe.MineMol], 2)}";
        yield return Act("attack", "атака", EnergyEconomyProbe.AttackCalls, EnergyEconomyProbe.AttackCost, EnergyEconomyProbe.TornMol, q[EnergyEconomyProbe.TornStore])
            + $", {Loc.T("store/mol", "запас/мол")} {Per(q[EnergyEconomyProbe.TornStore], q[EnergyEconomyProbe.TornMol], 3)}, {Loc.T("kills", "убийств")} {G(q[EnergyEconomyProbe.KilledN] / days)}/d";
        yield return Act("take", "кража", EnergyEconomyProbe.TakeCalls, EnergyEconomyProbe.TakeCost, EnergyEconomyProbe.TakenMol, q[EnergyEconomyProbe.TakenStore])
            + $", {Loc.T("store/mol", "запас/мол")} {Per(q[EnergyEconomyProbe.TakenStore], q[EnergyEconomyProbe.TakenMol], 3)}";
        yield return Loc.T($"dig {G(q[EnergyEconomyProbe.DigCalls] / days)}/d, cost {Per(q[EnergyEconomyProbe.DigCost], q[EnergyEconomyProbe.DigCalls], 2)}; photo gain per photon {Per(q[EnergyEconomyProbe.PhotoGain], q[EnergyEconomyProbe.PhotoCaught], 2)}",
                           $"копание {G(q[EnergyEconomyProbe.DigCalls] / days)}/д, цена {Per(q[EnergyEconomyProbe.DigCost], q[EnergyEconomyProbe.DigCalls], 2)}; фото на фотон {Per(q[EnergyEconomyProbe.PhotoGain], q[EnergyEconomyProbe.PhotoCaught], 2)}");
        string[] dn = Loc.En ? new[] { "idle", "plant", "eater", "miner", "hunter" } : new[] { "праздные", "растения", "едоки", "добытчики", "охотники" };
        double all = 0;
        for (int d = 0; d < 5; d++) all += q[EnergyEconomyProbe.DietBase + d * EnergyEconomyProbe.DietKeys];
        var parts = new List<string>();
        for (int d = 0; d < 5; d++)
        {
            int b = EnergyEconomyProbe.DietBase + d * EnergyEconomyProbe.DietKeys;
            double t = q[b];
            if (t <= 0) { parts.Add($"{dn[d]} 0"); continue; }
            parts.Add($"{dn[d]} {F(t / all, 3)}: net {F(q[b + 1] / t, 4)} +kids {F(q[b + 2] / t, 4)} photo {F(q[b + 3] / t, 4)} chem {F(q[b + 4] / t, 4)} mineE {F(q[b + 5] / t, 4)} upkeep {F(q[b + 6] / t, 4)}");
        }
        yield return Loc.T("per body-tick by diet (share: net, given to offspring, photo, chemistry, mined E, upkeep): ", "на тело-тик по диете (доля: нетто, детям, фото, химия, добыто E, содержание): ") + string.Join("; ", parts);
        double dN = q[EnergyEconomyProbe.DeathN], st = q[EnergyEconomyProbe.DeathStore], dE = q[EnergyEconomyProbe.DeathMatterE];
        yield return Loc.T(
            $"deaths {G(dN / days)}/d: store+held to heat {Per(st, dN, 2)}/body, matter to remains E {Per(dE, dN, 1)} R {Per(q[EnergyEconomyProbe.DeathMatterR], dN, 1)}/body ({Per(q[EnergyEconomyProbe.DeathMol], dN, 1)} mol, mass {Per(q[EnergyEconomyProbe.DeathMass], dN, 1)}); store share of the body's energy {F(st / (st + dE), 3)}, of its downhill energy {F(st / (st + q[EnergyEconomyProbe.DeathMatterR]), 3)}; killed {G(q[EnergyEconomyProbe.KilledN] / days)}/d with store {Per(q[EnergyEconomyProbe.KilledStore], q[EnergyEconomyProbe.KilledN], 2)} and {Per(q[EnergyEconomyProbe.KilledMol], q[EnergyEconomyProbe.KilledN], 1)} mol left",
            $"смертей {G(dN / days)}/д: запас+тепло в тепло {Per(st, dN, 2)}/тело, вещество в останки E {Per(dE, dN, 1)} R {Per(q[EnergyEconomyProbe.DeathMatterR], dN, 1)}/тело ({Per(q[EnergyEconomyProbe.DeathMol], dN, 1)} мол, масса {Per(q[EnergyEconomyProbe.DeathMass], dN, 1)}); доля запаса в энергии тела {F(st / (st + dE), 3)}, в энергии под гору {F(st / (st + q[EnergyEconomyProbe.DeathMatterR]), 3)}; убито {G(q[EnergyEconomyProbe.KilledN] / days)}/д с запасом {Per(q[EnergyEconomyProbe.KilledStore], q[EnergyEconomyProbe.KilledN], 2)} и {Per(q[EnergyEconomyProbe.KilledMol], q[EnergyEconomyProbe.KilledN], 1)} мол");
        yield return Loc.T($"births {births} ({G(births / days)}/d), abiogenesis {spawns} ({G(spawns / days)}/d), deaths {deaths}",
                           $"рождений {births} ({G(births / days)}/д), абиогенез {spawns} ({G(spawns / days)}/д), смертей {deaths}");
        // The local abiogenesis law (AbioModel 1) at the window's end, whichever law runs: Σ of the cells' chances.
        double pSum = 0, pWater = 0;
        int pCells = 0, pReady = 0;
        for (int i = 0; i < N; i++)
        {
            float p = AbioCellChance(i);
            if (p <= 0) continue;
            pCells++; pSum += p;
            if (Submerged(i)) pWater += p;
            if (p >= 0.5f * P.AbioCellRate) pReady++;
        }
        yield return Loc.T($"local abiogenesis law now: expected {G(pSum * P.DayLen)}/d over {pCells} cells ({pReady} at ≥ half the rate), under water {F(pSum > 0 ? pWater / pSum : 0, 2)} of it",
                           $"закон местного абиогенеза сейчас: ожидается {G(pSum * P.DayLen)}/д по {pCells} клеткам ({pReady} — не меньше половины ставки), под водой {F(pSum > 0 ? pWater / pSum : 0, 2)}");
    }

    static IEnumerable<string> ChemLines(Chemistry ch, double[] r)
    {
        var comp = Enumerable.Range(0, Chemistry.S).Where(s => ch.AtomCount(s) > 1).ToArray();
        double[] e = comp.Select(s => (double)ch.E[s]).ToArray();
        double[] Col(Func<int, double> f) => comp.Select(f).ToArray();
        var cols = new (string name, double[] v)[]
        {
            ("bond", Col(s => ch.Bond[s])), ("mass", Col(s => ch.Mass[s])), ("atoms", Col(s => ch.AtomCount(s))), ("affinity/atom", Col(s => ch.AffinityPerAtom[s])),
        };
        int splits = Enumerable.Range(0, Chemistry.S).Count(s => ch.SplitA[s] >= 0), exo = ch.Unstable.Length;
        int groundExo = Enumerable.Range(0, Chemistry.S).Count(s => s % 2 == 0 && ch.SplitExo[s] && ch.AtomCount(s) > 1);
        int bindExo = 0, bindAll = 0;
        for (int a = 0; a < Chemistry.S; a++) for (int b = a; b < Chemistry.S; b++) { int p = ch.Combine[a, b]; if (p < 0) continue; bindAll++; if (ch.E[a] + ch.E[b] > ch.E[p]) bindExo++; }
        double sumE = comp.Sum(s => (double)ch.E[s]), sumR = comp.Sum(s => r[s]);
        yield return "chem: " + string.Join(", ", cols.Select(c => $"E~{c.name} r {F(Oscillation.Pearson(e, c.v))} ρ {F(Oscillation.Spearman(e, c.v))}"))
            + Loc.T($"; splits {splits}, exothermic {exo} (ground compounds {groundExo} of 12); binds exothermic {bindExo} of {bindAll}; compounds R/E {F(sumR / sumE, 3)}",
                    $"; распадов {splits}, экзотермичных {exo} (основных соединений {groundExo} из 12); связываний экзотермичных {bindExo} из {bindAll}; соединения R/E {F(sumR / sumE, 3)}");
    }

    // --chem-table [--seeds 1-6] [--verbose]: the chemistry of each seed under the current laws (no world is
    // made): per species E, its energy above its elements, formation energy and caged atoms (model 1),
    // the split's dE, R and the bond; then the summary line of the energy audit and a pooled one.
    public static void ChemTable(string[] args)
    {
        string Arg(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        bool verbose = Array.IndexOf(args, "--verbose") >= 0;
        int exoAll = 0, groundExoAll = 0, bindExoAll = 0, bindUpAll = 0, bindAll = 0, n = 0;
        var e = new List<double>(); var bond = new List<double>();
        foreach (int seed in Batch.ParseSeeds(Arg("--seeds", "1-6")))
        {
            var ch = new Chemistry(seed);
            var r = EnergyEconomyProbe.Downhill(ch);
            Console.WriteLine($"=== seed {seed}: model {ch.Model}, zero {ch.EnergyZero}/atom; elements " + string.Join(" ", Enumerable.Range(0, Chemistry.ElementCount).Select(k => $"{ch.ElementName[k]}(v{ch.Valence[k]} x{ch.Affinity[k]:0.00})")));
            if (verbose)
                for (int s = 0; s < Chemistry.S; s++)
                    Console.WriteLine($"  {s,2} {ch.Formula(s) + (s % 2 == 1 ? "*" : ""),-12} E {ch.E[s],3}  above elements {ch.E[s] - ch.EnergyZero * ch.AtomCount(s),4}  form {ch.Formation[s],6:0.0} caged {ch.Caged[s]}  split {(ch.SplitA[s] >= 0 ? ch.SplitEnergy(s).ToString("+0;-0;0") : "-"),4}  R {r[s],4:0}  bond {ch.Bond[s]:0.00}{(ch.Solid[s] ? " solid" : "")}{(s == ch.Gas ? " gas" : "")}{(ch.VentHigh.Contains(s) ? " vent" : "")}");
            foreach (var line in ChemLines(ch, r)) Console.WriteLine("  " + line);
            exoAll += ch.Unstable.Length;
            groundExoAll += Enumerable.Range(0, Chemistry.S).Count(s => s % 2 == 0 && ch.SplitExo[s] && ch.AtomCount(s) > 1);
            for (int a = 0; a < Chemistry.S; a++) for (int b = a; b < Chemistry.S; b++) { int p = ch.Combine[a, b]; if (p < 0) continue; bindAll++; if (ch.E[a] + ch.E[b] > ch.E[p]) bindExoAll++; if (ch.E[a] + ch.E[b] < ch.E[p]) bindUpAll++; }
            for (int s = 0; s < Chemistry.S; s += 2) if (ch.AtomCount(s) > 1) { e.Add(ch.E[s] - (ch.Model == 0 ? 0 : ch.EnergyZero * ch.AtomCount(s))); bond.Add(ch.Bond[s]); }
            n++;
        }
        Console.WriteLine($"pooled over {n} seeds: exothermic splits {exoAll} of {28 * n}, ground compounds with an exothermic split {groundExoAll} of {12 * n}, exothermic binds {bindExoAll} of {bindAll} (uphill {bindUpAll}); r(E above elements, bond) of ground compounds {F(Oscillation.Pearson(e.ToArray(), bond.ToArray()))}");
    }

    public static void EnergyAuditReport(string[] args)
    {
        string Arg(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        int ticks = int.Parse(Arg("--ticks", "6000")), every = int.Parse(Arg("--every", "2000")), series = int.Parse(Arg("--series", "500"));
        int pop = int.Parse(Arg("--pop", "-1")), noAbioAt = int.Parse(Arg("--noabio-at", "-1"));
        bool abio = Array.IndexOf(args, "--noabio") < 0, probeOn = Array.IndexOf(args, "--no-probe") < 0;
        string load = Arg("--load", null);
        var seeds = load != null ? new List<int> { 0 } : Batch.ParseSeeds(Arg("--seeds", "1-6"));
        Console.WriteLine(Loc.T($"energy audit: {(load ?? "seeds " + string.Join(",", seeds))}, {ticks} ticks, windows of {every}{(noAbioAt >= 0 ? $", abiogenesis off at tick {noAbioAt}" : "")}{(probeOn ? "" : ", no probe")}",
                                $"аудит энергии: {(load ?? "сиды " + string.Join(",", seeds))}, {ticks} тиков, окна по {every}{(noAbioAt >= 0 ? $", абиогенез выключен с тика {noAbioAt}" : "")}{(probeOn ? "" : ", без зонда")}"));
        var pooledE = new List<double>(); var pooledCols = new List<double>[4];
        for (int k = 0; k < 4; k++) pooledCols[k] = new List<double>();
        foreach (int seed0 in seeds)
        {
            var w = load != null ? Load(load) : new World(Batch.Settings(args, seed0, pop, abio, 0));   // --size WxHxL: a small world
            if (load != null) foreach (var line in ParamHook.Apply(ParamHook.Parse(args))) Console.WriteLine("set after load " + line);
            var r = EnergyEconomyProbe.Downhill(w.Chem);
            Console.WriteLine($"=== seed {w.Seed} tick {w.Tick} pop {w.Agents.Count} ===");
            if (probeOn)
            {
                foreach (var line in ChemLines(w.Chem, r)) Console.WriteLine("  " + line);
                var ch = w.Chem;
                foreach (int s in Enumerable.Range(0, Chemistry.S).Where(s => ch.AtomCount(s) > 1))
                {
                    pooledE.Add(ch.E[s]);
                    pooledCols[0].Add(ch.Bond[s]); pooledCols[1].Add(ch.Mass[s]); pooledCols[2].Add(ch.AtomCount(s)); pooledCols[3].Add(ch.AffinityPerAtom[s]);
                }
                Console.WriteLine("  species: " + string.Join(" ", Enumerable.Range(0, Chemistry.S).Select(s =>
                    $"{ch.Formula(s)}{(s % 2 == 1 ? "*" : "")}:E{ch.E[s]}/R{r[s]:0}/b{ch.Bond[s]:0.00}")));
                foreach (var line in w.GroundLines(w.Ground(r))) Console.WriteLine("  [t0] " + line);
                foreach (var line in w.BodyLines(r)) Console.WriteLine("  [t0] " + line);
                w.EnergyProbe = new EnergyEconomyProbe(w);
                w.PredProbe = new PredationProbe();
            }
            var f0 = w.EnergyFlows();
            int births0 = w.Births, spawns0 = w.Spawns, deaths0 = w.Deaths;
            long t0 = w.Tick;
            double bur0 = w.BurialDecayHeat, blk0 = w.BlockDecayHeat;
            var line2 = new List<string>();
            for (int t = 1; t <= ticks; t++)
            {
                if (t == noAbioAt) w.Abiogenesis = false;
                w.Step();
                if (t % series == 0) line2.Add($"{t}:{w.Agents.Count}/{w.Births}/{w.Spawns}");
                if (t % every != 0 && t != ticks) continue;
                Console.WriteLine($"  -- tick {w.Tick} pop {w.Agents.Count} hash {w.StateHash():x16}");
                if (!probeOn) continue;
                var f1 = w.EnergyFlows();
                var q = w.EnergyProbe.Sum();
                foreach (var line in w.WindowLines(f0, f1, q, w.Tick - t0, w.Births - births0, w.Spawns - spawns0, w.Deaths - deaths0)) Console.WriteLine("  " + line);
                var pp = w.PredProbe;
                Console.WriteLine("  " + Loc.T($"predation probe: attacks {pp.Attacks} (missed {pp.Missed}), torn {pp.Torn} E {G(pp.TornE)} split {G(pp.TornSplit)}, store carried {G(pp.Store)}, kills {pp.Kills}, scavenged {pp.Scavenged}; env molecules {pp.EnvMols} E {G(pp.EnvE)}; photo {G(pp.Photo)}",
                                                  $"зонд хищничества: атак {pp.Attacks} (мимо {pp.Missed}), оторвано {pp.Torn} E {G(pp.TornE)} распад {G(pp.TornSplit)}, перенесено запаса {G(pp.Store)}, убийств {pp.Kills}, падаль {pp.Scavenged}; молекул из среды {pp.EnvMols} E {G(pp.EnvE)}; фото {G(pp.Photo)}"));
                var g = w.Ground(r);
                foreach (var line in w.GroundLines(g)) Console.WriteLine("  " + line);
                foreach (var line in w.BodyLines(r)) Console.WriteLine("  " + line);
                // H1: how long the ground's downhill energy would feed the bodies at the rate they release energy.
                double days = (w.Tick - t0) / (double)P.DayLen;
                double release = (q[EnergyEconomyProbe.SplitExoE] + q[EnergyEconomyProbe.BindExoE]) / days;
                double takenR = (q[EnergyEconomyProbe.IntakeR] + q[EnergyEconomyProbe.SoakR] + q[EnergyEconomyProbe.MineR]) / days;
                double battery = g.LooseR + g.BurTopR + g.TopR, batteryE = g.LooseE + g.BurTopE + g.TopE;
                double upkeep = (f1[FDissipate] - f0[FDissipate]) / days;
                double looseTaken = (q[EnergyEconomyProbe.IntakeR] + q[EnergyEconomyProbe.SoakR]) / days, accessible = g.LooseR + g.BurTopR + g.AccTopR;
                Console.WriteLine("  " + Loc.T(
                    $"battery (loose + top burials + top blocks): R {G(battery)}, E {G(batteryE)}; bodies release {G(release)}/d, take in R {G(takenR)}/d, spend {G(upkeep)}/d -> R lasts {F(battery / release, 1)} days at the release rate, {F(battery / upkeep, 1)} at the spending rate; reachable (loose + top burials + top blocks that pay to gnaw) R {G(accessible)} = {F(accessible / release, 1)} days; loose alone {F((g.LooseR + g.BurTopR) / Math.Max(1e-9, looseTaken), 1)} days at the uptake rate; loose decay drains {G((f1[FLooseDecay] - f0[FLooseDecay]) / days)}/d",
                    $"батарея (россыпь + верхние захоронения + верхние блоки): R {G(battery)}, E {G(batteryE)}; тела высвобождают {G(release)}/д, поглощают R {G(takenR)}/д, тратят {G(upkeep)}/д -> R хватит на {F(battery / release, 1)} дней по высвобождению, {F(battery / upkeep, 1)} по тратам; доступно (россыпь + верхние захоронения + окупаемые верхние блоки) R {G(accessible)} = {F(accessible / release, 1)} дней; одной россыпи {F((g.LooseR + g.BurTopR) / Math.Max(1e-9, looseTaken), 1)} дней по поглощению; распад на грунте уносит {G((f1[FLooseDecay] - f0[FLooseDecay]) / days)}/д"));
                {
                    // Ground decay by pool (ArrheniusDecay 1 adds burials and blocks; the flow `loose decay` holds all three)
                    // and the excitation lying loose (Σ C·Gap over the surface): "dead soil" away from where energy flows in.
                    double bur = (w.BurialDecayHeat - bur0) / days, blk = (w.BlockDecayHeat - blk0) / days;
                    double all = (f1[FLooseDecay] - f0[FLooseDecay]) / days, looseX = 0;
                    var xs = new double[w.N];
                    for (int c = 0; c < w.N; c++) looseX += xs[c] = w.LooseExcitation(c);
                    // Where it lies: the richest 1 % of cells, cells within 6 of a vent, cells where bodies died lately
                    // (DeathMap > 0.05: a death within ~4 env-step half-lives), and the mean of the rest ("dead soil").
                    var near = new bool[w.N]; var dead = new bool[w.N];
                    foreach (var vent in w.Vents)
                        for (int dy = -6; dy <= 6; dy++)
                            for (int dx = -6; dx <= 6; dx++)
                            {
                                int y = vent.Y + dy; if (y < 0 || y >= w.H || dx * dx + dy * dy > 36) continue;
                                near[y * w.W + ((vent.X + dx) % w.W + w.W) % w.W] = true;
                            }
                    double xNear = 0, xDead = 0, xRest = 0; int nNear = 0, nDead = 0, nRest = 0;
                    for (int c = 0; c < w.N; c++)
                    {
                        dead[c] = w.DeathMap[c] > 0.05f;
                        if (near[c]) { xNear += xs[c]; nNear++; }
                        else if (dead[c]) { xDead += xs[c]; nDead++; }
                        else { xRest += xs[c]; nRest++; }
                    }
                    var sorted = xs.OrderByDescending(x => x).ToArray();
                    double top = 0; for (int k = 0; k < Math.Max(1, w.N / 100); k++) top += sorted[k];
                    double Sh(double x) => looseX > 0 ? x / looseX : 0;
                    Console.WriteLine("  " + Loc.T(
                        $"ground decay per day by pool: loose {G(all - bur - blk)}, burials {G(bur)}, blocks {G(blk)}; loose excitation now {G(looseX)} ({F(looseX / w.N, 3)} per cell): richest 1 % of cells {F(Sh(top), 3)}, near vents {F(Sh(xNear), 3)} ({F(nNear / (double)w.N, 3)} of cells, {F(xNear / Math.Max(1, nNear), 2)} per cell), recent deaths {F(Sh(xDead), 3)} ({F(nDead / (double)w.N, 3)} of cells, {F(xDead / Math.Max(1, nDead), 2)} per cell), the rest {F(xRest / Math.Max(1, nRest), 2)} per cell",
                        $"распад в грунте за день по запасам: россыпь {G(all - bur - blk)}, захоронения {G(bur)}, блоки {G(blk)}; возбуждение россыпи сейчас {G(looseX)} ({F(looseX / w.N, 3)} на клетку): самые богатые 1 % клеток {F(Sh(top), 3)}, у вулканов {F(Sh(xNear), 3)} ({F(nNear / (double)w.N, 3)} клеток, {F(xNear / Math.Max(1, nNear), 2)} на клетку), недавние смерти {F(Sh(xDead), 3)} ({F(nDead / (double)w.N, 3)} клеток, {F(xDead / Math.Max(1, nDead), 2)} на клетку), остальное {F(xRest / Math.Max(1, nRest), 2)} на клетку"));
                    bur0 = w.BurialDecayHeat; blk0 = w.BlockDecayHeat;
                }
                f0 = f1; births0 = w.Births; spawns0 = w.Spawns; deaths0 = w.Deaths; t0 = w.Tick;
                w.EnergyProbe.Clear(); pp.Clear();
            }
            Console.WriteLine("  " + Loc.T("series tick:pop/births/abiogenesis ", "ряд тик:население/рождения/абиогенез ") + string.Join(" ", line2));
        }
        if (pooledE.Count > 0)
        {
            var e = pooledE.ToArray();
            string[] names = { "bond", "mass", "atoms", "affinity/atom" };
            Console.WriteLine(Loc.T($"pooled over seeds ({e.Length} compound species): ", $"по всем сидам ({e.Length} видов-соединений): ")
                + string.Join(", ", Enumerable.Range(0, 4).Select(k => $"E~{names[k]} r {F(Oscillation.Pearson(e, pooledCols[k].ToArray()))} ρ {F(Oscillation.Spearman(e, pooledCols[k].ToArray()))}")));
        }
    }
}
