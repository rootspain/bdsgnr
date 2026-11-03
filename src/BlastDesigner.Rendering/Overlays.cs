using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using SkiaSharp;

namespace BlastDesigner.Rendering;

/// <summary>Rubber-band geometry drawn by interactive tools.</summary>
public sealed class PreviewOverlay : IOverlay
{
    public List<(IReadOnlyList<Vec3> Points, bool Closed, SKColor Color)> Lines { get; } = new();
    public List<(Vec3 Point, SKColor Color)> Points { get; } = new();
    public List<(Vec3 Point, string Text)> Labels { get; } = new();
    /// <summary>Square markers for holes that a tool is about to create.</summary>
    public List<(Vec3 Point, SKColor Color)> Markers { get; } = new();
    public (float X0, float Y0, float X1, float Y1)? ScreenRect { get; set; }
    public Vec3? Snap { get; set; }
    /// <summary>Objects being dragged: the scene draws them shifted by <see cref="MoveOffset"/> until the move is applied.</summary>
    public IReadOnlyCollection<Guid>? Moving { get; set; }
    public Vec3 MoveOffset { get; set; }

    public void Clear()
    {
        Moving = null;
        MoveOffset = Vec3.Zero;
        Lines.Clear();
        Points.Clear();
        Labels.Clear();
        Markers.Clear();
        ScreenRect = null;
        Snap = null;
    }

    /// <summary>
    /// A copy for one frame. The frame is drawn on the render thread while tools keep changing the lists on the UI thread;
    /// drawing the live lists failed with "collection was modified".
    /// </summary>
    public PreviewOverlay Snapshot()
    {
        var copy = new PreviewOverlay { ScreenRect = ScreenRect, Snap = Snap, Moving = Moving, MoveOffset = MoveOffset };
        foreach (var (pts, closed, col) in Lines) copy.Lines.Add((pts.ToArray(), closed, col));
        copy.Points.AddRange(Points);
        copy.Labels.AddRange(Labels);
        copy.Markers.AddRange(Markers);
        return copy;
    }

    public void Draw(SKCanvas c, Camera cam, BlastProject project)
    {
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, PathEffect = SKPathEffect.CreateDash(new[] { 5f, 3f }, 0) };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var text = new SKPaint { IsAntialias = true, TextSize = 12, Color = PlanRenderer.Adapt(new SKColor(0xFF1D4ED8)) };
        foreach (var (pts, closed, col) in Lines)
        {
            stroke.Color = PlanRenderer.Adapt(col);
            PlanRenderer.DrawPolyline(c, cam, pts, closed, stroke);
        }
        foreach (var (p, col) in Points)
        {
            fill.Color = PlanRenderer.Adapt(col);
            var (x, y) = cam.ToScreen(p);
            c.DrawCircle(x, y, 4, fill);
        }
        foreach (var (p, col) in Markers)
        {
            var (x, y) = cam.ToScreen(p);
            fill.Color = PlanRenderer.Adapt(col);
            c.DrawRect(x - 4.5f, y - 4.5f, 9, 9, fill);
            using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = PlanRenderer.DarkMode ? new SKColor(0xFF101215) : SKColors.White };
            c.DrawRect(x - 4.5f, y - 4.5f, 9, 9, edge);
        }
        foreach (var (p, t) in Labels)
        {
            if (string.IsNullOrEmpty(t)) continue;
            var (x, y) = cam.ToScreen(p);
            using var bg = new SKPaint { Color = PlanRenderer.DarkMode ? new SKColor(0xEE20242A) : new SKColor(0xDDFFFFFF) };
            var w = text.MeasureText(t);
            c.DrawRect(x + 8, y - 14, w + 6, 17, bg);
            c.DrawText(t, x + 11, y - 1, text);
        }
        if (ScreenRect is var (x0, y0, x1, y1))
        {
            fill.Color = new SKColor(0x302563EB);
            var r = new SKRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
            c.DrawRect(r, fill);
            stroke.Color = new SKColor(0xFF2563EB);
            c.DrawRect(r, stroke);
        }
        if (Snap is Vec3 s)
        {
            var (x, y) = cam.ToScreen(s);
            stroke.PathEffect = null;
            stroke.Color = new SKColor(0xFF16A34A);
            stroke.StrokeWidth = 2;
            c.DrawRect(x - 6, y - 6, 12, 12, stroke);
        }
    }
}

/// <summary>Isochrones (firing-time contours) and colour-by-time of holes (Visualize timing).</summary>
public sealed class TimingContourOverlay : IOverlay
{
    public double IntervalMs { get; set; } = 25;
    public bool ColourHoles { get; set; } = true;
    public bool Contours { get; set; } = true;
    /// <summary>Current animation time; holes fired by then are drawn as larger circles.</summary>
    public double? AnimationTimeMs { get; set; }

