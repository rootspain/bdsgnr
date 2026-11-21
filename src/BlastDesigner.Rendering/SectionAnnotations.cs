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
/// of a hole (two, the shortest and the longest, when the lengths differ), the distance between two neighbours along the
/// line, the subdrill of the first; no line of a dimension crosses another (<see cref="LayoutDimensions"/>). Fonts and arrows keep
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

    /// <summary>
    /// The frame of the section on the screen: from the top of the frame to the lowest toe, a little out of the outer holes
    /// (more on the right for a length dimension there); null with no hole in the section. The scene is drawn inside it only.
    /// </summary>
    public SKRect? Frame(Camera cam, BlastProject project)
    {
        var profile = SectionProfile.Build(project, _a, _b, _halfWidth, HiddenHoles);
        if (profile.Holes.Count == 0) return null;
        using var text = new SKPaint { IsAntialias = true, TextSize = _font, Typeface = SKTypeface.Default };
        var holes = profile.Holes.Select(h => (H: h, X: cam.ToScreen(h.Collar).X)).OrderBy(h => h.X).ToList();
        return Frame(cam, holes, text);
    }

    private SKRect Frame(Camera cam, List<(SectionHole H, float X)> holes, SKPaint text)
    {
        float Y(double z) => cam.ToScreen(cam.Target.WithZ(z)).Y;
        double highest = holes.Max(h => h.H.Collar.Z), lowest = holes.Min(h => h.H.Toe.Z);
        var top = SectionProfile.FrameTop(highest, _font * 1.7 / cam.Scale);
        var xs = holes.Select(h => h.X).ToList();
        var (lengths, _) = LayoutDimensions(xs, holes.Select(h => h.H.Hole.Length).ToList(), _font);
        // a little out of the outer holes: room for the subdrill, and for a length right of the last hole
        var pad = _font * 3;
        float left = (float)(holes[0].X - pad), right = (float)(holes[^1].X + pad);
        if (_show.Dimensions && lengths.Contains(xs.Count - 1))
            right = Math.Max(right, DimensionX(xs, xs.Count - 1, _font) + text.MeasureText(Metres(holes[^1].H.Hole.Length)) + _font);
        return new SKRect(left, Y(top), right, Y(lowest));
    }

    /// <summary>The holes a sheet leaves out: no number, level or dimension of theirs either.</summary>
    public ISet<Guid>? HiddenHoles { get; init; }

    /// <summary>The automatic dimensions are left out: the section line has them as marks of its own.</summary>
    public bool SkipDimensions { get; init; }

    /// <summary>
    /// Filled when drawn: each automatic dimension as a mark held on its holes (its points in the plane of the section)
    /// with the lines and the figure it took on the screen, for the window to pick it and make it a mark of its own.
    /// </summary>
    public List<AutoDimension>? Recorded { get; init; }

    public void Draw(SKCanvas canvas, Camera cam, BlastProject project)
    {
        Recorded?.Clear();
        if (!_show.Any && _title.Length == 0) return;
        var profile = SectionProfile.Build(project, _a, _b, _halfWidth, HiddenHoles);
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
        // where the dimensions go: the lengths (one, or the shortest and the longest), the spacing between two neighbours
        var xs = holes.Select(h => h.X).ToList();
        var (lengths, spacingAt) = LayoutDimensions(xs, holes.Select(h => h.H.Hole.Length).ToList(), _font);
        var frame = Frame(cam, holes, text);
        float left = frame.Left, right = frame.Right, topY = frame.Top, bottomY = frame.Bottom;

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

        if (_show.Dimensions && !SkipDimensions)
        {
            var toe = cam.ToScreen(first.Toe);
            var frame2 = new SectionFrame(_a, _b);
            SectionPoint Held(SectionHole h, SectionHolePart part)
            {
                var (s, z) = frame2.ToSection(part switch { SectionHolePart.Toe => h.Toe, SectionHolePart.Grade => h.Hole.GradePoint, _ => h.Collar });
                return new SectionPoint(s, z) { HoleId = h.Hole.Id, HolePart = part };
            }
            SectionPoint At(float x, float y) { var (s, z) = frame2.ToSection(cam.ToWorld(x, y)); return new SectionPoint(s, z); }
            void Record(SectionMarkKind kind, SectionMarkStyle style, SKRect figure, params (SKPoint A, SKPoint B)[] lines) => Recorded?.Add(new AutoDimension(new SectionMark { Kind = kind, Auto = true, Style = style }, lines.ToList(), figure));
            // the lengths, each right of its hole, its figure on the right
            foreach (var i in lengths)
            {
                var h = holes[i].H;
                var x = DimensionX(xs, i, _font);
                float cy = cam.ToScreen(h.Collar).Y, ty = cam.ToScreen(h.Toe).Y;
                VerticalDimension(canvas, line, text, x, cy, ty, Metres(h.Hole.Length), labelLeft: false);
                var w = text.MeasureText(Metres(h.Hole.Length));
                Record(SectionMarkKind.Vertical, new SectionMarkStyle(), new SKRect(x, (cy + ty) / 2 - _font, x + _font * 0.35f + w, (cy + ty) / 2 + _font * 0.5f), (new SKPoint(x, cy), new SKPoint(x, ty)));
                Recorded?[^1].Mark.Points.AddRange(new[] { Held(h, SectionHolePart.Collar), Held(h, SectionHolePart.Toe), At(x, (cy + ty) / 2) });
            }
            // the distance along the line between two neighbours no length stands between, half way down the bench
            if (spacingAt is int k)
            {
                var (p, q) = (holes[k], holes[k + 1]);
                var y = (cam.ToScreen(p.H.Collar).Y + gradeY) / 2;
                HorizontalDimension(canvas, line, text, p.X, q.X, y, Metres(Math.Abs(q.H.Along - p.H.Along)));
                var w = text.MeasureText(Metres(Math.Abs(q.H.Along - p.H.Along)));
                Record(SectionMarkKind.Horizontal, new SectionMarkStyle(), new SKRect((p.X + q.X) / 2 - w / 2, y - _font, (p.X + q.X) / 2 + w / 2, y + _font * 0.5f), (new SKPoint(p.X, y), new SKPoint(q.X, y)));
                Recorded?[^1].Mark.Points.AddRange(new[] { Held(p.H, SectionHolePart.Collar), Held(q.H, SectionHolePart.Collar), At((p.X + q.X) / 2, y) });
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
                var figure = Metres(first.GradeZ - first.Toe.Z);
                canvas.DrawText(figure, sx - _font * 0.35f, baseline, text);
                text.TextAlign = SKTextAlign.Left;
                var w = text.MeasureText(figure);
                Record(SectionMarkKind.Leader, new SectionMarkStyle { Ends = SectionMarkEnds.Tick }, new SKRect(sx - _font * 0.35f - w, baseline - _font, sx, baseline + _font * 0.3f), (new SKPoint(sx, gradeY), new SKPoint(sx, baseline)));
                Recorded?[^1].Mark.Points.AddRange(new[] { Held(first, SectionHolePart.Grade), Held(first, SectionHolePart.Toe), At(sx - _font * 0.5f - w, baseline) });
            }
        }
    }

    /// <summary>Lengths differing by more than this get two dimensions, the shortest and the longest, m.</summary>
    public const double LengthsDiffer = 0.05;

    /// <summary>Where the dimension of the length of hole <paramref name="i"/> stands: right of it, towards the next one (beyond the last).</summary>
    public static float DimensionX(IReadOnlyList<float> xs, int i, float font) =>
        i < xs.Count - 1 ? xs[i] + Math.Min((xs[i + 1] - xs[i]) * 0.3f, font * 3) : xs[i] + font;

    /// <summary>
    /// The dimensions of a section, by the holes left to right (<paramref name="xs"/>, px) and their lengths: which holes get
    /// a length (one when all are alike; the shortest and the longest when they differ) and between which two neighbours
    /// the spacing goes (the second and the third first, then the first two, then on), so that no line of a dimension
    /// crosses another. When the two lengths leave the spacing no room, one length only: the longest, else the shortest,
    /// else that of the last hole (right of it, clear of every spacing).
    /// </summary>
    public static (List<int> Lengths, int? SpacingAt) LayoutDimensions(IReadOnlyList<float> xs, IReadOnlyList<double> lengths, float font)
    {
        var n = xs.Count;
        if (n == 0) return (new List<int>(), null);
        int shortest = 0, longest = 0;
        for (int i = 1; i < n; i++)
        {
            if (lengths[i] < lengths[shortest]) shortest = i;
            if (lengths[i] > lengths[longest]) longest = i;
        }
        var sets = new List<List<int>>();
        if (lengths[longest] - lengths[shortest] > LengthsDiffer)
        {
            sets.Add(new List<int> { shortest, longest }.OrderBy(i => i).ToList());
            sets.Add(new List<int> { longest });
            sets.Add(new List<int> { shortest });
        }
        else sets.Add(new List<int> { 0 });
        sets.Add(new List<int> { n - 1 });
        if (n == 1) return (sets[0], null);
        var pairs = new List<int>();
        if (n > 2) pairs.Add(1);
        pairs.Add(0);
        for (int k = 2; k < n - 1; k++) pairs.Add(k);
        foreach (var set in sets)
            foreach (var k in pairs)
                if (!set.Any(i => DimensionX(xs, i, font) is var x && x > xs[k] - 1 && x < xs[k + 1] + 1))
                    return (set, k);
        return (sets[^1], null);
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

/// <summary>An automatic dimension of a section as last drawn: the mark it would be, its lines and its figure on the screen.</summary>
public sealed record AutoDimension(SectionMark Mark, List<(SKPoint A, SKPoint B)> Lines, SKRect Figure);
