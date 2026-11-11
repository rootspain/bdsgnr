namespace BlastDesigner.Domain.Model;

/// <summary>Sections that can be switched on in a report, in their order; files keep them by name.</summary>
[Flags]
public enum ReportSections
{
    None = 0,
    Header = 1,
    Plan = 2,
    CrossSection = 4,
    Quantities = 8,
    HoleTable = 16,
    Loading = 32,
    Timing = 64,
    Vibration = 128,
    Outcomes = 256,
    /// <summary>Burden relief over the timing triangulation, on a plan with its legend.</summary>
    BurdenRelief = 512,
    All = 1023
}

public sealed class ReportOptions
{
    public string Title { get; set; } = "Blast report";
    /// <summary>
    /// What follows the title at the top of each page; the fields of a stamp work here, <c>{name}</c> is the name of the blast.
    /// Empty, or with all its fields empty, nothing follows it.
    /// </summary>
    public string TitleAddition { get; set; } = "— {name}";
    public ReportSections Sections { get; set; } = ReportSections.All;
    /// <summary>The sections that start on a page of their own.</summary>
    public ReportSections NewPage { get; set; } = ReportSections.None;
    public bool Landscape { get; set; }
    public TimingLabelMode PlanTimingLabels { get; set; } = TimingLabelMode.InholeTime;
    /// <summary>Top of the colour scale of the burden relief section, ms/m; null: the most relief of the blast.</summary>
    public double? BurdenReliefLimit { get; set; }
}

/// <summary>A plan on one sheet.</summary>
public sealed class PlanOptions
{
    /// <summary>Its fields are filled in: a design goes from blast to blast.</summary>
    public string Title { get; set; } = "{name}";
    /// <summary>The 1:N of the sheet; null fits the blast to the sheet.</summary>
    public double? Scale { get; set; }
    public bool Landscape { get; set; } = true;
}

/// <summary>Where a stamp stands on the page.</summary>
public enum StampPlace { BottomRight, BottomLeft, BottomWide, TopRight }

/// <summary>The side a stamp adjoins the one before it at the same place, with no gap.</summary>
public enum StampAttach { Horizontal, Vertical }

/// <summary>The pages a stamp is on.</summary>
public enum StampPages { All, First, Specified }

/// <summary>The line around the page.</summary>
public sealed class ReportFrame
{
    public bool Visible { get; set; }
    /// <summary>Distance from the edge of the sheet, mm.</summary>
    public double MarginMm { get; set; } = 10;
    /// <summary>Thickness of the line, mm.</summary>
    public double LineWidthMm { get; set; } = 0.5;
    public bool OnAllPages { get; set; } = true;
    /// <summary>The pages that have the frame when it is not on all of them: their numbers, separated by spaces.</summary>
    public string Pages { get; set; } = string.Empty;

    /// <summary>Whether a page, counted from 1, has the frame; what is not a number in <see cref="Pages"/> is passed over.</summary>
    public bool IsOn(int page) => Visible && (OnAllPages || IsListed(Pages, page));

    /// <summary>Whether a list of page numbers separated by spaces has a page.</summary>
    public static bool IsListed(string numbers, int page) => numbers.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(p => int.TryParse(p, out var n) && n == page);
}

/// <summary>Where a text stands in its cell of a stamp.</summary>
public enum StampAlign { Left, Center, Right }

/// <summary>Where the texts of a column of a stamp stand in the height of their lines.</summary>
public enum StampVerticalAlign { Top, Middle, Bottom }

/// <summary>A line of a stamp: a caption and its text, which can hold the fields of <see cref="ReportDesign.Fields"/>.</summary>
public sealed class StampRow
{
    public string Label { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public StampAlign LabelAlign { get; set; }
    public StampAlign TextAlign { get; set; }
}

/// <summary>A table on the page: who and what the sheet is about.</summary>
public sealed class ReportStamp
{
    public string Name { get; set; } = "Stamp";
    public StampPlace Place { get; set; } = StampPlace.BottomRight;
    public StampAttach Attach { get; set; } = StampAttach.Vertical;
    public StampPages Pages { get; set; } = StampPages.All;
    /// <summary>The pages of <see cref="StampPages.Specified"/>: their numbers, separated by spaces.</summary>
    public string PageNumbers { get; set; } = string.Empty;
    /// <summary>Width of the column of captions, mm; with <see cref="TextWidthMm"/> it is the width of the stamp. A column of width 0 is not drawn.</summary>
    public double LabelWidthMm { get; set; } = 41;
    /// <summary>Width of the column of texts, mm.</summary>
    public double TextWidthMm { get; set; } = 79;
    public StampVerticalAlign LabelVerticalAlign { get; set; } = StampVerticalAlign.Middle;
    public StampVerticalAlign TextVerticalAlign { get; set; } = StampVerticalAlign.Middle;
    public List<StampRow> Rows { get; set; } = new();

    /// <summary>Whether the column of captions shows.</summary>
    public bool HasLabels() => LabelWidthMm > 0;

    /// <summary>Whether the column of texts shows.</summary>
    public bool HasTexts() => TextWidthMm > 0;

    /// <summary>The width of the stamp, mm.</summary>
    public double WidthMm() => LabelWidthMm + TextWidthMm;

