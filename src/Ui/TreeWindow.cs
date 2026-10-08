using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Primordium;

// F1: the tree of life (ROADMAP 6.3, 9.3). Two views, both read from what the simulation thread
// publishes (SimObserver):
// - the cladogram of living clades (World.Phylo folded to its significant branches): time runs to the
//   right, a branch starts where its first genotype appeared and lasts until now, thicker with more
//   bodies; a click picks the branch, flies the camera to its oldest living member and lights up its
//   range on the map (overlay "ranges of clades", M);
// - lineages over time (the founders, Agent.Lineage): the band of each lineage is as thick as its
//   population at the samples of this session; speciations and new dominants from the chronicle are
//   marked on it, an extinct one ends with a cross. A click flies to a living member, or opens the
//   lineage's fossil when it is gone.
public partial class TreeWindow : UiWindow
{
    static readonly float[] Shares = { 0.005f, 0.01f, 0.02f, 0.05f };
    TabContainer tabs;
    public CladeChart Clades;
    public LineageChart Lines;
    OptionButton share;
    CheckBox ranges;
    Label info, status;
    public ulong PickHash;
    public long PickOrigin;
    long shownTree = -1, shownLin = -1;
    int overlayBefore;

    public TreeWindow() : base("tree", Loc.T("Tree of life", "Древо жизни"), new Vector2(1000, 640))
    {
        MinSize = new Vector2(640, 400);
        tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        Clades = new CladeChart { Win = this, Name = "clades" };
        Lines = new LineageChart { Win = this, Name = "lineages" };
        tabs.AddChild(Clades);
        tabs.AddChild(Lines);
        tabs.SetTabTitle(0, Loc.T("Cladogram of the living", "Кладограмма живых"));
        tabs.SetTabTitle(1, Loc.T("Lineages over time", "Линии во времени"));
        Body.AddChild(tabs);

        share = UiKit.Options(Loc.T("branches ≥ 0.5% of bodies", "ветви ≥ 0,5% тел"), Loc.T("branches ≥ 1%", "ветви ≥ 1%"), Loc.T("branches ≥ 2%", "ветви ≥ 2%"), Loc.T("branches ≥ 5%", "ветви ≥ 5%"));
        share.Selected = 1;
        share.TooltipText = Loc.T("a clade is drawn as its own branch when it holds at least this share of the population (and at least 3 bodies)",
                                  "клада рисуется своей ветвью, если в ней не меньше этой доли населения (и не меньше 3 тел)");
        share.ItemSelected += _ => { if (Main.Sim != null) Main.Sim.Obs.TreeShare = Shares[Math.Clamp(share.Selected, 0, Shares.Length - 1)]; };
        ranges = UiKit.Check(Loc.T("ranges on the map", "ареалы на карте"), false, on => ShowRanges(on),
            Loc.T("color the map by the branch living there (overlay “ranges of clades”, also under M); the picked branch is bright",
                  "раскрасить карту по ветви, что там живёт (оверлей «ареалы ветвей», он же в M); выбранная ветвь — ярко"));
        var go = UiKit.Button(Loc.T("go to it", "показать"), GoPicked, Loc.T("fly to the picked branch's oldest living member", "к старейшему живому представителю выбранной ветви"));
        var clear = UiKit.Button(Loc.T("unpick", "снять выбор"), () => Pick(null), Loc.T("show all branches on the map again", "снова показать на карте все ветви"));
        info = UiKit.Text("", 12, UiKit.Fg);
        info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        info.ClipText = true;
        Body.AddChild(UiKit.Row(10, share, ranges, go, clear, info));
        status = UiKit.Text("", 12, UiKit.Dim, wrap: true);
        Body.AddChild(status);
    }

    public override void ShowView(string view) => tabs.CurrentTab = view == "lineages" ? 1 : 0;

