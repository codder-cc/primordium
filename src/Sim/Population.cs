using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Primordium;

// A population template: real bodies copied out of a world (a lineage, a clade or everything in an
// area) — every body's genome bytes, the molecules it is made of (whole, partly absorbed and folded
// into its proteins), its proteins, its free energy, its looks, where it stood relative to the others
// and who descended from whom. Pasting it (World.PastePopulation) never makes anything out of nothing:
// the matter comes from the place or is brought in from outside (booked in World.HandInput), the
// energy from local reactions or from outside (World.HandEnergy), exactly like a planted design.
//
// Molecule species are numbers in a generated chemistry: the template keeps the chemistry it was taken
// from (seed + every species' formula and properties), so a world with another chemistry maps each
// body molecule to its nearest species there (ChemMap) and says what changed.
public sealed class PopulationTemplate
{
    public const string FormatName = "primordium-population";
    public const int MaxBodies = 20000;   // a memory guard for files, far above any brushed area

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Source { get; set; } = "";          // lineage | clade | area | bodies
    public int Seed { get; set; }                     // the world it was taken from
    public long Tick { get; set; }
    public PopulationChemistry Chemistry { get; set; } = new();
    public List<PopulationBody> Bodies { get; set; } = new();

    public int Lineages => Bodies.Select(b => b.Lineage).Distinct().Count();
    public long Molecules => Bodies.Sum(b => (long)(b.Body?.Values.Sum() ?? 0));
    public double Energy => Bodies.Sum(b => b.Energy);
    public int Width => Bodies.Count == 0 ? 0 : Bodies.Max(b => b.Dx) - Bodies.Min(b => b.Dx) + 1;
    public int Height => Bodies.Count == 0 ? 0 : Bodies.Max(b => b.Dy) - Bodies.Min(b => b.Dy) + 1;

    // Everything wrong with the file (empty: it can be pasted).
    public List<string> Check()
    {
        var errors = new List<string>();
        if (Format != FormatName) errors.Add(Loc.T($"not a population file (format '{Format}')", $"не файл популяции (формат «{Format}»)"));
        if (Version < 1 || Version > 1) errors.Add(Loc.T($"population format version {Version}, this build reads 1", $"версия формата популяции {Version}, эта сборка читает 1"));
        if (Bodies == null || Bodies.Count == 0) errors.Add(Loc.T("no bodies", "нет тел"));
        else if (Bodies.Count > MaxBodies) errors.Add(Loc.T($"{Bodies.Count} bodies: more than {MaxBodies}", $"{Bodies.Count} тел: больше {MaxBodies}"));
        if (Chemistry?.Molecules == null || Chemistry.Molecules.Count != Primordium.Chemistry.S)
            errors.Add(Loc.T($"the chemistry signature must list {Primordium.Chemistry.S} species", $"подпись химии должна перечислять {Primordium.Chemistry.S} видов"));
        if (Bodies != null)
            for (int k = 0; k < Bodies.Count && errors.Count < 8; k++)
            {
                var why = Bodies[k].Check();
                if (why != null) errors.Add(Loc.T($"body {k + 1}: {why}", $"тело {k + 1}: {why}"));
            }
        return errors;
    }

    static readonly JsonSerializerOptions json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string ToJson() => JsonSerializer.Serialize(this, json);
    public static PopulationTemplate FromJson(string text) => JsonSerializer.Deserialize<PopulationTemplate>(text) ?? throw new InvalidDataException("empty population");
    public PopulationTemplate Clone() => FromJson(ToJson());
}

