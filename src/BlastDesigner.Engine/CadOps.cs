using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>Drawing and layout operations added in version 6: offsets, curved layouts, extensions, surface envelopes.</summary>
public static class CadOps
{
    /// <summary>Offsets an open polyline sideways in plan; positive distance is to the left of the direction of travel. Joints are mitred (limited to 4×).</summary>
    public static List<Vec3> OffsetPolyline(IReadOnlyList<Vec3> line, double distance)
    {
        if (line.Count < 2) return line.ToList();
        var normals = new List<Vec3>();
        for (int i = 0; i < line.Count - 1; i++)
        {
            var d = line[i + 1] - line[i];
            var l = d.Length2D;
            normals.Add(l < 1e-12 ? new Vec3(0, 0, 0) : new Vec3(-d.Y / l, d.X / l, 0));
        }
        var result = new List<Vec3>();
        for (int i = 0; i < line.Count; i++)
        {
            Vec3 n;
            if (i == 0) n = normals[0];
            else if (i == line.Count - 1) n = normals[^1];
            else
            {
                var sum = normals[i - 1] + normals[i];
                var len = sum.Length2D;
                if (len < 1e-9) n = normals[i];
                else
                {
                    var bis = sum / len;
                    var cos = Vec3.Dot(bis, normals[i]);
                    n = bis * Math.Min(4, 1 / Math.Max(0.25, cos));
                }
            }
            result.Add(line[i] + n * distance);
        }
        return result;
    }

    /// <summary>Extends the last segment of a polyline to its first intersection with another polyline (within <paramref name="maxExtension"/>).</summary>
    public static List<Vec3>? ExtendToIntersection(IReadOnlyList<Vec3> line, IReadOnlyList<Vec3> target, bool targetClosed, double maxExtension = 1000, bool atStart = false)
    {
        if (line.Count < 2 || target.Count < 2) return null;
        var pts = atStart ? line.Reverse().ToList() : line.ToList();
        var a = pts[^2];
        var b = pts[^1];
        var dir = (b - a);
        var l = dir.Length2D;
        if (l < 1e-12) return null;
        var far = b + dir / l * maxExtension;
        double bestT = double.MaxValue;
        Vec3? hit = null;
        int n = targetClosed ? target.Count : target.Count - 1;
        for (int i = 0; i < n; i++)
        {
            if (GeoMath.SegmentIntersect2D(b, far, target[i], target[(i + 1) % target.Count], out var t, out var u) && t > 1e-9 && t < bestT)
            {
                bestT = t;
                var q = Vec3.Lerp(target[i], target[(i + 1) % target.Count], u);
                hit = new Vec3(q.X, q.Y, b.Z + (far.Z - b.Z) * t);
            }
        }
        if (hit == null) return null;
        pts[^1] = hit.Value;
        if (atStart) pts.Reverse();
        return pts;
    }

    public static List<Vec3> SegmentMidpoints(IReadOnlyList<Vec3> line, bool closed)
    {
        var r = new List<Vec3>();
        int n = closed ? line.Count : line.Count - 1;
        for (int i = 0; i < n; i++) r.Add(Vec3.Lerp(line[i], line[(i + 1) % line.Count], 0.5));
        return r;
    }

    /// <summary>
    /// Curved layout: rows follow successive offsets of a (curved) baseline, spaced by the burden.
    /// Staggered patterns shift every other row by half the spacing along the curve.
    /// </summary>
    public static List<Hole> CurvedLayout(IReadOnlyList<Vec3> baseline, int rows, double burden, double spacing, bool staggered, bool behindIsLeft, HoleParameters hp, out List<HoleRow> holeRows)
    {
        var holes = new List<Hole>();
        holeRows = new List<HoleRow>();
        for (int r = 0; r < rows; r++)
        {
            var off = OffsetPolyline(baseline, (behindIsLeft ? 1 : -1) * r * burden);
            var length = GeoMath.Length(off);
            var row = new HoleRow { Name = $"Row {r + 1}", Burden = burden, Spacing = spacing };
            var start = staggered && r % 2 == 1 ? spacing / 2 : 0;
            for (double s = start; s <= length + 1e-6; s += spacing)
            {
                var p = GeoMath.PointAlong(off, s, out _);
                var h = HoleDesign.CreateHole(p, hp);
                holes.Add(h);
                row.HoleIds.Add(h.Id);
                h.RowId = row.Id;
            }
            if (row.HoleIds.Count > 0) holeRows.Add(row);
        }
        return holes;
    }

