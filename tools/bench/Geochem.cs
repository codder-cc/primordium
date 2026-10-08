using System;
using System.Collections.Generic;
using System.Linq;

namespace Primordium;

// --geochem [--seeds 1-4] [--ticks 3000]: what the ground is made of by depth and what bodies need
// (ROADMAP 3, diagnosis). Observation only.
//   chemistry: every formula — energy of its two states, what splitting releases, bond, hardness grade;
//   strata: element shares of the atoms in rock at 0/3/10/30/60 levels under the top of each column
//     (and the most common molecules there), of the vents' ejecta and of the loose litter;
//   bodies (after --ticks with life): element shares of the atoms bodies hold, of the substrates of their
//     proteins that name a real reaction, of the proteins' folded material, and of the molecules whose
//     splitting gives energy.
public sealed partial class World
{
    public static readonly int[] GeoLevels = { 0, 3, 10, 30, 60 };

    // Atoms of each element in rock `d` levels under the top of each column (columns that deep only).
    public double[] StrataAtoms(int d, long[] molecules = null)
    {
        var at = new double[Chemistry.ElementCount];
        for (int c = 0; c < N; c++)
        {
            int z = Height[c] - 1 - d;
            if (z < 2) continue;
            int v = c * Z + z;
            if (Mat[v] < 2) continue;
            for (int s = 0; s < Chemistry.S; s++)
            {
                int n = VoxelCount(v, s);
                if (n == 0) continue;
                if (molecules != null) molecules[s] += n;
                for (int e = 0; e < Chemistry.ElementCount; e++) at[e] += (double)n * Chem.Atoms[s, e];
            }
        }
        return at;
    }

    static string Shares(double[] at)
    {
        double t = at.Sum();
        return string.Join(" ", at.Select(x => t > 0 ? $"{100 * x / t,5:F1}" : "    -"));
    }

    // An example design by its name in either language (names may be localized; the simulation does not
    // depend on them).
    static CreatureDesign FindExample(string name)
    {
        // Example names are stable keys (Russian); CreatureExamples.Key maps an English name to its key.
        string key = CreatureExamples.Key(name);
        var found = CreatureExamples.All.FirstOrDefault(d => string.Equals(d.Name, key, StringComparison.OrdinalIgnoreCase));
        if (found != null) return found;
        throw new ArgumentException(Loc.T($"--design: no example design '{name}'", $"--design: нет примера «{name}»"));
    }

    // A design for the probe: a JSON file (CreatureDesign, as the game's creature designer writes it)
    // when the argument names a file or ends in .json, otherwise a built-in example by name.
    public static bool IsDesignFile(string nameOrPath) =>
        nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || System.IO.File.Exists(nameOrPath);

    public static CreatureDesign FindDesign(string nameOrPath)
    {
        if (!IsDesignFile(nameOrPath)) return FindExample(nameOrPath);
        if (!System.IO.File.Exists(nameOrPath))
            throw new ArgumentException(Loc.T($"design file not found: {nameOrPath}", $"файл дизайна не найден: {nameOrPath}"));
        return CreatureLibrary.Load(nameOrPath);
    }