    // The contours of the last holes and interval: rebuilt only when a hole moves or changes time, not on every frame
    // of panning or of the animation. Drawn on the render thread, so guarded.
    private readonly object _lock = new();
    private int _key;
    private List<PolylineEntity> _contours = new();

    public void Draw(SKCanvas c, Camera cam, BlastProject project)
    {
        var holes = project.Holes.Where(h => h.FiringTimeMs.HasValue && !h.IsDummy).ToList();
        if (holes.Count == 0) return;
        var min = holes.Min(h => h.FiringTimeMs!.Value);
        var max = holes.Max(h => h.FiringTimeMs!.Value);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f };
        using var text = new SKPaint { IsAntialias = true, TextSize = 10 };
        if (ColourHoles)
            foreach (var h in holes)
            {
                fill.Color = ColorRamp.Heat(max > min ? (h.FiringTimeMs!.Value - min) / (max - min) : 0).WithAlpha(200);
                var (x, y) = cam.ToScreen(h.Collar);
                var r = (float)Math.Max(5, h.RadiusM * cam.Scale * 1.5);
                if (AnimationTimeMs is double at)
                {
                    if (h.FiringTimeMs!.Value > at) continue;
                    r = PlanRenderer.FiredRadius(r);
                }
                c.DrawCircle(x, y, r, fill);
            }
        if (!Contours || holes.Count < 3 || IntervalMs <= 0) return;
        var key = new HashCode();
        key.Add(IntervalMs); key.Add(project.Design.Burden); key.Add(project.Design.Spacing);
        foreach (var h in holes) { key.Add(h.Collar); key.Add(h.FiringTimeMs); }
        List<PolylineEntity> contours;
        lock (_lock)
        {
            if (_key != key.ToHashCode() || _contours.Count == 0)
            {
                var tin = Surfaces.Triangulate(holes.Select(h => h.Collar.WithZ(h.FiringTimeMs!.Value)), "time", MaxEdge(project, holes));
                _contours = Surfaces.Contours(tin, IntervalMs);
                _key = key.ToHashCode();
            }
            contours = _contours;
        }
        foreach (var line in contours)
        {
            var t = line.Points[0].Z;
            stroke.Color = ColorRamp.Heat(max > min ? (t - min) / (max - min) : 0);
            var pts = line.Points.Select(p => p.WithZ(holes[0].Collar.Z)).ToList();
            PlanRenderer.DrawPolyline(c, cam, pts, false, stroke);
            var (x, y) = cam.ToScreen(pts[pts.Count / 2]);
            text.Color = stroke.Color;
            c.DrawText($"{t:0}", x + 2, y - 2, text);
        }
    }

    /// <summary>
    /// Longest triangle edge of the timing contours: long enough to join neighbouring rows, short enough to leave a
    /// notch in the blast outline empty. Three times the design burden or spacing, or more when the rows on the ground
    /// are farther apart: rows 14 m apart with a 4 × 4.5 m design lost the contours between them, and the first row
    /// showed none.
    /// </summary>
    public static double MaxEdge(BlastProject p, IReadOnlyList<Hole> holes) => Math.Max(Math.Max(p.Design.Burden, p.Design.Spacing) * 3, 1.5 * RowGap(holes));

    /// <summary>
    /// Distance between rows on the ground, 90th percentile over the holes: from each hole to the nearest hole off the
    /// line to its nearest neighbour (more than 45° from it), which lies in the next row. 0 for fewer than three holes.
    /// </summary>
    public static double RowGap(IReadOnlyList<Hole> holes)
    {
        if (holes.Count < 3) return 0;
        var gaps = new List<double>();
        foreach (var h in holes)
        {
            Hole? nearest = null;
            double best = double.MaxValue;
            foreach (var o in holes)
            {
                if (o == h) continue;
                var d = Vec3.Distance2D(h.Collar, o.Collar);
                if (d > 1e-6 && d < best) { best = d; nearest = o; }
            }
            if (nearest == null) continue;
            double ax = nearest.Collar.X - h.Collar.X, ay = nearest.Collar.Y - h.Collar.Y, al = Math.Sqrt(ax * ax + ay * ay);
            double across = double.MaxValue;
            foreach (var o in holes)
            {
                double bx = o.Collar.X - h.Collar.X, by = o.Collar.Y - h.Collar.Y, bl = Math.Sqrt(bx * bx + by * by);
                if (bl < 1e-6) continue;
                // |cos| below cos 45°: off the row line in either direction
                if (Math.Abs(ax * bx + ay * by) / (al * bl) < Math.Sqrt(0.5) && bl < across) across = bl;
            }
            if (across < double.MaxValue) gaps.Add(across);
        }
        if (gaps.Count == 0) return 0;
        gaps.Sort();
        return gaps[(int)Math.Min(gaps.Count - 1, Math.Floor(0.9 * gaps.Count))];
    }
}

