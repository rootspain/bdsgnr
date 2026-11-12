using System.Text.Json.Nodes;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;

namespace BlastDesigner.Tests;

/// <summary>Pattern grids: a pattern remembers its grid, so it can be turned and laid out again after it was made.</summary>
public class PatternTests
{
    private static readonly Vec3[] Field = { new(0, 0, 100), new(47, -3, 100), new(53, 31, 100), new(21, 44, 100), new(-6, 27, 100) };
    private static readonly Vec3[] Rectangle = { new(0, 0, 100), new(41, 0, 100), new(41, 23, 100), new(0, 23, 100) };

    /// <summary>A regular 36-gon: turned by a multiple of 10° about its centre it is the same outline, so no node leaves it.</summary>
    private static Vec3[] Circle(double r = 20) =>
        Enumerable.Range(0, 36).Select(k => new Vec3(500 + r * Math.Sin(k * Math.PI / 18), 800 + r * Math.Cos(k * Math.PI / 18), 100)).ToArray();

    private static (BlastProject P, HolePattern Pat, List<Hole> Holes) Make(IReadOnlyList<Vec3> outline, double orientation = 0, PatternType type = PatternType.Staggered,
        double angle = 0, bool rotateOrientation = false)
    {
        var p = BlastProject.CreateNew();
        p.Header.BlastOrientation = orientation;
        var holes = HoleDesign.FillPolygon(outline, new PatternParameters { Burden = 4, Spacing = 4.5, Pattern = type, Orientation = orientation, StartNumber = 1, RotateOrientation = rotateOrientation },
            new HoleParameters { Angle = angle, BenchHeight = 10, Subdrill = 1 }, out var rows, out var pat);
        var layer = p.GetOrCreateLayer(StandardLayers.Holes);
        foreach (var h in holes) p.Add(h, layer);
        p.Rows.AddRange(rows);
        p.Patterns.Add(pat);
        return (p, pat, holes);
    }

    private static Hole H(BlastProject p, Guid id) => (Hole)p.FindEntity(id)!;

    /// <summary>The pattern tool before the grids were kept, as it was: the reference for the holes of a new pattern.</summary>
    private static List<(Vec3 Collar, string Label, int Row)> OldFill(IReadOnlyList<Vec3> polygon, PatternParameters pp)
    {
        var result = new List<(Vec3, string, int)>();
        var move = GeoMath.HoleDirection(pp.Orientation, 90);
        var vDir = new Vec3(-move.X, -move.Y, 0);
        var uDir = new Vec3(move.Y, -move.X, 0);
        var origin = polygon[0];
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        foreach (var p in polygon)
        {
            var d = p - origin;
            var u = d.X * uDir.X + d.Y * uDir.Y;
            var v = d.X * vDir.X + d.Y * vDir.Y;
            minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
        }
        var avgZ = polygon.Average(p => p.Z);
        int number = pp.StartNumber, rowIndex = 0;
        for (double v = minV + pp.Burden / 2; v <= maxV; v += pp.Burden, rowIndex++)
        {
            var offset = pp.Pattern == PatternType.Staggered && rowIndex % 2 == 1 ? pp.Spacing / 2 : 0;
            for (double u = minU + pp.Spacing / 2 + offset; u <= maxU; u += pp.Spacing)
            {
                var pt = (origin + uDir * u + vDir * v).WithZ(avgZ);
                if (!GeoMath.PointInPolygon(polygon, pt.X, pt.Y)) continue;
                if (pp.EdgeOffset > 0 && GeoMath.DistanceToPolyline2D(polygon, pt, true) < pp.EdgeOffset) continue;
                result.Add((pt, pp.LabelPrefix + number++, rowIndex));
            }
        }
        return result;
    }

