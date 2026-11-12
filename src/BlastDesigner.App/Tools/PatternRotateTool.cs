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
        var pat = Vm.SelectedPattern;
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
        foreach (var pat in shown)
        {
            if (pat.Outline.Count >= 3) PlanRenderer.DrawPolyline(c, cam, pat.Outline, true, dashed);
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

    /// <summary>The patterns whose grid is drawn: those of the selected holes.</summary>
    public static List<HolePattern> Shown(Document? doc)
    {
        if (doc is not { Selection.Count: > 0 } || doc.Project.Patterns.Count == 0) return new();
        var shown = doc.SelectedHoles.Select(h => h.PatternId).OfType<Guid>().ToHashSet();
        return doc.Project.Patterns.Where(p => shown.Contains(p.Id)).ToList();
    }
}
