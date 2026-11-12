using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.App.Tools;

/// <summary>
/// Round grips on the vertices of the selected lines: a drag of a grip moves its vertex; with Shift a click on a selected line
/// adds a vertex there, with Ctrl a click on a grip deletes its vertex. A click on a grip without a drag selects as anywhere else.
/// </summary>
public sealed class VertexGrips : ISelectGesture
{
    private readonly MainViewModel _vm;
    private ToolPointer? _down;
    /// <summary>Vertex whose grip was pressed: a drag moves it, a click selects as anywhere else.</summary>
    private (PolylineEntity Line, int Vertex)? _grip;
    /// <summary>Where the last drag preview put the vertex.</summary>
    private Vec3? _gripTo;
    /// <summary>Spot of a selected line pressed with Shift: a click adds a vertex there, a drag is the box as anywhere else.</summary>
    private (PolylineEntity Line, int At, Vec3 Point)? _newVertex;
    /// <summary>Grip pressed with Ctrl: a click deletes its vertex.</summary>
    private (PolylineEntity Line, int Vertex)? _oldVertex;

    public VertexGrips(MainViewModel vm, Func<bool> visible)
    {
        _vm = vm;
        Overlay = new VertexGripsOverlay(() => visible() ? vm.EditableLines() : new List<PolylineEntity>(), () => new SKColor(vm.Settings.SelectionColor));
    }

    public VertexGripsOverlay Overlay { get; }
    IOverlay? ISelectGesture.Overlay => Overlay;

    private Viewport View => _vm.View;

    /// <summary>A grip was pressed without Shift or Ctrl: a drag moves its vertex.</summary>
    public bool Active => _grip != null;

    /// <summary>The press adds or deletes a vertex when it is let go without a drag; it is no double click then.</summary>
    public bool EditsLine => _newVertex != null || _oldVertex != null;

    public void Hover(ToolPointer p) => Overlay.Hot = GripAt(p.X, p.Y);

    /// <summary>Notes what the press does to a selected line; true when it took a grip to drag.</summary>
    public bool TryPress(ToolPointer p)
    {
        _down = p;
        var shift = p.Modifiers.HasFlag(KeyModifiers.Shift);
        var control = p.Modifiers.HasFlag(KeyModifiers.Control);
        var free = !shift && !control;
        _grip = free && p.ClickCount < 2 ? GripAt(p.X, p.Y) : null;
        _gripTo = null;
        _oldVertex = control && !shift ? GripAt(p.X, p.Y) : null;
        _newVertex = shift && !control && GripAt(p.X, p.Y) == null ? SegmentAt(p.X, p.Y) : null;
        return _grip != null;
    }

    /// <summary>A press let go without a drag: adds or deletes the vertex it was for; false when it was for none.</summary>
    public bool Click()
    {
        if (_newVertex is { } added)
        {
            _newVertex = null;
            _vm.AddVertex(added.Line.Id, added.At, added.Point);
            return true;
        }
        if (_oldVertex is { } deleted)
        {
            _oldVertex = null;
            Overlay.Hot = null;
            _vm.DeleteVertex(deleted.Line.Id, deleted.Vertex);
            return true;
        }
        return false;
    }

    public void Drag(ToolPointer p, PreviewOverlay preview)
    {
        if (_grip is not { } grip) return;
        var from = grip.Line.Points[grip.Vertex];
        var (to, snap) = VertexTarget(p, grip.Line, from);
        _gripTo = to;
        preview.Snap = snap;
        var points = grip.Line.Points.ToList();
        points[grip.Vertex] = to;
        preview.Lines.Clear();
        preview.Points.Clear();
        preview.Lines.Add((points, grip.Line.Closed, Tool.PreviewColor));
        preview.Points.Add((to, Tool.PreviewColor));
        preview.Labels.Add((to, $"Δ {(to - from).Length2D:0.00} m"));
    }

    public void Release(ToolPointer p)
    {
        if (_grip is not { } grip) return;
        _grip = null;
        if (_gripTo is Vec3 to) _vm.MoveVertex(grip.Line.Id, grip.Vertex, to);
    }

    public void Cancel()
    {
        _down = null;
        _grip = null;
        _gripTo = null;
        _newVertex = null;
        _oldVertex = null;
    }

    /// <summary>The grip of a selected line at a screen point; the nearest one.</summary>
    private (PolylineEntity Line, int Vertex)? GripAt(double sx, double sy)
    {
        (PolylineEntity, int)? best = null;
        var bestD = VertexGripsOverlay.Reach;
        foreach (var line in _vm.EditableLines())
            for (int i = 0; i < line.Points.Count; i++)
            {
                var (x, y) = View.Camera.ToScreen(line.Points[i]);
                var d = Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy));
                if (d < bestD) { bestD = d; best = (line, i); }
            }
        return best;
    }

    /// <summary>The nearest spot of a selected line to a screen point, within the pick radius, with the index a vertex there takes.</summary>
    private (PolylineEntity Line, int At, Vec3 Point)? SegmentAt(double sx, double sy)
    {
        (PolylineEntity, int, Vec3)? best = null;
        var bestD = View.TargetRadius;
        foreach (var line in _vm.EditableLines())
        {
            int n = line.Points.Count;
            for (int i = 0; i < (line.Closed ? n : n - 1); i++)
            {
                Vec3 a = line.Points[i], b = line.Points[(i + 1) % n];
                var (ax, ay) = View.Camera.ToScreen(a);
                var (bx, by) = View.Camera.ToScreen(b);
                double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
                var t = l2 > 0 ? Math.Clamp(((sx - ax) * dx + (sy - ay) * dy) / l2, 0, 1) : 0;
                var d = Math.Sqrt(Math.Pow(ax + t * dx - sx, 2) + Math.Pow(ay + t * dy - sy, 2));
                // the view is a parallel projection: the same share of the segment on the screen and in the world, elevation too
                if (d < bestD) { bestD = d; best = (line, i + 1, Vec3.Lerp(a, b, t)); }
            }
        }
        return best;
    }

    /// <summary>Where a drag puts a vertex: as an object is moved, on another object under the cursor, else horizontally.</summary>
    private (Vec3 To, Vec3? Snap) VertexTarget(ToolPointer p, PolylineEntity line, Vec3 from)
    {
        var cam = View.Camera;
        var snap = View.SnapForDrag(p.X, p.Y, new[] { line.Id });
        return (snap ?? from + (cam.ToWorld(p.X, p.Y, from.Z) - cam.ToWorld(_down!.X, _down.Y, from.Z)).WithZ(0), snap);
    }
}
