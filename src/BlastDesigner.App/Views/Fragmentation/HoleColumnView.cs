using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.Views;

/// <summary>
/// «Hole and charge»: a drawing of the hole to scale (bench top, floor, bench height and subdrill, the stemming, the column
/// and the bottom charge with their lengths) beside a builder of the loading rule: the stemming (fitted or fixed), the
/// column explosives and the bottom explosives from the product catalogue with the bottom length, the subdrill, and «From
/// rule» to fill it from a rule of the project. One explosive or two. A component of its own, to be put in other windows too.
/// </summary>
public sealed class HoleColumnView : Grid
{
    private readonly BlastProject _p;
    private readonly HoleSchemeDrawing _drawing = new() { Width = 140, MinHeight = 250 };
    private readonly List<Button> _modeChips = new();
    private readonly ComboBox _stemMode = Combo("stemmingMode", "Fit", "Fixed");
    private readonly TextBox _stemLength = Field("stemmingLength");
    private readonly ProductPick _column, _bottom;
    private readonly TextBox _bottomLength = Field("bottomLength");
    private readonly ComboBox _subdrillMode = Combo("subdrillMode", "As in the project", "Own");
    private readonly TextBox _subdrill = Field("subdrill");
    private readonly ComboBox _rule = new() { Name = "fromRule", MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = Loc.T("Choose…") };
    private readonly TextBlock _columnRest = new() { Text = Loc.T("the rest"), Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Control _bottomRow;
    private double _benchHeight, _projectSubdrill, _diameterMm, _fitStemming;
    private FragDesign? _shown;
    private bool _loading;

    /// <summary>The hole has two explosives: a bottom charge under the column.</summary>
    public bool TwoExplosives { get; private set; }

    /// <summary>The explosives of the column and of the bottom charge chosen to try.</summary>
    public IReadOnlyList<string> ColumnIds => _column.Checked;
    public IReadOnlyList<string> BottomIds => _bottom.Checked;
    public BottomLengthMode BottomMode { get; private set; } = BottomLengthMode.Metres;
    public double BottomLength { get; private set; } = 2;
    public double BottomFraction { get; private set; } = 0.3;
    public double BottomFractionMin { get; private set; } = 0.2;
    public double BottomFractionMax { get; private set; } = 0.4;
    public StemmingMode StemmingMode => _stemMode.SelectedIndex == 1 ? StemmingMode.Fixed : StemmingMode.Fit;
    public double StemmingLength { get; private set; }
    /// <summary>The subdrill; null: as the holes have it.</summary>
    public double? Subdrill => _subdrillMode.SelectedIndex == 1 ? _ownSubdrill : null;
    private double _ownSubdrill;

    /// <summary>Anything of the charge changed.</summary>
    public event Action? Changed;

    /// <param name="wet">The holes are wet: the bottom charge only water resistant.</param>
    public HoleColumnView(BlastProject project, bool wet, ChargeScheme? current, double stemming, double benchHeight, double subdrill, double diameterMm)
    {
        _p = project;
        _benchHeight = benchHeight;
        _projectSubdrill = _ownSubdrill = subdrill;
        _diameterMm = diameterMm;
        StemmingLength = stemming;
        _column = new ProductPick("columnProducts", FragTargetSolver.ExplosiveChoices(project, false));
        _bottom = new ProductPick("bottomProducts", FragTargetSolver.ExplosiveChoices(project, wet));
        _column.Changed += Edited;
        _bottom.Changed += Edited;

        ColumnDefinitions = new ColumnDefinitions("Auto,*");
        ColumnSpacing = 14;
        SetColumn(_drawing, 0);
        Children.Add(_drawing);

        var rows = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,72"), ColumnSpacing = 8, RowSpacing = 8, VerticalAlignment = VerticalAlignment.Top };
        int r = 0;
        Control AddRow(string label, IBrush? swatch, Control pick, Control? value, string? name = null)
        {
            rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var head = Ui.Row(8);
            head.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(new Border { Width = 10, Height = 16, CornerRadius = new CornerRadius(2), Background = swatch ?? Brushes.Transparent, BorderBrush = swatch == null ? Brushes.Transparent : Look.LineStrong, BorderThickness = new Thickness(swatch == null ? 0 : 1) });
            head.Children.Add(new TextBlock { Text = Loc.T(label), Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center });
            SetRow(head, r); SetColumn(head, 0); rows.Children.Add(head);
            SetRow(pick, r); SetColumn(pick, 1); rows.Children.Add(pick);
            if (value != null) { SetRow(value, r); SetColumn(value, 2); rows.Children.Add(value); }
            var row = r++;
            if (name != null) head.Name = name;
            return head;
        }
        AddRow("Stemming", HoleSchemeDrawing.StemmingBrush, _stemMode, _stemLength);
        AddRow("Column", HoleSchemeDrawing.ColumnBrush, _column, _columnRest);
        _bottomRow = AddRow("Bottom charge", HoleSchemeDrawing.BottomBrush, _bottom, _bottomLength, "bottomRow");
        AddRow("Subdrill", null, _subdrillMode, _subdrill);
        AddRow("From rule", null, _rule, null);
        // the bottom row's pick and value go with it
        _bottomRowParts = new Control[] { _bottomRow, _bottom, _bottomLength };

        _rule.ItemsSource = project.LoadingRules.OrderBy(x => x.Priority).Select(x => x.Name).ToList();
        _rule.SelectionChanged += (_, _) =>
        {
            if (_loading || _rule.SelectedIndex < 0) return;
            var rule = project.LoadingRules.OrderBy(x => x.Priority).ElementAt(_rule.SelectedIndex);
            LoadRule(rule);
        };
        _stemMode.SelectionChanged += (_, _) => Edited();
        _subdrillMode.SelectionChanged += (_, _) => Edited();
        Commit(_stemLength, v => StemmingLength = Math.Max(0, v));
        Commit(_subdrill, v => _ownSubdrill = Math.Max(0, v));
        _bottomLength.LostFocus += (_, _) => ReadBottom();
        _bottomLength.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) ReadBottom(); };
        SetColumn(rows, 1);
        Children.Add(rows);

