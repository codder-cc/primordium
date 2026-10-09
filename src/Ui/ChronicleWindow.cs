using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Primordium;

// F8: the chronicle of the world (World.Chronicle, published by SimRunner as a ChronicleView). A feed
// of events filtered by type, lineage and time (or only the important ones), counters by type, and
// the fossils. A click on an event flies to its body if it lives and selects it, otherwise opens its
// fossil; a click on a fossil opens it.
public partial class ChronicleWindow : UiWindow
{
    public static readonly Color[] TypeColors =
    {
        new(0.3f, 1f, 0.55f), new(0.45f, 0.85f, 1f), new(1f, 0.82f, 0.4f), new(0.75f, 0.9f, 0.4f), new(0.85f, 0.6f, 0.35f), new(0.7f, 0.55f, 0.4f),
        new(0.35f, 0.62f, 1f), new(0.3f, 0.45f, 0.8f), new(1f, 0.3f, 0.25f), new(0.85f, 0.3f, 1f), new(1f, 0.55f, 1f), new(0.65f, 0.65f, 0.7f),
        new(1f, 0.95f, 0.5f), new(0.9f, 0.9f, 0.95f), new(0.6f, 0.85f, 0.95f), new(1f, 0.65f, 0.25f), new(0.8f, 0.45f, 0.6f),   // … ParasiteSpread
    };

    TabContainer tabs;
    ItemList feed, fossils;
    CheckBox onlyImportant;
    OptionButton period, fossilOrder;
    LineEdit lineage, fossilFilter;
    readonly CheckBox[] typeBox = new CheckBox[(int)EvType.Count];
    Label status, fossilStatus;
    readonly List<ChronicleEvent> shown = new();
    readonly List<Fossil> shownFossils = new();
    long shownVersion = -1;
    bool dirty = true;
    double filledAt;
    static readonly long[] Periods = { 0, 1, 10, 100 };   // days, 0 = all time

    public ChronicleWindow() : base("chronicle", Loc.T("World chronicle", "Хроника мира"), new Vector2(900, 600))
    {
        MinSize = new Vector2(620, 380);
        tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        Body.AddChild(tabs);
        tabs.AddChild(FeedTab());
        tabs.AddChild(FossilTab());
        tabs.SetTabTitle(0, Loc.T("Event feed", "Лента событий"));
        tabs.SetTabTitle(1, Loc.T("Fossils", "Окаменелости"));
        tabs.TabChanged += _ => dirty = true;
    }

    public override void ShowView(string view) => tabs.CurrentTab = view == "fossils" ? 1 : 0;

