using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Primordium;

// Y: regions — copy a rectangle of the world (drag it out on the map), keep it in a library in
// user://regions (a .region file and a picture beside it), paste it here or in another world with a
// brush that shows its outline, turned in quarter turns; export and import as compact binary or JSON.
// The world does the work between ticks (World.CopyRegion / PasteRegion through SimRunner.Do); what is
// taken out and brought in is booked as the hand's (World.Region.cs).
public partial class RegionsWindow : UiWindow
{
    public static string RegionsDir => UiKit.Global("user://regions");

    // The map tool: selecting a rectangle, or pasting the armed region.
    public enum ToolMode { None, Select, Paste }
    public ToolMode Mode { get; private set; }

    Button selectButton, copyButton;
    Label selLabel, armedLabel, mapLabel;
    LineEdit nameEdit;
    SpinBox fromLevel, toLevel, dzSpin;
    CheckBox fullHeight, copyBodies, pasteBodies;
    OptionButton rotation, pasteMode;
    VBoxContainer list;
    readonly Dictionary<string, ImageTexture> thumbs = new();

    // Selection in cells (x may run past the seam: it wraps), and the drag making it.
    bool hasSel, dragging;
    int selX, selY, selW, selH, dragFrom;
    Region armed;
    string armedPath;
    RegionMapping armedMap;
    int hoverCell = -1;
    Vector2 lastMouse = new(-1, -1);
    bool busy;

    RegionOutline outline;

    public RegionsWindow() : base("regions", Loc.T("Regions", "Участки"), new Vector2(640, 640))
    {
        MinSize = new Vector2(540, 420);

        // ---- copy ----
        Body.AddChild(UiKit.Title(Loc.T("Copy a piece of the world", "Скопировать кусок мира"), 13));
        selectButton = UiKit.Button(Loc.T("Select on the map", "Выделить на карте"), () => SetMode(Mode == ToolMode.Select ? ToolMode.None : ToolMode.Select),
            Loc.T("then drag a rectangle over the ground with the left button (Esc — stop)", "затем протяните прямоугольник по земле левой кнопкой (Esc — отмена)"));
        selectButton.ToggleMode = true;
        selLabel = UiKit.Text(Loc.T("nothing selected", "ничего не выделено"), 13, UiKit.Dim);
        selLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Body.AddChild(UiKit.Row(8, selectButton, selLabel,
            UiKit.Button(Loc.T("Clear", "Сбросить"), () => { hasSel = false; UpdateSelLabel(); }, Loc.T("drop the selection", "снять выделение"))));

        fullHeight = UiKit.Check(Loc.T("full height", "вся высота"), true, on => { fromLevel.Editable = toLevel.Editable = !on; }, Loc.T("every level from the bedrock to the sky", "все уровни от недр до неба"));
        fromLevel = UiKit.Spin(0, World.Z - 1, 1, 0, 80);
        toLevel = UiKit.Spin(1, World.Z, 1, World.Z, 80);
        fromLevel.Editable = toLevel.Editable = false;
        copyBodies = UiKit.Check(Loc.T("with bodies", "с существами"), true, null, Loc.T("the bodies standing in it, with genomes, matter and energy", "существа, стоящие там, с геномами, веществом и энергией"));
        Body.AddChild(UiKit.Row(8, fullHeight, UiKit.Text(Loc.T("levels from", "уровни от"), 13, UiKit.Dim), fromLevel, UiKit.Text(Loc.T("to", "до"), 13, UiKit.Dim), toLevel, UiKit.Spacer(), copyBodies));

        nameEdit = UiKit.Edit("", Loc.T("name (optional)", "название (необязательно)"));
        nameEdit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        copyButton = UiKit.Button(Loc.T("Copy to the library", "Копировать в библиотеку"), Copy, Loc.T("the selected columns over the chosen levels: blocks, burials, loose matter, water, bodies", "выделенные столбцы на выбранных уровнях: блоки, захоронения, рыхлое, вода, существа"));
        copyButton.AddThemeColorOverride("font_color", UiKit.Acc);
        Body.AddChild(UiKit.Row(8, nameEdit, copyButton));
        Body.AddChild(new HSeparator());

        // ---- paste ----
        Body.AddChild(UiKit.Title(Loc.T("Paste", "Вставка"), 13));
        rotation = UiKit.Options("0°", "90°", "180°", "270°");
        rotation.TooltipText = Loc.T("turn the region clockwise (seen from above)", "повернуть участок по часовой (вид сверху)");
        pasteMode = UiKit.Options(Loc.T("replace terrain", "заменить рельеф"), Loc.T("only above ground", "только над землёй"));
        pasteMode.TooltipText = Loc.T("replace: the copied levels there are taken out and the region put in; above ground: nothing is taken out, the region fills only the air",
            "заменить: скопированные уровни там вынимаются и кладётся участок; над землёй: ничего не вынимается, участок заполняет только воздух");
        pasteBodies = UiKit.Check(Loc.T("bodies", "существа"), true, null, Loc.T("off: only matter — the bodies there stay, the region's are not brought", "выкл.: только вещество — тамошние существа остаются, существа участка не вносятся"));
        dzSpin = UiKit.Spin(-World.Z, World.Z, 1, 0, 70);
        dzSpin.TooltipText = Loc.T("shift in levels (up +)", "сдвиг по уровням (вверх +)");
        Body.AddChild(UiKit.Row(8, UiKit.Button("⟲", () => Turn(-1), Loc.T("turn left", "повернуть влево")), rotation, UiKit.Button("⟳", () => Turn(1), Loc.T("turn right", "повернуть вправо")),
            pasteMode, pasteBodies, UiKit.Text(Loc.T("shift", "сдвиг"), 13, UiKit.Dim), dzSpin));
        armedLabel = UiKit.Text(Loc.T("choose a region below (Paste), then click the map", "выберите участок ниже («Вставить»), затем щёлкните по карте"), 12, UiKit.Dim, null, true);
        Body.AddChild(armedLabel);
        mapLabel = UiKit.Text("", 12, UiKit.Dim, null, true);
        Body.AddChild(mapLabel);
        Body.AddChild(new HSeparator());

        // ---- library ----
        Body.AddChild(UiKit.Row(8, UiKit.Title(Loc.T("Library", "Библиотека"), 13), UiKit.Spacer(),
            UiKit.Button(Loc.T("Import…", "Импорт…"), Import, Loc.T("add a region file (.region or .region.json) from anywhere", "добавить файл участка (.region или .region.json) откуда угодно")),
            UiKit.Button(Loc.T("Folder", "Папка"), () => { Directory.CreateDirectory(RegionsDir); OS.ShellOpen(RegionsDir); }, Loc.T("open the regions folder", "открыть папку участков"))));
        list = UiKit.Col(6);
        Body.AddChild(UiKit.Scroll(list));
        UpdateSelLabel();
    }

