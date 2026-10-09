using System.Collections.Generic;
using System.Text;
using Godot;

namespace Primordium;

// The command reference of the creature editor, generated from the VM's op table (Genome.Names and the
// second meanings GenomeAsm knows) with a short description of each (English and Russian); and the colours of the
// genome text.
public static class GenomeHelp
{
    // Stack effect and meaning; "a b → c": pops b then a, pushes c.
    static readonly Dictionary<string, string> TextEn = new()
    {
        ["nop"] = "does nothing",
        ["push"] = "→ N: push a number 0–3 (push 2)",
        ["lit"] = "→ N: push the next byte, 0–255 (lit 60)",
        ["dup"] = "a → a a", ["drop"] = "a →", ["swap"] = "a b → b a", ["over"] = "a b → a b a", ["rot"] = "a b c → b c a",
        ["add"] = "a b → a+b", ["sub"] = "a b → a−b", ["mul"] = "a b → a·b", ["div"] = "a b → a/b (by 0: 0)", ["mod"] = "a b → remainder",
        ["neg"] = "a → −a", ["inc"] = "a → a+1", ["dec"] = "a → a−1", ["lt"] = "a b → 1 if a<b", ["eq"] = "a b → 1 if a=b",
        ["rand"] = "→ random 0–255",
        ["label"] = "label N (0–3): a jump target",
        ["jmp"] = "jump to the next label N",
        ["jz"] = "a →: jump to label N if a=0",
        ["jnz"] = "a →: jump to label N if a≠0",
        ["call"] = "call the block at label N (return with ret)",
        ["ret"] = "return from call",
        ["skipz"] = "a →: skip the next byte if a=0",
        ["yield"] = "end this tick's cycles",
        ["load"] = "i → memory[i]", ["store"] = "v i →: memory[i] = v",
        ["energy"] = "→ body energy", ["age"] = "→ age / 64", ["have"] = "m → how many molecules m in the body", ["mass"] = "→ molecules in the body",
        ["temp"] = "→ cell temperature (Organs 1: thermoreceptor)", ["btemp"] = "→ body temperature (Organs 1: thermoreceptor)",
        ["light"] = "→ light on the body ×100 (Organs 1: photoreceptor)", ["photons"] = "→ cell photons ×100, under the canopy (Canopy 1) the body's own store (0 under a roof) (Organs 1: photoreceptor)",
        ["uv"] = "→ UV at the body ×100: solar activity and flares (light.1; without flares, same as light) (Organs 1: photoreceptor)",
        ["sense"] = "m → concentration of m in the cell (Organs 1: receptor)", ["sensed"] = "m d → concentration of m in neighbour cell d (Organs 1: receptor)",
        ["smell"] = "m → smell of m around (Organs 1: receptor)",
        ["look"] = "range d →: look in direction d (costs energy); pushes detail, distance, what is seen (Organs 1: photoreceptors ≥ LookMin)",
        ["ground"] = "d → material underfoot (d=4) or height step to neighbour d (Organs 1: mechanoreceptor)",
        ["pick"] = "n → select the n-th neighbour in the cell (n<0: in a neighbour cell); 1 if found",
        ["count"] = "→ how many candidates around for pick (Organs 1: mechanoreceptor)",
        ["feel"] = "→ energy of the selected (−1: nobody) (Organs 1: mechanoreceptor)",
        ["hurt"] = "→ ticks since the last attack here (−1: quiet); the attacker becomes the target (Organs 1: mechanoreceptor)",
        ["kin"] = "→ kinship with the selected (Organs 1: receptor)", ["ngene"] = "i → genome byte of the selected (Organs 1: receptor)", ["gene"] = "i → own genome byte", ["glen"] = "→ genome length",
        ["listen"] = "→ signal of the selected (Organs 1: mechanoreceptor)", ["emit"] = "s →: set own signal",
        ["enzyme"] = "protein gene: enzyme kind A B t=… q=… (kind: bind, split, photo, motor); organs of sense (Organs 1): enzyme receptor A, photoreceptor A, mechanoreceptor, thermoreceptor",
        ["intake"] = "m →: take in molecule m from the cell", ["drink"] = "a sip of everything around (or of soft organics underfoot)",
        ["expel"] = "m d →: expel molecule m toward d (recoil)", ["thrust"] = "d →: motor push toward d (needs a motor protein)",
        ["bind"] = "a b →: bind molecules a and b (pays if the product is poorer)", ["digest"] = "split a random molecule of the body",
        ["split"] = "m →: split molecule m (energy if the split is exothermic)", ["photo"] = "m →: catch a photon with molecule m (excitation)",
        ["divide"] = "share d →: divide (the child's share, side 0–4)", ["mate"] = "mate with the selected (gene exchange)",
        ["mine"] = "gnaw the block underfoot", ["gnaw"] = "d →: gnaw the wall or floor of neighbour d",
        ["attack"] = "force →: attack the selected", ["take"] = "m →: take molecule m from the selected", ["give"] = "m →: give molecule m to the selected",
        ["link"] = "link with the selected (both must agree); again to let go", ["share"] = "e →: pass energy to the selected",
        ["inject"] = "start length →: write a piece of own genome into the selected (length<0: take theirs)",
        ["cut"] = "start length →: cut a piece of own genome",
        ["dig"] = "d →: move a whole block", ["pile"] = "d →: lay a block of identical solid molecules", ["grow"] = "d →: secrete an aggregate of four own molecules",
        ["swim"] = "stroke up (swim up) or down (swim down), only in water",
    };

