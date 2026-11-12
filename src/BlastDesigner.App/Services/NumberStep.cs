using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace BlastDesigner.App.Services;

/// <summary>
/// ↑ and ↓ in a number field add or take away 1: the text changes as if typed, and the field takes it as it takes typing (on
/// Enter or on leaving it). A field is a number field while its text is one number; a text field that may hold digits only
/// (a hole number, a name) is marked <see cref="TextClass"/>, and a field that steps itself <see cref="OwnStepsClass"/>.
/// </summary>
public static class NumberStep
{
    /// <summary>Class of a text box whose text is text even when it is digits: the arrows leave it alone.</summary>
    public const string TextClass = "plainText";
    /// <summary>Class of a number box that steps by the arrows itself, with its own step.</summary>
    public const string OwnStepsClass = "ownSteps";

    /// <summary>Takes the arrows of every text box of the program; once, at the start.</summary>
    public static void Attach() =>
        InputElement.KeyDownEvent.AddClassHandler<TextBox>(OnKeyDown, RoutingStrategies.Tunnel);

    private static void OnKeyDown(TextBox box, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not (Key.Up or Key.Down) || e.KeyModifiers != KeyModifiers.None) return;
        if (box.AcceptsReturn || box.IsReadOnly || !box.IsEnabled || box.Classes.Contains(TextClass) || box.Classes.Contains(OwnStepsClass)) return;
        // the box inside a number spinner: the spinner steps itself
        if (box.FindAncestorOfType<NumericUpDown>() != null) return;
        if (Step(box.Text, e.Key == Key.Up ? 1 : -1) is not { } text) return;
        box.Text = text;
        box.CaretIndex = text.Length;
        e.Handled = true;
    }

    /// <summary>
    /// The text of a number with <paramref name="delta"/> added, written as it was: the same decimal separator and as many
    /// decimals. Null when the text is not one number (empty, a word, a number with a unit).
    /// </summary>
    public static string? Step(string? text, double delta)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        // the minus may be typed as a dash
        t = t.Replace('−', '-');
        var comma = t.Contains(',');
        if (comma && t.Contains('.')) return null;
        var plain = t.Replace(',', '.');
        if (!double.TryParse(plain, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)) return null;
        var point = plain.IndexOf('.');
        var decimals = point < 0 ? 0 : plain.Length - point - 1;
        var result = Math.Round(v + delta, decimals).ToString("F" + decimals, CultureInfo.InvariantCulture);
        // −0 reads as 0
        if (result.StartsWith('-') && result.Trim('-', '0', '.').Length == 0) result = result[1..];
        return comma ? result.Replace('.', ',') : result;
    }
}
