using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using BlastDesigner.Rendering;

namespace BlastDesigner.App.Views;

public static partial class AnalysisWindows
{
    // ---- blast analysis ----------------------------------------------------------------

    public const string BlastAnalysisTitle = "Blast analysis";

    // what was chosen in the window last time, while the program runs
    static double _firstMovementArrowSize = BlastDesigner.Engine.FirstMovement.DefaultArrowSize;
    static bool _analysisHideGeometry = true;
    static bool _analysisOnPlan;
    static BlastAnalysis _analysisTab = BlastAnalysis.FirstMovement;
    /// <summary>Upper limit of the burden relief, ms/m (null: the most relief of the blast).</summary>
    static double? _burdenReliefLimit;
    /// <summary>Turns the open window to a tab.</summary>
    static Action<BlastAnalysis>? _analysisSelect;

    /// <summary>The name of an analysis: its tab, its button on the ribbon and its sheet.</summary>
    public static string AnalysisName(BlastAnalysis a) => Loc.Ui(a switch
    {
        BlastAnalysis.InitiationAngle => "Angle of initiation",
        BlastAnalysis.BurdenRelief => "Burden relief",
        BlastAnalysis.Isolines => "Isolines",
        _ => "First movement"
    });

    /// <summary>Why a blast has nothing to show in the analyses.</summary>
    public static string NoFirstMovement(BlastProject p) =>
        Loc.T(BlastDesigner.Engine.FirstMovement.AllAtOnce(p) ? "All holes fire at the same time, the direction cannot be determined." : "No hole is initiated. Add an initiation point and ties, or electronic times.");

    /// <summary>Whether the analysis has anything to show for the blast, whose times are solved.</summary>
    public static bool HasAnalysis(BlastProject p, BlastAnalysis a) => a switch
    {
        BlastAnalysis.InitiationAngle => TimingVectorOverlay.InitiationAngles(p).Analysis(p).Count > 0,
        BlastAnalysis.BurdenRelief => BurdenRelief.Valid(new BurdenReliefOverlay().Delaunay(p)).Any(),
        BlastAnalysis.Isolines => p.Holes.Any(h => h.FiringTimeMs.HasValue && !h.IsDummy),
        _ => new FirstMovementOverlay().Arrows(p).Count > 0
    };

    /// <summary>
    /// The sheet of the tab last open in the Blast analysis window, with its choices, whether it is open or not; the
    /// isolines at <paramref name="isolineInterval"/> ms (the interval of the program settings).
    /// </summary>
    public static FirstMovementSheet FirstMovementPage(double isolineInterval, double yaw = 0) => new()
    {
        IsolineIntervalMs = isolineInterval,
        Analysis = _analysisTab,
        Title = AnalysisName(_analysisTab),
        HideGeometry = _analysisHideGeometry,
        Yaw = yaw,
        Arrows = new FirstMovementOverlay { ArrowSize = _firstMovementArrowSize },
        ReliefLimit = _burdenReliefLimit
    };

