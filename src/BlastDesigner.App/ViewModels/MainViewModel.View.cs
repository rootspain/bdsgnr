using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.App.Views;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.Rendering;

namespace BlastDesigner.App.ViewModels;

public sealed partial class MainViewModel
{
    private ViewState ViewSettings => Project!.View;

    /// <summary>Changes a view flag. View settings are saved with the project but are not undo steps.</summary>
    public void ToggleView(Action<ViewState> change)
    {
        if (Doc == null) return;
        var before = ViewFlags(ViewSettings);
        change(ViewSettings);
        // each flag that changed goes to the usage statistics, with its old value (to find flags switched straight back)
        foreach (var (name, value) in ViewFlags(ViewSettings))
            if (before.TryGetValue(name, out var old) && old != value) IO.UsageLog.Track(IO.UsageLog.Flag, name, value, old);
        Doc.NotifyViewChanged();
    }

    /// <summary>Font size of the hole labels: kept in the project and, for the next new blast, in the settings.</summary>
    public void SetHoleLabelFontSize(int size)
    {
        if (Doc == null) return;
        size = Math.Clamp(size, ViewState.MinHoleLabelFontSize, ViewState.MaxHoleLabelFontSize);
        if (ViewSettings.HoleLabelFontSize == size && Settings.HoleLabelFontSize == size) return;
        ViewSettings.HoleLabelFontSize = size;
        Settings.HoleLabelFontSize = size;
        Settings.Save();
        Doc.NotifyViewChanged();
    }

    private static readonly System.Reflection.PropertyInfo[] FlagProperties =
        typeof(ViewState).GetProperties().Where(p => p.CanWrite && (p.PropertyType == typeof(bool) || p.PropertyType.IsEnum)).ToArray();

    /// <summary>The on/off and choice settings of the view; each bit of a [Flags] choice (hole labels) is a flag of its own.</summary>
    private static Dictionary<string, string> ViewFlags(ViewState v)
    {
        var flags = new Dictionary<string, string>();
        foreach (var p in FlagProperties)
        {
            var value = p.GetValue(v);
            if (value is bool b) flags[p.Name] = b ? "on" : "off";
            else if (value is Enum e && p.PropertyType.IsDefined(typeof(FlagsAttribute), false))
            {
                var bits = Convert.ToInt64(e);
                foreach (var item in Enum.GetValues(p.PropertyType).Cast<Enum>())
                {
                    var bit = Convert.ToInt64(item);
                    if (bit != 0 && (bit & (bit - 1)) == 0) flags[$"{p.Name}.{item}"] = (bits & bit) != 0 ? "on" : "off";
                }
            }
            else if (value != null) flags[p.Name] = value.ToString()!;
        }
        return flags;
    }

    public void ViewEverything()
    {
        if (Doc == null) return;
        HiddenEntities.Clear();
        foreach (var l in Project!.Layers) l.Visible = true;
        var v = ViewSettings;
        v.HiddenHoleTypes.Clear(); v.HiddenMaterialTypes.Clear(); v.HiddenStates.Clear();
        v.CollarsOnly = false;
        AnalysisOverlays.Clear();
        AnimationTime = null;
        Doc.NotifyViewChanged();
        View.ZoomAll();
    }

    public void PlanView()
    {
        PushZoomHistory();
        View.Camera.Pitch = 90;
        View.Camera.Yaw = 0;
        View.Redraw();
    }

    /// <summary>Rotates the plan so the blast's free face points up the screen.</summary>
    public void BlastView()
    {
        if (Doc == null) return;
        PushZoomHistory();
        View.Camera.Pitch = 90;
        View.Camera.Yaw = Project!.Header.BlastOrientation;
        View.ZoomAll();
    }

    public void View3D()
    {
        PushZoomHistory();
        View.Camera.Pitch = 35;
        View.Camera.Yaw = Project?.Header.BlastOrientation + 180 ?? 0;
        View.ZoomAll();
    }

    public void SetBlastOrientation()
    {
        if (Doc == null) return;
        SetTool(new PickPointsTool(this, "Blast orientation", "Click two points: from the back of the blast towards the free face.", 2, 2, false, pts =>
        {
            var b = GeoMath.Bearing(pts[0], pts[1]);
            Change("Set blast orientation", p => p.Header.BlastOrientation = b);
            SetStatus(Loc.F("Blast orientation {0:0.0}°", b));
        })
        { Repeat = false, Measure = (pts, h) => pts.Count == 1 ? $"{GeoMath.Bearing(pts[0], h):0.0}°" : null });
    }

