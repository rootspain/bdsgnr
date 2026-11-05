using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.Views;

/// <summary>
/// Edits of a charge column made from the loading window: moving a boundary between decks or a primer, and the deck commands
/// (insert, split, duplicate, move, delete). Decks stay ordered from the collar down; a moved boundary takes the neighbour with it.
/// </summary>
public static class ColumnEdit
{
    /// <summary>Shortest deck a boundary move may leave, m; also the primer's distance from its deck ends.</summary>
    public const double MinLength = 0.1;

    /// <summary>Nearest multiple of <paramref name="step"/> (1 m with the plain mouse, 0.1 m with Shift).</summary>
    public static double Snap(double depth, double step) => Math.Round(Math.Round(depth / step) * step, 3);

    /// <summary>Next grid value above (shallower than) the current depth.</summary>
    public static double StepUp(double depth, double step) => Math.Round((Math.Ceiling(depth / step - 1e-6) - 1) * step, 3);

    /// <summary>Next grid value below (deeper than) the current depth.</summary>
    public static double StepDown(double depth, double step) => Math.Round((Math.Floor(depth / step + 1e-6) + 1) * step, 3);

    /// <summary>Depth kept inside the deck, at least <see cref="MinLength"/> from its top and bottom.</summary>
    public static double ClampPrimer(Deck d, double depth)
    {
        var lo = d.Top + MinLength;
        var hi = d.Bottom - MinLength;
        return Math.Round(lo > hi ? (d.Top + d.Bottom) / 2 : Math.Clamp(depth, lo, hi), 3);
    }

    /// <summary>Moves the boundary between decks <paramref name="i"/> and i+1; both decks keep at least <see cref="MinLength"/>.</summary>
    public static double MoveBoundary(List<Deck> decks, int i, double depth)
    {
        var a = decks[i];
        var b = decks[i + 1];
        var v = Math.Round(Math.Clamp(depth, a.Top + MinLength, b.Bottom - MinLength), 3);
        a.Bottom = v;
        b.Top = v;
        foreach (var p in a.Primers) p.Depth = ClampPrimer(a, p.Depth);
        foreach (var p in b.Primers) p.Depth = ClampPrimer(b, p.Depth);
        return v;
    }

    public static void MovePrimer(Deck d, Primer p, double depth) => p.Depth = ClampPrimer(d, depth);

    /// <summary>Sets the top of deck <paramref name="i"/>: the deck above follows; the first deck stays below the collar.</summary>
    public static void SetTop(List<Deck> decks, int i, double top)
    {
        if (i > 0) { MoveBoundary(decks, i - 1, top); return; }
        var d = decks[i];
        d.Top = Math.Round(Math.Clamp(top, 0, d.Bottom - MinLength), 3);
        foreach (var p in d.Primers) p.Depth = ClampPrimer(d, p.Depth);
    }

    /// <summary>Sets the bottom of deck <paramref name="i"/>: the deck below follows; the last deck stays above the toe.</summary>
    public static void SetBottom(List<Deck> decks, int i, double bottom, double holeLength)
    {
        if (i < decks.Count - 1) { MoveBoundary(decks, i, bottom); return; }
        var d = decks[i];
        d.Bottom = Math.Round(Math.Clamp(bottom, d.Top + MinLength, Math.Max(d.Top + MinLength, holeLength)), 3);
        foreach (var p in d.Primers) p.Depth = ClampPrimer(d, p.Depth);
    }

    /// <summary>
    /// Cuts deck <paramref name="i"/> in half and returns the lower half (inserted after it), or null when the deck is too short.
    /// Split: primers stay in the half they are in. Duplicate: both halves get the original primer layout measured from their bottoms.
    /// Insert: the lower half takes <paramref name="product"/> and has no primers.
    /// </summary>
    public static Deck? Split(List<Deck> decks, int i, SplitMode mode, Guid? product = null)
    {
        var d = decks[i];
        if (d.Length < 2 * MinLength - 1e-9) return null;
        var mid = Math.Clamp(Math.Round((d.Top + d.Bottom) / 2, 1), d.Top + MinLength, d.Bottom - MinLength);
        var lower = new Deck { Top = mid, Bottom = d.Bottom, ProductId = mode == SplitMode.Insert ? product : d.ProductId };
        var original = d.Primers;
        d.Bottom = mid;
        switch (mode)
        {
            case SplitMode.Split:
                lower.Primers = original.Where(p => p.Depth >= mid).ToList();
                d.Primers = original.Where(p => p.Depth < mid).ToList();
                break;
            case SplitMode.Duplicate:
                var shift = lower.Bottom - mid;
                lower.Primers = original.Select(p => { var c = p.Clone(); c.Depth = ClampPrimer(lower, p.Depth); return c; }).ToList();
                d.Primers = original.Select(p => { var c = p.Clone(); c.Depth = ClampPrimer(d, p.Depth - shift); return c; }).ToList();
                break;
            default:
                foreach (var p in d.Primers) p.Depth = ClampPrimer(d, p.Depth);
                break;
        }
        decks.Insert(i + 1, lower);
        return lower;
    }

    /// <summary>Swaps deck <paramref name="i"/> with deck i+1: each keeps its length and primer offsets.</summary>
    public static void SwapWithNext(List<Deck> decks, int i)
    {
        var a = decks[i];
        var b = decks[i + 1];
        var top = a.Top;
        Shift(b, top - b.Top);
        Shift(a, b.Bottom - a.Top);
        decks[i] = b;
        decks[i + 1] = a;
    }

    private static void Shift(Deck d, double delta)
    {
        d.Top = Math.Round(d.Top + delta, 3);
        d.Bottom = Math.Round(d.Bottom + delta, 3);
        foreach (var p in d.Primers) p.Depth = Math.Round(p.Depth + delta, 3);
    }

    /// <summary>Removes deck <paramref name="i"/>; the deck above (or below for the first one) takes its place.</summary>
    public static void Delete(List<Deck> decks, int i)
    {
        var d = decks[i];
        if (decks.Count > 1)
        {
            if (i > 0) decks[i - 1].Bottom = d.Bottom;
            else decks[1].Top = d.Top;
        }
        decks.RemoveAt(i);
    }
}

public enum SplitMode { Split, Duplicate, Insert }

/// <summary>
/// Drawing of the charge column: depth ruler, decks in product colours (stemming hatched), product and length labels and primers.
/// A click selects a deck; boundaries between decks and primers are dragged by height in 1 m steps (0.1 m with Shift, checked
/// on every move) and moved with ↑/↓ the same way.
/// </summary>
public sealed class ChargeColumn : Control
{
    private const double Pad = 10;
    public const double RulerWidth = 38;
    public const double TubeWidth = 56;
    private static readonly IBrush PrimerFill = new SolidColorBrush(Color.FromUInt32(0xFFE5484D));
    private static readonly IBrush PrimerHalo = new SolidColorBrush(Color.FromUInt32(0x73E5484D));
    private static readonly IBrush Air = Look.Hover;
    private static readonly IPen Hatch = new Pen(new SolidColorBrush(Color.FromUInt32(0x2E000000)), 3);
    private static readonly Cursor SizeNs = new(StandardCursorType.SizeNorthSouth);
    private static readonly Typeface Sans = new(FontFamily.Default);
    private static readonly Typeface SansBold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface Mono = new(Look.Mono);

