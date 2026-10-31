using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;

namespace BlastDesigner.Tests;

/// <summary>Synthetic bench surveys, their DXF form and the segment features the recognition learns from.</summary>
public class SurveySynthTests
{
    private static List<(SurveySegment Segment, double[] Values)> Features(SurveyScene s) =>
        SurveyFeatures.Compute(s.Lines.Select(l => (l, s.Layers[l.Id])).ToList(), s.Points.Select(p => p.Position).ToList(), s.Collars);

    [Fact]
    public void SameSeed_SameScene()
    {
        var a = SurveySynth.Generate(42);
        var b = SurveySynth.Generate(42);
        Assert.Equal(a.Entities.Count, b.Entities.Count);
        Assert.Equal(a.Lines.First().Points, b.Lines.First().Points);
        Assert.Equal(a.LineClasses.Values, b.LineClasses.Values);
        Assert.NotEqual(a.Lines.First().Points[0], SurveySynth.Generate(43).Lines.First().Points[0]);
    }

    [Fact]
    public void Scenes_HaveCrestsAndClassesForEverything()
    {
        for (int seed = 1; seed <= 30; seed++)
        {
            var s = SurveySynth.Generate(seed);
            Assert.Contains(s.LineClasses.Values, c => c == SurveyLineClass.Crest);
            Assert.All(s.Lines, l => Assert.True(s.LineClasses.ContainsKey(l.Id)));
            Assert.All(s.Points, p => Assert.True(s.PointClasses.ContainsKey(p.Id)));
            Assert.All(s.Entities, e => Assert.True(s.Layers.ContainsKey(e.Id)));
        }
        Assert.Contains(Enumerable.Range(1, 30).Select(seed => SurveySynth.Generate(seed)), s => s.LineClasses.Values.Contains(SurveyLineClass.Toe));
    }

    /// <summary>The wall's curvature is limited so the offset crest lines of different benches never cross.</summary>
    [Fact]
    public void CrestsOfDifferentBenches_DoNotCross()
    {
        for (int seed = 1; seed <= 40; seed++)
        {
            var s = SurveySynth.Generate(seed);
            var lines = s.Lines.Where(l => s.LineClasses[l.Id] == SurveyLineClass.Crest).ToList();
            for (int i = 0; i < lines.Count; i++)
                for (int j = i + 1; j < lines.Count; j++)
                {
                    // pieces of one crest (gaps) are close in height; different benches are a bench apart
                    if (Math.Abs(lines[i].Points.Average(p => p.Z) - lines[j].Points.Average(p => p.Z)) < 2.5) continue;
                    for (int a = 1; a < lines[i].Points.Count; a++)
                        for (int b = 1; b < lines[j].Points.Count; b++)
                            Assert.False(GeoMath.SegmentIntersect2D(lines[i].Points[a - 1], lines[i].Points[a], lines[j].Points[b - 1], lines[j].Points[b], out _, out _),
                                $"seed {seed}: crests cross");
                }
        }
    }

    [Fact]
    public void Traps_PutTheirLookAlikesIn()
    {
        var road = SurveySynth.Generate(5, trap: SurveyTrap.RoadAlongCrest);
        Assert.True(road.LineClasses.Values.Count(c => c == SurveyLineClass.Other) >= 2);
        var contours = SurveySynth.Generate(5, trap: SurveyTrap.ContoursAlongWall);
        Assert.Contains(contours.LineClasses.Values, c => c == SurveyLineClass.Contour);
        Assert.DoesNotContain(SurveySynth.Generate(5, trap: SurveyTrap.MissingToes).LineClasses.Values, c => c == SurveyLineClass.Toe);
        Assert.True(SurveySynth.Generate(5, trap: SurveyTrap.NarrowBerms).Benches.Count >= 3);
    }

