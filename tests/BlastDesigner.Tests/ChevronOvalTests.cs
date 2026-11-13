using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>A chevron's two segments are independent; the oval through their ends gives the delay every way from the vertex.</summary>
public class ChevronOvalTests
{
    private static readonly Vec3 Apex = new(100, 200, 50);

    /// <summary>A chevron with its ends at the given plan offsets from the vertex: [end 0, vertex, end 2].</summary>
    private static List<Vec3> Shape(Vec3 to0, Vec3 to2) => new() { Apex + to0, Apex, Apex + to2 };

    private static double[] Times(List<Vec3> shape, double delay0, double delay1, params Vec3[] holes)
    {
        var data = new[] { new TimingLineSegment(0, delay0, 0), new TimingLineSegment(0, delay1, 0) };
        var o = new TimingLineOptions { Front = TimingFrontShape.Chevron, Style = TimingLineStyle.TimeLine };
        return EbsTimingLine.RawTimes(shape, data, o, holes);
    }

    [Fact]
    public void TwoEqualPerpendicularSegmentsMakeACircle()
    {
        var a = new Vec3(5, 0, 0);
        var b = new Vec3(0, 5, 0);
        foreach (var deg in new[] { 0.0, 17, 45, 90, 133, 200, 290 })
        {
            var w = new Vec3(Math.Cos(deg * GeoMath.Deg), Math.Sin(deg * GeoMath.Deg), 0);
            Assert.Equal(5, Chevron.OvalRadius(a, b, w), 9);
        }
    }

    [Fact]
    public void TheOvalGoesThroughBothEnds_AlongASegmentTheDelayIsItsOwn()
    {
        // not perpendicular, not alike: the ends are still on the oval
        var shape = Shape(new Vec3(-6, -8, 0), new Vec3(7, -2, 0));
        var u = (shape[0] - Apex).Normalized();
        var v = (shape[2] - Apex).Normalized();
        Assert.Equal(8, Chevron.DelayAlong(shape, 8, 5, u), 9);
        Assert.Equal(5, Chevron.DelayAlong(shape, 8, 5, v), 9);
        // the oval drawn on the plan goes through the ends too
        var oval = Chevron.Oval(shape, 4);
        Assert.True(Vec3.Distance2D(oval[0], shape[0]) < 1e-9);
        Assert.True(Vec3.Distance2D(oval[1], shape[2]) < 1e-9);
    }

    [Fact]
    public void TimesGrowFromTheVertexEveryWay()
    {
        var shape = Shape(new Vec3(-5, -5, 0), new Vec3(5, -5, 0));
        var along0 = (shape[0] - Apex).Normalized();
        var t = Times(shape, 8, 4, Apex, Apex + along0 * 10, Apex - along0 * 10, Apex + new Vec3(0, 20, 0));
        Assert.Equal(0, t[0], 9);
        // 10 m along segment 0: 10 × its delay
        Assert.Equal(80, t[1], 6);
        // 10 m behind the vertex the other way: the oval is closed, the same delay
        Assert.Equal(80, t[2], 6);
        // ahead of the vertex, between the directions: a time too, not out of reach
        Assert.True(t[3] > 0 && t[3] < 1e30);
    }

    [Fact]
    public void AnEndMovesOnItsOwn()
    {
        var shape = Shape(new Vec3(-5, -5, 0), new Vec3(5, -5, 0));
        var moved = Chevron.MoveEnd(shape, 0, Apex + new Vec3(-12, 3, 9));
        Assert.Equal(shape[1], moved[1]);
        Assert.Equal(shape[2], moved[2]);
        // the end takes the elevation of the vertex
        Assert.Equal(new Vec3(Apex.X - 12, Apex.Y + 3, Apex.Z), moved[0]);
        // along the other segment it would make no oval: it stays
        var along = Chevron.MoveEnd(shape, 0, Apex + new Vec3(10, -10, 0));
        Assert.Equal(shape[0], along[0]);
        var straight = Chevron.MoveEnd(shape, 0, Apex + new Vec3(-10, 10, 0));
        Assert.Equal(shape[0], straight[0]);
    }
}
