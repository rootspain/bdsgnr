using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>Triangulated surface operations: creation, contours, clipping, simplification and intersections.</summary>
public static class Surfaces
{
    /// <summary>
    /// Bowyer–Watson Delaunay triangulation in plan. Duplicate points (within 1 mm) are merged. The <paramref name="forcedEdges"/>
    /// (pairs of input points) are forced into the triangulation as edges, so no triangle cuts across them; one that crosses
    /// an edge forced before it is left out.
    /// </summary>
    public static SurfaceEntity Triangulate(IEnumerable<Vec3> input, string name = "Surface", double maxEdge = 0, IEnumerable<(Vec3 A, Vec3 B)>? forcedEdges = null)
    {
        var pts = new List<Vec3>();
        var index = new Dictionary<(long, long), int>();
        static (long, long) Key(Vec3 p) => ((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000));
        foreach (var p in input)
            if (index.TryAdd(Key(p), pts.Count)) pts.Add(p);

        var surface = new SurfaceEntity { Name = name };
        if (pts.Count < 3) return surface;

        double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y), maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        var d = Math.Max(maxX - minX, maxY - minY) * 20 + 1;
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;
        // Triangulate on slightly jittered copies so co-circular points (regular grids) cannot produce overlapping triangles,
        // centred on the data: site coordinates in the millions leave too few digits for the circle and side tests.
        var extent = Math.Max(maxX - minX, maxY - minY) + 1;
        var rng = new Random(12345);
        var all = new List<Vec3>(pts.Select(p => new Vec3(p.X - cx + (rng.NextDouble() - 0.5) * 1e-6 * extent, p.Y - cy + (rng.NextDouble() - 0.5) * 1e-6 * extent, p.Z)))
        {
            new(-d, -d, 0),
            new(d, -d, 0),
            new(0, d, 0)
        };
        int s0 = pts.Count, s1 = pts.Count + 1, s2 = pts.Count + 2;
        var tris = new List<Tri> { MakeTri(all, s0, s1, s2) };
        // directed edge -> the triangle that has it: the way from a triangle to its neighbours
        var owner = new Dictionary<long, int>(pts.Count * 6, ConstrainedEdges.Mixed.Instance);
        void Own(int t)
        {
            var r = tris[t];
            owner[ConstrainedEdges.Key(r.A, r.B)] = t;
            owner[ConstrainedEdges.Key(r.B, r.C)] = t;
            owner[ConstrainedEdges.Key(r.C, r.A)] = t;
        }
        Own(0);
        int Across(int a, int b) => owner.TryGetValue(ConstrainedEdges.Key(b, a), out var t) ? t : -1;
        bool Beyond(int a, int b, Vec3 p) => (all[b].X - all[a].X) * (p.Y - all[a].Y) - (all[b].Y - all[a].Y) * (p.X - all[a].X) < 0;
        // a triangle whose circle holds the point: walking towards it from the last one made, close by in the order of insertion
        int Walk(Vec3 p)
        {
            var t = tris.Count - 1;
            for (var step = 0; step < tris.Count && t >= 0; step++)
            {
                var r = tris[t];
                if (r.InCircle(p.X, p.Y)) return t;
                t = Beyond(r.A, r.B, p) ? Across(r.A, r.B) : Beyond(r.B, r.C, p) ? Across(r.B, r.C) : Beyond(r.C, r.A, p) ? Across(r.C, r.A) : -1;
            }
            return -1;
        }
        var bad = new List<int>();
        var seen = new HashSet<int>();
        var next = new Stack<int>();
        // The triangles whose circle holds the point touch each other, so they are found from any one of them,
        // in the order of the list: the triangulation does not depend on how they were found.
        void FindBad(Vec3 p)
        {
            bad.Clear();
            var first = Walk(p);
            if (first < 0)
            {
                for (int t = 0; t < tris.Count; t++)
                    if (tris[t].InCircle(p.X, p.Y)) bad.Add(t);
                return;
            }
            seen.Clear();
            seen.Add(first);
            next.Push(first);
            while (next.TryPop(out var t))
            {
                bad.Add(t);
                var r = tris[t];
                foreach (var n in (ReadOnlySpan<int>)[Across(r.A, r.B), Across(r.B, r.C), Across(r.C, r.A)])
                    if (n >= 0 && seen.Add(n) && tris[n].InCircle(p.X, p.Y)) next.Push(n);
            }
            bad.Sort();
        }

