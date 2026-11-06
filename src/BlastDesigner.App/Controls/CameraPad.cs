using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BlastDesigner.App.Services;

namespace BlastDesigner.App.Controls;

public enum CameraCommand { None, Up, Down, Left, Right, ZoomIn, ZoomOut, Lock }

/// <summary>A ring of four arrows with a lock in its middle and a column of zoom buttons beside it, for a drawing that has no mouse of its own.</summary>
public sealed class CameraPad : Control
{
    private const double Ring = 55.5, Hub = 18, Arrow = 37;
    private static readonly Point Middle = new(56, 56);
    private static readonly Rect Column = new(120.5, 12.5, 39, 87);

    private CameraCommand _hover, _pressed;
    private bool _locked, _canZoom = true, _tabbed;

    public CameraPad()
    {
        Width = 160;
        Height = 112;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>The button, and whether Shift asks for a fine step.</summary>
    public event Action<CameraCommand, bool>? Pressed;

    public bool Locked
    {
        get => _locked;
        set { _locked = value; ShowTip(); InvalidateVisual(); }
    }

    /// <summary>False leaves the zoom buttons there but dead: the scale of the drawing is set elsewhere.</summary>
    public bool CanZoom
    {
        get => _canZoom;
        set { _canZoom = value; ShowTip(); InvalidateVisual(); }
    }

    /// <summary>The button at a point of the control.</summary>
    public static CameraCommand At(Point p)
    {
        if (Column.Contains(p)) return p.Y < Column.Center.Y ? CameraCommand.ZoomIn : CameraCommand.ZoomOut;
        double x = p.X - Middle.X, y = p.Y - Middle.Y, r = Math.Sqrt(x * x + y * y);
        if (r > Ring) return CameraCommand.None;
        if (r <= Hub) return CameraCommand.Lock;
        if (Math.Abs(x) > Math.Abs(y)) return x < 0 ? CameraCommand.Left : CameraCommand.Right;
        return y < 0 ? CameraCommand.Up : CameraCommand.Down;
    }

    /// <summary>What a press of a button does, whichever way it came.</summary>
    public void Press(CameraCommand command, bool fine = false)
    {
        if (command == CameraCommand.None || Dead(command)) return;
        Pressed?.Invoke(command, fine);
    }

    private static string FineTip => "\n" + Loc.Ui("With Shift the step is 4 times smaller");

    private string Tip(CameraCommand command) => command switch
    {
        CameraCommand.Up => Loc.Ui("Pan up") + FineTip,
        CameraCommand.Down => Loc.Ui("Pan down") + FineTip,
        CameraCommand.Left => Loc.Ui("Pan left") + FineTip,
        CameraCommand.Right => Loc.Ui("Pan right") + FineTip,
        CameraCommand.ZoomIn or CameraCommand.ZoomOut when !_canZoom => Loc.Ui("The scale is set in the options of the plan"),
        CameraCommand.ZoomIn => Loc.Ui("Zoom in") + FineTip,
        CameraCommand.ZoomOut => Loc.Ui("Zoom out") + FineTip,
        _ => _locked ? Loc.Ui("The frame is locked: click to fit the drawing to the objects that show again") : Loc.Ui("Lock the frame: the check boxes will not change the scale and position of the drawing")
    };

    private void ShowTip()
    {
        if (_hover != CameraCommand.None) ToolTip.SetTip(this, Tip(_hover));
    }

    private void Hover(CameraCommand command)
    {
        if (command == _hover) return;
        _hover = command;
        ShowTip();
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Hover(At(e.GetPosition(this)));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Hover(CameraCommand.None);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = At(e.GetPosition(this));
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var pressed = _pressed;
        _pressed = CameraCommand.None;
        e.Pointer.Capture(null);
        InvalidateVisual();
        if (pressed == At(e.GetPosition(this))) Press(pressed, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        _tabbed = e.NavigationMethod == NavigationMethod.Tab;
        InvalidateVisual();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _tabbed = false;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var command = e.Key switch
        {
            Key.Up => CameraCommand.Up,
            Key.Down => CameraCommand.Down,
            Key.Left => CameraCommand.Left,
            Key.Right => CameraCommand.Right,
            Key.Add or Key.OemPlus => CameraCommand.ZoomIn,
            Key.Subtract or Key.OemMinus => CameraCommand.ZoomOut,
            Key.Space => CameraCommand.Lock,
            _ => CameraCommand.None
        };
        if (command == CameraCommand.None) return;
        Press(command, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }

    private bool Dead(CameraCommand command) => !_canZoom && command is CameraCommand.ZoomIn or CameraCommand.ZoomOut;

    private IBrush? Fill(CameraCommand command) =>
        Dead(command) ? null : _pressed == command ? Look.Accent : _hover == command ? Look.Hover : null;

    private IBrush Ink(CameraCommand command) =>
        Dead(command) ? Look.Faint : _pressed == command ? Look.OnAccent : _hover == command ? Look.Accent : Look.TextSoft;

    private static Point Polar(double radius, double degrees) =>
        new(Middle.X + radius * Math.Cos(degrees * Math.PI / 180), Middle.Y + radius * Math.Sin(degrees * Math.PI / 180));

    /// <summary>The part of the ring around a direction, in degrees clockwise from the right.</summary>
    private static Geometry Sector(double degrees)
    {
        var sector = new StreamGeometry();
        using (var s = sector.Open())
        {
            s.BeginFigure(Polar(Hub, degrees - 45), true);
            s.LineTo(Polar(Ring, degrees - 45));
            s.ArcTo(Polar(Ring, degrees + 45), new Size(Ring, Ring), 0, false, SweepDirection.Clockwise);
            s.LineTo(Polar(Hub, degrees + 45));
            s.ArcTo(Polar(Hub, degrees - 45), new Size(Hub, Hub), 0, false, SweepDirection.CounterClockwise);
            s.EndFigure(true);
        }
        return sector;
    }

    private static void Glyph(DrawingContext c, string icon, Point at, IBrush ink, double size = 20)
    {
        var scale = size / 24;
        using (c.PushTransform(Matrix.CreateTranslation(-12, -12) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(at.X, at.Y)))
            c.DrawGeometry(null, new Pen(ink, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Look.Shape(icon));
    }

    public override void Render(DrawingContext c)
    {
        var strong = new Pen(Look.LineStrong);
        var line = new Pen(Look.Line);

        c.DrawEllipse(Look.Panel, _tabbed ? new Pen(Look.Blue, 2) : strong, Middle, Ring, Ring);
        foreach (var (command, degrees, icon) in new[] { (CameraCommand.Right, 0, "right"), (CameraCommand.Down, 90, "down"), (CameraCommand.Left, 180, "left"), (CameraCommand.Up, 270, "up") })
        {
            if (Fill(command) is { } fill) c.DrawGeometry(fill, null, Sector(degrees));
            Glyph(c, icon, Polar(Arrow, degrees), Ink(command));
            c.DrawLine(line, Polar(Hub, degrees + 45), Polar(Ring, degrees + 45));
        }

        var pressed = _pressed == CameraCommand.Lock;
        var hub = pressed ? Look.Accent : _locked ? Look.AccentSoft : _hover == CameraCommand.Lock ? Look.Hover : Look.Ribbon;
        c.DrawEllipse(hub, _locked ? new Pen(Look.Accent) : strong, Middle, Hub, Hub);
        Glyph(c, _locked ? "lock" : "unlock", Middle, pressed ? Look.OnAccent : _locked || _hover == CameraCommand.Lock ? Look.Accent : Look.TextSoft, 18);

        var half = Column.Height / 2;
        var rounded = new RoundedRect(Column, Column.Width / 2);
        using (c.PushClip(rounded))
        {
            c.DrawRectangle(Look.Panel, null, Column);
            if (Fill(CameraCommand.ZoomIn) is { } zoomIn) c.DrawRectangle(zoomIn, null, new Rect(Column.X, Column.Y, Column.Width, half));
            if (Fill(CameraCommand.ZoomOut) is { } zoomOut) c.DrawRectangle(zoomOut, null, new Rect(Column.X, Column.Y + half, Column.Width, half));
        }
        c.DrawLine(line, new Point(Column.Left, Column.Center.Y), new Point(Column.Right, Column.Center.Y));
        c.DrawRectangle(null, strong, Column, Column.Width / 2, Column.Width / 2);
        Glyph(c, "plus", new Point(Column.Center.X, Column.Y + half / 2), Ink(CameraCommand.ZoomIn));
        Glyph(c, "minus", new Point(Column.Center.X, Column.Y + half * 1.5), Ink(CameraCommand.ZoomOut));
    }
}
