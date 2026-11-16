using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>A vertex of the collar and depth polygons of a segment: what it is (null for one that has no name), where, and its two elevations.</summary>
public sealed record SurfaceVertex(string? Key, Vec3 Point, double Collar, double Depth, bool CollarSet, bool DepthSet)
{
    /// <summary>
    /// The elevation of the vertex on the collar polygon (true) or the depth polygon: the one point its grip, its label and
    /// the drawn polygon all stand on.
    /// </summary>
    public double Z(bool collar) => collar ? Collar : Depth;

    /// <summary>The vertex in space on the collar polygon (true) or the depth polygon.</summary>
    public Vec3 On(bool collar) => Point.WithZ(Z(collar));
}

/// <summary>
/// The collar and depth polygons of a pattern or a segment: two polygons on the vertices of its outline, each vertex with
/// an elevation, flat triangles between them. The elevations are kept once for the whole pattern
/// (<see cref="HolePattern.SurfaceVertices"/>) by the name of the vertex, so neighbouring segments share their vertices at
/// a seam and a vertex keeps its elevation when the outline is edited or a cut line moved. A vertex without one takes it
/// from the vertices beside it along the outline of the pattern; with none set, the collar is on the collar surface of the
/// project (or at the level of the grid) and the depth a bench height below it.
/// </summary>
public static class PatternSurfaces
{
    /// <summary>How near to a named point a vertex of a segment is that point, m.</summary>
    private const double Same = 1e-3;

    /// <summary>The named points of a pattern: the vertices of its outline, the crossings of its cut lines with it, the vertices of its islands.</summary>
    public static List<(string Key, Vec3 Point)> NamedPoints(BlastProject p, HolePattern root)
    {
        var list = new List<(string, Vec3)>();
        for (int i = 0; i < root.Outline.Count; i++) list.Add(($"O:{i}", root.Outline[i]));
        for (int k = 0; k < root.SegmentLines.Count; k++)
        {
            var crossings = LineCrossings(root, root.SegmentLines[k]);
            for (int j = 0; j < crossings.Count; j++) list.Add(($"L:{k}:{j + 1}", crossings[j].Point));
        }
        foreach (var island in PatternTree.Children(p, root).Where(s => s.IsIsland))
            for (int i = 0; i < island.IslandOutline!.Count; i++) list.Add(($"I:{island.Id:N}:{i}", island.IslandOutline[i]));
        return list;
    }

    /// <summary>Where a cut line crosses the outline of its pattern, along the line from its left end, with the side (from point k to point k + 1) of each crossing.</summary>
    public static List<(Vec3 Point, int Edge, double T)> LineCrossings(HolePattern root, SegmentLine line)
    {
        var (a1, b1, a2, b2) = line.U1 <= line.U2 ? (line.U1, line.V1, line.U2, line.V2) : (line.U2, line.V2, line.U1, line.V1);
        var p1 = PatternTree.GridToWorld(root, a1, b1);
        var p2 = PatternTree.GridToWorld(root, a2, b2);
        double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
        var o = root.Outline;
        var list = new List<(Vec3, int, double, double)>();
        for (int k = 0; k < o.Count; k++)
        {
            var q1 = o[k];
            var q2 = o[(k + 1) % o.Count];
            double ex = q2.X - q1.X, ey = q2.Y - q1.Y;
            var den = dx * ey - dy * ex;
            if (Math.Abs(den) < 1e-12) continue;
            double fx = q1.X - p1.X, fy = q1.Y - p1.Y;
            var t = (fx * ey - fy * ex) / den;
            var s = (fx * dy - fy * dx) / den;
            if (s < -1e-9 || s > 1 + 1e-9) continue;
            // a crossing through a vertex is found on both sides: once is enough
            var pt = new Vec3(p1.X + dx * t, p1.Y + dy * t, root.Center.Z);
            if (list.Any(x => Vec3.Distance2D(x.Item1, pt) < Same)) continue;
            list.Add((pt, k, Math.Clamp(s, 0, 1), t));
        }
        return list.OrderBy(x => x.Item4).Select(x => (x.Item1, x.Item2, x.Item3)).ToList();
    }

