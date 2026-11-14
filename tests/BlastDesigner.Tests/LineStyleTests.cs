using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.IO;
using BlastDesigner.Rendering;
using SkiaSharp;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>Line weights and types as AutoCAD has them: on the screen, and through a DXF file and back.</summary>
public class LineStyleTests
{
    [Fact]
    public void AWeightIsAPixelAQuarterMillimetre_ThinnerTooThickerInProportion()
    {
        Assert.Equal(1, LineTypes.WeightPixels(0));
        Assert.Equal(1, LineTypes.WeightPixels(13));
        Assert.Equal(1, LineTypes.WeightPixels(25));
        Assert.Equal(2, LineTypes.WeightPixels(50));
        Assert.Equal(4, LineTypes.WeightPixels(100));
    }

    [Fact]
    public void DashesAreInDrawingUnits_ADotIsAPixel_TooFineIsSolid()
    {
        var dashed = LineTypes.Standard.First(t => t.Name == "DASHED");
        Assert.Equal(new[] { 5f, 2.5f }, LineTypes.ScreenPattern(dashed, 10));
        // ten times closer, ten times longer
        Assert.Equal(new[] { 50f, 25f }, LineTypes.ScreenPattern(dashed, 100));
        var dot = LineTypes.Standard.First(t => t.Name == "DOT");
        Assert.Equal(new[] { 1f, 5f }, LineTypes.ScreenPattern(dot, 20));
        // far away the pattern is finer than the eye: drawn solid, as AutoCAD does
        Assert.Null(LineTypes.ScreenPattern(dashed, 1));
        // a pattern starting with a gap starts with its dash
        var gapFirst = new LineTypeDef { Name = "G", Pattern = new() { -1, 2 } };
        Assert.Equal(new[] { 20f, 10f }, LineTypes.ScreenPattern(gapFirst, 10));
        Assert.Null(LineTypes.ScreenPattern(null, 10));
    }

    [Fact]
    public void ByLayerAndContinuousAreSolid_AProjectTypeComesBeforeTheStandardOne()
    {
        var p = BlastProject.CreateNew("lt");
        Assert.Null(LineTypes.Find(p, null));
        Assert.Null(LineTypes.Find(p, "Continuous"));
        Assert.Null(LineTypes.Find(p, "ByLayer"));
        Assert.Equal(new[] { 0.5, -0.25 }, LineTypes.Find(p, "dashed")!.Pattern);
        p.LineTypes.Add(new LineTypeDef { Name = "DASHED", Pattern = new() { 3, -1 } });
        Assert.Equal(new[] { 3.0, -1 }, LineTypes.Find(p, "DASHED")!.Pattern);
    }

