using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Primordium;

// U: the library of life. Creature templates (the designer's library, user://creatures) to plant with
// brush 5, and population templates (user://populations): living bodies copied out of the world — a
// lineage, a clade of the tree of life or everything in a brushed circle — with their genomes, matter,
// energy, looks, places and relations, saved as JSON files and pasted back with brush 5, here or in
// another world (another chemistry is mapped to the nearest species). Nothing is made from nothing:
// pasting takes matter and energy from the place or books what is brought from outside.
public partial class LifeLibraryWindow : UiWindow
{
    public enum Arm { None, Paste, CopyArea }
    public Arm Armed { get; private set; }

    TabContainer tabs;
    // creatures
    ItemList designs;
    List<CreatureLibrary.Entry> designEntries = new();
    Label designInfo;
    // populations
    ItemList pops;
    List<PopulationLibrary.Entry> popEntries = new();
    Label popInfo, chemInfo, armNote;
    LineEdit copyName, filePath, transferPath;
    OptionButton matter, energySrc;
    CheckBox relations, remap;
    PopulationTemplate selected;
    string selectedPath;
    int shownWorld = -1;

    public LifeLibraryWindow() : base("life", Loc.T("Library of life", "Библиотека жизни"), new Vector2(1000, 620))
    {
        MinSize = new Vector2(780, 460);
        tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        Body.AddChild(tabs);
        tabs.AddChild(CreaturesTab());
        tabs.AddChild(PopulationsTab());
        tabs.SetTabTitle(0, Loc.T("Creatures", "Существа"));
        tabs.SetTabTitle(1, Loc.T("Populations", "Популяции"));
        tabs.CurrentTab = 1;
    }

    public override void ShowView(string view) => tabs.CurrentTab = view == "creatures" ? 0 : 1;

    static Label Fixed(string s, float w)
    {
        var l = UiKit.Text(s, 13, UiKit.Dim);
        l.CustomMinimumSize = new Vector2(w, 0);
        return l;
    }

    // ---- creatures ----

