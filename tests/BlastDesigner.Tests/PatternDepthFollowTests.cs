using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.Tests;

/// <summary>
/// A hole of a pattern drilled to the surface or to the depth polygon, moved or copied and pasted elsewhere: its level is
/// the one of its new place, and its length follows.
/// </summary>
public class PatternDepthFollowTests
{
    private static readonly Vec3[] Rectangle = { new(0, 0, 100), new(41, 0, 100), new(41, 23, 100), new(0, 23, 100) };

    /// <summary>A grade sloping down to the east: 85 at x = 0, a metre lower every 10 m.</summary>
    private static double Grade(double x) => 85 - 0.1 * x;

    private static (BlastProject P, HolePattern Pat) Make()
    {
        var p = BlastProject.CreateNew();
        var holes = HoleDesign.FillPolygon(Rectangle, new PatternParameters { Burden = 4, Spacing = 4.5, Pattern = PatternType.Rectangular, StartNumber = 1 },
            new HoleParameters { BenchHeight = 10, Subdrill = 1 }, out var rows, out var pat);
        var layer = p.GetOrCreateLayer(StandardLayers.Holes);
        foreach (var h in holes) p.Add(h, layer);
        p.Rows.AddRange(rows);
        p.Patterns.Add(pat);
        return (p, pat);
    }

    private static (BlastProject P, HolePattern Pat) ToSurface()
    {
        var (p, pat) = Make();
        p.Add(new SurfaceEntity
        {
            Name = "Grade", Kind = SurfaceKind.Grade,
            Vertices = { new Vec3(-100, -100, Grade(-100)), new Vec3(200, -100, Grade(200)), new Vec3(200, 100, Grade(200)), new Vec3(-100, 100, Grade(-100)) },
            Triangles = { 0, 1, 2, 0, 2, 3 }
        });
        pat.HoleDefaults.Depth.Mode = DepthMode.ToSurface;
        PatternTree.SyncLegacyDepth(pat);
        HoleDesign.RebuildPattern(p, pat);
        Assert.All(p.Holes, h => Assert.Equal(Grade(h.Collar.X), h.ToLevel!.Value, 6));
        return (p, pat);
    }

    private static void AssertDownTo(Hole h, double level)
    {
        Assert.Equal(level, h.ToLevel!.Value, 5);
        Assert.Equal(h.Collar.Z - level + h.Subdrill, h.Length, 5);
    }

    [Fact]
    public void AHoleMovedOnAPatternToTheSurface_GoesDownToTheSurfaceWhereItIsNow()
    {
        var (p, _) = ToSurface();
        var doc = new Document(p);
        var id = p.Holes.First().Id;
        doc.Execute("Move", pr => { var h = (Hole)pr.FindEntity(id)!; h.Collar += new Vec3(30, 0, 0); });
        var moved = (Hole)doc.Project.FindEntity(id)!;
        AssertDownTo(moved, Grade(moved.Collar.X));
        Assert.False(moved.DepthManual);
        // the others stay where they were
        Assert.All(doc.Project.Holes.Where(h => h.Id != id), h => AssertDownTo(h, Grade(h.Collar.X)));
    }

    [Fact]
    public void ACopyPasted_GoesDownToTheSurfaceWhereItIsPut()
    {
        var (p, pat) = ToSurface();
        var doc = new Document(p);
        var source = p.Holes.First();
        // as Paste does: the hole through the clipboard, a new id, put 60 m east
        var copy = (Hole)ProjectJson.DeserializeEntities(ProjectJson.SerializeEntities(new Entity[] { source }))[0];
        copy.Id = Guid.NewGuid();
        copy.Translate(new Vec3(60, 0, 0));
        doc.Execute("Paste", pr => pr.Entities.Add(copy));
        var pasted = (Hole)doc.Project.FindEntity(copy.Id)!;
        // an extra hole of the pattern of the one copied, on no node
        Assert.Equal(pat.Id, pasted.PatternId);
        Assert.True(HoleDesign.IsExtraHole(pasted));
        AssertDownTo(pasted, Grade(pasted.Collar.X));
        AssertDownTo((Hole)doc.Project.FindEntity(source.Id)!, Grade(source.Collar.X));
    }

    private static double Collar(double x, double y) => 100 + 0.1 * x + 0.05 * y;
    private static double Depth(double x, double y) => 90 - 0.02 * x + 0.04 * y;

    /// <summary>A pattern on the collar polygon, drilled down to the depth polygon, both planes.</summary>
    private static (BlastProject P, HolePattern Pat) Polygons()
    {
        var (p, root) = Make();
        for (int i = 0; i < root.Outline.Count; i++)
            PatternSurfaces.SetVertex(root, $"O:{i}", Collar(root.Outline[i].X, root.Outline[i].Y), Depth(root.Outline[i].X, root.Outline[i].Y));
        root.HoleDefaults.Depth.Collars = CollarSource.Polygon;
        root.HoleDefaults.Depth.Mode = DepthMode.Polygon;
        PatternTree.SyncLegacyDepth(root);
        HoleDesign.RebuildPattern(p, root);
        return (p, root);
    }

    [Fact]
    public void AHoleMovedOnAPatternToTheDepthPolygon_TakesThePolygonsWhereItIsNow()
    {
        var (p, _) = Polygons();
        var doc = new Document(p);
        var id = p.Holes.First().Id;
        doc.Execute("Move", pr => { var h = (Hole)pr.FindEntity(id)!; h.Collar += new Vec3(2, 1.5, 0); });
        var moved = (Hole)doc.Project.FindEntity(id)!;
        Assert.Equal(Collar(moved.Collar.X, moved.Collar.Y), moved.Collar.Z, 6);
        AssertDownTo(moved, Depth(moved.Collar.X, moved.Collar.Y));
    }

    [Fact]
    public void AHoleWithItsDepthSetByHand_KeepsItWhenMoved()
    {
        var (p, _) = Polygons();
        var doc = new Document(p);
        var id = p.Holes.First().Id;
        doc.Execute("Level", pr => ((Hole)pr.FindEntity(id)!).ToLevel = 80);
        Assert.True(((Hole)doc.Project.FindEntity(id)!).DepthManual);
        var z = ((Hole)doc.Project.FindEntity(id)!).Collar.Z;
        doc.Execute("Move", pr => { var h = (Hole)pr.FindEntity(id)!; h.Collar += new Vec3(2, 1.5, 0); });
        var moved = (Hole)doc.Project.FindEntity(id)!;
        Assert.Equal(z, moved.Collar.Z);
        AssertDownTo(moved, 80);
    }
}
