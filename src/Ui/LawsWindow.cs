using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Primordium;

// Law presets kept as JSON in user://presets.
public static class Presets
{
    public static List<(string name, string path)> List()
    {
        var list = new List<(string, string)>();
        if (!Directory.Exists(UiManager.PresetsDir)) return list;
        foreach (var path in Directory.GetFiles(UiManager.PresetsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            try { var p = ParamRegistry.LoadPreset(path); if (!string.IsNullOrWhiteSpace(p.Name)) name = p.Name; }
            catch { name += " (не читается)"; }
            list.Add((name, path));
        }
        return list;
    }

    // The current laws of the running world (from the published values), only those that differ.
    public static ParamPreset FromValues(string name, double[] values) => new()
    {
        Name = name,
        Values = ParamRegistry.All.Where(p => values[p.Index] != p.Default).ToDictionary(p => p.Name, p => values[p.Index]),
    };
}

// F2: the laws of the running world.
public partial class LawsWindow : UiWindow
{
    ParamEditor editor;
    OptionButton presets;
    LineEdit presetName;
    List<(string name, string path)> presetList = new();

    public LawsWindow() : base("laws", "Законы мира", new Vector2(780, 600))
    {
        MinSize = new Vector2(560, 360);
        Body.AddChild(UiKit.Text("Меняются между тиками и записываются в историю законов мира (она попадает в сохранение). " +
                                 "Ползунок применяется, когда его отпускаешь; число — по Enter. Законы с пометкой «только для нового мира» " +
                                 "задаются в окне «Новый мир» (F4).", 12, UiKit.Dim, null, true));
        editor = new ParamEditor(new LiveParams(() => Main.Sim));
        Body.AddChild(editor);
        Body.AddChild(new HSeparator());

        presets = UiKit.Options();
        presets.CustomMinimumSize = new Vector2(180, 0);
        presets.TooltipText = "наборы законов из user://presets";
        var apply = UiKit.Button("Применить набор", ApplyPreset, "заменить законы мира набором (не названные в нём — по умолчанию)");
        var del = new ConfirmButton("Удалить", DeletePreset, "удалить файл набора");
        var reset = new ConfirmButton("Всё по умолчанию", () => Main.Sim.ResetParams(), "вернуть все законы к умолчаниям");
        Body.AddChild(UiKit.Row(6, UiKit.Text("Наборы:", 13, UiKit.Dim), presets, apply, del, UiKit.Spacer(), reset));
        presetName = UiKit.Edit("", "имя нового набора", 220);
        presetName.TextSubmitted += _ => SavePreset();
        var openDir = UiKit.Button("Папка", () => OS.ShellOpen(UiManager.PresetsDir), "открыть папку наборов");
        Body.AddChild(UiKit.Row(6, UiKit.Text("Сохранить текущие законы как", 13, UiKit.Dim), presetName, UiKit.Button("Сохранить", SavePreset), UiKit.Spacer(), openDir));
    }

    protected override void OnOpen() => ListPresets();

    void ListPresets()
    {
        presetList = Presets.List();
        presets.Clear();
        presets.AddItem("по умолчанию");
        foreach (var (name, _) in presetList) presets.AddItem(name);
        presets.Selected = 0;
    }

    void ApplyPreset()
    {
        int k = presets.Selected;
        if (k <= 0) { Main.Sim.ResetParams(); return; }
        var (name, path) = presetList[k - 1];
        try { Main.Sim.ApplyPreset(ParamRegistry.LoadPreset(path)); }
        catch (Exception e) { Ui.Toast($"не прочитать набор «{name}»: {e.Message}", true); }
    }

    void DeletePreset()
    {
        int k = presets.Selected;
        if (k <= 0) { Ui.Toast("встроенный набор не удаляется", true); return; }
        try { File.Delete(presetList[k - 1].path); Ui.Toast($"набор «{presetList[k - 1].name}» удалён"); }
        catch (Exception e) { Ui.Toast("не удалить: " + e.Message, true); }
        ListPresets();
    }

    void SavePreset()
    {
        string name = presetName.Text.Trim();
        if (name.Length == 0) { Ui.Toast("впишите имя набора", true); presetName.GrabFocus(); return; }
        try
        {
            var preset = Presets.FromValues(name, SimRunner.ParamValues);
            string path = Path.Combine(UiManager.PresetsDir, CreatureLibrary.FileName(name));
            ParamRegistry.SavePreset(path, preset);
            Ui.Toast($"набор «{name}» сохранён: {preset.Values.Count} отличий от умолчаний");
            presetName.Text = "";
            presetName.ReleaseFocus();
            ListPresets();
            for (int i = 0; i < presetList.Count; i++) if (presetList[i].path == path) presets.Selected = i + 1;
        }
        catch (Exception e) { Ui.Toast("не сохранить набор: " + e.Message, true); }
    }
}
