using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Primordium.Model2;

namespace Primordium;

// Life model 2 on tiny worlds (docs/DESIGN-LIFE-MODEL-2.md §12.4, §14):
//   --model2-compile [--seeds 1-3] [--effort N] [--json]   compile the seeded cells for each seed, show what they fold into
//   --model2-demo [--seed 1] [--ticks N] [--effort N] [--k 1,2,3,perf] [--n N]
//       K1  phototrophs (F-1) on a lit flat world with their letters as litter: they live and divide; atoms exact,
//           energy within tolerance, every pool equal to what its cell says it is made of
//       K2  chemotactic heterotrophs (E-1) and their receptor knockout (E-1Δ) on a gradient of charged food:
//           displacement up the gradient
//       K3  E-1/F-1 and model-1 bodies in one world: both live, model 1 tears model 2 apart, model 2 takes in
//           what model 1 leaves; atoms exact, energy within tolerance
//       perf µs per body per tick for model 2 against model 1 on ~2000 bodies (run with DOTNET_PROCESSOR_COUNT=1)
public sealed partial class World
{
    static string Arg2(string[] args, string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
    static readonly CultureInfo Inv2 = CultureInfo.InvariantCulture;
    static double CarrierLitter = 6;   // ground carrier lying in every cell of the scenario worlds (--carrier)

    public static void Model2Compile(string[] args)
    {
        int effort = int.Parse(Arg2(args, "--effort", "6000"));
        foreach (int seed in Batch.ParseSeeds(Arg2(args, "--seeds", "1")))
        {
            var sw = Stopwatch.StartNew();
            var set = Seeds.Compile(new Chemistry(seed), effort);
            Console.WriteLine($"=== seed {seed} ({sw.ElapsedMilliseconds} ms)");
            Console.WriteLine(set.Report);
            if (Array.IndexOf(args, "--json") >= 0)
                foreach (var d in new[] { set.PhototrophDesign, set.HeterotrophDesign, set.KnockoutDesign }) Console.WriteLine(d.ToJson());
        }
    }

    public static void Model2Demo(string[] args)
    {
        int seed = int.Parse(Arg2(args, "--seed", "1"));
        var ks = Arg2(args, "--k", "1,2,3,perf").Split(',');
        CarrierLitter = double.Parse(Arg2(args, "--carrier", "16"), Inv2);
        Generated = Array.IndexOf(args, "--generated") >= 0;
        BandY = int.Parse(Arg2(args, "--band-y", Generated ? "-1" : (TinySide * 18 / 64).ToString(Inv2)));
        int law = P.MatterEnergy;
        P.MatterEnergy = 1;
        // The light chamber (unless --open, or a law named with --set): a bright sun (PhotonK 0.6) on short days (DayLen
        // 400) without seasons (Tilt 0) — the band at row 18 of 64 stays at 15–35 °C; without it the flat bedrock at the
        // equator warms to 45 °C and the seasons move the hot band (both models lose bodies to heat there).
        var keep = (P.PhotonK, P.DayLen, P.Tilt, P.MotorDrag);
        bool Named(string n) => args.Any(x => x.StartsWith(n + "=", StringComparison.OrdinalIgnoreCase));
        if (Array.IndexOf(args, "--open") < 0)
        {
            if (!Named("PhotonK")) P.PhotonK = 0.6f;
            if (!Named("DayLen")) P.DayLen = 400;
            if (!Named("Tilt")) P.Tilt = 0;
            if (!Named("MotorDrag")) P.MotorDrag = 1;   // cells this small swim in a viscous world (low Reynolds number)
            Console.WriteLine(string.Create(Inv2, $"light chamber: PhotonK {P.PhotonK}, DayLen {P.DayLen}, Tilt {P.Tilt}, MotorDrag {P.MotorDrag}; carrier litter {CarrierLitter}, band at row {BandY}"));
        }
        try
        {
            Seeds.ReceptorPrefer = int.Parse(Arg2(args, "--receptor", "1"));
            Seeds.ReceptorCount = int.Parse(Arg2(args, "--receptors", "2"));
            var set = Seeds.For(new World(TinySettings(seed)).Chem, int.Parse(Arg2(args, "--effort", "6000")));
            CarrierOf = set.Carrier;
            Console.WriteLine(set.Report);
            if (ks.Contains("1")) K1(args, seed, set);
            if (ks.Contains("2")) K2(args, seed, set);
            if (ks.Contains("3")) K3(args, seed, set);
            if (ks.Contains("perf")) Model2Perf(args, seed, set);
        }
        finally { P.MatterEnergy = law; (P.PhotonK, P.DayLen, P.Tilt, P.MotorDrag) = keep; }
    }

    // A flat lit world with the seed cells' letters lying as litter (`perCell` molecules of each in every cell).
    static bool Generated;   // --generated: a generated tiny world (terrain, water, litter of its seed) instead of a flat one
    static World Model2World(int seed, double perCell, IEnumerable<int> species, int life = 0)
    {
        World w;
        if (Generated) w = new World(TinySettings(seed, 0, false, false, TinySide, TinySide, TinyLevels, life));
        else if (life == 0) w = Blank(TinySide, TinySide, TinyLevels, seed);
        else { w = new World(TinySettings(seed, 0, false, false, TinySide, TinySide, TinyLevels, life)); w.Flatten(); }
        foreach (int s in species.Distinct())
            for (int c = 0; c < w.N; c++) w.C[s][c] += Qty.Of(perCell);
        return w;
    }

    // What the seed cells are made of: every letter of both strands of their genomes (and of their proteins), the carrier.
    static int[] SeedLetters(SeedSet set) => new[] { set.PhototrophDesign, set.HeterotrophDesign, set.KnockoutDesign, set.PredatorDesign }.Where(d => d != null).SelectMany(d => d.Body.Keys).Select(k => int.Parse(k, Inv2)).Where(s => s % 2 == 0).Append(set.Carrier).Distinct().OrderBy(s => s).ToArray();

    // n bodies in the band of rows around the equator (the light is best there), spread over every longitude.
    static List<Agent> PlantBand(World w, CreatureDesign d, int n, int band = 6)
    {
        var all = new List<Agent>();
        int groups = Math.Max(1, Math.Min(n, 8));
        for (int k = 0; k < groups; k++)
        {
            int m = n / groups + (k < n % groups ? 1 : 0);
            if (m > 0) all.AddRange(Plant(w, d, m, (k * w.W) / groups + w.W / (2 * groups), BandY >= 0 ? BandY : w.H / 2, band));
        }
        return all;
    }
    static int BandY = -1;   // --band-y: the row the band is centred on (−1: the equator)

    // Mean ground temperature by row now, for reports.
    static string TempRows(World w) =>
        string.Join(" ", Enumerable.Range(0, w.H).Where(y => y % 8 == 0 || y == w.H / 2).Select(y => string.Create(Inv2, $"y{y}:{Enumerable.Range(0, w.W).Average(x => w.Temp[y * w.W + x]):0.0}")));

    // Mean light by row (the sun's average over a day), for reports.
    static string LightRows(World w)
    {
        var sum = new double[w.H];
        int day = Math.Max(1, P.DayLen);
        for (int t = 0; t < day; t += day / 24)
        {
            for (int k = 0; k < day / 24; k++) w.Step();
            for (int y = 0; y < w.H; y++) for (int x = 0; x < w.W; x++) sum[y] += w.Light[y * w.W + x] / (w.W * 24.0);
        }
        return string.Join(" ", Enumerable.Range(0, w.H).Where(y => y % 8 == 0 || y == w.H / 2).Select(y => $"y{y}:{sum[y]:0.00}"));
    }

    // The element budget less what the interior (vents) and the hand brought in since `start` was read
    // (vents may open in a flat world too): what must equal `start` exactly.
    static double[] Inputs(World w, double[] start)
    {
        var b = w.ElementBudget();
        for (int e = 0; e < b.Length; e++) b[e] -= w.InteriorInput[e] + w.HandInput[e];
        return b;
    }

    static List<Agent> Plant(World w, CreatureDesign d, int n, int x, int y, int radius)
    {
        var r = w.SpawnDesign(d, x, y, new SpawnOptions { Count = n, Radius = radius, Matter = MatterSource.Import, Energy = EnergySource.Import });
        if (!r.Ok) throw new InvalidOperationException($"{d.Name}: {r}");
        return r.Agents;
    }

    // Every model-2 body's pool equals what its cell is made of (genome duplexes, the replica so far, every
    // protein copy; bonds and excited residues).
    static void PoolCheck(World w, string stage)
    {
        long rq = ResidueRaw, link = LinkRaw;
        foreach (var a in w.Agents)
        {
            if (a.Dead || a.Model != LifeModels.Chem || a.ModelState is not Cell st) continue;
            var want = new long[Chem2.L];
            long bond = 0;
            if (st.Genomes >= 1) for (int f = 0; f < Chem2.L; f++) { want[f] += st.GenomeLetters[f]; bond += st.GenomeLetters[f] * link; }
            for (int f = 0; f < Chem2.L; f++) { want[f] += st.ReplicaLetters[f]; bond += st.ReplicaLetters[f] * link; }
            foreach (var s in st.Slots)
            {
                var t = s.Type ?? ProteinType.Of(Chem2.Of(w.Chem), s.Seq);
                for (int f = 0; f < Chem2.L; f++) want[f] += (long)s.Total * t.Letters[f];
                bond += (long)s.Total * t.Len * link;
                for (int k = 0; k < 4; k++) for (int j = 0; j < t.Sites.Length; j++) if ((k >> j & 1) != 0) bond += (long)s.N[k] * t.SiteGap[j] * rq;
            }
            var poly = a.Poly ?? new Polymers();
            for (int f = 0; f < Chem2.L; f++) Require(poly.M[2 * f].Raw == want[f] * rq, $"{stage}: body {a.Id} pool of letter {f}: {poly.M[2 * f].Raw / (double)rq} residues, its cell says {want[f]}");
            for (int s = 1; s < Chemistry.S; s += 2) Require(poly.M[s].Raw == 0, $"{stage}: body {a.Id} pool holds an excited species");
            Require(poly.Bond == bond, $"{stage}: body {a.Id} bond energy {poly.Bond / Qty.One:R}, its cell says {bond / Qty.One:R}");
        }
    }

    sealed class Census2
    {
        public int Bodies, Genomes0, Copies, Divisions, Thrusts, Tumbles, Photons, Synth, Genotypes;
        public double Charge, Inv, Mass, Membrane, Ground, Excited, Poly;
        public override string ToString() => string.Create(Inv2, $"{Bodies} cells ({Genotypes} genotypes, no genome {Genomes0}), copies {Copies / (double)Math.Max(1, Bodies):0.0}/cell, charge {Charge / Math.Max(1, Bodies):0.0}, molecules {Inv / Math.Max(1, Bodies):0.0} (carrier {Ground / Math.Max(1, Bodies):0.00} + {Excited / Math.Max(1, Bodies):0.00} charged; polymer {Poly / Math.Max(1, Bodies):0.00}), mass {Mass / Math.Max(1, Bodies):0.0}, membrane excess {Membrane / Math.Max(1, Bodies):0.00}; divisions {Divisions}, photons {Photons}, syntheses {Synth}, pushes {Thrusts}, tumbles {Tumbles}");
    }

    static int CarrierOf = -1;   // the seeded cells' ground carrier (for the census)
    static Census2 Count2(World w, Func<Agent, bool> which = null)
    {
        var c = new Census2();
        var c2 = Chem2.Of(w.Chem);
        var hashes = new HashSet<ulong>();
        foreach (var a in w.Agents)
        {
            if (a.Dead || a.Model != LifeModels.Chem || a.ModelState is not Cell st || (which != null && !which(a))) continue;
            c.Bodies++;
            if (hashes.Add(a.Hash)) c.Genotypes++;
            if (st.Genomes == 0) c.Genomes0++;
            foreach (var s in st.Slots) c.Copies += s.Total;
            c.Charge += w.Charge(a); c.Inv += a.InvTotal; c.Mass += a.Mass;
            if (CarrierOf >= 0) { c.Ground += HaveRaw(a, CarrierOf) / (double)Qty.One; c.Excited += HaveRaw(a, CarrierOf + 1) / (double)Qty.One; }
            if (a.Poly != null) for (int s = 0; s < Chemistry.S; s++) c.Poly += a.Poly.M[s].D;
            c.Membrane += ChemModel.MembraneExcess(a, st, c2);
            c.Divisions += (int)st.Divisions; c.Thrusts += (int)st.Thrusts; c.Tumbles += (int)st.Tumbles; c.Photons += (int)st.Photons; c.Synth += (int)st.Synth;
        }
        return c;
    }

    // Where model 2's energy goes, per cell and tick over the living cells (their ledgers over their ages), with
    // the body's own upkeep and harm, its temperature and the ground's.
    static string LedgerLine(World w, Func<Agent, bool> which = null)
    {
        var sum = new double[Cell.LedgerN];
        double age = 0, upkeep = 0, harm = 0, tb = 0, ground = 0;
        long done = 0, errors = 0;
        var stalls = new long[4];
        int n = 0;
        foreach (var a in w.Agents)
        {
            if (a.Dead || a.ModelState is not Cell st || (which != null && !which(a))) continue;
            for (int k = 0; k < Cell.LedgerN; k++) sum[k] += st.Ledger[k];
            for (int k = 0; k < 4; k++) stalls[k] += st.Stalls[k];
            done += st.CopyDone; errors += st.CopyErrors;
            age += Math.Max(1, a.Age); upkeep += a.LifeUpkeep; harm += a.LifeHarm; tb += a.Tb; ground += w.Temp[a.Y * w.W + a.X]; n++;
        }
        if (n == 0) return "ledger: no cells";
        var parts = Enumerable.Range(0, Cell.LedgerN).Where(k => sum[k] != 0).Select(k => Cell.LedgerNames[k] + " " + (sum[k] / age).ToString("0.0000", Inv2));
        return "ledger per cell-tick: " + string.Join(", ", parts) + string.Create(Inv2, $"; upkeep {upkeep / age:0.0000}, harm {harm / age:0.0000}; Tb {tb / n:0.0} C on ground {ground / n:0.0} C; stalls synth monomer/charge {stalls[0]}/{stalls[1]}, copy {stalls[2]}/{stalls[3]}; copied {done} residues, error {(done > 0 ? errors / (double)done : 0):0.00000}");
    }

    // ---- K1: the phototroph lives and divides, the books balance ----

    static void K1(string[] args, int seed, SeedSet set)
    {
        int ticks = int.Parse(Arg2(args, "--ticks", "20000")), n = int.Parse(Arg2(args, "--n", "24"));
        double litter = double.Parse(Arg2(args, "--litter", "3"), Inv2);
        var w = Model2World(seed, litter, SeedLetters(set));
        for (int cc = 0; cc < w.N; cc++) w.C[set.Carrier][cc] += Qty.Of(CarrierLitter);   // the carrier is plentiful (it holds the charge)
        w.TrackHeat = true;
        if (Array.IndexOf(args, "--light") >= 0) Console.WriteLine("mean light by row over a day: " + LightRows(Model2World(seed, litter, SeedLetters(set))));
        var planted = PlantBand(w, Array.IndexOf(args, "--leaf") >= 0 ? CreatureExamples.All[0] : set.PhototrophDesign, n);
        var atoms0 = Inputs(w, null);
        var e0 = w.AuditEnergy();
        int births0 = w.Births;
        Console.WriteLine($"K1 seed {seed}: {planted.Count} F-1 planted ({set.PhototrophDesign.Body.Values.Sum()} molecules, charge {set.PhototrophDesign.Energy}); tick 0: {Count2(w)}");
        var sw = Stopwatch.StartNew();
        int every = Math.Max(1, ticks / 10);
        var watch = planted[0];
        bool trace = Array.IndexOf(args, "--trace") >= 0, dumped = false;
        var ring = new string[12];
        for (int t = 1; t <= ticks; t++)
        {
            w.Step();
            if (trace && watch.Dead && !dumped) { dumped = true; Console.WriteLine($"    #{watch.Id} died at {t}, cause {watch.Cause}:"); foreach (var line in ring) if (line != null) Console.WriteLine(line); }
            if (trace && (t % 25 == 0 || t > 0) && !watch.Dead && watch.ModelState is Cell ws)
                ring[t % ring.Length] = (string.Create(Inv2, $"    #{watch.Id} t{t}: photo-in {watch.GainPhoto:0.0} upkeep {watch.LifeUpkeep:0.0} charge {w.Charge(watch):0.00} cap {w.Capacity(watch):0.0} mol {watch.InvTotal} mass {watch.Mass:0.0} Tb {watch.Tb:0.0} light {w.LightAt(watch):0.00} photon {w.Photon[watch.Y * w.W + watch.X]:0.00} caught {ws.Photons} have {string.Join(",", Enumerable.Range(0, 32).Where(s => HaveRaw(watch, s) > 0).Select(s => s + ":" + (HaveRaw(watch, s) / (double)Qty.One).ToString("0.00", Inv2)))} loose {string.Join(",", SeedLetters(set).Select(s => s + ":" + w.ConcentrationOutside(watch, watch.Y * w.W + watch.X, s).ToString("0.000", Inv2)))} due {watch.Due:0.00} copies {string.Join("/", ws.Slots.Select(s => s.Total))} synth {ws.Synth} decayed {ws.Decayed} genomes {ws.Genomes} fork {ws.Fork} membrane {ChemModel.MembraneExcess(watch, ws, Chem2.Of(w.Chem)):0.00} xp {ws.DiagXp:0.000} xall {ws.DiagXall:0.000} tau {ws.DiagTau:0.000} acc {string.Join("/", ws.Acc.Select(x => x.ToString("0.00", Inv2)))}"));
            if (trace && t % 50 == 0 && ring[t % ring.Length] != null && !watch.Dead) Console.WriteLine(ring[t % ring.Length]);
            if (t % every == 0 || t == 50)
            {
                PoolCheck(w, $"K1 tick {t}");
                BudgetEqual(atoms0, Inputs(w, atoms0), $"K1 tick {t}: atoms", 1e-6);
                var e = w.AuditEnergy();
                Console.WriteLine($"  tick {t}: {Count2(w)}; births {w.Births - births0}; energy drift {EnergyAudit.Drift(e0, e):F4} (tol {EnergyAudit.Tolerance(e0, e):F2})");
                Console.WriteLine("    " + LedgerLine(w));
                {
                    // What lies where the cells are (molecules on their floors) and over the whole world, by species.
                    var cells2 = w.Agents.Where(a => !a.Dead && a.ModelState is Cell).ToList();
                    if (cells2.Count > 0)
                        Console.WriteLine("    loose at the cells / world mean: " + string.Join(" ", SeedLetters(set).Select(s => string.Create(Inv2, $"{s}:{cells2.Average(a => w.ConcentrationOutside(a, a.Y * w.W + a.X, s)) * P.VoxelSpace / w.MeanMoleculeVolume:0.00}/{Enumerable.Range(0, w.N).Average(c => w.C[s][c].D):0.00} in {cells2.Average(a => (HaveRaw(a, s) + HaveRaw(a, s + 1)) / (double)Qty.One):0.00}"))) + "; stalls by letter " + string.Join(" ", Enumerable.Range(0, Chem2.L).Where(f => cells2.Sum(a => ((Cell)a.ModelState).StallLetter[f]) > 0).Select(f => $"{f:x}:{cells2.Sum(a => ((Cell)a.ModelState).StallLetter[f])}")));
                }
                if (Array.IndexOf(args, "--temps") >= 0) Console.WriteLine("    ground by row: " + TempRows(w));
            }
        }
        string energy = EnergyWorldCheck(w, e0, "K1");
        var c = Count2(w);
        Console.WriteLine($"   deaths: starved {w.DeathsStarve}, broken {w.DeathsBroken}, climate {w.DeathsClimate}, killed {w.DeathsKilled}");
        Console.WriteLine($"K1 {(c.Bodies > 0 && w.Births > births0 ? "PASS" : "FAIL")}: {ticks} ticks in {sw.ElapsedMilliseconds} ms, {c.Bodies} F-1 alive (planted {planted.Count}), {w.Births - births0} births; atoms exact; {energy}");
    }

    // ---- K2: chemotaxis up a gradient of charged food, against the receptor knockout ----

    static void K2(string[] args, int seed, SeedSet set)
    {
        int ticks = int.Parse(Arg2(args, "--ticks2", "3000")), n = int.Parse(Arg2(args, "--n2", "32")), reps = int.Parse(Arg2(args, "--reps", "1"));
        double top = double.Parse(Arg2(args, "--food", "20"), Inv2);
        // Replicates: the same chemistry and scene, life's random streams perturbed (WorldSettings.LifeSeed = rep).
        var rows = new List<(double eAll, double kAll, double eShare, double kShare, double eDy, double kDy, double eCi, double kCi)>();
        for (int rep = 0; rep < reps; rep++)
        {
            var r = new List<(double dy, double ci, double all, double share)>();
            foreach (var (label, design) in new[] { ("E-1", set.HeterotrophDesign), ("E-1Δ", set.KnockoutDesign) })
            {
                var w = Model2World(seed, 3, SeedLetters(set), rep);
                w.TrackHeat = true;
                for (int c = 0; c < w.N; c++)
                {
                    w.C[set.Carrier][c] += Qty.Of(CarrierLitter);
                    w.C[set.Food][c] += Qty.Of(top * (c / w.W) / (w.H - 1));   // charged food rising along y
                }
                int y0 = w.H / 2;
                var bodies = new List<Agent>();
                for (int k = 0; k < n; k++) bodies.AddRange(Plant(w, design, 1, (k * w.W) / n, y0, 0));
                var atoms0 = Inputs(w, null);
                var e0 = w.AuditEnergy();
                var lastY = new Dictionary<long, int>();
                long up = 0, down = 0;
                int births0 = w.Births;
                for (int t = 1; t <= ticks; t++)
                {
                    w.Step();
                    foreach (var a in w.Agents)
                    {
                        if (a.Dead || a.Model != LifeModels.Chem) continue;
                        if (lastY.TryGetValue(a.Id, out int ly)) { if (a.Y > ly) up++; else if (a.Y < ly) down++; }
                        lastY[a.Id] = a.Y;
                    }
                }
                BudgetEqual(atoms0, Inputs(w, atoms0), $"K2 {label}: atoms", 1e-6);
                EnergyWorldCheck(w, e0, $"K2 {label}");
                PoolCheck(w, $"K2 {label}");
                var alive = w.Agents.Where(a => !a.Dead && a.Model == LifeModels.Chem).ToList();
                double dy = bodies.Average(a => a.Y - y0);   // founders, alive or where they died
                double all = alive.Count > 0 ? alive.Average(a => a.Y - y0) : 0, share = alive.Count > 0 ? alive.Count(a => a.Y > y0) / (double)alive.Count : 0;
                double ci = up + down > 0 ? (up - down) / (double)(up + down) : 0;
                r.Add((dy, ci, all, share));
                Console.WriteLine(string.Create(Inv2, $"K2 rep {rep} {label}: {ticks} ticks on a food gradient 0→{top:0} along y: all living cells {all:+0.0;-0.0} rows from the start (mean), {100 * share:0}% on the food side; founders {dy:+0.0;-0.0} rows, chemotaxis index {ci:+0.000;-0.000} ({up} steps up, {down} down), {alive.Count} alive, {w.Births - births0} births; {Count2(w)}"));
            }
            rows.Add((r[0].all, r[1].all, r[0].share, r[1].share, r[0].dy, r[1].dy, r[0].ci, r[1].ci));
        }
        // Paired over the replicates: E-1 against its knockout in the same world (mean row of the living cells).
        var d = rows.Select(x => x.eAll - x.kAll).ToArray();
        double mean = d.Average(), sd = d.Length > 1 ? Math.Sqrt(d.Sum(x => (x - mean) * (x - mean)) / (d.Length - 1)) : 0;
        double tStat = sd > 0 ? mean / (sd / Math.Sqrt(d.Length)) : 0;
        int wins = d.Count(x => x > 0);
        double sign = 0;   // one-sided sign test: P(at least `wins` of n by chance)
        for (int k = wins; k <= d.Length; k++) sign += Binomial(d.Length, k) / Math.Pow(2, d.Length);
        bool pass = d.Length >= 5 ? wins >= d.Length - 1 && tStat > 2 : mean > 1 && rows.Average(x => x.eCi - x.kCi) > 0;
        Console.WriteLine(string.Create(Inv2, $"K2 {(pass ? "PASS" : "FAIL")}: {d.Length} replicates; living E-1 {rows.Average(x => x.eAll):+0.0;-0.0} rows ({100 * rows.Average(x => x.eShare):0}% on the food side), knockout {rows.Average(x => x.kAll):+0.0;-0.0} ({100 * rows.Average(x => x.kShare):0}%); E-1 ahead in {wins} of {d.Length} (sign test p {sign:0.000}), mean difference {mean:+0.00;-0.00} ± {sd:0.00} rows (paired t {tStat:0.00}); founders {rows.Average(x => x.eDy):+0.0;-0.0} vs {rows.Average(x => x.kDy):+0.0;-0.0}, chemotaxis index {rows.Average(x => x.eCi):+0.000;-0.000} vs {rows.Average(x => x.kCi):+0.000;-0.000}; atoms exact, energy in tolerance"));
    }

    static double Binomial(int n, int k) { double r = 1; for (int i = 1; i <= k; i++) r = r * (n - k + i) / i; return r; }

    // ---- K3: model 2 and model 1 in one world ----

    // A model-1 hunter: strikes whoever shares its cell, drinks what lies around, divides when rich.
    static CreatureDesign Hunter() => new()
    {
        Name = "Hunter-1", Description = "Model 1: strikes whoever shares its cell (any model), drinks, divides when rich.",
        Body = new() { ["0"] = 10, ["any"] = 4 }, Energy = 40,
        Genome = "label 0\nlit 0\npick\njz 1\nlit 40\nattack\nlabel 1\ndrink\nenergy\nlit 80\nlt\njnz 0\npush 0\nlit 4\ndivide\njmp 0\n",
    };

    // K3 on the flat scene (default) or, with --world WxHxL, on a generated world of that size with its own life (model 1,
    // P.InitialPop by area) and the seeded model-2 cells planted over it: what lives after --ticks3, who killed whom
    // (strikes of model 1, contact hydrolysis of the predator P-1), atoms and energy.
    static void K3(string[] args, int seed, SeedSet set)
    {
        int ticks = int.Parse(Arg2(args, "--ticks3", "10000"));
        string size = Arg2(args, "--world", "");
        World w;
        var m2 = new List<Agent>();
        var m1 = new List<Agent>();
        var leaf = CreatureExamples.All[0];
        if (size.Length == 0)
        {
            w = Model2World(seed, 3, SeedLetters(set));
            for (int c = 0; c < w.N; c++) { w.C[set.Carrier][c] += Qty.Of(CarrierLitter); w.C[set.Food][c] += Qty.Of(4); w.C[0][c] += Qty.Of(4); if (set.Prey >= 0) w.C[set.Prey][c] += Qty.Of(4); }
            m2.AddRange(Plant(w, set.PhototrophDesign, 30, w.W / 2, w.H / 2, 28));
            m2.AddRange(Plant(w, set.HeterotrophDesign, 30, w.W / 2, w.H / 2, 28));
            if (set.PredatorDesign != null) m2.AddRange(Plant(w, set.PredatorDesign, 30, w.W / 2, w.H / 2, 28));
            m1.AddRange(Plant(w, leaf, 40, w.W / 2, w.H / 2, 28));
            m1.AddRange(Plant(w, Hunter(), 30, w.W / 2, w.H / 2, 28));
        }
        else
        {
            var dims = size.Split('x').Select(int.Parse).ToArray();
            w = new World(TinySettings(seed, -1, false, !FlareLaw, dims[0], dims[1], dims[2]));
            m1.AddRange(w.Agents.Where(a => !a.Dead));
            // model 2 in groups over the land, by rows of every latitude (each group where it lands)
            var rng = new SimRng(seed * 31 + 7);
            int groups = int.Parse(Arg2(args, "--groups", "24"));
            foreach (var d in new[] { set.PhototrophDesign, set.HeterotrophDesign, set.PredatorDesign }.Where(d => d != null))
                for (int k = 0; k < groups / 3; k++)
                {
                    var r = w.SpawnDesign(d, rng.Next(w.W), w.H / 6 + rng.Next(2 * w.H / 3), new SpawnOptions { Count = 5, Radius = 3, Matter = MatterSource.Import, Energy = EnergySource.Import });
                    if (r.Ok) m2.AddRange(r.Agents);
                }
        }
        w.TrackHeat = true;
        var atoms0 = Inputs(w, null);
        var e0 = w.AuditEnergy();
        var seen = new HashSet<Agent>(m2.Concat(m1));
        var killed = new int[3];   // by model of the victim
        var counted = new HashSet<long>();
        Console.WriteLine($"K3 seed {seed} {(size.Length == 0 ? "flat scene" : "world " + size)}: planted {m2.Count} model-2 (F-1, E-1{(set.PredatorDesign != null ? ", P-1 hydrolysing " + w.Chem.NameEn[set.Prey] : "")}), model 1: {m1.Count}");
        int peak2 = 0;
        for (int t = 1; t <= ticks; t++)
        {
            w.Step();
            foreach (var a in w.Agents) seen.Add(a);
            foreach (var a in seen) if (a.Dead && a.Cause == CauseKilled && counted.Add(a.Id)) killed[a.Model == LifeModels.Chem ? 2 : 1]++;
            seen.RemoveWhere(a => a.Dead);
            if (t % 100 == 0) peak2 = Math.Max(peak2, w.Agents.Count(a => !a.Dead && a.Model == LifeModels.Chem));
            if (t % Math.Max(1, ticks / 10) == 0)
            {
                PoolCheck(w, $"K3 tick {t}");
                BudgetEqual(atoms0, Inputs(w, atoms0), $"K3 tick {t}: atoms", 1e-6);
                int n1 = w.Agents.Count(a => !a.Dead && a.Model == LifeModels.Vm), n2 = w.Agents.Count(a => !a.Dead && a.Model == LifeModels.Chem);
                int p1 = w.Agents.Count(a => !a.Dead && a.Model == LifeModels.Chem && set.Predator != null && a.G.Length * 2 == set.Predator.Length && a.Hash == PredatorHash(set));
                long dig = w.Agents.Where(a => !a.Dead && a.ModelState is Cell).Sum(a => ((Cell)a.ModelState).Digested);
                Console.WriteLine($"  tick {t}: model 1 {n1}, model 2 {n2} (P-1 genotype {p1}; {Count2(w)}); killed: model-1 bodies {killed[1]} (by hydrolysis {w.DigestKills}), model-2 bodies {killed[2]}; splits by living hydrolysers {dig}");
            }
        }
        string energy = EnergyWorldCheck(w, e0, "K3");
        int a1 = w.Agents.Count(a => !a.Dead && a.Model == LifeModels.Vm), a2 = w.Agents.Count(a => !a.Dead && a.Model == LifeModels.Chem);
        Console.WriteLine($"K3 {(a1 > 0 && a2 > 0 ? "PASS" : "FAIL")}: after {ticks} ticks model 1 {a1}, model 2 {a2} alive (model 2 at most {peak2}); model-2 bodies killed {killed[2]}, model-1 bodies killed {killed[1]}, of them by model-2 hydrolysis {w.DigestKills}; atoms exact; {energy}");
    }

    static ulong PredatorHash(SeedSet set) => set.Predator == null ? 0 : Hash64(GeneTable.Pack(set.Predator));
    static ulong Hash64(byte[] g) { var a = new Agent(0, 0, 0, g, new byte[g.Length], LifeModels.Chem); return a.Hash; }

    // ---- speed ----

    static void Model2Perf(string[] args, int seed, SeedSet set)
    {
        int n = int.Parse(Arg2(args, "--nperf", "2000")), ticks = int.Parse(Arg2(args, "--tperf", "300"));
        foreach (var (label, design) in new[] { ("model 2 (F-1)", set.PhototrophDesign), ("model 2 (E-1)", set.HeterotrophDesign), ("model 1 (Leaf)", CreatureExamples.All[0]) })
        {
            var w = Model2World(seed, 3, SeedLetters(set));
            for (int c = 0; c < w.N; c++) { w.C[set.Carrier][c] += Qty.Of(CarrierLitter); w.C[set.Food][c] += Qty.Of(4); w.C[0][c] += Qty.Of(4); }
            var bodies = Plant(w, design, n, w.W / 2, w.H / 2, 40);
            for (int t = 0; t < 20; t++) w.Step();   // assembly and the first slow steps
            double busy0 = w.AgentBusy;
            ChemModel.Profile = Array.IndexOf(args, "--prof") >= 0; Array.Clear(ChemModel.Prof);
            long bodyTicks = 0;
            var sw = Stopwatch.StartNew();
            for (int t = 0; t < ticks; t++) { bodyTicks += w.Agents.Count(a => !a.Dead); w.Step(); }
            sw.Stop();
            double us = (w.AgentBusy - busy0) * 1000 / Math.Max(1, bodyTicks);
            Console.WriteLine(string.Create(Inv2, $"perf {label}: {bodies.Count} planted, {bodyTicks / (double)ticks:0} bodies on average over {ticks} ticks: agent phase {us:0.00} µs per body per tick, whole step {sw.Elapsed.TotalMilliseconds / ticks:0.00} ms per tick (processors {Environment.ProcessorCount})"));
            if (ChemModel.Profile) Console.WriteLine("   model 2 by part, µs per body per tick: " + string.Join(", ", Enumerable.Range(0, 7).Select(k => ChemModel.ProfNames[k] + " " + (ChemModel.Prof[k] * 1e6 / Stopwatch.Frequency / Math.Max(1, bodyTicks)).ToString("0.00", Inv2))));
            ChemModel.Profile = false;
        }
    }
}
