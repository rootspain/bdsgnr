using System.Text.Json.Serialization;
using BlastDesigner.Domain.Geometry;

namespace BlastDesigner.Domain.Model;

/// <summary>The whole blast design document.</summary>
public sealed class BlastProject
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public ProjectHeader Header { get; set; } = new();
    public DesignData Design { get; set; } = new();
    public List<Product> Products { get; set; } = new();
    /// <summary>The tables above the products: types, kinds, groups and families.</summary>
    public ProductCatalogue Catalogue { get; set; } = new();
    /// <summary>The products offered for anything new; the deleted ones only go on showing where they are used.</summary>
    [JsonIgnore] public IEnumerable<Product> ActiveProducts => Products.Where(p => !p.Deleted);
    public List<Layer> Layers { get; set; } = new();
    public List<Entity> Entities { get; set; } = new();
    public List<HoleRow> Rows { get; set; } = new();
    /// <summary>
    /// Patterns the pattern tool filled outlines with: each remembers its grid, so it can be turned or laid out again later
    /// (<see cref="HolePattern"/>). Kept beside the entities, not as one: a program that does not know them reads the holes as before.
    /// </summary>
    public List<HolePattern> Patterns { get; set; } = new();
    public List<LoadingRule> LoadingRules { get; set; } = new();
    public BlastOutcomes Outcomes { get; set; } = new();
    public CostSchema Costs { get; set; } = new();
    public ElectronicSettings Electronic { get; set; } = new();
    public VibrationModel Vibration { get; set; } = new();
    public SummaryBlock Summary { get; set; } = new();
    public ViewState View { get; set; } = new();
    public List<Attachment> Attachments { get; set; } = new();
    public Guid DefaultLayerId { get; set; }
    public List<InitiationRule> InitiationRules { get; set; } = new();
    public DesignTolerances Tolerances { get; set; } = new();
    public FlyrockModel Flyrock { get; set; } = new();
    public FragmentationModel Fragmentation { get; set; } = new();
    /// <summary>
    /// Shortest top stemming accepted by the loading check, in hole diameters; it goes with the project. Null in files
    /// from before it: the program settings give it then.
    /// </summary>
    public double? MinStemmingDiameters { get; set; }
    public List<VibrationRecord> VibrationRecords { get; set; } = new();
    public SiteCoordinateSystem Coordinates { get; set; } = new();
    /// <summary>Профиль расчёта проекта; null — встроенный «SHOTPlus 6» (так открываются файлы до движка вычислений).</summary>
    public ProjectCalc? Calc { get; set; }
    /// <summary>
    /// The design of the reports of this blast, with the framing of its drawings; null until a report of it is first
    /// previewed (and in files from before): the program settings give it then.
    /// </summary>
    public ReportDesign? ReportDesign { get; set; }

    [JsonIgnore] public IEnumerable<Hole> Holes => Entities.OfType<Hole>();
    [JsonIgnore] public IEnumerable<TieEntity> Ties => Entities.OfType<TieEntity>();

    /// <summary>The same project in a new object, for changing a list of it without touching the original.</summary>
    internal BlastProject ShallowCopy() => (BlastProject)MemberwiseClone();

    public Product? FindProduct(string? id) => id is null ? null : Products.FirstOrDefault(p => p.Id == id);
    public Layer? FindLayer(Guid id) => Layers.FirstOrDefault(l => l.Id == id);
    public Entity? FindEntity(Guid id) => Entities.FirstOrDefault(e => e.Id == id);
    public HolePattern? FindPattern(Guid? id) => id is null ? null : Patterns.FirstOrDefault(p => p.Id == id);

    /// <summary>Returns the layer with the given name, creating it when missing.</summary>
    public Layer GetOrCreateLayer(string name, uint color = 0xFF000000, string group = "")
    {
        var l = Layers.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (l != null) return l;
        l = new Layer { Name = name, Color = color, Group = group };
        Layers.Add(l);
        return l;
    }

    public Layer DefaultLayer => FindLayer(DefaultLayerId) ?? Layers.FirstOrDefault() ?? GetOrCreateLayer("Default");

    /// <summary>Adds an entity to a layer (the default layer when none is given).</summary>
    public T Add<T>(T e, Layer? layer = null) where T : Entity
    {
        e.LayerId = (layer ?? DefaultLayer).Id;
        Entities.Add(e);
        return e;
    }

    /// <summary>Creates an empty project with the standard layers and the starter catalogue.</summary>
    public static BlastProject CreateNew(string name = "New blast")
    {
        var p = new BlastProject();
        p.Header.Name = name;
        p.Header.Created = DateTime.Now;
        var def = p.GetOrCreateLayer("Default", 0xFF000000);
        p.GetOrCreateLayer(StandardLayers.Holes, 0xFF1E3A8A, "Design");
        p.GetOrCreateLayer(StandardLayers.Ties, 0xFFDC2626, "Design");
        p.GetOrCreateLayer(StandardLayers.Boundary, 0xFF16A34A, "Design");
        p.GetOrCreateLayer(StandardLayers.Survey, 0xFF7C3AED, StandardLayers.TopographyGroup);
        p.GetOrCreateLayer(StandardLayers.Surfaces, 0xFF64748B, StandardLayers.TopographyGroup);
        p.GetOrCreateLayer(StandardLayers.Annotation, 0xFF0F172A, "Drawing");
        p.DefaultLayerId = def.Id;
        p.Products = Product.DefaultCatalogue();
        p.Catalogue = ProductCatalogue.BuiltIn();
        ProductCatalogue.Link(p);
        p.LoadingRules.Add(DefaultRule(p));
        return p;
    }

    private static LoadingRule DefaultRule(BlastProject p)
    {
        var anfo = p.Products.First(x => x.Name == "ANFO");
        var stem = p.Products.First(x => x.Kind == ProductKind.Stemming);
        var booster = p.Products.First(x => x.Kind == ProductKind.Booster);
        var det = p.Products.First(x => x.Kind == ProductKind.DownholeDetonator);
        return new LoadingRule
        {
            Name = "Standard production",
            StemmingLength = 3,
            StemmingProductId = stem.Id,
            Decks = { new DeckTemplate { ProductId = anfo.Id, Length = 0, Primers = { new PrimerTemplate { Offset = 0.5, BoosterId = booster.Id, DetonatorId = det.Id } } } }
        };
    }
}

