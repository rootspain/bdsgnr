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
