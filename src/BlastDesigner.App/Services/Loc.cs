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

    public static string T(string english)
    {
        if (Language == "ru" && Ru.TryGetValue(english, out var ru)) return ru;
        return english;
    }

    /// <summary>Display name of user data that may be a standard name (layers, groups): translated only when listed as "name:…".</summary>
    public static string Name(string name)
    {
        var t = T("name:" + name);
        return t.StartsWith("name:", StringComparison.Ordinal) ? name : t;
    }

    public static string F(string englishFormat, params object?[] args) => string.Format(T(englishFormat), args);

    /// <summary>Label of the work-stage shell (ribbon, rail, panels): translated as "ui:…" so short shell labels do not change menu translations.</summary>
    public static string Ui(string english)
    {
        var t = T("ui:" + english);
        return t.StartsWith("ui:", StringComparison.Ordinal) ? english : t;
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
