using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using BlastDesigner.Domain.Model;
using BlastDesigner.IO;
using CSMath;
using Layer = ACadSharp.Tables.Layer;
using TextEntity = BlastDesigner.Domain.Model.TextEntity;

namespace BlastDesigner.Tests;

/// <summary>DWG/DXF import of survey drawings: hidden layers, 2D plan drawing next to the 3D survey, colours, text, blocks.</summary>
public class CadImportTests
{
    /// <summary>A survey drawing in miniature: 3D survey on "0", plan drawing at elevation 0, a switched-off and a frozen layer.</summary>
    private static string Drawing(Action<CadDocument>? more = null)
    {
        var doc = new CadDocument();
        var off = new Layer("Off") { IsOn = false };
        var frozen = new Layer("Frozen") { Color = new Color(3) };
        frozen.Flags |= LayerFlags.Frozen;
        var plan = new Layer("Plan") { Color = new Color(1) };
        var survey = new Layer("Survey") { Color = new Color(5) };
        foreach (var l in new[] { off, frozen, plan, survey }) doc.Layers.Add(l);

        doc.Entities.Add(new Polyline3D(new[] { new XYZ(0, 0, 610), new XYZ(10, 0, 610.4), new XYZ(20, 0, 609.8) }));
        doc.Entities.Add(new Point(new XYZ(5, 5, 610.2)) { Layer = survey });
        var outline = Lw(new XY(0, 0), new XY(50, 0), new XY(50, 50));
        outline.Layer = plan;
        outline.Color = new Color(2);
        doc.Entities.Add(outline);
        doc.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(5, 5, 0)) { Layer = off });
        doc.Entities.Add(new Line(new XYZ(1, 0, 0), new XYZ(6, 5, 0)) { Layer = frozen });
        more?.Invoke(doc);
        var path = Path.Combine(Path.GetTempPath(), $"bd_cad_{Guid.NewGuid():N}.dxf");
        using (var w = new DxfWriter(path, doc, false)) w.Write();
        return path;
    }

    private static LwPolyline Lw(params XY[] points) => new(points.Select(p => new LwPolyline.Vertex(p)).ToArray());

    private static CadImport Read(string path, CadImportOptions? o = null)
    {
        try { return CadFiles.ReadCad(path, o ?? new CadImportOptions()); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HiddenLayers_ImportHiddenByDefault_SkipOrShowOnRequest()
    {
        var hidden = Read(Drawing());
        Assert.False(hidden.Layers["Off"].Visible);
        Assert.False(hidden.Layers["Frozen"].Visible);
        Assert.True(hidden.Layers["Survey"].Visible);
        Assert.Equal(0, hidden.SkippedHidden);

        var skipped = Read(Drawing(), new CadImportOptions { HiddenLayers = CadHiddenLayers.Skip });
        Assert.Equal(2, skipped.SkippedHidden);
        Assert.DoesNotContain(skipped.Items, i => i.Layer.StartsWith("Off") || i.Layer.StartsWith("Frozen"));

        var shown = Read(Drawing(), new CadImportOptions { HiddenLayers = CadHiddenLayers.ImportVisible });
        Assert.True(shown.Layers["Off"].Visible);
    }

    [Fact]
    public void FlatLines_GoToTheirOwnLayer_OnlyNextToA3DSurvey()
    {
        var r = Read(Drawing());
        // the plan polyline and the two lines at 0 on the hidden layers; the 3D survey line stays where it is
        Assert.Equal(3, r.FlatLines);
        Assert.Contains(r.Items, i => i.Layer == "Plan (2D)");
        Assert.Contains(r.Items, i => i.Layer == "0" && i.Entity is PolylineEntity pl && pl.Points[0].Z == 610);
        Assert.False(r.Layers["Off (2D)"].Visible);

        var kept = Read(Drawing(), new CadImportOptions { SeparateFlatLines = false });
        Assert.Equal(0, kept.FlatLines);

        // a drawing that is all 2D is left alone
        var doc = new CadDocument();
        doc.Entities.Add(Lw(new XY(0, 0), new XY(5, 0)));
        var path = Path.Combine(Path.GetTempPath(), $"bd_cad_{Guid.NewGuid():N}.dxf");
        using (var w = new DxfWriter(path, doc, false)) w.Write();
        var flat = Read(path);
        Assert.Equal(0, flat.FlatLines);
        Assert.Equal("0", Assert.Single(flat.Items).Layer);
    }

    [Fact]
    public void Colours_OfObjectsAndLayers()
    {
        var r = Read(Drawing());
        Assert.Equal(0xFFFF0000u, r.Layers["Plan"].Color);
        var plan = r.Items.Single(i => i.Layer == "Plan (2D)").Entity;
        Assert.Equal(0xFFFFFF00u, plan.Color);
        // by layer: the layer's colour applies
        Assert.Null(r.Items.Single(i => i.Entity is PointEntity).Entity.Color);
    }

    [Fact]
    public void MText_LosesItsFormattingCodes()
    {
        Assert.Equal("01.04.22\nV=8000 м3\n16.04.22", CadFiles.PlainMText(@"\A1;     {\fISOCPEUR|b0|i0|c0|p34;01.04.22\fISOCPEUR|b0|i0|c204|p34;\P} {\fISOCPEUR|b0|i1|c204|p34;\Q0;\W1;V=8000 м3\P16.04.22}"));
        Assert.Equal("Блок 13.05.22\nгор 610.5-595м", CadFiles.PlainMText(@"{\f@Meiryo|b0|i0|c204|p34;Блок 13.05.22\Pгор 610.5-595м}"));
        Assert.Equal("1/2 a\\b {c}", CadFiles.PlainMText(@"\S1^2; a\\b \{c\}"));
        Assert.Equal("Ж", CadFiles.PlainMText(@"\U+0416"));

        var r = Read(Drawing(d => d.Entities.Add(new MText { Value = @"{\H2.5x;\C3;Бровка\Pверх}", InsertPoint = new XYZ(1, 1, 0), Height = 2 })));
        Assert.Equal("Бровка\nверх", r.Items.Select(i => i.Entity).OfType<TextEntity>().Single().Text);
    }

    [Fact]
    public void ArcsAndSplines_BecomePolylines()
    {
        var r = Read(Drawing(d =>
        {
            d.Entities.Add(new Arc { Center = new XYZ(0, 0, 600), Radius = 10, StartAngle = 0, EndAngle = Math.PI / 2 });
        }));
        var arc = r.Items.Select(i => i.Entity).OfType<PolylineEntity>().Single(p => p.Points.Count >= 8 && p.Points.All(v => Math.Abs(v.Z - 600) < 1e-9));
        Assert.All(arc.Points, v => Assert.Equal(10, Math.Sqrt(v.X * v.X + v.Y * v.Y), 6));
    }

    [Fact]
    public void BlockWithElevationAttribute_IsASurveyPoint()
    {
        var r = Read(Drawing(d =>
        {
            var block = new BlockRecord("PIKET");
            block.Entities.Add(new Circle { Radius = 0.2 });
            block.Entities.Add(new AttributeDefinition { Tag = "НОМЕР" });
            block.Entities.Add(new AttributeDefinition { Tag = "ОТМ" });
            d.BlockRecords.Add(block);
            var ins = new Insert(block) { InsertPoint = new XYZ(30, 40, 0) };
            // the insert gets an attribute per definition; the drawing fills them in
            ins.Attributes.Single(a => a.Tag == "НОМЕР").Value = "417";
            ins.Attributes.Single(a => a.Tag == "ОТМ").Value = "612,34";
            d.Entities.Add(ins);
        }));
        var p = r.Items.Select(i => i.Entity).OfType<PointEntity>().Single(x => x.Label == "417");
        Assert.Equal(612.34, p.Position.Z, 6);
        Assert.Equal(30, p.Position.X, 6);
    }

    /// <summary>
    /// The real survey drawing, read from the path in BLASTDESIGNER_REAL_DWG (customer data, never in the repository).
    /// Without the variable the test has nothing to check and passes.
    /// </summary>
    [Fact]
    public void RealSurveyDrawing_WhenAvailable()
    {
        var path = Environment.GetEnvironmentVariable("BLASTDESIGNER_REAL_DWG");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var r = CadFiles.ReadCad(path, new CadImportOptions());
        var points = r.Items.Select(i => i.Entity).OfType<PointEntity>().ToList();
        Assert.NotEmpty(points);
        Assert.All(points, p => Assert.True(Math.Abs(p.Position.Z) > 1e-6, "survey point without elevation"));
        // no line at elevation 0 left among the survey
        Assert.DoesNotContain(r.Items, i => !i.Layer.EndsWith(" (2D)") && i.Entity is PolylineEntity pl && pl.Points.All(v => Math.Abs(v.Z) < 1e-6));
        Assert.True(r.FlatLines > 0);
        Assert.Contains(r.Layers.Values, l => !l.Visible);
        Assert.DoesNotContain(r.Items.Select(i => i.Entity).OfType<TextEntity>(), t => t.Text.Contains(@"\P") || t.Text.Contains(@"\f") || t.Text.Contains('{'));
    }
}
