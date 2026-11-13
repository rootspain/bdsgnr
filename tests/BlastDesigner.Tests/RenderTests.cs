using System.Diagnostics;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Rendering;
using SkiaSharp;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>What a frame costs, recorded instead of drawn: the work of the program, not of the rasteriser.</summary>
public class RenderTests
{
    /// <summary>A survey grid: the vertices of a row have close numbers, as in most imported surfaces.</summary>
    private static BlastProject Survey(int side)
    {
        var p = BlastProject.CreateNew("render");
        var s = new SurfaceEntity { Name = "Survey", Kind = SurfaceKind.Topography };
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                s.Vertices.Add(new Vec3(x * 2.0, y * 2.0, (x + y) % 7));
        for (int y = 0; y + 1 < side; y++)
            for (int x = 0; x + 1 < side; x++)
            {
                int a = y * side + x, b = a + 1, c = a + side, d = c + 1;
                s.Triangles.AddRange(new[] { a, b, c, b, d, c });
            }
        p.Add(s);
        return p;
    }

    private static double FirstFrameMs(BlastProject p)
    {
        var cam = new Camera { Width = 800, Height = 600 };
        var bounds = Bounds3.Empty;
        foreach (var v in p.Entities.OfType<SurfaceEntity>().Single().Vertices) bounds.Add(v);
        cam.Fit(bounds, 0.1);
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0, 0, 800, 600));
        var clock = Stopwatch.StartNew();
        PlanRenderer.Render(canvas, cam, p, new SceneOptions());
        var ms = clock.Elapsed.TotalMilliseconds;
        recorder.EndRecording().Dispose();
        return ms;
    }

    private static byte[] Pixels(Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(400, 300);
        using var canvas = new SKCanvas(bitmap);
        draw(canvas);
        return bitmap.Bytes;
    }

    /// <summary>A slope of two triangles, 100 m square and rising to the north, or the same corners and a row of points as a cloud.</summary>
    private static BlastProject Slope(SurfaceKind kind = SurfaceKind.Topography)
    {
        var p = BlastProject.CreateNew("slope");
        var s = new SurfaceEntity { Name = "Slope", Kind = kind, ShowWireframe = false, Vertices = { new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 100, 100), new Vec3(0, 100, 100) }, Triangles = { 0, 1, 2, 0, 2, 3 } };
        for (int y = 2; y < 100; y += 4) s.Vertices.Add(new Vec3(50, y, y));
        p.Add(s);
        return p;
    }

    /// <summary>How many pixels the slope takes in a view across it, or from above, cut to a band.</summary>
    private static int Ink(BlastProject p, double band, double pitch = 0, IReadOnlyDictionary<SurfaceKind, SKColor>? colors = null)
    {
        var cam = new Camera { Width = 400, Height = 300, Pitch = pitch, Yaw = 0, Scale = 2, Target = new Vec3(50, 50, 50) };
        var options = new SceneOptions { DrawSummaryBlock = false, DrawNorthArrow = false, DrawScaleBar = false, ShowGrid = false, SliceHalfWidth = band, SurfaceColors = colors };
        var empty = BlastProject.CreateNew("empty");
        var drawn = Pixels(c => PlanRenderer.Render(c, cam, p, options));
        var blank = Pixels(c => PlanRenderer.Render(c, cam, empty, options));
        return Enumerable.Range(0, drawn.Length / 4).Count(i => Enumerable.Range(0, 4).Any(k => drawn[4 * i + k] != blank[4 * i + k]));
    }

    [Fact]
    public void AnOpenBandCutsASurfaceWithNoVertexInIt()
    {
        var narrow = Ink(Slope(), 5);
        var wide = Ink(Slope(), 20);
        Assert.True(narrow > 0, "the triangles cross the band");
        Assert.InRange((double)wide / narrow, 3.5, 4.5);
        Assert.True(Ink(Slope(), 500) > wide);
    }

    [Fact]
    public void ABandFromAboveCutsASurfaceToo()
    {
        var narrow = Ink(Slope(), 5, 90);
        Assert.True(narrow > 0);
        Assert.InRange((double)Ink(Slope(), 20, 90) / narrow, 3.5, 4.5);
    }

    [Fact]
    public void ABandLeavesThePointsOfACloudInIt()
    {
        var colors = new Dictionary<SurfaceKind, SKColor> { [SurfaceKind.PointCloud] = SKColors.Red };
        var narrow = Ink(Slope(SurfaceKind.PointCloud), 5, 0, colors);
        Assert.True(narrow > 0);
        Assert.True(Ink(Slope(SurfaceKind.PointCloud), 20, 0, colors) > 2 * narrow);
    }

    /// <summary>The colour in the middle of the slope seen from above, with the fill and the points drawn at the alphas given.</summary>
    private static SKColor Middle(SurfaceKind kind, byte? fill, byte? points)
    {
        var cam = new Camera { Width = 400, Height = 300, Pitch = 90, Scale = 2, Target = new Vec3(30, 50, 50) };
        var colors = new Dictionary<SurfaceKind, SKColor> { [kind] = SKColors.Red };
        var options = new SceneOptions { DrawSummaryBlock = false, DrawNorthArrow = false, DrawScaleBar = false, ShowGrid = false, SurfaceColors = colors, Background = SKColors.Black, SurfaceAlpha = fill, PointCloudAlpha = points };
        using var bitmap = new SKBitmap(400, 300);
        using var canvas = new SKCanvas(bitmap);
        PlanRenderer.Render(canvas, cam, Slope(kind), options);
        // a point of the cloud lies on x = 50: 20 m to the right of the middle, 40 px
        return kind == SurfaceKind.PointCloud ? bitmap.GetPixel(240, 150) : bitmap.GetPixel(200, 150);
    }

    [Fact]
    public void TheSurfaceFillTakesTheTransparencyOfTheSettings()
    {
        var opaque = Middle(SurfaceKind.Topography, 255, null);
        var half = Middle(SurfaceKind.Topography, 128, null);
        var none = Middle(SurfaceKind.Topography, 0, null);
        Assert.Equal(SKColors.Black, none);
        Assert.True(opaque.Red > half.Red && half.Red > 0, $"{opaque} {half}");
        Assert.Equal(0, BlastDesigner.IO.ProgramSettings.Alpha(100));
        Assert.Equal(255, BlastDesigner.IO.ProgramSettings.Alpha(0));
        Assert.InRange(BlastDesigner.IO.ProgramSettings.Alpha(BlastDesigner.IO.ProgramSettings.DefaultSurfaceTransparency), 228, 232);
    }

    [Fact]
    public void ThePointsOfACloudTakeTheirTransparency()
    {
        var opaque = Middle(SurfaceKind.PointCloud, null, 255);
        var faint = Middle(SurfaceKind.PointCloud, null, 60);
        Assert.True(opaque.Red > faint.Red && faint.Red > 0, $"{opaque} {faint}");
    }

    [Fact]
    public void ABandCutsTheWireframeOfASurface()
    {
        var p = Slope();
        p.Entities.OfType<SurfaceEntity>().Single().ShowWireframe = true;
        var mesh = Ink(Slope(), 5);
        var wired = Ink(p, 5);
        // the edges stay on the strip: outside it they would add far more than the fill has
        Assert.InRange(wired, mesh, mesh * 1.2);
    }

    /// <summary>How many pixels of a line of a kind come out in a colour.</summary>
    private static int LinePixels(LineKind kind, uint? own, SceneOptions options, SKColor colour)
    {
        var p = BlastProject.CreateNew("line");
        p.Add(new PolylineEntity { Kind = kind, Color = own, Points = { new Vec3(0, 0, 0), new Vec3(40, 0, 0) } });
        var cam = new Camera { Width = 400, Height = 300, Scale = 5, Target = new Vec3(20, 0.1, 0) };
        using var bitmap = new SKBitmap(400, 300);
        using var canvas = new SKCanvas(bitmap);
        PlanRenderer.Render(canvas, cam, p, options);
        return bitmap.Pixels.Count(c => c == colour);
    }

    [Fact]
    public void ASectionLineHasItsColourOnTheScreen()
    {
        var section = new SKColor(0xFFEA580C);
        var geometry = new SKColor(0xFF22B8C8);
        var options = new SceneOptions { DrawSummaryBlock = false, ShowGrid = false, SectionLineColor = section, GeometryColor = geometry };
        Assert.True(LinePixels(LineKind.CrossSection, null, options, section) > 100);
        Assert.Equal(0, LinePixels(LineKind.CrossSection, null, options, geometry));
        // its own colour stays, another line takes that of the geometry
        Assert.Equal(0, LinePixels(LineKind.CrossSection, 0xFF4ADE80, options, section));
        Assert.Equal(0, LinePixels(LineKind.General, null, options, section));
        Assert.True(LinePixels(LineKind.General, null, options, geometry) > 100);
        // a drawing on paper has no such colour
        Assert.Equal(0, LinePixels(LineKind.CrossSection, null, new SceneOptions { DrawSummaryBlock = false, ShowGrid = false }, section));
    }

    /// <summary>Two holes tied together with an initiation point, and with <paramref name="geometry"/> all that is not the blast.</summary>
    private static BlastProject Blast(bool geometry, bool tiesShown)
    {
        var p = BlastProject.CreateNew("blast");
        var a = p.Add(new Hole { Label = "1", Collar = new Vec3(10, 10, 5), Length = 11 });
        var b = p.Add(new Hole { Label = "2", Collar = new Vec3(20, 10, 5), Length = 11 });
        p.Add(new InitiationPoint { Position = a.Collar, HoleId = a.Id });
        Engine.Timing.Connect(p, a.Id, b.Id, p.Products.First(x => x.Kind == ProductKind.SurfaceConnector));
        Engine.Timing.Solve(p);
        p.View.ShowTies = tiesShown;
        if (!geometry) return p;
        var s = new SurfaceEntity { Name = "Survey", Kind = SurfaceKind.Topography };
        s.Vertices.AddRange(new[] { new Vec3(0, 0, 0), new Vec3(30, 0, 0), new Vec3(30, 20, 0), new Vec3(0, 20, 0) });
        s.Triangles.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
        p.Add(s);
        p.Add(new PolylineEntity { Points = new() { new Vec3(0, 5, 0), new Vec3(30, 15, 0) } });
        p.Add(new PointEntity { Position = new Vec3(5, 15, 0), Label = "point" });
        p.Add(new TextEntity { Text = "text", Position = new Vec3(15, 15, 0), Height = 2 });
        p.Add(new CircleEntity { Center = new Vec3(25, 5, 0), Radius = 3 });
        return p;
    }

    private static byte[] Plan(BlastProject p, SceneOptions options)
    {
        var cam = new Camera { Width = 400, Height = 300 };
        var bounds = Bounds3.Empty;
        bounds.Add(new Vec3(0, 0, 0));
        bounds.Add(new Vec3(30, 20, 0));
        cam.Fit(bounds, 0.1);
        return Pixels(c => PlanRenderer.Render(c, cam, p, options));
    }

    [Fact]
    public void WithoutGeometryOnlyTheBlastIsDrawnTiesIncluded()
    {
        var blast = Plan(Blast(false, true), new SceneOptions());
        Assert.Equal(blast, Plan(Blast(true, false), new SceneOptions { HideGeometry = true }));
        Assert.NotEqual(blast, Plan(Blast(true, true), new SceneOptions()));
        Assert.NotEqual(blast, Plan(Blast(false, false), new SceneOptions()));
    }

    [Fact]
    public async Task ARecordedFrameIsPlayedOnAnotherThreadWithoutTheProject()
    {
        var p = Survey(12);
        p.Add(new Hole { Label = "1", Collar = new Vec3(10, 10, 5), Length = 11 });
        var cam = new Camera { Width = 400, Height = 300 };
        var bounds = Bounds3.Empty;
        foreach (var v in p.Entities.OfType<SurfaceEntity>().Single().Vertices) bounds.Add(v);
        cam.Fit(bounds, 0.1);
        var options = new SceneOptions();
        var drawn = Pixels(c => PlanRenderer.Render(c, cam, p, options));
        using var recorder = new SKPictureRecorder();
        PlanRenderer.Render(recorder.BeginRecording(new SKRect(0, 0, 400, 300)), cam, p, options);
        using var frame = recorder.EndRecording();
        // what the UI thread may do while the frame waits for the render thread
        p.Entities.Clear();
        p.Layers.Clear();
        cam.Pan(50, 50);
        Assert.Equal(drawn, await Task.Run(() => Pixels(c => c.DrawPicture(frame))));
    }

    [Fact]
    public void TheFirstFrameOfALargeSurfaceTakesNoLongerThanTheNextOnes()
    {
        FirstFrameMs(Survey(10));
        var ms = FirstFrameMs(Survey(300));
        Assert.True(ms < 2000, $"{ms:0} ms");
    }

    [Fact]
    public void TheFirstFrameOfASurfaceWithItsWireframeIsQuickToo()
    {
        FirstFrameMs(Survey(10));
        var ms = FirstFrameMs(Survey(140));
        Assert.True(ms < 400, $"{ms:0} ms");
    }

    /// <summary>The circle that turns a pattern is filled solid: green when the grid can be turned, red when it cannot.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PatternTurnCircleIsGreenOrRed(bool refused)
    {
        var p = BlastProject.CreateNew("turn");
        var pat = new HolePattern { Center = new Vec3(0, 0, 0), Azimuth = 0, Burden = 3 };
        var cam = new Camera { Width = 400, Height = 300, Scale = 4, Target = new Vec3(0, 0, 0) };
        var overlay = new PatternRotateHandleOverlay(() => new[] { pat }, _ => refused, () => (new SKColor(0xFF16A34A), new SKColor(0xFFDC2626)));
        var (x, y) = cam.ToScreen(PatternRotateHandleOverlay.CircleCentre(pat, pat.Azimuth, cam));
        using var bitmap = new SKBitmap(400, 300);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            overlay.Draw(canvas, cam, p);
        }
        var want = refused ? new SKColor(0xFFDC2626) : new SKColor(0xFF16A34A);
        // the middle and a pixel off it, inside the edge: solid, no ring
        foreach (var (dx, dy) in new[] { (0, 0), (2, 0), (0, -2) })
            Assert.Equal(want, bitmap.GetPixel((int)Math.Round(x) + dx, (int)Math.Round(y) + dy));
    }

    /// <summary>The burden arrow ends at the edge of the circle, not in its middle: the centre is a radius beyond the tip.</summary>
    [Fact]
    public void PatternTurnCircleTouchesTheArrowTip()
    {
        var pat = new HolePattern { Center = new Vec3(0, 0, 0), Azimuth = 30, Burden = 3 };
        var cam = new Camera { Width = 400, Height = 300, Scale = 4, Target = new Vec3(0, 0, 0) };
        foreach (var big in new[] { false, true })
        {
            var (tx, ty) = cam.ToScreen(PatternRotateHandleOverlay.ArrowTip(pat, pat.Azimuth, cam));
            var (cx, cy) = cam.ToScreen(PatternRotateHandleOverlay.CircleCentre(pat, pat.Azimuth, cam, big));
            var (ox, oy) = cam.ToScreen(pat.Center);
            Assert.Equal(PatternRotateHandleOverlay.RadiusPx(big), Math.Sqrt((cx - tx) * (cx - tx) + (cy - ty) * (cy - ty)), 3);
            // beyond the tip, along the arrow
            Assert.True(Math.Sqrt((cx - ox) * (cx - ox) + (cy - oy) * (cy - oy)) > Math.Sqrt((tx - ox) * (tx - ox) + (ty - oy) * (ty - oy)));
        }
    }

    /// <summary>A flat square surface of a kind at an elevation, over the square 0…40.</summary>
    private static SurfaceEntity Flat(SurfaceKind kind, double z) => new()
    {
        Kind = kind, ShowWireframe = false,
        Vertices = { new(0, 0, z), new(40, 0, z), new(40, 40, z), new(0, 40, z) }, Triangles = { 0, 1, 2, 0, 2, 3 }
    };

    private static SKColor Pixel(BlastProject p, Camera cam, Vec3 at, byte? alpha = 255)
    {
        var colors = new Dictionary<SurfaceKind, SKColor> { [SurfaceKind.Bench] = SKColors.Red, [SurfaceKind.Grade] = SKColors.Blue };
        var options = new SceneOptions { DrawSummaryBlock = false, DrawNorthArrow = false, DrawScaleBar = false, ShowGrid = false, SurfaceColors = colors, Background = SKColors.Black, SurfaceAlpha = alpha };
        using var bitmap = new SKBitmap(400, 300);
        using var canvas = new SKCanvas(bitmap);
        PlanRenderer.Render(canvas, cam, p, options);
        var (x, y) = cam.ToScreen(at);
        return bitmap.GetPixel((int)x, (int)y);
    }

    [Fact]
    public void TheUpperSurfaceCoversTheLower_WhicheverComesFirst()
    {
        var cam = new Camera { Width = 400, Height = 300, Pitch = 90, Scale = 5, Target = new Vec3(20, 20, 0) };
        foreach (var upperFirst in new[] { true, false })
        {
            var p = BlastProject.CreateNew("order");
            if (upperFirst) { p.Add(Flat(SurfaceKind.Bench, 10)); p.Add(Flat(SurfaceKind.Grade, 0)); }
            else { p.Add(Flat(SurfaceKind.Grade, 0)); p.Add(Flat(SurfaceKind.Bench, 10)); }
            var c = Pixel(p, cam, new Vec3(20, 20, 10));
            Assert.True(c.Red > c.Blue, $"upper first {upperFirst}: {c}");
        }
    }

    [Fact]
    public void ALineUnderASurfaceGoesUnderItIn3D_OneAboveOverIt_InThePlanBothShow()
    {
        var oblique = new Camera { Width = 400, Height = 300, Pitch = 60, Yaw = 0, Scale = 5, Target = new Vec3(20, 20, 0) };
        var plan = new Camera { Width = 400, Height = 300, Pitch = 90, Scale = 5, Target = new Vec3(20, 20, 0) };
        foreach (var (z, seen) in new[] { (-5.0, false), (5.0, true) })
        {
            var p = BlastProject.CreateNew("line");
            var line = p.Add(new PolylineEntity { Points = { new(5, 20, z), new(35, 20, z) }, Color = 0xFF00FF00 });
            p.FindLayer(line.LayerId)!.LineWidth = 4;
            p.Add(Flat(SurfaceKind.Bench, 0));
            var c = Pixel(p, oblique, new Vec3(20, 20, z));
            Assert.True(seen == c.Green > c.Red, $"3D, z {z}: {c}");
            // a 2D drawing does not go under the surfaces in the plan
            var top = Pixel(p, plan, new Vec3(20, 20, z));
            Assert.True(top.Green > top.Red, $"plan, z {z}: {top}");
        }
    }

    [Fact]
    public void ALargeSurfaceBehindIsNotDrawnOverANearerOne()
    {
        // from the south, low: a small bench near the camera at 5 m, a large grade far and wide at 0 whose nearest point is
        // nearer than the bench's: whole in the order it would be drawn over the bench
        var cam = new Camera { Width = 400, Height = 300, Pitch = 20, Yaw = 0, Scale = 4, Target = new Vec3(20, 20, 0) };
        var p = BlastProject.CreateNew("large");
        var bench = Flat(SurfaceKind.Bench, 5);
        bench.Vertices = new() { new(10, 15, 5), new(30, 15, 5), new(30, 25, 5), new(10, 25, 5) };
        p.Add(bench);
        var grade = Flat(SurfaceKind.Grade, 0);
        grade.Vertices = new() { new(-200, -60, 0), new(200, -60, 0), new(200, 400, 0), new(-200, 400, 0) };
        p.Add(grade);
        var c = Pixel(p, cam, new Vec3(20, 20, 5));
        Assert.True(c.Red > c.Blue, c.ToString());
    }

    [Fact]
    public void TheKeptOrderGoesWhenALineMoves()
    {
        // two crossing lines, the red one above; then the green one is lifted over it: the order of the last frame is not kept
        var cam = new Camera { Width = 400, Height = 300, Pitch = 90, Scale = 5, Target = new Vec3(20, 20, 0) };
        var p = BlastProject.CreateNew("kept");
        var red = p.Add(new PolylineEntity { Points = { new(5, 20, 5), new(35, 20, 5) }, Color = 0xFFFF0000 });
        var green = p.Add(new PolylineEntity { Points = { new(20, 5, 0), new(20, 35, 0) }, Color = 0xFF00FF00 });
        var layer = p.FindLayer(red.LayerId)!;
        layer.LineWidth = 4;
        var cross = new Vec3(20, 20, 0);
        var first = Pixel(p, cam, cross);
        Assert.True(first.Red > first.Green, first.ToString());
        green.Translate(new Vec3(0, 0, 10));
        var after = Pixel(p, cam, cross);
        Assert.True(after.Green > after.Red, after.ToString());
    }

    [Fact]
    public void WithHiddenPalerWhatIsBehindASurfaceGoesPaler()
    {
        var cam = new Camera { Width = 400, Height = 300, Pitch = 90, Scale = 5, Target = new Vec3(20, 20, 0) };
        // the bench half over the grade: x 0…20
        var p = BlastProject.CreateNew("pale");
        var bench = Flat(SurfaceKind.Bench, 10);
        bench.Vertices = new() { new(0, 0, 10), new(20, 0, 10), new(20, 40, 10), new(0, 40, 10) };
        p.Add(bench);
        p.Add(Flat(SurfaceKind.Grade, 0));
        var map = DepthMap.Of(cam, p.Entities.OfType<SurfaceEntity>().ToList(), s => s.Vertices.Count);
        Assert.True(map.Hidden(cam, new Vec3(10, 20, 0)));
        Assert.False(map.Hidden(cam, new Vec3(30, 20, 0)));
        Assert.False(map.Hidden(cam, new Vec3(10, 20, 10)));
        // half see-through bench: under it the grade is not drawn without the eye, a quarter with it; off the bench the same
        var under = new Vec3(10, 20, 10);
        var off = new Vec3(30, 20, 0);
        var plainUnder = Pixel(p, cam, under, 128);
        var plainOff = Pixel(p, cam, off, 128);
        Assert.Equal(0, plainUnder.Blue);
        p.View.FadeHidden = true;
        var paleUnder = Pixel(p, cam, under, 128);
        Assert.True(paleUnder.Blue > 0 && paleUnder.Blue < plainOff.Blue / 2, $"{paleUnder} {plainOff}");
        Assert.Equal(plainOff, Pixel(p, cam, off, 128));
        // kept in the file
        Assert.True(BlastDesigner.Domain.ProjectJson.Deserialize(BlastDesigner.Domain.ProjectJson.Serialize(p)).View.FadeHidden);
    }
    /// <summary>A selected line gets a band against the selection colour round it, as the hole labels do; an unselected one none.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ASelectedObjectHasAHalo(bool selected, bool halo)
    {
        var p = BlastProject.CreateNew("halo");
        var line = new PolylineEntity { LayerId = p.Layers[0].Id, Points = new List<Vec3> { new(-20, 0, 0), new(20, 0, 0) } };
        p.Entities.Add(line);
        var cam = new Camera { Width = 400, Height = 300, Scale = 4, Target = new Vec3(0, 0, 0) };
        var options = new SceneOptions
        {
            Background = SKColors.Black, ShowGrid = false, DrawNorthArrow = false, DrawScaleBar = false, DrawSummaryBlock = false,
            Selection = selected ? new HashSet<Guid> { line.Id } : new HashSet<Guid>(), SelectionColor = new SKColor(0xFFFF00FF), SelectionHalo = halo
        };
        using var bitmap = new SKBitmap(400, 300);
        using (var canvas = new SKCanvas(bitmap)) PlanRenderer.Render(canvas, cam, p, options);
        var (x, y) = cam.ToScreen(new Vec3(0, 0, 0));
        // the selected line is 2 px wide: 2 px beyond its edge is the halo, or the background
        var beside = bitmap.GetPixel((int)x, (int)Math.Round(y) - 3);
        var lit = selected && halo;
        Assert.Equal(lit, beside.Red > 150 && beside.Green > 150 && beside.Blue > 150);
        // the line itself stays in the selection colour over its halo
        if (selected) Assert.Equal(new SKColor(0xFFFF00FF), bitmap.GetPixel((int)x, (int)Math.Round(y)));
    }

    /// <summary>How many pixels of a column across a selected line of a 4 px layer are not the background.</summary>
    private static int LitAcross(float extra, int halo)
    {
        var p = BlastProject.CreateNew("width");
        p.Layers[0].LineWidth = 4;
        var line = new PolylineEntity { LayerId = p.Layers[0].Id, Points = new List<Vec3> { new(-20, 0, 0), new(20, 0, 0) } };
        p.Entities.Add(line);
        var cam = new Camera { Width = 400, Height = 300, Scale = 4, Target = new Vec3(0, 0, 0) };
        var options = new SceneOptions
        {
            Background = SKColors.Black, ShowGrid = false, DrawNorthArrow = false, DrawScaleBar = false, DrawSummaryBlock = false,
            Selection = new HashSet<Guid> { line.Id }, SelectionColor = new SKColor(0xFFFF00FF),
            SelectionExtra = extra, SelectionHalo = halo > 0, SelectionHaloPx = halo
        };
        using var bitmap = new SKBitmap(400, 300);
        using (var canvas = new SKCanvas(bitmap)) PlanRenderer.Render(canvas, cam, p, options);
        var (x, _) = cam.ToScreen(new Vec3(0, 0, 0));
        return Enumerable.Range(100, 100).Count(y => { var c = bitmap.GetPixel((int)x, y); return c.Red > 30 || c.Green > 30 || c.Blue > 30; });
    }

    [Fact]
    public void ASelectedLineIsTwoPixelsThicker_NotTwiceAsThick()
    {
        Assert.Equal(4, LitAcross(0, 0));
        Assert.Equal(6, LitAcross(2, 0));
    }

    [Fact]
    public void TheHaloIsOneOrTwoPixels()
    {
        Assert.Equal(4 + 2, LitAcross(0, 1));
        Assert.Equal(4 + 4, LitAcross(0, 2));
    }

    [Fact]
    public void TheHaloIsDarkRoundALightSelectionAndLightRoundADarkOne()
    {
        Assert.True(PlanRenderer.HaloColor(new SKColor(0xFFFFFF00)).Red < 50);
        Assert.True(PlanRenderer.HaloColor(new SKColor(0xFFFF00FF)).Red > 200);
    }

    [Fact]
    public void ASurfaceHaloGoesRoundItsOuterEdgeOnly()
    {
        // a square of four triangles round a middle point: the four sides are the edge, the four spokes inside are not
        var s = new SurfaceEntity
        {
            Vertices = new List<Vec3> { new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0), new(5, 5, 0) },
            Triangles = new List<int> { 0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4 }
        };
        var edges = PlanRenderer.OuterEdges(s).Select(e => (Math.Min(e.A, e.B), Math.Max(e.A, e.B))).OrderBy(e => e).ToList();
        Assert.Equal(new[] { (0, 1), (0, 3), (1, 2), (2, 3) }, edges);
    }
}
