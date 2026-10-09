using System;
using System.Collections.Generic;
using System.Linq;

namespace Primordium.Model2;

// Seeded cells are player content written as functional specifications (docs/DESIGN-LIFE-MODEL-2.md §9): the
// compiler searches, by deterministic annealing from a fixed seed, for a sequence whose fold — the very
// function the world uses (ProteinType) — meets the requirements in the current seed's chemistry. The world
// sees only the sequence. Requirements are about physics (a pocket of this side for this molecule, a
// crossing, a coupled pigment…), never a function picked from a table.
public sealed class Need
{
    public enum Kinds { Membrane, Soluble, Pocket, Channel, Pigment, Motor, Polymerase, NoChannel, NoPocket, NoMotor, Pump, Stable, NoBarePigment, NoSplit, Unblocked,
        Contact,      // a homotypic contact (array) at least Strength in both R–R and T–T, their difference at most Margin
        Site,         // a site another chain excites (trans), on Side, whose excitation shifts ΔG_RT by Strength or more in the direction Prefer (1: to R, 2: to T)
        Modifies,     // excites Target's site TargetSite (its window held in the conformation Prefer of the target — 1 R, 2 T — at Strength, the other face Margin weaker)
        NoLinks,      // excites no site of the chains Others (itself if null) and is excited by none of them — besides what other needs ask
        Balanced,     // every inner pocket holds its ligands about as well in R as in T (|ΔG_R − ΔG_T| ≤ Margin): what is inside does not turn it
        Poise,        // the chain's own log-odds of T (unmodified, per copy) at outside food Species = Strength (±Margin), its outer pockets bound at concentration Conc
        NoWindow,     // no pocket holds the window Window (the promoter: it would transcribe or repress every gene)
        ModifiedBy,   // a site of the chain is excited by Binder (its window held in the target's conformation Prefer)
        DigestCoupled,    // an outer split of Species whose energy a carrier pocket inside takes
        Harvests }        // an outer pocket for the excited Species passing its excitation to the ground carrier Species2 inside
    public Kinds Kind;
    public int Side = -1;          // Pocket: ProteinType.In/Out/Tm (−1 any)
    public int Species = -1;       // Pocket, Channel, Motor: the molecule; Pigment: the ground carrier it charges
    public int Prefer;             // Pocket: 0 no preference, 1 binds better in R, 2 better in T (by Margin)
    public double Margin = 1.0;
    public double Strength = -1.5; // Pocket, Polymerase: ΔG at least this strong (in its better conformation)
    public byte[] Window;          // Polymerase: the promoter window it must bind (null: any window of Alphabet)
    public byte[] Alphabet;
    public ProteinType Binder;     // Unblocked: the polymerase whose pockets must not hold the transcript's start (null: the chain itself)
    public ProteinType Target;     // Modifies: the chain whose site it excites
    public int TargetSite;         // Modifies: which site of Target
    public ProteinType[] Others;   // NoLinks: the other chains of the cell
    public double Conc;            // Poise: the outside concentration (per room) the outer pockets meet
    public int Except = -1;        // NoPocket: a ligand that is allowed
    public int Species2 = -1;      // Harvests: the ground carrier
    public int[] AnyOf;            // Harvests: any of these excited kinds will do
    public double Hold;            // Site: (< 0) a pocket of some chain should be able to hold the window around the site at least this strongly in T
    public double Weight = 1;
    public int Count = 1;          // Pocket: how many such pockets
    public static Need Pocket(int side, int s, int prefer = 0, double strength = -1.5) => new() { Kind = Kinds.Pocket, Side = side, Species = s, Prefer = prefer, Strength = strength };
}

public sealed class GeneSpec
{
    public string Name;
    public int Length = 40;
    public readonly List<Need> Needs = new();
    public GeneSpec(string name, int length) { Name = name; Length = length; }
}

