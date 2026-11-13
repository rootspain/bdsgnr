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

/// <summary>An overlay computed from object positions: while objects are dragged it is drawn from their shifted positions.</summary>
public interface IMovePreviewOverlay : IOverlay
{
    void Draw(SKCanvas canvas, Camera camera, BlastProject project, ISet<Guid>? moved, Vec3 offset);
}

public sealed record SceneOptions
{
    public ISet<Guid> Selection { get; init; } = new HashSet<Guid>();
    /// <summary>
    /// The selected objects get a halo, as the hole labels do: drawn once more over the scene, round a band in a colour
    /// against the selection colour, so they read over a surface of any colour.
    /// </summary>
    public bool SelectionHalo { get; init; }
    /// <summary>How far the halo reaches out of a selected object, px (1 or 2).</summary>
    public int SelectionHaloPx { get; init; } = 2;
    /// <summary>How much thicker a selected object is drawn than its line, px (the collar of a hole half as much); 0 as thick.</summary>
    public float SelectionExtra { get; init; } = 2;
    /// <summary>The second pass of <see cref="SelectionHalo"/>: only the selected objects, no background, grid or legends.</summary>
    public bool HaloPass { get; init; }
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
    /// <summary>Maps and lines of an analysis: drawn under <see cref="Overlays"/>, and cut to the visible blocks like the blast.</summary>
    public IReadOnlyList<IOverlay> AnalysisOverlays { get; init; } = Array.Empty<IOverlay>();
    /// <summary>When set, only holes fired at or before this time are drawn as fired (timing animation).</summary>
    public double? AnimationTimeMs { get; init; }
    /// <summary>Holes in this set are hidden (visibility manager / isolate).</summary>
    public ISet<Guid>? HiddenEntities { get; init; }
    /// <summary>Optional clipping band (section and slice views): only geometry within this distance of the camera target plane is drawn.</summary>
    public double? SliceHalfWidth { get; init; }
    /// <summary>Space kept free at the top of the view for controls drawn over it (north arrow and summary block move below).</summary>
    public float TopInset { get; init; }
    /// <summary>Leave out surface wireframes (switched off, or the camera is moving and they wait until it stops).</summary>
    public bool HideWireframes { get; init; }
    /// <summary>Surface wireframes on selected surfaces only.</summary>
    public bool WireframesOnSelected { get; init; }
    /// <summary>The coordinate grid whatever the view says; null follows the view.</summary>
    public bool? ShowGrid { get; init; }
    /// <summary>Only the blast is drawn: holes, initiation points and ties, the ties also when the view has them switched off.</summary>
    public bool HideGeometry { get; init; }
    /// <summary>Objects drawn shifted by <see cref="MoveOffset"/> (live preview while they are dragged).</summary>
    public ISet<Guid>? MovedEntities { get; init; }
    public Vec3 MoveOffset { get; init; }
    /// <summary>The block under the cursor: drawn with all its edges, not only the short ones at its corners.</summary>
    public Guid? HotBlock { get; init; }
    /// <summary>The camera is moving: what is behind a surface is not worked out until it stops (<see cref="ViewState.FadeHidden"/>).</summary>
    public bool CameraMoving { get; init; }
    /// <summary>Colour of drawing objects (lines, points, polygons, text…) without their own colour; null uses the layer colours.</summary>
    public SKColor? GeometryColor { get; init; }
    /// <summary>Colour of section lines without their own colour, before <see cref="GeometryColor"/>; null treats them as other lines.</summary>
    public SKColor? SectionLineColor { get; init; }
    /// <summary>Surface fill by kind; null fills every surface with the elevation ramp.</summary>
    public IReadOnlyDictionary<SurfaceKind, SKColor>? SurfaceColors { get; init; }
    /// <summary>Alpha of the surface fill; null takes the one for the background (light or dark).</summary>
    public byte? SurfaceAlpha { get; init; }
    /// <summary>Alpha of the surface wireframe; null takes the default.</summary>
    public byte? WireframeAlpha { get; init; }
    /// <summary>Alpha of the points of a point cloud; null takes the one for the background.</summary>
    public byte? PointCloudAlpha { get; init; }
}

/// <summary>Draws a blast project with SkiaSharp. Used by the interactive viewport, image copy, printing and reports.</summary>
public static class PlanRenderer
{
    /// <summary>Translation hook for text drawn on the plan and in reports (the UI sets it to its localiser).</summary>
    public static Func<string, string> Translate { get; set; } = s => s;

    private static string T(string s) => Translate(s);
    private static string F(string format, params object[] args) => string.Format(Translate(format), args);

    [ThreadStatic] private static bool _dark;
    /// <summary>Collars coloured by a calculated property this frame: the property and the range of its values over the drawn holes.</summary>
    [ThreadStatic] private static (string Id, double Min, double Max)? _colorBy;

    private static (string, double, double)? ColorRange(BlastProject project, List<Hole> holes)
    {
        if (project.View.ColorBy is not { Length: > 0 } id) return null;
        double min = double.MaxValue, max = double.MinValue;
        foreach (var h in holes)
            if (CalcService.Value(project, id, h) is { IsError: false, Object: null } v && !double.IsInfinity(v.Number))
            {
                min = Math.Min(min, v.Number);
                max = Math.Max(max, v.Number);
            }
        return min <= max ? (id, min, max) : null;
    }