        // Insert points in spatially coherent order for speed.
        var order = Enumerable.Range(0, pts.Count).OrderBy(i => Math.Floor((pts[i].X - minX) / Math.Max(1e-9, (maxX - minX) / 64))).ThenBy(i => pts[i].Y).ToList();
        foreach (var i in order)
        {
            var p = all[i];
            FindBad(p);
            var edges = new Dictionary<(int, int), int>();
            foreach (var t in bad)
            {
                var tr = tris[t];
                AddEdge(edges, tr.A, tr.B);
                AddEdge(edges, tr.B, tr.C);
                AddEdge(edges, tr.C, tr.A);
            }
            foreach (var t in bad)
            {
                var tr = tris[t];
                owner.Remove(ConstrainedEdges.Key(tr.A, tr.B));
                owner.Remove(ConstrainedEdges.Key(tr.B, tr.C));
                owner.Remove(ConstrainedEdges.Key(tr.C, tr.A));
            }
            for (int k = bad.Count - 1; k >= 0; k--)
            {
                tris[bad[k]] = tris[^1];
                tris.RemoveAt(tris.Count - 1);
                if (bad[k] < tris.Count) Own(bad[k]);
            }
            foreach (var (e, count) in edges)
                if (count == 1)
                {
                    tris.Add(MakeTri(all, e.Item1, e.Item2, i));
                    Own(tris.Count - 1);
                }
        }

        var result = new List<int>(tris.Count * 3);
        foreach (var t in tris) { result.Add(t.A); result.Add(t.B); result.Add(t.C); }
        if (forcedEdges != null)
        {
            var forced = new ConstrainedEdges(all.Select(v => v.X).ToArray(), all.Select(v => v.Y).ToArray(), result);
            foreach (var (a, b) in forcedEdges)
                if (index.TryGetValue(Key(a), out var ia) && index.TryGetValue(Key(b), out var ib)) forced.Force(ia, ib);
        }

