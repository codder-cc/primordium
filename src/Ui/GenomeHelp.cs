using System.Collections.Generic;
using System.Text;
using Godot;

namespace Primordium;

// The command reference of the creature editor, generated from the VM's op table (Genome.Names and the
// second meanings GenomeAsm knows) with a short Russian description of each; and the colours of the
// genome text.
public static class GenomeHelp
{
    // Stack effect and meaning; "a b → c": pops b then a, pushes c.
    static readonly Dictionary<string, string> Text = new()
    {
        ["nop"] = "ничего не делает",
        ["push"] = "→ N: положить число 0–3 (push 2)",
        ["lit"] = "→ N: положить следующий байт, 0–255 (lit 60)",
        ["dup"] = "a → a a", ["drop"] = "a →", ["swap"] = "a b → b a", ["over"] = "a b → a b a", ["rot"] = "a b c → b c a",
        ["add"] = "a b → a+b", ["sub"] = "a b → a−b", ["mul"] = "a b → a·b", ["div"] = "a b → a/b (на 0 — 0)", ["mod"] = "a b → остаток",
        ["neg"] = "a → −a", ["inc"] = "a → a+1", ["dec"] = "a → a−1", ["lt"] = "a b → 1, если a<b", ["eq"] = "a b → 1, если a=b",
        ["rand"] = "→ случайное 0–255",
        ["label"] = "метка N (0–3) — цель переходов",
        ["jmp"] = "перейти к следующей метке N",
        ["jz"] = "a →: перейти к метке N, если a=0",
        ["jnz"] = "a →: перейти к метке N, если a≠0",
        ["call"] = "вызвать участок с меткой N (возврат — ret)",
        ["ret"] = "вернуться из call",
        ["skipz"] = "a →: пропустить следующий байт, если a=0",
        ["yield"] = "закончить такты этого тика",
        ["load"] = "i → память[i]", ["store"] = "v i →: память[i] = v",
        ["energy"] = "→ энергия тела", ["age"] = "→ возраст / 64", ["have"] = "m → сколько молекул m в теле", ["mass"] = "→ молекул в теле",
        ["temp"] = "→ температура клетки", ["btemp"] = "→ температура тела",
        ["light"] = "→ свет на тело ×100", ["photons"] = "→ фотоны клетки ×100 (под крышей 0)",
        ["sense"] = "m → концентрация m в клетке", ["sensed"] = "m d → концентрация m в соседней клетке d",
        ["smell"] = "m → запах m вокруг",
        ["look"] = "дальность d →: смотреть в сторону d (стоит энергии); кладёт деталь, расстояние, что видно",
        ["ground"] = "d → материал под ногами (d=4) или перепад высоты к соседу d",
        ["pick"] = "n → выбрать n-го соседа в клетке (n<0 — в соседней клетке); 1, если нашёлся",
        ["count"] = "→ сколько вокруг кандидатов для pick",
        ["feel"] = "→ энергия выбранного (−1 — никого)",
        ["hurt"] = "→ тиков с последней атаки здесь (−1 — тихо); напавший становится целью",
        ["kin"] = "→ родство с выбранным", ["ngene"] = "i → байт генома выбранного", ["gene"] = "i → свой байт генома", ["glen"] = "→ длина генома",
        ["listen"] = "→ сигнал выбранного", ["emit"] = "s →: выставить свой сигнал",
        ["enzyme"] = "ген белка: enzyme вид A B t=… q=… (вид: bind, split, photo, motor)",
        ["intake"] = "m →: втянуть молекулу m из клетки", ["drink"] = "глоток всего вокруг (или мягкой органики под ногами)",
        ["expel"] = "m d →: выбросить молекулу m в сторону d (отдача)", ["thrust"] = "d →: толчок мотором в сторону d (нужен белок-мотор)",
        ["bind"] = "a b →: соединить молекулы a и b (выгодно, если продукт беднее)", ["digest"] = "расщепить случайную молекулу тела",
        ["split"] = "m →: расщепить молекулу m (энергия, если распад экзотермичен)", ["photo"] = "m →: поймать фотон молекулой m (возбуждение)",
        ["divide"] = "доля d →: деление (доля ребёнку, сторона 0–4)", ["mate"] = "спаривание с выбранным (обмен генами)",
        ["mine"] = "грызть блок под ногами", ["gnaw"] = "d →: грызть стену или пол соседа d",
        ["attack"] = "сила →: напасть на выбранного", ["take"] = "m →: взять молекулу m у выбранного", ["give"] = "m →: дать молекулу m выбранному",
        ["link"] = "связь с выбранным (нужно согласие обоих); повторно — отпустить", ["share"] = "e →: передать энергию выбранному",
        ["inject"] = "начало длина →: вписать кусок своего генома выбранному (длина<0 — забрать его)",
        ["cut"] = "начало длина →: вырезать кусок своего генома",
        ["dig"] = "d →: перенести блок целиком", ["pile"] = "d →: сложить блок из одинаковых твёрдых молекул", ["grow"] = "d →: выделить агрегат из четырёх своих молекул",
        ["swim"] = "гребок вверх (swim up) или вниз (swim down) — только в воде",
    };

