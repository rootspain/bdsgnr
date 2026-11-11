using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using BlastDesigner.Rendering;

namespace BlastDesigner.Tests;

public class GeometryTests
{
    [Fact]
    public void HoleDirection_VerticalPointsDown()
    {
        var d = GeoMath.HoleDirection(0, 0);
        Assert.Equal(-1, d.Z, 6);
    }

    [Fact]
    public void HoleDirection_AngledLeansTowardsBearing()
    {
        var d = GeoMath.HoleDirection(90, 30);
        Assert.True(d.X > 0.49 && d.X < 0.51);
        Assert.Equal(-Math.Cos(30 * GeoMath.Deg), d.Z, 6);
    }

    [Fact]
    public void SetToe_RecoversAngleBearingAndLength()
    {
        var h = new Hole { Collar = new Vec3(100, 100, 50), Angle = 20, Bearing = 135, Length = 12 };
        var toe = h.DesignToe;
        var copy = new Hole { Collar = h.Collar };
        copy.SetToe(toe);
        Assert.Equal(20, copy.Angle, 6);
        Assert.Equal(135, copy.Bearing, 6);
        Assert.Equal(12, copy.Length, 6);
    }

    [Fact]
    public void PolygonAreaAndContainment()
    {
        var sq = new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 10, 0), new Vec3(0, 10, 0) };
        Assert.Equal(100, GeoMath.Area(sq), 6);
        Assert.True(GeoMath.PointInPolygon(sq, 5, 5));
        Assert.False(GeoMath.PointInPolygon(sq, 15, 5));
        var bigger = GeoMath.OffsetPolygon(sq, 1);
        Assert.Equal(144, GeoMath.Area(bigger), 3);
    }

    [Fact]
    public void ClipPolyline_KeepsInsidePart()
    {
        var sq = new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 10, 0), new Vec3(0, 10, 0) };
        var pieces = GeoMath.ClipPolylineToPolygon(new[] { new Vec3(-5, 5, 0), new Vec3(15, 5, 0) }, sq);
        Assert.Single(pieces);
        Assert.Equal(10, GeoMath.Length(pieces[0]), 6);
    }
}

public class DesignTests
{
    private static readonly Vec3[] Square = { new(0, 0, 100), new(40, 0, 100), new(40, 40, 100), new(0, 40, 100) };

    [Fact]
    public void FillPolygon_RectangularPatternCountsHoles()
    {
        var holes = HoleDesign.FillPolygon(Square, new PatternParameters { Burden = 4, Spacing = 5, Pattern = PatternType.Rectangular }, new HoleParameters { BenchHeight = 10, Subdrill = 1 }, out var rows);
        Assert.Equal(10, rows.Count);
        Assert.Equal(80, holes.Count);
        Assert.All(holes, h => Assert.Equal(11, h.Length, 6));
        Assert.All(holes, h => Assert.True(GeoMath.PointInPolygon(Square, h.Collar.X, h.Collar.Y)));
    }

    [Fact]
    public void FillPolygon_StaggeredOffsetsAlternateRows()
    {
        HoleDesign.FillPolygon(Square, new PatternParameters { Burden = 4, Spacing = 5, Pattern = PatternType.Staggered }, new HoleParameters(), out var rows);
        Assert.True(rows.Count >= 2);
    }

    [Fact]
    public void AngledHoleLengthReachesGrade()
    {
        var h = HoleDesign.CreateHole(new Vec3(0, 0, 100), new HoleParameters { Angle = 20, BenchHeight = 10, Subdrill = 1 });
        Assert.Equal(89, h.DesignToe.Z, 6);
    }

    [Fact]
    public void Duplicates_AreFoundAndRelabelled()
    {
        var holes = new List<Hole>
        {
            new() { Label = "1", Collar = new Vec3(0, 0, 0) },
            new() { Label = "1", Collar = new Vec3(0.1, 0, 0) },
            new() { Label = "2", Collar = new Vec3(5, 0, 0) }
        };
        Assert.Single(HoleDesign.FindDuplicates(holes, 0.2));
        Assert.Equal(1, HoleDesign.RelabelDuplicates(holes));
        Assert.Equal(3, holes.Select(h => h.Label).Distinct().Count());
    }

    [Fact]
    public void AutoRows_GroupsByOrientation()
    {
        var holes = HoleDesign.FillPolygon(Square, new PatternParameters { Burden = 4, Spacing = 5, Pattern = PatternType.Rectangular }, new HoleParameters(), out _);
        var rows = HoleDesign.AutoRows(holes, 0, 2);
        Assert.Equal(10, rows.Count);
    }
}

public class ChargingTests
{
    [Fact]
    public void ApplyRule_BuildsColumnWithStemmingAndPrimer()
    {
        var p = BlastProject.CreateNew();
        var h = p.Add(new Hole { Collar = new Vec3(0, 0, 0), Length = 11, Diameter = 102 });
        Assert.Equal(1, Charging.ApplyRules(p, new[] { h }));
        Assert.Equal(2, h.Decks.Count);
        Assert.Equal(3, Charging.TopStemming(p, h), 6);
        var anfo = p.Products.First(x => x.Name == "ANFO");
        var expected = Math.PI * 0.051 * 0.051 * 8 * anfo.Density * 1000 + 0.4;
        Assert.Equal(expected, Charging.ExplosiveMass(p, h), 3);
        Assert.Single(Charging.Primers(h));
        Assert.Empty(Charging.Validate(p, h));
    }

    [Fact]
    public void AdaptColumn_KeepsStemmingAndStretchesMainDeck()
    {
        var p = BlastProject.CreateNew();
        var h = p.Add(new Hole { Length = 11 });
        Charging.ApplyRules(p, new[] { h });
        var longer = Charging.AdaptColumn(p, h.Decks, 11, 13);
        Assert.Equal(3, longer.Where(d => d.Top > 0).Min(d => d.Top), 6);
        Assert.Equal(13, longer.Max(d => d.Bottom), 6);
        Assert.Equal(12.5, longer.SelectMany(d => d.Primers).Single().Depth, 6);
        var shorter = Charging.AdaptColumn(p, h.Decks, 11, 9);
        Assert.Equal(9, shorter.Max(d => d.Bottom), 6);
        Assert.Equal(3, shorter.Where(d => d.Top == 0).Single().Bottom, 6);
    }

