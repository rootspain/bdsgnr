using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Rendering;

/// <summary>
/// Orthographic camera. Pitch 90° looks straight down (plan view); 0° looks horizontally.
/// Yaw rotates the view about the vertical axis (north-up at 0°).
/// </summary>
public sealed class Camera
{
    public Vec3 Target { get; set; }
    public double Yaw { get; set; }
    public double Pitch { get; set; } = 90;
    /// <summary>Pixels per metre.</summary>
    public double Scale { get; set; } = 5;
    public double Width { get; set; } = 800;
    public double Height { get; set; } = 600;

    public bool IsPlan => Math.Abs(Pitch - 90) < 1e-6;

    public Vec3 Right
    {
        get
        {
            var y = Yaw * GeoMath.Deg;
            return new Vec3(Math.Cos(y), -Math.Sin(y), 0);
        }
    }

    private Vec3 ForwardHorizontal
    {
        get
        {
            var y = Yaw * GeoMath.Deg;
            return new Vec3(Math.Sin(y), Math.Cos(y), 0);
        }
    }

    public Vec3 Up
    {
        get
        {
            var p = Pitch * GeoMath.Deg;
            return ForwardHorizontal * Math.Sin(p) + new Vec3(0, 0, 1) * Math.Cos(p);
        }
    }

    /// <summary>Unit vector pointing from the scene towards the viewer.</summary>
    public Vec3 ToViewer
    {
        get
        {
            var p = Pitch * GeoMath.Deg;
            return new Vec3(0, 0, 1) * Math.Sin(p) - ForwardHorizontal * Math.Cos(p);
        }
    }

    public (float X, float Y) ToScreen(Vec3 p)
    {
        var d = p - Target;
        return ((float)(Width / 2 + Vec3.Dot(d, Right) * Scale), (float)(Height / 2 - Vec3.Dot(d, Up) * Scale));
    }

    /// <summary>Depth towards the viewer; larger values are closer.</summary>
    public double Depth(Vec3 p) => Vec3.Dot(p - Target, ToViewer);

    /// <summary>World point under a screen position, on the horizontal plane at <paramref name="planeZ"/>.</summary>
    public Vec3 ToWorld(double sx, double sy, double? planeZ = null)
    {
        var p = Target + Right * ((sx - Width / 2) / Scale) + Up * ((Height / 2 - sy) / Scale);
        var z0 = planeZ ?? Target.Z;
        var v = ToViewer;
        if (Math.Abs(v.Z) > 1e-6) p += v * ((z0 - p.Z) / v.Z);
        return p;
    }

    public void ZoomAt(double sx, double sy, double factor)
    {
        var before = ToWorld(sx, sy);
        Scale = Math.Clamp(Scale * factor, 1e-4, 1e5);
        var after = ToWorld(sx, sy);
        Target += before - after;
    }

    public void Pan(double dxPixels, double dyPixels) =>
        Target -= Right * (dxPixels / Scale) - Up * (dyPixels / Scale);

    /// <summary>
    /// Slides the target along the line of sight so that it sits at <paramref name="depth"/> (see <see cref="Depth"/>).
    /// The view is orthographic, so the picture does not change; only the point that <see cref="Orbit"/> turns about does.
    /// </summary>
    public void MoveTargetToDepth(double depth) => Target += ToViewer * depth;

    public void Orbit(double dYaw, double dPitch)
    {
        Yaw = GeoMath.NormalizeBearing(Yaw + dYaw);
        Pitch = Math.Clamp(Pitch + dPitch, -10, 90);
    }

    public void Fit(Bounds3 b, double margin = 0.1)
    {
        if (b.IsEmpty) return;
        Target = b.Center;
        // Size of the bounding box as seen by the camera.
        double w = 0, h = 0;
        var corners = new[]
        {
            b.Min, new Vec3(b.Max.X, b.Min.Y, b.Min.Z), new Vec3(b.Min.X, b.Max.Y, b.Min.Z), new Vec3(b.Max.X, b.Max.Y, b.Min.Z),
            new Vec3(b.Min.X, b.Min.Y, b.Max.Z), new Vec3(b.Max.X, b.Min.Y, b.Max.Z), new Vec3(b.Min.X, b.Max.Y, b.Max.Z), b.Max
        };
        foreach (var c in corners)
        {
            var d = c - Target;
            w = Math.Max(w, Math.Abs(Vec3.Dot(d, Right)) * 2);
            h = Math.Max(h, Math.Abs(Vec3.Dot(d, Up)) * 2);
        }
        w = Math.Max(w, 1);
        h = Math.Max(h, 1);
        Scale = Math.Min(Width / w, Height / h) * (1 - 2 * margin);
    }

    public Camera Clone() => (Camera)MemberwiseClone();

    public void SaveTo(ViewState v)
    {
        v.TargetX = Target.X; v.TargetY = Target.Y; v.TargetZ = Target.Z;
        v.Yaw = Yaw; v.Pitch = Pitch; v.Scale = Scale;
    }

    public void LoadFrom(ViewState v)
    {
        Target = new Vec3(v.TargetX, v.TargetY, v.TargetZ);
        Yaw = v.Yaw;
        Pitch = v.Pitch;
        Scale = v.Scale <= 0 ? 5 : v.Scale;
    }
}
