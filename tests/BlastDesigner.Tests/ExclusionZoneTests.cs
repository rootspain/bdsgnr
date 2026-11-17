using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.Rendering;
using SkiaSharp;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>The exclusion zone by sources: groups of charged holes, each with its own radius, the circles joined where they overlap.</summary>
public class ExclusionZoneTests
{
    /// <summary>A grid of charged holes, 4 m apart, with its south-west hole at <paramref name="x0"/>, <paramref name="y0"/>.</summary>
    private static List<Hole> Grid(BlastProject p, double x0, double y0, int cols = 4, int rows = 3, double length = 12, bool charged = true)
    {
        var anfo = p.Products.First(x => x.Name == "ANFO");
        var stem = p.Products.First(x => x.Kind == ProductKind.Stemming);
        var list = new List<Hole>();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var h = p.Add(new Hole { Label = $"{x0}-{r}-{c}", Collar = new Vec3(x0 + c * 4, y0 + r * 4, 100), Length = length, Diameter = 102 });
                if (charged)
                {
                    h.Decks.Add(new Deck { Top = 0, Bottom = 3, ProductId = stem.Id });
                    h.Decks.Add(new Deck { Top = 3, Bottom = length, ProductId = anfo.Id });
                }
                list.Add(h);
            }
        return list;
    }

    /// <summary>A project whose radius comes from the charge alone: no least flyrock clearance.</summary>
    private static BlastProject Project()
    {
        var p = BlastProject.CreateNew("zone");
        p.Flyrock.MinimumClearance = 0;
        return p;
    }

    [Fact]
    public void TwoPatternsFarApart_AreTwoSources_NumberedWestToEast_AndOneGridIsOne()
    {
        var p = Project();
        Grid(p, 300, 0);
        Grid(p, 0, 0);
        var sources = ExclusionZone.Sources(p);
        Assert.Equal(2, sources.Count);
        Assert.Equal(new[] { 1, 2 }, sources.Select(s => s.Number));
        Assert.True(sources[0].Holes.All(h => h.Collar.X < 100));
        Assert.All(sources, s => Assert.Equal(12, s.Holes.Count));

        var one = Project();
        Grid(one, 0, 0, 8, 6);
        Assert.Single(ExclusionZone.Sources(one));
        // the distance set in the project joins them
        p.Vibration.ExclusionMergeDistance = 400;
        Assert.Single(ExclusionZone.Sources(p));
    }

    [Fact]
    public void AnUnchargedHoleAside_IsNoSource_AndDoesNotGrowTheZone()
    {
        var p = Project();
        Grid(p, 0, 0);
        var aside = Grid(p, 2000, 0, 1, 1, charged: false)[0];
        var sources = ExclusionZone.Sources(p);
        Assert.Single(sources);
        Assert.DoesNotContain(aside, sources[0].Holes);
        var zones = ExclusionZone.Zones(sources);
        Assert.Single(zones);
        Assert.False(GeoMath.PointInPolygon(zones[0].Outline, aside.Collar.X, aside.Collar.Y));
    }

    [Fact]
    public void EachSourceHasTheRadiusOfItsOwnCharge()
    {
        var p = Project();
        Grid(p, 0, 0, length: 20);
        Grid(p, 600, 0, length: 8);
        var sources = ExclusionZone.Sources(p);
        Assert.Equal(2, sources.Count);
        // the deep holes carry more: their source reaches farther than the shallow one, the blast as a whole as far as it
        Assert.True(sources[0].Radius > sources[1].Radius * 1.1, $"{sources[0].Radius} vs {sources[1].Radius}");
        Assert.True(sources[0].Result.Mic > sources[1].Result.Mic);
        Assert.Equal(ExclusionZone.Compute(p).Radius, sources[0].Radius, 6);
    }

    [Fact]
    public void SourcesFarApart_AreTwoOutlines_TheGapBetweenThemOutside_EachWithItsRadiusArrow()
    {
        var p = Project();
        Grid(p, 0, 0);
        Grid(p, 3000, 0);
        var sources = ExclusionZone.Sources(p);
        var r = sources[0].Radius;
        Assert.True(3000 > 2 * r, $"radius {r}");
        var zones = ExclusionZone.Zones(sources);
        Assert.Equal(2, zones.Count);
        Assert.DoesNotContain(zones, z => GeoMath.PointInPolygon(z.Outline, 1500, 4));
        foreach (var z in zones)
        {
            var s = Assert.Single(z.Sources);
            var a = Assert.Single(z.Arrows);
            Assert.Equal(ZoneArrowKind.Radius, a.Kind);
            Assert.Equal(s.Radius, a.Length, 6);
            // from the northernmost hole straight north, the head on the edge of the zone
            Assert.Equal(s.Holes.Max(h => h.Collar.Y), a.From.Y, 6);
            Assert.Equal(a.From.X, a.To.X, 6);
            Assert.True(GeoMath.DistanceToPolyline2D(z.Outline, a.To, true) < r * 0.002);
        }
    }

    [Fact]
    public void SourcesCloseTogether_AreOneOutlineWithAWaist_WithItsLengthAndAWidthAcrossEachSource()
    {
        var p = Project();
        Grid(p, 0, 0);
        var probe = ExclusionZone.Sources(p)[0].Radius;
        // apart more than the merge distance, nearer than two radii: the circles overlap
        Grid(p, probe * 1.5, 0);
        var sources = ExclusionZone.Sources(p);
        Assert.Equal(2, sources.Count);
        var zone = Assert.Single(ExclusionZone.Zones(sources));
        Assert.Equal(2, zone.Sources.Count);
        // not the hull: the waist between the circles is cut in
        var hull = GeoMath.ConvexHull(zone.Outline);
        Assert.True(GeoMath.Area(zone.Outline) < GeoMath.Area(hull) * 0.99);
        // its length, and a width across each source, no radius of a source
        Assert.Equal(new[] { ZoneArrowKind.Length, ZoneArrowKind.Width, ZoneArrowKind.Width }, zone.Arrows.Select(a => a.Kind));
        var length = zone.Arrows[0];
        var span = probe * 1.5 + 12 + 2 * probe;
        Assert.InRange(length.Length, span * 0.99, span * 1.01);
        var lx = (length.To - length.From).X;
        var ly = (length.To - length.From).Y;
        foreach (var (width, source) in zone.Arrows.Skip(1).Zip(zone.Sources))
        {
            // across the circle of that source, through its middle, square to the length
            Assert.InRange(width.Length, 2 * probe * 0.98, (2 * probe + 8) * 1.01);
            var cx = source.Holes.Average(h => h.Collar.X);
            var cy = source.Holes.Average(h => h.Collar.Y);
            Assert.True(GeoMath.DistanceToPolyline2D(new List<Vec3> { width.From, width.To }, new Vec3(cx, cy, 0), false) < 1e-6);
            var dot = lx * (width.To - width.From).X + ly * (width.To - width.From).Y;
            Assert.True(Math.Abs(dot) < 1e-6 * length.Length * width.Length);
        }
        Assert.True(Math.Abs(zone.Arrows[1].From.X - zone.Arrows[2].From.X) > probe);
    }

    [Fact]
    public void ARadiusTypedIn_DrawsTheZoneOfItsSource_AndTheArrowGoesToItsAzimuth()
    {
        var p = Project();
        var west = Grid(p, 0, 0);
        Grid(p, 5000, 0);
        var calculated = ExclusionZone.Sources(p)[0].Radius;
        // kept by a hole of the source, whichever
        p.Vibration.ExclusionSources.Add(new ExclusionSourceSetting { HoleId = west[5].Id, Radius = 1000, Azimuth = 90 });
        var sources = ExclusionZone.Sources(p);
        Assert.Equal(1000, sources[0].Radius);
        Assert.Equal(calculated, sources[0].Result.Radius, 6);
        Assert.Equal(calculated, sources[1].Radius, 6);
        var zones = ExclusionZone.Zones(sources);
        var a = Assert.Single(zones[0].Arrows);
        Assert.Equal(1000, a.Length, 6);
        // to the east, from the easternmost hole, its head on the edge
        Assert.Equal(west.Max(h => h.Collar.X), a.From.X, 6);
        Assert.Equal(a.From.Y, a.To.Y, 6);
        Assert.True(a.To.X > a.From.X);
        Assert.True(GeoMath.DistanceToPolyline2D(zones.Single(z => z.Sources.Contains(sources[0])).Outline, a.To, true) < 2);
        // north still for the other
        var b = Assert.Single(zones.Single(z => z.Sources.Contains(sources[1])).Arrows);
        Assert.Equal(b.From.X, b.To.X, 6);
        Assert.True(b.To.Y > b.From.Y);
    }

    [Fact]
    public void RadiusSizing_GivesEachSourceItsRadius_EvenInAJoinedZone()
    {
        var p = Project();
        Grid(p, 0, 0);
        var r = ExclusionZone.Sources(p)[0].Radius;
        Grid(p, r * 1.5, 0);
        var sources = ExclusionZone.Sources(p);
        var zone = Assert.Single(ExclusionZone.Zones(sources, ExclusionSizing.Radius));
        Assert.Equal(new[] { ZoneArrowKind.Radius, ZoneArrowKind.Radius }, zone.Arrows.Select(x => x.Kind));
        Assert.All(zone.Arrows.Zip(sources), x => Assert.Equal(x.Second.Radius, x.First.Length, 6));
        // Radius by default; the setting is saved with the project
        Assert.Equal(ExclusionSizing.Radius, new VibrationModel().ExclusionSize);
        p.Vibration.ExclusionSize = ExclusionSizing.Length;
        p.Vibration.ExclusionSources.Add(new ExclusionSourceSetting { HoleId = sources[0].Holes[0].Id, Radius = 700, Azimuth = 45 });
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(p));
        Assert.Equal(ExclusionSizing.Length, back.Vibration.ExclusionSize);
        var set = Assert.Single(back.Vibration.ExclusionSources);
        Assert.Equal((700.0, 45.0), (set.Radius!.Value, set.Azimuth));
    }

    [Fact]
    public void TheEdgeOfAZone_IsTheRadiusFromTheNearestHole()
    {
        const double r = 750;
        var single = ExclusionZone.Outlines(new[] { ((IReadOnlyList<Vec3>)new[] { new Vec3(500000, 6000000, 120) }, r) });
        var circle = Assert.Single(single);
        Assert.True(circle.Count is > 30 and < 400, $"{circle.Count} vertices");
        Assert.All(circle, v => Assert.InRange(Vec3.Distance2D(v, new Vec3(500000, 6000000, 0)), r * 0.998, r * 1.002));
        Assert.All(circle, v => Assert.Equal(120, v.Z, 6));

        var p = Project();
        var holes = Grid(p, 0, 0, 6, 4);
        var outline = Assert.Single(ExclusionZone.Outlines(new[] { ((IReadOnlyList<Vec3>)holes.Select(h => h.Collar).ToList(), r) }));
        Assert.All(outline, v => Assert.True(holes.Min(h => Vec3.Distance2D(h.Collar, v)) > r * 0.998));
    }

    [Fact]
    public void TheArrowsOfAZone_AreSavedAndOpened_AndMoveWithIt()
    {
        var p = Project();
        var zone = new PolylineEntity
        {
            Kind = LineKind.ExclusionZone, Closed = true, Points = { new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 10, 0) },
            ZoneArrows = new() { new ZoneArrow { From = new Vec3(1, 2, 0), To = new Vec3(1, 7, 0), Kind = ZoneArrowKind.Radius } }
        };
        p.Add(zone);
        p.Vibration.ExclusionMergeDistance = 25;
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(p));
        var a = Assert.Single(back.Entities.OfType<PolylineEntity>().Single(e => e.Kind == LineKind.ExclusionZone).ZoneArrows!);
        Assert.Equal(new Vec3(1, 7, 0), a.To);
        Assert.Equal(5, a.Length, 9);
        Assert.Equal(25, back.Vibration.ExclusionMergeDistance);
        // a line of another kind has none in the file
        Assert.Null(new PolylineEntity().ZoneArrows);

        zone.Translate(new Vec3(100, 0, 0));
        Assert.Equal(new Vec3(101, 2, 0), zone.ZoneArrows![0].From);
        Assert.Equal(new Vec3(101, 7, 0), zone.ZoneArrows[0].To);
    }

    /// <summary>Rows of the column through the middle of an arrow that are not the white ground: the height of its figure plate.</summary>
    private static int PlateHeight(double scale)
    {
        var cam = new Camera { Width = 400, Height = 200, Scale = scale, Target = new Vec3(0, 0, 0) };
        using var bitmap = new SKBitmap(400, 200);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        var arrow = new ZoneArrow { From = new Vec3(-30, 0, 0), To = new Vec3(30, 0, 0), Kind = ZoneArrowKind.Length };
        PlanRenderer.DrawZoneArrows(canvas, cam, new[] { arrow }, SKColors.Red, SKColors.White);
        var (x, _) = cam.ToScreen(Vec3.Zero);
        var rows = Enumerable.Range(0, 200).Where(y => bitmap.GetPixel((int)x, y) != SKColors.White).ToList();
        return rows.Max() - rows.Min();
    }

    [Fact]
    public void TheFiguresOnTheArrows_AreTheSameSizeAtAnyZoom()
    {
        var near = PlateHeight(5);
        var far = PlateHeight(2);
        Assert.InRange(near, PlanRenderer.ZoneArrowTextSize, PlanRenderer.ZoneArrowTextSize * 2.5);
        Assert.Equal(near, far);
    }
}
