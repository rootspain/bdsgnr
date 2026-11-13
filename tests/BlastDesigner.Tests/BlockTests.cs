using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using BlastDesigner.Rendering;
using SkiaSharp;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>Blocks: their shape and grips, one block a layer, what they let through, and boxes of lines made blocks.</summary>
public class BlockTests
{
    private static BlockEntity Box(double az = 0) => new() { Origin = new Vec3(0, 0, -15), Azimuth = az, Length = 20, Width = 10, Height = 30 };

    private static void Near(Vec3 expected, Vec3 actual, double tol = 1e-9)
    {
        Assert.True(Vec3.Distance(expected, actual) <= tol, $"{expected} expected, {actual}");
    }

    [Fact]
    public void ThreePointsMakeABlockWithTheWorkPlaneThroughItsMiddle()
    {
        // the length to the east, the width picked to the south: to the right of the length
        var b = BlockEntity.FromPoints(new Vec3(100, 200, 5), new Vec3(120, 200, 5), new Vec3(110, 190, 5));
        Assert.Equal(90, b.Azimuth, 9);
        Assert.Equal(20, b.Length, 9);
        Assert.Equal(10, b.Width, 9);
        Assert.Equal(BlockEntity.DefaultHeight, b.Height);
        Assert.Equal(5 - 15, b.Bottom, 9);
        Assert.Equal(5 + 15, b.Top, 9);
        Assert.Equal(20 * 10 * 30, b.Volume, 6);
        // picked to the left the block turns over: the same box on that side
        var left = BlockEntity.FromPoints(new Vec3(100, 200, 5), new Vec3(120, 200, 5), new Vec3(110, 210, 5));
        Assert.Equal(10, left.Width, 9);
        Assert.True(left.Contains(new Vec3(110, 205, 5)));
        Assert.False(left.Contains(new Vec3(110, 195, 5)));
    }

    [Fact]
    public void ACornerDragsTheLengthAndWidth_TheOppositeCornerStays()
    {
        var b = Box(30);
        var stays = b.Corner(0);
        // corner 3: the far end of the length and of the width, on the ground
        var to = b.At(25, 12, 0);
        b.Current = b.Dragged(3, to, vertical: false, fine: false);
        Assert.Equal(25, b.Length, 9);
        Assert.Equal(12, b.Width, 9);
        Assert.Equal(30, b.Height, 9);
        Near(stays, b.Corner(0));
        // with Shift the top of a top corner goes up; the base stays
        b.Current = b.Dragged(7, b.Corner(7) + new Vec3(0, 0, 4), vertical: true, fine: false);
        Assert.Equal(34, b.Height, 9);
        Assert.Equal(-15, b.Bottom, 9);
    }

    [Fact]
    public void AFaceDragsItsOwnSide_CtrlStepsIt_AndPastTheOtherSideTheBlockTurnsOver()
    {
        var b = Box();
        // the face at the far end of the width (axis 1, side 1)
        b.Current = b.Dragged(8 + 2 * 1 + 1, b.At(10, 13.04, 15), false, fine: true);
        Assert.Equal(20, b.Length, 9);
        Assert.Equal(13.0, b.Width, 9);
        // the bottom face (axis 2, side 0) up by 5
        b.Current = b.Dragged(8 + 2 * 2, b.At(10, 5, 5), false, false);
        Assert.Equal(25, b.Height, 9);
        Assert.Equal(-10, b.Bottom, 9);
        // the near end of the length (to the north at azimuth 0) dragged 5 past the far one: the block turns over, 5 long, from the old far end
        b.Current = b.Dragged(8, b.At(25, 5, 0), false, false);
        Assert.Equal(5, b.Length, 9);
        Near(new Vec3(0, 20, -10), b.Origin);
        // never below the least size
        b.Current = b.Dragged(8, b.At(5, 5, 0), false, false);
        Assert.Equal(BlockEntity.MinSize, b.Length, 9);
    }

    [Fact]
    public void ATurnTurnsTheBlock()
    {
        var b = Box();
        b.Transform(v => new Vec3(v.Y, -v.X, v.Z));
        Assert.Equal(90, b.Azimuth, 9);
        Assert.Equal(20, b.Length, 9);
        Near(new Vec3(20, 0, -15), b.Corner(1));
    }

