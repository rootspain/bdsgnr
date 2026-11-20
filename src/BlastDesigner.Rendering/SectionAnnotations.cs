using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using SkiaSharp;
using System.Globalization;

namespace BlastDesigner.Rendering;

/// <summary>What the labels of a section show.</summary>
public sealed record SectionLabels(bool Numbers = true, bool Levels = true, bool Dimensions = true)
{
    public bool Any => Numbers || Levels || Dimensions;
}

/// <summary>
/// The labels of a section drawn over it, the same in the Section view window and on its sheet: the title, a frame from
/// above the collars (high enough for the numbers of the holes) to the toes, the numbers of the holes over their collars,
/// the levels on the left (the top of the frame, the collars, the grade, the toes), and one dimension of each: the length
/// of the first hole, the distance between the second and the third along the line, the subdrill of the first. Fonts and arrows keep
/// their size in pixels whatever the scale.
/// </summary>
public sealed class SectionAnnotations : IOverlay
{
    private readonly Vec3 _a, _b;
    private readonly double _halfWidth;
    private readonly string _title;
    private readonly SectionLabels _show;
    private readonly SKColor _ink;
    private readonly float _font;

    /// <param name="title">The title over the frame (Section along line A–A); empty for none.</param>
    /// <param name="font">Size of the numbers and the levels, px (pt on a sheet).</param>
    public SectionAnnotations(Vec3 a, Vec3 b, double halfWidth, string title, SectionLabels show, SKColor ink, float font = 12)
    {
        _a = a; _b = b; _halfWidth = halfWidth; _title = title; _show = show; _ink = ink; _font = font;
    }

    /// <summary>A length as the section writes it: one decimal, a point, no unit.</summary>
    public static string Metres(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    public void Draw(SKCanvas canvas, Camera cam, BlastProject project)
    {
        if (!_show.Any && _title.Length == 0) return;
        var profile = SectionProfile.Build(project, _a, _b, _halfWidth);
        using var text = new SKPaint { IsAntialias = true, TextSize = _font, Color = _ink, Typeface = SKTypeface.Default };
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = _ink };
        float Y(double z) => cam.ToScreen(cam.Target.WithZ(z)).Y;
        if (profile.Holes.Count == 0)
        {
            if (_title.Length > 0) Title(canvas, text, (float)cam.Width / 2, _font * 2);
            return;
        }
        // the holes left to right as the section shows them
        var holes = profile.Holes.Select(h => (H: h, X: cam.ToScreen(h.Collar).X)).OrderBy(h => h.X).ToList();
        var first = holes[0].H;
        double highest = holes.Max(h => h.H.Collar.Z), lowest = holes.Min(h => h.H.Toe.Z);
        // the band of the numbers over the collars, in metres at this scale
        var band = _font * 1.7 / cam.Scale;
        var top = SectionProfile.FrameTop(highest, band);
        // the frame a little out of the outer holes: room for the subdrill and the length beside the first
        var pad = _font * 3;
        // with two holes the length stands right of the second: more room there
        float left = (float)(holes[0].X - pad), right = (float)(holes[^1].X + (holes.Count == 2 ? pad * 1.6f : pad));
        float topY = Y(top), bottomY = Y(lowest);

        // the frame, the collars joined and the grade drawn double
        canvas.DrawRect(new SKRect(left, topY, right, bottomY), line);
        using (var path = new SKPath())
        {
            path.MoveTo(left, cam.ToScreen(first.Collar).Y);
            foreach (var (h, x) in holes) path.LineTo(x, cam.ToScreen(h.Collar).Y);
            path.LineTo(right, cam.ToScreen(holes[^1].H.Collar).Y);
            canvas.DrawPath(path, line);
        }
        var gradeY = Y(first.GradeZ);
        var subdrill = first.GradeZ - first.Toe.Z > 0.05;
        // the grade, one line across
        if (subdrill) canvas.DrawLine(left, gradeY, right, gradeY, line);

        if (_title.Length > 0) Title(canvas, text, (left + right) / 2, topY - _font * 0.9f);

        if (_show.Levels)
        {
            // top, collars, grade, toes; a label too close to one drawn above it is left out
            var levels = new List<double> { top };
            levels.AddRange(profile.CollarLevels());
            if (subdrill) levels.Add(first.GradeZ);
            levels.Add(lowest);
            text.TextAlign = SKTextAlign.Right;
            var drawn = new List<float>();
            foreach (var z in SectionProfile.Distinct(levels))
            {
                var y = Y(z);
                if (drawn.Any(d => Math.Abs(d - y) < _font * 0.95f)) continue;
                drawn.Add(y);
                canvas.DrawLine(left - _font * 0.5f, y, left, y, line);
                canvas.DrawText(Metres(z), left - _font * 0.7f, y + _font * 0.35f, text);
            }
            text.TextAlign = SKTextAlign.Left;
        }

        if (_show.Numbers)
        {
            // over each collar, in the band under the top of the frame; smaller when neighbours would overlap
            var widest = holes.Max(h => text.MeasureText(h.H.Hole.Label));
            var narrowest = holes.Count > 1 ? holes.Zip(holes.Skip(1), (p, q) => q.X - p.X).Min() : float.MaxValue;
            var size = widest > 0 ? Math.Clamp(_font * narrowest * 0.95f / widest, _font * 0.7f, _font) : _font;
            using var number = new SKPaint { IsAntialias = true, TextSize = size, Color = _ink, Typeface = SKTypeface.Default, TextAlign = SKTextAlign.Center };
            foreach (var (h, x) in holes) canvas.DrawText(h.Hole.Label, x, cam.ToScreen(h.Collar).Y - size * 0.4f, number);
        }

        if (_show.Dimensions)
        {
            var collar = cam.ToScreen(first.Collar);
            var toe = cam.ToScreen(first.Toe);
            // the length of the first hole: between it and the second, its figure on the right (with two holes, of the
            // second, right of it)
            if (holes.Count > 2)
            {
                var gap = Math.Min((holes[1].X - holes[0].X) * 0.3f, _font * 3);
                VerticalDimension(canvas, line, text, holes[0].X + gap, collar.Y, toe.Y, Metres(first.Hole.Length), labelLeft: false);
            }
            else if (holes.Count == 2)
            {
                // right of the second, so that the spacing between the two does not cross it
                var second = holes[1].H;
                VerticalDimension(canvas, line, text, holes[1].X + _font, cam.ToScreen(second.Collar).Y, cam.ToScreen(second.Toe).Y, Metres(second.Hole.Length), labelLeft: false);
            }
            else
                VerticalDimension(canvas, line, text, holes[0].X + _font, collar.Y, toe.Y, Metres(first.Hole.Length), labelLeft: false);
            // the distance along the line between the second and the third hole (the first two with two), half way
            // down the bench: clear of the length of the first
            if (holes.Count > 1)
            {
                var (p, q) = holes.Count > 2 ? (holes[1], holes[2]) : (holes[0], holes[1]);
                var y = (cam.ToScreen(p.H.Collar).Y + gradeY) / 2;
                HorizontalDimension(canvas, line, text, p.X, q.X, y, Metres(Math.Abs(q.H.Along - p.H.Along)));
            }
            // the subdrill of the first hole: from the grade to its toe, close to its left, the figure under it on a leader
            if (subdrill)
            {
                var sx = holes[0].X - _font * 0.45f;
                VerticalDimension(canvas, line, text, sx, gradeY, toe.Y, "", labelLeft: true);
                var baseline = toe.Y + _font * 1.25f;
                canvas.DrawLine(sx, toe.Y, sx, baseline, line);
                canvas.DrawLine(sx - _font * 0.3f, baseline, sx, baseline, line);
                text.TextAlign = SKTextAlign.Right;
                canvas.DrawText(Metres(first.GradeZ - first.Toe.Z), sx - _font * 0.35f, baseline, text);
                text.TextAlign = SKTextAlign.Left;
            }
        }
    }

