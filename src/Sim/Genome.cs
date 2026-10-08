using System;
using System.Collections.Generic;
using System.Numerics;

namespace Primordium;

// A genome is raw bytes run by a small stack machine. The low 6 bits pick the instruction; the top
// 2 bits are a tiny immediate: the operand of push/label/jumps, and for several instructions a
// second meaning (split→photo, expel→push, divide→mate, mine→gnaw, pile→grow, pick→count,
// intake→drink, bind→digest, nop→swim: a stroke up or down in water). There is
// no "photosynthesis", "walking" or "eating": `enzyme` turns the next three bytes into a protein that
// speeds up one reaction, and the rest of the code has to feed that reaction and use what it makes.
public static class Genome
{
    // Length is limited by what it costs (every byte is upkeep, copying and injecting cost per byte);
    // MaxLen is only a memory guard far beyond what any body can afford.
    public const int MinLen = 8, MaxLen = 4096;

    public const int Nop = 0, Push = 1, Lit = 2, Dup = 3, Drop = 4, Swap = 5, Over = 6, Rot = 7,
        Add = 8, Sub = 9, Mul = 10, Div = 11, Mod = 12, Neg = 13, Inc = 14, Dec = 15,
        Lt = 16, Eq = 17, Rand = 18, Label = 19, Jmp = 20, Jz = 21, Jnz = 22, Call = 23,
        Ret = 24, Skipz = 25, Yield = 26, Load = 27, Store = 28, Energy = 29, Age = 30, Have = 31,
        EnzymeOp = 32, MassOp = 33, Temp = 34, LightOp = 35, Sense = 36, Sensed = 37, Look = 38, Pick = 39,
        Feel = 40, Kin = 41, NGene = 42, Gene = 43, GLen = 44, Listen = 45, Emit = 46, Intake = 47,
        Expel = 48, Bind = 49, Split = 50, Divide = 51, Mine = 52, Attack = 53, Take = 54, Give = 55,
        Link = 56, Share = 57, Inject = 58, Cut = 59, Dig = 60, Pile = 61, Ground = 62, Smell = 63;

    public static readonly string[] Names =
    {
        "nop", "push", "lit", "dup", "drop", "swap", "over", "rot",
        "add", "sub", "mul", "div", "mod", "neg", "inc", "dec",
        "lt", "eq", "rand", "label", "jmp", "jz", "jnz", "call",
        "ret", "skipz", "yield", "load", "store", "energy", "age", "have",
        "enzyme", "mass", "temp", "light", "sense", "sensed", "look", "pick",
        "feel", "kin", "ngene", "gene", "glen", "listen", "emit", "intake",
        "expel", "bind", "split", "divide", "mine", "attack", "take", "give",
        "link", "share", "inject", "cut", "dig", "pile", "ground", "smell",
    };

    // Instruction kinds for statistics: the 64 instructions plus their second meanings.
    static readonly int[] Variants = { Split, Expel, Divide, Mine, Pile, Pick, Intake, Bind, Feel, Temp, LightOp, Nop };
    static readonly string[] VariantNames = { "photo", "push", "mate", "gnaw", "grow", "count", "drink", "digest", "hurt", "btemp", "photons", "swim" };
    public static readonly int OpSlots = 64 + Variants.Length;

    public static int Slot(int b)
    {
        int op = b & 63;
        if (b >> 6 >= 2)
            for (int k = 0; k < Variants.Length; k++)
                if (Variants[k] == op) return 64 + k;
        return op;
    }

    public static string SlotName(int slot) => slot < 64 ? Names[slot] : VariantNames[slot - 64];

    public static readonly string[] EnzymeKindEn = { "binding", "splitting", "light capture", "motor" };
    public static readonly string[] EnzymeKindRu = { "соединение", "расщепление", "захват света", "мотор" };
    public static string[] EnzymeKind => Loc.T(EnzymeKindEn, EnzymeKindRu);

    public static bool HasImm(int op) => op is Push or Label or Jmp or Jz or Jnz or Call;

    public static byte[] Random(SimRng r)
    {
        var g = new byte[r.Next(12, 41)];
        r.NextBytes(g);
        return g;
    }

    // The protein a gene (three bytes) makes.
    public static Enzyme Decode(byte b1, byte b2, byte b3)
    {
        uint h = Hash32.U((uint)(b1 | b2 << 8 | b3 << 16));
        return new Enzyme
        {
            Kind = (byte)(b1 & 3),
            Topt = -15f + ((b1 >> 2) & 63) * 0.8f,
            A = (byte)(b2 % Chemistry.S),
            B = (byte)(b3 % Chemistry.S),
            Eff = 0.35f + 0.65f * ((h >> 8) & 0xFFFF) / 65535f,
        };
    }

