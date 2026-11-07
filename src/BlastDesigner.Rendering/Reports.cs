using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using SkiaSharp;

namespace BlastDesigner.Rendering;

/// <summary>The arrows of the first movement on one sheet, as in their window.</summary>
public sealed class FirstMovementSheet
{
    public string Title { get; set; } = "";
    public bool Landscape { get; set; } = true;
    public bool HideGeometry { get; set; } = true;
    /// <summary>Which way the plan is turned, degrees.</summary>
    public double Yaw { get; set; }
    public required FirstMovementOverlay Arrows { get; init; }
}

/// <summary>The cross sections on sheets of their own: one per section line of the blast, or one along a line that was only picked.</summary>
public sealed class CrossSectionSheet
{
    public string Title { get; set; } = "";
    public bool Landscape { get; set; } = true;
    /// <summary>How far from the line objects are shown, m; null takes <see cref="Reports.SectionHalfWidth"/>.</summary>
    public double? HalfWidth { get; set; }
    /// <summary>A line that is not in the blast, in place of those that are.</summary>
    public (Vec3 A, Vec3 B)? Line { get; set; }
}

/// <summary>Builds reports and plan sheets (A4) as recorded pages, for the screen, a PDF file and the printer.</summary>
public static class Reports
{
    private static string T(string s) => PlanRenderer.Translate(s);

    private const float A4W = 595, A4H = 842;

    public static void WritePdf(BlastProject project, Stream output, ReportOptions opt)
    {
        Timing.Solve(project);
        using var report = Build(project, opt, new ReportDesign(), DateTime.Now);
        report.WritePdf(output);
    }

    /// <summary>The report of a project whose times are solved. It reads the project only, and <paramref name="now"/> is its date on every page.</summary>
    public static ReportDocument Build(BlastProject project, ReportOptions opt, ReportDesign design, DateTime now)
    {
        var w = opt.Landscape ? A4H : A4W;
        var h = opt.Landscape ? A4W : A4H;
        var q = Quantities.Compute(project);
        var layout = new ReportLayout(design, w, h);
        var fields = ReportLayout.Fields(project, opt.Title, now);
        var head = (opt.Title + " " + ReportLayout.FillOrNothing(opt.TitleAddition, fields)).Trim();
        var writer = new PageWriter(layout, w, h, head, now);
        void Begin(ReportSections section) { if (opt.NewPage.HasFlag(section)) writer.StartPage(); }

        if (opt.Sections.HasFlag(ReportSections.Header))
        {
            Begin(ReportSections.Header);
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
            Begin(ReportSections.Plan);
            writer.Heading(T("Plan"));
            var size = Math.Min(writer.Width, writer.Remaining - 10);
            if (size < 250) { writer.NewPage(); size = Math.Min(writer.Width, writer.Remaining - 10); }
            writer.Image((c, rect) => DrawPlan(c, rect, project, opt.PlanTimingLabels, design), size, plan: true);
        }
        if (opt.Sections.HasFlag(ReportSections.CrossSection))
        {
            var lines = SectionEnds(project);
            if (lines.Count > 0) Begin(ReportSections.CrossSection);
            // a page for each: the drawing fills it, so what follows goes on to the next
            foreach (var (a, b, caption, line) in lines)
            {
                writer.StartPage();
                writer.Heading($"{T("Cross section")} {caption}".Trim());
                writer.Image((c, rect) => DrawSection(c, rect, project, a, b, SectionHalfWidth(project), design.SectionOf(line)), writer.Remaining - 10, section: new PageSection(line, caption));
            }
        }
        if (opt.Sections.HasFlag(ReportSections.Quantities))
        {
            Begin(ReportSections.Quantities);
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
            Begin(ReportSections.HoleTable);
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
            Begin(ReportSections.Loading);
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
            Begin(ReportSections.Timing);
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
                Begin(ReportSections.Vibration);
                writer.Heading(T("Vibration prediction"));
                writer.Table(new[] { T("Monitor"), T("Distance m"), T("Charge kg"), T("PPV mm/s"), T("Airblast dB") },
                    monitors.Select(m => { var p = Vibration.Predict(project, m.Position); return new[] { m.Label, p.DistanceM.ToString("0"), p.ChargeKg.ToString("0.0"), p.Ppv.ToString("0.00"), p.AirblastDb.ToString("0.0") }; }),
                    new[] { 0.3f, 0.15f, 0.15f, 0.2f, 0.2f });
            }
        }
        if (opt.Sections.HasFlag(ReportSections.Outcomes))
        {
            Begin(ReportSections.Outcomes);
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
        return new ReportDocument(opt.Title, project.Header.Author, layout.Finish(writer.Finish(), fields));
    }

