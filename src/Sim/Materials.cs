using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Primordium;

// The matter library (MatterWindow, key J): substances as recipes of this world's real molecules — number
// fractions by species, a lattice order (0..255, which sets packing and porosity: World.PackedVolume) and a
// form (laid as blocks, or scattered loose). A name, a kind ("poison", "diamond", "organic"…) and a colour
// are only the player's labels on such a recipe: nothing in the engine reads them, and no material has code
// of its own. What a recipe does follows from the laws: its density, cohesion and strength from the
// molecules and the order, its harm from the reactive-damage law (World.React), its energy from the bond
// energies. Entries are JSON files in user://materials and carry the chemistry they were made in (seed
// and a descriptor per species), so a file from another world is mapped onto this world's molecules by
// nearest properties — never by inventing a molecule.
public sealed class MatterPart
{
    public int Species { get; set; }
    public double Fraction { get; set; }   // by number of molecules (normalised when used)
}

// One species of the chemistry a recipe was made in: its composition and the properties the mapping
// compares.
public sealed class SpeciesInfo
{
    public string Name { get; set; }
    public int[] Atoms { get; set; }
    public float Mass { get; set; }
    public float Bond { get; set; }
    public float Packing { get; set; }
    public float Affinity { get; set; }   // mean affinity of its atoms
    public int E { get; set; }
    public float Excitation { get; set; }
}

public sealed class ElementInfo
{
    public float Mass { get; set; }
    public float Valence { get; set; }
    public float Affinity { get; set; }
}

public sealed class ChemSignature
{
    public int Seed { get; set; }
    public List<ElementInfo> Elements { get; set; } = new();
    public List<SpeciesInfo> Species { get; set; } = new();

    public static ChemSignature Of(World w)
    {
        var ch = w.Chem;
        var sig = new ChemSignature { Seed = w.Seed };
        for (int e = 0; e < Chemistry.ElementCount; e++) sig.Elements.Add(new ElementInfo { Mass = ch.AtomicMass[e], Valence = ch.Valence[e], Affinity = ch.Affinity[e] });
        for (int s = 0; s < Chemistry.S; s++)
        {
            var atoms = new int[Chemistry.ElementCount];
            for (int e = 0; e < atoms.Length; e++) atoms[e] = ch.Atoms[s, e];
            sig.Species.Add(new SpeciesInfo
            {
                Name = ch.NameEn[s], Atoms = atoms, Mass = ch.Mass[s], Bond = ch.Bond[s], Packing = ch.Packing[s],
                Affinity = ch.AffinityPerAtom[s], E = ch.E[s], Excitation = ch.Excitation[s],
            });
        }
        return sig;
    }

    // The same chemistry: every species has the same composition and energy (the seed alone is not
    // enough — another generator version would give the same seed other molecules).
    public bool Matches(Chemistry ch)
    {
        if (Species == null || Species.Count != Chemistry.S) return false;
        for (int s = 0; s < Chemistry.S; s++)
        {
            var d = Species[s];
            if (d?.Atoms == null || d.Atoms.Length != Chemistry.ElementCount || d.E != ch.E[s]) return false;
            for (int e = 0; e < Chemistry.ElementCount; e++) if (d.Atoms[e] != ch.Atoms[s, e]) return false;
        }
        return true;
    }
}

// What a recipe is like, computed by the engine's own formulas (see each line).
public sealed class MatterProps
{
    public double MolPerBlock;     // molecules that fill one voxel at this order (World.BlockCapacity)
    public double MassPerMol, Density;   // mean mass of a molecule; mass per unit of volume (block mass / P.VoxelSpace)
    public double Porosity;        // share of the block's room beyond the molecules' ordered packing
    public double Cohesion;        // mean bond × how well the kinds fit (World.CoreCohesion)
    public double Barrier;         // what holds a molecule in the face against gnawing (World.VoxelBarrier)
    public double Compression;     // what a full block bears (World.CompressionCapacity)
    public double ReactHeld;       // reactive-damage chance per held molecule per tick at 15 °C (World.React)
    public double ReactLying;      // the same for a molecule lying in the cell
    public double HarmVsDecay;     // protein activity one held molecule destroys per tick, in units of P.EnzDecay
    public double EnergyPerMol, EnergyPerBlock;   // bond energy (Chemistry.E)
    public double GasShare;        // share of the world's gas
    public int Kinds;              // species in it
}

