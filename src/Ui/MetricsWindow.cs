using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;

namespace Primordium;

// F12: the evolution metrics over time (ROADMAP 7, 8). The `evo:` columns of the bench (EvoMetrics),
// sampled by the simulation thread every few hundred ticks for this session (SimObserver.Metrics), and
// the course-of-evolution columns kept with the world (World.Progress, as in F10). Pick the series on
// the left; each gets its own strip on a shared time axis, scaled to its own range in the visible window
// (its number is on the right); hover for values and the chronicle's events; CSV exports the whole table.
public partial class MetricsWindow : UiWindow
{
    public sealed class Def { public int Source; public string Col, Label, Help, Fmt = "0.##"; }   // Source 0: EvoMetrics (session), 1: World.Progress (saved)

    static Def M(string col, string en, string ru, string helpEn, string helpRu, string fmt = "0.##") =>
        new() { Source = 0, Col = col, Label = Loc.T(en, ru), Help = Loc.T(helpEn, helpRu), Fmt = fmt };

    // The groups of the list (built in the language of the moment: the window is rebuilt on a switch).
    static (string title, Def[] defs)[] Groups() => new[]
    {
        (Loc.T("Population and diversity", "Население и разнообразие"), new[]
        {
            M("pop", "bodies", "особей", "living bodies", "живых тел", "0"),
            M("genomes", "genomes", "геномов", "distinct genomes among the living", "разных геномов среди живых", "0"),
            M("kin_clusters", "kin clusters", "кластеров родства", "groups of genome fingerprints within 10 bits of 64 — how many kinds a kin-recognising body could tell apart",
              "группы отпечатков генома в пределах 10 бит из 64 — сколько «видов» различило бы тело, узнающее родню", "0"),
            M("lineages", "lineages", "линий", "founders still represented among the living (Agent.Lineage)", "основатели, у которых есть живые потомки (Agent.Lineage)", "0"),
            M("lineage_entropy", "lineage entropy", "энтропия линий", "H = −Σ p ln p over the shares of lineages", "H = −Σ p ln p по долям линий", "0.00"),
            M("lineages_eff", "effective lineages", "эффективных линий", "e^H: as many equally large lineages would give the same entropy", "e^H: столько равных линий дали бы ту же энтропию", "0.0"),
            M("dom_share", "dominant share", "доля доминанта", "share of the largest lineage", "доля крупнейшей линии", "0%"),
            M("dom_age", "dominant age", "возраст доминанта", "ticks since the largest lineage was first seen", "тиков с тех пор, как крупнейшую линию увидели впервые", "0"),
            M("lineages_lost", "lineages lost", "линий потеряно", "lineages seen at the previous sample and gone now", "линии, что были в прошлом замере и исчезли", "0"),
            M("new_lineages", "new lineages", "новых линий", "lineages not seen before", "линии, которых раньше не было", "0"),
        }),
        (Loc.T("Generations and code", "Поколения и код"), new[]
        {
            M("gen_mean", "generation, mean", "поколение, среднее", "mean generation of the living", "среднее поколение живых", "0.0"),
            M("gen_max", "generation, max", "поколение, макс.", "deepest generation alive", "самое глубокое живое поколение", "0"),
            M("genome_len", "genome length", "длина генома", "mean genome length, bytes", "средняя длина генома, байт", "0"),
            M("used_code", "used code", "используемый код", "mean share of genome bytes with protection > 20 (their instruction or protein did something)",
              "средняя доля байтов генома с защитой > 20 (их команда или белок что-то делали)", "0.0%"),
        }),
        (Loc.T("Proteins", "Белки"), new[]
        {
            M("specs", "protein specs", "видов белков", "distinct protein specs (kind, targets) held with amount ≥ 0.5", "разных белков (тип, мишени) в количестве ≥ 0,5", "0"),
            M("useful_specs", "useful specs", "полезных белков", "specs that name a reaction that exists", "белки, называющие существующую реакцию", "0"),
            M("bind_share", "with bind", "со связыванием", "share of bodies holding a bind protein", "доля тел с белком связывания", "0%"),
            M("split_share", "with split", "с расщеплением", "share of bodies holding a split protein", "доля тел с белком расщепления", "0%"),
            M("photo_share", "with photo", "со светом", "share of bodies holding a light-catching protein", "доля тел с белком света", "0%"),
            M("motor_share", "with motor", "с мотором", "share of bodies holding a motor protein", "доля тел с мотором", "0%"),
        }),
        (Loc.T("Diet", "Питание"), new[]
        {
            M("diet_plant", "light", "свет", "share living on light (by what fed them lately)", "доля живущих светом (по тому, что кормило их в последнее время)", "0%"),
            M("diet_eater", "chemistry", "химия", "share living on chemistry", "доля живущих химией", "0%"),
            M("diet_miner", "soil", "почва", "share eating soil", "доля едящих почву", "0%"),
            M("diet_hunter", "hunting", "охота", "share of hunters", "доля охотников", "0%"),
            M("diet_idle", "idle", "бездействие", "share nearly idle", "доля почти бездействующих", "0%"),
        }),
        (Loc.T("Novelty", "Новизна"), new[]
        {
            M("new_specs", "new useful specs", "новых полезных белков", "useful specs never seen at an earlier sample", "полезные белки, которых не было ни в одном прежнем замере", "0"),
            M("specs_ever", "specs ever", "белков за всё время", "useful specs seen in all", "полезных белков, виденных за всё время", "0"),
            M("firsts", "materials broken", "материалов вскрыто", "materials first broken with a protein's help (World.Firsts)", "материалы, впервые разрушенные с помощью белка (World.Firsts)", "0"),
        }),
        (Loc.T("Depth and mining", "Глубина и добыча"), new[]
        {
            M("roofed_share", "under a roof", "под крышей", "share of bodies with at least one solid block above them (voids skipped)", "доля тел, над которыми есть хотя бы один твёрдый блок (пустоты не считаются)", "0.0%"),
            M("body_depth_mean", "depth, mean", "глубина, средняя", "mean levels below the top of the body's column, over all bodies (0 on the surface)", "среднее число уровней ниже верха столба тела, по всем телам (0 на поверхности)", "0.00"),
            M("body_depth_p90", "depth, 90th percentile", "глубина, 90-й процентиль", "nine bodies in ten live at most this many levels below their column's top", "девять тел из десяти живут не глубже стольких уровней под верхом столба", "0"),
            M("body_depth_max", "depth, max", "глубина, макс.", "the deepest body, levels below its column's top", "самое глубокое тело, уровней под верхом столба", "0"),
            M("depth_levels_eff", "depth levels", "уровней глубины", "e^H over the occupied depth levels: 1 — everybody at one depth, more — spread over levels",
              "e^H по занятым уровням глубины: 1 — все на одной глубине, больше — распределены по уровням", "0.00"),
            M("mine_depth", "mining depth", "глубина добычи", "mean depth below the column's top of the molecules torn out of rock since the previous sample",
              "средняя глубина под верхом столба молекул, вырванных из породы с прошлого замера", "0.00"),
            M("mined_n", "molecules mined", "молекул добыто", "molecules torn out of rock since the previous sample", "молекул вырвано из породы с прошлого замера", "0"),
        }),
        (Loc.T("Regions (32×32)", "Регионы (32×32)"), new[]
        {
            M("pop_moran", "population clumping", "скученность населения", "Moran's I of bodies per 32×32 region (≈ −0.03 random, > 0 neighbouring regions alike, < 0 checkerboard)",
              "индекс Морана числа тел по регионам 32×32 (≈ −0,03 случайно, > 0 соседние регионы похожи, < 0 шахматка)", "0.00"),
            M("pop_regions_eff", "regions held", "занятых регионов", "e^H of the bodies over the 40 regions: how many regions hold them, effectively", "e^H тел по 40 регионам: сколько регионов их держит, эффективно", "0.0"),
            M("diet_moran", "diet clumping", "скученность питания", "Moran's I of each diet's share among populated regions (≥ 5 bodies), weighted by the diet's share",
              "индекс Морана доли каждого питания по населённым регионам (≥ 5 тел), с весом по доле питания", "0.00"),
            M("diet_beta_rel", "diets by region", "питание по регионам", "share of the diet entropy explained by the region: 0 — every region eats the same mix, 1 — one diet per region",
              "доля энтропии питания, объяснённая регионом: 0 — везде одна смесь, 1 — в каждом регионе одно питание", "0.000"),
        }),
        (Loc.T("Diet cycles", "Циклы питания"), new[]
        {
            M("osc_score", "lead–lag strength", "сила запаздывания", "largest S = corr(Δa(t), Δb(t+k)) − corr(Δb(t), Δa(t+k)) over pairs of diets and lags (last ≤ 48 samples): a's changes followed by b's, b's by the opposite of a's",
              "наибольшее S = corr(Δa(t), Δb(t+k)) − corr(Δb(t), Δa(t+k)) по парам питаний и лагам (последние ≤ 48 замеров): за изменением a следует b, за b — обратное a", "0.00"),
            M("osc_p", "lead–lag p", "запаздывание, p", "share of shuffled surrogates reaching that strength: small — above noise (lenient: bursts of booms and crashes also pass)",
              "доля перемешанных суррогатов с такой же силой: мало — выше шума (мягкий: всплески бумов и крахов тоже проходят)", "0.000"),
            M("osc_lag", "lag, ticks", "лаг, тиков", "the lag of the strongest pair", "лаг сильнейшей пары", "0"),
            M("osc_lead", "leading diet", "ведущее питание", "diet code of the leader (0 idle, 1 light, 2 chemistry, 3 soil, 4 hunting)", "код ведущего питания (0 бездействие, 1 свет, 2 химия, 3 почва, 4 охота)", "0"),
            M("osc_follow", "following diet", "ведомое питание", "diet code of the follower (0 idle, 1 light, 2 chemistry, 3 soil, 4 hunting)", "код ведомого питания (0 бездействие, 1 свет, 2 химия, 3 почва, 4 охота)", "0"),
            M("hunt_score", "hunters: strength", "охотники: сила", "the same strength, only pairs with the hunters", "та же сила, только пары с охотниками", "0.00"),
            M("hunt_p", "hunters: p", "охотники: p", "surrogate p of the hunters' pairs", "p суррогатов для пар с охотниками", "0.000"),
            M("osc_p_circ", "lead–lag p, strict", "запаздывание, p строгий", "surrogates shifted circularly (each keeps its own rhythm and bursts): small only if the timing between diets is beyond what each one's own rhythm gives",
              "суррогаты циклическим сдвигом (у каждого свой ритм и всплески): мало, только если согласованность питаний во времени больше, чем дают их собственные ритмы", "0.000"),
            M("hunt_p_circ", "hunters: p, strict", "охотники: p строгий", "the strict p of the hunters' pairs", "строгий p для пар с охотниками", "0.000"),
        }),
        (Loc.T("Course of evolution (kept in the save)", "Ход эволюции (хранится в сейве)"),
            EvolutionHistory.Names.Skip(1).Select(c => new Def { Source = 1, Col = c, Label = c, Help = EvolutionHelp(c), Fmt = c.EndsWith("_share") ? "0.0%" : "0.##" }).ToArray()),
    };

