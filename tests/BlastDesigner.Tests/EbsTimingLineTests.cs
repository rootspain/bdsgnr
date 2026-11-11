using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>Electronic timing line checked against hand-computed times.</summary>
public class EbsTimingLineTests
{
    static readonly BlastProject Project = BlastProject.CreateNew();
    static string Explosive(BlastProject p) => p.Products.First(x => x.IsExplosive).Id;
    static string Electronic(BlastProject p) => p.Products.First(x => x.Kind == ProductKind.ElectronicDetonator).Id;
    static string Pyrotechnic(BlastProject p) => p.Products.First(x => x.Kind == ProductKind.DownholeDetonator).Id;
    /// <summary>A vertical hole loaded with one primer, electronic unless another detonator is given.</summary>
    static Hole H(double x, double y, string? detonator = null) => new()
    {
        Collar = new Vec3(x, y, 100), Label = $"{x}/{y}",
        Decks = { new Deck { Top = 3, Bottom = 10, ProductId = Explosive(Project), Primers = { new Primer { Depth = 9.5, DetonatorId = detonator ?? Electronic(Project) } } } }
    };
    static List<Vec3> Line(params (double X, double Y)[] p) => p.Select(q => new Vec3(q.X, q.Y, 100)).ToList();

    [Fact]
    public void TimeLine_TimeIsReliefTimesDistanceShiftedToOffset()
    {
        var holes = new List<Hole> { H(3, 2), H(7, 5) };
        // Line along +X: the right-hand normal points to −Y, holes on +Y are later by relief × distance.
        var r = EbsTimingLine.Apply(Project, holes, Line((0, 0), (10, 0)), new[] { new TimingLineSegment(0, 8, 0) },
            new TimingLineOptions { Style = TimingLineStyle.TimeLine, StartOffsetMs = 100 }, 20000, true);
        Assert.Equal(2, r.Timed);
        Assert.Equal(100, holes[0].ElectronicTimeMs!.Value, 6);
        Assert.Equal(124, holes[1].ElectronicTimeMs!.Value, 6);
        Assert.False(holes[1].ElectronicTimeManual);
    }

    [Fact]
    public void TimeLine_ProgressiveAddsProgressScaledByTenMetres()
    {
        var holes = new List<Hole> { H(0, 0), H(0, 10), H(0, 20) };
        EbsTimingLine.Apply(Project, holes, Line((-5, 0), (5, 0)), new[] { new TimingLineSegment(0, 0, 7) },
            new TimingLineOptions { Style = TimingLineStyle.TimeLine, UseProgressive = true, ProgressRate = 4 }, 20000, true);
        Assert.Equal(0, holes[0].ElectronicTimeMs!.Value, 6);
        Assert.Equal(7, holes[1].ElectronicTimeMs!.Value, 6);   // 7 · (10/10)²
        Assert.Equal(28, holes[2].ElectronicTimeMs!.Value, 6);  // 7 · (20/10)²
    }

    [Fact]
    public void BurnLine_BurnsAlongSegmentsAndPicksEarliest()
    {
        var holes = new List<Hole> { H(0, 0), H(4, 0), H(10, 4) };
        var data = new[] { new TimingLineSegment(2, 5, 0), new TimingLineSegment(3, 5, 0) };
        EbsTimingLine.Apply(Project, holes, Line((0, 0), (10, 0), (10, 10)), data, new TimingLineOptions { Style = TimingLineStyle.BurnLine }, 20000, true);
        Assert.Equal(0, holes[0].ElectronicTimeMs!.Value, 6);
        Assert.Equal(8, holes[1].ElectronicTimeMs!.Value, 6);   // 4 m × 2 ms/m
        Assert.Equal(32, holes[2].ElectronicTimeMs!.Value, 6);  // 10 × 2 + 4 × 3 (segment 1) beats 10 × 2 + 4 × 5 (segment 0)
    }

