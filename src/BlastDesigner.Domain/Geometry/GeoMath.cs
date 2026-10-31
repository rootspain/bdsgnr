namespace BlastDesigner.Domain.Geometry;

/// <summary>2D/3D helper algorithms used by the model and the engine.</summary>
public static class GeoMath
{
    public const double Deg = Math.PI / 180.0;

    /// <summary>Unit direction from a bearing (degrees clockwise from north) and an angle from vertical (degrees), pointing down the hole.</summary>
    public static Vec3 HoleDirection(double bearingDeg, double angleFromVerticalDeg)
    {
        var a = angleFromVerticalDeg * Deg;
        var b = bearingDeg * Deg;
        var h = Math.Sin(a);
        return new Vec3(h * Math.Sin(b), h * Math.Cos(b), -Math.Cos(a));
    }

    /// <summary>Bearing in degrees clockwise from north of the 2D vector from a to b, in [0, 360).</summary>
    public static double Bearing(Vec3 a, Vec3 b)
    {
        var deg = Math.Atan2(b.X - a.X, b.Y - a.Y) / Deg;
        return deg < 0 ? deg + 360 : deg;
    }

    public static double NormalizeBearing(double deg)
    {
        deg %= 360;
        return deg < 0 ? deg + 360 : deg;
    }