    // Every EvoMetrics column shows up: one not listed above lands in "Other" under its own name.
    static (string title, Def[] defs)[] AllGroups()
    {
        var groups = Groups().ToList();
        var listed = new HashSet<string>(groups.SelectMany(g => g.defs).Where(d => d.Source == 0).Select(d => d.Col));
        var rest = EvoMetrics.Names.Where(c => !listed.Contains(c)).Select(c => M(c, c, c, "EvoMetrics: " + c, "EvoMetrics: " + c)).ToArray();
        if (rest.Length > 0) groups.Insert(groups.Count - 1, (Loc.T("Other", "Прочее"), rest));
        return groups.ToArray();
    }

    static string EvolutionHelp(string col)
    {
        foreach (var t in EvolutionWindow.Tracks)
            foreach (var s in t.Series)
                if (s.Col == col) return $"{t.Title}: {s.Label}. {t.Help}";
        return Loc.T("a column of the course of evolution (F10; see docs/SIMULATION.md)", "колонка хода эволюции (F10; см. docs/SIMULATION.md)");
    }

    public const int MaxShown = 8;
    static readonly HashSet<string> chosen = new() { "0:genomes", "0:lineages_eff", "0:useful_specs", "0:used_code", "0:gen_mean", "0:new_specs" };   // survives a language switch
    static string Key(Def d) => d.Source + ":" + d.Col;