    /// <summary>Every scene goes through the real import; the true classes must reach every imported survey object.</summary>
    [Fact]
    public void DxfRoundTrip_CarriesTheClassesOver()
    {
        foreach (var style in Enum.GetValues<SurveyPointStyle>())
            for (int seed = 1; seed <= 4; seed++)
            {
                var s = SurveySynth.Generate(seed, SurveySynthOptions.Train with { PointStyles = new[] { (style, 1.0) } });
                var path = Path.Combine(Path.GetTempPath(), $"bd_synth_{Guid.NewGuid():N}.dxf");
                try
                {
                    SurveyDxf.Write(s, path);
                    var imported = CadFiles.ReadCad(path, new CadImportOptions());
                    var (lines, points, unmatched) = SurveyDxf.Transfer(s, imported.Items.Select(i => i.Entity));
                    Assert.Equal(0, unmatched);
                    Assert.Equal(s.Lines.Count(), lines.Count);
                    Assert.Equal(s.Points.Count(), points.Count);
                    if (style == SurveyPointStyle.Block)
                        // blocks keep the elevation in an attribute; the import puts it back
                        Assert.All(imported.Items.Select(i => i.Entity).OfType<PointEntity>(), p => Assert.True(Math.Abs(p.Position.Z) > 1));
                }
                finally { File.Delete(path); }
            }
    }

    [Fact]
    public void Features_DoNotDependOnPositionOrTurn()
    {
        var s = SurveySynth.Generate(17);
        var before = Features(s);
        var (c, sn) = (Math.Cos(1.1), Math.Sin(1.1));
        // turned and moved in plan; elevations stay (lines at 0 are plan drawing, not survey)
        Vec3 Move(Vec3 p) => new(p.X * c - p.Y * sn + 1234.5, p.X * sn + p.Y * c - 987.6, p.Z);
        foreach (var e in s.Entities) e.Transform(Move);
        for (int i = 0; i < s.Collars.Count; i++) s.Collars[i] = Move(s.Collars[i]);
        var after = Features(s);
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Segment, after[i].Segment);
            for (int k = 0; k < SurveyFeatures.Names.Length; k++)
                Assert.True(Math.Abs(before[i].Values[k] - after[i].Values[k]) < 1e-6, $"{SurveyFeatures.Names[k]}: {before[i].Values[k]} → {after[i].Values[k]}");
        }
    }

    /// <summary>The features carry what tells a crest from a toe: a lower neighbour under a crest, a higher one over a toe.</summary>
    [Fact]
    public void Features_SeparateCrestsFromToes()
    {
        var rows = Enumerable.Range(100, 25).Select(seed => SurveySynth.Generate(seed)).SelectMany(s => Features(s).Select(f => (Class: s.LineClasses[f.Segment.LineId], f.Values))).ToList();
        Assert.All(rows, r => Assert.Equal(SurveyFeatures.Names.Length, r.Values.Length));
        double Med(SurveyLineClass c, string name) => SurveyFeatures.Median(rows.Where(r => r.Class == c).Select(r => r.Values[Array.IndexOf(SurveyFeatures.Names, name)]).ToList());
        Assert.True(Med(SurveyLineClass.Crest, "low_dz") < -3);
        Assert.True(Med(SurveyLineClass.Crest, "pts_low_dz") < -2);
        Assert.True(Med(SurveyLineClass.Toe, "high_dz") > 3);
        Assert.True(Med(SurveyLineClass.Toe, "pts_high_dz") > 2);
    }

    [Fact]
    public void Segments_AreAboutTheTargetLength()
    {
        var pts = Enumerable.Range(0, 21).Select(i => new Vec3(i * 5, 0, 600)).ToList(); // 100 m, a vertex every 5 m
        var segs = SurveyFeatures.Segments(Guid.NewGuid(), pts);
        Assert.Equal(0, segs[0].From);
        Assert.Equal(20, segs[^1].To);
        Assert.All(segs.Zip(segs.Skip(1)), p => Assert.Equal(p.First.To, p.Second.From));
        // pieces of 15 m; a tail of at least half that stays on its own, a shorter one joins the piece before
        Assert.All(segs.SkipLast(1), s => Assert.Equal(15, (s.To - s.From) * 5.0));
        Assert.InRange((segs[^1].To - segs[^1].From) * 5.0, 7.5, 22.5);
    }
}
