using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Primordium.Model2;

// The seeded minimal cells of model 2 (player content, docs/DESIGN-LIFE-MODEL-2.md §9.2), compiled for the
// current seed's chemistry from functional specifications — roles bound to the world's species by its
// chemistry, never by hand:
//   carrier g     the ground monoatomic species (not the gas) whose excited state g* pockets bind best: the
//                 cells' charge (and the heterotroph's food, lying around charged)
//   alphabet      the letters most often found in strongly binding windows (cheap ones first), the gas excluded
//   promoter      a window of the alphabet that the polymerase's pocket binds
// Genes (each a functional spec, compiled by Compiler):
//   Pol   soluble; a pocket for the promoter window and one for g* (copies the genome, transcribes)
//   Pg    membrane; a hydrophobic pocket (pigment) coupled to a pocket for g: light charges g → g*
//   ChN   membrane; channels for the letters (and g), none for g* (the charge stays in)
//   Mot   membrane; inside a pocket for g* (the motor's stroke) and one binding g* better in T (fed: tumble);
//         outside a pocket binding g* better in R (food ahead: run) — a comparator of now and lately
//   ChF   membrane; a channel for g* (the food)
// F-1 (phototroph): Pol, Pg, ChN…  E-1 (chemotactic heterotroph): Pol, Mot, ChF, ChN…  E-1Δ: E-1 with the
// outer pocket of Mot destroyed (its window replaced, recompiled for everything else).
public sealed class SeedSet
{
    public int Carrier, Food;                  // g and g* (Food = g*)
    public byte[] Alphabet, Promoter;
    public readonly Dictionary<string, byte[]> Genes = new();
    public readonly Dictionary<string, double> Scores = new();
    public byte[] Phototroph, Heterotroph, Knockout;   // genomes (residues)
    public CreatureDesign PhototrophDesign, HeterotrophDesign, KnockoutDesign;
    public string Report;
    public double Total;   // how far the genes are from their specs (0: all met)
}

public static class Seeds
{
    static readonly ConditionalWeakTable<Chemistry, SeedSet> cache = new();

    public static SeedSet For(Chemistry chem, int effort = 6000) => cache.GetValue(chem, ch => Compile(ch, effort));

    // The seed cells for a chemistry: compiled for each of the `tries` best carriers, the set whose genes meet their
    // specs best (the polymerase and the pigment counting twice) is kept.
    public static SeedSet Compile(Chemistry chem, int effort, int tries = 3)
    {
        SeedSet best = null;
        for (int rank = 0; rank < Math.Max(1, tries); rank++)
        {
            var s = CompileFor(chem, effort, rank);
            if (s == null) break;
            if (best == null || s.Total < best.Total) best = s;
            if (best.Total < 0.5) break;
        }
        return best;
    }

