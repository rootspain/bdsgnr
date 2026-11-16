using System.IO.Compression;
using System.Text.Json.Nodes;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;

namespace BlastDesigner.Tests;

/// <summary>The collar and depth polygons of a pattern: elevations at the vertices of each segment, flat triangles between them, shared at the seams.</summary>
public class PatternSurfaceTests
{
    private static readonly Vec3[] Rectangle = { new(0, 0, 100), new(41, 0, 100), new(41, 23, 100), new(0, 23, 100) };

    /// <summary>A plane: the polygons of a rectangle with these elevations at its corners are flat, so every point of them is on it.</summary>
    private static double Collar(double x, double y) => 100 + 0.1 * x + 0.05 * y;
    /// <summary>The depth from the collar: a plane too.</summary>
    private static double Depth(double x, double y) => 10 - 0.02 * x + 0.04 * y;

    private static (BlastProject P, HolePattern Pat) Make(double angle = 0)
    {
        var p = BlastProject.CreateNew();
        var holes = HoleDesign.FillPolygon(Rectangle, new PatternParameters { Burden = 4, Spacing = 4.5, Pattern = PatternType.Rectangular, StartNumber = 1 },
            new HoleParameters { BenchHeight = 10, Subdrill = 1, Angle = angle, Bearing = 30 }, out var rows, out var pat);
        var layer = p.GetOrCreateLayer(StandardLayers.Holes);
        foreach (var h in holes) p.Add(h, layer);
        p.Rows.AddRange(rows);
        p.Patterns.Add(pat);
        return (p, pat);
    }

    private static void Planes(HolePattern root)
    {
        for (int i = 0; i < root.Outline.Count; i++)
            PatternSurfaces.SetVertex(root, $"O:{i}", Collar(root.Outline[i].X, root.Outline[i].Y), Depth(root.Outline[i].X, root.Outline[i].Y));
        root.HoleDefaults.Depth.Collars = CollarSource.Polygon;
        root.HoleDefaults.Depth.Mode = DepthMode.Polygon;
        PatternTree.SyncLegacyDepth(root);
    }

    [Fact]
    public void TheHoles_StandOnTheCollarPolygon_AndGoDownToTheDepthPolygon()
    {
        var (p, root) = Make();
        Planes(root);
        HoleDesign.RebuildPattern(p, root);
        foreach (var h in p.Holes)
        {
            Assert.Equal(Collar(h.Collar.X, h.Collar.Y), h.Collar.Z, 6);
            // from the collar down by the depth there, the subdrill below
            Assert.Equal(h.Collar.Z - Depth(h.Collar.X, h.Collar.Y), h.ToLevel!.Value, 5);
            Assert.Equal(Depth(h.Collar.X, h.Collar.Y) + h.Subdrill, h.Length, 5);
        }
    }

    [Fact]
    public void AnInclinedHole_GoesDownByTheDepthUnderItsCollar()
    {
        var (p, root) = Make(angle: 20);
        Planes(root);
        HoleDesign.RebuildPattern(p, root);
        var cos = Math.Cos(20 * Math.PI / 180);
        foreach (var h in p.Holes)
        {
            Assert.Equal(h.Collar.Z - Depth(h.Collar.X, h.Collar.Y), h.ToLevel!.Value, 5);
            Assert.Equal(Depth(h.Collar.X, h.Collar.Y) / cos + h.Subdrill, h.Length, 5);
        }
    }

    [Fact]
    public void AVertexWithoutElevation_TakesItFromItsNeighboursAlongTheOutline()
    {
        var (p, root) = Make();
        root.HoleDefaults.Depth.Collars = CollarSource.Polygon;
        root.HoleDefaults.Depth.Mode = DepthMode.Polygon;
        // none set: collars at the level of the grid, depths the bench height
        var v = Assert.Single(PatternSurfaces.Polygons(p, root, null));
        Assert.All(v, x => Assert.Equal((100.0, 10.0), (x.Collar, x.Depth)));
        // two corners set: the others between them along the perimeter
        PatternSurfaces.SetVertex(root, "O:0", 100, null);
        PatternSurfaces.SetVertex(root, "O:2", 106.4, null);
        v = PatternSurfaces.Polygons(p, root, null)[0];
        // corner 1 is 41 m from corner 0 and 23 m from corner 2
        Assert.Equal(100 + 6.4 * 41 / 64, v[1].Collar, 9);
        Assert.Equal(100 + 6.4 * 23 / 64, v[3].Collar, 9);
        Assert.True(v[0].CollarSet && !v[1].CollarSet);
    }

