using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>How the bearing of the inclined holes of a pattern follows a turn of its grid.</summary>
public enum PatternBearingMode
{
    /// <summary>Turned by as much as the grid.</summary>
    Rotate,
    /// <summary>Kept as it is.</summary>
    Keep,
    /// <summary>Set to the blast orientation.</summary>
    BlastOrientation
}

public sealed class PatternRebuildOptions
{
    public PatternBearingMode Bearing { get; set; } = PatternBearingMode.Rotate;
    /// <summary>Turn of the grid the bearings of the existing holes follow in <see cref="PatternBearingMode.Rotate"/> (set by the turn).</summary>
    public double BearingDelta { get; set; }
    /// <summary>Collars are draped on it when set.</summary>
    public SurfaceEntity? CollarSurface { get; set; }
    /// <summary>Lengths reach it plus the subdrill when set (holes without a level).</summary>
    public SurfaceEntity? GradeSurface { get; set; }
    /// <summary>Layer of new holes; the holes layer when null.</summary>
    public Layer? Layer { get; set; }

    /// <summary>The options of a project: collars on its bench surface, lengths to its grade, new holes on the holes layer.</summary>
    public static PatternRebuildOptions For(BlastProject p, PatternBearingMode bearing = PatternBearingMode.Rotate) => new()
    {
        Bearing = bearing,
        CollarSurface = p.Entities.OfType<SurfaceEntity>().FirstOrDefault(s => s.Kind == SurfaceKind.Bench),
        GradeSurface = p.Entities.OfType<SurfaceEntity>().FirstOrDefault(s => s.Kind == SurfaceKind.Grade),
        Layer = p.GetOrCreateLayer(StandardLayers.Holes, 0xFF1E3A8A, "Design")
    };
}

/// <summary>Why a pattern cannot be laid out again: an English format with the label of the hole as {0}.</summary>
public sealed record PatternRefusal(string Reason, string HoleLabel);

public sealed class PatternRebuildReport
{
    public int Moved { get; set; }
    public int Deleted { get; set; }
    public int Added { get; set; }
    public int TiesLost { get; set; }
    public List<Guid> AddedIds { get; } = new();
    public List<Guid> DeletedIds { get; } = new();
    /// <summary>Holes whose depth the range of their pattern held at one of its ends (<see cref="DepthMode.Range"/>).</summary>
    public List<Guid> ClampedIds { get; } = new();
    /// <summary>Set when nothing was done.</summary>
    public PatternRefusal? Refusal { get; set; }
}

/// <summary>A node of a pattern grid inside its outline.</summary>
public readonly record struct PatternNode(int I, int J, Vec3 Point);

/// <summary>What laying a pattern out again does: holes that go to their node, holes whose node left the outline, new nodes.</summary>
public sealed class PatternPlan
{
    public List<(Hole Hole, PatternNode Node, Vec3 Collar)> Moves { get; } = new();
    public List<Hole> Deletes { get; } = new();
    public List<PatternNode> Adds { get; } = new();
}

public static partial class HoleDesign
{
    /// <summary>A node closer than this to the outline is on it, and is left out whichever side the rounding puts it.</summary>
    private const double OnOutline = 1e-6;

    /// <summary>Axes of a grid whose burden points along <paramref name="azimuth"/>: u along the rows, v against the movement.</summary>
    public static (Vec3 U, Vec3 V) PatternAxes(double azimuth)
    {
        var move = GeoMath.HoleDirection(azimuth, 90);
        return (new Vec3(move.Y, -move.X, 0), new Vec3(-move.X, -move.Y, 0));
    }

    /// <summary>Position of node (i, j) of the grid, at the collar level of the pattern.</summary>
    public static Vec3 NodePoint(HolePattern pat, int i, int j)
    {
        var (u, v) = PatternAxes(pat.Azimuth);
        var (a, b) = NodeGrid(pat, i, j);
        return new Vec3(pat.Center.X + u.X * a + v.X * b, pat.Center.Y + u.Y * a + v.Y * b, pat.Center.Z);
    }

    /// <summary>Position of node (i, j) in the axes of the grid about its centre: along the rows (u) and from the face back (v).</summary>
    public static (double U, double V) NodeGrid(HolePattern pat, int i, int j)
    {
        var stagger = pat.Type == PatternType.Staggered && (j & 1) == 1 ? pat.Spacing / 2 : 0;
        return (pat.PhaseU + i * pat.Spacing + stagger, pat.PhaseV + j * pat.Burden);
    }

    /// <summary>Whether a point gets a hole: inside the outline and not nearer to it than the edge offset.</summary>
    public static bool NodeInside(HolePattern pat, Vec3 pt)
    {
        if (pat.EdgeOutline is { Count: >= 3 } edge)
        {
            // a segment: inside the outline of its parent (the edge offset from it, not from the cuts) and on its side of each cut
            if (pat.Outline.Count < 3 || !PatternTree.InPart(pat, pt)) return false;
            var de = GeoMath.DistanceToPolyline2D(edge, pt, true);
            return de > OnOutline && (pat.EdgeOffset <= 0 || de >= pat.EdgeOffset);
        }
        if (!GeoMath.PointInPolygon(pat.Outline, pt.X, pt.Y)) return false;
        var d = GeoMath.DistanceToPolyline2D(pat.Outline, pt, true);
        return d > OnOutline && (pat.EdgeOffset <= 0 || d >= pat.EdgeOffset);
    }

    /// <summary>The nodes of the grid inside its outline (suppressed ones too), row by row, along each row.</summary>
    public static List<PatternNode> PatternNodes(HolePattern pat)
    {
        var nodes = new List<PatternNode>();
        if (pat.Outline.Count < 3 || pat.Burden <= 0 || pat.Spacing <= 0) return nodes;
        var r = (pat.EdgeOutline is { Count: >= 3 } edge ? edge : pat.Outline).Max(p => Vec3.Distance2D(p, pat.Center));
        long i0 = (long)Math.Floor((-r - pat.Spacing - pat.PhaseU) / pat.Spacing), i1 = (long)Math.Ceiling((r - pat.PhaseU) / pat.Spacing);
        long j0 = (long)Math.Floor((-r - pat.PhaseV) / pat.Burden), j1 = (long)Math.Ceiling((r - pat.PhaseV) / pat.Burden);
        if ((i1 - i0 + 1) * (j1 - j0 + 1) > 4_000_000) throw new InvalidOperationException("The pattern is too dense for its outline.");
        for (var j = (int)j0; j <= j1; j++)
            for (var i = (int)i0; i <= i1; i++)
            {
                var pt = NodePoint(pat, i, j);
                if (NodeInside(pat, pt)) nodes.Add(new PatternNode(i, j, pt));
            }
        return nodes;
    }