    [Fact]
    public void EachBlockHasALayerOfItsOwn_WithTheFirstFreeNumber()
    {
        var p = BlastProject.CreateNew("blocks");
        var doc = new Document(p);
        BlockLayers.BaseName = "Блок";
        Guid Add() { var b = Box(); doc.Execute("Add block", pr => pr.Add(b, BlockLayers.NewLayer(pr, BlockLayers.BaseName))); return b.Id; }
        var a = Add();
        var b2 = Add();
        var c = Add();
        string LayerOf(Guid id) => doc.Project.FindLayer(doc.Project.FindEntity(id)!.LayerId)!.Name;
        Assert.Equal(new[] { "Блок", "Блок 2", "Блок 3" }, new[] { LayerOf(a), LayerOf(b2), LayerOf(c) });
        // the block deleted, its layer goes with it; the next block takes the free number
        doc.Execute("Delete", pr => pr.Entities.RemoveAll(e => e.Id == b2));
        Assert.DoesNotContain(doc.Project.Layers, l => l.Name == "Блок 2");
        Assert.Equal("Блок 2", LayerOf(Add()));
        // a hole moved onto a block layer goes back; a block moved off its layer goes back
        var hole = new Hole { Collar = new Vec3(1, 1, 0), Length = 10 };
        doc.Execute("Hole", pr => pr.Add(hole));
        var holeLayer = doc.Project.FindEntity(hole.Id)!.LayerId;
        var blockLayer = doc.Project.FindEntity(a)!.LayerId;
        doc.Execute("Move to layer", pr => pr.FindEntity(hole.Id)!.LayerId = blockLayer);
        Assert.Equal(holeLayer, doc.Project.FindEntity(hole.Id)!.LayerId);
        doc.Execute("Move to layer", pr => pr.FindEntity(a)!.LayerId = holeLayer);
        Assert.Equal(blockLayer, doc.Project.FindEntity(a)!.LayerId);
        // a copy goes on a new layer
        var copy = doc.Project.FindEntity(a)!.CloneWithNewId();
        doc.Execute("Copy", pr => pr.Entities.Add(copy));
        Assert.NotEqual(blockLayer, doc.Project.FindEntity(copy.Id)!.LayerId);
        Assert.Equal("Блок 4", LayerOf(copy.Id));
        // the layer deleted with its block, undo brings both back
        doc.Execute("Delete layer", pr => { pr.Entities.RemoveAll(e => e.LayerId == blockLayer); pr.Layers.RemoveAll(l => l.Id == blockLayer); });
        Assert.Null(doc.Project.FindEntity(a));
        doc.Undo();
        Assert.NotNull(doc.Project.FindEntity(a));
        Assert.NotNull(doc.Project.FindLayer(blockLayer));
    }

