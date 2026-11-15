using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using Clipper2Lib;

namespace BlastDesigner.Engine;

public static partial class PatternTree
{
    /// <summary>
    /// How many segments a pattern can be cut into: its number of rows (a segment is not narrower than one row); 1 for a
    /// pattern without nodes.
    /// </summary>
    public static int MaxSegments(HolePattern root) => Math.Max(1, GridRows(root).Count);

    /// <summary>The grid rows (j) of the nodes of a pattern inside its outline, from the face back.</summary>
    private static List<int> GridRows(HolePattern pat)
    {
        if (pat.Outline.Count < 3 || pat.Burden <= 0 || pat.Spacing <= 0) return new List<int>();
        // the grid of the pattern as a whole, not of a segment
        var whole = new HolePattern
        {
            Outline = pat.Outline, Center = pat.Center, Azimuth = pat.Azimuth, PhaseU = pat.PhaseU, PhaseV = pat.PhaseV, Burden = pat.Burden, Spacing = pat.Spacing,
            Type = pat.Type, EdgeOffset = pat.EdgeOffset
        };
        return HoleDesign.PatternNodes(whole).Select(n => n.J).Distinct().OrderBy(j => j).ToList();
    }

    /// <summary>The segments a pattern is cut into: its number of cut lines plus one.</summary>
    public static int SegmentCount(HolePattern root) => root.SegmentLines.Count + 1;

    /// <summary>
    /// Cuts a pattern into <paramref name="count"/> segments (1 takes the segments away). From one: lines across the blast
    /// (along the rows) in the middle between rows, the rows shared as evenly as can be, the extra ones towards the face.
    /// Up: a new line halves (by rows) the biggest segment, both halves keep what it set. Down: the last line goes, the last
    /// segment joins the one before it and takes its parameters. The holes stay where they are while the segments set
    /// nothing of their own; then lays the tree out again (<see cref="Relayout"/>).
    /// </summary>
    public static PatternRebuildReport Split(BlastProject p, HolePattern root, int count, PatternRebuildOptions? o = null)
    {
        o ??= PatternRebuildOptions.For(p);
        if (TreeRefusal(p, root, o) is { } no) return new PatternRebuildReport { Refusal = no };
        count = Math.Clamp(count, 1, MaxSegments(root));
        if (count == SegmentCount(root)) return new PatternRebuildReport();
        var deleted = new List<Guid>();
        var rows = TreeRowIds(p, root);
        if (!root.IsContainer && count > 1) CutEvenly(p, root, count);
        else
        {
            while (root.IsContainer && SegmentCount(root) < count) HalveBiggest(p, root);
            while (root.IsContainer && SegmentCount(root) > count) JoinLast(p, root, deleted);
        }
        var report = Relayout(p, root, o, rows);
        report.DeletedIds.AddRange(deleted);
        report.Deleted += deleted.Count;
        return report;
    }

    /// <summary>The first cut: <paramref name="count"/> − 1 lines along the rows, the holes of the pattern given to its segments.</summary>
    private static void CutEvenly(BlastProject p, HolePattern root, int count)
    {
        var rows = GridRows(root);
        int each = rows.Count / count, extra = rows.Count % count, at = 0;
        for (int k = 0; k < count - 1; k++)
        {
            at += each + (k < extra ? 1 : 0);
            root.SegmentLines.Add(Between(root, rows[at - 1]));
        }
        for (int k = 0; k < count; k++) p.Patterns.Add(NewSegment(root, k, new PatternOverrides()));
        // the polygon stays with the container under another name: an older version does not lay the whole of it out again
        if (root.OutlineEntityId != null) { root.SourceOutlineId = root.OutlineEntityId; root.OutlineEntityId = null; }
    }

    /// <summary>A line along the rows in the middle between grid row <paramref name="row"/> of a pattern and the next.</summary>
    private static SegmentLine Between(HolePattern root, int row)
    {
        var r = Reach(root);
        var v = root.PhaseV + (row + 0.5) * root.Burden;
        return new SegmentLine { U1 = -r, V1 = v, U2 = r, V2 = v, Snapped = true, Row = row };
    }

