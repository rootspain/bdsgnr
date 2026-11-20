using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.Tests;

/// <summary>The loaded holes the tie-up leaves out (the Unconnected tab of the Blast analysis window).</summary>
public class TieUpCheckTests
{
    enum Det { None, Tube, Electronic, Both }

    static Hole H(BlastProject p, string label, double x, Det det, bool charged = true)
    {
        var deck = new Deck { Top = 3, Bottom = 10, ProductId = charged ? p.Products.First(e => e.IsExplosive).Id : p.Products.First(e => e.Kind == ProductKind.Stemming).Id };
        string Of(ProductKind k) => p.Products.First(d => d.Kind == k).Id;
        if (det is Det.Tube or Det.Both) deck.Primers.Add(new Primer { Depth = 9.5, DetonatorId = Of(ProductKind.DownholeDetonator) });
        if (det is Det.Electronic or Det.Both) deck.Primers.Add(new Primer { Depth = 8, DetonatorId = Of(ProductKind.ElectronicDetonator) });
        var h = new Hole { Collar = new Vec3(x, 0, 100), Label = label, Length = 10, Decks = { deck } };
        p.Add(h);
        return h;
    }

    static string[] Labels(TieUpCheck.Result r) => r.Holes.Select(h => h.Label).ToArray();

    [Fact]
    public void AShockTubeHoleIsConnectedWhenTheSignalReachesIt_AnElectronicOneWhenATieComesToIt()
    {
        var p = BlastProject.CreateNew();
        var ip = new InitiationPoint { Position = new Vec3(-5, 0, 100) };
        p.Add(ip);
        var tube = H(p, "tube", 0, Det.Tube);
        var lost = H(p, "lost", 30, Det.Tube);
        var ebs = H(p, "ebs", 10, Det.Electronic);
        var alone = H(p, "alone", 40, Det.Electronic);
        alone.ElectronicTimeMs = 100;   // a programmed time alone does not connect it
        var empty = H(p, "empty", 50, Det.None, charged: false);
        var dummy = H(p, "dummy", 60, Det.Tube);
        dummy.IsDummy = true;
        Timing.Connect(p, ip.Id, tube.Id, null);
        Timing.Connect(p, tube.Id, ebs.Id, null);
        var r = TieUpCheck.Unconnected(p, primedOnly: false);
        Assert.Equal(new[] { "lost", "alone" }, Labels(r));
        Assert.Equal((4, 1, 1), (r.Checked, r.ShockTube, r.Electronic));
        Assert.DoesNotContain(empty, r.Holes);
    }

    [Fact]
    public void AHoleWithBothSystems_IsNotConnectedWhenEitherIsNot()
    {
        var p = BlastProject.CreateNew();
        var ip = new InitiationPoint { Position = new Vec3(-5, 0, 100) };
        p.Add(ip);
        var both = H(p, "both", 0, Det.Both);
        var tied = H(p, "tied", 10, Det.Both);
        // tied from a hole the signal does not reach: the electronic detonator has its tie, the shock tube no signal
        var source = H(p, "source", 20, Det.Electronic);
        Timing.Connect(p, source.Id, tied.Id, null);
        var r = TieUpCheck.Unconnected(p, primedOnly: false);
        Assert.Equal(new[] { "both", "tied" }, Labels(r));
        Assert.Equal((2, 1), (r.ShockTube, r.Electronic));
        // from the initiation point: both systems connected
        Timing.Connect(p, ip.Id, both.Id, null);
        Assert.Equal(new[] { "tied" }, Labels(TieUpCheck.Unconnected(p, primedOnly: false)));
    }

    [Fact]
    public void OnlyWithAPrimer_LeavesOutTheChargesWithoutOne()
    {
        var p = BlastProject.CreateNew();
        var bare = H(p, "bare", 0, Det.None);
        var other = H(p, "other", 10, Det.None);
        var primed = H(p, "primed", 20, Det.Tube);
        Assert.Equal(new[] { "bare", "other", "primed" }, Labels(TieUpCheck.Unconnected(p, primedOnly: false)));
        var only = TieUpCheck.Unconnected(p, primedOnly: true);
        Assert.Equal(new[] { "primed" }, Labels(only));
        Assert.Equal(1, only.Checked);
        // a charge without a primer with a tie is connected
        Timing.Connect(p, bare.Id, other.Id, null);
        Assert.Equal(new[] { "primed" }, Labels(TieUpCheck.Unconnected(p, primedOnly: false)));
    }

    [Fact]
    public void AnInitiationPointOnAHoleConnectsIt()
    {
        var p = BlastProject.CreateNew();
        var h = H(p, "on", 0, Det.Electronic);
        p.Add(new InitiationPoint { Position = h.Collar, HoleId = h.Id });
        Assert.Empty(TieUpCheck.Unconnected(p, primedOnly: true).Holes);
    }
}