public static class StandardLayers
{
    public const string Holes = "Holes";
    public const string Ties = "Ties";
    public const string Boundary = "Boundary";
    public const string Survey = "Survey";
    public const string Surfaces = "Surfaces";
    public const string Annotation = "Annotation";
    public const string Analysis = "Analysis";
    public const string TimingLines = "Timing lines";
    /// <summary>Layer group of the survey, surfaces and recognized bench lines; "Survey" before 0.2.1.</summary>
    public const string TopographyGroup = "Topography";
}

public sealed class ProjectHeader
{
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = Environment.UserName;
    public string Description { get; set; } = string.Empty;
    public string Client { get; set; } = string.Empty;
    public string Mine { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Bench { get; set; } = string.Empty;
    public string BlastNumber { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? ShotDate { get; set; }
    /// <summary>Direction of the blast's free face, degrees clockwise from north. Used by "Blast view" and row logic.</summary>
    public double BlastOrientation { get; set; }
}

public enum PatternType { Rectangular, Staggered }

public sealed class DesignData
{
    public double Burden { get; set; } = 4;
    public double Spacing { get; set; } = 4.5;
    public double BenchHeight { get; set; } = 10;
    public double Subdrill { get; set; } = 1;
    public double Diameter { get; set; } = 102;
    public double Angle { get; set; }
    public double Bearing { get; set; }
    public double StemmingLength { get; set; } = 3;
    /// <summary>In-situ rock density, t/m³.</summary>
    public double RockDensity { get; set; } = 2.6;
    public PatternType Pattern { get; set; } = PatternType.Staggered;
    /// <summary>Holes closer than this (m) are treated as duplicates on import.</summary>
    public double DuplicateTolerance { get; set; } = 0.2;
    public List<TypeDefinition> HoleTypes { get; set; } = TypeDefinition.Defaults("Hole type", true);
    public List<TypeDefinition> MaterialTypes { get; set; } = TypeDefinition.Defaults("Material", false);
}

/// <summary>Name and colour of a hole type or material type (indexes 1..8).</summary>
public sealed class TypeDefinition
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint Color { get; set; }
    public double Diameter { get; set; }
    public double Density { get; set; } = 2.6;

    private static readonly uint[] Palette =
        { 0xFF1E3A8A, 0xFFDC2626, 0xFF16A34A, 0xFFF59E0B, 0xFF7C3AED, 0xFF0891B2, 0xFFDB2777, 0xFF65A30D };

    public static List<TypeDefinition> Defaults(string prefix, bool holes) =>
        Enumerable.Range(1, 8).Select(i => new TypeDefinition
        {
            Index = i,
            Name = holes && i == 1 ? "Production" : holes && i == 2 ? "Buffer" : holes && i == 3 ? "Trim" : $"{prefix} {i}",
            Color = Palette[i - 1],
            Diameter = 102
        }).ToList();
}

/// <summary>Named ordered set of holes forming a row.</summary>
public sealed class HoleRow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Row 1";
    public List<Guid> HoleIds { get; set; } = new();
    public double Burden { get; set; }
    public double Spacing { get; set; }
}