    static readonly Dictionary<string, string> TextRu = new()
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
        ["temp"] = "→ температура клетки (Organs 1: терморецептор)", ["btemp"] = "→ температура тела (Organs 1: терморецептор)",
        ["light"] = "→ свет на тело ×100 (Organs 1: фоторецептор)", ["photons"] = "→ фотоны клетки ×100, при пологе (Canopy 1) — свой запас тела (под крышей 0) (Organs 1: фоторецептор)",
        ["uv"] = "→ УФ у тела ×100: активность солнца и вспышки (light.1; без вспышек — как light) (Organs 1: фоторецептор)",
        ["sense"] = "m → концентрация m в клетке (Organs 1: рецептор)", ["sensed"] = "m d → концентрация m в соседней клетке d (Organs 1: рецептор)",
        ["smell"] = "m → запах m вокруг (Organs 1: рецептор)",
        ["look"] = "дальность d →: смотреть в сторону d (стоит энергии); кладёт деталь, расстояние, что видно (Organs 1: фоторецепторы ≥ LookMin)",
        ["ground"] = "d → материал под ногами (d=4) или перепад высоты к соседу d (Organs 1: механорецептор)",
        ["pick"] = "n → выбрать n-го соседа в клетке (n<0 — в соседней клетке); 1, если нашёлся",
        ["count"] = "→ сколько вокруг кандидатов для pick (Organs 1: механорецептор)",
        ["feel"] = "→ энергия выбранного (−1 — никого) (Organs 1: механорецептор)",
        ["hurt"] = "→ тиков с последней атаки здесь (−1 — тихо); напавший становится целью (Organs 1: механорецептор)",
        ["kin"] = "→ родство с выбранным (Organs 1: рецептор)", ["ngene"] = "i → байт генома выбранного (Organs 1: рецептор)", ["gene"] = "i → свой байт генома", ["glen"] = "→ длина генома",
        ["listen"] = "→ сигнал выбранного (Organs 1: механорецептор)", ["emit"] = "s →: выставить свой сигнал",
        ["enzyme"] = "ген белка: enzyme вид A B t=… q=… (вид: bind, split, photo, motor); органы чувств (Organs 1): enzyme рецептор A, фоторецептор A, механорецептор, терморецептор",
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

    static Dictionary<string, string> Text => Loc.En ? TextEn : TextRu;

