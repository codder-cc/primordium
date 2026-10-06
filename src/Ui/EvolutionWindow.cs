using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;

namespace Primordium;

// F10: the course of evolution (World.Progress, published by SimRunner as an EvolutionView). Stacked
// tracks on one time axis — novelty against the neutral shadow, complexity, ecology, tempo, phylogeny —
// with the chronicle's important events over them, a hover readout, a time range, CSV export and the
// summary hint (labelled as a hint: the tracks are what to read).
public partial class EvolutionWindow : UiWindow
{
    // A track: its title and series (column, label, colour, how to read it: value, or a rate per 10 000 ticks of a cumulative column).
    public sealed class Series { public string Col, Label; public Color Color; public bool Rate; public string Fmt = "0.##"; }
    public sealed class Track { public string Title, Help; public Series[] Series; }

    static Series S(string col, string label, Color c, bool rate = false, string fmt = "0.##") => new() { Col = col, Label = label, Color = c, Rate = rate, Fmt = fmt };
    static readonly Color C1 = new(0.45f, 0.85f, 1f), C2 = new(1f, 0.82f, 0.4f), C3 = new(0.5f, 0.95f, 0.55f), C4 = new(1f, 0.5f, 0.45f), C5 = new(0.8f, 0.6f, 1f);

    public static readonly Track[] Tracks =
    {
        new() { Title = "Новизна", Help = "Компоненты (белки и мотивы используемого кода), чья активность (носители × время) выше 95-го перцентиля нейтральной тени. Мир против контрольной тени: разница — новизна сверх случайного.",
            Series = new[] { S("adaptive", "мир", C1, fmt: "0"), S("adaptive_shadow", "тень", UiKit.Dim, fmt: "0"), S("novelty", "новизна", C2, fmt: "0") } },
        new() { Title = "Сложность", Help = "Используемый код (байтов с защитой > 0 на тело), разных белков и реакций на тело, масса.",
            Series = new[] { S("code_used", "код, байт", C1, fmt: "0.0"), S("body_proteins", "белков", C3), S("body_reactions", "реакций", C2), S("body_mass", "масса", C5, fmt: "0") } },
        new() { Title = "Экология", Help = "Ниши — сочетания питания × глубины (поверхность, неглубоко, глубоко, вода) × региона 32×32 с ≥ 5 телами; разнообразие питания e^H; линии ≥ 10 тел; доли под землёй и в воде.",
            Series = new[] { S("niches", "ниш", C1, fmt: "0"), S("diet_div", "питаний", C2), S("lineages_big", "линий ≥10", C3, fmt: "0"), S("under_share", "под землёй", C5, fmt: "0.0%"), S("water_share", "в воде", C4, fmt: "0%") } },
        new() { Title = "Темп", Help = "Смены крупнейшей линии, видообразования и вымирания хроники — на 10 000 тиков; сдвиг отпечатка самого частого генома между замерами (бит из 64).",
            Series = new[] { S("dom_changes", "смен доминанта", C2, true), S("speciations", "видообр.", C3, true), S("extinctions", "вымираний", C4, true), S("dom_drift", "дрейф, бит", C1, fmt: "0") } },
        new() { Title = "Филогения", Help = "Возраст MRCA крупнейшей линии (тиков), среднее попарное расстояние (шагов-мутаций, выборка 128 пар), живых генотипов, длина линии самого частого генотипа (шагов).",
            Series = new[] { S("mrca_age", "возраст MRCA", C1, fmt: "0"), S("pair_dist", "расхождение", C2, fmt: "0.0"), S("phylo_taxa", "генотипов", C3, fmt: "0"), S("dom_depth", "длина линии", C5, fmt: "0") } },
    };

    static readonly long[] Ranges = { 0, 5000, 20000, 100000 };

    Label verdict, status;
    OptionButton range;
    CheckBox marks;
    TrackChart chart;
    long shownVersion = -1;

    public EvolutionWindow() : base("evolution", "Ход эволюции", new Vector2(980, 700))
    {
        MinSize = new Vector2(640, 460);
        verdict = UiKit.Text("", 13, UiKit.Fg, UiKit.Bold);
        verdict.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        verdict.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        verdict.TooltipText = "Медиана нормированных трендов четырёх дорожек (новизна, используемый код, ниши, расхождение) за окно ProgressWindow. " +
                              "Это подсказка, а не истина: читайте сами дорожки.";
        range = UiKit.Options("всё время", "последние 5 000 тиков", "последние 20 000", "последние 100 000");
        range.ItemSelected += _ => chart.QueueRedraw();
        marks = UiKit.Check("события хроники", true, _ => chart.QueueRedraw(), "важные события хроники вертикальными линиями поверх дорожек");
        var csv = UiKit.Button("CSV", Export, "сохранить всю историю замеров в user://evolution/");
        Body.AddChild(UiKit.Row(10, verdict, range, marks, csv));
        chart = new TrackChart { Win = this, SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(560, 340) };
        Body.AddChild(chart);
        status = UiKit.Text("", 12, UiKit.Dim, wrap: true);
        Body.AddChild(status);
    }