    /// <summary>The elevations of the vertices of the outline of a pattern as they are in effect: set, or worked out along the outline between the set ones.</summary>
    private sealed class OutlineValues
    {
        public double[] Collar = Array.Empty<double>();
        public double[] Depth = Array.Empty<double>();
    }

    private static OutlineValues Outline(HolePattern root, SurfaceEntity? collarSurface)
    {
        var o = root.Outline;
        var byKey = root.SurfaceVertices.ToDictionary(v => v.Key);
        double DefaultCollar(Vec3 pt) => collarSurface?.ElevationAt(pt.X, pt.Y) ?? root.Center.Z;
        var collar = Along(o, i => byKey.TryGetValue($"O:{i}", out var v) ? v.Collar : null, i => DefaultCollar(o[i]));
        var depth = Along(o, i => byKey.TryGetValue($"O:{i}", out var v) ? v.Depth : null, i => collar[i] - root.HoleDefaults.BenchHeight);
        return new OutlineValues { Collar = collar, Depth = depth };
    }

    /// <summary>The values at the vertices of a closed polygon: those set, the others linear along its perimeter between the set ones beside them; with none set, the defaults.</summary>
    private static double[] Along(List<Vec3> o, Func<int, double?> set, Func<int, double> fallback)
    {
        var n = o.Count;
        var values = new double[n];
        var known = Enumerable.Range(0, n).Where(i => set(i) != null).ToList();
        if (known.Count == 0)
        {
            for (int i = 0; i < n; i++) values[i] = fallback(i);
            return values;
        }
        // the distance along the perimeter to each vertex
        var at = new double[n + 1];
        for (int i = 0; i < n; i++) at[i + 1] = at[i] + Vec3.Distance2D(o[i], o[(i + 1) % n]);
        var perimeter = at[n];
        foreach (var i in known) values[i] = set(i)!.Value;
        for (int i = 0; i < n; i++)
        {
            if (set(i) != null) continue;
            // the set vertices before and after it along the perimeter
            int before = known.LastOrDefault(k => k < i, known[^1]), after = known.FirstOrDefault(k => k > i, known[0]);
            var dBefore = (at[i] - at[before] + perimeter) % perimeter;
            var dAfter = (at[after] - at[i] + perimeter) % perimeter;
            if (before == after || dBefore + dAfter < 1e-12) { values[i] = values[before]; continue; }
            values[i] = values[before] + (values[after] - values[before]) * dBefore / (dBefore + dAfter);
        }
        return values;
    }

    /// <summary>
    /// The vertices of the polygons of a pattern or a segment (its outline, and each other piece of it), each with its name
    /// and its two elevations in effect.
    /// </summary>
    public static List<List<SurfaceVertex>> Polygons(BlastProject p, HolePattern pat, SurfaceEntity? collarSurface)
    {
        var root = PatternTree.Root(p, pat);
        var result = new List<List<SurfaceVertex>>();
        if (root.Outline.Count < 3) return result;
        var outline = Outline(root, collarSurface);
        var named = NamedPoints(p, root);
        var byKey = root.SurfaceVertices.ToDictionary(v => v.Key);
        var crossings = Enumerable.Range(0, root.SegmentLines.Count).Select(k => LineCrossings(root, root.SegmentLines[k])).ToList();

        // the elevations a point of the outline of the pattern has from the vertices at the ends of its side
        (double Collar, double Depth)? OnOutline(Vec3 pt)
        {
            var o = root.Outline;
            for (int k = 0; k < o.Count; k++)
            {
                var (c, s) = GeoMath.ClosestOnSegment2D(o[k], o[(k + 1) % o.Count], pt);
                if (Vec3.Distance2D(c, pt) > Same) continue;
                var k1 = (k + 1) % o.Count;
                return (outline.Collar[k] + (outline.Collar[k1] - outline.Collar[k]) * s, outline.Depth[k] + (outline.Depth[k1] - outline.Depth[k]) * s);
            }
            return null;
        }
        double CollarDefault(Vec3 pt) => collarSurface?.ElevationAt(pt.X, pt.Y) ?? root.Center.Z;

        SurfaceVertex Vertex(Vec3 pt)
        {
            var key = named.Where(x => Vec3.Distance2D(x.Point, pt) < Same).Select(x => x.Key).FirstOrDefault();
            byKey.TryGetValue(key ?? "", out var own);
            double collar, depth;
            if (key != null && key.StartsWith("O:") && int.TryParse(key[2..], out var i)) (collar, depth) = (outline.Collar[i], outline.Depth[i]);
            else if (OnOutline(pt) is { } e) (collar, depth) = e;
            else
            {
                collar = CollarDefault(pt);
                depth = collar - root.HoleDefaults.BenchHeight;
            }
            if (own?.Collar is double oc) collar = oc;
            if (own?.Depth is double od) depth = od;
            return new SurfaceVertex(key, pt.WithZ(collar), collar, depth, own?.Collar != null, own?.Depth != null);
        }

        var pieces = pat.IsContainer ? new List<List<Vec3>>() : new[] { pat.Outline }.Concat(pat.OutlineParts).Where(x => x.Count >= 3).ToList();
        foreach (var piece in pieces) result.Add(piece.Select(Vertex).ToList());
        return result;
    }