    /// <summary>How far the outline of a pattern reaches from its centre.</summary>
    private static double Reach(HolePattern pat) => pat.Outline.Count == 0 ? 1 : pat.Outline.Max(q => Vec3.Distance2D(q, pat.Center)) + 1;

    /// <param name="like">The pattern whose values the new segment starts with (so the holes it takes over keep theirs).</param>
    private static HolePattern NewSegment(HolePattern root, int order, PatternOverrides overrides, HolePattern? like = null) => new()
    {
        ParentId = root.Id,
        Order = order,
        Name = SegmentName(order),
        HoleDefaults = CopyDefaults((like ?? root).HoleDefaults),
        Overrides = overrides,
        Center = root.Center,
        Azimuth = root.Azimuth,
        InitialAzimuth = root.InitialAzimuth,
        PhaseU = root.PhaseU,
        PhaseV = root.PhaseV,
        Burden = root.Burden,
        Spacing = root.Spacing,
        Type = root.Type,
        Suppressed = root.Suppressed.Select(s => new GridIndex { I = s.I, J = s.J }).ToList()
    };

    private static PatternHoleDefaults CopyDefaults(PatternHoleDefaults d) => new()
    {
        Diameter = d.Diameter, Angle = d.Angle, Bearing = d.Bearing, Subdrill = d.Subdrill, BenchHeight = d.BenchHeight, HoleType = d.HoleType, Dummy = d.Dummy,
        Depth = d.Depth.Clone()
    };

    /// <summary>A, B, C… then 27, 28…</summary>
    public static string SegmentName(int order) => order < 26 ? ((char)('A' + order)).ToString() : (order + 1).ToString();

    /// <summary>A new line in the middle (by rows) of the biggest segment; the new segment behind it sets what that one set.</summary>
    private static void HalveBiggest(BlastProject p, HolePattern root)
    {
        var segments = Children(p, root);
        var best = segments.Select(s => (Seg: s, Rows: SegmentRows(p, s))).Where(x => x.Rows.Count >= 2).OrderByDescending(x => x.Rows.Count).ThenBy(x => x.Seg.Order).FirstOrDefault();
        if (best.Seg == null) { root.SegmentLines.Add(Between(root, GridRows(root).LastOrDefault())); p.Patterns.Add(NewSegment(root, segments.Count, new PatternOverrides())); return; }
        var (seg, rows) = best;
        var front = (rows.Count + 1) / 2;
        SegmentLine line;
        if (SameGrid(seg, root))
            line = Between(root, rows[front - 1]);
        else
        {
            // a grid of its own: the middle between its rows, in the axes of the parent
            var v = (RowV(seg, rows[front - 1]) + RowV(seg, rows[front])) / 2;
            var r = Reach(root);
            line = new SegmentLine { U1 = -r, V1 = v, U2 = r, V2 = v };
        }
        root.SegmentLines.Insert(seg.Order, line);
        foreach (var s in segments.Where(s => s.Order > seg.Order)) s.Order++;
        var behind = NewSegment(root, seg.Order + 1, seg.Overrides.Clone(), seg);
        behind.AnchorSide = seg.AnchorSide;
        p.Patterns.Add(behind);
        Rename(p, root);
    }

    /// <summary>Takes the last line away: the last segment joins the one before it (or the parent, when it was the only line).</summary>
    private static void JoinLast(BlastProject p, HolePattern root, List<Guid> deleted)
    {
        var segments = Children(p, root);
        var last = segments[^1];
        root.SegmentLines.RemoveAt(root.SegmentLines.Count - 1);
        if (root.SegmentLines.Count == 0)
        {
            // one segment left: the parent holds the holes again, with its own parameters
            foreach (var s in segments) Absorb(p, s, root, deleted);
            if (root.SourceOutlineId != null) { root.OutlineEntityId = root.SourceOutlineId; root.SourceOutlineId = null; }
            return;
        }
        Absorb(p, last, segments[^2], deleted);
    }

