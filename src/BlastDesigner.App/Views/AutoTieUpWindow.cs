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

namespace BlastDesigner.App.Views;

/// <summary>
/// Automatic tie-up: the goal, the layouts to try, the connectors, the initiation point and the materials on the left;
/// on the right the plan of the chosen variant (holes coloured by firing time, ties by delay) and the table of the
/// variants with what each costs and how it fires, the best marked. Every change plans them again; OK applies the
/// chosen variant.
/// </summary>
public static class AutoTieUpWindow
{
    /// <summary>The variant to apply.</summary>
    public sealed record Result(TieUpPlan Plan);

    private static string L(string english) => Loc.T(english);
    private static string N(double v, string format = "0.#") => v.ToString(format, CultureInfo.CurrentCulture);
    private static readonly IBrush Ok = new SolidColorBrush(Color.FromUInt32(0xFF5BC08A));
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromUInt32(0xFFF0C13A));

    // what was chosen last time, for the next opening in the session
    private static int _goal;
    private static int _perBlock = 1;
    private static bool _pickLead = true;
    private static HashSet<TieScheme> _schemes = new(TieUpPlanner.AllSchemes);
    private static string? _inRowId, _betweenId;

    private static readonly (string Name, double E, double S, double R)[] Goals =
    {
        ("Balanced", 1, 1, 1), ("Economy", 3, 1, 1), ("Seismic", 1, 3, 1), ("Reliability", 1, 1, 3)
    };

    public static string SchemeName(TieScheme s) => L(s switch
    {
        TieScheme.Rows => "Row by row",
        TieScheme.VFront => "V from the front",
        TieScheme.Echelon => "Echelon",
        TieScheme.Diagonal => "Diagonal",
        TieScheme.VDiagonal => "V diagonal",
        TieScheme.Fan => "Fan",
        _ => s.ToString()
    });

    /// <param name="holes">The holes to tie (non-electric ones).</param>
    /// <param name="leftOut">Holes left out for having electronic detonators only.</param>
    public static async Task<Result?> Show(MainViewModel vm, IReadOnlyList<Hole> holes, int leftOut)
    {
        var p = vm.Project!;
        var ips = p.Entities.OfType<InitiationPoint>().ToList();
        var connectors = TieUpPlanner.SurfaceConnectors(p);
        if (ips.Count == 0 || connectors.Count == 0 || holes.Count == 0) return null;

        var spacing = p.Design.Spacing > 0 ? p.Design.Spacing : 5;
        var burden = p.Design.Burden > 0 ? p.Design.Burden : 4;
        var inRow = connectors.FirstOrDefault(c => c.Id == _inRowId) ?? TieUpPlanner.Nearest(connectors, 3.4 * spacing) ?? connectors[0];
        var between = connectors.FirstOrDefault(c => c.Id == _betweenId) ?? TieUpPlanner.Nearest(connectors, 10.5 * burden) ?? connectors[^1];
        var start = ips[0];
        var window = p.Vibration.WindowMs > 0 ? p.Vibration.WindowMs : 8;

        // the charge of each hole for the instantaneous charge, from the calculation when it runs
        var charges = new Dictionary<Guid, double>();
        try
        {
            var s = CalcService.Live(p) ?? CalcService.Session(p);
            foreach (var h in holes) charges[h.Id] = s.Number("hole.chargeMass", h) is var m && double.IsFinite(m) ? m : 0;
        }
        catch { charges.Clear(); }

        List<TieUpPlan> plans = new();
        TieUpPlan? selected = null;
        var preview = new TieUpPreview(holes) { Name = "tiePreview" };
        var table = new Grid { Name = "variants" };
        var notes = new StackPanel { Spacing = 5 };
        var ratio = new TextBlock { FontSize = 12, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap };

        void Recompute()
        {
            var keep = selected?.Scheme;
            var goal = Goals[_goal];
            var o = new TieUpOptions
            {
                Start = start,
                InRow = inRow,
                BetweenRows = between,
                Connectors = connectors,
                Schemes = _schemes.Count > 0 ? _schemes.OrderBy(x => x).ToList() : TieUpPlanner.AllSchemes,
                PickLeadLength = _pickLead,
                HolesPerBlock = _perBlock,
                WindowMs = window,
                Charge = charges.Count > 0 ? h => charges.GetValueOrDefault(h.Id) : null,
                WeightEconomy = goal.E,
                WeightSeismic = goal.S,
                WeightReliability = goal.R
            };
            ratio.Text = Loc.F("{0} ms/m of burden · {1} ms/m of spacing", N(between.DelayMs / burden), N(inRow.DelayMs / spacing));
            plans = TieUpPlanner.Plan(p, holes, o);
            selected = plans.FirstOrDefault(x => x.Scheme == keep && keep != null) ?? plans.FirstOrDefault(x => x.Best) ?? plans.FirstOrDefault();
            FillTable();
            ShowSelected();
        }

        void ShowSelected()
        {
            preview.Start = start;
            preview.Plan = selected;
            preview.InvalidateVisual();
            notes.Children.Clear();
            if (selected == null) return;
            if (selected.Warnings.Count == 0)
                notes.Children.Add(Line(Ok, L("Checks passed: no neighbours within the window, the network is laid out before the first hole fires")));
            if (selected.Risky)
                notes.Children.Add(Line(Look.Danger, L("Some holes may not fire: the signal gets to them after the holes around have fired and may be cut. Take a variant without this mark, shorter connectors, a point nearer the middle or a longer in-hole delay")));
            foreach (var w in selected.Warnings) notes.Children.Add(Line(Warn, Loc.F(w.Format, w.Args)));
            if (selected.ByProduct.Count > 0)
                notes.Children.Add(Line(Look.Muted, string.Join(" · ", selected.ByProduct.OrderBy(k => k.Key.DelayMs).Select(k => $"{k.Key.Name} × {k.Value}"))));
            foreach (Control row in table.Children)
                if (row.Tag is TieUpPlan tp && row is Border b) b.Background = tp == selected ? Look.AccentSoft : Brushes.Transparent;
        }

        void FillTable()
        {
            table.Children.Clear();
            table.RowDefinitions.Clear();
            table.ColumnDefinitions = new ColumnDefinitions("2.1*,*,*,*,*,*,*,*,*,*");
            string[] heads =
            {
                "Layout", "Connectors", "Cost", "Length, m", "Holes in window", charges.Count > 0 ? "Max charge, kg" : "—",
                "Overlaps", "Network, ms", "Cut-off risk", "Score"
            };
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < heads.Length; c++)
            {
                var t = new TextBlock { Text = L(heads[c]), FontSize = 10.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 4, 6), VerticalAlignment = VerticalAlignment.Bottom };
                if (c > 0) t.TextAlignment = TextAlignment.Right;
                Grid.SetColumn(t, c);
                table.Children.Add(t);
            }
            int r = 1;
            foreach (var plan in plans.OrderBy(x => x.Risky).ThenBy(x => x.Score))
            {
                table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var band = new Border { Tag = plan, CornerRadius = new CornerRadius(6), Cursor = new Cursor(StandardCursorType.Hand), Height = 34 };
                Grid.SetRow(band, r);
                Grid.SetColumnSpan(band, heads.Length);
                band.PointerPressed += (_, _) => { selected = plan; ShowSelected(); };
                table.Children.Add(band);
                var name = Ui.Row(6, new TextBlock { Text = SchemeName(plan.Scheme), Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center });
                if (plan.Best)
                    name.Children.Add(new Border
                    {
                        Background = Look.Accent, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1), VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = L("best"), FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Look.OnAccent }
                    });
                if (plan.Risky)
                    name.Children.Add(new Border
                    {
                        Background = Look.Danger, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1), VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = L("misfire risk"), FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Look.Text }
                    });
                string[] values =
                {
                    N(plan.Connectors, "0"), plan.Cost > 0 ? N(plan.Cost, "#,0") : "—", N(plan.LengthM, "0"), N(plan.MaxHolesInWindow, "0"),
                    charges.Count > 0 ? N(plan.MaxCharge, "0") : "—", N(plan.Overlaps, "0"), N(plan.NetworkMs, "0"), N(plan.CutOffRisk, "0"), N(plan.Score, "0.00")
                };
                var cells = new List<Control> { name };
                foreach (var v in values) cells.Add(new TextBlock { Text = v, FontFamily = Look.Mono, FontSize = 12.5, Foreground = Look.TextSoft, TextAlignment = TextAlignment.Right });
                for (int c = 0; c < cells.Count; c++)
                {
                    var cell = cells[c];
                    cell.IsHitTestVisible = false;
                    cell.VerticalAlignment = VerticalAlignment.Center;
                    cell.Margin = new Thickness(8, 0, 4, 0);
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, c);
                    table.Children.Add(cell);
                }
                // a warning shows in the colour of the cell it is about
                if (plan.Overlaps > 0) ((TextBlock)cells[6]).Foreground = Warn;
                if (plan.CutOffRisk > 0) ((TextBlock)cells[8]).Foreground = Look.Danger;
                if (plan.LateHoles > 0) ((TextBlock)cells[7]).Foreground = Look.Danger;
                r++;
            }
        }

        // ---- the left side -----------------------------------------------------------------------------------------
        var goalButtons = new List<Button>();
        void MarkGoals()
        {
            for (int i = 0; i < goalButtons.Count; i++)
            {
                var on = i == _goal;
                goalButtons[i].Background = on ? Look.Accent : Brushes.Transparent;
                goalButtons[i].Foreground = on ? Look.OnAccent : Look.TextSoft;
                goalButtons[i].BorderBrush = on ? Look.Accent : Look.LineStrong;
            }
        }
        var goals = new WrapPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < Goals.Length; i++)
        {
            var at = i;
            var b = new Button { Content = L(Goals[i].Name), Height = 30, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 6), VerticalContentAlignment = VerticalAlignment.Center, Name = "goal" + i };
            b.Click += (_, _) => { _goal = at; MarkGoals(); Recompute(); };
            goalButtons.Add(b);
            goals.Children.Add(b);
        }
        MarkGoals();

        var schemes = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var s in TieUpPlanner.AllSchemes)
        {
            var box = new CheckBox { Content = SchemeName(s), IsChecked = _schemes.Contains(s), MinWidth = 140, Name = "scheme" + s };
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true) _schemes.Add(s); else _schemes.Remove(s);
                Recompute();
            };
            schemes.Children.Add(box);
        }

        ComboBox Connector(string name, Product current, Action<Product> set)
        {
            var combo = new ComboBox
            {
                Name = name, ItemsSource = connectors.Select(c => c.LeadLengthM > 0 ? $"{c.Name} · {N(c.LeadLengthM)} {L("m")}" : c.Name).ToList(),
                SelectedIndex = Math.Max(0, connectors.IndexOf(current)), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32
            };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) { set(connectors[combo.SelectedIndex]); Recompute(); } };
            return combo;
        }
        var inRowBox = Connector("inRow", inRow, c => { inRow = c; _inRowId = c.Id; });
        var betweenBox = Connector("betweenRows", between, c => { between = c; _betweenId = c.Id; });

        var startBox = new ComboBox { Name = "start", ItemsSource = ips.Select(i => i.HoleId != null && i.Hole != null ? $"{i.Label} ({i.Hole})" : i.Label).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32 };
        startBox.SelectionChanged += (_, _) => { if (startBox.SelectedIndex >= 0) { start = ips[startBox.SelectedIndex]; Recompute(); } };

        var blockItems = new List<string> { L("1 — a block for each hole") };
        for (int k = 2; k <= 4; k++) blockItems.Add(Loc.F("{0} holes from one block", k));
        var blockBox = new ComboBox { Name = "holesPerBlock", ItemsSource = blockItems, SelectedIndex = Math.Clamp(_perBlock - 1, 0, 3), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32 };
        blockBox.SelectionChanged += (_, _) => { _perBlock = blockBox.SelectedIndex + 1; Recompute(); };
        var pickLead = new CheckBox { Content = L("Pick the lead length for each tie"), IsChecked = _pickLead, Name = "pickLead" };
        pickLead.IsCheckedChanged += (_, _) => { _pickLead = pickLead.IsChecked == true; Recompute(); };

        var minInhole = holes.SelectMany(h => Charging.Primers(h)).Where(x => !Charging.IsElectronic(p, x.Primer)).Select(x => Charging.PrimerDelay(p, x.Primer)).Where(d => d > 0).DefaultIfEmpty(0).Min();
        var limits = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Hint(Loc.F("Window of the 8 ms rule: {0} ms (Vibration settings)", N(window))),
                Hint(minInhole > 0 ? Loc.F("Shortest in-hole delay: {0} ms; the surface network should be shorter", N(minInhole, "0")) : L("No in-hole delays: holes fire on the surface signal")),
                Hint(L("Cut-off risk: a hole firing within 2 burdens of a tie the signal has not passed yet"))
            }
        };

        var left = new StackPanel
        {
            Spacing = 18,
            Children =
            {
                FormKit.Section(L("Goal"), goals),
                FormKit.Section(L("Layouts to compare"), schemes),
                FormKit.Section(L("Delays"), new StackPanel { Spacing = 10, Children = { FormKit.Labeled(L("Connector between holes in a row"), inRowBox), FormKit.Labeled(L("Connector between rows"), betweenBox), ratio } }),
                FormKit.Section(L("Initiation"), FormKit.Labeled(L("Initiation point"), startBox)),
                FormKit.Section(L("Materials"), new StackPanel { Spacing = 10, Children = { pickLead, FormKit.Labeled(L("Holes on one connector block (fan)"), blockBox), Hint(L("With several holes on a block their order comes from in-hole delays stepped by the block delay")) } }),
                FormKit.Section(L("Limits"), limits)
            }
        };
        var leftPane = new Border
        {
            Width = 340, Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new ScrollViewer { Content = new Border { Padding = new Thickness(16, 14), Child = left } }
        };

        // ---- the right side -----------------------------------------------------------------------------------------
        var previewFrame = new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = preview };
        var tableFrame = new Border
        {
            Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 10, 6, 6),
            Child = new ScrollViewer { Content = table, MaxHeight = 260 }
        };
        var right = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 10, Margin = new Thickness(16, 14) };
        Grid.SetRow(tableFrame, 1);
        Grid.SetRow(notes, 2);
        right.Children.Add(previewFrame);
        right.Children.Add(tableFrame);
        right.Children.Add(notes);

        var body = new DockPanel();
        DockPanel.SetDock(leftPane, Dock.Left);
        body.Children.Add(leftPane);
        body.Children.Add(right);

        Recompute();
        var subtitle = Loc.F("Holes: {0} · non-electric", holes.Count) + (leftOut > 0 ? " · " + Loc.F("left out with electronic detonators only: {0}", leftOut) : "");
        var ok = await Dialogs.Custom("Automatic tie-up", body, validate: () => selected != null, width: 1240, height: 860, subtitle: subtitle, bare: true, okText: L("Apply variant"));
        if (!ok || selected == null) return null;
        // a variant with holes that may not fire is applied only when asked twice
        if (selected.Risky && !await Dialogs.Confirm(Loc.F("In the variant «{0}» {1} holes get the signal after the first hole has fired and {2} ties may be cut: these holes may not fire. Apply it anyway?", SchemeName(selected.Scheme), selected.LateHoles + selected.Unreached, selected.CutOffRisk), L("Automatic tie-up")))
            return null;
        return new Result(selected);
    }

    private static TextBlock Hint(string text) => new() { Text = text, FontSize = 12, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap };

    private static Control Line(IBrush colour, string text) => Ui.Row(8,
        new Ellipse { Width = 8, Height = 8, Fill = colour, VerticalAlignment = VerticalAlignment.Center },
        new TextBlock { Text = text, Foreground = Look.TextSoft, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
}

/// <summary>
/// The plan of a tie-up variant: holes coloured by firing time (blue first, amber, red last), ties coloured by the delay
/// of their connector (lighter for the ties of a shared block), the initiation point as a red triangle, the legend of
/// the delays in the corner.
/// </summary>
internal sealed class TieUpPreview : Control
{
    private readonly IReadOnlyList<Hole> _holes;
    public InitiationPoint? Start { get; set; }
    public TieUpPlan? Plan { get; set; }

    private static readonly uint[] DelayColours = { 0xFF4C9BFF, 0xFF5BC08A, 0xFFF0A63A, 0xFFE5484D, 0xFFB57BFF, 0xFF3FC7C7, 0xFFFF7EB6, 0xFFC9CFD6 };
    private static readonly Typeface Face = new(FontFamily.Default);

    public TieUpPreview(IReadOnlyList<Hole> holes)
    {
        _holes = holes;
        MinHeight = 260;
        ClipToBounds = true;
    }

    private static Color Ramp(double t)
    {
        // blue → amber → red
        static Color Mix(Color a, Color b, double k) => Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
        Color c0 = Color.FromUInt32(0xFF4C9BFF), c1 = Color.FromUInt32(0xFFF0A63A), c2 = Color.FromUInt32(0xFFE5484D);
        t = Math.Clamp(t, 0, 1);
        return t < 0.5 ? Mix(c0, c1, t * 2) : Mix(c1, c2, (t - 0.5) * 2);
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.FillRectangle(Look.Field, new Rect(size));
        if (_holes.Count == 0) return;
        var pts = _holes.Select(h => h.Collar).ToList();
        if (Start != null) pts.Add(Start.Position);
        double minX = pts.Min(v => v.X), maxX = pts.Max(v => v.X), minY = pts.Min(v => v.Y), maxY = pts.Max(v => v.Y);
        const double margin = 28;
        var scale = Math.Min((size.Width - 2 * margin) / Math.Max(1e-6, maxX - minX), (size.Height - 2 * margin - 22) / Math.Max(1e-6, maxY - minY));
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var offX = (size.Width - (maxX - minX) * scale) / 2;
        var offY = (size.Height + 22 - (maxY - minY) * scale) / 2;
        Point P(Vec3 v) => new(offX + (v.X - minX) * scale, size.Height - offY - (v.Y - minY) * scale);
        var pos = _holes.ToDictionary(h => h.Id, h => h.Collar);
        if (Start != null) pos[Start.Id] = Start.Position;

        var plan = Plan;
        var delays = plan?.Ties.Where(t => t.Product != null).Select(t => t.Product!.DelayMs).Distinct().OrderBy(d => d).ToList() ?? new();
        IBrush DelayBrush(double d) => new SolidColorBrush(Color.FromUInt32(DelayColours[Math.Max(0, delays.IndexOf(d)) % DelayColours.Length]));
        if (plan != null)
            foreach (var t in plan.Ties)
            {
                if (!pos.TryGetValue(t.From, out var a) || !pos.TryGetValue(t.To, out var b)) continue;
                var brush = t.Product == null ? Look.Danger : DelayBrush(t.Product.DelayMs);
                var pen = new Pen(brush, t.Block >= 0 ? 1.2 : 1.8) { DashStyle = t.Role == TieRole.Lead ? DashStyle.Dash : null };
                ctx.DrawLine(pen, P(a), P(b));
            }

        double first = 0, last = 1;
        if (plan != null && plan.Firing.Count > 0) { first = plan.Firing.Values.Min(); last = Math.Max(first + 1, plan.Firing.Values.Max()); }
        var r = Math.Clamp(scale * 0.6, 2.5, 6.5);
        foreach (var h in _holes)
        {
            IBrush fill = plan != null && plan.Firing.TryGetValue(h.Id, out var f) ? new SolidColorBrush(Ramp((f - first) / (last - first))) : Look.Faint;
            ctx.DrawEllipse(fill, new Pen(Look.Ground, 1), P(h.Collar), r, r);
        }
        if (Start != null)
        {
            var c = P(Start.Position);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(c.X, c.Y - 9), true);
                g.LineTo(new Point(c.X + 8, c.Y + 6));
                g.LineTo(new Point(c.X - 8, c.Y + 6));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(Look.Danger, new Pen(Look.Text, 1), geo);
        }

        // the legend: delays of the ties, and the time range of the colours
        double x = 10;
        foreach (var d in delays)
        {
            ctx.FillRectangle(DelayBrush(d), new Rect(x, 13, 14, 3));
            var text = new FormattedText(d.ToString("0", CultureInfo.CurrentCulture) + " " + Loc.T("ms"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11.5, Look.TextSoft);
            ctx.DrawText(text, new Point(x + 18, 6));
            x += 18 + text.Width + 14;
        }
        if (plan != null && plan.Firing.Count > 0)
        {
            var span = new FormattedText(Loc.F("Firing {0} – {1} ms", first.ToString("0", CultureInfo.CurrentCulture), last.ToString("0", CultureInfo.CurrentCulture)), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11.5, Look.Muted);
            var w = 90.0;
            var x0 = size.Width - w - span.Width - 22;
            ctx.DrawText(span, new Point(x0, 6));
            for (int i = 0; i < 30; i++) ctx.FillRectangle(new SolidColorBrush(Ramp(i / 29.0)), new Rect(size.Width - w - 10 + i * w / 30, 10, w / 30 + 0.5, 8));
        }
    }
}
