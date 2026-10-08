using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;

namespace Primordium;

public partial class Main : Node
{
    // The world is stepped on its own thread (SimRunner) and owned by it: the main thread reads the
    // published SimFrame and the terrain arrays, and changes the world only through Sim.Do(...).
    public SimRunner Sim { get; private set; }
    public World World => Sim?.World;
    public View3D View { get; private set; }
    public SimFrame Frame;                      // the newest published picture of the world
    public int Tpf { get => Sim.Tpf; set => Sim.Tpf = value; }
    public bool Paused { get => Sim.Paused; set => Sim.Paused = value; }
    public double Tps => Sim.Tps;
    public double SimMs => Sim.SimMs;
    public double ViewMs;                       // smoothed ms per frame of drawing
    SimStats Stats => Sim.Stats;
    public Census Census => Stats.Census;
    public World.Climate Climate => Stats.Climate;
    public List<(long lin, int n, int gen, Agent rep)> Lineages => Stats.Lineages;
    public List<(string name, Agent a, string value)> Records => Stats.Records;
    public int KinCount => Stats.KinCount;
    public bool ShowRecords;
    public long FastTo { get => Sim.FastTo; set => Sim.FastTo = value; }   // fast-forward: simulate without drawing until this tick
    public long FastFrom => Sim.FastFrom;
    public bool FastForward => Sim?.FastForward ?? false;
    int initialPop = P.InitialPop;
    // Off by default (the player starts life and strikes): A and ⇧X toggle them, --abio/--strikes at start.
    bool abiogenesis, strikes;
    int worldGeneration;

    Hud hud;
    public UiManager Ui { get; private set; }
    public int InitialPop => initialPop;
    string[] openAtStart;
    string langAtStart;
    int uiTest = -1;
    PerfOverlay perf;
    int perfEvery, perfQuit = -1, perfDone, pauseAfter = -1, startTpf = 8;
    double hudWait;
    bool hudInput;
    readonly HashSet<string> printedErrors = new();
    PanelContainer fastPanel;
    LineEdit fastEdit;
    Label fastNote, fastTitle, fastHint;
    int frame, shotFrames = -1;
    string shotPath;
    bool lDown, rDown, mDown, dragged;
    Vector2 pressPos;

    // The hand: a brush that pours matter (a new random kind every stroke) or water, kills or digs.
    static readonly string[] ToolNamesEn = { "", "pour", "water", "kill", "dig", "plant" };
    static readonly string[] ToolNamesRu = { "", "насыпать", "вода", "убить", "копнуть", "посадить" };
    public static string[] ToolNames => Loc.T(ToolNamesEn, ToolNamesRu);
    public int Tool;
    public volatile int PourSpecies = -1;   // chosen on the simulation thread (it draws from the world's random numbers)
    public bool PourLock;                   // brush 1 keeps its material from stroke to stroke (Z; I takes the one under the cursor)
    public float BrushR = 3;
    bool painting;
    double paintWait;
    int startTool, paintDabs;   // for screenshots: a brush and a number of dabs at the centre of the screen
    int plantAtStart;           // --plant N: N bodies of the first example design brought in near the busiest lineage, one selected (screenshots of the mark)

