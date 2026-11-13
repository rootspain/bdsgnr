using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>A surface built from points, lines and polygons keeps with them, and they with it.</summary>
public class SurfaceSourceTests
{
    /// <summary>A polygon at 0 and a point at 5 in its middle, and the surface built from both.</summary>
    private static (Document Doc, Guid Polygon, Guid Point, Guid Surface) Built(bool breaklines = false)
    {
        var p = BlastProject.CreateNew("sources");
        var polygon = p.Add(new PolylineEntity { Closed = true, Points = { new(0, 0, 0), new(20, 0, 0), new(20, 20, 0), new(0, 20, 0) } });
        var point = p.Add(new PointEntity { Position = new Vec3(10, 10, 5) });
        var s = SurfaceSources.Build(new Entity[] { polygon, point }, breaklines)!;
        p.Add(s);
        return (new Document(p), polygon.Id, point.Id, s.Id);
    }

    private static SurfaceEntity Surface(Document doc, Guid id) => (SurfaceEntity)doc.Project.FindEntity(id)!;
    private static T Get<T>(Document doc, Guid id) where T : Entity => (T)doc.Project.FindEntity(id)!;

    [Fact]
    public void AMovedPolygonTakesItsSurfaceAlong_InOneUndoStep()
    {
        var (doc, polygon, point, surface) = Built();
        Assert.Equal(new[] { polygon, point }, Surface(doc, surface).SourceIds);
        doc.Execute("Move", pr => pr.FindEntity(polygon)!.Translate(new Vec3(100, 0, 0)));
        var s = Surface(doc, surface);
        Assert.Contains(new Vec3(120, 20, 0), s.Vertices);
        Assert.DoesNotContain(new Vec3(0, 0, 0), s.Vertices);
        // the point stayed: the surface is built from both again
        Assert.Contains(new Vec3(10, 10, 5), s.Vertices);
        Assert.Equal(new[] { polygon, point }, s.SourceIds);
        doc.Undo();
        Assert.Contains(new Vec3(0, 0, 0), Surface(doc, surface).Vertices);
    }

    [Fact]
    public void ANewZOfAVertexBuildsTheSurfaceAgain()
    {
        var (doc, polygon, _, surface) = Built();
        doc.Execute("Z", pr => ((PolylineEntity)pr.FindEntity(polygon)!).Points[2] = new Vec3(20, 20, 7));
        Assert.Contains(new Vec3(20, 20, 7), Surface(doc, surface).Vertices);
    }

    [Fact]
    public void AMovedSurfaceTakesItsObjectsAlong()
    {
        var (doc, polygon, point, surface) = Built();
        var d = new Vec3(3, -4, 2);
        doc.Execute("Move", pr => pr.FindEntity(surface)!.Translate(d));
        Assert.Equal(new Vec3(3, -4, 2), Get<PolylineEntity>(doc, polygon).Points[0]);
        Assert.Equal(new Vec3(13, 6, 7), Get<PointEntity>(doc, point).Position);
        Assert.Equal(new[] { polygon, point }, Surface(doc, surface).SourceIds);
    }

    [Fact]
    public void MovedTogetherTheyMoveOnce()
    {
        var (doc, polygon, point, surface) = Built();
        var d = new Vec3(5, 5, 0);
        doc.Execute("Move", pr => { foreach (var id in new[] { polygon, point, surface }) pr.FindEntity(id)!.Translate(d); });
        Assert.Equal(new Vec3(5, 5, 0), Get<PolylineEntity>(doc, polygon).Points[0]);
        Assert.Equal(new Vec3(15, 15, 5), Get<PointEntity>(doc, point).Position);
        Assert.Contains(new Vec3(25, 25, 0), Surface(doc, surface).Vertices);
    }

