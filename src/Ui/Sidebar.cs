using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Primordium;

// One entry of the sidebar: an icon, a name, its hotkey, a line on what it does, the action, and when
// it is highlighted (the window is open, the tool is in hand) or shown at all.
public sealed class SideItem
{
    public string Id, Group, Caption, Key, Tip;
    public Icons.Fn Icon;
    public Func<string> Badge;   // instead of an icon: a short text (the language)
    public Action Act;
    public Func<bool> On;        // null: never highlighted
    public Func<bool> Shown;     // null: always shown
    public bool Visible => Shown == null || Shown();
}

// The left sidebar: a column of icons that opens every window and takes the main tools. Collapsed it
// is a narrow strip of icons; expanded it shows names and hotkeys too (the chevron on top or Tab).
// Everything is drawn here; the entries come from UiManager.Build. Items of the group "" sit at the bottom.
public partial class Sidebar : Control
{
    public const float Narrow = 44;
    const float Top = 44, GroupGap = 22, Pad = 8;

    public UiManager Ui;
    public readonly List<SideItem> Items = new();
    public readonly Dictionary<string, string> Groups = new();   // group id → its title
    public bool Expanded;

    float wide = 210, rowH = 32;
    readonly List<(Rect2 r, SideItem it)> rows = new();
    readonly List<(float y, string title)> heads = new();
    int hover = -1;   // index in rows, -2 the chevron
    long shownSig = -1;

    static readonly Color Bg = new(0.045f, 0.05f, 0.064f, 0.97f), IconCol = new(0.66f, 0.7f, 0.77f), TextCol = new(0.8f, 0.82f, 0.87f);
    readonly StyleBoxFlat hoverBox = UiKit.Box(new Color(1, 1, 1, 0.065f), 6);
    readonly StyleBoxFlat onBox = UiKit.Box(new Color(1f, 0.82f, 0.4f, 0.12f), 6);
    readonly StyleBoxFlat keyBox = UiKit.Box(new Color(1, 1, 1, 0.04f), 4, 0, new Color(1, 1, 1, 0.13f));