    // Copy errors: point changes, insertions, deletions (more often in long genomes), duplications. Bytes that have proven useful
    // (high protection) are copied more faithfully and resist deletion; duplicated pieces keep half
    // their protection — a working block can be copied and then tinkered with.
    // Works in two per-thread scratch buffers (the same random draws, in the same order, as the list
    // edits it replaced): only the child's two arrays are allocated.
    [ThreadStatic] static byte[] scratchG, scratchP;
    public static (byte[] g, byte[] p) Mutate(byte[] src, byte[] prot, SimRng r)
    {
        int n = src.Length;
        var g = scratchG ??= new byte[MaxLen + 32];
        var p = scratchP ??= new byte[MaxLen + 32];
        if (n + 14 > g.Length) { g = scratchG = new byte[n + 32]; p = scratchP = new byte[n + 32]; }
        Array.Copy(src, g, n);
        for (int i = 0; i < n; i++) p[i] = (byte)(prot[i] * 0.9f);
        for (int i = 0; i < n; i++)
            if (r.NextDouble() < 0.008 * (1 - p[i] / 320.0)) { g[i] = (byte)r.Next(256); p[i] = 0; }
        if (r.NextDouble() < 0.3 && n < MaxLen)
        {
            int at = r.Next(n + 1);
            Array.Copy(g, at, g, at + 1, n - at); Array.Copy(p, at, p, at + 1, n - at);
            g[at] = (byte)r.Next(256);
            p[at] = 0;
            n++;
        }
        if (r.NextDouble() < 0.3 + Math.Min(0.3, n / 400.0) && n > MinLen)
        {
            int at = r.Next(n);
            if (r.NextDouble() >= p[at] / 300.0)
            {
                Array.Copy(g, at + 1, g, at, n - at - 1); Array.Copy(p, at + 1, p, at, n - at - 1);
                n--;
            }
        }
        if (r.NextDouble() < 0.05 && n < MaxLen - 12)
        {
            int len = Math.Min(r.Next(2, 13), n), from = r.Next(n - len + 1), to = r.Next(n + 1);
            Span<byte> seg = stackalloc byte[len], sp = stackalloc byte[len];
            for (int k = 0; k < len; k++) { seg[k] = g[from + k]; sp[k] = (byte)(p[from + k] / 2); }
            Array.Copy(g, to, g, to + len, n - to); Array.Copy(p, to, p, to + len, n - to);
            seg.CopyTo(g.AsSpan(to)); sp.CopyTo(p.AsSpan(to));
            n += len;
        }
        return (g.AsSpan(0, n).ToArray(), p.AsSpan(0, n).ToArray());
    }

    // The list-based original, kept as the reference for the regression test.
    public static (byte[] g, byte[] p) MutateReference(byte[] src, byte[] prot, SimRng r)
    {
        int n = src.Length;
        var g = new List<byte>(src);
        var p = new List<byte>(n);
        for (int i = 0; i < n; i++) p.Add((byte)(prot[i] * 0.9f));
        for (int i = 0; i < g.Count; i++)
            if (r.NextDouble() < 0.008 * (1 - p[i] / 320.0)) { g[i] = (byte)r.Next(256); p[i] = 0; }
        if (r.NextDouble() < 0.3 && g.Count < MaxLen)
        {
            int at = r.Next(g.Count + 1);
            g.Insert(at, (byte)r.Next(256));
            p.Insert(at, 0);
        }
        if (r.NextDouble() < 0.3 + Math.Min(0.3, g.Count / 400.0) && g.Count > MinLen)
        {
            int at = r.Next(g.Count);
            if (r.NextDouble() >= p[at] / 300.0) { g.RemoveAt(at); p.RemoveAt(at); }
        }
        if (r.NextDouble() < 0.05 && g.Count < MaxLen - 12)
        {
            int len = Math.Min(r.Next(2, 13), g.Count), from = r.Next(g.Count - len + 1), to = r.Next(g.Count + 1);
            var seg = g.GetRange(from, len);
            var sp = p.GetRange(from, len).ConvertAll(v => (byte)(v / 2));
            g.InsertRange(to, seg);
            p.InsertRange(to, sp);
        }
        return (g.ToArray(), p.ToArray());
    }

    // Sexual recombination: the start of one parent's genome joined to the end of the other's,
    // cut at the same relative position.
    public static (byte[] g, byte[] p) Cross(Agent a, Agent b, SimRng r)
    {
        float f = 0.2f + 0.6f * (float)r.NextDouble();
        int ca = (int)(a.G.Length * f), cb = (int)(b.G.Length * f);
        int n = Math.Min(MaxLen, ca + b.G.Length - cb);
        var g = new byte[n];
        var p = new byte[n];
        Array.Copy(a.G, g, ca);
        Array.Copy(a.Prot, p, ca);
        Array.Copy(b.G, cb, g, ca, n - ca);
        Array.Copy(b.Prot, cb, p, ca, n - ca);
        return (g, p);
    }