    [Fact]
    public void ManualTimesAreKeptButStillSetTheZeroPoint()
    {
        var manual = H(3, 2);
        Timing.SetTime(manual, 999, true);
        var other = H(7, 5);
        var r = EbsTimingLine.Apply(Project, new List<Hole> { manual, other }, Line((0, 0), (10, 0)), new[] { new TimingLineSegment(0, 8, 0) },
            new TimingLineOptions(), 20000, true);
        Assert.Equal(1, r.SkippedManual);
        Assert.Equal(999, manual.ElectronicTimeMs);
        Assert.True(manual.ElectronicTimeManual);
        Assert.Equal(24, other.ElectronicTimeMs!.Value, 6);
    }

    [Fact]
    public void WithManualTimesAppliedAManualHoleTakesTheTimeOfTheLine()
    {
        var manual = H(3, 2);
        Timing.SetTime(manual, 999, true);
        var other = H(7, 5);
        var r = EbsTimingLine.Apply(Project, new List<Hole> { manual, other }, Line((0, 0), (10, 0)), new[] { new TimingLineSegment(0, 8, 0) },
            new TimingLineOptions { KeepManualTimes = false }, 20000, true);
        Assert.Equal(0, r.SkippedManual);
        Assert.Equal(0, manual.ElectronicTimeMs!.Value, 6);
        Assert.False(manual.ElectronicTimeManual);
        Assert.Equal(24, other.ElectronicTimeMs!.Value, 6);
    }

    [Fact]
    public void TimesAreRoundedToAMillisecondOrToATenth()
    {
        var h = H(0, 0);
        Timing.SetTime(h, 12.46, false);
        Assert.Equal(12, h.ElectronicTimeMs);
        Timing.SetTime(h, 12.46, false, allowSubMs: true);
        Assert.Equal(12.5, h.ElectronicTimeMs!.Value, 9);
        // the line plans what it programs
        var holes = new List<Hole> { H(0, 0), H(0, 1.23) };
        var (times, _) = EbsTimingLine.Plan(Project, holes, Line((-5, 0), (5, 0)), new[] { new TimingLineSegment(0, 10, 0) }, new TimingLineOptions(), 20000, false);
        Assert.Equal(12, times[holes[1].Id]);
        EbsTimingLine.Apply(Project, holes, Line((-5, 0), (5, 0)), new[] { new TimingLineSegment(0, 10, 0) }, new TimingLineOptions(), 20000, true);
        Assert.Equal(12.3, holes[1].ElectronicTimeMs!.Value, 9);
    }