// One body of a template. Species keys are species numbers of the template's chemistry.
public sealed class PopulationBody
{
    public long Id { get; set; }          // in the source world (relations below refer to these)
    public long Lineage { get; set; }
    public long Parent { get; set; }      // the body it came from, 0 for a founder (may be outside the template)
    public int Gen { get; set; }
    public int Dx { get; set; }           // column relative to the template's anchor (x wraps around the planet)
    public int Dy { get; set; }
    public int Rise { get; set; }         // its floor against the top of its column (0 on the surface, < 0 in a cave)
    public int Floor { get; set; }        // its floor against the anchor column's top (relative heights)
    public float Lift { get; set; }       // swimming height above its floor
    public string Genome { get; set; } = "";   // base64 of the exact bytes
    // Its life model's key (LifeModels.ByKey); absent (null) for model 1.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Model { get; set; }
    public string Prot { get; set; }           // base64: how much each byte has proven useful (optional)
    public double Energy { get; set; }
    public float Hue { get; set; }
    public float Sat { get; set; }
    public float Val { get; set; }
    public float Sx { get; set; } = 1;
    public float Sy { get; set; } = 1;
    public float Sz { get; set; } = 1;
    public int Shape { get; set; }
    public bool Designed { get; set; }    // it was planted by the player in the source world
    public Dictionary<string, int> Body { get; set; } = new();     // species → whole molecules
    public Dictionary<string, long> Pend { get; set; } = new();    // species → partly absorbed (Qty.Raw, 2⁻³² molecule)
    public List<ProteinInfo> Proteins { get; set; } = new();

    public byte[] GenomeBytes() => Convert.FromBase64String(Genome ?? "");
    public byte[] ProtBytes(int length)
    {
        if (string.IsNullOrEmpty(Prot)) return new byte[length];
        var p = Convert.FromBase64String(Prot);
        if (p.Length != length) Array.Resize(ref p, length);
        return p;
    }

    public static int Species(string key) =>
        int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s >= 0 && s < Chemistry.S ? s : -1;

    public string Check()
    {
        byte[] g;
        try { g = GenomeBytes(); }
        catch (FormatException) { return Loc.T("the genome is not base64", "геном не в base64"); }
        if (LifeModels.ByKey(Model) == null) return Loc.T($"unknown life model '{Model}'", $"неизвестная модель жизни «{Model}»");
        if (g.Length < Primordium.Genome.MinLen || g.Length > Primordium.Genome.MaxLen) return Loc.T($"genome of {g.Length} bytes", $"геном из {g.Length} байт");
        if (!string.IsNullOrEmpty(Prot)) { try { Convert.FromBase64String(Prot); } catch (FormatException) { return Loc.T("Prot is not base64", "Prot не в base64"); } }
        if (!(Energy >= 0) || double.IsInfinity(Energy)) return Loc.T("energy must be ≥ 0", "энергия должна быть ≥ 0");
        int total = 0;
        foreach (var (k, n) in Body ?? new())
        {
            if (Species(k) < 0) return Loc.T($"unknown species '{k}'", $"неизвестный вид «{k}»");
            if (n < 0) return Loc.T($"{k}: negative count", $"{k}: отрицательное количество");
            total += n;
        }
        foreach (var (k, n) in Pend ?? new())
            if (Species(k) < 0 || n < 0 || n >= 64L << Qty.Bits) return Loc.T($"bad partial matter '{k}'", $"неверное частичное вещество «{k}»");
        foreach (var e in Proteins ?? new())
            if (e.Kind < 0 || e.Kind > 3 || e.A < 0 || e.A >= Chemistry.S || e.B < 0 || e.B >= Chemistry.S || e.Material < 0 || e.Material >= Chemistry.S || e.Matter < 0 || !(e.Amount >= 0))
                return Loc.T("bad protein", "неверный белок");
        if (total < P.MinBody) return Loc.T($"{total} molecules: fewer than {P.MinBody} cannot hold together", $"{total} молекул: меньше {P.MinBody} не держится");
        return null;
    }
}

// A protein the body holds: the reaction it speeds up (A, B are species numbers), how much of it there
// is and the matter it is folded from (Matter: Qty.Raw of species Material).
public sealed class ProteinInfo
{
    public int Kind { get; set; }
    public int A { get; set; }
    public int B { get; set; }
    public float Topt { get; set; }
    public float Eff { get; set; }
    public float Amount { get; set; }
    public int Material { get; set; }
    public long Matter { get; set; }
    public int Src { get; set; } = -1;
}

// The chemistry a template was taken from: the seed and every species' formula and properties (enough
// to recognise the same chemistry and to find the nearest species in another one).
public sealed class PopulationChemistry
{
    public int Seed { get; set; }
    public float[] ElementMass { get; set; } = Array.Empty<float>();
    public List<MoleculeInfo> Molecules { get; set; } = new();

