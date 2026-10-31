using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>A piece of a survey line, vertices <see cref="From"/>..<see cref="To"/> inclusive.</summary>
public sealed record SurveySegment(Guid LineId, int From, int To);

/// <summary>
/// Features of survey line segments for the recognition of crests and toes (the same code for the training data and
/// the program). Lines are cut into pieces of about <see cref="SegmentLength"/> m. A piece is described by itself
/// (length, slope, wander of the elevation), its neighbours (lines met across it within <see cref="Reach"/> m on each
/// side: distance, height difference, how parallel), the spot heights beside it, the drawing (layer name words) and
/// the holes near it. Only differences, distances and angles are used, so the features do not depend on where the
/// survey is or how it is turned. Sides are named by height: the low side is the one whose neighbour is lower.
/// </summary>
public static class SurveyFeatures
{
    /// <summary>Changes whenever a feature is added, removed or computed differently; a model is tied to it.</summary>
    public const int Version = 1;
    public const double SegmentLength = 15;
    public const double Reach = 30;
    /// <summary>Distance written when nothing is met on a side.</summary>
    public const double NoHit = 40;
    private const double Cell = 10;

    public static readonly string[] Names =
    {
        "seg_length", "line_length", "line_closed", "line_sinuosity", "seg_rel_pos",
        "seg_slope", "seg_roughness", "line_z_range", "seg_z_std", "line_constant_z", "line_spacing_med", "line_spacing_cv",
        "low_hit", "low_dist", "low_dz", "low_parallel", "low_angle",
        "high_hit", "high_dist", "high_dz", "high_parallel", "high_angle",
        "neighbour_lines", "level_parallel",
        "pts_low_count", "pts_low_dz", "pts_high_count", "pts_high_dz", "pts_agree", "pts_on_vertices",
        "layer_crest_word", "layer_toe_word", "layer_edge_word", "layer_contour_word", "layer_road_word", "layer_boundary_word",
        "layer_generic", "layer_share",
        "collar_dist", "collar_dz", "collars_low_side"
    };

    /// <summary>A line of the survey: one with real elevations (lines at 0 are plan drawing).</summary>
    public static bool IsSurveyLine(PolylineEntity line) => line.Points.Count >= 2 && line.Points.Any(p => Math.Abs(p.Z) > 1e-6);

