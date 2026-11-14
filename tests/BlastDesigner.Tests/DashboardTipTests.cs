using System.Globalization;
using System.Text.RegularExpressions;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>The warnings of the dashboard say why they are raised and where to look further, in English and in Russian.</summary>
public class DashboardTipTests
{
    [Fact]
    public void EveryWarningHasATipWithItsNumbers()
    {
        var p = BlastProject.CreateNew();
        p.Design.Burden = 4;
        p.Design.Spacing = 5;
        p.Header.BlastOrientation = 0;
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                // the front hole of the second column too close to the row ahead
                p.Add(new Hole { Label = $"{r}-{c}", Collar = new Vec3(c * 5, -r * 4 + (r == 1 && c == 1 ? 2 : 0), 50), Length = 10 });
        var warnings = Analysis.Dashboard(p).Where(k => k.Warning).ToList();
        // no charges, no timing, one hole with a short burden
        Assert.Contains(warnings, k => k.Name == "Holes not loaded");
        Assert.Contains(warnings, k => k.Name == "Holes initiated");
        Assert.Contains(warnings, k => k.Name == "Low-burden holes");
        foreach (var k in warnings)
        {
            Assert.False(string.IsNullOrWhiteSpace(k.Tip), k.Name);
            var text = string.Format(CultureInfo.InvariantCulture, k.Tip!, k.TipArgs!);
            Assert.DoesNotContain("{", text);
        }
        var unloaded = warnings.Single(k => k.Name == "Holes not loaded");
        Assert.StartsWith("12 of 12 holes", string.Format(CultureInfo.InvariantCulture, unloaded.Tip!, unloaded.TipArgs!));
        var low = warnings.Single(k => k.Name == "Low-burden holes");
        Assert.Contains("less than 3.20 m (80 % of the design burden of 4.00 m)", string.Format(CultureInfo.InvariantCulture, low.Tip!, low.TipArgs!));
    }

    [Fact]
    public void EveryTipOfTheDashboardIsTranslatedWithTheSameValues()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Directory.Build.props"))) root = Path.GetDirectoryName(root)!;
        var source = File.ReadAllText(Path.Combine(root, "src", "BlastDesigner.Engine", "Analysis.cs"));
        var dashboard = source[source.IndexOf("public static List<Kpi> Dashboard", StringComparison.Ordinal)..];
        var tips = Regex.Matches(dashboard, @"^\s+""(.+)"",\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(7, tips.Count);
        var ru = File.ReadAllLines(Path.Combine(root, "src", "BlastDesigner.App", "Services", "ru.tsv"))
            .Select(l => l.Split('\t')).Where(x => x.Length >= 2).GroupBy(x => x[0]).ToDictionary(g => g.Key, g => g.First()[1]);
        static string[] Holes(string s) => Regex.Matches(s, @"\{\d+(:[^}]*)?\}").Select(m => m.Value).Order().ToArray();
        foreach (var tip in tips)
        {
            Assert.True(ru.TryGetValue(tip, out var tr), "no translation: " + tip);
            Assert.Equal(Holes(tip), Holes(tr!));
        }
    }
}
