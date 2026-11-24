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

    // ---- surfaces: meshes, faces in blocks, what cannot be read (doc/cad-surfaces-plan.md) ----

    private static CadImport ReadDoc(Action<CadDocument> fill, CadImportOptions? o = null)
    {
        var doc = new CadDocument();
        fill(doc);
        var path = Path.Combine(Path.GetTempPath(), $"bd_cad_{Guid.NewGuid():N}.dxf");
        using (var w = new DxfWriter(path, doc, false)) w.Write();
        return Read(path, o);
    }

    private static Layer NewLayer(CadDocument doc, string name) { var l = new Layer(name); doc.Layers.Add(l); return l; }

    private static List<SurfaceEntity> Surfaces(CadImport r) => r.Items.Select(i => i.Entity).OfType<SurfaceEntity>().ToList();

    private static PolyfaceMesh Polyface(Layer layer, double dx = 0)
    {
        // a triangle and a square beside it: three triangles
        var pf = new PolyfaceMesh { Layer = layer };
        foreach (var p in new[] { new XYZ(0, 0, 10), new XYZ(10, 0, 10), new XYZ(10, 10, 11), new XYZ(0, 10, 11), new XYZ(20, 0, 10), new XYZ(20, 10, 11) })
            pf.Vertices.Add(new VertexFaceMesh(new XYZ(p.X + dx, p.Y, p.Z)));
        pf.Faces.Add(new VertexFaceRecord { Index1 = 1, Index2 = 2, Index3 = 4 });
        pf.Faces.Add(new VertexFaceRecord { Index1 = 2, Index2 = -5, Index3 = 6, Index4 = 3 });
        return pf;
    }

    [Fact]
    public void APolyfaceMeshIsASurfaceOfItsOwn_ItsFacesInTriangles()
    {
        var r = ReadDoc(doc => doc.Entities.Add(Polyface(NewLayer(doc, "Topo"))));
        var s = Assert.Single(Surfaces(r));
        Assert.Equal("Topo", s.Name);
        Assert.Equal(9, s.Triangles.Count);
        Assert.Equal(6, s.Vertices.Count);
        Assert.Contains(s.Vertices, v => v.X == 20 && v.Y == 10 && v.Z == 11);
        Assert.Empty(r.Skipped);
        // without the faces to surfaces the meshes stay out, as the 3D faces
        Assert.Empty(Surfaces(ReadDoc(doc => doc.Entities.Add(Polyface(NewLayer(doc, "Topo"))), new CadImportOptions { FacesToSurfaces = false })));
    }

    [Fact]
    public void APolygonMeshIsAGridOfCells_ClosedInMItJoinsTheLastRowToTheFirst()
    {
        PolygonMesh Grid(bool closed)
        {
            var pm = new PolygonMesh { MVertexCount = 3, NVertexCount = 3 };
            pm.Flags |= PolylineFlags.PolygonMesh;
            if (closed) pm.Flags |= PolylineFlags.ClosedPolylineOrClosedPolygonMeshInM;
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) pm.Vertices.Add(new PolygonMeshVertex(new XYZ(i * 5, j * 5, i + j)));
            return pm;
        }
        Assert.Equal(8 * 3, Assert.Single(Surfaces(ReadDoc(doc => doc.Entities.Add(Grid(false))))).Triangles.Count);
        Assert.Equal(12 * 3, Assert.Single(Surfaces(ReadDoc(doc => doc.Entities.Add(Grid(true))))).Triangles.Count);
    }

    [Fact]
    public void AMeshFaceOfFiveCornersIsAFanOfThreeTriangles()
    {
        // MESH straight to the surface: the writer of the test library does not write MESH
        var mesh = new Mesh();
        foreach (var p in new[] { new XYZ(0, 0, 0), new XYZ(4, 0, 0), new XYZ(5, 3, 0), new XYZ(2, 5, 0), new XYZ(-1, 3, 0) }) mesh.Vertices.Add(p);
        mesh.Faces.Add(new[] { 0, 1, 2, 3, 4 });
        mesh.Faces.Add(new[] { 0, 1, 1 });
        var s = CadFiles.MeshSurface(mesh)!;
        Assert.Equal(new[] { 0, 1, 2, 0, 2, 3, 0, 3, 4 }, s.Triangles);
    }

    [Fact]
    public void SeveralSurfacesOfALayer_TheFacesFirst_TheMeshesNumbered()
    {
        var r = ReadDoc(doc =>
        {
            var topo = NewLayer(doc, "Topo");
            doc.Entities.Add(new Face3D { Layer = topo, FirstCorner = new XYZ(0, 0, 0), SecondCorner = new XYZ(1, 0, 0), ThirdCorner = new XYZ(0, 1, 0), FourthCorner = new XYZ(0, 1, 0) });
            doc.Entities.Add(Polyface(topo));
            doc.Entities.Add(Polyface(topo, 100));
        });
        Assert.Equal(new[] { "Topo", "Topo 2", "Topo 3" }, Surfaces(r).Select(s => s.Name));
        Assert.All(r.Items.Where(i => i.Entity is SurfaceEntity), i => Assert.Equal("Topo", i.Layer));
    }

    [Fact]
    public void ABlockIsTakenApart_TurnedAndScaledAsInserted_Layer0IsTheLayerOfTheInsertion()
    {
        var r = ReadDoc(doc =>
        {
            var inner = new BlockRecord("Inner");
            inner.Entities.Add(new Point(new XYZ(1, 0, 0)));
            doc.BlockRecords.Add(inner);
            var block = new BlockRecord("Bench");
            var own = NewLayer(doc, "Crest");
            block.Entities.Add(new Line(new XYZ(0, 0, 5), new XYZ(10, 0, 5)));
            block.Entities.Add(new Line(new XYZ(0, 1, 5), new XYZ(10, 1, 5)) { Layer = own });
            block.Entities.Add(new Face3D { FirstCorner = new XYZ(0, 0, 5), SecondCorner = new XYZ(10, 0, 5), ThirdCorner = new XYZ(0, 10, 5), FourthCorner = new XYZ(0, 10, 5) });
            block.Entities.Add(new Insert(inner));
            doc.BlockRecords.Add(block);
            // at (100, 50), turned 90° and twice as large
            doc.Entities.Add(new Insert(block) { Layer = NewLayer(doc, "Survey"), InsertPoint = new XYZ(100, 50, 0), Rotation = Math.PI / 2, XScale = 2, YScale = 2, ZScale = 1 });
        });
        var line = (PolylineEntity)r.Items.Single(i => i.Layer == "Survey" && i.Entity is PolylineEntity).Entity;
        Assert.Equal(100, line.Points[1].X, 6);
        Assert.Equal(70, line.Points[1].Y, 6);
        Assert.Equal(5, line.Points[1].Z, 6);
        // the line on a layer of its own keeps it
        Assert.Contains(r.Items, i => i.Layer == "Crest" && i.Entity is PolylineEntity);
        // the 3D face of the block is a surface of the layer of the insertion
        var face = Assert.Single(Surfaces(r));
        Assert.Equal("Survey", r.Items.Single(i => i.Entity == face).Layer);
        // the block in the block: its point at (1, 0) of it, turned and scaled with the outer one
        var point = (PointEntity)r.Items.Single(i => i.Entity is PointEntity).Entity;
        Assert.Equal(100, point.Position.X, 6);
        Assert.Equal(52, point.Position.Y, 6);
    }

    [Fact]
    public void AnArrayInsertionGivesEveryCopy()
    {
        var r = ReadDoc(doc =>
        {
            var mark = new BlockRecord("Mark");
            mark.Entities.Add(new Point(new XYZ(0, 0, 0)));
            doc.BlockRecords.Add(mark);
            // two columns 10 apart and three rows 5 apart, the whole turned 90°
            doc.Entities.Add(new Insert(mark) { InsertPoint = new XYZ(100, 0, 0), Rotation = Math.PI / 2, ColumnCount = 2, ColumnSpacing = 10, RowCount = 3, RowSpacing = 5 });
        });
        var at = r.Items.Select(i => i.Entity).OfType<PointEntity>().Select(p => (Math.Round(p.Position.X, 6), Math.Round(p.Position.Y, 6))).OrderBy(p => p).ToList();
        Assert.Equal(new[] { (90.0, 0.0), (90.0, 10.0), (95.0, 0.0), (95.0, 10.0), (100.0, 0.0), (100.0, 10.0) }, at);
    }

    [Fact]
    public void SolidsAndRegionsAreLeftOutAsACIS_ALineIsNot()
    {
        // the writer of the test library does not write them: the kind straight from the object
        Assert.Equal("ACIS", CadFiles.Unreadable(new Solid3D()));
        Assert.Equal("ACIS", CadFiles.Unreadable(new Region()));
        Assert.Null(CadFiles.Unreadable(new Line()));
        Assert.Null(CadFiles.Unreadable(new Hatch()));
    }
}

