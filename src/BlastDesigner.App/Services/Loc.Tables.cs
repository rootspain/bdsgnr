using System.Reflection;
using BlastDesigner.IO;

namespace BlastDesigner.App.Services;

public static partial class Loc
{
    /// <summary>Languages with a table of their own: the embedded &lt;code&gt;.tsv (English TAB translation; later lines win).</summary>
    public static readonly string[] Translated = { "ru", "kk", "fr" };

    private static readonly Dictionary<string, Lazy<Dictionary<string, string>>> Tables =
        Translated.ToDictionary(c => c, c => new Lazy<Dictionary<string, string>>(() => LoadTable(c), LazyThreadSafetyMode.ExecutionAndPublication));

    /// <summary>Where the tables of a language are looked up, in order: Kazakh falls back to Russian, the others to English (the key).</summary>
    public static IReadOnlyList<string> Chain(string language) => language switch
    {
        "kk" => new[] { "kk", "ru" },
        "ru" => new[] { "ru" },
        "fr" => new[] { "fr" },
        _ => Array.Empty<string>()
    };

    /// <summary>Folder of the user's corrections of the translation: &lt;code&gt;.user.tsv, the same format as the embedded tables.</summary>
    public static string CorrectionsFolder => Path.Combine(ProgramSettings.AppDataFolder, "Translations");

    /// <summary>The user's file of corrections for a language.</summary>
    public static string CorrectionsFile(string language) => Path.Combine(CorrectionsFolder, language + ".user.tsv");

    /// <summary>The table of a language: the embedded one, then the user's corrections over it.</summary>
    private static Dictionary<string, string> LoadTable(string code)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + code + ".tsv", StringComparison.Ordinal));
        if (resource != null)
        {
            using var stream = asm.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            Read(reader, map);
        }
        try
        {
            var user = CorrectionsFile(code);
            if (File.Exists(user))
            {
                using var reader = new StreamReader(user, System.Text.Encoding.UTF8);
                Read(reader, map);
            }
        }
        catch (Exception) { /* an unreadable file of corrections is left out; the embedded table stays */ }
        return map;
    }

    private static void Read(TextReader reader, Dictionary<string, string> map)
    {
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1) continue;
            map[line[..tab]] = line[(tab + 1)..];
        }
    }

    private static Dictionary<string, string>? Table(string code) => Tables.TryGetValue(code, out var t) ? t.Value : null;

    /// <summary>The text in a language, by its chain of tables; null — no table has it.</summary>
    private static string? Lookup(string english, string language)
    {
        foreach (var code in Chain(language))
            if (Table(code) is { } t && t.TryGetValue(english, out var tr)) return tr;
        return null;
    }

    /// <summary>Whether the table of the language (the current one when not given) has the text; English always has it.</summary>
    public static bool HasTranslation(string english, string? language = null)
    {
        language ??= Language;
        return language == "en" || (Table(language)?.ContainsKey(english) ?? false);
    }

    /// <summary>
    /// Creates the folder of corrections with a file for the language, if there is none, that says how to correct a
    /// line; returns the folder.
    /// </summary>
    public static string PrepareCorrections(string language)
    {
        Directory.CreateDirectory(CorrectionsFolder);
        if (Translated.Contains(language) && !File.Exists(CorrectionsFile(language)))
            File.WriteAllText(CorrectionsFile(language),
                "# Corrections of the " + language + " translation of Blast Designer: English text <TAB> your translation, one per line.\n" +
                "# Copy the English text exactly as it is in the program's table (it is the key). The lines here replace the built-in ones\n" +
                "# after a restart. Send this file to the developers so the correction goes into the program.\n",
                new System.Text.UTF8Encoding(false));
        return CorrectionsFolder;
    }
}
