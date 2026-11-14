using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.App.Tools;

/// <summary>
/// Circles of the selected fronts, shared by the front tool and the Select tool. On a chevron the vertex circle turns the
/// front about its centre, a circle at the base changes its angle and length (the other end mirrors it); on a line a circle
/// moves its end. The front is changed, and its holes reprogrammed, in one undo step when the button is released; Esc drops
/// the drag.
/// </summary>
public sealed class FrontLineHandles : ISelectGesture
{
    /// <summary>Screen distance a press has to travel before it is a drag, px.</summary>
    private const double DragPx = 4;
    /// <summary>Step of a turn with Shift held, degrees.</summary>
    private const double TurnStepDeg = 15;

    private readonly MainViewModel _vm;
    private (TimingLineEntity Line, int Handle)? _drag;
    private ToolPointer? _down;
    private bool _dragging;
    private List<Vec3>? _shown;
    /// <summary>The delays of a chevron while one of its ends is dragged: its segment's delay follows its length.</summary>
    private List<TimingLineSegment>? _segments;

    public FrontLineHandles(MainViewModel vm, Func<bool> visible)
    {
        _vm = vm;
        Overlay = new FrontLineHandlesOverlay(() => visible() ? vm.EditableFrontLines() : new List<TimingLineEntity>(), () => new SKColor(Look.Accent.Color.ToUInt32()));
    }

    public FrontLineHandlesOverlay Overlay { get; }
    IOverlay? ISelectGesture.Overlay => Overlay;

    /// <summary>The "handle" of the label in the middle of a straight front (a line front, a chevron opened into a straight line).</summary>
    public const int LabelHandle = FrontLineHandlesOverlay.LabelHandle;

    /// <summary>A circle was pressed and its drag is not over yet.</summary>
    public bool Active => _drag != null;