    protected override void OnOpen()
    {
        shownTree = shownLin = -1;
        if (Main.Sim != null) share.Selected = Math.Max(0, Array.IndexOf(Shares, Main.Sim.Obs.TreeShare));
        ranges.SetPressedNoSignal(Main.View.Overlay == View3D.RangeOverlay);
    }

    void ShowRanges(bool on)
    {
        var v = Main.View;
        if (on && v.Overlay != View3D.RangeOverlay) { overlayBefore = v.Overlay; v.Overlay = View3D.RangeOverlay; }
        else if (!on && v.Overlay == View3D.RangeOverlay) v.Overlay = overlayBefore == View3D.RangeOverlay ? 0 : overlayBefore;
    }

    public override void _Process(double delta)
    {
        if (!Visible || Main.Sim == null) return;
        ranges.SetPressedNoSignal(Main.View.Overlay == View3D.RangeOverlay);
        var t = Main.Sim.Obs.Tree;
        var l = Main.Sim.Obs.Lineages;
        if (t.Version == shownTree && l.Version == shownLin) return;
        shownTree = t.Version; shownLin = l.Version;
        Clades.View = t; Clades.QueueRedraw();
        Lines.View = l; Lines.QueueRedraw();
        UpdateInfo();
        status.Text = t.CellClade == null
            ? Loc.T("building the tree…", "строю дерево…")
            : Loc.T($"cladogram: {t.Nodes.Length} branches of ≥ {t.Threshold} bodies{(t.Raised ? " (threshold raised to fit)" : "")} out of {t.GenotypesAlive} living genotypes ({t.TreeNodes} nodes with their ancestors), {t.Pop} bodies · " +
                    $"lineages: {l.Rows.Length} of {l.Tracked} that ever had ≥ 2 bodies, sampled every {l.Every} ticks since tick {l.Since} (this session) · wheel scrolls · F1 to close",
                    $"кладограмма: {t.Nodes.Length} ветвей от {t.Threshold} тел{(t.Raised ? " (порог поднят, чтобы уместить)" : "")} из {t.GenotypesAlive} живых генотипов ({t.TreeNodes} узлов с предками), {t.Pop} тел · " +
                    $"линии: {l.Rows.Length} из {l.Tracked}, где бывало ≥ 2 тел, замер раз в {l.Every} тиков с тика {l.Since} (этот сеанс) · колесо — прокрутка · F1 — закрыть");
    }

    void UpdateInfo()
    {
        var t = Clades.View;
        int k = PickHash != 0 ? t.Find(PickHash, PickOrigin) : -1;
        if (PickHash != 0 && k < 0 && t.CellClade != null)
        {
            info.Text = Loc.T("the picked branch is no longer a branch of its own (died out or fell under the threshold)", "выбранная ветвь больше не отдельная (вымерла или ушла под порог)");
            info.AddThemeColorOverride("font_color", UiKit.Dim);
            return;
        }
        info.Text = k < 0 ? Loc.T("click a branch: fly to it, see its range", "клик по ветви — к ней и её ареал") : Describe(t.Nodes[k], k);
        info.AddThemeColorOverride("font_color", k < 0 ? UiKit.Dim : UiKit.Fg);
    }

    public static string Describe(CladeNode n, int k) =>
        Loc.T($"branch {k + 1}: lineage #{n.Lineage}{(n.Design != null ? $" (“{Hud.DesignName(n.Design)}”)" : "")} · {n.Count} bodies ({n.Own} not in sub-branches) · {n.Taxa} genotypes · since day {n.Origin / P.DayLen + 1}, {n.Depth} mutation steps from its root",
              $"ветвь {k + 1}: линия #{n.Lineage}{(n.Design != null ? $" («{Hud.DesignName(n.Design)}»)" : "")} · {n.Count} тел ({n.Own} вне подветвей) · {n.Taxa} генотипов · с суток {n.Origin / P.DayLen + 1}, {n.Depth} шагов-мутаций от корня");