        _loading = true;
        _stemMode.SelectedIndex = 0;
        _subdrillMode.SelectedIndex = 0;
        _loading = false;
        if (current != null) Load(current);
        else Refresh();
    }

    private readonly Control[] _bottomRowParts;

    /// <summary>The chips «One explosive | Two explosives» for the title of the card.</summary>
    public Control ModeSwitch()
    {
        var row = Ui.Row(0);
        foreach (var (two, text) in new[] { (false, "One explosive"), (true, "Two explosives") })
        {
            var chip = Ui.Chip(Loc.T(text), () => SetTwo(two), TwoExplosives == two);
            chip.Name = two ? "twoExplosives" : "oneExplosive";
            chip.Height = 30;
            _modeChips.Add(chip);
            row.Children.Add(chip);
        }
        return row;
    }

    private void SetTwo(bool two)
    {
        if (two && _bottom.Checked.Count == 0)
        {
            // a water resistant explosive at the bottom by default: the strongest of the catalogue
            var first = _bottom.Choices.Where(c => c.Usable && !_column.Checked.Contains(c.Product.Id)).OrderByDescending(c => c.Product.Density * c.Product.RelativeWeightStrength).FirstOrDefault();
            if (first != null) _bottom.Set(new[] { first.Product.Id });
        }
        TwoExplosives = two;
        Edited();
    }

    /// <summary>Puts a scheme in the builder.</summary>
    public void Load(ChargeScheme s)
    {
        _loading = true;
        _column.Set(new[] { s.ColumnProductId });
        TwoExplosives = !s.IsSingle;
        if (!s.IsSingle) _bottom.Set(new[] { s.BottomProductId! });
        BottomMode = s.BottomMode;
        BottomLength = s.BottomLength;
        BottomFraction = s.BottomFraction;
        BottomFractionMin = s.BottomFractionMin;
        BottomFractionMax = s.BottomFractionMax;
        _loading = false;
        Edited();
    }

    /// <summary>Fills the builder from a loading rule: its explosives, the bottom length and the stemming.</summary>
    public void LoadRule(LoadingRule rule)
    {
        if (ChargeScheme.FromRule(_p, rule) is { } s)
        {
            _loading = true;
            StemmingLength = rule.StemmingLength;
            _stemMode.SelectedIndex = 1;
            _loading = false;
            Load(s);
        }
    }

    /// <summary>The schemes to try: each column explosive alone, or with each bottom explosive (the same pair is one explosive).</summary>
    public List<ChargeScheme> Schemes()
    {
        var list = new List<ChargeScheme>();
        foreach (var c in _column.Checked)
        {
            if (!TwoExplosives) { list.Add(ChargeScheme.Single(c)); continue; }
            foreach (var b in _bottom.Checked.DefaultIfEmpty(c))
                list.Add(new ChargeScheme
                {
                    ColumnProductId = c, BottomProductId = b == c ? null : b, BottomMode = BottomMode, BottomLength = BottomLength,
                    BottomFraction = BottomFraction, BottomFractionMin = BottomFractionMin, BottomFractionMax = BottomFractionMax
                });
        }
        return list.GroupBy(s => (s.ColumnProductId, s.IsSingle ? null : s.BottomProductId)).Select(g => g.First()).ToList();
    }

    /// <summary>Draws a variant (null: the builder as it is); what the solver chose shows grey in the fields.</summary>
    public void ShowDesign(FragDesign? d)
    {
        _shown = d;
        Refresh();
    }

    /// <summary>The geometry of the area: the bench height, the subdrill and the diameter of its holes.</summary>
    public void SetGeometry(double benchHeight, double subdrill, double diameterMm, double fitStemming)
    {
        _benchHeight = benchHeight;
        _projectSubdrill = subdrill;
        _diameterMm = diameterMm;
        _fitStemming = fitStemming;
        Refresh();
    }

    private void ReadBottom()
    {
        var t = (_bottomLength.Text ?? "").Replace(" ", "").Replace(',', '.');
        double? P(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (t.EndsWith('%'))
        {
            var parts = t.TrimEnd('%').Split('-', '–');
            if (parts.Length == 2 && P(parts[0]) is double a && P(parts[1]) is double b)
            {
                BottomMode = BottomLengthMode.FractionRange;
                BottomFractionMin = Math.Clamp(Math.Min(a, b) / 100, 0, 1);
                BottomFractionMax = Math.Clamp(Math.Max(a, b) / 100, 0, 1);
            }
            else if (P(parts[0]) is double f) { BottomMode = BottomLengthMode.Fraction; BottomFraction = Math.Clamp(f / 100, 0, 1); }
        }
        else if (P(t.TrimEnd('m', 'м')) is double m) { BottomMode = BottomLengthMode.Metres; BottomLength = Math.Max(0, m); }
        Edited();
    }

    private void Edited()
    {
        if (_loading) return;
        _shown = null;
        Refresh();
        Changed?.Invoke();
    }

    private string BottomText() => BottomMode switch
    {
        BottomLengthMode.Metres => Qty.Show(BlastDesigner.Domain.Quantity.Length, BottomLength).ToString("0.0#", CultureInfo.CurrentCulture) + " " + Qty.Sym(BlastDesigner.Domain.Quantity.Length),
        BottomLengthMode.Fraction => (BottomFraction * 100).ToString("0", CultureInfo.CurrentCulture) + " %",
        _ => (BottomFractionMin * 100).ToString("0", CultureInfo.CurrentCulture) + "–" + (BottomFractionMax * 100).ToString("0", CultureInfo.CurrentCulture) + " %"
    };

    private void Refresh()
    {
        for (int i = 0; i < _modeChips.Count; i++) Ui.SetChip(_modeChips[i], (i == 1) == TwoExplosives);
        foreach (var c in _bottomRowParts) c.IsVisible = TwoExplosives;
        var subdrill = _shown?.Subdrill ?? Subdrill ?? _projectSubdrill;
        var stem = _shown?.Stemming ?? (StemmingMode == StemmingMode.Fixed ? StemmingLength : _fitStemming > 0 ? _fitStemming : StemmingLength);
        var holeLength = _benchHeight + subdrill;
        var charge = Math.Max(0, holeLength - stem);
        var bottom = !TwoExplosives ? 0 : _shown != null ? _shown.BottomLength
            : BottomMode == BottomLengthMode.Metres ? Math.Min(charge, BottomLength) : charge * (BottomMode == BottomLengthMode.Fraction ? BottomFraction : (BottomFractionMin + BottomFractionMax) / 2);
        SetText(_stemLength, Qty.Show(BlastDesigner.Domain.Quantity.Length, stem).ToString("0.0#", CultureInfo.CurrentCulture), solver: StemmingMode == StemmingMode.Fit);
        _stemLength.IsReadOnly = StemmingMode == StemmingMode.Fit;
        SetText(_subdrill, Qty.Show(BlastDesigner.Domain.Quantity.Length, subdrill).ToString("0.0#", CultureInfo.CurrentCulture), solver: false);
        _subdrill.IsReadOnly = _subdrillMode.SelectedIndex == 0;
        _subdrill.Foreground = _subdrillMode.SelectedIndex == 0 ? Look.Faint : Look.Text;
        SetText(_bottomLength, _shown != null && TwoExplosives ? Qty.Show(BlastDesigner.Domain.Quantity.Length, bottom).ToString("0.0#", CultureInfo.CurrentCulture) + " " + Qty.Sym(BlastDesigner.Domain.Quantity.Length) : BottomText(), solver: _shown != null && BottomMode == BottomLengthMode.FractionRange);
        _drawing.Set(_benchHeight, subdrill, stem, bottom, TwoExplosives);
    }

    private static void SetText(TextBox box, string text, bool solver)
    {
        box.Text = text;
        box.Foreground = solver ? Look.Faint : Look.Text;
    }

    private static void Commit(TextBox box, Action<double> set)
    {
        void Do()
        {
            var t = (box.Text ?? "").Replace(" ", "").Replace(',', '.');
            if (double.TryParse(new string(t.TakeWhile(ch => char.IsDigit(ch) || ch == '.' || ch == '-').ToArray()), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                set(Qty.Store(BlastDesigner.Domain.Quantity.Length, v));
        }
        box.LostFocus += (_, _) => { if (!box.IsReadOnly) { Do(); Owner(box)?.Edited(); } };
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter && !box.IsReadOnly) { Do(); Owner(box)?.Edited(); } };
    }

    private static HoleColumnView? Owner(Control c)
    {
        for (var x = c.Parent; x != null; x = x.Parent) if (x is HoleColumnView v) return v;
        return null;
    }

    private static ComboBox Combo(string name, params string[] items) => new()
    {
        Name = name, MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = items.Select(Loc.T).ToList()
    };

    private static TextBox Field(string name) => new()
    {
        Name = name, Height = 32, MinHeight = 32, Padding = new Thickness(8, 5), FontFamily = Look.Mono, Background = Look.Field, BorderBrush = Look.LineStrong
    };

    /// <summary>A list of explosives with check boxes in a flyout: «ANFO, +2» on the button. Those that cannot be used are grey with the reason.</summary>
    private sealed class ProductPick : Button
    {
        private readonly List<CheckBox> _boxes = new();
        public IReadOnlyList<ExplosiveChoice> Choices { get; }
        public List<string> Checked { get; } = new();
        public event Action? Changed;
        private bool _setting;

        public ProductPick(string name, IReadOnlyList<ExplosiveChoice> choices)
        {
            Name = name;
            Choices = choices;
            MinHeight = 32;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            HorizontalContentAlignment = HorizontalAlignment.Left;
            Background = Look.Field;
            BorderBrush = Look.LineStrong;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(4);
            Padding = new Thickness(10, 4);
            var list = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
            foreach (var c in choices)
            {
                var text = c.Product.Name + (c.Reason != null ? "  · " + Loc.T(c.Reason) : "");
                var box = new CheckBox { Content = text, IsEnabled = c.Usable, Tag = c.Product.Id, Foreground = c.Usable ? Look.Text : Look.Faint };
                box.IsCheckedChanged += (_, _) =>
                {
                    if (_setting) return;
                    var id = (string)box.Tag!;
                    if (box.IsChecked == true) { if (!Checked.Contains(id)) Checked.Add(id); }
                    else Checked.Remove(id);
                    Show();
                    Changed?.Invoke();
                };
                _boxes.Add(box);
                list.Children.Add(box);
            }
            if (choices.Count == 0) list.Children.Add(new TextBlock { Text = Loc.T("No explosive in the products"), Foreground = Look.Faint });
            Flyout = new Flyout { Content = new ScrollViewer { Content = list, MaxHeight = 320 }, Placement = PlacementMode.BottomEdgeAlignedLeft };
            Show();
        }

        public void Set(IEnumerable<string> ids)
        {
            _setting = true;
            Checked.Clear();
            Checked.AddRange(ids.Where(id => Choices.Any(c => c.Product.Id == id)));
            foreach (var b in _boxes) b.IsChecked = Checked.Contains((string)b.Tag!);
            _setting = false;
            Show();
        }

        private void Show()
        {
            var first = Checked.Select(id => Choices.First(c => c.Product.Id == id).Product.Name).FirstOrDefault();
            Content = new TextBlock
            {
                Text = first == null ? Loc.T("Choose…") : Checked.Count == 1 ? first : Loc.F(Loc.T("{0}, +{1}"), first, Checked.Count - 1),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }
    }
}

/// <summary>The hole to scale: bench top and floor, the stemming hatched, the column, the bottom charge, the subdrill below the floor, with their lengths.</summary>
public sealed class HoleSchemeDrawing : Control
{
    public static readonly IBrush StemmingBrush = new SolidColorBrush(Color.FromUInt32(0xFF6B7280));
    public static readonly IBrush ColumnBrush = new SolidColorBrush(Color.FromUInt32(0xFF5A4630));
    public static readonly IBrush BottomBrush = Look.Accent;
    private double _bench = 10, _subdrill = 1, _stemming = 3, _bottom;
    private bool _two;

    public void Set(double bench, double subdrill, double stemming, double bottom, bool two)
    {
        (_bench, _subdrill, _stemming, _bottom, _two) = (bench, subdrill, stemming, bottom, two);
        InvalidateVisual();
    }

    private static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), size, brush);

    private static string M(double v) => Qty.Show(BlastDesigner.Domain.Quantity.Length, v).ToString("0.#", CultureInfo.CurrentCulture);

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        double top = 16, bottomY = h - 12;
        var total = Math.Max(0.1, _bench + _subdrill);
        double Y(double depth) => top + (bottomY - top) * depth / total;
        double x0 = 52, x1 = 84;
        var line = new Pen(Look.LineStrong, 1);
        var dash = new Pen(Look.Faint, 1, new DashStyle(new double[] { 4, 3 }, 0));
        // bench top and floor
        ctx.DrawLine(new Pen(Look.TextSoft, 1.5), new Point(4, top), new Point(w - 4, top));
        ctx.DrawText(Text(Loc.T("bench top"), 10, Look.Faint), new Point(4, top - 14));
        var floor = Y(_bench);
        ctx.DrawLine(dash, new Point(4, floor), new Point(w - 4, floor));
        ctx.DrawText(Text(Loc.T("floor"), 10, Look.Faint), new Point(x1 + 10, floor - 14));
        // the column of the hole from the collar down
        var stemBottom = Y(Math.Min(_stemming, total));
        var chargeBottom = Y(total);
        var bottomTop = Y(Math.Max(_stemming, total - _bottom));
        var stemRect = new Rect(x0, top, x1 - x0, Math.Max(0, stemBottom - top));
        ctx.DrawRectangle(Look.Field, null, stemRect);
        using (ctx.PushClip(stemRect))
        {
            var hatch = new Pen(StemmingBrush, 1);
            for (double k = -stemRect.Height; k < stemRect.Width + stemRect.Height; k += 6)
                ctx.DrawLine(hatch, new Point(x0 + k, top + stemRect.Height), new Point(x0 + k + stemRect.Height, top));
        }
        ctx.DrawRectangle(ColumnBrush, null, new Rect(x0, stemBottom, x1 - x0, Math.Max(0, (_two ? bottomTop : chargeBottom) - stemBottom)));
        if (_two) ctx.DrawRectangle(BottomBrush, null, new Rect(x0, bottomTop, x1 - x0, Math.Max(0, chargeBottom - bottomTop)));
        ctx.DrawRectangle(null, line, new Rect(x0, top, x1 - x0, chargeBottom - top));
        // lengths on the right of the hole, the bench height and the subdrill on the left
        void Right(double y0, double y1, double v) { if (y1 - y0 > 12) ctx.DrawText(Text(M(v), 11, Look.TextSoft), new Point(x1 + 8, (y0 + y1) / 2 - 7)); }
        Right(top, stemBottom, _stemming);
        Right(stemBottom, _two ? bottomTop : chargeBottom, Math.Max(0, total - _stemming - (_two ? _bottom : 0)));
        if (_two) Right(bottomTop, chargeBottom, _bottom);
        ctx.DrawText(Text(M(_bench), 11, Look.Faint), new Point(14, (top + floor) / 2 - 7));
        ctx.DrawLine(line, new Point(40, top), new Point(40, floor));
        if (_subdrill > 0)
        {
            ctx.DrawText(Text(M(_subdrill), 11, Look.Faint), new Point(14, (floor + chargeBottom) / 2 - 7));
            ctx.DrawLine(line, new Point(40, floor), new Point(40, chargeBottom));
        }
    }
}
