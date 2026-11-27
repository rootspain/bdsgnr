using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Xml.Linq;

namespace BlastDesigner.App.Services;

/// <summary>
/// The SVG that Skia writes of a drawing, drawn again with GDI into an enhanced metafile (EMF): the vector picture Word
/// pastes as lines and AutoCAD as a metafile it can explode. Only what Skia writes is read: groups and their clips, paths
/// (M L H V C Q Z), rectangles, ellipses and circles, lines, polylines, polygons, texts with the place of each letter, and
/// the transforms on them. Pictures, gradients and patterns are left out.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SvgToEmf
{
    /// <summary>Logical units a unit of the SVG: GDI takes whole numbers, so a twentieth of a unit is kept.</summary>
    private const double Q = 20;

    /// <summary>
    /// The metafile of the SVG, a unit of it so many millimetres on the page; its handle, to be put on the clipboard or
    /// deleted (DeleteEnhMetaFile), zero when GDI made none.
    /// </summary>
    public static IntPtr Convert(byte[] svg, double mmPerUnit)
    {
        XDocument doc;
        using (var stream = new MemoryStream(svg)) doc = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        var root = doc.Root;
        if (root == null) return IntPtr.Zero;
        double w = Length(root, "width", 100), h = Length(root, "height", 100);
        var frame = new RECT { Left = 0, Top = 0, Right = (int)Math.Round(w * mmPerUnit * 100), Bottom = (int)Math.Round(h * mmPerUnit * 100) };
        var hdc = CreateEnhMetaFileW(IntPtr.Zero, null, ref frame, "Blast Designer\0Section\0\0");
        if (hdc == IntPtr.Zero) return IntPtr.Zero;
        // the metafile is drawn in the pixels of the screen it refers to: so many a millimetre
        var pxPerMmX = GetDeviceCaps(hdc, HORZRES) / (double)Math.Max(1, GetDeviceCaps(hdc, HORZSIZE));
        var pxPerMmY = GetDeviceCaps(hdc, VERTRES) / (double)Math.Max(1, GetDeviceCaps(hdc, VERTSIZE));
        SetGraphicsMode(hdc, GM_ADVANCED);
        SetBkMode(hdc, TRANSPARENT);
        var page = new Matrix(mmPerUnit * pxPerMmX, 0, 0, mmPerUnit * pxPerMmY, 0, 0);
        // nothing beyond the picture
        IntersectClipRect(hdc, 0, 0, (int)Math.Ceiling(w * page.A), (int)Math.Ceiling(h * page.D));
        var clips = root.Descendants().Where(e => e.Name.LocalName == "clipPath" && e.Attribute("id") != null)
            .GroupBy(e => (string)e.Attribute("id")!).ToDictionary(g => g.Key, g => g.First());
        var context = new Context(hdc, clips, w, h);
        Walk(context, root, page, new Style());
        return CloseEnhMetaFile(hdc);
    }

    /// <summary>The bytes of a metafile, to check it holds something; 0 for none.</summary>
    public static uint Size(IntPtr emf) => emf == IntPtr.Zero ? 0 : GetEnhMetaFileBits(emf, 0, null);

    public static void Delete(IntPtr emf) { if (emf != IntPtr.Zero) DeleteEnhMetaFile(emf); }

    private sealed record Context(IntPtr Hdc, Dictionary<string, XElement> Clips, double Width, double Height);

    // ---- the elements ------------------------------------------------------------------------

    private static void Walk(Context c, XElement e, Matrix parent, Style inherited)
    {
        var name = e.Name.LocalName;
        if (name is "clipPath" or "defs" or "mask" or "pattern" or "linearGradient" or "radialGradient" or "title" or "desc" or "metadata" or "image" or "style" or "symbol") return;
        var style = inherited.With(e);
        var m = name == "svg" ? parent : parent * Transform(e);
        var clipped = Clip(c, e, m);
        try
        {
            switch (name)
            {
                case "svg": case "g": case "a":
                    foreach (var child in e.Elements()) Walk(c, child, m, style);
                    break;
                case "text":
                    Text(c, e, m, style);
                    break;
                default:
                    Shape(c, e, m, style);
                    break;
            }
        }
        finally { if (clipped) RestoreDC(c.Hdc, -1); }
    }

    /// <summary>The clip of an element, its shapes in the space of the element: kept until the element is drawn.</summary>
    private static bool Clip(Context c, XElement e, Matrix m)
    {
        var url = (string?)e.Attribute("clip-path");
        if (url == null || !url.StartsWith("url(#") || !c.Clips.TryGetValue(url[5..].TrimEnd(')'), out var clip)) return false;
        var shape = clip.Elements().FirstOrDefault(x => x.Name.LocalName is "rect" or "path" or "ellipse" or "circle" or "polygon");
        if (shape == null) return false;
        SaveDC(c.Hdc);
        World(c, m * Transform(clip) * Transform(shape));
        BeginPath(c.Hdc);
        Geometry(c, shape);
        EndPath(c.Hdc);
        SelectClipPath(c.Hdc, RGN_AND);
        return true;
    }

    private static void Shape(Context c, XElement e, Matrix m, Style style)
    {
        var name = e.Name.LocalName;
        if (name is not ("path" or "rect" or "ellipse" or "circle" or "line" or "polyline" or "polygon")) return;
        var fill = name is "line" ? null : style.Fill;
        var stroke = style.Stroke;
        // the white the drawing is laid on: left out, the picture goes on the page as it is
        if (name == "rect" && stroke == null && fill == 0xFFFFFF && Number(e, "width") >= c.Width && Number(e, "height") >= c.Height) return;
        if (fill == null && stroke == null) return;
        World(c, m);
        BeginPath(c.Hdc);
        Geometry(c, e);
        EndPath(c.Hdc);
        SetPolyFillMode(c.Hdc, style.EvenOdd ? ALTERNATE : WINDING);
        IntPtr brush = IntPtr.Zero, pen = IntPtr.Zero;
        try
        {
            if (fill is { } f) { brush = CreateSolidBrush(Colorref(Blend(f, style.FillOpacity * style.Opacity))); SelectObject(c.Hdc, brush); }
            if (stroke is { } s) { pen = Pen(Blend(s, style.StrokeOpacity * style.Opacity), style); SelectObject(c.Hdc, pen); }
            if (fill != null && stroke != null) StrokeAndFillPath(c.Hdc);
            else if (fill != null) FillPath(c.Hdc);
            else StrokePath(c.Hdc);
        }
        finally
        {
            SelectObject(c.Hdc, GetStockObject(NULL_BRUSH));
            SelectObject(c.Hdc, GetStockObject(BLACK_PEN));
            if (brush != IntPtr.Zero) DeleteObject(brush);
            if (pen != IntPtr.Zero) DeleteObject(pen);
        }
    }

    private static IntPtr Pen(uint rgb, Style style)
    {
        // a stroke of no width is the thinnest line: half a unit
        var width = style.StrokeWidth > 0 ? style.StrokeWidth : 0.5;
        var cap = style.Cap switch { "round" => PS_ENDCAP_ROUND, "square" => PS_ENDCAP_SQUARE, _ => PS_ENDCAP_FLAT };
        var join = style.Join switch { "round" => PS_JOIN_ROUND, "bevel" => PS_JOIN_BEVEL, _ => PS_JOIN_MITER };
        var brush = new LOGBRUSH { lbStyle = BS_SOLID, lbColor = Colorref(rgb) };
        var dashes = style.Dashes?.Select(d => (uint)Math.Max(1, Math.Round(d * Q))).ToArray();
        if (dashes is { Length: > 0 })
        {
            // GDI wants the dashes in pairs
            if (dashes.Length % 2 == 1) dashes = dashes.Concat(dashes).ToArray();
            return ExtCreatePen(PS_GEOMETRIC | PS_USERSTYLE | cap | join, (uint)Math.Max(1, Math.Round(width * Q)), ref brush, (uint)dashes.Length, dashes);
        }
        return ExtCreatePen(PS_GEOMETRIC | PS_SOLID | cap | join, (uint)Math.Max(1, Math.Round(width * Q)), ref brush, 0, null);
    }

    /// <summary>The figures of a shape into the path being built, in logical units.</summary>
    private static void Geometry(Context c, XElement e)
    {
        var hdc = c.Hdc;
        switch (e.Name.LocalName)
        {
            case "rect":
            {
                double x = Number(e, "x"), y = Number(e, "y"), w = Number(e, "width"), h = Number(e, "height");
                Move(hdc, x, y); Line(hdc, x + w, y); Line(hdc, x + w, y + h); Line(hdc, x, y + h); CloseFigure(hdc);
                break;
            }
            case "ellipse": case "circle":
            {
                double cx = Number(e, "cx"), cy = Number(e, "cy");
                double rx = e.Name.LocalName == "circle" ? Number(e, "r") : Number(e, "rx"), ry = e.Name.LocalName == "circle" ? rx : Number(e, "ry");
                // four quarters, each a cubic
                const double k = 0.5522847498;
                Move(hdc, cx + rx, cy);
                Cubic(hdc, cx + rx, cy + ry * k, cx + rx * k, cy + ry, cx, cy + ry);
                Cubic(hdc, cx - rx * k, cy + ry, cx - rx, cy + ry * k, cx - rx, cy);
                Cubic(hdc, cx - rx, cy - ry * k, cx - rx * k, cy - ry, cx, cy - ry);
                Cubic(hdc, cx + rx * k, cy - ry, cx + rx, cy - ry * k, cx + rx, cy);
                CloseFigure(hdc);
                break;
            }
            case "line":
                Move(hdc, Number(e, "x1"), Number(e, "y1")); Line(hdc, Number(e, "x2"), Number(e, "y2"));
                break;
            case "polyline": case "polygon":
            {
                var n = Numbers((string?)e.Attribute("points") ?? "");
                for (int i = 0; i + 1 < n.Count; i += 2) { if (i == 0) Move(hdc, n[0], n[1]); else Line(hdc, n[i], n[i + 1]); }
                if (e.Name.LocalName == "polygon" && n.Count >= 4) CloseFigure(hdc);
                break;
            }
            case "path":
                PathData(hdc, (string?)e.Attribute("d") ?? "");
                break;
        }
    }

    /// <summary>The figures of path data: M L H V C S Q T Z, absolute and relative (an arc goes straight to its end).</summary>
    private static void PathData(IntPtr hdc, string d)
    {
        var tokens = Tokens(d);
        int i = 0;
        char cmd = 'M';
        double x = 0, y = 0, sx = 0, sy = 0, lcx = 0, lcy = 0, lqx = 0, lqy = 0;
        char last = ' ';
        double Next()
        {
            if (i >= tokens.Count || tokens[i] is not double v) return 0;
            i++;
            return v;
        }
        bool HasNumber() => i < tokens.Count && tokens[i] is double;
        while (i < tokens.Count)
        {
            if (tokens[i] is char letter) { cmd = letter; i++; }
            else if (cmd is 'M') cmd = 'L';
            else if (cmd is 'm') cmd = 'l';
            var rel = char.IsLower(cmd);
            double ox = rel ? x : 0, oy = rel ? y : 0;
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    x = ox + Next(); y = oy + Next(); sx = x; sy = y; Move(hdc, x, y);
                    break;
                case 'L':
                    x = ox + Next(); y = oy + Next(); Line(hdc, x, y);
                    break;
                case 'H':
                    x = ox + Next(); Line(hdc, x, y);
                    break;
                case 'V':
                    y = oy + Next(); Line(hdc, x, y);
                    break;
                case 'C':
                {
                    double x1 = ox + Next(), y1 = oy + Next(), x2 = ox + Next(), y2 = oy + Next();
                    x = ox + Next(); y = oy + Next();
                    Cubic(hdc, x1, y1, x2, y2, x, y); lcx = x2; lcy = y2;
                    break;
                }
                case 'S':
                {
                    double x1 = last is 'C' or 'S' ? 2 * x - lcx : x, y1 = last is 'C' or 'S' ? 2 * y - lcy : y;
                    double x2 = ox + Next(), y2 = oy + Next();
                    x = ox + Next(); y = oy + Next();
                    Cubic(hdc, x1, y1, x2, y2, x, y); lcx = x2; lcy = y2;
                    break;
                }
                case 'Q':
                {
                    double qx = ox + Next(), qy = oy + Next(), ex = ox + Next(), ey = oy + Next();
                    Quad(hdc, x, y, qx, qy, ex, ey); x = ex; y = ey; lqx = qx; lqy = qy;
                    break;
                }
                case 'T':
                {
                    double qx = last is 'Q' or 'T' ? 2 * x - lqx : x, qy = last is 'Q' or 'T' ? 2 * y - lqy : y;
                    double ex = ox + Next(), ey = oy + Next();
                    Quad(hdc, x, y, qx, qy, ex, ey); x = ex; y = ey; lqx = qx; lqy = qy;
                    break;
                }
                case 'A':
                    for (int k = 0; k < 5; k++) Next();
                    x = ox + Next(); y = oy + Next(); Line(hdc, x, y);
                    break;
                case 'Z':
                    CloseFigure(hdc); x = sx; y = sy;
                    // a Z takes no numbers: any after it are skipped
                    while (HasNumber()) i++;
                    break;
                default:
                    // a letter not known: what it took is skipped
                    while (HasNumber()) i++;
                    break;
            }
            last = char.ToUpperInvariant(cmd);
        }
    }

    private static void Text(Context c, XElement e, Matrix m, Style style)
    {
        if (style.Fill is not { } color) return;
        // the words without the line breaks and tabs the file is laid out with
        var text = string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value)).Trim('\r', '\n', '\t');
        if (text.Length == 0) return;
        var xs = Numbers((string?)e.Attribute("x") ?? "0");
        var ys = Numbers((string?)e.Attribute("y") ?? "0");
        if (xs.Count == 0) xs.Add(0);
        if (ys.Count == 0) ys.Add(0);
        World(c, m);
        var font = CreateFontW(-(int)Math.Max(1, Math.Round(style.FontSize * Q)), 0, 0, 0, style.Bold ? 700 : 400, style.Italic ? 1u : 0, 0, 0, DEFAULT_CHARSET, OUT_TT_PRECIS, 0, ANTIALIASED_QUALITY, 0, style.FontFamily);
        var old = SelectObject(c.Hdc, font);
        try
        {
            SetTextColor(c.Hdc, Colorref(Blend(color, style.FillOpacity * style.Opacity)));
            var perLetter = xs.Count == text.Length && text.Length > 1;
            // the places of the letters are where they start; one place is where the anchor says
            var align = perLetter ? TA_LEFT : style.Anchor switch { "middle" => TA_CENTER, "end" => TA_RIGHT, _ => TA_LEFT };
            SetTextAlign(c.Hdc, TA_BASELINE | TA_NOUPDATECP | align);
            if (perLetter && ys.Distinct().Count() > 1 && ys.Count == text.Length)
            {
                // letters each on a height of its own: one by one
                for (int i = 0; i < text.Length; i++) ExtTextOutW(c.Hdc, L(xs[i]), L(ys[i]), 0, IntPtr.Zero, text[i].ToString(), 1, null);
            }
            else if (perLetter)
            {
                // the advance of each letter to the next, the last as the one before it (it is not used)
                var dx = new int[text.Length];
                for (int i = 0; i + 1 < text.Length; i++) dx[i] = L(xs[i + 1]) - L(xs[i]);
                dx[^1] = text.Length > 1 ? dx[^2] : 0;
                ExtTextOutW(c.Hdc, L(xs[0]), L(ys[0]), 0, IntPtr.Zero, text, (uint)text.Length, dx);
            }
            else ExtTextOutW(c.Hdc, L(xs[0]), L(ys[0]), 0, IntPtr.Zero, text, (uint)text.Length, null);
        }
        finally
        {
            SelectObject(c.Hdc, old);
            DeleteObject(font);
        }
    }

    // ---- drawing helpers ---------------------------------------------------------------------

    /// <summary>The transform of the element with its units brought to the logical ones (a twentieth).</summary>
    private static void World(Context c, Matrix m)
    {
        var w = m * new Matrix(1 / Q, 0, 0, 1 / Q, 0, 0);
        var x = new XFORM { eM11 = (float)w.A, eM12 = (float)w.B, eM21 = (float)w.C, eM22 = (float)w.D, eDx = (float)w.E, eDy = (float)w.F };
        SetWorldTransform(c.Hdc, ref x);
    }

    private static int L(double v) => (int)Math.Round(v * Q);
    private static void Move(IntPtr hdc, double x, double y) => MoveToEx(hdc, L(x), L(y), IntPtr.Zero);
    private static void Line(IntPtr hdc, double x, double y) => LineTo(hdc, L(x), L(y));
    private static void Cubic(IntPtr hdc, double x1, double y1, double x2, double y2, double x, double y) =>
        PolyBezierTo(hdc, new[] { new POINT(L(x1), L(y1)), new POINT(L(x2), L(y2)), new POINT(L(x), L(y)) }, 3);
    private static void Quad(IntPtr hdc, double x0, double y0, double qx, double qy, double x, double y) =>
        Cubic(hdc, x0 + 2.0 / 3 * (qx - x0), y0 + 2.0 / 3 * (qy - y0), x + 2.0 / 3 * (qx - x), y + 2.0 / 3 * (qy - y), x, y);

    /// <summary>A colour as GDI takes it (0x00BBGGRR) from 0xRRGGBB.</summary>
    private static uint Colorref(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    /// <summary>A colour seen through so much of itself on white: GDI draws no transparency.</summary>
    private static uint Blend(uint rgb, double alpha)
    {
        if (alpha >= 1) return rgb;
        alpha = Math.Clamp(alpha, 0, 1);
        uint Mix(uint v) => (uint)Math.Round(v * alpha + 255 * (1 - alpha));
        return (Mix((rgb >> 16) & 0xFF) << 16) | (Mix((rgb >> 8) & 0xFF) << 8) | Mix(rgb & 0xFF);
    }

    // ---- reading the SVG ---------------------------------------------------------------------

    /// <summary>What an element is drawn with, as it inherits it from its groups.</summary>
    private sealed class Style
    {
        public uint? Fill { get; private set; } = 0x000000;
        public uint? Stroke { get; private set; }
        public double StrokeWidth { get; private set; } = 1;
        public double FillOpacity { get; private set; } = 1;
        public double StrokeOpacity { get; private set; } = 1;
        public double Opacity { get; private set; } = 1;
        public double[]? Dashes { get; private set; }
        public string Cap { get; private set; } = "butt";
        public string Join { get; private set; } = "miter";
        public bool EvenOdd { get; private set; }
        public double FontSize { get; private set; } = 12;
        public string FontFamily { get; private set; } = "Segoe UI";
        public bool Bold { get; private set; }
        public bool Italic { get; private set; }
        public string Anchor { get; private set; } = "start";

        public Style With(XElement e)
        {
            var s = (Style)MemberwiseClone();
            // the opacity of a group is not inherited as such, but its children are drawn through it
            var attributes = e.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value.Trim());
            if (attributes.TryGetValue("style", out var css))
                foreach (var part in css.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    if (part.Split(':', 2) is { Length: 2 } kv) attributes[kv[0].Trim()] = kv[1].Trim();
            foreach (var (key, value) in attributes)
            {
                switch (key)
                {
                    case "fill": s.Fill = Color(value); break;
                    case "stroke": s.Stroke = Color(value); break;
                    case "stroke-width": s.StrokeWidth = Parse(value, s.StrokeWidth); break;
                    case "fill-opacity": s.FillOpacity = Parse(value, 1); break;
                    case "stroke-opacity": s.StrokeOpacity = Parse(value, 1); break;
                    case "opacity": s.Opacity *= Parse(value, 1); break;
                    case "stroke-dasharray": s.Dashes = value == "none" ? null : Numbers(value).ToArray(); break;
                    case "stroke-linecap": s.Cap = value; break;
                    case "stroke-linejoin": s.Join = value; break;
                    case "fill-rule": s.EvenOdd = value == "evenodd"; break;
                    case "font-size": s.FontSize = Parse(value.Replace("px", "").Replace("pt", ""), s.FontSize); break;
                    case "font-family": s.FontFamily = value.Split(',')[0].Trim().Trim('\'', '"') is { Length: > 0 } family ? family : s.FontFamily; break;
                    case "font-weight": s.Bold = value is "bold" or "bolder" || (int.TryParse(value, out var weight) && weight >= 600); break;
                    case "font-style": s.Italic = value is "italic" or "oblique"; break;
                    case "text-anchor": s.Anchor = value; break;
                }
            }
            return s;
        }
    }

    /// <summary>0xRRGGBB of an SVG colour; null for none (or what is not read).</summary>
    private static uint? Color(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (value is "none" or "transparent" or "") return null;
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 3) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
            return hex.Length >= 6 && uint.TryParse(hex[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        if (value.StartsWith("rgb"))
        {
            var n = Numbers(value[(value.IndexOf('(') + 1)..].TrimEnd(')'));
            return n.Count >= 3 ? ((uint)Math.Clamp(n[0], 0, 255) << 16) | ((uint)Math.Clamp(n[1], 0, 255) << 8) | (uint)Math.Clamp(n[2], 0, 255) : null;
        }
        return value switch
        {
            "black" => 0x000000, "white" => 0xFFFFFF, "red" => 0xFF0000, "green" => 0x008000, "blue" => 0x0000FF,
            "yellow" => 0xFFFF00, "gray" or "grey" => 0x808080, "orange" => 0xFFA500, "currentcolor" => 0x000000,
            _ => null
        };
    }

    /// <summary>The transform attribute of an element: matrix, translate, scale, rotate, skewX, skewY, one after another.</summary>
    private static Matrix Transform(XElement e)
    {
        var t = (string?)e.Attribute("transform");
        var m = Matrix.Identity;
        if (string.IsNullOrWhiteSpace(t)) return m;
        int at = 0;
        while (at < t.Length)
        {
            var open = t.IndexOf('(', at);
            if (open < 0) break;
            var close = t.IndexOf(')', open);
            if (close < 0) break;
            var kind = t[at..open].Trim(' ', ',', '\t', '\n', '\r');
            var n = Numbers(t[(open + 1)..close]);
            double N(int i, double fallback = 0) => i < n.Count ? n[i] : fallback;
            m = m * kind switch
            {
                "matrix" => new Matrix(N(0, 1), N(1), N(2), N(3, 1), N(4), N(5)),
                "translate" => new Matrix(1, 0, 0, 1, N(0), N(1)),
                "scale" => new Matrix(N(0, 1), 0, 0, n.Count > 1 ? n[1] : N(0, 1), 0, 0),
                "rotate" => Rotate(N(0), N(1), N(2)),
                "skewX" => new Matrix(1, 0, Math.Tan(N(0) * Math.PI / 180), 1, 0, 0),
                "skewY" => new Matrix(1, Math.Tan(N(0) * Math.PI / 180), 0, 1, 0, 0),
                _ => Matrix.Identity
            };
            at = close + 1;
        }
        return m;
    }

    private static Matrix Rotate(double degrees, double cx, double cy)
    {
        var r = degrees * Math.PI / 180;
        double cos = Math.Cos(r), sin = Math.Sin(r);
        return new Matrix(1, 0, 0, 1, cx, cy) * new Matrix(cos, sin, -sin, cos, 0, 0) * new Matrix(1, 0, 0, 1, -cx, -cy);
    }

    private static double Number(XElement e, string name) => Parse((string?)e.Attribute(name) ?? "0", 0);
    private static double Length(XElement e, string name, double fallback) => Parse(((string?)e.Attribute(name) ?? "").Replace("px", "").Replace("pt", ""), fallback);
    private static double Parse(string s, double fallback) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>The numbers of a list, by commas or spaces.</summary>
    private static List<double> Numbers(string s) => Tokens(s).OfType<double>().ToList();

    /// <summary>The letters and numbers of path data (or of any list of numbers).</summary>
    private static List<object> Tokens(string s)
    {
        var list = new List<object>();
        int i = 0;
        while (i < s.Length)
        {
            var ch = s[i];
            if (char.IsWhiteSpace(ch) || ch == ',') { i++; continue; }
            if (char.IsLetter(ch) && ch is not ('e' or 'E')) { list.Add(ch); i++; continue; }
            // a number: a sign, digits, one point, an exponent
            int start = i;
            if (ch is '+' or '-') i++;
            bool dot = false;
            while (i < s.Length && (char.IsDigit(s[i]) || (s[i] == '.' && !dot))) { if (s[i] == '.') dot = true; i++; }
            if (i < s.Length && s[i] is 'e' or 'E')
            {
                int back = i;
                i++;
                if (i < s.Length && s[i] is '+' or '-') i++;
                if (i < s.Length && char.IsDigit(s[i])) { while (i < s.Length && char.IsDigit(s[i])) i++; }
                else i = back;
            }
            if (i == start) { i++; continue; }
            if (double.TryParse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) list.Add(v);
        }
        return list;
    }

    /// <summary>An affine transform as SVG writes it: x' = A x + C y + E, y' = B x + D y + F.</summary>
    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);
        /// <summary>The right one done first, then the left.</summary>
        public static Matrix operator *(Matrix l, Matrix r) => new(
            l.A * r.A + l.C * r.B, l.B * r.A + l.D * r.B,
            l.A * r.C + l.C * r.D, l.B * r.C + l.D * r.D,
            l.A * r.E + l.C * r.F + l.E, l.B * r.E + l.D * r.F + l.F);
    }

    // ---- GDI ---------------------------------------------------------------------------------

    private const int HORZSIZE = 4, VERTSIZE = 6, HORZRES = 8, VERTRES = 10;
    private const int GM_ADVANCED = 2, TRANSPARENT = 1, RGN_AND = 1, ALTERNATE = 1, WINDING = 2;
    private const uint TA_LEFT = 0, TA_RIGHT = 2, TA_CENTER = 6, TA_BASELINE = 24, TA_NOUPDATECP = 0;
    private const uint PS_GEOMETRIC = 0x10000, PS_SOLID = 0, PS_USERSTYLE = 7;
    private const uint PS_ENDCAP_ROUND = 0, PS_ENDCAP_SQUARE = 0x100, PS_ENDCAP_FLAT = 0x200;
    private const uint PS_JOIN_ROUND = 0, PS_JOIN_BEVEL = 0x1000, PS_JOIN_MITER = 0x2000;
    private const uint BS_SOLID = 0, DEFAULT_CHARSET = 1, OUT_TT_PRECIS = 4, ANTIALIASED_QUALITY = 4;
    private const int NULL_BRUSH = 5, BLACK_PEN = 7;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct XFORM { public float eM11, eM12, eM21, eM22, eDx, eDy; }
    [StructLayout(LayoutKind.Sequential)] private struct LOGBRUSH { public uint lbStyle; public uint lbColor; public UIntPtr lbHatch; }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateEnhMetaFileW(IntPtr hdcRef, string? fileName, ref RECT frame, string description);
    [DllImport("gdi32.dll")] private static extern IntPtr CloseEnhMetaFile(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteEnhMetaFile(IntPtr emf);
    [DllImport("gdi32.dll")] private static extern uint GetEnhMetaFileBits(IntPtr emf, uint size, byte[]? buffer);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("gdi32.dll")] private static extern int SetGraphicsMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetWorldTransform(IntPtr hdc, ref XFORM xform);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextAlign(IntPtr hdc, uint align);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetPolyFillMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern int IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool RestoreDC(IntPtr hdc, int saved);
    [DllImport("gdi32.dll")] private static extern bool BeginPath(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool EndPath(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool CloseFigure(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool StrokePath(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool FillPath(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool StrokeAndFillPath(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool SelectClipPath(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll")] private static extern bool LineTo(IntPtr hdc, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool PolyBezierTo(IntPtr hdc, POINT[] points, uint count);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr ExtCreatePen(uint style, uint width, ref LOGBRUSH brush, uint styleCount, uint[]? styles);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool ExtTextOutW(IntPtr hdc, int x, int y, uint options, IntPtr rect, string text, uint count, int[]? dx);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int obj);
}