/// <summary>
/// First movement: one arrow per valid triangle of the timing triangulation, at its centroid, pointing down
/// the time gradient; fill colour = hue of the screen direction, black outline; size in metres; big 7-point arrows when
/// they cover less than 30 % of the triangulated area. Clipped to closed blast boundaries.
/// </summary>
public sealed class FirstMovementOverlay : IMovePreviewOverlay
{
    public enum ArrowShape { Arrow, Triangle, Auto }

    public double ArrowSize { get; set; } = FirstMovement.DefaultArrowSize;
    /// <summary>Arrow: always the 7-point arrow; Triangle: always the small triangle; Auto: arrow below 30 % coverage).</summary>
    public ArrowShape Shape { get; set; } = ArrowShape.Arrow;
    /// <summary>Draw in the main plan's style (line arrows) instead of the filled arrows.</summary>
    public bool PlanStyle { get; set; }

    List<(Vec3, double)>? _inputs;
    TimingDelaunay? _delaunay;
    List<FirstMovement.Arrow> _arrows = new();

    /// <summary>
    /// Current triangulation (rebuilt when hole positions or times change), with the <paramref name="moved"/> holes
    /// shifted by <paramref name="offset"/> while they are dragged.
    /// </summary>
    public TimingDelaunay Delaunay(BlastProject project, ISet<Guid>? moved = null, Vec3 offset = default)
    {
        var inputs = FirstMovement.Inputs(project, moved, offset);
        if (_delaunay == null || _inputs == null || !inputs.SequenceEqual(_inputs))
        {
            _inputs = inputs;
            _delaunay = TimingDelaunay.Build(inputs);
            _arrows = FirstMovement.Arrows(_delaunay);
        }
        return _delaunay;
    }

    public IReadOnlyList<FirstMovement.Arrow> Arrows(BlastProject project) { Delaunay(project); return _arrows; }

    public void Draw(SKCanvas c, Camera cam, BlastProject project) => Draw(c, cam, project, null, default);

    public void Draw(SKCanvas c, Camera cam, BlastProject project, ISet<Guid>? moved, Vec3 offset)
    {
        var d = Delaunay(project, moved, offset);
        if (_arrows.Count == 0) return;
        var large = Shape == ArrowShape.Arrow || (Shape == ArrowShape.Auto && FirstMovement.UseLargeArrows(d, ArrowSize));
        var bounds = project.Entities.OfType<PolylineEntity>().Where(p => p.Closed && p.Kind == LineKind.Boundary && p.Points.Count > 2).ToList();
        var saved = c.Save();
        if (bounds.Count > 0)
        {
            using var clip = new SKPath();
            foreach (var b in bounds)
            {
                var (x0, y0) = cam.ToScreen(b.Points[0]);
                clip.MoveTo(x0, y0);
                for (int i = 1; i < b.Points.Count; i++) { var (x, y) = cam.ToScreen(b.Points[i]); clip.LineTo(x, y); }
                clip.Close();
            }
            c.ClipPath(clip, antialias: true);
        }
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.Black };
        if (PlanStyle)
        {
            // Main plan: the arrows keep Blast Designer's look (orange line with a head, 0.8 × burden long).
            outline.StrokeWidth = 2;
            outline.Color = new SKColor(0xFFEA580C);
            var length = Math.Max(project.Design.Burden, 2) * 0.8;
            foreach (var a in _arrows)
                PlanRenderer.DrawArrow(c, cam, a.Position, a.Position + new Vec3(a.DirX, a.DirY, 0) * length, outline, fill, 7);
            c.RestoreToCount(saved);
            return;
        }
        foreach (var a in _arrows)
        {
            var (sx, sy) = cam.ToScreen(a.Position);
            var (ex, ey) = cam.ToScreen(a.Position + new Vec3(a.DirX, a.DirY, 0));
            double dx = ex - sx, dy = ey - sy, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) continue;
            dx /= len; dy /= len;
            var size = ArrowSize * cam.Scale;
            var pts = large ? FirstMovement.LargeArrow(sx, sy, dx * size, dy * size) : FirstMovement.SmallArrow(sx, sy, dx * size, dy * size);
            // The hue comes from the screen direction with y up.
            var (r, g, bl) = FirstMovement.ColorFromHsv(Math.Atan2(-dy, dx) * 180 / Math.PI, 1, 1);
            fill.Color = new SKColor(r, g, bl);
            using var path = new SKPath();
            path.MoveTo((float)pts[0].X, (float)pts[0].Y);
            for (int i = 1; i < pts.Length; i++) path.LineTo((float)pts[i].X, (float)pts[i].Y);
            path.Close();
            c.DrawPath(path, fill);
            c.DrawPath(path, outline);
        }
        c.RestoreToCount(saved);
    }
}

