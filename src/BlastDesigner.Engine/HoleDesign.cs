using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>Parameters for creating holes (pattern, row and single-hole tools).</summary>
public sealed class HoleParameters
{
    public double Diameter { get; set; } = 102;
    public double Angle { get; set; }
    public double Bearing { get; set; }
    public double BenchHeight { get; set; } = 10;
    public double Subdrill { get; set; } = 1;
    public int HoleType { get; set; } = 1;
    public bool Dummy { get; set; }
    /// <summary>Optional collar surface; collars are draped on it when set.</summary>
    public SurfaceEntity? CollarSurface { get; set; }
    /// <summary>Optional grade surface; lengths reach this surface plus subdrill when set.</summary>
    public SurfaceEntity? GradeSurface { get; set; }

    public static HoleParameters From(DesignData d) => new()
    {
        Diameter = d.Diameter,
        Angle = d.Angle,
        Bearing = d.Bearing,
        BenchHeight = d.BenchHeight,
        Subdrill = d.Subdrill
    };
}

public sealed class PatternParameters
{
    public double Burden { get; set; } = 4;
    public double Spacing { get; set; } = 4.5;
    public PatternType Pattern { get; set; } = PatternType.Staggered;
    /// <summary>Direction the blast moves (towards the free face), degrees from north. Rows run perpendicular to it.</summary>
    public double Orientation { get; set; }
    /// <summary>Holes closer than this to the boundary are dropped (m).</summary>
    public double EdgeOffset { get; set; }
    public string LabelPrefix { get; set; } = string.Empty;
    public int StartNumber { get; set; } = 1;
    /// <summary>Turning the grid of the pattern later turns the blast orientation with it.</summary>
    public bool RotateOrientation { get; set; }
    /// <summary>
    /// The side of the polygon (from point k to point k + 1) the first row runs along, half a burden inside it; the direction
    /// is then across that side, out of the polygon, and <see cref="Orientation"/> is not used. Null: by the orientation.
    /// </summary>
    public int? FaceEdge { get; set; }
    /// <summary>Where the holes of a row start, looking along the blast direction.</summary>
    public RowAlignment Alignment { get; set; }
}

/// <summary>Hole layout operations: patterns, rows, along lines, numbering, draping to surfaces.</summary>
public static partial class HoleDesign
{
    public static Hole CreateHole(Vec3 collar, HoleParameters p)
    {
        var h = new Hole
        {
            Collar = collar,
            Diameter = p.Diameter,
            Angle = p.Angle,
            Bearing = p.Bearing,
            Subdrill = p.Subdrill,
            HoleType = p.HoleType,
            IsDummy = p.Dummy
        };
        if (p.CollarSurface?.ElevationAt(collar.X, collar.Y) is double cz) h.Collar = collar.WithZ(cz);
        SetLengthToGrade(h, p.GradeSurface, p.BenchHeight);
        return h;
    }

    /// <summary>Sets the hole length so that it reaches grade (surface or bench height) plus subdrill.</summary>
    public static void SetLengthToGrade(Hole h, SurfaceEntity? grade, double benchHeight)
    {
        var dir = h.Direction;
        var cos = Math.Max(0.05, -dir.Z);
        double vertical = benchHeight;
        if (grade != null)
        {
            // Iterate because the grade point moves in plan for angled holes.
            var depth = benchHeight / cos;
            for (int i = 0; i < 8; i++)
            {
                var pt = h.Collar + dir * depth;
                if (grade.ElevationAt(pt.X, pt.Y) is not double gz) break;
                var v = h.Collar.Z - gz;
                if (v <= 0) break;
                depth = v / cos;
                vertical = v;
            }
        }
        h.Length = (vertical + h.Subdrill) / cos;
    }

    /// <summary>Fills a polygon with a rectangular or staggered pattern.</summary>
    public static List<Hole> FillPolygon(IReadOnlyList<Vec3> polygon, PatternParameters pp, HoleParameters hp, out List<HoleRow> rows) =>
        FillPolygon(polygon, pp, hp, out rows, out _);

