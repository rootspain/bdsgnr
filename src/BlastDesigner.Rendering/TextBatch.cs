using SkiaSharp;

namespace BlastDesigner.Rendering;

/// <summary>
/// Labels collected for one draw per colour and size. Hundreds of hole numbers and tie delays drawn one by one took a
/// large part of a frame; as one text blob they are a single draw. Glyphs are placed by their advances, as DrawText does.
/// </summary>
internal sealed class TextBatch
{
    private readonly Dictionary<(SKColor Color, float Size), List<(string Text, float X, float Y)>> _groups = new();

    // Glyphs and advances by typeface, size and text: hole numbers and delays repeat from frame to frame, and turning
    // each into glyphs through the native library was most of the cost. One cache per thread (the viewport draws on the
    // render thread, exports on the UI thread); emptied when it grows large.
    [ThreadStatic] private static Dictionary<(IntPtr Typeface, float Size, string Text), (ushort[] Glyphs, float[] Advances)>? _glyphs;

    public void Add(string text, float x, float y, SKColor color, float size)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!_groups.TryGetValue((color, size), out var list)) _groups[(color, size)] = list = new();
        list.Add((text, x, y));
    }

    /// <summary>Draws everything collected with the typeface and antialiasing of <paramref name="paint"/>, then empties the batch.</summary>
    public void Flush(SKCanvas c, SKPaint paint)
    {
        if (_groups.Count == 0) return;
        var color = paint.Color;
        var size = paint.TextSize;
        foreach (var ((col, sz), list) in _groups)
        {
            paint.TextSize = sz;
            using var font = paint.ToFont();
            using var builder = new SKTextBlobBuilder();
            var cache = _glyphs ??= new();
            if (cache.Count > 20_000) cache.Clear();
            var typeface = paint.Typeface?.Handle ?? IntPtr.Zero;
            foreach (var (text, x, y) in list)
            {
                if (!cache.TryGetValue((typeface, sz, text), out var g))
                {
                    var glyphs = new ushort[font.CountGlyphs(text)];
                    font.GetGlyphs(text, glyphs);
                    var widths = new float[glyphs.Length];
                    font.GetGlyphWidths(glyphs, widths, Span<SKRect>.Empty);
                    cache[(typeface, sz, text)] = g = (glyphs, widths);
                }
                if (g.Glyphs.Length == 0) continue;
                var run = builder.AllocatePositionedRun(font, g.Glyphs.Length);
                g.Glyphs.CopyTo(run.GetGlyphSpan());
                var pos = run.GetPositionSpan();
                var cx = x;
                for (int i = 0; i < g.Glyphs.Length; i++) { pos[i] = new SKPoint(cx, y); cx += g.Advances[i]; }
            }
            using var blob = builder.Build();
            if (blob == null) continue;
            paint.Color = col;
            c.DrawText(blob, 0, 0, paint);
        }
        paint.Color = color;
        paint.TextSize = size;
        _groups.Clear();
    }
}

/// <summary>
/// Lines and filled triangles of any colour drawn as one triangle mesh, antialiased by a one-pixel fringe that fades to
/// transparent. Skia strokes wide antialiased paths on the CPU every frame; hundreds of tie arrows and charge columns
/// were most of a 3D frame that way, while a mesh is a single draw.
/// </summary>
internal sealed class LineMesh
{
    private readonly List<SKPoint> _points = new();
    private readonly List<SKColor> _colors = new();
    private const float Fringe = 0.5f;

    public bool IsEmpty => _points.Count == 0;

    private void Tri(SKPoint a, SKColor ca, SKPoint b, SKColor cb, SKPoint c, SKColor cc)
    {
        _points.Add(a); _points.Add(b); _points.Add(c);
        _colors.Add(ca); _colors.Add(cb); _colors.Add(cc);
    }

    private void Quad(SKPoint a, SKColor ca, SKPoint b, SKColor cb, SKPoint c, SKColor cc, SKPoint d, SKColor cd)
    {
        Tri(a, ca, b, cb, c, cc);
        Tri(a, ca, c, cc, d, cd);
    }

    /// <summary>A line of the given width with butt ends, as a solid core and a fading fringe on both sides.</summary>
    public void Line(SKColor color, float width, float x0, float y0, float x1, float y1)
    {
        float dx = x1 - x0, dy = y1 - y0, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f) return;
        float nx = -dy / len, ny = dx / len;
        float core = MathF.Max(width / 2 - Fringe, 0), outer = width / 2 + Fringe;
        // a line thinner than the fringe keeps its weight in the colour
        var solid = width < 2 * Fringe ? color.WithAlpha((byte)(color.Alpha * width / (2 * Fringe))) : color;
        var clear = color.WithAlpha(0);
        SKPoint P(float x, float y, float k) => new(x + nx * k, y + ny * k);
        var a0 = P(x0, y0, outer); var a1 = P(x0, y0, core); var a2 = P(x0, y0, -core); var a3 = P(x0, y0, -outer);
        var b0 = P(x1, y1, outer); var b1 = P(x1, y1, core); var b2 = P(x1, y1, -core); var b3 = P(x1, y1, -outer);
        Quad(a0, clear, b0, clear, b1, solid, a1, solid);
        if (core > 0) Quad(a1, solid, b1, solid, b2, solid, a2, solid);
        Quad(a2, solid, b2, solid, b3, clear, a3, clear);
    }

    /// <summary>A filled triangle with a fading fringe along its edges.</summary>
    public void Triangle(SKColor color, SKPoint a, SKPoint b, SKPoint c)
    {
        var clear = color.WithAlpha(0);
        Tri(a, color, b, color, c, color);
        // the fringe goes outwards from each edge, whichever way the triangle winds
        var cx = (a.X + b.X + c.X) / 3; var cy = (a.Y + b.Y + c.Y) / 3;
        void Edge(SKPoint p, SKPoint q)
        {
            float dx = q.X - p.X, dy = q.Y - p.Y, len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-3f) return;
            float nx = -dy / len, ny = dx / len;
            if ((p.X - cx) * nx + (p.Y - cy) * ny < 0) { nx = -nx; ny = -ny; }
            var po = new SKPoint(p.X + nx * 2 * Fringe, p.Y + ny * 2 * Fringe);
            var qo = new SKPoint(q.X + nx * 2 * Fringe, q.Y + ny * 2 * Fringe);
            Quad(p, color, q, color, qo, clear, po, clear);
        }
        Edge(a, b); Edge(b, c); Edge(c, a);
    }

    /// <summary>Draws everything collected in one call and empties the mesh.</summary>
    public void Flush(SKCanvas canvas)
    {
        if (_points.Count == 0) return;
        using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, _points.ToArray(), null, _colors.ToArray());
        using var paint = new SKPaint { IsAntialias = false };
        canvas.DrawVertices(vertices, SKBlendMode.Modulate, paint);
        _points.Clear();
        _colors.Clear();
    }
}
