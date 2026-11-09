using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

public sealed class TimingLineOptions
{
    public TimingLineStyle Style { get; set; } = TimingLineStyle.TimeLine;
    /// <summary>Time of the first-firing hole, ms.</summary>
    public double StartOffsetMs { get; set; }
    public bool UseProgressive { get; set; }
    /// <summary>Progressive exponent index: 0 → 1.1, 1 → 1.2, 2 → 1.4, 3 → 1.6, otherwise 2.0.</summary>
    public int ProgressRate { get; set; } = 1;
    /// <summary>Walk the line from its last point to its first (time and burn lines).</summary>
    public bool ReverseOrder { get; set; }
    /// <summary>Advanced burn: relief along the line, ms/m.</summary>
    public double BurdenReliefMsPerM { get; set; } = 8;
    /// <summary>Advanced burn: angles of the left and right side fronts, degrees.</summary>
    public double Angle1Deg { get; set; }
    public double Angle2Deg { get; set; }
    /// <summary>Holes whose time was set by hand keep it.</summary>
    public bool KeepManualTimes { get; set; } = true;
}

public sealed record TimingLineResult(int Timed, int SkippedManual, int OutOfReach, List<Hole> MaxTimeExceeded);

/// <summary>
/// Electronic timing line: the points picked in plan, the timing values of each segment, and the options for how to apply them.
/// </summary>
public static class EbsTimingLine
{
    public const int MaxPoints = 4;
    const double Invalid = 1e32;

    /// <summary>Point used for timing: the first firing primer (its plan position), else the collar.</summary>
    public static Vec3 TimingPoint(BlastProject project, Hole h)
    {
        var primer = Charging.Primers(h).OrderBy(p => Charging.PrimerDelay(project, p.Primer)).ThenByDescending(p => p.Primer.Depth).FirstOrDefault();
        return primer.Primer != null ? h.PointAtDepth(primer.Primer.Depth) : h.Collar;
    }

    /// <summary>Raw (un-normalised) time of every point; values ≥ 1e30 in magnitude mean the point is out of reach.</summary>
    public static double[] RawTimes(IReadOnlyList<Vec3> line, IReadOnlyList<TimingLineSegment> data, TimingLineOptions o, IReadOnlyList<Vec3> points, IReadOnlyList<Vec3>? advPoints = null)
    {
        if (line.Count < 2) throw new ArgumentException("A timing line needs at least two points.");
        if (data.Count < line.Count - 1) throw new ArgumentException("Every segment needs timing values.");
        if (o.Style == TimingLineStyle.AdvancedBurn)
        {
            var br = o.BurdenReliefMsPerM;
            return AdvancedTimes(line, br, br, br * Math.Tan(o.Angle1Deg * GeoMath.Deg), br * Math.Tan(o.Angle2Deg * GeoMath.Deg), 0, false, true, advPoints ?? points);
        }
        return LineTimes(line, data, o, points);
    }

    /// <summary>
    /// Programs electronic times of the holes. Times are shifted so the earliest of all holes in reach
    /// (manual ones included) fires at the start offset; holes past <paramref name="maxTimeMs"/> are reported and left as they were.
    /// </summary>
    public static TimingLineResult Apply(BlastProject project, IReadOnlyList<Hole> holes, IReadOnlyList<Vec3> line, IReadOnlyList<TimingLineSegment> data, TimingLineOptions o, double maxTimeMs, double roundToMs)
    {
        var (times, result) = Plan(project, holes, line, data, o, maxTimeMs, roundToMs);
        foreach (var h in holes)
            if (times.TryGetValue(h.Id, out var t)) Timing.SetTime(h, t, false);
        return result;
    }

