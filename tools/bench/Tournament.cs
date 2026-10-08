using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Primordium;

// Ancestor tournament (ROADMAP 7.1): do later genomes beat earlier ones in the same world?
//
//   --tournament [--seed S | --load late.sav] [--ticks T] [--snap-every E] [--ancestors early.sav[,more.sav]]
//                [--top 3] [--count 20] [--spots 4] [--arena-ticks 3000] [--reps 3] [--energy 40] [--residents]
//                [--pop N] [--noabio] (laws as usual)
// Ancestors: the `top` most common genomes (exact genome, World alive bodies grouped by Agent.Hash) at
// earlier ticks — snapshots every E ticks of a run of T ticks (from a new world of seed S or from --load),
// and/or saves given with --ancestors (same seed: the same chemistry). Moderns: the `top` most common
// genomes at the end. Every contestant is planted as a design copied from a body that carries its genome
// (CreatureDesign.FromAgent: its genome, its molecules; energy `--energy` for all), with matter and energy
// brought from outside (booked as the hand's).
// Arena: a copy of the final world (same terrain, climate, chemistry, laws and moment); its residents are
// killed first (their remains stay, as after any death) unless --residents. Into it go `count` bodies of
// each side — split evenly over its genomes and over `spots` places, half taken from where the moderns
// live and half from where the ancestors lived, both sides at every place, the order alternating —
// each genome as its own lineage. After `arena-ticks` the living bodies of each side's lineages are
// counted. Modern share = moderns / (moderns + ancestors); repeated with `reps` placements.
// Reading: above 50% — adaptation accumulates; about 50% — standing still or going in circles; below 50% —
// the world changed and the old genomes fit it better. Observation only: the main world is never touched
// (the arena is a copy loaded from memory).
public sealed partial class World
{
    sealed class Contestant
    {
        public long Tick, Lineage;
        public int Count, Molecules;
        public ulong Hash;
        public CreatureDesign Design;
        public List<int> Cells = new();   // where bodies with this genome were
    }

    static List<Contestant> TopGenomes(World w, int top, float energy)
    {
        var groups = w.Agents.Where(a => !a.Dead).GroupBy(a => a.Hash)
            .Select(g => g.OrderBy(a => a.Id).ToList())
            .OrderByDescending(g => g.Count).ThenBy(g => g[0].Hash).Take(top).ToList();
        var list = new List<Contestant>();
        foreach (var g in groups)
        {
            // The body of median size among those with the genome (ties: lowest id).
            var rep = g.OrderBy(a => a.InvTotal).ThenBy(a => a.Id).ElementAt(g.Count / 2);
            var d = CreatureDesign.FromAgent(w, rep, $"t{w.Tick} #{rep.Lineage} ×{g.Count}");
            d.Energy = energy;
            list.Add(new Contestant
            {
                Tick = w.Tick, Lineage = rep.Lineage, Count = g.Count, Molecules = rep.InvTotal, Hash = rep.Hash, Design = d,
                Cells = g.Take(64).Select(a => a.Y * W + a.X).ToList(),
            });
        }
        return list;
    }

    static byte[] SaveToMemory(World w)
    {
        using var ms = new MemoryStream();
        w.Save(ms, "tournament arena");
        return ms.ToArray();
    }

