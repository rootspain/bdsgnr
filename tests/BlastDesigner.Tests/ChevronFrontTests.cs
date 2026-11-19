using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>
/// A chevron's two segments are independent; it is timed as a contour line of SHOTPlus: a hole's time is its signed distance
/// from the line of the segment the bisector picks, times that segment's delay.
/// </summary>
public class ChevronFrontTests
{
    private static readonly Vec3 Apex = new(100, 200, 50);

    /// <summary>A chevron with its ends at the given plan offsets from the vertex: [end 0, vertex, end 2].</summary>
    private static List<Vec3> Shape(Vec3 to0, Vec3 to2) => new() { Apex + to0, Apex, Apex + to2 };

    private static double[] Times(List<Vec3> shape, double delay0, double delay1, params Vec3[] holes) =>
        Times(shape, delay0, delay1, false, TimingLineStyle.TimeLine, holes);

    private static double[] Times(List<Vec3> shape, double delay0, double delay1, bool reverse, TimingLineStyle style, params Vec3[] holes)
    {
        var data = new[] { new TimingLineSegment(0, delay0, 0), new TimingLineSegment(0, delay1, 0) };
        var o = new TimingLineOptions { Front = TimingFrontShape.Chevron, Style = style, ReverseOrder = reverse };
        return EbsTimingLine.RawTimes(shape, data, o, holes);
    }

    [Fact]
    public void ASymmetricChevronTimesFromTheLineOfTheNearerRay_IsochronesParallelToTheRays()
    {
        // vertex at the origin, ray to the north, 10 ms/m on both segments
        var shape = Shape(new Vec3(-10, -10, 0), new Vec3(10, -10, 0));
        Assert.Equal(0, Chevron.RayDir(shape).X, 9);
        Assert.Equal(1, Chevron.RayDir(shape).Y, 9);
        var r2 = Math.Sqrt(2);
        var t = Times(shape, 10, 10, Apex + new Vec3(0, 5, 0), Apex + new Vec3(3, 5, 0), Apex + new Vec3(-3, 5, 0), Apex + new Vec3(7, 1, 0), Apex);
        // on the axis both rays give it, the later of the two: 10 · 5/√2
        var fromLeft = 10 * -Vec3.Dot(new Vec3(10, 15, 0), new Vec3(1, -1, 0) / r2);
        var fromRight = 10 * -Vec3.Dot(new Vec3(0, 5, 0), new Vec3(-1, -1, 0) / r2);
        Assert.Equal(Math.Max(fromLeft, fromRight), t[0], 9);
        Assert.Equal(10 * 5 / r2, t[0], 9);
        // (3, 5): its distance from the line of the nearer, right ray, 8/√2 m
        Assert.Equal(10 * 8 / r2, t[1], 9);
        // mirror holes fire together
        Assert.Equal(t[1], t[2], 9);
        // (7, 1) lies on the parallel to the right ray through (3, 5): the same time
        Assert.Equal(t[1], t[3], 9);
        // the vertex is on the line
        Assert.Equal(0, t[4], 9);
    }

    [Fact]
    public void TheHolesBehindTheVertexGetSmallerTimes_NotOutOfReach()
    {
        var shape = Shape(new Vec3(-10, -10, 0), new Vec3(10, -10, 0));
        var t = Times(shape, 10, 10, Apex + new Vec3(0, 5, 0), Apex + new Vec3(0, -5, 0));
        Assert.Equal(-10 * 5 / Math.Sqrt(2), t[1], 9);
        Assert.True(t[1] < t[0]);
        Assert.True(Math.Abs(t[1]) < 1e30);
    }

    [Fact]
    public void TheRaySideIsLaterWhicheverWayRoundThePointsLie_ReversedTheOtherSide()
    {
        var shape = Shape(new Vec3(-10, -10, 0), new Vec3(10, -6, 0));
        // an end dragged over the ray leaves the points the other way round: the same front, the same times
        var swapped = new List<Vec3> { shape[2], shape[1], shape[0] };
        var holes = new[] { Apex + new Vec3(0, 5, 0), Apex + new Vec3(4, 2, 0), Apex + new Vec3(-6, 1, 0), Apex + new Vec3(1, -7, 0) };
        var t = Times(shape, 8, 5, holes);
        var s = Times(swapped, 5, 8, holes);
        for (int i = 0; i < holes.Length; i++) Assert.Equal(t[i], s[i], 9);
        // ahead of the vertex later than behind it
        Assert.True(t[0] > t[3]);
        // B → A: the other side later
        var back = Times(shape, 8, 5, true, TimingLineStyle.TimeLine, holes);
        Assert.True(back[0] < back[3]);
        var backSwapped = Times(swapped, 5, 8, true, TimingLineStyle.TimeLine, holes);
        for (int i = 0; i < holes.Length; i++) Assert.Equal(back[i], backSwapped[i], 9);
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
        // along the other segment it would make no chevron: it stays
        var along = Chevron.MoveEnd(shape, 0, Apex + new Vec3(10, -10, 0));
        Assert.Equal(shape[0], along[0]);
        // onto the continuation of the other one it is a straight line: see AnEndDrawnOutTo180DegreesSnapsIntoAStraightLine
        Assert.True(Chevron.IsStraight(Chevron.MoveEnd(shape, 0, Apex + new Vec3(-10, 10, 0))));
    }