    /// <summary>Places holes along a polyline at a fixed spacing (Row tool / "Add along selected polyline").</summary>
    public static List<Hole> AlongPolyline(IReadOnlyList<Vec3> line, double spacing, HoleParameters hp, bool includeEnd, out HoleRow row)
    {
        row = new HoleRow { Spacing = spacing };
        var holes = new List<Hole>();
        foreach (var pt in RowPositions(line, spacing, includeEnd))
        {
            var h = CreateHole(pt, hp);
            h.RowId = row.Id;
            row.HoleIds.Add(h.Id);
            holes.Add(h);
        }
        return holes;
    }

    /// <summary>
    /// Collar positions of a row along a polyline: every <paramref name="spacing"/> metres from the start, plus the end point
    /// when the remainder is more than 30 % of the spacing. The row tool previews exactly these points.
    /// </summary>
    public static List<Vec3> RowPositions(IReadOnlyList<Vec3> line, double spacing, bool includeEnd)
    {
        var result = new List<Vec3>();
        var total = GeoMath.Length(line);
        if (spacing <= 0 || line.Count < 2 || total <= 0) return result;
        int n = (int)Math.Floor(total / spacing + 1e-9);
        for (int i = 0; i <= n; i++)
        {
            var d = i * spacing;
            if (d > total + 1e-9) break;
            result.Add(GeoMath.PointAlong(line, d, out _));
        }
        if (includeEnd && total - n * spacing > spacing * 0.3) result.Add(line[^1]);
        return result;
    }

    /// <summary>Creates parallel rows of holes between two lines ("Drill holes between lines").</summary>
    public static List<Hole> BetweenLines(IReadOnlyList<Vec3> lineA, IReadOnlyList<Vec3> lineB, double spacing, int rowCount, HoleParameters hp, out List<HoleRow> rows)
    {
        rows = new List<HoleRow>();
        var holes = new List<Hole>();
        rowCount = Math.Max(2, rowCount);
        const int samples = 64;
        var la = GeoMath.Length(lineA);
        var lb = GeoMath.Length(lineB);
        for (int r = 0; r < rowCount; r++)
        {
            var t = (double)r / (rowCount - 1);
            var interp = new List<Vec3>();
            for (int s = 0; s <= samples; s++)
            {
                var pa = GeoMath.PointAlong(lineA, la * s / samples, out _);
                var pb = GeoMath.PointAlong(lineB, lb * s / samples, out _);
                interp.Add(Vec3.Lerp(pa, pb, t));
            }
            holes.AddRange(AlongPolyline(interp, spacing, hp, false, out var row));
            row.Name = $"Row {r + 1}";
            rows.Add(row);
        }
        return holes;
    }

    /// <summary>Groups holes into rows perpendicular to the blast orientation.</summary>
    public static List<HoleRow> AutoRows(IEnumerable<Hole> holes, double orientation, double tolerance)
    {
        var move = GeoMath.HoleDirection(orientation, 90);
        var list = holes.Select(h => (Hole: h, V: -(h.Collar.X * move.X + h.Collar.Y * move.Y), U: h.Collar.X * move.Y - h.Collar.Y * move.X))
            .OrderBy(x => x.V).ToList();
        var rows = new List<HoleRow>();
        var current = new List<(Hole Hole, double V, double U)>();
        double? anchor = null;
        void Flush()
        {
            if (current.Count == 0) return;
            var row = new HoleRow { Name = $"Row {rows.Count + 1}" };
            row.HoleIds.AddRange(current.OrderBy(x => x.U).Select(x => x.Hole.Id));
            rows.Add(row);
            current.Clear();
        }
        foreach (var x in list)
        {
            if (anchor is null || x.V - anchor.Value > tolerance) { Flush(); anchor = x.V; }
            current.Add(x);
        }
        Flush();
        return rows;
    }

    /// <summary>Renumbers holes sequentially following row order then position along the row.</summary>
    public static void Renumber(BlastProject project, IEnumerable<Hole> holes, string prefix, int start)
    {
        var set = holes.ToHashSet();
        int n = start;
        var done = new HashSet<Hole>();
        foreach (var row in project.Rows)
            foreach (var id in row.HoleIds)
                if (project.FindEntity(id) is Hole h && set.Contains(h) && done.Add(h))
                    h.Label = prefix + n++;
        foreach (var h in set.Where(h => !done.Contains(h)).OrderByDescending(h => h.Collar.Y).ThenBy(h => h.Collar.X))
            h.Label = prefix + n++;
    }

