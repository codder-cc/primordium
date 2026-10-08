using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace Primordium;

// Settings of the windows kept between runs (user://ui.json).
public sealed class UiState
{
    public sealed class Win { public float X { get; set; } public float Y { get; set; } public float W { get; set; } public float H { get; set; } public bool Open { get; set; } }
    public Dictionary<string, Win> Windows { get; set; } = new();
    public float AutosaveMinutes { get; set; } = 10;
    public int AutosaveSlots { get; set; } = 3;
    public bool ExamplesExported { get; set; }
    public string Design { get; set; }              // the creature last open in the editor (library path)
    public int SpawnMatter { get; set; }            // 0 local, 1 import
    public int SpawnEnergy { get; set; }
    public int SpawnCount { get; set; } = 1;
    public int PasteMatter { get; set; }            // library of life: population paste, 0 local, 1 import
    public int PasteEnergy { get; set; }
    public bool PasteRelations { get; set; } = true;
    public bool PasteRemap { get; set; } = true;
    public string Language { get; set; } = "en";   // en | ru
    public bool SidebarExpanded { get; set; }       // the left bar shows names and hotkeys
}

// The game's windows over the world: their stacking, Esc, toasts with what the simulation did, a small
// sidebar on the left, and remembering it all in user://ui.json. Lives on the HUD's canvas layer above the panel.
public partial class UiManager : Control
{
    public Main Main;
    public bool RestoreWindows = true;   // reopen the windows that were open last time
    public string LanguageOverride;      // --lang: this run only, ui.json keeps its choice
    public UiState State = new();
    public readonly List<UiWindow> Windows = new();
    public LawsWindow Laws;
    public NewWorldWindow NewWorld;
    public SavesWindow Saves;
    public CreatorWindow Creator;
    public ChronicleWindow Chronicle;
    public FossilWindow Fossil;
    public EvolutionWindow Evolution;
    public CatastropheWindow Catastrophes;
    public TreeWindow Tree;
    public MetricsWindow Metrics;
    public LifeLibraryWindow Life;
    public RegionsWindow Regions;

    readonly ConcurrentQueue<Action> posted = new();
    VBoxContainer toastBox;
    public Sidebar Sidebar;
    bool? sidebarOverride;               // --sidebar expanded|collapsed: this run only
    readonly List<(Control c, double until)> toasts = new();
    double saveAt = -1, now;
    bool loaded;

