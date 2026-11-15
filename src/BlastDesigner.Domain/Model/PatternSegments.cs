namespace BlastDesigner.Domain.Model;

/// <summary>What the holes of a pattern are drilled down to (<see cref="DepthSpec"/>).</summary>
public enum DepthMode
{
    /// <summary>One depth for all holes: the bench height of the pattern (to the grade surface when there is one).</summary>
    Fixed,
    /// <summary>
    /// Collars on the conditional rectangle: the collar of a hole is <see cref="DepthSpec.Level"/> plus the depth at its node,
    /// the hole goes down to the level (plus the subdrill).
    /// </summary>
    RectToLevel,
    /// <summary>
    /// Collars on the collar surface (or at the level of the grid); the four numbers are the elevations the holes go down
    /// to at the corners of the conditional rectangle (plus the subdrill).
    /// </summary>
    SurfaceToRect,
    /// <summary>Down to the level of the pattern (<see cref="HolePattern.ToLevel"/>).</summary>
    ToLevel,
    /// <summary>Down to the first grade surface in the way of each hole (<see cref="HolePattern.ToSurface"/>).</summary>
    ToSurface,
    /// <summary>
    /// Down to the grade surface (to <see cref="DepthSpec.Level"/> without one), the depth kept between the two numbers of
    /// the front pair; the holes held at an end are counted (<c>PatternRebuildReport.ClampedIds</c>).
    /// </summary>
    Range
}

/// <summary>Two numbers along an edge of the conditional rectangle: at the left end of a row and at its right end, looking along the blast.</summary>
public sealed class DepthPair
{
    public double From { get; set; }
    public double To { get; set; }

    public DepthPair() { }
    public DepthPair(double from, double to) { From = from; To = to; }
}

/// <summary>
/// The depth of the holes of a pattern. With the rectangle modes four numbers stand at the corners of the conditional
/// rectangle of the pattern (the box of its nodes in the axes of its grid): the front pair along the first row (at the
/// face), the back pair along the last row, each from the left end of the row to its right end. Between them the value
/// goes over bilinearly and is rounded to <see cref="Round"/>.
/// </summary>
public sealed class DepthSpec
{
    public DepthMode Mode { get; set; }
    public DepthPair Front { get; set; } = new(10, 10);
    public DepthPair Back { get; set; } = new(10, 10);
    /// <summary>The level the holes go down to in <see cref="DepthMode.RectToLevel"/>.</summary>
    public double Level { get; set; }
    /// <summary>Step the depth of a node is rounded to, metres; 0: not rounded.</summary>
    public double Round { get; set; } = 0.1;
    /// <summary>The numbers are where the holes end, the subdrill within them; off, the subdrill goes below them.</summary>
    public bool IncludesSubdrill { get; set; }

    /// <summary>Whether the four numbers of the conditional rectangle are used.</summary>
    public bool UsesRectangle => Mode is DepthMode.RectToLevel or DepthMode.SurfaceToRect;
    /// <summary>A mode the older fields of a pattern cannot say: the holes carry what it gives them.</summary>
    public bool IsOwnMode => UsesRectangle || Mode == DepthMode.Range;

    /// <summary>
    /// The value at relative position <paramref name="s"/> (0…1 along a row, left to right) and <paramref name="t"/> (0…1 from
    /// the first row to the last), rounded.
    /// </summary>
    public double At(double s, double t)
    {
        var front = (1 - s) * Front.From + s * Front.To;
        var back = (1 - s) * Back.From + s * Back.To;
        var d = (1 - t) * front + t * back;
        if (Round > 0) d = Math.Round(d / Round) * Round;
        // a value found by measuring, to the micrometre (as HoleLevel.Round)
        return Math.Round(d, 6) + 0.0;
    }

    public DepthSpec Clone() => new()
    {
        Mode = Mode, Front = new DepthPair(Front.From, Front.To), Back = new DepthPair(Back.From, Back.To), Level = Level, Round = Round,
        IncludesSubdrill = IncludesSubdrill
    };

    public bool SameAs(DepthSpec o) =>
        Mode == o.Mode && Front.From == o.Front.From && Front.To == o.Front.To && Back.From == o.Back.From && Back.To == o.Back.To
        && Level == o.Level && Round == o.Round && IncludesSubdrill == o.IncludesSubdrill;
}

/// <summary>
/// The parameters a segment sets itself (<see cref="HolePattern.ParentId"/>): null takes the value of the parent. The values
/// in effect stay in the usual fields of the segment, so code that knows nothing of segments reads them as before.
/// </summary>
public sealed class PatternOverrides
{
    public double? Burden { get; set; }
    public double? Spacing { get; set; }
    public PatternType? Type { get; set; }
    public double? Azimuth { get; set; }
    public RowAlignment? Alignment { get; set; }
    public double? EdgeOffset { get; set; }
    public double? Diameter { get; set; }
    public double? Angle { get; set; }
    public double? Bearing { get; set; }
    public double? Subdrill { get; set; }
    public double? BenchHeight { get; set; }
    public int? HoleType { get; set; }
    /// <summary>The depth as a whole: its mode and its numbers.</summary>
    public DepthSpec? Depth { get; set; }

    /// <summary>Whether the segment sets nothing itself.</summary>
    public bool IsEmpty =>
        Burden == null && Spacing == null && Type == null && Azimuth == null && Alignment == null && EdgeOffset == null && Diameter == null
        && Angle == null && Bearing == null && Subdrill == null && BenchHeight == null && HoleType == null && Depth == null;

    public PatternOverrides Clone()
    {
        var c = (PatternOverrides)MemberwiseClone();
        c.Depth = Depth?.Clone();
        return c;
    }
}

/// <summary>
/// A line the pattern is cut along into segments, in the axes of the grid of the pattern about its centre (u along the
/// rows, v against the blast movement), so a turn or a shift of the pattern takes it along.
/// </summary>
public sealed class SegmentLine
{
    public double U1 { get; set; }
    public double V1 { get; set; }
    public double U2 { get; set; }
    public double V2 { get; set; }
    /// <summary>The line was put in the middle between two rows (and stays there when the burden changes).</summary>
    public bool Snapped { get; set; }
    /// <summary>With <see cref="Snapped"/>: the line is between grid rows <see cref="Row"/> and <see cref="Row"/> + 1 of the parent.</summary>
    public int Row { get; set; }

    public SegmentLine Clone() => (SegmentLine)MemberwiseClone();
}