    [Theory]
    [InlineData(0, PatternType.Staggered, 0)]
    [InlineData(37, PatternType.Staggered, 0)]
    [InlineData(200, PatternType.Rectangular, 1.5)]
    [InlineData(-30, PatternType.Staggered, 2)]
    public void ANewPattern_IsLaidOutAsBefore_AndLayingItOutAgainUnturnedChangesNothing(double orientation, PatternType type, double edge)
    {
        var pp = new PatternParameters { Burden = 4, Spacing = 4.5, Pattern = type, Orientation = orientation, EdgeOffset = edge, LabelPrefix = "A", StartNumber = 5 };
        var old = OldFill(Field, pp);
        var holes = HoleDesign.FillPolygon(Field, pp, new HoleParameters(), out var rows, out var pat);
        Assert.Equal(old.Count, holes.Count);
        for (int k = 0; k < old.Count; k++)
        {
            Assert.Equal(0, Vec3.Distance(old[k].Collar, holes[k].Collar), 9);
            Assert.Equal(old[k].Label, holes[k].Label);
        }
        Assert.Equal(old.Select(o => o.Row).Distinct().Count(), rows.Count);
        // the grid rows are the rows of before, counted from the face row (j = 0)
        Assert.Equal(old.Select(o => o.Row), holes.Select(h => h.GridJ!.Value));

        var p = BlastProject.CreateNew();
        p.Header.BlastOrientation = orientation;
        foreach (var h in holes) p.Add(h);
        p.Rows.AddRange(rows);
        p.Patterns.Add(pat);
        var collars = holes.ToDictionary(h => h.Id, h => (h.Collar, h.Label));
        var rowOrder = rows.Select(r => r.Id).ToList();
        var report = HoleDesign.RebuildPattern(p, pat);
        Assert.Null(report.Refusal);
        Assert.Equal((0, 0, 0), (report.Moved, report.Deleted, report.Added));
        foreach (var h in p.Holes)
        {
            Assert.Equal(0, Vec3.Distance(collars[h.Id].Collar, h.Collar), 9);
            Assert.Equal(collars[h.Id].Label, h.Label);
        }
        Assert.Equal(rowOrder, p.Rows.Select(r => r.Id).ToList());
    }

    [Fact]
    public void TurningTheGrid_TurnsEveryNodeAboutTheCentre_AndKeepsTheHoles()
    {
        var (p, pat, holes) = Make(Circle());
        var before = holes.ToDictionary(h => h.Id, h => h.Collar);
        var report = HoleDesign.RotatePattern(p, pat, 30);
        Assert.Equal((0, 0), (report.Deleted, report.Added));
        Assert.Equal(before.Count, p.Holes.Count());
        var turn = GeneralOps.MakeTransform(Vec3.Zero, 30, pat.Center, 1);
        foreach (var h in p.Holes) Assert.True(Vec3.Distance2D(turn(before[h.Id]), h.Collar) < 1e-3);
        Assert.Equal(30, pat.Azimuth, 9);

        // and back: the same collars, the same ids
        HoleDesign.RotatePattern(p, pat, -30);
        foreach (var h in p.Holes) Assert.True(Vec3.Distance2D(before[h.Id], h.Collar) < 1e-6);
        Assert.Equal(before.Keys.OrderBy(x => x), p.Holes.Select(h => h.Id).OrderBy(x => x));
    }

    [Fact]
    public void TurningThereAndBack_PutsTheHolesThatStayedBackWhereTheyWere()
    {
        var (p, pat, holes) = Make(Rectangle);
        var before = holes.ToDictionary(h => h.Id, h => h.Collar);
        HoleDesign.RotatePattern(p, pat, 17);
        var stayed = p.Holes.Select(h => h.Id).Where(before.ContainsKey).ToHashSet();
        Assert.NotEmpty(stayed);
        HoleDesign.RotatePattern(p, pat, -17);
        foreach (var id in stayed) Assert.True(Vec3.Distance2D(before[id], H(p, id).Collar) < 1e-6);
        // every node of the outline has its hole again
        Assert.Equal(before.Count, p.Holes.Count());
        Assert.All(p.Holes, h => Assert.Contains(before.Values, c => Vec3.Distance2D(c, h.Collar) < 1e-6));
    }

