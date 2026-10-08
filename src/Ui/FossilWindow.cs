using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Primordium;

// One fossil (World.Chronicle): who it was, what its body was made of, its proteins, its genome as
// text (GenomeAsm), its biography and its tracked ancestors. "Revive" opens it in the creature
// editor as a design (Fossil.ToDesign), to be planted with brush 5.
public partial class FossilWindow : UiWindow
{
    Fossil fossil;
    Label head, info, body, proteins;
    Button parentButton, aliveButton;
    TextEdit genome;
    ItemList bio, ancestry;
    TabContainer tabs;
    readonly List<long> ancestryIds = new();

    public FossilWindow() : base("fossil", Loc.T("Fossil", "Окаменелость"), new Vector2(760, 640))
    {
        MinSize = new Vector2(560, 420);
        head = UiKit.Title("", 15);
        Body.AddChild(head);
        info = UiKit.Text("", 12, UiKit.Dim, null, true);
        Body.AddChild(info);
        var revive = UiKit.Button(Loc.T("Revive", "Воскресить"), Revive,
            Loc.T("open in the creature editor as a design: genome, body makeup, looks; plant it with brush 5",
                  "открыть в конструкторе существ как дизайн: геном, состав тела, облик; посадить — кистью 5"));
        revive.AddThemeColorOverride("font_color", UiKit.Acc);
        parentButton = UiKit.Button(Loc.T("To parent", "К родителю"), ToParent, Loc.T("a living parent on the map, otherwise its fossil", "живой родитель — на карте, иначе его окаменелость"));
        aliveButton = UiKit.Button(Loc.T("Show on map", "Показать на карте"), ToAlive, Loc.T("this creature is still alive", "это существо ещё живо"));
        Body.AddChild(UiKit.Row(8, revive, parentButton, aliveButton, UiKit.Spacer(),
            UiKit.Button(Loc.T("Chronicle", "Хроника"), () => Ui.Chronicle.Open(), Loc.T("event feed and all fossils (F8)", "лента событий и все окаменелости (F8)"))));
        body = UiKit.Text("", 12, UiKit.Fg, null, true);
        Body.AddChild(body);
        proteins = UiKit.Text("", 12, UiKit.Dim, null, true);
        Body.AddChild(proteins);

        tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        genome = new TextEdit { Editable = false, SizeFlagsVertical = SizeFlags.ExpandFill, WrapMode = TextEdit.LineWrappingMode.None };
        genome.AddThemeFontOverride("font", UiKit.Mono);
        tabs.AddChild(genome);
        bio = new ItemList { FocusMode = FocusModeEnum.None, SizeFlagsVertical = SizeFlags.ExpandFill };
        bio.AddThemeFontSizeOverride("font_size", 12);
        tabs.AddChild(bio);
        ancestry = new ItemList { FocusMode = FocusModeEnum.None, SizeFlagsVertical = SizeFlags.ExpandFill };
        ancestry.AddThemeFontSizeOverride("font_size", 12);
        ancestry.ItemClicked += (i, _, button) => { if (button == (long)MouseButton.Left) Goto(ancestryIds[(int)i]); };
        tabs.AddChild(ancestry);
        tabs.SetTabTitle(0, Loc.T("Genome", "Геном"));
        tabs.SetTabTitle(1, Loc.T("Biography", "Биография"));
        tabs.SetTabTitle(2, Loc.T("Ancestry", "Родословная"));
        Body.AddChild(tabs);
    }

    public override void ShowView(string view) => tabs.CurrentTab = view == "bio" ? 1 : view == "ancestry" ? 2 : 0;

    protected override void OnOpen()
    {
        // --open fossil without a fossil: the most important one there is.
        if (fossil == null && Main.Sim?.Chronicle.Fossils is { Length: > 0 } all)
            Show(all.OrderByDescending(f => f.Importance).ThenByDescending(f => f.Bio.Length).First(), false);
    }

