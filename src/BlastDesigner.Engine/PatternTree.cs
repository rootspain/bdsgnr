using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// Patterns cut into segments: a segment is a pattern of its own with a parent (<see cref="HolePattern.ParentId"/>), kept
/// in the same flat list. The parent holds the outline and the parameters by default; a segment takes them unless it sets
/// them itself (<see cref="HolePattern.Overrides"/>), and holds its part of the outline, its grid and its holes.
/// </summary>
public static partial class PatternTree
{
    /// <summary>The segments of a pattern, from the face back, its islands after them.</summary>
    public static List<HolePattern> Children(BlastProject p, HolePattern pat) =>
        p.Patterns.Where(s => s.ParentId == pat.Id).OrderBy(s => s.Order).ToList();

    /// <summary>The segments of a pattern between its cut lines (not its islands), from the face back: segment k between lines k − 1 and k.</summary>
    public static List<HolePattern> LineSegments(BlastProject p, HolePattern pat) =>
        Children(p, pat).Where(s => !s.IsIsland).ToList();

    /// <summary>
    /// Whether a point is in the part of a segment, without the edge offset: inside the outline of the parent, on the kept
    /// side of each cut, inside its island polygon (an island) and out of the islands taken out of it.
    /// </summary>
    public static bool InPart(HolePattern seg, Vec3 pt)
    {
        var outline = seg.EdgeOutline is { Count: >= 3 } edge ? edge : seg.Outline;
        if (outline.Count < 3 || !GeoMath.PointInPolygon(outline, pt.X, pt.Y)) return false;
        foreach (var c in seg.Cuts)
            if (!c.Keeps(pt.X, pt.Y)) return false;
        if (seg.IslandOutline is { Count: >= 3 } island && !GeoMath.PointInPolygon(island, pt.X, pt.Y)) return false;
        foreach (var x in seg.Excluded)
            if (x.Count >= 3 && GeoMath.PointInPolygon(x, pt.X, pt.Y)) return false;
        return true;
    }

    /// <summary>The parent of a segment; null for a pattern of its own (or one whose parent is gone).</summary>
    public static HolePattern? Parent(BlastProject p, HolePattern pat) => pat.ParentId is Guid id ? p.FindPattern(id) : null;

    /// <summary>The pattern at the top of the tree a pattern is in.</summary>
    public static HolePattern Root(BlastProject p, HolePattern pat)
    {
        var seen = new HashSet<Guid>();
        while (Parent(p, pat) is { } up && seen.Add(pat.Id)) pat = up;
        return pat;
    }

    /// <summary>The patterns under a pattern, all levels down, each parent before its segments.</summary>
    public static List<HolePattern> Descendants(BlastProject p, HolePattern pat)
    {
        var list = new List<HolePattern>();
        void Walk(HolePattern x)
        {
            foreach (var c in Children(p, x))
                if (!list.Contains(c)) { list.Add(c); Walk(c); }
        }
        Walk(pat);
        return list;
    }

    /// <summary>The patterns under a pattern that hold holes (it itself when it is not cut).</summary>
    public static List<HolePattern> Leaves(BlastProject p, HolePattern pat)
    {
        var all = new List<HolePattern> { pat };
        all.AddRange(Descendants(p, pat));
        return all.Where(x => !x.IsContainer).ToList();
    }

    /// <summary>The holes of a pattern and of all its segments.</summary>
    public static IEnumerable<Hole> TreeHoles(BlastProject p, HolePattern pat)
    {
        var ids = new HashSet<Guid> { pat.Id };
        foreach (var d in Descendants(p, pat)) ids.Add(d.Id);
        return p.Holes.Where(h => h.PatternId is Guid id && ids.Contains(id));
    }

    /// <summary>
    /// Takes the values down from the root to the segments: each segment gets those of its parent where it sets none.
    /// Called before every layout of the tree. Gives the segments whose grid (burden, spacing, type, direction, alignment,
    /// edge offset) changed, which have to be anchored again.
    /// </summary>
    public static List<HolePattern> Resolve(BlastProject p, HolePattern root)
    {
        var changed = new List<HolePattern>();
        foreach (var s in Descendants(p, root))
            if (Parent(p, s) is { } parent && Inherit(parent, s)) changed.Add(s);
        return changed;
    }