    /// <summary>
    /// The times <see cref="Apply"/> would program, without changing the holes (for the preview in the timing line window).
    /// </summary>
    public static (Dictionary<Guid, double> Times, TimingLineResult Result) Plan(BlastProject project, IReadOnlyList<Hole> holes, IReadOnlyList<Vec3> line, IReadOnlyList<TimingLineSegment> data, TimingLineOptions o, double maxTimeMs, double roundToMs)
    {
        // Time / burn lines use the firing point, the advanced burn uses the collar.
        var raw = RawTimes(line, data, o, holes.Select(h => TimingPoint(project, h)).ToList(), holes.Select(h => h.Collar).ToList());
        double min = Invalid;
        foreach (var t in raw) if (Math.Abs(t) < 1e30 && t < min) min = t;
        var shift = min - o.StartOffsetMs;
        int manual = 0, outOfReach = 0;
        var exceeded = new List<Hole>();
        var times = new Dictionary<Guid, double>();
        for (int i = 0; i < holes.Count; i++)
        {
            var h = holes[i];
            if (o.KeepManualTimes && h.ElectronicTimeManual && h.ElectronicTimeMs.HasValue) { manual++; continue; }
            if (Math.Abs(raw[i]) >= 1e30) { outOfReach++; continue; }
            var t = raw[i] - shift;
            if (t > maxTimeMs) { exceeded.Add(h); continue; }
            if (t <= -0.001) { outOfReach++; continue; }
            if (roundToMs > 0) t = Math.Round(t / roundToMs) * roundToMs;
            times[h.Id] = t;
        }
        return (times, new TimingLineResult(times.Count, manual, outOfReach, exceeded));
    }

    /// <summary>Builds the timing line object kept in the project from the picked points and the chosen values.</summary>
    public static TimingLineEntity CreateEntity(IReadOnlyList<Vec3> points, IReadOnlyList<TimingLineSegment> data, TimingLineOptions o) => new()
    {
        Points = points.ToList(),
        Segments = data.ToList(),
        Style = o.Style,
        StartOffsetMs = o.StartOffsetMs,
        UseProgressive = o.UseProgressive,
        ProgressRate = o.ProgressRate,
        ReverseOrder = o.ReverseOrder,
        BurdenReliefMsPerM = o.BurdenReliefMsPerM,
        Angle1Deg = o.Angle1Deg,
        Angle2Deg = o.Angle2Deg
    };

    public static TimingLineOptions OptionsOf(TimingLineEntity e) => new()
    {
        Style = e.Style,
        StartOffsetMs = e.StartOffsetMs,
        UseProgressive = e.UseProgressive,
        ProgressRate = e.ProgressRate,
        ReverseOrder = e.ReverseOrder,
        BurdenReliefMsPerM = e.BurdenReliefMsPerM,
        Angle1Deg = e.Angle1Deg,
        Angle2Deg = e.Angle2Deg
    };

    /// <summary>Programs the holes from a timing line kept in the project.</summary>
    public static TimingLineResult Apply(BlastProject project, IReadOnlyList<Hole> holes, TimingLineEntity line) =>
        Apply(project, holes, line.Points, line.Segments, OptionsOf(line), project.Electronic.MaxTimeMs, project.Electronic.TimeStepMs);

    static Vec3 Flat(Vec3 v) => new(v.X, v.Y, 0);
    static Vec3 Unit2D(Vec3 v) => Flat(v).Normalized();
    static double Prog(double distance, double progressMs, double exponent) => Math.Exp(exponent * Math.Log(Math.Abs(distance))) * progressMs / Math.Exp(exponent * Math.Log(10.0));