    private readonly BlastProject _project;
    private readonly Hole _hole;
    private readonly List<Deck> _decks;
    private (int Deck, int Primer) _dragPrimer = (-1, -1);
    private int _dragBoundary = -1;
    private int _hoverDeck = -1, _hoverBoundary = -1;
    private (int Deck, int Primer) _hoverPrimer = (-1, -1);

    public ChargeColumn(BlastProject project, Hole hole, List<Deck> decks)
    {
        _project = project;
        _hole = hole;
        _decks = decks;
        Focusable = true;
        ClipToBounds = true;
        MinHeight = 260;
    }

    /// <summary>Selected deck index (drawn with a white outline).</summary>
    public int SelectedDeck { get; set; } = -1;

    /// <summary>Boundary under the keyboard (index of the deck above it), -1 when none.</summary>
    public int ActiveBoundary { get; set; } = -1;

    /// <summary>Primer under the keyboard, (-1, -1) when none.</summary>
    public (int Deck, int Primer) ActivePrimer { get; set; } = (-1, -1);

    public bool IsDragging => _dragBoundary >= 0 || _dragPrimer.Deck >= 0;

    /// <summary>A deck was clicked (or the dragged handle belongs to it).</summary>
    public event Action<int>? DeckPicked;

    /// <summary>The deck under the mouse changed (-1 when none).</summary>
    public event Action<int>? DeckHovered;

    /// <summary>The column was changed by a drag or a key.</summary>
    public event Action? Edited;

    private double Len => Math.Max(0.1, _hole.EffectiveLength);
    private double TubeLeft => RulerWidth;
    private double TubeCenter => RulerWidth + TubeWidth / 2;

    /// <summary>Screen y of a depth in this control.</summary>
    public double Y(double depth) => Pad + depth / Len * Math.Max(1, Bounds.Height - 2 * Pad);

    public double Depth(double y) => (y - Pad) / Math.Max(1, Bounds.Height - 2 * Pad) * Len;

    /// <summary>Point on the tube (control coordinates) for a depth, used by the smoke test to drive the mouse.</summary>
    public Point TubePoint(double depth) => new(TubeCenter, Y(depth));

    private (int Deck, int Primer) HitPrimer(Point pt)
    {
        if (Math.Abs(pt.X - TubeCenter) > 11) return (-1, -1);
        for (int i = 0; i < _decks.Count; i++)
            for (int j = 0; j < _decks[i].Primers.Count; j++)
                if (Math.Abs(Y(_decks[i].Primers[j].Depth) - pt.Y) <= 8) return (i, j);
        return (-1, -1);
    }

    private int HitBoundary(Point pt)
    {
        if (pt.X < TubeLeft - 8 || pt.X > TubeLeft + TubeWidth + 8) return -1;
        for (int i = 0; i < _decks.Count - 1; i++)
            if (Math.Abs(Y(_decks[i].Bottom) - pt.Y) <= 5) return i;
        return -1;
    }

    private int HitDeck(Point pt)
    {
        var depth = Depth(pt.Y);
        return _decks.FindIndex(d => depth >= d.Top && depth < d.Bottom);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        var pt = e.GetPosition(this);
        var pr = HitPrimer(pt);
        var b = pr.Deck < 0 ? HitBoundary(pt) : -1;
        ActivePrimer = pr;
        ActiveBoundary = b;
        if (pr.Deck >= 0) { _dragPrimer = pr; DeckPicked?.Invoke(pr.Deck); }
        else if (b >= 0) { _dragBoundary = b; DeckPicked?.Invoke(b); }
        else if (HitDeck(pt) is var di and >= 0) DeckPicked?.Invoke(di);
        if (IsDragging) e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);
        if (IsDragging)
        {
            // The step is read on every move, so Shift can be pressed or released while dragging.
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 1;
            var depth = ColumnEdit.Snap(Depth(pt.Y), step);
            if (_dragPrimer.Deck >= 0)
            {
                var d = _decks[_dragPrimer.Deck];
                ColumnEdit.MovePrimer(d, d.Primers[_dragPrimer.Primer], depth);
            }
            else ColumnEdit.MoveBoundary(_decks, _dragBoundary, depth);
            Edited?.Invoke();
            InvalidateVisual();
            return;
        }
        _hoverPrimer = HitPrimer(pt);
        _hoverBoundary = _hoverPrimer.Deck < 0 ? HitBoundary(pt) : -1;
        Cursor = _hoverPrimer.Deck >= 0 || _hoverBoundary >= 0 ? SizeNs : Cursor.Default;
        var deck = HitDeck(pt);
        if (deck != _hoverDeck) { _hoverDeck = deck; DeckHovered?.Invoke(deck); }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsDragging) return;
        _dragPrimer = (-1, -1);
        _dragBoundary = -1;
        e.Pointer.Capture(null);
        Edited?.Invoke();
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverPrimer = (-1, -1);
        _hoverBoundary = -1;
        if (_hoverDeck != -1) { _hoverDeck = -1; DeckHovered?.Invoke(-1); }
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Up or Key.Down) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 1;
        double Next(double v) => e.Key == Key.Up ? ColumnEdit.StepUp(v, step) : ColumnEdit.StepDown(v, step);
        var (pd, pp) = ActivePrimer;
        if (pd >= 0 && pd < _decks.Count && pp < _decks[pd].Primers.Count)
        {
            var pr = _decks[pd].Primers[pp];
            ColumnEdit.MovePrimer(_decks[pd], pr, Next(pr.Depth));
        }
        else if (ActiveBoundary >= 0 && ActiveBoundary < _decks.Count - 1)
            ColumnEdit.MoveBoundary(_decks, ActiveBoundary, Next(_decks[ActiveBoundary].Bottom));
        else return;
        e.Handled = true;
        Edited?.Invoke();
        InvalidateVisual();
    }

    private static FormattedText Text(string s, Typeface face, double size, IBrush brush, double maxWidth = double.PositiveInfinity) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush) { MaxTextWidth = maxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var len = Len;

        // Ruler: a tick each metre (5 m on long holes), labels every 2, 5 or 10 m and at the toe.
        var tick = len > 40 ? 5 : 1;
        var label = len <= 16 ? 2 : len <= 40 ? 5 : 10;
        var tickPen = new Pen(Look.LineStrong, 1);
        for (double m = 0; m <= len + 1e-6; m += tick)
        {
            var y = Y(m);
            ctx.DrawLine(tickPen, new Point(RulerWidth - 6, y), new Point(RulerWidth - 2, y));
            if (Math.Abs(m % label) < 1e-6 && len - m > label * 0.4)
                DrawRight(ctx, m.ToString("0.0", CultureInfo.CurrentCulture), RulerWidth - 9, y);
        }
        DrawRight(ctx, len.ToString("0.0", CultureInfo.CurrentCulture), RulerWidth - 9, Y(len));

        var tube = new Rect(TubeLeft, Y(0), TubeWidth, Y(len) - Y(0));
        ctx.DrawRectangle(Air, null, tube, 4, 4);
        if (_hole.WaterDepth > 0)
            ctx.FillRectangle(Look.Blue, new Rect(TubeLeft - 5, Y(len - Math.Min(len, _hole.WaterDepth)), 3, Math.Min(len, _hole.WaterDepth) / len * tube.Height));

        using (ctx.PushClip(new RoundedRect(tube, 4)))
        {
            for (int i = 0; i < _decks.Count; i++)
            {
                var d = _decks[i];
                var prod = _project.FindProduct(d.ProductId);
                var r = new Rect(TubeLeft, Y(d.Top), TubeWidth, Math.Max(1, Y(d.Bottom) - Y(d.Top)));
                ctx.FillRectangle(new SolidColorBrush(Color.FromUInt32(prod?.Color ?? 0xFFCCCCCC)), r);
                if (prod?.Kind == ProductKind.Stemming)
                    using (ctx.PushClip(r))
                        for (var x = r.Left - r.Height; x < r.Right; x += 8)
                            ctx.DrawLine(Hatch, new Point(x, r.Bottom), new Point(x + r.Height, r.Top));
                if (i == _hoverDeck && i != SelectedDeck) ctx.DrawRectangle(null, new Pen(Look.Accent, 1.5), r.Deflate(0.75));
                if (i == SelectedDeck) ctx.DrawRectangle(null, new Pen(Look.Text, 2), r.Deflate(1));
            }
        }
        ctx.DrawRectangle(null, new Pen(Look.LineStrong, 1), tube, 4, 4);

        // Boundary handles show under the mouse, while dragged and when picked for the keyboard.
        for (int i = 0; i < _decks.Count - 1; i++)
        {
            var on = i == _dragBoundary || i == ActiveBoundary;
            if (!on && i != _hoverBoundary) continue;
            var y = _decks[i].Bottom;
            ctx.DrawRectangle(on ? Look.Accent : Look.Text, new Pen(Look.Field, 1), new Rect(TubeCenter - 14, Y(y) - 2.5, 28, 5), 2.5, 2.5);
        }

        for (int i = 0; i < _decks.Count; i++)
            for (int j = 0; j < _decks[i].Primers.Count; j++)
            {
                var c = new Point(TubeCenter, Y(_decks[i].Primers[j].Depth));
                if ((i, j) == _dragPrimer || (i, j) == _hoverPrimer || (i, j) == ActivePrimer) ctx.DrawEllipse(PrimerHalo, null, c, 11, 11);
                ctx.DrawEllipse(Brushes.Black, null, c, 9, 9);
                ctx.DrawEllipse(PrimerFill, new Pen(Brushes.White, 2), c, 7, 7);
            }

        // Product and length to the right of the tube when the deck is tall enough.
        var lx = TubeLeft + TubeWidth + 8;
        foreach (var d in _decks)
        {
            if (Y(d.Bottom) - Y(d.Top) < 28) continue;
            var cy = (Y(d.Top) + Y(d.Bottom)) / 2;
            var name = Text(_project.FindProduct(d.ProductId)?.Name ?? "?", SansBold, 11.5, Look.Text, Math.Max(10, w - lx - 2));
            var size = Text(d.Length.ToString("0.0", CultureInfo.CurrentCulture) + " " + Loc.Ui("m"), Mono, 11, Look.TextSoft);
            ctx.DrawText(name, new Point(lx, cy - name.Height));
            ctx.DrawText(size, new Point(lx, cy));
        }
    }

    private static void DrawRight(DrawingContext ctx, string s, double right, double y)
    {
        var t = Text(s, Mono, 10.5, Look.Faint);
        ctx.DrawText(t, new Point(right - t.Width, y - t.Height / 2));
    }
}