    /// <summary>The collar and the depth polygon of a pattern or a segment as surfaces (flat triangles on its vertices); null for one it has none of.</summary>
    public static (SurfaceEntity? Collar, SurfaceEntity? Depth) Surfaces(BlastProject p, HolePattern pat, SurfaceEntity? collarSurface)
    {
        var polygons = Polygons(p, pat, collarSurface);
        if (polygons.Count == 0) return (null, null);
        SurfaceEntity Build(Func<SurfaceVertex, double> z)
        {
            var points = polygons.SelectMany(x => x).Select(v => v.Point.WithZ(z(v))).ToList();
            var edges = new List<(Vec3, Vec3)>();
            foreach (var poly in polygons)
                for (int i = 0; i < poly.Count; i++) edges.Add((poly[i].Point.WithZ(z(poly[i])), poly[(i + 1) % poly.Count].Point.WithZ(z(poly[(i + 1) % poly.Count]))));
            return Engine.Surfaces.Triangulate(points, "Pattern polygon", 0, edges);
        }
        return (Build(v => v.Z(true)), Build(v => v.Z(false)));
    }

    /// <summary>The elevation of a polygon surface at a point, or that of its nearest vertex when the point is off it.</summary>
    public static double At(SurfaceEntity surface, double x, double y)
    {
        if (surface.ElevationAt(x, y) is double z) return z;
        return surface.Vertices.Count == 0 ? 0 : surface.Vertices.MinBy(v => (v.X - x) * (v.X - x) + (v.Y - y) * (v.Y - y)).Z;
    }

    /// <summary>
    /// The elevation where the axis of a hole meets the depth polygon (an inclined hole reaches it away from its collar),
    /// to the micrometre.
    /// </summary>
    public static double DepthLevel(Hole h, SurfaceEntity depth)
    {
        var dir = h.Direction;
        var cos = Math.Max(0.05, -dir.Z);
        var z = At(depth, h.Collar.X, h.Collar.Y);
        for (int i = 0; i < 12; i++)
        {
            var along = (h.Collar.Z - z) / cos;
            var pt = h.Collar + dir * along;
            var next = At(depth, pt.X, pt.Y);
            if (Math.Abs(next - z) < 1e-6) { z = next; break; }
            z = next;
        }
        return HoleLevel.Round(z);
    }

    /// <summary>
    /// The names of the crossings of the cut lines after a line was put in at <paramref name="index"/> (+1) or taken away
    /// from it (−1): the lines behind it change their number, the crossings of a line taken away go.
    /// </summary>
    public static void ShiftLines(HolePattern root, int index, int delta)
    {
        if (delta < 0) root.SurfaceVertices.RemoveAll(v => LineOf(v.Key) == index);
        foreach (var v in root.SurfaceVertices)
            if (LineOf(v.Key) is int k && k >= index && (delta > 0 || k > index))
                v.Key = $"L:{k + delta}:{v.Key.Split(':')[2]}";
    }

    private static int? LineOf(string key) => key.StartsWith("L:") && key.Split(':') is { Length: 3 } parts && int.TryParse(parts[1], out var k) ? k : null;

