using System.Text;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;

namespace BlastDesigner.Tests;

public class PredictionTests
{
    [Fact]
    public void Flyrock_CrateringMatchesFormula()
    {
        var expected = 13.5 * 13.5 / 9.81 * Math.Pow(Math.Sqrt(10) / 3, 2.6);
        Assert.Equal(expected, Flyrock.Cratering(13.5, 10, 3), 6);
        Assert.Equal(0, Flyrock.Rifling(13.5, 10, 3, 0), 6);
        Assert.True(Flyrock.Rifling(13.5, 10, 3, 45) > 0.99 * expected);
    }

    [Fact]
    public void KuzRam_X50AndCurves()
    {
        var x50 = Fragmentation.X50(8, 0.5, 100, 100);
        Assert.InRange(x50, 32.5, 33.1);
        Assert.Equal(0.5, Fragmentation.RosinRammler(x50, x50, 1.3), 3);
        Assert.Equal(0.5, Fragmentation.SwebrecPassing(x50, x50, 200, 1.3), 9);
        Assert.Equal(x50, Fragmentation.SizeAt(0.5, x50, 1.3), 1);
    }

    [Fact]
    public void SiteLaw_RecoversExactConstants()
    {
        var records = new List<VibrationRecord>();
        foreach (var (d, q) in new[] { (100.0, 50.0), (200, 80), (400, 120), (800, 200), (300, 60) })
            records.Add(new VibrationRecord { DistanceM = d, ChargeKg = q, PpvMm = 1000 * Math.Pow(d / Math.Sqrt(q), -1.5) });
        var fit = SiteLaw.Regress(records)!;
        Assert.Equal(1000, fit.K50, 3);
        Assert.Equal(1.5, fit.B, 6);
        Assert.Equal(1, fit.R2, 6);
    }

    [Fact]
    public void StudentT_Quantiles()
    {
        Assert.Equal(1.812, Stats.StudentTQuantile(0.95, 10), 2);
        Assert.Equal(2.571, Stats.StudentTQuantile(0.975, 5), 1);
        Assert.Equal(1.645, Stats.NormalQuantile(0.95), 3);
    }

    [Fact]
    public void ScaleLaw_DistanceForLimitInvertsPrediction()
    {
        var m = new VibrationModel { SiteK = 1140, SiteB = 1.6 };
        var d = SiteLaw.DistanceForPpv(m.SiteK, m.SiteB, 200, 10);
        Assert.Equal(10, Vibration.Ppv(m, d, 200), 6);
    }
}

public class DesignRuleTests
{
    private static (BlastProject P, Hole H) Loaded(double water = 0)
    {
        var p = BlastProject.CreateNew();
        var h = p.Add(new Hole { Label = "1", Collar = new Vec3(0, 0, 50), Length = 10, WaterDepth = water });
        return (p, h);
    }

    [Fact]
    public void WetAlternate_ReplacesBottomOfColumn()
    {
        var (p, h) = Loaded(3);
        var rule = p.LoadingRules[0];
        var heavy = p.Products.First(x => x.Name.StartsWith("Heavy"));
        rule.WetAlternateProductId = heavy.Id;
        Charging.ApplyRule(p, h, rule);
        var bottom = h.Decks.OrderBy(d => d.Top).Last();
        Assert.Equal(heavy.Id, bottom.ProductId);
        Assert.Equal(7, bottom.Top, 6);
        Assert.Equal(10, bottom.Bottom, 6);
        Assert.Single(h.Decks.SelectMany(d => d.Primers));
        Assert.DoesNotContain(Charging.Validate(p, h), i => i.Contains("overlaps"));
    }

    [Fact]
    public void HardAlternate_UsesMwdIntervals()
    {
        var (p, h) = Loaded();
        h.Mwd = new() { new() { Depth = 2, Hardness = 0.5 }, new() { Depth = 5, Hardness = 0.9 }, new() { Depth = 8, Hardness = 0.5 } };
        var rule = p.LoadingRules[0];
        var emulsion = p.Products.First(x => x.Name == "Bulk emulsion");
        rule.HardAlternateProductId = emulsion.Id;
        Charging.ApplyRule(p, h, rule);
        var hard = h.Decks.Single(d => d.ProductId == emulsion.Id);
        Assert.Equal(3.5, hard.Top, 6);
        Assert.Equal(6.5, hard.Bottom, 6);
    }

    [Fact]
    public void InitiationRule_SetsDetonatorAndPrimerCount()
    {
        var (p, h) = Loaded();
        Charging.ApplyRules(p, p.Holes);
        var det = p.Products.First(x => x.Name == "Downhole det 400 ms");
        p.InitiationRules.Add(new InitiationRule { DetonatorId = det.Id, PrimersPerDeck = 2 });
        Assert.Equal(1, Initiation.ApplyRules(p, p.Holes));
        var primers = h.Decks.SelectMany(d => d.Primers).ToList();
        Assert.Equal(2, primers.Count);
        Assert.All(primers, pr => Assert.Equal(det.Id, pr.DetonatorId));
    }