    [Fact]
    public void AnEndDrawnOutTo180DegreesSnapsIntoAStraightLine_ItsVertexInTheMiddle()
    {
        var shape = Shape(new Vec3(-5, -5, 0), new Vec3(5, -5, 0));
        Assert.False(Chevron.IsStraight(shape));
        // 3° off the continuation of the other segment: on it, as far as the cursor reaches along it
        var a = 3 * GeoMath.Deg;
        var back = new Vec3(-1, 1, 0).Normalized();
        var off = new Vec3(back.X * Math.Cos(a) - back.Y * Math.Sin(a), back.X * Math.Sin(a) + back.Y * Math.Cos(a), 0);
        var line = Chevron.MoveEnd(shape, 0, Apex + off * 10);
        Assert.True(Chevron.IsStraight(line));
        Assert.Equal(shape[2], line[2]);
        var reach = 10 * Math.Cos(a);
        Assert.Equal(Apex.X - reach / Math.Sqrt(2), line[0].X, 9);
        Assert.Equal(Apex.Y + reach / Math.Sqrt(2), line[0].Y, 9);
        // the vertex goes to the middle of the line, at its elevation
        Assert.Equal((line[0].X + line[2].X) / 2, line[1].X, 9);
        Assert.Equal((line[0].Y + line[2].Y) / 2, line[1].Y, 9);
        Assert.Equal(Apex.Z, line[1].Z);
        Assert.Equal(Chevron.Length(line), Vec3.Distance2D(line[0], line[1]), 9);
        Assert.Equal(180, Chevron.AngleDeg(line), 6);
        // the ray of a straight one is square to it, end 0 on its left
        var ray = Chevron.RayDir(line);
        Assert.Equal(0, Vec3.Dot(ray, line[2] - line[0]), 9);
        Assert.True(ray.X * (line[0] - line[1]).Y - ray.Y * (line[0] - line[1]).X > 0);

        // a snap, not a stop: further on it bends to the other side, the vertex staying where it is
        var a2 = -10 * GeoMath.Deg;
        var past = new Vec3(back.X * Math.Cos(a2) - back.Y * Math.Sin(a2), back.X * Math.Sin(a2) + back.Y * Math.Cos(a2), 0);
        var bent = Chevron.MoveEnd(shape, 0, Apex + past * 10);
        Assert.False(Chevron.IsStraight(bent));
        Assert.Equal(Apex, bent[1]);
        Assert.Equal(170, Chevron.AngleDeg(bent), 6);
        // and from the straight line an end bends it again
        var again = Chevron.MoveEnd(line, 2, line[1] + new Vec3(0, -6, 0));
        Assert.False(Chevron.IsStraight(again));
        Assert.Equal(line[1], again[1]);
    }

    [Fact]
    public void AnEndOfAStraightChevronMovesOnItsOwn_TheLineStaysStraight_ItsVertexInTheMiddle()
    {
        var line = Shape(new Vec3(-10, 0, 0), new Vec3(10, 0, 0));
        var moved = Chevron.MoveStraightEnd(line, 2, Apex + new Vec3(4, 12, 7));
        Assert.True(Chevron.IsStraight(moved));
        Assert.Equal(line[0], moved[0]);
        // the end goes where it is taken, at the elevation of the vertex; the vertex to the middle
        Assert.Equal(new Vec3(Apex.X + 4, Apex.Y + 12, Apex.Z), moved[2]);
        Assert.Equal(new Vec3(Apex.X - 3, Apex.Y + 6, Apex.Z), moved[1]);
        // onto the other end: it stays as it was
        Assert.Equal(line, Chevron.MoveStraightEnd(line, 0, line[2] + new Vec3(0.01, 0, 0)));
    }

