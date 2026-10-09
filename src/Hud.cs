using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Primordium;

// Overlay: status bar on top, key hints at the bottom, an information panel on the right.
public partial class Hud : Control
{
    public Main Main;
    public const float PanelW = 440;
    public bool Legend;

    Font ui, bold, mono;
    readonly Dictionary<ulong, ImageTexture> portraits = new();
    readonly List<(Rect2 r, Agent a)> hits = new();     // clickable rows in the panel

    public Agent HitTest(Vector2 p)
    {
        foreach (var (r, a) in hits) if (r.HasPoint(p)) return a;
        return null;
    }

    // Other clickable places of the panel: the inspector's tabs and the biography's links.
    readonly List<(Rect2 r, Action act)> clicks = new();
    public int AgentTab;   // 0 overview, 1 biography

    public bool Click(Vector2 p)
    {
        foreach (var (r, act) in clicks)
            if (r.HasPoint(p)) { act(); QueueRedraw(); return true; }
        return false;
    }

    static readonly Color Fg = new(0.93f, 0.94f, 0.97f), Dim = new(0.6f, 0.64f, 0.71f), Acc = new(1f, 0.82f, 0.4f),
        PanelBg = new(0.055f, 0.06f, 0.075f, 0.96f), BarBg = new(0.03f, 0.035f, 0.045f, 0.8f), Rule = new(1, 1, 1, 0.08f);
    static readonly Color CPlant = new(0.4f, 0.9f, 0.35f), CEater = new(0.35f, 0.62f, 1f), CMiner = new(1f, 0.65f, 0.25f), CHunter = new(1f, 0.25f, 0.2f);
    static readonly string[] SeasonsEn = { "spring", "summer", "autumn", "winter" };
    static readonly string[] SeasonsRu = { "весна", "лето", "осень", "зима" };
    static string[] Seasons => Loc.T(SeasonsEn, SeasonsRu);
    static readonly string[] CausesEn = { "", "starvation", "killed", "fell apart", "frost or heat", "cave-in", "the hand (brush)" };
    static readonly string[] CausesRu = { "", "голод", "убит", "распался", "мороз или жара", "обвал", "рука (кисть)" };
    static string[] Causes => Loc.T(CausesEn, CausesRu);
    static readonly (EvKind k, string en, string ru)[] EvShow =
    {
        (EvKind.Divide, "divisions", "деления"), (EvKind.Attack, "attacks", "атаки"), (EvKind.Kill, "kills", "убийства"),
        (EvKind.Take, "thefts", "кражи"), (EvKind.Give, "gifts", "дары"), (EvKind.Share, "energy →", "энергия →"),
        (EvKind.Link, "links", "сцепки"), (EvKind.Inject, "injections", "инъекции"), (EvKind.Cut, "excisions", "вырезания"),
        (EvKind.Dig, "digging", "раскопки"), (EvKind.Pile, "building", "постройки"), (EvKind.Mine, "mining", "добыча"),
    };

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        string[] sans = { "Helvetica Neue", "Helvetica", "Arial" };
        ui = new SystemFont { FontNames = sans };
        bold = new SystemFont { FontNames = sans, FontWeight = 700 };
        mono = new SystemFont { FontNames = new[] { "Menlo", "Monaco", "Courier New" } };
    }

    // Scroll state of the inspector and the band it may draw in.
    public float AgentScroll, AgentScrollMax;
    float clipTop = float.MinValue, clipBot = float.MaxValue;

    public float WorldScroll, WorldScrollMax;
    public void ScrollAgent(float d)
    {
        if (Main.View.Selected != null) AgentScroll = Math.Clamp(AgentScroll + d, 0, AgentScrollMax);
        else WorldScroll = Math.Clamp(WorldScroll + d, 0, WorldScrollMax);
    }

    void T(float x, float y, string s, Color c, int size = 13, Font f = null)
    {
        if (y - size < clipTop || y > clipBot) return;
        DrawString(f ?? ui, new Vector2(x, y), s, HorizontalAlignment.Left, -1, size, c);
    }

    void R(Rect2 r, Color c)
    {
        if (r.Position.Y < clipTop || r.End.Y > clipBot) return;
        DrawRect(r, c);
    }

    float TW(string s, int size = 13, Font f = null) => (f ?? ui).GetStringSize(s, HorizontalAlignment.Left, -1, size).X;

    // The text cut to `width` with an ellipsis (the whole of it if it fits).
    string Clip(string s, float width, int size = 13, Font f = null)
    {
        if (TW(s, size, f) <= width) return s;
        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (TW(s[..mid] + "…", size, f) <= width) lo = mid; else hi = mid - 1;
        }
        return s[..lo].TrimEnd(' ', '·', ',', '(') + "…";
    }

    ImageTexture Portrait(Agent a)
    {
        if (portraits.Count > 300) portraits.Clear();
        if (!portraits.TryGetValue(a.Hash, out var t))
            portraits[a.Hash] = t = ImageTexture.CreateFromImage(Primordium.Portrait.Render(a.G, 48));
        return t;
    }

    // The width of the sidebar on the left: nothing of the HUD's own is drawn under it.
    float Left => Main?.Ui?.LeftInset ?? 0;

    public double DrawMs;   // summed since Main last read it (perf overlay)
    string lastError;

    // The panel reads bodies the simulation thread is changing: a torn read must not take the game
    // down, it only spoils one redraw.
    public override void _Draw()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { DrawPanel(); }
        catch (Exception e)
        {
            clipTop = float.MinValue; clipBot = float.MaxValue;
            if (e.Message != lastError) { lastError = e.Message; GD.PrintErr("hud: " + e.Message); }
        }
        DrawMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    void DrawPanel()
    {
        var w = Main?.World;
        if (w == null || Main.Frame == null) return;
        var vs = GetViewportRect().Size;
        float px = vs.X - PanelW;
        hits.Clear();
        clicks.Clear();
        if (Main.FastForward) { DrawFastForward(w, vs); return; }
        DrawRect(new Rect2(px, 0, PanelW, vs.Y), PanelBg);
        DrawLine(new Vector2(px, 0), new Vector2(px, vs.Y), Rule);
        // The status lines and the hints start right of the sidebar (UiManager.LeftInset).
        float left = Left;
        DrawSetTransform(new Vector2(left, 0), 0, Vector2.One);
        TopBar(w, px - left);
        Hints(vs, px - left);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);

        float x = px + 18, y = 20, cw = PanelW - 36;
        var sel = Main.View.Selected;
        if (sel != null)
        {
            // The inspector gets the whole panel and scrolls with the mouse wheel.
            clipTop = 8; clipBot = vs.Y - 6;
            float end = AgentPanel(w, sel, x, y - AgentScroll, cw, vs.Y);
            clipTop = float.MinValue; clipBot = float.MaxValue;
            float content = end + AgentScroll - y, visible = vs.Y - 30;
            AgentScrollMax = Math.Max(0, content - visible);
            AgentScroll = Math.Clamp(AgentScroll, 0, AgentScrollMax);
            if (AgentScrollMax > 0)
            {
                float trackH = vs.Y - 20, barH = Math.Max(30, trackH * visible / content);
                DrawRect(new Rect2(vs.X - 6, 10 + (trackH - barH) * AgentScroll / AgentScrollMax, 3, barH), new Color(1, 1, 1, 0.25f));
            }
        }
        else
        {
            // The world panel scrolls too (the wheel over it).
            clipTop = 8; clipBot = vs.Y - 6;
            y -= WorldScroll;
            y = Graph(x, y, cw);
            y = Events(x, y, cw);
            y = ClimatePanel(x, y, cw);
            y = Organs(x, y, cw);
            y = Lineages(x, y, cw);
            y = Depths(w, x, y, cw);
            float end = ChemPanel(w, x, y, cw);
            clipTop = float.MinValue; clipBot = float.MaxValue;
            float content = end + WorldScroll - 20, visible = vs.Y - 30;
            WorldScrollMax = Math.Max(0, content - visible);
            WorldScroll = Math.Clamp(WorldScroll, 0, WorldScrollMax);
            if (WorldScrollMax > 0)
            {
                float trackH = vs.Y - 20, barH = Math.Max(30, trackH * visible / content);
                DrawRect(new Rect2(vs.X - 6, 10 + (trackH - barH) * WorldScroll / WorldScrollMax, 3, barH), new Color(1, 1, 1, 0.25f));
            }
        }
        if (Legend) DrawLegend(vs);
        else if (Main.ShowRecords) DrawRecords(vs);
        Tooltip(w, vs, px);
    }

    // While fast-forwarding only progress is drawn.
    void DrawFastForward(World w, Vector2 vs)
    {
        DrawRect(new Rect2(0, 0, vs.X, vs.Y), new Color(0.03f, 0.035f, 0.045f, 1f));
        float bw = Math.Min(600, vs.X - 80), x = (vs.X - bw) / 2, y = vs.Y / 2 - 40;
        double f = (double)(w.Tick - Main.FastFrom) / Math.Max(1, Main.FastTo - Main.FastFrom);
        T(x, y, Loc.T($"Fast-forward to day {Main.FastTo / P.DayLen + 1}", $"Промотка до суток {Main.FastTo / P.DayLen + 1}"), Fg, 18, bold);
        DrawRect(new Rect2(x, y + 14, bw, 10), new Color(1, 1, 1, 0.08f));
        DrawRect(new Rect2(x, y + 14, bw * (float)Math.Clamp(f, 0, 1), 10), new Color(0.4f, 0.9f, 0.5f));
        T(x, y + 46, Loc.T($"day {w.Day + 1} · tick {w.Tick:N0} · bodies {w.Agents.Count:N0} · {Main.Tps:F0} ticks/s · generations {w.MaxGen}",
                           $"сутки {w.Day + 1} · тик {w.Tick:N0} · особей {w.Agents.Count:N0} · {Main.Tps:F0} тиков/с · поколений {w.MaxGen}"), Dim, 13);
        T(x, y + 66, Loc.T("T or Esc — stop", "T или Esc — остановить"), Dim, 12);
    }

    // The most remarkable living bodies; click one to look at it.
    void DrawRecords(Vector2 vs)
    {
        var rec = Main.Records;
        float w = 440, h = 34 + rec.Count * 20, x = Left + 16, y = vs.Y - 82 - h;
        DrawRect(new Rect2(x, y, w, h), new Color(0.04f, 0.045f, 0.06f, 0.94f));
        T(x + 12, y + 20, Loc.T("Records · click — show (B — hide, O — oldest)", "Рекорды · клик — показать (B — скрыть, O — старейший)"), Fg, 13, bold);
        var m = GetViewport().GetMousePosition();
        for (int k = 0; k < rec.Count; k++)
        {
            var (name, a, value) = rec[k];
            float ry = y + 40 + k * 20;
            var row = new Rect2(x + 4, ry - 14, w - 8, 19);
            hits.Add((row, a));
            if (row.HasPoint(m)) DrawRect(row, new Color(1, 1, 1, 0.06f));
            DrawCircle(new Vector2(x + 17, ry - 4), 5, View3D.KinColor(a));
            T(x + 30, ry, SimStats.RecordTitle(name), Dim, 12);
            T(x + 210, ry, $"#{a.Id} · {Loc.Show(value)}", a == Main.View.Selected ? Acc : Fg, 12);
        }
    }

    // What everything on the map means.
    void DrawLegend(Vector2 vs)
    {
        (Color c, string s)[] rows =
        {
            (new(0.75f, 0.75f, 0.8f), Loc.T("body shape and color — family: kin look alike, branches diverge in color", "форма и цвет тела — семья: родня похожа, ветви расходятся по цвету")),
            (new(0.4f, 0.9f, 0.35f), Loc.T("cap: feeds on light", "шапочка: питается светом")),
            (new(0.35f, 0.62f, 1f), Loc.T("cap: chemistry (splits and joins molecules)", "шапочка: химией (расщепляет и соединяет молекулы)")),
            (new(1f, 0.65f, 0.25f), Loc.T("cap: mines molecules from aggregates", "шапочка: добывает молекулы из агрегатов")),
            (new(1f, 0.2f, 0.15f), Loc.T("cap: hunter", "шапочка: охотник")),
            (new(0.6f, 0.6f, 0.64f), Loc.T("cap: nearly idle", "шапочка: почти бездействует")),
            (new(0.4f, 0.95f, 0.5f), Loc.T("side cube: proteins — green light, blue chemistry, red motor", "кубик сбоку: белки — зелёный свет, синий химия, красный мотор")),
            (new(1f, 0.85f, 0.4f), Loc.T("small colored cubes flying off bodies — expelled molecules", "мелкие цветные кубики, разлетающиеся от тел — выброшенные молекулы")),
            (new(1f, 0.3f, 0.2f), Loc.T("red beam — strike, red flash — kill", "красный луч — удар, красная вспышка — убийство")),
            (new(0.85f, 0.3f, 1f), Loc.T("violet beam — gene insertion", "фиолетовый луч — вставка генов")),
            (new(0.3f, 1f, 1f), Loc.T("cyan bridge — colony link", "бирюзовая перемычка — связь колонии")),
            (new(0.55f, 0.55f, 0.6f), Loc.T("gray fading cube — death", "серый тающий кубик — смерть")),
            (new(1f, 0.42f, 0.12f), Loc.T("orange ball — volcano", "оранжевый шар — вулкан")),
            (new(1f, 0.55f, 1f), Loc.T("pink pillar with a ring — orbital strike", "розовый столб с кольцом — удар с орбиты")),
            (new(0.2f, 0.45f, 0.75f), Loc.T("clear blue — water, pale blue — ice, white — snow", "прозрачный синий — вода, светло-голубой — лёд, белый — снег")),
            (new(0.3f, 0.55f, 0.9f), Loc.T("in water a body without gas sinks and walks the bottom; gas in it is a bubble that floats it up. Light near the surface, food at the bottom",
                                           "в воде тело без газа тонет и ходит по дну; газ в теле — пузырь, с ним всплывает. У поверхности свет, у дна еда")),
            (new(0.5f, 0.33f, 0.17f), Loc.T("rock color — its molecules; cavities and vaults show in the cross-section (C)", "цвет породы — её молекулы; полости и своды видны в разрезе (C)")),
            (new(0.58f, 0.45f, 0.3f), Loc.T("thin top block — already grazed; a block eaten through vanishes", "тонкий верхний блок — его уже объели; съеденный до конца блок исчезает")),
            (new(0.45f, 0.4f, 0.6f), Loc.T("large body with a tinted floor under it — a creature spanning several cells", "большое тело и тонированный пол под ним — существо, занявшее несколько клеток")),
        };
        float w = 520, h = 34 + rows.Length * 18, x = Left + 16, y = vs.Y - 82 - h;
        DrawRect(new Rect2(x, y, w, h), new Color(0.04f, 0.045f, 0.06f, 0.94f));
        T(x + 12, y + 20, Loc.T("Legend (H — hide)", "Легенда (H — скрыть)"), Fg, 13, bold);
        for (int k = 0; k < rows.Length; k++)
        {
            float ry = y + 40 + k * 18;
            DrawRect(new Rect2(x + 12, ry - 10, 11, 11), rows[k].c);
            T(x + 30, ry, rows[k].s, Dim, 12);
        }
    }

    // Small card next to the cursor for the agent under it.
    void Tooltip(World w, Vector2 vs, float px)
    {
        var a = Main.View.Hover;
        if (a == null || a.Dead) return;
        var m = GetViewport().GetMousePosition();
        string l1 = Loc.T($"#{a.Id} · {Looks.ShapeNames[a.Shape]} · lineage #{a.Lineage} · gen {a.Gen}", $"#{a.Id} · {Looks.ShapeNames[a.Shape]} · линия #{a.Lineage} · поколение {a.Gen}")
                    + (a.Cells > 1 ? Loc.T($" · on {a.Cells} cells", $" · на {a.Cells} клетках") : "");
        string l2 = World.MatterLaw
            ? Loc.T($"{DietName(a)} · charge {w.Held(a):F0} of {w.Capacity(a):F0} · age {a.Age:N0}",
                    $"{DietName(a)} · заряд {w.Held(a):F0} из {w.Capacity(a):F0} · возраст {a.Age:N0}")
            : Loc.T($"{DietName(a)} · energy {Math.Max(0, a.Energy):F0} (comfortable store {a.Store:F0}) · age {a.Age:N0}",
                    $"{DietName(a)} · энергия {Math.Max(0, a.Energy):F0} (удобный запас {a.Store:F0}) · возраст {a.Age:N0}");
        int cell = a.Y * w.W + a.X;
        float fill = Main.Frame.Hover == a ? Main.Frame.HoverFloorFill : 0;
        string l3 = Loc.T($"cell {w.Temp[cell]:+0;-0} °C{CaveBrief(w, a)}, body {a.Tb:+0;-0} °C · neighbors {w.Count[cell] - 1}, floor {fill:P0} full",
                          $"клетка {w.Temp[cell]:+0;-0} °C{CaveBrief(w, a)}, тело {a.Tb:+0;-0} °C · соседей {w.Count[cell] - 1}, место занято на {fill:P0}")
                    + (w.Water[cell] > 0.05f ? Loc.T($" · water {w.Water[cell]:0.0}", $" · вода {w.Water[cell]:0.0}") : "") +
                    Loc.T($" · proteins {a.EnzN} ({EnzymeBrief(a)})", $" · белков {a.EnzN} ({EnzymeBrief(a)})");
        var sel = Main.View.Selected;
        string l4 = sel != null && sel != a ? (Looks.Kin(a, sel) > 0 ? Loc.T($"kin of the selected · relatedness {Looks.Kin(a, sel) * 100:F0}%", $"родня выбранного · близость {Looks.Kin(a, sel) * 100:F0}%")
                                                                     : Loc.T("not kin of the selected", "не родня выбранному"))
                                            : Loc.T("click — select and see all kin", "клик — выбрать и увидеть всю родню");
        float wdt = new[] { l1, l2, l3, l4 }.Max(s => TW(s, 12)) + 44;
        float x = Math.Min(m.X + 18, px - wdt - 8), y = Math.Min(m.Y + 18, vs.Y - 100);
        DrawRect(new Rect2(x, y, wdt, 76), new Color(0.04f, 0.045f, 0.06f, 0.93f));
        DrawRect(new Rect2(x, y, 4, 76), View3D.KinColor(a));
        DrawCircle(new Vector2(x + 22, y + 15), 7, View3D.KinColor(a));
        DrawCircle(new Vector2(x + 22, y + 15), 3, View3D.DietColor(a));
        T(x + 36, y + 19, l1, Fg, 12, bold);
        T(x + 14, y + 37, l2, Fg, 12);
        T(x + 14, y + 53, l3, Dim, 12);
        T(x + 14, y + 69, l4, Dim, 12);
    }

    // Colour of an instruction by what it deals with.
    static Color OpColor(byte b)
    {
        int op = b & 63;
        return op switch
        {
            <= Genome.Rand => new Color(0.55f, 0.6f, 0.7f),                       // stack and arithmetic
            <= Genome.Store => new Color(0.75f, 0.55f, 1f),                       // control and memory
            Genome.EnzymeOp => new Color(0.3f, 1f, 0.55f),                        // genes of proteins
            <= Genome.Pick => new Color(0.4f, 0.8f, 1f),                          // senses
            <= Genome.Emit => new Color(1f, 0.85f, 0.4f),                         // about others, signals
            <= Genome.Divide => new Color(0.35f, 0.62f, 1f),                      // membrane, reactions, life
            Genome.Mine or Genome.Dig or Genome.Pile or Genome.Ground => new Color(1f, 0.6f, 0.25f),   // the ground
            _ => new Color(1f, 0.35f, 0.3f),                                       // acting on others, genes
        };
    }

    static string EnzymeBrief(Agent a)
    {
        float l = a.EnzymeOf(Enzyme.Photo), c = a.EnzymeOf(Enzyme.Split) + a.EnzymeOf(Enzyme.Bind), m = a.EnzymeOf(Enzyme.Motor);
        if (l + c + m < 0.05f) return Loc.T("none", "нет");
        return string.Join(", ", new[] { (l, Loc.T("light", "свет")), (c, Loc.T("chem", "химия")), (m, Loc.T("motor", "мотор")) }.Where(t => t.Item1 >= 0.05f).Select(t => $"{t.Item2} {t.Item1:0.0}"));
    }

    static string DietName(Agent a) => World.Diet(a) switch
    {
        World.DietPlant => Loc.T("feeds on light", "питается светом"),
        World.DietEater => Loc.T("feeds on chemistry", "питается химией"),
        World.DietMiner => Loc.T("eats soil", "ест почву"),
        World.DietHunter => Loc.T("hunter", "охотник"),
        _ => Loc.T("nearly idle", "почти бездействует"),
    };

    void TopBar(World w, float px)
    {
        DrawRect(new Rect2(0, 0, px, 98), BarBg);
        int season = (int)(w.YearFrac * 4) % 4;
        float tod = w.DayFrac;
        string day = Loc.T($"Day {w.Day + 1} · {Seasons[season]} in the north · tick {w.Tick:N0}", $"Сутки {w.Day + 1} · {Seasons[season]} на севере · тик {w.Tick:N0}");
        string speed = (Main.Paused ? Loc.T("PAUSED", "ПАУЗА") : Loc.T($"speed ×{Main.Tpf} · {Main.Tps:F0} ticks/s", $"скорость ×{Main.Tpf} · {Main.Tps:F0} тиков/с"));
        string times = Loc.T($" · tick {Main.SimMs:F1} ms · frame {Main.ViewMs:F1} ms", $" · тик {Main.SimMs:F1} мс · кадр {Main.ViewMs:F1} мс");
        // On a narrow screen the timings give way first, then the sun clock, then the tick number.
        float dayW = TW(day, 16, bold);
        if (16 + dayW + 24 + 130 + TW(speed + times, 14, bold) <= px - 16) speed += times;
        bool clock = 16 + dayW + 24 + 130 + TW(speed, 14, bold) <= px - 16;
        if (16 + dayW + 16 + TW(speed, 14, bold) > px - 16) day = Loc.T($"Day {w.Day + 1} · {Seasons[season]}", $"Сутки {w.Day + 1} · {Seasons[season]}");
        T(16, 24, day, Fg, 16, bold);
        float sx = px - 16 - TW(speed, 14, bold);
        T(sx, 24, speed, Main.Paused ? Acc : Fg, 14, bold);

        // Sun clock: where the sun stands over the planet right now.
        float cx = sx - 130, cy = 19;
        if (clock)
        {
            DrawRect(new Rect2(cx, cy - 2, 100, 4), new Color(0.15f, 0.17f, 0.22f));
            DrawCircle(new Vector2(cx + tod * 100, cy), 5, new Color(1f, 0.85f, 0.35f));
        }

        var c = Main.Census;
        string tail = Loc.T($" · parentless {w.Spawns:N0} (first and abiogenic) · generations {w.MaxGen} · oldest {c.OldestAge:N0} t.",
                            $" · без родителей {w.Spawns:N0} (первые и самозарождённые) · поколений {w.MaxGen} · старейший {c.OldestAge:N0} т.");
        string pop = Loc.T($"alive {c.Pop:N0} · born {w.Births:N0} · died {w.Deaths:N0}", $"особей {c.Pop:N0} · рождено {w.Births:N0} · умерло {w.Deaths:N0}");
        string line = pop + Loc.T($" (starved {w.DeathsStarve:N0}, killed {w.DeathsKilled:N0}, broke {w.DeathsBroken:N0}, climate {w.DeathsClimate:N0}{(w.DeathsHand > 0 ? $", by hand {w.DeathsHand:N0}" : "")})",
                                  $" (голод {w.DeathsStarve:N0}, убиты {w.DeathsKilled:N0}, распад {w.DeathsBroken:N0}, климат {w.DeathsClimate:N0}{(w.DeathsHand > 0 ? $", рукой {w.DeathsHand:N0}" : "")})") + tail;
        if (TW(line) > px - 32) line = pop + Loc.T($" (killed {w.DeathsKilled:N0})", $" (убиты {w.DeathsKilled:N0})") + tail;
        if (TW(line) > px - 32) line = pop + tail;
        T(16, 44, Clip(line, px - 32, 13), Fg, 13);
        var v = Main.View;
        string OnOff(bool b) => b ? Loc.T("on", "вкл") : Loc.T("off", "выкл");
        // The overlay's name can be long: the other parts of the line give way first, then the name is cut
        // with an ellipsis, so the line never runs under the panel.
        string surface = Loc.T($"surface: {OverlayName(w, v.Overlay)}", $"поверхность: {OverlayName(w, v.Overlay)}");
        string coloring = Loc.T($" · coloring: {View3D.ColorModeNames[v.ColorMode]} · ", $" · окраска: {View3D.ColorModeNames[v.ColorMode]} · ") +
                          (v.Lighting ? Loc.T("sunlight", "освещение солнца") : Loc.T("no lighting", "без освещения")) +
                          (v.Slice >= 0 ? Loc.T($" · cross-section at row {v.Slice}", $" · разрез по строке {v.Slice}") : "") +
                          (v.Follow ? Loc.T(" · following the agent", " · слежу за агентом") : "");
        string life = Loc.T($" · abiogenesis {OnOff(w.Abiogenesis)} (A) · orbital strikes {OnOff(w.AutoStrikes)} (⇧X), so far {w.StrikeCount}",
                            $" · самозарождение {OnOff(w.Abiogenesis)} (A) · удары с орбиты {OnOff(w.AutoStrikes)} (⇧X), было {w.StrikeCount}");
        float room = px - 32;
        string mode = surface + coloring + life;
        if (TW(mode, 12) > room) mode = surface + coloring + Loc.T($" · abiogenesis {OnOff(w.Abiogenesis)}", $" · самозарождение {OnOff(w.Abiogenesis)}");
        if (TW(mode, 12) > room)
        {
            string brief = Loc.T($" · {View3D.ColorModeNames[v.ColorMode]} · abiogenesis {OnOff(w.Abiogenesis)}", $" · {View3D.ColorModeNames[v.ColorMode]} · самозарождение {OnOff(w.Abiogenesis)}");
            mode = Clip(surface, room - TW(brief, 12), 12) + brief;
        }
        T(16, 60, mode, Dim, 12);
        string skyLine = SkyLine(w, v.CursorCell, true);
        if (TW(skyLine, 12) > px - 32) skyLine = SkyLine(w, v.CursorCell, false);
        T(16, 76, Clip(skyLine, px - 32, 12), Dim, 12);
        // The climate epoch (World.ClimateCycles): ice age or interglacial, the phase of the cycles, ash, droughts.
        string epoch = w.EpochLine(true);
        if (TW(epoch, 12) > px - 32) epoch = w.EpochLine(false);
        T(16, 92, Clip(epoch, px - 32, 12), w.IceAgeNow || w.VolcanicWinterNow ? Acc : Dim, 12);
    }

    // The sky (World.Sky): the sun's activity and flares, the next eclipse, and the column under the cursor —
    // latitude, day length, the noon sun, the power now, the transparency. Read-only arithmetic.
    double eclipseAskedAt = -10; long eclipseNext = -1; World eclipseWorld;
    string SkyLine(World w, int cell, bool full)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (now - eclipseAskedAt > 2 || eclipseWorld != w) { eclipseAskedAt = now; eclipseWorld = w; eclipseNext = w.NextEclipse(w.Tick, 200); }
        string sun = World.FlareLaw ? Loc.T($"sun: activity {w.SolarActivity:P0}", $"солнце: активность {w.SolarActivity:P0}")
                                      + (w.FlarePower > 0 ? Loc.T($", FLARE {w.FlarePower:0.0}", $", ВСПЫШКА {w.FlarePower:0.0}") : "")
                                      + (full ? Loc.T($", flares so far {w.FlareCount}", $", вспышек было {w.FlareCount}") : "")
                                    : Loc.T("flares off", "вспышки выключены");
        string ecl = !World.EclipseLaw ? Loc.T("eclipses off", "затмения выключены")
                   : w.EclipseNow ? Loc.T($"ECLIPSE: shadow at ({w.EclipseX:0}, {w.EclipseY:0})", $"ЗАТМЕНИЕ: тень в ({w.EclipseX:0}, {w.EclipseY:0})")
                   : eclipseNext > 0 ? Loc.T($"eclipse in {(eclipseNext - w.Tick) / (float)P.DayLen:0.0} d", $"затмение через {(eclipseNext - w.Tick) / (float)P.DayLen:0.0} сут")
                   : Loc.T("no eclipses for 200 days", "затмений нет 200 суток");
        string here = "";
        if (cell >= 0)
        {
            int y = cell / w.W;
            float lat = w.Latitude(y), decl = w.SunDecl, deg = lat * 180 / MathF.PI;
            string ns = deg >= 0 ? Loc.T("N", "с.") : Loc.T("S", "ю.");
            here = full ? Loc.T($" · ({cell % w.W}, {y}): latitude {MathF.Abs(deg):0.0}° {ns}, day {World.DayShare(lat, decl) * 24:0.0} h of 24, " +
                                $"noon sun {World.NoonElevation(lat, decl):0}°, power {w.Sun[cell]:0.00} (at the bottom {w.Light[cell]:0.00}), transparency {w.Transp[cell]:0.00}",
                                $" · ({cell % w.W}, {y}): широта {MathF.Abs(deg):0.0}° {ns}, день {World.DayShare(lat, decl) * 24:0.0} ч из 24, " +
                                $"солнце в полдень {World.NoonElevation(lat, decl):0}°, мощность {w.Sun[cell]:0.00} (у дна {w.Light[cell]:0.00}), прозрачность {w.Transp[cell]:0.00}")
                        : Loc.T($" · ({cell % w.W}, {y}): {MathF.Abs(deg):0}° {ns}, day {World.DayShare(lat, decl) * 24:0.0} h, noon {World.NoonElevation(lat, decl):0}°, power {w.Sun[cell]:0.00}, transp. {w.Transp[cell]:0.00}",
                                $" · ({cell % w.W}, {y}): {MathF.Abs(deg):0}° {ns}, день {World.DayShare(lat, decl) * 24:0.0} ч, полдень {World.NoonElevation(lat, decl):0}°, мощность {w.Sun[cell]:0.00}, прозр. {w.Transp[cell]:0.00}");
        }
        return sun + " · " + ecl + here;
    }

    // Where a body is: underground, on dry ground, or in water — on the bottom, swimming, at the surface
    // (with its density against the water's: what decides whether it rises or sinks).
    static string Where(World w, Agent a)
    {
        int cell = a.Y * w.W + a.X;
        if (w.InCave(a)) return Loc.T("underground", "под землёй");
        if (!w.InWater(a)) return Loc.T("surface", "поверхность");
        string pos = World.OnFloor(a) ? Loc.T($"on the bottom, {w.Water[cell]:0.0} to the surface", $"на дне, до поверхности {w.Water[cell]:0.0}")
                   : w.AtSurface(a, cell) ? Loc.T("swimming at the surface", "плывёт у поверхности")
                   : Loc.T($"swimming mid-water: {a.Lift:0.0} above the bottom, {w.Below(a, cell):0.0} to the surface", $"плывёт в толще: над дном {a.Lift:0.0}, до поверхности {w.Below(a, cell):0.0}");
        string drift = a.Density < P.WaterDensity ? Loc.T("lighter than water — rises", "легче воды — всплывает")
                     : a.Density > P.WaterDensity ? Loc.T("heavier than water — sinks", "тяжелее воды — тонет") : Loc.T("same as water", "как вода");
        return Loc.T($"in water, {pos} · density {a.Density:0.00} ({drift})", $"в воде, {pos} · плотность {a.Density:0.00} ({drift})");
    }

    // Under a roof the body feels the cave climate (World.Cave). Read-only arithmetic on the world's arrays.
    static string CaveBrief(World w, Agent a)
    {
        int cell = a.Y * w.W + a.X;
        if (!w.InCave(a)) return "";
        return Loc.T($", under a roof of {w.Roof(cell, a.Z)} bl.: around {w.LocalTemp(cell, a.Z):+0;-0} °C", $", под крышей {w.Roof(cell, a.Z)} бл.: вокруг {w.LocalTemp(cell, a.Z):+0;-0} °C");
    }

    static (string, string) CaveLines(World w, Agent a)
    {
        int cell = a.Y * w.W + a.X;
        float surf = w.Temp[cell];
        if (!w.InCave(a)) return (Loc.T($"no roof · around {surf:+0;-0} °C (surface)", $"крыши нет · вокруг {surf:+0;-0} °C (поверхность)"), null);
        int roof = w.Roof(cell, a.Z);
        string law = World.CaveLaw ? "" : Loc.T(" (cave climate law off)", " (закон климата пещер выключен)");
        int depth = Math.Max(0, w.Height[cell] - 1 - a.Z);
        return (Loc.T($"roof {roof} bl. · shelter {w.Cover(cell, a.Z):P0} · depth {depth} lv.", $"крыша {roof} бл. · укрытие {w.Cover(cell, a.Z):P0} · глубина {depth} ур.") + law,
                Loc.T($"around {w.LocalTemp(cell, a.Z):+0.0;-0.0} °C: depth climate {w.CaveTemp(cell, a.Z):+0.0;-0.0}, surface {surf:+0.0;-0.0}",
                      $"вокруг {w.LocalTemp(cell, a.Z):+0.0;-0.0} °C: климат глубины {w.CaveTemp(cell, a.Z):+0.0;-0.0}, поверхность {surf:+0.0;-0.0}"));
    }

    string OverlayName(World w, int o) => o switch
    {
        0 => Loc.T("rock and soil", "породы и почва"),
        1 => Loc.T("temperature", "температура"),
        2 => Loc.T("sun power at the surface", "мощность солнца у поверхности"),
        View3D.TranspOverlay => (World.TranspLaw ? Loc.T($"atmospheric transparency (mean {w.TranspMean:0.00})", $"прозрачность атмосферы (в среднем {w.TranspMean:0.00})")
                                                 : Loc.T("atmospheric transparency (law off: clear sky everywhere)", "прозрачность атмосферы (закон выключен: небо везде ясное)"))
                                + (w.VeilTransMean < 0.999f ? Loc.T($", ash: light {w.VeilTransMean:P0}", $", пепел: свет {w.VeilTransMean:P0}") : ""),
        View3D.DayOverlay => Loc.T("day length: night — black, 12 h — green, polar day — light", "длина дня: ночь — чёрный, 12 ч — зелёный, полярный день — светлый"),
        View3D.FlareOverlay => World.FlareLaw ? (w.FlarePower > 0 ? Loc.T($"flare dose on open ground (power {w.FlarePower:0.0})", $"доза вспышки на открытой поверхности (мощность {w.FlarePower:0.0})")
                                                                  : Loc.T($"flare dose: no flare, dim — where solar activity reaches ({w.SolarActivity:P0})", $"доза вспышки: вспышки нет, тускло — куда достаёт активность солнца ({w.SolarActivity:P0})"))
                                              : Loc.T("flare dose (flares off)", "доза вспышки (вспышки выключены)"),
        3 => Loc.T("remains on the ground", "останки на земле"),
        4 => Loc.T("life density", "плотность жизни"),
        5 => Loc.T("recent deaths", "недавние смерти"),
        6 => Loc.T("body heat", "тепло от тел"),
        7 => Loc.T("load / strength (green → red)", "нагрузка / прочность (зелёный → красный)"),
        8 => Loc.T("matter: lattice order and packing — brighter with facets and gloss = ordered, darker = more mass per volume (C: every level)", "вещество: порядок решётки и упаковка — ярче, с гранями и блеском = упорядочено, темнее = больше массы на объём (C: все уровни)"),
        View3D.DepthTempOverlay => P.CaveClimate == 0 ? Loc.T("temperature at depth (law off: same as the surface everywhere)", "температура на глубине (закон выключен: везде как на поверхности)")
                                  : Loc.T("temperature at depth: on top — yearly mean, in the cross-section (C) — what a body feels at each level",
                                          "температура на глубине: сверху — средняя за год, в разрезе (C) — что чувствует тело на каждом уровне"),
        View3D.DeepOverlay => Loc.T($"deep element {w.Chem.ElementName[w.DeepElement]} (bias {w.DepthBias[w.DeepElement]:+0.00;-0.00}): share of atoms in the top block, in the cross-section (C) — by level, veins lighter",
                                    $"глубинный элемент {w.Chem.ElementName[w.DeepElement]} (смещение {w.DepthBias[w.DeepElement]:+0.00;-0.00}): доля в атомах верхнего блока, в разрезе (C) — по уровням, жилы светлее")
                              + (w.GeoOn ? "" : Loc.T(" (world created without a depth profile)", " (мир создан без профиля глубины)")),
        View3D.RangeOverlay => RangeName(),
        View3D.StockOverlay => Loc.T("food and gas stock: loose matter on the ground — green → yellow, air gas — blue, black — eaten out",
                                     "запас еды и газа: рыхлое на земле — зелёный → жёлтый, газ воздуха — синий, чёрное — выедено")
                               + (P.GasDiffK != 1 ? Loc.T($" (gas diffusion ×{P.GasDiffK:0.##})", $" (диффузия газа ×{P.GasDiffK:0.##})") : ""),
        _ when o - View3D.FirstSpecies == w.Chem.Gas => Loc.T($"gas {w.Chem.Name[w.Chem.Gas]} in the air (E{w.Chem.E[w.Chem.Gas]})", $"газ {w.Chem.Name[w.Chem.Gas]} в воздухе (E{w.Chem.E[w.Chem.Gas]})"),
        _ => Loc.T($"molecule {w.Chem.Name[o - View3D.FirstSpecies]} on the ground (E{w.Chem.E[o - View3D.FirstSpecies]}", $"молекула {w.Chem.Name[o - View3D.FirstSpecies]} на земле (E{w.Chem.E[o - View3D.FirstSpecies]}") +
             $"{(World.Harmful(w.Chem, o - View3D.FirstSpecies) ? Loc.T(", reactive (harms proteins)", ", реакционная (вредит белкам)") : w.Chem.Solid[o - View3D.FirstSpecies] ? Loc.T(", solid", ", твёрдая") : "")})",
    };

    // The range overlay: what is shown and from which tree (SimObserver.Tree, built while it is on).
    // A design's name in the language of the moment (the examples are stored under their Russian key).
    public static string DesignName(string name) => name == null ? null : Loc.En ? CreatureExamples.NameEn(name) : CreatureExamples.NameRu(name);

    string RangeName()
    {
        var t = Main.View.Ranges;
        if (t.CellClade == null) return Loc.T("ranges of clades (building the tree…)", "ареалы ветвей (строю дерево…)");
        string pick = Main.View.RangeHash != 0 && t.Find(Main.View.RangeHash, Main.View.RangeOrigin) is int k and >= 0
            ? Loc.T($", picked: branch {k + 1} bright", $", выбрана ветвь {k + 1} — ярко") : "";
        return Loc.T($"ranges of clades: {t.Nodes.Length} branches of ≥ {t.Threshold} bodies, color as in the tree of life (F1){pick}",
                     $"ареалы ветвей: {t.Nodes.Length} ветвей от {t.Threshold} тел, цвет как в древе жизни (F1){pick}");
    }

    void Hints(Vector2 vs, float px)
    {
        DrawRect(new Rect2(0, vs.Y - 74, px, 74), BarBg);
        var m = Main;
        if (m.Tool > 0)
        {
            // The hand's brush is out: what it does and how to steer it.
            string what = m.Tool switch
            {
                1 when m.PourMix != null => Loc.T($"pour: {m.Ui.Matter.BrushLabel} (library recipe, J) — Z: single species again", $"насыпать: {m.Ui.Matter.BrushLabel} (рецепт библиотеки, J) — Z: снова один вид"),
                1 => m.PourLock
                    ? Loc.T($"pour: {m.World.Chem.MatName[m.PourSpecies + 2]} — locked: every stroke pours it (Z — random again)",
                            $"насыпать: {m.World.Chem.MatName[m.PourSpecies + 2]} — закреплён: каждый мазок сыплет его (Z — снова случайный)")
                    : Loc.T($"pour: {m.World.Chem.MatName[m.PourSpecies + 2]} (each stroke is a new random material, a fresh loose heap)",
                            $"насыпать: {m.World.Chem.MatName[m.PourSpecies + 2]} (каждый мазок — новый случайный материал, свежая рыхлая насыпь)"),
                2 => Loc.T("flood with water (then it flows, evaporates and falls as rain like any other)", "залить водой (дальше она течёт, испаряется и выпадает дождём как обычная)"),
                3 => Loc.T("kill everyone in the circle (remains stay in place)", "убить всех в круге (останки остаются на месте)"),
                5 => m.Ui.Life.Armed != LifeLibraryWindow.Arm.None ? m.Ui.Life.BrushText : m.Ui.Creator.BrushText,
                _ => Loc.T("dig: remove the top blocks (the matter leaves the world with the hand)", "копнуть: снять верхние блоки (вещество уходит из мира с рукой)"),
            };
            string l1 = Loc.T($"brush — {what}", $"кисть — {what}");
            string l2 = m.Tool == 5 && m.Ui.Life.Armed != LifeLibraryWindow.Arm.None ? m.Ui.Life.BrushHint
                : m.Tool == 5
                ? Loc.T($"click — plant in this cell · spread {m.BrushR:0} ([ ]) · F7 — designer · 5, 0 or Esc — put away",
                        $"клик — посадить в эту клетку · разброс {m.BrushR:0} ([ ]) · F7 — конструктор · 5, 0 или Esc — убрать")
                : m.Tool == 1
                ? Loc.T($"radius {m.BrushR:0} · LMB — pour · [ ] — size · Z — lock this material, ⇧Z — next one, I — exact sample under the cursor · J — matter library · 1, 0 or Esc — put away",
                        $"радиус {m.BrushR:0} · ЛКМ — сыпать · [ ] — размер · Z — закрепить материал, ⇧Z — следующий, I — точный образец под курсором · J — библиотека веществ · 1, 0 или Esc — убрать")
                : Loc.T($"radius {m.BrushR:0} · LMB — paint · [ ] — size · 1–5 — other tool · same digit, 0 or Esc — put away",
                        $"радиус {m.BrushR:0} · ЛКМ — рисовать · [ ] — размер · 1–5 — другой инструмент · та же цифра, 0 или Esc — убрать");
            l1 = Clip(l1, px - 76, 13);
            l2 = Clip(l2, px - 62, 12);
            float tw = Math.Min(px - 20, Math.Max(TW(l1, 13) + 14, TW(l2, 12)) + 40);
            DrawRect(new Rect2(10, vs.Y - 120, tw, 42), new Color(0.08f, 0.09f, 0.12f, 0.92f));
            float tx = 22;
            if (m.Tool == 1)
            {
                var col = m.PourMix != null ? m.PourColour : m.World.Chem.MatCol[Math.Max(0, m.PourSpecies) + 2];
                DrawRect(new Rect2(18, vs.Y - 112, 12, 12), new Color(col.R, col.G, col.B));
                tx = 36;
            }
            T(tx, vs.Y - 101, l1, Fg, 13);
            T(22, vs.Y - 85, l2, Dim, 12);
        }
        T(16, vs.Y - 56, Clip(Loc.T("Space pause · . step · +/− speed · T skip N days, ⇧T 10 · R new world, ⇧R same one · P snapshot · 1–4 brush · F3 benchmark",
                               "Space пауза · . шаг · +/− скорость · T промотка на N суток, ⇧T на 10 · R новый мир, ⇧R тот же · P снимок · 1–4 кисть · F3 замер"), px - 32, 12), Dim, 12);
        T(16, vs.Y - 40, Clip(Loc.T("Tab sidebar · F1 tree of life · F2 laws · F4 new world · F5 quick save, F9 load · F6 saves · F7 designer · 5 plant a design · F8 chronicle · F10 evolution · F12 metrics · J matter",
                                    "Tab панель · F1 древо жизни · F2 законы · F4 новый мир · F5 сохранить, F9 загрузить · F6 сохранения · F7 конструктор · 5 посадить · F8 хроника · F10 эволюция · F12 метрики · J вещества"), px - 32, 12), Dim, 12);
        T(16, vs.Y - 24, Clip(Loc.T("LMB / WASD / two fingers — pan · RMB / Q E — rotate · wheel / pinch — zoom · G whole map · C cross-section, [ ] shift",
                               "ЛКМ / WASD / два пальца — сдвиг · ПКМ / Q E — поворот · колесо / щипок — зум · G вся карта · C разрез, [ ] сдвиг"), px - 32, 12), Dim, 12);
        T(16, vs.Y - 8, Clip(Loc.T("click — agent · H legend · B records · O oldest · K kin · F follow · V coloring · M surface · L light · N life · A abiogenesis · X strike",
                              "клик — агент · H легенда · B рекорды · O старейший · K родня · F следить · V окраска · M поверхность · L свет · N жизнь · A абиогенез · X удар"), px - 32, 12), Dim, 12);
    }

    float Header(float x, float y, string s)
    {
        T(x, y + 12, s, Fg, 14, bold);
        return y + 20;
    }

    float Graph(float x, float y, float cw)
    {
        y = Header(x, y, Loc.T("Population", "Население"));
        const float gh = 74;
        DrawRect(new Rect2(x, y, cw, gh), new Color(1, 1, 1, 0.04f));
        var hist = Main.Frame.Hist;
        int hn = Main.Frame.HistN;
        if (hn > 1)
        {
            int mx = 10;
            for (int i = 0; i < hn; i++) mx = Math.Max(mx, hist[i * 5]);
            Color[] cols = { Fg, CPlant, CEater, CMiner, CHunter };
            for (int k = 4; k >= 0; k--)
            {
                var pts = new Vector2[hn];
                for (int i = 0; i < hn; i++)
                    pts[i] = new Vector2(x + i * cw / Math.Max(1, hn - 1), y + gh - 2 - hist[i * 5 + k] * (gh - 6) / mx);
                DrawPolyline(pts, cols[k], k == 0 ? 1.6f : 1.2f, true);
            }
            T(x + 6, y + 14, Loc.T($"max {mx:N0}", $"макс {mx:N0}"), Dim, 11);
            EventMarks(x, y, cw, gh, hn);
        }
        y += gh + 14;
        var c = Main.Census;
        float lx = x;
        void Leg(Color col, string s)
        {
            DrawRect(new Rect2(lx, y - 9, 9, 9), col);
            T(lx + 13, y, s, Dim, 12);
            lx += 13 + TW(s, 12) + 12;
        }
        Leg(Fg, Loc.T($"all {c.Pop:N0}", $"все {c.Pop:N0}"));
        Leg(CPlant, Loc.T($"light {c.Plants:N0}", $"свет {c.Plants:N0}"));
        Leg(CEater, Loc.T($"chem {c.Eaters:N0}", $"химия {c.Eaters:N0}"));
        Leg(CMiner, Loc.T($"soil {c.Miners:N0}", $"почва {c.Miners:N0}"));
        Leg(CHunter, Loc.T($"hunt {c.Hunters:N0}", $"охота {c.Hunters:N0}"));
        if (c.InWater > 0) Leg(new Color(0.3f, 0.55f, 0.9f), Loc.T($"in water {c.InWater:N0} (afloat {c.Afloat:N0})", $"в воде {c.InWater:N0} (плывут {c.Afloat:N0})"));
        return y + 16;
    }

    float Events(float x, float y, float cw)
    {
        y = Header(x, y, Loc.T("Events over the last 1000 ticks", "События за последние 1000 тиков"));
        float colw = cw / 3;
        for (int k = 0; k < EvShow.Length; k++)
        {
            float cx = x + (k % 3) * colw, cy = y + 12 + (k / 3) * 17;
            long v = Main.Frame.EvRate[(int)EvShow[k].k];
            T(cx, cy, Loc.T(EvShow[k].en, EvShow[k].ru), Dim, 12);
            string s = v.ToString("N0");
            T(cx + colw - 10 - TW(s, 12, bold), cy, s, v > 0 ? Fg : Dim, 12, bold);
        }
        return y + 12 + 4 * 17 + 6;
    }

    float ClimatePanel(float x, float y, float cw)
    {
        var c = Main.Climate;
        if (c == null) return y;
        y = Header(x, y, Loc.T("Climate", "Климат"));
        T(x, y + 12, Loc.T($"mean t° {c.MeanT:+0;-0} °C · from {c.MinT:+0;-0} to {c.MaxT:+0;-0} · rain over {c.RainShare:P0}",
                           $"t° средняя {c.MeanT:+0;-0} °C · от {c.MinT:+0;-0} до {c.MaxT:+0;-0} · дождь над {c.RainShare:P0}"), Fg, 12);
        T(x, y + 29, Loc.T($"water {c.WaterShare:P0} · ice {c.IceShare:P0} · snow {c.SnowShare:P0} of the surface",
                           $"вода {c.WaterShare:P0} · лёд {c.IceShare:P0} · снег {c.SnowShare:P0} поверхности"), Dim, 12);
        return y + 38;
    }

    float Organs(float x, float y, float cw)
    {
        y = Header(x, y, Loc.T("Proteins — per body (mean)", "Белки — сколько на особь (средн.)"));
        var c = Main.Census;
        float colw = cw / 4;
        string[] names = Loc.T(new[] { "binding", "splitting", "light", "motor" }, new[] { "соединение", "расщепл.", "свет", "мотор" });
        for (int k = 0; k < 4; k++)
        {
            float cx = x + k * colw, cy = y + 12;
            T(cx, cy, names[k], Dim, 12);
            float v = c.EnzKind[k];
            string s = v < 0.05f && v > 0 ? "<0.1" : v.ToString("0.0");
            T(cx + colw - 8 - TW(s, 12, bold), cy, s, v > 0 ? Fg : Dim, 12, bold);
        }
        T(x, y + 29, Loc.T($"distinct proteins {c.AvgEnz:0.0} · genome conserved {c.AvgProt:P0} · body {c.AvgTb:+0;-0} °C · large bodies {c.Big:N0} (up to {c.MaxCells} cells)",
                           $"разных белков {c.AvgEnz:0.0} · закреплено генома {c.AvgProt:P0} · тело {c.AvgTb:+0;-0} °C · больших тел {c.Big:N0} (до {c.MaxCells} клеток)"), Dim, 12);
        if (!World.OrganLaw) return y + 12 + 2 * 17 + 8;
        // P.Organs 1: the organs of sense (World.Organs), per body (mean).
        T(x, y + 46, Loc.T($"senses: receptor {c.EnzKind[Enzyme.Receptor]:0.0} · photoreceptor {c.EnzKind[Enzyme.Photoreceptor]:0.0} · mechano {c.EnzKind[Enzyme.Mechano]:0.0} · thermo {c.EnzKind[Enzyme.Thermo]:0.0}",
                           $"чувства: рецептор {c.EnzKind[Enzyme.Receptor]:0.0} · фоторецептор {c.EnzKind[Enzyme.Photoreceptor]:0.0} · механо {c.EnzKind[Enzyme.Mechano]:0.0} · термо {c.EnzKind[Enzyme.Thermo]:0.0}"), Dim, 12);
        return y + 12 + 3 * 17 + 8;
    }

    float Lineages(float x, float y, float cw)
    {
        y = Header(x, y, Loc.T("Largest lineages · click — show", "Крупнейшие линии · клик — показать"));
        foreach (var (lin, n, gen, rep) in Main.Lineages)
        {
            var row = new Rect2(x - 4, y, cw + 8, 33);
            hits.Add((row, rep));
            if (row.HasPoint(GetViewport().GetMousePosition())) DrawRect(row, new Color(1, 1, 1, 0.05f));
            DrawTextureRect(Portrait(rep), new Rect2(x, y + 2, 28, 28), false);
            T(x + 36, y + 14, Loc.T($"lineage #{lin}", $"линия #{lin}"), Fg, 13, bold);
            T(x + 36, y + 28, Loc.T($"bodies {n:N0} · gen up to {gen} · {Looks.ShapeNames[rep.Shape]} · {DietName(rep)}",
                                    $"особей {n:N0} · поколение до {gen} · {Looks.ShapeNames[rep.Shape]} · {DietName(rep)}"), Dim, 12);
            DrawCircle(new Vector2(x + cw - 8, y + 16), 7, View3D.KinColor(rep));
            DrawCircle(new Vector2(x + cw - 8, y + 16), 3, View3D.DietColor(rep));
            y += 34;
        }
        return y + 8;
    }

    static readonly string[] Grades = { "○", "●", "●●", "●●●", "●●●●", "—" };

    // Energy it takes to work one molecule out of a lattice with this barrier (without teeth): the
    // face needs e^(barrier − P.FaceBarrier) of loosening, and loosening costs P.FaceWork apiece.
    static string Cost(float barrier)
    {
        double e = P.FaceWork * Math.Exp(barrier - P.FaceBarrier);
        return e < 10 ? $"{e:0.#}" : e < 1e5 ? $"{e:0}" : "∞";
    }

    // The rocks of this world, from soft to hard: what it takes to break each, and who managed first.
    float Depths(World w, float x, float y, float cw)
    {
        var ch = w.Chem;
        y = Header(x, y, Loc.T("Matter: composition, lattice and support", "Материя: состав, решётка и опора"));
        T(x, y + 10, Loc.T($"cave-ins {w.CollapsedBlocks} · buried {w.DeathsBuried} · pressure reactions {w.Metamorphoses}",
                           $"обвалы {w.CollapsedBlocks} · погребено {w.DeathsBuried} · реакций под давлением {w.Metamorphoses}"), Dim, 11);
        y += 16;
        foreach (var line in Wrap(Loc.T(
                                  "a molecule has to be torn out of a block: each attempt costs effort (the harder the rock, the more) and succeeds " +
                                  "with the given chance (for a block of average order; deeper and denser — less often); a bond-breaking protein " +
                                  "helps mining. Pressure orders the lattice; undermining and weak seams between different rocks cause cave-ins, " +
                                  "loose matter slides off slopes that are too steep. A block is full when its molecules fill its volume; gnawing at " +
                                  "a surface is a process: each attempt puts work into the cell's face, the work accumulates, and the molecule comes " +
                                  "out once enough has built up — faster with several gnawing. How much effort a molecule costs (the number after " +
                                  "“effort”) is set by the lattice: loose deposits and mixtures are cheap, ordered strong crystals cost more than any " +
                                  "molecule is worth unless a bond-breaking protein lowers the barrier (→). E — energy of one molecule",
                                  "молекулу из блока надо вырвать: попытка стоит сил (тем больше, чем твёрже порода) и удаётся " +
                                  "с указанным шансом (для блока средней упорядоченности; глубже и плотнее — реже); белок на связь " +
                                  "помогает добыче. Давление упорядочивает решётку; подкоп и слабые стыки разных пород вызывают обвалы, " +
                                  "рыхлое сползает со слишком крутых мест. Блок полон, когда его молекулы заполняют объём; поверхность " +
                                  "грызть — процесс: каждая попытка вкладывает работу в грань клетки, работа копится, и молекула выходит, " +
                                  "когда её набралось достаточно — у нескольких грызущих быстрее. Сколько сил стоит молекула (число после " +
                                  "«сил»), решает решётка: рыхлые отложения и смеси дёшевы, упорядоченные прочные кристаллы дороже любой " +
                                  "молекулы, если белок на связь не снимет барьер (→). E — энергия одной молекулы"), cw, 11))
        { T(x, y + 10, line, Dim, 11); y += 13; }
        y += 6;
        long tot = Math.Max(1, w.Mined.Sum());
        foreach (int m in Enumerable.Range(1, ch.MatCount - 1).OrderBy(m => w.TypicalBarrier(m, 0.5f)))
        {
            int t = ch.MatTier[m];
            var c = ch.MatCol[m];
            R(new Rect2(x, y + 1, 10, 10), new Color(c.R, c.G, c.B));
            string name = ch.MatName[m].Replace(" aggregate", "").Replace("агрегат ", "");
            T(x + 15, y + 10, name, Fg, 11);
            T(x + 118, y + 10, Grades[t], t >= 3 ? new Color(1f, 0.7f, 0.4f) : Dim, 10);
            int energy = 0;
            energy = t <= 4 ? ch.E[m - 2] : 0;   // per molecule (a full block holds MatCap of them)
            if (t <= 4) T(x + 152, y + 10, $"E{energy}", Dim, 11);
            string need = t > 4 ? Loc.T("indestructible", "не разрушается")
                : ch.MatKey[m] < 0 ? Loc.T($"effort {Cost(w.TypicalBarrier(m, 0.5f))} · no protein needed", $"сил {Cost(w.TypicalBarrier(m, 0.5f))} · белок не нужен")
                : Loc.T($"effort {Cost(w.TypicalBarrier(m, 0.5f))} → {Cost(w.TypicalBarrier(m, 0.5f) * (1 - P.CatalysisMax))} · protein {ch.Name[ch.MatKey[m]]}",
                        $"сил {Cost(w.TypicalBarrier(m, 0.5f))} → {Cost(w.TypicalBarrier(m, 0.5f) * (1 - P.CatalysisMax))} · белок {ch.Name[ch.MatKey[m]]}");
            T(x + 190, y + 10, need, t >= 2 ? Fg : Dim, 11);
            y += 15;
        }
        y += 4;
        string mined = string.Join(" · ", Enumerable.Range(0, 5).Where(k => w.Mined[k] > 0).Select(k => $"{Grades[k]} {w.Mined[k] * 100.0 / tot:0.#}%"));
        if (mined.Length > 0) { T(x, y + 10, Loc.T("molecules torn out by rock hardness: ", "вырвано молекул по сложности породы: ") + mined, Dim, 11); y += 16; }
        var firsts = w.Firsts.Where(f => f != null).OrderBy(f => f.Tick).ToList();
        if (firsts.Count == 0) { T(x, y + 10, Loc.T("no discoveries yet: nobody has learned to break hard rock with a protein", "открытий пока нет: твёрдую породу ещё никто не научился разрушать белком"), Dim, 11); y += 16; }
        foreach (var f in firsts)
        {
            T(x, y + 10, Loc.T($"discovery: {ch.MatName[f.Mat]} — lineage #{f.Lineage}, day {f.Tick / P.DayLen + 1}", $"открытие: {ch.MatName[f.Mat]} — линия #{f.Lineage}, сутки {f.Tick / P.DayLen + 1}"), Acc, 12);
            y += 15;
        }
        return y + 8;
    }

    float ChemPanel(World w, float x, float y, float cw)
    {
        var ch = w.Chem;
        y = Header(x, y, Loc.T($"World molecules · seed {w.Seed}", $"Молекулы мира · seed {w.Seed}"));
        T(x, y + 10, Loc.T("E — state energy; * — excitation; composition is conserved", "E — энергия состояния; * — возбуждение; состав сохраняется"), Dim, 11);
        y += 18;
        float colw = cw / 3;
        int rows = (Chemistry.S + 2) / 3;
        for (int s = 0; s < Chemistry.S; s++)
        {
            float cx = x + (s / rows) * colw, cy = y + 12 + (s % rows) * 14;
            var c = ch.Col[s];
            R(new Rect2(cx, cy - 9, 9, 9), new Color(c.R, c.G, c.B));
            var tc = World.Harmful(ch, s) ? new Color(1f, 0.45f, 0.45f) : ch.Solid[s] ? Acc : Dim;
            string label = $"{ch.Name[s]} {ch.Formula(s)} E{ch.E[s]}" + (World.Harmful(ch, s) ? Loc.T(" reactive", " реакц.") : ch.Solid[s] ? Loc.T(" solid", " твёрд") : s == ch.Gas ? Loc.T(" gas", " газ") : "");
            T(cx + 13, cy, label, s == ch.Gas ? new Color(0.6f, 0.85f, 1f) : tc, 11);
        }
        return y + 12 + rows * 14 + 10;
    }

    // Everything about one body. Returns where it ended (for scrolling).
    float AgentPanel(World w, Agent a, float x, float y, float cw, float maxY)
    {
        var ch = w.Chem;
        y = Header(x, y, Loc.T("Agent · Esc — back to the world", "Агент · Esc — назад к миру"));
        if (y + 66 > clipTop && y < clipBot) DrawTextureRect(Portrait(a), new Rect2(x, y + 2, 64, 64), false);
        float tx = x + 76;
        T(tx, y + 14, $"#{a.Id}" + (a.Dead ? Loc.T($" · died ({Causes[a.Cause]})", $" · погиб ({Causes[a.Cause]})") : ""), a.Dead ? new Color(1, 0.5f, 0.45f) : Fg, 14, bold);
        T(tx, y + 31, Loc.T($"lineage #{a.Lineage} · gen {a.Gen} · age {a.Age:N0}", $"линия #{a.Lineage} · поколение {a.Gen} · возраст {a.Age:N0}"), Dim, 12);
        bool matter = World.MatterLaw;   // P.MatterEnergy 1: the energy is the charge of its molecules (World.Charge)
        float cap = matter ? (float)w.Capacity(a) : a.Store, e = matter ? (float)w.Held(a) : (float)Math.Max(0, a.Energy);
        R(new Rect2(tx, y + 38, cw - 76, 7), new Color(1, 1, 1, 0.08f));
        R(new Rect2(tx, y + 38, (cw - 76) * Math.Clamp(e / Math.Max(1e-3f, cap), 0, 1), 7), new Color(0.4f, 0.9f, 0.5f));
        if (matter)
        {
            int excited = 0;
            foreach (int s in ch.Excited) excited += a.Inv[s];
            double fuel = w.FuelCharge(a);
            T(tx, y + 59, Loc.T($"charge {e:F1} of {cap:F0} ({excited} excited molecules)" + (fuel > 0.05 ? $" · fuel {fuel:F1}" : "") + (a.Energy > 0.05 ? $" · old store {a.Energy:F1}" : "") + (a.Due > 0 ? $" · owes {a.Due:F2}" : "") + $" · {a.LastCycles} cycles/tick",
                                $"заряд {e:F1} из {cap:F0} (возбуждённых молекул {excited})" + (fuel > 0.05 ? $" · топливо {fuel:F1}" : "") + (a.Energy > 0.05 ? $" · прежний запас {a.Energy:F1}" : "") + (a.Due > 0 ? $" · долг {a.Due:F2}" : "") + $" · {a.LastCycles} тактов/тик"), Dim, 12);
        }
        else
            T(tx, y + 59, Loc.T($"energy {e:F1} (comfortable store {cap:F0}, beyond it leaks faster) · {a.LastCycles} cycles/tick",
                                $"энергия {e:F1} (удобный запас {cap:F0}, сверх — утекает быстрее) · {a.LastCycles} тактов/тик"), Dim, 12);
        y += 74;
        // The player's creatures: planted from a design, or descended from one.
        string design = Main.Sim.DesignedLineages.TryGetValue(a.Lineage, out var dn) ? DesignName(dn) : null;
        if (a.Designed || design != null)
        {
            string mark = a.Designed ? Loc.T($"player's: planted from design “{design ?? "?"}”", $"от игрока: посажен из дизайна «{design ?? "?"}»")
                                     : Loc.T($"player's: descendant of design “{design}”, generation {a.Gen}", $"от игрока: потомок дизайна «{design}» в {a.Gen}-м поколении");
            R(new Rect2(x, y + 1, cw, 18), new Color(1f, 0.82f, 0.4f, 0.1f));
            T(x + 6, y + 14, mark, Acc, 12, bold);
            y += 24;
        }

        y = Tabs(x, y, cw);
        if (AgentTab == 1) return BioPanel(w, a, x, y, cw);

        // How it lives, in words.
        y = Section(x, y, Loc.T("How it lives", "Как живёт"));
        foreach (var line in Wrap(LifeStory(a), cw, 12)) { T(x, y + 10, line, Fg, 12); y += 15; }
        y += 4;

        // Energy budget per tick (smoothed).
        float net = a.EmaNet;
        T(x, y + 10, Loc.T($"income: light → into molecules +{a.EmaPhoto:0.000} · chemistry +{a.EmaChem:0.000} · from others +{a.EmaGot:0.000}",
                           $"доход: свет → в молекулы +{a.EmaPhoto:0.000} · химия +{a.EmaChem:0.000} · от других +{a.EmaGot:0.000}"), Dim, 12);
        T(x, y + 25, Loc.T($"soil: molecules worth {a.EmaMine:0.000} energy · costs: living −{a.EmaUpkeep:0.000} · climate −{a.EmaHarm:0.000}",
                           $"почва: молекул на {a.EmaMine:0.000} энергии · расход: жизнь −{a.EmaUpkeep:0.000} · климат −{a.EmaHarm:0.000}"), Dim, 12);
        T(x, y + 40, Loc.T($"net per tick {net:+0.000;-0.000}", $"итого за тик {net:+0.000;-0.000}")
                     + (net > 0.001f ? Loc.T(" — saving", " — копит") : net < -0.001f ? Loc.T(" — drawing on its store", " — тратит запас") : Loc.T(" — in balance", " — в равновесии")),
            net > 0.001f ? new Color(0.5f, 0.95f, 0.55f) : net < -0.001f ? new Color(1f, 0.55f, 0.5f) : Fg, 12, bold);
        y += 50;
        T(x, y + 10, Loc.T($"caught light {a.NPhoto:N0} · split {a.NSplit:N0} · joined {a.NBind:N0} · took in {a.NIntake:N0} · expelled {a.NExpel:N0}",
                           $"поймал свет {a.NPhoto:N0} · расщепил {a.NSplit:N0} · соединил {a.NBind:N0} · втянул {a.NIntake:N0} · выбросил {a.NExpel:N0}"), Dim, 12);
        y += 18;

        // The whole life: what it lived on (the lines above only cover the last ~100 ticks).
        float acts = Math.Max(0, a.LifeStart + a.GainChem + a.LifeGot - a.LifeKids - a.LifeUpkeep - a.LifeHarm - a.LifeSpill - a.LifeUphill - a.LifeMineCost - (float)(World.MatterLaw ? w.Held(a) : Math.Max(0, a.Energy)));
        int meals = a.NBind + a.NSplit;
        string when = a.LastMeal < 0 ? Loc.T("no profitable reactions yet", "реакций с выгодой не было")
            : Loc.T($"a reaction every {a.Age / Math.Max(1, meals):N0} t. on average, the last one ", $"реакция в среднем раз в {a.Age / Math.Max(1, meals):N0} т., последняя ")
              + (a.Dead ? Loc.T($"{a.Age - a.LastMeal:N0} t. before death", $"за {a.Age - a.LastMeal:N0} т. до смерти") : Loc.T($"{a.Age - a.LastMeal:N0} t. ago", $"{a.Age - a.LastMeal:N0} т. назад"));
        foreach (var line in Wrap(Loc.T($"received over its life: at birth {a.LifeStart:N0} · chemistry +{a.GainChem:N0} · from others +{a.LifeGot:N0} · stored in molecules by light {a.GainPhoto:N0}",
                                        $"за жизнь получил: при рождении {a.LifeStart:N0} · химия +{a.GainChem:N0} · от других +{a.LifeGot:N0} · светом запасено в молекулах {a.GainPhoto:N0}"), cw, 12))
        { T(x, y + 10, line, Fg, 12); y += 15; }
        foreach (var line in Wrap(Loc.T($"spent: living {a.LifeUpkeep:N0} · children {a.LifeKids:N0} · climate {a.LifeHarm:N0} · uphill reactions {a.LifeUphill:N0} · mining rock {a.LifeMineCost:N0} · other actions {acts:N0} · lost holding the store {a.LifeSpill:N0}",
                                        $"потратил: жизнь {a.LifeUpkeep:N0} · детям {a.LifeKids:N0} · климат {a.LifeHarm:N0} · реакции в минус {a.LifeUphill:N0} · добыча из породы {a.LifeMineCost:N0} · прочие действия {acts:N0} · ушло на удержание запаса {a.LifeSpill:N0}"), cw, 12))
        { T(x, y + 10, line, Fg, 12); y += 15; }
        foreach (var line in Wrap(when, cw, 12)) { T(x, y + 10, line, Dim, 12); y += 15; }
        y += 5;

        // Energy over the last ticks.
        var hist = Main.Frame.SelHist;
        int hc = Main.Frame.Sel == a ? Main.Frame.SelHistN : 0;
        if (hc > 1)
        {
            float gh = 26, mx = 1f;
            for (int k = 0; k < hc; k++) mx = Math.Max(mx, hist[k]);
            if (y > clipTop && y + gh < clipBot)
            {
                DrawRect(new Rect2(x, y + 2, cw, gh), new Color(1, 1, 1, 0.04f));
                var pts = new Vector2[hc];
                for (int k = 0; k < hc; k++)
                    pts[k] = new Vector2(x + k * cw / 299f, y + 2 + gh - Math.Max(0, hist[k]) / mx * (gh - 2));
                DrawPolyline(pts, new Color(0.4f, 0.9f, 0.5f), 1.3f, true);
            }
            T(x + 4, y + 13, Loc.T($"energy over {hc * 5} ticks (max {mx:0})", $"энергия за {hc * 5} тиков (макс {mx:0})"), Dim, 10);
            y += gh + 6;
        }

        // What its program actually spends its time on.
        int total = a.OpCount.Sum();
        if (total > 0)
        {
            var top = Enumerable.Range(0, Genome.OpSlots).Where(k => a.OpCount[k] > 0).OrderByDescending(k => a.OpCount[k]).Take(8)
                .Select(k => $"{Genome.SlotName(k)} {a.OpCount[k] * 100f / total:0}%");
            T(x, y + 10, Loc.T("runs most often:", "чаще всего выполняет:"), Dim, 12);
            y += 14;
            foreach (var line in Wrap(string.Join(" · ", top), cw, 12, mono)) { T(x, y + 10, line, Fg, 12, mono); y += 15; }
            y += 4;
        }

        y = Section(x, y, Loc.T("Body", "Тело"));
        int nextCells = Math.Min(P.MaxCells, a.Cells + 1);
        float growMass = P.GrowMass * MathF.Pow(nextCells - 1, P.GrowPow);
        string grow = a.Cells >= P.MaxCells ? Loc.T("limit", "предел") : Loc.T($"cell {nextCells} at mass {growMass:0}", $"{nextCells}-я клетка при массе {growMass:0}");
        float selFill = Main.Frame.Sel == a ? Main.Frame.SelFloorFill : 0;
        T(x, y + 10, Loc.T($"{a.InvTotal} molecules (comfortable up to {a.Room}{(a.Packing > 1 ? $", packed ×{a.Packing:0.0} — costlier to hold" : "")}) · mass {a.Mass:F0} · volume {a.Volume:F0} (floor {selFill:P0} full) · cells {a.Cells} ({grow})",
                           $"{a.InvTotal} молекул (удобно до {a.Room}{(a.Packing > 1 ? $", набит ×{a.Packing:0.0} — держать дороже" : "")}) · масса {a.Mass:F0} · объём {a.Volume:F0} (пол занят на {selFill:P0}) · клеток {a.Cells} ({grow})"), Dim, 12);
        T(x, y + 25, Loc.T($"level {a.Z} · {Where(w, a)} · body {a.Tb:+0;-0} °C", $"уровень {a.Z} · {Where(w, a)} · тело {a.Tb:+0;-0} °C"), Dim, 12);
        var (cave1, cave2) = CaveLines(w, a);
        T(x, y + 40, cave1, Dim, 12);
        if (cave2 != null) { T(x, y + 55, cave2, Dim, 12); y += 15; }
        // The sky over it (World.Sky): what reaches it, what its own matter lets through, the flare dose now.
        T(x, y + 55, Loc.T($"sky above the body: reaches {w.SkyExposure(a):P0} · body's screen passes {w.Shield(a):P0}", $"небо над телом: доходит {w.SkyExposure(a):P0} · экран тела пропускает {w.Shield(a):P0}")
                     + (World.FlareLaw ? Loc.T($" · flare dose {a.FlareDose:0.00}", $" · доза вспышки {a.FlareDose:0.00}") : ""), Dim, 12);
        y += 30;
        if (y + 30 > clipTop && y + 30 < clipBot) DrawCircle(new Vector2(x + 6, y + 37), 6, View3D.KinColor(a));
        T(x + 18, y + 41, Loc.T($"looks: {Looks.ShapeNames[a.Shape]} · kin on the planet {Main.KinCount:N0}", $"облик: {Looks.ShapeNames[a.Shape]} · родни на планете {Main.KinCount:N0}") +
                          (Main.View.KinFocus ? Loc.T(" (highlighted, K — off)", " (подсвечена, K — выкл.)") : Loc.T(" (K — highlight)", " (K — подсветить)")), Fg, 12);
        y += 50;
        var fr = Main.Frame;
        if (fr.Sel == a) T(x, y + 10, Loc.T($"support: load {fr.SelPressure:F1} / strength {fr.SelCapacity:F1} · order {fr.SelOrder:P0}",
                                            $"опора: нагрузка {fr.SelPressure:F1} / прочность {fr.SelCapacity:F1} · порядок {fr.SelOrder:P0}"), Dim, 11);
        y += 16;
        float lx = x;
        y += 10;
        foreach (int s in Enumerable.Range(0, Chemistry.S).Where(s => a.Inv[s] > 0).OrderByDescending(s => a.Inv[s]))
        {
            if (lx > x + cw - 40) { lx = x; y += 15; }
            var col = ch.Col[s];
            R(new Rect2(lx, y - 9, 9, 9), new Color(col.R, col.G, col.B));
            T(lx + 12, y, a.Inv[s].ToString(), World.Harmful(ch, s) ? new Color(1f, 0.45f, 0.45f) : ch.Solid[s] ? Acc : Fg, 12);
            lx += 34;
        }
        y += 14;

        // Proteins it carries right now: what they do, how much, how well, at which temperature.
        y = Section(x, y, Loc.T("Proteins", "Белки") + (a.EnzN == 0 ? Loc.T(" — none", " — нет") : ""));
        for (int k = 0; k < a.EnzN; k++)
        {
            var z = a.Enz[k];
            string what = z.Kind switch
            {
                Enzyme.Bind => $"{ch.Name[z.A]} + {ch.Name[z.B]}",
                Enzyme.Split => $"{ch.Name[z.A]} →",
                Enzyme.Photo => Loc.T($"light + {ch.Name[z.A]}", $"свет + {ch.Name[z.A]}"),
                Enzyme.Receptor => ch.Name[z.A],
                Enzyme.Photoreceptor => Loc.T($"pigment {ch.Name[z.A]}", $"пигмент {ch.Name[z.A]}") + (z.Material == z.A ? "" : Loc.T(" (missing: blind)", " (нет — слеп)")),
                _ => "",
            };
            T(x + 8, y + 10, Loc.T($"{Genome.EnzymeKind[z.Kind]} {what} · ×{z.Amount:0.0} · quality {z.Eff * 100:0}% · best at {z.Topt:+0;-0}°",
                                   $"{Genome.EnzymeKind[z.Kind]} {what} · ×{z.Amount:0.0} · качество {z.Eff * 100:0}% · лучше всего при {z.Topt:+0;-0}°"), Dim, 11);
            y += 13;
        }
        y += 4;

        // Prototype aggregate barriers; the local lattice order and composition refine these in Mine.
        y = Section(x, y, Loc.T("Depths — what it can break", "Недра — что ему по силам"));
        T(x, y + 10, Loc.T("molecules torn out: ", "вырвал молекул: ") + string.Join(" · ", Enumerable.Range(0, 5).Select(k => $"{Grades[k]} {a.NMinedTier[k]}")), Dim, 12);
        y += 16;
        foreach (int m in Enumerable.Range(1, ch.MatCount - 1).Where(m => ch.MatTier[m] >= 2 && ch.MatTier[m] <= 4).OrderBy(m => ch.MatTier[m]))
        {
            float cat = w.Catalysis(a, (byte)m, out _);
            bool hasEnz = a.Enz.Take(a.EnzN).Any(e => e.Kind == Enzyme.Split && e.A == ch.MatKey[m]);
            string state = cat > 0 ? Loc.T($"effort per molecule {Cost(w.TypicalBarrier(m, 0.5f))} → {Cost(w.TypicalBarrier(m, 0.5f) * (1 - cat))}",
                                           $"сил на молекулу {Cost(w.TypicalBarrier(m, 0.5f))} → {Cost(w.TypicalBarrier(m, 0.5f) * (1 - cat))}")
                : !hasEnz ? Loc.T($"no protein for {ch.Name[ch.MatKey[m]]}", $"нет белка на {ch.Name[ch.MatKey[m]]}")
                : Loc.T("protein inactive at this temperature", "белок не работает при такой температуре");
            T(x + 8, y + 10, $"{ch.MatName[m]} {Grades[ch.MatTier[m]]}: {state}", cat > 0 ? new Color(0.5f, 0.95f, 0.55f) : Dim, 11);
            y += 13;
        }
        y += 4;

        y = Section(x, y, Loc.T("Deeds", "Поступки"));
        T(x, y + 10, Loc.T($"children {a.NChildren} · matings {a.NMates} · steps {a.NMoves} · attacks {a.NAttacks} · kills {a.NKills}",
                           $"детей {a.NChildren} · спариваний {a.NMates} · шагов {a.NMoves} · атак {a.NAttacks} · убил {a.NKills}"), Dim, 12);
        T(x, y + 25, Loc.T($"blocks moved {a.NDigs} · built {a.NPiles} · aggregates made {a.NGrows} · rock gnawed {a.NMines} · stole {a.NTakes} · gave {a.NGives}",
                           $"перенёс блоков {a.NDigs} · строил {a.NPiles} · выделял агрегаты {a.NGrows} · грыз породу {a.NMines} · крал {a.NTakes} · дарил {a.NGives}"), Dim, 12);
        T(x, y + 40, Loc.T($"genes inserted {a.NInjects} · infected {a.NInfected} · excised {a.NCuts} · proteins made {a.NExpress} · irradiated {a.NStruck}",
                           $"вписал гены {a.NInjects} · заражён {a.NInfected} · вырезал {a.NCuts} · сделал белков {a.NExpress} · облучён {a.NStruck}"), Dim, 12);
        y += 50;

        y = Section(x, y, Loc.T("Processor", "Процессор"));
        string stack = a.Sp == 0 ? Loc.T("empty", "пусто") : string.Join(" ", a.Stack.Take(a.Sp));
        T(x, y + 10, Loc.T("stack: ", "стек: ") + stack, Dim, 12, mono);
        T(x, y + 25, Loc.T("memory: ", "память: ") + string.Join(" ", a.Mem), Dim, 12, mono);
        y += 34;

        // The whole genome at a glance: colour = kind of instruction, brightness = how well proven
        // (protected) the byte is. Settled blocks — "organs" — stand out as bright runs.
        var g = a.G;
        var prot = a.Prot;   // replaced together with G: read both once and stay within both
        int n = Math.Min(g.Length, prot.Length), cur = a.Ip % Math.Max(1, n);
        T(x, y + 10, Loc.T("genome: color — instruction kind, brightness — conservation", "геном: цвет — вид команды, яркость — закреплённость"), Dim, 11);
        y += 14;
        if (y > clipTop && y + 12 < clipBot)
        {
            float bw2 = cw / n;
            for (int i = 0; i < n; i++)
            {
                float pr = prot[i] / 255f;
                DrawRect(new Rect2(x + i * bw2, y, MathF.Max(1, bw2 - 0.5f), 10), OpColor(g[i]).Darkened(0.5f - 0.5f * MathF.Min(1, pr * 3)));
            }
            DrawRect(new Rect2(x + cur * bw2 - 1, y - 2, 2, 14), Fg);
        }
        y += 16;

        // The whole program; proven lines glow, the running one is marked.
        T(x, y + 10, Loc.T("full code (→ now, gold — conserved)", "код целиком (→ сейчас, золотом — закреплённое)"), Fg, 13, bold);
        y += 12;
        for (int i = 0; i < n;)
        {
            y += 15;
            string s = Genome.DisAt(g, i, out int len);
            bool here = cur >= i && cur < i + len;
            var lc = here ? Acc : Dim.Lerp(new Color(1f, 0.8f, 0.35f), MathF.Min(1, prot[i] / 80f));
            T(x, y, $"{(here ? "→" : " ")}{i,4}  {s}", lc, 12, mono);
            i += len;
        }
        return y + 20;
    }

    // Important chronicle events over the population graph: a tick of the event's colour at its time
    // (the graph has a sample every 100 ticks, the newest at the frame's tick).
    void EventMarks(float x, float y, float cw, float gh, int hn)
    {
        var events = Main.Sim.Chronicle.Events;
        long last = Main.Frame.Tick / 100 * 100, first = last - (hn - 1) * 100L;
        var m = GetViewport().GetMousePosition();
        string tip = null;
        float tipX = 0;
        for (int i = events.Length - 1; i >= 0; i--)
        {
            var e = events[i];
            if (e.Tick < first) break;
            if (!e.Important || e.Type == EvType.Player) continue;
            float ex = x + (e.Tick - first) / 100f * cw / Math.Max(1, hn - 1);
            if (ex > x + cw) continue;
            var c = ChronicleWindow.TypeColors[(int)e.Type];
            DrawLine(new Vector2(ex, y), new Vector2(ex, y + 7), c, 2);
            if (MathF.Abs(m.X - ex) < 3 && m.Y >= y - 2 && m.Y <= y + gh) { tip = $"{Chronicle.Day(e.Tick)}: {Loc.Show(e.Text)}"; tipX = ex; }
        }
        if (tip != null)
        {
            float tw = Math.Min(TW(tip, 11) + 12, Math.Max(80, x + cw - tipX + 200));
            float tx = Math.Clamp(tipX - tw / 2, x - 10, x + cw + 10 - tw);
            DrawRect(new Rect2(tx, y + gh - 18, tw, 16), new Color(0.03f, 0.035f, 0.045f, 0.95f));
            T(tx + 6, y + gh - 6, tip, Fg, 11);
        }
    }

    // A link in the panel: accent text that does something when clicked. Returns where it ends.
    float Link(float x, float y, string s, Action act, int size = 12)
    {
        float w = TW(s, size);
        var r = new Rect2(x - 2, y - size, w + 4, size + 5);
        bool inside = y - size >= clipTop && y <= clipBot;
        if (inside)
        {
            clicks.Add((r, act));
            bool hover = r.HasPoint(GetViewport().GetMousePosition());
            if (hover) DrawRect(r, new Color(1, 1, 1, 0.07f));
            DrawLine(new Vector2(x, y + 2), new Vector2(x + w, y + 2), Acc with { A = hover ? 0.9f : 0.35f }, 1);
        }
        T(x, y, s, Acc, size);
        return x + w;
    }

    float Tabs(float x, float y, float cw)
    {
        string[] names = Loc.T(new[] { "Overview", "Biography" }, new[] { "Обзор", "Биография" });
        float tx = x;
        for (int k = 0; k < names.Length; k++)
        {
            float w = TW(names[k], 13, bold) + 24;
            var r = new Rect2(tx, y + 2, w, 22);
            if (r.Position.Y >= clipTop && r.End.Y <= clipBot)
            {
                DrawRect(r, k == AgentTab ? new Color(0.24f, 0.21f, 0.12f) : new Color(1, 1, 1, 0.05f));
                if (k == AgentTab) DrawRect(new Rect2(tx, y + 22, w, 2), Acc);
                int tab = k;
                clicks.Add((r, () => { AgentTab = tab; AgentScroll = 0; }));
            }
            T(tx + 12, y + 18, names[k], k == AgentTab ? Acc : Dim, 13, bold);
            tx += w + 4;
        }
        DrawLine(new Vector2(x, y + 24), new Vector2(x + cw, y + 24), Rule);
        return y + 30;
    }

    // The biography tab: how it is watched, its parent, tracked ancestors and children (links), energy
    // and mass lately, and its biography, newest first.
    float BioPanel(World w, Agent a, float x, float y, float cw)
    {
        var f = Main.Frame;
        var view = Main.Sim.Chronicle;
        bool mine = f.Sel == a;
        var ui = Main.Ui;
        if (mine && f.SelTracked)
            foreach (var line in Wrap(Loc.T($"biography kept: {Chronicle.WhyText(a.TrackWhy)} · entries {a.BioN}", $"биография ведётся: {Chronicle.WhyText(a.TrackWhy)} · записей {a.BioN}")
                                      + (a.BioN > Chronicle.BioCap ? Loc.T($" (the last {Chronicle.BioCap} are kept)", $" (хранятся последние {Chronicle.BioCap})") : ""), cw, 12))
            { T(x, y + 12, line, Dim, 12); y += 15; }
        else { T(x, y + 12, Loc.T("the biography starts next tick (the selected creature is tracked)", "биография начнётся со следующего тика (выбранное существо отслеживается)"), Dim, 12); y += 15; }
        y += 6;

        // Parent.
        string parentLabel = Loc.T("parent: ", "родитель: ");
        float lx = x + TW(parentLabel, 12);
        T(x, y + 12, parentLabel, Dim, 12);
        if (a.ParentId <= 0) T(lx, y + 12, Loc.T("none — lineage founder", "нет — основатель линии") + (a.Designed ? Loc.T(" (planted by the player)", " (посажен игроком)") : ""), Fg, 12);
        else if (mine && f.SelParent is { } parent) Link(lx, y + 12, Loc.T($"#{parent.Id} — alive, show", $"#{parent.Id} — жив, показать"), () => { Main.Focus(parent); });
        else if (view.FossilOf(a.ParentId) is { } pf) Link(lx, y + 12, Loc.T($"#{a.ParentId} — fossil", $"#{a.ParentId} — окаменелость"), () => ui.Fossil.Show(pf));
        else T(lx, y + 12, Loc.T($"#{a.ParentId} — not preserved", $"#{a.ParentId} — не сохранился"), Dim, 12);
        y += 18;

        // Tracked ancestors up to the founder.
        var chain = new List<long>();
        for (long id = a.TrackedAncestor; id > 0 && chain.Count < 8; id = view.Ancestry.TryGetValue(id, out var n) ? n.TrackedParent : 0) chain.Add(id);
        if (chain.Count > 0)
        {
            T(x, y + 12, Loc.T("tracked ancestors:", "предки под наблюдением:"), Dim, 12);
            lx = x + TW(Loc.T("tracked ancestors: ", "предки под наблюдением: "), 12);
            foreach (long id in chain)
            {
                string mark = id == chain[^1] && view.Ancestry.TryGetValue(id, out var n0) && n0.TrackedParent == 0 ? Loc.T(" (founder)", " (основатель)") : "";
                long target = id;
                if (lx > x + cw - 120) { y += 16; lx = x + 12; }
                if (view.FossilOf(id) is { } af) lx = Link(lx, y + 12, $"#{id}{mark}", () => ui.Fossil.Show(af)) + 8;
                else if (Main.FindAlive(id) is { } alive) lx = Link(lx, y + 12, $"#{id}{mark}", () => Main.Focus(alive)) + 8;
                else { T(lx, y + 12, $"#{target}{mark}", Dim, 12); lx += TW($"#{target}{mark}", 12) + 8; }
            }
            y += 18;
        }

        // Children alive (and how many are gone).
        int kidsAlive = mine ? f.SelKids.Count : 0;
        string kidsLabel = Loc.T($"children {a.NChildren}", $"детей {a.NChildren}") + (mine ? Loc.T($", alive {kidsAlive}", $", живы {kidsAlive}") : "");
        T(x, y + 12, kidsLabel + (kidsAlive > 0 ? ":" : ""), Dim, 12);
        lx = x + TW(kidsLabel + ": ", 12);
        if (mine)
            for (int k = 0; k < Math.Min(kidsAlive, 18); k++)
            {
                var kid = f.SelKids[k];
                string s = $"#{kid.Id}";
                if (lx + TW(s, 12) > x + cw) { y += 16; lx = x + 12; }
                lx = Link(lx, y + 12, s, () => Main.Focus(kid)) + 8;
            }
        if (kidsAlive > 18) T(lx, y + 12, Loc.T($"and {kidsAlive - 18} more", $"и ещё {kidsAlive - 18}"), Dim, 12);
        y += 18;
        if (a.Dead && view.FossilOf(a.Id) is { } own) { Link(x, y + 12, Loc.T("open the fossil", "открыть окаменелость"), () => ui.Fossil.Show(own)); y += 18; }
        y += 4;

        // Energy and mass over the last ticks.
        int hc = mine ? f.SelHistN : 0;
        if (hc > 1)
        {
            float gh = 40, me = 1f, mm = 1f;
            for (int k = 0; k < hc; k++) { me = Math.Max(me, f.SelHist[k]); mm = Math.Max(mm, f.SelMass[k]); }
            if (y > clipTop && y + gh < clipBot)
            {
                DrawRect(new Rect2(x, y + 2, cw, gh), new Color(1, 1, 1, 0.04f));
                var pe = new Vector2[hc];
                var pm = new Vector2[hc];
                for (int k = 0; k < hc; k++)
                {
                    float px = x + k * cw / (SimRunner.SelHistCap - 1f);
                    pe[k] = new Vector2(px, y + 2 + gh - Math.Max(0, f.SelHist[k]) / me * (gh - 2));
                    pm[k] = new Vector2(px, y + 2 + gh - Math.Max(0, f.SelMass[k]) / mm * (gh - 2));
                }
                DrawPolyline(pm, new Color(0.45f, 0.7f, 1f), 1.3f, true);
                DrawPolyline(pe, new Color(0.4f, 0.9f, 0.5f), 1.3f, true);
            }
            T(x + 4, y + 13, Loc.T($"energy (green, max {me:0}) and mass (blue, max {mm:0}) over {hc * 5} ticks", $"энергия (зелёная, макс {me:0}) и масса (синяя, макс {mm:0}) за {hc * 5} тиков"), Dim, 10);
            y += gh + 8;
        }

        // The biography itself, newest first.
        y = Section(x, y, Loc.T("Life", "Жизнь") + (mine && f.SelBioN > 0 ? Loc.T($" — {f.SelBioN} entries, newest first", $" — {f.SelBioN} записей, новые сверху") : ""));
        if (!mine || f.SelBioN == 0) { T(x, y + 10, Loc.T("empty so far", "пока пусто"), Dim, 12); return y + 24; }
        var ch = w.Chem;
        for (int k = f.SelBioN - 1; k >= 0; k--)
        {
            var e = f.SelBio[k];
            T(x, y + 10, Loc.T($"{Chronicle.Day(e.Tick)} · tick {e.Tick}", $"{Chronicle.Day(e.Tick)} · тик {e.Tick}"), Dim, 11);
            float tx2 = x + 150;
            var c = e.Kind switch { BioKind.Death or BioKind.Killed => new Color(1f, 0.55f, 0.5f), BioKind.Event or BioKind.Record => Acc, BioKind.Born or BioKind.Child => new Color(0.5f, 0.95f, 0.55f), _ => Fg };
            foreach (var line in Wrap(Chronicle.BioText(e, ch), cw - 150, 12)) { T(tx2, y + 10, line, c, 12); y += 15; }
            y += 1;
        }
        return y + 20;
    }

    float Section(float x, float y, string title)
    {
        T(x, y + 14, title, Fg, 13, bold);
        return y + 20;
    }

    // A few words on its way of life, put together from what it has actually done.
    static string LifeStory(Agent a)
    {
        var parts = new List<string>();
        parts.Add(a.NMoves == 0 ? Loc.T("motionless", "неподвижен") : a.NMoves < 20 ? Loc.T("barely moves", "почти не двигается") : Loc.T($"mobile ({a.NMoves} steps)", $"подвижен ({a.NMoves} шагов)"));
        if (a.Cells > 1) parts.Add(Loc.T($"spans {a.Cells} cells", $"занимает {a.Cells} клеток"));
        parts.Add(a.NChildren + a.NMates == 0 ? Loc.T("does not reproduce", "не размножается")
                  : Loc.T($"offspring {a.NChildren}", $"потомков {a.NChildren}") + (a.NMates > 0 ? Loc.T($", mated {a.NMates} times", $", спаривался {a.NMates} раз") : ""));
        // Over its whole life (a body that eats rarely but a lot looks idle most of the time)...
        float age = Math.Max(1, a.Age);
        var life = new List<(float v, string s)>
        {
            (a.GainPhoto / age, Loc.T("catches light", "ловит свет")),
            (a.GainChem / age, Loc.T("splits and joins molecules", "расщепляет и соединяет молекулы")),
            (a.GainMine * 0.3f / age, Loc.T("eats soil", "ест почву")),
            (a.LifeGot / age, Loc.T("is fed by others", "его кормят другие")),
        };
        var lived = life.Where(t => t.v > 0.002f).OrderByDescending(t => t.v).Select(t => t.s).ToList();
        parts.Add(lived.Count == 0 ? Loc.T("got almost nothing over its life", "за жизнь почти ничего не добыл") : Loc.T("lives by: ", "живёт так: ") + string.Join(", ", lived));
        int meals = a.NBind + a.NSplit;
        if (meals > 0 && a.Age / meals >= 50) parts.Add(Loc.T($"eats rarely but a lot (a reaction every {a.Age / meals} t.)", $"ест редко и помногу (реакция раз в {a.Age / meals} т.)"));
        // ...and lately.
        var now = new List<(float v, string s)>
        {
            (a.EmaPhoto, Loc.T("catches light", "ловит свет")),
            (a.EmaChem, Loc.T("chemistry", "химия")),
            (a.EmaMine * 0.3f, Loc.T("soil", "почва")),
            (a.EmaGot, Loc.T("being fed", "кормят")),
        };
        var eat = now.Where(t => t.v > 0.002f).OrderByDescending(t => t.v).Select(t => t.s).ToList();
        parts.Add(eat.Count == 0 ? Loc.T("lately gets almost nothing", "последнее время почти ничего не добывает") : Loc.T("now: ", "сейчас: ") + string.Join(", ", eat));
        if (a.EmaAttack > 0.002f) parts.Add(Loc.T("hunts", "охотится"));
        if (a.Links.Count > 0) parts.Add(Loc.T($"in a colony ({a.Links.Count} links)", $"в колонии ({a.Links.Count} связей)"));
        if (a.NInjects > 0) parts.Add(Loc.T("writes its genes into others", "вписывает свои гены другим"));
        if (a.EnzN > 0) parts.Add(Loc.T($"makes proteins ({a.EnzN} kinds)", $"делает белки ({a.EnzN} видов)"));
        parts.Add(a.EmaNet > 0.001f ? Loc.T("saving energy", "копит энергию") : a.EmaNet < -0.001f ? Loc.T("drawing on its store", "тратит запас") : Loc.T("holding in balance", "держится в равновесии"));
        return string.Join(" · ", parts);
    }

    // Wrapped lines are kept: the long explanations are the same every redraw.
    readonly Dictionary<(string, float, int, Font), List<string>> wrapped = new();

    List<string> Wrap(string text, float width, int size, Font f = null)
    {
        var key = (text, width, size, f);
        if (wrapped.TryGetValue(key, out var cached)) return cached;
        if (wrapped.Count > 256) wrapped.Clear();
        var lines = wrapped[key] = new List<string>();
        var cur = "";
        foreach (var word in text.Split(' '))
        {
            var t = cur.Length == 0 ? word : cur + " " + word;
            if (TW(t, size, f) > width && cur.Length > 0) { lines.Add(cur); cur = word; }
            else cur = t;
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }
}
