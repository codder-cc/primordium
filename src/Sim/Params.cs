using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Primordium;

// What a world has to redo when a law changes (World.ApplyParamChanges).
[Flags]
public enum ParamEffect
{
    None = 0,
    Strength = 1,     // cached block strength is stale; every column is re-solved by the support solver
    MatCap = 2,       // Chemistry.MatCap (molecules that fill a voxel) is derived from it
    BodyVolume = 4,   // Chemistry.BodyVolume and every body's Volume are derived from it
}

// One tunable law of the world (a static field of P) with what the UI needs to offer it.
public sealed class ParamInfo
{
    public string Name { get; internal set; }
    public string Group { get; internal set; }
    public string Description { get; internal set; }   // short, Russian
    public double Default { get; internal set; }
    public double Min { get; internal set; }
    public double Max { get; internal set; }
    public double Step { get; internal set; }
    public bool IsInt { get; internal set; }
    // Live: takes effect in the running world at once. Otherwise it is read only when a world is
    // created (setting it now shapes the next new world).
    public bool Live { get; internal set; }
    public ParamEffect Effect { get; internal set; }
    public int Index { get; internal set; }   // position in ParamRegistry.All (and in value arrays)
    internal FieldInfo Field;
    internal long ChangedAt;                  // ParamRegistry.Version when it was last changed

    public double Value => ParamRegistry.Read(this);
    public bool IsDefault => Value == Default;
    public override string ToString() => $"{Name} = {Value} ({Group})";
}

// A named set of law values, saved as JSON (System.Text.Json). Values missing from a preset keep
// their defaults when it is applied.
public sealed class ParamPreset
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, double> Values { get; set; } = new();
}

// The registry of P's tunables: metadata, get/set by name, defaults, snapshots and presets, and a
// change notification. Changes in a running game go through SimRunner.SetParam (applied by the
// simulation thread between ticks, recorded in World.ParamLog), so a trajectory is the seed plus the
// timeline of law changes. Every change bumps Version; a World notices it before its next tick and
// invalidates whatever it derived from the changed laws.
public static class ParamRegistry
{
    static readonly List<ParamInfo> all = new();
    static readonly Dictionary<string, ParamInfo> byName = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ParamInfo> All => all;
    public static IReadOnlyList<string> Groups { get; }
    public static long Version { get; private set; }

    // Raised after a value changed (on the thread that changed it — the simulation thread in the game).
    public static event Action<ParamInfo, double, double> Changed;   // param, old value, new value

    // Current values in All order, replaced (never mutated) after every change: safe to read from any thread.
    public static double[] Published { get; private set; }

