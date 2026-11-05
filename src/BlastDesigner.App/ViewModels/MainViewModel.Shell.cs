using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.App.Views;
using BlastDesigner.Domain.Model;
using static BlastDesigner.App.Tools.MenuEntry;

namespace BlastDesigner.App.ViewModels;

/// <summary>One command on the ribbon: a big or small button, optionally a dropdown, optionally locked (not available in this program).</summary>
public sealed record RibbonItem(string Label, string Icon, Action? Run, Func<bool>? Enabled = null, string? Key = null, bool Big = false,
    Func<IEnumerable<MenuEntry>>? Drop = null, string? Lock = null, string? ToolName = null);

public sealed record RibbonGroup(string Name, List<RibbonItem> Items);

public sealed record Stage(string Name, List<RibbonGroup> Groups);

/// <summary>Quick hole filter shown under the plan: a count and a way to select those holes.</summary>
public sealed record QuickFilter(string Label, string Key, Func<BlastProject, IEnumerable<Hole>> Holes);

/// <summary>
/// The work-stage shell (ribbon by stage, view rail, object context menus, single-key shortcuts).
/// Ribbon buttons reuse the commands of the classic menu by path, so both always do the same thing.
/// </summary>
public sealed partial class MainViewModel
{
    private Dictionary<string, MenuEntry>? _commandIndex;
    private Dictionary<string, (Action Action, string? Tool)>? _toolbarIndex;

    /// <summary>Ribbon bindings that did not resolve to a command (checked by the UI smoke test).</summary>
    public List<string> MissingCommands { get; } = new();

    /// <summary>Every menu command by its English path ("File/Import/Text file…").</summary>
    public Dictionary<string, MenuEntry> CommandIndex
    {
        get
        {
            if (_commandIndex != null) return _commandIndex;
            _commandIndex = new Dictionary<string, MenuEntry>();
            void Walk(IEnumerable<MenuEntry> entries, string prefix)
            {
                foreach (var e in entries)
                {
                    if (e.IsSeparator) continue;
                    var path = prefix.Length == 0 ? e.Header : prefix + "/" + e.Header;
                    _commandIndex.TryAdd(path, e);
                    if (e.Children != null) Walk(e.Children, path);
                }
            }
            Walk(BuildMenu(), "");
            return _commandIndex;
        }
    }

    private Dictionary<string, (Action Action, string? Tool)> ToolbarIndex =>
        _toolbarIndex ??= BuildToolbars().SelectMany(g => g.Buttons).GroupBy(b => b.Tip).ToDictionary(g => g.Key, g => (g.First().Action, g.First().ToolName));

    private RibbonItem M(string label, string icon, string path, string? key = null, bool big = false)
    {
        if (!CommandIndex.TryGetValue(path, out var e)) { MissingCommands.Add(path); return new RibbonItem(label, icon, null, () => false, key, big); }
        if (e.Children != null || e.Dynamic != null)
            return new RibbonItem(label, icon, null, e.IsEnabled, key, big, () => e.Dynamic?.Invoke() ?? e.Children!);
        return new RibbonItem(label, icon, e.Action, e.IsEnabled, key, big);
    }

    private RibbonItem Tb(string label, string icon, string tip, string? key = null, bool big = false)
    {
        if (!ToolbarIndex.TryGetValue(tip, out var t)) { MissingCommands.Add("toolbar:" + tip); return new RibbonItem(label, icon, null, () => false, key, big); }
        return new RibbonItem(label, icon, t.Action, () => HasProject, key, big, ToolName: t.Tool);
    }

    private static RibbonItem A(string label, string icon, Action run, string? key = null, bool big = false, Func<bool>? enabled = null) =>
        new(label, icon, run, enabled, key, big);

    private static RibbonItem Locked(string label, string icon, bool big = false) =>
        new(label, icon, null, () => false, null, big, Lock: "Not in Blast Designer");