    /// <summary>Gives the holes of a segment to another pattern (keeping them where that grid has a node, else deleting them) and takes the segment away.</summary>
    private static void Absorb(BlastProject p, HolePattern from, HolePattern into, List<Guid> deleted)
    {
        var gone = new List<Guid>();
        foreach (var h in HoleDesign.PatternHoles(p, from).ToList())
        {
            if (NodeAt(into, NodeLocation(from, h)) is var (i, j))
            {
                h.PatternId = into.Id;
                h.GridI = i;
                h.GridJ = j;
            }
            else gone.Add(h.Id);
        }
        // nodes emptied by hand stay empty in the grid that takes them over, when it is the same grid
        if (SameGrid(from, into))
            foreach (var s in from.Suppressed)
                if (!into.IsSuppressed(s.I, s.J)) into.Suppressed.Add(new GridIndex { I = s.I, J = s.J });
        DeleteHoles(p, gone);
        deleted.AddRange(gone);
        // its rows go when they are empty, after the holes were laid out again (Relayout)
        p.Patterns.Remove(from);
    }

    /// <summary>The names of the segments of a pattern by their order (those not renamed by hand).</summary>
    private static void Rename(BlastProject p, HolePattern root)
    {
        var segments = Children(p, root);
        var auto = Enumerable.Range(0, segments.Count + 1).Select(SegmentName).ToHashSet();
        foreach (var s in segments)
            if (auto.Contains(s.Name)) s.Name = SegmentName(s.Order);
    }

    /// <summary>The grid rows a segment has holes or nodes in, from the face back.</summary>
    private static List<int> SegmentRows(BlastProject p, HolePattern seg) =>
        HoleDesign.PatternNodes(seg).Select(n => n.J).Distinct().OrderBy(j => j).ToList();

    private static double RowV(HolePattern pat, int j) => pat.PhaseV + j * pat.Burden;

    /// <summary>Whether two patterns have the same grid: their nodes are at the same points.</summary>
    public static bool SameGrid(HolePattern a, HolePattern b) =>
        Vec3.Distance2D(a.Center, b.Center) < 1e-9 && Math.Abs(a.Azimuth - b.Azimuth) < 1e-9 && Math.Abs(a.PhaseU - b.PhaseU) < 1e-9 && Math.Abs(a.PhaseV - b.PhaseV) < 1e-9
        && Math.Abs(a.Burden - b.Burden) < 1e-9 && Math.Abs(a.Spacing - b.Spacing) < 1e-9 && a.Type == b.Type;

    /// <summary>Why the tree of a pattern cannot be laid out again (a hole of any of its patterns); null when it can.</summary>
    public static PatternRefusal? TreeRefusal(BlastProject p, HolePattern root, PatternRebuildOptions? o = null)
    {
        foreach (var pat in new[] { root }.Concat(Descendants(p, root)))
            if (HoleDesign.RebuildRefusal(p, pat, o) is { } no) return no;
        return null;
    }

    /// <summary>
    /// Lays a pattern and its segments out again in one go: takes the values down to the segments, cuts the outline of the
    /// parent into their parts, places the grid of each segment (its own where it sets one, else that of the parent), gives
    /// each hole to the segment it now lies in (keeping its id where that grid has a node there), lays out every segment and
    /// numbers the holes through the whole tree. A pattern that is not cut is laid out as before.
    /// </summary>
    public static PatternRebuildReport Relayout(BlastProject p, HolePattern root, PatternRebuildOptions? o = null) => Relayout(p, root, o, null);

