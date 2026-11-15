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
}
