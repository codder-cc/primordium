using System;
using System.Globalization;
using Godot;

namespace Primordium;

// Colours, fonts, the shared Theme and small constructors for the game's windows (same palette as the HUD).
public static class UiKit
{
    public static readonly Color Fg = new(0.93f, 0.94f, 0.97f), Dim = new(0.6f, 0.64f, 0.71f), Acc = new(1f, 0.82f, 0.4f),
        Bad = new(1f, 0.5f, 0.45f), Good = new(0.5f, 0.95f, 0.55f),
        WinBg = new(0.055f, 0.06f, 0.075f, 0.97f), TitleBg = new(0.075f, 0.082f, 0.1f, 1f), Rule = new(1, 1, 1, 0.09f),
        Field = new(0.03f, 0.033f, 0.042f, 1f), Btn = new(0.12f, 0.13f, 0.16f, 1f), BtnHover = new(0.17f, 0.185f, 0.225f, 1f),
        BtnPress = new(0.24f, 0.21f, 0.12f, 1f);

    static readonly string[] Sans = { "Helvetica Neue", "Helvetica", "Arial" };
    public static readonly Font Ui = new SystemFont { FontNames = Sans };
    public static readonly Font Bold = new SystemFont { FontNames = Sans, FontWeight = 700 };
    public static readonly Font Mono = new SystemFont { FontNames = new[] { "Menlo", "Monaco", "Courier New" } };
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static Theme theme;

    public static StyleBoxFlat Box(Color bg, int radius = 4, float margin = 6, Color? border = null, float marginV = -1)
    {
        var s = new StyleBoxFlat { BgColor = bg };
        s.SetCornerRadiusAll(radius);
        s.ContentMarginLeft = s.ContentMarginRight = margin;
        s.ContentMarginTop = s.ContentMarginBottom = marginV < 0 ? margin * 0.6f : marginV;
        if (border is Color b) { s.BorderColor = b; s.SetBorderWidthAll(1); }
        return s;
    }

