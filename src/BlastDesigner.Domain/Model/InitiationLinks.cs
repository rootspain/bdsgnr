using BlastDesigner.Domain.Geometry;

namespace BlastDesigner.Domain.Model;

/// <summary>
/// Keeps initiation points on the holes they are placed on (<see cref="InitiationPoint.HoleId"/>): a moved hole takes
/// its point along, a point moved on its own takes its hole along, a deleted hole takes its point with it. Deleting a
/// point leaves its hole. Applied around every change of a document (<see cref="Document.Execute"/>).
/// </summary>
public static class InitiationLinks
{
    /// <summary>Distance in plan, m, within which a point of an older file counts as standing on a hole.</summary>
    public const double CoincidentTolerance = 0.01;

    /// <summary>Where a bound point and its hole were before a change.</summary>
    public readonly record struct State(Guid Point, Vec3 Position, Vec3 Collar);

    public static List<State> Capture(BlastProject p)
    {
        var list = new List<State>();
        foreach (var ip in p.Entities.OfType<InitiationPoint>())
            if (ip.HoleId is Guid hid && p.FindEntity(hid) is Hole h) list.Add(new State(ip.Id, ip.Position, h.Collar));
        return list;
    }

    /// <summary>Brings bound points and holes back together after a change; returns the number of points removed with their hole.</summary>
    public static int Apply(BlastProject p, IReadOnlyList<State> before)
    {
        var bound = p.Entities.OfType<InitiationPoint>().Where(i => i.HoleId != null).ToList();
        if (bound.Count == 0) return 0;
        var holes = p.Holes.ToDictionary(h => h.Id);
        var was = before.ToDictionary(s => s.Point);
        var gone = new HashSet<Guid>();
        foreach (var ip in bound)
        {
            if (!holes.TryGetValue(ip.HoleId!.Value, out var h)) { gone.Add(ip.Id); continue; }
            // the point moved and its hole did not: the hole follows (a locked hole keeps its place, and the point with it)
            if (was.TryGetValue(ip.Id, out var s) && ip.Position != s.Position && h.Collar == s.Collar && !h.Locked)
                h.Translate(ip.Position - h.Collar);
            ip.Position = h.Collar;
        }
        if (gone.Count > 0)
            p.Entities.RemoveAll(e => gone.Contains(e.Id) || e is TieEntity t && (gone.Contains(t.FromId) || gone.Contains(t.ToId)));
        return gone.Count;
    }

    /// <summary>
    /// For a loaded project: binds a point standing on a hole collar (older files did not record it) and removes points
    /// bound to a hole that is not there.
    /// </summary>
    public static void Normalize(BlastProject p)
    {
        var points = p.Entities.OfType<InitiationPoint>().ToList();
        if (points.Count == 0) return;
        foreach (var ip in points.Where(i => i.HoleId == null))
        {
            var on = p.Holes.Where(h => Vec3.Distance2D(h.Collar, ip.Position) <= CoincidentTolerance)
                .OrderBy(h => Vec3.Distance2D(h.Collar, ip.Position)).FirstOrDefault();
            if (on != null) ip.HoleId = on.Id;
        }
        Apply(p, Array.Empty<State>());
    }

    /// <summary>The point bound to the hole, if any.</summary>
    public static InitiationPoint? PointOn(BlastProject p, Guid holeId) =>
        p.Entities.OfType<InitiationPoint>().FirstOrDefault(i => i.HoleId == holeId);
}
