using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.App.Views;
using BlastDesigner.Domain.Model;
using static BlastDesigner.App.Tools.MenuEntry;

namespace BlastDesigner.App.ViewModels;

/// <summary>The main menu and toolbars, declared once and built into Avalonia controls.</summary>
public sealed partial class MainViewModel
{
    private bool P() => HasProject;
    private bool S() => HasSelection;
    private bool W() => HasProject && Doc?.IsReadOnly == false;

    private static Action Run(Func<Task> f) => () => _ = RunSafe(f);

    private static async Task RunSafe(Func<Task> f)
    {
        try { await f(); }
        catch (Exception ex) { Dialogs.ShowError(ex); }
    }

    public List<MenuEntry> BuildMenu() => AddVersion6Menus(BuildBaseMenu());

    private List<MenuEntry> BuildBaseMenu() => new()
    {
        Sub("File",
            Item("New", Run(New), null, "Ctrl+N"),
            Item("Open…", Run(Open), null, "Ctrl+O"),
            Item("Save", Run(async () => await Save()), W, "Ctrl+S"),
            Item("Save as…", Run(async () => await SaveAs()), P),
            Item("Save as template…", Run(SaveAsTemplate), P),
            Item("Close", Run(Close), P),
            Separator,
            Item("Merge…", Run(Merge), P),
            Sub("Import",
                Item("Text file…", Run(() => ImportText())),
                Item("DXF / DWG…", Run(() => ImportCad())),
                Item("String file (.str)…", Run(() => ImportStr(null, false))),
                Item("String file, ignore diameters…", Run(() => ImportStr(null, true))),
                Item("Drill plan (XML)…", Run(ImportDrillPlan)),
                Item("Wavefront OBJ file…", Run(() => ImportObj()))),
            Sub("Export",
                Item("Text file…", Run(ExportHolesText), P),
                Item("DXF…", Run(ExportDxf), P),
                Item("String file (.str)…", Run(ExportStr), P),
                Item("Quantities…", Run(ExportQuantities), P),
                Item("Drill plan (XML)…", Run(ExportDrillPlan), P),
                Item("Firing data…", Run(() => ExportFiring(false)), P),
                Item("Firing data with cosines…", Run(() => ExportFiring(true)), P),
                Item("Deck table (csv)…", Run(ExportDeckTable), P),
                Item("Load sheets (csv)…", Run(ExportLoadSheet), P),
                Item("Surface to OBJ…", Run(ExportObj), P)),
            Sub("Print",
                Item("Reports…", Run(PrintReports), P, "Ctrl+P"),
                Item("Plan…", Run(PrintPlan), P)),
            Separator,
            new MenuEntry { Header = "Recent files", Dynamic = () => Settings.RecentFiles.Select((f, i) => Item($"{i + 1} {f}", Run(() => OpenRecent(f)))) },
            Separator,
            Item("Exit", Run(Exit), null, "Alt+F4")),
        Sub("Edit",
            new MenuEntry { Header = "Undo", Action = Undo, IsEnabled = () => Doc?.CanUndo == true, Gesture = "Ctrl+Z" },
            new MenuEntry { Header = "Undo list", Dynamic = () => (Doc?.UndoNames ?? new List<string>()).Select((n, i) => Item($"{i + 1}. {Loc.T(n)}", () => UndoSteps(i + 1))) },
            new MenuEntry { Header = "Redo", Action = Redo, IsEnabled = () => Doc?.CanRedo == true, Gesture = "Ctrl+Y" },
            Separator,
            Item("Cut", Cut, S, "Ctrl+X"),
            Item("Copy", Copy, S, "Ctrl+C"),
            Item("Paste", Paste, P, "Ctrl+V"),
            Item("Copy image", CopyImage, P),
            Sub("Select",
                Item("All", SelectAll, P, "Ctrl+A"),
                Item("None", () => Doc?.ClearSelection(), P, "Ctrl+D"),
                Item("Like", SelectLike, S),
                Item("Exact", SelectExact, S),
                Item("Invert", InvertSelection, P)),
            Item("Edit selected…", EditSelected, S, "Ctrl+E"),
            Item("Delete", DeleteSelected, S, "Delete"),
            Separator,
            Item("Program settings…", Run(EditProgramSettings)),
            Item("Templates…", Run(ManageTemplates)),
            Item("Visual style…", Run(EditVisualStyle)),
            Separator,
            Item("Blast properties…", Run(EditBlastProperties), P),
            Item("Design data…", Run(EditDesignData), P),
            Item("Hole and material types…", Run(EditHoleTypes), P),
            Item("Available resources…", Run(EditResources), P),
            Item("Blast outcomes…", Run(EditOutcomes), P),
            Item("Summary block…", Run(EditSummaryBlock), P),
            Item("Resource cost schema…", Run(EditCostSchema), P),
            Item("Vibration site constants…", Run(EditVibrationModel), P)),
        Sub("View",
            Item("View everything", ViewEverything, P),
            Check("Collars only", () => Project?.View.CollarsOnly == true, () => ToggleView(v => v.CollarsOnly = !v.CollarsOnly), P),
            Item("Plan view", PlanView, P),
            Item("3D view", View3D, P),
            Item("Blast view", BlastView, P),
            Item("Set blast orientation", SetBlastOrientation, P),
            Check("Reverse draw order", () => Project?.View.ReverseDrawOrder == true, () => ToggleView(v => v.ReverseDrawOrder = !v.ReverseDrawOrder), P),
            Separator,
            Check("Layer manager", () => Window.LayersVisible, Window.ToggleLayers),
            Check("Properties", () => Window.PropertiesVisible, Window.TogglePropertiesPanel),
            Check("Table view", () => Window.TableVisible, Window.ToggleTable),
            Item("Attachment manager…", () => AnalysisWindows.AttachmentManager(this), P),
            Separator,
            Item("Overview", () => AnalysisWindows.Overview(this), P),
            Item("Fly through", () => AnalysisWindows.FlyThrough(this), P),
            Item("Single hole view (bullseye)", () => AnalysisWindows.SingleHole(this), P),
            Item("Exceptions view", () => AnalysisWindows.Exceptions(this), P),
            Item("Front row view", () => AnalysisWindows.FrontRow(this), P),
            Item("Horizontal slice", () => AnalysisWindows.HorizontalSlice(this), P),
            Item("Row check view", () => AnalysisWindows.RowCheck(this), P),
            Item("Section view", () => AnalysisWindows.SectionTool(this), P),
            Item("Markout", () => AnalysisWindows.Markout(this), P),
            Check("Loading", () => Project?.View.ShowLoading == true, () => ToggleView(v => v.ShowLoading = !v.ShowLoading), P),
            Separator,
            new MenuEntry { Header = "Hole types", Dynamic = () => HoleTypeMenu() },
            new MenuEntry { Header = "Material types", Dynamic = () => MaterialMenu() },
            new MenuEntry { Header = "Hole state", Dynamic = () => Enum.GetValues<HoleState>().Select(s => Check(Loc.Enum(s), () => Project?.View.HiddenStates.Contains(s) == false, () => ToggleState(s), P)) },
            new MenuEntry { Header = "Logging exceptions", Dynamic = () => Enum.GetValues<HoleException>().Select(e => Item(e == HoleException.None ? "All" : Loc.Enum(e), () => FilterException(e), P)) },
            Check("Grid", () => Project?.View.ShowGrid == true, () => ToggleView(v => v.ShowGrid = !v.ShowGrid), P),
            Check("Axes", () => Project?.View.ShowAxes == true, () => ToggleView(v => v.ShowAxes = !v.ShowAxes), P),
            Check("Hole legend", () => Project?.View.ShowHoleLegend == true, () => ToggleView(v => v.ShowHoleLegend = !v.ShowHoleLegend), P),
            Check("Tie legend", () => Project?.View.ShowTieLegend == true, () => ToggleView(v => v.ShowTieLegend = !v.ShowTieLegend), P),
            new MenuEntry { Header = "Hole labels", Dynamic = () => Enum.GetValues<HoleLabelMode>().Where(m => m != HoleLabelMode.None).Select(m => Check(Loc.Enum(m), () => Project?.View.HoleLabels.HasFlag(m) == true, () => ToggleLabel(m), P)) },
            new MenuEntry { Header = "Timing", Dynamic = () => Enum.GetValues<TimingLabelMode>().Where(m => m != TimingLabelMode.None).Select(m => Check(Loc.Enum(m), () => IsTimingLabelVisible(m), () => ToggleTimingLabel(m), P)) },
            Sub("Show",
                Check("Ties", () => Project?.View.ShowTies == true, () => ToggleView(v => v.ShowTies = !v.ShowTies), P),
                Check("Rows", () => Project?.View.ShowRows == true, () => ToggleView(v => v.ShowRows = !v.ShowRows), P),
                Check("Text", () => Project?.View.ShowText == true, () => ToggleView(v => v.ShowText = !v.ShowText), P),
                Check("Surfaces", () => Project?.View.ShowSurfaces == true, () => ToggleView(v => v.ShowSurfaces = !v.ShowSurfaces), P)),
            Item("Clear analysis display", ClearOverlays, P)),
        Sub("Calculations",
            Item("Visualize timing", VisualizeTiming, P),
            Item("Angle of initiation", AngleOfInitiation, P),
            Item("First movement", FirstMovement, P),
            Item("Burden relief", BurdenRelief, P),
            Item("Time envelope", () => AnalysisWindows.TimeEnvelope(this), P),
            Item("Quantities", () => AnalysisWindows.Quantities(this, false), P),
            Item("Project costs", () => AnalysisWindows.Quantities(this, true), P),
            Item("Electronic logging report", () => AnalysisWindows.LoggingReport(this), P),
            Sub("Vibration",
                Item("Vibration", () => AnalysisWindows.VibrationPrediction(this), P),
                Item("Vibration map", () => VibrationMap(false), P),
                Item("Airblast map", () => VibrationMap(true), P))),
        Sub("Tools",
            Sub("Auto loading",
                Item("Apply", Run(ApplyLoadingRules), W),
                Item("Edit loading rules…", Run(EditLoadingRules), P),
                Item("Clear", Run(ClearLoadingRules), W)),
            Sub("Convert",
                Item("Selected objects to points", () => ConvertToPoints(false), S),
                Item("Selected objects to group of points", () => ConvertToPoints(true), S),
                Item("Selected objects to a polyline", () => ConvertToPolyline(false), S),
                Item("Selected objects to a polygon", () => ConvertToPolyline(true), S),
                Item("Selected dummy holes to holes", () => ConvertDummy(false), S),
                Item("Selected holes to dummy holes", () => ConvertDummy(true), S),
                Item("Selected circles to polygons", CirclesToPolygons, S),
                Item("Selected polygons to polylines", () => SetClosed(false), S),
                Item("Selected polylines to polygons", () => SetClosed(true), S),
                Item("Selected points to monitoring points", PointsToMonitoring, S)),
            Sub("Electronic timing",
                Item("Add/Subtract time from holes…", Run(AddSubtractTime), P),
                Item("Adjust intervals…", Run(AdjustIntervals), P),
                Item("Adjust increments of selected holes…", Run(AdjustIncrements), P),
                Item("Copy times between horizons…", Run(CopyTimesBetweenHorizons), P),
                Item("Deprogram holes", Run(() => Deprogram(false)), P),
                Item("Deprogram manually set holes", Run(() => Deprogram(true)), P),
                Item("Find hole by detonator ID…", Run(() => FindHole("label")), P),
                Item("Re-apply timing", ReapplyTiming, P),
                Item("Convert pyrotechnic devices to electronic", Run(ConvertToElectronic), P),
                Item("Clear exceptions", Run(ClearExceptions), P),
                Item("Preferences…", EditElectronicSettings, P)),
            Sub("Holes",
                Item("Add at points", Run(AddHolesAtPoints), P),
                Item("Add along selected polyline…", Run(AddHolesAlongPolyline), P),
                Item("Fill selected polygon…", Run(FillSelectedPolygon), P),
                Item("Move to selected points…", Run(MoveHolesToPoints), P),
                Item("Create points at collar", () => CreatePointsAt("collar"), S),
                Item("Create points at grade", () => CreatePointsAt("grade"), S),
                Item("Create points at toe", () => CreatePointsAt("toe"), S),
                Item("Create polyline of bore track", CreateBoreTrackPolylines, S),
                Item("Move collars onto surface…", Run(DrapeCollarsOnSurface), P),
                Item("Set lengths to grade surface…", Run(LengthsToGrade), P),
                Separator,
                Item("Empty", Run(EmptyHoles), P),
                Item("Edit loading…", Run(() => EditHoleLoading()), P),
                Item("Join like decks", Run(JoinLikeDecks), P),
                Item("Release for loading", () => SetHoleState(HoleState.Released), S),
                Item("Mark drilled", () => SetHoleState(HoleState.Drilled), S),
                Item("Mark fired", () => SetHoleState(HoleState.Fired), S),
                Item("Set loading factor…", Run(SetLoadingFactor), P),
                Item("Reset design tracks", Run(ResetDesignTracks), P),
                Item("Correct slice direction of selected…", Run(CorrectSliceDirection), S),
                Item("Drill holes between lines…", Run(DrillBetweenLines), P),
                Separator,
                Item("Renumber…", Run(Renumber), P),
                Item("Check for duplicates by coordinates…", Run(CheckDuplicates), P),
                Item("Find hole…", Run(() => FindHole("label")), P),
                Item("Find empty hole", Run(() => FindHole("empty")), P),
                Item("Find misfire hole", Run(() => FindHole("misfire")), P),
                Item("Re-label duplicate holes", RelabelDuplicates, P),
                Item("Lock holes", () => LockHoles(true), S),
                Item("Unlock holes", () => LockHoles(false), S)),
            Sub("Products",
                Item("Edit available resources…", Run(EditResources), P),
                Item("Import catalogue from project…", Run(ImportCatalogue), P),
                Item("Export catalogue…", Run(ExportCatalogue), P)),
            Sub("Rows",
                Item("Attach holes to row…", Run(AttachHolesToRow), S),
                Item("Create row from holes", CreateRowFromHoles, S),
                Item("Detach holes from rows", DetachHolesFromRows, S),
                Item("Create rows automatically", AutoRows, P),
                Item("Optimize row spacing", OptimizeRows, P)),
            Sub("General functions",
                Item("Clip polylines to be inside polygon", Run(ClipPolylinesToPolygon), S),
                Item("Create centre of mass for polygons", Run(CentreOfMass), P),
                Item("Expand polygon…", Run(ExpandPolygon), S),
                Item("Move/copy entities…", Run(MoveCopyEntities), S),
                Item("Transform coordinates…", TransformCoordinates, S),
                Item("Mark clearance zone…", Run(MarkClearanceZone), P),
                Item("3D polygon area", Run(PolygonArea3D), S)),
            Sub("Surfaces",
                Item("Create surface", Run(() => CreateSurface(false)), S),
                Item("Create surface (honour breaklines)", Run(() => CreateSurface(true)), S),
                Item("Create a point set from surface…", Run(SurfacePointSet), P),
                Item("Create contours…", Run(SurfaceContours), P),
                Item("Create segment points", Run(SurfaceSegmentPoints), S),
                Item("Expand surface…", Run(SurfaceExpand), P),
                Item("Simplify surface…", Run(() => SurfaceSimplify(false)), P),
                Item("Simplify surface with smoothing…", Run(() => SurfaceSimplify(true)), P),
                Item("Decimate surface…", Run(SurfaceDecimate), P),
                Item("Move objects onto surface", Run(MoveObjectsOntoSurface), S),
                Item("Clip surface to polygon", Run(SurfaceClip), S),
                Item("Create intersection line and surface", Run(IntersectLineSurface), S),
                Item("Create intersection grade peg and surface", Run(IntersectGradePegs), S),
                Item("Create intersection of two surfaces", Run(IntersectSurfaces), S),
                Item("Create bench layout polyline…", Run(BenchLayoutPolyline), P),
                Item("Project line to bench surface", Run(ProjectLineToSurface), S),
                Item("Improve surface geometry", Run(SurfaceImprove), P),
                Item("Batter (toe from crest)…", Run(BatterTool), S),
                Item("Volume…", Run(VolumeTool), P),
                Item("Recognize bench elements…", Run(RecognizeBenchElements), P)),
            Sub("Ties",
                Item("Show palette", ShowTiePalette, P),
                Item("Automatic tie-up…", AutoTieUp, P),
                Item("Connect selected holes in order", ConnectSelectedInOrder, S),
                Item("Flip selected ties", FlipTies, S),
                Item("Delete tie up", Run(DeleteTieUp), P)),
            Item("Survey stations…", () => AnalysisWindows.SurveyStations(this), P),
            Item("Laser wizard…", Run(LaserWizard), P),
            Item("Report designer…", Run(PrintReports), P),
            Item("Vibration analysis…", () => AnalysisWindows.VibrationAnalysis(this), P)),
        Sub("Help",
            Item("Contents", () => AnalysisWindows.Help(this), null, "F1"),
            Item("Keyboard and mouse", () => AnalysisWindows.KeyboardHelp(this)),
            Item("What's new", () => WhatsNewWindow.Show()),
            Item("About", () => AnalysisWindows.About(this)),
            Sub("Log file",
                Check("Log extended debug information", () => IO.Log.Extended, () => IO.Log.Extended = !IO.Log.Extended),
                Item("Open log file", () => OpenExternal(IO.Log.FilePath)),
                Item("Find log file", () => OpenExternal(IO.ProgramSettings.LogFolder))),
            Item("Find missing objects", FindMissingObjects, P),
            Item("Restore default UI layout", Window.RestoreLayout))
    };

