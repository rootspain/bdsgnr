using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using SkiaSharp;

namespace BlastDesigner.Rendering;

/// <summary>Sections that can be switched on in a report.</summary>
[Flags]
public enum ReportSections
{
    None = 0,
    Header = 1,
    Plan = 2,
    Quantities = 4,
    HoleTable = 8,
    Loading = 16,
    Timing = 32,
    Vibration = 64,
    Outcomes = 128,
    All = 255
}

public sealed class ReportOptions
{
    public string Title { get; set; } = "Blast report";
    public ReportSections Sections { get; set; } = ReportSections.All;
    public bool Landscape { get; set; }
    public TimingLabelMode PlanTimingLabels { get; set; } = TimingLabelMode.InholeTime;
}

/// <summary>Builds PDF reports and plan prints with SkiaSharp's PDF backend (A4).</summary>
public static class Reports
{
    private static string T(string s) => PlanRenderer.Translate(s);

    private const float A4W = 595, A4H = 842, Margin = 40;

    public static void WritePdf(BlastProject project, Stream output, ReportOptions opt)
    {
        Timing.Solve(project);
        using var doc = SKDocument.CreatePdf(output, new SKDocumentPdfMetadata { Title = opt.Title, Author = project.Header.Author, Creator = "Blast Designer", Creation = DateTime.Now });
        var w = opt.Landscape ? A4H : A4W;
        var h = opt.Landscape ? A4W : A4H;
        var q = Quantities.Compute(project);
        var writer = new PageWriter(doc, w, h, opt.Title, project);

        if (opt.Sections.HasFlag(ReportSections.Header))
        {
            writer.Heading(project.Header.Name);
            writer.KeyValues(new()
            {
                [T("Mine / site")] = project.Header.Mine,
                [T("Location")] = project.Header.Location,
                [T("Bench")] = project.Header.Bench,
                [T("Blast number")] = project.Header.BlastNumber,
                [T("Client")] = project.Header.Client,
                [T("Designer")] = project.Header.Author,
                [T("Shot date")] = project.Header.ShotDate?.ToString("d") ?? "",
                [T("Description")] = project.Header.Description,
            });
        }
        if (opt.Sections.HasFlag(ReportSections.Plan))
        {
            writer.Heading(T("Plan"));
            var size = Math.Min(w - 2 * Margin, writer.Remaining - 10);
            if (size < 250) { writer.NewPage(); size = Math.Min(w - 2 * Margin, writer.Remaining - 10); }
            writer.Image((c, rect) => DrawPlan(c, rect, project, opt.PlanTimingLabels), size);
        }
        if (opt.Sections.HasFlag(ReportSections.Quantities))
        {
            writer.Heading(T("Quantities"));
            writer.KeyValues(new()
            {
                [T("Holes")] = $"{q.HoleCount} (+{q.DummyCount} dummy)",
                [T("Drilled")] = $"{q.DrilledMetres:0.0} m",
                [T("Average burden × spacing")] = $"{q.AverageBurden:0.00} × {q.AverageSpacing:0.00} m",
                [T("Volume")] = $"{q.Volume:0} m³",
                [T("Tonnes")] = $"{q.Tonnes:0} t",
                [T("Explosive")] = $"{q.ExplosiveKg:0.0} kg",
                [T("Powder factor")] = $"{q.PowderFactorKgM3:0.000} kg/m³  ({q.PowderFactorKgT:0.000} kg/t)",
                [T("Energy factor")] = $"{q.EnergyFactorMJt:0.00} MJ/t",
                [T("Max charge per window") + $" ({project.Vibration.WindowMs} ms)"] = $"{q.MaxInstantaneousCharge:0.0} kg",
                [T("Total cost")] = $"{q.TotalCost:0.00} {project.Costs.Currency} ({q.CostPerTonne:0.000}/t)",
            });
            writer.Table(new[] { T("Product"), T("Amount"), T("Unit"), T("Cost") }, q.Products.Select(p => new[] { p.Product.Name, p.Amount.ToString("0.0"), p.Unit, p.Cost.ToString("0.00") }), new[] { 0.5f, 0.2f, 0.1f, 0.2f });
        }
        if (opt.Sections.HasFlag(ReportSections.HoleTable))
        {
            writer.Heading(T("Holes"));
            writer.Table(new[] { T("Hole"), T("X"), T("Y"), T("Z"), T("Ang"), T("Brg"), T("Len"), T("Ø"), T("Type"), T("State"), T("kg"), T("Time") },
                project.Holes.OrderBy(h => h.FiringSequence ?? int.MaxValue).ThenBy(h => h.Label).Select(h => new[]
                {
                    h.Label, h.Collar.X.ToString("0.00"), h.Collar.Y.ToString("0.00"), h.Collar.Z.ToString("0.00"), h.Angle.ToString("0"), h.Bearing.ToString("0"),
                    h.EffectiveLength.ToString("0.0"), h.Diameter.ToString("0"), h.HoleType.ToString(), h.State.ToString(), Charging.ExplosiveMass(project, h).ToString("0.0"),
                    h.FiringTimeMs?.ToString("0") ?? ""
                }), new[] { 0.08f, 0.11f, 0.11f, 0.08f, 0.06f, 0.06f, 0.07f, 0.06f, 0.06f, 0.1f, 0.1f, 0.11f });
        }
        if (opt.Sections.HasFlag(ReportSections.Loading))
        {
            writer.Heading(T("Charging"));
            writer.Table(new[] { T("Hole"), T("Stemming"), T("Column (bottom → top)"), T("Primers"), T("kg") },
                project.Holes.Where(h => h.Decks.Count > 0).OrderBy(h => h.Label).Select(h => new[]
                {
                    h.Label, Charging.TopStemming(project, h).ToString("0.0"),
                    string.Join(" / ", h.Decks.OrderByDescending(d => d.Bottom).Select(d => $"{project.FindProduct(d.ProductId)?.Name} {d.Length:0.0}")),
                    Charging.Primers(h).Count().ToString(), Charging.ExplosiveMass(project, h).ToString("0.0")
                }), new[] { 0.1f, 0.12f, 0.56f, 0.1f, 0.12f });
        }
        if (opt.Sections.HasFlag(ReportSections.Timing))
        {
            writer.Heading(T("Timing"));
            var env = Timing.TimeEnvelope(project, project.Vibration.WindowMs);
            writer.KeyValues(new()
            {
                [T("Initiation points")] = project.Entities.OfType<InitiationPoint>().Count().ToString(),
                [T("Ties")] = project.Ties.Count().ToString(),
                [T("Firing duration")] = env.Count > 0 ? $"{env.Max(e => e.TimeMs) - env.Min(e => e.TimeMs):0} ms" : "-",
                [T("Max holes per window")] = env.Count > 0 ? env.Max(e => e.Holes).ToString() : "-",
                [T("Max charge per window")] = env.Count > 0 ? $"{env.Max(e => e.ChargeKg):0.0} kg" : "-",
            });
            if (env.Count > 0)
                writer.Image((c, rect) => DrawEnvelopeChart(c, rect, env, project.Vibration.WindowMs), 220);
        }
        if (opt.Sections.HasFlag(ReportSections.Vibration))
        {
            var monitors = project.Entities.OfType<PointEntity>().Where(p => p.Kind == PointKind.Monitoring).ToList();
            if (monitors.Count > 0)
            {
                writer.Heading(T("Vibration prediction"));
                writer.Table(new[] { T("Monitor"), T("Distance m"), T("Charge kg"), T("PPV mm/s"), T("Airblast dB") },
                    monitors.Select(m => { var p = Vibration.Predict(project, m.Position); return new[] { m.Label, p.DistanceM.ToString("0"), p.ChargeKg.ToString("0.0"), p.Ppv.ToString("0.00"), p.AirblastDb.ToString("0.0") }; }),
                    new[] { 0.3f, 0.15f, 0.15f, 0.2f, 0.2f });
            }
        }
        if (opt.Sections.HasFlag(ReportSections.Outcomes))
        {
            var o = project.Outcomes;
            writer.Heading(T("Blast outcomes"));
            writer.KeyValues(new()
            {
                [T("Fragmentation P50 / P80")] = $"{o.FragmentationP50?.ToString("0") ?? "-"} / {o.FragmentationP80?.ToString("0") ?? "-"} mm",
                [T("Measured PPV")] = o.MeasuredPpv?.ToString("0.00") ?? "-",
                [T("Measured airblast")] = o.MeasuredAirblast?.ToString("0.0") ?? "-",
                [T("Max flyrock")] = o.MaxFlyrock?.ToString("0") ?? "-",
                [T("Rating")] = o.Rating > 0 ? o.Rating + "/5" : "-",
                [T("Comments")] = o.Comments,
            });
        }
        writer.Finish();
        doc.Close();
    }