/// <summary>Per-hole arrows: direction of initiation or first movement, coloured by burden relief.</summary>
public sealed class TimingVectorOverlay : IOverlay
{
    public enum Mode { InitiationAngle, FirstMovement, BurdenRelief }
    public Mode Show { get; set; } = Mode.FirstMovement;
    public double NeighbourRadius { get; set; } = 8;
    public double ReliefLow { get; set; } = 4;
    public double ReliefHigh { get; set; } = 15;

    public void Draw(SKCanvas c, Camera cam, BlastProject project)
    {
        var res = Timing.Analyse(project, NeighbourRadius, project.Vibration.WindowMs);
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var text = new SKPaint { IsAntialias = true, TextSize = 9, Color = PlanRenderer.Ink };
        var len = Math.Max(project.Design.Burden, 2) * 0.8;
        foreach (var r in res)
        {
            if (Show == Mode.BurdenRelief)
            {
                if (r.ReliefMsPerM is not double rel) continue;
                fill.Color = rel < ReliefLow ? new SKColor(0xC0DC2626) : rel > ReliefHigh ? new SKColor(0xC02563EB) : new SKColor(0xC016A34A);
                var (x, y) = cam.ToScreen(r.Hole.Collar);
                c.DrawCircle(x, y, (float)Math.Max(7, r.Hole.RadiusM * cam.Scale * 2), fill);
                c.DrawText($"{rel:0.0}", x + 8, y + 12, text);
                continue;
            }
            var bearing = Show == Mode.InitiationAngle ? r.InitiationBearing : r.MovementBearing;
            if (bearing is not double b) continue;
            var dir = GeoMath.HoleDirection(b, 90);
            stroke.Color = Show == Mode.InitiationAngle ? PlanRenderer.Adapt(new SKColor(0xFF7C3AED)) : new SKColor(0xFFEA580C);
            PlanRenderer.DrawArrow(c, cam, r.Hole.Collar, r.Hole.Collar + dir * len, stroke, fill, 7);
        }
    }
}

/// <summary>Colour map of predicted PPV or airblast around the blast.</summary>
public sealed class VibrationMapOverlay : IOverlay
{
    private double[,]? _grid;
    private Bounds3 _area;

    public bool Airblast { get; set; }
    public int Resolution { get; set; } = 60;
    public double Margin { get; set; } = 300;

    public void Recalculate(BlastProject project)
    {
        var b = Bounds3.Empty;
        foreach (var h in project.Holes) b.Add(h.Collar);
        if (b.IsEmpty) { _grid = null; return; }
        b.Add(b.Min - new Vec3(Margin, Margin, 0));
        b.Add(b.Max + new Vec3(Margin, Margin, 0));
        _area = b;
        _grid = Vibration.Map(project, b, Resolution, Resolution, Airblast);
    }

    public void Draw(SKCanvas c, Camera cam, BlastProject project)
    {
        if (_grid == null) Recalculate(project);
        if (_grid == null) return;
        var limit = Airblast ? project.Vibration.AirblastLimitDb : project.Vibration.PpvLimit;
        int nx = _grid.GetLength(0), ny = _grid.GetLength(1);
        var dx = (_area.Max.X - _area.Min.X) / nx;
        var dy = (_area.Max.Y - _area.Min.Y) / ny;
        using var fill = new SKPaint { Style = SKPaintStyle.Fill };
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                var v = _grid[i, j];
                var t = Airblast ? (v - (limit - 30)) / 40 : Math.Log10(Math.Max(v, 0.1) / (limit / 10)) / 2;
                fill.Color = ColorRamp.Heat(t).WithAlpha(90);
                var p0 = new Vec3(_area.Min.X + i * dx, _area.Min.Y + j * dy, _area.Center.Z);
                var corners = new[] { p0, p0 + new Vec3(dx, 0, 0), p0 + new Vec3(dx, dy, 0), p0 + new Vec3(0, dy, 0) };
                PlanRenderer.DrawPolygonFill(c, cam, corners, fill);
            }
        // Limit contour.
        var tin = new SurfaceEntity();
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
                tin.Vertices.Add(new Vec3(_area.Min.X + (i + 0.5) * dx, _area.Min.Y + (j + 0.5) * dy, _grid[i, j]));
        for (int i = 0; i < nx - 1; i++)
            for (int j = 0; j < ny - 1; j++)
            {
                int a = i * ny + j, b = (i + 1) * ny + j, cc = (i + 1) * ny + j + 1, d = i * ny + j + 1;
                tin.Triangles.AddRange(new[] { a, b, cc, a, cc, d });
            }
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = new SKColor(0xFFDC2626) };
        foreach (var l in Surfaces.Contours(tin, 0, limit))
            PlanRenderer.DrawPolyline(c, cam, l.Points.Select(p => p.WithZ(_area.Center.Z)).ToList(), false, stroke);
    }
}

