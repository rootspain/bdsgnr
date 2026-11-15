using System.IO.Compression;
using System.Text.Json.Nodes;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;

namespace BlastDesigner.Tests;

/// <summary>Patterns cut into segments, and the depth of a pattern given by four numbers at the corners of its rectangle.</summary>
public class PatternSegmentTests
{
    /// <summary>41 × 23 m: a rectangular 4 × 4.5 grid has nodes at all four corners of its box.</summary>
    private static readonly Vec3[] Rectangle = { new(0, 0, 100), new(41, 0, 100), new(41, 23, 100), new(0, 23, 100) };

    internal static (BlastProject P, HolePattern Pat) Make(IReadOnlyList<Vec3> outline, PatternType type = PatternType.Rectangular, double orientation = 0,
        double burden = 4, double spacing = 4.5, string prefix = "", int start = 1)
    {
        var p = BlastProject.CreateNew();
        p.Header.BlastOrientation = orientation;
        var holes = HoleDesign.FillPolygon(outline, new PatternParameters { Burden = burden, Spacing = spacing, Pattern = type, Orientation = orientation, LabelPrefix = prefix, StartNumber = start },
            new HoleParameters { BenchHeight = 10, Subdrill = 1 }, out var rows, out var pat);
        var layer = p.GetOrCreateLayer(StandardLayers.Holes);
        foreach (var h in holes) p.Add(h, layer);
        p.Rows.AddRange(rows);
        p.Patterns.Add(pat);
        return (p, pat);
    }

