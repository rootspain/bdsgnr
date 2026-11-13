using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// A surface built from points, lines and polygons (<see cref="SurfaceEntity.SourceIds"/>) stays bound to them:
/// <list type="bullet">
/// <item>a source changed (moved, its vertices or Z edited, deleted) builds the surface again from the sources there are;
/// with fewer than three points left the surface is deleted too;</item>
/// <item>the surface moved or turned takes its sources along, each vertex as far as the surface point it lies on went;</item>
/// <item>the surface reshaped by a command on it (other vertices or triangles) is no longer bound.</item>
/// </list>
/// Hidden objects and objects on frozen layers follow too. All of it is part of the change, one undo step.
/// </summary>
public static class SurfaceSources
{
    /// <summary>The objects a surface can be built from: not holes, not surfaces, not ties, and with points.</summary>
    public static bool CanBeSource(Entity e) => e is not (Hole or SurfaceEntity or TieEntity) && e.GetVertices().Any();

    /// <summary>Builds a surface from the objects and binds it to them; null when they make no triangle.</summary>
    public static SurfaceEntity? Build(IEnumerable<Entity> entities, bool breaklines)
    {
        var sources = entities.Where(CanBeSource).ToList();
        if (sources.Count == 0) return null;
        var s = Surfaces.FromEntities(sources, breaklines);
        if (s.TriangleCount == 0) return null;
        s.SourceIds = sources.Select(e => e.Id).ToList();
        s.SourceBreaklines = breaklines;
        return s;
    }

    private sealed record Shape(Vec3[] Points, bool Closed);

    private static Shape ShapeOf(Entity e) => new(e.GetVertices().ToArray(), e is PolylineEntity { Closed: true });

    private static bool Same(Shape a, Shape b) => a.Closed == b.Closed && a.Points.AsSpan().SequenceEqual(b.Points);

    /// <summary>For <see cref="Document.Followers"/>: takes the shapes before a change, and after it keeps the surfaces with their sources.</summary>
    public static Action? Follow(BlastProject project)
    {
        var bound = project.Entities.OfType<SurfaceEntity>().Where(s => s.SourceIds.Count > 0).ToList();
        if (bound.Count == 0) return null;
        var surfaces = bound.Select(s => (s.Id, Points: s.Vertices.ToArray(), Triangles: s.Triangles.ToArray())).ToList();
        var sources = new Dictionary<Guid, Shape>();
        foreach (var s in bound)
            foreach (var id in s.SourceIds)
                if (!sources.ContainsKey(id) && project.FindEntity(id) is { } e) sources[id] = ShapeOf(e);
        return () =>
        {
            foreach (var (id, before, triangles) in surfaces)
                if (project.FindEntity(id) is SurfaceEntity s) Keep(project, s, before, triangles, sources);
        };
    }

    private static void Keep(BlastProject project, SurfaceEntity s, Vec3[] before, int[] triangles, Dictionary<Guid, Shape> was)
    {
        if (s.SourceIds.Count == 0) return;
        var present = s.SourceIds.Select(id => project.FindEntity(id)).OfType<Entity>().ToList();
        var deleted = present.Count < s.SourceIds.Count;
        var moved = !System.Runtime.InteropServices.CollectionsMarshal.AsSpan(s.Vertices).SequenceEqual(before);
        if (s.Vertices.Count != before.Length || !System.Runtime.InteropServices.CollectionsMarshal.AsSpan(s.Triangles).SequenceEqual(triangles))
        {
            // reshaped by a command on the surface itself: it is not the one its sources make any more
            s.SourceIds = new();
            return;
        }
        // the sources that changed in this change themselves (dragged, edited, moved together with the surface)
        var changed = present.Where(e => was.TryGetValue(e.Id, out var b) && !Same(b, ShapeOf(e))).ToHashSet();
        var rebuild = deleted;
        if (moved)
        {
            var map = PointMap(before, s.Vertices);
            foreach (var e in present)
            {
                if (!was.TryGetValue(e.Id, out var b)) continue;
                if (changed.Contains(e))
                {
                    // moved along with the surface it is in place already; otherwise its own change counts
                    if (!SameWithin(b.Points.Select(map).ToArray(), e.GetVertices().ToArray())) rebuild = true;
                    continue;
                }
                e.Transform(map);
            }
        }
        else if (changed.Count > 0) rebuild = true;
        if (!rebuild) return;
        var built = present.Count > 0 ? Surfaces.FromEntities(present, s.SourceBreaklines) : null;
        if (built == null || built.TriangleCount == 0)
        {
            project.Entities.Remove(s);
            return;
        }
        s.Vertices = built.Vertices;
        s.Triangles = built.Triangles;
        s.SourceIds = present.Select(e => e.Id).ToList();
        s.InvalidateIndex();
    }

    /// <summary>
    /// Where a point goes when the surface goes from <paramref name="before"/> to <paramref name="after"/> (the same
    /// vertices, moved): a point on a vertex goes with it, any other point as far as the nearest vertex went.
    /// </summary>
    private static Func<Vec3, Vec3> PointMap(Vec3[] before, IReadOnlyList<Vec3> after)
    {
        static (long, long, long) Key(Vec3 p) => ((long)Math.Round(p.X * 1e6), (long)Math.Round(p.Y * 1e6), (long)Math.Round(p.Z * 1e6));
        var index = new Dictionary<(long, long, long), int>();
        for (int i = 0; i < before.Length; i++) index.TryAdd(Key(before[i]), i);
        return p =>
        {
            if (index.TryGetValue(Key(p), out var i)) return after[i];
            int best = 0;
            double bestD = double.MaxValue;
            for (int k = 0; k < before.Length; k++)
            {
                var d = Vec3.Distance(before[k], p);
                if (d < bestD) { bestD = d; best = k; }
            }
            return p + (after[best] - before[best]);
        };
    }

    private static bool SameWithin(Vec3[] a, Vec3[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Vec3.Distance(a[i], b[i]) > 1e-6) return false;
        return true;
    }
}
