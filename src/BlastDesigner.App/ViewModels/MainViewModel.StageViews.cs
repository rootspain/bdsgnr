using BlastDesigner.App.Services;
using BlastDesigner.Domain.Model;
using BlastDesigner.Rendering;

namespace BlastDesigner.App.ViewModels;

/// <summary>
/// The stage buttons over the plan (Drilling · Plan/Fact, Loading · Plan/Fact): switching them is a change of the view,
/// no undo step; changing what a button shows marks the project changed, no undo step either.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The four stage buttons of the open project; none without one.</summary>
    public IReadOnlyList<StageView> StageButtons => Project is { } p ? StageViews.Of(p) : Array.Empty<StageView>();

    /// <summary>Name of a button in the language of the program, "Drilling · Fact".</summary>
    public static string StageName(StageView v) => StageName(v.Stage, v.Mode);

    public static string StageName(WorkStage stage, StageMode mode) => StageWord(stage.ToString()) + " · " + StageWord(mode.ToString());

    /// <summary>Short name of a button for the narrow columns of the layers panel, "Б·Ф" / "D·F".</summary>
    public static string StageShort(StageView v) => StageWord(v.Stage.ToString())[..1] + "·" + StageWord(v.Mode.ToString())[..1];

    /// <summary>
    /// A word of the stage buttons (Drilling, Loading, Plan, Fact) translated as "stage:…": the stage is "Заряжание"
    /// there, not "Зарядка" of the ribbon.
    /// </summary>
    public static string StageWord(string english)
    {
        var t = Loc.T("stage:" + english);
        return t.StartsWith("stage:", StringComparison.Ordinal) ? english : t;
    }

    /// <summary>
    /// A click on a stage button (<see cref="StageViews.Switch"/>); <paramref name="keepOthers"/> with Ctrl. What was hidden
    /// leaves the selection; one redraw.
    /// </summary>
    public void SwitchStage(StageView view, bool keepOthers)
    {
        if (Doc == null || Project is not { } p) return;
        var was = view.On;
        StageViews.Switch(p, view, keepOthers);
        IO.UsageLog.Track(IO.UsageLog.Flag, "Stage " + view.Name, view.On ? "on" : "off", was ? "on" : "off");
        DeselectHidden();
        SetStatus(Loc.F(Loc.Ui(view.On ? "{0}: on" : "{0}: off"), StageName(view)));
        Doc.NotifyViewChanged();
    }

    /// <summary>A group of stage buttons folded to its name (a setting of the program, not of the blast).</summary>
    public bool IsStageFolded(WorkStage stage) => Settings.FoldedStages?.Contains(stage.ToString()) == true;

    /// <summary>Folds a group of stage buttons to its name, or opens it again; kept in the program settings, no undo step.</summary>
    public void ToggleStageFold(WorkStage stage)
    {
        var folded = Settings.FoldedStages ?? new List<string>();
        if (!folded.Remove(stage.ToString())) folded.Add(stage.ToString());
        Settings.FoldedStages = folded.Count > 0 ? folded : null;
        Settings.Save();
    }

    /// <summary>F8 (drilling) and Shift+F8 (loading): plan, then fact, then plan again; the other stage stays as it is.</summary>
    public void CycleStage(WorkStage stage)
    {
        if (Project is not { } p) return;
        var plan = StageViews.Get(p, stage, StageMode.Plan);
        var fact = StageViews.Get(p, stage, StageMode.Fact);
        SwitchStage(plan.On ? fact : plan, true);
    }

    /// <summary>Takes out of the selection what is no longer drawn: on a hidden layer, or a hole of a hidden state.</summary>
    private void DeselectHidden()
    {
        if (Doc == null || Project is not { } p || Doc.Selection.Count == 0) return;
        var hidden = Doc.SelectedEntities.Where(e => p.FindLayer(e.LayerId) is { Visible: false } || e is Hole h && !PlanRenderer.HoleVisible(p.View, h)).Select(e => e.Id).ToList();
        if (hidden.Count > 0) Doc.Select(hidden, Domain.SelectionMode.Remove);
    }

    private bool _stagesChanged;

    /// <summary>
    /// The settings of a button or the layer columns were changed: the project is marked changed (no undo step) and the
    /// view drawn again. Called when the settings close, so the chips are not built anew under them.
    /// </summary>
    public void StagesEdited()
    {
        if (Doc == null) return;
        if (_stagesChanged) { _stagesChanged = false; Doc.MarkModified(); }
        else Doc.NotifyViewChanged();
    }

    /// <summary>Binds a layer to a button or takes it off; a layer bound to a button on is shown. Not an undo step; <see cref="StagesEdited"/> after.</summary>
    public void SetStageLayer(StageView view, Guid layerId, bool bound)
    {
        if (Doc == null || Project is not { } p) return;
        if (bound ? !view.Layers.Add(layerId) : !view.Layers.Remove(layerId)) return;
        if (bound && view.On && p.FindLayer(layerId) is { } l) l.Visible = true;
        _stagesChanged = true;
    }

    /// <summary>A hole state a button shows, on or off; the view follows when the button is on. Not an undo step; <see cref="StagesEdited"/> after.</summary>
    public void SetStageState(StageView view, HoleState state, bool shown)
    {
        if (Doc == null || Project is not { } p) return;
        if (shown ? !view.States.Add(state) : !view.States.Remove(state)) return;
        if (view.On) { StageViews.Apply(p); DeselectHidden(); }
        _stagesChanged = true;
    }

    /// <summary>The hole data a button shows; the view follows when the button is on. Not an undo step; <see cref="StagesEdited"/> after.</summary>
    public void SetStageBasis(StageView view, HoleBasis basis)
    {
        if (Doc == null || Project is not { } p || view.HoleBasis == basis) return;
        view.HoleBasis = basis;
        if (view.On) StageViews.Apply(p, view);
        _stagesChanged = true;
    }

    /// <summary>The buttons on whose hole states the Visibility tab shows a state, by name ("Drilling · Fact"); empty when none.</summary>
    public IReadOnlyList<string> StageOwnersOfState(HoleState state) =>
        Project is { } p ? StageViews.Enabled(p).Where(v => v.States.Contains(state)).Select(StageName).ToList() : Array.Empty<string>();

    /// <summary>The first bindings of the stage buttons of a new blast (by the layer names, as they are shown too).</summary>
    private static void InitialStages(BlastProject p) => StageViews.Initial(p, Loc.Name);
}
