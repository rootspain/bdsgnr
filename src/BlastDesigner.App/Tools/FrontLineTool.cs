using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.App.Tools;

/// <summary>
/// Circles of the selected two-segment fronts, shared by the front tool and the Select tool: the vertex circle turns the
/// front about its centre, a circle at the base changes its angle and length (the other end mirrors it). The front is
/// changed, and its holes reprogrammed, in one undo step when the button is released; Esc drops the drag.
/// </summary>
public sealed class FrontLineHandles
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

    public FrontLineHandles(MainViewModel vm, Func<bool> visible)
    {
        _vm = vm;
        Overlay = new FrontLineHandlesOverlay(() => visible() ? vm.EditableFrontLines() : new List<TimingLineEntity>(), () => new SKColor(Look.Accent.Color.ToUInt32()));
    }

    public FrontLineHandlesOverlay Overlay { get; }

    /// <summary>A circle was pressed and its drag is not over yet.</summary>
    public bool Active => _drag != null;

    /// <summary>The circle of a selected front at a screen point: the nearest one, the vertex first when they overlap.</summary>
    public (TimingLineEntity Line, int Handle)? HandleAt(double sx, double sy)
    {
        (TimingLineEntity, int)? best = null;
        var bestD = VertexGripsOverlay.Reach + 1;
        foreach (var line in _vm.EditableFrontLines())
            foreach (var i in new[] { 1, 0, 2 })
            {
                var (x, y) = _vm.View.Camera.ToScreen(line.Points[i]);
                var d = Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy));
                if (d < bestD) { bestD = d; best = (line, i); }
            }
        return best;
    }

    /// <summary>Marks the circle under the cursor (no button down).</summary>
    public void Hover(ToolPointer p) => Overlay.Hot = HandleAt(p.X, p.Y) is { } hit ? (hit.Line.Id, hit.Handle) : null;

    /// <summary>Takes a press on a circle; false when there is none at the point (the tool goes on as usual).</summary>
    public bool Press(ToolPointer p)
    {
        if (!p.Left || p.Modifiers.HasFlag(KeyModifiers.Control) || HandleAt(p.X, p.Y) is not { } hit) return false;
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
        if (handle == 1)
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
        else
        {
            _shown = Chevron.Reshape(pts, cam.ToWorld(p.X, p.Y, pts[1].Z));
            preview.Labels.Add((_shown[handle], $"{Chevron.AngleDeg(_shown):0.0}° · {Chevron.Length(_shown):0.00} m"));
        }
        Overlay.Dragged = (line, _shown, handle == 1);
    }

    /// <summary>Applies the drag (with undo, reprogramming the front's holes); a press without a drag changes nothing.</summary>
    public void Release()
    {
        if (_drag is not { } drag) return;
        var (line, handle) = drag;
        var shown = _dragging ? _shown : null;
        Reset();
        if (shown != null) _vm.SetTimingLinePoints(line.Id, shown, handle == 1 ? "Rotate timing line" : "Reshape timing line");
    }

    public void Cancel() => Reset();

    private void Reset()
    {
        if (_drag is { } drag && _dragging) _vm.HiddenEntities.Remove(drag.Line.Id);
        _drag = null;
        _down = null;
        _dragging = false;
        _shown = null;
        Overlay.Dragged = null;
    }
}

/// <summary>
/// Timing front tool: two segments meeting at a vertex at 30°, with a ray from the vertex outwards that shows where the times
/// grow. A press places the vertex; dragging with the button held pulls the base after the cursor (direction and length),
/// a click without a drag gives segments 60 px long at the present zoom, pointing up the screen. On release the timing
/// line window opens. A selected front is turned and reshaped by its circles and moved by dragging the line itself.
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
    public override string Hint => "Press to place the vertex of the front and drag to set its direction and length (a click gives 60 px); the ray shows where the times grow. On a selected front drag the accent circle at the vertex to turn it, a circle at the base to change its angle and length, the line to move it.";

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
        if (_handles.Press(p)) return;
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
        if (_handles.Active) { _handles.Release(); Preview.Clear(); return; }
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

    /// <summary>The front as the pointer gives it now, shown with its circles and its ray.</summary>
    private void UpdateShape(ToolPointer p)
    {
        var cam = View.Camera;
        Vec3 dir;
        double length;
        if (_stretching)
        {
            // the base follows the cursor: the ray points from it through the vertex
            var v = (_apex - cam.ToWorld(p.X, p.Y, _apex.Z)).WithZ(0);
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
        Preview.Guides.Add((new[] { _apex, _apex + dir * (cam.Width / Math.Max(cam.Scale, 1e-9)) }, PreviewColor));
        foreach (var q in _shape) Preview.Points.Add((q, PreviewColor));
        // bearing of the ray, clockwise from north
        var bearing = (Math.Atan2(dir.X, dir.Y) / GeoMath.Deg + 360) % 360;
        Preview.Labels.Add((_shape[0], $"{bearing:0.0}° · {Chevron.Length(_shape):0.00} m"));
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
