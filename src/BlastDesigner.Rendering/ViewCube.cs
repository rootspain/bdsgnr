using BlastDesigner.Domain.Geometry;

namespace BlastDesigner.Rendering;

/// <summary>
/// The view cube: a cube seen from above at a fixed tilt that turns with the view's azimuth. Each visible face is cut 3×3:
/// the middle stands for the face, the strips for the edges and the corner squares for the corners. A part looks from its
/// direction: the face's normal plus the normals of the faces next to it. The bottom is never looked from: the camera does
/// not go below the ground, so the strips along the bottom count as the face.
/// </summary>
public static class ViewCube
{
    /// <summary>The cube's tilt, degrees above the horizon.</summary>
    public const double Tilt = 30;

    /// <summary>A part of a face under the pointer: its outline on the screen and the direction it looks from.</summary>
    public sealed record Part(string Face, (float X, float Y)[] Outline, Vec3 From);

    /// <summary>A visible face: its name (Top, N, S, E, W), its outline and where its label goes.</summary>
    public sealed record Face(string Name, (float X, float Y)[] Outline, (float X, float Y) Centre);

    private static readonly (string Name, Vec3 Normal, Vec3 U, Vec3 V)[] Faces =
    {
        ("Top", new Vec3(0, 0, 1), new Vec3(1, 0, 0), new Vec3(0, 1, 0)),
        ("S", new Vec3(0, -1, 0), new Vec3(1, 0, 0), new Vec3(0, 0, 1)),
        ("N", new Vec3(0, 1, 0), new Vec3(-1, 0, 0), new Vec3(0, 0, 1)),
        ("E", new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
        ("W", new Vec3(-1, 0, 0), new Vec3(0, -1, 0), new Vec3(0, 0, 1)),
    };

    private static readonly double[] Cuts = { -1, -0.5, 0.5, 1 };

    /// <summary>The camera that draws the cube in a square of <paramref name="size"/> pixels for a view turned to <paramref name="yaw"/>.</summary>
    public static Camera CubeCamera(double yaw, double size) =>
        new() { Yaw = yaw, Pitch = Tilt, Width = size, Height = size, Scale = size / 3.4, Target = new Vec3(0, 0, 0) };

    private static IEnumerable<(string Name, Vec3 Normal, Vec3 U, Vec3 V)> Visible(Camera cam) =>
        Faces.Where(f => Vec3.Dot(f.Normal, cam.ToViewer) > 1e-6);

    /// <summary>The visible faces, back to front.</summary>
    public static IReadOnlyList<Face> VisibleFaces(Camera cam) =>
        Visible(cam).Select(f => new Face(f.Name, Quad(cam, f, -1, 1, -1, 1), cam.ToScreen(f.Normal)))
            .OrderBy(f => f.Name == "Top" ? 1 : 0).ToList();

    /// <summary>All parts of the visible faces.</summary>
    public static IReadOnlyList<Part> Parts(Camera cam)
    {
        var parts = new List<Part>();
        foreach (var f in Visible(cam))
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    var from = f.Normal;
                    if (i != 1) from += f.U * (i - 1);
                    if (j != 1) from += f.V * (j - 1);
                    // the bottom is not looked from
                    if (from.Z < 0) from = from.WithZ(0);
                    parts.Add(new Part(f.Name, Quad(cam, f, Cuts[i], Cuts[i + 1], Cuts[j], Cuts[j + 1]), Normal(from)));
                }
        return parts;
    }

    /// <summary>The part under a screen point, or null.</summary>
    public static Part? At(Camera cam, double x, double y) =>
        Parts(cam).FirstOrDefault(p => Inside(p.Outline, x, y));

    /// <summary>
    /// The view's pitch and yaw that look from <paramref name="from"/>. From straight above it is the plan, north up.
    /// </summary>
    public static (double Pitch, double Yaw) ViewFrom(Vec3 from)
    {
        var d = Normal(from);
        var h = Math.Sqrt(d.X * d.X + d.Y * d.Y);
        if (h < 1e-9) return (90, 0);
        var pitch = Math.Atan2(d.Z, h) / GeoMath.Deg;
        // the camera looks the other way from where it stands
        var yaw = Math.Atan2(-d.X, -d.Y) / GeoMath.Deg;
        // from the north the yaw is 180, not −180
        if (yaw <= -180 + 1e-9) yaw += 360;
        return (Math.Round(pitch, 6), Math.Round(yaw, 6));
    }

    /// <summary>True when the view looks from the direction of <paramref name="part"/>.</summary>
    public static bool Shows(Camera view, Part part) => Vec3.Dot(view.ToViewer, part.From) > 0.9999;

    private static Vec3 Normal(Vec3 v) => v * (1 / Math.Sqrt(Vec3.Dot(v, v)));

    private static (float X, float Y)[] Quad(Camera cam, (string Name, Vec3 Normal, Vec3 U, Vec3 V) f, double s0, double s1, double t0, double t1) =>
        new[] { (s0, t0), (s1, t0), (s1, t1), (s0, t1) }.Select(st => cam.ToScreen(f.Normal + f.U * st.Item1 + f.V * st.Item2)).ToArray();

    private static bool Inside((float X, float Y)[] poly, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > y) != (poly[j].Y > y) && x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }
}
