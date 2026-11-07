using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.App.Controls;

/// <summary>Pointer event in world coordinates handed to the active tool.</summary>
public sealed class ToolPointer
{
    public double X { get; init; }
    public double Y { get; init; }
    public Vec3 World { get; init; }
    /// <summary>World point after snapping to nearby vertices (equals <see cref="World"/> when nothing is near).</summary>
    public Vec3 Snapped { get; init; }
    public bool IsSnapped { get; init; }
    public Entity? Hit { get; init; }
    public bool Left { get; init; }
    public bool Right { get; init; }
    public KeyModifiers Modifiers { get; init; }
    public int ClickCount { get; init; }
}

/// <summary>The interactive plan/3D view. Draws the project with <see cref="PlanRenderer"/> and forwards input to the active tool.</summary>
public sealed class Viewport : Control
{
    private Point? _panStart;
    private Point? _orbitStart;
    private Point _lastPointer;

    public Camera Camera { get; } = new();
    public PreviewOverlay Preview { get; } = new();
    public List<IOverlay> Overlays { get; } = new();
    public Func<BlastProject?>? ProjectSource { get; set; }
    public Func<SceneOptions>? OptionsSource { get; set; }
    public Func<Tools.Tool?>? ToolSource { get; set; }
    public bool SnapEnabled { get; set; } = true;
    public double TargetRadius { get; set; } = 8;
    public bool LockPlan { get; set; }
    /// <summary>When surface wireframes are drawn (<see cref="IO.ProgramSettings.SurfaceWireframe"/>).</summary>
    public string SurfaceWireframe { get; set; } = IO.ProgramSettings.WireframeWhileStill;
    /// <summary>Frames per second and the time of the last frame in the corner of the view.</summary>
    public bool ShowFps { get; set; }
    private readonly FrameStats _stats = new();
    private static long _framesDrawn;

    /// <summary>Frames drawn by every viewport since the start (render benchmarks read it).</summary>
    public static long FramesDrawn => Interlocked.Read(ref _framesDrawn);
    /// <summary>Time spent drawing frames since the start, ms (render benchmarks read it).</summary>
    public static double DrawMs { get; private set; }

    /// <summary>Raised with the world position under the cursor (status bar coordinates).</summary>
    public event Action<Vec3>? PointerWorldMoved;
    public event Action? CameraChanged;
    /// <summary>Raised at the start of every frame, after the camera size is updated (tools refresh their preview, the window moves anchored buttons).</summary>
    public event Action? Rendering;

    public Viewport()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public BlastProject? Project => ProjectSource?.Invoke();

    public void Redraw() => InvalidateVisual();

