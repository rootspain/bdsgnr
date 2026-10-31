using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.Rendering;

namespace BlastDesigner.App.Views;

/// <summary>
/// Parameters of the electronic timing line: type, start time, direction and per-segment burn rate / relief / progress,
/// with the planned hole times previewed on the plan and summarised before they are applied.
/// </summary>
public static class TimingLineWindow
{
    static TimingLineOptions _last = new();
    static TimingLineSegment _lastSegment = new(5, 8, 0);
    static bool _sameForAll = true;

    /// <summary>
    /// New line from picked points (<paramref name="existing"/> null) or a line of the project to edit: its values fill the
    /// form, which then also offers its layer and reprogramming the holes. <paramref name="holes"/> are the holes it times.
    /// </summary>
    public static async Task<TimingLineSetup?> Show(MainViewModel vm, IReadOnlyList<Vec3> points, IReadOnlyList<Hole> holes, TimingLineEntity? existing = null)
    {
        var source = existing != null ? EbsTimingLine.OptionsOf(existing) : _last;
        var options = new TimingLineOptions
        {
            Style = source.Style, StartOffsetMs = source.StartOffsetMs, UseProgressive = source.UseProgressive, ProgressRate = source.ProgressRate,
            ReverseOrder = source.ReverseOrder, BurdenReliefMsPerM = source.BurdenReliefMsPerM, Angle1Deg = source.Angle1Deg, Angle2Deg = source.Angle2Deg
        };
        var count = Math.Max(1, points.Count - 1);
        var segments = existing != null && existing.Segments.Count >= count
            ? existing.Segments.Take(count).ToList()
            : Enumerable.Range(0, count).Select(_ => existing?.Segments.FirstOrDefault() ?? _lastSegment).ToList();
        var same = existing != null ? segments.All(s => s == segments[0]) : _sameForAll;
        var preview = new TimingLinePreviewOverlay();
        var form = new TimingLineForm(vm, points, holes, options, segments, same, preview, existing);
        var w = new Window
        {
            Title = existing != null ? Loc.T("Edit timing line") : Loc.T("Timing line"),
            Width = 760,
            Height = 700,
            MinWidth = 640,
            MinHeight = 480,
            Background = Look.Ground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = form
        };
        form.Closing = ok => { form.Applied = ok; w.Close(); };
        // The plan shows the planned times while the window is open; nothing changes in the project until Apply.
        // An edited line is hidden meanwhile, so the preview is not drawn over its old self.
        vm.View.Overlays.Add(preview);
        var hid = existing != null && vm.HiddenEntities.Add(existing.Id);
        try { await Dialogs.ShowModal(w); }
        finally
        {
            vm.View.Overlays.Remove(preview);
            if (hid) vm.HiddenEntities.Remove(existing!.Id);
            vm.View.Redraw();
        }
        if (!form.Applied) return null;
        var result = form.Segments();
        if (existing == null)
        {
            _last = form.Options;
            if (result.Count > 0) _lastSegment = result[0];
            _sameForAll = form.SameForAll;
        }
        return new TimingLineSetup(form.Options, result, form.LayerId, form.Reprogram);
    }
}

/// <summary>What the timing line window returns: the values, the layer (edit only) and whether to reprogram the holes.</summary>
public sealed record TimingLineSetup(TimingLineOptions Options, List<TimingLineSegment> Segments, Guid? LayerId, bool Reprogram);