    public static PopulationChemistry Of(Chemistry c, int seed)
    {
        var sig = new PopulationChemistry { Seed = seed, ElementMass = (float[])c.AtomicMass.Clone() };
        for (int s = 0; s < Chemistry.S; s++)
        {
            var atoms = new int[Chemistry.ElementCount];
            for (int e = 0; e < atoms.Length; e++) atoms[e] = c.Atoms[s, e];
            sig.Molecules.Add(new MoleculeInfo
            {
                S = s, NameEn = c.NameEn[s], NameRu = c.NameRu[s], Formula = c.Formula(s), Atoms = atoms, Excited = (s & 1) == 1,
                Mass = c.Mass[s], E = c.E[s], Bond = c.Bond[s], Solid = c.Solid[s], Gas = s == c.Gas,
            });
        }
        return sig;
    }
}

public sealed class MoleculeInfo
{
    public int S { get; set; }
    public string NameEn { get; set; }
    public string NameRu { get; set; }
    public string Formula { get; set; }
    public int[] Atoms { get; set; } = Array.Empty<int>();
    public bool Excited { get; set; }
    public float Mass { get; set; }
    public int E { get; set; }
    public float Bond { get; set; }
    public bool Solid { get; set; }
    public bool Gas { get; set; }
}

// Template species → this world's species. In the same chemistry it is the identity; in another one
// every species goes to the nearest species here: the same formula and excitation if there is one
// (atoms per element kept), else the closest by composition, excitation, the gas role, mass, bond
// energy, bond strength and solidity. Several species may land on one.
public sealed class ChemMap
{
    public readonly int[] To = new int[Chemistry.S];
    public bool Same;
    public readonly List<(int from, int to, bool formula, string en, string ru)> Changes = new();

    public static ChemMap Build(PopulationChemistry from, Chemistry to, int seed)
    {
        var map = new ChemMap();
        var here = PopulationChemistry.Of(to, seed);
        bool same = from?.Molecules != null && from.Molecules.Count == Chemistry.S;
        for (int s = 0; s < Chemistry.S && same; s++) same = Equal(from.Molecules[s], here.Molecules[s]);
        map.Same = same;
        for (int s = 0; s < Chemistry.S; s++) map.To[s] = s;
        if (same || from?.Molecules == null) return map;
        float meanMass = Math.Max(1e-3f, here.Molecules.Average(m => m.Mass));
        foreach (var m in from.Molecules)
        {
            if (m.S < 0 || m.S >= Chemistry.S) continue;
            int best = 0;
            double bestD = double.MaxValue;
            foreach (var t in here.Molecules)
            {
                double d = Distance(m, t, meanMass);
                if (d < bestD) { bestD = d; best = t.S; }
            }
            map.To[m.S] = best;
            var b = here.Molecules[best];
            bool formula = SameAtoms(m, b) && m.Excited == b.Excited;
            string F(MoleculeInfo x) => $"{x.Formula}{(x.Excited ? "*" : "")}";
            map.Changes.Add((m.S, best, formula,
                $"{m.S} {m.NameEn} ({F(m)}, mass {m.Mass:0.##}, bond energy {m.E}) → {best} {b.NameEn} ({F(b)}, mass {b.Mass:0.##}, bond energy {b.E})" + (formula ? "" : " — another formula: the atoms change"),
                $"{m.S} {m.NameRu} ({F(m)}, масса {m.Mass:0.##}, энергия связей {m.E}) → {best} {b.NameRu} ({F(b)}, масса {b.Mass:0.##}, энергия связей {b.E})" + (formula ? "" : " — другая формула: атомы меняются")));
        }
        return map;
    }

    static bool SameAtoms(MoleculeInfo a, MoleculeInfo b) => a.Atoms != null && b.Atoms != null && a.Atoms.SequenceEqual(b.Atoms);

    static bool Equal(MoleculeInfo a, MoleculeInfo b) =>
        SameAtoms(a, b) && a.Excited == b.Excited && a.E == b.E && a.Gas == b.Gas && a.Solid == b.Solid &&
        Math.Abs(a.Mass - b.Mass) < 1e-4f && Math.Abs(a.Bond - b.Bond) < 1e-4f;

