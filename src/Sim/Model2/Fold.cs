using System;
using System.Collections.Generic;

namespace Primordium.Model2;

// What a chain of residues does, computed from its sequence (docs/DESIGN-LIFE-MODEL-2.md §4) by general
// laws only — there is no table of motifs or functions:
//   membrane   a 7-residue window hydrophobic (mean h ≥ Chem2.TmH, set by Life2TmShare) and little charged (mean |q| ≤ Life2TmQ)
//              crosses the membrane; the loops between crossings alternate sides, the more positive ones inside
//   pockets    3-residue windows whose best ligand (one of the 32 species) binds with ΔG < Life2Cut, the
//              strongest first, not overlapping, at most max(1, ℓ/12); each keeps its 3 best ligands
//   R and T    two conformations: a pocket formed by window [i, i+2] in R is formed by [i+1, i+3] in T (the
//              register shift), so its affinities differ by themselves; the chain's own preference
//              ΔG_RT = Life2EpsC·Σ(−1)^i·h_i·b_i/√ℓ (L = e^{βΔG_RT}, MWC)
//   sites      up to two exposed residues (h < 0.45) within 12 of a pocket for an excited carrier: excited
//              ("phosphorylated") they change b_i and with it ΔG_RT
//   stability  ΔG_fold = Life2EpsF·(Σ_out h_i·b_i − h̄b̄·ℓ_out) − Life2EpsS·√ℓ; decay Life2Decay·e^{−β·clamp(ΔG_fold, ±4)}
// and from the pockets, by the same laws for every chain, what it can do (ProteinType.Acts):
//   channel    a pocket inside a crossing passes its ligands (down the gradient)
//   pump       a channel whose chain holds an excited carrier inside near it takes its ligand in (World.Intake:
//              the membrane work of model 1's uptake, CostIntake a molecule), at Life2Pump·θ_ligand·θ_carrier a copy
//   catalysis  a pocket for a molecule that splits downhill speeds the split (transition state: ρ·|ΔG|);
//              two pockets ≤ 12 apart for a and b that combine speed the bind
//   coupling   a pocket ≤ 12 from a catalysing one for a ground carrier g takes the released energy into g*
//              (downhill, if it is enough), one for an excited carrier pays an uphill bind
//   pigment    a hydrophobic pocket (h ≥ 0.5) catches photons; a pocket ≤ 12 from it for a ground carrier
//              takes the excitation (the photon is the pigment's gap)
//   motor      a membrane chain with a pocket inside for an excited carrier turns its charge into a push
//              (in R along the heading; in T a torque: tumbling)
//   window     any pocket may bind a window of the genome (promoters, operators: GeneTable)
// An excitation transfer one chain makes on another's site (ProteinType.LinksOf): the site, the donor's pocket that holds
// the window around it, its pocket for the excited carrier (and which ligand), and the four free energies of holding
// the window (donor face × target face: R–R, R–T, T–R, T–T).
public struct ModLink
{
    public int Site, Pocket, Carrier, CarrierLig, Species;
    public double GRR, GRT, GTR, GTT;
}

public sealed class ProteinType
{
    public const int MaxLig = 3, MaxSites = 2, In = 0, Out = 1, Tm = 2;
    public readonly byte[] Seq;
    public readonly ulong Hash;
    public int Len => Seq.Length;
    public readonly int[] Letters = new int[Chem2.L];   // residues by letter (its matter)
    public int Crossings;                                // membrane crossings (0: soluble)
    public int InLength;                                 // residues on the inner side (a motor's soluble leg)
    public bool[] InMembrane;                            // which residues cross the membrane
    public byte[] Sides;                                 // the side of every residue (In, Out, Tm)
    public Pocket[] Pockets = Array.Empty<Pocket>();
    public double[] DgRT = new double[4];                // the chain's own R/T preference by modification state
    public int[] Sites = Array.Empty<int>();             // residue positions of its modification sites
    public int[] SiteGap = Array.Empty<int>();           // their gaps (the energy an excitation leaves in them)
    public int[] SiteDonor = Array.Empty<int>();         // the pocket (index) whose carrier excites each site
    public double DgFold;
    public readonly List<Act> Acts = new();
    public bool CarrierPocket;                           // some pocket binds an excited carrier (what a polymerase needs)
    public readonly int[] OutLigands, InLigands;          // species its outer / inner pockets read (for sensing)
    readonly TypeLevel[] levels = new TypeLevel[Chem2.Levels];

