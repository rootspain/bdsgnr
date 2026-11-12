using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.Tests;

/// <summary>The turn of a pattern grid with the mouse (the rotate tool and the circle of the Select tool share it).</summary>
public class PatternTurnTests
{
    private static readonly Vec3[] Field = { new(0, 0, 100), new(47, -3, 100), new(53, 31, 100), new(21, 44, 100), new(-6, 27, 100) };

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 5)]
    [InlineData(false, true, 0.1)]
    [InlineData(true, true, 0.1)]
    public void TheStep_IsOneDegree_FiveWithShift_ATenthWithCtrl(bool shift, bool control, double step) =>
        Assert.Equal(step, PatternTurn.Step(shift, control));

    [Theory]
    [InlineData(10, 17.44, 1, 7)]
    [InlineData(10, 17.44, 5, 5)]
    [InlineData(10, 17.44, 0.1, 7.4)]
    [InlineData(10, 2.6, 5, -5)]
    [InlineData(10, 2.4, 5, -10)]
    public void TheTurn_IsRoundedToTheStep(double start, double now, double step, double turn) =>
        Assert.Equal(turn, PatternTurn.Snapped(start, now, step), 9);

    [Theory]
    [InlineData(350, 10, 20)]
    [InlineData(10, 350, -20)]
    [InlineData(179, 181, 2)]
    [InlineData(181, 179, -2)]
    [InlineData(190, -170, 0)]
    [InlineData(-170, 170, -20)]
    [InlineData(0, 180, 180)]
    public void TheTurn_IsTheShortestOne_AcrossNorthAndSouth(double start, double now, double turn) =>
        Assert.Equal(turn, PatternTurn.Snapped(start, now, 0.1), 9);

    [Theory]
    [InlineData(23)]
    [InlineData(-61.5)]
    [InlineData(170)]
    public void ThePreview_IsWhatTheTurnDoes(double delta)
    {
        var p = BlastProject.CreateNew();
        var holes = HoleDesign.FillPolygon(Field, new PatternParameters { Burden = 4, Spacing = 4.5, Pattern = PatternType.Staggered, StartNumber = 1 },
            new HoleParameters { BenchHeight = 10, Subdrill = 1 }, out var rows, out var pat);
        var layer = p.GetOrCreateLayer(StandardLayers.Holes);
        foreach (var h in holes) p.Add(h, layer);
        p.Rows.AddRange(rows);
        p.Patterns.Add(pat);
        var azimuth = pat.Azimuth;

        var plan = PatternTurn.Plan(p, pat, delta);
        Assert.Equal(azimuth, pat.Azimuth);
        Assert.All(holes, h => Assert.Same(h, p.FindEntity(h.Id)));
        var moves = plan.Moves.Select(m => (m.Hole.Id, m.Collar)).ToList();
        var deletes = plan.Deletes.Select(h => h.Id).ToList();
        var adds = plan.Adds.Select(n => n.Point).ToList();
        Assert.True(moves.Count > 0 && deletes.Count + adds.Count > 0, "the turn should move holes and change the outline's share");

        var report = HoleDesign.RotatePattern(p, pat, delta);
        Assert.Null(report.Refusal);
        Assert.Equal(GeoMath.NormalizeBearing(azimuth + delta), pat.Azimuth, 9);
        foreach (var (id, collar) in moves) Assert.True(Vec3.Distance2D(((Hole)p.FindEntity(id)!).Collar, collar) < 1e-9);
        Assert.All(deletes, id => Assert.Null(p.FindEntity(id)));
        Assert.Equal(deletes.Count, report.Deleted);
        Assert.Equal(adds.Count, report.AddedIds.Count);
        var added = report.AddedIds.Select(id => ((Hole)p.FindEntity(id)!).Collar).ToList();
        Assert.All(adds, a => Assert.Contains(added, c => Vec3.Distance2D(a, c) < 1e-9));
    }
}