    /// <summary>The values of a segment from its parent and its own overrides; whether its grid changed.</summary>
    public static bool Inherit(HolePattern parent, HolePattern s)
    {
        var o = s.Overrides;
        var grid = (s.Burden, s.Spacing, s.Type, s.Azimuth, s.Alignment, s.EdgeOffset);
        s.Burden = o.Burden ?? parent.Burden;
        s.Spacing = o.Spacing ?? parent.Spacing;
        s.Type = o.Type ?? parent.Type;
        s.Azimuth = o.Azimuth ?? parent.Azimuth;
        s.Alignment = o.Alignment ?? parent.Alignment;
        s.EdgeOffset = o.EdgeOffset ?? parent.EdgeOffset;
        s.InitialAzimuth = o.Azimuth != null ? s.InitialAzimuth : parent.InitialAzimuth;
        s.RotateOrientation = parent.RotateOrientation;
        s.KeepLabels = parent.KeepLabels;
        s.LabelPrefix = parent.LabelPrefix;
        s.StartNumber = parent.StartNumber;
        s.Center = s.Center.WithZ(parent.Center.Z);
        var pd = parent.HoleDefaults;
        var d = s.HoleDefaults;
        d.Diameter = o.Diameter ?? pd.Diameter;
        d.Angle = o.Angle ?? pd.Angle;
        d.Bearing = o.Bearing ?? pd.Bearing;
        d.Subdrill = o.Subdrill ?? pd.Subdrill;
        d.BenchHeight = o.BenchHeight ?? pd.BenchHeight;
        d.HoleType = o.HoleType ?? pd.HoleType;
        d.Dummy = pd.Dummy;
        if (o.Depth != null)
        {
            d.Depth = o.Depth.Clone();
            SyncLegacyDepth(s);
        }
        else
        {
            d.Depth = pd.Depth.Clone();
            s.ToLevel = parent.ToLevel;
            s.ToSurface = parent.ToSurface;
        }
        return grid != (s.Burden, s.Spacing, s.Type, s.Azimuth, s.Alignment, s.EdgeOffset);
    }

    /// <summary>Whether a hole is one the seam rule of its pattern added in a gap (it has no node).</summary>
    public static bool IsSeamHole(BlastProject p, Hole h) =>
        p.FindPattern(h.PatternId) is { } pat && Root(p, pat).SeamAdded.Contains(h.Id);

    /// <summary>
    /// Moves or turns a pattern cut into segments with all its holes (<see cref="HoleDesign.FollowPatterns"/>): its outline
    /// and centre, those of its segments, the sides of their cuts and their directions; the cut lines go along, being in the
    /// axes of its grid.
    /// </summary>
    public static void Transform(BlastProject p, HolePattern root, Func<Vec3, Vec3> f, double turn)
    {
        List<Vec3> Moved(List<Vec3> pts) => pts.Select(x => f(x).WithZ(x.Z)).ToList();
        foreach (var pat in new[] { root }.Concat(Descendants(p, root)))
        {
            pat.Outline = Moved(pat.Outline);
            pat.OutlineParts = pat.OutlineParts.Select(Moved).ToList();
            pat.Excluded = pat.Excluded.Select(Moved).ToList();
            if (pat.IslandOutline != null) pat.IslandOutline = Moved(pat.IslandOutline);
            if (pat.EdgeOutline != null) pat.EdgeOutline = Moved(pat.EdgeOutline);
            foreach (var c in pat.Cuts)
            {
                var at = f(new Vec3(c.X, c.Y, 0));
                var tip = f(new Vec3(c.X + c.NX, c.Y + c.NY, 0));
                (c.X, c.Y, c.NX, c.NY) = (at.X, at.Y, tip.X - at.X, tip.Y - at.Y);
            }
            pat.Center = f(pat.Center).WithZ(pat.Center.Z);
            pat.Azimuth = GeoMath.NormalizeBearing(pat.Azimuth + turn);
            pat.InitialAzimuth = GeoMath.NormalizeBearing(pat.InitialAzimuth + turn);
            if (pat.Overrides.Azimuth is double az) pat.Overrides.Azimuth = GeoMath.NormalizeBearing(az + turn);
        }
    }

