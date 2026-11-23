using System.Diagnostics;
using BlastDesigner.Calc;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// The Kuz-Ram prediction solved the other way: the powder factor, the grid area and the burden and spacing that give a
/// mean size X50, and the rock factor that a measured X50 means. Closed forms of <see cref="Fragmentation.X50"/>.
/// </summary>
public static class KuzRamInverse
{
    private static double ChargeTerm(double chargeKg, double rws) => Math.Pow(chargeKg, 1.0 / 6) * Math.Pow(115 / rws, 19.0 / 30);

    /// <summary>Powder factor (kg/m³) at which a charge of <paramref name="chargeKg"/> of strength <paramref name="rws"/> gives <paramref name="x50"/> cm in rock A.</summary>
    public static double PowderFactorFor(double x50, double a, double chargeKg, double rws) =>
        Math.Pow(a * ChargeTerm(chargeKg, rws) / x50, 1.25);

    /// <summary>Grid area B·S (m²) per hole that gives <paramref name="x50"/> with a charge per hole and a bench height.</summary>
    public static double GridAreaFor(double x50, double a, double chargeKg, double rws, double benchHeight) =>
        chargeKg / (PowderFactorFor(x50, a, chargeKg, rws) * benchHeight);

    /// <summary>Burden and spacing of a grid area at the ratio S/B.</summary>
    public static (double Burden, double Spacing) BurdenSpacing(double area, double ratio)
    {
        var b = Math.Sqrt(area / ratio);
        return (b, ratio * b);
    }

    /// <summary>The rock factor A a measured mean size gives: A = X50·K^0.8 / (Q^(1/6)·(115/RWS)^(19/30)).</summary>
    public static double RockFactorFrom(double measuredX50, double powderFactor, double chargeKg, double rws) =>
        measuredX50 * Math.Pow(powderFactor, 0.8) / ChargeTerm(chargeKg, rws);

    /// <summary>
    /// The rock factor of the site from measured blasts: A of each blast used (<see cref="RockFactorFrom"/>), their mean and
    /// standard deviation. Count 0 when none can be used.
    /// </summary>
    public static (double Mean, double StdDev, int Count, List<double> Values) Calibrate(IEnumerable<FragCalibration> blasts)
    {
        var a = blasts.Where(b => b.Use && b.MeasuredX50Cm > 0 && b.PowderFactor > 0 && b.ChargeKg > 0 && b.Rws > 0)
            .Select(b => RockFactorFrom(b.MeasuredX50Cm, b.PowderFactor, b.ChargeKg, b.Rws)).ToList();
        if (a.Count == 0) return (0, 0, 0, a);
        var mean = a.Average();
        var sd = a.Count > 1 ? Math.Sqrt(a.Sum(x => (x - mean) * (x - mean)) / (a.Count - 1)) : 0;
        return (mean, sd, a.Count, a);
    }

    /// <summary>
    /// The relative spread of A the band of the chart is drawn with: the spread of the calibration (its standard deviation over
    /// its mean) with two or more blasts, else ±20 %.
    /// </summary>
    public static double Uncertainty(FragmentationModel m)
    {
        var (mean, sd, n, _) = Calibrate(m.Calibration ?? new());
        return n >= 2 && mean > 0 ? Math.Max(0.02, sd / mean) : 0.2;
    }

    /// <summary>Mass of a column of explosive of a density (g/cm³) in a hole of a diameter (mm), kg.</summary>
    public static double ChargeMass(double diameterMm, double length, double densityGcm3) =>
        Math.PI * Math.Pow(diameterMm / 2000, 2) * Math.Max(0, length) * densityGcm3 * 1000;

    /// <summary>
    /// The burden and spacing that give <paramref name="x50"/> when the charge depends on the burden (stemming from B): a fixed
    /// point iteration, B → charge → grid area → B, until B moves less than a centimetre.
    /// </summary>
    /// <param name="charge">The charge per hole and its strength for a burden.</param>
    public static (double Burden, double Spacing, int Iterations) SolveGrid(double x50, double a, double benchHeight, double ratio,
        Func<double, (double ChargeKg, double Rws)> charge, double startBurden)
    {
        var b = Math.Max(0.1, startBurden);
        for (int i = 1; i <= 30; i++)
        {
            var (q, rws) = charge(b);
            if (q <= 0 || rws <= 0) return (b, ratio * b, i);
            var (nb, _) = BurdenSpacing(GridAreaFor(x50, a, q, rws, benchHeight), ratio);
            var done = Math.Abs(nb - b) < 0.01;
            b = nb;
            if (done) return (b, ratio * b, i);
        }
        return (b, ratio * b, 30);
    }
}

/// <summary>How long the bottom charge of a two-explosive column is.</summary>
public enum BottomLengthMode
{
    /// <summary>A length in metres.</summary>
    Metres,
    /// <summary>A share of the charge length.</summary>
    Fraction,
    /// <summary>A share of the charge length within a range, chosen for the lowest cost.</summary>
    FractionRange
}

/// <summary>
/// The charge of a hole from the product catalogue: one explosive, or a bottom charge of one explosive under a column of
/// another (emulsion 2 m at the bottom and ANFO above it). The same pair is a scheme of one explosive.
/// </summary>
public sealed class ChargeScheme
{
    /// <summary>The column explosive, the only one in a scheme of one.</summary>
    public string ColumnProductId { get; set; } = "";
    /// <summary>The bottom explosive; null (or the column one) for a scheme of one explosive.</summary>
    public string? BottomProductId { get; set; }
    public BottomLengthMode BottomMode { get; set; }
    /// <summary>Bottom charge length in metres (<see cref="BottomLengthMode.Metres"/>).</summary>
    public double BottomLength { get; set; } = 2;
    /// <summary>Bottom charge share of the charge length (<see cref="BottomLengthMode.Fraction"/>).</summary>
    public double BottomFraction { get; set; } = 0.3;
    /// <summary>The range of the share (<see cref="BottomLengthMode.FractionRange"/>).</summary>
    public double BottomFractionMin { get; set; } = 0.2;
    public double BottomFractionMax { get; set; } = 0.4;

    public bool IsSingle => string.IsNullOrEmpty(BottomProductId) || BottomProductId == ColumnProductId;

    public ChargeScheme Clone() => (ChargeScheme)MemberwiseClone();

    /// <summary>The scheme of one explosive.</summary>
    public static ChargeScheme Single(string productId) => new() { ColumnProductId = productId };

    /// <summary>Bottom charge length in a charge of <paramref name="chargeLength"/>; a range takes <paramref name="fraction"/> (its middle when null).</summary>
    public double BottomLengthFor(double chargeLength, double? fraction = null)
    {
        if (IsSingle || chargeLength <= 0) return 0;
        var l = BottomMode switch
        {
            BottomLengthMode.Metres => BottomLength,
            BottomLengthMode.Fraction => BottomFraction * chargeLength,
            _ => (fraction ?? (BottomFractionMin + BottomFractionMax) / 2) * chargeLength
        };
        return Math.Clamp(l, 0, chargeLength);
    }

    /// <summary>"Bulk emulsion 2 m + ANFO", or the name of the one explosive.</summary>
    public string Describe(BlastProject p, double? bottomLength = null)
    {
        var column = p.FindProduct(ColumnProductId)?.Name ?? "?";
        if (IsSingle) return column;
        var bottom = p.FindProduct(BottomProductId)?.Name ?? "?";
        var l = bottomLength ?? (BottomMode == BottomLengthMode.Metres ? BottomLength : double.NaN);
        var len = double.IsNaN(l) ? $"{BottomFraction * 100:0} %" : $"{l:0.0#} m";
        return $"{bottom} {len} + {column}";
    }

    /// <summary>
    /// The loading rule of the scheme: a bottom deck of its length (two explosives), the column up to the stemming (length
    /// 0), the stemming. The stemming product, the primers and the alternates come from <paramref name="template"/> (the
    /// rule the holes are loaded by now); each explosive deck takes its primers.
    /// </summary>
    public LoadingRule ToLoadingRule(BlastProject p, double stemming, double bottomLength, LoadingRule? template, string name)
    {
        var rule = new LoadingRule
        {
            Name = name,
            StemmingLength = Math.Max(0, stemming),
            StemmingProductId = template?.StemmingProductId ?? p.ActiveProducts.FirstOrDefault(x => x.Kind == ProductKind.Stemming)?.Id,
            WetAlternateProductId = template?.WetAlternateProductId,
            Priority = template?.Priority ?? 0
        };
        var primers = template?.Decks.Where(d => p.FindProduct(d.ProductId)?.IsExplosive == true).Select(d => d.Primers).FirstOrDefault(x => x.Count > 0)
                      ?? DefaultPrimers(p);
        List<PrimerTemplate> Copy() => primers.Select(x => x.Clone()).ToList();
        if (!IsSingle && bottomLength > 1e-6)
            rule.Decks.Add(new DeckTemplate { ProductId = BottomProductId, Length = Math.Round(bottomLength, 3), Primers = Copy() });
        rule.Decks.Add(new DeckTemplate { ProductId = ColumnProductId, Length = 0, Primers = Copy() });
        return rule;
    }