public sealed class MatterMapping
{
    public double[] Mix = new double[Chemistry.S];   // number fractions by this world's species
    public bool Exact = true;                        // made in this chemistry (no mapping)
    public readonly List<(string from, int to, double distance)> Map = new();
    public string Warning;                           // in the language of the moment
}

public sealed class MatterRecipe
{
    public const int FormatVersion = 1;
    public int Version { get; set; } = FormatVersion;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Kind { get; set; } = "";          // the player's label (see Kinds); never read by the engine
    public string Colour { get; set; }              // optional "#rrggbb" tag for the list
    public int Order { get; set; }                  // lattice order 0..255 (World.Order)
    public string Form { get; set; } = "block";     // block | loose
    public string Source { get; set; } = "";        // how it was made (composed, sampled at …, generated)
    public List<MatterPart> Parts { get; set; } = new();
    [JsonPropertyName("Chemistry")] public ChemSignature Signature { get; set; }    // the chemistry it was made in (null: this world's)

    [JsonIgnore] public bool Generated;
    [JsonIgnore] public string Path;

    public bool Loose => Form == "loose";

    public static readonly string[] Kinds = { "rock", "mineral", "mixture", "gas", "organic", "poison", "other" };
    static readonly string[] KindsEn = { "rock", "mineral / crystal", "mixture", "gas", "organic", "poison", "other" };
    static readonly string[] KindsRu = { "порода", "минерал / кристалл", "смесь", "газ", "органика", "яд", "другое" };
    public static string KindName(string k) { int i = Array.IndexOf(Kinds, k); return i < 0 ? (k ?? "") : Loc.T(KindsEn[i], KindsRu[i]); }

    static readonly JsonSerializerOptions json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string ToJson() => JsonSerializer.Serialize(this, json);
    public static MatterRecipe FromJson(string text)
    {
        var r = JsonSerializer.Deserialize<MatterRecipe>(text, json) ?? throw new FormatException("empty file");
        r.Parts ??= new();
        if (r.Parts.Count == 0) throw new FormatException(Loc.T("the recipe has no molecules", "в рецепте нет молекул"));
        r.Order = Math.Clamp(r.Order, 0, 255);
        if (r.Form != "loose") r.Form = "block";
        return r;
    }

    public MatterRecipe Clone()
    {
        var c = JsonSerializer.Deserialize<MatterRecipe>(ToJson(), json);   // no validation: a draft may be empty
        c.Generated = false;
        return c;
    }

    // A recipe of this world from number fractions (or counts) by species.
    public static MatterRecipe FromMix(World w, double[] mix, int order, bool loose, string name, string source, string kind = null)
    {
        var r = new MatterRecipe { Name = name, Order = Math.Clamp(order, 0, 255), Form = loose ? "loose" : "block", Source = source, Signature = ChemSignature.Of(w) };
        double sum = mix.Sum();
        for (int s = 0; s < Chemistry.S; s++) if (mix[s] > 0) r.Parts.Add(new MatterPart { Species = s, Fraction = Math.Round(mix[s] / sum, 6) });
        r.Kind = kind ?? Classify(w.Chem, r.MixOf(w.Chem), r.Order, false);
        return r;
    }

    // Number fractions by species of this world, without mapping (parts outside the range are dropped).
    public double[] MixOf(Chemistry ch)
    {
        var mix = new double[Chemistry.S];
        foreach (var p in Parts) if (p.Species >= 0 && p.Species < Chemistry.S && p.Fraction > 0) mix[p.Species] += p.Fraction;
        Normalise(mix);
        return mix;
    }

    static void Normalise(double[] mix)
    {
        double sum = mix.Sum();
        if (sum > 0) for (int s = 0; s < mix.Length; s++) mix[s] /= sum;
    }

    // ---- this world's molecules for the recipe ----