    /// <summary>
    /// The Blast analysis window: the angle of initiation, the first movement, the burden relief and the isolines of the
    /// firing times (at the interval of the Visualization window, without the colour by time), each on a tab, over
    /// one plan of its own (its view kept from tab to tab). It leaves the main window and the other windows free. Its
    /// switch puts the analysis of the open tab on the main plan; Clear analysis takes it off and turns the switch off.
    /// Opened again, it only comes forward on the tab asked for.
    /// </summary>
    public static void BlastAnalysisWindow(MainViewModel vm, BlastAnalysis tab)
    {
        if (Dialogs.ActivateExisting(BlastAnalysisTitle))
        {
            _analysisSelect?.Invoke(tab);
            return;
        }
        var p = vm.Project!;
        Timing.Solve(p);
        _analysisTab = tab;

        // the layers of the window, and the same of the main plan in its own style: line arrows, no second legend
        var arrows = new FirstMovementOverlay { ArrowSize = _firstMovementArrowSize };
        var angles = TimingVectorOverlay.InitiationAngles(p);
        var relief = new BurdenReliefOverlay { UpperLimit = _burdenReliefLimit, LegendTitle = Loc.T("Burden relief, ms/m") };
        var planArrows = new FirstMovementOverlay { PlanStyle = true };
        var planAngles = TimingVectorOverlay.InitiationAngles(p);
        var planRelief = new BurdenReliefOverlay { UpperLimit = _burdenReliefLimit, ShowLegend = false };
        // the isolines as the Isolines button draws them, on the plan of the window and on the main plan alike
        var isolines = new TimingContourOverlay { IntervalMs = vm.Settings.IsochroneIntervalMs, Contours = true, ColourHoles = false };
        var planIsolines = new TimingContourOverlay { IntervalMs = vm.Settings.IsochroneIntervalMs, Contours = true, ColourHoles = false };
        IOverlay Layer(BlastAnalysis a) => a switch { BlastAnalysis.InitiationAngle => angles, BlastAnalysis.BurdenRelief => relief, BlastAnalysis.Isolines => isolines, _ => arrows };
        IOverlay PlanLayer(BlastAnalysis a) => a switch { BlastAnalysis.InitiationAngle => planAngles, BlastAnalysis.BurdenRelief => planRelief, BlastAnalysis.Isolines => planIsolines, _ => planArrows };

        var view = SecondaryViewport(vm, () =>
        {
            var o = vm.BuildSceneOptions();
            // a drag on the main plan shows here too: the dragged holes and the analysis from their new places
            var drag = vm.View.Preview;
            return new SceneOptions
            {
                Selection = o.Selection, Background = o.Background, SelectionColor = o.SelectionColor, CollarPixels = o.CollarPixels, DrawSummaryBlock = false,
                Overlays = new[] { Layer(_analysisTab) }, HideGeometry = _analysisHideGeometry,
                MovedEntities = drag.Moving is { Count: > 0 } moving ? new HashSet<Guid>(moving) : null, MoveOffset = drag.MoveOffset
            };
        });
        vm.DragPreview += view.Redraw;

        static TextBlock Caption(string text) => new() { Text = Loc.T(text).ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Faint, Margin = new Thickness(2, 0, 0, 5) };
        static Control Group(string caption, Control content) => new StackPanel { Margin = new Thickness(0, 0, 22, 6), Children = { Caption(caption), content } };
        static Control IconText(string icon, string text) => Ui.Row(6, Look.Icon(icon, 16, Look.TextSoft), new TextBlock { Text = Loc.T(text), VerticalAlignment = VerticalAlignment.Center });
        static Button Command(string icon, string text, string name) => new()
        {
            Content = IconText(icon, text), Height = 32, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(8), VerticalContentAlignment = VerticalAlignment.Center, Name = name
        };

        // ---- the view, the same on every tab
        void ResetOrientation()
        {
            view.Camera.Pitch = 90;
            view.Camera.Yaw = vm.Project?.Header.BlastOrientation ?? view.Camera.Yaw;
            view.ZoomAll();
        }
        var reset = Ui.Flat(IconText("north", "Reset orientation"), Loc.T("Plan turned to the blast orientation, everything in view"), ResetOrientation, height: 32);
        var fit = Ui.Flat(IconText("fit", "Zoom all"), null, view.ZoomAll, height: 32);
        reset.Name = "reset";
        fit.Name = "fit";
        var viewGroup = Group("View", new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(1), Child = Ui.Row(0, reset, fit) });

