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
        var stagger = pat.Type == PatternType.Staggered && (j & 1) == 1 ? pat.Spacing / 2 : 0;
        var a = pat.PhaseU + i * pat.Spacing + stagger;
        var b = pat.PhaseV + j * pat.Burden;
        return new Vec3(pat.Center.X + u.X * a + v.X * b, pat.Center.Y + u.Y * a + v.Y * b, pat.Center.Z);
    }

    /// <summary>Whether a point gets a hole: inside the outline and not nearer to it than the edge offset.</summary>
    public static bool NodeInside(HolePattern pat, Vec3 pt)
    {
        if (!GeoMath.PointInPolygon(pat.Outline, pt.X, pt.Y)) return false;
        var d = GeoMath.DistanceToPolyline2D(pat.Outline, pt, true);
        return d > OnOutline && (pat.EdgeOffset <= 0 || d >= pat.EdgeOffset);
    }

    /// <summary>The nodes of the grid inside its outline (suppressed ones too), row by row, along each row.</summary>
    public static List<PatternNode> PatternNodes(HolePattern pat)
    {
        var nodes = new List<PatternNode>();
        if (pat.Outline.Count < 3 || pat.Burden <= 0 || pat.Spacing <= 0) return nodes;
        var r = pat.Outline.Max(p => Vec3.Distance2D(p, pat.Center));
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
    private static Vec3 AreaCentre(IReadOnlyList<Vec3> polygon)
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
                Diameter = hp.Diameter, Angle = hp.Angle, Bearing = hp.Bearing, Subdrill = hp.Subdrill, BenchHeight = hp.BenchHeight, HoleType = hp.HoleType, Dummy = hp.Dummy
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
    public static void Anchor(HolePattern pat)
    {
        if (pat.Outline.Count < 3 || pat.Burden <= 0 || pat.Spacing <= 0) return;
        if (pat.FaceEdge is int k && (k < 0 || k >= pat.Outline.Count)) pat.FaceEdge = null;
        if (pat.FaceEdge is int edge) pat.Azimuth = OutwardBearing(pat.Outline, edge);
        var (u, v) = PatternAxes(pat.Azimuth);
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue;
        foreach (var q in pat.Outline)
        {
            var d = q - pat.Center;
            var du = d.X * u.X + d.Y * u.Y;
            minU = Math.Min(minU, du);
            maxU = Math.Max(maxU, du);
            minV = Math.Min(minV, d.X * v.X + d.Y * v.Y);
        }
        if (pat.FaceEdge is int f)
        {
            // the line of the side, not the furthest point: a polygon bent out beyond it keeps the first row along the side
            var d = pat.Outline[f] - pat.Center;
            minV = d.X * v.X + d.Y * v.Y;
        }
        pat.PhaseV = minV + pat.Burden / 2;
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
    public static List<Hole> FillPolygon(IReadOnlyList<Vec3> polygon, PatternParameters pp, HoleParameters hp, out List<HoleRow> rows, out HolePattern pattern)
    {
        rows = new List<HoleRow>();
        var holes = new List<Hole>();
        if (polygon.Count < 3 || pp.Burden <= 0 || pp.Spacing <= 0)
        {
            pattern = new HolePattern { Outline = FlatOutline(polygon, polygon.Count > 0 ? polygon.Average(p => p.Z) : 0), Burden = pp.Burden, Spacing = pp.Spacing, Type = pp.Pattern };
            return holes;
        }
        pattern = CreatePattern(polygon, pp, hp);
        int number = pp.StartNumber;
        HoleRow? row = null;
        foreach (var n in PatternNodes(pattern))
        {
            if (row == null || pattern.RowIds.GetValueOrDefault(n.J) != row.Id)
            {
                row = new HoleRow { Name = $"Row {rows.Count + 1}", Burden = pp.Burden, Spacing = pp.Spacing };
                rows.Add(row);
                pattern.RowIds[n.J] = row.Id;
            }
            var h = CreateHole(n.Point, hp);
            h.Label = pp.LabelPrefix + number++;
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
        var suppressed = pat.Suppressed.Select(s => (s.I, s.J)).ToHashSet();
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
        void Level(Hole h, bool isNew)
        {
            if (pat.ToSurface) h.ToLevel = HoleLevel.SurfaceLevel(p, h) is double s && HoleLevel.Reaches(h, s) ? s : null;
            else if (isNew && pat.ToLevel is double z && HoleLevel.Reaches(h, z)) h.ToLevel = z;
            if (HoleLevel.Length(h) is double length) h.Length = length;
            else if (o.GradeSurface != null && HoleLevel.Of(h) == null) SetLengthToGrade(h, o.GradeSurface, pat.HoleDefaults.BenchHeight);
        }

        foreach (var (h, _, target) in plan.Moves)
        {
            var collar = o.CollarSurface?.ElevationAt(target.X, target.Y) is double cz ? target.WithZ(cz) : target;
            var shift = collar - h.Collar;
            if (shift.Length2D > 1e-9) report.Moved++;
            h.Collar = collar;
            if (h.BoreTrack != null) for (int k = 0; k < h.BoreTrack.Count; k++) h.BoreTrack[k] += shift;
            if (BearingOf(h, h.Bearing + o.BearingDelta) is double b) h.Bearing = b;
            Level(h, false);
        }

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
            Level(h, true);
            p.Add(h, layer);
            added.Add(h.Id);
            report.AddedIds.Add(h.Id);
        }
        report.Added = added.Count;

        var rows = RebuildRows(p, pat);
        var ordered = rows.SelectMany(r => r.HoleIds).Select(p.FindEntity).OfType<Hole>().ToList();
        if (!pat.KeepLabels)
        {
            int n = pat.StartNumber;
            foreach (var h in ordered) h.Label = pat.LabelPrefix + n++;
        }
        else
        {
            var next = p.Holes.Where(h => !added.Contains(h.Id)).Select(h => int.TryParse(new string(h.Label.Where(char.IsDigit).ToArray()), out var k) ? k : 0).DefaultIfEmpty(0).Max() + 1;
            foreach (var h in ordered.Where(h => added.Contains(h.Id))) h.Label = pat.LabelPrefix + next++;
        }
        pat.Revision++;
        return report;
    }

    /// <summary>
    /// The rows of the pattern made again by grid row, each keeping its row id: the row furthest along the blast orientation
    /// first, the holes of a row from left to right looking along it. They take the place of the old rows in the list.
    /// </summary>
    private static List<HoleRow> RebuildRows(BlastProject p, HolePattern pat)
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
        for (int k = 0; k < rows.Count; k++) rows[k].Name = $"Row {at + k + 1}";
        pat.RowIds = map;
        return rows;
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
        foreach (var h in PatternHoles(p, pat).ToList()) h.DetachFromPattern();
        p.Patterns.Remove(pat);
    }

    /// <summary>
    /// Follows a change of a document (<see cref="Domain.Document.Followers"/>) for the patterns it did not lay out itself:
    /// a pattern hole deleted by hand leaves its node empty from then on; one moved by hand keeps its offset from the node;
    /// all the holes of a pattern moved or turned together take the grid with them; a copy of a pattern hole is a hole of its own.
    /// </summary>
    public static Action? FollowPatterns(BlastProject project)
    {
        if (project.Patterns.Count == 0) return null;
        var holesBefore = project.Patterns.ToDictionary(pat => pat.Id, _ => new Dictionary<Guid, (Vec3 Collar, int? I, int? J)>());
        foreach (var h in project.Holes)
            if (h.PatternId is Guid id && holesBefore.TryGetValue(id, out var list)) list[h.Id] = (h.Collar, h.GridI, h.GridJ);
        var before = project.Patterns.Select(pat => (pat, pat.Revision)).ToList();
        return () =>
        {
            var now = project.Holes.ToDictionary(h => h.Id);
            foreach (var (pat, revision) in before)
            {
                if (!project.Patterns.Contains(pat) || pat.Revision != revision) continue;
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
                foreach (var h in now.Values.Where(h => h.PatternId == pat.Id && !was.ContainsKey(h.Id))) h.DetachFromPattern();
                if (moved.Count == 0) continue;
                if (moved.Count == present && present == was.Count && Rigid(moved) is var (turn, pivotFrom, pivotTo))
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
        };
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
