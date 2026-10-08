using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Primordium;

// A creature the player designs: a genome (GenomeAsm text), what its body should be made of and
// with how much energy it starts. Planting it (World.SpawnDesign) never makes matter or energy out of
// nothing: the molecules come from the place (or are brought in from outside and booked in
// World.HandInput, like the pour brush), the energy from local reactions (or is brought in and booked
// in World.HandEnergy).
public sealed class CreatureDesign
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Genome { get; set; } = "";   // GenomeAsm text (another life model: that model's text, ILifeModel.Describe)
    // The life model's key (LifeModels.ByKey); absent (null) for model 1, so older files mean model 1.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Model { get; set; }
    // Molecule → count. Keys: a species number 0–31, "gas" (the world's volatile species), "any"
    // (whatever is at hand), or a molecule's name or formula in the world's chemistry. Species
    // numbers mean different molecules in different seeds (the chemistry is generated): 0, 2, 4, 6
    // are always the four single elements, odd numbers the excited states.
    public Dictionary<string, int> Body { get; set; } = new();
    public float Energy { get; set; } = 30;
    // Looks (optional): otherwise derived from the genome's fingerprint like a self-assembled body.
    public float? Hue { get; set; }
    public float? Sat { get; set; }
    public float? Val { get; set; }
    public int? Shape { get; set; }

    public CreatureDesign Clone() => FromJson(ToJson());

    public ILifeModel Life() => LifeModels.ByKey(Model);

    public byte[] Assemble()
    {
        var life = Life() ?? throw new InvalidDataException(UnknownModel());
        if (life.Id == LifeModels.Vm) return GenomeAsm.Assemble(Genome);
        if (!life.TryCompile(Genome, out var g, out var errors)) throw new InvalidDataException(string.Join("; ", errors));
        return g;
    }

    string UnknownModel() => Loc.T($"unknown life model '{Model}'", $"неизвестная модель жизни «{Model}»");

    public const int AnyMolecule = -1;

    // The body request in species numbers (AnyMolecule for "any"); problems are added to `errors`.
    public Dictionary<int, int> ResolveBody(Chemistry chem, List<string> errors)
    {
        var body = new Dictionary<int, int>();
        foreach (var (key, count) in Body ?? new())
        {
            int s = ResolveMolecule(chem, key);
            if (s == int.MinValue) { errors.Add(Loc.T($"unknown molecule '{key}'", $"неизвестная молекула «{key}»")); continue; }
            if (count < 0) { errors.Add(Loc.T($"{key}: negative count", $"{key}: отрицательное количество")); continue; }
            if (count == 0) continue;
            body.TryGetValue(s, out int had);
            body[s] = had + count;
        }
        return body;
    }

    public static int ResolveMolecule(Chemistry chem, string key)
    {
        key = (key ?? "").Trim();
        if (key.Equals("any", StringComparison.OrdinalIgnoreCase) || key == "*") return AnyMolecule;
        if (key.Equals("gas", StringComparison.OrdinalIgnoreCase)) return chem.Gas;
        if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s)) return s >= 0 && s < Chemistry.S ? s : int.MinValue;
        for (s = 0; s < Chemistry.S; s++)   // a name in either language
            if (chem.NameEn[s].Equals(key, StringComparison.OrdinalIgnoreCase) || chem.NameRu[s].Equals(key, StringComparison.OrdinalIgnoreCase)) return s;
        for (s = 0; s < Chemistry.S; s += 2)   // a formula means its ground state
            if (chem.Formula(s).Equals(key, StringComparison.OrdinalIgnoreCase)) return s;
        return int.MinValue;
    }

    // Everything wrong with the design for this chemistry (empty: it can be planted).
    public List<string> Check(Chemistry chem)
    {
        var errors = new List<string>();
        var life = Life();
        if (life == null) errors.Add(UnknownModel());
        else if (!life.TryCompile(Genome, out _, out var asm)) errors.AddRange(asm);
        var body = ResolveBody(chem, errors);
        int total = body.Values.Sum();
        if (total < P.MinBody) errors.Add(Loc.T($"a body of {total} molecules: fewer than {P.MinBody} cannot hold together", $"тело из {total} молекул: меньше {P.MinBody} не держится"));
        if (!(Energy >= 0) || float.IsInfinity(Energy)) errors.Add(Loc.T("energy must be ≥ 0", "энергия должна быть ≥ 0"));
        return errors;
    }

    // A design copied from a living body: its genome as text, its molecules, its looks.
    public static CreatureDesign FromAgent(World w, Agent a, string name = null)
    {
        var d = new CreatureDesign
        {
            Name = name ?? Loc.T($"lineage {a.Lineage} #{a.Id}", $"линия {a.Lineage} #{a.Id}"),
            Description = Loc.T($"Taken from creature #{a.Id} (lineage {a.Lineage}, generation {a.Gen}, world {w.Seed}, tick {w.Tick}).",
                                $"Снято с существа #{a.Id} (линия {a.Lineage}, поколение {a.Gen}, мир {w.Seed}, тик {w.Tick})."),
            Genome = a.Life.Describe(a.G),
            Model = LifeModels.KeyFor(a.Model),
            Energy = MathF.Round((float)Math.Max(0, a.Energy)),
            Hue = a.Hue, Sat = a.Sat, Val = a.Val, Shape = a.Shape,
        };
        for (int s = 0; s < Chemistry.S; s++) if (a.Inv[s] > 0) d.Body[s.ToString(CultureInfo.InvariantCulture)] = a.Inv[s];
        return d;
    }

    static readonly JsonSerializerOptions json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string ToJson() => JsonSerializer.Serialize(this, json);
    public static CreatureDesign FromJson(string text) => JsonSerializer.Deserialize<CreatureDesign>(text) ?? throw new InvalidDataException("empty design");
}