    static SeedSet CompileFor(Chemistry chem, int effort, int rank)
    {
        var c = Chem2.Of(chem);
        var set = new SeedSet();
        // How often random windows bind each species (what pockets of this chemistry can hold at all), and how
        // often each letter lines a window that binds the carrier (chosen first) or anything.
        var rng = new SimRng(0xC0FFEE);
        var seq = new byte[3];
        var share = new double[Chemistry.S];
        var windows = new List<(byte[] seq, Window w)>();
        for (int k = 0; k < 8192; k++)
        {
            var q = new byte[3];
            for (int i = 0; i < 3; i++) q[i] = (byte)rng.Next(Chem2.L);
            var w = c.PocketOf(q, 0);
            windows.Add((q, w));
            for (int s = 0; s < Chemistry.S; s++) if (c.BindSpecies(w, s) < P.Life2Cut) share[s] += 1.0 / 8192;
        }
        // The carrier: a lone atom if any can be held (an excited compound falls apart, a lone atom only relaxes:
        // it lasts), a ground species with an excited state, not the gas, whose both states windows bind; a
        // bigger gap holds more.
        var carriers = Enumerable.Range(0, Chem2.L).Select(f => 2 * f).Where(s => s != chem.Gas && chem.PhotoUp[s] == s + 1 && chem.Gap[s + 1] > 0)
            .OrderByDescending(s => (share[s] > 0 && share[s + 1] > 0 ? 2 : 0) + (chem.AtomCount(s) == 1 ? 1 : 0))
            .ThenByDescending(s => Math.Min(share[s], share[s + 1]) * 100 + share[s + 1] * 20 + 0.05 * chem.Gap[s + 1]).ThenBy(s => s).ToList();
        if (rank >= carriers.Count) return null;
        int g = carriers[rank];
        set.Carrier = g; set.Food = g + 1;
        var count = new double[Chem2.L];
        foreach (var (q, w) in windows)
        {
            double bg = Math.Min(c.BindSpecies(w, g), c.BindSpecies(w, g + 1));
            if (bg < P.Life2Cut) foreach (var f in q) count[f] += 1;
        }
        // The alphabet: of the 8 letters most often lining windows that hold the carrier (with the most hydrophobic,
        // for crossings, and the most polar, for soluble chains; not the gas, not the carrier's own letter — the cell
        // keeps its carriers to hold its charge), the 5 that copy most faithfully among themselves and their
        // complements (the pairing law); the carrier-binders break ties.
        var letters = Enumerable.Range(0, Chem2.L).Where(f => 2 * f != chem.Gas && 2 * f != g).ToList();
        int hyd = letters.OrderByDescending(f => c.H[f]).ThenBy(f => f).First(), polar = letters.OrderBy(f => c.H[f]).ThenBy(f => f).First();
        var cand = letters.OrderByDescending(f => count[f]).ThenBy(f => f).Take(8).ToList();
        if (!cand.Contains(hyd)) cand.Add(hyd);
        if (!cand.Contains(polar)) cand.Add(polar);
        var alphabet = new List<int>();
        double bestErr = double.MaxValue;
        double beta25 = Chem2.Beta(Chem2.Level(25));
        int nc = cand.Count;
        double total = Math.Max(1, count.Sum());
        for (int mask = 0; mask < 1 << nc; mask++)
        {
            if (System.Numerics.BitOperations.PopCount((uint)mask) != 5) continue;
            var pick = Enumerable.Range(0, nc).Where(i => (mask >> i & 1) != 0).Select(i => (byte)cand[i]).ToArray();
            if (!pick.Any(f => c.H[f] >= c.TmH - 0.05) || !pick.Any(f => c.H[f] < 0.5)) continue;   // crossings and soluble chains possible
            var pool = pick.Concat(pick.Select(f => (byte)c.Comp[f])).Append((byte)(g / 2)).Distinct().ToArray();
            double err = c.CopyError(pick, pool, 3, beta25) - 0.05 * pick.Sum(f => count[f]) / total;
            if (err < bestErr) { bestErr = err; alphabet = pick.Select(f => (int)f).ToList(); }
        }
        if (alphabet.Count == 0) { alphabet = cand.Take(4).ToList(); if (!alphabet.Contains(hyd)) alphabet.Add(hyd); if (!alphabet.Contains(polar)) alphabet.Add(polar); }
        set.Alphabet = alphabet.Select(f => (byte)f).OrderBy(f => f).ToArray();
        var A = set.Alphabet;
        var report = new List<string>();
        string Name(int s) => chem.NameEn[s];


        byte[] Gene(string name, GeneSpec spec, long seed, byte[] letters = null, int mult = 1)
        {
            if (!spec.Needs.Any(x => x.Kind == Need.Kinds.Stable)) spec.Needs.Add(new Need { Kind = Need.Kinds.Stable, Strength = 4, Weight = 0.5 });   // well folded: lasts
            // a pocket for the charged carrier that took it apart would only make heat of the charge
            if (!spec.Needs.Any(x => x.Kind == Need.Kinds.NoSplit)) spec.Needs.Add(new Need { Kind = Need.Kinds.NoSplit, Species = set.Food, Weight = 0.5 });
            var s = Compiler.Compile(c, spec, letters ?? A, seed, effort * mult, out double score);
            set.Genes[name] = s; set.Scores[name] = score;
            report.Add($"{name}: score {score.ToString("0.00", CultureInfo.InvariantCulture)} [{Compiler.Explain(c, new ProteinType(c, s), spec)}] {new ProteinType(c, s).Detail(chem)}");
            return s;
        }

        var pol = new GeneSpec("Pol", 48);
        pol.Needs.Add(new Need { Kind = Need.Kinds.Soluble, Weight = 3 });
        pol.Needs.Add(new Need { Kind = Need.Kinds.Polymerase, Alphabet = A, Strength = -1.5 });
        pol.Needs.Add(Need.Pocket(ProteinType.In, set.Food));
        pol.Needs.Add(new Need { Kind = Need.Kinds.NoBarePigment, Weight = 0.5 });
        Gene("Pol", pol, 11, null, 3);   // the hardest: more search
        // The promoter: the window of the alphabet the polymerase binds best.
        byte[] promoter = null;
        {
            var pt = new ProteinType(c, set.Genes["Pol"]);
            double best = double.MaxValue;
            foreach (var p in pt.Pockets)
            {
                if (p.Side == ProteinType.Tm) continue;
                foreach (var w in new[] { p.R, p.T })
                {
                    double g0 = Compiler.BestPromoter(c, w, A, out var win);
                    if (g0 < best) { best = g0; promoter = win; }
                }
            }
            promoter ??= new[] { A[0], A[0], A[0] };
        }
        set.Promoter = promoter;

        var pg = new GeneSpec("Pg", 40);
        pg.Needs.Add(new Need { Kind = Need.Kinds.Membrane });
        pg.Needs.Add(new Need { Kind = Need.Kinds.Pigment, Species = g });
        pg.Needs.Add(new Need { Kind = Need.Kinds.NoChannel, Species = set.Food, Weight = 0.3 });
        pg.Needs.Add(new Need { Kind = Need.Kinds.NoMotor });
        pg.Needs.Add(new Need { Kind = Need.Kinds.NoBarePigment, Weight = 0.5 });
        Gene("Pg", pg, 12);

        // Pumps for the letters (two a gene), so the cell can gather its monomers; letters no pocket binds come in
        // only through the bare membrane (Life2Leak).
        var chans = new List<string>();
        // A pump for the ground carrier (the cell gathers what holds its charge); the letters come in through the bare
        // membrane (Life2Leak).
        var pumped = new List<int> { g };
        for (int k = 0; k < pumped.Count; k += 2)
        {
            var pu = new GeneSpec($"Pu{k / 2 + 1}", 44);
            pu.Needs.Add(new Need { Kind = Need.Kinds.Membrane });
            pu.Needs.Add(new Need { Kind = Need.Kinds.Pump, Species = pumped[k] });
            if (k + 1 < pumped.Count) pu.Needs.Add(new Need { Kind = Need.Kinds.Pump, Species = pumped[k + 1] });
            pu.Needs.Add(new Need { Kind = Need.Kinds.NoChannel, Species = set.Food });
            pu.Needs.Add(new Need { Kind = Need.Kinds.NoMotor });
            Gene(pu.Name, pu, 20 + k);
            chans.Add(pu.Name);
        }

        var mot = new GeneSpec("Mot", 48);
        mot.Needs.Add(new Need { Kind = Need.Kinds.Membrane });
        mot.Needs.Add(new Need { Kind = Need.Kinds.Motor, Species = set.Food });
        mot.Needs.Add(Need.Pocket(ProteinType.Out, set.Food, prefer: 1));
        mot.Needs.Add(Need.Pocket(ProteinType.In, set.Food, prefer: 2));
        Gene("Mot", mot, 13);
        // The knockout: the same motor without its outer pocket (recompiled from it with that need reversed).
        var ko = new GeneSpec("MotΔ", 48);
        ko.Needs.Add(new Need { Kind = Need.Kinds.Membrane, Weight = 3 });
        ko.Needs.Add(new Need { Kind = Need.Kinds.Motor, Species = set.Food, Weight = 3 });
        ko.Needs.Add(Need.Pocket(ProteinType.In, set.Food, prefer: 2));
        ko.Needs.Add(new Need { Kind = Need.Kinds.NoPocket, Side = ProteinType.Out, Weight = 2 });
        var koSeq = Knock(c, set.Genes["Mot"], ko, A, effort);
        set.Genes["MotΔ"] = koSeq; set.Scores["MotΔ"] = Compiler.Score(c, new ProteinType(c, koSeq), ko);
        report.Add($"MotΔ: score {set.Scores["MotΔ"].ToString("0.00", CultureInfo.InvariantCulture)} {new ProteinType(c, koSeq)} ({Diff(set.Genes["Mot"], koSeq)} residues changed)");

        var chf = new GeneSpec("ChF", 44);
        chf.Needs.Add(new Need { Kind = Need.Kinds.Membrane });
        chf.Needs.Add(new Need { Kind = Need.Kinds.Pump, Species = set.Food });
        chf.Needs.Add(new Need { Kind = Need.Kinds.NoMotor });
        Gene("ChF", chf, 14);

        byte[] Build(params string[] names)
        {
            var ts = names.Select(n => set.Genes[n]).ToList();
            var gen = Compiler.Genome(c, promoter, ts, A, 99);
            if (!Compiler.ReadsAs(c, gen, ts)) report.Add($"warning: the genome of {string.Join("+", names)} does not read as its genes");
            return gen;
        }
        var chanNames = chans.ToArray();
        set.Phototroph = Build(new[] { "Pol", "Pg" }.Concat(chanNames).ToArray());
        set.Heterotroph = Build(new[] { "Pol", "Mot", "ChF" }.Concat(chanNames).ToArray());
        set.Knockout = Build(new[] { "Pol", "MotΔ", "ChF" }.Concat(chanNames).ToArray());
        {
            var pool = A.Concat(A.Select(f => (byte)c.Comp[f])).Distinct().ToArray();
            var all = Enumerable.Range(0, Chem2.L).Select(f => (byte)f).ToArray();
            double beta = Chem2.Beta(Chem2.Level(25));
            report.Insert(0, $"copy error per residue at 25 °C (D 1 / 3 / 5): alphabet {c.CopyError(A, pool, 1, beta):0.000} / {c.CopyError(A, pool, 3, beta):0.000} / {c.CopyError(A, pool, 5, beta):0.000}; all letters {c.CopyError(all, all, 1, beta):0.000} / {c.CopyError(all, all, 3, beta):0.000} / {c.CopyError(all, all, 5, beta):0.000}");
        }
        report.Insert(0, "pairs: " + string.Join(" ", Enumerable.Range(0, Chem2.L).Select(f => $"{f:x}→{c.Comp[f]:x}({c.Pair[f, c.Comp[f]].ToString("0.0", CultureInfo.InvariantCulture)})")) + "; alphabet letters " + string.Concat(A.Select(f => "0123456789abcdef"[f])));
        report.Insert(0, $"chemistry {chem.Model}: carrier {Name(g)} (g* {Name(set.Food)}, gap {chem.Gap[set.Food]}), alphabet {string.Join(",", A.Select(f => Name(2 * f)))}, promoter {string.Concat(promoter.Select(f => "0123456789abcdef"[f]))}, binding cost {c.Eps0.ToString("0.00", CultureInfo.InvariantCulture)}, crossing h ≥ {c.TmH.ToString("0.00", CultureInfo.InvariantCulture)}");
        foreach (var (label, gen) in new[] { ("F-1", set.Phototroph), ("E-1", set.Heterotroph), ("E-1Δ", set.Knockout) })
        {
            var units = GeneTable.Parse(c, gen);
            report.Add($"{label}: {gen.Length} residues, {units.Count} units: " + string.Join(" ", units.Select(u => u.Type.Len)));
            var types = units.Select(u => u.Type).Distinct().ToArray();
            var table = GeneTable.Build(c, gen, types);
            for (int u = 0; u < table.Units.Length; u++)
                report.Add($"   unit {u} at {table.Units[u].Start}: promoter " + string.Join(", ", table.Promoter[u].Select(b => $"type {b.Type}{(b.Pol ? " (pol)" : "")} pocket {b.Pocket} {b.GR.ToString("0.0", CultureInfo.InvariantCulture)}/{b.GT.ToString("0.0", CultureInfo.InvariantCulture)}"))
                    + "; blockers " + string.Join(", ", table.Block[u].Select(b => $"type {b.Type} {Math.Min(b.GR, b.GT).ToString("0.0", CultureInfo.InvariantCulture)}")));
        }
        set.Total = 2 * (set.Scores["Pol"] + set.Scores["Pg"]) + set.Scores["Mot"] + set.Scores["ChF"];
        report.Insert(0, $"carrier candidate {rank + 1}: total score {set.Total.ToString("0.00", CultureInfo.InvariantCulture)}");
        set.Report = string.Join("\n", report);
        set.PhototrophDesign = Design(c, set, "F-1", set.Phototroph, Loc.T("Model 2 phototroph: a pigment charging the carrier, channels for its letters.", "Фототроф модели 2: пигмент заряжает переносчик, каналы для своих букв."));
        set.HeterotrophDesign = Design(c, set, "E-1", set.Heterotroph, Loc.T("Model 2 chemotactic heterotroph: eats the charged carrier through a channel, runs while food ahead is richer than inside.", "Хемотактический гетеротроф модели 2: ест заряженный переносчик через канал, бежит, пока впереди еды больше, чем внутри."));
        set.KnockoutDesign = Design(c, set, "E-1Δ", set.Knockout, Loc.T("E-1 with the motor's outer (receptor) pocket knocked out.", "Э-1 с выключенным внешним (рецепторным) карманом мотора."));
        return set;
    }

