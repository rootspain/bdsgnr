using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.App.Views;

/// <summary>A control that paints with SkiaSharp (charts and diagrams in analysis windows).</summary>
public sealed class SkiaView : Control
{
    public Action<SKCanvas, float, float>? Paint { get; set; }

    public override void Render(DrawingContext context)
    {
        var w = (float)Bounds.Width;
        var h = (float)Bounds.Height;
        context.Custom(new Op(new Rect(Bounds.Size), c => Paint?.Invoke(c, w, h)));
    }

    private sealed class Op : ICustomDrawOperation
    {
        private readonly Action<SKCanvas> _draw;
        public Op(Rect b, Action<SKCanvas> d) { Bounds = b; _draw = d; }
        public Rect Bounds { get; }
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => false;

        public void Render(ImmediateDrawingContext context)
        {
            using var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>()?.Lease();
            if (lease == null) return;
            lease.SkCanvas.Save();
            lease.SkCanvas.ClipRect(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
            try { _draw(lease.SkCanvas); }
            catch (Exception ex)
            {
                // Drawing runs on the render thread where exceptions are lost: log it and show it in the chart.
                Log.Error("Chart drawing failed", ex);
                using var bg = new SKPaint { Color = Chart.Bg };
                lease.SkCanvas.DrawRect(0, 0, (float)Bounds.Width, (float)Bounds.Height, bg);
                using var p = new SKPaint { Color = new SKColor(0xFFFF8A80), TextSize = 13, IsAntialias = true };
                lease.SkCanvas.DrawText(Loc.T("Chart could not be drawn:") + " " + ex.Message, 12, 24, p);
            }
            lease.SkCanvas.Restore();
        }
    }
}

/// <summary>Analysis and inspection windows opened from the View and Calculations menus.</summary>
public static class AnalysisWindows
{
    internal static Control Table(string[] headers, IEnumerable<string[]> rows)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserSortColumns = true, GridLinesVisibility = DataGridGridLinesVisibility.All };
        for (int i = 0; i < headers.Length; i++)
        {
            var idx = i;
            grid.Columns.Add(new DataGridTextColumn { Header = Loc.T(headers[i]), Binding = new Avalonia.Data.Binding($"[{idx}]"), CustomSortComparer = new RowOrder(idx) });
        }
        grid.ItemsSource = rows.ToList();
        return grid;
    }

    /// <summary>Rows of a <see cref="Table"/> by one column: the cells are text, the numbers among them sort by value.</summary>
    private sealed class RowOrder(int column) : System.Collections.IComparer
    {
        public int Compare(object? a, object? b) => NaturalOrder.Instance.Compare((a as string[])?[column], (b as string[])?[column]);
    }

    internal static Control WithButtons(Control content, params (string Text, Action Action)[] buttons) => WithButtons(content, null, buttons);

    /// <summary>The buttons over the content, <paramref name="aside"/> at the right end of their row.</summary>
    internal static Control WithButtons(Control content, Control? aside, params (string Text, Action Action)[] buttons)
    {
        var dock = new DockPanel { Margin = new Thickness(8) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (t, a) in buttons)
        {
            var b = new Button { Content = Loc.T(t) };
            b.Click += (_, _) => { try { a(); } catch (Exception ex) { Dialogs.ShowError(ex); } };
            bar.Children.Add(b);
        }
        Control top = bar;
        if (aside != null)
        {
            DockPanel.SetDock(aside, Dock.Right);
            top = new DockPanel { Margin = bar.Margin, Children = { aside, bar } };
            bar.Margin = default;
        }
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(content);
        return dock;
    }

    private static Viewport SecondaryViewport(MainViewModel vm, Func<SceneOptions>? options = null)
    {
        var v = new Viewport { ProjectSource = () => vm.Project, OptionsSource = options ?? vm.BuildSceneOptions, LockPlan = false, SurfaceWireframe = vm.Settings.SurfaceWireframe };
        v.ViewMenu = () => new[] { MenuEntry.Item("Zoom all", v.ZoomAll) };
        vm.ModelRefreshed += v.Redraw;
        return v;
    }

    // ---- timing ------------------------------------------------------------------------

    public static void VisualizeTiming(MainViewModel vm)
    {
        // One timing window at a time: a second call only brings it forward (no second overlay or animation timer).
        if (Dialogs.ActivateExisting("Visualization")) return;
        var p = vm.Project!;
        Timing.Solve(p);
        var timed = p.Holes.Where(h => h.FiringTimeMs.HasValue).ToList();
        if (timed.Count == 0) { _ = Dialogs.Info(Loc.T("No hole is initiated. Add an initiation point and ties, or electronic times.")); return; }
        // what was chosen in the window last time, in any project
        var s = vm.Settings;
        var overlay = new TimingContourOverlay { IntervalMs = s.IsochroneIntervalMs };
        vm.ShowOverlay(overlay);
        var max = timed.Max(h => h.FiringTimeMs!.Value);
        var min = timed.Min(h => h.FiringTimeMs!.Value);
        var playbackEnd = max > min ? max : max + 1;
        var slider = new Slider { Minimum = min, Maximum = playbackEnd, Value = playbackEnd, MinWidth = 60, Height = 36, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
        // the text of the window is the size of its chips, to keep it small on the plan
        const double textSize = 12;
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 130, FontSize = textSize };
        var interval = new NumericUpDown { Value = (decimal)Math.Clamp(s.IsochroneIntervalMs, 1, 1000), Minimum = 1, Maximum = 1000, Width = 96, Height = 28, MinHeight = 28, FontSize = textSize, VerticalContentAlignment = VerticalAlignment.Center, FormatString = "0" };
        var contours = new CheckBox { Content = Loc.T("Isochrones"), IsChecked = s.ShowIsochrones, FontSize = textSize, MinHeight = 24 };
        var colour = new CheckBox { Content = Loc.T("Colour by time"), IsChecked = s.ColourHolesByTime, FontSize = textSize, MinHeight = 24 };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        // the whole blast takes the seconds chosen, however long it is; 0 is its own clock, 1 ms of firing per 1 ms
        var playbackSeconds = Math.Clamp(s.PlaybackSeconds, 0, 600);
        var clock = new System.Diagnostics.Stopwatch();
        // round buttons with a filled sign, the tip says what they do
        static Control Sign(string data, double size, IBrush fill) => new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data), Fill = fill, Width = size, Height = size, Stretch = Stretch.Uniform };
        static Button Round(string name) => new()
        {
            Name = name,
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(18),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var play = Round("play");
        play.Classes.Add("primary");
        void SetPlaying(bool isPlaying)
        {
            play.Content = Sign(isPlaying ? "M6 4h4v16H6z M14 4h4v16h-4z" : "M6 4l15 8-15 8z", 14, Look.OnAccent);
            ToolTip.SetTip(play, Loc.T(isPlaying ? "Pause" : "Play"));
        }
        SetPlaying(false);
        void Update()
        {
            vm.AnimationTime = Math.Min(slider.Value, max);
            overlay.AnimationTimeMs = vm.AnimationTime;
            overlay.IntervalMs = (double)(interval.Value ?? 25);
            overlay.Contours = contours.IsChecked == true;
            overlay.ColourHoles = colour.IsChecked == true;
            s.IsochroneIntervalMs = overlay.IntervalMs;
            s.ShowIsochrones = overlay.Contours;
            s.ColourHolesByTime = overlay.ColourHoles;
            var fired = timed.Count(h => h.FiringTimeMs <= vm.AnimationTime);
            label.Text = Loc.F("{0:0} ms — {1}/{2} holes", vm.AnimationTime, fired, timed.Count);
            vm.View.Redraw();
        }
        slider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Update(); };
        // the choices reach the settings file at once, not only when the window closes
        void UpdateAndSave() { Update(); s.Save(); }
        interval.ValueChanged += (_, _) => UpdateAndSave();
        contours.IsCheckedChanged += (_, _) => UpdateAndSave();
        colour.IsCheckedChanged += (_, _) => UpdateAndSave();
        timer.Tick += (_, _) =>
        {
            // real elapsed time, so a late tick does not slow the blast down
            var elapsed = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            slider.Value = Math.Min(playbackEnd, slider.Value + elapsed * Timing.PlaybackRate(playbackEnd - min, playbackSeconds));
            if (slider.Value >= playbackEnd)
            {
                timer.Stop();
                SetPlaying(false);
            }
        };
        play.Click += (_, _) =>
        {
            if (timer.IsEnabled)
            {
                timer.Stop();
                SetPlaying(false);
                return;
            }
            if (slider.Value >= playbackEnd) slider.Value = min;
            clock.Restart();
            timer.Start();
            SetPlaying(true);
        };
        var stop = Round("stop");
        stop.Content = Sign("M5 5h14v14H5z", 12, Look.TextSoft);
        stop.Background = Brushes.Transparent;
        stop.BorderBrush = Look.LineStrong;
        stop.BorderThickness = new Thickness(1);
        ToolTip.SetTip(stop, Loc.T("Stop"));
        stop.Click += (_, _) =>
        {
            timer.Stop();
            slider.Value = min;
            SetPlaying(false);
        };
        static string Seconds(double v) => v > 0 ? v.ToString("0.#", CultureInfo.CurrentCulture) : "";
        var seconds = new TextBox
        {
            Name = "playbackSeconds",
            Text = Seconds(playbackSeconds),
            Watermark = "—",
            Width = 70,
            Height = 28,
            MinHeight = 28,
            FontSize = textSize,
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            InnerRightContent = new TextBlock { Text = Loc.T("s"), FontSize = 11, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }
        };
        var secondsChips = new List<(Button Chip, double Value)>();
        void SetSeconds(double value)
        {
            s.PlaybackSeconds = playbackSeconds = value;
            s.Save();
            seconds.Text = Seconds(value);
            foreach (var (chip, v) in secondsChips) Ui.SetChip(chip, v == value);
        }
        void TakeSeconds()
        {
            var text = (seconds.Text ?? "").Trim().Replace(',', '.');
            if (text.Length == 0) SetSeconds(0);
            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) SetSeconds(Math.Clamp(v, 0, 600));
            else seconds.Text = Seconds(playbackSeconds);
        }
        seconds.LostFocus += (_, _) => TakeSeconds();
        seconds.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { TakeSeconds(); e.Handled = true; } };
        var speedRow = Ui.Row(6, new TextBlock { Text = Loc.T("Show the blast in"), FontSize = textSize, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) }, seconds);
        foreach (var value in new double[] { 0, 2, 5, 10, 15 })
        {
            var chip = Ui.Chip(value == 0 ? Loc.T("Natural") : Loc.F("{0} s", value), () => SetSeconds(value), value == playbackSeconds, value == 0 ? Loc.F("The blast's own time: {0:0} ms", playbackEnd - min) : null);
            chip.Tag = "playback:" + value;
            secondsChips.Add((chip, value));
            speedRow.Children.Add(chip);
        }
        Grid.SetColumn(slider, 1);
        var panel = new StackPanel
        {
            Margin = new Thickness(8),
            Spacing = 6,
            Children =
            {
                // the scrub bar takes what the buttons leave, so it shrinks with the window
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    ColumnSpacing = 8,
                    Children =
                    {
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { play, stop } },
                        slider
                    }
                },
                speedRow,
                // in a narrow window the time and the last options wrap to a second line
                new WrapPanel
                {
                    Children =
                    {
                        Spaced(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = Loc.T("Isochrone interval (ms)"), FontSize = textSize, VerticalAlignment = VerticalAlignment.Center }, interval } }),
                        Spaced(contours),
                        Spaced(colour),
                        Spaced(label)
                    }
                }
            }
        };
        static Control Spaced(Control c) { c.Margin = new Thickness(0, 0, 12, 0); c.VerticalAlignment = VerticalAlignment.Center; return c; }
        Update();
        // the title and three rows exactly; the height is fixed, so the rows must not wrap: the width stops before that
        const double width = 720, minWidth = 640, height = 142;
        Window? shown = null;
        var close = Ui.Flat(Look.Icon("close", 12, Look.Muted), Loc.T("Close"), () => shown?.Close(), 30, 22);
        close.Name = "titleClose";
        close.CornerRadius = new CornerRadius(0);
        close[DockPanel.DockProperty] = Dock.Right;
        // a title of its own, thinner than the system one: it moves the window and closes it
        var title = new DockPanel
        {
            Name = "titleBar",
            Height = 22,
            Background = Look.Panel,
            [DockPanel.DockProperty] = Dock.Top,
            Children = { new Border { Height = 1, Background = Look.LineStrong, [DockPanel.DockProperty] = Dock.Bottom }, close, new TextBlock { Text = Loc.T("Visualization"), FontSize = 11, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) } }
        };
        title.PointerPressed += (_, e) => { if (shown != null && e.GetCurrentPoint(title).Properties.IsLeftButtonPressed) shown.BeginMoveDrag(e); };
        // Windows adds the height of its own title to the window when that title goes after the window is shown
        var w = Dialogs.ShowWindow("Visualization", new DockPanel { Children = { title, panel } }, width, height, alignBottomToOwner: true, beforeShow: b =>
        {
            b.ExtendClientAreaToDecorationsHint = true;
            b.ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
            b.ExtendClientAreaTitleBarHeightHint = 0;
        });
        shown = w;
        Dialogs.FixHeight(w, minWidth, height);
        w.Closed += (_, _) => { timer.Stop(); vm.ClearOverlays(); s.Save(); };
    }

    public static void TimeEnvelope(MainViewModel vm)
    {
        var p = vm.Project!;
        Timing.Solve(p);
        var window = new NumericUpDown { Value = (decimal)p.Vibration.WindowMs, Minimum = 1, Maximum = 500, Width = 120, FormatString = "0" };
        var view = new SkiaView();
        var summary = new TextBlock { Margin = new Thickness(0, 6) };
        void Update()
        {
            // Read the control here, on the UI thread: Paint runs on the render thread and must not touch controls.
            var windowMs = (double)(window.Value ?? 8);
            var env = Timing.TimeEnvelope(p, windowMs);
            var empty = Loc.T("No firing times: add an initiation point and ties, or electronic times.");
            view.Paint = (c, w, h) =>
            {
                using var bg = new SKPaint { Color = Chart.Bg };
                c.DrawRect(0, 0, w, h, bg);
                if (env.Count == 0)
                {
                    using var t = new SKPaint { Color = new SKColor(0xFFC9CFD6), TextSize = 14, IsAntialias = true };
                    c.DrawText(empty, 20, h / 2, t);
                    return;
                }
                Reports.DrawEnvelopeChart(c, new SKRect(10, 10, w - 10, h - 10), env, windowMs, dark: true);
            };
            summary.Text = env.Count == 0 ? empty :Loc.F("Max holes per window: {0}; max charge per window: {1:0.0} kg at {2:0} ms", env.Max(e => e.Holes), env.Max(e => e.ChargeKg), env.OrderByDescending(e => e.ChargeKg).First().TimeMs);
            view.InvalidateVisual();
        }
        window.ValueChanged += (_, _) => Update();
        Update();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = Loc.T("Window (ms)"), VerticalAlignment = VerticalAlignment.Center }, window } };
        var dock = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(summary, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(summary);
        dock.Children.Add(view);
        Dialogs.ShowWindow("Time envelope", dock, 820, 460);
    }

    public static void LoggingReport(MainViewModel vm)
    {
        var p = vm.Project!;
        var rows = p.Holes.OrderBy(h => h.Label).Select(h => new[]
        {
            h.Label, h.DetonatorId, h.ElectronicTimeMs?.ToString("0") ?? "", h.ElectronicTimeManual ? "✓" : "", h.Exceptions == HoleException.None ? "" : Loc.Enum(h.Exceptions), Loc.Enum(h.State)
        });
        var content = Table(new[] { "Hole", "Detonator ID", "Programmed (ms)", "Manual", "Exceptions", "State" }, rows);
        var programmed = p.Holes.Count(h => h.ElectronicTimeMs.HasValue);
        var dock = new DockPanel { Margin = new Thickness(8) };
        var head = new TextBlock { Text = Loc.F("{0} holes, {1} programmed, {2} with exceptions, {3} without detonator ID", p.Holes.Count(), programmed, p.Holes.Count(h => h.Exceptions != HoleException.None), p.Holes.Count(h => h.ElectronicTimeMs.HasValue && string.IsNullOrEmpty(h.DetonatorId))) };
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(content);
        Dialogs.ShowWindow("Electronic logging report", dock, 760, 520);
    }

    // ---- quantities & vibration ------------------------------------------------------------

    public static void Quantities(MainViewModel vm, bool costs)
    {
        var p = vm.Project!;
        var subset = vm.Doc!.SelectedHoles.Any() ? vm.Doc.SelectedHoles : null;
        var q = Engine.Quantities.Compute(p, subset);
        var text = new SelectableTextBlock
        {
            Margin = new Thickness(8),
            FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
            Text = string.Join("\n", new[]
            {
                subset != null ? Loc.T("Selected holes only") : Loc.T("All holes"),
                Loc.F("Holes: {0} (+{1} dummy)", q.HoleCount, q.DummyCount),
                Loc.F("Drilled: {0:0.0} m", q.DrilledMetres),
                Loc.F("Average burden × spacing: {0:0.00} × {1:0.00} m", q.AverageBurden, q.AverageSpacing),
                Loc.F("Volume: {0:0} m³", q.Volume),
                Loc.F("Tonnes: {0:0} t", q.Tonnes),
                Loc.F("Explosive: {0:0.0} kg", q.ExplosiveKg),
                Loc.F("Powder factor: {0:0.000} kg/m³, {1:0.000} kg/t", q.PowderFactorKgM3, q.PowderFactorKgT),
                Loc.F("Energy factor: {0:0.00} MJ/t", q.EnergyFactorMJt),
                Loc.F("Max charge per {0} ms: {1:0.0} kg", p.Vibration.WindowMs, q.MaxInstantaneousCharge),
                costs ? "" : null,
                costs ? Loc.F("Products: {0:0.00} {1}", q.ProductCost, p.Costs.Currency) : null,
                costs ? Loc.F("Drilling: {0:0.00} {1}", q.DrillingCost, p.Costs.Currency) : null,
                costs ? Loc.F("Loading: {0:0.00} {1}", q.LoadingCost, p.Costs.Currency) : null,
                costs ? Loc.F("Fixed: {0:0.00} {1}", q.FixedCost, p.Costs.Currency) : null,
                costs ? Loc.F("Total: {0:0.00} {1} ({2:0.000}/t)", q.TotalCost, p.Costs.Currency, q.CostPerTonne) : null,
            }.Where(s => s != null))
        };
        var products = Table(new[] { "Product", "Amount", "Unit", "Cost" }, q.Products.Select(x => new[] { x.Product.Name, x.Amount.ToString("0.0"), x.Unit, x.Cost.ToString("0.00") }));
        var perHole = Table(new[] { "Hole", "Area m²", "Volume m³", "Tonnes", "kg", "Stemming m", "kg/m³" },
            q.PerHole.Select(x => new[] { x.Hole.Label, x.Area.ToString("0.0"), x.Volume.ToString("0"), x.Tonnes.ToString("0"), x.ExplosiveKg.ToString("0.0"), x.StemmingM.ToString("0.0"), x.Volume > 0 ? (x.ExplosiveKg / x.Volume).ToString("0.000") : "" }));
        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = Loc.T("Summary"), Content = new ScrollViewer { Content = text } },
                new TabItem { Header = Loc.T("Products"), Content = products },
                new TabItem { Header = Loc.T("Per hole"), Content = perHole }
            }
        };
        Dialogs.ShowWindow(costs ? "Project costs" : "Quantities", WithButtons(tabs, ("Export CSV…", () => _ = vm.ExportQuantities()), ("Edit costs…", () => _ = vm.EditCostSchema())), 760, 560);
    }

    public static void VibrationPrediction(MainViewModel vm)
    {
        var p = vm.Project!;
        Timing.Solve(p);
        var monitors = p.Entities.OfType<PointEntity>().Where(x => x.Kind == PointKind.Monitoring).ToList();
        var rows = monitors.Select(m =>
        {
            var r = Vibration.Predict(p, m.Position);
            return new[] { m.Label, r.DistanceM.ToString("0"), r.ChargeKg.ToString("0.0"), r.Ppv.ToString("0.00"), r.AirblastDb.ToString("0.0"), Vibration.AllowableCharge(p.Vibration, r.DistanceM, p.Vibration.PpvLimit).ToString("0.0"), r.Ppv > p.Vibration.PpvLimit ? "⚠" : "" };
        }).ToList();
        var info = new TextBlock
        {
            Text = monitors.Count == 0 ? Loc.T("Add monitoring points (Survey tools) to predict vibration at receivers.") :
                Loc.F("PPV = {0} · (D/√Q)^-{1};  limit {2} mm/s;  window {3} ms", p.Vibration.SiteK, p.Vibration.SiteB, p.Vibration.PpvLimit, p.Vibration.WindowMs),
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap
        };
        var dock = new DockPanel();
        DockPanel.SetDock(info, Dock.Top);
        dock.Children.Add(info);
        dock.Children.Add(Table(new[] { "Monitor", "Distance m", "Charge kg", "PPV mm/s", "Airblast dB", "Allowable kg", "" }, rows));
        Dialogs.ShowWindow("Vibration", WithButtons(dock, ("Site constants…", () => _ = vm.EditVibrationModel()), ("Vibration map", () => vm.VibrationMap(false)), ("Airblast map", () => vm.VibrationMap(true))), 760, 420);
    }

    /// <summary>Loads a measured vibration trace (CSV: time, value[, value…]) and fits site constants from peaks.</summary>
    public static void VibrationAnalysis(MainViewModel vm)
    {
        var traces = new List<(string Name, List<(double T, double V)> Points)>();
        double offset = 0;
        var view = new SkiaView();
        var info = new TextBlock { Margin = new Thickness(0, 6), TextWrapping = TextWrapping.Wrap };
        void Repaint()
        {
            view.Paint = (c, w, h) =>
            {
                using var bg = new SKPaint { Color = Chart.Bg };
                c.DrawRect(0, 0, w, h, bg);
                if (traces.Count == 0) return;
                var all = traces.SelectMany(t => t.Points).ToList();
                var t0 = all.Min(x => x.T) + offset;
                var t1 = all.Max(x => x.T);
                var vmax = Math.Max(1e-9, all.Max(x => Math.Abs(x.V)));
                var colors = new[] { new SKColor(0xFF4C9BFF), new SKColor(0xFFFF6B6B), new SKColor(0xFF4ADE80), new SKColor(0xFFF0A63A) };
                using var axis = new SKPaint { Color = SKColors.Gray, StrokeWidth = 1 };
                c.DrawLine(40, h / 2, w - 10, h / 2, axis);
                for (int i = 0; i < traces.Count; i++)
                {
                    using var pen = new SKPaint { Color = colors[i % colors.Length], StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke };
                    using var path = new SKPath();
                    bool first = true;
                    foreach (var (t, v) in traces[i].Points)
                    {
                        var x = 40 + (float)((t - t0) / Math.Max(1e-9, t1 - t0)) * (w - 50);
                        var y = h / 2 - (float)(v / vmax) * (h / 2 - 10);
                        if (first) { path.MoveTo(x, y); first = false; } else path.LineTo(x, y);
                    }
                    c.DrawPath(path, pen);
                }
            };
            var peaks = traces.Select(t => $"{t.Name}: peak {t.Points.Max(x => Math.Abs(x.V)):0.00} at {t.Points.OrderByDescending(x => Math.Abs(x.V)).First().T:0.000}").ToList();
            info.Text = string.Join("   ", peaks);
            view.InvalidateVisual();
        }
        async void Load()
        {
            var path = await Dialogs.OpenFile("Load vibration trace", ("CSV", new[] { "*.csv", "*.txt" }));
            if (path == null) return;
            var lines = await File.ReadAllLinesAsync(path);
            var parsed = lines.Select(l => l.Split(',', ';', '\t')).Where(a => a.Length >= 2 && double.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _)).ToList();
            var columns = parsed.Count > 0 ? parsed[0].Length - 1 : 0;
            for (int c = 1; c <= columns; c++)
                traces.Add(($"{Path.GetFileNameWithoutExtension(path)} [{c}]", parsed.Select(a => (double.Parse(a[0], CultureInfo.InvariantCulture), double.TryParse(a[c], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0)).ToList()));
            Repaint();
        }
        async void SetOffset()
        {
            if (await Dialogs.PromptNumber("Set as offset", Loc.T("Time offset (s)"), offset) is double v) { offset = v; Repaint(); }
        }
        async void FitSite()
        {
            var p = vm.Project!;
            var monitors = p.Entities.OfType<PointEntity>().Where(x => x.Kind == PointKind.Monitoring).ToList();
            if (traces.Count == 0 || monitors.Count == 0) { await Dialogs.Info(Loc.T("Load a trace and add the monitoring point first.")); return; }
            var mic = Timing.MaxInstantaneousCharge(p, p.Vibration.WindowMs);
            var d = p.Holes.Min(h => Vec3.Distance(h.Collar, monitors[0].Position));
            var peak = traces.Max(t => t.Points.Max(x => Math.Abs(x.V)));
            var k = peak / Math.Pow(d / Math.Sqrt(Math.Max(1e-6, mic)), -p.Vibration.SiteB);
            if (await Dialogs.Confirm(Loc.F("Scaled distance {0:0.0}, measured {1:0.00}. Site constant K = {2:0}. Save it to the project?", d / Math.Sqrt(Math.Max(1e-6, mic)), peak, k)))
                vm.Change("Vibration site constants", pr => pr.Vibration.SiteK = k);
        }
        Repaint();
        var dock = new DockPanel();
        DockPanel.SetDock(info, Dock.Bottom);
        dock.Children.Add(info);
        dock.Children.Add(view);
        Dialogs.ShowWindow("Vibration analysis", WithButtons(dock, ("Load trace…", Load), ("Set as offset…", SetOffset), ("Fit site constant", FitSite), ("Clear", () => { traces.Clear(); Repaint(); })), 900, 500);
    }

    // ---- inspection views --------------------------------------------------------------

    public static void Exceptions(MainViewModel vm)
    {
        var p = vm.Project!;
        Timing.Solve(p);
        var rows = new List<string[]>();
        var holeOf = new Dictionary<string[], Guid>();
        foreach (var h in p.Holes.Where(h => !h.IsDummy).OrderBy(h => h.Label, NaturalOrder.Instance))
        {
            var issues = new List<string>();
            if (h.Exceptions != HoleException.None) issues.Add(Loc.Enum(h.Exceptions));
            if (h.Decks.Count == 0) issues.Add(Loc.T("Not loaded"));
            issues.AddRange(Charging.Validate(p, h).Select(Loc.T));
            if (!h.FiringTimeMs.HasValue) issues.Add(Loc.T("Not initiated"));
            if (h.ActualLength is double a && Math.Abs(a - h.Length) > 0.5) issues.Add(Loc.F("Dipped {0:0.0} vs design {1:0.0}", a, h.Length));
            if (issues.Count == 0) continue;
            var row = new[] { h.Label, string.Join("; ", issues) };
            rows.Add(row);
            holeOf[row] = h.Id;
        }
        var content = (DataGrid)Table(new[] { "Hole", "Problem" }, rows);
        // a view of the rows: the search filters them and the order chosen by a column stays
        var shown = new Avalonia.Collections.DataGridCollectionView(rows);
        content.ItemsSource = shown;
        var search = new TextBox { Watermark = Loc.T("Filter by label…"), Width = 200, Name = "exceptionsSearch" };
        search.TextChanged += (_, _) =>
        {
            var text = (search.Text ?? "").Trim();
            shown.Filter = text.Length == 0 ? null : r => ((string[])r)[0].Contains(text, StringComparison.OrdinalIgnoreCase);
            content.SelectedItems.Clear();
        };
        // the rows chosen in the table; with none chosen all those shown
        List<Guid> Chosen()
        {
            var chosen = content.SelectedItems.OfType<string[]>().ToList();
            return (chosen.Count > 0 ? chosen : shown.Cast<string[]>()).Select(r => holeOf[r]).ToList();
        }
        Dialogs.ShowWindow("Exceptions view", WithButtons(content, search, ("Select these holes", () => vm.Doc!.Select(Chosen())), ("Highlight", () => vm.SetHighlight(Chosen()))), 640, 480);
    }

    /// <summary>Front row: true burden of each front-row hole to the crest line.</summary>
    public static void FrontRow(MainViewModel vm)
    {
        var p = vm.Project!;
        var crest = p.Entities.OfType<PolylineEntity>().FirstOrDefault(l => l.Kind == LineKind.Crest) ?? vm.Doc!.SelectedEntities.OfType<PolylineEntity>().FirstOrDefault();
        var front = p.Rows.Count > 0
            ? p.Rows.Select(r => r.HoleIds.Select(p.FindEntity).OfType<Hole>().ToList()).Where(r => r.Count > 0)
                .OrderBy(r => crest == null ? 0 : r.Average(h => GeoMath.DistanceToPolyline2D(crest.Points, h.Collar, crest.Closed))).First()
            : p.Holes.ToList();
        var rows = front.Select(h =>
        {
            var d = crest == null ? double.NaN : GeoMath.DistanceToPolyline2D(crest.Points, h.Collar, crest.Closed);
            var toe = crest == null ? double.NaN : GeoMath.DistanceToPolyline2D(crest.Points, h.Toe, crest.Closed);
            return new[] { h.Label, double.IsNaN(d) ? "" : d.ToString("0.00"), double.IsNaN(toe) ? "" : toe.ToString("0.00"), h.Angle.ToString("0"), h.Bearing.ToString("0"), h.EffectiveLength.ToString("0.0"), (!double.IsNaN(d) && d < p.Design.Burden * 0.7) ? "⚠ " + Loc.T("Low burden") : "" };
        });
        var note = new TextBlock { Text = crest == null ? Loc.T("Draw or select a crest polyline (kind Crest) to measure front-row burden.") : Loc.F("Crest: {0}", crest.Label), Margin = new Thickness(0, 0, 0, 6) };
        var dock = new DockPanel();
        DockPanel.SetDock(note, Dock.Top);
        dock.Children.Add(note);
        dock.Children.Add(Table(new[] { "Hole", "Burden at collar", "Burden at toe", "Angle", "Bearing", "Length", "" }, rows));
        Dialogs.ShowWindow("Front row view", dock, 720, 420);
    }

    public static void RowCheck(MainViewModel vm)
    {
        var p = vm.Project!;
        var rows = p.Rows.Select(r =>
        {
            var holes = r.HoleIds.Select(p.FindEntity).OfType<Hole>().ToList();
            var (min, max, mean) = HoleDesign.RowSpacing(p, r);
            return new[] { r.Name, holes.Count.ToString(), mean.ToString("0.00"), min.ToString("0.00"), max.ToString("0.00"), holes.Count > 0 ? holes.Average(h => h.EffectiveLength).ToString("0.0") : "", holes.Count > 0 ? holes.Sum(h => Charging.ExplosiveMass(p, h)).ToString("0") : "" };
        });
        Dialogs.ShowWindow("Row check view", WithButtons(Table(new[] { "Row", "Holes", "Mean spacing", "Min", "Max", "Mean length", "kg" }, rows), ("Create rows automatically", vm.AutoRows), ("Optimize row spacing", vm.OptimizeRows)), 720, 420);
    }

    public static void Markout(MainViewModel vm)
    {
        var p = vm.Project!;
        var stations = p.Entities.OfType<PointEntity>().Where(x => x.Kind == PointKind.SurveyStation).ToList();
        var station = vm.Doc!.SelectedEntities.OfType<PointEntity>().FirstOrDefault(x => x.Kind == PointKind.SurveyStation) ?? stations.FirstOrDefault();
        var rows = SelectedOrAll(vm).OrderBy(h => h.Label).Select(h => new[]
        {
            h.Label, h.Collar.X.ToString("0.000"), h.Collar.Y.ToString("0.000"), h.Collar.Z.ToString("0.000"),
            station == null ? "" : Vec3.Distance2D(station.Position, h.Collar).ToString("0.000"),
            station == null ? "" : GeoMath.Bearing(station.Position, h.Collar).ToString("0.000"),
            h.Angle.ToString("0"), h.Bearing.ToString("0"), h.Length.ToString("0.0")
        });
        var note = new TextBlock { Text = station == null ? Loc.T("Add a survey station to see distances and bearings for set-out.") : Loc.F("From station {0} ({1})", station.Label, station.Position), Margin = new Thickness(0, 0, 0, 6) };
        var dock = new DockPanel();
        DockPanel.SetDock(note, Dock.Top);
        dock.Children.Add(note);
        dock.Children.Add(Table(new[] { "Hole", "X", "Y", "Z", "Distance", "Bearing", "Angle", "Hole bearing", "Length" }, rows));
        Dialogs.ShowWindow("Markout", dock, 860, 520);
    }

    private static IEnumerable<Hole> SelectedOrAll(MainViewModel vm) => vm.Doc!.SelectedHoles.Any() ? vm.Doc.SelectedHoles : vm.Project!.Holes;

    public static void SurveyStations(MainViewModel vm)
    {
        var p = vm.Project!;
        var rows = p.Entities.OfType<PointEntity>().Where(x => x.Kind == PointKind.SurveyStation).Select(s => new[] { s.Label, s.Position.X.ToString("0.000"), s.Position.Y.ToString("0.000"), s.Position.Z.ToString("0.000") });
        Dialogs.ShowWindow("Survey stations", WithButtons(Table(new[] { "Station", "X", "Y", "Z" }, rows), ("Add station", () => vm.DrawPointTool(PointKind.SurveyStation)), ("Markout", () => Markout(vm))), 520, 360);
    }

    public static void SingleHole(MainViewModel vm)
    {
        var h = vm.Doc!.SelectedHoles.FirstOrDefault();
        if (h == null) { _ = Dialogs.Info(Loc.T("Select a hole first.")); return; }
        _ = vm.EditHoleLoading(h);
    }

    // ---- first movement ---------------------------------------------------------------

    static double _firstMovementArrowSize = BlastDesigner.Engine.FirstMovement.DefaultArrowSize;
    static bool _firstMovementGradient;
    static FirstMovementOverlay? _firstMovementOpen;
    static bool _firstMovementOnPlan;

    /// <summary>
    /// "First Movement" window: its own plan with the arrows,
    /// arrow size ±0.2 m (0.2…10), reset orientation, zoom all and the X, Y, dirX, dirY export. The same arrows can be
    /// shown on the main plan.
    /// </summary>
    public static void FirstMovementWindow(MainViewModel vm)
    {
        // already open: bring it forward and put its arrows back on the plan if another analysis replaced them
        if (Dialogs.ActivateExisting("First movement"))
        {
            if (_firstMovementOpen is { } open && _firstMovementOnPlan) vm.ShowFirstMovementOnPlan(open, true);
            return;
        }
        var p = vm.Project!;
        Timing.Solve(p);
        var overlay = new FirstMovementOverlay { ArrowSize = _firstMovementArrowSize, Gradient = _firstMovementGradient, LegendTitle = Loc.T("Colour by direction") };
        if (overlay.Arrows(p).Count == 0)
        {
            _ = Dialogs.Info(Loc.T(BlastDesigner.Engine.FirstMovement.AllAtOnce(p) ? "All holes fire at the same time, the direction cannot be determined." : "No hole is initiated. Add an initiation point and ties, or electronic times."));
            return;
        }
        var view = SecondaryViewport(vm, () =>
        {
            var o = vm.BuildSceneOptions();
            // a drag on the main plan shows here too: the dragged holes and the arrows from their new places
            var drag = vm.View.Preview;
            return new SceneOptions
            {
                Selection = o.Selection, Background = o.Background, SelectionColor = o.SelectionColor, CollarPixels = o.CollarPixels, DrawSummaryBlock = false, Overlays = new IOverlay[] { overlay },
                MovedEntities = drag.Moving is { Count: > 0 } moving ? new HashSet<Guid>(moving) : null, MoveOffset = drag.MoveOffset
            };
        });
        vm.DragPreview += view.Redraw;
        var arrowCount = overlay.Arrows(p).Count;
        var size = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 54, TextAlignment = TextAlignment.Center, FontFamily = Look.Mono, Foreground = Look.Text, Name = "arrowSize" };
        var shapeChips = new List<Button>();
        var colourChips = new List<Button>();
        void Refresh()
        {
            _firstMovementArrowSize = overlay.ArrowSize;
            _firstMovementGradient = overlay.Gradient;
            for (var i = 0; i < colourChips.Count; i++) Ui.SetChip(colourChips[i], overlay.Gradient == (i == 1));
            size.Text = Loc.F("{0:0.0} m", overlay.ArrowSize);
            for (var i = 0; i < shapeChips.Count; i++) Ui.SetChip(shapeChips[i], (int)overlay.Shape == i);
            view.Redraw();
            vm.View.Redraw();
        }
        void ResetOrientation()
        {
            view.Camera.Pitch = 90;
            view.Camera.Yaw = p.Header.BlastOrientation;
            view.ZoomAll();
        }
        void Resize(double step)
        {
            overlay.ArrowSize = Math.Clamp(Math.Round(overlay.ArrowSize + step, 1), 0.2, 10);
            Refresh();
        }

        static TextBlock Caption(string text) => new() { Text = Loc.T(text).ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Faint, Margin = new Thickness(2, 0, 0, 5) };
        static Control Group(string caption, Control content) => new StackPanel { Margin = new Thickness(0, 0, 22, 6), Children = { Caption(caption), content } };
        static Control IconText(string icon, string text) => Ui.Row(6, Look.Icon(icon, 16, Look.TextSoft), new TextBlock { Text = Loc.T(text), VerticalAlignment = VerticalAlignment.Center });

        // shape: one segmented row of chips
        var shapes = Ui.Row(6);
        foreach (var (shapeName, i) in new[] { "Arrows", "Triangles" }.Select((n, i) => (n, i)))
        {
            var chip = Ui.Chip(Loc.T(shapeName), () => { overlay.Shape = (FirstMovementOverlay.ArrowShape)i; Refresh(); }, (int)overlay.Shape == i);
            chip.Height = 32;
            chip.Tag = "shape:" + i;
            shapeChips.Add(chip);
            shapes.Children.Add(chip);
        }

        // colour: one for all the arrows, or the colour of each direction with its legend on the plan
        var colours = Ui.Row(6);
        foreach (var (colourName, gradient) in new[] { ("One colour", false), ("Gradient", true) })
        {
            var chip = Ui.Chip(Loc.T(colourName), () => { overlay.Gradient = gradient; Refresh(); }, overlay.Gradient == gradient);
            chip.Height = 32;
            chip.Tag = "colour:" + (gradient ? 1 : 0);
            colourChips.Add(chip);
            colours.Children.Add(chip);
        }

        // arrow size: − value + in one field-like pill
        var smaller = Ui.Flat(new TextBlock { Text = "−", FontSize = 16 }, Loc.T("Decrease arrow"), () => Resize(-0.2), 32, 30);
        var bigger = Ui.Flat(new TextBlock { Text = "+", FontSize = 16 }, Loc.T("Increase arrow"), () => Resize(0.2), 32, 30);
        smaller.Name = "smaller";
        bigger.Name = "bigger";
        var stepper = new Border
        {
            Background = Look.Field,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(1),
            Child = Ui.Row(0, smaller, size, bigger)
        };
        stepper.PointerWheelChanged += (_, e) => { Resize(e.Delta.Y > 0 ? 0.2 : -0.2); e.Handled = true; };

        var reset = Ui.Flat(IconText("north", "Reset orientation"), Loc.T("Plan turned to the blast orientation, everything in view"), ResetOrientation, height: 32);
        var fit = Ui.Flat(IconText("fit", "Zoom all"), null, view.ZoomAll, height: 32);
        reset.Name = "reset";
        fit.Name = "fit";
        var viewGroup = new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(1), Child = Ui.Row(0, reset, fit) };

        var onPlan = new ToggleSwitch { IsChecked = true, OnContent = "", OffContent = "", Name = "onPlan", VerticalAlignment = VerticalAlignment.Center };
        // Same calculation on the main plan, drawn in its usual arrow style (orange line arrows).
        var planOverlay = new FirstMovementOverlay { PlanStyle = true };
        onPlan.IsCheckedChanged += (_, _) => { _firstMovementOnPlan = onPlan.IsChecked == true; vm.ShowFirstMovementOnPlan(planOverlay, _firstMovementOnPlan); };
        (_firstMovementOpen, _firstMovementOnPlan) = (planOverlay, true);
        vm.ShowFirstMovementOnPlan(planOverlay, true);
        var export = new Button
        {
            Content = IconText("export", "Export CSV…"),
            Height = 32,
            Padding = new Thickness(12, 0),
            CornerRadius = new CornerRadius(8),
            VerticalContentAlignment = VerticalAlignment.Center,
            Name = "export"
        };
        ToolTip.SetTip(export, Loc.T("X, Y and the direction of every arrow"));
        export.Click += async (_, _) =>
        {
            var path = await Dialogs.SaveFile("First movement", "first-movement.csv", "csv", ("CSV", new[] { "*.csv" }));
            if (path != null) File.WriteAllText(path, BlastDesigner.Engine.FirstMovement.ToCsv(overlay.Arrows(vm.Project!)) + Environment.NewLine);
        };

        var header = new Border
        {
            Background = Look.Panel,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 10, 14, 4),
            Child = new WrapPanel { Children = { Group("Shape", shapes), Group("Color", colours), Group("Arrow size", stepper), Group("View", viewGroup) } }
        };

        // action bar: what the arrows are, and what to do with them
        var actions = Ui.Row(14, Ui.Row(8, onPlan, new TextBlock { Text = Loc.T("Show on plan"), Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center }), export);
        var note = new TextBlock
        {
            Text = Loc.F("{0} arrows", arrowCount) + "  ·  " + Loc.T("The arrows show where the rock moves first, from the firing times of the neighbouring holes"),
            FontSize = 11.5,
            Foreground = Look.Faint,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 14, 0)
        };
        ToolTip.SetTip(note, note.Text);
        var bar = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        bar.Children.Add(actions);
        bar.Children.Add(note);
        var footer = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 8), Child = bar };

        var dock = new DockPanel { Background = Look.Ground };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(view);
        Refresh();
        // "Zoom all" needs the plan's size, which it only has after its first layout (not yet when the window opens)
        var fitted = false;
        view.SizeChanged += (_, e) =>
        {
            if (fitted || e.NewSize.Width < 1 || e.NewSize.Height < 1) return;
            fitted = true;
            ResetOrientation();
        };
        var w = Dialogs.ShowWindow("First movement", dock, 900, 640);
        w.MinWidth = 560;
        w.MinHeight = 360;
        w.Closed += (_, _) => { if (_firstMovementOpen == planOverlay) _firstMovementOpen = null; vm.DragPreview -= view.Redraw; };
    }

    // ---- secondary viewports -----------------------------------------------------------

    public static void SectionTool(MainViewModel vm)
    {
        vm.SetTool(new PickPointsTool(vm, "Cross-section", "Click two points of the section line.", 2, 2, false, pts =>
        {
            ShowSection(vm, pts[0], pts[1]);
            vm.SelectTool();
        }) { Repeat = false });
    }

    public static void ShowSection(MainViewModel vm, Vec3 a, Vec3 b)
    {
        var width = new NumericUpDown { Value = (decimal)Math.Max(2, vm.Project!.Design.Spacing), Minimum = 0.5m, Maximum = 500, Width = 120, FormatString = "0.#" };
        Viewport? view = null;
        view = SecondaryViewport(vm, () =>
        {
            var o = vm.BuildSceneOptions();
            return new SceneOptions { Selection = o.Selection, Background = o.Background, CollarPixels = o.CollarPixels, DrawSummaryBlock = false, SliceHalfWidth = (double)(width.Value ?? 5) };
        });
        var mid = Vec3.Lerp(a, b, 0.5);
        var holesZ = vm.Project.Holes.Any() ? vm.Project.Holes.Average(h => h.Collar.Z - h.Length / 2) : mid.Z;
        view.Camera.Target = mid.WithZ(holesZ);
        view.Camera.Pitch = 0;
        view.Camera.Yaw = GeoMath.Bearing(a, b) - 90;
        width.ValueChanged += (_, _) => view.Redraw();
        var fit = new Button { Content = Loc.T("Zoom all") };
        fit.Click += (_, _) =>
        {
            var bb = Bounds3.Empty;
            foreach (var e in vm.Project.Entities.Where(e => e is Hole)) foreach (var v in e.GetVertices()) if (Math.Abs(view.Camera.Depth(v)) <= (double)(width.Value ?? 5)) bb.Add(v);
            if (!bb.IsEmpty) view.Camera.Fit(bb, 0.1);
            view.Redraw();
        };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(6), Children = { new TextBlock { Text = Loc.T("Half width (m)"), VerticalAlignment = VerticalAlignment.Center }, width, fit } };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(view);
        var w = Dialogs.ShowWindow("Section view", dock, 900, 520);
        w.Opened += (_, _) => fit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    public static void HorizontalSlice(MainViewModel vm)
    {
        var p = vm.Project!;
        var zs = p.Holes.SelectMany(h => new[] { h.Collar.Z, h.Toe.Z }).DefaultIfEmpty(0).ToList();
        var level = new Slider { Minimum = zs.Min(), Maximum = zs.Max() + 0.01, Value = (zs.Min() + zs.Max()) / 2, Width = 300 };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var view = SecondaryViewport(vm, () =>
        {
            var o = vm.BuildSceneOptions();
            return new SceneOptions { Selection = o.Selection, Background = o.Background, CollarPixels = o.CollarPixels, DrawSummaryBlock = false, SliceHalfWidth = 0.5 };
        });
        view.Camera.Pitch = 90;
        void Update()
        {
            view.Camera.Target = view.Camera.Target.WithZ(level.Value);
            label.Text = $"Z = {level.Value:0.00}";
            view.Redraw();
        }
        level.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Update(); };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(6), Children = { new TextBlock { Text = Loc.T("Elevation"), VerticalAlignment = VerticalAlignment.Center }, level, label } };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(view);
        var w = Dialogs.ShowWindow("Horizontal slice", dock, 800, 600);
        w.Opened += (_, _) => { view.ZoomAll(); Update(); };
    }

    public static void Overview(MainViewModel vm)
    {
        var view = SecondaryViewport(vm);
        var w = Dialogs.ShowWindow("Overview", view, 360, 300);
        w.Opened += (_, _) => view.ZoomAll();
        w.Topmost = true;
    }

    public static void FlyThrough(MainViewModel vm)
    {
        var view = SecondaryViewport(vm);
        view.Camera.Pitch = 30;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => { view.Camera.Orbit(0.6, 0); view.CameraMoving(); view.Redraw(); };
        var play = new Button { Content = "▶ / ■" };
        play.Click += (_, _) => { if (timer.IsEnabled) timer.Stop(); else timer.Start(); };
        var pitch = new Slider { Minimum = 5, Maximum = 90, Value = 30, Width = 200 };
        pitch.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) { view.Camera.Pitch = pitch.Value; view.Redraw(); } };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(6), Children = { play, new TextBlock { Text = Loc.T("Elevation angle"), VerticalAlignment = VerticalAlignment.Center }, pitch, new TextBlock { Text = Loc.T("Shift+middle drag or Alt+drag to orbit, wheel to zoom"), Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center } } };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(view);
        var w = Dialogs.ShowWindow("Fly through", dock, 900, 640);
        w.Opened += (_, _) => { view.ZoomAll(); timer.Start(); };
        w.Closed += (_, _) => timer.Stop();
    }

    // ---- managers ------------------------------------------------------------------------

    public static void AttachmentManager(MainViewModel vm)
    {
        var host = new ContentControl();
        void Refresh()
        {
            var p = vm.Project!;
            host.Content = Table(new[] { "File", "Size", "Added", "Description" }, p.Attachments.Select(a => new[] { a.FileName, $"{a.Data.Length / 1024.0:0.0} KB", a.Added.ToString("g"), a.Description }));
        }
        async void Add()
        {
            var files = await Dialogs.OpenFiles("Add attachment", true);
            if (files.Count == 0) return;
            var items = files.Select(f => new Attachment { FileName = Path.GetFileName(f), Data = File.ReadAllBytes(f) }).ToList();
            vm.Change("Add attachment", p => p.Attachments.AddRange(items));
            Refresh();
        }
        async void Open()
        {
            var p = vm.Project!;
            if (p.Attachments.Count == 0) return;
            var idx = await Dialogs.Choose("Open attachment", Loc.T("Attachment"), p.Attachments.Select(a => a.FileName).ToList());
            if (idx is not int i) return;
            var a = p.Attachments[i];
            var path = Path.Combine(Path.GetTempPath(), "BlastDesigner", a.Id.ToString("N"), a.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, a.Data);
            MainViewModel.OpenExternal(path);
        }
        async void Remove()
        {
            var p = vm.Project!;
            if (p.Attachments.Count == 0) return;
            var idx = await Dialogs.Choose("Remove attachment", Loc.T("Attachment"), p.Attachments.Select(a => a.FileName).ToList());
            if (idx is not int i) return;
            var id = p.Attachments[i].Id;
            vm.Change("Remove attachment", pr => pr.Attachments.RemoveAll(a => a.Id == id));
            Refresh();
        }
        Refresh();
        Dialogs.ShowWindow("Attachment manager", WithButtons(host, ("Add…", Add), ("Open…", Open), ("Remove…", Remove)), 640, 400);
    }

    // ---- help ------------------------------------------------------------------------------

    public static void About() => _ = Dialogs.Info(
        $"Blast Designer {typeof(AnalysisWindows).Assembly.GetName().Version}\n" +
        Loc.T("Open-pit blast design: hole layout, charging, initiation timing, quantities, vibration and reports.") + "\n\n" +
        $".NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n" +
        Loc.F("Settings: {0}", ProgramSettings.SettingsPath), Loc.T("About"));

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines.Select(Loc.T));

    public static void KeyboardHelp() => _ = Dialogs.Info(Lines(
        "Mouse: wheel zooms at the cursor, middle button pans, Shift+middle or Alt+left orbits in 3D, right click opens the tool menu.",
        "Select tool: click selects, drag a box (left to right = window, right to left = crossing), Shift adds, Ctrl toggles, drag a selected object to move it, double-click edits.",
        "Keys: Esc cancels the tool, Enter finishes a line, Backspace removes the last point, F zooms all, Delete deletes the selection, F1 opens help.",
        "Ctrl+N/O/S new/open/save, Ctrl+Z/Y undo/redo, Ctrl+X/C/V clipboard, Ctrl+A select all, Ctrl+E edit selected, Ctrl+P reports."), Loc.T("Keyboard and mouse"));

    public static void Help() => _ = Dialogs.Info(Lines(
        "Typical workflow:",
        "1. File ▸ New, fill in the blast header.",
        "2. Import survey data (DXF, text) or draw the boundary and crest.",
        "3. Edit ▸ Design data: burden, spacing, bench height, diameter, subdrill.",
        "4. Blast tools ▸ Pattern (or Row / Hole) to lay out holes; build surfaces for collars and grade.",
        "5. Edit ▸ Available resources and Tools ▸ Auto loading ▸ Edit loading rules, then Apply.",
        "6. Loading tools ▸ Lead-in and Tie (or Automatic tie-up), or electronic tools for programmed times.",
        "7. Calculations: Visualize timing, Time envelope, Burden relief, Quantities, Vibration.",
        "8. File ▸ Print ▸ Reports / Plan, and File ▸ Export for drill rigs and other software."), Loc.T("Contents"));
}