// Where planted molecules and energy come from.
public enum MatterSource { Local, Import }
public enum EnergySource { Local, Import }

public sealed class SpawnOptions
{
    public MatterSource Matter { get; set; } = MatterSource.Local;   // Local: from the cell and its 4 neighbours (fails if short)
    public EnergySource Energy { get; set; } = EnergySource.Local;   // Local: exothermic splits of loose matter there
    public int Count { get; set; } = 1;
    public long Lineage { get; set; }                               // 0: a new lineage (the first planted body's id)
    public int Radius { get; set; } = 6;                            // further bodies go to random cells this close
}

public sealed class SpawnResult
{
    public int Requested, Made;
    public long Lineage;
    public readonly List<Agent> Agents = new();
    public readonly double[] AtomsImported = new double[Chemistry.ElementCount];   // booked in World.HandInput
    public double EnergyImported, EnergyLocal;                                      // HandEnergy / from local reactions
    public string Error;   // why the design (or the last failed body) could not be planted
    public bool Ok => Made > 0;
    public override string ToString() => $"{Made}/{Requested} planted" + (Error != null ? $" ({Error})" : "");
}

// A folder of designs as JSON files (the game passes user://creatures, globalized).
public static class CreatureLibrary
{
    public sealed class Entry
    {
        public string Path;
        public CreatureDesign Design;   // null if the file could not be read
        public string Error;
    }

    public static string FileName(string name)
    {
        var sb = new StringBuilder();
        foreach (char ch in string.IsNullOrWhiteSpace(name) ? "creature" : name.Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
        return sb + ".json";
    }

    public static string Save(CreatureDesign d, string dir)
    {
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, FileName(d.Name));
        File.WriteAllText(path, d.ToJson());
        return path;
    }

    public static CreatureDesign Load(string path) => CreatureDesign.FromJson(File.ReadAllText(path));

    public static List<Entry> List(string dir)
    {
        var list = new List<Entry>();
        if (!Directory.Exists(dir)) return list;
        foreach (var path in Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var e = new Entry { Path = path };
            try { e.Design = Load(path); }
            catch (Exception x) { e.Error = x.Message; }
            list.Add(e);
        }
        return list;
    }

    // Writes the built-in examples into the folder (existing files are kept unless overwrite).
    public static int ExportExamples(string dir, bool overwrite = false)
    {
        int n = 0;
        foreach (var d in CreatureExamples.All)
        {
            string path = System.IO.Path.Combine(dir, FileName(d.Name));
            if (!overwrite && File.Exists(path)) continue;
            Save(d, dir);
            n++;
        }
        return n;
    }
}

