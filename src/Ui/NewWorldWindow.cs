using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Primordium;

// F4: a new world from settings — seed, first population, outside influences, a law preset and, under
// "Дополнительно", every law (including those read only when a world is made).
public partial class NewWorldWindow : UiWindow
{
    LineEdit seed;
    SpinBox pop;
    CheckBox abio, strikes, flares, eclipses;
    OptionButton preset;
    Button more;
    ParamEditor editor;
    readonly DraftParams draft = new();
    List<(string name, string path)> presetList = new();
    Label note;

    public NewWorldWindow() : base("newworld", "Новый мир", new Vector2(600, 270))
    {
        MinSize = new Vector2(480, 240);
        seed = UiKit.Edit("", "число", 120);
        seed.TextSubmitted += _ => Create();
        var dice = UiKit.Button("случайный", () => seed.Text = NewSeed().ToString(), "новый случайный seed");
        Body.AddChild(UiKit.Row(8, Label("Seed"), seed, dice,
            UiKit.Text("задаёт элементы, молекулы, рельеф и климат", 12, UiKit.Dim, null, true)));

        pop = UiKit.Spin(0, 50000, 50, P.InitialPop, 110);
        pop.GetLineEdit().FocusMode = FocusModeEnum.Click;
        Body.AddChild(UiKit.Row(8, Label("Население"), pop,
            UiKit.Text("случайных геномов в начале; 0 — пустой мир, жизнь запускаете сами", 12, UiKit.Dim, null, true)));

        abio = UiKit.Check("Самозарождение", false, null, "время от времени новое случайное существо из местного вещества (A — переключить на ходу)");
        strikes = UiKit.Check("Удары с орбиты", false, null, "мутагенные удары из космоса время от времени (⇧X — переключить на ходу)");
        // Laws of the sky (World.Sky): the same switches as in "Дополнительно" (Flares, Eclipses).
        flares = UiKit.Check("Солнечные вспышки", true, on => SetLaw("Flares", on), "облучение освещённой стороны: мутации, износ белков, урон; закон Flares");
        eclipses = UiKit.Check("Затмения", true, on => SetLaw("Eclipses", on), "тень спутника проходит по дневной стороне; закон Eclipses");
        Body.AddChild(UiKit.Row(18, UiKit.Spacer(84, 0, false), abio, strikes, flares, eclipses));

        preset = UiKit.Options();
        preset.CustomMinimumSize = new Vector2(220, 0);
        preset.ItemSelected += _ => FillDraft();
        more = UiKit.Button("Дополнительно ▸", ToggleMore, "все законы нового мира");
        more.ToggleMode = true;
        Body.AddChild(UiKit.Row(8, Label("Законы"), preset, UiKit.Spacer(), more));

        editor = new ParamEditor(draft) { Visible = false, LockHint = "" };
        Body.AddChild(editor);

        note = UiKit.Text("", 12, UiKit.Dim, null, true);
        var create = UiKit.Button("Создать мир", Create, "заменить текущий мир новым (несохранённое пропадёт)");
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
        preset.AddItem("текущие законы");
        preset.AddItem("по умолчанию");
        foreach (var (name, _) in presetList) preset.AddItem("набор: " + name);
        preset.Selected = 0;
        FillDraft();
        note.Text = $"сейчас: seed {w?.Seed}, тик {w?.Tick:N0}. R — новый seed, ⇧R — тот же заново (с текущими законами).";
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
            catch (Exception e) { Ui.Toast("не прочитать набор: " + e.Message, true); draft.Fill(null); }
        }
        editor.Refresh(true);
        flares.SetPressedNoSignal(draft.Values.TryGetValue("Flares", out var f) && f != 0);
        eclipses.SetPressedNoSignal(draft.Values.TryGetValue("Eclipses", out var ec) && ec != 0);
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
        more.Text = more.ButtonPressed ? "Дополнительно ▾" : "Дополнительно ▸";
        float h = more.ButtonPressed ? 640 : 270;
        Size = new Vector2(more.ButtonPressed ? Math.Max(Size.X, 780) : Size.X, h);
        MinSize = new Vector2(480, more.ButtonPressed ? 420 : 240);
        KeepInside();
    }

    void Create()
    {
        double s = UiKit.ParseNumber(seed.Text, out bool ok);
        if (!ok || s < int.MinValue || s > int.MaxValue) { Ui.Toast("seed — целое число", true); return; }
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
