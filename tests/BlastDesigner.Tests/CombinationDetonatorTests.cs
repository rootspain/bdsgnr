using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;

namespace BlastDesigner.Tests;

/// <summary>
/// The combination detonator: one unit with an in-hole delay that fires the
/// charge and a surface delay that passes the signal on along the tie to the next hole.
/// </summary>
public class CombinationDetonatorTests
{
    private const string Combo25 = "c3a9e6f1-6b2d-4f8a-9d41-2e7b5c0a8f13", Combo42 = "4e8d2b7a-91c5-4a3e-b6f0-8d1c9e2a5b47";

    private static BlastProject Reopened(BlastProject p)
    {
        using var ms = new MemoryStream();
        ProjectFile.Write(p, ms);
        ms.Position = 0;
        return ProjectFile.Read(ms);
    }

    /// <summary>A row of holes 5 m apart, each with one primer of the detonator at <paramref name="depth"/>, and a point before the first.</summary>
    private static (BlastProject P, List<Hole> Holes, InitiationPoint Ip) Row(BlastProject p, string[] detonators, double depth = 9)
    {
        var explosive = p.Products.First(x => x.IsExplosive).Id;
        var holes = detonators.Select((det, i) =>
        {
            var h = p.Add(new Hole { Label = (i + 1).ToString(), Collar = new Vec3(i * 5, 0, 100), Length = 10 });
            h.Decks.Add(new Deck { Top = 3, Bottom = 10, ProductId = explosive, Primers = { new Primer { Depth = depth, DetonatorId = det } } });
            return h;
        }).ToList();
        var ip = p.Add(new InitiationPoint { Position = new Vec3(-5, 0, 100) });
        return (p, holes, ip);
    }

    [Fact]
    public void TheKindHasItsRecord_AndTheBuiltInCatalogueHasExamples()
    {
        var p = BlastProject.CreateNew();
        var kind = Assert.Single(p.Catalogue.Kinds, k => k.Behaviour == ProductKind.CombinationDetonator);
        Assert.Equal(ProductCatalogue.KindId(ProductKind.CombinationDetonator), kind.Id);
        Assert.NotEqual(ProductCatalogue.KindId(ProductKind.Other), kind.Id);
        Assert.Contains(p.Catalogue.Types, t => t.Name == "Shock tube" && t.KindIds.Contains(kind.Id));
        var combo = Assert.Single(p.Products, x => x.Id == Combo25);
        Assert.Equal(kind.Id, combo.KindId);
        Assert.True(combo.IsDetonator);
        Assert.True(combo.IsTieProduct);
        Assert.True(TieFit.GoesOnTie(combo));
        Assert.Equal((500, 25), (combo.InHoleDelay, combo.SurfaceDelay));
        Assert.NotNull(combo.FamilyId);
    }

    /// <summary>
    /// Combination detonators 500/25 in a chain of three holes tied with 17 ms surface connectors: the signal leaves each
    /// hole after its surface block and the connector (25 + 17), each hole fires at the arrival plus the in-hole delay.
    /// </summary>
    [Fact]
    public void Solve_ChainOfThree_TheSurfaceBlockOfTheHoleIsAddedToTheConnector()
    {
        var p = BlastProject.CreateNew();
        var c17 = p.Products.First(x => x.Kind == ProductKind.SurfaceConnector && x.DelayMs == 17);
        var (_, holes, ip) = Row(p, new[] { Combo25, Combo25, Combo25 });
        Timing.Connect(p, ip.Id, holes[0].Id, null, 0);
        Timing.Connect(p, holes[0].Id, holes[1].Id, c17);
        Timing.Connect(p, holes[1].Id, holes[2].Id, c17);
        Timing.Solve(p);

        Assert.Equal(new double?[] { 0, 42, 84 }, holes.Select(h => h.SurfaceTimeMs).ToArray());
        Assert.Equal(new double?[] { 500, 542, 584 }, holes.Select(h => h.FiringTimeMs).ToArray());
        Assert.All(holes, h => Assert.Equal(500, h.InholeDelayMs));
        Assert.Equal(new int?[] { 1, 2, 3 }, holes.Select(h => h.FiringSequence).ToArray());
        // the tie itself keeps its own delay (labels, legend): the block is the hole's
        Assert.All(p.Ties.Where(t => t.ProductId == c17.Id), t => Assert.Equal(17, Timing.TieDelay(p, t)));
        // the point is no hole: the first tie adds no block
        Assert.Equal(25, Timing.HoleSurfaceDelay(p, holes[0].Id));
        Assert.Null(Timing.HoleSurfaceDelay(p, ip.Id));
    }

    [Fact]
    public void Solve_ATieWithoutAProductOutOfACombinationHole_AddsTheSurfaceBlockOnly()
    {
        var p = BlastProject.CreateNew();
        var (_, holes, ip) = Row(p, new[] { Combo25, Combo42, Combo25 });
        Timing.Connect(p, ip.Id, holes[0].Id, null, 0);
        Timing.Connect(p, holes[0].Id, holes[1].Id, null, 0);
        Timing.Connect(p, holes[1].Id, holes[2].Id, null, 0);
        Timing.Solve(p);

        Assert.Equal(new double?[] { 0, 25, 25 + 42 }, holes.Select(h => h.SurfaceTimeMs).ToArray());
        Assert.Equal(new double?[] { 500, 525, 567 }, holes.Select(h => h.FiringTimeMs).ToArray());
    }

