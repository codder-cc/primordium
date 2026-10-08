using System;
using System.Globalization;
using System.Linq;

namespace Primordium;

// Population templates in the headless runner (Population.cs):
//   --plant-population file.json [--at x,y] [--local] [--local-energy] [--no-relations] [--no-remap]
//     before the run, paste the template with its centre at (x, y) (default: the middle of the map);
//     matter and energy brought from outside unless --local / --local-energy; at the end, how many
//     bodies of the pasted lineages are alive.
//   --copy-population out.json [--copy-lineage ID | --copy-area x,y,r]
//     after the run, copy a lineage (default: the biggest living one) or a circle of bodies to a file.
public static class PopulationTool
{
    static long[] pasted = Array.Empty<long>();

    static string Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static int[] Ints(string s) => s.Split(',').Select(v => int.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();

    public static void BeforeRun(World w, string[] args)
    {
        string file = Arg(args, "--plant-population");
        if (file == null) return;
        var t = PopulationLibrary.Load(file);
        int x = World.W / 2, y = World.H / 2;
        if (Arg(args, "--at") is string at) { var v = Ints(at); x = v[0]; y = v[1]; }
        var o = new PasteOptions
        {
            Matter = Array.IndexOf(args, "--local") >= 0 ? MatterSource.Local : MatterSource.Import,
            Energy = Array.IndexOf(args, "--local-energy") >= 0 ? EnergySource.Local : EnergySource.Import,
            KeepRelations = Array.IndexOf(args, "--no-relations") < 0,
            RemapGenes = Array.IndexOf(args, "--no-remap") < 0,
        };
        var r = w.PastePopulation(t, x, y, o);
        pasted = r.Lineages.ToArray();
        Console.WriteLine(Loc.T($"population '{t.Name}' ({file}): pasted {r.Made} of {r.Requested} at {x},{y}, lineages {string.Join(" ", r.Lineages)}, ",
                                $"популяция «{t.Name}» ({file}): вставлено {r.Made} из {r.Requested} в {x},{y}, линии {string.Join(" ", r.Lineages)}, ") +
                          Loc.T($"atoms from outside {string.Join("/", r.AtomsImported.Select(a => a.ToString("F0", CultureInfo.InvariantCulture)))}, energy from outside {r.EnergyImported:F1}, local {r.EnergyLocal:F1}",
                                $"атомы извне {string.Join("/", r.AtomsImported.Select(a => a.ToString("F0", CultureInfo.InvariantCulture)))}, энергия извне {r.EnergyImported:F1}, местная {r.EnergyLocal:F1}"));
        if (!r.Map.Same)
        {
            Console.WriteLine(Loc.T($"  another chemistry (template seed {t.Chemistry.Seed}, world {w.Seed}): {r.Map.Changed} species mapped, {r.GenesRemapped} protein genes retargeted",
                                    $"  другая химия (сид шаблона {t.Chemistry.Seed}, мира {w.Seed}): заменено видов {r.Map.Changed}, перенацелено генов белков {r.GenesRemapped}"));
            foreach (var c in r.Map.Changes) Console.WriteLine("    " + Loc.T(c.en, c.ru));
        }
        foreach (var (why, n) in r.Failures) Console.WriteLine(Loc.T($"  not made ×{n}: {why}", $"  не сделано ×{n}: {why}"));
    }

    public static void AfterRun(World w, string[] args)
    {
        if (pasted.Length > 0)
            Console.WriteLine(Loc.T($"pasted lineages at tick {w.Tick}: ", $"вставленные линии на тике {w.Tick}: ") +
                              string.Join(", ", pasted.Select(l => $"#{l} {w.Agents.Count(a => !a.Dead && a.Lineage == l)}")));
        string file = Arg(args, "--copy-population");
        if (file == null) return;
        System.Collections.Generic.List<Agent> bodies;
        string source;
        if (Arg(args, "--copy-area") is string area) { var v = Ints(area); bodies = w.AreaBodies(v[0], v[1], v[2]); source = "area"; }
        else
        {
            long lineage = Arg(args, "--copy-lineage") is string l ? long.Parse(l, CultureInfo.InvariantCulture)
                : w.Agents.Where(a => !a.Dead).GroupBy(a => a.Lineage).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault();
            bodies = w.LineageBodies(lineage);
            source = "lineage";
        }
        var t = w.CopyPopulation(bodies, System.IO.Path.GetFileNameWithoutExtension(file), source);
        System.IO.File.WriteAllText(file, t.ToJson());
        Console.WriteLine(Loc.T($"copied {t.Bodies.Count} bodies ({t.Lineages} lineages, {source}) to {file}", $"скопировано {t.Bodies.Count} тел (линий {t.Lineages}, {source}) в {file}"));
    }
}
