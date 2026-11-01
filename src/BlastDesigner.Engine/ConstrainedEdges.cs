namespace BlastDesigner.Engine;

/// <summary>
/// Forces given segments into a Delaunay triangulation as edges (constrained Delaunay): the triangles crossing a segment
/// are flipped until the segment is an edge (Sloan's method), then the new edges are flipped back towards Delaunay
/// wherever that does not cut a forced edge. Survey lines were only densified into points before, and triangles still
/// cut across crests and toes where the points around them were sparse.
/// Works in plan on local coordinates; the triangles stay counter-clockwise.
/// </summary>
internal sealed class ConstrainedEdges
{
    private readonly double[] _x, _y;
    private readonly List<int> _t;                       // 3 vertex indices per triangle, counter-clockwise
    private readonly Dictionary<long, int> _edge = new(Mixed.Instance); // directed edge (a, b) -> triangle that has it
    private readonly int[] _vertexTri;                    // a triangle at each vertex
    private readonly HashSet<long> _fixed = new(Mixed.Instance); // forced edges, undirected

    /// <summary>
    /// Hash for edge keys. The default hash of a long is its two halves xor-ed, so the edges of nearby vertices
    /// (close indices) all collided and building a survey surface took ten times longer.
    /// </summary>
    private sealed class Mixed : IEqualityComparer<long>
    {
        public static readonly Mixed Instance = new();
        public bool Equals(long a, long b) => a == b;
        public int GetHashCode(long k) => (int)(((ulong)k * 0x9E3779B97F4A7C15UL) >> 32);
    }

    public ConstrainedEdges(double[] x, double[] y, List<int> triangles)
    {
        _x = x; _y = y; _t = triangles;
        _vertexTri = Enumerable.Repeat(-1, x.Length).ToArray();
        for (int t = 0; t < _t.Count / 3; t++) Index(t);
    }

    /// <summary>Segments that could not be forced: they cross another forced segment or pass through a vertex.</summary>
    public int Skipped { get; private set; }

    private static long Key(int a, int b) => ((long)a << 32) | (uint)b;
    private static long Undirected(int a, int b) => a < b ? Key(a, b) : Key(b, a);

    private void Index(int t)
    {
        int a = _t[3 * t], b = _t[3 * t + 1], c = _t[3 * t + 2];
        _edge[Key(a, b)] = t; _edge[Key(b, c)] = t; _edge[Key(c, a)] = t;
        _vertexTri[a] = t; _vertexTri[b] = t; _vertexTri[c] = t;
    }

    private double Orient(int a, int b, int c) => (_x[b] - _x[a]) * (_y[c] - _y[a]) - (_y[b] - _y[a]) * (_x[c] - _x[a]);

    /// <summary>Orientation scaled to a distance from the line a–b, so one tolerance fits short and long segments.</summary>
    private double Side(int a, int b, int c)
    {
        var len = Math.Sqrt((_x[b] - _x[a]) * (_x[b] - _x[a]) + (_y[b] - _y[a]) * (_y[b] - _y[a]));
        return len < 1e-12 ? 0 : Orient(a, b, c) / len;
    }

    private const double OnLine = 1e-7;

    private bool Crosses(int a, int b, int u, int v) =>
        Side(a, b, u) * Side(a, b, v) < 0 && Side(u, v, a) * Side(u, v, b) < 0
        && Math.Abs(Side(a, b, u)) > OnLine && Math.Abs(Side(a, b, v)) > OnLine;

    private bool InCircle(int a, int b, int c, int d)
    {
        double ax = _x[a] - _x[d], ay = _y[a] - _y[d], bx = _x[b] - _x[d], by = _y[b] - _y[d], cx = _x[c] - _x[d], cy = _y[c] - _y[d];
        double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
        var det = a2 * (bx * cy - cx * by) - b2 * (ax * cy - cx * ay) + c2 * (ax * by - bx * ay);
        var scale = a2 + b2 + c2;
        return det > 1e-10 * scale * scale;
    }

    /// <summary>Third vertex of triangle t opposite the directed edge (a, b).</summary>
    private int Opposite(int t, int a, int b)
    {
        for (int k = 0; k < 3; k++)
        {
            var v = _t[3 * t + k];
            if (v != a && v != b) return v;
        }
        return -1;
    }