    public long RangeTicks => Ranges[Math.Clamp(range.Selected, 0, Ranges.Length - 1)];
    public bool Marks => marks.ButtonPressed;

    protected override void OnOpen() => shownVersion = -1;

    public override void _Process(double delta)
    {
        if (!Visible || Main.Sim == null) return;
        var v = Main.Sim.Evolution;
        if (v.Version == shownVersion) return;
        shownVersion = v.Version;
        chart.View = v;
        chart.QueueRedraw();
        if (v.Latest == null)
        {
            verdict.Text = "Замеров ещё нет (раз в " + P.ProgressEvery + " тиков)";
            status.Text = "";
            return;
        }
        string trend = string.Join(", ", EvolutionHistory.IndexTrackNames.Select((n, k) => $"{n} {(k < v.Trends.Length ? v.Trends[k] : 0):+0.00;−0.00;0}"));
        verdict.Text = $"Подсказка: эволюция {v.Verdict} (индекс {v.Index:+0.00;−0.00;0} за {v.Window / 1000} тыс. тиков: {trend})";
        verdict.AddThemeColorOverride("font_color", v.Verdict == "идёт" ? UiKit.Good : v.Verdict == "откат" ? UiKit.Bad : UiKit.Acc);
        double Col(string n) => v.Latest[EvolutionHistory.Col(n)];
        status.Text = $"замеров {v.Samples.Length} (раз в {v.Every * v.Stride} тиков; новые — раз в {v.Every}) · компонентов в мире {Col("comps"):0}, в тени {Col("comps_shadow"):0}, порог θ {Col("theta"):0} · " +
                      $"узлов родословной {Col("phylo_nodes"):0} (корней {Col("phylo_roots"):0}) · память {v.Mb:0.0} МБ · наведите на дорожку — значения и события · F10 — закрыть";
    }

    void Export()
    {
        var v = Main.Sim?.Evolution;
        if (v == null || v.Samples.Length == 0) { Ui.Toast("нечего сохранять: замеров ещё нет", true); return; }
        try
        {
            var dir = UiKit.Global("user://evolution");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"evolution_seed{v.Seed}_t{v.Tick}.csv");
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", EvolutionHistory.Names));
            var rows = v.Samples.ToList();
            if (v.Latest != null && (rows.Count == 0 || rows[^1][0] < v.Latest[0])) rows.Add(v.Latest);
            foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(x => x.ToString("G9", UiKit.Inv))));
            File.WriteAllText(path, sb.ToString());
            Ui.Toast("сохранено: " + path);
        }
        catch (Exception e) { Ui.Toast("CSV: " + e.Message, true); }
    }
}

// The stacked tracks. Each series is scaled to its own range in the visible window (its numbers are in
// the legend and the hover readout); the time axis is shared.
public partial class TrackChart : Control
{
    public EvolutionWindow Win;
    public EvolutionView View = EvolutionView.Empty;
    Vector2 mouse = new(-1, -1);

