using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.ViewModels;

/// <summary>Pattern grids after the pattern was made: turning them, laying them out again, letting holes go.</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// «Orientation turns with the pattern» for the next pattern, kept for the session; null until the user sets it: on when
    /// the project has no pattern yet, off otherwise (a second pattern must not turn the orientation of the first).
    /// </summary>
    public bool? PatternRotateOrientationChoice { get; set; }
    public bool PatternRotateOrientation => PatternRotateOrientationChoice ?? Project?.Patterns.Count is null or 0;
    /// <summary>How the bearings of the inclined holes follow a turn of the grid (rotate tool panel).</summary>
    public PatternBearingMode PatternBearing { get; set; } = PatternBearingMode.Rotate;

    /// <summary>The pattern of the selected holes: the one of the first selected hole that has one.</summary>
    public HolePattern? SelectedPattern =>
        Doc is { } doc ? doc.SelectedHoles.Select(h => doc.Project.FindPattern(h.PatternId)).FirstOrDefault(p => p != null) : null;

    private PatternRebuildOptions PatternOptions(BlastProject p) => new()
    {
        Bearing = PatternBearing,
        CollarSurface = p.Entities.OfType<SurfaceEntity>().FirstOrDefault(s => s.Kind == SurfaceKind.Bench),
        GradeSurface = p.Entities.OfType<SurfaceEntity>().FirstOrDefault(s => s.Kind == SurfaceKind.Grade),
        Layer = p.GetOrCreateLayer(StandardLayers.Holes, 0xFF1E3A8A, "Design")
    };

    public void PatternRotateTool() => SetTool(new PatternRotateTool(this));

    /// <summary>The tool panel shows the state of the active tool again (the rotate tool after a turn).</summary>
    public void RefreshToolPanel() => ToolChanged?.Invoke();

    /// <summary>Draws the grid of the pattern of the selected holes.</summary>
    public PatternOverlay PatternGrid => _patternGrid ??= new PatternOverlay(this);
    private PatternOverlay? _patternGrid;

    public Task RotatePatternDialog() => RotatePatternDialog(SelectedPattern?.Id);

    /// <summary>Asks for an angle and turns a pattern by it (the circle of the Select tool names its pattern).</summary>
    public async Task RotatePatternDialog(Guid? patternId)
    {
        if (Project?.FindPattern(patternId) is not { } pat) { await Dialogs.Info(Loc.T("Select a hole of a pattern first.")); return; }
        var angle = await Dialogs.PromptNumber("Rotate pattern", Loc.T("Turn, ° (clockwise)"), 0);
        if (angle is double a && Math.Abs(a) > 1e-9) await RotatePattern(pat.Id, a);
    }

    /// <summary>Turns a pattern grid by <paramref name="delta"/> degrees as one undo step; false when nothing was done.</summary>
    public Task<bool> RotatePattern(Guid patternId, double delta) =>
        LayOutPattern(patternId, "Rotate pattern", pat => pat.Azimuth = GeoMath.NormalizeBearing(pat.Azimuth + delta),
            (p, pat, o) => HoleDesign.RotatePattern(p, pat, delta, o));

    /// <summary>The smallest burden or spacing a pattern takes, m.</summary>
    public const double MinPatternStep = 0.1;

    /// <summary>
    /// Gives a pattern a new burden and spacing and lays it out again, as one undo step; false when nothing was done (a value
    /// below <see cref="MinPatternStep"/>, the same values, a pattern that cannot be laid out again, or the user said no).
    /// </summary>
    public async Task<bool> ResizePattern(Guid patternId, double burden, double spacing)
    {
        if (Project?.FindPattern(patternId) is not { } pat) return false;
        if (burden < MinPatternStep || spacing < MinPatternStep)
        {
            await Dialogs.Info(Loc.F("The burden and the spacing are at least {0} m.", MinPatternStep), Loc.T("Pattern size"));
            return false;
        }
        if (Math.Abs(burden - pat.Burden) < 1e-9 && Math.Abs(spacing - pat.Spacing) < 1e-9) return false;
        try
        {
            return await LayOutPattern(patternId, "Pattern size", x => HoleDesign.Resize(x, burden, spacing),
                (p, x, o) => HoleDesign.ResizePattern(p, x, burden, spacing, o));
        }
        catch (InvalidOperationException ex)
        {
            // a grid too dense for its outline
            await Dialogs.Info(Loc.T(ex.Message), Loc.T("Pattern size"));
            return false;
        }
    }

    public async Task RebuildSelectedPattern()
    {
        if (SelectedPattern is not { } pat) { await Dialogs.Info(Loc.T("Select a hole of a pattern first.")); return; }
        // the outline is taken again from its polygon, which may have been edited since
        var outline = Project!.FindEntity(pat.OutlineEntityId ?? Guid.Empty) is PolylineEntity { Closed: true, Points.Count: >= 3 } line ? line.Points.ToList() : null;
        await LayOutPattern(pat.Id, "Rebuild pattern", plan => { if (outline != null) plan.Outline = outline; },
            (p, x, o) => { if (outline != null) x.Outline = outline.ToList(); return HoleDesign.RebuildPattern(p, x, o); });
    }

    /// <summary>
    /// Lays a pattern out again in one undo step: says why it cannot be, and asks once before holes loaded or timed by hand
    /// are deleted. <paramref name="preview"/> changes a copy of the grid to see what would go.
    /// </summary>
    private async Task<bool> LayOutPattern(Guid patternId, string undoName, Action<HolePattern> preview, Func<BlastProject, HolePattern, PatternRebuildOptions, PatternRebuildReport> run)
    {
        if (Doc == null || Project!.FindPattern(patternId) is not { } pat) return false;
        var options = PatternOptions(Project);
        if (HoleDesign.RebuildRefusal(Project, pat, options) is { } no)
        {
            await Dialogs.Info(Loc.F(no.Reason, no.HoleLabel) + "\n" + Loc.T("The pattern cannot be laid out again."), Loc.T(undoName));
            return false;
        }
        var copy = ProjectJson.DeepClone(pat);
        preview(copy);
        var byHand = HoleDesign.PlanPattern(Project, copy).Deletes.Count(h => h.ManualLoading || h.ElectronicTimeManual);
        if (byHand > 0 && !await Dialogs.Confirm(Loc.F("{0} holes loaded or timed by hand leave the outline and will be deleted. Continue?", byHand), Loc.T(undoName))) return false;
        // the whole pattern selected stays selected with its new holes
        var holes = HoleDesign.PatternHoles(Project, pat).Select(h => h.Id).ToList();
        var whole = holes.Count > 0 && holes.All(Doc.Selection.Contains);
        PatternRebuildReport? report = null;
        if (!Change(undoName, p => { if (p.FindPattern(patternId) is { } x) report = run(p, x, PatternOptions(p)); })) return false;
        if (report == null) return false;
        // deleted holes leave the selection; the step of the change brings it back on undo
        if (report.DeletedIds.Count > 0 || (whole && report.AddedIds.Count > 0))
            Doc.Select(Doc.Selection.Except(report.DeletedIds).Concat(whole ? report.AddedIds : Enumerable.Empty<Guid>()).ToList(), record: false);
        SetStatus(Loc.T(undoName) + ": " + Loc.F("{0} moved, {1} deleted, {2} added", report.Moved, report.Deleted, report.Added)
            + (report.TiesLost > 0 ? "; " + Loc.F("{0} ties removed", report.TiesLost) : ""));
        return true;
    }

    /// <summary>Turns the grid so that its rows run along a segment (the burden across it, on the side nearer the present one).</summary>
    public Task<bool> RotatePatternAlong(Guid patternId, Vec3 a, Vec3 b)
    {
        if (Project!.FindPattern(patternId) is not { } pat || Vec3.Distance2D(a, b) < 1e-6) return Task.FromResult(false);
        var along = GeoMath.Bearing(a, b);
        var delta = new[] { along + 90, along - 90 }.Select(t => Turn(pat.Azimuth, t)).MinBy(Math.Abs);
        return Math.Abs(delta) < 1e-9 ? Task.FromResult(false) : RotatePattern(patternId, delta);
    }

    /// <summary>The shortest turn from one bearing to another, −180..180.</summary>
    public static double Turn(double from, double to) => PatternTurn.Turn(from, to);

    /// <summary>Turns the selected objects about their centre (holes of no pattern, with the rotate tool).</summary>
    public void RotateSelection(Vec3 pivot, double delta)
    {
        var ids = Doc!.Selection.ToHashSet();
        var f = GeneralOps.MakeTransform(Vec3.Zero, delta, pivot, 1);
        var timed = 0;
        Change("Rotate objects", p => timed = ReprogramTimingLines(p, ids, () =>
        {
            foreach (var e in p.Entities.Where(e => ids.Contains(e.Id)))
            {
                if (e is Hole { Locked: true } || p.FindLayer(e.LayerId) is { Locked: true }) continue;
                e.Transform(f);
            }
        }));
        ReportReprogrammed(timed);
    }

    public void SetPatternFlag(Guid patternId, string undoName, Action<HolePattern> set)
    {
        Change(undoName, p => { if (p.FindPattern(patternId) is { } pat) set(pat); });
        ToolChanged?.Invoke();
    }

    public void SelectPatternHoles()
    {
        if (SelectedPattern is not { } pat) return;
        Doc!.Select(HoleDesign.PatternHoles(Project!, pat).Where(IsSelectable).Select(h => h.Id));
    }

    public async Task DetachSelectedFromPattern()
    {
        var ids = SelectedHoleIds();
        if (!Project!.Holes.Any(h => ids.Contains(h.Id) && h.PatternId != null)) { await Dialogs.Info(Loc.T("Select a hole of a pattern first.")); return; }
        int n = 0;
        Change("Detach from pattern", p => n = HoleDesign.DetachFromPattern(p, HolesById(p, ids)));
        SetStatus(Loc.F("{0} holes detached from the pattern", n));
    }

    public async Task DissolveSelectedPattern()
    {
        if (SelectedPattern is not { } pat) { await Dialogs.Info(Loc.T("Select a hole of a pattern first.")); return; }
        if (!await Dialogs.Confirm(Loc.F("Dissolve {0}? Its holes stay as they are, but the grid can no longer be turned.", pat.Name))) return;
        var id = pat.Id;
        Change("Dissolve pattern", p => { if (p.FindPattern(id) is { } x) HoleDesign.DissolvePattern(p, x); });
    }
}
