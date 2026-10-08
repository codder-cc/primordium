using System;
using System.Globalization;
using System.Linq;

namespace Primordium;

// Bench flags for regions (World.Region.cs), applied to the world before the run:
//   --copy-region x,y,w,h out.region [--copy-levels z0,z1] [--copy-no-bodies]   write a region file (.region or .region.json)
//   --paste-region file --at x,y [--rotate k] [--paste-mode replace|above] [--no-bodies] [--dz n]
// The paste prints what moved and the hand's books; with --audit the run then checks the budgets as usual.
public static class RegionBench
{
    static int[] Ints(string s) => s.Split(',').Select(p => int.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray();
    static string Arg(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

    public static void Apply(World w, string[] args)
    {
        if (Arg(args, "--copy-region") is string rect)
        {
            int ci = Array.IndexOf(args, "--copy-region");
            var r4 = Ints(rect);
            string path = ci + 2 < args.Length ? args[ci + 2] : "region.region";
            var lv = Arg(args, "--copy-levels") is string l ? Ints(l) : new[] { 0, World.Z };
            var r = w.CopyRegion(r4[0], r4[1], r4[2], r4[3], lv[0], lv[1], Array.IndexOf(args, "--copy-no-bodies") < 0, System.IO.Path.GetFileNameWithoutExtension(path));
            r.Save(path);
            Console.WriteLine(Loc.T($"region {r.SizeX}×{r.SizeY} levels {r.Z0}…{r.Z1 - 1}: {r.Voxels} blocks, {r.Bodies.Count} bodies → {path}",
                                    $"участок {r.SizeX}×{r.SizeY} уровни {r.Z0}…{r.Z1 - 1}: блоков {r.Voxels}, существ {r.Bodies.Count} → {path}"));
        }
        if (Arg(args, "--paste-region") is string file)
        {
            var r = Region.Load(file);
            var at = Arg(args, "--at") is string a ? Ints(a) : new[] { 0, 0 };
            var o = new RegionPasteOptions
            {
                Rotation = Arg(args, "--rotate") is string k ? int.Parse(k, CultureInfo.InvariantCulture) : 0,
                Mode = Arg(args, "--paste-mode") == "above" ? PasteMode.AboveGround : PasteMode.Replace,
                Bodies = Array.IndexOf(args, "--no-bodies") < 0,
                Dz = Arg(args, "--dz") is string dz ? int.Parse(dz, CultureInfo.InvariantCulture) : 0,
            };
            var res = w.PasteRegion(r, at[0], at[1], o);
            Console.WriteLine(res.ToString());
            if (res.Mapping is { Identity: false } m) foreach (var line in m.Lines) Console.WriteLine("   " + line);
            var inv = CultureInfo.InvariantCulture;
            Console.WriteLine($"   hand: atoms in {string.Join("/", res.AtomsIn.Select(x => x.ToString("F0", inv)))} out {string.Join("/", res.AtomsOut.Select(x => x.ToString("F0", inv)))}, " +
                              $"energy in {res.EnergyIn.ToString("F1", inv)} out {res.EnergyOut.ToString("F1", inv)}, water in {res.WaterIn.ToString("F2", inv)} out {res.WaterOut.ToString("F2", inv)}");
            if (!res.Ok) Environment.Exit(2);
        }
    }
}