    [Fact]
    public void NodesThatLeaveTheOutline_LoseTheirHolesAndTies_AndNewNodesGetHoles()
    {
        var (p, pat, holes) = Make(Rectangle, type: PatternType.Rectangular);
        var ties = p.GetOrCreateLayer(StandardLayers.Ties);
        for (int k = 1; k < holes.Count; k++) p.Add(new TieEntity { FromId = holes[k - 1].Id, ToId = holes[k].Id }, ties);
        var report = HoleDesign.RotatePattern(p, pat, 45);
        Assert.True(report.Deleted > 0);
        Assert.True(report.Added > 0);
        Assert.True(report.TiesLost > 0);
        var ids = p.Holes.Select(h => h.Id).ToHashSet();
        Assert.All(p.Ties, t => Assert.True(ids.Contains(t.FromId) && ids.Contains(t.ToId)));
        Assert.Equal(holes.Count - 1 - report.TiesLost, p.Ties.Count());
        Assert.All(p.Holes, h => Assert.True(GeoMath.PointInPolygon(Rectangle, h.Collar.X, h.Collar.Y)));
        Assert.All(p.Rows, r => Assert.All(r.HoleIds, id => Assert.Contains(id, ids)));
        Assert.Equal(p.Holes.Count(), p.Rows.Sum(r => r.HoleIds.Count));
        // numbered again by the rows
        Assert.Equal(Enumerable.Range(1, ids.Count).Select(n => n.ToString()), p.Rows.SelectMany(r => r.HoleIds).Select(id => H(p, id).Label));
    }

