namespace BlastDesigner.Domain.Model;

public enum ProductKind
{
    BulkExplosive,
    PackagedExplosive,
    Stemming,
    GasBag,
    Booster,
    DownholeDetonator,
    SurfaceConnector,
    ElectronicDetonator,
    DetonatingCord,
    Other
}

/// <summary>Consumable used in the design. The catalogue is user-maintained and stored with the project (available resources).</summary>
public sealed class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public ProductKind Kind { get; set; }
    /// <summary>Density, g/cm³ (for explosives and stemming).</summary>
    public double Density { get; set; } = 1.0;
    /// <summary>Relative weight strength vs ANFO, %.</summary>
    public double RelativeWeightStrength { get; set; } = 100;
    /// <summary>Energy, MJ/kg.</summary>
    public double Energy { get; set; } = 3.7;
    /// <summary>Velocity of detonation, m/s.</summary>
    public double Vod { get; set; }
    /// <summary>Nominal delay for detonators and connectors, ms.</summary>
    public double DelayMs { get; set; }
    /// <summary>Mass of one unit for boosters and cartridges, kg.</summary>
    public double UnitMass { get; set; }
    /// <summary>Cartridge length for packaged products, m.</summary>
    public double UnitLength { get; set; }
    public bool WaterResistant { get; set; }
    /// <summary>Cost per kg for bulk/packaged/stemming, per unit otherwise.</summary>
    public double Cost { get; set; }
    public uint Color { get; set; } = 0xFFFF8C00;

    public bool IsExplosive => Kind is ProductKind.BulkExplosive or ProductKind.PackagedExplosive;
    public bool IsColumnProduct => Kind is ProductKind.BulkExplosive or ProductKind.PackagedExplosive or ProductKind.Stemming or ProductKind.GasBag;
    public bool IsDetonator => Kind is ProductKind.DownholeDetonator or ProductKind.ElectronicDetonator;

    public override string ToString() => Name;

    public Product Clone() => (Product)MemberwiseClone();

    /// <summary>Small generic starter catalogue so a new project is usable immediately. Values are typical textbook figures.</summary>
    public static List<Product> DefaultCatalogue() => new()
    {
        new() { Name = "ANFO", Kind = ProductKind.BulkExplosive, Density = 0.82, RelativeWeightStrength = 100, Energy = 3.7, Vod = 3500, Cost = 1.0, Color = 0xFFFFC107 },
        new() { Name = "Heavy ANFO 70/30", Kind = ProductKind.BulkExplosive, Density = 1.25, RelativeWeightStrength = 92, Energy = 3.4, Vod = 4800, WaterResistant = true, Cost = 1.2, Color = 0xFFFF9800 },
        new() { Name = "Bulk emulsion", Kind = ProductKind.BulkExplosive, Density = 1.15, RelativeWeightStrength = 80, Energy = 2.9, Vod = 5000, WaterResistant = true, Cost = 1.3, Color = 0xFFE91E63 },
        new() { Name = "Packaged emulsion 65mm", Kind = ProductKind.PackagedExplosive, Density = 1.18, RelativeWeightStrength = 85, Energy = 3.1, UnitMass = 2.5, UnitLength = 0.6, WaterResistant = true, Cost = 2.5, Color = 0xFFAB47BC },
        new() { Name = "Crushed aggregate", Kind = ProductKind.Stemming, Density = 1.8, Cost = 0.02, Color = 0xFF9E9E9E },
        new() { Name = "Drill cuttings", Kind = ProductKind.Stemming, Density = 1.6, Cost = 0, Color = 0xFFBCAAA4 },
        new() { Name = "Gas bag", Kind = ProductKind.GasBag, Density = 0, Cost = 5, Color = 0xFFE0F7FA },
        new() { Name = "Booster 400 g", Kind = ProductKind.Booster, UnitMass = 0.4, Density = 1.6, Energy = 5.0, Cost = 6, Color = 0xFFD32F2F },
        new() { Name = "Booster 900 g", Kind = ProductKind.Booster, UnitMass = 0.9, Density = 1.6, Energy = 5.0, Cost = 11, Color = 0xFFB71C1C },
        new() { Name = "Downhole det 500 ms", Kind = ProductKind.DownholeDetonator, DelayMs = 500, Cost = 4, Color = 0xFF1976D2 },
        new() { Name = "Downhole det 400 ms", Kind = ProductKind.DownholeDetonator, DelayMs = 400, Cost = 4, Color = 0xFF1565C0 },
        new() { Name = "Electronic detonator", Kind = ProductKind.ElectronicDetonator, DelayMs = 0, Cost = 25, Color = 0xFF00897B },
        new() { Name = "Surface connector 17 ms", Kind = ProductKind.SurfaceConnector, DelayMs = 17, Cost = 3, Color = 0xFFFFEB3B },
        new() { Name = "Surface connector 25 ms", Kind = ProductKind.SurfaceConnector, DelayMs = 25, Cost = 3, Color = 0xFFF44336 },
        new() { Name = "Surface connector 42 ms", Kind = ProductKind.SurfaceConnector, DelayMs = 42, Cost = 3, Color = 0xFFFFFFFF },
        new() { Name = "Surface connector 67 ms", Kind = ProductKind.SurfaceConnector, DelayMs = 67, Cost = 3, Color = 0xFF4CAF50 },
        new() { Name = "Surface connector 109 ms", Kind = ProductKind.SurfaceConnector, DelayMs = 109, Cost = 3, Color = 0xFF2196F3 },
        new() { Name = "Surface connector 0 ms", Kind = ProductKind.SurfaceConnector, DelayMs = 0, Cost = 3, Color = 0xFF9C27B0 },
    };
}