    static ParamRegistry()
    {
        // Planet
        I("DayLen", "Планета", "Тиков в сутках (меняет ход солнца сразу)", 50, 20000, 50);
        I("YearDays", "Планета", "Суток в году", 1, 200, 1);
        F("Tilt", "Планета", "Наклон оси, рад: сезоны, полярные день и ночь", 0, 1.2, 0.01);
        I("LightEvery", "Планета", "Свет и облака считаются раз в столько тиков", 1, 64, 1);
        I("EnvEvery", "Планета", "Химия почвы, климат и вода — раз в столько тиков", 1, 64, 1);
        I("ErodeEvery", "Планета", "Сползание склонов — раз в столько тиков", 1, 256, 1);
        // Mechanics
        F("Gravity", "Механика", "Сила тяжести: вес молекул в блоке и тел на полу", 0, 0.01, 0.0001, ParamEffect.Strength);
        F("CompressionK", "Механика", "Прочность блока на сжатие", 10, 5000, 10, ParamEffect.Strength);
        F("TensionK", "Механика", "Прочность боковой связи блоков (своды, нависания)", 1, 2000, 5, ParamEffect.Strength);
        F("CompactionPressure", "Механика", "Давление, при котором порода упорядочивается и захоронения реагируют", 0.5, 100, 0.5);
        I("StructureEvery", "Механика", "Расчёт опоры — не чаще, чем раз в столько тиков", 1, 64, 1);
        I("MetamorphEvery", "Механика", "Метаморфизм и адсорбция дождём — раз в столько тиков", 1, 1024, 1);
        // Rock
        F("BiteRate", "Порода", "Выветривание грани: доля молекулы за тик", 0, 0.1, 0.001);
        F("BiteCap", "Порода", "Запас выветривания грани, молекул", 0, 20, 0.5);
        F("FaceBarrier", "Порода", "Барьер, который грань берёт даром (вычитается из барьера)", 0, 5, 0.1);
        F("FaceWork", "Порода", "Работа на одну молекулу из грани (цена грызть породу)", 0.5, 100, 0.5);
        F("RockBarrier", "Порода", "Насколько крепко порода держит молекулы", 0, 20, 0.1);
        F("CatalysisMax", "Порода", "Доля барьера, которую снимает белок на связь", 0, 1, 0.01);
        // VM
        I("BaseCycles", "ВМ", "Тактов генома за тик (в тёплом теле)", 1, 64, 1);
        I("MaxCycles", "ВМ", "Предел тактов за тик", 1, 256, 1);
        F("CostInstr", "ВМ", "Энергия на одну команду", 0, 0.05, 0.0005);
        // Upkeep
        F("CostBase", "Содержание", "Плата за жизнь за тик", 0, 0.2, 0.001);
        F("CostMass", "Содержание", "Плата за единицу массы тела за тик", 0, 0.01, 0.0001);
        F("CostLen", "Содержание", "Плата за байт генома за тик", 0, 0.002, 0.00001);
        // Proteins
        F("CostExpress", "Белки", "Энергия на единицу белка (плюс молекула-субстрат)", 0, 5, 0.01);
        F("EnzDecay", "Белки", "Доля белка, распадающаяся за тик", 0, 0.1, 0.0005);
        F("Spont", "Белки", "Шанс реакции без белка (при 15 °C)", 0, 1, 0.01);
        F("EnzWidth", "Белки", "Ширина температурного окна белка, °C", 0.5, 60, 0.5);
        // Light
        F("PhotonK", "Свет", "Фотонов в клетку за тик при полном свете", 0, 1, 0.005);
        F("PhotonCap", "Свет", "Больше фотонов клетка не копит", 0.1, 50, 0.1);
        // Temperature
        F("ComfortLo", "Температура", "Нижняя граница удобной температуры, °C", -40, 40, 0.5);
        F("ComfortHi", "Температура", "Верхняя граница удобной температуры, °C", -20, 80, 0.5);
        F("TempTau", "Температура", "Градусов вне полосы на e-кратный рост вреда", 0.5, 50, 0.5);
        F("FreezeK", "Температура", "Вред холода за тик", 0, 0.1, 0.0005);
        F("HarmExpMax", "Температура", "Предел показателя экспоненты вреда", 1, 80, 1);
        F("HeatK", "Температура", "Вред жары за тик", 0, 0.1, 0.0005);
        F("Antifreeze", "Температура", "На сколько °C ниже полосы терпит тело, набитое молекулами", 0, 40, 0.5);
        // Actions
        F("CostIntake", "Действия", "Поглощение молекулы", 0, 0.1, 0.0005);
        F("CostExpel", "Действия", "Выброс молекулы", 0, 0.1, 0.0005);
        F("CostClimb", "Действия", "Подъём на блок за единицу массы", 0, 0.2, 0.001);
        F("CostSocial", "Действия", "Взять, дать, делиться, связь, спаривание", 0, 0.5, 0.001);
        F("CostMine", "Действия", "Попытка грызть породу (базовая)", 0, 0.5, 0.001);
        F("CostDig", "Действия", "Рытьё: сдвинуть блок целиком", 0, 20, 0.1);
        F("CostPile", "Действия", "Сложить блок из твёрдых молекул", 0, 20, 0.1);
        F("CostInjectBase", "Действия", "Вставка генов: основа", 0, 2, 0.01);
        F("CostInjectByte", "Действия", "Вставка генов: за байт", 0, 1, 0.005);
        F("CostCutBase", "Действия", "Вырезание генов: основа", 0, 2, 0.01);
        F("CostCutByte", "Действия", "Вырезание генов: за байт", 0, 1, 0.005);
        F("CostGrow", "Действия", "Выделить агрегат (grow)", 0, 5, 0.01);
        F("CostPush", "Действия", "Толчок мотора за единицу массы", 0, 0.5, 0.001);
        F("CostFall", "Действия", "Падение на блок сверх первого", 0, 5, 0.01);
        F("CostLook", "Действия", "Зрение за клетку дальности", 0, 0.05, 0.0005);
        I("PileUnits", "Действия", "Сколько одинаковых твёрдых молекул нужно для pile", 1, 64, 1);
        // Body
        I("MinBody", "Тело", "Меньше молекул — тело распадается", 1, 32, 1);
        F("StoreBase", "Тело", "Удобный запас энергии: основа", 0, 500, 1);
        F("StorePerMass", "Тело", "Удобный запас энергии: на единицу массы", 0, 20, 0.1);
        F("HoldK", "Тело", "Утечка запаса энергии теплом", 0, 0.05, 0.0001);
        F("CostLink", "Тело", "Связь с партнёром за тик", 0, 0.1, 0.0005);
        F("DecayK", "Тело", "Шанс распада нестабильной молекулы в теле за тик", 0, 0.01, 0.00001);
        F("UvK", "Тело", "Мутации от света: на байт генома за тик", 0, 0.0001, 0.000001);
        F("HeatToTemp", "Тело", "°C нагрева клетки на единицу рассеянной энергии", 0, 2, 0.01);
        F("GrowMass", "Тело", "Масса для второй клетки тела", 1, 1000, 1);
        F("GrowPow", "Тело", "Рост массы на каждую следующую клетку (степень)", 0.5, 4, 0.05);
        I("InvPerCell", "Тело", "Удобно молекул на клетку тела", 1, 1024, 1);
        F("CostCell", "Тело", "Каждая лишняя клетка тела за тик", 0, 0.1, 0.0005);
        // Volume
        F("VoxelSpace", "Объём", "Объём вокселя: полный блок и место над полом", 100, 10000, 50, ParamEffect.MatCap | ParamEffect.Strength);
        F("LooseBulk", "Объём", "Во сколько раз рыхлые останки объёмнее упакованных", 0.5, 10, 0.1);
        F("CompactShare", "Объём", "Доля пола, сверх которой останки стекают или прессуются", 0.05, 1, 0.01);
        // Energy
        F("EnergyK", "Энергия", "Энергия связи → энергия тела", 0, 5, 0.05);
        F("HeatShare", "Энергия", "Доля энергии реакции, уходящая в тепло тела", 0, 0.9, 0.01);
        // Reproduction
        F("DivMinEnergy", "Размножение", "Энергия, нужная для деления", 0, 200, 0.5);
        F("DivCostBase", "Размножение", "Деление: основа", 0, 50, 0.1);
        F("DivCostByte", "Размножение", "Деление: за байт генома", 0, 1, 0.005);
        I("DivMinBody", "Размножение", "Молекул в теле для деления", 2, 256, 1);
        F("MateMinEnergy", "Размножение", "Энергия, нужная для спаривания", 0, 200, 0.5);
        // Links, motion
        F("LinkFlow", "Связи", "Выравнивание энергии по связи за тик", 0, 0.5, 0.005);
        F("Recoil", "Движение", "Отдача от выброшенной массы", 0, 5, 0.05);
        F("Friction", "Движение", "Скорость, сохраняемая за тик на суше", 0, 1, 0.01);
        // Environment
        I("InitialPop", "Среда", "Случайных геномов в начале мира", 0, 50000, 100, live: false);
        F("AbioChance", "Среда", "Шанс самозарождения за тик (если абиогенез включён)", 0, 0.1, 0.0005);
        I("SpawnBody", "Среда", "Молекул в теле самозарождённого", 1, 64, 1);
        F("SpawnEnergy", "Среда", "Энергия самозарождённого (из местных реакций)", 0, 200, 1);
        F("InitLitter", "Среда", "Первичные останки: доля состава верхнего блока", 0, 10, 0.1, live: false);
        I("VentCount", "Среда", "Сколько вулканов держит мир", 0, 32, 1);
        // Climate
        F("TEquator", "Климат", "Температура экватора, °C", -50, 80, 0.5);
        F("TPole", "Климат", "Температура полюса, °C", -80, 50, 0.5);
        F("TDay", "Климат", "Суточный размах от света, °C", 0, 50, 0.5);
        F("TLapse", "Климат", "Похолодание на уровень высоты, °C", 0, 5, 0.05);
        F("TRelax", "Климат", "Скорость, с которой суша идёт к климату (за шаг среды)", 0, 0.5, 0.001);
        F("TRelaxWater", "Климат", "То же для воды", 0, 0.5, 0.0005);
        // Water
        F("SeaShare", "Вода", "Доля суши под водой в начале", 0, 0.9, 0.01, live: false);
        F("SwimDepth", "Вода", "Глубже — тело под водой", 0.1, 5, 0.05);
        F("WaterDensity", "Вода", "Плотность воды (тела тяжелее тонут)", 0.1, 3, 0.01);
        F("GasExpand", "Вода", "Во сколько раз газ в теле объёмнее (пузырь)", 1, 50, 0.5, ParamEffect.BodyVolume);
        F("Buoyancy", "Вода", "Сила плавучести", 0, 1, 0.01);
        F("WaterDrag", "Вода", "Вертикальная скорость, сохраняемая за тик", 0, 0.99, 0.01);
        F("CostSwim", "Вода", "Гребок за единицу массы", 0, 0.05, 0.0005);
        F("DepthK", "Вода", "Удорожание движения на блок глубины", 0, 2, 0.01);
        F("WaterFriction", "Вода", "Скорость, сохраняемая за тик в воде", 0, 1, 0.01);
        F("WaterDim", "Вода", "Ослабление света на блок воды или льда", 0, 3, 0.01);
        F("Solubility", "Вода", "Доля донных останков, доступная пловцу", 0, 1, 0.005);
        F("Evap", "Вода", "Испарение за шаг среды при 20 °C", 0, 0.01, 0.0001);
        F("RainShare", "Вода", "Доля влаги воздуха, выпадающая за шаг среды", 0, 0.5, 0.005);
        // Space
        I("StrikeMin", "Космос", "Меньше всего тиков между ударами", 1, 200000, 100);
        I("StrikeMax", "Космос", "Больше всего тиков между ударами", 1, 200000, 100);
        // Chronicle (only what is remembered and shown)
        I("ChronicleCap", "Хроника", "Сколько обычных событий хроники помнить (важные — всегда)", 100, 200000, 100);
        I("FossilCap", "Хроника", "Сколько окаменелостей хранить (лишние — наименее важные)", 10, 20000, 10);
        I("ChronicleTrackKids", "Хроника", "Вести биографию детей посаженных существ (1 — да)", 0, 1, 1);
        F("ChronicleCaveDays", "Хроника", "Суток под крышей для события «житель пещер»", 0.1, 50, 0.1);

        var missing = typeof(P).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => !f.IsLiteral && !f.IsInitOnly && !byName.ContainsKey(f.Name)).Select(f => f.Name).ToList();
        if (missing.Count > 0) throw new InvalidOperationException("P fields without registry entries: " + string.Join(", ", missing));
        Groups = all.Select(p => p.Group).Distinct().ToList();
        Publish();
    }

    static void F(string name, string group, string desc, double min, double max, double step, ParamEffect effect = ParamEffect.None, bool live = true) =>
        Add(name, group, desc, min, max, step, false, effect, live);
    static void I(string name, string group, string desc, double min, double max, double step, bool live = true) =>
        Add(name, group, desc, min, max, step, true, ParamEffect.None, live);

    static void Add(string name, string group, string desc, double min, double max, double step, bool isInt, ParamEffect effect, bool live)
    {
        var f = typeof(P).GetField(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"P has no field {name}");
        if (f.IsLiteral || f.IsInitOnly) throw new InvalidOperationException($"P.{name} is not tunable");
        if ((f.FieldType == typeof(int)) != isInt) throw new InvalidOperationException($"P.{name}: wrong type in the registry");
        var p = new ParamInfo
        {
            Name = name, Group = group, Description = desc, Min = min, Max = max, Step = step, IsInt = isInt,
            Live = live, Effect = effect, Field = f, Default = P.Defaults[name], Index = all.Count,
        };
        all.Add(p);
        byName[name] = p;
    }

    static void Publish() => Published = all.Select(Read).ToArray();

    internal static double Read(ParamInfo p) => p.IsInt ? (int)p.Field.GetValue(null) : (double)(float)p.Field.GetValue(null);

    public static ParamInfo Find(string name) => name != null && byName.TryGetValue(name, out var p) ? p : null;

    public static double Get(string name) => Read(Find(name) ?? throw new ArgumentException($"unknown parameter {name}"));

    // The value as it would be stored: clamped to the range, whole for integer laws, float precision.
    public static double Normalize(ParamInfo p, double value)
    {
        if (double.IsNaN(value)) value = p.Default;
        value = Math.Clamp(value, p.Min, p.Max);
        return p.IsInt ? Math.Round(value) : (float)value;
    }

    // Sets a law (clamped to its range). Returns false if there is no such law. Call between ticks.
    public static bool Set(string name, double value)
    {
        var p = Find(name);
        if (p == null) return false;
        Set(p, value);
        return true;
    }

    public static void Set(ParamInfo p, double value)
    {
        double old = Read(p);
        value = Normalize(p, value);
        if (value == old) return;
        if (p.IsInt) p.Field.SetValue(null, (int)value); else p.Field.SetValue(null, (float)value);
        p.ChangedAt = ++Version;
        Publish();
        Changed?.Invoke(p, old, value);
    }

    public static void ResetDefaults()
    {
        foreach (var p in all) Set(p, p.Default);
    }

    // Every law's current value.
    public static Dictionary<string, double> Snapshot() => all.ToDictionary(p => p.Name, Read);

    // Only the laws that differ from their defaults (what a preset needs to say).
    public static Dictionary<string, double> Changes() => all.Where(p => !p.IsDefault).ToDictionary(p => p.Name, Read);

    // Applies values by name; unknown names are reported, not fatal (a preset from another version).
    // resetFirst: laws the dictionary does not mention go back to their defaults.
    public static List<string> Restore(IReadOnlyDictionary<string, double> values, bool resetFirst = true)
    {
        var unknown = new List<string>();
        if (resetFirst)
            foreach (var p in all)
                if (values == null || !values.ContainsKey(p.Name)) Set(p, p.Default);
        if (values == null) return unknown;
        foreach (var (name, v) in values)
            if (!Set(name, v)) unknown.Add(name);
        return unknown;
    }

    // Laws changed after `since` (a Version seen before), with the effects they call for.
    public static ParamEffect EffectsSince(long since)
    {
        var e = ParamEffect.None;
        foreach (var p in all) if (p.ChangedAt > since) e |= p.Effect;
        return e;
    }

    static readonly JsonSerializerOptions json = new() { WriteIndented = true };

    public static string ToJson(ParamPreset preset) => JsonSerializer.Serialize(preset, json);
    public static ParamPreset FromJson(string text) => JsonSerializer.Deserialize<ParamPreset>(text) ?? throw new InvalidDataException("empty preset");

    // A preset of the current laws: only those that differ from the defaults, unless `full`.
    public static ParamPreset Capture(string name = "", string description = "", bool full = false) =>
        new() { Name = name, Description = description, Values = full ? Snapshot() : Changes() };

    public static void SavePreset(string path, ParamPreset preset)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson(preset));
    }

    public static ParamPreset LoadPreset(string path) => FromJson(File.ReadAllText(path));
}