    /// <param name="former">Rows of segments taken away before (by <see cref="Split"/>): they go when empty, their place kept.</param>
    private static PatternRebuildReport Relayout(BlastProject p, HolePattern root, PatternRebuildOptions? o, HashSet<Guid>? former)
    {
        o ??= PatternRebuildOptions.For(p);
        if (!root.IsContainer && former == null && Children(p, root).Count == 0) return HoleDesign.RebuildPattern(p, root, o);
        if (TreeRefusal(p, root, o) is { } no) return new PatternRebuildReport { Refusal = no };
        var report = new PatternRebuildReport();
        var treeRows = TreeRowIds(p, root);
        if (former != null) treeRows.UnionWith(former);
        var rowsAt = p.Rows.FindIndex(r => treeRows.Contains(r.Id));

        var was = Descendants(p, root).ToDictionary(s => s.Id, s => Defaults.Of(s.HoleDefaults));
        Resolve(p, root);
        Cut(p, root);
        foreach (var s in Children(p, root)) Place(root, s);

        // each hole to the segment it lies in
        var leaves = Leaves(p, root);
        var gone = new List<Guid>();
        foreach (var h in TreeHoles(p, root).ToList())
        {
            if (p.FindPattern(h.PatternId) is not { } from || h.GridI == null || h.GridJ == null) continue;
            var at = NodeLocation(from, h);
            var target = leaves.FirstOrDefault(l => InRegion(l, at));
            if (target == null || target == from) continue;
            if (NodeAt(target, at) is var (i, j))
            {
                // what the two segments make their holes with differently, the hole takes from its new one
                Defaults.Of(target.HoleDefaults).ApplyChanged(h, Defaults.Of(from.HoleDefaults));
                h.PatternId = target.Id;
                h.GridI = i;
                h.GridJ = j;
            }
            else gone.Add(h.Id);
        }
        DeleteHoles(p, gone);
        report.DeletedIds.AddRange(gone);
        // a value a segment now makes its holes with differently goes to the holes it has (they can all be laid out again)
        foreach (var leaf in leaves)
            if (was.TryGetValue(leaf.Id, out var before))
                foreach (var h in HoleDesign.PatternHoles(p, leaf)) Defaults.Of(leaf.HoleDefaults).ApplyChanged(h, before);
        report.Deleted += gone.Count;
        if (root.IsContainer) root.RowIds.Clear();

        var added = new HashSet<Guid>();
        foreach (var leaf in leaves)
        {
            var r = HoleDesign.RebuildPattern(p, leaf, o);
            if (r.Refusal != null) { report.Refusal = r.Refusal; return report; }
            report.Moved += r.Moved;
            report.Added += r.Added;
            report.Deleted += r.Deleted;
            report.TiesLost += r.TiesLost;
            report.AddedIds.AddRange(r.AddedIds);
            report.DeletedIds.AddRange(r.DeletedIds);
            foreach (var id in r.AddedIds) added.Add(id);
        }
        // rows the holes left (of the parent before the cut, of segments joined to others) go
        var kept = leaves.SelectMany(l => l.RowIds.Values).ToHashSet();
        p.Rows.RemoveAll(r => treeRows.Contains(r.Id) && !kept.Contains(r.Id) && r.HoleIds.Count == 0);
        if (root.IsContainer) Renumber(p, root, rowsAt, added);
        else MoveRows(p, root.RowIds.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList(), rowsAt);
        root.Revision++;
        return report;
    }

    /// <summary>The values of <see cref="PatternHoleDefaults"/> that holes standing in a segment follow.</summary>
    private readonly record struct Defaults(double Diameter, double Angle, double Bearing, double Subdrill, int HoleType)
    {
        public static Defaults Of(PatternHoleDefaults d) => new(d.Diameter, d.Angle, d.Bearing, d.Subdrill, d.HoleType);

        /// <summary>Gives a hole the values that differ from <paramref name="before"/>; the others it keeps as they are.</summary>
        public void ApplyChanged(Hole h, Defaults before)
        {
            if (Diameter != before.Diameter) h.Diameter = Diameter;
            if (Angle != before.Angle) h.Angle = Angle;
            if (Bearing != before.Bearing && h.Angle >= 0.1) h.Bearing = Bearing;
            if (Subdrill != before.Subdrill) h.Subdrill = Subdrill;
            if (HoleType != before.HoleType) h.HoleType = HoleType;
        }
    }

