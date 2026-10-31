using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>The class of a line segment and how sure the recognition is (0–1); below <see cref="SurveyRecognizer.CheckBelow"/> it wants a look.</summary>
public sealed record SegmentGuess(SurveySegment Segment, SurveyLineClass Class, double Confidence);

/// <summary>A bench found in the survey: its crest line, the toe line under it (if surveyed) and its shape.</summary>
/// <param name="Height">Crest above toe (or above the floor pickets when the toe is missing), m.</param>
/// <param name="FaceWidth">Plan distance crest → toe, m; NaN without a toe.</param>
/// <param name="FaceAngle">Degrees; NaN without a toe.</param>
public sealed record BenchElement(Guid Crest, Guid? Toe, double Height, double FaceWidth, double FaceAngle);

/// <summary>What the recognition found in a survey.</summary>
public sealed class SurveyRecognition
{
    public List<SegmentGuess> Segments { get; } = new();
    public Dictionary<Guid, (SurveyPointClass Class, double Confidence)> Points { get; } = new();
    public List<BenchElement> Benches { get; } = new();
    public bool FromModel { get; init; }

    /// <summary>The class of most of a line's length.</summary>
    public SurveyLineClass? LineClass(Guid line, IReadOnlyList<Vec3> pts)
    {
        var parts = Segments.Where(s => s.Segment.LineId == line).ToList();
        if (parts.Count == 0) return null;
        return parts.GroupBy(s => s.Class).OrderByDescending(g => g.Sum(s => Length(pts, s.Segment))).First().Key;
    }

    public static double Length(IReadOnlyList<Vec3> pts, SurveySegment s)
    {
        double d = 0;
        for (int i = s.From + 1; i <= s.To && i < pts.Count; i++) d += Vec3.Distance2D(pts[i - 1], pts[i]);
        return d;
    }
}

/// <summary>
/// Recognition of crests and toes in a mine survey by rules on the segment features (<see cref="SurveyFeatures"/>):
/// a crest has the ground dropping a bench height on one side (a lower line close by and steep, or lower pickets), a
/// toe has it rising; contours keep one elevation; a closed line with level ground round it is a boundary. Segment
/// classes are then smoothed along each line, pickets are placed against the crests found, and crests are paired with
/// the toes under them into benches. Also the fallback whenever the model is missing.
/// </summary>
public static class SurveyRecognizer
{
    /// <summary>Segments less sure than this are marked "check".</summary>
    public const double CheckBelow = 0.5;

    private static int F(string name) => Array.IndexOf(SurveyFeatures.Names, name);
    private static readonly int LowHit = F("low_hit"), LowDz = F("low_dz"), LowAngle = F("low_angle"), LowDist = F("low_dist");
    private static readonly int HighHit = F("high_hit"), HighDz = F("high_dz"), HighAngle = F("high_angle"), HighDist = F("high_dist");
    private static readonly int PtsLowCount = F("pts_low_count"), PtsLowDz = F("pts_low_dz"), PtsHighCount = F("pts_high_count"), PtsHighDz = F("pts_high_dz");
    private static readonly int ConstantZ = F("line_constant_z"), Closed = F("line_closed"), ZRange = F("line_z_range"), LevelParallel = F("level_parallel"), LineLength = F("line_length");
    private static readonly int CrestWord = F("layer_crest_word"), ToeWord = F("layer_toe_word"), ContourWord = F("layer_contour_word"), BoundaryWord = F("layer_boundary_word");

    /// <param name="features">Segment features already computed for the same input (training data); computed here when null.</param>
    public static SurveyRecognition Recognize(IReadOnlyList<(PolylineEntity Line, string Layer)> lines, IReadOnlyList<PointEntity> points, IReadOnlyList<Vec3> collars,
        List<(SurveySegment Segment, double[] Values)>? features = null)
    {
        var heights = points.Where(p => Math.Abs(p.Position.Z) > 1e-6).ToList();
        features ??= SurveyFeatures.Compute(lines, heights.Select(p => p.Position).ToList(), collars);
        var result = new SurveyRecognition();
        foreach (var g in features.GroupBy(f => f.Segment.LineId))
        {
            var guesses = g.Select(f => Classify(f.Segment, f.Values)).ToList();
            result.Segments.AddRange(Smooth(guesses));
        }
        Refresh(result, lines.Select(l => l.Line).ToList(), points);
        return result;
    }

    /// <summary>Benches and picket classes again from the segment classes (after the user changed some).</summary>
    public static void Refresh(SurveyRecognition result, IReadOnlyList<PolylineEntity> lines, IReadOnlyList<PointEntity> points)
    {
        var heights = points.Where(p => Math.Abs(p.Position.Z) > 1e-6).ToList();
        var byId = lines.ToDictionary(l => l.Id);
        result.Benches.Clear();
        result.Points.Clear();
        Benches(result, byId, heights);
        PlacePoints(result, byId, heights);
    }