    [Fact]
    public void RuleFromColumn_ReappliesAndAdaptsLikeAdaptColumn()
    {
        var p = BlastProject.CreateNew();
        string Id(string name) => p.Products.First(x => x.Name == name).Id;
        Primer Pr(double depth) => new() { Depth = depth, BoosterId = p.Products.First(x => x.Kind == ProductKind.Booster).Id, DetonatorId = p.Products.First(x => x.IsDetonator).Id };
        var column = new List<Deck>
        {
            new() { Top = 0, Bottom = 2.5, ProductId = Id("Crushed aggregate") },
            new() { Top = 2.5, Bottom = 7, ProductId = Id("ANFO"), Primers = { Pr(6.6) } },
            new() { Top = 7, Bottom = 8, ProductId = Id("Drill cuttings") },
            new() { Top = 8, Bottom = 11, ProductId = Id("Bulk emulsion"), Primers = { Pr(10.5), Pr(9) } }
        };
        // The rule goes through the project's JSON like the rules reference does.
        var rule = ProjectJson.DeepClone(Charging.RuleFromColumn(p, column, "Saved"));
        Assert.Equal(2.5, rule.StemmingLength, 6);
        Assert.Equal(0, rule.Decks.Single(t => t.ProductId == Id("ANFO")).Length);

        foreach (var length in new[] { 11.0, 14.0, 9.5 })
        {
            var h = new Hole { Length = length, Diameter = 102 };
            Charging.ApplyRule(p, h, rule);
            var expected = Charging.AdaptColumn(p, column, 11, length);
            Assert.Equal(expected.Count, h.Decks.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].ProductId, h.Decks[i].ProductId);
                Assert.Equal(expected[i].Top, h.Decks[i].Top, 6);
                Assert.Equal(expected[i].Bottom, h.Decks[i].Bottom, 6);
                Assert.Equal(expected[i].Primers.Select(x => Math.Round(x.Depth, 6)).OrderBy(x => x), h.Decks[i].Primers.Select(x => Math.Round(x.Depth, 6)).OrderBy(x => x));
            }
        }
    }

    [Fact]
    public void JoinLikeDecks_MergesAdjacentSameProduct()
    {
        var id = Guid.NewGuid().ToString();
        var h = new Hole { Length = 10, Decks = { new Deck { Top = 3, Bottom = 6, ProductId = id }, new Deck { Top = 6, Bottom = 10, ProductId = id } } };
        Charging.JoinLikeDecks(new[] { h });
        Assert.Single(h.Decks);
        Assert.Equal(7, h.Decks[0].Length, 6);
    }
}

public class TimingTests
{
    /// <summary>
    /// Tie delays by initiation system: a connector or cord relay its delay, detonating cord its length over its VOD,
    /// electric wiring and harness wire nothing, a user-defined tie its own delay.
    /// </summary>
    [Fact]
    public void TieDelay_ByInitiationSystem()
    {
        var p = BlastProject.CreateNew();
        var a = p.Add(new Hole { Collar = new Vec3(0, 0, 100) });
        var b = p.Add(new Hole { Collar = new Vec3(13, 0, 100) });
        Product Of(ProductKind k) => p.Products.First(x => x.Kind == k);
        double Delay(Product? product, double? own = null) => Timing.TieDelay(p, Timing.Connect(p, a.Id, b.Id, product, own));
        Assert.Equal(25, Delay(p.Products.First(x => x.Kind == ProductKind.SurfaceConnector && x.DelayMs == 25)));
        Assert.Equal(17, Delay(p.Products.First(x => x.Kind == ProductKind.CordRelay && x.DelayMs == 17)));
        Assert.Equal(13 / 6500.0 * 1000, Delay(Of(ProductKind.DetonatingCord)), 6);
        var electric = new Product { Name = "Electric detonator 25 ms", Kind = ProductKind.ElectricDetonator, DelayMs = 25 };
        p.Products.Add(electric);
        Assert.Equal(0, Delay(electric));
        Assert.Equal(0, Delay(Of(ProductKind.HarnessWire)));
        Assert.Equal(33, Delay(null, 33));
    }

    /// <summary>An electric round: harness wire takes the current to every hole at once, each fires at its detonator's delay.</summary>
    [Fact]
    public void Solve_ElectricRoundFiresByDetonatorDelay()
    {
        var p = BlastProject.CreateNew();
        var wire = p.Products.First(x => x.Kind == ProductKind.HarnessWire);
        // the built-in catalogue has no electric detonators: the round brings its own
        var dets = new[] { 25.0, 500.0 }.Select(ms => new Product { Name = $"Electric detonator {ms} ms", Kind = ProductKind.ElectricDetonator, DelayMs = ms }).ToList();
        p.Products.AddRange(dets);
        var ip = p.Add(new InitiationPoint { Position = new Vec3(-5, 0, 100) });
        var holes = new List<Hole>();
        for (int i = 0; i < 2; i++)
        {
            var h = p.Add(new Hole { Collar = new Vec3(i * 5, 0, 100), Length = 10 });
            h.Decks.Add(new Deck { Top = 3, Bottom = 10, ProductId = p.Products.First(x => x.IsExplosive).Id, Primers = { new Primer { Depth = 9, DetonatorId = dets[i].Id } } });
            holes.Add(h);
        }
        Timing.Connect(p, ip.Id, holes[0].Id, wire);
        Timing.Connect(p, holes[0].Id, holes[1].Id, wire);
        Timing.Solve(p);
        Assert.Equal(dets[0].DelayMs, holes[0].FiringTimeMs);
        Assert.Equal(dets[1].DelayMs, holes[1].FiringTimeMs);
    }

    private static (BlastProject P, List<Hole> Holes, InitiationPoint Ip) Row(int n)
    {
        var p = BlastProject.CreateNew();
        var holes = Enumerable.Range(0, n).Select(i => p.Add(new Hole { Label = (i + 1).ToString(), Collar = new Vec3(i * 5, 0, 0), Length = 10 })).ToList();
        var ip = p.Add(new InitiationPoint { Position = new Vec3(-5, 0, 0) });
        Charging.ApplyRules(p, holes);
        return (p, holes, ip);
    }

    [Fact]
    public void Solve_PropagatesSurfaceDelaysAndAddsInholeDelay()
    {
        var (p, holes, ip) = Row(4);
        var c17 = p.Products.First(x => x.DelayMs == 17 && x.Kind == ProductKind.SurfaceConnector);
        Timing.Connect(p, ip.Id, holes[0].Id, null, 0);
        for (int i = 1; i < holes.Count; i++) Timing.Connect(p, holes[i - 1].Id, holes[i].Id, c17);
        Timing.Solve(p);
        Assert.Equal(new double?[] { 0, 17, 34, 51 }, holes.Select(h => h.SurfaceTimeMs).ToArray());
        Assert.Equal(500 + 51, holes[3].FiringTimeMs);
        Assert.Equal(new int?[] { 1, 2, 3, 4 }, holes.Select(h => h.FiringSequence).ToArray());
    }

    /// <summary>An initiation point bound to a hole starts it; the ties go on from the hole.</summary>
    [Fact]
    public void Solve_BoundInitiationPointStartsItsHole()
    {
        var (p, holes, ip) = Row(3);
        var c17 = p.Products.First(x => x.DelayMs == 17 && x.Kind == ProductKind.SurfaceConnector);
        ip.Position = holes[1].Collar;
        ip.HoleId = holes[1].Id;
        Timing.Connect(p, holes[1].Id, holes[0].Id, c17);
        Timing.Connect(p, holes[1].Id, holes[2].Id, c17);
        Timing.Solve(p);
        Assert.Equal(new double?[] { 17, 0, 17 }, holes.Select(h => h.SurfaceTimeMs).ToArray());
        Assert.Equal(holes[1].Label, ip.Hole);
        // unbound, standing on the same spot, it starts nothing: the signal goes only along ties
        ip.HoleId = null;
        Timing.Solve(p);
        Assert.All(holes, h => Assert.Null(h.SurfaceTimeMs));
    }

