using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>Triangle of the timing triangulation: plane a·x + b·y + c·t = d (unit normal), burden relief |∇t| (ms/m).</summary>
public sealed class TimingTriangle
{
    public int V1, V2, V3;
    public double A, B, C, D;
    /// <summary>|∇t| in ms/m; −1 for triangles touching the super-triangle.</summary>
    public double Slope = -1;
    public bool Valid;
    public bool Internal;
}

/// <summary>
/// Timing triangulation used by First movement, Burden relief and timing contours
/// (SPISDelaunayCalc → EVIETimingDelaunay : EVIEModifiedDelaunay : EVIEBaseDelaunay), ported one to one:
/// incremental Delaunay with a super-triangle and Lawson flips, the modified in-circle test, the time plane per triangle,
/// the "too thin" filter (including the original's squared/linear typo) and the slope range.
/// </summary>
public sealed class TimingDelaunay
{
    static readonly int[] Next = { 0, 2, 3, 1 };

    sealed class Tri
    {
        public readonly int[] V = new int[4];
        public readonly int[] E = new int[4];
        public void Set(int v1, int v2, int v3, int e1, int e2, int e3) { V[1] = v1; V[2] = v2; V[3] = v3; E[1] = e1; E[2] = e2; E[3] = e3; }
    }

    readonly List<Tri> _tris = new();
    int _lastSuper = -1;
    int _lastTriLoc;

    /// <summary>Vertices: X, Y, elevation; three super points first.</summary>
    public List<Vec3> Vertices { get; } = new();
    /// <summary>Time of each vertex (0 for the super points).</summary>
    public List<double> Times { get; } = new();
    public List<TimingTriangle> Triangles { get; } = new();
    public int Duplicates { get; private set; }
    public double MinSlope { get; private set; }
    public double MaxSlope { get; private set; }
    /// <summary>Sorted times of the real vertices (Zvalues).</summary>
    public double[] SortedTimes { get; private set; } = Array.Empty<double>();

    /// <summary>Builds the triangulation of (point, time) pairs, inserted in the given order.</summary>
    public static TimingDelaunay Build(IReadOnlyList<(Vec3 Point, double Time)> points)
    {
        var d = new TimingDelaunay();
        if (points.Count == 0) return d;
        double xmin = points.Min(p => p.Point.X), xmax = points.Max(p => p.Point.X);
        double ymin = points.Min(p => p.Point.Y), ymax = points.Max(p => p.Point.Y);
        d.CreateSuperPoints(xmin, ymin, xmax, ymax);
        var stack = new Stack<Tri>();
        foreach (var (p, t) in points) d.InsertPoint(p, t, stack, 0.001);
        d.SetupZvalues();
        return d;
    }

    /// <summary>Centroid of a triangle (TriMidPt).</summary>
    public Vec3 MidPoint(TimingTriangle t) => (Vertices[t.V1] + Vertices[t.V2] + Vertices[t.V3]) / 3;