// Built-in designs, written as genome text. They use only what every seed has: the single elements
// (species 0, 2, 4, 6 and their excited states 1, 3, 5, 7), the world's gas and "any" matter.
// Their Name is a stable key (the Russian name, as in design files and the save's designed lineages:
// files exported earlier keep matching); DisplayName shows it in the current language. Description
// and genome comments are written in the language of the moment the design is made.
public static class CreatureExamples
{
    sealed record Spec(string Key, string En, string DescEn, string DescRu, Dictionary<string, int> Body, float Energy, float Hue, float Sat, float Val,
                       (string Op, string En, string Ru)[] Lines);

    // Genome text in one language: the instruction, its comment in the column after it.
    static string Text(Spec s, bool en)
    {
        var sb = new StringBuilder();
        foreach (var (op, cEn, cRu) in s.Lines)
        {
            string c = en ? cEn : cRu;
            if (op.Length == 0) sb.Append(c);
            else if (c == null) sb.Append(op);
            else sb.Append(op.PadRight(33)).Append("; ").Append(c);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    static CreatureDesign Make(Spec s) => new()
    {
        Name = s.Key,
        Description = Loc.T(s.DescEn, s.DescRu),
        Body = new(s.Body),
        Energy = s.Energy,
        Hue = s.Hue, Sat = s.Sat, Val = s.Val,
        Genome = Text(s, Loc.En),
    };

    static readonly Spec LeafSpec = new("Листок", "Leaf",
        "A minimal photosynthesizer: catches a photon with molecule 0 (a lone atom A exists in every world), relaxes the excitation 1 → 0 and takes the energy. Now and then it drinks the matter around, rarely rebuilds its proteins, divides when fed.",
        "Минимальный фотосинтетик: ловит фотон молекулой 0 (одиночный атом A есть в каждом мире), снимает возбуждение 1 → 0 и берёт энергию. Иногда пьёт вещество вокруг, изредка заново собирает белки, сытый делится.",
        new() { ["0"] = 10, ["any"] = 4 }, 40, 0.33f, 0.8f, 0.9f, new (string, string, string)[]
        {
            ("", "; Leaf — a minimal photosynthesizer", "; Листок — минимальный фотосинтетик"),
            ("label 1", null, null),
            ("enzyme photo 0 0 t=15.0", "light protein for molecule 0", "белок света для молекулы 0"),
            ("enzyme split 1 0 t=15.0", "protein that relaxes the excitation 1 → 0", "белок, снимающий возбуждение 1 → 0"),
            ("label 0", null, null),
            ("push 0", null, null),
            ("photo", "photon: 0 → 1", "фотон: 0 → 1"),
            ("push 1", null, null),
            ("split", "1 → 0, energy to the body", "1 → 0, энергия телу"),
            ("rand", null, null),
            ("lit 4", null, null),
            ("lt", null, null),
            ("jnz 1", "~1.5% of cycles: rebuild the proteins", "~1,5% циклов — заново собрать белки"),
            ("rand", null, null),
            ("lit 12", null, null),
            ("lt", null, null),
            ("jz 0", "most often: back to the light", "чаще всего — снова к свету"),
            ("drink", "a sip of the matter around", "глоток вещества вокруг"),
            ("energy", null, null),
            ("lit 60", null, null),
            ("lt", null, null),
            ("jnz 0", "hungry: to the light", "голоден — к свету"),
            ("push 0", "the child's share: half", "доля ребёнку: половина"),
            ("push 1", "where: the next cell", "куда: соседняя клетка"),
            ("divide", null, null),
            ("jmp 0", null, null),
        });

    static readonly Spec MoleSpec = new("Крот", "Mole",
        "A miner: gnaws the block beneath it and the walls nearby, digests what it mines (splitting without a protein is slow, but it goes). Divides when fed.",
        "Шахтёр: грызёт блок под собой и стены рядом, переваривает добытое (распад без белка идёт медленно, но идёт). Сытый делится.",
        new() { ["any"] = 14 }, 50, 0.08f, 0.7f, 0.75f, new (string, string, string)[]
        {
            ("", "; Mole — gnaws rock and digests what it mines", "; Крот — грызёт породу и переваривает добытое"),
            ("label 0", null, null),
            ("mine", "gnaw the block underfoot", "грызть блок под ногами"),
            ("digest", "split a random molecule of the body", "расщепить случайную молекулу тела"),
            ("mine", null, null),
            ("digest", null, null),
            ("rand", null, null),
            ("gnaw", "a wall in a random direction", "стену в случайную сторону"),
            ("digest", null, null),
            ("energy", null, null),
            ("lit 80", null, null),
            ("lt", null, null),
            ("jnz 0", "hungry: keep gnawing", "голоден — грызть дальше"),
            ("push 0", null, null),
            ("rand", null, null),
            ("divide", "in half, in a random direction", "пополам, в случайную сторону"),
            ("jmp 0", null, null),
        });

    static readonly Spec SwimmerSpec = new("Пловец", "Swimmer",
        "Holds a gas bubble and now and then strokes toward the surface, where there is more light; sometimes pushes with its motor. Takes energy from light, like the leaf. Every stroke is costly: stroke too often and it starves.",
        "Держит пузырь газа и изредка гребёт к поверхности, где больше света; иногда толкается мотором. Энергию берёт светом, как листок. Каждый гребок дорог: часто грести — умереть с голоду.",
        new() { ["0"] = 8, ["gas"] = 3, ["any"] = 3 }, 40, 0.58f, 0.85f, 0.95f, new (string, string, string)[]
        {
            ("", "; Swimmer — a gas bubble, a motor and light", "; Пловец — пузырь газа, мотор и свет"),
            ("label 1", null, null),
            ("enzyme motor 0 0 t=15.0", "motor: pushes and strokes", "мотор: толчки и гребки"),
            ("enzyme photo 0 0 t=15.0", null, null),
            ("enzyme split 1 0 t=15.0", null, null),
            ("label 0", null, null),
            ("push 0", null, null),
            ("photo", "photon: 0 → 1", "фотон: 0 → 1"),
            ("push 1", null, null),
            ("split", "1 → 0, energy to the body", "1 → 0, энергия телу"),
            ("rand", null, null),
            ("lit 8", null, null),
            ("lt", null, null),
            ("jz 0", "most often: back to the light (a stroke is costly)", "чаще всего — снова к свету (гребок дорог)"),
            ("swim up", "a stroke up (only in water)", "гребок вверх (только в воде)"),
            ("rand", null, null),
            ("lit 64", null, null),
            ("lt", null, null),
            ("jz 2", null, null),
            ("rand", null, null),
            ("thrust", "sometimes a motor push in a random direction", "иногда толчок мотором в случайную сторону"),
            ("label 2", null, null),
            ("rand", null, null),
            ("lit 64", null, null),
            ("lt", null, null),
            ("jnz 1", "now and then: renew the proteins", "изредка — обновить белки"),
            ("jmp 0", null, null),
        });

    static readonly Spec[] Specs = { LeafSpec, MoleSpec, SwimmerSpec };

    public static CreatureDesign Leaf => Make(LeafSpec);
    public static CreatureDesign Mole => Make(MoleSpec);
    public static CreatureDesign Swimmer => Make(SwimmerSpec);

    public static IReadOnlyList<CreatureDesign> All => new[] { Leaf, Mole, Swimmer };

    static Spec Find(string name)
    {
        name = name?.Trim();
        foreach (var s in Specs)
            if (string.Equals(s.Key, name, StringComparison.OrdinalIgnoreCase) || string.Equals(s.En, name, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    // A design name as shown: a built-in example's in the current language, any other as it is.
    public static string DisplayName(string name)
    {
        var s = Find(name);
        return s == null ? name : Loc.T(s.En, s.Key);
    }

    // Both languages of a design name (for text kept with Loc.Both).
    public static string NameEn(string name) => Find(name)?.En ?? name;
    public static string NameRu(string name) => Find(name)?.Key ?? name;

    // The stable name for one typed by the player: an example's English name maps back to its key.
    public static string Key(string name) => Find(name)?.Key ?? name;

    // The description as shown: an example's untouched description (in either language) in the
    // current language, anything else as written.
    public static string DisplayDescription(CreatureDesign d)
    {
        var s = Find(d?.Name);
        return s != null && (d.Description == s.DescEn || d.Description == s.DescRu) ? Loc.T(s.DescEn, s.DescRu) : d?.Description;
    }

    // The genome text as shown: an example's untouched genome (in either language) with comments in
    // the current language, anything else as written.
    public static string DisplayGenome(CreatureDesign d)
    {
        var s = Find(d?.Name);
        return s != null && (d.Genome == Text(s, true) || d.Genome == Text(s, false)) ? Text(s, Loc.En) : d?.Genome;
    }
}