public static class Compiler
{
    // How far a fold is from the spec: 0 when every need is met.
    // What each need still misses (for reports).
    public static string Explain(Chem2 c, ProteinType t, GeneSpec g) =>
        string.Join(" ", g.Needs.Select(n => $"{n.Kind}{(n.Species >= 0 ? ":" + n.Species : "")}={Miss(c, t, n):0.00}"));

    public static double Score(Chem2 c, ProteinType t, GeneSpec g)
    {
        double bad = 0;
        foreach (var n in g.Needs) bad += n.Weight * Miss(c, t, n);
        // No terminator inside the transcript (it would cut it short).
        for (int i = 0; i < t.Len; i++) if (GeneTable.Hairpin(c, t.Seq, i) > 0) bad += 2;
        return bad;
    }

    static double Miss(Chem2 c, ProteinType t, Need n)
    {
        var chem = c.Chem;
        switch (n.Kind)
        {
            case Need.Kinds.Membrane:
            {
                if (t.Crossings > 0) return 0;
                double best = 0;
                for (int i = 0; i + 7 <= t.Len; i++) { double h = 0; for (int j = i; j < i + 7; j++) h += c.H[t.Seq[j]] / 7; best = Math.Max(best, h); }
                return 1 + Math.Max(0, c.TmH - best) * 5;
            }
            case Need.Kinds.Soluble: return t.Crossings;
            case Need.Kinds.Pocket:
            {
                // The Count best pockets (each its best ligand slot for the species); a missing one costs as a window.
                var each = new List<double>();
                foreach (var p in t.Pockets)
                {
                    if (n.Side >= 0 && p.Side != n.Side) continue;
                    double best = double.MaxValue;
                    for (int l = 0; l < p.N; l++)
                    {
                        if (p.Lig(l) != n.Species) continue;
                        double g = Math.Min(p.GR(l), p.GT(l)), miss = Math.Max(0, g - n.Strength);
                        double pref = n.Prefer == 1 ? p.GR(l) - p.GT(l) + n.Margin : n.Prefer == 2 ? p.GT(l) - p.GR(l) + n.Margin : 0;
                        best = Math.Min(best, miss + Math.Max(0, pref) * 0.5 + 0.1 * l);
                    }
                    if (best < double.MaxValue) each.Add(best);
                }
                each.Sort();
                double sum = 0, window = 1 + Math.Max(0, BestWindow(c, t, n.Species, n.Side) - n.Strength) * 0.25;
                for (int k = 0; k < Math.Max(1, n.Count); k++) sum += k < each.Count ? each[k] : window;
                return sum;
            }
            case Need.Kinds.Pump:
            {
                foreach (var a in t.Acts) if (a.Kind == ProteinType.ActKind.Pump && a.Species == n.Species) return 0;
                double bad = t.Crossings > 0 ? 0 : 1;
                bool chan = false;
                foreach (var a in t.Acts) if (a.Kind == ProteinType.ActKind.Channel && a.Species == n.Species) chan = true;
                return bad + (chan ? 0.5 : 1 + Math.Max(0, BestWindow(c, t, n.Species, ProteinType.Tm) - P.Life2Cut) * 0.25) + (t.CarrierPocket ? 0 : 0.5);
            }
            case Need.Kinds.Channel:
                foreach (var a in t.Acts) if (a.Kind == ProteinType.ActKind.Channel && a.Species == n.Species) return 0;
                return 1 + (t.Crossings > 0 ? 0 : 1) + Math.Max(0, BestWindow(c, t.Seq, n.Species) - P.Life2Cut) * 0.25;
            case Need.Kinds.NoSplit:   // a pocket that would take the molecule apart (a carrier's charge to heat)
            {
                double bad = 0;
                foreach (var a in t.Acts) if (a.Kind == ProteinType.ActKind.Split && a.Species == n.Species) bad += 1;
                return bad;
            }
            case Need.Kinds.NoBarePigment:   // a pigment with no carrier to take its photon only makes heat
            {
                double bad = 0;
                foreach (var a in t.Acts) if (a.Kind == ProteinType.ActKind.Pigment && a.Couple < 0) bad += 1;
                return bad;
            }
            case Need.Kinds.Stable: return Math.Max(0, n.Strength - t.DgFold) * 0.1;   // Strength: the folding energy wanted
            case Need.Kinds.Unblocked:   // the polymerase bound to the transcript's first windows would stand in its own way (GeneTable.Block)
            {
                var pol = n.Binder ?? t;
                double bad = 0;
                for (int at = 0; at < Math.Min(t.Len - Chem2.K, GeneTable.BlockSpan); at++)
                    foreach (var p in pol.Pockets)
                    {
                        if (p.Side == ProteinType.Tm) continue;
                        double g = Math.Min(c.BindWindow(p.R, t.Seq, at), c.BindWindow(p.T, t.Seq, at));
                        if (g < P.Life2Cut + 1) bad += 0.5 + Math.Max(0, P.Life2Cut + 1 - g) * 0.25;
                    }
                return bad;
            }
            case Need.Kinds.Contact:
            {
                if (t.ContactPocket >= 0)
                    return Math.Max(0, Math.Max(t.ContactRR, t.ContactTT) - n.Strength) + 0.5 * Math.Max(0, Math.Abs(t.ContactRR - t.ContactTT) - n.Margin);
                // none yet: how close the pockets come to holding a window of the chain face to face
                double best = 10;
                foreach (var p in t.Pockets)
                {
                    if (p.Side == ProteinType.Tm) continue;
                    for (int at = 0; at + Chem2.K < t.Len; at++)
                        if (t.Sides[at + 1] == p.Side) best = Math.Min(best, Math.Max(c.BindWindow(p.R, t.Seq, at), c.BindWindow(p.T, t.Seq, at + 1)));
                }
                return 1 + 0.25 * Math.Max(0, best - n.Strength);
            }
            case Need.Kinds.Site:
            {
                // the Count best trans sites on Side, each shifting ΔG_RT by Strength or more in the direction Prefer
                var each = new List<double>();
                for (int k = 0; k < t.Sites.Length; k++)
                {
                    if (t.SiteDonor[k] >= 0 || (n.Side >= 0 && t.Sides[t.Sites[k]] != n.Side)) continue;
                    double eff = t.DgRT[1 << k] - t.DgRT[0], signed = n.Prefer == 2 ? eff : -eff;
                    double miss = 4 * Math.Max(0, n.Strength - signed) + (n.Species >= 0 && t.SiteGap[k] > chem.Gap[n.Species] ? 2 : 0);   // the carrier must be able to pay its gap
                    // and a window around it that some pocket can hold well in T ([j, j+2]) and less in R ([j−1, j+1])
                    int j = t.Sites[k];
                    if (n.Hold < 0 && j >= 1 && j + 2 < t.Len)
                    {
                        double g = c.BestHold(t.Seq, j, out int holder);
                        Span<byte> tri = stackalloc byte[3];
                        tri[0] = (byte)(holder >> 8); tri[1] = (byte)(holder >> 4 & 15); tri[2] = (byte)(holder & 15);
                        double gR = c.BindWindow(c.PocketOf(tri, 0), t.Seq, j - 1);
                        miss += 0.25 * Math.Max(0, g - n.Hold) + 0.25 * Math.Max(0, g + 1 - gR);
                    }
                    each.Add(miss);
                }
                each.Sort();
                double sum = 0;
                for (int k = 0; k < Math.Max(1, n.Count); k++) sum += k < each.Count ? each[k] : 1.5;
                return sum;
            }
            case Need.Kinds.Modifies:
            {
                // TargetSite ≥ 0: that site; −1: every trans site of the target (its window held in the target's conformation Prefer)
                var target = n.Target;
                if (target == null || n.TargetSite >= target.Sites.Length) return 0;
                var links = ProteinType.ComputeLinks(c, t, target);
                double sum = 0;
                for (int k = 0; k < target.Sites.Length; k++)
                {
                    if (n.TargetSite >= 0 ? k != n.TargetSite : target.SiteDonor[k] >= 0) continue;
                    double miss = double.MaxValue;
                    foreach (var l in links)
                    {
                        if (l.Site != k) continue;
                        double heldR = Math.Min(l.GRR, l.GTR), heldT = Math.Min(l.GRT, l.GTT);
                        double held = n.Prefer == 2 ? heldT : heldR, other = n.Prefer == 2 ? heldR : heldT;
                        miss = Math.Max(0, held - n.Strength) + 0.5 * Math.Max(0, held + n.Margin - other);
                    }
                    if (miss == double.MaxValue)
                    {
                        // no link yet: how close a pocket on the site's side comes to holding its window, and a carrier pocket
                        int j = target.Sites[k], side = target.Sides[j], at = n.Prefer == 2 ? j : j - 1;
                        double best = 10;
                        foreach (var p in t.Pockets)
                            if (p.Side == side) best = Math.Min(best, Math.Min(c.BindWindow(p.R, target.Seq, at), c.BindWindow(p.T, target.Seq, at)));
                        if (best == 10)
                            for (int i = 0; i + Chem2.K <= t.Len; i++)
                                if (t.Sides[Math.Min(t.Len - 1, i + 1)] == side) best = Math.Min(best, c.BindWindow(c.PocketOf(t.Seq, i), target.Seq, at));
                        miss = 1 + (t.CarrierPocket ? 0 : 0.5) + 0.25 * Math.Max(0, best - n.Strength);
                    }
                    sum += miss;
                }
                return sum;
            }
            case Need.Kinds.ModifiedBy:   // the chain's site is excited by Binder holding its window in conformation Prefer (as Modifies, from the target's side)
            {
                if (n.Binder == null) return 0;
                double best = 1.5;
                foreach (var l in ProteinType.ComputeLinks(c, n.Binder, t))
                {
                    double heldR = Math.Min(l.GRR, l.GTR), heldT = Math.Min(l.GRT, l.GTT);
                    double held = n.Prefer == 2 ? heldT : heldR, other = n.Prefer == 2 ? heldR : heldT;
                    best = Math.Min(best, Math.Max(0, held - n.Strength) + 0.5 * Math.Max(0, held + n.Margin - other));
                }
                return best;
            }
            case Need.Kinds.Harvests:
            {
                foreach (var a in t.Acts) if (a.Kind == ProteinType.ActKind.Harvest && (a.Species == n.Species || (n.AnyOf != null && Array.IndexOf(n.AnyOf, a.Species) >= 0))) return 0;
                bool outer = false, inner = false;
                foreach (var p in t.Pockets)
                    for (int l = 0; l < p.N; l++)
                    {
                        if (p.Side == ProteinType.Out && (p.Lig(l) == n.Species || (n.AnyOf != null && Array.IndexOf(n.AnyOf, p.Lig(l)) >= 0))) outer = true;
                        if (p.Side != ProteinType.Out && p.Lig(l) == n.Species2) inner = true;
                    }
                return 1 + (outer ? 0 : 0.5) + (inner ? 0 : 0.5) + (t.Crossings > 0 ? 0 : 0.5);
            }
            case Need.Kinds.DigestCoupled:
            {
                bool any = false;
                foreach (var a in t.Acts)
                    if (a.Kind == ProteinType.ActKind.Digest && a.Species == n.Species) { if (a.Couple >= 0) return 0; any = true; }
                return any ? 0.5 : 1;
            }
            case Need.Kinds.NoLinks:
            {
                double bad = 0;
                foreach (var a in t.Acts) if (a.Kind == ProteinType.ActKind.Modify) bad += 0.5;   // nor its own sites
                foreach (var o0 in n.Others ?? new ProteinType[] { null })
                {
                    var o = o0 ?? t;
                    foreach (var l in ProteinType.ComputeLinks(c, t, o)) if (o != n.Target || (n.TargetSite >= 0 && l.Site != n.TargetSite)) bad += 0.5 + 0.25 * Math.Max(0, P.Life2Cut - Math.Min(Math.Min(l.GRR, l.GRT), Math.Min(l.GTR, l.GTT)));
                    if (o != t) foreach (var l in ProteinType.ComputeLinks(c, o, t)) bad += 0.5 + 0.25 * Math.Max(0, P.Life2Cut - Math.Min(Math.Min(l.GRR, l.GRT), Math.Min(l.GTR, l.GTT)));
                }
                return bad;
            }
            case Need.Kinds.Balanced:
            {
                double bad = 0;
                foreach (var p in t.Pockets)
                    if (p.Side == ProteinType.In)
                        for (int l = 0; l < p.N; l++) bad += 0.5 * Math.Max(0, Math.Abs(p.GR(l) - p.GT(l)) - n.Margin);
                return bad;
            }
            case Need.Kinds.Poise:
            {
                double beta = Chem2.Beta(Chem2.Level(25)), lam = beta * t.DgRT[0];
                foreach (var p in t.Pockets)
                    if (p.Side == ProteinType.Out)
                        for (int l = 0; l < p.N; l++)
                            if (p.Lig(l) == n.Species)
                                lam += DetMath.Log((1 + n.Conc * DetMath.Exp(-beta * p.GT(l)) / P.Life2Kd0) / (1 + n.Conc * DetMath.Exp(-beta * p.GR(l)) / P.Life2Kd0));
                return Math.Max(0, Math.Abs(lam - n.Strength) - n.Margin);
            }
            case Need.Kinds.NoWindow:
            {
                if (n.Window == null) return 0;
                double bad = 0;
                foreach (var p in t.Pockets)
                {
                    if (p.Side == ProteinType.Tm) continue;
                    double g = Math.Min(c.BindWindow(p.R, n.Window, 0), c.BindWindow(p.T, n.Window, 0));
                    if (g < P.Life2Cut + 0.5) bad += 0.5 + 0.25 * (P.Life2Cut + 0.5 - g);
                }
                return bad;
            }
            case Need.Kinds.NoMotor:
            {
                double bad = 0;
                foreach (var a in t.Acts) if (a.Kind == ProteinType.ActKind.Motor) bad += 1;
                return bad;
            }
            case Need.Kinds.NoChannel:
                foreach (var a in t.Acts) if (a.Kind == ProteinType.ActKind.Channel && a.Species == n.Species) return 1;
                return 0;
            case Need.Kinds.NoPocket:
            {
                double bad = 0;
                foreach (var p in t.Pockets)
                    if (n.Side < 0 || p.Side == n.Side)
                        for (int l = 0; l < p.N; l++) if ((n.Species < 0 || p.Lig(l) == n.Species) && p.Lig(l) != n.Except) bad += 1;
                return bad;
            }
            case Need.Kinds.Pigment:
            {
                // Met; what its photon has beyond the carrier's gap is heat (a pigment matched to the carrier wastes less).
                double waste = double.MaxValue;
                foreach (var a in t.Acts)
                    if (a.Kind == ProteinType.ActKind.Pigment && a.CoupleSpecies == n.Species)
                        waste = Math.Min(waste, Math.Max(0, a.Work - chem.Gap[chem.PhotoUp[n.Species]]) / Math.Max(1, chem.Gap[chem.PhotoUp[n.Species]]));
                if (waste < double.MaxValue) return 0.3 * waste;
                bool pig = false, holds = false;
                foreach (var a in t.Acts) if (a.Kind == ProteinType.ActKind.Pigment) pig = true;
                foreach (var p in t.Pockets) for (int l = 0; l < p.N; l++) if (p.Lig(l) == n.Species && p.Side != ProteinType.Out) holds = true;
                return (pig ? 0.5 : 1) + (holds ? 0.3 : 0.5 + Math.Max(0, Math.Min(BestWindow(c, t, n.Species, ProteinType.In), BestWindow(c, t, n.Species, ProteinType.Tm)) - P.Life2Cut) * 0.25);
            }
            case Need.Kinds.Motor:
                foreach (var a in t.Acts) if (a.Kind == ProteinType.ActKind.Motor && a.Species == n.Species) return 0;
                return 1 + (t.Crossings > 0 ? 0 : 1) + Math.Max(0, BestWindow(c, t.Seq, n.Species) - P.Life2Cut) * 0.25;
            case Need.Kinds.Polymerase:
            {
                double bad = t.CarrierPocket ? 0 : 1;
                double best = double.MaxValue;
                foreach (var p in t.Pockets)
                {
                    if (p.Side == ProteinType.Tm) continue;
                    best = Math.Min(best, Math.Min(BindPromoter(c, p.R, n), BindPromoter(c, p.T, n)));
                }
                if (best == double.MaxValue)
                {
                    // No pocket yet: how close the best window of the chain comes to binding a promoter.
                    for (int i = 0; i + Chem2.K <= t.Len; i++) best = Math.Min(best, BindPromoter(c, c.PocketOf(t.Seq, i), n));
                    return bad + 1 + Math.Max(0, best - n.Strength) * 0.25;
                }
                return bad + Math.Max(0, best - n.Strength);
            }
        }
        return 0;
    }