    /// <summary>The ids of the rows of a pattern and of its segments.</summary>
    private static HashSet<Guid> TreeRowIds(BlastProject p, HolePattern root)
    {
        var ids = new HashSet<Guid>(root.RowIds.Values);
        foreach (var s in Descendants(p, root)) ids.UnionWith(s.RowIds.Values);
        return ids;
    }

    /// <summary>
    /// Numbers the holes through the tree: the rows of segment A from the face back, then those of segment B and so on, one
    /// block in the list of rows where the rows of the pattern were (Row 1, Row 2…); the holes by these rows, along each row,
    /// with the prefix and the first number of the parent.
    /// </summary>
    public static void Renumber(BlastProject p, HolePattern root, int rowsAt = -1, HashSet<Guid>? added = null)
    {
        var rows = new List<HoleRow>();
        foreach (var leaf in Leaves(p, root).OrderBy(OrderKey(p, root)))
        {
            // the rows of a segment from the face back: along its grid, as RebuildRows made them
            var mine = leaf.RowIds.OrderBy(kv => kv.Key).Select(kv => p.Rows.FirstOrDefault(r => r.Id == kv.Value)).OfType<HoleRow>();
            rows.AddRange(mine);
        }
        MoveRows(p, rows.Select(r => r.Id).ToList(), rowsAt);
        var ordered = rows.SelectMany(r => r.HoleIds).Select(p.FindEntity).OfType<Hole>().ToList();
        HoleDesign.Number(p, root, ordered, added ?? new HashSet<Guid>());
    }

    /// <summary>Puts rows as one block at <paramref name="at"/> in the list of rows (where they are when −1), named by their place.</summary>
    private static void MoveRows(BlastProject p, List<Guid> order, int at)
    {
        var byId = p.Rows.Where(r => order.Contains(r.Id)).ToDictionary(r => r.Id);
        var rows = order.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (at < 0) at = p.Rows.FindIndex(r => byId.ContainsKey(r.Id));
        p.Rows.RemoveAll(r => byId.ContainsKey(r.Id));
        if (at < 0 || at > p.Rows.Count) at = p.Rows.Count;
        p.Rows.InsertRange(at, rows);
        for (int k = 0; k < rows.Count; k++) rows[k].Name = $"Row {at + k + 1}";
    }

    /// <summary>The place of a leaf in the tree: the orders of its segments from the top down.</summary>
    private static Func<HolePattern, string> OrderKey(BlastProject p, HolePattern root) => leaf =>
    {
        var parts = new List<int>();
        for (var x = leaf; x != root && x.ParentId != null; x = Parent(p, x) ?? root) parts.Insert(0, x.Order);
        return string.Join(".", parts.Select(k => k.ToString("D4")));
    };

