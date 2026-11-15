namespace BlastDesigner.Domain.Geometry;

/// <summary>An affine map of space: a 3×3 matrix (rows <c>M11…M33</c>) and a shift, <c>p' = M·p + T</c>.</summary>
public readonly record struct Affine3(
    double M11, double M12, double M13,
    double M21, double M22, double M23,
    double M31, double M32, double M33,
    Vec3 T)
{
    public static readonly Affine3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1, Vec3.Zero);

    public static Affine3 Translation(Vec3 d) => Identity with { T = d };
    public static Affine3 Scale(double sx, double sy, double sz) => new(sx, 0, 0, 0, sy, 0, 0, 0, sz, Vec3.Zero);

    /// <summary>A turn about X by <paramref name="deg"/>, counterclockwise looking from +X (the right-hand rule).</summary>
    public static Affine3 RotationX(double deg)
    {
        var (s, c) = Math.SinCos(deg * Math.PI / 180);
        return new(1, 0, 0, 0, c, -s, 0, s, c, Vec3.Zero);
    }

    /// <summary>A turn about Y by <paramref name="deg"/>, counterclockwise looking from +Y (the right-hand rule).</summary>
    public static Affine3 RotationY(double deg)
    {
        var (s, c) = Math.SinCos(deg * Math.PI / 180);
        return new(c, 0, s, 0, 1, 0, -s, 0, c, Vec3.Zero);
    }

    /// <summary>A turn about Z by <paramref name="deg"/>, counterclockwise looking down (the right-hand rule).</summary>
    public static Affine3 RotationZ(double deg)
    {
        var (s, c) = Math.SinCos(deg * Math.PI / 180);
        return new(c, -s, 0, s, c, 0, 0, 0, 1, Vec3.Zero);
    }

    public Vec3 Apply(Vec3 p) => ApplyVector(p) + T;

    /// <summary>The matrix alone: a direction or a difference of points.</summary>
    public Vec3 ApplyVector(Vec3 v) => new(M11 * v.X + M12 * v.Y + M13 * v.Z, M21 * v.X + M22 * v.Y + M23 * v.Z, M31 * v.X + M32 * v.Y + M33 * v.Z);

    /// <summary>This map, then <paramref name="next"/>: <c>next(this(p))</c>.</summary>
    public Affine3 Then(Affine3 next) => new(
        next.M11 * M11 + next.M12 * M21 + next.M13 * M31, next.M11 * M12 + next.M12 * M22 + next.M13 * M32, next.M11 * M13 + next.M12 * M23 + next.M13 * M33,
        next.M21 * M11 + next.M22 * M21 + next.M23 * M31, next.M21 * M12 + next.M22 * M22 + next.M23 * M32, next.M21 * M13 + next.M22 * M23 + next.M23 * M33,
        next.M31 * M11 + next.M32 * M21 + next.M33 * M31, next.M31 * M12 + next.M32 * M22 + next.M33 * M32, next.M31 * M13 + next.M32 * M23 + next.M33 * M33,
        next.Apply(T));

    public double Determinant => M11 * (M22 * M33 - M23 * M32) - M12 * (M21 * M33 - M23 * M31) + M13 * (M21 * M32 - M22 * M31);

    /// <summary>The determinant of the plan part (X and Y): negative when the map mirrors the plan.</summary>
    public double PlanDeterminant => M11 * M22 - M12 * M21;

    /// <summary>Whether the map turns space inside out (an odd number of mirrors).</summary>
    public bool IsMirror => Determinant < 0;

    /// <summary>Whether it keeps a vertical vertical (no tilt about X or Y).</summary>
    public bool KeepsVertical => Math.Abs(M13) < 1e-9 && Math.Abs(M23) < 1e-9;

    public Affine3 Inverse()
    {
        var d = Determinant;
        if (Math.Abs(d) < 1e-300) throw new InvalidOperationException("The transform has no inverse");
        var i = new Affine3(
            (M22 * M33 - M23 * M32) / d, (M13 * M32 - M12 * M33) / d, (M12 * M23 - M13 * M22) / d,
            (M23 * M31 - M21 * M33) / d, (M11 * M33 - M13 * M31) / d, (M13 * M21 - M11 * M23) / d,
            (M21 * M32 - M22 * M31) / d, (M12 * M31 - M11 * M32) / d, (M11 * M22 - M12 * M21) / d,
            Vec3.Zero);
        return i with { T = -i.ApplyVector(T) };
    }
}