    // A branch was clicked (null: nothing picked).
    public void Pick(CladeNode n)
    {
        PickHash = n?.Hash ?? 0;
        PickOrigin = n?.Origin ?? 0;
        if (n != null && PickHash == 0) PickHash = 1;   // a genome hash of 0 is not "none"
        Main.View.RangeHash = PickHash;
        Main.View.RangeOrigin = PickOrigin;
        UpdateInfo();
        Clades.QueueRedraw();
        if (n != null) GoTo(n);
    }

    void GoPicked()
    {
        var t = Clades.View;
        int k = PickHash != 0 ? t.Find(PickHash, PickOrigin) : -1;
        if (k < 0) { Ui.Toast(Loc.T("pick a branch first", "сначала выберите ветвь"), true); return; }
        GoTo(t.Nodes[k]);
    }

    void GoTo(CladeNode n)
    {
        var a = Main.FindAlive(n.RepId) ?? AnyOfLineage(n.Lineage);
        if (a != null) { Main.Focus(a); Main.View.ZoomAt(30); return; }
        Ui.Toast(Loc.T("its members died since the tree was built", "её представители умерли, пока строилось дерево"), true);
    }

    Agent AnyOfLineage(long lineage)
    {
        var f = Main.Frame;
        if (f == null) return null;
        Agent best = null;
        for (int k = 0; k < f.Count; k++)
        {
            var a = f.Agents[k].Ref;
            if (a != null && !a.Dead && f.Agents[k].Lineage == lineage && (best == null || a.Age > best.Age)) best = a;
        }
        return best;
    }

    // A lineage row was clicked: a living member, else its fossil.
    public void GoLineage(LineageRow r)
    {
        var a = r.Alive ? Main.FindAlive(r.RepId) ?? AnyOfLineage(r.Id) : null;
        if (a != null) { Main.Focus(a); Main.View.ZoomAt(30); return; }
        var ch = Main.Sim.Chronicle;
        var f = ch.Fossils.Where(x => x.Lineage == r.Id).OrderByDescending(x => x.Importance).ThenByDescending(x => x.DiedTick).FirstOrDefault();
        if (f == null)
        {
            var e = ch.Events.LastOrDefault(x => x.Type == EvType.Extinction && x.Lineage == r.Id);
            if (e != null) f = ch.FossilFor(e);
        }
        if (f != null) { Ui.Fossil.Show(f); return; }
        Ui.Toast(Loc.T($"lineage #{r.Id} left no fossil (only participants of events and tracked bodies are kept)",
                       $"от линии #{r.Id} не осталось окаменелости (хранятся только участники событий и отслеживаемые)"), true);
    }
}

// Shared by both charts: vertical scrolling with the wheel, the time axis.
public partial class TimeRows : Control
{
    public TreeWindow Win;
    protected Vector2 Mouse = new(-1, -1);
    protected float Scroll, ContentH;
    protected const float Top = 6, AxisH = 18, LabelW = 230;

