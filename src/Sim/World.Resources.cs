using System;
using System.Linq;
using System.Threading;

namespace Primordium;

// Resources: depletion, replenishment and locality (ROADMAP 4).
//
// Observation: the resource probe (World.ResProbe, null = off) books, region by region (32×32 cells),
// where the air's gas and the loose food (every other loose molecule lying on the surface) go each
// tick: what bodies take in, what moves between regions by diffusion, what the environment adds or
// removes (decay, rain, weathering) and what bodies give back (expel, death). Only counts: the world
// never reads it, and with it off nothing is computed.
public sealed partial class World
{
    public const int RegionSide = 32, RegionsX = W / RegionSide, RegionsY = H / RegionSide, Regions = RegionsX * RegionsY;
    public static int RegionOf(int cell) => cell / W / RegionSide * RegionsX + cell % W / RegionSide;

    public sealed class ResourceProbe
    {
        // Phases of the tick a change of the pools is booked under.
        public const int PDiffusion = 0, PEnv = 1, PBio = 2, POther = 3, Phases = 4;
        public static readonly string[] PhaseNames = { "diffusion", "env", "bio", "other" };
        // Net change of the region's surface gas / loose food per phase, raw Qty summed over ticks.
        public readonly long[,] Gas = new long[Phases, Regions], Food = new long[Phases, Regions];
        public readonly long[] GasIntake = new long[Regions], FoodIntake = new long[Regions];   // gross uptake by bodies (raw Qty)
        public readonly long[] CaveGasIntake = new long[Regions];                              // ... of it drawn from under a roof
        public readonly long[] Rain = new long[Regions];                                       // gas adsorbed by rain (raw Qty)
        public readonly double[] Pop = new double[Regions];                                    // body-ticks
        public readonly long[,] Species = new long[Phases, Chemistry.S];                       // net change per phase of each loose species, planet-wide
        public readonly long[] SpeciesIntake = new long[Chemistry.S], SpeciesExpel = new long[Chemistry.S];   // gross uptake / expel by species (surface and caves)
        public double DiffusionGross;                                                         // Σ|Δ| of the gas per cell by diffusion / 2 (raw Qty)
        public long Ticks;
        internal readonly long[] gasMark = new long[Regions], foodMark = new long[Regions], speciesMark = new long[Chemistry.S];
        internal bool marked;
    }
    public ResourceProbe ResProbe;

    // The share of a column's air gas a body at level z under a roof reaches: e^(−roof/CaveGasK)
    // (P.CaveGasK > 0; the roof as World.Cave counts it). Simple model: no pool of its own per cavity,
    // the cave draws on the column's air, so the surface and the cave below compete for one stock.
    public Qty CaveGas(int cell, int z)
    {
        int roof = Roof(cell, z);
        Qty air = C[Chem.Gas][cell];
        return roof == 0 ? air : air * MathF.Exp(-roof / P.CaveGasK);
    }

    // Sums of the surface gas and the loose food per region (raw Qty).
    void RegionSums(long[] gas, long[] food, long[] species)
    {
        Array.Clear(gas); Array.Clear(food);
        for (int s = 0; s < Chemistry.S; s++)
        {
            var c = C[s];
            var into = s == Chem.Gas ? gas : food;
            long t = 0;
            for (int i = 0; i < N; i++) { into[RegionOf(i)] += c[i].Raw; t += c[i].Raw; }
            species[s] = t;
        }
    }

    readonly long[] resGas = new long[Regions], resFood = new long[Regions], resSpecies = new long[Chemistry.S];

    // Books the change of the pools since the last mark under `phase` (the first mark of a tick only sets it).
    void ResMark(int phase)
    {
        var p = ResProbe;
        if (p == null) return;
        RegionSums(resGas, resFood, resSpecies);
        if (p.marked && phase >= 0)
        {
            for (int r = 0; r < Regions; r++)
            {
                p.Gas[phase, r] += resGas[r] - p.gasMark[r];
                p.Food[phase, r] += resFood[r] - p.foodMark[r];
            }
            for (int s = 0; s < Chemistry.S; s++) p.Species[phase, s] += resSpecies[s] - p.speciesMark[s];
        }
        Array.Copy(resGas, p.gasMark, Regions);
        Array.Copy(resFood, p.foodMark, Regions);
        Array.Copy(resSpecies, p.speciesMark, Chemistry.S);
        p.marked = true;
    }

    void ResTick()
    {
        var p = ResProbe;
        if (p == null) return;
        p.Ticks++;
        foreach (var a in Agents) if (!a.Dead) p.Pop[RegionOf(a.Y * W + a.X)]++;
    }