    /// <summary>The colour of a hole: by its type, or on the blue-to-red ramp of the calculated property chosen to colour by (grey without a value).</summary>
    private static SKColor HoleColour(BlastProject project, Hole h)
    {
        if (_colorBy is { } c)
        {
            var v = CalcService.Value(project, c.Id, h);
            if (v.IsError || v.Object != null || double.IsInfinity(v.Number)) return new SKColor(0xFF9CA3AF);
            return ColorRamp.Heat(c.Max - c.Min < 1e-12 ? 0.5 : (v.Number - c.Min) / (c.Max - c.Min));
        }
        return Adapt(new SKColor(project.Design.HoleTypes.FirstOrDefault(t => t.Index == h.HoleType)?.Color ?? 0xFF1E3A8A));
    }
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
        if (!opt.HaloPass)
            using (var bg = new SKPaint { Color = opt.Background, Style = SKPaintStyle.Fill })
                canvas.DrawRect(0, 0, (float)cam.Width, (float)cam.Height, bg);
        var view = project.View;
        var visibleLayers = project.Layers.Where(l => l.Visible).Select(l => l.Id).ToHashSet();
        var layerById = project.Layers.ToDictionary(l => l.Id);
        // the visible blocks: only what is inside them is drawn (lines and surfaces are cut below)
        var clip = BlockClip.Of(project);
        bool Visible(Entity e) => visibleLayers.Contains(e.LayerId) && (opt.HiddenEntities == null || !opt.HiddenEntities.Contains(e.Id)) && InSlice(e)
            && (clip == null || e is PolylineEntity or CircleEntity or SurfaceEntity || clip.Shows(e));
        bool InSlice(Entity e)
        {
            // a surface is cut to the band when it is drawn: its triangles can cross the band with no vertex in it
            if (opt.SliceHalfWidth is not double w || e is SurfaceEntity) return true;
            foreach (var v in e.GetVertices()) if (Math.Abs(cam.Depth(v)) <= w) return true;
            return false;
        }
        SKColor ColorOf(Entity e)
        {
            if (opt.SectionLineColor is SKColor section && e.Color == null && e is PolylineEntity { Kind: LineKind.CrossSection })
                return Adapt(section);
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

        if (!opt.HaloPass && (opt.ShowGrid ?? view.ShowGrid)) DrawGrid(canvas, cam, stroke, text);
        // a sheet of a section asks for its grid: the views on the screen go by the switch of the plan, and have none
        if (!opt.HaloPass && opt.ShowGrid == true && opt.SliceHalfWidth != null && cam.Pitch == 0) DrawSectionGrid(canvas, cam, stroke, text);

        var entities = project.Entities.Where(e => Visible(e) && (!opt.HideGeometry || e is Hole or InitiationPoint or TieEntity) && (!opt.HaloPass || opt.Selection.Contains(e.Id))).ToList();
        if (clip != null) entities = entities.SelectMany(e => CutByBlocks(clip, e)).ToList();
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

        // The depth of the surfaces on the screen (a z-buffer) tells what is behind a surface. In 3D a line behind one is
        // not drawn, or with the eye (View ▸ hidden paler) drawn at a quarter of the opacity of the surfaces, which then
        // also go paler behind each other. In the plan lines and points stay as they are (a 2D drawing at 0 would go under
        // every surface): only the surfaces go paler with the eye. Worked out for every frame, coarser while the camera moves.
        // the halo pass has no surfaces of its own (their outer edges only, at the end): no depth, no order to sort
        var shownSurfaces = view.ShowSurfaces && !opt.HaloPass ? entities.OfType<SurfaceEntity>().ToList() : new List<SurfaceEntity>();
        var hiddenAlpha = (byte)((opt.SurfaceAlpha ?? (byte)(_dark ? 70 : 150)) / 4);
        // while the camera turns or zooms the map is coarser (quicker), and fine again once it stops
        var depth = (view.FadeHidden || !cam.IsPlan || shownSurfaces.Count > 1) && shownSurfaces.Count > 0
            ? DepthMap.Of(cam, shownSurfaces, GeometryFingerprint, opt.CameraMoving ? DepthMap.MovingCell : DepthMap.Cell) : null;
        var lineDepth = cam.IsPlan ? null : depth;
        var paleLines = view.FadeHidden;
        // Surfaces whole, far to near by their nearest point; then the lines by their segments, far to near.
        var batched = new HashSet<Entity>();
        // the halo pass draws its few lines one by one with the other objects, and leaves the order of the scene as it is
        if (!opt.HaloPass) DrawSurfacesAndLines();

        // Plain point crosses go into one batch per colour and width: thousands of survey points as two lines each would
        // be thousands of draw calls a frame, the batch is one.
        var crosses = new Dictionary<(SKColor Color, float Width), List<SKPoint>>();
        var pointLabels = new TextBatch();
        float viewW = (float)cam.Width, viewH = (float)cam.Height;
        foreach (var e in entities)
        {
            if (e is PointEntity pe && IsCross(pe.Kind) && (moved == null || !moved.Contains(e.Id)))
            {
                var (x, y) = cam.ToScreen(pe.Position);
                if (x < -10 || y < -10 || x > viewW + 10 || y > viewH + 10) continue;
                var selected = opt.Selection.Contains(e.Id);
                var crossColor = selected ? opt.SelectionColor : ColorOf(e);
                if (lineDepth != null && paleLines && !selected && lineDepth.Hidden(x, y, (float)cam.Depth(pe.Position))) crossColor = crossColor.WithAlpha(Math.Min(crossColor.Alpha, hiddenAlpha));
                var key = (crossColor, (float)((layerById.TryGetValue(e.LayerId, out var pl) ? pl.LineWidth : 1) + (selected ? opt.SelectionExtra : 0)));
                if (!crosses.TryGetValue(key, out var list)) crosses[key] = list = new List<SKPoint>();
                // on pixel centres: the crosses are drawn without antialiasing and stay sharp
                float cx = MathF.Floor(x) + 0.5f, cy = MathF.Floor(y) + 0.5f;
                list.Add(new SKPoint(cx - 4, cy)); list.Add(new SKPoint(cx + 5, cy));
                list.Add(new SKPoint(cx, cy - 4)); list.Add(new SKPoint(cx, cy + 5));
                if (!string.IsNullOrEmpty(pe.Label) && view.ShowText && (pl?.ShowText ?? true))
                    pointLabels.Add(pe.Label, x + 6, y - 6, key.Item1, text.TextSize);
                continue;
            }
            var mc = BeginMove(e);
            DrawEntity(e);
            EndMove(mc);
        }
        // Without antialiasing: the GPU draws such lines directly, while antialiased ones are built on the CPU each frame,
        // which for a few thousand survey points took most of the frame. Horizontal and vertical lines lose nothing.
        stroke.IsAntialias = false;
        foreach (var ((color, width), list) in crosses)
        {
            stroke.Color = color;
            stroke.StrokeWidth = width;
            canvas.DrawPoints(SKPointMode.Lines, list.ToArray(), stroke);
        }
        stroke.IsAntialias = true;
        pointLabels.Flush(canvas, text);

        void DrawSurfacesAndLines()
        {
            var tv = cam.ToViewer;
            var tg = cam.Target;
            float DepthOf(Vec3 p) => (float)((p.X - tg.X) * tv.X + (p.Y - tg.Y) * tv.Y + (p.Z - tg.Z) * tv.Z);
            var lines = new List<Entity>();
            foreach (var e in entities)
                // a dragged line goes with the canvas moved under it: drawn whole with the other objects
                if (e is PolylineEntity { Points.Count: >= 2 } or CircleEntity && moved?.Contains(e.Id) != true) lines.Add(e);
            foreach (var e in lines) batched.Add(e);
            // the order is kept while the lines and the surfaces stay and the camera turns by less than a couple of degrees:
            // moving and zooming do not change it
            var (items, circles) = DrawOrder(cam, shownSurfaces, lines, () =>
            {
                var list = new List<(float Key, Entity E, int Seg)>();
                var polys = new Dictionary<CircleEntity, List<Vec3>>();
                foreach (var s in shownSurfaces)
                {
                    var near = float.NegativeInfinity;
                    foreach (var v in s.Vertices) { var d = DepthOf(v); if (d > near) near = d; }
                    list.Add((near, s, -1));
                }
                foreach (var e in lines)
                {
                    IReadOnlyList<Vec3> pts;
                    var closed = true;
                    if (e is PolylineEntity pl) { pts = pl.Points; closed = pl.Closed; }
                    else { var poly = ((CircleEntity)e).ToPolygon(48); polys[(CircleEntity)e] = poly; pts = poly; }
                    if (e is PolylineEntity { Closed: true, Kind: LineKind.ClearanceZone } zone)
                        list.Add((zone.Points.Select(DepthOf).Average(), e, -2));
                    var n = closed ? pts.Count : pts.Count - 1;
                    for (int i = 0; i < n; i++)
                        list.Add(((DepthOf(pts[i]) + DepthOf(pts[(i + 1) % pts.Count])) / 2, e, i));
                }
                // the surfaces first, the lines over them (what of a line is behind a surface the depth map tells)
                list.Sort((a, b) => (a.Seg == -1) != (b.Seg == -1) ? (a.Seg == -1 ? -1 : 1) : a.Key.CompareTo(b.Key));
                return (list, polys);
            });

            // consecutive segments of one look go into one path: a survey is a few draws, not one a segment
            using var shown = new SKPath();
            using var hidden = new SKPath();
            (SKColor Color, float Width)? look = null;
            void Flush()
            {
                if (look is not { } l) return;
                stroke.StrokeWidth = l.Width;
                if (!shown.IsEmpty) { stroke.Color = l.Color; canvas.DrawPath(shown, stroke); shown.Reset(); }
                // behind a surface: paler with the eye, else not drawn
                if (!hidden.IsEmpty) { if (paleLines) { stroke.Color = l.Color.WithAlpha(Math.Min(l.Color.Alpha, hiddenAlpha)); canvas.DrawPath(hidden, stroke); } hidden.Reset(); }
            }
            float w = (float)cam.Width, h = (float)cam.Height;
            foreach (var (_, e, seg) in items)
            {
                if (seg == -1)
                {
                    Flush();
                    var s = (SurfaceEntity)e;
                    var mc = BeginMove(s);
                    // behind another surface it goes paler cell by cell: drawn on a layer of its own, then masked
                    // behind another surface: a quarter with the eye, else not drawn there at all (a large surface whole in the
                    // far-to-near order would otherwise cover a nearer one)
                    var mask = shownSurfaces.Count > 1 ? depth?.MaskFor(s, view.FadeHidden ? (byte)64 : (byte)0) : null;
                    if (mask != null) canvas.SaveLayer();
                    DrawSurface(canvas, cam, s, ColorOf(s), opt.Selection.Contains(s.Id) ? opt.SelectionColor : (SKColor?)null, SurfaceFill(s, opt), range, opt.HideWireframes || (opt.WireframesOnSelected && !opt.Selection.Contains(s.Id)), opt.SliceHalfWidth, opt);
                    if (mask != null)
                    {
                        using var dstIn = new SKPaint { BlendMode = SKBlendMode.DstIn, FilterQuality = SKFilterQuality.Low };
                        canvas.DrawImage(mask, depth!.ScreenRect, dstIn);
                        canvas.Restore();
                    }
                    EndMove(mc);
                    continue;
                }
                var selected = opt.Selection.Contains(e.Id);
                var color = selected ? opt.SelectionColor : ColorOf(e);
                if (seg == -2)
                {
                    Flush();
                    fill.Color = color.WithAlpha(30);
                    DrawPolygonFill(canvas, cam, ((PolylineEntity)e).Points, fill);
                    continue;
                }
                var width = (float)((layerById.TryGetValue(e.LayerId, out var lay) ? lay.LineWidth : 1) * (e is PolylineEntity { Kind: LineKind.CrossSection } ? 2 : 1) + (selected ? opt.SelectionExtra : 0));
                if (look != (color, width)) { Flush(); look = (color, width); }
                IReadOnlyList<Vec3> pts = e is CircleEntity c ? circles[c] : ((PolylineEntity)e).Points;
                Vec3 a = pts[seg], b = pts[(seg + 1) % pts.Count];
                var (ax, ay) = cam.ToScreen(a);
                var (bx, by) = cam.ToScreen(b);
                if ((ax < -4 && bx < -4) || (ay < -4 && by < -4) || (ax > w + 4 && bx > w + 4) || (ay > h + 4 && by > h + 4)) continue;
                if (lineDepth == null || selected) { shown.MoveTo(ax, ay); shown.LineTo(bx, by); continue; }
                // behind a surface the segment goes paler: cut where it goes behind and comes out again
                float da = DepthOf(a), db = DepthOf(b);
                var steps = Math.Clamp((int)(MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) / 4), 1, 128);
                bool? was = null;
                for (int k = 0; k <= steps; k++)
                {
                    float t = (float)k / steps;
                    float x = ax + (bx - ax) * t, y = ay + (by - ay) * t;
                    var behind = lineDepth.Hidden(x, y, da + (db - da) * t);
                    if (was == null) (behind ? hidden : shown).MoveTo(x, y);
                    else if (behind == was) (behind ? hidden : shown).LineTo(x, y);
                    else
                    {
                        // the change halfway between the samples
                        float tm = (k - 0.5f) / steps, mx = ax + (bx - ax) * tm, my = ay + (by - ay) * tm;
                        (was.Value ? hidden : shown).LineTo(mx, my);
                        (behind ? hidden : shown).MoveTo(mx, my);
                        (behind ? hidden : shown).LineTo(x, y);
                    }
                    was = behind;
                }
            }
            Flush();
            stroke.StrokeWidth = 1;
        }

