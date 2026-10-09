using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Primordium.Model2;

// Life model 2, the residue chemistry of a world (docs/DESIGN-LIFE-MODEL-2.md §3): the letters of the
// polymer are the 16 ground formulas of the seed's Chemistry, and everything a residue "is" is computed
// from that chemistry once per world — nothing is chosen by hand:
//   h(f)   hydrophobicity: weakly reactive atoms dislike water (from AffinityPerAtom against the elements')
//   q(f)   polarity: valence of its atoms against the elements' mean
//   v(f)   size (Chemistry.Volume), b(f) rigidity (Chemistry.Bond; the excited residue's is Bond[s*])
//   x(f)   gap: what the residue (or a ligand) takes when excited (Chemistry.Gap of its excited state)
//   mirror the "socket" for an element: elements ranked by valence (affinity, index), mirror(e) the one of
//          rank 3 − rank(e) — a pocket wants atom e where its residues have mirror(e)
//   pair   ε_pair(a, b) = Life2Pair·exp(−(q_a + q_b)²/σ_q²)·exp(−(v_a + v_b − 2v̄)²/σ_v²); comp(a) its best partner
// Ligands are the 32 species (h, q, composition, excited or not, gap) and windows of chains (sums).
// Also the caches of what is a pure function of a sequence in this chemistry (protein folds, gene tables):
// thread-safe, filled by whoever needs them first (the content does not depend on who).
public sealed class Chem2
{
    public const int L = Chemistry.S / 2, E = Chemistry.ElementCount, K = 3;   // letters, elements, pocket window
    public readonly Chemistry Chem;
    public readonly double[] H = new double[L], Q = new double[L], V = new double[L], B = new double[L], BStar = new double[L], HB = new double[L];
    public readonly int[] X = new int[L];               // gap of the residue's excited state
    public readonly int[,] Atoms = new int[L, E];
    public readonly int[] Mirror = new int[E];
    public readonly double[,] Pair = new double[L, L];
    public readonly int[] Comp = new int[L];
    public readonly double VMean, HBMean, BondMax;
    // What any binding costs (the ligand's lost freedom), set so that Life2PocketShare of random windows bind some
    // species below Life2Cut: one law for pocket rarity in every chemistry (the descriptors' scales differ by seed).
    public readonly double Eps0, EpsWindow;
    // The hydrophobicity a 7-residue window (little charged: mean |q| ≤ Life2TmQ) needs to cross the membrane, set
    // so that Life2TmShare of random windows do.
    public readonly double TmH;
    // Life2EpsX·e^{−(Δx/σ)²} for whole gap differences Δx (−64…64).
    readonly double[] resonance = new double[129];
    double Resonance(int dx) => dx < -64 || dx > 64 ? 0 : resonance[dx + 64];
    // Ligand species: hydrophobicity, polarity, atoms (by species 0–31), amphiphilicity.
    public readonly double[] LigH = new double[Chemistry.S], LigQ = new double[Chemistry.S], Amphi = new double[Chemistry.S];
    public readonly int[,] LigAtoms = new int[Chemistry.S, E];

    public readonly ConcurrentDictionary<SeqKey, ProteinType> Types = new();
    public readonly ConcurrentDictionary<(ulong genome, ulong types), GeneTable> Tables = new();

    static readonly ConditionalWeakTable<Chemistry, Chem2> of = new();
    public static Chem2 Of(Chemistry c) => of.GetValue(c, k => new Chem2(k));