    static readonly long[] Ranges = { 0, 5000, 20000, 100000 };
    readonly List<Def> all = new();
    readonly Dictionary<string, CheckBox> boxes = new();
    OptionButton range;
    MetricsChart chart;
    Label status;
    long shownVersion = -1, shownEvo = -1;

    public MetricsWindow() : base("metrics", Loc.T("Metrics", "Метрики"), new Vector2(1040, 680))
    {
        MinSize = new Vector2(700, 420);
        range = UiKit.Options(Loc.T("all time", "всё время"), Loc.T("last 5 000 ticks", "последние 5 000 тиков"), Loc.T("last 20 000", "последние 20 000"), Loc.T("last 100 000", "последние 100 000"));
        range.ItemSelected += _ => chart.QueueRedraw();
        var clear = UiKit.Button(Loc.T("clear", "сбросить"), () => { chosen.Clear(); foreach (var b in boxes.Values) b.SetPressedNoSignal(false); Changed(); },
            Loc.T("uncheck every series", "снять все ряды"));
        var csv = UiKit.Button("CSV", Export, Loc.T("save the whole table of this session's metrics to user://metrics/", "сохранить всю таблицу метрик этого сеанса в user://metrics/"));
        var hint = UiKit.Text(Loc.T($"up to {MaxShown} series · hover for values", $"до {MaxShown} рядов · наведите — значения"), 12, UiKit.Dim);
        hint.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Body.AddChild(UiKit.Row(10, hint, range, clear, csv));

        var list = UiKit.Col(2);
        foreach (var (title, defs) in AllGroups())
        {
            list.AddChild(UiKit.Text(title, 12, UiKit.Acc, UiKit.Bold));
            foreach (var d in defs)
            {
                all.Add(d);
                var box = UiKit.Check(d.Label, chosen.Contains(Key(d)), on => Toggle(d, on), d.Help);
                box.AddThemeFontSizeOverride("font_size", 12);
                boxes[Key(d)] = box;
                list.AddChild(box);
            }
        }
        var scroll = UiKit.Scroll(list);
        scroll.CustomMinimumSize = new Vector2(230, 300);
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        scroll.SizeFlagsHorizontal = SizeFlags.Fill;   // the list keeps its width, the chart takes the rest
        chart = new MetricsChart { Win = this, SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(420, 320) };
        var split = UiKit.Row(10, scroll, chart);
        split.SizeFlagsVertical = SizeFlags.ExpandFill;
        Body.AddChild(split);
        status = UiKit.Text("", 12, UiKit.Dim, wrap: true);
        Body.AddChild(status);
        Changed();
    }