    public void ZoomIn() { PushZoomHistory(); View.Camera.ZoomAt(View.Camera.Width / 2, View.Camera.Height / 2, 1.5); View.Redraw(); }
    public void ZoomOut() { PushZoomHistory(); View.Camera.ZoomAt(View.Camera.Width / 2, View.Camera.Height / 2, 1 / 1.5); View.Redraw(); }
    public void ZoomAll() { PushZoomHistory(); View.ZoomAll(); }
    public void ResetOrientation() { View.Camera.Yaw = 0; View.Redraw(); }

    /// <summary>Zooms to the selection, or to everything (View last / View all).</summary>
    public void ZoomSelection()
    {
        if (Doc == null || Doc.Selection.Count == 0) { ZoomAll(); return; }
        PushZoomHistory();
        var b = Bounds3.Empty;
        foreach (var e in Doc.SelectedEntities) b.Add(e.GetBounds());
        View.Camera.Fit(b, 0.2);
        View.Redraw();
    }

    public IEnumerable<MenuEntry> ViewContextMenu()
    {
        yield return MenuEntry.Item("Zoom all", ZoomAll);
        yield return MenuEntry.Item("Zoom in", ZoomIn);
        yield return MenuEntry.Item("Zoom out", ZoomOut);
        yield return MenuEntry.Item("Undo zoom", UndoZoom);
        yield return MenuEntry.Item("Plan view", PlanView);
        yield return MenuEntry.Item("3D view", View3D);
        yield return MenuEntry.Item("Reset orientation", ResetOrientation);
        yield return MenuEntry.Item("View selection", ZoomSelection);
        yield return MenuEntry.Check("Coordinates box", () => Settings.ShowCoordinatesBox, () => { Settings.ShowCoordinatesBox = !Settings.ShowCoordinatesBox; RefreshAll(); });
    }

    // ---- visibility filters -----------------------------------------------------------

    public void ToggleHoleType(int type) => ToggleView(v => { if (!v.HiddenHoleTypes.Remove(type)) v.HiddenHoleTypes.Add(type); });
    public void ToggleMaterialType(int type) => ToggleView(v => { if (!v.HiddenMaterialTypes.Remove(type)) v.HiddenMaterialTypes.Add(type); });
    public void ToggleState(HoleState s) => ToggleView(v => { if (!v.HiddenStates.Remove(s)) v.HiddenStates.Add(s); });

    /// <summary>Shows only holes with the given logging exception (or all when None).</summary>
    public void FilterException(HoleException ex)
    {
        if (Doc == null) return;
        HiddenEntities.Clear();
        if (ex != HoleException.None)
            foreach (var h in Project!.Holes.Where(h => !h.Exceptions.HasFlag(ex))) HiddenEntities.Add(h.Id);
        Doc.NotifyViewChanged();
    }

    public void ToggleLabel(HoleLabelMode m) => ToggleView(v => v.HoleLabels ^= m);
    public bool IsTimingLabelVisible(TimingLabelMode mode)
    {
        var view = Project?.View;
        return view != null && IsTimingLabelVisible(view, mode);
    }

    public void ToggleTimingLabel(TimingLabelMode mode)
    {
        if (Doc == null || mode == TimingLabelMode.None) return;
        ToggleView(v =>
        {
            var enabled = IsTimingLabelVisible(v, mode);
            switch (mode)
            {
                case TimingLabelMode.SurfaceTime: v.ShowSurfaceTimeLabel = !enabled; break;
                case TimingLabelMode.InholeTime: v.ShowHoleTimeLabel = !enabled; break;
                case TimingLabelMode.DelayNumber: v.ShowInholeDelayLabel = !enabled; break;
                case TimingLabelMode.HoleSequence: v.ShowHoleSequenceLabel = !enabled; break;
            }
            if (v.TimingLabels == mode) v.TimingLabels = TimingLabelMode.None;
        });
    }

    private static bool IsTimingLabelVisible(ViewState view, TimingLabelMode mode) => mode switch
    {
        TimingLabelMode.SurfaceTime => view.ShowSurfaceTimeLabel || view.TimingLabels == mode,
        TimingLabelMode.InholeTime => view.ShowHoleTimeLabel || view.TimingLabels == mode,
        TimingLabelMode.DelayNumber => view.ShowInholeDelayLabel || view.TimingLabels == mode,
        TimingLabelMode.HoleSequence => view.ShowHoleSequenceLabel || view.TimingLabels == mode,
        _ => false
    };

