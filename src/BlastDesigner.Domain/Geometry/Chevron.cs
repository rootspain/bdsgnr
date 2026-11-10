namespace BlastDesigner.Domain.Geometry;

/// <summary>
/// Geometry of the two-segment timing front: <c>[left end, vertex, right end]</c>, both segments of one length, symmetric
/// about the ray that leaves the vertex outwards (as the tip of an arrow points). "Left" is the left side looking along the
/// ray. Everything is in plan; the ends take the elevation of the vertex.
/// </summary>
public static class Chevron
{
    /// <summary>Angle between the segments of a new front, degrees.</summary>
    public const double DefaultAngleDeg = 30;
    public const double MinAngleDeg = 5;
    public const double MaxAngleDeg = 170;
    /// <summary>Shortest segment, m: the shape stays one even when an end is dragged onto the vertex.</summary>
    public const double MinLength = 0.05;

    /// <summary>The three points of a front with its vertex at <paramref name="apex"/> and its ray along <paramref name="rayDir"/>.</summary>
    public static List<Vec3> Build(Vec3 apex, Vec3 rayDir, double angleDeg, double length)
    {
        var d = rayDir.WithZ(0).Normalized();
        if (d == Vec3.Zero) d = new Vec3(0, 1, 0);
        var half = Math.Clamp(angleDeg, MinAngleDeg, MaxAngleDeg) / 2 * GeoMath.Deg;
        length = Math.Max(length, MinLength);
        var back = -d;
        return new List<Vec3> { apex + Turn(back, -half) * length, apex, apex + Turn(back, half) * length };
    }

    /// <summary>Middle of the shape (mean of its three points), the centre it turns about.</summary>
    public static Vec3 Center(IReadOnlyList<Vec3> p) => (p[0] + p[1] + p[2]) / 3;

    /// <summary>Unit plan direction of the ray: from the middle of the base through the vertex.</summary>
    public static Vec3 RayDir(IReadOnlyList<Vec3> p)
    {
        var d = (p[1] - (p[0] + p[2]) / 2).WithZ(0).Normalized();
        return d == Vec3.Zero ? new Vec3(0, 1, 0) : d;
    }

    /// <summary>Angle between the two segments in plan, degrees.</summary>
    public static double AngleDeg(IReadOnlyList<Vec3> p)
    {
        var a = (p[0] - p[1]).WithZ(0);
        var b = (p[2] - p[1]).WithZ(0);
        return Math.Abs(Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y)) / GeoMath.Deg;
    }

    /// <summary>Length of the segments in plan (their mean, should they differ), m.</summary>
    public static double Length(IReadOnlyList<Vec3> p) => (Vec3.Distance2D(p[0], p[1]) + Vec3.Distance2D(p[2], p[1])) / 2;

    /// <summary>The points turned by <paramref name="radians"/> (counter-clockwise in plan) about <paramref name="center"/>; elevations stay.</summary>
    public static List<Vec3> Rotate(IReadOnlyList<Vec3> points, Vec3 center, double radians)
    {
        var (s, c) = Math.SinCos(radians);
        return points.Select(q =>
        {
            double x = q.X - center.X, y = q.Y - center.Y;
            return new Vec3(center.X + x * c - y * s, center.Y + x * s + y * c, q.Z);
        }).ToList();
    }

    /// <summary>
    /// The shape with one end (0 or 2) dragged to <paramref name="target"/>: the vertex and the ray stay, the angle and the
    /// length follow the dragged end, and the other end mirrors it about the ray.
    /// </summary>
    public static List<Vec3> Reshape(IReadOnlyList<Vec3> points, Vec3 target)
    {
        var apex = points[1];
        var d = RayDir(points);
        var v = (target - apex).WithZ(0);
        var back = Vec3.Dot(v, -d);
        var side = Math.Abs(v.X * d.Y - v.Y * d.X);
        var halfDeg = Math.Atan2(side, back) / GeoMath.Deg;
        return Build(apex, d, 2 * halfDeg, v.Length2D);
    }

    static Vec3 Turn(Vec3 v, double a)
    {
        var (s, c) = Math.SinCos(a);
        return new Vec3(v.X * c - v.Y * s, v.X * s + v.Y * c, 0);
    }
}
