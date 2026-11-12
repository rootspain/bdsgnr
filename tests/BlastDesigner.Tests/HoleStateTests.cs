using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>The state of a hole is set by hand: emptying its column leaves it as it is, so a designed grid stays turnable.</summary>
public class HoleStateTests
{
    private static Hole Hole(HoleState state) => new()
    {
        Label = "2", Collar = new Vec3(0, 0, 10), Length = 10, State = state,
        Decks = { new Deck { Top = 3, Bottom = 10, ProductId = "anfo" } }
    };

    [Theory]
    [InlineData(HoleState.Designed)]
    [InlineData(HoleState.Released)]
    [InlineData(HoleState.Loaded)]
    public void EmptyLeavesTheState(HoleState state)
    {
        var h = Hole(state);
        Charging.Empty(new[] { h });
        Assert.Empty(h.Decks);
        Assert.Equal(state, h.State);
    }

    [Fact]
    public void ADesignedHoleWithAColumnLetsItsPatternBeLaidOutAgain()
    {
        var p = BlastProject.CreateNew("turn");
        var pat = new HolePattern { Name = "Pattern 1" };
        p.Patterns.Add(pat);
        var h = Hole(HoleState.Designed);
        h.PatternId = pat.Id;
        p.Add(h);
        Assert.Null(HoleDesign.RebuildRefusal(p, pat));
        h.State = HoleState.Loaded;
        Assert.NotNull(HoleDesign.RebuildRefusal(p, pat));
    }
}
