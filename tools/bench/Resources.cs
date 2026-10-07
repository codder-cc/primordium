using System;
using System.Linq;

namespace Primordium;

// --resources [--seeds 1-4] [--ticks 6000] [--from 3000] [--pop N] [--noabio] (laws as usual):
// where the air's gas and the loose food go, region by region (32×32), ROADMAP 4 diagnosis.
// From tick `from` to `ticks` the resource probe (World.ResProbe) books every tick's change of the
// surface pools by phase (diffusion, environment, bodies, other) and the bodies' gross uptake.
// Observation only: the world runs the same trajectory (same hash) with the probe on.
public sealed partial class World
{
    public static void ResourceReport(string[] args)
    {
        string Arg(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        var seeds = Batch.ParseSeeds(Arg("--seeds", "1-4"));
        int ticks = int.Parse(Arg("--ticks", "6000")), from = int.Parse(Arg("--from", "3000"));
        int pop = int.Parse(Arg("--pop", P.InitialPop.ToString()));
        bool abio = Array.IndexOf(args, "--noabio") < 0;
        const double Q = Qty.One;
        foreach (int seed in seeds)
        {
            var w = new World(seed, pop, abio);
            var ch = w.Chem;
            float d = ch.Diff[ch.Gas] * (float)P.GasDiffK;
            Console.WriteLine($"=== seed {seed} === gas {ch.Formula(ch.Gas)}{(ch.Gas % 2 == 1 ? "*" : "")} mass {ch.Mass[ch.Gas]:F2}, diffusion {d:F4} per edge per tick"
                + $" (GasDiffK {P.GasDiffK}): rms travel {Math.Sqrt(4 * d * 1):F2} cells/tick, {Math.Sqrt(4 * d * P.DayLen):F1}/day, {Math.Sqrt(4 * d * P.DayLen * P.YearDays):F0}/year (flat ground)");
            for (int t = 1; t <= ticks; t++)
            {
                if (t == from) w.ResProbe = new ResourceProbe();
                w.Step();
            }
            var p = w.ResProbe;
            double T = Math.Max(1, p.Ticks);
            // Stocks at the end: surface gas, loose food, gas and food adsorbed in the top block (reachable on the floor).
            var gas = new double[Regions]; var food = new double[Regions]; var adsGas = new double[Regions]; var adsFood = new double[Regions];
            for (int i = 0; i < N; i++)
            {
                int r = RegionOf(i);
                gas[r] += w.C[ch.Gas][i];
                for (int s = 0; s < Chemistry.S; s++) if (s != ch.Gas) food[r] += w.C[s][i];
            }
            foreach (var kv in w.Buried)
            {
                int c = kv.Key / Z, z = kv.Key % Z;
                if (z != w.Height[c] - 1) continue;
                int r = RegionOf(c);
                for (int s = 0; s < Chemistry.S; s++) { if (s == ch.Gas) adsGas[r] += kv.Value.Matter[s]; else adsFood[r] += kv.Value.Matter[s]; }
            }
            double bodyGas = 0;
            int g0 = ch.Gas & ~1;
            foreach (var a in w.Agents) if (!a.Dead) bodyGas += a.Inv[g0] + a.Inv[g0 + 1];
            int alive = w.Agents.Count(a => !a.Dead);
            {
                // How much a body takes in over its life: matter is for growth and division, energy comes from light on what it holds.
                var live = w.Agents.Where(a => !a.Dead).ToList();
                double never = live.Count(a => a.NIntake == 0), old = live.Count(a => a.Age >= 2000), oldNever = live.Count(a => a.Age >= 2000 && a.NIntake == 0);
                double rate = live.Sum(a => (double)a.NIntake) / Math.Max(1, live.Sum(a => (double)a.Age)) * 1000;
                double photo = live.Sum(a => (double)a.NPhoto) / Math.Max(1, live.Sum(a => (double)a.Age)) * 1000;
                Console.WriteLine($"  bodies: mean {live.Average(a => (double)a.InvTotal):F1} molecules; took in nothing all life {never / Math.Max(1, live.Count):P0} (of those older than 2000 ticks: {oldNever / Math.Max(1, old):P0} of {old}); "
                    + $"whole molecules taken in per 1000 ticks of life {rate:F1}, photons caught {photo:F0}");
            }
            double gI = p.GasIntake.Sum() / Q / T, fI = p.FoodIntake.Sum() / Q / T;
            Console.WriteLine($"  tick {ticks}: {alive} bodies; gas: air {gas.Sum():F0}, adsorbed on top blocks {adsGas.Sum():F0}, in bodies {bodyGas:F0} molecules (gas formula = {w.GasShareOfBodies():P1} of body atoms);"
                + $" loose food {food.Sum():F0} (+{adsFood.Sum():F0} in top-block burials)");
            Console.WriteLine($"  per tick over {T} ticks: gas uptake {gI:F2} (under roofs {p.CaveGasIntake.Sum() / Q / T:F2}), food uptake {fI:F2};"
                + $" diffusion moves {p.DiffusionGross / Q / T:F1} gas/tick between cells; rain adsorbs {p.Rain.Sum() / Q / T:F3}");
            string[] ph = ResourceProbe.PhaseNames;
            Console.WriteLine("  net change of the planet's surface gas by phase, per tick: " + string.Join(", ", Enumerable.Range(0, ResourceProbe.Phases)
                .Select(k => $"{ph[k]} {Enumerable.Range(0, Regions).Sum(r => p.Gas[k, r]) / Q / T:+0.000;-0.000}")));
            Console.WriteLine("  ... of the loose food: " + string.Join(", ", Enumerable.Range(0, ResourceProbe.Phases)
                .Select(k => $"{ph[k]} {Enumerable.Range(0, Regions).Sum(r => p.Food[k, r]) / Q / T:+0.000;-0.000}")));
            Console.WriteLine("  species (loose stock now; per tick: uptake, expel, net by diffusion/env/bio/other), the largest uptakes and stocks:");
            foreach (int s in Enumerable.Range(0, Chemistry.S).OrderByDescending(s => p.SpeciesIntake[s] * 1000.0 + w.C[s].Sum(q => q.D) / 1000).Take(6))
            {
                double stock = w.C[s].Sum(q => q.D);
                Console.WriteLine($"    {ch.Formula(s)}{(s % 2 == 1 ? "*" : "")}{(s == ch.Gas ? " (gas)" : (s & ~1) == (ch.Gas & ~1) ? " (gas formula)" : "")}: stock {stock:F0}, uptake {p.SpeciesIntake[s] / Q / T:F3}, expel {p.SpeciesExpel[s] / Q / T:F3}, net "
                    + string.Join(" ", Enumerable.Range(0, ResourceProbe.Phases).Select(k => $"{p.Species[k, s] / Q / T:+0.000;-0.000}")) + $"; E {ch.E[s]}, split->{ch.SplitA[s]}{(ch.SplitB[s] >= 0 ? "+" + ch.SplitB[s] : "")} releases {(ch.SplitExo[s] ? ch.SplitEnergy(s) : 0)}");
            }
            Console.WriteLine($"  global gas turnover (air ÷ uptake): {(gI > 0 ? gas.Sum() / gI : double.PositiveInfinity):F0} ticks; food: {(fI > 0 ? food.Sum() / fI : double.PositiveInfinity):F0} ticks");
            // Per region: where uptake comes from.
            double sumI = 0, byLocal = 0, byImport = 0, byStock = 0, byEnv = 0;
            var rows = Enumerable.Range(0, Regions).Select(r =>
            {
                double I = p.GasIntake[r] / Q / T, D = p.Gas[ResourceProbe.PDiffusion, r] / Q / T, E = p.Gas[ResourceProbe.PEnv, r] / Q / T;
                double B = p.Gas[ResourceProbe.PBio, r] / Q / T, O = p.Gas[ResourceProbe.POther, r] / Q / T;
                double rel = B + I;   // what bodies gave back (expel, death) = net bio + uptake
                return (r, pop: p.Pop[r] / T, G: gas[r], A: adsGas[r], F: food[r], I, D, E, rel, O, FI: p.FoodIntake[r] / Q / T, FD: (p.Food[ResourceProbe.PEnv, r] + p.Food[ResourceProbe.POther, r]) / Q / T, FB: p.Food[ResourceProbe.PBio, r] / Q / T);
            }).ToList();
            foreach (var x in rows)
            {
                if (x.I <= 0) continue;
                sumI += x.I;
                double local = Math.Min(x.I, Math.Max(0, x.rel)), rest = x.I - local;
                double imp = Math.Min(rest, Math.Max(0, x.D)); rest -= imp;
                double env = Math.Min(rest, Math.Max(0, x.E)); rest -= env;
                byLocal += local; byImport += imp; byEnv += env; byStock += rest;
            }
            if (sumI > 0)
                Console.WriteLine($"  gas uptake covered by: bodies' own release in the region {byLocal / sumI:P0}, diffusion from other regions {byImport / sumI:P0}, decay/env {byEnv / sumI:P0}, the region's stock {byStock / sumI:P0}");
            Console.WriteLine("  region     bodies   gas air  adsorbed  uptake/t  release/t  diff in/t  env/t   deplete(days, uptake only)  food  food up/t  food env/t");
            foreach (var x in rows.OrderByDescending(x => x.I).Take(10))
                Console.WriteLine($"  ({x.r % RegionsX},{x.r / RegionsX})  {x.pop,8:F0} {x.G,9:F0} {x.A,9:F0} {x.I,9:F3} {x.rel,10:F3} {x.D,+10:F3} {x.E,7:F3} {(x.I > 0 ? x.G / x.I / P.DayLen : double.PositiveInfinity),10:F1}"
                    + $"              {x.F,8:F0} {x.FI,9:F3} {x.FD,10:F3}");
            var occ = rows.Where(x => x.I > 0.01).ToList();
            if (occ.Count > 0)
            {
                var times = occ.Select(x => x.G / x.I / P.DayLen).OrderBy(v => v).ToList();
                Console.WriteLine($"  regions with uptake > 0.01/tick: {occ.Count} of {Regions}; days to empty the air by uptake alone: min {times[0]:F1}, median {times[times.Count / 2]:F1}, max {times[^1]:F1}");
            }
        }
    }
}
