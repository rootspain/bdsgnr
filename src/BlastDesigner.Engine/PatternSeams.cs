using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>What is wrong at a seam between two segments.</summary>
public enum SeamIssueKind
{
    /// <summary>Two collars of different segments too close to each other.</summary>
    Close,
    /// <summary>No hole of the next segment near enough across the seam.</summary>
    Gap,
    /// <summary>Two inclined holes of different segments come too close under the ground.</summary>
    Crossing
}

/// <summary>A pair of holes at a seam (for a gap, the hole and the nearest one across, or the point across where there is none).</summary>
public sealed record SeamIssue(SeamIssueKind Kind, Guid A, Guid? B, Vec3 From, Vec3 To, double Distance);

/// <summary>
/// The seams between the segments of a pattern: where two grids meet, the distances are not regular. The check finds the
/// collars too close (under 0.6 of the smaller spacing, or under 2 m), the gaps across a seam (over 1.5 of the larger
/// burden) and inclined holes that come within a metre of each other under the ground. The rule of the pattern
/// (<see cref="HolePattern.SeamRule"/>) only warns, takes the hole of the coarser grid away, or makes one hole of a pair in
/// the middle; a gap can get a hole of its own in the middle (<see cref="HolePattern.SeamFillGaps"/>).
/// </summary>
public static class PatternSeams
{
    public const double CloseShare = 0.6;
    public const double CloseMin = 2;
    public const double GapShare = 1.5;
    public const double CrossingMin = 1;

    /// <summary>The issues at the seams of a pattern cut into segments; none for a pattern that is not.</summary>
    public static List<SeamIssue> Check(BlastProject p, HolePattern root)
    {
        var issues = new List<SeamIssue>();
        if (!root.IsContainer) return issues;
        var leaves = PatternTree.Leaves(p, root).ToDictionary(l => l.Id);
        var holes = PatternTree.TreeHoles(p, root).Where(h => h.PatternId is Guid id && leaves.ContainsKey(id)).ToList();
        if (holes.Count == 0) return issues;
        var cell = Math.Max(1, leaves.Values.Max(l => Math.Max(l.Burden, l.Spacing)));
        var index = new CellIndex(cell, holes);
        HolePattern Of(Hole h) => leaves[h.PatternId!.Value];

        // collars too close: each pair once, from the hole with the smaller id
        foreach (var h in holes)
            foreach (var o in index.Near(h.Collar, cell))
            {
                if (o.PatternId == h.PatternId || h.Id.CompareTo(o.Id) >= 0) continue;
                var d = Vec3.Distance2D(h.Collar, o.Collar);
                var limit = Math.Max(CloseShare * Math.Min(Of(h).Spacing, Of(o).Spacing), CloseMin);
                if (d < limit) issues.Add(new SeamIssue(SeamIssueKind.Close, h.Id, o.Id, h.Collar, o.Collar, d));
            }

        // gaps across the seam between segments k and k + 1, from the holes of either side near the cut line
        var segments = PatternTree.Children(p, root);
        for (int k = 0; k + 1 < segments.Count; k++)
        {
            var (a, b) = (segments[k], segments[k + 1]);
            if (a.IsContainer || b.IsContainer || k >= root.SegmentLines.Count) continue;
            var cut = a.Cuts.LastOrDefault();
            if (cut == null) continue;
            var limit = GapShare * Math.Max(a.Burden, b.Burden);
            foreach (var (from, to) in new[] { (a, b), (b, a) })
            {
                var band = Math.Max(from.Burden, from.Spacing);
                foreach (var h in holes.Where(h => h.PatternId == from.Id))
                {
                    var across = (h.Collar.X - cut.X) * cut.NX + (h.Collar.Y - cut.Y) * cut.NY;
                    if (Math.Abs(across) > band) continue;
                    var nearest = index.Near(h.Collar, limit).Where(o => o.PatternId == to.Id).MinBy(o => Vec3.Distance2D(o.Collar, h.Collar));
                    if (nearest != null && Vec3.Distance2D(nearest.Collar, h.Collar) <= limit) continue;
                    // across the line by the burden of the other side, where its hole would be
                    var side = Math.Sign(across) == 0 ? 1 : -Math.Sign(across);
                    var dist = Math.Abs(across) + to.Burden / 2;
                    var point = nearest?.Collar ?? new Vec3(h.Collar.X + cut.NX * side * dist, h.Collar.Y + cut.NY * side * dist, h.Collar.Z);
                    if (issues.Any(x => x.Kind == SeamIssueKind.Gap && x.B == h.Id && x.A == nearest?.Id)) continue;
                    issues.Add(new SeamIssue(SeamIssueKind.Gap, h.Id, nearest?.Id, h.Collar, point, nearest != null ? Vec3.Distance2D(nearest.Collar, h.Collar) : double.PositiveInfinity));
                }
            }
        }

        // inclined holes that come within a metre of each other under the ground
        var inclined = holes.Where(h => h.Angle >= 0.1).ToList();
        if (inclined.Count > 0)
        {
            var reach = CrossingMin + 2 * holes.Max(h => h.EffectiveLength * Math.Sin(h.Angle * Math.PI / 180));
            foreach (var h in inclined)
                foreach (var o in index.Near(h.Collar, reach))
                {
                    if (o.PatternId == h.PatternId || (o.Angle >= 0.1 && h.Id.CompareTo(o.Id) >= 0)) continue;
                    var d = SegmentDistance(h.Collar, h.Toe, o.Collar, o.Toe);
                    if (d < CrossingMin && Vec3.Distance2D(h.Collar, o.Collar) >= CrossingMin) issues.Add(new SeamIssue(SeamIssueKind.Crossing, h.Id, o.Id, h.Collar, o.Collar, d));
                }
        }
        return issues;
    }