    public TimeRows() { MouseFilter = MouseFilterEnum.Stop; ClipContents = true; SizeFlagsVertical = SizeFlags.ExpandFill; SizeFlagsHorizontal = SizeFlags.ExpandFill; CustomMinimumSize = new Vector2(480, 300); }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm) { Mouse = mm.Position; QueueRedraw(); }
        else if (e is InputEventMouseButton { Pressed: true } mb)
        {
            if (mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                float max = Math.Max(0, ContentH - (Size.Y - Top - AxisH));
                Scroll = Math.Clamp(Scroll + (mb.ButtonIndex == MouseButton.WheelUp ? -54 : 54), 0, max);
                QueueRedraw();
                AcceptEvent();
            }
            else if (mb.ButtonIndex == MouseButton.Left) { Clicked(mb.Position); AcceptEvent(); }
        }
        else if (e is InputEventPanGesture pg)
        {
            float max = Math.Max(0, ContentH - (Size.Y - Top - AxisH));
            Scroll = Math.Clamp(Scroll + pg.Delta.Y * 20, 0, max);
            QueueRedraw();
            AcceptEvent();
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit) { Mouse = new Vector2(-1, -1); QueueRedraw(); }
    }

    protected virtual void Clicked(Vector2 p) { }

    protected void Axis(long start, long end, float left, float right)
    {
        var font = UiKit.Ui;
        float bottom = Size.Y - AxisH;
        for (int k = 0; k <= 5; k++)
        {
            double t = start + (end - start) * k / 5.0;
            float x = left + (float)((t - start) / Math.Max(1.0, end - start)) * (right - left);
            DrawLine(new Vector2(x, Top), new Vector2(x, bottom), UiKit.Rule, 1);
            double day = t / P.DayLen + 1;   // days counted from 1, as in the chronicle
            string d = end - start < 20.0 * P.DayLen ? day.ToString("0.0", UiKit.Inv) : Math.Floor(day).ToString("0", UiKit.Inv);
            string lbl = Loc.T($"day {d}", $"сутки {d}");
            DrawString(font, new Vector2(Math.Clamp(x - 24, 0, Size.X - 70), Size.Y - 4), lbl, HorizontalAlignment.Left, -1, 11, UiKit.Dim);
        }
    }

    protected void Box(List<string> lines)
    {
        var font = UiKit.Ui;
        float w = 0;
        foreach (var l in lines) w = Math.Max(w, font.GetStringSize(l, HorizontalAlignment.Left, -1, 11).X);
        w = Math.Min(w + 14, Size.X - 20);
        float bx = Math.Clamp(Mouse.X + 14, 4, Size.X - w - 4), by = Math.Clamp(Mouse.Y + 14, 4, Size.Y - lines.Count * 14 - 10);
        DrawRect(new Rect2(bx, by, w, lines.Count * 14 + 6), new Color(0.03f, 0.035f, 0.045f, 0.95f));
        for (int i = 0; i < lines.Count; i++)
            DrawString(font, new Vector2(bx + 7, by + 15 + i * 14), lines[i], HorizontalAlignment.Left, w - 10, 11, i == 0 ? UiKit.Acc : UiKit.Fg);
    }
}

// The cladogram: one row per branch in pre-order (a parent above its sub-branches), a horizontal bar
// from the branch's origin to now, a vertical link down from the parent at the moment it split off.
public partial class CladeChart : TimeRows
{
    public TreeView View = TreeView.Empty;
    const float RowH = 18;
    int hover = -1;

    protected override void Clicked(Vector2 p)
    {
        int k = RowAt(p);
        if (k >= 0) Win.Pick(View.Nodes[k]);
    }

    int RowAt(Vector2 p)
    {
        if (p.Y < Top || p.Y > Size.Y - AxisH) return -1;
        int k = (int)((p.Y - Top + Scroll) / RowH);
        return k >= 0 && k < View.Nodes.Length ? k : -1;
    }

