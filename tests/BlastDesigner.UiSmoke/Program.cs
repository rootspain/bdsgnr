using BlastDesigner.App.Tools;
using BlastDesigner.App.Controls;
using Avalonia.LogicalTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using BlastDesigner.App;
using BlastDesigner.App.Views;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;

// Headless smoke test: starts the real main window, builds a small blast through the view model,
// exercises the main features and saves screenshots. Exit code 0 means every step ran without an exception.

var outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "BlastDesignerSmoke");
Directory.CreateDirectory(outDir);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

// this version's notes count as seen: the start must not show them (nor save these settings over the real ones)
var settings = new ProgramSettings { ShowStartupAssistant = false, Language = args.Length > 1 ? args[1] : "ru", LastSeenVersion = Changelog.Current.ToString() };
// usage statistics of the smoke run go to its own folder
UsageLog.Folder = Path.Combine(outDir, "usage");
if (Directory.Exists(UsageLog.Folder)) Directory.Delete(UsageLog.Folder, true);
UsageLog.Start("smoke");
BlastDesigner.App.Services.Loc.Language = settings.Language;
var window = new MainWindow(settings) { Width = 1400, Height = 900 };
window.Show();
Pump();
var vm = window.ViewModel;
int failures = 0;

void Pump()
{
    for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
}

void Step(string name, Action a)
{
    try
    {
        a();
        Pump();
        Console.WriteLine($"OK   {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {name}: {ex}");
    }
}

void Shot(string name)
{
    Pump();
    var frame = window.CaptureRenderedFrame();
    var path = Path.Combine(outDir, name + ".png");
    frame?.Save(path);
    Console.WriteLine($"     screenshot {path}");
}

Step("new project", () => { if (vm.Doc == null) vm.NewSilent(); });
Step("new blast opens on the File tab", () =>
{
    window.Ribbon.Select(2); vm.NewSilent(); Pump();
    if (window.Ribbon.Current != 0) throw new Exception($"ribbon is on stage {window.Ribbon.Current}");
    window.Ribbon.Select(2); // the shots below are taken on the Layout stage
});
var boundary = new[] { new Vec3(1000, 2000, 300), new Vec3(1060, 2000, 300), new Vec3(1070, 2035, 300), new Vec3(1005, 2040, 300) };
Step("bench surface", () => vm.Change("Create surface", p =>
{
    var pts = new List<Vec3>();
    for (int x = 980; x <= 1090; x += 10)
        for (int y = 1980; y <= 2060; y += 10)
            pts.Add(new Vec3(x, y, 300 + 0.03 * (x - 980) + 0.02 * (y - 1980)));
    var s = Surfaces.Triangulate(pts);
    s.Kind = SurfaceKind.Bench;
    s.Name = "Bench";
    p.Add(s, p.GetOrCreateLayer(StandardLayers.Surfaces));
}));
Step("pattern", () =>
{
    var hp = vm.NewHoleParameters();
    vm.Change("Pattern", p =>
    {
        var holes = HoleDesign.FillPolygon(boundary, new PatternParameters { Burden = 4, Spacing = 4.5, Pattern = PatternType.Staggered, Orientation = 180 }, hp, out var rows);
        foreach (var h in holes) p.Add(h, vm.HoleLayer);
        p.Rows.AddRange(rows);
        p.Add(new PolylineEntity { Points = boundary.ToList(), Closed = true, Kind = LineKind.Boundary }, p.GetOrCreateLayer(StandardLayers.Boundary));
        p.Header.BlastOrientation = 180;
    });
    vm.View.ZoomAll();
});
Step("apply loading rules", () => vm.Change("Apply loading rules", p => Charging.ApplyRules(p, p.Holes)));
Step("initiation and auto tie-up", () => vm.Change("Automatic tie-up", p =>
{
    var ip = p.Add(new InitiationPoint { Position = new Vec3(1030, 1994, 300) }, p.GetOrCreateLayer(StandardLayers.Ties));
    var c17 = p.Products.First(x => x.Kind == ProductKind.SurfaceConnector && x.DelayMs == 17);
    var c42 = p.Products.First(x => x.Kind == ProductKind.SurfaceConnector && x.DelayMs == 42);
    Timing.AutoTieUp(p, p.Holes.ToList(), ip, c17, c42, 2);
}));
Step("monitoring point", () => vm.Change("Add point", p => p.Add(new PointEntity { Position = new Vec3(1300, 2200, 300), Kind = PointKind.Monitoring, Label = "House" })));
Step("timing labels are independent", () =>
{
    // new projects show the hole order: start from no timing label at all
    vm.ToggleView(v => { v.ShowHoleSequenceLabel = v.ShowHoleTimeLabel = v.ShowInholeDelayLabel = v.ShowSurfaceTimeLabel = false; v.TimingLabels = TimingLabelMode.None; });
    vm.ToggleTimingLabel(TimingLabelMode.InholeTime);
    vm.ToggleTimingLabel(TimingLabelMode.DelayNumber);
    vm.ToggleTimingLabel(TimingLabelMode.HoleSequence);
    if (!vm.IsTimingLabelVisible(TimingLabelMode.InholeTime) || !vm.IsTimingLabelVisible(TimingLabelMode.DelayNumber) || !vm.IsTimingLabelVisible(TimingLabelMode.HoleSequence))
        throw new Exception("enabling timing labels turned another label off");
    vm.ToggleTimingLabel(TimingLabelMode.InholeTime);
    if (vm.IsTimingLabelVisible(TimingLabelMode.InholeTime) || !vm.IsTimingLabelVisible(TimingLabelMode.DelayNumber) || !vm.IsTimingLabelVisible(TimingLabelMode.HoleSequence))
        throw new Exception("disabling hole time changed another timing label");
    vm.ToggleTimingLabel(TimingLabelMode.DelayNumber);
    if (vm.IsTimingLabelVisible(TimingLabelMode.DelayNumber) || !vm.IsTimingLabelVisible(TimingLabelMode.HoleSequence))
        throw new Exception("disabling in-hole delay changed hole order");
    vm.ToggleTimingLabel(TimingLabelMode.HoleSequence);
    vm.ToggleTimingLabel(TimingLabelMode.InholeTime);
});
Shot("01_plan");
Step("collapse the panel", () => { window.TogglePropertiesPanel(); if (window.PropertiesVisible) throw new Exception("panel still open"); });
Shot("01b_panel_collapsed");
Step("expand the panel", () => { window.TogglePropertiesPanel(); if (!window.PropertiesVisible) throw new Exception("panel still closed"); });
Step("full screen and back", () =>
{
    // the toggle saves settings.json, so keep the real one intact
    var saved = File.Exists(ProgramSettings.SettingsPath) ? File.ReadAllBytes(ProgramSettings.SettingsPath) : null;
    try
    {
        window.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().First(b => b.IsVisible && b.IsEnabled && b.Focusable).Focus(); window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.Alt, Avalonia.Input.PhysicalKey.Enter, null); Pump();
        if (window.WindowState != WindowState.FullScreen || settings.Window["FullScreen"] != "1") throw new Exception("not full screen");
        vm.View.Focus(); window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.Alt, Avalonia.Input.PhysicalKey.Enter, null); Pump();
        if (window.WindowState == WindowState.FullScreen || settings.Window["FullScreen"] != "0") throw new Exception("still full screen");
    }
    finally
    {
        if (saved != null) File.WriteAllBytes(ProgramSettings.SettingsPath, saved);
        else File.Delete(ProgramSettings.SettingsPath);
    }
});
Step("select row", () => vm.Doc!.Select(vm.Project!.Rows[0].HoleIds));
Shot("02_selection");
Step("visibility tab lists objects", () =>
{
    window.ShowInspectorTab(2); Pump();
    var boxes = window.GetLogicalDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible && c.Content is string t && t.EndsWith(")")).ToList();
    if (boxes.Count == 0) throw new Exception("no object kinds on the Visibility tab");
});
Shot("02b_visibility_tab");
Step("back to properties", () => window.ShowInspectorTab(0));
Step("mixed values and flag merge", () =>
{
    var a = new Hole { Length = 10, Diameter = 102, Exceptions = HoleException.Wet };
    var b = new Hole { Length = 12, Diameter = 102, Exceptions = HoleException.Wet | HoleException.Blocked };
    var grid = new PropertyGrid();
    var template = BlastDesigner.Domain.ProjectJson.DeepClone(a);
    grid.SetTargets(template, new object[] { a, b });
    if (!grid.Mixed.Contains("Length") || grid.Mixed.Contains("Diameter")) throw new Exception("mixed detection wrong");
    template.Exceptions |= HoleException.Misfire;
    grid.Changed.Add("Exceptions");
    grid.FlagEdits["Exceptions"] = (long)HoleException.Misfire;
    grid.ApplyTo(template, b);
    if (b.Exceptions != (HoleException.Wet | HoleException.Blocked | HoleException.Misfire)) throw new Exception("flag merge wrong: " + b.Exceptions);
});
Step("toggle table", () => window.ToggleTable());
Shot("03_table");
Step("toggle table back", () => window.ToggleTable());
Step("visualize timing overlay", () => vm.ShowOverlay(new BlastDesigner.Rendering.TimingContourOverlay { IntervalMs = 25 }));
Shot("04_isochrones");
Step("timing animation midway", () =>
{
    var times = vm.Project!.Holes.Where(h => h.FiringTimeMs.HasValue).Select(h => h.FiringTimeMs!.Value).OrderBy(t => t).ToList();
    vm.AnimationTime = times[times.Count / 2];
    vm.ShowOverlay(new BlastDesigner.Rendering.TimingContourOverlay { IntervalMs = 25, AnimationTimeMs = vm.AnimationTime });
});
Shot("04b_animation");
Step("end animation", () => { vm.AnimationTime = null; });
Step("burden relief", vm.BurdenRelief);
Shot("05_relief");
Step("first movement", vm.FirstMovement);
Shot("06_movement");
Step("first movement window: everything in view on opening, shape, arrow size, on plan", () =>
{
    var w = BlastDesigner.App.Services.Dialogs.LastShown!;
    if (w.Title != BlastDesigner.App.Services.Loc.T("First movement")) throw new Exception("not the first movement window: " + w.Title);
    Pump(); Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    var view = w.GetLogicalDescendants().OfType<BlastDesigner.App.Controls.Viewport>().First();
    foreach (var h in vm.Project!.Holes)
    {
        var (sx, sy) = view.Camera.ToScreen(h.Collar);
        if (sx < 0 || sy < 0 || sx > view.Bounds.Width || sy > view.Bounds.Height) throw new Exception($"hole {h.Label} is outside the plan on opening ({sx:0},{sy:0} in {view.Bounds.Width:0}x{view.Bounds.Height:0})");
    }
    // the plan keeps its own arrow style; the window's settings show on its chips and size field
    var buttons = w.GetLogicalDescendants().OfType<Button>().ToList();
    var triangles = buttons.First(b => (b.Tag as string) == "shape:1");
    triangles.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    if (triangles.Foreground != BlastDesigner.App.Services.Look.Accent || buttons.First(b => (b.Tag as string) == "shape:0").Foreground == BlastDesigner.App.Services.Look.Accent) throw new Exception("shape chip did not switch to triangles");
    var sizeText = w.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "arrowSize");
    var size0 = sizeText.Text;
    buttons.First(b => b.Name == "bigger").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    if (sizeText.Text == size0) throw new Exception($"arrow size stayed {size0}");
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "06b_first_movement_window.png"));
    buttons.First(b => b.Name == "smaller").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    buttons.First(b => (b.Tag as string) == "shape:0").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    w.GetLogicalDescendants().OfType<ToggleSwitch>().First(t => t.Name == "onPlan").IsChecked = false; Pump();
    if (vm.AnalysisOverlays.OfType<BlastDesigner.Rendering.FirstMovementOverlay>().Any()) throw new Exception("arrows left on the plan after switching off");
    w.Close(); Pump();
});
Step("clear overlays", vm.ClearOverlays);
Step("loading view 3D", () => { vm.ToggleView(v => v.ShowLoading = true); vm.View3D(); });
Shot("07_3d_loading");
Step("plan view", vm.PlanView);
Step("undo/redo", () => { vm.Undo(); vm.Redo(); });
Step("quantities", () =>
{
    var q = Quantities.Compute(vm.Project!);
    Console.WriteLine($"     holes {q.HoleCount}, volume {q.Volume:0} m3, explosive {q.ExplosiveKg:0} kg, PF {q.PowderFactorKgM3:0.000} kg/m3, MIC {q.MaxInstantaneousCharge:0} kg");
});
Step("vibration", () =>
{
    var pr = Vibration.Predict(vm.Project!, new Vec3(1300, 2200, 300));
    Console.WriteLine($"     PPV {pr.Ppv:0.00} mm/s at {pr.DistanceM:0} m with {pr.ChargeKg:0} kg");
});
Step("vibration map", () => vm.VibrationMap(false));
Shot("08_vibration_map");
Step("clear", vm.ClearOverlays);
Step("save and reload", () =>
{
    var path = Path.Combine(outDir, "smoke.bdp");
    BlastDesigner.IO.ProjectFile.Save(vm.Project!, path, 1);
    var p = BlastDesigner.IO.ProjectFile.Load(path);
    if (p.Holes.Count() != vm.Project!.Holes.Count()) throw new Exception("hole count differs after reload");
});
Step("pdf report", () =>
{
    using var fs = File.Create(Path.Combine(outDir, "report.pdf"));
    BlastDesigner.Rendering.Reports.WritePdf(vm.Project!, fs, new BlastDesigner.Rendering.ReportOptions());
});
Step("exports", () =>
{
    File.WriteAllText(Path.Combine(outDir, "blast.dxf"), CadFiles.ExportDxf(vm.Project!, vm.Project!.Entities));
    File.WriteAllText(Path.Combine(outDir, "firing.csv"), TextFiles.ExportFiringData(vm.Project!, vm.Project!.Holes, true));
});
Step("menu build", () => BlastDesigner.App.Tools.MenuEntry.Build(vm.BuildMenu()));
Step("menu translations", () =>
{
    if (settings.Language != "ru") return;
    var missing = new List<string>();
    void Walk(IEnumerable<BlastDesigner.App.Tools.MenuEntry> entries)
    {
        foreach (var e in entries)
        {
            if (!e.IsSeparator && !e.Header.Any(char.IsDigit) && !BlastDesigner.App.Services.Loc.HasTranslation(e.Header) && e.Header.Length > 2) missing.Add(e.Header);
            if (e.Children != null) Walk(e.Children);
        }
    }
    Walk(vm.BuildMenu());
    foreach (var (_, buttons) in vm.BuildToolbars())
        foreach (var b in buttons) if (!BlastDesigner.App.Services.Loc.HasTranslation(b.Tip)) missing.Add(b.Tip);
    if (missing.Count > 0) throw new Exception("untranslated: " + string.Join(" | ", missing.Distinct()));
});
// ---- version 6 features ----
void WindowShot(string name)
{
    Pump();
    var w = BlastDesigner.App.Services.Dialogs.LastShown;
    var frame = w?.CaptureRenderedFrame();
    var path = Path.Combine(outDir, name + ".png");
    frame?.Save(path);
    Console.WriteLine($"     screenshot {path}");
    w?.Close();
}
Step("v6 setup", () => vm.Change("Smoke v6 data", p =>
{
    p.Add(new PolylineEntity { Points = { new Vec3(995, 2045, 300), new Vec3(1075, 2045, 300) }, Kind = LineKind.Crest, Label = "Crest" });
    p.Add(new PolylineEntity { Points = { new Vec3(995, 2049, 288), new Vec3(1075, 2049, 288) }, Kind = LineKind.Toe, Label = "Toe" });
    p.Header.BlastOrientation = 0;
    foreach (var (h, i) in p.Holes.Take(10).Select((h, i) => (h, i))) { h.ActualCollar = h.Collar + new Vec3(i * 0.05, 0, 0); h.ActualLength = h.Length - i * 0.2; }
    foreach (var (d, q, v) in new[] { (300.0, 200.0, 9.0), (500, 200, 4.1), (800, 250, 2.2), (1200, 300, 1.1), (400, 150, 5.5) })
        p.VibrationRecords.Add(new VibrationRecord { Name = $"E{d}", DistanceM = d, ChargeKg = q, PpvMm = v });
    p.InitiationRules.Add(new InitiationRule { Name = "Default", DetonatorId = p.Products.First(x => x.IsDetonator).Id, PrimersPerDeck = 1 });
    p.Coordinates = new SiteCoordinateSystem { UtmZone = 50, SouthernHemisphere = true, OffsetEasting = 390000, OffsetNorthing = 6460000 };
}));
Step("initiation rules", () => { if (Initiation.ApplyRules(vm.Project!, vm.Project!.Holes) == 0) throw new Exception("no holes initiated"); });
Step("dashboard", () => Version6Windows.Dashboard(vm));
WindowShot("10_dashboard");
Step("burden check", () => Version6Windows.BurdenCheck(vm));
WindowShot("11_burden_check");
Step("front row profile", () => Version6Windows.FrontRowProfile(vm));
WindowShot("12_front_row_profile");
Step("charge vs time", () => Version6Windows.ChargeVsTime(vm));
WindowShot("13_charge_vs_time");
Step("timing overlap", () => Version6Windows.TimingOverlap(vm));
WindowShot("14_timing_overlap");
Step("volume overview", () => Version6Windows.VolumeOverview(vm));
WindowShot("15_volume_overview");
Step("flyrock", () => Version6Windows.Flyrock(vm));
WindowShot("16_flyrock");
Step("fragmentation", () => Version6Windows.Fragmentation(vm));
WindowShot("17_fragmentation");
Step("site law", () => Version6Windows.SiteLaw(vm));
WindowShot("18_site_law");
Step("exclusion zone", () => Version6Windows.ExclusionZone(vm));
WindowShot("19_exclusion_zone");
Step("tolerances", () => Version6Windows.Tolerances(vm));
WindowShot("20_tolerances");
Step("strata intercepts", () => Version6Windows.StrataIntercepts(vm));
WindowShot("21_strata");
Step("v6 exports", () =>
{
    File.WriteAllText(Path.Combine(outDir, "blast.kml"), Interchange.ExportKml(vm.Project!, vm.Project!.Entities));
    File.WriteAllText(Path.Combine(outDir, "iredes.xml"), Interchange.ExportIredes(vm.Project!, vm.Project!.Holes));
    File.WriteAllText(Path.Combine(outDir, "firing_ext.csv"), Interchange.ExportExtendedFiringData(vm.Project!, vm.Project!.Holes));
    if (Interchange.ImportIredes(Path.Combine(outDir, "iredes.xml"), vm.Project!.Design).Count != vm.Project!.Holes.Count(h => !h.IsDummy)) throw new Exception("IREDES round trip lost holes");
});