    public long RangeTicks => Ranges[Math.Clamp(range.Selected, 0, Ranges.Length - 1)];
    public Def[] Shown = Array.Empty<Def>();

    void Toggle(Def d, bool on)
    {
        if (on && chosen.Count >= MaxShown)
        {
            boxes[Key(d)].SetPressedNoSignal(false);
            Ui.Toast(Loc.T($"at most {MaxShown} series at once: uncheck one first", $"не больше {MaxShown} рядов сразу: сначала снимите один"), true);
            return;
        }
        if (on) chosen.Add(Key(d)); else chosen.Remove(Key(d));
        Changed();
    }

    void Changed()
    {
        Shown = all.Where(d => chosen.Contains(Key(d))).ToArray();
        chart?.QueueRedraw();
    }

    public override void ShowView(string view)
    {
        // --open metrics:col1+col2 (EvoMetrics columns) — for screenshots.
        var cols = view.Split('+');
        chosen.Clear();
        foreach (var c in cols) if (all.Any(d => d.Source == 0 && d.Col == c)) chosen.Add("0:" + c);
        foreach (var (k, b) in boxes) b.SetPressedNoSignal(chosen.Contains(k));
        Changed();
    }

    protected override void OnOpen() => shownVersion = -1;

    public override void _Process(double delta)
    {
        if (!Visible || Main.Sim == null) return;
        var m = Main.Sim.Obs.Metrics;
        var e = Main.Sim.Evolution;
        if (m.Version == shownVersion && e.Version == shownEvo) return;
        shownVersion = m.Version; shownEvo = e.Version;
        chart.View = m; chart.Evo = e;
        chart.QueueRedraw();
        status.Text = Loc.T(
            $"session metrics: {m.Rows.Length} samples, one every {m.Every} ticks since tick {m.Since} (not kept in the save: they start again after a load) · " +
            $"course of evolution: {e.Samples.Length} samples kept with the world · F12 to close",
            $"метрики сеанса: {m.Rows.Length} замеров, раз в {m.Every} тиков с тика {m.Since} (в сейв не пишутся: после загрузки начинаются заново) · " +
            $"ход эволюции: {e.Samples.Length} замеров хранится с миром · F12 — закрыть");
    }