    protected override void OnOpen()
    {
        EnsureOutline();
        Rebuild();
    }

    protected override void OnClose()
    {
        SetMode(ToolMode.None);
        if (outline != null) outline.Visible = false;
    }

    // --open regions:select / regions:paste (screenshots): a 40 × 40 selection on the map / the newest region
    // armed as the paste brush there; the window moves to the left, out of the way.
    public override void ShowView(string view)
    {
        Position = new Vector2(8, 140);
        if (view == "select") demo = 1;
        if (view == "paste") { demo = 2; CallDeferred(nameof(DemoArm)); }
    }

    int demo;   // a demo waiting for the camera to show the ground (1 select, 2 paste)
    Vector2 DemoPoint => new((GetViewportRect().Size.X - Hud.PanelW) * 0.8f, GetViewportRect().Size.Y * 0.6f);

    void DemoArm()
    {
        var newest = Directory.Exists(RegionsDir) ? Directory.GetFiles(RegionsDir, "*" + Region.Extension).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        if (newest != null) Arm(newest);
    }

    void DemoAim()
    {
        int c = Main.View.PickCell(DemoPoint);
        if (c < 0) return;
        if (demo == 1)
        {
            selW = selH = 40;
            selX = c % World.W - 20; selY = Math.Clamp(c / World.W - 20, 0, World.H - 40);
            hasSel = true;
            UpdateSelLabel();
        }
        else hoverCell = c;
        lastMouse = GetViewport().GetMousePosition();   // the brush stays there until the mouse moves
        demo = 0;
    }

    // The window is rebuilt on a language switch: its outline goes with it.
    public override void _ExitTree()
    {
        if (outline != null && IsInstanceValid(outline)) outline.QueueFree();
        outline = null;
    }

    void EnsureOutline()
    {
        if (outline != null && IsInstanceValid(outline)) return;
        outline = new RegionOutline();
        Main.View.AddChild(outline);
    }

