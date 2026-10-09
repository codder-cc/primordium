using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;

namespace Primordium;

// J: the matter library. Substances as recipes of this world's molecules (MatterRecipe): computed starters
// for this seed, the files of user://materials, and samples taken from the world this session. The
// editor composes a recipe from species, order and form and shows what it is by the engine's formulas
// (density, packing, cohesion, strength, reactive harm, energy); brush 1 pours exactly it (matter booked
// by the hand), the poisoning catastrophe (F11) can spray it. A file from another chemistry is mapped to
// the nearest molecules of this one, with the mapping and a warning shown.
public partial class MatterWindow : UiWindow
{
    public enum SampleKind { Auto, Block, Loose }

    List<MatterRecipe> starters = new();
    readonly List<MatterRecipe> session = new();
    List<MatterLibrary.Entry> files = new();
    readonly List<MatterRecipe> listed = new();   // what each line of the list holds (null: a header or a broken file)

    MatterRecipe current, origin;   // what the editor holds, and the list entry it came from
    MatterMapping mapping;
    World chemOf;
    bool loading, dirty;
    double recalcAt = -1, cursorAt;
    int lastCell = -1;
    string cursorText = "";

    ItemList library;
    LineEdit nameEdit, descEdit;
    OptionButton kind, form;
    HSlider order;
    Label orderLabel, card, mapNote, cursorNote, total;
    CheckBox ownColour;
    ColorPickerButton colour;
    VBoxContainer parts;
    TextureRect preview;
    TabContainer tabs;

    public MatterWindow() : base("matter", Loc.T("Matter library", "Библиотека веществ"), new Vector2(1120, 640))
    {
        MinSize = new Vector2(880, 480);
        var cols = UiKit.Row(12);
        cols.SizeFlagsVertical = SizeFlags.ExpandFill;
        Body.AddChild(cols);
        cols.AddChild(LibraryColumn());
        cols.AddChild(EditorColumn());
        cols.AddChild(CardColumn());
    }

    // ---- layout ----

