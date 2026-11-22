using System.Globalization;
using System.Text.RegularExpressions;
using BlastDesigner.Domain;

namespace BlastDesigner.App.Services;

/// <summary>
/// Values of the windows in the units of the program (<see cref="Measure"/>): shown from the kept SI value, kept from the
/// typed one, with the symbol of the unit in the language of the program.
/// </summary>
public static class Qty
{
    public static double Show(Quantity q, double kept) => Measure.Show(q, kept);
    public static double? Show(Quantity q, double? kept) => kept is double v ? Measure.Show(q, v) : null;
    public static double Store(Quantity q, double shown) => Measure.Store(q, shown);
    public static double? Store(Quantity q, double? shown) => shown is double v ? Measure.Store(q, v) : null;

    /// <summary>The symbol of the unit shown, translated.</summary>
    public static string Sym(Quantity q) => Loc.Ui(Measure.Symbol(q));

    /// <summary>The kept value as shown, in <paramref name="format"/> (the format of the quantity without one).</summary>
    public static string Text(Quantity q, double kept, string? format = null) =>
        Measure.Show(q, kept).ToString(format ?? Measure.Format(q), CultureInfo.CurrentCulture);

    /// <summary>The kept value as shown with its unit: "12.5 ft".</summary>
    public static string WithUnit(Quantity q, double kept, string? format = null) => Text(q, kept, format) + " " + Sym(q);

    /// <inheritdoc cref="MeasureText.Caption"/>
    public static string Caption(string text) => MeasureText.Caption(text);

    /// <inheritdoc cref="MeasureText.Formatted"/>
    public static (string Format, object?[] Args) Formatted(string format, object?[] args) => MeasureText.Formatted(format, args);

    /// <inheritdoc cref="MeasureText.InText"/>
    public static string InText(string text) => MeasureText.InText(text);
}
