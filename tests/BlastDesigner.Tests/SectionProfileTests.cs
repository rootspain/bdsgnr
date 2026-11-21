using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.Tests;

/// <summary>
/// The section along a line, for its labels: the holes near the line in their order along it, the spacing along the
/// line, the levels of the sample (collar 373, length 11.5, subdrill 1.0: grade 362.5), the top of the frame.
/// </summary>
public class SectionProfileTests
{
    static BlastProject Row(int count = 5, double spacing = 3.6, double offset = 0)
    {
        var p = BlastProject.CreateNew();
        for (int i = 0; i < count; i++)
            p.Add(new Hole { Label = (174 - i).ToString(), Collar = new Vec3(i * spacing, offset, 373), Length = 11.5, Subdrill = 1.0 });
        return p;
    }

    [Fact]
    public void TheHolesNearTheLine_InTheirOrderAlongIt_TheSpacingAlongTheLine()
    {
        var p = Row();
        // a hole off the line, beyond the half width
        p.Add(new Hole { Label = "far", Collar = new Vec3(5, 6, 373), Length = 11.5 });
        // the line drawn from the far end: the holes from that end
        var profile = SectionProfile.Build(p, new Vec3(20, 0, 373), new Vec3(-5, 0, 373), 2);
        Assert.Equal(new[] { "170", "171", "172", "173", "174" }, profile.Holes.Select(h => h.Hole.Label));
        Assert.Equal(3.6, profile.Spacing!.Value, 6);
        // a line at an angle to the row: the spacing along the line, not between the collars
        var slanted = SectionProfile.Build(Row(2, 4), new Vec3(0, 0, 373), new Vec3(3, 4, 373), 10);
        Assert.Equal(4 * 3 / 5.0, slanted.Spacing!.Value, 6);
    }

    [Fact]
    public void Levels_OfTheSample_TheTopOfTheFrameOverTheNumbers_CloseLevelsMerge()
    {
        var profile = SectionProfile.Build(Row(), new Vec3(-5, 0, 373), new Vec3(20, 0, 373), 2);
        var first = profile.Holes[0];
        Assert.Equal(362.5, first.GradeZ, 6);
        Assert.Equal(361.5, first.Toe.Z, 6);
        // the band of the numbers 1.6 m at this scale: 373 + 1.6 up to 375.0; at a larger scale less
        Assert.Equal(375.0, SectionProfile.FrameTop(373, 1.6));
        Assert.Equal(373.5, SectionProfile.FrameTop(373, 0.3));
        Assert.Equal(373.0, SectionProfile.FrameTop(372.6, 0.4));
        Assert.Equal(new[] { 375.0, 373.0, 362.5 }, SectionProfile.Distinct(new[] { 375.0, 373.0, 373.2, 362.5, 362.4 }));
        // collars of one bench, a little uneven: one level, their mean
        var uneven = Row(3);
        ((Hole)uneven.Entities.OfType<Hole>().First()).Collar = new Vec3(0, 0, 373.2);
        var levels = SectionProfile.Build(uneven, new Vec3(-5, 0, 373), new Vec3(20, 0, 373), 2).CollarLevels();
        Assert.Equal(373.0667, Assert.Single(levels), 3);
    }

    [Fact]
    public void AnAngledHole_ItsToeWhereItGoes()
    {
        var p = BlastProject.CreateNew();
        p.Add(new Hole { Label = "1", Collar = new Vec3(0, 0, 373), Length = 10, Angle = 20, Bearing = 90 });
        var hole = SectionProfile.Build(p, new Vec3(-5, 0, 373), new Vec3(20, 0, 373), 2).Holes.Single();
        Assert.True(hole.Toe.Z > 373 - 10 && hole.Toe.Z < 373 - 9);
        Assert.NotEqual(0, hole.Toe.X - hole.Collar.X, 3);
    }

    [Fact]
    public void Dimensions_OneLengthOrTheShortestAndTheLongest_NeverCrossingTheSpacing()
    {
        var five = new List<float> { 0, 100, 200, 300, 400 };
        // lengths alike: the first one, the spacing between the second and the third
        var (one, at) = SectionAnnotations.LayoutDimensions(five, new double[] { 11.5, 11.5, 11.52, 11.5, 11.5 }, 12);
        Assert.Equal(new[] { 0 }, one); Assert.Equal(1, at);
        // lengths differing: the shortest and the longest, the spacing clear of both
        var (two, at2) = SectionAnnotations.LayoutDimensions(five, new double[] { 10.3, 10.6, 10.9, 11.2, 11.8 }, 12);
        Assert.Equal(new[] { 0, 4 }, two);
        Assert.Equal(1, at2);
        var (both, at3) = SectionAnnotations.LayoutDimensions(new List<float> { 0, 100, 200 }, new double[] { 11, 10, 12 }, 12);
        Assert.Equal(new[] { 1, 2 }, both); Assert.Equal(0, at3);
        // two holes of two lengths: no room for both beside the spacing, the longest only, right of the last
        var (only, at4) = SectionAnnotations.LayoutDimensions(new List<float> { 0, 100 }, new double[] { 10, 11 }, 12);
        Assert.Equal(new[] { 1 }, only); Assert.Equal(0, at4);
        // two alike: the length right of the second
        var (alike, at5) = SectionAnnotations.LayoutDimensions(new List<float> { 0, 100 }, new double[] { 10, 10 }, 12);
        Assert.Equal(new[] { 1 }, alike); Assert.Equal(0, at5);
        // in every case no length stands between the two holes of the spacing
        foreach (var (lengths, k, xs) in new[] { (one, at, five), (two, at2, five) })
            Assert.All(lengths, i => Assert.False(SectionAnnotations.DimensionX(xs, i, 12) is var x && x > xs[k!.Value] && x < xs[k.Value + 1]));
    }