    Control LibraryColumn()
    {
        var col = UiKit.Col(6);
        col.CustomMinimumSize = new Vector2(250, 0);
        col.AddChild(UiKit.Title(Loc.T("Substances", "Вещества"), 13));
        library = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None, TooltipText = "user://materials" };
        library.ItemSelected += i => Pick((int)i);
        col.AddChild(library);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 4);
        foreach (var b in new Control[]
        {
            UiKit.Button(Loc.T("Compose", "Составить"), NewRecipe, Loc.T("a new recipe from this world's molecules", "новый рецепт из молекул этого мира")),
            UiKit.Button(Loc.T("Copy", "Копия"), Duplicate, Loc.T("a copy of the current one under a new name", "копия текущего под новым именем")),
            UiKit.Button(Loc.T("Save", "Сохранить"), SaveRecipe, Loc.T("to user://materials as JSON (with this world's chemistry signature)", "в user://materials как JSON (с подписью химии этого мира)")),
            new ConfirmButton(Loc.T("Delete", "Удалить"), DeleteRecipe, Loc.T("delete the file (or drop the sample)", "удалить файл (или убрать образец)")),
        })
        {
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            grid.AddChild(b);
        }
        col.AddChild(grid);
        col.AddChild(UiKit.Button(Loc.T("Open folder (import / export)", "Открыть папку (импорт / экспорт)"), OpenFolder,
            Loc.T("JSON files put there appear in the list; a file from another world is mapped onto this world's molecules", "положенные туда JSON-файлы появляются в списке; файл из другого мира переводится на молекулы этого")));
        col.AddChild(new HSeparator());
        col.AddChild(UiKit.Text(Loc.T("Sample from the world (where the cursor last was on the map):", "Взять из мира (где курсор был на карте последним):"), 12, UiKit.Dim, null, true));
        var sample = new GridContainer { Columns = 3 };
        sample.AddThemeConstantOverride("h_separation", 4);
        foreach (var b in new Control[]
        {
            UiKit.Button(Loc.T("Block", "Блок"), () => SampleAt(lastCell, SampleKind.Block, false), Loc.T("the top block under the cursor: its molecules and lattice order exactly (also I with brush 1)", "верхний блок под курсором: его молекулы и порядок решётки точно (также I с кистью 1)")),
            UiKit.Button(Loc.T("Loose", "Рыхлое"), () => SampleAt(lastCell, SampleKind.Loose, false), Loc.T("the loose matter lying there", "рыхлое вещество, лежащее там")),
            UiKit.Button(Loc.T("Body", "Тело"), SampleSelected, Loc.T("the molecules of the selected creature", "молекулы выбранного существа")),
        })
        {
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            sample.AddChild(b);
        }
        col.AddChild(sample);
        cursorNote = UiKit.Text("", 11, UiKit.Dim, null, true);
        col.AddChild(cursorNote);
        return col;
    }

    Control EditorColumn()
    {
        var col = UiKit.Col(6);
        col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nameEdit = UiKit.Edit("", Loc.T("name (only a label)", "имя (только метка)"));
        nameEdit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nameEdit.TextChanged += t => { if (!loading) { current.Name = t; Touch(); } };
        col.AddChild(UiKit.Row(8, Fixed(Loc.T("Name", "Имя"), 70), nameEdit));
        descEdit = UiKit.Edit("", Loc.T("description", "описание"));
        descEdit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        descEdit.TextChanged += t => { if (!loading) { current.Description = t; Touch(); } };
        col.AddChild(descEdit);
        kind = UiKit.Options(MatterRecipe.Kinds.Select(MatterRecipe.KindName).ToArray());
        kind.ItemSelected += i => { if (!loading) { current.Kind = MatterRecipe.Kinds[(int)i]; Touch(); } };
        ownColour = UiKit.Check(Loc.T("colour tag", "свой цвет"), false, on => { if (!loading) { current.Colour = on ? "#" + colour.Color.ToHtml(false) : null; Touch(); } });
        colour = new ColorPickerButton { CustomMinimumSize = new Vector2(40, 24), EditAlpha = false, FocusMode = FocusModeEnum.None };
        colour.ColorChanged += c => { if (!loading && ownColour.ButtonPressed) { current.Colour = "#" + c.ToHtml(false); Touch(); } };
        col.AddChild(UiKit.Row(8, Fixed(Loc.T("Kind", "Вид"), 70), kind, UiKit.Spacer(), ownColour, colour));
        form = UiKit.Options(Loc.T("blocks", "блоками"), Loc.T("loose / spray", "рыхло / распылить"));
        form.ItemSelected += i => { if (!loading) { current.Form = i == 1 ? "loose" : "block"; Touch(); } };
        col.AddChild(UiKit.Row(8, Fixed(Loc.T("Form", "Форма"), 70), form));
        order = new HSlider { MinValue = 0, MaxValue = 255, Step = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, FocusMode = FocusModeEnum.None, Scrollable = false, CustomMinimumSize = new Vector2(0, 16) };
        order.ValueChanged += v => { if (!loading) { current.Order = (int)v; Touch(); } };
        orderLabel = UiKit.Text("", 12, UiKit.Dim);
        orderLabel.CustomMinimumSize = new Vector2(150, 0);
        col.AddChild(UiKit.Row(8, Fixed(Loc.T("Order", "Порядок"), 70), order, orderLabel));
        col.AddChild(UiKit.Text(Loc.T("Lattice order sets packing: an ordered, compacted block holds more molecules in the same room (porosity falls). Freshly poured matter is 5 %, rock under pressure anneals to 96 %.",
                                      "Порядок решётки задаёт упаковку: упорядоченный, уплотнённый блок вмещает больше молекул в тот же объём (пористость падает). Свежая насыпь — 5 %, порода под давлением отжигается до 96 %."), 11, UiKit.Dim, null, true));
        col.AddChild(new HSeparator());
        col.AddChild(UiKit.Text(Loc.T("Molecules of this world (relative amounts):", "Молекулы этого мира (относительные количества):"), 12, UiKit.Dim));
        parts = UiKit.Col(3);
        var scroll = UiKit.Scroll(parts);
        scroll.CustomMinimumSize = new Vector2(0, 150);
        col.AddChild(scroll);
        total = UiKit.Text("", 12, UiKit.Dim);
        col.AddChild(UiKit.Row(8, UiKit.Button(Loc.T("+ molecule", "+ молекула"), () => { AddPartRow(FreeSpecies(), 10); PartsChanged(); }), total));
        mapNote = UiKit.Text("", 12, UiKit.Acc, null, true);
        col.AddChild(mapNote);
        return col;
    }

    Control CardColumn()
    {
        var col = UiKit.Col(6);
        col.CustomMinimumSize = new Vector2(330, 0);
        col.AddChild(UiKit.Title(Loc.T("What it is (by the engine's formulas)", "Что это (по формулам движка)"), 13));
        preview = new TextureRect { CustomMinimumSize = new Vector2(120, 120), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.Nearest,
            TooltipText = Loc.T("a block of it: facets and gloss by lattice order, darker by molecules per volume, pores by porosity", "блок из него: грани и блеск по порядку решётки, темнее — больше молекул на объём, поры — по пористости") };
        col.AddChild(preview);
        card = UiKit.Text("", 12, UiKit.Fg, null, true);
        col.AddChild(UiKit.Scroll(card));
        var pour = UiKit.Button(Loc.T("Pour with brush 1", "Насыпать кистью 1"), UseForBrush, Loc.T("brush 1 pours exactly this recipe; matter and its bond energy are booked as brought by the hand", "кисть 1 сыплет ровно этот рецепт; вещество и его энергия связи записываются как принесённые рукой"));
        pour.AddThemeColorOverride("font_color", UiKit.Acc);
        var poison = UiKit.Button(Loc.T("Spray as poisoning (F11)", "Распылить отравлением (F11)"), UseForPoisoning, Loc.T("choose it in the poisoning catastrophe", "выбрать его в катастрофе «отравление»"));
        col.AddChild(UiKit.Row(8, pour, poison));
        return col;
    }

    static Label Fixed(string s, float w)
    {
        var l = UiKit.Text(s, 13, UiKit.Dim);
        l.CustomMinimumSize = new Vector2(w, 0);
        return l;
    }

    // ---- the list ----

    public override void _Ready()
    {
        Directory.CreateDirectory(UiManager.MaterialsDir);
        Refresh(true);
    }

    // Every substance the window offers, for the poisoning catastrophe: starters, files, samples.
    public IEnumerable<MatterRecipe> All => starters.Concat(files.Where(f => f.Recipe != null).Select(f => f.Recipe)).Concat(session);

    void Refresh(bool pickFirst = false)
    {
        var w = Main?.World;
        if (w != null && w != chemOf) { chemOf = w; starters = MatterRecipe.Starters(w); }
        files = MatterLibrary.List(UiManager.MaterialsDir);
        library.Clear();
        listed.Clear();
        void Header(string text)
        {
            int i = library.AddItem(text);
            library.SetItemSelectable(i, false);
            library.SetItemCustomFgColor(i, UiKit.Dim);
            listed.Add(null);
        }
        void Item(MatterRecipe r, string tip)
        {
            int i = library.AddItem(Line(r), Swatch(r));
            library.SetItemTooltip(i, tip);
            listed.Add(r);
            if (r == current || r == origin) library.Select(i);
        }
        Header(Loc.T($"— computed for seed {w?.Seed} —", $"— посчитано для seed {w?.Seed} —"));
        foreach (var r in starters) Item(r, r.Description);
        if (session.Count > 0) Header(Loc.T("— samples and drafts (not saved) —", "— образцы и черновики (не сохранены) —"));
        foreach (var r in session) Item(r, r.Source);
        Header(Loc.T("— library (user://materials) —", "— библиотека (user://materials) —"));
        foreach (var f in files)
        {
            if (f.Recipe != null) { Item(f.Recipe, $"{f.Recipe.Description}\n{Path.GetFileName(f.Path)}"); continue; }
            int i = library.AddItem(Path.GetFileName(f.Path) + Loc.T(" (error)", " (ошибка)"));
            library.SetItemTooltip(i, f.Error);
            library.SetItemCustomFgColor(i, UiKit.Bad);
            listed.Add(null);
        }
        if (pickFirst && current == null && starters.Count > 0) Load(starters[0]);
    }

    string Line(MatterRecipe r)
    {
        bool foreign = r.Signature != null && Main?.World != null && !r.Signature.Matches(Main.World.Chem);
        return $"{(string.IsNullOrWhiteSpace(r.Name) ? Loc.T("untitled", "без имени") : r.Name)} · {MatterRecipe.KindName(r.Kind)}{(foreign ? Loc.T(" · other world", " · другой мир") : "")}";
    }

    Texture2D Swatch(MatterRecipe r)
    {
        var w = Main?.World;
        if (w == null) return null;
        var c = Main.MatterColour(w, r, r.Resolve(w).Mix);
        var img = Image.CreateEmpty(12, 12, false, Image.Format.Rgb8);
        img.Fill(new Color(c.R, c.G, c.B));
        return ImageTexture.CreateFromImage(img);
    }

    void Pick(int i)
    {
        if (i < 0 || i >= listed.Count || listed[i] == null) return;
        var r = listed[i];
        if (r == current) return;
        if (dirty && current != null && !session.Contains(current))
        {
            session.Add(current);   // nothing typed is lost: it waits among the drafts
            Ui.Toast(Loc.T($"unsaved changes to '{current.Name}' kept among the drafts", $"несохранённые изменения «{current.Name}» остались в черновиках"));
        }
        Load(r);
        Refresh();
    }

    // The editor works on the entry itself (a sample or a draft) or on a copy (a starter or a file:
    // saving writes it back by name).
    void Load(MatterRecipe r)
    {
        var w = Main.World;
        origin = r;
        var res = r.Resolve(w);
        if (!res.Exact)
        {
            // Another chemistry: the editor holds the mapped recipe of this world; the original file stays.
            var mapped = MatterRecipe.FromMix(w, res.Mix, r.Order, r.Loose, r.Name + Loc.T(" (this world)", " (этот мир)"), $"mapped from {Path.GetFileName(r.Path ?? r.Name)} (seed {r.Signature?.Seed})", r.Kind);
            mapped.Description = r.Description; mapped.Colour = r.Colour;
            current = mapped;
            mapping = res;
            dirty = true;
        }
        else
        {
            current = session.Contains(r) ? r : r.Clone();
            if (!session.Contains(r)) current.Path = r.Path;
            mapping = null;
            dirty = false;
        }
        Fill();
    }

    void Fill()
    {
        loading = true;
        nameEdit.Text = current.Name ?? "";
        descEdit.Text = current.Description ?? "";
        kind.Selected = Math.Max(0, Array.IndexOf(MatterRecipe.Kinds, current.Kind));
        form.Selected = current.Loose ? 1 : 0;
        order.SetValueNoSignal(current.Order);
        ownColour.ButtonPressed = current.Colour != null;
        { var mc = Main.MatterColour(Main.World, current, current.MixOf(Main.World.Chem)); colour.Color = current.Colour != null ? Color.FromHtml(current.Colour) : new Color(mc.R, mc.G, mc.B); }
        foreach (var c in parts.GetChildren()) { parts.RemoveChild(c); c.QueueFree(); }
        var mix = current.MixOf(Main.World.Chem);
        foreach (var p in current.Parts.Where(p => p.Species >= 0 && p.Species < Chemistry.S)) AddPartRow(p.Species, Math.Max(1, (int)Math.Round(p.Fraction * 1000)));
        loading = false;
        Recalc();
        UpdateTitle();
    }

    void UpdateTitle() => SetTitle(Loc.T("Matter library", "Библиотека веществ") + $" — {(string.IsNullOrWhiteSpace(current?.Name) ? Loc.T("untitled", "без имени") : current.Name)}{(dirty ? " •" : "")}");

    void Touch()
    {
        dirty = true;
        UpdateTitle();
        recalcAt = Time.GetTicksMsec() / 1000.0 + 0.1;
    }

    // ---- parts ----

    int FreeSpecies()
    {
        var used = current.Parts.Select(p => p.Species).ToHashSet();
        for (int s = 0; s < Chemistry.S; s++) if (!used.Contains(s) && s != Main.World.Chem.Gas) return s;
        return 0;
    }

    void AddPartRow(int species, int weight)
    {
        var ch = Main.World.Chem;
        var opt = UiKit.Options();
        opt.FitToLongestItem = false;
        opt.ClipText = true;
        opt.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        opt.CustomMinimumSize = new Vector2(200, 0);
        for (int s = 0; s < Chemistry.S; s++)
            opt.AddItem($"{s}: {ch.Name[s]} {ch.Formula(s)}{(ch.Excitation[s] > 0 ? "*" : "")} · E{ch.E[s]} · {Loc.T("bond", "связь")} {ch.Bond[s]:0.00}{(s == ch.Gas ? Loc.T(" · gas", " · газ") : "")}{(ch.Reactivity[s] > 0 ? Loc.T($" · react. {ch.Reactivity[s]:0.0}", $" · реакц. {ch.Reactivity[s]:0.0}") : "")}", s);
        opt.Selected = Math.Clamp(species, 0, Chemistry.S - 1);
        opt.ItemSelected += _ => PartsChanged();
        var spin = UiKit.Spin(1, 100000, 1, weight, 84);
        spin.ValueChanged += _ => PartsChanged();
        var share = UiKit.Text("", 12, UiKit.Dim);
        share.CustomMinimumSize = new Vector2(52, 0);
        var row = UiKit.Row(4);
        var del = UiKit.Button("✕", null, Loc.T("remove", "убрать"));
        del.Flat = true;
        del.Pressed += () => { parts.RemoveChild(row); row.QueueFree(); PartsChanged(); };
        row.AddChild(opt);
        row.AddChild(spin);
        row.AddChild(share);
        row.AddChild(del);
        parts.AddChild(row);
    }

    void PartsChanged()
    {
        if (loading) return;
        var weights = new double[Chemistry.S];
        foreach (var row in parts.GetChildren().OfType<HBoxContainer>())
        {
            if (row.IsQueuedForDeletion()) continue;
            weights[row.GetChild<OptionButton>(0).GetSelectedId()] += row.GetChild<SpinBox>(1).Value;
        }
        double sum = weights.Sum();
        current.Parts = Enumerable.Range(0, Chemistry.S).Where(s => weights[s] > 0).Select(s => new MatterPart { Species = s, Fraction = Math.Round(weights[s] / sum, 6) }).ToList();
        current.Signature = ChemSignature.Of(Main.World);
        mapping = null;
        Touch();
    }

    // ---- the card ----

    void Recalc()
    {
        var w = Main.World;
        if (w == null || current == null) return;
        var ch = w.Chem;
        var mix = current.Resolve(w).Mix;
        double sum = 0;
        foreach (var row in parts.GetChildren().OfType<HBoxContainer>())
        {
            if (row.IsQueuedForDeletion()) continue;
            sum += row.GetChild<SpinBox>(1).Value;
        }
        foreach (var row in parts.GetChildren().OfType<HBoxContainer>())
        {
            if (row.IsQueuedForDeletion()) continue;
            row.GetChild<Label>(2).Text = sum > 0 ? $"{row.GetChild<SpinBox>(1).Value / sum * 100:0.#}%" : "";
        }
        total.Text = Loc.T($"{current.Parts.Count} {(current.Parts.Count == 1 ? "kind" : "kinds")}", $"видов: {current.Parts.Count}");
        int o = current.Order;
        orderLabel.Text = Loc.T($"{o / 255.0 * 100:0}% ({o}/255)", $"{o / 255.0 * 100:0}% ({o}/255)");
        var p = MatterRecipe.Props(ch, mix, o);
        if (p.Kinds == 0) { card.Text = Loc.T("Add molecules to see what it is.", "Добавьте молекулы, чтобы увидеть, что это."); preview.Texture = null; return; }
        double densest = Enumerable.Range(0, Chemistry.S).Max(s => ch.Mass[s] / ch.Volume[s]);   // a pure block at full order
        string harm = p.HarmVsDecay >= 1 ? Loc.T("harmful: one held molecule wears proteins faster than they decay", "вредно: одна молекула в теле портит белки быстрее их распада")
                    : p.ReactHeld > 0 ? Loc.T("mildly reactive", "слабо реакционно") : Loc.T("inert: no excitation to spend", "инертно: нечего тратить");
        string energyNote = Loc.T("The engine has no energy for order (pressure anneals blocks for free): pouring books the matter and its bond energy; a high order is player content.",
                                  "У порядка в движке нет энергии (давление отжигает блоки даром): насыпка записывает вещество и его энергию связи; высокий порядок — содержимое от игрока.");
        card.Text = Loc.T(
            $"Molecules per block: {p.MolPerBlock:0} ({p.MolPerBlock / P.VoxelSpace:0.000} per unit of volume)\n" +
            $"Order {o / 255.0:P0} · porosity {p.Porosity:P1}\n" +
            $"Mass per molecule {p.MassPerMol:0.00} · density {p.Density:0.000} ({p.Density / densest:P0} of the world's densest packing)\n" +
            $"Cohesion {p.Cohesion:0.000} · gnaw barrier {p.Barrier:0.00} (work ×{Math.Exp(p.Barrier - P.FaceBarrier):G3}) · a full block bears {p.Compression:0.0}\n" +
            $"Reactive harm at 15 °C: {p.ReactHeld:0.00000} per held molecule a tick (×{p.HarmVsDecay:0.00} protein decay), {p.ReactLying:0.00000} lying — {harm}\n" +
            $"Bond energy {p.EnergyPerMol:0.0} per molecule, {p.EnergyPerBlock:0} per block\n" +
            $"{p.Kinds} {(p.Kinds == 1 ? "kind" : "kinds")}{(p.GasShare > 0 ? $", gas {p.GasShare:P0}" : "")} · form: {(current.Loose ? "loose / spray" : "blocks")}\n" +
            $"Main molecules: {MatterRecipe.MixText(ch, mix, true, 6)}\n\n{energyNote}",
            $"Молекул в блоке: {p.MolPerBlock:0} ({p.MolPerBlock / P.VoxelSpace:0.000} на единицу объёма)\n" +
            $"Порядок {o / 255.0:P0} · пористость {p.Porosity:P1}\n" +
            $"Масса молекулы {p.MassPerMol:0.00} · плотность {p.Density:0.000} ({p.Density / densest:P0} от самой плотной упаковки мира)\n" +
            $"Связность {p.Cohesion:0.000} · барьер грызения {p.Barrier:0.00} (работа ×{Math.Exp(p.Barrier - P.FaceBarrier):G3}) · полный блок держит {p.Compression:0.0}\n" +
            $"Реактивный урон при 15 °C: {p.ReactHeld:0.00000} на молекулу в теле за тик (×{p.HarmVsDecay:0.00} распада белка), {p.ReactLying:0.00000} лёжа — {harm}\n" +
            $"Энергия связи {p.EnergyPerMol:0.0} на молекулу, {p.EnergyPerBlock:0} на блок\n" +
            $"Видов {p.Kinds}{(p.GasShare > 0 ? $", газа {p.GasShare:P0}" : "")} · форма: {(current.Loose ? "рыхло / распылить" : "блоками")}\n" +
            $"Главные молекулы: {MatterRecipe.MixText(ch, mix, false, 6)}\n\n{energyNote}");
        preview.Texture = ImageTexture.CreateFromImage(Crystal(Main.MatterColour(w, current, mix), o / 255f, (float)(p.Density / densest), (float)p.Porosity, current.Loose));
        if (mapping != null && !mapping.Exact)
            mapNote.Text = mapping.Warning + "\n" + string.Join("\n", mapping.Map.Select(m => $"{m.from} → {ch.Name[m.to]} ({ch.Formula(m.to)}{(ch.Excitation[m.to] > 0 ? "*" : "")}), {Loc.T("distance", "расстояние")} {m.distance:0.00}"));
        else mapNote.Text = "";
    }

    // A small picture of a block of it: facets with gloss that grow with order, darker with density,
    // pores by porosity, a heap of grains when loose.
    static Image Crystal(Rgb c, float order, float density, float porosity, bool loose)
    {
        const int n = 96;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgb8);
        float dark = 1.15f - 0.55f * Math.Clamp(density, 0, 1);
        int cell = loose ? 4 : (int)(6 + 18 * order);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int cx = x / cell, cy = y / cell;
                uint h = Hash((uint)(cx * 73856093) ^ (uint)(cy * 19349663));
                float fx = (x % cell) / (float)cell, fy = (y % cell) / (float)cell;
                bool upper = ((h >> 3) & 1) == 0 ? fx > fy : fx + fy > 1;   // each cell split into two triangles
                uint hf = Hash(h + (upper ? 1u : 2u));
                float tilt = ((hf & 255) / 255f - 0.5f);
                float shade = 1 + tilt * (0.15f + 0.5f * order);
                float gloss = order * order * MathF.Pow(Math.Max(0, tilt * 2), 6) * 0.9f;
                float grain = 1 - (1 - order) * 0.25f * ((Hash((uint)(x * 31 + y * 1777)) & 255) / 255f);
                bool edge = !loose && order > 0.3f && (Math.Min(fx, fy) < 0.06f || MathF.Abs(fx - fy) < 0.03f && ((h >> 3) & 1) == 0 || MathF.Abs(fx + fy - 1) < 0.03f && ((h >> 3) & 1) == 1);
                bool pore = (Hash((uint)(x * 7919 + y * 104729)) & 1023) / 1023f < porosity * 0.8f;
                float k = dark * shade * grain * (edge ? 0.8f : 1f) * (pore ? 0.35f : 1f);
                img.SetPixel(x, y, new Color(Math.Min(1, c.R * k + gloss), Math.Min(1, c.G * k + gloss), Math.Min(1, c.B * k + gloss)));
            }
        return img;
    }

    static uint Hash(uint x) { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16; return x; }

    // ---- actions ----

    void NewRecipe()
    {
        KeepDraft();
        var w = Main.World;
        var mix = new double[Chemistry.S];
        mix[w.Chem.Solids.Length > 0 ? w.Chem.Solids[0] : 0] = 1;
        var r = MatterRecipe.FromMix(w, mix, 30, false, Loc.T("New substance", "Новое вещество"), "composed");
        session.Add(r);
        current = r; mapping = null; dirty = true;
        Fill();
        Refresh();
    }

    void KeepDraft()
    {
        if (dirty && current != null && current.Path == null && !current.Generated && !session.Contains(current)) session.Add(current);
    }

    void Duplicate()
    {
        if (current == null) return;
        KeepDraft();
        var r = current.Clone();
        r.Name = (r.Name ?? "") + Loc.T(" (copy)", " (копия)");
        r.Path = null;
        session.Add(r);
        current = r; mapping = null; dirty = true;
        Fill();
        Refresh();
    }

    void SaveRecipe()
    {
        if (current == null) return;
        if (string.IsNullOrWhiteSpace(current.Name)) { Ui.Toast(Loc.T("the substance has no name", "у вещества нет имени"), true); nameEdit.GrabFocus(); return; }
        if (current.Parts.Count == 0) { Ui.Toast(Loc.T("the recipe has no molecules", "в рецепте нет молекул"), true); return; }
        try
        {
            current.Signature ??= ChemSignature.Of(Main.World);
            string old = current.Path;
            current.Generated = false;
            string path = MatterLibrary.Save(current, UiManager.MaterialsDir);
            if (old != null && old != path && File.Exists(old)) File.Delete(old);   // renamed
            session.Remove(current);
            dirty = false;
            mapping = null;
            Refresh();
            SelectPath(path);
            UpdateTitle();
            Ui.Toast(Loc.T($"'{current.Name}' saved to the library ({Path.GetFileName(path)})", $"«{current.Name}» сохранено в библиотеку ({Path.GetFileName(path)})"));
        }
        catch (Exception e) { Ui.Toast(Loc.T("cannot save: ", "не сохранить: ") + e.Message, true); }
    }

    void SelectPath(string path)
    {
        for (int i = 0; i < listed.Count; i++)
            if (listed[i]?.Path == path) { library.Select(i); current = listed[i].Clone(); current.Path = path; Fill(); return; }
    }

    void DeleteRecipe()
    {
        if (current == null) return;
        if (session.Remove(current)) { Ui.Toast(Loc.T($"'{current.Name}' dropped", $"«{current.Name}» убрано")); }
        else if (current.Path != null && File.Exists(current.Path))
        {
            try { File.Delete(current.Path); Ui.Toast(Loc.T($"'{current.Name}' deleted from the library", $"«{current.Name}» удалено из библиотеки")); }
            catch (Exception e) { Ui.Toast(Loc.T("cannot delete: ", "не удалить: ") + e.Message, true); return; }
        }
        else { Ui.Toast(Loc.T("a computed substance cannot be deleted (it is made from this world's chemistry)", "посчитанное вещество не удалить (оно из химии этого мира)"), true); return; }
        current = null; dirty = false;
        Refresh(true);
        if (current == null && starters.Count > 0) Load(starters[0]);
    }

    void OpenFolder()
    {
        Directory.CreateDirectory(UiManager.MaterialsDir);
        OS.ShellOpen(UiManager.MaterialsDir);
        Refresh();
    }

    // Takes a sample between ticks (reading only) and shows it as a new draft; with toBrush brush 1 pours it.
    public void SampleAt(int cell, SampleKind what, bool toBrush)
    {
        if (cell < 0) { Ui.Toast(Loc.T("move the cursor over the map first (the sample is taken where it was)", "сначала наведите курсор на карту (образец берётся там, где он был)"), true); return; }
        Main.Sim.Do(w =>
        {
            var r = what == SampleKind.Loose ? MatterRecipe.SampleLoose(w, cell)
                  : what == SampleKind.Block ? MatterRecipe.SampleBlock(w, cell)
                  : MatterRecipe.SampleBlock(w, cell) ?? MatterRecipe.SampleLoose(w, cell);
            Ui.Post(() => Sampled(r, toBrush, Loc.T("nothing to take here", "здесь нечего взять")));
        });
    }

    void SampleSelected()
    {
        var a = Main.View.Selected;
        if (a == null || a.Dead) { Ui.Toast(Loc.T("first select a living creature on the map (click)", "сначала выберите живое существо на карте (клик)"), true); return; }
        Main.Sim.Do(w =>
        {
            var r = MatterRecipe.SampleBody(w, a);
            Ui.Post(() => Sampled(r, false, Loc.T("the creature has no molecules to take", "у существа нет молекул")));
        });
    }

    void Sampled(MatterRecipe r, bool toBrush, string none)
    {
        if (r == null) { Ui.Toast(none, true); return; }
        if (Main.World == null || !r.Signature.Matches(Main.World.Chem)) return;   // the world changed meanwhile
        KeepDraft();
        session.Add(r);
        current = r; mapping = null; dirty = true;
        Fill();
        Refresh();
        string what = MatterRecipe.MixText(Main.World.Chem, r.MixOf(Main.World.Chem), Loc.En, 3);
        Ui.Toast(Loc.T($"sampled {r.Name}: {what}, order {r.Order / 255.0:P0} — in the matter library (J), not saved yet", $"взято: {r.Name}: {what}, порядок {r.Order / 255.0:P0} — в библиотеке веществ (J), ещё не сохранено"));
        if (toBrush) UseForBrush();
    }

    void UseForBrush()
    {
        if (current == null || current.Parts.Count == 0) { Ui.Toast(Loc.T("the recipe has no molecules", "в рецепте нет молекул"), true); return; }
        var mix = current.Resolve(Main.World).Mix;
        Main.SetPourRecipe(current, mix);
        Ui.Toast(Loc.T($"brush 1 pours '{current.Name}' ({(current.Loose ? "loose" : "blocks")}, order {current.Order / 255.0:P0}) — Z: back to single species",
                       $"кисть 1 сыплет «{current.Name}» ({(current.Loose ? "рыхло" : "блоками")}, порядок {current.Order / 255.0:P0}) — Z: снова один вид"));
    }

    void UseForPoisoning()
    {
        if (current == null || current.Parts.Count == 0) return;
        KeepDraft();
        Refresh();
        Ui.Catastrophes.ChoosePoison(current);
        Ui.Catastrophes.Open();
    }

    // What the brush pours, for the HUD.
    public string BrushLabel => Main.PourRecipe == null ? "" :
        $"{(string.IsNullOrWhiteSpace(Main.PourRecipe.Name) ? Loc.T("untitled", "без имени") : Main.PourRecipe.Name)} ({(Main.PourRecipe.Loose ? Loc.T("loose", "рыхло") : Loc.T("blocks", "блоками"))}, {Loc.T("order", "порядок")} {Main.PourRecipe.Order / 255.0:P0})";

    // ---- the cursor ----

    public override void _Process(double delta)
    {
        if (!Visible) return;
        double now = Time.GetTicksMsec() / 1000.0;
        var w = Main.World;
        if (w != null && w != chemOf)
        {
            // Another world: other molecules. Starters are computed anew, the current one is mapped.
            var keep = current;
            current = null;
            Refresh();
            if (keep != null) Load(keep); else Refresh(true);
        }
        if (recalcAt >= 0 && now >= recalcAt) { recalcAt = -1; Recalc(); }
        var m = GetViewport().GetMousePosition();
        if (!Ui.IsOver(m))
        {
            int c = Main.View.BrushCell >= 0 ? Main.View.BrushCell : Main.View.PickCell(m);
            if (c >= 0) lastCell = c;
        }
        if (now >= cursorAt && lastCell >= 0)
        {
            cursorAt = now + 0.3;
            int cell = lastCell;
            Main.Sim.Do(sw =>
            {
                var r = MatterRecipe.SampleBlock(sw, cell);
                string text = r == null ? Loc.T($"({cell % w.W}, {cell / w.W}): no block on top", $"({cell % w.W}, {cell / w.W}): сверху нет блока")
                    : Loc.T($"({cell % w.W}, {cell / w.W}) top block: {MatterRecipe.MixText(sw.Chem, r.MixOf(sw.Chem), true, 3)}, order {r.Order / 255.0:P0}, {sw.Units[cell * w.Z + sw.Height[cell] - 1]} molecules, fill {sw.Fill(cell * w.Z + sw.Height[cell] - 1):P0}",
                            $"({cell % w.W}, {cell / w.W}) верхний блок: {MatterRecipe.MixText(sw.Chem, r.MixOf(sw.Chem), false, 3)}, порядок {r.Order / 255.0:P0}, {sw.Units[cell * w.Z + sw.Height[cell] - 1]} молекул, заполнен на {sw.Fill(cell * w.Z + sw.Height[cell] - 1):P0}");
                Ui.Post(() => cursorText = text);
            });
        }
        cursorNote.Text = lastCell < 0 ? Loc.T("Hover over the map: the block under the cursor is shown here.", "Наведите курсор на карту — здесь будет блок под ним.") : cursorText;
    }

    protected override void OnOpen() => Refresh(current == null);

    // --open matter:N selects the N-th substance of the list (for screenshots).
    public override void ShowView(string view)
    {
        // "pN": take it into brush 1 and close (with --paint: dabs at the screen centre, the map in view).
        bool pour = view.StartsWith('p');
        if (!int.TryParse(pour ? view[1..] : view, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return;
        var items = listed.Where(r => r != null).ToList();
        if (n >= 0 && n < items.Count) { Load(items[n]); Refresh(); if (pour) { UseForBrush(); Close(); } }
    }
}
