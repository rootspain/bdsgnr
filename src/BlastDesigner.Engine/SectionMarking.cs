using System.Globalization;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// The plane of a section along a line from <see cref="A"/> to <see cref="B"/>: a point of it is S metres along the line
/// from A (in plan) and Z high. Marks drawn by hand keep their points so and go with the line.
/// </summary>
public sealed class SectionFrame
{
    public Vec3 A { get; }
    public Vec3 B { get; }
    private readonly double _dx, _dy;

    public SectionFrame(Vec3 a, Vec3 b)
    {
        A = a; B = b;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        (_dx, _dy) = len < 1e-12 ? (1.0, 0.0) : (dx / len, dy / len);
    }

    public Vec3 ToWorld(double s, double z) => new(A.X + _dx * s, A.Y + _dy * s, z);
    public Vec3 ToWorld(SectionPoint p) => ToWorld(p.S, p.Z);
    public (double S, double Z) ToSection(Vec3 w) => ((w.X - A.X) * _dx + (w.Y - A.Y) * _dy, w.Z);
}

/// <summary>A point of a mark where it is drawn now: held on a hole or a surface, where that object is; <see cref="Lost"/> when it is gone.</summary>
public readonly record struct SectionSpot(double S, double Z, bool Lost = false);

/// <summary>An angle mark worked out: the vertex, the two sides (unit, in S and Z) and the angle between them.</summary>
public sealed record SectionAngle(double S, double Z, (double S, double Z) Side1, (double S, double Z) Side2, double Degrees, double Radius);

/// <summary>The marks drawn by hand on the sections: where their points are, their values and how they are written.</summary>
public static class SectionMarking
{
    /// <summary>The marks of a section line.</summary>
    public static IEnumerable<SectionMark> Of(BlastProject project, Guid line) => project.SectionMarks.Where(m => m.SectionLineId == line);

    /// <summary>The section line from a to b of the project (its first two points), if one runs so.</summary>
    public static PolylineEntity? LineAt(BlastProject project, Vec3 a, Vec3 b) =>
        SectionNames.Lines(project).FirstOrDefault(l => l.Points.Count >= 2 && l.Points[0].Equals(a) && l.Points[1].Equals(b));

    /// <summary>
    /// Where a point of a mark is: on its hole (collar, toe or grade) or on its surface at its S, else as kept. A point whose
    /// hole or surface is gone stays where it was and is lost.
    /// </summary>
    public static SectionSpot Resolve(BlastProject project, SectionFrame frame, SectionPoint p)
    {
        if (p.HoleId is Guid hid)
        {
            if (project.FindEntity(hid) is not Hole h) return new(p.S, p.Z, true);
            var (s, z) = frame.ToSection(p.HolePart switch { SectionHolePart.Toe => h.Toe, SectionHolePart.Grade => h.GradePoint, _ => h.Collar });
            return new(s, z);
        }
        if (p.SurfaceId is Guid sid)
        {
            if (project.FindEntity(sid) is not SurfaceEntity surface) return new(p.S, p.Z, true);
            var w = frame.ToWorld(p.S, p.Z);
            return surface.ElevationAt(w.X, w.Y) is double z ? new(p.S, z) : new(p.S, p.Z, true);
        }
        return new(p.S, p.Z);
    }

    /// <summary>All the points of a mark where they are now.</summary>
    public static List<SectionSpot> Resolve(BlastProject project, SectionFrame frame, SectionMark m) =>
        m.Points.Select(p => Resolve(project, frame, p)).ToList();

    /// <summary>
    /// The angle between the line of points 0–1 and that of points 2–3, in the quarter the arc point (4) is in, or the
    /// acute or obtuse one as the style asks; null for parallel lines.
    /// </summary>
    public static SectionAngle? Angle(IReadOnlyList<SectionSpot> p, SectionAngleMode mode = SectionAngleMode.AsDrawn)
    {
        if (p.Count < 5) return null;
        var (u1s, u1z) = Unit(p[1].S - p[0].S, p[1].Z - p[0].Z);
        var (u2s, u2z) = Unit(p[3].S - p[2].S, p[3].Z - p[2].Z);
        var cross = u1s * u2z - u1z * u2s;
        if (Math.Abs(cross) < 1e-9) return null;
        // where the lines meet: p0 + t·u1 = p2 + k·u2
        var ws = p[2].S - p[0].S;
        var wz = p[2].Z - p[0].Z;
        var t = (ws * u2z - wz * u2s) / cross;
        double cs = p[0].S + t * u1s, cz = p[0].Z + t * u1z;
        // the arc point as a·u1 + b·u2 from the vertex: the signs pick the quarter
        var vs = p[4].S - cs;
        var vz = p[4].Z - cz;
        var a = (vs * u2z - vz * u2s) / cross;
        var b = (u1s * vz - u1z * vs) / cross;
        var r1 = (S: a >= 0 ? u1s : -u1s, Z: a >= 0 ? u1z : -u1z);
        var r2 = (S: b >= 0 ? u2s : -u2s, Z: b >= 0 ? u2z : -u2z);
        var degrees = Math.Acos(Math.Clamp(r1.S * r2.S + r1.Z * r2.Z, -1, 1)) * 180 / Math.PI;
        // the other angle at the same vertex: one side turned back
        if ((mode == SectionAngleMode.Acute && degrees > 90) || (mode == SectionAngleMode.Obtuse && degrees < 90))
        {
            r2 = (-r2.S, -r2.Z);
            degrees = 180 - degrees;
        }
        var radius = Math.Max(Math.Sqrt(vs * vs + vz * vz), 1e-6);
        return new SectionAngle(cs, cz, r1, r2, degrees, radius);
    }

