using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.Tests;

/// <summary>Holes drilled down to a level: the length follows the collar, the angle and the level.</summary>
public class HoleLevelTests
{
    [Fact]
    public void TheLengthGoesFromTheCollarDownToTheLevel_AndFollowsTheHole()
    {
        var p = BlastProject.CreateNew();
        var h = p.Add(new Hole { Label = "1", Collar = new Vec3(0, 0, 100), Length = 10 });
        Charging.ApplyRules(p, new[] { h });
        var doc = new Document(p);
        Hole H() => (Hole)doc.Project.FindEntity(h.Id)!;

        doc.Execute("To level", pr => ((Hole)pr.FindEntity(h.Id)!).ToLevel = 85);
        Assert.Equal(15, H().Length, 9);
        // loaded by its rules: the column is loaded again for the new length
        Assert.Equal(15, H().Decks.Max(d => d.Bottom), 6);
        // inclined: along the hole
        doc.Execute("Angle", pr => ((Hole)pr.FindEntity(h.Id)!).Angle = 30);
        Assert.Equal(15 / Math.Cos(Math.PI / 6), H().Length, 9);
        // the collar goes up: the hole gets longer
        doc.Execute("Move", pr => pr.FindEntity(h.Id)!.Translate(new Vec3(3, 0, 2)));
        Assert.Equal(17 / Math.Cos(Math.PI / 6), H().Length, 9);
        // undo takes the length back with the move
        doc.Undo();
        Assert.Equal(15 / Math.Cos(Math.PI / 6), H().Length, 9);

        // the subdrill goes on past the level, whatever the bearing: the grade point is on the level, the toe below it
        doc.Execute("Subdrill", pr => { var x = (Hole)pr.FindEntity(h.Id)!; x.Bearing = 45; x.Subdrill = 1.5; });
        Assert.Equal(15 / Math.Cos(Math.PI / 6) + 1.5, H().Length, 9);
        Assert.Equal(85, H().GradePoint.Z, 9);
        Assert.True(H().Toe.Z < 85);
        // an underdrill (a negative subdrill) stops short of the level
        doc.Execute("Subdrill", pr => ((Hole)pr.FindEntity(h.Id)!).Subdrill = -1);
        Assert.Equal(15 / Math.Cos(Math.PI / 6) - 1, H().Length, 9);
        Assert.Equal(85 + Math.Cos(Math.PI / 6), H().Toe.Z, 9);
        doc.Execute("Subdrill", pr => { var x = (Hole)pr.FindEntity(h.Id)!; x.Bearing = 0; x.Subdrill = 0; });

        // empty or 0: no level, the length stays as it is and is edited by hand again
        doc.Execute("To level", pr => ((Hole)pr.FindEntity(h.Id)!).ToLevel = 0);
        doc.Execute("Length", pr => ((Hole)pr.FindEntity(h.Id)!).Length = 12);
        Assert.Equal(12, H().Length);
        Assert.Null(HoleLevel.Of(H()));

        // a collar not above the level: no length from it
        doc.Execute("To level", pr => ((Hole)pr.FindEntity(h.Id)!).ToLevel = 101);
        Assert.Equal(12, H().Length);
        Assert.False(HoleLevel.Reaches(H(), 101));
        Assert.Null(HoleLevel.Length(H()));
        Assert.False(HoleLevel.Reaches(H(), H().Collar.Z));

        // kept in the file
        H().ToLevel = 90;
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(doc.Project));
        Assert.Equal(90, ((Hole)back.FindEntity(h.Id)!).ToLevel);
        Assert.Null(ProjectJson.Deserialize(ProjectJson.Serialize(BlastProject.CreateNew())).Holes.FirstOrDefault()?.ToLevel);
    }

    /// <summary>A flat square surface 100 m across at an elevation.</summary>
    private static SurfaceEntity Flat(double z) => new()
    {
        Vertices = { new Vec3(-50, -50, z), new Vec3(50, -50, z), new Vec3(50, 50, z), new Vec3(-50, 50, z) },
        Triangles = { 0, 1, 2, 0, 2, 3 }
    };

    [Fact]
    public void AHoleGoesDownToTheFirstSurfaceItsLineMeets()
    {
        var p = BlastProject.CreateNew();
        var h = p.Add(new Hole { Label = "1", Collar = new Vec3(3, 4, 100), Length = 5 });
        Assert.Null(HoleLevel.SurfaceDistance(p, h));
        // the ground the collar stands on, the floor below it and a deeper one: the floor, past the toe
        p.Add(Flat(100));
        p.Add(Flat(80));
        p.Add(Flat(88));
        Assert.Equal(12, HoleLevel.SurfaceDistance(p, h)!.Value, 9);
        // inclined: along the hole
        h.Angle = 30;
        h.Bearing = 120;
        Assert.Equal(12 / Math.Cos(Math.PI / 6), HoleLevel.SurfaceDistance(p, h)!.Value, 9);
        // the subdrill goes on past it, an underdrill stops short
        h.Subdrill = 1;
        Assert.Equal(12 / Math.Cos(Math.PI / 6) + 1, HoleLevel.LengthToSurface(p, h)!.Value, 9);
        h.Subdrill = -1;
        Assert.Equal(12 / Math.Cos(Math.PI / 6) - 1, HoleLevel.LengthToSurface(p, h)!.Value, 9);
        // a hole off the surfaces meets none
        h.Collar = new Vec3(500, 500, 100);
        Assert.Null(HoleLevel.LengthToSurface(p, h));
        Assert.Null(HoleLevel.SurfaceLevel(p, h));
    }

    [Fact]
    public void EachHoleTakesTheLevelOfTheSurfaceItsOwnLineMeets()
    {
        var p = BlastProject.CreateNew();
        // two floors side by side at different elevations, a hole over each
        p.Add(new SurfaceEntity { Vertices = { new(-10, -10, 85), new(0, -10, 85), new(0, 10, 85), new(-10, 10, 85) }, Triangles = { 0, 1, 2, 0, 2, 3 } });
        p.Add(new SurfaceEntity { Vertices = { new(0, -10, 91), new(10, -10, 91), new(10, 10, 91), new(0, 10, 91) }, Triangles = { 0, 1, 2, 0, 2, 3 } });
        var a = p.Add(new Hole { Label = "a", Collar = new Vec3(-5, 0, 100), Length = 5 });
        var b = p.Add(new Hole { Label = "b", Collar = new Vec3(5, 0, 100), Length = 5, Subdrill = 1 });
        Assert.Equal(85, HoleLevel.SurfaceLevel(p, a)!.Value, 9);
        Assert.Equal(91, HoleLevel.SurfaceLevel(p, b)!.Value, 9);
        // inclined towards the other floor: the level where its own line meets one
        b.Angle = 20; b.Bearing = 270;
        var z = HoleLevel.SurfaceLevel(p, b)!.Value;
        var at = b.Collar + b.Direction * HoleLevel.SurfaceDistance(p, b)!.Value;
        Assert.Equal(at.X >= 0 ? 91 : 85, z, 9);
        // the level put in To level gives the same length as the way to the surface, with the subdrill
        b.ToLevel = z;
        Assert.Equal(HoleLevel.LengthToSurface(p, b)!.Value, HoleLevel.Length(b)!.Value, 9);
    }

    [Fact]
    public void TheLastIntervalGoesDownToTheToeWhateverChangesTheLength()
    {
        var p = BlastProject.CreateNew();
        var stem = p.Products.First(x => x.Kind == ProductKind.Stemming).Id;
        var anfo = p.Products.First(x => x.IsExplosive).Id;
        var h = p.Add(new Hole { Label = "2", Collar = new Vec3(0, 0, 100), Length = 11, ManualLoading = true });
        h.Decks.Add(new Deck { Top = 0, Bottom = 3, ProductId = stem });
        h.Decks.Add(new Deck { Top = 3, Bottom = 10, ProductId = anfo, Primers = { new Primer { Depth = 6.4 } } });
        h.Decks.Add(new Deck { Top = 10, Bottom = 11, ProductId = anfo, Primers = { new Primer { Depth = 10.9 } } });
        var doc = new Document(p);
        Hole H() => (Hole)doc.Project.FindEntity(h.Id)!;

        // longer by hand: the last interval to the toe, its primer 0.1 m above it still
        doc.Execute("Length", pr => ((Hole)pr.FindEntity(h.Id)!).Length = 15);
        Assert.Equal(new[] { 3.0, 10, 15 }, H().Decks.Select(d => d.Bottom));
        Assert.Equal(14.9, H().Decks[2].Primers[0].Depth, 9);
        Assert.Equal(6.4, H().Decks[1].Primers[0].Depth, 9);
        // by a level, and by a move of the collar
        doc.Execute("To level", pr => ((Hole)pr.FindEntity(h.Id)!).ToLevel = 86);
        Assert.Equal(14, H().Decks[^1].Bottom, 9);
        doc.Execute("Move", pr => pr.FindEntity(h.Id)!.Translate(new Vec3(0, 0, 1)));
        Assert.Equal(15, H().Decks[^1].Bottom, 9);
        // shorter than the intervals: those below the toe go, the one it cuts ends there
        doc.Execute("To level", pr => ((Hole)pr.FindEntity(h.Id)!).ToLevel = 93);
        Assert.Equal(new[] { 3.0, 8 }, H().Decks.Select(d => d.Bottom));
        Assert.Equal(6.4, H().Decks[1].Primers[0].Depth, 9);
        // undo brings the column back with the length
        doc.Undo();
        Assert.Equal(3, H().Decks.Count);
        // a change of something else leaves the column alone
        var column = H().Decks.Select(d => (d.Top, d.Bottom)).ToList();
        doc.Execute("Diameter", pr => ((Hole)pr.FindEntity(h.Id)!).Diameter = 115);
        Assert.Equal(column, H().Decks.Select(d => (d.Top, d.Bottom)));
    }
}
