using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>What putting collars onto a surface did: the holes seated, those off the surface, and what became of their pattern lists.</summary>
public sealed class CollarDrapeReport
{
    /// <summary>Holes whose collar went onto the surface (and carry it).</summary>
    public int Seated { get; set; }
    /// <summary>Holes with no surface under the collar: left as they were.</summary>
    public int Outside { get; set; }
    /// <summary>Patterns and segments whose Collars list was switched to the surface.</summary>
    public List<string> Switched { get; } = new();
    /// <summary>Patterns and segments with only some of their holes seated: name, holes seated, holes in all. Their list stays.</summary>
    public List<(string Name, int Seated, int Total)> Partial { get; } = new();
}

/// <summary>
/// Holes whose collar sits on a surface (<see cref="Hole.CollarSurfaceId"/>): the collar goes onto it straight down (or up)
/// whenever the hole stands at a new place, its angle, bearing and length as they were (a hole drilled to a level takes its
/// new length from <see cref="HoleLevel.Follow"/>). An elevation of the collar changed alone (typed in) takes it off.
/// </summary>
public static class CollarSurfaces
{
    /// <summary>The surface a hole (or a depth) is bound to, null when there is none or it is gone.</summary>
    public static SurfaceEntity? Find(BlastProject p, Guid? id) => id is Guid sid ? p.FindEntity(sid) as SurfaceEntity : null;

    /// <summary>
    /// Puts the collar onto the surface straight down (or up): the angle, the bearing and the length stay, the surveyed
    /// track and collar go up or down with it. False when the surface is not under the collar (nothing changes).
    /// </summary>
    public static bool Seat(Hole h, SurfaceEntity s)
    {
        if (s.ElevationAt(h.Collar.X, h.Collar.Y) is not double z) return false;
        var dz = z - h.Collar.Z;
        if (dz == 0) return true;
        h.Collar = h.Collar.WithZ(z);
        var shift = new Vec3(0, 0, dz);
        if (h.BoreTrack != null) for (int i = 0; i < h.BoreTrack.Count; i++) h.BoreTrack[i] += shift;
        if (h.ActualCollar is Vec3 a) h.ActualCollar = a + shift;
        return true;
    }

    /// <summary>Seats each hole bound to a surface of the project on it (copies, pasted holes); holes off theirs stay.</summary>
    public static void SeatAll(BlastProject p, IEnumerable<Hole> holes)
    {
        foreach (var h in holes)
            if (Find(p, h.CollarSurfaceId) is { } s) Seat(h, s);
    }

    /// <summary>
    /// The Collars onto surface button: the collars of the holes go onto the surface and are bound to it; with
    /// <paramref name="keepToe"/> the toes stay where they are (the length, angle and bearing change), otherwise the hole
    /// goes up or down whole. Holes off the surface stay as they were. A pattern or segment all of whose holes went onto it
    /// takes the surface in its Collars list (a pattern all of whose segments did, in its own list); one with only some of
    /// its holes seated keeps its list.
    /// </summary>
    public static CollarDrapeReport Drape(BlastProject p, IReadOnlyCollection<Hole> holes, SurfaceEntity s, bool keepToe)
    {
        var report = new CollarDrapeReport();
        var seated = new HashSet<Guid>();
        foreach (var h in holes)
        {
            if (!SeatOne(h, s, keepToe)) { report.Outside++; continue; }
            seated.Add(h.Id);
        }
        report.Seated = seated.Count;
        LinkPatterns(p, seated, s, report);
        return report;
    }

    /// <summary>One hole onto the surface and bound to it; false when the surface is not under it.</summary>
    internal static bool SeatOne(Hole h, SurfaceEntity s, bool keepToe)
    {
        if (s.ElevationAt(h.Collar.X, h.Collar.Y) is not double z) return false;
        if (keepToe)
        {
            var toe = h.DesignToe;
            h.Collar = h.Collar.WithZ(z);
            h.SetToe(toe);
        }
        else Seat(h, s);
        h.CollarSurfaceId = s.Id;
        return true;
    }