    /// <summary>Plan area of a triangle.</summary>
    public double Area(TimingTriangle t)
    {
        Vec3 a = Vertices[t.V1], b = Vertices[t.V2], c = Vertices[t.V3];
        return 0.5 * Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));
    }

    void CreateSuperPoints(double xmin, double ymin, double xmax, double ymax)
    {
        double cx = (xmax + xmin) / 2, cy = (ymax + ymin) / 2;
        double s = xmax - xmin + 1;
        if (ymax - ymin > s) s = ymax - ymin + 1;
        AddVertex(new Vec3(cx, cy + 1.4 * s, 0), 0);
        AddVertex(new Vec3(cx - 1.6 * s, cy - 0.7 * s, 0), 0);
        AddVertex(new Vec3(cx + 1.6 * s, cy - 0.7 * s, 0), 0);
        var t = new Tri();
        t.Set(0, 1, 2, -1, -1, -1);
        _tris.Add(t);
        _lastSuper = 2;
    }

    void AddVertex(Vec3 p, double time) { Vertices.Add(p); Times.Add(time); }

    int GetEdge(int itri, int jtri)
    {
        var t = _tris[itri];
        if (t.E[1] == jtri) return 1;
        if (t.E[2] == jtri) return 2;
        if (t.E[3] == jtri) return 3;
        throw new InvalidOperationException($"Delaunay: triangles {itri} and {jtri} are not adjacent.");
    }

    /// <summary>EVIEModifiedDelaunay.InCircle: on a near-cocircular quad the diagonal joins the vertices closer in elevation.</summary>
    static bool InCircle(Vec3 p1, Vec3 p2, Vec3 p3, Vec3 pp)
    {
        double x13 = p1.X - p3.X, y13 = p1.Y - p3.Y, x23 = p2.X - p3.X, y23 = p2.Y - p3.Y;
        double x1p = p1.X - pp.X, y1p = p1.Y - pp.Y, x2p = p2.X - pp.X, y2p = p2.Y - pp.Y;
        double cosa = x13 * x23 + y13 * y23;
        double cosb = x2p * x1p + y1p * y2p;
        double na = Math.Sqrt((x13 * x13 + y13 * y13) * (x23 * x23 + y23 * y23));
        double ca = na != 0 ? cosa / na : 0;
        double nb = Math.Sqrt((x1p * x1p + y1p * y1p) * (x2p * x2p + y2p * y2p));
        double cb = nb != 0 ? cosb / nb : 0;
        if (ca > -0.0001 && cb > -0.0001)
        {
            if (Math.Abs(ca) < 0.0001 && Math.Abs(cb) < 0.0001) return Math.Abs(pp.Z - p3.Z) < Math.Abs(p1.Z - p2.Z);
            return false;
        }
        if (cosa < 0 && cosb < 0) return true;
        return (x13 * y23 - x23 * y13) * cosb + (x2p * y1p - x1p * y2p) * cosa < 0;
    }

    int TriLoc(Vec3 pt, out Tri tri)
    {
        int count = 0, side = 1, max = 4 * _tris.Count;
        if (_lastTriLoc < 0 || _lastTriLoc >= _tris.Count) _lastTriLoc = _tris.Count - 1;
        tri = _tris[_lastTriLoc];
        do
        {
            var a = Vertices[tri.V[side]];
            var b = Vertices[tri.V[Next[side]]];
            if ((a.Y - pt.Y) * (b.X - pt.X) > (a.X - pt.X) * (b.Y - pt.Y))
            {
                _lastTriLoc = _tris[_lastTriLoc].E[side];
                if (_lastTriLoc > -1) tri = _tris[_lastTriLoc];
                side = 1;
                if (count > max) throw new InvalidOperationException("Delaunay: point location failed.");
                count++;
            }
            else side++;
        }
        while (side <= 3 && _lastTriLoc >= 0);
        return _lastTriLoc;
    }

    void InsertPoint(Vec3 p, double time, Stack<Tri> stack, double eps)
    {
        stack.Clear();
        int it = TriLoc(p, out var tri);
        int e1 = tri.E[1], e2 = tri.E[2], e3 = tri.E[3];
        int v1 = tri.V[1], v2 = tri.V[2], v3 = tri.V[3];
        AddVertex(p, time);
        // A duplicate stays in the vertex list (as in the original) but is not connected.
        foreach (var v in new[] { v1, v2, v3 })
            if (Math.Abs(p.X - Vertices[v].X) < eps && Math.Abs(p.Y - Vertices[v].Y) < eps) { Duplicates++; return; }
        int np = Vertices.Count - 1;
        int n = _tris.Count;
        tri.Set(np, v1, v2, n + 1, e1, n);
        var t2 = new Tri(); _tris.Add(t2); t2.Set(np, v2, v3, it, e2, n + 1);
        var t3 = new Tri(); _tris.Add(t3); t3.Set(np, v3, v1, n, e3, it);
        if (e1 > -1) stack.Push(tri);
        if (e2 > -1) { _tris[e2].E[GetEdge(e2, it)] = n; stack.Push(t2); }
        if (e3 > -1) { _tris[e3].E[GetEdge(e3, it)] = n + 1; stack.Push(t3); }
        while (stack.Count > 0)
        {
            tri = stack.Pop();
            int iop = tri.E[2];
            var op = _tris[iop];
            int k;
            it = op.E[1];
            if (it > -1 && _tris[it] == tri) k = 1;
            else
            {
                it = op.E[2];
                if (it > -1 && _tris[it] == tri) k = 2;
                else { it = op.E[3]; k = 3; }
            }
            int k2 = Next[k], k3 = Next[k2];
            v1 = op.V[k]; v2 = op.V[k2]; v3 = op.V[k3];
            if (InCircle(Vertices[v1], Vertices[v2], Vertices[v3], p))
            {
                e1 = op.E[k2]; e2 = op.E[k3]; e3 = tri.E[3];
                tri.V[3] = v3; tri.E[2] = e1; tri.E[3] = iop;
                op.V[1] = np; op.V[2] = v3; op.V[3] = v1;
                op.E[1] = it; op.E[2] = e2; op.E[3] = e3;
                if (e1 > -1) { _tris[e1].E[GetEdge(e1, iop)] = it; stack.Push(tri); }
                if (e2 > -1) stack.Push(op);
                if (e3 > -1) _tris[e3].E[GetEdge(e3, it)] = iop;
            }
        }
    }

    static double Det3(double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
        a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);

    /// <summary>EVIETimingDelaunay.SetupZvalues: time plane, slope and validity of every triangle, slope range.</summary>
    void SetupZvalues()
    {
        SortedTimes = Times.Skip(_lastSuper + 1).OrderBy(t => t).ToArray();
        MaxSlope = 0;
        MinSlope = 1e10;
        Triangles.Clear();
        foreach (var src in _tris)
        {
            var t = new TimingTriangle { V1 = src.V[1], V2 = src.V[2], V3 = src.V[3] };
            t.Internal = t.V1 > _lastSuper && t.V2 > _lastSuper && t.V3 > _lastSuper;
            Triangles.Add(t);
            if (!t.Internal) continue;
            double x = Vertices[t.V1].X, y = Vertices[t.V1].Y, s = Times[t.V1];
            double x2 = Vertices[t.V2].X, y2 = Vertices[t.V2].Y, s2 = Times[t.V2];
            double x3 = Vertices[t.V3].X, y3 = Vertices[t.V3].Y, s3 = Times[t.V3];
            t.A = Det3(y, s, 1, y2, s2, 1, y3, s3, 1);
            t.B = -Det3(x, s, 1, x2, s2, 1, x3, s3, 1);
            t.C = Det3(x, y, 1, x2, y2, 1, x3, y3, 1);
            double area = 0.5 * Math.Abs((x2 - x) * (y3 - y) - (y2 - y) * (x3 - x));
            // The original squares only the X difference of side 1–2 (Math.Pow(y2 − y, 1.0)); kept so the filter matches.
            double l2 = Math.Pow(x2 - x, 2.0) + Math.Pow(y2 - y, 1.0);
            double q = Math.Pow(x3 - x, 2.0) + Math.Pow(y3 - y, 2.0);
            if (q > l2) l2 = q;
            q = Math.Pow(x3 - x2, 2.0) + Math.Pow(y3 - y2, 2.0);
            if (q > l2) l2 = q;
            t.Valid = area > 0.05 * l2;
            double n = Math.Sqrt(t.A * t.A + t.B * t.B + t.C * t.C);
            if (Math.Abs(n) > 0.0001)
            {
                t.A /= n; t.B /= n; t.C /= n;
                t.D = t.A * x + t.B * y + t.C * s;
                if (t.C != 0) t.Slope = Math.Abs(Math.Sqrt(1 - t.C * t.C) / t.C);
                else { t.Slope = 0; t.Valid = false; }
                if (t.Valid)
                {
                    MaxSlope = Math.Max(t.Slope, MaxSlope);
                    MinSlope = Math.Min(t.Slope, MinSlope);
                }
            }
        }
        if (MaxSlope - MinSlope < 0.01) MaxSlope = Math.Ceiling(MinSlope);
        if (MaxSlope - MinSlope < 0.01) MaxSlope = MinSlope + 1;
    }
}