    // Where the labels are: for each label id k (0..3) the sorted positions of "label k", packed in one
    // array: entries [t[k], t[k+1]) after the five offsets. A few dozen bytes per genome instead of a
    // table of 4 ints per byte; NextLabel answers the same question by binary search.
    static readonly int[] NoLabels = { 5, 5, 5, 5, 5 };
    public static int[] Labels(byte[] g)
    {
        int n = g.Length;
        Span<int> count = stackalloc int[4];
        count.Clear();
        for (int i = 0; i < n; i++) if ((g[i] & 63) == Label) count[g[i] >> 6]++;
        int total = count[0] + count[1] + count[2] + count[3];
        if (total == 0) return NoLabels;
        var t = new int[5 + total];
        t[0] = 5;
        for (int k = 0; k < 4; k++) t[k + 1] = t[k] + count[k];
        Span<int> fill = stackalloc int[4];
        for (int k = 0; k < 4; k++) fill[k] = t[k];
        for (int i = 0; i < n; i++) if ((g[i] & 63) == Label) t[fill[g[i] >> 6]++] = i;
        return t;
    }

    // The next "label k" after position i (wrapping around the genome), or -1 if there is none.
    public static int NextLabel(int[] labels, int i, int k)
    {
        int lo = labels[k], hi = labels[k + 1];
        if (lo == hi) return -1;
        int first = lo;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (labels[mid] > i) hi = mid; else lo = mid + 1;
        }
        return lo < labels[k + 1] ? labels[lo] : labels[first];
    }

    // The old table (4 ints per byte), kept as the reference for the regression test.
    public static int[] LabelTable(byte[] g)
    {
        int n = g.Length;
        var t = new int[n * 4];
        Span<int> next = stackalloc int[4];
        next.Fill(-1);
        for (int pass = 0; pass < 2; pass++)
            for (int i = n - 1; i >= 0; i--)
            {
                for (int k = 0; k < 4; k++) t[i * 4 + k] = next[k];
                if ((g[i] & 63) == Label) next[g[i] >> 6] = i;
            }
        return t;
    }

    // Locality-sensitive fingerprint: similar genomes get similar bits (used for kin recognition).
    // Bit b is set when more than half of the hashed 3-byte windows have it set. The 64 per-bit
    // counts are kept "bit-sliced" (plane k holds bit k of every count), so adding a window is a few
    // word operations instead of 64.
    public static ulong SimHash(byte[] g)
    {
        Span<ulong> plane = stackalloc ulong[13];   // counts up to 8191 > MaxLen
        plane.Clear();
        int n = g.Length;
        for (int i = 0; i < n; i++)
        {
            ulong carry = Mix((ulong)(g[i] | g[(i + 1) % n] << 8 | g[(i + 2) % n] << 16));
            for (int k = 0; carry != 0 && k < plane.Length; k++)
            {
                ulong p = plane[k];
                plane[k] = p ^ carry;
                carry &= p;
            }
        }
        ulong r = 0;
        for (int b = 0; b < 64; b++)
        {
            int count = 0;
            for (int k = 0; k < plane.Length; k++) count |= (int)((plane[k] >> b) & 1) << k;
            if (count * 2 > n) r |= 1UL << b;
        }
        return r;
    }

    static ulong Mix(ulong x)
    {
        x ^= x >> 33; x *= 0xff51afd7ed558ccdUL;
        x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL;
        x ^= x >> 33;
        return x;
    }

    public static ulong Hash(byte[] g)
    {
        ulong h = 1469598103934665603UL;
        foreach (var b in g) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    // Some instructions have a second meaning when their immediate bits are 2 or 3.
    public static string Dis(byte b)
    {
        int op = b & 63, imm = b >> 6;
        if (imm >= 2)
            switch (op)
            {
                case Split: return "photo";
                case Expel: return "push";
                case Divide: return "mate";
                case Mine: return "gnaw";
                case Pile: return "grow";
                case Pick: return "count";
                case Intake: return "drink";
                case Feel: return "hurt";
                case Bind: return "digest";
                case Temp: return "btemp";
                case LightOp: return "photons";
            }
        return HasImm(op) ? $"{Names[op]} {imm}" : Names[op];
    }

    // Disassembly with operands: `lit` carries one byte, `enzyme` carries a three-byte gene.
    public static string DisAt(byte[] g, int i, out int len)
    {
        int op = g[i] & 63, n = g.Length;
        len = 1;
        if (op == Lit) { len = 2; return $"lit {g[(i + 1) % n]}"; }
        if (op != EnzymeOp) return Dis(g[i]);
        len = 4;
        var e = Decode(g[(i + 1) % n], g[(i + 2) % n], g[(i + 3) % n]);
        string what = e.Kind switch
        {
            Enzyme.Bind => $"{e.A}+{e.B}",
            Enzyme.Split => $"{e.A}→",
            Enzyme.Photo => Loc.T($"light+{e.A}", $"свет+{e.A}"),
            _ => "",
        };
        return $"enzyme {EnzymeKind[e.Kind]} {what} {e.Topt:0}° q{e.Eff * 100:0}";
    }
}