    // Recompile a gene from its sequence for a changed spec (few changes: low temperature from the start).
    static byte[] Knock(Chem2 c, byte[] from, GeneSpec spec, byte[] alphabet, int effort)
    {
        var rng = new SimRng(0xD17A);
        var seq = (byte[])from.Clone();
        double cur = Compiler.Score(c, new ProteinType(c, seq), spec);
        for (int it = 0; it < effort && cur > 0; it++)
        {
            var next = (byte[])seq.Clone();
            next[rng.Next(next.Length)] = alphabet[rng.Next(alphabet.Length)];
            double s = Compiler.Score(c, new ProteinType(c, next), spec) + 0.02 * Diff(from, next);
            if (s <= cur) { seq = next; cur = s; }
        }
        return seq;
    }

    static int Diff(byte[] a, byte[] b) { int d = 0; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) d++; return d; }

    // A design for a genome: its body holds the monomers for the genome and for a working set of proteins
    // (about 25 copies of every gene), carriers g (half of them charged) and a little of every letter more.
    static CreatureDesign Design(Chem2 c, SeedSet set, string name, byte[] genome, string description)
    {
        var chem = c.Chem;
        var units = GeneTable.Parse(c, genome);
        var letters = new double[Chem2.L];
        foreach (var r in genome) { letters[r] += 1; letters[c.Comp[r]] += 1; }
        foreach (var u in units) for (int f = 0; f < Chem2.L; f++) letters[f] += 25.0 * u.Type.Letters[f];
        double q = World.ResidueRaw / Qty.One;
        var d = new CreatureDesign { Name = name, Description = description, Model = "chem", Genome = new ChemModel().Describe(GeneTable.Pack(genome)) };
        long links = 2L * genome.Length;
        foreach (var u in units) links += 25L * u.Type.Len;
        for (int f = 0; f < Chem2.L; f++)
        {
            int n = (int)Math.Ceiling(letters[f] * q);
            if (n == 0) continue;
            n += 2;
            if (2 * f == set.Carrier) n += 6;
            d.Body[(2 * f).ToString(CultureInfo.InvariantCulture)] = n;
        }
        if (!d.Body.ContainsKey(set.Carrier.ToString(CultureInfo.InvariantCulture))) d.Body[set.Carrier.ToString(CultureInfo.InvariantCulture)] = 6;
        // The charge: the bonds of all of it, and a reserve of a third more (it must fit in the ground carriers' excitation).
        double cost = links * (double)World.LinkRaw / Qty.One / Math.Max(0.05, P.Life2LinkEff);
        int gNeed = (int)Math.Ceiling(cost * 1.5 / chem.Gap[set.Food]) + 4;
        int gHave = d.Body[set.Carrier.ToString(CultureInfo.InvariantCulture)];
        if (gHave < gNeed) d.Body[set.Carrier.ToString(CultureInfo.InvariantCulture)] = gNeed;
        double room = 0;
        foreach (var (k, n) in d.Body) { int s = int.Parse(k, CultureInfo.InvariantCulture); if (chem.PhotoUp[s] >= 0) room += n * chem.Gap[chem.PhotoUp[s]]; }
        d.Energy = (float)Math.Floor(Math.Min(0.8 * room, cost * 1.5 + 80));
        return d;
    }
}
