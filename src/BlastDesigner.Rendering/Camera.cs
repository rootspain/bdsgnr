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
    public double Yaw { get => _yaw; set { _yaw = value; _basisValid = false; } }
    public double Pitch { get => _pitch; set { _pitch = value; _basisValid = false; } }
    /// <summary>Pixels per metre.</summary>
    public double Scale { get; set; } = 5;
    public double Width { get; set; } = 800;
    public double Height { get; set; } = 600;

    private double _yaw;
    private double _pitch = 90;
    // The view directions depend only on yaw and pitch; ToScreen runs for every vertex of every frame and every hit
    // test, so the trigonometry is done once per change instead of on each call.
    private bool _basisValid;
    private Vec3 _right, _up, _toViewer, _forward;

    private void Basis()
    {
        if (_basisValid) return;
        var y = _yaw * GeoMath.Deg;
        var p = _pitch * GeoMath.Deg;
        _right = new Vec3(Math.Cos(y), -Math.Sin(y), 0);
        _forward = new Vec3(Math.Sin(y), Math.Cos(y), 0);
        _up = _forward * Math.Sin(p) + new Vec3(0, 0, 1) * Math.Cos(p);
        _toViewer = new Vec3(0, 0, 1) * Math.Sin(p) - _forward * Math.Cos(p);
        _basisValid = true;
    }

    public bool IsPlan => Math.Abs(Pitch - 90) < 1e-6;

    public Vec3 Right { get { Basis(); return _right; } }

    private Vec3 ForwardHorizontal { get { Basis(); return _forward; } }

    public Vec3 Up { get { Basis(); return _up; } }

    /// <summary>Unit vector pointing from the scene towards the viewer.</summary>
    public Vec3 ToViewer { get { Basis(); return _toViewer; } }

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