    [Fact]
    public void AStraightChevronTimesBySignedDistanceFromTheLine_OneParallelTogether()
    {
        var line = Shape(new Vec3(-10, 0, 0), new Vec3(10, 0, 0));
        Assert.True(Chevron.IsStraight(line));
        var holes = new[] { new Vec3(0, 3, 0), new Vec3(7, 3, 0), new Vec3(25, 3, 0), new Vec3(-7, -3, 0), new Vec3(0, -6, 0), new Vec3(4, 0, 0) }
            .Select(v => Apex + v).ToArray();
        // its ray (north, end 0 on the left) is the later side; behind the line the times go below nought, not mirrored
        var t = Times(line, 5, 5, holes);
        Assert.Equal(new[] { 15.0, 15, 15, -15, -30, 0 }, t.Select(x => Math.Round(x, 9)));

        // reversed: the other side later
        var back = Times(line, 5, 5, true, TimingLineStyle.TimeLine, holes);
        Assert.Equal(new[] { -15.0, -15, -15, 15, 30, 0 }, back.Select(x => Math.Round(x, 9)));
        // the bisector of two segments on one line is nought: no NaN
        Assert.All(t.Concat(back), x => Assert.False(double.IsNaN(x)));
    }

    [Fact]
    public void ConvertLineToChevron_TheVertexGoesForwardTo170Degrees_TheEndsStay_TheTypeIsTwoFronts()
    {
        var line = new TimingLineEntity
        {
            Front = TimingFrontShape.Chevron, Style = TimingLineStyle.TimeLine,
            Points = Shape(new Vec3(-10, 0, 0), new Vec3(10, 0, 0)), Segments = { new(0, 5, 0), new(0, 5, 0) }
        };
        Assert.True(line.CanConvertToChevron());
        // forward: end 0 on the left, the way the ray of a straight one points (here south)
        var ray = Chevron.RayDir(line.Points);
        line.ConvertToChevron();
        Assert.Equal(TimingLineStyle.BurnLine, line.Style);
        Assert.False(Chevron.IsStraight(line.Points));
        Assert.False(line.CanConvertToChevron());
        Assert.Equal(Chevron.BentAngleDeg, Chevron.AngleDeg(line.Points), 6);
        Assert.Equal(Apex + new Vec3(-10, 0, 0), line.Points[0]);
        Assert.Equal(Apex + new Vec3(10, 0, 0), line.Points[2]);
        var ahead = line.Points[1] - Apex;
        Assert.Equal(10 * Math.Tan(5 * GeoMath.Deg), Vec3.Dot(ahead, ray), 9);
        Assert.Equal(0, Vec3.Dot(ahead, new Vec3(1, 0, 0)), 9);
        // past the snap: an end touched does not snap it back at once
        Assert.False(Chevron.IsStraight(Chevron.MoveEnd(line.Points, 0, line.Points[0])));
        // a bent chevron is not converted
        var bent = new TimingLineEntity { Front = TimingFrontShape.Chevron, Points = Chevron.Build(Apex, new Vec3(0, 1, 0), 30, 10) };
        Assert.False(bent.CanConvertToChevron());
    }

    [Fact]
    public void TheFrontAnglesOfAChevronAreTheBearingsOfItsSegments_TypedInTheyTurnThem()
    {
        // segments to the south-west and the south-east of the vertex
        var line = new TimingLineEntity
        {
            Front = TimingFrontShape.Chevron, Points = Shape(new Vec3(-5, -5, 0), new Vec3(5, -5, 0)), Segments = { new(0, 8, 0), new(0, 6, 0) }
        };
        Assert.Equal(225, line.LeftFrontAzimuthDeg, 6);
        Assert.Equal(135, line.RightFrontAzimuthDeg, 6);
        // the left one turned to the west: its length kept, the vertex and the other end where they were
        line.LeftFrontAzimuthDeg = 270;
        Assert.Equal(270, line.LeftFrontAzimuthDeg, 6);
        Assert.Equal(Apex.X - Math.Sqrt(50), line.Points[0].X, 9);
        Assert.Equal(Apex.Y, line.Points[0].Y, 9);
        Assert.Equal(Apex, line.Points[1]);
        Assert.Equal(Apex + new Vec3(5, -5, 0), line.Points[2]);
        Assert.Equal(new[] { 8.0, 6 }, line.Segments.Select(x => x.ReliefMsPerM));
        // bent: the type is two fronts
        Assert.Equal(TimingLineStyle.BurnLine, line.Style);
        // onto the continuation of the other one: a straight line, one delay, the front line type
        line.RightFrontAzimuthDeg = 90;
        Assert.True(Chevron.IsStraight(line.Points));
        Assert.Equal(new[] { 8.0, 8 }, line.Segments.Select(x => x.ReliefMsPerM));
        Assert.Equal(TimingLineStyle.TimeLine, line.Style);
        // a line front has none
        var front = new TimingLineEntity { Front = TimingFrontShape.Line, Points = { Apex, Apex + new Vec3(10, 0, 0) } };
        Assert.Equal(0, front.LeftFrontAzimuthDeg);
        front.LeftFrontAzimuthDeg = 45;
        Assert.Equal(Apex + new Vec3(10, 0, 0), front.Points[1]);
    }

