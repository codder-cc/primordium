using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Primordium;

// F6: saved worlds in user://saves — a picture of the view at save time (name.png beside name.sav),
// tick, population, seed, date and note; save into a new slot, overwrite, delete, load; autosave.
public partial class SavesWindow : UiWindow
{
    VBoxContainer list;
    LineEdit note;
    SpinBox minutes, slots;
    Label autoInfo;
    readonly Dictionary<string, ImageTexture> thumbs = new();
    SimRunner.SaveOutcome seenAutosave;
    bool busy;

    public const string QuickName = "quick.sav";

    public SavesWindow() : base("saves", "Сохранения", new Vector2(620, 560))
    {
        MinSize = new Vector2(520, 320);
        note = UiKit.Edit("", "заметка к сохранению (необязательно)");
        note.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        note.TextSubmitted += _ => SaveNew();
        var save = UiKit.Button("Сохранить в новый слот", SaveNew, "весь мир в один файл: после загрузки он идёт ровно так же");
        save.AddThemeColorOverride("font_color", UiKit.Acc);
        Body.AddChild(UiKit.Row(8, note, save));

        list = UiKit.Col(6);
        Body.AddChild(UiKit.Scroll(list));
        Body.AddChild(new HSeparator());

        minutes = UiKit.Spin(0, 120, 1, 10, 80);
        minutes.ValueChanged += v => { Ui.State.AutosaveMinutes = (float)v; Ui.ConfigureRunner(Main.Sim); Ui.SaveSoon(); };
        slots = UiKit.Spin(1, 10, 1, 3, 70);
        slots.ValueChanged += v => { Ui.State.AutosaveSlots = (int)v; Ui.ConfigureRunner(Main.Sim); Ui.SaveSoon(); };
        Body.AddChild(UiKit.Row(8, UiKit.Text("Автосохранение каждые", 13, UiKit.Dim), minutes, UiKit.Text("мин (0 — выкл.), по кругу в", 13, UiKit.Dim), slots,
            UiKit.Text("слота", 13, UiKit.Dim), UiKit.Spacer(), UiKit.Button("Папка", () => OS.ShellOpen(UiManager.SavesDir), "открыть папку сохранений")));
        autoInfo = UiKit.Text("", 12, UiKit.Dim, null, true);
        Body.AddChild(autoInfo);
    }

    protected override void OnOpen()
    {
        minutes.SetValueNoSignal(Ui.State.AutosaveMinutes);
        slots.SetValueNoSignal(Ui.State.AutosaveSlots);
        Rebuild();
    }

    public override void _Process(double delta)
    {
        // An autosave happened (on the simulation thread): give it a picture and show it in the list.
        var last = Main.Sim?.LastAutosave;
        if (last != null && last != seenAutosave)
        {
            seenAutosave = last;
            if (last.Ok) Capture(Thumb(last.Path), () => { if (Visible) Rebuild(); });
        }
        if (Visible && Main.Sim != null)
            autoInfo.Text = (Ui.State.AutosaveMinutes <= 0 ? "автосохранение выключено" : $"файлы autosave_N.sav в {UiManager.SavesDir}") +
                            (last != null ? $" · последнее: {(last.Ok ? $"тик {last.Tick:N0}, {last.Bytes / 1048576.0:F1} МБ, {last.Ms:F0} мс" : "ошибка " + last.Error)}" : "") +
                            " · F5 — быстро сохранить, F9 — загрузить быстрое";
    }

    static string Thumb(string savePath) => Path.ChangeExtension(savePath, ".png");

    // ---- list ----

    public void Rebuild()
    {
        foreach (var c in list.GetChildren()) c.QueueFree();
        Directory.CreateDirectory(UiManager.SavesDir);
        var files = new List<(string path, SaveInfo info, string error)>();
        foreach (var path in Directory.GetFiles(UiManager.SavesDir, "*.sav"))
        {
            try { files.Add((path, SimRunner.ReadSaveInfo(path), null)); }
            catch (Exception e) { files.Add((path, null, e.Message)); }
        }
        if (files.Count == 0) list.AddChild(UiKit.Text("Сохранений пока нет.", 13, UiKit.Dim));
        foreach (var (path, info, error) in files.OrderByDescending(f => f.info?.SavedAt ?? File.GetLastWriteTimeUtc(f.path)))
            list.AddChild(SlotRow(path, info, error));
    }