    private static List<PrimerTemplate> DefaultPrimers(BlastProject p)
    {
        var booster = p.ActiveProducts.FirstOrDefault(x => x.Kind == ProductKind.Booster);
        var det = p.ActiveProducts.FirstOrDefault(x => x.Kind is ProductKind.DownholeDetonator or ProductKind.ElectronicDetonator);
        return new() { new PrimerTemplate { Offset = 0.5, BoosterId = booster?.Id, DetonatorId = det?.Id } };
    }

    /// <summary>
    /// The scheme a rule loads: its two explosives when it has two (the deepest deck is the bottom charge, of its length or a
    /// share of 30 % when it fills the column), else its one.
    /// </summary>
    public static ChargeScheme? FromRule(BlastProject p, LoadingRule rule)
    {
        var expl = rule.Decks.Where(d => p.FindProduct(d.ProductId)?.IsExplosive == true).ToList();
        if (expl.Count == 0) return null;
        var bottom = expl[0];
        var column = expl.FirstOrDefault(d => d.Length <= 0 && d.ProductId != bottom.ProductId) ?? expl.LastOrDefault(d => d.ProductId != bottom.ProductId);
        if (column == null) return Single(bottom.ProductId!);
        return bottom.Length > 0
            ? new ChargeScheme { ColumnProductId = column.ProductId!, BottomProductId = bottom.ProductId, BottomMode = BottomLengthMode.Metres, BottomLength = bottom.Length }
            : new ChargeScheme { ColumnProductId = column.ProductId!, BottomProductId = bottom.ProductId, BottomMode = BottomLengthMode.Fraction, BottomFraction = 0.3 };
    }

    /// <summary>The scheme a loaded hole has: by the same reading as <see cref="FromRule"/>, from its column.</summary>
    public static ChargeScheme? FromHole(BlastProject p, Hole h)
    {
        var expl = h.Decks.Where(d => p.FindProduct(d.ProductId)?.IsExplosive == true).OrderByDescending(d => d.Bottom).ToList();
        if (expl.Count == 0) return null;
        var bottom = expl[0];
        var other = expl.Where(d => d.ProductId != bottom.ProductId).ToList();
        if (other.Count == 0) return Single(bottom.ProductId!);
        var bottomLength = expl.Where(d => d.ProductId == bottom.ProductId).Sum(d => d.Length);
        return new ChargeScheme { ColumnProductId = other.OrderByDescending(d => d.Length).First().ProductId!, BottomProductId = bottom.ProductId, BottomMode = BottomLengthMode.Metres, BottomLength = Math.Round(bottomLength, 2) };
    }
}

/// <summary>An explosive of the catalogue as a choice of the solver: whether it can be used, and why not.</summary>
public sealed record ExplosiveChoice(Product Product, bool Usable, string? Reason);

/// <summary>The limits a variant is checked against: the hard ones reject it, the soft ones mark it.</summary>
public sealed class FragConstraints
{
    /// <summary>Burden in mm over the diameter in mm.</summary>
    public double BurdenPerDiameterMin { get; set; } = 20;
    public double BurdenPerDiameterMax { get; set; } = 40;
    /// <summary>Spacing over burden.</summary>
    public double RatioMin { get; set; } = 1.0;
    public double RatioMax { get; set; } = 1.5;
    /// <summary>Top stemming at least this many hole diameters…</summary>
    public double StemmingDiameters { get; set; } = 25;
    /// <summary>…and at least this share of the burden.</summary>
    public double StemmingPerBurden { get; set; } = 0.7;
    /// <summary>Powder factor, kg/m³.</summary>
    public double PowderFactorMin { get; set; }
    public double PowderFactorMax { get; set; } = 5;
    /// <summary>The flyrock clearance at most, m; null: not checked.</summary>
    public double? MaxFlyrockClearance { get; set; }
    /// <summary>The PPV at the monitoring points at most; null: not checked.</summary>
    public double? MaxPpv { get; set; }
    /// <summary>Soft: the uniformity index at least.</summary>
    public double MinUniformity { get; set; } = 1;
    /// <summary>Soft: the bench stiffness H/B at least.</summary>
    public double MinStiffness { get; set; } = 2;

    public FragConstraints Clone() => (FragConstraints)MemberwiseClone();
}

/// <summary>A limit a variant breaks: the hard ones reject it, <see cref="FragLimits.IsSoft"/> ones only mark it.</summary>
public enum FragLimit
{
    BurdenPerDiameterMin, BurdenPerDiameterMax, RatioMin, RatioMax, StemmingDiameters, StemmingBurden, PowderFactorMin, PowderFactorMax,
    Refusal, Flyrock, Ppv, X50Tolerance, X80, Oversize, Fines, Uniformity, Stiffness
}

public static class FragLimits
{
    public static bool IsSoft(FragLimit l) => l is FragLimit.Uniformity or FragLimit.Stiffness;

    /// <summary>The limit in words (English format, translated by the caller) with its value.</summary>
    public static (string Format, double Value) Describe(FragLimit l, FragConstraints c, FragTarget? t = null) => l switch
    {
        FragLimit.BurdenPerDiameterMin => ("B/d ≥ {0}", c.BurdenPerDiameterMin),
        FragLimit.BurdenPerDiameterMax => ("B/d ≤ {0}", c.BurdenPerDiameterMax),
        FragLimit.RatioMin => ("S/B ≥ {0}", c.RatioMin),
        FragLimit.RatioMax => ("S/B ≤ {0}", c.RatioMax),
        FragLimit.StemmingDiameters => ("Stemming ≥ {0}·d", c.StemmingDiameters),
        FragLimit.StemmingBurden => ("Stemming ≥ {0}·B", c.StemmingPerBurden),
        FragLimit.PowderFactorMin => ("Powder factor ≥ {0} kg/m³", c.PowderFactorMin),
        FragLimit.PowderFactorMax => ("Powder factor ≤ {0} kg/m³", c.PowderFactorMax),
        FragLimit.Refusal => ("The pattern cannot be laid out again", 0),
        FragLimit.Flyrock => ("Flyrock clearance ≤ {0} m", c.MaxFlyrockClearance ?? 0),
        FragLimit.Ppv => ("PPV ≤ {0} mm/s", c.MaxPpv ?? 0),
        FragLimit.X50Tolerance => ("X50 within ±{0} %", t?.TolerancePct ?? 5),
        FragLimit.X80 => ("X80 ≤ {0} cm", t?.X80MaxCm ?? 0),
        FragLimit.Oversize => ("Oversize ≤ {0} %", t?.OversizeMaxPct ?? 0),
        FragLimit.Fines => ("Fines ≤ {0} %", t?.FinesMaxPct ?? 0),
        FragLimit.Uniformity => ("n < {0}", c.MinUniformity),
        _ => ("H/B < {0}", c.MinStiffness)
    };
}

/// <summary>How the top stemming of a variant is set.</summary>
public enum StemmingMode
{
    /// <summary>The shortest the limits allow: the larger of <see cref="FragConstraints.StemmingDiameters"/>·d and <see cref="FragConstraints.StemmingPerBurden"/>·B.</summary>
    Fit,
    /// <summary>A fixed length.</summary>
    Fixed
}