    // The recipe in this world: its own species if it was made in this chemistry; otherwise every species
    // of the recipe's chemistry goes to the nearest species of this one by properties (composition by
    // element role, mass, bond, packing, excitation, energy), with the mapping shown and a warning.
    public MatterMapping Resolve(World w)
    {
        var ch = w.Chem;
        var m = new MatterMapping();
        if (Signature == null || Signature.Matches(ch))
        {
            m.Mix = MixOf(ch);
            return m;
        }
        m.Exact = false;
        int[] roleHere = Roles(Enumerable.Range(0, Primordium.Chemistry.ElementCount).Select(e => ch.AtomicMass[e]).ToArray());
        int[] roleThere = Signature.Elements?.Count == Primordium.Chemistry.ElementCount ? Roles(Signature.Elements.Select(e => e.Mass).ToArray()) : new[] { 0, 1, 2, 3 };
        foreach (var p in Parts)
        {
            if (p.Fraction <= 0) continue;
            var d = p.Species >= 0 && p.Species < (Signature.Species?.Count ?? 0) ? Signature.Species[p.Species] : null;
            if (d?.Atoms == null || d.Atoms.Length != Primordium.Chemistry.ElementCount)
            {
                m.Warning = Loc.T("the file's chemistry is incomplete: some molecules were left out", "химия файла неполна: часть молекул пропущена");
                continue;
            }
            int best = 0;
            double bestD = double.MaxValue;
            for (int t = 0; t < Primordium.Chemistry.S; t++)
            {
                double dist = Distance(d, roleThere, ch, t, roleHere);
                if (dist < bestD) { bestD = dist; best = t; }
            }
            m.Mix[best] += p.Fraction;
            m.Map.Add(($"{d.Name ?? "?"} ({FormulaOf(d.Atoms)})", best, bestD));
        }
        Normalise(m.Mix);
        string seed = Signature.Seed.ToString(CultureInfo.InvariantCulture);
        m.Warning = (m.Warning == null ? "" : m.Warning + ". ") +
                    Loc.T($"made in another chemistry (seed {seed}): its molecules are replaced by the nearest of this world by properties — the substance is not the same",
                          $"сделано в другой химии (seed {seed}): молекулы заменены ближайшими по свойствам молекулами этого мира — вещество не то же самое");
        return m;
    }

    // Element roles: rank by atomic mass (0 the lightest), the only order two worlds' elements share.
    static int[] Roles(float[] mass)
    {
        var order = Enumerable.Range(0, mass.Length).OrderBy(e => mass[e]).ThenBy(e => e).ToArray();
        var role = new int[mass.Length];
        for (int k = 0; k < order.Length; k++) role[order[k]] = k;
        return role;
    }

    static string FormulaOf(int[] atoms) => string.Concat(Enumerable.Range(0, atoms.Length).Where(e => atoms[e] > 0).Select(e => (char)('A' + e) + atoms[e].ToString(CultureInfo.InvariantCulture)));

    static double Distance(SpeciesInfo d, int[] roleThere, Chemistry ch, int t, int[] roleHere)
    {
        var fa = new double[Primordium.Chemistry.ElementCount];
        var fb = new double[Primordium.Chemistry.ElementCount];
        double na = Math.Max(1, d.Atoms.Sum()), nb = Math.Max(1, ch.AtomCount(t));
        for (int e = 0; e < fa.Length; e++) { fa[roleThere[e]] += d.Atoms[e] / na; fb[roleHere[e]] += ch.Atoms[t, e] / nb; }
        double comp = 0;
        for (int k = 0; k < fa.Length; k++) comp += Math.Abs(fa[k] - fb[k]);
        double mass = Math.Abs(Math.Log(Math.Max(0.01, d.Mass) / Math.Max(0.01, ch.Mass[t])));
        double bond = Math.Abs(d.Bond - ch.Bond[t]) / (d.Bond + ch.Bond[t] + 0.1);
        double pack = Math.Abs(d.Packing - ch.Packing[t]);
        double excited = (d.Excitation > 0) != (ch.Excitation[t] > 0) ? 1 : 0;
        double energy = Math.Abs(Math.Log((d.E + 1.0) / (ch.E[t] + 1.0)));
        double size = Math.Abs(na - nb) / 6.0;
        return comp + 0.5 * mass + 0.8 * bond + 0.4 * pack + 0.6 * excited + 0.2 * energy + 0.3 * size;
    }

