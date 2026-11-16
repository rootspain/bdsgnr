using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>The Transform tool: the map (scale, tilt, mirror about a base point) and what it does to each kind of object.</summary>
public class SpatialTransformTests
{
    private static void Near(Vec3 want, Vec3 got, double tol = 1e-9)
    {
        Assert.True(Vec3.Distance(want, got) < tol, $"want {want}, got {got}");
    }

    [Fact]
    public void ScaleComesFirst_ThenTheTilts_TheTiltAboutZIsClockwise()
    {
        var t = new SpatialTransform { ScaleX = 2, TiltZ = 90 };
        // two along X, then a quarter clockwise: east goes south
        Near(new Vec3(0, -2, 0), t.Apply(new Vec3(1, 0, 0)));
        // about X by the right-hand rule: north goes up
        Near(new Vec3(0, 0, 1), new SpatialTransform { TiltX = 90 }.Apply(new Vec3(0, 1, 0)));
        // about Y: up goes east
        Near(new Vec3(1, 0, 0), new SpatialTransform { TiltY = 90 }.Apply(new Vec3(0, 0, 1)));
    }

    [Fact]
    public void TheBasePointStays_AndTheMoveComesLast()
    {
        var pivot = new Vec3(100, 200, 50);
        var t = new SpatialTransform { Pivot = pivot, ScaleX = 3, ScaleY = 0.5, ScaleZ = 2, TiltX = 10, TiltY = -20, TiltZ = 33 };
        Near(pivot, t.Apply(pivot));
        Near(pivot + new Vec3(1, 2, 3), (t with { Move = new Vec3(1, 2, 3) }).Apply(pivot));
    }

    [Fact]
    public void TheAxesOfAnObject_ScaleAlongTheTurnedAxes()
    {
        // axes turned a quarter clockwise: X of the frame points south
        var t = new SpatialTransform { AxesAzimuth = 90, ScaleX = 2 };
        Near(new Vec3(0, -2, 0), t.Apply(new Vec3(0, -1, 0)));
        Near(new Vec3(1, 0, 0), t.Apply(new Vec3(1, 0, 0)));
    }

    [Fact]
    public void AMirrorFlipsTheAxisAboutTheBasePoint()
    {
        var t = new SpatialTransform { Pivot = new Vec3(10, 0, 0), MirrorX = true };
        Near(new Vec3(8, 5, 1), t.Apply(new Vec3(12, 5, 1)));
        Assert.True(t.Matrix.IsMirror);
        // both mirrors are a half turn, not a mirror
        Assert.False((t with { MirrorY = true }).Matrix.IsMirror);
    }

    [Fact]
    public void TheMapAndItsInverseGiveThePointBack()
    {
        var t = new SpatialTransform { Pivot = new Vec3(5, -3, 2), AxesAzimuth = 27, ScaleX = 1.7, ScaleY = 0.4, ScaleZ = 2.2, TiltX = 12, TiltY = -40, TiltZ = 200, MirrorY = true, Move = new Vec3(1, 1, 1) };
        var m = t.Matrix;
        var back = m.Inverse();
        foreach (var p in new[] { new Vec3(0, 0, 0), new Vec3(123.4, -56.7, 89), new Vec3(-1, 2, -3) })
            Near(p, back.Apply(m.Apply(p)), 1e-7);
    }

    /// <summary>The old Transform coordinates window goes through the same map and gives what its own formula gave.</summary>
    [Fact]
    public void MakeTransformGivesWhatItsOldFormulaGave()
    {
        var pivot = new Vec3(10, 20, 5);
        var move = new Vec3(3, -4, 1);
        const double deg = 37, scale = 1.5;
        Vec3 Old(Vec3 p)
        {
            var c = Math.Cos(-deg * GeoMath.Deg);
            var s = Math.Sin(-deg * GeoMath.Deg);
            var d = p - pivot;
            return pivot + new Vec3(d.X * c - d.Y * s, d.X * s + d.Y * c, d.Z) * scale + move;
        }
        var f = GeneralOps.MakeTransform(move, deg, pivot, scale);
        foreach (var p in new[] { new Vec3(0, 0, 0), new Vec3(15, 25, 8), new Vec3(-7, 3, 100) })
            Near(Old(p), f(p), 1e-9);
    }

