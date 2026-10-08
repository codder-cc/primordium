using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Primordium;

public readonly struct AsmError
{
    public readonly int Line;        // 1-based
    public readonly string Message;
    public AsmError(int line, string message) { Line = line; Message = message; }
    public override string ToString() => Loc.T($"line {Line}: {Message}", $"строка {Line}: {Message}");
}

public sealed class GenomeAsmException : Exception
{
    public readonly IReadOnlyList<AsmError> Errors;
    public GenomeAsmException(IReadOnlyList<AsmError> errors) : base(string.Join("\n", errors)) => Errors = errors;
}

// Genome text: one instruction per line, exactly as the VM reads the bytes (Genome, World.Vm).
//
//   <op>            the instruction with immediate bits 0          dup, intake, divide …
//   <op>.N          the same byte with immediate bits N (0–3)       dup.1
//   push N / label N / jmp N / jz N / jnz N / call N               N = 0–3 (the immediate)
//   <variant>       the second meaning (immediate 2), .3 for 3      photo, thrust, mate, gnaw, grow,
//                                                                   count, drink, digest, hurt, btemp,
//                                                                   photons, swim (swim = up, swim.3 = down)
//   uv              light.1: the UV at the body (with solar flares on; otherwise the light, as `light`)
//   lit N           two bytes: lit and a literal 0–255
//   enzyme KIND A B t=T q=Q [alt=K]   four bytes: a protein gene. KIND bind|split|photo|motor; A, B the
//                   molecules (0–31); T its best temperature (−15…35.4 °C in steps of 0.8); Q the wanted
//                   quality 0.35–1 (the closest the gene can give); alt=K (0–63) picks one of the 64 byte
//                   triples with that kind, A, B and T explicitly. `enzyme raw b1 b2 b3` gives the bytes.
//   byte N …        raw bytes (also for an operand cut off at the end of the genome, which the VM
//                   reads from the start)
//   ; // #          comments
//
// The ground-motor variant of `expel` is called `thrust` here (the game's panels call it push).
// Disassemble → Assemble gives back the same bytes, always; Assemble → Disassemble gives the
// canonical text. Numbers may be decimal or 0x hex.
public static class GenomeAsm
{
    static readonly int[] VariantOps = { Genome.Split, Genome.Expel, Genome.Divide, Genome.Mine, Genome.Pile, Genome.Pick, Genome.Intake, Genome.Bind, Genome.Feel, Genome.Temp, Genome.LightOp, Genome.Nop };
    static readonly string[] VariantNames = { "photo", "thrust", "mate", "gnaw", "grow", "count", "drink", "digest", "hurt", "btemp", "photons", "swim" };
    static readonly Dictionary<string, int> baseOps = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, int> variantOps = new(StringComparer.OrdinalIgnoreCase);
    static readonly string[] variantOf = new string[64];
    public static readonly string[] EnzymeKinds = { "bind", "split", "photo", "motor" };
    static readonly Dictionary<string, int> kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bind"] = Enzyme.Bind, ["split"] = Enzyme.Split, ["photo"] = Enzyme.Photo, ["motor"] = Enzyme.Motor,
        ["соединение"] = Enzyme.Bind, ["расщепление"] = Enzyme.Split, ["свет"] = Enzyme.Photo, ["мотор"] = Enzyme.Motor,
    };
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static GenomeAsm()
    {
        for (int op = 0; op < 64; op++) baseOps[Genome.Names[op]] = op;
        for (int k = 0; k < VariantOps.Length; k++) { variantOps[VariantNames[k]] = VariantOps[k]; variantOf[VariantOps[k]] = VariantNames[k]; }
    }

    // ---- bytes → text ----

    // The mnemonic of one byte (without operands).
    public static string Mnemonic(byte b)
    {
        int op = b & 63, imm = b >> 6;
        if (Genome.HasImm(op)) return $"{Genome.Names[op]} {imm}";
        if (op == Genome.LightOp && imm == 1) return "uv";   // light.1: the UV at the body with solar flares on (World.Sky)
        if (imm >= 2 && variantOf[op] != null) return imm == 2 ? variantOf[op] : variantOf[op] + ".3";
        return imm == 0 ? Genome.Names[op] : $"{Genome.Names[op]}.{imm}";
    }

    // The genome as text, one instruction per line. offsets: a comment with each instruction's position.
    public static string Disassemble(byte[] g, bool offsets = false)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < g.Length;)
        {
            string line = Instruction(g, i, out int len);
            if (offsets) line = line.PadRight(36) + "; @" + i;
            sb.Append(line).Append('\n');
            i += len;
        }
        return sb.ToString();
    }

    static string Instruction(byte[] g, int i, out int len)
    {
        byte b = g[i];
        int op = b & 63;
        len = 1;
        if (op == Genome.Lit)
        {
            if (i + 1 >= g.Length) return $"byte {b}   ; {Mnemonic(b)}: its operand is read from the start of the genome";
            len = 2;
            return $"{Mnemonic(b)} {g[i + 1]}";
        }
        if (op == Genome.EnzymeOp)
        {
            if (i + 3 >= g.Length) return $"byte {b}   ; {Mnemonic(b)}: its gene continues from the start of the genome";
            len = 4;
            return $"{Mnemonic(b)} {EnzymeText(g[i + 1], g[i + 2], g[i + 3])}";
        }
        return Mnemonic(b);
    }

    // The operands of a protein gene: kind, molecules, best temperature, quality (and the byte choice
    // if the quality alone does not single it out).
    public static string EnzymeText(byte b1, byte b2, byte b3)
    {
        var e = Genome.Decode(b1, b2, b3);
        string q = e.Eff.ToString("0.000", Inv);
        string text = $"{EnzymeKinds[e.Kind]} {e.A} {e.B} t={e.Topt.ToString("0.0", Inv)} q={q}";
        int alt = (b2 >> 5) * 8 + (b3 >> 5);
        if (ClosestAlt(b1, e.A, e.B, double.Parse(q, Inv)) != alt) text += $" alt={alt}";
        return text;
    }

    static int ClosestAlt(byte b1, int a, int b, double q)
    {
        int best = 0;
        double bestD = double.MaxValue;
        for (int alt = 0; alt < 64; alt++)
        {
            var (b2, b3) = AltBytes(a, b, alt);
            double d = Math.Abs(Genome.Decode(b1, b2, b3).Eff - q);
            if (d < bestD) { bestD = d; best = alt; }
        }
        return best;
    }

    static (byte, byte) AltBytes(int a, int b, int alt) => ((byte)(a | (alt >> 3) << 5), (byte)(b | (alt & 7) << 5));

    // The bytes of a protein gene for the wanted kind, molecules, temperature and quality.
    public static (byte b1, byte b2, byte b3) EnzymeBytes(int kind, int a, int b, float topt, double quality = 1, int alt = -1)
    {
        int t = Math.Clamp((int)MathF.Round((topt + 15f) / 0.8f), 0, 63);
        byte b1 = (byte)(kind & 3 | t << 2);
        if (alt < 0) alt = ClosestAlt(b1, a & 31, b & 31, quality);
        var (b2, b3) = AltBytes(a & 31, b & 31, alt);
        return (b1, b2, b3);
    }

    // ---- text → bytes ----

    public static byte[] Assemble(string text)
    {
        if (!TryAssemble(text, out var bytes, out var errors)) throw new GenomeAsmException(errors);
        return bytes;
    }

    public static bool TryAssemble(string text, out byte[] bytes, out List<AsmError> errors)
    {
        var output = new List<byte>();
        errors = new List<AsmError>();
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string line = StripComment(lines[n]).Trim();
            if (line.Length == 0) continue;
            var tokens = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            try { AssembleLine(tokens, output); }
            catch (FormatException e) { errors.Add(new AsmError(n + 1, e.Message)); }
        }
        if (errors.Count == 0 && output.Count < Genome.MinLen) errors.Add(new AsmError(lines.Length, Loc.T($"genome shorter than {Genome.MinLen} bytes ({output.Count})", $"геном короче {Genome.MinLen} байт ({output.Count})")));
        if (errors.Count == 0 && output.Count > Genome.MaxLen) errors.Add(new AsmError(lines.Length, Loc.T($"genome longer than {Genome.MaxLen} bytes ({output.Count})", $"геном длиннее {Genome.MaxLen} байт ({output.Count})")));
        bytes = errors.Count == 0 ? output.ToArray() : null;
        return errors.Count == 0;
    }

    static string StripComment(string line)
    {
        int cut = line.Length;
        int k = line.IndexOf(';'); if (k >= 0) cut = Math.Min(cut, k);
        k = line.IndexOf("//", StringComparison.Ordinal); if (k >= 0) cut = Math.Min(cut, k);
        k = line.IndexOf('#'); if (k >= 0) cut = Math.Min(cut, k);
        return line[..cut];
    }

    static int Number(string token, int min, int max, string what)
    {
        bool ok = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(token[2..], NumberStyles.HexNumber, Inv, out int v)
            : int.TryParse(token, NumberStyles.Integer, Inv, out v);
        if (!ok) throw new FormatException(Loc.T($"{what}: '{token}' is not a number", $"{what}: «{token}» — не число"));
        if (v < min || v > max) throw new FormatException(Loc.T($"{what}: {v} outside {min}…{max}", $"{what}: {v} вне {min}…{max}"));
        return v;
    }

    static double Real(string token, string what)
    {
        if (!double.TryParse(token.Replace(',', '.'), NumberStyles.Float, Inv, out double v)) throw new FormatException(Loc.T($"{what}: '{token}' is not a number", $"{what}: «{token}» — не число"));
        return v;
    }

    static void Args(string[] t, int count, string usage)
    {
        if (t.Length - 1 != count) throw new FormatException(Loc.T($"expected '{usage}'", $"ожидается «{usage}»"));
    }

    static void AssembleLine(string[] t, List<byte> output)
    {
        string word = t[0];
        if (word.Equals("byte", StringComparison.OrdinalIgnoreCase) || word.Equals("db", StringComparison.OrdinalIgnoreCase))
        {
            if (t.Length < 2) throw new FormatException(Loc.T("byte: needs at least one byte", "byte: нужен хотя бы один байт"));
            for (int k = 1; k < t.Length; k++) output.Add((byte)Number(t[k], 0, 255, "byte"));
            return;
        }
        int imm = -1;
        int dot = word.LastIndexOf('.');
        if (dot > 0)
        {
            imm = Number(word[(dot + 1)..], 0, 3, Loc.T("variant", "вариант"));
            word = word[..dot];
        }
        int op;
        if (word.Equals("uv", StringComparison.OrdinalIgnoreCase))
        {
            if (imm >= 0) throw new FormatException(Loc.T("uv: takes no variant (it is light.1)", "uv: без вариантов (это light.1)"));
            Args(t, 0, word);
            output.Add((byte)(Genome.LightOp | 1 << 6));
            return;
        }
        if (variantOps.TryGetValue(word, out op))
        {
            if (imm >= 0 && imm < 2) throw new FormatException(Loc.T($"{word}.{imm}: the second meaning has variants 2 or 3", $"{word}.{imm}: у второго значения варианты 2 или 3"));
            if (imm < 0) imm = 2;
            if (word.Equals("swim", StringComparison.OrdinalIgnoreCase) && t.Length == 2)
            {
                if (t[1].Equals("up", StringComparison.OrdinalIgnoreCase)) imm = 2;
                else if (t[1].Equals("down", StringComparison.OrdinalIgnoreCase)) imm = 3;
                else throw new FormatException(Loc.T("swim: up or down", "swim: up или down"));
            }
            else Args(t, 0, word);
            output.Add((byte)(op | imm << 6));
            return;
        }
        if (!baseOps.TryGetValue(word, out op))
            throw new FormatException(word.Equals("push", StringComparison.OrdinalIgnoreCase) ? Loc.T("push N (0–3); a motor push is thrust", "push N (0–3); толчок мотором — thrust") : Loc.T($"unknown instruction '{t[0]}'", $"неизвестная команда «{t[0]}»"));
        if (Genome.HasImm(op))
        {
            if (imm >= 0) throw new FormatException(Loc.T($"{word}: the number goes after a space ({word} N)", $"{word}: число пишется через пробел ({word} N)"));
            Args(t, 1, $"{word} N");
            output.Add((byte)(op | Number(t[1], 0, 3, word) << 6));
            return;
        }
        if (imm < 0) imm = 0;
        byte b = (byte)(op | imm << 6);
        if (op == Genome.Lit)
        {
            Args(t, 1, "lit N");
            output.Add(b);
            output.Add((byte)Number(t[1], 0, 255, "lit"));
            return;
        }
        if (op == Genome.EnzymeOp)
        {
            output.Add(b);
            var (b1, b2, b3) = ParseEnzyme(t);
            output.Add(b1); output.Add(b2); output.Add(b3);
            return;
        }
        Args(t, 0, Genome.Names[op]);
        output.Add(b);
    }

    static (byte, byte, byte) ParseEnzyme(string[] t)
    {
        if (t.Length >= 2 && t[1].Equals("raw", StringComparison.OrdinalIgnoreCase))
        {
            Args(t, 4, "enzyme raw b1 b2 b3");
            return ((byte)Number(t[2], 0, 255, "b1"), (byte)Number(t[3], 0, 255, "b2"), (byte)Number(t[4], 0, 255, "b3"));
        }
        if (t.Length < 4) throw new FormatException(Loc.T("expected 'enzyme bind|split|photo|motor A B t=T q=Q'", "ожидается «enzyme bind|split|photo|motor A B t=T q=Q»"));
        if (!kinds.TryGetValue(t[1], out int kind)) throw new FormatException(Loc.T($"unknown protein kind '{t[1]}' (bind, split, photo, motor)", $"неизвестный вид белка «{t[1]}» (bind, split, photo, motor)"));
        int a = Number(t[2], 0, Chemistry.S - 1, Loc.T("molecule A", "молекула A")), b = Number(t[3], 0, Chemistry.S - 1, Loc.T("molecule B", "молекула B"));
        double topt = 15, q = 1;
        int alt = -1, positional = 0;
        for (int k = 4; k < t.Length; k++)
        {
            string tok = t[k];
            int eq = tok.IndexOf('=');
            string key = eq > 0 ? tok[..eq].ToLowerInvariant() : (positional++ == 0 ? "t" : positional == 2 ? "q" : "?");
            string val = eq > 0 ? tok[(eq + 1)..] : tok;
            switch (key)
            {
                case "t": topt = Real(val, Loc.T("temperature", "температура")); break;
                case "q": q = Real(val, Loc.T("quality", "качество")); break;
                case "alt": alt = Number(val, 0, 63, "alt"); break;
                default: throw new FormatException(Loc.T($"unexpected '{tok}' (t=…, q=…, alt=…)", $"лишнее «{tok}» (t=…, q=…, alt=…)"));
            }
        }
        if (topt < -15.4 || topt > 35.8) throw new FormatException(Loc.T($"temperature {topt.ToString(Inv)} outside −15…35.4 °C", $"температура {topt.ToString(Inv)} вне −15…35.4 °C"));
        if (q < 0 || q > 1.5) throw new FormatException(Loc.T($"quality {q.ToString(Inv)} outside 0.35…1", $"качество {q.ToString(Inv)} вне 0.35…1"));
        return EnzymeBytes(kind, a, b, (float)topt, q, alt);
    }
}
