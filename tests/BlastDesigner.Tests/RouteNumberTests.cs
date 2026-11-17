using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.Tests;

/// <summary>
/// Route numbers of the holes with an electronic detonator: their place in the chain of ties in the order the ties were
/// made, taken back from a tie deleted onwards; and no second tie into such a hole.
/// </summary>
public class RouteNumberTests
{
    static BlastProject Project() => BlastProject.CreateNew();

    static Hole H(BlastProject p, string label, double x, bool electronic = true)
    {
        var det = p.Products.First(d => d.Kind == (electronic ? ProductKind.ElectronicDetonator : ProductKind.DownholeDetonator)).Id;
        var h = new Hole
        {
            Collar = new Vec3(x, 0, 100), Label = label, Length = 10,
            Decks = { new Deck { Top = 3, Bottom = 10, ProductId = p.Products.First(e => e.IsExplosive).Id, Primers = { new Primer { Depth = 9.5, DetonatorId = det } } } }
        };
        p.Add(h);
        return h;
    }

    static void Tie(BlastProject p, Hole a, Hole b)
    {
        Timing.Connect(p, a.Id, b.Id, null);
        RouteNumbers.Number(p, new[] { (a.Id, b.Id) });
    }

    [Fact]
    public void AChainIsNumberedInTheOrderItIsTied_ANewChainFromOne()
    {
        var p = Project();
        var a = H(p, "a", 0); var b = H(p, "b", 5); var c = H(p, "c", 10); var d = H(p, "d", 20); var e = H(p, "e", 25);
        Tie(p, a, b);
        Tie(p, b, c);
        Assert.Equal(new int?[] { 1, 2, 3 }, new[] { a, b, c }.Select(h => h.RouteNumber));
        // a second chain, though there are ties already: from 1 again
        Tie(p, d, e);
        Assert.Equal(new int?[] { 1, 2 }, new[] { d, e }.Select(h => h.RouteNumber));
        // a branch from b: one more than b
        var f = H(p, "f", 7);
        Tie(p, b, f);
        Assert.Equal(3, f.RouteNumber);
    }

    [Fact]
    public void AHoleWithoutAnElectronicDetonatorIsNotNumbered_TheChainAfterItStartsFromOne()
    {
        var p = Project();
        var pyro = H(p, "p", 0, electronic: false);
        var a = H(p, "a", 5); var b = H(p, "b", 10);
        Tie(p, pyro, a);
        Tie(p, a, b);
        Assert.Null(pyro.RouteNumber);
        Assert.Equal(new int?[] { 1, 2 }, new[] { a, b }.Select(h => h.RouteNumber));
        // from an initiation point: the first hole is 1
        var ip = new InitiationPoint { Position = new Vec3(30, 0, 100) };
        p.Add(ip);
        var c = H(p, "c", 35);
        Timing.Connect(p, ip.Id, c.Id, null);
        RouteNumbers.Number(p, new[] { (ip.Id, c.Id) });
        Assert.Equal(1, c.RouteNumber);
    }

    [Fact]
    public void TiesMadeTogether_InAnyOrder_NumberAlongTheChain()
    {
        var p = Project();
        var a = H(p, "a", 0); var b = H(p, "b", 5); var c = H(p, "c", 10);
        Timing.Connect(p, b.Id, c.Id, null);
        Timing.Connect(p, a.Id, b.Id, null);
        RouteNumbers.Number(p, new[] { (b.Id, c.Id), (a.Id, b.Id) });
        Assert.Equal(new int?[] { 1, 2, 3 }, new[] { a, b, c }.Select(h => h.RouteNumber));
    }