    /// <summary>
    /// The circle of a selected front at a screen point: the nearest one, the vertex first when they overlap. A chevron opened
    /// into a straight line has no vertex circle (<see cref="FrontLineHandlesOverlay.Circles"/>): it is not turned by it. Its
    /// label in the middle (<see cref="PlanRenderer.OnStraightLabel"/>) is <see cref="LabelHandle"/> instead, as the label of a
    /// line front: a press there converts it to a chevron.
    /// </summary>
    public (TimingLineEntity Line, int Handle)? HandleAt(double sx, double sy)
    {
        (TimingLineEntity, int)? best = null;
        var bestD = VertexGripsOverlay.Reach + 1;
        foreach (var line in _vm.EditableFrontLines())
            foreach (var i in FrontLineHandlesOverlay.Circles(line, line.Points))
            {
                var (x, y) = _vm.View.Camera.ToScreen(line.Points[i]);
                var d = Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy));
                if (d < bestD) { bestD = d; best = (line, i); }
            }
        if (best == null)
            foreach (var line in _vm.EditableFrontLines())
                if (line.CanConvertToChevron() && PlanRenderer.OnStraightLabel(_vm.View.Camera, line, sx, sy)) return (line, LabelHandle);
        return best;
    }

    /// <summary>Marks the circle under the cursor (no button down).</summary>
    public void Hover(ToolPointer p) => Overlay.Hot = HandleAt(p.X, p.Y) is { } hit ? (hit.Line.Id, hit.Handle) : null;

    /// <summary>Takes a press on a circle; false when there is none at the point (the tool goes on as usual).</summary>
    public bool TryPress(ToolPointer p)
    {
        if (!p.Left || p.Modifiers.HasFlag(KeyModifiers.Control) || HandleAt(p.X, p.Y) is not { } hit) return false;
        if (hit.Handle == LabelHandle)
        {
            // the label in the middle of a straight chevron: a press (or a drag) on it converts line to chevron at once
            Overlay.Hot = null;
            _vm.ConvertLinesToChevrons(new[] { hit.Line.Id });
            return true;
        }
        _drag = hit;
        _down = p;
        _dragging = false;
        _shown = null;
        return true;
    }

    public void Drag(ToolPointer p, PreviewOverlay preview)
    {
        if (_drag is not { } drag || _down == null) return;
        var (line, handle) = drag;
        if (!_dragging && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) <= DragPx) return;
        if (!_dragging)
        {
            _dragging = true;
            // the line itself is hidden while its preview is drawn by the overlay, as the timing line window does
            _vm.HiddenEntities.Add(line.Id);
        }
        var cam = _vm.View.Camera;
        var pts = line.Points;
        preview.Labels.Clear();
        if (line.Front == TimingFrontShape.Line)
        {
            var other = pts[1 - handle];
            var end = cam.ToWorld(p.X, p.Y, pts[handle].Z);
            if (p.Modifiers.HasFlag(KeyModifiers.Shift)) end = FrontLineTool.Stepped(other, end);
            _shown = pts.ToList();
            _shown[handle] = end;
            preview.Labels.Add((end, $"{Vec3.Distance2D(other, end):0.00} m"));
        }
        else if (handle == 1)
        {
            var center = Chevron.Center(pts);
            var cursor = cam.ToWorld(p.X, p.Y, center.Z);
            var from = Math.Atan2(pts[1].Y - center.Y, pts[1].X - center.X);
            var to = Math.Atan2(cursor.Y - center.Y, cursor.X - center.X);
            var turn = Math.IEEERemainder(to - from, 2 * Math.PI);
            if (p.Modifiers.HasFlag(KeyModifiers.Shift)) turn = Math.Round(turn / (TurnStepDeg * GeoMath.Deg)) * TurnStepDeg * GeoMath.Deg;
            _shown = Chevron.Rotate(pts, center, turn);
            preview.Labels.Add((_shown[1], $"Δ {turn / GeoMath.Deg:0.0}°"));
        }
        else if (Chevron.IsStraight(pts))
        {
            // a straight one: an end moves on its own as a line's does, the line stays straight, its vertex in its middle
            var other = pts[2 - handle];
            var end = cam.ToWorld(p.X, p.Y, pts[1].Z);
            if (p.Modifiers.HasFlag(KeyModifiers.Shift)) end = FrontLineTool.Stepped(other, end);
            _shown = Chevron.MoveStraightEnd(pts, handle, end);
            preview.Labels.Add((_shown[handle], $"{Vec3.Distance2D(other, _shown[handle]):0.00} m · 180°"));
        }
        else
        {
            // one end on its own: the vertex and the other end stay, the delay of the segment goes with its length
            _shown = Chevron.MoveEnd(pts, handle, cam.ToWorld(p.X, p.Y, pts[1].Z));
            _segments = ChevronDelays(line, _shown, handle);
            var seg = handle == 0 ? 0 : 1;
            var straight = Chevron.IsStraight(_shown) ? " · 180°" : "";
            preview.Labels.Add((_shown[handle], $"{_segments[seg].ReliefMsPerM:0.0} ms/m · {Vec3.Distance2D(_shown[handle], _shown[1]):0.00} m{straight}"));
        }
        var shownLine = line;
        if (_segments != null) { shownLine = line.WithPoints(line.Points); shownLine.Segments = _segments; }
        Overlay.Dragged = (shownLine, _shown);
    }

    /// <summary>Applies the drag (with undo, reprogramming the front's holes); a press without a drag changes nothing.</summary>
    public void Release(ToolPointer p)
    {
        if (_drag is not { } drag) return;
        var (line, handle) = drag;
        var shown = _dragging ? _shown : null;
        var segments = _segments;
        Reset();
        if (shown != null) _vm.SetTimingLinePoints(line.Id, shown, line.Front == TimingFrontShape.Chevron && handle == 1 ? "Rotate timing line" : "Reshape timing line", segments);
    }

    public void Cancel() => Reset();

    private void Reset()
    {
        if (_drag is { } drag && _dragging) _vm.HiddenEntities.Remove(drag.Line.Id);
        _drag = null;
        _down = null;
        _dragging = false;
        _shown = null;
        _segments = null;
        Overlay.Dragged = null;
    }

    /// <summary>
    /// The delays of a chevron's segments with end <paramref name="end"/> moved: that segment's delay grows with its length.
    /// Snapped into a straight line, the front has one delay, the first segment's, for both.
    /// </summary>
    public static List<TimingLineSegment> ChevronDelays(TimingLineEntity line, IReadOnlyList<Vec3> shown, int end)
    {
        var segments = line.Segments.ToList();
        var seg = end == 0 ? 0 : 1;
        if (segments.Count <= seg) return segments;
        if (Chevron.IsStraight(shown))
        {
            segments[1] = segments[1] with { ReliefMsPerM = segments[0].ReliefMsPerM };
            return segments;
        }
        var was = Vec3.Distance2D(line.Points[end], line.Points[1]);
        var now = Vec3.Distance2D(shown[end], shown[1]);
        if (was > 1e-9) segments[seg] = segments[seg] with { ReliefMsPerM = Math.Round(segments[seg].ReliefMsPerM * now / was, 2) };
        return segments;
    }
}

