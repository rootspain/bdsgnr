using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.Tests;

/// <summary>
/// The surface the collar of a hole sits on (<see cref="Hole.CollarSurfaceId"/>): bound by the Collars onto surface button,
/// followed when the hole is moved, copied or pasted, taken off by an elevation typed in; and the On the surface item of the
/// Collars list of a pattern (<see cref="DepthSpec.CollarSurfaceId"/>).
/// </summary>
public class CollarSurfaceTests
{
    /// <summary>A bench sloping up to the east: 100 at x = 0, a metre higher every 10 m, from x −100 to 200.</summary>
    private static double Bench(double x) => 100 + 0.1 * x;

    private static SurfaceEntity Sloping(string name = "Bench") => new()
    {
        Name = name, Kind = SurfaceKind.Bench,
        Vertices = { new Vec3(-100, -100, Bench(-100)), new Vec3(200, -100, Bench(200)), new Vec3(200, 100, Bench(200)), new Vec3(-100, 100, Bench(-100)) },
        Triangles = { 0, 1, 2, 0, 2, 3 }
    };

    private static Hole NewHole(double x, double y, double z = 90, double angle = 0, double bearing = 0, double length = 12) =>
        new() { Label = "1", Collar = new Vec3(x, y, z), Angle = angle, Bearing = bearing, Length = length };

    /// <summary>A project with the sloping bench and the holes, bound to it by the button (the toes not kept).</summary>
    private static (Document Doc, SurfaceEntity Surface) Bound(params Hole[] holes)
    {
        var p = BlastProject.CreateNew();
        var s = Sloping();
        p.Add(s);
        var layer = p.GetOrCreateLayer(StandardLayers.Holes);
        foreach (var h in holes) p.Add(h, layer);
        var doc = new Document(p);
        doc.Execute("Collars onto surface", pr => CollarSurfaces.Drape(pr, pr.Holes.ToList(), (SurfaceEntity)pr.FindEntity(s.Id)!, keepToe: false));
        return (doc, s);
    }

    private static Hole Get(Document doc, Guid id) => (Hole)doc.Project.FindEntity(id)!;

    [Fact]
    public void TheButton_PutsTheCollarsOnTheSurfaceAndBindsThem()
    {
        var a = NewHole(10, 0);
        var b = NewHole(300, 0); // off the surface
        var (doc, s) = Bound(a, b);
        var on = Get(doc, a.Id);
        Assert.Equal(Bench(10), on.Collar.Z, 9);
        Assert.Equal(s.Id, on.CollarSurfaceId);
        Assert.Equal(12, on.Length, 9);
        var off = Get(doc, b.Id);
        Assert.Equal(90, off.Collar.Z);
        Assert.Null(off.CollarSurfaceId);
    }

    [Fact]
    public void TheButtonKeepingTheToes_BindsTheHolesAndChangesTheirLengths()
    {
        var p = BlastProject.CreateNew();
        var s = Sloping();
        p.Add(s);
        p.Add(NewHole(10, 0), p.GetOrCreateLayer(StandardLayers.Holes));
        var doc = new Document(p);
        var report = doc.Execute("Collars onto surface", pr => CollarSurfaces.Drape(pr, pr.Holes.ToList(), (SurfaceEntity)pr.FindEntity(s.Id)!, keepToe: true));
        var h = doc.Project.Holes.Single();
        Assert.Equal(1, report.Seated);
        Assert.Equal(0, report.Outside);
        Assert.Equal(s.Id, h.CollarSurfaceId);
        Assert.Equal(78, h.Toe.Z, 9);
        Assert.Equal(Bench(10) - 78, h.Length, 9);
    }

    [Fact]
    public void AnAngledHoleMoved_GoesOntoTheSurfaceStraightDown_AngleBearingLengthKept()
    {
        var a = NewHole(10, 0, angle: 20, bearing: 45, length: 15);
        var (doc, _) = Bound(a);
        doc.Execute("Move", pr => Get(doc, a.Id).Translate(new Vec3(30, 5, 0)));
        var h = Get(doc, a.Id);
        Assert.Equal(40, h.Collar.X, 9);
        Assert.Equal(5, h.Collar.Y, 9);
        Assert.Equal(Bench(40), h.Collar.Z, 9);
        Assert.Equal(20, h.Angle, 9);
        Assert.Equal(45, h.Bearing, 9);
        Assert.Equal(15, h.Length, 9);
        Assert.NotNull(h.CollarSurfaceId);
    }