/// <summary>Content of the timing line window. Public so the UI smoke test can reach it.</summary>
public sealed class TimingLineForm : DockPanel
{
    private static readonly IBrush Ok = new SolidColorBrush(Color.FromUInt32(0xFF5BC08A));
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromUInt32(0xFFF0C13A));
    private static readonly uint[] SegmentColours = { 0xFF4C9BFF, 0xFF3FB97A, 0xFF9B7BFF };
    private static readonly string[] Exponents = { "1.1", "1.2", "1.4", "1.6", "2.0" };

    private readonly MainViewModel _vm;
    private readonly IReadOnlyList<Vec3> _points;
    private readonly IReadOnlyList<Hole> _holes;
    private readonly TimingLinePreviewOverlay _preview;
    private readonly List<TimingLineSegment> _segments;
    private readonly StackPanel _body = new() { Spacing = 20, Margin = new Thickness(18, 16, 18, 18) };
    private readonly StackPanel _result = new() { Spacing = 5 };
    private readonly Button _apply;

    public TimingLineForm(MainViewModel vm, IReadOnlyList<Vec3> points, IReadOnlyList<Hole> holes, TimingLineOptions options, List<TimingLineSegment> segments, bool sameForAll, TimingLinePreviewOverlay preview, TimingLineEntity? existing = null)
    {
        _vm = vm;
        _points = points;
        _holes = holes;
        _preview = preview;
        _existing = existing;
        Options = options;
        SameForAll = sameForAll;
        _segments = segments;
        LayerId = existing?.LayerId;

        var header = Header();
        SetDock(header, Dock.Top);
        Children.Add(header);
        _apply = new Button
        {
            Height = 32,
            Padding = new Thickness(16, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { "primary" },
            Tag = "apply"
        };
        var footer = Footer();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);
        Children.Add(new ScrollViewer { Content = _body });
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control) { Closing?.Invoke(true); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Build();
    }

    private readonly TimingLineEntity? _existing;

    public TimingLineOptions Options { get; }
    public bool SameForAll { get; private set; }
    /// <summary>Editing a line: its layer, and whether Apply also reprograms the holes it times (on by default).</summary>
    public Guid? LayerId { get; private set; }
    public bool Reprogram { get; private set; } = true;
    private bool Editing => _existing != null;
    public bool Applied { get; set; }
    public Action<bool>? Closing { get; set; }
    /// <summary>Planned result of the current values (refreshed on every change).</summary>
    public TimingLineResult? Planned { get; private set; }
    public IReadOnlyDictionary<Guid, double> PlannedTimes { get; private set; } = new Dictionary<Guid, double>();

    private static string L(string english) => Loc.Ui(english);
    private static string N(double v, string format = "0.##") => v.ToString(format, CultureInfo.CurrentCulture);
    private static double? Parse(string? s) =>
        double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    private bool Advanced => Options.Style == TimingLineStyle.AdvancedBurn;
    private int SegmentCount => _segments.Count;

    /// <summary>The values of every segment, the first row copied to all when "same for all segments" is on.</summary>
    public List<TimingLineSegment> Segments() => SameForAll ? _segments.Select(_ => _segments[0]).ToList() : _segments.ToList();

    // ---------- frame ----------

    private Control Header()
    {
        var length = Enumerable.Range(0, Math.Max(0, _points.Count - 1)).Sum(i => Vec3.Distance2D(_points[i], _points[i + 1]));
        Control Chip(string text) => new Border
        {
            Height = 24,
            Padding = new Thickness(9, 0),
            CornerRadius = new CornerRadius(12),
            Background = Look.Ribbon,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 12, Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center }
        };
        var selected = _vm.Doc?.SelectedHoles.Any() == true;
        var holes = Editing && _existing!.HoleIds.Count > 0 ? Loc.F("Holes of this line: {0}", _holes.Count)
            : selected ? Loc.F("Selected holes: {0}", _holes.Count) : Loc.F("All holes: {0}", _holes.Count);
        var row = new WrapPanel { Children =
        {
            new TextBlock { Text = Editing ? Loc.T("Edit timing line") : Loc.T("Timing line"), FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) },
            Chip(Loc.F("Segments: {0} · {1} m", SegmentCount, N(length, "0"))),
            Chip(holes)
        } };
        foreach (var c in row.Children.Skip(1).OfType<Control>()) c.Margin = new Thickness(0, 0, 6, 0);
        if (!Editing) return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 12), Child = row };

        var layers = _vm.Project!.Layers;
        var layer = new ComboBox
        {
            ItemsSource = layers.Select(l => Loc.Name(l.Name)).ToList(),
            SelectedIndex = layers.FindIndex(l => l.Id == LayerId),
            MinWidth = 150,
            Height = 30,
            VerticalAlignment = VerticalAlignment.Center,
            Name = "layer"
        };
        layer.SelectionChanged += (_, _) => { if (layer.SelectedIndex >= 0) LayerId = layers[layer.SelectedIndex].Id; };
        var right = Ui.Row(8, new TextBlock { Text = Loc.T("Layer"), Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center }, layer);
        right.Margin = new Thickness(12, 0, 0, 0);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(right, 1);
        g.Children.Add(row);
        g.Children.Add(right);
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 12), Child = g };
    }

    private Control Footer()
    {
        var cancel = new Button
        {
            Content = Loc.T("Cancel"),
            Height = 32,
            Padding = new Thickness(14, 0),
            CornerRadius = new CornerRadius(6),
            BorderBrush = Look.LineStrong,
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsCancel = true
        };
        cancel.Click += (_, _) => Closing?.Invoke(false);
        ToolTip.SetTip(_apply, L("Apply") + " (Ctrl+Enter)");
        _apply.Click += (_, _) => Closing?.Invoke(true);
        var hint = new TextBlock { Text = L("Mouse wheel or ↑/↓ change a value; Ctrl+Enter applies, Esc cancels"), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var buttons = Ui.Row(8, cancel, _apply);
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        dock.Children.Add(hint);
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 12), Child = dock };
    }

    private static Control Section(string title, params Control[] content)
    {
        var s = new StackPanel { Spacing = 10 };
        s.Children.Add(new TextBlock { Text = L(title).ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Faint });
        foreach (var c in content) s.Children.Add(c);
        return s;
    }

    private static Control Labeled(string label, Control field) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = L(label), FontSize = 12, Foreground = Look.Muted }, field }
    };

    /// <summary>
    /// Number field with its unit inside; the value is taken on every valid keystroke (the preview follows), and the mouse
    /// wheel or ↑/↓ step it.
    /// </summary>
    private TextBox Number(string name, double value, string unit, double step, double min, double max, Action<double> set)
    {
        var box = new TextBox
        {
            Name = name,
            Text = N(value),
            Width = 130,
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(8, 5),
            FontFamily = Look.Mono,
            Background = Look.Field,
            BorderBrush = Look.LineStrong,
            HorizontalAlignment = HorizontalAlignment.Left,
            InnerRightContent = new TextBlock { Text = L(unit), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }
        };
        var current = value;
        void Take(double v, bool rewrite)
        {
            v = Math.Clamp(v, min, max);
            current = v;
            set(v);
            if (rewrite) { box.Text = N(v); box.CaretIndex = box.Text.Length; }
            Recompute();
        }
        box.TextChanged += (_, _) => { if (Parse(box.Text) is double v && Math.Abs(v - current) > 1e-9 && v >= min && v <= max) Take(v, false); };
        box.LostFocus += (_, _) => box.Text = N(current);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Up) { Take(current + step, true); e.Handled = true; }
            else if (e.Key == Key.Down) { Take(current - step, true); e.Handled = true; }
        };
        box.PointerWheelChanged += (_, e) => { Take(current + Math.Sign(e.Delta.Y) * step, true); e.Handled = true; };
        return box;
    }

    // ---------- body ----------

    private void Build()
    {
        _body.Children.Clear();
        _body.Children.Add(Section("Line type", TypeCards()));

        var start = Number("start", Options.StartOffsetMs, "ms", 1, 0, 100000, v => Options.StartOffsetMs = v);
        var startRow = new WrapPanel();
        startRow.Children.Add(new Border { Margin = new Thickness(0, 0, 28, 8), Child = Labeled("Time of the first hole", start) });
        if (!Advanced) startRow.Children.Add(new Border { Margin = new Thickness(0, 0, 28, 8), Child = Labeled("Direction", DirectionChips()) });
        if (Editing)
        {
            var sw = new ToggleSwitch { IsChecked = Reprogram, OnContent = "", OffContent = "", Name = "reprogram" };
            sw.IsCheckedChanged += (_, _) => { Reprogram = sw.IsChecked == true; Recompute(); };
            startRow.Children.Add(new Border
            {
                Margin = new Thickness(0, 0, 0, 8),
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = Ui.Row(8, sw, new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 260,
                    Children =
                    {
                        new TextBlock { Text = L("Reprogram the holes from the changed line"), Foreground = Look.TextSoft, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = L("Off: only the line changes, the holes keep their times"), FontSize = 11.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap }
                    }
                })
            });
        }
        _body.Children.Add(Section("Start", startRow));

        if (Advanced) _body.Children.Add(Section("Burn with angles", AdvancedFields()));
        else
        {
            _body.Children.Add(Section("Segments", SegmentTable()));
            _body.Children.Add(ProgressiveRow());
        }
        // One card for the life of the window: rebuilding the form only moves it to the end of the new body.
        _resultCard ??= new Border
        {
            Background = Look.Panel,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            Child = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = L("Result").ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Faint }, _result } }
        };
        _body.Children.Add(_resultCard);
        Recompute();
    }

    private Border? _resultCard;

    private Control TypeCards()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*,10,*") };
        var cards = new (TimingLineStyle Style, string Title, string Text, string Icon)[]
        {
            (TimingLineStyle.TimeLine, "Contour line", "Time grows with the distance from the line", "M2 22L22 2 M2 14L14 2 M10 22L22 10 M2 6L6 2 M18 22L22 18"),
            (TimingLineStyle.BurnLine, "Burn line", "The signal runs along the line and spreads to the sides", "M2 12H18 M14 8l4 4-4 4 M6 12v8 M12 12v8 M6 12V4 M12 12V4"),
            (TimingLineStyle.AdvancedBurn, "Burn with angles", "The front spreads to the sides at set angles", "M12 21V8 M12 8L3 3 M12 8L21 3 M12 21h.01")
        };
        for (int i = 0; i < cards.Length; i++)
        {
            var (style, title, text, icon) = cards[i];
            var active = Options.Style == style;
            var path = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(icon),
                Stroke = active ? Look.Accent : Look.Muted,
                StrokeThickness = 1.8,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Width = 24,
                Height = 24
            };
            var b = new Button
            {
                Content = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new Viewbox { Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, Child = path },
                        new TextBlock { Text = L(title), FontWeight = FontWeight.SemiBold, Foreground = active ? Look.Accent : Look.Text },
                        new TextBlock { Text = L(text), FontSize = 11.5, Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap }
                    }
                },
                Padding = new Thickness(12, 10),
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Top,
                Background = active ? Look.AccentSoft : Look.Panel,
                BorderBrush = active ? Look.Accent : Look.Line,
                BorderThickness = new Thickness(active ? 1.5 : 1),
                Tag = "type:" + style
            };
            b.Classes.Add("flat");
            b.Click += (_, _) => { if (Options.Style != style) { Options.Style = style; Build(); } };
            Grid.SetColumn(b, i * 2);
            grid.Children.Add(b);
        }
        return grid;
    }

    private Control DirectionChips()
    {
        var row = Ui.Row(6);
        foreach (var (reverse, text) in new[] { (false, "A → B"), (true, "B → A") })
        {
            var r = reverse;
            var chip = Ui.Chip(text, () => { if (Options.ReverseOrder != r) { Options.ReverseOrder = r; Build(); } }, Options.ReverseOrder == reverse, L("A and B are marked on the plan"));
            chip.Height = 32;
            chip.Tag = reverse ? "reverse" : "forward";
            row.Children.Add(chip);
        }
        return row;
    }

    private Control SegmentTable()
    {
        var burn = Options.Style == TimingLineStyle.BurnLine;
        var progress = Options.UseProgressive;
        var stack = new StackPanel { Spacing = 8 };
        if (SegmentCount > 1)
        {
            var same = new ToggleSwitch { IsChecked = SameForAll, OnContent = "", OffContent = "", Name = "sameForAll" };
            same.IsCheckedChanged += (_, _) => { SameForAll = same.IsChecked == true; Build(); };
            stack.Children.Add(Ui.Row(8, same, new TextBlock { Text = L("Same values for all segments"), Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center }));
        }
        var columns = new List<string> { "48", "70" };
        if (burn) columns.Add("Auto");
        columns.Add("Auto");
        if (progress) columns.Add("Auto");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", columns)), ColumnSpacing = 12, RowSpacing = 8 };
        void Put(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); grid.Children.Add(c); }
        TextBlock Head(string t) => new() { Text = L(t), FontSize = 12, Foreground = Look.Muted };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var col = 0;
        Put(Head("No."), 0, col++);
        Put(Head("Length"), 0, col++);
        if (burn) Put(Head("Burn rate"), 0, col++);
        Put(Head("Relief"), 0, col++);
        if (progress) Put(Head("Progress"), 0, col);

        var rows = SameForAll ? 1 : SegmentCount;
        for (int i = 0; i < rows; i++)
        {
            var k = i;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var length = SameForAll
                ? Enumerable.Range(0, SegmentCount).Sum(j => Vec3.Distance2D(_points[j], _points[j + 1]))
                : Vec3.Distance2D(_points[k], _points[k + 1]);
            var stripe = new Border { Width = 4, Height = 22, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromUInt32(SameForAll ? 0xFF8A929D : SegmentColours[k % SegmentColours.Length])), VerticalAlignment = VerticalAlignment.Center };
            Put(Ui.Row(8, stripe, new TextBlock { Text = SameForAll && SegmentCount > 1 ? L("All") : $"{k + 1}", Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center }), k + 1, 0);
            Put(new TextBlock { Text = N(length, "0.#") + " " + L("m"), FontFamily = Look.Mono, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center }, k + 1, 1);
            var s = _segments[k];
            col = 2;
            if (burn) Put(Number($"burn{k}", s.BurnRateMsPerM, "ms/m", 0.5, 0, 1000, v => _segments[k] = _segments[k] with { BurnRateMsPerM = v }), k + 1, col++);
            Put(Number($"relief{k}", s.ReliefMsPerM, "ms/m", 0.5, 0, 1000, v => _segments[k] = _segments[k] with { ReliefMsPerM = v }), k + 1, col++);
            if (progress) Put(Number($"progress{k}", s.ProgressMs, "ms/10 m", 1, 0, 10000, v => _segments[k] = _segments[k] with { ProgressMs = v }), k + 1, col);
        }
        stack.Children.Add(grid);
        return stack;
    }

    private Control ProgressiveRow()
    {
        var sw = new ToggleSwitch { IsChecked = Options.UseProgressive, OnContent = "", OffContent = "", Name = "progressive" };
        sw.IsCheckedChanged += (_, _) => { Options.UseProgressive = sw.IsChecked == true; Build(); };
        var row = new WrapPanel();
        row.Children.Add(new Border { Margin = new Thickness(0, 0, 24, 6), Child = Ui.Row(8, sw, new TextBlock { Text = L("Progressive relief"), Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center }) });
        if (Options.UseProgressive)
        {
            var chips = Ui.Row(6, new TextBlock { Text = L("Exponent"), Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            for (int i = 0; i < Exponents.Length; i++)
            {
                var k = i;
                chips.Children.Add(Ui.Chip(Exponents[i], () => { Options.ProgressRate = k; Build(); }, Math.Clamp(Options.ProgressRate, 0, Exponents.Length - 1) == i));
            }
            row.Children.Add(new Border { Margin = new Thickness(0, 0, 0, 6), Child = chips });
        }
        return row;
    }

    private Control AdvancedFields()
    {
        var wrap = new WrapPanel();
        void Add(string label, Control field) => wrap.Children.Add(new Border { Margin = new Thickness(0, 0, 24, 8), Child = Labeled(label, field) });
        Add("Relief along the line", Number("advRelief", Options.BurdenReliefMsPerM, "ms/m", 0.5, 0, 1000, v => Options.BurdenReliefMsPerM = v));
        Add("Left front angle", Number("angle1", Options.Angle1Deg, "°", 1, 0, 89, v => Options.Angle1Deg = v));
        Add("Right front angle", Number("angle2", Options.Angle2Deg, "°", 1, 0, 89, v => Options.Angle2Deg = v));
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                wrap,
                new TextBlock { Text = L("The angle arms are drawn from the first point on the plan; the relief to each side is the relief along the line × tan(angle)."), FontSize = 11.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap }
            }
        };
    }

    // ---------- result ----------

    private void Recompute()
    {
        var p = _vm.Project!;
        var segments = Segments();
        Dictionary<Guid, double> times;
        TimingLineResult result;
        try
        {
            (times, result) = EbsTimingLine.Plan(p, _holes, _points, segments, Options, p.Electronic.MaxTimeMs, p.Electronic.TimeStepMs);
        }
        catch (Exception ex)
        {
            Planned = null;
            PlannedTimes = new Dictionary<Guid, double>();
            _result.Children.Clear();
            _result.Children.Add(Line(Warn, ex.Message));
            _apply.Content = L("Apply");
            _apply.IsEnabled = Editing && !Reprogram;
            return;
        }
        Planned = result;
        PlannedTimes = times;
        _preview.Line = EbsTimingLine.CreateEntity(_points, segments, Options);
        _preview.Times = times;
        _vm.View.Redraw();

        _result.Children.Clear();
        if (times.Count == 0) _result.Children.Add(Line(Warn, L("No hole gets a time from this line")));
        else
        {
            _result.Children.Add(Line(Ok, Loc.F("{0} holes get times: {1} – {2} ms", times.Count, N(times.Values.Min(), "0"), N(times.Values.Max(), "0"))));
            var timed = _holes.Where(h => times.ContainsKey(h.Id)).ToList();
            var radius = Math.Max(p.Design.Burden, p.Design.Spacing) * 1.6;
            double? closest = null;
            foreach (var h in timed)
                foreach (var o in timed)
                    if (o != h && Vec3.Distance2D(o.Collar, h.Collar) <= radius)
                    {
                        var dt = Math.Abs(times[o.Id] - times[h.Id]);
                        if (closest is null || dt < closest) closest = dt;
                    }
            var window = p.Vibration.WindowMs;
            var sorted = times.Values.OrderBy(t => t).ToList();
            var most = 0;
            for (int i = 0, j = 0; i < sorted.Count; i++)
            {
                while (sorted[i] - sorted[j] >= window) j++;
                most = Math.Max(most, i - j + 1);
            }
            if (closest is double c) _result.Children.Add(Line(c >= window ? Ok : Warn, Loc.F("Shortest interval between neighbours: {0} ms", N(c, "0.#"))));
            _result.Children.Add(most > 1
                ? Line(Warn, Loc.F("Up to {0} holes fire within one {1} ms window", most, N(window, "0")))
                : Line(Ok, Loc.F("No two holes within one {0} ms window", N(window, "0"))));
        }
        if (result.SkippedManual > 0) _result.Children.Add(Line(Look.Muted, Loc.F("{0} holes with a time set by hand keep it", result.SkippedManual)));
        if (result.OutOfReach > 0) _result.Children.Add(Line(Warn, Loc.F("{0} holes are out of reach of the line", result.OutOfReach)));
        if (result.MaxTimeExceeded.Count > 0) _result.Children.Add(Line(Warn, Loc.F("{0} holes would pass the maximum delay {1} ms and stay as they are", result.MaxTimeExceeded.Count, N(p.Electronic.MaxTimeMs, "0"))));
        // Editing without reprogramming changes only the line, so it can always be applied.
        var timesHoles = !Editing || Reprogram;
        _apply.Content = timesHoles && times.Count > 0 ? Loc.F("Apply to {0} holes", times.Count) : L("Apply");
        _apply.IsEnabled = times.Count > 0 || !timesHoles;
    }

    private static Control Line(IBrush colour, string text) => Ui.Row(8,
        new Ellipse { Width = 8, Height = 8, Fill = colour, VerticalAlignment = VerticalAlignment.Center },
        new TextBlock { Text = text, Foreground = Look.TextSoft, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
}