    /// <summary>
    /// The patterns of the seated holes: a leaf (a segment, or a pattern not cut) all of whose holes were seated takes the
    /// surface in its depth; when all the leaves of a tree did, its root takes it and the segments that set the depth
    /// themselves take it in theirs (their own depth dropped when it is then the root's).
    /// </summary>
    private static void LinkPatterns(BlastProject p, HashSet<Guid> seated, SurfaceEntity s, CollarDrapeReport report)
    {
        var leaves = p.Holes.Where(h => seated.Contains(h.Id) && h.PatternId != null).Select(h => p.FindPattern(h.PatternId)).OfType<HolePattern>().Distinct().ToList();
        if (leaves.Count == 0) return;
        var full = new HashSet<Guid>();
        foreach (var leaf in leaves)
        {
            var mine = HoleDesign.PatternHoles(p, leaf).ToList();
            var k = mine.Count(h => seated.Contains(h.Id));
            if (k == mine.Count) full.Add(leaf.Id);
            else report.Partial.Add((leaf.Name, k, mine.Count));
        }
        foreach (var root in leaves.Select(l => PatternTree.Root(p, l)).Distinct().ToList())
        {
            var tree = PatternTree.Leaves(p, root);
            if (tree.All(l => full.Contains(l.Id)))
            {
                OnSurface(root.HoleDefaults.Depth, s.Id);
                foreach (var seg in PatternTree.Descendants(p, root))
                {
                    if (seg.Overrides.Depth is not { } own) continue;
                    OnSurface(own, s.Id);
                    var asRoot = root.HoleDefaults.Depth.Clone();
                    asRoot.Mode = PatternTree.EffectiveDepthMode(root);
                    if (own.SameAs(asRoot)) seg.Overrides.Depth = null;
                }
                PatternTree.Resolve(p, root);
                report.Switched.Add(root.Name);
                continue;
            }
            foreach (var leaf in tree.Where(l => full.Contains(l.Id) && l.ParentId != null))
            {
                if (leaf.Overrides.Depth == null)
                {
                    var spec = leaf.HoleDefaults.Depth.Clone();
                    spec.Mode = PatternTree.EffectiveDepthMode(leaf);
                    if (spec.Mode == DepthMode.ToLevel && leaf.ToLevel is double tl) spec.Level = tl;
                    leaf.Overrides.Depth = spec;
                }
                OnSurface(leaf.Overrides.Depth, s.Id);
                report.Switched.Add(leaf.Name);
            }
            PatternTree.Resolve(p, root);
        }
    }

    private static void OnSurface(DepthSpec d, Guid surface)
    {
        d.Collars = CollarSource.ProjectSurface;
        d.CollarSurfaceId = surface;
    }

    /// <summary>
    /// Takes the holes of the leaves of a pattern whose collars do not sit on a surface of their own off theirs (the On the
    /// grid and Collar polygon items of the list), and binds those of the leaves that do to it.
    /// </summary>
    public static void FollowDepth(BlastProject p, HolePattern leaf)
    {
        var d = leaf.HoleDefaults.Depth;
        // the rectangle to a level sets the collars itself
        var id = d.OnOwnSurface && PatternTree.EffectiveDepthMode(leaf) != DepthMode.RectToLevel ? d.CollarSurfaceId : null;
        foreach (var h in HoleDesign.PatternHoles(p, leaf)) h.CollarSurfaceId = id;
    }

    /// <summary>
    /// Follows a change of a document (<see cref="Document.Followers"/>): a hole bound to a surface that is new (pasted,
    /// copied) or stands at a new place in plan is put onto it again; one whose collar elevation alone changed, off the
    /// surface, is taken off it; a change of its angle or bearing leaves it. A surface gone, or not under the hole, leaves
    /// the collar where it is and the hole bound to it. Nothing to do (null) when no hole is bound to a surface.
    /// </summary>
    public static Action? Follow(BlastProject project)
    {
        Dictionary<Guid, (Vec3 Collar, Guid Surface)>? linked = null;
        foreach (var h in project.Holes)
            if (h.CollarSurfaceId is Guid sid) (linked ??= new())[h.Id] = (h.Collar, sid);
        if (linked == null) return null;
        var existed = project.Holes.Select(h => h.Id).ToHashSet();
        return () =>
        {
            foreach (var h in project.Holes)
            {
                if (h.CollarSurfaceId is not Guid sid) continue;
                if (!linked.TryGetValue(h.Id, out var was))
                {
                    // a new hole bound to a surface goes onto it; one the change bound itself is as the change put it
                    if (!existed.Contains(h.Id) && Find(project, sid) is { } fresh) Seat(h, fresh);
                    continue;
                }
                // bound to another surface by the change itself
                if (was.Surface != sid) continue;
                if (Vec3.Distance2D(was.Collar, h.Collar) > 1e-6)
                {
                    if (Find(project, sid) is { } s) Seat(h, s);
                    continue;
                }
                if (Math.Abs(h.Collar.Z - was.Collar.Z) <= 1e-9) continue;
                // the elevation alone changed: off the surface now, unless it is where the surface is
                if (Find(project, sid)?.ElevationAt(h.Collar.X, h.Collar.Y) is not double z || Math.Abs(z - h.Collar.Z) > 1e-6) h.CollarSurfaceId = null;
            }
        };
    }
}