/// <summary>What the solver may change: the grid always, the S/B, the diameters, the charge, the stemming, the subdrill and the azimuth.</summary>
public sealed class FragDesignSpace
{
    /// <summary>The diameters to try, mm; empty: the present one.</summary>
    public List<double> DiametersMm { get; set; } = new();
    /// <summary>The charges to try; empty: the present one.</summary>
    public List<ChargeScheme> Schemes { get; set; } = new();
    /// <summary>S/B free within the ratio range of the <see cref="Constraints"/>; otherwise the present one.</summary>
    public bool RatioFree { get; set; }
    public StemmingMode Stemming { get; set; } = StemmingMode.Fit;
    /// <summary>The stemming of <see cref="StemmingMode.Fixed"/>, m.</summary>
    public double StemmingLength { get; set; } = 3;
    /// <summary>The subdrill, m; null: as in the project.</summary>
    public double? Subdrill { get; set; }
    /// <summary>The window of turns of the grid tried for fewer holes, ± degrees; 0: the azimuth is not changed.</summary>
    public double AzimuthWindowDeg { get; set; }
    /// <summary>Step the burden and spacing are rounded to, m.</summary>
    public double GridStep { get; set; } = 0.1;
    public int MaxCandidates { get; set; } = 10;
    /// <summary>Check each variant on a copy of the project with its calculation profile (else by the formulas only, marked as estimates).</summary>
    public bool RefineByProfile { get; set; } = true;
    /// <summary>How many of the best variants (by the formulas) are checked on the profile; the rest stay estimates.</summary>
    public int RefineTop { get; set; } = int.MaxValue;
    public FragConstraints Constraints { get; set; } = new();
}

/// <summary>A design as the solver tries it and as Apply puts it in the project.</summary>
public sealed class FragDesign
{
    public double Burden { get; set; }
    public double Spacing { get; set; }
    public double DiameterMm { get; set; }
    public ChargeScheme Scheme { get; set; } = new();
    /// <summary>Bottom charge length of a two-explosive scheme, m.</summary>
    public double BottomLength { get; set; }
    public double Stemming { get; set; }
    /// <summary>The subdrill; null: as the holes have it.</summary>
    public double? Subdrill { get; set; }
    /// <summary>Turn of the grid, degrees clockwise.</summary>
    public double AzimuthDelta { get; set; }
    /// <summary>The rock inputs put in the project with the design; null: as they are.</summary>
    public FragmentationModel? Rock { get; set; }
    /// <summary>Keep the rule of the charge in the project's list of rules.</summary>
    public bool SaveRule { get; set; }
    public string RuleName { get; set; } = "";

    public FragDesign Clone()
    {
        var c = (FragDesign)MemberwiseClone();
        c.Scheme = Scheme.Clone();
        return c;
    }
}

/// <summary>What a design gives: the prediction, the holes, the cost and the safety values.</summary>
public sealed record FragMetrics(double X50Cm, double Uniformity, double X80Cm, double OversizePct, double FinesPct, double PowderFactor,
    double ChargeKg, double ChargeLength, int HoleCount, double CostPerM3, double FlyrockClearance, double? Ppv, string? Refusal, bool Estimate);

/// <summary>A variant of the design with its prediction and the limits it breaks.</summary>
public sealed class FragCandidate
{
    public FragDesign Design { get; set; } = new();
    public FragMetrics Metrics { get; set; } = null!;
    public string SchemeName { get; set; } = "";
    /// <summary>Holes now in the area, and after the design.</summary>
    public int HolesBefore { get; set; }
    public int HoleDelta => Metrics.HoleCount - HolesBefore;
    /// <summary>The azimuth of the grid after the design, and the holes before and after its turn.</summary>
    public double Azimuth { get; set; }
    public int HolesBeforeTurn { get; set; }
    public int HolesAfterTurn { get; set; }
    public List<FragLimit> Violations { get; } = new();
    public List<FragLimit> Warnings { get; } = new();
    public bool IsCurrent { get; set; }
    public bool IsValid => Violations.Count == 0;
    public double Ratio => Design.Burden > 0 ? Design.Spacing / Design.Burden : 0;
}

/// <summary>What the solver found: the variants best first, the present design, the reachable X50 and why a target is not reached.</summary>
public sealed class FragSolveResult
{
    public List<FragCandidate> Candidates { get; } = new();
    public FragCandidate? Current { get; set; }
    /// <summary>The X50 the limits allow, cm.</summary>
    public double RangeMinCm { get; set; }
    public double RangeMaxCm { get; set; }
    /// <summary>With no variant: the one nearest to the target and the limit that stops it.</summary>
    public FragCandidate? Nearest { get; set; }
    public FragLimit? Binding { get; set; }
    /// <summary>The project's profile has its own X50 or n formulas: the closed forms only start the search.</summary>
    public bool FormulasChanged { get; set; }
    /// <summary>X50 did not grow with the grid on the profile: the scale was searched on a grid of points.</summary>
    public bool NonMonotone { get; set; }
    public int Evaluations { get; set; }
    public TimeSpan Elapsed { get; set; }
    public bool Cancelled { get; set; }
}

/// <summary>The design as it is now: the area, its holes and patterns, the rule they are loaded by, the averages of the formulas.</summary>
public sealed class FragBase
{
    public BlastProject Project { get; init; } = null!;
    /// <summary>The root patterns of the area.</summary>
    public List<HolePattern> Patterns { get; init; } = new();
    public List<Hole> Holes { get; init; } = new();
    public FragmentationModel Rock { get; init; } = new();
    public double RockFactor { get; init; }
    public double BenchHeight { get; init; }
    public double HoleLength { get; init; }
    public double Subdrill { get; init; }
    public double Angle { get; init; }
    public double DiameterMm { get; init; }
    public double Burden { get; init; }
    public double Spacing { get; init; }
    public double Azimuth { get; init; }
    public bool Wet { get; init; }
    public LoadingRule? Template { get; init; }
    public ChargeScheme? Scheme { get; init; }
    public double Stemming { get; init; }
    public double BottomLength { get; init; }
    /// <summary>
    /// The mean cell of a hole of the area (Voronoi, clipped by the outline) over the grid area B·S: the edges of a real
    /// block make it differ from 1, and the formulas take it so that they agree with the profile.
    /// </summary>
    public double CellFactor { get; init; } = 1;
    public int HoleCount => Holes.Count(h => !h.IsDummy);
    public double Ratio => Burden > 0 ? Spacing / Burden : 1.15;

    /// <summary>
    /// The base of a pattern (its whole tree; null: every pattern of the project) with the rock inputs to use (null: the
    /// project's).
    /// </summary>
    public static FragBase Of(BlastProject p, Guid? patternId, FragmentationModel? rock = null)
    {
        var roots = patternId is Guid id && p.FindPattern(id) is { } pat ? new List<HolePattern> { PatternTree.Root(p, pat) }
            : p.Patterns.Where(x => x.ParentId == null).ToList();
        var holes = roots.Count > 0 ? roots.SelectMany(r => PatternTree.TreeHoles(p, r)).Distinct().ToList() : p.Holes.ToList();
        var real = holes.Where(h => !h.IsDummy).ToList();
        if (real.Count == 0) real = p.Holes.Where(h => !h.IsDummy).ToList();
        rock ??= p.Fragmentation;
        double Avg(Func<Hole, double> f, double none) => real.Count > 0 ? real.Average(f) : none;
        var bench = Avg(h => Math.Max(0.1, h.Length - h.Subdrill) * Math.Cos(h.Angle * GeoMath.Deg), p.Design.BenchHeight);
        var diameter = real.Count > 0 ? real.GroupBy(h => h.Diameter).OrderByDescending(g => g.Count()).First().Key : p.Design.Diameter;
        var main = roots.FirstOrDefault();
        var typical = real.FirstOrDefault(h => !h.ManualLoading && h.Decks.Count > 0) ?? real.FirstOrDefault();
        var template = typical == null ? p.LoadingRules.OrderBy(r => r.Priority).FirstOrDefault()
            : p.LoadingRules.OrderBy(r => r.Priority).FirstOrDefault(r => r.Matches(typical)) ?? p.LoadingRules.OrderBy(r => r.Priority).FirstOrDefault();
        var loaded = real.FirstOrDefault(h => h.Decks.Any(d => p.FindProduct(d.ProductId)?.IsExplosive == true));
        // the rule of the area says the scheme; without one, the column of a loaded hole
        var scheme = template != null ? ChargeScheme.FromRule(p, template) : null;
        scheme ??= loaded != null ? ChargeScheme.FromHole(p, loaded) : null;
        scheme ??= p.ActiveProducts.FirstOrDefault(x => x.IsExplosive) is { } any ? ChargeScheme.Single(any.Id) : null;
        var stemming = template?.StemmingLength ?? (loaded != null ? Charging.TopStemming(p, loaded) : p.Design.StemmingLength);
        var holeLength = Avg(h => h.EffectiveLength, bench + p.Design.Subdrill);
        var bottom = scheme?.BottomLengthFor(Math.Max(0, holeLength - stemming)) ?? 0;
        var burden = main?.Burden ?? p.Design.Burden;
        var spacing = main?.Spacing ?? p.Design.Spacing;
        var cell = 1.0;
        if (real.Count > 0 && burden * spacing > 0)
        {
            var s = CalcService.Session(p);
            CalcService.ConfigureCells(s, null, null);
            var area = real.Average(h => Charging.Calculated(s, "hole.area", h, burden * spacing));
            if (area > 0) cell = area / (burden * spacing);
        }
        return new FragBase
        {
            Project = p, Patterns = roots, Holes = holes, Rock = rock, RockFactor = Fragmentation.RockFactor(rock, p.Design.RockDensity),
            BenchHeight = bench, HoleLength = holeLength, Subdrill = Avg(h => h.Subdrill, p.Design.Subdrill), Angle = Avg(h => h.Angle, 0),
            DiameterMm = diameter, Burden = burden, Spacing = spacing, CellFactor = cell, Azimuth = main?.Azimuth ?? 0,
            Wet = real.Any(h => h.WaterDepth > 0), Template = template, Scheme = scheme, Stemming = stemming, BottomLength = bottom
        };
    }

