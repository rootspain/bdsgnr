namespace BlastDesigner.Domain.Model;

/// <summary>
/// Initiation rule: chooses the downhole detonator, booster and primer count for matching holes.
/// Rules are applied in priority order and the first match wins, like loading rules.
/// </summary>
public sealed class InitiationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Initiation rule";
    public int Priority { get; set; }
    /// <summary>Hole type filter 1..8, 0 = any.</summary>
    public int HoleType { get; set; }
    /// <summary>Only holes in rows whose name contains this text (empty = any row).</summary>
    public string RowFilter { get; set; } = string.Empty;
    /// <summary>Horizon filter; -1 = any.</summary>
    public int Horizon { get; set; } = -1;
    public double MinLength { get; set; }
    public double MaxLength { get; set; } = 1000;
    public Guid? DetonatorId { get; set; }
    public Guid? BoosterId { get; set; }
    /// <summary>Primers per explosive deck; 0 keeps the existing count.</summary>
    public int PrimersPerDeck { get; set; }
    /// <summary>Overrides the detonator delay, ms; null uses the product's nominal delay.</summary>
    public double? DelayMs { get; set; }
    /// <summary>Primer offset above the bottom of each explosive deck, m.</summary>
    public double PrimerOffset { get; set; } = 0.5;

    public bool Matches(Hole h, string? rowName)
    {
        if (HoleType != 0 && h.HoleType != HoleType) return false;
        if (Horizon >= 0 && h.Horizon != Horizon) return false;
        if (h.EffectiveLength < MinLength || h.EffectiveLength > MaxLength) return false;
        if (RowFilter.Length > 0 && (rowName == null || !rowName.Contains(RowFilter, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }
}

/// <summary>Allowed deviation of the as-drilled hole from its design.</summary>
public sealed class DesignTolerances
{
    /// <summary>Plan distance between actual and design collar, m.</summary>
    public double CollarXY { get; set; } = 0.3;
    /// <summary>Difference between dipped and design length, m (either direction).</summary>
    public double Depth { get; set; } = 0.5;
    /// <summary>Plan distance between actual and design toe, m.</summary>
    public double ToeXY { get; set; } = 1.0;
    /// <summary>Difference in angle from vertical, degrees.</summary>
    public double AngleDeg { get; set; } = 2;
    /// <summary>Difference in bearing, degrees (checked only for holes steeper than 5° from vertical).</summary>
    public double BearingDeg { get; set; } = 5;
    /// <summary>Holes shorter than design by more than this fraction are flagged for redrill.</summary>
    public double RedrillFraction { get; set; } = 0.15;
}

/// <summary>One drilling log sample (measure-while-drilling) at a depth along the hole.</summary>
public sealed class MwdSample
{
    public double Depth { get; set; }
    /// <summary>Penetration rate, m/min.</summary>
    public double PenetrationRate { get; set; }
    /// <summary>Normalised hardness index, 0 (soft) .. 1 (hard), or a site-specific scale.</summary>
    public double Hardness { get; set; }
}

/// <summary>Flyrock prediction constants (Richards &amp; Moore style).</summary>
public sealed class FlyrockModel
{
    /// <summary>Site constant k; 13.5 is typical, 27 is conservative.</summary>
    public double SiteK { get; set; } = 13.5;
    /// <summary>Multiplier applied to the predicted maximum throw to get the clearance distance.</summary>
    public double SafetyFactor { get; set; } = 2;
    /// <summary>Minimum clearance distance for personnel, m.</summary>
    public double MinimumClearance { get; set; } = 300;
}

/// <summary>Kuz-Ram / Swebrec fragmentation inputs that are not derived from the design.</summary>
public sealed class FragmentationModel
{
    /// <summary>Rock factor A (Cunningham). 0 = derive from the rock mass description below.</summary>
    public double RockFactor { get; set; } = 8;
    /// <summary>Rock mass description: 10 powdery, 50 massive, or the joint factor when jointed.</summary>
    public double RockMassDescription { get; set; } = 50;
    public double JointFactor { get; set; }
    /// <summary>Uniaxial compressive strength, MPa.</summary>
    public double Ucs { get; set; } = 120;
    /// <summary>Young's modulus, GPa.</summary>
    public double YoungsModulus { get; set; } = 50;
    /// <summary>Standard deviation of drilling accuracy, m.</summary>
    public double DrillDeviation { get; set; } = 0.2;
    /// <summary>Largest block size in situ (Swebrec x_max), cm.</summary>
    public double MaxBlockCm { get; set; } = 200;
    /// <summary>Oversize threshold for the report, cm.</summary>
    public double OversizeCm { get; set; } = 100;
    /// <summary>Fines threshold for the report, cm.</summary>
    public double FinesCm { get; set; } = 2;
}

/// <summary>Measured vibration event used to fit the site law.</summary>
public sealed class VibrationRecord
{
    public string Name { get; set; } = string.Empty;
    public DateTime? Date { get; set; }
    public double DistanceM { get; set; }
    public double ChargeKg { get; set; }
    public double PpvMm { get; set; }
    public double? AirblastDb { get; set; }
    public bool Use { get; set; } = true;
}

/// <summary>Map projection used to exchange coordinates with geographic formats (KML).</summary>
public sealed class SiteCoordinateSystem
{
    /// <summary>UTM zone 1..60; 0 = local grid without geographic reference.</summary>
    public int UtmZone { get; set; }
    public bool SouthernHemisphere { get; set; }
    /// <summary>Added to local X/Y to obtain UTM easting/northing.</summary>
    public double OffsetEasting { get; set; }
    public double OffsetNorthing { get; set; }
    public double OffsetElevation { get; set; }
}
