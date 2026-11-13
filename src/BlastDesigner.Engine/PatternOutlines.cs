using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// A pattern made from a polygon keeps with it: when the polygon is edited (a vertex moved, added or deleted, the polygon
/// moved), its pattern is laid out on it again in the same change, so one undo takes both back
/// (<see cref="HoleDesign.FitToOutline"/>). A pattern whose holes are drilled, loaded, locked or surveyed stays as it is.
/// </summary>
public static class PatternOutlines
{
    /// <summary>Told when a polygon was edited but its pattern could not be laid out again, and why.</summary>
    public static Action<HolePattern, PatternRefusal>? Refused { get; set; }

    /// <summary>Follows a change of a document (<see cref="Document.Followers"/>).</summary>
    public static Action? Follow(BlastProject project)
    {
        List<(HolePattern Pattern, Guid Polygon, Vec3[] Points, int Revision)>? watched = null;
        foreach (var pat in project.Patterns)
            if (pat.OutlineEntityId is Guid id && project.FindEntity(id) is PolylineEntity { Closed: true } line)
                (watched ??= new()).Add((pat, id, line.Points.ToArray(), pat.Revision));
        if (watched == null) return null;
        return () =>
        {
            foreach (var (pat, id, before, revision) in watched)
            {
                // a pattern the change laid out itself (made, resized, turned) or took away is left to it
                if (!project.Patterns.Contains(pat) || pat.Revision != revision) continue;
                if (project.FindEntity(id) is not PolylineEntity { Closed: true, Points.Count: >= 3 } line) continue;
                if (line.Points.SequenceEqual(before)) continue;
                // moved with all its holes: the grid went along already (HoleDesign.FollowPatterns)
                if (pat.Outline.Count == line.Points.Count && pat.Outline.Zip(line.Points).All(x => Vec3.Distance2D(x.First, x.Second) < 1e-6)) continue;
                var report = HoleDesign.FitToOutline(project, pat, line.Points);
                if (report.Refusal is { } no) Refused?.Invoke(pat, no);
            }
        };
    }
}