    public override void _Draw()
    {
        var font = UiKit.Ui;
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.035f, 0.04f, 0.05f, 1f));
        var t = View;
        if (t.Nodes.Length == 0)
        {
            DrawString(font, new Vector2(16, 28), t.CellClade == null ? Loc.T("Building the tree…", "Строю дерево…")
                                                                      : Loc.T("No clade is large enough: nobody alive, or every genotype too small.", "Ни одна клада не дотягивает до порога: живых нет или все генотипы малы."),
                HorizontalAlignment.Left, -1, 13, UiKit.Dim);
            return;
        }
        long start = t.MinOrigin, end = Math.Max(t.Tick, start + 1);
        float left = 10, right = size.X - LabelW, bottom = size.Y - AxisH;
        float X(long tick) => left + (float)((tick - start) / (double)(end - start)) * (right - left);
        ContentH = t.Nodes.Length * RowH;
        Scroll = Math.Clamp(Scroll, 0, Math.Max(0, ContentH - (bottom - Top)));
        Axis(start, end, left, right);
        float Y(int row) => Top + row * RowH - Scroll + RowH / 2;
        int maxCount = t.Nodes.Max(n => n.Count);
        int pick = Win.PickHash != 0 ? t.Find(Win.PickHash, Win.PickOrigin) : -1;
        hover = RowAt(Mouse);

        // Links first, bars over them.
        for (int i = 0; i < t.Nodes.Length; i++)
        {
            var n = t.Nodes[i];
            if (n.Parent < 0) continue;
            float x = X(n.Origin), y0 = Y(n.Parent), y1 = Y(i);
            if (Math.Max(y0, y1) < Top - RowH || Math.Min(y0, y1) > bottom + RowH) continue;
            var c = Color.FromHsv(t.Nodes[n.Parent].Hue, 0.5f, 0.75f);
            DrawLine(new Vector2(x, Math.Max(Top, y0)), new Vector2(x, Math.Min(bottom, y1)), c with { A = 0.7f }, 1);
        }
        for (int i = 0; i < t.Nodes.Length; i++)
        {
            float y = Y(i);
            if (y < Top - RowH || y > bottom + RowH) continue;
            var n = t.Nodes[i];
            bool dim = pick >= 0 && !t.Under(i, pick);
            var c = Color.FromHsv(n.Hue, dim ? 0.25f : 0.72f, dim ? 0.55f : 1f);
            if (i == hover) DrawRect(new Rect2(0, y - RowH / 2, size.X, RowH), new Color(1, 1, 1, 0.05f));
            if (i == pick) DrawRect(new Rect2(0, y - RowH / 2, size.X, RowH), UiKit.Acc with { A = 0.1f });
            float th = 2 + 9 * MathF.Sqrt(n.Count / (float)maxCount);
            float x0 = X(n.Origin), x1 = right;
            DrawRect(new Rect2(x0, y - th / 2, Math.Max(2, x1 - x0), th), c);
            // Where its own sub-branches split off: a small notch.
            if (n.Kids.Length > 0) DrawCircle(new Vector2(X(n.Split), y), 2.5f, c.Lightened(0.3f));
            DrawCircle(new Vector2(x0, y), 3, c);
            string label = Loc.T($"{i + 1}. #{n.Lineage} · {n.Count} · {n.Taxa} gt.", $"{i + 1}. #{n.Lineage} · {n.Count} · {n.Taxa} ген.") + (n.Design != null ? $" · {Hud.DesignName(n.Design)}" : "");
            DrawString(font, new Vector2(right + 8, y + 4), label, HorizontalAlignment.Left, LabelW - 12, 11, dim ? UiKit.Dim : c);
        }
        if (ContentH > bottom - Top)
        {
            float trackH = bottom - Top, barH = Math.Max(24, trackH * trackH / ContentH);
            DrawRect(new Rect2(size.X - 4, Top + (trackH - barH) * Scroll / Math.Max(1, ContentH - trackH), 3, barH), new Color(1, 1, 1, 0.25f));
        }
        if (hover >= 0)
        {
            var n = t.Nodes[hover];
            var lines = new List<string>
            {
                TreeWindow.Describe(n, hover),
                Loc.T($"appeared at tick {n.Origin} ({Chronicle.Day(n.Origin)}); its sub-branches split off from tick {n.Split}; {n.Muts} mutated bytes from the founder",
                      $"появилась на тике {n.Origin} ({Chronicle.Day(n.Origin)}); подветви отходят с тика {n.Split}; {n.Muts} изменённых байтов от основателя"),
                n.RepId != 0 ? Loc.T($"oldest member #{n.RepId} · click — fly to it and light up its range", $"старейший — #{n.RepId} · клик — к нему и показать ареал")
                             : Loc.T("click — light up its range", "клик — показать ареал"),
            };
            Box(lines);
        }
    }
}