    /// <summary>A point placed in the middle of a tied row turns the ties behind it to run away from it.</summary>
    [Fact]
    public void OrientTiesFromInitiation_TurnsTiesAwayFromThePoint()
    {
        var (p, holes, ip) = Row(5);
        var c17 = p.Products.First(x => x.DelayMs == 17 && x.Kind == ProductKind.SurfaceConnector);
        for (int i = 1; i < holes.Count; i++) Timing.Connect(p, holes[i - 1].Id, holes[i].Id, c17);
        var lone = p.Add(new Hole { Label = "far", Collar = new Vec3(100, 100, 0), Length = 10 });
        var other = p.Add(new Hole { Label = "far2", Collar = new Vec3(105, 100, 0), Length = 10 });
        Timing.Connect(p, other.Id, lone.Id, c17);
        ip.Position = holes[2].Collar;
        ip.HoleId = holes[2].Id;
        Assert.Equal(2, Timing.OrientTiesFromInitiation(p));
        Timing.Solve(p);
        Assert.Equal(new double?[] { 34, 17, 0, 17, 34 }, holes.Select(h => h.SurfaceTimeMs).ToArray());
        // a tie the point does not reach keeps its direction
        Assert.Contains(p.Ties, t => t.FromId == other.Id && t.ToId == lone.Id);
        Assert.Equal(0, Timing.OrientTiesFromInitiation(p));
    }

    /// <summary>The tie tool turns only the ties it adds: a tie drawn towards the point is turned, the others stay.</summary>
    [Fact]
    public void OrientTiesFromInitiation_OnlyTurnsTheTiesGiven()
    {
        var (p, holes, ip) = Row(4);
        var c17 = p.Products.First(x => x.DelayMs == 17 && x.Kind == ProductKind.SurfaceConnector);
        ip.Position = holes[0].Collar;
        ip.HoleId = holes[0].Id;
        // both drawn against the signal, but only the new one is to be turned
        var old = Timing.Connect(p, holes[1].Id, holes[0].Id, c17);
        var added = Timing.Connect(p, holes[2].Id, holes[1].Id, c17);
        Assert.Equal(1, Timing.OrientTiesFromInitiation(p, new HashSet<Guid> { added.Id }));
        Assert.Contains(p.Ties, t => t.FromId == holes[1].Id && t.ToId == holes[2].Id);
        Assert.Contains(p.Ties, t => t.Id == old.Id && t.FromId == holes[1].Id && t.ToId == holes[0].Id);
    }

    [Fact]
    public void PlaybackRate_ShowsTheBlastInTheSecondsChosen()
    {
        Assert.Equal(1, Timing.PlaybackRate(972, 0));
        Assert.Equal(0.1, Timing.PlaybackRate(1000, 10), 9);
        // a longer blast runs faster and takes the same time
        Assert.Equal(0.5, Timing.PlaybackRate(5000, 10), 9);
    }

    /// <summary>0 ms ties reach both their ends at the same time: they point away from the end fewer ties from the point.</summary>
    [Fact]
    public void OrientTiesFromInitiation_TurnsZeroDelayTies()
    {
        var (p, holes, ip) = Row(4);
        ip.Position = holes[0].Collar;
        ip.HoleId = holes[0].Id;
        Timing.Connect(p, holes[0].Id, holes[1].Id, null);
        Timing.Connect(p, holes[3].Id, holes[2].Id, null);
        Timing.Connect(p, holes[2].Id, holes[1].Id, null);
        Assert.Equal(2, Timing.OrientTiesFromInitiation(p));
        Assert.Contains(p.Ties, t => t.FromId == holes[1].Id && t.ToId == holes[2].Id);
        Assert.Contains(p.Ties, t => t.FromId == holes[2].Id && t.ToId == holes[3].Id);
    }

    [Fact]
    public void ReachedTies_AreThoseJoinedToAnInitiationPoint()
    {
        var (p, holes, ip) = Row(4);
        ip.Position = holes[0].Collar;
        ip.HoleId = holes[0].Id;
        var joined = Timing.Connect(p, holes[1].Id, holes[0].Id, null);
        var apart = Timing.Connect(p, holes[3].Id, holes[2].Id, null);
        Assert.Equal(new[] { joined.Id }, Timing.ReachedTies(p));
        Timing.Connect(p, holes[2].Id, holes[1].Id, null);
        Assert.Contains(apart.Id, Timing.ReachedTies(p));
    }

    /// <summary>A bound point and its hole move together either way; a deleted hole takes the point, a deleted point leaves the hole.</summary>
    [Fact]
    public void InitiationLinks_PointAndHoleStayTogether()
    {
        var (p, holes, ip) = Row(3);
        ip.Position = holes[1].Collar;
        ip.HoleId = holes[1].Id;
        var doc = new Document(p);
        var d = new Vec3(2, 1, 0);
        doc.Execute("Move hole", pr => pr.FindEntity(holes[1].Id)!.Translate(d));
        var h1 = (Hole)doc.Project.FindEntity(holes[1].Id)!;
        var point = doc.Project.Entities.OfType<InitiationPoint>().Single();
        Assert.Equal(h1.Collar, point.Position);
        doc.Execute("Move point", pr => pr.FindEntity(point.Id)!.Translate(d));
        Assert.Equal(new Vec3(5 + 4, 2, 0), h1.Collar);
        Assert.Equal(h1.Collar, point.Position);
        // both moved together stay together
        doc.Execute("Move both", pr => { pr.FindEntity(point.Id)!.Translate(d); pr.FindEntity(h1.Id)!.Translate(d); });
        Assert.Equal(h1.Collar, point.Position);
        Assert.Equal(new Vec3(5 + 6, 3, 0), h1.Collar);
        // a locked hole keeps its place and the point with it
        h1.Locked = true;
        doc.Execute("Move point", pr => pr.FindEntity(point.Id)!.Translate(d));
        Assert.Equal(new Vec3(5 + 6, 3, 0), h1.Collar);
        Assert.Equal(h1.Collar, point.Position);
        h1.Locked = false;
        doc.Execute("Delete point", pr => pr.Entities.RemoveAll(e => e is InitiationPoint));
        Assert.Equal(3, doc.Project.Holes.Count());
        doc.Undo();
        Timing.Connect(doc.Project, doc.Project.Entities.OfType<InitiationPoint>().Single().Id, holes[0].Id, null, 0);
        doc.Execute("Delete hole", pr => pr.Entities.RemoveAll(e => e.Id == holes[1].Id));
        Assert.Empty(doc.Project.Entities.OfType<InitiationPoint>());
        Assert.Empty(doc.Project.Ties);
        Assert.Equal(2, doc.Project.Holes.Count());
    }

    /// <summary>A point of an older file standing on a collar is bound to that hole when the project is loaded.</summary>
    [Fact]
    public void InitiationLinks_NormalizeBindsPointOnCollar()
    {
        var (p, holes, ip) = Row(3);
        var off = p.Add(new InitiationPoint { Position = new Vec3(100, 100, 0) });
        ip.Position = holes[2].Collar + new Vec3(0.004, 0, 0);
        InitiationLinks.Normalize(p);
        Assert.Equal(holes[2].Id, ip.HoleId);
        Assert.Equal(holes[2].Collar, ip.Position);
        Assert.Null(off.HoleId);
    }