    /// <summary>Signed area in plan (positive when counter-clockwise).</summary>
    public static double SignedArea(IReadOnlyList<Vec3> poly)
    {
        double s = 0;
        for (int i = 0, n = poly.Count; i < n; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % n];
            s += p.X * q.Y - q.X * p.Y;
        }
        return s / 2;
    }

    public static double Area(IReadOnlyList<Vec3> poly) => Math.Abs(SignedArea(poly));

    /// <summary>True surface area of a (possibly non-planar) 3D polygon using fan triangulation around its centroid.</summary>
    public static double Area3D(IReadOnlyList<Vec3> poly)
    {
        if (poly.Count < 3) return 0;
        var c = Centroid(poly);
        double s = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i] - c;
            var b = poly[(i + 1) % poly.Count] - c;
            s += Vec3.Cross(a, b).Length / 2;
        }
        return s;
    }

    public static double Length(IReadOnlyList<Vec3> line, bool closed = false)
    {
        double s = 0;
        for (int i = 1; i < line.Count; i++) s += Vec3.Distance(line[i - 1], line[i]);
        if (closed && line.Count > 2) s += Vec3.Distance(line[^1], line[0]);
        return s;
    }

    /// <summary>Area-weighted centroid in plan; Z is the average vertex elevation.</summary>
    public static Vec3 Centroid(IReadOnlyList<Vec3> poly)
    {
        if (poly.Count == 0) return Vec3.Zero;
        double a = 0, cx = 0, cy = 0, z = 0;
        for (int i = 0, n = poly.Count; i < n; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % n];
            var cr = p.X * q.Y - q.X * p.Y;
            a += cr;
            cx += (p.X + q.X) * cr;
            cy += (p.Y + q.Y) * cr;
            z += p.Z;
        }
        z /= poly.Count;
        if (Math.Abs(a) < 1e-12)
        {
            double sx = 0, sy = 0;
            foreach (var p in poly) { sx += p.X; sy += p.Y; }
            return new Vec3(sx / poly.Count, sy / poly.Count, z);
        }
        return new Vec3(cx / (3 * a), cy / (3 * a), z);
    }

    public static bool PointInPolygon(IReadOnlyList<Vec3> poly, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var pi = poly[i];
            var pj = poly[j];
            if ((pi.Y > y) != (pj.Y > y) && x < (pj.X - pi.X) * (y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Closest point on segment ab to p in plan, and the parameter t in [0,1].</summary>
    public static (Vec3 Point, double T) ClosestOnSegment2D(Vec3 a, Vec3 b, Vec3 p)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var l2 = dx * dx + dy * dy;
        if (l2 < 1e-18) return (a, 0);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
        return (Vec3.Lerp(a, b, t), t);
    }

    public static double DistanceToPolyline2D(IReadOnlyList<Vec3> line, Vec3 p, bool closed)
    {
        if (line.Count == 0) return double.MaxValue;
        if (line.Count == 1) return Vec3.Distance2D(line[0], p);
        double best = double.MaxValue;
        int n = closed ? line.Count : line.Count - 1;
        for (int i = 0; i < n; i++)
        {
            var (c, _) = ClosestOnSegment2D(line[i], line[(i + 1) % line.Count], p);
            best = Math.Min(best, Vec3.Distance2D(c, p));
        }
        return best;
    }

    /// <summary>Intersection of 2D segments p1-p2 and p3-p4. Returns parameters along each segment.</summary>
    public static bool SegmentIntersect2D(Vec3 p1, Vec3 p2, Vec3 p3, Vec3 p4, out double t, out double u)
    {
        t = u = 0;
        var d = (p2.X - p1.X) * (p4.Y - p3.Y) - (p2.Y - p1.Y) * (p4.X - p3.X);
        if (Math.Abs(d) < 1e-15) return false;
        t = ((p3.X - p1.X) * (p4.Y - p3.Y) - (p3.Y - p1.Y) * (p4.X - p3.X)) / d;
        u = ((p3.X - p1.X) * (p2.Y - p1.Y) - (p3.Y - p1.Y) * (p2.X - p1.X)) / d;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }

    /// <summary>Point at a distance along a polyline (clamped to its ends).</summary>
    public static Vec3 PointAlong(IReadOnlyList<Vec3> line, double distance, out Vec3 direction)
    {
        direction = new Vec3(1, 0, 0);
        if (line.Count == 0) return Vec3.Zero;
        if (line.Count == 1) return line[0];
        double acc = 0;
        for (int i = 1; i < line.Count; i++)
        {
            var seg = Vec3.Distance(line[i - 1], line[i]);
            direction = (line[i] - line[i - 1]).Normalized();
            if (acc + seg >= distance && seg > 0)
                return Vec3.Lerp(line[i - 1], line[i], (distance - acc) / seg);
            acc += seg;
        }
        return line[^1];
    }

    /// <summary>Andrew's monotone chain convex hull in plan.</summary>
    public static List<Vec3> ConvexHull(IEnumerable<Vec3> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3) return pts;
        static double Cross(Vec3 o, Vec3 a, Vec3 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new Vec3[pts.Count * 2];
        int k = 0;
        foreach (var p in pts)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], p) <= 0) k--;
            hull[k++] = p;
        }
        for (int i = pts.Count - 2, t = k + 1; i >= 0; i--)
        {
            var p = pts[i];
            while (k >= t && Cross(hull[k - 2], hull[k - 1], p) <= 0) k--;
            hull[k++] = p;
        }
        return hull.Take(k - 1).ToList();
    }

    /// <summary>Offsets a closed polygon outward (positive distance) using mitred corners.</summary>
    public static List<Vec3> OffsetPolygon(IReadOnlyList<Vec3> poly, double distance)
    {
        int n = poly.Count;
        var result = new List<Vec3>(n);
        if (n < 3) return poly.ToList();
        // Outward for CCW polygons is to the right of the edge direction.
        var sign = SignedArea(poly) > 0 ? 1 : -1;
        for (int i = 0; i < n; i++)
        {
            var prev = poly[(i - 1 + n) % n];
            var cur = poly[i];
            var next = poly[(i + 1) % n];
            var n1 = EdgeNormal(prev, cur) * sign;
            var n2 = EdgeNormal(cur, next) * sign;
            var bis = (n1 + n2).Normalized();
            var cos = Vec3.Dot(bis, n1);
            var len = cos < 0.2 ? distance / 0.2 : distance / cos;
            result.Add(cur + bis * len);
        }
        return result;
    }

    private static Vec3 EdgeNormal(Vec3 a, Vec3 b)
    {
        var d = new Vec3(b.X - a.X, b.Y - a.Y, 0).Normalized();
        return new Vec3(d.Y, -d.X, 0);
    }

    /// <summary>Clips a polyline to the inside of a polygon, returning the pieces that lie inside.</summary>
    public static List<List<Vec3>> ClipPolylineToPolygon(IReadOnlyList<Vec3> line, IReadOnlyList<Vec3> poly)
    {
        var pieces = new List<List<Vec3>>();
        List<Vec3>? current = null;
        for (int i = 0; i < line.Count - 1; i++)
        {
            var a = line[i];
            var b = line[i + 1];
            var cuts = new List<double> { 0, 1 };
            for (int j = 0; j < poly.Count; j++)
                if (SegmentIntersect2D(a, b, poly[j], poly[(j + 1) % poly.Count], out var t, out _))
                    cuts.Add(t);
            cuts.Sort();
            for (int k = 0; k < cuts.Count - 1; k++)
            {
                var t0 = cuts[k];
                var t1 = cuts[k + 1];
                if (t1 - t0 < 1e-9) continue;
                var mid = Vec3.Lerp(a, b, (t0 + t1) / 2);
                if (PointInPolygon(poly, mid.X, mid.Y))
                {
                    var p0 = Vec3.Lerp(a, b, t0);
                    var p1 = Vec3.Lerp(a, b, t1);
                    if (current == null) { current = new List<Vec3> { p0 }; pieces.Add(current); }
                    else if (Vec3.Distance(current[^1], p0) > 1e-6) { current = new List<Vec3> { p0 }; pieces.Add(current); }
                    current.Add(p1);
                }
                else current = null;
            }
        }
        return pieces;
    }

    /// <summary>Douglas-Peucker simplification in plan.</summary>
    public static List<Vec3> Simplify(IReadOnlyList<Vec3> pts, double tolerance)
    {
        if (pts.Count < 3) return pts.ToList();
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            double maxD = 0;
            int idx = -1;
            for (int i = s + 1; i < e; i++)
            {
                var (c, _) = ClosestOnSegment2D(pts[s], pts[e], pts[i]);
                var d = Vec3.Distance2D(c, pts[i]);
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (idx >= 0 && maxD > tolerance)
            {
                keep[idx] = true;
                stack.Push((s, idx));
                stack.Push((idx, e));
            }
        }
        return pts.Where((_, i) => keep[i]).ToList();
    }
}