    // One match in a fresh copy of the arena: returns (moderns alive, ancestors alive, planted each side).
    static (int modern, int ancient, int plantedModern, int plantedAncient) Match(byte[] arena, List<Contestant> ancients, List<Contestant> moderns,
        int count, int spots, int arenaTicks, bool residents, int rep)
    {
        var w = Load(new MemoryStream(arena));
        if (!residents)
            foreach (var a in w.Agents.ToList())
                if (!a.Dead) w.Die(a, a.Y * W + a.X, CauseHand);
        // Places: alternately where the moderns live and where the ancestors lived, chosen by the repeat.
        var places = new List<int>();
        var pools = new[] { moderns.SelectMany(c => c.Cells).ToList(), ancients.SelectMany(c => c.Cells).ToList() };
        for (int k = 0; places.Count < spots && k < spots * 8; k++)
        {
            var pool = pools[k % 2].Count > 0 ? pools[k % 2] : pools[(k + 1) % 2];
            if (pool.Count == 0) break;
            int c = pool[(int)(Hash32.U(rep, k, 0x70A) % (uint)pool.Count)];
            if (!places.Contains(c)) places.Add(c);
        }
        var lineages = new Dictionary<Contestant, long>();
        int[] planted = new int[2];
        var sides = new[] { moderns, ancients };
        for (int side = 0; side < 2; side++)
            foreach (var c in sides[side]) lineages[c] = 0;
        for (int p = 0; p < places.Count; p++)
            for (int o = 0; o < 2; o++)
            {
                int side = (p + o + rep) % 2;   // who goes first alternates by place and repeat
                var list = sides[side];
                for (int j = 0; j < list.Count; j++)
                {
                    // count bodies per side, spread over genomes and places as evenly as integers allow
                    int slot = p * list.Count + j, slots = places.Count * list.Count;
                    int n = count / slots + (slot < count % slots ? 1 : 0);
                    if (n == 0) continue;
                    var c = list[j];
                    var r = w.SpawnDesign(c.Design, places[p] % W, places[p] / W,
                        new SpawnOptions { Matter = MatterSource.Import, Energy = EnergySource.Import, Count = n, Lineage = lineages[c], Radius = 3 });
                    if (r.Ok) { lineages[c] = r.Lineage; planted[side] += r.Made; }
                }
            }
        for (int t = 0; t < arenaTicks; t++) w.Step();
        var modernSet = moderns.Select(c => lineages[c]).Where(l => l > 0).ToHashSet();
        var ancientSet = ancients.Select(c => lineages[c]).Where(l => l > 0).ToHashSet();
        int m = 0, an = 0;
        foreach (var a in w.Agents)
        {
            if (a.Dead) continue;
            if (modernSet.Contains(a.Lineage)) m++;
            else if (ancientSet.Contains(a.Lineage)) an++;
        }
        return (m, an, planted[0], planted[1]);
    }