    public float Width => Expanded ? wide : Narrow;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        Name = "Sidebar";
        // The wide width fits the longest name and hotkey of this language.
        float cap = 0, key = 0;
        foreach (var it in Items)
        {
            cap = Math.Max(cap, UiKit.Ui.GetStringSize(it.Caption, HorizontalAlignment.Left, -1, 13).X);
            if (!string.IsNullOrEmpty(it.Key)) key = Math.Max(key, UiKit.Bold.GetStringSize(it.Key, HorizontalAlignment.Left, -1, 10).X);
        }
        wide = Math.Clamp(46 + cap + 14 + key + 12 + 12, 190, 280);
        Relayout();
    }

    public void Toggle()
    {
        Expanded = !Expanded;
        Ui.SidebarChanged(Expanded);
        Relayout();
    }

    void Relayout()
    {
        var vs = GetViewportRect().Size;
        if (vs.Y <= 0) vs = new Vector2(1280, 720);
        Position = Vector2.Zero;
        Size = new Vector2(Width, vs.Y);
        rows.Clear();
        heads.Clear();
        var shown = Items.Where(i => i.Visible).ToList();
        var bottom = shown.Where(i => i.Group == "").ToList();
        var groups = shown.Where(i => i.Group != "").Select(i => i.Group).Distinct().ToList();
        int n = shown.Count;
        float avail = vs.Y - Top - groups.Count * GroupGap - Pad * 3;
        rowH = Math.Clamp(MathF.Floor(avail / Math.Max(1, n)), 24, 34);
        float y = Top;
        foreach (var g in groups)
        {
            heads.Add((y, Groups.TryGetValue(g, out var t) ? t : g));
            y += GroupGap;
            foreach (var it in shown.Where(i => i.Group == g)) { rows.Add((new Rect2(0, y, Width, rowH), it)); y += rowH; }
        }
        y = vs.Y - Pad - bottom.Count * rowH;
        foreach (var it in bottom) { rows.Add((new Rect2(0, y, Width, rowH), it)); y += rowH; }
        QueueRedraw();
    }

    // What is highlighted and shown, as bits: the bar is drawn again only when it changes.
    long Signature()
    {
        long s = 17;
        foreach (var it in Items) s = s * 3 + (it.Visible ? 1 : 0) + (it.On?.Invoke() == true ? 2 : 0);
        return s;
    }

    public override void _Process(double delta)
    {
        var vs = GetViewportRect().Size;
        long sig = Signature() ^ ((long)vs.Y << 40) ^ (Expanded ? 1L << 62 : 0);
        if (sig != shownSig) { shownSig = sig; Relayout(); }
    }

    int RowAt(Vector2 p)
    {
        if (p.Y < Top && p.Y >= 0 && p.X >= 0 && p.X < Width) return -2;
        for (int i = 0; i < rows.Count; i++) if (rows[i].r.HasPoint(p)) return i;
        return -1;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm)
        {
            int h = RowAt(mm.Position);
            if (h != hover) { hover = h; QueueRedraw(); }
        }
        else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
        {
            int h = RowAt(mb.Position);
            if (h == -2) Toggle();
            else if (h >= 0) { rows[h].it.Act?.Invoke(); shownSig = -1; }
            AcceptEvent();
        }
        else if (e is InputEventMouseButton) AcceptEvent();   // the wheel over the bar must not zoom the map
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit && hover != -1) { hover = -1; QueueRedraw(); }
    }

    public override string _GetTooltip(Vector2 at)
    {
        int h = RowAt(at);
        return h == -2 ? "chevron" : h >= 0 ? "row" + h : "";
    }

    public override GodotObject _MakeCustomTooltip(string forText)
    {
        string name, key, tip;
        if (forText == "chevron")
        {
            name = Expanded ? Loc.T("Collapse the bar", "Свернуть панель") : Loc.T("Expand the bar", "Развернуть панель");
            key = "Tab";
            tip = Loc.T("names and hotkeys next to the icons, or icons only", "названия и клавиши рядом со значками или только значки");
        }
        else if (forText.StartsWith("row") && int.TryParse(forText[3..], out int i) && i < rows.Count)
        {
            var it = rows[i].it;
            (name, key, tip) = (it.Caption, it.Key, it.Tip);
        }
        else return null;
        var box = UiKit.Col(3);
        var head = UiKit.Row(8);
        head.AddChild(UiKit.Title(name, 13));
        if (!string.IsNullOrEmpty(key))
        {
            var k = UiKit.Text(key, 11, UiKit.Acc, UiKit.Bold);
            k.AddThemeStyleboxOverride("normal", UiKit.Box(new Color(1, 1, 1, 0.05f), 3, 5, new Color(1, 1, 1, 0.16f), 0));
            head.AddChild(k);
        }
        box.AddChild(head);
        if (!string.IsNullOrEmpty(tip))
        {
            var l = UiKit.Text(tip, 12, UiKit.Dim);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l.CustomMinimumSize = new Vector2(Math.Min(300, 4 + UiKit.Ui.GetStringSize(tip, HorizontalAlignment.Left, -1, 12).X), 0);
            box.AddChild(l);
        }
        return box;
    }

    public override void _Draw()
    {
        float w = Width, h = Size.Y;
        DrawRect(new Rect2(0, 0, w, h), Bg);
        DrawLine(new Vector2(w - 0.5f, 0), new Vector2(w - 0.5f, h), new Color(1, 1, 1, 0.08f));

        // The chevron and, expanded, the name of the game.
        bool hc = hover == -2;
        if (hc) hoverBox.Draw(GetCanvasItem(), new Rect2(4, 6, Expanded ? 36 : w - 8, Top - 12));
        Icons.Chevron(this, new Vector2(22, Top / 2), 18, hc ? UiKit.Fg : IconCol, Expanded);
        if (Expanded) DrawString(UiKit.Bold, new Vector2(46, Top / 2 + 5), "PRIMORDIUM", HorizontalAlignment.Left, -1, 12, UiKit.Dim);
        DrawLine(new Vector2(8, Top - 0.5f), new Vector2(w - 8, Top - 0.5f), new Color(1, 1, 1, 0.06f));

        foreach (var (y, title) in heads)
        {
            if (Expanded) DrawString(UiKit.Bold, new Vector2(14, y + 15), title.ToUpperInvariant(), HorizontalAlignment.Left, -1, 10, UiKit.Dim with { A = 0.75f });
            else if (y > Top) DrawLine(new Vector2(13, y + GroupGap / 2), new Vector2(w - 13, y + GroupGap / 2), new Color(1, 1, 1, 0.1f));
        }
        if (rows.Count > 0 && rows[^1].it.Group == "")
        {
            float by = rows.First(r => r.it.Group == "").r.Position.Y - 4;
            DrawLine(new Vector2(8, by), new Vector2(w - 8, by), new Color(1, 1, 1, 0.06f));
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var (r, it) = rows[i];
            bool on = it.On?.Invoke() == true, hov = i == hover;
            var cell = new Rect2(4, r.Position.Y + 1.5f, w - 8, r.Size.Y - 3);
            var under = Bg;
            if (on) { onBox.Draw(GetCanvasItem(), cell); under = new Color(0.13f, 0.12f, 0.1f); }
            if (hov) { hoverBox.Draw(GetCanvasItem(), cell); under = on ? new Color(0.17f, 0.16f, 0.14f) : new Color(0.1f, 0.105f, 0.12f); }
            if (on) DrawRect(new Rect2(0, r.Position.Y + 7, 2.5f, r.Size.Y - 14), UiKit.Acc);
            var col = on ? UiKit.Acc : hov ? UiKit.Fg : IconCol;
            var c = new Vector2(22, r.Position.Y + r.Size.Y / 2);
            float size = Math.Min(20, r.Size.Y - 10);
            if (it.Badge != null)
            {
                string b = it.Badge();
                var bs = UiKit.Bold.GetStringSize(b, HorizontalAlignment.Left, -1, 11);
                var br = new Rect2(c.X - 12, c.Y - 8.5f, 24, 17);
                UiKit.Box(new Color(0, 0, 0, 0), 4, 0, col with { A = 0.7f }).Draw(GetCanvasItem(), br);
                DrawString(UiKit.Bold, new Vector2(c.X - bs.X / 2, c.Y + 4), b, HorizontalAlignment.Left, -1, 11, col);
            }
            else it.Icon?.Invoke(this, c, size, col, under);
            if (!Expanded) continue;
            DrawString(UiKit.Ui, new Vector2(46, c.Y + 4.5f), it.Caption, HorizontalAlignment.Left, w - 46 - 50, 13, on ? UiKit.Acc : hov ? UiKit.Fg : TextCol);
            if (!string.IsNullOrEmpty(it.Key))
            {
                var ks = UiKit.Bold.GetStringSize(it.Key, HorizontalAlignment.Left, -1, 10);
                float kw = Math.Max(18, ks.X + 10);
                var kr = new Rect2(w - 12 - kw, c.Y - 8.5f, kw, 17);
                keyBox.Draw(GetCanvasItem(), kr);
                DrawString(UiKit.Bold, new Vector2(kr.Position.X + (kw - ks.X) / 2, c.Y + 3.5f), it.Key, HorizontalAlignment.Left, -1, 10, on ? UiKit.Acc : UiKit.Dim);
            }
        }
    }
}
