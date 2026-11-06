using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.App.Views;

/// <summary>What a preview window can show.</summary>
public enum ReportKind { Report, Plan, FirstMovement, CrossSection }

/// <summary>File ▸ Print: the report on the screen with what it is made of, and from there to a PDF file or the printer.</summary>
public static class ReportPreviewWindow
{
    /// <summary>
    /// Opens on <paramref name="kind"/> at a page, or on the design of the report; with no arrows or sections their sheets
    /// have the choices last made in their windows.
    /// </summary>
    public static async Task Show(MainViewModel vm, ReportKind kind, FirstMovementSheet? arrows = null, bool design = false, CrossSectionSheet? sections = null, int page = 0)
    {
        if (vm.Doc == null) return;
        // a copy of its own: the pages are built on another thread, and the window can rebuild them without touching the blast
        var state = vm.Doc.Capture();
        var project = Task.Run(() => { var p = state(); Timing.Solve(p); return p; });
        using var form = new ReportPreviewForm(vm, project, kind, arrows ?? AnalysisWindows.FirstMovementPage(), sections ?? AnalysisWindows.SectionPage(), design, page);
        var w = new Window
        {
            Title = Loc.Ui("Report preview"),
            Width = 1280,
            Height = 860,
            MinWidth = 1240,
            MinHeight = 560,
            Background = Look.Ground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = form
        };
        form.Closing = w.Close;
        // on the window, which has the keys when no field has the focus, and before the lists and scroll bars that take these keys
        w.AddHandler(InputElement.KeyDownEvent, form.PageKeys, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        await Dialogs.ShowModal(w);
        form.Design.KindsChosen = true;
        vm.Settings.ReportDesign = form.Design;
        vm.Settings.ReportPanelWidth = form.ReportPanelWidth;
        vm.Settings.ReportDesignPanelWidth = form.DesignPanelWidth;
        vm.Settings.Save();
    }
}

/// <summary>One page of a report on the screen, at a zoom.</summary>
public sealed class ReportPageView : Control
{
    private ReportDocument? _report;
    private int _page;
    private double _zoom = 1;

    public void Show(ReportDocument? report, int page, double zoom)
    {
        (_report, _page, _zoom) = (report, page, zoom);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available) =>
        _report is { Pages.Count: > 0 } r ? new Size(r.Pages[_page].Width * _zoom, r.Pages[_page].Height * _zoom) : default;

    public override void Render(DrawingContext context)
    {
        if (_report is { Pages.Count: > 0 } r) context.Custom(new Sheet(new Rect(Bounds.Size), r, _page, (float)_zoom));
    }

    private sealed class Sheet(Rect bounds, ReportDocument report, int page, float zoom) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => false;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height));
            canvas.Clear(SKColors.White);
            canvas.Scale(zoom);
            try { report.Draw(canvas, page); }
            catch (Exception ex) { Log.Error("Report page failed", ex); }
            canvas.Restore();
        }
    }
}

/// <summary>Content of the report preview window. Public so the UI smoke test can reach it.</summary>
public sealed class ReportPreviewForm : DockPanel, IDisposable
{
    private enum Fit { Width, Page, Free }

    private const int Pause = 250;
    private const double MinZoom = 0.25, MaxZoom = 4;
    /// <summary>The narrowest the panel of each tab can be, and what it leaves to the page at least.</summary>
    private const double MinReportPanel = 260, MinDesignPanel = 480, MinPageWidth = 300, GripWidth = 6;
    /// <summary>A press of an arrow, in parts of the drawing, and of a zoom button, as the wheel on the plan.</summary>
    private const double PanStep = 0.1, FinePanStep = PanStep / 4, ZoomStep = 1.2;
    /// <summary>Four of these make one <see cref="ZoomStep"/>.</summary>
    private static readonly double FineZoomStep = Math.Pow(ZoomStep, 0.25);