/// <summary>
/// Timing front tool, drawing the shape chosen in its panel (<see cref="MainViewModel.FrontShape"/>).
/// A chevron: two segments meeting at a vertex at a right angle (<see cref="Chevron.DefaultAngleDeg"/>), the times growing from the vertex outwards. A press places the vertex; dragging with the button held pulls the base after the cursor (direction and length),
/// a click without a drag gives segments 60 px long at the present zoom, pointing up the screen.
/// A line: a press places one end, the drag the other (a click gives 60 px across the screen); its ends are put in the order
/// that makes the times grow to the side of most holes, which an arrow shows.
/// On release the timing line window opens. A selected front is changed by its circles and moved by dragging the line itself.
/// </summary>
public sealed class FrontLineTool : Tool
{
    private const double DefaultLengthPx = 60;
    private const double DragPx = 4;
    private const double DirectionStepDeg = 15;

    private readonly Action<List<Vec3>> _finish;
    private readonly FrontLineHandles _handles;
    // the front being drawn
    private ToolPointer? _down;
    private Vec3 _apex;
    private bool _stretching;
    private bool _line;
    private List<Vec3> _holes = new();
    private List<Vec3>? _shape;
    // a selected front being moved
    private ToolPointer? _moveDown;
    private Vec3 _moveHandle;
    private bool _moving;
    private Vec3? _moveShown;

    public FrontLineTool(MainViewModel vm, Action<List<Vec3>> finish) : base(vm)
    {
        _finish = finish;
        _handles = new FrontLineHandles(vm, () => Preview.Moving == null && _down == null);
    }

    public override string Name => "Timing line";
    public override string Hint => Vm.FrontShape == TimingFrontShape.Line
        ? "Press at one end of the line and drag to the other (a click gives 60 px); the times grow from the line both ways, the holes at one distance from it fire together. On a selected line drag a circle to move its end, the label in its middle to make it a chevron, the line to move it."
        : "Press to place the vertex of the front and drag the way it is to point: the sides open behind the cursor (a click gives 60 px, upwards). On a selected front drag the accent circle at the vertex to turn it, a circle at the base to change its angle and length (drawn out to 180° it becomes a straight line, whose ends then move on their own), the line to move it.";

    /// <summary>The end of a segment from <paramref name="from"/> turned to the nearest step of 15°, its length kept.</summary>
    public static Vec3 Stepped(Vec3 from, Vec3 to)
    {
        var v = (to - from).WithZ(0);
        var step = DirectionStepDeg * GeoMath.Deg;
        var a = Math.Round(Math.Atan2(v.Y, v.X) / step) * step;
        return new Vec3(from.X + Math.Cos(a) * v.Length2D, from.Y + Math.Sin(a) * v.Length2D, to.Z);
    }

    public override void Activate()
    {
        base.Activate();
        View.Overlays.Add(_handles.Overlay);
    }

    public override void Deactivate()
    {
        _handles.Cancel();
        View.Overlays.Remove(_handles.Overlay);
        base.Deactivate();
    }

