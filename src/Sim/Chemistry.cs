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
    public readonly bool[] SplitExo = new bool[S], Solid = new bool[S];
    // The reactive-damage law's species factor (World.Life, P.ReactK): how hard a molecule hits the
    // protein substrate of a body it touches — the mean affinity of its atoms × the excitation energy it
    // carries (E above its own ground state; 0 for a ground state). Computed for every species alike:
    // there is no poison class, a "poison" is whatever this law makes harmful.
    public readonly float[] AffinityPerAtom = new float[S], Excitation = new float[S], Reactivity = new float[S];
    public readonly Rgb[] Col = new Rgb[S];
    // Molecule names in both languages (built from the same syllable draws, so the language never
    // touches the random stream); Name gives the current language's.
    public readonly string[] NameEn = new string[S], NameRu = new string[S];
    public string[] Name => Loc.En ? NameEn : NameRu;
    public readonly int[] Low, Unstable, Solids, VentHigh, VentMid, Excited;
    public readonly int Richest, Gas;
    public readonly int MostReactive;   // the species with the highest Reactivity (ties: the lower index)
    public readonly int MatCount = S + 2;
    public readonly string[] MatNameEn = new string[S + 2], MatNameRu = new string[S + 2];
    public string[] MatName => Loc.En ? MatNameEn : MatNameRu;
    public readonly float[] MatHard = new float[S + 2], MatBarrier = new float[S + 2], MatCohesion = new float[S + 2];
    public readonly bool[] MatLoose = new bool[S + 2];
    public readonly int[] MatTier = new int[S + 2], MatKey = new int[S + 2];
    public readonly int[] MatCap = new int[S + 2];   // molecules of a kind that fill a voxel by volume (a full block)
    public readonly Rgb[] MatCol = new Rgb[S + 2];
    public readonly byte[] BuiltMat = new byte[S];
    public readonly float[,] Contact = new float[S + 2, S + 2];
    static readonly string[] Syl = { "ка", "зу", "ми", "ро", "те", "ла", "во", "экс", "ши", "ан", "пу", "др", "ом", "ри", "не", "гу", "са", "ки", "ул", "бе" };
    static readonly string[] SylEn = { "ka", "zu", "mi", "ro", "te", "la", "vo", "ex", "shi", "an", "pu", "dr", "om", "ri", "ne", "gu", "sa", "ki", "ul", "be" };

    // How the energies were made (P.ChemEnergyModel when the world was created): 0 — drawn at random
    // (the legacy chemistry), 1 — from the composition and the bonds (FormationEnergies below).
    public readonly int Model;
    // Model 1 (0 for model 0): each ground state's energy of formation from its elements, before the
    // per-atom offset (negative: the bonds released energy when it formed), and the per-atom offset
    // (EnergyZero) that keeps every E ≥ 0. Caged: atoms the formula holds that no bond reaches.
    public readonly float[] Formation = new float[S];
    public readonly int[] Caged = new int[S];
    public int EnergyZero { get; private set; }

    public Chemistry(int seed) : this(seed, P.ChemEnergyModel) { }

    public Chemistry(int seed, int model)
    {
        Model = model;
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
            string name, nameEn;
            int s1, s2;
            do { s1 = r.Next(Syl.Length); s2 = r.Next(Syl.Length); name = Syl[s1] + Syl[s2]; } while (!names.Add(name));
            nameEn = SylEn[s1] + SylEn[s2];
            name = char.ToUpper(name[0]) + name[1..];
            nameEn = char.ToUpper(nameEn[0]) + nameEn[1..];
            for (int t = s; t <= s + 1; t++)
            {
                NameRu[t] = name + (t == s ? "" : "*");
                NameEn[t] = nameEn + (t == s ? "" : "*");
                Mass[t] = mass;
                Bond[t] = (affinity / count) * (valence / count) * (0.25f + count * 0.12f) / (1 + (t - s) * 0.5f);
                Packing[t] = Math.Clamp(valence / (count * 4f), 0.15f, 1f);
                Looseness[t] = 1.15f - Packing[t];
                Volume[t] = mass / (0.5f + Packing[t]);
                Diff[t] = 0.13f / MathF.Sqrt(mass);
                Solid[t] = Bond[t] >= 0.85f;
                AffinityPerAtom[t] = affinity / count;
                Col[t] = new Rgb(cr / count, cg / count, cb / count).Mul(t == s ? 0.8f : 1f);
                SplitA[t] = a; SplitB[t] = b;
            }
            // Excited monomers relax; complex molecules can dissociate into their constituents.
            if (a < 0) { SplitA[s + 1] = s; SplitB[s + 1] = -1; }
            PhotoUp[s] = s + 1;
        }
        // The legacy draws above stay (the random stream, and with it every formula, name and colour, is
        // the same in both models); model 1 replaces the energies only.
        if (Model == 1) FormationEnergies();
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
            Excitation[s] = E[s] - E[Ground(s)];
            Reactivity[s] = AffinityPerAtom[s] * Excitation[s];
            if (Reactivity[s] > Reactivity[MostReactive]) MostReactive = s;
        }
        var down = new List<Reaction>();
        for (int s = 0; s < S; s++) if (SplitExo[s]) down.Add(new Reaction(s, -1, -1, SplitEnergy(s)));
        for (int a = 0; a < S; a++)
            for (int b = a; b < S; b++)
                if (Combine[a, b] >= 0 && E[a] + E[b] > E[Combine[a, b]]) down.Add(new Reaction(a, b, Combine[a, b], E[a] + E[b] - E[Combine[a, b]]));
        Downhill = down.OrderByDescending(q => q.Energy).ToArray();   // stable: splits, then binds, in index order
        int[] Where(Func<int, bool> f) => Enumerable.Range(0, S).Where(f).ToArray();
        // Low-energy ground states: nothing in them for the reactive-damage law to spend.
        // (Model 1: ground states bound at least as low as their elements, with no downhill split.)
        Low = Model == 0 ? Where(s => E[s] <= 5 && Excitation[s] == 0)
                         : Where(s => Excitation[s] == 0 && !SplitExo[s] && E[s] <= EnergyZero * AtomCount(s));
        Unstable = Where(s => SplitExo[s]); Solids = Where(s => Solid[s]); Excited = Where(s => Excitation[s] > 0);
        // The most volatile species has an atmospheric reservoir. It is never destroyed by water.
        Gas = Enumerable.Range(0, S).OrderBy(s => Mass[s] * (0.1f + Bond[s])).First();
        // The vents' energetic ejecta: the highest E (model 1: the most energy above the elements they are
        // made of — excited and crowded molecules — since E there also counts the per-atom offset).
        VentHigh = Enumerable.Range(0, S).OrderByDescending(s => Model == 0 ? E[s] : E[s] - EnergyZero * AtomCount(s)).Take(8).ToArray();
        VentMid = Low;
        Array.Fill(MatKey, -1);
        MatNameEn[Air] = "void"; MatNameRu[Air] = "пустота";
        MatNameEn[Bedrock] = "bedrock boundary"; MatNameRu[Bedrock] = "граница недр";

        MatCol[Bedrock] = new Rgb(0.22f, 0.21f, 0.24f);
        MatTier[Bedrock] = 5; MatHard[Bedrock] = MatBarrier[Bedrock] = 1e9f;
        for (int s = 0; s < S; s++)
        {
            int m = s + 2;
            BuiltMat[s] = (byte)m;
            MatNameEn[m] = $"{NameEn[s]} aggregate";
            MatNameRu[m] = $"агрегат {NameRu[s]}";
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

    // Model 1: energies from composition and bonds (a Pauling-like additive bond model). The reference
    // is every element in its own aggregate (each atom bonded to its like), energy 0. A molecule's atoms
    // are joined by a tree of bonds (n − 1 for n atoms), each atom taking no more bonds than its
    // valence; the tree is the one the bonds favour most (greedy, from the highest-valence atom). A bond
    // between unlike atoms i–j gives up two half like-bonds for one unlike bond, and the difference is
    // Pauling's ionic resonance energy, ChemIonicK·(χi − χj)² with χ = Affinity (electronegativity):
    // so the formation energy is −Σ_tree ChemIonicK·Δχ² — compounds of unlike atoms are bound lower than
    // their elements, compounds of one element are neutral. An atom the tree cannot reach (every atom
    // that could hold it has used its valence) is caged: it keeps no bond and loses its v/2 like-bonds,
    // ChemIonicK·χ·v/2 each — such crowded formulas are energy-rich and fall apart by themselves.
    // Every ground state's E = round(EnergyZero·atoms + formation), EnergyZero the smallest whole
    // per-atom offset that keeps all E ≥ 0: an offset per atom changes no reaction's ΔE (atoms are
    // conserved), only where zero is. Excitation (the excited state's E above its ground) is the gap a
    // photon must bridge, ChemExciteK × √(mean electronegativity of the atoms) (at least 1): tightly
    // held electrons need bigger photons, with a diminishing return (affinities 0.2–2 give a gap within
    // a factor of 3, so no world is left with photons too small to live on). Ties in the tree go to the higher valence, then the lower element.
    void FormationEnergies()
    {
        float k = P.ChemIonicK;
        float worst = 0;
        var el = new int[8];
        var free = new int[8];
        var inTree = new bool[8];
        for (int s = 0; s < S; s += 2)
        {
            int n = 0;
            for (int e = 0; e < ElementCount; e++) for (int j = 0; j < Atoms[s, e]; j++) el[n++] = e;
            int start = 0;
            for (int j = 1; j < n; j++) if (Valence[el[j]] > Valence[el[start]]) start = j;
            for (int j = 0; j < n; j++) { free[j] = (int)Valence[el[j]]; inTree[j] = false; }
            inTree[start] = true;
            float energy = 0;
            int caged = 0;
            for (int added = 1; added < n; added++)
            {
                int bi = -1, bj = -1;
                float best = -1;
                for (int i = 0; i < n; i++)
                {
                    if (!inTree[i] || free[i] <= 0) continue;
                    for (int j = 0; j < n; j++)
                    {
                        if (inTree[j] || free[j] <= 0) continue;
                        float d = Affinity[el[i]] - Affinity[el[j]], g = k * d * d;
                        if (g > best || (g == best && (Valence[el[j]] > Valence[el[bj]] || (Valence[el[j]] == Valence[el[bj]] && el[j] < el[bj])))) { best = g; bi = i; bj = j; }
                    }
                }
                if (bi < 0) break;
                inTree[bj] = true; free[bi]--; free[bj]--;
                energy -= best;
            }
            for (int j = 0; j < n; j++)
                if (!inTree[j]) { caged++; energy += k * Affinity[el[j]] * Valence[el[j]] / 2; }
            Formation[s] = Formation[s + 1] = energy;
            Caged[s] = Caged[s + 1] = caged;
            worst = Math.Max(worst, -energy / n);
        }
        int zero = (int)MathF.Ceiling(worst - 1e-4f);
        for (int s = 0; s < S; s += 2)
        {
            int n = AtomCount(s);
            E[s] = Math.Max(0, (int)MathF.Round(zero * n + Formation[s]));
            E[s + 1] = E[s] + Math.Max(1, (int)MathF.Round(P.ChemExciteK * MathF.Sqrt(AffinityPerAtom[s])));
        }
        EnergyZero = zero;
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

    // A downhill reaction: B < 0 — the split of A (into SplitA/SplitB), otherwise the bind A + B → P;
    // Energy is what it releases (> 0).
    public readonly record struct Reaction(int A, int B, int P, int Energy);
    // Every exothermic split and bind, the most energetic first (ties: splits before binds, lower species first).
    public readonly Reaction[] Downhill;

    public static int Ground(int s) => s & ~1;   // the ground state of the formula (species come in ground/excited pairs)

    public int AtomCount(int s) { int n = 0; for (int e = 0; e < ElementCount; e++) n += Atoms[s, e]; return n; }
    public bool SameFormula(int a, int b) { for (int e = 0; e < ElementCount; e++) if (Atoms[a, e] != Atoms[b, e]) return false; return true; }
    public int SplitEnergy(int s) => SplitA[s] < 0 ? 0 : E[s] - E[SplitA[s]] - (SplitB[s] < 0 ? 0 : E[SplitB[s]]);
    public string Formula(int s) => string.Concat(Enumerable.Range(0, ElementCount).Where(e => Atoms[s, e] > 0).Select(e => ElementName[e] + Atoms[s, e]));
    public static int Spec(int v) => ((v % S) + S) % S;
}