    private readonly MainViewModel _vm;
    private readonly Task<BlastProject> _project;
    private readonly FirstMovementSheet _arrows;
    private readonly CrossSectionSheet _sections;
    private readonly ReportPageView _view = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(20) };
    private readonly ScrollViewer _scroll;
    private readonly ContentControl _left = new();
    private readonly TextBlock _pageText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 60, TextAlignment = TextAlignment.Center, Name = "pageText" };
    private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 48, TextAlignment = TextAlignment.Center, FontFamily = Look.Mono, FontSize = 12 };
    private readonly TextBlock _status = new() { FontSize = 12, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Name = "status" };
    private StackPanel _rows = new();
    private CameraPad? _pad;
    private Control? _visibility;
    private Control _settings;
    private Control _designer;
    private readonly Border _leftBorder;
    /// <summary>Of the bottom bar: as wide as the panel on the left, so what is centred beside it is centred under the page.</summary>
    private readonly ColumnDefinition _footerLeft = new();
    private readonly Button _settingsTab, _designTab, _save, _print;
    private readonly List<(ReportKind Kind, Button Chip)> _kinds = new();
    private readonly List<(ReportKind Kind, Control Panel)> _options = new();
    private readonly List<ReportDocument> _replaced = new();
    private ReportKind _kind;
    private ReportDocument? _document;
    private Task _building = Task.CompletedTask;
    private int _version, _page, _using;
    private double _zoom = 1;
    private Fit _fit = Fit.Page;
    private bool _shown, _designOpen;

    public ReportPreviewForm(MainViewModel vm, Task<BlastProject> project, ReportKind kind, FirstMovementSheet arrows, CrossSectionSheet sections, bool design, int page = 0)
    {
        _sections = sections;
        _vm = vm;
        _project = project;
        _arrows = arrows;
        _kind = kind;
        ReportPanelWidth = vm.Settings.ReportPanelWidth;
        DesignPanelWidth = vm.Settings.ReportDesignPanelWidth;
        Design = ProjectJson.DeepClone(vm.Settings.ReportDesign);
        // the very first time the check boxes start as in the Visibility tab of the main window; then they are the design's own
        if (!Design.KindsChosen) Design.HiddenKinds = vm.Project!.Entities.GroupBy(e => e.KindName).Where(g => g.All(e => vm.HiddenEntities.Contains(e.Id))).Select(g => g.Key).ToList();
        TranslateTitle();

        _settingsTab = FormKit.Tab(Loc.Ui("Report"), () => ShowTab(false));
        _designTab = FormKit.Tab(Loc.Ui("Report design"), () => ShowTab(true));
        _settingsTab.Name = "tabReport";
        _designTab.Name = "tabDesign";
        _save = FormKit.Tool("save", Loc.Ui("Save PDF…"), SavePdf);
        _save.Name = "savePdf";
        _print = FormKit.Primary(Loc.Ui("Print…"), Print);
        _print.Name = "print";
        var top = Toolbar();
        SetDock(top, Dock.Top);
        Children.Add(top);

        var footer = Footer();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        _settings = Settings();
        _designer = Designer();
        _leftBorder = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = _left };
        SetDock(_leftBorder, Dock.Left);
        Children.Add(_leftBorder);
        var grip = PanelGrip();
        SetDock(grip, Dock.Left);
        Children.Add(grip);

        _scroll = new ScrollViewer { Content = _view, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Name = "pages" };
        _scroll.SizeChanged += (_, _) => Refit();
        _scroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) Zoom(e.Delta.Y > 0 ? 1.15 : 1 / 1.15);
            else if (!TurnByWheel(e.Delta.Y < 0)) return;
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Children.Add(_scroll);

        ShowKind(kind);
        _page = page;
        ShowTab(design);
        Rebuild();
    }

    public ReportDesign Design { get; private set; }
    public double ReportPanelWidth { get; private set; }
    public double DesignPanelWidth { get; private set; }
    private ReportOptions _report => Design.Report;
    private PlanOptions _plan => Design.Plan;
    public Action? Closing { get; set; }
    public ReportDocument? Document => _document;
    public int Page => _page;
    public double ZoomLevel => _zoom;
    /// <summary>Ends when the pages on the screen are those of the current choices.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    public void Dispose()
    {
        _version++;
        _document?.Dispose();
        foreach (var r in _replaced) r.Dispose();
    }

    // ---------- the top of the window ----------

    private Control Toolbar()
    {
        var width = FormKit.Tool("fit", Loc.Ui("Width"), () => SetFit(Fit.Width));
        var page = FormKit.Tool("fit", Loc.Ui("Whole page"), () => SetFit(Fit.Page));
        width.Name = "fitWidth";
        page.Name = "fitPage";
        var saveDesign = FormKit.Tool("save", Loc.Ui("Save design…"), SaveDesignAs, Loc.Ui("Save the choices of the report, the frame, the stamps and the visibility to a file"));
        saveDesign.Name = "saveDesign";
        var loadDesign = FormKit.Tool("open", Loc.Ui("Load design…"), LoadDesignFrom, Loc.Ui("Replace the choices of the report, the frame, the stamps and the visibility with those of a file"));
        loadDesign.Name = "loadDesign";
        var right = Ui.Row(14, Ui.Row(4, saveDesign, loadDesign), Ui.Row(4, width, page), _save, _print);
        right.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(right, Dock.Right);
        // the tabs stand on the line under the band
        var tabs = Ui.Row(0, _settingsTab, _designTab);
        tabs.VerticalAlignment = VerticalAlignment.Bottom;
        return new Border
        {
            Background = Look.Panel,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 8, 14, 0),
            Child = new DockPanel { Children = { right, tabs } }
        };
    }

    /// <summary>The bar under the page: the pages and the zoom in the middle of the page, the button that closes the window at the end.</summary>
    private Control Footer()
    {
        var pages = Ui.Row(4, FormKit.Tool("left", null, () => GoTo(_page - 1), Loc.Ui("Previous page") + " (PgUp)"), _pageText, FormKit.Tool("right", null, () => GoTo(_page + 1), Loc.Ui("Next page") + " (PgDn)"));
        var minus = Ui.Flat(new TextBlock { Text = "−", FontSize = 16 }, Loc.Ui("Zoom out"), () => Zoom(1 / 1.25), 32, 32);
        var plus = Ui.Flat(new TextBlock { Text = "+", FontSize = 16 }, Loc.Ui("Zoom in"), () => Zoom(1.25), 32, 32);
        var controls = Ui.Row(14, pages, Ui.Row(4, minus, _zoomText, plus));
        controls.Name = "pageControls";
        controls.HorizontalAlignment = HorizontalAlignment.Center;
        var close = FormKit.Secondary(Loc.Ui("Close"), () => Closing?.Invoke());
        close.IsCancel = true;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 0, 16, 0);
        _status.Margin = new Thickness(16, 0, 12, 0);
        var page = new Panel { Children = { controls, close } };
        Grid.SetColumn(page, 1);
        var grid = new Grid { Children = { _status, page } };
        grid.ColumnDefinitions.Add(_footerLeft);
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10), Child = grid };
    }

    private void ShowTab(bool design)
    {
        FormKit.SetTab(_settingsTab, !design);
        FormKit.SetTab(_designTab, design);
        _left.Content = design ? _designer : _settings;
        _designOpen = design;
        ShowPanelWidth();
    }

    private void ShowPanelWidth()
    {
        _leftBorder.Width = _designOpen ? DesignPanelWidth : ReportPanelWidth;
        _footerLeft.Width = new GridLength(_leftBorder.Width + GripWidth);
    }

    /// <summary>Sets the width of the panel of the open tab, within what the tab and the page need.</summary>
    public void ResizePanel(double width)
    {
        var least = _designOpen ? MinDesignPanel : MinReportPanel;
        width = Math.Max(least, Math.Min(width, Bounds.Width - MinPageWidth));
        if (_designOpen) DesignPanelWidth = width;
        else ReportPanelWidth = width;
        ShowPanelWidth();
    }

    /// <summary>The strip beside the panel that drags its edge.</summary>
    private Control PanelGrip()
    {
        var grip = new Border { Width = GripWidth, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeWestEast), Name = "panelGrip" };
        var dragging = false;
        grip.PointerEntered += (_, _) => grip.Background = Look.LineStrong;
        grip.PointerExited += (_, _) => { if (!dragging) grip.Background = Brushes.Transparent; };
        grip.PointerPressed += (_, e) =>
        {
            dragging = true;
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) => { if (dragging) ResizePanel(e.GetPosition(this).X); };
        grip.PointerReleased += (_, e) =>
        {
            dragging = false;
            e.Pointer.Capture(null);
            if (!grip.IsPointerOver) grip.Background = Brushes.Transparent;
        };
        return grip;
    }

    // ---------- what goes into the report ----------

    /// <summary>The title of a design that was never given one is in the language of the program.</summary>
    private void TranslateTitle()
    {
        if (_report.Title == new ReportOptions().Title) _report.Title = Loc.T(_report.Title);
    }

    private Control Settings()
    {
        _kinds.Clear();
        _options.Clear();
        var kinds = new WrapPanel();
        void Kind(ReportKind kind, string text)
        {
            var chip = Ui.Chip(text, () => { ShowKind(kind); Rebuild(); }, kind == _kind);
            chip.Name = "kind" + kind;
            chip.Margin = new Thickness(0, 0, 6, 6);
            _kinds.Add((kind, chip));
            kinds.Children.Add(chip);
        }
        Kind(ReportKind.Report, Loc.Ui("Report"));
        Kind(ReportKind.Plan, Loc.Ui("Plan"));
        Kind(ReportKind.FirstMovement, Loc.T("First movement"));
        Kind(ReportKind.CrossSection, Loc.T("Section view"));

        var body = new StackPanel { Spacing = 16, Margin = new Thickness(16, 14), Children = { kinds } };
        void Options(ReportKind kind, Control panel)
        {
            _options.Add((kind, panel));
            body.Children.Add(panel);
        }
        Options(ReportKind.Report, ReportOptions());
        Options(ReportKind.Plan, PlanSettings());
        Options(ReportKind.FirstMovement, ArrowOptions(_arrows));
        Options(ReportKind.CrossSection, SectionOptions(_sections));
        return new ScrollViewer { Content = body };
    }

    /// <summary>What follows the title at the top of the pages, with the fields of the blast.</summary>
    private TextBox TitleAddition()
    {
        var box = FormKit.Text(_report.TitleAddition);
        box.Name = "titleAddition";
        ToolTip.SetTip(box, Loc.Ui("At the top of each page, after the title. {name} is the name of the blast; the fields of the stamps work too"));
        box.TextChanged += (_, _) => { _report.TitleAddition = box.Text ?? ""; Rebuild(); };
        return box;
    }

    private TextBox TitleBox(string value, Action<string> set)
    {
        var box = FormKit.Text(value);
        box.Name = "reportTitle";
        box.TextChanged += (_, _) => { set(box.Text ?? ""); Rebuild(); };
        return box;
    }

    private CheckBox Flag(string text, bool value, Action<bool> set, string? name = null)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Name = name };
        box.IsCheckedChanged += (_, _) => { set(box.IsChecked == true); Rebuild(); };
        return box;
    }

    private Control ReportOptions()
    {
        var timing = new ComboBox { ItemsSource = Enum.GetValues<TimingLabelMode>().Select(m => Loc.Enum(m)).ToList(), SelectedIndex = (int)_report.PlanTimingLabels, MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
        timing.SelectionChanged += (_, _) => { if (timing.SelectedIndex >= 0) { _report.PlanTimingLabels = (TimingLabelMode)timing.SelectedIndex; Rebuild(); } };
        var sections = new StackPanel { Spacing = 6 };
        foreach (var section in Enum.GetValues<ReportSections>().Where(x => x is not ReportSections.None and not ReportSections.All)) sections.Children.Add(SectionGroup(section));
        return new StackPanel
        {
            Spacing = 14,
            Children =
            {
                FormKit.Labeled(Loc.T("Title"), TitleBox(_report.Title, v => _report.Title = v)),
                FormKit.Labeled(Loc.Ui("After the title"), TitleAddition()),
                FormKit.Section(Loc.T("Sections"), sections),
                FormKit.Section(Loc.Ui("Page"), new StackPanel { Spacing = 10, Children = { Flag(Loc.T("Landscape"), _report.Landscape, on => _report.Landscape = on, "landscape"), FormKit.Labeled(Loc.T("Timing labels on the plan"), timing) } })
            }
        };
    }

    /// <summary>A section of the report: its switch on the title, and how it starts.</summary>
    private Control SectionGroup(ReportSections section)
    {
        var on = new ToggleSwitch { IsChecked = _report.Sections.HasFlag(section), OnContent = "", OffContent = "", MinWidth = 0, Name = "section" + section, VerticalAlignment = VerticalAlignment.Center };
        var newPage = Flag(Loc.Ui("New page"), _report.NewPage.HasFlag(section), page => _report.NewPage = page ? _report.NewPage | section : _report.NewPage & ~section, "newPage" + section);
        newPage.IsVisible = on.IsChecked == true;
        on.IsCheckedChanged += (_, _) =>
        {
            _report.Sections = on.IsChecked == true ? _report.Sections | section : _report.Sections & ~section;
            newPage.IsVisible = on.IsChecked == true;
            Rebuild();
        };
        newPage.MinWidth = 0;
        newPage.Margin = new Thickness(8, 0, 4, 0);
        DockPanel.SetDock(on, Dock.Right);
        DockPanel.SetDock(newPage, Dock.Right);
        var title = Loc.Enum(section);
        var name = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Foreground = Look.Text, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(name, title);
        return new Border
        {
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4),
            Child = new DockPanel { Children = { on, newPage, name } }
        };
    }

    private Control PlanSettings()
    {
        var scales = new double?[] { null, 250, 500, 1000, 2000 };
        var scale = new ComboBox { ItemsSource = new[] { Loc.T("Fit to page"), "1:250", "1:500", "1:1000", "1:2000" }, SelectedIndex = Math.Max(0, Array.IndexOf(scales, _plan.Scale)), MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch, Name = "planScale" };
        scale.SelectionChanged += (_, _) => { if (scale.SelectedIndex >= 0) { _plan.Scale = scales[scale.SelectedIndex]; ShowPadZoom(); Rebuild(); } };
        return new StackPanel
        {
            Spacing = 14,
            Children =
            {
                FormKit.Labeled(Loc.T("Title"), TitleBox(_plan.Title, v => _plan.Title = v)),
                FormKit.Labeled(Loc.T("Scale"), scale),
                Flag(Loc.T("Landscape"), _plan.Landscape, on => _plan.Landscape = on)
            }
        };
    }

    private Control ArrowOptions(FirstMovementSheet arrows) => new StackPanel
    {
        Spacing = 14,
        Children =
        {
            FormKit.Labeled(Loc.T("Title"), TitleBox(arrows.Title, v => arrows.Title = v)),
            Flag(Loc.Ui("Hide geometry"), arrows.HideGeometry, on => arrows.HideGeometry = on),
            Flag(Loc.T("Landscape"), arrows.Landscape, on => arrows.Landscape = on)
        }
    };

    private Control SectionOptions(CrossSectionSheet sheet)
    {
        var half = FormKit.Text(sheet.HalfWidth?.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) ?? "");
        half.Name = "sectionHalfWidth";
        half.TextChanged += (_, _) =>
        {
            // empty is the width of the blast; what is not a number waits for the rest of the typing
            if (string.IsNullOrWhiteSpace(half.Text)) sheet.HalfWidth = null;
            else if (double.TryParse(half.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m >= 0.5) sheet.HalfWidth = m;
            else return;
            Rebuild();
        };
        return new StackPanel
        {
            Spacing = 14,
            Children =
            {
                FormKit.Labeled(Loc.T("Title"), TitleBox(sheet.Title, v => sheet.Title = v)),
                FormKit.Labeled(Loc.T("Half width (m)"), half),
                Flag(Loc.T("Landscape"), sheet.Landscape, on => sheet.Landscape = on)
            }
        };
    }

    private void ShowKind(ReportKind kind)
    {
        _kind = kind;
        _page = 0;
        foreach (var (k, chip) in _kinds) Ui.SetChip(chip, k == kind);
        foreach (var (k, panel) in _options) panel.IsVisible = k == kind;
        ShowPadZoom();
    }

    /// <summary>A plan at a scale of its own has no zoom: the sheet says what the scale is.</summary>
    private void ShowPadZoom()
    {
        if (_pad != null) _pad.CanZoom = _kind != ReportKind.Plan || _plan.Scale == null;
    }

    // ---------- the design ----------

    private Control Designer()
    {
        _rows = new StackPanel { Spacing = 6 };
        var frame = new PropertyGrid { Exclude = new() { nameof(ReportFrame.Visible), nameof(ReportFrame.OnAllPages), nameof(ReportFrame.Pages) } };
        FormLayouts.Apply(frame, typeof(ReportFrame));
        frame.SetTarget(Design.Frame);
        frame.PropertyEdited += _ => Rebuild();

        var grid = new PropertyGrid { Exclude = new() { nameof(ReportStamp.LabelWidthMm), nameof(ReportStamp.TextWidthMm), nameof(ReportStamp.LabelVerticalAlign), nameof(ReportStamp.TextVerticalAlign), nameof(ReportStamp.PageNumbers) } };
        FormLayouts.Apply(grid, typeof(ReportStamp));
        grid.WideFields.Add(nameof(ReportStamp.Name));
        grid.Chips.Add(nameof(ReportStamp.Attach));
        // the chips take what they need, the list of places has room to give
        grid.FieldColumns = "*,8,Auto";
        var numbers = FormKit.Text();
        numbers.Name = "stampPages";
        numbers.Watermark = "1 3 5";
        var numbersRow = FormKit.Labeled(Loc.Ui("Page numbers, separated by spaces"), numbers);
        grid.Beside[nameof(ReportStamp.Pages)] = numbersRow;
        var stamps = new ListEditor<ReportStamp>(Design.Stamps, s => s.Name, NewStamp, s => { var c = ProjectJson.DeepClone(s); c.Name += " (2)"; return c; }, grid) { ListWidth = 150 };
        stamps.Changed += Rebuild;
        stamps.SelectionChanged += ShowRows;
        void ShowNumbers(ReportStamp? stamp)
        {
            numbers.Text = stamp?.PageNumbers;
            numbersRow.IsVisible = stamp?.Pages == StampPages.Specified;
        }
        numbers.TextChanged += (_, _) =>
        {
            if (stamps.Selected is not { } stamp || stamp.PageNumbers == (numbers.Text ?? "")) return;
            stamp.PageNumbers = numbers.Text ?? "";
            Rebuild();
        };
        stamps.Changed += () => ShowNumbers(stamps.Selected);
        stamps.SelectionChanged += ShowNumbers;
        ShowNumbers(stamps.Selected);
        stamps.AddBelow(FormKit.Section(Loc.Ui("Stamp text"), new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _rows,
                new TextBlock { Text = Loc.Ui("Fields:") + " " + string.Join(" ", ReportDesign.Fields), FontSize = 11, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap }
            }
        }));
        ShowRows(stamps.Selected);

        Border Box(Control content) => new() { Padding = new Thickness(16, 14), BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = content };
        // the list shows its selected row, and that must not scroll the whole panel down to it
        var stampsHost = new ContentControl { Height = 500, Content = stamps };
        stampsHost.AddHandler(Control.RequestBringIntoViewEvent, (_, e) => e.Handled = true);
        _visibility = Box(FormKit.Section(Loc.Ui("Visibility"), Visibility()));
        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Children =
                {
                    Box(FormKit.Section(Loc.Ui("Frame"), new StackPanel { Spacing = 10, Children = { frame, FramePages() } }, FrameSwitch())),
                    _visibility,
                    stampsHost,
                    Box(FormKit.Section(Loc.Ui("Footer"), new StackPanel { Spacing = 6, Children = { FooterDate(), FooterNumber() } }))
                }
            }
        };
    }

    /// <summary>The objects, and beside them the buttons that move the drawing.</summary>
    private Control Visibility()
    {
        var objects = Objects();
        _pad = new CameraPad { Name = "cameraPad", Locked = Design.View.Locked, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        _pad.Pressed += Aim;
        Grid.SetColumn(_pad, 1);
        return new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { objects, _pad } };
    }

    private void Aim(CameraCommand command, bool fine)
    {
        var view = Design.View;
        var step = fine ? FinePanStep : PanStep;
        var zoom = fine ? FineZoomStep : ZoomStep;
        // an arrow moves the drawing on the sheet, so the view goes the other way
        switch (command)
        {
            case CameraCommand.Up: view.PanY -= step; break;
            case CameraCommand.Down: view.PanY += step; break;
            case CameraCommand.Left: view.PanX += step; break;
            case CameraCommand.Right: view.PanX -= step; break;
            case CameraCommand.ZoomIn: view.ZoomBy(zoom); break;
            case CameraCommand.ZoomOut: view.ZoomBy(1 / zoom); break;
            case CameraCommand.Lock:
                view.Locked = !view.Locked;
                view.LockedHiddenKinds = view.Locked ? Design.HiddenKinds.ToList() : new();
                _pad!.Locked = view.Locked;
                break;
        }
        Rebuild();
    }

    /// <summary>A group of the footer: its switch on the title, what it holds only while it is on.</summary>
    private Control FooterGroup(string title, string name, bool on, Action<bool> set, Control body)
    {
        var toggle = new ToggleSwitch { IsChecked = on, OnContent = "", OffContent = "", MinWidth = 0, Name = name, VerticalAlignment = VerticalAlignment.Center };
        body.IsVisible = on;
        body.Margin = new Thickness(0, 4, 0, 4);
        toggle.IsCheckedChanged += (_, _) =>
        {
            set(toggle.IsChecked == true);
            body.IsVisible = toggle.IsChecked == true;
            Rebuild();
        };
        DockPanel.SetDock(toggle, Dock.Right);
        return new Border
        {
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4),
            Child = new StackPanel { Children = { new DockPanel { Children = { toggle, new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Foreground = Look.Text } } }, body } }
        };
    }

    /// <summary>The date at the foot of the pages. It is one or the other, so the two boxes work as a choice.</summary>
    private Control FooterDate()
    {
        var footer = Design.Footer;
        var date = new CheckBox { Content = Loc.T("Date"), Name = "footerDateOnly" };
        var time = new CheckBox { Content = Loc.Ui("Date and time"), Name = "footerDateTime" };
        void Show() => (date.IsChecked, time.IsChecked) = (!footer.DateWithTime, footer.DateWithTime);
        void Choose(bool withTime)
        {
            footer.DateWithTime = withTime;
            Show();
            Rebuild();
        }
        date.Click += (_, _) => Choose(false);
        time.Click += (_, _) => Choose(true);
        Show();
        return FooterGroup(Loc.T("Date"), "footerDate", footer.ShowDate, on => footer.ShowDate = on, Ui.Row(16, date, time));
    }

    /// <summary>The page number at the foot of the pages, on the same choice of pages as a stamp.</summary>
    private Control FooterNumber()
    {
        var footer = Design.Footer;
        var pages = new ComboBox { ItemsSource = Enum.GetValues<StampPages>().Select(p => Loc.Enum(p)).ToList(), SelectedIndex = (int)footer.NumberedPages, MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch, Name = "footerPages" };
        var numbers = FormKit.Text(footer.PageNumbers);
        numbers.Name = "footerPageNumbers";
        numbers.Watermark = "1 3 5";
        var numbersRow = FormKit.Labeled(Loc.Ui("Page numbers, separated by spaces"), numbers);
        numbersRow.IsVisible = footer.NumberedPages == StampPages.Specified;
        pages.SelectionChanged += (_, _) =>
        {
            if (pages.SelectedIndex < 0) return;
            footer.NumberedPages = (StampPages)pages.SelectedIndex;
            numbersRow.IsVisible = footer.NumberedPages == StampPages.Specified;
            Rebuild();
        };
        numbers.TextChanged += (_, _) => { footer.PageNumbers = numbers.Text ?? ""; Rebuild(); };
        Grid.SetColumn(numbersRow, 2);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*"), Children = { FormKit.Labeled(Loc.Property(nameof(ReportStamp.Pages)), pages), numbersRow } };
        return FooterGroup(Loc.Ui("Page number"), "footerPageNumber", footer.ShowPageNumber, on => footer.ShowPageNumber = on, body);
    }

    private ToggleSwitch FrameSwitch()
    {
        var visible = new ToggleSwitch { IsChecked = Design.Frame.Visible, OnContent = "", OffContent = "", MinWidth = 0, Name = "propVisible" };
        ToolTip.SetTip(visible, Loc.Property(nameof(ReportFrame.Visible)));
        visible.IsCheckedChanged += (_, _) => { Design.Frame.Visible = visible.IsChecked == true; Rebuild(); };
        return visible;
    }

    /// <summary>The pages with the frame: all of them, or those whose numbers are given.</summary>
    private Control FramePages()
    {
        var numbers = FormKit.Text(Design.Frame.Pages);
        numbers.Name = "framePages";
        numbers.Watermark = "1 3 5";
        numbers.Width = 170;
        numbers.IsVisible = !Design.Frame.OnAllPages;
        ToolTip.SetTip(numbers, Loc.Ui("Page numbers, separated by spaces"));
        numbers.TextChanged += (_, _) => { Design.Frame.Pages = numbers.Text ?? ""; Rebuild(); };
        Button all = null!, some = null!;
        void Choose(bool onAll)
        {
            Design.Frame.OnAllPages = onAll;
            Ui.SetChip(all, onAll);
            Ui.SetChip(some, !onAll);
            numbers.IsVisible = !onAll;
            Rebuild();
        }
        all = Ui.Chip(Loc.Ui("on all"), () => Choose(true), Design.Frame.OnAllPages);
        some = Ui.Chip(Loc.Ui("specified"), () => Choose(false), !Design.Frame.OnAllPages);
        all.Name = "framePagesAll";
        some.Name = "framePagesSome";
        var label = new TextBlock { Text = Loc.Ui("On pages"), FontSize = 12, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center };
        return Ui.Row(8, label, all, some, numbers);
    }

    /// <summary>One check box per kind of object with its count, as in the Visibility tab of the main window, and the coordinate grid; they act on the report only.</summary>
    private Control Objects()
    {
        // the first column as wide as its longest name: the second has the shorter ones and takes what is left
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        void Add(CheckBox box)
        {
            var at = grid.Children.Count;
            if (at % 2 == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(box, at / 2);
            Grid.SetColumn(box, at % 2);
            grid.Children.Add(box);
        }
        foreach (var kind in _vm.Project!.Entities.GroupBy(e => e.KindName).OrderBy(g => g.Key))
        {
            var box = new CheckBox { Content = new TextBlock { Text = $"{Loc.T(kind.Key)} ({kind.Count()})", TextWrapping = TextWrapping.Wrap }, IsChecked = !Design.HiddenKinds.Contains(kind.Key), Tag = "kind:" + kind.Key, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Top };
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true) Design.HiddenKinds.Remove(kind.Key);
                else Design.HiddenKinds.Add(kind.Key);
                Rebuild();
            };
            Add(box);
        }
        var coordinates = new CheckBox { Content = new TextBlock { Text = Loc.Ui("Coord. grid"), TextWrapping = TextWrapping.Wrap }, IsChecked = Design.ShowGrid, Tag = "grid", Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Top };
        coordinates.IsCheckedChanged += (_, _) => { Design.ShowGrid = coordinates.IsChecked == true; Rebuild(); };
        Add(coordinates);
        return grid;
    }

    private static ReportStamp NewStamp() => new()
    {
        Name = Loc.Ui("Stamp"),
        Rows = new()
        {
            new() { Label = Loc.T("Mine"), Text = "{mine}" },
            new() { Label = Loc.T("Blast"), Text = "{blast} {number}" },
            new() { Label = Loc.T("Designer"), Text = "{author}" },
            new() { Label = Loc.T("Date"), Text = "{date}" },
            new() { Label = Loc.Ui("Sheet"), Text = "{page} / {pages}" }
        }
    };

    /// <summary>The columns of the lines of a stamp: then the alignment of the two texts, and the button that deletes a line.</summary>
    private static Grid RowGrid() => new() { ColumnDefinitions = new ColumnDefinitions("100,*,32,32,32"), ColumnSpacing = 6 };

    /// <summary>A button that goes through the alignments of a cell, showing the one it has.</summary>
    private Button AlignButton<T>(string name, string tip, T value, Action<T> set) where T : struct, Enum
    {
        var all = Enum.GetValues<T>();
        Control Icon() => Look.Icon("align" + value, 18, Look.Accent);
        Button button = null!;
        button = Ui.Flat(Icon(), tip, () =>
        {
            value = all[(Array.IndexOf(all, value) + 1) % all.Length];
            set(value);
            button.Content = Icon();
            Rebuild();
        }, 32, 32);
        button.Name = name;
        return button;
    }

    /// <summary>The widths of the two columns of a stamp, over the columns they set.</summary>
    private Control ColumnWidths(ReportStamp stamp)
    {
        TextBox Width(string name, double value, Action<double> set)
        {
            var box = FormKit.Text(value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture));
            box.Name = name;
            box.TextChanged += (_, _) =>
            {
                // 0 leaves the column out, any other has to stay wide enough to draw; what is not a number waits for the rest of the typing
                if (double.TryParse((box.Text ?? "").Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mm) && (mm == 0 || mm >= 5))
                {
                    set(mm);
                    Rebuild();
                }
            };
            return box;
        }
        var label = FormKit.Labeled(Loc.T("Width mm"), Width("stampLabelWidth", stamp.LabelWidthMm, v => stamp.LabelWidthMm = v));
        var text = FormKit.Labeled(Loc.T("Width mm"), Width("stampTextWidth", stamp.TextWidthMm, v => stamp.TextWidthMm = v));
        var labelAlign = AlignButton("stampLabelVerticalAlign", Loc.Ui("Vertical alignment of the whole column of captions: click for the next one"), stamp.LabelVerticalAlign, a => stamp.LabelVerticalAlign = a);
        var textAlign = AlignButton("stampTextVerticalAlign", Loc.Ui("Vertical alignment of the whole column of texts: click for the next one"), stamp.TextVerticalAlign, a => stamp.TextVerticalAlign = a);
        // beside the fields, under the captions of the widths
        labelAlign.VerticalAlignment = textAlign.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(text, 1);
        Grid.SetColumn(labelAlign, 2);
        Grid.SetColumn(textAlign, 3);
        var line = RowGrid();
        line.Children.Add(label);
        line.Children.Add(text);
        line.Children.Add(labelAlign);
        line.Children.Add(textAlign);
        return line;
    }

    /// <summary>The lines of the selected stamp, a caption and a text each.</summary>
    private void ShowRows(ReportStamp? stamp)
    {
        _rows.Children.Clear();
        if (stamp == null) return;
        _rows.Children.Add(ColumnWidths(stamp));
        foreach (var row in stamp.Rows)
        {
            var label = FormKit.Text(row.Label);
            var text = FormKit.Text(row.Text);
            label.TextChanged += (_, _) => { row.Label = label.Text ?? ""; Rebuild(); };
            text.TextChanged += (_, _) => { row.Text = text.Text ?? ""; Rebuild(); };
            var labelAlign = AlignButton("stampLabelAlign", Loc.Ui("Alignment of the caption: click for the next one"), row.LabelAlign, a => row.LabelAlign = a);
            var textAlign = AlignButton("stampTextAlign", Loc.Ui("Alignment of the text: click for the next one"), row.TextAlign, a => row.TextAlign = a);
            var delete = FormKit.Tool("trash", null, () => { stamp.Rows.Remove(row); ShowRows(stamp); Rebuild(); }, Loc.Ui("Delete"));
            var line = RowGrid();
            Grid.SetColumn(text, 1);
            Grid.SetColumn(labelAlign, 2);
            Grid.SetColumn(textAlign, 3);
            Grid.SetColumn(delete, 4);
            line.Children.Add(label);
            line.Children.Add(text);
            line.Children.Add(labelAlign);
            line.Children.Add(textAlign);
            line.Children.Add(delete);
            _rows.Children.Add(line);
        }
        var add = FormKit.Tool("plus", Loc.Ui("Add"), () => { stamp.Rows.Add(new StampRow()); ShowRows(stamp); Rebuild(); });
        add.Name = "addStampRow";
        add.HorizontalAlignment = HorizontalAlignment.Left;
        _rows.Children.Add(add);
    }

    // ---------- the pages ----------

    private void Rebuild() => Ready = RebuildPages();

    private async Task RebuildPages()
    {
        var version = ++_version;
        _status.Text = Loc.Ui("Building the pages…");
        _save.IsEnabled = _print.IsEnabled = false;
        // the first pages at once, the next when the typing stops
        if (_shown) await Task.Delay(Pause);
        if (version != _version) return;
        _shown = true;

        // everything the build reads is copied here: the window goes on changing its own
        var (kind, now, design) = (_kind, DateTime.Now, ProjectJson.DeepClone(Design));
        var arrows = new FirstMovementSheet { Title = _arrows.Title, Landscape = _arrows.Landscape, HideGeometry = _arrows.HideGeometry, Yaw = _arrows.Yaw, Arrows = _arrows.Arrows };
        var sections = new CrossSectionSheet { Title = _sections.Title, Landscape = _sections.Landscape, HalfWidth = _sections.HalfWidth, Line = _sections.Line };
        var previous = _building;
        string? note = null;
        var build = Task.Run(async () =>
        {
            // one at a time: they read the same copy of the project, and the plan changes its view while it draws
            await previous.ContinueWith(_ => { });
            var project = await _project;
            if (kind == ReportKind.FirstMovement && arrows.Arrows.Arrows(project).Count == 0) note = AnalysisWindows.NoFirstMovement(project);
            if (kind == ReportKind.CrossSection && sections.Line == null && Reports.SectionLines(project).Count == 0) note = Loc.Ui("The blast has no section lines. Draw one with the Section line tool.");
            return kind switch
            {
                ReportKind.Plan => Reports.BuildPlan(project, design.Plan, design, now),
                ReportKind.CrossSection => Reports.BuildCrossSections(project, sections, design, now),
                ReportKind.FirstMovement => Reports.BuildFirstMovement(project, arrows, design, now),
                _ => Reports.Build(project, design.Report, design, now)
            };
        });
        _building = build;
        ReportDocument document;
        try { document = await build; }
        catch (Exception ex)
        {
            Log.Error("Report failed", ex);
            if (version == _version) _status.Text = ex.Message;
            return;
        }
        if (version != _version) { document.Dispose(); return; }
        var old = _document;
        _document = document;
        if (old != null)
        {
            if (_using > 0) _replaced.Add(old);
            else old.Dispose();
        }
        _page = Math.Clamp(_page, 0, document.Pages.Count - 1);
        _save.IsEnabled = _print.IsEnabled = true;
        _status.Text = note ?? Loc.F(Loc.Ui("Pages: {0}"), document.Pages.Count);
        Refit();
    }

    /// <summary>A document being saved or printed is kept whole until that ends, even if new choices replace it on the screen.</summary>
    private async Task Using(Func<ReportDocument, Task> work)
    {
        if (_document is not { } document) return;
        _using++;
        try { await work(document); }
        catch (Exception ex) { Dialogs.ShowError(ex); }
        finally
        {
            if (--_using == 0)
            {
                foreach (var r in _replaced) r.Dispose();
                _replaced.Clear();
            }
        }
    }

    /// <summary>Writes the design as it is in the window to a file.</summary>
    public void SaveDesign(string path) => ReportDesignFile.Save(path, Design);

    /// <summary>Puts the design of a file in place of the one in the window, with its visibility.</summary>
    public void LoadDesign(string path)
    {
        var loaded = ReportDesignFile.Load(path);
        loaded.KindsChosen = true;
        var open = _left.Content == _designer;
        Design = loaded;
        TranslateTitle();
        // the fields are bound to the old design: new ones for the new one
        _designer = Designer();
        _settings = Settings();
        ShowKind(_kind);
        _left.Content = open ? _designer : _settings;
        Rebuild();
    }

    private async void LoadDesignFrom()
    {
        var path = await Dialogs.OpenFile("Load report design", ("Report design file", new[] { "*" + ReportDesignFile.Extension }));
        if (path == null) return;
        try
        {
            LoadDesign(path);
            _status.Text = Loc.F(Loc.Ui("Loaded: {0}"), Path.GetFileName(path));
        }
        catch (InvalidDataException) { await Dialogs.Info(Loc.Ui("The file is not a report design.")); }
        catch (Exception ex) { Dialogs.ShowError(ex); }
    }

    private async void SaveDesignAs()
    {
        var path = await Dialogs.SaveFile("Save report design", Loc.Ui("Report design") + ReportDesignFile.Extension, ReportDesignFile.Extension, ("Report design file", new[] { "*" + ReportDesignFile.Extension }));
        if (path == null) return;
        try
        {
            SaveDesign(path);
            _status.Text = Loc.F(Loc.Ui("Saved: {0}"), Path.GetFileName(path));
        }
        catch (Exception ex) { Dialogs.ShowError(ex); }
    }

    private void SavePdf() => _ = Using(d => _vm.SaveReport(d, _kind switch { ReportKind.Plan => "_plan", ReportKind.FirstMovement => "_first_movement", ReportKind.CrossSection => "_section", _ => "_report" }));

    private void Print() => _ = Using(d => Printing.Print((Window)TopLevel.GetTopLevel(this)!, d));

    public void GoTo(int page)
    {
        if (_document == null) return;
        var target = Math.Clamp(page, 0, _document.Pages.Count - 1);
        if (target == _page) return;
        _page = target;
        Refit();
        _scroll.Offset = default;
    }

    /// <summary>PgUp and PgDn turn the pages; a list that is open keeps them.</summary>
    public void PageKeys(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.PageUp or Key.PageDown) || e.KeyModifiers != KeyModifiers.None) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is ComboBox { IsDropDownOpen: true }) return;
        GoTo(_page + (e.Key == Key.PageDown ? 1 : -1));
        e.Handled = true;
    }

    /// <summary>The wheel turns the pages; a page taller than the panel scrolls first and turns at its end. False leaves the wheel to the scroll.</summary>
    private bool TurnByWheel(bool down)
    {
        var rest = _scroll.Extent.Height - _scroll.Viewport.Height;
        if (down ? _scroll.Offset.Y < rest - 1 : _scroll.Offset.Y > 1) return false;
        GoTo(_page + (down ? 1 : -1));
        return true;
    }

    private void Zoom(double factor)
    {
        _fit = Fit.Free;
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        ShowPage();
    }

    private void SetFit(Fit fit)
    {
        _fit = fit;
        Refit();
    }

    private void Refit()
    {
        if (_document is { Pages.Count: > 0 } d && _fit != Fit.Free && _scroll.Bounds.Width > 1)
        {
            var page = d.Pages[_page];
            double width = (_scroll.Bounds.Width - 60) / page.Width, height = (_scroll.Bounds.Height - 44) / page.Height;
            _zoom = Math.Clamp(_fit == Fit.Width ? width : Math.Min(width, height), MinZoom, MaxZoom);
        }
        ShowPage();
    }

    private void ShowPage()
    {
        _view.Show(_document, _page, _zoom);
        // what the visibility acts on has to be on the page in view
        if (_visibility != null && _document is { Pages.Count: > 0 } document)
        {
            _visibility.IsEnabled = document.Pages[_page].HasPlan;
            _visibility.Opacity = _visibility.IsEnabled ? 1 : 0.5;
        }
        _pageText.Text = _document == null ? "" : $"{_page + 1} / {_document.Pages.Count}";
        _zoomText.Text = $"{_zoom * 100:0} %";
    }
}