    private static RibbonGroup G(string name, params RibbonItem[] items) => new(name, items.ToList());


    /// <summary>The seven work stages and their ribbon groups.</summary>
    public List<Stage> BuildStages() => new()
    {
        new Stage("File", new()
        {
            G("Document", M("New", "new", "File/New", "Ctrl N", true), M("Open", "open", "File/Open…", "Ctrl O", true), M("Save", "save", "File/Save", "Ctrl S", true),
                M("Recent", "recent", "File/Recent files"), M("Save as…", "save", "File/Save as…"), M("Close", "close", "File/Close")),
            G("Templates", M("From template", "template", "File/New", big: true), M("Save as template", "template", "File/Save as template…"), M("Manage templates", "template", "Edit/Templates…")),
            G("Data exchange", M("Import", "import", "File/Import", big: true), M("Export", "export", "File/Export", big: true),
                M("Merge with file…", "merge", "File/Merge…"), M("Copy image", "image", "Edit/Copy image")),
            G("Print", M("Print plan", "print", "File/Print/Plan…", "Ctrl P", true), M("Reports", "report", "File/Print/Reports…"), M("Report designer", "edit", "Tools/Report designer…")),
            G("Settings", M("Program settings", "gear", "Edit/Program settings…", big: true), M("Blast properties", "edit", "Edit/Blast properties…"), M("Design data", "edit", "Edit/Design data…"),
                M("Site coordinate system", "north", "Edit/Site coordinate system…"), M("Visual style", "eye", "Edit/Visual style…"))
        }),
        new Stage("Topography", new()
        {
            G("Survey", M("Laser wizard", "laser", "Tools/Laser wizard…", big: true), Tb("Survey points", "point", "Survey point", big: true),
                M("Survey stations", "station", "Tools/Survey stations…"), Tb("Bore track", "bore", "Bore track"), Tb("Grade pegs", "peg", "Grade peg"),
                Tb("Monitoring points", "wave", "Monitoring point"), M("MWD data", "chart", "File/Import/MWD data, single file…"), M("Point cloud (LAS)", "surface", "File/Import/LIDAR files (las)…")),
            G("Surfaces", M("Build", "surface", "Tools/Surfaces", big: true), Tb("Surface tool", "surface", "Surface"), M("Project objects", "slice", "Tools/Surfaces/Move objects onto surface"),
                M("Minimum", "surface", "Tools/Surfaces/Find minimum surface"), M("Maximum", "surface", "Tools/Surfaces/Find maximum surface"),
                M("Intersection of surfaces", "section", "Tools/Surfaces/Create intersection of multiple surfaces"), M("Clip to polygon", "polygon", "Tools/Surfaces/Clip surface to polygon"),
                M("Contours", "iso", "Tools/Surfaces/Create contours…"), M("Volume", "cube", "Tools/Surfaces/Volume…"),
                M("Recognize (AI)", "wand", "Tools/Surfaces/Recognize bench elements…")),
            G("Lines and polygons", M("Convert", "convert", "Tools/Convert"), M("Move / copy", "pan", "Tools/General functions/Move/copy entities…"),
                M("Transform", "orbit", "Tools/General functions/Transform coordinates…"), M("Expand polygon", "polygon", "Tools/General functions/Expand polygon…"),
                M("Clip lines", "edit", "Tools/General functions/Clip polylines to be inside polygon"), M("Offset", "polyline", "Tools/General functions/Polyline offset…"),
                M("Midpoints", "point", "Tools/General functions/Create midpoints of selected segments"), M("Centre of mass", "point", "Tools/General functions/Create centre of mass for polygons"),
                M("Extend to intersection", "polyline", "Tools/General functions/Extend line to intersection"), M("Clearance zone", "exclusion", "Tools/General functions/Mark clearance zone…"))
        }),
        new Stage("Layout", new()
        {
            G("Layout", Tb("Hole", "hole", "Hole", "D", true), Tb("Row", "row", "Row", "R", true), Tb("Pattern", "pattern", "Pattern", "P", true),
                Tb("Baseline", "baseline", "Baseline"), M("Fill polygon", "fill", "Tools/Holes/Fill selected polygon…"), M("Curved layout", "curve", "Tools/Holes/Curved layout…")),
            G("From objects", M("At points", "point", "Tools/Holes/Add at points"), M("Along line", "polyline", "Tools/Holes/Add along selected polyline…"),
                M("Between lines", "row", "Tools/Holes/Drill holes between lines…"), M("From polylines", "polyline", "Tools/Holes/Create holes from selected polylines"),
                M("To survey points", "point", "Tools/Holes/Move to selected points…"), M("Delete outside polygon", "polygon", "Tools/Holes/Delete holes outside polygon")),
            G("Rows", M("Optimize", "optimize", "Tools/Rows/Optimize row spacing", big: true), M("Rows automatically", "pack", "Tools/Rows/Create rows automatically"),
                M("Row from holes", "row", "Tools/Rows/Create row from holes"), M("Detach from row", "row", "Tools/Rows/Detach holes from rows")),
            G("Face", M("Front row profile", "profile", "View/Front row burden profile"), M("Batter", "batter", "Tools/Surfaces/Batter (toe from crest)…"), Tb("Section", "section", "Cross-section", "X")),
            G("Numbering", M("Renumber", "label", "Tools/Holes/Renumber…", big: true), M("Duplicate labels", "label", "Tools/Holes/Re-label duplicate holes"),
                M("Duplicate coordinates", "dup", "Tools/Holes/Check for duplicates by coordinates…"), M("Holes → dummy", "dummy", "Tools/Convert/Selected holes to dummy holes"),
                M("Dummy → holes", "hole", "Tools/Convert/Selected dummy holes to holes")),
            G("Markout and tracks", M("Markout", "markout", "View/Markout"), Tb("Distance at depth", "measure", "Hole measure"), M("Reset design tracks", "bore", "Tools/Holes/Reset design tracks"),
                M("Collars onto surface", "slice", "Tools/Holes/Move collars onto surface…"), M("Lengths to grade", "slice", "Tools/Holes/Set lengths to grade surface…")),
            G("Data", M("Table", "table", "View/Table view", "Ctrl T", true))
        }),
        new Stage("Loading", new()
        {
            G("Loading", Tb("Load", "charge", "Loading", "L", true), M("By rules", "auto", "Tools/Auto loading/Apply", big: true),
                M("Initiation only", "det", "Tools/Holes/Apply initiation rules only"), M("Empty", "empty", "Tools/Holes/Empty"), M("Release for loading", "check", "Tools/Holes/Release for loading")),
            G("Rules and products", M("Loading rules", "rules", "Tools/Auto loading/Edit loading rules…", big: true), M("Initiation rules", "rules", "Edit/Initiation rules…"),
                M("Products", "charge", "Edit/Available resources…"), M("Hole and material types", "edit", "Edit/Hole and material types…")),
            G("Charge column", M("Edit column", "charge", "Tools/Holes/Edit loading…"), Tb("In-hole detonators", "det", "In-hole delay"),
                M("Join decks", "merge", "Tools/Holes/Join like decks"), M("Loading factor", "charge", "Tools/Holes/Set loading factor…")),
            G("Strata", M("Strata intercepts", "slice", "Calculations/Strata intercepts"), M("Material from strata", "slice", "Tools/Holes/Set material type from strata")),
            G("As-built", M("Clear actuals", "empty", "Tools/Holes/Clear actuals…"), M("Mark drilled", "check", "Tools/Holes/Mark drilled"),
                M("Design tolerances", "check", "Edit/Design tolerances…"), M("Tolerance check", "check", "Calculations/Design tolerance check")),
            G("Result", M("Quantities", "chart", "Calculations/Quantities", "Ctrl⇧Q", true))
        }),
        new Stage("Initiation", new()
        {
            G("Tie-up", Tb("Ties", "tie", "Tie", "T", true), Tb("Initiation point", "star", "Lead-in", "I", true),
                Tb("Dummy hole", "dummy", "Dummy hole"), M("Initiation catalogue", "palette", "Tools/Ties/Show palette"), M("Automatic tie-up", "auto", "Tools/Ties/Automatic tie-up…"),
                M("Connect in order", "tie", "Tools/Ties/Connect selected holes in order"), M("Flip ties", "flip", "Tools/Ties/Flip selected ties"), M("Delete ties", "close", "Tools/Ties/Delete tie up")),
            G("Electronic", Tb("Time by click", "clock", "Set hole time", "E", true), Tb("Along row", "row", "Row set", big: true),
                Tb("Timing line", "iso", "Timing line"), Tb("Opening point", "point", "Opening point"), Locked("V pattern", "vee"), Locked("Encoding sequence", "ebs"), Locked("i-kon loggers", "ebs")),
            G("Edit times", M("Shift time…", "clock", "Tools/Electronic timing/Add/Subtract time from holes…"), M("Scale intervals…", "optimize", "Tools/Electronic timing/Adjust intervals…"),
                M("Constant step…", "optimize", "Tools/Electronic timing/Adjust increments of selected holes…"), M("Between horizons", "dup", "Tools/Electronic timing/Copy times between horizons…"),
                M("Re-apply", "auto", "Tools/Electronic timing/Re-apply timing"), M("Deprogram", "empty", "Tools/Electronic timing/Deprogram holes")),
            G("Conversion", M("Pyrotechnic → electronic", "convert", "Tools/Electronic timing/Convert pyrotechnic devices to electronic"),
                M("Clear exceptions", "close", "Tools/Electronic timing/Clear exceptions"), M("Electronic settings", "gear", "Tools/Electronic timing/Preferences…")),
            G("Check", M("Isochrones", "iso", "Calculations/Visualize timing", "Ctrl⇧V", true), M("Timing overlap", "chart", "Calculations/Timing overlap"),
                M("Logging report", "report", "Calculations/Electronic logging report"))
        }),
        new Stage("Analysis", new()
        {
            G("Blast dynamics", M("Visualization", "play", "Calculations/Visualize timing", "Ctrl⇧V", true), M("Angle of initiation", "arrow", "Calculations/Angle of initiation"),
                M("First movement", "arrow", "Calculations/First movement"), M("Burden relief", "chart", "Calculations/Burden relief"), M("Time envelope", "chart", "Calculations/Time envelope"),
                M("Charge vs time", "chart", "Calculations/Time envelope overview"), M("Clear analysis", "close", "View/Clear analysis display", "Esc")),
            G("Quality control", M("Bullseye", "exclusion", "View/Single hole view (bullseye)"), M("Exceptions", "eye", "View/Exceptions view"), M("Front row", "row", "View/Front row view"),
                M("Row check", "row", "View/Row check view"), M("Burden check", "measure", "View/Burden check view"), M("Horizontal slice", "slice", "View/Horizontal slice"),
                M("Charge in 3D", "charge", "View/Loading"), M("Tolerances", "check", "Calculations/Design tolerance check"), M("Fly through", "send", "View/Fly through")),
            G("Volumes", M("Quantities", "chart", "Calculations/Quantities", "Ctrl⇧Q", true), M("Dashboard", "dash", "View/Dashboard", big: true),
                M("Volume between surfaces", "cube", "Tools/Surfaces/Volume…"), M("Volume overview", "polygon", "Calculations/Volume overview"), M("Costs", "money", "Calculations/Project costs")),
            G("Environment", M("Exclusion zone", "exclusion", "Calculations/Environmental prediction/Exclusion zone calculator", big: true),
                M("Vibration", "wave", "Calculations/Vibration/Vibration"), M("Vibration map", "wave", "Calculations/Vibration/Vibration map"), M("Airblast map", "air", "Calculations/Vibration/Airblast map"),
                M("Flyrock", "frag", "Calculations/Environmental prediction/Flyrock prediction"), M("Scale law", "wave", "Calculations/Environmental prediction/Scale law calculator"),
                M("Vibration records", "wave", "Tools/Vibration analysis…")),
            G("Fragmentation", M("Prediction", "frag", "Calculations/Environmental prediction/Fragmentation prediction", big: true), M("Inputs", "edit", "Edit/Fragmentation inputs…"))
        }),
        new Stage("Output", new()
        {
            G("Reports", M("Reports", "report", "File/Print/Reports…", big: true), M("Print plan", "print", "File/Print/Plan…", "Ctrl P", true),
                M("Report designer", "edit", "Tools/Report designer…"), M("Logging report", "ebs", "Calculations/Electronic logging report"), Locked("EBS field report", "ebs")),
            G("To the drill rig", M("IREDES", "send", "File/Export/IREDES Plan file…", "Ctrl⇧X", true), M("Drill plan (XML)", "send", "File/Export/Drill plan (XML)…"),
                M("Surpac STR", "export", "File/Export/String file (.str)…"), Locked("Leica", "send"), Locked("MineSight 3D", "export")),
            G("To loading", M("Load sheets (CSV)", "table", "File/Export/Load sheets (csv)…"), M("Deck table (CSV)", "table", "File/Export/Deck table (csv)…"), Locked("ELS for MMU", "table")),
            G("Vibration monitoring", M("Firing data", "wave", "File/Export/Firing data…"), M("With cosines", "wave", "File/Export/Firing data with cosines…"),
                M("Extended firing data", "wave", "File/Export/Extended firing data with cosines…")),
            G("Other formats", M("Export", "export", "File/Export", big: true), M("KML / KMZ", "export", "File/Export/KMZ file…"), M("DXF", "export", "File/Export/DXF…")),
            G("BlastIQ cloud", Locked("Send plan", "cloud"), Locked("Load plan", "cloud"), Locked("Blast management", "cloud"))
        })
    };

