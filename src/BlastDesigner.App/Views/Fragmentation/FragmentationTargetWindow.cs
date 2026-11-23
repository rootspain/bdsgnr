using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using SkiaSharp;

namespace BlastDesigner.App.Views;

/// <summary>
/// Analysis ▸ Fragmentation target: the rock on the left (the same inputs editor and presets as the fragmentation page of
/// the project properties), the target X50 and the search on the right: the hole and its charge, what may change, the
/// variants with their prediction, the curves and what applying one does. Modal: the project does not change while it is
/// open, so the variants do not go stale; «Apply» puts the chosen one in the project as one undo step, «Close» leaves the
/// project as it was (the rock inputs too).
/// </summary>
public sealed class FragmentationTargetWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly BlastProject _p;
    private readonly FragmentationInputsEditor _editor;
    private readonly FragmentationPresetBar _presets;
    private readonly FragmentationPreview _preview = new() { WithUniformity = true };
    private readonly TextBlock _previewTitle = FragmentationUi.Small();
    private readonly TextBox _x50 = NumberBox("targetX50", 64, 22), _tolerance = NumberBox("targetTolerance", 64, 15);
    private readonly TextBlock _x50Unit = new() { Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _extra = new() { Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _reach = new() { Name = "targetReach", Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _area = new() { Name = "targetArea", MinHeight = 32, MinWidth = 200 };
    private readonly List<(Guid? Id, string Name, int Holes)> _areas = new();
    private HoleColumnView _hole = null!;
    private readonly Border _holeHost = new();
    private readonly Button _ratioChip, _diameterChip, _azimuthChip, _solve;
    private readonly TextBlock _status = new() { Name = "targetStatus", Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap };
    private readonly Grid _table = new() { Name = "targetTable" };
    private readonly SkiaView _chart = new() { Height = 170 };
    private readonly TextBlock _chartLegend = new() { Foreground = Look.Faint, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _chartTitle = new() { FontWeight = FontWeight.SemiBold, Foreground = Look.Text };
    // the room of the axis numbers in DrawGrading: left of the plot and under it
    private const float AxisLeft = 38, AxisBottom = 26;
    private readonly Border _sizeAxis = new() { Name = "sizeAxis", Background = Brushes.Transparent, Height = AxisBottom, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(AxisLeft, 0, 0, 0) };
    private readonly Border _passAxis = new() { Name = "passAxis", Background = Brushes.Transparent, Width = AxisLeft, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, AxisBottom) };
    private readonly Grid _changes = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14, RowSpacing = 4 };
    private readonly TextBlock _changesTitle = new() { FontWeight = FontWeight.SemiBold, Foreground = Look.Text, Margin = new Thickness(0, 0, 0, 8) };
    private readonly CheckBox _saveRule = new() { Name = "targetSaveRule", IsChecked = true, Content = Loc.T("Keep the rule in the project") };
    private readonly Button _apply;
    private readonly List<double> _diameters = new();
    private readonly List<double> _projectDiameters;
    private bool _ratioFree, _azimuth = true;
    private double _azimuthWindow = 15;
    private readonly FragConstraints _constraints = new();
    private FragBase _base = null!;
    private FragSolveResult? _result;
    private List<FragCandidate> _rows = new();
    private int _selected = -1;
    private CancellationTokenSource? _cts;
    private readonly string _rockAtStart;

    /// <summary>The variant applied (null until then): for the smoke test.</summary>
    public FragApply.Report? Applied { get; private set; }

    public static async Task Open(MainViewModel vm, Guid? patternId)
    {
        if (vm.Project == null) return;
        var w = new FragmentationTargetWindow(vm, patternId);
        await Dialogs.ShowModal(w);
    }

    public FragmentationTargetWindow(MainViewModel vm, Guid? patternId)
    {
        _vm = vm;
        _p = vm.Project!;
        Title = Loc.T("Fragmentation target");
        Width = 1500;
        Height = 960;
        MinWidth = 1180;
        MinHeight = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Ground;
        DarkStart.Attach(this, Look.Ground);
        _constraints.StemmingDiameters = Charging.MinStemmingDiameters;

        var rock = ProjectJson.DeepClone(_p.Fragmentation);
        rock.Target ??= new FragTarget();
        _rockAtStart = RockJson(rock);
        _editor = new FragmentationInputsEditor(rock, _p.Design.RockDensity);
        _presets = new FragmentationPresetBar(_editor);
        _presets.Loaded += () => { _editor.Draft.Target ??= new FragTarget(); ShowTarget(); InputsChanged(); };
        _editor.Changed += InputsChanged;

        // the area: the patterns with their holes, or all of them
        foreach (var root in _p.Patterns.Where(x => x.ParentId == null))
            _areas.Add((root.Id, root.Name, PatternTree.TreeHoles(_p, root).Count(h => !h.IsDummy)));
        if (_areas.Count != 1 || _p.Holes.Any(h => !h.IsDummy && h.PatternId == null))
            _areas.Add((null, Loc.T("All holes"), _p.Holes.Count(h => !h.IsDummy)));
        _area.ItemsSource = _areas.Select(a => Loc.F(Loc.T("«{0}», {1} holes"), a.Name, a.Holes)).ToList();
        var start = patternId is Guid pid ? _areas.FindIndex(a => a.Id == PatternTree.Root(_p, _p.FindPattern(pid)!).Id) : -1;
        _area.SelectedIndex = start >= 0 ? start : 0;
        _projectDiameters = FragTargetSolver.ProjectDiameters(_p);

        _ratioChip = Chip("targetRatio", () => { _ratioFree = !_ratioFree; Changed(); });
        _diameterChip = Chip("targetDiameters", () => { });
        _diameterChip.Flyout = DiameterFlyout();
        _azimuthChip = Chip("targetAzimuth", () => { _azimuth = !_azimuth; Changed(); });
        _solve = FormKit.Primary(Loc.T("Find"), () => _ = Solve());
        _solve.Name = "targetSolve";
        _apply = FormKit.Primary(Loc.T("Apply"), () => _ = Apply());
        _apply.Name = "targetApply";

        LoadArea();
        Content = Layout();
        ShowTarget();
        _x50.LostFocus += (_, _) => TargetEdited();
        _x50.KeyDown += (_, e) => { if (e.Key == Key.Enter) TargetEdited(); };
        _tolerance.LostFocus += (_, _) => TargetEdited();
        _tolerance.KeyDown += (_, e) => { if (e.Key == Key.Enter) TargetEdited(); };
        _area.SelectionChanged += (_, _) => { if (_hole != null) { LoadArea(); Changed(); } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; _ = CloseAsked(); } };
        Closing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;
            _ = CloseAsked();
        };
        InputsChanged();
    }

    private static string RockJson(FragmentationModel m)
    {
        var c = ProjectJson.DeepClone(m);
        c.Target = null;
        return System.Text.Json.JsonSerializer.Serialize(c, ProjectJson.Options);
    }

    private FragTarget Target => _editor.Draft.Target ??= new FragTarget();
    private Guid? AreaId => _area.SelectedIndex >= 0 && _area.SelectedIndex < _areas.Count ? _areas[_area.SelectedIndex].Id : null;
    private IReadOnlyList<Guid> RootIds => AreaId is Guid id ? new[] { id } : _p.Patterns.Where(x => x.ParentId == null).Select(x => x.Id).ToList();

    /// <summary>The base of the area with the rock as edited; the hole and charge builder made again for it.</summary>
    private void LoadArea()
    {
        _base = FragBase.Of(_p, AreaId, _editor.Draft);
        if (_diameters.Count == 0 || !_holeLoaded) { _diameters.Clear(); _diameters.Add(_base.DiameterMm); }
        _hole = new HoleColumnView(_p, _base.Wet, _base.Scheme, _base.Stemming, _base.BenchHeight, _base.Subdrill, _base.DiameterMm) { Name = "holeColumn" };
        _hole.SetGeometry(_base.BenchHeight, _base.Subdrill, _base.DiameterMm, FragTargetSolver.StemmingFor(Space(), _base.Burden, _base.DiameterMm));
        _hole.Changed += Changed;
        _holeLoaded = true;
        _holeHost.Child = HoleCard();
    }
    private bool _holeLoaded;

    // ---------- layout ----------

    private Control Layout()
    {
        // no header band: the blast and the area go to the title of the window, the note to the footer
        var area = _areas.Count > 0 && AreaId is Guid id ? _p.FindPattern(id)?.Name : null;
        Title = Loc.T("Fragmentation target") + " — " + (_p.Header.Name is { Length: > 0 } n ? n : Loc.T("Blast")) + (area != null ? " · «" + area + "»" : "");
        var close = FormKit.Secondary(Loc.T("Close"), () => _ = CloseAsked());
        close.Name = "targetClose";
        var footer = FormKit.Footer(Loc.T("The project does not change until «Apply»") + ". " + Loc.T("Applying is one undo step (Ctrl+Z). Kuz-Ram prediction, ±30–50 % without a calibrated A."), close, _apply);
        DockPanel.SetDock(footer, Dock.Bottom);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("480,*") };
        var left = RockColumn();
        var right = SearchColumn();
        Grid.SetColumn(right, 1);
        body.Children.Add(left);
        body.Children.Add(right);
        return new DockPanel { Children = { footer, body } };
    }

    private Control RockColumn()
    {
        var title = new TextBlock { Text = Loc.T("Rock"), FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center };
        var calibrate = FormKit.Secondary(Loc.T("Calibrate A…"), () => _ = Calibrate());
        calibrate.Name = "calibrateA";
        var head = new DockPanel();
        DockPanel.SetDock(calibrate, Dock.Right);
        head.Children.Add(calibrate);
        head.Children.Add(title);
        _presets.HorizontalAlignment = HorizontalAlignment.Left;
        var presets = new StackPanel { Spacing = 4, Children = { _presets, _presets.Note } };
        var stack = new StackPanel { Spacing = 14, Margin = new Thickness(16, 14, 16, 18), Children = { head, presets, _editor } };
        var previewCard = FragmentationUi.Card(new StackPanel { Spacing = 8, Children = { _previewTitle, _preview } });
        previewCard.Margin = new Thickness(16, 0, 16, 14);
        DockPanel.SetDock(previewCard, Dock.Bottom);
        return new Border
        {
            BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new DockPanel { Children = { previewCard, new ScrollViewer { Content = stack } } }
        };
    }

    private Control SearchColumn()
    {
        var target = FragmentationUi.Card(TargetRow());
        var holeAndSpace = new Grid { ColumnDefinitions = new ColumnDefinitions("1.3*,*"), ColumnSpacing = 14 };
        holeAndSpace.Children.Add(_holeHost);
        var space = SpaceCard();
        Grid.SetColumn(space, 1);
        holeAndSpace.Children.Add(space);
        var table = FragmentationUi.Card(new ScrollViewer { Content = _table, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 280 });
        table.Padding = new Thickness(0, 6);
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("1.7*,*"), ColumnSpacing = 14 };
        var chartLegend = _chartLegend;
        var chartHead = new DockPanel();
        DockPanel.SetDock(chartLegend, Dock.Right);
        chartHead.Children.Add(chartLegend);
        chartHead.Children.Add(_chartTitle);
        _chartTitle.Text = Loc.T("Size distribution");
        // the axes say what they are on hover: transparent strips over the numbers of each axis
        var chartArea = new Grid { Children = { _chart, _sizeAxis, _passAxis } };
        bottom.Children.Add(FragmentationUi.Card(new StackPanel { Spacing = 8, Children = { chartHead, chartArea } }));
        var changes = FragmentationUi.Card(new StackPanel { Children = { _changesTitle, _changes, _saveRule } });
        Grid.SetColumn(changes, 1);
        bottom.Children.Add(changes);
        _saveRule.Margin = new Thickness(0, 6, 0, 0);
        var stack = new StackPanel { Spacing = 14, Margin = new Thickness(16, 14, 16, 14), Children = { target, holeAndSpace, table, bottom } };
        return new ScrollViewer { Content = stack };
    }

    private Control TargetRow()
    {
        var title = new TextBlock { Text = Loc.T("Target"), FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center };
        var change = new Button { Content = Loc.T("change"), Name = "targetMore", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Look.Accent, Padding = new Thickness(4, 0), Cursor = new Cursor(StandardCursorType.Hand) };
        change.Click += (_, _) => _ = EditExtra();
        var row = Ui.Row(12, title,
            new TextBlock { Text = Loc.T("Mean size X50"), Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center }, _x50, _x50Unit,
            new TextBlock { Text = Loc.T("Tolerance ±, %"), Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center }, _tolerance,
            _extra, change);
        var dock = new DockPanel();
        DockPanel.SetDock(_reach, Dock.Right);
        dock.Children.Add(_reach);
        dock.Children.Add(row);
        return dock;
    }

    private Control HoleCard()
    {
        var title = new TextBlock { Text = Loc.T("Hole and charge"), FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var mode = _hole.ModeSwitch();
        DockPanel.SetDock(mode, Dock.Right);
        head.Children.Add(mode);
        head.Children.Add(title);
        return FragmentationUi.Card(new DockPanel { Children = { head, _hole } }.Also(d => DockPanel.SetDock(head, Dock.Top)));
    }

    private Control SpaceCard()
    {
        var title = new TextBlock { Text = Loc.T("What may change"), FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center };
        var areaRow = Ui.Row(8, new TextBlock { Text = Loc.T("Search area"), Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center }, _area);
        var head = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10), Children = { title, areaRow } };
        var grid = Chip("targetGrid", () => { });
        grid.Content = "✓ " + Loc.T("Grid B × S");
        grid.IsHitTestVisible = false;
        Ui.SetChip(grid, true);
        var chips = new WrapPanel { Orientation = Orientation.Horizontal, Children = { grid, _ratioChip, _diameterChip, _azimuthChip } };
        foreach (var c in chips.Children) c.Margin = new Thickness(0, 0, 8, 8);
        var limits = FormKit.Secondary(Loc.T("Limits…"), () => _ = EditLimits());
        limits.Name = "targetLimits";
        var actions = Ui.Row(10, _solve, limits);
        actions.Margin = new Thickness(0, 6, 0, 8);
        return FragmentationUi.Card(new StackPanel { Children = { head, chips, actions, _status } });
    }

    private static Button Chip(string name, Action click)
    {
        var b = Ui.Chip("", click);
        b.Name = name;
        b.Height = 32;
        return b;
    }

    private static TextBox NumberBox(string name, double width, double font) => new()
    {
        Name = name, Width = width, Height = font > 18 ? 40 : 32, FontSize = font, FontFamily = Look.Mono, Background = Look.Field, BorderBrush = Look.LineStrong,
        Padding = new Thickness(8, 4), HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center
    };

    private static string N(double v, string f) => v.ToString(f, CultureInfo.CurrentCulture);
    private string Size(double cm) => N(SizeUnits.FromCm(cm, _editor.Unit), SizeUnits.Format(_editor.Unit));
    private string Unit => Loc.Ui(SizeUnits.Symbol(_editor.Unit));
    private static string M(double m) => N(Qty.Show(BlastDesigner.Domain.Quantity.Length, m), "0.0");
    private static double? Parse(string? s) =>
        double.TryParse((s ?? "").Replace(" ", "").Replace(',', '.').TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    // ---------- inputs ----------

    private void ShowTarget()
    {
        _x50.Text = Size(Target.X50Cm);
        _x50Unit.Text = Unit;
        _tolerance.Text = N(Target.TolerancePct, "0.#");
        var extra = new List<string>();
        if (Target.X80MaxCm is double x80) extra.Add(Loc.F(Loc.T("X80 at most {0} {1}"), Size(x80), Unit));
        if (Target.OversizeMaxPct is double ov) extra.Add(Loc.F(Loc.T("oversize at most {0} %"), N(ov, "0.#")));
        if (Target.FinesMaxPct is double fi) extra.Add(Loc.F(Loc.T("fines at most {0} %"), N(fi, "0.#")));
        _extra.Text = extra.Count == 0 ? Loc.T("No other targets") + " ·" : Loc.T("Also:") + " " + string.Join(", ", extra) + " ·";
    }

    private void TargetEdited()
    {
        if (Parse(_x50.Text) is double x && x > 0) Target.X50Cm = SizeUnits.ToCm(x, _editor.Unit);
        if (Parse(_tolerance.Text) is double t && t > 0) Target.TolerancePct = Math.Min(50, t);
        ShowTarget();
        Changed();
    }

    /// <summary>The rock inputs changed: the base again with the new rock, the prediction of the present grid.</summary>
    private void InputsChanged()
    {
        if (_hole == null) return;
        _base = FragBase.Of(_p, AreaId, _editor.Draft);
        var copy = ProjectJson.DeepClone(_p);
        copy.Fragmentation = _editor.Draft;
        _preview.Show(Fragmentation.Compute(copy), _editor.Unit);
        _previewTitle.Text = Loc.F(Loc.T("Prediction at the present grid {0} × {1} m"), M(_base.Burden), M(_base.Spacing));
        ShowTarget();
        Changed();
    }

    /// <summary>
    /// Anything of the search changed: the variants found go (they no longer answer it), the reachable range and the
    /// estimate by the formulas are worked out again at once.
    /// </summary>
    private void Changed()
    {
        if (_hole == null) return;
        _cts?.Cancel();
        _result = null;
        _rows.Clear();
        _selected = -1;
        _ratioChip.Content = (_ratioFree ? "✓ " : "") + (_ratioFree ? Loc.F(Loc.T("S/B {0}–{1}"), N(_constraints.RatioMin, "0.0"), N(_constraints.RatioMax, "0.0")) : Loc.T("S/B as now"));
        Ui.SetChip(_ratioChip, _ratioFree);
        _diameterChip.Content = (_diameters.Count > 1 || _diameters[0] != _base.DiameterMm ? "✓ " : "") + Loc.F(Loc.T("Diameter: {0} mm"), string.Join(", ", _diameters.Order().Select(d => N(d, "0.#"))));
        Ui.SetChip(_diameterChip, _diameters.Count > 1 || _diameters[0] != _base.DiameterMm);
        _azimuthChip.Content = (_azimuth ? "✓ " + Loc.F(Loc.T("Azimuth ±{0}°: fewer holes"), N(_azimuthWindow, "0")) : Loc.T("Azimuth not changed"));
        Ui.SetChip(_azimuthChip, _azimuth);
        _hole.SetGeometry(_base.BenchHeight, _hole.Subdrill ?? _base.Subdrill, _diameters[0], FragTargetSolver.StemmingFor(Space(), _base.Burden, _diameters[0]));
        // the range and the grid by the formulas only, at once
        var quick = Space();
        quick.RefineByProfile = false;
        quick.AzimuthWindowDeg = 0;
        try
        {
            var r = FragTargetSolver.Solve(_base, Target, quick);
            _reach.Text = r.RangeMaxCm > 0 ? Loc.F(Loc.T("Reachable: {0}–{1} {2}"), Size(r.RangeMinCm), Size(r.RangeMaxCm), Unit) : "";
            var est = r.Candidates.FirstOrDefault() ?? r.Nearest;
            _status.Text = est == null ? "" : Loc.F(Loc.T("By the formulas: {0} × {1} m, X50 ≈ {2} {3}. «Find» checks it on the project."), M(est.Design.Burden), M(est.Design.Spacing), Size(est.Metrics.X50Cm), Unit)
                + "  " + Loc.F(Loc.T("{0} explosives in the products"), FragTargetSolver.ExplosiveCandidates(_p, false).Count);
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        ShowTable();
    }

    private FragDesignSpace Space()
    {
        var s = new FragDesignSpace
        {
            DiametersMm = _diameters.ToList(),
            Schemes = _hole?.Schemes() ?? new(),
            RatioFree = _ratioFree,
            Stemming = _hole?.StemmingMode ?? StemmingMode.Fit,
            StemmingLength = _hole?.StemmingLength ?? 3,
            Subdrill = _hole?.Subdrill,
            AzimuthWindowDeg = _azimuth ? _azimuthWindow : 0,
            Constraints = _constraints.Clone()
        };
        return s;
    }

    // ---------- the search ----------

    /// <summary>Runs the solver in the background (a second press stops it); the variants fill the table.</summary>
    public async Task Solve()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        _editor.Commit();
        TargetEdited();
        var cts = _cts = new CancellationTokenSource();
        _solve.Content = Loc.T("Stop");
        var progress = new Progress<double>(v => _status.Text = Loc.F(Loc.T("Searching… {0} %"), N(v * 100, "0")));
        FragSolveResult? r = null;
        var space = Space();
        var b = _base;
        var target = Target.Clone();
        try { r = await Task.Run(() => FragTargetSolver.Solve(b, target, space, progress, cts.Token)); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Dialogs.ShowError(ex); }
        finally
        {
            if (_cts == cts) _cts = null;
            _solve.Content = Loc.T("Find");
        }
        if (r == null || cts.IsCancellationRequested) { _status.Text = Loc.T("Stopped."); return; }
        _result = r;
        _rows = new List<FragCandidate> { r.Current! }.Concat(r.Candidates).ToList();
        _selected = r.Candidates.Count > 0 ? 1 : 0;
        _reach.Text = Loc.F(Loc.T("Reachable: {0}–{1} {2}"), Size(r.RangeMinCm), Size(r.RangeMaxCm), Unit);
        _status.Text = Loc.F(Loc.T("Found {0} variants in {1} s"), r.Candidates.Count, N(r.Elapsed.TotalSeconds, "0.0"))
            + " · " + Loc.F(Loc.T("{0} explosives in the products"), FragTargetSolver.ExplosiveCandidates(_p, false).Count)
            + (r.FormulasChanged ? " · " + Loc.T("the profile has formulas of its own") : "")
            + (r.NonMonotone ? " · " + Loc.T("X50 does not grow with the grid on this profile: searched on points") : "");
        if (r.Candidates.Count == 0) _status.Text = Unreachable(r);
        ShowTable();
    }

    /// <summary>Why no variant: the reachable range, the nearest variant and the limit that stops it.</summary>
    private string Unreachable(FragSolveResult r)
    {
        var limit = r.Binding is FragLimit l ? LimitText(l) : Loc.T("a limit");
        var near = r.Nearest;
        return Loc.F(Loc.T("Within the limits {0}–{1} {2} can be reached. Nearest to {3} is a variant of {4} {2}, stopped by {5}; try a smaller diameter or a stronger explosive."),
            Size(r.RangeMinCm), Size(r.RangeMaxCm), Unit, Size(Target.X50Cm), near != null ? Size(near.Metrics.X50Cm) : "—", limit);
    }

    private string LimitText(FragLimit l)
    {
        var (format, value) = FragLimits.Describe(l, _constraints, Target);
        return Loc.F(Loc.T(format), N(value, "0.##"));
    }

    // ---------- the variants ----------

    private static readonly string[] Heads = { "", "Grid B × S, m", "S/B", "d, mm", "Explosive", "q, kg/m³", "X50", "n", "X80", "Oversize", "Holes", "Cost", "Note" };

    private void ShowTable()
    {
        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        _table.ColumnDefinitions = new ColumnDefinitions("28,Auto,Auto,Auto,*,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto");
        _table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < Heads.Length; c++)
        {
            var text = c is 6 or 8 ? Loc.T(Heads[c]) + ", " + Unit : Loc.T(Heads[c]);
            var t = new TextBlock { Text = text, Foreground = Look.Faint, FontSize = 12, Margin = new Thickness(8, 6, 10, 8) };
            Grid.SetColumn(t, c);
            _table.Children.Add(t);
        }
        if (_rows.Count == 0)
        {
            _table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var hint = new TextBlock { Text = Loc.T("Press «Find» to find the variants."), Foreground = Look.Faint, Margin = new Thickness(12, 8), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(hint, 1);
            Grid.SetColumnSpan(hint, Heads.Length);
            _table.Children.Add(hint);
            ShowSelected();
            return;
        }
        var cur = _rows[0].Metrics.CostPerM3;
        for (int i = 0; i < _rows.Count; i++)
        {
            var x = _rows[i];
            var m = x.Metrics;
            var row = i + 1;
            _table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var selected = i == _selected;
            var back = new Border { Background = selected ? Look.AccentSoft : Brushes.Transparent, BorderBrush = selected ? Look.Accent : Look.Line, BorderThickness = new Thickness(selected ? 3 : 0, 1, 0, 0), Name = "targetRow" + i, Cursor = new Cursor(StandardCursorType.Hand) };
            var index = i;
            back.PointerPressed += (_, _) => Select(index);
            Grid.SetRow(back, row);
            Grid.SetColumnSpan(back, Heads.Length);
            _table.Children.Add(back);
            var cost = x.IsCurrent || cur <= 0 ? "—" : (m.CostPerM3 / cur - 1 >= 0 ? "+" : "−") + N(Math.Abs(m.CostPerM3 / cur - 1) * 100, "0") + " %";
            var holes = x.IsCurrent ? m.HoleCount.ToString(CultureInfo.CurrentCulture) : $"{m.HoleCount} ({(x.HoleDelta >= 0 ? "+" : "−")}{Math.Abs(x.HoleDelta)})";
            var cells = new[]
            {
                x.IsCurrent ? "" : selected ? "◉" : "○",
                $"{M(x.Design.Burden)} × {M(x.Design.Spacing)}", N(x.Ratio, "0.00"), N(x.Design.DiameterMm, "0"), SchemeText(x.Design),
                N(m.PowderFactor, "0.00"), Size(m.X50Cm), N(m.Uniformity, "0.00"), Size(m.X80Cm), N(m.OversizePct, "0.0") + " %", holes, cost
            };
            for (int c = 0; c < cells.Length; c++)
            {
                var t = new TextBlock
                {
                    Text = cells[c], Margin = new Thickness(8, 7, 10, 7), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = c == 0 ? Look.Accent : x.IsCurrent ? Look.Muted : Look.Text, FontWeight = c == 6 ? FontWeight.SemiBold : FontWeight.Normal,
                    FontFamily = c is 1 or 2 or 3 or 5 or 6 or 7 or 8 or 9 or 10 or 11 ? Look.Mono : FontFamily.Default, IsHitTestVisible = false
                };
                Grid.SetRow(t, row);
                Grid.SetColumn(t, c);
                _table.Children.Add(t);
            }
            var notes = Ui.Row(6);
            notes.IsHitTestVisible = false;
            notes.Margin = new Thickness(8, 4);
            if (x.IsCurrent) notes.Children.Add(new TextBlock { Text = Loc.T("Current"), Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center });
            if (i == 1 && !x.IsCurrent) notes.Children.Add(Badge(Loc.T("Recommended"), Look.Accent, Look.OnAccent));
            foreach (var w in x.Warnings) notes.Children.Add(Badge(LimitText(w), Look.AccentSoft, Look.Accent));
            if (m.Estimate && !x.IsCurrent) notes.Children.Add(Badge(Loc.T("estimate"), Look.Hover, Look.Muted));
            Grid.SetRow(notes, row);
            Grid.SetColumn(notes, Heads.Length - 1);
            _table.Children.Add(notes);
        }
        ShowSelected();
    }

    private static Border Badge(string text, IBrush back, IBrush fore) => new()
    {
        Background = back, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2),
        Child = new TextBlock { Text = text, Foreground = fore, FontSize = 12, FontWeight = FontWeight.SemiBold }
    };

    /// <summary>Chooses a row of the table: the hole drawn for it, its curve and what applying it would do.</summary>
    public void Select(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        _selected = index;
        ShowTable();
    }

    /// <summary>The variants found, the present design first (for the smoke test).</summary>
    public IReadOnlyList<FragCandidate> Rows => _rows;
    public FragSolveResult? Result => _result;

    private void ShowSelected()
    {
        var x = _selected >= 0 && _selected < _rows.Count ? _rows[_selected] : null;
        _hole.ShowDesign(x == null || x.IsCurrent ? null : x.Design);
        _apply.IsEnabled = x is { IsCurrent: false };
        DrawChart(x);
        ShowChanges(x);
    }

    private void DrawChart(FragCandidate? x)
    {
        var current = _rows.Count > 0 ? _rows[0].Metrics : null;
        var unit = _editor.Unit;
        var band = KuzRamInverse.Uncertainty(_editor.Draft);
        var target = Target.X50Cm;
        _chartTitle.Text = Loc.T("Size distribution");
        _chartLegend.Text = Loc.F(Loc.T("- - current   —— variant   ▒ A ±{0} %"), N(band * 100, "0"));
        ToolTip.SetTip(_sizeAxis, Loc.F(Loc.T("Size of the piece, {0}; the scale is logarithmic"), Unit));
        ToolTip.SetTip(_passAxis, Loc.T("Passing, % of the mass: the share of pieces smaller than the size below"));
        var currentPrev = _preview.Result;
        var cx50 = current?.X50Cm ?? currentPrev?.X50Cm ?? 0;
        var cn = current?.Uniformity ?? currentPrev?.Uniformity ?? 1;
        var variant = x is { IsCurrent: false } ? x.Metrics : null;
        var targetText = Loc.F(Loc.T("target {0}"), Size(target));
        _chart.Paint = (c, w, h) => DrawGrading(c, w, h, unit, cx50, cn, variant?.X50Cm ?? 0, variant?.Uniformity ?? 1, band, target, targetText);
        _chart.InvalidateVisual();
    }

    /// <summary>
    /// The passing curves on a log size axis: the present one dashed, the variant solid with the band of A round it (X50 goes
    /// with A), the target marked on the 50 % line.
    /// </summary>
    private static void DrawGrading(SKCanvas c, float w, float h, SizeUnit unit, double currentX50, double currentN, double x50, double n, double band, double target, string targetText)
    {
        using var bg = new SKPaint { Color = Chart.Bg };
        c.DrawRect(0, 0, w, h, bg);
        var plot = new SKRect(AxisLeft, 10, w - 14, h - AxisBottom);
        if (plot.Width < 40 || plot.Height < 30) return;
        // the axis from a tenth of the smallest X50 to five times the largest, in cm
        var xs = new[] { currentX50, x50, target }.Where(v => v > 0).ToList();
        if (xs.Count == 0) return;
        double lo = Math.Log10(Math.Max(0.05, xs.Min() / 15)), hi = Math.Log10(xs.Max() * 6);
        float X(double cm) => plot.Left + (float)((Math.Log10(Math.Max(1e-6, cm)) - lo) / (hi - lo)) * plot.Width;
        float Y(double pct) => plot.Bottom - (float)(pct / 100) * plot.Height;
        using var grid = new SKPaint { Color = new SKColor(0xFF2C3138), StrokeWidth = 1, IsAntialias = true };
        using var text = new SKPaint { Color = new SKColor(0xFF8A929D), TextSize = 10.5f, IsAntialias = true };
        foreach (var pct in new[] { 0, 50, 100 })
        {
            c.DrawLine(plot.Left, Y(pct), plot.Right, Y(pct), grid);
            c.DrawText(pct.ToString(CultureInfo.CurrentCulture), plot.Left - 6, Y(pct) + 4, new SKPaint { Color = text.Color, TextSize = 10.5f, IsAntialias = true, TextAlign = SKTextAlign.Right });
        }
        // ticks at 1, 2, 5 of each decade of the unit shown
        for (int dec = -2; dec <= 4; dec++)
            foreach (var m in new[] { 1, 2, 5 })
            {
                var shown = m * Math.Pow(10, dec);
                var cm = SizeUnits.ToCm(shown, unit);
                var px = X(cm);
                if (px < plot.Left - 0.5f || px > plot.Right + 0.5f) continue;
                c.DrawLine(px, plot.Top, px, plot.Bottom, grid);
                c.DrawText(shown.ToString("0.##", CultureInfo.CurrentCulture), px, plot.Bottom + 14, new SKPaint { Color = text.Color, TextSize = 10.5f, IsAntialias = true, TextAlign = SKTextAlign.Center });
            }
        SKPath Curve(double a50, double un)
        {
            var path = new SKPath();
            for (int k = 0; k <= 80; k++)
            {
                var cm = Math.Pow(10, lo + (hi - lo) * k / 80);
                var pt = new SKPoint(X(cm), Y(100 * Fragmentation.RosinRammler(cm, a50, un)));
                if (k == 0) path.MoveTo(pt); else path.LineTo(pt);
            }
            return path;
        }
        if (x50 > 0)
        {
            // the band: the curve of A + band to the curve of A − band
            using var band1 = Curve(x50 * (1 + band), n);
            using var band2 = Curve(x50 * (1 - band), n);
            using var area = new SKPath();
            area.AddPath(band1);
            var pts = band2.Points.Reverse().ToArray();
            foreach (var pt in pts) area.LineTo(pt);
            area.Close();
            using var fill = new SKPaint { Color = new SKColor(0x40F0A63A), IsAntialias = true, Style = SKPaintStyle.Fill };
            c.DrawPath(area, fill);
        }
        if (currentX50 > 0)
        {
            using var dash = new SKPaint { Color = new SKColor(0xFFA3ABB6), StrokeWidth = 1.5f, IsAntialias = true, Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0) };
            using var path = Curve(currentX50, currentN);
            c.DrawPath(path, dash);
        }
        if (x50 > 0)
        {
            using var pen = new SKPaint { Color = new SKColor(0xFFF0A63A), StrokeWidth = 2.2f, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var path = Curve(x50, n);
            c.DrawPath(path, pen);
        }
        // the target on the 50 % line
        using var dot = new SKPaint { Color = new SKColor(0xFFE6E9ED), StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash(new[] { 2f, 3f }, 0) };
        c.DrawLine(X(target), plot.Top, X(target), plot.Bottom, dot);
        c.DrawLine(plot.Left, Y(50), X(target), Y(50), dot);
        using var mark = new SKPaint { Color = new SKColor(0xFFF0A63A), IsAntialias = true };
        c.DrawCircle(X(target), Y(50), 4, mark);
        c.DrawText(targetText, X(target) + 6, Y(50) - 6, text);
    }

    /// <summary>What applying the variant does: the holes moved, added and deleted (laid out on a copy), the azimuth, the charge, the rule.</summary>
    private void ShowChanges(FragCandidate? x)
    {
        _changes.Children.Clear();
        _changes.RowDefinitions.Clear();
        if (x == null || x.IsCurrent)
        {
            _changesTitle.Text = Loc.T("What will change");
            AddChange(Loc.T("Choose a variant in the table."), "");
            _saveRule.IsVisible = false;
            return;
        }
        _changesTitle.Text = Loc.F(Loc.T("What will change: variant {0}"), _selected);
        var copy = ProjectJson.DeepClone(_p);
        var d = DesignToApply(x);
        var r = FragApply.Apply(copy, RootIds, d, MainViewModel.TemplateOf(copy, RootIds));
        if (r.Refusal != null) { AddChange(Loc.T("Cannot be applied"), Loc.T(r.Refusal)); return; }
        AddChange(Loc.T("Holes to move"), r.Moved.ToString(CultureInfo.CurrentCulture));
        AddChange(Loc.T("Holes to add"), r.Added.ToString(CultureInfo.CurrentCulture));
        AddChange(Loc.T("Holes to delete"), r.Deleted.ToString(CultureInfo.CurrentCulture));
        if (Math.Abs(x.Design.AzimuthDelta) > 1e-9) AddChange(Loc.T("Grid azimuth"), $"{N(_base.Azimuth, "0")}° → {N(x.Azimuth, "0")}°");
        AddChange(Loc.T("Charging of the variant"), SchemeText(x.Design));
        AddChange(Loc.T("Rule"), "«" + d.RuleName + "»");
        _saveRule.IsVisible = true;
    }

    private void AddChange(string label, string value)
    {
        var row = _changes.RowDefinitions.Count;
        _changes.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var a = new TextBlock { Text = label, Foreground = Look.Muted };
        var b = new TextBlock { Text = value, Foreground = Look.Text, TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(a, row); Grid.SetRow(b, row); Grid.SetColumn(b, 1);
        _changes.Children.Add(a);
        _changes.Children.Add(b);
    }

    /// <summary>The design of a variant as Apply puts it: with the rock inputs (and the target) of the window and the rule's name.</summary>
    private FragDesign DesignToApply(FragCandidate x)
    {
        var d = x.Design.Clone();
        var rock = ProjectJson.DeepClone(_editor.Draft);
        rock.Target = Target.Clone();
        d.Rock = rock;
        d.SaveRule = _saveRule.IsChecked == true;
        d.RuleName = Loc.F(Loc.T("X50 target: {0}"), SchemeText(x.Design));
        return d;
    }

    /// <summary>«Bulk emulsion 2.0 m + ANFO», or the name of the one explosive; the present design as its holes are loaded.</summary>
    private string SchemeText(FragDesign d)
    {
        var column = _p.FindProduct(d.Scheme.ColumnProductId)?.Name ?? "?";
        if (d.Scheme.IsSingle) return column;
        return Loc.F(Loc.T("{0} {1} m + {2}"), _p.FindProduct(d.Scheme.BottomProductId)?.Name ?? "?", M(d.BottomLength), column);
    }

    // ---------- apply, close ----------

    private bool _closing;

    /// <summary>Applies the chosen variant as one undo step and closes the window.</summary>
    public async Task Apply()
    {
        if (_selected < 1 || _selected >= _rows.Count) return;
        var x = _rows[_selected];
        var report = await _vm.ApplyFragCandidate(RootIds, DesignToApply(x));
        if (report == null || report.Refusal != null) return;
        Applied = report;
        var got = Fragmentation.Compute(_vm.Project!).X50Cm;
        _vm.SetStatus(Loc.F(Loc.T("Target {0} {1}, got {2} {1}; {3} moved, {4} deleted, {5} added"), Size(Target.X50Cm), Unit, Size(got), report.Moved, report.Deleted, report.Added));
        _closing = true;
        Close();
    }

    /// <summary>Closes without applying: the project stays as it was; edited rock inputs may be saved to a preset first.</summary>
    public async Task CloseAsked()
    {
        _cts?.Cancel();
        _editor.Commit();
        if (RockJson(_editor.Draft) != _rockAtStart && !SkipPresetQuestion
            && await Dialogs.Confirm(Loc.T("The rock inputs were changed. The project will not take them; save them to a preset?"), Loc.T("Fragmentation target")))
            await _presets.SavePresetAs();
        _closing = true;
        Close();
    }

    /// <summary>The smoke test closes without the question.</summary>
    public bool SkipPresetQuestion { get; set; }

    // ---------- small dialogs ----------

    private Flyout DiameterFlyout()
    {
        var list = new StackPanel { Spacing = 4, Margin = new Thickness(6) };
        var own = NumberBox("ownDiameter", 80, 13);
        void Fill()
        {
            // the box outlives the row it sits in: out of the old row first, or the new row cannot take it
            if (own.Parent is Panel was) was.Children.Remove(own);
            list.Children.Clear();
            foreach (var d in _projectDiameters.Concat(_diameters).Distinct().Order())
            {
                var box = new CheckBox { Content = N(d, "0.#") + " " + Loc.Ui("mm"), IsChecked = _diameters.Contains(d), Tag = d, Foreground = Look.Text };
                box.IsCheckedChanged += (_, _) =>
                {
                    var v = (double)box.Tag!;
                    if (box.IsChecked == true) { if (!_diameters.Contains(v)) _diameters.Add(v); }
                    else if (_diameters.Count > 1) _diameters.Remove(v);
                    else box.IsChecked = true;
                    Changed();
                };
                list.Children.Add(box);
            }
            var add = FormKit.Secondary(Loc.T("Add"), () =>
            {
                if (Parse(own.Text) is double v && v >= 20 && v <= 1000 && !_diameters.Contains(v)) { _diameters.Add(v); own.Text = ""; Fill(); Changed(); }
            });
            list.Children.Add(Ui.Row(6, own, new TextBlock { Text = Loc.Ui("mm"), Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center }, add));
        }
        Fill();
        return new Flyout { Content = list, Placement = PlacementMode.BottomEdgeAlignedLeft };
    }

    /// <summary>Puts a diameter in the list to try (the smoke test, and «Add» of the flyout).</summary>
    public void AddDiameter(double d) { if (!_diameters.Contains(d)) { _diameters.Add(d); Changed(); } }

    private async Task EditExtra()
    {
        TextBox Box(double? v, string name) => new() { Name = name, Text = v is double x ? N(x, "0.##") : "", Width = 120, Background = Look.Field };
        var x80 = Box(Target.X80MaxCm is double a ? SizeUnits.FromCm(a, _editor.Unit) : null, "targetX80");
        var ov = Box(Target.OversizeMaxPct, "targetOversize");
        var fi = Box(Target.FinesMaxPct, "targetFines");
        var form = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                FormKit.Labeled(Loc.F(Loc.T("X80 at most, {0}"), Unit), x80),
                FormKit.Labeled(Loc.T("Oversize at most, %"), ov),
                FormKit.Labeled(Loc.T("Fines at most, %"), fi),
                new TextBlock { Text = Loc.T("Empty: no limit."), Foreground = Look.Faint }
            }
        };
        if (!await Dialogs.Custom("Other targets", form, width: 420)) return;
        Target.X80MaxCm = Parse(x80.Text) is double v80 && v80 > 0 ? SizeUnits.ToCm(v80, _editor.Unit) : null;
        Target.OversizeMaxPct = Parse(ov.Text) is double vo && vo > 0 ? vo : null;
        Target.FinesMaxPct = Parse(fi.Text) is double vf && vf > 0 ? vf : null;
        ShowTarget();
        Changed();
    }

    private async Task EditLimits()
    {
        var c = _constraints;
        var fields = new List<(string Label, Func<double> Get, Action<double> Set)>
        {
            ("B/d at least", () => c.BurdenPerDiameterMin, v => c.BurdenPerDiameterMin = v),
            ("B/d at most", () => c.BurdenPerDiameterMax, v => c.BurdenPerDiameterMax = v),
            ("S/B at least", () => c.RatioMin, v => c.RatioMin = v),
            ("S/B at most", () => c.RatioMax, v => c.RatioMax = v),
            ("Stemming at least, hole diameters", () => c.StemmingDiameters, v => c.StemmingDiameters = v),
            ("Stemming at least, share of B", () => c.StemmingPerBurden, v => c.StemmingPerBurden = v),
            ("Powder factor at least, kg/m³", () => c.PowderFactorMin, v => c.PowderFactorMin = v),
            ("Powder factor at most, kg/m³", () => c.PowderFactorMax, v => c.PowderFactorMax = v),
            ("Flyrock clearance at most, m (0: not checked)", () => c.MaxFlyrockClearance ?? 0, v => c.MaxFlyrockClearance = v > 0 ? v : null),
            ("PPV at the monitoring points at most, mm/s (0: not checked)", () => c.MaxPpv ?? 0, v => c.MaxPpv = v > 0 ? v : null),
            ("Uniformity n at least (warning)", () => c.MinUniformity, v => c.MinUniformity = v),
            ("Bench stiffness H/B at least (warning)", () => c.MinStiffness, v => c.MinStiffness = v),
            ("Azimuth window, ± degrees", () => _azimuthWindow, v => _azimuthWindow = Math.Clamp(v, 1, 180)),
        };
        var boxes = fields.Select(f => new TextBox { Text = N(f.Get(), "0.##"), Width = 110, Background = Look.Field }).ToList();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowSpacing = 6, ColumnSpacing = 12 };
        for (int i = 0; i < fields.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = new TextBlock { Text = Loc.T(fields[i].Label), Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, i); Grid.SetRow(boxes[i], i); Grid.SetColumn(boxes[i], 1);
            grid.Children.Add(l); grid.Children.Add(boxes[i]);
        }
        if (!await Dialogs.Custom("Limits", grid, width: 560, subtitle: Loc.T("The hard limits reject a variant, the warnings only mark it."))) return;
        for (int i = 0; i < fields.Count; i++) if (Parse(boxes[i].Text) is double v && v >= 0) fields[i].Set(v);
        Changed();
    }

    /// <summary>
    /// The rock factor from past blasts: the measured mean size of each with what it was loaded with gives its A; their mean
    /// is put in A and their spread draws the band of the chart. The blasts are kept with the rock inputs and their preset.
    /// </summary>
    private async Task Calibrate()
    {
        var list = ProjectJson.DeepClone(_editor.Draft.Calibration ?? new List<FragCalibration>());
        if (list.Count == 0 && _preview.Result is { } r)
            list.Add(new FragCalibration { Name = _p.Header.Name, MeasuredX50Cm = Math.Round(r.X50Cm, 1), PowderFactor = Math.Round(r.PowderFactor, 3), ChargeKg = Math.Round(r.ChargePerHole, 1), Rws = Math.Round(r.Rws) });
        var rows = new StackPanel { Spacing = 6 };
        var result = new TextBlock { Name = "calibrationResult", Foreground = Look.TextSoft, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        void Recalc()
        {
            var (mean, sd, n, values) = KuzRamInverse.Calibrate(list);
            result.Text = n == 0 ? Loc.T("Enter the measured X50 of past blasts with what they were loaded with.")
                : Loc.F(Loc.T("A of the blasts: {0}. Mean A = {1}, spread ±{2} ({3} %)."), string.Join(", ", values.Select(v => N(v, "0.0"))), N(mean, "0.0#"), N(sd, "0.0#"), N(mean > 0 ? sd / mean * 100 : 0, "0"));
        }
        void Fill()
        {
            rows.Children.Clear();
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("150,90,90,90,70,Auto") };
            string[] titles = { "Blast", "X50 measured, cm", "q, kg/m³", "Charge, kg", "RWS", "" };
            for (int i = 0; i < titles.Length; i++) { var t = new TextBlock { Text = Loc.T(titles[i]), Foreground = Look.Faint, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }; Grid.SetColumn(t, i); head.Children.Add(t); }
            rows.Children.Add(head);
            foreach (var b in list)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("150,90,90,90,70,Auto"), ColumnSpacing = 4 };
                TextBox Cell(string text, int col, Action<string> set)
                {
                    var t = new TextBox { Text = text, Background = Look.Field, Margin = new Thickness(0, 0, 4, 0) };
                    t.LostFocus += (_, _) => { set(t.Text ?? ""); Recalc(); };
                    Grid.SetColumn(t, col);
                    g.Children.Add(t);
                    return t;
                }
                Cell(b.Name, 0, s => b.Name = s);
                Cell(N(b.MeasuredX50Cm, "0.##"), 1, s => b.MeasuredX50Cm = Parse(s) ?? b.MeasuredX50Cm);
                Cell(N(b.PowderFactor, "0.###"), 2, s => b.PowderFactor = Parse(s) ?? b.PowderFactor);
                Cell(N(b.ChargeKg, "0.#"), 3, s => b.ChargeKg = Parse(s) ?? b.ChargeKg);
                Cell(N(b.Rws, "0"), 4, s => b.Rws = Parse(s) ?? b.Rws);
                var del = FormKit.Tool("close", null, () => { list.Remove(b); Fill(); Recalc(); }, Loc.T("Delete"));
                Grid.SetColumn(del, 5);
                g.Children.Add(del);
                rows.Children.Add(g);
            }
            rows.Children.Add(FormKit.Secondary(Loc.T("Add a blast"), () => { list.Add(new FragCalibration { Name = Loc.F(Loc.T("Blast {0}"), list.Count + 1), Rws = 100 }); Fill(); Recalc(); }));
        }
        Fill();
        Recalc();
        if (!await Dialogs.Custom("Calibrate A", new StackPanel { Children = { rows, result } }, width: 680, subtitle: Loc.T("A = X50·q^0.8 / (Q^(1/6)·(115/RWS)^(19/30)) from each measured blast"), okText: Loc.T("Take the mean A"))) return;
        var (m, _, count, _) = KuzRamInverse.Calibrate(list);
        _editor.Draft.Calibration = list.Count > 0 ? list : null;
        if (count > 0)
        {
            _editor.Draft.RockFactor = Math.Round(m, 2);
            _editor.Draft.RockFactorSet = true;
            _editor.Load(_editor.Draft);
        }
        else InputsChanged();
    }
}

internal static class ControlExtensions
{
    public static T Also<T>(this T c, Action<T> a) { a(c); return c; }
}