    /// <summary>Finds pairs of holes whose collars are within the tolerance.</summary>
    public static List<(Hole A, Hole B)> FindDuplicates(IReadOnlyList<Hole> holes, double tolerance)
    {
        var result = new List<(Hole, Hole)>();
        var cell = Math.Max(tolerance, 1e-3);
        var grid = new Dictionary<(long, long), List<Hole>>();
        foreach (var h in holes)
        {
            var key = ((long)Math.Floor(h.Collar.X / cell), (long)Math.Floor(h.Collar.Y / cell));
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (grid.TryGetValue((key.Item1 + dx, key.Item2 + dy), out var bucket))
                        foreach (var o in bucket)
                            if (Vec3.Distance2D(o.Collar, h.Collar) <= tolerance) result.Add((o, h));
            if (!grid.TryGetValue(key, out var b)) grid[key] = b = new List<Hole>();
            b.Add(h);
        }
        return result;
    }

    /// <summary>Gives every hole with a repeated label a unique label by appending a suffix.</summary>
    public static int RelabelDuplicates(IEnumerable<Hole> holes)
    {
        int changed = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in holes)
        {
            if (used.Add(h.Label)) continue;
            int k = 2;
            string candidate;
            do candidate = $"{h.Label}_{k++}"; while (!used.Add(candidate));
            h.Label = candidate;
            changed++;
        }
        return changed;
    }

    /// <summary>Moves each hole to the nearest unused point (Holes ▸ Move to selected points).</summary>
    public static int MoveToPoints(IList<Hole> holes, IList<Vec3> points, double maxDistance)
    {
        var free = points.ToList();
        int moved = 0;
        foreach (var h in holes)
        {
            if (free.Count == 0) break;
            var best = free.OrderBy(p => Vec3.Distance2D(p, h.Collar)).First();
            if (Vec3.Distance2D(best, h.Collar) > maxDistance) continue;
            var toe = h.DesignToe + (best - h.Collar);
            h.Collar = best;
            h.SetToe(toe);
            free.Remove(best);
            moved++;
        }
        return moved;
    }

    /// <summary>Drapes collars onto a surface keeping the toe elevation (so lengths change).</summary>
    public static void DrapeCollars(IEnumerable<Hole> holes, SurfaceEntity surface, bool keepToe)
    {
        foreach (var h in holes)
        {
            if (surface.ElevationAt(h.Collar.X, h.Collar.Y) is not double z) continue;
            if (keepToe)
            {
                var toe = h.DesignToe;
                h.Collar = h.Collar.WithZ(z);
                h.SetToe(toe);
            }
            else h.Collar = h.Collar.WithZ(z);
        }
    }

    /// <summary>Holes whose collars fall inside a polygon.</summary>
    public static IEnumerable<Hole> InsidePolygon(IEnumerable<Hole> holes, IReadOnlyList<Vec3> poly) =>
        holes.Where(h => GeoMath.PointInPolygon(poly, h.Collar.X, h.Collar.Y));

    /// <summary>Optimises spacing so that holes are evenly distributed along each row between its first and last hole.</summary>
    public static void EvenOutRow(BlastProject project, HoleRow row)
    {
        var holes = row.HoleIds.Select(project.FindEntity).OfType<Hole>().ToList();
        if (holes.Count < 3) return;
        var a = holes[0].Collar;
        var b = holes[^1].Collar;
        for (int i = 1; i < holes.Count - 1; i++)
        {
            var target = Vec3.Lerp(a, b, (double)i / (holes.Count - 1));
            holes[i].Translate(target.WithZ(holes[i].Collar.Z) - holes[i].Collar);
        }
    }

    /// <summary>Summary statistics for the "Profile"/"Row check" style views.</summary>
    public static (double MinSpacing, double MaxSpacing, double MeanSpacing) RowSpacing(BlastProject project, HoleRow row)
    {
        var holes = row.HoleIds.Select(project.FindEntity).OfType<Hole>().ToList();
        var d = new List<double>();
        for (int i = 1; i < holes.Count; i++) d.Add(Vec3.Distance2D(holes[i - 1].Collar, holes[i].Collar));
        return d.Count == 0 ? (0, 0, 0) : (d.Min(), d.Max(), d.Average());
    }
}