        // ---- first movement: the arrow size, − value + in one field-like pill
        var size = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 54, TextAlignment = TextAlignment.Center, FontFamily = Look.Mono, Foreground = Look.Text, Name = "arrowSize" };
        void Resize(double step)
        {
            arrows.ArrowSize = _firstMovementArrowSize = Math.Clamp(Math.Round(arrows.ArrowSize + step, 1), 0.2, 10);
            size.Text = Loc.F("{0:0.0} m", arrows.ArrowSize);
            view.Redraw();
            vm.View.Redraw();
        }
        size.Text = Loc.F("{0:0.0} m", arrows.ArrowSize);
        var smaller = Ui.Flat(new TextBlock { Text = "−", FontSize = 16 }, Loc.T("Decrease arrow"), () => Resize(-0.2), 32, 30);
        var bigger = Ui.Flat(new TextBlock { Text = "+", FontSize = 16 }, Loc.T("Increase arrow"), () => Resize(0.2), 32, 30);
        smaller.Name = "smaller";
        bigger.Name = "bigger";
        var stepper = new Border { Background = Look.Field, BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(1), Child = Ui.Row(0, smaller, size, bigger) };
        stepper.PointerWheelChanged += (_, e) => { Resize(e.Delta.Y > 0 ? 0.2 : -0.2); e.Handled = true; };
        var arrowGroup = Group("Arrow size", stepper);
        var export = Command("export", "Export CSV…", "export");
        ToolTip.SetTip(export, Loc.T("X, Y and the direction of every arrow"));
        export.Click += async (_, _) =>
        {
            var path = await Dialogs.SaveFile("First movement", "first-movement.csv", "csv", ("CSV", new[] { "*.csv" }));
            if (path != null) File.WriteAllText(path, BlastDesigner.Engine.FirstMovement.ToCsv(arrows.Arrows(vm.Project!)) + Environment.NewLine);
        };

        // ---- burden relief: the upper limit of the scale, and the relief under the cursor
        var limit = new TextBox
        {
            Text = _burdenReliefLimit is double u ? u.ToString("0.##", CultureInfo.CurrentCulture) : "",
            Width = 110, Height = 32, FontFamily = Look.Mono, Background = Look.Field, BorderBrush = Look.LineStrong, Name = "reliefLimit",
            InnerRightContent = new TextBlock { Text = Loc.Ui("ms/m"), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }
        };
        ToolTip.SetTip(limit, Loc.T("Empty: the most relief of the blast. More than the limit is drawn in red."));
        limit.TextChanged += (_, _) =>
        {
            var text = (limit.Text ?? "").Trim().Replace(',', '.');
            double? value = text.Length == 0 ? null : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : relief.UpperLimit;
            relief.UpperLimit = planRelief.UpperLimit = _burdenReliefLimit = value;
            view.Redraw();
            vm.View.Redraw();
        };
        var limitGroup = Group("Upper limit", limit);

        // ---- isolines: the time between two lines, the one of the Visualization window
        var interval = new NumericUpDown
        {
            Value = (decimal)Math.Clamp(vm.Settings.IsochroneIntervalMs, 1, 1000), Minimum = 1, Maximum = 1000, Increment = 5, FormatString = "0",
            Width = 120, Height = 32, FontFamily = Look.Mono, VerticalContentAlignment = VerticalAlignment.Center, Name = "isolineInterval"
        };
        var intervalGroup = Group("Interval ms", interval);
        var under = new TextBlock { Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center, Name = "reliefUnder", MinWidth = 220, Margin = new Thickness(0, 0, 14, 0) };
        void Under(double? value) => under.Text = value is double v ? Loc.F("Under the cursor: {0} ms/m", v.ToString("0.0", CultureInfo.CurrentCulture)) : Loc.T("Move the cursor over the triangles");
        Under(null);
        view.PointerMoved += (_, e) =>
        {
            if (_analysisTab != BlastAnalysis.BurdenRelief || vm.Project is not { } pr) return;
            var at = e.GetPosition(view);
            Under(BurdenRelief.At(relief.Delaunay(pr), view.Camera.ToWorld(at.X, at.Y)));
        };
        view.PointerExited += (_, _) => Under(null);

        // ---- on the main plan: the analysis of the open tab, in place of any other analysis display
        var onPlan = new ToggleSwitch { IsChecked = _analysisOnPlan, OnContent = "", OffContent = "", Name = "onPlan", VerticalAlignment = VerticalAlignment.Center };
        IOverlay? shown = null;
        var placing = false;
        void OnPlan()
        {
            placing = true;
            try
            {
                if (shown != null) vm.ShowAnalysisOnPlan(shown, false);
                shown = _analysisOnPlan ? PlanLayer(_analysisTab) : null;
                if (shown != null) vm.ShowAnalysisOnPlan(shown, true);
            }
            finally { placing = false; }
        }
        onPlan.IsCheckedChanged += (_, _) =>
        {
            if (_analysisOnPlan == (onPlan.IsChecked == true)) return;
            _analysisOnPlan = onPlan.IsChecked == true;
            OnPlan();
        };
        // another analysis display put on the plan, or Clear analysis: the switch goes off with it
        void Replaced()
        {
            if (placing || shown == null || vm.AnalysisOverlays.Contains(shown)) return;
            shown = null;
            _analysisOnPlan = false;
            onPlan.IsChecked = false;
        }
        vm.OverlaysChanged += Replaced;

        var hideGeometry = new CheckBox { Content = Loc.Ui("Hide geometry"), IsChecked = _analysisHideGeometry, Name = "hideGeometry", VerticalAlignment = VerticalAlignment.Center };
        hideGeometry.IsCheckedChanged += (_, _) => { _analysisHideGeometry = hideGeometry.IsChecked == true; view.ZoomAll(); };
        var print = Command("print", "Print", "print");
        ToolTip.SetTip(print, Loc.T("The sheet of the open tab"));
        print.Click += (_, _) => Ui.Safe(() => _ = ReportPreviewWindow.Show(vm, ReportKind.FirstMovement, FirstMovementPage(vm.Settings.IsochroneIntervalMs, view.Camera.Yaw)));

        // ---- the tabs
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Name = "analysisTabs" };
        var tabButtons = new List<(BlastAnalysis Analysis, Button Button, TextBlock Text)>();
        foreach (var a in Enum.GetValues<BlastAnalysis>())
        {
            var text = new TextBlock { Text = AnalysisName(a), VerticalAlignment = VerticalAlignment.Center };
            var b = new Button
            {
                Content = Ui.Row(6, Look.Icon(a switch { BlastAnalysis.BurdenRelief => "chart", BlastAnalysis.Isolines => "iso", _ => "arrow" }, 15, Look.TextSoft), text),
                Height = 32, VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(14, 0),
                CornerRadius = new CornerRadius(6, 6, 0, 0), BorderBrush = Look.Accent, Name = "tab" + a, Tag = a
            };
            tabButtons.Add((a, b, text));
            tabs.Children.Add(b);
        }
        var tools = new WrapPanel();
        var header = new Border
        {
            Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 10, 14, 4),
            Child = tools
        };
        var strip = new Border { Background = Look.Ground, Padding = new Thickness(10, 6, 10, 0), Child = tabs };

        // ---- the footer: what the tab shows, and what to do with it
        var note = new TextBlock { FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 14, 0), Name = "analysisNote" };
        var left = new DockPanel();
        DockPanel.SetDock(under, Dock.Left);
        left.Children.Add(under);
        left.Children.Add(note);
        var actions = Ui.Row(14, hideGeometry, Ui.Row(8, onPlan, new TextBlock { Text = Loc.T("Show on plan"), Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center }), export, print);
        var bar = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        bar.Children.Add(actions);
        bar.Children.Add(left);
        var footer = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 8), Child = bar };

        // nothing to show on the tab: why, over the plan
        var empty = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Look.TextSoft, MaxWidth = 420, Name = "analysisEmpty" };
        var emptyBox = new Border
        {
            Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Child = empty
        };
        var plan = new Grid { Children = { view, emptyBox } };

        // what the tab says of the blast as it is now
        void Describe()
        {
            if (vm.Project is not { } pr) return;
            var none = HasAnalysis(pr, _analysisTab) ? null : NoFirstMovement(pr);
            emptyBox.IsVisible = none != null;
            empty.Text = none ?? "";
            note.Text = none != null ? "" : _analysisTab switch
            {
                BlastAnalysis.InitiationAngle => Loc.F("{0} holes", angles.Analysis(pr).Count) + "  ·  " + Loc.T("The angle at which each hole is initiated, from the times of its neighbours"),
                BlastAnalysis.BurdenRelief => ReliefNote(pr),
                BlastAnalysis.Isolines => Loc.F("{0} holes", pr.Holes.Count(h => h.FiringTimeMs.HasValue && !h.IsDummy)) + "  ·  " + Loc.F("Lines of equal firing time, every {0} ms", isolines.IntervalMs.ToString("0.#", CultureInfo.CurrentCulture)),
                _ => Loc.F("{0} arrows", arrows.Arrows(pr).Count) + "  ·  " + Loc.T("The arrows show where the rock moves first, from the firing times of the neighbouring holes")
            };
            ToolTip.SetTip(note, note.Text);
        }
        string ReliefNote(BlastProject pr)
        {
            var d = relief.Delaunay(pr);
            var (min, max) = BurdenRelief.Range(d, null);
            limit.Watermark = max.ToString("0.0", CultureInfo.CurrentCulture);
            return Loc.F("{0} triangles: {1} – {2} ms/m", BurdenRelief.Valid(d).Count(), min.ToString("0.0", CultureInfo.CurrentCulture), max.ToString("0.0", CultureInfo.CurrentCulture));
        }
        vm.ModelRefreshed += Describe;

        void Select(BlastAnalysis a)
        {
            if (a != _analysisTab) UsageLog.Track(UsageLog.Tab, a.ToString(), "blast analysis");
            _analysisTab = a;
            foreach (var (x, b, text) in tabButtons)
            {
                var active = x == a;
                b.Background = active ? Look.Panel : Brushes.Transparent;
                b.BorderThickness = new Thickness(0, active ? 2 : 0, 0, 0);
                text.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
                text.Foreground = active ? Look.Accent : Look.Muted;
            }
            tools.Children.Clear();
            if (a == BlastAnalysis.FirstMovement) tools.Children.Add(arrowGroup);
            if (a == BlastAnalysis.BurdenRelief) tools.Children.Add(limitGroup);
            if (a == BlastAnalysis.Isolines) tools.Children.Add(intervalGroup);
            tools.Children.Add(viewGroup);
            export.IsVisible = a == BlastAnalysis.FirstMovement;
            under.IsVisible = a == BlastAnalysis.BurdenRelief;
            Under(null);
            Describe();
            view.Redraw();
            if (_analysisOnPlan) OnPlan();
        }
        foreach (var (x, b, _) in tabButtons) b.Click += (_, _) => Select(x);
        interval.ValueChanged += (_, _) =>
        {
            if (interval.Value is not decimal v) return;
            isolines.IntervalMs = planIsolines.IntervalMs = vm.Settings.IsochroneIntervalMs = (double)v;
            Describe();
            view.Redraw();
            vm.View.Redraw();
        };
        Select(tab);
        _analysisSelect = Select;

        var dock = new DockPanel { Background = Look.Ground };
        DockPanel.SetDock(strip, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(strip);
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(plan);
        // "Zoom all" needs the plan's size, which it only has after its first layout (not yet when the window opens)
        var fitted = false;
        view.SizeChanged += (_, e) =>
        {
            if (fitted || e.NewSize.Width < 1 || e.NewSize.Height < 1) return;
            fitted = true;
            ResetOrientation();
        };
        var w = Dialogs.ShowWindow(BlastAnalysisTitle, dock, 920, 660);
        w.MinWidth = 600;
        w.MinHeight = 380;
        var select = (Action<BlastAnalysis>)Select;
        w.Closed += (_, _) =>
        {
            if (_analysisSelect == select) _analysisSelect = null;
            vm.DragPreview -= view.Redraw;
            vm.ModelRefreshed -= Describe;
            vm.OverlaysChanged -= Replaced;
            // the analysis goes from the main plan with its window; the switch is kept for the next time
            if (shown != null && vm.AnalysisOverlays.Contains(shown)) vm.ShowAnalysisOnPlan(shown, false);
        };
    }
}
