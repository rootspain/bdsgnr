namespace BlastDesigner.Domain.Geometry;

/// <summary>
/// A closed outline on the screen, drawn by hand (a lasso) or a box: what it rings, and which segments it cuts. Inside is by
/// the nonzero rule: a loop drawn twice or a figure eight rings all it goes round, it leaves no holes. The edges are kept in
/// horizontal bands, so a point is tested against the few edges of its band, not all of them.
/// </summary>
public sealed class Lasso
{
    /// <summary>How far the cursor goes sideways from the press before the first move decides window or crossing, px.</summary>
    public const double DecideAt = 8;

    private readonly (double X, double Y)[] _p;
    private readonly List<int>[] _bands;
    private readonly double _band;

    public double MinX { get; }
    public double MaxX { get; }
    public double MinY { get; }
    public double MaxY { get; }

    /// <summary>The outline through the points, closed from the last back to the first.</summary>
    public Lasso(IEnumerable<(double X, double Y)> points)
    {
        _p = points.ToArray();
        int n = _p.Length;
        if (n == 0)
        {
            _bands = Array.Empty<List<int>>();
            return;
        }
        (MinX, MaxX, MinY, MaxY) = (_p.Min(q => q.X), _p.Max(q => q.X), _p.Min(q => q.Y), _p.Max(q => q.Y));
        int count = Math.Clamp(n / 2, 1, 1024);
        _band = Math.Max((MaxY - MinY) / count, 1e-9);
        _bands = new List<int>[count];
        for (int k = 0; k < count; k++) _bands[k] = new List<int>();
        for (int i = 0; i < n; i++)
        {
            var (a, b) = (_p[i], _p[(i + 1) % n]);
            int k0 = Band(Math.Min(a.Y, b.Y)), k1 = Band(Math.Max(a.Y, b.Y));
            for (int k = k0; k <= k1; k++) _bands[k].Add(i);
        }
    }

    /// <summary>A box by two opposite corners.</summary>
    public static Lasso Rect(double x0, double y0, double x1, double y1) => new(new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) });

    /// <summary>The outline has no inside: fewer than three points.</summary>
    public bool Empty => _p.Length < 3;

    private int Band(double y) => Math.Clamp((int)Math.Floor((y - MinY) / _band), 0, _bands.Length - 1);

    /// <summary>The point is rung by the outline (nonzero winding).</summary>
    public bool Contains(double x, double y)
    {
        if (Empty || x < MinX || x > MaxX || y < MinY || y > MaxY) return false;
        int n = _p.Length, wn = 0;
        foreach (var i in _bands[Band(y)])
        {
            var (a, b) = (_p[i], _p[(i + 1) % n]);
            double left = (b.X - a.X) * (y - a.Y) - (x - a.X) * (b.Y - a.Y);
            if (a.Y <= y) { if (b.Y > y && left > 0) wn++; }
            else if (b.Y <= y && left < 0) wn--;
        }
        return wn != 0;
    }

    /// <summary>The segment cuts or touches an edge of the outline.</summary>
    public bool CutsEdge(double ax, double ay, double bx, double by)
    {
        if (_p.Length < 2) return false;
        if (Math.Max(ax, bx) < MinX || Math.Min(ax, bx) > MaxX || Math.Max(ay, by) < MinY || Math.Min(ay, by) > MaxY) return false;
        int n = _p.Length;
        for (int i = 0; i < n; i++)
        {
            var (c, d) = (_p[i], _p[(i + 1) % n]);
            if (Segments(ax, ay, bx, by, c.X, c.Y, d.X, d.Y)) return true;
        }
        return false;
    }

    /// <summary>Some part of the segment is inside: an end is rung, or it crosses the outline.</summary>
    public bool Crosses(double ax, double ay, double bx, double by) =>
        Contains(ax, ay) || Contains(bx, by) || CutsEdge(ax, ay, bx, by);

    private static bool Segments(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        double d1 = Side(cx, cy, dx, dy, ax, ay), d2 = Side(cx, cy, dx, dy, bx, by);
        double d3 = Side(ax, ay, bx, by, cx, cy), d4 = Side(ax, ay, bx, by, dx, dy);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
        return (d1 == 0 && On(cx, cy, dx, dy, ax, ay)) || (d2 == 0 && On(cx, cy, dx, dy, bx, by))
            || (d3 == 0 && On(ax, ay, bx, by, cx, cy)) || (d4 == 0 && On(ax, ay, bx, by, dx, dy));
    }

    private static double Side(double ax, double ay, double bx, double by, double px, double py) => (bx - ax) * (py - ay) - (px - ax) * (by - ay);

    private static bool On(double ax, double ay, double bx, double by, double px, double py) =>
        px >= Math.Min(ax, bx) && px <= Math.Max(ax, bx) && py >= Math.Min(ay, by) && py <= Math.Max(ay, by);

    /// <summary>
    /// Window or crossing of a drawn outline, like a box: the first move sideways decides, to the left is crossing. A curve
    /// that never went <see cref="DecideAt"/> sideways is decided by the side it went further to.
    /// </summary>
    public static bool IsCrossing(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count == 0) return false;
        double x0 = points[0].X, left = 0, right = 0;
        foreach (var (x, _) in points)
        {
            double dx = x - x0;
            if (Math.Abs(dx) >= DecideAt) return dx < 0;
            left = Math.Max(left, -dx);
            right = Math.Max(right, dx);
        }
        return left > right;
    }
}
