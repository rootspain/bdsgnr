using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Engine;
using SkiaSharp;

namespace BlastDesigner.App.Tools;

/// <summary>
/// Draws a row and previews its holes at the design spacing while drawing. The second click ends a straight row;
/// Shift+click adds a bend instead, for a curved row that the next plain click (or ✓, Enter, a double click) ends.
/// ✕ discards the row like Esc; the tool stays active for the next row.
/// </summary>
public sealed class RowDrawTool : Tool
{
    private readonly List<Vec3> _points = new();
    private readonly Action<IReadOnlyList<Vec3>, double> _create;
    private Vec3? _hover;

    public RowDrawTool(MainViewModel vm, Action<IReadOnlyList<Vec3>, double> create) : base(vm)
    {
        _create = create;
        AnchorActions = new[]
        {
            new AnchorAction("check", "Finish row (Enter)", () => FinishEntry()),
            new AnchorAction("close", "Cancel row (Esc)", Cancel)
        };
    }

    public override string Name => "Row";
    public override string Hint => "Click the row start, then its end. For a curved row, Shift+click the bends before the end.";
    public IReadOnlyList<Vec3> Points => _points;
    public override Vec3? ActionAnchor => _points.Count >= 2 ? _points[^1] : null;
    public override IReadOnlyList<AnchorAction> AnchorActions { get; }

    private double Spacing => Vm.Project?.Design.Spacing ?? 0;

    public override void Activate()
    {
        base.Activate();
        View.Rendering += UpdatePreview;
    }

    public override void Deactivate()
    {
        View.Rendering -= UpdatePreview;
        _points.Clear();
        base.Deactivate();
    }

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left) return;
        if (p.ClickCount >= 2 && _points.Count >= 2) { Finish(); return; }
        if (_points.Count == 0 || Vec3.Distance2D(_points[^1], p.Snapped) > 1e-6) _points.Add(p.Snapped);
        // A plain click after the start ends the row; with Shift it is a bend of a curved row and drawing goes on.
        if (_points.Count >= 2 && !p.Modifiers.HasFlag(KeyModifiers.Shift)) { Finish(); return; }
        UpdatePreview(); View.Redraw();
    }

    public override void PointerMoved(ToolPointer p)
    {
        _hover = p.Snapped;
        UpdatePreview(); View.Redraw();
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (key is Key.Enter or Key.Return) return FinishEntry();
        if (key == Key.Back && _points.Count > 0) { RemoveSegment(); return true; }
        return base.KeyDown(key, mods);
    }

    protected override bool HasPendingInput => _points.Count > 0;

    public override void Cancel()
    {
        _points.Clear();
        base.Cancel();
        UpdatePreview(); View.Redraw();
    }

    public override bool FinishEntry()
    {
        if (_points.Count == 0) return false;
        // Only the start clicked: the row ends at the cursor, as the preview shows it.
        if (_points.Count == 1 && _hover is Vec3 h && Vec3.Distance2D(_points[0], h) > 1e-6) _points.Add(h);
        if (_points.Count < 2) { Vm.SetStatus(Loc.F("At least {0} points are needed", 2)); return true; }
        Finish();
        return true;
    }

    /// <summary>Removes the last clicked point, i.e. the segment drawn last; earlier segments stay.</summary>
    public void RemoveSegment()
    {
        if (_points.Count > 0) _points.RemoveAt(_points.Count - 1);
        UpdatePreview(); View.Redraw();
    }

    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p)
    {
        yield return MenuEntry.Item("Set row properties..", () => _ = Vm.EditDesignData());
        yield return MenuEntry.Item("Finish entry", Finish, () => _points.Count >= 2);
        yield return MenuEntry.Item("Cancel segment", RemoveSegment, () => _points.Count > 0);
        yield return MenuEntry.Item("Cancel entry", Cancel, () => _points.Count > 0);
        foreach (var e in base.ContextMenu(p)) yield return e;
    }

    private void Finish()
    {
        if (_points.Count < 2 || Spacing <= 0) return;
        var pts = _points.ToList();
        _points.Clear();
        Preview.Clear();
        try { _create(pts, Spacing); }
        catch (Exception ex) { Dialogs.ShowError(ex); }
        View.Redraw();
    }

    private void UpdatePreview()
    {
        Preview.Lines.Clear();
        Preview.Points.Clear();
        Preview.Labels.Clear();
        Preview.Markers.Clear();
        if (_points.Count == 0) return;
        var pts = _points.ToList();
        if (_hover is Vec3 h && Vec3.Distance2D(pts[^1], h) > 1e-6) pts.Add(h);
        if (pts.Count > 1) Preview.Lines.Add((pts, false, PreviewColor));
        foreach (var p in _points) Preview.Points.Add((p, PreviewColor));
        var holes = HoleDesign.RowPositions(pts, Spacing, true);
        foreach (var m in holes) Preview.Markers.Add((m, new SKColor(0xFFF0A63A)));
        if (_hover is Vec3 hv && holes.Count > 0) Preview.Labels.Add((hv, Loc.F("{0} holes", holes.Count)));
    }
}