    static readonly (string en, string ru, string[] ops)[] Groups =
    {
        ("Stack and numbers", "Стек и числа", new[] { "push", "lit", "dup", "drop", "swap", "over", "rot", "add", "sub", "mul", "div", "mod", "neg", "inc", "dec", "lt", "eq", "rand", "nop" }),
        ("Control and memory", "Управление и память", new[] { "label", "jmp", "jz", "jnz", "call", "ret", "skipz", "yield", "load", "store" }),
        ("Body state", "Состояние тела", new[] { "energy", "age", "have", "mass", "temp", "btemp", "gene", "glen" }),
        ("World", "Мир", new[] { "sense", "sensed", "smell", "light", "photons", "uv", "look", "ground" }),
        ("Attention and communication", "Внимание и общение", new[] { "pick", "count", "feel", "hurt", "kin", "ngene", "listen", "emit" }),
        ("Proteins and chemistry", "Белки и химия", new[] { "enzyme", "bind", "split", "photo", "digest" }),
        ("Membrane and movement", "Мембрана и движение", new[] { "intake", "drink", "expel", "thrust", "swim" }),
        ("Life and others", "Жизнь и другие", new[] { "divide", "mate", "attack", "take", "give", "share", "link", "inject", "cut" }),
        ("Rock", "Порода", new[] { "mine", "gnaw", "dig", "pile", "grow" }),
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
            if (op == Genome.LightOp) yield return ("uv", op, true);
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
        foreach (var k in new[] { "up", "down", "raw", "t", "q", "alt", "a", "b", "рецептор", "фоторецептор", "механорецептор", "терморецептор", "соединение", "расщепление", "свет", "мотор" }) h.AddMemberKeywordColor(k, new Color(0.6f, 1f, 0.75f));
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
        sb.Append("[color=#9aa3b5]").Append(Loc.T(
              "One instruction per line. 'a b → c': pops a and b off the stack, pushes c. " +
              "Byte variant: suffix .1/.2/.3 (dup.1). Comments: ; // #. " +
              "Molecules are this world's numbers 0–31 (0, 2, 4, 6: single elements; odd: excited states).",
              "По команде в строке. «a b → c»: снимает со стека a и b, кладёт c. " +
              "Вариант байта — суффикс .1/.2/.3 (dup.1). Комментарии: ; // #. " +
              "Молекулы — номера 0–31 этого мира (0, 2, 4, 6 — одиночные элементы, нечётные — возбуждённые).")).Append("[/color]\n");
        foreach (var (en, ru, ops) in Groups)
        {
            sb.Append("\n[b]").Append(Loc.T(en, ru)).Append("[/b]\n");
            foreach (var name in ops)
            {
                if (!known.Contains(name)) continue;
                int op = System.Array.IndexOf(Genome.Names, name);
                if (op < 0) foreach (var (n, o, _) in Mnemonics()) if (n == name) op = o;
                sb.Append("[color=#").Append(OpColor(op).ToHtml(false)).Append("][code]").Append(name).Append("[/code][/color]  ")
                  .Append(Text.TryGetValue(name, out var d) ? d : "").Append('\n');
            }
        }
        sb.Append(Loc.T(
                "\n[b]Protein[/b]\n[code]enzyme photo 0 0 t=15.0 q=0.9[/code]: kind (bind binding, split splitting, photo light capture, motor motor), " +
                "molecules A and B, best temperature (−15…35.4 °C) and wanted quality 0.35–1. A protein costs energy and one molecule of the body.\n" +
                "\n[b]Organs of sense[/b] (the law Organs 1)\nWith the law on, a sense is a protein the genome makes, not an ability: without the organ a reading gives 0 " +
                "(nothing, or −1) and still costs a little; with it every reading costs more the stronger the organ, and every unit of an organ costs upkeep each tick. " +
                "An organ wears like any protein and has to be made again: a line that stops making it (in a cave, a photoreceptor) loses it. " +
                "Strength = amount × quality × how close the body is to its best temperature; a reading is scaled by it (up to 1) and its noise falls with it.\n" +
                "[code]enzyme receptor 8[/code]: binds molecule 8 outside — sense, sensed and smell read through it; it also reports molecules like 8 (sharing its atoms), so a receptor for a formula of its own is specific.\n" +
                "[code]enzyme photoreceptor 0[/code]: pigment 0 — light, photons, uv and look; the pigment is a molecule of 0 folded into the protein (the body must hold one when it makes it) " +
                "and must be excitable by light; it sees by its excitation (stronger for a bigger gap). look needs several of them (LookMin) and reaches LookPerUnit cells per unit; it costs more with range.\n" +
                "[code]enzyme mechanoreceptor[/code]: touch — count, hurt, ground, feel, listen (caught with the chance of its strength).\n" +
                "[code]enzyme thermoreceptor t=20[/code]: temp and btemp, accurate near its best temperature.\n" +
                "A motor pushes with all its copies: a full push of a heavy body needs more of them (MotorLoad). Organ genes are motor genes by their B: with the law off they make a motor.\n",
                "\n[b]Белок[/b]\n[code]enzyme photo 0 0 t=15.0 q=0.9[/code] — вид (bind соединение, split расщепление, photo захват света, motor мотор), " +
                "молекулы A и B, лучшая температура (−15…35,4 °C) и желаемое качество 0,35–1. Белок стоит энергии и одну молекулу тела.\n" +
                "\n[b]Органы чувств[/b] (закон Organs 1)\nПри законе чувство — белок, который делает геном, а не готовая способность: без органа чтение даёт 0 " +
                "(ничего или −1) и всё равно чуть стоит; с органом каждое чтение стоит тем больше, чем сильнее орган, а каждая единица органа — содержание каждый тик. " +
                "Орган изнашивается, как любой белок, и его надо делать заново: линия, которая перестала его делать (в пещере — фоторецептор), его теряет. " +
                "Сила = количество × качество × близость тела к его лучшей температуре; чтение умножается на неё (до 1), шум с ней падает.\n" +
                "[code]enzyme рецептор 8[/code] — связывает молекулу 8 снаружи: sense, sensed и smell читают через него; он отзывается и на похожие на 8 молекулы (по общим атомам), так что рецептор на редкую формулу точен.\n" +
                "[code]enzyme фоторецептор 0[/code] — пигмент 0: light, photons, uv и look; пигмент — молекула 0, вложенная в белок (она должна быть в теле, когда белок делается), " +
                "и свет должен её возбуждать; видит по своему возбуждению (сильнее при большей щели). look нужно несколько таких (LookMin), дальность LookPerUnit клеток на единицу, цена растёт с дальностью.\n" +
                "[code]enzyme механорецептор[/code] — осязание: count, hurt, ground, feel, listen (ловит с вероятностью своей силы).\n" +
                "[code]enzyme терморецептор t=20[/code] — temp и btemp, точен около своей лучшей температуры.\n" +
                "Мотор толкает всеми копиями: полный толчок тяжёлого тела требует их больше (MotorLoad). Гены органов — это гены мотора по их B: без закона из них выходит мотор.\n"))
          .Append(Loc.T("\n[b]Bytes[/b]\n[code]byte 12 200[/code]: raw bytes.\n", "\n[b]Байты[/b]\n[code]byte 12 200[/code] — сырые байты.\n"));
        return sb.ToString();
    }
}
