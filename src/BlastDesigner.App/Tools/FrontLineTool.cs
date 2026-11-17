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
/// Timing front tool, drawn by clicks. The first click puts the start of a line, the second its end. A click on that end
/// (or Enter) finishes a line: its ends are put in the order that makes the times grow to the side of most holes. A click
/// anywhere else puts the end of a second segment and finishes a chevron, its vertex at the end of the first line, its
/// segments as long and at the angle they were clicked. Backspace takes the last point back, Esc drops the input.
/// When the front is finished the timing line window opens. A selected front is changed by its circles and moved by
/// dragging the line itself, while no front is being drawn.
/// </summary>
public sealed class FrontLineTool : Tool
{
    private const double DragPx = 4;
    private const double DirectionStepDeg = 15;
    /// <summary>Radius of the vertices clicked, px: larger than the points of other tools, as the circles of a selected front.</summary>
    public const float VertexRadius = 7;

    private readonly Action<List<Vec3>> _finish;
    private readonly FrontLineHandles _handles;
    // the front being drawn: the points clicked, and the cursor
    private readonly List<Vec3> _points = new();
    private ToolPointer? _cursor;
    // a selected front being moved
    private ToolPointer? _moveDown;
    private Vec3 _moveHandle;
    private bool _moving;
    private Vec3? _moveShown;

    public FrontLineTool(MainViewModel vm, Action<List<Vec3>> finish) : base(vm)
    {
        _finish = finish;
        _handles = new FrontLineHandles(vm, () => Preview.Moving == null && _points.Count == 0);
    }

    public override string Name => "Timing line";
    public override string Hint =>
        "Click the start and the end of the line. A click on its end (or Enter) finishes a line; a click elsewhere draws a second segment and finishes a chevron, its vertex at the end of the line. Backspace takes the last point back, Esc drops the input. On a selected front drag a circle to change it, the line to move it.";

    /// <summary>The points clicked of the front being drawn: none, its start, or the start and the end of its first line.</summary>
    public IReadOnlyList<Vec3> Points => _points;

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

    protected override bool HasPendingInput => _points.Count > 0 || _moveDown != null || _handles.Active;

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left) return;
        if (_points.Count == 0)
        {
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
            _points.Add(p.Snapped);
            _cursor = p;
            UpdatePreview();
            return;
        }
        // a click on the end of the first line finishes it as a line
        if (_points.Count == 2 && OnEnd(p)) { FinishLine(); return; }
        var at = Next(p);
        // a point on the last one adds nothing: the shape needs a length
        if (Vec3.Distance2D(_points[^1], at) < Chevron.MinLength) return;
        _points.Add(at);
        if (_points.Count == 3) { FinishChevron(); return; }
        _cursor = p;
        UpdatePreview();
    }

    public override void PointerMoved(ToolPointer p)
    {
        if (_handles.Active) { _handles.Drag(p, Preview); return; }
        if (_moveDown != null) { MovePreview(p); return; }
        if (_points.Count == 0) { _handles.Hover(p); return; }
        _cursor = p;
        UpdatePreview();
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
        }
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        // Enter with nothing to finish is left to the window, which closes the tool
        if (key is Key.Enter or Key.Return) return FinishEntry();
        if (key == Key.Back && _points.Count > 0)
        {
            _points.RemoveAt(_points.Count - 1);
            if (_points.Count == 0) Preview.Clear(); else UpdatePreview();
            View.Redraw();
            return true;
        }
        return base.KeyDown(key, mods);
    }

    /// <summary>Enter: the start and the end clicked finish a line; with the start alone the input waits for its end.</summary>
    public override bool FinishEntry()
    {
        if (_points.Count == 0) return false;
        if (_points.Count == 1) { Vm.SetStatus(Loc.T("Click the end of the line")); return true; }
        FinishLine();
        return true;
    }

    public override void Cancel()
    {
        _points.Clear();
        _cursor = null;
        _moveDown = null;
        _moving = false;
        _handles.Cancel();
        base.Cancel();
    }

    /// <summary>The point a click or the cursor gives after the last point: snapped, with Shift turned to a step of 15° from it.</summary>
    private Vec3 Next(ToolPointer p)
    {
        var last = _points[^1];
        var at = p.Snapped.WithZ(last.Z);
        return p.Modifiers.HasFlag(KeyModifiers.Shift) ? Stepped(last, at) : at;
    }

    /// <summary>The pointer on the end of the first line, within the pick radius on the screen.</summary>
    private bool OnEnd(ToolPointer p)
    {
        var (x, y) = View.Camera.ToScreen(_points[1]);
        return Math.Sqrt((x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y)) <= Math.Max(View.TargetRadius, VertexGripsOverlay.Reach);
    }

    /// <summary>A line: its ends in order toward most of the holes (the selected ones, or all).</summary>
    private void FinishLine()
    {
        var holes = Vm.SelectedOrAllHoles().Where(h => !h.IsDummy).Select(h => h.Collar).ToList();
        Finish(EbsTimingLine.FacingLine(_points[0], _points[1], holes));
    }

    /// <summary>A chevron: the vertex at the end of the first line, the ends at the start of it and at the last click.</summary>
    private void FinishChevron() => Finish(new List<Vec3> { _points[0], _points[1], _points[2] });

    private void Finish(List<Vec3> pts)
    {
        _points.Clear();
        _cursor = null;
        Preview.Clear();
        // the input is over: back to Select (V), where the front made is selected and can be changed at once
        Vm.SelectTool();
        try { _finish(pts); }
        catch (Exception ex) { Dialogs.ShowError(ex); }
        View.Redraw();
    }

    /// <summary>The points clicked and the segment to the cursor, with its bearing and length, and on the second segment the angle at the vertex.</summary>
    private void UpdatePreview()
    {
        Preview.Lines.Clear();
        Preview.Guides.Clear();
        Preview.Points.Clear();
        Preview.Labels.Clear();
        var shown = _points.ToList();
        if (_cursor is { } c) shown.Add(Next(c));
        if (shown.Count > 1) Preview.Lines.Add((shown, false, PreviewColor));
        // the vertices large enough to be clicked: the end of the line larger still under the cursor, where a click finishes it
        Preview.PointRadius = VertexRadius;
        Preview.HotPoint = _points.Count == 2 && _cursor is { } over && OnEnd(over) ? _points[1] : null;
        foreach (var q in _points) Preview.Points.Add((q, PreviewColor));
        if (shown.Count < 2) return;
        var from = shown[^2];
        var to = shown[^1];
        var d = (to - from).WithZ(0);
        var bearing = (Math.Atan2(d.X, d.Y) / GeoMath.Deg + 360) % 360;
        var label = $"{bearing:0.0}° · {d.Length2D:0.00} m";
        if (shown.Count == 3 && d.Length2D > 1e-9) label = $"{Chevron.AngleDeg(shown):0.0}° · {d.Length2D:0.00} m";
        Preview.Labels.Add((to, label));
        View.Redraw();
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
