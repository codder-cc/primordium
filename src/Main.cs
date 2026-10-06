using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;

namespace Primordium;

public partial class Main : Node
{
    public World World { get; private set; }
    public View3D View { get; private set; }
    public int Tpf = 8;
    public bool Paused;
    public double Tps, SimMs, ViewMs;   // smoothed: ms per tick of simulation, ms per frame of drawing
    public Census Census = new();
    public readonly List<int[]> Hist = new();                 // every 100 ticks: all, plants, eaters, miners, hunters
    public readonly long[] EvRate = new long[(int)EvKind.Count];
    public List<(long lin, int n, int gen, Agent rep)> Lineages = new();
    public int KinCount;
    public bool ShowRecords;
    public long FastTo = -1, FastFrom;   // fast-forward: simulate without drawing until this tick
    public bool FastForward => FastTo > (World?.Tick ?? 0);
    public readonly List<(string name, Agent a, string value)> Records = new();
    public readonly List<float> SelHist = new();   // energy of the selected agent, every 5 ticks
    Agent histOf;
    public World.Climate Climate;
    int initialPop = P.InitialPop;
    bool abiogenesis = true;

    Hud hud;
    PanelContainer fastPanel;
    LineEdit fastEdit;
    Label fastNote;
    readonly long[] evPrev = new long[(int)EvKind.Count];
    int frame, shotFrames = -1;
    string shotPath;
    bool lDown, rDown, mDown, dragged;
    Vector2 pressPos;

    // The hand: a brush that pours matter (a new random kind every stroke) or water, kills or digs.
    public static readonly string[] ToolNames = { "", "насыпать", "вода", "убить", "копнуть" };
    public int Tool, PourSpecies = -1;
    public float BrushR = 3;
    bool painting;
    double paintWait;
    int startTool, paintDabs;   // for screenshots: a brush and a number of dabs at the centre of the screen