    [Fact]
    public void Solve_TakesEarliestPath()
    {
        var (p, holes, ip) = Row(3);
        var c17 = p.Products.First(x => x.DelayMs == 17);
        var c109 = p.Products.First(x => x.DelayMs == 109);
        Timing.Connect(p, ip.Id, holes[0].Id, null, 0);
        Timing.Connect(p, holes[0].Id, holes[1].Id, c17);
        Timing.Connect(p, holes[1].Id, holes[2].Id, c17);
        Timing.Connect(p, holes[0].Id, holes[2].Id, c109);
        Timing.Solve(p);
        Assert.Equal(34, holes[2].SurfaceTimeMs);
    }

    [Fact]
    public void AutoTieUp_InitiatesEveryHole()
    {
        var p = BlastProject.CreateNew();
        var holes = HoleDesign.FillPolygon(new[] { new Vec3(0, 0, 0), new Vec3(30, 0, 0), new Vec3(30, 20, 0), new Vec3(0, 20, 0) },
            new PatternParameters { Burden = 4, Spacing = 5 }, new HoleParameters(), out var rows);
        foreach (var h in holes) p.Add(h);
        p.Rows.AddRange(rows);
        var ip = p.Add(new InitiationPoint { Position = new Vec3(15, -5, 0) });
        var c17 = p.Products.First(x => x.DelayMs == 17);
        var c42 = p.Products.First(x => x.DelayMs == 42);
        Timing.AutoTieUp(p, holes, ip, c17, c42, 2);
        Timing.Solve(p);
        Assert.All(holes, h => Assert.NotNull(h.FiringTimeMs));
    }

    [Fact]
    public void Electronic_RowSetAndEnvelope()
    {
        var (p, holes, _) = Row(5);
        Timing.RowSet(holes, 100, 8);
        Timing.Solve(p);
        Assert.Equal(132, holes[4].FiringTimeMs);
        var env = Timing.TimeEnvelope(p, 8);
        Assert.All(env, e => Assert.Equal(1, e.Holes));
        Timing.ScaleIntervals(holes, 2);
        Assert.Equal(164, holes[4].ElectronicTimeMs);
    }
}

public class SurfaceAndQuantityTests
{
    [Fact]
    public void Triangulate_PlaneGivesExactElevationsAndContours()
    {
        var pts = new List<Vec3>();
        for (int x = 0; x <= 10; x++)
            for (int y = 0; y <= 10; y++)
                pts.Add(new Vec3(x, y, 100 + 0.5 * x));
        var s = Surfaces.Triangulate(pts);
        Assert.Equal(200, s.TriangleCount);
        Assert.Equal(102.25, s.ElevationAt(4.5, 3.3)!.Value, 6);
        Assert.Null(s.ElevationAt(20, 20));
        var contours = Surfaces.Contours(s, 1);
        Assert.Equal(5, contours.Count);
    }

    /// <summary>
    /// A bench face: crest and toe 1.5 m apart in plan, 60 m long without vertices between, and a picket 20 m off
    /// each side, at site coordinates in the millions. The pickets lie inside the huge circles of the thin face, so plain
    /// Delaunay joins them across the lines; with the lines forced in as edges no triangle cuts across, and the face
    /// lies between the two lines.
    /// </summary>
    [Fact]
    public void FromEntities_TrianglesDoNotCutAcrossSurveyLines()
    {
        const double x0 = 4_316_000, y0 = 5_513_000;
        PolylineEntity Line(double dy, double z) => new() { Points = { new Vec3(x0, y0 + dy, z), new Vec3(x0 + 60, y0 + dy, z) } };
        var crest = Line(0, 110);
        var toe = Line(-1.5, 100);
        var pickets = new List<Entity>
        {
            new PointEntity { Position = new Vec3(x0 + 30, y0 + 20, 110.5) },
            new PointEntity { Position = new Vec3(x0 + 30, y0 - 21.5, 99.5) }
        };
        int Crossings(SurfaceEntity s)
        {
            int n = 0;
            for (int t = 0; t < s.Triangles.Count; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    var a = s.Vertices[s.Triangles[t + k]]; var b = s.Vertices[s.Triangles[t + (k + 1) % 3]];
                    foreach (var l in new[] { crest, toe })
                        for (int i = 1; i < l.Points.Count; i++)
                            if (GeoMath.SegmentIntersect2D(a, b, l.Points[i - 1], l.Points[i], out var ta, out var tb) && ta > 1e-6 && ta < 1 - 1e-6 && tb > 1e-6 && tb < 1 - 1e-6) n++;
                }
            return n;
        }
        var input = new List<Entity> { crest, toe };
        input.AddRange(pickets);
        // densify far beyond the segment length: only the line vertices go in, the hardest case for the lines
        var plain = Surfaces.Triangulate(input.SelectMany(e => e.GetVertices()));
        Assert.True(Crossings(plain) > 0, "the test scene should defeat plain Delaunay");
        var forced = Surfaces.FromEntities(input, true, densify: 100);
        Assert.Equal(0, Crossings(forced));
        // halfway between crest and toe, on the face
        var z = forced.ElevationAt(x0 + 45, y0 - 0.75)!.Value;
        Assert.InRange(z, 100, 110);
    }

    /// <summary>
    /// The band between a crest and its toe goes to the faces, the berms above and below stay with the ground; the two
    /// parts keep every triangle and only the vertices they use.
    /// </summary>
    [Fact]
    public void SplitFaces_BandBetweenCrestAndToe()
    {
        var crest = new List<Vec3> { new(0, 0, 110), new(60, 0, 110) };
        var toe = new List<Vec3> { new(0, -6, 100), new(60, -6, 100) };
        var input = new List<Entity>
        {
            new PolylineEntity { Points = crest }, new PolylineEntity { Points = toe },
            new PointEntity { Position = new Vec3(10, 15, 110.2) }, new PointEntity { Position = new Vec3(50, 15, 110.1) },
            new PointEntity { Position = new Vec3(10, -20, 100.1) }, new PointEntity { Position = new Vec3(50, -20, 99.9) }
        };
        var s = Surfaces.FromEntities(input, true);
        var (ground, faces) = Surfaces.SplitFaces(s, new[] { crest }, new[] { toe });
        Assert.NotNull(faces);
        Assert.Equal(s.TriangleCount, ground.TriangleCount + faces!.TriangleCount);
        // every face triangle lies between the two lines, every ground triangle outside them
        for (int t = 0; t < faces.Triangles.Count; t += 3)
            Assert.InRange((faces.Vertices[faces.Triangles[t]].Y + faces.Vertices[faces.Triangles[t + 1]].Y + faces.Vertices[faces.Triangles[t + 2]].Y) / 3, -6, 0);
        for (int t = 0; t < ground.Triangles.Count; t += 3)
            Assert.False((ground.Vertices[ground.Triangles[t]].Y + ground.Vertices[ground.Triangles[t + 1]].Y + ground.Vertices[ground.Triangles[t + 2]].Y) / 3 is > -6 and < 0);
        Assert.Equal(105, faces.ElevationAt(30, -3)!.Value, 1);
        Assert.Equal(faces.Triangles.Distinct().Count(), faces.Vertices.Count);
    }