    /// <summary>Flips the edge u–v when the two triangles around it form a convex quad; false otherwise.</summary>
    private bool Flip(int u, int v, out int p, out int q)
    {
        p = q = -1;
        if (!_edge.TryGetValue(Key(u, v), out var t1) || !_edge.TryGetValue(Key(v, u), out var t2)) return false;
        p = Opposite(t1, u, v);
        q = Opposite(t2, v, u);
        if (p < 0 || q < 0) return false;
        // u and v strictly on opposite sides of p–q: the quad u, q, v, p is convex
        if (!(Orient(p, q, u) * Orient(p, q, v) < 0)) return false;
        _edge.Remove(Key(u, v)); _edge.Remove(Key(v, u));
        // t1 (u, v, p) and t2 (v, u, q) become (u, q, p) and (q, v, p), both counter-clockwise
        _t[3 * t1] = u; _t[3 * t1 + 1] = q; _t[3 * t1 + 2] = p;
        _t[3 * t2] = q; _t[3 * t2 + 1] = v; _t[3 * t2 + 2] = p;
        Index(t1); Index(t2);
        return true;
    }

    /// <summary>Makes a–b an edge of the triangulation and keeps it from being flipped away.</summary>
    public void Force(int a, int b)
    {
        if (a == b) return;
        if (_edge.ContainsKey(Key(a, b)) || _edge.ContainsKey(Key(b, a))) { _fixed.Add(Undirected(a, b)); return; }
        var crossed = CrossedEdges(a, b);
        if (crossed == null) { Skipped++; return; }
        var queue = new Queue<(int U, int V)>(crossed);
        var created = new List<(int, int)>();
        int guard = 50 * queue.Count + 100;
        while (queue.Count > 0 && guard-- > 0)
        {
            var (u, v) = queue.Dequeue();
            if (!Flip(u, v, out var p, out var q)) { queue.Enqueue((u, v)); continue; }
            if (Crosses(a, b, p, q)) queue.Enqueue((p, q));
            else created.Add((p, q));
        }
        if (!(_edge.ContainsKey(Key(a, b)) || _edge.ContainsKey(Key(b, a)))) { Skipped++; return; }
        _fixed.Add(Undirected(a, b));
        Restore(created);
    }

    /// <summary>
    /// The edges the segment a–b crosses, in order from a, walking the triangles; null when it crosses a forced edge or
    /// runs through a vertex.
    /// </summary>
    private List<(int, int)>? CrossedEdges(int a, int b)
    {
        // the triangle at a whose angle contains the direction to b
        int t = _vertexTri[a], x = -1, y = -1;
        for (int turn = 0; turn < 10_000; turn++)
        {
            int k = _t[3 * t] == a ? 0 : _t[3 * t + 1] == a ? 1 : 2;
            int tx = _t[3 * t + (k + 1) % 3], ty = _t[3 * t + (k + 2) % 3];
            if (Side(a, tx, b) > OnLine && Side(a, b, ty) > OnLine) { x = tx; y = ty; break; }
            if ((Math.Abs(Side(a, b, tx)) <= OnLine && Dot(a, b, tx) > 0) || (Math.Abs(Side(a, b, ty)) <= OnLine && Dot(a, b, ty) > 0)) return null;
            // next triangle counter-clockwise around a: across the edge (ty, a)
            if (!_edge.TryGetValue(Key(a, ty), out t)) return null;
        }
        if (x < 0) return null;
        var result = new List<(int, int)>();
        // walk across: x stays right of a→b, y left
        for (int step = 0; step < 100_000; step++)
        {
            if (_fixed.Contains(Undirected(x, y))) return null;
            result.Add((x, y));
            if (!_edge.TryGetValue(Key(y, x), out var next)) return null;
            var w = Opposite(next, y, x);
            if (w == b) return result;
            var s = Side(a, b, w);
            if (Math.Abs(s) <= OnLine) return null;
            if (s < 0) x = w; else y = w;
        }
        return null;
    }

    private double Dot(int a, int b, int c) => (_x[b] - _x[a]) * (_x[c] - _x[a]) + (_y[b] - _y[a]) * (_y[c] - _y[a]);

    /// <summary>Flips edges made while forcing a segment back towards Delaunay, except forced ones (Lawson's flips).</summary>
    private void Restore(List<(int U, int V)> edges)
    {
        var queue = new Queue<(int U, int V)>(edges);
        int guard = 20 * queue.Count + 100;
        while (queue.Count > 0 && guard-- > 0)
        {
            var (u, v) = queue.Dequeue();
            if (_fixed.Contains(Undirected(u, v))) continue;
            if (!_edge.TryGetValue(Key(u, v), out var t1) || !_edge.TryGetValue(Key(v, u), out var t2)) continue;
            var p = Opposite(t1, u, v);
            var q = Opposite(t2, v, u);
            if (p < 0 || q < 0 || !InCircle(u, v, p, q)) continue;
            if (!Flip(u, v, out p, out q)) continue;
            // the quad's outer edges may now break the empty-circle rule
            queue.Enqueue((u, q)); queue.Enqueue((q, v)); queue.Enqueue((v, p)); queue.Enqueue((p, u));
        }
    }
}