    // The plan is drawn by hand, so recoloured theme brushes and a new plan background do not redraw it by themselves.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Services.Look.ThemeChanged += Redraw;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Services.Look.ThemeChanged -= Redraw;
        base.OnDetachedFromVisualTree(e);
    }

    // While the camera moves the frame leaves out the costliest details; one full frame follows when it has settled.
    private const double SettleMs = 200;
    private long _cameraMovedAt;
    private Avalonia.Threading.DispatcherTimer? _settle;

    /// <summary>The camera is being moved by the user (pan, orbit, zoom): draw quick frames until it stops.</summary>
    public void CameraMoving()
    {
        _cameraMovedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _settle ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(SettleMs + 20), Avalonia.Threading.DispatcherPriority.Background, (_, _) => { _settle!.Stop(); Redraw(); });
        _settle.Stop();
        _settle.Start();
    }

    public override void Render(DrawingContext context)
    {
        Camera.Width = Bounds.Width;
        Camera.Height = Bounds.Height;
        Rendering?.Invoke();
        var project = Project;
        if (project == null)
        {
            context.FillRectangle(Services.Look.Field, new Rect(Bounds.Size));
            return;
        }
        var opt = OptionsSource?.Invoke() ?? new SceneOptions();
        var all = new List<IOverlay>(opt.Overlays);
        all.AddRange(Overlays);
        all.Add(Preview);
        var options = new SceneOptions
        {
            Selection = opt.Selection,
            Background = opt.Background,
            SelectionColor = opt.SelectionColor,
            CollarPixels = opt.CollarPixels,
            DrawSummaryBlock = opt.DrawSummaryBlock,
            DrawHoleLegend = opt.DrawHoleLegend,
            DrawTieLegend = opt.DrawTieLegend,
            DrawElevationScale = opt.DrawElevationScale,
            GeometryColor = opt.GeometryColor,
            SectionLineColor = opt.SectionLineColor,
            SurfaceColors = opt.SurfaceColors,
            AnimationTimeMs = opt.AnimationTimeMs,
            HiddenEntities = opt.HiddenEntities,
            HideGeometry = opt.HideGeometry,
            SliceHalfWidth = opt.SliceHalfWidth,
            TopInset = opt.TopInset,
            HideWireframes = SurfaceWireframe == IO.ProgramSettings.WireframeNever
                || (SurfaceWireframe == IO.ProgramSettings.WireframeWhileStill && System.Diagnostics.Stopwatch.GetElapsedTime(_cameraMovedAt).TotalMilliseconds < SettleMs),
            WireframesOnSelected = SurfaceWireframe == IO.ProgramSettings.WireframeSelected,
            Overlays = all,
            MovedEntities = Preview.Moving is { Count: > 0 } moving ? new HashSet<Guid>(moving) : null,
            MoveOffset = Preview.MoveOffset
        };
        // The frame is recorded here on the UI thread and the render thread only plays it back: it never reads the model
        // or the camera, which the UI thread goes on changing. Drawn from the live lists a frame could fail or mix two states.
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
        try { PlanRenderer.Render(canvas, Camera, project, options); }
        catch (Exception ex) { IO.Log.Error("Render failed", ex); }
        var frame = recorder.EndRecording();
        context.Custom(new SkiaDraw(new Rect(Bounds.Size), frame, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds, ShowFps ? _stats : null));
    }

    /// <summary>
    /// Frames drawn in the last second and how long the last one took to draw. The view redraws only when something changes,
    /// so the rate is meaningful while panning, orbiting or animating; the frame time is the limit (1000 / ms frames per second).
    /// </summary>
    private sealed class FrameStats
    {
        private readonly Queue<long> _frames = new();
        private readonly SKPaint _text = new() { Color = SKColors.White, IsAntialias = true, TextSize = 12, Typeface = SKTypeface.FromFamilyName("Consolas") };
        private readonly SKPaint _back = new() { Color = new SKColor(0, 0, 0, 150), IsAntialias = true };

        public void Draw(SKCanvas canvas, Rect bounds, double ms)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            _frames.Enqueue(now);
            while (_frames.Count > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_frames.Peek(), now).TotalSeconds > 1) _frames.Dequeue();
            var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} FPS · {1:0.0} ms", _frames.Count, ms);
            var w = _text.MeasureText(text);
            // above the scale bar in the bottom right corner
            var r = SKRect.Create((float)bounds.Width - w - 22, (float)bounds.Height - 62, w + 14, 20);
            _text.Color = ms > 33 ? new SKColor(0xFFFCA5A5) : ms > 16.7 ? new SKColor(0xFFFDE68A) : SKColors.White;
            canvas.DrawRoundRect(r, 5, 5, _back);
            canvas.DrawText(text, r.Left + 7, r.Top + 14.5f, _text);
        }
    }

    private sealed class SkiaDraw : ICustomDrawOperation
    {
        private static bool _backendLogged;
        private readonly object _gate = new();
        private SKPicture? _frame;
        private readonly double _recordMs;
        private readonly FrameStats? _stats;

        public SkiaDraw(Rect bounds, SKPicture frame, double recordMs, FrameStats? stats)
        {
            Bounds = bounds;
            _frame = frame;
            _recordMs = recordMs;
            _stats = stats;
        }

        public Rect Bounds { get; }

        public void Dispose()
        {
            lock (_gate)
            {
                _frame?.Dispose();
                _frame = null;
            }
        }

        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => true;

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease == null) return;
            using var l = lease.Lease();
            if (!_backendLogged)
            {
                _backendLogged = true;
                IO.Log.Info(l.GrContext != null ? $"Viewport rendering on the GPU ({l.GrContext.Backend})" : "Viewport rendering in software (no GPU context)");
            }
            var canvas = l.SkCanvas;
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            try { lock (_gate) if (_frame != null) canvas.DrawPicture(_frame); }
            catch (Exception ex) { IO.Log.Error("Render failed", ex); }
            var ms = _recordMs + System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            _stats?.Draw(canvas, Bounds, ms);
            DrawMs += ms;
            Interlocked.Increment(ref _framesDrawn);
            canvas.Restore();
        }
    }

    // ---- picking ------------------------------------------------------------

    /// <summary>True when the entity is drawn: display switches, hole filters and objects hidden with "Show only selected".</summary>
    private static bool Shown(BlastProject project, Entity e, ISet<Guid>? hidden) =>
        PlanRenderer.EntityShown(project.View, e) && hidden?.Contains(e.Id) != true;

    /// <summary>
    /// Entity nearest to a screen point within the target radius, preferring holes and editable layers. With
    /// <paramref name="accept"/> only those entities count, so a tool is not blocked by another object on the same spot.
    /// </summary>
    public Entity? HitTest(double sx, double sy, bool includeLocked = false, Func<Entity, bool>? accept = null)
    {
        var project = Project;
        if (project == null) return null;
        var layers = project.Layers.ToDictionary(l => l.Id);
        var hidden = OptionsSource?.Invoke().HiddenEntities;
        Entity? best = null;
        double bestD = TargetRadius;
        foreach (var e in project.Entities)
        {
            if (!layers.TryGetValue(e.LayerId, out var layer) || !layer.Visible || !Shown(project, e, hidden)) continue;
            if (!includeLocked && (layer.Frozen)) continue;
            if (accept != null && !accept(e)) continue;
            var d = ScreenDistance(e, sx, sy);
            if (e is Hole) d -= 2;
            if (d < bestD) { bestD = d; best = e; }
        }
        if (best == null)
        {
            // Surfaces are picked by containment.
            var w = Camera.ToWorld(sx, sy);
            best = project.Entities.OfType<SurfaceEntity>().FirstOrDefault(s => layers.TryGetValue(s.LayerId, out var l) && l.Visible && Shown(project, s, hidden) && (accept == null || accept(s)) && s.ElevationAt(w.X, w.Y).HasValue);
        }
        return best;
    }

    private double ScreenDistance(Entity e, double sx, double sy)
    {
        switch (e)
        {
            case Hole h:
            {
                var (x, y) = Camera.ToScreen(h.Collar);
                return Math.Max(0, Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy)) - h.RadiusM * Camera.Scale);
            }
            case PointEntity pt:
            {
                var (x, y) = Camera.ToScreen(pt.Position);
                return Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy));
            }
            case PolylineEntity pl:
                return SegmentsDistance(pl.Points, pl.Closed, sx, sy);
            case TieEntity t:
                return SegmentsDistance(new[] { t.FromPos, t.ToPos }, false, sx, sy);
            case ArrowEntity a:
                return SegmentsDistance(new[] { a.Start, a.End }, false, sx, sy);
            case TimingLineEntity tl:
                return SegmentsDistance(tl.Points, false, sx, sy);
            case DimensionEntity d:
                return SegmentsDistance(new[] { d.Start, d.End }, false, sx, sy);
            case CircleEntity c:
                return SegmentsDistance(c.ToPolygon(36), true, sx, sy);
            case InitiationPoint ip:
            {
                // The star picks it, ahead of the hole it sits on (drawn over it); its see-through middle picks the hole.
                var (x, y) = Camera.ToScreen(ip.Position);
                var dist = Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy));
                var star = BlastDesigner.Rendering.PlanRenderer.InitiationStarRadius * 0.8;
                return dist > star ? dist - star : dist < BlastDesigner.Rendering.PlanRenderer.InitiationStarHole ? 1 : -3;
            }
            case SurfaceEntity:
                return double.MaxValue;
            default:
                return e.GetVertices().Select(v => { var (x, y) = Camera.ToScreen(v); return Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy)); }).DefaultIfEmpty(double.MaxValue).Min();
        }
    }

    /// <summary>Screen distance to a polyline; runs for every line on every pointer move, so it projects in place without allocating.</summary>
    private double SegmentsDistance(IReadOnlyList<Vec3> pts, bool closed, double sx, double sy)
    {
        if (pts.Count == 0) return double.MaxValue;
        var (x0, y0) = Camera.ToScreen(pts[0]);
        double best = Math.Sqrt((x0 - sx) * (x0 - sx) + (y0 - sy) * (y0 - sy));
        double fx = x0, fy = y0, px = x0, py = y0;
        int n = closed ? pts.Count + 1 : pts.Count;
        for (int i = 1; i < n; i++)
        {
            double qx, qy;
            if (i == pts.Count) (qx, qy) = (fx, fy);
            else { var (x, y) = Camera.ToScreen(pts[i]); (qx, qy) = (x, y); }
            double dx = qx - px, dy = qy - py, len2 = dx * dx + dy * dy;
            double t = len2 > 0 ? Math.Clamp(((sx - px) * dx + (sy - py) * dy) / len2, 0, 1) : 0;
            double cx = px + t * dx - sx, cy = py + t * dy - sy;
            best = Math.Min(best, Math.Sqrt(cx * cx + cy * cy));
            (px, py) = (qx, qy);
        }
        return best;
    }

    /// <summary>Entities whose geometry lies fully inside a screen rectangle.</summary>
    public IEnumerable<Entity> InRect(double x0, double y0, double x1, double y1, bool crossing)
    {
        var project = Project;
        if (project == null) yield break;
        var layers = project.Layers.ToDictionary(l => l.Id);
        var hidden = OptionsSource?.Invoke().HiddenEntities;
        var (minX, maxX, minY, maxY) = (Math.Min(x0, x1), Math.Max(x0, x1), Math.Min(y0, y1), Math.Max(y0, y1));
        foreach (var e in project.Entities)
        {
            if (!layers.TryGetValue(e.LayerId, out var layer) || !layer.Visible || layer.Frozen) continue;
            if (!Shown(project, e, hidden)) continue;
            var verts = e is Hole hh ? new[] { hh.Collar } : e is TieEntity t ? new[] { t.FromPos, t.ToPos } : e.GetVertices().ToArray();
            if (verts.Length == 0) continue;
            bool Inside(Vec3 v) { var (x, y) = Camera.ToScreen(v); return x >= minX && x <= maxX && y >= minY && y <= maxY; }
            if (crossing ? verts.Any(Inside) : verts.All(Inside)) yield return e;
        }
    }

    private (Vec3 Point, bool Snapped) Snap(double sx, double sy, Vec3 world)
    {
        var project = Project;
        if (!SnapEnabled || project == null) return (world, false);
        var layers = project.Layers.ToDictionary(l => l.Id);
        var hidden = OptionsSource?.Invoke().HiddenEntities;
        Vec3? best = null;
        double bestD = TargetRadius * 1.5;
        foreach (var e in project.Entities)
        {
            if (e is SurfaceEntity or TieEntity) continue;
            // a hole snaps by its collar: with the collars switched off there is nothing shown to snap to
            if (e is Hole && !project.View.ShowCollars) continue;
            if (!layers.TryGetValue(e.LayerId, out var layer) || !layer.Visible || !Shown(project, e, hidden)) continue;
            var verts = e is Hole h ? new[] { h.Collar } : e.GetVertices();
            foreach (var v in verts)
            {
                var (x, y) = Camera.ToScreen(v);
                var d = Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy));
                if (d < bestD) { bestD = d; best = v; }
            }
        }
        return best is Vec3 b ? (b, true) : (world, false);
    }

    /// <summary>
    /// Snap point for an object being dragged: the visible vertex (hole collar, line vertex…) under the cursor, within
    /// the snap radius, ignoring the dragged objects themselves — they are drawn under the cursor and would always win.
    /// </summary>
    public Vec3? SnapForDrag(double sx, double sy, ICollection<Guid> dragged)
    {
        var project = Project;
        if (!SnapEnabled || project == null) return null;
        var layers = project.Layers.ToDictionary(l => l.Id);
        var hidden = OptionsSource?.Invoke().HiddenEntities;
        Vec3? best = null;
        double bestD = TargetRadius * 1.5;
        foreach (var e in project.Entities)
        {
            if (e is SurfaceEntity or TieEntity || dragged.Contains(e.Id)) continue;
            // a hole snaps by its collar: with the collars switched off there is nothing shown to snap to
            if (e is Hole && !project.View.ShowCollars) continue;
            if (!layers.TryGetValue(e.LayerId, out var layer) || !layer.Visible || !Shown(project, e, hidden)) continue;
            var verts = e is Hole h ? new[] { h.Collar } : e.GetVertices();
            foreach (var v in verts)
            {
                var (x, y) = Camera.ToScreen(v);
                var d = Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy));
                if (d < bestD) { bestD = d; best = v; }
            }
        }
        return best;
    }

    private ToolPointer MakePointer(PointerEventArgs e, int clicks = 0) =>
        MakePointer(e.GetPosition(this), e.GetCurrentPoint(this).Properties, e.KeyModifiers, clicks);

    private ToolPointer MakePointer(Point p, PointerPointProperties props, KeyModifiers modifiers, int clicks)
    {
        var project = Project;
        // Work on the elevation of the design: average collar elevation, else the camera target.
        double? planeZ = project?.Holes.Any() == true ? project.Holes.Average(h => h.Collar.Z) : null;
        var world = Camera.ToWorld(p.X, p.Y, planeZ);
        var (snapped, isSnapped) = Snap(p.X, p.Y, world);
        return new ToolPointer
        {
            X = p.X,
            Y = p.Y,
            World = world,
            Snapped = snapped,
            IsSnapped = isSnapped,
            Hit = HitTest(p.X, p.Y),
            Left = props.IsLeftButtonPressed,
            Right = props.IsRightButtonPressed,
            Modifiers = modifiers,
            ClickCount = clicks
        };
    }

    // ---- input ---------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        FlushMove();
        Focus();
        var pt = e.GetCurrentPoint(this);
        _lastPointer = pt.Position;
        if (pt.Properties.IsMiddleButtonPressed)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !LockPlan) BeginOrbit(pt.Position);
            else _panStart = pt.Position;
            e.Pointer.Capture(this);
            return;
        }
        if (pt.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt) && !LockPlan)
        {
            BeginOrbit(pt.Position);
            e.Pointer.Capture(this);
            return;
        }
        if (pt.Properties.IsRightButtonPressed) return; // context menu is opened on release
        ToolSource?.Invoke()?.PointerPressed(MakePointer(e, e.ClickCount));
        e.Pointer.Capture(this);
        Redraw();
    }

    private void BeginOrbit(Point start)
    {
        _orbitStart = start;
        SetOrbitPivot();
    }

    /// <summary>Puts the camera target on the object at the centre of the view, so that the next orbit turns about it.</summary>
    public void SetOrbitPivot()
    {
        if (OrbitPivotDepth() is double depth) Camera.MoveTargetToDepth(depth);
    }

    /// <summary>
    /// Depth of the point at the centre of the view, which the orbit turns about. In order of preference: the nearest
    /// visible surface under the centre; the object vertex closest to the centre; the median depth of everything on
    /// screen; the middle of the whole design. Null when there is nothing to look at.
    /// </summary>
    private double? OrbitPivotDepth()
    {
        var project = Project;
        if (project == null) return null;
        var layers = project.Layers.ToDictionary(l => l.Id);
        var hidden = OptionsSource?.Invoke().HiddenEntities;
        var (right, up, toViewer, target) = (Camera.Right, Camera.Up, Camera.ToViewer, Camera.Target);
        var (halfW, halfH) = (Camera.Width / 2 / Camera.Scale, Camera.Height / 2 / Camera.Scale);
        var nearRadius = TargetRadius * 5 / Camera.Scale;
        double? surfaceHit = null, nearest = null;
        var nearestD = double.MaxValue;
        var onScreen = new List<double>();
        var all = Bounds3.Empty;
        foreach (var e in project.Entities)
        {
            if (e is TieEntity || !layers.TryGetValue(e.LayerId, out var layer) || !layer.Visible || !Shown(project, e, hidden)) continue;
            all.Add(e.GetBounds());
            if (e is SurfaceEntity s)
            {
                // Screen-plane coordinates (metres from the view centre) and depth of every vertex.
                var v = s.Vertices;
                var sp = new (double R, double U, double D)[v.Count];
                for (int i = 0; i < v.Count; i++)
                {
                    var d = v[i] - target;
                    sp[i] = (Vec3.Dot(d, right), Vec3.Dot(d, up), Vec3.Dot(d, toViewer));
                }
                var t = s.Triangles;
                for (int k = 0; k + 2 < t.Count; k += 3)
                {
                    var (a, b, c) = (sp[t[k]], sp[t[k + 1]], sp[t[k + 2]]);
                    // Barycentric coordinates of the view centre (0, 0) in the projected triangle.
                    var den = (b.U - c.U) * (a.R - c.R) + (c.R - b.R) * (a.U - c.U);
                    if (Math.Abs(den) < 1e-12) continue;
                    var wa = ((b.U - c.U) * -c.R + (c.R - b.R) * -c.U) / den;
                    var wb = ((c.U - a.U) * -c.R + (a.R - c.R) * -c.U) / den;
                    var wc = 1 - wa - wb;
                    if (wa < 0 || wb < 0 || wc < 0) continue;
                    var depth = wa * a.D + wb * b.D + wc * c.D;
                    if (surfaceHit is not double h || depth > h) surfaceHit = depth;
                }
                if (surfaceHit != null) continue;
            }
            foreach (var p in e.GetVertices())
            {
                var d = p - target;
                var (r, u) = (Vec3.Dot(d, right), Vec3.Dot(d, up));
                if (Math.Abs(r) > halfW || Math.Abs(u) > halfH) continue;
                var depth = Vec3.Dot(d, toViewer);
                onScreen.Add(depth);
                var dist = r * r + u * u;
                if (dist < nearestD) { nearestD = dist; nearest = depth; }
            }
        }
        if (surfaceHit != null) return surfaceHit;
        if (nearest != null && nearestD <= nearRadius * nearRadius) return nearest;
        if (onScreen.Count > 0) { onScreen.Sort(); return onScreen[onScreen.Count / 2]; }
        return all.IsEmpty ? null : Vec3.Dot(all.Center - target, toViewer);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_panStart != null)
        {
            Camera.Pan(p.X - _lastPointer.X, p.Y - _lastPointer.Y);
            _lastPointer = p;
            CameraMoving();
            CameraChanged?.Invoke();
            Redraw();
            return;
        }
        if (_orbitStart != null)
        {
            Camera.Orbit((p.X - _lastPointer.X) * 0.4, (p.Y - _lastPointer.Y) * 0.3);
            _lastPointer = p;
            CameraMoving();
            CameraChanged?.Invoke();
            Redraw();
            return;
        }
        // The mouse reports far more often than the screen refreshes, and each move snaps and hit-tests the whole
        // project: only the latest position is handled, once the input queue is empty, so frames are not starved.
        _pendingMove = (p, e.GetCurrentPoint(this).Properties, e.KeyModifiers);
        if (_moveQueued) return;
        _moveQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(FlushMove, Avalonia.Threading.DispatcherPriority.Background);
    }

    private (Point Position, PointerPointProperties Properties, KeyModifiers Modifiers)? _pendingMove;
    private bool _moveQueued;

    /// <summary>Hands the latest pointer move to the tool; called before a press or release so the tool sees moves in order.</summary>
    private void FlushMove()
    {
        _moveQueued = false;
        if (_pendingMove is not { } m) return;
        _pendingMove = null;
        var tp = MakePointer(m.Position, m.Properties, m.Modifiers, 0);
        PointerWorldMoved?.Invoke(tp.Snapped);
        Preview.Snap = tp.IsSnapped ? tp.Snapped : null;
        ToolSource?.Invoke()?.PointerMoved(tp);
        Redraw();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        FlushMove();
        e.Pointer.Capture(null);
        if (_panStart != null || _orbitStart != null)
        {
            _panStart = _orbitStart = null;
            return;
        }
        var tp = MakePointer(e);
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            ShowContextMenu(tp);
            return;
        }
        ToolSource?.Invoke()?.PointerReleased(tp);
        Redraw();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        Camera.ZoomAt(p.X, p.Y, e.Delta.Y > 0 ? 1.2 : 1 / 1.2);
        CameraMoving();
        CameraChanged?.Invoke();
        Redraw();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (ToolSource?.Invoke()?.KeyDown(e.Key, e.KeyModifiers) == true) { e.Handled = true; Redraw(); }
    }

    private void ShowContextMenu(ToolPointer tp)
    {
        var tool = ToolSource?.Invoke();
        var items = tool?.ContextMenu(tp).ToList() ?? new List<Tools.MenuEntry>();
        // The selection menu is complete on its own (view commands live on the rail and keys); drawing tools still get the view items.
        if (tool is not Tools.SelectTool)
        {
            if (items.Count > 0) items.Add(Tools.MenuEntry.Separator);
            items.AddRange(ViewMenu?.Invoke() ?? Enumerable.Empty<Tools.MenuEntry>());
        }
        var menu = new ContextMenu { ItemsSource = Tools.MenuEntry.Build(items) };
        ContextMenu = menu;
        menu.Open(this);
    }

    /// <summary>View items appended to every context menu (zoom all, plan view…).</summary>
    public Func<IEnumerable<Tools.MenuEntry>>? ViewMenu { get; set; }

    public void ZoomAll()
    {
        var project = Project;
        if (project == null) return;
        // the camera takes its size at render time; before the first frame (a window just opened) take it from the layout
        if (Bounds.Width >= 1 && Bounds.Height >= 1) { Camera.Width = Bounds.Width; Camera.Height = Bounds.Height; }
        // only what is drawn: hidden layers, display switches, hole filters and hidden objects do not count
        var visibleLayers = project.Layers.Where(l => l.Visible).Select(l => l.Id).ToHashSet();
        var options = OptionsSource?.Invoke();
        var hidden = options?.HiddenEntities;
        var b = Bounds3.Empty;
        foreach (var e in project.Entities)
            if (e is not TieEntity && (options?.HideGeometry != true || e is Hole or InitiationPoint) && visibleLayers.Contains(e.LayerId) && Shown(project, e, hidden)) b.Add(e.GetBounds());
        if (b.IsEmpty) { Camera.Target = Vec3.Zero; Camera.Scale = 5; }
        else Camera.Fit(b);
        CameraChanged?.Invoke();
        Redraw();
    }
}