    // --invade [design.json] [--seeds 1-6] [--at 2000] [--ticks 8000] [--every 2000] [--count 5] [--spots 6] [--pop N] [--noabio]
    //   [--design Крот | --design path.json] (laws as usual, e.g. --set GeoProfile=0 for the other arm).
    // An invasion probe: at tick `at` the design (a built-in example by name, or a design file written by
    // the game's creature designer, given right after --invade or with --design) is planted (matter and energy brought from outside,
    // the same in every world) in `spots` lowland places, `count` bodies each, as one lineage; the probe then
    // follows that lineage: bodies alive, how deep they are under the ground the world was made with, how
    // many under a roof, the deep element's share of their atoms and of all that has been mined.
    public static void InvasionProbe(string[] args)
    {
        string Arg(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        var seeds = Batch.ParseSeeds(Arg("--seeds", "1-6"));
        int at = int.Parse(Arg("--at", "2000")), ticks = int.Parse(Arg("--ticks", "8000")), every = int.Parse(Arg("--every", "2000"));
        int count = int.Parse(Arg("--count", "5")), spots = int.Parse(Arg("--spots", "6")), pop = int.Parse(Arg("--pop", P.InitialPop.ToString()));
        bool abio = Array.IndexOf(args, "--noabio") < 0;
        // The design: `--invade file.json` (the word right after the flag, unless it is another flag) or --design.
        string name = Arg("--design", null), after = Arg("--invade", null);
        if (name == null && after != null && !after.StartsWith("--")) name = after;
        name ??= "Крот";
        CreatureDesign design;
        try { design = FindDesign(name); }
        catch (Exception e) when (e is ArgumentException || e is System.IO.IOException || e is System.Text.Json.JsonException || e is System.IO.InvalidDataException)
        {
            Console.Error.WriteLine("--invade: " + e.Message);
            Environment.Exit(2);
            return;
        }
        string source = IsDesignFile(name) ? Loc.T($" from {name}", $" из {name}") : "";
        name = CreatureExamples.DisplayName(design.Name);
        Console.WriteLine(Loc.T($"invasion probe: «{name}»{source}, {spots}×{count} bodies at tick {at}, profile {(P.GeoProfile != 0 ? "on" : "off")}, {ticks} ticks",
                                $"проба вторжения: «{name}»{source}, {spots}×{count} тел на тике {at}, профиль {(P.GeoProfile != 0 ? "вкл" : "выкл")}, {ticks} тиков"));
        Console.WriteLine("seed  tick  pop    probe  depth_mean depth_max roof1  deep_share  mined_depth deep_mined");
        foreach (int seed in seeds)
        {
            var w = new World(seed, pop, abio);
            var problems = design.Check(w.Chem);   // molecule names resolve per seed (the chemistry is generated)
            if (problems.Count > 0)
            {
                Console.WriteLine(Loc.T($"  seed {seed}: the design cannot be planted: ", $"  сид {seed}: дизайн нельзя посадить: ") + string.Join("; ", problems));
                continue;
            }
            long lineage = 0;
            for (int t = 1; t <= ticks; t++)
            {
                if (t == at)
                {
                    int planted = 0;
                    for (int k = 0, tries = 0; k < spots && tries < 5000; tries++)
                    {
                        int c = (int)(Hash32.U(seed, tries, 0x1A7) % N);
                        if (w.Submerged(c) || w.Height[c] > Crust + 9) continue;
                        var r = w.SpawnDesign(design, c % W, c / W, new SpawnOptions { Matter = MatterSource.Import, Energy = EnergySource.Import, Count = count, Lineage = lineage, Radius = 3 });
                        if (!r.Ok) continue;
                        lineage = r.Lineage; planted += r.Made; k++;
                    }
                    Console.WriteLine(Loc.T($"  seed {seed}: planted {planted} bodies, lineage #{lineage}", $"  сид {seed}: посажено {planted} тел, линия #{lineage}"));
                }
                w.Step();
                if (t < at || (t - at) % every != 0 && t != ticks) continue;
                var mine = w.Agents.Where(a => !a.Dead && a.Lineage == lineage).ToList();
                double depth = 0, deep = 0, all = 0; int dmax = 0, roof = 0;
                foreach (var a in mine)
                {
                    int c = a.Y * W + a.X, d = Math.Max(0, w.Height0[c] - a.Z);
                    depth += d; dmax = Math.Max(dmax, d);
                    if (w.Roof(c, a.Z) >= 1) roof++;
                    for (int s = 0; s < Chemistry.S; s++) { deep += (double)a.Inv[s] * w.Chem.Atoms[s, w.DeepElement]; all += (double)a.Inv[s] * w.Chem.AtomCount(s); }
                }
                var g = w.GeoCensus();
                int n = mine.Count;
                Console.WriteLine($"{seed,4} {t,5} {w.Agents.Count,5} {n,8} {(n > 0 ? depth / n : 0),10:F2} {dmax,9} {(n > 0 ? roof / (double)n : 0),5:P0} {(all > 0 ? deep / all : 0),10:P1} {g[2],11:F2} {g[4],10:P1}");
            }
        }
    }

    public static void GeochemReport(string[] args)
    {
        string Arg(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        var seeds = Batch.ParseSeeds(Arg("--seeds", "1-4"));
        int ticks = int.Parse(Arg("--ticks", "3000"));
        foreach (int seed in seeds)
        {
            var w = new World(seed, ticks > 0 ? P.InitialPop : 0, true);
            var ch = w.Chem;
            Console.WriteLine($"=== seed {seed} ===  elements: " + string.Join(", ", Enumerable.Range(0, Chemistry.ElementCount)
                .Select(e => $"{ch.ElementName[e]} mass {ch.AtomicMass[e]:F2} val {ch.Valence[e]} aff {ch.Affinity[e]:F2}" + (w.GeoOn ? $" depth bias {w.DepthBias[e]:+0.00;-0.00}" : ""))));
            Console.WriteLine("  formula        E  E*  split(E/E*)  bond grade solid  name");
            for (int s = 0; s < Chemistry.S; s += 2)
                Console.WriteLine($"  {ch.Formula(s),-12} {ch.E[s],3} {ch.E[s + 1],3}   {(ch.SplitExo[s] ? ch.SplitEnergy(s).ToString() : "-"),3}/{(ch.SplitExo[s + 1] ? ch.SplitEnergy(s + 1).ToString() : "-"),-3}    {ch.Bond[s],5:F2} {ch.MatTier[s + 2],3}   {(ch.Solid[s] ? "yes" : "  -")}  {ch.Name[s]}"
                    + (s == ch.Gas || s + 1 == ch.Gas ? " (gas)" : "") + (ch.VentHigh.Contains(s) || ch.VentHigh.Contains(s + 1) ? " (vent)" : ""));
            Console.WriteLine("  element shares of atoms, %       " + string.Join(" ", Enumerable.Range(0, Chemistry.ElementCount).Select(e => $"{ch.ElementName[e],5}")) + "   most common");
            foreach (int d in GeoLevels)
            {
                var mol = new long[Chemistry.S];
                var at = w.StrataAtoms(d, mol);
                long tot = mol.Sum();
                Console.WriteLine($"  rock {d,2} levels under the top     {Shares(at)}   " + string.Join(", ", Enumerable.Range(0, Chemistry.S).Where(s => mol[s] > 0)
                    .OrderByDescending(s => mol[s]).Take(4).Select(s => $"{ch.Formula(s)}{(s % 2 == 1 ? "*" : "")} {100.0 * mol[s] / Math.Max(1, tot):F0}%")));
            }
            {
                // What vents bring: the ejecta they would lay (VentHigh, or the depth profile).
                var at = new double[Chemistry.ElementCount];
                for (int k = 0; k < 4096; k++)
                {
                    int s = w.VentMolecule(Hash32.F(k, 77));
                    for (int e = 0; e < Chemistry.ElementCount; e++) at[e] += ch.MatCap[s + 2] * ch.Atoms[s, e];
                }
                Console.WriteLine($"  vent ejecta                       {Shares(at)}");
            }
            {
                var at = new double[Chemistry.ElementCount];
                for (int s = 0; s < Chemistry.S; s++)
                {
                    if (s == ch.Gas) continue;
                    double n = 0;
                    for (int i = 0; i < N; i++) n += w.C[s][i];
                    for (int e = 0; e < Chemistry.ElementCount; e++) at[e] += n * ch.Atoms[s, e];
                }
                Console.WriteLine($"  loose litter (start)              {Shares(at)}");
            }
            {
                var at = new double[Chemistry.ElementCount];
                for (int s = 0; s < Chemistry.S; s++)
                    if (ch.SplitExo[s]) for (int e = 0; e < Chemistry.ElementCount; e++) at[e] += ch.SplitEnergy(s) * ch.Atoms[s, e];
                Console.WriteLine($"  food: atoms of molecules whose split releases energy (weighted by it) {Shares(at)}");
            }
            for (int t = 0; t < ticks; t++) w.Step();
            if (ticks <= 0) continue;
            var live = w.Agents.Where(a => !a.Dead).ToList();
            var body = new double[Chemistry.ElementCount];
            var sub = new double[Chemistry.ElementCount];
            var mat = new double[Chemistry.ElementCount];
            var exo = new double[Chemistry.ElementCount];
            var kinds = new double[4];
            foreach (var a in live)
            {
                for (int s = 0; s < Chemistry.S; s++)
                    for (int e = 0; e < Chemistry.ElementCount; e++) body[e] += (double)a.Inv[s] * ch.Atoms[s, e];
                for (int k = 0; k < a.EnzN; k++)
                {
                    var z = a.Enz[k];
                    if (z.Amount < 0.5f) continue;
                    for (int e = 0; e < Chemistry.ElementCount; e++) mat[e] += z.Matter.D * ch.Atoms[z.Material, e];
                    int a1 = z.A % Chemistry.S, b1 = z.B % Chemistry.S;
                    bool real = z.Kind switch
                    {
                        Enzyme.Bind => ch.Combine[a1, b1] >= 0,
                        Enzyme.Split => ch.SplitA[a1] >= 0,
                        Enzyme.Photo => ch.PhotoUp[a1] >= 0,
                        _ => false,
                    };
                    if (!real) continue;
                    kinds[z.Kind] += z.Amount;
                    for (int e = 0; e < Chemistry.ElementCount; e++)
                    {
                        double n = ch.Atoms[a1, e] + (z.Kind == Enzyme.Bind ? ch.Atoms[b1, e] : 0);
                        sub[e] += z.Amount * n;
                        bool gives = z.Kind == Enzyme.Split ? ch.SplitExo[a1] : z.Kind == Enzyme.Bind && ch.E[a1] + ch.E[b1] > ch.E[ch.Combine[a1, b1]];
                        if (gives) exo[e] += z.Amount * n;
                    }
                }
            }
            Console.WriteLine($"  after {ticks} ticks: {live.Count} bodies; proteins naming a reaction (amount) bind {kinds[0]:F0} split {kinds[1]:F0} photo {kinds[2]:F0}");
            Console.WriteLine($"  atoms held in bodies              {Shares(body)}");
            Console.WriteLine($"  substrates of real proteins       {Shares(sub)}");
            Console.WriteLine($"  ... of those releasing energy     {Shares(exo)}");
            Console.WriteLine($"  proteins' folded material         {Shares(mat)}");
            var minedAt = w.GeoMined.Take(5);
            Console.WriteLine($"  mined by depth under the column top (0 / 1-2 / 3-9 / 10-29 / 30+): {string.Join(" / ", minedAt)}; deep-element share of body atoms: {(w.GeoOn ? $"{w.DeepShareOfBodies():P1}" : "-")}");
        }
    }
}