    public static void Tournament(string[] args)
    {
        string Arg(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int seed = int.Parse(Arg("--seed", "1")), ticks = int.Parse(Arg("--ticks", Arg("--load", null) != null ? "0" : "8000"));
        int snapEvery = int.Parse(Arg("--snap-every", Math.Max(1, ticks / 4).ToString()));
        int top = int.Parse(Arg("--top", "3")), count = int.Parse(Arg("--count", "20")), spots = int.Parse(Arg("--spots", "4"));
        int arenaTicks = int.Parse(Arg("--arena-ticks", "3000")), reps = int.Parse(Arg("--reps", "3"));
        int pop = int.Parse(Arg("--pop", P.InitialPop.ToString()));
        float energy = float.Parse(Arg("--energy", "40"), inv);
        bool abio = Array.IndexOf(args, "--noabio") < 0, residents = Array.IndexOf(args, "--residents") >= 0;
        string load = Arg("--load", null), ancestorSaves = Arg("--ancestors", null);

        // Ancestors from saves first: loading sets the laws, the arena's own load sets them back.
        var ancestry = new List<List<Contestant>>();
        int? seedOfAncestors = null;
        if (ancestorSaves != null)
            foreach (var path in ancestorSaves.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var old = Load(path);
                seedOfAncestors = old.Seed;
                var list = TopGenomes(old, top, energy);
                if (list.Count > 0) ancestry.Add(list);
                Console.WriteLine(Loc.T($"ancestors from {path}: tick {old.Tick}, {old.Agents.Count} bodies", $"предки из {path}: тик {old.Tick}, {old.Agents.Count} тел"));
            }
        var w = load != null ? Load(load) : new World(seed, pop, abio);
        if (seedOfAncestors is int sa && sa != w.Seed)
        {
            Console.Error.WriteLine(Loc.T($"--ancestors: seed {sa} differs from the arena's {w.Seed} (another chemistry)", $"--ancestors: сид {sa} не совпадает с сидом арены {w.Seed} (другая химия)"));
            Environment.Exit(2);
        }
        Console.WriteLine(Loc.T($"tournament: world seed {w.Seed} from tick {w.Tick}, run {ticks} ticks, snapshots every {snapEvery}; top {top} genomes per side, {count} bodies per side at {spots} places, arena {arenaTicks} ticks × {reps} placements, residents {(residents ? "kept" : "killed")}",
                                $"турнир: мир сида {w.Seed} с тика {w.Tick}, прогон {ticks} тиков, снимки каждые {snapEvery}; по {top} генома на сторону, {count} тел на сторону в {spots} местах, арена {arenaTicks} тиков × {reps} расстановок, жители {(residents ? "оставлены" : "убиты")}"));
        for (int t = 1; t <= ticks; t++)
        {
            w.Step();
            if (t % snapEvery == 0 && t < ticks)
            {
                var list = TopGenomes(w, top, energy);
                if (list.Count > 0) ancestry.Add(list);
                Console.WriteLine(Loc.T($"  snapshot at tick {w.Tick}: {w.Agents.Count} bodies, top genomes ", $"  снимок на тике {w.Tick}: {w.Agents.Count} тел, частые геномы ")
                    + string.Join(", ", list.Select(c => $"#{c.Lineage}×{c.Count}")));
            }
        }
        var moderns = TopGenomes(w, top, energy);
        if (moderns.Count == 0 || ancestry.Count == 0)
        {
            Console.WriteLine(Loc.T("nothing to compare: no living bodies or no ancestors (give --ancestors or a run with snapshots)", "сравнивать нечего: нет живых тел или предков (задайте --ancestors или прогон со снимками)"));
            return;
        }
        Console.WriteLine(Loc.T($"moderns at tick {w.Tick} ({w.Agents.Count} bodies, hash {w.StateHash():x16}): ", $"современники на тике {w.Tick} ({w.Agents.Count} тел, хеш {w.StateHash():x16}): ")
            + string.Join(", ", moderns.Select(c => $"#{c.Lineage}×{c.Count} ({c.Molecules} {Loc.T("mol", "мол")})")));
        var arena = SaveToMemory(w);
        Console.WriteLine(Loc.T("ancestors_tick  age   planted(mod/anc)  alive per placement (mod:anc)         modern_share  wins  verdict",
                                "тик_предков     возр  посажено(совр/пр) живых по расстановкам (совр:пр)       доля_совр     побед вывод"));
        foreach (var ancients in ancestry)
        {
            var shares = new List<double>();
            var cells = new List<string>();
            int wins = 0, pm = 0, pa = 0;
            for (int r = 0; r < reps; r++)
            {
                var (m, a, plantedM, plantedA) = Match(arena, ancients, moderns, count, spots, arenaTicks, residents, r);
                pm = plantedM; pa = plantedA;
                cells.Add($"{m}:{a}");
                if (m + a == 0) continue;   // both died out: no verdict from this placement
                double share = m / (double)(m + a);
                shares.Add(share);
                if (share > 0.5) wins++;
            }
            double mean = shares.Count > 0 ? shares.Average() : double.NaN;
            string verdict = shares.Count == 0 ? Loc.T("both died out", "вымерли обе")
                : mean > 0.6 ? Loc.T("moderns win", "побеждают современники")
                : mean < 0.4 ? Loc.T("ancestors win", "побеждают предки")
                : Loc.T("even", "поровну");
            long anc = ancients[0].Tick;
            Console.WriteLine($"{anc,14} {w.Tick - anc,5}   {pm,6}/{pa,-6}       {string.Join(" ", cells),-36} {(double.IsNaN(mean) ? "-" : (mean * 100).ToString("F0", inv) + "%"),12}  {wins,2}/{shares.Count,-2} {verdict}"
                + (shares.Count > 1 ? $" ({shares.Min() * 100:F0}–{shares.Max() * 100:F0}%)" : ""));
        }
    }
}