    void Export()
    {
        var m = Main.Sim?.Obs.Metrics;
        if (m == null || m.Rows.Length == 0) { Ui.Toast(Loc.T("nothing to save: no samples yet", "нечего сохранять: замеров ещё нет"), true); return; }
        try
        {
            var dir = UiKit.Global("user://metrics");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"metrics_seed{m.Seed}_t{(long)m.Rows[^1][0]}.csv");
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", MetricsView.Columns));
            foreach (var r in m.Rows) sb.AppendLine(string.Join(",", r.Select(x => x.ToString("G9", UiKit.Inv))));
            File.WriteAllText(path, sb.ToString());
            Ui.Toast(Loc.T("saved: ", "сохранено: ") + path);
        }
        catch (Exception x) { Ui.Toast("CSV: " + x.Message, true); }
    }
}

// The strips of the chosen series, drawn like the tracks of the course of evolution (TrackChart).
public partial class MetricsChart : Control
{
    public MetricsWindow Win;
    public MetricsView View = MetricsView.Empty;
    public EvolutionView Evo = EvolutionView.Empty;
    Vector2 mouse = new(-1, -1);
    static readonly Color[] Palette =
    {
        new(0.45f, 0.85f, 1f), new(1f, 0.82f, 0.4f), new(0.5f, 0.95f, 0.55f), new(1f, 0.5f, 0.45f),
        new(0.8f, 0.6f, 1f), new(0.95f, 0.95f, 0.6f), new(0.4f, 0.75f, 0.75f), new(1f, 0.6f, 0.85f),
    };

