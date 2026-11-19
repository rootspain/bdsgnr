using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>A point front of the front tool: the delays go round its point, the ends of a line put together make it.</summary>
public class PointFrontTests
{
    private static readonly Vec3 At = new(100, 200, 50);

    private static string Explosive(BlastProject p) => p.Products.First(x => x.IsExplosive).Id;
    private static string Electronic(BlastProject p) => p.Products.First(x => x.Kind == ProductKind.ElectronicDetonator).Id;

    /// <summary>A vertical hole with one electronic primer at the given plan offset from the point.</summary>
    private static Hole H(BlastProject p, double dx, double dy) => new()
    {
        Collar = new Vec3(At.X + dx, At.Y + dy, 100), Label = $"{dx}/{dy}", Length = 10,
        Decks = { new Deck { Top = 3, Bottom = 10, ProductId = Explosive(p), Primers = { new Primer { Depth = 9.5, DetonatorId = Electronic(p) } } } }
    };

    private static TimingLineOptions Options(bool reverse = false, bool progressive = false) =>
        new() { Front = TimingFrontShape.Point, Style = TimingLineStyle.Point, ReverseOrder = reverse, UseProgressive = progressive };

    private static double[] Times(double relief, TimingLineOptions o, params Vec3[] holes) =>
        EbsTimingLine.RawTimes(new[] { At }, new[] { new TimingLineSegment(0, relief, 20) }, o, holes);

    [Fact]
    public void ThePointTimesInCircles_OneRadiusOneTime_TwiceAsFarTwiceAsLate()
    {
        var holes = new[] { new Vec3(5, 0, 0), new Vec3(0, -5, 0), new Vec3(3, 4, -20), new Vec3(-10, 0, 0), new Vec3(6, 8, 0), new Vec3(0, 0, 0) }
            .Select(v => At + v).ToArray();
        var t = Times(8, Options(), holes);
        // on one circle of 5 m: one time, 8 ms/m × 5 m; elevation does not count
        Assert.Equal(40, t[0], 9);
        Assert.Equal(40, t[1], 9);
        Assert.Equal(40, t[2], 9);
        // twice as far, twice as late
        Assert.Equal(80, t[3], 9);
        Assert.Equal(80, t[4], 9);
        Assert.Equal(0, t[5], 9);
    }

    [Fact]
    public void TheProgressIsAddedAsAContourLineAddsIt()
    {
        // P · (d / 10)^e: 20 ms at 10 m, exponent 1.2 (rate 1)
        var t = Times(8, Options(progressive: true), At + new Vec3(10, 0, 0), At + new Vec3(0, 20, 0), At);
        Assert.Equal(80 + 20, t[0], 9);
        Assert.Equal(160 + 20 * Math.Pow(2, 1.2), t[1], 9);
        Assert.Equal(0, t[2], 9);
        Assert.All(t, x => Assert.False(double.IsNaN(x)));
    }

    [Fact]
    public void ToThePoint_TheFarthestHoleFirst_TheTimesComeBackToIt()
    {
        var holes = new[] { At + new Vec3(2, 0, 0), At + new Vec3(0, 6, 0), At + new Vec3(-10, 0, 0) };
        var from = Times(5, Options(), holes);
        var to = Times(5, Options(reverse: true), holes);
        Assert.Equal(new[] { 10.0, 30, 50 }, from.Select(x => Math.Round(x, 9)));
        Assert.Equal(new[] { 40.0, 20, 0 }, to.Select(x => Math.Round(x, 9)));
    }

    [Fact]
    public void ProgrammedFromThePoint_TheNearestAtTheStart_AllInReach()
    {
        var p = BlastProject.CreateNew("point");
        var near = H(p, 3, 4);
        var far = H(p, -6, 8);
        var behind = H(p, 0, -10);
        var holes = new List<Hole> { near, far, behind };
        foreach (var h in holes) p.Add(h);
        var line = EbsTimingLine.CreateEntity(new[] { At }, new[] { new TimingLineSegment(0, 8, 0) }, Options());
        line.StartOffsetMs = 100;
        var r = EbsTimingLine.Apply(p, holes, line);
        Assert.Equal(3, r.Timed);
        Assert.Empty(r.Unreached);
        Assert.Equal(100, near.ElectronicTimeMs!.Value, 6);
        Assert.Equal(140, far.ElectronicTimeMs!.Value, 6);
        Assert.Equal(140, behind.ElectronicTimeMs!.Value, 6);
        Assert.True(EbsTimingLine.Timable(line));
    }

