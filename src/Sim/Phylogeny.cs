using System;
using System.Collections.Generic;
using System.Numerics;

namespace Primordium;

// The family tree of everything alive (ROADMAP 9.2), kept like Empirical's systematics: a taxon is a
// genome (Agent.Hash); a child with its parent taxon's genome joins it, a mutated child founds a new
// taxon under it. Only ancestors of the living are kept: a taxon with no living bodies and no kept
// descendants is dropped at once (and so, up the tree, its ancestors that thereby lose their last
// descendant); every Compact() splices out dead taxa with a single child (the child's branch then
// spans several steps — Depth stays the number of original steps from the root). So the tree holds
// at most about twice as many nodes as there are living taxa.
//
// Observation only. Node indices are stable (an Agent keeps its taxon's index in Agent.Taxon); freed
// slots are reused.
public sealed class Phylogeny
{
    public int[] Parent = new int[1024], Alive = new int[1024], Kids = new int[1024], Depth = new int[1024], Mut = new int[1024];
    public long[] Origin = new long[1024], Lineage = new long[1024];
    public ulong[] Hash = new ulong[1024];
    public int High;                       // slots used so far
    public readonly List<int> Free = new();
    public int Nodes => High - Free.Count;

    // A body was born: its taxon (parentTaxon < 0: a founder, a root of its own).
    public int Born(int parentTaxon, ulong hash, int mutations, long tick, long lineage)
    {
        if (parentTaxon >= 0 && Hash[parentTaxon] == hash && Alive[parentTaxon] + Kids[parentTaxon] > 0)
        {
            Alive[parentTaxon]++;
            return parentTaxon;
        }
        int t;
        if (Free.Count > 0) { t = Free[^1]; Free.RemoveAt(Free.Count - 1); }
        else
        {
            if (High == Parent.Length) Grow(High * 2);
            t = High++;
        }
        bool has = parentTaxon >= 0 && Alive[parentTaxon] + Kids[parentTaxon] > 0;
        Parent[t] = has ? parentTaxon : -1;
        Alive[t] = 1; Kids[t] = 0;
        Depth[t] = has ? Depth[parentTaxon] + 1 : 0;
        Mut[t] = (has ? Mut[parentTaxon] : 0) + mutations;
        Origin[t] = tick; Lineage[t] = lineage; Hash[t] = hash;
        if (has) Kids[parentTaxon]++;
        return t;
    }

    void Grow(int n)
    {
        Array.Resize(ref Parent, n); Array.Resize(ref Alive, n); Array.Resize(ref Kids, n); Array.Resize(ref Depth, n); Array.Resize(ref Mut, n);
        Array.Resize(ref Origin, n); Array.Resize(ref Lineage, n); Array.Resize(ref Hash, n);
    }

    public void Died(int t)
    {
        if (t < 0 || t >= High || Alive[t] <= 0) return;
        Alive[t]--;
        while (t >= 0 && Alive[t] == 0 && Kids[t] == 0)
        {
            int p = Parent[t];
            Release(t);
            if (p >= 0) Kids[p]--;
            t = p;
        }
    }

    void Release(int t)
    {
        Alive[t] = Kids[t] = 0; Parent[t] = -1; Hash[t] = 0;
        Kids[t] = -1;   // marks a free slot
        Free.Add(t);
    }

    public bool Used(int t) => Kids[t] >= 0;

    // Splices out dead taxa with exactly one child (their child's branch takes them over).
    public int Compact()
    {
        int spliced = 0;
        for (int x = 0; x < High; x++)
        {
            if (!Used(x)) continue;
            int p = Parent[x];
            while (p >= 0 && Alive[p] == 0 && Kids[p] == 1) p = Parent[p];
            Parent[x] = p;
        }
        // Whatever is dead with one child is now bypassed by it: free it (its own parent's kid count is
        // unchanged — the chain was one child, now it is that child).
        for (int x = 0; x < High; x++)
            if (Used(x) && Alive[x] == 0 && Kids[x] == 1) { Release(x); spliced++; }
        return spliced;
    }

    // Steps between two taxa: up to their last common ancestor (through the root if none).
    public int Distance(int a, int b)
    {
        int da = Depth[a], db = Depth[b];
        int x = a, y = b;
        while (x != y)
        {
            if (x < 0 || y < 0) return da + db + 2;
            if (Depth[x] >= Depth[y]) x = Parent[x]; else y = Parent[y];
        }
        return x < 0 ? da + db + 2 : da + db - 2 * Depth[x];
    }

    public long Bytes => (long)Parent.Length * (5 * 4 + 3 * 8);
}
