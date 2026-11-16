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
/// Turns the grid of the pattern of the selected holes about its centre: a drag turns it with a preview of where the holes go
/// (grey), which are deleted (red) and which are added; on release it is one undo step. Without a pattern the selected objects
/// are turned as they are about their centre.
/// </summary>
public sealed class PatternRotateTool : Tool
{
    private readonly PatternTurnDrag _turn;
    private bool _byLine;

    public PatternRotateTool(MainViewModel vm) : base(vm) => _turn = new PatternTurnDrag(vm);

    public override string Name => "Rotate pattern";
    public override string Hint => _byLine
        ? "Click a segment of a line: the rows of the pattern run along it."
        : "Drag to turn the pattern about its centre (1° steps, Shift 5°, Ctrl 0.1°). Esc cancels the turn.";

    public HolePattern? Pattern => _turn.Pattern;
    public bool ByLine { get => _byLine; set { _byLine = value; Vm.SetStatus(Loc.T(Hint)); Vm.RefreshToolPanel(); } }

    public override void Activate()
    {
        Target();
        base.Activate();
        if (Vm.Doc is not { Selection.Count: > 0 }) Vm.SetStatus(Loc.T("Select the holes of a pattern (or objects to turn) first."));
        Show();
    }

    /// <summary>What is turned: the pattern of the selection, or the selection about its centre.</summary>
    private void Target()
    {
        var pat = Vm.TurnTarget;
        _turn.PatternId = pat?.Id;
        if (pat != null) _turn.Pivot = pat.Center;
        else if (Vm.Doc is { Selection.Count: > 0 } doc)
            _turn.Pivot = GeoMath.Centroid(doc.SelectedEntities.SelectMany(e => e.GetVertices()).ToList());
    }

    protected override bool HasPendingInput => _turn.Turning || _byLine;

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left || Vm.Doc is not { Selection.Count: > 0 }) return;
        if (_byLine)
        {
            if (_turn.PatternId is not Guid id || p.Hit is not PolylineEntity line || line.Points.Count < 2) return;
            var (a, b) = NearestSegment(line, p.World);
            _ = Turned(Vm.RotatePatternAlong(id, a, b));
            ByLine = false;
            return;
        }
        _turn.Begin(p.World);
        Show();
    }

    public override void PointerMoved(ToolPointer p)
    {
        if (!_turn.Turning) return;
        _turn.Update(p.World, p.Modifiers);
        Show();
    }

    public override void PointerReleased(ToolPointer p)
    {
        if (!_turn.Turning) return;
        var delta = _turn.Delta;
        if (Math.Abs(delta) > 1e-9 && _turn.PatternId != null && Vm.Doc is { Selection.Count: > 0 }) { _ = Turned(_turn.Commit()); return; }
        _turn.Reset();
        if (Math.Abs(delta) > 1e-9) Apply(delta);
        else Show();
    }

    /// <summary>Turns by an angle (the drag, the angle field and the ±90° buttons of the panel).</summary>
    public void Apply(double delta)
    {
        if (Vm.Doc is not { Selection.Count: > 0 }) return;
        if (_turn.PatternId is Guid id) _ = Turned(Vm.RotatePattern(id, delta));
        else
        {
            Vm.RotateSelection(_turn.Pivot, delta);
            Show();
        }
    }

    private async Task Turned(Task<bool> turn)
    {
        try { await turn; }
        catch (Exception ex) { Dialogs.ShowError(ex); }
        Show();
        Vm.RefreshToolPanel();
    }

    public override void Cancel()
    {
        _turn.Reset();
        if (_byLine) ByLine = false;
        Show();
    }

    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p)
    {
        yield return MenuEntry.Item("Turn by +90°", () => Apply(90), () => Vm.HasSelection);
        yield return MenuEntry.Item("Turn by −90°", () => Apply(-90), () => Vm.HasSelection);
        yield return MenuEntry.Item("Along a line", () => ByLine = true, () => _turn.PatternId != null);
        foreach (var e in base.ContextMenu(p)) yield return e;
    }

    private static (Vec3 A, Vec3 B) NearestSegment(PolylineEntity line, Vec3 at)
    {
        var pts = line.Points;
        var best = (pts[0], pts[1]);
        var bestD = double.MaxValue;
        var n = line.Closed ? pts.Count : pts.Count - 1;
        for (int i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            var d = Vec3.Distance2D(GeoMath.ClosestOnSegment2D(a, b, at).Point, at);
            if (d < bestD) { bestD = d; best = (a, b); }
        }
        return best;
    }

    /// <summary>The centre, the handle on the burden ray and, while dragging, where everything goes.</summary>
    private void Show()
    {
        // the selection may have changed since the tool opened
        if (!_turn.Turning) Target();
        Preview.Clear();
        if (Vm.Doc is not { Selection.Count: > 0 }) { View.Redraw(); return; }
        _turn.ShowPreview(Preview);
        View.Redraw();
    }
}

/// <summary>The grid of the pattern of the selected holes: its outline dashed, its centre and the burden arrow.</summary>
public sealed class PatternOverlay : IOverlay
{
    private readonly MainViewModel _vm;

    public PatternOverlay(MainViewModel vm) => _vm = vm;

    /// <summary>The pattern whose arrow is not drawn: it is being turned, and the turn shows where it goes.</summary>
    public Guid? HiddenArrow { get; set; }

