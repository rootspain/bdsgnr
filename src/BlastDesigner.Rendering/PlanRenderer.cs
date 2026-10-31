using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using SkiaSharp;

namespace BlastDesigner.Rendering;

/// <summary>Something drawn on top of the scene (tool previews, analysis results).</summary>
public interface IOverlay
{
    void Draw(SKCanvas canvas, Camera camera, BlastProject project);
}

public sealed class SceneOptions
{
    public ISet<Guid> Selection { get; init; } = new HashSet<Guid>();
    public SKColor Background { get; init; } = SKColors.White;
    public SKColor SelectionColor { get; init; } = new(0xFFFF00FF);
    public float CollarPixels { get; init; } = 6;
    public bool DrawSummaryBlock { get; init; } = true;
    /// <summary>Allows the hole type legend (still subject to the project's "Hole legend" switch).</summary>
    public bool DrawHoleLegend { get; init; } = true;
    /// <summary>Allows the tie (connector) legend (still subject to the project's "Tie legend" switch).</summary>
    public bool DrawTieLegend { get; init; } = true;
    /// <summary>Key to the elevation colours, drawn when surfaces are filled by elevation (<see cref="SurfaceColors"/> null).</summary>
    public bool DrawElevationScale { get; init; }
    public bool DrawNorthArrow { get; init; } = true;
    public bool DrawScaleBar { get; init; } = true;
    public IReadOnlyList<IOverlay> Overlays { get; init; } = Array.Empty<IOverlay>();
    /// <summary>When set, only holes fired at or before this time are drawn as fired (timing animation).</summary>
    public double? AnimationTimeMs { get; init; }
    /// <summary>Holes in this set are hidden (visibility manager / isolate).</summary>
    public ISet<Guid>? HiddenEntities { get; init; }
    /// <summary>Optional clipping band (section and slice views): only geometry within this distance of the camera target plane is drawn.</summary>
    public double? SliceHalfWidth { get; init; }
    /// <summary>Space kept free at the top of the view for controls drawn over it (north arrow and summary block move below).</summary>
    public float TopInset { get; init; }
    /// <summary>Objects drawn shifted by <see cref="MoveOffset"/> (live preview while they are dragged).</summary>
    public ISet<Guid>? MovedEntities { get; init; }
    public Vec3 MoveOffset { get; init; }
    /// <summary>Colour of drawing objects (lines, points, polygons, text…) without their own colour; null uses the layer colours.</summary>
    public SKColor? GeometryColor { get; init; }
    /// <summary>Surface fill by kind; null fills every surface with the elevation ramp.</summary>
    public IReadOnlyDictionary<SurfaceKind, SKColor>? SurfaceColors { get; init; }
}

/// <summary>Draws a blast project with SkiaSharp. Used by the interactive viewport, image copy, printing and reports.</summary>
public static class PlanRenderer
{
    /// <summary>Translation hook for text drawn on the plan and in reports (the UI sets it to its localiser).</summary>
    public static Func<string, string> Translate { get; set; } = s => s;

    private static string T(string s) => Translate(s);
    private static string F(string format, params object[] args) => string.Format(Translate(format), args);

    [ThreadStatic] private static bool _dark;
    [ThreadStatic] private static float _topInset;

    /// <summary>Colour for text and outlines that contrasts with the current background (black on light, light grey on dark).</summary>
    public static bool DarkMode => _dark;

    public static SKColor Ink => _dark ? new SKColor(0xFFE6E9ED) : SKColors.Black;

    /// <summary>Lightens dark layer colours when drawing on a dark background so they stay visible.</summary>
    public static SKColor Adapt(SKColor c)
    {
        if (!_dark) return c;
        var lum = (0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue) / 255;
        if (lum >= 0.3) return c;
        const double t = 0.55;
        return new SKColor((byte)(c.Red + (230 - c.Red) * t), (byte)(c.Green + (233 - c.Green) * t), (byte)(c.Blue + (237 - c.Blue) * t), c.Alpha);
    }

    public static bool IsDark(SKColor background) => (0.2126 * background.Red + 0.7152 * background.Green + 0.0722 * background.Blue) / 255 < 0.4;

