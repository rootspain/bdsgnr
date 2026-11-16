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
/// The cut lines of the patterns with a selected hole that are cut into segments, on the plan: each crosses the outline and
/// stands out of it by <see cref="ExtendPx"/> pixels at both ends, where its round grips are. A grip moves its end along the
/// outline (with Shift only across the rows), the line itself moves parallel to itself; both stick to the middles between the
/// rows of the parent, Ctrl lets them go free. The drag shows where the holes would go; the release is one undo step. A
/// double click on a line turns it back along the rows; its menu also takes it away. Lines do not cross inside the outline.
/// </summary>
public sealed class SegmentLineGrips : ISelectGesture
{
    /// <summary>How far a line stands out of the outline at each end, px; its grips are there.</summary>
    public const double ExtendPx = 24;
    /// <summary>How near to a line a press takes it, px.</summary>
    private const double LineReach = 5;
    private const double DragPx = 4;
    private static readonly SKColor MovedColour = new(0xFF94A3B8);
    private static readonly SKColor Added = new(0xFF64748B);
    private static readonly SKColor Removed = new(0xFFDC2626);

    private readonly MainViewModel _vm;
    private readonly Func<bool> _visible;
    private ToolPointer? _down;
    private bool _dragging;
    private bool _busy;
    /// <summary>The line as it was when pressed.</summary>
    private SegmentLine? _from;

    public SegmentLineGrips(MainViewModel vm, Func<bool> visible)
    {
        _vm = vm;
        _visible = visible;
        Overlay = new SegmentLinesOverlay(this, () => PlanRenderer.Adapt(new SKColor(vm.Settings.SelectionColor)));
    }

    public SegmentLinesOverlay Overlay { get; }
    IOverlay? ISelectGesture.Overlay => Overlay;

    /// <summary>A line or a grip under the cursor: the pattern, the line and its end (0 the left one, 1 the right one, −1 the line).</summary>
    public (Guid Root, int Line, int End)? Hot { get; private set; }
    /// <summary>The line or grip being dragged.</summary>
    public (Guid Root, int Line, int End)? Pressed { get; private set; }
    /// <summary>Where the drag puts the pressed line.</summary>
    public SegmentLine? DraggedTo { get; private set; }

    public bool Active => Pressed != null;

    /// <summary>
    /// The patterns whose lines are shown: those cut into segments with a selected hole, while nothing is moved, on the plan
    /// and in a 3D view that looks down on the ground (as the circle that turns a pattern).
    /// </summary>
    public IReadOnlyList<HolePattern> Roots() =>
        _visible() && PatternRotateHandle.SeesTheGround(_vm.View.Camera) && _vm.Project is { } project
            ? PatternOverlay.Shown(_vm.Doc).Select(x => PatternTree.Root(project, x)).Distinct().Where(r => r.IsContainer).ToList() : new List<HolePattern>();

    /// <summary>Where the cursor is on the horizontal plane through the centre of a pattern: in 3D the drag follows it whatever is under it.</summary>
    private Vec3 OnGround(HolePattern root, ToolPointer p) => _vm.View.Camera.ToWorld(p.X, p.Y, root.Center.Z);