    /// <summary>View rail: (icon, tooltip, key, separator before, action, tool name for the checked state).</summary>
    public List<(string Icon, string Tip, string Key, bool Sep, Action Action, string? Tool)> BuildRail() => new()
    {
        ("select", "Select", "V", false, SelectTool, "Select"),
        ("pan", "Pan (or middle button)", "H", false, () => SetTool(new PanTool(this)), "Pan"),
        ("zoombox", "Zoom to rectangle", "Z", false, () => SetTool(new ZoomBoxTool(this)), "Zoom"),
        ("fit", "View everything", "Home", false, ZoomAll, null),
        ("prev", "Previous view", "⇧Z", false, UndoZoom, null),
        ("orbit", "Rotate 3D", "O", true, () => SetTool(new RotateTool(this, false)), "Rotate"),
        ("north", "North up / rotate plan", "N", false, () => SetTool(new RotateTool(this, true)), "North"),
        ("measure", "Measure", "M", true, MeasureTool, "Measure"),
        ("polyline", "Drawing: polyline (others in the list)", "W", false, RunDrawingTool, "Polyline"),
        ("snap", "Snap to objects", "S", true, ToggleSnap, null),
        ("gridi", "Coordinate grid", "G", false, () => ToggleView(v => v.ShowGrid = !v.ShowGrid), null),
        ("layers", "Layers", "F7", false, () => Window.ShowInspectorTab(1), null)
    };

