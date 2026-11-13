using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using SkiaSharp;

namespace BlastDesigner.Rendering;

/// <summary>
/// Depth of the surfaces on the screen, at half its resolution: for each cell the depth of the surface nearest the viewer
/// (a z-buffer filled on the CPU). With it what lies behind a surface (under it in the plan) is told from what is in front,
/// and drawn paler (View ▸ hidden paler) or not at all. Built again only when the camera or the surfaces change; while the
/// camera moves at a quarter of the cells.
/// </summary>
public sealed class DepthMap
{
    /// <summary>Screen pixels per cell along each side: with the camera still, and while it moves (four times fewer cells, quicker).</summary>
    public const int Cell = 2, MovingCell = 4;

    private readonly int _cell;

    private readonly float[] _depth;
    /// <summary>The surface nearest the viewer in each cell (its place in the list the map was built from), −1 where none is.</summary>
    private readonly int[] _owner;
    private readonly int _w, _h;
    private readonly float _eps;
    private readonly Dictionary<Guid, int> _index = new();
    private readonly Dictionary<(int, byte), SKImage> _masks = new();

    private DepthMap(int w, int h, float eps, int cell)
    {
        _cell = cell;
        _w = w;
        _h = h;
        _eps = eps;
        _depth = new float[w * h];
        _owner = new int[w * h];
        Array.Fill(_depth, float.NegativeInfinity);
        Array.Fill(_owner, -1);
    }

    /// <summary>
    /// The mask of a surface, a cell a pixel: <paramref name="hidden"/> (of 255) where another surface is in front of it, full
    /// elsewhere. Drawn over the surface with DstIn it fades it cell by cell, also inside a large triangle whose corners are
    /// all in sight. Null for a surface the map was not built with.
    /// </summary>
    public SKImage? MaskFor(SurfaceEntity s, byte hidden)
    {
        if (!_index.TryGetValue(s.Id, out var idx)) return null;
        lock (_masks)
        {
            if (_masks.TryGetValue((idx, hidden), out var made)) return made;
            // an alpha-only image is not taken by DstIn: the mask is in the alpha of black pixels (premultiplied)
            var pixels = new uint[_w * _h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (uint)(_owner[i] >= 0 && _owner[i] != idx ? hidden : (byte)255) << 24;
            var image = SKImage.FromPixelCopy(new SKImageInfo(_w, _h, SKColorType.Bgra8888, SKAlphaType.Premul), System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()), _w * 4)!;
            _masks[(idx, hidden)] = image;
            return image;
        }
    }

    /// <summary>Size of the map in screen pixels: what a mask covers.</summary>
    public SKRect ScreenRect => new(0, 0, _w * _cell, _h * _cell);

    /// <summary>
    /// Whether a point at a screen position and depth (towards the viewer, larger is nearer) is behind a surface. A point on a
    /// surface is not behind it: a margin of a few cells of slope and a decimetre is kept.
    /// </summary>
    public bool Hidden(float sx, float sy, float depth)
    {
        int x = (int)(sx / _cell), y = (int)(sy / _cell);
        if (x < 0 || y < 0 || x >= _w || y >= _h) return false;
        return _depth[y * _w + x] > depth + _eps;
    }

    public bool Hidden(Camera cam, Vec3 p)
    {
        var (x, y) = cam.ToScreen(p);
        return Hidden(x, y, (float)cam.Depth(p));
    }

    private static readonly object Gate = new();
    private static (int Key, DepthMap Map)? _last;

    /// <summary>The map of the surfaces for the camera; the last one again while nothing changed.</summary>
    public static DepthMap Of(Camera cam, IReadOnlyList<SurfaceEntity> surfaces, Func<SurfaceEntity, int> fingerprint, int cell = Cell)
    {
        var key = new HashCode();
        key.Add(cell);
        key.Add(cam.Target.X); key.Add(cam.Target.Y); key.Add(cam.Target.Z);
        key.Add(cam.Yaw); key.Add(cam.Pitch); key.Add(cam.Scale); key.Add(cam.Width); key.Add(cam.Height);
        foreach (var s in surfaces) { key.Add(s.Id); key.Add(fingerprint(s)); }
        var k = key.ToHashCode();
        lock (Gate)
        {
            if (_last is { } last && last.Key == k) return last.Map;
            var map = Build(cam, surfaces, cell);
            _last = (k, map);
            return map;
        }
    }

    private static DepthMap Build(Camera cam, IReadOnlyList<SurfaceEntity> surfaces, int cell)
    {
        int w = Math.Max(1, (int)Math.Ceiling(cam.Width / cell)), h = Math.Max(1, (int)Math.Ceiling(cam.Height / cell));
        // a cell's worth of slope on a steep face, and a decimetre, are not "behind"
        var map = new DepthMap(w, h, (float)(0.1 + 3.0 * cell / Math.Max(cam.Scale, 1e-6)), cell);
        var right = cam.Right; var up = cam.Up; var toViewer = cam.ToViewer; var target = cam.Target;
        double hw = cam.Width / 2, hh = cam.Height / 2, scale = cam.Scale;
        for (int si = 0; si < surfaces.Count; si++)
        {
            var s = surfaces[si];
            map._index.TryAdd(s.Id, si);
            var v = s.Vertices;
            var xs = new float[v.Count]; var ys = new float[v.Count]; var ds = new float[v.Count];
            for (int i = 0; i < v.Count; i++)
            {
                double dx = v[i].X - target.X, dy = v[i].Y - target.Y, dz = v[i].Z - target.Z;
                xs[i] = (float)((hw + (dx * right.X + dy * right.Y + dz * right.Z) * scale) / cell);
                ys[i] = (float)((hh - (dx * up.X + dy * up.Y + dz * up.Z) * scale) / cell);
                ds[i] = (float)(dx * toViewer.X + dy * toViewer.Y + dz * toViewer.Z);
            }
            var t = s.Triangles;
            for (int i = 0; i + 2 < t.Count; i += 3) map.Fill(xs, ys, ds, t[i], t[i + 1], t[i + 2], si);
        }
        return map;
    }

    /// <summary>Fills a triangle's cells with its depth where it is nearer than what is there (edge functions, cell centres).</summary>
    private void Fill(float[] xs, float[] ys, float[] ds, int a, int b, int c, int owner)
    {
        float ax = xs[a], ay = ys[a], bx = xs[b], by = ys[b], cx = xs[c], cy = ys[c];
        var area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (Math.Abs(area) < 1e-6f) return;
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx)))), x1 = Math.Min(_w - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy)))), y1 = Math.Min(_h - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
        if (x0 > x1 || y0 > y1) return;
        var inv = 1 / area;
        float da = ds[a], db = ds[b], dc = ds[c];
        for (int y = y0; y <= y1; y++)
        {
            var py = y + 0.5f;
            for (int x = x0; x <= x1; x++)
            {
                var px = x + 0.5f;
                // barycentric weights from the edge functions; the sign of the area takes either winding
                var wa = ((bx - px) * (cy - py) - (by - py) * (cx - px)) * inv;
                var wb = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) * inv;
                var wc = 1 - wa - wb;
                if (wa < -1e-4f || wb < -1e-4f || wc < -1e-4f) continue;
                var d = wa * da + wb * db + wc * dc;
                var at = y * _w + x;
                if (d > _depth[at]) { _depth[at] = d; _owner[at] = owner; }
            }
        }
    }
}
