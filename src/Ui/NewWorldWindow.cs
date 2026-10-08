using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Primordium;

// F4: a new world from settings — seed, first population, outside influences, a law preset and, under
// "More", every law (including those read only when a world is made).
public partial class NewWorldWindow : UiWindow
{
    LineEdit seed;
    SpinBox pop;
    CheckBox abio, strikes, flares, eclipses, cycles;
    OptionButton preset;
    Button more;
    ParamEditor editor;
    readonly DraftParams draft = new();
    List<(string name, string path)> presetList = new();
    Label note;

    public NewWorldWindow() : base("newworld", Loc.T("New world", "Новый мир"), new Vector2(600, 270))
    {
        MinSize = new Vector2(480, 240);
        seed = UiKit.Edit("", Loc.T("number", "число"), 120);
        seed.TextSubmitted += _ => Create();
        var dice = UiKit.Button(Loc.T("random", "случайный"), () => seed.Text = NewSeed().ToString(), Loc.T("a new random seed", "новый случайный seed"));
        Body.AddChild(UiKit.Row(8, Label("Seed"), seed, dice,
            UiKit.Text(Loc.T("sets the elements, molecules, terrain and climate", "задаёт элементы, молекулы, рельеф и климат"), 12, UiKit.Dim, null, true)));

        pop = UiKit.Spin(0, 50000, 50, P.InitialPop, 110);
        pop.GetLineEdit().FocusMode = FocusModeEnum.Click;
        Body.AddChild(UiKit.Row(8, Label(Loc.T("Population", "Население")), pop,
            UiKit.Text(Loc.T("random genomes at the start; 0 — an empty world, you start life yourself", "случайных геномов в начале; 0 — пустой мир, жизнь запускаете сами"), 12, UiKit.Dim, null, true)));

        abio = UiKit.Check(Loc.T("Abiogenesis", "Самозарождение"), false, null, Loc.T("now and then a new random creature from local matter (A — toggle while running)", "время от времени новое случайное существо из местного вещества (A — переключить на ходу)"));
        strikes = UiKit.Check(Loc.T("Orbital strikes", "Удары с орбиты"), false, null, Loc.T("mutagenic strikes from space now and then (⇧X — toggle while running)", "мутагенные удары из космоса время от времени (⇧X — переключить на ходу)"));
        // Laws of the sky (World.Sky): the same switches as in "More" (Flares, Eclipses).
        flares = UiKit.Check(Loc.T("Solar flares", "Солнечные вспышки"), true, on => SetLaw("Flares", on), Loc.T("irradiation of the lit side: mutations, protein wear, damage; law Flares", "облучение освещённой стороны: мутации, износ белков, урон; закон Flares"));
        eclipses = UiKit.Check(Loc.T("Eclipses", "Затмения"), true, on => SetLaw("Eclipses", on), Loc.T("the moon's shadow crosses the day side; law Eclipses", "тень спутника проходит по дневной стороне; закон Eclipses"));
        cycles = UiKit.Check(Loc.T("Climate cycles", "Климатические циклы"), true, on => SetLaw("ClimateCycles", on), Loc.T("obliquity, eccentricity and the sun vary: ice ages and volcanic winters; law ClimateCycles", "наклон оси, эксцентриситет и солнце меняются: ледниковья и вулканические зимы; закон ClimateCycles"));
        Body.AddChild(UiKit.Row(18, UiKit.Spacer(84, 0, false), abio, strikes, flares, eclipses, cycles));

        preset = UiKit.Options();
        preset.CustomMinimumSize = new Vector2(220, 0);
        preset.ItemSelected += _ => FillDraft();
        more = UiKit.Button(Loc.T("More ▸", "Дополнительно ▸"), ToggleMore, Loc.T("all the laws of the new world", "все законы нового мира"));
        more.ToggleMode = true;
        Body.AddChild(UiKit.Row(8, Label(Loc.T("Laws", "Законы")), preset, UiKit.Spacer(), more));

        editor = new ParamEditor(draft) { Visible = false, LockHint = "" };
        Body.AddChild(editor);

        note = UiKit.Text("", 12, UiKit.Dim, null, true);
        var create = UiKit.Button(Loc.T("Create world", "Создать мир"), Create, Loc.T("replace the current world with a new one (anything unsaved is lost)", "заменить текущий мир новым (несохранённое пропадёт)"));
        create.AddThemeColorOverride("font_color", UiKit.Acc);
        Body.AddChild(UiKit.Spacer(0, 0, false));
        Body.AddChild(UiKit.Row(8, note, create));
    }