// How an agent looks. A newborn of abiogenesis gets a look from its genome fingerprint; after that
// the look is inherited like a neutral marker: every child copies its parent with a tiny random drift
// and, rarely, a new body shape. Close relatives look nearly identical, branches of a family drift
// apart in colour over generations, a change of shape marks a new branch.
public static class Looks
{
    public const int ShapeCount = 8;
    public static readonly string[] ShapeNamesEn = { "sphere", "cube", "cone", "cylinder", "capsule", "prism", "ring", "rhomb" };
    public static readonly string[] ShapeNamesRu = { "шар", "куб", "конус", "цилиндр", "капсула", "призма", "кольцо", "ромб" };
    public static string[] ShapeNames => Loc.T(ShapeNamesEn, ShapeNamesRu);

    static readonly ulong[] Pat =
    {
        0x9E3779B97F4A7C15UL, 0xC2B2AE3D27D4EB4FUL, 0x165667B19E3779F9UL, 0xD6E8FEB86659FD93UL,
        0xFF51AFD7ED558CCDUL, 0xC4CEB9FE1A85EC53UL, 0x2545F4914F6CDD1DUL, 0x5851F42D4C957F2DUL,
    };

    static float Proj(ulong tag, int k) => 64 - 2 * BitOperations.PopCount(tag ^ Pat[k]);
    static float Sig(float v) => 1f / (1f + MathF.Exp(-v));
    static float Angle(float y, float x) => (MathF.Atan2(y, x) / (2 * MathF.PI) + 1f) % 1f;

    public static void Apply(Agent a)
    {
        ulong t = a.Tag;
        float p0 = Proj(t, 0), p1 = Proj(t, 1), p2 = Proj(t, 2), p3 = Proj(t, 3);
        float p4 = Proj(t, 4), p5 = Proj(t, 5), p6 = Proj(t, 6), p7 = Proj(t, 7);
        a.Hue = Angle(p1, p0);
        a.Sat = 0.55f + 0.4f * Sig(p2 / 5f);
        a.Val = 0.75f + 0.25f * Sig(p3 / 5f);
        a.Shape = (int)(Angle(p5, p4) * ShapeCount) % ShapeCount;
        a.Sy = 0.65f + 0.85f * Sig(p6 / 5f);
        a.Sx = 0.75f + 0.5f * Sig(p7 / 5f);
        a.Sz = 0.75f + 0.5f * Sig((p6 - p7) / 7f);
    }

    static float Gauss(SimRng r) => MathF.Sqrt(-2f * MathF.Log(1f - (float)r.NextDouble())) * MathF.Cos(2f * MathF.PI * (float)r.NextDouble());

    public static void Inherit(Agent child, Agent parent, SimRng r)
    {
        child.Hue = ((parent.Hue + 0.012f * Gauss(r)) % 1f + 1f) % 1f;
        child.Sat = Math.Clamp(parent.Sat + 0.01f * Gauss(r), 0.45f, 1f);
        child.Val = Math.Clamp(parent.Val + 0.01f * Gauss(r), 0.6f, 1f);
        child.Sx = Math.Clamp(parent.Sx * MathF.Exp(0.015f * Gauss(r)), 0.6f, 1.4f);
        child.Sy = Math.Clamp(parent.Sy * MathF.Exp(0.015f * Gauss(r)), 0.5f, 1.7f);
        child.Sz = Math.Clamp(parent.Sz * MathF.Exp(0.015f * Gauss(r)), 0.6f, 1.4f);
        child.Shape = r.NextDouble() < 0.01 ? r.Next(ShapeCount) : parent.Shape;
    }

    // How closely two agents are related judging by their looks: 0 = different family, 1 = twins.
    public static float Kin(Agent a, Agent b)
    {
        if (a.Lineage != b.Lineage) return 0;
        float dh = MathF.Abs(a.Hue - b.Hue);
        dh = MathF.Min(dh, 1 - dh);
        float d = dh * 2f + MathF.Abs(a.Sat - b.Sat) + MathF.Abs(a.Val - b.Val) + (a.Shape != b.Shape ? 0.15f : 0f);
        return Math.Max(0f, 1f - d / 0.5f);
    }
}
