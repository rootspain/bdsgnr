using System.Globalization;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using SkiaSharp;

namespace BlastDesigner.Rendering;

/// <summary>A recorded page, in points (1/72 inch).</summary>
public sealed record ReportPage(SKPicture Picture, float Width, float Height, bool HasPlan = false, PageSection? Section = null)
{
    public bool HasSection => Section != null;
}

/// <summary>The section drawn on a page: its line, null for one only picked, and what tells it from the others.</summary>
public sealed record PageSection(Guid? Line, string Caption);

/// <summary>
/// A report built once: the screen, the PDF file and the printer all take these pages, so they cannot differ. The pages
/// are recorded, so a document can be shown and printed on any thread and no longer needs the project.
/// </summary>
public sealed class ReportDocument : IDisposable
{
    private readonly object _gate = new();
    private readonly string _title, _author;
    private bool _disposed;

    internal ReportDocument(string title, string author, List<ReportPage> pages) => (_title, _author, Pages) = (title, author, pages);

    public IReadOnlyList<ReportPage> Pages { get; }
    public string Title => _title;

    /// <summary>Draws a page; a document already replaced and disposed draws nothing.</summary>
    public void Draw(SKCanvas canvas, int page)
    {
        lock (_gate)
            if (!_disposed) canvas.DrawPicture(Pages[page].Picture);
    }

    public void WritePdf(Stream output)
    {
        using var doc = SKDocument.CreatePdf(output, new SKDocumentPdfMetadata { Title = _title, Author = _author, Creator = "Blast Designer", Creation = DateTime.Now });
        for (int i = 0; i < Pages.Count; i++)
        {
            Draw(doc.BeginPage(Pages[i].Width, Pages[i].Height), i);
            doc.EndPage();
        }
        doc.Close();
    }

    /// <summary>A page as a picture on white at the given resolution, for what cannot take the PDF (the Windows printers).</summary>
    public SKBitmap Rasterize(int page, float dpi)
    {
        var scale = dpi / 72;
        var p = Pages[page];
        var bitmap = new SKBitmap(new SKImageInfo((int)Math.Ceiling(p.Width * scale), (int)Math.Ceiling(p.Height * scale), SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Scale(scale);
        Draw(canvas, page);
        return bitmap;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var p in Pages) p.Picture.Dispose();
        }
    }
}

/// <summary>Where the content of a sheet goes once the frame and the stamps of the design have their place, and how they are drawn.</summary>
public sealed class ReportLayout
{
    public const float Margin = 40;
    private const float PointsPerMm = 72 / 25.4f, RowHeight = 14;

    private readonly ReportDesign _design;
    private readonly float _w, _h;

    public ReportLayout(ReportDesign design, float width, float height)
    {
        (_design, _w, _h) = (design, width, height);
    }

    /// <summary>Distance of the frame from the edge of a sheet, 0 on a sheet without one.</summary>
    private float Inset(int page) => _design.Frame.IsOn(page + 1) ? (float)_design.Frame.MarginMm * PointsPerMm : 0;

    private bool IsDecorated => _design.Frame.Visible || _design.Stamps.Count > 0;

    private IEnumerable<ReportStamp> On(int page) => _design.Stamps.Where(s => s.Rows.Count > 0 && s.WidthMm() > 0 && s.IsOn(page + 1));

    private static float Height(ReportStamp s) => s.Rows.Count * RowHeight;

    /// <summary>Where the edge of the sheet is for the stamps: they lean on the frame, without one they keep off the edge.</summary>
    private float Edge(int page) => _design.Frame.IsOn(page + 1) ? Inset(page) : 14;

    public ReportFooter Footer => _design.Footer;

    /// <summary>The baseline of the date and the page number: at the foot of the sheet, under the frame and the stamps.</summary>
    public float FootLine(int page)
    {
        var bottom = _design.Frame.IsOn(page + 1) ? Inset(page) : On(page).Any(s => s.Place != StampPlace.TopRight) ? Edge(page) : Margin;
        return Math.Min(_h - bottom + 15, _h - 4);
    }