    // The promoter window a pocket binds: the need's own, or (none given) the best 3-letter window of its alphabet.
    static double BindPromoter(Chem2 c, in Window w, Need n)
    {
        if (n.Window != null) return c.BindWindow(w, n.Window, 0);
        return BestPromoter(c, w, n.Alphabet, out _);
    }

    public static double BestPromoter(Chem2 c, in Window w, byte[] alphabet, out byte[] window)
    {
        double best = double.MaxValue;
        Span<byte> p = stackalloc byte[3];
        window = new byte[3];
        foreach (var x in alphabet) foreach (var y in alphabet) foreach (var z in alphabet)
        {
            p[0] = x; p[1] = y; p[2] = z;
            double g = c.BindWindow(w, p, 0);
            if (g < best) { best = g; window[0] = x; window[1] = y; window[2] = z; }
        }
        return best;
    }

    static double BestWindow(Chem2 c, byte[] seq, int s)
    {
        double best = double.MaxValue;
        for (int i = 0; i + Chem2.K <= seq.Length; i++) best = Math.Min(best, c.BindSpecies(c.PocketOf(seq, i), s));
        return best;
    }

    // The best window for s on a side (−1 any; a window's side is its middle residue's), in R or T.
    static double BestWindow(Chem2 c, ProteinType t, int s, int side)
    {
        double best = double.MaxValue;
        var seq = t.Seq;
        for (int i = 0; i + Chem2.K <= seq.Length; i++)
        {
            if (side >= 0 && t.Sides[i + 1] != side) continue;
            best = Math.Min(best, c.BindSpecies(c.PocketOf(seq, i), s));
        }
        return best == double.MaxValue ? 10 : best;
    }