    /// <summary>Scores of the classes for one segment; the best one wins, its margin over the second is the confidence.</summary>
    public static SegmentGuess Classify(SurveySegment seg, double[] v)
    {
        // a drop on the low side / a rise on the high side: the neighbour line (2–25 m, steep) or the pickets
        static double Band(double x, double lo, double hi, double soft) => x < lo - soft || x > hi + soft ? 0 : x < lo ? (x - lo + soft) / soft : x > hi ? (hi + soft - x) / soft : 1;
        var lineDrop = v[LowHit] * Band(-v[LowDz], 2.5, 25, 1.5) * Band(v[LowAngle], 22, 85, 10);
        var lineRise = v[HighHit] * Band(v[HighDz], 2.5, 25, 1.5) * Band(v[HighAngle], 22, 85, 10);
        var ptsDrop = Math.Min(1, v[PtsLowCount] / 2) * Band(-v[PtsLowDz], 2, 25, 1.5);
        var ptsRise = Math.Min(1, v[PtsHighCount] / 2) * Band(v[PtsHighDz], 2, 25, 1.5);
        // the other side of a crest is level ground (its bench), of a toe level ground (the berm or floor)
        var levelHigh = v[PtsHighCount] > 0 ? Band(Math.Abs(v[PtsHighDz]), 0, 1.5, 1.5) : 0.5;
        var levelLow = v[PtsLowCount] > 0 ? Band(Math.Abs(v[PtsLowDz]), 0, 1.5, 1.5) : 0.5;

        var crest = Math.Max(lineDrop, ptsDrop * 0.9) * (0.6 + 0.4 * levelHigh) + 0.15 * v[CrestWord];
        var toe = Math.Max(lineRise, ptsRise * 0.9) * (0.6 + 0.4 * levelLow) + 0.15 * v[ToeWord];
        // pickets on the wrong side overrule the lines: ground rising behind a "crest" or dropping in front of a "toe"
        if (ptsRise > 0.5 && ptsDrop < 0.3) crest *= 0.5;
        if (ptsDrop > 0.5 && ptsRise < 0.3) toe *= 0.5;
        // level pickets right behind a "toe" are a berm: it is the crest of a lower bench whose own toe was not surveyed,
        // looking up at the bench above; level pickets in front of a "crest" likewise
        var flatHigh = v[PtsHighCount] > 0 ? Band(Math.Abs(v[PtsHighDz]), 0, 1.2, 1.0) : 0;
        var flatLow = v[PtsLowCount] > 0 ? Band(Math.Abs(v[PtsLowDz]), 0, 1.2, 1.0) : 0;
        if (lineDrop < 0.3) toe *= 1 - 0.6 * flatHigh;
        if (lineRise < 0.3) crest *= 1 - 0.6 * flatLow;
        // a segment with a drop on one side and a rise on the other sits mid-face: the nearer, steeper neighbour is its face
        if (lineDrop > 0.3 && lineRise > 0.3)
        {
            var faceBelow = v[LowAngle] / Math.Max(1, v[LowDist]);
            var faceAbove = v[HighAngle] / Math.Max(1, v[HighDist]);
            if (faceBelow > faceAbove) toe *= 0.6; else crest *= 0.6;
        }
        // one elevation all along: surveyed lines never keep it to the centimetre, interpolated contours always do
        var contour = v[ConstantZ] * (1.2 + 0.3 * v[LevelParallel]) + 0.2 * v[ContourWord];
        // a short closed line at one level (a blast outline); a whole pit's crest closes too, but is long and steps down
        var boundary = v[Closed] * Band(v[ZRange], 0, 2, 2) * (v[LineLength] < 600 ? 1.1 : (1 - Math.Max(lineDrop, lineRise)) * 0.8) + 0.2 * v[BoundaryWord];
        var other = 0.35;

        var scores = new (SurveyLineClass Class, double Score)[]
        {
            (SurveyLineClass.Crest, crest), (SurveyLineClass.Toe, toe), (SurveyLineClass.Contour, contour),
            (SurveyLineClass.Boundary, boundary), (SurveyLineClass.Other, other)
        }.OrderByDescending(s => s.Score).ToArray();
        // how far the winner is ahead of the runner-up, relative to its own score
        var confidence = scores[0].Score <= 0 ? 0 : Math.Clamp((scores[0].Score - scores[1].Score) / scores[0].Score, 0, 1);
        return new SegmentGuess(seg, scores[0].Class, confidence);
    }

    /// <summary>Majority of the five nearest segments along the line; a lone different segment takes its neighbours' class.</summary>
    private static IEnumerable<SegmentGuess> Smooth(List<SegmentGuess> line)
    {
        for (int i = 0; i < line.Count; i++)
        {
            var window = line.Skip(Math.Max(0, i - 2)).Take(Math.Min(line.Count, i + 3) - Math.Max(0, i - 2)).ToList();
            var vote = window.GroupBy(s => s.Class).Select(g => (g.Key, Weight: g.Sum(s => s.Confidence))).OrderByDescending(x => x.Weight).First();
            var own = line[i];
            if (vote.Key == own.Class || vote.Weight < window.Sum(s => s.Confidence) * 0.6) yield return own;
            else yield return own with { Class = vote.Key, Confidence = Math.Min(own.Confidence, vote.Weight / window.Sum(s => s.Confidence)) };
        }
    }

