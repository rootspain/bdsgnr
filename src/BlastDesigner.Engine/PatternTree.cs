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
    /// <summary>The segments of a pattern, from the face back.</summary>
    public static List<HolePattern> Children(BlastProject p, HolePattern pat) =>
        p.Patterns.Where(s => s.ParentId == pat.Id).OrderBy(s => s.Order).ToList();

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

    /// <summary>
    /// The depth mode of a pattern in effect: a rectangle mode as set, else the one its older fields give (to the surface,
    /// to a level, or one depth), which may have been set by code that knows only them.
    /// </summary>
    public static DepthMode EffectiveDepthMode(HolePattern pat) =>
        pat.HoleDefaults.Depth.UsesRectangle ? pat.HoleDefaults.Depth.Mode
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
