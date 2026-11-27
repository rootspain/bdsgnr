using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.Tests;

/// <summary>
/// A copy of a hole of a pattern, pasted or copied, stays in the pattern (the segment) of the one copied as an extra hole on
/// no node (<see cref="HoleDesign.IsExtraHole"/>): layouts leave it where it is, it turns with the pattern, takes its
/// depth at its place, and goes with the pattern when it is dissolved.
/// </summary>
public class PatternExtraHoleTests
{
    private static readonly Vec3[] Rectangle = { new(0, 0, 100), new(41, 0, 100), new(41, 23, 100), new(0, 23, 100) };

    private static Hole H(Document doc, Guid id) => (Hole)doc.Project.FindEntity(id)!;

    /// <summary>As Paste does: the hole through the clipboard, a new id, moved by <paramref name="offset"/>.</summary>
    private static Guid Paste(Document doc, Guid sourceId, Vec3 offset)
    {
        var copy = (Hole)ProjectJson.DeserializeEntities(ProjectJson.SerializeEntities(new Entity[] { H(doc, sourceId) }))[0];
        copy.Id = Guid.NewGuid();
        copy.RowId = null;
        copy.Translate(offset);
        doc.Execute("Paste", pr => pr.Entities.Add(copy));
        return copy.Id;
    }

    [Fact]
    public void APastedCopy_IsAnExtraHoleOfTheSegmentOfTheOneCopied()
    {
        var (p, root) = PatternSegmentTests.Make(Rectangle);
        PatternTree.Split(p, root, 2);
        var leaves = PatternTree.Leaves(p, root);
        var second = leaves[1];
        var source = p.Holes.First(h => h.PatternId == second.Id);
        var doc = new Document(p);
        // put over the other segment: still of the segment of the one copied
        var id = Paste(doc, source.Id, leaves[0].Center - second.Center);
        var copy = H(doc, id);
        Assert.Equal(second.Id, copy.PatternId);
        Assert.True(HoleDesign.IsExtraHole(copy));
        Assert.Null(copy.GridOffset);
        Assert.Null(copy.RowId);
        // the one copied stays on its node
        Assert.NotNull(H(doc, source.Id).GridI);
    }

    [Fact]
    public void ACopyOfMoveCopy_IsAnExtraHoleToo()
    {
        var (p, root) = PatternSegmentTests.Make(Rectangle);
        var doc = new Document(p);
        var source = p.Holes.First();
        Hole? copy = null;
        doc.Execute("Copy entities", pr =>
        {
            copy = (Hole)GeneralOps.Copy(new Entity[] { pr.FindEntity(source.Id)! }, new Vec3(1.5, 1.5, 0), 1).Single();
            pr.Entities.Add(copy);
        });
        Assert.Equal(root.Id, H(doc, copy!.Id).PatternId);
        Assert.True(HoleDesign.IsExtraHole(H(doc, copy.Id)));
    }

    [Fact]
    public void AnExtraHole_OutlivesALayout_WhereItWas_NumberedAfterTheOthers()
    {
        var (p, root) = PatternSegmentTests.Make(Rectangle);
        var doc = new Document(p);
        var count = p.Holes.Count();
        var id = Paste(doc, p.Holes.First().Id, new Vec3(2, 2, 0));
        var at = H(doc, id).Collar;
        doc.Execute("Resize", pr => HoleDesign.ResizePattern(pr, pr.FindPattern(root.Id)!, 4.2, 4.7));
        var extra = H(doc, id);
        Assert.Equal(at.X, extra.Collar.X, 9);
        Assert.Equal(at.Y, extra.Collar.Y, 9);
        Assert.Equal(root.Id, extra.PatternId);
        var nodes = doc.Project.Holes.Count(h => h.PatternId == root.Id && !HoleDesign.IsExtraHole(h));
        Assert.Equal(nodes + 1, doc.Project.Holes.Count(h => h.PatternId == root.Id));
        // the layout numbers the holes on the nodes first, the extra hole after them
        Assert.Equal(nodes + 1, int.Parse(extra.Label));
        Assert.True(count > 0);
    }

    [Fact]
    public void AnExtraHole_TurnsWithThePattern()
    {
        var (p, root) = PatternSegmentTests.Make(Rectangle);
        var doc = new Document(p);
        var id = Paste(doc, p.Holes.First().Id, new Vec3(1, 1, 0));
        var before = H(doc, id).Collar;
        var centre = root.Center;
        doc.Execute("Rotate pattern", pr => HoleDesign.RotatePattern(pr, pr.FindPattern(root.Id)!, 30));
        var after = H(doc, id).Collar;
        Assert.Equal(Vec3.Distance2D(before, centre), Vec3.Distance2D(after, centre), 6);
        Assert.Equal(GeoMath.NormalizeBearing(GeoMath.Bearing(centre, before) + 30), GeoMath.Bearing(centre, after), 6);
        Assert.Equal(root.Id, H(doc, id).PatternId);
    }

