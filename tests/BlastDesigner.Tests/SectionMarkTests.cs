using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.Tests;

/// <summary>
/// The marks drawn by hand on a section: their points in the plane of the line, held on holes and surfaces, the angle
/// between two lines and the slope of one, kept in the file, going with their line, drawn on the sheet.
/// </summary>
public class SectionMarkTests
{
    static SectionSpot At(double s, double z) => new(s, z);

    [Fact]
    public void APointOfTheSection_IsAlongTheLineAndUp_AndGoesWithTheLine()
    {
        var frame = new SectionFrame(new Vec3(10, 20, 0), new Vec3(13, 24, 0));
        var w = frame.ToWorld(5, 370);
        Assert.Equal(13, w.X, 6); Assert.Equal(24, w.Y, 6); Assert.Equal(370, w.Z, 6);
        var (s, z) = frame.ToSection(w);
        Assert.Equal(5, s, 6); Assert.Equal(370, z, 6);
        // the line moved: the same S and Z stand on it where it is now
        var moved = new SectionFrame(new Vec3(110, 20, 0), new Vec3(113, 24, 0));
        Assert.Equal(113, moved.ToWorld(5, 370).X, 6);
    }

    [Fact]
    public void TheAngle_OfTheQuarterOfItsArc_OrTheAcuteOrObtuseOne()
    {
        // a slope at 50° to the level, both from the origin
        var c = Math.Cos(50 * Math.PI / 180);
        var s = Math.Sin(50 * Math.PI / 180);
        var level = new[] { At(-5, 0), At(5, 0) };
        var slope = new[] { At(-2 * c, -2 * s), At(2 * c, 2 * s) };
        SectionAngle? Arc(SectionSpot arc, SectionAngleMode mode = SectionAngleMode.AsDrawn) =>
            SectionMarking.Angle(new[] { level[0], level[1], slope[0], slope[1], arc }, mode);
        // between the level to the right and the slope going up
        var acute = Arc(At(1, 0.3))!;
        Assert.Equal(50, acute.Degrees, 6);
        Assert.Equal(0, acute.S, 6); Assert.Equal(0, acute.Z, 6);
        Assert.Equal(Math.Sqrt(1.09), acute.Radius, 6);
        // the quarter on the left of the slope going up: the obtuse angle
        Assert.Equal(130, Arc(At(-1, 0.3))!.Degrees, 6);
        // asked for the acute one there, or the obtuse one in the acute quarter
        Assert.Equal(50, Arc(At(-1, 0.3), SectionAngleMode.Acute)!.Degrees, 6);
        Assert.Equal(130, Arc(At(1, 0.3), SectionAngleMode.Obtuse)!.Degrees, 6);
        // lines side by side: no angle
        Assert.Null(SectionMarking.Angle(new[] { level[0], level[1], At(-5, 1), At(5, 1), At(0, 0.5) }));
    }

    [Fact]
    public void TheSlope_InDegreesPerCentOrOneTo()
    {
        var a = At(0, 0);
        var b = At(1, 1.19);
        Assert.Equal("50°", SectionMarking.Slope(a, b, new SectionMarkStyle()));
        Assert.Equal("119%", SectionMarking.Slope(a, b, new SectionMarkStyle { SlopeFormat = SectionSlopeFormat.Percent }));
        Assert.Equal("1:0.84", SectionMarking.Slope(a, b, new SectionMarkStyle { SlopeFormat = SectionSlopeFormat.Ratio }));
    }

    [Fact]
    public void TheFigures_OfTheSample_WithTheTextOfTheirOwn()
    {
        var length = new SectionMark { Kind = SectionMarkKind.Vertical, Points = { new(0, 373), new(0, 359), new(1, 365) } };
        Assert.Equal("14.0", SectionMarking.Label(length, length.Points.Select(p => At(p.S, p.Z)).ToList()));
        var spacing = new SectionMark { Kind = SectionMarkKind.Horizontal, Points = { new(0, 365), new(3.6, 365), new(0, 366) } };
        Assert.Equal("3.6", SectionMarking.Label(spacing, spacing.Points.Select(p => At(p.S, p.Z)).ToList()));
        var level = new SectionMark { Kind = SectionMarkKind.Level, Points = { new(0, 375) }, Style = new SectionMarkStyle { Text = "+<> м" } };
        Assert.Equal("+375.0 м", SectionMarking.Label(level, new[] { At(0, 375) }));
        var angle = new SectionMark { Kind = SectionMarkKind.Angle, Points = { new(-5, 0), new(5, 0), new(0, 0), new(1, 1), new(1, 0.2) } };
        Assert.Equal("45°", SectionMarking.Label(angle, angle.Points.Select(p => At(p.S, p.Z)).ToList()));
    }