    public void Show(Fossil f, bool open = true)
    {
        fossil = f;
        var view = Main.Sim.Chronicle;
        var names = view.Molecules;
        string Mol(int s) => s < names.Length ? names[s] : $"#{s}";
        SetTitle(Loc.T($"Fossil #{f.AgentId}", $"Окаменелость #{f.AgentId}"));
        string design = Main.Sim.DesignedLineages.TryGetValue(f.Lineage, out var dn) ? Loc.T($" · design “{dn}”", $" · дизайн «{dn}»") : "";
        head.Text = Loc.T($"#{f.AgentId} · lineage #{f.Lineage} · generation {f.Gen}{design}", $"#{f.AgentId} · линия #{f.Lineage} · поколение {f.Gen}{design}");
        string life = f.DiedTick >= 0
            ? Loc.T($"lived from tick {f.BornTick} to {f.DiedTick} ({Chronicle.Day(f.BornTick)} – {f.DiedTick / P.DayLen + 1}), cause of death: {Chronicle.CauseName(f.Cause)}",
                    $"жило с тика {f.BornTick} по {f.DiedTick} ({Chronicle.Day(f.BornTick)} – {f.DiedTick / P.DayLen + 1}), причина смерти: {Chronicle.CauseName(f.Cause)}")
            : Loc.T($"taken alive at tick {f.BornTick + f.Age} (born at tick {f.BornTick})", $"снято живым на тике {f.BornTick + f.Age} (родилось на тике {f.BornTick})");
        var ev = f.EventSeq >= 0 ? view.Events.FirstOrDefault(e => e.Seq == f.EventSeq) : null;
        info.Text = Loc.T($"{life} · age {f.Age} · offspring {f.Children} · mass {f.Mass:0} · energy at birth {f.Energy:0}\n",
                          $"{life} · возраст {f.Age} · потомков {f.Children} · масса {f.Mass:0} · энергия при рождении {f.Energy:0}\n") +
                    Loc.T($"kept: {Chronicle.WhyText(f.Why)}", $"сохранено: {Chronicle.WhyText(f.Why)}") +
                    (ev != null ? Loc.T($" · event: {ev.Shown}", $" · событие: {ev.Shown}") : f.EventSeq >= 0 ? Loc.T($" · event no. {f.EventSeq}", $" · событие №{f.EventSeq}") : "") +
                    (f.ParentId > 0 ? Loc.T($" · parent #{f.ParentId}", $" · родитель #{f.ParentId}") : (f.Why & Chronicle.WhyFounder) != 0 ? "" : Loc.T(" · no parent", " · без родителя"));
        var mols = Enumerable.Range(0, Chemistry.S).Where(s => f.Body[s] > 0).OrderByDescending(s => f.Body[s]).Select(s => $"{Mol(s)} ×{f.Body[s]}");
        int total = f.Body.Sum();
        body.Text = total > 0 ? Loc.T($"Body ({total} molecules): ", $"Тело ({total} молекул): ") + string.Join(" · ", mols)
                              : Loc.T("Body: makeup not preserved (a fossil from an event's genome)", "Тело: состав не сохранился (окаменелость по геному из события)");
        proteins.Text = f.Proteins.Length == 0 ? Loc.T("No proteins.", "Белков не было.") : Loc.T("Proteins: ", "Белки: ") + string.Join(" · ", f.Proteins.Select(z =>
            $"{Genome.EnzymeKind[z.Kind]} " + z.Kind switch { Enzyme.Bind => $"{Mol(z.A)} + {Mol(z.B)}", Enzyme.Split => $"{Mol(z.A)} →", Enzyme.Photo => Loc.T("light + ", "свет + ") + Mol(z.A), _ => "" } +
            $" ×{z.Amount:0.0}, {z.Eff * 100:0}%, {z.Topt:+0;-0}°"));
        genome.Text = f.Genome != null ? Loc.T($"; {f.Genome.Length} bytes\n", $"; {f.Genome.Length} байт\n") + GenomeAsm.Disassemble(f.Genome) : Loc.T("; no genome", "; генома нет");
        bio.Clear();
        if (f.Bio.Length == 0) bio.AddItem(Loc.T("no biography was kept (the creature was not tracked)", "биография не велась (существо не было под наблюдением)"));
        foreach (var e in f.Bio) bio.AddItem($"{Chronicle.Day(e.Tick),-10} {Loc.T("tick", "тик")} {e.Tick,-8}  {Chronicle.BioText(e, Main.World?.Chem)}");
        FillAncestry(view);
        bool parentKnown = f.ParentId > 0 && (Main.FindAlive(f.ParentId) != null || view.FossilOf(f.ParentId) != null);
        parentButton.Disabled = !parentKnown;
        parentButton.TooltipText = f.ParentId <= 0 ? Loc.T("founder: no parent", "основатель: родителя нет")
                                : parentKnown ? Loc.T($"parent #{f.ParentId}", $"родитель #{f.ParentId}") : Loc.T($"parent #{f.ParentId} not preserved", $"родитель #{f.ParentId} не сохранился");
        aliveButton.Disabled = Main.FindAlive(f.AgentId) == null;
        if (open) Open();
    }

