using System;
using System.Linq;
using Primordium.Model2;

namespace Primordium;

// --model2-motor [--seed 5] [--effort 3000]: the seeded motor (Mot) and its knockout (MotΔ) as a two-state (MWC) machine —
// the share of copies in R (run) by the food outside and inside (concentrations per room, as ChemModel.Mwc computes
// them), and the run and turn drive per copy. Shows how strongly the receptor can bias running in this chemistry.
public sealed partial class World
{
    public static void Model2Motor(string[] args)
    {
        int seed = int.Parse(Arg2(args, "--seed", "5"));
        var chem = new World(TinySettings(seed)).Chem;
        var set = Seeds.For(chem, int.Parse(Arg2(args, "--effort", "3000")));
        var c = Chem2.Of(chem);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int level = Chem2.Level(25);
        foreach (var name in new[] { "Mot", "MotΔ" })
        {
            var t = ProteinType.Of(c, set.Genes[name]);
            var lv = t.At(level);
            Console.WriteLine($"{name}: {t.Detail(c.Chem)}; L {lv.L[0].ToString("0.000", inv)}");
            double[] outs = { 0, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2 }, ins = { 0.01, 0.05, 0.1, 0.2, 0.5 };
            Console.WriteLine("  share in R (rows: food inside per room; columns: outside)  " + string.Join(" ", outs.Select(o => o.ToString("0.000", inv).PadLeft(6))));
            foreach (var ci in ins)
            {
                var line = $"  in {ci.ToString("0.00", inv),5}:";
                foreach (var co in outs)
                {
                    double num = 1, den = 1;
                    for (int p = 0; p < t.Pockets.Length; p++)
                    {
                        var pk = t.Pockets[p];
                        double sr = 0, sT = 0;
                        for (int l = 0; l < pk.N; l++)
                        {
                            if (pk.Lig(l) != set.Food) continue;
                            double conc = pk.Side == ProteinType.Out ? co : pk.Side == ProteinType.Tm ? 0.5 * (ci + co) : ci;
                            sr += conc * lv.KR[p * ProteinType.MaxLig + l]; sT += conc * lv.KT[p * ProteinType.MaxLig + l];
                        }
                        num *= 1 + sr; den *= 1 + sT;
                    }
                    double f = num / (num + lv.L[0] * den);
                    line += " " + f.ToString("0.000", inv).PadLeft(6);
                }
                Console.WriteLine(line);
            }
        }
    }
}
