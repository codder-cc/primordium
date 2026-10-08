using System;
using System.Collections.Generic;
using System.Linq;

namespace Primordium;

// --food-chain [--seeds 1-2 | --load path.sav] [--ticks 6000] [--every 2000] [--pop N] [--noabio] [--top 6]
//   [--edge 0.05] [--min-share 0.005] [--by lineage|genome] (laws as usual)
// Who feeds on whom (ROADMAP 7.1, length of food chains). Observation only: World.FoodProbe books, per
// lineage, the molecules bodies gain from the environment and those torn out of (attack) or stolen from
// (take) other bodies, and kills; every `every` ticks the window is summarised and cleared:
//   prey share — of all molecules gained, the share that came from bodies;
//   TL mean / max — trophic level (1: only environment; 1 + Σ f·TL of the sources), weighted by molecules
//     gained / the highest of the lineages that gained ≥ min-share of all;
//   chain — lineages on the longest path of edges that bring ≥ edge of the eater's molecules (1: nobody
//     feeds on others to that degree); own — prey taken inside the eater's own lineage; kills.
// Nodes are lineages (founder ids; predation inside one is reported as own, not as a level), or with
// --by genome exact genomes (the family tree's taxa: kin with another genome is another node)..
// The probe does not change the trajectory (the state hash is printed to check against a run without it).
public sealed partial class World
{
    public static void FoodChainReport(string[] args)
    {
        string Arg(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int ticks = int.Parse(Arg("--ticks", "6000")), every = int.Parse(Arg("--every", "2000")), top = int.Parse(Arg("--top", "6"));
        int pop = int.Parse(Arg("--pop", P.InitialPop.ToString()));
        double edge = double.Parse(Arg("--edge", "0.05"), inv), minShare = double.Parse(Arg("--min-share", "0.005"), inv);
        bool abio = Array.IndexOf(args, "--noabio") < 0, byGenome = Arg("--by", "lineage") == "genome";
        string load = Arg("--load", null);
        var seeds = load != null ? new System.Collections.Generic.List<int> { 0 } : Batch.ParseSeeds(Arg("--seeds", "1-2"));
        Console.WriteLine(Loc.T($"food chain: {(load ?? "seeds " + string.Join(",", seeds))}, {ticks} ticks, windows of {every}, nodes by {(byGenome ? "genome" : "lineage")}; significant edge ≥ {edge:P0} of the eater's molecules, lineages ≥ {minShare:P1} of all",
                                $"цепочки питания: {(load ?? "сиды " + string.Join(",", seeds))}, {ticks} тиков, окна по {every}, узлы — {(byGenome ? "геномы" : "линии")}; значимое ребро ≥ {edge:P0} молекул едока, линии ≥ {minShare:P1} всех"));
        Console.WriteLine(Loc.T("seed   tick    pop  lineages predators  prey_share  TL_mean TL_max chain   own_prey   kills  hash",
                                "сид    тик     тел  линий    хищников   доля_добычи УТ_сред УТ_макс цепь  своя_линия убийств хеш"));
        foreach (int seed0 in seeds)
        {
            var w = load != null ? Load(load) : new World(seed0, pop, abio);
            int seed = w.Seed;
            var probe = w.FoodProbe = new FoodWebProbe { ByGenome = byGenome };
            for (int t = 1; t <= ticks; t++)
            {
                w.Step();
                probe.Ticks++;
                if (t % every != 0 && t != ticks) continue;
                var s = probe.Summarize(edge, minShare);
                Console.WriteLine($"{seed,4} {w.Tick,7} {w.Agents.Count,6} {s.Lineages,8} {s.Predators,9}  {s.PreyShare,10:P2} {s.MeanLevel,8:F3} {(s.MaxLevel > 0 ? s.MaxLevel.ToString("F2", inv) : "-"),6} {s.Chain,5} {(s.PreyMolecules > 0 ? s.OwnLineagePrey / (double)s.PreyMolecules : 0),10:P0} {s.Kills,7}  {w.StateHash():x16}");
                string Id(long k) => byGenome ? ((ulong)k).ToString("x16") : k.ToString();
                string Lvl(long k) => s.Level.TryGetValue(k, out double l) ? l.ToString("F2", inv) : "-";
                foreach (var e in s.Top.Take(top))
                    Console.WriteLine(Loc.T($"        {(byGenome ? "genome" : "lineage")} #{Id(e.eater)} (TL {Lvl(e.eater)}) ← #{Id(e.source)}{(e.eater == e.source ? " (own)" : "")}: {e.molecules} molecules ({e.share:P1} of its gain), {e.kills} kills",
                                            $"        {(byGenome ? "геном" : "линия")} #{Id(e.eater)} (УТ {Lvl(e.eater)}) ← #{Id(e.source)}{(e.eater == e.source ? " (своя)" : "")}: {e.molecules} молекул ({e.share:P1} её прихода), {e.kills} убийств"));
                probe.Clear();
            }
        }
    }
}
