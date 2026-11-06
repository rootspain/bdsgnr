using System.Text;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.Rendering;
using SkiaSharp;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>A report built once as pages: the same on the screen, in the file and on the printer, with the frame and stamps of its design.</summary>
public class ReportTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 14, 7, 0);

    /// <summary>Enough holes for the hole table to run over several pages.</summary>
    private static BlastProject Blast(int holes = 150)
    {
        var p = BlastProject.CreateNew("report");
        p.Header.Name = "Blast 7";
        p.Header.Mine = "North";
        var list = new List<Hole>();
        for (int i = 0; i < holes; i++) list.Add(p.Add(new Hole { Label = (i + 1).ToString(), Collar = new Vec3(i % 15 * 5.0, i / 15 * 4.0, 300), Length = 11 }));
        p.Add(new InitiationPoint { Position = list[0].Collar, HoleId = list[0].Id });
        var connector = p.Products.First(x => x.Kind == ProductKind.SurfaceConnector);
        for (int i = 1; i < holes; i++) Timing.Connect(p, list[i - 1].Id, list[i].Id, connector);
        p.Add(new TextEntity { Text = "geometry", Position = new Vec3(30, 45, 300), Height = 4 });
        Timing.Solve(p);
        return p;
    }

    private static ReportStamp Stamp(StampPages pages = StampPages.All, StampPlace place = StampPlace.BottomRight) => new()
    {
        Pages = pages,
        Place = place,
        Rows = new() { new() { Label = "Blast", Text = "{blast}" }, new() { Label = "Sheet", Text = "{page} / {pages}" }, new() { Label = "Mine", Text = "{mine}" } }
    };

    private static int PdfPages(ReportDocument report)
    {
        using var stream = new MemoryStream();
        report.WritePdf(stream);
        var text = Encoding.Latin1.GetString(stream.ToArray());
        Assert.StartsWith("%PDF", text);
        return System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page\b").Count;
    }

    /// <summary>How much of a line of the sheet (in points) is not white.</summary>
    private static double Ink(SKBitmap page, int y, int from, int to)
    {
        var dark = 0;
        for (int x = from; x < to; x++) if (page.GetPixel(x, y).Red < 200) dark++;
        return (double)dark / (to - from);
    }

    [Fact]
    public void TheFileHasThePagesOfTheScreen()
    {
        using var report = Reports.Build(Blast(), new ReportOptions(), new ReportDesign(), Now);
        Assert.True(report.Pages.Count > 2, $"{report.Pages.Count} pages");
        Assert.Equal(report.Pages.Count, PdfPages(report));
    }

    [Fact]
    public void BuildingChangesNothingInTheProject()
    {
        var p = Blast();
        foreach (var h in p.Holes) h.FiringTimeMs = null;
        var before = ProjectJson.Serialize(p);
        using (Reports.Build(p, new ReportOptions(), new ReportDesign { Frame = { Visible = true } }, Now)) { }
        using (Reports.BuildPlan(p, new PlanOptions { Title = "Plan" }, new ReportDesign(), Now)) { }
        Assert.Equal(before, ProjectJson.Serialize(p));
        Assert.All(p.Holes, h => Assert.Null(h.FiringTimeMs));
    }

    [Fact]
    public void ThePagesAreTheSameWhateverTheThreadAndTheDesignThatIsOff()
    {
        var p = Blast(30);
        using var here = Reports.Build(p, new ReportOptions(), new ReportDesign(), Now);
        using var there = Task.Run(() => Reports.Build(p, new ReportOptions(), new ReportDesign { Frame = { Visible = false } }, Now)).Result;
        using var a = here.Rasterize(0, 72);
        using var b = there.Rasterize(0, 72);
        Assert.Equal(a.Bytes, b.Bytes);
    }

    [Fact]
    public void APageIsPrintedAtTheResolutionAsked()
    {
        using var report = Reports.BuildPlan(Blast(30), new PlanOptions { Landscape = true }, new ReportDesign(), Now);
        using var page = report.Rasterize(0, 144);
        Assert.Equal(842 * 2, page.Width);
        Assert.Equal(595 * 2, page.Height);
    }

    [Fact]
    public void TheFrameRunsRoundEveryPage()
    {
        using var report = Reports.Build(Blast(), new ReportOptions(), new ReportDesign { Frame = { Visible = true, MarginMm = 10, LineWidthMm = 1 } }, Now);
        var inset = (int)Math.Round(10 * 72 / 25.4);
        for (int i = 0; i < report.Pages.Count; i++)
        {
            using var page = report.Rasterize(i, 72);
            Assert.True(Ink(page, inset, inset, page.Width - inset) > 0.95, $"top of page {i + 1}");
            Assert.True(Ink(page, page.Height - inset, inset, page.Width - inset) > 0.95, $"bottom of page {i + 1}");
        }
    }

    [Theory]
    [InlineData("2", false, true, false)]
    [InlineData("1  3", true, false, true)]
    [InlineData("3 x 1", true, false, true)]
    [InlineData("", false, false, false)]
    public void TheFrameIsOnTheGivenPages(string numbers, bool first, bool second, bool third)
    {
        using var report = Reports.Build(Blast(), new ReportOptions(), new ReportDesign { Frame = { Visible = true, MarginMm = 10, LineWidthMm = 1, OnAllPages = false, Pages = numbers } }, Now);
        var inset = (int)Math.Round(10 * 72 / 25.4);
        bool Framed(int index)
        {
            using var page = report.Rasterize(index, 72);
            return Ink(page, inset, inset, page.Width - inset) > 0.95;
        }
        Assert.Equal(new[] { first, second, third }, new[] { Framed(0), Framed(1), Framed(2) });
    }

    [Fact]
    public void APageWithoutTheFrameIsLaidOutAsWithoutADesign()
    {
        using var plain = Reports.Build(Blast(), new ReportOptions(), new ReportDesign(), Now);
        using var framed = Reports.Build(Blast(), new ReportOptions(), new ReportDesign { Frame = { Visible = true, MarginMm = 15, OnAllPages = false, Pages = "99" } }, Now);
        Assert.Equal(plain.Pages.Count, framed.Pages.Count);
        using var a = plain.Rasterize(1, 72);
        using var b = framed.Rasterize(1, 72);
        Assert.Equal(a.Bytes, b.Bytes);
    }

    [Theory]
    [InlineData(StampPages.All, true)]
    [InlineData(StampPages.First, false)]
    public void AStampIsOnItsPages(StampPages pages, bool onTheSecond)
    {
        using var report = Reports.Build(Blast(), new ReportOptions(), new ReportDesign { Stamps = { Stamp(pages) } }, Now);
        // the top line of a stamp of three rows, 14 pt from the edge of the sheet
        double Line(int index)
        {
            using var page = report.Rasterize(index, 72);
            return Ink(page, page.Height - 14 - 3 * 14, page.Width - 300, page.Width - 20);
        }
        Assert.True(Line(0) > 0.9);
        Assert.Equal(onTheSecond, Line(1) > 0.9);
    }

    /// <summary>The vertical lines of a stamp of three rows on a plan sheet, in points from the right edge of the sheet.</summary>
    private static List<int> StampLines(ReportStamp stamp)
    {
        using var report = Reports.BuildPlan(Blast(5), new PlanOptions { Landscape = false }, new ReportDesign { Stamps = { stamp } }, Now);
        using var page = report.Rasterize(0, 72);
        var y = page.Height - 14 - 7;
        return Enumerable.Range(0, page.Width).Where(x => page.GetPixel(x, y).Red < 200 && page.GetPixel(x, y - 14).Red < 200 && page.GetPixel(x, y - 28).Red < 200).Select(x => page.Width - x).ToList();
    }

    [Fact]
    public void TheColumnsOfAStampHaveTheirWidths()
    {
        var mm = 72 / 25.4;
        var lines = StampLines(new ReportStamp { LabelWidthMm = 30, TextWidthMm = 50, Rows = Stamp().Rows });
        // from the right: the end of the stamp, the line between the columns, its start
        Assert.InRange(lines.Min(), 13, 15);
        Assert.InRange(lines.Where(x => x > 20).Min(), 14 + 50 * mm - 2, 14 + 50 * mm + 2);
        Assert.InRange(lines.Max(), 14 + 80 * mm - 2, 14 + 80 * mm + 2);
    }

    [Theory]
    [InlineData("2", false, true, false)]
    [InlineData("1 3", true, false, true)]
    [InlineData("", false, false, false)]
    public void AStampIsOnTheGivenPages(string numbers, bool first, bool second, bool third)
    {
        var stamp = Stamp(StampPages.Specified);
        stamp.PageNumbers = numbers;
        using var report = Reports.Build(Blast(), new ReportOptions(), new ReportDesign { Stamps = { stamp } }, Now);
        bool Stamped(int index)
        {
            using var page = report.Rasterize(index, 72);
            return Ink(page, page.Height - 14 - 3 * 14, page.Width - 300, page.Width - 20) > 0.9;
        }
        Assert.Equal(new[] { first, second, third }, new[] { Stamped(0), Stamped(1), Stamped(2) });
    }

    private static ReportStamp Narrow(StampAttach attach) => new() { Attach = attach, LabelWidthMm = 20, TextWidthMm = 40, Rows = Stamp().Rows };

    [Fact]
    public void AStampAdjoinsTheOneBeforeItWithNoGap()
    {
        var width = (int)(60 * 72 / 25.4);
        using var side = Reports.BuildPlan(Blast(5), new PlanOptions { Landscape = false }, new ReportDesign { Stamps = { Narrow(StampAttach.Vertical), Narrow(StampAttach.Horizontal) } }, Now);
        using var sheet = side.Rasterize(0, 72);
        // side by side: one line along the top of both, nothing over them
        var top = sheet.Height - 14 - 3 * 14;
        Assert.True(Ink(sheet, top, sheet.Width - 14 - 2 * width + 3, sheet.Width - 17) > 0.95);
        Assert.True(Ink(sheet, top - 14, sheet.Width - 14 - width, sheet.Width - 17) < 0.5);

        using var over = Reports.BuildPlan(Blast(5), new PlanOptions { Landscape = false }, new ReportDesign { Stamps = { Narrow(StampAttach.Vertical), Narrow(StampAttach.Vertical) } }, Now);
        using var stacked = over.Rasterize(0, 72);
        // one over the other: the top of the first is the bottom of the second, and the second ends where the first does
        Assert.True(Ink(stacked, top, stacked.Width - 14 - width + 3, stacked.Width - 17) > 0.95);
        Assert.True(Ink(stacked, top - 3 * 14, stacked.Width - 14 - width + 3, stacked.Width - 17) > 0.95);
        Assert.True(Ink(stacked, top, stacked.Width - 14 - 2 * width, stacked.Width - 16 - width) < 0.2);
    }

    [Fact]
    public void AStampAtTheTopAdjoinsDownwardsAndOnTheLeftOutwards()
    {
        var first = Narrow(StampAttach.Vertical);
        first.Place = StampPlace.BottomLeft;
        var second = Narrow(StampAttach.Horizontal);
        second.Place = StampPlace.BottomLeft;
        var width = (int)(60 * 72 / 25.4);
        using var report = Reports.BuildPlan(Blast(5), new PlanOptions { Landscape = false }, new ReportDesign { Stamps = { first, second } }, Now);
        using var page = report.Rasterize(0, 72);
        Assert.True(Ink(page, page.Height - 14 - 3 * 14, 17, 14 + 2 * width - 3) > 0.95);

        var upper = Narrow(StampAttach.Vertical);
        upper.Place = StampPlace.TopRight;
        var lower = Narrow(StampAttach.Vertical);
        lower.Place = StampPlace.TopRight;
        using var top = Reports.BuildPlan(Blast(5), new PlanOptions { Landscape = false }, new ReportDesign { Stamps = { upper, lower } }, Now);
        using var sheet = top.Rasterize(0, 72);
        Assert.True(Ink(sheet, 14 + 3 * 14, sheet.Width - 14 - width + 3, sheet.Width - 17) > 0.95);
        Assert.True(Ink(sheet, 14 + 6 * 14, sheet.Width - 14 - width + 3, sheet.Width - 17) > 0.95);
    }

    [Fact]
    public void ACellHasItsOwnAlignment()
    {
        var mm = 72 / 25.4;
        double Middle(StampAlign align)
        {
            var stamp = new ReportStamp { LabelWidthMm = 0, TextWidthMm = 60, Rows = { new() { Text = "x", TextAlign = align } } };
            using var report = Reports.BuildPlan(Blast(5), new PlanOptions { Landscape = false }, new ReportDesign { Stamps = { stamp } }, Now);
            using var page = report.Rasterize(0, 72);
            int left = page.Width - 14 - (int)(60 * mm) + 2, right = page.Width - 16;
            var ink = new List<int>();
            for (int y = page.Height - 26; y < page.Height - 16; y++)
                for (int x = left; x < right; x++)
                    if (page.GetPixel(x, y).Red < 200) ink.Add(x - left);
            return ink.Average() / (right - left);
        }
        Assert.InRange(Middle(StampAlign.Left), 0, 0.1);
        Assert.InRange(Middle(StampAlign.Center), 0.45, 0.55);
        Assert.InRange(Middle(StampAlign.Right), 0.9, 1);
    }

    [Fact]
    public void AColumnHasItsOwnVerticalAlignment()
    {
        var mm = 72 / 25.4;
        // how far down the ink of each column is, in a stamp of one line
        (double Label, double Text) Height(StampVerticalAlign label, StampVerticalAlign text)
        {
            var stamp = new ReportStamp { LabelWidthMm = 30, TextWidthMm = 30, LabelVerticalAlign = label, TextVerticalAlign = text, Rows = { new() { Label = "HHH", Text = "HHH" } } };
            using var report = Reports.BuildPlan(Blast(5), new PlanOptions { Landscape = false }, new ReportDesign { Stamps = { stamp } }, Now);
            using var page = report.Rasterize(0, 288);
            var scale = 4;
            int right = page.Width - 14 * scale, width = (int)(30 * mm * scale), bottom = page.Height - 14 * scale, top = bottom - 14 * scale;
            double Ink(int from, int to)
            {
                var rows = new List<int>();
                for (int y = top + 4; y < bottom - 4; y++)
                    for (int x = from + 8; x < to - 8; x++)
                        if (page.GetPixel(x, y).Red < 200) rows.Add(y - top);
                return rows.Average();
            }
            return (Ink(right - 2 * width, right - width), Ink(right - width, right));
        }
        var top = Height(StampVerticalAlign.Top, StampVerticalAlign.Top);
        var middle = Height(StampVerticalAlign.Middle, StampVerticalAlign.Middle);
        var bottom = Height(StampVerticalAlign.Bottom, StampVerticalAlign.Bottom);
        Assert.True(top.Label < middle.Label && middle.Label < bottom.Label);
        Assert.True(top.Text < middle.Text && middle.Text < bottom.Text);
        // the columns do not follow each other
        var apart = Height(StampVerticalAlign.Top, StampVerticalAlign.Bottom);
        Assert.Equal(top.Label, apart.Label);
        Assert.Equal(bottom.Text, apart.Text);
    }

    [Fact]
    public void TheDateAndThePageNumberAreAtTheFootOfTheSheet()
    {
        // the top of the ink of the date, from the bottom of the sheet
        int Foot(ReportDesign design)
        {
            using var report = Reports.Build(Blast(5), new ReportOptions { Sections = ReportSections.None }, design, Now);
            using var page = report.Rasterize(0, 72);
            for (int y = page.Height / 2; y < page.Height; y++)
                for (int x = 45; x < 120; x++)
                    if (page.GetPixel(x, y).Red < 200) return page.Height - y;
            return 0;
        }
        Assert.InRange(Foot(new ReportDesign()), 26, 40);
        // under the stamp, not over it
        var stamp = new ReportStamp { Rows = { new() { Label = "a", Text = "b" }, new() { Label = "c", Text = "d" } } };
        Assert.InRange(Foot(new ReportDesign { Stamps = { stamp } }), 1, 13);
        // a stamp at the top of the sheet changes nothing
        var high = new ReportStamp { Place = StampPlace.TopRight, Rows = { new() { Label = "a", Text = "b" } } };
        Assert.Equal(Foot(new ReportDesign()), Foot(new ReportDesign { Stamps = { high } }));
        // under the frame: its line is the first ink on the way down when the date is below it
        var frame = new ReportDesign { Frame = { Visible = true, MarginMm = 10 } };
        using var report = Reports.Build(Blast(5), new ReportOptions { Sections = ReportSections.None }, frame, Now);
        using var page = report.Rasterize(0, 72);
        var line = page.Height - (int)(10 * 72 / 25.4);
        var ink = Enumerable.Range(line + 3, page.Height - line - 3).Any(y => Enumerable.Range(45, 75).Any(x => page.GetPixel(x, y).Red < 200));
        Assert.True(ink);
        Assert.InRange(page.Height - Foot(frame), line - 2, line + 2);
    }

    [Fact]
    public void TheFooterShowsWhatTheDesignAsks()
    {
        // the ink at the foot of a page, left for the date and right for the number
        (int Date, int Number) Foot(ReportFooter footer, int index = 0)
        {
            using var report = Reports.Build(Blast(150), new ReportOptions(), new ReportDesign { Footer = footer }, Now);
            using var page = report.Rasterize(index, 72);
            int Ink(int from, int to) => Enumerable.Range(page.Height - 34, 18).Sum(y => Enumerable.Range(from, to - from).Count(x => page.GetPixel(x, y).Red < 200));
            return (Ink(30, 200), Ink(page.Width - 90, page.Width - 20));
        }
        var all = Foot(new ReportFooter());
        Assert.True(all.Date > 0 && all.Number > 0);
        Assert.Equal(0, Foot(new ReportFooter { ShowDate = false }).Date);
        Assert.Equal(0, Foot(new ReportFooter { ShowPageNumber = false }).Number);
        // the date alone is shorter than with its time
        Assert.InRange(Foot(new ReportFooter { DateWithTime = false }).Date, 1, all.Date - 1);
        // the number on some pages only
        Assert.True(Foot(new ReportFooter { NumberedPages = StampPages.First }).Number > 0);
        Assert.Equal(0, Foot(new ReportFooter { NumberedPages = StampPages.First }, 1).Number);
        var listed = new ReportFooter { NumberedPages = StampPages.Specified, PageNumbers = "2" };
        Assert.Equal(0, Foot(listed).Number);
        Assert.True(Foot(listed, 1).Number > 0);
    }

    [Fact]
    public void TheFooterGoesWithTheDesignFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + BlastDesigner.IO.ReportDesignFile.Extension);
        try
        {
            BlastDesigner.IO.ReportDesignFile.Save(path, new ReportDesign { Footer = new ReportFooter { ShowDate = false, DateWithTime = false, ShowPageNumber = false, NumberedPages = StampPages.Specified, PageNumbers = "2 3" } });
            var footer = BlastDesigner.IO.ReportDesignFile.Load(path).Footer;
            Assert.False(footer.ShowDate);
            Assert.False(footer.DateWithTime);
            Assert.False(footer.ShowPageNumber);
            Assert.Equal(StampPages.Specified, footer.NumberedPages);
            Assert.Equal("2 3", footer.PageNumbers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void APageKnowsItHasThePlan()
    {
        var p = Blast(150);
        using var report = Reports.Build(p, new ReportOptions(), new ReportDesign(), Now);
        Assert.True(report.Pages.Count > 1);
        Assert.Single(report.Pages, page => page.HasPlan);
        // a page of its own, and with the frame and the stamps drawn over it
        using var alone = Reports.Build(p, new ReportOptions { NewPage = ReportSections.Plan }, new ReportDesign { Frame = { Visible = true } }, Now);
        Assert.False(alone.Pages[0].HasPlan);
        Assert.True(alone.Pages[1].HasPlan);
        using var none = Reports.Build(p, new ReportOptions { Sections = ReportSections.HoleTable }, new ReportDesign(), Now);
        Assert.DoesNotContain(none.Pages, page => page.HasPlan);
        using var sheet = Reports.BuildPlan(p, new PlanOptions { Title = "Plan" }, new ReportDesign(), Now);
        Assert.True(sheet.Pages[0].HasPlan);
        using var arrows = Reports.BuildFirstMovement(p, new FirstMovementSheet { Title = "Arrows", Arrows = new FirstMovementOverlay() }, new ReportDesign(), Now);
        Assert.True(arrows.Pages[0].HasPlan);
    }

    /// <summary>A blast with section lines across its holes.</summary>
    private static BlastProject WithSections(int lines)
    {
        var p = Blast(30);
        for (int i = 0; i < lines; i++)
            p.Add(new PolylineEntity { Kind = LineKind.CrossSection, Points = { new Vec3(-5, i * 4.0, 300), new Vec3(75, i * 4.0, 300) } });
        return p;
    }

    [Fact]
    public void ASectionLineIsThickerThanAPolyline()
    {
        var thick = WithSections(1);
        var thin = WithSections(1);
        thin.Entities.OfType<PolylineEntity>().Single().Kind = LineKind.General;
        int Ink(byte[] pixels) => pixels.Count(b => b < 128);
        Assert.True(Ink(PlanPixels(thick, new ReportDesign())) > Ink(PlanPixels(thin, new ReportDesign())));
    }

    [Fact]
    public void EachSectionLineHasItsSheet()
    {
        var sheet = new CrossSectionSheet { Title = "Section" };
        using var none = Reports.BuildCrossSections(Blast(30), sheet, new ReportDesign(), Now);
        Assert.Single(none.Pages);
        using var two = Reports.BuildCrossSections(WithSections(2), sheet, new ReportDesign(), Now);
        Assert.Equal(2, two.Pages.Count);
        Assert.DoesNotContain(two.Pages, page => page.HasPlan);
        // holes on the sheet, each along its own line
        using var a = two.Rasterize(0, 72);
        using var b = two.Rasterize(1, 72);
        Assert.NotEqual(a.Bytes, b.Bytes);
        using var empty = none.Rasterize(0, 72);
        Assert.NotEqual(empty.Bytes, a.Bytes);
        // a line that was only picked takes the place of those of the blast
        using var picked = Reports.BuildCrossSections(WithSections(2), new CrossSectionSheet { Title = "Section", Line = (new Vec3(-5, 0, 300), new Vec3(75, 0, 300)) }, new ReportDesign(), Now);
        Assert.Single(picked.Pages);
    }

    [Fact]
    public void TheSectionsComeAfterThePlanInTheReport()
    {
        var plan = ReportSections.Plan | ReportSections.CrossSection;
        using var with = Reports.Build(WithSections(3), new ReportOptions { Sections = plan }, new ReportDesign(), Now);
        using var without = Reports.Build(WithSections(3), new ReportOptions { Sections = ReportSections.Plan }, new ReportDesign(), Now);
        Assert.True(with.Pages.Count > without.Pages.Count);
        Assert.True(with.Pages[0].HasPlan);
        // nothing to show, nothing added
        using var none = Reports.Build(Blast(30), new ReportOptions { Sections = plan }, new ReportDesign(), Now);
        Assert.Equal(without.Pages.Count, none.Pages.Count);
        // the order of the switches is the order of the report
        var order = Enum.GetValues<ReportSections>().ToList();
        Assert.Equal(order.IndexOf(ReportSections.Plan) + 1, order.IndexOf(ReportSections.CrossSection));
        Assert.Equal(ReportSections.All, Enum.GetValues<ReportSections>().Where(s => s != ReportSections.All).Aggregate((x, y) => x | y));
    }

    [Fact]
    public void AColumnOfAStampOfWidthZeroIsLeftOut()
    {
        var mm = 72 / 25.4;
        ReportStamp Columns(double label, double text) => new()
        {
            LabelWidthMm = label,
            TextWidthMm = text,
            // words in the first line only: what goes through all three is a line of the stamp
            Rows = { new() { Label = "caption", Text = "text" }, new(), new() }
        };
        // no captions: only the texts, and no line between the columns
        var texts = StampLines(Columns(0, 50));
        Assert.InRange(texts.Max(), 14 + 50 * mm - 2, 14 + 50 * mm + 2);
        Assert.Equal(2, Groups(texts));
        // no texts: only the captions
        var labels = StampLines(Columns(30, 0));
        Assert.InRange(labels.Max(), 14 + 30 * mm - 2, 14 + 30 * mm + 2);
        Assert.Equal(2, Groups(labels));
        // a column with nothing written in it stays: only its width decides
        var blank = Columns(30, 50);
        foreach (var row in blank.Rows) row.Label = "";
        Assert.InRange(StampLines(blank).Max(), 14 + 80 * mm - 2, 14 + 80 * mm + 2);
        Assert.Equal(3, Groups(StampLines(blank)));
    }

    /// <summary>How many separate lines a list of positions makes.</summary>
    private static int Groups(List<int> positions)
    {
        var sorted = positions.OrderBy(x => x).ToList();
        return 1 + sorted.Zip(sorted.Skip(1)).Count(p => p.Second - p.First > 1);
    }

    [Fact]
    public void AnEmptyCellHasNoLinesAboveAndBelowIt()
    {
        var stamp = new ReportStamp
        {
            LabelWidthMm = 40,
            TextWidthMm = 40,
            Rows = { new() { Label = "a", Text = "1" }, new() { Label = "", Text = "2" }, new() { Label = "c", Text = "3" }, new() { Label = "d", Text = "" }, new() { Label = "e", Text = "5" } }
        };
        using var report = Reports.BuildPlan(Blast(5), new PlanOptions { Landscape = false }, new ReportDesign { Stamps = { stamp } }, Now);
        using var page = report.Rasterize(0, 72);
        var width = (int)(80 * 72 / 25.4);
        int left = page.Width - 14 - width, split = left + width / 2, right = page.Width - 14;
        // the line under row i (from 0), in each column
        double Under(int row, bool text) => Ink(page, page.Height - 14 - (5 - row - 1) * 14, text ? split + 3 : left + 3, text ? right - 3 : split - 3);
        // the empty caption of row 1 joins rows 0 to 2
        Assert.True(Under(0, false) < 0.1);
        Assert.True(Under(1, false) < 0.1);
        Assert.True(Under(2, false) > 0.9);
        Assert.True(Under(3, false) > 0.9);
        // the empty text of row 3 joins rows 2 to 4
        Assert.True(Under(0, true) > 0.9);
        Assert.True(Under(1, true) > 0.9);
        Assert.True(Under(2, true) < 0.1);
        Assert.True(Under(3, true) < 0.1);
        // the outline of the stamp stays
        Assert.True(Ink(page, page.Height - 14 - 5 * 14, left + 3, right - 3) > 0.9);
        Assert.True(Ink(page, page.Height - 14, left + 3, right - 3) > 0.9);
    }

    /// <summary>A blast on a surface of many triangles, its edges on or off.</summary>
    private static BlastProject OnSurface(bool wireframe)
    {
        var p = Blast(5);
        var s = new SurfaceEntity { Name = "Survey", Kind = SurfaceKind.Topography, ShowWireframe = wireframe };
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 12; x++)
                s.Vertices.Add(new Vec3(x * 3.0 - 5, y * 3.0 - 5, 300 + (x + y) % 3));
        for (int y = 0; y < 11; y++)
            for (int x = 0; x < 11; x++)
            {
                int a = y * 12 + x, b = a + 1, c = a + 12, d = c + 1;
                s.Triangles.AddRange(new[] { a, b, c, b, d, c });
            }
        p.Add(s);
        return p;
    }

    [Fact]
    public void TheEdgesOfASurfaceAreNotOnTheSheets()
    {
        Assert.Equal(PlanPixels(OnSurface(false), new ReportDesign()), PlanPixels(OnSurface(true), new ReportDesign()));
        byte[] Report(BlastProject p)
        {
            using var report = Reports.Build(p, new ReportOptions { Sections = ReportSections.Plan }, new ReportDesign(), Now);
            using var page = report.Rasterize(0, 72);
            return page.Bytes;
        }
        Assert.Equal(Report(OnSurface(false)), Report(OnSurface(true)));
        byte[] Arrows(BlastProject p)
        {
            using var report = Reports.BuildFirstMovement(p, new FirstMovementSheet { Title = "Arrows", HideGeometry = false, Arrows = new FirstMovementOverlay() }, new ReportDesign(), Now);
            using var page = report.Rasterize(0, 72);
            return page.Bytes;
        }
        Assert.Equal(Arrows(OnSurface(false)), Arrows(OnSurface(true)));
    }

    [Fact]
    public void AStampWithNothingInItIsNotDrawn()
    {
        var plain = PlanPixels(Blast(5), new ReportDesign());
        Assert.Equal(plain, PlanPixels(Blast(5), new ReportDesign { Stamps = { new ReportStamp() } }));
        var noWidth = new ReportStamp { LabelWidthMm = 0, TextWidthMm = 0, Rows = { new() { Label = "a", Text = "b" } } };
        Assert.Equal(plain, PlanPixels(Blast(5), new ReportDesign { Stamps = { noWidth } }));
    }

    [Fact]
    public void TheContentMakesRoomForTheStamps()
    {
        var plain = Reports.Build(Blast(), new ReportOptions(), new ReportDesign(), Now);
        var stamped = Reports.Build(Blast(), new ReportOptions(), new ReportDesign { Stamps = { Stamp(), Stamp(), Stamp(place: StampPlace.TopRight) } }, Now);
        Assert.True(stamped.Pages.Count > plain.Pages.Count, $"{plain.Pages.Count} and {stamped.Pages.Count} pages");
        plain.Dispose();
        stamped.Dispose();
    }

    [Fact]
    public void TheFieldsOfAStampAreFilledIn()
    {
        var fields = ReportLayout.Fields(Blast(5), "Plan", Now);
        Assert.Equal("Blast 7 / North / Plan", ReportLayout.Expand("{blast} / {mine} / {title}", 0, 1, fields));
        Assert.Equal("2 / 5", ReportLayout.Expand("{page} / {pages}", 1, 5, fields));
        // every field offered in the window is one the report knows
        foreach (var field in ReportDesign.Fields) Assert.DoesNotContain("{", ReportLayout.Expand(field, 0, 1, fields));
    }

    private static byte[] FirstPage(ReportOptions options, string name = "Blast 7")
    {
        var p = Blast(5);
        p.Header.Name = name;
        using var report = Reports.Build(p, options, new ReportDesign(), Now);
        using var page = report.Rasterize(0, 72);
        return page.Bytes;
    }

    [Fact]
    public void WhatFollowsTheTitleIsATextOfTheUser()
    {
        var fields = ReportLayout.Fields(Blast(5), "Report", Now);
        Assert.Equal("- Blast 7 (North)", ReportLayout.Fill("- {name} ({mine})", fields));
        // the head of the page is only the title and that text
        Assert.Equal(FirstPage(new ReportOptions { Title = "Report", TitleAddition = "— Blast 7" }), FirstPage(new ReportOptions { Title = "Report" }));
        Assert.Equal(FirstPage(new ReportOptions { Title = "Report Blast 7", TitleAddition = "" }), FirstPage(new ReportOptions { Title = "Report", TitleAddition = "{name}" }));
        Assert.NotEqual(FirstPage(new ReportOptions { Title = "Report", TitleAddition = "" }), FirstPage(new ReportOptions { Title = "Report" }));
    }

    [Fact]
    public void NothingFollowsTheTitleWhenThereIsNothingToShow()
    {
        var nothing = FirstPage(new ReportOptions { Title = "Report", TitleAddition = "" });
        Assert.Equal(nothing, FirstPage(new ReportOptions { Title = "Report", TitleAddition = "   " }));
        // an empty name leaves no dash behind
        Assert.Equal(FirstPage(new ReportOptions { Title = "Report", TitleAddition = "" }, ""), FirstPage(new ReportOptions { Title = "Report" }, ""));
        var fields = ReportLayout.Fields(new BlastProject { Header = { Name = "", Mine = "North" } }, "Report", Now);
        Assert.Equal("", ReportLayout.FillOrNothing("— {name}", fields));
        Assert.Equal("— North", ReportLayout.FillOrNothing("— {name}{mine}", fields));
        // a text with no fields is the user's own
        Assert.Equal("draft", ReportLayout.FillOrNothing("draft", fields));
    }

    private static int Pages(ReportOptions options, BlastProject? project = null)
    {
        using var report = Reports.Build(project ?? Blast(5), options, new ReportDesign(), Now);
        return report.Pages.Count;
    }

    [Fact]
    public void ASectionStartsOnANewPageWhenItSaysSo()
    {
        var two = ReportSections.Header | ReportSections.Quantities;
        Assert.Equal(2, Pages(new ReportOptions { Sections = two, NewPage = ReportSections.All }));
        Assert.Equal(1, Pages(new ReportOptions { Sections = two }));
        Assert.Equal(1, Pages(new ReportOptions { Sections = two, NewPage = ReportSections.Header }));
        Assert.Equal(2, Pages(new ReportOptions { Sections = two, NewPage = ReportSections.Quantities }));
    }

    [Fact]
    public void NoSectionLeavesABlankPage()
    {
        // the first section is on the first page, a section with nothing to show opens none
        Assert.Equal(1, Pages(new ReportOptions { Sections = ReportSections.Header, NewPage = ReportSections.All }));
        Assert.Equal(1, Pages(new ReportOptions { Sections = ReportSections.Header | ReportSections.Vibration, NewPage = ReportSections.All }));
        Assert.Equal(1, Pages(new ReportOptions { Sections = ReportSections.Vibration, NewPage = ReportSections.All }));
    }

    [Fact]
    public void TheChoicesOfTheReportGoToTheDesignFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + BlastDesigner.IO.ReportDesignFile.Extension);
        try
        {
            BlastDesigner.IO.ReportDesignFile.Save(path, new ReportDesign
            {
                Report = { Title = "T", TitleAddition = "- {mine}", Sections = ReportSections.Plan, NewPage = ReportSections.Timing, Landscape = true, PlanTimingLabels = TimingLabelMode.SurfaceTime },
                Plan = { Title = "P {name}", Scale = 500, Landscape = false }
            });
            var back = BlastDesigner.IO.ReportDesignFile.Load(path);
            Assert.Equal(("T", "- {mine}", ReportSections.Plan, ReportSections.Timing, true, TimingLabelMode.SurfaceTime),
                (back.Report.Title, back.Report.TitleAddition, back.Report.Sections, back.Report.NewPage, back.Report.Landscape, back.Report.PlanTimingLabels));
            Assert.Equal(("P {name}", 500d, false), (back.Plan.Title, back.Plan.Scale!.Value, back.Plan.Landscape));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheTitleOfAPlanTakesTheNameOfTheBlast()
    {
        using var report = Reports.BuildPlan(Blast(5), new PlanOptions(), new ReportDesign(), Now);
        Assert.Equal("Blast 7", report.Title);
    }

    [Fact]
    public void TheFirstMovementSheetFollowsTheWindow()
    {
        var p = Blast(60);
        foreach (var h in p.Holes) h.ElectronicTimeMs = h.Collar.X * 3 + h.Collar.Y * 2;
        Timing.Solve(p);
        byte[] Sheet(bool hide)
        {
            using var report = Reports.BuildFirstMovement(p, new FirstMovementSheet { Title = "Arrows", HideGeometry = hide, Arrows = new FirstMovementOverlay() }, new ReportDesign(), Now);
            Assert.Single(report.Pages);
            using var page = report.Rasterize(0, 72);
            return page.Bytes;
        }
        Assert.NotEqual(Sheet(true), Sheet(false));
    }

    private static byte[] PlanPixels(BlastProject p, ReportDesign design)
    {
        using var plan = Reports.BuildPlan(p, new PlanOptions { Title = "Plan" }, design, Now);
        using var page = plan.Rasterize(0, 72);
        return page.Bytes;
    }

    [Fact]
    public void AKindLeftOutIsNotDrawnAndNotCountedInTheFit()
    {
        var p = Blast(30);
        var before = ProjectJson.Serialize(p);
        var expected = Blast(30);
        expected.Entities.RemoveAll(e => e is TextEntity);
        Assert.Equal(PlanPixels(expected, new ReportDesign()), PlanPixels(p, new ReportDesign { HiddenKinds = { new TextEntity().KindName } }));
        Assert.NotEqual(PlanPixels(p, new ReportDesign()), PlanPixels(p, new ReportDesign { HiddenKinds = { new TextEntity().KindName } }));
        Assert.Equal(before, ProjectJson.Serialize(p));
    }

    [Fact]
    public void AFirstMovementSheetWithNoArrowsStillHasItsPage()
    {
        var p = Blast(5);
        foreach (var hole in p.Holes) hole.FiringTimeMs = null;
        using var sheet = Reports.BuildFirstMovement(p, new FirstMovementSheet { Title = "Arrows", Arrows = new FirstMovementOverlay() }, new ReportDesign(), Now);
        Assert.Single(sheet.Pages);
        using var empty = Reports.BuildFirstMovement(new BlastProject(), new FirstMovementSheet { Title = "Arrows", Arrows = new FirstMovementOverlay() }, new ReportDesign(), Now);
        Assert.Single(empty.Pages);
    }

    [Fact]
    public void ALockedFrameStaysWhenAKindIsSwitchedOff()
    {
        var text = new TextEntity().KindName;
        var p = Blast(30);
        // locked with everything on: the text goes, the holes stay where they were
        var locked = new ReportDesign { HiddenKinds = { text }, View = new DrawingView { Locked = true } };
        Assert.NotEqual(PlanPixels(p, new ReportDesign { HiddenKinds = { text } }), PlanPixels(p, locked));
        var shown = PlanPixels(p, new ReportDesign());
        Assert.NotEqual(shown, PlanPixels(p, locked));
        // locked with the text off, the frame stays that of the holes when the text comes back
        var fitted = new ReportDesign { HiddenKinds = { text }, View = new DrawingView { Locked = true, LockedHiddenKinds = { text } } };
        Assert.Equal(PlanPixels(p, new ReportDesign { HiddenKinds = { text } }), PlanPixels(p, fitted));
        fitted.HiddenKinds.Clear();
        Assert.NotEqual(shown, PlanPixels(p, fitted));
        // unlocked, the kinds of the lock count for nothing
        var open = new ReportDesign { View = new DrawingView { Locked = false, LockedHiddenKinds = { text } } };
        Assert.Equal(shown, PlanPixels(p, open));
    }

    [Fact]
    public void TheViewMovesAndZoomsTheDrawing()
    {
        var p = Blast(30);
        var plain = PlanPixels(p, new ReportDesign());
        Assert.NotEqual(plain, PlanPixels(p, new ReportDesign { View = new DrawingView { PanX = 0.1 } }));
        Assert.NotEqual(plain, PlanPixels(p, new ReportDesign { View = new DrawingView { PanY = 0.1 } }));
        Assert.NotEqual(plain, PlanPixels(p, new ReportDesign { View = new DrawingView { Zoom = 1.2 } }));
        // back and forth is where it started
        var view = new DrawingView { PanX = 0.3, PanY = -0.2 };
        view.ZoomBy(2);
        Assert.Equal(2, view.Zoom, 9);
        Assert.Equal(0.6, view.PanX, 9);
        Assert.Equal(-0.4, view.PanY, 9);
        view.ZoomBy(0.5);
        Assert.Equal(1, view.Zoom, 9);
        Assert.Equal(0.3, view.PanX, 9);
        // the limit of the zoom holds the pan too: what is in the middle stays there
        var far = new DrawingView { Zoom = DrawingView.MaxZoom, PanX = 1 };
        far.ZoomBy(2);
        Assert.Equal(DrawingView.MaxZoom, far.Zoom);
        Assert.Equal(1, far.PanX);
    }

    [Fact]
    public void ThePanIsThatOfTheViewNotOfThePicture()
    {
        // the ink of the holes goes to the left when the view goes to the right, and down when it goes up
        (double X, double Y) Ink(DrawingView view)
        {
            using var plan = Reports.BuildPlan(Blast(30), new PlanOptions { Title = "Plan" }, new ReportDesign { View = view }, Now);
            using var page = plan.Rasterize(0, 72);
            double x = 0, y = 0, n = 0;
            for (int j = 20; j < page.Height - 90; j++)
                for (int i = 20; i < page.Width - 20; i++)
                {
                    if (page.GetPixel(i, j).Red > 200) continue;
                    x += i;
                    y += j;
                    n++;
                }
            return (x / n, y / n);
        }
        var plain = Ink(new DrawingView());
        Assert.True(Ink(new DrawingView { PanX = 0.02 }).X < plain.X);
        Assert.True(Ink(new DrawingView { PanY = 0.02 }).Y > plain.Y);
    }

    [Fact]
    public void AScaleWrittenOnTheSheetHasNoZoom()
    {
        byte[] Sheet(double zoom)
        {
            using var plan = Reports.BuildPlan(Blast(30), new PlanOptions { Title = "Plan", Scale = 500 }, new ReportDesign { View = new DrawingView { Zoom = zoom } }, Now);
            using var page = plan.Rasterize(0, 72);
            return page.Bytes;
        }
        Assert.Equal(Sheet(1), Sheet(3));
    }

    [Fact]
    public void TheViewGoesWithTheDesignFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + BlastDesigner.IO.ReportDesignFile.Extension);
        try
        {
            BlastDesigner.IO.ReportDesignFile.Save(path, new ReportDesign { View = new DrawingView { Locked = true, LockedHiddenKinds = { "Text" }, Zoom = 1.44, PanX = 0.2, PanY = -0.1 } });
            var view = BlastDesigner.IO.ReportDesignFile.Load(path).View;
            Assert.True(view.Locked);
            Assert.Equal(new[] { "Text" }, view.LockedHiddenKinds);
            Assert.Equal(1.44, view.Zoom);
            Assert.Equal(0.2, view.PanX);
            Assert.Equal(-0.1, view.PanY);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheGridOfAReportDoesNotFollowTheMainWindow()
    {
        var shown = Blast(30);
        shown.View.ShowGrid = true;
        var hidden = Blast(30);
        hidden.View.ShowGrid = false;
        Assert.Equal(PlanPixels(hidden, new ReportDesign()), PlanPixels(shown, new ReportDesign()));
        Assert.Equal(PlanPixels(shown, new ReportDesign { ShowGrid = true }), PlanPixels(hidden, new ReportDesign { ShowGrid = true }));
        Assert.NotEqual(PlanPixels(shown, new ReportDesign()), PlanPixels(shown, new ReportDesign { ShowGrid = true }));
    }

    [Fact]
    public void TheVisibilityGoesWithTheProgramSettings()
    {
        var settings = new BlastDesigner.IO.ProgramSettings { ReportDesign = new ReportDesign { HiddenKinds = { "Text" }, KindsChosen = true, ShowGrid = true } };
        var back = ProjectJson.DeepClone(settings).ReportDesign;
        Assert.True(back.ShowGrid);
        Assert.True(back.KindsChosen);
        Assert.Equal(new[] { "Text" }, back.HiddenKinds);
        Assert.False(new ReportDesign().ShowGrid);
    }

    [Fact]
    public void ADesignIsSavedToAFileAndReadBack()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + BlastDesigner.IO.ReportDesignFile.Extension);
        try
        {
            var design = new ReportDesign { Frame = { Visible = true, MarginMm = 7, OnAllPages = false, Pages = "1 3" }, Stamps = { Stamp(StampPages.First, StampPlace.TopRight) }, HiddenKinds = { "Text" }, KindsChosen = true, ShowGrid = true };
            BlastDesigner.IO.ReportDesignFile.Save(path, design);
            var back = BlastDesigner.IO.ReportDesignFile.Load(path);
            Assert.Equal(7, back.Frame.MarginMm);
            Assert.Equal("1 3", back.Frame.Pages);
            Assert.False(back.Frame.OnAllPages);
            Assert.Equal(StampPlace.TopRight, back.Stamps.Single().Place);
            Assert.Equal("{blast}", back.Stamps.Single().Rows[0].Text);
            // the visibility goes with it
            Assert.Equal(new[] { "Text" }, back.HiddenKinds);
            Assert.True(back.ShowGrid);

            File.WriteAllText(path, "{\"format\":\"something else\"}");
            Assert.Throws<InvalidDataException>(() => BlastDesigner.IO.ReportDesignFile.Load(path));
            File.WriteAllText(path, "not json");
            Assert.Throws<InvalidDataException>(() => BlastDesigner.IO.ReportDesignFile.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheArrowsAreOnTheSheet()
    {
        var p = Blast(60);
        foreach (var h in p.Holes) h.ElectronicTimeMs = h.Collar.X * 3 + h.Collar.Y * 2;
        Timing.Solve(p);
        var arrows = new FirstMovementOverlay();
        Assert.NotEmpty(arrows.Arrows(p));
        using var report = Reports.BuildFirstMovement(p, new FirstMovementSheet { Title = "Arrows", Arrows = arrows }, new ReportDesign(), Now);
        using var page = report.Rasterize(0, 72);
        var yellow = 0;
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
            {
                var c = page.GetPixel(x, y);
                if (c.Red > 200 && c.Green > 150 && c.Blue < 110) yellow++;
            }
        Assert.True(yellow > 200, $"{yellow} yellow pixels");
    }

    [Fact]
    public void ADisposedDocumentDrawsNothing()
    {
        var report = Reports.BuildPlan(Blast(5), new PlanOptions(), new ReportDesign(), Now);
        report.Dispose();
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);
        report.Draw(canvas, 0);
    }
}