/// <summary>
/// Hole ▸ Loading: the charge column of one hole (or the template for all selected holes). Drawing of the column on the left,
/// the deck table in the middle and the selected deck's properties and primers on the right; totals and checks below.
/// </summary>
public static class HoleLoadingWindow
{
    /// <param name="owner">Window the dialog belongs to; the main window when null (the hole form passes itself).</param>
    public static async Task<List<Deck>?> Show(MainViewModel vm, Hole hole, int holeCount = 1, Window? owner = null)
    {
        var editor = new HoleLoadingEditor(vm.Project!, hole, holeCount);
        var w = new Window
        {
            Title = Loc.F("Loading · hole {0}", hole.Label),
            Width = 1100,
            Height = 780,
            MinWidth = 900,
            MinHeight = 600,
            Background = Look.Ground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = editor
        };
        editor.Closing = ok => { editor.Applied = ok; w.Close(); };
        editor.StoreMinStemming = vm.SetMinStemming;
        editor.StoreRule = rule => vm.Change("Save loading rule", p =>
        {
            var i = p.LoadingRules.FindIndex(r => r.Id == rule.Id);
            if (i >= 0) p.LoadingRules[i] = rule; else p.LoadingRules.Add(rule);
        });
        await Dialogs.ShowModal(w, owner);
        return editor.Applied ? editor.Decks.OrderBy(d => d.Top).ToList() : null;
    }
}

