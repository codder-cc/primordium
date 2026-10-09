using System;
using System.Collections.Generic;
using System.Linq;

namespace Primordium;

// Organs of sense (P.Organs, World.Organs): probes for the headless runner.
//
//   --design-probe [--designs Leaf,Mole,Swimmer|file.json,…] [--seeds 1-4] [--ticks 3000] [--every 1000]
//                  [--count 4] [--spots 3] [--size WxHxL] [--pop N] [--noabio]   (laws as usual: --set Organs=1 …)
//     Each design alone in a small world of each seed (96×96×64 unless --size; no other life unless --pop):
//     `spots` places × `count` bodies brought from outside (on land; the swimmer in a lake), followed —
//     its living descendants, their charge (MatterEnergy 1) or energy, and their organ proteins.
//
//   --cave-probe [--seeds 1-3] [--ticks 20000] [--every 2000] [--count 12]   (laws as usual)
//     The blind cavefish test on a built world (Scenario.cs): a lit floor and, under the same floor, a
//     roofed cave, joined by an open shaft; one founding design (CaveSeeker, below) — a body that eats
//     what lies around, reads the light now and then and makes photoreceptors — planted half in the light,
//     half in the dark. Mutation does the rest: the share of bodies with photoreceptors and their amount,
//     under the roof and in the open, over time.
public sealed partial class World
{
    static string ArgOf(string[] args, string name, string def)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
    }

    public static void DesignProbe(string[] args)
    {
        var seeds = Batch.ParseSeeds(ArgOf(args, "--seeds", "1-4"));
        int ticks = int.Parse(ArgOf(args, "--ticks", "3000")), every = int.Parse(ArgOf(args, "--every", "1000"));
        int count = int.Parse(ArgOf(args, "--count", "4")), spots = int.Parse(ArgOf(args, "--spots", "3")), pop = int.Parse(ArgOf(args, "--pop", "0"));
        bool abio = Array.IndexOf(args, "--noabio") < 0 && pop > 0;
        var names = ArgOf(args, "--designs", "Leaf,Mole,Swimmer").Split(',', StringSplitOptions.RemoveEmptyEntries);
        Console.WriteLine($"design probe: Organs {P.Organs}, MatterEnergy {P.MatterEnergy}, chemistry model {P.ChemEnergyModel}; {spots}×{count} bodies, {ticks} ticks");
        Console.WriteLine("design      seed  tick   alive  gen  energy  photoR  recept  mechano thermo  motor  sense/tick");
        foreach (string name in names)
        {
            var design = FindDesign(name.Trim());
            bool swimmer = design.Name == CreatureExamples.Swimmer.Name || name.Contains("swim", StringComparison.OrdinalIgnoreCase);   // planted in a lake
            var finals = new List<int>();
            foreach (int seed in seeds)
            {
                var settings = Batch.Settings(args, seed, pop, abio, 0);
                if (Array.IndexOf(args, "--size") < 0) { settings.Width = settings.Height = SmallSide; settings.Levels = SmallLevels; }
                var w = new World(settings);
                long lineage = 0;
                int planted = 0;
                for (int k = 0, tries = 0; k < spots && tries < 20000; tries++)
                {
                    int c = (int)(Hash32.U(seed, tries, 0x5E) % w.N);
                    if (MathF.Abs(w.Temp[c] - 15) > 10 || w.Count[c] > 0) continue;
                    if (swimmer ? w.Water[c] < 2 : w.Submerged(c)) continue;
                    var r = w.SpawnDesign(design, c % w.W, c / w.W, new SpawnOptions { Matter = MatterSource.Import, Energy = EnergySource.Import, Count = count, Lineage = lineage, Radius = 2 });
                    if (!r.Ok) continue;
                    lineage = r.Lineage; planted += r.Made; k++;
                }
                if (planted == 0) { Console.WriteLine($"{CreatureExamples.DisplayName(design.Name),-10} {seed,4}  no place to plant"); continue; }
                int alive = 0;
                for (int t = 1; t <= ticks; t++)
                {
                    w.Step();
                    if (t % every != 0 && t != ticks) continue;
                    var mine = w.Agents.Where(a => !a.Dead && a.Lineage == lineage).ToList();
                    alive = mine.Count;
                    var oc = w.OrganCensus();
                    double Am(int kind) => mine.Count == 0 ? 0 : mine.Average(a => Enumerable.Range(0, a.EnzN).Where(k => a.Enz[k].Kind == kind).Sum(k => (double)a.Enz[k].Amount));
                    double sense = oc.Skip(OrganNames.Length - 8).Take(5).Sum();
                    Console.WriteLine($"{CreatureExamples.DisplayName(design.Name),-10} {seed,4} {t,6} {alive,6} {(mine.Count > 0 ? mine.Max(a => a.Gen) : 0),5} {(mine.Count > 0 ? mine.Average(a => w.Held(a)) : 0),7:F1} {Am(Enzyme.Photoreceptor),7:F2} {Am(Enzyme.Receptor),7:F2} {Am(Enzyme.Mechano),7:F2} {Am(Enzyme.Thermo),6:F2} {Am(Enzyme.Motor),6:F2} {sense,8:F2}");
                    if (alive == 0) break;
                }
                finals.Add(alive);
            }
            Console.WriteLine($"SUMMARY {CreatureExamples.DisplayName(design.Name)}: alive at the end {string.Join(" ", finals)} (seeds {string.Join(",", seeds)}), survived in {finals.Count(x => x > 0)}/{finals.Count}");
        }
    }

    // ---- the blind cavefish ----

    // A body that drinks what lies around and relaxes its excited atoms, and in the light also catches photons —
    // but only when its photoreceptor tells it there is light (without the eye `light` reads 0 and it never
    // tries). In the open the eye pays; under a roof light is always 0 and the eye is only a cost (making it,
    // its upkeep, every reading). Nothing tells the body where it lives; mutation and selection do the rest.
    public static CreatureDesign CaveSeeker(int pigment) => new()
    {
        Name = "пещерник",
        Body = new() { ["0"] = 10, [pigment.ToString()] = 4, ["any"] = 6 },
        Energy = 30,
        Genome = $@"label 1
enzyme photoreceptor {pigment} t=15 q=0.9
enzyme photo 0 0 t=15 q=0.9
enzyme split 1 0 t=15 q=0.9
label 0
light
lit 4
lt
jnz 2
push 0
photo
push 0
photo
label 2
drink
push 1
split
energy
lit 24
lt
jnz 3
push 0
rand
divide
label 3
rand
lit 5
lt
jnz 1
jmp 0
",
    };

    public static void CaveProbe(string[] args)
    {
        var seeds = Batch.ParseSeeds(ArgOf(args, "--seeds", "1-3"));
        int ticks = int.Parse(ArgOf(args, "--ticks", "20000")), every = int.Parse(ArgOf(args, "--every", "2000")), count = int.Parse(ArgOf(args, "--count", "12"));
        float food = float.Parse(ArgOf(args, "--food", "0.04"), System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine($"cave probe: Organs {P.Organs}, MatterEnergy {P.MatterEnergy}, chemistry model {P.ChemEnergyModel}; {ticks} ticks, food {food} per cell and tick");
        Console.WriteLine("seed   tick  pop_open pop_cave  photoR_open photoR_cave  has_open has_cave  gen_open gen_cave  roofed_cells");
        foreach (int seed in seeds)
        {
            var w = CaveWorld(seed, out int floor, out int caveFloor);
            int pigment = 0;
            var design = CaveSeeker(pigment);
            var problems = design.Check(w.Chem);
            if (problems.Count > 0) { Console.WriteLine($"seed {seed}: {string.Join("; ", problems)}"); continue; }
            int mid = w.H / 2;
            foreach (var (x, roofed) in new[] { (w.W / 4, false), (3 * w.W / 4, true) })
                for (int k = 0; k < 4; k++)
                {
                    int c = (mid - 12 + 8 * k) * w.W + x;
                    var r = w.SpawnDesign(design, c % w.W, c / w.W, new SpawnOptions { Matter = MatterSource.Import, Energy = EnergySource.Import, Count = count / 4, Radius = 2 });
                    if (!roofed) continue;
                    foreach (var a in r.Agents)   // into the cave below (the design is planted on the floor's top)
                    {
                        int ac = a.Y * w.W + a.X;
                        if (w.Mat[ac * w.Z + caveFloor] != Chemistry.Air) continue;
                        w.Unplace(a, ac); a.Z = caveFloor; w.Place(a, ac);
                    }
                }
            for (int t = 1; t <= ticks; t++)
            {
                if (t % 10 == 1) w.FeedCave(food * 10, floor, caveFloor);
                w.Step();
                if (t % every != 0 && t != ticks) continue;
                var live = w.Agents.Where(a => !a.Dead).ToList();
                var inCave = live.Where(a => w.InCave(a)).ToList();
                var inOpen = live.Where(a => !w.InCave(a)).ToList();
                double Photo(Agent a) => Enumerable.Range(0, a.EnzN).Where(k => a.Enz[k].Kind == Enzyme.Photoreceptor).Sum(k => (double)a.Enz[k].Amount);
                double Mean(List<Agent> l) => l.Count == 0 ? double.NaN : l.Average(Photo);
                double Has(List<Agent> l) => l.Count == 0 ? double.NaN : l.Count(a => Photo(a) >= 0.5) / (double)l.Count;
                double Gen(List<Agent> l) => l.Count == 0 ? 0 : l.Average(a => a.Gen);
                int roofed = 0;
                for (int c = 0; c < w.N; c++) if (c % w.W >= w.W / 2 && w.Height[c] > caveFloor + 1 && w.Mat[c * w.Z + caveFloor] == Chemistry.Air) roofed++;
                Console.WriteLine($"{seed,4} {t,6} {inOpen.Count,9} {inCave.Count,8} {Mean(inOpen),12:F2} {Mean(inCave),11:F2} {Has(inOpen),9:P0} {Has(inCave),8:P0} {Gen(inOpen),9:F1} {Gen(inCave),8:F1} {roofed,12}");
                if (live.Count == 0) break;
            }
        }
    }

    // A flat world of strong rock 10 levels high; its east half hollowed into a cave (levels 4–6 under a roof
    // of 3, held up by a pillar every 4 columns), the west half open to the sky. Bodies cannot cross: the
    // cave's mouth is a wall.
    static World CaveWorld(int seed, out int floor, out int caveFloor)
    {
        var w = new World(TinySettings(seed, 0, false, false, SmallSide, SmallSide, 32));
        w.Flatten();
        var ch = w.Chem;
        int rock = Enumerable.Range(0, Chemistry.S).OrderByDescending(s => ch.Bond[s] / ch.Mass[s]).First();
        floor = 10; caveFloor = 4;
        w.Floor(floor, rock);
        for (int c = 0; c < w.N; c++)
        {
            int x = c % w.W, y = c / w.W;
            if (x <= w.W / 2 || x == w.W - 1 || (x % 4 == 0 && y % 4 == 0)) continue;
            for (int z = caveFloor; z < caveFloor + 3; z++) if (w.Mat[c * w.Z + z] != Chemistry.Air) w.RemoveVoxel(c, z);
        }
        w.StepStructure();
        return w;
    }

    // Food for the probe: loose excited atoms (A*, species 1: in every chemistry) on every floor, open and
    // roofed alike, brought from outside and booked like the pour brush.
    void FeedCave(float perCell, int floor, int caveFloor)
    {
        for (int c = 0; c < N; c++)
        {
            int n = (int)(perCell + mainRng.NextDouble());
            if (n <= 0) continue;
            int level = Height[c] > caveFloor + 1 && Mat[c * Z + caveFloor] == Chemistry.Air ? caveFloor : Height[c];
            ChangeLooseAt(c, level, 1, n);
            for (int e = 0; e < Chemistry.ElementCount; e++) HandInput[e] += (double)n * Chem.Atoms[1, e];
            Flows[FHand] += (double)n * Chem.E[1];
        }
    }
}
