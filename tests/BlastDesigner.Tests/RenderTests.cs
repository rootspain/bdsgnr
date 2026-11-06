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