/// <summary>First movement: one arrow per valid triangle.</summary>
public static class FirstMovement
{
    public sealed record Arrow(Vec3 Position, double DirX, double DirY);

    /// <summary>Default arrow size, m ("TimingCalculation/ArrowSize").</summary>
    public const double DefaultArrowSize = 2.0;

    /// <summary>
    /// Point and time of every hole as the triangulation is fed: the middle of the first
    /// firing charge at its firing time. Holes without a primed deck use the collar; holes without a time are left out.
    /// </summary>
    /// <summary>Firing point and time of every timed hole; the <paramref name="moved"/> holes shifted by <paramref name="offset"/> (a drag in progress).</summary>
    public static List<(Vec3 Point, double Time)> Inputs(BlastProject project, ISet<Guid>? moved = null, Vec3 offset = default)
    {
        var list = new List<(Vec3, double)>();
        foreach (var h in project.Holes)
        {
            if (h.FiringTimeMs is not double t) continue;
            var first = Charging.Primers(h).OrderBy(p => Charging.PrimerDelay(project, p.Primer)).FirstOrDefault();
            var point = first.Deck != null ? h.PointAtDepth((first.Deck.Top + first.Deck.Bottom) / 2) : h.Collar;
            if (moved != null && moved.Contains(h.Id)) point += offset;
            list.Add((point, t));
        }
        return list;
    }