    /// <summary>Prints the plan at a fixed scale (1:N) or fit-to-page, with a title block.</summary>
    public static void WritePlanPdf(BlastProject project, Stream output, double? scaleDenominator, bool landscape, string title)
    {
        Timing.Solve(project);
        using var plan = BuildPlan(project, new PlanOptions { Title = title, Scale = scaleDenominator, Landscape = landscape }, new ReportDesign(), DateTime.Now);
        plan.WritePdf(output);
    }

    /// <summary>The plan on one sheet, of a project whose times are solved.</summary>
    public static ReportDocument BuildPlan(BlastProject project, PlanOptions opt, ReportDesign design, DateTime now) =>
        Sheet(project, ReportLayout.Fill(opt.Title, ReportLayout.Fields(project, "", now)), opt.Landscape, opt.Scale, design, now, (c, rect) => DrawPlan(c, rect, project, project.View.TimingLabels, design, opt.Scale));

    /// <summary>The arrows of the first movement on one sheet, of a project whose times are solved.</summary>
    public static ReportDocument BuildFirstMovement(BlastProject project, FirstMovementSheet opt, ReportDesign design, DateTime now) =>
        Sheet(project, opt.Title, opt.Landscape, null, design, now, (c, rect) =>
        {
            var hidden = Hidden(project, design.HiddenKinds);
            var unframed = Unframed(project, design.View, hidden);
            var cam = new Camera { Width = rect.Width, Height = rect.Height, Pitch = 90, Yaw = opt.Yaw };
            var b = Bounds3.Empty;
            foreach (var e in project.Entities.Where(e => !unframed.Contains(e.Id) && (opt.HideGeometry ? e is Hole or InitiationPoint : e is not SurfaceEntity))) b.Add(e.GetBounds());
            cam.Fit(b, 0.05);
            cam.Scale *= design.View.Zoom;
            Pan(cam, design.View);
            DrawScene(c, rect, project, cam, new SceneOptions { DrawSummaryBlock = false, HideGeometry = opt.HideGeometry, HideWireframes = true, HiddenEntities = hidden, ShowGrid = design.ShowGrid, Overlays = new IOverlay[] { opt.Arrows } });
        });

    /// <summary>The section lines of a blast, in the order of the blast.</summary>
    /// <remarks>A section goes along the first segment of its line, whatever else the line has.</remarks>
    public static List<PolylineEntity> SectionLines(BlastProject project) =>
        project.Entities.OfType<PolylineEntity>().Where(l => l.Kind == LineKind.CrossSection && l.Points.Count >= 2).ToList();

    /// <summary>The objects a section shows on each side of its line unless told otherwise, m.</summary>
    public static double SectionHalfWidth(BlastProject project) => Math.Max(2, project.Design.Spacing);

    /// <summary>The ends of the section lines, each with what tells it from the others: its label, or its number among several.</summary>
    private static List<(Vec3 A, Vec3 B, string Caption, Guid? Line)> SectionEnds(BlastProject project)
    {
        var lines = SectionLines(project);
        return lines.Select((l, i) => (l.Points[0], l.Points[1], l.Label.Length > 0 ? l.Label : lines.Count > 1 ? (i + 1).ToString() : "", (Guid?)l.Id)).ToList();
    }