    [Fact]
    public void AMovedHole_KeepsItsLoadingTypeTimesAndDetonator()
    {
        var (p, pat, holes) = Make(Circle());
        var h = holes[7];
        Charging.ApplyRules(p, new[] { h });
        h.ManualLoading = true;
        h.HoleType = 3;
        h.ElectronicTimeMs = 125;
        h.ElectronicTimeManual = true;
        h.DetonatorId = "EDD-42";
        h.Notes = "wet";
        var decks = ProjectJson.DeepClone(h.Decks);
        HoleDesign.RotatePattern(p, pat, 40);
        var moved = H(p, h.Id);
        Assert.Equal(3, moved.HoleType);
        Assert.Equal(125, moved.ElectronicTimeMs);
        Assert.True(moved.ManualLoading);
        Assert.Equal("EDD-42", moved.DetonatorId);
        Assert.Equal("wet", moved.Notes);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(decks), System.Text.Json.JsonSerializer.Serialize(moved.Decks));
    }

    [Fact]
    public void DeletedAndMovedHoles_StayDeletedAndMovedAfterATurn()
    {
        var (p, pat, holes) = Make(Circle());
        var doc = new Document(p);
        var gone = holes[5];
        var shifted = holes[12];
        var shiftedAt = shifted.Collar;
        doc.Execute("Delete", pr => pr.Entities.RemoveAll(e => e.Id == gone.Id));
        var pattern = doc.Project.Patterns[0];
        Assert.True(pattern.IsSuppressed(gone.GridI!.Value, gone.GridJ!.Value));
        doc.Execute("Move", pr => pr.FindEntity(shifted.Id)!.Translate(new Vec3(1, 0.5, 0)));
        var offset = H(doc.Project, shifted.Id).GridOffset;
        Assert.NotNull(offset);
        Assert.Equal(Math.Sqrt(1.25), offset!.Value.Length2D, 9);

        doc.Execute("Rotate pattern", pr => HoleDesign.RotatePattern(pr, pr.Patterns[0], 30));
        var pat2 = doc.Project.Patterns[0];
        Assert.DoesNotContain(doc.Project.Holes, h => h.GridI == gone.GridI && h.GridJ == gone.GridJ);
        Assert.Equal(holes.Count - 1, doc.Project.Holes.Count());
        var s = H(doc.Project, shifted.Id);
        var (u, v) = HoleDesign.PatternAxes(pat2.Azimuth);
        var expected = HoleDesign.NodePoint(pat2, s.GridI!.Value, s.GridJ!.Value) + u * offset.Value.X + v * offset.Value.Y;
        Assert.True(Vec3.Distance2D(expected, s.Collar) < 1e-6);
        // turned about the centre with its node
        Assert.True(Vec3.Distance2D(GeneralOps.MakeTransform(Vec3.Zero, 30, pat2.Center, 1)(shiftedAt + new Vec3(1, 0.5, 0)), s.Collar) < 1e-3);
        // kept in the file
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(doc.Project));
        Assert.True(back.Patterns[0].IsSuppressed(gone.GridI!.Value, gone.GridJ!.Value));
        Assert.Equal(offset.Value.X, H(back, shifted.Id).GridOffset!.Value.X, 9);
    }

    [Fact]
    public void MovingAllTheHolesTogether_TakesTheGridWithThem()
    {
        var (p, pat, holes) = Make(Rectangle);
        var doc = new Document(p);
        var centre = pat.Center;
        var f = GeneralOps.MakeTransform(new Vec3(100, -50, 0), 25, new Vec3(10, 10, 0), 1);
        doc.Execute("Transform coordinates", pr => { foreach (var h in pr.Holes) h.Transform(f); });
        var pat2 = doc.Project.Patterns[0];
        Assert.Equal(25, pat2.Azimuth, 6);
        Assert.Equal(0, Vec3.Distance2D(f(centre), pat2.Center), 6);
        Assert.All(doc.Project.Holes, h => Assert.Null(h.GridOffset));
        var collars = doc.Project.Holes.ToDictionary(h => h.Id, h => h.Collar);
        var report = HoleDesign.RebuildPattern(doc.Project, pat2);
        Assert.Equal((0, 0), (report.Deleted, report.Added));
        Assert.All(doc.Project.Holes, h => Assert.True(Vec3.Distance2D(collars[h.Id], h.Collar) < 1e-6));
    }

    [Fact]
    public void WithTheOrientationKept_TheFaceRowIsTheOtherEndAfterHalfATurn()
    {
        var (p, pat, holes) = Make(Rectangle, type: PatternType.Rectangular);
        var lastJ = holes.Max(h => h.GridJ!.Value);
        HoleDesign.RotatePattern(p, pat, 180);
        Assert.Equal(0, p.Header.BlastOrientation);
        var first = p.Rows[0].HoleIds.Select(id => H(p, id)).ToList();
        Assert.All(first, h => Assert.Equal(lastJ, h.GridJ));
        Assert.Equal("1", first[0].Label);
        // the face row is the one furthest along the blast (north at 0°)
        Assert.True(first.Average(h => h.Collar.Y) > p.Rows[^1].HoleIds.Select(id => H(p, id)).Average(h => h.Collar.Y));
        Assert.Equal("Row 1", p.Rows[0].Name);

        var (q, qat, _) = Make(Rectangle, type: PatternType.Rectangular, rotateOrientation: true);
        HoleDesign.RotatePattern(q, qat, 180);
        Assert.Equal(180, q.Header.BlastOrientation, 9);
        Assert.All(q.Rows[0].HoleIds.Select(id => H(q, id)), h => Assert.Equal(0, h.GridJ));
    }

    [Theory]
    [InlineData(PatternBearingMode.Rotate, 30)]
    [InlineData(PatternBearingMode.Keep, 0)]
    [InlineData(PatternBearingMode.BlastOrientation, 75)]
    public void TheBearingOfInclinedHoles_FollowsTheChosenMode(PatternBearingMode mode, double expected)
    {
        var (p, pat, holes) = Make(Circle(), angle: 10);
        p.Header.BlastOrientation = 75;
        var vertical = holes[3];
        vertical.Angle = 0;
        vertical.Bearing = 12;
        HoleDesign.RotatePattern(p, pat, 30, new PatternRebuildOptions { Bearing = mode });
        Assert.All(p.Holes.Where(h => h.Id != vertical.Id), h => Assert.Equal(expected, h.Bearing, 6));
        // vertical holes have no bearing to turn
        Assert.Equal(12, H(p, vertical.Id).Bearing);
    }

    [Fact]
    public void APatternWithDrilledLockedOrSurveyedHoles_IsNotTurned()
    {
        foreach (var spoil in new Action<BlastProject, Hole>[]
        {
            (_, h) => h.State = HoleState.Drilled,
            (_, h) => h.Locked = true,
            (_, h) => h.ActualCollar = h.Collar,
            (p, h) => p.FindLayer(h.LayerId)!.Locked = true,
            (p, h) => p.FindLayer(h.LayerId)!.Frozen = true
        })
        {
            var (p, pat, holes) = Make(Rectangle);
            spoil(p, holes[2]);
            var collars = p.Holes.ToDictionary(h => h.Id, h => h.Collar);
            var report = HoleDesign.RotatePattern(p, pat, 20);
            Assert.NotNull(report.Refusal);
            Assert.Equal(0, pat.Azimuth);
            Assert.All(p.Holes, h => Assert.Equal(collars[h.Id], h.Collar));
        }
    }

    [Fact]
    public void ThePatternIsKeptInTheFile_AndAFileWithoutPatternsStillOpens()
    {
        var (p, pat, holes) = Make(Field, orientation: 15);
        pat.Suppressed.Add(new GridIndex { I = 2, J = 1 });
        holes[0].GridOffset = new Vec3(0.3, -0.2, 0);
        var path = Path.Combine(Path.GetTempPath(), $"pattern_{Guid.NewGuid():N}.bdp");
        try
        {
            ProjectFile.Save(p, path, 0);
            var back = ProjectFile.Load(path);
            var bp = Assert.Single(back.Patterns);
            Assert.Equal(pat.Id, bp.Id);
            Assert.Equal(pat.Outline.Count, bp.Outline.Count);
            Assert.Equal(pat.PhaseU, bp.PhaseU, 12);
            Assert.Equal(pat.RowIds, bp.RowIds);
            Assert.True(bp.IsSuppressed(2, 1));
            var h0 = H(back, holes[0].Id);
            Assert.Equal((pat.Id, holes[0].GridI, holes[0].GridJ), (h0.PatternId!.Value, h0.GridI, h0.GridJ));
            Assert.Equal(-0.2, h0.GridOffset!.Value.Y, 12);
        }
        finally { File.Delete(path); }

        // a file of the program before the grids: no patterns, holes of their own
        var json = JsonNode.Parse(ProjectJson.Serialize(p))!.AsObject();
        json.Remove("patterns");
        foreach (var e in json["entities"]!.AsArray())
            foreach (var key in new[] { "patternId", "gridI", "gridJ", "gridOffset" }) e!.AsObject().Remove(key);
        var old = ProjectJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.Empty(old.Patterns);
        Assert.Equal(holes.Count, old.Holes.Count());
        Assert.All(old.Holes, h => Assert.Null(h.PatternId));
    }

    [Fact]
    public void ATurn_IsOneUndoStep()
    {
        var (p, pat, holes) = Make(Rectangle, rotateOrientation: true);
        var doc = new Document(p);
        var collars = p.Holes.ToDictionary(h => h.Id, h => h.Collar);
        var steps = doc.UndoNames.Count;
        PatternRebuildReport? report = null;
        doc.Execute("Rotate pattern", pr => report = HoleDesign.RotatePattern(pr, pr.Patterns[0], 33));
        Assert.True(report!.Deleted + report.Added + report.Moved > 0);
        Assert.Equal(steps + 1, doc.UndoNames.Count);
        doc.Undo();
        Assert.Equal(0, doc.Project.Patterns[0].Azimuth);
        Assert.Equal(0, doc.Project.Header.BlastOrientation);
        Assert.Equal(collars.Count, doc.Project.Holes.Count());
        Assert.All(doc.Project.Holes, h => Assert.Equal(collars[h.Id], h.Collar));
        // a turn is not taken for a hand edit: nothing was suppressed and no offsets were kept
        doc.Redo();
        Assert.Empty(doc.Project.Patterns[0].Suppressed);
        Assert.All(doc.Project.Holes, h => Assert.Null(h.GridOffset));
    }

    [Fact]
    public void ACopyOfAPatternHole_IsAHoleOfItsOwn_AndDetachedHolesLeaveTheirNodeEmpty()
    {
        var (p, pat, holes) = Make(Circle());
        var doc = new Document(p);
        var copyId = Guid.Empty;
        doc.Execute("Paste", pr => { var c = (Hole)pr.FindEntity(holes[0].Id)!.CloneWithNewId(); c.Translate(new Vec3(1, 1, 0)); pr.Entities.Add(c); copyId = c.Id; });
        Assert.Null(H(doc.Project, copyId).PatternId);
        doc.Execute("Detach", pr => HoleDesign.DetachFromPattern(pr, new[] { (Hole)pr.FindEntity(holes[1].Id)! }));
        Assert.Null(H(doc.Project, holes[1].Id).PatternId);
        doc.Execute("Rebuild", pr => HoleDesign.RebuildPattern(pr, pr.Patterns[0]));
        Assert.Equal(1, doc.Project.Holes.Count(h => Vec3.Distance2D(h.Collar, holes[1].Collar) < 1e-6));
        doc.Execute("Dissolve", pr => HoleDesign.DissolvePattern(pr, pr.Patterns[0]));
        Assert.Empty(doc.Project.Patterns);
        Assert.All(doc.Project.Holes, h => Assert.Null(h.PatternId));
    }

    /// <summary>An outline drawn over uneven ground lies at one elevation, the mean of its points, which the centre has too.</summary>
    [Fact]
    public void TheOutlineOfANewPattern_LiesAtOneElevation()
    {
        var uneven = new[] { new Vec3(0, 0, 100), new Vec3(41, 0, 104), new Vec3(41, 23, 98), new Vec3(0, 23, 102) };
        HoleDesign.FillPolygon(uneven, new PatternParameters { Burden = 4, Spacing = 4.5, StartNumber = 1 }, new HoleParameters(), out _, out var pat);
        Assert.Equal(uneven.Length, pat.Outline.Count);
        Assert.All(pat.Outline, p => Assert.Equal(101, p.Z, 9));
        Assert.Equal(101, pat.Center.Z, 9);
        // in plan the outline is the polygon as drawn
        Assert.All(pat.Outline.Zip(uneven), t => Assert.Equal(0, Vec3.Distance2D(t.First, t.Second), 9));
    }

    /// <summary>
    /// A new burden and spacing lay the grid out again: rows a burden apart, the first half a burden from the face side of the
    /// outline, holes a spacing apart along a row, the rows taking the new values; the holes that stay keep their ids.
    /// </summary>
    [Theory]
    [InlineData(PatternType.Rectangular, 0)]
    [InlineData(PatternType.Staggered, 30)]
    public void ANewBurdenAndSpacing_LayTheGridOutAgain(PatternType type, double orientation)
    {
        var (p, pat, before) = Make(Rectangle, orientation, type);
        var report = HoleDesign.ResizePattern(p, pat, 5, 6);
        Assert.Null(report.Refusal);
        Assert.Equal((5.0, 6.0), (pat.Burden, pat.Spacing));
        var holes = HoleDesign.PatternHoles(p, pat).ToList();
        Assert.NotEmpty(holes);
        Assert.Contains(holes, h => before.Any(b => b.Id == h.Id));
        var (u, v) = HoleDesign.PatternAxes(pat.Azimuth);
        double V(Vec3 a) => (a.X - pat.Center.X) * v.X + (a.Y - pat.Center.Y) * v.Y;
        double U(Vec3 a) => (a.X - pat.Center.X) * u.X + (a.Y - pat.Center.Y) * u.Y;
        var face = pat.Outline.Min(V);
        var rows = holes.GroupBy(h => h.GridJ!.Value).OrderBy(g => g.Key).ToList();
        Assert.Equal(2.5, rows[0].Min(h => V(h.Collar)) - face, 6);
        for (int k = 1; k < rows.Count; k++) Assert.Equal(5, V(rows[k].First().Collar) - V(rows[k - 1].First().Collar), 6);
        foreach (var r in rows)
        {
            var along = r.Select(h => U(h.Collar)).OrderBy(x => x).ToList();
            for (int k = 1; k < along.Count; k++) Assert.Equal(6, along[k] - along[k - 1], 6);
        }
        Assert.All(p.Rows.Where(r => pat.RowIds.ContainsValue(r.Id)), r => Assert.Equal((5.0, 6.0), (r.Burden, r.Spacing)));
        Assert.All(holes, h => Assert.Equal(h.RowId, pat.RowIds[h.GridJ!.Value]));
    }

    [Fact]
    public void ADrilledPatternIsNotResized()
    {
        var (p, pat, holes) = Make(Rectangle);
        holes[0].State = HoleState.Drilled;
        var report = HoleDesign.ResizePattern(p, pat, 5, 6);
        Assert.NotNull(report.Refusal);
        Assert.Equal((4.0, 4.5), (pat.Burden, pat.Spacing));
    }
}