    static double[] LineTimes(IReadOnlyList<Vec3> line, IReadOnlyList<TimingLineSegment> data, TimingLineOptions o, IReadOnlyList<Vec3> points)
    {
        var coords = line.ToList();
        if (o.ReverseOrder) coords.Reverse();
        int count = coords.Count;
        var seg = new List<Vec3>();
        var nrm = new List<Vec3>();
        var len = new double[count - 1];
        for (int k = 0; k < count - 1; k++)
        {
            var d = Flat(coords[k + 1] - coords[k]);
            len[k] = d.Length;
            d = d.Normalized();
            seg.Add(d);
            nrm.Add(Unit2D(Vec3.Cross(d, new Vec3(0, 0, 1))));
        }
        double R(int i) => data[i].ReliefMsPerM;

        // Bisector tests that pick the governing segment of a contour line.
        var testPts = new Vec3[3];
        var testNorms = new Vec3[3];
        if (count > 1) testPts[0] = Flat(coords[1]);
        if (count > 2)
        {
            testPts[1] = Flat(coords[2]);
            testNorms[0] = nrm[0] * R(0) - nrm[1] * R(1);
            if (Vec3.Dot(coords[1] - coords[0], testNorms[0]) < 0) testNorms[0] = -testNorms[0];
        }
        if (count > 3)
        {
            testNorms[1] = nrm[1] * R(1) - nrm[2] * R(2);
            if (Vec3.Dot(coords[2] - coords[1], testNorms[1]) < 0) testNorms[1] = -testNorms[1];
            testNorms[2] = nrm[0] * R(0) - nrm[2] * R(2);
            var (ax, ay) = Perpendicular(testNorms[0], nrm[0]);
            var (bx, by) = Perpendicular(testNorms[1], nrm[1]);
            if (LineIntersection(coords[1].X, coords[1].Y, ax, ay, coords[2].X, coords[2].Y, bx, by, out var lambda))
            {
                testPts[2] = new Vec3(coords[1].X + lambda * ax, coords[1].Y + lambda * ay, 0);
                if (Vec3.Dot(coords[0] - testPts[2], testNorms[2]) > 0) testNorms[2] = -testNorms[2];
            }
            else testPts[2] = new Vec3(0, 0, -1);
        }

        double exponent = o.ProgressRate switch { 0 => 1.1, 1 => 1.2, 2 => 1.4, 3 => 1.6, _ => 2.0 };
        var result = new double[points.Count];
        for (int p = 0; p < points.Count; p++)
            result[p] = o.Style == TimingLineStyle.TimeLine ? ContourTime(points[p]) : BurnTime(points[p]);
        return result;

        double ContourTime(Vec3 pt)
        {
            var test = new bool[count];
            for (int n = 0; n <= count - 2; n++) test[n] = Vec3.Dot(Flat(pt - testPts[n]), testNorms[n]) > 0;
            int index = count == 2 ? 0 : count == 3 ? (test[0] ? 1 : 0) : (test[0] && !test[1]) ? 1 : (test[0] || test[2]) ? 2 : 0;
            var dist = Vec3.Dot(Flat(pt - coords[index]), nrm[index]);
            var t = -dist * R(index);
            if (dist < 0 && o.UseProgressive) t += Prog(dist, data[index].ProgressMs, exponent);
            return t;
        }

        double BurnTime(Vec3 pt)
        {
            double burned = 0, best = Invalid;
            for (int k = 0; k <= count - 2; k++)
            {
                var h = Flat(pt - coords[k]);
                var along = Vec3.Dot(h, seg[k]);
                if ((along <= len[k] + 0.001 || k == count - 2) && (along > -0.001 || k == 0))
                {
                    var away = Math.Abs(Vec3.Dot(h, nrm[k]));
                    var side = away * R(k);
                    if (o.UseProgressive) side += Prog(away, data[k].ProgressMs, exponent);
                    var t = side + along * data[k].BurnRateMsPerM + burned;
                    if (t < best) best = t;
                }
                burned += len[k] * data[k].BurnRateMsPerM;
            }
            if (best <= 1e30) return best;

            // Outside an interior corner: the later of the two adjacent segments governs.
            double worst = -Invalid;
            burned = 0;
            for (int k = 0; k <= count - 3; k++)
            {
                var h1 = Flat(pt - coords[k]);
                var along1 = Vec3.Dot(h1, seg[k]);
                var away1 = Math.Abs(Vec3.Dot(h1, nrm[k]));
                var h2 = Flat(pt - coords[k + 1]);
                var along2 = Vec3.Dot(h2, seg[k + 1]);
                var away2 = Math.Abs(Vec3.Dot(h2, nrm[k + 1]));
                if (along1 > len[k] - 0.001 && along2 < 0.001)
                {
                    var side = away1 * R(k);
                    if (o.UseProgressive) side += Prog(away1, data[k].ProgressMs, exponent);
                    worst = Math.Max(worst, side + along1 * data[k].BurnRateMsPerM + burned);
                    side = away2 * R(k + 1);
                    if (o.UseProgressive) side += Prog(away2, data[k + 1].ProgressMs, exponent);
                    // The corner distance burns with the first segment's rate.
                    worst = Math.Max(worst, side + (along2 + len[k]) * data[k].BurnRateMsPerM + burned);
                }
                burned += len[k] * data[k].BurnRateMsPerM;
            }
            return worst;
        }
    }

