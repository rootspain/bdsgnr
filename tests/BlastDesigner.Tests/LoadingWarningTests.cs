using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>The loading warnings of a hole by kind: what the exclamation mark over the collar stands for.</summary>
public class LoadingWarningTests
{
    private static (BlastProject P, Product Anfo, Product Stem, Product Booster) Project()
    {
        var p = BlastProject.CreateNew();
        return (p, p.Products.First(x => x.Name == "ANFO"), p.Products.First(x => x.Kind == ProductKind.Stemming), p.Products.First(x => x.Kind == ProductKind.Booster));
    }

    /// <summary>A 12 m hole: 3 m of stemming, then explosive to <paramref name="bottom"/>, with or without a primer.</summary>
    private static Hole Charged(BlastProject p, Product anfo, Product stem, Product booster, double bottom = 12, bool primer = true, double stemming = 3)
    {
        var h = p.Add(new Hole { Collar = new Vec3(p.Holes.Count() * 5, 0, 0), Length = 12, Diameter = 102 });
        h.Decks.Add(new Deck { Top = 0, Bottom = stemming, ProductId = stem.Id });
        var d = new Deck { Top = stemming, Bottom = bottom, ProductId = anfo.Id };
        if (primer) d.Primers.Add(new Primer { Depth = bottom - 0.5, BoosterId = booster.Id });
        h.Decks.Add(d);
        return h;
    }

    [Fact]
    public void EachTroubleHasItsKind_AndAnyOfThemMarksTheHole()
    {
        var (p, anfo, stem, booster) = Project();
        var good = Charged(p, anfo, stem, booster);
        var noPrimer = Charged(p, anfo, stem, booster, primer: false);
        var gap = Charged(p, anfo, stem, booster, bottom: 10);
        var below = Charged(p, anfo, stem, booster, bottom: 13);
        var shortStem = Charged(p, anfo, stem, booster, stemming: 1);
        Assert.Empty(Charging.LoadingWarnings(p, good));
        Assert.False(Charging.HasLoadingWarning(p, good));
        Assert.Equal(new[] { LoadingWarning.NoPrimer }, Charging.LoadingWarnings(p, noPrimer));
        Assert.Contains(LoadingWarning.ColumnGap, Charging.LoadingWarnings(p, gap));
        Assert.Contains(LoadingWarning.BelowToe, Charging.LoadingWarnings(p, below));
        Assert.Contains(LoadingWarning.ShortStemming, Charging.LoadingWarnings(p, shortStem));
        Assert.All(new[] { noPrimer, gap, below, shortStem }, h => Assert.True(Charging.HasLoadingWarning(p, h)));
        // an uncharged hole has nothing to warn about
        Assert.Empty(Charging.LoadingWarnings(p, p.Add(new Hole { Length = 10 })));
    }
}