/// <summary>
/// The grid of a pattern drawn with the pattern tool. Node (i, j) lies at
/// <c>Center + u·(PhaseU + i·Spacing + stagger) + v·(PhaseV + j·Burden)</c>, where v points against the blast movement
/// (<see cref="Azimuth"/>), u along the rows, and odd rows of a staggered pattern are shifted by half a spacing; row j = 0 was
/// the face row when the pattern was made. Its holes know their node (<see cref="Hole.GridI"/>, <see cref="Hole.GridJ"/>), so
/// turning the grid moves each hole with its node and keeps everything else of it.
/// </summary>
public sealed class HolePattern
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Pattern 1";
    /// <summary>The outline the pattern fills (a copy of its points).</summary>
    public List<Vec3> Outline { get; set; } = new();
    /// <summary>The polygon the outline was taken from, when there is one in the project.</summary>
    public Guid? OutlineEntityId { get; set; }
    /// <summary>Centre of the outline's area: the grid turns about it. Z is the collar level of new holes.</summary>
    public Vec3 Center { get; set; }
    /// <summary>Direction of the burden (the blast movement the rows were laid across), degrees clockwise from north.</summary>
    public double Azimuth { get; set; }
    /// <summary><see cref="Azimuth"/> when the pattern was made: the turn of the grid since is the difference.</summary>
    public double InitialAzimuth { get; set; }
    public double PhaseU { get; set; }
    public double PhaseV { get; set; }
    public double Burden { get; set; }
    public double Spacing { get; set; }
    public PatternType Type { get; set; }
    public double EdgeOffset { get; set; }
    public string LabelPrefix { get; set; } = string.Empty;
    public int StartNumber { get; set; } = 1;
    public double? ToLevel { get; set; }
    public bool ToSurface { get; set; }
    public PatternHoleDefaults HoleDefaults { get; set; } = new();
    /// <summary>The row of each grid row j.</summary>
    public Dictionary<int, Guid> RowIds { get; set; } = new();
    /// <summary>Nodes whose hole was deleted by hand: laying the grid out again does not bring them back.</summary>
    public List<GridIndex> Suppressed { get; set; } = new();
    /// <summary>Turning the grid turns the blast orientation by as much.</summary>
    public bool RotateOrientation { get; set; }
    /// <summary>Laying the grid out again keeps the numbers of the holes; new holes take the next free ones.</summary>
    public bool KeepLabels { get; set; }
    /// <summary>Raised by every layout of the grid within a change, so what follows the change can tell it from a hand edit.</summary>
    [JsonIgnore] public int Revision { get; set; }

    public bool IsSuppressed(int i, int j) => Suppressed.Any(s => s.I == i && s.J == j);
}

/// <summary>What the new holes of a pattern are made with.</summary>
public sealed class PatternHoleDefaults
{
    public double Diameter { get; set; } = 102;
    public double Angle { get; set; }
    public double Bearing { get; set; }
    public double Subdrill { get; set; } = 1;
    public double BenchHeight { get; set; } = 10;
    public int HoleType { get; set; } = 1;
    public bool Dummy { get; set; }
}

/// <summary>A node of a pattern grid.</summary>
public sealed class GridIndex
{
    public int I { get; set; }
    public int J { get; set; }
}

public sealed class Layer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Layer";
    public string Group { get; set; } = string.Empty;
    public uint Color { get; set; } = 0xFF000000;
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    /// <summary>Frozen layers are visible but cannot be selected or changed.</summary>
    public bool Frozen { get; set; }
    public bool ShowText { get; set; } = true;
    public double LineWidth { get; set; } = 1;

    public bool IsEditable => Visible && !Locked && !Frozen;
}