    public struct Pocket
    {
        public int Pos, Side, N;
        public Window R, T;
        public int L0, L1, L2;          // ligand species (N of them)
        public double G0R, G1R, G2R, G0T, G1T, G2T;   // their ΔG in R and in T
        public int Lig(int k) => k == 0 ? L0 : k == 1 ? L1 : L2;
        public double GR(int k) => k == 0 ? G0R : k == 1 ? G1R : G2R;
        public double GT(int k) => k == 0 ? G0T : k == 1 ? G1T : G2T;
    }

    public enum ActKind : byte { Channel, Split, Bind, Pigment, Motor, Modify, Pump, Digest, Harvest }
    public struct Act
    {
        public ActKind Kind;
        public int Pocket, Lig;          // the acting pocket and its ligand index (Bind: the first partner)
        public int Pocket2, Lig2;        // Bind: the second partner
        public int Couple, CoupleLig;    // coupling pocket and ligand (−1: none)
        public int Species, Species2, CoupleSpecies;
        public int Site;                 // Modify: which site
        public double Work;              // Motor: work per cycle (energy); Pigment: photon energy
    }

    // Values that depend on temperature (β), per whole degree, made on first use.
    public sealed class TypeLevel
    {
        public double[] KR, KT;          // 1/K_d by pocket·MaxLig + ligand
        public double[] L = new double[4];
        public double[] Cat;             // per act: catalytic speed-up (Split/Bind), 0 otherwise
        public double Decay;             // per tick, before TempFactor
        public double ContactK, ContactShift;   // the homotypic contact: e^{−β·mean ΔG}/K_0 and β·(ΔG_RR − ΔG_TT)
        public double Beta;
    }

    public ProteinType(Chem2 c, byte[] seq)
    {
        Seq = seq;
        Hash = SeqKey.HashOf(seq);
        foreach (var f in seq) Letters[f]++;
        Fold(c);
        var outs = new List<int>(); var ins = new List<int>();
        foreach (var p in Pockets)
            for (int k = 0; k < p.N; k++)
            {
                var list = p.Side == Out ? outs : ins;
                if (!list.Contains(p.Lig(k))) list.Add(p.Lig(k));
            }
        OutLigands = outs.ToArray(); InLigands = ins.ToArray();
    }