    /// <summary>Cuts a line at vertices into pieces of about <paramref name="target"/> m in plan; a short tail joins the piece before.</summary>
    public static List<SurveySegment> Segments(Guid id, IReadOnlyList<Vec3> pts, double target = SegmentLength)
    {
        var result = new List<SurveySegment>();
        int from = 0;
        double run = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            run += Vec3.Distance2D(pts[i - 1], pts[i]);
            if (run >= target && i < pts.Count - 1)
            {
                result.Add(new SurveySegment(id, from, i));
                from = i;
                run = 0;
            }
        }
        if (from < pts.Count - 1)
        {
            if (result.Count > 0 && run < target / 2) result[^1] = result[^1] with { To = pts.Count - 1 };
            else result.Add(new SurveySegment(id, from, pts.Count - 1));
        }
        return result;
    }

    /// <summary>Features of every segment of every survey line, in the order of <see cref="Names"/>.</summary>
    public static List<(SurveySegment Segment, double[] Values)> Compute(IReadOnlyList<(PolylineEntity Line, string Layer)> lines, IReadOnlyList<Vec3> points, IReadOnlyList<Vec3> collars)
    {
        var survey = lines.Where(l => IsSurveyLine(l.Line)).ToList();
        var index = new EdgeIndex(survey.Select(l => l.Line).ToList());
        var pointGrid = Grid(points);
        var layerCounts = survey.GroupBy(l => l.Layer).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<(SurveySegment, double[])>();
        for (int li = 0; li < survey.Count; li++)
        {
            var (line, layer) = survey[li];
            var pts = line.Points;
            var lineLen = GeoMath.Length(pts, line.Closed);
            var chord = Vec3.Distance2D(pts[0], pts[^1]);
            var zs = pts.Select(p => p.Z).ToList();
            var gaps = new List<double>();
            for (int i = 1; i < pts.Count; i++) gaps.Add(Vec3.Distance2D(pts[i - 1], pts[i]));
            var spacingMed = Median(gaps);
            var spacingCv = gaps.Count > 1 && spacingMed > 0 ? Std(gaps) / gaps.Average() : 0;
            var words = LayerWords(layer);
            var cumulative = 0.0;
            foreach (var seg in Segments(line.Id, pts))
            {
                var sp = pts.Skip(seg.From).Take(seg.To - seg.From + 1).ToList();
                var segLen = GeoMath.Length(sp);
                var segStart = cumulative;
                cumulative += segLen;
                var v = new List<double>(Names.Length)
                {
                    segLen, lineLen, line.Closed ? 1 : 0, Math.Min(10, chord > 1e-6 ? lineLen / chord : 10), lineLen > 0 ? (segStart + segLen / 2) / lineLen : 0.5,
                    segLen > 0 ? Math.Abs(sp[^1].Z - sp[0].Z) / segLen : 0, Roughness(sp), zs.Max() - zs.Min(), Std(sp.Select(p => p.Z).ToList()),
                    zs.Max() - zs.Min() < 0.05 ? 1 : 0, spacingMed, spacingCv
                };

                // neighbours across the segment, both sides
                var hits = new[] { new List<Hit>(), new List<Hit>() };
                foreach (var f in new[] { 0.1, 0.3, 0.5, 0.7, 0.9 })
                {
                    var q = GeoMath.PointAlong(sp, f * segLen, out var dir);
                    dir = dir.WithZ(0).Normalized();
                    var n = new Vec3(-dir.Y, dir.X, 0);
                    for (int side = 0; side < 2; side++)
                        if (index.Cast(q, side == 0 ? n : -n, Reach, li, dir) is { } h) hits[side].Add(h);
                }
                var sides = hits.Select(h => (Hit: h.Count / 5.0, Dist: h.Count > 0 ? Median(h.Select(x => x.Dist).ToList()) : NoHit,
                    Dz: h.Count > 0 ? Median(h.Select(x => x.Dz).ToList()) : 0, Parallel: h.Count > 0 ? Median(h.Select(x => x.Parallel).ToList()) : 0)).ToArray();
                // the low side: the lower neighbour; a single neighbour above makes the other side the low one
                int low = sides[0].Hit > 0 && sides[1].Hit > 0 ? (sides[0].Dz <= sides[1].Dz ? 0 : 1)
                    : sides[0].Hit > 0 ? (sides[0].Dz < 0 ? 0 : 1) : sides[1].Hit > 0 ? (sides[1].Dz < 0 ? 1 : 0) : 0;
                foreach (var s in new[] { sides[low], sides[1 - low] })
                    v.AddRange(new[] { s.Hit, s.Dist, s.Dz, s.Parallel, s.Hit > 0 ? Math.Atan2(Math.Abs(s.Dz), Math.Max(0.01, s.Dist)) * 180 / Math.PI : 0 });
                var all = hits[0].Concat(hits[1]).ToList();
                v.Add(all.Select(h => h.Line).Distinct().Count());
                v.Add(all.Count == 0 ? 0 : all.Count(h => Math.Abs(h.Dz) < 1) / (double)all.Count);

                // spot heights beside the segment: 1–20 m off, within its length
                var byside = new[] { new List<double>(), new List<double>() };
                var onVertices = 0;
                foreach (var p in Near(pointGrid, sp, 20))
                {
                    if (sp.Any(x => Vec3.Distance2D(x, p) < 0.05)) { onVertices++; continue; }
                    var (dist, side, z, along) = Beside(sp, p);
                    if (dist < 1 || dist > 20 || along < -2 || along > segLen + 2) continue;
                    byside[side].Add(p.Z - z);
                }
                var meds = byside.Select(b => b.Count > 0 ? Median(b) : 0).ToArray();
                int plow = byside[0].Count > 0 && byside[1].Count > 0 ? (meds[0] <= meds[1] ? 0 : 1)
                    : byside[0].Count > 0 ? (meds[0] < 0 ? 0 : 1) : byside[1].Count > 0 ? (meds[1] < 0 ? 1 : 0) : low;
                v.AddRange(new double[] { byside[plow].Count, meds[plow], byside[1 - plow].Count, meds[1 - plow] });
                v.Add(byside[0].Count + byside[1].Count == 0 || sides[0].Hit + sides[1].Hit == 0 ? 0.5 : plow == low ? 1 : 0);
                v.Add(onVertices / (double)sp.Count);

                // the drawing
                v.AddRange(words);
                v.Add(layerCounts[layer] / (double)survey.Count);

                // holes: nearest collar, collars' height against the segment, on which side they stand
                var mid = sp[sp.Count / 2];
                var nearC = collars.Where(c => Vec3.Distance2D(c, mid) <= 40).ToList();
                v.Add(collars.Count == 0 ? 60 : Math.Min(60, collars.Min(c => Vec3.Distance2D(c, mid))));
                v.Add(nearC.Count == 0 ? 0 : Median(nearC.Select(c => c.Z - mid.Z).ToList()));
                v.Add(nearC.Count == 0 ? 0.5 : nearC.Count(c => Beside(sp, c).Side == low) / (double)nearC.Count);

                result.Add((seg, v.ToArray()));
            }
        }
        return result;
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static double[] LayerWords(string layer)
    {
        var n = layer.Trim().ToLowerInvariant();
        bool Has(params string[] words) => words.Any(n.Contains);
        bool Token(params string[] tokens) => n.Split(' ', '_', '-', '.').Any(tokens.Contains);
        var generic = n.Length == 0 || n.All(char.IsDigit) || System.Text.RegularExpressions.Regex.IsMatch(n, @"^(layer|слой)\s*\d*$")
            || Has("survey", "съемк", "съёмк", "пикет", "points", "точки");
        return new double[]
        {
            Has("верх", "crest", "top") || Token("вб") ? 1 : 0,
            Has("низ", "toe", "bottom", "подошв") || Token("нб") ? 1 : 0,
            Has("бровк", "bench", "уступ") ? 1 : 0,
            Has("горизонт", "contour", "рельеф", "topo") ? 1 : 0,
            Has("дорог", "съезд", "road", "ramp") ? 1 : 0,
            Has("контур", "границ", "блок", "blast", "boundar") ? 1 : 0,
            generic ? 1 : 0
        };
    }

    /// <summary>Distance, side (0 left of the direction of travel, 1 right), line elevation and distance along for a point beside a polyline.</summary>
    private static (double Dist, int Side, double Z, double Along) Beside(IReadOnlyList<Vec3> line, Vec3 p)
    {
        double best = double.MaxValue, z = 0, along = 0, run = 0;
        int side = 0;
        for (int i = 1; i < line.Count; i++)
        {
            var (c, t) = GeoMath.ClosestOnSegment2D(line[i - 1], line[i], p);
            var d = Vec3.Distance2D(c, p);
            var len = Vec3.Distance2D(line[i - 1], line[i]);
            if (d < best)
            {
                best = d;
                z = line[i - 1].Z + (line[i].Z - line[i - 1].Z) * t;
                along = run + t * len;
                var e = line[i] - line[i - 1];
                side = e.X * (p.Y - line[i - 1].Y) - e.Y * (p.X - line[i - 1].X) >= 0 ? 0 : 1;
            }
            run += len;
        }
        return (best, side, z, along);
    }

    private static double Roughness(IReadOnlyList<Vec3> pts)
    {
        // mean change of slope between consecutive edges
        var slopes = new List<double>();
        for (int i = 1; i < pts.Count; i++)
        {
            var d = Vec3.Distance2D(pts[i - 1], pts[i]);
            if (d > 1e-6) slopes.Add((pts[i].Z - pts[i - 1].Z) / d);
        }
        if (slopes.Count < 2) return 0;
        double s = 0;
        for (int i = 1; i < slopes.Count; i++) s += Math.Abs(slopes[i] - slopes[i - 1]);
        return s / (slopes.Count - 1);
    }

    private static Dictionary<(long, long), List<Vec3>> Grid(IEnumerable<Vec3> points)
    {
        var g = new Dictionary<(long, long), List<Vec3>>();
        foreach (var p in points)
        {
            var k = ((long)Math.Floor(p.X / Cell), (long)Math.Floor(p.Y / Cell));
            if (!g.TryGetValue(k, out var l)) g[k] = l = new();
            l.Add(p);
        }
        return g;
    }

    private static IEnumerable<Vec3> Near(Dictionary<(long, long), List<Vec3>> grid, IReadOnlyList<Vec3> line, double margin)
    {
        var (x0, y0, x1, y1) = (line.Min(p => p.X) - margin, line.Min(p => p.Y) - margin, line.Max(p => p.X) + margin, line.Max(p => p.Y) + margin);
        for (var cx = (long)Math.Floor(x0 / Cell); cx <= (long)Math.Floor(x1 / Cell); cx++)
            for (var cy = (long)Math.Floor(y0 / Cell); cy <= (long)Math.Floor(y1 / Cell); cy++)
                if (grid.TryGetValue((cx, cy), out var l))
                    foreach (var p in l) yield return p;
    }

    public static double Median(List<double> v)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static double Std(List<double> v)
    {
        if (v.Count < 2) return 0;
        var m = v.Average();
        return Math.Sqrt(v.Average(x => (x - m) * (x - m)));
    }

    /// <summary>A line met across a segment: plan distance, its height above the segment, |cos| of the angle between them, which line.</summary>
    private readonly record struct Hit(double Dist, double Dz, double Parallel, int Line);

    /// <summary>Edges of all survey lines in a plan grid, for casting rays across a line.</summary>
    private sealed class EdgeIndex
    {
        private readonly List<PolylineEntity> _lines;
        private readonly Dictionary<(long, long), List<(int Line, int Edge)>> _cells = new();

        public EdgeIndex(List<PolylineEntity> lines)
        {
            _lines = lines;
            for (int li = 0; li < lines.Count; li++)
            {
                var pts = lines[li].Points;
                var n = lines[li].Closed ? pts.Count : pts.Count - 1;
                for (int i = 0; i < n; i++)
                {
                    var (a, b) = (pts[i], pts[(i + 1) % pts.Count]);
                    for (var cx = (long)Math.Floor(Math.Min(a.X, b.X) / Cell); cx <= (long)Math.Floor(Math.Max(a.X, b.X) / Cell); cx++)
                        for (var cy = (long)Math.Floor(Math.Min(a.Y, b.Y) / Cell); cy <= (long)Math.Floor(Math.Max(a.Y, b.Y) / Cell); cy++)
                        {
                            if (!_cells.TryGetValue((cx, cy), out var l)) _cells[(cx, cy)] = l = new();
                            l.Add((li, i));
                        }
                }
            }
        }

        /// <summary>The nearest edge of another line crossed by the ray from <paramref name="q"/> along <paramref name="dir"/>.</summary>
        public Hit? Cast(Vec3 q, Vec3 dir, double reach, int self, Vec3 along)
        {
            var e = q + dir * reach;
            Hit? best = null;
            var bestT = double.MaxValue;
            var seen = new HashSet<(int, int)>();
            for (var cx = (long)Math.Floor(Math.Min(q.X, e.X) / Cell); cx <= (long)Math.Floor(Math.Max(q.X, e.X) / Cell); cx++)
                for (var cy = (long)Math.Floor(Math.Min(q.Y, e.Y) / Cell); cy <= (long)Math.Floor(Math.Max(q.Y, e.Y) / Cell); cy++)
                {
                    if (!_cells.TryGetValue((cx, cy), out var l)) continue;
                    foreach (var (li, ei) in l)
                    {
                        if (li == self || !seen.Add((li, ei))) continue;
                        var pts = _lines[li].Points;
                        var (a, b) = (pts[ei], pts[(ei + 1) % pts.Count]);
                        if (!GeoMath.SegmentIntersect2D(q, e, a, b, out var t, out var u) || t < 1e-6 || t >= bestT) continue;
                        bestT = t;
                        var ed = (b - a).WithZ(0).Normalized();
                        best = new Hit(t * reach, a.Z + (b.Z - a.Z) * u - q.Z, Math.Abs(Vec3.Dot(ed, along)), li);
                    }
                }
            return best;
        }
    }
}

