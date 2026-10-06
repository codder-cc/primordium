using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Primordium;

// `--set Name=value`: change a world parameter for one run (or for every run of a batch: each run is
// its own process, so all of them see the same values).
//
// The hook first looks for a runtime registry: a public static method TrySet(string name, string
// value) or TrySet(string name, double value) returning bool on Primordium.Params,
// Primordium.ParamRegistry or Primordium.P. Once the parameters become runtime values with a
// registry, it is used as is. Until then it falls back to reflection over public static fields of P
// (`Name` or `P.Name`) and of World (`World.TileSize`). Constants of P are compile-time literals
// (inlined where they are used): they cannot be changed at run time, and the hook says so instead of
// pretending.
public static class ParamHook
{
    public static List<(string name, string value)> Parse(string[] args)
    {
        var list = new List<(string, string)>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--set") continue;
            int eq = args[i + 1].IndexOf('=');
            if (eq <= 0) throw new ArgumentException($"--set expects Name=value, got '{args[i + 1]}'");
            list.Add((args[i + 1][..eq].Trim(), args[i + 1][(eq + 1)..].Trim()));
        }
        return list;
    }

    // Applies one setting; returns a description of what was set. Throws if it cannot be set.
    public static string Apply(string name, string value)
    {
        var asm = typeof(World).Assembly;
        foreach (var typeName in new[] { "Primordium.Params", "Primordium.ParamRegistry", "Primordium.P" })
        {
            var type = asm.GetType(typeName);
            if (type == null) continue;
            var bys = type.GetMethod("TrySet", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null);
            if (bys != null && bys.ReturnType == typeof(bool))
            {
                if (!(bool)bys.Invoke(null, new object[] { name, value })) throw new ArgumentException($"registry {typeName} refused {name}={value}");
                return $"{name}={value} (registry {typeName})";
            }
            var byd = type.GetMethod("TrySet", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(double) }, null);
            if (byd != null && byd.ReturnType == typeof(bool))
            {
                if (!(bool)byd.Invoke(null, new object[] { name, double.Parse(value, CultureInfo.InvariantCulture) }))
                    throw new ArgumentException($"registry {typeName} refused {name}={value}");
                return $"{name}={value} (registry {typeName})";
            }
        }
        string owner = "P", field = name;
        int dot = name.IndexOf('.');
        if (dot > 0) { owner = name[..dot]; field = name[(dot + 1)..]; }
        var t = owner switch { "P" => typeof(P), "World" => typeof(World), _ => throw new ArgumentException($"unknown parameter owner '{owner}' (P or World)") };
        var f = t.GetField(field, BindingFlags.Public | BindingFlags.Static);
        if (f == null) throw new ArgumentException($"no public static field {owner}.{field}");
        if (f.IsLiteral)
            throw new ArgumentException($"{owner}.{field} is a compile-time constant in this build (inlined where used): it can only be changed by editing P.cs and rebuilding, until the parameters become runtime values with a registry");
        if (f.IsInitOnly) throw new ArgumentException($"{owner}.{field} is readonly");
        object v = Convert.ChangeType(value, f.FieldType, CultureInfo.InvariantCulture);
        f.SetValue(null, v);
        return $"{owner}.{field}={Convert.ToString(f.GetValue(null), CultureInfo.InvariantCulture)} (field)";
    }

    // How the settings are written into result files: "Name=value;Name=value", or "default".
    public static string Describe(List<(string name, string value)> set) =>
        set.Count == 0 ? "default" : string.Join(";", set.Select(s => $"{s.name}={s.value}"));
}