    public static TimingDelaunay Triangulate(BlastProject project) => TimingDelaunay.Build(Inputs(project));

    /// <summary>
    /// Arrows at the centroids of internal valid triangles, pointing along the horizontal part of the plane normal,
    /// i.e. down the time gradient (towards earlier holes). Nearly flat planes (|(a, b)| &lt; 0.01) get no arrow.
    /// </summary>
    public static List<Arrow> Arrows(TimingDelaunay d)
    {
        var list = new List<Arrow>();
        foreach (var t in d.Triangles)
        {
            if (!t.Internal || !t.Valid) continue;
            var l = Math.Sqrt(t.A * t.A + t.B * t.B);
            if (l < 0.01) continue;
            list.Add(new Arrow(d.MidPoint(t), t.A / l, t.B / l));
        }
        return list;
    }

    /// <summary>Area of the 7-point arrow of a given size (unit direction).</summary>
    public static double LargeArrowArea(double size) => Math.Abs(GeoMath.Area(LargeArrow(0, 0, size, 0).Select(p => new Vec3(p.X, p.Y, 0)).ToList()));

    /// <summary>The big arrow is drawn when the arrows cover less than 30 % of the triangulated area.</summary>
    public static bool UseLargeArrows(TimingDelaunay d, double arrowSize)
    {
        double area = 0, n = 0;
        foreach (var t in d.Triangles)
            if (t.Internal && t.Valid) { area += d.Area(t); n++; }
        return n * LargeArrowArea(arrowSize) / area < 0.3;
    }

    /// <summary>Small arrow: triangle with its tip at +d, base at −d, half-width 0.7·|d| (any y-up or y-down frame).</summary>
    public static (double X, double Y)[] SmallArrow(double xs, double ys, double xd, double yd)
    {
        const double k = 0.7;
        return new[] { (xs + xd, ys + yd), (xs - xd - k * yd, ys - yd + k * xd), (xs - xd + k * yd, ys - yd - k * xd) };
    }

    /// <summary>Large 7-point arrow (head of half-width 2/3·|d|, shaft of half-width 1/3·|d|).</summary>
    public static (double X, double Y)[] LargeArrow(double xs, double ys, double xd, double yd)
    {
        double hx = 2 * yd / 3, hy = 2 * xd / 3, sx = hx / 2, sy = hy / 2;
        return new[]
        {
            (xs + xd, ys + yd), (xs - hx, ys + hy), (xs - sx, ys + sy), (xs - xd - sx, ys - yd + sy),
            (xs - xd + sx, ys - yd - sy), (xs + sx, ys - sy), (xs + hx, ys - hy)
        };
    }

    /// <summary>ColorFromHSV as used for the arrow fill (hue in degrees; negative hues fall to the last sector, as there).</summary>
    public static (byte R, byte G, byte B) ColorFromHsv(double hue, double saturation, double value)
    {
        int sector = Convert.ToInt32(Math.Floor(hue / 60.0)) % 6;
        double f = hue / 60.0 - Math.Floor(hue / 60.0);
        value *= 255.0;
        int v = Convert.ToInt32(value);
        int p = Convert.ToInt32(value * (1.0 - saturation));
        int q = Convert.ToInt32(value * (1.0 - f * saturation));
        int t = Convert.ToInt32(value * (1.0 - (1.0 - f) * saturation));
        (int r, int g, int b) = sector switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q)
        };
        return ((byte)r, (byte)g, (byte)b);
    }

    /// <summary>CSV export of the original's hidden button: X, Y, dirX, dirY per arrow.</summary>
    public static string ToCsv(IEnumerable<Arrow> arrows) =>
        string.Join(Environment.NewLine, arrows.Select(a => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2},{1:F2},{2:F4},{3:F4}", a.Position.X, a.Position.Y, a.DirX, a.DirY)));
}
