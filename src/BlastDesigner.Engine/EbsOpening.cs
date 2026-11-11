using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// Times of an EBS opening point (SHOTPlus <c>SPEBSOpeningPoint.ProgramBlast</c>). With d from the reference point to the hole's
/// firing point (<see cref="EbsTimingLine.TimingPoint"/>), t = (d·b̂)/burden · burden delay + (d·ŝ)/spacing · spacing delay. Opened
/// at the point, both terms are taken as they are, either way, and the reference becomes the hole with the least time. Then the
/// times are shifted to the time of the first hole, and the largest delay, the holes out of reach and the manual times go as
/// for a timing front; only the holes with an electronic detonator are timed.
/// </summary>
public static class EbsOpening
{
    /// <summary>Raw times of the points (un-shifted), ms.</summary>
    public static double[] RawTimes(EbsOpeningPoint op, IReadOnlyList<Vec3> points)
    {
        var b = op.BurdenDir;
        var s = op.SpacingDir;
        double Time(Vec3 from, Vec3 p, bool open)
        {
            var d = (p - from).WithZ(0);
            var tb = op.BurdenM > 0 ? Vec3.Dot(d, b) / op.BurdenM * op.BurdenDelayMs : 0;
            var ts = op.SpacingM > 0 ? Vec3.Dot(d, s) / op.SpacingM * op.SpacingDelayMs : 0;
            return open ? Math.Abs(tb) + Math.Abs(ts) : tb + ts;
        }
        if (!op.OpenHere || points.Count == 0) return points.Select(p => Time(op.Position, p, false)).ToArray();
        // opened here: the hole nearest the point in time opens the segment, the others grow either way from it
        var first = points[Enumerable.Range(0, points.Count).MinBy(i => Time(op.Position, points[i], true))];
        return points.Select(p => Time(first, p, true)).ToArray();
    }

    /// <summary>The times <see cref="Apply"/> would program, without changing the holes (the preview of its window).</summary>
    public static (Dictionary<Guid, double> Times, TimingLineResult Result) Plan(BlastProject project, IReadOnlyList<Hole> holes, EbsOpeningPoint op, bool keepManualTimes = true) =>
        EbsTimingLine.Program(project, holes, e => RawTimes(op, e.Select(h => EbsTimingLine.TimingPoint(project, h)).ToList()),
            op.StartOffsetMs, keepManualTimes, false, project.Electronic.MaxTimeMs, project.Electronic.AllowSubMs);

    /// <summary>Programs the holes from the opening point.</summary>
    public static TimingLineResult Apply(BlastProject project, IReadOnlyList<Hole> holes, EbsOpeningPoint op, bool keepManualTimes = true) =>
        EbsTimingLine.Put(project, holes, Plan(project, holes, op, keepManualTimes), project.Electronic.AllowSubMs);
}