    Chem2(Chemistry c)
    {
        Chem = c;
        for (int d = -64; d <= 64; d++) { double z = d / (double)P.Life2SigmaX; resonance[d + 64] = P.Life2EpsX * DetMath.Exp(-z * z); }
        double aMean = 0, aMin = double.MaxValue, aMax = double.MinValue, vMeanEl = 0;
        for (int e = 0; e < E; e++)
        {
            aMean += c.Affinity[e] / E; vMeanEl += c.Valence[e] / E;
            aMin = Math.Min(aMin, c.Affinity[e]); aMax = Math.Max(aMax, c.Affinity[e]);
        }
        double span = Math.Max(1e-6, aMax - aMin);
        for (int s = 0; s < Chemistry.S; s++)
        {
            int n = c.AtomCount(s);
            double q = 0, mean = 0, var = 0;
            for (int e = 0; e < E; e++) { LigAtoms[s, e] = c.Atoms[s, e]; q += c.Atoms[s, e] * (c.Valence[e] - vMeanEl); mean += c.Atoms[s, e] * c.Affinity[e]; }
            mean /= n;
            for (int e = 0; e < E; e++) var += c.Atoms[s, e] * (c.Affinity[e] - mean) * (c.Affinity[e] - mean);
            LigQ[s] = q / n;
            LigH[s] = Math.Clamp(0.5 + (aMean - c.AffinityPerAtom[s]) / span, 0, 1);
            Amphi[s] = n * var / n;   // AtomCount × variance of its atoms' affinity (0 for one element)
        }
        double v = 0, hb = 0, bmax = 0;
        for (int f = 0; f < L; f++)
        {
            int s = 2 * f;
            H[f] = LigH[s]; Q[f] = LigQ[s]; V[f] = c.Volume[s]; B[f] = c.Bond[s]; BStar[f] = c.Bond[s + 1];
            X[f] = c.Gap[s + 1];
            HB[f] = H[f] * B[f];
            for (int e = 0; e < E; e++) Atoms[f, e] = c.Atoms[s, e];
            v += V[f] / L; hb += HB[f] / L; bmax = Math.Max(bmax, B[f]);
        }
        VMean = v; HBMean = hb; BondMax = Math.Max(1e-6, bmax);
        // Polarity in units of its spread over the letters (a seed of very unequal valences is not all charge).
        double qm = 0, qv = 0;
        for (int f = 0; f < L; f++) qm += Q[f] / L;
        for (int f = 0; f < L; f++) qv += (Q[f] - qm) * (Q[f] - qm) / L;
        double qs = Math.Sqrt(Math.Max(1e-9, qv));
        for (int f = 0; f < L; f++) Q[f] /= qs;
        for (int s = 0; s < Chemistry.S; s++) LigQ[s] /= qs;
        // Elements by valence (then affinity, then index); the mirror of rank r is rank 3 − r.
        var order = new int[E];
        for (int e = 0; e < E; e++) order[e] = e;
        Array.Sort(order, (x, y) => c.Valence[x] != c.Valence[y] ? c.Valence[x].CompareTo(c.Valence[y]) : c.Affinity[x] != c.Affinity[y] ? c.Affinity[x].CompareTo(c.Affinity[y]) : x.CompareTo(y));
        for (int r = 0; r < E; r++) Mirror[order[r]] = order[E - 1 - r];
        // The cost of binding: the Life2PocketShare quantile of the best binding of random windows (fixed draws).
        {
            var rng = new SimRng(0x5EED2);
            const int samples = 4096;
            var best = new double[samples];
            var seq = new byte[K];
            for (int k = 0; k < samples; k++)
            {
                for (int i = 0; i < K; i++) seq[i] = (byte)rng.Next(L);
                var w = PocketOf(seq, 0);
                double g = double.MaxValue;
                for (int s = 0; s < Chemistry.S; s++) g = Math.Min(g, BindSpecies(w, s));
                best[k] = g;
            }
            Array.Sort(best);
            int at = Math.Clamp((int)(P.Life2PocketShare * samples), 0, samples - 1);
            Eps0 = P.Life2Cut - best[at];
            // A window of a chain is tethered: binding it costs less freedom than a free molecule's. Its cost is set
            // so that Life2WindowShare of random pocket–window pairs bind below the cut.
            var pair = new double[samples];
            var other = new byte[K];
            for (int k = 0; k < samples; k++)
            {
                for (int i = 0; i < K; i++) { seq[i] = (byte)rng.Next(L); other[i] = (byte)rng.Next(L); }
                var w = PocketOf(seq, 0);
                Span<double> lc = stackalloc double[E];
                WindowLigand(other, 0, lc, out double lq, out double lh);
                pair[k] = Bind(w.T, w.Q, w.H, w.X, lc, lq, lh, 0) - Eps0;
            }
            Array.Sort(pair);
            EpsWindow = P.Life2Cut - pair[Math.Clamp((int)(P.Life2WindowShare * samples), 0, samples - 1)];
            var hs = new double[samples];
            for (int k = 0; k < samples; k++)
            {
                double h = 0, q = 0;
                for (int i = 0; i < 7; i++) { int f = rng.Next(L); h += H[f] / 7; q += Math.Abs(Q[f]) / 7; }
                hs[k] = q <= P.Life2TmQ ? h : double.MinValue;
            }
            Array.Sort(hs);
            int top = Math.Clamp(samples - 1 - (int)(P.Life2TmShare * samples), 0, samples - 1);
            TmH = hs[top] > double.MinValue ? hs[top] + 1e-12 : double.MaxValue;
        }
        PairLaw = P.Life2PairLaw;
        if (PairLaw == 0)
        {
            double sq = 0.5, sv = 0.25 * VMean;
            for (int a = 0; a < L; a++)
                for (int b = 0; b < L; b++)
                {
                    double dq = Q[a] + Q[b], dv = V[a] + V[b] - 2 * VMean;
                    Pair[a, b] = P.Life2Pair * DetMath.Exp(-dq * dq / (sq * sq)) * DetMath.Exp(-dv * dv / (sv * sv));
                }
        }
        else
        {
            // Sockets: atom e faces atom mirror(e) (an involution, so the law is symmetric). A facing pair holds half
            // by fit and half by its ionic resonance (Pauling, the bond-energy model of the chemistry), in units of
            // the mean over the elements; an atom without its partner costs Life2PairMiss; per atom of the larger.
            var ion = new double[E];
            double ionMean = 0;
            for (int e = 0; e < E; e++)
            {
                double d = c.Affinity[e] - c.Affinity[Mirror[e]];
                ion[e] = P.ChemIonicK * d * d; ionMean += ion[e] / E;
            }
            for (int e = 0; e < E; e++) SocketWeight[e] = ionMean > 1e-9 ? 0.5 * (1 + ion[e] / ionMean) : 1;
            for (int a = 0; a < L; a++)
                for (int b = 0; b < L; b++)
                {
                    double m = 0, x = 0;
                    int na = 0, nb = 0;
                    for (int e = 0; e < E; e++)
                    {
                        int ca = Atoms[a, e], cb = Atoms[b, Mirror[e]];
                        m += Math.Min(ca, cb) * SocketWeight[e]; x += Math.Abs(ca - cb);
                        na += ca; nb += cb;
                    }
                    Pair[a, b] = P.Life2Pair * (m - P.Life2PairMiss * x / 2) / Math.Max(1, Math.Max(na, nb));
                }
        }
        for (int a = 0; a < L; a++)
        {
            int best = 0;
            for (int b = 1; b < L; b++) if (Pair[a, b] > Pair[a, best]) best = b;
            Comp[a] = best;
        }
    }