    // ---- the map tool (Main routes the left button here first) ----

    public bool ToolActive => Visible && Mode != ToolMode.None;

    public void SetMode(ToolMode m)
    {
        Mode = m;
        dragging = false;
        selectButton?.SetPressedNoSignal(m == ToolMode.Select);
        if (m != ToolMode.Paste) hoverCell = -1;
    }

    // Esc: put the tool away (true if there was one).
    public bool CancelTool()
    {
        if (!ToolActive) return false;
        SetMode(ToolMode.None);
        return true;
    }

    public bool MapPress(Vector2 pos)
    {
        if (!ToolActive) return false;
        int c = Main.View.PickCell(pos);
        if (c < 0) return true;   // over the sky: swallowed, nothing happens
        if (Mode == ToolMode.Select)
        {
            dragging = true; dragFrom = c;
            SetSel(c, c);
            return true;
        }
        if (Mode == ToolMode.Paste && armed != null) Paste(c);
        return true;
    }

    void SetSel(int a, int b)
    {
        int ax = a % World.W, ay = a / World.W, bx = b % World.W, by = b / World.W;
        selX = Math.Min(ax, bx); selY = Math.Min(ay, by);
        selW = Math.Abs(ax - bx) + 1; selH = Math.Abs(ay - by) + 1;
        hasSel = true;
        UpdateSelLabel();
    }

    void UpdateSelLabel()
    {
        if (selLabel == null) return;
        selLabel.Text = hasSel
            ? Loc.T($"{selW} × {selH} columns at ({selX}, {selY})", $"{selW} × {selH} столбцов в ({selX}, {selY})")
            : Mode == ToolMode.Select ? Loc.T("drag over the ground…", "протяните по земле…") : Loc.T("nothing selected", "ничего не выделено");
        if (copyButton != null) copyButton.Disabled = !hasSel || busy;
    }

    public override void _Process(double delta)
    {
        if (!Visible || Main?.View == null) return;
        if (demo != 0) DemoAim();
        var mouse = GetViewport().GetMousePosition();
        bool overUi = mouse.X > GetViewportRect().Size.X - Hud.PanelW || Ui.IsOver(mouse);
        if (dragging)
        {
            if (!Input.IsMouseButtonPressed(MouseButton.Left)) { dragging = false; SetMode(ToolMode.None); UpdateSelLabel(); }
            else { int c = Main.View.PickCell(mouse); if (c >= 0) SetSel(dragFrom, c); }
        }
        if (Mode == ToolMode.Paste && !overUi && mouse != lastMouse) { int c = Main.View.PickCell(mouse); if (c >= 0) hoverCell = c; }
        lastMouse = mouse;
        EnsureOutline();
        // The outline: the paste brush where it would land, else the selection.
        if (Mode == ToolMode.Paste && armed != null && hoverCell >= 0)
        {
            var (x, y, w, h) = PasteRect(hoverCell);
            outline.Show(Main.View, x, y, w, h, new Color(0.45f, 0.9f, 1f), $"{w} × {h}");
        }
        else if (hasSel) outline.Show(Main.View, selX, selY, selW, selH, new Color(1f, 0.82f, 0.4f), $"{selW} × {selH}");
        else outline.Visible = false;
    }

    // Where the armed region lands with its centre at the cell under the cursor.
    (int x, int y, int w, int h) PasteRect(int c)
    {
        int rot = rotation.Selected;
        int w = rot % 2 == 0 ? armed.SizeX : armed.SizeY, h = rot % 2 == 0 ? armed.SizeY : armed.SizeX;
        return (((c % World.W - w / 2) % World.W + World.W) % World.W, c / World.W - h / 2, w, h);
    }

    void Turn(int d) => rotation.Select(((rotation.Selected + d) % 4 + 4) % 4);

    // ---- copy and paste ----