    private IEnumerable<MenuEntry> HoleTypeMenu()
    {
        if (Project == null) yield break;
        yield return Check("Dummy holes", () => !Project.View.HiddenHoleTypes.Contains(0), () => ToggleHoleType(0));
        foreach (var t in Project.Design.HoleTypes)
        {
            var idx = t.Index;
            yield return Check(t.Name, () => !Project.View.HiddenHoleTypes.Contains(idx), () => ToggleHoleType(idx));
        }
        yield return Item("All", () => ToggleView(v => v.HiddenHoleTypes.Clear()));
    }

    private IEnumerable<MenuEntry> MaterialMenu()
    {
        if (Project == null) yield break;
        foreach (var t in Project.Design.MaterialTypes)
        {
            var idx = t.Index;
            yield return Check(t.Name, () => !Project.View.HiddenMaterialTypes.Contains(idx), () => ToggleMaterialType(idx));
        }
        yield return Item("All", () => ToggleView(v => v.HiddenMaterialTypes.Clear()));
    }

    /// <summary>Toolbar groups: (group name, buttons). Each button is (glyph, tooltip, action, isTool).</summary>
    public List<(string Group, List<(string Glyph, string Tip, Action Action, string? ToolName)> Buttons)> BuildToolbars() => new()
    {
        ("Tools", new()
        {
            ("🗋", "New", Run(New), null), ("📂", "Open", Run(Open), null), ("💾", "Save", Run(async () => await Save()), null),
            ("🖶", "Reports", Run(PrintReports), null), ("🗺", "Plan", Run(PrintPlan), null),
            ("✖", "Delete", DeleteSelected, null), ("↶", "Undo", Undo, null), ("↷", "Redo", Redo, null),
            ("✂", "Cut", Cut, null), ("⧉", "Copy", Copy, null), ("📋", "Paste", Paste, null)
        }),
        ("View tools", new()
        {
            ("➚", "Select", SelectTool, "Select"), ("🔍", "Zoom", () => SetTool(new ZoomBoxTool(this)), "Zoom"),
            ("＋", "Zoom in", ZoomIn, null), ("－", "Zoom out", ZoomOut, null), ("⟲", "Undo zoom", UndoZoom, null),
            ("⟳", "Rotate views", () => SetTool(new RotateTool(this, false)), "Rotate"), ("✋", "Pan", () => SetTool(new PanTool(this)), "Pan"),
            ("⛶", "Zoom all", ZoomAll, null), ("N", "North", () => SetTool(new RotateTool(this, true)), "North"),
            ("▦", "Plan view", PlanView, null), ("◩", "3D view", View3D, null),
            ("☰", "Layer manager", Window.ToggleLayers, null), ("🔒", "Lock holes", () => LockHoles(true), null), ("🔓", "Unlock holes", () => LockHoles(false), null),
            ("⌖", "Snap to objects", ToggleSnap, null)
        }),
        ("Survey tools", new()
        {
            ("⌁", "Laser wizard", Run(LaserWizard), null), ("✚", "Survey point", () => DrawPointTool(PointKind.Survey), "Survey point"),
            ("◭", "Surface", SurfaceTool, "Surface"), ("▽", "Grade peg", () => DrawPointTool(PointKind.GradePeg), "Grade peg"),
            ("⊠", "Monitoring point", () => DrawPointTool(PointKind.Monitoring), "Monitoring point"), ("⤓", "Bore track", BoreTrackTool, "Bore track")
        }),
        ("Blast tools", new()
        {
            ("⎯", "Baseline", () => DrawPolylineTool(false, LineKind.Baseline), "Polyline"), ("●", "Hole", () => HoleTool(), "Hole"), ("⋯", "Row", RowTool, "Row"),
            ("⁘", "Pattern", PatternTool, "Pattern"), ("▣", "Fill polygon", Run(FillSelectedPolygon), null), ("⬚", "Boundary", () => DrawPolylineTool(true, LineKind.Boundary), "Polygon"),
            ("≡", "Optimize rows", OptimizeRows, null), ("📐", "Hole measure", HoleMeasureTool, "Hole measure"), ("V", "Volume", Run(VolumeTool), null),
            ("#", "Hole label", HoleLabelTool, "Hole label"), ("◢", "Batter", Run(BatterTool), null), ("⊟", "Cross-section", () => AnalysisWindows.SectionTool(this), null)
        }),
        ("Loading tools", new()
        {
            ("★", "Lead-in", () => InitiationPointTool(false), "Lead-in"), ("↗", "Tie", () => SetTool(new TieTool(this)), "Tie"),
            ("○", "Dummy hole", () => HoleTool(true), "Dummy hole"), ("⏱", "In-hole delay", InholeDelayTool, "In-hole delay"),
            ("⚙", "Loading rules", Run(EditLoadingRules), null), ("▮", "Loading", LoadingTool, "Loading"), ("⚡", "Apply loading rules", Run(ApplyLoadingRules), null)
        }),
        ("Electronic tools", new()
        {
            ("⏲", "Set hole time", SetTimeTool, "Set"), ("⏩", "Row set", RowSetTool, "Row set"), ("⋰", "Timing line", TimingLineTool, "Timing line"),
            ("±", "Add/subtract time", Run(AddSubtractTime), null), ("✦", "Opening point", () => InitiationPointTool(true), "Opening point")
        }),
        ("Drawing tools", new()
        {
            ("·", "Point", () => DrawPointTool(PointKind.General), "Point"), ("T", "Text", DrawTextTool, "Text"), ("➝", "Arrow", DrawArrowTool, "Arrow"),
            ("〰", "Polyline", () => DrawPolylineTool(false), "Polyline"), ("⬠", "Polygon", () => DrawPolylineTool(true), "Polygon"),
            ("📏", "Measure", MeasureTool, "Measure"), ("↔", "Dimension", DrawDimensionTool, "Dimension"), ("◯", "Circle", DrawCircleTool, "Circle"),
            ("⇧", "North point", () => DrawPointTool(PointKind.NorthPoint), "North point")
        })
    };