        void DrawEntity(Entity e)
        {
            var selected = opt.Selection.Contains(e.Id);
            var color = selected ? opt.SelectionColor : ColorOf(e);
            // an object behind a surface goes paler with it
            if (lineDepth != null && paleLines && !selected && e is PointEntity or TextEntity or ArrowEntity or DimensionEntity && lineDepth.Hidden(cam, e.GetVertices().First()))
                color = color.WithAlpha(Math.Min(color.Alpha, hiddenAlpha));
            stroke.Color = color;
            // a selected object a couple of pixels thicker than its line; a section line, twice as thick, too
            var lineWidth = layerById.TryGetValue(e.LayerId, out var lay) ? lay.LineWidth : 1;
            if (e is PolylineEntity { Kind: LineKind.CrossSection }) lineWidth *= 2;
            stroke.StrokeWidth = (float)(lineWidth + (selected ? opt.SelectionExtra : 0));
            fill.Color = color;
            text.Color = color;
            var showText = view.ShowText && (lay?.ShowText ?? true);
            switch (e)
            {
                case PolylineEntity pl:
                    // drawn with the surfaces, far to near; here only a dragged one, and the label of a contour
                    if (!batched.Contains(pl))
                    {
                        DrawPolyline(canvas, cam, pl.Points, pl.Closed, stroke);
                        if (pl.Closed && pl.Kind == LineKind.ClearanceZone && !opt.HaloPass)
                        {
                            fill.Color = color.WithAlpha(30);
                            DrawPolygonFill(canvas, cam, pl.Points, fill);
                        }
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
                    // off the view whichever way it is turned: no farther than its length from the anchor
                    var reach = text.TextSize * (t.Text.Length + 1);
                    if (x < -reach || y < -reach || x > viewW + reach || y > viewH + reach) { text.TextSize = 11; break; }
                    canvas.Save();
                    canvas.RotateDegrees((float)-t.Rotation, x, y);
                    canvas.DrawText(t.Text, x, y, text);
                    canvas.Restore();
                    text.TextSize = 11;
                    break;
                }
                case CircleEntity c:
                    if (!batched.Contains(c)) DrawPolyline(canvas, cam, c.ToPolygon(48), true, stroke);
                    break;
                case BlockEntity b:
                    DrawBlock(canvas, cam, b, stroke, selected, opt.HotBlock == b.Id);
                    break;
                case ArrowEntity a:
                    DrawArrow(canvas, cam, a.Start, a.End, stroke, fill, 10);
                    break;
                case DimensionEntity d:
                    DrawDimension(canvas, cam, d, stroke, text);
                    break;
                case InitiationPoint:
                    // drawn after the holes: an initiation point usually sits on one
                    break;
                case EbsOpeningPoint op:
                    DrawOpeningPoint(canvas, cam, op, selected ? color : Adapt(new SKColor(0xFF00897B)), text);
                    break;
                case TimingLineEntity tl:
                    DrawTimingLine(canvas, cam, tl, selected || e.Color != null ? color : (SKColor?)null, text);
                    // a selected chevron of the front tool shows its ray, as long as its sides
                    if (selected && tl.Front == TimingFrontShape.Chevron && tl.Points.Count == 3)
                    {
                        DrawChevronRay(canvas, cam, tl.Points, color, Chevron.Length(tl.Points));
                        DrawChevronOval(canvas, cam, tl.Points, color);
                    }
                    break;
            }
        }

        // Ties and rows refer to holes by id: one lookup table per frame, instead of searching the whole project for
        // each end of each tie (with a survey under the blast that was most of the frame).
        Dictionary<Guid, Entity>? byId = null;
        var showTies = view.ShowTies || opt.HideGeometry;
        var showRows = view.ShowRows && !opt.HideGeometry && !opt.HaloPass;
        if (showTies || showRows)
        {
            byId = new Dictionary<Guid, Entity>(project.Entities.Count);
            // with blocks the rows run through the holes shown only
            foreach (var e in clip == null ? project.Entities : entities) byId.TryAdd(e.Id, e);
        }
        if (showTies) DrawTies(canvas, cam, project, entities.OfType<TieEntity>(), byId!, opt, text);
        if (showRows) DrawRows(canvas, cam, project, byId!, stroke);

        var holes = entities.OfType<Hole>().Where(h => HoleVisible(view, h)).ToList();
        if (!cam.IsPlan) holes = holes.OrderBy(h => cam.Depth(h.Collar)).ToList();
        if (view.ShowDipTrack || view.ShowActualTrack || view.ShowDeviation || view.ShowGradePoint || view.ShowProfile)
            foreach (var h in holes) { var mc = BeginMove(h); DrawHoleTracks(canvas, cam, h, view); EndMove(mc); }
        // Depth lines and charge columns of all holes in one batch, under the collars: in 3D with the charge shown they
        // were three or four thick lines per hole, thousands of draws a frame. Dragged holes draw their own.
        // the halo pass keeps the colour range of the scene: the selected holes alone would make another one
        if (!opt.HaloPass) _colorBy = ColorRange(project, holes);
        var columns = new LineMesh();
        foreach (var h in holes)
            if (moved?.Contains(h.Id) != true) DrawHoleColumn(canvas, cam, project, h, opt, fill, columns);
        columns.Flush(canvas);
        foreach (var h in holes)
        {
            var mc = BeginMove(h);
            if (mc >= 0) { var own = new LineMesh(); DrawHoleColumn(canvas, cam, project, h, opt, fill, own); own.Flush(canvas); }
            DrawHole(canvas, cam, project, h, opt, stroke, fill);
            EndMove(mc);
        }
        if (view.ShowBurdenMarker && !opt.HaloPass) DrawBurdenMarkers(canvas, cam, project, holes, text);
        foreach (var ip in entities.OfType<InitiationPoint>())
        {
            var mc = BeginMove(ip);
            DrawInitiationPoint(canvas, cam, ip, opt.Selection.Contains(ip.Id) ? opt.SelectionColor : ColorOf(ip), text, opt.Selection.Contains(ip.Id));
            EndMove(mc);
        }
        var holeLabels = new TextBatch();
        var labelSides = HoleLabelLayout.Sides(view);
        // the halo pass leaves the hole labels to the scene: they have their own outline, a second one made blots of them
        foreach (var h in opt.HaloPass ? new List<Hole>() : holes)
        {
            // labels of dragged holes follow them at once, the rest go into one batch
            if (moved?.Contains(h.Id) == true) { var mc = BeginMove(h); var own = new TextBatch(); DrawHoleLabels(cam, project, h, view, labelSides, opt, text, own); own.Flush(canvas, text, halo: true); EndMove(mc); }
            else DrawHoleLabels(cam, project, h, view, labelSides, opt, text, holeLabels);
        }
        // outlined: the labels read over the surfaces, whatever their colour
        holeLabels.Flush(canvas, text, halo: true);
        var productsKey = ProductsKey(project);
        foreach (var h in opt.HaloPass ? new List<Hole>() : holes)
        {
            if (!LoadingWarning(project, h, productsKey)) continue;
            var mc = BeginMove(h);
            DrawLoadingWarning(canvas, cam, h, opt, fill);
            EndMove(mc);
        }

        if (opt.HaloPass)
        {
            // a selected surface by its outer edge only: a halo round the whole mesh would cover it
            foreach (var s in entities.OfType<SurfaceEntity>())
            {
                var mc = BeginMove(s);
                DrawSurfaceEdge(canvas, cam, s, opt.SelectionColor);
                EndMove(mc);
            }
            return;
        }
        if (opt.SelectionHalo && opt.SelectionHaloPx > 0 && opt.Selection.Count > 0) DrawSelectionHalo(canvas, cam, project, opt);

        if (opt.AnalysisOverlays.Count > 0)
        {
            // the maps of an analysis are cut to the outline the blocks take on the screen
            var save = canvas.Save();
            if (clip != null) canvas.ClipPath(BlockOutline(cam, clip));
            foreach (var o in opt.AnalysisOverlays) o.Draw(canvas, cam, project);
            canvas.RestoreToCount(save);
        }
        foreach (var o in opt.Overlays)
            if (o is IMovePreviewOverlay mp) mp.Draw(canvas, cam, project, opt.MovedEntities, opt.MoveOffset);
            else o.Draw(canvas, cam, project);