        surface.Vertices = pts;
        for (int k = 0; k < result.Count; k += 3)
        {
            int ta = result[k], tb = result[k + 1], tc = result[k + 2];
            if (ta >= s0 || tb >= s0 || tc >= s0) continue;
            if (maxEdge > 0 && (Vec3.Distance2D(pts[ta], pts[tb]) > maxEdge || Vec3.Distance2D(pts[tb], pts[tc]) > maxEdge || Vec3.Distance2D(pts[tc], pts[ta]) > maxEdge))
                continue;
            surface.Triangles.Add(ta);
            surface.Triangles.Add(tb);
            surface.Triangles.Add(tc);
        }
        return surface;
    }

    private static void AddEdge(Dictionary<(int, int), int> edges, int a, int b)
    {
        var key = a < b ? (a, b) : (b, a);
        edges[key] = edges.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    private static Tri MakeTri(List<Vec3> pts, int a, int b, int c)
    {
        var pa = pts[a];
        var pb = pts[b];
        var pc = pts[c];
        // Keep counter-clockwise orientation.
        if ((pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X) < 0) (b, c, pb, pc) = (c, b, pc, pb);
        var dd = 2 * (pa.X * (pb.Y - pc.Y) + pb.X * (pc.Y - pa.Y) + pc.X * (pa.Y - pb.Y));
        double ux, uy;
        if (Math.Abs(dd) < 1e-18) { ux = pa.X; uy = pa.Y; }
        else
        {
            var a2 = pa.X * pa.X + pa.Y * pa.Y;
            var b2 = pb.X * pb.X + pb.Y * pb.Y;
            var c2 = pc.X * pc.X + pc.Y * pc.Y;
            ux = (a2 * (pb.Y - pc.Y) + b2 * (pc.Y - pa.Y) + c2 * (pa.Y - pb.Y)) / dd;
            uy = (a2 * (pc.X - pb.X) + b2 * (pa.X - pc.X) + c2 * (pb.X - pa.X)) / dd;
        }
        var r2 = (pa.X - ux) * (pa.X - ux) + (pa.Y - uy) * (pa.Y - uy);
        return new Tri(a, b, c, ux, uy, r2);
    }

    private readonly record struct Tri(int A, int B, int C, double Cx, double Cy, double R2)
    {
        public bool InCircle(double x, double y) => (x - Cx) * (x - Cx) + (y - Cy) * (y - Cy) < R2 * (1 - 1e-12);
    }

    /// <summary>
    /// Creates a surface from points and polylines. Breaklines are densified and every piece of them is forced into the
    /// triangulation as an edge, so the surface follows the lines and no triangle cuts across them.
    /// </summary>
    public static SurfaceEntity FromEntities(IEnumerable<Entity> entities, bool honourBreaklines, double densify = 2.0, double maxEdge = 0)
    {
        var pts = new List<Vec3>();
        var edges = new List<(Vec3, Vec3)>();
        foreach (var e in entities)
        {
            switch (e)
            {
                case PolylineEntity pl when honourBreaklines:
                    for (int i = 0; i < pl.Points.Count; i++)
                    {
                        pts.Add(pl.Points[i]);
                        var next = i + 1 < pl.Points.Count ? pl.Points[i + 1] : pl.Closed ? pl.Points[0] : (Vec3?)null;
                        if (next is not Vec3 n) continue;
                        var len = Vec3.Distance(pl.Points[i], n);
                        int steps = Math.Max(1, (int)(len / densify));
                        var prev = pl.Points[i];
                        for (int k = 1; k <= steps; k++)
                        {
                            var q = k == steps ? n : Vec3.Lerp(pl.Points[i], n, (double)k / steps);
                            if (k < steps) pts.Add(q);
                            edges.Add((prev, q));
                            prev = q;
                        }
                    }
                    break;
                case Hole h:
                    pts.Add(h.Collar);
                    break;
                case SurfaceEntity s:
                    pts.AddRange(s.Vertices);
                    break;
                default:
                    pts.AddRange(e.GetVertices());
                    break;
            }
        }
        return Triangulate(pts, honourBreaklines ? "Surface (breaklines)" : "Surface", maxEdge, honourBreaklines ? edges : null);
    }

    /// <summary>
    /// Splits a survey surface into its bench faces and the rest. A triangle is a face when its corners all lie on crest
    /// and toe lines, with at least one of each (the band between a crest and its toe), or when it is steeper than
    /// <paramref name="minSlopeDeg"/> and touches a crest or toe (from the line down to a spot height on the face).
    /// Berms, roads and windrows stay with the rest: they are flat, or their edges are neither crests nor toes.
    /// Faces is null when no triangle qualifies.
    /// </summary>
    public static (SurfaceEntity Ground, SurfaceEntity? Faces) SplitFaces(SurfaceEntity s, IEnumerable<IReadOnlyList<Vec3>> crests, IEnumerable<IReadOnlyList<Vec3>> toes, double minSlopeDeg = 30)
    {
        // which vertices lie on a crest (1) or a toe (2) line: within 1 cm of a segment, found through a 5 m grid
        const double cell = 5, near = 0.01;
        var segs = new List<(Vec3 A, Vec3 B, int Class)>();
        foreach (var (lines, cls) in new[] { (crests, 1), (toes, 2) })
            foreach (var l in lines)
                for (int i = 1; i < l.Count; i++) segs.Add((l[i - 1], l[i], cls));
        var grid = new Dictionary<(long, long), List<int>>();
        for (int i = 0; i < segs.Count; i++)
        {
            var (a, b, _) = segs[i];
            for (var x = (long)Math.Floor((Math.Min(a.X, b.X) - near) / cell); x <= (long)Math.Floor((Math.Max(a.X, b.X) + near) / cell); x++)
                for (var y = (long)Math.Floor((Math.Min(a.Y, b.Y) - near) / cell); y <= (long)Math.Floor((Math.Max(a.Y, b.Y) + near) / cell); y++)
                {
                    if (!grid.TryGetValue((x, y), out var list)) grid[(x, y)] = list = new();
                    list.Add(i);
                }
        }
        var onLine = new int[s.Vertices.Count];
        for (int v = 0; v < onLine.Length; v++)
        {
            var p = s.Vertices[v];
            if (!grid.TryGetValue(((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell)), out var list)) continue;
            foreach (var i in list)
            {
                // on the line in plan and in height (a twin line a few metres above is not the same line)
                var (q, _) = GeoMath.ClosestOnSegment2D(segs[i].A, segs[i].B, p);
                if (Vec3.Distance2D(p, q) <= near && Math.Abs(p.Z - q.Z) <= 0.05) onLine[v] |= segs[i].Class;
            }
        }

        var tan = Math.Tan(minSlopeDeg * Math.PI / 180);
        var face = new List<int>();
        var rest = new List<int>();
        for (int t = 0; t + 2 < s.Triangles.Count; t += 3)
        {
            int a = s.Triangles[t], b = s.Triangles[t + 1], c = s.Triangles[t + 2];
            int all = onLine[a] | onLine[b] | onLine[c];
            bool band = onLine[a] != 0 && onLine[b] != 0 && onLine[c] != 0 && all == 3;
            bool steep = all != 0 && Slope(s.Vertices[a], s.Vertices[b], s.Vertices[c]) >= tan;
            var into = band || steep ? face : rest;
            into.Add(a); into.Add(b); into.Add(c);
        }
        if (face.Count == 0) return (s, null);
        return (Part(s, rest, s.Name), Part(s, face, s.Name));

        // rise over run of the triangle's plane
        static double Slope(Vec3 a, Vec3 b, Vec3 c)
        {
            var n = Vec3.Cross(b - a, c - a);
            var h = Math.Sqrt(n.X * n.X + n.Y * n.Y);
            return Math.Abs(n.Z) < 1e-12 ? double.MaxValue : h / Math.Abs(n.Z);
        }
        // the given triangles with only the vertices they use
        static SurfaceEntity Part(SurfaceEntity s, List<int> tris, string name)
        {
            var map = new Dictionary<int, int>();
            var part = new SurfaceEntity { Name = name, Kind = s.Kind, ShowWireframe = s.ShowWireframe };
            foreach (var i in tris)
            {
                if (!map.TryGetValue(i, out var j)) { j = part.Vertices.Count; map[i] = j; part.Vertices.Add(s.Vertices[i]); }
                part.Triangles.Add(j);
            }
            return part;
        }
    }

    /// <summary>
    /// Splits the bench platforms off a survey surface (its faces already split off by <see cref="SplitFaces"/>). A
    /// triangle is on a bench when it is gentler than <paramref name="maxSlopeDeg"/> and has, within
    /// <paramref name="reach"/> m in plan and <paramref name="level"/> m in height of its middle, both a crest (the edge
    /// of the face below the platform) and a toe (the foot of the face above it). The ground above the top crest (no toe
    /// at its level) and the pit floor (no crest at its level) stay with the rest. Benches is null when none qualifies.
    /// </summary>
    public static (SurfaceEntity Ground, SurfaceEntity? Benches) SplitBenches(SurfaceEntity s, IEnumerable<IReadOnlyList<Vec3>> crests, IEnumerable<IReadOnlyList<Vec3>> toes,
        double maxSlopeDeg = 20, double reach = 40, double level = 1.5)
    {
        // crest and toe points every 2 m, in a grid of 10 m cells
        const double cell = 10, step = 2;
        Dictionary<(long, long), List<Vec3>> Grid(IEnumerable<IReadOnlyList<Vec3>> lines)
        {
            var grid = new Dictionary<(long, long), List<Vec3>>();
            void Put(Vec3 p)
            {
                var key = ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell));
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = new();
                list.Add(p);
            }
            foreach (var l in lines)
                for (int i = 0; i < l.Count; i++)
                {
                    Put(l[i]);
                    if (i == 0) continue;
                    int n = (int)(Vec3.Distance2D(l[i - 1], l[i]) / step);
                    for (int k = 1; k < n; k++) Put(Vec3.Lerp(l[i - 1], l[i], (double)k / n));
                }
            return grid;
        }
        var crestGrid = Grid(crests);
        var toeGrid = Grid(toes);
        bool Near(Dictionary<(long, long), List<Vec3>> grid, Vec3 c)
        {
            int r = (int)Math.Ceiling(reach / cell);
            long cx = (long)Math.Floor(c.X / cell), cy = (long)Math.Floor(c.Y / cell);
            for (var x = cx - r; x <= cx + r; x++)
                for (var y = cy - r; y <= cy + r; y++)
                    if (grid.TryGetValue((x, y), out var list))
                        foreach (var p in list)
                            if (Math.Abs(p.Z - c.Z) <= level && Vec3.Distance2D(p, c) <= reach) return true;
            return false;
        }

        // gentle triangles, and which of them see a crest and a toe at their level
        var tan = Math.Tan(maxSlopeDeg * Math.PI / 180);
        int count = s.Triangles.Count / 3;
        var flat = new bool[count];
        var seen = new bool[count];
        for (int t = 0; t < count; t++)
        {
            var a = s.Vertices[s.Triangles[3 * t]]; var b = s.Vertices[s.Triangles[3 * t + 1]]; var c = s.Vertices[s.Triangles[3 * t + 2]];
            var n = Vec3.Cross(b - a, c - a);
            flat[t] = Math.Abs(n.Z) > 1e-12 && Math.Sqrt(n.X * n.X + n.Y * n.Y) / Math.Abs(n.Z) <= tan;
            var mid = (a + b + c) / 3;
            seen[t] = flat[t] && Near(crestGrid, mid) && Near(toeGrid, mid);
        }
        // From each triangle that sees both lines the bench spreads over its gentle neighbours at the same level (within
        // the level of that triangle) and within twice the reach of it: a platform wider than the reach fills up, a
        // ramp stops once it has climbed or dropped. Whole connected patches were too much: ramps and gaps in the
        // faces joined the platforms into one.
        var byEdge = new Dictionary<(int, int), List<int>>();
        var mids = new Vec3[count];
        for (int t = 0; t < count; t++)
        {
            mids[t] = (s.Vertices[s.Triangles[3 * t]] + s.Vertices[s.Triangles[3 * t + 1]] + s.Vertices[s.Triangles[3 * t + 2]]) / 3;
            for (int k = 0; k < 3; k++)
            {
                int u = s.Triangles[3 * t + k], v = s.Triangles[3 * t + (k + 1) % 3];
                var key = u < v ? (u, v) : (v, u);
                if (!byEdge.TryGetValue(key, out var list)) byEdge[key] = list = new List<int>(2);
                list.Add(t);
            }
        }
        var isBench = new bool[count];
        var queue = new Queue<(int T, Vec3 Seed)>();
        for (int t = 0; t < count; t++)
            if (seen[t]) { isBench[t] = true; queue.Enqueue((t, mids[t])); }
        while (queue.Count > 0)
        {
            var (t, seed) = queue.Dequeue();
            for (int k = 0; k < 3; k++)
            {
                int u = s.Triangles[3 * t + k], v = s.Triangles[3 * t + (k + 1) % 3];
                foreach (var o in byEdge[u < v ? (u, v) : (v, u)])
                    if (!isBench[o] && flat[o] && Math.Abs(mids[o].Z - seed.Z) <= level && Vec3.Distance2D(mids[o], seed) <= 2 * reach)
                    {
                        isBench[o] = true;
                        queue.Enqueue((o, seed));
                    }
            }
        }
        var bench = new List<int>();
        var rest = new List<int>();
        for (int t = 0; t < count; t++)
        {
            var into = isBench[t] ? bench : rest;
            into.Add(s.Triangles[3 * t]); into.Add(s.Triangles[3 * t + 1]); into.Add(s.Triangles[3 * t + 2]);
        }
        if (bench.Count == 0) return (s, null);
        return (Part(s, rest), Part(s, bench));

        static SurfaceEntity Part(SurfaceEntity s, List<int> tris)
        {
            var map = new Dictionary<int, int>();
            var part = new SurfaceEntity { Name = s.Name, Kind = s.Kind, ShowWireframe = s.ShowWireframe };
            foreach (var i in tris)
            {
                if (!map.TryGetValue(i, out var j)) { j = part.Vertices.Count; map[i] = j; part.Vertices.Add(s.Vertices[i]); }
                part.Triangles.Add(j);
            }
            return part;
        }
    }

    /// <summary>Contour lines at a fixed interval, joined into polylines.</summary>
    public static List<PolylineEntity> Contours(SurfaceEntity s, double interval, double? onlyLevel = null)
    {
        var result = new List<PolylineEntity>();
        if (s.Vertices.Count == 0 || interval <= 0 && onlyLevel is null) return result;
        var minZ = s.Vertices.Min(v => v.Z);
        var maxZ = s.Vertices.Max(v => v.Z);
        var levels = new List<double>();
        if (onlyLevel is double lv) levels.Add(lv);
        else for (var z = Math.Ceiling(minZ / interval) * interval; z <= maxZ; z += interval) levels.Add(z);

        // Levels are nudged slightly so contours never pass exactly through vertices (which yields degenerate segments).
        var eps = 1e-7 * (maxZ - minZ + 1);
        foreach (var level in levels)
        {
            var z = level + eps;
            var segments = new List<(Vec3, Vec3)>();
            for (int t = 0; t < s.Triangles.Count; t += 3)
            {
                var a = s.Vertices[s.Triangles[t]];
                var b = s.Vertices[s.Triangles[t + 1]];
                var c = s.Vertices[s.Triangles[t + 2]];
                var cut = new List<Vec3>(2);
                Cross(a, b, z, cut);
                Cross(b, c, z, cut);
                Cross(c, a, z, cut);
                if (cut.Count == 2 && Vec3.Distance2D(cut[0], cut[1]) > 1e-4) segments.Add((cut[0], cut[1]));
            }
            foreach (var line in JoinSegments(segments))
            {
                var pts = line.Select(p => p.WithZ(level)).ToList();
                var closed = pts.Count > 3 && Vec3.Distance(pts[0], pts[^1]) < 1e-6;
                if (closed) pts.RemoveAt(pts.Count - 1);
                result.Add(new PolylineEntity { Points = pts, Kind = LineKind.Contour, Label = level.ToString("0.##"), Closed = closed });
            }
        }
        return result;
    }

    private static void Cross(Vec3 a, Vec3 b, double z, List<Vec3> output)
    {
        if ((a.Z < z) == (b.Z < z)) return;
        var t = (z - a.Z) / (b.Z - a.Z);
        output.Add(Vec3.Lerp(a, b, t).WithZ(z));
    }

    /// <summary>Joins unordered segments into polylines by matching endpoints.</summary>
    public static List<List<Vec3>> JoinSegments(List<(Vec3 A, Vec3 B)> segments)
    {
        static (long, long) Key(Vec3 p) => ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4));
        var byEnd = new Dictionary<(long, long), List<int>>();
        for (int i = 0; i < segments.Count; i++)
        {
            foreach (var p in new[] { segments[i].A, segments[i].B })
            {
                var k = Key(p);
                if (!byEnd.TryGetValue(k, out var l)) byEnd[k] = l = new();
                l.Add(i);
            }
        }
        var used = new bool[segments.Count];
        var lines = new List<List<Vec3>>();
        for (int i = 0; i < segments.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var line = new LinkedList<Vec3>();
            line.AddLast(segments[i].A);
            line.AddLast(segments[i].B);
            foreach (var forward in new[] { true, false })
            {
                while (true)
                {
                    var end = forward ? line.Last!.Value : line.First!.Value;
                    var next = byEnd[Key(end)].FirstOrDefault(j => !used[j], -1);
                    if (next < 0) break;
                    used[next] = true;
                    var s = segments[next];
                    var other = Key(s.A) == Key(end) ? s.B : s.A;
                    if (forward) line.AddLast(other); else line.AddFirst(other);
                }
            }
            lines.Add(line.ToList());
        }
        return lines;
    }

    /// <summary>Removes triangles whose centroid lies outside the polygon (or inside it when <paramref name="keepInside"/> is false).</summary>
    public static SurfaceEntity Clip(SurfaceEntity s, IReadOnlyList<Vec3> polygon, bool keepInside = true)
    {
        var result = new SurfaceEntity { Name = s.Name + " (clipped)", Kind = s.Kind };
        var map = new Dictionary<int, int>();
        for (int t = 0; t < s.Triangles.Count; t += 3)
        {
            var c = (s.Vertices[s.Triangles[t]] + s.Vertices[s.Triangles[t + 1]] + s.Vertices[s.Triangles[t + 2]]) / 3;
            if (GeoMath.PointInPolygon(polygon, c.X, c.Y) != keepInside) continue;
            for (int k = 0; k < 3; k++)
            {
                var vi = s.Triangles[t + k];
                if (!map.TryGetValue(vi, out var ni)) { ni = result.Vertices.Count; result.Vertices.Add(s.Vertices[vi]); map[vi] = ni; }
                result.Triangles.Add(ni);
            }
        }
        return result;
    }

    /// <summary>Vertex-clustering decimation on a plan grid of the given cell size.</summary>
    public static SurfaceEntity Decimate(SurfaceEntity s, double cellSize)
    {
        var groups = s.Vertices.Select((v, i) => (v, i))
            .GroupBy(x => ((long)Math.Floor(x.v.X / cellSize), (long)Math.Floor(x.v.Y / cellSize)))
            .Select(g => new Vec3(g.Average(x => x.v.X), g.Average(x => x.v.Y), g.Average(x => x.v.Z)));
        var r = Triangulate(groups, s.Name + " (decimated)");
        r.Kind = s.Kind;
        return r;
    }

    /// <summary>Removes vertices whose elevation is predicted within tolerance by the surface without them.</summary>
    public static SurfaceEntity Simplify(SurfaceEntity s, double tolerance, bool smooth)
    {
        var src = smooth ? Smooth(s, 2) : s;
        var neighbours = Neighbours(src);
        var keep = new List<Vec3>();
        var hull = GeoMath.ConvexHull(src.Vertices).ToHashSet();
        for (int i = 0; i < src.Vertices.Count; i++)
        {
            var v = src.Vertices[i];
            if (hull.Contains(v) || neighbours[i].Count < 3) { keep.Add(v); continue; }
            // Inverse-distance prediction from the ring of neighbours.
            double w = 0, z = 0;
            foreach (var n in neighbours[i])
            {
                var d = Math.Max(1e-6, Vec3.Distance2D(src.Vertices[n], v));
                w += 1 / d;
                z += src.Vertices[n].Z / d;
            }
            if (Math.Abs(z / w - v.Z) > tolerance) keep.Add(v);
        }
        var r = Triangulate(keep, s.Name + " (simplified)");
        r.Kind = s.Kind;
        return r;
    }

    /// <summary>Laplacian smoothing of elevations.</summary>
    public static SurfaceEntity Smooth(SurfaceEntity s, int iterations)
    {
        var verts = s.Vertices.ToList();
        var neighbours = Neighbours(s);
        for (int it = 0; it < iterations; it++)
        {
            var next = verts.ToList();
            for (int i = 0; i < verts.Count; i++)
                if (neighbours[i].Count > 0)
                    next[i] = verts[i].WithZ((verts[i].Z + neighbours[i].Average(n => verts[n].Z)) / 2);
            verts = next;
        }
        return new SurfaceEntity { Name = s.Name, Kind = s.Kind, Vertices = verts, Triangles = s.Triangles.ToList() };
    }

    private static List<HashSet<int>> Neighbours(SurfaceEntity s)
    {
        var n = Enumerable.Range(0, s.Vertices.Count).Select(_ => new HashSet<int>()).ToList();
        for (int t = 0; t < s.Triangles.Count; t += 3)
        {
            int a = s.Triangles[t], b = s.Triangles[t + 1], c = s.Triangles[t + 2];
            n[a].Add(b); n[a].Add(c); n[b].Add(a); n[b].Add(c); n[c].Add(a); n[c].Add(b);
        }
        return n;
    }

    /// <summary>Extends a surface outward by a distance, extrapolating hull elevations horizontally.</summary>
    public static SurfaceEntity Expand(SurfaceEntity s, double distance)
    {
        var hull = GeoMath.ConvexHull(s.Vertices);
        var outer = GeoMath.OffsetPolygon(hull, distance);
        var pts = s.Vertices.Concat(outer.Select((p, i) => p.WithZ(hull[i].Z))).ToList();
        var r = Triangulate(pts, s.Name + " (expanded)");
        r.Kind = s.Kind;
        return r;
    }

    /// <summary>Removes sliver triangles (very small minimum angle) along the hull.</summary>
    public static SurfaceEntity ImproveGeometry(SurfaceEntity s, double minAngleDeg = 5)
    {
        var r = new SurfaceEntity { Name = s.Name, Kind = s.Kind, Vertices = s.Vertices.ToList() };
        for (int t = 0; t < s.Triangles.Count; t += 3)
        {
            var a = s.Vertices[s.Triangles[t]];
            var b = s.Vertices[s.Triangles[t + 1]];
            var c = s.Vertices[s.Triangles[t + 2]];
            if (MinAngle(a, b, c) < minAngleDeg) continue;
            r.Triangles.AddRange(new[] { s.Triangles[t], s.Triangles[t + 1], s.Triangles[t + 2] });
        }
        return r;
    }

    private static double MinAngle(Vec3 a, Vec3 b, Vec3 c)
    {
        static double Ang(Vec3 p, Vec3 q, Vec3 r)
        {
            var u = new Vec3(q.X - p.X, q.Y - p.Y, 0).Normalized();
            var v = new Vec3(r.X - p.X, r.Y - p.Y, 0).Normalized();
            return Math.Acos(Math.Clamp(Vec3.Dot(u, v), -1, 1)) / GeoMath.Deg;
        }
        return Math.Min(Ang(a, b, c), Math.Min(Ang(b, c, a), Ang(c, a, b)));
    }

    /// <summary>Drapes a polyline on the surface, inserting points where it crosses triangle edges.</summary>
    public static List<Vec3> Drape(SurfaceEntity s, IReadOnlyList<Vec3> line, double step = 1.0)
    {
        var result = new List<Vec3>();
        for (int i = 0; i < line.Count; i++)
        {
            var p = line[i];
            if (i > 0)
            {
                var a = line[i - 1];
                var len = Vec3.Distance2D(a, p);
                int n = Math.Max(1, (int)(len / step));
                for (int k = 1; k < n; k++)
                {
                    var q = Vec3.Lerp(a, p, (double)k / n);
                    if (s.ElevationAt(q.X, q.Y) is double qz) result.Add(q.WithZ(qz));
                }
            }
            result.Add(s.ElevationAt(p.X, p.Y) is double z ? p.WithZ(z) : p);
        }
        return result;
    }

    /// <summary>Points where a line crosses the triangle edges of the surface (segment points).</summary>
    public static List<Vec3> SegmentPoints(SurfaceEntity s, IReadOnlyList<Vec3> line)
    {
        var pts = new List<Vec3>();
        var edges = new HashSet<(int, int)>();
        for (int t = 0; t < s.Triangles.Count; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = s.Triangles[t + k], b = s.Triangles[t + (k + 1) % 3];
                edges.Add(a < b ? (a, b) : (b, a));
            }
        for (int i = 0; i < line.Count - 1; i++)
            foreach (var (a, b) in edges)
                if (GeoMath.SegmentIntersect2D(line[i], line[i + 1], s.Vertices[a], s.Vertices[b], out _, out var u))
                    pts.Add(Vec3.Lerp(s.Vertices[a], s.Vertices[b], u));
        return pts;
    }

    /// <summary>Intersection between two surfaces: zero contour of their elevation difference.</summary>
    public static List<PolylineEntity> Intersect(SurfaceEntity a, SurfaceEntity b)
    {
        var diffVerts = new List<Vec3>();
        foreach (var v in a.Vertices)
            if (b.ElevationAt(v.X, v.Y) is double z) diffVerts.Add(new Vec3(v.X, v.Y, v.Z - z));
        foreach (var v in b.Vertices)
            if (a.ElevationAt(v.X, v.Y) is double z) diffVerts.Add(new Vec3(v.X, v.Y, z - v.Z));
        var diff = Triangulate(diffVerts);
        var lines = Contours(diff, 0, 0);
        foreach (var l in lines)
        {
            l.Points = l.Points.Select(p => a.ElevationAt(p.X, p.Y) is double z ? p.WithZ(z) : p).ToList();
            l.Kind = LineKind.Intersection;
        }
        return lines;
    }

    /// <summary>Cut and fill volumes of surface <paramref name="top"/> above <paramref name="bottom"/> inside a polygon, by grid sampling.</summary>
    public static (double Cut, double Fill, double Area) VolumeBetween(SurfaceEntity top, SurfaceEntity? bottom, double baseElevation, IReadOnlyList<Vec3>? polygon, double cell = 1.0)
    {
        var b = top.GetBounds();
        double cut = 0, fill = 0, area = 0;
        for (var x = b.Min.X + cell / 2; x < b.Max.X; x += cell)
            for (var y = b.Min.Y + cell / 2; y < b.Max.Y; y += cell)
            {
                if (polygon != null && !GeoMath.PointInPolygon(polygon, x, y)) continue;
                if (top.ElevationAt(x, y) is not double zt) continue;
                var zb = bottom?.ElevationAt(x, y) ?? (bottom == null ? baseElevation : double.NaN);
                if (double.IsNaN(zb)) continue;
                var dz = zt - zb;
                area += cell * cell;
                if (dz > 0) cut += dz * cell * cell; else fill -= dz * cell * cell;
            }
        return (cut, fill, area);
    }

    /// <summary>Generates a regular grid of points from the surface ("Create a point set from surface").</summary>
    public static List<Vec3> PointSet(SurfaceEntity s, double spacing)
    {
        var b = s.GetBounds();
        var pts = new List<Vec3>();
        for (var x = b.Min.X; x <= b.Max.X; x += spacing)
            for (var y = b.Min.Y; y <= b.Max.Y; y += spacing)
                if (s.ElevationAt(x, y) is double z) pts.Add(new Vec3(x, y, z));
        return pts;
    }
}