    public void SetTimingLabel(TimingLabelMode m) => ToggleTimingLabel(m);

    /// <summary>
    /// With electronic detonators the firing order matters more than the hole number: when the hole number is shown and
    /// the hole order is not, swaps them (the timing line tool calls this as it opens).
    /// </summary>
    public void PreferHoleOrderLabel()
    {
        var p = Project;
        if (p == null || !p.View.HoleLabels.HasFlag(HoleLabelMode.Label) || IsTimingLabelVisible(TimingLabelMode.HoleSequence)) return;
        if (!p.Holes.Any(h => Charging.Primers(h).Any(x => p.FindProduct(x.Primer.DetonatorId)?.Kind == ProductKind.ElectronicDetonator))) return;
        ToggleView(v =>
        {
            v.HoleLabels &= ~HoleLabelMode.Label;
            v.ShowHoleSequenceLabel = true;
        });
    }

    // ---- layers -----------------------------------------------------------------------

    public void HideSelectedLayers()
    {
        var layers = Doc!.SelectedEntities.Select(e => e.LayerId).ToHashSet();
        foreach (var l in Project!.Layers.Where(l => layers.Contains(l.Id))) l.Visible = false;
        Doc.ClearSelection();
        Doc.NotifyViewChanged();
    }

    public void LockSelectedLayers(bool locked)
    {
        var layers = Doc!.SelectedEntities.Select(e => e.LayerId).ToHashSet();
        Change(locked ? "Lock layers" : "Unlock layers", p => { foreach (var l in p.Layers.Where(l => layers.Contains(l.Id))) l.Locked = locked; });
    }

    public void SetLayerFlag(Layer layer, Action<Layer> change, bool undoable = false)
    {
        if (undoable)
        {
            var id = layer.Id;
            Change("Layer properties", p => { if (p.FindLayer(id) is { } l) change(l); });
        }
        else
        {
            change(layer);
            Doc?.NotifyViewChanged();
        }
    }

    public async Task NewLayer(string group = "")
    {
        var name = await Dialogs.Prompt("New layer", Loc.T("Layer name"), Loc.T("Layer") + " " + (Project!.Layers.Count + 1));
        if (string.IsNullOrWhiteSpace(name)) return;
        Change("New layer", p => p.Layers.Add(new Layer { Name = name, Group = group, Color = 0xFF334155 }));
    }

    public async Task NewGroup()
    {
        var name = await Dialogs.Prompt("New group", Loc.T("Group name"), Loc.T("Group"));
        if (!string.IsNullOrWhiteSpace(name)) await NewLayer(name);
    }

    public async Task EditLayer(Layer layer)
    {
        var edited = await Dialogs.EditObject("Layer properties", layer, nameof(Layer.Id));
        if (edited == null) return;
        var id = layer.Id;
        Change("Layer properties", p =>
        {
            var l = p.FindLayer(id);
            if (l == null) return;
            l.Name = edited.Name; l.Group = edited.Group; l.Color = edited.Color; l.Visible = edited.Visible;
            l.Locked = edited.Locked; l.Frozen = edited.Frozen; l.ShowText = edited.ShowText; l.LineWidth = edited.LineWidth;
        });
    }

    public void IsolateLayer(Layer layer)
    {
        foreach (var l in Project!.Layers) l.Visible = l.Id == layer.Id;
        Doc!.NotifyViewChanged();
    }

    public void ShowGroup(string group, bool visible)
    {
        foreach (var l in Project!.Layers.Where(l => l.Group == group)) l.Visible = visible;
        Doc!.NotifyViewChanged();
    }

    public void MakeDefaultLayer(Layer layer) => Change("Make default layer", p => p.DefaultLayerId = layer.Id);

    public void SelectLayerObjects(Layer layer, bool select) =>
        Doc!.Select(Project!.Entities.Where(e => e.LayerId == layer.Id).Select(e => e.Id), select ? Domain.SelectionMode.Add : Domain.SelectionMode.Remove);

    public async Task DeleteLayerObjects(Layer layer)
    {
        if (!await Dialogs.Confirm(Loc.F("Delete all objects on layer {0}?", layer.Name))) return;
        var id = layer.Id;
        Change("Delete objects", p => p.Entities.RemoveAll(e => e.LayerId == id));
    }