/// <summary>Content of the loading window. Public so the UI smoke test can reach the column and the decks.</summary>
public sealed class HoleLoadingEditor : DockPanel
{
    private static readonly IBrush Ok = new SolidColorBrush(Color.FromUInt32(0xFF5BC08A));
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromUInt32(0xFFF0C13A));
    private static readonly IBrush Red = new SolidColorBrush(Color.FromUInt32(0xFFFF9A9D));
    private static readonly IBrush RedSoft = new SolidColorBrush(Color.FromUInt32(0x1AE5484D));
    private static readonly IBrush RedLine = new SolidColorBrush(Color.FromUInt32(0x73E5484D));
    private static readonly IBrush InfoSoft = new SolidColorBrush(Color.FromUInt32(0x1A6AA8FF));
    private static readonly IBrush InfoIcon = new SolidColorBrush(Color.FromUInt32(0xFF6AA8FF));
    private static readonly IBrush RowHover = Look.Hover;
    private static readonly IBrush PrimerRed = new SolidColorBrush(Color.FromUInt32(0xFFE5484D));

    private readonly BlastProject _p;
    private readonly Hole _hole;
    private readonly int _holeCount;
    private readonly StackPanel _rows = new();
    private readonly StackPanel _checks = new() { Spacing = 6 };
    private readonly StackPanel _props = new() { Spacing = 14, Margin = new Thickness(14) };
    private readonly StackPanel _totals = new() { Orientation = Orientation.Horizontal, Spacing = 22, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _legend = new() { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Dictionary<string, Button> _deckButtons = new();
    private int _hovered = -1;

    public HoleLoadingEditor(BlastProject project, Hole hole, int holeCount)
    {
        _p = project;
        _hole = hole;
        _holeCount = holeCount;
        Decks = hole.Decks.Select(d => d.Clone()).OrderBy(d => d.Top).ToList();
        Column = new ChargeColumn(project, hole, Decks);
        Column.DeckPicked += i => { Selected = i; Refresh(); };
        Column.DeckHovered += i => { _hovered = i; BuildTable(); };
        Column.Edited += Refresh;
        Selected = Decks.Count > 0 ? Decks.FindIndex(d => _p.FindProduct(d.ProductId)?.IsExplosive == true) : -1;
        if (Selected < 0 && Decks.Count > 0) Selected = 0;

        var header = Header();
        SetDock(header, Dock.Top);
        Children.Add(header);
        if (holeCount > 1)
        {
            var scope = Scope();
            SetDock(scope, Dock.Top);
            Children.Add(scope);
        }
        var toolbar = Toolbar();
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);
        var footer = Footer();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*,310") };
        var left = ColumnPane();
        var mid = ListPane();
        var right = new Border { Background = Look.Panel, Child = new ScrollViewer { Content = _props } };
        Grid.SetColumn(mid, 1);
        Grid.SetColumn(right, 2);
        body.Children.Add(left);
        body.Children.Add(mid);
        body.Children.Add(right);
        Children.Add(body);

        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Bubble);
        Refresh();
    }

    /// <summary>The edited column (copies of the hole's decks), top to bottom.</summary>
    public List<Deck> Decks { get; }

    public ChargeColumn Column { get; }

    /// <summary>Selected deck index, -1 when the column is empty.</summary>
    public int Selected { get; private set; }

    public bool Applied { get; set; }

    /// <summary>Checks shown under the table: passed (green tick) or a warning (amber triangle).</summary>
    public List<(bool Ok, string Text)> Checks { get; } = new();

    public Action<bool>? Closing { get; set; }
    /// <summary>Takes the minimum stemming in diameters edited in its check or loaded with a rule (project and program settings).</summary>
    public Action<double>? StoreMinStemming { get; set; }

    private Deck? Sel => Selected >= 0 && Selected < Decks.Count ? Decks[Selected] : null;

    private static string L(string english) => Loc.Ui(english);

    private static string N(double v, string format = "0.0") => v.ToString(format, CultureInfo.CurrentCulture);

    /// <summary>"скважине" after 1, 21, 31…, otherwise "скважинам" (dative).</summary>
    private static bool One(int n) => n % 10 == 1 && n % 100 != 11;

    private Hole Snapshot()
    {
        var tmp = ProjectJson.DeepClone(_hole);
        tmp.Decks = Decks.Select(d => d.Clone()).ToList();
        return tmp;
    }

    // ---------- header, scope, toolbar, footer ----------

    private Control Header()
    {
        static Control Chip(string label, string value) => new Border
        {
            Height = 24,
            Padding = new Thickness(9, 0),
            CornerRadius = new CornerRadius(12),
            Background = Look.Ribbon,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                FontSize = 12,
                Foreground = Look.TextSoft,
                VerticalAlignment = VerticalAlignment.Center,
                Inlines = new InlineCollection { new Run(label + " "), new Run(value) { FontFamily = Look.Mono, Foreground = Look.Text, FontWeight = FontWeight.Medium } }
            }
        };
        var row = Ui.Row(10,
            new TextBlock { Text = Loc.F("Loading · hole {0}", _hole.Label), FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center },
            Ui.Row(6,
                Chip(L("Depth"), N(_hole.EffectiveLength) + " " + L("m")),
                Chip("Ø", N(_hole.Diameter, "0") + " " + L("mm")),
                Chip(L("Water"), N(_hole.WaterDepth) + " " + L("m"))));
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 10), Child = row };
    }

    private Control Scope()
    {
        var stem = Charging.TopStemming(_p, _hole);
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines!.Add(new Run(L("The column will be applied to") + " "));
        text.Inlines.Add(new Run(Loc.F(One(_holeCount) ? "{0} selected hole" : "{0} selected holes", _holeCount)) { FontWeight = FontWeight.SemiBold, Foreground = Look.Text });
        text.Inlines.Add(new Run(". " + Loc.F("If a hole is longer or shorter, the main explosive deck adapts and the stemming stays {0} m.", N(stem))));
        var g = new DockPanel();
        var icon = Look.Icon("info", 16, InfoIcon);
        icon.Margin = new Thickness(0, 1, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(icon, Dock.Left);
        g.Children.Add(icon);
        g.Children.Add(text);
        return new Border { Background = InfoSoft, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 9), Child = g };
    }

    private static Button Btn(string text, string? icon, Action click, bool ghost = false, string? tip = null)
    {
        var content = icon == null ? (Control)new TextBlock { Text = text } : Ui.Row(6, Look.Icon(icon, 16, Look.TextSoft), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Content = content,
            Height = 32,
            Padding = new Thickness(12, 0),
            CornerRadius = new CornerRadius(6),
            BorderBrush = Look.LineStrong,
            BorderThickness = new Thickness(1),
            Background = ghost ? Brushes.Transparent : Look.Ribbon,
            Foreground = Look.Text,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => Ui.Safe(click);
        return b;
    }

    private readonly ComboBox _rules = new() { Width = 230, Height = 32, VerticalAlignment = VerticalAlignment.Center };
    private Button? _applyRule;
    private Button _saveRule = null!;

    /// <summary>Stores a rule in the project's loading rules (set by the window: one undoable change of the project).</summary>
    public Action<LoadingRule>? StoreRule { get; set; }

    /// <summary>Selected rule in the "Rule" list, null when none.</summary>
    public LoadingRule? SelectedRule => _rules.SelectedIndex >= 0 && _rules.SelectedIndex < _p.LoadingRules.Count ? _p.LoadingRules[_rules.SelectedIndex] : null;

    private void FillRules(LoadingRule? select)
    {
        _rules.ItemsSource = _p.LoadingRules.Select(r => r.Name).ToList();
        _rules.SelectedIndex = select == null ? -1 : _p.LoadingRules.IndexOf(select);
        if (_applyRule != null) _applyRule.IsEnabled = _p.LoadingRules.Count > 0;
    }

    /// <summary>Asks for the rule's name and filters, stores it in the loading rules and selects it in the list.</summary>
    public async Task SaveAsRule()
    {
        if (!Decks.Any(d => _p.FindProduct(d.ProductId)?.IsExplosive == true)) return;
        var rule = await SaveRuleWindow.Show(TopLevel.GetTopLevel(this) as Window, _p, _hole, Decks);
        if (rule == null) return;
        // the rule keeps the minimum stemming the column was checked against
        rule.MinStemmingDiameters = Charging.MinStemmingDiameters;
        StoreRule?.Invoke(rule);
        FillRules(_p.LoadingRules.FirstOrDefault(r => r.Id == rule.Id));
    }

    private Control Toolbar()
    {
        _rules.PlaceholderText = L("— not selected —");
        _applyRule = Btn(Loc.T("Load by rule"), "wand", () =>
        {
            if (SelectedRule is not { } rule) return;
            // the rule's minimum stemming replaces the current one
            if (rule.MinStemmingDiameters is double stem) StoreMinStemming?.Invoke(stem);
            var tmp = ProjectJson.DeepClone(_hole);
            Charging.ApplyRule(_p, tmp, rule);
            Decks.Clear();
            Decks.AddRange(tmp.Decks.OrderBy(d => d.Top));
            Selected = Decks.Count > 0 ? 0 : -1;
            Column.ActiveBoundary = -1;
            Column.ActivePrimer = (-1, -1);
            Refresh();
        });
        FillRules(_p.LoadingRules.OrderBy(x => x.Priority).FirstOrDefault(x => x.Matches(_hole)));
        _saveRule = Btn(L("Save as rule"), "save", () => _ = SaveAsRule(), ghost: true, tip: L("Save the current column as a loading rule"));
        _saveRule.Tag = "saveRule";
        var empty = Btn(Loc.T("Empty"), "erase", () =>
        {
            Decks.Clear();
            Selected = -1;
            Column.ActiveBoundary = -1;
            Column.ActivePrimer = (-1, -1);
            Refresh();
        }, ghost: true);
        var sep = new Border { Width = 1, Height = 22, Background = Look.Line, Margin = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center };
        var row = Ui.Row(8, new TextBlock { Text = Loc.T("Rule"), Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center }, _rules, _applyRule, _saveRule, sep, empty);
        return new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 10), Child = row };
    }

    private Control Footer()
    {
        var cancel = Btn(Loc.T("Cancel"), null, () => Closing?.Invoke(false), ghost: true);
        cancel.IsCancel = true;
        var apply = new Button
        {
            Content = _holeCount > 1 ? Loc.F(One(_holeCount) ? "Apply to {0} hole" : "Apply to {0} holes", _holeCount) : L("Apply"),
            Height = 32,
            Padding = new Thickness(14, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { "primary" }
        };
        // Not IsDefault: Enter commits the field being typed in and keeps the window open; Ctrl+Enter applies.
        ToolTip.SetTip(apply, L("Apply the column and close") + " (Ctrl+Enter)");
        apply.Click += (_, _) => Closing?.Invoke(true);
        var buttons = Ui.Row(8, cancel, apply);
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        dock.Children.Add(_totals);
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 12), Child = dock };
    }

    private static TextBlock PaneHead(string text) => new()
    {
        Text = text.ToUpper(CultureInfo.CurrentCulture),
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        LetterSpacing = 0.6,
        Foreground = Look.Faint,
        VerticalAlignment = VerticalAlignment.Center
    };

    // ---------- left: column ----------

    private Control ColumnPane()
    {
        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var head = PaneHead(L("Column"));
        Grid.SetRow(Column, 1);
        Grid.SetRow(_legend, 2);
        g.Children.Add(head);
        g.Children.Add(Column);
        g.Children.Add(new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = _legend, [Grid.RowProperty] = 2 });
        return new Border { Background = Look.Field, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(14, 14, 10, 14), Child = g };
    }

    private void BuildLegend()
    {
        _legend.Children.Clear();
        foreach (var prod in Decks.Select(d => _p.FindProduct(d.ProductId)).OfType<Product>().Distinct())
            _legend.Children.Add(Ui.Row(6, Swatch(prod.Color), new TextBlock { Text = prod.Name, FontSize = 11.5, Foreground = Look.Muted, TextTrimming = TextTrimming.CharacterEllipsis }));
        _legend.Children.Add(Ui.Row(6, Dot(10), new TextBlock { Text = Loc.T("Booster"), FontSize = 11.5, Foreground = Look.Muted }));
    }

    private static Control Swatch(uint color, double size = 10) => new Border
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(2),
        Background = new SolidColorBrush(Color.FromUInt32(color)),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Control Dot(double size) => new Border
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = PrimerRed,
        BorderBrush = Brushes.White,
        BorderThickness = new Thickness(size > 9 ? 2 : 1.5),
        VerticalAlignment = VerticalAlignment.Center
    };

    // ---------- middle: deck table ----------

    private const string TableColumns = "*,62,62,66,74,74";

    private Control ListPane()
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var tools = Ui.Row(2,
            DeckButton("add", "plus", Loc.Ui("Add a deck below") + " (Insert)", AddDeck),
            DeckButton("split", "split", Loc.Ui("Split the selected deck in half"), () => SplitDeck(SplitMode.Split)),
            DeckButton("dup", "dup", Loc.Ui("Duplicate") + " (Ctrl+D)", () => SplitDeck(SplitMode.Duplicate)),
            DeckButton("up", "arrowup", Loc.Ui("Move up") + " (Alt+↑)", () => MoveDeck(-1)),
            DeckButton("down", "arrowdown", Loc.Ui("Move down") + " (Alt+↓)", () => MoveDeck(1)),
            DeckButton("delete", "trash", Loc.Ui("Delete") + " (Delete)", DeleteDeck));
        DockPanel.SetDock(tools, Dock.Right);
        head.Children.Add(tools);
        head.Children.Add(PaneHead(L("Decks from top to bottom")));

        var cols = new Grid { ColumnDefinitions = new ColumnDefinitions(TableColumns) };
        string[] titles = { L("Product"), L("Top, m"), L("Bottom, m"), L("Length, m"), L("Mass, kg"), L("Primers") };
        for (int i = 0; i < titles.Length; i++)
            cols.Children.Add(new TextBlock
            {
                Text = titles[i],
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Look.Faint,
                TextAlignment = i is >= 1 and <= 4 ? TextAlignment.Right : TextAlignment.Left,
                Margin = new Thickness(10, 7),
                [Grid.ColumnProperty] = i
            });
        var tableHead = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = cols };
        var tableDock = new DockPanel();
        DockPanel.SetDock(tableHead, Dock.Top);
        tableDock.Children.Add(tableHead);
        tableDock.Children.Add(new ScrollViewer { Content = _rows });
        var table = new Border
        {
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top,
            Child = tableDock
        };

        var hint = new TextBlock
        {
            Text = L("Drag deck boundaries and primers on the column on the left: 1 m steps, 0.1 m with Shift (↑/↓ keys too). The neighbouring deck moves with the boundary, so there are no gaps or overlaps."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Look.Faint,
            Margin = new Thickness(0, 10)
        };
        table.MaxHeight = 380;
        _checks.VerticalAlignment = VerticalAlignment.Bottom;
        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        Grid.SetRow(table, 1);
        Grid.SetRow(hint, 2);
        Grid.SetRow(_checks, 3);
        g.Children.Add(head);
        g.Children.Add(table);
        g.Children.Add(hint);
        g.Children.Add(_checks);
        return new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(14), Child = g };
    }

    private Button DeckButton(string key, string icon, string tip, Action click)
    {
        var b = Ui.Flat(Look.Icon(icon, 16, Look.TextSoft), tip, () => { click(); }, 30, 30);
        b.Tag = key;
        _deckButtons[key] = b;
        return b;
    }

    private void BuildTable()
    {
        _rows.Children.Clear();
        if (Decks.Count == 0)
        {
            _rows.Children.Add(new TextBlock { Text = L("The column is empty. Load it by a rule or add a deck."), Foreground = Look.Muted, Margin = new Thickness(10, 12), TextWrapping = TextWrapping.Wrap });
            return;
        }
        var hole = Snapshot();
        for (int i = 0; i < Decks.Count; i++)
        {
            var d = Decks[i];
            var idx = i;
            var prod = _p.FindProduct(d.ProductId);
            var expl = prod?.IsExplosive == true;
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(TableColumns), Height = 38 };
            g.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(10, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children = { Swatch(prod?.Color ?? 0xFFCCCCCC), new TextBlock { Text = prod?.Name ?? "?", Foreground = Look.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center } }
            });
            g.Children.Add(NumCell(N(d.Top), 1));
            g.Children.Add(NumCell(N(d.Bottom), 2));
            g.Children.Add(NumCell(N(d.Length), 3));
            g.Children.Add(expl ? NumCell(N(Charging.DeckMass(_p, hole, d)), 4) : NumCell("—", 4, Look.Faint));
            Control primers = !expl
                ? new TextBlock { Text = "—", Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center }
                : d.Primers.Count > 0
                    ? Badge(Ui.Row(4, Dot(8), new TextBlock { Text = d.Primers.Count.ToString(CultureInfo.CurrentCulture), FontSize = 11, Foreground = Look.TextSoft }), Look.Ribbon, Look.Line)
                    : Badge(new TextBlock { Text = L("none"), FontSize = 11, Foreground = Red }, RedSoft, RedLine);
            primers.Margin = new Thickness(10, 0);
            Grid.SetColumn(primers, 5);
            g.Children.Add(primers);
            var sel = i == Selected;
            var row = new Border
            {
                Background = sel ? Look.AccentSoft : Brushes.Transparent,
                BorderBrush = i == _hovered && !sel ? Look.Accent : Look.Line,
                BorderThickness = i == _hovered && !sel ? new Thickness(1.5) : new Thickness(0, 0, 0, i < Decks.Count - 1 ? 1 : 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new Border { BorderBrush = Look.Accent, BorderThickness = new Thickness(sel ? 3 : 0, 0, 0, 0), Child = g }
            };
            row.PointerEntered += (_, _) => { if (!sel) row.Background = RowHover; };
            row.PointerExited += (_, _) => { if (!sel) row.Background = Brushes.Transparent; };
            row.PointerPressed += (_, _) => { Selected = idx; Column.ActiveBoundary = -1; Column.ActivePrimer = (-1, -1); Refresh(); };
            _rows.Children.Add(row);
        }
    }

    private static TextBlock NumCell(string text, int column, IBrush? brush = null) => new()
    {
        Text = text,
        FontFamily = Look.Mono,
        FontSize = 12.5,
        Foreground = brush ?? Look.Text,
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(4, 0, 10, 0),
        [Grid.ColumnProperty] = column
    };

    private static Border Badge(Control content, IBrush bg, IBrush line) => new()
    {
        Height = 20,
        Padding = new Thickness(7, 0),
        CornerRadius = new CornerRadius(10),
        Background = bg,
        BorderBrush = line,
        BorderThickness = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Child = content
    };

    // ---------- deck commands ----------

    private Guid? FirstOf(Func<Product, bool> pick) => _p.Products.FirstOrDefault(pick)?.Id;

    public void AddDeck()
    {
        if (Sel is not { } d)
        {
            if (Decks.Count > 0) return;
            Decks.Add(new Deck { Top = 0, Bottom = Math.Max(0.1, _hole.EffectiveLength), ProductId = FirstOf(x => x.IsExplosive) });
            Selected = 0;
        }
        else
        {
            var expl = _p.FindProduct(d.ProductId)?.IsExplosive == true;
            var product = expl ? FirstOf(x => x.Kind == ProductKind.Stemming) ?? d.ProductId : FirstOf(x => x.IsExplosive) ?? d.ProductId;
            if (ColumnEdit.Split(Decks, Selected, SplitMode.Insert, product) == null) return;
            Selected++;
        }
        Refresh();
    }

    public void SplitDeck(SplitMode mode)
    {
        if (Sel == null || ColumnEdit.Split(Decks, Selected, mode) == null) return;
        Refresh();
    }

    public void MoveDeck(int delta)
    {
        if (Sel == null) return;
        var j = Selected + delta;
        if (j < 0 || j >= Decks.Count) return;
        ColumnEdit.SwapWithNext(Decks, Math.Min(Selected, j));
        Selected = j;
        Column.ActiveBoundary = -1;
        Column.ActivePrimer = (-1, -1);
        Refresh();
    }

    public void DeleteDeck()
    {
        if (Sel == null) return;
        ColumnEdit.Delete(Decks, Selected);
        Selected = Math.Min(Math.Max(0, Selected - 1), Decks.Count - 1);
        Column.ActiveBoundary = -1;
        Column.ActivePrimer = (-1, -1);
        Refresh();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Moving the focus away commits a field still being typed in before the window closes.
            Column.Focus();
            Closing?.Invoke(true);
            e.Handled = true;
            return;
        }
        if (e.Source is TextBox) return;
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Insert: AddDeck(); break;
            case Key.D when ctrl: SplitDeck(SplitMode.Duplicate); break;
            case Key.Up when alt: MoveDeck(-1); break;
            case Key.Down when alt: MoveDeck(1); break;
            case Key.Delete when e.Source is not ComboBox: DeleteDeck(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------- right: properties of the selected deck ----------

    private void BuildProps()
    {
        _props.Children.Clear();
        if (Sel is not { } d)
        {
            _props.Children.Add(new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(8),
                BorderBrush = Look.LineStrong,
                BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = L("The column is empty. Load it by a rule or add a deck."), TextWrapping = TextWrapping.Wrap, Foreground = Look.Muted, TextAlignment = TextAlignment.Center }
            });
            return;
        }
        var prod = _p.FindProduct(d.ProductId);
        var expl = prod?.IsExplosive == true;
        _props.Children.Add(Ui.Row(8, Swatch(prod?.Color ?? 0xFFCCCCCC, 12),
            new TextBlock { Text = Loc.F("Deck {0} of {1}", Selected + 1, Decks.Count), FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Look.Text }));

        var choices = _p.Products.Where(x => x.IsColumnProduct || x.Id == d.ProductId).ToList();
        var combo = new ComboBox { ItemsSource = choices.Select(x => x.Name).ToList(), SelectedIndex = choices.FindIndex(x => x.Id == d.ProductId), HorizontalAlignment = HorizontalAlignment.Stretch, Height = 32 };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0 || choices[combo.SelectedIndex].Id == d.ProductId) return;
            d.ProductId = choices[combo.SelectedIndex].Id;
            if (!choices[combo.SelectedIndex].IsExplosive) d.Primers.Clear();
            Refresh();
        };
        _props.Children.Add(Field(L("Product"), combo));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*"), RowDefinitions = new RowDefinitions("Auto,10,Auto") };
        void Put(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); grid.Children.Add(c); }
        var len = _hole.EffectiveLength;
        Put(Field(L("Top"), NumberBox("top", d.Top, v => ColumnEdit.SetTop(Decks, Selected, v))), 0, 0);
        Put(Field(L("Bottom"), NumberBox("bottom", d.Bottom, v => ColumnEdit.SetBottom(Decks, Selected, v, len))), 0, 2);
        Put(Field(L("Length"), NumberBox("length", d.Length, v => ColumnEdit.SetBottom(Decks, Selected, d.Top + Math.Max(ColumnEdit.MinLength, v), len))), 2, 0);
        Put(Field(L("Density"), ReadOnly(prod != null && prod.Density > 0 ? N(prod.Density, "0.00") : "—", L("g/cm³"))), 2, 2);
        _props.Children.Add(grid);

        var hole = Snapshot();
        var mass = Charging.DeckMass(_p, hole, d);
        var calc = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        void Line(string label, string value)
        {
            var row = calc.RowDefinitions.Count;
            calc.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            calc.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = Look.Muted, Margin = new Thickness(0, 2, 12, 2), [Grid.RowProperty] = row });
            calc.Children.Add(new TextBlock { Text = value, FontSize = 12.5, FontFamily = Look.Mono, Foreground = Look.Text, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 2), [Grid.RowProperty] = row, [Grid.ColumnProperty] = 1 });
        }
        if (expl)
        {
            var perMetre = prod!.Kind == ProductKind.PackagedExplosive && prod.UnitLength > 0
                ? prod.UnitMass / prod.UnitLength
                : Charging.DeckMass(_p, hole, new Deck { Top = 0, Bottom = 1, ProductId = d.ProductId });
            var column = Decks.Where(x => _p.FindProduct(x.ProductId)?.IsExplosive == true).Sum(x => Charging.DeckMass(_p, hole, x));
            Line(L("Linear charge"), N(perMetre) + " " + L("kg/m"));
            Line(L("Deck mass"), N(mass) + " " + L("kg"));
            Line(L("Share of the explosive column"), N(column > 0 ? mass / column * 100 : 0, "0") + " %");
        }
        else
        {
            Line(L("Material volume"), N(_hole.VolumeBetween(d.Top, d.Bottom) * 1000, "0") + " " + L("l"));
            if (mass > 0) Line(L("Deck mass"), N(mass) + " " + L("kg"));
            if (prod != null) Line(L("Role"), Loc.Enum(prod.Kind));
        }
        _props.Children.Add(new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(8), Background = Look.Field, BorderBrush = Look.Line, BorderThickness = new Thickness(1), Child = calc });

        var add = Ui.Flat(Look.Icon("plus", 16, Look.TextSoft), L("Add a primer"), AddPrimer, 30, 30);
        add.Tag = "addPrimer";
        add.IsEnabled = expl;
        var primerHead = new DockPanel();
        DockPanel.SetDock(add, Dock.Right);
        primerHead.Children.Add(add);
        primerHead.Children.Add(PaneHead(L("Primers in the deck")));
        _props.Children.Add(primerHead);
        if (!expl) { _props.Children.Add(EmptyNote(L("Primers go only into explosive decks."))); return; }
        if (d.Primers.Count == 0) { _props.Children.Add(EmptyNote(L("This deck has no primer: the charge will not fire."))); return; }
        var list = new StackPanel { Spacing = 6, Margin = new Thickness(0, -8, 0, 0) };
        for (int j = 0; j < d.Primers.Count; j++) list.Children.Add(PrimerCard(d, j));
        _props.Children.Add(list);
    }

    private static Control Field(string label, Control c) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontSize = 11.5, Foreground = Look.Muted }, c }
    };

    private static Control EmptyNote(string text) => new Border
    {
        Margin = new Thickness(0, -8, 0, 0),
        Padding = new Thickness(12),
        CornerRadius = new CornerRadius(8),
        BorderBrush = Look.LineStrong,
        BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Look.Muted, TextAlignment = TextAlignment.Center }
    };

    private static Control ReadOnly(string value, string unit) => new Border
    {
        Height = 32,
        Padding = new Thickness(8, 0),
        CornerRadius = new CornerRadius(6),
        BorderBrush = Look.LineStrong,
        BorderThickness = new Thickness(1),
        Child = new DockPanel
        {
            Children =
            {
                new TextBlock { Text = unit, FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right },
                new TextBlock { Text = value, FontFamily = Look.Mono, Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center }
            }
        }
    };

    private static double? Parse(string? s) =>
        double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private string? _refocus;

    /// <summary>Metre field: commits on Enter or when it loses focus; the linked fields and the neighbour deck follow.</summary>
    private TextBox NumberBox(string name, double value, Action<double> commit, string unit = "m")
    {
        var box = new TextBox
        {
            Name = name,
            Text = N(value, "0.0##"),
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(8, 5),
            FontFamily = Look.Mono,
            Background = Look.Field,
            BorderBrush = Look.LineStrong,
            InnerRightContent = new TextBlock { Text = L(unit), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }
        };
        var text = box.Text;
        void Commit(bool keepFocus)
        {
            if (box.Text == text) { if (keepFocus) box.SelectAll(); return; }
            text = box.Text;
            if (Parse(box.Text) is double v) commit(v);
            if (keepFocus) _refocus = name;
            Refresh();
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { Commit(true); e.Handled = true; } };
        box.LostFocus += (_, _) => Commit(false);
        if (_refocus == name)
        {
            _refocus = null;
            box.AttachedToVisualTree += (_, _) => { box.Focus(); box.SelectAll(); };
        }
        return box;
    }

    private void AddPrimer()
    {
        if (Sel is not { } d || _p.FindProduct(d.ProductId)?.IsExplosive != true) return;
        d.Primers.Add(new Primer
        {
            Depth = ColumnEdit.ClampPrimer(d, Math.Round(d.Bottom - 0.5, 1)),
            BoosterId = FirstOf(x => x.Kind == ProductKind.Booster),
            DetonatorId = FirstOf(x => x.IsDetonator)
        });
        Column.ActivePrimer = (Selected, d.Primers.Count - 1);
        Column.ActiveBoundary = -1;
        Refresh();
    }

    private Control PrimerCard(Deck d, int j)
    {
        var pr = d.Primers[j];
        var active = Column.ActivePrimer == (Selected, j);
        var booster = _p.FindProduct(pr.BoosterId);
        var det = _p.FindProduct(pr.DetonatorId);
        var info = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = booster?.Name ?? det?.Name ?? Loc.T("Booster"), Foreground = Look.Text, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock
                {
                    FontSize = 11.5,
                    Foreground = Look.Muted,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Inlines = new InlineCollection { new Run(Loc.F("at {0} m", N(pr.Depth))) { FontFamily = Look.Mono }, new Run(det != null && booster != null ? " · " + det.Name : "") }
                }
            }
        };
        var del = Ui.Flat(Look.Icon("trash", 15, Look.TextSoft), L("Delete the primer"), () =>
        {
            d.Primers.RemoveAt(j);
            Column.ActivePrimer = (-1, -1);
            Refresh();
        }, 28, 28);
        var top = new DockPanel();
        var dot = Dot(10);
        dot.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(dot, Dock.Left);
        DockPanel.SetDock(del, Dock.Right);
        top.Children.Add(dot);
        top.Children.Add(del);
        top.Children.Add(info);
        var body = new StackPanel { Spacing = 8, Children = { top } };
        // Every primer card is always open: depth, delay, booster and detonator are edited in place.
        {
            var boosters = _p.Products.Where(x => x.Kind == ProductKind.Booster).ToList();
            var dets = _p.Products.Where(x => x.IsDetonator).ToList();
            ComboBox Pick(List<Product> items, Guid? id, Action<Guid?> set)
            {
                var c = new ComboBox { ItemsSource = items.Select(x => x.Name).Prepend("—").ToList(), SelectedIndex = items.FindIndex(x => x.Id == id) + 1, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 30 };
                c.SelectionChanged += (_, _) =>
                {
                    var v = c.SelectedIndex > 0 ? items[c.SelectedIndex - 1].Id : (Guid?)null;
                    if (v == id) return;
                    set(v);
                    Refresh();
                };
                return c;
            }
            var delay = new TextBox { Text = pr.DelayMs is double ms ? N(ms, "0.##") : "", Watermark = det != null ? N(det.DelayMs, "0") : "", Height = 30, MinHeight = 30, FontFamily = Look.Mono, Background = Look.Field, BorderBrush = Look.LineStrong };
            void CommitDelay(bool keepFocus)
            {
                var v = Parse(delay.Text);
                if (v == pr.DelayMs) { if (keepFocus) delay.SelectAll(); return; }
                pr.DelayMs = v;
                if (keepFocus) _refocus = "primerDelay" + j;
                Refresh();
            }
            if (_refocus == "primerDelay" + j)
            {
                _refocus = null;
                delay.AttachedToVisualTree += (_, _) => { delay.Focus(); delay.SelectAll(); };
            }
            delay.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { CommitDelay(true); e.Handled = true; } };
            delay.LostFocus += (_, _) => CommitDelay(false);
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*"), RowDefinitions = new RowDefinitions("Auto,8,Auto,8,Auto") };
            void Put(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); g.Children.Add(c); }
            Put(Field(L("Depth"), NumberBox("primerDepth" + j, pr.Depth, v => ColumnEdit.MovePrimer(d, pr, v))), 0, 0);
            Put(Field(L("Delay, ms"), delay), 0, 2);
            // Detonator above booster, each across the whole card.
            var detField = Field(L("Detonator"), Pick(dets, pr.DetonatorId, v => pr.DetonatorId = v));
            var boosterField = Field(Loc.T("Booster"), Pick(boosters, pr.BoosterId, v => pr.BoosterId = v));
            Put(detField, 2, 0);
            Put(boosterField, 4, 0);
            Grid.SetColumnSpan(detField, 3);
            Grid.SetColumnSpan(boosterField, 3);
            body.Children.Add(g);
        }
        var card = new Border
        {
            Padding = new Thickness(10, 8, 6, 8),
            CornerRadius = new CornerRadius(8),
            Background = Look.Ribbon,
            BorderBrush = active ? Look.Accent : Look.Line,
            BorderThickness = new Thickness(1),
            Child = body
        };
        top.PointerPressed += (_, _) =>
        {
            Column.ActivePrimer = (Selected, j);
            Column.ActiveBoundary = -1;
            Refresh();
        };
        return card;
    }

    // ---------- totals and checks ----------

    private void BuildTotals()
    {
        var hole = Snapshot();
        var expl = Decks.Where(d => _p.FindProduct(d.ProductId)?.IsExplosive == true).ToList();
        var stem = Charging.TopStemming(_p, hole);
        _totals.Children.Clear();
        void Total(string label, string value) => _totals.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = label.ToUpper(CultureInfo.CurrentCulture), FontSize = 11, LetterSpacing = 0.5, Foreground = Look.Faint },
                new TextBlock { Text = value, FontFamily = Look.Mono, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Look.Text }
            }
        });
        Total(L("Explosive in hole"), N(Charging.ExplosiveMass(_p, hole)) + " " + L("kg"));
        Total(L("Explosive column"), N(expl.Sum(d => d.Length)) + " " + L("m"));
        Total(L("Top stemming"), N(Decks.Count > 0 ? stem : 0) + " " + L("m"));
        Total(L("Primers"), Decks.Sum(d => d.Primers.Count).ToString(CultureInfo.CurrentCulture));

        _checks.Children.Clear();
        Checks.Clear();
        TextBlock CheckText(string text) => new() { Text = text, FontSize = 12.5, Foreground = Look.TextSoft, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        void Check(bool ok, string text, params Control[] content)
        {
            Checks.Add((ok, text));
            var row = Ui.Row(8, Look.Icon(ok ? "okc" : "warn", 15, ok ? Ok : Warn));
            if (content.Length == 0) row.Children.Add(CheckText(text));
            else foreach (var c in content) row.Children.Add(c);
            _checks.Children.Add(row);
        }
        var noPrimer = expl.Count(d => d.Primers.Count == 0);
        Check(noPrimer == 0 && expl.Count > 0, expl.Count == 0 ? L("No explosive in the hole, nowhere to put a primer")
            : noPrimer == 0 ? L("Every explosive deck has a primer") : Loc.F("Explosive decks without a primer: {0}", noPrimer));
        // Same rules as Charging.HasLoadingWarning, which marks holes in the viewport.
        var len = hole.EffectiveLength;
        var gap = Charging.ColumnGap(hole);
        Check(gap < 0.05, gap < 0.05 ? Loc.F("The column covers the whole depth {0} m", N(len))
            : Loc.F("The column does not cover {0} m of {1} m", N(gap), N(len)));
        var minStem = Charging.MinStemming(hole);
        var stemLen = Decks.Count > 0 ? stem : 0;
        var diameters = N(Charging.MinStemmingDiameters, "0.#");
        var stemOk = stemLen >= minStem - 1e-6;
        // the minimum to the centimetre: rounded to 0.1 m a stemming equal to it could read as longer than the minimum shown
        var stemFormat = stemOk ? "Stemming {0} m, at least {1} diameters" : "Stemming {0} m, less than {1} diameters ({2} m)";
        // the number of diameters is a field inside the sentence: the text around it comes from the same translation
        var parts = Loc.T(stemFormat).Split("{1}");
        object[] args = { N(stemLen), diameters, N(minStem, "0.0#") };
        Check(stemOk, string.Format(Loc.T(stemFormat), args),
            CheckText(string.Format(parts[0], args).TrimEnd()), MinStemmingBox(), CheckText(parts.Length > 1 ? string.Format(parts[1], args).TrimStart() : ""));
        foreach (var issue in Charging.Validate(_p, hole).Where(s => !s.Contains("no primer", StringComparison.Ordinal)))
            Check(false, issue);
    }

    /// <summary>Minimum stemming in hole diameters, edited in its check; a program setting, not part of the hole.</summary>
    private TextBox MinStemmingBox()
    {
        const string name = "minStemming";
        var box = new TextBox
        {
            Name = name,
            Text = N(Charging.MinStemmingDiameters, "0.#"),
            Width = 48,
            Height = 24,
            MinHeight = 24,
            Padding = new Thickness(6, 2),
            FontFamily = Look.Mono,
            FontSize = 12.5,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Look.Field,
            BorderBrush = Look.LineStrong
        };
        ToolTip.SetTip(box, L("Minimum top stemming in hole diameters; kept in the project and saved with a loading rule"));
        var text = box.Text;
        void Commit(bool keepFocus)
        {
            if (box.Text == text) { if (keepFocus) box.SelectAll(); return; }
            text = box.Text;
            if (Parse(box.Text) is double v) StoreMinStemming?.Invoke(Math.Clamp(v, 0, 100));
            if (keepFocus) _refocus = name;
            Refresh();
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { Commit(true); e.Handled = true; } };
        box.LostFocus += (_, _) => Commit(false);
        if (_refocus == name)
        {
            _refocus = null;
            box.AttachedToVisualTree += (_, _) => { box.Focus(); box.SelectAll(); };
        }
        return box;
    }

    public void Refresh()
    {
        if (Selected >= Decks.Count) Selected = Decks.Count - 1;
        Column.SelectedDeck = Selected;
        Column.InvalidateVisual();
        BuildTable();
        BuildProps();
        BuildTotals();
        BuildLegend();
        var n = Decks.Count;
        _deckButtons["split"].IsEnabled = _deckButtons["dup"].IsEnabled = Sel?.Length >= 2 * ColumnEdit.MinLength;
        _deckButtons["add"].IsEnabled = n == 0 || Sel?.Length >= 2 * ColumnEdit.MinLength;
        _deckButtons["up"].IsEnabled = Selected > 0;
        _deckButtons["down"].IsEnabled = Selected >= 0 && Selected < n - 1;
        _deckButtons["delete"].IsEnabled = Sel != null;
        _saveRule.IsEnabled = Decks.Any(d => _p.FindProduct(d.ProductId)?.IsExplosive == true);
    }
}