    [Fact]
    public void AnExtraHoleOfASegment_TurnsWithTheWholePattern()
    {
        var (p, root) = PatternSegmentTests.Make(Rectangle);
        PatternTree.Split(p, root, 2);
        var seg = PatternTree.Leaves(p, root)[0];
        var doc = new Document(p);
        var id = Paste(doc, p.Holes.First(h => h.PatternId == seg.Id).Id, new Vec3(1, 1, 0));
        var before = H(doc, id).Collar;
        var centre = root.Center;
        doc.Execute("Rotate pattern", pr => PatternTree.Rotate(pr, pr.FindPattern(root.Id)!, -20));
        var after = H(doc, id).Collar;
        Assert.Equal(Vec3.Distance2D(before, centre), Vec3.Distance2D(after, centre), 6);
        Assert.Equal(GeoMath.NormalizeBearing(GeoMath.Bearing(centre, before) - 20), GeoMath.Bearing(centre, after), 6);
        Assert.Equal(seg.Id, H(doc, id).PatternId);
    }

    [Fact]
    public void AnExtraHoleDeleted_LeavesNoNodeEmpty_DissolvingThePatternFreesIt()
    {
        var (p, root) = PatternSegmentTests.Make(Rectangle);
        var doc = new Document(p);
        var a = Paste(doc, p.Holes.First().Id, new Vec3(1, 1, 0));
        var b = Paste(doc, p.Holes.Last().Id, new Vec3(-1, 1, 0));
        doc.Execute("Delete", pr => pr.Entities.RemoveAll(e => e.Id == a));
        Assert.Empty(doc.Project.FindPattern(root.Id)!.Suppressed);
        doc.Execute("Dissolve", pr => HoleDesign.DissolvePattern(pr, pr.FindPattern(root.Id)!));
        Assert.Null(H(doc, b).PatternId);
        Assert.False(HoleDesign.IsExtraHole(H(doc, b)));
    }

    [Fact]
    public void AnExtraHole_TakesTheDepthOfThePatternAtItsPlace_OnALayoutToo()
    {
        var (p, root) = PatternSegmentTests.Make(Rectangle);
        static double Grade(double x) => 85 - 0.1 * x;
        p.Add(new SurfaceEntity
        {
            Name = "Grade", Kind = SurfaceKind.Grade,
            Vertices = { new Vec3(-100, -100, Grade(-100)), new Vec3(200, -100, Grade(200)), new Vec3(200, 100, Grade(200)), new Vec3(-100, 100, Grade(-100)) },
            Triangles = { 0, 1, 2, 0, 2, 3 }
        });
        root.HoleDefaults.Depth.Mode = DepthMode.ToSurface;
        PatternTree.SyncLegacyDepth(root);
        HoleDesign.RebuildPattern(p, root);
        var doc = new Document(p);
        var id = Paste(doc, p.Holes.First().Id, new Vec3(30, 0, 0));
        var h = H(doc, id);
        Assert.Equal(Grade(h.Collar.X), h.ToLevel!.Value, 6);
        doc.Execute("Layout", pr => HoleDesign.RebuildPattern(pr, pr.FindPattern(root.Id)!));
        h = H(doc, id);
        Assert.Equal(Grade(h.Collar.X), h.ToLevel!.Value, 6);
    }

    [Fact]
    public void ACopyOfAHoleOnASurface_StaysOnItAndInThePattern()
    {
        var (p, root) = PatternSegmentTests.Make(Rectangle);
        var bench = new SurfaceEntity
        {
            Name = "Bench", Kind = SurfaceKind.Bench,
            Vertices = { new Vec3(-100, -100, 90), new Vec3(200, -100, 120), new Vec3(200, 100, 120), new Vec3(-100, 100, 90) },
            Triangles = { 0, 1, 2, 0, 2, 3 }
        };
        p.Add(bench);
        var doc = new Document(p);
        doc.Execute("Collars onto surface", pr => CollarSurfaces.Drape(pr, pr.Holes.ToList(), (SurfaceEntity)pr.FindEntity(bench.Id)!, false));
        var id = Paste(doc, doc.Project.Holes.First().Id, new Vec3(10, 1, 0));
        var copy = H(doc, id);
        Assert.Equal(root.Id, copy.PatternId);
        Assert.Equal(bench.Id, copy.CollarSurfaceId);
        Assert.Equal(bench.ElevationAt(copy.Collar.X, copy.Collar.Y)!.Value, copy.Collar.Z, 9);
    }
}
