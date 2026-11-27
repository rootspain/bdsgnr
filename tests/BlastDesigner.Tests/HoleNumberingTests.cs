using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>
/// The Renumber tool: holes numbered in order from a counter, the prefix typed in or each hole's own, a number another hole
/// has swapped with it.
/// </summary>
public class HoleNumberingTests
{
    [Theory]
    [InlineData("B12-A7", "B12-A")]
    [InlineData("57", "")]
    [InlineData("A", "A")]
    [InlineData("", "")]
    [InlineData("A1B22", "A1B")]
    public void ThePrefixIsAllBeforeTheNumberALabelEndsWith(string label, string prefix) => Assert.Equal(prefix, HoleNumbering.PrefixOf(label));

    [Fact]
    public void WithNoPrefixEachHoleKeepsItsOwn_WithOneTheyAllTakeIt()
    {
        Assert.Equal("B12-A3", HoleNumbering.LabelFor("B12-A7", "", 3));
        Assert.Equal("3", HoleNumbering.LabelFor("57", null, 3));
        Assert.Equal("C3", HoleNumbering.LabelFor("57", "C", 3));
        Assert.Equal("C3", HoleNumbering.LabelFor("B12-A7", "C", 3));
    }

    [Fact]
    public void TheHolesAreNumberedInTheOrderGivenFromTheCounter()
    {
        var p = BlastProject.CreateNew();
        var holes = new[] { "A9", "A8", "B4" }.Select(l => p.Add(new Hole { Label = l })).ToList();
        var swaps = HoleNumbering.Apply(p, holes.Select(h => h.Id).ToList(), null, 5);
        // the row goes on across rows, each hole keeping its own prefix
        Assert.Equal(new[] { "A5", "A6", "B7" }, holes.Select(h => h.Label));
        Assert.Empty(swaps);
    }

    [Fact]
    public void ANumberAnotherHoleHasIsSwappedWithIt()
    {
        var p = BlastProject.CreateNew();
        var clicked = p.Add(new Hole { Label = "A7" });
        var holder = p.Add(new Hole { Label = "A3" });
        var swaps = HoleNumbering.Apply(p, new[] { clicked.Id }, "", 3);
        Assert.Equal(("A3", "A7"), (clicked.Label, holder.Label));
        Assert.Equal(new[] { ("A3", "A7") }, swaps);
    }
}