    private void Title(SKCanvas canvas, SKPaint text, float x, float y)
    {
        using var title = new SKPaint { IsAntialias = true, TextSize = _font * 1.35f, Color = _ink, Typeface = SKTypeface.Default, TextAlign = SKTextAlign.Center, FakeBoldText = true };
        canvas.DrawText(_title, x, Math.Max(y, title.TextSize), title);
    }

    private void Arrow(SKCanvas canvas, SKPaint line, float x, float y, float dx, float dy)
    {
        var size = _font * 0.45f;
        canvas.DrawLine(x, y, x - dx * size - dy * size * 0.45f, y - dy * size + dx * size * 0.45f, line);
        canvas.DrawLine(x, y, x - dx * size + dy * size * 0.45f, y - dy * size - dx * size * 0.45f, line);
    }

    private void VerticalDimension(SKCanvas canvas, SKPaint line, SKPaint text, float x, float y1, float y2, string label, bool labelLeft)
    {
        if (Math.Abs(y2 - y1) < 2) return;
        canvas.DrawLine(x, y1, x, y2, line);
        var down = y2 > y1 ? 1 : -1;
        Arrow(canvas, line, x, y1, 0, -down);
        Arrow(canvas, line, x, y2, 0, down);
        if (label.Length == 0) return;
        text.TextAlign = labelLeft ? SKTextAlign.Right : SKTextAlign.Left;
        canvas.DrawText(label, x + (labelLeft ? -_font * 0.35f : _font * 0.35f), (y1 + y2) / 2 + _font * 0.35f, text);
        text.TextAlign = SKTextAlign.Left;
    }

    private void HorizontalDimension(SKCanvas canvas, SKPaint line, SKPaint text, float x1, float x2, float y, string label)
    {
        if (Math.Abs(x2 - x1) < 2) return;
        var width = text.MeasureText(label) + _font * 0.6f;
        var middle = (x1 + x2) / 2;
        // the figure in a gap of the line when it fits, over it otherwise
        if (Math.Abs(x2 - x1) > width + _font * 1.5f)
        {
            canvas.DrawLine(x1, y, middle - width / 2, y, line);
            canvas.DrawLine(middle + width / 2, y, x2, y, line);
            text.TextAlign = SKTextAlign.Center;
            canvas.DrawText(label, middle, y + _font * 0.35f, text);
        }
        else
        {
            canvas.DrawLine(x1, y, x2, y, line);
            text.TextAlign = SKTextAlign.Center;
            canvas.DrawText(label, middle, y - _font * 0.4f, text);
        }
        text.TextAlign = SKTextAlign.Left;
        Arrow(canvas, line, x1, y, -1, 0);
        Arrow(canvas, line, x2, y, 1, 0);
    }
}
