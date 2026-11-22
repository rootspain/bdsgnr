namespace BlastDesigner.Domain;

/// <summary>The units values are shown and typed in; they are kept and calculated in SI whatever is chosen.</summary>
public enum UnitSystem { Metric, Imperial }

/// <summary>What the angle of a hole is counted from on the screen; it is kept from vertical.</summary>
public enum AngleReference
{
    /// <summary>0° is vertical, as the program keeps it.</summary>
    FromVertical,
    /// <summary>90° is vertical; a hole up from the horizontal has a negative angle.</summary>
    FromHorizontal
}

/// <summary>The kinds of value whose unit follows the <see cref="UnitSystem"/> (or, the angle of a hole, the <see cref="AngleReference"/>).</summary>
public enum Quantity
{
    /// <summary>Coordinates, lengths, levels, burden and spacing: m · ft.</summary>
    Length,
    /// <summary>Diameter of a hole or a cartridge: mm · in.</summary>
    Diameter,
    /// <summary>kg · lb.</summary>
    Mass,
    /// <summary>A booster, kept in kg: g · oz.</summary>
    SmallMass,
    /// <summary>Charge per length of a hole: kg/m · lb/ft.</summary>
    LinearCharge,
    /// <summary>Core load of a cord, kept in kg/m: g/m · gr/ft.</summary>
    CordLoad,
    /// <summary>Density of an explosive: g/cm³ in both (g/cc in the US).</summary>
    ExplosiveDensity,
    /// <summary>Density of rock: t/m³ · short ton/yd³.</summary>
    RockDensity,
    /// <summary>m³ · yd³.</summary>
    Volume,
    /// <summary>A mass of rock: t · short ton.</summary>
    Tonnage,
    /// <summary>m² · ft².</summary>
    Area,
    /// <summary>Powder factor: kg/m³ · lb/yd³.</summary>
    PowderFactor,
    /// <summary>Velocity of detonation, of the signal: m/s · ft/s.</summary>
    Velocity,
    /// <summary>A time per length (the signal down a tube, the relief of a front): ms/m · ms/ft.</summary>
    DelayPerLength,
    /// <summary>Peak particle velocity: mm/s · in/s.</summary>
    Ppv,
    /// <summary>A cost per kilogram: per kg · per lb.</summary>
    CostPerMass,
    /// <summary>A cost per metre (of drilling): per m · per ft.</summary>
    CostPerLength,
    /// <summary>°C · °F.</summary>
    Temperature,
    /// <summary>The angle of a hole, kept from vertical: as <see cref="Measure.Angle"/> counts it.</summary>
    HoleAngle
}

/// <summary>
/// The values of the program as shown and typed: <see cref="Show"/> from the kept (SI, the angle of a hole from vertical)
/// to the units chosen, <see cref="Store"/> back. The choice is of the program, not of a project
/// (<see cref="System"/>, <see cref="Angle"/>): the same blast shows in the units of whoever opens it.
/// </summary>
public static class Measure
{
    public static UnitSystem System { get; set; } = UnitSystem.Metric;
    public static AngleReference Angle { get; set; } = AngleReference.FromVertical;

    private const double Foot = 0.3048, Inch = 0.0254, Pound = 0.45359237, Ounce = 0.028349523125, Grain = 0.00006479891, CubicYard = 0.764554857984, ShortTon = 907.18474;

    /// <summary>How many of the shown unit one kept unit is, in the imperial system (shown = kept × factor).</summary>
    private static double ImperialFactor(Quantity q) => q switch
    {
        Quantity.Length => 1 / Foot,
        Quantity.Diameter => 0.001 / Inch,
        Quantity.Mass => 1 / Pound,
        Quantity.SmallMass => 1 / Ounce,
        Quantity.LinearCharge => Foot / Pound,
        Quantity.CordLoad => Foot / Grain,
        Quantity.RockDensity => 1000 * CubicYard / ShortTon,
        Quantity.Volume => 1 / CubicYard,
        Quantity.Tonnage => 1000 / ShortTon,
        Quantity.Area => 1 / (Foot * Foot),
        Quantity.PowderFactor => CubicYard / Pound,
        Quantity.Velocity => 1 / Foot,
        Quantity.DelayPerLength => Foot,
        Quantity.Ppv => 0.001 / Inch,
        Quantity.CostPerMass => Pound,
        Quantity.CostPerLength => Foot,
        _ => 1
    };