    [Fact]
    public void Tolerance_FlagsCollarAndShortHole()
    {
        var (p, h) = Loaded();
        h.ActualCollar = h.Collar + new Vec3(0.5, 0, 0);
        h.ActualLength = 8;
        var r = Tolerance.Check(p).Single();
        Assert.Contains("Collar position", r.Failures);
        Assert.Contains("Short", r.Failures);
        Assert.Contains("Redrill", r.Failures);
    }

    [Fact]
    public void StrataIntercept_FindsDepthOfFlatSurface()
    {
        var (p, h) = Loaded();
        var s = Surfaces.Triangulate(new[] { new Vec3(-10, -10, 45), new Vec3(10, -10, 45), new Vec3(10, 10, 45), new Vec3(-10, 10, 45) });
        var hit = Strata.Intercepts(new[] { h }, new[] { s }).Single();
        Assert.Equal(5, hit.Depth, 3);
    }
}

public class AnalysisTests
{
    private static BlastProject Grid(int rows, int cols, double burden, double spacing)
    {
        var p = BlastProject.CreateNew();
        p.Design.Burden = burden;
        p.Design.Spacing = spacing;
        p.Header.BlastOrientation = 0; // free face to the north (+Y)
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                p.Add(new Hole { Label = $"{r}-{c}", Collar = new Vec3(c * spacing, -r * burden, 50), Length = 10 });
        return p;
    }

    [Fact]
    public void BurdenCheck_MeasuresRowSpacingTowardsFace()
    {
        var p = Grid(3, 4, 4, 5);
        var checks = Analysis.CheckBurden(p);
        var back = checks.Where(c => c.Hole.Label.StartsWith("2-")).ToList();
        Assert.All(back, c => Assert.Equal(4, c.Burden!.Value, 6));
        Assert.All(back, c => Assert.Equal(5, c.Spacing!.Value, 6));
        Assert.All(checks.Where(c => c.Hole.Label.StartsWith("0-")), c => Assert.Null(c.Burden));
    }

    [Fact]
    public void FaceBurden_UsesCrestAndToe()
    {
        var h = new Hole { Collar = new Vec3(0, -4, 50), Length = 10 };
        var crest = new PolylineEntity { Points = { new Vec3(-10, 0, 50), new Vec3(10, 0, 50) } };
        var toe = new PolylineEntity { Points = { new Vec3(-10, 2, 40), new Vec3(10, 2, 40) } };
        var fb = Analysis.FrontRowBurden(h, crest, toe);
        Assert.Equal(4, fb.CollarBurden, 6);
        Assert.Equal(6, fb.ToeBurden, 6);
        Assert.Equal(0, fb.DepthOfMin, 6);
    }

    [Fact]
    public void Quantities_SubsetKeepsItsShareOfTheVolume()
    {
        var p = Grid(4, 6, 4, 5);
        var all = Quantities.Compute(p);
        var inner = p.Holes.Where(h => h.Label is "1-2" or "2-3").ToList();
        var sub = Quantities.Compute(p, inner);
        Assert.Equal(2 * 4 * 5 * 10, sub.Volume, 6);
        Assert.True(sub.Volume < all.Volume / 6);
    }

    [Fact]
    public void ChargeVsTime_IsCumulative()
    {
        var p = Grid(1, 3, 4, 5);
        Charging.ApplyRules(p, p.Holes);
        var t = 0.0;
        foreach (var h in p.Holes) { Timing.SetTime(h, t); t += 25; }
        var pts = Analysis.ChargeVsTime(p, 10);
        Assert.Equal(3, pts.Count);
        Assert.Equal(pts.Sum(x => x.ChargeKg), pts[^1].CumulativeKg, 6);
    }
}

