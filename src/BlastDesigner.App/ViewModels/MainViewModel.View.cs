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

    /// <summary>
    /// "Do not scale" of the hole labels: on, they keep their font size on the screen at any zoom; off, they grow and
    /// shrink with the plan. Kept in the project and, for the next new blast, in the settings.
    /// </summary>
    public void SetHoleLabelsFixedSize(bool on)
    {
        if (Doc == null) return;
        if (ViewSettings.HoleLabelsFixedSize == on && Settings.HoleLabelsFixedSize == on) return;
        ViewSettings.HoleLabelsFixedSize = on;
        Settings.HoleLabelsFixedSize = on;
        Settings.Save();
        Doc.NotifyViewChanged();
    }

    /// <summary>
    /// Colour of a hole label (null: its own colour back): kept in the project and, for the next new blast, in the
    /// settings.
    /// </summary>
    public void SetHoleLabelColor(HoleLabel label, uint? colour)
    {
        if (Doc == null) return;
        if (colour is uint c)
        {
            ViewSettings.LabelColors[label] = c | 0xFF000000;
            Settings.HoleLabelColors[label] = c | 0xFF000000;
        }
        else
        {
            ViewSettings.LabelColors.Remove(label);
            Settings.HoleLabelColors.Remove(label);
        }
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

    /// <summary>The eye by 2D and 3D (Shift+H): what is behind a surface paler, or everything drawn far to near as it comes.</summary>
    public void ToggleFadeHidden()
    {
        if (Doc == null) return;
        ToggleView(v => v.FadeHidden = !v.FadeHidden);
        SetStatus(ViewSettings.FadeHidden ? Loc.T("Hidden paler: what is behind a surface is drawn paler") : Loc.T("Everything drawn far to near"));
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

    /// <summary>Looks at the blast from a direction picked on the view cube: a face, an edge or a corner.</summary>
    public void ViewFrom(Vec3 from)
    {
        var (pitch, yaw) = ViewCube.ViewFrom(from);
        PushZoomHistory();
        View.Camera.Pitch = pitch;
        View.Camera.Yaw = yaw;
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

    /// <summary>
    /// The Depths chip: on, a loaded hole shows its charge column and an empty one the line from collar to toe; off,
    /// the collars only.
    /// </summary>
    public void ToggleDepths() => ToggleView(v => v.CollarsOnly = !v.CollarsOnly);
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
        // with stage buttons on, the layer is named by them and the date (it goes to them, Document.Followers)
        var suggested = StageViews.NewLayerName(Project!, StageName, DateTime.Today) ?? Loc.T("Layer") + " " + (Project!.Layers.Count + 1);
        var name = await Dialogs.Prompt("New layer", Loc.T("Layer name"), suggested);
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
        var edited = await Dialogs.EditObject("Layer properties", layer, g => LineStyleFields.Apply(g, Project!, layer: true), nameof(Layer.Id));
        if (edited == null) return;
        var id = layer.Id;
        Change("Layer properties", p =>
        {
            var l = p.FindLayer(id);
            if (l == null) return;
            l.Name = edited.Name; l.Group = edited.Group; l.Color = edited.Color; l.Visible = edited.Visible;
            l.Locked = edited.Locked; l.Frozen = edited.Frozen; l.ShowText = edited.ShowText; l.LineWidth = edited.LineWidth;
            l.LineWeight = edited.LineWeight; l.LineType = edited.LineType;
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
    public bool HasAnalysisDisplay => AnalysisOverlays.Count > 0 || Highlight.Ids.Count > 0 || Project?.Entities.Any(IsExclusionZone) == true;

    /// <summary>The polygon of the Exclusion zone calculator.</summary>
    public static bool IsExclusionZone(Entity e) => e is PolylineEntity { Kind: LineKind.ExclusionZone };

    /// <summary>
    /// Clear analysis (the chip over the plan, Esc, the menu): the displays, the highlight and the Visualization window
    /// go, and the exclusion zone drawn on the plan is deleted (one undo step brings it back).
    /// </summary>
    public void ClearAnalysis()
    {
        Dialogs.CloseWindow("Visualization");
        ClearOverlays();
        if (Project?.Entities.Any(IsExclusionZone) == true) Change("Clear analysis", p => p.Entities.RemoveAll(IsExclusionZone));
    }

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

    /// <summary>The isolines of the firing times are on the plan (by the Blast analysis or the Visualization window).</summary>
    public bool IsolinesShown => AnalysisOverlays.OfType<TimingContourOverlay>().Any(o => o.Contours);

    /// <summary>The Visualization window has its display on the plan (not the isolines of the Blast analysis window).</summary>
    public bool VisualizationShown => AnalysisOverlays.OfType<TimingContourOverlay>().Any(o => o != _analysisLayer);

    /// <summary>The layer the Blast analysis window put on the plan (its isolines are not those of the Visualization window).</summary>
    private IOverlay? _analysisLayer;

    /// <summary>The angle of initiation: opens the Blast analysis window on its tab.</summary>
    public void AngleOfInitiation() => BlastAnalysis(Rendering.BlastAnalysis.InitiationAngle);
    /// <summary>First movement: opens the Blast analysis window on its tab.</summary>
    public void FirstMovement() => BlastAnalysis(Rendering.BlastAnalysis.FirstMovement);
    /// <summary>Burden relief: opens the Blast analysis window on its tab.</summary>
    public void BurdenRelief() => BlastAnalysis(Rendering.BlastAnalysis.BurdenRelief);
    /// <summary>The Dynamics analysis button: the Blast analysis window on the tab last open.</summary>
    public void DynamicsAnalysis() { if (Doc != null) AnalysisWindows.BlastAnalysisWindow(this); }
    private void BlastAnalysis(Rendering.BlastAnalysis tab) { if (Doc != null) AnalysisWindows.BlastAnalysisWindow(this, tab); }

    /// <summary>
    /// Puts the analysis of the Blast analysis window on the main plan, as the one analysis display (like the others), or
    /// takes it off.
    /// </summary>
    public void ShowAnalysisOnPlan(IOverlay layer, bool show)
    {
        if (show) { AnalysisOverlays.Clear(); AnalysisOverlays.Add(layer); _analysisLayer = layer; }
        else { AnalysisOverlays.Remove(layer); if (_analysisLayer == layer) _analysisLayer = null; }
        View.Redraw();
        OverlaysChanged?.Invoke();
    }

    public void VibrationMap(bool airblast)
    {
        if (ToggleOff(x => x is VibrationMapOverlay m && m.Airblast == airblast)) return;
        var o = new VibrationMapOverlay { Airblast = airblast };
        o.Recalculate(Project!);
        ShowOverlay(o);
        ZoomAll();
    }

    // ---- the Visibility tab: switches, groups, padlocks and the chips over the plan --------------------------------

    /// <summary>
    /// A switch of the Visibility tab. <see cref="Group"/> is the group it is shown in; a switch of the topography or
    /// holes group can be pinned as a chip over the plan. <see cref="Tip"/> is the English text of its tool tip.
    /// </summary>
    public sealed record VisibilityToggle(string Id, string Label, string Group, Func<ViewState, bool> Get, Action<ViewState> Toggle, string Tip)
    {
        public bool Pinnable => Group != ViewKinds.Interface;
    }

    private static VisibilityToggle VisKind(string kind, string label, string tip) =>
        new(kind, label, ViewKinds.Topography, v => !v.HiddenKinds.Contains(kind), v => { if (!v.HiddenKinds.Remove(kind)) v.HiddenKinds.Add(kind); }, tip);

    private static VisibilityToggle VisFlag(string id, string label, string group, Func<ViewState, bool> get, Action<ViewState, bool> set, string tip) =>
        new(id, label, group, get, v => set(v, !get(v)), tip);

    /// <summary>Every switch of the Visibility tab, in the order of the tab.</summary>
    public static readonly IReadOnlyList<VisibilityToggle> VisibilityToggles = new[]
    {
        // topography
        VisFlag("Surfaces", "Surfaces", ViewKinds.Topography, v => v.ShowSurfaces, (v, on) => v.ShowSurfaces = on,
            "2D and 3D: the triangulated surfaces (topography, grade, top of rock), filled by their type or by elevation. Hiding them changes no calculation: lengths to the grade and volumes use the surfaces either way."),
        VisKind(ViewKinds.PolylineKind, "Polylines", "2D and 3D: open lines (crest, toe, markout lines and the like). The crest is used by the burden marker and the burden check whether it is shown or not."),
        VisKind(ViewKinds.PolygonKind, "Polygons", "2D and 3D: closed outlines (blast outline, zones, boundaries)."),
        VisKind(ViewKinds.CircleKind, "Circles", "2D and 3D: circles (zones, danger radii and the like)."),
        VisKind(ViewKinds.PointKind, "Points", "2D and 3D: monitoring points, grade pegs, survey stations, the north point and plain points, with their labels."),
        VisKind(ViewKinds.ArrowKind, "Arrows", "2D and 3D: arrows (direction of throw, notes)."),
        VisKind(ViewKinds.DimensionKind, "Dimensions", "2D and 3D: dimension lines with the distance written on them."),
        VisKind(ViewKinds.TextKind, "Text", "2D and 3D: text notes. The hole labels are not in it: they have their own switch, Hole labels."),
        // holes and ties
        new VisibilityToggle(ViewKinds.HoleKind, "Holes", ViewKinds.Holes, v => !v.HiddenKinds.Contains(ViewKinds.HoleKind),
            v => { if (!v.HiddenKinds.Remove(ViewKinds.HoleKind)) v.HiddenKinds.Add(ViewKinds.HoleKind); },
            "2D and 3D: all the holes, dummy holes too. Hidden holes cannot be selected but count in the calculations. Hole types and states are hidden with the filters below."),
        VisFlag("Ties", "Ties", ViewKinds.Holes, v => v.ShowTies, (v, on) => v.ShowTies = on,
            "2D and 3D: the surface ties between holes in the colour of their connector, with the delay."),
        new VisibilityToggle(ViewKinds.InitiationPointKind, "Initiation pt.", ViewKinds.Holes, v => !v.HiddenKinds.Contains(ViewKinds.InitiationPointKind),
            v => { if (!v.HiddenKinds.Remove(ViewKinds.InitiationPointKind)) v.HiddenKinds.Add(ViewKinds.InitiationPointKind); },
            "2D and 3D: the point the blast is fired from, with its time. The surface times and the firing order of the holes start from it."),
        VisFlag("Rows", "Rows", ViewKinds.Holes, v => v.ShowRows, (v, on) => v.ShowRows = on,
            "Plan: the lines of the rows the holes are laid out in. A row is picked by clicking its line only while the rows are shown."),
        VisFlag("HoleTrack", "Hole track", ViewKinds.Holes, v => !v.CollarsOnly, (v, on) => v.CollarsOnly = !on,
            "2D and 3D: a loaded hole shows its charge column, the decks in their product colours; an empty one the line from collar to toe with a dot at the toe. Off: collars only. Display only; the same decks give the explosive mass, quantities, costs and the vibration charge."),
        VisFlag("Collar", "Collar", ViewKinds.Holes, v => v.ShowCollars, (v, on) => v.ShowCollars = on,
            "2D and 3D: the collar circle, filled by hole state (designed — empty, drilled — pale, loaded — solid). When off, selected holes show no collar either; holes can still be picked. Display only."),
        VisFlag("DipTrack", "Dip track", ViewKinds.Holes, v => v.ShowDipTrack, (v, on) => v.ShowDipTrack = on,
            "2D and 3D: a thick cyan line down to the dipped depth with a mark at its end, only for dipped holes. The dipped length replaces the design length in loading, quantities and the tolerance check."),
        VisFlag("GradePoint", "Grade point", ViewKinds.Holes, v => v.ShowGradePoint, (v, on) => v.ShowGradePoint = on,
            "2D and 3D: a green diamond where the hole crosses the design grade (length minus subdrill). Shows the bench floor; the bench height from it is used in quantities, volumes and the fragmentation prediction."),
        VisFlag("Deviation", "Deviation", ViewKinds.Holes, v => v.ShowDeviation, (v, on) => v.ShowDeviation = on,
            "2D and 3D: red lines with \"Δ\" from the design collar to the actual collar and from the design toe to the surveyed toe. The same deviations are compared with the tolerances in the drilling tolerance check."),
        VisFlag("BurdenMarker", "Burden marker", ViewKinds.Holes, v => v.ShowBurdenMarker, (v, on) => v.ShowBurdenMarker = on,
            "Plan: a dashed violet line with \"B\" from each front-row hole to the nearest point of the crest. Needs a crest line; the same burden is used by the burden check and the face burst (flyrock) prediction."),
        VisFlag("Profile", "MWD profile", ViewKinds.Holes, v => v.ShowProfile, (v, on) => v.ShowProfile = on,
            "2D and 3D: rock hardness from the measure-while-drilling log along the track, blue soft to red hard, only for holes with a log. Loading rules use it to switch to the hard or soft rock product and lengthen stemming in a soft collar."),
        VisFlag("HoleLabels", "Hole labels", ViewKinds.Holes, v => v.ShowText, (v, on) => v.ShowText = on,
            "2D and 3D: all the hole labels at once (numbers, times, lengths and the rest). Which labels and on which side is set in the columns below."),
        // interface
        VisFlag("Grid", "Grid", ViewKinds.Interface, v => v.ShowGrid, (v, on) => v.ShowGrid = on,
            "2D and 3D: the coordinate grid with the coordinates at its edges (G)."),
        VisFlag("Axes", "Axes", ViewKinds.Interface, v => v.ShowAxes, (v, on) => v.ShowAxes = on,
            "2D and 3D: the X, Y, Z axes in the corner."),
        VisFlag("HoleLegend", "Hole legend", ViewKinds.Interface, v => v.ShowHoleLegend, (v, on) => v.ShowHoleLegend = on,
            "Printed plans: the hole types with their colours and counts. On the screen it shows only at the Layout stage and with two or more hole types."),
        VisFlag("TieLegend", "Tie legend", ViewKinds.Interface, v => v.ShowTieLegend, (v, on) => v.ShowTieLegend = on,
            "Printed plans: the connectors of the ties with their colours and counts. Not drawn on the screen: the connectors are listed in the tie palette."),
        VisFlag("ReverseDrawOrder", "Reverse draw order", ViewKinds.Interface, v => v.ReverseDrawOrder, (v, on) => v.ReverseDrawOrder = on,
            "Draw the objects in reverse order: what was underneath comes on top. Helps where lines or surfaces cover each other.")
    };

    public static VisibilityToggle? FindToggle(string id) => VisibilityToggles.FirstOrDefault(t => t.Id == id);

    /// <summary>At most so many switches are pinned as chips over the plan.</summary>
    public const int MaxPinnedChips = 5;

    /// <summary>The switches pinned as chips over the plan, in order; the chips of before when none were chosen.</summary>
    public IReadOnlyList<string> PinnedChips => Settings.PinnedChips ?? new List<string> { "HoleLabels", "HoleTrack", "Ties" };

    public bool IsPinned(string id) => PinnedChips.Contains(id);

    /// <summary>Pins a switch as a chip over the plan or takes it off; false when five are pinned already.</summary>
    public bool TogglePin(string id)
    {
        var pins = PinnedChips.Where(p => FindToggle(p) != null).ToList();
        if (!pins.Remove(id))
        {
            if (pins.Count >= MaxPinnedChips || FindToggle(id) is not { Pinnable: true }) return false;
            pins.Add(id);
        }
        Settings.PinnedChips = pins;
        Settings.Save();
        RefreshAll();
        return true;
    }

    public void ToggleVisibility(VisibilityToggle t) => ToggleView(t.Toggle);

    /// <summary>The eye of a group of the Visibility tab: everything in it hidden, or back as its own switches have it.</summary>
    public void ToggleGroupHidden(string group) => ToggleView(v => { if (!v.HiddenGroups.Remove(group)) v.HiddenGroups.Add(group); });

    /// <summary>The padlock of a group: its objects stay drawn but cannot be selected; a selection of them is dropped.</summary>
    public void ToggleGroupLocked(string group)
    {
        if (Doc == null) return;
        var locking = !ViewSettings.LockedGroups.Contains(group);
        ToggleView(v => { if (!v.LockedGroups.Remove(group)) v.LockedGroups.Add(group); });
        if (locking)
        {
            var keep = Doc.Selection.Where(id => Project!.FindEntity(id) is not { } e || !ViewKinds.Locked(ViewSettings, e)).ToList();
            if (keep.Count != Doc.Selection.Count) Doc.Select(keep);
        }
    }

    /// <summary>
    /// "Show all" of the Visibility tab: every object back, hidden one by one, by its kind, its group, the ties and
    /// surfaces switches or the hole filters. The display switches of the holes and the interface are left as they are.
    /// </summary>
    public void ShowEverything()
    {
        if (Doc == null) return;
        HiddenEntities.Clear();
        ToggleView(v =>
        {
            v.HiddenKinds.Clear(); v.HiddenGroups.Clear();
            v.ShowTies = true; v.ShowSurfaces = true;
            v.HiddenHoleTypes.Clear(); v.HiddenMaterialTypes.Clear(); v.HiddenStates.Clear();
        });
    }
}