    // The chain of tracked ancestors up to the founder (Chronicle.Ancestry), each with what is left of it.
    void FillAncestry(ChronicleView view)
    {
        ancestry.Clear();
        ancestryIds.Clear();
        long id = fossil.ParentId > 0 ? fossil.ParentId : 0;
        if (view.Ancestry.TryGetValue(fossil.AgentId, out var self) && self.TrackedParent > 0 && self.TrackedParent != fossil.ParentId)
        {
            Line(fossil.ParentId, Loc.T($"parent #{fossil.ParentId}", $"родитель #{fossil.ParentId}"), view);
            id = self.TrackedParent;
        }
        for (int guard = 0; id > 0 && guard < 200; guard++)
        {
            view.Ancestry.TryGetValue(id, out var n);
            string what = n == null ? Loc.T($"#{id} (not tracked)", $"#{id} (не отслеживался)")
                        : Loc.T($"#{id} · generation {n.Gen} · ", $"#{id} · поколение {n.Gen} · ") + Chronicle.Day(n.Born)
                          + (n.Died >= 0 ? $" – {n.Died / P.DayLen + 1}, {Chronicle.CauseName(n.Cause)}" : Loc.T(", alive", ", жив")) + $" · {Chronicle.WhyText(n.Why)}";
            Line(id, what, view);
            if (n == null) break;
            id = n.TrackedParent;
        }
        if (ancestry.ItemCount == 0) { ancestry.AddItem(fossil.ParentId > 0 ? Loc.T("ancestors were not tracked", "предки не отслеживались") : Loc.T("lineage founder: no ancestors", "основатель линии: предков нет")); ancestryIds.Add(0); }
    }

    void Line(long id, string text, ChronicleView view)
    {
        bool alive = Main.FindAlive(id) != null, kept = view.FossilOf(id) != null;
        int k = ancestry.AddItem(text + (alive ? Loc.T(" — alive, click to show", " — жив, клик: показать") : kept ? Loc.T(" — fossil, click to open", " — окаменелость, клик: открыть") : ""));
        ancestry.SetItemCustomFgColor(k, alive ? UiKit.Good : kept ? UiKit.Acc : UiKit.Dim);
        ancestryIds.Add(id);
    }

    void Goto(long id)
    {
        if (id <= 0) return;
        var a = Main.FindAlive(id);
        if (a != null) { Main.Focus(a); Main.View.ZoomAt(30); return; }
        var f = Main.Sim.Chronicle.FossilOf(id);
        if (f != null) Show(f); else Ui.Toast(Loc.T($"nothing is left of #{id}", $"от #{id} ничего не осталось"));
    }

    void ToParent() { if (fossil != null) Goto(fossil.ParentId); }
    void ToAlive() { if (fossil != null) Goto(fossil.AgentId); }

    void Revive()
    {
        if (fossil?.Genome == null) { Ui.Toast(Loc.T("the fossil has no genome", "у окаменелости нет генома"), true); return; }
        var d = fossil.ToDesign(Main.World.Seed);
        Ui.Creator.LoadDesign(d, null);
        Ui.Creator.Open();
        Ui.Toast(Loc.T($"“{d.Name}” is in the editor: {fossil.Genome.Length} bytes of genome, {d.Body.Values.Sum()} molecules. Save it or plant it with brush 5",
                       $"«{d.Name}» в конструкторе: {fossil.Genome.Length} байт генома, {d.Body.Values.Sum()} молекул. Сохраните или сажайте кистью 5"));
    }
}
