using System.Text.Json.Serialization;
using BlastDesigner.Domain.Geometry;

namespace BlastDesigner.Domain.Model;

/// <summary>Kind of electronic timing line.</summary>
public enum TimingLineStyle
{
    /// <summary>Contour line: time grows with the distance from the nearest segment (relief per segment).</summary>
    TimeLine,
    /// <summary>Burn line: the signal burns along the line (burn rate) and spreads sideways (relief).</summary>
    BurnLine,
    /// <summary>Advanced burn: one relief along the line, sideways relief = relief × tan(angle) per side.</summary>
    AdvancedBurn
}

/// <summary>Shape a line was drawn with by the front tool.</summary>
public enum TimingFrontShape
{
    /// <summary>Picked point by point (files written before the front tool).</summary>
    None,
    /// <summary>One segment: the times grow from it to one side, the holes at one distance from it fire together.</summary>
    Line,
    /// <summary>Two segments meeting at a vertex, see <see cref="Geometry.Chevron"/>.</summary>
    Chevron
}

/// <summary>
/// An object that programs electronic times: a timing front or an EBS opening point. A hole is timed by one of them, the one
/// whose <see cref="HoleIds"/> lists it.
/// </summary>
public interface IHoleTimer
{
    Guid Id { get; }
    List<Guid> HoleIds { get; }
}

/// <summary>
/// EBS opening point (SHOTPlus <c>SPEBSOpeningPoint</c>): the times of its holes grow from it along the burden, by
/// <see cref="BurdenDelayMs"/> a burden, and along the spacing, by <see cref="SpacingDelayMs"/> a spacing. Opened here, both
/// grow either way from the hole nearest to it in time: a V pattern.
/// </summary>
public sealed class EbsOpeningPoint : Entity, IHoleTimer
{
    public Vec3 Position { get; set; }
    /// <summary>Burden, m.</summary>
    public double BurdenM { get; set; } = 4;
    /// <summary>Spacing, m.</summary>
    public double SpacingM { get; set; } = 5;
    /// <summary>Which way the burden goes from the point, degrees clockwise from north; the spacing goes at right angles to it.</summary>
    public double BurdenBearingDeg { get; set; }
    /// <summary>Delay of one burden, ms.</summary>
    public double BurdenDelayMs { get; set; } = 42;
    /// <summary>Delay of one spacing, ms.</summary>
    public double SpacingDelayMs { get; set; } = 17;
    /// <summary>Time of the first hole, ms.</summary>
    public double StartOffsetMs { get; set; }
    /// <summary>Open the segment at this point: the times grow to both sides of it.</summary>
    public bool OpenHere { get; set; } = true;
    /// <summary>Holes the point programs.</summary>
    public List<Guid> HoleIds { get; set; } = new();

    /// <summary>Unit plan direction of the burden.</summary>
    [JsonIgnore] public Vec3 BurdenDir => new(Math.Sin(BurdenBearingDeg * GeoMath.Deg), Math.Cos(BurdenBearingDeg * GeoMath.Deg), 0);
    /// <summary>Unit plan direction of the spacing: the burden turned a right angle clockwise.</summary>
    [JsonIgnore] public Vec3 SpacingDir => new(Math.Cos(BurdenBearingDeg * GeoMath.Deg), -Math.Sin(BurdenBearingDeg * GeoMath.Deg), 0);
    /// <summary>Overall relief, ms/m: √((spacing delay / spacing)² + (burden delay / burden)²).</summary>
    [JsonIgnore] public double TotalReliefMsPerM =>
        Math.Sqrt(Math.Pow(SpacingM > 0 ? SpacingDelayMs / SpacingM : 0, 2) + Math.Pow(BurdenM > 0 ? BurdenDelayMs / BurdenM : 0, 2));

    public override string KindName => "EBS opening point";
    public override IEnumerable<Vec3> GetVertices() { yield return Position; }
    public override void Translate(Vec3 d) => Position += d;
    public override void Transform(Func<Vec3, Vec3> f)
    {
        var ahead = f(Position + BurdenDir);
        Position = f(Position);
        var d = (ahead - Position).WithZ(0);
        if (d.Length2D > 1e-9) BurdenBearingDeg = GeoMath.NormalizeBearing(Math.Atan2(d.X, d.Y) / GeoMath.Deg);
    }
    public override double PickDistance(Vec3 p) => Vec3.Distance2D(Position, p);
}