    void Fold(Chem2 c)
    {
        int n = Seq.Length;
        var chem = c.Chem;
        // 1. Membrane crossings and sides.
        var tm = new bool[n];
        const int w = 7;
        for (int i = 0; i + w <= n; i++)
        {
            double h = 0, q = 0;
            for (int j = i; j < i + w; j++) { h += c.H[Seq[j]]; q += Math.Abs(c.Q[Seq[j]]); }
            if (h / w >= c.TmH && q / w <= P.Life2TmQ) for (int j = i; j < i + w; j++) tm[j] = true;
        }
        var loop = new int[n];   // which loop a residue is in (−1: in a crossing)
        int loops = 0, crossings = 0;
        for (int i = 0; i < n; i++)
        {
            if (tm[i]) { loop[i] = -1; if (i == 0 || !tm[i - 1]) { crossings++; } continue; }
            if (i == 0 || tm[i - 1]) loops++;
            loop[i] = loops - 1;
        }
        Crossings = crossings;
        // Loops alternate sides; the parity whose loops carry more positive charge is inside.
        double posEven = 0, posOdd = 0;
        for (int i = 0; i < n; i++) if (loop[i] >= 0 && c.Q[Seq[i]] > 0) { if (LoopParity(loop, i, tm) == 0) posEven += c.Q[Seq[i]]; else posOdd += c.Q[Seq[i]]; }
        int inParity = posEven >= posOdd ? 0 : 1;
        int Side(int i)
        {
            if (crossings == 0) return In;
            if (tm[i]) return Tm;
            return LoopParity(loop, i, tm) == inParity ? In : Out;
        }
        for (int i = 0; i < n; i++) if (crossings > 0 && Side(i) == In) InLength++;
        Sides = new byte[n];
        for (int i = 0; i < n; i++) Sides[i] = (byte)Side(i);
        if (crossings == 0) InLength = n;
        InMembrane = tm;

        // 2. Pockets: every window's best ligand in R and T; the strongest, not overlapping, at most max(1, ℓ/12).
        var cand = new List<(double g, int pos)>();
        int last = n - Chem2.K;
        var bestR = new double[Math.Max(0, last + 1)];
        for (int i = 0; i <= last; i++)
        {
            var wr = c.PocketOf(Seq, i);
            var wt = i + 1 <= last ? c.PocketOf(Seq, i + 1) : wr;
            double g = double.MaxValue;
            for (int s = 0; s < Chemistry.S; s++) g = Math.Min(g, Math.Min(c.BindSpecies(wr, s), c.BindSpecies(wt, s)));
            if (g < P.Life2Cut) cand.Add((g, i));
        }
        // Equal strength: a window that binds best is the R face of the pocket at its own place and the T face of the one
        // before it; which of the two forms is the chain's (a parity of its letters there), not always the first —
        // otherwise every pocket would hold its best ligand in T.
        int Tie(int pos) => (Seq[pos] ^ Seq[Math.Min(n - 1, pos + 2)]) & 1;
        cand.Sort((x, y) => x.g != y.g ? x.g.CompareTo(y.g) : Tie(x.pos) != Tie(y.pos) ? Tie(x.pos).CompareTo(Tie(y.pos)) : x.pos.CompareTo(y.pos));
        int max = Math.Max(1, n / 12);
        var used = new bool[n + 1];
        var pockets = new List<Pocket>();
        foreach (var (g, pos) in cand)
        {
            if (pockets.Count >= max) break;
            bool free = true;
            for (int j = pos; j <= Math.Min(n - 1, pos + Chem2.K); j++) if (used[j]) { free = false; break; }
            if (!free) continue;
            for (int j = pos; j <= Math.Min(n - 1, pos + Chem2.K); j++) used[j] = true;
            var p = new Pocket { Pos = pos, Side = Side(pos + 1), R = c.PocketOf(Seq, pos) };
            p.T = pos + 1 <= last ? c.PocketOf(Seq, pos + 1) : p.R;
            // Its three best ligands (by the better of R and T), each below the cut in one of them.
            Span<double> gr = stackalloc double[Chemistry.S], gt = stackalloc double[Chemistry.S];
            for (int s = 0; s < Chemistry.S; s++) { gr[s] = c.BindSpecies(p.R, s); gt[s] = c.BindSpecies(p.T, s); }
            for (int k = 0; k < MaxLig; k++)
            {
                int best = -1;
                for (int s = 0; s < Chemistry.S; s++)
                {
                    if (s == p.L0 && k > 0 || s == p.L1 && k > 1) continue;
                    double m = Math.Min(gr[s], gt[s]);
                    if (m >= P.Life2Cut) continue;
                    if (best < 0 || m < Math.Min(gr[best], gt[best])) best = s;
                }
                if (best < 0) break;
                if (k == 0) { p.L0 = best; p.G0R = gr[best]; p.G0T = gt[best]; }
                else if (k == 1) { p.L1 = best; p.G1R = gr[best]; p.G1T = gt[best]; }
                else { p.L2 = best; p.G2R = gr[best]; p.G2T = gt[best]; }
                p.N = k + 1;
            }
            if (p.N > 0) pockets.Add(p);
        }
        pockets.Sort((x, y) => x.Pos.CompareTo(y.Pos));
        Pockets = pockets.ToArray();

        // 3. Conformations, modification sites, stability.
        double sqrt = Math.Sqrt(Math.Max(1, n));
        double rt = 0, core = 0;
        int outer = 0;
        for (int i = 0; i < n; i++)
        {
            rt += ((i & 1) == 0 ? 1 : -1) * c.HB[Seq[i]];
            if (!tm[i]) { core += c.HB[Seq[i]]; outer++; }
        }
        double baseRT = P.Life2EpsC * rt / sqrt;
        DgFold = P.Life2EpsF * (core - c.HBMean * outer) - P.Life2EpsS * sqrt;
        for (int k = 0; k < Pockets.Length; k++)
            for (int l = 0; l < Pockets[k].N; l++)
                if (chem.Gap[Pockets[k].Lig(l)] > 0) CarrierPocket = true;
        // Sites: exposed residues near a pocket for an excited carrier (the chain excites them itself), the two with the
        // largest effect on ΔG_RT; then, while fewer than two, exposed excitable residues another chain may excite (a pocket
        // of that chain holding the window around it next to a pocket for an excited carrier: ModLink) — also by effect.
        var sites = new List<(double eff, int pos, int donor)>();
        var transSites = new List<(double eff, int pos, int donor)>();
        for (int i = 0; i < n; i++)
        {
            int f = Seq[i];
            if (tm[i] || c.H[f] >= 0.45) continue;
            if (c.X[f] > 0 && i >= 1 && i + 2 < n)
                transSites.Add((Math.Abs(P.Life2EpsC * ((i & 1) == 0 ? 1 : -1) * c.H[f] * (c.BStar[f] - c.B[f]) / sqrt), i, -1));
            int donor = -1;
            for (int k = 0; k < Pockets.Length && donor < 0; k++)
            {
                if (Math.Abs(Pockets[k].Pos + 1 - i) > 12 || (i >= Pockets[k].Pos && i <= Pockets[k].Pos + Chem2.K)) continue;
                for (int l = 0; l < Pockets[k].N; l++)
                {
                    int s = Pockets[k].Lig(l);
                    if (chem.Gap[s] > 0 && chem.Gap[s] >= c.X[f]) { donor = k; break; }
                }
            }
            if (donor < 0) continue;
            double eff = P.Life2EpsC * ((i & 1) == 0 ? 1 : -1) * c.H[f] * (c.BStar[f] - c.B[f]) / sqrt;
            sites.Add((Math.Abs(eff), i, donor));
        }
        sites.Sort((x, y) => x.eff != y.eff ? y.eff.CompareTo(x.eff) : x.pos.CompareTo(y.pos));
        if (sites.Count < MaxSites && P.Life2TransMod != 0)
        {
            transSites.RemoveAll(x => sites.Exists(y => y.pos == x.pos) || x.eff <= 0);
            transSites.Sort((x, y) => x.eff != y.eff ? y.eff.CompareTo(x.eff) : x.pos.CompareTo(y.pos));
            for (int k = 0; k < transSites.Count && sites.Count < MaxSites; k++) sites.Add(transSites[k]);
        }
        int ns = Math.Min(MaxSites, sites.Count);
        Sites = new int[ns]; SiteGap = new int[ns]; SiteDonor = new int[ns];
        for (int k = 0; k < ns; k++) { Sites[k] = sites[k].pos; SiteGap[k] = c.X[Seq[sites[k].pos]]; SiteDonor[k] = sites[k].donor; }
        for (int st = 0; st < 4; st++)
        {
            double d = baseRT;
            for (int k = 0; k < ns; k++)
                if ((st >> k & 1) != 0)
                {
                    int i = Sites[k], f = Seq[i];
                    d += P.Life2EpsC * ((i & 1) == 0 ? 1 : -1) * c.H[f] * (c.BStar[f] - c.B[f]) / sqrt;
                }
            DgRT[st] = d;
        }

        // 4. What the pockets do.
        for (int k = 0; k < Pockets.Length; k++)
        {
            var p = Pockets[k];
            for (int l = 0; l < p.N; l++)
            {
                int s = p.Lig(l);
                if (p.Side == Tm)
                {
                    // A pump: the chain also holds an excited carrier inside, near the pore — the carrier's turnover
                    // drives the molecule in against the gradient, and the pore opens to one side at a time
                    // (alternating access), so it is no open channel. Otherwise the pore is a channel.
                    var pump = new Act { Kind = ActKind.Pump, Pocket = k, Lig = l, Species = s, Couple = -1, CoupleSpecies = -1 };
                    if (FindCouple(c, k, 1, false, ref pump)) Acts.Add(pump);
                    else Acts.Add(new Act { Kind = ActKind.Channel, Pocket = k, Lig = l, Species = s, Couple = -1 });
                    continue;
                }
                // Facing out, a pocket for a molecule that splits downhill speeds the split of what it touches outside:
                // the molecules of a body it is pressed against (World.Digest) — the same transition-state law.
                if (p.Side == Out)
                {
                    if (s % 2 == 0 && chem.SplitA[s] >= 0 && chem.SplitEnergy(s) > 0)
                    {
                        // Work: the share of the split's activation energy the pocket takes away (law Life2Lysis 1) — how much of
                        // the molecule its better face fits (matched atoms over the molecule's) times its rigidity, at most
                        // CatalysisMax (as model 1's proteins lower a face's barrier in rock); a pocket inside next to it for
                        // a ground carrier takes the split's energy into the carrier (coupled, as a split inside).
                        var face = p.GR(l) <= p.GT(l) ? p.R : p.T;
                        double match = 0, atoms = 0;
                        for (int e = 0; e < Chem2.E; e++) { match += Math.Min(c.LigAtoms[s, e], face.T[e]); atoms += c.LigAtoms[s, e]; }
                        double fit = atoms > 0 ? match / atoms : 0;
                        var d = new Act { Kind = ActKind.Digest, Pocket = k, Lig = l, Species = s, Couple = -1, CoupleSpecies = -1, Work = P.CatalysisMax * fit * Math.Min(1, face.Rho / c.BondMax) };
                        if (P.Life2Lysis != 0) FindCouple(c, k, chem.SplitEnergy(s), true, ref d);
                        Acts.Add(d);
                    }
                    // Facing out, a pocket for an excited molecule next to a pocket inside for a ground carrier whose gap the
                    // molecule's covers: the excitation passes from the held molecule to the carrier (transfer downhill, the
                    // rest heat) — the molecule stays outside, relaxed (law Life2Harvest).
                    if (P.Life2Harvest != 0 && Crossings > 0 && chem.Gap[s] > 0)
                    {
                        var h = new Act { Kind = ActKind.Harvest, Pocket = k, Lig = l, Species = s, Couple = -1, CoupleSpecies = -1 };
                        if (FindCouple(c, k, chem.Gap[s], true, ref h)) Acts.Add(h);
                    }
                    continue;
                }
                if (p.Side != In) continue;
                // A split that releases energy, coupled to a ground carrier nearby if the energy suffices.
                // (Ground substrates only: an excited molecule held in a pocket is a carrier there — its excitation leaves by
                // coupling, motors, transfer or the spontaneous decay of the body physics, not by this law.)
                if (s % 2 == 0 && chem.SplitA[s] >= 0 && chem.SplitEnergy(s) > 0)
                {
                    var a = new Act { Kind = ActKind.Split, Pocket = k, Lig = l, Species = s, Couple = -1, CoupleSpecies = -1 };
                    FindCouple(c, k, chem.SplitEnergy(s), true, ref a);
                    Acts.Add(a);
                }
                // A bind with a partner in another pocket of the chain nearby.
                for (int k2 = k + 1; k2 < Pockets.Length; k2++)
                {
                    var p2 = Pockets[k2];
                    if (p2.Side != In || p2.Pos - p.Pos > 12) continue;
                    for (int l2 = 0; l2 < p2.N; l2++)
                    {
                        int s2 = p2.Lig(l2), prod = chem.Combine[s, s2];
                        if (prod < 0) continue;
                        int de = chem.E[s] + chem.E[s2] - chem.E[prod];
                        var a = new Act { Kind = ActKind.Bind, Pocket = k, Lig = l, Pocket2 = k2, Lig2 = l2, Species = s, Species2 = s2, Couple = -1, CoupleSpecies = -1 };
                        if (de > 0) FindCouple(c, k, de, true, ref a);
                        else if (!FindCouple(c, k, -de, false, ref a)) continue;   // uphill: only with an excited carrier to pay
                        Acts.Add(a);
                    }
                }
                // A motor: a membrane chain with an excited carrier bound inside.
                if (Crossings > 0 && chem.Gap[s] > 0)
                {
                    double rho = Math.Min(1, p.R.Rho / c.BondMax);
                    double work = chem.Gap[s] * (double)World.ResidueRaw / Qty.One * rho * (1 - DetMath.Exp(-InLength / (double)P.Life2Leg));
                    Acts.Add(new Act { Kind = ActKind.Motor, Pocket = k, Lig = l, Species = s, Couple = -1, Work = work });
                }
            }
            // A pigment: a hydrophobic pocket; its photon is its largest gap; a ground carrier nearby takes it.
            if (p.R.H >= 0.5 && p.Side != Out)
            {
                var a = new Act { Kind = ActKind.Pigment, Pocket = k, Couple = -1, CoupleSpecies = -1, Work = p.R.X };
                FindCouple(c, k, p.R.X, true, ref a, pigment: true);
                Acts.Add(a);
            }
        }
        for (int k = 0; k < Sites.Length; k++)
            if (SiteDonor[k] >= 0) Acts.Add(new Act { Kind = ActKind.Modify, Pocket = SiteDonor[k], Site = k, Couple = -1 });

        // 5. A homotypic contact: a pocket that holds a window of the same chain (on its own side) in the same conformation
        // of both copies — R face on R face ([at, at+2]) and T face on T face ([at+1, at+3], the register shift). Copies
        // held by it lie side by side and turn together (an allosteric array: one MWC unit over the contacted copies).
        ContactPocket = -1;
        if (P.Life2Array != 0)
        {
            double bestC = P.Life2Cut;
            for (int k = 0; k < Pockets.Length; k++)
            {
                if (Pockets[k].Side == Tm) continue;
                for (int at = 0; at + Chem2.K < n; at++)
                {
                    if (Sides[at + 1] != Pockets[k].Side || tm[at + 1]) continue;
                    double rr = c.BindWindow(Pockets[k].R, Seq, at), tt = c.BindWindow(Pockets[k].T, Seq, at + 1);
                    double g = Math.Max(rr, tt);
                    if (g < bestC) { bestC = g; ContactPocket = k; ContactAt = at; ContactRR = rr; ContactTT = tt; }
                }
            }
        }
    }