    [Fact]
    public void Lengths_AreWrittenWithOneDecimal_AndAPoint() =>
        Assert.Equal(new[] { "11.5", "3.6", "1.0", "375.0" }, new[] { 11.5, 3.6, 1.0, 375.0 }.Select(SectionAnnotations.Metres));

    [Fact]
    public void TheLabels_AreDrawnOverTheSection_AndLeftOutWhenTurnedOff()
    {
        var p = Row();
        p.Add(new PolylineEntity { Kind = LineKind.CrossSection, Label = "A", Points = { new Vec3(-5, 0, 373), new Vec3(20, 0, 373) } });
        int Ink(SectionVisibility shown)
        {
            using var bitmap = new SKBitmap(600, 400);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            Reports.DrawSection(canvas, new SKRect(0, 0, 600, 400), p, new Vec3(-5, 0, 373), new Vec3(20, 0, 373), 2, shown, Reports.SectionTitle("A–A"));
            return bitmap.Pixels.Count(c => c.Red < 100 && c.Green < 100 && c.Blue < 100);
        }
        var all = Ink(new SectionVisibility());
        var none = Ink(new SectionVisibility { ShowNumbers = false, ShowLevels = false, ShowDimensions = false });
        var noDimensions = Ink(new SectionVisibility { ShowDimensions = false });
        Assert.True(all > noDimensions && noDimensions > none, $"{all} {noDimensions} {none}");
    }

    [Fact]
    public void ThePlan_IsDrawnInsideTheFrameOnly()
    {
        // a ground surface far wider than the row: in the section it is cut to the frame
        SKBitmap Sheet(bool surface)
        {
            var p = Row();
            if (surface)
                p.Add(new SurfaceEntity { Vertices = { new Vec3(-200, -50, 370), new Vec3(200, -50, 370), new Vec3(200, 50, 370), new Vec3(-200, 50, 370) }, Triangles = { 0, 1, 2, 0, 2, 3 } });
            var bitmap = new SKBitmap(600, 400);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            Reports.DrawSection(canvas, new SKRect(0, 0, 600, 400), p, new Vec3(-5, 0, 373), new Vec3(20, 0, 373), 2, new SectionVisibility(), Reports.SectionTitle("A–A"));
            return bitmap;
        }
        using var bare = Sheet(false);
        using var cut = Sheet(true);
        int Differ(int x0, int x1) { int n = 0; for (int x = x0; x < x1; x++) for (int y = 0; y < 400; y++) if (bare.GetPixel(x, y) != cut.GetPixel(x, y)) n++; return n; }
        // the surface is there, inside the frame
        Assert.True(Differ(0, 600) > 0);
        // and nothing of it at the sides, beyond the frame (with its levels on the left)
        Assert.Equal(0, Differ(0, 20));
        Assert.Equal(0, Differ(580, 600));
    }

    [Fact]
    public void HolesLeftOutOfTheSheet_HaveNoNumbersNorDimensions()
    {
        var p = Row();
        int Ink(BlastProject project, SectionVisibility shown)
        {
            using var bitmap = new SKBitmap(600, 400);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            Reports.DrawSection(canvas, new SKRect(0, 0, 600, 400), project, new Vec3(-5, 0, 373), new Vec3(20, 0, 373), 2, shown);
            return bitmap.Pixels.Count(c => c.Red < 100 && c.Green < 100 && c.Blue < 100);
        }
        // the holes hidden on the sheet: as a blast with no holes, their labels left out too
        var none = Ink(Row(0), new SectionVisibility());
        Assert.Equal(none, Ink(p, new SectionVisibility { HiddenKinds = { new Hole().KindName } }));
        Assert.True(Ink(p, new SectionVisibility()) > none);
        Assert.Empty(SectionProfile.Build(p, new Vec3(-5, 0, 373), new Vec3(20, 0, 373), 2, p.Holes.Select(h => h.Id).ToHashSet()).Holes);
    }

    [Fact]
    public void TheTiesAndTheInitiationPoints_AreNeverInASection_TheHalfWidthIs4()
    {
        Assert.Equal(4, Reports.SectionHalfWidth(BlastProject.CreateNew()));
        byte[] Pixels(bool ties)
        {
            var p = Row(3);
            if (ties)
            {
                var holes = p.Holes.ToList();
                BlastDesigner.Engine.Timing.Connect(p, holes[0].Id, holes[1].Id, p.Products.First(x => x.Kind == ProductKind.SurfaceConnector));
                BlastDesigner.Engine.Timing.Connect(p, holes[1].Id, holes[2].Id, p.Products.First(x => x.Kind == ProductKind.SurfaceConnector));
                p.Add(new InitiationPoint { Position = holes[0].Collar, HoleId = holes[0].Id });
            }
            using var bitmap = new SKBitmap(600, 400);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            Reports.DrawSection(canvas, new SKRect(0, 0, 600, 400), p, new Vec3(-5, 0, 373), new Vec3(12, 0, 373), 2, new SectionVisibility { ShowNumbers = false, ShowLevels = false, ShowDimensions = false });
            return bitmap.Bytes;
        }
        Assert.Equal(Pixels(false), Pixels(true));
    }
}
