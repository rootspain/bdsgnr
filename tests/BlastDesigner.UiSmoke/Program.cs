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
// The real settings.json is kept as it is for the whole run: several steps save settings (full screen, the
// initiation catalogue's position on close) and would leave the smoke settings in the user's file. It is put back at
// the end, on any exit and after a crash.
var realSettings = File.Exists(ProgramSettings.SettingsPath) ? File.ReadAllBytes(ProgramSettings.SettingsPath) : null;
// the user's catalogue too: the run starts without it and leaves it as it was
var realCatalogue = File.Exists(BlastDesigner.IO.UserCatalogue.FilePath) ? File.ReadAllBytes(BlastDesigner.IO.UserCatalogue.FilePath) : null;
if (realCatalogue != null) File.Delete(BlastDesigner.IO.UserCatalogue.FilePath);
void RestoreSettings()
{
    try
    {
        if (realCatalogue != null) File.WriteAllBytes(BlastDesigner.IO.UserCatalogue.FilePath, realCatalogue);
        else if (File.Exists(BlastDesigner.IO.UserCatalogue.FilePath)) File.Delete(BlastDesigner.IO.UserCatalogue.FilePath);
        if (realSettings != null) File.WriteAllBytes(ProgramSettings.SettingsPath, realSettings);
        else if (File.Exists(ProgramSettings.SettingsPath)) File.Delete(ProgramSettings.SettingsPath);
    }
    catch (Exception ex) { Console.WriteLine("could not put the settings back: " + ex.Message); }
}
AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreSettings();
AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreSettings();
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
Step("hole labels in two columns", () =>
{
    // each label in the column of its side, in its order; the arrow of a row puts it at the end of the other side
    var p = vm.Project!;
    Control Named(string name) => window.GetLogicalDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible) ?? throw new Exception("no " + name + " on the Visibility tab");
    var right = Named("labelsRight");
    if (!right.GetLogicalDescendants().OfType<Control>().Any(c => c.Name == "labelNumber")) throw new Exception("the hole number should start on the right");
    var move = window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "moveLabelNumber");
    move.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    var (left, _) = HoleLabelLayout.Sides(p.View);
    if (left[^1] != HoleLabel.Number) throw new Exception($"the arrow should move the number to the end of the left side: {string.Join(", ", left)}");
    if (!Named("labelsLeft").GetLogicalDescendants().OfType<Control>().Any(c => c.Name == "labelNumber")) throw new Exception("the left column should show the number after the move");
    vm.ToggleView(HoleLabelLayout.Reset); Pump();
    Named("labelsLeft").BringIntoView(); Pump();
});
Shot("02c_hole_labels");
Shot("02b_visibility_tab");
Step("back to properties", () => window.ShowInspectorTab(0));
Step("layer list on the properties tab", () =>
{
    // shows the layer of the selection; picking another moves the selection there as one undo step
    ComboBox Combo() => window.GetLogicalDescendants().OfType<ComboBox>().FirstOrDefault(c => c.Name == "selectionLayer" && c.IsEffectivelyVisible) ?? throw new Exception("no layer list on the Properties tab");
    var p = vm.Project!;
    var ids = vm.Doc!.Selection.ToHashSet();
    var from = p.Entities.First(e => ids.Contains(e.Id)).LayerId;
    if (Combo().SelectedIndex != p.Layers.FindIndex(l => l.Id == from)) throw new Exception($"the layer list should show the layer of the selection: {Combo().SelectedIndex}");
    var to = p.Layers.First(l => l.Id != from);
    Combo().SelectedIndex = p.Layers.IndexOf(to); Pump();
    if (p.Entities.Where(e => ids.Contains(e.Id)).Any(e => e.LayerId != to.Id)) throw new Exception("picking a layer should move the selection to it");
    if (Combo().SelectedIndex != p.Layers.IndexOf(to)) throw new Exception("the layer list should show the new layer");
    vm.Undo(); Pump();
    if (vm.Project!.Entities.Where(e => ids.Contains(e.Id)).Any(e => e.LayerId != from)) throw new Exception("undo should bring the selection back to its layer");
});
Step("properties of holes: loading on top, fields in folding groups inside the panel", () =>
{
    var selected = vm.Doc!.Selection.ToList();
    var holes = vm.Project!.Holes.Take(3).Select(h => h.Id).ToList();
    vm.Doc.Select(holes.Take(1)); Pump();
    Control Named(string name) => window.GetLogicalDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible) ?? throw new Exception("no " + name + " on the properties tab");
    bool Shows(string name) => window.GetLogicalDescendants().OfType<Control>().Any(c => c.Name == name && c.IsEffectivelyVisible);
    void Press(string name) { Named(name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump(); }
    if (Named("editLoading").TranslatePoint(new Point(), window)!.Value.Y >= Named("selectionLayer").TranslatePoint(new Point(), window)!.Value.Y) throw new Exception("the loading button should be above the layer list");
    Named("holeCharge");
    // every field fits the width of the panel
    var grid = window.GetLogicalDescendants().OfType<PropertyGrid>().First(g => g.IsEffectivelyVisible);
    foreach (var box in grid.GetLogicalDescendants().OfType<TextBox>().Where(b => b.IsEffectivelyVisible))
        if (box.TranslatePoint(new Point(box.Bounds.Width, 0), grid)!.Value.X > grid.Bounds.Width + 0.5) throw new Exception($"{box.Name} runs out of the panel");
    // a group folds and stays folded for the next selection
    if (Shows("propWaterDepth") || !Shows("propLength")) throw new Exception("Measurements should start folded, Geometry open");
    Press("sectionMeasurements");
    if (!Shows("propWaterDepth")) throw new Exception("the group did not open");
    vm.Doc.Select(holes); Pump();
    if (!Shows("propWaterDepth")) throw new Exception("the open group closed with the selection");
    Press("sectionMeasurements");
    if (Shows("propWaterDepth")) throw new Exception("the group did not fold");
    Named("chargeTotal"); Named("chargePerHole");
    // a field in a group edits all the holes, in one undo step
    var length = (TextBox)Named("propLength");
    var was = vm.Project.Holes.First(h => h.Id == holes[0]).Length;
    length.Text = "12.5"; length.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent)); Pump();
    if (vm.Project.Holes.Where(h => holes.Contains(h.Id)).Any(h => h.Length != 12.5)) throw new Exception("the length was not set on the holes");
    Shot("02d_properties_holes");
    vm.Undo(); Pump();
    if (vm.Project!.Holes.First(h => h.Id == holes[0]).Length != was) throw new Exception("undo should bring the length back");
    vm.Doc!.Select(selected); Pump();
});
Step("properties of ties: the delay is taken by Enter and when the focus leaves, and the times follow", () =>
{
    var selected = vm.Doc!.Selection.ToList();
    var p = vm.Project!;
    var ties = p.Ties.Where(t => p.FindEntity(t.ToId) is Hole { FiringTimeMs: not null }).Take(2).ToList();
    var ids = ties.Select(t => t.Id).ToList();
    var hole = ties[0].ToId;
    double? Time() => ((Hole)vm.Project!.FindEntity(hole)!).FiringTimeMs;
    TextBox Delay() => window.GetLogicalDescendants().OfType<TextBox>().First(b => b.Name == "propDelayMs" && b.IsEffectivelyVisible);
    void Enter(TextBox box) => box.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
    void Leave(TextBox box) => box.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent));
    vm.Doc.Select(ids.Take(1)); Pump();
    var was = Time();
    var steps = vm.Doc.UndoNames.Count;
    var box = Delay();
    box.Text = "321"; Enter(box); Pump();
    if (Time() == was) throw new Exception("Enter did not recalculate the times");
    var byEnter = Time();
    if (vm.Doc.UndoNames.Count != steps + 1) throw new Exception("the delay by Enter should be one undo step");
    // the panel was built anew; the box taken away must not make a second step, also with its focus lost
    Leave(box); Pump();
    if (vm.Doc.UndoNames.Count != steps + 1) throw new Exception("the focus lost after Enter made a second undo step");
    box = Delay();
    box.Text = "654"; Leave(box); Pump();
    if (Time() == byEnter) throw new Exception("leaving the field did not recalculate the times");
    while (vm.Doc.UndoNames.Count > steps) vm.Undo();
    Pump();
    // ties with different delays show as mixed: Enter sets them all in one step
    vm.Change("Smoke tie delay", pr => ((TieEntity)pr.FindEntity(ids[0])!).DelayMs = 5);
    vm.Doc.Select(ids); Pump();
    var mixedSteps = vm.Doc.UndoNames.Count;
    box = Delay();
    box.Text = "77"; Enter(box); Pump();
    Leave(box); Pump();
    if (vm.Doc.UndoNames.Count != mixedSteps + 1) throw new Exception("mixed delays by Enter should be one undo step");
    if (ids.Any(id => ((TieEntity)vm.Project!.FindEntity(id)!).DelayMs != 77)) throw new Exception("the delay was not set on all the ties");
    while (vm.Doc.UndoNames.Count > steps) vm.Undo();
    Pump();
    if (Time() != was) throw new Exception("undo should bring the time back");
    vm.Doc.Select(selected); Pump();
});
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
    if (buttons.Any(b => (b.Tag as string) == "shape:2")) throw new Exception("the Auto shape is still offered");
    if (w.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "arrowSize").Text != BlastDesigner.App.Services.Loc.F("{0:0.0} m", 1.2)) throw new Exception("the arrows should start at 1.2 m");
    // one colour first; the gradient colours the arrows by direction and shows its legend
    var arrows = view.OptionsSource!().Overlays.OfType<BlastDesigner.Rendering.FirstMovementOverlay>().First();
    if (arrows.Gradient || buttons.First(b => (b.Tag as string) == "colour:0").Foreground != BlastDesigner.App.Services.Look.Accent) throw new Exception("the arrows should start in one colour");
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "06c_first_movement_one_colour.png"));
    buttons.First(b => (b.Tag as string) == "colour:1").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    if (!arrows.Gradient) throw new Exception("the gradient chip did not switch");
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "06d_first_movement_gradient.png"));
    buttons.First(b => (b.Tag as string) == "colour:0").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
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
    // the arrows follow an initiation point moved with its hole, and the hole moved under its point
    var start = vm.Project.Holes.Where(h => h.FiringTimeMs != null).OrderBy(h => h.FiringTimeMs).First();
    var startId = start.Id;
    vm.Change("Smoke point on the first hole", p => p.Add(new InitiationPoint { Position = start.Collar, HoleId = startId, Label = "IPM" }));
    var ipm = vm.Project.Entities.OfType<InitiationPoint>().First(i => i.Label == "IPM");
    string Arrows() => string.Join(";", view.ProjectSource!() is { } pr ? new BlastDesigner.Rendering.FirstMovementOverlay().Arrows(pr).Select(a => $"{a.Position.X:0.00},{a.Position.Y:0.00},{a.DirX:0.000},{a.DirY:0.000}") : Array.Empty<string>());
    string WindowArrows() => string.Join(";", view.OptionsSource!().Overlays.OfType<BlastDesigner.Rendering.FirstMovementOverlay>().First().Arrows(vm.Project!).Select(a => $"{a.Position.X:0.00},{a.Position.Y:0.00},{a.DirX:0.000},{a.DirY:0.000}"));
    var before = WindowArrows();
    vm.Doc!.Select(new[] { ipm.Id }); vm.MoveSelection(new Vec3(3, 2, 0)); Pump();
    if (((Hole)vm.Project.FindEntity(startId)!).Collar != vm.Project.Entities.OfType<InitiationPoint>().First(i => i.Label == "IPM").Position) throw new Exception("the hole did not follow its initiation point");
    var afterPoint = WindowArrows();
    if (afterPoint == before) throw new Exception("the first-movement arrows were not recalculated after moving the initiation point");
    if (afterPoint != Arrows()) throw new Exception("the window's arrows differ from a fresh calculation after moving the initiation point");
    vm.Doc.Select(new[] { startId }); vm.MoveSelection(new Vec3(-3, -2, 0)); Pump();
    var afterHole = WindowArrows();
    if (afterHole == afterPoint || afterHole != Arrows()) throw new Exception("the first-movement arrows were not recalculated after moving the hole");
    // while the point is dragged its hole moves with it on screen, and the arrows are drawn from where they are
    vm.SelectTool(); Pump();
    ipm = vm.Project.Entities.OfType<InitiationPoint>().First(i => i.Label == "IPM");
    vm.Doc.Select(new[] { ipm.Id });
    var (px, py) = vm.View.Camera.ToScreen(ipm.Position);
    vm.ActiveTool!.PointerPressed(new ToolPointer { X = px, Y = py - 10, World = ipm.Position, Snapped = ipm.Position, Hit = ipm, Left = true, ClickCount = 1 });
    var to = ipm.Position + new Vec3(4, 0, 0);
    vm.ActiveTool.PointerMoved(new ToolPointer { X = px + 60, Y = py - 10, World = to, Snapped = to, Left = true });
    var drag = vm.View.Preview;
    if (drag.Moving == null || !drag.Moving.Contains(startId)) throw new Exception("the hole under the dragged initiation point is not dragged with it");
    var still = string.Join(";", new BlastDesigner.Rendering.FirstMovementOverlay().Arrows(vm.Project).Select(a => $"{a.DirX:0.000},{a.DirY:0.000}"));
    // what the plan draws during the drag: the arrows from the shifted positions
    var shifted = FirstMovement.Arrows(BlastDesigner.Engine.TimingDelaunay.Build(FirstMovement.Inputs(vm.Project, new HashSet<Guid>(drag.Moving), drag.MoveOffset)));
    if (string.Join(";", shifted.Select(a => $"{a.DirX:0.000},{a.DirY:0.000}")) == still) throw new Exception($"the arrows do not change while the point is dragged (offset {drag.MoveOffset})");
    vm.ActiveTool.Cancel(); vm.View.Preview.Clear(); Pump();
    for (int i = 0; i < 10 && vm.Project!.Entities.OfType<InitiationPoint>().Any(x => x.Label == "IPM"); i++) vm.Undo();
    vm.Doc.ClearSelection(); Pump();
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
Step("pattern tool: direction, offset, prefix and start number on its panel, no dialog after the outline", () =>
{
    vm.PatternTool(); Pump();
    TextBox Box(string name) => window.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == name) ?? throw new Exception("no field " + name + " on the pattern panel");
    foreach (var name in new[] { "patternDirection", "patternOffset", "patternPrefix", "patternStart" }) Box(name);
    // all the fields on one line (a narrow window scrolls them instead of wrapping)
    var tops = new[] { "patternDirection", "patternOffset", "patternPrefix", "patternStart" }.Select(n => Box(n).TranslatePoint(new Point(0, 0), window)!.Value.Y).Distinct().ToList();
    if (tops.Count != 1) throw new Exception("the pattern fields are on more than one line");
    var prefix = Box("patternPrefix"); prefix.Text = "S"; prefix.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent));
    var start = Box("patternStart"); start.Text = "700"; start.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent));
    Pump();
    var before = vm.Project!.Holes.Count();
    var dialog = BlastDesigner.App.Services.Dialogs.LastShown;
    var x0 = vm.Project.Holes.Max(h => h.Collar.X) + 30;
    var y0 = vm.Project.Holes.Min(h => h.Collar.Y);
    foreach (var (x, y) in new[] { (x0, y0), (x0 + 20, y0), (x0 + 20, y0 + 16), (x0, y0 + 16) })
    {
        var w = new BlastDesigner.Domain.Geometry.Vec3(x, y, 300);
        vm.ActiveTool!.PointerPressed(new ToolPointer { World = w, Snapped = w, Left = true, ClickCount = 1 });
    }
    vm.ActiveTool!.KeyDown(Avalonia.Input.Key.Enter, Avalonia.Input.KeyModifiers.None);
    for (int i = 0; i < 20; i++) Pump();
    if (BlastDesigner.App.Services.Dialogs.LastShown != dialog && BlastDesigner.App.Services.Dialogs.LastShown?.IsVisible == true) throw new Exception("a dialog opened after the outline: " + BlastDesigner.App.Services.Dialogs.LastShown.Title);
    var added = vm.Project.Holes.Skip(before).ToList();
    if (added.Count == 0) throw new Exception("no holes from the pattern tool");
    if (!added.Any(h => h.Label == "S700") || added.Any(h => !h.Label.StartsWith("S"))) throw new Exception("labels not from the panel: " + string.Join(",", added.Take(5).Select(h => h.Label)));
    if (vm.PatternStartNumber != null) throw new Exception("the start number should return to the next free one");
    vm.Undo();
    vm.SelectTool();
});
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
    string DrawTip(string header) => BlastDesigner.App.Services.Loc.F(BlastDesigner.App.Services.Loc.Ui("Drawing: {0} (others in the list)"), BlastDesigner.App.Services.Loc.T(header).ToLower(System.Globalization.CultureInfo.CurrentCulture)) + " (W)";
    Click(DrawTip("Polyline"), 8);
    {
        var drawButton = buttons.First(x => Avalonia.Controls.ToolTip.GetTip(x) as string == DrawTip("Polyline"));
        drawButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Pump();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "36b_drawing_tools.png"));
        BlastDesigner.App.Views.Ui.LastMenu?.Close(); Pump();
    }
    // every drawing tool of the list has its icon, and the one picked becomes the button's tool (icon, tip, key W)
    if (vm.DrawingTools().Any(e => e.Icon == null)) throw new Exception("a drawing tool without an icon");
    // the point and the north point light up too (their tools were named by the translated kind)
    foreach (var header in new[] { "Point", "North point" })
    {
        vm.DrawingTools().First(e => e.Header == header).Action!(); Pump();
        var bs = window.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().ToList();
        var btn = bs.First(x => Avalonia.Controls.ToolTip.GetTip(x) as string == DrawTip(header));
        if (btn.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault()?.Stroke != BlastDesigner.App.Services.Look.Accent) throw new Exception(header + " on: its icon is not in the accent colour");
    }
    vm.SelectTool(); Pump();
    // holding the left button on the icon opens the list, and the release then starts nothing
    {
        var drawButton = window.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().First(x => (Avalonia.Controls.ToolTip.GetTip(x) as string)?.EndsWith(" (W)") == true && x.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Count() >= 2);
        BlastDesigner.App.Views.Ui.LastMenu?.Close();
        var at = drawButton.TranslatePoint(new Point(12, drawButton.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, Avalonia.Input.MouseButton.Left); Pump();
        ContextMenu? held;
        try
        {
            for (int i = 0; i < 12; i++) { Thread.Sleep(50); Pump(); }
            held = BlastDesigner.App.Views.Ui.LastMenu is { IsOpen: true } m ? m : null;
        }
        finally { window.MouseUp(at, Avalonia.Input.MouseButton.Left); Pump(); }
        if (held == null) throw new Exception("holding the drawing button did not open its list");
        if (vm.ActiveTool is not SelectTool) throw new Exception("the release after a hold started a tool: " + vm.ActiveTool?.Name);
        held.Close(); Pump();
    }
    var order = string.Join(",", vm.DrawingTools().Select(e => e.Header));
    if (order != "Point,Polyline,Polygon,Circle,Arrow,Text,Dimension,North point") throw new Exception("drawing tools order: " + order);
    vm.DrawingTools().First(e => e.Header == "Circle").Action!(); Pump();
    if (vm.ActiveTool?.Name != "Circle" || vm.DrawingTool != "Circle") throw new Exception("picking Circle did not start it");
    buttons = window.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().ToList();
    if (!buttons.Any(x => Avalonia.Controls.ToolTip.GetTip(x) as string == DrawTip("Circle"))) throw new Exception("the drawing button does not show the circle");
    // the active tool's icon in the accent colour
    var circleButton = buttons.First(x => Avalonia.Controls.ToolTip.GetTip(x) as string == DrawTip("Circle"));
    if (circleButton.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault()?.Stroke != BlastDesigner.App.Services.Look.Accent) throw new Exception("the active drawing tool's icon is not in the accent colour");
    vm.SelectTool(); Pump();
    vm.RunDrawingTool();
    if (vm.ActiveTool?.Name != "Circle") throw new Exception("the drawing button / key W should start the circle again");
    vm.DrawingTools().First(e => e.Header == "Polyline").Action!(); vm.SelectTool(); Pump();
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
Step("visualization window: the scrub bar shrinks with the window, no x3, fixed height", () =>
{
    vm.VisualizeTiming(); Pump();
    var w = BlastDesigner.App.Services.Dialogs.LastShown!;
    if (w.Title != BlastDesigner.App.Services.Loc.T("Visualization")) throw new Exception("window title: " + w.Title);
    if (w.MinHeight != w.Height || w.MaxHeight != w.Height || w.MinWidth >= w.Width) throw new Exception($"size limits {w.MinWidth}x{w.MinHeight}-{w.MaxHeight} for {w.Width}x{w.Height}");
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
Step("fragmentation inputs: rock factor A and Protodyakonov f, matched by the blastability table", () =>
{
    var form = new FragmentationForm(vm.Project!);
    var w = new Window { Width = 640, Height = 760, Content = form, Background = BlastDesigner.App.Services.Look.Ground };
    w.Show(); Pump();
    TextBox Box(string name) => form.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(b => b.Name == name) ?? throw new Exception("no field " + name);
    void Type(string name, string text) { var b = Box(name); b.Text = text; b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent)); Pump(); }
    double Shown(string name) => double.Parse(Box(name).Text!.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
    TextBlock Warning(string name) => form.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == name);
    Slider Slide(string name) => form.GetLogicalDescendants().OfType<Slider>().First(x => x.Name == name);
    // f sets the strength; A takes the same share of the range of the A table, rounded: f 15 is 75 % of 0.3…20,
    // 75 % of A 0.6…18 is 13.6, A 14; no formula
    Type("protodyakonovF", "15");
    if (form.Draft.Ucs != 150 || form.Draft.RockFactor != 14 || !form.Draft.RockFactorSet || Shown("rockFactorA") != 14)
        throw new Exception($"f 15 should give σc 150 MPa and A 14 from the table: σc {form.Draft.Ucs}, A {form.Draft.RockFactor}");
    if (form.GetLogicalDescendants().OfType<Button>().Any(b => b.Name == "calcFromF")) throw new Exception("Calculate from f is still offered");
    // A typed in puts the matching f while it is typed (no Enter): A 4 is 20 % of 0.6…18, f 4.1, rounded 4
    Box("rockFactorA").Text = "4"; Pump();
    if (form.Draft.RockFactor != 4 || Math.Abs(form.Draft.Ucs - 40) > 1e-9 || Shown("protodyakonovF") != 4 || Box("rockFactorA").Text != "4")
        throw new Exception($"A 4 typed in should give f 4 at once: σc {form.Draft.Ucs}, f shown {Box("protodyakonovF").Text}, A {Box("rockFactorA").Text}");
    // the rock mass no longer moves A
    var massive = form.Draft.RockFactor;
    var jointedChip = form.GetLogicalDescendants().OfType<Button>().First(b => (b.Tag as string) == "mass:Jointed");
    jointedChip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    if (form.Draft.RockFactor != massive || form.RockFactor != massive) throw new Exception("the rock mass changed A: " + form.RockFactor);
    form.GetLogicalDescendants().OfType<Button>().First(b => (b.Tag as string) == "mass:Massive").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    // the sliders go in steps of 1, one for each value of their table: 0, the MinVal of each row, the last MaxVal; A also
    // the MaxVal of each row and the whole numbers inside the rows (A 0, 0.6, 1, 2, 3 … 18; f 0, 0.3 … 20); moving one
    // puts in the matching value of the other table; each describes its value under it
    var aSlider = Slide("rockFactorASlider");
    var fSlider = Slide("protodyakonovFSlider");
    if (ToolTip.GetTip(aSlider) != null || ToolTip.GetTip(fSlider) != null) throw new Exception("the sliders still have tooltips");
    aSlider.Value = 0; Pump();
    if (form.RockFactor != 0 || !form.Draft.RockFactorSet || form.Draft.Ucs != 0 || fSlider.Value != 0)
        throw new Exception($"the left end should be A 0, f 0: A {form.RockFactor}, f {Box("protodyakonovF").Text}, f slider {fSlider.Value}");
    if (aSlider.Maximum != 19 || fSlider.Maximum != 15 || aSlider.TickFrequency != 1 || fSlider.TickFrequency != 1) throw new Exception($"slider steps: A {aSlider.Maximum}, f {fSlider.Maximum}");
    // a value within its table never leaves the other 0: A 0.6 would round f 0.3 to 0, f takes 0.3 (its lowest)
    aSlider.Value = 1; Pump();
    if (form.RockFactor != 0.6 || Math.Abs(form.Draft.Ucs - 3) > 1e-9) throw new Exception($"A 0.6 should give f 0.3, not 0: f {form.Draft.Ucs / 10}");
    // below its table the other one stays 0 by the rounding: A 0.4 gives f 0
    Box("rockFactorA").Text = "0,4"; Pump();
    if (form.Draft.Ucs != 0) throw new Exception($"A 0.4 should give f 0: f {form.Draft.Ucs / 10}");
    fSlider.Value = 5; Pump();
    if (Math.Abs(form.Draft.Ucs - 10) > 1e-9 || form.RockFactor != 1 || aSlider.Value != 2) throw new Exception($"f step 5 should be f 1 (VII), A 1.2 rounded 1: f {form.Draft.Ucs / 10}, A {form.RockFactor}");
    aSlider.Value = 2; Pump();
    if (form.RockFactor != 1) throw new Exception($"A step 2 should be A 1 (inside soft rock 0.6–2): {form.RockFactor}");
    aSlider.Value = 4; Pump();
    if (Math.Abs(form.Draft.Ucs - 30) > 1e-9 || form.RockFactor != 3 || fSlider.Value != 8) throw new Exception($"A step 4 should be A 3, f 3.0 rounded 3: f {form.Draft.Ucs / 10}, A {form.RockFactor}, f slider {fSlider.Value}");
    // a value typed puts the slider at the last step not above it, 0 below the first MinVal
    Box("rockFactorA").Text = "0,4"; Pump();
    if (aSlider.Value != 0) throw new Exception($"A 0.4 should put the slider at 0: {aSlider.Value}");
    Box("rockFactorA").Text = "15,5"; Pump();
    if (aSlider.Value != 16) throw new Exception($"A 15.5 should put the slider at 15: {aSlider.Value}");
    aSlider.Value = aSlider.Maximum; Pump();
    if (form.RockFactor != 18) throw new Exception($"A last step should be A 18: {form.RockFactor}");
    // the ends match each other, and f → A → f comes back: f 15 gives A 14, A 14 gives f 15
    if (form.Draft.Ucs != 200) throw new Exception($"A 18 should give f 20: {form.Draft.Ucs / 10}");
    Type("protodyakonovF", "15");
    if (form.RockFactor != 14) throw new Exception($"f 15 should give A 14: {form.RockFactor}");
    Type("rockFactorA", "13");
    Type("rockFactorA", "14");
    if (form.Draft.Ucs != 150) throw new Exception($"A 14 should give f 15 back: {form.Draft.Ucs / 10}");
    // A between the ranges of the table (7…8) says between which rock types it is
    Box("rockFactorA").Text = "7,5"; Pump();
    var between = form.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "rockFactorACategory").Text ?? "";
    if (!between.Contains(BlastDesigner.App.Services.Loc.Ui("Medium strength")) || !between.Contains(BlastDesigner.App.Services.Loc.Ui("Strong rock")))
        throw new Exception("A 7.5 should read between medium strength and strong rock: " + between);
    fSlider.Value = fSlider.Maximum; Pump();
    if (form.Draft.Ucs != 200 || form.Draft.RockFactor != 18 || aSlider.Value != aSlider.Maximum) throw new Exception($"the f slider end should be f 20 (I), A 18: A {form.Draft.RockFactor}, slider {aSlider.Value}");
    // under f the Protodyakonov category, under A the rock type
    string Described(string name) => form.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == name).Text ?? "";
    if (!Described("protodyakonovFCategory").StartsWith(BlastDesigner.App.Services.Loc.Ui("I (highest strength)"))
        || !Described("rockFactorACategory").Contains(BlastDesigner.App.Services.Loc.Ui("Extremely strong rock")))
        throw new Exception("f 20 should read category I and extremely strong rock: " + Described("protodyakonovFCategory") + " | " + Described("rockFactorACategory"));
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "31_fragmentation_inputs.png"));
    // hard limits cut an entry to 0…50 (f) and 0…25 (A); outside the range of the table (f 0.3…20, A 0.6…18) the field is flagged
    Type("protodyakonovF", "60");
    if (form.Draft.Ucs != 500 || Box("protodyakonovF").Text != "50" || !Warning("protodyakonovFWarning").IsVisible) throw new Exception($"f 60 should be cut to 50 and said so: σc {form.Draft.Ucs}, shown {Box("protodyakonovF").Text}");
    Type("protodyakonovF", "0,2");
    if (Math.Abs(form.Draft.Ucs - 2) > 1e-9 || !Warning("protodyakonovFWarning").IsVisible) throw new Exception("f 0.2 should be taken and flagged");
    Type("protodyakonovF", "15");
    if (Warning("protodyakonovFWarning").IsVisible) throw new Exception("f 15 flagged");
    Type("rockFactorA", "30");
    if (form.Draft.RockFactor != 25 || !Warning("rockFactorAWarning").IsVisible) throw new Exception($"A 30 should be cut to 25: {form.Draft.RockFactor}");
    Type("rockFactorA", "0,5");
    if (form.Draft.RockFactor != 0.5 || !Warning("rockFactorAWarning").IsVisible) throw new Exception("A 0.5 should be taken and flagged");
    Type("rockFactorA", "0,6");
    if (Warning("rockFactorAWarning").IsVisible) throw new Exception("A 0.6, the lowest A of the table, flagged");
    Type("rockFactorA", "20");
    if (!Warning("rockFactorAWarning").IsVisible) throw new Exception("A 20, above the table, not flagged");
    Type("rockFactorA", "6");
    if (Warning("rockFactorAWarning").IsVisible) throw new Exception("A 6 flagged");
    // the jointed mass shows its joint factor, the others hide it
    var jointed = form.GetLogicalDescendants().OfType<Button>().First(b => (b.Tag as string) == "mass:Jointed");
    jointed.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    if (!Box("jointFactor").IsEffectivelyVisible || form.Draft.RockMassDescription != 0) throw new Exception("jointed mass: the joint factor field should show, RMD 0");
    // a preset keeps the inputs and brings them back into another window
    // the sizes are typed in mm here: 800 mm is 80 cm
    form.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "sizeUnitMm").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    Type("protodyakonovF", "7"); Type("oversize", "800");
    var preset = Path.Combine(outDir, "smoke" + BlastDesigner.IO.FragmentationPreset.Extension);
    if (form.GetLogicalDescendants().OfType<Button>().Count(b => b.Name is "savePreset" or "loadPreset") != 2) throw new Exception("no preset buttons in the window");
    form.SavePreset(preset);
    w.Close(); Pump();
    var again = new FragmentationForm(vm.Project!);
    var w2 = new Window { Width = 640, Height = 760, Content = again, Background = BlastDesigner.App.Services.Look.Ground };
    w2.Show(); Pump();
    again.LoadPreset(preset); Pump();
    if (again.Draft.Ucs != 70 || again.Draft.OversizeCm != 80 || again.Draft.RockMassDescription != 0 || again.Draft.RockFactor != 7 || !again.Draft.RockFactorSet)
        throw new Exception($"preset not loaded: σc {again.Draft.Ucs}, oversize {again.Draft.OversizeCm}, RMD {again.Draft.RockMassDescription}, A {again.Draft.RockFactor}");
    if (again.GetLogicalDescendants().OfType<TextBox>().First(b => b.Name == "protodyakonovF").Text != "7") throw new Exception("the loaded preset is not shown");
    w2.Close(); Pump();
});
Step("fragmentation inputs: block sizes in mm or cm, the switch kept in the preset", () =>
{
    var form = new FragmentationForm(vm.Project!);
    var w = new Window { Width = 640, Height = 820, Content = form, Background = BlastDesigner.App.Services.Look.Ground };
    w.Show(); Pump();
    TextBox Box(string name) => form.GetLogicalDescendants().OfType<TextBox>().First(b => b.Name == name);
    void Type(string name, string text) { var b = Box(name); b.Text = text; b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent)); Pump(); }
    void Unit(string unit) { form.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "sizeUnit" + unit).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump(); }
    string UnitText(string name) => (Box(name).InnerRightContent as TextBlock)?.Text ?? "";
    var mm = BlastDesigner.App.Services.Loc.Ui("mm");
    var cm = BlastDesigner.App.Services.Loc.Ui("cm");
    // the group titles are blue
    var title = form.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == BlastDesigner.App.Services.Loc.Ui("Size distribution").ToUpper(System.Globalization.CultureInfo.CurrentCulture));
    if (title.Foreground != BlastDesigner.App.Services.Look.Blue) throw new Exception("the group title is not blue");
    Unit("Mm");
    Type("maxBlock", "1500");
    if (Math.Abs(form.Draft.MaxBlockCm - 150) > 1e-9 || UnitText("maxBlock") != mm || UnitText("fines") != mm) throw new Exception($"1500 mm should be 150 cm: {form.Draft.MaxBlockCm}, unit {UnitText("maxBlock")}");
    // switching the unit converts what is shown, the sizes stay the same
    Unit("Cm");
    if (Box("maxBlock").Text != "150" || form.Draft.MaxBlockCm != 150 || form.Draft.SizeUnit != SizeUnit.Cm || UnitText("oversize") != cm)
        throw new Exception($"in cm the largest block should show 150: {Box("maxBlock").Text}, {form.Draft.MaxBlockCm} cm, unit {form.Draft.SizeUnit}");
    var x50 = form.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "preview0");
    if (x50 != null && !x50.Text!.EndsWith(cm)) throw new Exception("X50 not in cm: " + x50.Text);
    Unit("Mm");
    if (Box("maxBlock").Text != "1500") throw new Exception("back in mm the largest block should show 1500: " + Box("maxBlock").Text);
    if (x50 != null && !form.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "preview0").Text!.EndsWith(mm)) throw new Exception("X50 not in mm");
    form.GetLogicalDescendants().OfType<ScrollViewer>().First().Offset = new Vector(0, 10000);
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "17b_fragmentation_size_units.png"));
    // the preset keeps the unit and puts it back over the one of the window
    Unit("Cm");
    var preset = Path.Combine(outDir, "smoke-cm" + BlastDesigner.IO.FragmentationPreset.Extension);
    form.SavePreset(preset);
    Unit("Mm");
    form.LoadPreset(preset); Pump();
    if (form.Draft.SizeUnit != SizeUnit.Cm || Box("maxBlock").Text != "150") throw new Exception($"the preset unit not applied: {form.Draft.SizeUnit}, {Box("maxBlock").Text}");
    w.Close(); Pump();
});
Step("exceptions window: the buttons take the rows chosen in the table, with none chosen the whole list", () =>
{
    var steps = vm.Doc!.UndoNames.Count;
    var selected = vm.Doc.Selection.ToList();
    var marked = vm.Project!.Holes.Where(h => !h.IsDummy).Take(3).Select(h => h.Id).ToList();
    vm.Change("Smoke exceptions", p => { foreach (var id in marked) ((Hole)p.FindEntity(id)!).Exceptions = HoleException.Wet; });
    BlastDesigner.App.Views.AnalysisWindows.Exceptions(vm); Pump();
    var w = BlastDesigner.App.Services.Dialogs.LastShown!;
    var table = w.GetLogicalDescendants().OfType<DataGrid>().First();
    var items = table.ItemsSource!.Cast<string[]>().ToList();
    void Press(string text) { w.GetLogicalDescendants().OfType<Button>().First(b => b.Content as string == BlastDesigner.App.Services.Loc.T(text)).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump(); }
    if (items.Count < 3) throw new Exception($"{items.Count} rows in the exceptions table");
    // the hole numbers are text in the table but sort by value, on opening and by the column
    var natural = items.Select(i => i[0]).OrderBy(l => l, BlastDesigner.Domain.NaturalOrder.Instance).ToList();
    if (!items.Select(i => i[0]).SequenceEqual(natural)) throw new Exception("the table does not open in the order of the numbers");
    table.Columns[0].Sort(System.ComponentModel.ListSortDirection.Descending); Pump();
    var sorted = table.CollectionView!.Cast<string[]>().Select(i => i[0]).ToList();
    if (!sorted.SequenceEqual(Enumerable.Reverse(natural))) throw new Exception("the column does not sort by number: " + string.Join(" ", sorted.Take(12)));
    table.Columns[0].Sort(System.ComponentModel.ListSortDirection.Ascending); Pump();
    // sorting leaves the first row chosen, in sight in the table
    table.SelectedItems.Clear(); Pump();
    Press("Highlight");
    if (vm.Highlight.Ids.Count != items.Count) throw new Exception($"with no row chosen the whole list should be highlighted: {vm.Highlight.Ids.Count} of {items.Count}");
    // the search keeps the rows whose hole has the text, whatever its case; the buttons then take those shown
    var search = w.GetLogicalDescendants().OfType<TextBox>().First(t => t.Name == "exceptionsSearch");
    var label = items[1][0];
    search.Text = label.ToLowerInvariant(); Pump();
    var found = table.CollectionView!.Cast<string[]>().ToList();
    if (found.Count == 0 || found.Count >= items.Count || found.Any(r => !r[0].Contains(label, StringComparison.OrdinalIgnoreCase))) throw new Exception($"searching {label}: {found.Count} rows of {items.Count}");
    Press("Highlight");
    if (vm.Highlight.Ids.Count != found.Count) throw new Exception($"with a search the rows shown should be highlighted: {vm.Highlight.Ids.Count} of {found.Count}");
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "40_exceptions_search.png"));
    search.Text = ""; Pump();
    if (table.CollectionView!.Cast<string[]>().Count() != items.Count) throw new Exception("clearing the search should bring all the rows back");
    table.SelectedItems.Add(items[0]); table.SelectedItems.Add(items[2]); Pump();
    Press("Highlight");
    if (vm.Highlight.Ids.Count != 2) throw new Exception($"{vm.Highlight.Ids.Count} holes highlighted instead of the 2 rows chosen");
    Press("Select these holes");
    var wanted = new[] { items[0][0], items[2][0] };
    if (vm.Doc!.SelectedHoles.Count() != 2 || vm.Doc.SelectedHoles.Any(h => !wanted.Contains(h.Label))) throw new Exception($"{vm.Doc.SelectedHoles.Count()} holes selected instead of the 2 rows chosen");
    w.Close(); vm.ClearOverlays(); Pump();
    while (vm.Doc.UndoNames.Count > steps) vm.Undo();
    vm.Doc!.Select(selected); Pump();
});
Step("exceptions window: Highlight rings the holes, Clear analysis on the plan removes them", () =>
{
    vm.ClearOverlays(); Pump();
    BlastDesigner.App.Views.AnalysisWindows.Exceptions(vm); Pump();
    var w = BlastDesigner.App.Services.Dialogs.LastShown!;
    var highlight = w.GetLogicalDescendants().OfType<Button>().First(b => b.Content as string == BlastDesigner.App.Services.Loc.T("Highlight"));
    highlight.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    var clearText = "✕ " + BlastDesigner.App.Services.Loc.Ui("Clear analysis");
    Button? Clear() => window.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == clearText || (b.Content as TextBlock)?.Text == clearText);
    // the sample may have no exceptions: then a highlight from another check window
    if (vm.Highlight.Ids.Count == 0) { Console.WriteLine("     (no exceptions in the sample: one hole highlighted directly)"); vm.SetHighlight(new[] { vm.Project!.Holes.First().Id }); Pump(); }
    var clear = Clear() ?? throw new Exception("no Clear analysis button on the plan while holes are highlighted");
    clear.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    if (vm.Highlight.Ids.Count > 0) throw new Exception("Clear analysis left the highlight");
    if (Clear() != null) throw new Exception("Clear analysis still shown after clearing");
    w.Close(); Pump();
});
Step("visualization window: the chosen speed, interval and switches come back next time, also in another project", () =>
{
    vm.VisualizeTiming(); Pump();
    var w = BlastDesigner.App.Services.Dialogs.LastShown!;
    void Chip(string tag) => w.GetLogicalDescendants().OfType<Button>().First(b => (b.Tag as string) == tag).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    TextBox Seconds() => w.GetLogicalDescendants().OfType<TextBox>().First(t => t.Name == "playbackSeconds");
    foreach (var tag in new[] { "playback:0", "playback:2", "playback:5", "playback:10", "playback:15" }) w.GetLogicalDescendants().OfType<Button>().First(b => (b.Tag as string) == tag);
    // the height is fixed and fits the rows, at the smallest width too
    if (w.MinHeight != w.MaxHeight) throw new Exception("the height of the window should be fixed");
    var fullWidth = w.Width;
    w.Width = w.MinWidth; Pump();
    var rows = w.GetLogicalDescendants().OfType<WrapPanel>().First();
    var bottom = rows.TranslatePoint(new Point(0, rows.Bounds.Height), w)!.Value.Y;
    if (bottom > w.Bounds.Height - 4 || rows.Bounds.Height > 32) throw new Exception($"the last row ends at {bottom:0} of {w.Bounds.Height:0}, {rows.Bounds.Height:0} high: it wraps or is cut");
    w.Width = fullWidth; Pump();
    // round buttons: Play turns into Pause while the blast runs, Stop ends it
    Button Named(string name) => w.GetLogicalDescendants().OfType<Button>().First(b => b.Name == name);
    if (Named("play").Bounds.Width != Named("play").Bounds.Height || Named("play").CornerRadius.TopLeft * 2 != Named("play").Bounds.Width) throw new Exception("the Play button is not round");
    void Tap(string name) { Named(name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump(); }
    string? Tip() => ToolTip.GetTip(Named("play")) as string;
    Tap("play");
    if (Tip() != BlastDesigner.App.Services.Loc.T("Pause")) throw new Exception("Play should turn into Pause: " + Tip());
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "30d_visualization_playing.png"));
    Tap("stop");
    if (Tip() != BlastDesigner.App.Services.Loc.T("Play")) throw new Exception("Stop should bring Play back: " + Tip());
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "30c_visualization_speed.png"));
    // a number of its own is taken by Enter and by leaving the field; a chip puts its number in the field
    Seconds().Text = "7";
    Seconds().RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
    if (vm.Settings.PlaybackSeconds != 7) throw new Exception("the seconds typed in were not taken: " + vm.Settings.PlaybackSeconds);
    Chip("playback:5");
    if (Seconds().Text != "5") throw new Exception("the chip did not put its seconds in the field: " + Seconds().Text);
    w.GetLogicalDescendants().OfType<NumericUpDown>().First().Value = 40;
    foreach (var c in w.GetLogicalDescendants().OfType<CheckBox>()) c.IsChecked = false;
    Pump();
    // in the settings file at once, with the window still open
    var saved = BlastDesigner.IO.ProgramSettings.Load();
    BlastDesigner.App.Services.Dialogs.CloseWindow("Visualization"); vm.ClearOverlays(); Pump();
    if (saved.PlaybackSeconds != 5 || saved.IsochroneIntervalMs != 40 || saved.ShowIsochrones || saved.ColourHolesByTime)
        throw new Exception($"not saved: seconds {saved.PlaybackSeconds}, interval {saved.IsochroneIntervalMs}, isochrones {saved.ShowIsochrones}, colour {saved.ColourHolesByTime}");
    vm.VisualizeTiming(); Pump();
    w = BlastDesigner.App.Services.Dialogs.LastShown!;
    if (Seconds().Text != "5"
        || w.GetLogicalDescendants().OfType<NumericUpDown>().First().Value != 40
        || w.GetLogicalDescendants().OfType<CheckBox>().Any(c => c.IsChecked == true))
        throw new Exception("the window did not open with the values chosen last time");
    // back to the defaults for the other steps
    Chip("playback:0");
    if (Seconds().Text != "" || vm.Settings.PlaybackSeconds != 0) throw new Exception("Natural should clear the seconds");
    w.GetLogicalDescendants().OfType<NumericUpDown>().First().Value = 25;
    foreach (var c in w.GetLogicalDescendants().OfType<CheckBox>()) c.IsChecked = true;
    Pump();
    // a thin title of its own instead of the system one; its cross closes the window
    if (!w.ExtendClientAreaToDecorationsHint || w.GetLogicalDescendants().OfType<DockPanel>().First(d => d.Name == "titleBar").Bounds.Height != 22) throw new Exception("no thin title bar");
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "30e_visualization_title.png"));
    Tap("titleClose");
    if (w.IsVisible) throw new Exception("the cross of the title did not close the window");
    vm.ClearOverlays(); Pump();
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
    // the minimum stemming typed in its check goes to the project (and to settings.json, kept intact here)
    var savedSettings = File.Exists(ProgramSettings.SettingsPath) ? File.ReadAllBytes(ProgramSettings.SettingsPath) : null;
    var stemBefore = vm.Project.MinStemmingDiameters;
    void Stemming(string text)
    {
        var box = dlg.GetLogicalDescendants().OfType<TextBox>().First(t => t.Name == "minStemming");
        box.Text = text;
        box.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent)); Pump();
    }
    Stemming("30");
    if (vm.Project.MinStemmingDiameters != 30 || Charging.MinStemmingDiameters != 30) throw new Exception($"the minimum stemming should go to the project: {vm.Project.MinStemmingDiameters}");

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
    if (rule.MinStemmingDiameters != 30) throw new Exception($"the rule should keep the minimum stemming: {rule.MinStemmingDiameters}");
    // loading by the rule puts its minimum stemming back over the current one
    Stemming("20");
    var byRule = dlg.GetLogicalDescendants().OfType<Avalonia.Controls.Button>()
        .First(b => b.GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>().Any(t => t.Text == BlastDesigner.App.Services.Loc.T("Load by rule")));
    Click(byRule);
    if (vm.Project.MinStemmingDiameters != 30 || Charging.MinStemmingDiameters != 30) throw new Exception($"loading by the rule should set its minimum stemming: {vm.Project.MinStemmingDiameters}");
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
    vm.Change("Smoke stemming back", p => p.MinStemmingDiameters = stemBefore);
    if (savedSettings != null) File.WriteAllBytes(ProgramSettings.SettingsPath, savedSettings); else File.Delete(ProgramSettings.SettingsPath);
    vm.Settings.MinStemmingDiameters = 25;
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
Step("resources: the last row scrolls clear of the buttons under the list", () =>
{
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    Pump();
    var sv = dlg.GetLogicalDescendants().OfType<ScrollViewer>().First(s => s.GetLogicalDescendants().OfType<Button>().Any(b => b.Tag is Product));
    // what the list scrolls over is all of it: the bottom of the last row lies within the height it scrolls
    var content = (Control)sv.Content!;
    var last = sv.GetLogicalDescendants().OfType<Button>().Last(b => b.Tag is Product);
    var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), content)!.Value.Y;
    if (bottom > sv.Extent.Height + 0.5) throw new Exception($"the last row ends {bottom - sv.Extent.Height:0} px past the end of the scrolled list (extent {sv.Extent.Height:0})");
});
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
    Click(dlg.GetLogicalDescendants().OfType<Button>().First(b => b.Tag as string == "confirmDelete"));
    // a deletion is logical: the product stays, hidden from the list, and comes back
    if (ed.Products.Count != count || !used.Deleted || ed.Visible.Contains(used)) throw new Exception("confirmed delete should hide the product and keep it");
    var showDeleted = dlg.GetLogicalDescendants().OfType<CheckBox>().First(c => c.Name == "showDeleted");
    showDeleted.IsChecked = true; Pump();
    if (!ed.Visible.Contains(used)) throw new Exception("Show deleted should list the deleted product");
    Click(dlg.GetLogicalDescendants().OfType<Button>().First(b => b.Tag == used));
    Click(dlg.GetLogicalDescendants().OfType<Button>().First(b => b.Tag as string == "restore"));
    if (used.Deleted) throw new Exception("Restore did not bring the product back");
    showDeleted.IsChecked = false; Pump();
    ed.AddProduct(ProductKind.Booster); Pump();
    if (ed.Selected?.Kind != ProductKind.Booster || ed.ChangedCount != 1) throw new Exception($"add: {ed.Selected?.Kind}, changed {ed.ChangedCount}");
    dlg.Close();
});
Step("resources: the tables add, edit, delete with their children and restore the parent only; applied they reach the project and the user's file", () =>
{
    var steps = vm.Doc!.UndoNames.Count;
    var open = vm.EditResources(); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    var ed = dlg.GetLogicalDescendants().OfType<ProductsEditor>().First();
    var te = dlg.GetLogicalDescendants().OfType<CatalogueEditor>().First();
    void Click(Button b) { b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump(); }
    Button ByTag(string tag) => dlg.GetLogicalDescendants().OfType<Button>().First(b => b.Tag as string == tag);
    Button ByName(string name) => dlg.GetLogicalDescendants().OfType<Button>().First(b => b.Name == name);
    void Rename(string text) { var box = dlg.GetLogicalDescendants().OfType<TextBox>().First(t => t.Name == "tableName" && t.IsEffectivelyVisible); box.Text = text; box.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent)); Pump(); }
    // a record added just now and tied to nothing goes for good, without a trace
    var productsBefore = ed.Products.Count;
    ed.AddProduct(ProductKind.Booster); Pump();
    var deleteProduct = dlg.GetLogicalDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && ToolTip.GetTip(b) is string t && t.StartsWith(BlastDesigner.App.Services.Loc.Ui("Delete")));
    Click(deleteProduct);
    if (ed.Products.Count != productsBefore) throw new Exception("a new product should go for good");
    Click(ByTag("section:Groups"));
    var groupsBefore = ed.Tables.Groups.Count;
    Click(ByName("tableAdd"));
    Click(ByName("tableDelete"));
    if (ed.Tables.Groups.Count != groupsBefore) throw new Exception("a new group should go for good");
    // a new kind with a behaviour, a product of it and a family
    Click(ByTag("section:Kinds"));
    if (!te.IsVisible) throw new Exception("the Kinds section did not open");
    Click(ByName("tableAdd"));
    Rename("Smoke kind");
    var kind = ed.Tables.Kinds.First(k => k.Name == "Smoke kind");
    dlg.GetLogicalDescendants().OfType<ComboBox>().First(c => c.Name == "tableBehaviour").SelectedIndex = Array.IndexOf(Enum.GetValues<ProductKind>(), ProductKind.Booster); Pump();
    if (kind.Behaviour != ProductKind.Booster) throw new Exception("the behaviour of the kind was not set");
    // the list of behaviours says what they do: it is not the table of kinds
    var behaviours = dlg.GetLogicalDescendants().OfType<ComboBox>().First(c => c.Name == "tableBehaviour").ItemsSource!.Cast<string>().ToList();
    if (behaviours.Distinct().Count() != Enum.GetValues<ProductKind>().Length || behaviours.Any(b => ed.Tables.Kinds.Any(k => BlastDesigner.App.Services.Loc.T(k.Name) == b))) throw new Exception("the behaviours repeat the kinds: " + string.Join("; ", behaviours));
    // under the list, what the chosen behaviour is for; it follows the list
    TextBlock Note() => dlg.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "tableBehaviourNote");
    var boosterNote = Note().Text;
    if (string.IsNullOrEmpty(boosterNote)) throw new Exception("no note under the behaviour");
    var combo = dlg.GetLogicalDescendants().OfType<ComboBox>().First(c => c.Name == "tableBehaviour");
    var notes = new HashSet<string>();
    for (int i = 0; i < behaviours.Count; i++) { combo.SelectedIndex = i; Pump(); notes.Add(Note().Text!); }
    if (notes.Count != behaviours.Count) throw new Exception($"{notes.Count} notes for {behaviours.Count} behaviours");
    if (BlastDesigner.App.Services.Loc.Language == "ru" && notes.Any(n => n.Any(c => c is >= 'a' and <= 'z'))) throw new Exception("a note is not translated");
    combo.SelectedIndex = Array.IndexOf(Enum.GetValues<ProductKind>(), ProductKind.Booster); Pump();
    if (Note().Text != boosterNote) throw new Exception("the note did not follow the list");
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    dlg.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "39i_resources_behaviour_note.png"));
    // a built-in name shows in the language of the program
    Click(ByTag("row:" + ed.Tables.Kinds[0].Id));
    var shownName = dlg.GetLogicalDescendants().OfType<TextBox>().First(t => t.Name == "tableName" && t.IsEffectivelyVisible);
    if (shownName.Text != BlastDesigner.App.Services.Loc.T(ed.Tables.Kinds[0].Name)) throw new Exception("the name field should show the translated name: " + shownName.Text);
    shownName.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent)); Pump();
    if (ed.Tables.Kinds[0].Name != "Bulk explosive") throw new Exception("leaving the field unchanged should keep the stored name");
    Click(ByTag("row:" + kind.Id));
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    dlg.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "39f_resources_kinds.png"));
    Click(ByTag("section:Products"));
    ed.AddProduct(kind); Pump();
    var product = ed.Selected!;
    if (product.KindId != kind.Id || product.Kind != ProductKind.Booster) throw new Exception("the product did not take the kind and its behaviour");
    // a kind in no group: its products have a chip of their own
    var looseChip = dlg.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.Content is string s && s.StartsWith(BlastDesigner.App.Services.Loc.Ui("<unassigned>"))) ?? throw new Exception("no chip of the unassigned products");
    Click(looseChip);
    if (ed.Visible.Count != 1 || ed.Visible[0] != product) throw new Exception($"the unassigned chip shows {ed.Visible.Count} products");
    Click(dlg.GetLogicalDescendants().OfType<Button>().First(b => b.Content is string s && s.StartsWith(BlastDesigner.App.Services.Loc.F("All {0}", ""))));
    Click(ByTag("section:Families"));
    Click(ByName("tableAdd"));
    Rename("Smoke family");
    var family = ed.Tables.Families.First(f => f.Name == "Smoke family");
    family.KindId = kind.Id;
    product.FamilyId = family.Id;
    // a group and a type with the kind
    Click(ByTag("section:Groups"));
    Click(ByName("tableAdd"));
    Rename("Smoke group");
    var group = ed.Tables.Groups.First(g => g.Name == "Smoke group");
    // the row of the unassigned kinds is worked out: it lists the kind and takes no deletion
    Click(ByTag("row:unassigned"));
    if (ByName("tableDelete").IsEnabled) throw new Exception("the unassigned row should not be deleted");
    if (!dlg.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Smoke kind" && t.IsEffectivelyVisible)) throw new Exception("the unassigned row does not list the kind");
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    dlg.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "39h_resources_unassigned.png"));
    Click(ByTag("row:" + group.Id));
    dlg.GetLogicalDescendants().OfType<CheckBox>().First(c => c.Tag as string == "kind:" + kind.Id).IsChecked = true; Pump();
    if (!group.KindIds.Contains(kind.Id)) throw new Exception("the group did not take the kind");
    Click(ByTag("section:Types"));
    Click(ByName("tableAdd"));
    Rename("Smoke type");
    dlg.GetLogicalDescendants().OfType<CheckBox>().First(c => c.Tag as string == "kind:" + kind.Id).IsChecked = true; Pump();
    var type = ed.Tables.Types.First(t => t.Name == "Smoke type");
    // deleting the type warns of what goes with it, and takes it
    Click(ByName("tableDelete"));
    if (type.Deleted) throw new Exception("a type with children should ask first");
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    dlg.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "39g_resources_delete_type.png"));
    Click(ByTag("confirmTableDelete"));
    if (!type.Deleted || !kind.Deleted || !family.Deleted || !product.Deleted) throw new Exception($"the children did not go: {type.Deleted} {kind.Deleted} {family.Deleted} {product.Deleted}");
    if (!ed.Tables.Types.Contains(type)) throw new Exception("a new type tied to a kind is deleted logically");
    if (group.Deleted || group.KindIds.Contains(kind.Id)) throw new Exception("the group should stay and lose the kind");
    // deleted records are hidden, and restoring brings back the parent only
    if (te.Selected == type) throw new Exception("the deleted type stayed in the list");
    dlg.GetLogicalDescendants().OfType<CheckBox>().First(c => c.Name == "tableShowDeleted").IsChecked = true; Pump();
    Click(ByTag("row:" + type.Id));
    Click(ByName("tableRestore"));
    if (type.Deleted || !kind.Deleted || !product.Deleted) throw new Exception("restoring should bring back the type only");
    dlg.GetLogicalDescendants().OfType<CheckBox>().First(c => c.Name == "tableShowDeleted").IsChecked = false; Pump();
    Click(ByTag("section:Products"));
    Click(ByTag("apply"));
    if (!open.IsCompleted) throw new Exception("the window did not close");
    if (vm.Doc!.UndoNames.Count != steps + 1) throw new Exception("the changes should be one undo step");
    if (!vm.Project!.Catalogue.Kinds.Any(k => k.Id == kind.Id && k.Deleted) || !vm.Project.Catalogue.Types.Any(t => t.Id == type.Id)) throw new Exception("the tables did not reach the project");
    var mine = BlastDesigner.IO.UserCatalogue.Load();
    if (!mine.Tables.Types.Any(t => t.Id == type.Id) || !mine.Tables.Kinds.Any(k => k.Id == kind.Id && k.Deleted) || !mine.Products.Any(p => p.Id == product.Id && p.Deleted))
        throw new Exception("the records did not reach the user's file");
    // a deleted product is not offered, the types of the catalogue follow the table
    var grid = new PropertyGrid();
    vm.AddProductChoices(grid);
    if (grid.Choices["ProductId"].Any(c => c.Id == product.Id)) throw new Exception("a deleted product is offered");
    vm.Undo(); Pump();
    File.Delete(BlastDesigner.IO.UserCatalogue.FilePath);
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
    // the frame rate is a setting, off unless switched on
    if (!dlg.GetLogicalDescendants().OfType<ToggleSwitch>().Any(t => t.Name == "showFps") || new ProgramSettings().ShowFps) throw new Exception("frame rate setting missing or on by default");
    // surface wireframe: four choices, hidden while the camera moves by default
    var wireChips = dlg.GetLogicalDescendants().OfType<Button>().Where(b => (b.Tag as string)?.StartsWith("wireframe:") == true).ToList();
    if (wireChips.Count != 4 || new ProgramSettings().SurfaceWireframe != ProgramSettings.WireframeWhileStill) throw new Exception("surface wireframe setting missing or not hidden while moving by default");
    var heading = dlg.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == BlastDesigner.App.Services.Loc.Ui("Surface colouring"));
    var sv = dlg.GetLogicalDescendants().OfType<ScrollViewer>().First(s => s.Content is StackPanel);
    sv.Offset = new Vector(0, heading.TranslatePoint(new Point(0, 0), (Visual)sv.Content!)!.Value.Y - 20); Pump();
});
WindowShot("41c_settings_surfaces");
Step("settings: Apply applies and keeps the window, OK applies and closes, buttons Apply, Cancel, OK", () =>
{
    var applied = 0;
    var task = SettingsWindow.Show(new ProgramSettings { Language = settings.Language }, _ => applied++); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    var buttons = dlg.GetLogicalDescendants().OfType<Button>().Where(b => b.Tag is "apply" or "ok" || b.IsCancel).ToList();
    var order = buttons.OrderBy(b => b.TranslatePoint(new Point(0, 0), dlg)!.Value.X).Select(b => b.Tag as string ?? "cancel").ToList();
    if (string.Join(",", order) != "apply,cancel,ok") throw new Exception("button order: " + string.Join(",", order));
    buttons.First(b => b.Tag is "apply").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    if (applied != 1 || !dlg.IsVisible || task.IsCompleted) throw new Exception("Apply did not apply or closed the window");
    buttons.First(b => b.Tag is "ok").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    if (applied != 2 || !task.IsCompleted || task.Result == null) throw new Exception("OK did not apply and close");
});
Step("settings: the colour swatch opens a colour picker that sets the colour", () =>
{
    _ = SettingsWindow.Show(new ProgramSettings { Language = settings.Language }); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    var button = dlg.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "surfaceBenchPicker") ?? throw new Exception("the bench colour swatch is not a button");
    var flyout = button.Flyout as Flyout ?? throw new Exception("no colour picker on the swatch");
    button.BringIntoView(); Pump();
    flyout.ShowAt(button); Pump();
    var view = flyout.Content as Avalonia.Controls.ColorView ?? throw new Exception("the flyout holds no colour view");
    view.Color = Avalonia.Media.Color.FromUInt32(0xFF123456); Pump();
    var hex = dlg.GetLogicalDescendants().OfType<TextBox>().First(t => t.Name == "surfaceBench");
    if (hex.Text != "#123456") throw new Exception("the picked colour did not reach the field: " + hex.Text);
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    dlg.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "41d_settings_colour_picker.png"));
    flyout.Hide(); Pump();
    dlg.Close(); Pump();
});
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
    foreach (var name in new[] { "Dip track", "Actual track", "Grade point", "Deviation", "Burden marker", "MWD profile", "Collar" })
        if (!labels.Contains(BlastDesigner.App.Services.Loc.Ui(name))) throw new Exception($"{name} missing from the two-column lists");
    // the labels written at the hole are rows of the side columns
    var rows = window.GetLogicalDescendants().OfType<Control>().Select(c => c.Name).ToHashSet();
    foreach (var l in Enum.GetValues<HoleLabel>())
        if (!rows.Contains("label" + l)) throw new Exception($"{l} missing from the hole label columns");
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
Step("Properties ▸ Add holes: electronic holes on no timing line join the line, are programmed and selected", () =>
{
    var holes = vm.Project!.Holes.Where(h => !h.IsDummy && h.Decks.Any(d => d.Primers.Count > 0)).ToList();
    if (holes.Count < 30) throw new Exception("the smoke blast needs 30 primed holes");
    var det = vm.Project.Products.FirstOrDefault(p => p.Kind == ProductKind.ElectronicDetonator) ?? throw new Exception("no electronic detonator in the catalogue");
    var y = holes.Max(h => h.Collar.Y) + 3;
    var pts = new List<BlastDesigner.Domain.Geometry.Vec3> { new(holes.Min(h => h.Collar.X) - 3, y, 300), new(holes.Max(h => h.Collar.X) + 3, y, 300) };
    var line = BlastDesigner.Engine.EbsTimingLine.CreateEntity(pts, new[] { new TimingLineSegment(5, 8, 0) }, new BlastDesigner.Engine.TimingLineOptions());
    var other = BlastDesigner.Engine.EbsTimingLine.CreateEntity(pts, new[] { new TimingLineSegment(5, 8, 0) }, new BlastDesigner.Engine.TimingLineOptions());
    line.HoleIds = holes.Take(20).Select(h => h.Id).ToList();
    other.HoleIds = holes.Skip(20).Take(5).Select(h => h.Id).ToList();
    var electronic = holes.Take(30).Select(h => h.Id).ToHashSet();
    vm.Change("Smoke electronic holes", p =>
    {
        p.Add(line); p.Add(other);
        foreach (var h in p.Holes.Where(h => electronic.Contains(h.Id)))
            foreach (var d in h.Decks) foreach (var pr in d.Primers) pr.DetonatorId = det.Id;
    });
    var tabs = window.GetLogicalDescendants().OfType<TabControl>().First(t => t.Items.OfType<TabItem>().Any(i => (i.Tag as string) == "Layers"));
    var tabBefore = tabs.SelectedIndex;
    vm.Doc!.Select(new[] { line.Id }); Pump();
    window.ShowInspectorTab(0); Pump();
    var button = window.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "addHolesToLine") ?? throw new Exception("no Add holes button on a timing line");
    button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    for (int i = 0; i < 50; i++) Pump();
    var after = ((TimingLineEntity)vm.Project.FindEntity(line.Id)!).HoleIds.ToHashSet();
    var want = holes.Take(20).Concat(holes.Skip(25).Take(5)).Select(h => h.Id).ToHashSet();
    if (!after.SetEquals(want)) throw new Exception($"the line has {after.Count} holes, {after.Count(want.Contains)} of the {want.Count} expected (holes of the other line must stay with it)");
    if (!vm.Doc.Selection.SetEquals(want)) throw new Exception("the line's holes are not selected");
    var planned = BlastDesigner.Engine.EbsTimingLine.Plan(vm.Project, vm.Project.Holes.Where(h => want.Contains(h.Id)).ToList(), line.Points, line.Segments, BlastDesigner.Engine.EbsTimingLine.OptionsOf(line), vm.Project.Electronic.MaxTimeMs, vm.Project.Electronic.TimeStepMs).Times;
    foreach (var (id, t) in planned)
        if (((Hole)vm.Project.FindEntity(id)!).ElectronicTimeMs is not double et || Math.Abs(et - t) > 1e-6) throw new Exception("an added hole is not programmed from the line");
    // Select assigned: the holes the line programs, from its properties
    vm.Doc.Select(new[] { line.Id }); Pump();
    window.ShowInspectorTab(0); Pump();
    var select = window.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "selectLineHoles") ?? throw new Exception("no Select assigned button on a timing line");
    select.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    if (!vm.Doc.Selection.SetEquals(want)) throw new Exception($"Select assigned selected {vm.Doc.Selection.Count} objects, {vm.Doc.Selection.Count(want.Contains)} of the line's {want.Count} holes");
    vm.Undo(); vm.Undo();
    vm.Undo();
    if (((TimingLineEntity)vm.Project.FindEntity(line.Id)!).HoleIds.Count != 20) throw new Exception("one undo should take the added holes back");
    vm.Undo();
    vm.Doc.ClearSelection();
    tabs.SelectedIndex = tabBefore; Pump();
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
    var strips = window.GetLogicalDescendants().OfType<Avalonia.Controls.ScrollViewer>().Where(s => s.IsEffectivelyVisible && s.Content is Avalonia.Controls.StackPanel { Children.Count: > 0 } p && p.Orientation == Avalonia.Layout.Orientation.Horizontal && s.Extent.Width > s.Viewport.Width + 10).ToList();
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
    var strips = window.Ribbon.GetLogicalDescendants().OfType<Avalonia.Controls.ScrollViewer>().Where(s => s.IsEffectivelyVisible && s.Content is Avalonia.Controls.StackPanel { Children.Count: > 0 } p && p.Orientation == Avalonia.Layout.Orientation.Horizontal && s.Extent.Width > s.Viewport.Width + 10).ToList();
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
Step("tie tool: ties joined to the initiation point run away from it, at 0 ms too, the whole of a Shift entry", () =>
{
    var x0 = vm.Project!.Holes.Max(h => h.Collar.X) + 100;
    var y0 = vm.Project.Holes.Min(h => h.Collar.Y) - 50;
    var tiesBefore = vm.Project.Ties.Count();
    var (connector, delay) = (vm.ActiveConnector, vm.ActiveTieDelayMs);
    vm.ActiveConnector = null; vm.ActiveTieDelayMs = null;
    var steps = vm.Doc!.UndoNames.Count;
    var ids = new List<Guid>();
    vm.Change("Smoke tied holes", p =>
    {
        for (int i = 0; i < 4; i++) { var h = new Hole { Collar = new BlastDesigner.Domain.Geometry.Vec3(x0 + i * 5, y0, 100), Label = "T" + (i + 1) }; p.Add(h); ids.Add(h.Id); }
        p.Add(new InitiationPoint { Position = ((Hole)p.FindEntity(ids[0])!).Collar, HoleId = ids[0], Label = "IPT" });
        BlastDesigner.Engine.Timing.Connect(p, ids[0], ids[1], null);
    });
    vm.Doc!.Select(ids); vm.ZoomSelection(); vm.Doc.ClearSelection(); Pump();
    vm.SetTool(new TieTool(vm)); Pump();
    void Click(Guid id, bool shift = false)
    {
        var h = (Hole)vm.Project!.FindEntity(id)!;
        var (x, y) = vm.View.Camera.ToScreen(h.Collar);
        vm.ActiveTool!.PointerPressed(new ToolPointer { X = x, Y = y, World = h.Collar, Snapped = h.Collar, Left = true, ClickCount = 1, Modifiers = shift ? Avalonia.Input.KeyModifiers.Shift : Avalonia.Input.KeyModifiers.None });
    }
    // drawn from the far end: the first tie reaches no point yet, the second joins both to the tied hole
    Click(ids[3]); Click(ids[2], shift: true); Click(ids[1]); Pump();
    if (!vm.Project!.Ties.Any(t => t.FromId == ids[1] && t.ToId == ids[2]) || !vm.Project.Ties.Any(t => t.FromId == ids[2] && t.ToId == ids[3]))
        throw new Exception("the ties of the entry should run away from the hole joined to the initiation point");
    while (vm.Doc.UndoNames.Count > steps) vm.Undo();
    if (vm.Project.Ties.Count() != tiesBefore) throw new Exception("the ties were not undone");
    vm.ActiveConnector = connector; vm.ActiveTieDelayMs = delay;
    vm.SelectTool();
});
Step("tie tool: a line from one hole to another ties every hole on the way, in order, in one undo step", () =>
{
    // beside the blast, a little off the straight line (within the pick radius), and one hole well off it
    var x0 = vm.Project!.Holes.Max(h => h.Collar.X) + 100;
    var y0 = vm.Project.Holes.Min(h => h.Collar.Y);
    var tiesBefore = vm.Project.Ties.Count();
    var ids = new List<Guid>();
    var off = Guid.Empty;
    vm.Change("Smoke holes in a line", p =>
    {
        for (int i = 0; i < 5; i++) { var h = new Hole { Collar = new BlastDesigner.Domain.Geometry.Vec3(x0 + i * 5, y0 + 0.03 * (i % 2), 100), Label = "L" + (i + 1) }; p.Add(h); ids.Add(h.Id); }
        var o = new Hole { Collar = new BlastDesigner.Domain.Geometry.Vec3(x0 + 10, y0 + 6, 100), Label = "off" };
        p.Add(o);
        off = o.Id;
    });
    vm.Doc!.Select(ids); vm.ZoomSelection(); vm.Doc.ClearSelection(); Pump();
    vm.SetTool(new TieTool(vm)); Pump();
    void Click(Guid id, bool shift = false)
    {
        var h = (Hole)vm.Project!.FindEntity(id)!;
        var (x, y) = vm.View.Camera.ToScreen(h.Collar);
        vm.ActiveTool!.PointerPressed(new ToolPointer { X = x, Y = y, World = h.Collar, Snapped = h.Collar, Left = true, ClickCount = 1, Modifiers = shift ? Avalonia.Input.KeyModifiers.Shift : Avalonia.Input.KeyModifiers.None });
    }
    void Move(Guid id)
    {
        var h = (Hole)vm.Project!.FindEntity(id)!;
        var (x, y) = vm.View.Camera.ToScreen(h.Collar);
        vm.ActiveTool!.PointerMoved(new ToolPointer { X = x, Y = y, World = h.Collar, Snapped = h.Collar });
    }
    // the pick radius from the line on the screen, on both sides, at any zoom: grey lines show the capture area
    void CheckCapture()
    {
        var lines = vm.View.Preview.Guides;
        if (vm.View.Preview.Lines.Count != 1 || lines.Count != 2) throw new Exception($"{vm.View.Preview.Lines.Count} preview lines and {lines.Count} grey ones instead of the tie and two");
        var cam = vm.View.Camera;
        var (ax, ay) = cam.ToScreen(((Hole)vm.Project!.FindEntity(ids[0])!).Collar);
        var (bx, by) = cam.ToScreen(((Hole)vm.Project.FindEntity(ids[4])!).Collar);
        double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
        var sides = new List<double>();
        foreach (var l in lines)
            foreach (var pt in l.Points)
            {
                var (x, y) = cam.ToScreen(pt);
                sides.Add(((x - ax) * dy - (y - ay) * dx) / len);
            }
        if (sides.Any(s => Math.Abs(Math.Abs(s) - vm.View.TargetRadius) > 0.1) || sides[0] * sides[2] >= 0)
            throw new Exception("the grey lines should be the pick radius away on both sides: " + string.Join(" ", sides.Select(s => s.ToString("0.00"))));
    }
    Click(ids[0]); Move(ids[4]); CheckCapture();
    vm.View.Redraw(); Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    Shot("31_tie_capture");
    vm.View.Camera.Scale *= 2; vm.View.Redraw();
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    CheckCapture();
    vm.View.Camera.Scale /= 2; vm.View.Redraw();
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    // F shows and hides them, its box follows; + and − change the pick radius, also the plus of the main keys with Shift
    void Press(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers mods = Avalonia.Input.KeyModifiers.None)
    {
        window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key, KeyModifiers = mods });
        Pump();
    }
    CheckBox CaptureBox() => window.GetLogicalDescendants().OfType<CheckBox>().First(c => c.Name == "tieCapture");
    if (CaptureBox().IsChecked != true) throw new Exception("Capture width should be on by default");
    Press(Avalonia.Input.Key.F); Move(ids[4]);
    if (vm.View.Preview.Guides.Count != 0 || CaptureBox().IsChecked != false) throw new Exception("F should hide the grey lines and clear the box");
    if (BlastDesigner.IO.ProgramSettings.Load().TieCaptureShown) throw new Exception("Capture width should be kept in the settings");
    Press(Avalonia.Input.Key.F); Move(ids[4]);
    if (vm.View.Preview.Guides.Count != 2 || CaptureBox().IsChecked != true) throw new Exception("F should bring the grey lines back");
    if (!BlastDesigner.IO.ProgramSettings.Load().TieCaptureShown) throw new Exception("Capture width back on should be kept in the settings");
    var radius = vm.View.TargetRadius;
    Press(Avalonia.Input.Key.Add);
    if (vm.View.TargetRadius != radius + 1) throw new Exception($"+ should widen the radius by 1: {vm.View.TargetRadius} from {radius}");
    Move(ids[4]); CheckCapture();
    Press(Avalonia.Input.Key.OemPlus, Avalonia.Input.KeyModifiers.Shift);
    if (vm.View.TargetRadius != radius + 2) throw new Exception("Shift and the plus of the main keys should widen the radius");
    Press(Avalonia.Input.Key.Subtract); Press(Avalonia.Input.Key.OemMinus);
    if (vm.View.TargetRadius != radius) throw new Exception("− should narrow the radius by 1: " + vm.View.TargetRadius);
    Click(ids[4]); Pump();
    if (vm.View.Preview.Guides.Count != 0) throw new Exception("the grey lines stayed after the tie");
    var ties = vm.Project!.Ties.Where(t => ids.Contains(t.FromId) || ids.Contains(t.ToId)).ToList();
    if (ties.Count != 4) throw new Exception($"{ties.Count} ties instead of 4 through the holes on the line");
    for (int i = 1; i < 5; i++)
        if (!ties.Any(t => t.FromId == ids[i - 1] && t.ToId == ids[i])) throw new Exception($"no tie from hole {i} to {i + 1}");
    // the tie finished the entry: the next click only starts a new one; a hole already tied takes another tie, and
    // with Shift the entry goes on from it, so the next click ties again
    var steps = vm.Doc!.UndoNames.Count;
    Click(off); Pump();
    if (vm.Doc.UndoNames.Count != steps) throw new Exception("after a tie without Shift the next click should start a new entry, not tie");
    Click(ids[2], shift: true); Pump();
    if (!vm.Project.Ties.Any(t => (t.FromId == off && t.ToId == ids[2]) || (t.FromId == ids[2] && t.ToId == off))) throw new Exception("no tie to a hole already tied");
    steps = vm.Doc.UndoNames.Count;
    Click(ids[3]); Pump();
    if (vm.Doc.UndoNames.Count != steps + 1) throw new Exception("with Shift the entry should go on from the hole tied to");
    vm.Undo();
    // with an initiation point on the first hole, a line drawn back towards it is turned to run away from the point
    vm.Change("Smoke initiation point", p => p.Add(new InitiationPoint { Position = ((Hole)p.FindEntity(ids[0])!).Collar, HoleId = ids[0], Label = "IPT" }));
    Click(ids[3]); Click(ids[1]); Pump();
    if (!vm.Project.Ties.Any(t => t.FromId == ids[1] && t.ToId == ids[2]) || !vm.Project.Ties.Any(t => t.FromId == ids[2] && t.ToId == ids[3]))
        throw new Exception("ties drawn towards the initiation point should be turned to run away from it");
    vm.ActiveTool!.Cancel();
    vm.Undo();
    vm.Undo();
    vm.Undo();
    vm.Undo();
    if (vm.Project.Ties.Count() != tiesBefore) throw new Exception("one undo should remove all the ties of the line");
    // Tie chain off on the panel: one tie from the first hole to the last
    var chainBox = window.GetLogicalDescendants().OfType<CheckBox>().FirstOrDefault(c => c.Name == "tieChain") ?? throw new Exception("no Tie chain box on the tie panel");
    if (chainBox.IsChecked != true) throw new Exception("Tie chain should be on by default");
    chainBox.IsChecked = false; Pump();
    vm.ActiveTool!.Cancel();
    var stepsBefore = vm.Doc!.UndoNames.Count;
    Click(ids[0]); Move(ids[4]);
    if (vm.View.Preview.Guides.Count != 0) throw new Exception("with the chain off nothing is captured: no grey lines");
    Click(ids[4]); Pump();
    if (vm.Doc.UndoNames.Count != stepsBefore + 1) throw new Exception("one tie should be one undo step");
    var single = vm.Project.Ties.Where(t => ids.Contains(t.FromId) || ids.Contains(t.ToId)).ToList();
    if (single.Count != 1 || single[0].FromId != ids[0] || single[0].ToId != ids[4]) throw new Exception($"with the chain off: {single.Count} ties, expected one from the first hole to the last");
    vm.Undo();
    chainBox.IsChecked = true; Pump();
    vm.Undo();
    vm.SelectTool();
});
Step("select tool: a selected row of holes is dragged with the mouse on the plan", () =>
{
    vm.SelectTool(); Pump();
    var row = vm.Project!.Rows[0];
    var ids = row.HoleIds.ToList();
    vm.Doc!.Select(ids); vm.ZoomSelection(); vm.PlanView(); Pump();
    var hole = (Hole)vm.Project.FindEntity(ids[ids.Count / 2])!;
    var before = ids.ToDictionary(id => id, id => ((Hole)vm.Project.FindEntity(id)!).Collar);
    var (sx, sy) = vm.View.Camera.ToScreen(hole.Collar);
    var origin = vm.View.TranslatePoint(new Point(0, 0), window)!.Value;
    var start = new Point(origin.X + sx, origin.Y + sy);
    window.MouseDown(start, Avalonia.Input.MouseButton.Left); Pump();
    for (int k = 1; k <= 10; k++) { window.MouseMove(new Point(start.X + 6 * k, start.Y + 3 * k), Avalonia.Input.RawInputModifiers.LeftMouseButton); Pump(); }
    window.MouseUp(new Point(start.X + 60, start.Y + 30), Avalonia.Input.MouseButton.Left); Pump();
    var moved = ids.Count(id => Vec3.Distance2D(((Hole)vm.Project.FindEntity(id)!).Collar, before[id]) > 0.01);
    if (moved != ids.Count) throw new Exception($"{moved} of the row's {ids.Count} holes moved with the drag");
    vm.Undo();
    // grabbed by its line between two holes (rows shown): the whole row moves
    var showRows = vm.Project.View.ShowRows;
    vm.Project.View.ShowRows = true;
    vm.Doc.Select(ids); Pump();
    var h0 = (Hole)vm.Project.FindEntity(ids[0])!; var h1 = (Hole)vm.Project.FindEntity(ids[1])!;
    var (mx, my) = vm.View.Camera.ToScreen(Vec3.Lerp(h0.Collar, h1.Collar, 0.5));
    var mid = new Point(origin.X + mx, origin.Y + my);
    window.MouseDown(mid, Avalonia.Input.MouseButton.Left); Pump();
    for (int k = 1; k <= 10; k++) { window.MouseMove(new Point(mid.X + 5 * k, mid.Y - 4 * k), Avalonia.Input.RawInputModifiers.LeftMouseButton); Pump(); }
    window.MouseUp(new Point(mid.X + 50, mid.Y - 40), Avalonia.Input.MouseButton.Left); Pump();
    moved = ids.Count(id => Vec3.Distance2D(((Hole)vm.Project.FindEntity(id)!).Collar, before[id]) > 0.01);
    if (moved != ids.Count) throw new Exception($"grabbed by its line, {moved} of the row's {ids.Count} holes moved");
    vm.Undo();
    // a click on the line of a row that is not selected selects its holes (with the ties along it hidden: a click on
    // a tie selects the tie)
    var showTies = vm.Project.View.ShowTies;
    vm.Project.View.ShowTies = false;
    vm.Doc.ClearSelection();
    vm.Project.View.ShowRows = true; vm.Project.View.ShowTies = false; Pump();
    // another spot of the line than the drag started at: a press there again in time would be a double click
    var (qx, qy) = vm.View.Camera.ToScreen(Vec3.Lerp((Hole)vm.Project.FindEntity(ids[1])! is var g1 ? g1.Collar : default, ((Hole)vm.Project.FindEntity(ids[2])!).Collar, 0.4));
    var spot = new Point(origin.X + qx, origin.Y + qy);
    window.MouseDown(spot, Avalonia.Input.MouseButton.Left); Pump();
    window.MouseUp(spot, Avalonia.Input.MouseButton.Left); Pump();
    if (!vm.Doc.Selection.SetEquals(ids)) throw new Exception($"a click on the row line selected {vm.Doc.Selection.Count} objects ({string.Join(",", vm.Doc.SelectedEntities.Select(e => e.KindName))}), not the row's {ids.Count} holes");
    vm.Project.View.ShowRows = showRows;
    vm.Project.View.ShowTies = showTies;
    vm.Doc.ClearSelection();
});
Step("polyline tool: no label by the cursor before the first point, the distance after it", () =>
{
    vm.DrawPolylineTool(false); Pump();
    var at = vm.Project!.Holes.First().Collar + new Vec3(5, 5, 0);
    vm.ActiveTool!.PointerMoved(new ToolPointer { World = at, Snapped = at });
    if (vm.View.Preview.Labels.Any()) throw new Exception("a label by the cursor before the first point: '" + vm.View.Preview.Labels[0].Text + "'");
    vm.ActiveTool.PointerPressed(new ToolPointer { World = at, Snapped = at, Left = true, ClickCount = 1 });
    var next = at + new Vec3(10, 0, 0);
    vm.ActiveTool.PointerMoved(new ToolPointer { World = next, Snapped = next });
    if (!vm.View.Preview.Labels.Any(l => l.Text.Length > 0)) throw new Exception("no distance label after the first point");
    // a second point, then a click on it again finishes the line
    var lines = vm.Project.Entities.OfType<PolylineEntity>().Count();
    var (nx, ny) = vm.View.Camera.ToScreen(next);
    vm.ActiveTool.PointerPressed(new ToolPointer { X = nx, Y = ny, World = next, Snapped = next, Left = true, ClickCount = 1 });
    vm.ActiveTool.PointerPressed(new ToolPointer { X = nx + 2, Y = ny + 1, World = next, Snapped = next, Left = true, ClickCount = 1 }); Pump();
    var made = vm.Project.Entities.OfType<PolylineEntity>().Skip(lines).ToList();
    if (made.Count != 1 || made[0].Points.Count != 2) throw new Exception($"a click on the last point should finish the line: {made.Count} lines, {made.FirstOrDefault()?.Points.Count} points");
    vm.Undo();
    vm.ActiveTool?.Cancel(); vm.SelectTool();
});
Step("an initiation point on a hole is picked by its star's rays, the hole by its middle", () =>
{
    var hole = vm.Project!.Holes.First(h => !h.IsDummy);
    var ip = new InitiationPoint { Position = hole.Collar, Label = "IPX" };
    vm.Change("Smoke initiation point on a hole", p => p.Add(ip));
    vm.Doc!.Select(new[] { hole.Id }); vm.ZoomSelection(); vm.Doc.ClearSelection(); vm.PlanView(); Pump();
    var (x, y) = vm.View.Camera.ToScreen(hole.Collar);
    if (vm.View.HitTest(x, y)?.Id != hole.Id) throw new Exception("the middle should pick the hole");
    var onRay = vm.View.HitTest(x, y - BlastDesigner.Rendering.PlanRenderer.InitiationStarRadius * 0.7);
    if (onRay?.Id != ip.Id) throw new Exception("a ray of the star should pick the initiation point, picked " + onRay?.KindName);
    // the tie tool starts at the point from anywhere on the star, its middle too
    var tiedIds = vm.Project.Ties.SelectMany(t => new[] { t.FromId, t.ToId }).ToHashSet();
    var other = vm.Project.Holes.Where(h => !h.IsDummy && h.Id != hole.Id && !tiedIds.Contains(h.Id)).OrderBy(h => Vec3.Distance(h.Collar, hole.Collar)).First();
    vm.SetTool(new TieTool(vm)); Pump();
    var (ox, oy) = vm.View.Camera.ToScreen(other.Collar);
    vm.ActiveTool!.PointerPressed(new ToolPointer { X = x, Y = y, World = hole.Collar, Snapped = hole.Collar, Left = true, ClickCount = 1 });
    vm.ActiveTool.PointerPressed(new ToolPointer { X = ox, Y = oy, World = other.Collar, Snapped = other.Collar, Left = true, ClickCount = 1 }); Pump();
    var fromStar = vm.Project.Ties.FirstOrDefault(t => t.ToId == other.Id);
    if (fromStar?.FromId != ip.Id) throw new Exception("a tie started on the star's middle should come from the initiation point, not " + (fromStar == null ? "nothing" : vm.Project.FindEntity(fromStar.FromId)?.KindName));
    vm.ActiveTool?.Cancel(); vm.SelectTool();
    vm.Undo();
    vm.Undo();
    // an initiation point is placed only on a hole
    vm.InitiationPointTool(false); Pump();
    var points = vm.Project.Entities.OfType<InitiationPoint>().Count();
    var at = hole.Collar + new Vec3(3, 3, 0);
    vm.ActiveTool!.PointerPressed(new ToolPointer { World = at, Snapped = at, Left = true, ClickCount = 1 }); Pump();
    if (vm.Project.Entities.OfType<InitiationPoint>().Count() != points) throw new Exception("an initiation point placed off the holes");
    // on a hole it is bound to it and goes on to the tie tool, chained from that hole, with the initiation catalogue
    vm.ActiveTool!.PointerPressed(new ToolPointer { X = x, Y = y, World = hole.Collar, Snapped = hole.Collar, Left = true, ClickCount = 1 }); Pump();
    var placed = vm.Project.Entities.OfType<InitiationPoint>().LastOrDefault();
    if (placed?.HoleId != hole.Id) throw new Exception("the initiation point should be bound to the hole it was placed on");
    var catalogue = BlastDesigner.App.Services.Dialogs.LastShown;
    if (vm.ActiveTool is not TieTool || catalogue?.Title != BlastDesigner.App.Services.Loc.T(TiePaletteWindow.Title) || !catalogue.IsVisible) throw new Exception("after the initiation point: the tie tool and the catalogue");
    var next = vm.Project.Holes.Where(h => !h.IsDummy && h.Id != hole.Id && !vm.Project.Ties.Any(t => t.FromId == h.Id || t.ToId == h.Id)).OrderBy(h => Vec3.Distance(h.Collar, hole.Collar)).First();
    var (nx, ny) = vm.View.Camera.ToScreen(next.Collar);
    vm.ActiveTool.PointerPressed(new ToolPointer { X = nx, Y = ny, World = next.Collar, Snapped = next.Collar, Left = true, ClickCount = 1 }); Pump();
    if (vm.Project.Ties.FirstOrDefault(t => t.ToId == next.Id)?.FromId != hole.Id) throw new Exception("the first tie after placing the point should go from its hole");
    if (next.FiringTimeMs == null) throw new Exception("the hole tied from the point's hole is not initiated");
    catalogue.Close(); vm.SelectTool(); vm.Undo(); vm.Undo();
});
Step("the Ties button opens the initiation catalogue, whose position goes to the settings", () =>
{
    vm.TieToolWithPalette(); Pump();
    var win = BlastDesigner.App.Services.Dialogs.LastShown ?? throw new Exception("no initiation catalogue");
    if (vm.ActiveTool is not TieTool || win.Title != BlastDesigner.App.Services.Loc.T(TiePaletteWindow.Title)) throw new Exception("the Ties button should give the tie tool and the catalogue: " + win.Title);
    win.Position = new PixelPoint(win.Position.X + 30, win.Position.Y + 20); Pump();
    if (!settings.Window.TryGetValue(TiePaletteWindow.PositionKey, out var pos) || pos != $"{win.Position.X},{win.Position.Y}") throw new Exception("catalogue position not in the settings: " + pos);
    var left = win.Position;
    win.Close(); vm.SelectTool(); Pump();
    // opened again, it is where it was left, and stays there
    vm.TieToolWithPalette(); Pump();
    win = BlastDesigner.App.Services.Dialogs.LastShown!;
    Pump();
    if (win.Position != left) throw new Exception($"the catalogue opened at {win.Position}, not where it was left ({left})");
    if (settings.Window[TiePaletteWindow.PositionKey] != $"{left.X},{left.Y}") throw new Exception("opening the catalogue changed its saved place: " + settings.Window[TiePaletteWindow.PositionKey]);
    win.Close(); vm.SelectTool();
});
Step("initiation catalogue: Type → Family → Variant, user-defined delay, the window stays put", () =>
{
    vm.ShowTiePalette(); Pump();
    var win = BlastDesigner.App.Services.Dialogs.LastShown ?? throw new Exception("initiation catalogue did not open");
    Button Chip(string tag) => win.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => (b.Tag as string) == tag) ?? throw new Exception("no " + tag + " in the catalogue");
    void Press(string tag) { Chip(tag).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump(); }
    var types = win.GetLogicalDescendants().OfType<Button>().Count(b => (b.Tag as string)?.StartsWith("type:") == true);
    if (types != 7) throw new Exception($"{types} types instead of 7");
    // Electric: the family of the electric detonators and its variants by delay
    string TypeTag(string name) => "type:" + vm.Project!.Catalogue.Types.First(t => t.Name == name).Id;
    Press(TypeTag("Electric"));
    if (BlastDesigner.App.Services.Dialogs.LastShown != win || !win.IsVisible) throw new Exception("choosing a type recreated or closed the catalogue");
    if (vm.ActiveConnector?.Kind != ProductKind.ElectricDetonator) throw new Exception("Electric did not pick an electric detonator");
    Chip("family:Electric detonator");
    if (win.GetLogicalDescendants().OfType<Button>().Any(b => (b.Tag as string)?.StartsWith("subtype:") == true)) throw new Exception("the Subtype group is still there");
    var variants = win.GetLogicalDescendants().OfType<Button>().Where(b => (b.Tag as string)?.StartsWith("variant:") == true).ToList();
    if (variants.Count != vm.Project!.Products.Count(p => p.Kind == ProductKind.ElectricDetonator && p.FamilyName == "Electric detonator")) throw new Exception($"{variants.Count} variants of the electric detonator family");
    var last = vm.Project.Products.Where(p => p.Kind == ProductKind.ElectricDetonator).OrderBy(p => p.DelayMs).Last();
    Press("variant:" + last.Id);
    if (vm.ActiveConnector?.Id != last.Id) throw new Exception("the variant was not taken");
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    win.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "39d_initiation_catalogue.png"));
    // user defined: a delay of its own on the new ties
    Press(TypeTag("User defined"));
    var delay = win.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "tieUserDelay") ?? throw new Exception("no delay for the user-defined type");
    delay.Text = "33"; delay.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent)); Pump();
    if (vm.ActiveTieDelayMs != 33 || vm.ActiveConnector != null) throw new Exception("the user-defined delay was not taken");
    // back to the shock tube connectors for the steps after
    Press(TypeTag("Shock tube"));
    if (vm.ActiveTieDelayMs != null || vm.ActiveConnector?.Kind != ProductKind.SurfaceConnector) throw new Exception("shock tube did not go back to a connector");
    // a type lists the products of all its kinds: the shock tube connectors and the downhole detonators
    foreach (var f in vm.Project.Products.Where(p => p.Kind is ProductKind.SurfaceConnector or ProductKind.DownholeDetonator).Select(p => p.FamilyName).Distinct()) Chip("family:" + f);
    var downhole = vm.Project.Products.First(p => p.Kind == ProductKind.DownholeDetonator);
    Press("family:" + downhole.FamilyName);
    if (vm.ActiveConnector?.Kind != ProductKind.DownholeDetonator) throw new Exception("the downhole detonator family was not taken");
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    win.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "39e_initiation_catalogue_shock_tube.png"));
    Press("family:" + vm.Project.Products.First(p => p.Kind == ProductKind.SurfaceConnector).FamilyName);
    if (vm.ActiveConnector?.Kind != ProductKind.SurfaceConnector) throw new Exception("shock tube did not go back to a connector");
    if (!win.IsVisible) throw new Exception("the catalogue closed");
    win.Close(); vm.SelectTool();
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
    // OK opens the selected file, as a double click does
    var ok = w.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().FirstOrDefault(b => b.Name == "openSelected") ?? throw new Exception("no OK button");
    if (!ok.IsEnabled) throw new Exception("OK is off with files in the list");
    var task = StartupWindow.Show(s); Pump();
    var okForm = StartupWindow.Last!;
    var okWin = BlastDesigner.App.Services.Dialogs.LastShown!;
    okWin.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().First(b => b.Name == "openSelected").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Pump();
    if (!task.IsCompleted || task.Result != StartupChoice.Recent || StartupWindow.SelectedRecent != okForm.VisibleFiles[0]) throw new Exception("OK did not open the selected file");
    w.Close(); Pump();
    _ = StartupWindow.Show(new ProgramSettings { Language = settings.Language }); Pump();
    Capture(BlastDesigner.App.Services.Dialogs.LastShown!, "40c_startup_empty");
    if (BlastDesigner.App.Services.Dialogs.LastShown!.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().First(b => b.Name == "openSelected").IsEnabled) throw new Exception("OK is on with an empty list");
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
Step("layers panel: Topography before Drawing, layers without a group last", () =>
{
    vm.Change("smoke groups", p => { p.GetOrCreateLayer("Smoke loose"); p.GetOrCreateLayer("Smoke drawing", 0xFF000000, "Drawing"); });
    var panel = window.GetLogicalDescendants().OfType<LayersPanel>().First();
    panel.Refresh(); Pump();
    var groups = panel.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.StartsWith("▾ ") || t.StartsWith("▸ ")).Select(t => t[2..]).ToList();
    int At(string key) => groups.IndexOf(key.Length == 0 ? BlastDesigner.App.Services.Loc.T("(no group)") : BlastDesigner.App.Services.Loc.Name(key));
    if (At(StandardLayers.TopographyGroup) < 0 || At("Drawing") < 0 || At("") < 0) throw new Exception("groups missing: " + string.Join(", ", groups));
    if (At(StandardLayers.TopographyGroup) > At("Drawing") || At("") != groups.Count - 1) throw new Exception("wrong order: " + string.Join(", ", groups));
    vm.Undo(); Pump();
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
    if (!r.FromModel) throw new Exception("not recognized by the model: " + BlastDesigner.Recognition.SurveyModel.Problem);
    var chips = form.GetLogicalDescendants().OfType<Button>().ToList();
    if (!chips.Any(b => (b.Tag as string) == "method:model" && b.IsEnabled) || !chips.Any(b => (b.Tag as string) == "method:rules")) throw new Exception("no model / rules switch");
    // switching to the rules and back recognizes again in the same window, which stays where it is (the switch also
    // saves the choice in the user's settings, which the smoke test leaves alone: it calls what the switch calls after)
    var win = BlastDesigner.App.Services.Dialogs.LastShown!;
    var position = win.Position;
    void Wait(Task t) { for (int i = 0; i < 1000 && !t.IsCompleted; i++) { Pump(); Thread.Sleep(10); } Pump(); }
    Wait(form.Recognize(false));
    if (BlastDesigner.App.Services.Dialogs.LastShown != win || !win.IsVisible || win.Position != position) throw new Exception("the window was recreated or moved");
    if (form.Recognition.FromModel) throw new Exception("rules did not recognize");
    var overlays = vm.View.Overlays.OfType<SurveyRecognitionOverlay>().ToList();
    if (overlays.Count != 1 || overlays[0].Recognition != form.Recognition) throw new Exception("the plan does not show the new result");
    Wait(form.Recognize(true));
    r = form.Recognition;
    if (!r.FromModel || BlastDesigner.App.Services.Dialogs.LastShown != win) throw new Exception("back to the model failed");
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
    // each type in its own layer, drawn in the layer's colour: shown and hidden with the layer
    var crestLayer = p2.Layers.FirstOrDefault(l => l.Name == "Crest lines") ?? throw new Exception("no crest layer");
    if (crestLayer.Color != SurveyRecognitionWindow.ColorOf(SurveyLineClass.Crest)) throw new Exception("crest layer has the wrong colour");
    if (p2.Entities.OfType<PolylineEntity>().Any(l => l.Kind == LineKind.Crest && (l.LayerId != crestLayer.Id || l.Color != null))) throw new Exception("a crest outside its layer or with its own colour");
    // a type set by hand in Properties moves the line the same way, and back out when the type is taken away
    var byHand = p2.Entities.OfType<PolylineEntity>().First(l => l.Kind == LineKind.Crest);
    byHand.Kind = LineKind.Toe;
    SurveyRecognitionWindow.PutInKindLayer(p2, byHand);
    if (p2.FindLayer(byHand.LayerId)?.Name != "Toe lines") throw new Exception("a toe set by hand is not in the toe layer");
    byHand.Kind = LineKind.General;
    SurveyRecognitionWindow.PutInKindLayer(p2, byHand);
    if (p2.FindLayer(byHand.LayerId)?.Name != StandardLayers.Survey) throw new Exception("a line without a type stayed in a type layer");
    byHand.Kind = LineKind.Crest;
    SurveyRecognitionWindow.PutInKindLayer(p2, byHand);
    if (!p2.Entities.OfType<SurfaceEntity>().Any()) throw new Exception("no bench surface built");
    // the faces between crests and toes as a surface of their own, of kind Face
    if (!p2.Entities.OfType<SurfaceEntity>().Any(s => s.Kind == SurfaceKind.Face && s.TriangleCount > 0)) throw new Exception("no face surface");
    // and the bench platforms, of kind Bench
    if (!p2.Entities.OfType<SurfaceEntity>().Any(s => s.Kind == SurfaceKind.Bench && s.TriangleCount > 0)) throw new Exception("no bench surface");
    // every survey line shapes the surface, not only crests and toes: a line left as "other" passes through it too
    // (the survey surface and its faces together)
    var surfaceVertices = p2.Entities.OfType<SurfaceEntity>().SelectMany(s => s.Vertices).ToList();
    var other = p2.Entities.OfType<PolylineEntity>().FirstOrDefault(l => l.Kind == LineKind.General && l.Points.All(q => q.Z != 0));
    if (other != null && !other.Points.All(q => surfaceVertices.Any(v => Vec3.Distance(v, q) < 0.01))) throw new Exception("a survey line of class other is not in the surface");
    if (vm.ActiveTool is SurveyClassTool || vm.View.Overlays.OfType<SurveyRecognitionOverlay>().Any()) throw new Exception("tool or overlay left after closing");
    vm.Undo(); Pump();
    if (vm.Project!.Entities.OfType<PolylineEntity>().Any(l => l.Kind == LineKind.Crest) || vm.Project.Entities.OfType<SurfaceEntity>().Any()
        || vm.Project.Layers.Any(l => l.Name == "Crest lines")) throw new Exception("undo did not take it all back");
});
Step("DXF import window: hidden layers as chips with a note, toggles, Import returns the choice", () =>
{
    var path = Path.Combine(outDir, "survey.dxf");
    // the window only shows the file's name and size; it does not read the drawing
    File.WriteAllText(path, "0" + Environment.NewLine + "EOF" + Environment.NewLine);
    var task = CadImportWindow.Show(path); Pump();
    var dlg = BlastDesigner.App.Services.Dialogs.LastShown!;
    var form = dlg.GetLogicalDescendants().OfType<CadImportForm>().First();
    var chips = form.GetLogicalDescendants().OfType<Button>().Where(b => (b.Tag as string)?.StartsWith("hidden:") == true).ToList();
    if (chips.Count != 3) throw new Exception($"{chips.Count} hidden-layer chips");
    foreach (var name in new[] { "separateFlat", "faces", "text" })
        if (!form.GetLogicalDescendants().OfType<ToggleSwitch>().Any(t => t.Name == name)) throw new Exception("no toggle " + name);
    var note = form.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "hiddenNote");
    var before = note.Text;
    chips.First(b => (string)b.Tag! == "hidden:Skip").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    if (note.Text == before) throw new Exception("the note did not follow the choice");
    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump();
    dlg.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "61_dxf_import.png"));
    form.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "import").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
    if (!task.IsCompleted || task.Result?.HiddenLayers != CadHiddenLayers.Skip) throw new Exception("Import did not return the chosen options");
});
UsageLog.Stop();
RestoreSettings();

Console.WriteLine(failures == 0 ? "SMOKE PASSED" : $"SMOKE FAILED ({failures})");
return failures == 0 ? 0 : 1;