    [Fact]
    public void APointFrontFollowsItsHoles()
    {
        var p = BlastProject.CreateNew("point");
        var a = H(p, 5, 0);
        var b = H(p, 10, 0);
        p.Add(a); p.Add(b);
        var doc = new Document(p);
        var line = EbsTimingLine.CreateEntity(new[] { At }, new[] { new TimingLineSegment(0, 10, 0) }, Options());
        line.HoleIds = new List<Guid> { a.Id, b.Id };
        doc.Execute("point", pr => { pr.Add(line); EbsTimingLine.Apply(pr, new List<Hole> { a, b }, line); });
        Assert.Equal(50, b.ElectronicTimeMs!.Value - a.ElectronicTimeMs!.Value, 6);
        // the far hole moved twice as far: its time follows, in the same step
        doc.Execute("move", _ => b.Collar = new Vec3(At.X + 15, At.Y, 100));
        Assert.Equal(100, b.ElectronicTimeMs!.Value - a.ElectronicTimeMs!.Value, 6);
    }

    [Fact]
    public void ThePointFrontIsSavedAndOpenedAgain()
    {
        var p = BlastProject.CreateNew("point");
        var line = EbsTimingLine.CreateEntity(new[] { At }, new[] { new TimingLineSegment(0, 7, 12) }, Options(reverse: true));
        line.Front = TimingFrontShape.Point;
        p.Add(line);
        var path = Path.Combine(Path.GetTempPath(), $"point-{Guid.NewGuid():N}.bdproj");
        try
        {
            BlastDesigner.IO.ProjectFile.Save(p, path, 0);
            var opened = BlastDesigner.IO.ProjectFile.Load(path).Entities.OfType<TimingLineEntity>().Single();
            Assert.Equal(TimingFrontShape.Point, opened.Front);
            Assert.True(opened.IsPoint());
            Assert.True(opened.HasHandles);
            Assert.Equal(At, opened.Points.Single());
            Assert.Equal(new TimingLineSegment(0, 7, 12), opened.Segments.Single());
            Assert.True(opened.ReverseOrder);
            Assert.Equal(TimingLineStyle.Point, opened.Style);
            Assert.Equal(new[] { TimingLineStyle.TimeLine, TimingLineStyle.BurnLine, TimingLineStyle.Point }, opened.AllowedStyles());
        }
        finally { File.Delete(path); }
        // a copy (undo, clipboard) keeps it
        var copy = (TimingLineEntity)ProjectJson.DeserializeEntities(ProjectJson.SerializeEntities(new Entity[] { line }))[0];
        Assert.True(copy.IsPoint());
    }

    [Fact]
    public void TheEndsOfALinePutTogetherMakeAPoint_AtTheEndThatStayed()
    {
        // the line of the tool: a straight chevron, its vertex in the middle
        var line = new TimingLineEntity
        {
            Front = TimingFrontShape.Chevron, Style = TimingLineStyle.TimeLine,
            Points = { At + new Vec3(-10, 0, 0), At, At + new Vec3(10, 0, 3) }, Segments = { new(0, 6, 4), new(0, 6, 4) }
        };
        var merged = Chevron.Merge(line.Points, 0);
        Assert.Equal(new Vec3(At.X + 10, At.Y, At.Z), merged.Single());
        line.Points = merged;
        line.PointByPoints();
        Assert.Equal(TimingFrontShape.Point, line.Front);
        Assert.True(line.IsPoint());
        Assert.Equal(TimingLineStyle.Point, line.Style);
        Assert.Equal(new[] { new TimingLineSegment(0, 6, 4) }, line.Segments);
        Assert.Equal(6, line.PointReliefMsPerM);
        line.PointReliefMsPerM = 9;
        Assert.Equal(9, line.Segments[0].ReliefMsPerM);

        // a line of two points (older files) too, the other end
        var two = new List<Vec3> { At, At + new Vec3(5, 5, 0) };
        Assert.Equal(At, Chevron.Merge(two, 1).Single());
        // a line picked point by point is no front: it stays as it is
        var picked = new TimingLineEntity { Points = { At }, Segments = { new(0, 6, 0) } };
        picked.PointByPoints();
        Assert.Equal(TimingFrontShape.None, picked.Front);
        Assert.Equal(0, picked.PointReliefMsPerM);
    }

