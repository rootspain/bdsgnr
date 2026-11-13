using BlastDesigner.Domain.Geometry;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>The outline of a lasso or a box: what it rings by the nonzero rule, the lines it cuts, window or crossing.</summary>
public class LassoTests
{
    private static Lasso Of(params (double, double)[] pts) => new(pts);

    [Fact]
    public void AConcaveOutlineRingsItsInsideOnly()
    {
        // a U: the notch between the arms is outside
        var u = Of((0, 0), (30, 0), (30, 30), (20, 30), (20, 10), (10, 10), (10, 30), (0, 30));
        Assert.True(u.Contains(5, 20));
        Assert.True(u.Contains(25, 20));
        Assert.True(u.Contains(15, 5));
        Assert.False(u.Contains(15, 20));
        Assert.False(u.Contains(40, 5));
        Assert.False(u.Contains(-1, 5));
    }

    [Fact]
    public void AFigureEightRingsBothLoops()
    {
        var eight = Of((0, 0), (20, 10), (20, 0), (0, 10));
        // the two loops meet at (10, 5) and wind opposite ways: both are inside by the nonzero rule
        Assert.True(eight.Contains(3, 5));
        Assert.True(eight.Contains(17, 5));
        Assert.False(eight.Contains(10, 1));
        Assert.False(eight.Contains(10, 9));
    }

    [Fact]
    public void ALoopDrawnTwiceLeavesNoHole()
    {
        // two turns round the same way: the inner part is wound twice, still inside (even-odd would cut a hole)
        var twice = Of((0, 0), (40, 0), (40, 40), (0, 40), (0, 5), (35, 5), (35, 35), (5, 35), (5, 2));
        Assert.True(twice.Contains(20, 20));
        Assert.True(twice.Contains(2, 20));
    }

    [Fact]
    public void ALineThroughTheOutlineIsCrossedWithNoVertexInside()
    {
        var box = Lasso.Rect(10, 10, 20, 20);
        Assert.False(box.Contains(0, 15));
        Assert.False(box.Contains(30, 15));
        Assert.True(box.CutsEdge(0, 15, 30, 15));
        Assert.True(box.Crosses(0, 15, 30, 15));
        Assert.False(box.Crosses(0, 25, 30, 25));
        // fully inside: crossed, but cuts no edge
        Assert.True(box.Crosses(12, 12, 18, 18));
        Assert.False(box.CutsEdge(12, 12, 18, 18));
    }

    [Fact]
    public void ManyEdgesInBandsGiveTheSameAnswerAsAllOfThem()
    {
        // a wobbly circle of 400 points: the bands must not lose an edge
        var pts = Enumerable.Range(0, 400).Select(i =>
        {
            double t = 2 * Math.PI * i / 400, r = 100 + 10 * Math.Sin(7 * t);
            return (200 + r * Math.Cos(t), 200 + r * Math.Sin(t));
        }).ToArray();
        var lasso = new Lasso(pts);
        var rnd = new Random(5);
        for (int k = 0; k < 2000; k++)
        {
            double x = rnd.NextDouble() * 400, y = rnd.NextDouble() * 400;
            Assert.Equal(GeoMath.PointInPolygon(pts.Select(p => new Vec3(p.Item1, p.Item2, 0)).ToList(), x, y), lasso.Contains(x, y));
        }
    }

    [Fact]
    public void TheFirstMoveSidewaysDecidesWindowOrCrossing()
    {
        // goes right first, then far to the left and back: still a window
        Assert.False(Lasso.IsCrossing(new (double, double)[] { (100, 100), (104, 101), (110, 103), (40, 150), (100, 102) }));
        // to the left first: a crossing
        Assert.True(Lasso.IsCrossing(new (double, double)[] { (100, 100), (95, 98), (90, 97), (160, 150), (100, 100) }));
        // straight down and back, a little to the left: decided by the side it went further to
        Assert.True(Lasso.IsCrossing(new (double, double)[] { (100, 100), (98, 140), (97, 160), (101, 120) }));
    }

    [Fact]
    public void TooFewPointsRingNothing()
    {
        Assert.True(Of((0, 0), (10, 10)).Empty);
        Assert.False(Of((0, 0), (10, 10)).Contains(5, 5));
        Assert.True(Of().Empty);
    }
}
