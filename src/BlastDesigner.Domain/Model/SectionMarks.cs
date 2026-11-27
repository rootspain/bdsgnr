namespace BlastDesigner.Domain.Model;

/// <summary>What a mark drawn by hand on a section is.</summary>
public enum SectionMarkKind
{
    /// <summary>A dimension along the line: points 0 and 1 measured, point 2 where the dimension line runs.</summary>
    Horizontal,
    /// <summary>A dimension up and down: points 0 and 1 measured, point 2 where the dimension line runs.</summary>
    Vertical,
    /// <summary>The true length between points 0 and 1, the dimension line through point 2 parallel to them.</summary>
    Aligned,
    /// <summary>A short dimension up and down at point 0 to point 1, its figure on a shelf at point 2 (a subdrill).</summary>
    Leader,
    /// <summary>The angle between the line of points 0–1 and that of points 2–3, its arc through point 4.</summary>
    Angle,
    /// <summary>The slope of the line of points 0–1, its figure at point 2.</summary>
    Slope,
    /// <summary>A level across the frame at the height of point 0, its figure left of the frame.</summary>
    Level,
    /// <summary>The height of point 0 on a shelf at point 1.</summary>
    PointZ,
    /// <summary>A line through its points.</summary>
    Polyline,
    /// <summary>A text at point 0.</summary>
    Text
}

/// <summary>What the ends of a dimension line are.</summary>
public enum SectionMarkEnds { Arrow, Tick, Dot, None }

/// <summary>Which of the angles between two lines an angle mark shows.</summary>
public enum SectionAngleMode
{
    /// <summary>The one the arc point is in, as drawn.</summary>
    AsDrawn,
    /// <summary>The acute one (90° at most).</summary>
    Acute,
    /// <summary>The obtuse one (90° at least).</summary>
    Obtuse
}

/// <summary>How a slope is written.</summary>
public enum SectionSlopeFormat
{
    /// <summary>50°.</summary>
    Degrees,
    /// <summary>119 %.</summary>
    Percent,
    /// <summary>1:0.84 (one up to so much across).</summary>
    Ratio
}

/// <summary>Where on a hole a point of a mark is held.</summary>
public enum SectionHolePart { Collar, Toe, Grade }

/// <summary>
/// A point of a mark in the plane of its section: <see cref="S"/> metres along the line from its first point,
/// <see cref="Z"/> the height. With a hole or a surface it is held on that object and follows it: the hole gives its
/// collar, toe or grade, the surface its height at <see cref="S"/>.
/// </summary>
public sealed class SectionPoint
{
    public double S { get; set; }
    public double Z { get; set; }
    /// <summary>The hole the point is held on, if any.</summary>
    public Guid? HoleId { get; set; }
    public SectionHolePart HolePart { get; set; }
    /// <summary>The surface the point is held on, if any (at <see cref="S"/>).</summary>
    public Guid? SurfaceId { get; set; }

    public SectionPoint() { }
    public SectionPoint(double s, double z) { S = s; Z = z; }

    public SectionPoint Copy() => (SectionPoint)MemberwiseClone();
}

/// <summary>How a mark is drawn. Null fields take the look of the section.</summary>
public sealed class SectionMarkStyle
{
    /// <summary>ARGB; null — the colour of the labels of the section.</summary>
    public uint? Color { get; set; }
    /// <summary>A line type of the project or a standard one; null — solid.</summary>
    public string? LineType { get; set; }
    /// <summary>Hundredths of a millimetre, as <see cref="LineTypes.Weights"/>.</summary>
    public int LineWeight { get; set; } = 25;
    public SectionMarkEnds Ends { get; set; } = SectionMarkEnds.Arrow;
    /// <summary>Size of the figure against that of the labels of the section; 1 the same.</summary>
    public double TextScale { get; set; } = 1;
    /// <summary>Decimals of a length (an angle has none unless set).</summary>
    public int Decimals { get; set; } = 1;
    /// <summary>Decimals of an angle.</summary>
    public int AngleDecimals { get; set; }
    /// <summary>A text of its own instead of the value; &lt;&gt; in it is the value.</summary>
    public string? Text { get; set; }
    public SectionAngleMode AngleMode { get; set; } = SectionAngleMode.AsDrawn;
    public SectionSlopeFormat SlopeFormat { get; set; } = SectionSlopeFormat.Degrees;