    Control SlotRow(string path, SaveInfo info, string error)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiKit.Box(new Color(1, 1, 1, 0.035f), 4, 8, null, 6));
        var row = UiKit.Row(10);
        panel.AddChild(row);
        var pic = new TextureRect { CustomMinimumSize = new Vector2(144, 81), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered };
        pic.Texture = LoadThumb(path);
        var picBg = new PanelContainer();
        picBg.AddThemeStyleboxOverride("panel", UiKit.Box(new Color(0, 0, 0, 0.35f), 3, 0));
        picBg.AddChild(pic);
        row.AddChild(picBg);

        var text = UiKit.Col(2);
        text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        string file = Path.GetFileNameWithoutExtension(path);
        string kind = file == "quick" ? "быстрое сохранение" : file.StartsWith("autosave") ? "автосохранение" : file;
        text.AddChild(UiKit.Text(info?.Note is { Length: > 0 } n && n != "autosave" ? n : kind, 14, UiKit.Fg, UiKit.Bold));
        if (info != null)
        {
            text.AddChild(UiKit.Text($"сутки {info.Tick / P.DayLen + 1} · тик {info.Tick:N0} · особей {info.Population:N0} · seed {info.Seed}", 12, UiKit.Fg));
            long bytes = new FileInfo(path).Length;
            text.AddChild(UiKit.Text($"{info.SavedAt.ToLocalTime():dd.MM.yyyy HH:mm} · {bytes / 1048576.0:F1} МБ · {kind}", 12, UiKit.Dim));
        }
        else text.AddChild(UiKit.Text("не читается: " + error, 12, UiKit.Bad, null, true));
        row.AddChild(text);

        var buttons = UiKit.Col(3);
        var load = UiKit.Button("Загрузить", () => Load(path), "заменить текущий мир сохранённым");
        load.Disabled = info == null;
        buttons.AddChild(load);
        buttons.AddChild(new ConfirmButton("Перезаписать", () => SaveTo(path, info?.Note is { Length: > 0 } n2 && n2 != "autosave" ? n2 : note.Text.Trim()), "записать текущий мир в этот слот"));
        buttons.AddChild(new ConfirmButton("Удалить", () => Delete(path), "удалить файл сохранения"));
        row.AddChild(buttons);
        return panel;
    }

    ImageTexture LoadThumb(string savePath)
    {
        string p = Thumb(savePath);
        if (!File.Exists(p)) return null;
        string key = p + File.GetLastWriteTimeUtc(p).Ticks;
        if (thumbs.TryGetValue(key, out var t)) return t;
        var img = Image.LoadFromFile(p);
        if (img == null) return null;
        return thumbs[key] = ImageTexture.CreateFromImage(img);
    }

    // ---- actions ----

    void SaveNew()
    {
        string path = Path.Combine(UiManager.SavesDir, $"save_{DateTime.Now:yyyyMMdd_HHmmss}.sav");
        SaveTo(path, note.Text.Trim());
        note.Text = "";
        note.ReleaseFocus();
    }

    // Saves the world to a file: first a picture of the view (without the panels), then the world itself.
    public void SaveTo(string path, string text)
    {
        if (busy) return;
        busy = true;
        Directory.CreateDirectory(UiManager.SavesDir);
        Capture(Thumb(path), () =>
            Main.Sim.Save(path, string.IsNullOrEmpty(text) ? null : text, o => Ui.Post(() =>
            {
                busy = false;
                if (Visible) Rebuild();
            })));
    }

    public void QuickSave() => SaveTo(Path.Combine(UiManager.SavesDir, QuickName), "быстрое сохранение");

    public void QuickLoad()
    {
        string path = Path.Combine(UiManager.SavesDir, QuickName);
        if (!File.Exists(path)) { Ui.Toast("быстрого сохранения ещё нет (F5 — сохранить)", true); return; }
        Load(path);
    }

    void Load(string path)
    {
        Ui.Toast("загружаю " + Path.GetFileName(path) + "…");
        Main.Sim.Load(path);
    }

    void Delete(string path)
    {
        try
        {
            File.Delete(path);
            if (File.Exists(Thumb(path))) File.Delete(Thumb(path));
            Ui.Toast("удалено: " + Path.GetFileName(path));
        }
        catch (Exception e) { Ui.Toast("не удалить: " + e.Message, true); }
        Rebuild();
    }

    // A small picture of the world: the panels are hidden for one frame, the view is read back, cropped
    // to the map (left of the info panel) and scaled down.
    async void Capture(string pngPath, Action then)
    {
        var layer = Ui.GetParent<CanvasLayer>();
        try
        {
            layer.Visible = false;
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var img = GetViewport().GetTexture().GetImage();
            layer.Visible = true;
            var vs = GetViewportRect().Size;
            float k = img.GetWidth() / Math.Max(1f, vs.X);
            int w = (int)(img.GetWidth() - Hud.PanelW * k), h = img.GetHeight();
            int top = (int)(70 * k), bottom = (int)(58 * k);
            var crop = img.GetRegion(new Rect2I(0, top, Math.Max(16, w), Math.Max(16, h - top - bottom)));
            float aspect = (float)crop.GetWidth() / crop.GetHeight();
            crop.Resize(Math.Max(16, (int)(160 * aspect)), 160, Image.Interpolation.Bilinear);
            crop.SavePng(pngPath);
        }
        catch (Exception e) { GD.PrintErr("thumbnail: " + e.Message); }
        finally { layer.Visible = true; }
        then?.Invoke();
    }
}