    [Fact]
    public void AChevronEndPutOntoItsVertexLeavesTheStraightLineOfTheOtherSegment_ItsLaterSideKept()
    {
        var apex = At;
        var shape = Chevron.Build(apex, new Vec3(0, 1, 0), 90, 10);
        foreach (var end in new[] { 0, 2 })
        {
            var line = Chevron.DropSegment(shape, end);
            Assert.True(Chevron.IsStraight(line));
            var far = shape[2 - end];
            // the vertex and the end that stayed are the ends of the line, its vertex in the middle
            Assert.Equal(end == 0 ? apex : far, line[0]);
            Assert.Equal(end == 0 ? far : apex, line[2]);
            Assert.Equal(0, Vec3.Distance2D(line[1], (line[0] + line[2]) / 2), 9);
            // walked as the chevron was: the side of the ray stays the later one of that segment
            var o = new TimingLineOptions { Front = TimingFrontShape.Chevron, Style = TimingLineStyle.TimeLine };
            var data = new[] { new TimingLineSegment(0, 8, 0), new TimingLineSegment(0, 8, 0) };
            var ahead = apex + new Vec3(0, 5, 0);
            var behind = apex + new Vec3(0, -5, 0);
            var t = EbsTimingLine.RawTimes(line, data, o, new[] { ahead, behind });
            Assert.True(t[0] > t[1]);
        }
    }

    /// <summary>A front of the given shape: a point, the straight line of the tool or a chevron, about <see cref="At"/>.</summary>
    private static TimingLineEntity Front(TimingLineStyle shape)
    {
        var o = new TimingLineOptions { Front = shape == TimingLineStyle.Point ? TimingFrontShape.Point : TimingFrontShape.Chevron, Style = shape, StartOffsetMs = 30, ReverseOrder = true, UseProgressive = true, ProgressRate = 2 };
        var pts = shape switch
        {
            TimingLineStyle.Point => new List<Vec3> { At },
            TimingLineStyle.TimeLine => new List<Vec3> { At + new Vec3(-8, 0, 0), At, At + new Vec3(8, 0, 0) },
            _ => Chevron.Build(At, new Vec3(0, -1, 0), 90, 6)
        };
        var segs = shape == TimingLineStyle.Point ? new[] { new TimingLineSegment(0, 7, 5) } : new[] { new TimingLineSegment(0, 7, 5), new TimingLineSegment(0, 9, 3) };
        var line = EbsTimingLine.CreateEntity(pts, segs, o);
        line.Color = 0xFF112233;
        return line;
    }

    public static IEnumerable<object[]> Conversions() =>
        from a in new[] { TimingLineStyle.Point, TimingLineStyle.TimeLine, TimingLineStyle.BurnLine }
        from b in new[] { TimingLineStyle.Point, TimingLineStyle.TimeLine, TimingLineStyle.BurnLine }
        where a != b
        select new object[] { a, b };