    /// <summary>The seam rule taken back before a layout: the holes it added go, the ones it moved go back to their nodes, the nodes it emptied are free.</summary>
    internal static List<Guid> Reset(BlastProject p, HolePattern root, IEnumerable<HolePattern> leaves)
    {
        var gone = root.SeamAdded.Where(id => p.FindEntity(id) is Hole).ToList();
        root.SeamAdded.Clear();
        foreach (var leaf in leaves)
        {
            foreach (var s in leaf.SeamOffsets)
                foreach (var h in HoleDesign.PatternHoles(p, leaf))
                    if (h.GridI == s.I && h.GridJ == s.J && h.GridOffset is Vec3 off && Math.Abs(off.X - s.X) < 1e-9 && Math.Abs(off.Y - s.Y) < 1e-9) h.GridOffset = null;
            leaf.SeamOffsets.Clear();
            leaf.SeamDropped.Clear();
        }
        return gone;
    }

    /// <summary>
    /// Applies the seam rule of a pattern after its segments were laid out: gives the segments whose holes it takes away or
    /// moves, to be laid out again, and adds the holes in the gaps.
    /// </summary>
    internal static HashSet<HolePattern> Apply(BlastProject p, HolePattern root, PatternRebuildOptions o)
    {
        var again = new HashSet<HolePattern>();
        if (!root.IsContainer || (root.SeamRule == SeamRule.Warn && !root.SeamFillGaps)) return again;
        var issues = Check(p, root);
        var done = new HashSet<Guid>();
        if (root.SeamRule != SeamRule.Warn)
            foreach (var issue in issues.Where(x => x.Kind == SeamIssueKind.Close))
            {
                if (done.Contains(issue.A) || issue.B is not Guid bid || done.Contains(bid)) continue;
                if (p.FindEntity(issue.A) is not Hole ha || p.FindEntity(bid) is not Hole hb) continue;
                if (p.FindPattern(ha.PatternId) is not { } pa || p.FindPattern(hb.PatternId) is not { } pb) continue;
                // the finer grid wins (the rows next to a face usually); with the same, the segment nearer the face
                var aWins = pa.Burden * pa.Spacing < pb.Burden * pb.Spacing - 1e-9 || (Math.Abs(pa.Burden * pa.Spacing - pb.Burden * pb.Spacing) < 1e-9 && pa.Order <= pb.Order);
                var (win, winPat, lose, losePat) = aWins ? (ha, pa, hb, pb) : (hb, pb, ha, pa);
                if (lose.GridI is not int li || lose.GridJ is not int lj) continue;
                losePat.SeamDropped.Add(new GridIndex { I = li, J = lj });
                again.Add(losePat);
                if (root.SeamRule == SeamRule.Merge && win.GridOffset == null && win.GridI is int wi && win.GridJ is int wj)
                {
                    var mid = new Vec3((win.Collar.X + lose.Collar.X) / 2, (win.Collar.Y + lose.Collar.Y) / 2, 0);
                    var (u, v) = HoleDesign.PatternAxes(winPat.Azimuth);
                    var d = mid - HoleDesign.NodePoint(winPat, wi, wj);
                    var off = new Vec3(d.X * u.X + d.Y * u.Y, d.X * v.X + d.Y * v.Y, 0);
                    win.GridOffset = off;
                    winPat.SeamOffsets.Add(new SeamOffset { I = wi, J = wj, X = off.X, Y = off.Y });
                    again.Add(winPat);
                }
                done.Add(win.Id);
                done.Add(lose.Id);
            }
        if (root.SeamFillGaps)
        {
            var layer = o.Layer ?? p.GetOrCreateLayer(StandardLayers.Holes, 0xFF1E3A8A, "Design");
            var added = new List<Vec3>();
            foreach (var issue in issues.Where(x => x.Kind == SeamIssueKind.Gap))
            {
                if (p.FindEntity(issue.A) is not Hole h || p.FindPattern(h.PatternId) is not { } pat) continue;
                var at = new Vec3((issue.From.X + issue.To.X) / 2, (issue.From.Y + issue.To.Y) / 2, h.Collar.Z);
                if (added.Any(x => Vec3.Distance2D(x, at) < pat.Spacing / 2)) continue;
                added.Add(at);
                var d = pat.HoleDefaults;
                var hole = HoleDesign.CreateHole(at, new HoleParameters
                {
                    Diameter = d.Diameter, Angle = d.Angle, Bearing = d.Bearing, Subdrill = d.Subdrill, BenchHeight = d.BenchHeight, HoleType = d.HoleType, Dummy = d.Dummy,
                    CollarSurface = o.CollarSurface, GradeSurface = o.GradeSurface
                });
                // a hole of the segment without a node: laying it out again leaves it, the next layout takes it away
                hole.PatternId = pat.Id;
                if (h.ToLevel is double z && HoleLevel.Reaches(hole, z)) { hole.ToLevel = z; hole.Length = HoleLevel.Length(hole) ?? hole.Length; }
                p.Add(hole, layer);
                root.SeamAdded.Add(hole.Id);
            }
        }
        return again;
    }