/// <summary>Timing values of one segment of a timing line, ms/m (progress: extra ms at 10 m).</summary>
public sealed record TimingLineSegment(double BurnRateMsPerM, double ReliefMsPerM, double ProgressMs);

/// <summary>
/// Electronic timing line kept in the project: up to four points, the timing
/// values of each segment and the options it programmed the holes with. Points keep the elevation they were picked at.
/// A front drawn as a line (<see cref="Front"/>) has two points, one drawn as a chevron three: <c>[left end, vertex, right end]</c>,
/// see <see cref="Geometry.Chevron"/>.
/// </summary>
public sealed class TimingLineEntity : Entity, IHoleTimer
{
    /// <summary>Length of the advanced-burn angle arms, m.</summary>
    public const double ArmLength = 20;

    public List<Vec3> Points { get; set; } = new();
    public TimingLineStyle Style { get; set; }
    public List<TimingLineSegment> Segments { get; set; } = new();
    public double StartOffsetMs { get; set; }
    public bool UseProgressive { get; set; }
    public int ProgressRate { get; set; } = 1;
    public bool ReverseOrder { get; set; }
    public double BurdenReliefMsPerM { get; set; } = 8;
    public double Angle1Deg { get; set; }
    public double Angle2Deg { get; set; }
    /// <summary>Holes the line programmed, so that editing it reprograms exactly those; empty in files written before.</summary>
    public List<Guid> HoleIds { get; set; } = new();
    /// <summary>The shape the front tool drew it with; its circles change it on the plan.</summary>
    public TimingFrontShape Front { get; set; }
    /// <summary>A chevron of the front tool; the files written before <see cref="Front"/> have only this.</summary>
    public bool IsChevron
    {
        get => Front == TimingFrontShape.Chevron;
        set { if (value) Front = TimingFrontShape.Chevron; }
    }

    /// <summary>The shape is changed by its circles: a chevron of three points, a line of two.</summary>
    [JsonIgnore]
    public bool HasHandles => (Front == TimingFrontShape.Chevron && Points.Count == 3) || (Front == TimingFrontShape.Line && Points.Count == 2);

    /// <summary>A copy for drawing with other points (same values, same id); nothing in the project refers to it.</summary>
    public TimingLineEntity WithPoints(IEnumerable<Vec3> points) => new()
    {
        Id = Id, LayerId = LayerId, GroupId = GroupId, Color = Color,
        Points = points.ToList(), Style = Style, Segments = Segments.ToList(), StartOffsetMs = StartOffsetMs,
        UseProgressive = UseProgressive, ProgressRate = ProgressRate, ReverseOrder = ReverseOrder,
        BurdenReliefMsPerM = BurdenReliefMsPerM, Angle1Deg = Angle1Deg, Angle2Deg = Angle2Deg,
        HoleIds = HoleIds.ToList(), Front = Front
    };

    public override string KindName => "Timing line";
    public override IEnumerable<Vec3> GetVertices() => Points;
    public override void Translate(Vec3 d) { for (int i = 0; i < Points.Count; i++) Points[i] += d; }
    public override void Transform(Func<Vec3, Vec3> f) { for (int i = 0; i < Points.Count; i++) Points[i] = f(Points[i]); }
    public override double PickDistance(Vec3 p) => GeoMath.DistanceToPolyline2D(Points, p, false);

    /// <summary>End of the left (1) or right (2) advanced-burn angle arm, drawn from the first point.</summary>
    public Vec3 AngleEnd(int side)
    {
        if (Points.Count < 2) return Points.Count == 1 ? Points[0] : Vec3.Zero;
        var d = (Points[1] - Points[0]).WithZ(0).Normalized() * ArmLength;
        var a = side == 1 ? Math.PI / 2 + Angle1Deg * Math.PI / 180 : -(Math.PI / 2 + Angle2Deg * Math.PI / 180);
        var (s, c) = Math.SinCos(a);
        return Points[0] + new Vec3(d.X * c - d.Y * s, d.X * s + d.Y * c, 0);
    }

    /// <summary>Points in the order the line is walked (reversed when <see cref="ReverseOrder"/> is set, except for the advanced burn).</summary>
    [JsonIgnore]
    public IReadOnlyList<Vec3> OrderedPoints
    {
        get
        {
            if (!ReverseOrder || Style == TimingLineStyle.AdvancedBurn) return Points;
            var l = Points.ToList();
            l.Reverse();
            return l;
        }
    }
}