/// <summary>One step of a charging rule, built from the bottom of the hole upward.</summary>
public sealed class DeckTemplate
{
    public Guid? ProductId { get; set; }
    /// <summary>Deck length in metres. Zero means "fill the remaining column below the top stemming".</summary>
    public double Length { get; set; }
    public int PrimerCount { get; set; }
    public Guid? BoosterId { get; set; }
    public Guid? DetonatorId { get; set; }
    /// <summary>Primer offset above the bottom of the deck, m.</summary>
    public double PrimerOffset { get; set; } = 0.5;
    /// <summary>
    /// Offsets of each primer above the bottom of the deck, m (deepest first). When it lists <see cref="PrimerCount"/> values it is used
    /// instead of <see cref="PrimerOffset"/> and the even spacing; set by rules saved from a designed column.
    /// </summary>
    public List<double>? PrimerOffsets { get; set; }
}

/// <summary>Automatic charging rule. The first matching rule by priority loads a hole.</summary>
public sealed class LoadingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Rule";
    public int Priority { get; set; }
    /// <summary>Hole type filter 1..8, 0 = any.</summary>
    public int HoleType { get; set; }
    public double MinLength { get; set; }
    public double MaxLength { get; set; } = 1000;
    public double MinDiameter { get; set; }
    public double MaxDiameter { get; set; } = 1000;
    /// <summary>Apply only to wet (true), dry (false) or any (null) holes.</summary>
    public bool? Wet { get; set; }
    public double StemmingLength { get; set; } = 3;
    public Guid? StemmingProductId { get; set; }
    public List<DeckTemplate> Decks { get; set; } = new();
    /// <summary>Alternate explosive for the wet part of the column when the designed product is not water resistant.</summary>
    public Guid? WetAlternateProductId { get; set; }
    /// <summary>Alternate explosive for intervals whose MWD hardness is at or above <see cref="HardnessThreshold"/>.</summary>
    public Guid? HardAlternateProductId { get; set; }
    public double HardnessThreshold { get; set; } = 0.7;
    /// <summary>Alternate explosive for intervals softer than <see cref="SoftnessThreshold"/>.</summary>
    public Guid? SoftAlternateProductId { get; set; }
    public double SoftnessThreshold { get; set; } = 0.3;
    /// <summary>Stemming is lengthened by this much when the MWD log shows soft rock at the collar, m.</summary>
    public double SoftCollarExtraStemming { get; set; }

    public bool Matches(Hole h)
    {
        if (HoleType != 0 && h.HoleType != HoleType) return false;
        var len = h.EffectiveLength;
        if (len < MinLength || len > MaxLength) return false;
        if (h.Diameter < MinDiameter || h.Diameter > MaxDiameter) return false;
        if (Wet.HasValue && Wet.Value != h.WaterDepth > 0) return false;
        return true;
    }
}