    /// <summary>Closest distance between two segments in space.</summary>
    public static double SegmentDistance(Vec3 p1, Vec3 q1, Vec3 p2, Vec3 q2)
    {
        Vec3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        double a = Vec3.Dot(d1, d1), e = Vec3.Dot(d2, d2), f = Vec3.Dot(d2, r);
        double s, t;
        if (a <= 1e-12 && e <= 1e-12) return (p1 - p2).Length;
        if (a <= 1e-12) { s = 0; t = Math.Clamp(f / e, 0, 1); }
        else
        {
            var c = Vec3.Dot(d1, r);
            if (e <= 1e-12) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
            else
            {
                var b = Vec3.Dot(d1, d2);
                var den = a * e - b * b;
                s = den > 1e-12 ? Math.Clamp((b * f - c * e) / den, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        return (p1 + d1 * s - (p2 + d2 * t)).Length;
    }

    /// <summary>The holes in square cells of a plan, to find the near ones without looking at every pair.</summary>
    private sealed class CellIndex
    {
        private readonly double _cell;
        private readonly Dictionary<(long, long), List<Hole>> _cells = new();

        public CellIndex(double cell, IEnumerable<Hole> holes)
        {
            _cell = cell;
            foreach (var h in holes)
            {
                var key = Key(h.Collar.X, h.Collar.Y);
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<Hole>();
                list.Add(h);
            }
        }

        private (long, long) Key(double x, double y) => ((long)Math.Floor(x / _cell), (long)Math.Floor(y / _cell));

        /// <summary>The holes whose cell is within <paramref name="radius"/> of a point (a few more than that radius).</summary>
        public IEnumerable<Hole> Near(Vec3 at, double radius)
        {
            var (cx, cy) = Key(at.X, at.Y);
            var n = (long)Math.Ceiling(radius / _cell);
            for (var x = cx - n; x <= cx + n; x++)
                for (var y = cy - n; y <= cy + n; y++)
                    if (_cells.TryGetValue((x, y), out var list))
                        foreach (var h in list) yield return h;
        }
    }
}