    /// <summary>The outline of a pattern: the points of the polygon, all at one elevation.</summary>
    private static List<Vec3> FlatOutline(IReadOnlyList<Vec3> polygon, double z) => polygon.Select(p => p.WithZ(z)).ToList();

    /// <summary>Centre of the area of a polygon, worked out from its first point (map coordinates are large); Z is the mean.</summary>
    internal static Vec3 AreaCentre(IReadOnlyList<Vec3> polygon)
    {
        var o = polygon[0];
        var c = GeoMath.Centroid(polygon.Select(p => new Vec3(p.X - o.X, p.Y - o.Y, p.Z)).ToList());
        return new Vec3(c.X + o.X, c.Y + o.Y, polygon.Average(p => p.Z));
    }

    /// <summary>
    /// The grid of a new pattern: the first row half a burden from the face side of the outline, the first hole of a row half
    /// a spacing from its start, as the pattern tool has always laid them; row j = 0 is the face row. The outline lies at one
    /// elevation, that of the centre (the mean of the points of the polygon).
    /// </summary>
    public static HolePattern CreatePattern(IReadOnlyList<Vec3> polygon, PatternParameters pp, HoleParameters hp)
    {
        var az = GeoMath.NormalizeBearing(pp.Orientation);
        var (u, v) = PatternAxes(az);
        var origin = polygon[0];
        double minU = double.MaxValue, minV = double.MaxValue;
        foreach (var p in polygon)
        {
            var d = p - origin;
            minU = Math.Min(minU, d.X * u.X + d.Y * u.Y);
            minV = Math.Min(minV, d.X * v.X + d.Y * v.Y);
        }
        var c = AreaCentre(polygon);
        var dc = c - origin;
        var pattern = new HolePattern
        {
            Outline = FlatOutline(polygon, c.Z),
            Center = c,
            Azimuth = az,
            InitialAzimuth = az,
            PhaseU = minU + pp.Spacing / 2 - (dc.X * u.X + dc.Y * u.Y),
            PhaseV = minV + pp.Burden / 2 - (dc.X * v.X + dc.Y * v.Y),
            Burden = pp.Burden,
            Spacing = pp.Spacing,
            Type = pp.Pattern,
            EdgeOffset = pp.EdgeOffset,
            LabelPrefix = pp.LabelPrefix,
            StartNumber = pp.StartNumber,
            RotateOrientation = pp.RotateOrientation,
            HoleDefaults = new PatternHoleDefaults
            {
                Diameter = hp.Diameter, Angle = hp.Angle, Bearing = hp.Bearing, Subdrill = hp.Subdrill, BenchHeight = hp.BenchHeight, HoleType = hp.HoleType, Dummy = hp.Dummy,
                Depth = new DepthSpec { Front = new DepthPair(hp.BenchHeight, hp.BenchHeight), Back = new DepthPair(hp.BenchHeight, hp.BenchHeight) }
            },
            FaceEdge = pp.FaceEdge is int k && k >= 0 && k < polygon.Count ? k : null,
            Alignment = pp.Alignment
        };
        if (pattern.FaceEdge != null || pattern.Alignment != RowAlignment.Left)
        {
            Anchor(pattern);
            pattern.InitialAzimuth = pattern.Azimuth;
        }
        return pattern;
    }

    /// <summary>The longest side of a polygon (from point k to point k + 1, the last back to the first).</summary>
    public static int LongestEdge(IReadOnlyList<Vec3> polygon)
    {
        int best = 0;
        double len = -1;
        for (int k = 0; k < polygon.Count; k++)
        {
            var d = Vec3.Distance2D(polygon[k], polygon[(k + 1) % polygon.Count]);
            if (d > len) (best, len) = (k, d);
        }
        return best;
    }

