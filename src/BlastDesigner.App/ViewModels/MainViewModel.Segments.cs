using System.Text.Json;
using BlastDesigner.App.Services;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.ViewModels;

/// <summary>
/// Patterns cut into segments (<see cref="PatternTree"/>): the number of segments, the cut lines, what a segment sets of its
/// own and the depth of a pattern or a segment. Each change is one undo step that lays the whole tree out again.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Cuts the pattern (of a segment, its parent) into <paramref name="count"/> segments; 1 takes them away. Asks before
    /// segments that set parameters of their own go. False when nothing was done.
    /// </summary>
    public async Task<bool> SetSegmentCount(Guid patternId, int count)
    {
        if (Project?.FindPattern(patternId) is not { } pat) return false;
        var root = PatternTree.Root(Project, pat);
        count = Math.Clamp(count, 1, PatternTree.MaxSegments(root));
        var now = PatternTree.SegmentCount(root);
        if (count == now) return false;
        if (count < now)
        {
            var going = PatternTree.LineSegments(Project, root).Where(s => (count == 1 || s.Order >= count) && !s.Overrides.IsEmpty).Select(s => s.Name).ToList();
            if (going.Count > 0 && !await Dialogs.Confirm(Loc.F("Segments {0} set parameters of their own, which will be lost. Continue?", string.Join(", ", going)), Loc.T("Segments")))
                return false;
        }
        return await LayOutTree(root.Id, $"{root.Name}: {Loc.T("segments")}", (p, r, o) => PatternTree.Split(p, r, count, o));
    }

    /// <summary>Sets what a segment makes its holes with, or its grid, and lays the tree out again; <paramref name="what"/> names the step.</summary>
    public Task<bool> SetSegmentOverride(Guid segmentId, string what, Action<PatternOverrides> set)
    {
        if (Project?.FindPattern(segmentId) is not { ParentId: not null } seg) return Task.FromResult(false);
        // a field taken away with the panel gives its value again: a change that changes nothing is no step
        var probe = seg.Overrides.Clone();
        set(probe);
        if (Same(probe, seg.Overrides)) return Task.FromResult(false);
        return LayOutTree(PatternTree.Root(Project, seg).Id, $"{seg.Name}: {Loc.T(what)}", (p, r, o) =>
        {
            if (p.FindPattern(segmentId) is { } s) set(s.Overrides);
            return PatternTree.Relayout(p, r, o);
        }, seg);
    }

    /// <summary>The segment takes everything from its parent again.</summary>
    public Task<bool> ResetSegment(Guid segmentId)
    {
        if (Project?.FindPattern(segmentId) is not { ParentId: not null } seg) return Task.FromResult(false);
        return LayOutTree(PatternTree.Root(Project, seg).Id, $"{seg.Name}: {Loc.T("as the parent")}", (p, r, o) =>
        {
            if (p.FindPattern(segmentId) is { } s) { s.Overrides = new PatternOverrides(); s.AnchorSide = PatternAnchorSide.Face; }
            return PatternTree.Relayout(p, r, o);
        }, seg);
    }

    /// <summary>
    /// Sets a value of a pattern cut into segments (or of one that is not): the segments that do not set it themselves take
    /// it, their holes too, in one undo step.
    /// </summary>
    public Task<bool> SetPatternDefaults(Guid patternId, string what, Action<HolePattern> set)
    {
        if (Project?.FindPattern(patternId) is not { } pat) return Task.FromResult(false);
        var probe = ProjectJson.DeepClone(pat);
        set(probe);
        if (Same(probe, pat)) return Task.FromResult(false);
        var root = PatternTree.Root(Project, pat);
        return LayOutTree(root.Id, $"{pat.Name}: {Loc.T(what)}", (p, r, o) =>
        {
            if (p.FindPattern(patternId) is { } x) set(x);
            return PatternTree.Relayout(p, r, o);
        }, pat);
    }

    /// <summary>
    /// The depth of a pattern or of a segment (as its own; null: the parent's again). Holes whose depth mode changes leave
    /// what the old one gave them: the level of the rectangle, or the one depth.
    /// </summary>
    public Task<bool> SetPatternDepth(Guid patternId, DepthSpec? depth) =>
        ChangeDepth(patternId, x =>
        {
            if (x.ParentId != null) x.Overrides.Depth = depth?.Clone();
            else if (depth != null)
            {
                x.HoleDefaults.Depth = depth.Clone();
                PatternTree.SyncLegacyDepth(x);
            }
        });

    /// <summary>The one depth of a pattern or a segment (its bench height): the holes drilled to it take the new length.</summary>
    public Task<bool> SetPatternBench(Guid patternId, double bench) =>
        ChangeDepth(patternId, x =>
        {
            if (x.ParentId != null) x.Overrides.BenchHeight = bench;
            else x.HoleDefaults.BenchHeight = bench;
        }, always: true);

    /// <summary>A change of the depth of a pattern or a segment, in one undo step, the holes of the leaves whose depth changed following it.</summary>
    private Task<bool> ChangeDepth(Guid patternId, Action<HolePattern> set, bool always = false)
    {
        if (Project?.FindPattern(patternId) is not { } pat) return Task.FromResult(false);
        var probe = ProjectJson.DeepClone(pat);
        set(probe);
        if (Same(probe, pat)) return Task.FromResult(false);
        var root = PatternTree.Root(Project, pat);
        return LayOutTree(root.Id, $"{pat.Name}: {Loc.T("depth")}", (p, r, o) =>
        {
            if (p.FindPattern(patternId) is not { } x) return new PatternRebuildReport();
            var leaves = PatternTree.Leaves(p, r);
            var before = leaves.ToDictionary(l => l.Id, l => (PatternTree.EffectiveDepthMode(l), l.ToLevel, l.HoleDefaults.BenchHeight, l.HoleDefaults.Depth.Collars));
            var depths = leaves.ToDictionary(l => l.Id, l => l.HoleDefaults.Depth.Clone());
            set(x);
            PatternTree.Resolve(p, r);
            // a new depth of a segment is taken by all its holes, those edited by hand too
            foreach (var leaf in leaves.Where(l => !depths[l.Id].SameAs(l.HoleDefaults.Depth)))
                foreach (var h in HoleDesign.PatternHoles(p, leaf)) h.DepthManual = false;
            foreach (var leaf in leaves)
                if (before[leaf.Id] != (PatternTree.EffectiveDepthMode(leaf), leaf.ToLevel, leaf.HoleDefaults.BenchHeight, leaf.HoleDefaults.Depth.Collars)
                    && (always || before[leaf.Id].Item1 != PatternTree.EffectiveDepthMode(leaf) || before[leaf.Id].Item2 != leaf.ToLevel || before[leaf.Id].Item4 != leaf.HoleDefaults.Depth.Collars))
                    PatternTree.FollowDepthMode(p, leaf, o);
            return PatternTree.Relayout(p, r, o);
        }, pat);
    }

    /// <summary>Whether two objects of the project are the same, as they are saved.</summary>
    private static bool Same<T>(T a, T b) => JsonSerializer.Serialize(a, ProjectJson.Options) == JsonSerializer.Serialize(b, ProjectJson.Options);

    /// <summary>
    /// Sets elevations of vertices of the collar (or depth) polygon of a pattern, by the names of the vertices, and lays the
    /// pattern out again: one undo step. A vertex shared by two segments is changed for both.
    /// </summary>
    public Task<bool> SetPolygonVertices(Guid patternId, bool collar, IReadOnlyDictionary<string, double> values)
    {
        if (Project?.FindPattern(patternId) is not { } pat || values.Count == 0) return Task.FromResult(false);
        var root = PatternTree.Root(Project, pat);
        return LayOutTree(root.Id, $"{root.Name}: {Loc.T(collar ? "collar polygon" : "depth polygon")}", (p, r, o) =>
        {
            foreach (var (key, z) in values) PatternSurfaces.SetVertex(r, key, collar ? z : null, collar ? null : z);
            return PatternTree.Relayout(p, r, o);
        });
    }

    /// <summary>A vertex of the collar (or depth) polygon takes its elevation from the vertices beside it again.</summary>
    public Task<bool> ClearPolygonVertex(Guid patternId, bool collar, string key)
    {
        if (Project?.FindPattern(patternId) is not { } pat) return Task.FromResult(false);
        var root = PatternTree.Root(Project, pat);
        if (root.SurfaceVertices.FirstOrDefault(v => v.Key == key) is not { } own || (collar ? own.Collar : own.Depth) == null) return Task.FromResult(false);
        return LayOutTree(root.Id, $"{root.Name}: {Loc.T(collar ? "collar polygon" : "depth polygon")}", (p, r, o) =>
        {
            if (r.SurfaceVertices.FirstOrDefault(v => v.Key == key) is { } x)
            {
                if (collar) x.Collar = null; else x.Depth = null;
                if (x.Collar == null && x.Depth == null) r.SurfaceVertices.Remove(x);
            }
            return PatternTree.Relayout(p, r, o);
        });
    }

    /// <summary>Puts cut line <paramref name="index"/> of a pattern where it was dragged to (one undo step).</summary>
    public Task<bool> MoveSegmentLine(Guid rootId, int index, SegmentLine line)
    {
        if (Project?.FindPattern(rootId) is not { } root || index < 0 || index >= root.SegmentLines.Count) return Task.FromResult(false);
        return LayOutTree(rootId, $"{root.Name}: {Loc.T("segment line")}", (p, r, o) =>
        {
            r.SegmentLines[index] = line.Clone();
            return PatternTree.Relayout(p, r, o);
        });
    }

    /// <summary>Turns cut line <paramref name="index"/> back along the rows, in the middle between the two rows nearest to it.</summary>
    public Task<bool> StraightenSegmentLine(Guid rootId, int index)
    {
        if (Project?.FindPattern(rootId) is not { } root || index < 0 || index >= root.SegmentLines.Count) return Task.FromResult(false);
        var line = PatternTree.Straightened(root, root.SegmentLines[index]);
        return MoveSegmentLine(rootId, index, line);
    }

    /// <summary>Takes cut line <paramref name="index"/> away: the two segments beside it become one, with the parameters of the front one.</summary>
    public async Task<bool> RemoveSegmentLine(Guid rootId, int index)
    {
        if (Project?.FindPattern(rootId) is not { } root || index < 0 || index >= root.SegmentLines.Count) return false;
        var behind = PatternTree.Children(Project, root).FirstOrDefault(s => s.Order == index + 1);
        if (behind != null && !behind.Overrides.IsEmpty
            && !await Dialogs.Confirm(Loc.F("Segments {0} set parameters of their own, which will be lost. Continue?", behind.Name), Loc.T("Segments")))
            return false;
        return await LayOutTree(rootId, $"{root.Name}: {Loc.T("segments")}", (p, r, o) => PatternTree.RemoveLine(p, r, index, o));
    }

    /// <summary>
    /// Cuts an island out of the pattern of the selected holes by the selected closed polyline inside it (Pattern ▸ Island
    /// from polygon); the island is a segment of its own.
    /// </summary>
    public async Task AddIslandFromSelection()
    {
        if (SelectedPattern is not { } root || Doc == null) { await Dialogs.Info(Loc.T("Select a hole of a pattern first.")); return; }
        var polygon = Doc.SelectedEntities.OfType<PolylineEntity>().FirstOrDefault(l => l.Closed && l.Points.Count >= 3 && l.Id != root.PolygonId);
        if (polygon == null || !polygon.Points.Any(q => GeoMath.PointInPolygon(root.Outline, q.X, q.Y)))
        {
            await Dialogs.Info(Loc.T("Select a closed polyline inside the pattern together with a hole of it."), Loc.T("Island from polygon"));
            return;
        }
        var points = polygon.Points.ToList();
        var id = polygon.Id;
        await LayOutTree(root.Id, $"{root.Name}: {Loc.T("island")}", (p, r, o) => PatternTree.AddIsland(p, r, points, id, o));
    }

    /// <summary>Takes an island away: its holes go to the segments around it.</summary>
    public async Task<bool> RemoveIsland(Guid islandId)
    {
        if (Project?.FindPattern(islandId) is not { IsIsland: true } island || PatternTree.Parent(Project, island) is not { } root) return false;
        if (!island.Overrides.IsEmpty && !await Dialogs.Confirm(Loc.F("Segments {0} set parameters of their own, which will be lost. Continue?", island.Name), Loc.T("Segments"))) return false;
        return await LayOutTree(PatternTree.Root(Project, root).Id, $"{root.Name}: {Loc.T("island")}", (p, r, o) => PatternTree.RemoveIsland(p, r, islandId, o));
    }

    /// <summary>Selects the holes of one segment (a row of the list of segments).</summary>
    public void SelectSegmentHoles(Guid segmentId)
    {
        if (Project?.FindPattern(segmentId) is not { } seg) return;
        Doc!.Select(PatternTree.TreeHoles(Project, seg).Where(IsSelectable).Select(h => h.Id));
    }
}
