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
            host.Content = Table(new[] { "Group", "Holes", "Volume m³", "Tonnes", "Explosive kg", "Drilled m", "kg/m³" },
                groups.Select(g => new[] { g.Group, g.Holes.ToString(), F(g.Volume, "0"), F(g.Tonnes, "0"), F(g.ExplosiveKg, "0.0"), F(g.DrilledM, "0.0"), F(g.PowderFactor, "0.000") }));
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
            host.Content = Table(new[] { "Hole", "kg/m", "Burden m", "Stemming m", "Face burst m", "Cratering m", "Rifling m", "Max m" },
                r.Holes.OrderByDescending(h => h.Max).Select(h => new[] { h.Hole.Label, F(h.ChargePerMetre, "0.0"), F(h.Burden), F(h.Stemming), F(h.FaceBurst, "0"), F(h.Cratering, "0"), F(h.Rifling, "0"), F(h.Max, "0") }));
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
                var poly = Engine.Flyrock.ClearancePolygon(p, r);
                if (poly.Count > 2) vm.Change("Flyrock clearance zone", pr => pr.Add(new PolylineEntity { Points = poly, Closed = true, Kind = LineKind.ClearanceZone, Label = Loc.F("Flyrock clearance {0:0} m", r.Clearance) }, pr.GetOrCreateLayer(StandardLayers.Analysis, 0xFFDC2626, "Analysis")));
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
        var text = new SelectableTextBlock { Margin = new Thickness(8), FontSize = 14 };
        Engine.ExclusionZone.Result r = Engine.ExclusionZone.Compute(vm.Project!);
        void Update()
        {
            if (vm.Project is not { } p) return;
            r = Engine.ExclusionZone.Compute(p);
            text.Text = string.Join("\n", new[]
            {
                Loc.F("Maximum instantaneous charge: {0:0.0} kg (window {1} ms)", r.Mic, p.Vibration.WindowMs),
                Loc.F("Vibration ({0} mm/s, K = {1:0}, B = {2:0.00}): {3:0} m", p.Vibration.PpvLimit, p.Vibration.SiteK, p.Vibration.SiteB, r.VibrationRadius),
                Loc.F("Airblast ({0} dB): {1:0} m", p.Vibration.AirblastLimitDb, r.AirblastRadius),
                Loc.F("Flyrock clearance: {0:0} m", r.FlyrockRadius),
                "",
                Loc.F("Exclusion zone radius: {0:0} m", r.Radius)
            });
        }
        Update();
        Dialogs.ShowWindow("Exclusion zone calculator", WithButtons(text,
            ("Site constants…", () => _ = vm.EditVibrationModel()),
            ("Flyrock constants…", () => _ = vm.EditFlyrockModel()),
            ("Recalculate", Update),
            ("Draw exclusion zone", () =>
            {
                if (vm.Project is not { } p) return;
                var holes = p.Holes.Where(h => !h.IsDummy).ToList();
                if (holes.Count == 0) return;
                var zone = GeneralOps.ClearanceZone(holes, r.Radius);
                zone.Label = Loc.F("Exclusion zone {0:0} m", r.Radius);
                vm.Change("Exclusion zone", pr => pr.Add(zone, pr.GetOrCreateLayer(StandardLayers.Analysis, 0xFFDC2626, "Analysis")));
            })), 620, 300);
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
