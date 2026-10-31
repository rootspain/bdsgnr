using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>Electronic timing line checked against hand-computed times.</summary>
public class EbsTimingLineTests
{
    static readonly BlastProject Project = BlastProject.CreateNew();
    static Hole H(double x, double y) => new() { Collar = new Vec3(x, y, 100), Label = $"{x}/{y}" };
    static List<Vec3> Line(params (double X, double Y)[] p) => p.Select(q => new Vec3(q.X, q.Y, 100)).ToList();

    [Fact]
    public void TimeLine_TimeIsReliefTimesDistanceShiftedToOffset()
    {
        var holes = new List<Hole> { H(3, 2), H(7, 5) };
        // Line along +X: the right-hand normal points to −Y, holes on +Y are later by relief × distance.
        var r = EbsTimingLine.Apply(Project, holes, Line((0, 0), (10, 0)), new[] { new TimingLineSegment(0, 8, 0) },
            new TimingLineOptions { Style = TimingLineStyle.TimeLine, StartOffsetMs = 100 }, 20000, 0);
        Assert.Equal(2, r.Timed);
        Assert.Equal(100, holes[0].ElectronicTimeMs!.Value, 6);
        Assert.Equal(124, holes[1].ElectronicTimeMs!.Value, 6);
        Assert.False(holes[1].ElectronicTimeManual);
    }

    [Fact]
    public void TimeLine_ProgressiveAddsProgressScaledByTenMetres()
    {
        var holes = new List<Hole> { H(0, 0), H(0, 10), H(0, 20) };
        EbsTimingLine.Apply(Project, holes, Line((-5, 0), (5, 0)), new[] { new TimingLineSegment(0, 0, 7) },
            new TimingLineOptions { Style = TimingLineStyle.TimeLine, UseProgressive = true, ProgressRate = 4 }, 20000, 0);
        Assert.Equal(0, holes[0].ElectronicTimeMs!.Value, 6);
        Assert.Equal(7, holes[1].ElectronicTimeMs!.Value, 6);   // 7 · (10/10)²
        Assert.Equal(28, holes[2].ElectronicTimeMs!.Value, 6);  // 7 · (20/10)²
    }

    [Fact]
    public void BurnLine_BurnsAlongSegmentsAndPicksEarliest()
    {
        var holes = new List<Hole> { H(0, 0), H(4, 0), H(10, 4) };
        var data = new[] { new TimingLineSegment(2, 5, 0), new TimingLineSegment(3, 5, 0) };
        EbsTimingLine.Apply(Project, holes, Line((0, 0), (10, 0), (10, 10)), data, new TimingLineOptions { Style = TimingLineStyle.BurnLine }, 20000, 0);
        Assert.Equal(0, holes[0].ElectronicTimeMs!.Value, 6);
        Assert.Equal(8, holes[1].ElectronicTimeMs!.Value, 6);   // 4 m × 2 ms/m
        Assert.Equal(32, holes[2].ElectronicTimeMs!.Value, 6);  // 10 × 2 + 4 × 3 (segment 1) beats 10 × 2 + 4 × 5 (segment 0)
    }

    [Fact]
    public void ManualTimesAreKeptButStillSetTheZeroPoint()
    {
        var manual = H(3, 2);
        Timing.SetTime(manual, 999, true);
        var other = H(7, 5);
        var r = EbsTimingLine.Apply(Project, new List<Hole> { manual, other }, Line((0, 0), (10, 0)), new[] { new TimingLineSegment(0, 8, 0) },
            new TimingLineOptions(), 20000, 0);
        Assert.Equal(1, r.SkippedManual);
        Assert.Equal(999, manual.ElectronicTimeMs);
        Assert.True(manual.ElectronicTimeManual);
        Assert.Equal(24, other.ElectronicTimeMs!.Value, 6);
    }

    [Fact]
    public void AdvancedBurn_UsesReliefAlongAndTanAngleSideways()
    {
        var holes = new List<Hole> { H(0, 0), H(6, 3), H(6, -3) };
        var o = new TimingLineOptions { Style = TimingLineStyle.AdvancedBurn, BurdenReliefMsPerM = 4, Angle1Deg = 45, Angle2Deg = 0 };
        var data = new[] { new TimingLineSegment(0, 0, 0) };
        var r = EbsTimingLine.Apply(Project, holes, Line((0, 0), (10, 0)), data, o, 30, 0);
        Assert.Equal(0, holes[0].ElectronicTimeMs!.Value, 6);
        // Left of the line (angle 1): 3 × 4·tan45 + 6 × 4 = 36 > max 30 → reported, not timed.
        Assert.Null(holes[1].ElectronicTimeMs);
        Assert.Single(r.MaxTimeExceeded);
        // Right of the line (angle 2 = 0°): only the distance along counts.
        Assert.Equal(24, holes[2].ElectronicTimeMs!.Value, 6);
    }

    [Fact]
    public void ReverseOrderStartsTheBurnAtTheLastPoint()
    {
        var holes = new List<Hole> { H(0, 0), H(10, 0) };
        EbsTimingLine.Apply(Project, holes, Line((0, 0), (10, 0)), new[] { new TimingLineSegment(2, 5, 0) },
            new TimingLineOptions { Style = TimingLineStyle.BurnLine, ReverseOrder = true }, 20000, 0);
        Assert.Equal(20, holes[0].ElectronicTimeMs!.Value, 6);
        Assert.Equal(0, holes[1].ElectronicTimeMs!.Value, 6);
    }

    [Fact]
    public void EntityKeepsPointsAndOptionsThroughProjectFile()
    {
        var line = EbsTimingLine.CreateEntity(Line((0, 0), (10, 0), (10, 10)), new[] { new TimingLineSegment(2, 5, 1), new TimingLineSegment(3, 6, 0) },
            new TimingLineOptions { Style = TimingLineStyle.BurnLine, StartOffsetMs = 50, ReverseOrder = true });
        var copy = (TimingLineEntity)ProjectJson.DeserializeEntities(ProjectJson.SerializeEntities(new Entity[] { line }))[0];
        Assert.Equal(line.Points, copy.Points);
        Assert.Equal(100, copy.Points[0].Z);
        Assert.Equal(line.Segments, copy.Segments);
        Assert.Equal(TimingLineStyle.BurnLine, copy.Style);
        Assert.Equal(50, copy.StartOffsetMs);
        Assert.True(copy.ReverseOrder);
        Assert.Equal(new Vec3(10, 10, 100), copy.OrderedPoints[0]);
    }

    [Fact]
    public void AngleArmsPointBackwardsAtTheirAngles()
    {
        var line = new TimingLineEntity { Points = Line((0, 0), (10, 0)), Angle1Deg = 0, Angle2Deg = 45 };
        // Arm 1 (left): line direction turned 90° anticlockwise; arm 2 (right): turned 135° clockwise.
        var e1 = line.AngleEnd(1);
        var e2 = line.AngleEnd(2);
        Assert.Equal(0, e1.X, 6); Assert.Equal(20, e1.Y, 6); Assert.Equal(100, e1.Z, 6);
        Assert.Equal(-20 / Math.Sqrt(2), e2.X, 6); Assert.Equal(-20 / Math.Sqrt(2), e2.Y, 6);
    }
}