    Control FeedTab()
    {
        var col = UiKit.Col(6);
        onlyImportant = UiKit.Check(Loc.T("important only", "только важное"), false, _ => dirty = true,
            Loc.T("events the chronicle keeps forever (firsts of their kind, extinctions, new dominants, speciation…)",
                  "события, которые хроника хранит всегда (первые в своём роде, вымирания, новые доминанты, видообразование…)"));
        period = UiKit.Options(Loc.T("all time", "за всё время"), Loc.T("last day", "за последние сутки"), Loc.T("last 10 days", "за 10 суток"), Loc.T("last 100 days", "за 100 суток"));
        period.ItemSelected += _ => dirty = true;
        lineage = UiKit.Edit("", Loc.T("lineage #", "линия #"), 110);
        lineage.TextChanged += _ => dirty = true;
        var all = UiKit.Button(Loc.T("all types", "все типы"), () => SetTypes(true));
        var none = UiKit.Button(Loc.T("none", "ни одного"), () => SetTypes(false));
        col.AddChild(UiKit.Row(8, onlyImportant, period, UiKit.Text(Loc.T("lineage", "линия"), 13, UiKit.Dim), lineage, UiKit.Spacer(), all, none));
        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 10);
        flow.AddThemeConstantOverride("v_separation", 0);
        for (int k = 0; k < typeBox.Length; k++)
        {
            var box = typeBox[k] = UiKit.Check(Chronicle.TypeNames[k], true, _ => dirty = true, Loc.T("show events of this type", "показывать события этого типа"));
            box.AddThemeColorOverride("font_color", TypeColors[k]);
            box.AddThemeColorOverride("font_hover_color", TypeColors[k]);
            box.AddThemeColorOverride("font_pressed_color", TypeColors[k]);
            box.AddThemeColorOverride("font_hover_pressed_color", TypeColors[k]);
            box.AddThemeFontSizeOverride("font_size", 12);
            flow.AddChild(box);
        }
        col.AddChild(flow);
        feed = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None, TooltipText = "" };
        feed.AddThemeFontSizeOverride("font_size", 12);
        feed.ItemClicked += (i, _, button) => { if (button == (long)MouseButton.Left) Go(shown[(int)i]); };
        col.AddChild(feed);
        status = UiKit.Text("", 12, UiKit.Dim);
        col.AddChild(status);
        return col;
    }

    Control FossilTab()
    {
        var col = UiKit.Col(6);
        fossilOrder = UiKit.Options(Loc.T("most important first", "сначала важные"), Loc.T("newest first", "сначала новые"), Loc.T("oldest first", "сначала древние"));
        fossilOrder.ItemSelected += _ => dirty = true;
        fossilFilter = UiKit.Edit("", Loc.T("lineage or number", "линия или номер"), 150);
        fossilFilter.TextChanged += _ => dirty = true;
        col.AddChild(UiKit.Row(8, fossilOrder, UiKit.Text(Loc.T("find", "найти"), 13, UiKit.Dim), fossilFilter, UiKit.Spacer(),
            UiKit.Text(Loc.T("click to open: genome, proteins, biography, “Revive”", "клик — открыть: геном, белки, биография, «Воскресить»"), 12, UiKit.Dim)));
        fossils = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None };
        fossils.AddThemeFontSizeOverride("font_size", 12);
        fossils.ItemClicked += (i, _, button) => { if (button == (long)MouseButton.Left) Ui.Fossil.Show(shownFossils[(int)i]); };
        col.AddChild(fossils);
        fossilStatus = UiKit.Text("", 12, UiKit.Dim);
        col.AddChild(fossilStatus);
        return col;
    }

    void SetTypes(bool on)
    {
        foreach (var b in typeBox) b.SetPressedNoSignal(on);
        dirty = true;
    }

    protected override void OnOpen() => dirty = true;

    public override void _Process(double delta)
    {
        if (!Visible || Main.Sim == null) return;
        var view = Main.Sim.Chronicle;
        if (view.Version == shownVersion && !dirty) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (!dirty && now - filledAt < 0.5) return;   // new events: the list is rebuilt at most twice a second
        filledAt = now;
        shownVersion = view.Version;
        dirty = false;
        if (tabs.CurrentTab == 0) FillFeed(view); else FillFossils(view);
        for (int k = 0; k < typeBox.Length; k++) typeBox[k].Text = $"{Chronicle.TypeNames[k]} {view.Counts[k]}";
    }

    void FillFeed(ChronicleView view)
    {
        shown.Clear();
        long lin = ParseId(lineage.Text);
        long days = Periods[Math.Clamp(period.Selected, 0, Periods.Length - 1)];
        long from = days > 0 ? view.Tick - days * P.DayLen : long.MinValue;
        bool important = onlyImportant.ButtonPressed;
        for (int i = view.Events.Length - 1; i >= 0 && shown.Count < 3000; i--)
        {
            var e = view.Events[i];
            if (e.Tick < from) break;
            if (important && !e.Important) continue;
            if (!typeBox[(int)e.Type].ButtonPressed) continue;
            if (lin > 0 && e.Lineage != lin) continue;
            shown.Add(e);
        }
        feed.Clear();
        foreach (var e in shown)
        {
            int k = feed.AddItem($"{(e.Important ? "★" : "  ")} {Chronicle.Day(e.Tick),-10} {Loc.T("tick", "тик")} {e.Tick,-8:0}  {e.Shown}");
            feed.SetItemCustomFgColor(k, e.Important ? TypeColors[(int)e.Type] : TypeColors[(int)e.Type].Lerp(UiKit.Dim, 0.35f));
            feed.SetItemTooltip(k, Chronicle.TypeNames[(int)e.Type] + " · " + Where(e, view));
        }
        int total = view.Events.Length;
        int importantN = view.Events.Count(e => e.Important);
        long ever = view.Counts.Sum();
        status.Text = Loc.T($"showing {shown.Count} of {total} kept (important {importantN}, {ever} ever) · " +
                            "click: go to the creature if alive, else its fossil · F8 to close",
                            $"показано {shown.Count} из {total} хранимых (важных {importantN}, всего было {ever}) · " +
                            "клик — к существу, если живо, иначе его окаменелость · F8 — закрыть");
    }

    static string Where(ChronicleEvent e, ChronicleView view)
    {
        string who = e.AgentId == 0 ? Loc.T("no creature", "без существа")
                   : view.FossilByAgent.ContainsKey(e.AgentId) ? Loc.T($"#{e.AgentId}: fossil kept", $"#{e.AgentId}: есть окаменелость") : $"#{e.AgentId}";
        return $"{who}" + (e.X >= 0 ? Loc.T($" · cell ({e.X}, {e.Y})", $" · клетка ({e.X}, {e.Y})") : "") + (e.Lineage != 0 ? Loc.T($" · lineage #{e.Lineage}", $" · линия #{e.Lineage}") : "");
    }

    void FillFossils(ChronicleView view)
    {
        shownFossils.Clear();
        long id = ParseId(fossilFilter.Text);
        IEnumerable<Fossil> list = view.Fossils.Where(f => id <= 0 || f.Lineage == id || f.AgentId == id);
        list = fossilOrder.Selected switch
        {
            1 => list.OrderByDescending(f => f.DiedTick < 0 ? f.BornTick + f.Age : f.DiedTick),
            2 => list.OrderBy(f => f.BornTick),
            _ => list.OrderByDescending(f => f.Importance).ThenByDescending(f => f.DiedTick),
        };
        shownFossils.AddRange(list.Take(3000));
        fossils.Clear();
        foreach (var f in shownFossils)
        {
            string life = f.DiedTick >= 0 ? $"{Chronicle.Day(f.BornTick)} – {f.DiedTick / P.DayLen + 1}, {Chronicle.CauseName(f.Cause)}" : Loc.T($"{Chronicle.Day(f.BornTick)}, taken alive", $"{Chronicle.Day(f.BornTick)}, снят живым");
            int k = fossils.AddItem(Loc.T($"#{f.AgentId,-9} lineage #{f.Lineage,-9} gen. {f.Gen,-4} {life} · genome {f.Genome?.Length ?? 0} bytes · offspring {f.Children} · {Chronicle.WhyText(f.Why)}",
                                    $"#{f.AgentId,-9} линия #{f.Lineage,-9} пок. {f.Gen,-4} {life} · геном {f.Genome?.Length ?? 0} байт · потомков {f.Children} · {Chronicle.WhyText(f.Why)}"));
            fossils.SetItemCustomFgColor(k, (f.Why & Chronicle.WhyEvent) != 0 ? UiKit.Acc : f.Designed ? UiKit.Good : UiKit.Fg);
        }
        fossilStatus.Text = Loc.T($"fossils {view.Fossils.Length} (up to {P.FossilCap} kept, the least important go first) · showing {shownFossils.Count}",
                                  $"окаменелостей {view.Fossils.Length} (хранится до {P.FossilCap}, лишние — наименее важные) · показано {shownFossils.Count}");
    }

    static long ParseId(string text)
    {
        var digits = new string((text ?? "").Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out long v) ? v : 0;
    }

    // A click on an event: its body if it lives, else its fossil, else at least the place.
    public void Go(ChronicleEvent e)
    {
        var a = Main.FindAlive(e.AgentId);
        if (a != null)
        {
            Main.Focus(a);
            Main.View.ZoomAt(30);
            return;
        }
        var f = Main.Sim.Chronicle.FossilFor(e);
        if (f != null) { Ui.Fossil.Show(f); return; }
        if (e.X >= 0) { Main.View.LookAtCell(e.X, e.Y); Ui.Toast(Loc.T($"the event was here: ({e.X}, {e.Y})", $"событие было здесь: ({e.X}, {e.Y})")); return; }
        Ui.Toast(Loc.T("this event has no creature and no place", "у этого события нет существа и места"));
    }
}