    [Fact]
    public void ATurnedSurfaceTurnsItsObjects()
    {
        var (doc, polygon, _, surface) = Built(breaklines: true);
        // a quarter turn about the origin
        doc.Execute("Rotate", pr => pr.FindEntity(surface)!.Transform(v => new Vec3(-v.Y, v.X, v.Z)));
        var pts = Get<PolylineEntity>(doc, polygon).Points;
        Assert.Equal(new Vec3(0, 20, 0), pts[1]);
        Assert.Equal(new Vec3(-20, 20, 0), pts[2]);
    }

    [Fact]
    public void ADeletedObjectLeavesTheSurfaceOfTheOthers_AndWithTooFewPointsTheSurfaceGoes()
    {
        var (doc, polygon, point, surface) = Built();
        doc.Execute("Delete", pr => pr.Entities.Remove(pr.FindEntity(point)!));
        var s = Surface(doc, surface);
        Assert.Equal(new[] { polygon }, s.SourceIds);
        Assert.DoesNotContain(new Vec3(10, 10, 5), s.Vertices);
        Assert.Equal(2, s.TriangleCount);

        var p = BlastProject.CreateNew("few");
        var line = p.Add(new PolylineEntity { Points = { new(0, 0, 0), new(10, 0, 0) } });
        var top = p.Add(new PointEntity { Position = new Vec3(5, 5, 1) });
        var few = SurfaceSources.Build(new Entity[] { line, top }, false)!;
        p.Add(few);
        var d2 = new Document(p);
        d2.Execute("Delete", pr => pr.Entities.Remove(pr.FindEntity(top.Id)!));
        Assert.Null(d2.Project.FindEntity(few.Id));
        d2.Undo();
        Assert.NotNull(d2.Project.FindEntity(few.Id));
    }

    [Fact]
    public void HolesAndSurfacesAreNotBuiltFrom()
    {
        var (doc, polygon, point, surface) = Built();
        var hole = new Hole { Collar = new Vec3(5, 5, 9), Length = 10 };
        var s = SurfaceSources.Build(new Entity[] { Get<PolylineEntity>(doc, polygon), hole, Surface(doc, surface) }, false)!;
        Assert.Equal(new[] { polygon }, s.SourceIds);
        Assert.DoesNotContain(new Vec3(5, 5, 9), s.Vertices);
        Assert.Null(SurfaceSources.Build(new Entity[] { hole }, false));
    }

    [Fact]
    public void ACopyIsBoundToCopiedObjectsOnly()
    {
        var (doc, polygon, point, surface) = Built();
        var alone = (SurfaceEntity)GeneralOps.Copy(new Entity[] { Surface(doc, surface) }, new Vec3(50, 0, 0), 1).Single();
        Assert.Empty(alone.SourceIds);
        var all = GeneralOps.Copy(new Entity[] { Get<PolylineEntity>(doc, polygon), Get<PointEntity>(doc, point), Surface(doc, surface) }, new Vec3(50, 0, 0), 1);
        var copy = all.OfType<SurfaceEntity>().Single();
        Assert.Equal(all.Where(e => e is not SurfaceEntity).Select(e => e.Id), copy.SourceIds);
        // and kept in the file
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(doc.Project));
        Assert.Equal(new[] { polygon, point }, ((SurfaceEntity)back.FindEntity(surface)!).SourceIds);
    }

    [Fact]
    public void ACommandThatReshapesTheSurfaceUnbindsIt()
    {
        var (doc, polygon, _, surface) = Built();
        doc.Execute("Cut", pr => { var s = (SurfaceEntity)pr.FindEntity(surface)!; s.Triangles = s.Triangles.Take(3).ToList(); s.Vertices = s.Vertices.ToList(); s.Vertices[0] += new Vec3(0, 0, 1); });
        Assert.Empty(Surface(doc, surface).SourceIds);
        // the polygon stayed where it was
        Assert.Equal(new Vec3(0, 0, 0), Get<PolylineEntity>(doc, polygon).Points[0]);
    }
}