    /// <summary>The design as it is now.</summary>
    public FragDesign CurrentDesign() => new()
    {
        Burden = Burden, Spacing = Spacing, DiameterMm = DiameterMm, Scheme = Scheme?.Clone() ?? new ChargeScheme(), BottomLength = BottomLength, Stemming = Stemming
    };
}

/// <summary>Works out what a design gives.</summary>
public interface IFragEvaluator
{
    /// <summary>True for the calculation on the profile, false for the formulas.</summary>
    bool Exact { get; }
    FragMetrics Evaluate(FragDesign design);
}

/// <summary>
/// The Kuz-Ram formulas over the averages of the area: microseconds a design, exact for a uniform block with the built-in
/// formulas. The hole count is the present one scaled by the grid area.
/// </summary>
public sealed class FormulaFragEvaluator : IFragEvaluator
{
    private readonly FragBase _b;
    public FormulaFragEvaluator(FragBase b) => _b = b;
    public bool Exact => false;

    /// <summary>The hole length with a subdrill (as the holes have it when null).</summary>
    public double HoleLength(double? subdrill) => _b.HoleLength + (subdrill is double s ? s - _b.Subdrill : 0);

    /// <summary>The charge per hole of a design (with its boosters), its strength and its lengths.</summary>
    public (double ChargeKg, double Rws, double ChargeLength, double Bottom, double Column, double Cost) Charge(double diameterMm, ChargeScheme scheme, double bottomLength, double stemming, double? subdrill)
    {
        var p = _b.Project;
        var len = Math.Max(0.1, HoleLength(subdrill) - stemming);
        var column = p.FindProduct(scheme.ColumnProductId);
        var bottom = scheme.IsSingle ? null : p.FindProduct(scheme.BottomProductId);
        var l1 = bottom == null ? 0 : Math.Clamp(bottomLength, 0, len);
        var l2 = len - l1;
        double Mass(Product? x, double l) => x == null ? 0 : x.Kind == ProductKind.PackagedExplosive && x.UnitLength > 0 && x.UnitMass > 0
            ? Math.Floor(l / x.UnitLength + 1e-9) * x.UnitMass : KuzRamInverse.ChargeMass(diameterMm, l, x.Density);
        var m1 = Mass(bottom, l1);
        var m2 = Mass(column, l2);
        var boosters = (_b.Template?.Decks.SelectMany(d => d.Primers) ?? Enumerable.Empty<PrimerTemplate>()).Sum(x => p.FindProduct(x.BoosterId)?.UnitMass ?? 0);
        var rws = len > 0 ? ((bottom?.RelativeWeightStrength ?? 0) * l1 + (column?.RelativeWeightStrength ?? 100) * l2) / len : 100;
        var stem = p.FindProduct(_b.Template?.StemmingProductId);
        var cost = m1 * (bottom?.Cost ?? 0) + m2 * (column?.Cost ?? 0) + KuzRamInverse.ChargeMass(diameterMm, stemming, stem?.Density ?? 0) * (stem?.Cost ?? 0);
        return (m1 + m2 + boosters, rws, len, bottom == null ? len : l1, bottom == null ? 0 : l2, cost);
    }

    public FragMetrics Evaluate(FragDesign d)
    {
        var p = _b.Project;
        var m = _b.Rock;
        var h = _b.BenchHeight;
        var (q, rws, len, l1, l2, productCost) = Charge(d.DiameterMm, d.Scheme, d.BottomLength, d.Stemming, d.Subdrill);
        var pf = q / Math.Max(1e-9, _b.CellFactor * d.Burden * d.Spacing * h);
        var x50 = Fragmentation.X50(_b.RockFactor, pf, q, rws);
        var n = Fragmentation.Uniformity(d.Burden, d.Spacing, d.DiameterMm, m.DrillDeviation, len, h, l1, l2);
        var holes = (int)Math.Round(_b.HoleCount * _b.Burden * _b.Spacing / Math.Max(1e-9, d.Burden * d.Spacing));
        var holeLength = HoleLength(d.Subdrill);
        var costPerHole = productCost + holeLength * p.Costs.DrillingPerMetre + p.Costs.LoadingPerHole;
        var column = p.FindProduct(d.Scheme.ColumnProductId);
        var bottom = d.Scheme.IsSingle ? null : p.FindProduct(d.Scheme.BottomProductId);
        var kgPerM = Math.Max(KuzRamInverse.ChargeMass(d.DiameterMm, 1, column?.Density ?? 0), KuzRamInverse.ChargeMass(d.DiameterMm, 1, bottom?.Density ?? 0));
        var k = p.Flyrock.SiteK;
        var throwMax = Math.Max(Flyrock.FaceBurst(k, kgPerM, d.Burden), Flyrock.Cratering(k, kgPerM, d.Stemming));
        var clearance = Math.Max(p.Flyrock.MinimumClearance, throwMax * p.Flyrock.SafetyFactor);
        return new FragMetrics(x50, n, Fragmentation.SizeAt(0.8, x50, n), 100 * (1 - Fragmentation.RosinRammler(m.OversizeCm, x50, n)),
            100 * Fragmentation.RosinRammler(m.FinesCm, x50, n), pf, q, len, holes, costPerHole / Math.Max(1e-9, _b.CellFactor * d.Burden * d.Spacing * h), clearance, null, null, true);
    }
}

/// <summary>
/// The calculation profile on a copy of the project with the design put in it (<see cref="FragApply.Apply"/>): exact for the
/// real block (the outline, the heights, the rules, the profile's own formulas), a copy and a calculation a design.
/// </summary>
public sealed class ProfileFragEvaluator : IFragEvaluator
{
    private readonly FragBase _b;
    private readonly byte[] _snapshot;
    public ProfileFragEvaluator(FragBase b)
    {
        _b = b;
        // one serialisation of the source; each design is read from it, the source is never touched
        _snapshot = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(b.Project, ProjectJson.Options);
    }
    public bool Exact => true;
    private int _count;
    public int Count => _count;

    public BlastProject Copy() => System.Text.Json.JsonSerializer.Deserialize<BlastProject>(_snapshot, ProjectJson.Options)!;

    public FragMetrics Evaluate(FragDesign d)
    {
        Interlocked.Increment(ref _count);
        var q = Copy();
        var design = d.Clone();
        design.Rock ??= _b.Rock;
        var report = FragApply.Apply(q, _b.Patterns.Select(x => x.Id).ToList(), design, _b.Template);
        if (report.Refusal is { } no) return new FragMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, no, false);
        return Measure(q, FragApply.AreaHoles(q, _b.Patterns.Select(x => x.Id).ToList()));
    }

    /// <summary>The prediction, cost and safety values of a project as it is.</summary>
    public static FragMetrics Measure(BlastProject q, IReadOnlyCollection<Hole> area)
    {
        var r = Fragmentation.Compute(q);
        var s = CalcService.Session(q);
        double V(string id, object o) => Charging.Calculated(s, id, o, 0);
        var volume = V("blast.volume", q);
        var cost = volume > 0 ? (V("blast.productCost", q) + V("blast.drillingCost", q) + V("blast.loadingCost", q)) / volume : 0;
        var monitors = q.Entities.OfType<PointEntity>().Where(x => x.Kind == PointKind.Monitoring).ToList();
        // by the largest charge in a delay window, the largest charge of a hole without the timing (as the exclusion zone)
        var charge = V("blast.micOrMaxCharge", q);
        double? ppv = monitors.Count > 0 ? monitors.Max(x => V("monitor.distance", x) is var dist and > 0 ? Vibration.Ppv(q.Vibration, dist, charge) : 0) : null;
        var len = V("blast.avgChargeLength", q);
        return new FragMetrics(r.X50Cm, r.Uniformity, r.X80Cm, r.OversizePct, r.FinesPct, r.PowderFactor, r.ChargePerHole, len,
            area.Count(h => !h.IsDummy), cost, V("blast.flyrockClearance", q), ppv, null, false);
    }
}