public class CadOpsTests
{
    [Fact]
    public void OffsetPolyline_ShiftsLeft()
    {
        var off = CadOps.OffsetPolyline(new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 0) }, 2);
        Assert.All(off, p => Assert.Equal(2, p.Y, 9));
    }

    [Fact]
    public void ExtendToIntersection_ReachesTarget()
    {
        var r = CadOps.ExtendToIntersection(new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0) }, new[] { new Vec3(5, -5, 0), new Vec3(5, 5, 0) }, false)!;
        Assert.Equal(5, r[^1].X, 9);
    }

    [Fact]
    public void CurvedLayout_CreatesRowsAlongOffsets()
    {
        var baseline = Enumerable.Range(0, 11).Select(i => new Vec3(i * 2, Math.Sin(i / 3.0) * 3, 50)).ToList();
        var holes = CadOps.CurvedLayout(baseline, 3, 4, 5, true, false, new HoleParameters(), out var rows);
        Assert.Equal(3, rows.Count);
        Assert.Equal(holes.Count, rows.Sum(r => r.HoleIds.Count));
    }

    [Fact]
    public void Envelope_TakesLowerOfTwoPlanes()
    {
        var a = Surfaces.Triangulate(new[] { new Vec3(0, 0, 10), new Vec3(10, 0, 10), new Vec3(10, 10, 10), new Vec3(0, 10, 10) });
        var b = Surfaces.Triangulate(new[] { new Vec3(0, 0, 5), new Vec3(10, 0, 15), new Vec3(10, 10, 15), new Vec3(0, 10, 5) });
        var min = CadOps.Envelope(a, b, true);
        Assert.Equal(5.5, min.ElevationAt(0.5, 5)!.Value, 1);
        Assert.Equal(10, min.ElevationAt(9.5, 5)!.Value, 1);
    }
}

public class InterchangeTests
{
    [Fact]
    public void Utm_RoundTripAndCentralMeridian()
    {
        var (e, n) = Utm.FromLatLon(0, 3, 31);
        Assert.Equal(500000, e, 3);
        Assert.Equal(0, n, 3);
        var (e2, n2) = Utm.FromLatLon(-31.95, 115.86, 50);
        var (lat, lon) = Utm.ToLatLon(e2, n2, 50, true);
        Assert.Equal(-31.95, lat, 7);
        Assert.Equal(115.86, lon, 7);
    }