    public override void _Ready()
    {
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized);
        var args = OS.GetCmdlineUserArgs();
        if (Array.IndexOf(args, "--abio") >= 0) abiogenesis = true;
        if (Array.IndexOf(args, "--noabio") >= 0) abiogenesis = false;
        if (Array.IndexOf(args, "--strikes") >= 0) strikes = true;
        ShowRecords = Array.IndexOf(args, "--records") >= 0;
        int seed = (int)(Time.GetTicksMsec() % 100000), warm = 0, slice = -1;
        float zoom = 0, focus = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--seed") seed = int.Parse(args[i + 1]);
            if (args[i] == "--warm") warm = int.Parse(args[i + 1]);
            if (args[i] == "--shot") shotPath = args[i + 1];
            if (args[i] == "--slice") slice = int.Parse(args[i + 1]);
            if (args[i] == "--pop") initialPop = int.Parse(args[i + 1]);
            if (args[i] == "--focus") focus = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            if (args[i] == "--zoom") zoom = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            if (args[i] == "--tool") startTool = int.Parse(args[i + 1]);
            if (args[i] == "--paint") paintDabs = int.Parse(args[i + 1]);
            if (args[i] == "--plant") plantAtStart = int.Parse(args[i + 1]);
            if (args[i] == "--overlay") startOverlay = int.Parse(args[i + 1]);
            if (args[i] == "--perf") perfEvery = int.Parse(args[i + 1]);
            if (args[i] == "--perfquit") perfQuit = int.Parse(args[i + 1]);
            if (args[i] == "--pauseafter") pauseAfter = int.Parse(args[i + 1]);
            if (args[i] == "--tpf") startTpf = int.Parse(args[i + 1]);
            if (args[i] == "--viewthreads") View3D.ViewWorkers = int.Parse(args[i + 1]);
            if (args[i] == "--open") openAtStart = args[i + 1].Split(',');
            if (args[i] == "--lang") langAtStart = args[i + 1];
        }

        View = new View3D();
        AddChild(View);
        var layer = new CanvasLayer();
        AddChild(layer);
        hud = new Hud { Main = this };
        layer.AddChild(hud);
        BuildFastForm(layer);
        perf = new PerfOverlay { Main = this };
        layer.AddChild(perf);
        if (Array.IndexOf(args, "--perfshow") >= 0) perf.Visible = true;

        NewWorld(seed, warm);
        // The windows (laws, new world, saves, creature editor) and toasts, above the panel.
        Ui = new UiManager { Main = this, RestoreWindows = openAtStart == null, LanguageOverride = langAtStart };
        layer.AddChild(Ui);
        Ui.ConfigureRunner(Sim);
        if (openAtStart != null)
            foreach (var item in openAtStart)
            {
                // "window" or "window:view" (newworld:more, creator:looks, creator:help) — for screenshots.
                if (item == "none") continue;   // no window, and none restored from ui.json either
                var part = item.Split(':');
                var win = Ui.ById(part[0] == "params" ? "laws" : part[0]) ?? throw new ArgumentException("--open: laws|params, newworld[:more], saves, creator[:looks|:help], chronicle[:fossils], fossil[:bio|:ancestry], evolution, tree[:lineages], metrics, catastrophes, life[:creatures|:populations], regions[:select|:paste]");
                win.Open();
                if (part.Length > 1) win.ShowView(part[1]);
            }
        if (Array.IndexOf(args, "--uitest") >= 0) uiTest = 0;
        View.Slice = slice;
        View.Overlay = startOverlay;
        if (zoom > 0) View.ZoomAt(zoom);
        if (Array.IndexOf(args, "--strike") >= 0)
        {
            // For screenshots: strike the busiest place right away.
            var a = Lineages.Count > 0 ? Lineages[0].rep : null;
            if (a != null) Sim.Do(w => w.StrikeAt(a.X, a.Y, 9));
        }
        if (focus > 0 && Lineages.Count > 0)
        {
            // Select an agent of the biggest lineage (or the body covering most cells) and look at it
            // from close up (for screenshots).
            View.Selected = Lineages[0].rep;
            if (Array.IndexOf(args, "--oldest") >= 0 && Records.Count > 0) View.Selected = Records[0].a;
            if (Array.IndexOf(args, "--big") >= 0)
                View.Selected = BiggestRecord() ?? View.Selected;
            View.LookAt(View.Selected);
            View.ZoomAt(focus);
        }
        lookSun = Array.IndexOf(args, "--looksun") >= 0;
        lookEclipse = lookSun || Array.IndexOf(args, "--lookeclipse") >= 0;
        if (Array.IndexOf(args, "--fastform") >= 0) OpenFastForm();   // for screenshots of the form
        // For screenshots of the biography: select the living body with the longest biography, show that tab.
        if (Array.IndexOf(args, "--seltracked") >= 0)
            Sim.Do(w =>
            {
                var a = w.Agents.Where(x => !x.Dead && x.Tracked).OrderByDescending(x => x.BioN).ThenBy(x => x.Id).FirstOrDefault();
                if (a != null) Ui.Post(() => { View.Selected = a; View.LookAt(a); View.ZoomAt(30); });
            });
        // A saved world instead of the new one (as F9 / the saves window would load it).
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--load") Sim.Load(args[i + 1]);
        selCave = Array.IndexOf(args, "--selcave") >= 0;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--tab") hud.AgentTab = args[i + 1] == "bio" ? 1 : 0;
        if (startTool > 0) SetTool(startTool);
        if (shotPath != null) shotFrames = uiTest >= 0 ? 128 : 60;   // with --uitest: after the planting, with its toasts and a planted body selected
    }

    int startOverlay;
    bool lookEclipse, lookSun;
    bool selCave;

    // A new world: the old simulation thread is stopped, the new world is warmed up here
    // (synchronously, as before) and then runs on its own thread.
    void NewWorld(int seed, int warm = 0)
    {
        bool wasPaused = Sim?.Paused ?? false;
        int tpf = Sim?.Tpf ?? startTpf;
        Sim?.Stop();
        var settings = new WorldSettings { Seed = seed, InitialPop = initialPop, Abiogenesis = abiogenesis, Strikes = strikes };
        var sim = new SimRunner(new World(settings)) { Tpf = tpf, Paused = wasPaused };
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < warm; i++)
        {
            sim.TickOnce();
            if (perfEvery > 0 && i % 1000 == 999) GD.Print($"warm {i + 1} pop {sim.World.Agents.Count} {sw.Elapsed.TotalSeconds:F0}s");
        }
        painting = false;
        PourLock = false;   // another world, other matter
        if (Tool == 1) PourSpecies = sim.World.RandomPourable();
        View.SetWorld(sim.World);
        worldGeneration = sim.WorldGeneration;
        Sim = sim;
        Ui?.ConfigureRunner(sim);
        sim.Start();
        Frame = sim.Acquire();
        perf?.Reset(sim);
    }

    // The holder of the "covers the most cells" record (found by its size, not by the record's name,
    // which is in the interface language): the body among the record holders that covers most cells.
    Agent BiggestRecord() => Records.Count == 0 ? null : Records.Select(r => r.a).Where(a => a != null).MaxBy(a => a.Cells);

    // A new world from the "New world" window: made on the simulation thread and swapped in between ticks.
    public void CreateWorld(WorldSettings settings)
    {
        initialPop = settings.InitialPop;
        abiogenesis = settings.Abiogenesis;
        strikes = settings.Strikes;
        Ui.Toast(Loc.T($"creating a world: seed {settings.Seed}, population {settings.InitialPop}…", $"создаю мир: seed {settings.Seed}, население {settings.InitialPop}…"));
        Sim.NewWorld(settings);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) Sim?.Stop();
    }

    public override void _ExitTree() => Sim?.Stop();

    public override void _Process(double delta)
    {
        if (World == null) return;
        var total = Stopwatch.StartNew();
        if (Sim.Error is { } err && printedErrors.Add(err)) GD.PrintErr("simulation stopped: " + err);
        if (Sim.WorldGeneration != worldGeneration)
        {
            // The simulation thread swapped in another world (SimRunner.NewWorld or Load).
            worldGeneration = Sim.WorldGeneration;
            View.SetWorld(Sim.World);
            abiogenesis = Sim.World.Abiogenesis;
            strikes = Sim.World.AutoStrikes;
        }
        Frame = Sim.Acquire();
        if (View.Selected is { Dead: true } && View.Follow) View.Follow = false;
        Sim.Selected = View.Selected;
        Sim.Hover = View.Hover;
        Sim.WantStress = View.Overlay == 7;
        // The clade tree is built on the simulation thread only while the tree window or the range overlay shows it.
        Sim.Obs.WantTree = View.Overlay == View3D.RangeOverlay || (Ui?.Tree?.Visible ?? false);
        View.Ranges = Sim.Obs.Tree;
        frame++;
        if (FastForward)
        {
            // Nothing is drawn but the progress: the simulation thread runs flat out.
            if ((hudWait -= delta) <= 0) { hud.QueueRedraw(); hudWait = 0.1; }
            Perf(delta, total);
            return;
        }
        var mouse = GetViewport().GetMousePosition();
        // A press that started on the map but was let go over a window never reaches _UnhandledInput.
        if (!Input.IsMouseButtonPressed(MouseButton.Left)) { lDown = false; painting = false; }
        if (!Input.IsMouseButtonPressed(MouseButton.Right)) rDown = false;
        if (!Input.IsMouseButtonPressed(MouseButton.Middle)) mDown = false;
        if (uiTest >= 0) UiTest();
        if (lookEclipse && frame == 30)
        {
            // For screenshots of an eclipse: fly to the moon's shadow (World.Sky).
            lookEclipse = false;
            var w = World;
            if (w != null && lookSun) { View.LookAtCell(Math.Clamp((int)w.SunX, 0, World.W - 1), World.H / 2); View.ZoomAt(160); }   // --looksun: the day side
            else if (w != null && w.EclipseNow) { View.LookAtCell(Math.Clamp((int)w.EclipseX, 0, World.W - 1), Math.Clamp((int)w.EclipseY, 0, World.H - 1)); View.ZoomAt(120); }
        }
        if (selCave && frame == 30)
        {
            // For screenshots of the cave climate: select the living body under the thickest roof and cut the view through it.
            selCave = false;
            Sim.Do(w =>
            {
                var a = w.Agents.Where(x => !x.Dead && w.InCave(x)).OrderByDescending(x => w.Roof(x.Y * World.W + x.X, x.Z)).ThenBy(x => x.Id).FirstOrDefault();
                if (a != null) Ui.Post(() => { View.Selected = a; if (View.Slice < 0) View.Slice = a.Y; View.LookAt(a); View.ZoomAt(80); });
            });
        }
        View.BrushCell = Tool > 0 && !OverPanel(mouse) ? View.PickCell(mouse) : -1;
        View.BrushR = BrushR;
        View.BrushTool = Tool;
        if (painting && View.BrushCell >= 0 && (paintWait -= delta) <= 0)
        {
            Paint(View.BrushCell);
            paintWait = 1.0 / 15;
        }
        if (frame % 3 == 0 && !lDown && !rDown && !mDown)
        {
            var mp = GetViewport().GetMousePosition();
            View.Hover = OverPanel(mp) ? null : View.Pick(mp);
            View.CursorCell = OverPanel(mp) ? -1 : View.PickCell(mp);   // the sky under the cursor (HUD)
        }
        View.Speed = Paused ? 1 : Tpf;

        float sp = (float)delta * 900;
        var pan = Vector2.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) pan.Y += sp;
        if (Input.IsPhysicalKeyPressed(Key.S)) pan.Y -= sp;
        if (Input.IsPhysicalKeyPressed(Key.A)) pan.X += sp;
        if (Input.IsPhysicalKeyPressed(Key.D)) pan.X -= sp;
        if (pan != Vector2.Zero && !fastPanel.Visible && !Ui.Typing) View.Pan(pan);
        if (fastPanel.Visible)
        {
            var vs = GetViewport().GetVisibleRect().Size;
            fastPanel.Position = new Vector2((vs.X - Hud.PanelW - fastPanel.Size.X) / 2, (vs.Y - fastPanel.Size.Y) / 2);
        }

        var sw = Stopwatch.StartNew();
        View.Frame = Frame;
        View.Refresh((float)delta);
        ViewMs = ViewMs * 0.9 + sw.Elapsed.TotalMilliseconds * 0.1;
        // The panel redraws ~10 times a second, ~30 while the mouse moves or keys are pressed.
        hudWait -= delta;
        if (hudWait <= 0 || (hudInput && hudWait <= 0.067) || shotFrames >= 0)
        {
            hud.QueueRedraw();
            hudWait = 0.1;
            hudInput = false;
        }

        if (shotFrames == 8 && Array.IndexOf(OS.GetCmdlineUserArgs(), "--big") >= 0)
        {
            if (BiggestRecord() is { } big) { View.Selected = big; View.LookAt(big); View.ZoomAt(22); }
        }
        if (shotFrames == 8 && Array.IndexOf(OS.GetCmdlineUserArgs(), "--oldest") >= 0)
        {
            if (Records.Count > 0) { View.Selected = Records[0].a; View.LookAt(Records[0].a); }
        }
        if (frame == 20 && plantAtStart > 0 && CreatureExamples.All.Count > 0)
        {
            var at = Lineages.Count > 0 ? Lineages[0].rep : null;
            int cx = at?.X ?? World.W / 2, cy = at?.Y ?? World.H / 2;
            Sim.SpawnDesign(CreatureExamples.All[0], cx, cy, new SpawnOptions { Matter = MatterSource.Import, Energy = EnergySource.Import, Count = plantAtStart, Radius = 4 });
            Sim.Do(w =>
            {
                var a = w.Agents.FirstOrDefault(x => x.Designed && !x.Dead);
                if (a != null) Ui.Post(() => { View.Selected = a; View.LookAt(a); View.ZoomAt(24); });
            });
        }
        if (shotFrames == 30 && paintDabs > 0)
        {
            int c = View.PickCell(GetViewport().GetVisibleRect().Size / 2);
            for (int k = 0; k < paintDabs && c >= 0 && Tool > 0; k++) Paint(c);
        }
        if (shotFrames > 0 && --shotFrames == 0)
        {
            GetViewport().GetTexture().GetImage().SavePng(shotPath);
            Sim.Stop();
            GetTree().Quit();
        }
        Perf(delta, total);
    }

    // The F3 overlay and the --perf log.
    void Perf(double delta, Stopwatch total)
    {
        perf.Frame(delta, total.Elapsed.TotalMilliseconds, hud.DrawMs, View.Prof);
        hud.DrawMs = 0;
        Array.Clear(View.Prof);
        if (perfEvery > 0 && perf.Frames >= perfEvery)
        {
            string line = perf.Report(false);
            GD.Print(line);
            if (perf.Visible) perf.Show(perf.Report(true));
            perf.Reset(Sim);
            perfDone++;
            if (perfDone == pauseAfter) { Paused = true; GD.Print("PERF -> paused"); }
            if (perfDone == perfQuit) { Sim.Stop(); GetTree().Quit(); }
        }
        else if (perfEvery <= 0 && perf.Visible && perf.Seconds >= 0.5)
        {
            perf.Show(perf.Report(true));
            perf.Reset(Sim);
        }
    }

    // ---- fast-forward form ----

    void BuildFastForm(CanvasLayer layer)
    {
        fastPanel = new PanelContainer { Visible = false };
        var style = new StyleBoxFlat { BgColor = new Color(0.05f, 0.055f, 0.07f, 0.97f), ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 14, ContentMarginBottom = 14 };
        style.SetCornerRadiusAll(6);
        fastPanel.AddThemeStyleboxOverride("panel", style);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        var title = fastTitle = new Label();
        title.AddThemeFontSizeOverride("font_size", 17);
        var hint = fastHint = new Label();
        hint.AddThemeFontSizeOverride("font_size", 13);
        hint.AddThemeColorOverride("font_color", new Color(0.6f, 0.64f, 0.71f));
        fastEdit = new LineEdit { CustomMinimumSize = new Vector2(320, 0), PlaceholderText = "10" };
        fastEdit.AddThemeFontSizeOverride("font_size", 16);
        fastEdit.TextSubmitted += OnFastSubmitted;
        fastEdit.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                CloseFastForm();
                fastEdit.AcceptEvent();
            }
        };
        fastNote = new Label { Text = "" };
        fastNote.AddThemeFontSizeOverride("font_size", 12);
        fastNote.AddThemeColorOverride("font_color", new Color(1f, 0.55f, 0.5f));
        box.AddChild(title);
        box.AddChild(hint);
        box.AddChild(fastEdit);
        box.AddChild(fastNote);
        fastPanel.AddChild(box);
        layer.AddChild(fastPanel);
    }

    void OpenFastForm()
    {
        // Set on every opening: the language may have changed since the form was built.
        fastTitle.Text = Loc.T("Fast-forward without rendering", "Промотка без отрисовки");
        fastHint.Text = Loc.T("How many days to fast-forward? For example 25.\nOr \"to 200\" — up to day 200.\nEnter — start (empty — 10 days), Esc — close.",
            "Сколько суток промотать? Например 25.\nИли «до 200» — до 200-х суток.\nEnter — начать (пусто — 10 суток), Esc — закрыть.");
        fastNote.Text = Loc.T($"now day {World.Day + 1}", $"сейчас сутки {World.Day + 1}");
        fastNote.AddThemeColorOverride("font_color", new Color(0.6f, 0.64f, 0.71f));
        fastEdit.Text = "";
        fastPanel.Visible = true;
        fastEdit.GrabFocus();
    }

    void CloseFastForm()
    {
        fastPanel.Visible = false;
        fastEdit.ReleaseFocus();
    }

    void OnFastSubmitted(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (text.Length == 0) text = "10";
        bool until = text.StartsWith("до") || text.StartsWith("to") || text.StartsWith("until");
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (!long.TryParse(digits, out long n) || n <= 0 || n > 100000)
        {
            fastNote.Text = Loc.T("enter a number of days, e.g. 25 or \"to 200\"", "нужно число суток, например 25 или «до 200»");
            fastNote.AddThemeColorOverride("font_color", new Color(1f, 0.55f, 0.5f));
            return;
        }
        long now = Sim.Tick;
        long target = until ? (n - 1) * P.DayLen : now + n * P.DayLen;
        if (target <= now)
        {
            fastNote.Text = Loc.T($"day {n} has already passed (now day {World.Day + 1})", $"сутки {n} уже прошли (сейчас {World.Day + 1})");
            fastNote.AddThemeColorOverride("font_color", new Color(1f, 0.55f, 0.5f));
            return;
        }
        CloseFastForm();
        StartFast(target);
    }

    void StartFast(long target)
    {
        Sim.FastFrom = Sim.Tick;
        FastTo = target;
    }

    // A living body of the newest frame by its id (null if it is dead or gone).
    public Agent FindAlive(long id)
    {
        var f = Frame;
        if (f == null || id <= 0) return null;
        for (int k = 0; k < f.Count; k++)
        {
            var a = f.Agents[k].Ref;
            if (a != null && a.Id == id && !a.Dead) return a;
        }
        return null;
    }

    public void Focus(Agent a)
    {
        View.Selected = a;
        View.LookAt(a);
    }

    bool OverPanel(Vector2 p) => p.X > GetViewport().GetVisibleRect().Size.X - Hud.PanelW || (Ui?.IsOver(p) ?? false);

    // One dab of the brush at a column (15 a second while the button is held). Applied by the
    // simulation thread between ticks.
    void Paint(int c)
    {
        int x = c % World.W, y = c / World.W;
        float r = BrushR;
        switch (Tool)
        {
            case 1: Sim.Do(w => { if (PourSpecies >= 0) w.Pour(x, y, r, PourSpecies, 0.25f); }); break;   // a quarter of a block at the centre per dab
            case 2: Sim.Do(w => w.PourWater(x, y, r, 0.15f)); break;
            case 3: Sim.Do(w => w.KillIn(x, y, r)); break;
            case 4: Sim.Do(w => w.DigOut(x, y, r, 0.25f)); break;
        }
    }

    // A new random kind of matter for pouring (drawn from the world's random numbers, so on its thread).
    void NewPourSpecies() => Sim.Do(w => PourSpecies = w.RandomPourable());

    public bool PerfShown => perf.Visible;
    public void TogglePerf() { perf.Visible = !perf.Visible; perf.Reset(Sim); }

    public void SetTool(int t)
    {
        Tool = Tool == t ? 0 : t;   // the same key again puts the brush away
        painting = false;
        if (Tool == 1 && !(PourLock && PourSpecies >= 0)) NewPourSpecies();
    }

    // Brush 1: keep the material (Z), step to the next one and keep it (⇧Z), or take the one of the top
    // block under the cursor (I). Choosing reads the world only (no random numbers drawn).
    void PourKey(Key key, bool shift)
    {
        var w = World;
        if (key == Key.Z && !shift)
        {
            PourLock = !PourLock;
            if (!PourLock) NewPourSpecies();
            Ui.Toast(PourLock ? Loc.T($"brush 1 keeps {w.Chem.MatName[PourSpecies + 2]} for every stroke", $"кисть 1 сыплет {w.Chem.MatName[PourSpecies + 2]} каждым мазком")
                              : Loc.T("brush 1: a new random material each stroke", "кисть 1: каждый мазок — новый случайный материал"));
            return;
        }
        int s = PourSpecies;
        if (key == Key.Z)
        {
            do s = (s + 1) % Chemistry.S; while (s == w.Chem.Gas);
        }
        else
        {
            int c = View.PickCell(GetViewport().GetMousePosition());
            if (c < 0) { Ui.Toast(Loc.T("point at the ground to take its material", "наведите на землю, чтобы взять её материал"), true); return; }
            int h = w.Height[c];
            int m = h > 0 ? w.Mat[c * World.Z + h - 1] : 0;
            if (m < 2 || m - 2 == w.Chem.Gas)
            {
                // No aggregate on top: the most plentiful loose molecule lying there, if any.
                float best = 0.5f; m = -1;
                for (int k = 0; k < Chemistry.S; k++)
                    if (k != w.Chem.Gas && w.C[k][c].F > best) { best = w.C[k][c].F; m = k + 2; }
                if (m < 0) { Ui.Toast(Loc.T("nothing to take here: no rock and no loose matter on top", "здесь нечего взять: сверху нет ни породы, ни рыхлого"), true); return; }
            }
            s = m - 2;
        }
        PourSpecies = s;
        PourLock = true;
        Ui.Toast(Loc.T($"brush 1 keeps {w.Chem.MatName[s + 2]} for every stroke (Z — random again)", $"кисть 1 сыплет {w.Chem.MatName[s + 2]} каждым мазком (Z — снова случайный)"));
    }

    public override void _UnhandledInput(InputEvent e)
    {
        hudInput = true;
        switch (e)
        {
            case InputEventKey { Pressed: true, Echo: false } k:
                OnKey(k);
                break;
            case InputEventMouseButton mb:
                OnMouseButton(mb);
                break;
            case InputEventMouseMotion mm:
                if (lDown && (dragged || (mm.Position - pressPos).Length() > 4)) { dragged = true; View.Pan(mm.Relative); }
                if (mDown) View.Pan(mm.Relative);
                if (rDown) View.Orbit(mm.Relative);
                break;
            case InputEventPanGesture pg:
                if (OverPanel(pg.Position)) hud.ScrollAgent(pg.Delta.Y * 30);
                else View.Pan(-pg.Delta * 14);
                break;
            case InputEventMagnifyGesture mg:
                View.ZoomBy(1f / mg.Factor);
                break;
        }
    }

    void OnKey(InputEventKey k)
    {
        var v = View;
        if (Ui.Typing && k.PhysicalKeycode is < Key.F1 or > Key.F12) return;   // keys go to the text field
        hudWait = 0;   // show the change right away
        switch (k.PhysicalKeycode)
        {
            case Key.Space: Paused = !Paused; break;
            case Key.T:
                if (FastForward) FastTo = -1;
                else if (k.ShiftPressed) StartFast(Sim.Tick + 10L * P.DayLen);
                else OpenFastForm();
                break;
            case Key.Period: if (Paused) Sim.Step(); break;
            case Key.Equal: case Key.KpAdd: Tpf = Math.Min(256, Tpf * 2); break;
            case Key.Minus: case Key.KpSubtract: Tpf = Math.Max(1, Tpf / 2); break;
            case Key.Q: v.RotateStep(-1); break;
            case Key.E: v.RotateStep(1); break;
            case Key.G: v.ResetCamera(); break;
            case Key.F:
                v.Follow = !v.Follow && v.Selected != null;
                if (v.Follow) v.LookAt(v.Selected);
                break;
            case Key.F1: Ui.Tree.Toggle(); break;
            case Key.F12: Ui.Metrics.Toggle(); break;
            case Key.Z when Tool == 1: PourKey(Key.Z, k.ShiftPressed); break;
            case Key.I when Tool == 1: PourKey(Key.I, false); break;
            case Key.F3: TogglePerf(); break;
            case Key.F2: Ui.Laws.Toggle(); break;
            case Key.F4: Ui.NewWorld.Toggle(); break;
            case Key.F5: Ui.Saves.QuickSave(); break;
            case Key.F6: Ui.Saves.Toggle(); break;
            case Key.F7: Ui.Creator.Toggle(); break;
            case Key.F8: Ui.Chronicle.Toggle(); break;
            case Key.F9: Ui.Saves.QuickLoad(); break;
            case Key.F10: Ui.Evolution.Toggle(); break;
            case Key.F11: Ui.Catastrophes.Toggle(); break;
            case Key.U: Ui.Life.Toggle(); break;
            case Key.Y: Ui.Regions.Toggle(); break;
            case Key.K: v.KinFocus = !v.KinFocus; break;
            case Key.B: ShowRecords = !ShowRecords; break;
            case Key.O:
                if (Records.Count > 0) { Focus(Records[0].a); v.Follow = true; }
                break;
            case Key.H: hud.Legend = !hud.Legend; break;
            case Key.V: v.ColorMode = (v.ColorMode + 1) % View3D.ColorModeNames.Length; break;
            case Key.M: v.Overlay = (v.Overlay + (k.ShiftPressed ? -1 : 1) + v.OverlayCount) % v.OverlayCount; break;
            case Key.L: v.Lighting = !v.Lighting; break;
            case Key.C: v.Slice = v.Slice < 0 ? World.H / 2 : -1; break;
            case Key.Bracketleft:
                if (Tool > 0) BrushR = Math.Max(1, BrushR - 1);
                else if (v.Slice >= 0) v.Slice = Math.Max(0, v.Slice - 2);
                break;
            case Key.Bracketright:
                if (Tool > 0) BrushR = Math.Min(24, BrushR + 1);
                else if (v.Slice >= 0) v.Slice = Math.Min(World.H - 1, v.Slice + 2);
                break;
            case Key.Key1: case Key.Kp1: SetTool(1); break;
            case Key.Key2: case Key.Kp2: SetTool(2); break;
            case Key.Key3: case Key.Kp3: SetTool(3); break;
            case Key.Key4: case Key.Kp4: SetTool(4); break;
            case Key.Key5: case Key.Kp5: SetTool(5); break;
            case Key.Key0: case Key.Kp0: Tool = 0; painting = false; break;
            case Key.N:
                {
                    // A group of newcomers under the cursor (or somewhere at random).
                    int c = View.PickCell(GetViewport().GetMousePosition());
                    if (c < 0) c = (int)(GD.Randi() % World.N);
                    Sim.Do(w => w.SpawnGroup(c % World.W, c / World.W, 40));
                    break;
                }
            case Key.A:
                abiogenesis = !abiogenesis;
                bool on = abiogenesis;
                Sim.Do(w => w.Abiogenesis = on);
                break;
            case Key.X:
                if (k.ShiftPressed) { strikes = !strikes; Sim.SetStrikes(strikes); }
                else
                {
                    int c = View.PickCell(GetViewport().GetMousePosition());
                    if (c >= 0) Sim.Do(w => w.StrikeAt(c % World.W, c / World.W, 8));
                }
                break;
            case Key.R: NewWorld(k.ShiftPressed ? World.Seed : (int)(Time.GetTicksMsec() % 100000)); break;
            case Key.Escape:
                if (FastForward) { FastTo = -1; break; }
                if (Ui.Regions.CancelTool()) break;   // the regions window's selection or paste brush
                if (Tool > 0) { Tool = 0; painting = false; break; }
                v.Selected = null; v.Follow = false;
                break;
            case Key.P:
                var path = OS.GetSystemDir(OS.SystemDir.Desktop) + $"/primordium-{World.Seed}-{World.Tick}.png";
                GetViewport().GetTexture().GetImage().SavePng(path);
                GD.Print(Loc.T("screenshot: ", "снимок: ") + path);
                break;
        }
    }

    void OnMouseButton(InputEventMouseButton mb)
    {
        switch (mb.ButtonIndex)
        {
            case MouseButton.WheelUp when mb.Pressed:
                if (OverPanel(mb.Position)) hud.ScrollAgent(-60); else View.ZoomBy(0.9f);
                break;
            case MouseButton.WheelDown when mb.Pressed:
                if (OverPanel(mb.Position)) hud.ScrollAgent(60); else View.ZoomBy(1.11f);
                break;
            case MouseButton.Left:
                if (mb.Pressed)
                {
                    if (hud.Click(mb.Position)) return;   // tabs and links of the inspector
                    var hit = hud.HitTest(mb.Position);
                    if (hit != null) { Focus(hit); return; }
                    if (OverPanel(mb.Position)) return;
                    if (Ui.Regions.MapPress(mb.Position)) return;   // selecting or pasting a region (RegionsWindow)
                    if (Tool == 5)
                    {
                        // Plant the creature editor's design in the column under the cursor.
                        int c = View.PickCell(mb.Position);
                        if (c >= 0) { if (Ui.Life.Armed != LifeLibraryWindow.Arm.None) Ui.Life.BrushClick(c); else Ui.Creator.Spawn(c); }
                        return;
                    }
                    if (Tool > 0)
                    {
                        // A stroke: pouring takes a new random kind of matter each time, unless it is locked (Z, I).
                        if (Tool == 1 && !PourLock) NewPourSpecies();
                        painting = true; paintWait = 0;
                        return;
                    }
                    lDown = true; dragged = false; pressPos = mb.Position;
                }
                else if (painting) painting = false;
                else if (lDown)
                {
                    lDown = false;
                    if (!dragged)
                    {
                        View.Selected = View.Pick(mb.Position);
                        if (View.Selected == null) View.Follow = false;
                    }
                }
                break;
            case MouseButton.Right: rDown = mb.Pressed; break;
            case MouseButton.Middle: mDown = mb.Pressed; break;
        }
    }

    // --uitest: change a law, quick-save, plant every example design (brought in and from local
    // matter), quick-load, and report — the toasts are printed as "notice:" lines.
    void UiTest()
    {
        int f = uiTest++;
        int centre = View.PickCell(GetViewport().GetVisibleRect().Size / 2 - new Vector2(Hud.PanelW / 2, 0));
        if (centre < 0) centre = World.H / 2 * World.W + World.W / 2;
        if (f == 20) { GD.Print($"uitest: FaceWork was {SimRunner.ParamValues[ParamRegistry.Find("FaceWork").Index]}"); Sim.SetParam("FaceWork", 6); }
        if (f == 30) Ui.Saves.QuickSave();
        if (f == 90)
        {
            int k = 0;
            foreach (var d in CreatureExamples.All)
            {
                int cx = (centre % World.W + 12 * k++) % World.W, cy = centre / World.W;
                Sim.SpawnDesign(d, cx, cy, new SpawnOptions { Matter = MatterSource.Import, Energy = EnergySource.Import, Count = 3, Radius = 3 });
                Sim.SpawnDesign(d, cx, cy, new SpawnOptions { Matter = MatterSource.Local, Energy = EnergySource.Local, Count = 2, Radius = 3 });
            }
        }
        if (f == 110) Sim.Do(w => GD.Print($"uitest: tick {w.Tick}, designed bodies {w.Agents.Count(a => a.Designed && !a.Dead)}, lineages {w.DesignedLineages.Count}, FaceWork {P.FaceWork}"));
        if (f == 112) Sim.Do(w =>
        {
            var a = w.Agents.FirstOrDefault(a => a.Designed && !a.Dead);
            if (a != null) Ui.Post(() => { View.Selected = a; View.LookAt(a); View.ZoomAt(30); });
        });
        if (f == 130) Ui.Saves.QuickLoad();
        if (f == 200) Sim.Do(w => GD.Print($"uitest: after load tick {w.Tick}, designed bodies {w.Agents.Count(a => a.Designed && !a.Dead)}, FaceWork {P.FaceWork}"));
        if (f == 230 && shotPath == null) { Sim.Stop(); GetTree().Quit(); }
    }
}
