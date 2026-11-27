using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace BlastDesigner.Tests;

/// <summary>
/// The Kazakh and French tables of the interface (kk.tsv, fr.tsv) against the Russian one (ru.tsv): every key translated,
/// no empty value, the placeholders {0}, {1:0.0} the same as in the English key. Short ui: labels much longer than the
/// English are only reported.
/// </summary>
public class LocalizationCoverageTests(ITestOutputHelper output)
{
    private static string Services()
    {
        for (var dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "Directory.Build.props"))) return Path.Combine(dir, "src", "BlastDesigner.App", "Services");
        var meta = typeof(LocalizationCoverageTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "RepositoryRoot")?.Value;
        if (meta != null && File.Exists(Path.Combine(meta, "Directory.Build.props"))) return Path.Combine(meta, "src", "BlastDesigner.App", "Services");
        throw new DirectoryNotFoundException("the repository is not above " + AppContext.BaseDirectory);
    }

    /// <summary>English key → translation; later lines win, as in the program.</summary>
    private static Dictionary<string, string> Table(string code)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(Services(), code + ".tsv")))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            map[line[..tab]] = line[(tab + 1)..];
        }
        return map;
    }

    private static readonly Regex Hole = new(@"\{\d+(:[^}]*)?\}", RegexOptions.Compiled);
    private static string[] Holes(string s) => Hole.Matches(s).Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("kk")]
    [InlineData("fr")]
    public void EveryKeyIsTranslatedWithTheSamePlaceholders(string code)
    {
        var ru = Table("ru");
        var table = Table(code);
        var missing = ru.Keys.Where(k => !table.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, $"{code}: {missing.Count} keys not translated, e.g. {string.Join(" | ", missing.Take(5))}");
        var empty = table.Where(p => p.Value.Trim().Length == 0).Select(p => p.Key).ToList();
        Assert.True(empty.Count == 0, $"{code}: empty values for {string.Join(" | ", empty.Take(5))}");
        var holes = table.Where(p => !Holes(p.Key).SequenceEqual(Holes(p.Value))).Select(p => p.Key).ToList();
        Assert.True(holes.Count == 0, $"{code}: placeholders differ in {string.Join(" | ", holes.Take(5))}");
        var extra = table.Keys.Where(k => !ru.ContainsKey(k)).ToList();
        Assert.True(extra.Count == 0, $"{code}: keys that ru.tsv does not have: {string.Join(" | ", extra.Take(5))}");

        var longer = table.Where(p => p.Key.StartsWith("ui:", StringComparison.Ordinal) && p.Value.Length > 1.3 * (p.Key.Length - 3) + 3).ToList();
        output.WriteLine($"{code}: {table.Count} keys translated; {longer.Count} ui: labels longer than 1.3 × English (warning only)");
        foreach (var p in longer.Take(20)) output.WriteLine($"  {p.Key[3..]} → {p.Value}");
    }

    [Theory]
    [InlineData("kk")]
    [InlineData("fr")]
    public void UnitsAtTheEndOfCaptionsStay(string code)
    {
        // a caption with a unit at its end (", m", ", kg/m³") keeps one: MeasureText.Caption converts it to the units of the program
        var unit = new Regex(@", (m|mm|cm|kg|t|ms|s|m²|m³|kg/m|kg/m³|t/m³|g/cm³|mm/s|m/s|MJ/kg|MJ/t|dB|°)$");
        var table = Table(code);
        var lost = table.Where(p => unit.IsMatch(p.Key) && !p.Value.Contains(", ", StringComparison.Ordinal)).Select(p => p.Key).ToList();
        Assert.True(lost.Count == 0, $"{code}: the unit at the end is lost in {string.Join(" | ", lost.Take(5))}");
    }
}
