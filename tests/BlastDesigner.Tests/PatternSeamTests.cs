using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.Tests;

/// <summary>Gaps at the seams of a pattern cut into segments: only the holes that face the cut line, marked where the other segment's hole should be.</summary>
public class PatternSeamTests
{
    private static readonly Vec3[] Rectangle = { new(0, 0, 100), new(41, 0, 100), new(41, 23, 100), new(0, 23, 100) };

    private static (BlastProject P, HolePattern Root, List<HolePattern> Seg) Cut()
    {
        var p = BlastProject.CreateNew();
        var holes = HoleDesign.FillPolygon(Rectangle, new PatternParameters { Burden = 4, Spacing = 4.5, Pattern = PatternType.Rectangular, StartNumber = 1 },
            new HoleParameters { BenchHeight = 10, Subdrill = 1 }, out var rows, out var root);
        var layer = p.GetOrCreateLayer(StandardLayers.Holes);
        foreach (var h in holes) p.Add(h, layer);
        p.Rows.AddRange(rows);
        p.Patterns.Add(root);
        PatternTree.Split(p, root, 2);
        return (p, root, PatternTree.Children(p, root));
    }

    private static double Across(HolePattern seg, Vec3 q)
    {
        var c = seg.Cuts.Last();
        return (q.X - c.X) * c.NX + (q.Y - c.Y) * c.NY;
    }

    [Fact]
    public void AGap_IsMarkedFromTheHoleFacingTheSeam_ToWhereTheOtherSegmentsHoleShouldBe()
    {
        var (p, root, seg) = Cut();
        // the first row of the back segment emptied by hand: a gap all along the seam
        var first = HoleDesign.PatternHoles(p, seg[1]).Min(h => h.GridJ!.Value);
        foreach (var h in HoleDesign.PatternHoles(p, seg[1]).Where(h => h.GridJ == first)) seg[1].Suppressed.Add(new GridIndex { I = h.GridI!.Value, J = first });
        PatternTree.Relayout(p, root);
        var gaps = PatternSeams.Check(p, root).Where(x => x.Kind == SeamIssueKind.Gap).ToList();
        Assert.NotEmpty(gaps);
        var front = HoleDesign.PatternHoles(p, seg[0]).ToDictionary(h => h.Id);
        foreach (var g in gaps.Where(g => front.ContainsKey(g.A)))
        {
            var h = front[g.A];
            // from the last row of the front segment, the one nearest the line
            var nearest = HoleDesign.PatternHoles(p, seg[0]).Min(x => Math.Abs(Across(seg[0], x.Collar)));
            Assert.Equal(nearest, Math.Abs(Across(seg[0], h.Collar)), 6);
            // to half the burden of the back segment behind the line, straight across from the hole
            Assert.Equal(-seg[1].Burden / 2, Across(seg[0], g.To), 6);
            Assert.True(Vec3.Distance2D(g.From, g.To) < 2 * root.Burden);
        }
    }

    [Fact]
    public void AHoleBehindAnotherOfItsSegment_HasNoGap()
    {
        var (p, root, seg) = Cut();
        // a tilted cut line: rows of the front segment come to it at different distances
        var line = root.SegmentLines[0];
        root.SegmentLines[0] = new SegmentLine { U1 = line.U1, V1 = line.V1 - 3, U2 = line.U2, V2 = line.V2 + 3 };
        PatternTree.Relayout(p, root);
        var front = HoleDesign.PatternHoles(p, seg[0]).ToList();
        foreach (var g in PatternSeams.Check(p, root).Where(x => x.Kind == SeamIssueKind.Gap && front.Any(h => h.Id == x.A)))
        {
            var h = front.First(x => x.Id == g.A);
            var across = Across(seg[0], h.Collar);
            var foot = new Vec3(h.Collar.X - seg[0].Cuts.Last().NX * across, h.Collar.Y - seg[0].Cuts.Last().NY * across, 0);
            // no hole of its own segment stands between it and the line
            Assert.DoesNotContain(front, q => q.Id != h.Id && Math.Abs(Across(seg[0], q.Collar)) < Math.Abs(across) - 1e-6
                && Vec3.Distance2D(GeoMath.ClosestOnSegment2D(h.Collar, foot, q.Collar).Point, q.Collar) < seg[0].Spacing / 2);
        }
    }
}
