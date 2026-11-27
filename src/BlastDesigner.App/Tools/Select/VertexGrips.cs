using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.App.Tools;

/// <summary>
/// Round grips on the vertices of the selected lines and on the two ends of the selected dimensions: a drag of a grip moves its
/// vertex (an end of a dimension on its own, its other end and offset kept), with Shift up or down (Z) on its own, the rest
/// of the line where it is; with Shift a click on a selected line adds a vertex there, with Ctrl a click on a grip of a line
/// deletes its vertex. A click on a grip of a line without a drag selects its vertex (its fields in the properties panel), with
/// Shift adds it to the vertices selected.
/// </summary>
public sealed class VertexGrips : ISelectGesture
{
    private readonly MainViewModel _vm;
    private ToolPointer? _down;
    /// <summary>Vertex whose grip was pressed: a drag moves it, a click selects as anywhere else.</summary>
    private (Entity Line, int Vertex)? _grip;
    /// <summary>Where the last drag preview put the vertex.</summary>
    private Vec3? _gripTo;
    /// <summary>Spot of a selected line pressed with Shift: a click adds a vertex there, a drag is the box as anywhere else.</summary>
    private (PolylineEntity Line, int At, Vec3 Point)? _newVertex;
    /// <summary>Grip pressed with Ctrl: a click deletes its vertex.</summary>
    private (PolylineEntity Line, int Vertex)? _oldVertex;
    /// <summary>
    /// Whether the drag goes up or down (Shift held), where the vertex was when it went over to that and the pointer of the
    /// switch: pressing or letting go of Shift on the way keeps the way made.
    /// </summary>
    private (bool Vertical, Vec3 Base, ToolPointer From)? _way;

    public VertexGrips(MainViewModel vm, Func<bool> visible)
    {
        _vm = vm;
        // a line being dragged shows its grips where they go (the copy drawn in its place), none left at the old place
        Overlay = new VertexGripsOverlay(() => visible()
            ? vm.EditableGripped().Select(e => _preview?.Replaced?.TryGetValue(e.Id, out var moved) == true ? moved : e).ToList()
            : new List<Entity>(), () => new SKColor(vm.Settings.SelectionColor))
        {
            Selected = () => vm.SelectedLineVertices.Select(v => (v.Line.Id, v.Vertex)).ToHashSet()
        };
    }

    public VertexGripsOverlay Overlay { get; }
    IOverlay? ISelectGesture.Overlay => Overlay;

    private Viewport View => _vm.View;