    [Fact]
    public void AHoleToALevelMoved_TakesItsLengthFromTheLevelAtItsNewCollar()
    {
        var a = NewHole(10, 0, angle: 10, bearing: 90);
        a.ToLevel = 80;
        a.Subdrill = 1;
        var (doc, _) = Bound(a);
        doc.Execute("Move", pr => Get(doc, a.Id).Translate(new Vec3(50, 0, 0)));
        var h = Get(doc, a.Id);
        Assert.Equal(Bench(60), h.Collar.Z, 9);
        Assert.Equal((Bench(60) - 80) / Math.Cos(10 * Math.PI / 180) + 1, h.Length, 6);
    }

    [Fact]
    public void AChangeOfTheAngle_LeavesTheCollar()
    {
        var a = NewHole(10, 0);
        var (doc, s) = Bound(a);
        var before = Get(doc, a.Id).Collar;
        doc.Execute("Edit angle", pr => { var h = Get(doc, a.Id); h.Angle = 15; h.Bearing = 120; });
        var h = Get(doc, a.Id);
        Assert.Equal(before, h.Collar);
        Assert.Equal(s.Id, h.CollarSurfaceId);
    }

    [Fact]
    public void AnElevationTypedIn_UnbindsTheHole_OneUndoBringsAllBack()
    {
        var a = NewHole(10, 0);
        var (doc, s) = Bound(a);
        doc.Execute("Edit collar", pr => { var h = Get(doc, a.Id); h.Collar = h.Collar.WithZ(95); });
        var edited = Get(doc, a.Id);
        Assert.Equal(95, edited.Collar.Z);
        Assert.Null(edited.CollarSurfaceId);
        // moved now, it stays at its elevation
        doc.Execute("Move", pr => Get(doc, a.Id).Translate(new Vec3(20, 0, 0)));
        Assert.Equal(95, Get(doc, a.Id).Collar.Z);
        doc.Undo();
        doc.Undo();
        var back = Get(doc, a.Id);
        Assert.Equal(Bench(10), back.Collar.Z, 9);
        Assert.Equal(s.Id, back.CollarSurfaceId);
    }

    [Fact]
    public void MovedOffTheSurface_TheCollarStaysAsMoved_TheBindingLives()
    {
        var a = NewHole(10, 0);
        var (doc, s) = Bound(a);
        doc.Execute("Move", pr => Get(doc, a.Id).Translate(new Vec3(500, 0, 0)));
        var h = Get(doc, a.Id);
        Assert.Equal(510, h.Collar.X, 9);
        Assert.Equal(Bench(10), h.Collar.Z, 9);
        Assert.Equal(s.Id, h.CollarSurfaceId);
        // back over the surface it goes onto it again
        doc.Execute("Move", pr => Get(doc, a.Id).Translate(new Vec3(-450, 0, 0)));
        Assert.Equal(Bench(60), Get(doc, a.Id).Collar.Z, 9);
    }

    [Fact]
    public void TheSurfaceDeleted_AMoveLeavesTheCollar_UndoBringsTheSurfaceBack()
    {
        var a = NewHole(10, 0);
        var (doc, s) = Bound(a);
        doc.Execute("Delete", pr => pr.Entities.RemoveAll(e => e.Id == s.Id));
        doc.Execute("Move", pr => Get(doc, a.Id).Translate(new Vec3(30, 0, 0)));
        var h = Get(doc, a.Id);
        Assert.Equal(Bench(10), h.Collar.Z, 9);
        Assert.Equal(s.Id, h.CollarSurfaceId);
        doc.Undo();
        doc.Undo();
        Assert.NotNull(doc.Project.FindEntity(s.Id));
        Assert.Equal(10, Get(doc, a.Id).Collar.X, 9);
    }

    [Fact]
    public void ACopy_GoesOntoTheSurface_BoundToTheCopyOfItWhenItIsCopiedToo()
    {
        var a = NewHole(10, 0);
        var (doc, s) = Bound(a);
        // as Move/copy entities: the copies added, then seated
        List<Entity> alone = new(), both = new();
        doc.Execute("Copy", pr =>
        {
            alone = GeneralOps.Copy(new Entity[] { Get(doc, a.Id) }, new Vec3(20, 0, 3), 1);
            pr.Entities.AddRange(alone);
            CollarSurfaces.SeatAll(pr, alone.OfType<Hole>());
        });
        var copy = (Hole)alone.Single();
        Assert.Equal(s.Id, copy.CollarSurfaceId);
        Assert.Equal(Bench(30), Get(doc, copy.Id).Collar.Z, 9);
        doc.Execute("Copy", pr =>
        {
            both = GeneralOps.Copy(new Entity[] { Get(doc, a.Id), pr.FindEntity(s.Id)! }, new Vec3(0, 300, 0), 1);
            pr.Entities.AddRange(both);
            CollarSurfaces.SeatAll(pr, both.OfType<Hole>());
        });
        var surfaceCopy = both.OfType<SurfaceEntity>().Single();
        var holeCopy = both.OfType<Hole>().Single();
        Assert.Equal(surfaceCopy.Id, holeCopy.CollarSurfaceId);
        Assert.Equal(Bench(10), Get(doc, holeCopy.Id).Collar.Z, 9);
    }