/// <summary>A design put in a project: the rock inputs, the diameter, the subdrill, the turn and the size of the grid, the charge.</summary>
public static class FragApply
{
    public sealed class Report
    {
        public string? Refusal { get; set; }
        public int Moved { get; set; }
        public int Deleted { get; set; }
        public int Added { get; set; }
        public List<Guid> DeletedIds { get; } = new();
        public int Loaded { get; set; }
        public LoadingRule? Rule { get; set; }
    }

    /// <summary>The holes of the trees of the patterns (all holes without patterns).</summary>
    public static List<Hole> AreaHoles(BlastProject p, IReadOnlyList<Guid> rootIds)
    {
        var roots = rootIds.Select(id => p.FindPattern(id)).OfType<HolePattern>().ToList();
        return roots.Count > 0 ? roots.SelectMany(r => PatternTree.TreeHoles(p, r)).Distinct().ToList() : p.Holes.ToList();
    }

    /// <summary>Why the patterns cannot be laid out again; null when they can.</summary>
    public static PatternRefusal? Refusal(BlastProject p, IReadOnlyList<Guid> rootIds)
    {
        var o = PatternRebuildOptions.For(p);
        foreach (var r in rootIds.Select(id => p.FindPattern(id)).OfType<HolePattern>())
            if ((r.IsContainer ? PatternTree.TreeRefusal(p, r, o) : HoleDesign.RebuildRefusal(p, r, o)) is { } no) return no;
        return null;
    }

    /// <summary>
    /// Puts a design in a project, in one go (the caller makes it one undo step): the rock inputs, the diameter and the
    /// subdrill of the holes and of the new holes of the patterns, the turn and the new burden and spacing of each pattern
    /// (a pattern cut into segments with its tree), then the holes of the area loaded by the rule of the charge (holes
    /// loaded by hand keep their columns). Holes outside the area are not moved.
    /// </summary>
    public static Report Apply(BlastProject p, IReadOnlyList<Guid> rootIds, FragDesign d, LoadingRule? template)
    {
        var report = new Report();
        if (Refusal(p, rootIds) is { } no) { report.Refusal = string.Format(no.Reason, no.HoleLabel); return report; }
        if (d.Rock != null) p.Fragmentation = ProjectJson.DeepClone(d.Rock);
        var roots = rootIds.Select(id => p.FindPattern(id)).OfType<HolePattern>().ToList();
        foreach (var r in roots)
        {
            foreach (var x in PatternTree.Descendants(p, r).Prepend(r))
            {
                x.HoleDefaults.Diameter = d.DiameterMm;
                if (d.Subdrill is double sd) x.HoleDefaults.Subdrill = sd;
            }
        }
        foreach (var h in AreaHoles(p, rootIds)) SetHole(h, d);
        foreach (var r in roots)
        {
            var o = PatternRebuildOptions.For(p);
            PatternRebuildReport? turn = null;
            if (Math.Abs(d.AzimuthDelta) > 1e-9)
                turn = r.IsContainer ? PatternTree.Rotate(p, r, d.AzimuthDelta, o) : HoleDesign.RotatePattern(p, r, d.AzimuthDelta, o);
            var o2 = PatternRebuildOptions.For(p);
            var size = r.IsContainer ? PatternTree.Resize(p, r, d.Burden, d.Spacing, o2) : HoleDesign.ResizePattern(p, r, d.Burden, d.Spacing, o2);
            foreach (var rep in new[] { turn, size })
            {
                if (rep == null) continue;
                if (rep.Refusal is { } rf) { report.Refusal = string.Format(rf.Reason, rf.HoleLabel); return report; }
                report.Moved += rep.Moved;
                report.Deleted += rep.Deleted;
                report.Added += rep.Added;
                report.DeletedIds.AddRange(rep.DeletedIds);
            }
        }
        // the new holes take the diameter of the pattern; the subdrill as the others
        var area = AreaHoles(p, rootIds);
        foreach (var h in area) SetHole(h, d);
        var rule = d.Scheme.ToLoadingRule(p, d.Stemming, d.BottomLength, template, string.IsNullOrEmpty(d.RuleName) ? "X50" : d.RuleName);
        report.Rule = rule;
        foreach (var h in area.Where(h => !h.IsDummy && !h.ManualLoading))
        {
            Charging.ApplyRule(p, h, rule);
            report.Loaded++;
        }
        if (d.SaveRule)
        {
            rule.Priority = p.LoadingRules.Count > 0 ? p.LoadingRules.Max(x => x.Priority) + 1 : 0;
            p.LoadingRules.RemoveAll(x => x.Name == rule.Name);
            p.LoadingRules.Add(rule);
        }
        return report;
    }

    private static void SetHole(Hole h, FragDesign d)
    {
        if (h.Locked) return;
        h.Diameter = d.DiameterMm;
        if (d.Subdrill is double sd && Math.Abs(sd - h.Subdrill) > 1e-9)
        {
            var delta = sd - h.Subdrill;
            h.Subdrill = sd;
            if (h.ToLevel is double z) h.ToLevel = z - delta * Math.Cos(h.Angle * GeoMath.Deg);
            h.Length += delta;
        }
    }
}

/// <summary>
/// Finds the grids (and, when allowed, the diameters, charges, S/B and azimuth) that give the target X50. Two floors: the
/// closed forms give the grid of each combination at once, then a few calculations on copies of the project bring it to the
/// target by the real profile. The source project is never changed.
/// </summary>
public static class FragTargetSolver
{
    /// <summary>The explosives of the catalogue the solver may use: explosive, not deleted, with a density and an RWS; a wet bottom charge only water resistant.</summary>
    public static List<Product> ExplosiveCandidates(BlastProject p, bool wet) =>
        ExplosiveChoices(p, wet).Where(c => c.Usable).Select(c => c.Product).ToList();

    /// <summary>The explosives of the catalogue with whether each can be used and why not (English, translated by the caller).</summary>
    public static List<ExplosiveChoice> ExplosiveChoices(BlastProject p, bool wet) =>
        p.Products.Where(x => x.IsExplosive && !x.Deleted).Select(x =>
        {
            string? why = x.RelativeWeightStrength <= 0 ? "no RWS" : x.Density <= 0 ? "no density" : wet && !x.WaterResistant ? "not water resistant" : null;
            return new ExplosiveChoice(x, why == null, why ?? (x.Cost <= 0 ? "no cost" : null));
        }).ToList();

    /// <summary>The diameters of the holes of the project, smallest first.</summary>
    public static List<double> ProjectDiameters(BlastProject p) =>
        p.Holes.Where(h => !h.IsDummy).Select(h => Math.Round(h.Diameter, 1)).Distinct().Order().ToList();

    /// <summary>Whether the project's profile computes X50 or n by formulas of its own.</summary>
    public static bool FormulasChanged(BlastProject p)
    {
        var mine = CalcService.ModelFor(p);
        if (ReferenceEquals(mine, CalcService.BuiltInModel)) return false;
        string Text(CalcModel m, string id) => string.Join("|", m.NodeOf(id)?.Formulas.Select(f => f.Def.Expression + "?" + f.Def.Condition) ?? Enumerable.Empty<string>());
        return new[] { "blast.x50", "blast.uniformity", "blast.powderFactor", "blast.avgCharge" }.Any(id => Text(mine, id) != Text(CalcService.BuiltInModel, id));
    }

    public static Task<FragSolveResult> SolveAsync(BlastProject project, Guid? patternId, FragmentationModel? rock, FragTarget target, FragDesignSpace space,
        IProgress<double>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Solve(FragBase.Of(project, patternId, rock), target, space, progress, ct), ct);

    /// <summary>The stemming of a design: fixed, or the shortest the limits allow, rounded up to the centimetre.</summary>
    public static double StemmingFor(FragDesignSpace space, double burden, double diameterMm)
    {
        if (space.Stemming == StemmingMode.Fixed) return space.StemmingLength;
        var c = space.Constraints;
        return Math.Ceiling(Math.Max(c.StemmingDiameters * diameterMm / 1000, c.StemmingPerBurden * burden) * 100 - 1e-6) / 100;
    }

    private sealed record Combo(double DiameterMm, ChargeScheme Scheme);