    /// <summary>
    /// The stamps of a page where they stand: the first of a place in its corner, each next one against the one before it,
    /// on its inner side or (<see cref="StampAttach.Vertical"/>) towards the middle of the sheet.
    /// </summary>
    private List<(ReportStamp Stamp, SKRect Rect)> Placed(int page)
    {
        var edge = Edge(page);
        var placed = new List<(ReportStamp, SKRect)>();
        foreach (var group in On(page).GroupBy(s => s.Place))
        {
            var top = group.Key == StampPlace.TopRight;
            var fromLeft = group.Key is StampPlace.BottomLeft or StampPlace.BottomWide;
            SKRect? last = null;
            foreach (var s in group)
            {
                var width = group.Key == StampPlace.BottomWide ? _w - 2 * edge : Math.Min((float)s.WidthMm() * PointsPerMm, _w - 2 * edge);
                var height = Height(s);
                float x, y;
                if (last is not { } before)
                {
                    x = fromLeft ? edge : _w - edge - width;
                    y = top ? edge : _h - edge - height;
                }
                else if (s.Attach == StampAttach.Horizontal && group.Key != StampPlace.BottomWide)
                {
                    x = fromLeft ? before.Right : before.Left - width;
                    y = top ? before.Top : before.Bottom - height;
                }
                else
                {
                    x = fromLeft ? before.Left : before.Right - width;
                    y = top ? before.Bottom : before.Top - height;
                }
                var rect = new SKRect(x, y, x + width, y + height);
                placed.Add((s, rect));
                last = rect;
            }
        }
        return placed;
    }

    /// <summary>Height taken by the stamps of one end of the sheet.</summary>
    private float Reserved(int page, bool top)
    {
        var edge = Edge(page);
        var rects = Placed(page).Where(p => (p.Stamp.Place == StampPlace.TopRight) == top).Select(p => p.Rect).ToList();
        if (rects.Count == 0) return 0;
        return top ? rects.Max(r => r.Bottom) - edge : _h - edge - rects.Min(r => r.Top);
    }

    /// <summary>The content of a page; the running head and foot stand just outside it, as without a design.</summary>
    public SKRect Area(int page)
    {
        var inset = Inset(page);
        var side = Math.Max(Margin, inset + 12);
        var end = Math.Max(Margin, inset + 24);
        var top = Reserved(page, true);
        var bottom = Reserved(page, false);
        return new SKRect(side, end + (top > 0 ? top + 20 : 0), _w - side, _h - end - (bottom > 0 ? bottom + 20 : 0));
    }

    /// <summary>Puts the frame and the stamps over the recorded pages: the number of pages is known only now.</summary>
    public List<ReportPage> Finish(List<ReportPage> pages, Dictionary<string, string> fields)
    {
        if (!IsDecorated) return pages;
        var result = new List<ReportPage>(pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            using var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(new SKRect(0, 0, pages[i].Width, pages[i].Height));
            canvas.DrawPicture(pages[i].Picture);
            Decorate(canvas, i, pages.Count, fields);
            result.Add(pages[i] with { Picture = recorder.EndRecording() });
            pages[i].Picture.Dispose();
        }
        return result;
    }