    [Fact]
    public void ATieGoesOnlyToTheStartOfAChain_NeverIntoOne_NorIntoARing()
    {
        var p = Project();
        var a = H(p, "a", 0); var b = H(p, "b", 5); var c = H(p, "c", 10); var d = H(p, "d", 15);
        Tie(p, a, b);
        // a tie comes into b: no other one
        Assert.False(RouteNumbers.CanTie(p, c.Id, b.Id));
        // back between the two holes tied, or the same one again: a ring
        Assert.False(RouteNumbers.CanTie(p, b.Id, a.Id));
        Assert.False(RouteNumbers.CanTie(p, a.Id, b.Id));
        // on to a hole without ties: allowed
        Assert.True(RouteNumbers.CanTie(p, b.Id, d.Id));
        Tie(p, b, c);
        // from the last hole back to the first: a ring
        Assert.False(RouteNumbers.CanTie(p, c.Id, a.Id));
        // the last hole of another chain to the last hole of this one: a tie comes into it, whichever way its ties point
        var e = H(p, "e", 30); var f = H(p, "f", 35);
        Tie(p, e, f);
        Assert.False(RouteNumbers.CanTie(p, f.Id, c.Id));
        Timing.FlipTies(p.Ties.Where(t => t.FromId == b.Id && t.ToId == c.Id));
        Assert.False(RouteNumbers.CanTie(p, f.Id, c.Id));
        // the end of one chain to the start of another: allowed, and the other chain goes on from it
        Assert.True(RouteNumbers.CanTie(p, f.Id, a.Id));
        Tie(p, f, a);
        Assert.Equal(new int?[] { 1, 2, 3, 4, 5 }, new[] { e, f, a, b, c }.Select(h => h.RouteNumber));
        // no branch: from b, which a tie goes out of to c already, no tie to another hole
        var g = H(p, "g", 40);
        Assert.False(RouteNumbers.CanTie(p, b.Id, g.Id));
        Assert.Equal(RouteNumbers.Out, RouteNumbers.Refusal(p, b.Id, g.Id)!.Value.Format);
        // from the end of the chain: on
        Assert.True(RouteNumbers.CanTie(p, c.Id, g.Id));
        // a pyrotechnic hole takes as many as it is given
        var x = H(p, "x", 20, electronic: false); var y = H(p, "y", 25, electronic: false);
        Timing.Connect(p, x.Id, y.Id, null);
        Assert.True(RouteNumbers.CanTie(p, d.Id, y.Id));
    }

    [Fact]
    public void DeletingATieToAHoleWithoutANumber_LeavesTheHoleItCameFrom()
    {
        var p = Project();
        var a = H(p, "a", 0); var b = H(p, "b", 5); var c = H(p, "c", 10);
        var pyro = H(p, "p", 15, electronic: false);
        Tie(p, a, b); Tie(p, b, c);
        Timing.Connect(p, c.Id, pyro.Id, null);
        var doc = new Document(p);
        int? N(Hole h) => ((Hole)doc.Project.FindEntity(h.Id)!).RouteNumber;
        var tie = p.Ties.First(t => t.ToId == pyro.Id).Id;
        doc.Execute("Delete tie", pr => pr.Entities.RemoveAll(e => e.Id == tie));
        // the hole the tie came from, and the chain before it, keep their numbers
        Assert.Equal(new int?[] { 1, 2, 3 }, new[] { a, b, c }.Select(N));
    }

    [Fact]
    public void AHoleWithoutANumberTiedFromAChain_GoesOnFromIt_NotFromOne()
    {
        var p = Project();
        var a = H(p, "a", 0); var b = H(p, "b", 5); var c = H(p, "c", 10);
        Tie(p, a, b);
        // b lost its number (as a tie deleted after it once did), but a tie still comes into it from a
        b.RouteNumber = null;
        Tie(p, b, c);
        Assert.Equal(new int?[] { 1, 2, 3 }, new[] { a, b, c }.Select(h => h.RouteNumber));
    }

    [Fact]
    public void DeletingATie_TakesTheNumbersBackFromTheHolesAfterIt()
    {
        var p = Project();
        var a = H(p, "a", 0); var b = H(p, "b", 5); var c = H(p, "c", 10); var d = H(p, "d", 15);
        Tie(p, a, b); Tie(p, b, c); Tie(p, c, d);
        var doc = new Document(p);
        var tie = p.Ties.First(t => t.FromId == b.Id && t.ToId == c.Id).Id;
        doc.Execute("Delete tie", pr => pr.Entities.RemoveAll(e => e.Id == tie));
        int? N(Hole h) => ((Hole)doc.Project.FindEntity(h.Id)!).RouteNumber;
        Assert.Equal(new int?[] { 1, 2, null, null }, new[] { a, b, c, d }.Select(N));
        // one undo brings the tie and the numbers back
        doc.Undo();
        Assert.Equal(new int?[] { 1, 2, 3, 4 }, new[] { a, b, c, d }.Select(N));
        // a tie made again between the same holes, in one step, is no tie deleted
        doc.Execute("Tie again", pr => { Timing.Connect(pr, b.Id, c.Id, null); RouteNumbers.Number(pr, new[] { (b.Id, c.Id) }); });
        Assert.Equal(new int?[] { 1, 2, 3, 4 }, new[] { a, b, c, d }.Select(N));
    }
}