    /// <summary>
    /// The block is counted once: a combination detonator on a tie out of a hole with one is that hole's lead and adds
    /// nothing; out of a hole with an ordinary detonator (or a point) it is the delay of the tie; a delay of the tie's own
    /// comes on top of the block.
    /// </summary>
    [Fact]
    public void Solve_ACombinationDetonatorOnTheTieToo_IsNotCountedTwice()
    {
        var p = BlastProject.CreateNew();
        var c25 = p.FindProduct(Combo25)!;
        var c42 = p.FindProduct(Combo42)!;
        var downhole = p.Products.First(x => x.Kind == ProductKind.DownholeDetonator && x.DelayMs == 500).Id;
        var (_, holes, ip) = Row(p, new[] { downhole, Combo25, Combo25, Combo25 });
        Timing.Connect(p, ip.Id, holes[0].Id, null, 0);
        Timing.Connect(p, holes[0].Id, holes[1].Id, c42);     // ordinary hole: the tie's 42
        Timing.Connect(p, holes[1].Id, holes[2].Id, c42);     // combination hole: its own 25 only, the tie is its lead
        Timing.Connect(p, holes[2].Id, holes[3].Id, c25, 9);  // a delay of its own: 25 + 9
        Timing.Solve(p);

        Assert.Equal(new double?[] { 0, 42, 67, 101 }, holes.Select(h => h.SurfaceTimeMs).ToArray());
        Assert.Equal(new double?[] { 500, 542, 567, 601 }, holes.Select(h => h.FiringTimeMs).ToArray());
    }

    [Fact]
    public void Solve_SeveralCombinationDetonatorsInAHole_TheShortestBlockSendsTheSignalOn()
    {
        var p = BlastProject.CreateNew();
        var c17 = p.Products.First(x => x.Kind == ProductKind.SurfaceConnector && x.DelayMs == 17);
        var (_, holes, ip) = Row(p, new[] { Combo42, Combo25 });
        holes[0].Decks[0].Primers.Add(new Primer { Depth = 5, DetonatorId = Combo25 });
        Assert.Equal(25, Timing.HoleSurfaceDelay(p, holes[0]));
        Timing.Connect(p, ip.Id, holes[0].Id, null, 0);
        Timing.Connect(p, holes[0].Id, holes[1].Id, c17);
        Timing.Solve(p);
        Assert.Equal(42, holes[1].SurfaceTimeMs);
    }

    /// <summary>The ties are turned from the initiation by the same times: the block of a hole counts there too.</summary>
    [Fact]
    public void OrientTies_CountsTheSurfaceBlock()
    {
        var p = BlastProject.CreateNew();
        var c17 = p.Products.First(x => x.Kind == ProductKind.SurfaceConnector && x.DelayMs == 17);
        var (_, holes, ip) = Row(p, new[] { Combo25, Combo25 });
        Timing.Connect(p, ip.Id, holes[0].Id, null, 0);
        // drawn the wrong way: from the second hole into the first
        Timing.Connect(p, holes[1].Id, holes[0].Id, c17);
        Assert.Equal(1, Timing.OrientTiesFromInitiation(p));
        Timing.Solve(p);
        Assert.Equal(42, holes[1].SurfaceTimeMs);
    }

    [Fact]
    public void Solve_TheRunDownTheTube_AndADelayOfThePrimer_CountForTheInHolePartOnly()
    {
        var p = BlastProject.CreateNew();
        var combo = p.FindProduct(Combo25)!;
        combo.PropagationMsPerM = 0.5;
        var (_, holes, ip) = Row(p, new[] { Combo25, Combo25, Combo25 }, depth: 8);
        // the second hole's primer has a delay of its own: it replaces the in-hole part, not the surface block
        Charging.Primers(holes[1]).Single().Primer.DelayMs = 400;
        Timing.Connect(p, ip.Id, holes[0].Id, null, 0);
        Timing.Connect(p, holes[0].Id, holes[1].Id, combo);
        Timing.Connect(p, holes[1].Id, holes[2].Id, combo);
        Timing.Solve(p);

        // the ties are combination detonators out of combination holes: the block of each hole counts once
        Assert.Equal(new double?[] { 0, 25, 50 }, holes.Select(h => h.SurfaceTimeMs).ToArray());
        Assert.Equal(new double?[] { 0 + 500 + 4, 25 + 400 + 4, 50 + 500 + 4 }, holes.Select(h => h.FiringTimeMs).ToArray());
    }

