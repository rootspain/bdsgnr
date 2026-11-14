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
    public void AStraightChevronTimesFromTheLineBothWays_TheFirstSegmentsDelay_OneParallelTogether()
    {
        var line = Shape(new Vec3(-10, 0, 0), new Vec3(10, 0, 0));
        Assert.True(Chevron.IsStraight(line));
        var holes = new[] { new Vec3(0, 3, 0), new Vec3(7, 3, 0), new Vec3(25, 3, 0), new Vec3(-7, -3, 0), new Vec3(0, -6, 0), new Vec3(4, 0, 0) }
            .Select(v => Apex + v).ToArray();
        // the delay of the first segment for the whole line, 5 ms/m; the second's, 9, is not used
        var t = Times(line, 5, 9, holes);
        Assert.Equal(new[] { 15.0, 15, 15, 15, 30, 0 }, t.Select(x => Math.Round(x, 9)));

        // reversed: the farthest from the line first, the ones on it last
        var data = new[] { new TimingLineSegment(0, 5, 0), new TimingLineSegment(0, 9, 0) };
        var back = EbsTimingLine.RawTimes(line, data, new TimingLineOptions { Front = TimingFrontShape.Chevron, Style = TimingLineStyle.TimeLine, ReverseOrder = true }, holes);
        Assert.Equal(new[] { 15.0, 15, 15, 15, 0, 30 }, back.Select(x => Math.Round(x, 9)));
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
    public void AChevronOfTheTwoFrontsTypeIsTimedByItsOvalAsOneOfTheFrontLineType_TheAdvancedBurnIsNot()
    {
        var shape = Shape(new Vec3(-6, -8, 0), new Vec3(7, -2, 0));
        var holes = new[] { Apex + new Vec3(0, 10, 0), Apex + new Vec3(-4, -3, 0), Apex + new Vec3(9, 1, 0) };
        var data = new[] { new TimingLineSegment(0, 8, 0), new TimingLineSegment(0, 5, 0) };
        double[] Of(TimingLineStyle style) => EbsTimingLine.RawTimes(shape, data, new TimingLineOptions { Front = TimingFrontShape.Chevron, Style = style }, holes);
        Assert.Equal(Of(TimingLineStyle.TimeLine), Of(TimingLineStyle.BurnLine));
        Assert.True(EbsTimingLine.TimedAsChevron(new TimingLineOptions { Front = TimingFrontShape.Chevron, Style = TimingLineStyle.BurnLine }, 3));
        Assert.False(EbsTimingLine.TimedAsChevron(new TimingLineOptions { Front = TimingFrontShape.Chevron, Style = TimingLineStyle.AdvancedBurn }, 3));
        // a burn line that is no chevron burns as before
        Assert.False(EbsTimingLine.TimedAsChevron(new TimingLineOptions { Front = TimingFrontShape.None, Style = TimingLineStyle.BurnLine }, 3));
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
        var both = new[] { TimingLineStyle.TimeLine, TimingLineStyle.BurnLine };
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
        Assert.Equal(Enum.GetValues<TimingLineStyle>(), picked.AllowedStyles());
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
