using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

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
    public string Genome { get; set; } = "";   // GenomeAsm text
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

    public byte[] Assemble() => GenomeAsm.Assemble(Genome);

    public const int AnyMolecule = -1;

    // The body request in species numbers (AnyMolecule for "any"); problems are added to `errors`.
    public Dictionary<int, int> ResolveBody(Chemistry chem, List<string> errors)
    {
        var body = new Dictionary<int, int>();
        foreach (var (key, count) in Body ?? new())
        {
            int s = ResolveMolecule(chem, key);
            if (s == int.MinValue) { errors.Add($"неизвестная молекула «{key}»"); continue; }
            if (count < 0) { errors.Add($"{key}: отрицательное количество"); continue; }
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
        for (s = 0; s < Chemistry.S; s++)
            if (chem.Name[s].Equals(key, StringComparison.OrdinalIgnoreCase)) return s;
        for (s = 0; s < Chemistry.S; s += 2)   // a formula means its ground state
            if (chem.Formula(s).Equals(key, StringComparison.OrdinalIgnoreCase)) return s;
        return int.MinValue;
    }

    // Everything wrong with the design for this chemistry (empty: it can be planted).
    public List<string> Check(Chemistry chem)
    {
        var errors = new List<string>();
        if (!GenomeAsm.TryAssemble(Genome, out _, out var asm)) errors.AddRange(asm.Select(e => e.ToString()));
        var body = ResolveBody(chem, errors);
        int total = body.Values.Sum();
        if (total < P.MinBody) errors.Add($"тело из {total} молекул: меньше {P.MinBody} не держится");
        if (!(Energy >= 0) || float.IsInfinity(Energy)) errors.Add("энергия должна быть ≥ 0");
        return errors;
    }

    // A design copied from a living body: its genome as text, its molecules, its looks.
    public static CreatureDesign FromAgent(World w, Agent a, string name = null)
    {
        var d = new CreatureDesign
        {
            Name = name ?? $"линия {a.Lineage} #{a.Id}",
            Description = $"Снято с существа #{a.Id} (линия {a.Lineage}, поколение {a.Gen}, мир {w.Seed}, тик {w.Tick}).",
            Genome = GenomeAsm.Disassemble(a.G),
            Energy = MathF.Round(Math.Max(0, a.Energy)),
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
public static class CreatureExamples
{
    public static CreatureDesign Leaf => new()
    {
        Name = "Листок",
        Description = "Минимальный фотосинтетик: ловит фотон молекулой 0 (одиночный атом A есть в каждом мире), снимает возбуждение 1 → 0 и берёт энергию. Иногда пьёт вещество вокруг, изредка заново собирает белки, сытый делится.",
        Body = new() { ["0"] = 10, ["any"] = 4 },
        Energy = 40,
        Hue = 0.33f, Sat = 0.8f, Val = 0.9f,
        Genome = @"; Листок — минимальный фотосинтетик
label 1
enzyme photo 0 0 t=15.0          ; белок света для молекулы 0
enzyme split 1 0 t=15.0          ; белок, снимающий возбуждение 1 → 0
label 0
push 0
photo                            ; фотон: 0 → 1
push 1
split                            ; 1 → 0, энергия телу
rand
lit 4
lt
jnz 1                            ; ~1,5% циклов — заново собрать белки
rand
lit 12
lt
jz 0                             ; чаще всего — снова к свету
drink                            ; глоток вещества вокруг
energy
lit 60
lt
jnz 0                            ; голоден — к свету
push 0                           ; доля ребёнку: половина
push 1                           ; куда: соседняя клетка
divide
jmp 0
",
    };

    public static CreatureDesign Mole => new()
    {
        Name = "Крот",
        Description = "Шахтёр: грызёт блок под собой и стены рядом, переваривает добытое (распад без белка идёт медленно, но идёт). Сытый делится.",
        Body = new() { ["any"] = 14 },
        Energy = 50,
        Hue = 0.08f, Sat = 0.7f, Val = 0.75f,
        Genome = @"; Крот — грызёт породу и переваривает добытое
label 0
mine                             ; грызть блок под ногами
digest                           ; расщепить случайную молекулу тела
mine
digest
rand
gnaw                             ; стену в случайную сторону
digest
energy
lit 80
lt
jnz 0                            ; голоден — грызть дальше
push 0
rand
divide                           ; пополам, в случайную сторону
jmp 0
",
    };

    public static CreatureDesign Swimmer => new()
    {
        Name = "Пловец",
        Description = "Держит пузырь газа и изредка гребёт к поверхности, где больше света; иногда толкается мотором. Энергию берёт светом, как листок. Каждый гребок дорог: часто грести — умереть с голоду.",
        Body = new() { ["0"] = 8, ["gas"] = 3, ["any"] = 3 },
        Energy = 40,
        Hue = 0.58f, Sat = 0.85f, Val = 0.95f,
        Genome = @"; Пловец — пузырь газа, мотор и свет
label 1
enzyme motor 0 0 t=15.0          ; мотор: толчки и гребки
enzyme photo 0 0 t=15.0
enzyme split 1 0 t=15.0
label 0
push 0
photo                            ; фотон: 0 → 1
push 1
split                            ; 1 → 0, энергия телу
rand
lit 8
lt
jz 0                             ; чаще всего — снова к свету (гребок дорог)
swim up                          ; гребок вверх (только в воде)
rand
lit 64
lt
jz 2
rand
thrust                           ; иногда толчок мотором в случайную сторону
label 2
rand
lit 64
lt
jnz 1                            ; изредка — обновить белки
jmp 0
",
    };

    public static IReadOnlyList<CreatureDesign> All => new[] { Leaf, Mole, Swimmer };
}
