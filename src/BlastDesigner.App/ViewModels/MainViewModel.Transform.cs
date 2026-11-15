using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Engine;

namespace BlastDesigner.App.ViewModels;

public partial class MainViewModel
{
    /// <summary>The Transform tool on the selection (button of the tool rail, Ctrl+T, the context menu).</summary>
    public void TransformTool()
    {
        if (Doc == null) return;
        SetTool(new TransformTool(this));
    }

    /// <summary>
    /// Scales, tilts, mirrors and moves the objects as one step of undo; the timing lines among them program their holes
    /// again. Holes, patterns and locked layers are left, and the status line says how many.
    /// </summary>
    public TransformOutcome? ApplySpatialTransform(IReadOnlyCollection<Guid> ids, SpatialTransform t)
    {
        if (Doc == null || t.Problem != null) return null;
        TransformOutcome? outcome = null;
        var timed = 0;
        var set = ids.ToHashSet();
        Change("Transform", p => timed = ReprogramTimingLines(p, set, () => outcome = SpatialTransforms.Apply(p, set, t)));
        if (outcome is { } r)
        {
            var left = new List<string>();
            if (r.Holes + r.PatternOutlines > 0) left.Add(Loc.F("holes and patterns {0}", r.Holes + r.PatternOutlines));
            if (r.Locked > 0) left.Add(Loc.F("on locked layers {0}", r.Locked));
            SetStatus(Loc.F("Transformed: {0}", r.Changed) + (left.Count > 0 ? "; " + Loc.T("left") + ": " + string.Join(", ", left) : ""));
        }
        ReportReprogrammed(timed);
        return outcome;
    }
}