    // ---- properties, by the engine's formulas ----

    // As World.PackedVolume: the room a molecule takes in a block of this order.
    public static float PackedVolume(Chemistry ch, int s, int order) => ch.Volume[s] * (1 + P.Bulking * ch.Looseness[s] * (1 - order / 255f));

    public static MatterProps Props(Chemistry ch, double[] mix, int order)
    {
        var p = new MatterProps();
        double vol = 0, vol0 = 0, mass = 0, bond = 0, purity = 0, react = 0, energy = 0;
        for (int a = 0; a < Chemistry.S; a++)
        {
            double f = mix[a];
            if (f <= 0) continue;
            p.Kinds++;
            vol += f * PackedVolume(ch, a, order);
            vol0 += f * ch.Volume[a];
            mass += f * ch.Mass[a];
            bond += f * ch.Bond[a];
            react += f * World.ReactChance(ch, a, 15);
            energy += f * ch.E[a];
            if (a == ch.Gas) p.GasShare = f;
            for (int b = 0; b < Chemistry.S; b++) if (mix[b] > 0) purity += f * mix[b] * ch.Contact[a + 2, b + 2];
        }
        if (p.Kinds == 0) return p;
        p.MolPerBlock = P.VoxelSpace / vol;
        p.MassPerMol = mass;
        p.Density = mass * p.MolPerBlock / P.VoxelSpace;
        p.Porosity = 1 - vol0 / vol;
        p.Cohesion = Math.Max(1e-4, bond * purity);
        double o = order / 255.0;
        p.Barrier = P.RockBarrier * (0.15 + p.Cohesion * p.Cohesion) * (0.35 + o);
        p.Compression = P.CompressionK * (0.3 + p.Cohesion) * (P.LooseStrength + (1.5 - P.LooseStrength) * o * o);
        p.ReactHeld = react;
        p.ReactLying = react * P.ReactContact;
        p.HarmVsDecay = P.EnzDecay > 0 ? react * P.ReactWear / P.EnzDecay : 0;
        p.EnergyPerMol = energy;
        p.EnergyPerBlock = energy * Math.Floor(p.MolPerBlock);
        return p;
    }

    // A label for a new entry, from its properties (the player may change it; nothing reads it).
    public static string Classify(Chemistry ch, double[] mix, int order, bool body)
    {
        if (body) return "organic";
        var p = Props(ch, mix, order);
        if (p.GasShare >= 0.5) return "gas";
        if (p.HarmVsDecay >= 1) return "poison";
        if (p.Kinds == 1 && order >= 160) return "mineral";
        if (p.Kinds >= 3 || mix.Max() < 0.8) return "mixture";
        return "rock";
    }

    // "Kazu 60 %, Mira* 40 %" — the main kinds of a mix (number shares), for lists and the chronicle.
    public static string MixText(Chemistry ch, double[] mix, bool en, int most = 4)
    {
        var parts = Enumerable.Range(0, Chemistry.S).Where(s => mix[s] > 0).OrderByDescending(s => mix[s]).ThenBy(s => s).ToList();
        var shown = parts.Take(most).Select(s => $"{(en ? ch.NameEn[s] : ch.NameRu[s])} {mix[s] * 100:0.#}%");
        return string.Join(", ", shown) + (parts.Count > most ? (en ? $" and {parts.Count - most} more" : $" и ещё {parts.Count - most}") : "");
    }

    public static string CountsText(Chemistry ch, long[] counts, bool en)
    {
        var mix = new double[Chemistry.S];
        for (int s = 0; s < Chemistry.S; s++) mix[s] = counts[s];
        Normalise(mix);
        return MixText(ch, mix, en);
    }

    // ---- taken from the world (read only) ----

