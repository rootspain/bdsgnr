using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>
/// Which decks of a column take a change of the hole's length (<see cref="Deck.Fits"/>): shared in proportion to their
/// lengths, the stemming only when the user marked it, the primers kept at their distance from the deck bottom, the marks
/// kept by the project file, by a rule saved from the column and by the column a rule builds.
/// </summary>
public class DeckFitTests
{
    private static readonly BlastProject P = BlastProject.CreateNew();

    private static string Id(string name) => P.Products.First(x => x.Name == name).Id;

    private static Primer Pr(double depth) => new()
    {
        Depth = depth,
        BoosterId = P.Products.First(x => x.Kind == ProductKind.Booster).Id,
        DetonatorId = P.Products.First(x => x.IsDetonator).Id
    };

    /// <summary>Stemming 0–3, ANFO 3–8 with a primer at 7.5, emulsion 8–11 with a primer at 10.6; marks as given.</summary>
    private static List<Deck> Column(bool? stem, bool? anfo, bool? emulsion) => new()
    {
        new() { Top = 0, Bottom = 3, ProductId = Id("Crushed aggregate"), Fits = stem },
        new() { Top = 3, Bottom = 8, ProductId = Id("ANFO"), Primers = { Pr(7.5) }, Fits = anfo },
        new() { Top = 8, Bottom = 11, ProductId = Id("Bulk emulsion"), Primers = { Pr(10.6) }, Fits = emulsion }
    };

    private static void Bounds(IReadOnlyList<Deck> decks, params double[] bottoms)
    {
        Assert.Equal(bottoms.Length, decks.Count);
        for (int i = 0; i < bottoms.Length; i++)
        {
            Assert.Equal(i == 0 ? 0 : bottoms[i - 1], decks[i].Top, 6);
            Assert.Equal(bottoms[i], decks[i].Bottom, 6);
        }
    }

    [Fact]
    public void AColumnWithNoMarksHasItsLongestExplosiveDeckTakeTheChangeNeverTheStemming()
    {
        Assert.Equal(Id("ANFO"), Charging.FitDecks(P, Column(null, null, null)).Single().ProductId);
        // a column of stemming only: nothing is chosen for the user
        Assert.Empty(Charging.FitDecks(P, new[] { new Deck { Top = 0, Bottom = 11, ProductId = Id("Crushed aggregate") } }));
        // once one deck has a mark, a deck with none keeps its length
        var marked = Column(null, null, true);
        Assert.Equal(Id("Bulk emulsion"), Charging.FitDecks(P, marked).Single().ProductId);
        Charging.MarkFits(P, marked);
        Assert.Equal(new bool?[] { false, false, true }, marked.Select(d => d.Fits));
    }

    [Fact]
    public void TheMarkedDeckInTheMiddleTakesTheChangeAndTheDecksBelowGoWithTheToe()
    {
        var longer = Charging.AdaptColumn(P, Column(false, true, false), 11, 13);
        Bounds(longer, 3, 10, 13);
        // the primers keep their distance from the bottom of their deck
        Assert.Equal(9.5, longer[1].Primers.Single().Depth, 6);
        Assert.Equal(12.6, longer[2].Primers.Single().Depth, 6);
        Bounds(Charging.AdaptColumn(P, Column(false, true, false), 11, 9), 3, 6, 9);
    }

    [Fact]
    public void TheBottomDeckMarkedTakesTheChangeAndTheMainChargeKeepsItsLength()
    {
        var longer = Charging.AdaptColumn(P, Column(false, false, true), 11, 12.5);
        Bounds(longer, 3, 8, 12.5);
        Assert.Equal(7.5, longer[1].Primers.Single().Depth, 6);
        Assert.Equal(12.1, longer[2].Primers.Single().Depth, 6);
    }

    [Fact]
    public void TwoMarkedDecksShareTheChangeInProportionToTheirLengths()
    {
        // 5 and 3 m, 1.6 m longer: 6 and 3.6 m
        Bounds(Charging.AdaptColumn(P, Column(false, true, true), 11, 12.6), 3, 9, 12.6);
        // 0.8 m shorter: 4.5 and 2.7 m
        Bounds(Charging.AdaptColumn(P, Column(false, true, true), 11, 10.2), 3, 7.5, 10.2);
    }

    [Fact]
    public void AMarkedStemmingGrowsAndTheChargesKeepTheirLengths()
    {
        var longer = Charging.AdaptColumn(P, Column(true, false, false), 11, 13);
        Bounds(longer, 5, 10, 13);
        Assert.Equal(9.5, longer[1].Primers.Single().Depth, 6);
    }