    [Fact]
    public void Kml_RoundTripsHoleCollars()
    {
        var p = BlastProject.CreateNew();
        p.Coordinates = new SiteCoordinateSystem { UtmZone = 50, SouthernHemisphere = true, OffsetEasting = 390000, OffsetNorthing = 6460000 };
        p.Add(new Hole { Label = "A1", Collar = new Vec3(123.4, 567.8, 30) });
        var kml = Interchange.ExportKml(p, p.Entities);
        var path = Path.Combine(Path.GetTempPath(), $"bd6_{Guid.NewGuid():N}.kml");
        File.WriteAllText(path, kml);
        try
        {
            var pt = Interchange.ImportKml(path, p.Coordinates).OfType<PointEntity>().Single();
            Assert.Equal(123.4, pt.Position.X, 2);
            Assert.Equal(567.8, pt.Position.Y, 2);
            Assert.Equal("A1", pt.Label);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Iredes_RoundTripsHoles()
    {
        var p = BlastProject.CreateNew();
        p.Add(new Hole { Label = "H1", Collar = new Vec3(10, 20, 100), Length = 12, Angle = 15, Bearing = 90, Diameter = 115 });
        p.Add(new Hole { Label = "H2", Collar = new Vec3(14, 20, 100), Length = 11 });
        var path = Path.Combine(Path.GetTempPath(), $"bd6_{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, Interchange.ExportIredes(p, p.Holes));
        try
        {
            var holes = Interchange.ImportIredes(path, p.Design);
            Assert.Equal(2, holes.Count);
            Assert.Equal("H1", holes[0].Label);
            Assert.Equal(15, holes[0].Angle, 2);
            Assert.Equal(90, holes[0].Bearing, 2);
            Assert.Equal(115, holes[0].Diameter, 6);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Las_ReadsFormat0Points()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bd6_{Guid.NewGuid():N}.las");
        using (var bw = new BinaryWriter(File.Create(path)))
        {
            var header = new byte[227];
            Encoding.ASCII.GetBytes("LASF").CopyTo(header, 0);
            header[24] = 1; header[25] = 2;
            BitConverter.GetBytes((ushort)227).CopyTo(header, 94);
            BitConverter.GetBytes(227u).CopyTo(header, 96);
            header[104] = 0;
            BitConverter.GetBytes((ushort)20).CopyTo(header, 105);
            BitConverter.GetBytes(3u).CopyTo(header, 107);
            for (int i = 0; i < 3; i++) BitConverter.GetBytes(0.01).CopyTo(header, 131 + i * 8);
            BitConverter.GetBytes(1000.0).CopyTo(header, 155);
            bw.Write(header);
            foreach (var (x, y, z) in new[] { (100, 200, 300), (5000, 200, 310), (100, 9000, 320) })
            {
                var rec = new byte[20];
                BitConverter.GetBytes(x).CopyTo(rec, 0);
                BitConverter.GetBytes(y).CopyTo(rec, 4);
                BitConverter.GetBytes(z).CopyTo(rec, 8);
                bw.Write(rec);
            }
        }
        try
        {
            var (info, pts) = Interchange.ReadLas(path, 0.1);
            Assert.Equal(3, info.PointCount);
            Assert.Equal(3, pts.Count);
            Assert.Contains(pts, q => Math.Abs(q.X - 1001) < 1e-9 && Math.Abs(q.Y - 2) < 1e-9 && Math.Abs(q.Z - 3) < 1e-9);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Mwd_AttachesSamplesByHoleLabel()
    {
        var h = new Hole { Label = "7" };
        var n = Interchange.ImportMwd(new[] { "Hole,Depth,ROP,Hardness", "7,1,0.8,0.2", "7,2,0.4,0.9", "8,1,1,1" }, new[] { h });
        Assert.Equal(1, n);
        Assert.Equal(2, h.Mwd!.Count);
        Assert.Equal(0.9, h.Mwd[1].Hardness, 9);
    }
}

public class SettingsTests
{
    [Fact]
    public void Upgrade_MovesOldLightCanvasToDark_KeepsUserColours()
    {
        var old = new ProgramSettings { BackgroundColor = 0xFFFFFFFF, SelectionColor = 0xFFFF00FF, SettingsVersion = 0 };
        old.Upgrade();
        Assert.Equal(0xFF101215u, old.BackgroundColor);
        Assert.Equal(0xFF4C9BFFu, old.SelectionColor);
        var custom = new ProgramSettings { BackgroundColor = 0xFFEEEEEE, SettingsVersion = 0 };
        custom.Upgrade();
        Assert.Equal(0xFFEEEEEEu, custom.BackgroundColor);
        Assert.Equal(ProgramSettings.CurrentVersion, custom.SettingsVersion);
    }

    [Fact]
    public void Adapt_LightensDarkColoursOnlyOnDarkBackground()
    {
        var navy = new SkiaSharp.SKColor(0xFF1E3A8A);
        using var bmp = new SkiaSharp.SKBitmap(64, 64);
        using var canvas = new SkiaSharp.SKCanvas(bmp);
        var cam = new BlastDesigner.Rendering.Camera { Width = 64, Height = 64 };
        BlastDesigner.Rendering.PlanRenderer.Render(canvas, cam, BlastProject.CreateNew(), new BlastDesigner.Rendering.SceneOptions { Background = new SkiaSharp.SKColor(0xFF101215) });
        var lightened = BlastDesigner.Rendering.PlanRenderer.Adapt(navy);
        Assert.True(lightened.Red + lightened.Green + lightened.Blue > navy.Red + navy.Green + navy.Blue + 150);
        BlastDesigner.Rendering.PlanRenderer.Render(canvas, cam, BlastProject.CreateNew(), new BlastDesigner.Rendering.SceneOptions { Background = SkiaSharp.SKColors.White });
        Assert.Equal(navy, BlastDesigner.Rendering.PlanRenderer.Adapt(navy));
    }
}

public class SelectionUndoTests
{
    private static (BlastDesigner.Domain.Document Doc, Guid A, Guid B) Make()
    {
        var p = new BlastProject();
        var a = new PointEntity { Position = new Vec3(0, 0, 0) };
        var b = new PointEntity { Position = new Vec3(5, 0, 0) };
        p.Entities.Add(a); p.Entities.Add(b);
        return (new BlastDesigner.Domain.Document(p), a.Id, b.Id);
    }

    [Fact]
    public void SelectionChangesAreUndoSteps()
    {
        var (doc, a, b) = Make();
        doc.Select(new[] { a });
        doc.Select(new[] { b }, BlastDesigner.Domain.SelectionMode.Add);
        doc.Select(new[] { b }, BlastDesigner.Domain.SelectionMode.Add); // no change: no step
        Assert.Equal(2, doc.UndoNames.Count);
        doc.Undo();
        Assert.Equal(new[] { a }, doc.Selection);
        doc.Undo();
        Assert.Empty(doc.Selection);
        Assert.False(doc.IsModified);
        doc.Redo();
        Assert.Equal(new[] { a }, doc.Selection);
    }

    [Fact]
    public void UndoOfAModelChangeRestoresItsSelection()
    {
        var (doc, a, b) = Make();
        doc.Select(new[] { a });
        doc.Execute("Move", p => p.FindEntity(a)!.Translate(new Vec3(1, 0, 0)));
        doc.Select(new[] { b });
        doc.Undo(); // selection back to a
        Assert.Equal(new[] { a }, doc.Selection);
        Assert.Equal(1, ((PointEntity)doc.Project.FindEntity(a)!).Position.X);
        doc.Undo(); // move undone, selection still a
        Assert.Equal(0, ((PointEntity)doc.Project.FindEntity(a)!).Position.X);
        Assert.Equal(new[] { a }, doc.Selection);
    }

    [Fact]
    public void UnrecordedSelectionAddsNoStep()
    {
        var (doc, a, _) = Make();
        doc.Select(new[] { a }, record: false);
        Assert.Empty(doc.UndoNames);
    }
}