    public static FragSolveResult Solve(FragBase b, FragTarget target, FragDesignSpace space, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var result = new FragSolveResult { FormulasChanged = FormulasChanged(b.Project) };
        var fast = new FormulaFragEvaluator(b);
        var exact = space.RefineByProfile ? new ProfileFragEvaluator(b) : null;
        var c = space.Constraints;

        // the design as it is
        var current = new FragCandidate
        {
            Design = b.CurrentDesign(), IsCurrent = true, HolesBefore = b.HoleCount, Azimuth = b.Azimuth, HolesBeforeTurn = b.HoleCount, HolesAfterTurn = b.HoleCount
        };
        var currentCopy = exact?.Copy();
        if (currentCopy != null) currentCopy.Fragmentation = ProjectJson.DeepClone(b.Rock);
        current.Metrics = currentCopy != null ? ProfileFragEvaluator.Measure(currentCopy, FragApply.AreaHoles(currentCopy, b.Patterns.Select(x => x.Id).ToList())) : fast.Evaluate(current.Design);
        current.SchemeName = b.Scheme?.Describe(b.Project, b.BottomLength) ?? "";
        result.Current = current;
        if (b.Scheme == null) { result.Elapsed = watch.Elapsed; return result; }

        var diameters = space.DiametersMm.Count > 0 ? space.DiametersMm.Distinct().ToList() : new List<double> { b.DiameterMm };
        var schemes = space.Schemes.Count > 0 ? space.Schemes : new List<ChargeScheme> { b.Scheme };
        var combos = diameters.SelectMany(d => schemes.Select(s => new Combo(d, s))).ToList();

        // the reachable range and its ends, by the formulas
        double rangeMin = double.PositiveInfinity, rangeMax = 0;
        (Combo C, double B, FragLimit Limit)? lowEnd = null, highEnd = null;
        foreach (var combo in combos)
        {
            var (lo, loLimit, hi, hiLimit) = BurdenRange(b, fast, space, combo);
            if (hi < lo) continue;
            var ratio = space.RatioFree ? (c.RatioMin + c.RatioMax) / 2 : b.Ratio;
            double X50At(double burden) => fast.Evaluate(DesignOf(b, space, combo, burden, ratio * burden, Fraction(combo.Scheme), fast)).X50Cm;
            var x0 = X50At(lo);
            var x1 = X50At(hi);
            if (x0 < rangeMin) { rangeMin = x0; lowEnd = (combo, lo, loLimit); }
            if (x1 > rangeMax) { rangeMax = x1; highEnd = (combo, hi, hiLimit); }
        }
        result.RangeMinCm = double.IsInfinity(rangeMin) ? 0 : rangeMin;
        result.RangeMaxCm = rangeMax;

        var found = new List<FragCandidate>();
        int done = 0;
        var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Math.Min(combos.Count, Environment.ProcessorCount - 1)) };
        try
        {
            Parallel.ForEach(combos, parallel, combo =>
            {
                var cand = SolveCombo(b, target, space, combo, fast, exact, result, ct);
                lock (found)
                {
                    if (cand != null) found.Add(cand);
                    progress?.Report(++done / (double)combos.Count);
                }
            });
        }
        catch (OperationCanceledException) { result.Cancelled = true; }

        foreach (var cand in found) Check(cand, b, target, space);
        var valid = found.Where(x => x.IsValid).ToList();
        // by cost a cubic metre; at the same cost (within half a percent) the more uniform first
        valid.Sort((x, y) =>
        {
            var dc = x.Metrics.CostPerM3 - y.Metrics.CostPerM3;
            if (Math.Abs(dc) > 0.005 * Math.Max(1e-9, Math.Min(Math.Abs(x.Metrics.CostPerM3), Math.Abs(y.Metrics.CostPerM3)))) return dc < 0 ? -1 : 1;
            return y.Metrics.Uniformity.CompareTo(x.Metrics.Uniformity);
        });
        result.Candidates.AddRange(valid.Take(space.MaxCandidates));

        if (result.Candidates.Count == 0 && !result.Cancelled)
        {
            // nothing reaches the target: the variant at the end of the range nearer to it, and what stops it
            var below = target.X50Cm < result.RangeMinCm;
            var end = below ? lowEnd : target.X50Cm > result.RangeMaxCm ? highEnd : null;
            if (end is { } e)
            {
                var ratio = space.RatioFree ? (c.RatioMin + c.RatioMax) / 2 : b.Ratio;
                var design = DesignOf(b, space, e.C, e.B, ratio * e.B, Fraction(e.C.Scheme), fast);
                var near = Finish(b, design, fast, exact, space);
                Check(near, b, target, space);
                result.Nearest = near;
                result.Binding = e.Limit;
            }
            else
            {
                // in the range but every variant breaks a limit: the nearest to the target and its first limit
                var near = found.OrderBy(x => Math.Abs(x.Metrics.X50Cm / target.X50Cm - 1)).FirstOrDefault();
                result.Nearest = near;
                result.Binding = near?.Violations.FirstOrDefault(v => v != FragLimit.X50Tolerance) is FragLimit v0 && near.Violations.Contains(v0) ? v0 : near?.Violations.FirstOrDefault();
            }
        }
        result.Evaluations = exact?.Count ?? 0;
        result.Elapsed = watch.Elapsed;
        return result;
    }

    private static double? Fraction(ChargeScheme s) => s.BottomMode == BottomLengthMode.FractionRange ? (s.BottomFractionMin + s.BottomFractionMax) / 2 : null;

    /// <summary>A design of a combination at a burden and spacing: the stemming of the space, the bottom charge of the scheme.</summary>
    private static FragDesign DesignOf(FragBase b, FragDesignSpace space, Combo combo, double burden, double spacing, double? fraction, FormulaFragEvaluator fast)
    {
        var stem = StemmingFor(space, burden, combo.DiameterMm);
        var len = Math.Max(0.1, fast.HoleLength(space.Subdrill) - stem);
        return new FragDesign
        {
            Burden = burden, Spacing = spacing, DiameterMm = combo.DiameterMm, Scheme = combo.Scheme.Clone(), Stemming = stem, Subdrill = space.Subdrill,
            BottomLength = combo.Scheme.BottomLengthFor(len, fraction), Rock = b.Rock
        };
    }

    /// <summary>
    /// The burdens a combination may take and the limit at each end: B/d, the powder factor and, with a fixed stemming, the
    /// stemming per burden.
    /// </summary>
    private static (double Lo, FragLimit LoLimit, double Hi, FragLimit HiLimit) BurdenRange(FragBase b, FormulaFragEvaluator fast, FragDesignSpace space, Combo combo)
    {
        var c = space.Constraints;
        var ratio = space.RatioFree ? (c.RatioMin + c.RatioMax) / 2 : b.Ratio;
        double lo = c.BurdenPerDiameterMin * combo.DiameterMm / 1000, hi = c.BurdenPerDiameterMax * combo.DiameterMm / 1000;
        FragLimit loL = FragLimit.BurdenPerDiameterMin, hiL = FragLimit.BurdenPerDiameterMax;
        // powder factor = Q / (m·B²·H): its upper limit is a smallest burden, its lower limit a largest
        double Pf(double burden) => fast.Evaluate(DesignOf(b, space, combo, burden, ratio * burden, Fraction(combo.Scheme), fast)).PowderFactor;
        if (Pf(lo) > c.PowderFactorMax)
        {
            var x = Bisect(lo, hi * 3, burden => Pf(burden) - c.PowderFactorMax);
            if (x > lo) { lo = x; loL = FragLimit.PowderFactorMax; }
        }
        if (c.PowderFactorMin > 0 && Pf(hi) < c.PowderFactorMin)
        {
            var x = Bisect(lo / 3, hi, burden => Pf(burden) - c.PowderFactorMin);
            if (x < hi) { hi = x; hiL = FragLimit.PowderFactorMin; }
        }
        if (space.Stemming == StemmingMode.Fixed && c.StemmingPerBurden > 0 && space.StemmingLength / c.StemmingPerBurden < hi)
        {
            hi = space.StemmingLength / c.StemmingPerBurden;
            hiL = FragLimit.StemmingBurden;
        }
        return (lo, loL, hi, hiL);
    }

    /// <summary>Root of a function that changes sign once on [a, b] (decreasing or increasing), by halving.</summary>
    private static double Bisect(double a, double b, Func<double, double> f)
    {
        double fa = f(a);
        for (int i = 0; i < 60; i++)
        {
            var m = (a + b) / 2;
            var fm = f(m);
            if (Math.Sign(fm) == Math.Sign(fa)) { a = m; fa = fm; } else b = m;
        }
        return (a + b) / 2;
    }

    /// <summary>Golden-section search for the maximum of a function on [a, b].</summary>
    public static double GoldenMax(double a, double b, Func<double, double> f, double tol = 1e-3)
    {
        const double g = 0.6180339887498949;
        double x1 = b - g * (b - a), x2 = a + g * (b - a), f1 = f(x1), f2 = f(x2);
        while (b - a > tol)
        {
            if (f1 < f2) { a = x1; x1 = x2; f1 = f2; x2 = a + g * (b - a); f2 = f(x2); }
            else { b = x2; x2 = x1; f2 = f1; x1 = b - g * (b - a); f1 = f(x1); }
        }
        return (a + b) / 2;
    }

    /// <summary>The grid of a combination by the closed forms at a ratio (the stemming follows the burden).</summary>
    private static (double B, double S, double? Fraction) Closed(FragBase b, FragTarget target, FragDesignSpace space, Combo combo, double ratio, double? fraction, FormulaFragEvaluator fast)
    {
        var (B, S, _) = KuzRamInverse.SolveGrid(target.X50Cm, b.RockFactor, b.BenchHeight * b.CellFactor, ratio, burden =>
        {
            var stem = StemmingFor(space, burden, combo.DiameterMm);
            var len = Math.Max(0.1, fast.HoleLength(space.Subdrill) - stem);
            var (q, rws, _, _, _, _) = fast.Charge(combo.DiameterMm, combo.Scheme, combo.Scheme.BottomLengthFor(len, fraction), stem, space.Subdrill);
            return (q, rws);
        }, b.Burden);
        return (B, S, fraction);
    }

    private static FragCandidate? SolveCombo(FragBase b, FragTarget target, FragDesignSpace space, Combo combo, FormulaFragEvaluator fast, ProfileFragEvaluator? exact, FragSolveResult result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var c = space.Constraints;
        // a range of the bottom share: the cheapest a cubic metre at the target, by the formulas
        double? fraction = Fraction(combo.Scheme);
        if (combo.Scheme.BottomMode == BottomLengthMode.FractionRange && !combo.Scheme.IsSingle)
            fraction = GoldenMax(combo.Scheme.BottomFractionMin, combo.Scheme.BottomFractionMax, f =>
            {
                var (B, S, _) = Closed(b, target, space, combo, b.Ratio, f, fast);
                return -fast.Evaluate(DesignOf(b, space, combo, B, S, f, fast)).CostPerM3;
            }, 0.01);
        // a free S/B: the most uniform at the target; where n is cut flat, nearer 1.15
        var ratio = b.Ratio;
        if (space.RatioFree)
            ratio = GoldenMax(c.RatioMin, c.RatioMax, m =>
            {
                var (B, S, _) = Closed(b, target, space, combo, m, fraction, fast);
                return fast.Evaluate(DesignOf(b, space, combo, B, S, fraction, fast)).Uniformity - 1e-4 * Math.Abs(m - 1.15);
            }, 0.005);
        var (b0, s0, _) = Closed(b, target, space, combo, ratio, fraction, fast);
        // a grid far outside the burdens of the combination does not reach the target: not laid out on the profile (a grid
        // too dense would make thousands of holes), only its end of the range by the formulas
        var (lo, _, hi, _) = BurdenRange(b, fast, space, combo);
        if (b0 < lo * 0.9 || b0 > hi * 1.1)
        {
            var end = Math.Clamp(b0, lo, hi);
            return Finish(b, DesignOf(b, space, combo, end, ratio * end, fraction, fast), fast, null, space);
        }
        var design = DesignOf(b, space, combo, b0, s0, fraction, fast);
        if (exact != null)
        {
            ct.ThrowIfCancellationRequested();
            design = Refine(b, target, space, combo, design, fraction, fast, exact, result, ct);
        }
        design = Round(b, target, space, combo, design, fraction, fast, exact, ct);
        var cand = Finish(b, design, fast, exact, space);
        // the azimuth: fewer holes, then the X50 checked again
        if (space.AzimuthWindowDeg > 0 && b.Patterns.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (delta, before, after) = BestTurn(b, design, space.AzimuthWindowDeg);
            cand.HolesBeforeTurn = before;
            cand.HolesAfterTurn = after;
            if (Math.Abs(delta) > 1e-9)
            {
                design.AzimuthDelta = delta;
                cand = Finish(b, design, fast, exact, space);
                cand.HolesBeforeTurn = before;
                cand.HolesAfterTurn = after;
                if (exact != null && Math.Abs(cand.Metrics.X50Cm / target.X50Cm - 1) * 100 > target.TolerancePct)
                {
                    // one more step of the scale on the turned grid, rounded again
                    var k = Math.Pow(target.X50Cm / Math.Max(1e-9, cand.Metrics.X50Cm), 1 / 1.6);
                    var d2 = DesignOf(b, space, combo, design.Burden * k, design.Spacing * k, fraction, fast);
                    d2.AzimuthDelta = delta;
                    d2 = Round(b, target, space, combo, d2, fraction, fast, exact, ct);
                    d2.AzimuthDelta = delta;
                    cand = Finish(b, d2, fast, exact, space);
                    cand.HolesBeforeTurn = before;
                    cand.HolesAfterTurn = after;
                }
            }
        }
        return cand;
    }

    /// <summary>
    /// Brings the closed-form grid to the target by the profile: the scale k of the grid solved for X50(k) = target. X50 grows as
    /// k^1.6 by the formulas, so the steps are secant steps on ln X50 against ln k, kept inside the bracket once the target is
    /// bracketed (regula falsi, Illinois); a profile whose X50 does not grow with k is searched on 20 points of k instead.
    /// </summary>
    private static FragDesign Refine(FragBase b, FragTarget target, FragDesignSpace space, Combo combo, FragDesign start, double? fraction,
        FormulaFragEvaluator fast, ProfileFragEvaluator exact, FragSolveResult result, CancellationToken ct)
    {
        var tol = target.TolerancePct / 400;
        FragDesign At(double k) => DesignOf(b, space, combo, start.Burden * k, start.Spacing * k, fraction, fast);
        double Y(double k) { ct.ThrowIfCancellationRequested(); var m = exact.Evaluate(At(k)); return m.Refusal != null || m.X50Cm <= 0 ? double.NaN : Math.Log(m.X50Cm / target.X50Cm); }
        double x0 = 0, y0 = Y(1);
        if (double.IsNaN(y0)) return start;
        if (Math.Abs(y0) <= tol) return start;
        double x1 = x0 - y0 / 1.6, y1 = Y(Math.Exp(x1));
        if (double.IsNaN(y1)) return start;
        double best = Math.Abs(y0) < Math.Abs(y1) ? x0 : x1, bestY = Math.Min(Math.Abs(y0), Math.Abs(y1));
        for (int i = 0; i < 6 && bestY > tol; i++)
        {
            var slope = (y1 - y0) / (x1 - x0);
            if (!(slope > 0.05))
            {
                // not growing: points of k instead
                lock (result) result.NonMonotone = true;
                var grid = Enumerable.Range(0, 20).Select(j => Math.Exp(best) * (0.7 + 0.6 * j / 19)).ToList();
                var bk = grid.Select(k => (k, y: Math.Abs(Y(k)))).Where(t => !double.IsNaN(t.y)).OrderBy(t => t.y).FirstOrDefault();
                return bk.k > 0 ? At(bk.k) : At(Math.Exp(best));
            }
            double x2;
            if (Math.Sign(y0) != Math.Sign(y1)) x2 = x1 - y1 * (x1 - x0) / (y1 - y0);
            else x2 = x1 - y1 / slope;
            var y2 = Y(Math.Exp(x2));
            if (double.IsNaN(y2)) break;
            if (Math.Abs(y2) < bestY) { best = x2; bestY = Math.Abs(y2); }
            // keep the bracket when there is one
            if (Math.Sign(y2) != Math.Sign(y1)) { x0 = x1; y0 = y1; }
            else if (Math.Sign(y0) != Math.Sign(y1)) y0 /= 2;
            else { x0 = x1; y0 = y1; }
            x1 = x2; y1 = y2;
        }
        return At(Math.Exp(best));
    }

    /// <summary>
    /// The burden and spacing rounded to the step: of the four nodes around the grid, the two nearest to the target by the
    /// formulas (scaled to the profile at the unrounded grid) are calculated and the nearer one is taken.
    /// </summary>
    private static FragDesign Round(FragBase b, FragTarget target, FragDesignSpace space, Combo combo, FragDesign d, double? fraction, FormulaFragEvaluator fast, ProfileFragEvaluator? exact, CancellationToken ct)
    {
        var step = space.GridStep > 0 ? space.GridStep : 0.1;
        double Fl(double v) => Math.Max(step, Math.Floor(v / step + 1e-9) * step);
        double Ce(double v) => Math.Max(step, Math.Ceiling(v / step - 1e-9) * step);
        var nodes = new[] { (Fl(d.Burden), Fl(d.Spacing)), (Fl(d.Burden), Ce(d.Spacing)), (Ce(d.Burden), Fl(d.Spacing)), (Ce(d.Burden), Ce(d.Spacing)) }
            .Distinct().Select(n => (B: Math.Round(n.Item1, 6), S: Math.Round(n.Item2, 6))).ToList();
        var fastHere = fast.Evaluate(d).X50Cm;
        var designs = nodes.Select(n =>
        {
            var x = DesignOf(b, space, combo, n.B, n.S, fraction, fast);
            x.AzimuthDelta = d.AzimuthDelta;
            return (Design: x, Guess: fast.Evaluate(x).X50Cm / Math.Max(1e-9, fastHere));
        }).ToList();
        // the ratio within its limits when it can be
        var c = space.Constraints;
        var inRatio = designs.Where(x => x.Design.Spacing / x.Design.Burden >= c.RatioMin - 1e-9 && x.Design.Spacing / x.Design.Burden <= c.RatioMax + 1e-9).ToList();
        if (inRatio.Count > 0 && !space.RatioFree && Math.Abs(b.Ratio - Math.Clamp(b.Ratio, c.RatioMin, c.RatioMax)) < 1e-9) designs = inRatio;
        else if (inRatio.Count > 0 && space.RatioFree) designs = inRatio;
        // the scale of the profile against the formulas is the same for the neighbours: the target as a share of the present X50
        var want = exact == null ? target.X50Cm / Math.Max(1e-9, fastHere) : 1.0;
        var ranked = designs.OrderBy(x => Math.Abs(x.Guess - want)).Take(exact == null ? 1 : 2).ToList();
        if (exact == null || ranked.Count == 1) return ranked[0].Design;
        ct.ThrowIfCancellationRequested();
        return ranked.Select(x => (x.Design, M: exact.Evaluate(x.Design))).Where(x => x.M.Refusal == null && x.M.X50Cm > 0)
            .OrderBy(x => Math.Abs(x.M.X50Cm / target.X50Cm - 1)).Select(x => x.Design).DefaultIfEmpty(ranked[0].Design).First();
    }

    /// <summary>The candidate of a design: calculated on the profile when there is one, else by the formulas (an estimate).</summary>
    private static FragCandidate Finish(FragBase b, FragDesign d, FormulaFragEvaluator fast, ProfileFragEvaluator? exact, FragDesignSpace space)
    {
        var m = exact != null ? exact.Evaluate(d) : fast.Evaluate(d);
        return new FragCandidate
        {
            Design = d, Metrics = m, HolesBefore = b.HoleCount, SchemeName = d.Scheme.Describe(b.Project, d.BottomLength),
            Azimuth = GeoMath.NormalizeBearing(b.Azimuth + d.AzimuthDelta), HolesBeforeTurn = m.HoleCount, HolesAfterTurn = m.HoleCount
        };
    }

    /// <summary>The limits a candidate breaks: the hard ones as violations, the soft ones as warnings.</summary>
    public static void Check(FragCandidate x, FragBase b, FragTarget target, FragDesignSpace space)
    {
        var c = space.Constraints;
        var d = x.Design;
        var m = x.Metrics;
        void V(bool broken, FragLimit l) { if (broken && !x.Violations.Contains(l)) x.Violations.Add(l); }
        void W(bool broken, FragLimit l) { if (broken && !x.Warnings.Contains(l)) x.Warnings.Add(l); }
        x.Violations.Clear();
        x.Warnings.Clear();
        if (m.Refusal != null) { V(true, FragLimit.Refusal); return; }
        var bd = d.Burden * 1000 / Math.Max(1e-9, d.DiameterMm);
        V(bd < c.BurdenPerDiameterMin - 1e-6, FragLimit.BurdenPerDiameterMin);
        V(bd > c.BurdenPerDiameterMax + 1e-6, FragLimit.BurdenPerDiameterMax);
        V(x.Ratio < c.RatioMin - 1e-6, FragLimit.RatioMin);
        V(x.Ratio > c.RatioMax + 1e-6, FragLimit.RatioMax);
        V(d.Stemming < c.StemmingDiameters * d.DiameterMm / 1000 - 1e-6, FragLimit.StemmingDiameters);
        V(d.Stemming < c.StemmingPerBurden * d.Burden - 1e-6, FragLimit.StemmingBurden);
        V(m.PowderFactor < c.PowderFactorMin - 1e-9, FragLimit.PowderFactorMin);
        V(m.PowderFactor > c.PowderFactorMax + 1e-9, FragLimit.PowderFactorMax);
        V(c.MaxFlyrockClearance is double fc && m.FlyrockClearance > fc + 1e-6, FragLimit.Flyrock);
        V(c.MaxPpv is double pv && m.Ppv is double ppv && ppv > pv + 1e-9, FragLimit.Ppv);
        V(Math.Abs(m.X50Cm / target.X50Cm - 1) * 100 > target.TolerancePct + 1e-9, FragLimit.X50Tolerance);
        V(target.X80MaxCm is double x80 && m.X80Cm > x80 + 1e-9, FragLimit.X80);
        V(target.OversizeMaxPct is double ov && m.OversizePct > ov + 1e-9, FragLimit.Oversize);
        V(target.FinesMaxPct is double fi && m.FinesPct > fi + 1e-9, FragLimit.Fines);
        W(m.Uniformity < c.MinUniformity - 1e-9, FragLimit.Uniformity);
        W(b.BenchHeight / Math.Max(1e-9, d.Burden) < c.MinStiffness - 1e-9, FragLimit.Stiffness);
    }

    /// <summary>
    /// The turn of the grid within ±<paramref name="window"/>° that leaves the fewest holes, counted without laying the
    /// pattern out: 5° steps, then 1° about the best. A gain below two holes is noise: no turn then. At the same count the
    /// turn nearest to none.
    /// </summary>
    public static (double Delta, int Before, int After) BestTurn(FragBase b, FragDesign d, double window)
    {
        int Count(double delta) => b.Patterns.Sum(r => CountHoles(b.Project, r, d.Burden, d.Spacing, delta));
        var none = Count(0);
        var w = Math.Min(180, Math.Abs(window));
        var tried = new Dictionary<double, int> { [0] = none };
        int At(double delta) { delta = Math.Round(delta, 6); if (!tried.TryGetValue(delta, out var n)) tried[delta] = n = Count(delta); return n; }
        for (double a = -w; a <= w + 1e-9; a += 5) At(a);
        var coarse = tried.OrderBy(t => t.Value).ThenBy(t => Math.Abs(t.Key)).First().Key;
        for (double a = Math.Max(-w, coarse - 4); a <= Math.Min(w, coarse + 4) + 1e-9; a += 1) At(a);
        var best = tried.OrderBy(t => t.Value).ThenBy(t => Math.Abs(t.Key)).First();
        return none - best.Value < 2 ? (0, none, none) : (best.Key, none, best.Value);
    }

    /// <summary>The holes a pattern would have with a burden, a spacing and a turn of its grid (its tree for a pattern cut into segments).</summary>
    public static int CountHoles(BlastProject p, HolePattern root, double burden, double spacing, double delta)
    {
        if (root.IsContainer)
        {
            var plan = PatternTree.Plan(p, root, (q, r) =>
            {
                var o = PatternRebuildOptions.For(q);
                if (Math.Abs(delta) > 1e-9) PatternTree.Rotate(q, r, delta, o);
                return PatternTree.Resize(q, r, burden, spacing, PatternRebuildOptions.For(q));
            });
            return plan.Moves.Count + plan.Adds.Count;
        }
        var copy = ProjectJson.DeepClone(root);
        copy.Azimuth = GeoMath.NormalizeBearing(copy.Azimuth + delta);
        copy.FaceEdge = null;
        HoleDesign.Resize(copy, burden, spacing);
        var suppressed = copy.Suppressed.Concat(copy.SeamDropped).Select(s => (s.I, s.J)).ToHashSet();
        return HoleDesign.PatternNodes(copy).Count(n => !suppressed.Contains((n.I, n.J)));
    }
}