    /// <summary>Drawing tools of the rail's drawing button: header, icon, tool name (for the highlight) and how to start it.</summary>
    public (string Header, string Icon, string Tool, Action Start)[] DrawingToolList => new (string, string, string, Action)[]
    {
        ("Point", "point", "Point", () => DrawPointTool(PointKind.General)),
        ("Polyline", "polyline", "Polyline", () => DrawPolylineTool(false)),
        ("Polygon", "polygon", "Polygon", () => DrawPolylineTool(true)),
        ("Circle", "circle", "Circle", DrawCircleTool),
        ("Arrow", "arrow", "Arrow", DrawArrowTool),
        ("Text", "text", "Text", DrawTextTool),
        ("Dimension", "dimension", "Dimension", DrawDimensionTool),
        ("North point", "northmark", "North point", () => DrawPointTool(PointKind.NorthPoint))
    };

    /// <summary>The drawing tool the rail button shows and starts (the last one picked from its list).</summary>
    public string DrawingTool { get; private set; } = "Polyline";

    public (string Header, string Icon, string Tool, Action Start) CurrentDrawingTool =>
        DrawingToolList.FirstOrDefault(t => t.Header == DrawingTool) is { Header: not null } t ? t : DrawingToolList.First(x => x.Header == "Polyline");