    [Fact]
    public void HolesTiesPatternOutlinesAndLockedLayersAreLeft()
    {
        var p = BlastProject.CreateNew("t");
        var hole = new Hole { Collar = new Vec3(1, 1, 0), Length = 10 };
        var line = new PolylineEntity { Points = { new Vec3(0, 0, 0), new Vec3(10, 0, 0) } };
        var outline = new PolylineEntity { Closed = true, Points = { new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 10, 0) } };
        var locked = p.GetOrCreateLayer("locked");
        locked.Locked = true;
        var onLocked = new PolylineEntity { Points = { new Vec3(0, 0, 0), new Vec3(5, 0, 0) } };
        p.Add(hole); p.Add(line); p.Add(outline); p.Add(onLocked, locked);
        p.Patterns.Add(new HolePattern { OutlineEntityId = outline.Id });
        var collar = hole.Collar;
        var r = SpatialTransforms.Apply(p, new[] { hole.Id, line.Id, outline.Id, onLocked.Id }, new SpatialTransform { ScaleX = 2 });
        Assert.Equal(new TransformOutcome(1, 1, 1, 1), r);
        Assert.Equal(collar, hole.Collar);
        Near(new Vec3(20, 0, 0), line.Points[1]);
        Near(new Vec3(10, 0, 0), outline.Points[1]);
        Near(new Vec3(5, 0, 0), onLocked.Points[1]);
    }

    [Fact]
    public void TheOutlineOfAPattern_IsTilted_ButNotScaledOrMirrored()
    {
        var p = BlastProject.CreateNew("t");
        var outline = new PolylineEntity { Closed = true, Points = { new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 10, 0) } };
        p.Add(outline);
        p.Patterns.Add(new HolePattern { OutlineEntityId = outline.Id });
        var tilt = new SpatialTransform { TiltX = 10 };
        Assert.Single(SpatialTransforms.Taken(p, new[] { outline.Id }, SpatialTransform.None));
        Assert.Equal(new TransformOutcome(1, 0, 0, 0), SpatialTransforms.Apply(p, new[] { outline.Id }, tilt));
        Assert.Equal(10 * Math.Sin(10 * Math.PI / 180), outline.Points[2].Z, 9);
        var before = outline.Points.ToList();
        Assert.Equal(new TransformOutcome(0, 0, 1, 0), SpatialTransforms.Apply(p, new[] { outline.Id }, new SpatialTransform { TiltX = 5, ScaleY = 2 }));
        Assert.Equal(new TransformOutcome(0, 0, 1, 0), SpatialTransforms.Apply(p, new[] { outline.Id }, new SpatialTransform { MirrorX = true }));
        Assert.Equal(before, outline.Points);
    }

    [Fact]
    public void AScaleOfNoughtOrATiltOf89IsRefused()
    {
        Assert.NotNull(new SpatialTransform { ScaleY = 0 }.Problem);
        Assert.NotNull(new SpatialTransform { TiltX = 89 }.Problem);
        Assert.Null(new SpatialTransform { TiltZ = 400 }.Problem);
        var p = BlastProject.CreateNew("t");
        Assert.Throws<ArgumentException>(() => SpatialTransforms.Apply(p, Array.Empty<Guid>(), new SpatialTransform { ScaleX = -1 }));
    }

    [Fact]
    public void ACircleStaysACircle_ItsRadiusTakesTheScale()
    {
        var c = new CircleEntity { Center = new Vec3(10, 0, 0), Radius = 2 };
        c.Transform(new SpatialTransform { ScaleX = 3, ScaleY = 3, ScaleZ = 3 });
        Near(new Vec3(30, 0, 0), c.Center);
        Assert.Equal(6, c.Radius, 9);
        // a tilt moves the centre only
        c.Transform(new SpatialTransform { TiltX = 30 });
        Assert.Equal(6, c.Radius, 9);
    }

    [Fact]
    public void AText_TakesTheScaleInPlanAndTurns_ButIsNotMirrored()
    {
        var t = new TextEntity { Position = new Vec3(1, 0, 0), Height = 2, Rotation = 0 };
        t.Transform(new SpatialTransform { ScaleX = 2, ScaleY = 2, TiltZ = 90 });
        Assert.Equal(4, t.Height, 9);
        // a quarter clockwise: the line runs south
        Assert.Equal(-90, t.Rotation, 9);
        Near(new Vec3(0, -2, 0), t.Position);
        var m = new TextEntity { Position = new Vec3(3, 0, 0), Height = 1, Rotation = 0 };
        m.Transform(new SpatialTransform { MirrorX = true });
        // the line runs the other way, its letters upright: still a text one reads
        Assert.Equal(180, Math.Abs(m.Rotation), 9);
        Assert.Equal(1, m.Height, 9);
    }

    private static List<Vec3> Sorted(IEnumerable<Vec3> points) =>
        points.Select(v => new Vec3(Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6))).OrderBy(v => v.X).ThenBy(v => v.Y).ThenBy(v => v.Z).ToList();

    [Fact]
    public void ABlock_ScalesAlongItsAxes_TurnsAndMirrors_ButIsNotTilted()
    {
        var b = new BlockEntity { Origin = new Vec3(0, 0, 0), Azimuth = 30, Length = 10, Width = 4, Height = 6 };
        var along = new SpatialTransform { AxesAzimuth = b.Azimuth - 90, ScaleX = 2, ScaleZ = 0.5 };
        var want = Sorted(b.Corners().Select(along.Apply));
        b.Transform(along);
        Assert.Equal(20, b.Length, 9);
        Assert.Equal(4, b.Width, 9);
        Assert.Equal(3, b.Height, 9);
        Assert.Equal(want, Sorted(b.Corners()));

        var mirror = new SpatialTransform { Pivot = new Vec3(3, 3, 0), MirrorX = true, TiltZ = 15 };
        want = Sorted(b.Corners().Select(mirror.Apply));
        b.Transform(mirror);
        Assert.Equal(want, Sorted(b.Corners()));

        Assert.False(b.Takes(new SpatialTransform { TiltY = 5 }));
        Assert.True(b.Takes(new SpatialTransform { TiltZ = 5 }));
    }

    [Fact]
    public void ADimensionLine_GoesWhereTheMapTakesIt_AlsoMirrored()
    {
        var d = new DimensionEntity { Start = new Vec3(0, 0, 0), End = new Vec3(10, 0, 0), Offset = 2 };
        var t = new SpatialTransform { Pivot = new Vec3(5, 0, 0), MirrorY = true, ScaleY = 1.5 };
        var (a, b) = d.Line();
        var (wa, wb) = (t.Apply(a), t.Apply(b));
        d.Transform(t);
        var (ga, gb) = d.Line();
        Near(wa, ga); Near(wb, gb);
        Assert.Equal(-3, d.Offset, 9);
    }

    private static double PlanArea(SurfaceEntity s, int tri)
    {
        var (a, b, c) = (s.Vertices[s.Triangles[tri * 3]], s.Vertices[s.Triangles[tri * 3 + 1]], s.Vertices[s.Triangles[tri * 3 + 2]]);
        return ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) / 2;
    }

    [Fact]
    public void AMirroredSurfaceStillFacesUp()
    {
        var s = new SurfaceEntity { Vertices = { new(0, 0, 0), new(10, 0, 0), new(0, 10, 0) }, Triangles = { 0, 1, 2 } };
        Assert.True(PlanArea(s, 0) > 0);
        s.Transform(new SpatialTransform { MirrorX = true });
        Assert.True(PlanArea(s, 0) > 0);
        Near(new Vec3(-10, 0, 0), s.Vertices[1]);
    }

    [Fact]
    public void AMirroredChevron_KeepsItsLeftSegmentOnTheLeft_WithItsDelay()
    {
        var pts = Chevron.Build(new Vec3(0, 0, 0), new Vec3(0, 1, 0), 30, 10);
        var line = new TimingLineEntity
        {
            Front = TimingFrontShape.Chevron, Style = TimingLineStyle.BurnLine, Points = pts.ToList(),
            Segments = { new TimingLineSegment(5, 0, 0), new TimingLineSegment(9, 0, 0) }
        };
        var t = new SpatialTransform { MirrorX = true };
        var (left, right) = (t.Apply(pts[0]), t.Apply(pts[2]));
        line.Transform(t);
        // the mirrored right end is now on the left: first, and with the delay of what was the right segment
        Near(right, line.Points[0]);
        Near(left, line.Points[2]);
        Assert.Equal(9, line.Segments[0].BurnRateMsPerM);
        Assert.Equal(5, line.Segments[1].BurnRateMsPerM);
    }

    [Fact]
    public void ThePreviewIsACopyWithTheSameId_TheObjectStays()
    {
        var line = new PolylineEntity { Points = { new Vec3(0, 0, 0), new Vec3(10, 0, 0) } };
        var copy = (PolylineEntity)SpatialTransforms.Preview(line, new SpatialTransform { ScaleX = 2 });
        Assert.Equal(line.Id, copy.Id);
        Near(new Vec3(20, 0, 0), copy.Points[1]);
        Near(new Vec3(10, 0, 0), line.Points[1]);
    }

    [Fact]
    public void TheFrameIsTakenInTheTurnedAxes()
    {
        var pts = new[] { new Vec3(0, 0, 0), new Vec3(10, 10, 5) };
        var (lo, hi) = SpatialTransforms.Frame(pts, 0)!.Value;
        Near(new Vec3(0, 0, 0), lo); Near(new Vec3(10, 10, 5), hi);
        // turned 45° counterclockwise: the diagonal (north-east) lies along the X of the frame
        var (lo2, hi2) = SpatialTransforms.Frame(pts, -45)!.Value;
        Assert.Equal(Math.Sqrt(200), hi2.X - lo2.X, 9);
        Assert.Equal(0, hi2.Y - lo2.Y, 9);
        Near(new Vec3(10, 10, 5), SpatialTransforms.FromFrame(hi2, -45));
    }
}