    // The homotypic contact (ContactPocket −1: none) — its pocket, the window it holds, the free energies R–R and T–T.
    public int ContactPocket = -1, ContactAt;
    public double ContactRR, ContactTT;

    // The excitation transfers chain `e` (the donor: a pocket holding a window of `t` around a site, next to — within 12
    // residues, on the same side — a pocket for an excited carrier whose gap covers the site's) can make on chain `t`'s
    // sites ("phosphorylation" in trans, design §4.6). The window faces: the target presents [j−1, j+1] in R and
    // [j, j+2] in T, the donor's pocket its R or T face; four free energies per link. Pure function of both sequences.
    public static ModLink[] LinksOf(Chem2 c, ProteinType e, ProteinType t)
    {
        if (P.Life2TransMod == 0 || t.Sites.Length == 0 || !e.CarrierPocket) return Array.Empty<ModLink>();
        var key = (e.Hash, t.Hash);
        if (c.Links.TryGetValue(key, out var have)) return have;
        var arr = ComputeLinks(c, e, t);
        if (c.Links.Count > 1 << 16) c.Links.Clear();   // a pure function of the key: forgetting costs time only
        c.Links.TryAdd(key, arr);
        return arr;
    }

    public static ModLink[] ComputeLinks(Chem2 c, ProteinType e, ProteinType t)
    {
        if (P.Life2TransMod == 0 || t.Sites.Length == 0 || !e.CarrierPocket) return Array.Empty<ModLink>();
        var list = new List<ModLink>();
        var chem = c.Chem;
        for (int k = 0; k < t.Sites.Length; k++)
        {
            if (t.SiteDonor[k] >= 0) continue;   // excited by its own chain
            int j = t.Sites[k], side = t.Sides[j];
            if (j < 1 || j + 2 >= t.Len) continue;
            ModLink best = default; best.Pocket = -1;
            double bestG = P.Life2Cut;
            for (int p = 0; p < e.Pockets.Length; p++)
            {
                ref var pk = ref e.Pockets[p];
                if (pk.Side != side) continue;
                // the carrier next to it that can pay the site's gap (the strongest binder)
                int cp = -1, cl = -1; double cg = double.MaxValue;
                for (int q = 0; q < e.Pockets.Length; q++)
                {
                    if (q == p || e.Pockets[q].Side != side || Math.Abs(e.Pockets[q].Pos - pk.Pos) > 12) continue;
                    for (int l = 0; l < e.Pockets[q].N; l++)
                    {
                        int s = e.Pockets[q].Lig(l);
                        if (chem.Gap[s] <= 0 || chem.Gap[s] < t.SiteGap[k]) continue;
                        double g = Math.Min(e.Pockets[q].GR(l), e.Pockets[q].GT(l));
                        if (g < cg) { cg = g; cp = q; cl = l; }
                    }
                }
                if (cp < 0) continue;
                double rr = c.BindWindow(pk.R, t.Seq, j - 1), rt = c.BindWindow(pk.R, t.Seq, j);
                double tr = c.BindWindow(pk.T, t.Seq, j - 1), tt = c.BindWindow(pk.T, t.Seq, j);
                double g0 = Math.Min(Math.Min(rr, rt), Math.Min(tr, tt));
                if (g0 >= bestG) continue;
                bestG = g0;
                best = new ModLink { Site = k, Pocket = p, Carrier = cp, CarrierLig = cl, Species = e.Pockets[cp].Lig(cl), GRR = rr, GRT = rt, GTR = tr, GTT = tt };
            }
            if (best.Pocket >= 0) list.Add(best);
        }
        return list.ToArray();
    }