    [Fact]
    public void AChevronOfTheTwoFrontsTypeIsTimedAsAContourLineAsOneOfTheFrontLineType_TheAdvancedBurnIsNot()
    {
        var shape = Shape(new Vec3(-6, -8, 0), new Vec3(7, -2, 0));
        var holes = new[] { Apex + new Vec3(0, 10, 0), Apex + new Vec3(-4, -3, 0), Apex + new Vec3(9, 1, 0) };
        var data = new[] { new TimingLineSegment(0, 8, 0), new TimingLineSegment(0, 5, 0) };
        double[] Of(TimingLineStyle style) => EbsTimingLine.RawTimes(shape, data, new TimingLineOptions { Front = TimingFrontShape.Chevron, Style = style }, holes);
        Assert.Equal(Of(TimingLineStyle.TimeLine), Of(TimingLineStyle.BurnLine));
        Assert.NotEqual(Of(TimingLineStyle.TimeLine), Of(TimingLineStyle.AdvancedBurn));
        // a line that is no chevron, of the front line type: a contour line as the chevron
        double[] Picked(TimingLineStyle style) => EbsTimingLine.RawTimes(shape, data, new TimingLineOptions { Front = TimingFrontShape.None, Style = style }, holes);
        Assert.Equal(Of(TimingLineStyle.TimeLine), Picked(TimingLineStyle.TimeLine));
        // the type follows the shape
        var line = new TimingLineEntity { Front = TimingFrontShape.Chevron, Points = shape.ToList(), Style = TimingLineStyle.TimeLine };
        line.TypeByShape();
        Assert.Equal(TimingLineStyle.BurnLine, line.Style);
        line.Points = Shape(new Vec3(-10, 0, 0), new Vec3(10, 0, 0));
        line.TypeByShape();
        Assert.Equal(TimingLineStyle.TimeLine, line.Style);
        line.Style = TimingLineStyle.AdvancedBurn;
        line.TypeByShape();
        Assert.Equal(TimingLineStyle.AdvancedBurn, line.Style);
    }

    [Fact]
    public void OneShapeOneType_AFrontTakesTheShapeOfTheTypeChosen_APickedLineAnyType()
    {
        var both = new[] { TimingLineStyle.TimeLine, TimingLineStyle.BurnLine, TimingLineStyle.Point };
        var front = new TimingLineEntity { Front = TimingFrontShape.Line, Points = { Apex + new Vec3(-10, 0, 0), Apex + new Vec3(10, 0, 0) }, Segments = { new(0, 6, 0) } };
        Assert.Equal(both, front.AllowedStyles());
        // a line front is a straight front: it can be converted to a chevron (its label, its context menu)
        Assert.True(front.CanConvertToChevron());
        // two fronts: the line front is a chevron bent to 170°, its ends where they were, the delay on both segments
        front.Style = TimingLineStyle.BurnLine;
        front.ShapeByType();
        Assert.Equal(TimingFrontShape.Chevron, front.Front);
        Assert.Equal(3, front.Points.Count);
        Assert.Equal(Chevron.BentAngleDeg, Chevron.AngleDeg(front.Points), 6);
        Assert.Equal(Apex + new Vec3(-10, 0, 0), front.Points[0]);
        Assert.Equal(Apex + new Vec3(10, 0, 0), front.Points[2]);
        Assert.Equal(new[] { 6.0, 6 }, front.Segments.Select(x => x.ReliefMsPerM));
        // the vertex goes the way the times of the line grew: to the left of A → B (north here)
        Assert.True(front.Points[1].Y > Apex.Y);
        Assert.Equal(both, front.AllowedStyles());
        // front line: straight again, the vertex in the middle
        front.Segments[1] = front.Segments[1] with { ReliefMsPerM = 9 };
        front.Style = TimingLineStyle.TimeLine;
        front.ShapeByType();
        Assert.True(Chevron.IsStraight(front.Points));
        Assert.Equal(Apex, front.Points[1]);
        Assert.Equal(new[] { 6.0, 6 }, front.Segments.Select(x => x.ReliefMsPerM));
        // the type it has, the shape it has: nothing changes
        var bent = new TimingLineEntity { Front = TimingFrontShape.Chevron, Style = TimingLineStyle.BurnLine, Points = Chevron.Build(Apex, new Vec3(0, 1, 0), 30, 10) };
        var was = bent.Points.ToList();
        bent.ShapeByType();
        Assert.Equal(was, bent.Points);
        var picked = new TimingLineEntity { Points = { Apex, Apex + new Vec3(10, 0, 0), Apex + new Vec3(10, 10, 0) } };
        Assert.Equal(new[] { TimingLineStyle.TimeLine, TimingLineStyle.BurnLine, TimingLineStyle.AdvancedBurn }, picked.AllowedStyles());
    }

