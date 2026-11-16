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
        List<(HolePattern Pattern, Guid Polygon, Vec3[] Points, int Revision, double CentreZ, Dictionary<Guid, Vec3> Holes)>? watched = null;
        // a pattern cut into segments keeps its polygon as SourceOutlineId (an older version leaves it alone)
        foreach (var pat in project.Patterns.Where(x => x.ParentId == null))
            if (pat.PolygonId is Guid id && project.FindEntity(id) is PolylineEntity { Closed: true } line)
                (watched ??= new()).Add((pat, id, line.Points.ToArray(), pat.Revision, pat.Center.Z, PatternTree.TreeHoles(project, pat).ToDictionary(h => h.Id, h => h.Collar)));
        List<(HolePattern Island, Guid Polygon, Vec3[] Points, int Revision)>? islands = null;
        foreach (var s in project.Patterns)
            if (s.IslandPolygonId is Guid iid && project.FindEntity(iid) is PolylineEntity { Closed: true } il)
                (islands ??= new()).Add((s, iid, il.Points.ToArray(), s.Revision));
        if (watched == null && islands == null) return null;
        return () =>
        {
            foreach (var (pat, id, before, revision, centreZ, holes) in watched ?? new())
            {
                // a pattern the change laid out itself (made, resized, turned) or took away is left to it
                if (!project.Patterns.Contains(pat) || pat.Revision != revision) continue;
                if (project.FindEntity(id) is not PolylineEntity { Closed: true, Points.Count: >= 3 } line) continue;
                if (line.Points.SequenceEqual(before)) continue;
                // moved or turned as a whole, up and down too: the pattern goes along as it is, nothing laid out again
                if (RigidMove(before, line.Points) is var (f, turn, dz))
                {
                    if (Follow(project, pat, line.Points, f, turn, dz, centreZ, holes) is { } refused) Refused?.Invoke(pat, refused);
                    continue;
                }
                // moved with all its holes: the grid went along already (HoleDesign.FollowPatterns)
                if (pat.Outline.Count == line.Points.Count && pat.Outline.Zip(line.Points).All(x => Vec3.Distance2D(x.First, x.Second) < 1e-6)) continue;
                var report = pat.IsContainer ? PatternTree.FitToOutline(project, pat, line.Points) : HoleDesign.FitToOutline(project, pat, line.Points);
                if (report.Refusal is { } no) Refused?.Invoke(pat, no);
            }
            // the polygon of an island edited: the island takes its new shape and the pattern is cut again
            foreach (var (island, id, before, revision) in islands ?? new())
            {
                if (!project.Patterns.Contains(island) || island.Revision != revision) continue;
                if (project.FindEntity(id) is not PolylineEntity { Closed: true, Points.Count: >= 3 } line || line.Points.SequenceEqual(before)) continue;
                if (island.IslandOutline is { } now && now.Count == line.Points.Count && now.Zip(line.Points).All(x => Vec3.Distance2D(x.First, x.Second) < 1e-6)) continue;
                if (project.FindPattern(island.ParentId) is not { } root) continue;
                island.IslandOutline = line.Points.Select(q => q.WithZ(root.Center.Z)).ToList();
                var report = PatternTree.Relayout(project, PatternTree.Root(project, root));
                if (report.Refusal is { } no) Refused?.Invoke(root, no);
            }
        };
    }

    /// <summary>
    /// The shift, turn (degrees clockwise) and lift that take every vertex of a polygon to where it is now, within a
    /// millimetre; null when it was not moved as a whole.
    /// </summary>
    private static (Func<Vec3, Vec3> F, double Turn, double Dz)? RigidMove(IReadOnlyList<Vec3> from, IReadOnlyList<Vec3> to)
    {
        if (from.Count < 3 || from.Count != to.Count) return null;
        var (a0, a) = (from[0], to[0]);
        var dz = a.Z - a0.Z;
        var k = Enumerable.Range(1, from.Count - 1).MaxBy(i => Vec3.Distance2D(from[i], a0));
        if (Math.Abs(Vec3.Distance2D(to[k], a) - Vec3.Distance2D(from[k], a0)) > 1e-3) return null;
        var turn = GeoMath.Bearing(a, to[k]) - GeoMath.Bearing(a0, from[k]);
        var flat = GeneralOps.MakeTransform(a - a0, turn, a0, 1);
        Vec3 F(Vec3 q) => flat(q).WithZ(q.Z + dz);
        if (Enumerable.Range(0, from.Count).Any(i => Vec3.Distance(F(from[i]), to[i]) > 1e-3)) return null;
        return (F, turn, dz);
    }

    /// <summary>
    /// A pattern (cut into segments or not) taken along with its polygon moved as a whole: its holes, its outline and grid,
    /// its segments, cut lines and islands (with their polygons), the elevations of its polygons and its depth levels. What
    /// the change moved already (the holes, the grid) is not moved again. The reason when its holes may not move.
    /// </summary>
    private static PatternRefusal? Follow(BlastProject p, HolePattern root, IReadOnlyList<Vec3> polygon, Func<Vec3, Vec3> f, double turn, double dz,
        double centreZ, Dictionary<Guid, Vec3> holesBefore)
    {
        var holes = PatternTree.TreeHoles(p, root).Where(h => holesBefore.TryGetValue(h.Id, out var was) && Vec3.Distance(was, h.Collar) < 1e-9).ToList();
        if (holes.Count > 0 && (root.IsContainer ? PatternTree.TreeRefusal(p, root) : HoleDesign.RebuildRefusal(p, root)) is { } no) return no;
        var tree = new[] { root }.Concat(PatternTree.Descendants(p, root)).ToList();
        // the polygons of the islands, where the change left them
        foreach (var island in tree.Where(s => s.IslandOutline != null && s.IslandPolygonId != null))
            if (p.FindEntity(island.IslandPolygonId!.Value) is PolylineEntity line && line.Points.Count == island.IslandOutline!.Count
                && line.Points.Zip(island.IslandOutline).All(x => Vec3.Distance2D(x.First, x.Second) < 1e-6))
                line.Points = line.Points.Select(f).ToList();
        // the grid in plan, unless the holes moved with the polygon took it already (HoleDesign.FollowPatterns)
        var flat = polygon.Select(q => q.WithZ(root.Center.Z)).ToList();
        if (!(root.Outline.Count == flat.Count && root.Outline.Zip(flat).All(x => Vec3.Distance2D(x.First, x.Second) < 1e-6)))
            PatternTree.Transform(p, root, q => f(q).WithZ(q.Z), turn);
        var lift = centreZ + dz - root.Center.Z;
        if (Math.Abs(lift) > 1e-9) PatternTree.Lift(p, root, lift);
        foreach (var h in holes)
        {
            h.Transform(f);
            if (h.ToLevel is double z) h.ToLevel = z + dz;
        }
        return null;
    }
}
