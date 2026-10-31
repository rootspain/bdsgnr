using System.Text.RegularExpressions;

namespace BlastDesigner.IO;

/// <summary>
/// Release notes kept as Markdown in CHANGELOG.md (Russian) and CHANGELOG.en.md (English), embedded in this assembly.
/// The format is deliberately small: "## 0.1.7 — 2026-10-31" starts a release, "### Новое" a section, "- " an item
/// (an indented line continues the item); items may use **bold** and `code`.
/// </summary>
public static partial class Changelog
{
    public sealed record Section(string Title, List<string> Items);
    public sealed record Release(Version Version, string Date, List<Section> Sections);

    public const string RussianResource = "BlastDesigner.CHANGELOG.md";
    public const string EnglishResource = "BlastDesigner.CHANGELOG.en.md";

    /// <summary>Version of the running program (the assembly version set in Directory.Build.props).</summary>
    public static Version Current => Normalize(typeof(Changelog).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>The notes in the interface language (Russian for "ru", English otherwise), newest first.</summary>
    public static List<Release> Load(string language)
    {
        var asm = typeof(Changelog).Assembly;
        using var s = asm.GetManifestResourceStream(language == "ru" ? RussianResource : EnglishResource) ?? asm.GetManifestResourceStream(EnglishResource);
        if (s == null) return new();
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    [GeneratedRegex(@"^##\s+v?(\d+(?:\.\d+){1,3})\s*(?:[—–-]\s*(.*))?$")]
    private static partial Regex ReleaseHeading();

    public static List<Release> Parse(string markdown)
    {
        var releases = new List<Release>();
        Release? release = null;
        Section? section = null;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.Trim();
            if (t.StartsWith("## "))
            {
                section = null;
                var m = ReleaseHeading().Match(t);
                release = m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? new Release(Normalize(v), m.Groups[2].Value.Trim(), new()) : null;
                if (release != null) releases.Add(release);
            }
            else if (release == null) continue;
            else if (t.StartsWith("### "))
            {
                section = new Section(t[4..].Trim(), new());
                release.Sections.Add(section);
            }
            else if (t.StartsWith("- ") || t.StartsWith("* "))
            {
                if (section == null) release.Sections.Add(section = new Section("", new()));
                section.Items.Add(t[2..].Trim());
            }
            else if (t.Length > 0 && char.IsWhiteSpace(line[0]) && section is { Items.Count: > 0 })
                section.Items[^1] += " " + t;
        }
        return releases.OrderByDescending(r => r.Version).ToList();
    }

    /// <summary>Releases after <paramref name="seen"/> up to <paramref name="current"/>, newest first.</summary>
    public static List<Release> Since(IEnumerable<Release> all, Version seen, Version current) =>
        all.Where(r => r.Version > Normalize(seen) && r.Version <= Normalize(current)).OrderByDescending(r => r.Version).ToList();

    /// <summary>
    /// What to show on start: nothing on a new installation (no settings file yet) or when this version was already seen;
    /// after an update, the releases since the last version seen. Settings from before the notes existed count as 0.0.
    /// </summary>
    public static List<Release> ToShow(IEnumerable<Release> all, string? lastSeen, bool newInstallation, Version current)
    {
        if (newInstallation) return new();
        var seen = lastSeen != null && Version.TryParse(lastSeen, out var v) ? Normalize(v) : new Version(0, 0, 0);
        return seen >= Normalize(current) ? new() : Since(all, seen, current);
    }

    /// <summary>Major.minor.build: "0.1.7" and the assembly's "0.1.7.0" are the same version.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