    // The top block of a column, exactly: its molecules by kind and its lattice order.
    public static MatterRecipe SampleBlock(World w, int cell)
    {
        int h = Math.Clamp(w.Height[cell], 0, w.Z);
        if (h < 3) return null;
        int v = cell * w.Z + h - 1;
        if (w.Mat[v] < 2) return null;
        var mix = new double[Chemistry.S];
        for (int s = 0; s < Chemistry.S; s++) mix[s] = w.VoxelCount(v, s);
        if (mix.Sum() <= 0) return null;
        int order = w.Order[v];
        return FromMix(w, mix, order, false, Loc.T($"block at ({cell % w.W}, {cell / w.W}, level {h - 1})", $"блок в ({cell % w.W}, {cell / w.W}, уровень {h - 1})"),
            $"sampled block ({cell % w.W}, {cell / w.W}, {h - 1}), tick {w.Tick}: {mix.Sum():0} molecules, order {order}");
    }

    // Loose matter lying on the surface of a column (the air's gas left out unless that is all there is).
    public static MatterRecipe SampleLoose(World w, int cell)
    {
        var mix = new double[Chemistry.S];
        for (int s = 0; s < Chemistry.S; s++) if (s != w.Chem.Gas) mix[s] = Math.Floor(w.C[s][cell].F);
        if (mix.Sum() <= 0) mix[w.Chem.Gas] = Math.Floor(w.C[w.Chem.Gas][cell].F);
        if (mix.Sum() <= 0) return null;
        return FromMix(w, mix, 0, true, Loc.T($"loose matter at ({cell % w.W}, {cell / w.W})", $"рыхлое в ({cell % w.W}, {cell / w.W})"),
            $"sampled loose matter ({cell % w.W}, {cell / w.W}), tick {w.Tick}: {mix.Sum():0} molecules");
    }

    // The molecules a body holds (its protein substrate and partial uptake left out).
    public static MatterRecipe SampleBody(World w, Agent a)
    {
        if (a == null || a.Dead || a.InvTotal == 0) return null;
        var mix = new double[Chemistry.S];
        for (int s = 0; s < Chemistry.S; s++) mix[s] = a.Inv[s];
        return FromMix(w, mix, 0, true, Loc.T($"body of #{a.Id}", $"тело #{a.Id}"), $"sampled body #{a.Id} (lineage #{a.Lineage}), tick {w.Tick}: {a.InvTotal} molecules", "organic");
    }

    // ---- computed for each world from its chemistry (never fixed species) ----

