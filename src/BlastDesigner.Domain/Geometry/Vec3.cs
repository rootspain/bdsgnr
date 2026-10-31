using System.Text.Json.Serialization;

namespace BlastDesigner.Domain.Geometry;

/// <summary>Point or vector in project coordinates: X = easting, Y = northing, Z = elevation (metres).</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public static Vec3 operator *(double k, Vec3 a) => a * k;
    public static Vec3 operator /(Vec3 a, double k) => new(a.X / k, a.Y / k, a.Z / k);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    [JsonIgnore] public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    [JsonIgnore] public double Length2D => Math.Sqrt(X * X + Y * Y);

    public Vec3 Normalized()
    {
        var l = Length;
        return l < 1e-12 ? Zero : this / l;
    }

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static double Distance(Vec3 a, Vec3 b) => (a - b).Length;
    public static double Distance2D(Vec3 a, Vec3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + (b - a) * t;

    public Vec3 WithZ(double z) => new(X, Y, z);

    public override string ToString() => $"{X:0.###}, {Y:0.###}, {Z:0.###}";
}

public struct Bounds3
{
    public Vec3 Min;
    public Vec3 Max;
    public bool IsEmpty;

    public static Bounds3 Empty => new() { IsEmpty = true };

    public void Add(Vec3 p)
    {
        if (IsEmpty) { Min = Max = p; IsEmpty = false; return; }
        Min = new Vec3(Math.Min(Min.X, p.X), Math.Min(Min.Y, p.Y), Math.Min(Min.Z, p.Z));
        Max = new Vec3(Math.Max(Max.X, p.X), Math.Max(Max.Y, p.Y), Math.Max(Max.Z, p.Z));
    }

    public void Add(Bounds3 b)
    {
        if (b.IsEmpty) return;
        Add(b.Min);
        Add(b.Max);
    }

    public readonly Vec3 Center => (Min + Max) / 2;
    public readonly Vec3 Size => Max - Min;
}
