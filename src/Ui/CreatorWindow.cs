using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;

namespace Primordium;

// F7: the creature editor. A library of designs (user://creatures), the genome as text with live
// checking, what the body is made of, how much energy it starts with, its looks, and how it is planted
// (matter and energy from the place or brought in from outside). Brush 5 plants it where you click.
public partial class CreatorWindow : UiWindow
{
    CreatureDesign current = CreatureExamples.Leaf;
    string currentPath;
    bool dirty, loading;
    double checkAt = -1, availAt;

    ItemList library;
    List<CreatureLibrary.Entry> entries = new();
    LineEdit nameEdit, descEdit;
    CodeEdit code;
    Label status, bodyTotal, availability, explain, lookNote;
    readonly List<int> errorLines = new();
    VBoxContainer bodyRows;
    SpinBox energy, count;
    OptionButton matter, energySrc, shape;
    CheckBox customLooks;
    HSlider hue, sat, val;
    TextureRect portrait;
    ColorRect swatch;
    World chemOf;
    byte[] lastBytes;
    int lastCell = -1;

    public bool Valid { get; private set; }
    TabContainer tabs;

    public override void ShowView(string view) => tabs.CurrentTab = view == "looks" ? 1 : view == "help" ? 2 : 0;

    public CreatorWindow() : base("creator", Loc.T("Creature designer", "Конструктор существ"), new Vector2(1040, 610))
    {
        MinSize = new Vector2(820, 460);
        var cols = UiKit.Row(12);
        cols.SizeFlagsVertical = SizeFlags.ExpandFill;
        Body.AddChild(cols);
        cols.AddChild(LibraryColumn());
        cols.AddChild(GenomeColumn());
        cols.AddChild(RightColumn());
    }

    // ---- layout ----

