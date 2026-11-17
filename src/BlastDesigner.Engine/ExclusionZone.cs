using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using Clipper2Lib;

namespace BlastDesigner.Engine;

/// <summary>
/// Exclusion zone: the larger of the flyrock clearance and the vibration and airblast distances. The charged holes standing
/// together are a source of it, each with its own radius from its own charge; the zone is the circles of those radii about
/// the holes, one outline where they overlap, apart where they do not.
/// </summary>
public static class ExclusionZone
{
    public sealed record Result(double Mic, double VibrationRadius, double AirblastRadius, double FlyrockRadius)
    {
        public double Radius => Math.Max(VibrationRadius, Math.Max(AirblastRadius, FlyrockRadius));
    }

    /// <summary>
    /// A group of charged holes standing together, numbered west to east, the radii worked out over it, the radius typed in
    /// for it if any, and where its radius arrow points (degrees clockwise from north).
    /// </summary>
    public sealed record Source(int Number, List<Hole> Holes, Result Result, double? SetRadius = null, double Azimuth = 0)
    {
        /// <summary>The radius of its zone: the one typed in, else the calculated one.</summary>
        public double Radius => SetRadius ?? Result.Radius;
    }

    /// <summary>The setting of the project for this source: the first one kept by a hole of it.</summary>
    public static ExclusionSourceSetting? Setting(BlastProject project, IEnumerable<Hole> holes)
    {
        var ids = holes.Select(h => h.Id).ToHashSet();
        return project.Vibration.ExclusionSources.FirstOrDefault(x => ids.Contains(x.HoleId));
    }

    /// <summary>One outline of the zone, the sources inside it and its arrows (a radius, or a length and a width).</summary>
    public sealed record Zone(List<Vec3> Outline, List<Source> Sources, List<ZoneArrow> Arrows)
    {
        public double Radius => Sources.Count == 0 ? 0 : Sources.Max(s => s.Radius);
    }

    /// <summary>Solves the timing, then the radii are the properties <c>blast.vibrationRadius</c>, <c>blast.airblastRadius</c>, <c>blast.flyrockClearance</c>.</summary>
    public static Result Compute(BlastProject project)
    {
        Timing.Solve(project);
        var s = CalcService.Live(project) ?? CalcService.Session(project);
        double V(string id) => Charging.Calculated(s, id, project, 0);
        return new Result(V("blast.micOrMaxCharge"), V("blast.vibrationRadius"), V("blast.airblastRadius"), V("blast.flyrockClearance"));
    }

    /// <summary>The radii over these holes only (the timing solved already): the charge in a window counts their charges alone.</summary>
    private static Result Over(BlastProject project, IReadOnlyList<Hole> holes)
    {
        var s = CalcService.Session(project);
        s.Scope.Subset = holes;
        s.Scope.BuiltinArgs["mic.holes"] = holes;
        double V(string id) => Charging.Calculated(s, id, project, 0);
        return new Result(V("blast.micOrMaxCharge"), V("blast.vibrationRadius"), V("blast.airblastRadius"), V("blast.flyrockClearance"));
    }

    /// <summary>The holes that make the zone: the charged ones; with none charged, every hole that is not a dummy.</summary>
    public static List<Hole> SourceHoles(BlastProject project)
    {
        var s = CalcService.Live(project) ?? CalcService.Session(project);
        var real = project.Holes.Where(h => !h.IsDummy).ToList();
        var charged = real.Where(h => h.Decks.Count > 0 && s.Value("hole.hasExplosive", h).Object is true).ToList();
        return charged.Count > 0 ? charged : real;
    }

    /// <summary>
    /// The distance that joins holes into one source when none is set: three times the median distance from a hole to its
    /// nearest neighbour, so a pattern is one source and another pattern a few spacings off is another. At least a metre.
    /// </summary>
    public static double AutoMergeDistance(IReadOnlyList<Hole> holes)
    {
        if (holes.Count < 2) return 1;
        var nearest = new List<double>(holes.Count);
        foreach (var h in holes)
        {
            var best = double.PositiveInfinity;
            foreach (var o in holes)
            {
                if (ReferenceEquals(o, h)) continue;
                var d = Vec3.Distance2D(o.Collar, h.Collar);
                if (d > 1e-6 && d < best) best = d;
            }
            if (double.IsFinite(best)) nearest.Add(best);
        }
        if (nearest.Count == 0) return 1;
        nearest.Sort();
        return Math.Max(1, 3 * nearest[nearest.Count / 2]);
    }

    /// <summary>The merge distance of the project: the one set, else <see cref="AutoMergeDistance"/>.</summary>
    public static double MergeDistance(BlastProject project, IReadOnlyList<Hole> holes) =>
        project.Vibration.ExclusionMergeDistance > 0 ? project.Vibration.ExclusionMergeDistance : AutoMergeDistance(holes);