    /// <summary>Prints the plan at a fixed scale (1:N) or fit-to-page, with a title block.</summary>
    public static void WritePlanPdf(BlastProject project, Stream output, double? scaleDenominator, bool landscape, string title)
    {
        Timing.Solve(project);
        using var doc = SKDocument.CreatePdf(output);
        var w = landscape ? A4H : A4W;
        var h = landscape ? A4W : A4H;
        var c = doc.BeginPage(w, h);
        var rect = new SKRect(Margin, Margin, w - Margin, h - Margin - 50);
        DrawPlan(c, rect, project, project.View.TimingLabels, scaleDenominator);
        using var text = new SKPaint { IsAntialias = true, TextSize = 10, Color = SKColors.Black };
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, Color = SKColors.Black };
        c.DrawRect(Margin, h - Margin - 45, w - 2 * Margin, 45, stroke);
        text.TextSize = 13;
        c.DrawText(title, Margin + 8, h - Margin - 27, text);
        text.TextSize = 9;
        c.DrawText($"{project.Header.Mine}  {project.Header.Bench}  {project.Header.BlastNumber}", Margin + 8, h - Margin - 12, text);
        c.DrawText($"{T("Scale")} {(scaleDenominator is double s ? "1:" + s.ToString("0") : T("fit"))}   {DateTime.Now:g}   {project.Header.Author}", w / 2, h - Margin - 12, text);
        doc.EndPage();
        doc.Close();
    }

    public static void DrawPlan(SKCanvas c, SKRect rect, BlastProject project, TimingLabelMode timing, double? scaleDenominator = null)
    {
        var cam = new Camera { Width = rect.Width, Height = rect.Height, Pitch = 90 };
        var b = Bounds3.Empty;
        foreach (var e in project.Entities.Where(e => e is not SurfaceEntity)) b.Add(e.GetBounds());
        if (b.IsEmpty) foreach (var e in project.Entities) b.Add(e.GetBounds());
        cam.Fit(b, 0.05);
        // 1 pt = 1/72 inch = 0.3528 mm on paper.
        if (scaleDenominator is double s && s > 0) cam.Scale = 1000.0 / 0.3528 / s;
        c.Save();
        c.ClipRect(rect);
        c.Translate(rect.Left, rect.Top);
        var view = project.View;
        var savedTiming = view.TimingLabels;
        var savedSurfaceTime = view.ShowSurfaceTimeLabel;
        var savedHoleTime = view.ShowHoleTimeLabel;
        var savedInholeDelay = view.ShowInholeDelayLabel;
        var savedHoleSequence = view.ShowHoleSequenceLabel;
        try
        {
            view.TimingLabels = timing;
            view.ShowSurfaceTimeLabel = false;
            view.ShowHoleTimeLabel = false;
            view.ShowInholeDelayLabel = false;
            view.ShowHoleSequenceLabel = false;
            PlanRenderer.Render(c, cam, project, new SceneOptions { DrawSummaryBlock = false });
        }
        finally
        {
            view.TimingLabels = savedTiming;
            view.ShowSurfaceTimeLabel = savedSurfaceTime;
            view.ShowHoleTimeLabel = savedHoleTime;
            view.ShowInholeDelayLabel = savedInholeDelay;
            view.ShowHoleSequenceLabel = savedHoleSequence;
        }
        c.Restore();
        using var border = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Gray, StrokeWidth = 0.5f };
        c.DrawRect(rect, border);
    }

    public static void DrawEnvelopeChart(SKCanvas c, SKRect rect, List<Timing.EnvelopePoint> env, double window, bool dark = false)
    {
        var ink = dark ? new SKColor(0xFFC9CFD6) : SKColors.Black;
        using var axis = new SKPaint { Style = SKPaintStyle.Stroke, Color = ink, StrokeWidth = 0.8f, IsAntialias = true };
        using var bar = new SKPaint { Style = SKPaintStyle.Fill, Color = dark ? new SKColor(0xFF4C9BFF) : new SKColor(0xFF2563EB) };
        using var line = new SKPaint { Style = SKPaintStyle.Stroke, Color = dark ? new SKColor(0xFFFF6B6B) : new SKColor(0xFFDC2626), StrokeWidth = 1.2f, IsAntialias = true };
        using var text = new SKPaint { TextSize = dark ? 11 : 8, Color = ink, IsAntialias = true };
        var left = rect.Left + 35;
        var bottom = rect.Bottom - 18;
        var plotW = rect.Right - left - 35;
        var plotH = bottom - rect.Top - 10;
        c.DrawLine(left, bottom, left + plotW, bottom, axis);
        c.DrawLine(left, bottom, left, bottom - plotH, axis);
        if (env.Count == 0) return;
        var t0 = env.Min(e => e.TimeMs);
        var t1 = Math.Max(t0 + 1, env.Max(e => e.TimeMs) + window);
        var maxH = Math.Max(1, env.Max(e => e.Holes));
        var maxQ = Math.Max(1, env.Max(e => e.ChargeKg));
        foreach (var e in env)
        {
            var x = left + (float)((e.TimeMs - t0) / (t1 - t0)) * plotW;
            var hgt = (float)e.Holes / maxH * plotH;
            c.DrawRect(x, bottom - hgt, Math.Max(1, plotW / env.Count * 0.6f), hgt, bar);
        }
        using var path = new SKPath();
        for (int i = 0; i < env.Count; i++)
        {
            var x = left + (float)((env[i].TimeMs - t0) / (t1 - t0)) * plotW;
            var y = bottom - (float)(env[i].ChargeKg / maxQ) * plotH;
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        c.DrawPath(path, line);
        c.DrawText($"{t0:0} ms", left, bottom + 12, text);
        c.DrawText($"{t1:0} ms", left + plotW - 30, bottom + 12, text);
        c.DrawText($"{maxH} {T("holes")}", rect.Left, rect.Top + 8, text);
        text.Color = line.Color;
        c.DrawText($"{maxQ:0} kg", left + plotW + 3, rect.Top + 8, text);
    }

    /// <summary>Lays out flowing content over A4 pages.</summary>
    private sealed class PageWriter
    {
        private readonly SKDocument _doc;
        private readonly float _w, _h;
        private readonly string _title;
        private readonly BlastProject _project;
        private SKCanvas? _c;
        private float _y;
        private int _page;
        private readonly SKPaint _text = new() { IsAntialias = true, TextSize = 9, Color = SKColors.Black };
        private readonly SKPaint _bold = new() { IsAntialias = true, TextSize = 9, Color = SKColors.Black, FakeBoldText = true };
        private readonly SKPaint _line = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f, Color = SKColors.Gray };

        public PageWriter(SKDocument doc, float w, float h, string title, BlastProject project)
        {
            _doc = doc; _w = w; _h = h; _title = title; _project = project;
            NewPage();
        }

        public float Remaining => _h - Margin - 20 - _y;

        public void NewPage()
        {
            if (_c != null) _doc.EndPage();
            _c = _doc.BeginPage(_w, _h);
            _page++;
            _text.TextSize = 8;
            _text.Color = SKColors.Gray;
            _c.DrawText($"{_title} — {_project.Header.Name}", Margin, Margin - 15, _text);
            _c.DrawText($"{T("Page")} {_page}", _w - Margin - 40, _h - Margin + 15, _text);
            _c.DrawText(DateTime.Now.ToString("g"), Margin, _h - Margin + 15, _text);
            _text.TextSize = 9;
            _text.Color = SKColors.Black;
            _y = Margin;
        }

        private void Ensure(float height)
        {
            if (Remaining < height) NewPage();
        }

        public void Heading(string text)
        {
            Ensure(40);
            _bold.TextSize = 13;
            _y += 18;
            _c!.DrawText(text, Margin, _y, _bold);
            _y += 6;
            _c.DrawLine(Margin, _y, _w - Margin, _y, _line);
            _y += 10;
            _bold.TextSize = 9;
        }

        public void KeyValues(Dictionary<string, string> kv)
        {
            foreach (var (k, v) in kv)
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                Ensure(14);
                _y += 12;
                _c!.DrawText(k, Margin, _y, _bold);
                _c.DrawText(v, Margin + 170, _y, _text);
            }
            _y += 6;
        }

        public void Table(string[] headers, IEnumerable<string[]> rows, float[] widths)
        {
            var total = _w - 2 * Margin;
            void Header()
            {
                Ensure(30);
                _y += 12;
                float x = Margin;
                for (int i = 0; i < headers.Length; i++) { _c!.DrawText(headers[i], x, _y, _bold); x += widths[i] * total; }
                _y += 4;
                _c!.DrawLine(Margin, _y, _w - Margin, _y, _line);
            }
            Header();
            foreach (var r in rows)
            {
                if (Remaining < 14) { NewPage(); Header(); }
                _y += 11;
                float x = Margin;
                for (int i = 0; i < r.Length && i < widths.Length; i++)
                {
                    var cell = r[i] ?? "";
                    var maxW = widths[i] * total - 4;
                    while (cell.Length > 1 && _text.MeasureText(cell) > maxW) cell = cell[..^2] + "…";
                    _c!.DrawText(cell, x, _y, _text);
                    x += widths[i] * total;
                }
            }
            _y += 8;
        }

        public void Image(Action<SKCanvas, SKRect> draw, float height)
        {
            Ensure(height + 10);
            var rect = new SKRect(Margin, _y + 4, _w - Margin, _y + 4 + height);
            draw(_c!, rect);
            _y += height + 12;
        }

        public void Finish()
        {
            if (_c != null) _doc.EndPage();
            _c = null;
            _text.Dispose(); _bold.Dispose(); _line.Dispose();
        }
    }

    /// <summary>Renders the current view into a PNG (Edit ▸ Copy image, thumbnails).</summary>
    public static byte[] RenderPng(BlastProject project, Camera cam, int width, int height, SceneOptions? opt = null)
    {
        var c2 = cam.Clone();
        c2.Width = width;
        c2.Height = height;
        if (cam.Width > 0) c2.Scale = cam.Scale * Math.Min(width / cam.Width, height / cam.Height);
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        PlanRenderer.Render(surface.Canvas, c2, project, opt ?? new SceneOptions());
        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}