    /// <summary>A direction perpendicular to <paramref name="n"/> (fallback: <paramref name="fallback"/>).</summary>
    static (double X, double Y) Perpendicular(Vec3 n, Vec3 fallback)
    {
        if (n.Y != 0) return (10.0, -10.0 * n.X / n.Y);
        if (n.X != 0) return (-10.0 * n.Y / n.X, 10.0);
        return (fallback.X, fallback.Y);
    }

    /// <summary>Intersection of the infinite lines p1 + λ·d1 and p2 + μ·d2; false when parallel.</summary>
    static bool LineIntersection(double p1x, double p1y, double d1x, double d1y, double p2x, double p2y, double d2x, double d2y, out double lambda)
    {
        var den = d1x * d2y - d1y * d2x;
        lambda = 0;
        if (Math.Abs(den) < 1e-12) return false;
        lambda = ((p2x - p1x) * d2y - (p2y - p1y) * d2x) / den;
        return true;
    }

    static double[] AdvancedTimes(IReadOnlyList<Vec3> line, double tcl1, double tcl2, double tcp1, double tcp2, double startPoint, bool doubleEnd, bool doubleSide, IReadOnlyList<Vec3> points)
    {
        var segments = new List<Vec3>();
        var lengths = new List<double>();
        var startDist = new List<double>();
        double total = 0;
        for (int i = 0; i < line.Count - 1; i++)
        {
            var d = Flat(line[i + 1] - line[i]);
            lengths.Add(d.Length);
            startDist.Add(total);
            total += d.Length;
            segments.Add(d.Normalized());
        }
        return points.Select(CalcTime).ToArray();

        static bool IsLeft(Vec3 a, Vec3 b) => a.X * b.Y - a.Y * b.X > 0;

        double GetTime(double along, double away, double segStart)
        {
            var cl = tcl1;
            var cp = tcp1;
            var l = along + segStart - startPoint;
            if (l < 0) cl = tcl2;
            if (doubleEnd) l = Math.Abs(l);
            if (away < 0) cp = tcp2;
            if (doubleSide) away = Math.Abs(away);
            return away * cp + l * cl;
        }

        double CalcTime(Vec3 pt)
        {
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                var v = Flat(pt - line[i]);
                var along = Vec3.Dot(v, segments[i]);
                var away = Vec3.Cross(v, segments[i]).Length;
                if (IsLeft(v, segments[i])) away = -away;
                if ((i == segments.Count - 1 && along > lengths[i]) || i == 0) return GetTime(along, away, startDist[i]);

                var v0 = Flat(pt - line[i - 1]);
                var along0 = Vec3.Dot(v0, segments[i - 1]);
                var away0 = Vec3.Cross(v0, segments[i - 1]).Length;
                if (IsLeft(v0, segments[i - 1])) away0 = -away0;
                if (along >= 0 && along0 > lengths[i - 1]) return GetTime(along, away, startDist[i]);
                if ((along > 0 && along0 <= lengths[i - 1]) || (along < 0 && along0 > lengths[i - 1]))
                {
                    var bisector = new Vec3(segments[i].Y + segments[i - 1].Y, -segments[i].X - segments[i - 1].X, 0);
                    if (!IsLeft(v, bisector)) return GetTime(Math.Max(0, along), away, startDist[i]);
                    return GetTime(Math.Min(along0, lengths[i - 1]), away0, startDist[i - 1]);
                }
            }
            return Invalid;
        }
    }
}