    [Fact]
    public void AnOldFileWithATimeStepUnderAMillisecondAllowsTenths()
    {
        foreach (var (step, sub) in new[] { (0.1, true), (1.0, false) })
        {
            using var ms = new MemoryStream();
            BlastDesigner.IO.ProjectFile.Write(BlastProject.CreateNew(), ms);
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Update, true))
            {
                var entry = zip.GetEntry("project.json")!;
                string json;
                using (var r = new StreamReader(entry.Open())) json = r.ReadToEnd();
                json = json.Replace("\"electronic\": {", $"\"electronic\": {{ \"timeStepMs\": {step.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                Assert.Contains("timeStepMs", json);
                entry.Delete();
                using var w = new StreamWriter(zip.CreateEntry("project.json").Open());
                w.Write(json);
            }
            ms.Position = 0;
            Assert.Equal(sub, BlastDesigner.IO.ProjectFile.Read(ms).Electronic.AllowSubMs);
        }
    }

    [Fact]
    public void AdvancedBurn_UsesReliefAlongAndTanAngleSideways()
    {
        var holes = new List<Hole> { H(0, 0), H(6, 3), H(6, -3) };
        var o = new TimingLineOptions { Style = TimingLineStyle.AdvancedBurn, BurdenReliefMsPerM = 4, Angle1Deg = 45, Angle2Deg = 0 };
        var data = new[] { new TimingLineSegment(0, 0, 0) };
        var r = EbsTimingLine.Apply(Project, holes, Line((0, 0), (10, 0)), data, o, 30, true);
        Assert.Equal(0, holes[0].ElectronicTimeMs!.Value, 6);
        // Left of the line (angle 1): 3 × 4·tan45 + 6 × 4 = 36 > max 30 → reported, not timed.
        Assert.Null(holes[1].ElectronicTimeMs);
        Assert.Single(r.MaxTimeExceeded);
        // Right of the line (angle 2 = 0°): only the distance along counts.
        Assert.Equal(24, holes[2].ElectronicTimeMs!.Value, 6);
    }

    [Fact]
    public void ReverseOrderStartsTheBurnAtTheLastPoint()
    {
        var holes = new List<Hole> { H(0, 0), H(10, 0) };
        EbsTimingLine.Apply(Project, holes, Line((0, 0), (10, 0)), new[] { new TimingLineSegment(2, 5, 0) },
            new TimingLineOptions { Style = TimingLineStyle.BurnLine, ReverseOrder = true }, 20000, true);
        Assert.Equal(20, holes[0].ElectronicTimeMs!.Value, 6);
        Assert.Equal(0, holes[1].ElectronicTimeMs!.Value, 6);
    }

    [Fact]
    public void EntityKeepsPointsAndOptionsThroughProjectFile()
    {
        var line = EbsTimingLine.CreateEntity(Line((0, 0), (10, 0), (10, 10)), new[] { new TimingLineSegment(2, 5, 1), new TimingLineSegment(3, 6, 0) },
            new TimingLineOptions { Style = TimingLineStyle.BurnLine, StartOffsetMs = 50, ReverseOrder = true });
        var copy = (TimingLineEntity)ProjectJson.DeserializeEntities(ProjectJson.SerializeEntities(new Entity[] { line }))[0];
        Assert.Equal(line.Points, copy.Points);
        Assert.Equal(100, copy.Points[0].Z);
        Assert.Equal(line.Segments, copy.Segments);
        Assert.Equal(TimingLineStyle.BurnLine, copy.Style);
        Assert.Equal(50, copy.StartOffsetMs);
        Assert.True(copy.ReverseOrder);
        Assert.Equal(new Vec3(10, 10, 100), copy.OrderedPoints[0]);
    }

    [Fact]
    public void AngleArmsPointBackwardsAtTheirAngles()
    {
        var line = new TimingLineEntity { Points = Line((0, 0), (10, 0)), Angle1Deg = 0, Angle2Deg = 45 };
        // Arm 1 (left): line direction turned 90° anticlockwise; arm 2 (right): turned 135° clockwise.
        var e1 = line.AngleEnd(1);
        var e2 = line.AngleEnd(2);
        Assert.Equal(0, e1.X, 6); Assert.Equal(20, e1.Y, 6); Assert.Equal(100, e1.Z, 6);
        Assert.Equal(-20 / Math.Sqrt(2), e2.X, 6); Assert.Equal(-20 / Math.Sqrt(2), e2.Y, 6);
    }

    [Fact]
    public void ChevronIsSymmetricAboutItsRayAtItsAngle()
    {
        var apex = new Vec3(5, 5, 100);
        var pts = Chevron.Build(apex, new Vec3(0, 1, 0), 30, 10);
        Assert.Equal(apex, pts[1]);
        Assert.Equal(30, Chevron.AngleDeg(pts), 6);
        Assert.Equal(10, Vec3.Distance2D(pts[0], apex), 6);
        Assert.Equal(10, Vec3.Distance2D(pts[2], apex), 6);
        // left end on the left looking along the ray (north), both ends behind the vertex
        Assert.True(pts[0].X < apex.X && pts[2].X > apex.X);
        Assert.True(pts[0].Y < apex.Y && pts[2].Y < apex.Y);
        Assert.Equal(100, pts[0].Z, 6);
        var ray = Chevron.RayDir(pts);
        Assert.Equal(0, ray.X, 6); Assert.Equal(1, ray.Y, 6);
    }

    [Fact]
    public void ChevronTurnsAboutItsCentreKeepingItsShape()
    {
        var pts = Chevron.Build(new Vec3(0, 0, 100), new Vec3(1, 0, 0), 30, 12);
        var center = Chevron.Center(pts);
        var turned = Chevron.Rotate(pts, center, Math.PI / 2);
        Assert.Equal(center.X, Chevron.Center(turned).X, 6);
        Assert.Equal(center.Y, Chevron.Center(turned).Y, 6);
        Assert.Equal(30, Chevron.AngleDeg(turned), 6);
        Assert.Equal(12, Chevron.Length(turned), 6);
        var ray = Chevron.RayDir(turned);
        Assert.Equal(0, ray.X, 6); Assert.Equal(1, ray.Y, 6);
    }

    [Fact]
    public void ChevronReshapeKeepsVertexAndRayAndMirrorsTheOtherEnd()
    {
        var pts = Chevron.Build(new Vec3(0, 0, 100), new Vec3(0, 1, 0), 30, 10);
        // drag the left end to 45° off the axis, 20 m from the vertex
        var target = new Vec3(-20 * Math.Sin(Math.PI / 4), -20 * Math.Cos(Math.PI / 4), 100);
        var shaped = Chevron.Reshape(pts, target);
        Assert.Equal(pts[1], shaped[1]);
        Assert.Equal(90, Chevron.AngleDeg(shaped), 6);
        Assert.Equal(20, Chevron.Length(shaped), 6);
        Assert.Equal(target.X, shaped[0].X, 6); Assert.Equal(target.Y, shaped[0].Y, 6);
        Assert.Equal(-target.X, shaped[2].X, 6); Assert.Equal(target.Y, shaped[2].Y, 6);
        var ray = Chevron.RayDir(shaped);
        Assert.Equal(0, ray.X, 6); Assert.Equal(1, ray.Y, 6);
        // dragged past the vertex the angle stops at its largest
        Assert.Equal(Chevron.MaxAngleDeg, Chevron.AngleDeg(Chevron.Reshape(pts, new Vec3(-5, 5, 100))), 6);
    }

    [Fact]
    public void ChevronTimesGrowAlongTheRay()
    {
        // ray to the north: holes ahead of the tip fire later, holes inside the angle earlier, mirror holes at the same time
        var pts = Chevron.Build(new Vec3(0, 0, 100), new Vec3(0, 1, 0), 30, 10);
        var holes = new List<Hole> { H(0, -8), H(0, 0), H(0, 8), H(-3, 2), H(3, 2) };
        EbsTimingLine.Apply(Project, holes, pts, new[] { new TimingLineSegment(0, 8, 0), new TimingLineSegment(0, 8, 0) },
            new TimingLineOptions { Style = TimingLineStyle.TimeLine }, 20000, true);
        var t = holes.Select(h => h.ElectronicTimeMs!.Value).ToList();
        Assert.True(t[0] < t[1] && t[1] < t[2]);
        Assert.Equal(t[3], t[4], 6);
    }

    [Fact]
    public void ChevronFlagSurvivesProjectFileAndOldLinesReadWithout()
    {
        var line = EbsTimingLine.CreateEntity(Chevron.Build(new Vec3(0, 0, 100), new Vec3(0, 1, 0), 30, 10), new[] { new TimingLineSegment(0, 8, 0), new TimingLineSegment(0, 8, 0) }, new TimingLineOptions());
        line.IsChevron = true;
        var copy = (TimingLineEntity)ProjectJson.DeserializeEntities(ProjectJson.SerializeEntities(new Entity[] { line }))[0];
        Assert.True(copy.IsChevron);
        Assert.True(copy.HasHandles);
        var old = EbsTimingLine.CreateEntity(Line((0, 0), (10, 0), (10, 10), (0, 10)), Enumerable.Repeat(new TimingLineSegment(0, 8, 0), 3).ToList(), new TimingLineOptions());
        var oldCopy = (TimingLineEntity)ProjectJson.DeserializeEntities(ProjectJson.SerializeEntities(new Entity[] { old }))[0];
        Assert.False(oldCopy.IsChevron);
        Assert.False(oldCopy.HasHandles);
        Assert.Equal(4, oldCopy.Points.Count);
    }

    [Fact]
    public void ALineTimesTheHolesParallelToItTogetherAndGrowsToTheSideOfMostHoles()
    {
        // drawn west to east under a block that lies to the south: the ends are swapped so the times grow southwards
        var holes = new List<Hole> { H(0, -3), H(6, -3), H(0, -6), H(6, -6), H(3, 2) };
        var line = EbsTimingLine.FacingLine(new Vec3(-5, 0, 100), new Vec3(10, 0, 100), holes.Select(h => h.Collar));
        Assert.Equal(10, line[0].X);
        EbsTimingLine.Apply(Project, holes, line, new[] { new TimingLineSegment(0, 8, 0) }, new TimingLineOptions { Style = TimingLineStyle.TimeLine }, 20000, true);
        var t = holes.Select(h => h.ElectronicTimeMs!.Value).ToList();
        Assert.Equal(t[0], t[1], 6);
        Assert.Equal(t[2], t[3], 6);
        Assert.Equal(24, t[2] - t[0], 6);
        // the hole on the other side fires first, as the contour line has always timed it
        Assert.True(t[4] < t[0]);
        // already facing the holes the order stays
        Assert.Equal(-5, EbsTimingLine.FacingLine(new Vec3(-5, 0, 100), new Vec3(10, 0, 100), new[] { new Vec3(0, 3, 100) })[0].X);
    }

    [Fact]
    public void AReversedChevronTimesFromTheHolesItPointsAt()
    {
        // ray to the north: the hole ahead of the vertex fires first, the times grow back into the angle
        var pts = Chevron.Build(new Vec3(0, 0, 100), new Vec3(0, 1, 0), 30, 10);
        var holes = new List<Hole> { H(0, 2), H(0, -4), H(0, -8), H(-2, -6), H(2, -6) };
        EbsTimingLine.Apply(Project, holes, pts, new[] { new TimingLineSegment(0, 8, 0), new TimingLineSegment(0, 8, 0) },
            new TimingLineOptions { Style = TimingLineStyle.TimeLine, ReverseOrder = true, StartOffsetMs = 100 }, 20000, true);
        var t = holes.Select(h => h.ElectronicTimeMs!.Value).ToList();
        Assert.Equal(100, t[0], 6);
        Assert.True(t[0] < t[1] && t[1] < t[2]);
        Assert.Equal(t[3], t[4], 6);
    }

    [Fact]
    public void InAMixedBlockThePyrotechnicHolesFireByTheTieUpAndTheElectronicOnesByTheLine()
    {
        var p = BlastProject.CreateNew();
        var nonel = H(0, 5, Pyrotechnic(p));
        var ebs = H(10, 5);
        // a time left by an older version on the pyrotechnic hole is cleared
        nonel.ElectronicTimeMs = 300;
        p.Add(nonel); p.Add(ebs);
        var ip = new InitiationPoint { Position = new Vec3(-5, 5, 100), StartTimeMs = 0 };
        p.Add(ip);
        p.Add(new TieEntity { FromId = ip.Id, ToId = nonel.Id, DelayMs = 42 });
        var r = EbsTimingLine.Apply(p, new List<Hole> { nonel, ebs }, Line((-5, 0), (20, 0)), new[] { new TimingLineSegment(0, 8, 0) },
            new TimingLineOptions { StartOffsetMs = 100 }, 20000, true);
        Assert.Equal(1, r.Timed);
        Assert.Equal(1, r.NotElectronic);
        Assert.Null(nonel.ElectronicTimeMs);
        Timing.Solve(p);
        Assert.Equal(42 + Charging.PrimerDelay(p, nonel.Decks[0].Primers[0]), nonel.FiringTimeMs!.Value, 6);
        Assert.Equal(p.Electronic.BaseTimeMs + 100, ebs.FiringTimeMs!.Value, 6);
    }

    [Fact]
    public void AChevronLeavesTheHolesBehindItsVertexWithoutATime()
    {
        // ray to the north: the times grow from the vertex northwards, the holes inside the angle are behind it
        var pts = Chevron.Build(new Vec3(0, 0, 100), new Vec3(0, 1, 0), 30, 10);
        var ahead = H(0, 6);
        var near = H(0, 1);
        var behind = H(0, -6);
        behind.ElectronicTimeMs = 50;
        var manual = H(0, -8);
        Timing.SetTime(manual, 77, true);
        var holes = new List<Hole> { ahead, near, behind, manual };
        var r = EbsTimingLine.Apply(Project, holes, pts, new[] { new TimingLineSegment(0, 8, 0), new TimingLineSegment(0, 8, 0) },
            new TimingLineOptions { Style = TimingLineStyle.TimeLine, Front = TimingFrontShape.Chevron, StartOffsetMs = 10 }, 20000, true);
        Assert.Equal(10, near.ElectronicTimeMs!.Value, 6);
        Assert.True(ahead.ElectronicTimeMs > near.ElectronicTimeMs);
        Assert.Null(behind.ElectronicTimeMs);
        Assert.Contains(behind, r.Unreached);
        // a time set by hand stays
        Assert.Equal(77, manual.ElectronicTimeMs);
    }

    [Fact]
    public void AnOldFileLosesTheTimesALineGaveItsPyrotechnicHoles()
    {
        var p = BlastProject.CreateNew();
        var nonel = H(0, 5, Pyrotechnic(p));
        var byHand = H(5, 5, Pyrotechnic(p));
        var ebs = H(10, 5);
        nonel.ElectronicTimeMs = 120;
        Timing.SetTime(byHand, 60, true);
        ebs.ElectronicTimeMs = 140;
        foreach (var h in new[] { nonel, byHand, ebs }) p.Add(h);
        p.Add(new TimingLineEntity { Points = Line((0, 0), (10, 0)), Segments = { new TimingLineSegment(0, 8, 0) }, HoleIds = { nonel.Id, byHand.Id, ebs.Id } });
        using var ms = new MemoryStream();
        BlastDesigner.IO.ProjectFile.Write(p, ms);
        ms.Position = 0;
        var opened = BlastDesigner.IO.ProjectFile.Read(ms);
        Assert.Null(opened.Holes.First(h => h.Id == nonel.Id).ElectronicTimeMs);
        Assert.Equal(60, opened.Holes.First(h => h.Id == byHand.Id).ElectronicTimeMs);
        Assert.Equal(140, opened.Holes.First(h => h.Id == ebs.Id).ElectronicTimeMs);
    }

    [Fact]
    public void AHoleWhosePrimerOffsetPassesTheLargestDelayIsNotProgrammed()
    {
        var p = BlastProject.CreateNew();
        p.Electronic.UseOffsets = true;
        var h = H(0, 5);
        h.Decks[0].Primers.Add(new Primer { Depth = 6, DetonatorId = Electronic(p), OffsetMs = 25 });
        var plain = H(10, 5);
        Assert.Equal(25, Charging.MaxPrimerOffset(p, h));
        // the line gives both 19 990 ms: 19 990 + 25 is past 20 000
        var r = EbsTimingLine.Apply(p, new List<Hole> { h, plain }, Line((-5, 0), (20, 0)), new[] { new TimingLineSegment(0, 8, 0) },
            new TimingLineOptions { StartOffsetMs = 19990 }, 20000);
        Assert.Equal(new[] { h }, r.MaxTimeExceeded);
        Assert.Null(h.ElectronicTimeMs);
        Assert.Equal(19990, plain.ElectronicTimeMs);
        // as in SHOTPlus (HoleLoading.MaxOffset): the offset counts without UseOffsets too, the deck delays do not
        p.Electronic.UseOffsets = false;
        p.Electronic.IntraDeckDelayMs = 15;
        Assert.Equal(25, Charging.MaxPrimerOffset(p, h));
        h.Decks[0].Primers[^1].OffsetMs = -10;
        Assert.Equal(0, Charging.MaxPrimerOffset(p, h));
    }

    [Fact]
    public void RaisingAPrimerUpTheHoleRetimesItFromTheLineWithoutOpeningTheWindow()
    {
        var p = BlastProject.CreateNew();
        // a hole leaning north with two charges, its primer in the lower one
        var h = new Hole { Label = "1", Collar = new Vec3(0, 0, 100), Length = 12, Angle = 30, Bearing = 0 };
        h.Decks.Add(new Deck { Top = 0, Bottom = 3, ProductId = p.Products.First(x => x.Kind == ProductKind.Stemming).Id });
        h.Decks.Add(new Deck { Top = 3, Bottom = 6, ProductId = Explosive(p) });
        h.Decks.Add(new Deck { Top = 6, Bottom = 7, ProductId = p.Products.First(x => x.Kind == ProductKind.Stemming).Id });
        h.Decks.Add(new Deck { Top = 7, Bottom = 12, ProductId = Explosive(p), Primers = { new Primer { Depth = 11.5, DetonatorId = Electronic(p) } } });
        var other = H(0, -10);
        var manual = H(5, -10);
        Timing.SetTime(manual, 4321, true);
        p.Add(h); p.Add(other); p.Add(manual);
        // a contour line along x to the south: the time grows northwards, with the distance from it
        var line = EbsTimingLine.CreateEntity(Line((-20, -20), (20, -20)), new[] { new TimingLineSegment(0, 10, 0) }, new TimingLineOptions());
        line.HoleIds = new List<Guid> { h.Id, other.Id, manual.Id };
        p.Add(line);
        EbsTimingLine.Apply(p, p.Holes.ToList(), line);
        var doc = new Document(p);
        var before = h.ElectronicTimeMs!.Value;

        doc.Execute("Raise the primer", pr =>
        {
            var hole = (Hole)pr.FindEntity(h.Id)!;
            var primer = hole.Decks[3].Primers[0];
            hole.Decks[3].Primers.Clear();
            primer.Depth = 5.5;
            hole.Decks[1].Primers.Add(primer);
        });
        var raised = (Hole)doc.Project.FindEntity(h.Id)!;
        // the middle of the upper charge is 5 m higher up the hole: 5 · sin 30° = 2.5 m less to the north, 25 ms earlier
        Assert.Equal(before - 25, raised.ElectronicTimeMs!.Value, 6);
        Assert.Equal(4321, ((Hole)doc.Project.FindEntity(manual.Id)!).ElectronicTimeMs);
        // one undo step brings both back
        doc.Undo();
        Assert.Equal(before, ((Hole)doc.Project.FindEntity(h.Id)!).ElectronicTimeMs!.Value, 6);
        // a change that leaves the holes as they are reprograms nothing
        doc.Execute("Rename", pr => { foreach (var x in pr.Holes) x.ElectronicTimeMs = x.ElectronicTimeManual ? x.ElectronicTimeMs : 1; ((Hole)pr.FindEntity(other.Id)!).Notes = "n"; });
        Assert.Equal(1, ((Hole)doc.Project.FindEntity(other.Id)!).ElectronicTimeMs);
    }

    [Fact]
    public void TheShapeOfAFrontSurvivesTheProjectFile()
    {
        var line = EbsTimingLine.CreateEntity(Line((0, 0), (10, 0)), new[] { new TimingLineSegment(0, 8, 0) }, new TimingLineOptions());
        line.Front = TimingFrontShape.Line;
        var copy = (TimingLineEntity)ProjectJson.DeserializeEntities(ProjectJson.SerializeEntities(new Entity[] { line }))[0];
        Assert.Equal(TimingFrontShape.Line, copy.Front);
        Assert.False(copy.IsChevron);
        Assert.True(copy.HasHandles);
        // a chevron of a file written before the shape: only its flag
        var old = ProjectJson.DeserializeEntities(ProjectJson.SerializeEntities(new Entity[] { line }).Replace("\"front\": \"Line\"", "\"isChevron\": true").Replace("\"front\":\"Line\"", "\"isChevron\":true"));
        Assert.Equal(TimingFrontShape.Chevron, ((TimingLineEntity)old[0]).Front);
    }
}