    /// <summary>Each polyline becomes a hole: first vertex is the collar, last vertex is the toe.</summary>
    public static List<Hole> HolesFromPolylines(IEnumerable<PolylineEntity> lines, HoleParameters hp)
    {
        var result = new List<Hole>();
        foreach (var l in lines.Where(l => l.Points.Count >= 2))
        {
            var h = HoleDesign.CreateHole(l.Points[0], hp);
            var top = l.Points[0].Z >= l.Points[^1].Z;
            h.Collar = top ? l.Points[0] : l.Points[^1];
            h.SetToe(top ? l.Points[^1] : l.Points[0]);
            h.Subdrill = Math.Min(h.Subdrill, h.Length);
            if (l.Points.Count > 2) h.BoreTrack = (top ? l.Points : Enumerable.Reverse(l.Points)).ToList();
            result.Add(h);
        }
        return result;
    }

    public static List<Hole> HolesOutside(IEnumerable<Hole> holes, IReadOnlyList<Vec3> polygon) =>
        holes.Where(h => !GeoMath.PointInPolygon(polygon, h.Collar.X, h.Collar.Y)).ToList();

    /// <summary>Surface whose elevation is the minimum (or maximum) of two surfaces wherever either exists.</summary>
    public static SurfaceEntity Envelope(SurfaceEntity a, SurfaceEntity b, bool minimum)
    {
        var pts = new List<Vec3>();
        foreach (var v in a.Vertices.Concat(b.Vertices))
        {
            var za = a.ElevationAt(v.X, v.Y);
            var zb = b.ElevationAt(v.X, v.Y);
            double z = (za, zb) switch
            {
                (double x, double y) => minimum ? Math.Min(x, y) : Math.Max(x, y),
                (double x, null) => x,
                (null, double y) => y,
                _ => v.Z
            };
            pts.Add(v.WithZ(z));
        }
        // Where the surfaces cross, add the crossing lines so the envelope follows them exactly.
        foreach (var line in Surfaces.Intersect(a, b)) pts.AddRange(line.Points);
        var s = Surfaces.Triangulate(pts, (minimum ? "Minimum of " : "Maximum of ") + a.Name + " / " + b.Name);
        s.Kind = a.Kind;
        return s;
    }

    /// <summary>Intersection lines of every pair of the given surfaces.</summary>
    public static List<PolylineEntity> IntersectAll(IReadOnlyList<SurfaceEntity> surfaces)
    {
        var r = new List<PolylineEntity>();
        for (int i = 0; i < surfaces.Count; i++)
            for (int j = i + 1; j < surfaces.Count; j++)
                foreach (var l in Surfaces.Intersect(surfaces[i], surfaces[j]))
                {
                    l.Label = $"{surfaces[i].Name} × {surfaces[j].Name}";
                    r.Add(l);
                }
        return r;
    }

    /// <summary>Extends a surface to a polygon: polygon vertices take the elevation of the nearest surface vertex, then the whole set is re-triangulated.</summary>
    public static SurfaceEntity ExpandToPolygon(SurfaceEntity s, IReadOnlyList<Vec3> polygon, double densify = 5)
    {
        var pts = new List<Vec3>(s.Vertices);
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var n = Math.Max(1, (int)Math.Ceiling(Vec3.Distance2D(a, b) / densify));
            for (int k = 0; k < n; k++)
            {
                var p = Vec3.Lerp(a, b, (double)k / n);
                if (s.ElevationAt(p.X, p.Y) is double z) { pts.Add(p.WithZ(z)); continue; }
                var nearest = s.Vertices.OrderBy(v => Vec3.Distance2D(v, p)).First();
                pts.Add(p.WithZ(nearest.Z));
            }
        }
        var result = Surfaces.Triangulate(pts, s.Name);
        result.Kind = s.Kind;
        return Surfaces.Clip(result, polygon);
    }
}