    /// <summary>Bearing straight out of a polygon across its side k (from point k to point k + 1), degrees from north.</summary>
    public static double OutwardBearing(IReadOnlyList<Vec3> polygon, int k)
    {
        var a = polygon[k];
        var b = polygon[(k + 1) % polygon.Count];
        var o = polygon[0];
        double area = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var p = polygon[i] - o;
            var q = polygon[(i + 1) % polygon.Count] - o;
            area += p.X * q.Y - q.X * p.Y;
        }
        // anticlockwise (positive area) the outside is on the right of a side, clockwise on its left
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var (nx, ny) = area >= 0 ? (dy, -dx) : (-dy, dx);
        return GeoMath.NormalizeBearing(Math.Atan2(nx, ny) / GeoMath.Deg);
    }

    /// <summary>
    /// Lays the grid on its outline: with a face side the direction across that side, the first row half a burden inside it;
    /// else the first row half a burden from the face side of the outline. Along a row the holes start half a spacing from the
    /// left end of the outline, from its right end, or with the same room at both (<see cref="HolePattern.Alignment"/>).
    /// </summary>
    public static void Anchor(HolePattern pat) => Anchor(pat, true, true);

    /// <summary>
    /// <see cref="Anchor(HolePattern)"/> across the rows (where the first row goes), along them (where the first hole of a row
    /// goes), or both. A segment laid from its back side (<see cref="HolePattern.AnchorSide"/>) has its last row half a
    /// burden inside its back side instead.
    /// </summary>
    public static void Anchor(HolePattern pat, bool along, bool across)
    {
        if (pat.Outline.Count < 3 || pat.Burden <= 0 || pat.Spacing <= 0) return;
        if (pat.FaceEdge is int k && (k < 0 || k >= pat.Outline.Count)) pat.FaceEdge = null;
        if (pat.FaceEdge is int edge) pat.Azimuth = OutwardBearing(pat.Outline, edge);
        var (u, v) = PatternAxes(pat.Azimuth);
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        foreach (var q in pat.Outline.Concat(pat.OutlineParts.SelectMany(x => x)))
        {
            var d = q - pat.Center;
            var du = d.X * u.X + d.Y * u.Y;
            var dv = d.X * v.X + d.Y * v.Y;
            minU = Math.Min(minU, du);
            maxU = Math.Max(maxU, du);
            minV = Math.Min(minV, dv);
            maxV = Math.Max(maxV, dv);
        }
        if (pat.FaceEdge is int f)
        {
            // the line of the side, not the furthest point: a polygon bent out beyond it keeps the first row along the side
            var d = pat.Outline[f] - pat.Center;
            minV = d.X * v.X + d.Y * v.Y;
        }
        if (across)
        {
            if (pat.AnchorSide == PatternAnchorSide.Back && pat.ParentId != null)
            {
                // the last row half a burden inside the back side, row 0 still the one nearest the face
                var rows = Math.Floor(Math.Max(0, maxV - minV - pat.Burden) / pat.Burden + 1e-9);
                pat.PhaseV = maxV - pat.Burden / 2 - rows * pat.Burden;
            }
            else pat.PhaseV = minV + pat.Burden / 2;
        }
        if (!along) return;
        // between the holes half a spacing in from both ends: what is left over goes to the right, both sides or the left
        var span = maxU - minU - pat.Spacing;
        var count = span >= 0 ? Math.Floor(span / pat.Spacing + 1e-9) + 1 : 1;
        var spare = span - (count - 1) * pat.Spacing;
        var share = pat.Alignment switch { RowAlignment.Centre => 0.5, RowAlignment.Right => 1.0, _ => 0.0 };
        pat.PhaseU = minU + pat.Spacing / 2 + spare * share;
    }

    /// <summary>
    /// Lays a pattern out on its polygon again after the polygon was edited: on its face side the grid follows the side (its
    /// direction and first row), else it stays where it is and only the outline changes. Holes go to their nodes, those whose
    /// node left the outline are deleted, new nodes get holes, as <see cref="RebuildPattern"/>.
    /// </summary>
    public static PatternRebuildReport FitToOutline(BlastProject p, HolePattern pat, IReadOnlyList<Vec3> polygon, PatternRebuildOptions? o = null)
    {
        o ??= PatternRebuildOptions.For(p);
        if (RebuildRefusal(p, pat, o) is { } no) return new PatternRebuildReport { Refusal = no };
        // the elevations of the collar and depth polygons stay with their vertices
        PatternSurfaces.RemapOutline(pat, pat.Outline, polygon);
        pat.Outline = FlatOutline(polygon, pat.Center.Z);
        if (pat.FaceEdge != null || pat.Alignment != RowAlignment.Left)
        {
            if (pat.FaceEdge is int k && k >= polygon.Count) pat.FaceEdge = LongestEdge(polygon);
            var c = AreaCentre(polygon);
            pat.Center = c.WithZ(pat.Center.Z);
            var before = pat.Azimuth;
            Anchor(pat);
            // the holes turn with the side, as with a turn of the grid
            o.BearingDelta = PatternTurn.Turn(before, pat.Azimuth);
        }
        return RebuildPattern(p, pat, o);
    }

    /// <summary>Fills a polygon with a rectangular or staggered pattern and gives the grid it was laid out on.</summary>
    /// <param name="firstRow">The place of the first row of the pattern in the rows of the project: the rows are named by
    /// their letters from it (<see cref="HoleRow.Letter"/>), the holes by their row (<see cref="RowLabel"/>).</param>
    public static List<Hole> FillPolygon(IReadOnlyList<Vec3> polygon, PatternParameters pp, HoleParameters hp, out List<HoleRow> rows, out HolePattern pattern, int firstRow = 0)
    {
        rows = new List<HoleRow>();
        var holes = new List<Hole>();
        if (polygon.Count < 3 || pp.Burden <= 0 || pp.Spacing <= 0)
        {
            pattern = new HolePattern { Outline = FlatOutline(polygon, polygon.Count > 0 ? polygon.Average(p => p.Z) : 0), Burden = pp.Burden, Spacing = pp.Spacing, Type = pp.Pattern };
            return holes;
        }
        pattern = CreatePattern(polygon, pp, hp);
        // each row numbered on its own from the first number, after the prefix and the letter of the row
        int number = pp.StartNumber;
        HoleRow? row = null;
        foreach (var n in PatternNodes(pattern))
        {
            if (row == null || pattern.RowIds.GetValueOrDefault(n.J) != row.Id)
            {
                row = new HoleRow { Name = HoleRow.Letter(firstRow + rows.Count), Burden = pp.Burden, Spacing = pp.Spacing };
                rows.Add(row);
                pattern.RowIds[n.J] = row.Id;
                number = pp.StartNumber;
            }
            var h = CreateHole(n.Point, hp);
            h.Label = RowLabel(pp.LabelPrefix, row.Name, number++);
            h.RowId = row.Id;
            h.PatternId = pattern.Id;
            h.GridI = n.I;
            h.GridJ = n.J;
            row.HoleIds.Add(h.Id);
            holes.Add(h);
        }
        return holes;
    }

    public static IEnumerable<Hole> PatternHoles(BlastProject p, HolePattern pat) => p.Holes.Where(h => h.PatternId == pat.Id);

    /// <summary>Why the pattern cannot be laid out again (its holes are drilled, locked or on a closed layer); null when it can.</summary>
    public static PatternRefusal? RebuildRefusal(BlastProject p, HolePattern pat, PatternRebuildOptions? o = null)
    {
        foreach (var h in PatternHoles(p, pat))
        {
            if (h.State != HoleState.Designed) return new PatternRefusal(h.State switch
            {
                HoleState.Released => "Hole {0} is released for loading.",
                HoleState.Loaded => "Hole {0} is already loaded.",
                HoleState.Fired => "Hole {0} is already fired.",
                _ => "Hole {0} is already drilled."
            }, h.Label);
            if (h.Locked) return new PatternRefusal("Hole {0} is locked.", h.Label);
            if (h.ActualCollar != null) return new PatternRefusal("Hole {0} has a surveyed collar.", h.Label);
            if (p.FindLayer(h.LayerId) is { Locked: true } or { Frozen: true }) return new PatternRefusal("The layer of hole {0} is locked or frozen.", h.Label);
        }
        if (o?.Layer is { Locked: true } or { Frozen: true }) return new PatternRefusal("The holes layer is locked or frozen.", "");
        return null;
    }

    /// <summary>What laying the pattern out again would do, without doing it (the rotate tool shows it while dragging).</summary>
    public static PatternPlan PlanPattern(BlastProject p, HolePattern pat)
    {
        var plan = new PatternPlan();
        // nodes emptied by hand, and those the seam rule of a segment emptied
        var suppressed = pat.Suppressed.Concat(pat.SeamDropped).Select(s => (s.I, s.J)).ToHashSet();
        var free = new Dictionary<(int, int), PatternNode>();
        foreach (var n in PatternNodes(pat))
            if (!suppressed.Contains((n.I, n.J))) free[(n.I, n.J)] = n;
        var (u, v) = PatternAxes(pat.Azimuth);
        foreach (var h in PatternHoles(p, pat))
        {
            if (h.GridI is not int i || h.GridJ is not int j) continue;
            if (free.Remove((i, j), out var node))
            {
                var collar = node.Point;
                if (h.GridOffset is Vec3 o) collar += u * o.X + v * o.Y;
                plan.Moves.Add((h, node, collar.WithZ(h.Collar.Z)));
            }
            else plan.Deletes.Add(h);
        }
        plan.Adds.AddRange(free.Values.OrderBy(n => n.J).ThenBy(n => n.I));
        return plan;
    }

    /// <summary>Turns the grid by <paramref name="delta"/> degrees (clockwise) about its centre and lays it out again.</summary>
    public static PatternRebuildReport RotatePattern(BlastProject p, HolePattern pat, double delta, PatternRebuildOptions? o = null)
    {
        o ??= new PatternRebuildOptions();
        if (RebuildRefusal(p, pat, o) is { } no) return new PatternRebuildReport { Refusal = no };
        pat.Azimuth = GeoMath.NormalizeBearing(pat.Azimuth + delta);
        // turned by hand: the grid no longer goes along a side of its polygon
        pat.FaceEdge = null;
        if (pat.RotateOrientation) p.Header.BlastOrientation = GeoMath.NormalizeBearing(p.Header.BlastOrientation + delta);
        o.BearingDelta = delta;
        TurnExtraHoles(p, new[] { pat }, pat.Center, delta, o, bearings: true);
        return RebuildPattern(p, pat, o);
    }

    /// <summary>
    /// Gives the grid a new burden and spacing and lays it out again: the first row half the new burden from the face side of
    /// the outline, the first hole of a row half the new spacing from its start, as a new pattern on this outline and direction
    /// would have them. The rows take the new burden and spacing.
    /// </summary>
    public static PatternRebuildReport ResizePattern(BlastProject p, HolePattern pat, double burden, double spacing, PatternRebuildOptions? o = null)
    {
        o ??= new PatternRebuildOptions();
        if (RebuildRefusal(p, pat, o) is { } no) return new PatternRebuildReport { Refusal = no };
        if (burden <= 0 || spacing <= 0) throw new ArgumentException("The burden and the spacing must be above zero.");
        Resize(pat, burden, spacing);
        return RebuildPattern(p, pat, o);
    }

    /// <summary>The burden and spacing of a grid, and where its first row and first hole of a row go with them.</summary>
    public static void Resize(HolePattern pat, double burden, double spacing)
    {
        pat.Burden = burden;
        pat.Spacing = spacing;
        Anchor(pat);
    }

    /// <summary>
    /// Lays the pattern out on its grid again. A hole whose node is still inside the outline goes to it (keeping its id,
    /// loading, type, times and detonator); one whose node left the outline is deleted with its ties; a new node gets a new
    /// hole. The rows are made again by grid row, the face row first, and the holes numbered by them unless the numbers are kept.
    /// </summary>
    public static PatternRebuildReport RebuildPattern(BlastProject p, HolePattern pat, PatternRebuildOptions? o = null)
    {
        o ??= new PatternRebuildOptions();
        var report = new PatternRebuildReport();
        if (RebuildRefusal(p, pat, o) is { } no) { report.Refusal = no; return report; }
        var plan = PlanPattern(p, pat);
        var turn = GeoMath.NormalizeBearing(pat.Azimuth - pat.InitialAzimuth);
        double? BearingOf(Hole h, double rotated) => h.Angle < 0.1 ? null : o.Bearing switch
        {
            PatternBearingMode.Rotate => GeoMath.NormalizeBearing(rotated),
            PatternBearingMode.BlastOrientation => p.Header.BlastOrientation,
            _ => null
        };
        // the four numbers of the depth at the corners of the conditional rectangle of the grid
        var depth = pat.HoleDefaults.Depth;
        var rect = depth.UsesRectangle ? DepthRectangle.Of(pat) : null;
        // the collar and depth polygons, on the vertices of the outline of the pattern (or segment)
        var (collarPoly, depthPoly) = depth.UsesPolygons ? PatternSurfaces.Surfaces(p, pat, o.CollarSurface) : (null, null);
        // the surface the collars sit on (the On the surface item of the list); null when it is gone: the collars stay
        var ownSurface = depth.OnOwnSurface;
        var collarSurface = ownSurface ? CollarSurfaces.Find(p, depth.CollarSurfaceId) : null;
        // a hole bound to a surface goes onto it at its node; one bound to a surface gone or not under it, or taken off the
        // pattern's by an elevation typed in, keeps its collar elevation
        double? OwnCollar(Hole h, Vec3 at) => CollarSurfaces.Find(p, h.CollarSurfaceId)?.ElevationAt(at.X, at.Y);
        bool KeepsZ(Hole h) => h.CollarSurfaceId != null || ownSurface;
        Vec3 OnCollarPolygon(Vec3 at) => collarPoly != null && depth.Collars == CollarSource.Polygon ? at.WithZ(PatternSurfaces.At(collarPoly, at.X, at.Y)) : at;
        double? RectDepth(int i, int j) => rect is { } r && r.Relative(pat, i, j) is var (s, t) ? depth.At(s, t) : null;
        void Level(Hole h, bool isNew, double? rd)
        {
            if (depth.Mode == DepthMode.Polygon && depthPoly != null && !h.DepthManual)
            {
                var below = depth.IncludesSubdrill ? h.Subdrill : 0;
                var z = PatternSurfaces.DepthLevel(h, depthPoly) + below;
                h.ToLevel = HoleLevel.Reaches(h, z) ? z : null;
                if (HoleLevel.Length(h) is double polygonLength) h.Length = polygonLength;
                return;
            }
            if (depth.Mode == DepthMode.Range && !h.DepthManual)
            {
                // down to the grade (or the level), the depth held between the two numbers
                h.ToLevel = null;
                var cos = Math.Max(0.05, Math.Cos(h.Angle * Math.PI / 180));
                if (o.GradeSurface != null) SetLengthToGrade(h, o.GradeSurface, pat.HoleDefaults.BenchHeight);
                else h.Length = (h.Collar.Z - depth.Level) / cos + h.Subdrill;
                var vertical = (h.Length - h.Subdrill) * cos;
                double lo = Math.Min(depth.Front.From, depth.Front.To), hi = Math.Max(depth.Front.From, depth.Front.To);
                if (vertical < lo - 1e-9 || vertical > hi + 1e-9)
                {
                    h.Length = (Math.Clamp(vertical, lo, hi) + h.Subdrill) / cos;
                    report.ClampedIds.Add(h.Id);
                }
                return;
            }
            if (rd is double d)
            {
                var below = depth.IncludesSubdrill ? h.Subdrill : 0;
                if (depth.Mode == DepthMode.RectToLevel) h.ToLevel = depth.Level + below;
                else h.ToLevel = HoleLevel.Reaches(h, d + below) ? d + below : null;
            }
            else if (pat.ToSurface) h.ToLevel = HoleLevel.SurfaceLevel(p, h) is double s && HoleLevel.Reaches(h, s) ? s : null;
            else if (isNew && pat.ToLevel is double z && HoleLevel.Reaches(h, z)) h.ToLevel = z;
            if (HoleLevel.Length(h) is double length) h.Length = length;
            else if (o.GradeSurface != null && HoleLevel.Of(h) == null) SetLengthToGrade(h, o.GradeSurface, pat.HoleDefaults.BenchHeight);
        }

        foreach (var (h, node, target) in plan.Moves)
        {
            // a depth edited by hand stays as it is, its collar elevation with it
            var rd = h.DepthManual ? null : RectDepth(node.I, node.J);
            // the rectangle to a level sets the collars itself; otherwise a hole bound to a surface goes onto it
            var collar = depth.Mode != DepthMode.RectToLevel && OwnCollar(h, target) is double hz ? target.WithZ(hz)
                : depth.Collars == CollarSource.Polygon && collarPoly != null ? (h.DepthManual ? target.WithZ(h.Collar.Z) : OnCollarPolygon(target))
                : depth.Mode == DepthMode.RectToLevel && rd is double dz ? target.WithZ(depth.Level + dz)
                : depth.Mode == DepthMode.RectToLevel && h.DepthManual ? target.WithZ(h.Collar.Z)
                : KeepsZ(h) ? target
                : o.CollarSurface?.ElevationAt(target.X, target.Y) is double cz ? target.WithZ(cz) : target;
            // a node where the hole already is (within a nanometre) leaves it as it is, to the last bit
            if (Vec3.Distance2D(collar, h.Collar) <= 1e-9) collar = new Vec3(h.Collar.X, h.Collar.Y, collar.Z);
            var shift = collar - h.Collar;
            if (shift.Length2D > 1e-9) report.Moved++;
            h.Collar = collar;
            if (h.BoreTrack != null) for (int k = 0; k < h.BoreTrack.Count; k++) h.BoreTrack[k] += shift;
            if (BearingOf(h, h.Bearing + o.BearingDelta) is double b) h.Bearing = b;
            Level(h, false, rd);
        }
        // the extra holes (copies, on no node) stay where they are and take the depth of the pattern there
        foreach (var h in PatternHoles(p, pat).Where(h => IsExtraHole(h) && !h.DepthManual && !PatternTree.IsSeamHole(p, h)).ToList())
            Level(h, false, null);

        var gone = plan.Deletes.Select(h => h.Id).ToHashSet();
        if (gone.Count > 0)
        {
            report.TiesLost = p.Entities.RemoveAll(e => e is TieEntity t && (gone.Contains(t.FromId) || gone.Contains(t.ToId)));
            p.Entities.RemoveAll(e => gone.Contains(e.Id));
            foreach (var r in p.Rows) r.HoleIds.RemoveAll(gone.Contains);
            report.Deleted = gone.Count;
            report.DeletedIds.AddRange(gone);
        }

        var d = pat.HoleDefaults;
        var hp = new HoleParameters
        {
            Diameter = d.Diameter, Angle = d.Angle, Bearing = d.Bearing, Subdrill = d.Subdrill, BenchHeight = d.BenchHeight, HoleType = d.HoleType, Dummy = d.Dummy,
            CollarSurface = o.CollarSurface, GradeSurface = o.GradeSurface
        };
        var layer = o.Layer ?? p.GetOrCreateLayer(StandardLayers.Holes, 0xFF1E3A8A, "Design");
        var added = new HashSet<Guid>();
        foreach (var n in plan.Adds)
        {
            var h = CreateHole(n.Point, hp);
            if (BearingOf(h, d.Bearing + turn) is double b) h.Bearing = b;
            h.PatternId = pat.Id;
            h.GridI = n.I;
            h.GridJ = n.J;
            var rd = RectDepth(n.I, n.J);
            if (depth.Collars == CollarSource.Polygon && collarPoly != null) h.Collar = OnCollarPolygon(h.Collar);
            else if (depth.Mode == DepthMode.RectToLevel && rd is double dz) h.Collar = h.Collar.WithZ(depth.Level + dz);
            else if (collarSurface != null)
            {
                // a new hole of a pattern with its collars on a surface goes onto it and is bound to it
                CollarSurfaces.Seat(h, collarSurface);
                h.CollarSurfaceId = collarSurface.Id;
            }
            Level(h, true, rd);
            p.Add(h, layer);
            added.Add(h.Id);
            report.AddedIds.Add(h.Id);
        }
        report.Added = added.Count;

        var rows = RebuildRows(p, pat);
        var ordered = rows.SelectMany(r => r.HoleIds).Select(p.FindEntity).OfType<Hole>().ToList();
        // the extra holes (copies, on no node) are in no row: they take the numbers after the others
        ordered.AddRange(PatternHoles(p, pat).Where(IsExtraHole));
        // the holes of a segment are numbered through its whole tree (PatternTree.Renumber)
        if (pat.ParentId == null) Number(p, pat, ordered, added);
        pat.Revision++;
        return report;
    }

    /// <summary>The label of a hole of a row: the prefix of its pattern, the name of the row and its number in the row (B12-A3).</summary>
    public static string RowLabel(string prefix, string row, int number) => prefix + row + number;

    /// <summary>The number a label ends with, 0 when it ends with none.</summary>
    internal static int TailNumber(string label)
    {
        var i = label.Length;
        while (i > 0 && char.IsDigit(label[i - 1])) i--;
        return i < label.Length && int.TryParse(label[i..], out var k) ? k : 0;
    }

    /// <summary>
    /// The holes of a pattern numbered in row order: a hole of a row after the prefix and the letter of its row, each row from
    /// the first number (<see cref="RowLabel"/>); a hole in no row (an extra one) the prefix and its place among them all.
    /// With the numbers kept only the new ones are numbered: in a row on from the last number of the row, in none on from
    /// the last number of the project.
    /// </summary>
    internal static void Number(BlastProject p, HolePattern pat, List<Hole> ordered, HashSet<Guid> added)
    {
        var rows = p.Rows.ToDictionary(r => r.Id);
        HoleRow? RowOf(Hole h) => h.RowId is Guid id && rows.TryGetValue(id, out var r) ? r : null;
        var next = new Dictionary<Guid, int>();
        if (!pat.KeepLabels)
        {
            int n = pat.StartNumber;
            foreach (var h in ordered)
            {
                if (RowOf(h) is { } row)
                {
                    var k = next.GetValueOrDefault(row.Id, pat.StartNumber);
                    h.Label = RowLabel(pat.LabelPrefix, row.Name, k);
                    next[row.Id] = k + 1;
                }
                else h.Label = pat.LabelPrefix + n;
                n++;
            }
        }
        else
        {
            var loose = p.Holes.Where(h => !added.Contains(h.Id)).Select(h => int.TryParse(new string(h.Label.Where(char.IsDigit).ToArray()), out var k) ? k : 0).DefaultIfEmpty(0).Max() + 1;
            foreach (var h in ordered.Where(h => added.Contains(h.Id)))
            {
                if (RowOf(h) is { } row)
                {
                    if (!next.TryGetValue(row.Id, out var k))
                        k = row.HoleIds.Where(id => !added.Contains(id)).Select(p.FindEntity).OfType<Hole>().Select(x => TailNumber(x.Label)).DefaultIfEmpty(pat.StartNumber - 1).Max() + 1;
                    h.Label = RowLabel(pat.LabelPrefix, row.Name, k);
                    next[row.Id] = k + 1;
                }
                else h.Label = pat.LabelPrefix + loose++;
            }
        }
    }

    /// <summary>The holes of rows numbered by their row: the prefix, the letter of the row and the number in it from <paramref name="start"/>.</summary>
    public static void LabelRows(IEnumerable<HoleRow> rows, IEnumerable<Hole> holes, string prefix = "", int start = 1)
    {
        var byId = holes.ToDictionary(h => h.Id);
        foreach (var row in rows)
        {
            var k = start;
            foreach (var id in row.HoleIds)
                if (byId.TryGetValue(id, out var h)) h.Label = RowLabel(prefix, row.Name, k++);
        }
    }

    /// <summary>
    /// A row named anew, its holes numbered by it. Holes numbered after the old name take the new one in its place, their
    /// numbers and prefixes kept (A3 becomes D3, B12-A3 B12-D3). A row whose holes are not all so numbered (a file of before,
    /// numbers given by hand) is numbered anew: the prefix of the pattern of each hole, the new name and the place in the
    /// row from the first number of the pattern (1 without one).
    /// </summary>
    public static void RenameRow(BlastProject p, Guid rowId, string name)
    {
        if (p.Rows.FirstOrDefault(r => r.Id == rowId) is not { } row) return;
        var old = row.Name;
        row.Name = name;
        var holes = row.HoleIds.Select(p.FindEntity).OfType<Hole>().ToList();
        var pattern = old.Length > 0 ? new System.Text.RegularExpressions.Regex("^(.*)" + System.Text.RegularExpressions.Regex.Escape(old) + "(\\d+)$") : null;
        var matches = holes.Select(h => pattern?.Match(h.Label)).ToList();
        if (holes.Count > 0 && matches.All(m => m is { Success: true }))
        {
            for (int i = 0; i < holes.Count; i++) holes[i].Label = matches[i]!.Groups[1].Value + name + matches[i]!.Groups[2].Value;
            return;
        }
        for (int i = 0; i < holes.Count; i++)
        {
            var of = p.FindPattern(holes[i].PatternId);
            holes[i].Label = RowLabel(of?.LabelPrefix ?? "", name, (of?.StartNumber ?? 1) + i);
        }
    }

    /// <summary>
    /// The rows of the pattern made again by grid row, each keeping its row id: the row furthest along the blast orientation
    /// first, the holes of a row from left to right looking along it. They take the place of the old rows in the list.
    /// </summary>
    internal static List<HoleRow> RebuildRows(BlastProject p, HolePattern pat)
    {
        var holes = PatternHoles(p, pat).Where(h => h.GridJ != null).ToList();
        var ids = holes.Select(h => h.Id).ToHashSet();
        var mine = pat.RowIds.Values.ToHashSet();
        var at = p.Rows.FindIndex(r => mine.Contains(r.Id));
        foreach (var r in p.Rows) r.HoleIds.RemoveAll(ids.Contains);
        var old = p.Rows.Where(r => mine.Contains(r.Id) && r.HoleIds.Count == 0).ToDictionary(r => r.Id);
        p.Rows.RemoveAll(r => old.ContainsKey(r.Id));
        if (at < 0 || at > p.Rows.Count) at = p.Rows.Count;

        var move = GeoMath.HoleDirection(p.Header.BlastOrientation, 90);
        var across = new Vec3(move.Y, -move.X, 0);
        double Along(Vec3 a, Vec3 dir) => (a.X - pat.Center.X) * dir.X + (a.Y - pat.Center.Y) * dir.Y;
        var made = new List<(HoleRow Row, double Front)>();
        var map = new Dictionary<int, Guid>();
        foreach (var g in holes.GroupBy(h => h.GridJ!.Value))
        {
            var row = pat.RowIds.TryGetValue(g.Key, out var id) && old.Remove(id, out var kept) ? kept : new HoleRow();
            row.Burden = pat.Burden;
            row.Spacing = pat.Spacing;
            row.HoleIds = g.OrderBy(h => Along(h.Collar, across)).ThenBy(h => h.GridI).Select(h => h.Id).ToList();
            foreach (var h in g) h.RowId = row.Id;
            map[g.Key] = row.Id;
            made.Add((row, g.Average(h => Along(h.Collar, move))));
        }
        var rows = made.OrderByDescending(m => m.Front).Select(m => m.Row).ToList();
        p.Rows.InsertRange(at, rows);
        for (int k = 0; k < rows.Count; k++) rows[k].Name = HoleRow.Letter(at + k);
        pat.RowIds = map;
        return rows;
    }

    /// <summary>
    /// Whether a hole is an extra hole of its pattern: one of the pattern (a copy of one of its holes pasted or copied, kept
    /// in the pattern or segment of the one copied) on no node. A layout leaves it where it is, neither moved nor deleted;
    /// it goes along when the pattern is moved or turned as a whole, takes the values the pattern gives its holes and the
    /// depth of the pattern at its place; deleted, it leaves no node empty. The holes the seam rule puts in gaps are on no
    /// node either (<see cref="PatternTree.IsSeamHole"/>), but the next layout makes them again.
    /// </summary>
    public static bool IsExtraHole(Hole h) => h.PatternId != null && (h.GridI == null || h.GridJ == null);

    /// <summary>
    /// The extra holes of the leaves of a pattern (<see cref="IsExtraHole"/>) turned with its grid about a pivot, by
    /// <paramref name="delta"/> degrees clockwise; with <paramref name="bearings"/> the inclined ones take the bearing the
    /// layout gives the holes on the nodes (<see cref="PatternRebuildOptions.Bearing"/>). They then take the depth of the
    /// pattern at their new place.
    /// </summary>
    internal static void TurnExtraHoles(BlastProject p, IEnumerable<HolePattern> leaves, Vec3 pivot, double delta, PatternRebuildOptions o, bool bearings)
    {
        if (Math.Abs(delta) < 1e-12) return;
        var f = GeneralOps.MakeTransform(new Vec3(0, 0, 0), delta, pivot, 1);
        var turned = new List<(Hole Hole, HolePattern Pattern)>();
        foreach (var leaf in leaves)
            foreach (var h in PatternHoles(p, leaf).Where(h => IsExtraHole(h) && !PatternTree.IsSeamHole(p, h)).ToList())
            {
                var to = f(h.Collar);
                h.Translate(new Vec3(to.X - h.Collar.X, to.Y - h.Collar.Y, 0));
                if (bearings && h.Angle >= 0.1)
                {
                    if (o.Bearing == PatternBearingMode.Rotate) h.Bearing = GeoMath.NormalizeBearing(h.Bearing + delta);
                    else if (o.Bearing == PatternBearingMode.BlastOrientation) h.Bearing = p.Header.BlastOrientation;
                }
                if (!h.DepthManual) turned.Add((h, leaf));
            }
        Relevel(p, turned);
    }

    /// <summary>Takes holes off their pattern grid; their nodes get no hole when the grid is laid out again.</summary>
    public static int DetachFromPattern(BlastProject p, IEnumerable<Hole> holes)
    {
        int n = 0;
        foreach (var h in holes)
        {
            if (p.FindPattern(h.PatternId) is { } pat && h.GridI is int i && h.GridJ is int j && !pat.IsSuppressed(i, j))
                pat.Suppressed.Add(new GridIndex { I = i, J = j });
            if (h.PatternId == null) continue;
            h.DetachFromPattern();
            n++;
        }
        return n;
    }

    /// <summary>Removes the grid of a pattern; its holes stay as holes of their own.</summary>
    public static void DissolvePattern(BlastProject p, HolePattern pat)
    {
        // a pattern cut into segments takes its segments with it
        foreach (var s in PatternTree.Descendants(p, pat))
        {
            foreach (var h in PatternHoles(p, s).ToList()) h.DetachFromPattern();
            p.Patterns.Remove(s);
        }
        foreach (var h in PatternHoles(p, pat).ToList()) h.DetachFromPattern();
        p.Patterns.Remove(pat);
    }

    /// <summary>
    /// Follows a change of a document (<see cref="Domain.Document.Followers"/>) for the patterns it did not lay out itself:
    /// a pattern hole deleted by hand leaves its node empty from then on; one moved by hand keeps its offset from the node;
    /// all the holes of a pattern moved or turned together take the grid with them; a copy of a pattern hole is an extra hole of
    /// the pattern (or segment) of the one copied, on no node (<see cref="IsExtraHole"/>).
    /// A pattern cut into segments goes along only when all the holes of all its segments moved together (its segments and
    /// cut lines with it); the holes of one segment moved together keep their offsets, as the segment stays in its part.
    /// The length or level of a hole with a depth from/to edited by hand is kept from then on (<see cref="Hole.DepthManual"/>).
    /// A hole moved, or a copy pasted, of a pattern drilled to the surface or to the depth polygon takes the level of its new
    /// place (<see cref="Relevel"/>).
    /// </summary>
    public static Action? FollowPatterns(BlastProject project)
    {
        if (project.Patterns.Count == 0) return null;
        var holesBefore = project.Patterns.ToDictionary(pat => pat.Id, _ => new Dictionary<Guid, (Vec3 Collar, int? I, int? J)>());
        var depthBefore = new Dictionary<Guid, (double Length, double? Level, double Z, double Angle, double Subdrill)>();
        foreach (var h in project.Holes)
        {
            if (h.PatternId is not Guid id || !holesBefore.TryGetValue(id, out var list)) continue;
            list[h.Id] = (h.Collar, h.GridI, h.GridJ);
            if (project.FindPattern(id)!.HoleDefaults.Depth is { } ds && (ds.IsOwnMode || ds.UsesPolygons)) depthBefore[h.Id] = (h.Length, h.ToLevel, h.Collar.Z, h.Angle, h.Subdrill);
        }
        var before = project.Patterns.Select(pat => (pat, pat.Revision)).ToList();
        var revisions = before.ToDictionary(x => x.pat.Id, x => x.Revision);
        return () =>
        {
            var now = project.Holes.ToDictionary(h => h.Id);
            // the holes now standing elsewhere, with the pattern whose depth they take there
            var relevel = new List<(Hole Hole, HolePattern Pattern)>();
            // a pattern cut into segments with all its holes moved or turned together: the whole tree goes along
            var handled = new HashSet<Guid>();
            foreach (var (root, revision) in before.Where(x => x.pat.IsContainer && x.pat.ParentId == null))
            {
                if (!project.Patterns.Contains(root) || root.Revision != revision) continue;
                var tree = PatternTree.Descendants(project, root);
                if (tree.Any(s => !revisions.TryGetValue(s.Id, out var r) || r != s.Revision)) continue;
                var was = tree.SelectMany(s => holesBefore[s.Id]).ToList();
                if (was.Count == 0 || was.Any(x => !now.ContainsKey(x.Key))) continue;
                var moved = was.Select(x => (Hole: now[x.Key], From: x.Value.Collar)).ToList();
                if (moved.Any(m => Vec3.Distance2D(m.From, m.Hole.Collar) <= 1e-6) || Rigid(moved) is not var (turn, pivotFrom, pivotTo)) continue;
                PatternTree.Transform(project, root, GeneralOps.MakeTransform(pivotTo - pivotFrom, turn, pivotFrom, 1), turn);
                foreach (var (h, _) in moved)
                    if (h.PatternId is Guid pid && project.FindPattern(pid) is { } leaf) relevel.Add((h, leaf));
                handled.Add(root.Id);
                foreach (var s in tree) handled.Add(s.Id);
            }
            foreach (var (pat, revision) in before)
            {
                if (handled.Contains(pat.Id) || !project.Patterns.Contains(pat) || pat.Revision != revision) continue;
                var was = holesBefore[pat.Id];
                var moved = new List<(Hole Hole, Vec3 From)>();
                var present = 0;
                foreach (var (id, (from, gi, gj)) in was)
                {
                    if (!now.TryGetValue(id, out var h))
                    {
                        // deleted by hand: its node stays empty, unless the node is out of the outline anyway
                        if (gi is int i && gj is int j && !pat.IsSuppressed(i, j) && NodeInside(pat, NodePoint(pat, i, j)))
                            pat.Suppressed.Add(new GridIndex { I = i, J = j });
                        continue;
                    }
                    if (h.PatternId != pat.Id) continue;
                    present++;
                    if (Vec3.Distance2D(from, h.Collar) > 1e-6) moved.Add((h, from));
                }
                foreach (var h in now.Values.Where(h => h.PatternId == pat.Id && !was.ContainsKey(h.Id) && !PatternTree.IsSeamHole(project, h)))
                {
                    // a copy: an extra hole of the pattern (segment) of the one copied, on no node (IsExtraHole), wherever
                    // it is put; the depth of the pattern where it is, unless the one copied had a depth of its own
                    if (!h.DepthManual) relevel.Add((h, pat));
                    h.GridI = h.GridJ = null;
                    h.GridOffset = null;
                }
                // a length or a level of a hole with a depth from/to edited by hand: kept when the pattern is laid out again
                foreach (var id in was.Keys)
                    if (now.TryGetValue(id, out var h) && h.PatternId == pat.Id && depthBefore.TryGetValue(id, out var d)
                        && (h.ToLevel != d.Level || (Math.Abs(h.Length - d.Length) > 1e-9 && h.Collar.Z == d.Z && h.Angle == d.Angle && h.Subdrill == d.Subdrill)))
                        h.DepthManual = true;
                if (moved.Count == 0) continue;
                foreach (var (h, _) in moved)
                    if (!h.DepthManual) relevel.Add((h, pat));
                // a segment stays in its part of the pattern: its holes moved together keep their offsets from their nodes
                if (pat.ParentId == null && moved.Count == present && present == was.Count && Rigid(moved) is var (turn, pivotFrom, pivotTo))
                {
                    var f = GeneralOps.MakeTransform(pivotTo - pivotFrom, turn, pivotFrom, 1);
                    pat.Outline = pat.Outline.Select(x => f(x).WithZ(x.Z)).ToList();
                    pat.Center = f(pat.Center).WithZ(pat.Center.Z);
                    pat.Azimuth = GeoMath.NormalizeBearing(pat.Azimuth + turn);
                    pat.InitialAzimuth = GeoMath.NormalizeBearing(pat.InitialAzimuth + turn);
                    continue;
                }
                var (u, v) = PatternAxes(pat.Azimuth);
                foreach (var (h, _) in moved)
                {
                    if (h.GridI is not int i || h.GridJ is not int j) continue;
                    var d = h.Collar - NodePoint(pat, i, j);
                    var o = new Vec3(d.X * u.X + d.Y * u.Y, d.X * v.X + d.Y * v.Y, 0);
                    h.GridOffset = o.Length2D < 1e-6 ? null : o;
                }
            }
            Relevel(project, relevel);
        };
    }

    /// <summary>
    /// The depth of holes standing at a new place, taken there from the pattern they are of (or were copied from): drilled to
    /// the surface, the level where each meets it now; to the depth polygon, the level of the polygon under it now (the
    /// collar on the collar polygon, when the pattern has one). The length follows the level (<see cref="HoleLevel.Follow"/>).
    /// A depth of another mode stays: a level, a fixed depth and the rectangle go by the pattern or the node, not the place.
    /// </summary>
    public static void Relevel(BlastProject project, IReadOnlyList<(Hole Hole, HolePattern Pattern)> holes)
    {
        if (holes.Count == 0) return;
        var bench = project.Entities.OfType<SurfaceEntity>().FirstOrDefault(s => s.Kind == SurfaceKind.Bench);
        var polygons = new Dictionary<Guid, (SurfaceEntity? Collar, SurfaceEntity? Depth)>();
        foreach (var (h, pat) in holes)
        {
            var depth = pat.HoleDefaults.Depth;
            var mode = PatternTree.EffectiveDepthMode(pat);
            // a hole bound to a surface is on it at its new place before its depth is taken there
            if (CollarSurfaces.Find(project, h.CollarSurfaceId) is { } own) CollarSurfaces.Seat(h, own);
            if (depth.UsesPolygons)
            {
                if (!polygons.TryGetValue(pat.Id, out var poly)) polygons[pat.Id] = poly = PatternSurfaces.Surfaces(project, pat, bench);
                if (depth.Collars == CollarSource.Polygon && poly.Collar != null) h.Collar = h.Collar.WithZ(PatternSurfaces.At(poly.Collar, h.Collar.X, h.Collar.Y));
                if (mode == DepthMode.Polygon && poly.Depth != null)
                {
                    var z = PatternSurfaces.DepthLevel(h, poly.Depth) + (depth.IncludesSubdrill ? h.Subdrill : 0);
                    h.ToLevel = HoleLevel.Reaches(h, z) ? z : null;
                }
            }
            if (mode == DepthMode.ToSurface)
                h.ToLevel = HoleLevel.SurfaceLevel(project, h) is double s && HoleLevel.Reaches(h, s) ? s : null;
            if (HoleLevel.Length(h) is double length) h.Length = length;
        }
    }

    /// <summary>The turn (degrees clockwise) and shift that took every hole from where it was to where it is, within a millimetre; null when there is none.</summary>
    private static (double Turn, Vec3 PivotFrom, Vec3 PivotTo)? Rigid(List<(Hole Hole, Vec3 From)> moved)
    {
        var (a, a0) = (moved[0].Hole.Collar, moved[0].From);
        var far = moved.MaxBy(m => Vec3.Distance2D(m.From, a0));
        double turn = 0;
        if (Vec3.Distance2D(far.From, a0) > 1e-3)
        {
            if (Math.Abs(Vec3.Distance2D(far.Hole.Collar, a) - Vec3.Distance2D(far.From, a0)) > 1e-3) return null;
            turn = GeoMath.Bearing(a, far.Hole.Collar) - GeoMath.Bearing(a0, far.From);
        }
        var f = GeneralOps.MakeTransform(a - a0, turn, a0, 1);
        foreach (var (h, from) in moved)
            if (Vec3.Distance2D(f(from), h.Collar) > 1e-3) return null;
        return (turn, a0, a);
    }
}