    /// <summary>
    /// The depth mode of a pattern in effect: a rectangle mode as set, else the one its older fields give (to the surface,
    /// to a level, or one depth), which may have been set by code that knows only them.
    /// </summary>
    public static DepthMode EffectiveDepthMode(HolePattern pat) =>
        pat.HoleDefaults.Depth.IsOwnMode ? pat.HoleDefaults.Depth.Mode
        : pat.ToSurface ? DepthMode.ToSurface
        : pat.ToLevel != null ? DepthMode.ToLevel
        : DepthMode.Fixed;

    /// <summary>The older fields of a pattern (to level, to the surface) from the mode of its depth, so a version that reads only them gets the same.</summary>
    public static void SyncLegacyDepth(HolePattern pat)
    {
        var d = pat.HoleDefaults.Depth;
        switch (d.Mode)
        {
            case DepthMode.ToSurface: pat.ToSurface = true; break;
            case DepthMode.ToLevel: pat.ToSurface = false; pat.ToLevel = d.Level; break;
            case DepthMode.Fixed: pat.ToSurface = false; pat.ToLevel = null; break;
            // the holes carry their own levels (Hole.ToLevel), which is what an older version reads
            default: pat.ToSurface = false; pat.ToLevel = null; break;
        }
    }

    /// <summary>The depth mode of a pattern taken from its older fields (files before schema 4).</summary>
    public static void DepthFromLegacy(HolePattern pat)
    {
        var d = pat.HoleDefaults.Depth;
        d.Mode = pat.ToSurface ? DepthMode.ToSurface : pat.ToLevel != null ? DepthMode.ToLevel : DepthMode.Fixed;
        if (pat.ToLevel is double z) d.Level = z;
        d.Front = new DepthPair(pat.HoleDefaults.BenchHeight, pat.HoleDefaults.BenchHeight);
        d.Back = new DepthPair(pat.HoleDefaults.BenchHeight, pat.HoleDefaults.BenchHeight);
    }
}

/// <summary>
/// The conditional rectangle of a pattern: the box of its nodes in the axes of its grid (u along the rows, v from the face
/// back), from which a node gets its relative position for the depth (<see cref="DepthSpec.At"/>).
/// </summary>
public readonly record struct DepthRectangle(double MinU, double MaxU, double MinV, double MaxV)
{
    /// <summary>The rectangle of the nodes of a pattern inside its outline; null when it has none.</summary>
    public static DepthRectangle? Of(HolePattern pat)
    {
        var nodes = HoleDesign.PatternNodes(pat);
        if (nodes.Count == 0) return null;
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        foreach (var n in nodes)
        {
            var (a, b) = HoleDesign.NodeGrid(pat, n.I, n.J);
            minU = Math.Min(minU, a); maxU = Math.Max(maxU, a);
            minV = Math.Min(minV, b); maxV = Math.Max(maxV, b);
        }
        return new DepthRectangle(minU, maxU, minV, maxV);
    }

    /// <summary>Relative position of node (i, j): s along the row from its left end, t from the first row to the last.</summary>
    public (double S, double T) Relative(HolePattern pat, int i, int j)
    {
        var (a, b) = HoleDesign.NodeGrid(pat, i, j);
        var s = MaxU - MinU > 1e-9 ? Math.Clamp((a - MinU) / (MaxU - MinU), 0, 1) : 0;
        var t = MaxV - MinV > 1e-9 ? Math.Clamp((b - MinV) / (MaxV - MinV), 0, 1) : 0;
        return (s, t);
    }

    /// <summary>The corners in plan: front left, front right, back right, back left.</summary>
    public Vec3[] Corners(HolePattern pat)
    {
        var (u, v) = HoleDesign.PatternAxes(pat.Azimuth);
        Vec3 P(double a, double b) => new(pat.Center.X + u.X * a + v.X * b, pat.Center.Y + u.Y * a + v.Y * b, pat.Center.Z);
        return new[] { P(MinU, MinV), P(MaxU, MinV), P(MaxU, MaxV), P(MinU, MaxV) };
    }
}
