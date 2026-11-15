using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>What the Transform tool did: the objects it changed and those it left, by why.</summary>
public sealed record TransformOutcome(int Changed, int Holes, int PatternOutlines, int Locked);

/// <summary>
/// The Transform tool on a project: scale, tilt, mirror and move of the selected objects (doc/transform-tool-plan.md).
/// Holes, ties and anything of a pattern of holes are left (a pattern has its own tool), so are the outlines of patterns and
/// the objects of locked layers; a block is left when tilted about X or Y.
/// </summary>
public static class SpatialTransforms
{
    /// <summary>Why the tool leaves an object, or null when it takes it.</summary>
    public static string? Left(BlastProject p, Entity e, SpatialTransform t) =>
        e is Hole ? "hole" :
        PatternOutlineIds(p).Contains(e.Id) ? "pattern outline" :
        p.FindLayer(e.LayerId) is { Locked: true } or { Frozen: true } ? "locked" :
        !e.Takes(t) ? "not taken" : null;

    /// <summary>The polygons patterns of holes are laid out in: changing one would lay its pattern out again.</summary>
    public static HashSet<Guid> PatternOutlineIds(BlastProject p) =>
        p.Patterns.Where(x => x.OutlineEntityId != null).Select(x => x.OutlineEntityId!.Value).ToHashSet();

    /// <summary>The selected objects the tool changes with <paramref name="t"/>.</summary>
    public static List<Entity> Taken(BlastProject p, IEnumerable<Guid> ids, SpatialTransform t)
    {
        var set = ids as ISet<Guid> ?? ids.ToHashSet();
        var outlines = PatternOutlineIds(p);
        return p.Entities.Where(e => set.Contains(e.Id) && e is not Hole && !outlines.Contains(e.Id)
            && p.FindLayer(e.LayerId) is not ({ Locked: true } or { Frozen: true }) && e.Takes(t)).ToList();
    }

    /// <summary>Changes the selected objects the tool takes, in place; what it left is counted by why.</summary>
    public static TransformOutcome Apply(BlastProject p, IEnumerable<Guid> ids, SpatialTransform t)
    {
        if (t.Problem is { } problem) throw new ArgumentException(problem);
        var set = ids.ToHashSet();
        int changed = 0, holes = 0, outlines = 0, locked = 0;
        foreach (var e in p.Entities.Where(e => set.Contains(e.Id)).ToList())
        {
            switch (Left(p, e, t))
            {
                case "hole": holes++; continue;
                case "pattern outline": outlines++; continue;
                case "locked": locked++; continue;
                case "not taken": continue;
            }
            e.Transform(t);
            changed++;
        }
        return new TransformOutcome(changed, holes, outlines, locked);
    }

    /// <summary>A copy of <paramref name="e"/> as the tool would leave it, with the same id: the preview draws it in place of it.</summary>
    public static Entity Preview(Entity e, SpatialTransform t)
    {
        var copy = BlastDesigner.Domain.ProjectJson.DeepClone<Entity>(e);
        copy.Transform(t);
        return copy;
    }

    /// <summary>
    /// The points the frame of the tool is drawn round: the vertices, a circle by points on it (so the frame holds the whole
    /// circle), a block by its corners.
    /// </summary>
    public static IEnumerable<Vec3> FramePoints(Entity e) => e switch
    {
        CircleEntity c => c.ToPolygon(24),
        _ => e.GetVertices()
    };

    /// <summary>
    /// The frame round points in axes turned clockwise by <paramref name="axesAzimuth"/>: the corners of the box low and
    /// high along each axis of the frame, in the frame's own coordinates (X, Y along the turned axes, Z up).
    /// </summary>
    public static (Vec3 Min, Vec3 Max)? Frame(IEnumerable<Vec3> points, double axesAzimuth)
    {
        var turn = Affine3.RotationZ(axesAzimuth);
        Vec3? lo = null, hi = null;
        foreach (var w in points)
        {
            var q = turn.ApplyVector(w);
            lo = lo is { } l ? new Vec3(Math.Min(l.X, q.X), Math.Min(l.Y, q.Y), Math.Min(l.Z, q.Z)) : q;
            hi = hi is { } h ? new Vec3(Math.Max(h.X, q.X), Math.Max(h.Y, q.Y), Math.Max(h.Z, q.Z)) : q;
        }
        return lo is { } a && hi is { } b ? (a, b) : null;
    }

    /// <summary>A point given in the frame's coordinates (<see cref="Frame"/>), in the world.</summary>
    public static Vec3 FromFrame(Vec3 local, double axesAzimuth) => Affine3.RotationZ(-axesAzimuth).ApplyVector(local);

    /// <summary>
    /// The turn of the axes of an object, for "the axes of the object": a block along its length, a single line along its
    /// first segment, a text along its line; null when the selection has no one object to take them from.
    /// </summary>
    public static double? ObjectAxes(IReadOnlyList<Entity> taken)
    {
        if (taken.Count == 0) return null;
        var first = taken[0];
        double? turn = first switch
        {
            BlockEntity b => b.Azimuth - 90,
            PolylineEntity { Points.Count: >= 2 } pl when Vec3.Distance2D(pl.Points[0], pl.Points[1]) > 1e-9 => -Math.Atan2(pl.Points[1].Y - pl.Points[0].Y, pl.Points[1].X - pl.Points[0].X) * 180 / Math.PI,
            TextEntity t => -t.Rotation,
            DimensionEntity d when Vec3.Distance2D(d.Start, d.End) > 1e-9 => -Math.Atan2(d.End.Y - d.Start.Y, d.End.X - d.Start.X) * 180 / Math.PI,
            _ => null
        };
        return turn is double v ? GeoMath.NormalizeBearing(v) : null;
    }
}
