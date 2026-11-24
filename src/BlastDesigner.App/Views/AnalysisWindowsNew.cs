using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using SkiaSharp;
using static BlastDesigner.App.Views.AnalysisWindows;

namespace BlastDesigner.App.Views;

/// <summary>Simple chart drawing for the analysis windows: axes, line series, bars and scatter points.</summary>
public static class Chart
{
    /// <summary>Chart ground of the dark interface (same as the plan background).</summary>
    public static readonly SKColor Bg = new(0xFF101215);

    /// <summary>Lightens series colours that would be too dark on the chart ground.</summary>
    private static SKColor Visible(SKColor c)
    {
        var lum = (0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue) / 255;
        if (lum >= 0.45) return c;
        const double t = 0.45;
        return new SKColor((byte)(c.Red + (230 - c.Red) * t), (byte)(c.Green + (233 - c.Green) * t), (byte)(c.Blue + (237 - c.Blue) * t), c.Alpha);
    }

    public sealed record Series(string Name, IReadOnlyList<(double X, double Y)> Points, SKColor Color, bool Bars = false, bool Scatter = false, bool RightAxis = false);

    public static void Draw(SKCanvas c, float w, float h, IReadOnlyList<Series> series, string xTitle, string yTitle, bool logX = false, bool logY = false, string? y2Title = null)
    {
        using var bg = new SKPaint { Color = Chart.Bg };
        c.DrawRect(0, 0, w, h, bg);
        var plot = new SKRect(60, 14, w - (y2Title != null ? 60 : 16), h - 40);
        if (plot.Width < 20 || plot.Height < 20) return;
        var left = series.Where(s => !s.RightAxis).SelectMany(s => s.Points).ToList();
        var right = series.Where(s => s.RightAxis).SelectMany(s => s.Points).ToList();
        var all = left.Concat(right).ToList();
        if (all.Count == 0) return;
        double Tx(double v) => logX ? Math.Log10(Math.Max(1e-9, v)) : v;
        double Ty(double v) => logY ? Math.Log10(Math.Max(1e-9, v)) : v;
        var x0 = all.Min(p => Tx(p.X)); var x1 = all.Max(p => Tx(p.X));
        if (x1 - x0 < 1e-9) { x0 -= 1; x1 += 1; }
        (double, double) Range(List<(double X, double Y)> pts)
        {
            if (pts.Count == 0) return (0, 1);
            var a = logY ? pts.Min(p => Ty(p.Y)) : Math.Min(0, pts.Min(p => p.Y));
            var b = pts.Max(p => Ty(p.Y));
            return b - a < 1e-9 ? (a - 1, b + 1) : (a, b + (b - a) * 0.05);
        }
        var (y0, y1) = Range(left);
        var (r0, r1) = Range(right);
        float X(double v) => plot.Left + (float)((Tx(v) - x0) / (x1 - x0)) * plot.Width;
        float Y(double v, bool r) => plot.Bottom - (float)((Ty(v) - (r ? r0 : y0)) / ((r ? r1 : y1) - (r ? r0 : y0))) * plot.Height;

        using var axis = new SKPaint { Color = SKColors.Gray, StrokeWidth = 1, IsAntialias = true };
        using var gridPen = new SKPaint { Color = new SKColor(0xFF242930), StrokeWidth = 1 };
        for (int i = 0; i <= 5; i++)
        {
            var fy = plot.Bottom - i * plot.Height / 5;
            c.DrawLine(plot.Left, fy, plot.Right, fy, gridPen);
            var vy = y0 + i * (y1 - y0) / 5;
            Label(c, Fmt(logY ? Math.Pow(10, vy) : vy), plot.Left - 4, fy + 4, SKTextAlign.Right);
            if (y2Title != null) Label(c, Fmt(r0 + i * (r1 - r0) / 5), plot.Right + 4, fy + 4, SKTextAlign.Left);
            var fx = plot.Left + i * plot.Width / 5;
            c.DrawLine(fx, plot.Top, fx, plot.Bottom, gridPen);
            var vx = x0 + i * (x1 - x0) / 5;
            Label(c, Fmt(logX ? Math.Pow(10, vx) : vx), fx, plot.Bottom + 14, SKTextAlign.Center);
        }
        c.DrawRect(plot, new SKPaint { Color = new SKColor(0xFF3A414A), Style = SKPaintStyle.Stroke });
        Label(c, Loc.T(xTitle), plot.MidX, h - 8, SKTextAlign.Center);
        c.Save(); c.RotateDegrees(-90); Label(c, Loc.T(yTitle), -plot.MidY, 14, SKTextAlign.Center); c.Restore();
        if (y2Title != null) { c.Save(); c.RotateDegrees(90); Label(c, Loc.T(y2Title), plot.MidY, -w + 12, SKTextAlign.Center); c.Restore(); }

        float legendY = plot.Top + 14;
        foreach (var s in series)
        {
            using var pen = new SKPaint { Color = Visible(s.Color), StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var fill = new SKPaint { Color = Visible(s.Color).WithAlpha(200), IsAntialias = true };
            if (s.Bars)
            {
                var bw = s.Points.Count > 1 ? Math.Max(1, (X(s.Points[1].X) - X(s.Points[0].X)) * 0.8f) : 10;
                foreach (var (px, py) in s.Points) c.DrawRect(X(px) - bw / 2, Y(py, s.RightAxis), bw, Y(logY ? 1 : 0, s.RightAxis) - Y(py, s.RightAxis), fill);
            }
            else if (s.Scatter)
                foreach (var (px, py) in s.Points) c.DrawCircle(X(px), Y(py, s.RightAxis), 3.5f, fill);
            else if (s.Points.Count > 1)
            {
                using var path = new SKPath();
                path.MoveTo(X(s.Points[0].X), Y(s.Points[0].Y, s.RightAxis));
                foreach (var (px, py) in s.Points.Skip(1)) path.LineTo(X(px), Y(py, s.RightAxis));
                c.DrawPath(path, pen);
            }
            c.DrawRect(plot.Left + 8, legendY - 8, 10, 10, fill);
            Label(c, Loc.T(s.Name), plot.Left + 22, legendY + 1, SKTextAlign.Left);
            legendY += 15;
        }
    }

    private static void Label(SKCanvas c, string s, float x, float y, SKTextAlign align)
    {
        using var paint = new SKPaint { Color = new SKColor(0xFFA3ABB6), IsAntialias = true, TextSize = 11, TextAlign = align };
        c.DrawText(s, x, y, paint);
    }

    private static string Fmt(double v) => Math.Abs(v) >= 1000 ? v.ToString("0", CultureInfo.CurrentCulture) : Math.Abs(v) >= 10 ? v.ToString("0.#", CultureInfo.CurrentCulture) : v.ToString("0.##", CultureInfo.CurrentCulture);
}

/// <summary>Windows for the analysis and prediction features.</summary>
public static class AnalisysWindows
{
    private static Control Stack(Control top, Control fill, Control? bottom = null)
    {
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        if (bottom != null) { DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom); }
        dock.Children.Add(fill);
        return dock;
    }

    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };

    private static NumericUpDown Number(double value, double min, double max, double step = 1) =>
        new() { Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, Width = 130, FormatString = "0.##" };

    private static StackPanel Inputs(params (string Label, Control Control)[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (l, c) in items) { sp.Children.Add(new TextBlock { Text = Loc.T(l), VerticalAlignment = VerticalAlignment.Center }); sp.Children.Add(c); }
        return sp;
    }

    private static void Highlight(MainViewModel vm, IEnumerable<Guid> ids) => vm.SetHighlight(ids);

    /// <summary>The header of a column of tonnes: short tons in the imperial system.</summary>
    private static string Tons => BlastDesigner.Domain.Measure.System == BlastDesigner.Domain.UnitSystem.Metric ? "Tonnes" : "Short tons";

    private static string F(double v, string fmt = "0.00") => double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString(fmt, CultureInfo.CurrentCulture);

    // ---- dashboard ----------------------------------------------------------------------

    private static TextBlock Name(Analysis.Kpi k)
    {
        var t = new TextBlock { Text = (k.Warning ? "⚠ " : "") + Loc.T(k.Name), Margin = new Thickness(0, 0, 12, 0) };
        if (k.Warning) t.Foreground = Brushes.OrangeRed;
        DockPanel.SetDock(t, Dock.Left);
        return t;
    }

    /// <summary>Why a warning is raised and where to look further; null for a value in order.</summary>
    internal static string? Tip(Analysis.Kpi k) =>
        !k.Warning || k.Tip == null ? null : Loc.F(k.Tip, (k.TipArgs ?? Array.Empty<object?>()).Select(a => a is string s ? Units(s) : a).ToArray());

    /// <summary>Translates the unit words in values formatted by the engine.</summary>
    private static string Units(string value)
    {
        // the numbers with a metric unit in the units of the program first
        value = Qty.InText(value);
        if (Loc.Language == "en") return value;
        foreach (var (en, tr) in new[] { (" dummy", " dummy"), (" mm/s at ", " mm/s at "), ("MJ/t", "MJ/t"), ("kg/m³", "kg/m³"), (" ms", " ms"), (" kg", " kg"), (" cm", " cm"), (" mm", " mm"), (" dB", " dB"), (" m³", " m³"), (" m", " m"), (" t", " t") })
            value = value.Replace(en, Loc.T(tr));
        return value;
    }

    public static void Dashboard(MainViewModel vm)
    {
        var host = new ContentControl();
        void Refresh()
        {
            // an undo replaces the project, and these windows stay open while another one opens, with none in between
            if (vm.Project is not { } p) return;
            var kpis = Analysis.Dashboard(p);
            var panel = new WrapPanel();
            foreach (var g in kpis.GroupBy(k => k.Group))
            {
                var card = new StackPanel { Spacing = 4, Margin = new Thickness(6), Width = 300 };
                card.Children.Add(new TextBlock { Text = Loc.T(g.Key), FontWeight = FontWeight.Bold, FontSize = 15 });
                foreach (var k in g)
                {
                    var row = new DockPanel
                    {
                        // the tip shows over the whole row, the gap between the name and the value too
                        Background = Brushes.Transparent,
                        Children =
                        {
                            Name(k),
                            new TextBlock { Text = Units(k.Value), HorizontalAlignment = HorizontalAlignment.Right, FontWeight = FontWeight.SemiBold }
                        }
                    };
                    if (Tip(k) is { } tip) ToolTip.SetTip(row, tip);
                    card.Children.Add(row);
                }
                panel.Children.Add(new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8), Margin = new Thickness(4), Child = card });
            }
            host.Content = new ScrollViewer { Content = panel };
        }
        Refresh();
        vm.ModelRefreshed += Refresh;
        var w = Dialogs.ShowWindow("Dashboard", WithButtons(host, ("Refresh", Refresh)), 1000, 620);
        w.Closed += (_, _) => vm.ModelRefreshed -= Refresh;
    }

    // ---- burden ---------------------------------------------------------------------------

    public static void BurdenCheck(MainViewModel vm)
    {
        var tol = Number(20, 1, 90);
        var host = new ContentControl();
        List<Analysis.BurdenCheck> checks = new();
        void Update()
        {
            if (vm.Project is not { } p) return;
            checks = Analysis.CheckBurden(p, (double)(tol.Value ?? 20) / 100);
            host.Content = Table(new[] { "Hole", "Burden", "Spacing", "Hole ahead", "" },
                checks.OrderBy(c => c.Hole.Label).Select(c => new[] { c.Hole.Label, c.Burden is double b ? F(b) : Loc.T("front row"), c.Spacing is double s ? F(s) : "", c.AheadHole?.Label ?? "", c.Low ? "⚠ " + Loc.T("Low burden") : c.High ? "⚠ " + Loc.T("High burden") : "" }));
        }
        tol.ValueChanged += (_, _) => Update();
        Update();
        var top = Stack(Note(Loc.F("Burden is measured towards the free face (blast orientation {0:0}°). Design burden {1:0.00} m, spacing {2:0.00} m.", vm.Project!.Header.BlastOrientation, vm.Project.Design.Burden, vm.Project.Design.Spacing)), Inputs(("Tolerance (%)", tol)));
        Dialogs.ShowWindow("Burden check view", WithButtons(Stack(top, host),
            ("Select flagged holes", () => vm.Doc!.Select(checks.Where(c => c.Low || c.High).Select(c => c.Hole.Id))),
            ("Highlight", () => Highlight(vm, checks.Where(c => c.Low || c.High).Select(c => c.Hole.Id))),
            ("Set blast orientation", vm.SetBlastOrientation)), 720, 520);
    }

    public static void FrontRowProfile(MainViewModel vm)
    {
        var p = vm.Project!;
        var lines = p.Entities.OfType<PolylineEntity>().ToList();
        var crest = vm.Doc!.SelectedEntities.OfType<PolylineEntity>().FirstOrDefault(l => l.Kind == LineKind.Crest) ?? lines.FirstOrDefault(l => l.Kind == LineKind.Crest);
        var toe = lines.FirstOrDefault(l => l.Kind == LineKind.Toe);
        if (crest == null) { _ = Dialogs.Info(Loc.T("Draw or select a crest polyline (kind Crest) and optionally a toe polyline (kind Toe).")); return; }
        var holes = vm.Doc.SelectedHoles.Any() ? vm.Doc.SelectedHoles.ToList()
            : p.Holes.Where(h => !h.IsDummy).OrderBy(h => GeoMath.DistanceToPolyline2D(crest.Points, h.Collar, crest.Closed)).Take(Math.Max(1, p.Rows.FirstOrDefault()?.HoleIds.Count ?? 20)).ToList();
        var results = holes.Select(h => Analysis.FrontRowBurden(h, crest, toe)).OrderBy(r => r.Hole.Label).ToList();
        var chart = new SkiaView();
        var pick = new ComboBox { ItemsSource = results.Select(r => r.Hole.Label).ToList(), SelectedIndex = results.Count > 0 ? 0 : -1, Width = 160 };
        void Paint()
        {
            var r = pick.SelectedIndex >= 0 ? results[pick.SelectedIndex] : null;
            chart.Paint = (c, w, h) =>
            {
                if (r == null) return;
                Chart.Draw(c, w, h, new[]
                {
                    new Chart.Series("Burden", r.Profile.Select(x => (x.Depth, x.Burden)).ToList(), SKColors.SteelBlue),
                    new Chart.Series("Design burden", new[] { (0.0, p.Design.Burden), (r.Profile[^1].Depth, p.Design.Burden) }, SKColors.Gray)
                }, "Depth (m)", "Burden (m)");
            };
            chart.InvalidateVisual();
        }
        pick.SelectionChanged += (_, _) => Paint();
        Paint();
        var table = Table(new[] { "Hole", "Collar burden", "Toe burden", "Minimum", "At depth", "" },
            results.Select(r => new[] { r.Hole.Label, F(r.CollarBurden), F(r.ToeBurden), F(r.MinBurden), F(r.DepthOfMin, "0.0"), r.MinBurden < p.Design.Burden * 0.7 ? "⚠ " + Loc.T("Low burden") : "" }));
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,6,*") };
        Grid.SetRow(chart, 2);
        grid.Children.Add(table);
        grid.Children.Add(chart);
        var note = Note(toe == null ? Loc.T("No toe line (kind Toe): the face is assumed vertical below the crest.") : Loc.F("Face between crest \"{0}\" and toe \"{1}\".", crest.Label, toe.Label));
        Dialogs.ShowWindow("Front row burden profile", Stack(Stack(note, Inputs(("Hole", pick))), grid), 760, 640);
    }

    // ---- timing ------------------------------------------------------------------------------

    public static void ChargeVsTime(MainViewModel vm)
    {
        var bin = Number(vm.Project!.Vibration.WindowMs, 1, 1000);
        var chart = new SkiaView();
        var info = new TextBlock { Margin = new Thickness(0, 6) };
        List<Analysis.ChargeTimePoint> pts = new();
        void Update()
        {
            if (vm.Project is not { } p) return;
            pts = Analysis.ChargeVsTime(p, (double)(bin.Value ?? 8));
            chart.Paint = (c, w, h) => Chart.Draw(c, w, h, new[]
            {
                new Chart.Series("Charge per interval", pts.Select(x => (x.TimeMs, x.ChargeKg)).ToList(), new SKColor(0xFFF59E0B), Bars: true),
                new Chart.Series("Cumulative charge", pts.Select(x => (x.TimeMs, x.CumulativeKg)).ToList(), new SKColor(0xFF2563EB), RightAxis: true)
            }, "Time (ms)", "kg per interval", y2Title: "Cumulative kg");
            info.Text = pts.Count == 0 ? Loc.T("No timing.") : Loc.F("Largest interval: {0:0.0} kg ({1} holes) at {2:0} ms; total {3:0} kg", pts.Max(x => x.ChargeKg), pts.OrderByDescending(x => x.ChargeKg).First().Holes, pts.OrderByDescending(x => x.ChargeKg).First().TimeMs, pts[^1].CumulativeKg);
            chart.InvalidateVisual();
        }
        bin.ValueChanged += (_, _) => Update();
        Update();
        Dialogs.ShowWindow("Time envelope overview", WithButtons(Stack(Inputs(("Interval (ms)", bin)), chart, info),
            ("Export CSV…", () => _ = vm.SaveCsv("Export charge vs time", "_charge_time", "Time ms,kg,Cumulative kg,Holes\n" + string.Join("\n", pts.Select(x => string.Join(",", x.TimeMs.ToString(CultureInfo.InvariantCulture), x.ChargeKg.ToString("0.0", CultureInfo.InvariantCulture), x.CumulativeKg.ToString("0.0", CultureInfo.InvariantCulture), x.Holes)))))), 900, 520);
    }

    public static void TimingOverlap(MainViewModel vm)
    {
        var radius = Number(vm.Project!.Design.Spacing * 1.5, 0.5, 1000, 0.5);
        var window = Number(vm.Project.Vibration.WindowMs, 0.5, 1000, 0.5);
        var host = new ContentControl();
        List<(Hole A, Hole B, double DeltaMs, double Distance)> pairs = new();
        void Update()
        {
            if (vm.Project is not { } p) return;
            pairs = Analysis.TimingOverlap(p, (double)(radius.Value ?? 6), (double)(window.Value ?? 8));
            host.Content = Table(new[] { "Hole", "Neighbour", "Δt (ms)", "Distance (m)" }, pairs.Select(x => new[] { x.A.Label, x.B.Label, F(x.DeltaMs, "0.#"), F(x.Distance) }));
        }
        radius.ValueChanged += (_, _) => Update();
        window.ValueChanged += (_, _) => Update();
        Update();
        Dialogs.ShowWindow("Timing overlap", WithButtons(Stack(Inputs(("Neighbour radius (m)", radius), ("Window (ms)", window)), host),
            ("Select these holes", () => vm.Doc!.Select(pairs.SelectMany(x => new[] { x.A.Id, x.B.Id }).Distinct())),
            ("Highlight", () => Highlight(vm, pairs.SelectMany(x => new[] { x.A.Id, x.B.Id })))), 640, 480);
    }

    public static void VolumeOverview(MainViewModel vm)
    {
        var modes = new[] { "All", "Row", "Hole type", "Material type" };
        var by = new ComboBox { ItemsSource = modes.Select(Loc.T).ToList(), SelectedIndex = 1, Width = 180 };
        var host = new ContentControl();
        void Update()
        {
            if (vm.Project is not { } p) return;
            var groups = Analysis.VolumeOverview(p, modes[Math.Max(0, by.SelectedIndex)]);
            host.Content = Table(new[] { "Group", "Holes", "Volume m³", Tons, "Explosive kg", "Drilled m", Qty.Sym(BlastDesigner.Domain.Quantity.PowderFactor) },
                groups.Select(g => new[] { g.Group, g.Holes.ToString(), F(Qty.Show(BlastDesigner.Domain.Quantity.Volume, g.Volume), "0"), F(Qty.Show(BlastDesigner.Domain.Quantity.Tonnage, g.Tonnes), "0"), F(Qty.Show(BlastDesigner.Domain.Quantity.Mass, g.ExplosiveKg), "0.0"), F(Qty.Show(BlastDesigner.Domain.Quantity.Length, g.DrilledM), "0.0"), F(Qty.Show(BlastDesigner.Domain.Quantity.PowderFactor, g.PowderFactor), "0.000") }));
        }
        by.SelectionChanged += (_, _) => Update();
        Update();
        Dialogs.ShowWindow("Volume overview", Stack(Inputs(("Group by", by)), host), 760, 420);
    }

    // ---- prediction --------------------------------------------------------------------------

    public static void Flyrock(MainViewModel vm)
    {
        var host = new ContentControl();
        var info = Note("");
        Engine.Flyrock.Report r = Engine.Flyrock.Compute(vm.Project!);
        void Update()
        {
            if (vm.Project is not { } p) return;
            r = Engine.Flyrock.Compute(p);
            host.Content = Table(new[] { "Hole", Qty.Sym(BlastDesigner.Domain.Quantity.LinearCharge), "Burden m", "Stemming m", "Face burst m", "Cratering m", "Rifling m", "Max m" },
                r.Holes.OrderByDescending(h => h.Max).Select(h => new[] { h.Hole.Label, F(Qty.Show(BlastDesigner.Domain.Quantity.LinearCharge, h.ChargePerMetre), "0.0"), F(Qty.Show(BlastDesigner.Domain.Quantity.Length, h.Burden)), F(Qty.Show(BlastDesigner.Domain.Quantity.Length, h.Stemming)), F(Qty.Show(BlastDesigner.Domain.Quantity.Length, h.FaceBurst), "0"), F(Qty.Show(BlastDesigner.Domain.Quantity.Length, h.Cratering), "0"), F(Qty.Show(BlastDesigner.Domain.Quantity.Length, h.Rifling), "0"), F(Qty.Show(BlastDesigner.Domain.Quantity.Length, h.Max), "0") }));
            info.Text = r.Holes.Count == 0 ? Loc.T("Load the holes first: flyrock is calculated from the charge columns.") :
                Loc.F("Maximum throw {0:0} m (hole {1}); clearance with safety factor {2}: {3:0} m. k = {4}. Face burst uses the crest line (kind Crest) when there is one.", r.MaxThrow, r.Critical?.Label, p.Flyrock.SafetyFactor, r.Clearance, p.Flyrock.SiteK);
        }
        Update();
        vm.ModelRefreshed += Update;
        var w = Dialogs.ShowWindow("Flyrock prediction", WithButtons(Stack(info, host),
            ("Constants…", () => _ = vm.EditFlyrockModel()),
            ("Draw clearance zone", () =>
            {
                if (vm.Project is not { } p) return;
                var polys = Engine.Flyrock.ClearancePolygon(p, r);
                if (polys.Count > 0) vm.Change("Flyrock clearance zone", pr =>
                {
                    var layer = pr.GetOrCreateLayer(StandardLayers.Analysis, 0xFFDC2626, "Analysis");
                    foreach (var poly in polys)
                        pr.Add(new PolylineEntity { Points = poly, Closed = true, Kind = LineKind.ClearanceZone, Label = Loc.F("Flyrock clearance {0:0} m", r.Clearance) }, layer);
                });
            }),
            ("Select critical hole", () => { if (r.Critical != null) vm.Doc!.Select(new[] { r.Critical.Id }); }),
            ("Export CSV…", () => _ = vm.SaveCsv("Export flyrock", "_flyrock", Interchange.ExportFlyrock(r)))), 860, 520);
        w.Closed += (_, _) => vm.ModelRefreshed -= Update;
    }

    public static void Fragmentation(MainViewModel vm)
    {
        var chart = new SkiaView();
        var info = new SelectableTextBlock { Margin = new Thickness(0, 6), TextWrapping = TextWrapping.Wrap };
        Engine.Fragmentation.Result r = Engine.Fragmentation.Compute(vm.Project!);
        // the sizes in the unit of the fragmentation inputs
        var unit = SizeUnits.Of(vm.Project!.Fragmentation);
        string Size(double cm) => SizeUnits.FromCm(cm, unit).ToString(SizeUnits.Format(unit), CultureInfo.CurrentCulture) + " " + Loc.Ui(SizeUnits.Symbol(unit));
        List<(double, double)> InUnit(List<(double SizeCm, double PassingPct)> curve) => curve.Select(c => (SizeUnits.FromCm(c.SizeCm, unit), c.PassingPct)).ToList();
        void Update()
        {
            if (vm.Project is not { } p) return;
            var sel = vm.Doc!.SelectedHoles.ToList();
            r = Engine.Fragmentation.Compute(p, sel.Count > 0 ? sel : null);
            unit = SizeUnits.Of(p.Fragmentation);
            chart.Paint = (c, w, h) => Chart.Draw(c, w, h, new[]
            {
                new Chart.Series("Kuz-Ram (Rosin-Rammler)", InUnit(r.Curve), new SKColor(0xFF2563EB)),
                new Chart.Series("Swebrec", InUnit(r.Swebrec), new SKColor(0xFFDC2626))
            }, Loc.F("Size ({0})", Loc.Ui(SizeUnits.Symbol(unit))), "Passing (%)", logX: true);
            info.Text = r.X50Cm <= 0 ? Loc.T("Load the holes first: fragmentation needs the powder factor and charge per hole.") : string.Join("   ", new[]
            {
                Loc.F("Rock factor A = {0:0.0}", r.RockFactor), Loc.F("Powder factor {0:0.000} kg/m³", r.PowderFactor), Loc.F("Charge per hole {0:0} kg", r.ChargePerHole), Loc.F("RWS {0:0}", r.Rws),
                Loc.F("n = {0:0.00}", r.Uniformity), "X20 " + Size(r.X20Cm), "X50 " + Size(r.X50Cm), "X80 " + Size(r.X80Cm),
                Loc.F("Oversize > {0}: {1:0.0} %", Size(p.Fragmentation.OversizeCm), r.OversizePct), Loc.F("Fines < {0}: {1:0.0} %", Size(p.Fragmentation.FinesCm), r.FinesPct),
                sel.Count > 0 ? Loc.F("({0} selected holes)", sel.Count) : Loc.T("(all holes)")
            });
            chart.InvalidateVisual();
        }
        Update();
        // it reports on the selected holes when there are any
        vm.ModelRefreshed += Update;
        vm.SelectionChanged += Update;
        var w = Dialogs.ShowWindow("Fragmentation prediction", WithButtons(Stack(new Panel(), chart, info),
            ("Inputs…", () => _ = vm.EditFragmentationModel()),
            ("Recalculate", Update),
            ("Export CSV…", () => _ = vm.SaveCsv("Export fragmentation", "_fragmentation", Interchange.ExportFragmentation(r, unit)))), 900, 600);
        w.Closed += (_, _) => { vm.ModelRefreshed -= Update; vm.SelectionChanged -= Update; };
    }

    /// <summary>Site-law regression of the project's vibration records, plus a scale-law calculator for any distance and charge.</summary>
    public static void SiteLaw(MainViewModel vm)
    {
        var records = ProjectJson.DeepClone(vm.Project!.VibrationRecords);
        var chart = new SkiaView();
        var result = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6) };
        var distance = Number(500, 1, 100000, 10);
        var charge = Number(Math.Max(1, Timing.MaxInstantaneousCharge(vm.Project, vm.Project.Vibration.WindowMs)), 0.1, 1e6, 10);
        var confidence = Number(95, 50, 99.9, 1);
        var calc = new TextBlock { Margin = new Thickness(0, 4), TextWrapping = TextWrapping.Wrap };
        Engine.SiteLaw.Fit? fit = null;
        var editor = new ListEditor<VibrationRecord>(records, r => $"{(r.Use ? "" : "✗ ")}{(r.Name.Length > 0 ? r.Name : "?")}  {r.PpvMm:0.##} mm/s @ {r.DistanceM:0} m",
            () => new VibrationRecord { Name = Loc.F("Event {0}", records.Count + 1), Date = DateTime.Today, ChargeKg = (double)(charge.Value ?? 100), DistanceM = (double)(distance.Value ?? 500) },
            r => ProjectJson.DeepClone(r));
        editor.ListWidth = 190;
        void Update()
        {
            fit = Engine.SiteLaw.Regress(records);
            var conf = (double)(confidence.Value ?? 95) / 100;
            var used = records.Where(r => r.Use && r.DistanceM > 0 && r.ChargeKg > 0 && r.PpvMm > 0).ToList();
            chart.Paint = (c, w, h) =>
            {
                var series = new List<Chart.Series> { new("Measured", used.Select(r => (Engine.SiteLaw.ScaledDistance(r.DistanceM, r.ChargeKg), r.PpvMm)).ToList(), new SKColor(0xFFE6E9ED), Scatter: true) };
                if (fit != null && used.Count > 0)
                {
                    var sds = used.Select(r => Engine.SiteLaw.ScaledDistance(r.DistanceM, r.ChargeKg)).ToList();
                    var lo = sds.Min() * 0.7; var hi = sds.Max() * 1.4;
                    var xs = Enumerable.Range(0, 30).Select(i => lo * Math.Pow(hi / lo, i / 29.0)).ToList();
                    series.Add(new("50 % (mean)", xs.Select(x => (x, fit.PpvAt(x, 0.5))).ToList(), new SKColor(0xFF2563EB)));
                    series.Add(new(Loc.F("{0:0.#} % confidence", conf * 100), xs.Select(x => (x, fit.PpvAt(x, conf))).ToList(), new SKColor(0xFFDC2626)));
                }
                Chart.Draw(c, w, h, series, "Scaled distance (m/√kg)", "PPV (mm/s)", logX: true, logY: true);
            };
            chart.InvalidateVisual();
            result.Text = fit == null ? Loc.T("Add at least two events with distance, charge and PPV to fit the site law.") :
                Loc.F("PPV = K · (D/√Q)^-B.  Events: {0}.  K(50 %) = {1:0}, B = {2:0.000}, R² = {3:0.000}, standard error {4:0.000} (log10).  K at {5:0.#} % = {6:0}.", fit.Count, fit.K50, fit.B, fit.R2, fit.StdError, conf * 100, fit.KAt(conf));
            if (vm.Project is not { } p) return;
            var d = (double)(distance.Value ?? 500);
            var q = (double)(charge.Value ?? 100);
            var k = fit?.K50 ?? p.Vibration.SiteK;
            var b = fit?.B ?? p.Vibration.SiteB;
            var sd = Engine.SiteLaw.ScaledDistance(d, q);
            var ppv50 = fit?.PpvAt(sd, 0.5) ?? Vibration.Ppv(p.Vibration, d, q);
            var ppvC = fit?.PpvAt(sd, conf) ?? ppv50;
            var kc = fit?.KAt(conf) ?? k;
            calc.Text = Loc.F("Scaled distance {0:0.0} m/√kg: PPV {1:0.00} mm/s (50 %), {2:0.00} mm/s ({3:0.#} %). Allowable charge for {4} mm/s: {5:0.0} kg. Distance for {4} mm/s: {6:0} m.",
                sd, ppv50, ppvC, conf * 100, p.Vibration.PpvLimit, Math.Pow(d / Math.Pow(kc / p.Vibration.PpvLimit, 1 / b), 2), Engine.SiteLaw.DistanceForPpv(kc, b, q, p.Vibration.PpvLimit));
        }
        editor.Changed += Update;
        distance.ValueChanged += (_, _) => Update();
        charge.ValueChanged += (_, _) => Update();
        confidence.ValueChanged += (_, _) => Update();
        Update();
        var right = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto,Auto") };
        var calcPanel = new StackPanel { Children = { Inputs(("Distance (m)", distance), ("Charge (kg)", charge), ("Confidence (%)", confidence)), calc } };
        Grid.SetRow(result, 1);
        Grid.SetRow(calcPanel, 2);
        right.Children.Add(chart);
        right.Children.Add(result);
        right.Children.Add(calcPanel);
        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("540,8,*") };
        Grid.SetColumn(right, 2);
        split.Children.Add(editor);
        split.Children.Add(right);
        void Save() => vm.Change("Vibration records", pr => pr.VibrationRecords = ProjectJson.DeepClone(records));
        Dialogs.ShowWindow("Scale law calculator", WithButtons(split,
            ("Save events to project", Save),
            ("Use fitted K and B", () =>
            {
                if (fit == null) return;
                var conf = (double)(confidence.Value ?? 95) / 100;
                Save();
                vm.Change("Vibration site constants", pr => { pr.Vibration.SiteK = fit.KAt(conf); pr.Vibration.SiteB = fit.B; });
            }),
            ("Import CSV…", async () =>
            {
                var path = await Dialogs.OpenFile("Import vibration events", ("CSV", new[] { "*.csv", "*.txt" }));
                if (path == null) return;
                foreach (var line in (await File.ReadAllLinesAsync(path)).Skip(1))
                {
                    var a = line.Split(',', ';', '\t');
                    if (a.Length < 4) continue;
                    double V(int i) => double.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
                    records.Add(new VibrationRecord { Name = a[0], DistanceM = V(1), ChargeKg = V(2), PpvMm = V(3), AirblastDb = a.Length > 4 ? V(4) : null });
                }
                editor.Reload(records.Count - 1);
                Update();
            })), 1200, 640);
    }

    public static void ExclusionZone(MainViewModel vm)
    {
        var host = new ContentControl();
        var merge = new TextBox { Width = 90, Name = "exclusionMerge", VerticalAlignment = VerticalAlignment.Center };
        // the fields of the sources typed in and not yet taken: taken before the zone is drawn
        var pending = new List<Action>();
        // what the arrows measure, under the list of the sources; none without sources
        var size = SizeSwitch(vm);
        void Update()
        {
            if (vm.Project is not { } p) return;
            // the cards show the source with the largest radius; with no hole charged the blast as a whole
            var sources = Engine.ExclusionZone.Sources(p);
            var r = sources.Count > 0 ? sources.MaxBy(s => s.Radius)!.Result : Engine.ExclusionZone.Compute(p);
            pending.Clear();
            host.Content = ExclusionZoneView(vm, p, r, sources, pending);
            size.IsVisible = sources.Count > 0;
            var auto = Engine.ExclusionZone.AutoMergeDistance(Engine.ExclusionZone.SourceHoles(p));
            merge.Watermark = Loc.F("auto {0:0} m", auto);
            merge.Text = p.Vibration.ExclusionMergeDistance > 0 ? Qty.Show(BlastDesigner.Domain.Quantity.Length, p.Vibration.ExclusionMergeDistance).ToString("0.##", CultureInfo.CurrentCulture) : "";
        }
        Update();
        // the distance that joins holes into one source: empty or 0 is worked out from the spacing
        void SetMerge()
        {
            if (vm.Project is not { } p) return;
            var text = merge.Text?.Trim() ?? "";
            double d = 0;
            if (text.Length > 0 && !double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { Update(); return; }
            d = Math.Max(0, Qty.Store(BlastDesigner.Domain.Quantity.Length, d));
            if (Math.Abs(d - p.Vibration.ExclusionMergeDistance) < 1e-9) return;
            ChangeZone(vm, "Exclusion zone sources", pr => pr.Vibration.ExclusionMergeDistance = d);
            Update();
        }
        merge.LostFocus += (_, _) => SetMerge();
        merge.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { SetMerge(); e.Handled = true; } };

        TextBlock Label(string text) => new() { Text = Loc.T(text), Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var mergeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        mergeRow.Children.Add(Label("Merge distance, m"));
        mergeRow.Children.Add(merge);
        ToolTip.SetTip(mergeRow, Loc.T("Charged holes no farther apart than this are one source of the zone; empty — from the spacing of the holes."));

        Button Action(string text, bool primary, Action click)
        {
            var b = new Button { Content = Loc.T(text), MinHeight = 32, Padding = new Thickness(14, 6), VerticalContentAlignment = VerticalAlignment.Center };
            if (primary) b.Classes.Add("primary");
            b.Click += (_, _) => { try { click(); } catch (Exception ex) { Dialogs.ShowError(ex); } };
            return b;
        }
        // the constants are edited in their own window: the radii are worked out again when it is closed, the zone on the plan too
        async void Edit(Func<Task> edit)
        {
            await edit();
            if (vm.Project is { } p && ZoneShown(p)) vm.Change("Exclusion zone", pr => PlaceZones(pr));
            Update();
        }
        var setup = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        // the constants and Recalculate together on the left, Draw on its own on the right
        foreach (var b in new[] { Action("Site constants…", false, () => Edit(vm.EditVibrationModel)), Action("Flyrock constants…", false, () => Edit(vm.EditFlyrockModel)), Action("Recalculate", false, Update) })
        {
            b.Margin = new Thickness(0, 0, 8, 0);
            setup.Children.Add(b);
        }
        var run = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        // one button: draws the zone while there is none on the plan, else hides it
        Button toggle = null!;
        toggle = Action("Draw exclusion zone", true, () =>
        {
            if (vm.Project is not { } p) return;
            if (ZoneShown(p)) vm.Change("Hide exclusion zone", pr => pr.Entities.RemoveAll(MainViewModel.IsExclusionZone));
            else
            {
                foreach (var take in pending.ToList()) take();
                SetMerge();
                vm.Change("Exclusion zone", pr => PlaceZones(pr));
            }
            ShowToggle();
        });
        toggle.Name = "exclusionDraw";
        void ShowToggle() => toggle.Content = Loc.T(vm.Project is { } p && ZoneShown(p) ? "Hide exclusion zone" : "Draw exclusion zone");
        ShowToggle();
        // the zone may come and go by an undo or Clear analysis too
        Action refreshed = ShowToggle;
        vm.ModelRefreshed += refreshed;
        run.Children.Add(toggle);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetColumn(run, 1);
        footer.Children.Add(setup);
        footer.Children.Add(run);
        // the list of the sources, Size, the merge distance on the next line, the buttons: the window as high as they are
        var page = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        page.Children.Add(host);
        page.Children.Add(size);
        page.Children.Add(mergeRow);
        page.Children.Add(footer);
        var window = Dialogs.ShowWindow("Exclusion zone calculator", page, 800, 600, beforeShow: w => { w.ClearValue(Window.HeightProperty); w.SizeToContent = SizeToContent.Height; });
        window.Closed += (_, _) => vm.ModelRefreshed -= refreshed;
    }

    private static bool ZoneShown(BlastProject p) => p.Entities.Any(MainViewModel.IsExclusionZone);

    /// <summary>
    /// The exclusion zone on the plan, worked out anew from the project: the zone drawn before goes, one zone on the plan,
    /// not one over the other.
    /// </summary>
    private static void PlaceZones(BlastProject pr)
    {
        var zones = Engine.ExclusionZone.Zones(Engine.ExclusionZone.Sources(pr), pr.Vibration.ExclusionSize);
        pr.Entities.RemoveAll(MainViewModel.IsExclusionZone);
        if (zones.Count == 0) return;
        var layer = pr.GetOrCreateLayer(StandardLayers.Analysis, 0xFFDC2626, "Analysis");
        foreach (var z in zones)
            pr.Add(new PolylineEntity
            {
                Points = z.Outline, Closed = true, Kind = LineKind.ExclusionZone,
                Label = Loc.F("Exclusion zone {0:0} m", z.Radius), ZoneArrows = z.Arrows.Count > 0 ? z.Arrows : null
            }, layer);
    }

    /// <summary>A change of a setting of the zone; the zone on the plan, if there is one, is drawn again with it in the same undo step.</summary>
    private static void ChangeZone(MainViewModel vm, string name, Action<BlastProject> change) =>
        vm.Change(name, pr =>
        {
            var shown = ZoneShown(pr);
            change(pr);
            if (shown) PlaceZones(pr);
        });

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>Sets the radius typed in and the azimuth of the radius arrow of a source, kept by a hole of it; one with neither goes.</summary>
    private static void SetSource(MainViewModel vm, Engine.ExclusionZone.Source s, double? radius, double azimuth) =>
        ChangeZone(vm, "Exclusion zone source", pr =>
        {
            var list = pr.Vibration.ExclusionSources;
            var set = Engine.ExclusionZone.Setting(pr, s.Holes);
            if (set == null) list.Add(set = new ExclusionSourceSetting { HoleId = s.Holes[0].Id });
            set.Radius = radius;
            set.Azimuth = azimuth;
            // the settings of holes gone and the ones that set nothing
            var ids = pr.Holes.Select(h => h.Id).ToHashSet();
            list.RemoveAll(x => !ids.Contains(x.HoleId) || (x.Radius == null && x.Azimuth == 0));
        });

    /// <summary>
    /// A row a source: its radius (calculated, or typed in over it), the azimuth of its radius arrow, how many holes it has,
    /// its charge and what sets the calculated radius.
    /// </summary>
    private static Control SourcesTable(MainViewModel vm, IReadOnlyList<Engine.ExclusionZone.Source> sources, List<Action> pending)
    {
        var table = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto,*"), Name = "exclusionSources" };
        string Lead(Engine.ExclusionZone.Result x) =>
            x.Radius == x.VibrationRadius ? "Vibration" : x.Radius == x.AirblastRadius ? "Airblast" : "Flyrock";
        void Cell(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); table.Children.Add(c); }
        TextBlock Text(string text, IBrush brush, FontWeight weight = FontWeight.Normal, double size = 13) =>
            new() { Text = text, Foreground = brush, FontWeight = weight, FontSize = size, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 22, 3) };
        table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Cell(Text(Loc.T("Radius, m"), Look.Muted, size: 12), 0, 1);
        Cell(Text(Loc.T("Azimuth, °"), Look.Muted, size: 12), 0, 2);
        for (int i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            int row = i + 1;
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Cell(Text(Loc.F("Source {0}", s.Number), Look.Text, FontWeight.SemiBold), row, 0);

            // the radii in the unit of lengths shown: the one typed in kept in metres
            var calculated = Qty.Show(BlastDesigner.Domain.Quantity.Length, s.Result.Radius);
            var radius = new TextBox { Width = 80, Name = $"exclusionRadius{s.Number}", Watermark = calculated.ToString("0", CultureInfo.CurrentCulture), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 12, 2) };
            var azimuth = new TextBox { Width = 64, Name = $"exclusionAzimuth{s.Number}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 22, 2) };
            ToolTip.SetTip(radius, Loc.F("Calculated {0:0} m. Type another to draw the zone of this source with it; empty — the calculated one", s.Result.Radius));
            ToolTip.SetTip(azimuth, Loc.T("Where the radius arrow of this source points, degrees clockwise from north"));
            double? set = Qty.Show(BlastDesigner.Domain.Quantity.Length, s.SetRadius);
            double side = s.Azimuth;
            void Show()
            {
                radius.Text = (set ?? calculated).ToString("0.#", CultureInfo.CurrentCulture);
                radius.FontWeight = set != null ? FontWeight.SemiBold : FontWeight.Normal;
                radius.Foreground = set != null ? Look.Accent : Look.Text;
                azimuth.Text = side.ToString("0.#", CultureInfo.CurrentCulture);
            }
            Show();
            void Take()
            {
                double? r = set;
                var text = radius.Text?.Trim() ?? "";
                // empty, or the calculated radius as it is shown: none typed in
                if (text.Length == 0 || text == calculated.ToString("0.#", CultureInfo.CurrentCulture)) r = null;
                else if (TryNumber(text, out var v) && v > 0) r = v;
                var a = side;
                if (TryNumber(azimuth.Text, out var w)) a = (w % 360 + 360) % 360;
                if (r != set || Math.Abs(a - side) > 1e-9)
                {
                    set = r;
                    side = a;
                    SetSource(vm, s, Qty.Store(BlastDesigner.Domain.Quantity.Length, set), side);
                }
                Show();
            }
            pending.Add(Take);
            foreach (var box in new[] { radius, azimuth })
            {
                box.LostFocus += (_, _) => Take();
                box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Take(); e.Handled = true; } };
            }
            Cell(radius, row, 1);
            Cell(azimuth, row, 2);
            Cell(Text(Loc.F("{0} holes", s.Holes.Count), Look.Muted), row, 3);
            Cell(Text(Loc.F("{0:0.0} kg", s.Result.Mic), Look.TextSoft), row, 4);
            Cell(Text(Loc.F("Set by: {0}", Loc.T(Lead(s.Result))), Look.TextSoft), row, 5);
        }
        var list = new StackPanel { Spacing = 6 };
        if (sources.Count > 1)
            list.Children.Add(new TextBlock { Text = Loc.F("{0} sources: each zone of its own radius", sources.Count), FontSize = 12, Foreground = Look.Muted });
        list.Children.Add(table);
        return new Border
        {
            Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10), Margin = new Thickness(0, 12, 0, 0), Child = list
        };
    }

    /// <summary>What the arrows of the zone measure: buttons side by side in one frame, as 2D and 3D over the plan.</summary>
    private static Control SizeSwitch(MainViewModel vm)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var of = new Dictionary<Button, ExclusionSizing>();
        void Show()
        {
            var now = vm.Project?.Vibration.ExclusionSize ?? ExclusionSizing.Radius;
            foreach (var (b, size) in of)
            {
                b.Background = size == now ? Look.AccentSoft : Brushes.Transparent;
                b.Foreground = size == now ? Look.Accent : Look.Muted;
            }
        }
        foreach (var (size, text, tip) in new[]
        {
            (ExclusionSizing.Length, "Length", "A zone of one source its radius; a zone several sources make its length and a width across each source"),
            (ExclusionSizing.Radius, "Radius", "Each source the arrow of its radius, also inside a zone several sources make")
        })
        {
            var b = new Button
            {
                Content = Loc.T(text), Padding = new Thickness(12, 4), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(0),
                FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center, Name = "exclusionSize" + size
            };
            ToolTip.SetTip(b, Loc.T(tip));
            b.Click += (_, _) =>
            {
                if (vm.Project is not { } p || p.Vibration.ExclusionSize == size) return;
                ChangeZone(vm, "Exclusion zone arrows", pr => pr.Vibration.ExclusionSize = size);
                Show();
            };
            of[b] = size;
            buttons.Children.Add(b);
        }
        Show();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(new TextBlock { Text = Loc.T("Size"), Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        row.Children.Add(new Border
        {
            Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Center, Child = buttons
        });
        return row;
    }

    /// <summary>
    /// The radius of the exclusion zone large on top, with the charge it comes from; below it the three radii it is the
    /// largest of, each on a card with a bar of its length against the radius, the one that sets the radius marked. With
    /// several sources these are of the one with the largest radius, and a list of the sources follows.
    /// </summary>
    private static Control ExclusionZoneView(MainViewModel vm, BlastProject p, Engine.ExclusionZone.Result r, IReadOnlyList<Engine.ExclusionZone.Source> sources, List<Action> pending)
    {
        var v = p.Vibration;
        var factors = new (string Icon, string Name, double Radius, string Detail)[]
        {
            ("wave", "Vibration", r.VibrationRadius, Loc.F("{0} mm/s · K = {1:0} · B = {2:0.00}", v.PpvLimit, v.SiteK, v.SiteB)),
            ("air", "Airblast", r.AirblastRadius, Loc.F("Limit {0} dB", v.AirblastLimitDb)),
            ("exclusion", "Flyrock", r.FlyrockRadius, Loc.T("Clearance of the flyrock")),
        };
        var max = r.Radius;
        var lead = factors.OrderByDescending(f => f.Radius).First();
        string Metres(double m) => double.IsFinite(m) ? Loc.F("{0:0} m", m) : "—";
        TextBlock Caption(string text) => new() { Text = text, FontSize = 12, Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap };

        // the radius
        var radius = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        radius.Children.Add(Caption(Loc.T("Exclusion zone radius")));
        radius.Children.Add(new SelectableTextBlock { Text = Metres(max), FontSize = 34, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, Name = "exclusionRadius" });
        radius.Children.Add(new TextBlock { Text = Loc.F("Set by: {0}", Loc.T(lead.Name)), FontSize = 12, Foreground = Look.TextSoft });
        var mark = new Border
        {
            Width = 52, Height = 52, CornerRadius = new CornerRadius(26), Background = Look.AccentSoft, Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = Look.Icon("exclusion", 28, Look.Accent)
        };
        var charge = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 0, 0, 0) };
        charge.Children.Add(Caption(Loc.T("Maximum instantaneous charge")));
        charge.Children.Add(new SelectableTextBlock { Text = Loc.F("{0:0.0} kg", r.Mic), FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Look.Text });
        charge.Children.Add(new TextBlock { Text = Loc.F("In a window of {0} ms", v.WindowMs), FontSize = 12, Foreground = Look.TextSoft });
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(radius, 1);
        Grid.SetColumn(charge, 2);
        top.Children.Add(mark);
        top.Children.Add(radius);
        top.Children.Add(charge);
        var hero = new Border { Background = Look.Ribbon, BorderBrush = Look.Accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 14), Child = top };

        // the three radii
        var cards = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(-6, 12, -6, 0) };
        foreach (var f in factors)
        {
            var leads = f == lead;
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(Look.Icon(f.Icon, 18, leads ? Look.Accent : Look.Muted));
            head.Children.Add(new TextBlock { Text = Loc.T(f.Name), FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center });
            // the bar: the radius against the largest
            var share = max > 0 && double.IsFinite(f.Radius) ? Math.Clamp(f.Radius / max, 0.001, 1) : 0.001;
            var bar = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions($"{share.ToString(CultureInfo.InvariantCulture)}*,{Math.Max(0.001, 1 - share).ToString(CultureInfo.InvariantCulture)}*"),
                Height = 4, Background = Look.Line, Margin = new Thickness(0, 10, 0, 8)
            };
            bar.Children.Add(new Border { Background = leads ? Look.Accent : Look.Blue, CornerRadius = new CornerRadius(2) });
            var body = new StackPanel();
            body.Children.Add(head);
            body.Children.Add(new SelectableTextBlock { Text = Metres(f.Radius), FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, Margin = new Thickness(0, 8, 0, 0) });
            body.Children.Add(bar);
            body.Children.Add(Caption(f.Detail));
            cards.Children.Add(new Border
            {
                Background = Look.Panel, BorderBrush = leads ? Look.Accent : Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12), Margin = new Thickness(6, 0), Child = body
            });
        }
        var view = new StackPanel();
        view.Children.Add(hero);
        view.Children.Add(cards);
        if (sources.Count > 0) view.Children.Add(SourcesTable(vm, sources, pending));
        return new ScrollViewer { Content = view, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    // ---- quality -----------------------------------------------------------------------------

    public static void Tolerances(MainViewModel vm)
    {
        var host = new ContentControl();
        var info = Note("");
        List<ToleranceResult> results = new();
        void Update()
        {
            if (vm.Project is not { } p) return;
            results = Tolerance.Check(p);
            var t = p.Tolerances;
            host.Content = Table(new[] { "Hole", "Collar dev m", "Depth dev m", "Toe dev m", "Angle dev °", "Bearing dev °", "Result" },
                results.OrderBy(x => x.Pass).ThenBy(x => x.Hole.Label).Select(x => new[] { x.Hole.Label, F(x.CollarDev), F(x.DepthDev), F(x.ToeDev), F(x.AngleDev, "0.0"), F(x.BearingDev, "0.0"), x.Pass ? Loc.T("Pass") : "⚠ " + string.Join("; ", x.Failures.Select(Loc.T)) }));
            info.Text = Loc.F("{0} of {1} holes within tolerance (collar {2} m, depth {3} m, toe {4} m, angle {5}°, bearing {6}°).", results.Count(x => x.Pass), results.Count, t.CollarXY, t.Depth, t.ToeXY, t.AngleDeg, t.BearingDeg);
        }
        Update();
        vm.ModelRefreshed += Update;
        var w = Dialogs.ShowWindow("Design tolerance check", WithButtons(Stack(info, host),
            ("Tolerances…", () => _ = vm.EditTolerances()),
            ("Select failing holes", () => vm.Doc!.Select(results.Where(x => !x.Pass).Select(x => x.Hole.Id))),
            ("Highlight", () => Highlight(vm, results.Where(x => !x.Pass).Select(x => x.Hole.Id))),
            ("Export CSV…", () => _ = vm.SaveCsv("Export tolerance check", "_tolerances", Interchange.ExportTolerances(results)))), 860, 520);
        w.Closed += (_, _) => vm.ModelRefreshed -= Update;
    }

    public static void StrataIntercepts(MainViewModel vm)
    {
        var p = vm.Project!;
        var surfaces = p.Entities.OfType<SurfaceEntity>().Where(s => s.Kind == SurfaceKind.Strata).ToList();
        var note = surfaces.Count == 0 ? Loc.T("No surface has kind Strata; all surfaces are used.") : Loc.F("{0} strata surfaces.", surfaces.Count);
        if (surfaces.Count == 0) surfaces = p.Entities.OfType<SurfaceEntity>().ToList();
        var holes = vm.Doc!.SelectedHoles.Any() ? vm.Doc.SelectedHoles.ToList() : p.Holes.ToList();
        var list = Engine.Strata.Intercepts(holes, surfaces);
        var table = Table(new[] { "Hole", "Surface", "Depth", "X", "Y", "Z" }, list.Select(i => new[] { i.Hole.Label, i.Surface.Name, F(i.Depth), F(i.Point.X, "0.###"), F(i.Point.Y, "0.###"), F(i.Point.Z, "0.###") }));
        Dialogs.ShowWindow("Strata intercepts", WithButtons(Stack(Note(note), table),
            ("Create intercept points", () => vm.Change("Create intercept points", pr =>
            {
                var layer = pr.GetOrCreateLayer(StandardLayers.Analysis, 0xFFDC2626, "Analysis");
                foreach (var i in list) pr.Add(new PointEntity { Position = i.Point, Label = $"{i.Hole.Label}/{i.Surface.Name}" }, layer);
            })),
            ("Set material type from strata", () => _ = vm.MaterialFromStrata()),
            ("Export CSV…", () => _ = vm.SaveCsv("Export strata intercepts", "_intercepts", Interchange.ExportStrataIntercepts(list)))), 760, 480);
    }
}