    private void Decorate(SKCanvas c, int page, int pages, Dictionary<string, string> fields)
    {
        if (_design.Frame.IsOn(page + 1))
        {
            var inset = Inset(page);
            using var line = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = (float)_design.Frame.LineWidthMm * PointsPerMm, IsAntialias = true };
            c.DrawRect(inset, inset, _w - 2 * inset, _h - 2 * inset, line);
        }
        foreach (var (stamp, rect) in Placed(page)) DrawStamp(c, stamp, rect, page, pages, fields);
    }

    private static void DrawStamp(SKCanvas c, ReportStamp s, SKRect r, int page, int pages, Dictionary<string, string> fields)
    {
        using var back = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White };
        using var line = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 0.7f, IsAntialias = true };
        using var label = new SKPaint { IsAntialias = true, TextSize = 7, Color = new SKColor(0xFF555555) };
        using var text = new SKPaint { IsAntialias = true, TextSize = 8.5f, Color = SKColors.Black };
        c.DrawRect(r, back);
        c.DrawRect(r, line);
        // the columns keep their proportion when the stamp is wider or narrower than they add up to
        bool labels = s.HasLabels(), texts = s.HasTexts();
        var split = !labels ? r.Left : !texts ? r.Right : r.Left + r.Width * (float)(s.LabelWidthMm / (s.LabelWidthMm + s.TextWidthMm));
        if (labels && texts) c.DrawLine(split, r.Top, split, r.Bottom, line);
        for (int i = 0; i < s.Rows.Count; i++)
        {
            var y = r.Top + i * RowHeight;
            // an empty cell has no line above or below it: it reads as one cell with its neighbours in the column
            if (i > 0 && labels && Filled(s.Rows[i - 1].Label) && Filled(s.Rows[i].Label)) c.DrawLine(r.Left, y, split, y, line);
            if (i > 0 && texts && Filled(s.Rows[i - 1].Text) && Filled(s.Rows[i].Text)) c.DrawLine(split, y, r.Right, y, line);
            if (labels) Cell(c, s.Rows[i].Label, s.Rows[i].LabelAlign, label, r.Left, split, Baseline(y, s.LabelVerticalAlign, label));
            if (texts) Cell(c, Expand(s.Rows[i].Text, page, pages, fields), s.Rows[i].TextAlign, text, split, r.Right, Baseline(y, s.TextVerticalAlign, text));
        }
    }

    private static bool Filled(string cell) => !string.IsNullOrWhiteSpace(cell);

    /// <summary>Where a text stands in a line of a stamp that starts at <paramref name="top"/>; the bottom leaves room for the tails of the letters.</summary>
    private static float Baseline(float top, StampVerticalAlign align, SKPaint paint)
    {
        var capitals = paint.TextSize * 0.72f;
        return align switch
        {
            StampVerticalAlign.Top => top + 1.5f + capitals,
            StampVerticalAlign.Bottom => top + RowHeight - 2.5f,
            _ => top + (RowHeight + capitals) / 2
        };
    }

    /// <summary>A text in a cell between two lines, cut to fit.</summary>
    private static void Cell(SKCanvas c, string value, StampAlign align, SKPaint paint, float left, float right, float baseline)
    {
        var fitted = Fit(value, paint, right - left - 6);
        var width = paint.MeasureText(fitted);
        var x = align switch
        {
            StampAlign.Center => (left + right - width) / 2,
            StampAlign.Right => right - 3 - width,
            _ => left + 3
        };
        c.DrawText(fitted, x, baseline, paint);
    }

    private static string Fit(string text, SKPaint paint, float width)
    {
        while (text.Length > 1 && paint.MeasureText(text) > width) text = text[..^2] + "…";
        return text;
    }

    /// <summary>The text of a stamp with its fields filled in.</summary>
    public static string Expand(string text, int page, int pages, Dictionary<string, string> fields)
    {
        return Fill(text, fields).Replace("{page}", (page + 1).ToString(CultureInfo.CurrentCulture)).Replace("{pages}", pages.ToString(CultureInfo.CurrentCulture));
    }

    /// <summary>A text with the fields of the blast filled in; those of the sheet stay for <see cref="Expand"/>.</summary>
    public static string Fill(string text, Dictionary<string, string> fields)
    {
        foreach (var (key, value) in fields) text = text.Replace(key, value);
        return text;
    }

    /// <summary>A text with its fields filled in, or nothing when all of them are empty: no dash left with no name after it.</summary>
    public static string FillOrNothing(string text, Dictionary<string, string> fields)
    {
        var used = fields.Where(f => text.Contains(f.Key)).ToList();
        return used.Count > 0 && used.All(f => string.IsNullOrWhiteSpace(f.Value)) ? "" : Fill(text, fields);
    }

    /// <summary>
    /// The fields of a stamp or footer: the header of the blast, and every calculated blast total of the calculation profile
    /// as <c>{calc:blast.powderFactor}</c> (with its unit).
    /// </summary>
    public static Dictionary<string, string> Fields(BlastProject project, string title, DateTime now)
    {
        var fields = HeaderFields(project, title, now);
        // один сеанс на все итоги: ячейки и МВЗ считаются один раз, а не на каждое поле
        var session = CalcService.Live(project) ?? CalcService.Session(project);
        foreach (var d in CalcService.Calculated(project, "blast"))
            fields["{calc:" + d.Id + "}"] = CalcService.Format(session, d.Id, project);
        return fields;
    }

    private static Dictionary<string, string> HeaderFields(BlastProject project, string title, DateTime now) => new()
    {
        ["{title}"] = title,
        ["{name}"] = project.Header.Name,
        ["{blast}"] = project.Header.Name,
        ["{number}"] = project.Header.BlastNumber,
        ["{mine}"] = project.Header.Mine,
        ["{bench}"] = project.Header.Bench,
        ["{location}"] = project.Header.Location,
        ["{client}"] = project.Header.Client,
        ["{author}"] = project.Header.Author,
        ["{shotdate}"] = project.Header.ShotDate?.ToString("d", CultureInfo.CurrentCulture) ?? "",
        ["{date}"] = now.ToString("d", CultureInfo.CurrentCulture)
    };
}
