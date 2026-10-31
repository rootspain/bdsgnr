using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>Timing triangulation and First movement arrows.</summary>
public class FirstMovementTests
{
    static List<(Vec3, double)> Grid(Func<double, double, double> time, int nx = 6, int ny = 5, double dx = 4, double dy = 3.5)
    {
        var list = new List<(Vec3, double)>();
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                // a little jitter avoids the exactly cocircular grid squares
                double x = i * dx + 0.05 * Math.Sin(i * 7 + j * 3), y = j * dy + 0.05 * Math.Cos(i * 5 + j * 11);
                list.Add((new Vec3(x, y, 100), time(x, y)));
            }
        return list;
    }

    [Fact]
    public void TriangleCountMatchesReferenceVerify()
    {
        var pts = Grid((x, y) => x);
        var d = TimingDelaunay.Build(pts);
        Assert.Equal(2 * (d.Vertices.Count - d.Duplicates - 3) + 1, d.Triangles.Count);
        Assert.Equal(pts.Count, d.SortedTimes.Length);
    }

    [Fact]
    public void TriangulationIsDelaunay()
    {
        var rnd = new Random(3);
        var pts = Enumerable.Range(0, 80).Select(_ => (new Vec3(rnd.NextDouble() * 100, rnd.NextDouble() * 60, 0), rnd.NextDouble() * 500)).ToList();
        var d = TimingDelaunay.Build(pts);
        foreach (var t in d.Triangles.Where(t => t.Internal))
        {
            Vec3 a = d.Vertices[t.V1], b = d.Vertices[t.V2], c = d.Vertices[t.V3];
            // counter-clockwise, so c > 0 and (a, b) points down the time gradient
            Assert.True((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) > 0);
            foreach (var (p, _) in pts)
            {
                if (p == a || p == b || p == c) continue;
                double ax = a.X - p.X, ay = a.Y - p.Y, bx = b.X - p.X, by = b.Y - p.Y, cx = c.X - p.X, cy = c.Y - p.Y;
                var det = (ax * ax + ay * ay) * (bx * cy - cx * by) - (bx * bx + by * by) * (ax * cy - cx * ay) + (cx * cx + cy * cy) * (ax * by - bx * ay);
                Assert.True(det <= 1e-6, "a point lies inside a circumcircle");
            }
        }
    }

    [Fact]
    public void ArrowsPointTowardsEarlierHolesAndSlopeIsReliefPerMetre()
    {
        // Time grows 5 ms per metre to the east: arrows point west, burden relief 5 ms/m everywhere.
        var d = TimingDelaunay.Build(Grid((x, y) => 5 * x));
        var arrows = FirstMovement.Arrows(d);
        Assert.NotEmpty(arrows);
        foreach (var a in arrows) { Assert.Equal(-1, a.DirX, 3); Assert.Equal(0, a.DirY, 3); }
        foreach (var t in d.Triangles.Where(t => t.Internal && t.Valid)) Assert.Equal(5, t.Slope, 6);
    }

    [Fact]
    public void FlatTimeGivesNoArrowsAndSlopeRangeIsWidened()
    {
        var d = TimingDelaunay.Build(Grid((x, y) => 100));
        Assert.Empty(FirstMovement.Arrows(d));
        Assert.Equal(d.MinSlope + 1, d.MaxSlope, 9);
    }

    [Fact]
    public void ArrowShapesAndColourMatchReference()
    {
        var small = FirstMovement.SmallArrow(0, 0, 2, 0);
        Assert.Equal((2.0, 0.0), small[0]);
        Assert.Equal((-2.0, 1.4), small[1]);
        Assert.Equal((-2.0, -1.4), small[2]);
        Assert.Equal(7, FirstMovement.LargeArrow(0, 0, 2, 0).Length);
        Assert.Equal(((byte)255, (byte)0, (byte)0), FirstMovement.ColorFromHsv(0, 1, 1));
        Assert.Equal(((byte)0, (byte)255, (byte)255), FirstMovement.ColorFromHsv(180, 1, 1));
    }

    [Fact]
    public void InputsUseTheMiddleOfTheFirstFiringChargeAndSkipUntimedHoles()
    {
        var p = BlastProject.CreateNew();
        var layer = p.GetOrCreateLayer(StandardLayers.Holes);
        var timed = new Hole { Collar = new Vec3(0, 0, 100), Length = 10, ElectronicTimeMs = 25 };
        var untimed = new Hole { Collar = new Vec3(5, 0, 100), Length = 10 };
        p.Add(timed, layer);
        p.Add(untimed, layer);
        Timing.Solve(p);
        var inputs = FirstMovement.Inputs(p);
        var only = Assert.Single(inputs);
        Assert.Equal(timed.Collar, only.Point);   // no primed deck: collar
        Assert.Equal(p.Electronic.BaseTimeMs + 25, only.Time);
    }
}