    /// <summary>
    /// The part of each segment: the strip of the outline of the parent between its cut lines (in the axes of the parent's
    /// grid), as the kept sides of the lines for its nodes and as the pieces of the outline for drawing.
    /// </summary>
    private static void Cut(BlastProject p, HolePattern root)
    {
        var segments = Children(p, root);
        var (u, v) = HoleDesign.PatternAxes(root.Azimuth);
        Vec3 World(double a, double b) => new(root.Center.X + u.X * a + v.X * b, root.Center.Y + u.Y * a + v.Y * b, root.Center.Z);
        // a snapped line stays in the middle between its rows when the burden or the first row of the parent changes
        foreach (var line in root.SegmentLines.Where(l => l.Snapped))
            line.V1 = line.V2 = root.PhaseV + (line.Row + 0.5) * root.Burden;
        var planes = new List<(CutPlane Back, CutPlane Front)>();
        foreach (var line in root.SegmentLines)
        {
            var (a1, b1, a2, b2) = line.U1 <= line.U2 ? (line.U1, line.V1, line.U2, line.V2) : (line.U2, line.V2, line.U1, line.V1);
            double du = a2 - a1, dv = b2 - b1, len = Math.Sqrt(du * du + dv * dv);
            if (len < 1e-9) (du, dv, len) = (1, 0, 1);
            // the normal towards the back (growing v) in the axes of the grid, then in plan
            double gu = -dv / len, gv = du / len;
            var n = new Vec3(u.X * gu + v.X * gv, u.Y * gu + v.Y * gv, 0);
            var at = World(a1, b1);
            planes.Add((new CutPlane { X = at.X, Y = at.Y, NX = n.X, NY = n.Y }, new CutPlane { X = at.X, Y = at.Y, NX = -n.X, NY = -n.Y, Strict = true }));
        }
        var edge = root.Outline.Select(q => q.WithZ(root.Center.Z)).ToList();
        foreach (var s in segments)
        {
            var k = Math.Clamp(s.Order, 0, root.SegmentLines.Count);
            s.Cuts = new List<CutPlane>();
            if (k > 0) s.Cuts.Add(planes[k - 1].Back);
            if (k < planes.Count) s.Cuts.Add(planes[k].Front);
            s.EdgeOutline = edge;
            var pieces = Clip(edge, s.Cuts, root.Center);
            s.Outline = pieces.Count > 0 ? pieces[0] : new List<Vec3>();
            s.OutlineParts = pieces.Skip(1).ToList();
            s.OutlineEntityId = null;
        }
    }

    /// <summary>The pieces of a polygon on the kept side of every plane, the biggest first (Clipper2, about a local origin).</summary>
    private static List<List<Vec3>> Clip(List<Vec3> polygon, List<CutPlane> planes, Vec3 origin)
    {
        if (polygon.Count < 3) return new List<List<Vec3>>();
        var subject = new PathsD { new PathD(polygon.Select(q => new PointD(q.X - origin.X, q.Y - origin.Y))) };
        var reach = polygon.Max(q => Vec3.Distance2D(q, origin)) * 4 + 100;
        foreach (var c in planes)
        {
            double px = c.X - origin.X, py = c.Y - origin.Y;
            // a big square on the kept side of the line
            double tx = -c.NY, ty = c.NX;
            var half = new PathD
            {
                new PointD(px - tx * reach, py - ty * reach), new PointD(px + tx * reach, py + ty * reach),
                new PointD(px + tx * reach + c.NX * reach, py + ty * reach + c.NY * reach), new PointD(px - tx * reach + c.NX * reach, py - ty * reach + c.NY * reach)
            };
            subject = Clipper.Intersect(subject, new PathsD { half }, FillRule.NonZero, 6);
            if (subject.Count == 0) break;
        }
        return subject.Where(path => Math.Abs(Clipper.Area(path)) > 1e-6).OrderByDescending(path => Math.Abs(Clipper.Area(path)))
            .Select(path => path.Select(q => new Vec3(q.x + origin.X, q.y + origin.Y, origin.Z)).ToList()).ToList();
    }

    /// <summary>
    /// The grid of a segment: that of its parent while it sets none of its own, so cutting moves no hole; with a burden, a
    /// direction or an alignment of its own the rows are laid from its side towards the face (or its back side), half a
    /// burden inside; with a spacing of its own the holes of a row start as in a pattern of its own.
    /// </summary>
    private static void Place(HolePattern root, HolePattern s)
    {
        s.Center = root.Center;
        s.PhaseU = root.PhaseU;
        s.PhaseV = root.PhaseV;
        s.FaceEdge = null;
        var o = s.Overrides;
        var turned = o.Azimuth is double az && Math.Abs(GeoMath.NormalizeBearing(az - root.Azimuth)) > 1e-9;
        var across = turned || (o.Burden is double b && Math.Abs(b - root.Burden) > 1e-9) || s.AnchorSide == PatternAnchorSide.Back && o.Burden != null;
        var along = turned || (o.Spacing is double sp && Math.Abs(sp - root.Spacing) > 1e-9) || (o.Alignment is RowAlignment al && al != root.Alignment);
        if (across || along) HoleDesign.Anchor(s, along, across);
    }