    /// <summary>
    /// Timing contours of three rows 14 m and 13 m apart, 4.6 m between holes, with the 4 × 4.5 m design: the limit
    /// follows the rows on the ground, so the contours reach the first row (they stopped at the second one).
    /// </summary>
    [Fact]
    public void TimingContours_ReachRowsFartherApartThanTheDesign()
    {
        var p = BlastProject.CreateNew();
        double[] xs = { 0, 14, 27 };
        var holes = new List<Hole>();
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < 12; i++)
                holes.Add(new Hole { Collar = new Vec3(1000 + xs[c], 2000 + i * 4.6, 100), FiringTimeMs = (c == 0 ? 50 : c == 1 ? 25 : 0) + i * 17 });
        var limit = TimingContourOverlay.MaxEdge(p, holes);
        Assert.True(limit > 14.8, $"limit {limit}");
        var tin = Surfaces.Triangulate(holes.Select(h => h.Collar.WithZ(h.FiringTimeMs!.Value)), "time", limit);
        // the first row is part of the timing surface
        Assert.NotNull(tin.ElevationAt(1007, 2025));
        // and a notch stays empty: a 3 × 4.5 m pattern has a limit near the design one
        var tight = new List<Hole>();
        for (int c = 0; c < 4; c++) for (int i = 0; i < 6; i++) tight.Add(new Hole { Collar = new Vec3(c * 4, i * 4.5, 0) });
        Assert.InRange(TimingContourOverlay.MaxEdge(p, tight), 13.4, 13.6);
    }

    /// <summary>
    /// Two faces with a 20 m platform between them: the platform (toe of the upper face and crest of the lower one at its
    /// level) is a bench, the ground above the top crest and the floor below the last toe are not.
    /// </summary>
    [Fact]
    public void SplitBenches_PlatformBetweenFaces()
    {
        List<Vec3> Line(double y, double z) => new() { new(0, y, z), new(80, y, z) };
        var crest1 = Line(0, 110); var toe1 = Line(-6, 100);
        var crest2 = Line(-26, 100); var toe2 = Line(-32, 90);
        var input = new List<Entity> { new PolylineEntity { Points = crest1 }, new PolylineEntity { Points = toe1 }, new PolylineEntity { Points = crest2 }, new PolylineEntity { Points = toe2 } };
        foreach (var x in new[] { 10.0, 40, 70 })
        {
            input.Add(new PointEntity { Position = new Vec3(x, 30, 110.1) });
            input.Add(new PointEntity { Position = new Vec3(x, -16, 100.1) });
            input.Add(new PointEntity { Position = new Vec3(x, -70, 90.1) });
        }
        var s = Surfaces.FromEntities(input, true);
        var crests = new[] { crest1, crest2 };
        var toes = new[] { toe1, toe2 };
        var (ground, faces) = Surfaces.SplitFaces(s, crests, toes);
        var (rest, benches) = Surfaces.SplitBenches(ground, crests, toes);
        Assert.NotNull(faces);
        Assert.NotNull(benches);
        Assert.Equal(s.TriangleCount, faces!.TriangleCount + benches!.TriangleCount + rest.TriangleCount);
        Assert.Equal(100, benches.ElevationAt(40, -16)!.Value, 0);
        Assert.Null(benches.ElevationAt(40, 20));
        Assert.Null(benches.ElevationAt(40, -60));
        Assert.NotNull(rest.ElevationAt(40, 20));
        Assert.NotNull(rest.ElevationAt(40, -60));
    }

    [Fact]
    public void VolumeBetween_FlatSurfaceAboveBase()
    {
        var s = Surfaces.Triangulate(new[] { new Vec3(0, 0, 10), new Vec3(10, 0, 10), new Vec3(10, 10, 10), new Vec3(0, 10, 10) });
        var (cut, fill, area) = Surfaces.VolumeBetween(s, null, 0, null, 0.5);
        Assert.Equal(1000, cut, 0);
        Assert.Equal(0, fill);
        Assert.Equal(100, area, 0);
    }

    [Fact]
    public void Quantities_VoronoiAreasMatchPattern()
    {
        var p = BlastProject.CreateNew();
        var poly = new[] { new Vec3(0, 0, 100), new Vec3(40, 0, 100), new Vec3(40, 40, 100), new Vec3(0, 40, 100) };
        var holes = HoleDesign.FillPolygon(poly, new PatternParameters { Burden = 4, Spacing = 5, Pattern = PatternType.Rectangular }, new HoleParameters { BenchHeight = 10, Subdrill = 1 }, out var rows);
        foreach (var h in holes) p.Add(h);
        p.Rows.AddRange(rows);
        p.Add(new PolylineEntity { Points = poly.ToList(), Closed = true, Kind = LineKind.Boundary });
        Charging.ApplyRules(p, holes);
        var q = Quantities.Compute(p);
        Assert.Equal(16000, q.Volume, 0);
        Assert.Equal(4, q.AverageBurden, 3);
        Assert.Equal(5, q.AverageSpacing, 3);
        Assert.True(q.PowderFactorKgM3 > 0.2 && q.PowderFactorKgM3 < 1.5);
        Assert.Contains(q.Products, x => x.Product.Name == "ANFO");
    }

    [Fact]
    public void Vibration_ScaledDistanceLaw()
    {
        var m = new VibrationModel { SiteK = 1140, SiteB = 1.6 };
        var ppv = Vibration.Ppv(m, 100, 100);
        Assert.Equal(1140 * Math.Pow(10, -1.6), ppv, 6);
        Assert.Equal(100, Vibration.AllowableCharge(m, 100, ppv), 3);
    }

    /// <summary>The prediction written out plainly: every window of every hole, summed and searched from scratch.</summary>
    private static Vibration.Prediction PlainPrediction(BlastProject project, Vec3 receiver)
    {
        var m = project.Vibration;
        var holes = project.Holes.Where(h => !h.IsDummy).Select(h => (H: h, Q: Charging.ExplosiveMass(project, h), D: Vec3.Distance(h.Collar, receiver))).Where(x => x.Q > 0).ToList();
        var timed = holes.Where(x => x.H.FiringTimeMs.HasValue).OrderBy(x => x.H.FiringTimeMs).ToList();
        var best = new Vibration.Prediction(0, 0, 0, 0, 0);
        if (timed.Count == 0)
        {
            foreach (var x in holes)
            {
                var ppv = Vibration.Ppv(m, x.D, x.Q);
                if (ppv > best.Ppv) best = new(ppv, Vibration.KPaToDb(Vibration.AirblastKPa(m, x.D, x.Q)), x.Q, x.D, 0);
            }
            return best;
        }
        for (int i = 0; i < timed.Count; i++)
        {
            var t0 = timed[i].H.FiringTimeMs!.Value;
            double q = 0, d = double.MaxValue;
            for (int j = i; j < timed.Count && timed[j].H.FiringTimeMs!.Value < t0 + m.WindowMs; j++)
            {
                q += timed[j].Q;
                d = Math.Min(d, timed[j].D);
            }
            var ppv = Vibration.Ppv(m, d, q);
            if (ppv > best.Ppv) best = new(ppv, Vibration.KPaToDb(Vibration.AirblastKPa(m, d, q)), q, d, t0);
        }
        return best;
    }

    private static BlastProject VibrationBlast(int delayMs, bool ties = true)
    {
        var p = BlastProject.CreateNew();
        var rng = new Random(7);
        var holes = new List<Hole>();
        for (int i = 0; i < 60; i++)
            holes.Add(p.Add(new Hole { Label = (i + 1).ToString(), Collar = new Vec3(i % 10 * 5 + rng.NextDouble(), i / 10 * 4 + rng.NextDouble(), rng.NextDouble()), Length = 8 + rng.NextDouble() * 6, IsDummy = i % 13 == 5 }));
        Charging.ApplyRules(p, holes);
        if (ties)
        {
            p.Add(new InitiationPoint { Position = holes[0].Collar, HoleId = holes[0].Id });
            for (int i = 1; i < holes.Count; i++) Timing.Connect(p, holes[i - 1].Id, holes[i].Id, null, delayMs == 0 ? 0 : delayMs + rng.Next(5));
        }
        Timing.Solve(p);
        return p;
    }

    [Theory]
    [InlineData(3, true, 8)]    // a few holes in each window
    [InlineData(40, true, 8)]   // one hole in each window
    [InlineData(0, true, 8)]    // all at once: one window of the whole blast
    [InlineData(3, false, 8)]   // no timing: each hole on its own
    [InlineData(3, true, 0)]    // no window at all
    public void Vibration_MapIsThePlainPredictionOfEachCell(int delayMs, bool ties, double windowMs)
    {
        var p = VibrationBlast(delayMs, ties);
        p.Vibration.WindowMs = windowMs;
        var area = new Bounds3 { Min = new Vec3(-300, -200, 0), Max = new Vec3(350, 250, 10) };
        var ppv = Vibration.Map(p, area, 9, 7, false);
        var air = Vibration.Map(p, area, 9, 7, true);
        for (int i = 0; i < 9; i++)
            for (int j = 0; j < 7; j++)
            {
                var at = new Vec3(area.Min.X + (area.Max.X - area.Min.X) * (i + 0.5) / 9, area.Min.Y + (area.Max.Y - area.Min.Y) * (j + 0.5) / 7, area.Center.Z);
                var plain = PlainPrediction(p, at);
                Assert.Equal(plain, Vibration.Predict(p, at));
                Assert.Equal(plain.Ppv, ppv[i, j]);
                Assert.Equal(plain.AirblastDb, air[i, j]);
            }
    }

    [Fact]
    public void TimingVectors_AreWorkedOutAgainOnlyWhenTheHolesChange()
    {
        var p = VibrationBlast(3);
        var overlay = new TimingVectorOverlay();
        var first = overlay.Analysis(p);
        Assert.Same(first, overlay.Analysis(p));
        var moved = p.Holes.First();
        moved.Translate(new Vec3(1, 0, 0));
        var afterMove = overlay.Analysis(p);
        Assert.NotSame(first, afterMove);
        // a programmed time fires a hole with an electronic detonator
        var last = p.Holes.Last();
        foreach (var (primer, _) in Charging.Primers(last)) primer.DetonatorId = p.Products.First(x => x.Kind == ProductKind.ElectronicDetonator).Id;
        last.ElectronicTimeMs = 5000;
        Timing.Solve(p);
        var afterTime = overlay.Analysis(p);
        Assert.NotSame(afterMove, afterTime);
        overlay.NeighbourRadius = 20;
        Assert.NotSame(afterTime, overlay.Analysis(p));
        // after an undo the same values are new holes
        var same = overlay.Analysis(p);
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(p));
        Timing.Solve(back);
        Assert.NotSame(same, overlay.Analysis(back));
    }
}