    static int LoopParity(int[] loop, int i, bool[] tm) => loop[i] & 1;

    // A pocket of the chain within 12 residues of pocket k (not k itself) for a carrier: ground (up: it takes
    // energy ≥ its excited gap, at most `energy`) or excited (it gives its gap ≥ `energy`). The strongest binder.
    bool FindCouple(Chem2 c, int k, int energy, bool up, ref Act a, bool pigment = false)
    {
        var chem = c.Chem;
        double best = double.MaxValue;
        for (int j = 0; j < Pockets.Length; j++)
        {
            if (j == k || Math.Abs(Pockets[j].Pos - Pockets[k].Pos) > 12 || Pockets[j].Side == Out) continue;
            for (int l = 0; l < Pockets[j].N; l++)
            {
                int s = Pockets[j].Lig(l);
                bool fits = up ? s % 2 == 0 && chem.PhotoUp[s] >= 0 && chem.Gap[chem.PhotoUp[s]] > 0 && chem.Gap[chem.PhotoUp[s]] <= energy
                               : chem.Gap[s] > 0 && chem.Gap[s] >= energy;
                if (!fits) continue;
                double g = Math.Min(Pockets[j].GR(l), Pockets[j].GT(l));
                if (g < best) { best = g; a.Couple = j; a.CoupleLig = l; a.CoupleSpecies = s; }
            }
        }
        return a.Couple >= 0;
    }

