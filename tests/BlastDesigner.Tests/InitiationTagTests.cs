using SkiaSharp;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Rendering;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>
/// The side of the tag of an initiation point (<see cref="PlanRenderer.InitiationTagSide"/>): away from the holes around it,
/// across a row when they stand on both sides, to the south inside a pattern, to the right with no hole near; and the tag
/// clear of the circle of its hole.
/// </summary>
public class InitiationTagTests
{
    [Fact]
    public void TheTagIsDrawnInTheColourOfThePoint_ByLayerRedOrTeal()
    {
        var p = BlastProject.CreateNew();
        var ip = p.Add(new InitiationPoint { Position = new Vec3(0, 0, 0), StartTimeMs = 1 });
        var cam = new Camera { Width = 200, Height = 100, Pitch = 90, Scale = 10, Target = new Vec3(0, 0, 0) };
        SKColor Body()
        {
            using var bitmap = new SKBitmap(200, 100);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            PlanRenderer.Render(canvas, cam, p, new SceneOptions());
            // the body of the tag, beside its time (no hole near: the tag goes right)
            var tag = PlanRenderer.InitiationTag(cam, p, ip);
            return bitmap.GetPixel((int)Math.Round(tag[1].X + 2), (int)Math.Round((tag[1].Y + tag[0].Y) / 2 + 1));
        }
        var byLayer = Body();
        Assert.True(byLayer.Red > 150 && byLayer.Green < 100, $"by layer the tag is red: {byLayer}");
        ip.Color = 0xFF1D4ED8;
        var own = Body();
        Assert.True(own.Blue > 150 && own.Red < 100, $"with a colour of its own the tag takes it: {own}");
    }

    [Fact]
    public void TheTagWritesTheStartTime_AndALongOneMakesItLonger()
    {
        var p = BlastProject.CreateNew();
        var ip = p.Add(new InitiationPoint { Position = new Vec3(0, 0, 0), StartTimeMs = 0 });
        var cam = new Camera { Width = 800, Height = 600, Pitch = 90, Scale = 10, Target = new Vec3(0, 0, 0) };
        Assert.Equal("0", PlanRenderer.InitiationTagText(ip));
        var shortTag = PlanRenderer.InitiationTag(cam, p, ip);
        ip.StartTimeMs = 12.5;
        Assert.Equal("12.5", PlanRenderer.InitiationTagText(ip));
        // with the delay of the lead-in
        ip.LeadInDelayMs = 25;
        Assert.Equal("37.5", PlanRenderer.InitiationTagText(ip));
        ip.LeadInDelayMs = 0;
        ip.StartTimeMs = 123456;
        var longTag = PlanRenderer.InitiationTag(cam, p, ip);
        // no hole near: the tag goes right, the body longer for the long time
        float Length(SKPoint[] t) => Math.Abs(t[2].X - t[1].X);
        Assert.True(Length(longTag) > Length(shortTag));
    }

    /// <summary>A 3 × 3 pattern, 5 m apart, from (0, 0) (south-west) to (10, 10); a plan camera, north up.</summary>
    private static (BlastProject P, Camera Cam, Hole[,] Holes) Pattern()
    {
        var p = BlastProject.CreateNew();
        var holes = new Hole[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                holes[i, j] = p.Add(new Hole { Collar = new Vec3(i * 5, j * 5, 100), Length = 10, Diameter = 102 });
        return (p, new Camera { Width = 400, Height = 300, Pitch = 90, Scale = 10, Target = new Vec3(5, 5, 100) }, holes);
    }

    private static (float, float) Side(BlastProject p, Camera cam, Hole h)
    {
        var ip = p.Add(new InitiationPoint { Position = h.Collar, HoleId = h.Id });
        return PlanRenderer.InitiationTagSide(cam, p, ip);
    }

    [Fact]
    public void InsideAPatternTheTagGoesSouth()
    {
        var (p, cam, holes) = Pattern();
        // south of the plan is down the screen with north up
        Assert.Equal((0f, 1f), Side(p, cam, holes[1, 1]));
    }

    [Fact]
    public void OnAnEdgeTheTagGoesOutOfThePattern()
    {
        var (p, cam, holes) = Pattern();
        // the middle of the south row: neighbours east and west, the row to the north taken: out to the south
        Assert.Equal((0f, 1f), Side(p, cam, holes[1, 0]));
        // the middle of the north row: out to the north (up the screen)
        Assert.Equal((0f, -1f), Side(p, cam, holes[1, 2]));
        // the middle of the west column: out to the west (left)
        Assert.Equal((-1f, 0f), Side(p, cam, holes[0, 1]));
        // the middle of the east column: out to the east (right)
        Assert.Equal((1f, 0f), Side(p, cam, holes[2, 1]));
    }

    [Fact]
    public void AtACornerTheTagTurnsAwayFromBothNeighbours()
    {
        var (p, cam, holes) = Pattern();
        // south-west: neighbours east and north; the tag towards neither of them
        var (ux, uy) = Side(p, cam, holes[0, 0]);
        Assert.True(ux <= 0 && uy >= 0, $"({ux}, {uy})");
    }

    [Fact]
    public void ARowOfHolesPutsTheTagAcrossIt()
    {
        var p = BlastProject.CreateNew();
        var row = Enumerable.Range(0, 5).Select(i => p.Add(new Hole { Collar = new Vec3(i * 5, 0, 100), Length = 10 })).ToList();
        var cam = new Camera { Width = 400, Height = 300, Pitch = 90, Scale = 10, Target = new Vec3(10, 0, 100) };
        // the middle of a lone row: no hole on either side across it, up first
        Assert.Equal((0f, -1f), Side(p, cam, row[2]));
        // its end: away from the row
        Assert.Equal((-1f, 0f), Side(p, cam, row[0]));
    }

    [Fact]
    public void AHoleAloneHasItsTagOnTheRight()
    {
        var p = BlastProject.CreateNew();
        var h = p.Add(new Hole { Collar = new Vec3(0, 0, 100), Length = 10 });
        Assert.Equal((1f, 0f), Side(p, new Camera { Width = 400, Height = 300, Pitch = 90, Scale = 10, Target = h.Collar }, h));
    }

    [Fact]
    public void TheTagStaysClearOfTheCircleOfItsHoleCloseIn()
    {
        var p = BlastProject.CreateNew();
        var h = p.Add(new Hole { Collar = new Vec3(0, 0, 100), Length = 10, Diameter = 250 });
        var ip = p.Add(new InitiationPoint { Position = h.Collar, HoleId = h.Id });
        var cam = new Camera { Width = 400, Height = 300, Pitch = 90, Scale = 400, Target = h.Collar };
        var (x, y) = cam.ToScreen(h.Collar);
        var tip = PlanRenderer.InitiationTag(cam, p, ip)[0];
        var radius = h.RadiusM * cam.Scale;
        Assert.True(Math.Sqrt((tip.X - x) * (tip.X - x) + (tip.Y - y) * (tip.Y - y)) > radius + 3, "the point of the tag is inside the hole's circle");
    }
}