    Control LibraryColumn()
    {
        var col = UiKit.Col(6);
        col.CustomMinimumSize = new Vector2(190, 0);
        col.AddChild(UiKit.Title(Loc.T("Library", "Библиотека"), 13));
        library = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None, TooltipText = "user://creatures" };
        library.ItemSelected += i => Pick((int)i);
        col.AddChild(library);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 4);
        foreach (var b in new Control[]
        {
            UiKit.Button(Loc.T("New", "Новый"), NewDesign, Loc.T("an empty design with a simple genome", "пустой дизайн с простым геномом")),
            UiKit.Button(Loc.T("Copy", "Копия"), Duplicate, Loc.T("a copy of the current one under a new name", "копия текущего под новым именем")),
            UiKit.Button(Loc.T("Save", "Сохранить"), SaveDesign, Loc.T("to the library (file named after the design)", "в библиотеку (файл по имени)")),
            new ConfirmButton(Loc.T("Delete", "Удалить"), DeleteDesign, Loc.T("delete the file from the library", "удалить файл из библиотеки")),
        })
        {
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            grid.AddChild(b);
        }
        col.AddChild(grid);
        col.AddChild(UiKit.Button(Loc.T("Take from selected", "Взять у выбранного"), TakeSelected, Loc.T("genome, body and looks of the selected creature (click it on the map)", "геном, тело и облик выбранного существа (клик по нему на карте)")));
        col.AddChild(UiKit.Button(Loc.T("Restore examples", "Вернуть примеры"), () => { int n = CreatureLibrary.ExportExamples(UiManager.CreaturesDir); ListLibrary(); Ui.Toast(n > 0 ? Loc.T($"examples restored: {n}", $"примеров восстановлено: {n}") : Loc.T("all examples are in place", "все примеры на месте")); }, Loc.T("Leaf, Mole, Swimmer", "Листок, Крот, Пловец")));
        return col;
    }

    Control GenomeColumn()
    {
        var col = UiKit.Col(6);
        col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nameEdit = UiKit.Edit("", Loc.T("name", "имя"));
        nameEdit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nameEdit.TextChanged += t => { if (!loading) { current.Name = CreatureExamples.Key(t.Trim()); Touch(); } };
        col.AddChild(UiKit.Row(8, UiKit.Text(Loc.T("Name", "Имя"), 13, UiKit.Dim), nameEdit));
        descEdit = UiKit.Edit("", Loc.T("description", "описание"));
        descEdit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        descEdit.TextChanged += t => { if (!loading) { current.Description = t; Touch(); } };
        col.AddChild(descEdit);
        code = new CodeEdit
        {
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            GuttersDrawLineNumbers = true, SyntaxHighlighter = GenomeHelp.Highlighter(),
            HighlightCurrentLine = true, AutoBraceCompletionEnabled = false, CustomMinimumSize = new Vector2(280, 200),
        };
        code.TextChanged += () => { if (!loading) { current.Genome = code.Text; Touch(); } };
        col.AddChild(code);
        status = UiKit.Text("", 12, UiKit.Dim, null, true);
        col.AddChild(status);
        return col;
    }

    Control RightColumn()
    {
        var tabs = this.tabs = new TabContainer { CustomMinimumSize = new Vector2(360, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
        tabs.AddChild(BodyTab());
        tabs.AddChild(LooksTab());
        tabs.AddChild(HelpTab());
        tabs.SetTabTitle(0, Loc.T("Body and planting", "Тело и посадка"));
        tabs.SetTabTitle(1, Loc.T("Looks", "Облик"));
        tabs.SetTabTitle(2, Loc.T("Reference", "Справка"));
        return tabs;
    }

    Control BodyTab()
    {
        var col = UiKit.Col(6);
        col.AddChild(UiKit.Text(Loc.T("What the body is made of (a species number of this world, gas: the world's gas, any: whatever is at hand):", "Из каких молекул тело (номер вида этого мира, gas — газ мира, any — что найдётся):"), 12, UiKit.Dim, null, true));
        bodyRows = UiKit.Col(3);
        col.AddChild(bodyRows);
        bodyTotal = UiKit.Text("", 12, UiKit.Dim);
        col.AddChild(UiKit.Row(8, UiKit.Button(Loc.T("+ molecule", "+ молекула"), () => { AddBodyRow("any", 1); BodyChanged(); }), bodyTotal));

        energy = UiKit.Spin(0, 1000, 1, 30, 90);
        energy.ValueChanged += v => { if (!loading) { current.Energy = (float)v; Touch(); } };
        col.AddChild(UiKit.Row(8, UiKit.Text(Loc.T("Energy at planting", "Энергия при посадке"), 13, UiKit.Dim), energy));
        col.AddChild(new HSeparator());

        matter = UiKit.Options(Loc.T("from local matter", "из местного вещества"), Loc.T("bring from outside", "принести извне"));
        matter.ItemSelected += _ => { Ui.State.SpawnMatter = matter.Selected; Explain(); Ui.SaveSoon(); };
        energySrc = UiKit.Options(Loc.T("from local reactions", "из местных реакций"), Loc.T("bring from outside", "принести извне"));
        energySrc.ItemSelected += _ => { Ui.State.SpawnEnergy = energySrc.Selected; Explain(); Ui.SaveSoon(); };
        col.AddChild(UiKit.Row(8, Fixed(Loc.T("Matter", "Вещество"), 70), matter));
        col.AddChild(UiKit.Row(8, Fixed(Loc.T("Energy", "Энергия"), 70), energySrc));
        explain = UiKit.Text("", 12, UiKit.Dim, null, true);
        col.AddChild(explain);
        count = UiKit.Spin(1, 50, 1, 1, 80);
        count.ValueChanged += v => { Ui.State.SpawnCount = (int)v; Ui.SaveSoon(); };
        var plant = UiKit.Button(Loc.T("Plant with brush (5)", "Посадить кистью (5)"), () => { Ui.Life.Disarm(); Main.SetTool(5); }, Loc.T("brush 5: a click on the map plants the design in that cell; [ ] sets the scatter radius", "кисть 5: клик по карте сажает дизайн в эту клетку; [ ] — радиус разброса"));
        plant.AddThemeColorOverride("font_color", UiKit.Acc);
        col.AddChild(UiKit.Row(8, Fixed(Loc.T("Count", "Сколько"), 70), count, UiKit.Spacer(), plant));
        availability = UiKit.Text("", 12, UiKit.Dim, null, true);
        col.AddChild(availability);
        var scroll = UiKit.Scroll(col);
        scroll.Name = "body";
        return scroll;
    }

    static Label Fixed(string s, float w)
    {
        var l = UiKit.Text(s, 13, UiKit.Dim);
        l.CustomMinimumSize = new Vector2(w, 0);
        return l;
    }

    Control LooksTab()
    {
        var col = UiKit.Col(8);
        col.Name = "looks";
        customLooks = UiKit.Check(Loc.T("Custom colour and shape", "Свой цвет и форма"), false, on =>
        {
            if (loading) return;
            if (on) { current.Hue = (float)hue.Value; current.Sat = (float)sat.Value; current.Val = (float)val.Value; current.Shape = shape.Selected; }
            else { current.Hue = current.Sat = current.Val = null; current.Shape = null; }
            Touch();
        });
        col.AddChild(customLooks);
        HSlider Slider(string label, Action<float> set)
        {
            var s = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.01, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, FocusMode = FocusModeEnum.None, Scrollable = false, CustomMinimumSize = new Vector2(0, 16) };
            s.ValueChanged += v => { if (!loading && customLooks.ButtonPressed) { set((float)v); Touch(false); } };
            col.AddChild(UiKit.Row(8, Fixed(label, 90), s));
            return s;
        }
        hue = Slider(Loc.T("hue", "оттенок"), v => current.Hue = v);
        sat = Slider(Loc.T("saturation", "насыщенность"), v => current.Sat = v);
        val = Slider(Loc.T("brightness", "яркость"), v => current.Val = v);
        shape = UiKit.Options(Looks.ShapeNames);
        shape.ItemSelected += i => { if (!loading && customLooks.ButtonPressed) { current.Shape = (int)i; Touch(false); } };
        col.AddChild(UiKit.Row(8, Fixed(Loc.T("shape", "форма"), 90), shape));
        portrait = new TextureRect { CustomMinimumSize = new Vector2(112, 112), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.Nearest, TooltipText = Loc.T("portrait from the genome, as in the panel (relatives look alike)", "портрет по геному — как в панели (родня похожа)") };
        swatch = new ColorRect { CustomMinimumSize = new Vector2(56, 56), SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = Loc.T("body colour on the map", "цвет тела на карте") };
        col.AddChild(UiKit.Row(16, portrait, swatch));
        lookNote = UiKit.Text("", 12, UiKit.Dim, null, true);
        col.AddChild(lookNote);
        return col;
    }

    Control HelpTab()
    {
        var r = new RichTextLabel
        {
            BbcodeEnabled = true, Text = GenomeHelp.Reference(), SizeFlagsVertical = SizeFlags.ExpandFill,
            SelectionEnabled = true, FocusMode = FocusModeEnum.None,
        };
        r.AddThemeFontOverride("mono_font", UiKit.Mono);
        r.AddThemeFontOverride("bold_font", UiKit.Bold);
        r.AddThemeFontSizeOverride("normal_font_size", 12);
        r.AddThemeFontSizeOverride("mono_font_size", 12);
        r.AddThemeFontSizeOverride("bold_font_size", 13);
        r.AddThemeColorOverride("default_color", UiKit.Fg);
        r.Name = "help";
        return r;
    }

    // ---- library ----

    public override void _Ready()
    {
        Directory.CreateDirectory(UiManager.CreaturesDir);
        if (!Ui.State.ExamplesExported)
        {
            CreatureLibrary.ExportExamples(UiManager.CreaturesDir);
            Ui.State.ExamplesExported = true;
            Ui.SaveSoon();
        }
        matter.Selected = Math.Clamp(Ui.State.SpawnMatter, 0, 1);
        energySrc.Selected = Math.Clamp(Ui.State.SpawnEnergy, 0, 1);
        count.SetValueNoSignal(Math.Max(1, Ui.State.SpawnCount));
        ListLibrary();
        var start = entries.FindIndex(e => e.Path == Ui.State.Design && e.Design != null);
        if (start < 0) start = entries.FindIndex(e => e.Design != null);
        if (start >= 0) Pick(start); else LoadDesign(CreatureExamples.Leaf, null);
        Explain();
    }

    void ListLibrary()
    {
        entries = CreatureLibrary.List(UiManager.CreaturesDir);
        library.Clear();
        foreach (var e in entries)
        {
            int i = library.AddItem(e.Design != null ? CreatureExamples.DisplayName(e.Design.Name) : Path.GetFileName(e.Path) + Loc.T(" (error)", " (ошибка)"));
            library.SetItemTooltip(i, e.Design != null ? CreatureExamples.DisplayDescription(e.Design) : e.Error);
            if (e.Path == currentPath) library.Select(i);
        }
    }

    void Pick(int i)
    {
        if (i < 0 || i >= entries.Count) return;
        var e = entries[i];
        if (e.Design == null) { Ui.Toast(Loc.T($"cannot read {Path.GetFileName(e.Path)}: {e.Error}", $"не прочитать {Path.GetFileName(e.Path)}: {e.Error}"), true); return; }
        if (dirty && e.Path != currentPath) Ui.Toast(Loc.T($"unsaved changes to '{Shown}' discarded", $"несохранённые изменения «{Shown}» отброшены"));
        LoadDesign(e.Design.Clone(), e.Path);
        library.Select(i);
    }

    public void LoadDesign(CreatureDesign d, string path)
    {
        loading = true;
        current = d;
        currentPath = path;
        nameEdit.Text = CreatureExamples.DisplayName(d.Name);
        descEdit.Text = CreatureExamples.DisplayDescription(d);
        code.Text = CreatureExamples.DisplayGenome(d) ?? "";
        code.ClearUndoHistory();
        energy.SetValueNoSignal(d.Energy);
        customLooks.ButtonPressed = d.Hue != null;
        hue.SetValueNoSignal(d.Hue ?? 0.5f);
        sat.SetValueNoSignal(d.Sat ?? 0.7f);
        val.SetValueNoSignal(d.Val ?? 0.85f);
        shape.Selected = d.Shape ?? 0;
        RebuildBody();
        loading = false;
        dirty = false;
        if (path != null) { Ui.State.Design = path; Ui.SaveSoon(); }
        else library.DeselectAll();
        Validate();
        UpdateTitle();
    }

    // The current design's name as shown (an example's in the current language).
    string Shown => CreatureExamples.DisplayName(current.Name);

    void UpdateTitle() => SetTitle(Loc.T("Creature designer", "Конструктор существ") + $" — {(string.IsNullOrEmpty(current.Name) ? Loc.T("untitled", "без имени") : Shown)}{(dirty ? " •" : "")}");

    void NewDesign()
    {
        LoadDesign(new CreatureDesign
        {
            Name = Loc.T("New creature", "Новое существо"),
            Description = "",
            Body = new() { ["any"] = 10 },
            Energy = 30,
            Genome = Loc.T("; new creature: light on molecule 0, divides when fed", "; новое существо: свет на молекуле 0, деление сытым") + "\nlabel 1\nenzyme photo 0 0 t=15.0\nenzyme split 1 0 t=15.0\nlabel 0\npush 0\nphoto\npush 1\nsplit\nenergy\nlit 60\nlt\njnz 0\npush 0\npush 1\ndivide\njmp 0\n",
        }, null);
        dirty = true;
        UpdateTitle();
    }

    void Duplicate()
    {
        var d = current.Clone();
        d.Name = CreatureExamples.DisplayName(d.Name ?? "") + Loc.T(" (copy)", " (копия)");
        LoadDesign(d, null);
        dirty = true;
        UpdateTitle();
    }

    void SaveDesign()
    {
        if (string.IsNullOrWhiteSpace(current.Name)) { Ui.Toast(Loc.T("the design has no name", "у дизайна нет имени"), true); nameEdit.GrabFocus(); return; }
        try
        {
            string path = CreatureLibrary.Save(current, UiManager.CreaturesDir);
            if (currentPath != null && currentPath != path && File.Exists(currentPath)) File.Delete(currentPath);   // renamed
            currentPath = path;
            dirty = false;
            Ui.State.Design = path;
            Ui.SaveSoon();
            ListLibrary();
            UpdateTitle();
            Ui.Toast(Loc.T($"'{Shown}' saved to the library", $"«{Shown}» сохранён в библиотеку") + (Valid ? "" : Loc.T(" (with errors: cannot be planted yet)", " (с ошибками — посадить пока нельзя)")));
        }
        catch (Exception e) { Ui.Toast(Loc.T("cannot save: ", "не сохранить: ") + e.Message, true); }
    }

    void DeleteDesign()
    {
        if (currentPath == null || !File.Exists(currentPath)) { Ui.Toast(Loc.T("this design is not in the library yet", "этот дизайн ещё не в библиотеке"), true); return; }
        try { File.Delete(currentPath); Ui.Toast(Loc.T($"'{Shown}' deleted from the library", $"«{Shown}» удалён из библиотеки")); }
        catch (Exception e) { Ui.Toast(Loc.T("cannot delete: ", "не удалить: ") + e.Message, true); return; }
        currentPath = null;
        dirty = false;
        ListLibrary();
        if (entries.Count > 0) Pick(0); else NewDesign();
    }

    // A design from the selected living body (read on the simulation thread, between ticks).
    void TakeSelected()
    {
        var a = Main.View.Selected;
        if (a == null || a.Dead) { Ui.Toast(Loc.T("first select a living creature on the map (click)", "сначала выберите живое существо на карте (клик)"), true); return; }
        Main.Sim.Do(w =>
        {
            if (a.Dead) { Ui.Post(() => Ui.Toast(Loc.T("the creature died before it could be read", "существо умерло раньше, чем его успели прочитать"), true)); return; }
            var d = CreatureDesign.FromAgent(w, a);
            Ui.Post(() =>
            {
                if (dirty) Ui.Toast(Loc.T($"unsaved changes to '{Shown}' discarded", $"несохранённые изменения «{Shown}» отброшены"));
                LoadDesign(d, null);
                dirty = true;
                UpdateTitle();
                Ui.Toast(Loc.T($"took the genome of #{a.Id}: {d.Assemble().Length} bytes, {d.Body.Values.Sum()} molecules", $"взят геном #{a.Id}: {d.Assemble().Length} байт, {d.Body.Values.Sum()} молекул"));
            });
        });
    }

    // ---- body ----

    void RebuildBody()
    {
        foreach (var c in bodyRows.GetChildren()) c.QueueFree();
        chemOf = Main?.World;
        foreach (var (k, n) in current.Body ?? new()) AddBodyRow(k, n);
        UpdateBodyTotal();
    }

    void AddBodyRow(string key, int n)
    {
        var chem = Main.World?.Chem;
        var opt = UiKit.Options();
        opt.FitToLongestItem = false;
        opt.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        opt.ClipText = true;
        opt.CustomMinimumSize = new Vector2(160, 0);
        opt.AddItem(Loc.T("any: whatever is at hand", "any — что найдётся"), 0);
        opt.AddItem(chem != null ? Loc.T($"gas: the gas ({chem.Name[chem.Gas]})", $"gas — газ ({chem.Name[chem.Gas]})") : Loc.T("gas: the world's gas", "gas — газ мира"), 1);
        for (int s = 0; s < Chemistry.S; s++)
            opt.AddItem(chem != null ? $"{s}: {chem.Name[s]} {chem.Formula(s)} · E{chem.E[s]}{(chem.Solid[s] ? Loc.T(" · solid", " · твёрд") : "")}{(World.Harmful(chem, s) ? Loc.T(" · reactive", " · реакц.") : "")}" : s.ToString(), 2 + s);
        int sel;
        if (key.Equals("any", StringComparison.OrdinalIgnoreCase) || key == "*") sel = 0;
        else if (key.Equals("gas", StringComparison.OrdinalIgnoreCase)) sel = 1;
        else
        {
            int s = chem != null ? CreatureDesign.ResolveMolecule(chem, key) : int.TryParse(key, out int q) ? q : int.MinValue;
            if (s >= 0 && s < Chemistry.S) sel = 2 + s;
            else { opt.AddItem(Loc.T($"'{key}': not in this world", $"«{key}» — нет в этом мире"), 100); opt.SetItemMetadata(opt.ItemCount - 1, key); sel = opt.ItemCount - 1; }
        }
        opt.Selected = sel;
        opt.ItemSelected += _ => BodyChanged();
        var spin = UiKit.Spin(0, 999, 1, n, 76);
        spin.ValueChanged += _ => BodyChanged();
        var row = UiKit.Row(4);
        var del = UiKit.Button("✕", null, Loc.T("remove", "убрать"));
        del.Flat = true;
        del.Pressed += () => { row.QueueFree(); bodyRows.RemoveChild(row); BodyChanged(); };
        row.AddChild(opt);
        row.AddChild(spin);
        row.AddChild(del);
        bodyRows.AddChild(row);
    }

    void BodyChanged()
    {
        if (loading) return;
        var body = new Dictionary<string, int>();
        foreach (var row in bodyRows.GetChildren().OfType<HBoxContainer>())
        {
            if (row.IsQueuedForDeletion()) continue;
            var opt = row.GetChild<OptionButton>(0);
            var spin = row.GetChild<SpinBox>(1);
            int id = opt.GetSelectedId();
            string key = id == 0 ? "any" : id == 1 ? "gas" : id == 100 ? (string)opt.GetItemMetadata(opt.Selected) : (id - 2).ToString(CultureInfo.InvariantCulture);
            body[key] = body.GetValueOrDefault(key) + (int)spin.Value;
        }
        current.Body = body;
        UpdateBodyTotal();
        Touch();
    }

    void UpdateBodyTotal()
    {
        int total = current.Body?.Values.Sum() ?? 0;
        bodyTotal.Text = Loc.T($"total {total} (fewer than {P.MinBody} cannot hold together)", $"всего {total} (меньше {P.MinBody} не держится)");
        bodyTotal.AddThemeColorOverride("font_color", total < P.MinBody ? UiKit.Bad : UiKit.Dim);
    }

    void Explain()
    {
        string m = matter.Selected == 0
            ? Loc.T("Molecules are taken from the loose matter of the cell and its four neighbours and from the soft top block (except its last molecule). If there is not enough, nothing is taken and planting fails.",
                    "Молекулы берутся из рыхлого вещества клетки и четырёх соседей и из мягкого верхнего блока (кроме его последней молекулы). Не хватит — ничего не берётся, посадка не удаётся.")
            : Loc.T("Molecules are brought from outside: every atom is booked as hand input (HandInput), like the pour brush.",
                    "Молекулы приносятся извне: каждый атом записывается в приход рукой (HandInput), как у кисти «насыпать».");
        string e = energySrc.Selected == 0
            ? Loc.T("Energy comes from exothermic splits of the loose matter there; the products stay in the world.",
                    "Энергия — от выгодных распадов рыхлого вещества там же; продукты остаются в мире.")
            : Loc.T("Energy is brought from outside and booked in the hand ledger (HandEnergy).",
                    "Энергия приносится извне и записывается в журнал руки (HandEnergy).");
        explain.Text = m + " " + e + Loc.T(" Nothing appears for free.", " Даром ничего не появляется.");
    }

    // ---- checking ----

    void Touch(bool genome = true)
    {
        dirty = true;
        UpdateTitle();
        checkAt = Time.GetTicksMsec() / 1000.0 + (genome ? 0.25 : 0.05);
    }

    void Validate()
    {
        foreach (int l in errorLines) if (l < code.GetLineCount()) code.SetLineBackgroundColor(l, new Color(0, 0, 0, 0));
        errorLines.Clear();
        bool asmOk = GenomeAsm.TryAssemble(current.Genome, out var bytes, out var asmErrors);
        foreach (var e in asmErrors)
        {
            int l = Math.Clamp(e.Line - 1, 0, Math.Max(0, code.GetLineCount() - 1));
            code.SetLineBackgroundColor(l, new Color(1f, 0.25f, 0.2f, 0.22f));
            errorLines.Add(l);
        }
        var chem = Main.World?.Chem;
        var other = new List<string>();
        if (chem != null)
        {
            var all = current.Check(chem);
            var asmText = asmErrors.Select(e => e.ToString()).ToHashSet();
            other = all.Where(s => !asmText.Contains(s)).ToList();
        }
        Valid = asmOk && other.Count == 0;
        if (asmOk)
        {
            int instr = 0;
            for (int i = 0; i < bytes.Length;) { Genome.DisAt(bytes, i, out int len); i += len; instr++; }
            int enz = bytes.Count(b => (b & 63) == Genome.EnzymeOp);
            string line = Loc.T($"genome valid: {bytes.Length} bytes, {instr} instructions, {enz} protein genes · length cost {P.CostLen * bytes.Length:0.0000} energy per tick",
                                $"геном верен: {bytes.Length} байт, {instr} команд, генов белка {enz} · плата за длину {P.CostLen * bytes.Length:0.0000} энергии за тик");
            status.Text = other.Count == 0 ? line : line + "\n" + string.Join("\n", other);
            status.AddThemeColorOverride("font_color", other.Count == 0 ? UiKit.Good : UiKit.Bad);
            if (lastBytes == null || !lastBytes.AsSpan().SequenceEqual(bytes))
            {
                lastBytes = bytes;
                portrait.Texture = ImageTexture.CreateFromImage(Portrait.Render(bytes, 56));
            }
        }
        else
        {
            status.Text = Loc.T($"{asmErrors.Count} errors: ", $"ошибок {asmErrors.Count}: ") + string.Join("; ", asmErrors.Take(4)) + (asmErrors.Count > 4 ? " …" : "") +
                          (other.Count > 0 ? "\n" + string.Join("\n", other) : "");
            status.AddThemeColorOverride("font_color", UiKit.Bad);
        }
        UpdateLooks(asmOk ? bytes : null);
    }

    void UpdateLooks(byte[] bytes)
    {
        float h = current.Hue ?? 0, s = current.Sat ?? 0, v = current.Val ?? 0;
        int sh = current.Shape ?? 0;
        bool custom = current.Hue != null;
        if (!custom && bytes != null)
        {
            var a = new Agent(0, 0, 0, bytes);
            Looks.Apply(a);
            h = a.Hue; s = a.Sat; v = a.Val; sh = a.Shape;
        }
        hue.Editable = sat.Editable = val.Editable = custom;
        shape.Disabled = !custom;
        swatch.Color = Color.FromHsv(h, s, v);
        lookNote.Text = custom ? Loc.T($"custom looks: {Looks.ShapeNames[sh]}. Descendants inherit the looks with drift.",
                                       $"свой облик: {Looks.ShapeNames[sh]}. Потомки наследуют облик с дрейфом.")
                               : Loc.T($"looks from the genome, as for self-assembled bodies: {Looks.ShapeNames[sh]}. Turn on 'Custom colour and shape' to set them.",
                                       $"облик из генома, как у самозарождённых: {Looks.ShapeNames[sh]}. Включите «свой цвет и форма», чтобы задать.");
    }

    // ---- planting ----

    // Brush 5 clicked a column: plant the current design there.
    public void Spawn(int cell)
    {
        var w = Main.World;
        if (w == null) return;
        var errors = current.Check(w.Chem);
        if (errors.Count > 0) { Ui.Toast(Loc.T($"'{Shown}' cannot be planted: {string.Join("; ", errors.Take(3))}", $"«{Shown}» нельзя посадить: {string.Join("; ", errors.Take(3))}"), true); return; }
        var options = new SpawnOptions
        {
            Matter = matter.Selected == 0 ? MatterSource.Local : MatterSource.Import,
            Energy = energySrc.Selected == 0 ? EnergySource.Local : EnergySource.Import,
            Count = (int)count.Value,
            Radius = Math.Max(1, (int)Main.BrushR),
        };
        Main.Sim.SpawnDesign(current, cell % w.W, cell / w.W, options);
    }

    public string BrushText => Loc.En
        ? $"plant '{Shown}' ×{(int)count.Value} · matter {(matter.Selected == 0 ? "local" : "from outside")}, energy {(energySrc.Selected == 0 ? "local" : "from outside")}" + (Valid ? "" : " · the design has errors (F7)")
        : $"посадить «{Shown}» ×{(int)count.Value} · вещество {(matter.Selected == 0 ? "местное" : "извне")}, энергия {(energySrc.Selected == 0 ? "местная" : "извне")}" + (Valid ? "" : " · в дизайне ошибки (F7)");

    // What the place under the cursor offers for the body (loose matter of the cell and its neighbours).
    void Availability()
    {
        var w = Main.World;
        if (w == null) return;
        var m = GetViewport().GetMousePosition();
        int cell = Main.View.BrushCell >= 0 ? Main.View.BrushCell : Ui.IsOver(m) ? lastCell : Main.View.PickCell(m);
        if (cell < 0) cell = lastCell;
        if (cell < 0) { availability.Text = Loc.T("Hover over the map to see how much of the needed matter is nearby.", "Наведите курсор на карту — здесь появится, сколько нужного вещества рядом."); return; }
        lastCell = cell;
        var cells = new[] { cell, w.Nb(cell, 0), w.Nb(cell, 1), w.Nb(cell, 2), w.Nb(cell, 3) }.Distinct().ToArray();
        float Loose(int s) => cells.Sum(c => w.C[s][c].F);
        var ch = w.Chem;
        var parts = new List<string>();
        var errs = new List<string>();
        var body = current.ResolveBody(ch, errs);
        bool all = true;
        foreach (var (s, n) in body.OrderBy(kv => kv.Key))
        {
            float have = s == CreatureDesign.AnyMolecule
                ? Enumerable.Range(0, Chemistry.S).Where(q => q != ch.Gas).Sum(Loose)
                : Loose(s);
            bool ok = have >= n;
            all &= ok;
            parts.Add($"{(s == CreatureDesign.AnyMolecule ? "any" : ch.Name[s])} {have:0}/{n}{(ok ? "" : Loc.T(" (short)", " — мало"))}");
        }
        float e = 0;
        for (int s = 0; s < Chemistry.S; s++) if (ch.SplitExo[s]) e += Loose(s) * ch.SplitEnergy(s);
        bool eok = e >= current.Energy;
        string found = parts.Count > 0 ? string.Join(" · ", parts) : "—";
        string place = Loc.T($"At the cursor ({cell % w.W}, {cell / w.W}), loose matter of the cell and its neighbours: {found}. Energy from local splits ≈ {e:0} of {current.Energy:0}.",
                             $"У курсора ({cell % w.W}, {cell / w.W}), рыхлое вещество клетки и соседей: {found}. Энергия местных распадов ≈ {e:0} из {current.Energy:0}.");
        if (matter.Selected == 0 && !all) place += Loc.T(" The soft top block may add its own; otherwise use 'bring from outside'.", " Мягкий верхний блок может добавить своё; иначе — «принести извне».");
        availability.Text = place;
        availability.AddThemeColorOverride("font_color", (matter.Selected == 1 || all) && (energySrc.Selected == 1 || eok) ? UiKit.Dim : new Color(1f, 0.7f, 0.5f));
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (checkAt >= 0 && now >= checkAt) { checkAt = -1; Validate(); }
        if (Main.World != chemOf && Main.World != null) { loading = true; RebuildBody(); loading = false; Validate(); }
        if (now >= availAt) { availAt = now + 0.25; Availability(); }
    }

    protected override void OnOpen()
    {
        ListLibrary();
        Validate();
    }

    public CreatureDesign Current => current;
}
