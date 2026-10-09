using System;
using System.Collections.Generic;

namespace Primordium.Model2;

// The genome of a model-2 cell read as genes (docs/DESIGN-LIFE-MODEL-2.md §3.2, §4.1, §5). The genome is a
// polymer of letters (Agent.G, two residues per byte, low nibble first). What is a gene follows from the
// pairing law and the binding law only:
//   terminator  a hairpin: a stem of 5 residues followed after a loop of 3–8 by its reverse complement
//               (pairs by Chem2.Comp) — the transcript ends before it
//   unit        the stretch from the end of one terminator (or the genome's start) to the next: its first window
//               (3 residues) is the promoter, the rest up to the terminator (at most Life2MaxGene) the transcript
//   expression  a protein of the cell whose pocket binds the promoter window and that has a pocket for an
//               excited carrier (a polymerase: it couples copying to charge) transcribes the unit at
//               Life2Tx·θ_pol per slow step; any protein bound to a window of the first 12 residues past the
//               promoter is in its way (repressor); the occupancies compete (θ = x/(1 + Σx))
//   origin      the genome's first window: a polymerase bound there copies the genome
// The transcript is the protein (one polymer for genome and proteins, open decision O2: "RNA world").
public sealed class Unit
{
    public int Start, Begin, End;   // promoter at Start, transcript [Begin, End), the unit ends where the next starts
    public byte[] Transcript;
    public ProteinType Type;
}

public sealed class GeneTable
{
    public Unit[] Units;
    // Which present protein types bind which windows: per unit its promoter binders and its blockers.
    public struct Binder { public int Type, Pocket; public double GR, GT; public bool Pol; }
    public Binder[][] Promoter, Block;   // by unit; Block: summed over the 12 windows past the promoter
    public ProteinType[] Types;          // the types the table was made for (by index used in Binder.Type)

    public static int Length(byte[] g) => g.Length * 2;
    public static int At(byte[] g, int i) => (i & 1) == 0 ? g[i >> 1] & 15 : g[i >> 1] >> 4;

    public static byte[] Residues(byte[] g)
    {
        var r = new byte[g.Length * 2];
        for (int i = 0; i < r.Length; i++) r[i] = (byte)At(g, i);
        return r;
    }

    public static byte[] Pack(ReadOnlySpan<byte> res)
    {
        var g = new byte[(res.Length + 1) / 2];
        for (int i = 0; i < res.Length; i++) g[i >> 1] |= (byte)((res[i] & 15) << ((i & 1) * 4));
        return g;
    }

    public const int Stem = 5, LoopMin = 3, LoopMax = 8, BlockSpan = 12;

    // The hairpin that starts at i (its length) or 0.
    public static int Hairpin(Chem2 c, ReadOnlySpan<byte> r, int i)
    {
        for (int loop = LoopMin; loop <= LoopMax; loop++)
        {
            int end = i + 2 * Stem + loop;
            if (end > r.Length) break;
            bool ok = true;
            for (int k = 0; k < Stem && ok; k++) ok = c.Pairs(r[i + k], r[end - 1 - k]);
            if (ok) return 2 * Stem + loop;
        }
        return 0;
    }

    public static List<Unit> Parse(Chem2 c, ReadOnlySpan<byte> r)
    {
        var units = new List<Unit>();
        int start = 0, n = r.Length;
        while (start + Chem2.K < n)
        {
            int begin = start + Chem2.K, end = n, next = n;
            for (int i = begin; i < n; i++)
            {
                int h = Hairpin(c, r, i);
                if (h > 0) { end = i; next = i + h; break; }
            }
            int stop = Math.Min(end, begin + P.Life2MaxGene);
            if (stop - begin >= 8)
            {
                var t = r.Slice(begin, stop - begin).ToArray();
                units.Add(new Unit { Start = start, Begin = begin, End = stop, Transcript = t, Type = ProteinType.Of(c, t) });
            }
            start = next;
        }
        return units;
    }

    // The table for a genome (residues) and the protein types present (in a fixed order).
    public static GeneTable Build(Chem2 c, byte[] res, ProteinType[] types)
    {
        var units = Parse(c, res);
        var t = new GeneTable { Units = units.ToArray(), Types = types, Promoter = new Binder[units.Count][], Block = new Binder[units.Count][] };
        var list = new List<Binder>();
        for (int u = 0; u < units.Count; u++)
        {
            list.Clear();
            Bindings(c, res, units[u].Start, types, list);
            t.Promoter[u] = list.ToArray();
            list.Clear();
            for (int at = units[u].Begin; at < Math.Min(units[u].End - Chem2.K, units[u].Begin + BlockSpan); at++) Bindings(c, res, at, types, list);
            t.Block[u] = list.ToArray();
        }
        return t;
    }

    static void Bindings(Chem2 c, byte[] res, int at, ProteinType[] types, List<Binder> list)
    {
        if (at + Chem2.K > res.Length) return;
        for (int k = 0; k < types.Length; k++)
        {
            var p = types[k].Pockets;
            for (int j = 0; j < p.Length; j++)
            {
                if (p[j].Side == ProteinType.Tm) continue;
                double gr = c.BindWindow(p[j].R, res, at), gt = c.BindWindow(p[j].T, res, at);
                if (Math.Min(gr, gt) >= P.Life2Cut) continue;
                list.Add(new Binder { Type = k, Pocket = j, GR = gr, GT = gt, Pol = types[k].CarrierPocket });
            }
        }
    }

    public static ulong TypesHash(ProteinType[] types)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (var t in types) { h ^= t.Hash; h *= 0x100000001B3UL; h = (h << 7) | (h >> 57); }
        return h ^ (ulong)types.Length;
    }

    public static GeneTable For(Chem2 c, byte[] g, ulong genomeHash, ProteinType[] types)
    {
        var key = (genomeHash, TypesHash(types));
        if (c.Tables.TryGetValue(key, out var t)) return t;
        if (c.Tables.Count > 1 << 16) c.Tables.Clear();   // a pure function of the key: forgetting costs time, never changes a result
        return c.Tables.GetOrAdd(key, _ => Build(c, Residues(g), types));
    }
}