    /// <summary>A line on the screen: where it crosses the outline and its two grips beyond.</summary>
    public static ((float X, float Y) A, (float X, float Y) B) Grips(Camera cam, HolePattern root, SegmentLine line)
    {
        var (a, b) = PatternTree.LineEnds(root, line);
        var (ax, ay) = cam.ToScreen(a);
        var (bx, by) = cam.ToScreen(b);
        double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) (dx, dy, len) = (1, 0, 1);
        dx /= len;
        dy /= len;
        return (((float)(ax - dx * ExtendPx), (float)(ay - dy * ExtendPx)), ((float)(bx + dx * ExtendPx), (float)(by + dy * ExtendPx)));
    }

    /// <summary>The grip or line at a screen point.</summary>
    private (Guid Root, int Line, int End)? HitAt(double sx, double sy)
    {
        var cam = _vm.View.Camera;
        (Guid, int, int)? line = null;
        foreach (var root in Roots())
            for (int k = 0; k < root.SegmentLines.Count; k++)
            {
                var (a, b) = Grips(cam, root, root.SegmentLines[k]);
                if (Dist(a, sx, sy) <= VertexGripsOverlay.Reach) return (root.Id, k, 0);
                if (Dist(b, sx, sy) <= VertexGripsOverlay.Reach) return (root.Id, k, 1);
                if (line == null && ToSegment(a, b, sx, sy) <= LineReach) line = (root.Id, k, -1);
            }
        return line;
    }

    private static double Dist((float X, float Y) a, double x, double y) => Math.Sqrt((a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y));

    private static double ToSegment((float X, float Y) a, (float X, float Y) b, double x, double y)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        var t = l2 < 1e-9 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / l2, 0, 1);
        return Math.Sqrt(Math.Pow(a.X + t * dx - x, 2) + Math.Pow(a.Y + t * dy - y, 2));
    }

    public void Hover(ToolPointer p)
    {
        var hit = _busy ? null : HitAt(p.X, p.Y);
        if (hit != Hot) { Hot = hit; _vm.View.Redraw(); }
        if (hit == null) return;
        var text = hit.Value.End < 0
            ? Loc.T("Drag: move the cut line (Ctrl: free of the rows); double click: along the rows again")
            : Loc.T("Drag: move the end of the cut line along the outline (Shift: across the rows only, Ctrl: free of the rows)");
        _vm.View.Preview.Labels.Add((p.World, text));
    }

    public bool TryPress(ToolPointer p)
    {
        if (_busy || !p.Left || _vm.Doc?.IsReadOnly != false || HitAt(p.X, p.Y) is not { } hit) return false;
        if (p.ClickCount >= 2)
        {
            if (hit.End < 0) _ = Run(_vm.StraightenSegmentLine(hit.Root, hit.Line));
            return true;
        }
        if (_vm.Project?.FindPattern(hit.Root) is not { } root) return false;
        Pressed = hit;
        _from = root.SegmentLines[hit.Line].Clone();
        _down = p;
        _dragging = false;
        DraggedTo = null;
        return true;
    }

    public void Drag(ToolPointer p, PreviewOverlay preview)
    {
        if (Pressed is not { } hit || _down == null || _from == null) return;
        if (!_dragging && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) <= DragPx) return;
        if (_vm.Project?.FindPattern(hit.Root) is not { } root || hit.Line >= root.SegmentLines.Count) { Cancel(); return; }
        _dragging = true;
        var to = Moved(root, hit, p);
        var lines = root.SegmentLines.Select(l => l.Clone()).ToList();
        lines[hit.Line] = to;
        // a line stops at its neighbours: the last place where they keep their order stays
        if (!PatternTree.LinesInOrder(root, lines)) to = DraggedTo ?? _from;
        if (DraggedTo == null || !Same(DraggedTo, to))
        {
            DraggedTo = to;
            preview.Clear();
            if (!Same(to, _from)) ShowPlan(root, hit.Line, to, preview);
        }
        _vm.View.Redraw();
    }

    private static bool Same(SegmentLine a, SegmentLine b) =>
        Math.Abs(a.U1 - b.U1) < 1e-9 && Math.Abs(a.V1 - b.V1) < 1e-9 && Math.Abs(a.U2 - b.U2) < 1e-9 && Math.Abs(a.V2 - b.V2) < 1e-9 && a.Snapped == b.Snapped && a.Row == b.Row;

    /// <summary>Where the holes would go with the line there, as the turn of a pattern shows it.</summary>
    private void ShowPlan(HolePattern root, int index, SegmentLine to, PreviewOverlay preview)
    {
        if (_vm.Project is not { } project) return;
        PatternPlan plan;
        BlastProject copy;
        try
        {
            plan = PatternTree.Plan(project, root, (q, r) =>
            {
                r.SegmentLines[index] = to.Clone();
                return PatternTree.Relayout(q, r, PatternRebuildOptions.For(q));
            }, out copy);
        }
        catch (InvalidOperationException) { return; }
        // the parts of the segments as they would be, and the holes that go to another segment or another place
        var colour = PlanRenderer.Adapt(new SKColor(_vm.Settings.SelectionColor));
        if (copy.FindPattern(root.Id) is { } r)
            foreach (var s in PatternTree.Children(copy, r))
                foreach (var part in new[] { s.Outline }.Concat(s.OutlineParts))
                    if (part.Count >= 3) preview.Guides.Add((part.Append(part[0]).ToList(), colour));
        var moved = plan.Moves.Where(m => Vec3.Distance2D(m.Hole.Collar, m.Collar) > 1e-6 || copy.FindEntity(m.Hole.Id) is Hole c && c.PatternId != m.Hole.PatternId).ToList();
        foreach (var m in moved) preview.Markers.Add((m.Collar, MovedColour));
        foreach (var n in plan.Adds) preview.Markers.Add((n.Point, Added));
        foreach (var h in plan.Deletes) preview.Markers.Add((h.Collar, Removed));
        var (a, _) = PatternTree.LineEnds(root, to);
        preview.Labels.Add((a, Loc.F("{0} moved, {1} deleted, {2} added", moved.Count, plan.Deletes.Count, plan.Adds.Count)));
    }

    /// <summary>The line the drag gives: moved parallel to itself, or one end moved along the outline.</summary>
    private SegmentLine Moved(HolePattern root, (Guid Root, int Line, int End) hit, ToolPointer p)
    {
        var free = p.Modifiers.HasFlag(KeyModifiers.Control);
        var from = _from!;
        if (hit.End < 0)
        {
            var dv = PatternTree.WorldToGrid(root, OnGround(root, p)).V - PatternTree.WorldToGrid(root, OnGround(root, _down!)).V;
            var line = new SegmentLine { U1 = from.U1, V1 = from.V1 + dv, U2 = from.U2, V2 = from.V2 + dv };
            if (free) return line;
            // the first end in the middle between two rows, the line kept as it is turned
            var snap = PatternTree.SnapV(root, line.V1, out var row) - line.V1;
            line.V1 += snap;
            line.V2 += snap;
            if (Math.Abs(line.V1 - line.V2) < 1e-9) { line.Snapped = true; line.Row = row; }
            return line;
        }
        var (a, b) = PatternTree.LineEnds(root, from);
        var keep = PatternTree.WorldToGrid(root, hit.End == 0 ? b : a);
        var moving = PatternTree.WorldToGrid(root, hit.End == 0 ? a : b);
        var cursor = OnGround(root, p);
        if (p.Modifiers.HasFlag(KeyModifiers.Shift)) cursor = PatternTree.GridToWorld(root, moving.U, PatternTree.WorldToGrid(root, cursor).V);
        Vec3 end;
        if (free) end = PatternTree.NearestOnOutline(root, cursor);
        else
        {
            var v = PatternTree.SnapV(root, PatternTree.WorldToGrid(root, cursor).V, out _);
            end = PatternTree.OutlineAtV(root, v, cursor) ?? PatternTree.NearestOnOutline(root, cursor);
        }
        var e = PatternTree.WorldToGrid(root, end);
        var result = new SegmentLine { U1 = keep.U, V1 = keep.V, U2 = e.U, V2 = e.V };
        if (Math.Abs(keep.V - e.V) < 1e-9 && Math.Abs(PatternTree.SnapV(root, e.V, out var r) - e.V) < 1e-9) { result.Snapped = true; result.Row = r; }
        return result;
    }

    public void Release(ToolPointer p)
    {
        if (Pressed is not { } hit) return;
        var to = _dragging ? DraggedTo : null;
        var changed = to != null && _from != null && !Same(to, _from);
        Reset();
        if (changed) _ = Run(_vm.MoveSegmentLine(hit.Root, hit.Line, to!));
        _vm.View.Redraw();
    }

    private async Task Run(Task<bool> change)
    {
        _busy = true;
        try { await change; }
        catch (Exception ex) { Dialogs.ShowError(ex); }
        finally
        {
            _busy = false;
            _vm.View.Redraw();
        }
    }

    public void Cancel()
    {
        if (Pressed == null) return;
        Reset();
        _vm.View.Preview.Clear();
        _vm.View.Redraw();
    }

    private void Reset()
    {
        Pressed = null;
        DraggedTo = null;
        _from = null;
        _down = null;
        _dragging = false;
    }

    /// <summary>On a line: back along the rows, or take it away (the two segments beside it become one).</summary>
    public IEnumerable<MenuEntry>? ContextMenu(ToolPointer p)
    {
        if (_busy || HitAt(p.X, p.Y) is not { } hit) return null;
        bool Free() => !_busy && _vm.Doc?.IsReadOnly == false;
        return new List<MenuEntry>
        {
            MenuEntry.Item("Along the rows", () => _ = Run(_vm.StraightenSegmentLine(hit.Root, hit.Line)), Free),
            MenuEntry.Item("Remove the cut line", () => _ = Run(_vm.RemoveSegmentLine(hit.Root, hit.Line)), Free)
        };
    }
}