    public TypeLevel At(int level)
    {
        var t = levels[level];
        if (t != null) return t;
        double beta = Chem2.Beta(level), k0 = P.Life2Kd0;
        t = new TypeLevel { KR = new double[Pockets.Length * MaxLig], KT = new double[Pockets.Length * MaxLig], Cat = new double[Acts.Count], Beta = beta };
        for (int k = 0; k < Pockets.Length; k++)
            for (int l = 0; l < Pockets[k].N; l++)
            {
                t.KR[k * MaxLig + l] = DetMath.Exp(-beta * Pockets[k].GR(l)) / k0;
                t.KT[k * MaxLig + l] = DetMath.Exp(-beta * Pockets[k].GT(l)) / k0;
            }
        for (int s = 0; s < 4; s++) t.L[s] = DetMath.Exp(beta * DgRT[s]);
        for (int i = 0; i < Acts.Count; i++)
        {
            var a = Acts[i];
            if (a.Kind != ActKind.Split && a.Kind != ActKind.Bind && a.Kind != ActKind.Digest) continue;
            var p = Pockets[a.Pocket];
            double g = -Math.Min(p.GR(a.Lig), p.GT(a.Lig));
            if (a.Kind == ActKind.Bind) g = 0.5 * (g - Math.Min(Pockets[a.Pocket2].GR(a.Lig2), Pockets[a.Pocket2].GT(a.Lig2)));
            t.Cat[i] = Math.Min(P.Life2CatMax, DetMath.Exp(beta * P.Life2CatTS * p.R.Rho * Math.Max(0, g)));
        }
        t.Decay = P.Life2Decay * DetMath.Exp(-beta * Math.Clamp(DgFold, -4, 4));
        if (ContactPocket >= 0) { t.ContactK = DetMath.Exp(-beta * 0.5 * (ContactRR + ContactTT)) / k0; t.ContactShift = beta * (ContactRR - ContactTT); }
        levels[level] = t;   // a race fills the same values
        return t;
    }