    [Fact]
    public void APastedHole_IsSeatedByTheFollower_ItsBindingToASurfaceNotInTheProjectDropped()
    {
        var a = NewHole(10, 0);
        var (doc, s) = Bound(a);
        // as Paste does: the hole through the clipboard, a new id, put elsewhere
        var copy = (Hole)ProjectJson.DeserializeEntities(ProjectJson.SerializeEntities(new Entity[] { Get(doc, a.Id) }))[0];
        copy.Id = Guid.NewGuid();
        copy.Translate(new Vec3(40, 0, 0));
        doc.Execute("Paste", pr => pr.Entities.Add(copy));
        var pasted = Get(doc, copy.Id);
        Assert.Equal(s.Id, pasted.CollarSurfaceId);
        Assert.Equal(Bench(50), pasted.Collar.Z, 9);
        // into another project: the surface is not there, the hole is bound to none
        var other = new Document(BlastProject.CreateNew());
        var foreign = (Hole)ProjectJson.DeserializeEntities(ProjectJson.SerializeEntities(new Entity[] { Get(doc, a.Id) }))[0];
        foreign.Id = Guid.NewGuid();
        other.Execute("Paste", pr =>
        {
            pr.Entities.Add(foreign);
            if (CollarSurfaces.Find(pr, foreign.CollarSurfaceId) == null) foreign.CollarSurfaceId = null;
        });
        Assert.Null(((Hole)other.Project.FindEntity(foreign.Id)!).CollarSurfaceId);
    }

    [Fact]
    public void TheFollower_DoesNothingWithoutBoundHoles()
    {
        var p = BlastProject.CreateNew();
        p.Add(NewHole(0, 0), p.GetOrCreateLayer(StandardLayers.Holes));
        Assert.Null(CollarSurfaces.Follow(p));
    }

    [Fact]
    public void TheBinding_IsSavedAndLoaded_AnOlderFileHasNone()
    {
        var a = NewHole(10, 0);
        var (doc, s) = Bound(a);
        var path = Path.Combine(Path.GetTempPath(), $"collar_{Guid.NewGuid():N}.bdp");
        try
        {
            BlastDesigner.IO.ProjectFile.Save(doc.Project, path, 0);
            var loaded = BlastDesigner.IO.ProjectFile.Load(path);
            Assert.Equal(s.Id, loaded.Holes.Single().CollarSurfaceId);
        }
        finally { File.Delete(path); }
        var json = ProjectJson.SerializeEntities(new Entity[] { NewHole(0, 0) });
        Assert.DoesNotContain("collarSurfaceId", json);
        Assert.Null(((Hole)ProjectJson.DeserializeEntities(json)[0]).CollarSurfaceId);
    }

    // ---- the Collars list of a pattern ------------------------------------------------------------

    private static readonly Vec3[] Rectangle = { new(0, 0, 90), new(41, 0, 90), new(41, 23, 90), new(0, 23, 90) };

    private static (BlastProject P, HolePattern Pat, SurfaceEntity S) Pattern()
    {
        var p = BlastProject.CreateNew();
        var s = Sloping();
        p.Add(s);
        var holes = HoleDesign.FillPolygon(Rectangle, new PatternParameters { Burden = 4, Spacing = 4.5, Pattern = PatternType.Rectangular, StartNumber = 1 },
            new HoleParameters { BenchHeight = 10, Subdrill = 1 }, out var rows, out var pat);
        var layer = p.GetOrCreateLayer(StandardLayers.Holes);
        foreach (var h in holes) p.Add(h, layer);
        p.Rows.AddRange(rows);
        p.Patterns.Add(pat);
        return (p, pat, s);
    }

    [Fact]
    public void APatternOnTheSurface_LaysItsNewHolesOutOnIt_TheGridItemUnbindsThem()
    {
        var (p, pat, s) = Pattern();
        var doc = new Document(p);
        doc.Execute("Depth", pr =>
        {
            var x = pr.FindPattern(pat.Id)!;
            x.HoleDefaults.Depth.CollarSurfaceId = s.Id;
            CollarSurfaces.FollowDepth(pr, x);
            // the outline grown: new holes
            x.Outline = x.Outline.Select(v => new Vec3(v.X * 1.5, v.Y, v.Z)).ToList();
            HoleDesign.RebuildPattern(pr, x);
        });
        Assert.True(doc.Project.Holes.Count() > 9);
        Assert.All(doc.Project.Holes, h =>
        {
            Assert.Equal(s.Id, h.CollarSurfaceId);
            Assert.Equal(Bench(h.Collar.X), h.Collar.Z, 9);
        });
        // On the grid: unbound, the collars left as they are
        doc.Execute("Depth", pr =>
        {
            var x = pr.FindPattern(pat.Id)!;
            x.HoleDefaults.Depth.CollarSurfaceId = null;
            CollarSurfaces.FollowDepth(pr, x);
            HoleDesign.RebuildPattern(pr, x);
        });
        Assert.All(doc.Project.Holes, h => Assert.Null(h.CollarSurfaceId));
    }

