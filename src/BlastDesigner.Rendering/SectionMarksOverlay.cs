using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using SkiaSharp;

namespace BlastDesigner.Rendering;

/// <summary>
/// The marks drawn by hand on a section (<see cref="SectionMark"/>), over it, the same in the Section view window and on
/// its sheet: dimensions with their extension lines and ends, angles as arcs, levels across the frame with their figure
/// left of it, lines and texts, each in its colour, line type and weight. A mark whose hole or surface is gone is red.
/// What it drew is kept for the window to pick a mark and its grips.
/// </summary>
public sealed class SectionMarksOverlay : IOverlay
{
    private readonly Guid _line;
    private readonly SectionFrame _frame;
    private readonly SKColor _ink;
    private readonly float _font;
    /// <summary>The frame of the section on the screen: a level runs across it, its figure left of it.</summary>
    public Func<Camera, BlastProject, SKRect?>? Box { get; set; }
    private readonly double? _unitsPerMm;

    /// <summary>The colour a lost mark is drawn in.</summary>
    public static readonly SKColor LostColor = new(0xFFE5484D);

    /// <param name="box">The frame of the section on the screen: a level runs across it, its figure left of it.</param>
    /// <param name="unitsPerMm">On a sheet: its units a millimetre, for the weights; null on the screen (pixels).</param>
    public SectionMarksOverlay(Guid line, Vec3 a, Vec3 b, SKColor ink, float font, Func<Camera, BlastProject, SKRect?>? box = null, double? unitsPerMm = null)
    {
        _line = line; _frame = new SectionFrame(a, b); _ink = ink; _font = font; Box = box; _unitsPerMm = unitsPerMm;
    }

    /// <summary>The section line whose marks it draws.</summary>
    public Guid LineId => _line;
    /// <summary>The colour of the marks without one of their own.</summary>
    public SKColor Ink => _ink;

    /// <summary>The marks shown selected, with their grips.</summary>
    public ISet<Guid> Selected { get; init; } = new HashSet<Guid>();
    public SKColor SelectionColor { get; init; } = new(0xFFF5A623);
    /// <summary>A mark being drawn, shown as it will be.</summary>
    public SectionMark? Preview { get; set; }
    /// <summary>While the selected marks are dragged: how far, along the line and up, m; they are drawn there.</summary>
    public (double S, double Z)? MoveBy { get; set; }

    /// <summary>What a mark took on the screen when last drawn: its lines and the boxes of its figures.</summary>
    public sealed record Drawn(SectionMark Mark, List<(SKPoint A, SKPoint B)> Lines, List<SKRect> Texts, List<SKPoint> Grips);

    /// <summary>The marks as last drawn, the preview left out.</summary>
    public IReadOnlyList<Drawn> LastDrawn => _drawn;
    private readonly List<Drawn> _drawn = new();

    public SectionFrame Frame => _frame;

    public void Draw(SKCanvas canvas, Camera cam, BlastProject project)
    {
        _drawn.Clear();
        var box = Box?.Invoke(cam, project);
        foreach (var m in SectionMarking.Of(project, _line).ToList())
        {
            var d = DrawMark(canvas, cam, project, m, box, Selected.Contains(m.Id));
            if (d != null) _drawn.Add(d);
        }
        if (Preview is { } preview) DrawMark(canvas, cam, project, preview, box, false);
    }

    /// <summary>The mark nearest a screen point within the radius: by its lines and figures.</summary>
    public SectionMark? HitTest(double x, double y, double radius)
    {
        SectionMark? best = null;
        var bestD = radius;
        foreach (var d in _drawn)
        {
            var dist = d.Texts.Any(r => r.Contains((float)x, (float)y)) ? 0 : double.MaxValue;
            foreach (var (a, b) in d.Lines) dist = Math.Min(dist, SegmentDistance(a, b, x, y));
            if (dist <= bestD) { bestD = dist; best = d.Mark; }
        }
        return best;
    }