    public static void Render(SKCanvas canvas, Camera cam, BlastProject project, SceneOptions opt)
    {
        _dark = IsDark(opt.Background);
        _topInset = opt.TopInset;
        using (var bg = new SKPaint { Color = opt.Background, Style = SKPaintStyle.Fill })
            canvas.DrawRect(0, 0, (float)cam.Width, (float)cam.Height, bg);
        var view = project.View;
        var visibleLayers = project.Layers.Where(l => l.Visible).Select(l => l.Id).ToHashSet();
        var layerById = project.Layers.ToDictionary(l => l.Id);
        bool Visible(Entity e) => visibleLayers.Contains(e.LayerId) && (opt.HiddenEntities == null || !opt.HiddenEntities.Contains(e.Id)) && InSlice(e);
        bool InSlice(Entity e)
        {
            if (opt.SliceHalfWidth is not double w) return true;
            foreach (var v in e.GetVertices()) if (Math.Abs(cam.Depth(v)) <= w) return true;
            return false;
        }
        SKColor ColorOf(Entity e)
        {
            if (opt.GeometryColor is SKColor g && e.Color == null && e is PolylineEntity or PointEntity or TextEntity or CircleEntity or ArrowEntity or DimensionEntity)
                return Adapt(g);
            return Adapt(new(e.Color ?? (layerById.TryGetValue(e.LayerId, out var l) ? l.Color : 0xFF000000)));
        }
        // The view is a parallel projection, so a world offset is the same screen offset everywhere.
        var moved = opt.MovedEntities is { Count: > 0 } m ? m : null;
        var moveX = (float)(Vec3.Dot(opt.MoveOffset, cam.Right) * cam.Scale);
        var moveY = (float)(-Vec3.Dot(opt.MoveOffset, cam.Up) * cam.Scale);
        int BeginMove(Entity e)
        {
            if (moved == null || !moved.Contains(e.Id)) return -1;
            var count = canvas.Save();
            canvas.Translate(moveX, moveY);
            return count;
        }
        void EndMove(int count) { if (count >= 0) canvas.RestoreToCount(count); }

        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var text = new SKPaint { IsAntialias = true, TextSize = 11, Color = Ink, Typeface = SKTypeface.Default };

        if (view.ShowGrid) DrawGrid(canvas, cam, stroke, text);

        var entities = project.Entities.Where(Visible).ToList();
        if (view.ReverseDrawOrder) entities.Reverse();

        // Every surface shares one height scale, from the lowest point of any shown surface to the highest: by elevation
        // a colour means the same elevation everywhere, by kind the same lightness does, and the plan scale shows the ends.
        (double Min, double Max)? range = null;
        if (view.ShowSurfaces)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var s in entities.OfType<SurfaceEntity>())
                foreach (var v in s.Vertices) { if (v.Z < lo) lo = v.Z; if (v.Z > hi) hi = v.Z; }
            if (lo <= hi) range = (lo, hi);
        }

        // Surfaces first, sorted far-to-near when viewed obliquely.
        if (view.ShowSurfaces)
            foreach (var s in entities.OfType<SurfaceEntity>())
            {
                var mc = BeginMove(s);
                DrawSurface(canvas, cam, s, ColorOf(s), opt.Selection.Contains(s.Id) ? opt.SelectionColor : (SKColor?)null, SurfaceFill(s, opt), range);
                EndMove(mc);
            }

        foreach (var e in entities)
        {
            var mc = BeginMove(e);
            DrawEntity(e);
            EndMove(mc);
        }

        void DrawEntity(Entity e)
        {
            var selected = opt.Selection.Contains(e.Id);
            var color = selected ? opt.SelectionColor : ColorOf(e);
            stroke.Color = color;
            stroke.StrokeWidth = (float)((layerById.TryGetValue(e.LayerId, out var lay) ? lay.LineWidth : 1) * (selected ? 2 : 1));
            fill.Color = color;
            text.Color = color;
            var showText = view.ShowText && (lay?.ShowText ?? true);
            switch (e)
            {
                case PolylineEntity pl:
                    DrawPolyline(canvas, cam, pl.Points, pl.Closed, stroke);
                    if (pl.Closed && pl.Kind == LineKind.ClearanceZone)
                    {
                        fill.Color = color.WithAlpha(30);
                        DrawPolygonFill(canvas, cam, pl.Points, fill);
                    }
                    if (showText && pl.Kind == LineKind.Contour && pl.Points.Count > 1)
                    {
                        var (x, y) = cam.ToScreen(pl.Points[pl.Points.Count / 2]);
                        canvas.DrawText(pl.Label, x + 2, y - 2, text);
                    }
                    break;
                case PointEntity p:
                    DrawPoint(canvas, cam, p, stroke, fill, text, showText);
                    break;
                case TextEntity t when view.ShowText:
                {
                    var (x, y) = cam.ToScreen(t.Position);
                    text.TextSize = (float)Math.Clamp(t.Height * cam.Scale, 6, 200);
                    canvas.Save();
                    canvas.RotateDegrees((float)-t.Rotation, x, y);
                    canvas.DrawText(t.Text, x, y, text);
                    canvas.Restore();
                    text.TextSize = 11;
                    break;
                }
                case CircleEntity c:
                    DrawPolyline(canvas, cam, c.ToPolygon(48), true, stroke);
                    break;
                case ArrowEntity a:
                    DrawArrow(canvas, cam, a.Start, a.End, stroke, fill, 10);
                    break;
                case DimensionEntity d:
                    DrawDimension(canvas, cam, d, stroke, text);
                    break;
                case InitiationPoint ip:
                    DrawInitiationPoint(canvas, cam, ip, color, text);
                    break;
                case TimingLineEntity tl:
                    DrawTimingLine(canvas, cam, tl, selected || e.Color != null ? color : (SKColor?)null, text);
                    break;
            }
        }

        if (view.ShowTies) DrawTies(canvas, cam, project, entities.OfType<TieEntity>(), opt, stroke, fill, text);
        if (view.ShowRows) DrawRows(canvas, cam, project, stroke);

        var holes = entities.OfType<Hole>().Where(h => HoleVisible(view, h)).ToList();
        if (!cam.IsPlan) holes = holes.OrderBy(h => cam.Depth(h.Collar)).ToList();
        if (view.ShowDipTrack || view.ShowActualTrack || view.ShowDeviation || view.ShowGradePoint || view.ShowProfile)
            foreach (var h in holes) { var mc = BeginMove(h); DrawHoleTracks(canvas, cam, h, view); EndMove(mc); }
        foreach (var h in holes) { var mc = BeginMove(h); DrawHole(canvas, cam, project, h, opt, stroke, fill); EndMove(mc); }
        if (view.ShowBurdenMarker) DrawBurdenMarkers(canvas, cam, project, holes, text);
        foreach (var h in holes) { var mc = BeginMove(h); DrawHoleLabels(canvas, cam, project, h, view, opt, text); EndMove(mc); }
        foreach (var h in holes)
        {
            if (!Charging.HasLoadingWarning(project, h)) continue;
            var mc = BeginMove(h);
            DrawLoadingWarning(canvas, cam, h, opt, fill);
            EndMove(mc);
        }

        foreach (var o in opt.Overlays) o.Draw(canvas, cam, project);

        if (view.ShowAxes) DrawAxes(canvas, cam, stroke, text);
        if (opt.DrawNorthArrow) DrawNorth(canvas, cam, stroke, fill, text);
        if (opt.DrawScaleBar) DrawScaleBar(canvas, cam, stroke, text);
        if (opt.DrawElevationScale && opt.SurfaceColors == null && range is { } r)
        {
            DrawElevationScale(canvas, cam, r.Min, r.Max, text);
            text.TextSize = 11;
        }
        if (view.ShowHoleLegend && opt.DrawHoleLegend) DrawHoleLegend(canvas, cam, project, holes, fill, text);
        if (view.ShowTieLegend && opt.DrawTieLegend) DrawTieLegend(canvas, cam, project, fill, text);
        if (opt.DrawSummaryBlock && project.Summary.Visible) DrawSummary(canvas, cam, project, fill, stroke, text);
    }

    /// <summary>Radius of a hole that has already fired in the timing animation.</summary>
    public static float FiredRadius(float r) => Math.Max(r * 2.2f, r + 5);

    /// <summary>
    /// Whether an entity is drawn under the current display switches (ties, surfaces, text, hole filters), ignoring layers.
    /// Picking and selection use the same rule so nothing hidden can be selected.
    /// </summary>
    public static bool EntityShown(ViewState view, Entity e) => e switch
    {
        Hole h => HoleVisible(view, h),
        TieEntity => view.ShowTies,
        SurfaceEntity => view.ShowSurfaces,
        TextEntity => view.ShowText,
        _ => true
    };

    public static bool HoleVisible(ViewState view, Hole h) =>
        !view.HiddenHoleTypes.Contains(h.IsDummy ? 0 : h.HoleType) && !view.HiddenMaterialTypes.Contains(h.MaterialType) && !view.HiddenStates.Contains(h.State);

    // ---- primitives -------------------------------------------------------

    public static void DrawPolyline(SKCanvas c, Camera cam, IReadOnlyList<Vec3> pts, bool closed, SKPaint paint)
    {
        if (pts.Count < 2) return;
        using var path = new SKPath();
        var (x0, y0) = cam.ToScreen(pts[0]);
        path.MoveTo(x0, y0);
        for (int i = 1; i < pts.Count; i++)
        {
            var (x, y) = cam.ToScreen(pts[i]);
            path.LineTo(x, y);
        }
        if (closed) path.Close();
        c.DrawPath(path, paint);
    }

    public static void DrawPolygonFill(SKCanvas c, Camera cam, IReadOnlyList<Vec3> pts, SKPaint paint)
    {
        if (pts.Count < 3) return;
        using var path = new SKPath();
        var (x0, y0) = cam.ToScreen(pts[0]);
        path.MoveTo(x0, y0);
        for (int i = 1; i < pts.Count; i++) { var (x, y) = cam.ToScreen(pts[i]); path.LineTo(x, y); }
        path.Close();
        c.DrawPath(path, paint);
    }

    public static void DrawArrow(SKCanvas c, Camera cam, Vec3 a, Vec3 b, SKPaint stroke, SKPaint fill, float head)
    {
        var (x0, y0) = cam.ToScreen(a);
        var (x1, y1) = cam.ToScreen(b);
        c.DrawLine(x0, y0, x1, y1, stroke);
        var ang = Math.Atan2(y1 - y0, x1 - x0);
        using var path = new SKPath();
        path.MoveTo(x1, y1);
        path.LineTo((float)(x1 - head * Math.Cos(ang - 0.4)), (float)(y1 - head * Math.Sin(ang - 0.4)));
        path.LineTo((float)(x1 - head * Math.Cos(ang + 0.4)), (float)(y1 - head * Math.Sin(ang + 0.4)));
        path.Close();
        fill.Color = stroke.Color;
        c.DrawPath(path, fill);
    }

    private static void DrawPoint(SKCanvas c, Camera cam, PointEntity p, SKPaint stroke, SKPaint fill, SKPaint text, bool showText)
    {
        var (x, y) = cam.ToScreen(p.Position);
        switch (p.Kind)
        {
            case PointKind.Monitoring:
                c.DrawRect(x - 5, y - 5, 10, 10, stroke);
                c.DrawLine(x - 5, y - 5, x + 5, y + 5, stroke);
                c.DrawLine(x - 5, y + 5, x + 5, y - 5, stroke);
                break;
            case PointKind.GradePeg:
                using (var path = new SKPath())
                {
                    path.MoveTo(x, y + 6); path.LineTo(x - 5, y - 4); path.LineTo(x + 5, y - 4); path.Close();
                    c.DrawPath(path, stroke);
                }
                break;
            case PointKind.SurveyStation:
                c.DrawCircle(x, y, 6, stroke);
                c.DrawCircle(x, y, 2, fill);
                break;
            case PointKind.NorthPoint:
                c.DrawLine(x, y + 12, x, y - 12, stroke);
                c.DrawText("N", x - 4, y - 14, text);
                break;
            default:
                c.DrawLine(x - 4, y, x + 4, y, stroke);
                c.DrawLine(x, y - 4, x, y + 4, stroke);
                break;
        }
        if (showText && !string.IsNullOrEmpty(p.Label)) c.DrawText(p.Label, x + 6, y - 6, text);
    }

    private static void DrawDimension(SKCanvas c, Camera cam, DimensionEntity d, SKPaint stroke, SKPaint text)
    {
        var dir = new Vec3(d.End.X - d.Start.X, d.End.Y - d.Start.Y, 0).Normalized();
        var n = new Vec3(-dir.Y, dir.X, 0) * d.Offset;
        var a = d.Start + n;
        var b = d.End + n;
        DrawPolyline(c, cam, new[] { d.Start, a }, false, stroke);
        DrawPolyline(c, cam, new[] { d.End, b }, false, stroke);
        DrawPolyline(c, cam, new[] { a, b }, false, stroke);
        var (x, y) = cam.ToScreen(Vec3.Lerp(a, b, 0.5));
        c.DrawText($"{d.Distance:0.00} m", x + 3, y - 3, text);
    }

    /// <summary>
    /// Electronic timing line as drawn on the plan: a 3 px line through the picked points at their
    /// own elevations; at the middle of every segment a bracket with the segment relief (ms/m) on the side times grow to,
    /// on both sides plus an arrow at the segment end for a burn line; the advanced burn is a red dashed line with a start
    /// circle and two 20 m angle arms labelled with relief / cos(angle). Brackets and labels are screen-aligned.
    /// </summary>
    public static void DrawTimingLine(SKCanvas c, Camera cam, TimingLineEntity tl, SKColor? colorOverride, SKPaint text)
    {
        if (tl.Points.Count == 0) return;
        var color = colorOverride ?? Adapt(tl.Style == TimingLineStyle.BurnLine ? new SKColor(0xFF808000) : new SKColor(0xFF008000));
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = color };
        using var thin = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = color };
        var oldColor = text.Color;
        text.Color = color;

        // Bracket around the label at the middle of a→b (screen), on the −n side (or +n when mirrored).
        void Label(Vec3 a, Vec3 b, string label, bool mirrored, bool arrow)
        {
            var (x0, y0) = cam.ToScreen(a);
            var (x1, y1) = cam.ToScreen(b);
            float dx = x1 - x0, dy = y1 - y0, len = MathF.Sqrt(dx * dx + dy * dy);
            if (len == 0) return;
            dx /= len; dy /= len;
            var s = " " + label + " ";
            float w = text.MeasureText(s), h = text.TextSize * 1.2f;
            float cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            float ax = dx * w / 2, ay = dy * w / 2;
            float nx = -dy * h, ny = dx * h;
            if (mirrored) { nx = -nx; ny = -ny; }
            using (var path = new SKPath())
            {
                path.MoveTo(cx + ax, cy + ay);
                path.LineTo(cx + ax - nx, cy + ay - ny);
                path.LineTo(cx - nx * 1.5f, cy - ny * 1.5f);
                path.LineTo(cx - ax - nx, cy - ay - ny);
                path.LineTo(cx - ax, cy - ay);
                c.DrawPath(path, thin);
            }
            c.Save();
            c.RotateRadians(MathF.Atan2(dy, dx), cx, cy);
            // In the rotated frame +y is the +n side; the label sits just inside the bracket.
            c.DrawText(s, cx - w / 2, mirrored ? cy + text.TextSize : cy - text.TextSize * 0.25f, text);
            c.Restore();
            if (arrow)
            {
                using var tri = new SKPath();
                tri.MoveTo(x1, y1);
                tri.LineTo(x1 - ax + nx / 3, y1 - ay + ny / 3);
                tri.LineTo(x1 - ax - nx / 3, y1 - ay - ny / 3);
                tri.Close();
                c.DrawPath(tri, thin);
            }
        }

        if (tl.Style == TimingLineStyle.AdvancedBurn)
        {
            using (var red = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = colorOverride ?? Adapt(SKColors.Red), PathEffect = SKPathEffect.CreateDash(new[] { 8f, 8f }, 0) })
                DrawPolyline(c, cam, tl.Points, false, red);
            using (var circle = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = colorOverride ?? Adapt(SKColors.Red) })
            {
                var (sx, sy) = cam.ToScreen(tl.Points[0]);
                c.DrawCircle(sx, sy, 5, circle);
            }
            if (tl.Points.Count > 1)
            {
                var e1 = tl.AngleEnd(1);
                var e2 = tl.AngleEnd(2);
                DrawPolyline(c, cam, new[] { e1, tl.Points[0], e2 }, false, stroke);
                Label(e1, tl.Points[0], (tl.BurdenReliefMsPerM / Math.Cos(tl.Angle1Deg * Math.PI / 180)).ToString("0"), false, false);
                Label(tl.Points[0], e2, (tl.BurdenReliefMsPerM / Math.Cos(tl.Angle2Deg * Math.PI / 180)).ToString("0"), false, false);
            }
        }
        else
        {
            var pts = tl.OrderedPoints;
            DrawPolyline(c, cam, pts, false, stroke);
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var relief = i < tl.Segments.Count ? tl.Segments[i].ReliefMsPerM : 0;
                var label = relief.ToString("0.0");
                Label(pts[i], pts[i + 1], label, false, false);
                if (tl.Style == TimingLineStyle.BurnLine) Label(pts[i], pts[i + 1], label, true, true);
            }
        }
        text.Color = oldColor;
    }

    private static void DrawInitiationPoint(SKCanvas c, Camera cam, InitiationPoint ip, SKColor color, SKPaint text)
    {
        var (x, y) = cam.ToScreen(ip.Position);
        using var p = new SKPaint { IsAntialias = true, Color = Adapt(ip.IsElectronic ? new SKColor(0xFF00897B) : new SKColor(0xFFDC2626)), Style = SKPaintStyle.Fill };
        using var path = new SKPath();
        for (int i = 0; i < 10; i++)
        {
            var r = i % 2 == 0 ? 9f : 4f;
            var a = -Math.PI / 2 + i * Math.PI / 5;
            var px = x + (float)(r * Math.Cos(a));
            var py = y + (float)(r * Math.Sin(a));
            if (i == 0) path.MoveTo(px, py); else path.LineTo(px, py);
        }
        path.Close();
        c.DrawPath(path, p);
        text.Color = p.Color;
        c.DrawText($"{ip.Label} {ip.StartTimeMs:0} {T("ms")}", x + 10, y - 8, text);
        text.Color = color;
    }

    /// <summary>Per-surface render cache: shaded colours (geometry + theme only) and reusable per-frame buffers.</summary>
    private sealed class SurfaceMesh
    {
        public int Fingerprint;
        public int TriCount = -1;
        public bool Dark;
        /// <summary>Fill the colours were computed with; 0 = elevation ramp.</summary>
        public uint Fill;
        /// <summary>Shared elevation range the ramp was stretched over (NaN when it used the surface's own range).</summary>
        public double RangeMin = double.NaN, RangeMax = double.NaN;
        public SKColor[] SoupColors = Array.Empty<SKColor>(); // 3 per triangle, natural order
        public SKPoint[] Points = Array.Empty<SKPoint>();     // 3 per triangle, projected each frame
        public SKPoint[] VertexScreen = Array.Empty<SKPoint>(); // 1 per vertex, projected each frame
        public float[] VertexDepth = Array.Empty<float>();
        public SKPoint[] Ordered = Array.Empty<SKPoint>();    // oblique: depth-sorted soup
        public SKColor[] OrderedColors = Array.Empty<SKColor>();
        public int[] Order = Array.Empty<int>();
        public float[] Keys = Array.Empty<float>();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SurfaceEntity, SurfaceMesh> _surfaceMeshes = new();

    /// <summary>Wireframe is skipped above this triangle count: at that size the single mesh fill carries the shape.</summary>
    private const int WireframeTriangleCap = 40_000;

    /// <summary>Cheap identity of the surface geometry: counts plus a few sample vertices, so moves and edits refresh the colours.</summary>
    private static int GeometryFingerprint(SurfaceEntity s)
    {
        var v = s.Vertices;
        var h = new HashCode();
        h.Add(v.Count);
        h.Add(s.Triangles.Count);
        foreach (var i in v.Count == 0 ? Array.Empty<int>() : new[] { 0, v.Count / 2, v.Count - 1 })
        {
            h.Add(v[i].X); h.Add(v[i].Y); h.Add(v[i].Z);
        }
        return h.ToHashCode();
    }

    /// <summary>Lightness of a kind's colour at the lowest and the highest point of the shown surfaces (1 = the colour itself).</summary>
    public const double KindLow = 0.78, KindHigh = 1.22;

    /// <summary>Fill colour of a surface when surfaces are coloured by kind: its own colour, else its kind's; null for the elevation ramp.</summary>
    private static SKColor? SurfaceFill(SurfaceEntity s, SceneOptions opt)
    {
        if (opt.SurfaceColors == null) return null;
        var c = s.Color is uint own ? new SKColor(own) : opt.SurfaceColors.TryGetValue(s.Kind, out var k) ? k : new SKColor(0xFF8A9BB0);
        // A little darker on a light plan so the translucent fill keeps its hue against white.
        return _dark ? c : Shade(c, 0.88);
    }

    private static SKColor Shade(SKColor c, double f) =>
        new((byte)Math.Clamp(c.Red * f, 0, 255), (byte)Math.Clamp(c.Green * f, 0, 255), (byte)Math.Clamp(c.Blue * f, 0, 255), c.Alpha);

    private static void DrawSurface(SKCanvas c, Camera cam, SurfaceEntity s, SKColor color, SKColor? selected, SKColor? fill, (double Min, double Max)? range = null)
    {
        // A point cloud coloured by kind shows its measured points, not a mesh made up between them.
        if (fill is SKColor pc && s.Kind == SurfaceKind.PointCloud)
        {
            DrawPointCloud(c, cam, s, selected ?? pc);
            return;
        }
        int tri = s.TriangleCount;
        if (tri == 0) return;

        var mesh = _surfaceMeshes.GetValue(s, _ => new SurfaceMesh());
        // The same surface can be drawn by the viewport's render thread and by an export on the UI thread.
        lock (mesh) DrawSurfaceMesh(c, cam, s, mesh, tri, fill is SKColor f ? Shade(f, 0.62) : color, selected, fill, range);
    }

    private static void DrawPointCloud(SKCanvas c, Camera cam, SurfaceEntity s, SKColor color)
    {
        if (s.Vertices.Count == 0) return;
        var pts = new SKPoint[s.Vertices.Count];
        for (int i = 0; i < pts.Length; i++)
        {
            var (x, y) = cam.ToScreen(s.Vertices[i]);
            pts[i] = new SKPoint(x, y);
        }
        using var paint = new SKPaint { IsAntialias = true, Color = color.WithAlpha(_dark ? (byte)200 : (byte)220), StrokeWidth = pts.Length > 50_000 ? 1.5f : 2.5f, StrokeCap = SKStrokeCap.Round };
        c.DrawPoints(SKPointMode.Points, pts, paint);
    }

    private static void DrawSurfaceMesh(SKCanvas c, Camera cam, SurfaceEntity s, SurfaceMesh mesh, int tri, SKColor color, SKColor? selected, SKColor? fill, (double Min, double Max)? range)
    {
        // The per-triangle fill colour depends only on the geometry, the theme, the fill and the shared elevation range,
        // so compute the expensive normal shading and colouring once and reuse it; only the projection runs per frame.
        var fingerprint = GeometryFingerprint(s);
        var fillKey = fill is SKColor fk ? (uint)fk | 1u : 0u;
        var rMin = range?.Min ?? double.NaN;
        var rMax = range?.Max ?? double.NaN;
        if (mesh.TriCount != tri || mesh.Dark != _dark || mesh.Fingerprint != fingerprint || mesh.Fill != fillKey
            || !mesh.RangeMin.Equals(rMin) || !mesh.RangeMax.Equals(rMax))
        {
            mesh.Fingerprint = fingerprint;
            mesh.TriCount = tri;
            mesh.Dark = _dark;
            mesh.Fill = fillKey;
            mesh.RangeMin = rMin;
            mesh.RangeMax = rMax;
            mesh.SoupColors = new SKColor[tri * 3];
            mesh.Points = new SKPoint[tri * 3];
            mesh.Ordered = Array.Empty<SKPoint>();
            mesh.OrderedColors = Array.Empty<SKColor>();
            mesh.Order = Array.Empty<int>();
            mesh.Keys = Array.Empty<float>();
            double minZ = double.MaxValue, maxZ = double.MinValue;
            if (range is { } shared) (minZ, maxZ) = shared;
            else foreach (var v in s.Vertices) { if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z; }
            var light = new Vec3(-0.4, 0.5, 0.75).Normalized();
            byte alpha = (byte)(_dark ? 70 : 150);
            for (int t = 0; t < tri; t++)
            {
                var a = s.Vertices[s.Triangles[3 * t]];
                var b = s.Vertices[s.Triangles[3 * t + 1]];
                var cc = s.Vertices[s.Triangles[3 * t + 2]];
                var n = Vec3.Cross(b - a, cc - a).Normalized();
                if (n.Z < 0) n = -n;
                var shade = 0.55 + 0.45 * Math.Max(0, Vec3.Dot(n, light));
                var h = maxZ > minZ ? ((a.Z + b.Z + cc.Z) / 3 - minZ) / (maxZ - minZ) : 0.5;
                // By kind: the kind's hue, darker at the bottom and lighter at the top (±22 %) so height still reads.
                // Otherwise the elevation ramp.
                var bc = fill is SKColor kc ? kc : ColorRamp.Elevation(h);
                var k = fill != null ? shade * (KindLow + (KindHigh - KindLow) * h) : shade;
                var col = new SKColor((byte)Math.Min(255, bc.Red * k), (byte)Math.Min(255, bc.Green * k), (byte)Math.Min(255, bc.Blue * k), alpha);
                mesh.SoupColors[3 * t] = mesh.SoupColors[3 * t + 1] = mesh.SoupColors[3 * t + 2] = col;
            }
        }

        // Project each vertex once with the camera basis computed once per frame (ToScreen recomputes the
        // trigonometry on every call), then gather the triangle corners from it.
        var verts = s.Vertices;
        int vc = verts.Count;
        bool plan = cam.IsPlan;
        if (mesh.VertexScreen.Length != vc) { mesh.VertexScreen = new SKPoint[vc]; mesh.VertexDepth = new float[vc]; }
        var right = cam.Right; var up = cam.Up; var toViewer = cam.ToViewer; var target = cam.Target;
        double hw = cam.Width / 2, hh = cam.Height / 2, scale = cam.Scale;
        var vs = mesh.VertexScreen;
        var vd = mesh.VertexDepth;
        for (int i = 0; i < vc; i++)
        {
            var v = verts[i];
            double dx = v.X - target.X, dy = v.Y - target.Y, dz = v.Z - target.Z;
            vs[i] = new SKPoint((float)(hw + (dx * right.X + dy * right.Y + dz * right.Z) * scale),
                                (float)(hh - (dx * up.X + dy * up.Y + dz * up.Z) * scale));
            if (!plan) vd[i] = (float)(dx * toViewer.X + dy * toViewer.Y + dz * toViewer.Z);
        }
        var pts = mesh.Points;
        var tris = s.Triangles;
        for (int k = 0; k < tri * 3; k++) pts[k] = vs[tris[k]];

        // One GPU draw call for the whole mesh. Oblique views need far-to-near order so translucent triangles blend.
        SKPoint[] soupPts;
        SKColor[] soupCols;
        if (plan)
        {
            soupPts = pts;
            soupCols = mesh.SoupColors;
        }
        else
        {
            if (mesh.Ordered.Length != tri * 3) { mesh.Ordered = new SKPoint[tri * 3]; mesh.OrderedColors = new SKColor[tri * 3]; mesh.Order = new int[tri]; mesh.Keys = new float[tri]; }
            // Sort by precomputed keys (sum of corner depths, far first): no comparison delegate, so the order is
            // always consistent and depth is computed once per vertex instead of on every comparison.
            for (int t = 0; t < tri; t++)
            {
                mesh.Order[t] = t;
                mesh.Keys[t] = vd[tris[3 * t]] + vd[tris[3 * t + 1]] + vd[tris[3 * t + 2]];
            }
            Array.Sort(mesh.Keys, mesh.Order);
            for (int i = 0; i < tri; i++)
            {
                int t = mesh.Order[i];
                mesh.Ordered[3 * i] = pts[3 * t]; mesh.Ordered[3 * i + 1] = pts[3 * t + 1]; mesh.Ordered[3 * i + 2] = pts[3 * t + 2];
                mesh.OrderedColors[3 * i] = mesh.OrderedColors[3 * i + 1] = mesh.OrderedColors[3 * i + 2] = mesh.SoupColors[3 * t];
            }
            soupPts = mesh.Ordered;
            soupCols = mesh.OrderedColors;
        }

        using (var skVertices = SKVertices.CreateCopy(SKVertexMode.Triangles, soupPts, soupCols))
        using (var vpaint = new SKPaint { IsAntialias = false })
            c.DrawVertices(skVertices, SKBlendMode.Modulate, vpaint);

        // Wireframe as a single batched path, skipping triangles fully outside the viewport.
        if (s.ShowWireframe && cam.Scale > 0.5 && tri <= WireframeTriangleCap)
        {
            float w = (float)cam.Width, hgt = (float)cam.Height;
            using var wire = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f, Color = (selected ?? color).WithAlpha(110) };
            using var path = new SKPath();
            for (int t = 0; t < tri; t++)
            {
                var p0 = pts[3 * t]; var p1 = pts[3 * t + 1]; var p2 = pts[3 * t + 2];
                if ((p0.X < 0 && p1.X < 0 && p2.X < 0) || (p0.X > w && p1.X > w && p2.X > w) ||
                    (p0.Y < 0 && p1.Y < 0 && p2.Y < 0) || (p0.Y > hgt && p1.Y > hgt && p2.Y > hgt)) continue;
                path.MoveTo(p0); path.LineTo(p1); path.LineTo(p2); path.Close();
            }
            c.DrawPath(path, wire);
        }
    }

    private static void DrawTies(SKCanvas c, Camera cam, BlastProject project, IEnumerable<TieEntity> ties, SceneOptions opt, SKPaint stroke, SKPaint fill, SKPaint text)
    {
        foreach (var t in ties)
        {
            var from = Timing.PositionOf(project, t.FromId);
            var to = Timing.PositionOf(project, t.ToId);
            if (from is null || to is null) continue;
            var product = project.FindProduct(t.ProductId);
            var col = opt.Selection.Contains(t.Id) ? opt.SelectionColor : product != null ? new SKColor(product.Color) : new SKColor(0xFF6B7280);
            if (!_dark && col.Red > 230 && col.Green > 230 && col.Blue > 230) col = new SKColor(0xFF374151);
            col = Adapt(col);
            stroke.Color = col;
            stroke.StrokeWidth = opt.Selection.Contains(t.Id) ? 3 : 2;
            var a = from.Value;
            var b = to.Value;
            var dir = (b - a).Normalized();
            var gap = Math.Min(8 / cam.Scale, Vec3.Distance(a, b) / 3);
            DrawArrow(c, cam, a + dir * gap, b - dir * gap, stroke, fill, 8);
            if (cam.Scale > 4)
            {
                var (x, y) = cam.ToScreen(Vec3.Lerp(a, b, 0.5));
                text.Color = col;
                text.TextSize = 9;
                c.DrawText($"{Timing.TieDelay(project, t):0}", x + 2, y - 2, text);
                text.TextSize = 11;
            }
        }
    }

    private static void DrawRows(SKCanvas c, Camera cam, BlastProject project, SKPaint stroke)
    {
        stroke.Color = new SKColor(0x8016A34A);
        stroke.StrokeWidth = 1;
        stroke.PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0);
        foreach (var r in project.Rows)
        {
            var pts = r.HoleIds.Select(project.FindEntity).OfType<Hole>().Select(h => h.Collar).ToList();
            DrawPolyline(c, cam, pts, false, stroke);
        }
        stroke.PathEffect = null;
    }

    private static void DrawHole(SKCanvas c, Camera cam, BlastProject project, Hole h, SceneOptions opt, SKPaint stroke, SKPaint fill)
    {
        var view = project.View;
        var selected = opt.Selection.Contains(h.Id);
        var typeColor = Adapt(new SKColor(project.Design.HoleTypes.FirstOrDefault(t => t.Index == h.HoleType)?.Color ?? 0xFF1E3A8A));
        var color = selected ? opt.SelectionColor : typeColor;
        var (x, y) = cam.ToScreen(h.Collar);
        var r = (float)Math.Max(opt.CollarPixels / 2, h.RadiusM * cam.Scale);

        // Hole depth line (collar to toe) and, on its own switch, the charge column along it.
        var (tx, ty) = cam.ToScreen(h.Toe);
        if (Math.Abs(tx - x) + Math.Abs(ty - y) > 1)
        {
            if (!view.CollarsOnly)
            {
                stroke.Color = color.WithAlpha(160);
                stroke.StrokeWidth = 1;
                c.DrawLine(x, y, tx, ty, stroke);
            }
            if (view.ShowLoading && h.Decks.Count > 0)
            {
                stroke.StrokeWidth = Math.Max(3, r);
                foreach (var d in h.Decks)
                {
                    var p = project.FindProduct(d.ProductId);
                    if (p == null) continue;
                    stroke.Color = new SKColor(p.Color);
                    var (ax, ay) = cam.ToScreen(h.PointAtDepth(d.Top));
                    var (bx, by) = cam.ToScreen(h.PointAtDepth(d.Bottom));
                    c.DrawLine(ax, ay, bx, by, stroke);
                }
                stroke.StrokeWidth = 1;
            }
            if (!view.CollarsOnly)
            {
                fill.Color = color.WithAlpha(160);
                c.DrawCircle(tx, ty, 1.5f, fill);
            }
        }

        bool fired = opt.AnimationTimeMs is double at && h.FiringTimeMs is double ft && ft <= at;
        stroke.Color = color;
        stroke.StrokeWidth = selected ? 2.5f : 1.5f;
        if (h.IsDummy)
        {
            c.DrawLine(x - r, y - r, x + r, y + r, stroke);
            c.DrawLine(x - r, y + r, x + r, y - r, stroke);
            c.DrawCircle(x, y, r, stroke);
        }
        else
        {
            if (fired)
            {
                // Fired holes are shown as a larger circle during timing animation.
                var big = FiredRadius(r);
                fill.Color = typeColor;
                c.DrawCircle(x, y, big, fill);
                stroke.Color = selected ? opt.SelectionColor : Ink;
                c.DrawCircle(x, y, big, stroke);
                stroke.Color = color;
            }
            fill.Color = h.State switch
            {
                HoleState.Designed => opt.Background,
                HoleState.Drilled or HoleState.Dipped => typeColor.WithAlpha(90),
                HoleState.Released => new SKColor(0xFFFDE68A),
                HoleState.Loaded => typeColor,
                HoleState.Fired => new SKColor(0xFF6B7280),
                _ => opt.Background
            };
            if (view.ShowLoading && h.Decks.Count > 0)
            {
                var exp = h.Decks.Select(d => project.FindProduct(d.ProductId)).FirstOrDefault(p => p?.IsExplosive == true);
                if (exp != null) fill.Color = new SKColor(exp.Color);
            }
            if (view.ShowCollars || selected)
            {
                c.DrawCircle(x, y, r, fill);
                c.DrawCircle(x, y, r, stroke);
            }
        }
        if (h.Exceptions != HoleException.None)
        {
            stroke.Color = new SKColor(0xFFDC2626);
            stroke.StrokeWidth = 1.5f;
            c.DrawCircle(x, y, r + 3, stroke);
        }
        if (h.Locked)
        {
            fill.Color = new SKColor(0xFF6B7280);
            c.DrawRect(x + r, y + r, 3, 3, fill);
        }
    }

    private static void DrawHoleLabels(SKCanvas c, Camera cam, BlastProject project, Hole h, ViewState view, SceneOptions opt, SKPaint text)
    {
        var layer = project.FindLayer(h.LayerId);
        if (layer is { ShowText: false }) return;
        if (cam.Scale < 1.2) return;
        var (x, y) = cam.ToScreen(h.Collar);
        var r = (float)Math.Max(opt.CollarPixels / 2, h.RadiusM * cam.Scale);
        var selected = opt.Selection.Contains(h.Id);
        text.Color = selected ? opt.SelectionColor : Ink;
        text.TextSize = 10;
        var lines = new List<string>();
        var m = view.HoleLabels;
        var numbered = m.HasFlag(HoleLabelMode.Label);
        if (numbered)
        {
            // Hole number top right of the collar in blue, mirroring the hole order top left.
            text.Color = selected ? opt.SelectionColor : new SKColor(_dark ? 0xFF60A5FAu : 0xFF1D4ED8u);
            c.DrawText(h.Label, x + r + 1, y - r - 2, text);
            text.Color = selected ? opt.SelectionColor : Ink;
        }
        if (m.HasFlag(HoleLabelMode.Length)) lines.Add($"L {h.EffectiveLength:0.0}");
        if (m.HasFlag(HoleLabelMode.Diameter)) lines.Add($"Ø {h.Diameter:0}");
        if (m.HasFlag(HoleLabelMode.Angle)) lines.Add($"α {h.Angle:0}°");
        if (m.HasFlag(HoleLabelMode.Bearing)) lines.Add($"Az {h.Bearing:0}°");
        if (m.HasFlag(HoleLabelMode.CollarZ)) lines.Add($"Z {h.Collar.Z:0.0}");
        if (m.HasFlag(HoleLabelMode.Charge)) lines.Add($"{Charging.ExplosiveMass(project, h):0.0} kg");
        if (m.HasFlag(HoleLabelMode.DeckMasses))
            foreach (var d in h.Decks.OrderBy(d => d.Top))
                if (project.FindProduct(d.ProductId) is { IsExplosive: true }) lines.Add($"▪ {Charging.DeckMass(project, h, d):0.0} kg");
        if (m.HasFlag(HoleLabelMode.DeckSequence) && DeckSequence(project, h) is string seq) lines.Add(F("Decks: {0}", seq));
        if (m.HasFlag(HoleLabelMode.Temperature) && h.Temperature is double temp) lines.Add($"{temp:0} °C");
        if (m.HasFlag(HoleLabelMode.DetonatorId) && !string.IsNullOrWhiteSpace(h.DetonatorId)) lines.Add(h.DetonatorId);
        if (m.HasFlag(HoleLabelMode.Type)) lines.Add($"T{h.HoleType}");
        // the other labels follow on the right, under the number
        var first = numbered ? y - r + 9 : y - r;
        for (int i = 0; i < lines.Count; i++) c.DrawText(lines[i], x + r + 2, first + i * 11, text);

        var timingLabels = new List<string>();
        if ((view.ShowSurfaceTimeLabel || view.TimingLabels == TimingLabelMode.SurfaceTime) && h.SurfaceTimeMs is double s) timingLabels.Add($"{s:0}");
        if ((view.ShowHoleTimeLabel || view.TimingLabels == TimingLabelMode.InholeTime) && h.FiringTimeMs is double f) timingLabels.Add($"{f:0}");
        if ((view.ShowInholeDelayLabel || view.TimingLabels == TimingLabelMode.DelayNumber) && h.InholeDelayMs > 0) timingLabels.Add($"{h.InholeDelayMs:0}");
        if ((view.ShowHoleSequenceLabel || view.TimingLabels == TimingLabelMode.HoleSequence) && h.FiringSequence is int sequence)
        {
            // Top left of the collar, clear of the labels on the right and the times below.
            var order = sequence.ToString();
            c.DrawText(order, x - r - text.MeasureText(order) - 1, y - r - 2, text);
        }
        for (int i = 0; i < timingLabels.Count; i++)
        {
            var timing = timingLabels[i];
            text.Color = Adapt(new SKColor(0xFFB91C1C));
            var w = text.MeasureText(timing);
            c.DrawText(timing, x - w / 2, y + r + 11 + i * 11, text);
        }
        text.TextSize = 11;
    }

    /// <summary>
    /// Firing order of the decks of a hole, top to bottom ("2-1": the bottom deck fires first), from the earliest primer
    /// delay of each charged deck; null when fewer than two decks carry primers.
    /// </summary>
    private static string? DeckSequence(BlastProject project, Hole h)
    {
        var decks = h.Decks.Where(d => d.Primers.Count > 0).OrderBy(d => d.Top)
            .Select(d => d.Primers.Min(p => Charging.PrimerDelay(project, p))).ToList();
        if (decks.Count < 2) return null;
        var order = decks.Select((t, i) => (t, i)).OrderBy(x => x.t).Select((x, rank) => (x.i, rank)).OrderBy(x => x.i).Select(x => x.rank + 1);
        return string.Join("-", order);
    }

    /// <summary>Dip track, surveyed track, grade point, deviation from the design and MWD profile, as switched on.</summary>
    private static void DrawHoleTracks(SKCanvas c, Camera cam, Hole h, ViewState view)
    {
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var text = new SKPaint { IsAntialias = true, TextSize = 9.5f };
        void Line(Vec3 a, Vec3 b)
        {
            var (x0, y0) = cam.ToScreen(a);
            var (x1, y1) = cam.ToScreen(b);
            c.DrawLine(x0, y0, x1, y1, stroke);
        }
        if (view.ShowProfile && h.Mwd is { Count: > 1 } mwd)
        {
            // Hardness along the track, soft blue to hard red, drawn wide under the other tracks.
            var samples = mwd.OrderBy(s => s.Depth).ToList();
            stroke.StrokeWidth = 5;
            stroke.StrokeCap = SKStrokeCap.Butt;
            for (int i = 1; i < samples.Count; i++)
            {
                stroke.Color = ColorRamp.Heat(Math.Clamp((samples[i - 1].Hardness + samples[i].Hardness) / 2, 0, 1)).WithAlpha(200);
                Line(h.PointAtDepth(samples[i - 1].Depth), h.PointAtDepth(samples[i].Depth));
            }
        }
        if (view.ShowDipTrack && h.ActualLength is double dipped)
        {
            stroke.StrokeWidth = 3;
            stroke.StrokeCap = SKStrokeCap.Round;
            stroke.Color = Adapt(new SKColor(0xB022B8C8));
            var end = h.PointAtDepth(dipped);
            Line(h.Collar, end);
            var (ex, ey) = cam.ToScreen(end);
            fill.Color = Adapt(new SKColor(0xFF22B8C8));
            c.DrawRect(ex - 3, ey - 1, 6, 2.5f, fill);
        }
        if (view.ShowActualTrack && h.BoreTrack is { Count: > 1 } track)
        {
            stroke.StrokeWidth = 1.6f;
            stroke.Color = Adapt(new SKColor(0xFFEA580C));
            stroke.PathEffect = SKPathEffect.CreateDash(new[] { 5f, 3f }, 0);
            DrawPolyline(c, cam, track, false, stroke);
            stroke.PathEffect = null;
        }
        if (view.ShowGradePoint)
        {
            var (gx, gy) = cam.ToScreen(h.GradePoint);
            fill.Color = Adapt(new SKColor(0xFF16A34A));
            using var diamond = new SKPath();
            diamond.MoveTo(gx, gy - 4); diamond.LineTo(gx + 4, gy); diamond.LineTo(gx, gy + 4); diamond.LineTo(gx - 4, gy); diamond.Close();
            c.DrawPath(diamond, fill);
        }
        if (view.ShowDeviation)
        {
            stroke.StrokeWidth = 1.4f;
            stroke.Color = Adapt(new SKColor(0xFFDC2626));
            text.Color = stroke.Color;
            void Offset(Vec3 design, Vec3 actual)
            {
                var d = Vec3.Distance(design, actual);
                if (d < 0.01) return;
                Line(design, actual);
                var (ax, ay) = cam.ToScreen(actual);
                c.DrawCircle(ax, ay, 2.5f, stroke);
                c.DrawText($"Δ{d:0.00}", ax + 4, ay + 10, text);
            }
            if (h.ActualCollar is Vec3 ac) Offset(h.Collar, ac);
            if (h.BoreTrack is { Count: > 1 } bt) Offset(h.DesignToe, bt[^1]);
        }
    }

    /// <summary>Burden of the front row: from each hole next to the crest to the nearest crest point, dashed and labelled.</summary>
    private static void DrawBurdenMarkers(SKCanvas c, Camera cam, BlastProject project, IReadOnlyList<Hole> holes, SKPaint text)
    {
        var crest = project.Entities.OfType<PolylineEntity>().FirstOrDefault(l => l.Kind == LineKind.Crest && l.Points.Count > 1);
        if (crest == null || holes.Count == 0) return;
        var near = holes.Select(h =>
        {
            Vec3 best = crest.Points[0];
            var bestD = double.MaxValue;
            for (int i = 1; i < crest.Points.Count; i++)
            {
                var (q, _) = GeoMath.ClosestOnSegment2D(crest.Points[i - 1], crest.Points[i], h.Collar);
                var d = Vec3.Distance2D(q, h.Collar);
                if (d < bestD) { bestD = d; best = q; }
            }
            return (Hole: h, Point: best.WithZ(h.Collar.Z), Burden: bestD);
        }).ToList();
        // Front row: the holes about as close to the crest as the closest one.
        var limit = Math.Max(1.5 * Math.Max(project.Design.Burden, 1), near.Min(n => n.Burden) * 1.3);
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = Adapt(new SKColor(0xFF9B7BFF)), PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0) };
        var old = text.Color;
        text.Color = stroke.Color;
        text.TextSize = 10;
        foreach (var (h, point, burden) in near.Where(n => n.Burden <= limit))
        {
            var (x0, y0) = cam.ToScreen(h.Collar);
            var (x1, y1) = cam.ToScreen(point);
            c.DrawLine(x0, y0, x1, y1, stroke);
            c.DrawText($"B {burden:0.0}", (x0 + x1) / 2 + 3, (y0 + y1) / 2 - 3, text);
        }
        text.Color = old;
        text.TextSize = 11;
    }

    /// <summary>Amber warning triangle with "!" above the collar of a hole whose charge fails a loading check.</summary>
    private static void DrawLoadingWarning(SKCanvas c, Camera cam, Hole h, SceneOptions opt, SKPaint fill)
    {
        var (x, y) = cam.ToScreen(h.Collar);
        var r = (float)Math.Max(opt.CollarPixels / 2, h.RadiusM * cam.Scale);
        const float size = 13;
        var bottom = y - r - 10;
        using var tri = new SKPath();
        tri.MoveTo(x, bottom - size);
        tri.LineTo(x + size * 0.58f, bottom);
        tri.LineTo(x - size * 0.58f, bottom);
        tri.Close();
        fill.Color = new SKColor(0xFFF59E0B);
        c.DrawPath(tri, fill);
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(0xFF7C4A03) };
        c.DrawPath(tri, edge);
        fill.Color = new SKColor(0xFF1F1300);
        c.DrawRect(x - 0.9f, bottom - size + 4.5f, 1.8f, 4.5f, fill);
        c.DrawCircle(x, bottom - 2.2f, 1.1f, fill);
    }

    // ---- decorations ---------------------------------------------------------

    private static void DrawGrid(SKCanvas c, Camera cam, SKPaint stroke, SKPaint text)
    {
        if (!cam.IsPlan) return;
        var step = NiceStep(100 / cam.Scale);
        var a = cam.ToWorld(0, cam.Height);
        var b = cam.ToWorld(cam.Width, 0);
        var corners = new[] { a, b, cam.ToWorld(0, 0), cam.ToWorld(cam.Width, cam.Height) };
        var minX = corners.Min(p => p.X); var maxX = corners.Max(p => p.X);
        var minY = corners.Min(p => p.Y); var maxY = corners.Max(p => p.Y);
        if ((maxX - minX) / step > 400) return;
        stroke.Color = _dark ? new SKColor(0xFF242930) : new SKColor(0xFFE5E7EB);
        stroke.StrokeWidth = 1;
        text.Color = new SKColor(0xFF9CA3AF);
        text.TextSize = 9;
        for (var x = Math.Floor(minX / step) * step; x <= maxX; x += step)
        {
            var (sx1, sy1) = cam.ToScreen(new Vec3(x, minY, cam.Target.Z));
            var (sx2, sy2) = cam.ToScreen(new Vec3(x, maxY, cam.Target.Z));
            c.DrawLine(sx1, sy1, sx2, sy2, stroke);
            if (Math.Abs(cam.Yaw) < 1e-6) c.DrawText(x.ToString("0"), sx1 + 2, (float)cam.Height - 4, text);
        }
        for (var y = Math.Floor(minY / step) * step; y <= maxY; y += step)
        {
            var (sx1, sy1) = cam.ToScreen(new Vec3(minX, y, cam.Target.Z));
            var (sx2, sy2) = cam.ToScreen(new Vec3(maxX, y, cam.Target.Z));
            c.DrawLine(sx1, sy1, sx2, sy2, stroke);
            if (Math.Abs(cam.Yaw) < 1e-6) c.DrawText(y.ToString("0"), 2, sy1 - 2, text);
        }
        text.TextSize = 11;
    }

    public static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        var p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var f = raw / p;
        return (f < 1.5 ? 1 : f < 3.5 ? 2 : f < 7.5 ? 5 : 10) * p;
    }

    private static void DrawAxes(SKCanvas c, Camera cam, SKPaint stroke, SKPaint text)
    {
        var o = new SKPoint(40, (float)cam.Height - 40);
        var axes = new[] { (new Vec3(1, 0, 0), "X", SKColors.Red), (new Vec3(0, 1, 0), "Y", SKColors.Green), (new Vec3(0, 0, 1), "Z", _dark ? new SKColor(0xFF4C9BFF) : SKColors.Blue) };
        foreach (var (v, name, col) in axes)
        {
            var dx = (float)Vec3.Dot(v, cam.Right) * 25;
            var dy = -(float)Vec3.Dot(v, cam.Up) * 25;
            if (Math.Abs(dx) + Math.Abs(dy) < 2) continue;
            stroke.Color = col;
            stroke.StrokeWidth = 2;
            c.DrawLine(o.X, o.Y, o.X + dx, o.Y + dy, stroke);
            text.Color = col;
            c.DrawText(name, o.X + dx * 1.2f - 3, o.Y + dy * 1.2f + 4, text);
        }
    }

    private static void DrawNorth(SKCanvas c, Camera cam, SKPaint stroke, SKPaint fill, SKPaint text)
    {
        var cx = (float)cam.Width - 30;
        var cy = 40f + _topInset;
        var dx = (float)Vec3.Dot(new Vec3(0, 1, 0), cam.Right);
        var dy = -(float)Vec3.Dot(new Vec3(0, 1, 0), cam.Up);
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.05f) return;
        dx /= len; dy /= len;
        using var path = new SKPath();
        path.MoveTo(cx + dx * 18, cy + dy * 18);
        path.LineTo(cx - dy * 7 - dx * 10, cy + dx * 7 - dy * 10);
        path.LineTo(cx, cy);
        path.Close();
        fill.Color = Ink;
        c.DrawPath(path, fill);
        stroke.Color = Ink;
        stroke.StrokeWidth = 1;
        using var path2 = new SKPath();
        path2.MoveTo(cx + dx * 18, cy + dy * 18);
        path2.LineTo(cx + dy * 7 - dx * 10, cy - dx * 7 - dy * 10);
        path2.LineTo(cx, cy);
        path2.Close();
        c.DrawPath(path2, stroke);
        text.Color = Ink;
        c.DrawText("N", cx + dx * 26 - 4, cy + dy * 26 + 4, text);
    }

    /// <summary>
    /// Key to the elevation fill at the left edge above the axes: a thin ramp with the highest elevation of the shown
    /// surfaces above it and the lowest below it — the same range the fill is stretched over.
    /// </summary>
    private static void DrawElevationScale(SKCanvas c, Camera cam, double min, double max, SKPaint text)
    {
        const float barW = 6, barH = 130;
        var barL = 22f;
        var bottom = (float)cam.Height - 112; // with the label below, still clear of the axes in the bottom-left corner
        var top = bottom - barH;
        var colors = Enumerable.Range(0, 6).Select(i => ColorRamp.Elevation(1 - i / 5.0)).ToArray();
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, bottom), colors, null, SKShaderTileMode.Clamp))
        using (var bar = new SKPaint { IsAntialias = true, Shader = shader })
            c.DrawRoundRect(new SKRect(barL, top, barL + barW, bottom), 3, 3, bar);
        text.TextSize = 11;
        text.Color = Ink;
        c.DrawText($"{max:0.0} m", barL - 4, top - 6, text);
        c.DrawText($"{min:0.0} m", barL - 4, bottom + 15, text);
    }

    private static void DrawScaleBar(SKCanvas c, Camera cam, SKPaint stroke, SKPaint text)
    {
        var metres = NiceStep(120 / cam.Scale);
        var px = (float)(metres * cam.Scale);
        var x = (float)cam.Width - px - 20;
        var y = (float)cam.Height - 20;
        stroke.Color = Ink;
        stroke.StrokeWidth = 2;
        c.DrawLine(x, y, x + px, y, stroke);
        c.DrawLine(x, y - 4, x, y + 4, stroke);
        c.DrawLine(x + px, y - 4, x + px, y + 4, stroke);
        text.Color = Ink;
        c.DrawText($"{metres:0.##} m", x, y - 6, text);
    }

    private static void DrawHoleLegend(SKCanvas c, Camera cam, BlastProject project, List<Hole> holes, SKPaint fill, SKPaint text)
    {
        var used = holes.Select(h => h.HoleType).Distinct().OrderBy(x => x).ToList();
        float x = 10, y = 20 + _topInset;
        foreach (var t in used)
        {
            var def = project.Design.HoleTypes.FirstOrDefault(d => d.Index == t);
            fill.Color = new SKColor(def?.Color ?? 0xFF000000);
            c.DrawCircle(x + 5, y - 4, 5, fill);
            text.Color = Ink;
            c.DrawText($"{T(def?.Name ?? "Type " + t)} ({holes.Count(h => h.HoleType == t)})", x + 14, y, text);
            y += 15;
        }
    }

    private static void DrawTieLegend(SKCanvas c, Camera cam, BlastProject project, SKPaint fill, SKPaint text)
    {
        var used = project.Ties.Select(t => project.FindProduct(t.ProductId)).Where(p => p != null).Distinct().ToList();
        float x = 10, y = (float)cam.Height - 60 - used.Count * 15;
        foreach (var p in used)
        {
            fill.Color = new SKColor(p!.Color);
            c.DrawRect(x, y - 8, 18, 4, fill);
            text.Color = Ink;
            c.DrawText($"{p.Name} ({project.Ties.Count(t => t.ProductId == p.Id)})", x + 24, y, text);
            y += 15;
        }
    }

    private static void DrawSummary(SKCanvas c, Camera cam, BlastProject project, SKPaint fill, SKPaint stroke, SKPaint text)
    {
        var lines = new List<string>();
        if (project.Summary.ShowHeader)
        {
            lines.Add(project.Header.Name);
            if (!string.IsNullOrWhiteSpace(project.Header.Mine)) lines.Add(project.Header.Mine + " " + project.Header.Bench);
        }
        var holes = project.Holes.Where(h => !h.IsDummy).ToList();
        if (project.Summary.ShowPattern && holes.Count > 0)
            lines.Add(F("Holes: {0}   Ø {1:0} mm   mean length {2:0.0} m", holes.Count, holes.Average(h => h.Diameter), holes.Average(h => h.EffectiveLength)));
        if (project.Summary.ShowQuantities && holes.Count > 0)
            lines.Add(F("Explosive: {0:0} kg", holes.Sum(h => Charging.ExplosiveMass(project, h))));
        if (lines.Count == 0) return;
        text.TextSize = 11;
        var w = lines.Max(l => text.MeasureText(l)) + 16;
        var h = lines.Count * 15 + 10;
        var right = project.Summary.Corner.EndsWith("Right");
        var x = right ? (float)cam.Width - w - 60 : 10;
        var y = project.Summary.Corner.StartsWith("Top") ? 10 + _topInset : (float)cam.Height - h - 40;
        fill.Color = _dark ? new SKColor(0xEE20242A) : new SKColor(0xEEFFFFFF);
        c.DrawRect(x, y, w, h, fill);
        stroke.Color = _dark ? new SKColor(0xFF3A414A) : new SKColor(0xFF9CA3AF);
        stroke.StrokeWidth = 1;
        c.DrawRect(x, y, w, h, stroke);
        text.Color = Ink;
        for (int i = 0; i < lines.Count; i++) c.DrawText(lines[i], x + 8, y + 18 + i * 15, text);
    }
}

public static class ColorRamp
{
    /// <summary>Blue → green → yellow → red for t in [0, 1].</summary>
    public static SKColor Heat(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var stops = new[] { (0.0, 0x2563EB), (0.33, 0x10B981), (0.66, 0xFACC15), (1.0, 0xDC2626) };
        for (int i = 1; i < stops.Length; i++)
            if (t <= stops[i].Item1)
            {
                var f = (t - stops[i - 1].Item1) / (stops[i].Item1 - stops[i - 1].Item1);
                return Lerp(stops[i - 1].Item2, stops[i].Item2, f);
            }
        return new SKColor(0xFFDC2626);
    }

    public static SKColor Elevation(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5 ? Lerp(0x8D9EB5, 0xB5A58D, t * 2) : Lerp(0xB5A58D, 0xE6DCC8, (t - 0.5) * 2);
    }

    private static SKColor Lerp(int a, int b, double f)
    {
        byte C(int shift) => (byte)(((a >> shift) & 0xFF) + (((b >> shift) & 0xFF) - ((a >> shift) & 0xFF)) * f);
        return new SKColor(C(16), C(8), C(0));
    }
}