    public static string UiPath => UiKit.Global("user://ui.json");
    public static string SavesDir => UiKit.Global("user://saves");
    public static string PresetsDir => UiKit.Global("user://presets");
    public static string CreaturesDir => UiKit.Global("user://creatures");
    public static string PopulationsDir => UiKit.Global("user://populations");

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        Theme = UiKit.Theme;
        LoadState();
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--sidebar") sidebarOverride = args[i + 1] == "expanded";
        Loc.Set(LanguageOverride ?? State.Language);
        Build();
        loaded = true;
        if (RestoreWindows) CallDeferred(nameof(RestoreOpen));
    }

    // Switches the interface language: the windows are built again in place, open ones stay open.
    public void SetLanguage(string code)
    {
        if (code == Loc.Code) return;
        SaveState();
        Loc.Set(code);
        State.Language = Loc.Code;
        foreach (var c in GetChildren()) { RemoveChild(c); c.QueueFree(); }
        Windows.Clear();
        toasts.Clear();
        Build();
        RestoreOpen();
        SaveState();
    }

    void Build()
    {
        Sidebar = new Sidebar { Ui = this, Expanded = sidebarOverride ?? State.SidebarExpanded };
        Sidebar.Groups["world"] = Loc.T("World", "Мир");
        Sidebar.Groups["life"] = Loc.T("Life", "Жизнь");
        Sidebar.Groups["tools"] = Loc.T("Tools", "Инструменты");
        Sidebar.Items.AddRange(SideItems());
        AddChild(Sidebar);

        Add(Laws = new LawsWindow());
        Add(NewWorld = new NewWorldWindow());
        Add(Saves = new SavesWindow());
        Add(Creator = new CreatorWindow());
        Add(Chronicle = new ChronicleWindow());
        Add(Fossil = new FossilWindow());
        Add(Evolution = new EvolutionWindow());
        Add(Catastrophes = new CatastropheWindow());
        Add(Tree = new TreeWindow());
        Add(Metrics = new MetricsWindow());
        Add(Life = new LifeLibraryWindow());
        Add(Regions = new RegionsWindow());

        toastBox = UiKit.Col(4);
        toastBox.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(toastBox);

        foreach (var w in Windows)
            if (State.Windows.TryGetValue(w.Id, out var s))
            {
                if (s.W > 0) w.Size = new Vector2(Math.Max(w.MinSize.X, s.W), Math.Max(w.MinSize.Y, s.H));
                w.Position = new Vector2(s.X, s.Y);
            }
            else Center(w);
    }

    // The sidebar: one list, top to bottom. A window's entry shows only once the window exists (ById),
    // so a new window needs its Add(...) above and one Win(...) line here.
    IEnumerable<SideItem> SideItems()
    {
        var m = Main;
        // world
        yield return Win("world", "laws", Icons.Laws, Loc.T("Laws", "Законы"), "F2", Loc.T("world laws: costs, strength, climate…", "законы мира: цены, прочность, климат…"));
        yield return Win("world", "newworld", Icons.NewWorld, Loc.T("New world", "Новый мир"), "F4", Loc.T("seed, population, abiogenesis, strikes, law preset", "seed, население, самозарождение, удары, набор законов"));
        yield return Win("world", "saves", Icons.Saves, Loc.T("Saves", "Сохранения"), "F6", Loc.T("slots, loading, autosave (F5 — quick save, F9 — load)", "слоты, загрузка, автосохранение (F5 — быстро сохранить, F9 — загрузить)"));
        yield return Win("world", "catastrophes", Icons.Catastrophes, Loc.T("Catastrophes", "Катастрофы"), "F11", Loc.T("ice age, flood, volcanic winter, solar flare, drought, poisoning — by the same laws as the rest of the world", "ледниковье, потоп, вулканическая зима, вспышка, засуха, отравление — теми же законами, что и в мире"));
        yield return Win("world", "regions", Icons.Regions, Loc.T("Regions", "Регионы"), "Y", Loc.T("parts of the map with their own conditions", "части карты со своими условиями"));
        // life
        yield return Win("life", "creator", Icons.Designer, Loc.T("Designer", "Конструктор"), "F7", Loc.T("your own creatures: genome, body, planting with brush 5", "свои существа: геном, тело, посадка кистью 5"));
        yield return Win("life", "tree", Icons.Tree, Loc.T("Tree of life", "Древо жизни"), "F1", Loc.T("lineages over time and the cladogram of living clades; a click flies to a member or opens a fossil", "линии во времени и кладограмма живых ветвей; клик — к представителю или его окаменелости"));
        yield return Win("life", "chronicle", Icons.Chronicle, Loc.T("Chronicle", "Хроника"), "F8", Loc.T("world events, fossils; the selected body's biography is a tab in its card", "события мира, окаменелости; биография выбранного — вкладка в карточке существа"));
        yield return Win("life", "evolution", Icons.Evolution, Loc.T("Evolution", "Ход эволюции"), "F10", Loc.T("novelty vs. the neutral shadow, complexity, ecology, tempo, phylogeny; a summary hint", "новизна против нейтральной тени, сложность, экология, темп, филогения; сводная подсказка"));
        yield return Win("life", "metrics", Icons.Metrics, Loc.T("Metrics", "Метрики"), "F12", Loc.T("evolution metrics over time: pick series, hover for values, export CSV", "метрики эволюции во времени: выбор рядов, значения под курсором, экспорт CSV"));
        yield return Win("life", "matter", Icons.Matter, Loc.T("Matter library", "Библиотека веществ"), "J", Loc.T("the world's materials and molecules", "материалы и молекулы мира"));
        yield return Win("life", "life", Icons.LifeLib, Loc.T("Life library", "Библиотека жизни"), "U", Loc.T("creature templates and populations: copy a lineage, a clade or an area, save, paste here or in another world", "шаблоны существ и популяций: скопировать линию, ветвь или область, сохранить, вставить здесь или в другом мире"));
        // tools: the hand's brushes (the same digit again puts it away), then the view
        yield return Brush(1, Icons.Pour, Loc.T("Pour matter", "Насыпать"), Loc.T("brush: pour a heap of matter (Z — keep the material, I — take the one under the cursor)", "кисть: насыпать кучу вещества (Z — закрепить материал, I — взять тот, что под курсором)"));
        yield return Brush(2, Icons.Water, Loc.T("Water", "Вода"), Loc.T("brush: flood with water; it then flows, evaporates and rains", "кисть: залить водой; дальше она течёт, испаряется и выпадает дождём"));
        yield return Brush(3, Icons.Kill, Loc.T("Kill", "Убить"), Loc.T("brush: kill everyone in the circle (remains stay)", "кисть: убить всех в круге (останки остаются)"));
        yield return Brush(4, Icons.Dig, Loc.T("Dig", "Копать"), Loc.T("brush: remove the top blocks", "кисть: снять верхние блоки"));
        yield return Brush(5, Icons.Plant, Loc.T("Plant a design", "Посадить"), Loc.T("brush: plant the designer's creature in the cell under the cursor", "кисть: посадить существо из конструктора в клетку под курсором"));
        yield return Tool("slice", Icons.Slice, Loc.T("Cross-section", "Разрез"), "C", Loc.T("cut the world open along a row; [ ] move the cut", "разрезать мир по строке; [ ] сдвигают разрез"),
            () => { var v = m.View; v.Slice = v.Slice < 0 ? World.H / 2 : -1; }, () => m.View.Slice >= 0);
        yield return Tool("overlay", Icons.Overlay, Loc.T("Surface layer", "Слой поверхности"), "M", Loc.T("what the surface shows: rock, temperature, light, deaths, clades… (⇧M — back)", "что показывает поверхность: породы, температура, свет, смерти, ветви… (⇧M — назад)"),
            () => { var v = m.View; v.Overlay = (v.Overlay + 1) % v.OverlayCount; }, () => m.View.Overlay != 0);
        yield return Tool("light", Icons.Light, Loc.T("Sunlight", "Освещение"), "L", Loc.T("light and shade of the sun on the map, or flat colours", "свет и тени солнца на карте или ровные цвета"),
            () => m.View.Lighting = !m.View.Lighting, () => m.View.Lighting);
        yield return Tool("perf", Icons.Perf, Loc.T("Performance", "Замер"), "F3", Loc.T("frame, simulation and drawing times over the map", "время кадра, симуляции и отрисовки поверх карты"),
            () => m.TogglePerf(), () => m.PerfShown);
        // at the bottom (group ""): the language
        yield return new SideItem
        {
            Id = "lang", Group = "", Caption = Loc.T("Русский", "English"), Badge = () => Loc.En ? "RU" : "EN",
            Tip = Loc.T("switch the interface to Russian", "переключить интерфейс на английский"),
            Act = () => CallDeferred(nameof(SwitchLanguage)),
        };
    }

    SideItem Win(string group, string id, Icons.Fn icon, string caption, string key, string tip) => new()
    {
        Id = id, Group = group, Icon = icon, Caption = caption, Key = key, Tip = tip,
        Act = () => ById(id)?.Toggle(), On = () => ById(id)?.Visible == true, Shown = () => ById(id) != null,
    };

    SideItem Brush(int n, Icons.Fn icon, string caption, string tip) => new()
    {
        Id = "brush" + n, Group = "tools", Icon = icon, Caption = caption, Key = n.ToString(), Tip = tip,
        Act = () => Main.SetTool(n), On = () => Main.Tool == n,
    };

    static SideItem Tool(string id, Icons.Fn icon, string caption, string key, string tip, Action act, Func<bool> on) => new()
    {
        Id = id, Group = "tools", Icon = icon, Caption = caption, Key = key, Tip = tip, Act = act, On = on,
    };

    void SwitchLanguage() => SetLanguage(Loc.En ? "ru" : "en");

    // How much of the screen's left edge the bar takes (the HUD, the toasts and the windows keep out of it).
    public float LeftInset => Visible && Sidebar != null && Sidebar.Visible ? Sidebar.Width : 0;

    public void SidebarChanged(bool expanded)
    {
        sidebarOverride = null;
        State.SidebarExpanded = expanded;
        foreach (var w in Windows) if (w.Visible) w.KeepInside();
        SaveSoon();
    }

    void RestoreOpen()
    {
        foreach (var w in Windows)
            if (State.Windows.TryGetValue(w.Id, out var s) && s.Open) w.Open();
    }

    void Add(UiWindow w)
    {
        w.Ui = this;
        Windows.Add(w);
        AddChild(w);
    }

    void Center(UiWindow w)
    {
        var vs = GetViewportRect().Size;
        if (vs.X <= 0) vs = new Vector2(1280, 720);
        int k = Windows.IndexOf(w);
        float left = LeftInset;
        w.Position = new Vector2(left + Math.Max(0, (vs.X - Hud.PanelW - left - w.Size.X) / 2 + k * 24), Math.Max(0, 96 + k * 18));
    }

    // Front-most open window.
    public UiWindow Top => GetChildren().OfType<UiWindow>().LastOrDefault(w => w.Visible);

    public void Raise(UiWindow w)
    {
        MoveChild(w, -1);
        if (toastBox != null) MoveChild(toastBox, -1);
    }

    // Is the screen point over a window or the sidebar (then the world must not react to the mouse)?
    public bool IsOver(Vector2 p)
    {
        if (!Visible) return false;
        if (p.X < LeftInset) return true;
        foreach (var w in Windows) if (w.Visible && w.GetGlobalRect().HasPoint(p)) return true;
        return false;
    }

    // A text field has the keyboard: keys must not steer the camera or the game.
    public bool Typing => GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit;

    // Runs an action on the main thread (from simulation callbacks).
    public void Post(Action a) => posted.Enqueue(a);

    public void Toast(string text, bool bad = false)
    {
        GD.Print((bad ? "notice! " : "notice: ") + text);
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", UiKit.Box(new Color(0.05f, 0.055f, 0.07f, 0.94f), 4, 10, bad ? UiKit.Bad with { A = 0.6f } : UiKit.Rule, 5));
        var l = UiKit.Text(text, 13, bad ? UiKit.Bad : UiKit.Fg);
        l.MouseFilter = MouseFilterEnum.Ignore;
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(Math.Min(480, 12 + UiKit.Ui.GetStringSize(text, HorizontalAlignment.Left, -1, 13).X), 0);
        panel.AddChild(l);
        toastBox.AddChild(panel);
        toasts.Add((panel, now + 5 + text.Length / 40.0));
        while (toasts.Count > 5) { toasts[0].c.QueueFree(); toasts.RemoveAt(0); }
    }

    public override void _Input(InputEvent e)
    {
        if (!Visible) return;
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Tab } tab && !tab.ShiftPressed && !tab.CtrlPressed
            && !tab.AltPressed && !tab.MetaPressed && !Typing && !Main.FastForward)
        {
            Sidebar.Toggle();   // here, before the GUI: Tab must not move the keyboard focus instead
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && !Main.FastForward && Top is { } top)
        {
            top.Close();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is InputEventMouseButton { Pressed: true } mb)
        {
            // A click in a window brings it to the front; a click elsewhere takes the keyboard away from its fields.
            UiWindow hit = null;
            foreach (var w in GetChildren().OfType<UiWindow>()) if (w.Visible && w.GetGlobalRect().HasPoint(mb.Position)) hit = w;
            if (hit != null) { if (hit != Top) Raise(hit); }
            else if (GetViewport().GuiGetFocusOwner() is { } f && IsAncestorOf(f)) f.ReleaseFocus();
        }
    }

    public override void _Process(double delta)
    {
        now += delta;
        while (posted.TryDequeue(out var a))
        {
            try { a(); } catch (Exception x) { GD.PrintErr("ui: " + x); }
        }
        var sim = Main.Sim;
        if (sim != null)
            while (sim.Notices.TryDequeue(out var n))
            {
                bool bad = n.StartsWith(SimRunner.BadNotice);
                Toast(bad ? n.Substring(SimRunner.BadNotice.Length) : n, bad);
            }
        Visible = !Main.FastForward;
        var vs = GetViewportRect().Size;
        toastBox.Position = new Vector2(LeftInset + 14, vs.Y - 132 - toastBox.Size.Y);
        for (int i = toasts.Count - 1; i >= 0; i--)
        {
            var (c, until) = toasts[i];
            double left = until - now;
            if (left <= 0) { c.QueueFree(); toasts.RemoveAt(i); toastBox.Size = Vector2.Zero; continue; }
            c.Modulate = new Color(1, 1, 1, (float)Math.Min(1, left / 0.6));
        }
        if (saveAt >= 0 && now >= saveAt) { saveAt = -1; SaveState(); }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized && loaded) foreach (var w in Windows) w.KeepInside();
    }

    // ---- ui.json ----

    public void SaveSoon() { if (loaded) saveAt = now + 0.5; }

    void LoadState()
    {
        try { if (File.Exists(UiPath)) State = JsonSerializer.Deserialize<UiState>(File.ReadAllText(UiPath)) ?? new UiState(); }
        catch (Exception e) { GD.PrintErr("ui.json: " + e.Message); State = new UiState(); }
    }

    public void SaveState()
    {
        foreach (var w in Windows)
            State.Windows[w.Id] = new UiState.Win { X = w.Position.X, Y = w.Position.Y, W = w.Size.X, H = w.Size.Y, Open = w.Visible };
        try { File.WriteAllText(UiPath, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { GD.PrintErr("ui.json: " + e.Message); }
    }

    public override void _ExitTree() { if (loaded) SaveState(); }

    // Autosave settings for a (new) simulation runner.
    public void ConfigureRunner(SimRunner sim)
    {
        Directory.CreateDirectory(SavesDir);
        sim.AutosaveDir = SavesDir;
        sim.AutosaveMinutes = State.AutosaveMinutes;
        sim.AutosaveSlots = Math.Max(1, State.AutosaveSlots);
    }

    public UiWindow ById(string id) => Windows.FirstOrDefault(w => w.Id == id);
}