    public void Draw(SKCanvas c, Camera cam, BlastProject project)
    {
        var shown = Shown(_vm.Doc);
        if (shown.Count == 0) return;
        var colour = PlanRenderer.Adapt(new SKColor(_vm.Settings.SelectionColor));
        using var dashed = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.25f, Color = colour, PathEffect = SKPathEffect.CreateDash(new[] { 7f, 5f }, 0) };
        using var solid = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = colour };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = colour };
        // the burden arrow in its own colour (yellow by default), the outline and the centre in the selection colour
        using var arrow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.75f, Color = PlanRenderer.Adapt(new SKColor(_vm.Settings.PatternArrowColor)) };
        // the outline and the segments of each pattern once, the centre and the arrow of what a turn turns (a segment or the pattern)
        foreach (var root in shown.Select(x => PatternTree.Root(project, x)).Distinct())
        {
            if (root.Outline.Count >= 3) PlanRenderer.DrawPolyline(c, cam, root.Outline, true, dashed);
            DrawSegments(c, cam, project, root, colour);
        }
        foreach (var pat in shown)
        {
            if (pat.ParentId != null && pat.Outline.Count >= 3) PlanRenderer.DrawPolyline(c, cam, pat.Outline, true, solid);
            var (cx, cy) = cam.ToScreen(pat.Center);
            c.DrawCircle(cx, cy, 4, fill);
            if (pat.Id == HiddenArrow) continue;
            var (tx, ty) = cam.ToScreen(PatternRotateHandleOverlay.ArrowTip(pat, pat.Azimuth, cam));
            c.DrawLine(cx, cy, tx, ty, arrow);
            // the head of the arrow, drawn on the screen
            var ang = Math.Atan2(ty - cy, tx - cx);
            foreach (var side in new[] { 2.6, -2.6 })
                c.DrawLine(tx, ty, tx + (float)(9 * Math.Cos(ang + side)), ty + (float)(9 * Math.Sin(ang + side)), arrow);
        }
    }

    /// <summary>
    /// The parts of a pattern cut into segments, each dotted with its name at its middle, and the conditional rectangle of
    /// each part (or of the pattern itself) whose depth has four numbers, with the numbers at its corners.
    /// </summary>
    private static void DrawSegments(SKCanvas c, Camera cam, BlastProject project, HolePattern root, SKColor colour)
    {
        using var dotted = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = colour.WithAlpha(0xB0), PathEffect = SKPathEffect.CreateDash(new[] { 2f, 3f }, 0) };
        using var frame = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = colour.WithAlpha(0x90), PathEffect = SKPathEffect.CreateDash(new[] { 10f, 3f, 2f, 3f }, 0) };
        using var text = new SKPaint { IsAntialias = true, Color = colour, TextSize = 12, Typeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) };
        using var small = new SKPaint { IsAntialias = true, Color = colour, TextSize = 11 };
        var leaves = PatternTree.Leaves(project, root);
        if (root.IsContainer)
        {
            // the problems at the seams: red between holes too close (or crossing under the ground), orange across a gap
            using var close = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, Color = new SKColor(0xFFEF4444) };
            using var gap = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, Color = new SKColor(0xFFF59E0B), PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0) };
            foreach (var issue in PatternSeams.Check(project, root))
            {
                var (ax, ay) = cam.ToScreen(issue.From);
                var (bx, by) = cam.ToScreen(issue.To);
                c.DrawLine(ax, ay, bx, by, issue.Kind == SeamIssueKind.Gap ? gap : close);
            }
        }
        if (root.IsContainer)
            foreach (var s in PatternTree.Children(project, root))
            {
                foreach (var part in new[] { s.Outline }.Concat(s.OutlineParts))
                    if (part.Count >= 3) PlanRenderer.DrawPolyline(c, cam, part, true, dotted);
                if (s.Outline.Count < 3) continue;
                var (x, y) = cam.ToScreen(GeoMath.Centroid(s.Outline.Select(q => q - s.Outline[0]).ToList()) + s.Outline[0]);
                c.DrawText(s.Name, x - text.MeasureText(s.Name) / 2, y + 4, text);
            }
        foreach (var leaf in leaves)
        {
            var d = leaf.HoleDefaults.Depth;
            if (!d.UsesRectangle || DepthRectangle.Of(leaf) is not { } rect) continue;
            var corners = rect.Corners(leaf);
            PlanRenderer.DrawPolyline(c, cam, corners, true, frame);
            // front left, front right, back right, back left
            var numbers = new[] { d.Front.From, d.Front.To, d.Back.To, d.Back.From };
            for (int k = 0; k < 4; k++)
            {
                var (x, y) = cam.ToScreen(corners[k]);
                var label = numbers[k].ToString("0.0#", System.Globalization.CultureInfo.CurrentCulture);
                c.DrawText(label, x + 4, y - 4, small);
            }
        }
    }

    /// <summary>
    /// The patterns whose grid is drawn with its arrow and circle: those of the selected holes. Holes all in one segment show
    /// that segment (it turns on its own); holes of several segments show the pattern they are cut from.
    /// </summary>
    public static List<HolePattern> Shown(Document? doc)
    {
        if (doc is not { Selection.Count: > 0 } || doc.Project.Patterns.Count == 0) return new();
        var p = doc.Project;
        var shown = new HashSet<Guid>();
        foreach (var g in doc.SelectedHoles.Select(h => p.FindPattern(h.PatternId)).OfType<HolePattern>().GroupBy(x => PatternTree.Root(p, x).Id))
        {
            var mine = g.Select(x => x.Id).Distinct().ToList();
            shown.Add(mine.Count == 1 && g.First().ParentId != null ? mine[0] : g.Key);
        }
        return p.Patterns.Where(x => shown.Contains(x.Id)).ToList();
    }
}