    public readonly int PairLaw;
    public readonly double[] SocketWeight = new double[E];   // law 1: what a facing atom pair e–mirror(e) holds (mean 1)

    // β(T) = 0.5·288/(273 + T): the thermal scale of model 1's Boltzmann factor, sharper in the cold.
    // Temperature levels: whole degrees −40…+80 °C (a body's temperature is rounded to one).
    public const int Levels = 121, LevelLow = -40;
    public static int Level(float tb) => Math.Clamp((int)Math.Floor(tb + 0.5f) - LevelLow, 0, Levels - 1);
    public static double Beta(int level) => 0.5 * 288.0 / (273.0 + level + LevelLow);

    // Free energy of a pocket (target atoms t, polarity q, hydrophobicity h, best gap x) binding a ligand of
    // atoms c, polarity lq, hydrophobicity lh, excited with gap lx (lx 0: ground).
    public double Bind(ReadOnlySpan<double> t, double q, double h, int x, ReadOnlySpan<double> c, double lq, double lh, int lx)
    {
        double match = 0, miss = 0;
        for (int e = 0; e < E; e++) { double ce = c[e], te = t[e]; match += Math.Min(ce, te); miss += Math.Abs(ce - te); }
        double dh = h - lh;
        double g = Eps0 - P.Life2EpsB * match + P.Life2EpsM * miss + P.Life2EpsQ * q * lq + P.Life2EpsH * dh * dh;
        if (lx > 0) g -= Resonance(x - lx);
        return g;
    }

    public double BindSpecies(in Window w, int s)
    {
        Span<double> c = stackalloc double[E];
        for (int e = 0; e < E; e++) c[e] = LigAtoms[s, e];
        return Bind(w.T, w.Q, w.H, w.X, c, LigQ[s], LigH[s], Chem.Gap[s]);
    }

    // A window of three residues of a chain (a genome window, as a ligand): read residue by residue, its mean
    // residue's atoms, polarity and hydrophobicity.
    public void WindowLigand(ReadOnlySpan<byte> seq, int at, Span<double> c, out double q, out double h)
    {
        c.Clear(); q = 0; h = 0;
        for (int i = at; i < at + K; i++)
        {
            int f = seq[i];
            for (int e = 0; e < E; e++) c[e] += Atoms[f, e] / (double)K;
            q += Q[f] / K; h += H[f] / K;
        }
    }