    public void ToggleSnap()
    {
        Settings.SnapToObjects = !Settings.SnapToObjects;
        View.SnapEnabled = Settings.SnapToObjects;
        SetStatus(Settings.SnapToObjects ? Loc.T("Snap to objects on") : Loc.T("Snap to objects off"));
    }

    public async void FindMissingObjects()
    {
        var problems = new List<string>();
        var ids = Project!.Entities.Select(e => e.Id).ToHashSet();
        var orphanTies = Project.Ties.Count(t => !ids.Contains(t.FromId) || !ids.Contains(t.ToId));
        if (orphanTies > 0) problems.Add(Loc.F("{0} ties point at deleted holes", orphanTies));
        var noLayer = Project.Entities.Count(e => Project.FindLayer(e.LayerId) == null);
        if (noLayer > 0) problems.Add(Loc.F("{0} objects have no layer", noLayer));
        var hiddenLayers = Project.Layers.Where(l => !l.Visible && Project.Entities.Any(e => e.LayerId == l.Id)).Select(l => l.Name).ToList();
        if (hiddenLayers.Count > 0) problems.Add(Loc.F("Hidden layers with objects: {0}", string.Join(", ", hiddenLayers)));
        var far = Project.Entities.Where(e => e.GetVertices().Any(v => double.IsNaN(v.X) || Math.Abs(v.X) > 1e8 || Math.Abs(v.Y) > 1e8)).ToList();
        if (far.Count > 0) problems.Add(Loc.F("{0} objects have invalid coordinates", far.Count));
        if (problems.Count == 0) { await Dialogs.Info(Loc.T("No missing objects found.")); return; }
        if (await Dialogs.Confirm(string.Join("\n", problems) + "\n\n" + Loc.T("Repair (remove broken ties, assign default layer, show hidden layers)?")))
            Change("Find missing objects", p =>
            {
                var all = p.Entities.Select(e => e.Id).ToHashSet();
                p.Entities.RemoveAll(e => e is TieEntity t && (!all.Contains(t.FromId) || !all.Contains(t.ToId)));
                foreach (var e in p.Entities.Where(e => p.FindLayer(e.LayerId) == null)) e.LayerId = p.DefaultLayer.Id;
                foreach (var l in p.Layers) l.Visible = true;
                p.Entities.RemoveAll(e => far.Any(f => f.Id == e.Id));
            });
    }

    public async Task ImportCatalogue()
    {
        var path = await Dialogs.OpenFile("Import catalogue from project", ("Blast Designer project", new[] { "*" + IO.ProjectFile.Extension, "*" + IO.ProjectFile.TemplateExtension }));
        if (path == null) return;
        var other = IO.ProjectFile.Load(path);
        int added = 0;
        Change("Import catalogue", p =>
        {
            foreach (var pr in other.Products.Where(pr => !p.Products.Any(x => x.Name == pr.Name && x.Kind == pr.Kind)))
            {
                p.Products.Add(pr);
                added++;
            }
        });
        SetStatus(Loc.F("{0} products added", added));
    }

    public async Task ExportCatalogue()
    {
        var path = await Dialogs.SaveFile("Export catalogue", "catalogue" + IO.ProjectFile.TemplateExtension, IO.ProjectFile.TemplateExtension, ("Template", new[] { "*" + IO.ProjectFile.TemplateExtension }));
        if (path == null) return;
        var t = BlastProject.CreateNew("Catalogue");
        t.Products = Project!.Products.Select(p => p.Clone()).ToList();
        t.LoadingRules = Domain.ProjectJson.DeepClone(Project.LoadingRules);
        IO.ProjectFile.Save(t, path, 0);
    }
}