    void Copy()
    {
        if (!hasSel || busy) return;
        busy = true;
        UpdateSelLabel();
        int x = selX, y = selY, w = selW, h = selH;
        int z0 = fullHeight.ButtonPressed ? 0 : (int)fromLevel.Value, z1 = fullHeight.ButtonPressed ? World.Z : (int)toLevel.Value;
        bool bodies = copyBodies.ButtonPressed;
        string name = nameEdit.Text.Trim();
        Main.Sim.Do(world =>
        {
            Region r = null; string error = null;
            try { r = world.CopyRegion(x, y, w, h, z0, z1, bodies, name.Length > 0 ? name : null); }
            catch (ArgumentException e) { error = e.Message; }
            Ui.Post(() =>
            {
                busy = false;
                UpdateSelLabel();
                if (r == null) { Ui.Toast(Loc.T("cannot copy: ", "не скопировать: ") + error, true); return; }
                try
                {
                    Directory.CreateDirectory(RegionsDir);
                    string file = Region.FileName(name.Length > 0 ? name : $"region_{DateTime.Now:yyyyMMdd_HHmmss}"), path = Path.Combine(RegionsDir, file + Region.Extension);
                    for (int k = 2; File.Exists(path); k++) path = Path.Combine(RegionsDir, $"{file}_{k}{Region.Extension}");
                    r.Save(path);
                    SaveThumb(r, Thumb(path));
                    nameEdit.Text = "";
                    Ui.Toast(Loc.T($"copied {r.SizeX} × {r.SizeY} columns, levels {r.Z0}…{r.Z1 - 1}: {r.Voxels:N0} blocks, {r.Bodies.Count} bodies → {Path.GetFileName(path)}",
                        $"скопировано {r.SizeX} × {r.SizeY} столбцов, уровни {r.Z0}…{r.Z1 - 1}: блоков {r.Voxels:N0}, существ {r.Bodies.Count} → {Path.GetFileName(path)}"));
                    Rebuild();
                }
                catch (Exception e) { Ui.Toast(Loc.T("cannot save the region: ", "не сохранить участок: ") + e.Message, true); }
            });
        });
    }

    void Arm(string path)
    {
        try
        {
            armed = Region.Load(path);
            armedPath = path;
            armedMap = Main.World.MapChemistry(armed.Chem);
        }
        catch (Exception e) { Ui.Toast(Loc.T("cannot read the region: ", "не прочитать участок: ") + e.Message, true); armed = null; return; }
        SetMode(ToolMode.Paste);
        string chem = armedMap.Identity
            ? Loc.T("same chemistry", "та же химия")
            : Loc.T($"another chemistry (seed {armed.Chem.Seed}, here {Main.World.Seed}): its {armedMap.Changed} molecule kinds become this world's nearest — hover for the mapping",
                    $"другая химия (seed {armed.Chem.Seed}, здесь {Main.World.Seed}): {armedMap.Changed} видов молекул станут ближайшими здешними — наведите, чтобы увидеть сопоставление");
        armedLabel.Text = Loc.T($"brush: “{armed.Name}” {armed.SizeX} × {armed.SizeY} — click the map to paste (Esc — put away)", $"кисть: «{armed.Name}» {armed.SizeX} × {armed.SizeY} — щёлкните по карте, чтобы вставить (Esc — убрать)");
        armedLabel.AddThemeColorOverride("font_color", UiKit.Acc);
        mapLabel.Text = chem + (armed.WorldZ != World.Z ? Loc.T($" · from a world {armed.WorldZ} levels tall: what is above {World.Z - 1} is clipped", $" · из мира высотой {armed.WorldZ}: всё выше {World.Z - 1} обрежется") : "");
        mapLabel.AddThemeColorOverride("font_color", armedMap.Identity ? UiKit.Dim : UiKit.Bad);
        mapLabel.TooltipText = armedMap.Identity ? "" : string.Join("\n", armedMap.Lines);
        mapLabel.MouseFilter = MouseFilterEnum.Stop;
    }

    void Paste(int cell)
    {
        var r = armed;
        var (x, y, _, _) = PasteRect(cell);
        var o = new PasteOptions { Rotation = rotation.Selected, Mode = pasteMode.Selected == 1 ? PasteMode.AboveGround : PasteMode.Replace, Bodies = pasteBodies.ButtonPressed, Dz = (int)dzSpin.Value };
        Main.Sim.Do(world =>
        {
            var res = world.PasteRegion(r, x, y, o);
            Ui.Post(() => Ui.Toast(res.ToString(), !res.Ok));
        });
    }

    // ---- library ----

    static string Thumb(string path) => Path.Combine(Path.GetDirectoryName(path) ?? "", BaseName(path) + ".png");
    static string BaseName(string path)
    {
        string f = Path.GetFileName(path);
        return f.EndsWith(Region.JsonExtension, StringComparison.OrdinalIgnoreCase) ? f[..^Region.JsonExtension.Length]
             : f.EndsWith(Region.Extension, StringComparison.OrdinalIgnoreCase) ? f[..^Region.Extension.Length] : Path.GetFileNameWithoutExtension(f);
    }