    [Fact]
    public void ALineFrontOfAnOlderFileOpensAsTheStraightChevron()
    {
        var p = BlastProject.CreateNew("old line");
        var old = new TimingLineEntity
        {
            Front = TimingFrontShape.Line, Style = TimingLineStyle.TimeLine,
            Points = { new Vec3(0, 0, 300), new Vec3(20, 10, 301) }, Segments = { new(0, 7, 0) }
        };
        p.Add(old);
        var path = Path.Combine(Path.GetTempPath(), $"oldline-{Guid.NewGuid():N}.bdproj");
        try
        {
            BlastDesigner.IO.ProjectFile.Save(p, path, 0);
            var line = BlastDesigner.IO.ProjectFile.Load(path).Entities.OfType<TimingLineEntity>().Single();
            Assert.Equal(TimingFrontShape.Chevron, line.Front);
            Assert.Equal(3, line.Points.Count);
            Assert.True(Chevron.IsStraight(line.Points));
            Assert.Equal(new Vec3(10, 5, 300), line.Points[1]);
            Assert.Equal(new Vec3(20, 10, 300), line.Points[2]);
            Assert.Equal(new[] { 7.0, 7 }, line.Segments.Select(x => x.ReliefMsPerM));
            Assert.Equal(TimingLineStyle.TimeLine, line.Style);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheDelayOfASegmentTypedInMovesItsEnd_AStraightOneHasOneDelayForBothHalves()
    {
        var bent = new TimingLineEntity
        {
            Front = TimingFrontShape.Chevron, Points = Shape(new Vec3(-6, -8, 0), new Vec3(3, -4, 0)), Segments = { new(0, 8, 0), new(0, 5, 0) }
        };
        Assert.Equal(8, bent.FirstSegmentReliefMsPerM);
        Assert.Equal(5, bent.SecondSegmentReliefMsPerM);
        // twice the delay, twice as long, the direction and the other segment kept
        bent.FirstSegmentReliefMsPerM = 16;
        Assert.Equal(Apex + new Vec3(-12, -16, 0), bent.Points[0]);
        Assert.Equal(Apex + new Vec3(3, -4, 0), bent.Points[2]);
        Assert.Equal(new[] { 16.0, 5 }, bent.Segments.Select(x => x.ReliefMsPerM));
        bent.SecondSegmentReliefMsPerM = 2.5;
        Assert.Equal(Apex + new Vec3(1.5, -2, 0), bent.Points[2]);

        var straight = new TimingLineEntity
        {
            Front = TimingFrontShape.Chevron, Points = Shape(new Vec3(-10, 0, 0), new Vec3(10, 0, 0)), Segments = { new(0, 8, 0), new(0, 8, 0) }
        };
        Assert.Equal(8, straight.LineReliefMsPerM);
        straight.LineReliefMsPerM = 4;
        Assert.True(Chevron.IsStraight(straight.Points));
        Assert.Equal(Apex + new Vec3(-5, 0, 0), straight.Points[0]);
        Assert.Equal(Apex, straight.Points[1]);
        Assert.Equal(Apex + new Vec3(5, 0, 0), straight.Points[2]);
        Assert.Equal(new[] { 4.0, 4 }, straight.Segments.Select(x => x.ReliefMsPerM));
        // a line that is no chevron has none
        var picked = new TimingLineEntity { Points = { Apex, Apex + new Vec3(10, 0, 0) }, Segments = { new(0, 8, 0) } };
        Assert.Equal(0, picked.FirstSegmentReliefMsPerM);
        picked.FirstSegmentReliefMsPerM = 3;
        Assert.Equal(8, picked.Segments[0].ReliefMsPerM);
    }
}