    [Fact]
    public void AHoleIsShownWholeByItsCollar_LinesAreCutAtTheWalls()
    {
        var p = BlastProject.CreateNew("clip");
        var layer = BlockLayers.NewLayer(p, "Block");
        p.Add(Box(), layer);
        var clip = BlockClip.Of(p)!;
        // collar inside, the hole going far below the block: shown
        Assert.True(clip.Shows(new Hole { Collar = new Vec3(5, 5, 0), Length = 100 }));
        // collar outside, the hole running through the block: not shown
        Assert.False(clip.Shows(new Hole { Collar = new Vec3(5, 5, 20), Length = 30 }));
        // a line from outside to inside: the part inside, to the wall
        var pieces = clip.Pieces(new[] { new Vec3(-10, 5, 0), new Vec3(5, 5, 0) }, false);
        Assert.Single(pieces);
        Near(new Vec3(0, 5, 0), pieces[0][0]);
        Near(new Vec3(5, 5, 0), pieces[0][1]);
        // a polygon around the block crossing it: open runs, not closed along the walls
        var square = new[] { new Vec3(-5, 2, 0), new Vec3(25, 2, 0), new Vec3(25, 8, 0), new Vec3(-5, 8, 0) };
        var runs = clip.Pieces(square, true);
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.All(r, v => Assert.True(clip.Contains(v))));
        // the layer hidden: nothing is cut
        layer.Visible = false;
        Assert.Null(BlockClip.Of(p));
    }

    [Fact]
    public void ASurfaceIsCutToTheBlock_AndSeveralBlocksShowWhatIsInAny()
    {
        var p = BlastProject.CreateNew("cut");
        p.Add(Box(), BlockLayers.NewLayer(p, "Block"));
        var grade = new SurfaceEntity { Vertices = { new(-50, -50, 0), new(50, -50, 0), new(50, 50, 0), new(-50, 50, 0) }, Triangles = { 0, 1, 2, 0, 2, 3 } };
        p.Add(grade);
        var clip = BlockClip.Of(p)!;
        var cut = PlanRenderer.CutSurface(clip, grade)!;
        Assert.Equal(grade.Id, cut.Id);
        Assert.All(cut.Vertices, v => Assert.True(clip.Contains(v)));
        // the area in plan is the block's footprint
        double area = 0;
        for (int i = 0; i < cut.Triangles.Count; i += 3)
        {
            Vec3 a = cut.Vertices[cut.Triangles[i]], b = cut.Vertices[cut.Triangles[i + 1]], c = cut.Vertices[cut.Triangles[i + 2]];
            area += Math.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) / 2;
        }
        Assert.Equal(200, area, 6);
        // the same object while nothing changes: the renderer keeps its mesh
        Assert.Same(cut, PlanRenderer.CutSurface(clip, grade));
        // a second block far off: a point in it is shown too
        var far = Box();
        far.Origin = new Vec3(1000, 1000, -15);
        p.Add(far, BlockLayers.NewLayer(p, "Block"));
        var two = BlockClip.Of(p)!;
        Assert.True(two.Contains(new Vec3(1005, 1005, 0)));
        Assert.True(two.Contains(new Vec3(5, 5, 0)));
        Assert.False(two.Contains(new Vec3(500, 500, 0)));
    }

    [Fact]
    public void TheRendererDrawsOnlyWhatIsInsideTheBlock()
    {
        var p = BlastProject.CreateNew("draw");
        p.Add(Box(), BlockLayers.NewLayer(p, "Block", 0xFF000000));
        var inside = new PointEntity { Position = new Vec3(10, 5, 0), Color = 0xFFFF0000 };
        var outside = new PointEntity { Position = new Vec3(40, 5, 0), Color = 0xFFFF0000 };
        p.Add(inside);
        p.Add(outside);
        var cam = new Camera { Width = 400, Height = 300, Pitch = 90, Scale = 5, Target = new Vec3(25, 5, 0) };
        using var bitmap = new SKBitmap(400, 300);
        using var canvas = new SKCanvas(bitmap);
        PlanRenderer.Render(canvas, cam, p, new SceneOptions { DrawSummaryBlock = false, DrawNorthArrow = false, DrawScaleBar = false, ShowGrid = false });
        bool Red(Vec3 at) { var (x, y) = cam.ToScreen(at); return bitmap.GetPixel((int)x, (int)y) is var c && c.Red > 200 && c.Green < 80; }
        Assert.True(Red(inside.Position));
        Assert.False(Red(outside.Position));
    }

    [Fact]
    public void ABlockShowsShortEdgesAtItsCorners_AllItsEdgesUnderTheCursorOrSelected()
    {
        var p = BlastProject.CreateNew("stubs");
        var block = Box();
        p.Add(block, BlockLayers.NewLayer(p, "Block", 0xFFFFFFFF));
        var cam = new Camera { Width = 400, Height = 300, Pitch = 90, Scale = 10, Target = block.Center };
        SKColor At(SceneOptions o, Vec3 world)
        {
            using var bitmap = new SKBitmap(400, 300);
            using var canvas = new SKCanvas(bitmap);
            PlanRenderer.Render(canvas, cam, p, o);
            var (x, y) = cam.ToScreen(world);
            return bitmap.GetPixel((int)Math.Round(x), (int)Math.Round(y));
        }
        SceneOptions Options(Guid? hot = null, bool selected = false) => new()
        {
            Background = SKColors.Black, DrawSummaryBlock = false, DrawNorthArrow = false, DrawScaleBar = false, ShowGrid = false, HotBlock = hot,
            Selection = selected ? new HashSet<Guid> { block.Id } : new HashSet<Guid>()
        };
        // 5 px from a corner along the length: on the short edge; the middle of the length: nothing
        var nearCorner = block.At(0.5, 0, 0);
        var middle = block.At(10, 0, 0);
        var faint = At(Options(), nearCorner);
        Assert.InRange(faint.Red, 20, 120);
        Assert.Equal(SKColors.Black, At(Options(), middle));
        // under the cursor all the edges, brighter
        Assert.True(At(Options(hot: block.Id), middle).Red > 100);
        Assert.True(At(Options(selected: true), middle) != SKColors.Black);
        // the short edges keep to a quarter of an edge small on the screen
        var far = new Camera { Width = 400, Height = 300, Pitch = 90, Scale = 0.5, Target = block.Center };
        Assert.All(PlanRenderer.BlockStubs(far, block), s => Assert.True((s.B - s.A).Length <= 0.25 * 20 * 0.5 + 1e-3));
    }

    [Fact]
    public void TwelveLinesOfABoxOnABlockLayerMakeTheBlock_OtherLinesDoNot()
    {
        var p = BlastProject.CreateNew("lines");
        var b = Box(25);
        var layer = p.GetOrCreateLayer("Блок 2");
        var corners = b.Corners();
        foreach (var (i, j) in BlockEntity.Edges) p.Add(new PolylineEntity { Points = { corners[i], corners[j] } }, layer);
        // a tilted box on another block layer
        var tilted = p.GetOrCreateLayer("Block");
        foreach (var (i, j) in BlockEntity.Edges)
            p.Add(new PolylineEntity { Points = { corners[i] + new Vec3(0, 0, corners[i].X * 0.1), corners[j] + new Vec3(0, 0, corners[j].X * 0.1) } }, tilted);
        // eleven edges on a third
        var short11 = p.GetOrCreateLayer("Блок 3");
        foreach (var (i, j) in BlockEntity.Edges.Skip(1)) p.Add(new PolylineEntity { Points = { corners[i], corners[j] } }, short11);
        var failed = BlockAssembly.Assemble(p);
        Assert.Equal(new[] { "Block", "Блок 3" }, failed.OrderBy(x => x, StringComparer.Ordinal));
        var made = p.Entities.OfType<BlockEntity>().Single();
        Assert.Equal(layer.Id, made.LayerId);
        Assert.Equal(BlockLayers.Group, layer.Group);
        Assert.DoesNotContain(p.Entities, e => e.LayerId == layer.Id && e is PolylineEntity);
        foreach (var c in b.Corners()) Assert.Contains(made.Corners(), x => Vec3.Distance(x, c) < 1e-6);
        Assert.Equal(b.Volume, made.Volume, 6);
    }

    [Fact]
    public void ABlockWrittenToDxfComesBackABlock()
    {
        var p = BlastProject.CreateNew("dxf");
        var b = Box(40);
        b.Origin = new Vec3(500, 600, 0);
        p.Add(b, BlockLayers.NewLayer(p, "Block"));
        var path = Path.Combine(Path.GetTempPath(), "bd-block-" + Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            File.WriteAllText(path, CadFiles.ExportDxf(p, p.Entities));
            var drawing = CadFiles.ReadCad(path, new CadImportOptions());
            var back = BlastProject.CreateNew("back");
            foreach (var (layerName, e) in drawing.Items) back.Add(e, back.GetOrCreateLayer(layerName));
            Assert.Empty(BlockAssembly.Assemble(back));
            var made = back.Entities.OfType<BlockEntity>().Single();
            foreach (var c in b.Corners()) Assert.Contains(made.Corners(), x => Vec3.Distance(x, c) < 1e-3);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ABlockIsKeptInTheProjectFile()
    {
        var p = BlastProject.CreateNew("file");
        var b = Box(12);
        p.Add(b, BlockLayers.NewLayer(p, "Block"));
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(p));
        var again = (BlockEntity)back.FindEntity(b.Id)!;
        Assert.Equal(b.Current, again.Current);
    }
}