    /// <summary>The holes at the corners of the grid: front left, front right, back left, back right (looking along the blast).</summary>
    private static (Hole FL, Hole FR, Hole BL, Hole BR) Corners(BlastProject p, HolePattern pat)
    {
        var holes = HoleDesign.PatternHoles(p, pat).ToList();
        var j0 = holes.Min(h => h.GridJ!.Value);
        var j1 = holes.Max(h => h.GridJ!.Value);
        Hole At(int j, bool right) => right ? holes.Where(h => h.GridJ == j).MaxBy(h => h.GridI)! : holes.Where(h => h.GridJ == j).MinBy(h => h.GridI)!;
        return (At(j0, false), At(j0, true), At(j1, false), At(j1, true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    public void RectToLevel_CornerHoles_GetTheFourNumbers(double orientation)
    {
        var (p, pat) = Make(Rectangle, orientation: orientation);
        pat.HoleDefaults.Depth = new DepthSpec { Mode = DepthMode.RectToLevel, Level = 80, Front = new DepthPair(10, 12), Back = new DepthPair(11, 13.5) };
        PatternTree.SyncLegacyDepth(pat);
        var report = HoleDesign.RebuildPattern(p, pat);
        Assert.Null(report.Refusal);
        var (fl, fr, bl, br) = Corners(p, pat);
        // looking along the blast (north at 0°) the left end of a row is to the west
        if (orientation == 0) Assert.True(fl.Collar.X < fr.Collar.X);
        Assert.Equal(90, fl.Collar.Z, 9);
        Assert.Equal(92, fr.Collar.Z, 9);
        Assert.Equal(91, bl.Collar.Z, 9);
        Assert.Equal(93.5, br.Collar.Z, 9);
        foreach (var h in p.Holes)
        {
            Assert.Equal(80, h.ToLevel);
            Assert.Equal(h.Collar.Z - 80 + h.Subdrill, h.Length, 9);
            // rounded to 0.1 m
            var d = h.Collar.Z - 80;
            Assert.Equal(Math.Round(d, 1), d, 9);
        }
    }

    [Fact]
    public void SurfaceToRect_CornerHoles_GoDownToTheFourLevels()
    {
        var (p, pat) = Make(Rectangle);
        pat.HoleDefaults.Depth = new DepthSpec { Mode = DepthMode.SurfaceToRect, Front = new DepthPair(90, 88), Back = new DepthPair(89, 87.5) };
        PatternTree.SyncLegacyDepth(pat);
        HoleDesign.RebuildPattern(p, pat);
        var (fl, fr, bl, br) = Corners(p, pat);
        Assert.Equal((90.0, 88.0, 89.0, 87.5), (fl.ToLevel!.Value, fr.ToLevel!.Value, bl.ToLevel!.Value, br.ToLevel!.Value));
        Assert.All(p.Holes, h => Assert.Equal(100, h.Collar.Z, 9));
        Assert.Equal(100 - 87.5 + 1, br.Length, 9);
        // between the corners the levels go over bilinearly, rounded to 0.1 m
        var mid = p.Holes.Single(h => h.GridJ == fl.GridJ && h.GridI == fl.GridI + 4);
        var r = DepthRectangle.Of(pat)!.Value;
        var (s, t) = r.Relative(pat, mid.GridI!.Value, mid.GridJ!.Value);
        Assert.Equal(0, t);
        Assert.Equal(Math.Round(90 - 2 * s, 1), mid.ToLevel!.Value, 9);
    }

    [Fact]
    public void Depth_IsBilinear_AndRounded()
    {
        var d = new DepthSpec { Front = new DepthPair(10, 12), Back = new DepthPair(11, 13.5) };
        Assert.Equal(10, d.At(0, 0));
        Assert.Equal(12, d.At(1, 0));
        Assert.Equal(11, d.At(0, 1));
        Assert.Equal(13.5, d.At(1, 1));
        Assert.Equal(11.6, d.At(0.5, 0.5));
        d.Round = 0;
        Assert.Equal(11.625, d.At(0.5, 0.5), 9);
    }

    [Fact]
    public void ASchema3File_OpensUnchanged_ItsDepthTakenFromTheOldFields()
    {
        var (p, pat) = Make(Rectangle);
        pat.ToLevel = 88;
        foreach (var h in p.Holes) { h.ToLevel = 88; h.Length = HoleLevel.Length(h)!.Value; }
        var before = p.Holes.ToDictionary(h => h.Id, h => (h.Label, h.Collar, h.Length, h.ToLevel, h.RowId));

        // the file of the version before segments: schema 3, none of the new fields
        var json = JsonNode.Parse(ProjectJson.Serialize(p))!.AsObject();
        json["schemaVersion"] = 3;
        foreach (var x in json["patterns"]!.AsArray())
        {
            var o = x!.AsObject();
            foreach (var key in new[] { "parentId", "order", "overrides", "segmentLines", "sourceOutlineId" }) o.Remove(key);
            o["holeDefaults"]!.AsObject().Remove("depth");
        }
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using (var s = zip.CreateEntry("manifest.json").Open())
                s.Write(System.Text.Encoding.UTF8.GetBytes($"{{\"format\":\"{ProjectFile.FormatId}\",\"schemaVersion\":3,\"application\":\"Blast Designer\",\"saved\":\"2026-01-01T00:00:00\",\"name\":\"x\"}}"));
            using (var s = zip.CreateEntry("project.json").Open())
                s.Write(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        }
        ms.Position = 0;
        var back = ProjectFile.Read(ms);
        var bp = Assert.Single(back.Patterns);
        Assert.Null(bp.ParentId);
        Assert.False(bp.IsContainer);
        Assert.True(bp.Overrides.IsEmpty);
        Assert.Equal(DepthMode.ToLevel, bp.HoleDefaults.Depth.Mode);
        Assert.Equal(88, bp.HoleDefaults.Depth.Level);
        Assert.Equal(88, bp.ToLevel);
        Assert.Equal(BlastProject.CurrentSchemaVersion, back.SchemaVersion);
        Assert.Equal(before.Count, back.Holes.Count());
        foreach (var h in back.Holes)
            Assert.Equal(before[h.Id], (h.Label, h.Collar, h.Length, h.ToLevel, h.RowId));
    }

    [Fact]
    public void Resolve_TakesTheValuesOfTheParent_WhereTheSegmentSetsNone()
    {
        var (p, root) = Make(Rectangle);
        var seg = new HolePattern { ParentId = root.Id, Order = 0 };
        seg.Overrides.Diameter = 89;
        seg.Overrides.Depth = new DepthSpec { Mode = DepthMode.SurfaceToRect, Front = new DepthPair(90, 90), Back = new DepthPair(90, 90) };
        p.Patterns.Add(seg);
        var changed = PatternTree.Resolve(p, root);
        Assert.Contains(seg, changed);
        Assert.Equal(root.Burden, seg.Burden);
        Assert.Equal(root.Spacing, seg.Spacing);
        Assert.Equal(89, seg.HoleDefaults.Diameter);
        Assert.Equal(root.HoleDefaults.Subdrill, seg.HoleDefaults.Subdrill);
        Assert.Equal(DepthMode.SurfaceToRect, seg.HoleDefaults.Depth.Mode);
        root.HoleDefaults.Diameter = 115;
        root.HoleDefaults.Subdrill = 1.5;
        Assert.Empty(PatternTree.Resolve(p, root));
        Assert.Equal(89, seg.HoleDefaults.Diameter);
        Assert.Equal(1.5, seg.HoleDefaults.Subdrill);
        Assert.Same(root, PatternTree.Root(p, seg));
    }

    // ---- stage 2: cut and lay out again ----

    private static readonly Vec3[] Field = { new(0, 0, 100), new(47, -3, 100), new(53, 31, 100), new(21, 44, 100), new(-6, 27, 100) };

    private static Hole H(BlastProject p, Guid id) => (Hole)p.FindEntity(id)!;

    [Theory]
    [InlineData(PatternType.Staggered, 0, 3)]
    [InlineData(PatternType.Rectangular, 37, 2)]
    [InlineData(PatternType.Staggered, 200, 4)]
    public void Cutting_WithoutChangingParameters_MovesNoHole(PatternType type, double orientation, int count)
    {
        var (p, root) = Make(Field, type, orientation, prefix: "A", start: 5);
        var before = p.Holes.ToDictionary(h => h.Id, h => (h.Collar, h.Label));
        var rowCount = p.Rows.Count;
        var report = PatternTree.Split(p, root, count);
        Assert.Null(report.Refusal);
        Assert.Equal(0, report.Moved + report.Added + report.Deleted);
        Assert.True(root.IsContainer);
        Assert.Equal(count - 1, root.SegmentLines.Count);
        var segments = PatternTree.Children(p, root);
        Assert.Equal(count, segments.Count);
        Assert.Empty(HoleDesign.PatternHoles(p, root));
        Assert.All(segments, s => Assert.NotEmpty(HoleDesign.PatternHoles(p, s)));
        Assert.Equal(before.Count, p.Holes.Count());
        foreach (var h in p.Holes)
        {
            Assert.Equal(before[h.Id].Collar, h.Collar);
            Assert.Equal(before[h.Id].Label, h.Label);
        }
        // the rows are split by segment, as many as before, named Row 1, Row 2… in order
        Assert.Equal(rowCount, p.Rows.Count);
        Assert.Equal(Enumerable.Range(1, p.Rows.Count).Select(k => $"Row {k}"), p.Rows.Select(r => r.Name));
        Assert.Null(root.OutlineEntityId);
    }

    [Fact]
    public void Cutting_SharesTheRowsEvenly_TheExtraOnesTowardsTheFace()
    {
        var (p, root) = Make(Rectangle);
        Assert.Equal(6, PatternTree.MaxSegments(root));
        PatternTree.Split(p, root, 4);
        var rows = PatternTree.Children(p, root).Select(s => HoleDesign.PatternHoles(p, s).Select(h => h.GridJ).Distinct().Count()).ToList();
        Assert.Equal(new[] { 2, 2, 1, 1 }, rows);
    }

    [Fact]
    public void TheNumbers_RunThroughTheParent_SegmentBySegment()
    {
        var (p, root) = Make(Rectangle, prefix: "P", start: 10);
        PatternTree.Split(p, root, 2);
        var seg = PatternTree.Children(p, root);
        // the back segment gets a smaller spacing: more holes, numbered after all those of the front one
        seg[1].Overrides.Spacing = 3;
        PatternTree.Relayout(p, root);
        var labels = p.Rows.SelectMany(r => r.HoleIds).Select(id => H(p, id).Label).ToList();
        Assert.Equal(Enumerable.Range(10, labels.Count).Select(k => "P" + k), labels);
        var front = HoleDesign.PatternHoles(p, seg[0]).Select(h => int.Parse(h.Label[1..])).ToList();
        var back = HoleDesign.PatternHoles(p, seg[1]).Select(h => int.Parse(h.Label[1..])).ToList();
        Assert.True(front.Max() < back.Min());
        Assert.Equal(p.Holes.Count(), labels.Count);
        Assert.Equal(Enumerable.Range(1, p.Rows.Count).Select(k => $"Row {k}"), p.Rows.Select(r => r.Name));
    }

    [Fact]
    public void ANewBurdenOfTheParent_LaysOutOnlyTheSegmentsThatTakeIt()
    {
        var (p, root) = Make(Rectangle);
        PatternTree.Split(p, root, 2);
        var seg = PatternTree.Children(p, root);
        seg[0].Overrides.Burden = 3;
        seg[0].Overrides.Spacing = 3.5;
        PatternTree.Relayout(p, root);
        var own = HoleDesign.PatternHoles(p, seg[0]).ToDictionary(h => h.Id, h => h.Collar);
        var taking = HoleDesign.PatternHoles(p, seg[1]).ToDictionary(h => h.Id, h => h.Collar);
        Assert.Equal(3, seg[0].Burden);

        var report = PatternTree.Resize(p, root, 5, 4.5);
        Assert.Null(report.Refusal);
        Assert.Equal(3, seg[0].Burden);
        Assert.Equal(5, seg[1].Burden);
        // the segment with a burden of its own: none of its holes moved
        foreach (var h in HoleDesign.PatternHoles(p, seg[0]))
            if (own.TryGetValue(h.Id, out var was)) Assert.Equal(was, h.Collar);
        // the one that takes the burden of the parent was laid out again
        Assert.Contains(HoleDesign.PatternHoles(p, seg[1]), h => !taking.TryGetValue(h.Id, out var was) || Vec3.Distance2D(was, h.Collar) > 1e-6);
        Assert.All(HoleDesign.PatternHoles(p, seg[1]), h => Assert.Equal(5, p.Rows.First(r => r.Id == h.RowId).Burden));
    }

    [Fact]
    public void BackToOneSegment_TheParentHoldsTheHolesAgain()
    {
        var (p, root) = Make(Field, PatternType.Staggered);
        var polygon = new PolylineEntity { Closed = true, Points = Field.ToList() };
        p.Add(polygon, p.DefaultLayer);
        root.OutlineEntityId = polygon.Id;
        var before = p.Holes.ToDictionary(h => h.Id, h => (h.Collar, h.Label));
        var rows = p.Rows.Count;
        PatternTree.Split(p, root, 3);
        Assert.Equal(polygon.Id, root.SourceOutlineId);
        PatternTree.Split(p, root, 2);
        Assert.Equal(2, PatternTree.Children(p, root).Count);
        PatternTree.Split(p, root, 1);
        Assert.False(root.IsContainer);
        Assert.Empty(PatternTree.Children(p, root));
        Assert.Single(p.Patterns);
        Assert.Equal(polygon.Id, root.OutlineEntityId);
        Assert.Null(root.SourceOutlineId);
        Assert.Equal(rows, p.Rows.Count);
        Assert.Equal(before.Count, p.Holes.Count());
        Assert.All(p.Holes, h => Assert.Equal(before[h.Id], (h.Collar, h.Label)));
        Assert.All(p.Holes, h => Assert.Equal(root.Id, h.PatternId));
    }

    [Fact]
    public void MoreSegments_HalveTheBiggest_KeepingWhatItSets()
    {
        var (p, root) = Make(Rectangle);
        PatternTree.Split(p, root, 2);
        var seg = PatternTree.Children(p, root);
        seg[0].Overrides.Diameter = 89;
        PatternTree.Relayout(p, root);
        PatternTree.Split(p, root, 3);
        var now = PatternTree.Children(p, root);
        Assert.Equal(3, now.Count);
        Assert.Equal(new[] { "A", "B", "C" }, now.Select(s => s.Name));
        Assert.Equal(new[] { 0, 1, 2 }, now.Select(s => s.Order));
        // both segments have 3 rows: the front one is halved, both halves keep its diameter
        Assert.Equal(89, now[0].Overrides.Diameter);
        Assert.Equal(89, now[1].Overrides.Diameter);
        Assert.Null(now[2].Overrides.Diameter);
        Assert.All(HoleDesign.PatternHoles(p, now[1]), h => Assert.Equal(89, h.Diameter));
    }

    [Fact]
    public void Turning_TheParent_TurnsItsSegmentsAndLines()
    {
        var (p, root) = Make(Rectangle);
        PatternTree.Split(p, root, 2);
        var count = p.Holes.Count();
        var report = PatternTree.Rotate(p, root, 30);
        Assert.Null(report.Refusal);
        Assert.All(PatternTree.Children(p, root), s => Assert.Equal(30, s.Azimuth, 9));
        Assert.Empty(HoleDesign.PatternHoles(p, root));
        PatternTree.Rotate(p, root, -30);
        Assert.Equal(count, p.Holes.Count());
    }

    [Fact]
    public void ASegmentWithItsOwnDepth_DrillsToIt()
    {
        var (p, root) = Make(Rectangle);
        PatternTree.Split(p, root, 2);
        var seg = PatternTree.Children(p, root);
        seg[1].Overrides.Depth = new DepthSpec { Mode = DepthMode.SurfaceToRect, Front = new DepthPair(90, 90), Back = new DepthPair(85, 85) };
        PatternTree.Relayout(p, root);
        Assert.All(HoleDesign.PatternHoles(p, seg[0]), h => Assert.Null(h.ToLevel));
        var back = HoleDesign.PatternHoles(p, seg[1]).ToList();
        Assert.Contains(back, h => h.ToLevel == 90);
        Assert.Contains(back, h => h.ToLevel == 85);
    }

    [Fact]
    public void AHoleChangedByHand_KeepsItsValues_ThroughCutsAndHalvings()
    {
        var (p, root) = Make(Rectangle);
        root.HoleDefaults.Diameter = 115;
        var hand = p.Holes.First();
        hand.Diameter = 76;
        PatternTree.Split(p, root, 2);
        PatternTree.Split(p, root, 3);
        Assert.Equal(76, hand.Diameter);
        PatternTree.Split(p, root, 1);
        Assert.Equal(76, hand.Diameter);
    }

    // ---- stage 3: what the panel and the cut lines on the plan use ----

    [Fact]
    public void RemovingAMiddleLine_JoinsTheTwoSegmentsBesideIt()
    {
        var (p, root) = Make(Rectangle);
        PatternTree.Split(p, root, 3);
        var seg = PatternTree.Children(p, root);
        seg[1].Overrides.Diameter = 89;
        PatternTree.Relayout(p, root);
        var before = p.Holes.ToDictionary(h => h.Id, h => h.Collar);
        PatternTree.RemoveLine(p, root, 0);
        var now = PatternTree.Children(p, root);
        Assert.Equal(2, now.Count);
        Assert.Equal(new[] { "A", "B" }, now.Select(s => s.Name));
        // the front segment keeps its parameters and takes the holes of the one behind the line
        Assert.Null(now[0].Overrides.Diameter);
        Assert.All(HoleDesign.PatternHoles(p, now[0]), h => Assert.Equal(102, h.Diameter));
        Assert.Equal(before.Count, p.Holes.Count());
        Assert.All(p.Holes, h => Assert.Equal(before[h.Id], h.Collar));
    }

    [Fact]
    public void CutLines_MustNotCrossInsideTheOutline()
    {
        var (p, root) = Make(Rectangle);
        PatternTree.Split(p, root, 3);
        Assert.True(PatternTree.LinesInOrder(root, root.SegmentLines));
        var lines = root.SegmentLines.Select(l => l.Clone()).ToList();
        // the first line tilted past the second one at one end
        lines[0].V2 = lines[1].V2 + 1;
        Assert.False(PatternTree.LinesInOrder(root, lines));
        // a line across the rows is no cut line
        lines = root.SegmentLines.Select(l => l.Clone()).ToList();
        lines[0].U2 = lines[0].U1;
        Assert.False(PatternTree.LinesInOrder(root, lines));
    }

    [Fact]
    public void AMovedLine_TakesTheHolesOfTheRowItPassed_AndThePlanSaysSoWithoutChangingTheProject()
    {
        var (p, root) = Make(Rectangle);
        PatternTree.Split(p, root, 2);
        var seg = PatternTree.Children(p, root);
        var a = HoleDesign.PatternHoles(p, seg[0]).Count();
        var line = root.SegmentLines[0].Clone();
        line.V1 = line.V2 = line.V1 + root.Burden;
        line.Row++;
        var plan = PatternTree.Plan(p, root, (q, r) => { r.SegmentLines[0] = line.Clone(); return PatternTree.Relayout(q, r); }, out var copy);
        Assert.Empty(plan.Adds);
        Assert.Empty(plan.Deletes);
        Assert.Equal(a, HoleDesign.PatternHoles(p, seg[0]).Count());
        Assert.True(HoleDesign.PatternHoles(copy, copy.FindPattern(seg[0].Id)!).Count() > a);

        root.SegmentLines[0] = line;
        PatternTree.Relayout(p, root);
        Assert.True(HoleDesign.PatternHoles(p, seg[0]).Count() > a);
        var straight = PatternTree.Straightened(root, new SegmentLine { U1 = -10, V1 = line.V1 - 0.7, U2 = 10, V2 = line.V1 + 0.9 });
        Assert.True(straight.Snapped);
        Assert.Equal(straight.V1, straight.V2);
    }

    // ---- stage 4: seams and what follows a change ----

    /// <summary>A pattern cut in two in a document, its polygon in the project.</summary>
    private static (Document Doc, Guid Root, Guid Polygon) CutInDocument(IReadOnlyList<Vec3> outline, int count = 2)
    {
        var (p, root) = Make(outline, PatternType.Staggered);
        var polygon = new PolylineEntity { Closed = true, Points = outline.ToList() };
        p.Add(polygon, p.DefaultLayer);
        root.OutlineEntityId = polygon.Id;
        PatternTree.Split(p, root, count);
        return (new Document(p), root.Id, polygon.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public void MovingOrTurningAllTheHoles_TakesTheParentItsSegmentsAndLinesAlong(double turn)
    {
        var (doc, rootId, _) = CutInDocument(Field, 3);
        var p0 = doc.Project;
        var root0 = p0.FindPattern(rootId)!;
        var lines = root0.SegmentLines.Select(l => (l.U1, l.V1, l.U2, l.V2)).ToList();
        var labels = p0.Holes.ToDictionary(h => h.Id, h => (h.Label, h.PatternId));
        var f = GeneralOps.MakeTransform(new Vec3(12, -7, 0), turn, root0.Center, 1);
        doc.Execute("Move", pr =>
        {
            foreach (var h in PatternTree.TreeHoles(pr, pr.FindPattern(rootId)!).ToList()) h.Transform(f);
            // the polygon goes with them, as when the selection is moved
            foreach (var l in pr.Entities.OfType<PolylineEntity>()) l.Transform(f);
        });
        var p = doc.Project;
        var root = p.FindPattern(rootId)!;
        Assert.True(root.IsContainer);
        Assert.Equal(lines, root.SegmentLines.Select(l => (l.U1, l.V1, l.U2, l.V2)).ToList());
        Assert.Equal(GeoMath.NormalizeBearing(turn), root.Azimuth, 9);
        Assert.All(PatternTree.Children(p, root), s => Assert.Equal(root.Azimuth, s.Azimuth, 9));
        Assert.All(p.Holes, h => Assert.Null(h.GridOffset));
        // laid out again where it is now, nothing moves
        var collars = p.Holes.ToDictionary(h => h.Id, h => h.Collar);
        var report = PatternTree.Relayout(p, root);
        Assert.Equal(0, report.Moved + report.Added + report.Deleted);
        Assert.All(p.Holes, h => Assert.True(Vec3.Distance2D(collars[h.Id], h.Collar) < 1e-6));
        Assert.All(p.Holes, h => Assert.Equal(labels[h.Id], (h.Label, h.PatternId)));
    }

    [Fact]
    public void MovingTheHolesOfOneSegment_KeepsThemAsOffsets_TheSegmentStaysInItsPart()
    {
        var (doc, rootId, _) = CutInDocument(Rectangle);
        var seg = PatternTree.Children(doc.Project, doc.Project.FindPattern(rootId)!)[0];
        var centre = seg.Center;
        doc.Execute("Move", pr =>
        {
            foreach (var h in HoleDesign.PatternHoles(pr, pr.FindPattern(seg.Id)!).ToList()) h.Collar += new Vec3(0.5, 0, 0);
        });
        var s = doc.Project.FindPattern(seg.Id)!;
        Assert.Equal(centre, s.Center);
        Assert.All(HoleDesign.PatternHoles(doc.Project, s), h => Assert.Equal(0.5, h.GridOffset!.Value.Length2D, 9));
    }

    [Fact]
    public void EditingThePolygonOfTheParent_CutsItAgain()
    {
        var (doc, rootId, polygonId) = CutInDocument(Rectangle);
        var segments = PatternTree.Children(doc.Project, doc.Project.FindPattern(rootId)!).Count;
        var before = doc.Project.Holes.Count();
        // the north side moved 8 m further out
        doc.Execute("Move vertex", pr =>
        {
            var line = (PolylineEntity)pr.FindEntity(polygonId)!;
            line.Points[2] = line.Points[2] + new Vec3(0, 8, 0);
            line.Points[3] = line.Points[3] + new Vec3(0, 8, 0);
        });
        var p = doc.Project;
        var root = p.FindPattern(rootId)!;
        Assert.True(root.IsContainer);
        Assert.Equal(segments, PatternTree.Children(p, root).Count);
        Assert.Equal(31, root.Outline.Max(q => q.Y), 9);
        Assert.True(p.Holes.Count() > before);
        Assert.All(p.Holes, h => Assert.NotEqual(root.Id, h.PatternId));
        // north is the face here (the blast goes north): the new rows are in the front segment
        Assert.True(PatternTree.Children(p, root)[0].Outline.Max(q => q.Y) > 30);
    }

    [Fact]
    public void ALengthEditedByHand_IsKeptThroughALayout_UntilTheDepthChanges()
    {
        var (p, root) = Make(Rectangle);
        root.HoleDefaults.Depth = new DepthSpec { Mode = DepthMode.SurfaceToRect, Front = new DepthPair(90, 90), Back = new DepthPair(88, 88) };
        PatternTree.SyncLegacyDepth(root);
        HoleDesign.RebuildPattern(p, root);
        var doc = new Document(p);
        var id = p.Holes.First().Id;
        doc.Execute("Hole length", pr => { var h = (Hole)pr.FindEntity(id)!; h.ToLevel = 85; h.Length = HoleLevel.Length(h)!.Value; });
        var hand = (Hole)doc.Project.FindEntity(id)!;
        Assert.True(hand.DepthManual);
        HoleDesign.RebuildPattern(doc.Project, doc.Project.Patterns[0]);
        Assert.Equal(85, hand.ToLevel);
        Assert.Equal(100 - 85 + 1, hand.Length, 9);
        Assert.All(doc.Project.Holes.Where(h => h.Id != id), h => Assert.False(h.DepthManual));
    }

    /// <summary>
    /// Two segments whose holes come close at the seam: the cut line half a metre in front of a row of the back segment, the
    /// front one finer (2.5 × 3) laid from its back side, so its last row is 1.25 m in front of the line.
    /// </summary>
    private static (BlastProject P, HolePattern Root, List<HolePattern> Seg) CloseSeam()
    {
        var (p, root) = Make(Rectangle);
        PatternTree.Split(p, root, 2);
        var v = root.PhaseV + 3 * root.Burden - 0.5;
        root.SegmentLines[0] = new SegmentLine { U1 = -50, V1 = v, U2 = 50, V2 = v };
        var seg = PatternTree.Children(p, root);
        seg[0].Overrides.Burden = 2.5;
        seg[0].Overrides.Spacing = 3;
        seg[0].AnchorSide = PatternAnchorSide.Back;
        PatternTree.Relayout(p, root);
        return (p, root, seg);
    }

    [Fact]
    public void Seams_FindTheHolesTooClose_AndTheRuleTakesTheCoarserOnesAway()
    {
        var (p, root, seg) = CloseSeam();
        var close = PatternSeams.Check(p, root).Where(x => x.Kind == SeamIssueKind.Close).ToList();
        Assert.NotEmpty(close);
        Assert.All(close, x => Assert.True(x.Distance < Math.Max(0.6 * 3, 2)));
        var count = p.Holes.Count();

        root.SeamRule = SeamRule.Drop;
        PatternTree.Relayout(p, root);
        Assert.Empty(PatternSeams.Check(p, root).Where(x => x.Kind == SeamIssueKind.Close));
        Assert.True(p.Holes.Count() < count);
        // the coarser grid (the back segment) lost them
        Assert.NotEmpty(seg[1].SeamDropped);
        Assert.Empty(seg[0].SeamDropped);
        // back to a warning: the holes come back
        root.SeamRule = SeamRule.Warn;
        PatternTree.Relayout(p, root);
        Assert.Equal(count, p.Holes.Count());
        Assert.Empty(seg[1].SeamDropped);
        Assert.Equal(p.Holes.Count(), p.Holes.Select(h => h.Label).Distinct().Count());
    }

    [Fact]
    public void Seams_OneHoleInTheMiddle_IsTakenBackByTheNextLayout()
    {
        var (p, root, seg) = CloseSeam();
        var count = p.Holes.Count();
        var close = PatternSeams.Check(p, root).Count(x => x.Kind == SeamIssueKind.Close);
        root.SeamRule = SeamRule.Merge;
        PatternTree.Relayout(p, root);
        // the finer front segment keeps its hole of each pair, moved to the middle
        Assert.NotEmpty(seg[0].SeamOffsets);
        Assert.True(p.Holes.Count() < count);
        Assert.True(PatternSeams.Check(p, root).Count(x => x.Kind == SeamIssueKind.Close) < close);
        root.SeamRule = SeamRule.Warn;
        PatternTree.Relayout(p, root);
        Assert.Empty(seg[0].SeamOffsets);
        Assert.Equal(count, p.Holes.Count());
        Assert.All(p.Holes, h => Assert.Null(h.GridOffset));
    }

    [Fact]
    public void Seams_AGap_GetsAHoleInTheMiddle_TakenAwayByTheNextLayout()
    {
        var (p, root) = Make(Rectangle);
        PatternTree.Split(p, root, 2);
        var seg = PatternTree.Children(p, root);
        // the first row of the back segment emptied by hand: nothing across the seam within 1.5 burdens
        var first = HoleDesign.PatternHoles(p, seg[1]).Min(h => h.GridJ!.Value);
        foreach (var h in HoleDesign.PatternHoles(p, seg[1]).Where(h => h.GridJ == first)) seg[1].Suppressed.Add(new GridIndex { I = h.GridI!.Value, J = first });
        PatternTree.Relayout(p, root);
        Assert.Contains(PatternSeams.Check(p, root), x => x.Kind == SeamIssueKind.Gap);
        var count = p.Holes.Count();
        root.SeamFillGaps = true;
        PatternTree.Relayout(p, root);
        Assert.NotEmpty(root.SeamAdded);
        Assert.Equal(count + root.SeamAdded.Count, p.Holes.Count());
        var added = root.SeamAdded.Select(id => H(p, id)).ToList();
        Assert.All(added, h => Assert.False(string.IsNullOrEmpty(h.Label)));
        Assert.All(added, h => Assert.Null(h.GridI));
        Assert.Equal(p.Holes.Count(), p.Holes.Select(h => h.Label).Distinct().Count());
        root.SeamFillGaps = false;
        PatternTree.Relayout(p, root);
        Assert.Empty(root.SeamAdded);
        Assert.Equal(count, p.Holes.Count());
    }

    // ---- stage 5: islands, the depth range, totals by segment ----

    private static readonly Vec3[] Island = { new(14, 8, 100), new(28, 8, 100), new(28, 16, 100), new(14, 16, 100) };

    [Fact]
    public void AnIsland_TakesTheHolesInsideItsPolygon_WithoutMovingAny()
    {
        var (p, root) = Make(Rectangle);
        var collars = p.Holes.ToDictionary(h => h.Id, h => h.Collar);
        var report = PatternTree.AddIsland(p, root, Island);
        Assert.Null(report.Refusal);
        Assert.Equal(0, report.Moved + report.Added + report.Deleted);
        Assert.True(root.IsContainer);
        Assert.Empty(root.SegmentLines);
        Assert.Equal(1, PatternTree.SegmentCount(root));
        var island = Assert.Single(PatternTree.Children(p, root), s => s.IsIsland);
        var around = Assert.Single(PatternTree.LineSegments(p, root));
        var inside = HoleDesign.PatternHoles(p, island).ToList();
        Assert.NotEmpty(inside);
        Assert.All(inside, h => Assert.True(GeoMath.PointInPolygon(Island, h.Collar.X, h.Collar.Y)));
        Assert.All(HoleDesign.PatternHoles(p, around), h => Assert.False(GeoMath.PointInPolygon(Island, h.Collar.X, h.Collar.Y)));
        Assert.All(p.Holes, h => Assert.Equal(collars[h.Id], h.Collar));
        // its own diameter, and cut lines beside it
        island.Overrides.Diameter = 76;
        PatternTree.Split(p, root, 3);
        Assert.Equal(3, PatternTree.LineSegments(p, root).Count);
        Assert.All(HoleDesign.PatternHoles(p, island), h => Assert.Equal(76, h.Diameter));
        Assert.All(PatternTree.LineSegments(p, root).SelectMany(s => HoleDesign.PatternHoles(p, s)), h => Assert.False(GeoMath.PointInPolygon(Island, h.Collar.X, h.Collar.Y)));
        // numbered after the segments between the lines
        var labels = p.Rows.SelectMany(r => r.HoleIds).Select(id => H(p, id).Label).ToList();
        Assert.Equal(Enumerable.Range(1, labels.Count).Select(k => k.ToString()), labels);
        Assert.Equal(inside.Count, HoleDesign.PatternHoles(p, island).Count());

        // taken away again: the holes go to the segments around, back to one pattern when nothing else is cut
        PatternTree.Split(p, root, 1);
        Assert.True(root.IsContainer);
        PatternTree.RemoveIsland(p, root, island.Id);
        Assert.False(root.IsContainer);
        Assert.Single(p.Patterns);
        Assert.All(p.Holes, h => Assert.Equal(root.Id, h.PatternId));
        Assert.All(p.Holes, h => Assert.Equal(collars[h.Id], h.Collar));
    }

    [Fact]
    public void EditingThePolygonOfAnIsland_CutsThePatternAgain()
    {
        var (p, root) = Make(Rectangle);
        var polygon = new PolylineEntity { Closed = true, Points = Island.ToList() };
        p.Add(polygon, p.DefaultLayer);
        PatternTree.AddIsland(p, root, Island, polygon.Id);
        var doc = new Document(p);
        var island = PatternTree.Children(doc.Project, doc.Project.FindPattern(root.Id)!).Single(s => s.IsIsland);
        var before = HoleDesign.PatternHoles(doc.Project, island).Count();
        doc.Execute("Move vertex", pr =>
        {
            var line = (PolylineEntity)pr.FindEntity(polygon.Id)!;
            line.Points[1] = new Vec3(40, 8, 100);
            line.Points[2] = new Vec3(40, 16, 100);
        });
        var now = doc.Project.FindPattern(island.Id)!;
        Assert.Equal(40, now.IslandOutline!.Max(q => q.X), 9);
        Assert.True(HoleDesign.PatternHoles(doc.Project, now).Count() > before);
    }

    [Fact]
    public void TheDepthRange_KeepsTheDepthBetweenItsNumbers_AndCountsTheHolesHeld()
    {
        var (p, root) = Make(Rectangle);
        // no grade surface: down to the level, 100 − 88 = 12 m, held at 11
        root.HoleDefaults.Depth = new DepthSpec { Mode = DepthMode.Range, Level = 88, Front = new DepthPair(8, 11) };
        PatternTree.SyncLegacyDepth(root);
        var report = HoleDesign.RebuildPattern(p, root);
        Assert.Equal(p.Holes.Count(), report.ClampedIds.Count);
        Assert.All(p.Holes, h => Assert.Equal(11 + 1, h.Length, 9));
        Assert.All(p.Holes, h => Assert.Null(h.ToLevel));
        root.HoleDefaults.Depth.Level = 91;
        report = HoleDesign.RebuildPattern(p, root);
        Assert.Empty(report.ClampedIds);
        Assert.All(p.Holes, h => Assert.Equal(9 + 1, h.Length, 9));
    }

    [Fact]
    public void TheQuantities_HaveTheTotalsOfEachSegment()
    {
        var (p, root) = Make(Rectangle);
        PatternTree.Split(p, root, 2);
        var q = Quantities.Compute(p);
        Assert.Equal(2, q.Segments.Count);
        Assert.Equal(new[] { "A", "B" }, q.Segments.Select(s => s.Segment.Name));
        Assert.Equal(q.HoleCount, q.Segments.Sum(s => s.Holes));
        Assert.Equal(p.Holes.Sum(h => h.EffectiveLength), q.Segments.Sum(s => s.DrilledMetres), 6);
        Assert.Contains("Pattern,Segment,Holes", TextFiles.ExportQuantities(q));
    }

    [Fact]
    public void DissolvingTheParent_TakesItsSegments()
    {
        var (p, root) = Make(Rectangle);
        PatternTree.Split(p, root, 3);
        HoleDesign.DissolvePattern(p, root);
        Assert.Empty(p.Patterns);
        Assert.All(p.Holes, h => Assert.Null(h.PatternId));
    }
}