        if (view.ShowAxes) DrawAxes(canvas, cam, stroke, text);
        if (opt.DrawNorthArrow) DrawNorth(canvas, cam, stroke, fill, text);
        if (opt.DrawScaleBar) DrawScaleBar(canvas, cam, stroke, text);
        if (opt.DrawElevationScale && opt.SurfaceColors == null && range is { } r)
        {
            DrawElevationScale(canvas, cam, r.Min, r.Max, text);
            text.TextSize = 11;
        }
        if (view.ShowHoleLegend && opt.DrawHoleLegend) DrawHoleLegend(canvas, project, holes, fill, text);
        if (view.ShowTieLegend && opt.DrawTieLegend) DrawTieLegend(canvas, cam, project, fill, text);
        if (opt.DrawSummaryBlock && project.Summary.Visible) DrawSummary(canvas, cam, project, fill, stroke, text);
    }

    /// <summary>The colour of the halo against the selection colour, as for the hole labels: dark round a light one, light round a dark one.</summary>
    public static SKColor HaloColor(SKColor selection) =>
        (0.2126 * selection.Red + 0.7152 * selection.Green + 0.0722 * selection.Blue) / 255 > 0.45 ? new SKColor(0, 0, 0, 200) : new SKColor(255, 255, 255, 215);

    /// <summary>
    /// The selected objects once more over the scene, on a layer whose filter puts a band in <see cref="HaloColor"/> round
    /// all that is drawn on it (the drawing grown by <see cref="SceneOptions.SelectionHaloPx"/>), and the drawing over the band.
    /// </summary>
    private static void DrawSelectionHalo(SKCanvas canvas, Camera cam, BlastProject project, SceneOptions opt)
    {
        var screen = new SKRect(0, 0, (float)cam.Width, (float)cam.Height);
        // the layer and its filter cover the selected objects only, with room for their labels; a dragged selection the view
        var box = SKRect.Empty;
        if (opt.MovedEntities is { Count: > 0 }) box = screen;
        else
            foreach (var e in project.Entities)
            {
                if (!opt.Selection.Contains(e.Id)) continue;
                var b = e.GetBounds();
                if (b.IsEmpty) continue;
                for (int i = 0; i < 8; i++)
                {
                    var (x, y) = cam.ToScreen(new Vec3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z));
                    box = box.IsEmpty ? new SKRect(x, y, x + 0.01f, y + 0.01f) : SKRect.Union(box, new SKRect(x, y, x + 0.01f, y + 0.01f));
                }
            }
        if (box.IsEmpty) return;
        box.Inflate(80, 40);
        if (!box.IntersectsWith(screen)) return;
        box.Intersect(screen);
        using var tint = SKColorFilter.CreateBlendMode(HaloColor(opt.SelectionColor), SKBlendMode.SrcIn);
        using var grown = SKImageFilter.CreateDilate(opt.SelectionHaloPx, opt.SelectionHaloPx);
        using var band = SKImageFilter.CreateColorFilter(tint, grown);
        using var self = SKImageFilter.CreateOffset(0, 0);
        using var both = SKImageFilter.CreateMerge(band, self);
        using var paint = new SKPaint { ImageFilter = both };
        canvas.SaveLayer(box, paint);
        Render(canvas, cam, project, opt with { HaloPass = true });
        canvas.Restore();
    }

    private sealed class EdgeCache { public int Key; public (int A, int B)[] Edges = Array.Empty<(int, int)>(); }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SurfaceEntity, EdgeCache> _edges = new();

    /// <summary>The outer edge of a surface: the sides of its triangles that no other triangle shares. Kept while the mesh stays.</summary>
    public static IReadOnlyList<(int A, int B)> OuterEdges(SurfaceEntity s)
    {
        var cache = _edges.GetOrCreateValue(s);
        var key = HashCode.Combine(GeometryFingerprint(s), s.Triangles.Count);
        lock (cache)
        {
            if (cache.Key == key && (cache.Edges.Length > 0 || s.Triangles.Count == 0)) return cache.Edges;
            var count = new Dictionary<(int, int), int>();
            var t = s.Triangles;
            for (int i = 0; i + 2 < t.Count; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = t[i + k], b = t[i + (k + 1) % 3];
                    var e = a < b ? (a, b) : (b, a);
                    count[e] = count.TryGetValue(e, out var n) ? n + 1 : 1;
                }
            cache.Edges = count.Where(x => x.Value == 1).Select(x => x.Key).ToArray();
            cache.Key = key;
            return cache.Edges;
        }
    }

    private static void DrawSurfaceEdge(SKCanvas canvas, Camera cam, SurfaceEntity s, SKColor color)
    {
        using var path = new SKPath();
        foreach (var (a, b) in OuterEdges(s))
        {
            if (a >= s.Vertices.Count || b >= s.Vertices.Count) continue;
            var (ax, ay) = cam.ToScreen(s.Vertices[a]);
            var (bx, by) = cam.ToScreen(s.Vertices[b]);
            path.MoveTo(ax, ay);
            path.LineTo(bx, by);
        }
        using var pen = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = color };
        canvas.DrawPath(path, pen);
    }

    /// <summary>What of an entity is drawn inside the blocks: itself, the parts of a line, the cut part of a surface, or nothing.</summary>
    private static IEnumerable<Entity> CutByBlocks(BlockClip clip, Entity e)
    {
        switch (e)
        {
            case PolylineEntity pl:
                foreach (var piece in clip.Pieces(pl.Points, pl.Closed)) yield return pl.Piece(piece);
                break;
            case CircleEntity c:
                foreach (var piece in clip.Pieces(c.ToPolygon(48), true))
                    yield return new PolylineEntity { Id = c.Id, LayerId = c.LayerId, Color = c.Color, GroupId = c.GroupId, Points = piece };
                break;
            case SurfaceEntity s:
                if (CutSurface(clip, s) is { } cut) yield return cut;
                break;
            default:
                yield return e;
                break;
        }
    }

    private sealed class OrderCache
    {
        public int Key;
        public double Yaw, Pitch;
        public bool Plan;
        public List<(float Key, Entity E, int Seg)> Items = new();
        public Dictionary<CircleEntity, List<Vec3>> Circles = new();
    }
    private static readonly object OrderGate = new();
    private static OrderCache? _order;

    /// <summary>
    /// The far-to-near order of the surfaces and the line segments: worked out again only when a line or a surface changes
    /// (its vertices, their count) or the camera turns by more than <see cref="SortTolerance"/> degrees; between, the order
    /// of the last frame. Panning and zooming keep it: depth along the line of sight does not change its order with them.
    /// </summary>
    private static (List<(float Key, Entity E, int Seg)> Items, Dictionary<CircleEntity, List<Vec3>> Circles) DrawOrder(
        Camera cam, IReadOnlyList<SurfaceEntity> surfaces, IReadOnlyList<Entity> lines,
        Func<(List<(float, Entity, int)>, Dictionary<CircleEntity, List<Vec3>>)> sort)
    {
        var key = new HashCode();
        foreach (var s in surfaces) { key.Add(s.Id); key.Add(GeometryFingerprint(s)); }
        foreach (var e in lines)
        {
            key.Add(e.Id);
            if (e is PolylineEntity pl)
            {
                key.Add(pl.Points.Count); key.Add(pl.Closed);
                // the ends and the middle: an edited or moved line changes one of them
                key.Add(pl.Points[0]); key.Add(pl.Points[^1]); key.Add(pl.Points[pl.Points.Count / 2]);
            }
            else if (e is CircleEntity c) { key.Add(c.Center); key.Add(c.Radius); }
        }
        var k = key.ToHashCode();
        lock (OrderGate)
        {
            if (_order is { } o && o.Key == k && o.Plan == cam.IsPlan
                && (cam.IsPlan || (Math.Abs(o.Yaw - cam.Yaw) < SortTolerance && Math.Abs(o.Pitch - cam.Pitch) < SortTolerance)))
                return (o.Items, o.Circles);
            var (items, circles) = sort();
            _order = new OrderCache { Key = k, Yaw = cam.Yaw, Pitch = cam.Pitch, Plan = cam.IsPlan, Items = items, Circles = circles };
            return (items, circles);
        }
    }

    private sealed class CutCache { public int Key; public SurfaceEntity? Cut; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SurfaceEntity, CutCache> _cuts = new();

    /// <summary>
    /// The part of a surface inside the blocks, the same object to the renderer (same id) for as long as neither the
    /// blocks nor the surface change; null when nothing of it is inside.
    /// </summary>
    public static SurfaceEntity? CutSurface(BlockClip clip, SurfaceEntity s)
    {
        if (!clip.Overlaps(s.GetBounds())) return null;
        var key = HashCode.Combine(clip.Key, GeometryFingerprint(s));
        var cache = _cuts.GetValue(s, _ => new CutCache { Key = ~key });
        lock (cache)
        {
            if (cache.Key != key)
            {
                var (v, t) = clip.Cut(s.Vertices, s.Triangles);
                cache.Cut = t.Count == 0 ? null : s.WithMesh(v, t);
                cache.Key = key;
            }
            return cache.Cut;
        }
    }

    /// <summary>Length of the short edges at a corner of a block that is neither selected nor under the cursor, px.</summary>
    public const float BlockStubPx = 10;
    /// <summary>Opacity of the short edges at the corners of a block (80 % transparent), and of all its edges under the cursor.</summary>
    public const byte BlockStubAlpha = 51, BlockHotAlpha = 180;

    /// <summary>
    /// The short edges at the corners of a block on the screen: from each corner along each of its three edges,
    /// <see cref="BlockStubPx"/> long, and no longer than a quarter of the edge (a block small on the screen keeps its
    /// corners apart). Picking goes by the same segments.
    /// </summary>
    public static List<(SKPoint A, SKPoint B)> BlockStubs(Camera cam, BlockEntity b)
    {
        var corners = b.Corners().Select(v => { var (x, y) = cam.ToScreen(v); return new SKPoint(x, y); }).ToArray();
        var stubs = new List<(SKPoint, SKPoint)>(24);
        foreach (var (i, j) in BlockEntity.Edges)
        {
            var d = corners[j] - corners[i];
            var len = d.Length;
            if (len < 0.5f) continue;
            var k = Math.Min(BlockStubPx, len / 4) / len;
            var step = new SKPoint(d.X * k, d.Y * k);
            stubs.Add((corners[i], corners[i] + step));
            stubs.Add((corners[j], corners[j] - step));
        }
        return stubs;
    }

    /// <summary>
    /// A block: selected, its twelve edges in the selection colour; under the cursor, its twelve edges see-through; else
    /// only the short edges at its corners, so it shows where it is without crossing what it shows.
    /// </summary>
    private static void DrawBlock(SKCanvas c, Camera cam, BlockEntity b, SKPaint stroke, bool selected, bool hot)
    {
        var width = stroke.StrokeWidth;
        var color = stroke.Color;
        using var path = new SKPath();
        if (selected || hot)
        {
            var corners = b.Corners();
            foreach (var (i, j) in BlockEntity.Edges)
            {
                var (x0, y0) = cam.ToScreen(corners[i]);
                var (x1, y1) = cam.ToScreen(corners[j]);
                path.MoveTo(x0, y0);
                path.LineTo(x1, y1);
            }
            if (!selected) stroke.Color = color.WithAlpha(BlockHotAlpha);
        }
        else
        {
            foreach (var (a, e) in BlockStubs(cam, b)) { path.MoveTo(a); path.LineTo(e); }
            stroke.Color = color.WithAlpha(BlockStubAlpha);
            stroke.StrokeWidth = 1.5f;
        }
        c.DrawPath(path, stroke);
        stroke.Color = color;
        stroke.StrokeWidth = width;
    }

    /// <summary>The screen outline of the visible blocks (each block's silhouette, the hull of its corners).</summary>
    private static SKPath BlockOutline(Camera cam, BlockClip clip)
    {
        var path = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var b in clip.Blocks)
        {
            var hull = Hull(b.Corners().Select(v => { var (x, y) = cam.ToScreen(v); return new SKPoint(x, y); }).ToList());
            if (hull.Count < 3) continue;
            path.AddPoly(hull.ToArray(), true);
        }
        return path;
    }

    /// <summary>Convex hull of screen points (monotone chain).</summary>
    private static List<SKPoint> Hull(List<SKPoint> pts)
    {
        pts = pts.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3) return pts;
        static float Cross(SKPoint o, SKPoint a, SKPoint b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new List<SKPoint>();
        foreach (var pass in new[] { pts, Enumerable.Reverse(pts).ToList() })
        {
            var start = h.Count;
            foreach (var p in pass)
            {
                while (h.Count >= start + 2 && Cross(h[^2], h[^1], p) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(p);
            }
            h.RemoveAt(h.Count - 1);
        }
        return h;
    }

    private sealed class WarningCache { public int Key; public bool Warning; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Hole, WarningCache> _warnings = new();

    /// <summary>
    /// The loading check of a hole, kept until its charge column, length, water, diameter or the products change: run
    /// for every hole on every frame it took more time than drawing the holes.
    /// </summary>
    private static bool LoadingWarning(BlastProject project, Hole h, int productsKey)
    {
        if (h.Decks.Count == 0) return false;
        var key = new HashCode();
        key.Add(productsKey);
        key.Add(Charging.MinStemmingDiameters);
        key.Add(h.Length); key.Add(h.ActualLength); key.Add(h.WaterDepth); key.Add(h.Diameter); key.Add(h.BoreTrack?.Count);
        foreach (var d in h.Decks) { key.Add(d.Top); key.Add(d.Bottom); key.Add(d.ProductId); key.Add(d.Primers.Count); }
        var k = key.ToHashCode();
        var cache = _warnings.GetValue(h, _ => new WarningCache { Key = ~k });
        lock (cache)
        {
            if (cache.Key != k) { cache.Warning = Charging.HasLoadingWarning(project, h); cache.Key = k; }
            return cache.Warning;
        }
    }

    /// <summary>What of the products the loading check depends on.</summary>
    private static int ProductsKey(BlastProject project)
    {
        var key = new HashCode();
        foreach (var p in project.Products) { key.Add(p.Id); key.Add(p.Kind); key.Add(p.IsExplosive); key.Add(p.WaterResistant); }
        return key.ToHashCode();
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
        // projected into one array and handed over in one call (a native call per vertex added up over a survey);
        // a line entirely outside the view is not drawn at all
        var screen = new SKPoint[pts.Count];
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < screen.Length; i++)
        {
            var (x, y) = cam.ToScreen(pts[i]);
            screen[i] = new SKPoint(x, y);
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        var pad = paint.StrokeWidth + 2;
        if (maxX < -pad || maxY < -pad || minX > cam.Width + pad || minY > cam.Height + pad) return;
        using var path = new SKPath();
        path.AddPoly(screen, closed);
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

    /// <summary>Point kinds drawn as a plain cross (batched by <see cref="Render"/>).</summary>
    private static bool IsCross(PointKind kind) => kind is not (PointKind.Monitoring or PointKind.GradePeg or PointKind.SurveyStation or PointKind.NorthPoint);

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

    /// <summary>Own colour of a timing line, as drawn when not selected: its colour override, else the colour of its style.</summary>
    /// <summary>
    /// An EBS opening point: a four-pointed star with two rays, along the burden and along the spacing, each as long as the
    /// distance it stands for and marked with its delay and that distance ("42ms / 4 m").
    /// </summary>
    public static void DrawOpeningPoint(SKCanvas c, Camera cam, EbsOpeningPoint op, SKColor color, SKPaint text)
    {
        var (x, y) = cam.ToScreen(op.Position);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, Color = color };
        using var head = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        using (var star = new SKPath())
        {
            const float r = 9, w = 2.6f;
            star.MoveTo(x, y - r); star.LineTo(x + w, y - w); star.LineTo(x + r, y); star.LineTo(x + w, y + w);
            star.LineTo(x, y + r); star.LineTo(x - w, y + w); star.LineTo(x - r, y); star.LineTo(x - w, y - w); star.Close();
            c.DrawPath(star, fill);
        }
        var label = text.Color;
        text.Color = color;
        foreach (var (dir, length, delay) in new[] { (op.BurdenDir, op.BurdenM, op.BurdenDelayMs), (op.SpacingDir, op.SpacingM, op.SpacingDelayMs) })
        {
            if (length <= 0) continue;
            var end = op.Position + dir * length;
            DrawArrow(c, cam, op.Position, end, line, head, 7);
            var (ex, ey) = cam.ToScreen(end);
            c.DrawText(FormattableString.Invariant($"{delay:0.#}ms / {length:0.##} m"), ex + 5, ey - 4, text);
        }
        text.Color = label;
    }

    public static SKColor TimingLineColor(TimingLineEntity tl) =>
        Adapt(tl.Color is uint own ? new SKColor(own) : tl.Style == TimingLineStyle.BurnLine ? new SKColor(0xFF808000) : new SKColor(0xFF008000));

    /// <summary>
    /// Ray of a two-segment front (<see cref="Chevron"/>): dashed, from the vertex outwards along the axis. On a selected front it
    /// is as long as the sides, while the front is created or turned as long as the view is wide; an unselected one has none.
    /// </summary>
    /// <summary>
    /// The oval of a chevron's delays, thin and dashed: its centre at the vertex, through both ends (its segments are its
    /// semi-diameters). The delay a hole gets is the oval's radius toward it, in the chevron's scale.
    /// </summary>
    public static void DrawChevronOval(SKCanvas c, Camera cam, IReadOnlyList<Vec3> points, SKColor color)
    {
        if (points.Count != 3) return;
        using var dashed = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = color.WithAlpha(170), PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0) };
        DrawPolyline(c, cam, Chevron.Oval(points), true, dashed);
    }

    public static void DrawChevronRay(SKCanvas c, Camera cam, IReadOnlyList<Vec3> points, SKColor color, double? length = null)
    {
        if (points.Count != 3) return;
        var apex = points[1];
        length ??= cam.Width / Math.Max(cam.Scale, 1e-9);
        using var dashed = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = color, PathEffect = SKPathEffect.CreateDash(new[] { 8f, 5f }, 0) };
        DrawPolyline(c, cam, new[] { apex, apex + Chevron.RayDir(points) * length.Value }, false, dashed);
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
        var color = colorOverride ?? TimingLineColor(tl);
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

    /// <summary>Outer radius of the initiation point star, px: large enough to stick out from under the hole it sits on.</summary>
    public const float InitiationStarRadius = 16;
    /// <summary>Radius of the see-through middle of the star, px: the collar of the hole under it shows (and is picked) there.</summary>
    public const float InitiationStarHole = 5.5f;

    private static void DrawInitiationPoint(SKCanvas c, Camera cam, InitiationPoint ip, SKColor color, SKPaint text, bool selected = false)
    {
        var (x, y) = cam.ToScreen(ip.Position);
        using var p = new SKPaint { IsAntialias = true, Color = selected ? color : Adapt(ip.IsElectronic ? new SKColor(0xFF00897B) : new SKColor(0xFFDC2626)), Style = SKPaintStyle.Fill };
        using var path = new SKPath();
        for (int i = 0; i < 10; i++)
        {
            var r = i % 2 == 0 ? InitiationStarRadius : InitiationStarRadius * 0.44f;
            var a = -Math.PI / 2 + i * Math.PI / 5;
            var px = x + (float)(r * Math.Cos(a));
            var py = y + (float)(r * Math.Sin(a));
            if (i == 0) path.MoveTo(px, py); else path.LineTo(px, py);
        }
        path.Close();
        // a see-through middle, so the hole the point sits on stays visible
        path.AddCircle(x, y, InitiationStarHole);
        path.FillType = SKPathFillType.EvenOdd;
        c.DrawPath(path, p);
        text.Color = p.Color;
        c.DrawText($"{ip.Label} {ip.StartTimeMs:0} {T("ms")}", x + InitiationStarRadius + 2, y - 10, text);
        text.Color = color;
    }

    /// <summary>Per-surface render cache: shaded colours (geometry + theme only) and reusable per-frame buffers.</summary>
    private sealed class SurfaceMesh
    {
        public int Fingerprint;
        public int TriCount = -1;
        public bool Dark;
        /// <summary>Alpha the fill colours were made with.</summary>
        public byte Alpha;
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
        /// <summary>Camera direction <see cref="Order"/> was sorted for.</summary>
        public double SortYaw = double.NaN, SortPitch = double.NaN;
        /// <summary>Vertex index pairs of the wireframe, each edge once (shared triangle edges were drawn twice).</summary>
        public int[] Edges = Array.Empty<int>();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SurfaceEntity, SurfaceMesh> _surfaceMeshes = new();

    /// <summary>Degrees the camera may turn before a surface is sorted far-to-near again.</summary>
    private const double SortTolerance = 2;

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

    private static void DrawSurface(SKCanvas c, Camera cam, SurfaceEntity s, SKColor color, SKColor? selected, SKColor? fill, (double Min, double Max)? range = null, bool hideWireframe = false, double? slice = null, SceneOptions? opt = null, DepthMap? depth = null)
    {
        var alphas = (Fill: opt?.SurfaceAlpha ?? (byte)(_dark ? 70 : 150), Wire: opt?.WireframeAlpha ?? (byte)110, Points: opt?.PointCloudAlpha ?? (_dark ? (byte)200 : (byte)220));
        // A point cloud coloured by kind shows its measured points, not a mesh made up between them.
        if (fill is SKColor pc && s.Kind == SurfaceKind.PointCloud)
        {
            DrawPointCloud(c, cam, s, selected ?? pc, slice, alphas.Points);
            return;
        }
        int tri = s.TriangleCount;
        if (tri == 0) return;

        var mesh = _surfaceMeshes.GetValue(s, _ => new SurfaceMesh());
        lock (mesh) DrawSurfaceMesh(c, cam, s, mesh, tri, fill is SKColor f ? Shade(f, 0.62) : color, selected, fill, range, hideWireframe, slice, alphas.Fill, alphas.Wire, depth);
    }

    private static void DrawPointCloud(SKCanvas c, Camera cam, SurfaceEntity s, SKColor color, double? slice, byte alpha)
    {
        var shown = slice is double band ? s.Vertices.Where(v => Math.Abs(cam.Depth(v)) <= band).ToList() : s.Vertices;
        if (shown.Count == 0) return;
        var pts = new SKPoint[shown.Count];
        for (int i = 0; i < pts.Length; i++)
        {
            var (x, y) = cam.ToScreen(shown[i]);
            pts[i] = new SKPoint(x, y);
        }
        using var paint = new SKPaint { IsAntialias = true, Color = color.WithAlpha(alpha), StrokeWidth = pts.Length > 50_000 ? 1.5f : 2.5f, StrokeCap = SKStrokeCap.Round };
        c.DrawPoints(SKPointMode.Points, pts, paint);
    }

    private static void DrawSurfaceMesh(SKCanvas c, Camera cam, SurfaceEntity s, SurfaceMesh mesh, int tri, SKColor color, SKColor? selected, SKColor? fill, (double Min, double Max)? range, bool hideWireframe, double? slice, byte alpha, byte wireAlpha, DepthMap? depth = null)
    {
        // The per-triangle fill colour depends only on the geometry, the theme, the fill and the shared elevation range,
        // so compute the expensive normal shading and colouring once and reuse it; only the projection runs per frame.
        var fingerprint = GeometryFingerprint(s);
        var fillKey = fill is SKColor fk ? (uint)fk | 1u : 0u;
        var rMin = range?.Min ?? double.NaN;
        var rMax = range?.Max ?? double.NaN;
        if (mesh.TriCount != tri || mesh.Dark != _dark || mesh.Alpha != alpha || mesh.Fingerprint != fingerprint || mesh.Fill != fillKey
            || !mesh.RangeMin.Equals(rMin) || !mesh.RangeMax.Equals(rMax))
        {
            mesh.Fingerprint = fingerprint;
            mesh.TriCount = tri;
            mesh.Dark = _dark;
            mesh.Alpha = alpha;
            mesh.Fill = fillKey;
            mesh.RangeMin = rMin;
            mesh.RangeMax = rMax;
            mesh.SoupColors = new SKColor[tri * 3];
            mesh.Points = new SKPoint[tri * 3];
            mesh.Ordered = Array.Empty<SKPoint>();
            mesh.OrderedColors = Array.Empty<SKColor>();
            mesh.Order = Array.Empty<int>();
            mesh.Keys = Array.Empty<float>();
            mesh.Edges = Array.Empty<int>();
            double minZ = double.MaxValue, maxZ = double.MinValue;
            if (range is { } shared) (minZ, maxZ) = shared;
            else foreach (var v in s.Vertices) { if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z; }
            var light = new Vec3(-0.4, 0.5, 0.75).Normalized();
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
            if (!plan || slice != null || depth != null) vd[i] = (float)(dx * toViewer.X + dy * toViewer.Y + dz * toViewer.Z);
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
            if (mesh.Ordered.Length != tri * 3) { mesh.Ordered = new SKPoint[tri * 3]; mesh.OrderedColors = new SKColor[tri * 3]; mesh.Order = new int[tri]; mesh.Keys = new float[tri]; mesh.SortYaw = double.NaN; }
            // Sort by precomputed keys (sum of corner depths, far first): no comparison delegate, so the order is
            // always consistent and depth is computed once per vertex instead of on every comparison. Panning and
            // zooming keep the order, so it is sorted again only when the camera turns by more than a couple of degrees:
            // the order hardly changes in between, and the translucent fill does not show it.
            if (!(Math.Abs(mesh.SortYaw - cam.Yaw) < SortTolerance) || !(Math.Abs(mesh.SortPitch - cam.Pitch) < SortTolerance))
            {
                for (int t = 0; t < tri; t++)
                {
                    mesh.Order[t] = t;
                    mesh.Keys[t] = vd[tris[3 * t]] + vd[tris[3 * t + 1]] + vd[tris[3 * t + 2]];
                }
                Array.Sort(mesh.Keys, mesh.Order);
                (mesh.SortYaw, mesh.SortPitch) = (cam.Yaw, cam.Pitch);
            }
            for (int i = 0; i < tri; i++)
            {
                int t = mesh.Order[i];
                mesh.Ordered[3 * i] = pts[3 * t]; mesh.Ordered[3 * i + 1] = pts[3 * t + 1]; mesh.Ordered[3 * i + 2] = pts[3 * t + 2];
                mesh.OrderedColors[3 * i] = mesh.OrderedColors[3 * i + 1] = mesh.OrderedColors[3 * i + 2] = mesh.SoupColors[3 * t];
            }
            soupPts = mesh.Ordered;
            soupCols = mesh.OrderedColors;
        }

        // behind another surface (or a fold of its own) a corner goes paler: a quarter of the opacity of the fill
        bool[]? behind = null;
        if (depth != null)
        {
            behind = new bool[vc];
            for (int i = 0; i < vc; i++) behind[i] = depth.Hidden(vs[i].X, vs[i].Y, vd[i]);
            var faded = new SKColor[soupCols.Length];
            var pale = (byte)(alpha / 4);
            for (int i = 0; i < tri; i++)
            {
                var t = plan ? i : mesh.Order[i];
                for (int j = 0; j < 3; j++)
                {
                    var col = soupCols[3 * i + j];
                    faded[3 * i + j] = behind[tris[3 * t + j]] ? col.WithAlpha(pale) : col;
                }
            }
            soupCols = faded;
        }
        if (slice is double band) (soupPts, soupCols) = CutToSlice(mesh, tris, tri, plan, (float)band);
        if (soupPts.Length > 0)
        {
            using var skVertices = SKVertices.CreateCopy(SKVertexMode.Triangles, soupPts, soupCols);
            using var vpaint = new SKPaint { IsAntialias = false };
            c.DrawVertices(skVertices, SKBlendMode.Modulate, vpaint);
        }

        // Wireframe: every edge once, skipping edges fully outside the view (a path of every triangle's outline drew shared
        // edges twice). With the whole surface in view it is the costliest part of a frame, so by default it waits while
        // the camera moves (Program settings ▸ Viewport ▸ Surface wireframe).
        if (s.ShowWireframe && !hideWireframe && cam.Scale > 0.5 && tri <= WireframeTriangleCap)
        {
            float w = (float)cam.Width, hgt = (float)cam.Height;
            if (mesh.Edges.Length == 0) mesh.Edges = UniqueEdges(s.Triangles, tri);
            var edges = mesh.Edges;
            using var wire = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f, Color = (selected ?? color).WithAlpha(wireAlpha) };
            using var path = new SKPath();
            using var behindPath = new SKPath();
            for (int k = 0; k < edges.Length; k += 2)
            {
                var p0 = vs[edges[k]]; var p1 = vs[edges[k + 1]];
                if (slice is double edgeBand && !CutToSlice(ref p0, ref p1, vd[edges[k]], vd[edges[k + 1]], (float)edgeBand)) continue;
                if ((p0.X < 0 && p1.X < 0) || (p0.X > w && p1.X > w) || (p0.Y < 0 && p1.Y < 0) || (p0.Y > hgt && p1.Y > hgt)) continue;
                // an edge with both ends behind a surface goes paler with the fill
                var into = behind != null && behind[edges[k]] && behind[edges[k + 1]] ? behindPath : path;
                into.MoveTo(p0); into.LineTo(p1);
            }
            c.DrawPath(path, wire);
            if (!behindPath.IsEmpty)
            {
                wire.Color = wire.Color.WithAlpha((byte)(wireAlpha / 4));
                c.DrawPath(behindPath, wire);
            }
        }
    }

    /// <summary>The triangles of a surface cut to the band around the target plane. The view is a parallel projection, so depth runs straight across a triangle on the screen too.</summary>
    private static (SKPoint[] Points, SKColor[] Colors) CutToSlice(SurfaceMesh mesh, List<int> tris, int tri, bool plan, float band)
    {
        var points = new List<SKPoint>();
        var colors = new List<SKColor>();
        var vd = mesh.VertexDepth;
        // two planes add at most two corners to a triangle
        Span<(SKPoint P, float D)> corners = new (SKPoint, float)[5], cut = new (SKPoint, float)[5];
        for (int i = 0; i < tri; i++)
        {
            int t = plan ? i : mesh.Order[i];
            float d0 = vd[tris[3 * t]], d1 = vd[tris[3 * t + 1]], d2 = vd[tris[3 * t + 2]];
            if ((d0 > band && d1 > band && d2 > band) || (d0 < -band && d1 < -band && d2 < -band)) continue;
            corners[0] = (mesh.Points[3 * t], d0);
            corners[1] = (mesh.Points[3 * t + 1], d1);
            corners[2] = (mesh.Points[3 * t + 2], d2);
            int n = Cut(corners[..3], cut, band, 1);
            n = Cut(cut[..n], corners, band, -1);
            for (int k = 1; k + 1 < n; k++)
            {
                points.Add(corners[0].P); points.Add(corners[k].P); points.Add(corners[k + 1].P);
                for (int j = 0; j < 3; j++) colors.Add(mesh.SoupColors[3 * t]);
            }
        }
        return (points.ToArray(), colors.ToArray());
    }

    /// <summary>The corners of a polygon on the inner side of one plane of the band, and where its edges cross the plane; how many there are.</summary>
    private static int Cut(ReadOnlySpan<(SKPoint P, float D)> from, Span<(SKPoint P, float D)> to, float band, int side)
    {
        int n = 0;
        for (int i = 0; i < from.Length; i++)
        {
            var (p, d) = from[i];
            var (q, e) = from[(i + 1) % from.Length];
            float u = side * d - band, v = side * e - band;
            if (u <= 0) to[n++] = (p, d);
            if ((u < 0 && v > 0) || (u > 0 && v < 0))
            {
                var t = u / (u - v);
                to[n++] = (new SKPoint(p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t), d + (e - d) * t);
            }
        }
        return n;
    }

    /// <summary>A wireframe edge cut to the band around the target plane; false when none of it is in the band.</summary>
    private static bool CutToSlice(ref SKPoint a, ref SKPoint b, float da, float db, float band)
    {
        if ((da > band && db > band) || (da < -band && db < -band)) return false;
        float start = 0, end = 1;
        if (da != db)
        {
            float x = (band - da) / (db - da), y = (-band - da) / (db - da);
            start = Math.Max(0, Math.Min(x, y));
            end = Math.Min(1, Math.Max(x, y));
        }
        var (p, q) = (a, b);
        a = new SKPoint(p.X + (q.X - p.X) * start, p.Y + (q.Y - p.Y) * start);
        b = new SKPoint(p.X + (q.X - p.X) * end, p.Y + (q.Y - p.Y) * end);
        return true;
    }

    /// <summary>Each triangle edge once, as pairs of vertex indices.</summary>
    private static int[] UniqueEdges(List<int> tris, int tri)
    {
        var seen = new HashSet<long>(tri * 2);
        var result = new List<int>(tri * 4);
        for (int t = 0; t < tri; t++)
            for (int k = 0; k < 3; k++)
            {
                int a = tris[3 * t + k], b = tris[3 * t + (k + 1) % 3];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                // spread by an odd factor, which keeps keys distinct: the default hash of a long xors its halves, and the
                // edges of nearby vertices all collided, so a survey grid of 90 000 points took 17 s before its first frame
                if (seen.Add(key * unchecked((long)0x9E3779B97F4A7C15UL))) { result.Add(a); result.Add(b); }
            }
        return result.ToArray();
    }

    private static void DrawTies(SKCanvas c, Camera cam, BlastProject project, IEnumerable<TieEntity> ties, IReadOnlyDictionary<Guid, Entity> byId, SceneOptions opt, SKPaint text)
    {
        static Vec3? PositionOf(IReadOnlyDictionary<Guid, Entity> byId, Guid id) => byId.TryGetValue(id, out var e) ? e switch
        {
            Hole h => h.Collar,
            InitiationPoint ip => ip.Position,
            _ => null
        } : null;
        // All arrows go into one triangle mesh: drawn one by one, 750 ties were 750 antialiased lines and 750 small
        // filled paths, half of a blast frame; as paths, Skia stroked them on the CPU. Ties outside the view are skipped.
        var products = new Dictionary<string, Product>();
        foreach (var p in project.Products) products.TryAdd(p.Id, p);
        var arrows = new LineMesh();
        var labels = new TextBatch();
        float w = (float)cam.Width, h = (float)cam.Height;
        const float head = 8;
        {
            foreach (var t in ties)
            {
                var from = PositionOf(byId, t.FromId);
                var to = PositionOf(byId, t.ToId);
                if (from is null || to is null) continue;
                var a = from.Value;
                var b = to.Value;
                var dir = (b - a).Normalized();
                var gap = Math.Min(8 / cam.Scale, Vec3.Distance(a, b) / 3);
                var (x0, y0) = cam.ToScreen(a + dir * gap);
                var (x1, y1) = cam.ToScreen(b - dir * gap);
                if ((x0 < -20 && x1 < -20) || (x0 > w + 20 && x1 > w + 20) || (y0 < -20 && y1 < -20) || (y0 > h + 20 && y1 > h + 20)) continue;
                var product = t.ProductId is string pid && products.TryGetValue(pid, out var found) ? found : null;
                var selected = opt.Selection.Contains(t.Id);
                var col = selected ? opt.SelectionColor : product != null ? new SKColor(product.Color) : new SKColor(0xFF6B7280);
                if (!_dark && col.Red > 230 && col.Green > 230 && col.Blue > 230) col = new SKColor(0xFF374151);
                col = Adapt(col);
                arrows.Line(col, selected ? 3f : 2f, x0, y0, x1, y1);
                var ang = Math.Atan2(y1 - y0, x1 - x0);
                arrows.Triangle(col, new SKPoint(x1, y1),
                    new SKPoint((float)(x1 - head * Math.Cos(ang - 0.4)), (float)(y1 - head * Math.Sin(ang - 0.4))),
                    new SKPoint((float)(x1 - head * Math.Cos(ang + 0.4)), (float)(y1 - head * Math.Sin(ang + 0.4))));
                if (cam.Scale > 4)
                {
                    var (x, y) = cam.ToScreen(Vec3.Lerp(a, b, 0.5));
                    labels.Add(Timing.TieDelay(project, t, a, b).ToString("0"), x + 2, y - 2, col, 9);
                }
            }
            arrows.Flush(c);
            labels.Flush(c, text);
        }
    }

    private static void DrawRows(SKCanvas c, Camera cam, BlastProject project, IReadOnlyDictionary<Guid, Entity> byId, SKPaint stroke)
    {
        stroke.Color = new SKColor(0x8016A34A);
        stroke.StrokeWidth = 1;
        stroke.PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0);
        foreach (var r in project.Rows)
        {
            var pts = r.HoleIds.Select(id => byId.TryGetValue(id, out var e) ? e : null).OfType<Hole>().Select(h => h.Collar).ToList();
            DrawPolyline(c, cam, pts, false, stroke);
        }
        stroke.PathEffect = null;
    }

    /// <summary>Hole depth line (collar to toe) and, on its own switch, the charge column along it, into <paramref name="lines"/>.</summary>
    private static void DrawHoleColumn(SKCanvas c, Camera cam, BlastProject project, Hole h, SceneOptions opt, SKPaint fill, LineMesh lines)
    {
        var view = project.View;
        if (view.CollarsOnly && !(view.ShowLoading && h.Decks.Count > 0)) return;
        var (x, y) = cam.ToScreen(h.Collar);
        var (tx, ty) = cam.ToScreen(h.Toe);
        if (Math.Abs(tx - x) + Math.Abs(ty - y) <= 1) return;
        var selected = opt.Selection.Contains(h.Id);
        var typeColor = HoleColour(project, h);
        var color = selected ? opt.SelectionColor : typeColor;
        var r = (float)Math.Max(opt.CollarPixels / 2, h.RadiusM * cam.Scale);
        if (!view.CollarsOnly) lines.Line(color.WithAlpha(160), 1, x, y, tx, ty);
        if (view.ShowLoading && h.Decks.Count > 0)
            foreach (var d in h.Decks)
            {
                var p = project.FindProduct(d.ProductId);
                if (p == null) continue;
                var (ax, ay) = cam.ToScreen(h.PointAtDepth(d.Top));
                var (bx, by) = cam.ToScreen(h.PointAtDepth(d.Bottom));
                lines.Line(new SKColor(p.Color), Math.Max(3, r), ax, ay, bx, by);
            }
        if (!view.CollarsOnly)
        {
            fill.Color = color.WithAlpha(160);
            c.DrawCircle(tx, ty, 1.5f, fill);
        }
    }

    private static void DrawHole(SKCanvas c, Camera cam, BlastProject project, Hole h, SceneOptions opt, SKPaint stroke, SKPaint fill)
    {
        var view = project.View;
        var selected = opt.Selection.Contains(h.Id);
        var typeColor = HoleColour(project, h);
        var color = selected ? opt.SelectionColor : typeColor;
        var (x, y) = cam.ToScreen(h.Collar);
        var r = (float)Math.Max(opt.CollarPixels / 2, h.RadiusM * cam.Scale);

        bool fired = opt.AnimationTimeMs is double at && h.FiringTimeMs is double ft && ft <= at;
        stroke.Color = color;
        // the collar: 1 px thicker when selected, not 2, it is small
        stroke.StrokeWidth = 1.5f + (selected && opt.SelectionExtra > 0 ? 1 : 0);
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
            // collars switched off stay off for selected holes too
            if (view.ShowCollars)
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

    private static void DrawHoleLabels(Camera cam, BlastProject project, Hole h, ViewState view, (List<HoleLabel> Left, List<HoleLabel> Right) sides, SceneOptions opt, SKPaint text, TextBatch batch)
    {
        if (cam.Scale < 1.2) return;
        var (x, y) = cam.ToScreen(h.Collar);
        if (x < -100 || y < -100 || x > cam.Width + 100 || y > cam.Height + 100) return;
        var layer = project.FindLayer(h.LayerId);
        if (layer is { ShowText: false }) return;
        var r = (float)Math.Max(opt.CollarPixels / 2, h.RadiusM * cam.Scale);
        var selected = opt.Selection.Contains(h.Id);
        text.Color = selected ? opt.SelectionColor : Ink;
        var size = Math.Clamp(view.HoleLabelFontSize, ViewState.MinHoleLabelFontSize, ViewState.MaxHoleLabelFontSize);
        text.TextSize = size;
        // each side top to bottom in the order of its list, the column centred on the collar; a label with nothing to
        // say for this hole (no time yet, no detonator) leaves no gap
        var (left, right) = sides;
        var number = selected ? opt.SelectionColor : new SKColor(_dark ? 0xFF60A5FAu : 0xFF1D4ED8u);
        var timing = selected ? opt.SelectionColor : Adapt(new SKColor(0xFFB91C1C));
        var ink = text.Color;
        void Column(List<HoleLabel> side, bool onRight)
        {
            var lines = new List<(string Text, SKColor Color)>();
            foreach (var l in side)
                if (HoleLabelLayout.IsShown(view, l))
                    foreach (var t in LabelText(project, h, l))
                        lines.Add((t, l == HoleLabel.Number ? number : l is HoleLabel.SurfaceTime or HoleLabel.InholeDelay or HoleLabel.HoleTime ? timing : ink));
            // the calculated properties chosen in Edit ▸ Calculations go under the labels of the right side
            if (onRight && view.CalcLabels is { Count: > 0 } calc && !h.IsDummy)
                foreach (var id in calc) lines.Add((CalcService.Format(project, id, h), ink));
            // a line is the font size and a pixel; the first baseline puts the middle of the column at the collar centre
            var line = size + 1;
            var top = y - lines.Count * line / 2f + size * 0.8f;
            for (int i = 0; i < lines.Count; i++)
            {
                var lx = onRight ? x + r + 2 : x - r - 2 - text.MeasureText(lines[i].Text);
                batch.Add(lines[i].Text, lx, top + i * line, lines[i].Color, text.TextSize);
            }
        }
        Column(left, false);
        Column(right, true);
        text.TextSize = 11;
    }

    /// <summary>The lines of one label for a hole: none when the hole has no such value.</summary>
    private static IEnumerable<string> LabelText(BlastProject project, Hole h, HoleLabel l)
    {
        switch (l)
        {
            case HoleLabel.Number: yield return h.Label; break;
            case HoleLabel.Length: yield return $"L {h.EffectiveLength:0.0}"; break;
            case HoleLabel.Diameter: yield return $"Ø {h.Diameter:0}"; break;
            case HoleLabel.Angle: yield return $"α {h.Angle:0}°"; break;
            case HoleLabel.Bearing: yield return $"Az {h.Bearing:0}°"; break;
            case HoleLabel.Elevation: yield return $"Z {h.Collar.Z:0.0}"; break;
            case HoleLabel.ExplosiveMass: yield return $"{Charging.ExplosiveMass(project, h):0.0} kg"; break;
            case HoleLabel.DeckMasses:
                foreach (var d in h.Decks.OrderBy(d => d.Top))
                    if (project.FindProduct(d.ProductId) is { IsExplosive: true }) yield return $"▪ {Charging.DeckMass(project, h, d):0.0} kg";
                break;
            case HoleLabel.DeckSequence: if (DeckSequence(project, h) is string seq) yield return F("Decks: {0}", seq); break;
            case HoleLabel.Temperature: if (h.Temperature is double temp) yield return $"{temp:0} °C"; break;
            case HoleLabel.DetonatorId: if (!string.IsNullOrWhiteSpace(h.DetonatorId)) yield return h.DetonatorId; break;
            case HoleLabel.Type: yield return $"T{h.HoleType}"; break;
            case HoleLabel.HoleOrder: if (h.FiringSequence is int sequence) yield return sequence.ToString(); break;
            case HoleLabel.SurfaceTime: if (h.SurfaceTimeMs is double s) yield return $"{s:0}"; break;
            case HoleLabel.InholeDelay: if (h.InholeDelayMs > 0) yield return $"{h.InholeDelayMs:0}"; break;
            case HoleLabel.HoleTime: if (h.FiringTimeMs is double f) yield return $"{f:0}"; break;
        }
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

    /// <summary>The grid of a section: a line at each elevation, with its figure, and lines across at the same step along the section.</summary>
    private static void DrawSectionGrid(SKCanvas c, Camera cam, SKPaint stroke, SKPaint text)
    {
        var step = NiceStep(100 / cam.Scale);
        double halfWidth = cam.Width / 2 / cam.Scale, halfHeight = cam.Height / 2 / cam.Scale;
        float w = (float)cam.Width, h = (float)cam.Height;
        var target = cam.Target;
        stroke.Color = _dark ? new SKColor(0xFF242930) : new SKColor(0xFFE5E7EB);
        stroke.StrokeWidth = 1;
        text.Color = new SKColor(0xFF9CA3AF);
        text.TextSize = 9;
        for (var z = Math.Floor((target.Z - halfHeight) / step) * step; z <= target.Z + halfHeight; z += step)
        {
            var y = cam.ToScreen(target.WithZ(z)).Y;
            c.DrawLine(0, y, w, y, stroke);
            c.DrawText(z.ToString("0"), 2, y - 2, text);
        }
        for (var d = Math.Floor(-halfWidth / step) * step; d <= halfWidth; d += step)
        {
            var x = cam.ToScreen(target + cam.Right * d).X;
            c.DrawLine(x, 0, x, h, stroke);
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

    private static void DrawHoleLegend(SKCanvas c, BlastProject project, List<Hole> holes, SKPaint fill, SKPaint text)
    {
        var used = holes.Select(h => h.HoleType).Distinct().OrderBy(x => x).ToList();
        // one type (or none) tells nothing a legend is for
        if (used.Count <= 1) return;
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
        foreach (var id in project.Summary.CalcTotals ?? new())
            lines.Add($"{CalcService.Caption(project, id)}: {CalcService.Format(project, id, project)}");
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