/// <summary>The "handwriting" of a survey, measured the same way on a real drawing and on synthetic ones.</summary>
public sealed record SurveyStats(
    int Lines, int Points, double SpacingP10, double SpacingMedian, double SpacingP90,
    double ZRangeMedian, double ZRangeP90, int CrestLike, int ToeLike, int Middle, int Lonely,
    double NeighbourDzMedian, double NeighbourDistMedian, double FaceAngleP10, double FaceAngleMedian, double FaceAngleP90,
    double PointsOnVertices, double TwinShare)
{
    /// <summary>Measures survey lines (with real elevations) and points: spacing, wander, neighbours across, face angles.</summary>
    public static SurveyStats Measure(IReadOnlyList<PolylineEntity> lines, IReadOnlyList<Vec3> points)
    {
        var survey = lines.Where(SurveyFeatures.IsSurveyLine).ToList();
        var features = SurveyFeatures.Compute(survey.Select(l => (l, "0")).ToList(), Array.Empty<Vec3>(), Array.Empty<Vec3>());
        var spacing = new List<double>();
        foreach (var l in survey)
            for (int i = 1; i < l.Points.Count; i++) spacing.Add(Vec3.Distance2D(l.Points[i - 1], l.Points[i]));
        var ranges = survey.Select(l => l.Points.Max(p => p.Z) - l.Points.Min(p => p.Z)).ToList();
        int F(string name) => Array.IndexOf(SurveyFeatures.Names, name);
        // a line counts as crest-like when most of its length has a lower neighbour 2–25 m down, and so on
        // share of length with another line within 3 m at the same level (a twin: the edge surveyed twice)
        double twin = 0, length = 0;
        foreach (var (_, v) in features)
        {
            var near = (v[F("low_hit")] > 0 && v[F("low_dist")] < 3 && Math.Abs(v[F("low_dz")]) < 0.7) || (v[F("high_hit")] > 0 && v[F("high_dist")] < 3 && Math.Abs(v[F("high_dz")]) < 0.7);
            length += v[F("seg_length")];
            if (near) twin += v[F("seg_length")];
        }
        int crest = 0, toe = 0, middle = 0, lonely = 0;
        var dz = new List<double>();
        var dist = new List<double>();
        var angles = new List<double>();
        foreach (var g in features.GroupBy(x => x.Segment.LineId))
        {
            var segs = g.ToList();
            if (segs.Sum(s => s.Values[F("seg_length")]) < 20) continue;
            bool Below((SurveySegment, double[] V) s) => s.V[F("low_hit")] > 0 && s.V[F("low_dz")] < -2 && s.V[F("low_dz")] > -25;
            bool Above((SurveySegment, double[] V) s) => s.V[F("high_hit")] > 0 && s.V[F("high_dz")] > 2 && s.V[F("high_dz")] < 25;
            double below = segs.Count(Below) / (double)segs.Count, above = segs.Count(Above) / (double)segs.Count;
            if (below > 0.4 && above > 0.4) middle++; else if (below > 0.4) crest++; else if (above > 0.4) toe++; else lonely++;
            foreach (var s in segs.Where(Below))
            {
                dz.Add(-s.Values[F("low_dz")]);
                dist.Add(s.Values[F("low_dist")]);
                angles.Add(s.Values[F("low_angle")]);
            }
        }
        var grid = new HashSet<(long, long)>(survey.SelectMany(l => l.Points).Select(p => ((long)Math.Round(p.X * 20), (long)Math.Round(p.Y * 20))));
        var onVertex = points.Count == 0 ? 0 : points.Count(p => grid.Contains(((long)Math.Round(p.X * 20), (long)Math.Round(p.Y * 20)))) / (double)points.Count;
        return new SurveyStats(survey.Count, points.Count, Q(spacing, 0.1), Q(spacing, 0.5), Q(spacing, 0.9), Q(ranges, 0.5), Q(ranges, 0.9),
            crest, toe, middle, lonely, Q(dz, 0.5), Q(dist, 0.5), Q(angles, 0.1), Q(angles, 0.5), Q(angles, 0.9), onVertex, length > 0 ? twin / length : 0);
    }

    private static double Q(List<double> v, double q)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s[(int)Math.Min(s.Count - 1, q * s.Count)];
    }
}