    public async Task DeleteLayer(Layer layer)
    {
        if (Project!.Layers.Count <= 1) return;
        var count = Project.Entities.Count(e => e.LayerId == layer.Id);
        if (count > 0 && !await Dialogs.Confirm(Loc.F("Layer {0} has {1} objects. Delete the layer and its objects?", layer.Name, count))) return;
        var id = layer.Id;
        Change("Delete layer", p =>
        {
            p.Entities.RemoveAll(e => e.LayerId == id);
            p.Layers.RemoveAll(l => l.Id == id);
            if (p.DefaultLayerId == id) p.DefaultLayerId = p.Layers[0].Id;
        });
    }

    /// <summary>Moves the selected objects to a layer.</summary>
    public void MoveSelectionToLayer(Layer layer)
    {
        var ids = Doc!.Selection.ToHashSet();
        var lid = layer.Id;
        Change("Move to layer", p => { foreach (var e in p.Entities.Where(e => ids.Contains(e.Id))) e.LayerId = lid; });
    }

    // ---- analysis windows and overlays -------------------------------------------------

    /// <summary>Raised when an analysis display is added to or removed from the plan (the shell shows a "clear" chip).</summary>
    public event Action? OverlaysChanged;

    public void ShowOverlay(IOverlay overlay, bool exclusive = true)
    {
        if (exclusive) AnalysisOverlays.Clear();
        AnalysisOverlays.Add(overlay);
        View.Redraw();
        OverlaysChanged?.Invoke();
    }

    /// <summary>
    /// Rings round the holes on the plan (the Highlight buttons of the check windows); like the analysis displays, they
    /// go with Clear analysis or Esc.
    /// </summary>
    public void SetHighlight(IEnumerable<Guid> ids)
    {
        Highlight.Ids.Clear();
        foreach (var id in ids) Highlight.Ids.Add(id);
        View.Redraw();
        OverlaysChanged?.Invoke();
    }

    /// <summary>An analysis display or a highlight is on the plan.</summary>
    public bool HasAnalysisDisplay => AnalysisOverlays.Count > 0 || Highlight.Ids.Count > 0;

    public void ClearOverlays()
    {
        AnalysisOverlays.Clear();
        AnimationTime = null;
        Highlight.Ids.Clear();
        View.Redraw();
        OverlaysChanged?.Invoke();
    }

    /// <summary>Same analysis display shown again = switch it off (map buttons work as toggles).</summary>
    private bool ToggleOff(Func<IOverlay, bool> same)
    {
        if (!AnalysisOverlays.Any(same)) return false;
        ClearOverlays();
        return true;
    }

    public void VisualizeTiming() { if (Doc != null) AnalysisWindows.VisualizeTiming(this); }
    public void AngleOfInitiation() => VectorOverlay(TimingVectorOverlay.Mode.InitiationAngle);
    /// <summary>First movement: opens the First Movement window, which also puts the arrows on the plan.</summary>
    public void FirstMovement() { if (Doc != null) AnalysisWindows.FirstMovementWindow(this); }

    /// <summary>Shows (as the one analysis display, like the others) or hides the first-movement arrows on the main plan.</summary>
    public void ShowFirstMovementOnPlan(FirstMovementOverlay overlay, bool show)
    {
        if (show) AnalysisOverlays.Clear();
        else AnalysisOverlays.RemoveAll(o => o is FirstMovementOverlay);
        if (show) AnalysisOverlays.Add(overlay);
        View.Redraw();
        OverlaysChanged?.Invoke();
    }
    /// <summary>Burden relief: opens its window, which also fills the triangles of the main plan.</summary>
    public void BurdenRelief() { if (Doc != null) AnalysisWindows.BurdenReliefWindow(this); }
    private double NeighbourRadius => Math.Max(Project!.Design.Burden, Project.Design.Spacing) * 1.6;

    private void VectorOverlay(TimingVectorOverlay.Mode mode)
    {
        if (ToggleOff(o => o is TimingVectorOverlay v && v.Show == mode)) return;
        ShowOverlay(new TimingVectorOverlay { Show = mode, NeighbourRadius = NeighbourRadius });
    }

    public void VibrationMap(bool airblast)
    {
        if (ToggleOff(x => x is VibrationMapOverlay m && m.Airblast == airblast)) return;
        var o = new VibrationMapOverlay { Airblast = airblast };
        o.Recalculate(Project!);
        ShowOverlay(o);
        ZoomAll();
    }
}