    /// <summary>
    /// The marks inside a screen box: all their lines, or a figure of theirs whole (a box drawn round the figure takes the
    /// mark, as one round the number of a hole takes the hole), from left to right; from right to left any of their lines
    /// or figures it touches.
    /// </summary>
    public IEnumerable<SectionMark> InRect(SKRect r, bool crossing)
    {
        foreach (var d in _drawn)
        {
            var points = d.Lines.SelectMany(l => new[] { l.A, l.B }).Concat(d.Texts.SelectMany(t => new[] { new SKPoint(t.Left, t.Top), new SKPoint(t.Right, t.Bottom) })).ToList();
            if (points.Count == 0) continue;
            var figure = d.Texts.Any(t => r.Contains(t));
            var touched = points.Any(p => r.Contains(p)) || d.Texts.Any(t => r.IntersectsWith(t)) || d.Lines.Any(l => Crosses(r, l.A, l.B));
            if (crossing ? touched : figure || points.All(p => r.Contains(p))) yield return d.Mark;
        }
    }

    /// <summary>The mark whose figure is under a screen point.</summary>
    public SectionMark? FigureAt(double x, double y) => _drawn.FirstOrDefault(d => d.Texts.Any(r => r.Contains((float)x, (float)y)))?.Mark;

    /// <summary>A line through a box, its ends outside it.</summary>
    private static bool Crosses(SKRect r, SKPoint a, SKPoint b)
    {
        // the line against each side of the box
        bool Cut(SKPoint c, SKPoint d)
        {
            double rx = b.X - a.X, ry = b.Y - a.Y, sx = d.X - c.X, sy = d.Y - c.Y;
            var den = rx * sy - ry * sx;
            if (Math.Abs(den) < 1e-9) return false;
            var t = ((c.X - a.X) * sy - (c.Y - a.Y) * sx) / den;
            var u = ((c.X - a.X) * ry - (c.Y - a.Y) * rx) / den;
            return t >= 0 && t <= 1 && u >= 0 && u <= 1;
        }
        SKPoint tl = new(r.Left, r.Top), tr = new(r.Right, r.Top), bl = new(r.Left, r.Bottom), br = new(r.Right, r.Bottom);
        return Cut(tl, tr) || Cut(tr, br) || Cut(br, bl) || Cut(bl, tl);
    }

    /// <summary>The grip of a selected mark at a screen point within the radius: the mark and the index of its point.</summary>
    public (SectionMark Mark, int Index)? GripAt(double x, double y, double radius)
    {
        foreach (var d in _drawn)
            for (int i = 0; i < d.Grips.Count; i++)
                if (Math.Abs(d.Grips[i].X - x) <= radius && Math.Abs(d.Grips[i].Y - y) <= radius && Selected.Contains(d.Mark.Id)) return (d.Mark, i);
        return null;
    }