public class FileFormatTests
{
    private static BlastProject Sample()
    {
        var p = BlastProject.CreateNew("Test blast");
        var holes = HoleDesign.FillPolygon(new[] { new Vec3(0, 0, 100), new Vec3(20, 0, 100), new Vec3(20, 12, 100), new Vec3(0, 12, 100) },
            new PatternParameters { Burden = 4, Spacing = 5 }, new HoleParameters { Angle = 10, Bearing = 45 }, out var rows);
        foreach (var h in holes) p.Add(h);
        p.Rows.AddRange(rows);
        Charging.ApplyRules(p, holes);
        p.Add(new PolylineEntity { Points = { new Vec3(0, 0, 100), new Vec3(5, 5, 100) }, Kind = LineKind.Crest });
        p.Add(Surfaces.Triangulate(holes.Select(h => h.Collar)));
        p.Attachments.Add(new Attachment { FileName = "note.txt", Data = "hello"u8.ToArray() });
        return p;
    }

    [Fact]
    public void ProjectFile_RoundTrip()
    {
        var p = Sample();
        var path = Path.Combine(Path.GetTempPath(), $"bd_test_{Guid.NewGuid():N}.bdp");
        try
        {
            ProjectFile.Save(p, path, 2);
            ProjectFile.Save(p, path, 2);
            Assert.True(File.Exists(path + ".001"));
            var r = ProjectFile.Load(path);
            Assert.Equal(p.Header.Name, r.Header.Name);
            Assert.Equal(p.Holes.Count(), r.Holes.Count());
            Assert.Equal(p.Holes.First().Decks.Count, r.Holes.First().Decks.Count);
            Assert.Equal(p.Holes.First().Angle, r.Holes.First().Angle);
            Assert.Single(r.Entities.OfType<SurfaceEntity>());
            Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(r.Attachments[0].Data));
            Assert.Equal(Charging.ExplosiveMass(p, p.Holes.First()), Charging.ExplosiveMass(r, r.Holes.First()), 6);
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }

    [Fact]
    public void Fragmentation_RockFactorStaysWithinItsAllowedRange()
    {
        Assert.Equal(25, Fragmentation.RockFactor(new FragmentationModel { RockFactor = 40 }, 2.6));
        Assert.Equal(9, Fragmentation.RockFactor(new FragmentationModel { RockFactor = 9 }, 2.6));
        // calculated: a very strong, very jointed rock would give 0.06·(0 + 200 + 15 + 50) = 15.9; more is cut to 25
        Assert.Equal(25, Fragmentation.RockFactor(new FragmentationModel { RockFactor = 0, RockMassDescription = 0, JointFactor = 400, Ucs = 250 }, 2.6));
        Assert.Equal(0, Fragmentation.RockFactor(new FragmentationModel { RockFactor = 0, RockMassDescription = 0, JointFactor = 0, Ucs = 0, YoungsModulus = 0 }, 1.0));
    }

