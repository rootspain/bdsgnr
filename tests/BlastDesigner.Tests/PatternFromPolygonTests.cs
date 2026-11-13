using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>A polygon made into a pattern: the first row along a side, the holes of a row aligned, the grid keeping with the polygon.</summary>
public class PatternFromPolygonTests
{
    /// <summary>A rectangle 21 × 10 m from (0, 0), anticlockwise: side 0 is the south side, the longest.</summary>
    private static readonly Vec3[] Rect = { new(0, 0, 100), new(21, 0, 100), new(21, 10, 100), new(0, 10, 100) };

    private static (BlastProject P, PolylineEntity Polygon, HolePattern Pattern) Made(Vec3[] polygon, int? edge, RowAlignment align = RowAlignment.Left, double burden = 4, double spacing = 4)
    {
        var p = BlastProject.CreateNew("pp");
        var poly = p.Add(new PolylineEntity { Closed = true, Points = polygon.ToList() });
        var pp = new PatternParameters { Burden = burden, Spacing = spacing, Pattern = PatternType.Rectangular, FaceEdge = edge, Alignment = align };
        var holes = HoleDesign.FillPolygon(poly.Points, pp, new HoleParameters(), out var rows, out var pat);
        foreach (var h in holes) p.Add(h);
        p.Rows.AddRange(rows);
        pat.OutlineEntityId = poly.Id;
        p.Patterns.Add(pat);
        return (p, poly, pat);
    }

    private static List<Hole> Row0(BlastProject p, HolePattern pat) => HoleDesign.PatternHoles(p, pat).Where(h => h.GridJ == 0).ToList();

    [Fact]
    public void TheLongestSideIsFound_AndTheBlastGoesOutAcrossIt()
    {
        Assert.Equal(0, HoleDesign.LongestEdge(Rect));
        Assert.Equal(180, HoleDesign.OutwardBearing(Rect, 0), 9);
        Assert.Equal(90, HoleDesign.OutwardBearing(Rect, 1), 9);
        // drawn the other way round the outside is the same
        var clockwise = Rect.Reverse().ToArray();
        Assert.Equal(180, HoleDesign.OutwardBearing(clockwise, 2), 9);
    }

    [Fact]
    public void TheFirstRowRunsHalfABurdenInsideTheSide_EvenOnASlantedSide()
    {
        // a quadrilateral whose long side is slanted
        var quad = new Vec3[] { new(0, 0, 0), new(30, 10, 0), new(28, 25, 0), new(-2, 18, 0) };
        var edge = HoleDesign.LongestEdge(quad);
        var (p, _, pat) = Made(quad, edge, burden: 5, spacing: 4);
        var row = Row0(p, pat);
        Assert.NotEmpty(row);
        foreach (var h in row)
            Assert.Equal(2.5, GeoMath.DistanceToPolyline2D(new[] { quad[edge], quad[(edge + 1) % 4] }, h.Collar, false), 6);
        Assert.Equal(HoleDesign.OutwardBearing(quad, edge), pat.Azimuth, 9);
        Assert.Equal(edge, pat.FaceEdge);
    }

    [Theory]
    [InlineData(RowAlignment.Left, 3, 19)]
    [InlineData(RowAlignment.Centre, 2.5, 18.5)]
    [InlineData(RowAlignment.Right, 2, 18)]
    public void TheHolesOfARowAreAlignedLeftCentreOrRight(RowAlignment align, double minX, double maxX)
    {
        // blasting south across the south side: looking along it the left end is the east one (x = 21)
        var (p, _, pat) = Made(Rect, 0, align);
        var row = Row0(p, pat);
        Assert.Equal(5, row.Count);
        Assert.All(row, h => Assert.Equal(2, h.Collar.Y, 6));
        Assert.Equal(minX, row.Min(h => h.Collar.X), 6);
        Assert.Equal(maxX, row.Max(h => h.Collar.X), 6);
    }