/// <summary>The cut lines of <see cref="SegmentLineGrips"/>, each with its grips beyond the outline; the dragged one where the drag puts it.</summary>
public sealed class SegmentLinesOverlay : IOverlay
{
    private readonly SegmentLineGrips _grips;
    private readonly Func<SKColor> _colour;

    public SegmentLinesOverlay(SegmentLineGrips grips, Func<SKColor> colour)
    {
        _grips = grips;
        _colour = colour;
    }

    /// <summary>The gesture whose lines are drawn.</summary>
    public SegmentLineGrips Owner => _grips;

    public void Draw(SKCanvas c, Camera cam, BlastProject project)
    {
        var roots = _grips.Roots();
        if (roots.Count == 0) return;
        var colour = _colour();
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = colour };
        using var moved = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, Color = colour, PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0) };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = colour };
        foreach (var root in roots)
            for (int k = 0; k < root.SegmentLines.Count; k++)
            {
                var dragged = _grips.Pressed is { } pr && pr.Root == root.Id && pr.Line == k && _grips.DraggedTo != null;
                var l = dragged ? _grips.DraggedTo! : root.SegmentLines[k];
                var (a, b) = SegmentLineGrips.Grips(cam, root, l);
                c.DrawLine(a.X, a.Y, b.X, b.Y, dragged ? moved : line);
                var hot = _grips.Hot is { } h && h.Root == root.Id && h.Line == k ? h.End : -2;
                c.DrawCircle(a.X, a.Y, VertexGripsOverlay.Radius + (hot is 0 or -1 ? 2 : 0), fill);
                c.DrawCircle(b.X, b.Y, VertexGripsOverlay.Radius + (hot is 1 or -1 ? 2 : 0), fill);
            }
    }
}
