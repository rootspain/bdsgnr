using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using Xunit;

namespace BlastDesigner.Tests;

public class TieFitTests
{
    private static (BlastProject P, Product Esi, Product Tube, Product Downhole) Blast()
    {
        var p = BlastProject.CreateNew();
        return (p, p.Products.First(x => x.Kind == ProductKind.ElectronicDetonator),
            p.Products.First(x => x.Kind == ProductKind.SurfaceConnector),
            p.Products.First(x => x.Kind == ProductKind.DownholeDetonator));
    }

    private static Hole Hole(BlastProject p, double x, params Product[] detonators)
    {
        var h = p.Add(new Hole { Label = $"{x}", Collar = new Vec3(x, 0, 100), Length = 10 });
        var deck = new Deck { Top = 3, Bottom = 10, ProductId = p.Products.First(q => q.IsExplosive).Id };
        double depth = 9;
        foreach (var d in detonators) deck.Primers.Add(new Primer { Depth = depth--, DetonatorId = d.Id });
        h.Decks.Add(deck);
        return h;
    }

    [Fact]
    public void AnEsiTieGoesToHolesWithAnElectronicPrimer_AnotherToHolesWithAnotherPrimer()
    {
        var (p, esi, tube, downhole) = Blast();
        var e1 = Hole(p, 0, esi);
        var e2 = Hole(p, 5, esi);
        var s1 = Hole(p, 10, downhole);
        var mixed = Hole(p, 15, esi, downhole);
        var bare = p.Add(new Hole { Label = "bare", Collar = new Vec3(20, 0, 100), Length = 10 });
        var ip = p.Add(new InitiationPoint { Position = new Vec3(-5, 0, 100) });

        Assert.True(TieFit.Fits(p, e1.Id, e2.Id, esi));
        Assert.False(TieFit.Fits(p, e1.Id, e2.Id, tube));
        // a tie without a product, or with a delay of its own, is not ESI
        Assert.False(TieFit.Fits(p, e1.Id, e2.Id, null));
        Assert.Equal((TieFit.OnlyElectronic, e1.Label), TieFit.Refusal(p, e1.Id, e2.Id, tube));
        Assert.Equal((TieFit.NotElectronic, s1.Label), TieFit.Refusal(p, e1.Id, s1.Id, esi));
        // a mixed hole takes either; a hole not charged yet any
        Assert.True(TieFit.Fits(p, e1.Id, mixed.Id, esi));
        Assert.True(TieFit.Fits(p, s1.Id, mixed.Id, tube));
        Assert.True(TieFit.Fits(p, bare.Id, e1.Id, esi));
        Assert.True(TieFit.Fits(p, bare.Id, s1.Id, tube));
        // from an initiation point any, the hole asks for its own
        Assert.True(TieFit.Fits(p, ip.Id, e1.Id, esi));
        Assert.True(TieFit.Fits(p, ip.Id, s1.Id, tube));
        Assert.False(TieFit.Fits(p, ip.Id, e1.Id, tube));
        Assert.False(TieFit.Fits(p, ip.Id, s1.Id, esi));
    }