    Control CreaturesTab()
    {
        var row = UiKit.Row(12);
        row.Name = "creatures";
        var left = UiKit.Col(6);
        left.CustomMinimumSize = new Vector2(230, 0);
        left.AddChild(UiKit.Title(Loc.T("Creature templates", "Шаблоны существ"), 13));
        designs = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None, TooltipText = UiManager.CreaturesDir };
        designs.ItemSelected += i => ShowDesign((int)i);
        left.AddChild(designs);
        left.AddChild(UiKit.Row(4,
            UiKit.Button(Loc.T("Refresh", "Обновить"), ListDesigns, Loc.T("read the folder again", "перечитать папку")),
            UiKit.Button(Loc.T("Folder", "Папка"), () => OS.ShellOpen(UiManager.CreaturesDir), UiManager.CreaturesDir)));
        row.AddChild(left);
        var right = UiKit.Col(8);
        right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        designInfo = UiKit.Text("", 12, UiKit.Fg, null, true);
        right.AddChild(designInfo);
        var plant = UiKit.Button(Loc.T("Plant with brush (5)", "Посадить кистью (5)"), PlantDesign, Loc.T("brush 5: a click on the map plants it (count, matter and energy sources: the designer's settings, F7)", "кисть 5: клик по карте сажает его (сколько, откуда вещество и энергия — настройки конструктора, F7)"));
        plant.AddThemeColorOverride("font_color", UiKit.Acc);
        right.AddChild(UiKit.Row(8, plant,
            UiKit.Button(Loc.T("Open in designer (F7)", "Открыть в конструкторе (F7)"), () => { if (PickedDesign() is { } e) { Ui.Creator.LoadDesign(e.Design.Clone(), e.Path); Ui.Creator.Open(); } })));
        right.AddChild(UiKit.Text(Loc.T("A creature template is a genome and the molecules and energy its body should get; planted, it is made of this world's matter (from the place or brought from outside and booked), like in the designer.",
            "Шаблон существа — геном и то, из каких молекул и с какой энергией собрать тело; при посадке оно делается из вещества этого мира (местного или принесённого извне и учтённого), как в конструкторе."), 12, UiKit.Dim, null, true));
        row.AddChild(right);
        return row;
    }

    void ListDesigns()
    {
        designEntries = CreatureLibrary.List(UiManager.CreaturesDir);
        designs.Clear();
        foreach (var e in designEntries)
        {
            int i = designs.AddItem(e.Design != null ? CreatureExamples.DisplayName(e.Design.Name) : Path.GetFileName(e.Path) + Loc.T(" (error)", " (ошибка)"));
            designs.SetItemTooltip(i, e.Design != null ? CreatureExamples.DisplayDescription(e.Design) : e.Error);
        }
        if (designEntries.Count > 0) { designs.Select(0); ShowDesign(0); }
        else designInfo.Text = Loc.T($"No designs in {UiManager.CreaturesDir}.", $"В {UiManager.CreaturesDir} нет дизайнов.");
    }

    CreatureLibrary.Entry PickedDesign()
    {
        var sel = designs.GetSelectedItems();
        if (sel.Length == 0 || sel[0] >= designEntries.Count || designEntries[sel[0]].Design == null) { Ui.Toast(Loc.T("pick a creature template", "выберите шаблон существа"), true); return null; }
        return designEntries[sel[0]];
    }

    void ShowDesign(int i)
    {
        if (i < 0 || i >= designEntries.Count) return;
        var e = designEntries[i];
        if (e.Design == null) { designInfo.Text = Loc.T($"cannot read {e.Path}: {e.Error}", $"не прочитать {e.Path}: {e.Error}"); return; }
        var d = e.Design;
        int bytes = GenomeAsm.TryAssemble(d.Genome, out var g, out _) ? g.Length : 0;
        var w = Main.World;
        var errors = w != null ? d.Check(w.Chem) : new List<string>();
        string body = string.Join(", ", (d.Body ?? new()).Select(kv => $"{kv.Key} ×{kv.Value}"));
        designInfo.Text = Loc.T($"{CreatureExamples.DisplayName(d.Name)}\n{CreatureExamples.DisplayDescription(d)}\n\nbody: {body} · energy {d.Energy:0.#} · genome {bytes} bytes\nfile: {e.Path}\n",
                                $"{CreatureExamples.DisplayName(d.Name)}\n{CreatureExamples.DisplayDescription(d)}\n\nтело: {body} · энергия {d.Energy:0.#} · геном {bytes} байт\nфайл: {e.Path}\n") +
                          (errors.Count == 0 ? Loc.T("can be planted in this world", "можно посадить в этом мире") : Loc.T("cannot be planted here: ", "здесь не посадить: ") + string.Join("; ", errors.Take(3)));
    }

    void PlantDesign()
    {
        if (PickedDesign() is not { } e) return;
        Ui.Creator.LoadDesign(e.Design.Clone(), e.Path);
        Armed = Arm.None;
        if (Main.Tool != 5) Main.SetTool(5);
        Ui.Toast(Loc.T($"brush 5 plants '{CreatureExamples.DisplayName(e.Design.Name)}'", $"кисть 5 сажает «{CreatureExamples.DisplayName(e.Design.Name)}»"));
    }

    // ---- populations ----

    Control PopulationsTab()
    {
        var row = UiKit.Row(12);
        row.Name = "populations";
        var left = UiKit.Col(6);
        left.CustomMinimumSize = new Vector2(250, 0);
        left.AddChild(UiKit.Title(Loc.T("Population templates", "Шаблоны популяций"), 13));
        pops = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None, TooltipText = UiManager.PopulationsDir };
        pops.ItemSelected += i => ShowPopulation((int)i);
        left.AddChild(pops);
        left.AddChild(UiKit.Row(4,
            UiKit.Button(Loc.T("Refresh", "Обновить"), () => ListPopulations(selectedPath), Loc.T("read the folder again (files copied in from elsewhere appear)", "перечитать папку (появятся скопированные туда файлы)")),
            UiKit.Button(Loc.T("Folder", "Папка"), () => { Directory.CreateDirectory(UiManager.PopulationsDir); OS.ShellOpen(UiManager.PopulationsDir); }, UiManager.PopulationsDir),
            new ConfirmButton(Loc.T("Delete", "Удалить"), DeletePopulation, Loc.T("delete the file", "удалить файл"))));
        row.AddChild(left);

        var col = UiKit.Col(6);
        // What the picked template is, and how it fits this world's chemistry.
        popInfo = UiKit.Text("", 12, UiKit.Fg, null, true);
        col.AddChild(popInfo);
        chemInfo = UiKit.Text("", 12, UiKit.Dim, null, true);
        col.AddChild(chemInfo);
        filePath = new LineEdit { Editable = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = Loc.T("the file (select and copy the path; copy the file to another machine's populations folder)", "файл (путь можно выделить и скопировать; файл — перенести в папку популяций на другой машине)") };
        col.AddChild(UiKit.Row(8, Fixed(Loc.T("File", "Файл"), 70), filePath));
        col.AddChild(new HSeparator());

        // Paste.
        col.AddChild(UiKit.Title(Loc.T("Paste", "Вставка"), 13));
        matter = UiKit.Options(Loc.T("from local matter", "из местного вещества"), Loc.T("bring from outside", "принести извне"));
        energySrc = UiKit.Options(Loc.T("from local reactions", "из местных реакций"), Loc.T("bring from outside", "принести извне"));
        matter.ItemSelected += _ => { Ui.State.PasteMatter = matter.Selected; Ui.SaveSoon(); };
        energySrc.ItemSelected += _ => { Ui.State.PasteEnergy = energySrc.Selected; Ui.SaveSoon(); };
        col.AddChild(UiKit.Row(8, Fixed(Loc.T("Matter", "Вещество"), 70), matter, Fixed(Loc.T("Energy", "Энергия"), 60), energySrc));
        relations = UiKit.Check(Loc.T("Keep lineages and parents", "Сохранить линии и родство"), true, on => { Ui.State.PasteRelations = on; Ui.SaveSoon(); },
            Loc.T("bodies of one source lineage share a new lineage, pasted children keep their pasted parents (off: one new lineage for all)", "тела одной исходной линии получают общую новую линию, вставленные дети — своих вставленных родителей (выкл.: одна новая линия на всех)"));
        remap = UiKit.Check(Loc.T("Retarget protein genes", "Перенацелить гены белков"), true, on => { Ui.State.PasteRemap = on; Ui.SaveSoon(); ShowChem(); },
            Loc.T("in another chemistry: protein genes (enzyme A B) point at the mapped species; numbers the code puts on the stack stay as they are", "в другой химии: гены белков (enzyme A B) указывают на заменённые виды; номера, которые код кладёт на стек, остаются как есть"));
        col.AddChild(UiKit.Row(12, relations, remap));
        var paste = UiKit.Button(Loc.T("Paste with brush (5)", "Вставить кистью (5)"), () => ArmBrush(Arm.Paste), Loc.T("brush 5: a click puts the template's centre there; every body goes to its own place around it", "кисть 5: клик ставит туда центр шаблона, каждое тело — на своё место вокруг"));
        paste.AddThemeColorOverride("font_color", UiKit.Acc);
        col.AddChild(UiKit.Row(8, paste));
        col.AddChild(new HSeparator());

        // Copy.
        col.AddChild(UiKit.Title(Loc.T("Copy from this world", "Скопировать из этого мира"), 13));
        copyName = UiKit.Edit("", Loc.T("name of the template", "имя шаблона"));
        copyName.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        col.AddChild(UiKit.Row(8, Fixed(Loc.T("Name", "Имя"), 70), copyName));
        col.AddChild(UiKit.Row(6,
            UiKit.Button(Loc.T("Selected lineage", "Линию выбранного"), CopyLineage, Loc.T("every living body of the selected creature's lineage (click it on the map)", "все живые тела линии выбранного существа (клик по нему на карте)")),
            UiKit.Button(Loc.T("Picked clade (F1)", "Ветвь из древа (F1)"), CopyClade, Loc.T("every living body of the branch picked in the tree of life (F1, cladogram)", "все живые тела ветви, выбранной в древе жизни (F1, кладограмма)")),
            UiKit.Button(Loc.T("Area with brush (5)", "Область кистью (5)"), () => ArmBrush(Arm.CopyArea), Loc.T("brush 5: a click copies every body in the circle; [ ] sets its radius", "кисть 5: клик копирует все тела в круге; [ ] — радиус"))));
        armNote = UiKit.Text("", 12, UiKit.Acc, null, true);
        col.AddChild(armNote);
        col.AddChild(new HSeparator());

        // Files from elsewhere.
        col.AddChild(UiKit.Title(Loc.T("Import / export", "Импорт / экспорт"), 13));
        transferPath = UiKit.Edit("", Loc.T("path to a .json file (or a folder to export into)", "путь к файлу .json (или папка для экспорта)"));
        transferPath.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        col.AddChild(UiKit.Row(6, transferPath,
            UiKit.Button(Loc.T("Import", "Импорт"), Import, Loc.T("copy that file into the library", "скопировать этот файл в библиотеку")),
            UiKit.Button(Loc.T("Export", "Экспорт"), Export, Loc.T("copy the picked template there", "скопировать туда выбранный шаблон"))));
        col.AddChild(UiKit.Text(Loc.T($"Templates are JSON files in {UiManager.PopulationsDir}: copy them between machines, then Refresh.",
                                      $"Шаблоны — файлы JSON в {UiManager.PopulationsDir}: их можно переносить между машинами, затем «Обновить»."), 11, UiKit.Dim, null, true));
        var scroll = UiKit.Scroll(col);
        row.AddChild(scroll);
        return row;
    }

    public override void _Ready()
    {
        Directory.CreateDirectory(UiManager.PopulationsDir);
        matter.Selected = Math.Clamp(Ui.State.PasteMatter, 0, 1);
        energySrc.Selected = Math.Clamp(Ui.State.PasteEnergy, 0, 1);
        relations.SetPressedNoSignal(Ui.State.PasteRelations);
        remap.SetPressedNoSignal(Ui.State.PasteRemap);
        ListDesigns();
        ListPopulations(null);
    }

    protected override void OnOpen()
    {
        ListDesigns();
        ListPopulations(selectedPath);
    }

    void ListPopulations(string keep)
    {
        popEntries = PopulationLibrary.List(UiManager.PopulationsDir);
        pops.Clear();
        int pick = -1;
        foreach (var e in popEntries)
        {
            string label = e.Template != null ? $"{e.Template.Name}  ·  {e.Template.Bodies.Count}" : Path.GetFileName(e.Path) + Loc.T(" (error)", " (ошибка)");
            int i = pops.AddItem(label);
            pops.SetItemTooltip(i, e.Template != null ? e.Template.Description : e.Error);
            if (e.Path == keep) pick = i;
        }
        if (pick < 0 && popEntries.Count > 0) pick = 0;
        if (pick >= 0) { pops.Select(pick); ShowPopulation(pick); }
        else
        {
            selected = null; selectedPath = null; filePath.Text = "";
            popInfo.Text = Loc.T("No population templates yet. Copy a lineage, a clade or an area of this world below, or put .json files into the folder.",
                                 "Шаблонов популяций пока нет. Скопируйте ниже линию, ветвь или область этого мира — или положите файлы .json в папку.");
            chemInfo.Text = "";
        }
    }

    void ShowPopulation(int i)
    {
        if (i < 0 || i >= popEntries.Count) return;
        var e = popEntries[i];
        selectedPath = e.Path;
        filePath.Text = e.Path;
        selected = e.Template;
        if (selected == null) { popInfo.Text = Loc.T($"cannot read: {e.Error}", $"не прочитать: {e.Error}"); chemInfo.Text = ""; return; }
        var t = selected;
        var problems = t.Check();
        string energy = t.Energy.ToString("0");
        popInfo.Text = Loc.T($"{t.Name} — {t.Description}\n{t.Bodies.Count} {(t.Bodies.Count == 1 ? "body" : "bodies")}, {t.Lineages} {(t.Lineages == 1 ? "lineage" : "lineages")} · {t.Molecules} molecules · energy {energy} · spread {t.Width}×{t.Height} cells · from world {t.Seed}, tick {t.Tick} ({t.Source})",
                             $"{t.Name} — {t.Description}\n{t.Bodies.Count} тел, линий {t.Lineages} · молекул {t.Molecules} · энергия {energy} · размах {t.Width}×{t.Height} клеток · из мира {t.Seed}, тик {t.Tick} ({t.Source})") +
                       (problems.Count > 0 ? "\n" + Loc.T("cannot be pasted: ", "нельзя вставить: ") + string.Join("; ", problems.Take(3)) : "");
        ShowChem();
    }

    // The template's chemistry against this world's: the same, or which species go where.
    void ShowChem()
    {
        var w = Main.World;
        if (selected == null || w == null) { chemInfo.Text = ""; return; }
        shownWorld = w.Seed;
        ChemMap map;
        try { map = ChemMap.Build(selected.Chemistry, w.Chem, w.Seed); }
        catch (Exception x) { chemInfo.Text = x.Message; return; }
        if (map.Same) { chemInfo.Text = Loc.T("Chemistry: the same as this world's — bodies are pasted exactly as copied.", "Химия: та же, что в этом мире — тела вставятся в точности как скопированы."); chemInfo.AddThemeColorOverride("font_color", UiKit.Dim); return; }
        var used = new HashSet<int>();
        foreach (var b in selected.Bodies)
        {
            foreach (var k in b.Body.Keys) used.Add(PopulationBody.Species(k));
            foreach (var k in b.Pend.Keys) used.Add(PopulationBody.Species(k));
            foreach (var p in b.Proteins) { used.Add(p.Material); used.Add(p.A); used.Add(p.B); }
        }
        var lines = map.Changes.Where(c => used.Contains(c.from)).Select(c => Loc.T(c.en, c.ru)).ToList();
        int genes = 0;
        if (remap.ButtonPressed) foreach (var b in selected.Bodies) { try { map.RemapGenes(b.GenomeBytes(), out int n); genes += n; } catch (FormatException) { } }
        chemInfo.Text = Loc.T($"Another chemistry (world {selected.Chemistry.Seed}, this one {w.Seed}): the body molecules are mapped to the nearest species here — another formula means other atoms, other bond energy; the bodies are made of this world's matter. Genomes are kept byte for byte (the machine is the same), but they name molecules by species number: ",
                              $"Другая химия (мир {selected.Chemistry.Seed}, этот — {w.Seed}): молекулы тел заменяются ближайшими видами здесь — другая формула значит другие атомы и энергию связей; тела делаются из вещества этого мира. Геномы сохраняются байт в байт (машина та же), но молекулы в них названы номерами видов: ") +
                         (remap.ButtonPressed ? Loc.T($"{genes} protein genes will be retargeted (their quality changes with the bytes); numbers put on the stack by code are not.", $"генов белков будет перенацелено: {genes} (их качество меняется вместе с байтами); номера, которые код кладёт на стек, — нет.")
                                              : Loc.T("protein genes keep their numbers and may now point at other molecules.", "гены белков сохраняют номера и могут указывать уже на другие молекулы.")) +
                         "\n" + string.Join("\n", lines.Take(10)) + (lines.Count > 10 ? Loc.T($"\n… and {lines.Count - 10} more", $"\n… и ещё {lines.Count - 10}") : "");
        chemInfo.AddThemeColorOverride("font_color", UiKit.Acc);
    }

    void DeletePopulation()
    {
        if (selectedPath == null || !File.Exists(selectedPath)) { Ui.Toast(Loc.T("pick a template", "выберите шаблон"), true); return; }
        try { File.Delete(selectedPath); Ui.Toast(Loc.T($"deleted {Path.GetFileName(selectedPath)}", $"удалён {Path.GetFileName(selectedPath)}")); }
        catch (Exception x) { Ui.Toast(Loc.T("cannot delete: ", "не удалить: ") + x.Message, true); }
        ListPopulations(null);
    }

    string NameOr(string fallback) => string.IsNullOrWhiteSpace(copyName.Text) ? fallback : copyName.Text.Trim();

    void CopyLineage()
    {
        var a = Main.View.Selected;
        if (a == null) { Ui.Toast(Loc.T("select a creature on the map first", "сначала выберите существо на карте"), true); return; }
        long lineage = a.Lineage;
        Copy(w => w.LineageBodies(lineage), NameOr(Loc.T($"lineage {lineage}", $"линия {lineage}")), "lineage");
    }

    void CopyClade()
    {
        ulong hash = Ui.Tree.PickHash;
        long origin = Ui.Tree.PickOrigin;
        if (hash == 0) { Ui.Toast(Loc.T("pick a branch in the tree of life first (F1, cladogram)", "сначала выберите ветвь в древе жизни (F1, кладограмма)"), true); return; }
        Copy(w => w.CladeBodies(hash, origin), NameOr(Loc.T($"clade {hash & 0xFFFF:x4}", $"ветвь {hash & 0xFFFF:x4}")), "clade");
    }

    void Copy(Func<World, List<Agent>> select, string name, string source)
    {
        Main.Sim.CopyPopulation(select, name, source, t => Ui.Post(() => Saved(t)));
    }

    void Saved(PopulationTemplate t)
    {
        if (t.Bodies.Count == 0) { Ui.Toast(Loc.T("no living bodies there: nothing copied", "живых тел там нет — ничего не скопировано"), true); return; }
        try
        {
            string file = CreatureLibrary.FileName(t.Name);
            string dir = UiManager.PopulationsDir;
            for (int k = 2; File.Exists(Path.Combine(dir, file)); k++) file = CreatureLibrary.FileName($"{t.Name} {k}");
            string path = PopulationLibrary.Save(t, dir, file);
            Ui.Toast(Loc.T($"population '{t.Name}' copied: {t.Bodies.Count} bodies, {t.Lineages} lineages → {path}", $"популяция «{t.Name}» скопирована: {t.Bodies.Count} тел, линий {t.Lineages} → {path}"));
            ListPopulations(path);
        }
        catch (Exception x) { Ui.Toast(Loc.T("cannot save: ", "не сохранить: ") + x.Message, true); }
    }

    void Import()
    {
        string from = transferPath.Text.Trim();
        if (from.Length == 0 || !File.Exists(from)) { Ui.Toast(Loc.T("no such file", "нет такого файла"), true); return; }
        try
        {
            var t = PopulationLibrary.Load(from);
            var problems = t.Check();
            if (problems.Count > 0) { Ui.Toast(Loc.T("not a usable population: ", "негодная популяция: ") + string.Join("; ", problems.Take(2)), true); return; }
            string to = Path.Combine(UiManager.PopulationsDir, Path.GetFileName(from));
            File.Copy(from, to, true);
            Ui.Toast(Loc.T($"imported → {to}", $"импортировано → {to}"));
            ListPopulations(to);
        }
        catch (Exception x) { Ui.Toast(Loc.T("cannot import: ", "не импортировать: ") + x.Message, true); }
    }

    void Export()
    {
        if (selectedPath == null) { Ui.Toast(Loc.T("pick a template", "выберите шаблон"), true); return; }
        string to = transferPath.Text.Trim();
        if (to.Length == 0) { Ui.Toast(Loc.T("type where to export (a folder or a file path)", "укажите, куда экспортировать (папку или путь к файлу)"), true); return; }
        try
        {
            if (Directory.Exists(to)) to = Path.Combine(to, Path.GetFileName(selectedPath));
            File.Copy(selectedPath, to, true);
            Ui.Toast(Loc.T($"exported → {to}", $"экспортировано → {to}"));
        }
        catch (Exception x) { Ui.Toast(Loc.T("cannot export: ", "не экспортировать: ") + x.Message, true); }
    }

    // ---- brush 5 ----

    void ArmBrush(Arm arm)
    {
        if (arm == Arm.Paste && (selected == null || selected.Check().Count > 0)) { Ui.Toast(Loc.T("pick a template that can be pasted", "выберите шаблон, который можно вставить"), true); return; }
        Armed = arm;
        if (Main.Tool != 5) Main.SetTool(5);
    }

    public void Disarm() => Armed = Arm.None;

    // A click with brush 5 while the library holds it.
    public void BrushClick(int cell)
    {
        int x = cell % Main.World.W, y = cell / Main.World.W;
        if (Armed == Arm.CopyArea)
        {
            float r = Main.BrushR;
            Copy(w => w.AreaBodies(x, y, r), NameOr(Loc.T($"area {x},{y}", $"область {x},{y}")), "area");
            return;
        }
        if (Armed != Arm.Paste || selected == null) return;
        var options = new PasteOptions
        {
            Matter = matter.Selected == 0 ? MatterSource.Local : MatterSource.Import,
            Energy = energySrc.Selected == 0 ? EnergySource.Local : EnergySource.Import,
            KeepRelations = relations.ButtonPressed,
            RemapGenes = remap.ButtonPressed,
        };
        Main.Sim.PastePopulation(selected.Clone(), x, y, options);
    }

    public string BrushText => Armed == Arm.CopyArea
        ? Loc.T($"copy every body in the circle (radius {Main.BrushR:0}) into a population template", $"скопировать все тела в круге (радиус {Main.BrushR:0}) в шаблон популяции")
        : Loc.T($"paste population '{selected?.Name}' ({selected?.Bodies.Count} bodies, {selected?.Width}×{selected?.Height} cells) · matter {(matter.Selected == 0 ? "local" : "from outside")}, energy {(energySrc.Selected == 0 ? "local" : "from outside")}",
                $"вставить популяцию «{selected?.Name}» ({selected?.Bodies.Count} тел, {selected?.Width}×{selected?.Height} клеток) · вещество {(matter.Selected == 0 ? "местное" : "извне")}, энергия {(energySrc.Selected == 0 ? "местная" : "извне")}");

    public string BrushHint => Armed == Arm.CopyArea
        ? Loc.T($"click — copy the bodies in this circle · radius {Main.BrushR:0} ([ ]) · U — library of life · 5, 0 or Esc — put away", $"клик — скопировать тела в этом круге · радиус {Main.BrushR:0} ([ ]) · U — библиотека жизни · 5, 0 или Esc — убрать")
        : Loc.T("click — the template's centre goes here, every body to its place around it · U — library of life · 5, 0 or Esc — put away", "клик — сюда встанет центр шаблона, каждое тело — на своё место вокруг · U — библиотека жизни · 5, 0 или Esc — убрать");

    public override void _Process(double delta)
    {
        if (Armed != Arm.None && Main.Tool != 5) Armed = Arm.None;
        armNote.Text = Armed == Arm.CopyArea ? Loc.T("Brush 5 is copying: click the map.", "Кисть 5 копирует: щёлкните по карте.")
                     : Armed == Arm.Paste ? Loc.T("Brush 5 is pasting: click the map.", "Кисть 5 вставляет: щёлкните по карте.") : "";
        if (Visible && Main.World is { } w && w.Seed != shownWorld) ShowChem();
    }
}