// ---- work-stage shell ----
Step("ribbon commands resolve", () =>
{
    vm.MissingCommands.Clear();
    var stages = vm.BuildStages();
    if (vm.MissingCommands.Count > 0) throw new Exception("unresolved: " + string.Join(" | ", vm.MissingCommands.Distinct()));
    Console.WriteLine($"     {stages.Count} stages, {stages.Sum(s => s.Groups.Count)} groups, {stages.Sum(s => s.Groups.Sum(g => g.Items.Count))} commands");
});
Step("shell translations", () =>
{
    if (settings.Language != "ru") return;
    var missing = new List<string>();
    foreach (var s in vm.BuildStages())
    {
        missing.Add(s.Name);
        foreach (var g in s.Groups) { missing.Add(g.Name); missing.AddRange(g.Items.Select(i => i.Label)); }
    }
    missing.AddRange(vm.BuildRail().Select(r => r.Tip));
    missing.AddRange(vm.QuickFilters().Select(f => f.Label));
    var untranslated = missing.Distinct().Where(m => !BlastDesigner.App.Services.Loc.HasTranslation("ui:" + m) && m.Any(char.IsLetter)).ToList();
    File.WriteAllLines(Path.Combine(outDir, "untranslated.txt"), untranslated);
    if (untranslated.Count > 0) throw new Exception("untranslated: " + string.Join(" | ", untranslated));
});
Step("object context menus", () =>
{
    var p = vm.Project!;
    void Check(string kind, IEnumerable<Guid> ids, string expect)
    {
        vm.Doc!.Select(ids);
        var items = vm.ObjectContextMenu().Where(e => !e.IsSeparator).Select(e => e.Header).ToList();
        if (!items.Contains(expect)) throw new Exception($"{kind} menu lacks '{expect}': {string.Join(", ", items)}");
    }
    Check("holes", p.Holes.Take(3).Select(h => h.Id), "Apply loading rules");
    Check("polyline", p.Entities.OfType<PolylineEntity>().Where(l => !l.Closed).Take(1).Select(l => l.Id), "Add along selected polyline…");
    Check("polygon", p.Entities.OfType<PolylineEntity>().Where(l => l.Closed).Take(1).Select(l => l.Id), "Fill selected polygon…");
    Check("surface", p.Entities.OfType<SurfaceEntity>().Take(1).Select(s => s.Id), "Volume…");
    Check("empty", Array.Empty<Guid>(), "Paste");
});
Step("hole at point menu icon", () =>
{
    vm.Doc!.Select(vm.Project!.Entities.OfType<PointEntity>().Select(e => e.Id));
    var entry = vm.ObjectContextMenu().FirstOrDefault(e => e.Header == "Add at points") ?? throw new Exception("no Add at points in the points menu");
    if (entry.Icon != "point") throw new Exception($"icon is {entry.Icon ?? "none"}");
    var menu = new ContextMenu { ItemsSource = BlastDesigner.App.Tools.MenuEntry.Build(vm.ObjectContextMenu()) };
    menu.Open(vm.View); Pump();
    var host = TopLevel.GetTopLevel(menu);
    if (host is Window w && w != window) w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "02c_points_menu.png"));
    else window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "02c_points_menu.png"));
    Console.WriteLine($"     menu host {host?.GetType().Name}");
    menu.Close(); vm.Doc!.ClearSelection();
});
for (int stage = 0; stage < 7; stage++)
{
    var st = stage;
    Step($"stage {st + 1}", () => window.Ribbon.Select(st));
    Shot($"30_stage_{st + 1}");
}
Step("pattern tool panel", () => { window.Ribbon.Select(2); vm.PatternTool(); });
Shot("31_pattern_tool");
Step("back to select", vm.SelectTool);
Step("dark plan zoomed", () => { vm.Doc!.Select(vm.Project!.Rows[2].HoleIds); vm.ZoomSelection(); vm.Doc.ClearSelection(); vm.PlanView(); });
Shot("32_dark_plan");
Step("dark 3d", () => { vm.ZoomAll(); vm.View3D(); });
Shot("33_dark_3d");
Step("dark timing", () => { vm.PlanView(); vm.ZoomAll(); vm.VisualizeTiming(); });
Shot("34_dark_timing");
Step("clear timing", vm.ClearOverlays);