    public SectionMarkStyle Copy() => (SectionMarkStyle)MemberwiseClone();
}

/// <summary>A named style offered for the marks of a section (Bench level, Grade level, Slope, Dimension…).</summary>
public sealed class SectionMarkPreset
{
    public string Name { get; set; } = string.Empty;
    public SectionMarkStyle Style { get; set; } = new();

    public const uint Blue = 0xFF3B82F6, Yellow = 0xFFE5D700, Grey = 0xFF8A93A3;

    /// <summary>The style of the line of a slope face (Откос); it was called Slope, which is the name of the slope tool.</summary>
    public const string SlopeFace = "Slope face";

    /// <summary>
    /// The styles offered at first, as on the sections of the mines: the bench level blue, the grade level yellow, the slopes
    /// and the dimensions in the colour of the labels, the helping lines grey and dashed.
    /// </summary>
    public static List<SectionMarkPreset> Defaults() => new()
    {
        new() { Name = "Bench level", Style = new() { Color = Blue, LineWeight = 50 } },
        new() { Name = "Grade level", Style = new() { Color = Yellow, LineWeight = 50 } },
        new() { Name = SlopeFace, Style = new() { LineWeight = 35 } },
        new() { Name = "Dimension", Style = new() { LineWeight = 18 } },
        new() { Name = "Helping line", Style = new() { Color = Grey, LineType = "DASHED", LineWeight = 18 } }
    };

    /// <summary>The style a mark of a kind takes when none was made default: a level is a bench level, a line a slope, the rest dimensions.</summary>
    public static SectionMarkStyle BuiltIn(SectionMarkKind kind)
    {
        var all = Defaults();
        var style = kind switch
        {
            SectionMarkKind.Level => all[0].Style,
            SectionMarkKind.Polyline => all[2].Style,
            _ => all[3].Style
        };
        // a short dimension with its figure on a shelf: ticks, as on the drawings of the subdrill
        if (kind == SectionMarkKind.Leader) style.Ends = SectionMarkEnds.Tick;
        if (kind is SectionMarkKind.Angle or SectionMarkKind.Slope) style.Ends = SectionMarkEnds.None;
        return style;
    }
}

/// <summary>
/// What a section keeps of its own (the section of a section line, <see cref="SectionLineId"/>): the shifts of the top and
/// the bottom of its frame set in the section window. One for a section line that has any; kept in the project file.
/// </summary>
public sealed class SectionSettings
{
    public Guid SectionLineId { get; set; }
    /// <summary>Added to the height of the top of the frame, m (up positive, down negative).</summary>
    public double FrameTopShift { get; set; }
    /// <summary>Added to the height of the bottom of the frame (the lowest toe), m (up positive).</summary>
    public double FrameBottomShift { get; set; }
}

/// <summary>
/// A dimension, an angle, a level, a line or a text drawn by hand on a section. It belongs to a section line
/// (<see cref="SectionLineId"/>), is kept in the project file and printed with the sheet of that section. Its points are
/// in the plane of the section, so it goes with the line when the line is moved or turned.
/// </summary>
public sealed class SectionMark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SectionLineId { get; set; }
    public SectionMarkKind Kind { get; set; }
    /// <summary>The points; what each one is goes by <see cref="Kind"/>.</summary>
    public List<SectionPoint> Points { get; set; } = new();
    /// <summary>The words of a text mark.</summary>
    public string? Text { get; set; }
    /// <summary>Made from an automatic dimension of the section, when it was picked to be edited.</summary>
    public bool Auto { get; set; }
    public SectionMarkStyle Style { get; set; } = new();

    public SectionMark Copy()
    {
        var m = (SectionMark)MemberwiseClone();
        m.Points = Points.Select(p => p.Copy()).ToList();
        m.Style = Style.Copy();
        return m;
    }

    /// <summary>How many points a mark of a kind takes; 0 for any number (a line).</summary>
    public static int PointCount(SectionMarkKind kind) => kind switch
    {
        SectionMarkKind.Horizontal or SectionMarkKind.Vertical or SectionMarkKind.Aligned or SectionMarkKind.Leader => 3,
        SectionMarkKind.Angle => 5,
        SectionMarkKind.Slope => 3,
        SectionMarkKind.Level or SectionMarkKind.Text => 1,
        SectionMarkKind.PointZ => 2,
        _ => 0
    };
}