    // The picture of a region from above (Region.Preview), scaled up without smoothing.
    static void SaveThumb(Region r, string png)
    {
        var px = r.Preview();
        var img = Image.CreateEmpty(r.SizeX, r.SizeY, false, Image.Format.Rgb8);
        for (int y = 0; y < r.SizeY; y++)
            for (int x = 0; x < r.SizeX; x++) { var c = px[y * r.SizeX + x]; img.SetPixel(x, y, new Color(c.R, c.G, c.B)); }
        int k = Math.Max(1, 128 / Math.Max(r.SizeX, r.SizeY));
        if (k > 1) img.Resize(r.SizeX * k, r.SizeY * k, Image.Interpolation.Nearest);
        img.SavePng(png);
    }

    public void Rebuild()
    {
        foreach (var c in list.GetChildren()) c.QueueFree();
        Directory.CreateDirectory(RegionsDir);
        var files = Directory.GetFiles(RegionsDir).Where(f => f.EndsWith(Region.Extension, StringComparison.OrdinalIgnoreCase) || f.EndsWith(Region.JsonExtension, StringComparison.OrdinalIgnoreCase)).ToList();
        var rows = new List<(string path, RegionInfo info, string error)>();
        foreach (var f in files)
        {
            try { rows.Add((f, Region.ReadInfo(f), null)); }
            catch (Exception e) { rows.Add((f, null, e.Message)); }
        }
        if (rows.Count == 0) list.AddChild(UiKit.Text(Loc.T("No regions yet: select a rectangle on the map and copy it.", "Участков пока нет: выделите прямоугольник на карте и скопируйте."), 13, UiKit.Dim, null, true));
        foreach (var (path, info, error) in rows.OrderByDescending(r => r.info?.SavedAt ?? File.GetLastWriteTimeUtc(r.path)))
            list.AddChild(Row(path, info, error));
    }