    [Fact]
    public void NeighbouringSegments_ShareTheirVerticesAtTheSeam()
    {
        var (p, root) = Make();
        PatternTree.Split(p, root, 2);
        Planes(root);
        PatternSurfaces.SetVertex(root, "L:0:1", 112, 15);
        PatternTree.Relayout(p, root);
        var seg = PatternTree.Children(p, root);
        var a = PatternSurfaces.Polygons(p, seg[0], null)[0];
        var b = PatternSurfaces.Polygons(p, seg[1], null)[0];
        var seamA = a.Single(x => x.Key == "L:0:1");
        var seamB = b.Single(x => x.Key == "L:0:1");
        Assert.Equal(seamA.Point.X, seamB.Point.X, 9);
        Assert.Equal((112.0, 15.0), (seamA.Collar, seamA.Depth));
        Assert.Equal((112.0, 15.0), (seamB.Collar, seamB.Depth));
        // the other end of the line is not set: it is on the side of the outline between its corners, the same for both
        Assert.Equal(a.Single(x => x.Key == "L:0:2").Collar, b.Single(x => x.Key == "L:0:2").Collar, 9);
        // the holes near the raised end are higher in both segments
        Assert.Contains(HoleDesign.PatternHoles(p, seg[0]), h => h.Collar.Z > Collar(h.Collar.X, h.Collar.Y) + 0.5);
        Assert.Contains(HoleDesign.PatternHoles(p, seg[1]), h => h.Collar.Z > Collar(h.Collar.X, h.Collar.Y) + 0.5);
    }

    [Fact]
    public void AMovedCutLine_KeepsTheElevationsOfItsEnds_AndRemovingALineDropsThem()
    {
        var (p, root) = Make();
        PatternTree.Split(p, root, 3);
        Planes(root);
        PatternSurfaces.SetVertex(root, "L:1:1", 120, 18);
        var line = root.SegmentLines[0];
        line.Row--;
        PatternTree.Relayout(p, root);
        var c = PatternSurfaces.Polygons(p, PatternTree.Children(p, root)[2], null)[0].Single(x => x.Key == "L:1:1");
        Assert.Equal((120.0, 18.0), (c.Collar, c.Depth));
        // the first line taken away: the second is the first now, its ends with it
        PatternTree.RemoveLine(p, root, 0);
        Assert.Contains(root.SurfaceVertices, v => v.Key == "L:0:1" && v.Collar == 120);
        Assert.DoesNotContain(root.SurfaceVertices, v => v.Key.StartsWith("L:1:"));
        // a line put in before it: its number goes up again
        PatternTree.Split(p, root, 3);
        Assert.Single(root.SurfaceVertices, v => v.Collar == 120 && v.Key.StartsWith("L:"));
    }

    [Fact]
    public void ASchema4FileWithTheRectangle_OpensWithPolygons_TheHolesWhereTheyWere()
    {
        var (p, root) = Make();
        // the four numbers of a plane: the old bilinear depth and the flat triangles agree everywhere
        root.HoleDefaults.Depth = new DepthSpec { Mode = DepthMode.RectToLevel, Level = 80, Front = new DepthPair(10, 12), Back = new DepthPair(11, 13) };
        PatternTree.SyncLegacyDepth(root);
        HoleDesign.RebuildPattern(p, root);
        var before = p.Holes.ToDictionary(h => h.Id, h => (h.Collar, h.Length, h.ToLevel));

        var json = JsonNode.Parse(ProjectJson.Serialize(p))!.AsObject();
        json["schemaVersion"] = 4;
        foreach (var x in json["patterns"]!.AsArray()) x!.AsObject().Remove("surfaceVertices");
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using (var s = zip.CreateEntry("manifest.json").Open())
                s.Write(System.Text.Encoding.UTF8.GetBytes($"{{\"format\":\"{ProjectFile.FormatId}\",\"schemaVersion\":4,\"application\":\"Blast Designer\",\"saved\":\"2026-01-01T00:00:00\",\"name\":\"x\"}}"));
            using (var s = zip.CreateEntry("project.json").Open())
                s.Write(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        }
        ms.Position = 0;
        var back = ProjectFile.Read(ms);
        var bp = back.Patterns.Single();
        Assert.Equal(DepthMode.Polygon, bp.HoleDefaults.Depth.Mode);
        Assert.Equal(CollarSource.Polygon, bp.HoleDefaults.Depth.Collars);
        Assert.Equal(4, bp.SurfaceVertices.Count);
        // collars on the rectangle: the numbers were the depths from the collar to the level
        Assert.All(bp.SurfaceVertices, v => Assert.Equal(v.Collar!.Value - 80, v.Depth!.Value, 6));
        HoleDesign.RebuildPattern(back, bp, PatternRebuildOptions.For(back));
        // the old depth of a node was rounded to 0.1 m, the polygons are not: within 5 cm
        foreach (var h in back.Holes)
        {
            var (collar, length, level) = before[h.Id];
            Assert.True(Math.Abs(collar.Z - h.Collar.Z) <= 0.05 + 1e-6, $"{collar.Z} → {h.Collar.Z}");
            Assert.True(Math.Abs(length - h.Length) <= 0.05 + 1e-6);
            Assert.Equal(level, h.ToLevel);
            Assert.Equal(collar.X, h.Collar.X, 9);
        }
    }
}
