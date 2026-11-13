using BlastDesigner.Domain.Geometry;

namespace BlastDesigner.Domain.Model;

/// <summary>
/// What the visible blocks of a project let through: while a block's layer is visible only what is inside a block is shown
/// (inside any of them, with several). A hole is shown whole when its collar is inside; points, texts and initiation points
/// by their position; a tie with both ends inside; lines, polygons and surfaces are cut at the walls, top and bottom.
/// Only the showing: the calculations go over the whole project.
/// </summary>
public sealed class BlockClip
{
    private BlockClip(IReadOnlyList<BlockEntity> blocks) => Blocks = blocks;

    public IReadOnlyList<BlockEntity> Blocks { get; }

    /// <summary>The blocks on visible layers; null when there is none and everything is shown.</summary>
    public static BlockClip? Of(BlastProject project)
    {
        List<BlockEntity>? blocks = null;
        foreach (var e in project.Entities)
            if (e is BlockEntity b && project.FindLayer(b.LayerId) is { Visible: true })
                (blocks ??= new()).Add(b);
        return blocks == null ? null : new BlockClip(blocks);
    }

    /// <summary>Changes with any block that cuts: a cache of cut shapes is good while it stays.</summary>
    public int Key
    {
        get
        {
            var h = new HashCode();
            foreach (var b in Blocks) { h.Add(b.Origin.X); h.Add(b.Origin.Y); h.Add(b.Origin.Z); h.Add(b.Azimuth); h.Add(b.Length); h.Add(b.Width); h.Add(b.Height); }
            return h.ToHashCode();
        }
    }

    public bool Contains(Vec3 p)
    {
        foreach (var b in Blocks) if (b.Contains(p, 1e-6)) return true;
        return false;
    }

    /// <summary>Whether anything of the entity is shown.</summary>
    public bool Shows(Entity e) => e switch
    {
        BlockEntity => true,
        Hole h => Contains(h.Collar),
        PointEntity p => Contains(p.Position),
        TextEntity t => Contains(t.Position),
        InitiationPoint ip => Contains(ip.Position),
        EbsOpeningPoint op => Contains(op.Position),
        TieEntity t => Contains(t.FromPos) && Contains(t.ToPos),
        ArrowEntity or DimensionEntity => e.GetVertices().All(Contains),
        TimingLineEntity tl => tl.Points.Any(Contains),
        PolylineEntity pl => Pieces(pl.Points, pl.Closed).Count > 0,
        CircleEntity c => Pieces(c.ToPolygon(48), true).Count > 0,
        SurfaceEntity s => Overlaps(s.GetBounds()),
        _ => e.GetVertices().Any(Contains)
    };

    /// <summary>Whether the box of an object meets the box of a block (a quick test before cutting).</summary>
    public bool Overlaps(Bounds3 box)
    {
        if (box.IsEmpty) return false;
        foreach (var b in Blocks)
        {
            var c = Bounds3.Empty;
            foreach (var v in b.Corners()) c.Add(v);
            if (box.Min.X <= c.Max.X && box.Max.X >= c.Min.X && box.Min.Y <= c.Max.Y && box.Max.Y >= c.Min.Y && box.Min.Z <= c.Max.Z && box.Max.Z >= c.Min.Z) return true;
        }
        return false;
    }

    /// <summary>
    /// The parts of a line inside the blocks, each a run of points; a segment through a block keeps only its middle.
    /// A polygon gives open runs: its outline is not closed along the walls.
    /// </summary>
    public List<List<Vec3>> Pieces(IReadOnlyList<Vec3> pts, bool closed)
    {
        var pieces = new List<List<Vec3>>();
        if (pts.Count == 0) return pieces;
        if (pts.Count == 1) { if (Contains(pts[0])) pieces.Add(new List<Vec3> { pts[0] }); return pieces; }
        List<Vec3>? run = null;
        int n = closed ? pts.Count : pts.Count - 1;
        for (int i = 0; i < n; i++)
        {
            Vec3 a = pts[i], b = pts[(i + 1) % pts.Count];
            foreach (var (t0, t1) in Intervals(a, b))
            {
                var p0 = Lerp(a, b, t0);
                var p1 = Lerp(a, b, t1);
                // goes on from where the run stopped: the end of the last segment
                if (run != null && t0 < 1e-9 && Vec3.Distance(run[^1], p0) < 1e-9) run.Add(p1);
                else { run = new List<Vec3> { p0, p1 }; pieces.Add(run); }
            }
            // a run ends where its segment leaves the blocks
            if (run != null && Vec3.Distance(run[^1], b) > 1e-9) run = null;
        }
        // a polygon whose start is inside: its last run goes on into its first
        if (closed && pieces.Count > 1 && Vec3.Distance(pieces[^1][^1], pieces[0][0]) < 1e-9)
        {
            pieces[^1].AddRange(pieces[0].Skip(1));
            pieces.RemoveAt(0);
        }
        return pieces;
    }