    [Fact]
    public void TieDelay_OfACombinationDetonatorIsItsSurfaceDelay_AndMixesWithConnectors()
    {
        var p = BlastProject.CreateNew();
        var c17 = p.Products.First(x => x.Kind == ProductKind.SurfaceConnector && x.DelayMs == 17);
        var combo = p.FindProduct(Combo42)!;
        // a downhole detonator in the first hole, a combination one in the others
        var downhole = p.Products.First(x => x.Kind == ProductKind.DownholeDetonator && x.DelayMs == 500).Id;
        var (_, holes, ip) = Row(p, new[] { downhole, Combo42, Combo42 });
        Timing.Connect(p, ip.Id, holes[0].Id, null, 0);
        Assert.Equal(17, Timing.TieDelay(p, Timing.Connect(p, holes[0].Id, holes[1].Id, c17)));
        Assert.Equal(42, Timing.TieDelay(p, Timing.Connect(p, holes[1].Id, holes[2].Id, combo)));
        // a delay of the tie's own wins over the product
        Assert.Equal(9, Timing.TieDelay(p, new TieEntity { FromId = holes[1].Id, ToId = holes[2].Id, ProductId = combo.Id, DelayMs = 9 }));
        Timing.Solve(p);
        Assert.Equal(new double?[] { 500, 517, 559 }, holes.Select(h => h.FiringTimeMs).ToArray());
        Assert.Null(TieFit.Refusal(p, holes[1].Id, holes[2].Id, combo));
    }

    [Fact]
    public void TheDelaysAreKeptInTheFile()
    {
        var p = BlastProject.CreateNew();
        var mine = new Product { Name = "HandiDet 475/17 ms", Family = "HandiDet", Kind = ProductKind.CombinationDetonator, InHoleDelayMs = 475, SurfaceDelayMs = 17, PropagationMsPerM = 0.3 };
        p.Products.Add(mine);
        var (_, holes, ip) = Row(p, new[] { mine.Id, mine.Id });
        Timing.Connect(p, ip.Id, holes[0].Id, null, 0);
        Timing.Connect(p, holes[0].Id, holes[1].Id, mine);

        var back = Reopened(p);
        var again = back.FindProduct(mine.Id)!;
        Assert.Equal(ProductKind.CombinationDetonator, again.Kind);
        Assert.Equal(ProductCatalogue.KindId(ProductKind.CombinationDetonator), again.KindId);
        Assert.Equal((475, 17, 0.3), (again.InHoleDelayMs, again.SurfaceDelayMs, again.PropagationMsPerM));
        Assert.Equal("HandiDet", again.Family);
        Assert.NotNull(again.FamilyId);
        Timing.Solve(back);
        var h = back.Holes.OrderBy(x => x.Label).ToList();
        Assert.Equal(new double?[] { 475 + 2.7, 17 + 475 + 2.7 }, h.Select(x => x.FiringTimeMs).ToArray());
        // the built-in examples are there once
        Assert.Single(Reopened(back).Products, x => x.Id == Combo25);
    }

    /// <summary>
    /// A file from before the kind: its own records of the shock tube type and the detonators group do not list it; it
    /// opens without errors, with the kind added to them, the built-in examples, and its times as before.
    /// </summary>
    [Fact]
    public void AnOldFileOpens_WithTheKindAddedToItsShockTubeTypeOnce()
    {
        var kindId = ProductCatalogue.KindId(ProductKind.CombinationDetonator);
        var old = BlastProject.CreateNew();
        old.Products.RemoveAll(x => x.Kind == ProductKind.CombinationDetonator);
        old.Catalogue.Kinds.RemoveAll(k => k.Id == kindId);
        old.Catalogue.Families.RemoveAll(f => f.KindId == kindId);
        foreach (var t in old.Catalogue.Types) t.KindIds.Remove(kindId);
        foreach (var g in old.Catalogue.Groups) g.KindIds.Remove(kindId);
        var c17 = old.Products.First(x => x.Kind == ProductKind.SurfaceConnector && x.DelayMs == 17);
        var downhole = old.Products.First(x => x.Kind == ProductKind.DownholeDetonator && x.DelayMs == 500).Id;
        var (_, holes, ip) = Row(old, new[] { downhole, downhole });
        Timing.Connect(old, ip.Id, holes[0].Id, null, 0);
        Timing.Connect(old, holes[0].Id, holes[1].Id, c17);

        var opened = Reopened(old);
        Assert.Single(opened.Catalogue.Kinds, k => k.Id == kindId);
        Assert.Contains(kindId, opened.Catalogue.Types.Single(t => t.Name == "Shock tube").KindIds);
        Assert.Contains(kindId, opened.Catalogue.Groups.Single(g => g.Name == "Detonators").KindIds);
        Assert.Contains(opened.Products, x => x.Id == Combo25);
        Timing.Solve(opened);
        Assert.Equal(new double?[] { 500, 517 }, opened.Holes.OrderBy(x => x.Label).Select(x => x.FiringTimeMs).ToArray());

        // the user takes it out of the type: a project saved since keeps that
        opened.Catalogue.Types.Single(t => t.Name == "Shock tube").KindIds.Remove(kindId);
        Assert.DoesNotContain(kindId, Reopened(opened).Catalogue.Types.Single(t => t.Name == "Shock tube").KindIds);
    }
}