    // Anneal a sequence of the spec's length over `alphabet` (letters 0–15) from a fixed seed.
    public static byte[] Compile(Chem2 c, GeneSpec g, byte[] alphabet, long seed, int iterations, out double score, int restarts = 4)
    {
        var rng = new SimRng(seed);
        byte[] best = null;
        double bestScore = double.MaxValue;
        for (int restart = 0; restart < restarts && bestScore > 0; restart++)
        {
            var seq = new byte[g.Length];
            for (int i = 0; i < seq.Length; i++) seq[i] = alphabet[rng.Next(alphabet.Length)];
            double cur = Score(c, new ProteinType(c, seq), g);
            double temp = 1.0;
            double cool = Math.Pow(0.002, 1.0 / Math.Max(1, iterations));
            for (int it = 0; it < iterations && cur > 0; it++, temp *= cool)
            {
                var next = (byte[])seq.Clone();
                int moves = 1 + (rng.Next(4) == 0 ? 1 : 0);
                for (int m = 0; m < moves; m++) next[rng.Next(next.Length)] = alphabet[rng.Next(alphabet.Length)];
                double s = Score(c, new ProteinType(c, next), g);
                if (s <= cur || rng.NextDouble() < DetMath.Exp(-(s - cur) / temp)) { seq = next; cur = s; }
                if (cur < bestScore) { bestScore = cur; best = (byte[])seq.Clone(); }
            }
            if (cur < bestScore) { bestScore = cur; best = (byte[])seq.Clone(); }
        }
        score = bestScore;
        return best;
    }