    [Fact]
    public void WithNoDeckMarkedTheColumnStaysAndTheToeOnlyCutsIt()
    {
        // longer: the column as it was, an empty part at the bottom
        Bounds(Charging.AdaptColumn(P, Column(false, false, false), 11, 13), 3, 8, 11);
        // shorter: the bottom deck cut by the toe
        Bounds(Charging.AdaptColumn(P, Column(false, false, false), 11, 10), 3, 8, 10);
    }

    [Fact]
    public void ShorterThanTheMarkedDeckCanGiveTheDeckKeepsATenthAndTheToeCutsTheRest()
    {
        // ANFO 5 m marked, the hole 6 m shorter: ANFO 0.1 m, the emulsion below cut by the toe at 5 m
        var shorter = Charging.AdaptColumn(P, Column(false, true, false), 11, 5);
        Bounds(shorter, 3, 3.1, 5);
    }

    [Fact]
    public void AHandLoadedHoleWhoseLengthChangesFollowsItsMarks()
    {
        var p = BlastProject.CreateNew();
        var h = p.Add(new Hole { Length = 11, Diameter = 102, ManualLoading = true });
        h.Decks = Column(false, false, true);
        h.Length = 12;
        Charging.ColumnToLength(p, h, 11);
        Bounds(h.Decks, 3, 8, 12);
        Assert.True(h.ManualLoading);
        Assert.Equal(new bool?[] { false, false, true }, h.Decks.Select(d => d.Fits));
    }

    [Fact]
    public void TheMarksAreKeptByTheFileAndAColumnFromAnOldFileHasNone()
    {
        var p = BlastProject.CreateNew();
        var h = p.Add(new Hole { Length = 11 });
        h.Decks = Column(true, false, true);
        var back = ProjectJson.DeepClone(p).Holes.Single();
        Assert.Equal(new bool?[] { true, false, true }, back.Decks.Select(d => d.Fits));
        // a file written before the marks: no "fits" in its decks
        h.Decks = Column(null, null, null);
        var json = System.Text.Json.JsonSerializer.Serialize(h, ProjectJson.Options);
        Assert.DoesNotContain("\"fits\"", json);
        var old = System.Text.Json.JsonSerializer.Deserialize<Hole>(json, ProjectJson.Options)!;
        Assert.All(old.Decks, d => Assert.Null(d.Fits));
        Assert.Equal(Id("ANFO"), Charging.FitDecks(p, old.Decks).Single().ProductId);
    }

    [Fact]
    public void ARuleSavedFromTheColumnBuildsItOnEveryLengthAsTheColumnWouldAdaptWithItsMarks()
    {
        foreach (var column in new[] { Column(false, true, true), Column(true, false, false), Column(true, true, false) })
        {
            var rule = ProjectJson.DeepClone(Charging.RuleFromColumn(P, column, "Saved"));
            Assert.Equal(column[0].Fits, rule.StemmingFits);
            foreach (var length in new[] { 11.0, 13.5, 9.0 })
            {
                var h = new Hole { Length = length, Diameter = 102 };
                Charging.ApplyRule(P, h, rule);
                var expected = Charging.AdaptColumn(P, column, 11, length);
                Assert.Equal(expected.Count, h.Decks.Count);
                for (int i = 0; i < expected.Count; i++)
                {
                    Assert.Equal(expected[i].ProductId, h.Decks[i].ProductId);
                    Assert.Equal(expected[i].Top, h.Decks[i].Top, 6);
                    Assert.Equal(expected[i].Bottom, h.Decks[i].Bottom, 6);
                    Assert.Equal(expected[i].Primers.Select(x => Math.Round(x.Depth, 6)), h.Decks[i].Primers.Select(x => Math.Round(x.Depth, 6)));
                    // the column the rule builds is marked as the one it was saved from
                    Assert.Equal(column[i].Fits, h.Decks[i].Fits);
                }
            }
        }
    }

    [Fact]
    public void AFillOfAnOlderRuleIsTheMarkedDeckOfTheColumnItBuilds()
    {
        var p = BlastProject.CreateNew();
        var h = p.Add(new Hole { Length = 11, Diameter = 102 });
        Charging.ApplyRules(p, new[] { h });
        var fill = p.LoadingRules.First().Decks.Single(t => t.Length <= 0);
        Assert.Equal(fill.ProductId, Charging.FitDecks(p, h.Decks).Single().ProductId);
        Assert.All(h.Decks.Where(d => d.ProductId != fill.ProductId), d => Assert.False(d.Fits));
    }

    [Fact]
    public void AColumnCopiedToALongerHoleStretchesTheMarkedDecks()
    {
        // the copy to the selected holes goes through AdaptColumn with the template's marks
        var copy = Charging.AdaptColumn(P, Column(false, true, true), 11, 19);
        Bounds(copy, 3, 13, 19);
        Assert.Equal(new bool?[] { false, true, true }, copy.Select(d => d.Fits));
    }
}