public sealed class BlastOutcomes
{
    public double? FragmentationP50 { get; set; }
    public double? FragmentationP80 { get; set; }
    public double? MaxFlyrock { get; set; }
    public double? MeasuredPpv { get; set; }
    public double? MeasuredAirblast { get; set; }
    public double? MuckpileHeight { get; set; }
    public double? DiggingRate { get; set; }
    public int Rating { get; set; }
    public string Comments { get; set; } = string.Empty;
}

public sealed class CostSchema
{
    public string Currency { get; set; } = "USD";
    public double DrillingPerMetre { get; set; } = 10;
    public double LoadingPerHole { get; set; } = 5;
    public double LabourPerBlast { get; set; } = 500;
    public double OtherFixed { get; set; }
}

public sealed class ElectronicSettings
{
    public double HoleIntervalMs { get; set; } = 17;
    public double RowIntervalMs { get; set; } = 42;
    public double MaxTimeMs { get; set; } = 20000;
    /// <summary>Times are programmed to 0.1 ms; off, to whole milliseconds (files before 0.3.1 had a time step instead).</summary>
    public bool AllowSubMs { get; set; }
    /// <summary>Programmed times are offset from this base time.</summary>
    public double BaseTimeMs { get; set; }
    /// <summary>Between the explosive decks of a hole (separated by inert ones), each next one after the one before, ms.</summary>
    public double InterDeckDelayMs { get; set; }
    /// <summary>Between the electronic primers of one deck, ms.</summary>
    public double IntraDeckDelayMs { get; set; }
    /// <summary>The order the explosive decks of a hole fire in.</summary>
    public DeckFiringOrder DeckOrder { get; set; }
    /// <summary>The order the electronic primers of a deck fire in.</summary>
    public PrimerFiringOrder PrimerOrder { get; set; }
    /// <summary>Each electronic primer fires at the hole's time plus its own offset (<see cref="Primer.OffsetMs"/>), not by the deck delays.</summary>
    public bool UseOffsets { get; set; }
}

/// <summary>The order the explosive decks of a hole fire in (SHOTPlus: bottom-top, top-bottom, simultaneous).</summary>
public enum DeckFiringOrder { BottomUp, TopDown, Simultaneous }

/// <summary>The order the electronic primers of one deck fire in.</summary>
public enum PrimerFiringOrder { BottomUp, TopDown }

/// <summary>Site constants for scaled-distance vibration and airblast prediction.</summary>
public sealed class VibrationModel
{
    /// <summary>PPV = K · (D / √Q)^(−B), mm/s.</summary>
    public double SiteK { get; set; } = 1140;
    public double SiteB { get; set; } = 1.6;
    /// <summary>Airblast P = Ka · (D / ∛Q)^(−Ba), kPa.</summary>
    public double AirK { get; set; } = 10;
    public double AirB { get; set; } = 1.45;
    /// <summary>Window for the maximum instantaneous charge, ms.</summary>
    public double WindowMs { get; set; } = 8;
    public double PpvLimit { get; set; } = 10;
    public double AirblastLimitDb { get; set; } = 120;
}

public sealed class SummaryBlock
{
    public bool Visible { get; set; } = true;
    public bool ShowHeader { get; set; } = true;
    public bool ShowQuantities { get; set; } = true;
    public bool ShowPattern { get; set; } = true;
    public string Corner { get; set; } = "TopRight";
    /// <summary>Calculated blast totals listed in the summary panel and the summary block of the plan (ids such as <c>blast.powderFactor</c>).</summary>
    public List<string>? CalcTotals { get; set; }
}