    [Fact]
    public void FragmentationPreset_SavesAndLoadsTheInputs()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + FragmentationPreset.Extension);
        try
        {
            FragmentationPreset.Save(path, new FragmentationModel { RockFactor = 0, Ucs = 140, JointFactor = 35, RockMassDescription = 0, OversizeCm = 90, SizeUnit = SizeUnit.Cm });
            var m = FragmentationPreset.Load(path);
            Assert.Equal((0.0, 140.0, 35.0, 0.0, 90.0), (m.RockFactor, m.Ucs, m.JointFactor, m.RockMassDescription, m.OversizeCm));
            Assert.Equal(SizeUnit.Cm, m.SizeUnit);
            File.WriteAllText(path, "{\"rockFactor\": 3}");
            Assert.Throws<InvalidDataException>(() => FragmentationPreset.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FragmentSizeUnit_IsSavedWithTheProjectAndConvertsTheSizes()
    {
        var p = new BlastProject();
        p.Fragmentation.SizeUnit = SizeUnit.Cm;
        Assert.Equal(SizeUnit.Cm, ProjectJson.Deserialize(ProjectJson.Serialize(p)).Fragmentation.SizeUnit);
        // a file from before it has none: the program settings give it
        var old = ProjectJson.Deserialize(ProjectJson.Serialize(new BlastProject())).Fragmentation;
        Assert.Null(old.SizeUnit);
        Assert.Equal(SizeUnits.Default, SizeUnits.Of(old));
        // the sizes stay in cm, only what is shown is converted
        Assert.Equal(2000, SizeUnits.FromCm(200, SizeUnit.Mm));
        Assert.Equal(200, SizeUnits.FromCm(200, SizeUnit.Cm));
        Assert.Equal(150, SizeUnits.ToCm(1500, SizeUnit.Mm));
    }

    private static SurfaceEntity Mesh(int side)
    {
        var s = new SurfaceEntity { Name = "Survey" };
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++) s.Vertices.Add(new Vec3(x, y, x * 0.1 + y));
        for (int y = 0; y + 1 < side; y++)
            for (int x = 0; x + 1 < side; x++)
            {
                int a = y * side + x;
                s.Triangles.AddRange(new[] { a, a + 1, a + side, a + 1, a + side + 1, a + side });
            }
        return s;
    }

    [Fact]
    public void Document_SelectionAlone_IsNoChangeOfTheModel()
    {
        var p = BlastProject.CreateNew();
        var h = p.Add(new Hole { Label = "1" });
        var doc = new Document(p);
        int changed = 0, selected = 0;
        doc.Changed += (_, _) => changed++;
        doc.SelectionChanged += (_, _) => selected++;
        doc.Select(new[] { h.Id });
        Assert.Equal((0, 1), (changed, selected));
        Assert.True(doc.CanUndo);
        doc.Undo();
        Assert.Empty(doc.Selection);
        doc.Redo();
        Assert.Single(doc.Selection);
        Assert.Equal(0, changed);
        Assert.False(doc.IsModified);
        // an undo of the model still tells
        doc.Execute("Move", pr => pr.FindEntity(h.Id)!.Translate(new Vec3(1, 0, 0)));
        doc.Undo();
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Document_UndoBringsBackTheMeshes()
    {
        var p = BlastProject.CreateNew();
        var first = Mesh(5);
        var second = Mesh(4);
        p.Add(first); p.Add(second); p.Add(new Hole { Label = "1" });
        var (vertices, triangles) = (first.Vertices.ToList(), first.Triangles.ToList());
        var doc = new Document(p);

        // a move, an edit of the mesh, a change of something else, a deletion: each comes back
        doc.Execute("Move", pr => pr.FindEntity(first.Id)!.Translate(new Vec3(10, 0, 0)));
        var moved = ((SurfaceEntity)doc.Project.FindEntity(first.Id)!).Vertices.ToList();
        doc.Execute("Cut", pr => ((SurfaceEntity)pr.FindEntity(first.Id)!).Triangles.RemoveRange(0, 3));
        doc.Execute("Rename", pr => ((SurfaceEntity)pr.FindEntity(first.Id)!).Name = "Renamed");
        doc.Execute("Delete", pr => pr.Entities.RemoveAll(e => e.Id == first.Id));
        Assert.Null(doc.Project.FindEntity(first.Id));

        SurfaceEntity Now() => (SurfaceEntity)doc.Project.FindEntity(first.Id)!;
        doc.Undo();
        Assert.Equal("Renamed", Now().Name);
        Assert.Equal(triangles.Count - 3, Now().Triangles.Count);
        doc.Undo();
        Assert.Equal("Survey", Now().Name);
        doc.Undo();
        Assert.Equal(triangles, Now().Triangles);
        Assert.Equal(moved, Now().Vertices);
        doc.Undo();
        Assert.Equal(vertices, Now().Vertices);
        Assert.Equal(16, ((SurfaceEntity)doc.Project.FindEntity(second.Id)!).Vertices.Count);
        doc.Redo(); doc.Redo(); doc.Redo(); doc.Redo();
        Assert.Null(doc.Project.FindEntity(first.Id));
        doc.Undo();
        Assert.Equal(moved, Now().Vertices);
        Assert.Equal(triangles.Count - 3, Now().Triangles.Count);

        // a restored surface changed in place is noticed: the snapshot must not take it for the stored mesh
        doc.Execute("Move again", pr => pr.FindEntity(first.Id)!.Translate(new Vec3(0, 5, 0)));
        doc.Undo();
        Assert.Equal(moved, Now().Vertices);
    }

    [Fact]
    public void Document_MeshesInTheUndoHistoryStayWithinTheirLimit()
    {
        var p = BlastProject.CreateNew();
        var s = p.Add(Mesh(40));
        var doc = new Document(p) { MaxMeshBytes = 200_000 };
        for (int i = 0; i < 20; i++) doc.Execute("Move", pr => pr.FindEntity(s.Id)!.Translate(new Vec3(1, 0, 0)));
        // 1600 vertices of 24 bytes each move, the triangles once
        Assert.InRange(doc.UndoNames.Count, 2, 6);
        doc.Undo();
        Assert.Equal(19, ((SurfaceEntity)doc.Project.FindEntity(s.Id)!).Vertices[0].X);
    }

    [Fact]
    public void Document_Capture_IsTheProjectOfThatMomentAndChangesNothing()
    {
        var p = BlastProject.CreateNew();
        var s = p.Add(Mesh(4));
        var h = p.Add(new Hole { Label = "1" });
        p.Attachments.Add(new Attachment { FileName = "a.txt", Data = new byte[] { 1, 2, 3 } });
        var doc = new Document(p);
        doc.Execute("Move", pr => pr.FindEntity(h.Id)!.Translate(new Vec3(1, 0, 0)));
        var live = doc.Project;
        var state = doc.Capture();
        var undoSteps = doc.UndoNames.Count;
        doc.Execute("Later", pr => { pr.FindEntity(s.Id)!.Translate(new Vec3(7, 0, 0)); pr.FindEntity(h.Id)!.Translate(new Vec3(7, 0, 0)); });

        var copy = Task.Run(state).Result;
        Assert.NotSame(live, copy);
        Assert.Equal(1, ((Hole)copy.FindEntity(h.Id)!).Collar.X);
        var surface = (SurfaceEntity)copy.FindEntity(s.Id)!;
        Assert.Equal(0, surface.Vertices[0].X);
        Assert.Equal(s.Triangles.Count, surface.Triangles.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, copy.Attachments[0].Data);
        Assert.Same(live, doc.Project);
        Assert.Equal(undoSteps + 1, doc.UndoNames.Count);
        // it makes a whole file
        using var file = new MemoryStream();
        ProjectFile.Write(copy, file);
        file.Position = 0;
        Assert.Equal(surface.Vertices, ((SurfaceEntity)ProjectFile.Read(file).FindEntity(s.Id)!).Vertices);
    }

    [Fact]
    public void Recovery_AWriteStartedBeforeAClearLeavesNoFile()
    {
        var dir = Directory.CreateTempSubdirectory("recovery").FullName;
        try
        {
            var store = new RecoveryStore(dir);
            var p = BlastProject.CreateNew();
            store.Write(p, null, store.Ticket);
            Assert.Single(store.Pending());
            var ticket = store.Ticket;
            store.Clear(null, p.Id);
            store.Write(p, null, ticket);
            Assert.Empty(Directory.GetFiles(dir));
            store.Write(p, null);
            Assert.Single(store.Pending());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ProjectFile_KeepsTheProductsOnce()
    {
        var json = System.Text.Encoding.UTF8.GetString(ProjectJson.Serialize(BlastProject.CreateNew()));
        Assert.DoesNotContain("activeProducts", json);
    }

    [Fact]
    public void Document_FailedChange_LetsTheListenersSolveTheRestoredProject()
    {
        var p = BlastProject.CreateNew();
        var a = p.Add(new Hole { Label = "1", Collar = new Vec3(10, 20, 0) });
        var b = p.Add(new Hole { Label = "2", Collar = new Vec3(14, 20, 0) });
        p.Add(new InitiationPoint { Position = a.Collar, HoleId = a.Id });
        Timing.Connect(p, a.Id, b.Id, null, 25);
        Timing.Solve(p);
        var doc = new Document(p);
        doc.Select(new[] { a.Id }, record: false);
        doc.Changed += (_, _) => Timing.Solve(doc.Project);

        Assert.Throws<InvalidOperationException>(() => doc.Execute("Fail", pr =>
        {
            pr.Entities.RemoveAll(e => e is Hole);
            throw new InvalidOperationException();
        }));

        Assert.Equal(2, doc.Project.Holes.Count(h => h.FiringTimeMs.HasValue));
        Assert.Equal(25, doc.Project.Holes.First(h => h.Label == "2").FiringTimeMs);
        Assert.Equal(b.Collar, doc.Project.Ties.Single().ToPos);
        Assert.Contains(a.Id, doc.Selection);
        Assert.False(doc.IsModified);
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void EditCopy_OfASurface_SharesTheMeshAndKeepsTheFieldsApart()
    {
        var s = new SurfaceEntity { Name = "Survey", Vertices = { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0) }, Triangles = { 0, 1, 2 } };
        var copy = (SurfaceEntity)s.EditCopy();
        Assert.Same(s.Vertices, copy.Vertices);
        Assert.Equal(1, copy.TriangleCount);
        copy.Name = "Edited";
        Assert.Equal("Survey", s.Name);
        // anything else is a copy of its own
        var line = new PolylineEntity { Points = { new Vec3(0, 0, 0) } };
        Assert.NotSame(line.Points, ((PolylineEntity)line.EditCopy()).Points);
    }

    [Fact]
    public void SafeFile_ReplacesTheFile_AndAFailureLeavesTheOldOne()
    {
        var dir = Directory.CreateTempSubdirectory("safefile").FullName;
        var path = Path.Combine(dir, "settings.json");
        try
        {
            SafeFile.WriteAllText(path, "старое");
            SafeFile.WriteAllText(path, "новое");
            Assert.Equal("новое", File.ReadAllText(path));
            File.SetAttributes(path, FileAttributes.ReadOnly);
            Assert.ThrowsAny<Exception>(() => SafeFile.WriteAllText(path, "третье"));
            Assert.Equal("новое", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Document_UndoRedoRestoresState()
    {
        var doc = new Document(Sample());
        var count = doc.Project.Holes.Count();
        doc.Execute("Delete", p => p.Entities.RemoveAll(e => e is Hole));
        Assert.Empty(doc.Project.Holes);
        doc.Undo();
        Assert.Equal(count, doc.Project.Holes.Count());
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(doc.Project.Attachments[0].Data));
        doc.Redo();
        Assert.Empty(doc.Project.Holes);
        Assert.True(doc.IsModified);
    }

    [Fact]
    public void Merge_RenamesCollidingIdsAndKeepsTies()
    {
        var a = Sample();
        var b = ProjectJson.DeepClone(a);
        var ip = b.Add(new InitiationPoint());
        Timing.Connect(b, ip.Id, b.Holes.First().Id, null);
        var n = ProjectFile.Merge(a, b);
        Assert.True(n > 0);
        Assert.Equal(a.Entities.Count, a.Entities.Select(e => e.Id).Distinct().Count());
        var tie = a.Ties.Single();
        Assert.NotNull(a.FindEntity(tie.ToId));
    }

    [Fact]
    public void TextImport_GuessesColumnsAndImportsHoles()
    {
        var lines = new[] { "Hole,X,Y,Z,Angle,Bearing,Length,Diameter", "1,100,200,50,0,0,11,102", "2,105,200,50,10,90,12,102", "3,105.05,200,50,0,0,11,102" };
        var fmt = TextImportFormat.Guess(lines);
        Assert.Equal(',', fmt.Delimiter);
        Assert.Equal(1, fmt.SkipLines);
        var r = TextFiles.Import(lines, fmt, new DesignData());
        Assert.Equal(2, r.Entities.Count);
        Assert.Equal(1, r.Skipped);
        var h2 = (Hole)r.Entities[1];
        Assert.Equal(10, h2.Angle);
        Assert.Equal(12, h2.Length);
    }

    [Fact]
    public void Exports_ProduceContent()
    {
        var p = Sample();
        Assert.Contains("3DFACE", CadFiles.ExportDxf(p, p.Entities));
        Assert.Contains("Label,CollarX", TextFiles.ExportHoles(p, p.Holes));
        Assert.Contains("Hole,Deck,Top", TextFiles.ExportDeckTable(p, p.Holes));
        Assert.Contains("DRPPlan", CadFiles.ExportDrillPlan(p, p.Holes));
        var str = CadFiles.ExportStr(p.Entities.OfType<PolylineEntity>(), "t");
        var path = Path.GetTempFileName();
        File.WriteAllText(path, str);
        Assert.Single(CadFiles.ImportStr(path, false));
        File.Delete(path);
    }

    [Fact]
    public void DxfExport_ReadsBackWithCadReader()
    {
        var p = Sample();
        var path = Path.Combine(Path.GetTempPath(), $"bd_{Guid.NewGuid():N}.dxf");
        File.WriteAllText(path, CadFiles.ExportDxf(p, p.Entities.Where(e => e is PolylineEntity or SurfaceEntity)));
        try
        {
            var items = CadFiles.ImportCad(path, new CadImportOptions());
            Assert.Contains(items, i => i.Entity is PolylineEntity);
            Assert.Contains(items, i => i.Entity is SurfaceEntity);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reports_WritePdf()
    {
        var p = Sample();
        using var ms = new MemoryStream();
        Reports.WritePdf(p, ms, new ReportOptions());
        Assert.True(ms.Length > 2000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(ms.ToArray(), 0, 4));
        using var ms2 = new MemoryStream();
        Reports.WritePlanPdf(p, ms2, 500, true, "Plan");
        Assert.True(ms2.Length > 1000);
        var png = Reports.RenderPng(p, new Camera { Width = 400, Height = 300 }, 400, 300);
        Assert.True(png.Length > 1000);
    }
}
