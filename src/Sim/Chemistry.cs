using System;
using System.Collections.Generic;
using System.Linq;

namespace Primordium;

// A finite, seeded artificial chemistry, not a table of terrestrial rocks. Every reaction preserves
// its element vector. Each formula has a ground and excited state; photons change state, not atoms.
public sealed class Chemistry
{
    public const int S = 32, ElementCount = 4, Common = 8;
    public const byte Air = 0, Bedrock = 1;

    public readonly float[] AtomicMass = new float[ElementCount], Valence = new float[ElementCount], Affinity = new float[ElementCount];
    public readonly string[] ElementName = new string[ElementCount];
    public readonly int[,] Atoms = new int[S, ElementCount];
    public readonly int[] E = new int[S], SplitA = new int[S], SplitB = new int[S], PhotoUp = new int[S];
    public readonly int[,] Combine = new int[S, S];
    public readonly float[] Mass = new float[S], Diff = new float[S], Bond = new float[S], Packing = new float[S];
    // How much more room a molecule takes in a disordered heap than in its ordered lattice, relative to
    // P.Bulking: irregular, poorly packing molecules bulk most (1.15 − packing: 0.15 … 1).
    public readonly float[] Looseness = new float[S];
    public readonly float[] Volume = new float[S];   // room one molecule takes: its mass over how tightly it packs
    public readonly float[] BodyVolume = new float[S];   // room it takes held in a body: the gas is a bubble there
    public readonly bool[] SplitExo = new bool[S], Poison = new bool[S], Solid = new bool[S];
    public readonly Rgb[] Col = new Rgb[S];
    public readonly string[] Name = new string[S];
    public readonly int[] Low, Unstable, Toxic, Solids, VentHigh, VentMid;
    public readonly int Richest, Gas;
    public readonly int MatCount = S + 2;
    public readonly string[] MatName = new string[S + 2];
    public readonly float[] MatHard = new float[S + 2], MatBarrier = new float[S + 2], MatCohesion = new float[S + 2];
    public readonly bool[] MatLoose = new bool[S + 2];
    public readonly int[] MatTier = new int[S + 2], MatKey = new int[S + 2];
    public readonly int[] MatCap = new int[S + 2];   // molecules of a kind that fill a voxel by volume (a full block)
    public readonly Rgb[] MatCol = new Rgb[S + 2];
    public readonly byte[] BuiltMat = new byte[S];
    public readonly float[,] Contact = new float[S + 2, S + 2];
    static readonly string[] Syl = { "ка", "зу", "ми", "ро", "те", "ла", "во", "экс", "ши", "ан", "пу", "др", "ом", "ри", "не", "гу", "са", "ки", "ул", "бе" };

