using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Primordium;

// Where the law editor reads and writes values: the running world (through SimRunner commands) or a
// draft for a new world.
public interface IParamSource
{
    double Get(ParamInfo p);
    void Set(ParamInfo p, double value);
    bool Locked(ParamInfo p);
    long Version { get; }
}

// The laws of the running world: values from the published array, changes as commands between ticks.
public sealed class LiveParams : IParamSource
{
    readonly Func<SimRunner> sim;
    public LiveParams(Func<SimRunner> sim) => this.sim = sim;
    public double Get(ParamInfo p) => SimRunner.ParamValues[p.Index];
    public void Set(ParamInfo p, double value) => sim()?.SetParam(p.Name, value);
    public bool Locked(ParamInfo p) => !p.Live;
    public long Version => SimRunner.ParamVersion;
}

// Laws for a world not made yet (all of them editable).
public sealed class DraftParams : IParamSource
{
    public readonly Dictionary<string, double> Values = new();
    long version;
    public double Get(ParamInfo p) => Values.TryGetValue(p.Name, out var v) ? v : p.Default;
    public void Set(ParamInfo p, double value) { Values[p.Name] = ParamRegistry.Normalize(p, value); version++; }
    public bool Locked(ParamInfo p) => false;
    public long Version => version;
    public void Fill(IReadOnlyDictionary<string, double> values)
    {
        Values.Clear();
        foreach (var p in ParamRegistry.All)
            Values[p.Name] = values != null && values.TryGetValue(p.Name, out var v) ? ParamRegistry.Normalize(p, v) : p.Default;
        version++;
    }
}

// Every law with a slider, a number field, its range and description; groups on the left, search on
// top. Built once; values are refreshed only when the source's version changes. A slider sends its
// value when it is let go (a change of strength or gravity re-solves the support of the whole map).
public partial class ParamEditor : VBoxContainer
{
    sealed class Row
    {
        public ParamInfo P;
        public VBoxContainer Box;
        public Label Name, Mark;
        public HSlider Slider;
        public LineEdit Field;
        public Button Reset;
        public bool Dragging;
        public string Search;
    }

    public IParamSource Source;
    public string LockHint = "только для нового мира";
    readonly List<Row> rows = new();
    readonly Dictionary<string, Label> groupHeads = new();
    readonly Dictionary<string, Button> groupButtons = new();
    LineEdit search;
    Label summary;
    string group = "";   // "" — all
    long seen = -1;
    bool built;