Step("dropdown buttons open menus", () =>
{
    var buttons = window.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().ToList();
    void Click(string tip, int minItems)
    {
        var b = buttons.FirstOrDefault(x => Avalonia.Controls.ToolTip.GetTip(x) as string == tip) ?? throw new Exception("button not found: " + tip);
        b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Pump();
        var m = BlastDesigner.App.Views.Ui.LastMenu;
        if (m == null || !m.IsOpen) throw new Exception("menu did not open: " + tip);
        var n = m.ItemsSource?.Cast<object>().Count() ?? 0;
        if (n < minItems) throw new Exception($"menu {tip} has {n} items");
        m.Close(); Pump();
    }
    Click(BlastDesigner.App.Services.Loc.Ui("All commands in the classic menu"), 6);
    Click(BlastDesigner.App.Services.Loc.Ui("Blast properties, design data, resources"), 6);
    window.Ribbon.Select(0); Pump();
    buttons = window.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().ToList();
    Click(BlastDesigner.App.Services.Loc.Ui("Import"), 5);
    Click(BlastDesigner.App.Services.Loc.Ui("Drawing: polyline (others in the list)") + " (W)", 8);
});
Step("analysis maps toggle and clear", () =>
{
    vm.ClearOverlays();
    vm.VibrationMap(false);
    if (vm.AnalysisOverlays.Count != 1) throw new Exception("vibration map not shown");
    vm.VibrationMap(false);
    if (vm.AnalysisOverlays.Count != 0) throw new Exception("second click did not remove the map");
    vm.VibrationMap(true);
    var chip = window.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().FirstOrDefault(b => Avalonia.Controls.ToolTip.GetTip(b) as string == BlastDesigner.App.Services.Loc.Ui("Remove maps, isochrones and arrows from the plan (Esc)")) ?? throw new Exception("no clear chip");
    chip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    if (vm.AnalysisOverlays.Count != 0) throw new Exception("clear chip did not clear");
});
Step("analysis overlays never stack", () =>
{
    var cmds = new (string Name, Action Run, Type Kind)[]
    {
        ("timing", vm.VisualizeTiming, typeof(BlastDesigner.Rendering.TimingContourOverlay)),
        ("angle", vm.AngleOfInitiation, typeof(BlastDesigner.Rendering.TimingVectorOverlay)),
        ("movement", vm.FirstMovement, typeof(BlastDesigner.Rendering.FirstMovementOverlay)),
        ("relief", vm.BurdenRelief, typeof(BlastDesigner.Rendering.TimingVectorOverlay)),
        ("vibration map", () => vm.VibrationMap(false), typeof(BlastDesigner.Rendering.VibrationMapOverlay)),
        ("airblast map", () => vm.VibrationMap(true), typeof(BlastDesigner.Rendering.VibrationMapOverlay))
    };
    vm.ClearOverlays();
    foreach (var a in cmds)
        foreach (var b in cmds)
        {
            a.Run(); Pump(); b.Run(); Pump();
            var n = vm.AnalysisOverlays.Count;
            if (n > 1) throw new Exception($"{a.Name} then {b.Name}: {n} overlays on the plan");
            if (a.Name != b.Name && (n != 1 || vm.AnalysisOverlays[0].GetType() != b.Kind)) throw new Exception($"{a.Name} then {b.Name}: expected only {b.Name}, got {n}");
            vm.ClearOverlays(); BlastDesigner.App.Services.Dialogs.CloseWindow("Visualization"); Pump();
        }
    vm.VisualizeTiming(); Pump(); vm.VisualizeTiming(); Pump();
    if (vm.AnalysisOverlays.Count != 1) throw new Exception($"timing twice: {vm.AnalysisOverlays.Count} overlays");
    BlastDesigner.App.Services.Dialogs.CloseWindow("Visualization"); Pump();
    if (vm.AnalysisOverlays.Count != 0) throw new Exception("closing the timing window left its overlay");
});
Step("visualization window: the scrub bar shrinks with the window, no x3, half size at least", () =>
{
    vm.VisualizeTiming(); Pump();
    var w = BlastDesigner.App.Services.Dialogs.LastShown!;
    if (w.Title != BlastDesigner.App.Services.Loc.T("Visualization")) throw new Exception("window title: " + w.Title);
    if (w.MinWidth != w.Width / 2 || w.MinHeight != w.Height / 2) throw new Exception($"min size {w.MinWidth}x{w.MinHeight} for {w.Width}x{w.Height}");
    if (w.GetLogicalDescendants().OfType<RadioButton>().Any(r => (r.Content as string) == "x3")) throw new Exception("x3 still offered");
    var slider = w.GetLogicalDescendants().OfType<Slider>().First();
    w.Width = 920; Pump();
    var wide = slider.Bounds.Width;
    w.Width = 600; Pump();
    var narrow = slider.Bounds.Width;
    if (narrow >= wide - 200) throw new Exception($"scrub bar did not shrink: {wide} -> {narrow}");
    var row = (Control)slider.Parent!;
    if (row.Bounds.Width > w.Bounds.Width) throw new Exception("the top row overflows the window");
    w.Width = w.MinWidth; Pump();
    if (row.DesiredSize.Width > w.Bounds.Width + 0.5) throw new Exception($"at the smallest width the top row needs {row.DesiredSize.Width:0}, the window has {w.Bounds.Width:0}");
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "30b_visualization_narrow.png"));
    BlastDesigner.App.Services.Dialogs.CloseWindow("Visualization"); vm.ClearOverlays(); Pump();
});
Step("loading dialog", () => { var hole = vm.Project!.Holes.First(h => h.Decks.Count > 1); _ = HoleLoadingWindow.Show(vm, hole, 5); });
Step("loading dialog drags boundaries and primers", () =>
{
    Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    var editor = dlg.GetLogicalDescendants().OfType<HoleLoadingEditor>().First();
    var col = editor.Column;
    var decks = editor.Decks;
    Avalonia.Point At(double depth) => col.TranslatePoint(col.TubePoint(depth), dlg)!.Value;
    var left = Avalonia.Input.RawInputModifiers.LeftMouseButton;
    var shift = left | Avalonia.Input.RawInputModifiers.Shift;
    void Near(double actual, double expected, string what) { if (Math.Abs(actual - expected) > 1e-6) throw new Exception($"{what}: {actual} instead of {expected}"); }

    // Boundary between the top stemming and the explosive: 1 m steps, 0.1 m while Shift is held, back to 1 m when released.
    var b0 = decks[0].Bottom;
    dlg.MouseDown(At(b0), Avalonia.Input.MouseButton.Left); Pump();
    dlg.MouseMove(At(b0 + 1.37), left); Pump();
    Near(decks[0].Bottom, Math.Round(b0 + 1.37), "boundary with the plain mouse");
    Near(decks[1].Top, decks[0].Bottom, "neighbour deck top");
    dlg.MouseMove(At(b0 + 1.37), shift); Pump();
    Near(decks[0].Bottom, Math.Round(b0 + 1.37, 1), "boundary with Shift");
    dlg.MouseMove(At(b0 + 2.26), left); Pump();
    Near(decks[0].Bottom, Math.Round(b0 + 2.26), "boundary after Shift was released");
    dlg.MouseUp(At(b0 + 2.26), Avalonia.Input.MouseButton.Left); Pump();
    Near(decks[1].Top, decks[0].Bottom, "neighbour deck top after the drag");
    if (editor.Selected != 0) throw new Exception("dragging a boundary should select the deck above it");

    // Keyboard on the picked boundary: ↑ one metre, Shift+↓ 0.1 m.
    var kb = decks[0].Bottom;
    dlg.KeyPress(Avalonia.Input.Key.Up, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ArrowUp, null); Pump();
    Near(decks[0].Bottom, kb - 1, "↑ on the boundary");
    dlg.KeyPress(Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.Shift, Avalonia.Input.PhysicalKey.ArrowDown, null); Pump();
    Near(decks[0].Bottom, kb - 0.9, "Shift+↓ on the boundary");

    // Primer of the explosive deck: whole metres, 0.1 m with Shift, never outside its deck.
    var di = decks.FindIndex(d => d.Primers.Count > 0);
    if (di < 0) throw new Exception("rule-loaded hole has no primer");
    var deck = decks[di];
    var pr = deck.Primers[0];
    var target = deck.Top + (deck.Bottom - deck.Top) * 0.43;
    dlg.MouseDown(At(pr.Depth), Avalonia.Input.MouseButton.Left); Pump();
    dlg.MouseMove(At(target), left); Pump();
    Near(pr.Depth, Math.Clamp(Math.Round(target), deck.Top + 0.1, deck.Bottom - 0.1), "primer with the plain mouse");
    dlg.MouseMove(At(target), shift); Pump();
    Near(pr.Depth, Math.Round(target, 1), "primer with Shift");
    dlg.MouseMove(At(deck.Top - 3), left); Pump();
    Near(pr.Depth, Math.Round(deck.Top + 0.1, 3), "primer must stay inside its deck");
    dlg.MouseMove(At(target), shift); Pump();
    dlg.MouseUp(At(target), Avalonia.Input.MouseButton.Left); Pump();
    if (editor.Selected != di) throw new Exception("dragging a primer should select its deck");
    var pk = pr.Depth;
    dlg.KeyPress(Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ArrowDown, null); Pump();
    Near(pr.Depth, Math.Floor(pk + 1e-6) + 1, "↓ on the primer");
    if (!editor.Checks.Any(c => c.Ok)) throw new Exception("no passed checks on a loaded hole");
    if (decks.Zip(decks.Skip(1)).Any(x => Math.Abs(x.First.Bottom - x.Second.Top) > 1e-6)) throw new Exception("gap or overlap after the drags");
});
WindowShot("37_loading_dialog");
Step("loading dialog: save as rule", () =>
{
    var hole = vm.Project!.Holes.First(h => h.Decks.Count > 1);
    _ = HoleLoadingWindow.Show(vm, hole); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    var editor = dlg.GetLogicalDescendants().OfType<HoleLoadingEditor>().First();
    ColumnEdit.MoveBoundary(editor.Decks, 0, 4.0); editor.Refresh();
    var save = dlg.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().First(b => (string?)b.Tag == "saveRule");
    void Click(Avalonia.Controls.Button b) { b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Pump(); }
    void Capture(Avalonia.Controls.Window w, string name) { Pump(); Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump(); var path = Path.Combine(outDir, name + ".png"); w.CaptureRenderedFrame()?.Save(path); Console.WriteLine($"     screenshot {path}"); }
    if (!save.IsEnabled) throw new Exception("Save as rule disabled on a loaded hole");
    var before = vm.Project.LoadingRules.Count;

    Click(save);
    var form = SaveRuleWindow.Last!;
    var ruleWindow = BlastDesigner.App.Services.Dialogs.LastShown!;
    if ((string?)form.SaveButton.Content != BlastDesigner.App.Services.Loc.Ui("Save") || form.Existing != null) throw new Exception("default name should be a new rule");
    form.NameBox.Text = "Smoke rule"; Pump();
    Capture(ruleWindow, "37c_save_rule");
    Click(form.SaveButton);
    var rule = vm.Project.LoadingRules.FirstOrDefault(r => r.Name == "Smoke rule") ?? throw new Exception("rule not stored in the loading rules");
    if (vm.Project.LoadingRules.Count != before + 1) throw new Exception("expected one new rule");
    if (editor.SelectedRule != rule) throw new Exception("saved rule is not selected in the Rule list");
    var longer = new Hole { Length = hole.Length + 2, Diameter = hole.Diameter };
    Charging.ApplyRule(vm.Project, longer, rule);
    if (Math.Abs(Charging.TopStemming(vm.Project, longer) - 4) > 1e-6) throw new Exception("saved rule lost the 4 m stemming");
    if (Math.Abs(longer.Decks.Max(d => d.Bottom) - longer.EffectiveLength) > 1e-6) throw new Exception("explosive column did not adapt to the longer hole");

    // Same name again: the form offers to replace, and saving keeps one rule.
    Click(save);
    var again = SaveRuleWindow.Last!;
    again.NameBox.Text = "smoke rule"; Pump();
    if (again.Existing != rule || (string?)again.SaveButton.Content != BlastDesigner.App.Services.Loc.Ui("Replace")) throw new Exception("existing name does not offer Replace");
    Capture(BlastDesigner.App.Services.Dialogs.LastShown!, "37d_save_rule_replace");
    Click(again.SaveButton);
    if (vm.Project.LoadingRules.Count != before + 1) throw new Exception("replace added a second rule");
    if (vm.Project.LoadingRules.Single(r => r.Id == rule.Id).Name != "smoke rule") throw new Exception("rule not replaced");

    Capture(dlg, "37e_loading_dialog_rule_saved");

    var empty = dlg.GetLogicalDescendants().OfType<Avalonia.Controls.Button>()
        .First(b => b.GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>().Any(t => t.Text == BlastDesigner.App.Services.Loc.T("Empty")));
    Click(empty);
    if (save.IsEnabled) throw new Exception("Save as rule must be disabled without explosive");
    dlg.Close();
});
Step("loading dialog: Enter commits a field and keeps the window", () =>
{
    var hole = vm.Project!.Holes.First(h => h.Decks.Count > 1);
    var result = HoleLoadingWindow.Show(vm, hole); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    var editor = dlg.GetLogicalDescendants().OfType<HoleLoadingEditor>().First();
    var col = editor.Column;
    var pt = col.TranslatePoint(col.TubePoint(editor.Decks[0].Top + 0.5), dlg)!.Value;
    dlg.MouseDown(pt, Avalonia.Input.MouseButton.Left); dlg.MouseUp(pt, Avalonia.Input.MouseButton.Left); Pump();
    if (editor.Selected != 0) throw new Exception("click did not select the top deck");
    var box = dlg.GetLogicalDescendants().OfType<Avalonia.Controls.TextBox>().First(t => t.Name == "bottom");
    box.Focus(); box.Text = "5.0"; Pump();
    dlg.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null); Pump();
    if (!dlg.IsVisible || result.IsCompleted) throw new Exception("Enter closed the loading window");
    if (Math.Abs(editor.Decks[0].Bottom - 5) > 1e-6 || Math.Abs(editor.Decks[1].Top - 5) > 1e-6) throw new Exception($"Enter did not apply the bottom: {editor.Decks[0].Bottom}");
    var focused = dlg.FocusManager?.GetFocusedElement() as Avalonia.Controls.TextBox;
    if (focused?.Name != "bottom" || focused.SelectedText != focused.Text) throw new Exception("focus should stay in the Bottom field with its text selected");
    var five = 5.0.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
    if (!editor.Checks.Any(c => c.Text.Contains(five))) throw new Exception("checks were not updated after Enter");
    var cells = dlg.GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>().Count(t => t.Text == five);
    if (cells < 2) throw new Exception("table was not updated after Enter");
    // A second value right away, then Ctrl+Enter applies and closes.
    focused.Text = "6"; dlg.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null); Pump();
    if (Math.Abs(editor.Decks[0].Bottom - 6) > 1e-6) throw new Exception("second Enter did not apply");
    dlg.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Enter, null); Pump();
    if (dlg.IsVisible || !result.IsCompleted || result.Result is not { } decks || Math.Abs(decks[0].Bottom - 6) > 1e-6) throw new Exception("Ctrl+Enter did not apply and close");
});
Step("loading dialog: primer cards are always open", () =>
{
    var hole = vm.Project!.Holes.First(h => h.Decks.Count > 1);
    _ = HoleLoadingWindow.Show(vm, hole); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    var editor = dlg.GetLogicalDescendants().OfType<HoleLoadingEditor>().First();
    Avalonia.Controls.TextBox? Field(string name) => dlg.GetLogicalDescendants().OfType<Avalonia.Controls.TextBox>().FirstOrDefault(t => t.Name == name && t.IsEffectivelyVisible);
    void Open(int count, string when)
    {
        for (int j = 0; j < count; j++)
            if (Field("primerDepth" + j) == null) throw new Exception($"primer {j + 1} card is collapsed {when}");
        var combos = dlg.GetLogicalDescendants().OfType<Avalonia.Controls.ComboBox>().Count(c => c.IsEffectivelyVisible);
        if (combos < 2 + 2 * count) throw new Exception($"booster/detonator lists missing {when}: {combos} combo boxes");
    }
    var di = editor.Selected;
    if (editor.Decks[di].Primers.Count != 1) throw new Exception("expected the explosive deck with one primer to be selected");
    Open(1, "when the window opens");
    var picks = dlg.GetLogicalDescendants().OfType<Avalonia.Controls.ComboBox>().Where(c => c.IsEffectivelyVisible && c.ItemsSource is IEnumerable<string> it && it.FirstOrDefault() == "—").Take(2).ToList();
    var detName = vm.Project.Products.First(x => x.IsDetonator).Name;
    var detCombo = picks.First(c => ((IEnumerable<string>)c.ItemsSource!).Contains(detName));
    var boosterCombo = picks.First(c => c != detCombo);
    if (detCombo.TranslatePoint(new Avalonia.Point(0, 0), dlg)!.Value.Y >= boosterCombo.TranslatePoint(new Avalonia.Point(0, 0), dlg)!.Value.Y) throw new Exception("detonator list should be above the booster list");
    if (Math.Abs(detCombo.Bounds.Width - boosterCombo.Bounds.Width) > 1 || detCombo.Bounds.Width < 2 * Field("primerDepth0")!.Bounds.Width) throw new Exception($"detonator/booster lists should span the card ({detCombo.Bounds.Width:0} px)");
    var add = dlg.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().First(b => (string?)b.Tag == "addPrimer");
    add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Pump();
    Open(2, "after adding a primer");
    var col = editor.Column;
    var at = col.TranslatePoint(col.TubePoint(editor.Decks[0].Bottom), dlg)!.Value;
    dlg.MouseDown(at, Avalonia.Input.MouseButton.Left);
    dlg.MouseMove(col.TranslatePoint(col.TubePoint(editor.Decks[0].Bottom + 1), dlg)!.Value, Avalonia.Input.RawInputModifiers.LeftMouseButton);
    dlg.MouseUp(col.TranslatePoint(col.TubePoint(editor.Decks[0].Bottom + 1), dlg)!.Value, Avalonia.Input.MouseButton.Left); Pump();
    var pick = col.TranslatePoint(col.TubePoint((editor.Decks[di].Top + editor.Decks[di].Bottom) / 2 + 0.3), dlg)!.Value;
    dlg.MouseDown(pick, Avalonia.Input.MouseButton.Left); dlg.MouseUp(pick, Avalonia.Input.MouseButton.Left); Pump();
    Open(2, "after a drag and a click on the column");
    var depth = Field("primerDepth1")!;
    depth.Focus(); depth.Text = "9.5";
    dlg.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null); Pump();
    Open(2, "after Enter");
    if (Math.Abs(editor.Decks[di].Primers[1].Depth - 9.5) > 1e-6) throw new Exception("Enter did not move the primer");
    if ((dlg.FocusManager?.GetFocusedElement() as Avalonia.Controls.TextBox)?.Name != "primerDepth1") throw new Exception("focus left the primer depth field");
    if (!dlg.IsVisible) throw new Exception("Enter closed the window");
});
WindowShot("37f_loading_dialog_primers_open");
Step("loading dialog: empty hole warns about primers", () =>
{
    var hole = vm.Project!.Holes.First(h => h.Decks.Count > 1);
    _ = HoleLoadingWindow.Show(vm, hole); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    var editor = dlg.GetLogicalDescendants().OfType<HoleLoadingEditor>().First();
    var empty = dlg.GetLogicalDescendants().OfType<Avalonia.Controls.Button>()
        .First(b => b.GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>().Any(t => t.Text == BlastDesigner.App.Services.Loc.T("Empty")));
    empty.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Pump();
    if (editor.Decks.Count != 0) throw new Exception("Empty left decks");
    var c = editor.Checks[0];
    if (c.Ok || c.Text != BlastDesigner.App.Services.Loc.Ui("No explosive in the hole, nowhere to put a primer")) throw new Exception($"empty hole check: {c}");
});
WindowShot("37b_loading_dialog_empty");
Step("hole form", () => { var h = vm.Project!.Holes.First(x => x.Decks.Count > 1); _ = HoleEditWindow.Show(vm, new List<Hole> { h }); });
WindowShot("38_hole_form");
Step("hole form scrolled to the end", () =>
{
    var h = vm.Project!.Holes.First(x => x.Decks.Count > 1);
    _ = HoleEditWindow.Show(vm, new List<Hole> { h }); Pump();
    var sv = BlastDesigner.App.Services.Dialogs.LastShown!.GetLogicalDescendants().OfType<ScrollViewer>().First();
    sv.Offset = new Vector(0, 10000); Pump();
});
WindowShot("38c_hole_form_end");
Step("hole form for several holes", () => { _ = HoleEditWindow.Show(vm, vm.Project!.Holes.Take(5).ToList()); });
WindowShot("38b_hole_form_several");
Step("resources window", () => { _ = ProductsWindow.Show(vm.Project!); });
WindowShot("39_resources");
Step("resources: filter by kind, search, kind fields, delete asks when used", () =>
{
    _ = ProductsWindow.Show(vm.Project!); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    var ed = dlg.GetLogicalDescendants().OfType<ProductsEditor>().First();
    void Click(Button b) { b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump(); }
    var chip = dlg.GetLogicalDescendants().OfType<Button>().First(b => b.Content is string s && s.StartsWith(BlastDesigner.App.Services.Loc.Ui("Surface connectors")));
    Click(chip);
    if (ed.Visible.Count == 0 || ed.Visible.Any(p => p.Kind != ProductKind.SurfaceConnector)) throw new Exception("kind filter shows other kinds");
    var row = dlg.GetLogicalDescendants().OfType<Button>().First(b => b.Tag is Product);
    Click(row);
    var names = dlg.GetLogicalDescendants().OfType<TextBox>().Select(t => t.Name).ToHashSet();
    if (!names.Contains("delay") || names.Contains("density") || names.Contains("vod")) throw new Exception("surface connector shows the wrong fields: " + string.Join(",", names));
    Pump(); dlg.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "39b_resources_connectors.png"));
    var search = dlg.GetLogicalDescendants().OfType<TextBox>().First();
    search.Text = "109"; Pump();
    if (ed.Visible.Count != 1) throw new Exception($"search found {ed.Visible.Count} instead of 1");
    search.Text = ""; Pump();
    Click(chip);
    // The window edits copies: find the used product among them by id.
    var usedId = vm.Project!.Products.First(p => vm.Project.Holes.Any(h => h.Decks.Any(d => d.ProductId == p.Id))).Id;
    var used = ed.Products.First(p => p.Id == usedId);
    Click(dlg.GetLogicalDescendants().OfType<Button>().First(b => b.Tag == used));
    var count = ed.Products.Count;
    var delete = dlg.GetLogicalDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) is string t && t.StartsWith(BlastDesigner.App.Services.Loc.Ui("Delete")));
    Click(delete);
    if (ed.Products.Count != count) throw new Exception("used product deleted without asking");
    Pump(); dlg.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "39c_resources_delete_used.png"));
    Click(dlg.GetLogicalDescendants().OfType<Button>().First(b => (string?)b.Tag == "confirmDelete"));
    if (ed.Products.Count != count - 1 || ed.Products.Contains(used)) throw new Exception("confirmed delete did not remove the product");
    ed.AddProduct(ProductKind.Booster); Pump();
    if (ed.Selected?.Kind != ProductKind.Booster || ed.ChangedCount != 2) throw new Exception($"add: {ed.Selected?.Kind}, changed {ed.ChangedCount}");
    dlg.Close();
});
Step("Ctrl+D clears the selection", () =>
{
    vm.SelectAll(); Pump();
    if (!vm.HasSelection) throw new Exception("nothing selected to clear");
    vm.View.Focus();
    window.KeyPress(Avalonia.Input.Key.D, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.D, "d"); Pump();
    if (vm.HasSelection) throw new Exception("Ctrl+D left objects selected");
});
Step("settings window", () => { _ = SettingsWindow.Show(vm.Settings); });
WindowShot("41_settings");
Step("settings: a section opens from its heading", () =>
{
    _ = SettingsWindow.Show(vm.Settings); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    var sv = dlg.GetLogicalDescendants().OfType<ScrollViewer>().First(s => s.Content is StackPanel);
    var nav = dlg.GetLogicalDescendants().OfType<Button>().Where(b => b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == BlastDesigner.App.Services.Loc.Ui("Viewport"))).First();
    nav.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    var heading = sv.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == BlastDesigner.App.Services.Loc.Ui("Viewport"));
    var y = heading.TranslatePoint(new Point(0, 0), sv)!.Value.Y;
    if (y < -1 || y > 30) throw new Exception($"Viewport heading is at {y:0} px from the top instead of the top");
    dlg.Close();
});
Step("settings: light theme previews live and Cancel puts the dark one back", () =>
{
    _ = SettingsWindow.Show(vm.Settings); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    var form = dlg.GetLogicalDescendants().OfType<SettingsForm>().First();
    var light = dlg.GetLogicalDescendants().OfType<Button>().First(b => (string?)b.Tag == "theme:" + ProgramSettings.LightTheme);
    light.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    if (!BlastDesigner.App.Services.Look.IsLight || !form.Edited.IsLight) throw new Exception("light theme not previewed");
    if (form.Edited.BackgroundColor != ProgramSettings.LightBackground) throw new Exception("default background did not follow the theme");
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    dlg.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "41b_settings_light.png"));
    form.Closing!(false); Pump();
    if (BlastDesigner.App.Services.Look.IsLight) throw new Exception("Cancel did not restore the dark theme");
});
Step("light theme with a green accent", () =>
{
    BlastDesigner.App.Services.Look.ApplyTheme(true, 0xFF3FB97A);
    vm.Settings.BackgroundColor = ProgramSettings.LightBackground;
    window.ShowInspectorTab(2); vm.View.Redraw();
});
Shot("42_light_theme");
Step("back to the dark theme", () =>
{
    BlastDesigner.App.Services.Look.ApplyTheme(false, ProgramSettings.DefaultAccent);
    vm.Settings.BackgroundColor = ProgramSettings.DarkBackground;
    vm.View.Redraw();
});
Step("hole symbol key on the visibility tab", () =>
{
    window.ShowInspectorTab(0); Pump(); window.ShowInspectorTab(2); Pump();
    var key = window.GetLogicalDescendants().OfType<Expander>().FirstOrDefault(e => (string?)e.Header == BlastDesigner.App.Services.Loc.Ui("Hole symbols")) ?? throw new Exception("hole symbol key missing");
    if (!key.IsExpanded) throw new Exception("key should start open");
    key.BringIntoView(); Pump();
});
Shot("43_hole_symbols");
Step("back to properties after the key", () => window.ShowInspectorTab(0));
Step("surfaces coloured by kind", () =>
{
    var s = vm.Project!.Entities.OfType<SurfaceEntity>().First();
    vm.Change("Smoke surface kind", p => p.Entities.OfType<SurfaceEntity>().First(x => x.Id == s.Id).Kind = SurfaceKind.Topography);
    vm.Settings.SurfaceColoring = ProgramSettings.SurfaceByType;
    vm.View3D(); vm.ZoomAll();
});
Shot("44_surfaces_by_kind");
Step("surfaces coloured by elevation", () => { vm.Settings.SurfaceColoring = ProgramSettings.SurfaceByElevation; vm.View.Redraw(); });
Shot("44b_surfaces_by_elevation");
Step("surface key on the visibility tab", () =>
{
    vm.Settings.SurfaceColoring = ProgramSettings.SurfaceByType;
    window.ShowInspectorTab(0); Pump(); window.ShowInspectorTab(2); Pump();
    var key = window.GetLogicalDescendants().OfType<Expander>().FirstOrDefault(e => (string?)e.Header == BlastDesigner.App.Services.Loc.Ui("Surface colours")) ?? throw new Exception("surface key missing");
    var texts = key.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
    if (!texts.Any(t => t != null && t.StartsWith(BlastDesigner.App.Services.Loc.Enum(SurfaceKind.Topography)))) throw new Exception("key does not list the topography surface: " + string.Join(" | ", texts));
    window.ShowInspectorTab(0); vm.PlanView(); vm.Undo();
});
Step("surface kind list shows the kind colours", () =>
{
    var s = vm.Project!.Entities.OfType<SurfaceEntity>().First();
    vm.Doc!.Select(new[] { s.Id }); window.ShowInspectorTab(0); Pump();
    var combo = window.GetLogicalDescendants().OfType<ComboBox>()
        .FirstOrDefault(c => c.ItemsSource is System.Collections.IEnumerable items && items.OfType<StackPanel>().Count() == Enum.GetValues<SurfaceKind>().Length)
        ?? throw new Exception("surface kind list without colour swatches");
    var swatch = ((System.Collections.IEnumerable)combo.ItemsSource!).OfType<StackPanel>().ElementAt((int)SurfaceKind.Bench).Children.OfType<Border>().First();
    if (((Avalonia.Media.ISolidColorBrush)swatch.Background!).Color.ToUInt32() != vm.Settings.SurfaceColor("Bench")) throw new Exception("bench swatch is not the bench colour");
    combo.IsDropDownOpen = true; Pump();
});
Shot("45_surface_kind_list");
Step("close the surface kind list", () =>
{
    foreach (var c in window.GetLogicalDescendants().OfType<ComboBox>()) c.IsDropDownOpen = false;
    vm.Doc!.ClearSelection();
});
Step("settings: surface colours", () =>
{
    _ = SettingsWindow.Show(vm.Settings); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    if (!dlg.GetLogicalDescendants().OfType<TextBox>().Any(t => t.Name == "surfaceBench")) throw new Exception("no colour for benches");
    var heading = dlg.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == BlastDesigner.App.Services.Loc.Ui("Surface colouring"));
    var sv = dlg.GetLogicalDescendants().OfType<ScrollViewer>().First(s => s.Content is StackPanel);
    sv.Offset = new Vector(0, heading.TranslatePoint(new Point(0, 0), (Visual)sv.Content!)!.Value.Y - 20); Pump();
});
WindowShot("41c_settings_surfaces");
Step("hole form applies a field and the layer in one undo step", () =>
{
    var h = vm.Project!.Holes.First(x => !x.IsDummy);
    var diameter = h.Diameter;
    var task = vm.EditHoles(new List<Hole> { h }); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    if (!dlg.GetLogicalDescendants().OfType<HoleEditForm>().Any()) throw new Exception("hole form did not open");
    var box = dlg.GetLogicalDescendants().OfType<TextBox>().First(t => t.Name == nameof(Hole.Diameter));
    box.Text = "115"; box.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent)); Pump();
    var apply = dlg.GetLogicalDescendants().OfType<Button>().First(b => b.Classes.Contains("primary") && b.Tag == null);
    apply.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    if (!task.IsCompleted) throw new Exception("form did not close");
    if (vm.Project.FindEntity(h.Id) is not Hole after || Math.Abs(after.Diameter - 115) > 1e-9) throw new Exception("diameter not applied");
    vm.Undo();
    if (vm.Project.FindEntity(h.Id) is not Hole back || Math.Abs(back.Diameter - diameter) > 1e-9) throw new Exception("undo did not restore the diameter");
});
Step("visibility: hole items in two columns", () =>
{
    window.ShowInspectorTab(0); Pump(); window.ShowInspectorTab(2); Pump();
    var grids = window.GetLogicalDescendants().OfType<Avalonia.Controls.Primitives.UniformGrid>().Where(g => g.Columns == 2).ToList();
    var labels = grids.SelectMany(g => g.Children.OfType<CheckBox>()).Select(c => (c.Content as TextBlock)?.Text).ToList();
    foreach (var name in new[] { "Temperature", "Dip track", "Actual track", "Grade point", "Deviation", "Burden marker", "Deck masses", "MWD profile", "Deck sequence", "Surface time", "Detonator ID", "Bearing", "Collar" })
        if (!labels.Contains(BlastDesigner.App.Services.Loc.Ui(name))) throw new Exception($"{name} missing from the two-column lists");
    var sv = grids[0].GetLogicalAncestors().OfType<ScrollViewer>().First();
    foreach (var e in sv.GetLogicalDescendants().OfType<Expander>()) e.IsExpanded = false;
    sv.Offset = new Vector(0, 230); Pump();
});
Shot("48_visibility_columns");
Step("hole items drawn on the plan", () =>
{
    window.ShowInspectorTab(0);
    var h = vm.Project!.Holes.First(x => x.ActualLength.HasValue);
    vm.Change("Smoke hole data", p =>
    {
        foreach (var x in p.Holes.Take(12)) { x.Temperature = 42; x.DetonatorId = "IK-" + x.Label; }
    });
    vm.ToggleView(v =>
    {
        v.HoleLabels = HoleLabelMode.Label | HoleLabelMode.Angle | HoleLabelMode.Bearing | HoleLabelMode.Temperature | HoleLabelMode.DeckMasses | HoleLabelMode.DeckSequence | HoleLabelMode.DetonatorId;
        v.ShowHoleSequenceLabel = true; v.ShowDipTrack = true; v.ShowActualTrack = true; v.ShowGradePoint = true; v.ShowDeviation = true; v.ShowBurdenMarker = true; v.ShowProfile = true;
    });
    vm.View3D(); vm.View.Camera.Pitch = 40; vm.View.Camera.Target = h.Collar; vm.View.Camera.Scale = 18; vm.View.Redraw();
});
Shot("48b_hole_items");
Step("hole items back off", () =>
{
    vm.Undo(); vm.Undo(); vm.PlanView(); vm.ZoomAll();
});
Task<TimingLineSetup?>? timingLineTask = null;
Step("timing line window: result, preview and fields by type", () =>
{
    vm.PlanView(); vm.ZoomAll();
    var holes = vm.Project!.Holes.Where(h => !h.IsDummy).ToList();
    var ys = holes.Select(h => h.Collar.Y).ToList();
    var y = ys.Max() + 3;
    var pts = new List<BlastDesigner.Domain.Geometry.Vec3> { new(holes.Min(h => h.Collar.X) - 3, y, 300), new(holes.Average(h => h.Collar.X), y + 2, 300), new(holes.Max(h => h.Collar.X) + 3, y, 300) };
    timingLineTask = TimingLineWindow.Show(vm, pts, holes); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    var form = dlg.GetLogicalDescendants().OfType<TimingLineForm>().First();
    if (form.Planned == null || form.Planned.Timed == 0) throw new Exception("no planned times in the result");
    if (!vm.View.Overlays.OfType<BlastDesigner.Rendering.TimingLinePreviewOverlay>().Any()) throw new Exception("no preview on the plan");
    TextBox? Field(string name) => dlg.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == name);
    void Click(string tag) { dlg.GetLogicalDescendants().OfType<Button>().First(b => (string?)b.Tag == tag).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump(); }
    Click("type:" + TimingLineStyle.TimeLine);
    if (Field("burn0") != null) throw new Exception("contour line shows the burn rate");
    Click("type:" + TimingLineStyle.BurnLine);
    if (Field("burn0") == null) throw new Exception("burn line hides the burn rate");
    var same = dlg.GetLogicalDescendants().OfType<ToggleSwitch>().First(t => t.Name == "sameForAll");
    same.IsChecked = false; Pump();
    if (Field("relief1") == null) throw new Exception("separate segments should show a row per segment");
    if (Field("progress0") != null) throw new Exception("progress shown while progressive relief is off");
    var before = form.PlannedTimes.Values.Max();
    var relief = Field("relief0")!; relief.Text = "20"; Pump();
    if (form.PlannedTimes.Values.Max() <= before) throw new Exception("the result did not follow the relief");
    Click("type:" + TimingLineStyle.AdvancedBurn);
    if (Field("angle1") == null || Field("relief0") != null) throw new Exception("burn with angles shows the wrong fields");
    Click("type:" + TimingLineStyle.TimeLine);
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    dlg.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "47_timing_line.png"));
});
Shot("47b_timing_line_preview");
Step("timing line window: Cancel removes the preview and changes nothing", () =>
{
    var times = vm.Project!.Holes.Select(h => h.ElectronicTimeMs).ToList();
    var form = BlastDesigner.App.Services.Dialogs.LastShown!.GetLogicalDescendants().OfType<TimingLineForm>().First();
    form.Closing!(false); Pump();
    if (timingLineTask is not { IsCompleted: true } t || t.Result != null) throw new Exception("Cancel did not close with no result");
    if (vm.View.Overlays.OfType<BlastDesigner.Rendering.TimingLinePreviewOverlay>().Any()) throw new Exception("preview left on the plan");
    if (!vm.Project.Holes.Select(h => h.ElectronicTimeMs).SequenceEqual(times)) throw new Exception("Cancel changed hole times");
});
Task? editLineTask = null;
Step("edit a timing line: its values, layer and reprogramming in one step", () =>
{
    var holes = vm.Project!.Holes.Where(h => !h.IsDummy).ToList();
    var y = holes.Max(h => h.Collar.Y) + 3;
    var pts = new List<BlastDesigner.Domain.Geometry.Vec3> { new(holes.Min(h => h.Collar.X) - 3, y, 300), new(holes.Max(h => h.Collar.X) + 3, y, 300) };
    var line = BlastDesigner.Engine.EbsTimingLine.CreateEntity(pts, new[] { new TimingLineSegment(5, 8, 0) }, new BlastDesigner.Engine.TimingLineOptions());
    line.HoleIds = holes.Take(20).Select(h => h.Id).ToList();
    vm.Change("Smoke timing line", p => p.Add(line));
    editLineTask = vm.EditTimingLine((TimingLineEntity)vm.Project.FindEntity(line.Id)!); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    if (dlg.Title != BlastDesigner.App.Services.Loc.T("Edit timing line")) throw new Exception("not the edit window: " + dlg.Title);
    var form = dlg.GetLogicalDescendants().OfType<TimingLineForm>().First();
    if (form.Planned?.Timed != 20) throw new Exception($"the line should time its own 20 holes, plans {form.Planned?.Timed}");
    if (!dlg.GetLogicalDescendants().OfType<ComboBox>().Any(c => c.Name == "layer") || !dlg.GetLogicalDescendants().OfType<ToggleSwitch>().Any(t => t.Name == "reprogram"))
        throw new Exception("layer list or reprogram switch missing");
    var relief = dlg.GetLogicalDescendants().OfType<TextBox>().First(t => t.Name == "relief0");
    relief.Text = "12"; Pump();
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    dlg.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "47c_edit_timing_line.png"));
    var planned = form.PlannedTimes.ToDictionary(kv => kv.Key, kv => kv.Value);
    form.Closing!(true); Pump();
    if (editLineTask is not { IsCompleted: true }) throw new Exception("edit window did not close");
    var edited = (TimingLineEntity)vm.Project.FindEntity(line.Id)!;
    if (Math.Abs(edited.Segments[0].ReliefMsPerM - 12) > 1e-9) throw new Exception("line relief not changed");
    foreach (var (id, t) in planned)
        if (vm.Project.FindEntity(id) is not Hole h || h.ElectronicTimeMs is not double et || Math.Abs(et - t) > 1e-6) throw new Exception("hole not reprogrammed as previewed");
    vm.Undo();
    if (Math.Abs(((TimingLineEntity)vm.Project.FindEntity(line.Id)!).Segments[0].ReliefMsPerM - 8) > 1e-9) throw new Exception("one undo should restore the line");
    vm.Undo();
});
Step("moving a timing line reprograms its holes in the same undo step", () =>
{
    var holes = vm.Project!.Holes.Where(h => !h.IsDummy).ToList();
    var y = holes.Max(h => h.Collar.Y) + 3;
    // a bent line with a different relief on each side: a straight one gives the same relative times wherever it is moved
    var (x0, x1) = (holes.Min(h => h.Collar.X) - 3, holes.Max(h => h.Collar.X) + 3);
    var pts = new List<BlastDesigner.Domain.Geometry.Vec3> { new(x0, y, 300), new((x0 + x1) / 2, y + 10, 300), new(x1, y, 300) };
    var line = BlastDesigner.Engine.EbsTimingLine.CreateEntity(pts, new[] { new TimingLineSegment(5, 8, 0), new TimingLineSegment(5, 3, 0) }, new BlastDesigner.Engine.TimingLineOptions());
    line.HoleIds = holes.Where((h, i) => i % 3 != 0).Select(h => h.Id).ToList();
    vm.Change("Smoke timing line", p => { p.Add(line); BlastDesigner.Engine.EbsTimingLine.Apply(p, p.Holes.Where(h => line.HoleIds.Contains(h.Id)).ToList(), line); });
    var before = vm.Project.Holes.ToDictionary(h => h.Id, h => h.ElectronicTimeMs);
    var delta = new BlastDesigner.Domain.Geometry.Vec3((holes.Max(h => h.Collar.X) - holes.Min(h => h.Collar.X)) / 2, -(y - holes.Min(h => h.Collar.Y)) / 2, 0);
    // what a line drawn at the new place would give
    var moved = (TimingLineEntity)vm.Project.FindEntity(line.Id)!.CloneWithNewId();
    moved.Translate(delta);
    var expected = BlastDesigner.Engine.EbsTimingLine.Plan(vm.Project, vm.Project.Holes.Where(h => line.HoleIds.Contains(h.Id)).ToList(), moved.Points, moved.Segments, BlastDesigner.Engine.EbsTimingLine.OptionsOf(moved), vm.Project.Electronic.MaxTimeMs, vm.Project.Electronic.TimeStepMs).Times;
    vm.Doc!.Select(new[] { line.Id });
    vm.MoveSelection(delta);
    var changed = 0;
    foreach (var (id, t) in expected)
    {
        var h = (Hole)vm.Project.FindEntity(id)!;
        if (h.ElectronicTimeMs is not double et || Math.Abs(et - t) > 1e-6) throw new Exception($"hole {h.Label} not reprogrammed: {h.ElectronicTimeMs} vs {t}");
        if (before[id] != et) changed++;
    }
    if (changed == 0) throw new Exception("moving the line changed no hole time");
    if (vm.Project.Holes.Where(h => !line.HoleIds.Contains(h.Id)).Any(h => h.ElectronicTimeMs != before[h.Id])) throw new Exception("holes not of the line were reprogrammed");
    vm.Undo();
    if (vm.Project.Holes.Any(h => h.ElectronicTimeMs != before[h.Id])) throw new Exception("one undo should restore the hole times");
    // a line from an older file does not know its holes: it finds them at its old place and keeps them
    var own = line.HoleIds.ToHashSet();
    vm.Change("Smoke old line", p =>
    {
        ((TimingLineEntity)p.FindEntity(line.Id)!).HoleIds.Clear();
        var k = 0;
        foreach (var h in p.Holes.Where(h => !h.IsDummy && !own.Contains(h.Id))) BlastDesigner.Engine.Timing.SetTime(h, 1003 + 17 * k++, false);
    });
    var others = vm.Project.Holes.ToDictionary(h => h.Id, h => h.ElectronicTimeMs);
    vm.MoveSelection(delta);
    var found = ((TimingLineEntity)vm.Project.FindEntity(line.Id)!).HoleIds.ToHashSet();
    if (!found.SetEquals(own)) throw new Exception($"old line found {found.Count} holes, {found.Count(own.Contains)} of its {own.Count}");
    foreach (var (id, t) in expected)
        if (((Hole)vm.Project.FindEntity(id)!).ElectronicTimeMs is not double et || Math.Abs(et - t) > 1e-6) throw new Exception("old line did not reprogram its holes");
    if (vm.Project.Holes.Where(h => !own.Contains(h.Id)).Any(h => h.ElectronicTimeMs != others[h.Id])) throw new Exception("old line reprogrammed holes not of it");
    vm.Undo();
    if (((TimingLineEntity)vm.Project.FindEntity(line.Id)!).HoleIds.Count != 0) throw new Exception("undo should forget the found holes too");
    vm.Undo();
    vm.Doc.ClearSelection();
    vm.Undo();
});
Step("timing line tool shows the hole order instead of the hole number with electronic detonators", () =>
{
    var p = vm.Project!;
    var (labels, sequence) = (p.View.HoleLabels, p.View.ShowHoleSequenceLabel);
    var timing = p.View.TimingLabels;
    void NumberShown() => vm.ToggleView(v => { v.HoleLabels |= HoleLabelMode.Label; v.ShowHoleSequenceLabel = false; if (v.TimingLabels == TimingLabelMode.HoleSequence) v.TimingLabels = TimingLabelMode.None; });
    var det = p.Products.First(x => x.Kind == ProductKind.ElectronicDetonator);
    var analog = p.Products.First(x => x.Kind == ProductKind.DownholeDetonator);
    var primers = p.Holes.SelectMany(h => BlastDesigner.Engine.Charging.Primers(h)).Select(x => x.Primer).ToList();
    var kept = primers.Select(x => x.DetonatorId).ToList();
    if (primers.Count == 0) throw new Exception("no primers to test with");
    foreach (var x in primers) x.DetonatorId = analog.Id;
    NumberShown();
    vm.TimingLineTool(); Pump(); vm.SelectTool();
    if (!p.View.HoleLabels.HasFlag(HoleLabelMode.Label) || vm.IsTimingLabelVisible(TimingLabelMode.HoleSequence)) throw new Exception("analog detonators only: the labels should stay");
    primers[0].DetonatorId = det.Id;
    vm.TimingLineTool(); Pump();
    if (p.View.HoleLabels.HasFlag(HoleLabelMode.Label) || !vm.IsTimingLabelVisible(TimingLabelMode.HoleSequence)) throw new Exception("hole number not swapped for the hole order");
    vm.SelectTool();
    for (var i = 0; i < primers.Count; i++) primers[i].DetonatorId = kept[i];
    vm.ToggleView(v => { v.HoleLabels = labels; v.ShowHoleSequenceLabel = sequence; v.TimingLabels = timing; });
});
Step("time envelope", () => AnalysisWindows.TimeEnvelope(vm));
WindowShot("35_time_envelope");
Step("ribbon scrolls with the mouse wheel", () =>
{
    window.Width = 900; Pump();
    window.Ribbon.Select(5); Pump();
    window.Width = 700; Pump();
    var strips = window.GetLogicalDescendants().OfType<Avalonia.Controls.ScrollViewer>().Where(s => s.Content is Avalonia.Controls.StackPanel p && p.Orientation == Avalonia.Layout.Orientation.Horizontal && s.Extent.Width > s.Viewport.Width + 10).ToList();
    if (strips.Count < 2) throw new Exception($"expected tabs and ribbon to overflow at 700 px, found {strips.Count}");
    foreach (var sv in strips) {
    var before = sv.Offset.X;
    var inner = (sv.Content as Avalonia.Controls.Panel)!.Children[0];
    inner.RaiseEvent(new Avalonia.Input.PointerWheelEventArgs(inner, new Avalonia.Input.Pointer(1, Avalonia.Input.PointerType.Mouse, true), window, new Avalonia.Point(10, 10), 0, new Avalonia.Input.PointerPointProperties(), Avalonia.Input.KeyModifiers.None, new Avalonia.Vector(0, -2)) { RoutedEvent = Avalonia.Input.InputElement.PointerWheelChangedEvent });
    Pump();
    if (sv.Offset.X <= before) throw new Exception($"wheel did not scroll the ribbon ({before} -> {sv.Offset.X})");
    }
    Shot("36_ribbon_scrolled");
    window.Width = 1400; Pump();
});
Step("ribbon drags with the left button", () =>
{
    window.Width = 700; window.Ribbon.Select(5); Pump();
    var strips = window.Ribbon.GetLogicalDescendants().OfType<Avalonia.Controls.ScrollViewer>().Where(s => s.Content is Avalonia.Controls.StackPanel p && p.Orientation == Avalonia.Layout.Orientation.Horizontal && s.Extent.Width > s.Viewport.Width + 10).ToList();
    var groups = strips.Last();
    groups.Offset = new Avalonia.Vector(0, 0); Pump();
    var stage = window.Ribbon.Current;
    var start = groups.TranslatePoint(new Avalonia.Point(300, 40), window)!.Value;
    window.MouseDown(start, Avalonia.Input.MouseButton.Left);
    // Real mouse moves carry the pressed button in their modifiers; the headless helper needs it passed explicitly.
    for (int i = 1; i <= 10; i++) window.MouseMove(start - new Avalonia.Point(i * 20, 0), Avalonia.Input.RawInputModifiers.LeftMouseButton);
    window.MouseUp(start - new Avalonia.Point(200, 0), Avalonia.Input.MouseButton.Left);
    Pump();
    if (groups.Offset.X < 150) throw new Exception($"drag did not scroll the ribbon (offset {groups.Offset.X})");
    if (window.Ribbon.Current != stage) throw new Exception("drag changed the stage");
    var tabs = strips.First();
    var tab = tabs.TranslatePoint(new Avalonia.Point(60, 19), window)!.Value;
    window.MouseDown(tab, Avalonia.Input.MouseButton.Left); window.MouseUp(tab, Avalonia.Input.MouseButton.Left); Pump();
    if (window.Ribbon.Current == stage) throw new Exception("a plain click on a stage tab no longer works");
    window.Width = 1400; Pump();
});
Step("done button and Enter on tools", () =>
{
    var doneTip = BlastDesigner.App.Services.Loc.Ui("Done") + " (Enter)";
    void Done()
    {
        var b = window.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().First(x => Avalonia.Controls.ToolTip.GetTip(x) as string == doneTip);
        b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Pump();
    }
    foreach (var (name, open) in new (string, Action)[] { ("rotate", () => vm.SetTool(new RotateTool(vm, false))), ("pan", () => vm.SetTool(new PanTool(vm))), ("zoom", () => vm.SetTool(new ZoomBoxTool(vm))), ("tie", () => vm.SetTool(new TieTool(vm))), ("set time", vm.SetTimeTool), ("hole", () => vm.HoleTool()), ("measure", vm.MeasureTool) })
    {
        open(); Pump();
        Done();
        if (vm.ActiveTool is not SelectTool) throw new Exception($"Done did not close the {name} tool");
    }
    vm.SetTool(new RotateTool(vm, false)); Pump();
    window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null); Pump();
    if (vm.ActiveTool is not SelectTool) throw new Exception("Enter did not close the rotate tool");
    var countBefore = vm.Project!.Entities.Count;
    vm.DrawPolylineTool(true); Pump();
    void Click(double x, double y) { var w = new BlastDesigner.Domain.Geometry.Vec3(x, y, 300); var (sx, sy) = vm.View.Camera.ToScreen(w); vm.ActiveTool!.PointerPressed(new ToolPointer { X = sx, Y = sy, World = w, Snapped = w, Left = true, ClickCount = 1 }); }
    Click(1100, 2100);
    Done();
    if (vm.ActiveTool is SelectTool) throw new Exception("Done with one point closed the polygon tool and lost the entry");
    Click(1130, 2100); Click(1130, 2125);
    Done();
    if (vm.Project!.Entities.Count <= countBefore) throw new Exception("Done did not finish the polygon");
    if (vm.ActiveTool is SelectTool) throw new Exception("polygon tool should stay for the next polygon");
    Done();
    if (vm.ActiveTool is not SelectTool) throw new Exception("second Done did not close the polygon tool");
    vm.Undo();
});
Step("Esc closes a tool and its floating panel; two-stage for drawing tools", () =>
{
    void Esc() { vm.View.Focus(); window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null); Pump(); }
    foreach (var (name, open) in new (string, Action)[] { ("set time", vm.SetTimeTool), ("hole", () => vm.HoleTool()), ("tie", () => vm.SetTool(new TieTool(vm))), ("measure", vm.MeasureTool), ("rotate", () => vm.SetTool(new RotateTool(vm, false))) })
    {
        open(); Pump();
        if (vm.ActiveTool is SelectTool) throw new Exception($"{name} tool did not open");
        Esc();
        if (vm.ActiveTool is not SelectTool) throw new Exception($"Esc did not close the {name} tool");
    }
    // Drawing tool: the first Esc drops the pending points but keeps the tool, the second closes it.
    vm.DrawPolylineTool(true); Pump();
    var drawing = vm.ActiveTool;
    void Click(double x, double y) { var w = new BlastDesigner.Domain.Geometry.Vec3(x, y, 300); var (sx, sy) = vm.View.Camera.ToScreen(w); vm.ActiveTool!.PointerPressed(new ToolPointer { X = sx, Y = sy, World = w, Snapped = w, Left = true, ClickCount = 1 }); }
    Click(1100, 2100); Click(1130, 2100); Pump();
    Esc();
    if (vm.ActiveTool != drawing) throw new Exception("first Esc should keep the drawing tool and only cancel the entry");
    Esc();
    if (vm.ActiveTool is not SelectTool) throw new Exception("second Esc should close the drawing tool");
});
Step("row tool: live holes and anchored buttons", () =>
{
    vm.Change("Design data", p => p.Design.Spacing = 5);
    vm.RowTool(); Pump();
    var tool = (RowDrawTool)vm.ActiveTool!;
    // The anchored buttons follow the rendered frame, so render one before looking at them.
    void Frame() { Pump(); window.CaptureRenderedFrame(); Pump(); }
    ToolPointer At(double x, double y, bool shift = false) { var w = new BlastDesigner.Domain.Geometry.Vec3(x, y, 300); var (sx, sy) = vm.View.Camera.ToScreen(w); return new ToolPointer { X = sx, Y = sy, World = w, Snapped = w, Left = true, ClickCount = 1, Modifiers = shift ? Avalonia.Input.KeyModifiers.Shift : Avalonia.Input.KeyModifiers.None }; }
    tool.PointerPressed(At(1080, 2140));
    tool.PointerMoved(At(1110, 2140)); Frame();
    if (vm.View.Preview.Markers.Count != 7) throw new Exception($"expected 7 preview holes along 30 m at 5 m, got {vm.View.Preview.Markers.Count}");
    if (window.AnchorButtons.Count != 0) throw new Exception("buttons shown before the first segment is finished");
    // Shift+click: bends of a curved row, drawing goes on with ✓ and ✕ at the last point.
    tool.PointerPressed(At(1110, 2140, shift: true));
    tool.PointerMoved(At(1125, 2160));
    tool.PointerPressed(At(1125, 2160, shift: true)); Frame();
    if (tool.Points.Count != 3) throw new Exception($"Shift+click should add bends, {tool.Points.Count} points");
    var buttons = window.AnchorButtons;
    if (buttons.Count != 2) throw new Exception($"expected ✓ and ✕ next to the segment end, got {buttons.Count}");
    var (ex, _) = vm.View.Camera.ToScreen(tool.Points[^1]);
    var centre = (Avalonia.Controls.Canvas.GetLeft(buttons[0]) + Avalonia.Controls.Canvas.GetLeft(buttons[1]) + 30) / 2;
    if (Math.Abs(centre - ex) > 1.5) throw new Exception($"buttons are not centred under the segment end ({centre:0} vs {ex:0})");
    Shot("38_row_tool");
    void Press(string icon) { window.AnchorButtons.First(b => (string?)b.Tag == icon).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Frame(); }
    Press("close");
    if (tool.Points.Count != 0 || vm.View.Preview.Markers.Count != 0) throw new Exception($"✕ should discard the whole row like Esc, {tool.Points.Count} points left");
    if (vm.ActiveTool != tool) throw new Exception("✕ should keep the row tool for a new row");
    if (window.AnchorButtons.Count != 0) throw new Exception("buttons stay after ✕");
    // A plain second click ends a straight row at once.
    var holesBefore = vm.Project!.Entities.OfType<BlastDesigner.Domain.Model.Hole>().Count();
    tool.PointerPressed(At(1080, 2140)); tool.PointerPressed(At(1110, 2140)); Frame();
    var added = vm.Project!.Entities.OfType<BlastDesigner.Domain.Model.Hole>().Count() - holesBefore;
    if (added != 7) throw new Exception($"the second click should create the 7-hole row, created {added}");
    if (vm.ActiveTool != tool || tool.Points.Count != 0) throw new Exception("row tool should stay active and empty for the next row");
    if (window.AnchorButtons.Count != 0) throw new Exception("buttons stay after the row was created");
    vm.Undo();
    // A curved row ends with ✓.
    holesBefore = vm.Project!.Entities.OfType<BlastDesigner.Domain.Model.Hole>().Count();
    tool.PointerPressed(At(1080, 2140)); tool.PointerPressed(At(1110, 2140, shift: true)); tool.PointerPressed(At(1125, 2160, shift: true)); Frame();
    Press("check");
    if (vm.Project!.Entities.OfType<BlastDesigner.Domain.Model.Hole>().Count() <= holesBefore || tool.Points.Count != 0) throw new Exception("✓ did not create the curved row");
    vm.Undo(); vm.Undo(); vm.SelectTool();
});
Step("selection is undoable", () =>
{
    vm.SelectTool(); vm.Doc!.ClearSelection(); Pump();
    var holes = vm.Project!.Holes.Take(3).Select(h => h.Id).ToList();
    vm.Doc.Select(new[] { holes[0] }); vm.Doc.Select(new[] { holes[1], holes[2] }, BlastDesigner.Domain.SelectionMode.Add); Pump();
    var modified = vm.Doc.IsModified;
    window.KeyPress(Avalonia.Input.Key.Z, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Z, null); Pump();
    if (!vm.Doc.Selection.SetEquals(new[] { holes[0] })) throw new Exception($"Ctrl+Z should bring back the one-hole selection, got {vm.Doc.Selection.Count}");
    vm.Undo(); Pump();
    if (vm.Doc.Selection.Count != 0) throw new Exception("second undo should clear the selection");
    if (vm.Doc.IsModified != modified) throw new Exception("selection undo changed the modified flag");
    vm.Redo(); vm.Redo(); Pump();
    if (vm.Doc.Selection.Count != 3) throw new Exception("redo did not restore the selection");
    vm.Doc.ClearSelection(record: false);
});
Step("tool windows open once", () =>
{
    vm.VisualizeTiming();
    Pump();
    var first = BlastDesigner.App.Services.Dialogs.LastShown;
    vm.VisualizeTiming();
    Pump();
    if (BlastDesigner.App.Services.Dialogs.LastShown != first) throw new Exception("second timing window created");
    Version6Windows.Dashboard(vm); var d1 = BlastDesigner.App.Services.Dialogs.LastShown;
    Version6Windows.Dashboard(vm); if (BlastDesigner.App.Services.Dialogs.LastShown != d1) throw new Exception("second dashboard created");
    d1?.Close(); first?.Close(); vm.ClearOverlays();
    Pump();
    vm.VisualizeTiming(); Pump();
    if (BlastDesigner.App.Services.Dialogs.LastShown == first) throw new Exception("closed window was reused");
    BlastDesigner.App.Services.Dialogs.LastShown?.Close(); vm.ClearOverlays();
});
Step("tie palette stays put when a connector is picked", () =>
{
    vm.Change("Add connector", p => p.Products.Add(new BlastDesigner.Domain.Model.Product { Name = "SC 17", Kind = BlastDesigner.Domain.Model.ProductKind.SurfaceConnector, DelayMs = 17 }));
    vm.ShowTiePalette(); Pump();
    var win = BlastDesigner.App.Services.Dialogs.LastShown ?? throw new Exception("tie palette did not open");
    var buttons = win.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().Where(b => b.Content is Avalonia.Controls.StackPanel).ToList();
    if (buttons.Count < 2) throw new Exception($"palette should list connectors, found {buttons.Count} buttons");
    buttons[^1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Pump();
    if (BlastDesigner.App.Services.Dialogs.LastShown != win) throw new Exception("picking a connector recreated the palette window");
    if (!win.IsVisible) throw new Exception("palette window closed after picking a connector");
    win.Close(); vm.SelectTool(); vm.Undo();
});
Step("hidden objects are not selectable", () =>
{
    vm.ZoomAll();
    if (!vm.Project!.View.ShowTies) vm.ToggleView(v => v.ShowTies = true);
    var w = vm.View.Bounds.Width; var h = vm.View.Bounds.Height;
    if (!vm.View.InRect(0, 0, w, h, false).OfType<TieEntity>().Any()) throw new Exception("ties not selectable while shown");
    vm.ToggleView(v => v.ShowTies = false);
    if (vm.View.InRect(0, 0, w, h, false).OfType<TieEntity>().Any()) throw new Exception("hidden ties caught by rectangle");
    vm.SelectAll();
    if (vm.Doc!.SelectedEntities.OfType<TieEntity>().Any()) throw new Exception("hidden ties selected by Select all");
    vm.ToggleView(v => v.ShowSurfaces = false);
    if (vm.View.InRect(0, 0, w, h, true).OfType<SurfaceEntity>().Any()) throw new Exception("hidden surfaces caught by rectangle");
    vm.ToggleView(v => { v.ShowTies = true; v.ShowSurfaces = true; });
    vm.Doc.ClearSelection();
});

Step("delete selection", () => { vm.Doc!.Select(vm.Project!.Rows[0].HoleIds); vm.DeleteSelected(); });
Shot("09_after_delete");

Step("startup assistant", () =>
{
    void Capture(Avalonia.Controls.Window w, string name) { Pump(); Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump(); var path = Path.Combine(outDir, name + ".png"); w.CaptureRenderedFrame()?.Save(path); Console.WriteLine($"     screenshot {path}"); }
    var folder = Path.Combine(outDir, "recent", "Карьер Северный", "Блок 12");
    Directory.CreateDirectory(folder);
    string File(string name, DateTime when)
    {
        var p = Path.Combine(folder, name);
        System.IO.File.WriteAllText(p, "{}");
        System.IO.File.SetLastWriteTime(p, when);
        return p;
    }
    var now = DateTime.Now;
    var pinned = File("Взрыв_12-340.bdp", now.AddDays(-20));
    var s = new ProgramSettings
    {
        Language = settings.Language,
        PinnedFiles = { pinned },
        RecentFiles =
        {
            File("Взрыв_12-352.bdp", now.AddHours(-1)),
            File("Взрыв_12-351.bdp", now.AddDays(-1)),
            pinned,
            File("Взрыв_12-349.bdp", now.AddDays(-Math.Min(4, now.Day - 1)).AddHours(-3)),
            Path.Combine(folder, "Старый_сервер", "Взрыв_11-120.bdp"),
            File("Взрыв_11-098.bdp", now.AddMonths(-3))
        }
    };
    _ = StartupWindow.Show(s); Pump();
    var w = BlastDesigner.App.Services.Dialogs.LastShown!;
    var form = StartupWindow.Last!;
    if (form.VisibleFiles.FirstOrDefault() != pinned) throw new Exception("pinned file is not on top");
    if (form.VisibleFiles.Count != 6) throw new Exception("missing files must stay in the list");
    Capture(w, "40_startup_assistant");
    form.Highlight(true);
    Capture(w, "40b_startup_drop");
    form.Highlight(false);
    var search = w.GetLogicalDescendants().OfType<Avalonia.Controls.TextBox>().First();
    search.Text = "11-"; Pump();
    if (form.VisibleFiles.Count != 2) throw new Exception("search does not filter by name");
    w.Close(); Pump();
    _ = StartupWindow.Show(new ProgramSettings { Language = settings.Language }); Pump();
    Capture(BlastDesigner.App.Services.Dialogs.LastShown!, "40c_startup_empty");
    BlastDesigner.App.Services.Dialogs.LastShown!.Close(); Pump();
});
Step("what's new: shown once after an update, never on a new installation", () =>
{
    var saved = 0;
    var updated = new ProgramSettings { LastSeenVersion = "0.1.6" };
    var w = WhatsNewWindow.ShowIfUpdated(updated, _ => saved++) ?? throw new Exception("no notes after updating from 0.1.6");
    Pump();
    if (saved != 1 || updated.LastSeenVersion != Changelog.Current.ToString()) throw new Exception($"version not remembered: {updated.LastSeenVersion}, saved {saved}");
    var release = Changelog.Load(settings.Language).First(r => r.Version == Changelog.Current);
    var texts = w.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Inlines?.Count > 0 ? string.Concat(t.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : t.Text ?? "").ToList();
    var first = release.Sections[0].Items[0].Replace("**", "").Replace("`", "");
    if (!texts.Any(t => t == first)) throw new Exception("first item of this version not shown: " + first);
    if (!texts.Any(t => t.Contains(Changelog.Current.ToString()))) throw new Exception("version not in the header");
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "50_whats_new.png"));
    w.Close(); Pump();
    if (WhatsNewWindow.ShowIfUpdated(updated, _ => saved++) != null || saved != 1) throw new Exception("shown again on the same version");
    if (WhatsNewWindow.ShowIfUpdated(new ProgramSettings { LastSeenVersion = Changelog.Current.ToString() }, _ => saved++) != null) throw new Exception("shown for the version already seen");
});
Step("usage statistics: hot keys and flags reach the log and the analysis window", () =>
{
    vm.Doc!.ClearSelection();
    void Press(Avalonia.Input.Key key) { window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key }); Pump(); }
    // grid on and straight back off: a hot key twice and one flag reverted
    Press(Avalonia.Input.Key.G);
    Press(Avalonia.Input.Key.G);
    // tabs the user clicks: a ribbon stage and the Layers tab of the right panel; the program's own switches do not count
    var stage = (window.Ribbon.Current + 1) % window.Ribbon.Stages.Count;
    var stageName = window.Ribbon.Stages[stage].Name;
    window.GetLogicalDescendants().OfType<Button>().First(b => b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == BlastDesigner.App.Services.Loc.Ui(stageName)))
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    var panelTabs = window.GetLogicalDescendants().OfType<TabControl>().First(t => t.Items.OfType<TabItem>().Any(i => (i.Tag as string) == "Layers"));
    panelTabs.SelectedIndex = 1; Pump();
    window.ShowInspectorTab(0); Pump();
    UsageLog.Flush();
    var events = UsageAnalysis.Read(UsageLog.Files());
    if (!events.Any(e => e.Source == UsageLog.SessionSource && e.Id == "start")) throw new Exception("no session start in the log");
    if (events.Count(e => e.Source == UsageLog.Hotkey && e.Id == "G") < 2) throw new Exception("hot key G not logged");
    if (!events.Any(e => e.Source == UsageLog.Flag && e.Id == "ShowGrid")) throw new Exception("grid flag not logged");
    if (!events.Any(e => e.Source == UsageLog.Flag && e.Id.StartsWith("HoleLabels."))) throw new Exception("hole label bits not logged");
    if (!events.Any(e => e.Source == UsageLog.Tab && e.Id == stageName && e.Value == "ribbon")) throw new Exception("ribbon tab not logged");
    if (!events.Any(e => e.Source == UsageLog.Tab && e.Id == "Layers" && e.Value == "panel")) throw new Exception("panel tab not logged");
    if (events.Any(e => e.Source == UsageLog.Tab && e.Id == "Properties")) throw new Exception("a switch by the program was logged as a tab click");
    var w = UsageWindow.Show(); Pump();
    var reverts = w.GetLogicalDescendants().OfType<Grid>().FirstOrDefault(g => g.Name == "reverts") ?? throw new Exception("no flags switched back listed");
    if (!reverts.Children.OfType<TextBlock>().Any(t => t.Text == UsageWindow.FlagName("ShowGrid"))) throw new Exception("grid not among the flags switched back");
    w.GetLogicalDescendants().OfType<Button>().First(b => (b.Tag as string) == "top:1").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    var tabs = w.GetLogicalDescendants().OfType<Grid>().FirstOrDefault(g => g.Name == "top") ?? throw new Exception("no tabs listed");
    if (!tabs.Children.OfType<TextBlock>().Any(t => t.Text == UsageWindow.Name(UsageLog.Tab, "Layers"))) throw new Exception("Layers not among the tabs");
    w.GetLogicalDescendants().OfType<Button>().First(b => (b.Tag as string) == "top:3").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    var top = w.GetLogicalDescendants().OfType<Grid>().FirstOrDefault(g => g.Name == "top") ?? throw new Exception("no hot keys listed");
    if (!top.Children.OfType<TextBlock>().Any(t => t.Text == "G")) throw new Exception("G not among the hot keys");
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "51_usage_statistics.png"));
    w.Close(); Pump();
    _ = SettingsWindow.Show(vm.Settings); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    if (!dlg.GetLogicalDescendants().OfType<ToggleSwitch>().Any(t => t.Name == "usageLogging") || !dlg.GetLogicalDescendants().OfType<Button>().Any(b => b.Name == "usageAnalysis"))
        throw new Exception("usage statistics missing in the settings");
    dlg.GetLogicalDescendants().OfType<SettingsForm>().First().Closing!(false); Pump();
});
Step("new blast window: today's shot date, fields in cards, template and Create", () =>
{
    var header = new ProjectHeader { Name = "New blast", Author = "Tester", ShotDate = DateTime.Today };
    var task = NewBlastWindow.Show(header, new[] { "Quarry A" }, 1); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    var form = dlg.GetLogicalDescendants().OfType<NewBlastForm>().First();
    if (dlg.GetLogicalDescendants().OfType<CalendarDatePicker>().First(d => d.Name == "shotDate").SelectedDate != DateTime.Today) throw new Exception("shot date is not today");
    if (form.Template != "Quarry A") throw new Exception("template not preselected");
    dlg.GetLogicalDescendants().OfType<TextBox>().First(t => t.Name == nameof(ProjectHeader.BlastNumber)).Text = "B-17";
    dlg.GetLogicalDescendants().OfType<ComboBox>().First(c => c.Name == "template").SelectedIndex = 0; Pump();
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    dlg.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "52_new_blast.png"));
    dlg.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "create").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    if (task is not { IsCompleted: true } || task.Result is not { } setup) throw new Exception("Create did not close with a result");
    if (setup.Template != null || header.BlastNumber != "B-17") throw new Exception($"result: template {setup.Template}, number {header.BlastNumber}");
});
Step("program icon on the main window and dialogs", () =>
{
    if (window.Icon == null) throw new Exception("main window has no icon");
    var w = WhatsNewWindow.Show(); Pump();
    if (w.Icon == null) throw new Exception("tool window has no icon");
    w.Close(); Pump();
});
Step("selecting on the plan brings Properties forward", () =>
{
    vm.SelectTool();
    vm.Doc!.ClearSelection();
    window.ShowInspectorTab(1); Pump();
    var hole = vm.Project!.Holes.First();
    var (sx, sy) = vm.View.Camera.ToScreen(hole.Collar);
    var at = new ToolPointer { X = sx, Y = sy, World = hole.Collar, Snapped = hole.Collar, Left = true, ClickCount = 1, Hit = hole };
    vm.ActiveTool!.PointerPressed(at); vm.ActiveTool!.PointerReleased(at); Pump();
    if (!vm.Doc.Selection.Contains(hole.Id)) throw new Exception("the click selected nothing");
    if (window.LayersVisible || !window.PropertiesVisible) throw new Exception("Properties is not the active tab");
    // a click on empty plan selects nothing and leaves the tab alone
    vm.Doc.ClearSelection(); window.ShowInspectorTab(1); Pump();
    var empty = new ToolPointer { X = 1, Y = 1, World = hole.Collar, Snapped = hole.Collar, Left = true, ClickCount = 1 };
    vm.ActiveTool!.PointerPressed(empty); vm.ActiveTool!.PointerReleased(empty); Pump();
    if (!window.LayersVisible) throw new Exception("an empty click switched the tab");
    window.ShowInspectorTab(0);
});
Step("bench elements: recognize a survey, change a class by click, apply with split and surface, undo", () =>
{
    vm.NewSilent(); Pump();
    // a synthetic pit survey, as the import would bring it (lines at elevation 0 left out)
    var scene = SurveySynth.Generate(13);
    vm.Change("survey", p => { var layer = p.GetOrCreateLayer("Survey", 0xFF64748B); foreach (var e in scene.Entities) if (e is not PolylineEntity pl || pl.Points.Any(v => v.Z != 0)) p.Add(e, layer); });
    vm.View.ZoomAll(); Pump();
    var task = vm.RecognizeBenchElements();
    for (int i = 0; i < 1000 && !task.IsCompleted; i++) { Pump(); Thread.Sleep(10); }
    Pump();
    var form = BlastDesigner.App.Services.Dialogs.LastShown?.Content as SurveyRecognitionForm ?? throw new Exception("no bench elements window");
    if (vm.ActiveTool is not SurveyClassTool) throw new Exception("the class tool is not active");
    var r = form.Recognition;
    if (!r.Segments.Any(x => x.Class == SurveyLineClass.Crest) || r.Benches.Count == 0) throw new Exception("no crest or bench recognized");
    // a click between two vertices of the first segment cycles its class
    var seg = r.Segments[0];
    var line = (PolylineEntity)vm.Project!.FindEntity(seg.Segment.LineId)!;
    var mid = Vec3.Lerp(line.Points[seg.Segment.From], line.Points[seg.Segment.From + 1], 0.5);
    var (sx, sy) = vm.View.Camera.ToScreen(mid);
    var before = seg.Class;
    vm.ActiveTool!.PointerPressed(new ToolPointer { X = sx, Y = sy, World = mid, Snapped = mid, Left = true, ClickCount = 1 });
    if (r.Segments[0].Class == before) throw new Exception("click did not change the class");
    Shot("60_bench_elements");
    BlastDesigner.App.Services.Dialogs.LastShown!.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "60b_bench_elements_window.png"));
    form.Set(0, before, false);
    form.GetLogicalDescendants().OfType<CheckBox>().First(c => c.Name == "surface").IsChecked = true;
    form.Apply();
    form.Close!(); Pump();
    var p2 = vm.Project!;
    if (!p2.Entities.OfType<PolylineEntity>().Any(l => l.Kind == LineKind.Crest)) throw new Exception("no line got the crest type");
    if (!p2.Entities.OfType<SurfaceEntity>().Any()) throw new Exception("no bench surface built");
    if (vm.ActiveTool is SurveyClassTool || vm.View.Overlays.OfType<SurveyRecognitionOverlay>().Any()) throw new Exception("tool or overlay left after closing");
    vm.Undo(); Pump();
    if (vm.Project!.Entities.OfType<PolylineEntity>().Any(l => l.Kind == LineKind.Crest) || vm.Project.Entities.OfType<SurfaceEntity>().Any()) throw new Exception("undo did not take it all back");
});
UsageLog.Stop();

Console.WriteLine(failures == 0 ? "SMOKE PASSED" : $"SMOKE FAILED ({failures})");
return failures == 0 ? 0 : 1;