    public Chemistry(int seed)
    {
        var r = new Random(unchecked(seed * 7919 + 17));
        var colours = new Rgb[ElementCount];
        for (int e = 0; e < ElementCount; e++)
        {
            ElementName[e] = ((char)('A' + e)).ToString();
            AtomicMass[e] = 0.5f + 1.5f * (float)r.NextDouble();
            Valence[e] = 1 + r.Next(4);
            Affinity[e] = 0.2f + 1.8f * (float)r.NextDouble();
            colours[e] = Rgb.Hsv((float)r.NextDouble(), 0.35f, 0.8f);
        }
        Array.Fill(SplitA, -1); Array.Fill(SplitB, -1); Array.Fill(PhotoUp, -1);
        var names = new HashSet<string>();
        for (int f = 0; f < S / 2; f++)
        {
            int s = f * 2, a = -1, b = -1;
            if (f < ElementCount) Atoms[s, f] = 1;
            else
            {
                // Always assemble from smaller existing formulas. Rejection prevents duplicate formulas.
                bool unique;
                do
                {
                    a = 2 * r.Next(f); b = 2 * r.Next(Math.Min(f, ElementCount));
                    for (int e = 0; e < ElementCount; e++) Atoms[s, e] = Atoms[a, e] + Atoms[b, e];
                    unique = AtomCount(s) <= 6;
                    for (int t = 0; t < s && unique; t += 2) if (SameFormula(s, t)) unique = false;
                } while (!unique);
            }
            float mass = 0, affinity = 0, valence = 0, cr = 0, cg = 0, cb = 0;
            int count = AtomCount(s);
            for (int e = 0; e < ElementCount; e++)
            {
                Atoms[s + 1, e] = Atoms[s, e];
                mass += Atoms[s, e] * AtomicMass[e];
                affinity += Atoms[s, e] * Affinity[e]; valence += Atoms[s, e] * Valence[e];
                cr += Atoms[s, e] * colours[e].R; cg += Atoms[s, e] * colours[e].G; cb += Atoms[s, e] * colours[e].B;
            }
            E[s] = f < ElementCount ? 0 : r.Next(2, 13);
            E[s + 1] = E[s] + r.Next(3, 9);
            string name;
            do name = Syl[r.Next(Syl.Length)] + Syl[r.Next(Syl.Length)]; while (!names.Add(name));
            name = char.ToUpper(name[0]) + name[1..];
            for (int t = s; t <= s + 1; t++)
            {
                Name[t] = name + (t == s ? "" : "*");
                Mass[t] = mass;
                Bond[t] = (affinity / count) * (valence / count) * (0.25f + count * 0.12f) / (1 + (t - s) * 0.5f);
                Packing[t] = Math.Clamp(valence / (count * 4f), 0.15f, 1f);
                Looseness[t] = 1.15f - Packing[t];
                Volume[t] = mass / (0.5f + Packing[t]);
                Diff[t] = 0.13f / MathF.Sqrt(mass);
                Solid[t] = Bond[t] >= 0.85f;
                Poison[t] = t != s && affinity / count > 1.1f;
                Col[t] = new Rgb(cr / count, cg / count, cb / count).Mul(t == s ? 0.8f : 1f);
                SplitA[t] = a; SplitB[t] = b;
            }
            // Excited monomers relax; complex molecules can dissociate into their constituents.
            if (a < 0) { SplitA[s + 1] = s; SplitB[s + 1] = -1; }
            PhotoUp[s] = s + 1;
        }
        for (int a = 0; a < S; a++)
            for (int b = 0; b < S; b++)
            {
                Combine[a, b] = -1;
                for (int t = 0; t < S; t += 2)
                {
                    bool fits = true;
                    for (int e = 0; e < ElementCount; e++) if (Atoms[a, e] + Atoms[b, e] != Atoms[t, e]) { fits = false; break; }
                    if (fits) { Combine[a, b] = t; break; }
                }
            }
        for (int s = 0; s < S; s++)
        {
            SplitExo[s] = SplitA[s] >= 0 && SplitEnergy(s) > 0;
            if (E[s] > E[Richest]) Richest = s;
        }
        int[] Where(Func<int, bool> f) => Enumerable.Range(0, S).Where(f).ToArray();
        Low = Where(s => E[s] <= 5 && !Poison[s]);
        Unstable = Where(s => SplitExo[s]); Toxic = Where(s => Poison[s]); Solids = Where(s => Solid[s]);
        // The most volatile species has an atmospheric reservoir. It is never destroyed by water.
        Gas = Enumerable.Range(0, S).OrderBy(s => Mass[s] * (0.1f + Bond[s])).First();
        VentHigh = Enumerable.Range(0, S).OrderByDescending(s => E[s]).Take(8).ToArray();
        VentMid = Low;
        Array.Fill(MatKey, -1);
        MatName[Air] = "пустота"; MatName[Bedrock] = "граница недр";

        MatCol[Bedrock] = new Rgb(0.22f, 0.21f, 0.24f);
        MatTier[Bedrock] = 5; MatHard[Bedrock] = MatBarrier[Bedrock] = 1e9f;
        for (int s = 0; s < S; s++)
        {
            int m = s + 2;
            BuiltMat[s] = (byte)m;
            MatName[m] = $"агрегат {Name[s]}";
            MatCol[m] = Col[s]; MatKey[m] = s;
            MatCohesion[m] = Bond[s];
            MatBarrier[m] = 0.2f + Bond[s] * Bond[s];
            MatHard[m] = 0.2f + Bond[s] * 1.5f;
            MatTier[m] = Math.Clamp((int)(MatBarrier[m] / 2), 0, 4);
            MatLoose[m] = Bond[s] < 1;
        }
        for (int a = 2; a < MatCount; a++)
            for (int b = 2; b < MatCount; b++)
            {
                float overlap = 0;
                for (int e = 0; e < ElementCount; e++) overlap += Math.Min(Atoms[a - 2, e], Atoms[b - 2, e]);
                float similarity = overlap / Math.Max(AtomCount(a - 2), AtomCount(b - 2));
                // Unlike aggregates still interlock (0.2), similar ones more; a monolith is 2.5–5× stronger.
                Contact[a, b] = a == b ? 1f : 0.2f + 0.18f * similarity * similarity;
            }
        ApplyParams();
    }

    // What depends on the world's laws (P) rather than on the seed: a full block's count of each kind
    // (P.VoxelSpace) and the room the gas takes held in a body (P.GasExpand). World calls it again when
    // those laws change (World.ApplyParamChanges).
    public void ApplyParams()
    {
        for (int s = 0; s < S; s++)
        {
            BodyVolume[s] = s == Gas ? Volume[s] * P.GasExpand : Volume[s];
            MatCap[s + 2] = Math.Clamp((int)MathF.Round(P.VoxelSpace / Volume[s]), 1, ushort.MaxValue);
        }
    }

    public int AtomCount(int s) { int n = 0; for (int e = 0; e < ElementCount; e++) n += Atoms[s, e]; return n; }
    public bool SameFormula(int a, int b) { for (int e = 0; e < ElementCount; e++) if (Atoms[a, e] != Atoms[b, e]) return false; return true; }
    public int SplitEnergy(int s) => SplitA[s] < 0 ? 0 : E[s] - E[SplitA[s]] - (SplitB[s] < 0 ? 0 : E[SplitB[s]]);
    public string Formula(int s) => string.Concat(Enumerable.Range(0, ElementCount).Where(e => Atoms[s, e] > 0).Select(e => ElementName[e] + Atoms[s, e]));
    public static int Spec(int v) => ((v % S) + S) % S;
}