    Control Row(string path, RegionInfo info, string error)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiKit.Box(new Color(1, 1, 1, path == armedPath && armed != null ? 0.08f : 0.035f), 4, 8, null, 6));
        var row = UiKit.Row(10);
        panel.AddChild(row);
        var pic = new TextureRect { CustomMinimumSize = new Vector2(84, 84), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.Nearest };
        pic.Texture = LoadThumb(path);
        var picBg = new PanelContainer();
        picBg.AddThemeStyleboxOverride("panel", UiKit.Box(new Color(0, 0, 0, 0.35f), 3, 0));
        picBg.AddChild(pic);
        row.AddChild(picBg);

        var text = UiKit.Col(2);
        text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        text.AddChild(UiKit.Text(info?.Name is { Length: > 0 } n ? n : BaseName(path), 14, UiKit.Fg, UiKit.Bold));
        if (info != null)
        {
            string levels = info.Z0 <= 0 && info.Z1 >= World.Z ? Loc.T("full height", "вся высота") : Loc.T($"levels {info.Z0}…{info.Z1 - 1}", $"уровни {info.Z0}…{info.Z1 - 1}");
            text.AddChild(UiKit.Text(Loc.T($"{info.SizeX} × {info.SizeY} columns · {levels} · {info.Voxels:N0} blocks · {info.Bodies} bodies",
                $"{info.SizeX} × {info.SizeY} столбцов · {levels} · блоков {info.Voxels:N0} · существ {info.Bodies}"), 12, UiKit.Fg));
            bool same = Main.World != null && info.SrcSeed == Main.World.Seed;
            text.AddChild(UiKit.Text(Loc.T($"seed {info.SrcSeed}, tick {info.SrcTick:N0}{(same ? "" : " · another world")} · {info.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {new FileInfo(path).Length / 1024.0:N0} KB{(path.EndsWith(".json") ? " JSON" : "")}",
                $"seed {info.SrcSeed}, тик {info.SrcTick:N0}{(same ? "" : " · другой мир")} · {info.SavedAt.ToLocalTime():dd.MM.yyyy HH:mm} · {new FileInfo(path).Length / 1024.0:N0} КБ{(path.EndsWith(".json") ? " JSON" : "")}"), 12, UiKit.Dim));
        }
        else text.AddChild(UiKit.Text(Loc.T("unreadable: ", "не читается: ") + error, 12, UiKit.Bad, null, true));
        row.AddChild(text);

        var buttons = UiKit.Col(3);
        var paste = UiKit.Button(Loc.T("Paste", "Вставить"), () => { Arm(path); Rebuild(); }, Loc.T("take it as the paste brush: its outline follows the cursor, a click pastes", "взять кистью вставки: контур идёт за курсором, щелчок вставляет"));
        paste.Disabled = info == null;
        buttons.AddChild(paste);
        var ej = UiKit.Button(Loc.T("Export JSON…", "Экспорт JSON…"), () => Export(path, true), Loc.T("readable exchange format", "читаемый формат для обмена"));
        var eb = UiKit.Button(Loc.T("Export binary…", "Экспорт двоичный…"), () => Export(path, false), Loc.T("compact (Brotli), as in the library", "компактный (Brotli), как в библиотеке"));
        ej.Disabled = eb.Disabled = info == null;
        buttons.AddChild(ej);
        buttons.AddChild(eb);
        buttons.AddChild(new ConfirmButton(Loc.T("Delete", "Удалить"), () => Delete(path), Loc.T("delete the region file", "удалить файл участка")));
        row.AddChild(buttons);
        return panel;
    }

    ImageTexture LoadThumb(string path)
    {
        string p = Thumb(path);
        if (!File.Exists(p))
        {
            try { SaveThumb(Region.Load(path), p); }   // an imported region without its picture: draw one
            catch { return null; }
        }
        string key = p + File.GetLastWriteTimeUtc(p).Ticks;
        if (thumbs.TryGetValue(key, out var t)) return t;
        var img = Image.LoadFromFile(p);
        return img == null ? null : thumbs[key] = ImageTexture.CreateFromImage(img);
    }

    void Delete(string path)
    {
        try
        {
            File.Delete(path);
            if (File.Exists(Thumb(path))) File.Delete(Thumb(path));
            if (path == armedPath) { armed = null; armedPath = null; SetMode(ToolMode.None); }
            Ui.Toast(Loc.T("deleted: ", "удалено: ") + Path.GetFileName(path));
        }
        catch (Exception e) { Ui.Toast(Loc.T("cannot delete: ", "не удалить: ") + e.Message, true); }
        Rebuild();
    }

    void PickFile(FileDialog.FileModeEnum mode, string title, string suggested, Action<string> picked)
    {
        var dlg = new FileDialog
        {
            FileMode = mode, Access = FileDialog.AccessEnum.Filesystem, Title = title, UseNativeDialog = true,
            Filters = new[] { Loc.T("*.region, *.json ; Primordium region", "*.region, *.json ; Участок Primordium") },
            CurrentDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        };
        if (suggested != null) dlg.CurrentFile = suggested;
        dlg.FileSelected += f => { picked(f); dlg.QueueFree(); };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered(new Vector2I(760, 480));
    }

    void Export(string path, bool json) =>
        PickFile(FileDialog.FileModeEnum.SaveFile, Loc.T("Export region", "Экспорт участка"), BaseName(path) + (json ? Region.JsonExtension : Region.Extension), dest =>
        {
            try
            {
                string ext = json ? Region.JsonExtension : Region.Extension;
                if (!dest.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) dest = (json && dest.EndsWith(".json") ? dest[..^5] : dest) + ext;
                Region.Load(path).Save(dest);
                if (File.Exists(Thumb(path))) File.Copy(Thumb(path), Thumb(dest), true);
                Ui.Toast(Loc.T("exported: ", "экспортировано: ") + dest);
            }
            catch (Exception e) { Ui.Toast(Loc.T("cannot export: ", "не экспортировать: ") + e.Message, true); }
        });

    void Import() =>
        PickFile(FileDialog.FileModeEnum.OpenFile, Loc.T("Import region", "Импорт участка"), null, src =>
        {
            try
            {
                var r = Region.Load(src);   // refuse what is not a region before copying anything
                Directory.CreateDirectory(RegionsDir);
                string name = BaseName(src), dest = Path.Combine(RegionsDir, name + Region.Extension);
                for (int k = 2; File.Exists(dest); k++) dest = Path.Combine(RegionsDir, $"{name}_{k}{Region.Extension}");
                r.Save(dest);   // into the library as binary
                SaveThumb(r, Thumb(dest));
                Ui.Toast(Loc.T("imported: ", "импортировано: ") + Path.GetFileName(dest));
                Rebuild();
            }
            catch (Exception e) { Ui.Toast(Loc.T("not a Primordium region: ", "не участок Primordium: ") + e.Message, true); }
        });
}

