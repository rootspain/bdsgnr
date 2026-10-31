using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>Triangulated surface operations: creation, contours, clipping, simplification and intersections.</summary>
public static class Surfaces
{
    /// <summary>Bowyer–Watson Delaunay triangulation in plan. Duplicate points (within 1 mm) are merged.</summary>
    public static SurfaceEntity Triangulate(IEnumerable<Vec3> input, string name = "Surface", double maxEdge = 0)
    {
        var pts = new List<Vec3>();
        var seen = new HashSet<(long, long)>();
        foreach (var p in input)
            if (seen.Add(((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000)))) pts.Add(p);

        var surface = new SurfaceEntity { Name = name };
        if (pts.Count < 3) return surface;

        double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y), maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        var d = Math.Max(maxX - minX, maxY - minY) * 20 + 1;
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;
        // Triangulate on slightly jittered copies so co-circular points (regular grids) cannot produce overlapping triangles.
        var extent = Math.Max(maxX - minX, maxY - minY) + 1;
        var rng = new Random(12345);
        var all = new List<Vec3>(pts.Select(p => new Vec3(p.X + (rng.NextDouble() - 0.5) * 1e-6 * extent, p.Y + (rng.NextDouble() - 0.5) * 1e-6 * extent, p.Z)))
        {
            new(cx - d, cy - d, 0),
            new(cx + d, cy - d, 0),
            new(cx, cy + d, 0)
        };
        int s0 = pts.Count, s1 = pts.Count + 1, s2 = pts.Count + 2;
        var tris = new List<Tri> { MakeTri(all, s0, s1, s2) };

        // Insert points in spatially coherent order for speed.
        var order = Enumerable.Range(0, pts.Count).OrderBy(i => Math.Floor((pts[i].X - minX) / Math.Max(1e-9, (maxX - minX) / 64))).ThenBy(i => pts[i].Y).ToList();
        foreach (var i in order)
        {
            var p = all[i];
            var bad = new List<int>();
            for (int t = 0; t < tris.Count; t++)
                if (tris[t].InCircle(p.X, p.Y)) bad.Add(t);
            var edges = new Dictionary<(int, int), int>();
            foreach (var t in bad)
            {
                var tr = tris[t];
                AddEdge(edges, tr.A, tr.B);
                AddEdge(edges, tr.B, tr.C);
                AddEdge(edges, tr.C, tr.A);
            }
            for (int k = bad.Count - 1; k >= 0; k--)
            {
                tris[bad[k]] = tris[^1];
                tris.RemoveAt(tris.Count - 1);
            }
            foreach (var (e, count) in edges)
                if (count == 1) tris.Add(MakeTri(all, e.Item1, e.Item2, i));
        }

        surface.Vertices = pts;
        foreach (var t in tris)
        {
            if (t.A >= s0 || t.B >= s0 || t.C >= s0) continue;
            if (maxEdge > 0 && (Vec3.Distance2D(pts[t.A], pts[t.B]) > maxEdge || Vec3.Distance2D(pts[t.B], pts[t.C]) > maxEdge || Vec3.Distance2D(pts[t.C], pts[t.A]) > maxEdge))
                continue;
            surface.Triangles.Add(t.A);
            surface.Triangles.Add(t.B);
            surface.Triangles.Add(t.C);
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

    /// <summary>Creates a surface from points and polylines; breaklines are densified so the triangulation follows them.</summary>
    public static SurfaceEntity FromEntities(IEnumerable<Entity> entities, bool honourBreaklines, double densify = 2.0, double maxEdge = 0)
    {
        var pts = new List<Vec3>();
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
                        int steps = (int)(len / densify);
                        for (int k = 1; k < steps; k++) pts.Add(Vec3.Lerp(pl.Points[i], n, (double)k / steps));
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
        return Triangulate(pts, honourBreaklines ? "Surface (breaklines)" : "Surface", maxEdge);
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