    /// <summary>A grip was pressed without Ctrl: a drag moves its vertex (with Shift up or down).</summary>
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
        // with Shift a grip is still taken: its vertex goes up or down on its own
        _grip = !control && p.ClickCount < 2 ? GripAt(p.X, p.Y) : null;
        _gripTo = null;
        _way = null;
        _oldVertex = control && !shift && GripAt(p.X, p.Y) is { Line: PolylineEntity line } old ? (line, old.Vertex) : null;
        _newVertex = shift && !control && GripAt(p.X, p.Y) == null ? SegmentAt(p.X, p.Y) : null;
        return _grip != null;
    }

    /// <summary>
    /// A press let go without a drag: adds or deletes the vertex it was for, or selects the vertex of the line whose grip it
    /// was on; false when it was for none.
    /// </summary>
    public bool Click()
    {
        if (_grip is { Line: PolylineEntity gripped } chosen)
        {
            _grip = null;
            // with Shift the vertex is added to the ones selected
            _vm.SelectVertex(gripped.Id, chosen.Vertex, _down?.Modifiers.HasFlag(KeyModifiers.Shift) == true);
            return true;
        }
        if (_newVertex is { } added)
        {
            _newVertex = null;
            _vm.ClearSelectedVertex();
            _vm.AddVertex(added.Line.Id, added.At, added.Point);
            return true;
        }
        if (_oldVertex is { } deleted)
        {
            _oldVertex = null;
            Overlay.Hot = null;
            _vm.ClearSelectedVertex();
            _vm.DeleteVertex(deleted.Line.Id, deleted.Vertex);
            return true;
        }
        return false;
    }

    public void Drag(ToolPointer p, PreviewOverlay preview)
    {
        if (_grip is not { } grip) return;
        var points = VertexGripsOverlay.GripPoints(grip.Line).ToList();
        var from = points[grip.Vertex];
        var vertical = p.Modifiers.HasFlag(KeyModifiers.Shift);
        if (_way is not { } way || way.Vertical != vertical) _way = way = (vertical, _gripTo ?? from, _way == null ? _down! : p);
        var (to, snap) = vertical ? (way.Base.WithZ(way.Base.Z + SelectionDrag.VerticalWay(View.Camera, way.From.Y, p.Y)), (Vec3?)null) : VertexTarget(p, grip.Line, way);
        // an end of an open line brought onto its other end closes it: the two ends are one vertex
        _closes = !vertical && ClosingEnd(grip, p) is { } other;
        if (_closes) { to = points[grip.Vertex == 0 ? points.Count - 1 : 0]; snap = to; }
        _gripTo = to;
        preview.Snap = snap;
        points[grip.Vertex] = to;
        preview.Lines.Clear();
        preview.Points.Clear();
        // the line itself drawn with the vertex where it goes, in place of the line as it was: no trace of the old place
        var moved = grip.Line.EditCopy();
        if (moved is PolylineEntity { } closing && _closes) { closing.Points.RemoveAt(grip.Vertex); closing.Closed = true; }
        else if (moved is PolylineEntity pl && grip.Vertex < pl.Points.Count) pl.Points[grip.Vertex] = to;
        else if (moved is DimensionEntity dim && grip.Vertex <= 1) { if (grip.Vertex == 0) dim.Start = to; else dim.End = to; }
        else moved = null;
        if (moved != null) { preview.Replaced = new Dictionary<Guid, Entity> { [grip.Line.Id] = moved }; _preview = preview; }
        else preview.Lines.Add((points, grip.Line is PolylineEntity { Closed: true }, Tool.PreviewColor));
        // a dimension says the length it will measure
        if (grip.Line is DimensionEntity) preview.Labels.Add((Vec3.Lerp(points[0], points[1], 0.5), $"{Vec3.Distance2D(points[0], points[1]):0.00} m"));
        preview.Points.Add((to, Tool.PreviewColor));
        preview.Labels.Add((to, _closes ? Services.Loc.T("Close line") : vertical ? $"Δ Z {to.Z - from.Z:+0.00;-0.00} m" : $"Δ {(to - from).Length2D:0.00} m"));
    }

    /// <summary>The drag closes the line: an end of an open line is on its other end.</summary>
    private bool _closes;

    /// <summary>
    /// The other end of an open line of four points or more when the cursor dragging one end is on it (within the reach of
    /// a grip); null otherwise. Three points closed would be a polygon of two.
    /// </summary>
    private Vec3? ClosingEnd((Entity Line, int Vertex) grip, ToolPointer p)
    {
        if (grip.Line is not PolylineEntity { Closed: false } line || line.Points.Count < 4) return null;
        var last = line.Points.Count - 1;
        if (grip.Vertex != 0 && grip.Vertex != last) return null;
        var other = line.Points[grip.Vertex == 0 ? last : 0];
        var (x, y) = View.Camera.ToScreen(other);
        return Math.Sqrt((x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y)) <= GripReach ? other : null;
    }

    /// <summary>The preview the dragged line is drawn in, to give it back when the drag ends.</summary>
    private PreviewOverlay? _preview;

    private void EndPreview()
    {
        if (_preview != null) _preview.Replaced = null;
        _preview = null;
    }

    public void Release(ToolPointer p)
    {
        if (_grip is not { } grip) return;
        _grip = null;
        EndPreview();
        _way = null;
        if (_gripTo is not Vec3 to) return;
        if (_closes && grip.Line is PolylineEntity closed)
        {
            // the end dragged goes, the line is closed; the vertex they joined at is the one selected
            _closes = false;
            // the joined vertex: the last of the line once its first is gone, or its first
            var joined = grip.Vertex == 0 ? closed.Points.Count - 2 : 0;
            _vm.CloseLineAtEnd(closed.Id, grip.Vertex);
            _vm.SelectVertex(closed.Id, joined);
            return;
        }
        _vm.MoveVertex(grip.Line.Id, grip.Vertex, to);
        // the vertex moved is the one selected: its fields show where it went
        if (grip.Line is PolylineEntity) _vm.SelectVertex(grip.Line.Id, grip.Vertex);
    }

    public void Cancel()
    {
        EndPreview();
        _down = null;
        _grip = null;
        _gripTo = null;
        _way = null;
        _closes = false;
        _newVertex = null;
        _oldVertex = null;
    }

    /// <summary>From the middle of the green snap square (12 px a side) to its corner, px.</summary>
    private const double SnapSquareReach = 8.6;

    /// <summary>
    /// How near a grip the cursor takes it, px: as far as the snap finds a vertex (the pick radius), and the whole of its
    /// green square.
    /// </summary>
    private double GripReach => Math.Max(Math.Max(VertexGripsOverlay.Reach, View.TargetRadius), SnapSquareReach);

    /// <summary>The grip of a selected line or dimension at a screen point; the nearest one.</summary>
    private (Entity Line, int Vertex)? GripAt(double sx, double sy)
    {
        (Entity, int)? best = null;
        // as far as the snap finds a vertex (the pick radius), and the whole of its green square: a cursor the square is
        // drawn round is on the vertex
        var bestD = GripReach;
        foreach (var line in _vm.EditableGripped())
        {
            var points = VertexGripsOverlay.GripPoints(line);
            for (int i = 0; i < points.Count; i++)
            {
                var (x, y) = View.Camera.ToScreen(points[i]);
                var d = Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy));
                if (d < bestD) { bestD = d; best = (line, i); }
            }
        }
        return best;
    }

    /// <summary>The nearest spot of a selected line to a screen point, within the pick radius, with the index a vertex there takes.</summary>
    private (PolylineEntity Line, int At, Vec3 Point)? SegmentAt(double sx, double sy)
    {
        (PolylineEntity, int, Vec3)? best = null;
        var bestD = View.TargetRadius;
        foreach (var line in _vm.EditableLines())
            if (Spot(View.Camera, line, sx, sy) is { } spot && spot.Distance < bestD) { bestD = spot.Distance; best = (line, spot.At, spot.Point); }
        return best;
    }

    /// <summary>
    /// The spot of a line nearest to a screen point: the index a vertex there takes, the point on the line and how far it
    /// is from the screen point, px; null for a line of less than two points.
    /// </summary>
    public static (int At, Vec3 Point, double Distance)? Spot(Camera cam, PolylineEntity line, double sx, double sy)
    {
        (int, Vec3, double)? best = null;
        int n = line.Points.Count;
        for (int i = 0; i < (line.Closed ? n : n - 1); i++)
        {
            Vec3 a = line.Points[i], b = line.Points[(i + 1) % n];
            var (ax, ay) = cam.ToScreen(a);
            var (bx, by) = cam.ToScreen(b);
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            var t = l2 > 0 ? Math.Clamp(((sx - ax) * dx + (sy - ay) * dy) / l2, 0, 1) : 0;
            var d = Math.Sqrt(Math.Pow(ax + t * dx - sx, 2) + Math.Pow(ay + t * dy - sy, 2));
            // the view is a parallel projection: the same share of the segment on the screen and in the world, elevation too
            if (best == null || d < best.Value.Item3) best = (i + 1, Vec3.Lerp(a, b, t), d);
        }
        return best;
    }

    /// <summary>
    /// Where a drag puts a vertex: as an object is moved, on another object under the cursor, else horizontally from where
    /// the way so far put it.
    /// </summary>
    private (Vec3 To, Vec3? Snap) VertexTarget(ToolPointer p, Entity line, (bool Vertical, Vec3 Base, ToolPointer From) way)
    {
        var cam = View.Camera;
        var snap = View.SnapForDrag(p.X, p.Y, new[] { line.Id });
        var from = way.Base;
        return (snap ?? from + (cam.ToWorld(p.X, p.Y, from.Z) - cam.ToWorld(way.From.X, way.From.Y, from.Z)).WithZ(0), snap);
    }
}