    private static (double S, double Z) Unit(double s, double z)
    {
        var len = Math.Sqrt(s * s + z * z);
        return len < 1e-12 ? (1, 0) : (s / len, z / len);
    }

    /// <summary>The slope of a line from a to b: its angle to the level, degrees.</summary>
    public static double SlopeDegrees(SectionSpot a, SectionSpot b) =>
        Math.Atan2(Math.Abs(b.Z - a.Z), Math.Abs(b.S - a.S)) * 180 / Math.PI;

    /// <summary>The value a mark measures: a length or a height, m (kept); an angle or a slope, degrees; null for a line or a text.</summary>
    public static double? Value(SectionMark m, IReadOnlyList<SectionSpot> p) => m.Kind switch
    {
        SectionMarkKind.Horizontal when p.Count >= 2 => Math.Abs(p[1].S - p[0].S),
        SectionMarkKind.Vertical or SectionMarkKind.Leader when p.Count >= 2 => Math.Abs(p[1].Z - p[0].Z),
        SectionMarkKind.Aligned when p.Count >= 2 => Math.Sqrt(Math.Pow(p[1].S - p[0].S, 2) + Math.Pow(p[1].Z - p[0].Z, 2)),
        SectionMarkKind.Angle => Angle(p, m.Style.AngleMode)?.Degrees,
        SectionMarkKind.Slope when p.Count >= 2 => SlopeDegrees(p[0], p[1]),
        SectionMarkKind.Level or SectionMarkKind.PointZ when p.Count >= 1 => p[0].Z,
        _ => null
    };

    /// <summary>A number with a point and so many decimals.</summary>
    public static string Number(double v, int decimals) => v.ToString("F" + Math.Clamp(decimals, 0, 4), CultureInfo.InvariantCulture);

    /// <summary>A slope as the style writes it: 50°, 119%, 1:0.84.</summary>
    public static string Slope(SectionSpot a, SectionSpot b, SectionMarkStyle style)
    {
        double ds = Math.Abs(b.S - a.S), dz = Math.Abs(b.Z - a.Z);
        return style.SlopeFormat switch
        {
            SectionSlopeFormat.Percent => ds < 1e-9 ? "∞" : Number(dz / ds * 100, style.AngleDecimals) + "%",
            SectionSlopeFormat.Ratio => dz < 1e-9 ? "1:∞" : "1:" + Number(ds / dz, Math.Max(style.Decimals, 2)),
            _ => Number(SlopeDegrees(a, b), style.AngleDecimals) + "°"
        };
    }

    /// <summary>
    /// The figure of a mark: its value (lengths with the decimals of the style, angles in degrees), the text of the style
    /// in its place with &lt;&gt; for the value, the words of a text mark; "?" for a value from a point that is lost.
    /// </summary>
    public static string Label(SectionMark m, IReadOnlyList<SectionSpot> p)
    {
        if (m.Kind == SectionMarkKind.Text) return m.Text ?? "";
        if (m.Kind == SectionMarkKind.Polyline) return m.Style.Text ?? "";
        string value;
        if (p.Any(x => x.Lost)) value = "?";
        else if (m.Kind == SectionMarkKind.Slope && p.Count >= 2) value = Slope(p[0], p[1], m.Style);
        else if (Value(m, p) is not double v) value = "?";
        // a length or a level in the unit of lengths shown
        else value = m.Kind == SectionMarkKind.Angle ? Number(v, m.Style.AngleDecimals) + "°" : Number(Measure.Show(Quantity.Length, v), m.Style.Decimals);
        return string.IsNullOrEmpty(m.Style.Text) ? value : m.Style.Text.Replace("<>", value);
    }

    /// <summary>
    /// Before a change: the section lines there are. After it the marks of a line that is gone go with it, and a section line
    /// new in the change at the same place as one before it (a copy pasted on it, or with the same points) takes a copy of
    /// its marks.
    /// </summary>
    public static Action? Follow(BlastProject project)
    {
        var before = SectionNames.Lines(project).ToDictionary(l => l.Id, l => (l.Points.Count >= 2 ? l.Points[0] : default, l.Points.Count >= 2 ? l.Points[1] : default));
        return () =>
        {
            var lines = SectionNames.Lines(project);
            var ids = lines.Select(l => l.Id).ToHashSet();
            project.SectionMarks.RemoveAll(m => !ids.Contains(m.SectionLineId));
            project.SectionOwnDimensions.RemoveAll(id => !ids.Contains(id));
            foreach (var l in lines)
            {
                if (before.ContainsKey(l.Id) || l.Points.Count < 2) continue;
                // a new line along an old one (a copy): the marks of that line, of the new one too; the copy's marks
                // keep their S and Z, so a copy moved aside shows them on its own section
                var source = before.FirstOrDefault(b => b.Value.Item1.Equals(l.Points[0]) && b.Value.Item2.Equals(l.Points[1])).Key;
                if (source == Guid.Empty || !ids.Contains(source)) continue;
                foreach (var m in project.SectionMarks.Where(m => m.SectionLineId == source).ToList())
                {
                    var c = m.Copy();
                    c.Id = Guid.NewGuid();
                    c.SectionLineId = l.Id;
                    project.SectionMarks.Add(c);
                }
            }
        };
    }
}
