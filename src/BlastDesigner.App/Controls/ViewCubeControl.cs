using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.Views;
using BlastDesigner.IO;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Rendering;

namespace BlastDesigner.App.Controls;

/// <summary>
/// The view cube under the Plan and 3D buttons: a see-through cube that turns with the view's azimuth. A click on a visible
/// face, edge or corner looks at the blast from there.
/// </summary>
public sealed class ViewCubeControl : Control
{
    private readonly Viewport _view;
    private readonly Action<Vec3> _lookFrom;
    private ViewCube.Part? _hover;
    private (double Yaw, double Pitch) _drawn = (double.NaN, double.NaN);

    public ViewCubeControl(Viewport view, Action<Vec3> lookFrom, double size = 84)
    {
        _view = view;
        _lookFrom = lookFrom;
        Width = Height = size;
        Cursor = new Cursor(StandardCursorType.Hand);
        // the view turns by the mouse, the keys and the buttons; the cube follows whatever was drawn
        view.Rendering += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_drawn != (_view.Camera.Yaw, _view.Camera.Pitch)) InvalidateVisual();
        });
    }

    private Camera CubeCamera => ViewCube.CubeCamera(_view.Camera.Yaw, Width);

    /// <summary>The part the click at a point would look from; for tests.</summary>
    public ViewCube.Part? PartAt(Point p) => ViewCube.At(CubeCamera, p.X, p.Y);

    /// <summary>Looks from the part under <paramref name="p"/>; false when there is none.</summary>
    public bool ClickAt(Point p)
    {
        if (PartAt(p) is not { } part) return false;
        UsageLog.Track(UsageLog.Button, "View cube " + part.Face, "view");
        Ui.Safe(() => _lookFrom(part.From));
        InvalidateVisual();
        return true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var part = PartAt(e.GetPosition(this));
        if (part?.From == _hover?.From && part?.Face == _hover?.Face) return;
        _hover = part;
        ToolTip.SetTip(this, part == null ? null : Describe(part.From));
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (ClickAt(e.GetPosition(this))) e.Handled = true;
    }

    public override void Render(DrawingContext dc)
    {
        var cam = CubeCamera;
        _drawn = (_view.Camera.Yaw, _view.Camera.Pitch);
        // a background that takes the pointer, so the cube is not clicked through to the plan between its faces
        dc.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var fill = new SolidColorBrush(Look.Panel.Color, 0.55);
        var edge = new Pen(Look.LineStrong, 1);
        foreach (var f in ViewCube.VisibleFaces(cam))
            dc.DrawGeometry(fill, edge, Polygon(f.Outline));
        var shown = new SolidColorBrush(Look.Accent.Color, 0.35);
        foreach (var p in ViewCube.Parts(cam).Where(p => ViewCube.Shows(_view.Camera, p)))
            dc.DrawGeometry(shown, null, Polygon(p.Outline));
        if (_hover != null)
            foreach (var p in ViewCube.Parts(cam).Where(p => p.From == _hover.From))
                dc.DrawGeometry(new SolidColorBrush(Look.Accent.Color, 0.6), null, Polygon(p.Outline));
        foreach (var f in ViewCube.VisibleFaces(cam))
        {
            var text = new FormattedText(Label(f.Name), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), f.Name == "Top" ? 11 : 10, Look.Text);
            dc.DrawText(text, new Point(f.Centre.X - text.Width / 2, f.Centre.Y - text.Height / 2));
        }
    }

    private static StreamGeometry Polygon((float X, float Y)[] pts)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(new Point(pts[0].X, pts[0].Y), true);
        foreach (var p in pts.Skip(1)) c.LineTo(new Point(p.X, p.Y));
        c.EndFigure(true);
        return g;
    }

    private static string Label(string face) => face switch
    {
        "Top" => Loc.Ui("Top"),
        "N" => Loc.Ui("N"),
        "S" => Loc.Ui("S"),
        "E" => Loc.Ui("E"),
        _ => Loc.Ui("W")
    };

    /// <summary>The tip of a part: "View from the south-east, from above".</summary>
    public static string Describe(Vec3 from)
    {
        var ns = from.Y > 1e-6 ? "north" : from.Y < -1e-6 ? "south" : "";
        var ew = from.X > 1e-6 ? "east" : from.X < -1e-6 ? "west" : "";
        var side = ns.Length > 0 && ew.Length > 0 ? ns + "-" + ew : ns + ew;
        if (side.Length == 0) return Loc.Ui("View from above");
        var text = side switch
        {
            "north" => Loc.Ui("View from the north"),
            "south" => Loc.Ui("View from the south"),
            "east" => Loc.Ui("View from the east"),
            "west" => Loc.Ui("View from the west"),
            "north-east" => Loc.Ui("View from the north-east"),
            "north-west" => Loc.Ui("View from the north-west"),
            "south-east" => Loc.Ui("View from the south-east"),
            _ => Loc.Ui("View from the south-west")
        };
        return from.Z > 1e-6 ? Loc.F(Loc.Ui("{0}, from above"), text) : text;
    }
}