    /// <summary>The parts of a segment inside any block, as sorted, merged parameters along it.</summary>
    private List<(double T0, double T1)> Intervals(Vec3 a, Vec3 b)
    {
        var list = new List<(double, double)>();
        foreach (var block in Blocks)
            if (block.ClipSegment(a, b) is { } t) list.Add(t);
        if (list.Count < 2) return list;
        list.Sort();
        var merged = new List<(double T0, double T1)> { list[0] };
        foreach (var (t0, t1) in list.Skip(1))
            if (t0 <= merged[^1].T1 + 1e-12) merged[^1] = (merged[^1].T0, Math.Max(merged[^1].T1, t1));
            else merged.Add((t0, t1));
        return merged;
    }

    private static Vec3 Lerp(Vec3 a, Vec3 b, double t) => t <= 0 ? a : t >= 1 ? b : a + (b - a) * t;

    /// <summary>
    /// The part of a triangle mesh inside the blocks: a triangle inside a block is kept, one across a wall is cut to the
    /// polygon inside (Sutherland–Hodgman on the six faces) and fanned into triangles. With blocks overlapping, a triangle
    /// in both is taken once.
    /// </summary>
    public (List<Vec3> Vertices, List<int> Triangles) Cut(IReadOnlyList<Vec3> vertices, IReadOnlyList<int> triangles)
    {
        var outV = new List<Vec3>();
        var outT = new List<int>();
        var boxes = Blocks.Select(b =>
        {
            var c = Bounds3.Empty;
            foreach (var v in b.Corners()) c.Add(v);
            return (Block: b, Box: c);
        }).ToList();
        var poly = new List<Vec3>(9);
        var next = new List<Vec3>(9);
        for (int i = 0; i + 2 < triangles.Count; i += 3)
        {
            Vec3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
            foreach (var (block, box) in boxes)
            {
                if (Math.Max(a.X, Math.Max(b.X, c.X)) < box.Min.X || Math.Min(a.X, Math.Min(b.X, c.X)) > box.Max.X
                    || Math.Max(a.Y, Math.Max(b.Y, c.Y)) < box.Min.Y || Math.Min(a.Y, Math.Min(b.Y, c.Y)) > box.Max.Y
                    || Math.Max(a.Z, Math.Max(b.Z, c.Z)) < box.Min.Z || Math.Min(a.Z, Math.Min(b.Z, c.Z)) > box.Max.Z) continue;
                if (block.Contains(a, 1e-9) && block.Contains(b, 1e-9) && block.Contains(c, 1e-9))
                {
                    var k = outV.Count;
                    outV.Add(a); outV.Add(b); outV.Add(c);
                    outT.Add(k); outT.Add(k + 1); outT.Add(k + 2);
                    break;
                }
                poly.Clear(); poly.Add(a); poly.Add(b); poly.Add(c);
                // the six faces, as "local coordinate within 0…size"
                for (int axis = 0; axis < 3 && poly.Count > 0; axis++)
                    for (int side = 0; side < 2 && poly.Count > 0; side++)
                    {
                        double size = axis == 0 ? block.Length : axis == 1 ? block.Width : block.Height;
                        double Inside(Vec3 p) { var (u, v, w) = block.Local(p); var x = axis == 0 ? u : axis == 1 ? v : w; return side == 0 ? x : size - x; }
                        next.Clear();
                        for (int j = 0; j < poly.Count; j++)
                        {
                            Vec3 p = poly[j], q = poly[(j + 1) % poly.Count];
                            double dp = Inside(p), dq = Inside(q);
                            if (dp >= 0) next.Add(p);
                            if ((dp >= 0) != (dq >= 0)) next.Add(p + (q - p) * (dp / (dp - dq)));
                        }
                        (poly, next) = (next, poly);
                    }
                if (poly.Count < 3) continue;
                var start = outV.Count;
                outV.AddRange(poly);
                for (int j = 1; j + 1 < poly.Count; j++) { outT.Add(start); outT.Add(start + j); outT.Add(start + j + 1); }
            }
        }
        return (outV, outT);
    }
}