/// <summary>Saved camera and visibility for the main view.</summary>
public sealed class ViewState
{
    public double TargetX { get; set; }
    public double TargetY { get; set; }
    public double TargetZ { get; set; }
    public double Yaw { get; set; }
    public double Pitch { get; set; } = 90;
    public double Scale { get; set; } = 5;
    public HashSet<int> HiddenHoleTypes { get; set; } = new();
    public HashSet<int> HiddenMaterialTypes { get; set; } = new();
    public HashSet<HoleState> HiddenStates { get; set; } = new();
    public bool ShowGrid { get; set; } = true;
    public bool ShowAxes { get; set; } = true;
    /// <summary>
    /// What lies behind a surface (under it in the plan) is drawn paler: a quarter of the opacity of the surface in front.
    /// Holes, their charges and labels stay as they are. Off: surfaces and lines are drawn far to near, as they come.
    /// </summary>
    public bool FadeHidden { get; set; }
    public bool ShowHoleLegend { get; set; }
    public bool ShowTieLegend { get; set; }
    // new projects show collars with their charge columns, the hole number and the firing order (no plain hole tracks)
    public bool CollarsOnly { get; set; } = true;
    public bool ReverseDrawOrder { get; set; }
    public bool ShowLoading { get; set; } = true;
    public HoleLabelMode HoleLabels { get; set; } = HoleLabelMode.Label;
    /// <summary>Font size of the hole labels on the plan and in 3D, px; a file before it reads the default.</summary>
    public int HoleLabelFontSize { get; set; } = DefaultHoleLabelFontSize;
    public const int DefaultHoleLabelFontSize = 12, MinHoleLabelFontSize = 6, MaxHoleLabelFontSize = 40;
    public TimingLabelMode TimingLabels { get; set; } = TimingLabelMode.None;
    public bool ShowSurfaceTimeLabel { get; set; }
    public bool ShowInholeDelayLabel { get; set; }
    public bool ShowHoleTimeLabel { get; set; }
    public bool ShowHoleSequenceLabel { get; set; } = true;
    public bool ShowTies { get; set; } = true;
    public bool ShowRows { get; set; }
    public bool ShowText { get; set; } = true;
    public bool ShowSurfaces { get; set; } = true;
    /// <summary>Collar circles; off leaves only the hole tracks.</summary>
    public bool ShowCollars { get; set; } = true;
    /// <summary>Surveyed bore track (as drilled) drawn along its points.</summary>
    public bool ShowActualTrack { get; set; }
    /// <summary>Track down to the dipped (measured) depth, over the design track.</summary>
    public bool ShowDipTrack { get; set; }
    /// <summary>Marker at the design grade (toe minus subdrill).</summary>
    public bool ShowGradePoint { get; set; }
    /// <summary>Calculated properties of holes shown as columns of the hole table and of the CSV export (ids such as <c>hole.volume</c>).</summary>
    public List<string>? CalcColumns { get; set; }
    /// <summary>Calculated properties of holes written next to the collar, after the labels of <see cref="HoleLabel"/>.</summary>
    public List<string>? CalcLabels { get; set; }
    /// <summary>A calculated property of holes the collars are coloured by (blue lowest to red highest); null: by hole type.</summary>
    public string? ColorBy { get; set; }
    /// <summary>Offset of the as-drilled collar and toe from the design, with its distance.</summary>
    public bool ShowDeviation { get; set; }
    /// <summary>Burden from front-row holes to the crest, drawn and labelled.</summary>
    public bool ShowBurdenMarker { get; set; }
    /// <summary>Measure-while-drilling hardness drawn along the hole track.</summary>
    public bool ShowProfile { get; set; }
    /// <summary>
    /// Hole labels drawn left and right of the collar, top to bottom (<see cref="HoleLabelLayout"/>); whether each one is
    /// drawn is its own switch above.
    /// </summary>
    public List<HoleLabel> LabelsLeft { get; set; } = HoleLabelLayout.DefaultLeft.ToList();
    public List<HoleLabel> LabelsRight { get; set; } = HoleLabelLayout.DefaultRight.ToList();
}

/// <summary>A value written next to a hole on the plan.</summary>
public enum HoleLabel
{
    Number, Length, Diameter, Elevation, Angle, Bearing, Temperature, ExplosiveMass, DeckMasses, DeckSequence, DetonatorId, Type,
    HoleOrder, SurfaceTime, InholeDelay, HoleTime
}

/// <summary>Which side of the collar each hole label is on and in what order, and whether it is switched on.</summary>
public static class HoleLabelLayout
{
    public static readonly HoleLabel[] DefaultLeft =
    {
        HoleLabel.HoleOrder, HoleLabel.HoleTime, HoleLabel.SurfaceTime, HoleLabel.InholeDelay, HoleLabel.DeckSequence,
        HoleLabel.DetonatorId, HoleLabel.Type, HoleLabel.Temperature
    };
    public static readonly HoleLabel[] DefaultRight =
    {
        HoleLabel.Number, HoleLabel.Length, HoleLabel.Diameter, HoleLabel.Elevation, HoleLabel.Angle, HoleLabel.Bearing,
        HoleLabel.ExplosiveMass, HoleLabel.DeckMasses
    };