    public MetricsChart() { MouseFilter = MouseFilterEnum.Stop; ClipContents = true; }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm) { mouse = mm.Position; QueueRedraw(); }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit) { mouse = new Vector2(-1, -1); QueueRedraw(); }
    }

    double[][] RowsOf(MetricsWindow.Def d)
    {
        if (d.Source == 0) return View.Rows;
        var rows = Evo.Samples;
        if (Evo.Latest != null && (rows.Length == 0 || rows[^1][0] < Evo.Latest[0])) rows = rows.Append(Evo.Latest).ToArray();
        return rows;
    }

    static int ColOf(MetricsWindow.Def d) => d.Source == 0 ? MetricsView.Col(d.Col) : EvolutionHistory.Col(d.Col);

    public override void _Draw()
    {
        var font = UiKit.Ui;
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.035f, 0.04f, 0.05f, 1f));
        var shown = Win.Shown;
        if (shown.Length == 0)
        {
            DrawString(font, new Vector2(16, 28), Loc.T("Pick a series on the left.", "Выберите ряд слева."), HorizontalAlignment.Left, -1, 13, UiKit.Dim);
            return;
        }
        // The time span of whatever is shown.
        long first = long.MaxValue, end = long.MinValue;
        foreach (var d in shown)
        {
            var rows = RowsOf(d);
            if (rows.Length == 0) continue;
            first = Math.Min(first, (long)rows[0][0]);
            end = Math.Max(end, (long)rows[^1][0]);
        }
        if (first == long.MaxValue || end <= first)
        {
            DrawString(font, new Vector2(16, 28), Loc.T($"Too few samples yet: at least two are needed (one every {View.Every} ticks).", $"Мало замеров: нужно хотя бы два (раз в {View.Every} тиков)."),
                HorizontalAlignment.Left, -1, 13, UiKit.Dim);
            return;
        }
        long span = Win.RangeTicks, start = span > 0 ? Math.Max(first, end - span) : first;
        if (end <= start) start = end - 1;
        float left = 8, right = size.X - 170, top = 4, bottom = size.Y - 18;
        int n = shown.Length;
        float gap = 5, th = (bottom - top - gap * (n - 1)) / n;
        float X(double tick) => left + (float)((tick - start) / (double)(end - start)) * (right - left);

        for (int k = 0; k <= 5; k++)
        {
            double t = start + (end - start) * k / 5.0;
            float x = X(t);
            DrawLine(new Vector2(x, top), new Vector2(x, bottom), UiKit.Rule, 1);
            string lbl = t >= 10000 ? Loc.T($"{t / 1000:0}k", $"{t / 1000:0} тыс.") : $"{t:0}";
            DrawString(font, new Vector2(Math.Clamp(x - 20, 0, size.X - 60), size.Y - 4), lbl, HorizontalAlignment.Left, -1, 11, UiKit.Dim);
        }

        // The chronicle's important events (not the player's).
        var events = new List<ChronicleEvent>();
        if (Win.Main.Sim != null)
            foreach (var e in Win.Main.Sim.Chronicle.Events)
                if (e.Important && e.Type != EvType.Player && e.Tick >= start && e.Tick <= end) events.Add(e);
        foreach (var e in events)
        {
            float x = X(e.Tick);
            var c = ChronicleWindow.TypeColors[(int)e.Type];
            DrawLine(new Vector2(x, top), new Vector2(x, bottom), c with { A = 0.22f }, 1);
        }

        bool over = mouse.X >= left && mouse.X <= right && mouse.Y >= top && mouse.Y <= bottom;
        double hoverTick = over ? start + (mouse.X - left) / (right - left) * (end - start) : double.NaN;
        TooltipText = "";
        for (int k = 0; k < n; k++)
        {
            var d = shown[k];
            var color = Palette[k % Palette.Length];
            float y0 = top + k * (th + gap), y1 = y0 + th;
            DrawRect(new Rect2(left, y0, right - left, th), new Color(1, 1, 1, 0.025f));
            var all = RowsOf(d);
            int c = ColOf(d);
            var rows = all.Where(r => r[0] >= start && c >= 0 && c < r.Length && double.IsFinite(r[c])).ToArray();   // NaN: undefined at that sample
            DrawString(font, new Vector2(left + 6, y0 + 14), d.Label + (d.Source == 1 ? Loc.T("  (saved)", "  (сейв)") : ""), HorizontalAlignment.Left, -1, 12, UiKit.Fg);
            if (rows.Length == 0)
            {
                DrawString(font, new Vector2(right + 8, y0 + 14), Loc.T("no samples", "нет замеров"), HorizontalAlignment.Left, 160, 11, UiKit.Dim);
                continue;
            }
            double lo = Math.Min(0, rows.Min(r => r[c])), up = rows.Max(r => r[c]);
            if (up - lo < 1e-9) up = lo + 1;
            float Y(double v) => y1 - 3 - (float)((v - lo) / (up - lo)) * (th - 20);
            if (rows.Length >= 2)
            {
                var pts = new Vector2[rows.Length];
                for (int i = 0; i < rows.Length; i++) pts[i] = new Vector2(X(rows[i][0]), Y(rows[i][c]));
                DrawPolyline(pts, color, 1.5f, true);
            }
            else DrawCircle(new Vector2(X(rows[0][0]), Y(rows[0][c])), 2.5f, color);
            // The scale: top and bottom of the strip.
            DrawString(font, new Vector2(right - 60, y0 + 12), up.ToString(d.Fmt, UiKit.Inv), HorizontalAlignment.Right, 56, 10, UiKit.Dim);
            int hi = rows.Length - 1;
            if (over)
            {
                double best = double.MaxValue;
                for (int i = 0; i < rows.Length; i++) { double dd = Math.Abs(rows[i][0] - hoverTick); if (dd < best) { best = dd; hi = i; } }
                DrawCircle(new Vector2(X(rows[hi][0]), Y(rows[hi][c])), 3, color);
            }
            DrawString(font, new Vector2(right + 8, y0 + 14), rows[hi][c].ToString(d.Fmt, UiKit.Inv), HorizontalAlignment.Left, 160, 13, color);
            DrawString(font, new Vector2(right + 8, y0 + 28), Loc.T($"min {rows.Min(r => r[c]).ToString(d.Fmt, UiKit.Inv)} · max {up.ToString(d.Fmt, UiKit.Inv)}",
                                                                    $"мин {rows.Min(r => r[c]).ToString(d.Fmt, UiKit.Inv)} · макс {up.ToString(d.Fmt, UiKit.Inv)}"),
                HorizontalAlignment.Left, 160, 10, UiKit.Dim);
            if (mouse.Y >= y0 && mouse.Y <= y1 && over) TooltipText = d.Help;
        }

        if (over)
        {
            float x = mouse.X;
            DrawLine(new Vector2(x, top), new Vector2(x, bottom), UiKit.Acc with { A = 0.5f }, 1);
            var lines = new List<string> { Loc.T($"tick {hoverTick:0} ({Chronicle.Day((long)hoverTick)})", $"тик {hoverTick:0} ({Chronicle.Day((long)hoverTick)})") };
            foreach (var e in events)
                if (Math.Abs(X(e.Tick) - mouse.X) < 4) lines.Add($"{Chronicle.TypeNames[(int)e.Type]}: {e.Shown}");
            float w = 0;
            foreach (var l in lines) w = Math.Max(w, font.GetStringSize(l, HorizontalAlignment.Left, -1, 11).X);
            w = Math.Min(w + 14, size.X - 20);
            float bx = Math.Clamp(mouse.X + 12, 4, size.X - w - 4), by = Math.Clamp(mouse.Y + 12, 4, size.Y - lines.Count * 14 - 10);
            DrawRect(new Rect2(bx, by, w, lines.Count * 14 + 6), new Color(0.03f, 0.035f, 0.045f, 0.95f));
            for (int i = 0; i < lines.Count; i++)
                DrawString(font, new Vector2(bx + 7, by + 15 + i * 14), lines[i], HorizontalAlignment.Left, w - 10, 11, i == 0 ? UiKit.Acc : UiKit.Fg);
        }
    }
}
