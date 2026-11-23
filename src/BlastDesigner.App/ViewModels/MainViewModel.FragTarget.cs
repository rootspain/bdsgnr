using BlastDesigner.App.Services;
using BlastDesigner.App.Views;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.ViewModels;

/// <summary>Analysis ▸ Fragmentation target: the window that finds the grid for a target X50, and applying its variant as one undo step.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Opens the target fragmentation window on the pattern of the selection (on the first pattern without one).</summary>
    public Task FragmentationTarget() => FragmentationTargetWindow.Open(this, SelectedPattern?.Id);

    /// <summary>
    /// Puts a variant of the target fragmentation window in the project as one undo step: the rock inputs (with the
    /// target), the diameter and the charge, the turn and the size of the grid of each pattern of the area, the loading.
    /// Asks first when holes loaded or timed by hand would be deleted; false when nothing was done.
    /// </summary>
    /// <param name="rootIds">The root patterns of the area (none: every pattern).</param>
    public async Task<FragApply.Report?> ApplyFragCandidate(IReadOnlyList<Guid> rootIds, FragDesign design, string undoName = "Fragmentation target")
    {
        if (Doc == null || Project == null) return null;
        if (FragApply.Refusal(Project, rootIds) is { } no)
        {
            await Dialogs.Info(Loc.F(no.Reason, no.HoleLabel) + "\n" + Loc.T("The pattern cannot be laid out again."), Loc.T(undoName));
            return null;
        }
        // what would go, on a copy: holes done by hand are asked about before
        var template = TemplateOf(Project, rootIds);
        var copy = ProjectJson.DeepClone(Project);
        var preview = FragApply.Apply(copy, rootIds, design.Clone(), template);
        var byHand = preview.DeletedIds.Select(Project.FindEntity).OfType<Hole>().Count(h => h.ManualLoading || h.ElectronicTimeManual);
        if (byHand > 0 && !await Dialogs.Confirm(Loc.F("{0} holes loaded or timed by hand leave the outline and will be deleted. Continue?", byHand), Loc.T(undoName))) return null;
        FragApply.Report? report = null;
        if (!Change(undoName, p => report = FragApply.Apply(p, rootIds, design, TemplateOf(p, rootIds)))) return null;
        if (report?.Refusal != null) return report;
        if (report is { DeletedIds.Count: > 0 }) Doc.Select(Doc.Selection.Except(report.DeletedIds).ToList(), record: false);
        return report;
    }

    /// <summary>The rule the holes of the area are loaded by now: its stemming product and primers go into the rule of the variant.</summary>
    internal static LoadingRule? TemplateOf(BlastProject p, IReadOnlyList<Guid> rootIds) =>
        FragBase.Of(p, rootIds.Count == 1 ? rootIds[0] : null).Template;
}