    public override void _Ready()
    {
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized);
        var args = OS.GetCmdlineUserArgs();
        if (Array.IndexOf(args, "--noabio") >= 0) abiogenesis = false;
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
        }

        View = new View3D();
        AddChild(View);
        var layer = new CanvasLayer();
        AddChild(layer);
        hud = new Hud { Main = this };
        layer.AddChild(hud);
        BuildFastForm(layer);

        NewWorld(seed);
        for (int i = 0; i < warm; i++) Tick();
        Census = World.TakeCensus();
        ComputeLineages();
        View.Slice = slice;
        if (zoom > 0) View.ZoomAt(zoom);
        if (Array.IndexOf(args, "--strike") >= 0)
        {
            // For screenshots: strike the busiest place right away.
            var a = Lineages.Count > 0 ? Lineages[0].rep : null;
            if (a != null) World.StrikeAt(a.X, a.Y, 9);
        }
        if (focus > 0 && Lineages.Count > 0)
        {
            // Select an agent of the biggest lineage (or the body covering most cells) and look at it
            // from close up (for screenshots).
            View.Selected = Lineages[0].rep;
            if (Array.IndexOf(args, "--oldest") >= 0)
            {
                ComputeRecords();
                if (Records.Count > 0) View.Selected = Records[0].a;
            }
            if (Array.IndexOf(args, "--big") >= 0)
            {
                ComputeRecords();
                foreach (var r in Records) if (r.name.StartsWith("занимает")) View.Selected = r.a;
            }
            View.LookAt(View.Selected);
            View.ZoomAt(focus);
        }
        if (Array.IndexOf(args, "--fastform") >= 0) OpenFastForm();   // for screenshots of the form
        if (startTool > 0) SetTool(startTool);
        if (shotPath != null) shotFrames = 60;
    }

    void NewWorld(int seed)
    {
        World = new World(seed, initialPop, abiogenesis);
        painting = false;
        if (Tool == 1) PourSpecies = World.RandomPourable();
        View.SetWorld(World);
        Hist.Clear();
        Array.Clear(EvRate);
        Array.Clear(evPrev);
        Lineages.Clear();
        Census = new Census();
    }

    void Tick()
    {
        World.Step();
        var s = View.Selected;
        if (s != histOf) { histOf = s; SelHist.Clear(); }
        if (s is { Dead: false } && World.Tick % 5 == 0)
        {
            SelHist.Add(s.Energy);
            if (SelHist.Count > 300) SelHist.RemoveAt(0);
        }
        if (World.Tick % 100 == 0)
        {
            var c = World.TakeCensus();
            Hist.Add(new[] { c.Pop, c.Plants, c.Eaters, c.Miners, c.Hunters });
            if (Hist.Count > 400) Hist.RemoveAt(0);
        }
        if (World.Tick % 1000 == 0)
        {
            for (int k = 0; k < EvRate.Length; k++) EvRate[k] = World.Ev[k] - evPrev[k];
            Array.Copy(World.Ev, evPrev, evPrev.Length);
        }
    }

    public override void _Process(double delta)
    {
        if (World == null) return;
        int done = 0;
        var sw = Stopwatch.StartNew();
        if (FastForward)
        {
            // Nothing is drawn: spend most of the frame simulating.
            while (World.Tick < FastTo && sw.Elapsed.TotalMilliseconds < 250) { Tick(); done++; }
            Tps = Tps * 0.7 + done / Math.Max(1e-3, sw.Elapsed.TotalSeconds) * 0.3;
            if (!FastForward)
            {
                FastTo = -1;
                Census = World.TakeCensus();
                Climate = World.TakeClimate();
                ComputeLineages();
                ComputeRecords();
            }
            frame++;
            hud.QueueRedraw();
            return;
        }
        if (!Paused)
        {
            while (done < Tpf && sw.Elapsed.TotalMilliseconds < 28) { Tick(); done++; }
            if (done > 0) SimMs = SimMs * 0.9 + sw.Elapsed.TotalMilliseconds / done * 0.1;
        }
        Tps = Tps * 0.92 + (Paused ? 0 : done / Math.Max(1e-3, delta)) * 0.08;
        if (frame++ % 20 == 0)
        {
            Census = World.TakeCensus();
            Climate = World.TakeClimate();
            ComputeLineages();
            var sel = View.Selected;
            KinCount = sel == null ? 0 : World.Agents.Count(a => !a.Dead && a != sel && Looks.Kin(a, sel) > 0);
            ComputeRecords();
        }
        var mouse = GetViewport().GetMousePosition();
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
        }
        View.Speed = Paused ? 1 : Tpf;

        float sp = (float)delta * 900;
        var pan = Vector2.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) pan.Y += sp;
        if (Input.IsPhysicalKeyPressed(Key.S)) pan.Y -= sp;
        if (Input.IsPhysicalKeyPressed(Key.A)) pan.X += sp;
        if (Input.IsPhysicalKeyPressed(Key.D)) pan.X -= sp;
        if (pan != Vector2.Zero && !fastPanel.Visible) View.Pan(pan);
        if (fastPanel.Visible)
        {
            var vs = GetViewport().GetVisibleRect().Size;
            fastPanel.Position = new Vector2((vs.X - Hud.PanelW - fastPanel.Size.X) / 2, (vs.Y - fastPanel.Size.Y) / 2);
        }

        sw.Restart();
        View.Refresh((float)delta);
        hud.QueueRedraw();
        ViewMs = ViewMs * 0.9 + sw.Elapsed.TotalMilliseconds * 0.1;

        if (shotFrames == 8 && Array.IndexOf(OS.GetCmdlineUserArgs(), "--big") >= 0)
        {
            ComputeRecords();
            foreach (var r in Records) if (r.name.StartsWith("занимает")) { View.Selected = r.a; View.LookAt(r.a); View.ZoomAt(22); }
        }
        if (shotFrames == 8 && Array.IndexOf(OS.GetCmdlineUserArgs(), "--oldest") >= 0)
        {
            ComputeRecords();
            if (Records.Count > 0) { View.Selected = Records[0].a; View.LookAt(Records[0].a); }
        }
        if (shotFrames == 30 && paintDabs > 0)
        {
            int c = View.PickCell(GetViewport().GetVisibleRect().Size / 2);
            for (int k = 0; k < paintDabs && c >= 0 && Tool > 0; k++) Paint(c);
        }
        if (shotFrames > 0 && --shotFrames == 0)
        {
            GetViewport().GetTexture().GetImage().SavePng(shotPath);
            GetTree().Quit();
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
        var title = new Label { Text = "Промотка без отрисовки" };
        title.AddThemeFontSizeOverride("font_size", 17);
        var hint = new Label { Text = "Сколько суток промотать? Например 25.\nИли «до 200» — до 200-х суток.\nEnter — начать (пусто — 10 суток), Esc — закрыть." };
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
        fastNote.Text = $"сейчас сутки {World.Day + 1}";
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
        bool until = text.StartsWith("до");
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (!long.TryParse(digits, out long n) || n <= 0 || n > 100000)
        {
            fastNote.Text = "нужно число суток, например 25 или «до 200»";
            fastNote.AddThemeColorOverride("font_color", new Color(1f, 0.55f, 0.5f));
            return;
        }
        long target = until ? (n - 1) * P.DayLen : World.Tick + n * P.DayLen;
        if (target <= World.Tick)
        {
            fastNote.Text = $"сутки {n} уже прошли (сейчас {World.Day + 1})";
            fastNote.AddThemeColorOverride("font_color", new Color(1f, 0.55f, 0.5f));
            return;
        }
        CloseFastForm();
        StartFast(target);
    }

    void StartFast(long target)
    {
        FastFrom = World.Tick;
        FastTo = target;
    }

    // The most remarkable living bodies right now.
    void ComputeRecords()
    {
        Records.Clear();
        Agent Max(Func<Agent, float> f)
        {
            Agent best = null;
            float bv = float.MinValue;
            foreach (var a in World.Agents)
            {
                if (a.Dead) continue;
                float v = f(a);
                if (v > bv) { bv = v; best = a; }
            }
            return best;
        }
        static float Protected(Agent a)
        {
            int n = 0;
            foreach (var b in a.Prot) if (b > 20) n++;
            return n / (float)a.Prot.Length;
        }
        void Add(string name, Func<Agent, float> f, Func<Agent, string> show, bool needPositive = false)
        {
            var a = Max(f);
            if (a != null && (!needPositive || f(a) > 0)) Records.Add((name, a, show(a)));
        }
        Add("старейший", a => a.Age, a => $"возраст {a.Age:N0}");
        Add("больше всех детей", a => a.NChildren, a => $"детей {a.NChildren}", true);
        Add("самое глубокое поколение", a => a.Gen, a => $"поколение {a.Gen}");
        Add("самый крупный", a => a.Mass, a => $"масса {a.Mass:0} · {a.Cells} кл.");
        Add("занимает больше всех клеток", a => a.Cells + a.Mass * 1e-4f, a => $"{a.Cells} клеток · масса {a.Mass:0}");
        Add("самый сытый", a => a.Energy, a => $"энергия {a.Energy:0}");
        Add("больше всех белков", a => a.EnzymeTotal, a => $"белков {a.EnzymeTotal:0.0}", true);
        Add("самый закреплённый геном", Protected, a => $"закреплено {Protected(a):P0}", true);
        Add("самый длинный геном", a => a.G.Length, a => $"{a.G.Length} байт");
        Add("главный убийца", a => a.NKills, a => $"убил {a.NKills}", true);
        Add("больше всех атак", a => a.NAttacks, a => $"атак {a.NAttacks}", true);
        Add("распространитель генов", a => a.NInjects, a => $"вставок {a.NInjects}", true);
        Add("больше всех спаривался", a => a.NMates, a => $"спариваний {a.NMates}", true);
        Add("в самой тесной клетке", a => World.Count[a.Y * World.W + a.X], a => $"соседей {World.Count[a.Y * World.W + a.X] - 1}");
        Add("самое горячее тело", a => a.Tb, a => $"{a.Tb:+0;-0} °C");
        Add("самое холодное тело", a => -a.Tb, a => $"{a.Tb:+0;-0} °C");
    }

    public void Focus(Agent a)
    {
        View.Selected = a;
        View.LookAt(a);
    }

    void ComputeLineages()
    {
        Lineages = World.Agents.Where(a => !a.Dead).GroupBy(a => a.Lineage)
            .Select(g => (g.Key, g.Count(), g.Max(a => a.Gen), g.OrderByDescending(a => a.Gen).First()))
            .OrderByDescending(t => t.Item2).Take(5).ToList();
    }

    bool OverPanel(Vector2 p) => p.X > GetViewport().GetVisibleRect().Size.X - Hud.PanelW;

    // One dab of the brush at a column (15 a second while the button is held).
    void Paint(int c)
    {
        int x = c % World.W, y = c / World.W;
        switch (Tool)
        {
            case 1: World.Pour(x, y, BrushR, PourSpecies, 0.25f); break;   // a quarter of a block at the centre per dab
            case 2: World.PourWater(x, y, BrushR, 0.15f); break;
            case 3: World.KillIn(x, y, BrushR); break;
            case 4: World.DigOut(x, y, BrushR, 0.25f); break;
        }
    }

    void SetTool(int t)
    {
        Tool = Tool == t ? 0 : t;   // the same key again puts the brush away
        painting = false;
        if (Tool == 1) PourSpecies = World.RandomPourable();
    }

    public override void _UnhandledInput(InputEvent e)
    {
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
        switch (k.PhysicalKeycode)
        {
            case Key.Space: Paused = !Paused; break;
            case Key.T:
                if (FastForward) FastTo = -1;
                else if (k.ShiftPressed) StartFast(World.Tick + 10L * P.DayLen);
                else OpenFastForm();
                break;
            case Key.Period: if (Paused) Tick(); break;
            case Key.Equal: case Key.KpAdd: Tpf = Math.Min(256, Tpf * 2); break;
            case Key.Minus: case Key.KpSubtract: Tpf = Math.Max(1, Tpf / 2); break;
            case Key.Q: v.RotateStep(-1); break;
            case Key.E: v.RotateStep(1); break;
            case Key.G: v.ResetCamera(); break;
            case Key.F:
                v.Follow = !v.Follow && v.Selected != null;
                if (v.Follow) v.LookAt(v.Selected);
                break;
            case Key.K: v.KinFocus = !v.KinFocus; break;
            case Key.B: ShowRecords = !ShowRecords; break;
            case Key.O:
                ComputeRecords();
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
            case Key.Key0: case Key.Kp0: Tool = 0; painting = false; break;
            case Key.N:
                {
                    // A group of newcomers under the cursor (or somewhere at random).
                    int c = View.PickCell(GetViewport().GetMousePosition());
                    if (c < 0) c = (int)(GD.Randi() % World.N);
                    World.SpawnGroup(c % World.W, c / World.W, 40);
                    break;
                }
            case Key.A: World.Abiogenesis = abiogenesis = !World.Abiogenesis; break;
            case Key.X:
                if (k.ShiftPressed) World.AutoStrikes = !World.AutoStrikes;
                else
                {
                    int c = View.PickCell(GetViewport().GetMousePosition());
                    if (c >= 0) World.StrikeAt(c % World.W, c / World.W, 8);
                }
                break;
            case Key.R: NewWorld(k.ShiftPressed ? World.Seed : (int)(Time.GetTicksMsec() % 100000)); break;
            case Key.Escape:
                if (FastForward) { FastTo = -1; break; }
                if (Tool > 0) { Tool = 0; painting = false; break; }
                v.Selected = null; v.Follow = false;
                break;
            case Key.P:
                var path = OS.GetSystemDir(OS.SystemDir.Desktop) + $"/primordium-{World.Seed}-{World.Tick}.png";
                GetViewport().GetTexture().GetImage().SavePng(path);
                GD.Print("снимок: " + path);
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
                    var hit = hud.HitTest(mb.Position);
                    if (hit != null) { Focus(hit); return; }
                    if (OverPanel(mb.Position)) return;
                    if (Tool > 0)
                    {
                        // A stroke: pouring takes a new random kind of matter each time.
                        if (Tool == 1) PourSpecies = World.RandomPourable();
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
}