    [Theory]
    [MemberData(nameof(Conversions))]
    public void EveryFrontTakesEveryShape_ItsValuesAndHolesKept_ItsHolesProgrammedAgain_OneUndoBack(TimingLineStyle from, TimingLineStyle to)
    {
        var p = BlastProject.CreateNew("convert");
        // the holes to the south of the point
        var holes = new List<Hole> { H(p, -4, -6), H(p, 0, -9), H(p, 5, -5), H(p, 2, -14) };
        foreach (var h in holes) p.Add(h);
        var doc = new Document(p);
        var line = Front(from);
        line.HoleIds = holes.Select(h => h.Id).ToList();
        doc.Execute("front", pr => { pr.Add(line); EbsTimingLine.Apply(pr, holes, line); });
        var was = (line.Points.ToList(), line.Front, line.Style, line.Segments.ToList());
        var timesWas = holes.Select(h => h.ElectronicTimeMs).ToList();
        Assert.All(timesWas, t => Assert.NotNull(t));

        // the type chosen in the properties panel: the front takes its shape, its holes are programmed again, one step
        doc.Execute("type", pr =>
        {
            var l = (TimingLineEntity)pr.FindEntity(line.Id)!;
            var mine = pr.Holes.Where(h => l.HoleIds.Contains(h.Id)).ToList();
            l.Style = to;
            l.ShapeByType(new Vec3(mine.Average(h => h.Collar.X), mine.Average(h => h.Collar.Y), 0));
            EbsTimingLine.Apply(pr, mine, l);
        });
        var now = (TimingLineEntity)doc.Project.FindEntity(line.Id)!;
        Assert.Equal(to, now.Style);
        Assert.Equal(to, now.AllowedStyles().Single(s => s == to));
        switch (to)
        {
            case TimingLineStyle.Point:
                Assert.True(now.IsPoint());
                // at the vertex (the middle of a line), the delay of the first segment
                Assert.Equal(0, Vec3.Distance2D(now.Points[0], At), 9);
                Assert.Equal(new[] { new TimingLineSegment(0, 7, 5) }, now.Segments);
                break;
            case TimingLineStyle.TimeLine:
                Assert.Equal(TimingFrontShape.Chevron, now.Front);
                Assert.True(Chevron.IsStraight(now.Points));
                Assert.Equal(7, now.Segments[0].ReliefMsPerM);
                Assert.Equal(7, now.Segments[1].ReliefMsPerM);
                break;
            default:
                Assert.Equal(TimingFrontShape.Chevron, now.Front);
                Assert.False(Chevron.IsStraight(now.Points));
                Assert.Equal(7, now.Segments[0].ReliefMsPerM);
                break;
        }
        if (from == TimingLineStyle.Point)
        {
            // made from a point: through it, its later side (the ray) toward the middle of its holes, south here
            Assert.Equal(0, Vec3.Distance2D(now.Points[1], At), 9);
            Assert.True(Chevron.RayDir(now.Points).Y < -0.99);
            Assert.Equal(to == TimingLineStyle.BurnLine ? Chevron.DefaultAngleDeg : 180, Chevron.AngleDeg(now.Points), 6);
            Assert.Equal(TimingLineEntity.FromPointLength, Chevron.Length(now.Points), 9);
        }
        // the start, the progress, the order, the colour and the holes stay
        Assert.Equal(30, now.StartOffsetMs);
        Assert.True(now.UseProgressive);
        Assert.Equal(2, now.ProgressRate);
        Assert.True(now.ReverseOrder);
        Assert.Equal(0xFF112233u, now.Color);
        Assert.Equal(holes.Select(h => h.Id), now.HoleIds);
        // programmed again from the new shape: what it plans is what the holes have, the first at the start
        var mineNow = doc.Project.Holes.Where(h => now.HoleIds.Contains(h.Id)).ToList();
        var plan = EbsTimingLine.Plan(doc.Project, mineNow, now.Points, now.Segments, EbsTimingLine.OptionsOf(now), doc.Project.Electronic.MaxTimeMs, doc.Project.Electronic.AllowSubMs).Times;
        Assert.Equal(mineNow.Count, plan.Count);
        foreach (var h in mineNow) Assert.Equal(plan[h.Id], h.ElectronicTimeMs!.Value, 6);
        Assert.Equal(30, mineNow.Min(h => h.ElectronicTimeMs!.Value), 6);

        // one undo: the front and the times as they were
        doc.Undo();
        var back = (TimingLineEntity)doc.Project.FindEntity(line.Id)!;
        Assert.Equal(was.Item1, back.Points);
        Assert.Equal(was.Item2, back.Front);
        Assert.Equal(was.Item3, back.Style);
        Assert.Equal(was.Item4, back.Segments);
        Assert.Equal(timesWas, holes.Select(h => ((Hole)doc.Project.FindEntity(h.Id)!).ElectronicTimeMs).ToList());
    }

    [Fact]
    public void ALinePickedPointByPointTakesNoPoint_AFrontNoAdvancedBurn()
    {
        var picked = new TimingLineEntity { Points = { At, At + new Vec3(10, 0, 0) }, Segments = { new(0, 8, 0) } };
        Assert.Equal(new[] { TimingLineStyle.TimeLine, TimingLineStyle.BurnLine, TimingLineStyle.AdvancedBurn }, picked.AllowedStyles());
        foreach (var s in new[] { TimingLineStyle.Point, TimingLineStyle.TimeLine, TimingLineStyle.BurnLine })
            Assert.DoesNotContain(TimingLineStyle.AdvancedBurn, Front(s).AllowedStyles());
        // a picked line set to the point type keeps its shape
        picked.Style = TimingLineStyle.Point;
        picked.ShapeByType();
        Assert.Equal(2, picked.Points.Count);
    }

    [Fact]
    public void APointFrontIsPickedByItsPoint_AndDrawnAsARing()
    {
        var line = new TimingLineEntity { Front = TimingFrontShape.Point, Points = { At }, Segments = { new(0, 8, 0) } };
        Assert.Equal(5, line.PickDistance(At + new Vec3(3, 4, 0)), 9);
        Assert.Equal(At, line.AngleEnd(1));
    }
}
