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
    static readonly string[] Seasons = { "весна", "лето", "осень", "зима" };
    static readonly string[] Causes = { "", "голод", "убит", "распался", "мороз или жара", "обвал", "рука (кисть)" };
    static readonly (EvKind k, string name)[] EvShow =
    {
        (EvKind.Divide, "деления"), (EvKind.Attack, "атаки"), (EvKind.Kill, "убийства"),
        (EvKind.Take, "кражи"), (EvKind.Give, "дары"), (EvKind.Share, "энергия →"),
        (EvKind.Link, "сцепки"), (EvKind.Inject, "инъекции"), (EvKind.Cut, "вырезания"),
        (EvKind.Dig, "раскопки"), (EvKind.Pile, "постройки"), (EvKind.Mine, "добыча"),
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

    ImageTexture Portrait(Agent a)
    {
        if (portraits.Count > 300) portraits.Clear();
        if (!portraits.TryGetValue(a.Hash, out var t))
            portraits[a.Hash] = t = ImageTexture.CreateFromImage(Primordium.Portrait.Render(a.G, 48));
        return t;
    }

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
        TopBar(w, px);
        Hints(vs, px);

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
        T(x, y, $"Промотка до суток {Main.FastTo / P.DayLen + 1}", Fg, 18, bold);
        DrawRect(new Rect2(x, y + 14, bw, 10), new Color(1, 1, 1, 0.08f));
        DrawRect(new Rect2(x, y + 14, bw * (float)Math.Clamp(f, 0, 1), 10), new Color(0.4f, 0.9f, 0.5f));
        T(x, y + 46, $"сутки {w.Day + 1} · тик {w.Tick:N0} · особей {w.Agents.Count:N0} · {Main.Tps:F0} тиков/с · поколений {w.MaxGen}", Dim, 13);
        T(x, y + 66, "T или Esc — остановить", Dim, 12);
    }

    // The most remarkable living bodies; click one to look at it.
    void DrawRecords(Vector2 vs)
    {
        var rec = Main.Records;
        float w = 440, h = 34 + rec.Count * 20, x = 16, y = vs.Y - 82 - h;
        DrawRect(new Rect2(x, y, w, h), new Color(0.04f, 0.045f, 0.06f, 0.94f));
        T(x + 12, y + 20, "Рекорды · клик — показать (B — скрыть, O — старейший)", Fg, 13, bold);
        var m = GetViewport().GetMousePosition();
        for (int k = 0; k < rec.Count; k++)
        {
            var (name, a, value) = rec[k];
            float ry = y + 40 + k * 20;
            var row = new Rect2(x + 4, ry - 14, w - 8, 19);
            hits.Add((row, a));
            if (row.HasPoint(m)) DrawRect(row, new Color(1, 1, 1, 0.06f));
            DrawCircle(new Vector2(x + 17, ry - 4), 5, View3D.KinColor(a));
            T(x + 30, ry, name, Dim, 12);
            T(x + 210, ry, $"#{a.Id} · {value}", a == Main.View.Selected ? Acc : Fg, 12);
        }
    }

    // What everything on the map means.
    void DrawLegend(Vector2 vs)
    {
        (Color c, string s)[] rows =
        {
            (new(0.75f, 0.75f, 0.8f), "форма и цвет тела — семья: родня похожа, ветви расходятся по цвету"),
            (new(0.4f, 0.9f, 0.35f), "шапочка: питается светом"),
            (new(0.35f, 0.62f, 1f), "шапочка: химией (расщепляет и соединяет молекулы)"),
            (new(1f, 0.65f, 0.25f), "шапочка: добывает молекулы из агрегатов"),
            (new(1f, 0.2f, 0.15f), "шапочка: охотник"),
            (new(0.6f, 0.6f, 0.64f), "шапочка: почти бездействует"),
            (new(0.4f, 0.95f, 0.5f), "кубик сбоку: белки — зелёный свет, синий химия, красный мотор"),
            (new(1f, 0.85f, 0.4f), "мелкие цветные кубики, разлетающиеся от тел — выброшенные молекулы"),
            (new(1f, 0.3f, 0.2f), "красный луч — удар, красная вспышка — убийство"),
            (new(0.85f, 0.3f, 1f), "фиолетовый луч — вставка генов"),
            (new(0.3f, 1f, 1f), "бирюзовая перемычка — связь колонии"),
            (new(0.55f, 0.55f, 0.6f), "серый тающий кубик — смерть"),
            (new(1f, 0.42f, 0.12f), "оранжевый шар — вулкан"),
            (new(1f, 0.55f, 1f), "розовый столб с кольцом — удар с орбиты"),
            (new(0.2f, 0.45f, 0.75f), "прозрачный синий — вода, светло-голубой — лёд, белый — снег"),
            (new(0.3f, 0.55f, 0.9f), "в воде тело без газа тонет и ходит по дну; газ в теле — пузырь, с ним всплывает. У поверхности свет, у дна еда"),
            (new(0.5f, 0.33f, 0.17f), "цвет породы — её молекулы; полости и своды видны в разрезе (C)"),
            (new(0.58f, 0.45f, 0.3f), "тонкий верхний блок — его уже объели; съеденный до конца блок исчезает"),
            (new(0.45f, 0.4f, 0.6f), "большое тело и тонированный пол под ним — существо, занявшее несколько клеток"),
        };
        float w = 520, h = 34 + rows.Length * 18, x = 16, y = vs.Y - 82 - h;
        DrawRect(new Rect2(x, y, w, h), new Color(0.04f, 0.045f, 0.06f, 0.94f));
        T(x + 12, y + 20, "Легенда (H — скрыть)", Fg, 13, bold);
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
        string l1 = $"#{a.Id} · {Looks.ShapeNames[a.Shape]} · линия #{a.Lineage} · поколение {a.Gen}" + (a.Cells > 1 ? $" · на {a.Cells} клетках" : "");
        string l2 = $"{DietName(a)} · энергия {Math.Max(0, a.Energy):F0} (удобный запас {a.Store:F0}) · возраст {a.Age:N0}";
        int cell = a.Y * World.W + a.X;
        string l3 = $"клетка {w.Temp[cell]:+0;-0} °C{CaveBrief(w, a)}, тело {a.Tb:+0;-0} °C · соседей {w.Count[cell] - 1}, место занято на {(Main.Frame.Hover == a ? Main.Frame.HoverFloorFill : 0):P0}" + (w.Water[cell] > 0.05f ? $" · вода {w.Water[cell]:0.0}" : "") +
                    $" · белков {a.EnzN} ({EnzymeBrief(a)})";
        var sel = Main.View.Selected;
        string l4 = sel != null && sel != a ? (Looks.Kin(a, sel) > 0 ? $"родня выбранного · близость {Looks.Kin(a, sel) * 100:F0}%" : "не родня выбранному")
                                            : "клик — выбрать и увидеть всю родню";
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
        if (l + c + m < 0.05f) return "нет";
        return string.Join(", ", new[] { (l, "свет"), (c, "химия"), (m, "мотор") }.Where(t => t.Item1 >= 0.05f).Select(t => $"{t.Item2} {t.Item1:0.0}"));
    }

    static string DietName(Agent a) => World.Diet(a) switch
    {
        World.DietPlant => "питается светом",
        World.DietEater => "питается химией",
        World.DietMiner => "ест почву",
        World.DietHunter => "охотник",
        _ => "почти бездействует",
    };

    void TopBar(World w, float px)
    {
        DrawRect(new Rect2(0, 0, px, 98), BarBg);
        int season = (int)(w.YearFrac * 4) % 4;
        float tod = w.DayFrac;
        T(16, 24, $"Сутки {w.Day + 1} · {Seasons[season]} на севере · тик {w.Tick:N0}", Fg, 16, bold);
        string speed = (Main.Paused ? "ПАУЗА" : $"скорость ×{Main.Tpf} · {Main.Tps:F0} тиков/с") + $" · тик {Main.SimMs:F1} мс · кадр {Main.ViewMs:F1} мс";
        float sx = px - 16 - TW(speed, 14, bold);
        T(sx, 24, speed, Main.Paused ? Acc : Fg, 14, bold);

        // Sun clock: where the sun stands over the planet right now.
        float cx = sx - 130, cy = 19;
        DrawRect(new Rect2(cx, cy - 2, 100, 4), new Color(0.15f, 0.17f, 0.22f));
        DrawCircle(new Vector2(cx + tod * 100, cy), 5, new Color(1f, 0.85f, 0.35f));

        var c = Main.Census;
        string tail = $" · без родителей {w.Spawns:N0} (первые и самозарождённые) · поколений {w.MaxGen} · старейший {c.OldestAge:N0} т.";
        string line = $"особей {c.Pop:N0} · рождено {w.Births:N0} · умерло {w.Deaths:N0} (голод {w.DeathsStarve:N0}, убиты {w.DeathsKilled:N0}, распад {w.DeathsBroken:N0}, климат {w.DeathsClimate:N0}{(w.DeathsHand > 0 ? $", рукой {w.DeathsHand:N0}" : "")})" + tail;
        if (TW(line) > px - 32) line = $"особей {c.Pop:N0} · рождено {w.Births:N0} · умерло {w.Deaths:N0} (убиты {w.DeathsKilled:N0})" + tail;
        if (TW(line) > px - 32) line = $"особей {c.Pop:N0} · рождено {w.Births:N0} · умерло {w.Deaths:N0}" + tail;
        T(16, 44, line, Fg, 13);
        var v = Main.View;
        string mode = $"поверхность: {OverlayName(w, v.Overlay)} · окраска: {View3D.ColorModeNames[v.ColorMode]} · " +
                      $"{(v.Lighting ? "освещение солнца" : "без освещения")}{(v.Slice >= 0 ? $" · разрез по строке {v.Slice}" : "")}{(v.Follow ? " · слежу за агентом" : "")}" +
                      $" · самозарождение {(w.Abiogenesis ? "вкл" : "выкл")} (A) · удары с орбиты {(w.AutoStrikes ? "вкл" : "выкл")} (⇧X), было {w.StrikeCount}";
        T(16, 60, mode, Dim, 12);
        string skyLine = SkyLine(w, v.CursorCell, true);
        if (TW(skyLine, 12) > px - 32) skyLine = SkyLine(w, v.CursorCell, false);
        T(16, 76, skyLine, Dim, 12);
        // The climate epoch (World.ClimateCycles): ice age or interglacial, the phase of the cycles, ash, droughts.
        string epoch = w.EpochLine(true);
        if (TW(epoch, 12) > px - 32) epoch = w.EpochLine(false);
        T(16, 92, epoch, w.IceAgeNow || w.VolcanicWinterNow ? Acc : Dim, 12);
    }

    // The sky (World.Sky): the sun's activity and flares, the next eclipse, and the column under the cursor —
    // latitude, day length, the noon sun, the power now, the transparency. Read-only arithmetic.
    double eclipseAskedAt = -10; long eclipseNext = -1; World eclipseWorld;
    string SkyLine(World w, int cell, bool full)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (now - eclipseAskedAt > 2 || eclipseWorld != w) { eclipseAskedAt = now; eclipseWorld = w; eclipseNext = w.NextEclipse(w.Tick, 200); }
        string sun = World.FlareLaw ? $"солнце: активность {w.SolarActivity:P0}" + (w.FlarePower > 0 ? $", ВСПЫШКА {w.FlarePower:0.0}" : "") + (full ? $", вспышек было {w.FlareCount}" : "") : "вспышки выключены";
        string ecl = !World.EclipseLaw ? "затмения выключены" : w.EclipseNow ? $"ЗАТМЕНИЕ: тень в ({w.EclipseX:0}, {w.EclipseY:0})"
                   : eclipseNext > 0 ? $"затмение через {(eclipseNext - w.Tick) / (float)P.DayLen:0.0} сут" : "затмений нет 200 суток";
        string here = "";
        if (cell >= 0)
        {
            int y = cell / World.W;
            float lat = World.Latitude(y), decl = w.SunDecl, deg = lat * 180 / MathF.PI;
            here = full ? $" · ({cell % World.W}, {y}): широта {MathF.Abs(deg):0.0}° {(deg >= 0 ? "с." : "ю.")}, день {World.DayShare(lat, decl) * 24:0.0} ч из 24, " +
                          $"солнце в полдень {World.NoonElevation(lat, decl):0}°, мощность {w.Sun[cell]:0.00} (у дна {w.Light[cell]:0.00}), прозрачность {w.Transp[cell]:0.00}"
                        : $" · ({cell % World.W}, {y}): {MathF.Abs(deg):0}° {(deg >= 0 ? "с." : "ю.")}, день {World.DayShare(lat, decl) * 24:0.0} ч, полдень {World.NoonElevation(lat, decl):0}°, мощность {w.Sun[cell]:0.00}, прозр. {w.Transp[cell]:0.00}";
        }
        return sun + " · " + ecl + here;
    }

    // Where a body is: underground, on dry ground, or in water — on the bottom, swimming, at the surface
    // (with its density against the water's: what decides whether it rises or sinks).
    static string Where(World w, Agent a)
    {
        int cell = a.Y * World.W + a.X;
        if (w.InCave(a)) return "под землёй";
        if (!w.InWater(a)) return "поверхность";
        string pos = World.OnFloor(a) ? $"на дне, до поверхности {w.Water[cell]:0.0}"
                   : w.AtSurface(a, cell) ? "плывёт у поверхности"
                   : $"плывёт в толще: над дном {a.Lift:0.0}, до поверхности {w.Below(a, cell):0.0}";
        string drift = a.Density < P.WaterDensity ? "легче воды — всплывает" : a.Density > P.WaterDensity ? "тяжелее воды — тонет" : "как вода";
        return $"в воде, {pos} · плотность {a.Density:0.00} ({drift})";
    }

    // Under a roof the body feels the cave climate (World.Cave). Read-only arithmetic on the world's arrays.
    static string CaveBrief(World w, Agent a)
    {
        int cell = a.Y * World.W + a.X;
        if (!w.InCave(a)) return "";
        return $", под крышей {w.Roof(cell, a.Z)} бл.: вокруг {w.LocalTemp(cell, a.Z):+0;-0} °C";
    }

    static (string, string) CaveLines(World w, Agent a)
    {
        int cell = a.Y * World.W + a.X;
        float surf = w.Temp[cell];
        if (!w.InCave(a)) return ($"крыши нет · вокруг {surf:+0;-0} °C (поверхность)", null);
        int roof = w.Roof(cell, a.Z);
        string law = World.CaveLaw ? "" : " (закон климата пещер выключен)";
        return ($"крыша {roof} бл. · укрытие {w.Cover(cell, a.Z):P0} · глубина {Math.Max(0, w.Height[cell] - 1 - a.Z)} ур." + law,
                $"вокруг {w.LocalTemp(cell, a.Z):+0.0;-0.0} °C: климат глубины {w.CaveTemp(cell, a.Z):+0.0;-0.0}, поверхность {surf:+0.0;-0.0}");
    }

    static string OverlayName(World w, int o) => o switch
    {
        0 => "породы и почва",
        1 => "температура",
        2 => "мощность солнца у поверхности",
        View3D.TranspOverlay => (World.TranspLaw ? $"прозрачность атмосферы (в среднем {w.TranspMean:0.00})" : "прозрачность атмосферы (закон выключен: небо везде ясное)")
                                + (w.VeilTransMean < 0.999f ? $", пепел: свет {w.VeilTransMean:P0}" : ""),
        View3D.DayOverlay => "длина дня: ночь — чёрный, 12 ч — зелёный, полярный день — светлый",
        View3D.FlareOverlay => World.FlareLaw ? (w.FlarePower > 0 ? $"доза вспышки на открытой поверхности (мощность {w.FlarePower:0.0})" : $"доза вспышки: вспышки нет, тускло — куда достаёт активность солнца ({w.SolarActivity:P0})") : "доза вспышки (вспышки выключены)",
        3 => "останки на земле",
        4 => "плотность жизни",
        5 => "недавние смерти",
        6 => "тепло от тел",
        7 => "нагрузка / прочность (зелёный → красный)",
        8 => "порядок решётки (тёмный → светлый)",
        View3D.DepthTempOverlay => P.CaveClimate == 0 ? "температура на глубине (закон выключен: везде как на поверхности)"
                                  : "температура на глубине: сверху — средняя за год, в разрезе (C) — что чувствует тело на каждом уровне",
        View3D.DeepOverlay => $"глубинный элемент {w.Chem.ElementName[w.DeepElement]} (смещение {w.DepthBias[w.DeepElement]:+0.00;-0.00}): доля в атомах верхнего блока, в разрезе (C) — по уровням, жилы светлее"
                              + (w.GeoOn ? "" : " (мир создан без профиля глубины)"),
        View3D.StockOverlay => "запас еды и газа: рыхлое на земле — зелёный → жёлтый, газ воздуха — синий, чёрное — выедено"
                               + (P.GasDiffK != 1 ? $" (диффузия газа ×{P.GasDiffK:0.##})" : ""),
        _ when o - View3D.FirstSpecies == w.Chem.Gas => $"газ {w.Chem.Name[w.Chem.Gas]} в воздухе (E{w.Chem.E[w.Chem.Gas]})",
        _ => $"молекула {w.Chem.Name[o - View3D.FirstSpecies]} на земле (E{w.Chem.E[o - View3D.FirstSpecies]}" +
             $"{(w.Chem.Poison[o - View3D.FirstSpecies] ? ", яд" : w.Chem.Solid[o - View3D.FirstSpecies] ? ", твёрдая" : "")})",
    };

    void Hints(Vector2 vs, float px)
    {
        DrawRect(new Rect2(0, vs.Y - 74, px, 74), BarBg);
        var m = Main;
        if (m.Tool > 0)
        {
            // The hand's brush is out: what it does and how to steer it.
            string what = m.Tool switch
            {
                1 => $"насыпать: {m.World.Chem.MatName[m.PourSpecies + 2]} (каждый мазок — новый случайный материал, свежая рыхлая насыпь)",
                2 => "залить водой (дальше она течёт, испаряется и выпадает дождём как обычная)",
                3 => "убить всех в круге (останки остаются на месте)",
                5 => m.Ui.Creator.BrushText,
                _ => "копнуть: снять верхние блоки (вещество уходит из мира с рукой)",
            };
            string l1 = $"кисть — {what}";
            string l2 = m.Tool == 5
                ? $"клик — посадить в эту клетку · разброс {m.BrushR:0} ([ ]) · F7 — конструктор · 5, 0 или Esc — убрать"
                : $"радиус {m.BrushR:0} · ЛКМ — рисовать · [ ] — размер · 1–5 — другой инструмент · та же цифра, 0 или Esc — убрать";
            float tw = Math.Min(px - 20, Math.Max(TW(l1, 13), TW(l2, 12)) + 40);
            DrawRect(new Rect2(10, vs.Y - 120, tw, 42), new Color(0.08f, 0.09f, 0.12f, 0.92f));
            float tx = 22;
            if (m.Tool == 1)
            {
                var col = m.World.Chem.MatCol[m.PourSpecies + 2];
                DrawRect(new Rect2(18, vs.Y - 112, 12, 12), new Color(col.R, col.G, col.B));
                tx = 36;
            }
            T(tx, vs.Y - 101, l1, Fg, 13);
            T(22, vs.Y - 85, l2, Dim, 12);
        }
        T(16, vs.Y - 56, "Space пауза · . шаг · +/− скорость · T промотка на N суток, ⇧T на 10 · R новый мир, ⇧R тот же · P снимок · 1–4 кисть · F3 замер", Dim, 12);
        T(16, vs.Y - 40, "F2 законы мира · F4 новый мир · F5 быстро сохранить, F9 загрузить · F6 сохранения · F7 конструктор существ · 5 посадить дизайн · F8 хроника", Dim, 12);
        T(16, vs.Y - 24, "ЛКМ / WASD / два пальца — сдвиг · ПКМ / Q E — поворот · колесо / щипок — зум · G вся карта · C разрез, [ ] сдвиг", Dim, 12);
        T(16, vs.Y - 8, "клик — агент · H легенда · B рекорды · O старейший · K родня · F следить · V окраска · M поверхность · L свет · N жизнь · A абиогенез · X удар", Dim, 12);
    }

    float Header(float x, float y, string s)
    {
        T(x, y + 12, s, Fg, 14, bold);
        return y + 20;
    }

    float Graph(float x, float y, float cw)
    {
        y = Header(x, y, "Население");
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
            T(x + 6, y + 14, $"макс {mx:N0}", Dim, 11);
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
        Leg(Fg, $"все {c.Pop:N0}");
        Leg(CPlant, $"свет {c.Plants:N0}");
        Leg(CEater, $"химия {c.Eaters:N0}");
        Leg(CMiner, $"почва {c.Miners:N0}");
        Leg(CHunter, $"охота {c.Hunters:N0}");
        if (c.InWater > 0) Leg(new Color(0.3f, 0.55f, 0.9f), $"в воде {c.InWater:N0} (плывут {c.Afloat:N0})");
        return y + 16;
    }

    float Events(float x, float y, float cw)
    {
        y = Header(x, y, "События за последние 1000 тиков");
        float colw = cw / 3;
        for (int k = 0; k < EvShow.Length; k++)
        {
            float cx = x + (k % 3) * colw, cy = y + 12 + (k / 3) * 17;
            long v = Main.Frame.EvRate[(int)EvShow[k].k];
            T(cx, cy, EvShow[k].name, Dim, 12);
            string s = v.ToString("N0");
            T(cx + colw - 10 - TW(s, 12, bold), cy, s, v > 0 ? Fg : Dim, 12, bold);
        }
        return y + 12 + 4 * 17 + 6;
    }

    float ClimatePanel(float x, float y, float cw)
    {
        var c = Main.Climate;
        if (c == null) return y;
        y = Header(x, y, "Климат");
        T(x, y + 12, $"t° средняя {c.MeanT:+0;-0} °C · от {c.MinT:+0;-0} до {c.MaxT:+0;-0} · дождь над {c.RainShare:P0}", Fg, 12);
        T(x, y + 29, $"вода {c.WaterShare:P0} · лёд {c.IceShare:P0} · снег {c.SnowShare:P0} поверхности", Dim, 12);
        return y + 38;
    }

    float Organs(float x, float y, float cw)
    {
        y = Header(x, y, "Белки — сколько на особь (средн.)");
        var c = Main.Census;
        float colw = cw / 4;
        string[] names = { "соединение", "расщепл.", "свет", "мотор" };
        for (int k = 0; k < 4; k++)
        {
            float cx = x + k * colw, cy = y + 12;
            T(cx, cy, names[k], Dim, 12);
            float v = c.EnzKind[k];
            string s = v < 0.05f && v > 0 ? "<0.1" : v.ToString("0.0");
            T(cx + colw - 8 - TW(s, 12, bold), cy, s, v > 0 ? Fg : Dim, 12, bold);
        }
        T(x, y + 29, $"разных белков {c.AvgEnz:0.0} · закреплено генома {c.AvgProt:P0} · тело {c.AvgTb:+0;-0} °C · больших тел {c.Big:N0} (до {c.MaxCells} клеток)", Dim, 12);
        return y + 12 + 2 * 17 + 8;
    }

    float Lineages(float x, float y, float cw)
    {
        y = Header(x, y, "Крупнейшие линии · клик — показать");
        foreach (var (lin, n, gen, rep) in Main.Lineages)
        {
            var row = new Rect2(x - 4, y, cw + 8, 33);
            hits.Add((row, rep));
            if (row.HasPoint(GetViewport().GetMousePosition())) DrawRect(row, new Color(1, 1, 1, 0.05f));
            DrawTextureRect(Portrait(rep), new Rect2(x, y + 2, 28, 28), false);
            T(x + 36, y + 14, $"линия #{lin}", Fg, 13, bold);
            T(x + 36, y + 28, $"особей {n:N0} · поколение до {gen} · {Looks.ShapeNames[rep.Shape]} · {DietName(rep)}", Dim, 12);
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
        y = Header(x, y, "Материя: состав, решётка и опора");
        T(x, y + 10, $"обвалы {w.CollapsedBlocks} · погребено {w.DeathsBuried} · реакций под давлением {w.Metamorphoses}", Dim, 11);
        y += 16;
        foreach (var line in Wrap("молекулу из блока надо вырвать: попытка стоит сил (тем больше, чем твёрже порода) и удаётся " +
                                  "с указанным шансом (для блока средней упорядоченности; глубже и плотнее — реже); белок на связь " +
                                  "помогает добыче. Давление упорядочивает решётку; подкоп и слабые стыки разных пород вызывают обвалы, " +
                                  "рыхлое сползает со слишком крутых мест. Блок полон, когда его молекулы заполняют объём; поверхность " +
                                  "грызть — процесс: каждая попытка вкладывает работу в грань клетки, работа копится, и молекула выходит, " +
                                  "когда её набралось достаточно — у нескольких грызущих быстрее. Сколько сил стоит молекула (число после " +
                                  "«сил»), решает решётка: рыхлые отложения и смеси дёшевы, упорядоченные прочные кристаллы дороже любой " +
                                  "молекулы, если белок на связь не снимет барьер (→). E — энергия одной молекулы", cw, 11))
        { T(x, y + 10, line, Dim, 11); y += 13; }
        y += 6;
        long tot = Math.Max(1, w.Mined.Sum());
        foreach (int m in Enumerable.Range(1, ch.MatCount - 1).OrderBy(m => w.TypicalBarrier(m, 0.5f)))
        {
            int t = ch.MatTier[m];
            var c = ch.MatCol[m];
            R(new Rect2(x, y + 1, 10, 10), new Color(c.R, c.G, c.B));
            string name = ch.MatName[m].Replace("агрегат ", "");
            T(x + 15, y + 10, name, Fg, 11);
            T(x + 118, y + 10, Grades[t], t >= 3 ? new Color(1f, 0.7f, 0.4f) : Dim, 10);
            int energy = 0;
            energy = t <= 4 ? ch.E[m - 2] : 0;   // per molecule (a full block holds MatCap of them)
            if (t <= 4) T(x + 152, y + 10, $"E{energy}", Dim, 11);
            string need = t > 4 ? "не разрушается"
                : ch.MatKey[m] < 0 ? $"сил {Cost(w.TypicalBarrier(m, 0.5f))} · белок не нужен"
                : $"сил {Cost(w.TypicalBarrier(m, 0.5f))} → {Cost(w.TypicalBarrier(m, 0.5f) * (1 - P.CatalysisMax))} · белок {ch.Name[ch.MatKey[m]]}";
            T(x + 190, y + 10, need, t >= 2 ? Fg : Dim, 11);
            y += 15;
        }
        y += 4;
        string mined = string.Join(" · ", Enumerable.Range(0, 5).Where(k => w.Mined[k] > 0).Select(k => $"{Grades[k]} {w.Mined[k] * 100.0 / tot:0.#}%"));
        if (mined.Length > 0) { T(x, y + 10, "вырвано молекул по сложности породы: " + mined, Dim, 11); y += 16; }
        var firsts = w.Firsts.Where(f => f != null).OrderBy(f => f.Tick).ToList();
        if (firsts.Count == 0) { T(x, y + 10, "открытий пока нет: твёрдую породу ещё никто не научился разрушать белком", Dim, 11); y += 16; }
        foreach (var f in firsts)
        {
            T(x, y + 10, $"открытие: {ch.MatName[f.Mat]} — линия #{f.Lineage}, сутки {f.Tick / P.DayLen + 1}", Acc, 12);
            y += 15;
        }
        return y + 8;
    }

    float ChemPanel(World w, float x, float y, float cw)
    {
        var ch = w.Chem;
        y = Header(x, y, $"Молекулы мира · seed {w.Seed}");
        T(x, y + 10, "E — энергия состояния; * — возбуждение; состав сохраняется", Dim, 11);
        y += 18;
        float colw = cw / 3;
        int rows = (Chemistry.S + 2) / 3;
        for (int s = 0; s < Chemistry.S; s++)
        {
            float cx = x + (s / rows) * colw, cy = y + 12 + (s % rows) * 14;
            var c = ch.Col[s];
            R(new Rect2(cx, cy - 9, 9, 9), new Color(c.R, c.G, c.B));
            var tc = ch.Poison[s] ? new Color(1f, 0.45f, 0.45f) : ch.Solid[s] ? Acc : Dim;
            string label = $"{ch.Name[s]} {ch.Formula(s)} E{ch.E[s]}" + (ch.Poison[s] ? " яд" : ch.Solid[s] ? " твёрд" : s == ch.Gas ? " газ" : "");
            T(cx + 13, cy, label, s == ch.Gas ? new Color(0.6f, 0.85f, 1f) : tc, 11);
        }
        return y + 12 + rows * 14 + 10;
    }

    // Everything about one body. Returns where it ended (for scrolling).
    float AgentPanel(World w, Agent a, float x, float y, float cw, float maxY)
    {
        var ch = w.Chem;
        y = Header(x, y, "Агент · Esc — назад к миру");
        if (y + 66 > clipTop && y < clipBot) DrawTextureRect(Portrait(a), new Rect2(x, y + 2, 64, 64), false);
        float tx = x + 76;
        T(tx, y + 14, $"#{a.Id}" + (a.Dead ? $" · погиб ({Causes[a.Cause]})" : ""), a.Dead ? new Color(1, 0.5f, 0.45f) : Fg, 14, bold);
        T(tx, y + 31, $"линия #{a.Lineage} · поколение {a.Gen} · возраст {a.Age:N0}", Dim, 12);
        float cap = a.Store, e = (float)Math.Max(0, a.Energy);
        R(new Rect2(tx, y + 38, cw - 76, 7), new Color(1, 1, 1, 0.08f));
        R(new Rect2(tx, y + 38, (cw - 76) * Math.Clamp(e / cap, 0, 1), 7), new Color(0.4f, 0.9f, 0.5f));
        T(tx, y + 59, $"энергия {e:F1} (удобный запас {cap:F0}, сверх — утекает быстрее) · {a.LastCycles} тактов/тик", Dim, 12);
        y += 74;
        // The player's creatures: planted from a design, or descended from one.
        string design = Main.Sim.DesignedLineages.TryGetValue(a.Lineage, out var dn) ? dn : null;
        if (a.Designed || design != null)
        {
            string mark = a.Designed ? $"от игрока: посажен из дизайна «{design ?? "?"}»" : $"от игрока: потомок дизайна «{design}» в {a.Gen}-м поколении";
            R(new Rect2(x, y + 1, cw, 18), new Color(1f, 0.82f, 0.4f, 0.1f));
            T(x + 6, y + 14, mark, Acc, 12, bold);
            y += 24;
        }

        y = Tabs(x, y, cw);
        if (AgentTab == 1) return BioPanel(w, a, x, y, cw);

        // How it lives, in words.
        y = Section(x, y, "Как живёт");
        foreach (var line in Wrap(LifeStory(a), cw, 12)) { T(x, y + 10, line, Fg, 12); y += 15; }
        y += 4;

        // Energy budget per tick (smoothed).
        float net = a.EmaNet;
        T(x, y + 10, $"доход: свет → в молекулы +{a.EmaPhoto:0.000} · химия +{a.EmaChem:0.000} · от других +{a.EmaGot:0.000}", Dim, 12);
        T(x, y + 25, $"почва: молекул на {a.EmaMine:0.000} энергии · расход: жизнь −{a.EmaUpkeep:0.000} · климат −{a.EmaHarm:0.000}", Dim, 12);
        T(x, y + 40, $"итого за тик {net:+0.000;-0.000}" + (net > 0.001f ? " — копит" : net < -0.001f ? " — тратит запас" : " — в равновесии"),
            net > 0.001f ? new Color(0.5f, 0.95f, 0.55f) : net < -0.001f ? new Color(1f, 0.55f, 0.5f) : Fg, 12, bold);
        y += 50;
        T(x, y + 10, $"поймал свет {a.NPhoto:N0} · расщепил {a.NSplit:N0} · соединил {a.NBind:N0} · втянул {a.NIntake:N0} · выбросил {a.NExpel:N0}", Dim, 12);
        y += 18;

        // The whole life: what it lived on (the lines above only cover the last ~100 ticks).
        float acts = Math.Max(0, a.LifeStart + a.GainChem + a.LifeGot - a.LifeKids - a.LifeUpkeep - a.LifeHarm - a.LifeSpill - a.LifeUphill - a.LifeMineCost - (float)Math.Max(0, a.Energy));
        int meals = a.NBind + a.NSplit;
        string when = a.LastMeal < 0 ? "реакций с выгодой не было"
            : $"реакция в среднем раз в {a.Age / Math.Max(1, meals):N0} т., последняя " + (a.Dead ? $"за {a.Age - a.LastMeal:N0} т. до смерти" : $"{a.Age - a.LastMeal:N0} т. назад");
        foreach (var line in Wrap($"за жизнь получил: при рождении {a.LifeStart:N0} · химия +{a.GainChem:N0} · от других +{a.LifeGot:N0} · светом запасено в молекулах {a.GainPhoto:N0}", cw, 12))
        { T(x, y + 10, line, Fg, 12); y += 15; }
        foreach (var line in Wrap($"потратил: жизнь {a.LifeUpkeep:N0} · детям {a.LifeKids:N0} · климат {a.LifeHarm:N0} · реакции в минус {a.LifeUphill:N0} · добыча из породы {a.LifeMineCost:N0} · прочие действия {acts:N0} · ушло на удержание запаса {a.LifeSpill:N0}", cw, 12))
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
            T(x + 4, y + 13, $"энергия за {hc * 5} тиков (макс {mx:0})", Dim, 10);
            y += gh + 6;
        }

        // What its program actually spends its time on.
        int total = a.OpCount.Sum();
        if (total > 0)
        {
            var top = Enumerable.Range(0, Genome.OpSlots).Where(k => a.OpCount[k] > 0).OrderByDescending(k => a.OpCount[k]).Take(8)
                .Select(k => $"{Genome.SlotName(k)} {a.OpCount[k] * 100f / total:0}%");
            T(x, y + 10, "чаще всего выполняет:", Dim, 12);
            y += 14;
            foreach (var line in Wrap(string.Join(" · ", top), cw, 12, mono)) { T(x, y + 10, line, Fg, 12, mono); y += 15; }
            y += 4;
        }

        y = Section(x, y, "Тело");
        int nextCells = Math.Min(P.MaxCells, a.Cells + 1);
        string grow = a.Cells >= P.MaxCells ? "предел" : $"{nextCells}-я клетка при массе {P.GrowMass * MathF.Pow(nextCells - 1, P.GrowPow):0}";
        T(x, y + 10, $"{a.InvTotal} молекул (удобно до {a.Room}{(a.Packing > 1 ? $", набит ×{a.Packing:0.0} — держать дороже" : "")}) · масса {a.Mass:F0} · объём {a.Volume:F0} (пол занят на {(Main.Frame.Sel == a ? Main.Frame.SelFloorFill : 0):P0}) · клеток {a.Cells} ({grow})", Dim, 12);
        T(x, y + 25, $"уровень {a.Z} · {Where(w, a)} · тело {a.Tb:+0;-0} °C", Dim, 12);
        var (cave1, cave2) = CaveLines(w, a);
        T(x, y + 40, cave1, Dim, 12);
        if (cave2 != null) { T(x, y + 55, cave2, Dim, 12); y += 15; }
        // The sky over it (World.Sky): what reaches it, what its own matter lets through, the flare dose now.
        T(x, y + 55, $"небо над телом: доходит {w.SkyExposure(a):P0} · экран тела пропускает {w.Shield(a):P0}" + (World.FlareLaw ? $" · доза вспышки {a.FlareDose:0.00}" : ""), Dim, 12);
        y += 30;
        if (y + 30 > clipTop && y + 30 < clipBot) DrawCircle(new Vector2(x + 6, y + 37), 6, View3D.KinColor(a));
        T(x + 18, y + 41, $"облик: {Looks.ShapeNames[a.Shape]} · родни на планете {Main.KinCount:N0}" +
                          (Main.View.KinFocus ? " (подсвечена, K — выкл.)" : " (K — подсветить)"), Fg, 12);
        y += 50;
        var fr = Main.Frame;
        if (fr.Sel == a) T(x, y + 10, $"опора: нагрузка {fr.SelPressure:F1} / прочность {fr.SelCapacity:F1} · порядок {fr.SelOrder:P0}", Dim, 11);
        y += 16;
        float lx = x;
        y += 10;
        foreach (int s in Enumerable.Range(0, Chemistry.S).Where(s => a.Inv[s] > 0).OrderByDescending(s => a.Inv[s]))
        {
            if (lx > x + cw - 40) { lx = x; y += 15; }
            var col = ch.Col[s];
            R(new Rect2(lx, y - 9, 9, 9), new Color(col.R, col.G, col.B));
            T(lx + 12, y, a.Inv[s].ToString(), ch.Poison[s] ? new Color(1f, 0.45f, 0.45f) : ch.Solid[s] ? Acc : Fg, 12);
            lx += 34;
        }
        y += 14;

        // Proteins it carries right now: what they do, how much, how well, at which temperature.
        y = Section(x, y, "Белки" + (a.EnzN == 0 ? " — нет" : ""));
        for (int k = 0; k < a.EnzN; k++)
        {
            var z = a.Enz[k];
            string what = z.Kind switch
            {
                Enzyme.Bind => $"{ch.Name[z.A]} + {ch.Name[z.B]}",
                Enzyme.Split => $"{ch.Name[z.A]} →",
                Enzyme.Photo => $"свет + {ch.Name[z.A]}",
                _ => "",
            };
            T(x + 8, y + 10, $"{Genome.EnzymeKind[z.Kind]} {what} · ×{z.Amount:0.0} · качество {z.Eff * 100:0}% · лучше всего при {z.Topt:+0;-0}°", Dim, 11);
            y += 13;
        }
        y += 4;

        // Prototype aggregate barriers; the local lattice order and composition refine these in Mine.
        y = Section(x, y, "Недра — что ему по силам");
        T(x, y + 10, "вырвал молекул: " + string.Join(" · ", Enumerable.Range(0, 5).Select(k => $"{Grades[k]} {a.NMinedTier[k]}")), Dim, 12);
        y += 16;
        foreach (int m in Enumerable.Range(1, ch.MatCount - 1).Where(m => ch.MatTier[m] >= 2 && ch.MatTier[m] <= 4).OrderBy(m => ch.MatTier[m]))
        {
            float cat = w.Catalysis(a, (byte)m, out _);
            bool hasEnz = a.Enz.Take(a.EnzN).Any(e => e.Kind == Enzyme.Split && e.A == ch.MatKey[m]);
            string state = cat > 0 ? $"сил на молекулу {Cost(w.TypicalBarrier(m, 0.5f))} → {Cost(w.TypicalBarrier(m, 0.5f) * (1 - cat))}"
                : !hasEnz ? $"нет белка на {ch.Name[ch.MatKey[m]]}"
                : "белок не работает при такой температуре";
            T(x + 8, y + 10, $"{ch.MatName[m]} {Grades[ch.MatTier[m]]}: {state}", cat > 0 ? new Color(0.5f, 0.95f, 0.55f) : Dim, 11);
            y += 13;
        }
        y += 4;

        y = Section(x, y, "Поступки");
        T(x, y + 10, $"детей {a.NChildren} · спариваний {a.NMates} · шагов {a.NMoves} · атак {a.NAttacks} · убил {a.NKills}", Dim, 12);
        T(x, y + 25, $"перенёс блоков {a.NDigs} · строил {a.NPiles} · выделял агрегаты {a.NGrows} · грыз породу {a.NMines} · крал {a.NTakes} · дарил {a.NGives}", Dim, 12);
        T(x, y + 40, $"вписал гены {a.NInjects} · заражён {a.NInfected} · вырезал {a.NCuts} · сделал белков {a.NExpress} · облучён {a.NStruck}", Dim, 12);
        y += 50;

        y = Section(x, y, "Процессор");
        string stack = a.Sp == 0 ? "пусто" : string.Join(" ", a.Stack.Take(a.Sp));
        T(x, y + 10, "стек: " + stack, Dim, 12, mono);
        T(x, y + 25, "память: " + string.Join(" ", a.Mem), Dim, 12, mono);
        y += 34;

        // The whole genome at a glance: colour = kind of instruction, brightness = how well proven
        // (protected) the byte is. Settled blocks — "organs" — stand out as bright runs.
        var g = a.G;
        var prot = a.Prot;   // replaced together with G: read both once and stay within both
        int n = Math.Min(g.Length, prot.Length), cur = a.Ip % Math.Max(1, n);
        T(x, y + 10, "геном: цвет — вид команды, яркость — закреплённость", Dim, 11);
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
        T(x, y + 10, "код целиком (→ сейчас, золотом — закреплённое)", Fg, 13, bold);
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
            if (MathF.Abs(m.X - ex) < 3 && m.Y >= y - 2 && m.Y <= y + gh) { tip = $"{Chronicle.Day(e.Tick)}: {e.Text}"; tipX = ex; }
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
        string[] names = { "Обзор", "Биография" };
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
            foreach (var line in Wrap($"биография ведётся: {Chronicle.WhyText(a.TrackWhy)} · записей {a.BioN}" + (a.BioN > Chronicle.BioCap ? $" (хранятся последние {Chronicle.BioCap})" : ""), cw, 12))
            { T(x, y + 12, line, Dim, 12); y += 15; }
        else { T(x, y + 12, "биография начнётся со следующего тика (выбранное существо отслеживается)", Dim, 12); y += 15; }
        y += 6;

        // Parent.
        float lx = x + TW("родитель: ", 12);
        T(x, y + 12, "родитель: ", Dim, 12);
        if (a.ParentId <= 0) T(lx, y + 12, "нет — основатель линии" + (a.Designed ? " (посажен игроком)" : ""), Fg, 12);
        else if (mine && f.SelParent is { } parent) Link(lx, y + 12, $"#{parent.Id} — жив, показать", () => { Main.Focus(parent); });
        else if (view.FossilOf(a.ParentId) is { } pf) Link(lx, y + 12, $"#{a.ParentId} — окаменелость", () => ui.Fossil.Show(pf));
        else T(lx, y + 12, $"#{a.ParentId} — не сохранился", Dim, 12);
        y += 18;

        // Tracked ancestors up to the founder.
        var chain = new List<long>();
        for (long id = a.TrackedAncestor; id > 0 && chain.Count < 8; id = view.Ancestry.TryGetValue(id, out var n) ? n.TrackedParent : 0) chain.Add(id);
        if (chain.Count > 0)
        {
            T(x, y + 12, "предки под наблюдением:", Dim, 12);
            lx = x + TW("предки под наблюдением: ", 12);
            foreach (long id in chain)
            {
                string mark = id == chain[^1] && view.Ancestry.TryGetValue(id, out var n0) && n0.TrackedParent == 0 ? " (основатель)" : "";
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
        T(x, y + 12, $"детей {a.NChildren}" + (mine ? $", живы {kidsAlive}" : "") + (kidsAlive > 0 ? ":" : ""), Dim, 12);
        lx = x + TW($"детей {a.NChildren}" + (mine ? $", живы {kidsAlive}" : "") + ": ", 12);
        if (mine)
            for (int k = 0; k < Math.Min(kidsAlive, 18); k++)
            {
                var kid = f.SelKids[k];
                string s = $"#{kid.Id}";
                if (lx + TW(s, 12) > x + cw) { y += 16; lx = x + 12; }
                lx = Link(lx, y + 12, s, () => Main.Focus(kid)) + 8;
            }
        if (kidsAlive > 18) T(lx, y + 12, $"и ещё {kidsAlive - 18}", Dim, 12);
        y += 18;
        if (a.Dead && view.FossilOf(a.Id) is { } own) { Link(x, y + 12, "открыть окаменелость", () => ui.Fossil.Show(own)); y += 18; }
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
            T(x + 4, y + 13, $"энергия (зелёная, макс {me:0}) и масса (синяя, макс {mm:0}) за {hc * 5} тиков", Dim, 10);
            y += gh + 8;
        }

        // The biography itself, newest first.
        y = Section(x, y, "Жизнь" + (mine && f.SelBioN > 0 ? $" — {f.SelBioN} записей, новые сверху" : ""));
        if (!mine || f.SelBioN == 0) { T(x, y + 10, "пока пусто", Dim, 12); return y + 24; }
        var ch = w.Chem;
        for (int k = f.SelBioN - 1; k >= 0; k--)
        {
            var e = f.SelBio[k];
            T(x, y + 10, $"{Chronicle.Day(e.Tick)} · тик {e.Tick}", Dim, 11);
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
        parts.Add(a.NMoves == 0 ? "неподвижен" : a.NMoves < 20 ? "почти не двигается" : $"подвижен ({a.NMoves} шагов)");
        if (a.Cells > 1) parts.Add($"занимает {a.Cells} клеток");
        parts.Add(a.NChildren + a.NMates == 0 ? "не размножается" : $"потомков {a.NChildren}" + (a.NMates > 0 ? $", спаривался {a.NMates} раз" : ""));
        // Over its whole life (a body that eats rarely but a lot looks idle most of the time)...
        float age = Math.Max(1, a.Age);
        var life = new List<(float v, string s)>
        {
            (a.GainPhoto / age, "ловит свет"),
            (a.GainChem / age, "расщепляет и соединяет молекулы"),
            (a.GainMine * 0.3f / age, "ест почву"),
            (a.LifeGot / age, "его кормят другие"),
        };
        var lived = life.Where(t => t.v > 0.002f).OrderByDescending(t => t.v).Select(t => t.s).ToList();
        parts.Add(lived.Count == 0 ? "за жизнь почти ничего не добыл" : "живёт так: " + string.Join(", ", lived));
        int meals = a.NBind + a.NSplit;
        if (meals > 0 && a.Age / meals >= 50) parts.Add($"ест редко и помногу (реакция раз в {a.Age / meals} т.)");
        // ...and lately.
        var now = new List<(float v, string s)>
        {
            (a.EmaPhoto, "ловит свет"),
            (a.EmaChem, "химия"),
            (a.EmaMine * 0.3f, "почва"),
            (a.EmaGot, "кормят"),
        };
        var eat = now.Where(t => t.v > 0.002f).OrderByDescending(t => t.v).Select(t => t.s).ToList();
        parts.Add(eat.Count == 0 ? "последнее время почти ничего не добывает" : "сейчас: " + string.Join(", ", eat));
        if (a.EmaAttack > 0.002f) parts.Add("охотится");
        if (a.Links.Count > 0) parts.Add($"в колонии ({a.Links.Count} связей)");
        if (a.NInjects > 0) parts.Add("вписывает свои гены другим");
        if (a.EnzN > 0) parts.Add($"делает белки ({a.EnzN} видов)");
        parts.Add(a.EmaNet > 0.001f ? "копит энергию" : a.EmaNet < -0.001f ? "тратит запас" : "держится в равновесии");
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
