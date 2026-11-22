using System.Globalization;
using System.Text.RegularExpressions;

namespace BlastDesigner.Domain;

/// <summary>
/// Metric units written in texts (captions, formats, texts of the engine), English and Russian, in the units of the
/// program (<see cref="Measure"/>): the numbers they follow shown, the unit imperial. In the metric system texts stay as
/// they are.
/// </summary>
public static class MeasureText
{
    /// <summary>The metric units written in texts (English and Russian), the quantity each is of and its imperial symbol.</summary>
    private static readonly (string Metric, Quantity Q, string Imperial)[] Units =
    {
        ("kg/m³", Quantity.PowderFactor, "lb/yd³"), ("кг/м³", Quantity.PowderFactor, "lb/yd³"),
        ("t/m³", Quantity.RockDensity, "st/yd³"), ("т/м³", Quantity.RockDensity, "st/yd³"),
        ("kg/m", Quantity.LinearCharge, "lb/ft"), ("кг/м", Quantity.LinearCharge, "lb/ft"),
        ("g/m", Quantity.CordLoad, "gr/ft"), ("г/м", Quantity.CordLoad, "gr/ft"),
        ("mm/s", Quantity.Ppv, "in/s"), ("мм/с", Quantity.Ppv, "in/s"),
        ("ms/m", Quantity.DelayPerLength, "ms/ft"), ("мс/м", Quantity.DelayPerLength, "ms/ft"),
        ("m/s", Quantity.Velocity, "ft/s"), ("м/с", Quantity.Velocity, "ft/s"),
        ("m³", Quantity.Volume, "yd³"), ("м³", Quantity.Volume, "yd³"),
        ("m²", Quantity.Area, "ft²"), ("м²", Quantity.Area, "ft²"),
        ("mm", Quantity.Diameter, "in"), ("мм", Quantity.Diameter, "in"),
        ("kg", Quantity.Mass, "lb"), ("кг", Quantity.Mass, "lb"),
        ("m", Quantity.Length, "ft"), ("м", Quantity.Length, "ft"),
        ("°C", Quantity.Temperature, "°F"),
        ("per kg", Quantity.CostPerMass, "per lb"), ("за кг", Quantity.CostPerMass, "per lb"),
    };

    private static readonly Dictionary<string, (Quantity Q, string Imperial)> ByMetric = Units.ToDictionary(u => u.Metric, u => (u.Q, u.Imperial));

    // the units longest first, so that kg/m³ is not read as kg; a unit ends where no letter, slash or power follows
    private static readonly string UnitPattern = string.Join("|", Units.Select(u => Regex.Escape(u.Metric)).OrderByDescending(u => u.Length));

    private static readonly Regex Ending = new(@"(?:(?<lead>,\s*|\s\()|(?<=\p{L})\s)(?<unit>" + UnitPattern + @")(?<close>\)?)$", RegexOptions.Compiled);

    /// <summary>
    /// A caption with the metric unit at its end ("Burden, m", "Burden (m)", "Volume m³") in the units of the program: the
    /// text itself in the metric system.
    /// </summary>
    public static string Caption(string text)
    {
        if (Measure.System == UnitSystem.Metric || text.Length == 0) return text;
        var m = Ending.Match(text);
        if (!m.Success || !ByMetric.TryGetValue(m.Groups["unit"].Value, out var unit)) return text;
        // a bracket closes only what it opened
        if ((m.Groups["close"].Length > 0) != m.Groups["lead"].Value.EndsWith('(')) return text;
        return text[..m.Groups["unit"].Index] + unit.Imperial + m.Groups["close"].Value;
    }

    // "{1:0.0} m", "{0:0.00} × {1:0.00} m": the numbers a unit follows in a format
    private static readonly Regex Placed = new(@"(?<numbers>(?:\{\d+(?::[^}]*)?\}\s*×\s*)*\{\d+(?::[^}]*)?\})(?<gap>\s?)(?<unit>" + UnitPattern + @")(?![\p{L}/²³])", RegexOptions.Compiled);
    private static readonly Regex Index = new(@"\{(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// A format whose numbers are followed by a metric unit ("{0:0.0} m") in the units of the program: those numbers shown,
    /// the unit imperial. Other placeholders, and numbers that are not numbers, stay as they are.
    /// </summary>
    public static (string Format, object?[] Args) Formatted(string format, object?[] args)
    {
        if (Measure.System == UnitSystem.Metric || args.Length == 0) return (format, args);
        var copy = (object?[])args.Clone();
        var converted = new HashSet<int>();
        var result = Placed.Replace(format, m =>
        {
            var unit = ByMetric[m.Groups["unit"].Value];
            var indices = Index.Matches(m.Groups["numbers"].Value).Select(x => int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
            if (indices.Any(i => i >= copy.Length || !IsNumber(copy[i]))) return m.Value;
            foreach (var i in indices)
                if (converted.Add(i)) copy[i] = Measure.Show(unit.Q, Convert.ToDouble(copy[i], CultureInfo.InvariantCulture));
            return m.Groups["numbers"].Value + m.Groups["gap"].Value + unit.Imperial;
        });
        return (result, copy);
    }

    private static bool IsNumber(object? o) => o is double or float or decimal or int or long;

    private static readonly Regex Written = new(@"(?<![\d.,])(?<num>-?\d+(?:[.,]\d+)?)(?<gap>\s?)(?<unit>" + UnitPattern + @")(?![\p{L}/²³])", RegexOptions.Compiled);

    /// <summary>
    /// A text with numbers written with a metric unit ("12.5 m", as the engine writes them) in the units of the program:
    /// each number shown with as many decimals as it had, the unit imperial. The text itself in the metric system.
    /// </summary>
    public static string InText(string text)
    {
        if (Measure.System == UnitSystem.Metric || text.Length == 0) return text;
        return Written.Replace(text, m =>
        {
            var num = m.Groups["num"].Value;
            var comma = num.Contains(',');
            if (!double.TryParse(num.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return m.Value;
            var unit = ByMetric[m.Groups["unit"].Value];
            var decimals = num.Contains('.') || comma ? num.Length - num.IndexOfAny(new[] { '.', ',' }) - 1 : 0;
            var shown = Measure.Show(unit.Q, v).ToString("F" + decimals, CultureInfo.InvariantCulture);
            if (comma) shown = shown.Replace('.', ',');
            return shown + m.Groups["gap"].Value + unit.Imperial;
        });
    }
}
