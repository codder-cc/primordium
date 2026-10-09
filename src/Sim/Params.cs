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
    public string Group { get; internal set; }   // the group's key (stable, Russian); GroupTitle is what is shown
    public string GroupTitle => ParamRegistry.GroupTitle(Group);
    public string DescriptionEn { get; internal set; }
    public string DescriptionRu { get; internal set; }
    public string Description => Loc.T(DescriptionEn, DescriptionRu);   // short, in the current language
    public string DescriptionBoth => Loc.Both(DescriptionEn, DescriptionRu);   // for text that is kept (chronicle)
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

    // Shown names of the groups (the keys stay Russian: they are what ParamInfo.Group holds).
    static readonly Dictionary<string, string> groupEn = new()
    {
        ["Планета"] = "Planet", ["Механика"] = "Mechanics", ["Порода"] = "Rock", ["ВМ"] = "VM",
        ["Содержание"] = "Upkeep", ["Белки"] = "Proteins", ["Свет"] = "Light", ["Температура"] = "Temperature",
        ["Действия"] = "Actions", ["Тело"] = "Body", ["Износ"] = "Wear", ["Объём"] = "Volume",
        ["Энергия"] = "Energy", ["Размножение"] = "Reproduction", ["Связи"] = "Links", ["Движение"] = "Motion",
        ["Среда"] = "Environment", ["Геохимия"] = "Geochemistry", ["Климат"] = "Climate", ["Вода"] = "Water",
        ["Ресурсы"] = "Resources", ["Космос"] = "Space", ["Хроника"] = "Chronicle",
    };

    // The group's name in the current language.
    public static string GroupTitle(string group) =>
        group != null && Loc.En && groupEn.TryGetValue(group, out var en) ? en : group;

    // Raised after a value changed (on the thread that changed it — the simulation thread in the game).
    public static event Action<ParamInfo, double, double> Changed;   // param, old value, new value

    // Current values in All order, replaced (never mutated) after every change: safe to read from any thread.
    public static double[] Published { get; private set; }

    static ParamRegistry()
    {
        // Planet
        I("DayLen", "Планета", "Ticks per day (moves the sun at once)", "Тиков в сутках (меняет ход солнца сразу)", 50, 20000, 50);
        I("YearDays", "Планета", "Days per year", "Суток в году", 1, 200, 1);
        F("Tilt", "Планета", "Axial tilt, rad: seasons, polar day and night", "Наклон оси, рад: сезоны, полярные день и ночь", 0, 1.2, 0.01);
        I("LightEvery", "Планета", "Light and clouds computed every N ticks", "Свет и облака считаются раз в столько тиков", 1, 64, 1);
        I("EnvEvery", "Планета", "Soil chemistry, climate and water: every N ticks", "Химия почвы, климат и вода — раз в столько тиков", 1, 64, 1);
        I("ErodeEvery", "Планета", "Slope creep: every N ticks", "Сползание склонов — раз в столько тиков", 1, 256, 1);
        // Mechanics
        F("Gravity", "Механика", "Gravity: weight of molecules in a block and of bodies on the floor", "Сила тяжести: вес молекул в блоке и тел на полу", 0, 0.01, 0.0001, ParamEffect.Strength);
        F("CompressionK", "Механика", "Compressive strength of a block", "Прочность блока на сжатие", 10, 5000, 10, ParamEffect.Strength);
        F("TensionK", "Механика", "Strength of the lateral bond between blocks (arches, overhangs)", "Прочность боковой связи блоков (своды, нависания)", 1, 2000, 5, ParamEffect.Strength);
        F("CompactionPressure", "Механика", "Pressure at which rock orders and burials react", "Давление, при котором порода упорядочивается и захоронения реагируют", 0.5, 100, 0.5);
        F("FrictionQ", "Механика", "Rock friction (Mohr–Coulomb): how many times lateral confinement adds to compressive strength (≈4 is a friction angle of ~37°)", "Трение породы (Мор — Кулон): во сколько раз боковое обжатие прибавляет прочности на сжатие (≈4 — угол трения ~37°)", 0, 10, 0.1);
        F("Bulking", "Механика", "Bulking: crushed, disordered mass takes (1 + this) times more room than the same molecules in an ordered lattice (by the molecule's looseness)", "Разрыхление: во сколько раз (1 + это) больше места занимает раздробленная, неупорядоченная масса, чем те же молекулы в упорядоченной решётке (по рыхлости молекулы)", 0, 2, 0.05);
        F("DensifyK", "Механика", "Densification: each step of ordering needs pressure CompactionPressure × e^(DensifyK × order) — the denser, the harder", "Уплотнение: каждый шаг упорядочения требует давления CompactionPressure × e^(DensifyK × порядок) — чем плотнее, тем труднее", 0, 10, 0.1);
        F("LooseStrength", "Механика", "Own strength of disordered mass (fill, rubble) with no cement between grains; fully ordered rock has 1.5; in between it goes with the square of order", "Собственная прочность неупорядоченной массы (насыпь, щебень) без цемента между зёрнами; у полностью упорядоченной породы — 1,5; между ними — по квадрату порядка", 0, 1.5, 0.01);
        F("ModulusRatio", "Механика", "Stiffness: a block's modulus is its uniaxial strength times this (E/UCS, 100–500 for rock); it strains load/modulus, at most 1/this before failing; squeezed and packed room lets the column above settle, unloading lets it spring back", "Жёсткость: модуль блока — его одноосная прочность, умноженная на это (E/UCS, у пород 100–500); блок сжимается на нагрузку/модуль, не больше 1/это до разрушения; сжатое и уплотнённое место даёт столбу над ним осесть, разгрузка — отскочить", 2, 2000, 1);
        F("LateralK", "Механика", "Lateral pressure at rest: share of a neighbour's vertical load with which it props a block from the side (≈0.4)", "Боковое давление в покое: доля вертикальной нагрузки соседа, которой он подпирает блок сбоку (≈0,4)", 0, 1, 0.01);
        I("StructureEvery", "Механика", "Support solve: at most once every N ticks", "Расчёт опоры — не чаще, чем раз в столько тиков", 1, 64, 1);
        I("StructureVoxels", "Механика", "Support solve resolution: one more StructureEvery between solves per this many hanging voxels the last solve visited (cost, not physics)", "Разрешение расчёта опоры: ещё один StructureEvery между расчётами на столько висящих вокселей прошлого расчёта (цена, не физика)", 100, 100000, 100);
        I("StructureStretch", "Механика", "Support solve resolution: at most this many times StructureEvery between solves", "Разрешение расчёта опоры: не больше стольких StructureEvery между расчётами", 1, 64, 1);
        I("MetamorphEvery", "Механика", "Metamorphism and rain adsorption: every N ticks", "Метаморфизм и адсорбция дождём — раз в столько тиков", 1, 1024, 1);
        // Rock
        F("BiteRate", "Порода", "Face weathering: fraction of a molecule per tick", "Выветривание грани: доля молекулы за тик", 0, 0.1, 0.001);
        F("BiteCap", "Порода", "Face weathering reserve, molecules", "Запас выветривания грани, молекул", 0, 20, 0.5);
        F("FaceBarrier", "Порода", "Barrier an exposed face gets for free (subtracted from the barrier)", "Барьер, который грань берёт даром (вычитается из барьера)", 0, 5, 0.1);
        F("FaceWork", "Порода", "Work per molecule taken from a face (the cost of gnawing rock)", "Работа на одну молекулу из грани (цена грызть породу)", 0.5, 100, 0.5);
        F("RockBarrier", "Порода", "How firmly rock holds its molecules", "Насколько крепко порода держит молекулы", 0, 20, 0.1);
        F("CatalysisMax", "Порода", "Share of the barrier a protein removes per bond", "Доля барьера, которую снимает белок на связь", 0, 1, 0.01);
        // VM
        I("BaseCycles", "ВМ", "Genome cycles per tick (in a warm body)", "Тактов генома за тик (в тёплом теле)", 1, 64, 1);
        I("MaxCycles", "ВМ", "Cycle limit per tick", "Предел тактов за тик", 1, 256, 1);
        F("CostInstr", "ВМ", "Energy per instruction", "Энергия на одну команду", 0, 0.05, 0.0005);
        // Upkeep
        F("CostBase", "Содержание", "Cost of living per tick", "Плата за жизнь за тик", 0, 0.2, 0.001);
        F("CostMass", "Содержание", "Cost per unit of body mass per tick", "Плата за единицу массы тела за тик", 0, 0.01, 0.0001);
        F("CostLen", "Содержание", "Cost per genome byte per tick", "Плата за байт генома за тик", 0, 0.002, 0.00001);
        // Proteins
        F("CostExpress", "Белки", "Energy per unit of protein (plus a substrate molecule)", "Энергия на единицу белка (плюс молекула-субстрат)", 0, 5, 0.01);
        F("EnzDecay", "Белки", "Share of protein that decays per tick", "Доля белка, распадающаяся за тик", 0, 0.1, 0.0005);
        F("Spont", "Белки", "Chance of a reaction without a protein (at 15 °C)", "Шанс реакции без белка (при 15 °C)", 0, 1, 0.01);
        F("EnzWidth", "Белки", "Width of a protein's temperature window, °C", "Ширина температурного окна белка, °C", 0.5, 60, 0.5);
        // Light
        F("PhotonK", "Свет", "Photons per cell per tick in full light", "Фотонов в клетку за тик при полном свете", 0, 1, 0.005);
        F("PhotonCap", "Свет", "A cell stores no more photons than this", "Больше фотонов клетка не копит", 0.1, 50, 0.1);
        I("Insolation", "Свет", "Cosine-law insolation: power = sin of sun elevation, latitude climate from the daily sum (1 on, 0 the old curve, saturating already at ~17°)", "Инсоляция по закону косинуса: мощность = sin высоты солнца, климат широты — от суточной суммы (1 — вкл, 0 — прежняя кривая, насыщение уже при ~17°)", 0, 1, 1);
        F("InsolExp", "Свет", "Law exponent: power = sin(elevation)^k (1 is the cosine law)", "Показатель закона: мощность = sin(высоты)^k (1 — закон косинуса)", 0.25, 4, 0.05);
        F("TwilightLo", "Свет", "Twilight: sine of sun elevation where light begins", "Сумерки: синус высоты солнца, где свет начинается", -0.3, 0, 0.01);
        F("TwilightHi", "Свет", "Twilight: sine of elevation from which the cosine law applies", "Сумерки: синус высоты, с которого действует закон косинуса", 0.01, 0.3, 0.01);
        F("ClimInertia", "Свет", "Share of annual mean insolation in latitude climate (thermal inertia; 0 is the day's climate)", "Доля годовой средней инсоляции в климате широты (тепловая инерция; 0 — климат дня)", 0, 1, 0.05);
        F("ClimPow", "Свет", "Latitude climate: share of the pole–equator range = (insolation/equatorial)^k", "Климат широты: доля размаха полюс–экватор = (инсоляция/экваториальная)^k", 0.3, 5, 0.05);
        I("Transparency", "Свет", "Uneven atmospheric transparency: drifting clear and hazy areas (1 on, 0 clear sky everywhere)", "Неоднородная прозрачность атмосферы: дрейфующие ясные и мутные области (1 — вкл, 0 — небо везде ясное)", 0, 1, 1);
        F("TranspMin", "Свет", "Transparency of the haziest sky", "Прозрачность самого мутного неба", 0, 1, 0.01);
        F("TranspNoise", "Свет", "Amplitude of drifting transparency patches over the belts", "Размах дрейфующих пятен прозрачности поверх поясов", 0, 2, 0.05);
        F("TranspScale", "Свет", "Size of transparency patches, share of the planet's circumference", "Размер пятен прозрачности, доля окружности планеты", 0.05, 1, 0.01);
        F("TranspDrift", "Свет", "Drift of transparency patches, cells per day", "Дрейф пятен прозрачности, клеток за сутки", 0, 50, 0.1);
        F("TranspAlt", "Свет", "Transparency gain per level of altitude above sea", "Прибавка прозрачности на уровень высоты над морем", 0, 0.1, 0.001);
        F("TranspHydro", "Свет", "Coupling to water: clear sky evaporates more and rains less", "Связь с водой: ясное небо испаряет больше и дождит меньше", 0, 1, 0.05);
        F("ShadeK", "Свет", "Shading: in a cell, higher (and larger) bodies take light first; 0 off", "Затенение: в клетке свет сначала берут тела выше (и крупнее); 0 — выкл", 0, 20, 0.1);
        I("Canopy", "Свет", "Light in a cell: 0 — one shared pool, those above have the first chance (A); 1 — canopy: light goes down through the bodies, each stops 1−e^(−ShadeK·cover) of what reaches it, the rest is lost on the ground (B)", "Свет в клетке: 0 — общий запас, у верхних первый шанс (A); 1 — полог: свет идёт вниз сквозь тела, каждое задерживает 1−e^(−ShadeK·покрытие) дошедшего, остаток теряется на земле (B)", 0, 1, 1);
        // Temperature
        F("ComfortLo", "Температура", "Lower bound of comfortable temperature, °C", "Нижняя граница удобной температуры, °C", -40, 40, 0.5);
        F("ComfortHi", "Температура", "Upper bound of comfortable temperature, °C", "Верхняя граница удобной температуры, °C", -20, 80, 0.5);
        F("TempTau", "Температура", "Degrees outside the band per e-fold growth of harm", "Градусов вне полосы на e-кратный рост вреда", 0.5, 50, 0.5);
        F("FreezeK", "Температура", "Cold harm per tick", "Вред холода за тик", 0, 0.1, 0.0005);
        F("HarmExpMax", "Температура", "Cap on the harm exponent", "Предел показателя экспоненты вреда", 1, 80, 1);
        F("HeatK", "Температура", "Heat harm per tick", "Вред жары за тик", 0, 0.1, 0.0005);
        F("Antifreeze", "Температура", "How many °C below the band a body packed with molecules tolerates", "На сколько °C ниже полосы терпит тело, набитое молекулами", 0, 40, 0.5);
        // Actions
        F("CostIntake", "Действия", "Taking in a molecule", "Поглощение молекулы", 0, 0.1, 0.0005);
        F("CostExpel", "Действия", "Expelling a molecule", "Выброс молекулы", 0, 0.1, 0.0005);
        F("CostClimb", "Действия", "Climbing one block, per unit of mass", "Подъём на блок за единицу массы", 0, 0.2, 0.001);
        F("CostSocial", "Действия", "Take, give, share, link, mate", "Взять, дать, делиться, связь, спаривание", 0, 0.5, 0.001);
        F("CostMine", "Действия", "Attempt to gnaw rock (base)", "Попытка грызть породу (базовая)", 0, 0.5, 0.001);
        F("CostDig", "Действия", "Digging: moving a whole block", "Рытьё: сдвинуть блок целиком", 0, 20, 0.1);
        F("CostPile", "Действия", "Piling a block from solid molecules", "Сложить блок из твёрдых молекул", 0, 20, 0.1);
        F("BodyHold", "Действия", "How firmly a living body holds its molecules: tearing one out (attack, take) costs this times the work of gnawing it out of a face of the same disordered aggregate", "Насколько крепко живое тело держит свои молекулы: вырвать одну (attack, take) стоит столько раз работу выгрызть её из грани такого же беспорядочного агрегата", 0.05, 4, 0.05);
        F("StrikeUnit", "Действия", "Work of an attack per unit of its argument", "Работа удара на единицу его аргумента", 0.01, 1, 0.01);
        F("ShareUnit", "Действия", "Energy handed over by share per unit of its argument", "Энергия, передаваемая share, на единицу аргумента", 0.01, 1, 0.005);
        F("MateShare", "Действия", "Share of each parent's energy and molecules that goes into a mated child", "Доля энергии и молекул каждого родителя, уходящая в ребёнка от спаривания", 0.05, 0.5, 0.01);
        I("AlarmTicks", "Действия", "How long an attack in a cell can be noticed (hurt), ticks", "Сколько тиков удар в клетке ещё заметен (hurt)", 1, 128, 1);
        I("HandshakeTicks", "Действия", "Window in which both sides must want to mate or link, ticks", "Окно, в котором обе стороны должны захотеть спаривания или связи, тиков", 1, 64, 1);
        F("TornStore", "Действия", "Share of a body's stored energy that goes with molecules torn or pulled out of it (attack, take), in proportion to the molecules taken (0: the store stays behind)", "Доля запаса энергии тела, которая уходит с вырванными из него молекулами (attack, take), пропорционально числу молекул (0 — запас остаётся в теле)", 0, 1, 0.05);
        F("CostInjectBase", "Действия", "Gene injection: base", "Вставка генов: основа", 0, 2, 0.01);
        F("CostInjectByte", "Действия", "Gene injection: per byte", "Вставка генов: за байт", 0, 1, 0.005);
        F("CostCutBase", "Действия", "Gene excision: base", "Вырезание генов: основа", 0, 2, 0.01);
        F("CostCutByte", "Действия", "Gene excision: per byte", "Вырезание генов: за байт", 0, 1, 0.005);
        F("CostGrow", "Действия", "Secreting an aggregate (grow)", "Выделить агрегат (grow)", 0, 5, 0.01);
        F("CostPush", "Действия", "Motor push per unit of mass", "Толчок мотора за единицу массы", 0, 0.5, 0.001);
        F("CostFall", "Действия", "Fall per block beyond the first", "Падение на блок сверх первого", 0, 5, 0.01);
        F("CostLook", "Действия", "Vision per cell of range", "Зрение за клетку дальности", 0, 0.05, 0.0005);
        I("PileUnits", "Действия", "How many identical solid molecules pile needs", "Сколько одинаковых твёрдых молекул нужно для pile", 1, 64, 1);
        // Body
        I("MinBody", "Тело", "Fewer molecules and the body falls apart", "Меньше молекул — тело распадается", 1, 32, 1);
        F("StoreBase", "Тело", "Comfortable energy store: base", "Удобный запас энергии: основа", 0, 500, 1);
        F("StorePerMass", "Тело", "Comfortable energy store: per unit of mass", "Удобный запас энергии: на единицу массы", 0, 20, 0.1);
        F("HoldK", "Тело", "Leak of the energy store as heat", "Утечка запаса энергии теплом", 0, 0.05, 0.0001);
        F("CostLink", "Тело", "Link to a partner per tick", "Связь с партнёром за тик", 0, 0.1, 0.0005);
        F("DecayK", "Тело", "Chance an unstable molecule in a body decays per tick", "Шанс распада нестабильной молекулы в теле за тик", 0, 0.01, 0.00001);
        // Wear of body matter (World.Wear)
        F("PhotoDamage", "Износ", "Photodamage: chance a captured photon destroys the excited body molecule instead of filling the store (at zero hold; 0 off)", "Фотоповреждение: шанс, что пойманный фотон разрушает возбуждённую молекулу тела вместо запаса (при нулевом удержании; 0 — выкл)", 0, 1, 0.01);
        F("PhotoHold", "Износ", "Photodamage: how much a molecule's hold (its connectivity + the body matrix) per unit of excitation energy lowers the chance, e-fold", "Фотоповреждение: насколько удержание молекулы (её связность + матрица тела) на единицу энергии возбуждения снижает шанс, e-кратно", 0, 100, 0.5);
        F("PhotoCage", "Износ", "Photodamage: contribution of the body matrix (mean connectivity of its molecules × fill) to the hold", "Фотоповреждение: вклад матрицы тела (средняя связность его молекул × заполненность) в удержание", 0, 10, 0.1);
        F("WearK", "Износ", "Wear: chance per tick that a body molecule (at zero connectivity, 15 °C) loses its bond to the body and leaves by decay or whole (0 off)", "Износ: шанс за тик, что молекула тела (при нулевой связности, 15 °C) теряет связь с телом и уходит распадом или целой (0 — выкл)", 0, 0.01, 0.00001);
        F("ReactK", "Износ", "Reactive damage: chance per tick that a held molecule reacts with the body's proteins, per unit of its reactivity (mean atom affinity × excitation energy) at 15 °C; every species alike (0 off)", "Реактивный урон: шанс за тик, что молекула в теле реагирует с его белками, на единицу её реактивности (среднее сродство атомов × энергия возбуждения) при 15 °C; для всех видов одинаково (0 — выкл)", 0, 0.01, 0.00001);
        F("ReactContact", "Износ", "Reactive damage: a molecule lying in the body's cell reacts this share as often as a held one", "Реактивный урон: молекула, лежащая в клетке тела, реагирует с такой долей частоты удерживаемой", 0, 1, 0.01);
        F("ReactWear", "Износ", "Reactive damage: share of a protein's activity one reaction destroys (the substrate returns to the body)", "Реактивный урон: доля активности белка, которую разрушает одна реакция (субстрат возвращается в тело)", 0, 1, 0.01);
        F("WearHold", "Износ", "Wear: molecule connectivity per e-fold drop of the chance", "Износ: связность молекулы на e-кратное снижение шанса", 0, 30, 0.1);
        F("UvK", "Тело", "Light-induced mutations: per genome byte per tick", "Мутации от света: на байт генома за тик", 0, 0.0001, 0.000001);
        F("HeatToTemp", "Тело", "°C of cell heating per unit of dissipated energy", "°C нагрева клетки на единицу рассеянной энергии", 0, 2, 0.01);
        F("GrowMass", "Тело", "Mass for the body's second cell", "Масса для второй клетки тела", 1, 1000, 1);
        F("GrowPow", "Тело", "Mass growth for each further cell (power)", "Рост массы на каждую следующую клетку (степень)", 0.5, 4, 0.05);
        I("InvPerCell", "Тело", "Comfortable molecules per body cell", "Удобно молекул на клетку тела", 1, 1024, 1);
        F("CostCell", "Тело", "Each extra body cell per tick", "Каждая лишняя клетка тела за тик", 0, 0.1, 0.0005);
        // Volume
        F("VoxelSpace", "Объём", "Voxel volume: a full block and the room above the floor", "Объём вокселя: полный блок и место над полом", 100, 10000, 50, ParamEffect.MatCap | ParamEffect.Strength);
        F("LooseBulk", "Объём", "How many times loose remains are bulkier than packed ones", "Во сколько раз рыхлые останки объёмнее упакованных", 0.5, 10, 0.1);
        F("CompactShare", "Объём", "Floor share above which remains flow or get pressed", "Доля пола, сверх которой останки стекают или прессуются", 0.05, 1, 0.01);
        // Energy
        F("EnergyK", "Энергия", "Bond energy → body energy", "Энергия связи → энергия тела", 0, 5, 0.05);
        F("HeatShare", "Энергия", "Share of reaction energy that goes to body heat", "Доля энергии реакции, уходящая в тепло тела", 0, 0.9, 0.01);
        I("MatterEnergy", "Энергия", "Where a body's energy is: 1 — in its matter (the excitation of the molecules it holds pays every cost by relaxing them; reactions in the body excite its ground molecules; molecules carry it when eaten, shared or left in remains), 0 — a number in the body, as before",
          "Где энергия тела: 1 — в его веществе (возбуждение молекул тела оплачивает каждую трату их релаксацией; реакции в теле возбуждают его основные молекулы; молекулы уносят её, когда их съедают, отдают или оставляют в останках), 0 — число в теле, как раньше", 0, 1, 1);
        F("EnergyReserve", "Энергия", "Energy a body keeps in hand: an act that would leave it less is not done", "Энергия, которую тело держит про запас: действие, после которого останется меньше, не делается", 0, 20, 0.1);
        F("CaptureHeat", "Энергия", "MatterEnergy 1: share of the energy of an exothermic reaction in a body that warms it instead of exciting its molecules", "MatterEnergy 1: доля энергии экзотермической реакции в теле, которая греет его, а не возбуждает его молекулы", 0, 1, 0.01);
        // Reproduction
        F("DivMinEnergy", "Размножение", "Energy needed for division", "Энергия, нужная для деления", 0, 200, 0.5);
        F("DivCostBase", "Размножение", "Division: base", "Деление: основа", 0, 50, 0.1);
        F("DivCostByte", "Размножение", "Division: per genome byte", "Деление: за байт генома", 0, 1, 0.005);
        I("DivMinBody", "Размножение", "Molecules in the body for division", "Молекул в теле для деления", 2, 256, 1);
        F("MateMinEnergy", "Размножение", "Energy needed for mating", "Энергия, нужная для спаривания", 0, 200, 0.5);
        // Links, motion
        F("LinkFlow", "Связи", "Energy equalization over a link per tick", "Выравнивание энергии по связи за тик", 0, 0.5, 0.005);
        F("Recoil", "Движение", "Recoil from expelled mass", "Отдача от выброшенной массы", 0, 5, 0.05);
        F("Friction", "Движение", "Velocity kept per tick on land", "Скорость, сохраняемая за тик на суше", 0, 1, 0.01);
        // Environment
        I("InitialPop", "Среда", "Random genomes at the start of a world", "Случайных геномов в начале мира", 0, 50000, 100, live: false);
        F("AbioChance", "Среда", "Chance of abiogenesis per tick, legacy law (AbioModel 0; raised when bodies are few)", "Шанс самозарождения за тик, прежний закон (AbioModel 0; выше, когда тел мало)", 0, 0.1, 0.0005);
        I("AbioModel", "Среда", "Abiogenesis: 1 from local chemistry (chance per cell by the energy its loose matter releases, temperature and wetness; no count of bodies), 0 the legacy global chance", "Самозарождение: 1 — из местной химии (шанс клетки по энергии её рыхлого вещества, температуре и влажности; без счёта тел), 0 — прежний общий шанс", 0, 1, 1);
        F("AbioCellRate", "Среда", "Abiogenesis (AbioModel 1): chance per cell per tick at full readiness (loose matter able to release SpawnEnergy), 15 °C and the best wetness", "Самозарождение (AbioModel 1): шанс на клетку за тик при полной готовности (рыхлое вещество может дать SpawnEnergy), 15 °C и лучшей влажности", 0, 0.001, 0.0000005);
        I("SpawnBody", "Среда", "Molecules in an abiogenic body", "Молекул в теле самозарождённого", 1, 64, 1);
        F("SpawnEnergy", "Среда", "Energy of an abiogenic body (from local reactions)", "Энергия самозарождённого (из местных реакций)", 0, 200, 1);
        F("InitLitter", "Среда", "Primordial remains: share of the top block's composition", "Первичные останки: доля состава верхнего блока", 0, 10, 0.1, live: false);
        F("ReliefScale", "Среда", "Relief: how many times the valleys-to-mountains rise (~28 levels at 1) is stretched in a new world", "Рельеф: во сколько раз растянут перепад от долин до гор (~28 уровней при 1) в новом мире", 0.25, 5, 0.25, live: false);
        I("VentCount", "Среда", "How many volcanoes the world keeps", "Сколько вулканов держит мир", 0, 32, 1);
        // Geochemistry (read when a world is made)
        I("GeoProfile", "Геохимия", "Element profile by depth: strata, veins and volcanic output (1 on, 0 strata by strength only, as before)", "Профиль элементов по глубине: пласты, жилы и выбросы вулканов (1 — вкл, 0 — пласты только по прочности, как раньше)", 0, 1, 1, live: false);
        F("DepthScale", "Геохимия", "Depth levels per e-fold shift of stratum composition (at DepthBias = 1)", "Уровней глубины на e-кратный сдвиг состава пласта (при DepthBias = 1)", 0.5, 100, 0.5, live: false);
        F("DepthMid", "Геохимия", "Depth below the column top where the profile is neutral: above, surface elements; below, deep ones", "Глубина под верхом столба, где профиль нейтрален: выше — поверхностные элементы, ниже — глубинные", 0, 60, 1, live: false);
        F("DeepElementMin", "Геохимия", "Smallest DepthBias of the world's deepest element (a generator guarantee)", "Наименьший DepthBias самого глубинного элемента мира (гарантия генератора)", 0, 1, 0.05, live: false);
        F("VeinThreshold", "Геохимия", "Coherent-field threshold for veins (higher means narrower, rarer veins)", "Порог связного поля для жил (выше — жилы уже и реже)", 0.5, 1, 0.01, live: false);
        F("VeinGain", "Геохимия", "How many e-fold a vein is enriched in the deep element", "Во сколько e-раз жила обогащена глубинным элементом", 0, 20, 0.5, live: false);
        I("ChemEnergyModel", "Геохимия", "Molecule energies: 1 from composition and bonds (Pauling-like bond energies; stable compounds are low), 0 drawn at random as before", "Энергии молекул: 1 — из состава и связей (энергии связей по Полингу; устойчивые соединения — низкие), 0 — случайные, как раньше", 0, 1, 1, live: false);
        F("ChemIonicK", "Геохимия", "Energy of a bond between unlike atoms below their like bonds, per (difference of affinities)²; an unreachable atom costs this × affinity × valence / 2", "Энергия связи разных атомов ниже их связей с подобными, на (разность сродств)²; атом, до которого не дотянулась ни одна связь, стоит столько × сродство × валентность / 2", 0.5, 30, 0.5, live: false);
        F("ChemExciteK", "Геохимия", "Excitation a photon brings to a molecule: this × √(mean affinity of its atoms)", "Возбуждение, которое фотон даёт молекуле: столько × √(среднее сродство её атомов)", 0.5, 30, 0.5, live: false);
        // Climate
        F("TEquator", "Климат", "Equator temperature, °C", "Температура экватора, °C", -50, 80, 0.5);
        F("TPole", "Климат", "Pole temperature, °C", "Температура полюса, °C", -80, 50, 0.5);
        F("TDay", "Климат", "Daily range from light, °C", "Суточный размах от света, °C", 0, 50, 0.5);
        F("TLapse", "Климат", "Cooling per level of altitude at relief scale 1, °C (divided by the world's ReliefScale)", "Похолодание на уровень высоты при рельефе ×1, °C (делится на ReliefScale мира)", 0, 5, 0.01);
        F("TRelax", "Климат", "Rate at which land approaches the climate (per environment step)", "Скорость, с которой суша идёт к климату (за шаг среды)", 0, 0.5, 0.001);
        F("TRelaxWater", "Климат", "The same for water", "То же для воды", 0, 0.5, 0.0005);
        I("CaveClimate", "Климат", "Cave climate: rock above a body softens daily and seasonal swings (1 on, 0 surface temperature everywhere)", "Климат пещер: порода над телом смягчает суточные и сезонные качели (1 — вкл, 0 — везде температура поверхности)", 0, 1, 1);
        F("GeoGrad", "Климат", "Warming per level of depth below the column top, °C (geothermal heat)", "Потепление на уровень глубины под верхом столба, °C (тепло недр)", 0, 2, 0.01);
        F("CaveDepthK", "Климат", "Roof blocks per e-fold shelter: cave climate share 1 − e^(−roof/K)", "Блоков крыши на e-кратное укрытие: доля пещерного климата 1 − e^(−крыша/K)", 0.1, 50, 0.1);
        I("TmeanTau", "Климат", "Time constant of a cell's moving mean temperature (deep climate), ticks", "Постоянная скользящей средней температуры клетки (климат глубины), тиков", 100, 1000000, 100);
        I("ClimateCycles", "Климат", "Non-stationary climate: tilt, eccentricity and solar cycles, ice ages, volcanic winters (1 on, 0 stationary climate; player catastrophes work either way)", "Нестационарный климат: циклы наклона оси, эксцентриситета и солнца, ледниковья, вулканические зимы (1 — вкл, 0 — климат стационарен; катастрофы игрока работают и так)", 0, 1, 1);
        F("TiltAmp", "Климат", "Amplitude of axial tilt oscillation, ± rad around Tilt", "Размах колебания наклона оси, ± рад вокруг Tilt", 0, 0.5, 0.005);
        F("TiltPeriod", "Климат", "Period of axial tilt oscillation, days (0 from seed, 40…120)", "Период колебания наклона оси, суток (0 — из seed, 40…120)", 0, 2000, 1);
        F("EccAmp", "Климат", "Largest orbital eccentricity: sun at perihelion × 1 + 2e", "Наибольший эксцентриситет орбиты: солнце в перигелии × 1 + 2e", 0, 0.3, 0.005);
        F("EccPeriod", "Климат", "Eccentricity period (circle to EccAmp and back), days (0 from seed, 100…300)", "Период эксцентриситета (от круга до EccAmp и обратно), суток (0 — из seed, 100…300)", 0, 5000, 1);
        F("PrecPeriod", "Климат", "Precession period: perihelion goes around the year in this many days (0 from seed, 20…50)", "Период прецессии: перигелий обходит год за столько суток (0 — из seed, 20…50)", 0, 2000, 1);
        F("SunDriftAmp", "Климат", "Slow drift of solar luminosity, ± share", "Медленный дрейф светимости солнца, ± доля", 0, 0.3, 0.005);
        F("SunDriftPeriod", "Климат", "Period of solar luminosity drift, days (0 from seed, 200…600)", "Период дрейфа светимости солнца, суток (0 — из seed, 200…600)", 0, 10000, 1);
        F("ClimSens", "Климат", "Climate sensitivity: °C per unit relative change of sunlight (sun, eccentricity, ash)", "Чувствительность климата: °C на единицу относительного изменения солнечного света (солнце, эксцентриситет, пепел)", 0, 100, 0.5);
        F("IceAgeThreshold", "Климат", "Ice age threshold: summer insolation at 65° below this share of normal makes glaciers grow", "Порог ледниковья: летняя инсоляция на 65° ниже этой доли нормы — ледники растут", 0.5, 1.2, 0.005);
        F("IceAgeDT", "Климат", "Cooling in a full ice age, °C (at the pole; 40% at the equator)", "Похолодание в полном ледниковье, °C (у полюса, у экватора — 40%)", 0, 40, 0.5);
        F("IceAgeTau", "Климат", "Time constant of glacier advance and retreat, days", "Постоянная роста и отступания ледников, суток", 0.1, 200, 0.1);
        F("MegaEruptionRate", "Климат", "Mega-eruptions per day (volcanic winter)", "Мегаизвержений в сутки (вулканическая зима)", 0, 1, 0.001);
        I("MegaEruptionBlocks", "Климат", "Blocks of deep matter per mega-eruption (counted as volcanic output)", "Блоков вещества недр за мегаизвержение (учитываются как выброс вулкана)", 0, 2000, 10);
        F("MegaAsh", "Климат", "Mega-eruption ash: mean optical depth over the planet once it has spread", "Пепел мегаизвержения: средняя оптическая толщина над планетой, когда он разошёлся", 0, 5, 0.01);
        F("AshTau", "Климат", "Settling time of stratospheric ash, days (e-fold)", "Время оседания пепла в стратосфере, суток (e-кратно)", 0.1, 500, 0.1);
        F("AshSpreadDays", "Климат", "Days for ash to spread from the eruption latitude to the poles", "За сколько суток пепел расходится от широты извержения до полюсов", 0.1, 100, 0.1);
        // Water
        F("SeaShare", "Вода", "Share of land under water at the start", "Доля суши под водой в начале", 0, 0.9, 0.01, live: false);
        F("SwimDepth", "Вода", "Deeper than this, a body is underwater", "Глубже — тело под водой", 0.1, 5, 0.05);
        F("WaterDensity", "Вода", "Water density (heavier bodies sink)", "Плотность воды (тела тяжелее тонут)", 0.1, 3, 0.01);
        F("GasExpand", "Вода", "How many times bulkier gas is in a body (bladder)", "Во сколько раз газ в теле объёмнее (пузырь)", 1, 50, 0.5, ParamEffect.BodyVolume);
        F("Buoyancy", "Вода", "Buoyancy force", "Сила плавучести", 0, 1, 0.01);
        F("WaterDrag", "Вода", "Vertical velocity kept per tick", "Вертикальная скорость, сохраняемая за тик", 0, 0.99, 0.01);
        F("CostSwim", "Вода", "Stroke per unit of mass", "Гребок за единицу массы", 0, 0.05, 0.0005);
        F("DepthK", "Вода", "Extra cost of motion per block of depth", "Удорожание движения на блок глубины", 0, 2, 0.01);
        F("WaterFriction", "Вода", "Velocity kept per tick in water", "Скорость, сохраняемая за тик в воде", 0, 1, 0.01);
        F("WaterDim", "Вода", "Light attenuation per block of water or ice", "Ослабление света на блок воды или льда", 0, 3, 0.01);
        F("Solubility", "Вода", "Share of bottom remains available to a swimmer", "Доля донных останков, доступная пловцу", 0, 1, 0.005);
        F("Evap", "Вода", "Evaporation per environment step at 20 °C", "Испарение за шаг среды при 20 °C", 0, 0.01, 0.0001);
        F("RainShare", "Вода", "Share of air moisture that falls per environment step", "Доля влаги воздуха, выпадающая за шаг среды", 0, 0.5, 0.005);
        I("Currents", "Вода", "Currents: water flowing between columns carries bodies off the bottom with it (1 on, 0 off)", "Течения: вода, текущая между столбами, несёт с собой тела над дном (1 — вкл, 0 — выкл)", 0, 1, 1);
        I("IceFloat", "Вода", "Ice floats: it forms on top of the water and insulates it, the water stays liquid under it (1 on, 0 water freezes through as before)", "Лёд плавает: нарастает поверх воды и укрывает её, под ним вода остаётся жидкой (1 — вкл, 0 — вода промерзает насквозь, как раньше)", 0, 1, 1);
        F("IceInsulation", "Вода", "Ice floats: how deep the cold reaches into open water per step, and how much ice slows freezing as much again, blocks", "Лёд плавает: на сколько блоков холод проникает в открытую воду за шаг и сколько льда вдвое замедляет промерзание, блоков", 0.05, 10, 0.05);
        I("CaveWater", "Вода", "Water enters caves through open faces, floods and drains them (1 on, 0 caves stay dry)", "Вода затекает в пещеры через открытые грани, затапливает их и уходит (1 — вкл, 0 — пещеры сухие)", 0, 1, 1);
        // Resources (World.Resources)
        F("GasDiffK", "Ресурсы", "Air gas diffusion multiplier (1 as before; less makes gas more local, eaten out on the spot)", "Множитель диффузии газа воздуха (1 — как раньше; меньше — газ локальнее, выедается на месте)", 0, 2, 0.01);
        F("CaveGasK", "Ресурсы", "Gas under a roof: roof blocks per e-fold weakening of access to the column's gas (0 off: under a roof, only what lies on the cavity floor)", "Газ под крышей: блоков крыши на e-кратное ослабление доступа к газу столба (0 — выкл: под крышей только лежащее на полу полости)", 0, 50, 0.5);
        F("LeachK", "Ресурсы", "Leaching: share per tick of the lightest loose molecule on the surface that water (standing or rain) carries into the ground under the top block; heavier molecules less, by mobility; out of reach until that block goes (0 off)", "Вымывание: доля самой лёгкой рыхлой молекулы на поверхности, которую вода (стоячая или дождь) уносит за тик в грунт под верхний блок; тяжёлые молекулы — меньше, по подвижности; недоступно, пока блок не уйдёт (0 — выкл)", 0, 0.01, 0.00001);
        F("LooseDecayK", "Ресурсы", "Decay on the ground: chance per environment step (at 15 °C) that a loose molecule with an energy-releasing breakdown splits into its parts", "Распад на земле: шанс за шаг среды (при 15 °C), что рыхлая молекула с экзотермическим распадом распадётся на части", 0, 0.01, 0.00005);
        F("WeatherK", "Ресурсы", "Weathering: chance per environment step (at 15 °C, dry, cohesion 0.9) that the top block of a column loses a molecule to the loose litter; water, rain and weak cohesion speed it up", "Выветривание: шанс за шаг среды (при 15 °C, сухо, связность 0,9), что верхний блок столба теряет молекулу в рыхлое; вода, дождь и слабая связность ускоряют", 0, 0.01, 0.00001);
        // Space
        I("StrikeMin", "Космос", "Fewest ticks between strikes", "Меньше всего тиков между ударами", 1, 200000, 100);
        I("StrikeMax", "Космос", "Most ticks between strikes", "Больше всего тиков между ударами", 1, 200000, 100);
        I("Eclipses", "Космос", "Eclipses: the moon's shadow crosses the day side (1 on, 0 off)", "Затмения: тень спутника проходит по дневной стороне (1 — вкл, 0 — выкл)", 0, 1, 1);
        F("MoonPeriod", "Космос", "Days between new moons (0 from seed, 3…9)", "Суток между новолуниями (0 — из seed, 3…9)", 0, 100, 0.1);
        F("MoonTilt", "Космос", "Moon's orbital inclination, rad (0 from seed, 0.2…0.45): how rare eclipses are", "Наклон орбиты спутника, рад (0 — из seed, 0,2…0,45): реже ли затмения", 0, 1.5, 0.01);
        F("MoonDist", "Космос", "Distance to the moon in planet radii (farther means shorter eclipses)", "Расстояние до спутника в радиусах планеты (дальше — короче затмения)", 1.5, 100, 0.5);
        F("EclipseR", "Космос", "Radius of the eclipse's umbra, cells", "Радиус полной тени затмения, клеток", 1, 80, 1);
        F("EclipseDepth", "Космос", "Share of light in the umbra", "Доля света в полной тени", 0, 1, 0.01);
        I("Flares", "Космос", "Solar flares: irradiation of the lit side (1 on, 0 off; with them off, bench keeps orbital strikes)", "Солнечные вспышки: облучение освещённой стороны (1 — вкл, 0 — выкл; с выключенными в bench остаются удары с орбиты)", 0, 1, 1);
        F("SolarCycle", "Космос", "Solar activity cycle, days (0 from seed, 30…80)", "Цикл активности солнца, суток (0 — из seed, 30…80)", 0, 1000, 1);
        F("SolarLumAmp", "Космос", "Solar luminosity swing with activity, ± share", "Колебание яркости солнца с активностью, ± доля", 0, 0.2, 0.005);
        F("FlareRate", "Космос", "Flares per day at full activity", "Вспышек в сутки при полной активности", 0, 10, 0.05);
        F("FlareLen", "Космос", "Mean flare duration, ticks (from a third to twice)", "Средняя длительность вспышки, тиков (от трети до двух)", 10, 2000, 10);
        F("FlarePowerMin", "Космос", "Power of the weakest flare", "Мощность слабейшей вспышки", 0, 20, 0.1);
        F("FlarePareto", "Космос", "Tail of the flare power distribution (Pareto): lower means strong ones are more frequent", "Хвост распределения мощности (Парето): меньше — чаще сильные", 0.5, 10, 0.1);
        F("FlareWaterDim", "Космос", "Flare attenuation per block of water above a body (e-fold)", "Ослабление вспышки на блок воды над телом (e-кратно)", 0, 5, 0.05);
        F("ShieldK", "Космос", "Body shielding: dose × e^(−k·Σ mass·packing of molecules / comfortable volume)", "Экран тела: доза × e^(−k·Σ масса·упаковка молекул / удобный объём)", 0, 10, 0.05);
        F("FlareMutK", "Космос", "Flare mutations: per genome byte per tick per unit of dose", "Мутации от вспышки: на байт генома за тик на единицу дозы", 0, 0.001, 0.000005);
        F("FlareProtK", "Космос", "Protein destruction by flares: share per tick per unit of dose", "Разрушение белков вспышкой: доля за тик на единицу дозы", 0, 0.1, 0.0005);
        F("FlareHarmK", "Космос", "Flare damage: energy per tick per unit of dose", "Урон от вспышки: энергия за тик на единицу дозы", 0, 1, 0.005);
        F("FlareHeatK", "Космос", "Flare heating of a body: external energy per tick per unit of dose", "Нагрев тела вспышкой: энергия извне за тик на единицу дозы", 0, 5, 0.05);
        // Chronicle (only what is remembered and shown)
        I("ChronicleCap", "Хроника", "How many ordinary chronicle events to remember (important ones always)", "Сколько обычных событий хроники помнить (важные — всегда)", 100, 200000, 100);
        I("FossilCap", "Хроника", "How many fossils to keep (the least important go first)", "Сколько окаменелостей хранить (лишние — наименее важные)", 10, 20000, 10);
        I("ChronicleTrackKids", "Хроника", "Keep biographies of the children of planted creatures (1 yes)", "Вести биографию детей посаженных существ (1 — да)", 0, 1, 1);
        F("ChronicleCaveDays", "Хроника", "Days under a roof for the \"cave dweller\" event", "Суток под крышей для события «житель пещер»", 0.1, 50, 0.1);
        I("ProgressEvery", "Хроника", "Ticks between samples of evolutionary progress (neutral shadow, lineage, tracks)", "Тиков между замерами хода эволюции (нейтральная тень, родословная, дорожки)", 10, 100000, 10);
        I("ProgressWindow", "Хроника", "Window of the evolutionary progress summary, ticks (track trends over this span)", "Окно сводной подсказки хода эволюции, тиков (тренды дорожек за это время)", 1000, 1000000, 1000);

        var missing = typeof(P).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => !f.IsLiteral && !f.IsInitOnly && !byName.ContainsKey(f.Name)).Select(f => f.Name).ToList();
        if (missing.Count > 0) throw new InvalidOperationException("P fields without registry entries: " + string.Join(", ", missing));
        var untitled = all.Select(p => p.Group).Distinct().Where(g => !groupEn.ContainsKey(g)).ToList();
        if (untitled.Count > 0) throw new InvalidOperationException("law groups without an English name: " + string.Join(", ", untitled));
        Groups = all.Select(p => p.Group).Distinct().ToList();
        Publish();
    }

    static void F(string name, string group, string en, string ru, double min, double max, double step, ParamEffect effect = ParamEffect.None, bool live = true) =>
        Add(name, group, en, ru, min, max, step, false, effect, live);
    static void I(string name, string group, string en, string ru, double min, double max, double step, bool live = true) =>
        Add(name, group, en, ru, min, max, step, true, ParamEffect.None, live);

    static void Add(string name, string group, string en, string ru, double min, double max, double step, bool isInt, ParamEffect effect, bool live)
    {
        var f = typeof(P).GetField(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"P has no field {name}");
        if (f.IsLiteral || f.IsInitOnly) throw new InvalidOperationException($"P.{name} is not tunable");
        if ((f.FieldType == typeof(int)) != isInt) throw new InvalidOperationException($"P.{name}: wrong type in the registry");
        var p = new ParamInfo
        {
            Name = name, Group = group, DescriptionEn = en, DescriptionRu = ru, Min = min, Max = max, Step = step, IsInt = isInt,
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