    /// <summary>Each crest line with the toe line most often found under it on its low side, or the floor pickets.</summary>
    private static void Benches(SurveyRecognition r, Dictionary<Guid, PolylineEntity> lines, List<PointEntity> points)
    {
        var classOf = lines.Keys.ToDictionary(id => id, id => r.LineClass(id, lines[id].Points));
        var toes = lines.Values.Where(l => classOf[l.Id] == SurveyLineClass.Toe).ToList();
        foreach (var crest in lines.Values.Where(l => classOf[l.Id] == SurveyLineClass.Crest))
        {
            var found = new List<(Guid Toe, double Dz, double Dist)>();
            var drops = new List<double>();
            for (int i = 0; i < crest.Points.Count; i += Math.Max(1, crest.Points.Count / 12))
            {
                var c = crest.Points[i];
                (Guid, double, double)? best = null;
                foreach (var t in toes)
                {
                    var d = GeoMath.DistanceToPolyline2D(t.Points, c, t.Closed);
                    if (d > 40) continue;
                    var tz = NearestZ(t.Points, c);
                    var dz = c.Z - tz;
                    if (dz < 2 || dz > 30) continue;
                    if (best == null || d < best.Value.Item3) best = (t.Id, dz, d);
                }
                if (best is { } b) found.Add(b);
                else
                {
                    // no toe: the floor pickets 5–40 m off, a bench height down
                    var below = points.Select(p => (p, d: Vec3.Distance2D(p.Position, c))).Where(x => x.d > 3 && x.d < 40 && c.Z - x.p.Position.Z > 2).Select(x => c.Z - x.p.Position.Z).ToList();
                    if (below.Count > 0) drops.Add(SurveyFeatures.Median(below));
                }
            }
            if (found.Count >= 2)
            {
                var toe = found.GroupBy(f => f.Toe).OrderByDescending(g => g.Count()).First();
                var h = SurveyFeatures.Median(toe.Select(f => f.Dz).ToList());
                var w = SurveyFeatures.Median(toe.Select(f => f.Dist).ToList());
                r.Benches.Add(new BenchElement(crest.Id, toe.Key, h, w, Math.Atan2(h, Math.Max(0.01, w)) * 180 / Math.PI));
            }
            else if (drops.Count > 0) r.Benches.Add(new BenchElement(crest.Id, null, SurveyFeatures.Median(drops), double.NaN, double.NaN));
        }
    }

    /// <summary>Pickets against the nearest crest: on its vertices, level with it behind it (bench top), below it (floor), between crest and toe (face).</summary>
    private static void PlacePoints(SurveyRecognition r, Dictionary<Guid, PolylineEntity> lines, List<PointEntity> points)
    {
        var crests = r.Benches.Select(b => (Crest: lines[b.Crest], Toe: b.Toe is Guid t ? lines[t] : null, b.Height)).ToList();
        var vertices = new HashSet<(long, long)>(lines.Values.Where(SurveyFeatures.IsSurveyLine).SelectMany(l => l.Points).Select(p => ((long)Math.Round(p.X * 20), (long)Math.Round(p.Y * 20))));
        foreach (var p in points)
        {
            var q = p.Position;
            if (vertices.Contains(((long)Math.Round(q.X * 20), (long)Math.Round(q.Y * 20)))) { r.Points[p.Id] = (SurveyPointClass.OnLine, 1); continue; }
            var near = crests.Select(c => (c, d: GeoMath.DistanceToPolyline2D(c.Crest.Points, q, c.Crest.Closed))).Where(x => x.d < 40).OrderBy(x => x.d).FirstOrDefault();
            if (near.c.Crest == null) { r.Points[p.Id] = (SurveyPointClass.Other, 0.4); continue; }
            var dz = q.Z - NearestZ(near.c.Crest.Points, q);
            var face = near.c.Toe != null && GeoMath.DistanceToPolyline2D(near.c.Toe.Points, q, near.c.Toe.Closed) < near.d && dz < -1 && dz > -near.c.Height + 1;
            r.Points[p.Id] = face ? (SurveyPointClass.Face, 0.6)
                : Math.Abs(dz) < 1.5 ? (SurveyPointClass.BenchTop, 0.7)
                : dz < -1.5 ? (SurveyPointClass.Floor, 0.7)
                : (SurveyPointClass.Other, 0.4);
        }
    }

    private static double NearestZ(IReadOnlyList<Vec3> line, Vec3 p)
    {
        double best = double.MaxValue, z = 0;
        for (int i = 1; i < line.Count; i++)
        {
            var (c, t) = GeoMath.ClosestOnSegment2D(line[i - 1], line[i], p);
            var d = Vec3.Distance2D(c, p);
            if (d < best) { best = d; z = line[i - 1].Z + (line[i].Z - line[i - 1].Z) * t; }
        }
        return z;
    }
}