    /// <summary>
    /// The names of the vertices of the outline after the polygon of the pattern was edited: a vertex keeps its elevations
    /// when it keeps its number (a vertex moved) or when one of the new polygon stands where it was (vertices added or
    /// taken away elsewhere); the others go.
    /// </summary>
    public static void RemapOutline(HolePattern root, IReadOnlyList<Vec3> before, IReadOnlyList<Vec3> after)
    {
        if (before.Count == after.Count) return;
        var map = new Dictionary<int, int>();
        for (int i = 0; i < before.Count; i++)
            for (int j = 0; j < after.Count; j++)
                if (Vec3.Distance2D(before[i], after[j]) < Same && !map.ContainsValue(j)) { map[i] = j; break; }
        var kept = new List<PatternVertex>();
        foreach (var v in root.SurfaceVertices)
        {
            if (!v.Key.StartsWith("O:")) { kept.Add(v); continue; }
            if (int.TryParse(v.Key[2..], out var i) && map.TryGetValue(i, out var j)) { v.Key = $"O:{j}"; kept.Add(v); }
        }
        root.SurfaceVertices = kept;
    }

    /// <summary>Sets the elevations of a vertex of a pattern (null leaves that one as it is); <paramref name="clear"/> takes them both away.</summary>
    public static void SetVertex(HolePattern root, string key, double? collar, double? depth, bool clear = false)
    {
        var v = root.SurfaceVertices.FirstOrDefault(x => x.Key == key);
        if (clear) { if (v != null) root.SurfaceVertices.Remove(v); return; }
        if (v == null) root.SurfaceVertices.Add(v = new PatternVertex { Key = key });
        if (collar is double c) v.Collar = HoleLevel.Round(c);
        if (depth is double d) v.Depth = HoleLevel.Round(d);
    }

    /// <summary>
    /// Converts the depths by four numbers at the corners of the rectangle (before schema 5) to polygons: the value of the
    /// rectangle at each vertex of each segment is set at that vertex. Collars on the rectangle become the collar polygon;
    /// in both modes the depth becomes the depth polygon. A vertex shared by two segments takes the value of the later.
    /// </summary>
    public static void FromRectangles(BlastProject p)
    {
        foreach (var root in p.Patterns.Where(x => x.ParentId == null).ToList())
        {
            var leaves = PatternTree.Leaves(p, root).Where(l => l.HoleDefaults.Depth.UsesRectangle).ToList();
            if (leaves.Count == 0) continue;
            var surface = p.Entities.OfType<SurfaceEntity>().FirstOrDefault(s => s.Kind == SurfaceKind.Bench);
            var values = new List<(string Key, double? Collar, double Depth)>();
            foreach (var leaf in leaves)
            {
                var spec = leaf.HoleDefaults.Depth;
                if (DepthRectangle.Of(leaf) is not { } rect) continue;
                foreach (var v in Polygons(p, leaf, surface).SelectMany(x => x).Where(v => v.Key != null))
                {
                    // the bilinear surface of the four numbers carried on out to the vertex, not rounded
                    var (s, t) = rect.RelativeAt(leaf, v.Point);
                    var d = (1 - t) * ((1 - s) * spec.Front.From + s * spec.Front.To) + t * ((1 - s) * spec.Back.From + s * spec.Back.To);
                    // the subdrill stays as the spec says (IncludesSubdrill): the polygon is where the numbers were
                    values.Add(spec.Mode == DepthMode.RectToLevel ? (v.Key!, spec.Level + d, spec.Level) : (v.Key!, null, d));
                }
            }
            foreach (var (key, collar, depth) in values) SetVertex(root, key, collar, depth);
        }
        foreach (var pat in p.Patterns)
            foreach (var spec in new[] { pat.HoleDefaults.Depth, pat.Overrides.Depth }.OfType<DepthSpec>())
            {
                if (!spec.UsesRectangle) continue;
                spec.Collars = spec.Mode == DepthMode.RectToLevel ? CollarSource.Polygon : CollarSource.ProjectSurface;
                spec.Mode = DepthMode.Polygon;
            }
    }
}
