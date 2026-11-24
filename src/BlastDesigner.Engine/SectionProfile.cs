using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>A hole of a section: where it stands along the line, its collar, toe and grade.</summary>
/// <param name="Along">Distance of its collar from the start of the line, along it, m.</param>
public sealed record SectionHole(Hole Hole, double Along, Vec3 Collar, Vec3 Toe, double GradeZ);

/// <summary>
/// What a section along a line shows of the blast, for its labels and dimensions: the holes within the half width of
/// the line, in their order along it, and the distance between the first two of them along the line.
/// </summary>
public sealed record SectionProfile(IReadOnlyList<SectionHole> Holes, double? Spacing)
{
    /// <summary>Levels closer than this merge into one on the axis of a section, m.</summary>
    public const double MergeLevels = 0.3;

    /// <summary>The top of the frame is a multiple of this, m.</summary>
    public const double FrameStep = 0.5;

    /// <summary>
    /// The holes whose collar is within <paramref name="halfWidth"/> of the line from a to b (square to it), along it;
    /// those in <paramref name="hidden"/> (left out of a sheet) not.
    /// </summary>
    public static SectionProfile Build(BlastProject project, Vec3 a, Vec3 b, double halfWidth, ISet<Guid>? hidden = null)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-9) return new SectionProfile(Array.Empty<SectionHole>(), null);
        dx /= length; dy /= length;
        var holes = new List<SectionHole>();
        foreach (var h in project.Holes)
        {
            if (hidden?.Contains(h.Id) == true) continue;
            // the hole as it is drawn: from the design or from what was drilled (the stage buttons)
            var basis = project.View.HoleBasis;
            var collar = h.DrawnCollar(basis);
            double ox = collar.X - a.X, oy = collar.Y - a.Y;
            var across = -ox * dy + oy * dx;
            if (Math.Abs(across) > halfWidth) continue;
            holes.Add(new SectionHole(h, ox * dx + oy * dy, collar, h.DrawnToe(basis), h.DrawnPointAtDepth(basis, Math.Max(0, h.Length - h.Subdrill)).Z));
        }
        holes.Sort((p, q) => p.Along.CompareTo(q.Along));
        return new SectionProfile(holes, holes.Count >= 2 ? holes[1].Along - holes[0].Along : null);
    }

    /// <summary>
    /// The top of the frame: above the highest collar by the height of the numbers of the holes (<paramref name="label"/>,
    /// m at the scale of the section), up to a multiple of <see cref="FrameStep"/>. With it and the collars a ruler on the
    /// paper tells how even the surface is.
    /// </summary>
    public static double FrameTop(double highestCollar, double label) => Math.Ceiling((highestCollar + label) / FrameStep - 1e-9) * FrameStep;

    /// <summary>The levels in the order given, each closer than <see cref="MergeLevels"/> to one before it left out.</summary>
    public static List<double> Distinct(IEnumerable<double> levels)
    {
        var kept = new List<double>();
        foreach (var z in levels)
            if (!kept.Any(k => Math.Abs(k - z) < MergeLevels)) kept.Add(z);
        return kept;
    }

    /// <summary>
    /// The collar levels of the holes: one each, the ones closer than <see cref="MergeLevels"/> together as their mean.
    /// </summary>
    public IReadOnlyList<double> CollarLevels()
    {
        var groups = new List<List<double>>();
        foreach (var z in Holes.Select(h => h.Collar.Z).OrderByDescending(z => z))
        {
            if (groups.Count > 0 && groups[^1][0] - z < MergeLevels) groups[^1].Add(z);
            else groups.Add(new List<double> { z });
        }
        return groups.Select(g => g.Average()).ToList();
    }
}