    void ResExpel(int s)
    {
        var p = ResProbe;
        if (p != null) Interlocked.Add(ref p.SpeciesExpel[s], 1L << Qty.Bits);
    }

    void ResIntake(int cell, int s, Qty m, bool cave)
    {
        var p = ResProbe;
        if (p == null) return;
        int r = RegionOf(cell);
        Interlocked.Add(ref p.SpeciesIntake[s], m.Raw);
        if (cave) { if (s == Chem.Gas) Interlocked.Add(ref p.CaveGasIntake[r], m.Raw); return; }   // not the surface pools
        if (s == Chem.Gas) Interlocked.Add(ref p.GasIntake[r], m.Raw);
        else Interlocked.Add(ref p.FoodIntake[r], m.Raw);
    }

    // ---- observation: resources and how bodies spread over them (bench metrics, no effect on the world) ----

    public static readonly string[] ResNames =
    {
        "gas_share", "region_diets", "diet_beta", "region_pop_cv", "gas_air", "food_loose", "gas_cv", "food_cv",
    };

    // gas_share: atoms of bodies in the gas's formula (either state); region_diets: e^H of the diets
    // (World.Diet) inside a 32×32 region with ≥ 5 bodies, weighted by its bodies (the effective number of
    // diets a body meets nearby); diet_beta: H(planet) − mean H(region), nats — how much the regions'
    // diets differ from each other (0: every region the same mix); region_pop_cv: coefficient of variation
    // of the bodies per region; gas_air / food_loose: air gas and loose food per cell, molecules;
    // gas_cv / food_cv: coefficient of variation of those stocks across regions (patches).
    public double[] ResCensus()
    {
        var v = new double[ResNames.Length];
        var diets = new int[Regions, 5];
        var pop = new int[Regions];
        double bodyGas = GasShareOfBodies();
        foreach (var a in Agents)
        {
            if (a.Dead) continue;
            int r = RegionOf(a.Y * W + a.X);
            pop[r]++;
            diets[r, Diet(a)]++;
        }
        static double Entropy(Func<int, double> n, int k, double total)
        {
            double h = 0;
            for (int j = 0; j < k; j++) { double p = n(j) / total; if (p > 0) h -= p * Math.Log(p); }
            return h;
        }
        int all = 0;
        var planet = new double[5];
        for (int r = 0; r < Regions; r++) { all += pop[r]; for (int j = 0; j < 5; j++) planet[j] += diets[r, j]; }
        double hw = 0, ehw = 0; int counted = 0;
        for (int r = 0; r < Regions; r++)
        {
            if (pop[r] < 5) continue;
            int rr = r;
            double h = Entropy(j => diets[rr, j], 5, pop[r]);
            hw += h * pop[r]; ehw += Math.Exp(h) * pop[r]; counted += pop[r];
        }
        v[0] = bodyGas;
        if (counted > 0)
        {
            v[1] = ehw / counted;
            v[2] = Math.Max(0, Entropy(j => planet[j], 5, all) - hw / counted);
        }
        v[3] = Cv(pop.Select(x => (double)x).ToArray());
        var gas = new double[Regions]; var food = new double[Regions];
        var g = C[Chem.Gas];
        for (int i = 0; i < N; i++) gas[RegionOf(i)] += g[i];
        for (int s = 0; s < Chemistry.S; s++)
        {
            if (s == Chem.Gas) continue;
            var c = C[s];
            for (int i = 0; i < N; i++) food[RegionOf(i)] += c[i];
        }
        v[4] = gas.Sum() / N; v[5] = food.Sum() / N;
        v[6] = Cv(gas); v[7] = Cv(food);
        return v;
    }

    // Atoms of bodies in molecules of the gas's formula (either state), of all their atoms.
    public double GasShareOfBodies()
    {
        int g0 = Chem.Gas & ~1;
        double gas = 0, all = 0;
        foreach (var a in Agents)
        {
            if (a.Dead) continue;
            for (int s = 0; s < Chemistry.S; s++)
            {
                double n = (double)a.Inv[s] * Chem.AtomCount(s);
                all += n;
                if ((s & ~1) == g0) gas += n;
            }
        }
        return all > 0 ? gas / all : 0;
    }

    static double Cv(double[] x)
    {
        double m = x.Average();
        if (m <= 0) return 0;
        double var = x.Sum(y => (y - m) * (y - m)) / x.Length;
        return Math.Sqrt(var) / m;
    }
}
