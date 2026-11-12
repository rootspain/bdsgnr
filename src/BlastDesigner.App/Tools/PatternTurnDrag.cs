using Avalonia.Input;
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
/// A turn of the grid of a pattern (or of the selected objects about their centre) by dragging the mouse about a pivot, shared by
/// the rotate tool and the circle of the Select tool: the angle in steps, the preview of where the holes go, and the turn itself.
/// </summary>
public sealed class PatternTurnDrag
{
    private static readonly SKColor Moved = new(0xFF94A3B8);
    private static readonly SKColor Added = new(0xFF64748B);
    private static readonly SKColor Removed = new(0xFFDC2626);
    /// <summary>Length of the handle of objects without a pattern, px.</summary>
    private const double HandlePixels = 90;

    private readonly MainViewModel _vm;

    public PatternTurnDrag(MainViewModel vm) => _vm = vm;

    /// <summary>The pattern turned; null turns the selected objects about <see cref="Pivot"/>.</summary>
    public Guid? PatternId { get; set; }
    public Vec3 Pivot { get; set; }
    /// <summary>Bearing from the pivot to the press; null when no turn is under way.</summary>
    public double? StartBearing { get; private set; }
    /// <summary>The turn so far, degrees clockwise, in whole steps.</summary>
    public double Delta { get; private set; }

    public HolePattern? Pattern => _vm.Project?.FindPattern(PatternId);
    public bool Turning => StartBearing != null;
    /// <summary>The direction the handle shows: the burden of the pattern, north for objects without one.</summary>
    private double Azimuth => Pattern?.Azimuth ?? 0;

    public void Begin(Vec3 world)
    {
        StartBearing = GeoMath.Bearing(Pivot, world);
        Delta = 0;
    }

    /// <summary>The turn the cursor gives: in steps of 1°, with Shift 5°, with Ctrl 0.1°.</summary>
    public void Update(Vec3 world, KeyModifiers modifiers)
    {
        if (StartBearing is not double start) return;
        var step = PatternTurn.Step(modifiers.HasFlag(KeyModifiers.Shift), modifiers.HasFlag(KeyModifiers.Control));
        Delta = Math.Round(MainViewModel.Turn(start, GeoMath.Bearing(Pivot, world)) / step) * step;
    }

    public void Reset()
    {
        StartBearing = null;
        Delta = 0;
    }

    /// <summary>The end of the handle pointing at <paramref name="azimuth"/>: the tip of the burden arrow of a pattern.</summary>
    public Vec3 Handle(double azimuth)
    {
        var cam = _vm.View.Camera;
        if (Pattern is { } pat) return PatternRotateHandleOverlay.ArrowTip(pat, azimuth, cam);
        var dir = GeoMath.HoleDirection(azimuth, 90);
        return Pivot + new Vec3(dir.X, dir.Y, 0) * (HandlePixels / Math.Max(1e-9, cam.Scale));
    }

    /// <summary>The centre, the handle on the burden ray and, while turning, where everything goes.</summary>
    public void ShowPreview(PreviewOverlay preview)
    {
        if (_vm.Doc is not { } doc) return;
        var accent = new SKColor(_vm.Settings.SelectionColor);
        // the arrow as it turns, in the colour of the burden arrow
        var arrow = PlanRenderer.Adapt(new SKColor(_vm.Settings.PatternArrowColor));
        preview.Points.Add((Pivot, accent));
        var az = Azimuth + Delta;
        preview.Lines.Add((new[] { Pivot, Handle(az) }, false, arrow));
        preview.Points.Add((Handle(az), arrow));
        if (StartBearing == null || Math.Abs(Delta) <= 1e-9) return;
        preview.Guides.Add((new[] { Pivot, Handle(Azimuth) }, Moved));
        if (Pattern is { } pat)
        {
            var plan = PatternTurn.Plan(doc.Project, pat, Delta);
            foreach (var m in plan.Moves) preview.Markers.Add((m.Collar, Moved));
            foreach (var n in plan.Adds) preview.Markers.Add((n.Point, Added));
            foreach (var h in plan.Deletes) preview.Markers.Add((h.Collar, Removed));
            preview.Labels.Add((Handle(az), $"{Delta:+0.#;−0.#;0}° · " + Loc.F("{0} moved, {1} deleted, {2} added", plan.Moves.Count, plan.Deletes.Count, plan.Adds.Count)));
        }
        else
        {
            var f = GeneralOps.MakeTransform(Vec3.Zero, Delta, Pivot, 1);
            foreach (var h in doc.SelectedHoles) preview.Markers.Add((f(h.Collar), Moved));
            foreach (var line in doc.SelectedEntities.OfType<PolylineEntity>()) preview.Lines.Add((line.Points.Select(f).ToList(), line.Closed, Moved));
            preview.Labels.Add((Handle(az), $"{Delta:+0.#;−0.#;0}°"));
        }
    }

    /// <summary>Turns the pattern by the turn made (one undo step) and ends the turn; false when nothing was done.</summary>
    public async Task<bool> Commit()
    {
        var delta = Delta;
        Reset();
        if (PatternId is not Guid id || Math.Abs(delta) <= 1e-9) return false;
        try { return await _vm.RotatePattern(id, delta); }
        catch (Exception ex) { Dialogs.ShowError(ex); return false; }
    }
}