    [Fact]
    public void WithNoSideThePatternIsLaidAsBefore()
    {
        var p = BlastProject.CreateNew("pp");
        var pp = new PatternParameters { Burden = 4, Spacing = 4, Pattern = PatternType.Rectangular, Orientation = 180 };
        var pat = HoleDesign.CreatePattern(Rect, pp, new HoleParameters());
        Assert.Null(pat.FaceEdge);
        Assert.Equal(RowAlignment.Left, pat.Alignment);
        var copy = ProjectJson.DeepClone(pat);
        HoleDesign.Anchor(copy);
        Assert.Equal(pat.PhaseU, copy.PhaseU, 9);
        Assert.Equal(pat.PhaseV, copy.PhaseV, 9);
    }

    [Fact]
    public void AnEditedPolygonTakesItsPatternAlong_InOneUndoStep()
    {
        var (p, poly, pat) = Made(Rect, 0);
        var doc = new Document(p);
        var before = HoleDesign.PatternHoles(p, pat).Count();
        // the north side moved 8 m further: two more rows
        doc.Execute("Move vertex", pr =>
        {
            var line = (PolylineEntity)pr.FindEntity(poly.Id)!;
            line.Points[2] = new Vec3(21, 18, 100);
            line.Points[3] = new Vec3(0, 18, 100);
        });
        var x = doc.Project.FindPattern(pat.Id)!;
        Assert.Equal(before + 10, HoleDesign.PatternHoles(doc.Project, x).Count());
        // the first row stays along the south side
        Assert.All(Row0(doc.Project, x), h => Assert.Equal(2, h.Collar.Y, 6));
        doc.Undo();
        Assert.Equal(before, HoleDesign.PatternHoles(doc.Project, doc.Project.FindPattern(pat.Id)!).Count());
    }

    [Fact]
    public void ATurnedPolygonTurnsTheGridWithItsSide()
    {
        var (p, poly, pat) = Made(Rect, 0);
        var doc = new Document(p);
        // turned 90° anticlockwise about (0, 0): the south side now goes north, on the west
        doc.Execute("Rotate", pr => pr.FindEntity(poly.Id)!.Transform(v => new Vec3(-v.Y, v.X, v.Z)));
        var x = doc.Project.FindPattern(pat.Id)!;
        Assert.Equal(90, x.Azimuth, 6);
        Assert.All(Row0(doc.Project, x), h => Assert.Equal(-2, h.Collar.X, 6));
    }

    [Fact]
    public void ADrilledHoleKeepsThePatternWhereItIs_AndSaysWhy()
    {
        var (p, poly, pat) = Made(Rect, 0);
        HoleDesign.PatternHoles(p, pat).First().State = HoleState.Drilled;
        var doc = new Document(p);
        var collars = HoleDesign.PatternHoles(p, pat).Select(h => h.Collar).ToList();
        PatternRefusal? told = null;
        PatternOutlines.Refused = (_, r) => told = r;
        try
        {
            doc.Execute("Move vertex", pr => ((PolylineEntity)pr.FindEntity(poly.Id)!).Points[2] = new Vec3(30, 10, 100));
        }
        finally { PatternOutlines.Refused = null; }
        Assert.NotNull(told);
        Assert.Equal(collars, HoleDesign.PatternHoles(doc.Project, doc.Project.FindPattern(pat.Id)!).Select(h => h.Collar).ToList());
        // the polygon is edited all the same
        Assert.Equal(new Vec3(30, 10, 100), ((PolylineEntity)doc.Project.FindEntity(poly.Id)!).Points[2]);
    }

    [Fact]
    public void AGridTurnedByHandNoLongerFollowsTheSide()
    {
        var (p, _, pat) = Made(Rect, 0);
        HoleDesign.RotatePattern(p, pat, 15);
        Assert.Null(pat.FaceEdge);
    }
}