    static (BlastProject P, PolylineEntity Line, Hole Hole) Blast()
    {
        var p = BlastProject.CreateNew();
        var hole = p.Add(new Hole { Label = "1", Collar = new Vec3(5, 0, 373), Length = 11.5, Subdrill = 1 });
        var line = p.Add(new PolylineEntity { Kind = LineKind.CrossSection, Label = "A", Points = { new Vec3(0, 0, 373), new Vec3(20, 0, 373) } });
        return (p, line, hole);
    }

    [Fact]
    public void APointHeldOnAHole_FollowsIt_AndIsLostWithIt()
    {
        var (p, line, hole) = Blast();
        var frame = new SectionFrame(line.Points[0], line.Points[1]);
        var collar = new SectionPoint { HoleId = hole.Id, HolePart = SectionHolePart.Collar };
        var grade = new SectionPoint { HoleId = hole.Id, HolePart = SectionHolePart.Grade };
        Assert.Equal(new SectionSpot(5, 373), SectionMarking.Resolve(p, frame, collar));
        Assert.Equal(362.5, SectionMarking.Resolve(p, frame, grade).Z, 6);
        hole.Collar = new Vec3(7, 0, 374);
        Assert.Equal(new SectionSpot(7, 374), SectionMarking.Resolve(p, frame, collar));
        // the length up to the grade, recomputed from the hole
        var length = new SectionMark { Kind = SectionMarkKind.Vertical, SectionLineId = line.Id, Points = { collar, grade, new SectionPoint(8, 368) } };
        Assert.Equal("10.5", SectionMarking.Label(length, SectionMarking.Resolve(p, frame, length)));
        p.Entities.Remove(hole);
        Assert.True(SectionMarking.Resolve(p, frame, collar).Lost);
        Assert.Equal("?", SectionMarking.Label(length, SectionMarking.Resolve(p, frame, length)));
    }

    [Fact]
    public void APointHeldOnASurface_TakesItsHeightAtItsPlace()
    {
        var (p, line, _) = Blast();
        var ground = p.Add(new SurfaceEntity { Vertices = { new(-50, -50, 370), new(50, -50, 380), new(50, 50, 380), new(-50, 50, 370) }, Triangles = { 0, 1, 2, 0, 2, 3 } });
        var frame = new SectionFrame(line.Points[0], line.Points[1]);
        var on = new SectionPoint(10, 0) { SurfaceId = ground.Id };
        // the plane rises 0.1 a metre along X: at x = 10 it is 376
        Assert.Equal(376, SectionMarking.Resolve(p, frame, on).Z, 6);
    }