    public static ProteinType Of(Chem2 c, byte[] seq)
    {
        var key = new SeqKey(seq);
        if (c.Types.TryGetValue(key, out var t)) return t;
        return c.Types.GetOrAdd(key, k => new ProteinType(c, k.Seq));
    }

    // Every pocket with its side, place and ligands (ΔG in R/T), for reports.
    public string Detail(Chemistry chem)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(ToString());
        foreach (var p in Pockets)
        {
            sb.Append($" | {(p.Side == In ? "in" : p.Side == Out ? "out" : "tm")}@{p.Pos}:");
            for (int l = 0; l < p.N; l++) sb.Append($" {chem.NameEn[p.Lig(l)]}({p.Lig(l)}) {p.GR(l):0.0}/{p.GT(l):0.0}");
        }
        return sb.ToString();
    }

    public override string ToString()
    {
        var parts = new List<string> { $"ℓ{Len}" };
        if (Crossings > 0) parts.Add($"TM×{Crossings}");
        foreach (var a in Acts) parts.Add(a.Kind.ToString().ToLowerInvariant() + (a.Kind == ActKind.Modify ? "" : $":{a.Species}") + (a.Couple >= 0 ? $"→{a.CoupleSpecies}" : ""));
        for (int k = 0; k < Sites.Length; k++) parts.Add($"site@{Sites[k]}{(SiteDonor[k] < 0 ? "(trans)" : "")}:{DgRT[1 << k] - DgRT[0]:+0.00;-0.00}");
        if (ContactPocket >= 0) parts.Add($"array@{ContactAt}:{ContactRR:0.0}/{ContactTT:0.0}");
        return string.Join(" ", parts);
    }
}