    /// <summary>
    /// Both sides as stored, each label once (on the side it is first found); a label on neither side (a file from
    /// before the layout, or a label added since) goes to the end of its default side.
    /// </summary>
    public static (List<HoleLabel> Left, List<HoleLabel> Right) Sides(ViewState v)
    {
        var seen = new HashSet<HoleLabel>();
        var left = (v.LabelsLeft ?? new()).Where(l => Enum.IsDefined(l) && seen.Add(l)).ToList();
        var right = (v.LabelsRight ?? new()).Where(l => Enum.IsDefined(l) && seen.Add(l)).ToList();
        foreach (var l in Enum.GetValues<HoleLabel>())
            if (!seen.Contains(l)) (DefaultLeft.Contains(l) ? left : right).Add(l);
        return (left, right);
    }

    /// <summary>Puts a label on a side at an index of that side (counted without the label itself).</summary>
    public static void Move(ViewState v, HoleLabel label, bool right, int index)
    {
        var (l, r) = Sides(v);
        l.Remove(label);
        r.Remove(label);
        var to = right ? r : l;
        to.Insert(Math.Clamp(index, 0, to.Count), label);
        v.LabelsLeft = l;
        v.LabelsRight = r;
    }

    public static void Reset(ViewState v)
    {
        v.LabelsLeft = DefaultLeft.ToList();
        v.LabelsRight = DefaultRight.ToList();
    }

    /// <summary>The switch of a label that is a bit of <see cref="ViewState.HoleLabels"/>; null for the timing labels.</summary>
    public static HoleLabelMode? Flag(HoleLabel l) => l switch
    {
        HoleLabel.Number => HoleLabelMode.Label,
        HoleLabel.Length => HoleLabelMode.Length,
        HoleLabel.Diameter => HoleLabelMode.Diameter,
        HoleLabel.Elevation => HoleLabelMode.CollarZ,
        HoleLabel.Angle => HoleLabelMode.Angle,
        HoleLabel.Bearing => HoleLabelMode.Bearing,
        HoleLabel.Temperature => HoleLabelMode.Temperature,
        HoleLabel.ExplosiveMass => HoleLabelMode.Charge,
        HoleLabel.DeckMasses => HoleLabelMode.DeckMasses,
        HoleLabel.DeckSequence => HoleLabelMode.DeckSequence,
        HoleLabel.DetonatorId => HoleLabelMode.DetonatorId,
        HoleLabel.Type => HoleLabelMode.Type,
        _ => null
    };

    /// <summary>The timing label mode of a timing label; null for the others.</summary>
    public static TimingLabelMode? Timing(HoleLabel l) => l switch
    {
        HoleLabel.HoleOrder => TimingLabelMode.HoleSequence,
        HoleLabel.SurfaceTime => TimingLabelMode.SurfaceTime,
        HoleLabel.InholeDelay => TimingLabelMode.DelayNumber,
        HoleLabel.HoleTime => TimingLabelMode.InholeTime,
        _ => null
    };

    public static bool IsShown(ViewState v, HoleLabel l) => Flag(l) is { } f ? v.HoleLabels.HasFlag(f) : Timing(l) switch
    {
        TimingLabelMode.SurfaceTime => v.ShowSurfaceTimeLabel || v.TimingLabels == TimingLabelMode.SurfaceTime,
        TimingLabelMode.InholeTime => v.ShowHoleTimeLabel || v.TimingLabels == TimingLabelMode.InholeTime,
        TimingLabelMode.DelayNumber => v.ShowInholeDelayLabel || v.TimingLabels == TimingLabelMode.DelayNumber,
        TimingLabelMode.HoleSequence => v.ShowHoleSequenceLabel || v.TimingLabels == TimingLabelMode.HoleSequence,
        _ => false
    };
}

[Flags]
public enum HoleLabelMode
{
    None = 0,
    Label = 1,
    Length = 2,
    Diameter = 4,
    /// <summary>Angle from vertical (the bearing has its own flag since both were one label).</summary>
    Angle = 8,
    CollarZ = 16,
    Charge = 32,
    Type = 64,
    Bearing = 128,
    DetonatorId = 256,
    /// <summary>Mass of each explosive deck.</summary>
    DeckMasses = 512,
    /// <summary>Order in which the decks of the hole fire (top to bottom).</summary>
    DeckSequence = 1024,
    Temperature = 2048
}

public enum TimingLabelMode
{
    None,
    SurfaceTime,
    InholeTime,
    DelayNumber,
    HoleSequence
}

public sealed class Attachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime Added { get; set; } = DateTime.Now;
    /// <summary>File content; stored as a separate entry in the project archive.</summary>
    [JsonIgnore] public byte[] Data { get; set; } = Array.Empty<byte>();
}