    public double BindWindow(in Window w, ReadOnlySpan<byte> seq, int at)
    {
        Span<double> c = stackalloc double[E];
        WindowLigand(seq, at, c, out double q, out double h);
        return Bind(w.T, w.Q, w.H, w.X, c, q, h, 0) - Eps0 + EpsWindow;
    }

    // What a window of a chain wants as a pocket: the mean of its residues' mirrored atoms (a cavity lined by
    // three residues holds a ligand of about a residue's size, a socket for each atom), mean polarity,
    // hydrophobicity and rigidity, its largest gap.
    public Window PocketOf(ReadOnlySpan<byte> seq, int at)
    {
        var w = new Window();
        for (int i = at; i < at + K; i++)
        {
            int f = seq[i];
            for (int e = 0; e < E; e++) w.T[e] += Atoms[f, Mirror[e]] / (double)K;
            w.Q += Q[f] / K; w.H += H[f] / K; w.Rho += B[f] / K;
            w.X = Math.Max(w.X, X[f]);
        }
        return w;
    }

    // The chance a copy puts a wrong letter opposite a template letter, averaged over `templates`, with the
    // monomers `pool` equally at hand, discrimination d and thermal scale beta (the pairing law, ChemModel.Copy).
    public double CopyError(ReadOnlySpan<byte> templates, ReadOnlySpan<byte> pool, double d, double beta, int passes = 0)
    {
        Span<double> w = stackalloc double[L];
        foreach (var b in pool) w[b] = 1;
        double err = 0;
        foreach (var t in templates) err += CopyError(t, w, d, beta, passes);
        return templates.Length > 0 ? err / templates.Length : 0;
    }

    // The chance a copy ends with a wrong letter opposite template letter t, with monomers at hand in proportions w
    // (by letter), discrimination d, thermal scale beta and `passes` of kinetic proofreading (Hopfield): every pass
    // checks the letter in place again and takes a wrong one off with 1 − e^{−β·d·Δε}, a new one then drawn by the
    // same law — each pass multiplies the error by about e^{−β·d·Δε} (ChemModel.Copy does exactly this, by draws).
    public double CopyError(int t, ReadOnlySpan<double> w, double d, double beta, int passes)
    {
        Span<double> p = stackalloc double[L], q = stackalloc double[L];
        double z = 0;
        for (int b = 0; b < L; b++) { p[b] = w[b] > 0 ? w[b] * DetMath.Exp(beta * d * Pair[t, b]) : 0; z += p[b]; }
        if (z <= 0) return 1;
        int c = Comp[t];
        for (int b = 0; b < L; b++) q[b] = p[b] /= z;
        for (int pass = 0; pass < passes; pass++)
        {
            double off = 0;
            for (int b = 0; b < L; b++)
            {
                if (b == c || q[b] == 0) continue;
                double r = 1 - DetMath.Exp(-beta * d * Math.Max(0, Pair[t, c] - Pair[t, b]));
                off += q[b] * r; q[b] *= 1 - r;
            }
            for (int b = 0; b < L; b++) q[b] += off * p[b];
        }
        return Math.Clamp(1 - q[c], 0, 1);
    }

    // The discrimination a polymerase pocket of rigidity rho gives (1: a copy without an enzyme).
    public double Discrimination(double rho) => 1 + P.Life2Fidelity * Math.Min(1, rho / BondMax);

    // Two residues pair strongly enough to hold a stem (a terminator's hairpin).
    public bool Pairs(int a, int b) => Pair[a, b] >= P.Life2Stem * P.Life2Pair;
}

// A pocket's face in one conformation.
public struct Window
{
    public Vec4 T;
    public double Q, H, Rho;
    public int X;
}

[InlineArray(4)]
public struct Vec4 { double e0; }

// A sequence as a dictionary key (residue letters 0–15, one per byte).
public readonly struct SeqKey : IEquatable<SeqKey>
{
    public readonly byte[] Seq;
    public readonly ulong Hash;
    public SeqKey(byte[] seq) { Seq = seq; Hash = HashOf(seq); }
    public static ulong HashOf(ReadOnlySpan<byte> s)
    {
        ulong h = 1469598103934665603UL;
        foreach (byte b in s) { h ^= b; h *= 1099511628211UL; }
        h ^= (ulong)s.Length * 0x9E3779B97F4A7C15UL;
        return h;
    }
    public bool Equals(SeqKey o) => Hash == o.Hash && Seq.AsSpan().SequenceEqual(o.Seq);
    public override bool Equals(object o) => o is SeqKey k && Equals(k);
    public override int GetHashCode() => (int)(Hash ^ (Hash >> 32));
}
