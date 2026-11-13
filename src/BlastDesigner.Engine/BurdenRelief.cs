using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// Burden relief over the timing triangulation: in each triangle of the
/// firing points the steepness of the time, |∇t| in ms/m; triangles too thin to tell are left out.
/// </summary>
public static class BurdenRelief
{
    /// <summary>Classes of the colour scale, from the least relief to the most.</summary>
    public const int Classes = 5;

    /// <summary>The triangulation of the firing points of the timed holes, as First movement builds it.</summary>
    public static TimingDelaunay Build(BlastProject project, ISet<Guid>? moved = null, Vec3 offset = default) =>
        TimingDelaunay.Build(FirstMovement.Inputs(project, moved, offset));

    /// <summary>The triangles that count: inside the blast and not too thin.</summary>
    public static IEnumerable<TimingTriangle> Valid(TimingDelaunay d) => d.Triangles.Where(t => t.Internal && t.Valid && t.Slope >= 0);

    /// <summary>The range of the scale: the least relief of the triangles, and the upper limit or their most.</summary>
    public static (double Min, double Max) Range(TimingDelaunay d, double? upperLimit)
    {
        var slopes = Valid(d).Select(t => t.Slope).ToList();
        if (slopes.Count == 0) return (0, upperLimit ?? 1);
        var min = slopes.Min();
        var max = upperLimit is double u && u > min ? u : slopes.Max();
        return (min, Math.Max(max, min + 1e-6));
    }

    /// <summary>The class of a relief, 0 to <see cref="Classes"/> − 1; past the upper limit the last.</summary>
    public static int Class(double slope, double min, double max) =>
        Math.Clamp((int)Math.Floor((slope - min) / (max - min) * Classes), 0, Classes - 1);

    /// <summary>The relief of the triangle under a plan point, ms/m; null outside them.</summary>
    public static double? At(TimingDelaunay d, Vec3 p)
    {
        foreach (var t in Valid(d))
        {
            Vec3 a = d.Vertices[t.V1], b = d.Vertices[t.V2], c = d.Vertices[t.V3];
            double s1 = Cross(a, b, p), s2 = Cross(b, c, p), s3 = Cross(c, a, p);
            if ((s1 >= 0 && s2 >= 0 && s3 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0)) return t.Slope;
        }
        return null;
    }

    static double Cross(Vec3 a, Vec3 b, Vec3 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
}