// Lineages over time: a band per lineage, as thick as its population at each sample.
public partial class LineageChart : TimeRows
{
    public LineageView View = LineageView.Empty;
    const float RowH = 22;

    protected override void Clicked(Vector2 p)
    {
        int k = RowAt(p);
        if (k >= 0) Win.GoLineage(View.Rows[k]);
    }

    int RowAt(Vector2 p)
    {
        if (p.Y < Top || p.Y > Size.Y - AxisH) return -1;
        int k = (int)((p.Y - Top + Scroll) / RowH);
        return k >= 0 && k < View.Rows.Length ? k : -1;
    }

    public static Color LineageColor(long id) => Color.FromHsv(id * 0.618034f % 1f, 0.7f, 0.95f);   // as the "lineage" coloring of the bodies (V)

    public override void _Draw()
    {
        var font = UiKit.Ui;
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.035f, 0.04f, 0.05f, 1f));
        var v = View;
        if (v.Ticks.Length < 2 || v.Rows.Length == 0)
        {
            DrawString(font, new Vector2(16, 28), Loc.T($"Too few samples yet: one every {v.Every} ticks, at least two are needed (and a lineage of ≥ {SimObserver.MinPeak} bodies).",
                                                         $"Мало замеров: раз в {v.Every} тиков, нужно хотя бы два (и линия от {SimObserver.MinPeak} тел)."),
                HorizontalAlignment.Left, -1, 13, UiKit.Dim);
            return;
        }
        long start = v.Ticks[0], end = Math.Max(v.Ticks[^1], start + 1);
        float left = 10, right = size.X - LabelW, bottom = size.Y - AxisH;
        float X(double tick) => left + (float)((tick - start) / (double)(end - start)) * (right - left);
        ContentH = v.Rows.Length * RowH;
        Scroll = Math.Clamp(Scroll, 0, Math.Max(0, ContentH - (bottom - Top)));
        Axis(start, end, left, right);
        int maxPeak = v.Rows.Max(r => r.Peak);
        int hover = RowAt(Mouse);

        // Chronicle marks per lineage.
        var marks = new Dictionary<long, List<ChronicleEvent>>();
        foreach (var e in Win.Main.Sim.Chronicle.Events)
            if (e.Lineage != 0 && e.Tick >= start && (e.Type is EvType.Speciation or EvType.NewDominant or EvType.Extinction))
            {
                if (!marks.TryGetValue(e.Lineage, out var l)) marks[e.Lineage] = l = new List<ChronicleEvent>();
                l.Add(e);
            }

        var seg = new List<Vector2>();
        for (int k = 0; k < v.Rows.Length; k++)
        {
            float y = Top + k * RowH - Scroll + RowH / 2;
            if (y < Top - RowH || y > bottom + RowH) continue;
            var r = v.Rows[k];
            var c = LineageColor(r.Id);
            if (!r.Alive) c = c.Lerp(UiKit.Dim, 0.45f);
            if (k == hover) DrawRect(new Rect2(0, y - RowH / 2, size.X, RowH), new Color(1, 1, 1, 0.05f));
            DrawLine(new Vector2(left, y), new Vector2(right, y), new Color(1, 1, 1, 0.04f), 1);
            // One vertical stroke per pixel column: the nearest sample's population.
            seg.Clear();
            float half = RowH / 2 - 2;
            int i = 0;
            for (float x = Math.Max(left, X(r.First)); x <= Math.Min(right, X(r.Last)) + 0.5f; x += 1)
            {
                double tick = start + (x - left) / (right - left) * (end - start);
                while (i + 1 < v.Ticks.Length && Math.Abs(v.Ticks[i + 1] - tick) <= Math.Abs(v.Ticks[i] - tick)) i++;
                int n = r.N[i];
                if (n <= 0) continue;
                float h = Math.Max(0.6f, half * MathF.Log(1 + n) / MathF.Log(1 + maxPeak));   // log: a lineage of 5 still shows next to one of 5 000
                seg.Add(new Vector2(x, y - h)); seg.Add(new Vector2(x, y + h));
            }
            if (seg.Count > 0) DrawMultiline(seg.ToArray(), c, 1);
            if (!r.Alive)
            {
                float xe = X(r.Last) + 4;
                DrawLine(new Vector2(xe - 3, y - 3), new Vector2(xe + 3, y + 3), UiKit.Bad, 1.5f);
                DrawLine(new Vector2(xe - 3, y + 3), new Vector2(xe + 3, y - 3), UiKit.Bad, 1.5f);
            }
            if (marks.TryGetValue(r.Id, out var ev))
                foreach (var e in ev)
                {
                    if (e.Type == EvType.Extinction) continue;
                    float x = X(e.Tick);
                    var mc = ChronicleWindow.TypeColors[(int)e.Type];
                    DrawPolygon(new[] { new Vector2(x, y - RowH / 2 + 1), new Vector2(x - 4, y - RowH / 2 + 7), new Vector2(x + 4, y - RowH / 2 + 7) }, new[] { mc });
                }
            string label = (r.Alive ? Loc.T($"#{r.Id} · {r.Now} now · peak {r.Peak}", $"#{r.Id} · сейчас {r.Now} · пик {r.Peak}")
                                    : Loc.T($"#{r.Id} · extinct · peak {r.Peak}", $"#{r.Id} · вымерла · пик {r.Peak}")) + (r.Design != null ? $" · {Hud.DesignName(r.Design)}" : "");
            DrawString(font, new Vector2(right + 8, y + 4), label, HorizontalAlignment.Left, LabelW - 12, 11, c);
        }
        if (ContentH > bottom - Top)
        {
            float trackH = bottom - Top, barH = Math.Max(24, trackH * trackH / ContentH);
            DrawRect(new Rect2(size.X - 4, Top + (trackH - barH) * Scroll / Math.Max(1, ContentH - trackH), 3, barH), new Color(1, 1, 1, 0.25f));
        }
        if (hover >= 0 && Mouse.X <= right)
        {
            var r = v.Rows[hover];
            double tick = start + (Mouse.X - left) / (right - left) * (end - start);
            int i = 0;
            while (i + 1 < v.Ticks.Length && Math.Abs(v.Ticks[i + 1] - tick) <= Math.Abs(v.Ticks[i] - tick)) i++;
            var lines = new List<string>
            {
                Loc.T($"lineage #{r.Id}: {r.N[i]} bodies at tick {v.Ticks[i]} ({Chronicle.Day(v.Ticks[i])})", $"линия #{r.Id}: {r.N[i]} тел на тике {v.Ticks[i]} ({Chronicle.Day(v.Ticks[i])})"),
                Loc.T($"seen from tick {r.First} to {r.Last}, peak {r.Peak}", $"видна с тика {r.First} по {r.Last}, пик {r.Peak}"),
            };
            if (marks.TryGetValue(r.Id, out var ev))
                foreach (var e in ev)
                    if (Math.Abs(X(e.Tick) - Mouse.X) < 5) lines.Add($"{Chronicle.TypeNames[(int)e.Type]}: {e.Shown}");
            lines.Add(r.Alive ? Loc.T("click — fly to its oldest member", "клик — к старейшему представителю") : Loc.T("click — open its fossil", "клик — открыть окаменелость"));
            Box(lines);
        }
        else if (hover >= 0)
        {
            var r = v.Rows[hover];
            Box(new List<string> { Loc.T($"lineage #{r.Id}", $"линия #{r.Id}"), r.Alive ? Loc.T("click — fly to its oldest member", "клик — к старейшему представителю") : Loc.T("click — open its fossil", "клик — открыть окаменелость") });
        }
    }
}
