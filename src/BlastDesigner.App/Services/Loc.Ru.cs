using System.Reflection;

namespace BlastDesigner.App.Services;

public static partial class Loc
{
    /// <summary>Russian translations, loaded from the embedded ru.tsv (English TAB Russian; later lines win).</summary>
    private static readonly Dictionary<string, string> Ru = LoadTable("ru.tsv");

    private static Dictionary<string, string> LoadTable(string name)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + name, StringComparison.Ordinal));
        if (resource == null) return map;
        using var stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            map[line[..tab]] = line[(tab + 1)..];
        }
        return map;
    }

    /// <summary>English strings that have no translation (used by tests).</summary>
    public static bool HasTranslation(string english) => Ru.ContainsKey(english);
}