    static readonly (string title, string[] ops)[] Groups =
    {
        ("Стек и числа", new[] { "push", "lit", "dup", "drop", "swap", "over", "rot", "add", "sub", "mul", "div", "mod", "neg", "inc", "dec", "lt", "eq", "rand", "nop" }),
        ("Управление и память", new[] { "label", "jmp", "jz", "jnz", "call", "ret", "skipz", "yield", "load", "store" }),
        ("Состояние тела", new[] { "energy", "age", "have", "mass", "temp", "btemp", "gene", "glen" }),
        ("Мир", new[] { "sense", "sensed", "smell", "light", "photons", "look", "ground" }),
        ("Внимание и общение", new[] { "pick", "count", "feel", "hurt", "kin", "ngene", "listen", "emit" }),
        ("Белки и химия", new[] { "enzyme", "bind", "split", "photo", "digest" }),
        ("Мембрана и движение", new[] { "intake", "drink", "expel", "thrust", "swim" }),
        ("Жизнь и другие", new[] { "divide", "mate", "attack", "take", "give", "share", "link", "inject", "cut" }),
        ("Порода", new[] { "mine", "gnaw", "dig", "pile", "grow" }),
    };

    // Every mnemonic the assembler takes: the 64 instructions and the second meanings (immediate 2).
    public static IEnumerable<(string name, int op, bool variant)> Mnemonics()
    {
        for (int op = 0; op < 64; op++)
        {
            yield return (Genome.Names[op], op, false);
            if (Genome.HasImm(op)) continue;
            string v = GenomeAsm.Mnemonic((byte)(op | 2 << 6));
            if (!v.Contains('.')) yield return (v, op, true);
        }
    }

    public static Color OpColor(int op) => op switch
    {
        <= Genome.Rand => new Color(0.62f, 0.68f, 0.8f),
        <= Genome.Store => new Color(0.78f, 0.6f, 1f),
        Genome.EnzymeOp => new Color(0.3f, 1f, 0.55f),
        <= Genome.Pick => new Color(0.4f, 0.8f, 1f),
        <= Genome.Emit => new Color(1f, 0.85f, 0.4f),
        <= Genome.Divide => new Color(0.45f, 0.7f, 1f),
        Genome.Mine or Genome.Dig or Genome.Pile or Genome.Ground => new Color(1f, 0.62f, 0.3f),
        _ => new Color(1f, 0.45f, 0.4f),
    };

    public static CodeHighlighter Highlighter()
    {
        var h = new CodeHighlighter
        {
            NumberColor = new Color(0.95f, 0.75f, 0.55f),
            SymbolColor = new Color(0.6f, 0.64f, 0.71f),
            FunctionColor = UiKit.Fg,
            MemberVariableColor = UiKit.Fg,
        };
        foreach (var (name, op, _) in Mnemonics()) h.AddKeywordColor(name, OpColor(op));
        foreach (var k in GenomeAsm.EnzymeKinds) h.AddMemberKeywordColor(k, new Color(0.6f, 1f, 0.75f));
        foreach (var k in new[] { "up", "down", "raw", "t", "q", "alt" }) h.AddMemberKeywordColor(k, new Color(0.6f, 1f, 0.75f));
        h.AddKeywordColor("byte", new Color(0.8f, 0.8f, 0.8f));
        h.AddKeywordColor("db", new Color(0.8f, 0.8f, 0.8f));
        var comment = new Color(0.45f, 0.5f, 0.55f);
        h.AddColorRegion(";", "", comment, true);
        h.AddColorRegion("//", "", comment, true);
        h.AddColorRegion("#", "", comment, true);
        return h;
    }

    // BBCode for a RichTextLabel.
    public static string Reference()
    {
        var known = new HashSet<string>();
        foreach (var (n, _, _) in Mnemonics()) known.Add(n);
        var sb = new StringBuilder();
        sb.Append("[color=#9aa3b5]По команде в строке. «a b → c»: снимает со стека a и b, кладёт c. ")
          .Append("Вариант байта — суффикс .1/.2/.3 (dup.1). Комментарии: ; // #. ")
          .Append("Молекулы — номера 0–31 этого мира (0, 2, 4, 6 — одиночные элементы, нечётные — возбуждённые).[/color]\n");
        foreach (var (title, ops) in Groups)
        {
            sb.Append("\n[b]").Append(title).Append("[/b]\n");
            foreach (var name in ops)
            {
                if (!known.Contains(name)) continue;
                int op = System.Array.IndexOf(Genome.Names, name);
                if (op < 0) foreach (var (n, o, _) in Mnemonics()) if (n == name) op = o;
                sb.Append("[color=#").Append(OpColor(op).ToHtml(false)).Append("][code]").Append(name).Append("[/code][/color]  ")
                  .Append(Text.TryGetValue(name, out var d) ? d : "").Append('\n');
            }
        }
        sb.Append("\n[b]Белок[/b]\n[code]enzyme photo 0 0 t=15.0 q=0.9[/code] — вид (bind соединение, split расщепление, photo захват света, motor мотор), ")
          .Append("молекулы A и B, лучшая температура (−15…35,4 °C) и желаемое качество 0,35–1. Белок стоит энергии и одну молекулу тела.\n")
          .Append("\n[b]Байты[/b]\n[code]byte 12 200[/code] — сырые байты.\n");
        return sb.ToString();
    }
}