    public ParamEditor() { }
    public ParamEditor(IParamSource source) { Source = source; }

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 6);
        SizeFlagsVertical = SizeFlags.ExpandFill;
        Build();
    }

    void Build()
    {
        if (built) return;
        built = true;
        search = UiKit.Edit("", "поиск по имени и описанию…");
        search.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        search.ClearButtonEnabled = true;
        search.TextChanged += _ => Filter();
        summary = UiKit.Text("", 12, UiKit.Dim);
        AddChild(UiKit.Row(10, search, summary));

        var split = UiKit.Row(10);
        split.SizeFlagsVertical = SizeFlags.ExpandFill;
        AddChild(split);

        var groups = UiKit.Col(2);
        groups.CustomMinimumSize = new Vector2(132, 0);
        AddGroupButton(groups, "", "Все");
        foreach (var g in ParamRegistry.Groups) AddGroupButton(groups, g, g);
        var gs = UiKit.Scroll(groups);
        gs.SizeFlagsHorizontal = SizeFlags.Fill;
        gs.CustomMinimumSize = new Vector2(140, 0);
        split.AddChild(gs);

        var list = UiKit.Col(2);
        foreach (var g in ParamRegistry.Groups)
        {
            var head = UiKit.Title(g, 14);
            head.AddThemeColorOverride("font_color", UiKit.Acc);
            groupHeads[g] = head;
            list.AddChild(head);
            foreach (var p in ParamRegistry.All.Where(p => p.Group == g)) list.AddChild(MakeRow(p).Box);
        }
        var scroll = UiKit.Scroll(list);
        split.AddChild(scroll);
        Filter();
        Refresh(true);
    }

    void AddGroupButton(VBoxContainer box, string g, string text)
    {
        var b = UiKit.Button(text, () => { group = g; search.Text = ""; Filter(); });
        b.ToggleMode = true;
        b.Alignment = HorizontalAlignment.Left;
        b.Flat = true;
        b.AddThemeColorOverride("font_color", UiKit.Dim);
        groupButtons[g] = b;
        box.AddChild(b);
    }

    Row MakeRow(ParamInfo p)
    {
        var r = new Row { P = p, Search = (p.Name + " " + p.Description + " " + p.Group).ToLowerInvariant() };
        rows.Add(r);
        r.Box = UiKit.Col(2);
        r.Box.AddThemeConstantOverride("separation", 1);
        string tip = $"{p.Name} ({p.Group})\n{p.Description}\nпо умолчанию {UiKit.Num(p.Default, p.IsInt)}, диапазон {UiKit.Num(p.Min, p.IsInt)} … {UiKit.Num(p.Max, p.IsInt)}, шаг {UiKit.Num(p.Step, p.IsInt)}" +
                     (p.Live ? "" : "\nчитается только при создании мира") +
                     ((p.Effect & ParamEffect.Strength) != 0 ? "\nсмена пересчитывает опору всей карты (один тяжёлый тик)" : "") +
                     ((p.Effect & ParamEffect.BodyVolume) != 0 ? "\nсмена пересчитывает объём всех тел" : "");

        r.Name = UiKit.Text(p.Name, 13, UiKit.Fg, UiKit.Bold);
        r.Name.TooltipText = tip;
        r.Mark = UiKit.Text("изменён", 11, UiKit.Acc);
        var desc = UiKit.Text(p.Description, 12, UiKit.Dim);
        desc.TooltipText = tip;
        desc.ClipText = true;
        desc.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var head = UiKit.Row(8, r.Name, r.Mark, desc);
        if (Source.Locked(p))
        {
            var lockL = UiKit.Text("· " + LockHint, 11, new Color(0.55f, 0.75f, 1f));
            lockL.TooltipText = "Этот закон читается только при создании мира. Его можно задать в окне «Новый мир» (F4), в разделе «Дополнительно».";
            head.AddChild(lockL);
        }
        r.Box.AddChild(head);

        r.Slider = new HSlider
        {
            MinValue = p.Min, MaxValue = p.Max, Step = p.Step, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter, FocusMode = FocusModeEnum.None, Scrollable = false,
            Editable = !Source.Locked(p), TooltipText = tip, CustomMinimumSize = new Vector2(120, 16),
        };
        r.Slider.DragStarted += () => r.Dragging = true;
        r.Slider.DragEnded += _ => { r.Dragging = false; Commit(r, r.Slider.Value); };
        r.Slider.ValueChanged += v =>
        {
            r.Field.Text = UiKit.Num(ParamRegistry.Normalize(p, v), p.IsInt);
            if (!r.Dragging) Commit(r, v);
        };
        r.Field = UiKit.Edit("", "", 84);
        r.Field.Editable = !Source.Locked(p);
        r.Field.Alignment = HorizontalAlignment.Right;
        r.Field.TextSubmitted += t => { FieldCommit(r); r.Field.ReleaseFocus(); };
        r.Field.FocusExited += () => FieldCommit(r);
        var range = UiKit.Text($"{UiKit.Num(p.Min, p.IsInt)} … {UiKit.Num(p.Max, p.IsInt)}", 11, UiKit.Dim);
        range.CustomMinimumSize = new Vector2(96, 0);
        range.HorizontalAlignment = HorizontalAlignment.Right;
        r.Reset = UiKit.Button("↺", () => Commit(r, p.Default), $"вернуть {UiKit.Num(p.Default, p.IsInt)}");
        r.Reset.Flat = true;
        r.Reset.CustomMinimumSize = new Vector2(26, 0);
        r.Reset.Disabled = Source.Locked(p);
        r.Box.AddChild(UiKit.Row(8, r.Slider, r.Field, range, r.Reset));
        r.Box.AddChild(UiKit.Spacer(0, 4, false));
        return r;
    }

    void FieldCommit(Row r)
    {
        if (Source.Locked(r.P)) return;
        double v = UiKit.ParseNumber(r.Field.Text, out bool ok);
        if (!ok) { r.Field.Text = UiKit.Num(Source.Get(r.P), r.P.IsInt); return; }
        v = ParamRegistry.Normalize(r.P, v);
        r.Field.Text = UiKit.Num(v, r.P.IsInt);
        Commit(r, v);
    }

    void Commit(Row r, double v)
    {
        if (Source.Locked(r.P)) return;
        v = ParamRegistry.Normalize(r.P, v);
        if (v == Source.Get(r.P)) { Show(r, v); return; }
        Source.Set(r.P, v);
        Show(r, v);
    }

    void Show(Row r, double v)
    {
        r.Slider.SetValueNoSignal(v);
        if (!r.Field.HasFocus()) r.Field.Text = UiKit.Num(v, r.P.IsInt);
        bool changed = v != r.P.Default;
        r.Mark.Visible = changed;
        r.Reset.Modulate = changed ? Colors.White : new Color(1, 1, 1, 0.25f);
        r.Name.AddThemeColorOverride("font_color", changed ? UiKit.Acc : UiKit.Fg);
    }

    void Filter()
    {
        string q = search.Text.Trim().ToLowerInvariant();
        foreach (var (g, b) in groupButtons) b.ButtonPressed = q.Length == 0 && g == group;
        var shown = new HashSet<string>();
        foreach (var r in rows)
        {
            bool on = q.Length > 0 ? r.Search.Contains(q) : group == "" || r.P.Group == group;
            r.Box.Visible = on;
            if (on) shown.Add(r.P.Group);
        }
        foreach (var (g, head) in groupHeads) head.Visible = shown.Contains(g);
    }

    // Re-reads the values (only when the source changed).
    public void Refresh(bool force = false)
    {
        if (!built || Source == null) return;
        long v = Source.Version;
        if (!force && v == seen) return;
        seen = v;
        var changedIn = new Dictionary<string, int>();
        int changed = 0;
        foreach (var r in rows)
        {
            if (r.Dragging) continue;
            double val = Source.Get(r.P);
            Show(r, val);
            if (val != r.P.Default) { changed++; changedIn[r.P.Group] = changedIn.GetValueOrDefault(r.P.Group) + 1; }
        }
        foreach (var (g, b) in groupButtons)
            b.Text = g == "" ? $"Все ({changed})" : changedIn.TryGetValue(g, out int n) ? $"{g} · {n}" : g;
        summary.Text = changed == 0 ? $"{rows.Count} законов, все по умолчанию" : $"изменено {changed} из {rows.Count}";
    }

    public override void _Process(double delta)
    {
        if (IsVisibleInTree()) Refresh();
    }

    public void FocusSearch() => search?.GrabFocus();
}
