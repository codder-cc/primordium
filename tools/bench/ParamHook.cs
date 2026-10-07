using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Primordium;

// Laws of the world (P, ParamRegistry) from the command line, for a single run, a batch, the long test:
//
//   --preset path.json            a saved preset: every law it names, the rest back to defaults (first)
//   --set Name=value              one law before the world is made (repeatable; --param is the same flag)
//   --param-at TICK:Name=value    a law changed between ticks during the run (World.SetParam, logged)
//   --catastrophe-at "TICK:spec"  a catastrophe between ticks (World.Catastrophe, logged), e.g. "3000:iceage days=5",
//                                 "6000:volcano x=-1 y=-1", "2000:drought x=100 y=60 r=24 days=5 dt=8" (Catastrophe.Parse)
//
// Names are those of `--list-params` (case does not matter); values are clamped to the law's range
// and rounded for integer laws, as the registry stores them (the run prints what was actually set).
// Laws are process-wide: a batch runs every world in its own process and hands each the same flags.
public static class ParamHook
{
    public sealed class Laws
    {
        public readonly List<string> Presets = new();
        public readonly List<(string name, double value)> Set = new();
        public readonly List<(long tick, string name, double value)> At = new();
        public readonly List<(long tick, Catastrophe c)> Catastrophes = new();
        public readonly List<string> Forward = new();   // the flags as given, for child processes

        // How the laws are written into result files: "Name=value;…", "@tick:Name=value", "preset:file", or "default".
        public string Describe()
        {
            var parts = Presets.Select(p => "preset:" + System.IO.Path.GetFileNameWithoutExtension(p))
                .Concat(Set.Select(s => $"{s.name}={s.value.ToString("R", CultureInfo.InvariantCulture)}"))
                .Concat(At.Select(a => $"@{a.tick}:{a.name}={a.value.ToString("R", CultureInfo.InvariantCulture)}"))
                .Concat(Catastrophes.Select(c => $"@{c.tick}:{c.c.Spec()}")).ToList();
            return parts.Count == 0 ? "default" : string.Join(";", parts).Replace(',', ' ');
        }

        public bool Any => Presets.Count + Set.Count + At.Count + Catastrophes.Count > 0;
    }

    static (string name, double value) NameValue(string flag, string text)
    {
        int eq = text.IndexOf('=');
        if (eq <= 0) throw new ArgumentException($"{flag} expects Name=value, got '{text}'");
        string name = text[..eq].Trim();
        var p = ParamRegistry.Find(name) ?? throw new ArgumentException($"{flag}: unknown law '{name}' (see --list-params)");
        if (!double.TryParse(text[(eq + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            throw new ArgumentException($"{flag}: '{text[(eq + 1)..]}' is not a number");
        return (p.Name, v);
    }

    // Reads the flags (validating names and numbers; nothing is changed yet).
    public static Laws Parse(string[] args)
    {
        var laws = new Laws();
        for (int i = 0; i < args.Length - 1; i++)
        {
            string flag = args[i], text = args[i + 1];
            switch (flag)
            {
                case "--preset":
                    if (!System.IO.File.Exists(text)) throw new ArgumentException($"--preset: no file {text}");
                    laws.Presets.Add(text);
                    break;
                case "--set":
                case "--param":
                    laws.Set.Add(NameValue(flag, text));
                    break;
                case "--param-at":
                    int colon = text.IndexOf(':');
                    if (colon <= 0 || !long.TryParse(text[..colon], out long tick)) throw new ArgumentException($"--param-at expects TICK:Name=value, got '{text}'");
                    var (name, value) = NameValue(flag, text[(colon + 1)..]);
                    laws.At.Add((tick, name, value));
                    break;
                case "--catastrophe-at":
                    int cc = text.IndexOf(':');
                    if (cc <= 0 || !long.TryParse(text[..cc], out long ct)) throw new ArgumentException($"--catastrophe-at expects TICK:spec, got '{text}'");
                    try { laws.Catastrophes.Add((ct, Catastrophe.Parse(text[(cc + 1)..]))); }
                    catch (FormatException e) { throw new ArgumentException($"--catastrophe-at: {e.Message}"); }
                    break;
                default: continue;
            }
            laws.Forward.Add(flag); laws.Forward.Add(text);
        }
        return laws;
    }

    // Applies presets, then single laws, through the registry (before a world is made). Returns what
    // the registry actually holds for each, for printing.
    public static List<string> Apply(Laws laws)
    {
        var done = new List<string>();
        foreach (var path in laws.Presets)
        {
            var unknown = ParamRegistry.Restore(ParamRegistry.LoadPreset(path).Values);
            done.Add($"preset {path}" + (unknown.Count > 0 ? $" (unknown laws ignored: {string.Join(", ", unknown)})" : ""));
        }
        foreach (var (name, value) in laws.Set)
        {
            if (!ParamRegistry.Set(name, value)) throw new ArgumentException($"unknown law {name}");
            double now = ParamRegistry.Get(name);
            done.Add($"{name}={now.ToString("G7", CultureInfo.InvariantCulture)}" + (Math.Abs(now - value) > 1e-6 * Math.Max(1, Math.Abs(value)) ? $" (asked {value.ToString("R", CultureInfo.InvariantCulture)}, clamped/rounded)" : ""));
        }
        return done;
    }

    // Applies the --param-at changes due before the step to tick `tick`; returns a line per change.
    public static IEnumerable<string> ApplyDue(Laws laws, World w)
    {
        foreach (var (at, c) in laws.Catastrophes)
            if (at == w.Tick)
            {
                string done = w.Catastrophe(c, out string error);
                yield return $"tick {w.Tick}: catastrophe {c.Spec()}: " + (done ?? "FAILED " + error);
            }
        foreach (var (at, name, value) in laws.At)
            if (at == w.Tick)
            {
                w.SetParam(name, value);
                yield return $"tick {w.Tick}: {name} = {ParamRegistry.Get(name).ToString("R", CultureInfo.InvariantCulture)}";
            }
    }
}