    static Label Label(string s)
    {
        var l = UiKit.Text(s, 13, UiKit.Dim);
        l.CustomMinimumSize = new Vector2(84, 0);
        return l;
    }

    static int NewSeed() => (int)(GD.Randi() % 100000);

    protected override void OnOpen()
    {
        var w = Main.World;
        seed.Text = NewSeed().ToString();
        pop.Value = Main.InitialPop >= 0 ? Main.InitialPop : P.InitialPop;
        abio.ButtonPressed = w?.Abiogenesis ?? false;
        strikes.ButtonPressed = w?.AutoStrikes ?? false;
        presetList = Presets.List();
        preset.Clear();
        preset.AddItem(Loc.T("current laws", "текущие законы"));
        preset.AddItem(Loc.T("defaults", "по умолчанию"));
        foreach (var (name, _) in presetList) preset.AddItem(Loc.T("preset: ", "набор: ") + name);
        preset.Selected = 0;
        FillDraft();
        note.Text = Loc.T($"now: seed {w?.Seed}, tick {w?.Tick:N0}. R — new seed, ⇧R — the same one again (with the current laws).",
            $"сейчас: seed {w?.Seed}, тик {w?.Tick:N0}. R — новый seed, ⇧R — тот же заново (с текущими законами).");
    }

    void FillDraft()
    {
        int k = preset.Selected;
        if (k <= 0)
        {
            var vals = SimRunner.ParamValues;
            draft.Fill(ParamRegistry.All.ToDictionary(p => p.Name, p => vals[p.Index]));
        }
        else if (k == 1) draft.Fill(null);
        else
        {
            try { draft.Fill(ParamRegistry.LoadPreset(presetList[k - 2].path).Values); }
            catch (Exception e) { Ui.Toast(Loc.T("cannot read the preset: ", "не прочитать набор: ") + e.Message, true); draft.Fill(null); }
        }
        editor.Refresh(true);
        flares.SetPressedNoSignal(draft.Values.TryGetValue("Flares", out var f) && f != 0);
        eclipses.SetPressedNoSignal(draft.Values.TryGetValue("Eclipses", out var ec) && ec != 0);
        cycles.SetPressedNoSignal(draft.Values.TryGetValue("ClimateCycles", out var cy) && cy != 0);
    }

    void SetLaw(string name, bool on)
    {
        draft.Set(ParamRegistry.Find(name), on ? 1 : 0);
        editor.Refresh(true);
    }

    public override void ShowView(string view) { if (view == "more") { more.ButtonPressed = true; ToggleMore(); } }

    void ToggleMore()
    {
        editor.Visible = more.ButtonPressed;
        more.Text = more.ButtonPressed ? Loc.T("More ▾", "Дополнительно ▾") : Loc.T("More ▸", "Дополнительно ▸");
        float h = more.ButtonPressed ? 640 : 270;
        Size = new Vector2(more.ButtonPressed ? Math.Max(Size.X, 780) : Size.X, h);
        MinSize = new Vector2(480, more.ButtonPressed ? 420 : 240);
        KeepInside();
    }

    void Create()
    {
        double s = UiKit.ParseNumber(seed.Text, out bool ok);
        if (!ok || s < int.MinValue || s > int.MaxValue) { Ui.Toast(Loc.T("seed must be an integer", "seed — целое число"), true); return; }
        var settings = new WorldSettings
        {
            Seed = (int)s,
            InitialPop = (int)pop.Value,
            Abiogenesis = abio.ButtonPressed,
            Strikes = strikes.ButtonPressed,
            Params = new Dictionary<string, double>(draft.Values),
            PresetName = preset.GetItemText(preset.Selected),
        };
        Main.CreateWorld(settings);
        Close();
    }
}
