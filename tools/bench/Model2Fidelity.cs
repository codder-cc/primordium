using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Primordium.Model2;

namespace Primordium;

// --model2-fidelity [--seeds 1-12] [--laws 1,0] [--effort 3000]: how faithfully life model 2 can copy its genome
// in each seed's chemistry (docs/DESIGN-LIFE-MODEL-2.md §3.2, §3.4) — for each pairing law (Life2PairLaw):
//   pairs     every letter's complement and its pairing energy; the letters a copy without an enzyme gets right
//             ≥ 90 % of the time among all 16 at hand
//   alphabet  the seeded cells' letters (Model2.Seeds) and the monomers a copy of them needs (with complements)
//   error     per residue at 25 °C with those monomers equally at hand: without an enzyme (D 1), with the seeded
//             polymerase's discrimination (D from its pocket's rigidity), and with its proofreading passes
//   heredity  the expected substitutions per copy of the phototroph's genome (p·L): ≤ 1 sustains heredity
//             (≥ 37 % of the copies exact: Eigen's threshold for a master sequence with an advantage of e)
// The share of seeds that sustain heredity is the summary. The measured error of copies in a living cell is
// in --model2-demo (K1: copies and substitutions).
public sealed partial class World
{
    public static void Model2Fidelity(string[] args)
    {
        var inv = CultureInfo.InvariantCulture;
        int effort = int.Parse(Arg2(args, "--effort", "3000"));
        var laws = Arg2(args, "--laws", "1,0").Split(',').Select(int.Parse).ToArray();
        var seeds = Batch.ParseSeeds(Arg2(args, "--seeds", "1-12")).ToArray();
        int law0 = P.Life2PairLaw;
        double beta = Chem2.Beta(Chem2.Level(25));
        try
        {
            foreach (int law in laws)
            {
                P.Life2PairLaw = law;
                Console.WriteLine($"pairing law {law} (Life2PairLaw), Life2Pair {P.Life2Pair.ToString(inv)}, Life2Fidelity {P.Life2Fidelity.ToString(inv)}, proofreading passes {P.Life2Proofread}, 25 °C (β {beta.ToString("0.000", inv)})");
                Console.WriteLine("seed  faithful  alphabet  pool  err D1   D    err D   err D+pr  genome  p·L     heredity  compile");
                var pl = new List<double>();
                int ok = 0;
                foreach (int seed in seeds)
                {
                    var sw = Stopwatch.StartNew();
                    var chem = new Chemistry(seed);
                    var c = Chem2.Of(chem);
                    var all = Enumerable.Range(0, Chem2.L).Select(f => (byte)f).ToArray();
                    int faithful = all.Count(t => c.CopyError(new[] { t }, all, 1, beta) < 0.1);
                    var set = Seeds.Compile(chem, effort);
                    var A = set.Alphabet;
                    var pool = A.Concat(A.Select(f => (byte)c.Comp[f])).Distinct().OrderBy(f => f).ToArray();
                    var pol = new ProteinType(c, set.Genes["Pol"]);
                    double rho = 0;
                    int held = 0;
                    foreach (var p in pol.Pockets) if (p.Side != ProteinType.Tm) { held++; rho = Math.Max(rho, p.R.Rho); }
                    double d = c.Discrimination(rho);
                    int passes = held >= 2 ? P.Life2Proofread : 0;
                    var genome = set.Phototroph;
                    double e1 = c.CopyError(genome, pool, 1, beta), ed = c.CopyError(genome, pool, d, beta), ep = c.CopyError(genome, pool, d, beta, passes);
                    double pL = ep * genome.Length;
                    bool her = pL <= 1;
                    if (her) ok++;
                    pl.Add(pL);
                    string L(byte[] x) => string.Concat(x.Select(f => "0123456789abcdef"[f]));
                    Console.WriteLine($"{seed,4}  {faithful,8}  {L(A),-8}  {L(pool),-4}  {e1.ToString("0.0000", inv),6}  {d.ToString("0.0", inv),4}  {ed.ToString("0.00000", inv),7}  {ep.ToString("0.00000", inv),8}  {genome.Length,6}  {pL.ToString("0.000", inv),6}  {(her ? "yes" : "no"),8}  {sw.ElapsedMilliseconds} ms");
                    if (Array.IndexOf(args, "--pairs") >= 0)
                        Console.WriteLine("      pairs: " + string.Join(" ", all.Select(f => $"{f:x}→{c.Comp[f]:x}({c.Pair[f, c.Comp[f]].ToString("0.0", inv)})")));
                }
                pl.Sort();
                Console.WriteLine($"law {law}: heredity sustained in {ok} of {seeds.Length} seeds ({(100.0 * ok / Math.Max(1, seeds.Length)).ToString("0", inv)} %); substitutions per genome copy: median {pl[pl.Count / 2].ToString("0.000", inv)}, best {pl[0].ToString("0.000", inv)}, worst {pl[^1].ToString("0.000", inv)}");
            }
        }
        finally { P.Life2PairLaw = law0; }
    }
}
