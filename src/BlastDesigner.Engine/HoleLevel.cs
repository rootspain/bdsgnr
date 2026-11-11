using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// Holes drilled down to a level (<see cref="Hole.ToLevel"/>): the grade point of the hole is on that elevation, so the
/// length runs along the hole from the collar down to it, plus the subdrill (minus an underdrill, a negative subdrill), and
/// follows the collar, the angle, the subdrill and the level whenever any of them changes.
/// </summary>
public static class HoleLevel
{
    /// <summary>The level of a hole, null when it has none (empty or 0).</summary>
    public static double? Of(Hole h) => h.ToLevel is double z && z != 0 ? z : null;

    /// <summary>Whether the collar of the hole is above a level: a hole can be drilled down to it only then.</summary>
    public static bool Reaches(Hole h, double level) => h.Collar.Z > level;

    /// <summary>
    /// Length of the hole drilled to its level: along the hole from the collar down to the level, plus the subdrill (an
    /// underdrill takes it off). Null without a level, with the collar not above it, with a hole too near horizontal (or
    /// pointing up) to reach it, or with an underdrill as long as the way to the level.
    /// </summary>
    public static double? Length(Hole h)
    {
        if (Of(h) is not double level || !Reaches(h, level)) return null;
        var cos = Math.Cos(h.Angle * Math.PI / 180);
        if (cos < 0.01) return null;
        var length = (h.Collar.Z - level) / cos + h.Subdrill;
        return length > 0 ? length : null;
    }

    /// <summary>
    /// Distance along the hole from its collar to the first surface of the project the hole meets, going from the collar
    /// towards the toe and on past it; null when no surface is in its way. A surface the collar stands on (closer than a
    /// few centimetres) is not one the hole goes down to.
    /// </summary>
    public static double? SurfaceDistance(BlastProject project, Hole h)
    {
        const double onCollar = 0.05;
        Vec3 o = h.Collar, dir = h.Direction;
        double? best = null;
        foreach (var s in project.Entities.OfType<SurfaceEntity>())
        {
            var v = s.Vertices;
            for (int i = 0; i + 2 < s.Triangles.Count; i += 3)
                if (RayTriangle(o, dir, v[s.Triangles[i]], v[s.Triangles[i + 1]], v[s.Triangles[i + 2]]) is double t && t > onCollar && (best == null || t < best))
                    best = t;
        }
        return best;
    }

    /// <summary>Length of a hole drilled down to the first surface in its way: the distance to it plus the subdrill; null without one.</summary>
    public static double? LengthToSurface(BlastProject project, Hole h) =>
        SurfaceDistance(project, h) is double d && d + h.Subdrill > 0 ? d + h.Subdrill : null;

    /// <summary>
    /// Elevation where the hole meets the first surface in its way: the level the To the surface button puts in its field
    /// (the length then follows it, <see cref="Length"/>); null when the hole meets none.
    /// </summary>
    public static double? SurfaceLevel(BlastProject project, Hole h) =>
        SurfaceDistance(project, h) is double d ? h.Collar.Z + h.Direction.Z * d : null;

    /// <summary>Möller–Trumbore: the distance along the ray (unit direction) to the triangle, null when it misses.</summary>
    private static double? RayTriangle(Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c)
    {
        Vec3 e1 = b - a, e2 = c - a;
        var p = Vec3.Cross(d, e2);
        var det = Vec3.Dot(e1, p);
        if (Math.Abs(det) < 1e-12) return null;
        var inv = 1 / det;
        var s = o - a;
        var u = Vec3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return null;
        var q = Vec3.Cross(s, e1);
        var w = Vec3.Dot(d, q) * inv;
        if (w < 0 || u + w > 1) return null;
        return Vec3.Dot(e2, q) * inv;
    }

    /// <summary>
    /// Follows a change of a document (<see cref="Document.Followers"/>): every hole with a level takes the length down to it;
    /// one loaded by its rules is loaded again for the new length, as after an edit of the length.
    /// </summary>
    public static Action? Follow(BlastProject project) => () =>
    {
        foreach (var h in project.Holes)
        {
            if (Length(h) is not double length || Math.Abs(h.Length - length) < 1e-9) continue;
            h.Length = length;
            if (!h.ManualLoading && h.Decks.Count > 0 && h.ActualLength == null) Charging.ApplyRules(project, new[] { h });
        }
    };
}