    [Fact]
    public void TheProductsOfATie_ByTheTypesOfTheCatalogue_ThoseThatFit()
    {
        var (p, esi, tube, downhole) = Blast();
        var e1 = Hole(p, 0, esi);
        var e2 = Hole(p, 5, esi);
        var s1 = Hole(p, 10, downhole);
        var s2 = Hole(p, 15, downhole);
        var electronic = p.Add(new TieEntity { FromId = e1.Id, ToId = e2.Id, ProductId = esi.Id });
        var shock = p.Add(new TieEntity { FromId = s1.Id, ToId = s2.Id, ProductId = tube.Id });

        var onShock = TieFit.Offered(p, new[] { shock });
        // no ESI, no explosive or downhole detonator; the cord and the bus line are there, each under its type
        Assert.DoesNotContain(onShock, o => o.Product.Kind == ProductKind.ElectronicDetonator);
        Assert.DoesNotContain(onShock, o => o.Product.IsExplosive || o.Product.Kind is ProductKind.Booster or ProductKind.DownholeDetonator);
        Assert.Contains(onShock, o => o.Product.Kind == ProductKind.DetonatingCord && ProductCatalogue.ShortNameOf(o.Type!) == "Det. cord");
        Assert.Contains(onShock, o => o.Product.Kind == ProductKind.HarnessWire && ProductCatalogue.ShortNameOf(o.Type!) == "BusLine");
        Assert.All(onShock, o => Assert.True(o.Fits));
        Assert.Single(onShock, o => o.Product.Id == tube.Id);
        // in the order of the types, the connectors of the shock tube by their delay
        var connectors = onShock.Where(o => o.Product.Kind == ProductKind.SurfaceConnector).Select(o => o.Product.DelayMs).ToList();
        Assert.Equal(connectors.OrderBy(d => d), connectors);
        Assert.True(onShock.FindIndex(o => o.Product.Kind == ProductKind.SurfaceConnector) < onShock.FindIndex(o => o.Product.Kind == ProductKind.DetonatingCord));

        // between electronic holes: ESI only, Davey Tronic among them
        var onEsi = TieFit.Offered(p, new[] { electronic });
        Assert.All(onEsi, o => Assert.Equal(ProductKind.ElectronicDetonator, o.Product.Kind));
        Assert.Contains(onEsi, o => o.Product.Name == "Davey Tronic" && ProductCatalogue.ShortNameOf(o.Type!) == "ESI");

        // both ties: nothing fits both but what they have, which stays, marked
        var both = TieFit.Offered(p, new[] { electronic, shock });
        Assert.Equal(new[] { tube.Id, esi.Id }.OrderBy(x => x), both.Select(o => o.Product.Id).OrderBy(x => x));
        Assert.All(both, o => Assert.False(o.Fits));
    }

    [Fact]
    public void AnOldProject_TakesEsiAndBusLine_ANameOfTheUsersStays()
    {
        var p = BlastProject.CreateNew();
        var esiType = p.Catalogue.Types.First(t => t.Name == "Electronic Initiation System");
        esiType.Name = "EBS detonators";
        esiType.ShortName = "EBS";
        var busType = p.Catalogue.Types.First(t => t.Name == "Bus line");
        busType.Name = "Harness wire";
        busType.ShortName = "Мой провод";
        p.Catalogue.Kinds.First(k => k.Behaviour == ProductKind.HarnessWire).Name = "Harness wire";
        var wire = p.Products.First(x => x.Kind == ProductKind.HarnessWire);
        wire.Name = "Harness wire";
        wire.Family = "Harness wire";
        p.ReportDesign = new ReportDesign { HiddenKinds = { "Hole" } };
        var json = System.Text.Encoding.UTF8.GetString(ProjectJson.Serialize(p));
        Assert.Contains("\"Hole\"]", json);
        var back = ProjectJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(json.Replace("\"Hole\"]", "\"Hole\",\"EBS opening point\"]")));
        ProductCatalogue.Attach(back, new ProductCatalogue(), Array.Empty<Product>());

        var t = back.Catalogue.Types.First(x => x.Id == esiType.Id);
        Assert.Equal(("Electronic Initiation System", "ESI"), (t.Name, t.ShortName));
        var b = back.Catalogue.Types.First(x => x.Id == busType.Id);
        Assert.Equal(("Bus line", "Мой провод"), (b.Name, b.ShortName));
        Assert.Equal("Bus line", back.Catalogue.Kinds.First(k => k.Behaviour == ProductKind.HarnessWire).Name);
        var w = back.Products.Single(x => x.Kind == ProductKind.HarnessWire);
        Assert.Equal(("Bus line", "Bus line"), (w.Name, w.Family));
        // the opening point hidden by its old kind name is hidden by its new one
        Assert.Contains(EbsOpeningPoint.Kind, back.ReportDesign!.HiddenKinds);
        Assert.DoesNotContain(EbsOpeningPoint.OldKind, back.ReportDesign.HiddenKinds);
    }
}