    /// <summary>The value as it is shown, from the kept one.</summary>
    public static double Show(Quantity q, double kept)
    {
        if (q == Quantity.HoleAngle) return Angle == AngleReference.FromHorizontal ? 90 - kept : kept;
        // a booster is kept in kg and shown in g, the core load of a cord kept in kg/m and shown in g/m
        if (System == UnitSystem.Metric) return q is Quantity.CordLoad or Quantity.SmallMass ? kept * 1000 : kept;
        if (q == Quantity.Temperature) return kept * 9 / 5 + 32;
        return kept * ImperialFactor(q);
    }

    /// <summary>The value to keep, from the shown one.</summary>
    public static double Store(Quantity q, double shown)
    {
        if (q == Quantity.HoleAngle) return Angle == AngleReference.FromHorizontal ? 90 - shown : shown;
        if (System == UnitSystem.Metric) return q is Quantity.CordLoad or Quantity.SmallMass ? shown / 1000 : shown;
        if (q == Quantity.Temperature) return (shown - 32) * 5 / 9;
        return shown / ImperialFactor(q);
    }

    /// <summary>The symbol of the unit shown (English; the program translates it).</summary>
    public static string Symbol(Quantity q)
    {
        var metric = System == UnitSystem.Metric;
        return q switch
        {
            Quantity.Length => metric ? "m" : "ft",
            Quantity.Diameter => metric ? "mm" : "in",
            Quantity.Mass => metric ? "kg" : "lb",
            Quantity.SmallMass => metric ? "g" : "oz",
            Quantity.LinearCharge => metric ? "kg/m" : "lb/ft",
            Quantity.CordLoad => metric ? "g/m" : "gr/ft",
            Quantity.ExplosiveDensity => "g/cm³",
            Quantity.RockDensity => metric ? "t/m³" : "st/yd³",
            Quantity.Volume => metric ? "m³" : "yd³",
            Quantity.Tonnage => metric ? "t" : "st",
            Quantity.Area => metric ? "m²" : "ft²",
            Quantity.PowderFactor => metric ? "kg/m³" : "lb/yd³",
            Quantity.Velocity => metric ? "m/s" : "ft/s",
            Quantity.DelayPerLength => metric ? "ms/m" : "ms/ft",
            Quantity.Ppv => metric ? "mm/s" : "in/s",
            Quantity.CostPerMass => metric ? "per kg" : "per lb",
            Quantity.CostPerLength => metric ? "per m" : "per ft",
            Quantity.Temperature => metric ? "°C" : "°F",
            Quantity.HoleAngle => "°",
            _ => ""
        };
    }

    /// <summary>A format of the shown value: as many decimals as its unit needs.</summary>
    public static string Format(Quantity q)
    {
        var metric = System == UnitSystem.Metric;
        return q switch
        {
            Quantity.Diameter => metric ? "0.#" : "0.###",
            Quantity.Ppv => metric ? "0.##" : "0.###",
            Quantity.HoleAngle or Quantity.Temperature => "0.#",
            Quantity.Velocity or Quantity.SmallMass => "0",
            _ => "0.###"
        };
    }

    /// <summary>The kept value shown in its format (invariant digits; the caller adds the unit).</summary>
    public static string Text(Quantity q, double kept, string? format = null, IFormatProvider? culture = null) =>
        Show(q, kept).ToString(format ?? Format(q), culture ?? global::System.Globalization.CultureInfo.CurrentCulture);
}