/// <summary>
/// What the Transform tool does to the selection: a scale along each axis, a tilt about each axis, mirrors and a move, about
/// a base point, along the axes of the world or turned by <see cref="AxesAzimuth"/> (the axes of an object). In this order:
/// mirror and scale, tilt about X, about Y, about Z, then the move. Tilts about X and Y follow the right-hand rule; the tilt
/// about Z is clockwise seen from above, as bearings and the turns of the program go.
/// </summary>
public sealed record SpatialTransform
{
    public Vec3 Pivot { get; init; }
    /// <summary>How far the axes of the frame are turned clockwise from those of the world (X east, Y north), °; 0 the world's.</summary>
    public double AxesAzimuth { get; init; }
    public double ScaleX { get; init; } = 1;
    public double ScaleY { get; init; } = 1;
    public double ScaleZ { get; init; } = 1;
    public double TiltX { get; init; }
    public double TiltY { get; init; }
    public double TiltZ { get; init; }
    public bool MirrorX { get; init; }
    public bool MirrorY { get; init; }
    public Vec3 Move { get; init; }

    public static readonly SpatialTransform None = new();

    /// <summary>Whether it changes nothing.</summary>
    public bool IsIdentity => ScaleX == 1 && ScaleY == 1 && ScaleZ == 1 && TiltX == 0 && TiltY == 0 && TiltZ == 0 && !MirrorX && !MirrorY && Move == Vec3.Zero;

    /// <summary>The same scale along the three axes.</summary>
    public bool IsUniformScale => Math.Abs(ScaleX - ScaleY) < 1e-12 && Math.Abs(ScaleY - ScaleZ) < 1e-12;

    /// <summary>Whether it tilts about X or Y: a vertical does not stay vertical.</summary>
    public bool TiltsVertical => Math.Abs(TiltX) > 1e-12 || Math.Abs(TiltY) > 1e-12;

    /// <summary>Whether it mirrors (one mirror; both together are a half turn).</summary>
    public bool Mirrors => MirrorX != MirrorY;

    /// <summary>Why it cannot be applied, or null: no scale of 0 or below (mirrors do that), no tilt about X or Y of 89° or more.</summary>
    public string? Problem =>
        ScaleX <= 0 || ScaleY <= 0 || ScaleZ <= 0 ? "The scale must be above 0" :
        Math.Abs(TiltX) >= 89 || Math.Abs(TiltY) >= 89 ? "A tilt about X or Y must be below 89°" : null;

    /// <summary>The map of points.</summary>
    public Affine3 Matrix
    {
        get
        {
            // into the frame (the axes turned to the bearing), there scale, mirror and tilt, back out, about the pivot
            var frame = Affine3.RotationZ(-AxesAzimuth);
            var local = Affine3.Scale(ScaleX * (MirrorX ? -1 : 1), ScaleY * (MirrorY ? -1 : 1), ScaleZ)
                .Then(Affine3.RotationX(TiltX)).Then(Affine3.RotationY(TiltY)).Then(Affine3.RotationZ(-TiltZ));
            return Affine3.Translation(-Pivot).Then(frame.Inverse()).Then(local).Then(frame).Then(Affine3.Translation(Pivot + Move));
        }
    }

    public Vec3 Apply(Vec3 p) => Matrix.Apply(p);

    /// <summary>How much longer a length in plan along the X and Y of the frame gets, on average (a text, a circle).</summary>
    public double PlanScale => (ScaleX + ScaleY) / 2;
}