    /// <summary>The sections on sheets, of a project whose times are solved.</summary>
    public static ReportDocument BuildCrossSections(BlastProject project, CrossSectionSheet opt, ReportDesign design, DateTime now)
    {
        var w = opt.Landscape ? A4H : A4W;
        var h = opt.Landscape ? A4W : A4H;
        var layout = new ReportLayout(design, w, h);
        var half = opt.HalfWidth ?? SectionHalfWidth(project);
        var lines = opt.Line is { } line ? new List<(Vec3 A, Vec3 B, string Caption, Guid? Line)> { (line.A, line.B, "", null) } : SectionEnds(project);
        var pages = new List<ReportPage>();
        for (int i = 0; i < lines.Count; i++)
        {
            var (a, b, caption, id) = lines[i];
            pages.Add(SheetPage(project, layout, i, $"{opt.Title} {caption}".Trim(), w, h, null, now, false, (c, rect) => DrawSection(c, rect, project, a, b, half, design.SectionOf(id)), new PageSection(id, caption)));
        }
        // no line yet: an empty sheet, not a document with no pages
        if (pages.Count == 0) pages.Add(SheetPage(project, layout, 0, opt.Title, w, h, null, now, false, (_, _) => { }));
        return new ReportDocument(opt.Title, project.Header.Author, layout.Finish(pages, ReportLayout.Fields(project, opt.Title, now)));
    }

    /// <summary>What the objects look like across a line, seen square on to it and fitted to the holes near it.</summary>
    public static void DrawSection(SKCanvas c, SKRect rect, BlastProject project, Vec3 a, Vec3 b, double halfWidth, SectionVisibility shown)
    {
        var hidden = Hidden(project, shown.HiddenKinds);
        var unframed = Unframed(project, shown.View, hidden);
        var mid = Vec3.Lerp(a, b, 0.5);
        var depth = project.Holes.Any() ? project.Holes.Average(h => h.Collar.Z - h.Length / 2) : mid.Z;
        var cam = new Camera { Width = rect.Width, Height = rect.Height, Pitch = 0, Yaw = GeoMath.Bearing(a, b) - 90, Target = mid.WithZ(depth) };
        var fit = Bounds3.Empty;
        foreach (var hole in project.Holes.Where(h => !unframed.Contains(h.Id)))
            foreach (var v in hole.GetVertices())
                if (Math.Abs(cam.Depth(v)) <= halfWidth) fit.Add(v);
        if (fit.IsEmpty) { fit.Add(a.WithZ(depth)); fit.Add(b.WithZ(depth)); }
        cam.Fit(fit, 0.1);
        // the fit goes to the middle of what it found: the slice stays on the line
        cam.MoveTargetToDepth(cam.Depth(mid.WithZ(cam.Target.Z)));
        cam.Scale *= shown.View.Zoom;
        Pan(cam, shown.View);
        DrawScene(c, rect, project, cam, new SceneOptions { DrawSummaryBlock = false, HideWireframes = true, HiddenEntities = hidden, ShowGrid = shown.ShowGrid, SliceHalfWidth = halfWidth });
    }

    /// <summary>One sheet: a drawing over the title block.</summary>
    private static ReportDocument Sheet(BlastProject project, string title, bool landscape, double? scaleDenominator, ReportDesign design, DateTime now, Action<SKCanvas, SKRect> draw)
    {
        var w = landscape ? A4H : A4W;
        var h = landscape ? A4W : A4H;
        var layout = new ReportLayout(design, w, h);
        var pages = new List<ReportPage> { SheetPage(project, layout, 0, title, w, h, scaleDenominator, now, true, draw) };
        return new ReportDocument(title, project.Header.Author, layout.Finish(pages, ReportLayout.Fields(project, title, now)));
    }