    public static List<MatterRecipe> Starters(World w)
    {
        var ch = w.Chem;
        var list = new List<MatterRecipe>();
        var solid = Enumerable.Range(0, Chemistry.S).Where(s => s != ch.Gas).ToArray();
        double[] One(int s) { var m = new double[Chemistry.S]; m[s] = 1; return m; }
        MatterRecipe Add(double[] mix, int order, bool loose, string en, string ru, string den, string dru, string kind)
        {
            var r = FromMix(w, mix, order, loose, Loc.T(en, ru), "generated from this world's chemistry", kind);
            r.Description = Loc.T(den, dru);
            r.Generated = true;
            list.Add(r);
            return r;
        }
        // Hardest: the strongest bond (cohesion of a pure block), at full order — the barrier and the
        // compression strength both peak there.
        int hard = solid.OrderByDescending(s => ch.Bond[s]).ThenBy(s => s).First();
        Add(One(hard), 255, false, $"Hardest crystal ({ch.NameEn[hard]})", $"Самый твёрдый кристалл ({ch.NameRu[hard]})",
            "the molecule with the strongest bond of this world, fully ordered and compacted (a 'diamond' of this chemistry)",
            "молекула с самой прочной связью в этом мире, полностью упорядоченная и уплотнённая («алмаз» этой химии)", "mineral");
        // Densest: the most mass per packed room.
        int dense = solid.OrderByDescending(s => ch.Mass[s] / ch.Volume[s]).ThenBy(s => s).First();
        Add(One(dense), 255, false, $"Densest compacted ({ch.NameEn[dense]})", $"Самое плотное ({ch.NameRu[dense]})",
            "the most mass per packed volume, at full order", "больше всего массы на упакованный объём, при полном порядке", "mineral");
        // Most reactive: the three species the reactive-damage law rates highest, in proportion to it.
        var reactive = Enumerable.Range(0, Chemistry.S).Where(s => ch.Reactivity[s] > 0).OrderByDescending(s => ch.Reactivity[s]).ThenBy(s => s).Take(3).ToArray();
        var rmix = new double[Chemistry.S];
        foreach (int s in reactive) rmix[s] = ch.Reactivity[s];
        Add(rmix, 0, true, "Most reactive mixture", "Самая реакционная смесь",
            "the three molecules with the highest reactivity (atom affinity × excitation energy) in proportion to it: harmful by the reactive-damage law",
            "три молекулы с наибольшей реактивностью (сродство атомов × энергия возбуждения), в её пропорции: вредна по закону реактивного урона", "poison");
        // Lightest volatile: the world's gas (lightest and least bound, Chemistry.Gas).
        Add(One(ch.Gas), 0, true, $"Lightest volatile ({ch.NameEn[ch.Gas]})", $"Самое летучее ({ch.NameRu[ch.Gas]})",
            "the world's gas: least mass × bond; scattered it joins the air", "газ мира: наименьшие масса × связь; рассыпанный, уходит в воздух", "gas");
        // Energy-rich deposit: the most bond energy per packed volume, as a loosely ordered sediment.
        int rich = solid.OrderByDescending(s => ch.E[s] / ch.Volume[s]).ThenBy(s => s).First();
        var richMix = One(rich);
        int richGround = Primordium.Chemistry.Ground(rich);
        if (richGround != rich && richGround != ch.Gas) { richMix[rich] = 0.7; richMix[richGround] = 0.3; }
        Add(richMix, 60, false, $"Energy-rich deposit ({ch.NameEn[rich]})", $"Энергоёмкая залежь ({ch.NameRu[rich]})",
            "the most bond energy per volume (with some of its ground state), a loosely ordered sediment", "больше всего энергии связи на объём (с долей основного состояния), рыхло упорядоченный осадок", "rock");
        // Softest heap: the weakest bond, disordered.
        int soft = solid.OrderBy(s => ch.Bond[s]).ThenBy(s => s).First();
        Add(One(soft), 0, false, $"Softest heap ({ch.NameEn[soft]})", $"Самая мягкая насыпь ({ch.NameRu[soft]})",
            "the weakest bond, freshly heaped: crumbles and slides", "самая слабая связь, свежая насыпь: крошится и сползает", "rock");
        // Inert filler: the three ground states of least energy (nothing for the reactive law to spend).
        var inert = solid.Where(s => ch.Excitation[s] == 0).OrderBy(s => ch.E[s]).ThenBy(s => s).Take(3).ToArray();
        var imix = new double[Chemistry.S];
        foreach (int s in inert) imix[s] = 1;
        Add(imix, 25, false, "Inert low-energy fill", "Инертная бедная засыпка",
            "three ground states of the least bond energy: harmless by the reactive law, little to eat",
            "три основных состояния с наименьшей энергией связи: безвредны по закону реактивности, есть почти нечего", "mixture");
        return list;
    }
}

public static class MatterLibrary
{
    public sealed class Entry
    {
        public string Path;
        public MatterRecipe Recipe;   // null if the file could not be read
        public string Error;
    }

    public static string FileName(string name)
    {
        var sb = new StringBuilder();
        foreach (char ch in string.IsNullOrWhiteSpace(name) ? "matter" : name.Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
        return sb + ".json";
    }

    public static string Save(MatterRecipe r, string dir)
    {
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, FileName(r.Name));
        File.WriteAllText(path, r.ToJson());
        r.Path = path;
        return path;
    }

    public static List<Entry> List(string dir)
    {
        var list = new List<Entry>();
        if (!Directory.Exists(dir)) return list;
        foreach (var path in Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var e = new Entry { Path = path };
            try { e.Recipe = MatterRecipe.FromJson(File.ReadAllText(path)); e.Recipe.Path = path; }
            catch (Exception x) { e.Error = x.Message; }
            list.Add(e);
        }
        return list;
    }
}
