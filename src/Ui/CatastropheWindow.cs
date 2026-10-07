using System;
using Godot;

namespace Primordium;

// F11: catastrophes as the player's tool (ROADMAP 9.4). Each one runs a mechanism the world already has
// (World.ClimateCycles: the glaciation, a mega-eruption with its ash, the flare path of World.Sky, a
// blocking high over a region, matter poured by the hand) — with matter, water and energy booked. A press
// arms the button, a second press within a few seconds sets it off (SimRunner.Catastrophe, between
// ticks); the chronicle (Player) and the law log record it, so the trajectory stays reproducible.
public partial class CatastropheWindow : UiWindow
{
    SpinBox x, y, r, iceDays, floodK, volcBlocks, volcAsh, flarePower, flareDays, droughtDays, droughtDT, poisonN;
    CheckBox volcHere;
    Label status, poisonNote;
    ConfirmButton poisonGo;
    double refreshIn;

    public CatastropheWindow() : base("catastrophes", "Катастрофы", new Vector2(720, 520))
    {
        MinSize = new Vector2(600, 440);
        Body.AddChild(UiKit.Text("Те же законы, что и в мире, запущенные вручную: вещество, вода и энергия учитываются. Кнопка — дважды (подтверждение). " +
                                 "Катастрофа записывается в хронику и в историю законов: мир из сохранения или из seed с этой историей идёт так же.", 12, UiKit.Dim, null, true));

        x = UiKit.Spin(0, World.W - 1, 1, World.W / 2, 80);
        y = UiKit.Spin(0, World.H - 1, 1, World.H / 2, 80);
        r = UiKit.Spin(2, 120, 1, 20, 70);
        var centre = UiKit.Button("центр экрана", () => TakeCentre(false), "взять клетку в центре экрана");
        Body.AddChild(UiKit.Row(6, UiKit.Text("Место (засуха, отравление, вулкан):", 13, UiKit.Dim), UiKit.Text("x", 13), x, UiKit.Text("y", 13), y, UiKit.Text("радиус", 13), r, centre));
        Body.AddChild(new HSeparator());

        iceDays = UiKit.Spin(0.5, 200, 0.5, 6);
        Line("Ледниковье", "ледники растут в обоих полушариях N суток (как при холодном лете на 65°), потом отступают; снег и лёд — из той же воды",
            () => new Catastrophe { Kind = CatastropheKind.IceAge, Days = (float)iceDays.Value }, UiKit.Text("суток", 12, UiKit.Dim), iceDays);

        floodK = UiKit.Spin(0.1, 20, 0.1, 1);
        Line("Потоп", "море и озёра поднимаются на K блоков и растекаются по низинам; вода приходит извне и учитывается в водном балансе",
            () => new Catastrophe { Kind = CatastropheKind.Flood, Amount = (float)floodK.Value }, UiKit.Text("блоков", 12, UiKit.Dim), floodK);

        volcBlocks = UiKit.Spin(0, 2000, 10, P.MegaEruptionBlocks, 80);
        volcAsh = UiKit.Spin(0.05, 5, 0.05, P.MegaAsh, 70);
        volcHere = UiKit.Check("в месте", false, null, "в выбранном месте; иначе — у одного из вулканов");
        Line("Вулканическая зима", "мегаизвержение: блоки из недр (как выброс вулкана, учитываются) и пепел в стратосфере — расходится по планете, гасит свет и холодит, оседает за AshTau суток",
            () => new Catastrophe { Kind = CatastropheKind.VolcanicWinter, X = volcHere.ButtonPressed ? (int)x.Value : -1, Y = volcHere.ButtonPressed ? (int)y.Value : -1, Amount = (float)volcBlocks.Value, R = (float)volcAsh.Value },
            UiKit.Text("блоков", 12, UiKit.Dim), volcBlocks, UiKit.Text("пепел", 12, UiKit.Dim), volcAsh, volcHere);

        flarePower = UiKit.Spin(1, 500, 1, 20, 70);
        flareDays = UiKit.Spin(0.05, 10, 0.05, 0.25, 70);
        Line("Сильная вспышка", "солнечная вспышка такой мощности: доза на освещённой стороне — мутации, износ белков, урон и нагрев, как у вспышек солнца",
            () => new Catastrophe { Kind = CatastropheKind.SolarFlare, Amount = (float)flarePower.Value, Days = (float)flareDays.Value },
            UiKit.Text("мощность", 12, UiKit.Dim), flarePower, UiKit.Text("суток", 12, UiKit.Dim), flareDays);

        droughtDays = UiKit.Spin(0.5, 200, 0.5, 5, 70);
        droughtDT = UiKit.Spin(0, 40, 0.5, 8, 70);
        Line("Засуха", "над местом встаёт антициклон: ни облаков, ни дождя (влага выпадает в других местах), теплее — озёра и почва сохнут",
            () => new Catastrophe { Kind = CatastropheKind.Drought, X = (int)x.Value, Y = (int)y.Value, R = (float)r.Value, Days = (float)droughtDays.Value, Amount = (float)droughtDT.Value },
            UiKit.Text("суток", 12, UiKit.Dim), droughtDays, UiKit.Text("°C", 12, UiKit.Dim), droughtDT);

        poisonN = UiKit.Spin(1, 5000, 1, 30, 80);
        poisonGo = Line("Отравление", "рука рассыпает ядовитую молекулу этого мира (она портит белки тех, кто её впитал); вещество извне, учитывается",
            () => new Catastrophe { Kind = CatastropheKind.Poison, X = (int)x.Value, Y = (int)y.Value, R = Math.Min(60, (float)r.Value), Amount = (float)poisonN.Value },
            UiKit.Text("молекул в центре", 12, UiKit.Dim), poisonN);
        poisonNote = UiKit.Text("", 12, UiKit.Dim);
        Body.AddChild(poisonNote);

        Body.AddChild(new HSeparator());
        status = UiKit.Text("", 12, UiKit.Fg, null, true);
        Body.AddChild(status);
    }