    // A whole genome: units of promoter + transcript + terminator (a hairpin of the alphabet's letters and
    // their complements). Returns the residues.
    public static byte[] Genome(Chem2 c, byte[] promoter, IReadOnlyList<byte[]> transcripts, byte[] alphabet, long seed)
    {
        var rng = new SimRng(seed ^ 0x7E2);
        var res = new List<byte>();
        // Stem letters: of the alphabet, those with a strong partner (in the alphabet if there are such).
        var pairing = alphabet.Where(x => c.Pairs(x, c.Comp[x]) && alphabet.Contains((byte)c.Comp[x])).ToArray();
        if (pairing.Length == 0) pairing = alphabet.Where(x => c.Pairs(x, c.Comp[x])).ToArray();
        if (pairing.Length == 0) pairing = Enumerable.Range(0, Chem2.L).Where(x => c.Pairs(x, c.Comp[x])).Select(x => (byte)x).ToArray();
        if (pairing.Length == 0) pairing = alphabet;
        foreach (var t in transcripts)
        {
            // stem, loop of 4, reverse complement of the stem — drawn until the genome read so far has exactly the
            // units wanted (no hairpin straddling the transcript's end, none reaching back into it)
            var unit = new List<byte>();
            for (int tries = 0; tries < 200; tries++)
            {
                unit.Clear();
                unit.AddRange(promoter);
                unit.AddRange(t);
                var stem = new byte[GeneTable.Stem];
                for (int k = 0; k < stem.Length; k++) stem[k] = pairing[rng.Next(pairing.Length)];
                unit.AddRange(stem);
                for (int k = 0; k < 4; k++) unit.Add(alphabet[rng.Next(alphabet.Length)]);
                for (int k = stem.Length - 1; k >= 0; k--) unit.Add((byte)c.Comp[stem[k]]);
                var all = res.Concat(unit).ToArray();
                var units = GeneTable.Parse(c, all);
                if (units.Count == res.Count(_ => false) + Count(c, res) + 1 && units[^1].Transcript.AsSpan().SequenceEqual(t) && units.Take(units.Count - 1).Select(u => u.End - u.Begin).SequenceEqual(Lengths(c, res))) break;
            }
            res.AddRange(unit);
        }
        if (res.Count % 2 != 0) res.Add(alphabet[0]);
        while (res.Count < 2 * Primordium.Genome.MinLen) res.Add(alphabet[0]);
        return res.ToArray();
    }

    static int Count(Chem2 c, List<byte> res) => res.Count == 0 ? 0 : GeneTable.Parse(c, res.ToArray()).Count;
    static IEnumerable<int> Lengths(Chem2 c, List<byte> res) => res.Count == 0 ? Array.Empty<int>() : GeneTable.Parse(c, res.ToArray()).Select(u => u.End - u.Begin).ToArray();

    // Whether a genome reads as exactly these transcripts.
    public static bool ReadsAs(Chem2 c, byte[] genome, IReadOnlyList<byte[]> transcripts)
    {
        var units = GeneTable.Parse(c, genome);
        if (units.Count != transcripts.Count) return false;
        for (int k = 0; k < units.Count; k++) if (!units[k].Transcript.AsSpan().SequenceEqual(transcripts[k])) return false;
        return true;
    }
}
