using System.Text.RegularExpressions;

namespace BlastDesigner.App.Services;

/// <summary>
/// UI localisation. English text is the key; other languages map it to a translation.
/// Missing translations fall back to the English text.
/// </summary>
public static partial class Loc
{
    public static string Language { get; set; } = "ru";

    public static IReadOnlyList<(string Code, string Name)> Languages { get; } = new[] { ("en", "English"), ("ru", "Русский") };

    /// <summary>The text in the language of the program; a unit at its end ("Burden, m") in the units of the program (<see cref="Qty.Caption"/>).</summary>
    public static string T(string english) => Qty.Caption(Raw(english));

    /// <summary>The text in the language of the program, as it is.</summary>
    private static string Raw(string english) => Language == "ru" && Ru.TryGetValue(english, out var ru) ? ru : english;

    /// <summary>Display name of user data that may be a standard name (layers, groups): translated only when listed as "name:…".</summary>
    public static string Name(string name)
    {
        var t = T("name:" + name);
        return t.StartsWith("name:", StringComparison.Ordinal) ? name : t;
    }

    /// <summary>
    /// A format in the language of the program, filled; a number followed by a metric unit ("{0:0.0} m") is shown in the
    /// units of the program (<see cref="Qty.Formatted"/>).
    /// </summary>
    public static string F(string englishFormat, params object?[] args)
    {
        var (format, values) = Qty.Formatted(Raw(englishFormat), args);
        return string.Format(format, values);
    }

    /// <summary>A short name of a catalogue type: translated as "short:…", else as a name (the full one it falls back to).</summary>
    public static string Short(string english)
    {
        var t = T("short:" + english);
        return t.StartsWith("short:", StringComparison.Ordinal) ? T(english) : t;
    }

    /// <summary>Label of the work-stage shell (ribbon, rail, panels): translated as "ui:…" so short shell labels do not change menu translations.</summary>
    public static string Ui(string english)
    {
        var t = T("ui:" + english);
        return t.StartsWith("ui:", StringComparison.Ordinal) ? Qty.Caption(english) : t;
    }

    /// <summary>Display name of a model property: translated, or the property name split into words.</summary>
    public static string Property(string name)
    {
        var english = Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", " $1");
        english = char.ToUpperInvariant(english[0]) + english[1..].ToLowerInvariant();
        return T(english);
    }

    public static string Enum(object value)
    {
        var text = value.ToString() ?? "";
        // Flag combinations come back as "A, B": translate each part.
        return string.Join(", ", text.Split(", ").Select(Property));
    }
}