    ConfirmButton Line(string name, string help, Func<Catastrophe> make, params Control[] inputs)
    {
        var go = new ConfirmButton("Вызвать", () => Fire(make()), help) { Ask = "точно? ещё раз" };
        var title = UiKit.Text(name, 13, UiKit.Fg, UiKit.Bold);
        title.CustomMinimumSize = new Vector2(150, 0);
        var row = UiKit.Row(6, title);
        foreach (var c in inputs) row.AddChild(c);
        row.AddChild(UiKit.Spacer());
        row.AddChild(go);
        Body.AddChild(row);
        var h = UiKit.Text(help, 11, UiKit.Dim, null, true);
        Body.AddChild(h);
        return go;
    }

    void Fire(Catastrophe c)
    {
        Main.Sim.Catastrophe(c);
        refreshIn = 0.3;
    }

    void TakeCentre(bool quiet)
    {
        var vs = GetViewportRect().Size;
        int cell = Main.View.PickCell(new Vector2((vs.X - Hud.PanelW) / 2, vs.Y / 2));
        if (cell < 0) { if (!quiet) Ui.Toast("в центре экрана нет поверхности", true); return; }
        x.Value = cell % World.W; y.Value = cell / World.W;
    }

    protected override void OnOpen() { TakeCentre(true); Refresh(); }

    void Refresh()
    {
        var w = Main.World;
        if (w == null) return;
        bool poison = w.Chem.Toxic.Length > 0;
        poisonGo.Disabled = !poison;
        poisonNote.Text = poison ? $"яд этого мира: {w.Chem.Name[w.Chem.Toxic[0]]} ({w.Chem.Formula(w.Chem.Toxic[0])})" : "в химии этого мира нет ядовитых молекул — отравить нечем";
        status.Text = w.EpochLine(true) + $"\nледниковий {w.IceAges}, вулканических зим {w.VolcanicWinters}, мегаизвержений {w.MegaEruptions}, катастроф игрока {w.CatastropheCount}";
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        if ((refreshIn -= delta) <= 0) { refreshIn = 0.5; Refresh(); }
    }
}
