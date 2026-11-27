using System.Text.RegularExpressions;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.Tests;

/// <summary>The problems of a charge column (<see cref="Charging.Issues"/>): what they say, and that each is translated.</summary>
public class ColumnIssueTests
{
    /// <summary>A hole 10 m deep, wet, with a deck of ANFO without a primer that overlaps the one above and goes 1.5 m below the toe.</summary>
    private static (BlastProject P, Hole H) Faulty()
    {
        var p = BlastProject.CreateNew();
        var stem = p.Products.First(x => x.Kind == ProductKind.Stemming).Id;
        var anfo = p.Products.First(x => x.Name == "ANFO");
        anfo.WaterResistant = false;
        var h = p.Add(new Hole { Collar = new Vec3(0, 0, 0), Length = 10, Diameter = 102, WaterDepth = 2 });
        h.Decks.Add(new Deck { Top = 0, Bottom = 3, ProductId = stem });
        h.Decks.Add(new Deck { Top = 2.5, Bottom = 11.5, ProductId = anfo.Id });
        return (p, h);
    }

    [Fact]
    public void ADeckBelowTheToeSaysWhereItEndsAndHowDeepTheHoleIs()
    {
        var (p, h) = Faulty();
        var issues = Charging.Issues(p, h);
        Assert.Equal(4, issues.Count);
        var below = issues[0];
        Assert.Equal(new object[] { 2, 11.5, 10.0 }, below.Args);
        Assert.Equal("Deck 2 goes below the toe: its bottom is at 11.5 m, the hole is 10 m deep", Charging.Validate(p, h)[0]);
        Assert.Single(issues, i => i.IsNoPrimer);
        // the dipped depth is the one checked
        h.ActualLength = 12;
        Assert.DoesNotContain(Charging.Issues(p, h), i => i.Format.Contains("below the toe"));
    }

    [Fact]
    public void EveryProblemOfAColumnIsTranslatedWithTheSameValues()
    {
        var (p, h) = Faulty();
        var root = Root();
        static string[] Holes(string s) => Regex.Matches(s, @"\{\d+(:[^}]*)?\}").Select(m => m.Value).Order().ToArray();
        foreach (var code in new[] { "ru", "kk", "fr" })
        {
            var table = File.ReadAllLines(Path.Combine(root, "src", "BlastDesigner.App", "Services", code + ".tsv"))
                .Select(l => l.Split('\t')).Where(x => x.Length >= 2).GroupBy(x => x[0]).ToDictionary(g => g.Key, g => g.First()[1]);
            foreach (var issue in Charging.Issues(p, h))
            {
                Assert.True(table.TryGetValue(issue.Format, out var tr), $"{code}: no translation: {issue.Format}");
                Assert.Equal(Holes(issue.Format), Holes(tr!));
            }
        }
    }

    private static string Root()
    {
        for (var dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "Directory.Build.props"))) return dir;
        var meta = typeof(ColumnIssueTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "RepositoryRoot")?.Value;
        return meta ?? throw new InvalidOperationException("repository root not found");
    }
}