    /// <summary>Whether a point lies in the part of a leaf (no edge offset): inside the outline and on the kept side of every cut.</summary>
    private static bool InRegion(HolePattern leaf, Vec3 pt)
    {
        var outline = leaf.EdgeOutline is { Count: >= 3 } edge ? edge : leaf.Outline;
        if (outline.Count < 3 || !GeoMath.PointInPolygon(outline, pt.X, pt.Y)) return false;
        return leaf.Cuts.All(c => c.Keeps(pt.X, pt.Y));
    }

    /// <summary>Where the node of a hole is in plan: its collar less its offset from the node.</summary>
    private static Vec3 NodeLocation(HolePattern pat, Hole h)
    {
        if (h.GridOffset is not Vec3 off) return h.Collar;
        var (u, v) = HoleDesign.PatternAxes(pat.Azimuth);
        return h.Collar - u * off.X - v * off.Y;
    }

    /// <summary>The node of a grid at a point, within a tenth of a millimetre; null when none is there.</summary>
    private static (int I, int J)? NodeAt(HolePattern pat, Vec3 pt)
    {
        if (pat.Burden <= 0 || pat.Spacing <= 0) return null;
        var (u, v) = HoleDesign.PatternAxes(pat.Azimuth);
        var d = pt - pat.Center;
        double a = d.X * u.X + d.Y * u.Y, b = d.X * v.X + d.Y * v.Y;
        var j = (int)Math.Round((b - pat.PhaseV) / pat.Burden);
        var stagger = pat.Type == PatternType.Staggered && (j & 1) == 1 ? pat.Spacing / 2 : 0;
        var i = (int)Math.Round((a - pat.PhaseU - stagger) / pat.Spacing);
        return Vec3.Distance2D(HoleDesign.NodePoint(pat, i, j), pt) < 1e-4 ? (i, j) : null;
    }

    /// <summary>Deletes holes with their ties and takes them out of their rows.</summary>
    private static void DeleteHoles(BlastProject p, List<Guid> ids)
    {
        if (ids.Count == 0) return;
        var gone = ids.ToHashSet();
        p.Entities.RemoveAll(e => e is TieEntity t && (gone.Contains(t.FromId) || gone.Contains(t.ToId)));
        p.Entities.RemoveAll(e => gone.Contains(e.Id));
        foreach (var r in p.Rows) r.HoleIds.RemoveAll(gone.Contains);
    }

    /// <summary>A new burden and spacing for a pattern and its tree: the grid of the parent placed again, its segments following.</summary>
    public static PatternRebuildReport Resize(BlastProject p, HolePattern root, double burden, double spacing, PatternRebuildOptions? o = null)
    {
        o ??= PatternRebuildOptions.For(p);
        if (TreeRefusal(p, root, o) is { } no) return new PatternRebuildReport { Refusal = no };
        if (burden <= 0 || spacing <= 0) throw new ArgumentException("The burden and the spacing must be above zero.");
        HoleDesign.Resize(root, burden, spacing);
        return Relayout(p, root, o);
    }

    /// <summary>Turns a pattern and its tree by <paramref name="delta"/> degrees about its centre; the cut lines turn with it.</summary>
    public static PatternRebuildReport Rotate(BlastProject p, HolePattern root, double delta, PatternRebuildOptions? o = null)
    {
        o ??= PatternRebuildOptions.For(p);
        if (!root.IsContainer) return HoleDesign.RotatePattern(p, root, delta, o);
        if (TreeRefusal(p, root, o) is { } no) return new PatternRebuildReport { Refusal = no };
        root.Azimuth = GeoMath.NormalizeBearing(root.Azimuth + delta);
        root.FaceEdge = null;
        if (root.RotateOrientation) p.Header.BlastOrientation = GeoMath.NormalizeBearing(p.Header.BlastOrientation + delta);
        o.BearingDelta = delta;
        return Relayout(p, root, o);
    }
}