    private static ReportPage SheetPage(BlastProject project, ReportLayout layout, int index, string title, float w, float h, double? scaleDenominator, DateTime now, bool hasPlan, Action<SKCanvas, SKRect> draw, PageSection? section = null)
    {
        var area = layout.Area(index);
        using var recorder = new SKPictureRecorder();
        var c = recorder.BeginRecording(new SKRect(0, 0, w, h));
        draw(c, new SKRect(area.Left, area.Top, area.Right, area.Bottom - 50));
        using var text = new SKPaint { IsAntialias = true, TextSize = 10, Color = SKColors.Black };
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, Color = SKColors.Black };
        c.DrawRect(area.Left, area.Bottom - 45, area.Width, 45, stroke);
        text.TextSize = 13;
        c.DrawText(title, area.Left + 8, area.Bottom - 27, text);
        text.TextSize = 9;
        c.DrawText($"{project.Header.Mine}  {project.Header.Bench}  {project.Header.BlastNumber}", area.Left + 8, area.Bottom - 12, text);
        c.DrawText($"{T("Scale")} {(scaleDenominator is double s ? "1:" + s.ToString("0") : T("fit"))}   {now:g}   {project.Header.Author}", w / 2, area.Bottom - 12, text);
        return new ReportPage(recorder.EndRecording(), w, h, hasPlan, section);
    }

    /// <summary>The objects of some kinds: those the design leaves out of the drawings.</summary>
    private static HashSet<Guid> Hidden(BlastProject project, List<string> kinds) =>
        project.Entities.Where(e => kinds.Contains(e.KindName)).Select(e => e.Id).ToHashSet();

    /// <summary>The objects a drawing is not fitted to: those it leaves out, or those it left out when the frame was locked.</summary>
    private static HashSet<Guid> Unframed(BlastProject project, DrawingView view, HashSet<Guid> hidden) =>
        view.Locked ? Hidden(project, view.LockedHiddenKinds) : hidden;

    private static void Pan(Camera cam, DrawingView view) => cam.Pan(-view.PanX * cam.Width, view.PanY * cam.Height);

    public static void DrawPlan(SKCanvas c, SKRect rect, BlastProject project, TimingLabelMode timing, ReportDesign design, double? scaleDenominator = null)
    {
        var hidden = Hidden(project, design.HiddenKinds);
        var unframed = Unframed(project, design.View, hidden);
        var cam = new Camera { Width = rect.Width, Height = rect.Height, Pitch = 90 };
        var b = Bounds3.Empty;
        foreach (var e in project.Entities.Where(e => e is not SurfaceEntity && !unframed.Contains(e.Id))) b.Add(e.GetBounds());
        if (b.IsEmpty) foreach (var e in project.Entities.Where(e => !unframed.Contains(e.Id))) b.Add(e.GetBounds());
        cam.Fit(b, 0.05);
        // 1 pt = 1/72 inch = 0.3528 mm on paper. A scale written on the sheet stays true: the zoom is for a fitted drawing.
        if (scaleDenominator is double s && s > 0) cam.Scale = 1000.0 / 0.3528 / s;
        else cam.Scale *= design.View.Zoom;
        Pan(cam, design.View);
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
            DrawScene(c, rect, project, cam, new SceneOptions { DrawSummaryBlock = false, HideWireframes = true, HiddenEntities = hidden, ShowGrid = design.ShowGrid });
        }
        finally
        {
            view.TimingLabels = savedTiming;
            view.ShowSurfaceTimeLabel = savedSurfaceTime;
            view.ShowHoleTimeLabel = savedHoleTime;
            view.ShowInholeDelayLabel = savedInholeDelay;
            view.ShowHoleSequenceLabel = savedHoleSequence;
        }
    }

    private static void DrawScene(SKCanvas c, SKRect rect, BlastProject project, Camera cam, SceneOptions options)
    {
        c.Save();
        c.ClipRect(rect);
        c.Translate(rect.Left, rect.Top);
        PlanRenderer.Render(c, cam, project, options);
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
        private readonly ReportLayout _layout;
        private readonly float _w, _h;
        private readonly string _head;
        private readonly DateTime _now;
        private readonly List<ReportPage> _pages = new();
        private SKPictureRecorder? _recorder;
        private SKCanvas? _c;
        private SKRect _area;
        private float _y;
        private int _page;
        private bool _hasPlan;
        private PageSection? _section;
        private readonly SKPaint _text = new() { IsAntialias = true, TextSize = 9, Color = SKColors.Black };
        private readonly SKPaint _bold = new() { IsAntialias = true, TextSize = 9, Color = SKColors.Black, FakeBoldText = true };
        private readonly SKPaint _line = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f, Color = SKColors.Gray };

        public PageWriter(ReportLayout layout, float w, float h, string head, DateTime now)
        {
            _layout = layout; _w = w; _h = h; _head = head; _now = now;
            NewPage();
        }

        public float Remaining => _area.Bottom - 20 - _y;
        public float Width => _area.Width;

        private void EndPage()
        {
            if (_recorder == null) return;
            _pages.Add(new ReportPage(_recorder.EndRecording(), _w, _h, _hasPlan, _section));
            _hasPlan = false;
            _section = null;
            _recorder.Dispose();
            _recorder = null;
        }

        /// <summary>A new page for what follows, unless the page is still empty: no blank sheets.</summary>
        public void StartPage()
        {
            if (_y > _area.Top) NewPage();
        }

        public void NewPage()
        {
            EndPage();
            _area = _layout.Area(_page);
            _recorder = new SKPictureRecorder();
            _c = _recorder.BeginRecording(new SKRect(0, 0, _w, _h));
            _page++;
            _text.TextSize = 8;
            _text.Color = SKColors.Gray;
            _c.DrawText(_head, _area.Left, _area.Top - 15, _text);
            var foot = _layout.FootLine(_page - 1);
            var footer = _layout.Footer;
            if (footer.HasNumber(_page)) _c.DrawText($"{T("Page")} {_page}", _area.Right - 40, foot, _text);
            if (footer.ShowDate) _c.DrawText(_now.ToString(footer.DateWithTime ? "g" : "d"), _area.Left, foot, _text);
            _text.TextSize = 9;
            _text.Color = SKColors.Black;
            _y = _area.Top;
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
            _c!.DrawText(text, _area.Left, _y, _bold);
            _y += 6;
            _c.DrawLine(_area.Left, _y, _area.Right, _y, _line);
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
                _c!.DrawText(k, _area.Left, _y, _bold);
                _c.DrawText(v, _area.Left + 170, _y, _text);
            }
            _y += 6;
        }

        public void Table(string[] headers, IEnumerable<string[]> rows, float[] widths)
        {
            var total = _area.Width;
            void Header()
            {
                Ensure(30);
                _y += 12;
                float x = _area.Left;
                for (int i = 0; i < headers.Length; i++) { _c!.DrawText(headers[i], x, _y, _bold); x += widths[i] * total; }
                _y += 4;
                _c!.DrawLine(_area.Left, _y, _area.Right, _y, _line);
            }
            Header();
            foreach (var r in rows)
            {
                if (Remaining < 14) { NewPage(); Header(); }
                _y += 11;
                float x = _area.Left;
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

        public void Image(Action<SKCanvas, SKRect> draw, float height, bool plan = false, PageSection? section = null)
        {
            Ensure(height + 10);
            _hasPlan |= plan;
            _section ??= section;
            var rect = new SKRect(_area.Left, _y + 4, _area.Right, _y + 4 + height);
            draw(_c!, rect);
            _y += height + 12;
        }

        public List<ReportPage> Finish()
        {
            EndPage();
            _c = null;
            _text.Dispose(); _bold.Dispose(); _line.Dispose();
            return _pages;
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