    [Fact]
    public void ADrawingGoesOutAndComesBackWithItsWeightsTypesAndScales()
    {
        var p = BlastProject.CreateNew("lt");
        p.LineTypeScale = 5;
        p.LineTypes.Add(new LineTypeDef { Name = "MINE_EDGE", Description = "Edge of the pit", Pattern = new() { 2, -1, 0, -1 } });
        var layer = p.GetOrCreateLayer("Design lines", 0xFF16A34A, "Design");
        layer.LineWeight = 50;
        layer.LineType = "HIDDEN";
        p.Add(new PolylineEntity { Points = { new(0, 0, 0), new(10, 0, 0), new(10, 5, 0) }, LineWeight = 35, LineType = "CENTER", LineTypeScale = 2 }, layer);
        p.Add(new PolylineEntity { Points = { new(0, 10, 0), new(10, 10, 0) }, LineType = "MINE_EDGE" }, layer);
        p.Add(new CircleEntity { Center = new Vec3(5, 5, 0), Radius = 2 }, layer);
        var text = CadFiles.ExportDxf(p, p.Entities);
        Assert.Contains("AC1015", text);
        var path = Path.Combine(Path.GetTempPath(), $"lt-{Guid.NewGuid():N}.dxf");
        try
        {
            File.WriteAllText(path, text);
            var back = CadFiles.ReadCad(path, new CadImportOptions());
            var lines = back.Items.Select(x => x.Entity).OfType<PolylineEntity>().ToList();
            var centre = lines.Single(l => l.Points.Count == 3);
            Assert.Equal(35, centre.LineWeight);
            Assert.Equal("CENTER", centre.LineType, ignoreCase: true);
            Assert.Equal(2, centre.LineTypeScale, 9);
            Assert.Equal("MINE_EDGE", lines.Single(l => l.Points.Count == 2).LineType, ignoreCase: true);
            // by layer stays by layer
            var circle = back.Items.Select(x => x.Entity).OfType<CircleEntity>().Single();
            Assert.Null(circle.LineWeight);
            Assert.Null(circle.LineType);
            var info = back.Layers[CadFilesTestName("Design lines")];
            Assert.Equal(50, info.LineWeight);
            Assert.Equal("HIDDEN", info.LineType, ignoreCase: true);
            var edge = back.LineTypes.Single(t => t.Name.Equals("MINE_EDGE", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(new[] { 2.0, -1, 0, -1 }, edge.Pattern);
            Assert.Equal(5, back.LineTypeScale, 9);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A layer name as the DXF writer puts it (letters, digits, _ and -).</summary>
    private static string CadFilesTestName(string name) => new(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());

    [Fact]
    public void ADashedLineIsDrawnInDashes_ASolidOneWhole()
    {
        int Gaps(string? type)
        {
            var p = BlastProject.CreateNew("lt");
            p.Entities.Add(new PolylineEntity { LayerId = p.Layers[0].Id, Points = { new(-40, 0, 0), new(40, 0, 0) }, LineType = type, LineTypeScale = 10 });
            var cam = new Camera { Width = 400, Height = 300, Scale = 4, Target = new Vec3(0, 0, 0) };
            var options = new SceneOptions { Background = SKColors.Black, ShowGrid = false, DrawNorthArrow = false, DrawScaleBar = false, DrawSummaryBlock = false };
            using var bitmap = new SKBitmap(400, 300);
            using (var canvas = new SKCanvas(bitmap)) PlanRenderer.Render(canvas, cam, p, options);
            var (_, y) = cam.ToScreen(Vec3.Zero);
            // along the line, between its ends: how many pixels are background
            return Enumerable.Range(50, 300).Count(x => { var c = bitmap.GetPixel(x, (int)y); return c.Red < 30 && c.Green < 30 && c.Blue < 30; });
        }
        Assert.Equal(0, Gaps(null));
        // dashes of 20 px and gaps of 10 px: about a third of the line is gaps
        Assert.InRange(Gaps("DASHED"), 80, 120);
    }

    [Fact]
    public void OnASheetAWeightIsItsMillimetres_NoughtAHairline()
    {
        Assert.Equal(5, LineTypes.WeightOnSheet(50, 10), 5);
        Assert.Equal(0.5, LineTypes.WeightOnSheet(0, 10), 5);
        // a scene drawn at 10 units a millimetre: a line of 0.5 mm is 5 units across, on the screen it would be 2 pixels
        int Across(double? unitsPerMm)
        {
            var p = BlastProject.CreateNew("sheet");
            p.Entities.Add(new PolylineEntity { LayerId = p.Layers[0].Id, Points = { new(-40, 0, 0), new(40, 0, 0) }, LineWeight = 50 });
            var cam = new Camera { Width = 400, Height = 300, Scale = 4, Target = new Vec3(0, 0, 0) };
            var options = new SceneOptions { Background = SKColors.Black, ShowGrid = false, DrawNorthArrow = false, DrawScaleBar = false, DrawSummaryBlock = false, UnitsPerMm = unitsPerMm };
            using var bitmap = new SKBitmap(400, 300);
            using (var canvas = new SKCanvas(bitmap)) PlanRenderer.Render(canvas, cam, p, options);
            var (x, _) = cam.ToScreen(Vec3.Zero);
            return Enumerable.Range(100, 100).Count(y => { var c = bitmap.GetPixel((int)x, y); return c.Red > 30 || c.Green > 30 || c.Blue > 30; });
        }
        Assert.Equal(2, Across(null));
        Assert.InRange(Across(10), 5, 6);
    }
}