    static double Distance(MoleculeInfo a, MoleculeInfo b, float meanMass)
    {
        double d = 0;
        int n = Math.Min(a.Atoms?.Length ?? 0, b.Atoms?.Length ?? 0);
        for (int e = 0; e < n; e++) d += 2.0 * Math.Abs(a.Atoms[e] - b.Atoms[e]);
        if (a.Excited != b.Excited) d += 3;
        if (a.Gas != b.Gas) d += 3;
        if (a.Solid != b.Solid) d += 0.5;
        d += Math.Abs(a.Mass - b.Mass) / meanMass;
        d += Math.Abs(a.E - b.E) / 6.0;
        d += Math.Abs(a.Bond - b.Bond);
        return d;
    }

    public int Changed => Changes.Count(c => c.from != c.to || !c.formula);

    // Protein genes (`enzyme` and its three bytes) name their molecules by species number: point them
    // at the mapped species. The genome is read straight through like the disassembler does; code
    // reached only by jumping into the middle of an instruction, and molecule numbers the code puts on
    // the stack (push/lit) are left as they are. The quality of a remapped gene changes with its bytes.
    public byte[] RemapGenes(byte[] g, out int genes)
    {
        genes = 0;
        var r = (byte[])g.Clone();
        if (Same) return r;
        for (int i = 0; i < r.Length;)
        {
            int op = r[i] & 63;
            if (op == Genome.Lit) { i += 2; continue; }
            if (op != Genome.EnzymeOp || i + 3 >= r.Length) { i++; continue; }
            byte b2 = r[i + 2], b3 = r[i + 3];
            int a = To[b2 % Chemistry.S], b = To[b3 % Chemistry.S];
            r[i + 2] = (byte)((b2 & ~31) | a);
            r[i + 3] = (byte)((b3 & ~31) | b);
            if (r[i + 2] != b2 || r[i + 3] != b3) genes++;
            i += 4;
        }
        return r;
    }
}

// A folder of population templates as JSON files (the game passes user://populations, globalized).
public static class PopulationLibrary
{
    public sealed class Entry
    {
        public string Path;
        public PopulationTemplate Template;   // null if the file could not be read
        public string Error;
    }

    public static string Save(PopulationTemplate t, string dir, string fileName = null)
    {
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, fileName ?? CreatureLibrary.FileName(t.Name));
        File.WriteAllText(path, t.ToJson());
        return path;
    }

    public static PopulationTemplate Load(string path) => PopulationTemplate.FromJson(File.ReadAllText(path));

    public static List<Entry> List(string dir)
    {
        var list = new List<Entry>();
        if (!Directory.Exists(dir)) return list;
        foreach (var path in Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var e = new Entry { Path = path };
            try { e.Template = Load(path); }
            catch (Exception x) { e.Error = x.Message; }
            list.Add(e);
        }
        return list;
    }
}

public sealed class PasteOptions
{
    public MatterSource Matter { get; set; } = MatterSource.Local;   // Local: each body from its own cell, 4 neighbours, soft top block
    public EnergySource Energy { get; set; } = EnergySource.Local;   // Local: downhill reactions of loose matter there (World.PlanLocalEnergy)
    public bool KeepRelations { get; set; } = true;   // bodies of one source lineage share a new lineage, parents stay parents
    public bool RemapGenes { get; set; } = true;      // in another chemistry: protein genes point at the mapped species
}

public sealed class PasteResult
{
    public int Requested, Made, GenesRemapped;
    public readonly List<Agent> Agents = new();
    public readonly List<long> SourceIds = new();   // the template body each pasted body was made from
    public readonly List<long> Lineages = new();
    public readonly double[] AtomsImported = new double[Chemistry.ElementCount];   // booked in World.HandInput
    public double EnergyImported, EnergyLocal;
    public readonly Dictionary<string, int> Failures = new();   // why bodies were not made, how many
    public ChemMap Map;
    public string Error;   // the template cannot be pasted at all, or the most common failure
    public bool Ok => Made > 0;
    public override string ToString() => $"{Made}/{Requested} pasted" + (Error != null ? $" ({Error})" : "");
}
