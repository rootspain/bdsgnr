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
    /// <summary>Delay relay on detonating cord (surface).</summary>
    CordRelay,
    /// <summary>Electric delay detonator (in the hole; fired through the wiring).</summary>
    ElectricDetonator,
    /// <summary>Harness wire connecting electric detonators (no delay of its own).</summary>
    HarnessWire,
    Other
}

/// <summary>Consumable used in the design. The catalogue is user-maintained and stored with the project (available resources).</summary>
public sealed class Product
{
    public string Id { get; set; } = ProductCatalogue.NewId();
    public string Name { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    /// <summary>
    /// Product family, e.g. "Exel Connectadet": the products of one kind and family are the variants (delays) of it in
    /// the initiation catalogue. Empty: the name without its delay (<see cref="FamilyName"/>). A copy of the name of the
    /// record of <see cref="FamilyId"/>, kept with it by <see cref="ProductCatalogue.Link"/>.
    /// </summary>
    public string Family { get; set; } = string.Empty;
    /// <summary>How the program calculates with the product: the behaviour of its record of <see cref="KindId"/>.</summary>
    public ProductKind Kind
    {
        get => _kind;
        set { if (_kind != value) _kindId = null; _kind = value; }
    }
    private ProductKind _kind;
    /// <summary>Its record in the table of kinds; without one the built-in kind of <see cref="Kind"/>.</summary>
    public string KindId
    {
        get => _kindId ?? ProductCatalogue.KindId(_kind);
        set => _kindId = value;
    }
    private string? _kindId;
    /// <summary>Its record in the table of families; none for products outside the initiation catalogue.</summary>
    public string? FamilyId { get; set; }
    /// <summary>Hidden when new records are made; what already uses it goes on showing it.</summary>
    public bool Deleted { get; set; }
    /// <summary>Added in the open window and not applied yet: not stored.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool IsNew { get; set; }
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
    public bool IsDetonator => Kind is ProductKind.DownholeDetonator or ProductKind.ElectronicDetonator or ProductKind.ElectricDetonator;

    public override string ToString() => Name;

    /// <summary>The family, or without one the name less a trailing delay ("Surface connector 17 ms" → "Surface connector").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FamilyName => !string.IsNullOrWhiteSpace(Family) ? Family.Trim()
        : System.Text.RegularExpressions.Regex.Replace(Name, @"\s*\d+(?:[.,]\d+)?\s*(?:ms|мс)?\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim() is { Length: > 0 } n ? n : Name;

    public Product Clone() => (Product)MemberwiseClone();

    /// <summary>Puts the product in a kind of the table: it calculates as that kind and leaves its family.</summary>
    public void SetKind(CatalogueKind kind)
    {
        Kind = kind.Behaviour;
        KindId = kind.Id;
        FamilyId = null;
    }

    /// <summary>Small generic starter catalogue so a new project is usable immediately. Values are typical textbook figures.</summary>
    public static List<Product> DefaultCatalogue() => new()
    {
        new() { Id = "9f64c21c-209d-4f34-b7f5-003e3c75cb04", Name = "ANFO", Kind = ProductKind.BulkExplosive, Density = 0.82, RelativeWeightStrength = 100, Energy = 3.7, Vod = 3500, Cost = 1.0, Color = 0xFFFFC107 },
        new() { Id = "92ae9673-0326-4a02-9719-d0b8ce34361c", Name = "Heavy ANFO 70/30", Kind = ProductKind.BulkExplosive, Density = 1.25, RelativeWeightStrength = 92, Energy = 3.4, Vod = 4800, WaterResistant = true, Cost = 1.2, Color = 0xFFFF9800 },
        new() { Id = "12ea5d70-649d-4d4d-918e-89f026b73375", Name = "Bulk emulsion", Kind = ProductKind.BulkExplosive, Density = 1.15, RelativeWeightStrength = 80, Energy = 2.9, Vod = 5000, WaterResistant = true, Cost = 1.3, Color = 0xFFE91E63 },
        new() { Id = "00b4ab9d-9fa5-4532-89d5-be77e98a1cdf", Name = "Packaged emulsion 65mm", Kind = ProductKind.PackagedExplosive, Density = 1.18, RelativeWeightStrength = 85, Energy = 3.1, UnitMass = 2.5, UnitLength = 0.6, WaterResistant = true, Cost = 2.5, Color = 0xFFAB47BC },
        new() { Id = "5034fa29-7728-45eb-90b3-6fc6b043eda7", Name = "Crushed aggregate", Kind = ProductKind.Stemming, Density = 1.8, Cost = 0.02, Color = 0xFF9E9E9E },
        new() { Id = "0b973c2d-ab33-41ed-ade4-d6fe70559ff0", Name = "Drill cuttings", Kind = ProductKind.Stemming, Density = 1.6, Cost = 0, Color = 0xFFBCAAA4 },
        new() { Id = "2ce94890-3295-42d2-87d7-58a47c0ba208", Name = "Gas bag", Kind = ProductKind.GasBag, Density = 0, Cost = 5, Color = 0xFFE0F7FA },
        new() { Id = "e51b9280-5cfc-4f7e-ae38-04459551f496", Name = "Booster 400 g", Kind = ProductKind.Booster, UnitMass = 0.4, Density = 1.6, Energy = 5.0, Cost = 6, Color = 0xFFD32F2F },
        new() { Id = "5e754a2f-7c8e-42f5-8759-88cb6128e8e7", Name = "Booster 900 g", Kind = ProductKind.Booster, UnitMass = 0.9, Density = 1.6, Energy = 5.0, Cost = 11, Color = 0xFFB71C1C },
        new() { Id = "9b1a41ce-34f1-41fb-b691-10ef5d988960", Name = "Downhole det 500 ms", Family = "Downhole detonator", Kind = ProductKind.DownholeDetonator, DelayMs = 500, Cost = 4, Color = 0xFF1976D2 },
        new() { Id = "3d1c671d-5460-4696-ba1c-59a8c5bbea03", Name = "Downhole det 400 ms", Family = "Downhole detonator", Kind = ProductKind.DownholeDetonator, DelayMs = 400, Cost = 4, Color = 0xFF1565C0 },
        new() { Id = "38a53a3b-bf68-4fa1-a7a4-6998b9284666", Name = "Electronic detonator", Family = "Electronic detonator", Kind = ProductKind.ElectronicDetonator, DelayMs = 0, Cost = 25, Color = 0xFF00897B },
        new() { Id = "0fe4a38c-2cbe-45a4-9f66-73016d8029fa", Name = "Surface connector 17 ms", Family = "Surface connector", Kind = ProductKind.SurfaceConnector, DelayMs = 17, Cost = 3, Color = 0xFFFFEB3B },
        new() { Id = "95edced9-9aab-4121-84e0-55eb7ee2f529", Name = "Surface connector 25 ms", Family = "Surface connector", Kind = ProductKind.SurfaceConnector, DelayMs = 25, Cost = 3, Color = 0xFFF44336 },
        new() { Id = "dfe87a96-71f0-4a93-9bf0-431198d83df2", Name = "Surface connector 42 ms", Family = "Surface connector", Kind = ProductKind.SurfaceConnector, DelayMs = 42, Cost = 3, Color = 0xFFFFFFFF },
        new() { Id = "79d2c2c3-7feb-4f8b-ab96-bd7869d3739c", Name = "Surface connector 67 ms", Family = "Surface connector", Kind = ProductKind.SurfaceConnector, DelayMs = 67, Cost = 3, Color = 0xFF4CAF50 },
        new() { Id = "963a5b41-dfb2-4bce-922a-a58140529796", Name = "Surface connector 109 ms", Family = "Surface connector", Kind = ProductKind.SurfaceConnector, DelayMs = 109, Cost = 3, Color = 0xFF2196F3 },
        new() { Id = "41f9b701-37db-4fbb-8b14-51f861f29db3", Name = "Surface connector 0 ms", Family = "Surface connector", Kind = ProductKind.SurfaceConnector, DelayMs = 0, Cost = 3, Color = 0xFF9C27B0 },
        new() { Id = "e51e810f-ea5a-419b-8156-694c39ad1df6", Name = "Detonating cord 10 g/m", Family = "Detonating cord", Kind = ProductKind.DetonatingCord, UnitMass = 0.01, Vod = 6500, Cost = 0.8, Color = 0xFFFF5722 },
        new() { Id = "0ec2651e-4945-4554-915e-f8c4378e6058", Name = "Cord relay 17 ms", Family = "Cord relay", Kind = ProductKind.CordRelay, DelayMs = 17, Cost = 3, Color = 0xFFFFB300 },
        new() { Id = "2eaae6f2-75f8-45b1-ae7b-10d76024d14b", Name = "Cord relay 25 ms", Family = "Cord relay", Kind = ProductKind.CordRelay, DelayMs = 25, Cost = 3, Color = 0xFFE53935 },
        new() { Id = "9bc120bb-4f12-4f79-9421-a3878a714183", Name = "Cord relay 42 ms", Family = "Cord relay", Kind = ProductKind.CordRelay, DelayMs = 42, Cost = 3, Color = 0xFF8D6E63 },
        new() { Id = "b6b2be9a-c3d3-4fa1-83bf-c42467dc8e56", Name = "Electric detonator 25 ms", Family = "Electric detonator", Kind = ProductKind.ElectricDetonator, DelayMs = 25, Cost = 3, Color = 0xFF5C6BC0 },
        new() { Id = "7ff165ec-6ed6-4544-94a3-4e16747fe227", Name = "Electric detonator 500 ms", Family = "Electric detonator", Kind = ProductKind.ElectricDetonator, DelayMs = 500, Cost = 3, Color = 0xFF3949AB },
        new() { Id = "f7407010-0e86-4dae-8a4f-2b03856e388a", Name = "Harness wire", Family = "Harness wire", Kind = ProductKind.HarnessWire, Cost = 0.2, Color = 0xFF78909C },
    };
}

/// <summary>One step of a charging rule, built from the bottom of the hole upward.</summary>
public sealed class DeckTemplate
{
    public string? ProductId { get; set; }
    /// <summary>Deck length in metres. Zero means "fill the remaining column below the top stemming".</summary>
    public double Length { get; set; }
    public int PrimerCount { get; set; }
    public string? BoosterId { get; set; }
    public string? DetonatorId { get; set; }
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
    public string? StemmingProductId { get; set; }
    public List<DeckTemplate> Decks { get; set; } = new();
    /// <summary>Alternate explosive for the wet part of the column when the designed product is not water resistant.</summary>
    public string? WetAlternateProductId { get; set; }
    /// <summary>Alternate explosive for intervals whose MWD hardness is at or above <see cref="HardnessThreshold"/>.</summary>
    public string? HardAlternateProductId { get; set; }
    public double HardnessThreshold { get; set; } = 0.7;
    /// <summary>Alternate explosive for intervals softer than <see cref="SoftnessThreshold"/>.</summary>
    public string? SoftAlternateProductId { get; set; }
    public double SoftnessThreshold { get; set; } = 0.3;
    /// <summary>Stemming is lengthened by this much when the MWD log shows soft rock at the collar, m.</summary>
    public double SoftCollarExtraStemming { get; set; }
    /// <summary>
    /// Minimum top stemming in hole diameters the rule was saved with; loading a hole by the rule makes it the project's.
    /// Null for rules saved before it, which leave the project's as it is.
    /// </summary>
    public double? MinStemmingDiameters { get; set; }

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