    /// <summary>The rail's drawing button and key W: the current drawing tool.</summary>
    public void RunDrawingTool() => CurrentDrawingTool.Start();

    /// <summary>Drawing tools offered by the rail's drawing button; picking one makes it the button's tool.</summary>
    public IEnumerable<MenuEntry> DrawingTools() => DrawingToolList.Select(t =>
        Item(t.Header, () => { DrawingTool = t.Header; t.Start(); }).WithIcon(t.Icon));

    public List<QuickFilter> QuickFilters() => new()
    {
        new("Empty", "Ctrl+E", p => p.Holes.Where(h => !h.IsDummy && h.Decks.Count == 0)),
        new("Dipped", "Ctrl+L", p => p.Holes.Where(h => h.ActualLength.HasValue)),
        new("Misfires", "Ctrl+M", p => p.Holes.Where(h => h.Exceptions.HasFlag(HoleException.Misfire))),
        new("Not programmed", "Ctrl+U", p => p.Holes.Where(h => !h.IsDummy && !h.ElectronicTimeMs.HasValue))
    };

    public void SelectHoles(IEnumerable<Hole> holes)
    {
        var ids = holes.Select(h => h.Id).ToList();
        Doc?.Select(ids);
        if (ids.Count > 0) ZoomSelection();
        SetStatus(Loc.F("{0} holes selected", ids.Count));
    }