    protected override bool HasPendingInput => _down != null || _moveDown != null || _handles.Active;

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left) return;
        if (_handles.TryPress(p)) return;
        if (p.Hit is TimingLineEntity hit && Vm.Doc!.Selection.Contains(hit.Id) && Vm.Project!.FindLayer(hit.LayerId) is not { Locked: true })
        {
            _moveDown = p;
            _moving = false;
            _moveShown = null;
            var cam = View.Camera;
            _moveHandle = hit.Points.DefaultIfEmpty(p.World).MinBy(v =>
            {
                var (x, y) = cam.ToScreen(v);
                return (x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y);
            });
            return;
        }
        _down = p;
        _apex = p.Snapped;
        _stretching = false;
        _line = Vm.FrontShape == TimingFrontShape.Line;
        _holes = _line ? Vm.SelectedOrAllHoles().Where(h => !h.IsDummy).Select(h => h.Collar).ToList() : new();
        UpdateShape(p);
    }

    public override void PointerMoved(ToolPointer p)
    {
        if (_handles.Active) { _handles.Drag(p, Preview); return; }
        if (_moveDown != null) { MovePreview(p); return; }
        if (_down == null) { _handles.Hover(p); return; }
        if (!_stretching && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) > DragPx) _stretching = true;
        UpdateShape(p);
    }

    public override void PointerReleased(ToolPointer p)
    {
        if (_handles.Active) { _handles.Release(p); Preview.Clear(); return; }
        if (_moveDown != null)
        {
            var delta = _moving ? _moveShown : null;
            _moveDown = null;
            _moving = false;
            Preview.Clear();
            if (delta is Vec3 d) Vm.MoveSelection(d);
            return;
        }
        if (_down == null) return;
        UpdateShape(p);
        var pts = _shape;
        _down = null;
        _shape = null;
        Preview.Clear();
        if (pts == null) return;
        try { _finish(pts); }
        catch (Exception ex) { Dialogs.ShowError(ex); }
        View.Redraw();
    }

    public override void Cancel()
    {
        _down = null;
        _shape = null;
        _moveDown = null;
        _moving = false;
        _handles.Cancel();
        base.Cancel();
    }

    /// <summary>The front as the pointer gives it now, shown with its circles.</summary>
    private void UpdateShape(ToolPointer p)
    {
        if (_line) { UpdateLine(p); return; }
        var cam = View.Camera;
        Vec3 dir;
        double length;
        if (_stretching)
        {
            // the front follows the cursor: dragged up, it points up and its sides open below the vertex
            var v = (cam.ToWorld(p.X, p.Y, _apex.Z) - _apex).WithZ(0);
            length = v.Length2D;
            dir = v.Normalized();
            if (p.Modifiers.HasFlag(KeyModifiers.Shift) && dir != Vec3.Zero)
            {
                var step = DirectionStepDeg * GeoMath.Deg;
                var a = Math.Round(Math.Atan2(dir.Y, dir.X) / step) * step;
                dir = new Vec3(Math.Cos(a), Math.Sin(a), 0);
            }
        }
        else
        {
            length = DefaultLengthPx / Math.Max(cam.Scale, 1e-9);
            dir = cam.Up.WithZ(0).Normalized();
        }
        if (dir == Vec3.Zero) dir = new Vec3(0, 1, 0);
        _shape = Chevron.Build(_apex, dir, Chevron.DefaultAngleDeg, length);

        Preview.Lines.Clear();
        Preview.Guides.Clear();
        Preview.Points.Clear();
        Preview.Labels.Clear();
        Preview.Lines.Add((_shape, false, PreviewColor));
        foreach (var q in _shape) Preview.Points.Add((q, PreviewColor));
        // bearing the front points to, clockwise from north
        var bearing = (Math.Atan2(dir.X, dir.Y) / GeoMath.Deg + 360) % 360;
        Preview.Labels.Add((_shape[0], $"{bearing:0.0}° · {Chevron.Length(_shape):0.00} m"));
    }

    /// <summary>The line as the pointer gives it now, its ends in order, with an arrow from its middle to the side the times grow to.</summary>
    private void UpdateLine(ToolPointer p)
    {
        var cam = View.Camera;
        var end = _stretching ? cam.ToWorld(p.X, p.Y, _apex.Z) : _apex;
        if (_stretching && p.Modifiers.HasFlag(KeyModifiers.Shift)) end = Stepped(_apex, end);
        if (Vec3.Distance2D(_apex, end) < Chevron.MinLength)
        {
            var up = cam.Up.WithZ(0).Normalized();
            if (up == Vec3.Zero) up = new Vec3(0, 1, 0);
            end = _apex + new Vec3(up.Y, -up.X, 0) * (DefaultLengthPx / Math.Max(cam.Scale, 1e-9));
        }
        _shape = EbsTimingLine.FacingLine(_apex, end, _holes);

        Preview.Lines.Clear();
        Preview.Guides.Clear();
        Preview.Points.Clear();
        Preview.Labels.Clear();
        Preview.Lines.Add((_shape, false, PreviewColor));
        foreach (var q in _shape) Preview.Points.Add((q, PreviewColor));
        // the times grow from the line both ways: no arrow. Its ends are still put in order toward most holes, the way it
        // bends when it is made a chevron
        var d = (_shape[1] - _shape[0]).WithZ(0);
        var length = d.Length2D;
        var bearing = (Math.Atan2(d.X, d.Y) / GeoMath.Deg + 360) % 360;
        Preview.Labels.Add((_shape[1], $"{bearing:0.0}° · {length:0.00} m"));
    }

    /// <summary>The selected objects follow the cursor; the move is applied (with undo and reprogramming) on release.</summary>
    private void MovePreview(ToolPointer p)
    {
        var down = _moveDown!;
        if (!_moving && Math.Abs(p.X - down.X) + Math.Abs(p.Y - down.Y) <= DragPx) return;
        _moving = true;
        var cam = View.Camera;
        var selection = Vm.Doc!.Selection;
        var snap = View.SnapForDrag(p.X, p.Y, selection);
        var target = snap ?? _moveHandle + (cam.ToWorld(p.X, p.Y, _moveHandle.Z) - cam.ToWorld(down.X, down.Y, _moveHandle.Z)).WithZ(0);
        var delta = target - _moveHandle;
        _moveShown = delta;
        Preview.Snap = snap;
        Preview.Moving ??= InitiationLinks.WithLinked(Vm.Project!, selection);
        Vm.RaiseDragPreview();
        Preview.MoveOffset = delta;
        Preview.Labels.Clear();
        Preview.Labels.Add((_moveHandle + delta, $"Δ {delta.Length2D:0.00} m"));
    }
}