    [Fact]
    public void TheButtonOnAllTheHolesOfAPattern_SwitchesItsList_OnSomeOfThemItSaysSo()
    {
        var (p, pat, s) = Pattern();
        var doc = new Document(p);
        var some = p.Holes.Take(3).Select(h => h.Id).ToHashSet();
        var partial = doc.Execute("Collars onto surface", pr => CollarSurfaces.Drape(pr, pr.Holes.Where(h => some.Contains(h.Id)).ToList(), (SurfaceEntity)pr.FindEntity(s.Id)!, false));
        Assert.Empty(partial.Switched);
        Assert.Equal((pat.Name, 3, p.Holes.Count()), partial.Partial.Single());
        Assert.Null(doc.Project.FindPattern(pat.Id)!.HoleDefaults.Depth.CollarSurfaceId);
        var all = doc.Execute("Collars onto surface", pr => CollarSurfaces.Drape(pr, pr.Holes.ToList(), (SurfaceEntity)pr.FindEntity(s.Id)!, false));
        Assert.Equal(pat.Name, all.Switched.Single());
        Assert.True(doc.Project.FindPattern(pat.Id)!.HoleDefaults.Depth.OnOwnSurface);
        // a hole moved off its node goes onto the surface; one whose elevation was typed in keeps it on a new layout
        var id = doc.Project.Holes.First().Id;
        doc.Execute("Edit collar", pr => { var h = Get(doc, id); h.Collar = h.Collar.WithZ(120); });
        Assert.Null(Get(doc, id).CollarSurfaceId);
        doc.Execute("Layout", pr => HoleDesign.RebuildPattern(pr, pr.FindPattern(pat.Id)!));
        Assert.Equal(120, Get(doc, id).Collar.Z);
    }

    [Fact]
    public void TheButtonOnTheHolesOfOneSegment_SwitchesThatSegmentOnly()
    {
        var (p, pat, s) = Pattern();
        PatternTree.Split(p, pat, 2);
        var leaves = PatternTree.Leaves(p, pat);
        Assert.Equal(2, leaves.Count);
        var doc = new Document(p);
        var first = leaves[0].Id;
        var ids = p.Holes.Where(h => h.PatternId == first).Select(h => h.Id).ToHashSet();
        var report = doc.Execute("Collars onto surface", pr => CollarSurfaces.Drape(pr, pr.Holes.Where(h => ids.Contains(h.Id)).ToList(), (SurfaceEntity)pr.FindEntity(s.Id)!, false));
        Assert.Single(report.Switched);
        var seg = doc.Project.FindPattern(first)!;
        Assert.Equal(s.Id, seg.Overrides.Depth!.CollarSurfaceId);
        Assert.True(seg.HoleDefaults.Depth.OnOwnSurface);
        Assert.False(doc.Project.FindPattern(leaves[1].Id)!.HoleDefaults.Depth.OnOwnSurface);
        // all of them: the root takes it, the segment's own depth of only the surface dropped
        doc.Execute("Collars onto surface", pr => CollarSurfaces.Drape(pr, pr.Holes.ToList(), (SurfaceEntity)pr.FindEntity(s.Id)!, false));
        Assert.Equal(s.Id, doc.Project.FindPattern(pat.Id)!.HoleDefaults.Depth.CollarSurfaceId);
        Assert.Null(doc.Project.FindPattern(first)!.Overrides.Depth);
        Assert.All(PatternTree.Leaves(doc.Project, doc.Project.FindPattern(pat.Id)!), l => Assert.True(l.HoleDefaults.Depth.OnOwnSurface));
    }

    [Fact]
    public void DepthSpec_ClonesAndComparesTheSurface()
    {
        var d = new DepthSpec { CollarSurfaceId = Guid.NewGuid() };
        var c = d.Clone();
        Assert.Equal(d.CollarSurfaceId, c.CollarSurfaceId);
        Assert.True(d.SameAs(c));
        c.CollarSurfaceId = null;
        Assert.False(d.SameAs(c));
    }
}
