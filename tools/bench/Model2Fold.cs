using System;
using System.Collections.Generic;
using System.Linq;
using Primordium.Model2;

namespace Primordium;

// --fold-stats [--seeds 1-8] [--chains 20000] [--alphabet 4|16]: what random chains of model 2 fold into
// (docs/DESIGN-LIFE-MODEL-2.md §4.9) — the share with a pocket, membrane chains, catalysis downhill, coupled
// uphill binds, motors, pigments, channels; and the effect of point substitutions on the best pocket
// (neutral: |ΔΔG| < 0.5; destroyed: the best pocket's ligand no longer bound). Calibrates the folding laws.
public static class Model2Fold
{
    public static void Stats(string[] args)
    {
        string Arg(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        int chains = int.Parse(Arg("--chains", "20000")), alphabet = int.Parse(Arg("--alphabet", "16"));
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine($"fold stats: {chains} random chains of 30–120 residues per seed, alphabet {alphabet} letters");
        if (Array.IndexOf(args, "--species") >= 0)
        {
            // Per species: the share of random windows that bind it below the cut (whatever else they bind).
            foreach (int seed in Batch.ParseSeeds(Arg("--seeds", "1-8")))
            {
                var ch = new Chemistry(seed);
                var c2 = Chem2.Of(ch);
                var r2 = new SimRng(seed);
                var hits = new int[Chemistry.S];
                var seq = new byte[3];
                for (int k = 0; k < chains; k++)
                {
                    for (int i = 0; i < 3; i++) seq[i] = (byte)r2.Next(alphabet);
                    var w = c2.PocketOf(seq, 0);
                    for (int s = 0; s < Chemistry.S; s++) if (c2.BindSpecies(w, s) < P.Life2Cut) hits[s]++;
                }
                Console.WriteLine($"seed {seed}: " + string.Join(" ", Enumerable.Range(0, Chemistry.S).Where(s => hits[s] > 0).Select(s => $"{s}{(s % 2 == 1 ? "*" : "")}({ch.AtomCount(s)}at,h{c2.LigH[s]:0.0}):{100.0 * hits[s] / chains:0.00}%")));
            }
            return;
        }
        if (Array.IndexOf(args, "--histogram") >= 0)
        {
            // Best ΔG of every window over the 32 species, all seeds pooled, in bins of 0.5.
            var hist = new SortedDictionary<int, long>();
            long all = 0;
            foreach (int seed in Batch.ParseSeeds(Arg("--seeds", "1-8")))
            {
                var c2 = Chem2.Of(new Chemistry(seed));
                var r2 = new SimRng(seed);
                var seq = new byte[3];
                for (int k = 0; k < chains; k++)
                {
                    for (int i = 0; i < 3; i++) seq[i] = (byte)r2.Next(alphabet);
                    var w = c2.PocketOf(seq, 0);
                    double g = double.MaxValue;
                    for (int s = 0; s < Chemistry.S; s++) g = Math.Min(g, c2.BindSpecies(w, s));
                    int bin = (int)Math.Floor(g * 2);
                    hist[bin] = hist.GetValueOrDefault(bin) + 1; all++;
                }
            }
            long cum = 0;
            foreach (var (bin, n) in hist) { cum += n; Console.WriteLine($"  dG {bin / 2.0,6:0.0}..{(bin + 1) / 2.0,5:0.0}: {100.0 * n / all,6:0.00}%  cumulative {100.0 * cum / all,6:0.00}%"); }
            return;
        }
        Console.WriteLine("seed  pocket  membrane  split↓  bind↓  bind↑coupled  coupled↓  motor  pigment  channel  window  mean pockets  neutral  destroyed");
        foreach (int seed in Batch.ParseSeeds(Arg("--seeds", "1-8")))
        {
            var chem = new Chemistry(seed);
            var c = Chem2.Of(chem);
            var rng = new SimRng(seed * 7919 + 3);
            int pocket = 0, membrane = 0, split = 0, bindDown = 0, bindUp = 0, coupled = 0, motor = 0, pigment = 0, channel = 0, window = 0;
            long pockets = 0, subs = 0, neutral = 0, destroyed = 0;
            for (int k = 0; k < chains; k++)
            {
                int len = 30 + rng.Next(91);
                var seq = new byte[len];
                for (int i = 0; i < len; i++) seq[i] = (byte)rng.Next(alphabet);
                var t = new ProteinType(c, seq);
                if (t.Pockets.Length > 0) pocket++;
                if (t.Crossings > 0) membrane++;
                pockets += t.Pockets.Length;
                bool sp = false, bd = false, bu = false, cp = false, mo = false, pg = false, ch = false;
                foreach (var a in t.Acts)
                {
                    switch (a.Kind)
                    {
                        case ProteinType.ActKind.Split: sp = true; if (a.Couple >= 0) cp = true; break;
                        case ProteinType.ActKind.Bind:
                            int de = chem.E[a.Species] + chem.E[a.Species2] - chem.E[chem.Combine[a.Species, a.Species2]];
                            if (de > 0) { bd = true; if (a.Couple >= 0) cp = true; } else bu = true;
                            break;
                        case ProteinType.ActKind.Motor: mo = true; break;
                        case ProteinType.ActKind.Pigment: pg = true; break;
                        case ProteinType.ActKind.Channel: ch = true; break;
                    }
                }
                if (sp) split++; if (bd) bindDown++; if (bu) bindUp++; if (cp) coupled++; if (mo) motor++; if (pg) pigment++; if (ch) channel++;
                // Does any pocket bind a window of another random chain (a promoter-like site)?
                if (t.Pockets.Length > 0)
                {
                    var other = new byte[3];
                    for (int i = 0; i < 3; i++) other[i] = (byte)rng.Next(alphabet);
                    foreach (var p in t.Pockets) if (Math.Min(c.BindWindow(p.R, other, 0), c.BindWindow(p.T, other, 0)) < P.Life2Cut) { window++; break; }
                }
                // Point substitutions on the best pocket.
                if (t.Pockets.Length > 0 && k % 10 == 0)
                {
                    var best = t.Pockets.OrderBy(p => Math.Min(p.G0R, p.G0T)).First();
                    double g0 = Math.Min(best.G0R, best.G0T);
                    for (int r = 0; r < 4; r++)
                    {
                        var m = (byte[])seq.Clone();
                        int at = rng.Next(len);
                        m[at] = (byte)((m[at] + 1 + rng.Next(alphabet - 1)) % alphabet);
                        var tm = new ProteinType(c, m);
                        double g1 = double.MaxValue;
                        foreach (var p in tm.Pockets)
                            for (int l = 0; l < p.N; l++)
                                if (p.Lig(l) == best.L0) g1 = Math.Min(g1, Math.Min(p.GR(l), p.GT(l)));
                        subs++;
                        if (g1 == double.MaxValue) destroyed++;
                        else if (Math.Abs(g1 - g0) < 0.5) neutral++;
                    }
                }
            }
            string F(double x) => (100.0 * x / chains).ToString("0.0", inv) + "%";
            Console.WriteLine($"{seed,4}  {F(pocket),6}  {F(membrane),8}  {F(split),6}  {F(bindDown),5}  {F(bindUp),12}  {F(coupled),8}  {F(motor),5}  {F(pigment),7}  {F(channel),7}  {F(window),6}  {(pockets / (double)chains).ToString("0.00", inv),12}  {(100.0 * neutral / Math.Max(1, subs)).ToString("0.0", inv),6}%  {(100.0 * destroyed / Math.Max(1, subs)).ToString("0.0", inv),8}%");
        }
    }
}
