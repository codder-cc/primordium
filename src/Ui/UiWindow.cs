using System;
using Godot;

namespace Primordium;

// A floating panel over the world: a title bar to drag it by, a close button, a grip in the corner to
// resize it. UiManager keeps the windows, their order, Esc and where they were (user://ui.json).
public partial class UiWindow : Control
{
    public string Id;
    public UiManager Ui;
    public Main Main => Ui.Main;
    public Vector2 MinSize = new(360, 240);
    public event Action Opened, Closed;

    protected VBoxContainer Body;   // the content goes here
    Label title;
    bool dragging, resizing;
    Vector2 grab;

    protected UiWindow() { }

    public UiWindow(string id, string caption, Vector2 size)
    {
        Id = id;
        Name = "Window_" + id;
        Size = size;
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;

        var bg = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        bg.AddThemeStyleboxOverride("panel", UiKit.Box(UiKit.WinBg, 6, 0, new Color(1, 1, 1, 0.13f)));
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var bar = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.Move };
        var barStyle = UiKit.Box(UiKit.TitleBg, 6, 12, null, 6);
        barStyle.CornerRadiusBottomLeft = barStyle.CornerRadiusBottomRight = 0;
        bar.AddThemeStyleboxOverride("panel", barStyle);
        bar.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
        bar.OffsetBottom = 32;
        var row = UiKit.Row(8);
        title = UiKit.Title(caption, 14);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.MouseFilter = MouseFilterEnum.Ignore;
        var close = UiKit.Button("✕", Close, Loc.T("close (Esc)", "закрыть (Esc)"));
        close.Flat = true;
        close.AddThemeColorOverride("font_color", UiKit.Dim);
        row.AddChild(title);
        row.AddChild(close);
        bar.AddChild(row);
        bar.GuiInput += BarInput;
        AddChild(bar);

        Body = UiKit.Col(8);
        Body.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Body.OffsetLeft = 14; Body.OffsetRight = -14; Body.OffsetTop = 42; Body.OffsetBottom = -12;
        Body.MouseFilter = MouseFilterEnum.Pass;
        Body.MinimumSizeChanged += Fit;
        AddChild(Body);

        var grip = new Label { Text = "◢", MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.Fdiagsize, TooltipText = "" };
        grip.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.25f));
        grip.AddThemeFontSizeOverride("font_size", 11);
        grip.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight);
        grip.OffsetLeft = -14; grip.OffsetTop = -16; grip.OffsetRight = -2; grip.OffsetBottom = -1;
        grip.GuiInput += GripInput;
        AddChild(grip);
    }

    public void SetTitle(string caption) => title.Text = caption;

    void BarInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            dragging = mb.Pressed;
            grab = mb.GlobalPosition - Position;
            if (!mb.Pressed) Ui.SaveSoon();
        }
        else if (e is InputEventMouseMotion mm && dragging)
        {
            Position = mm.GlobalPosition - grab;
            KeepInside();
        }
    }

    void GripInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            resizing = mb.Pressed;
            grab = mb.GlobalPosition - Size;
            if (!mb.Pressed) Ui.SaveSoon();
        }
        else if (e is InputEventMouseMotion mm && resizing)
        {
            var vs = GetViewportRect().Size;
            var s = mm.GlobalPosition - grab;
            Size = new Vector2(Math.Clamp(s.X, MinSize.X, vs.X - Position.X), Math.Clamp(s.Y, MinSize.Y, vs.Y - Position.Y));
        }
    }

    // The window is never smaller than what its content needs.
    void Fit()
    {
        var need = Body.GetCombinedMinimumSize() + new Vector2(28, 54);
        if (Size.X < need.X || Size.Y < need.Y) Size = new Vector2(Math.Max(Size.X, need.X), Math.Max(Size.Y, need.Y));
    }

    // Keeps the window on screen (all of it if it fits).
    public void KeepInside()
    {
        var vs = GetViewportRect().Size;
        if (vs.X <= 0) return;
        Fit();
        Size = new Vector2(Math.Min(Size.X, vs.X), Math.Min(Size.Y, vs.Y));
        Position = new Vector2(Math.Clamp(Position.X, 0, Math.Max(0, vs.X - Size.X)), Math.Clamp(Position.Y, 0, Math.Max(0, vs.Y - Size.Y)));
    }

    public bool IsOpen => Visible;

    public void Open()
    {
        if (!Visible)
        {
            Visible = true;
            KeepInside();
            OnOpen();
            Opened?.Invoke();
            Ui.SaveSoon();
        }
        Ui.Raise(this);
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        var focus = GetViewport()?.GuiGetFocusOwner();
        if (focus != null && IsAncestorOf(focus)) focus.ReleaseFocus();
        OnClose();
        Closed?.Invoke();
        Ui.SaveSoon();
    }

    public void Toggle() { if (Visible && Ui.Top == this) Close(); else Open(); }

    public virtual void ShowView(string view) { }   // --open window:view
    protected virtual void OnOpen() { }
    protected virtual void OnClose() { }
}