    public static Theme Theme
    {
        get
        {
            if (theme != null) return theme;
            var t = theme = new Theme { DefaultFont = Ui, DefaultFontSize = 13 };
            foreach (var type in new[] { "Label", "Button", "LineEdit", "CheckBox", "CheckButton", "OptionButton", "PopupMenu", "TextEdit", "CodeEdit", "ItemList", "TabContainer", "TooltipLabel", "RichTextLabel", "SpinBox" })
                t.SetColor("font_color", type, Fg);
            t.SetColor("font_color", "TooltipLabel", Fg);
            t.SetStylebox("panel", "TooltipPanel", Box(new Color(0.03f, 0.035f, 0.045f, 0.97f), 4, 8, Rule));
            t.SetFontSize("font_size", "TooltipLabel", 12);
            // Buttons
            t.SetStylebox("normal", "Button", Box(Btn, 4, 9, null, 4));
            t.SetStylebox("hover", "Button", Box(BtnHover, 4, 9, null, 4));
            t.SetStylebox("pressed", "Button", Box(BtnPress, 4, 9, Acc with { A = 0.5f }, 4));
            t.SetStylebox("hover_pressed", "Button", Box(BtnPress, 4, 9, Acc with { A = 0.6f }, 4));
            t.SetStylebox("disabled", "Button", Box(new Color(0.08f, 0.085f, 0.1f), 4, 9, null, 4));
            t.SetStylebox("focus", "Button", new StyleBoxEmpty());
            t.SetColor("font_hover_color", "Button", Fg);
            t.SetColor("font_pressed_color", "Button", Acc);
            t.SetColor("font_hover_pressed_color", "Button", Acc);
            t.SetColor("font_disabled_color", "Button", Dim with { A = 0.5f });
            t.SetColor("font_focus_color", "Button", Fg);
            // Text fields
            t.SetStylebox("normal", "LineEdit", Box(Field, 3, 6, Rule, 3));
            t.SetStylebox("focus", "LineEdit", Box(Field, 3, 6, Acc with { A = 0.55f }, 3));
            t.SetStylebox("read_only", "LineEdit", Box(new Color(0.07f, 0.075f, 0.09f), 3, 6, null, 3));
            t.SetColor("font_uneditable_color", "LineEdit", Dim);
            t.SetColor("font_placeholder_color", "LineEdit", Dim with { A = 0.6f });
            t.SetColor("caret_color", "LineEdit", Acc);
            t.SetColor("selection_color", "LineEdit", new Color(0.35f, 0.45f, 0.7f, 0.5f));
            foreach (var type in new[] { "TextEdit", "CodeEdit" })
            {
                t.SetStylebox("normal", type, Box(Field, 3, 6, Rule, 4));
                t.SetStylebox("focus", type, Box(Field, 3, 6, Acc with { A = 0.4f }, 4));
                t.SetColor("caret_color", type, Acc);
                t.SetColor("selection_color", type, new Color(0.35f, 0.45f, 0.7f, 0.45f));
                t.SetColor("current_line_color", type, new Color(1, 1, 1, 0.035f));
                t.SetColor("line_number_color", type, Dim with { A = 0.6f });
                t.SetFont("font", type, Mono);
                t.SetFontSize("font_size", type, 12);
            }
            // Lists, popups
            t.SetStylebox("panel", "ItemList", Box(Field, 3, 4, Rule));
            t.SetStylebox("focus", "ItemList", new StyleBoxEmpty());
            t.SetStylebox("selected", "ItemList", Box(BtnPress, 3, 4));
            t.SetStylebox("selected_focus", "ItemList", Box(BtnPress, 3, 4));
            t.SetStylebox("hovered", "ItemList", Box(new Color(1, 1, 1, 0.05f), 3, 4));
            t.SetColor("font_selected_color", "ItemList", Acc);
            t.SetColor("font_hovered_color", "ItemList", Fg);
            t.SetStylebox("panel", "PopupMenu", Box(new Color(0.06f, 0.065f, 0.08f, 0.99f), 4, 6, Rule));
            t.SetStylebox("hover", "PopupMenu", Box(BtnHover, 3, 4));
            t.SetColor("font_hover_color", "PopupMenu", Acc);
            t.SetFontSize("font_size", "PopupMenu", 13);
            // Tabs
            t.SetStylebox("panel", "TabContainer", Box(new Color(0.04f, 0.045f, 0.055f, 1f), 3, 8, Rule));
            t.SetStylebox("tab_selected", "TabContainer", Box(Btn, 3, 10, null, 4));
            t.SetStylebox("tab_unselected", "TabContainer", Box(new Color(0.07f, 0.075f, 0.09f), 3, 10, null, 4));
            t.SetStylebox("tab_hovered", "TabContainer", Box(BtnHover, 3, 10, null, 4));
            t.SetColor("font_selected_color", "TabContainer", Acc);
            t.SetColor("font_unselected_color", "TabContainer", Dim);
            t.SetColor("font_hovered_color", "TabContainer", Fg);
            // Sliders
            var track = Box(new Color(1, 1, 1, 0.1f), 2, 0, null, 2);
            t.SetStylebox("slider", "HSlider", track);
            t.SetStylebox("grabber_area", "HSlider", Box(Acc with { A = 0.55f }, 2, 0, null, 2));
            t.SetStylebox("grabber_area_highlight", "HSlider", Box(Acc with { A = 0.8f }, 2, 0, null, 2));
            t.SetIcon("grabber", "HSlider", Dot(12, Fg));
            t.SetIcon("grabber_highlight", "HSlider", Dot(12, Acc));
            t.SetIcon("grabber_disabled", "HSlider", Dot(10, Dim with { A = 0.5f }));
            // Scroll bars
            t.SetStylebox("scroll", "VScrollBar", Box(new Color(1, 1, 1, 0.03f), 3, 0, null, 0));
            t.SetStylebox("grabber", "VScrollBar", Box(new Color(1, 1, 1, 0.2f), 3, 3, null, 3));
            t.SetStylebox("grabber_highlight", "VScrollBar", Box(new Color(1, 1, 1, 0.3f), 3, 3, null, 3));
            t.SetStylebox("grabber_pressed", "VScrollBar", Box(Acc with { A = 0.5f }, 3, 3, null, 3));
            t.SetColor("font_color", "CheckBox", Fg);
            t.SetColor("font_hover_color", "CheckBox", Fg);
            t.SetColor("font_pressed_color", "CheckBox", Fg);
            t.SetColor("font_hover_pressed_color", "CheckBox", Fg);
            foreach (var st in new[] { "normal", "pressed", "hover", "hover_pressed", "focus", "disabled" })
                t.SetStylebox(st, "CheckBox", new StyleBoxEmpty { ContentMarginLeft = 2, ContentMarginRight = 4, ContentMarginTop = 2, ContentMarginBottom = 2 });
            t.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = Rule, Thickness = 1 });
            t.SetConstant("separation", "HSeparator", 10);
            return t;
        }
    }

    static ImageTexture Dot(int size, Color c)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float r = size / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = new Vector2(x + 0.5f - r, y + 0.5f - r).Length();
                img.SetPixel(x, y, c with { A = Math.Clamp(r - d, 0, 1) * c.A });
            }
        return ImageTexture.CreateFromImage(img);
    }

    public static Label Text(string text, int size = 13, Color? color = null, Font font = null, bool wrap = false)
    {
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Pass };
        if (size != 13) l.AddThemeFontSizeOverride("font_size", size);
        if (color is Color c) l.AddThemeColorOverride("font_color", c);
        if (font != null) l.AddThemeFontOverride("font", font);
        if (wrap) { l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; l.CustomMinimumSize = new Vector2(40, 0); }
        return l;
    }

    public static Label Title(string text, int size = 14) => Text(text, size, Fg, Bold);

    public static Button Button(string text, Action pressed = null, string tip = null)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, TooltipText = tip ?? "" };
        if (pressed != null) b.Pressed += pressed;
        return b;
    }

    public static CheckBox Check(string text, bool on, Action<bool> toggled = null, string tip = null)
    {
        var c = new CheckBox { Text = text, ButtonPressed = on, FocusMode = Control.FocusModeEnum.None, TooltipText = tip ?? "" };
        if (toggled != null) c.Toggled += v => toggled(v);
        return c;
    }

    public static OptionButton Options(params string[] items)
    {
        var o = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        foreach (var s in items) o.AddItem(s);
        return o;
    }

    public static HBoxContainer Row(int sep = 6, params Control[] items)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", sep);
        foreach (var c in items) h.AddChild(c);
        return h;
    }

    public static VBoxContainer Col(int sep = 6)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", sep);
        return v;
    }

    public static Control Spacer(float w = 0, float h = 0, bool expand = true)
    {
        var c = new Control { CustomMinimumSize = new Vector2(w, h), MouseFilter = Control.MouseFilterEnum.Ignore };
        if (expand) c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return c;
    }

    public static ScrollContainer Scroll(Control content)
    {
        var s = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        s.AddChild(content);
        return s;
    }

    public static LineEdit Edit(string text = "", string placeholder = "", float width = 0)
    {
        var e = new LineEdit { Text = text, PlaceholderText = placeholder };
        if (width > 0) e.CustomMinimumSize = new Vector2(width, 0);
        return e;
    }

    public static SpinBox Spin(double min, double max, double step, double value, float width = 90)
    {
        var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(width, 0), FocusMode = Control.FocusModeEnum.None };
        return s;
    }

    public static double ParseNumber(string text, out bool ok)
    {
        ok = double.TryParse((text ?? "").Trim().Replace(',', '.').Replace(" ", ""), NumberStyles.Float, Inv, out double v);
        return v;
    }

    public static string Num(double v, bool isInt) => isInt ? ((long)Math.Round(v)).ToString(Inv) : v.ToString("G6", Inv);

    public static string Global(string userPath) => ProjectSettings.GlobalizePath(userPath);
}

// A button that asks once more before doing something irreversible: the first click turns it into
// "sure?" for a few seconds, the second does it.
public partial class ConfirmButton : Button
{
    public Action Confirmed;
    public string Ask = Loc.T("sure?", "точно?");
    string text;
    bool armed;
    ulong armedAt;

    public ConfirmButton() { }
    public ConfirmButton(string text, Action confirmed, string tip = null)
    {
        Text = this.text = text;
        Confirmed = confirmed;
        TooltipText = tip ?? "";
        FocusMode = FocusModeEnum.None;
        Pressed += OnPressed;
    }

    void OnPressed()
    {
        if (armed) { Disarm(); Confirmed?.Invoke(); return; }
        armed = true;
        armedAt = Time.GetTicksMsec();
        Text = Ask;
        AddThemeColorOverride("font_color", UiKit.Bad);
        AddThemeColorOverride("font_hover_color", UiKit.Bad);
    }

    void Disarm()
    {
        armed = false;
        Text = text;
        RemoveThemeColorOverride("font_color");
        RemoveThemeColorOverride("font_hover_color");
    }

    public override void _Process(double delta)
    {
        if (armed && Time.GetTicksMsec() - armedAt > 3000) Disarm();
    }
}