    public TrackChart() { MouseFilter = MouseFilterEnum.Stop; ClipContents = true; }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm) { mouse = mm.Position; QueueRedraw(); }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit) { mouse = new Vector2(-1, -1); QueueRedraw(); }
    }

    static double Value(double[][] rows, int i, EvolutionWindow.Series s)
    {
        int c = EvolutionHistory.Col(s.Col);
        if (!s.Rate) return rows[i][c];
        if (i == 0) return 0;
        int j = Math.Max(0, i - 5);   // over the last five intervals: single events would be spikes
        double dt = rows[i][0] - rows[j][0];
        return dt > 0 ? (rows[i][c] - rows[j][c]) / dt * 10000 : 0;
    }

    public override void _Draw()
    {
        var font = UiKit.Ui;
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.035f, 0.04f, 0.05f, 1f));
        var v = View;
        var all = v.Samples.ToList();
        if (v.Latest != null && (all.Count == 0 || all[^1][0] < v.Latest[0])) all.Add(v.Latest);
        if (all.Count < 2)
        {
            DrawString(font, new Vector2(16, 28), "Мало замеров: нужно хотя бы два (раз в " + P.ProgressEvery + " тиков).", HorizontalAlignment.Left, -1, 13, UiKit.Dim);
            return;
        }
        long end = (long)all[^1][0], span = Win.RangeTicks;
        long start = span > 0 ? Math.Max((long)all[0][0], end - span) : (long)all[0][0];
        if (end <= start) start = end - 1;
        var rows = all.Where(r => r[0] >= start).ToArray();
        var allArr = all.ToArray();
        int firstIndex = all.Count - rows.Length;
        float left = 8, right = size.X - 150, top = 4, bottom = size.Y - 18;
        int n = EvolutionWindow.Tracks.Length;
        float gap = 6, th = (bottom - top - gap * (n - 1)) / n;
        float X(double tick) => left + (float)((tick - start) / (double)(end - start)) * (right - left);

        // The time axis.
        for (int k = 0; k <= 5; k++)
        {
            double t = start + (end - start) * k / 5.0;
            float x = X(t);
            DrawLine(new Vector2(x, top), new Vector2(x, bottom), UiKit.Rule, 1);
            string lbl = t >= 10000 ? $"{t / 1000:0} тыс." : $"{t:0}";
            DrawString(font, new Vector2(Math.Clamp(x - 20, 0, size.X - 60), size.Y - 4), lbl, HorizontalAlignment.Left, -1, 11, UiKit.Dim);
        }

        // The chronicle's important events (not the player's).
        var events = new List<ChronicleEvent>();
        if (Win.Marks && Win.Main.Sim != null)
            foreach (var e in Win.Main.Sim.Chronicle.Events)
                if (e.Important && e.Type != EvType.Player && e.Tick >= start && e.Tick <= end) events.Add(e);
        foreach (var e in events)
        {
            float x = X(e.Tick);
            var c = ChronicleWindow.TypeColors[(int)e.Type];
            DrawLine(new Vector2(x, top), new Vector2(x, bottom), c with { A = 0.28f }, 1);
            DrawLine(new Vector2(x, top), new Vector2(x, top + 6), c, 2);
        }

        // Hover: the nearest sample.
        int hi = -1;
        if (mouse.X >= left && mouse.X <= right && mouse.Y >= top && mouse.Y <= bottom)
        {
            double best = double.MaxValue;
            for (int i = 0; i < rows.Length; i++) { double d = Math.Abs(X(rows[i][0]) - mouse.X); if (d < best) { best = d; hi = i; } }
        }

        for (int k = 0; k < n; k++)
        {
            var tr = EvolutionWindow.Tracks[k];
            float y0 = top + k * (th + gap), y1 = y0 + th;
            DrawRect(new Rect2(left, y0, right - left, th), new Color(1, 1, 1, 0.025f));
            DrawString(font, new Vector2(left + 6, y0 + 14), tr.Title, HorizontalAlignment.Left, -1, 12, UiKit.Fg);
            float ly = y0 + 14;
            foreach (var s in tr.Series)
            {
                var vals = new double[rows.Length];
                for (int i = 0; i < rows.Length; i++) vals[i] = Value(allArr, firstIndex + i, s);
                double lo = Math.Min(0, vals.Min()), up = vals.Max();
                if (up - lo < 1e-9) up = lo + 1;
                var pts = new Vector2[rows.Length];
                for (int i = 0; i < rows.Length; i++)
                    pts[i] = new Vector2(X(rows[i][0]), y1 - 3 - (float)((vals[i] - lo) / (up - lo)) * (th - 20));
                DrawPolyline(pts, s.Color, s.Col == "novelty" ? 2f : 1.4f, true);
                double shown = hi >= 0 ? vals[hi] : vals[^1];
                DrawString(font, new Vector2(right + 8, ly), $"{s.Label} {shown.ToString(s.Fmt, UiKit.Inv)}", HorizontalAlignment.Left, 140, 11, s.Color);
                ly += 13;
            }
            if (mouse.Y >= y0 && mouse.Y <= y1 && mouse.X >= left && mouse.X <= right) TooltipText = tr.Help;
        }

        if (hi >= 0)
        {
            float x = X(rows[hi][0]);
            DrawLine(new Vector2(x, top), new Vector2(x, bottom), UiKit.Acc with { A = 0.6f }, 1);
            var lines = new List<string> { $"тик {rows[hi][0]:0} ({Chronicle.Day((long)rows[hi][0])})" };
            foreach (var e in events)
                if (Math.Abs(X(e.Tick) - mouse.X) < 4) lines.Add($"{Chronicle.TypeNames[(int)e.Type]}: {e.Text}");
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
