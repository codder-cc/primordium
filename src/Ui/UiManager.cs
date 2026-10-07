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
}

// The game's windows over the world: their stacking, Esc, toasts with what the simulation did, a small
// toolbar, and remembering it all in user://ui.json. Lives on the HUD's canvas layer above the panel.
public partial class UiManager : Control
{
    public Main Main;
    public bool RestoreWindows = true;   // reopen the windows that were open last time
    public UiState State = new();
    public readonly List<UiWindow> Windows = new();
    public LawsWindow Laws;
    public NewWorldWindow NewWorld;
    public SavesWindow Saves;
    public CreatorWindow Creator;
    public ChronicleWindow Chronicle;
    public FossilWindow Fossil;
    public EvolutionWindow Evolution;

    readonly ConcurrentQueue<Action> posted = new();
    VBoxContainer toastBox;
    HBoxContainer toolbar;
    readonly List<(Control c, double until)> toasts = new();
    double saveAt = -1, now;
    bool loaded;

    public static string UiPath => UiKit.Global("user://ui.json");
    public static string SavesDir => UiKit.Global("user://saves");
    public static string PresetsDir => UiKit.Global("user://presets");
    public static string CreaturesDir => UiKit.Global("user://creatures");

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        Theme = UiKit.Theme;
        LoadState();

        toolbar = UiKit.Row(4);
        toolbar.Position = new Vector2(14, 88);
        foreach (var (text, tip, act) in new (string, string, Action)[]
        {
            ("Законы  F2", "законы мира: цены, прочность, климат…", () => Laws.Toggle()),
            ("Новый мир  F4", "seed, население, самозарождение, удары, набор законов", () => NewWorld.Toggle()),
            ("Сохранения  F6", "слоты, загрузка, автосохранение (F5 — быстро сохранить, F9 — загрузить)", () => Saves.Toggle()),
            ("Конструктор  F7", "свои существа: геном, тело, посадка кистью 5", () => Creator.Toggle()),
            ("Хроника  F8", "события мира, окаменелости; биография выбранного — вкладка в карточке существа", () => Chronicle.Toggle()),
            ("Ход эволюции  F10", "новизна против нейтральной тени, сложность, экология, темп, филогения; сводная подсказка", () => Evolution.Toggle()),
        })
        {
            var b = UiKit.Button(text, act, tip);
            b.AddThemeFontSizeOverride("font_size", 12);
            b.AddThemeStyleboxOverride("normal", UiKit.Box(new Color(0.04f, 0.045f, 0.06f, 0.85f), 4, 8, UiKit.Rule, 3));
            toolbar.AddChild(b);
        }
        AddChild(toolbar);

        Add(Laws = new LawsWindow());
        Add(NewWorld = new NewWorldWindow());
        Add(Saves = new SavesWindow());
        Add(Creator = new CreatorWindow());
        Add(Chronicle = new ChronicleWindow());
        Add(Fossil = new FossilWindow());
        Add(Evolution = new EvolutionWindow());

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
        loaded = true;
        if (RestoreWindows) CallDeferred(nameof(RestoreOpen));
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
        w.Position = new Vector2(Math.Max(0, (vs.X - Hud.PanelW - w.Size.X) / 2 + k * 24), Math.Max(0, 96 + k * 18));
    }

    // Front-most open window.
    public UiWindow Top => GetChildren().OfType<UiWindow>().LastOrDefault(w => w.Visible);

    public void Raise(UiWindow w)
    {
        MoveChild(w, -1);
        if (toastBox != null) MoveChild(toastBox, -1);
    }

    // Is the screen point over a window or the toolbar (then the world must not react to the mouse)?
    public bool IsOver(Vector2 p)
    {
        if (!Visible) return false;
        if (toolbar.GetGlobalRect().HasPoint(p)) return true;
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
        toolbar.Visible = !Main.FastForward;
        toastBox.Position = new Vector2(14, vs.Y - 132 - toastBox.Size.Y);
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
