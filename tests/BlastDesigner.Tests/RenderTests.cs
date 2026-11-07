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
}
