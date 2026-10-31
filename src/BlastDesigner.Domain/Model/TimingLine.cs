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

/// <summary>Timing values of one segment of a timing line, ms/m (progress: extra ms at 10 m).</summary>
public sealed record TimingLineSegment(double BurnRateMsPerM, double ReliefMsPerM, double ProgressMs);

/// <summary>
/// Electronic timing line kept in the project: up to four points, the timing
/// values of each segment and the options it programmed the holes with. Points keep the elevation they were picked at.
/// </summary>
public sealed class TimingLineEntity : Entity
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