    /// <summary>
    /// Groups of holes joined by steps no longer than <paramref name="distance"/> in plan (single linkage, over a grid of
    /// cells of that size), whatever pattern they are in.
    /// </summary>
    public static List<List<Hole>> Cluster(IReadOnlyList<Hole> holes, double distance)
    {
        var d = Math.Max(distance, 1e-6);
        var parent = Enumerable.Range(0, holes.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }
        (long, long) Cell(Vec3 p) => ((long)Math.Floor(p.X / d), (long)Math.Floor(p.Y / d));
        var cells = new Dictionary<(long, long), List<int>>();
        for (int i = 0; i < holes.Count; i++)
        {
            var c = Cell(holes[i].Collar);
            if (!cells.TryGetValue(c, out var list)) cells[c] = list = new List<int>();
            list.Add(i);
        }
        for (int i = 0; i < holes.Count; i++)
        {
            var (cx, cy) = Cell(holes[i].Collar);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                {
                    if (!cells.TryGetValue((cx + dx, cy + dy), out var near)) continue;
                    foreach (var j in near)
                        if (j > i && Vec3.Distance2D(holes[i].Collar, holes[j].Collar) <= d)
                            parent[Find(i)] = Find(j);
                }
        }
        return Enumerable.Range(0, holes.Count).GroupBy(Find).Select(g => g.Select(i => holes[i]).ToList()).ToList();
    }

    /// <summary>
    /// The sources of the zone: the charged holes grouped by the merge distance, numbered west to east (north to south
    /// where they are level), each with the radii of its own charge.
    /// </summary>
    public static List<Source> Sources(BlastProject project)
    {
        Timing.Solve(project);
        var holes = SourceHoles(project);
        if (holes.Count == 0) return new();
        var groups = Cluster(holes, MergeDistance(project, holes))
            .OrderBy(g => Math.Round(g.Average(h => h.Collar.X), 3)).ThenByDescending(g => g.Average(h => h.Collar.Y)).ToList();
        return groups.Select((g, i) =>
        {
            var set = Setting(project, g);
            double? r = set?.Radius is double v && v > 0 && double.IsFinite(v) ? v : null;
            return new Source(i + 1, g, Over(project, g), r, set?.Azimuth ?? 0);
        }).ToList();
    }

    /// <summary>
    /// The area within a radius of a set of points, for each set its own radius, as closed outlines in plan: the hull of a
    /// set grown by its radius with round corners, the sets joined where they overlap. Only the outer edges (a gap ringed by
    /// sources is inside the zone). The outlines stand at the mean height of the points.
    /// </summary>
    public static List<List<Vec3>> Outlines(IEnumerable<(IReadOnlyList<Vec3> Points, double Radius)> parts)
    {
        var list = parts.Where(p => p.Points.Count > 0 && p.Radius > 0 && double.IsFinite(p.Radius)).ToList();
        if (list.Count == 0) return new();
        // about a local origin: plan coordinates of a mine are millions of metres
        var o = list[0].Points[0];
        var grown = new PathsD();
        foreach (var (pts, r) in list)
        {
            var hull = GeoMath.ConvexHull(pts.Select(p => new Vec3(p.X - o.X, p.Y - o.Y, 0)));
            var flat = hull.Count < 3 || GeoMath.Area(hull) < 1e-6;
            var path = new PathD(hull.Select(p => new PointD(p.X, p.Y)));
            // an arc no farther than a thousandth of the radius from its chords
            grown.AddRange(Clipper.InflatePaths(new PathsD { path }, r, JoinType.Round, flat ? EndType.Round : EndType.Polygon, 2, 3, r * 0.001));
        }
        var union = Clipper.Union(grown, new PathsD(), FillRule.NonZero, 3);
        if (union.Count == 0) return new();
        // the outer edges turn the way the largest one does; the edges of gaps the other way
        var outer = Math.Sign(Clipper.Area(union.MaxBy(p => Math.Abs(Clipper.Area(p)))!));
        var z = list.SelectMany(p => p.Points).Average(p => p.Z);
        return union.Where(p => Math.Sign(Clipper.Area(p)) == outer && Math.Abs(Clipper.Area(p)) > 1e-6)
            .OrderByDescending(p => Math.Abs(Clipper.Area(p)))
            .Select(p => p.Select(q => new Vec3(q.x + o.X, q.y + o.Y, z)).ToList()).ToList();
    }