    [Fact]
    public void TheMarks_AreKeptInTheFile_AndGoWithTheirLine()
    {
        var (p, line, hole) = Blast();
        var doc = new Document(p);
        var mark = new SectionMark { Kind = SectionMarkKind.Level, SectionLineId = line.Id, Points = { new(0, 375) }, Style = new SectionMarkStyle { Color = SectionMarkPreset.Blue, LineWeight = 50, LineType = "DASHED" } };
        doc.Execute("add", pr => pr.SectionMarks.Add(mark));
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(doc.Project));
        var kept = Assert.Single(back.SectionMarks);
        Assert.Equal(SectionMarkKind.Level, kept.Kind);
        Assert.Equal(375, kept.Points[0].Z);
        Assert.Equal(SectionMarkPreset.Blue, kept.Style.Color);
        Assert.Equal("DASHED", kept.Style.LineType);
        // undone with the step
        doc.Undo();
        Assert.Empty(doc.Project.SectionMarks);
        doc.Redo();
        Assert.Single(doc.Project.SectionMarks);
        // the line deleted: its marks with it
        doc.Execute("delete", pr => pr.Entities.RemoveAll(e => e.Id == line.Id));
        Assert.Empty(doc.Project.SectionMarks);
    }

    [Fact]
    public void AFileFromBefore_OpensWithNoMarks()
    {
        var json = System.Text.Encoding.UTF8.GetString(ProjectJson.Serialize(BlastProject.CreateNew()));
        var old = json.Replace("\"sectionMarks\":[],", "");
        Assert.DoesNotContain("sectionMarks", old);
        Assert.Empty(ProjectJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(old)).SectionMarks);
    }

    [Fact]
    public void TheBuiltInStyles_ALevelBlue_AShortDimensionWithTicks()
    {
        Assert.Equal(SectionMarkPreset.Blue, SectionMarkPreset.BuiltIn(SectionMarkKind.Level).Color);
        Assert.Equal(SectionMarkEnds.Tick, SectionMarkPreset.BuiltIn(SectionMarkKind.Leader).Ends);
        Assert.Equal(1, SectionMarkPreset.BuiltIn(SectionMarkKind.Horizontal).Decimals);
        Assert.Equal(0, SectionMarkPreset.BuiltIn(SectionMarkKind.Angle).AngleDecimals);
        Assert.Equal(new[] { "Bench level", "Grade level", "Slope face", "Dimension", "Helping line" }, SectionMarkPreset.Defaults().Select(d => d.Name));
        // kept as Slope by the first build: it takes its own name
        var settings = new BlastDesigner.IO.ProgramSettings { SectionMarkPresets = { new SectionMarkPreset { Name = "Slope" } } };
        settings.Upgrade();
        Assert.Equal(2, settings.SectionMarkPresets.Count(p => p.Name == SectionMarkPreset.SlopeFace));
    }

    [Fact]
    public void TheMarks_ArePrintedOnTheSheet_InTheirColour_AndLeftOutWhenTurnedOff()
    {
        var (p, line, hole) = Blast();
        p.SectionMarks.Add(new SectionMark { Kind = SectionMarkKind.Level, SectionLineId = line.Id, Points = { new(0, 375) }, Style = new SectionMarkStyle { Color = SectionMarkPreset.Blue, LineWeight = 50 } });
        p.SectionMarks.Add(new SectionMark { Kind = SectionMarkKind.Vertical, SectionLineId = line.Id, Points = { new SectionPoint { HoleId = hole.Id }, new SectionPoint { HoleId = hole.Id, HolePart = SectionHolePart.Toe }, new(3, 368) } });
        (int Blue, int Dark) Sheet(bool marks)
        {
            using var bitmap = new SKBitmap(600, 400);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            Reports.DrawSection(canvas, new SKRect(0, 0, 600, 400), p, line.Points[0], line.Points[1], 2, new SectionVisibility { ShowMarks = marks, ShowNumbers = false, ShowLevels = false, ShowDimensions = false });
            return (bitmap.Pixels.Count(c => c.Blue > 180 && c.Red < 120 && c.Green < 170), bitmap.Pixels.Count(c => c.Red < 100 && c.Green < 100 && c.Blue < 100));
        }
        var on = Sheet(true);
        var off = Sheet(false);
        Assert.True(on.Blue > off.Blue + 100, $"the blue level: {on.Blue} {off.Blue}");
        Assert.True(on.Dark > off.Dark, $"the dimension: {on.Dark} {off.Dark}");
    }

    [Fact]
    public void TheAutomaticDimensions_AreRecordedAsMarksHeldOnTheirHoles_AndLeftOutWhenTakenOver()
    {
        var p = BlastProject.CreateNew();
        var holes = Enumerable.Range(0, 4).Select(i => p.Add(new Hole { Label = i.ToString(), Collar = new Vec3(i * 3.6, 0, 373), Length = 11.5, Subdrill = 1 })).ToList();
        var line = p.Add(new PolylineEntity { Kind = LineKind.CrossSection, Label = "A", Points = { new Vec3(-5, 0, 373), new Vec3(15, 0, 373) } });
        var recorded = new List<AutoDimension>();
        var cam = new Camera { Width = 600, Height = 400, Scale = 15, Pitch = 0, Yaw = 0, Target = new Vec3(5, 0, 366) };
        using var bitmap = new SKBitmap(600, 400);
        using var canvas = new SKCanvas(bitmap);
        new SectionAnnotations(line.Points[0], line.Points[1], 2, "", new SectionLabels(false, false, true), SKColors.Black) { Recorded = recorded }.Draw(canvas, cam, p);
        // the length, the spacing and the subdrill
        Assert.Equal(new[] { SectionMarkKind.Vertical, SectionMarkKind.Horizontal, SectionMarkKind.Leader }, recorded.Select(r => r.Mark.Kind));
        Assert.All(recorded, r => Assert.True(r.Mark.Auto));
        var frame = new SectionFrame(line.Points[0], line.Points[1]);
        string Label(SectionMark m) => SectionMarking.Label(m, SectionMarking.Resolve(p, frame, m));
        Assert.Equal(new[] { "11.5", "3.6", "1.0" }, recorded.Select(r => Label(r.Mark)));
        // held on the holes: a longer hole, a longer figure
        Assert.Equal(holes[0].Id, recorded[0].Mark.Points[0].HoleId);
        holes[0].Length = 12.5;
        Assert.Equal("12.5", Label(recorded[0].Mark));
        // taken over: not drawn, not recorded
        new SectionAnnotations(line.Points[0], line.Points[1], 2, "", new SectionLabels(false, false, true), SKColors.Black) { Recorded = recorded, SkipDimensions = true }.Draw(canvas, cam, p);
        Assert.Empty(recorded);
        // the line deleted: out of the list of the lines with their own dimensions
        var doc = new Document(p);
        doc.Execute("own", pr => pr.SectionOwnDimensions.Add(line.Id));
        doc.Execute("delete", pr => pr.Entities.RemoveAll(e => e.Id == line.Id));
        Assert.Empty(doc.Project.SectionOwnDimensions);
    }

    [Fact]
    public void TheWeight_GoesToTheLinesOfADimension()
    {
        var (p, line, hole) = Blast();
        int Ink(int weight)
        {
            p.SectionMarks.Clear();
            p.SectionMarks.Add(new SectionMark { Kind = SectionMarkKind.Horizontal, SectionLineId = line.Id, Points = { new(2, 370), new(12, 370), new(7, 368) }, Style = new SectionMarkStyle { LineWeight = weight } });
            var cam = new Camera { Width = 400, Height = 300, Scale = 20, Pitch = 0, Yaw = 0, Target = new Vec3(7, 0, 369) };
            using var bitmap = new SKBitmap(400, 300);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            new SectionMarksOverlay(line.Id, line.Points[0], line.Points[1], SKColors.Black, 12).Draw(canvas, cam, p);
            return bitmap.Pixels.Count(c => c.Red < 128);
        }
        Assert.True(Ink(70) > Ink(18) * 1.5, $"{Ink(70)} {Ink(18)}");
    }

    [Fact]
    public void TheLineType_GoesToTheLinesOfADimension()
    {
        var (p, line, _) = Blast();
        int Ink(string? type)
        {
            p.SectionMarks.Clear();
            p.SectionMarks.Add(new SectionMark { Kind = SectionMarkKind.Horizontal, SectionLineId = line.Id, Points = { new(2, 370), new(12, 370), new(7, 368) }, Style = new SectionMarkStyle { LineWeight = 50, LineType = type, Ends = SectionMarkEnds.None } });
            var cam = new Camera { Width = 400, Height = 300, Scale = 20, Pitch = 0, Yaw = 0, Target = new Vec3(7, 0, 369) };
            using var bitmap = new SKBitmap(400, 300);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            new SectionMarksOverlay(line.Id, line.Points[0], line.Points[1], SKColors.Black, 12).Draw(canvas, cam, p);
            return bitmap.Pixels.Count(c => c.Red < 128);
        }
        // dashed: gaps in the dimension line and the extension lines
        Assert.True(Ink("DASHED") < Ink(null) * 0.85, $"{Ink("DASHED")} {Ink(null)}");
    }

    [Fact]
    public void AMark_IsPickedByWhatItDrew()
    {
        var (p, line, _) = Blast();
        var mark = new SectionMark { Kind = SectionMarkKind.Polyline, SectionLineId = line.Id, Points = { new(0, 365), new(10, 375) } };
        p.SectionMarks.Add(mark);
        var cam = new Camera { Width = 400, Height = 300, Scale = 10, Pitch = 0, Yaw = 0, Target = new Vec3(5, 0, 370) };
        var overlay = new SectionMarksOverlay(line.Id, line.Points[0], line.Points[1], SKColors.Black, 12);
        using var bitmap = new SKBitmap(400, 300);
        using var canvas = new SKCanvas(bitmap);
        overlay.Draw(canvas, cam, p);
        var (x, y) = cam.ToScreen(new SectionFrame(line.Points[0], line.Points[1]).ToWorld(5, 370));
        Assert.Same(mark, overlay.HitTest(x, y, 6));
        Assert.Null(overlay.HitTest(x, y - 40, 6));
        Assert.Single(overlay.InRect(new SKRect(0, 0, 400, 300), false));
        // an angle picked by a box round its figure only, as a hole by a box round its number
        var angle = new SectionMark { Kind = SectionMarkKind.Angle, SectionLineId = line.Id, Points = { new(0, 365), new(10, 365), new(5, 365), new(7, 369), new(7, 366) } };
        p.SectionMarks.Add(angle);
        using var again = new SKCanvas(bitmap);
        overlay.Draw(again, cam, p);
        var figure = overlay.LastDrawn.Single(d => d.Mark == angle).Texts.Single();
        var round = new SKRect(figure.Left - 3, figure.Top - 3, figure.Right + 3, figure.Bottom + 3);
        Assert.Contains(angle, overlay.InRect(round, false));
        Assert.DoesNotContain(mark, overlay.InRect(round, false));
    }
}