/// <summary>Highlights a set of entities with a coloured halo (find results, exceptions).</summary>
public sealed class HighlightOverlay : IOverlay
{
    public HashSet<Guid> Ids { get; } = new();
    public SKColor Color { get; set; } = new(0xFFF97316);

    public void Draw(SKCanvas c, Camera cam, BlastProject project)
    {
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = Color };
        if (Ids.Count == 0) return;
        // one pass over the project, not a search per highlighted object (a find can highlight hundreds)
        foreach (var e in project.Entities)
            if (Ids.Contains(e.Id))
                foreach (var v in e.GetVertices().Take(1))
                {
                    var (x, y) = cam.ToScreen(v);
                    c.DrawCircle(x, y, 14, stroke);
                }
    }
}

/// <summary>
/// Live preview of the timing line window: the line as it will be drawn, its start (A) and end (B) in the order it is
/// walked, and the planned time of every hole it reaches (colour, label, isochrones). Nothing is changed until applied.
/// </summary>
public sealed class TimingLinePreviewOverlay : IOverlay
{
    public TimingLineEntity Line { get; set; } = new();
    public IReadOnlyDictionary<Guid, double> Times { get; set; } = new Dictionary<Guid, double>();
    public double IntervalMs { get; set; } = 25;

    public void Draw(SKCanvas c, Camera cam, BlastProject project)
    {
        using var text = new SKPaint { IsAntialias = true, TextSize = 11, Color = PlanRenderer.Ink };
        var holes = project.Holes.Where(h => Times.ContainsKey(h.Id)).ToList();
        if (holes.Count > 0)
        {
            var min = holes.Min(h => Times[h.Id]);
            var max = holes.Max(h => Times[h.Id]);
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f };
            if (holes.Count >= 3 && IntervalMs > 0)
            {
                var tin = Surfaces.Triangulate(holes.Select(h => h.Collar.WithZ(Times[h.Id])), "time", Math.Max(project.Design.Burden, project.Design.Spacing) * 3);
                foreach (var line in Surfaces.Contours(tin, IntervalMs))
                {
                    stroke.Color = ColorRamp.Heat(max > min ? (line.Points[0].Z - min) / (max - min) : 0).WithAlpha(170);
                    PlanRenderer.DrawPolyline(c, cam, line.Points.Select(p => p.WithZ(holes[0].Collar.Z)).ToList(), false, stroke);
                }
            }
            text.TextSize = 10;
            foreach (var h in holes)
            {
                var t = Times[h.Id];
                fill.Color = ColorRamp.Heat(max > min ? (t - min) / (max - min) : 0).WithAlpha(210);
                var (x, y) = cam.ToScreen(h.Collar);
                c.DrawCircle(x, y, (float)Math.Max(5, h.RadiusM * cam.Scale * 1.5), fill);
                c.DrawText($"{t:0}", x + 7, y - 6, text);
            }
            text.TextSize = 11;
        }
        if (Line.Points.Count < 2) return;
        PlanRenderer.DrawTimingLine(c, cam, Line, null, text);
        // A and B in the order the line is walked, so the direction switch is visible on the plan.
        var walked = Line.OrderedPoints;
        void Mark(Vec3 p, string label)
        {
            var (x, y) = cam.ToScreen(p);
            using var badge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0xFF2563EB) };
            c.DrawCircle(x, y - 16, 9, badge);
            using var white = new SKPaint { IsAntialias = true, TextSize = 11, Color = SKColors.White, FakeBoldText = true };
            c.DrawText(label, x - white.MeasureText(label) / 2, y - 12, white);
        }
        Mark(walked[0], "A");
        Mark(walked[^1], "B");
    }
}