    /// <summary>
    /// The outlines of the zone with their arrows. <see cref="ExclusionSizing.Length"/>: an outline of one source gets its
    /// radius, an outline several sources make its greatest length and, for each source in it, its width there (the piece
    /// of the line square to the length through the middle of the source that lies inside the outline).
    /// <see cref="ExclusionSizing.Radius"/>: every source its radius. A radius arrow goes from the outermost hole of the
    /// source toward its azimuth, the radius long, so its head is on the edge of its circle.
    /// </summary>
    public static List<Zone> Zones(IReadOnlyList<Source> sources, ExclusionSizing size = ExclusionSizing.Length)
    {
        var shown = sources.Where(s => s.Radius > 0 && double.IsFinite(s.Radius)).ToList();
        var outlines = Outlines(shown.Select(s => ((IReadOnlyList<Vec3>)s.Holes.Select(h => h.Collar).ToList(), s.Radius)));
        var zones = new List<Zone>();
        var left = shown.ToList();
        foreach (var outline in outlines)
        {
            var inside = left.Where(s => s.Holes.Any(h => GeoMath.PointInPolygon(outline, h.Collar.X, h.Collar.Y))).ToList();
            left.RemoveAll(inside.Contains);
            var z = outline[0].Z;
            var arrows = new List<ZoneArrow>();
            if (size == ExclusionSizing.Radius || inside.Count == 1) arrows.AddRange(inside.Select(s => RadiusArrow(s, z)));
            else if (inside.Count > 1) arrows.AddRange(LengthAndWidths(outline, inside));
            zones.Add(new Zone(outline, inside, arrows));
        }
        return zones;
    }

    /// <summary>The radius of a source: from its outermost hole toward its azimuth, the radius long.</summary>
    public static ZoneArrow RadiusArrow(Source s, double z)
    {
        var a = s.Azimuth * Math.PI / 180;
        var u = new Vec3(Math.Sin(a), Math.Cos(a), 0);
        var from = s.Holes.MaxBy(h => h.Collar.X * u.X + h.Collar.Y * u.Y)!.Collar.WithZ(z);
        return new ZoneArrow { From = from, To = from + u * s.Radius, Kind = ZoneArrowKind.Radius };
    }

    /// <summary>
    /// The greatest length of an outline, between its two farthest vertices, then for each source its width square to that
    /// length: the piece of the cross line through the middle of the source's holes that lies inside the outline.
    /// </summary>
    public static List<ZoneArrow> LengthAndWidths(IReadOnlyList<Vec3> outline, IReadOnlyList<Source> sources)
    {
        var result = new List<ZoneArrow>();
        if (outline.Count < 3) return result;
        Vec3 a = outline[0], b = outline[0];
        var far = -1.0;
        for (int i = 0; i < outline.Count; i++)
            for (int j = i + 1; j < outline.Count; j++)
            {
                var d = Vec3.Distance2D(outline[i], outline[j]);
                if (d > far) { far = d; a = outline[i]; b = outline[j]; }
            }
        if (far <= 0) return result;
        var z = outline[0].Z;
        a = a.WithZ(z);
        b = b.WithZ(z);
        result.Add(new ZoneArrow { From = a, To = b, Kind = ZoneArrowKind.Length });
        var v = new Vec3(-(b.Y - a.Y) / far, (b.X - a.X) / far, 0);
        foreach (var s in sources)
        {
            var c = new Vec3(s.Holes.Average(h => h.Collar.X), s.Holes.Average(h => h.Collar.Y), z);
            if (Chord(outline, c, v) is { } w) result.Add(new ZoneArrow { From = w.From.WithZ(z), To = w.To.WithZ(z), Kind = ZoneArrowKind.Width });
        }
        return result;
    }

    /// <summary>The piece of the line through <paramref name="c"/> along <paramref name="v"/> inside the outline that holds <paramref name="c"/>.</summary>
    private static (Vec3 From, Vec3 To)? Chord(IReadOnlyList<Vec3> outline, Vec3 c, Vec3 v)
    {
        // where the line meets the edges, along it from c
        var hits = new List<double>();
        for (int i = 0; i < outline.Count; i++)
        {
            var p = outline[i];
            var q = outline[(i + 1) % outline.Count];
            var e = new Vec3(q.X - p.X, q.Y - p.Y, 0);
            var den = v.X * e.Y - v.Y * e.X;
            if (Math.Abs(den) < 1e-12) continue;
            var w = new Vec3(p.X - c.X, p.Y - c.Y, 0);
            var s = (w.X * e.Y - w.Y * e.X) / den;
            var t = (w.X * v.Y - w.Y * v.X) / den;
            if (t >= 0 && t < 1) hits.Add(s);
        }
        hits.Sort();
        // the pieces inside are between the 1st and 2nd crossing, the 3rd and 4th…
        for (int i = 0; i + 1 < hits.Count; i += 2)
            if (hits[i] <= 0 && hits[i + 1] >= 0) return (c + v * hits[i], c + v * hits[i + 1]);
        return null;
    }
}