    /// <summary>Shows only the selected objects (Esc in the filter bar or "Show all" brings the rest back).</summary>
    public void ShowOnlySelected()
    {
        if (Doc == null) return;
        HiddenEntities.Clear();
        foreach (var e in Project!.Entities.Where(e => !Doc.Selection.Contains(e.Id))) HiddenEntities.Add(e.Id);
        Doc.NotifyViewChanged();
    }

    public void ShowAllEntities()
    {
        HiddenEntities.Clear();
        Doc?.NotifyViewChanged();
    }

    /// <summary>Feeds a typed coordinate to the active tool as if the user clicked there.</summary>
    public async Task EnterCoordinates()
    {
        if (Doc == null || ActiveTool == null || ActiveTool is SelectTool) return;
        var text = await Dialogs.Prompt("Enter coordinates", Loc.T("X Y [Z] (separated by spaces or semicolons)"), "");
        if (string.IsNullOrWhiteSpace(text)) return;
        var parts = text.Split(new[] { ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToList();
        if (parts.Count < 2 || parts.Take(Math.Min(3, parts.Count)).Any(double.IsNaN)) { await Dialogs.Info(Loc.T("Enter two or three numbers.")); return; }
        var z = parts.Count > 2 ? parts[2] : (Project!.Holes.Any() ? Project.Holes.Average(h => h.Collar.Z) : View.Camera.Target.Z);
        var w = new Domain.Geometry.Vec3(parts[0], parts[1], z);
        var (sx, sy) = View.Camera.ToScreen(w);
        ActiveTool.PointerPressed(new Controls.ToolPointer { X = sx, Y = sy, World = w, Snapped = w, Left = true, ClickCount = 1 });
        View.Redraw();
    }

    /// <summary>Context menu for the plan: depends on what is selected, not on the active tool.</summary>
    public IEnumerable<MenuEntry> ObjectContextMenu()
    {
        var sel = Doc?.SelectedEntities.ToList() ?? new();
        bool W() => Doc?.IsReadOnly == false;
        var edit = new[]
        {
            Separator,
            Item("Cut", Cut, W, "Ctrl+X"), Item("Copy", Copy, null, "Ctrl+C"), Item("Delete", DeleteSelected, W, "Delete"),
            Item("Group", GroupSelected, W, "Ctrl+G"), Item("Ungroup", UngroupSelected, W), Item("Hide layers", HideSelectedLayers)
        };
        var props = Item("Properties…", () => Window.ShowInspectorTab(0), null, "Enter");
        if (sel.Count == 0)
            return new[]
            {
                Item("Paste", Paste, W, "Ctrl+V"), Item("Select all", SelectAll, null, "Ctrl+A"), Separator,
                Item("View everything", ZoomAll, null, "Home"), Item("Previous view", UndoZoom), Item("Set blast orientation", SetBlastOrientation), Item("Reset orientation", BlastView),
                Separator,
                Check("Grid", () => Project?.View.ShowGrid == true, () => ToggleView(v => v.ShowGrid = !v.ShowGrid)),
                Check("Ties", () => Project?.View.ShowTies == true, () => ToggleView(v => v.ShowTies = !v.ShowTies)),
                Item("Copy image", CopyImage), Item("Show all objects", ShowAllEntities, () => HiddenEntities.Count > 0)
            };
        var kinds = sel.Select(KindOf).Distinct().ToList();
        if (kinds.Count > 1)
            return new[] { props, Item("Select like", SelectLike), Item("Invert selection", InvertSelection), Item("Show only selected", ShowOnlySelected) }.Concat(edit);
        return kinds[0] switch
        {
            "holes" => new[]
            {
                props, Item("Select like", SelectLike), Item("Select exact", SelectExact), Item("Invert selection", InvertSelection), Separator,
                Item("Apply loading rules", Run(ApplyLoadingRules), W), Item("Add/Subtract time from holes…", Run(AddSubtractTime), W), Item("Selected holes to dummy holes", () => ConvertDummy(true), W), Separator,
                Sub("Loading", Item("Edit loading…", Run(() => EditHoleLoading()), W), Item("Empty", Run(EmptyHoles), W), Item("Join like decks", Run(JoinLikeDecks), W),
                    Item("Set loading factor…", Run(SetLoadingFactor), W), Item("Apply initiation rules only", Run(ApplyInitiationRules), W), Item("Release for loading", () => SetHoleState(HoleState.Released), W)),
                Sub("Electronic timing", Item("Adjust increments of selected holes…", Run(AdjustIncrements), W), Item("Deprogram holes", Run(() => Deprogram(false)), W),
                    Item("Connect selected holes in order", ConnectSelectedInOrder, W)),
                Sub("Rows and strata", Item("Attach holes to row…", Run(AttachHolesToRow), W), Item("Create row from holes", CreateRowFromHoles, W),
                    Item("Detach holes from rows", DetachHolesFromRows, W), Item("Set material type from strata", Run(MaterialFromStrata), W)),
                Sub("Tracks and points", Item("Create points at collar", () => CreatePointsAt("collar"), W), Item("Create points at grade", () => CreatePointsAt("grade"), W),
                    Item("Create points at toe", () => CreatePointsAt("toe"), W), Item("Create polyline of bore track", CreateBoreTrackPolylines, W), Item("Reset design tracks", Run(ResetDesignTracks), W)),
                Separator, Item("Show only selected", ShowOnlySelected)
            }.Concat(edit),
            "polyline" => new[]
            {
                props, Separator,
                Item("Add along selected polyline…", Run(AddHolesAlongPolyline), W), Item("Section along line", SectionAlongSelection),
                Item("Polyline offset…", Run(PolylineOffset), W), Separator,
                Item("Create holes from selected polylines", Run(HolesFromPolylines), W), Item("Extend line to intersection", Run(ExtendLine), W),
                Item("Create midpoints of selected segments", MidpointsOfSegments, W), Item("Project line to bench surface", Run(ProjectLineToSurface), W),
                Sub("Convert", Item("Selected polylines to polygons", () => SetClosed(true), W), Item("Selected objects to points", () => ConvertToPoints(false), W))
            }.Concat(edit),
            "polygon" => new[]
            {
                props, Separator,
                Item("Fill selected polygon…", Run(FillSelectedPolygon), W), Item("Delete holes outside polygon", Run(DeleteHolesOutsidePolygon), W), Item("Volume…", Run(VolumeTool)), Separator,
                Item("Expand polygon…", Run(ExpandPolygon), W), Item("Clip polylines to be inside polygon", Run(ClipPolylinesToPolygon), W),
                Item("Clip surface to polygon", Run(SurfaceClip), W), Item("Create centre of mass for polygons", Run(CentreOfMass), W), Item("3D polygon area", Run(PolygonArea3D)),
                Sub("Convert", Item("Selected polygons to polylines", () => SetClosed(false), W), Item("Selected objects to points", () => ConvertToPoints(false), W))
            }.Concat(edit),
            "surface" => new[]
            {
                props, Separator,
                Item("Volume…", Run(VolumeTool)), Item("Create intersection line and surface", Run(IntersectLineSurface), W), Item("Create intersection of multiple surfaces", Run(IntersectMultipleSurfaces), W), Separator,
                Sub("Expand", Item("Expand surface…", Run(SurfaceExpand), W), Item("Expand surface to polygon", Run(ExpandSurfaceToPolygon), W)),
                Sub("Simplify", Item("Simplify surface…", Run(() => SurfaceSimplify(false)), W), Item("Simplify surface with smoothing…", Run(() => SurfaceSimplify(true)), W),
                    Item("Decimate surface…", Run(SurfaceDecimate), W), Item("Improve surface geometry", Run(SurfaceImprove), W)),
                Item("Clip surface to polygon", Run(SurfaceClip), W), Item("Create contours…", Run(SurfaceContours), W)
            }.Concat(edit),
            "points" => new[]
            {
                props, Item("Select like", SelectLike), Item("Select exact", SelectExact), Separator,
                Item("Add at points", Run(AddHolesAtPoints), W).WithIcon("point"), Item("Move to selected points…", Run(MoveHolesToPoints), W), Separator,
                Item("Selected points to monitoring points", PointsToMonitoring, W), Item("Create surface", Run(() => CreateSurface(true)), W),
                Sub("Convert", Item("Selected objects to a polyline", () => ConvertToPolyline(false), W), Item("Selected objects to a polygon", () => ConvertToPolyline(true), W))
            }.Concat(edit),
            _ => new[] { props, Item("Select like", SelectLike), Item("Select exact", SelectExact), Item("Transform coordinates..", TransformCoordinates, W) }.Concat(edit)
        };
    }

    private static string KindOf(Entity e) => e switch
    {
        Hole => "holes",
        PolylineEntity { Closed: true } => "polygon",
        PolylineEntity => "polyline",
        SurfaceEntity => "surface",
        PointEntity => "points",
        _ => "other"
    };

    private void SectionAlongSelection()
    {
        var line = Doc?.SelectedEntities.OfType<PolylineEntity>().FirstOrDefault(l => l.Points.Count >= 2);
        if (line != null) AnalysisWindows.ShowSection(this, line.Points[0], line.Points[^1]);
    }
}