    private static double SegmentDistance(SKPoint a, SKPoint b, double x, double y)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        var t = len2 > 0 ? Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len2, 0, 1) : 0;
        double cx = a.X + t * dx - x, cy = a.Y + t * dy - y;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    private Drawn? DrawMark(SKCanvas c, Camera cam, BlastProject project, SectionMark m, SKRect? box, bool selected)
    {
        var spots = SectionMarking.Resolve(project, _frame, m);
        if (spots.Count == 0) return null;
        if (selected && MoveBy is { } by) spots = spots.Select(s => new SectionSpot(s.S + by.S, s.Z + by.Z)).ToList();
        var lost = spots.Any(s => s.Lost);
        var color = selected ? SelectionColor : lost ? LostColor : m.Style.Color is uint argb ? new SKColor(argb) : _ink;
        var weight = _unitsPerMm is double u ? LineTypes.WeightOnSheet(m.Style.LineWeight, u) : LineTypes.WeightPixels(m.Style.LineWeight);
        var drawn = new Drawn(m, new(), new(), new());
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = weight, Color = color, StrokeCap = SKStrokeCap.Butt };
        // the lines of a dimension (its line, extension lines, arc, leader) and its own lines (a level, a line) take the
        // weight and the type of the mark
        using var thin = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = weight, Color = color };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        using var text = new SKPaint { IsAntialias = true, TextSize = (float)(_font * m.Style.TextScale), Color = color, Typeface = SKTypeface.Default, TextAlign = SKTextAlign.Center };
        var dash = LineTypes.ScreenPattern(LineTypes.Find(project, m.Style.LineType), _font * 1.5);
        // the type goes to all the lines of a mark: its own and those of a dimension (extension lines, arc, leader)
        using var dashes = dash != null ? SKPathEffect.CreateDash(dash, 0) : null;
        line.PathEffect = dashes;
        thin.PathEffect = dashes;
        // the ends solid: a short tick would fall in a gap
        using var solid = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = weight, Color = color };
        SKPoint P(SectionSpot s) { var (x, y) = cam.ToScreen(_frame.ToWorld(s.S, s.Z)); return new SKPoint(x, y); }
        var pts = spots.Select(P).ToList();
        drawn.Grips.AddRange(pts);
        var label = SectionMarking.Label(m, spots);

        void Line(SKPoint a, SKPoint b, SKPaint paint) { c.DrawLine(a, b, paint); drawn.Lines.Add((a, b)); }
        void End(SKPoint at, SKPoint toward)
        {
            float dx = toward.X - at.X, dy = toward.Y - at.Y;
            var len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-3) return;
            dx /= len; dy /= len;
            // the ends grow with a heavy line, to be seen on it
            var size = Math.Max(_font * 0.6f, weight * 4);
            switch (m.Style.Ends)
            {
                case SectionMarkEnds.Arrow:
                    using (var path = new SKPath())
                    {
                        path.MoveTo(at);
                        path.LineTo(at.X + dx * size - dy * size * 0.3f, at.Y + dy * size + dx * size * 0.3f);
                        path.LineTo(at.X + dx * size + dy * size * 0.3f, at.Y + dy * size - dx * size * 0.3f);
                        path.Close();
                        c.DrawPath(path, fill);
                    }
                    break;
                case SectionMarkEnds.Tick:
                    // a slash across the end, at 45° to the line
                    var t = size * 0.5f;
                    c.DrawLine(at.X - (dx - dy) * t, at.Y - (dy + dx) * t, at.X + (dx - dy) * t, at.Y + (dy + dx) * t, solid);
                    break;
                case SectionMarkEnds.Dot:
                    c.DrawCircle(at, size * 0.25f, fill);
                    break;
            }
        }
        void Text(string s, SKPoint at, SKTextAlign align = SKTextAlign.Center, float angle = 0)
        {
            if (s.Length == 0) return;
            text.TextAlign = align;
            var w = text.MeasureText(s);
            c.Save();
            c.RotateDegrees(angle, at.X, at.Y);
            c.DrawText(s, at.X, at.Y, text);
            c.Restore();
            var left = align == SKTextAlign.Center ? at.X - w / 2 : align == SKTextAlign.Right ? at.X - w : at.X;
            drawn.Texts.Add(new SKRect(left, at.Y - text.TextSize, left + w, at.Y + text.TextSize * 0.25f));
        }
        // a dimension line between a and b, with its ends, the figure over its middle (beside it when there is no room)
        void Dimension(SKPoint a, SKPoint b)
        {
            Line(a, b, thin);
            End(a, b); End(b, a);
            var mid = new SKPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            var angle = MathF.Atan2(b.Y - a.Y, b.X - a.X) * 180 / MathF.PI;
            if (angle > 90) angle -= 180;
            if (angle < -90) angle += 180;
            // up and down: the figure upright, right of the line, as the length of a hole is written
            if (Math.Abs(Math.Abs(angle) - 90) < 1) { Text(label, new SKPoint(mid.X + _font * 0.4f, mid.Y + text.TextSize * 0.35f), SKTextAlign.Left); return; }
            var rad = angle * MathF.PI / 180;
            var over = new SKPoint(mid.X + MathF.Sin(rad) * _font * 0.35f, mid.Y - MathF.Cos(rad) * _font * 0.35f);
            Text(label, over, SKTextAlign.Center, angle);
        }
        // an extension line from a measured point to the dimension line, a little past it
        void Extension(SKPoint from, SKPoint to)
        {
            float dx = to.X - from.X, dy = to.Y - from.Y;
            var len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            var past = _font * 0.3f / len;
            var gap = Math.Min(_font * 0.2f, len * 0.3f) / len;
            Line(new SKPoint(from.X + dx * gap, from.Y + dy * gap), new SKPoint(to.X + dx * past, to.Y + dy * past), thin);
        }

        switch (m.Kind)
        {
            case SectionMarkKind.Horizontal when pts.Count >= 3:
            {
                var y = pts[2].Y;
                var a = new SKPoint(pts[0].X, y);
                var b = new SKPoint(pts[1].X, y);
                Extension(pts[0], a); Extension(pts[1], b);
                Dimension(a, b);
                break;
            }
            case SectionMarkKind.Vertical when pts.Count >= 3:
            {
                var x = pts[2].X;
                var a = new SKPoint(x, pts[0].Y);
                var b = new SKPoint(x, pts[1].Y);
                Extension(pts[0], a); Extension(pts[1], b);
                Dimension(a, b);
                break;
            }
            case SectionMarkKind.Aligned when pts.Count >= 3:
            {
                float dx = pts[1].X - pts[0].X, dy = pts[1].Y - pts[0].Y;
                var len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 1e-3) break;
                float nx = -dy / len, ny = dx / len;
                var off = (pts[2].X - pts[0].X) * nx + (pts[2].Y - pts[0].Y) * ny;
                var a = new SKPoint(pts[0].X + nx * off, pts[0].Y + ny * off);
                var b = new SKPoint(pts[1].X + nx * off, pts[1].Y + ny * off);
                Extension(pts[0], a); Extension(pts[1], b);
                Dimension(a, b);
                break;
            }
            case SectionMarkKind.Leader when pts.Count >= 3:
            {
                // a short dimension at the first point, its figure on a shelf at the third
                var a = pts[0];
                var b = new SKPoint(pts[0].X, pts[1].Y);
                Line(a, b, thin);
                End(a, b); End(b, a);
                var low = a.Y > b.Y ? a : b;
                var shelf = pts[2];
                Line(low, shelf, thin);
                text.TextAlign = SKTextAlign.Left;
                var w = text.MeasureText(label);
                var right = shelf.X >= low.X;
                var end = new SKPoint(shelf.X + (right ? w + _font * 0.3f : -(w + _font * 0.3f)), shelf.Y);
                Line(shelf, end, thin);
                Text(label, new SKPoint((shelf.X + end.X) / 2, shelf.Y - _font * 0.25f));
                break;
            }
            case SectionMarkKind.Angle when SectionMarking.Angle(spots, m.Style.AngleMode) is { } angle:
            {
                var vertex = P(new SectionSpot(angle.S, angle.Z));
                var r = (float)(angle.Radius * cam.Scale);
                SKPoint Along((double S, double Z) side, float k) => P(new SectionSpot(angle.S + side.S * k / cam.Scale, angle.Z + side.Z * k / cam.Scale));
                var e1 = Along(angle.Side1, r);
                var e2 = Along(angle.Side2, r);
                var a1 = MathF.Atan2(e1.Y - vertex.Y, e1.X - vertex.X) * 180 / MathF.PI;
                var a2 = MathF.Atan2(e2.Y - vertex.Y, e2.X - vertex.X) * 180 / MathF.PI;
                var sweep = a2 - a1;
                while (sweep > 180) sweep -= 360;
                while (sweep < -180) sweep += 360;
                using (var arc = new SKPath())
                {
                    arc.AddArc(new SKRect(vertex.X - r, vertex.Y - r, vertex.X + r, vertex.Y + r), a1, sweep);
                    c.DrawPath(arc, thin);
                }
                // the arc for picking: its chord in pieces
                for (int i = 0; i < 8; i++)
                {
                    float t0 = (a1 + sweep * i / 8) * MathF.PI / 180, t1 = (a1 + sweep * (i + 1) / 8) * MathF.PI / 180;
                    drawn.Lines.Add((new SKPoint(vertex.X + r * MathF.Cos(t0), vertex.Y + r * MathF.Sin(t0)), new SKPoint(vertex.X + r * MathF.Cos(t1), vertex.Y + r * MathF.Sin(t1))));
                }
                End(e1, Rotate(vertex, e1, sweep > 0 ? 1 : -1));
                End(e2, Rotate(vertex, e2, sweep > 0 ? -1 : 1));
                // the figure out on the middle of the arc
                var mid = (a1 + sweep / 2) * MathF.PI / 180;
                var at = new SKPoint(vertex.X + (r + _font * 0.9f) * MathF.Cos(mid), vertex.Y + (r + _font * 0.9f) * MathF.Sin(mid) + text.TextSize * 0.35f);
                Text(label, at);
                break;
            }
            case SectionMarkKind.Slope when pts.Count >= 3:
            {
                var angle = MathF.Atan2(pts[1].Y - pts[0].Y, pts[1].X - pts[0].X) * 180 / MathF.PI;
                if (angle > 90) angle -= 180;
                if (angle < -90) angle += 180;
                Text(label, pts[2], SKTextAlign.Center, angle);
                break;
            }
            case SectionMarkKind.Level:
            {
                var y = pts[0].Y;
                float left = box?.Left ?? 0, right = box?.Right ?? (float)cam.Width;
                Line(new SKPoint(left, y), new SKPoint(right, y), line);
                Text(label, new SKPoint(left - _font * 0.7f, y + text.TextSize * 0.35f), SKTextAlign.Right);
                break;
            }
            case SectionMarkKind.PointZ when pts.Count >= 2:
            {
                c.DrawCircle(pts[0], _font * 0.2f, fill);
                Line(pts[0], pts[1], thin);
                text.TextAlign = SKTextAlign.Left;
                var w = text.MeasureText(label);
                var right = pts[1].X >= pts[0].X;
                var end = new SKPoint(pts[1].X + (right ? w + _font * 0.3f : -(w + _font * 0.3f)), pts[1].Y);
                Line(pts[1], end, thin);
                Text(label, new SKPoint((pts[1].X + end.X) / 2, pts[1].Y - _font * 0.25f));
                break;
            }
            case SectionMarkKind.Polyline when pts.Count >= 2:
                for (int i = 1; i < pts.Count; i++) Line(pts[i - 1], pts[i], line);
                break;
            case SectionMarkKind.Text:
                Text(label, pts[0], SKTextAlign.Left);
                break;
        }

        if (selected)
            using (var grip = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = SelectionColor })
            using (var back = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(0, 0, 0, 160) })
                foreach (var p in pts)
                {
                    var g = new SKRect(p.X - 4, p.Y - 4, p.X + 4, p.Y + 4);
                    c.DrawRect(g, back);
                    c.DrawRect(g, grip);
                }
        return drawn;
    }

    /// <summary>A point a little along the arc from p about the vertex: which way an end of the arc points.</summary>
    private static SKPoint Rotate(SKPoint vertex, SKPoint p, int way)
    {
        var a = 0.15f * way;
        float dx = p.X - vertex.X, dy = p.Y - vertex.Y;
        return new SKPoint(vertex.X + dx * MathF.Cos(a) - dy * MathF.Sin(a), vertex.Y + dx * MathF.Sin(a) + dy * MathF.Cos(a));
    }
}