// The outline of a rectangle of columns on the ground (the selection or the paste brush): a line
// along the top of every edge column, posts at the corners and its size above it.
public partial class RegionOutline : Node3D
{
    readonly MeshInstance3D lines = new();
    readonly ImmediateMesh mesh = new();
    readonly StandardMaterial3D mat = new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled, VertexColorUseAsAlbedo = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
    readonly Label3D label = new() { Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, FontSize = 64, PixelSize = 0.02f, OutlineSize = 12, FixedSize = false };
    (int, int, int, int, Color, long) shown;

    public RegionOutline()
    {
        lines.Mesh = mesh;
        lines.MaterialOverride = mat;
        lines.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(lines);
        AddChild(label);
    }

    public void Show(View3D view, int x0, int y0, int w, int h, Color col, string text)
    {
        Visible = true;
        var world = view.World;
        if (world == null) return;
        long version = world.TerrainVersion;
        var key = (x0, y0, w, h, col, version);
        if (key == shown) return;
        shown = key;
        int W = World.W, H = World.H;
        float G(int x, int y) { int c = Math.Clamp(y, 0, H - 1) * W + ((x % W) + W) % W; return view.Ground(c) + (world.Water[c] + world.Ice[c]) * P.BlockH + 0.25f; }
        mesh.ClearSurfaces();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        // A ribbon under half a cell wide: flat for a run along the ground, a cross of two for a step or a post.
        void Quad(Vector3 a, Vector3 b, Vector3 off)
        {
            foreach (var p in new[] { a - off, b - off, b + off, a - off, b + off, a + off }) { mesh.SurfaceSetColor(col); mesh.SurfaceAddVertex(p); }
        }
        void Seg(Vector3 a, Vector3 b)
        {
            const float r = 0.22f;
            if (a.X == b.X && a.Z == b.Z) { Quad(a, b, new Vector3(r, 0, 0)); Quad(a, b, new Vector3(0, 0, r)); }
            else if (a.X != b.X) Quad(a, b, new Vector3(0, 0, r));
            else Quad(a, b, new Vector3(r, 0, 0));
        }
        int ya = Math.Max(0, y0), yb = Math.Min(H, y0 + h);
        float top = 0;
        // North and south edges, column by column (x wraps: a segment never crosses the seam).
        for (int i = 0; i < w; i++)
        {
            int x = ((x0 + i) % W + W) % W;
            foreach (int y in new[] { ya, yb - 1 })
            {
                float g = G(x, y);
                top = Math.Max(top, g);
                float ez = y == ya ? ya : yb;
                Seg(new Vector3(x, g, ez), new Vector3(x + 1, g, ez));
                if (i + 1 < w && x + 1 < W) Seg(new Vector3(x + 1, g, ez), new Vector3(x + 1, G(x + 1, y), ez));
            }
        }
        // West and east edges.
        foreach (int side in new[] { 0, 1 })
        {
            int x = side == 0 ? ((x0 % W) + W) % W : ((x0 + w - 1) % W + W) % W;
            float ex = side == 0 ? x : x + 1;
            for (int y = ya; y < yb; y++)
            {
                float g = G(x, y);
                top = Math.Max(top, g);
                Seg(new Vector3(ex, g, y), new Vector3(ex, g, y + 1));
                if (y + 1 < yb) Seg(new Vector3(ex, g, y + 1), new Vector3(ex, G(x, y + 1), y + 1));
            }
        }
        // Corner posts.
        foreach (var (cx, cy) in new[] { (x0, ya), (x0 + w - 1, ya), (x0, yb - 1), (x0 + w - 1, yb - 1) })
        {
            int x = ((cx % W) + W) % W;
            float ex = cx == x0 ? x : x + 1, ez = cy == ya ? ya : yb, g = G(x, cy);
            Seg(new Vector3(ex, g, ez), new Vector3(ex, g + 6, ez));
        }
        mesh.SurfaceEnd();
        label.Text = text;
        label.Modulate = col;
        label.Position = new Vector3(((x0 + w / 2f) % W + W) % W, top + 5, (ya + yb) / 2f);
        label.PixelSize = 0.012f * Math.Max(1, Math.Max(w, h) / 24f);
    }
}