    /// <summary>Whether a page, counted from 1, has the stamp.</summary>
    public bool IsOn(int page) => Pages switch
    {
        StampPages.All => true,
        StampPages.First => page == 1,
        _ => ReportFrame.IsListed(PageNumbers, page)
    };
}

/// <summary>The line at the foot of the pages of a report.</summary>
public sealed class ReportFooter
{
    public bool ShowDate { get; set; } = true;
    /// <summary>The date with its time, otherwise the date alone; both in the format of the system.</summary>
    public bool DateWithTime { get; set; } = true;
    public bool ShowPageNumber { get; set; } = true;
    public StampPages NumberedPages { get; set; } = StampPages.All;
    /// <summary>The pages of <see cref="StampPages.Specified"/>: their numbers, separated by spaces.</summary>
    public string PageNumbers { get; set; } = string.Empty;

    /// <summary>Whether a page, counted from 1, shows its number.</summary>
    public bool HasNumber(int page) => ShowPageNumber && NumberedPages switch
    {
        StampPages.All => true,
        StampPages.First => page == 1,
        _ => ReportFrame.IsListed(PageNumbers, page)
    };
}

/// <summary>
/// How the drawings of a report stand over the fit to their objects. Nothing in it is a place on the ground:
/// a design goes from blast to blast, and has to frame each of them.
/// </summary>
public sealed class DrawingView
{
    public const double MinZoom = 0.1, MaxZoom = 50;

    /// <summary>The check boxes of the visibility leave the frame as it is: the drawing stays fitted to <see cref="LockedHiddenKinds"/>.</summary>
    public bool Locked { get; set; }
    /// <summary>The kinds that were left out when the frame was locked.</summary>
    public List<string> LockedHiddenKinds { get; set; } = new();
    /// <summary>Times the scale of the fit.</summary>
    public double Zoom { get; set; } = 1;
    /// <summary>How far the view has gone to the right, in widths of the drawing.</summary>
    public double PanX { get; set; }
    /// <summary>How far the view has gone up, in heights of the drawing.</summary>
    public double PanY { get; set; }

    /// <summary>Zooms about the middle of the drawing, which keeps what is there in place.</summary>
    public void ZoomBy(double factor)
    {
        var zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        PanX *= zoom / Zoom;
        PanY *= zoom / Zoom;
        Zoom = zoom;
    }
}

/// <summary>What the drawing of a section shows and how it is framed.</summary>
public sealed class SectionVisibility
{
    /// <summary>Kinds of object left out, by <see cref="Entity.KindName"/>.</summary>
    public List<string> HiddenKinds { get; set; } = new();
    public bool ShowGrid { get; set; }
    public DrawingView View { get; set; } = new();
}

/// <summary>What is drawn around the content of every report: it belongs to the user, not to one blast.</summary>
public sealed class ReportDesign
{
    /// <summary>What the text of a stamp can take from the blast and the sheet.</summary>
    public static readonly IReadOnlyList<string> Fields = new[] { "{title}", "{blast}", "{number}", "{mine}", "{bench}", "{location}", "{client}", "{author}", "{shotdate}", "{date}", "{page}", "{pages}" };

    public ReportOptions Report { get; set; } = new();
    public PlanOptions Plan { get; set; } = new();
    public ReportFrame Frame { get; set; } = new();
    public List<ReportStamp> Stamps { get; set; } = new();
    public ReportFooter Footer { get; set; } = new();
    /// <summary>Kinds of object left out of the drawings of a report, by <see cref="Entity.KindName"/>.</summary>
    public List<string> HiddenKinds { get; set; } = new();
    /// <summary>The kinds are the design's own from the first preview on; that one takes them from the main window.</summary>
    public bool KindsChosen { get; set; }
    /// <summary>The coordinate grid on the drawings of a report, whatever the main window shows.</summary>
    public bool ShowGrid { get; set; }
    public DrawingView View { get; set; } = new();
    /// <summary>The same for the drawing of a section, apart from the plan: what a section line starts from, and a line only picked keeps.</summary>
    public SectionVisibility Section { get; set; } = new();
    /// <summary>What the section of each line has made its own, by the id of the line.</summary>
    public Dictionary<Guid, SectionVisibility> Sections { get; set; } = new();

    /// <summary>What the section of a line shows; null for a line only picked.</summary>
    public SectionVisibility SectionOf(Guid? line) => line is Guid id && Sections.TryGetValue(id, out var own) ? own : Section;

    /// <summary>
    /// A copy without what belongs to the drawings of one blast: the zoom and the shift of the plan and of the section,
    /// and what the section lines of the blast have made their own. That is what goes to a design file and to the
    /// program settings, to be put on other blasts.
    /// </summary>
    public ReportDesign Portable()
    {
        var d = ProjectJson.DeepClone(this);
        d.View = new();
        d.Section.View = new();
        d.Sections = new();
        return d;
    }

    /// <summary>Takes the framing of the drawings and the sections of the lines from the design of a blast, which a design put on it keeps.</summary>
    public void KeepFramingOf(ReportDesign blast)
    {
        View = blast.View;
        Section.View = blast.Section.View;
        Sections = blast.Sections;
    }
}
