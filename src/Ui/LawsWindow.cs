using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Primordium;

// Law presets as JSON: the built-in ones of the repository (res://presets: legacy — the default world
// before 2026-10-09 (11), default — today's defaults written out) and the player's in user://presets.
public static class Presets
{
    public const string BuiltInDir = "res://presets";

    public static bool BuiltIn(string path) => path.StartsWith("res://", StringComparison.Ordinal);

    public static List<(string name, string path)> List()
    {
        var list = new List<(string, string)>();
        foreach (var file in DirAccess.GetFilesAt(BuiltInDir).Where(f => f.EndsWith(".json", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal))
        {
            string path = BuiltInDir + "/" + file, name = Path.GetFileNameWithoutExtension(file);
            try { var p = Load(path); if (!string.IsNullOrWhiteSpace(p.Name)) name = p.Name; }
            catch { name += Loc.T(" (unreadable)", " (не читается)"); }
            list.Add((name + Loc.T(" (built-in)", " (встроенный)"), path));
        }
        if (!Directory.Exists(UiManager.PresetsDir)) return list;
        foreach (var path in Directory.GetFiles(UiManager.PresetsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            try { var p = ParamRegistry.LoadPreset(path); if (!string.IsNullOrWhiteSpace(p.Name)) name = p.Name; }
            catch { name += Loc.T(" (unreadable)", " (не читается)"); }
            list.Add((name, path));
        }
        return list;
    }

    // A preset by its path: a built-in one through Godot's file access (it may be packed), a player's from disk.
    public static ParamPreset Load(string path) => BuiltIn(path)
        ? ParamRegistry.FromJson(Godot.FileAccess.GetFileAsString(path))
        : ParamRegistry.LoadPreset(path);

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

    public LawsWindow() : base("laws", Loc.T("World laws", "Законы мира"), new Vector2(780, 600))
    {
        MinSize = new Vector2(560, 360);
        Body.AddChild(UiKit.Text(Loc.T("Laws change between ticks and are recorded in the world's law history (it goes into the save). " +
                                       "A slider applies when you let go of it; a number on Enter. Laws marked \"new world only\" " +
                                       "are set in the New world window (F4).",
                                       "Меняются между тиками и записываются в историю законов мира (она попадает в сохранение). " +
                                       "Ползунок применяется, когда его отпускаешь; число — по Enter. Законы с пометкой «только для нового мира» " +
                                       "задаются в окне «Новый мир» (F4)."), 12, UiKit.Dim, null, true));
        editor = new ParamEditor(new LiveParams(() => Main.Sim));
        Body.AddChild(editor);
        Body.AddChild(new HSeparator());

        presets = UiKit.Options();
        presets.CustomMinimumSize = new Vector2(180, 0);
        presets.TooltipText = Loc.T("law presets from user://presets", "наборы законов из user://presets");
        var apply = UiKit.Button(Loc.T("Apply preset", "Применить набор"), ApplyPreset,
            Loc.T("replace the world's laws with the preset (laws it does not name go to defaults)", "заменить законы мира набором (не названные в нём — по умолчанию)"));
        var del = new ConfirmButton(Loc.T("Delete", "Удалить"), DeletePreset, Loc.T("delete the preset file", "удалить файл набора"));
        var reset = new ConfirmButton(Loc.T("All defaults", "Всё по умолчанию"), () => Main.Sim.ResetParams(), Loc.T("reset every law to its default", "вернуть все законы к умолчаниям"));
        Body.AddChild(UiKit.Row(6, UiKit.Text(Loc.T("Presets:", "Наборы:"), 13, UiKit.Dim), presets, apply, del, UiKit.Spacer(), reset));
        presetName = UiKit.Edit("", Loc.T("new preset name", "имя нового набора"), 220);
        presetName.TextSubmitted += _ => SavePreset();
        var openDir = UiKit.Button(Loc.T("Folder", "Папка"), () => OS.ShellOpen(UiManager.PresetsDir), Loc.T("open the presets folder", "открыть папку наборов"));
        Body.AddChild(UiKit.Row(6, UiKit.Text(Loc.T("Save current laws as", "Сохранить текущие законы как"), 13, UiKit.Dim), presetName, UiKit.Button(Loc.T("Save", "Сохранить"), SavePreset), UiKit.Spacer(), openDir));
    }

    protected override void OnOpen() => ListPresets();

    void ListPresets()
    {
        presetList = Presets.List();
        presets.Clear();
        presets.AddItem(Loc.T("defaults", "по умолчанию"));
        foreach (var (name, _) in presetList) presets.AddItem(name);
        presets.Selected = 0;
    }

    void ApplyPreset()
    {
        int k = presets.Selected;
        if (k <= 0) { Main.Sim.ResetParams(); return; }
        var (name, path) = presetList[k - 1];
        try { Main.Sim.ApplyPreset(Presets.Load(path)); }
        catch (Exception e) { Ui.Toast(Loc.T($"cannot read preset \"{name}\": {e.Message}", $"не прочитать набор «{name}»: {e.Message}"), true); }
    }

    void DeletePreset()
    {
        int k = presets.Selected;
        if (k <= 0 || Presets.BuiltIn(presetList[k - 1].path)) { Ui.Toast(Loc.T("a built-in preset cannot be deleted", "встроенный набор не удаляется"), true); return; }
        try { File.Delete(presetList[k - 1].path); Ui.Toast(Loc.T($"preset \"{presetList[k - 1].name}\" deleted", $"набор «{presetList[k - 1].name}» удалён")); }
        catch (Exception e) { Ui.Toast(Loc.T("cannot delete: ", "не удалить: ") + e.Message, true); }
        ListPresets();
    }

    void SavePreset()
    {
        string name = presetName.Text.Trim();
        if (name.Length == 0) { Ui.Toast(Loc.T("enter a preset name", "впишите имя набора"), true); presetName.GrabFocus(); return; }
        try
        {
            var preset = Presets.FromValues(name, SimRunner.ParamValues);
            string path = Path.Combine(UiManager.PresetsDir, CreatureLibrary.FileName(name));
            ParamRegistry.SavePreset(path, preset);
            Ui.Toast(Loc.T($"preset \"{name}\" saved: {preset.Values.Count} differences from defaults", $"набор «{name}» сохранён: {preset.Values.Count} отличий от умолчаний"));
            presetName.Text = "";
            presetName.ReleaseFocus();
            ListPresets();
            for (int i = 0; i < presetList.Count; i++) if (presetList[i].path == path) presets.Selected = i + 1;
        }
        catch (Exception e) { Ui.Toast(Loc.T("cannot save preset: ", "не сохранить набор: ") + e.Message, true); }
    }
}
