using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;
using BlastDesigner.IO;

namespace BlastDesigner.Tests;

public class ProductCatalogueTests
{
    private static CatalogueType Type(ProductCatalogue c, string name) => c.Types.First(t => t.Name == name);
    private static CatalogueKind Kind(ProductCatalogue c, ProductKind k) => c.Kinds.First(x => x.Behaviour == k);

    [Fact]
    public void BuiltIn_HasFixedTextIdsAndEveryKindOfTheProgram()
    {
        var c = ProductCatalogue.BuiltIn();
        var ids = c.Types.Select(t => t.Id).Concat(c.Kinds.Select(k => k.Id)).Concat(c.Groups.Select(g => g.Id)).Concat(c.Families.Select(f => f.Id)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var id in ids)
        {
            Assert.True(Guid.TryParse(id, out _), id);
            Assert.Equal(id.ToLowerInvariant(), id);
            Assert.Equal('4', id[14]);
        }
        Assert.Equal(ids, new[] { ProductCatalogue.BuiltIn() }.SelectMany(b => b.Types.Select(t => t.Id).Concat(b.Kinds.Select(k => k.Id)).Concat(b.Groups.Select(g => g.Id)).Concat(b.Families.Select(f => f.Id))));
        foreach (var k in Enum.GetValues<ProductKind>())
            Assert.Equal(ProductCatalogue.KindId(k), Kind(c, k).Id);
        Assert.True(Type(c, "User defined").UserDelay);
        // the kinds of the shock tube as the initiation catalogue lists them
        Assert.Equal(new[] { ProductKind.SurfaceConnector, ProductKind.DownholeDetonator, ProductKind.Other }.Select(ProductCatalogue.KindId), Type(c, "Shock tube").KindIds);
        Assert.All(c.Groups.SelectMany(g => g.KindIds).Concat(c.Types.SelectMany(t => t.KindIds)).Concat(c.Families.Select(f => f.KindId)), id => Assert.Contains(c.Kinds, k => k.Id == id));
    }

    [Fact]
    public void NewId_IsUuidV4Text()
    {
        var id = ProductCatalogue.NewId();
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", id);
        Assert.NotEqual(id, ProductCatalogue.NewId());
    }

    [Fact]
    public void DeletingAType_TakesItsKindsAndTheirFamilies_WithAWarningCount()
    {
        var c = ProductCatalogue.BuiltIn();
        var type = Type(c, "Cord delay");
        var kind = Kind(c, ProductKind.CordRelay);
        Assert.Equal((1, 1, 0), c.Dependents(type));
        c.Delete(type);
        Assert.True(type.Deleted);
        Assert.True(kind.Deleted);
        Assert.True(c.Families.First(f => f.KindId == kind.Id).Deleted);
        // nothing is removed
        Assert.Contains(c.Kinds, k => k.Id == kind.Id);
    }

    [Fact]
    public void DeletingAType_LeavesAKindAnotherTypeLists_ButTakesItOutOfThisType()
    {
        var c = ProductCatalogue.BuiltIn();
        var relay = Kind(c, ProductKind.CordRelay);
        var cord = Type(c, "Detonating cord system");
        cord.KindIds.Add(relay.Id);
        var delay = Type(c, "Cord delay");
        Assert.Equal((0, 0, 0), c.Dependents(delay));
        c.Delete(delay);
        Assert.False(relay.Deleted);
        Assert.DoesNotContain(relay.Id, delay.KindIds);
        Assert.Contains(relay.Id, cord.KindIds);
    }

    [Fact]
    public void Groups_StayOutOfTheHierarchy()
    {
        var c = ProductCatalogue.BuiltIn();
        var group = c.Groups.First(g => g.Name == "Detonating cord");
        var cord = Kind(c, ProductKind.DetonatingCord);
        c.Delete(cord);
        Assert.False(group.Deleted);
        Assert.DoesNotContain(cord.Id, group.KindIds);
        c.Delete(group);
        Assert.False(Kind(c, ProductKind.CordRelay).Deleted);
    }

    [Fact]
    public void CanRemove_OnlyANewRecordTiedToNothing()
    {
        var c = ProductCatalogue.BuiltIn();
        var none = new List<Product>();
        var kind = new CatalogueKind { IsNew = true };
        c.Kinds.Add(kind);
        Assert.True(c.CanRemove(kind, none));
        // a record that is not new is deleted logically
        Assert.False(c.CanRemove(Kind(c, ProductKind.Other), none));
        var group = new CatalogueGroup { IsNew = true };
        c.Groups.Add(group);
        Assert.True(c.CanRemove(group, none));
        group.KindIds.Add(kind.Id);
        Assert.False(c.CanRemove(group, none));
        Assert.False(c.CanRemove(kind, none));
        group.KindIds.Clear();
        var type = new CatalogueType { IsNew = true, KindIds = { kind.Id } };
        c.Types.Add(type);
        Assert.False(c.CanRemove(type, none));
        Assert.False(c.CanRemove(kind, none));
        type.KindIds.Clear();
        var family = new CatalogueFamily { IsNew = true, KindId = kind.Id };
        c.Families.Add(family);
        Assert.True(c.CanRemove(family, none));
        Assert.False(c.CanRemove(kind, none));
        var product = new Product { IsNew = true, FamilyId = family.Id };
        Assert.False(c.CanRemove(family, new[] { product }));
        Assert.False(ProductCatalogue.CanRemove(product));
        c.Remove(family);
        Assert.True(c.CanRemove(kind, none));
        Assert.False(c.CanRemove(kind, new[] { new Product { KindId = kind.Id } }));
        c.Remove(kind);
        Assert.DoesNotContain(kind, c.Kinds);
        Assert.True(ProductCatalogue.CanRemove(new Product { IsNew = true }));
        Assert.False(ProductCatalogue.CanRemove(new Product { IsNew = true, Family = "Mine" }));
        Assert.False(ProductCatalogue.CanRemove(new Product()));
    }

    [Fact]
    public void IsNew_IsNotStored_AndEndsWhenApplied()
    {
        var c = new ProductCatalogue { Kinds = { new CatalogueKind { IsNew = true } } };
        var product = new Product { IsNew = true };
        Assert.False(ProjectJson.DeepClone(c).Kinds[0].IsNew);
        Assert.False(ProjectJson.DeepClone(product).IsNew);
        c.Accept(new[] { product });
        Assert.False(c.Kinds[0].IsNew);
        Assert.False(product.IsNew);
    }

    [Fact]
    public void UnassignedKinds_AreThoseNoGroupLists()
    {
        var c = ProductCatalogue.BuiltIn();
        Assert.Empty(c.UnassignedKinds);
        var mine = new CatalogueKind { Name = "Mine", Order = 99 };
        c.Kinds.Add(mine);
        Assert.Equal(new[] { mine }, c.UnassignedKinds);
        // a deleted group lists nothing, a deleted kind is not offered
        c.Delete(c.Groups.First(g => g.Name == "Stemming"));
        Assert.Contains(Kind(c, ProductKind.Stemming), c.UnassignedKinds);
        c.Delete(mine);
        Assert.DoesNotContain(mine, c.UnassignedKinds);
    }

    [Fact]
    public void Restoring_BringsBackTheParentOnly_AndAChildNeedsItsParent()
    {
        var c = ProductCatalogue.BuiltIn();
        var type = Type(c, "Cord delay");
        var kind = Kind(c, ProductKind.CordRelay);
        var family = c.Families.First(f => f.KindId == kind.Id);
        c.Delete(type);
        Assert.False(c.CanRestore(kind));
        Assert.False(c.CanRestore(family));
        type.Deleted = false;
        Assert.True(kind.Deleted);
        Assert.True(family.Deleted);
        Assert.True(c.CanRestore(kind));
        kind.Deleted = false;
        Assert.True(c.CanRestore(family));
        // a kind no type lists hangs on nothing
        var bulk = Kind(c, ProductKind.BulkExplosive);
        c.Delete(bulk);
        Assert.True(c.CanRestore(bulk));
    }

    [Fact]
    public void Layered_ProjectOverUserOverBuiltIn_DeletionByTheUserCountsInTheProject()
    {
        var builtIn = ProductCatalogue.BuiltIn();
        var kindId = ProductCatalogue.KindId(ProductKind.Stemming);
        var mine = new CatalogueKind { Name = "Mine", Behaviour = ProductKind.Stemming, Order = 50 };
        var user = new ProductCatalogue { Kinds = { new CatalogueKind { Id = kindId, Name = "User name", Behaviour = ProductKind.Stemming }, mine } };
        var project = new ProductCatalogue { Kinds = { new CatalogueKind { Id = kindId, Name = "Project name", Behaviour = ProductKind.Stemming } } };
        var c = ProductCatalogue.Layered(builtIn, user, project);
        Assert.Equal("Project name", c.Kinds.First(k => k.Id == kindId).Name);
        Assert.Contains(c.Kinds, k => k.Id == mine.Id);
        Assert.Equal(builtIn.Kinds.Count + 1, c.Kinds.Count);
        // deleted at the user: deleted in the project too, whatever the project says
        user.Kinds[0].Deleted = true;
        Assert.False(project.Kinds[0].Deleted);
        Assert.True(ProductCatalogue.Layered(builtIn, user, project).Kinds.First(k => k.Id == kindId).Deleted);
        // deleted in a project only stays in that project
        project.Kinds[0].Deleted = true;
        user.Kinds[0].Deleted = false;
        Assert.True(ProductCatalogue.Layered(builtIn, user, project).Kinds.First(k => k.Id == kindId).Deleted);
        // the layers are left as they were
        Assert.Equal("User name", user.Kinds[0].Name);
    }

    [Fact]
    public void BuiltInProducts_HaveFixedIdsAndTheirKindsAndFamilies()
    {
        var first = BlastProject.CreateNew();
        var second = BlastProject.CreateNew();
        Assert.Equal(first.Products.Select(p => p.Id), second.Products.Select(p => p.Id));
        Assert.All(first.Products, p => Assert.Equal('4', p.Id[14]));
        var connector = first.Products.First(p => p.Kind == ProductKind.SurfaceConnector);
        Assert.Equal(ProductCatalogue.KindId(ProductKind.SurfaceConnector), connector.KindId);
        Assert.Equal("Surface connector", first.Catalogue.Families.First(f => f.Id == connector.FamilyId).Name);
        // ANFO stands outside the initiation catalogue
        Assert.Null(first.Products.First(p => p.Name == "ANFO").FamilyId);
    }

    /// <summary>A file of schema 1: products with kinds and family names on them, no tables, only part of the catalogue.</summary>
    [Fact]
    public void OldProject_GetsTheTables_KeepsItsProducts_AndTheMissingOnesComeIn()
    {
        var oldId = Guid.NewGuid().ToString();
        var p = new BlastProject { Products =
        {
            new Product { Id = oldId, Name = "Surface connector 17 ms", Kind = ProductKind.SurfaceConnector, DelayMs = 17 },
            new Product { Name = "Nonel 42", Family = "Nonel", Kind = ProductKind.SurfaceConnector, DelayMs = 42 },
            new Product { Name = "ANFO", Kind = ProductKind.BulkExplosive }
        } };
        p.Add(new TieEntity { ProductId = oldId });
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(p));
        ProductCatalogue.Attach(back, new ProductCatalogue(), Array.Empty<Product>());
        // a product of the program under an old id stays one product
        Assert.Single(back.Products, x => x.Name == "Surface connector 17 ms");
        Assert.Single(back.Products, x => x.Name == "ANFO");
        Assert.Equal(oldId, back.Ties.Single().ProductId);
        Assert.NotNull(back.FindProduct(oldId));
        // what the old catalogue lacked, the family named by the project
        Assert.Contains(back.Products, x => x.Kind == ProductKind.CordRelay);
        Assert.Contains(back.Catalogue.Families, f => f.Name == "Nonel" && f.KindId == ProductCatalogue.KindId(ProductKind.SurfaceConnector));
        Assert.Equal(back.Products.First(x => x.Name == "Nonel 42").FamilyId, back.Catalogue.Families.First(f => f.Name == "Nonel").Id);
        var count = back.Products.Count;
        var families = back.Catalogue.Families.Count;
        ProductCatalogue.Attach(back, new ProductCatalogue(), Array.Empty<Product>());
        Assert.Equal((count, families), (back.Products.Count, back.Catalogue.Families.Count));
    }

    [Fact]
    public void DeletingAKindOrFamily_TakesTheirProducts_WhichComeBackOneByOne()
    {
        var p = BlastProject.CreateNew();
        var c = p.Catalogue;
        var kind = Kind(c, ProductKind.CordRelay);
        var family = c.Families.First(f => f.KindId == kind.Id);
        Assert.Equal((1, 3), c.Dependents(kind, p.Products));
        Assert.Equal(3, c.Dependents(family, p.Products));
        c.Delete(kind, p.Products);
        var relay = p.Products.First(x => x.Kind == ProductKind.CordRelay);
        Assert.True(relay.Deleted);
        Assert.DoesNotContain(relay, p.ActiveProducts);
        Assert.Same(relay, p.FindProduct(relay.Id));
        Assert.False(c.CanRestore(relay));
        kind.Deleted = false;
        Assert.False(c.CanRestore(relay));
        family.Deleted = false;
        Assert.True(c.CanRestore(relay));
        Assert.True(relay.Deleted);
    }

    [Fact]
    public void Attach_ADeletionByTheUserReachesTheProducts_AndAProductOfTheUserComesIn()
    {
        var p = BlastProject.CreateNew();
        var anfo = p.Products.First(x => x.Name == "ANFO");
        var gone = anfo.Clone();
        gone.Deleted = true;
        var mine = new Product { Name = "Mine", Kind = ProductKind.Booster };
        ProductCatalogue.Attach(p, new ProductCatalogue(), new[] { gone, mine });
        Assert.True(anfo.Deleted);
        Assert.Contains(p.Products, x => x.Id == mine.Id);
    }

    [Fact]
    public void Product_KeepsACustomKind_AndCalculatesAsItsBehaviour()
    {
        var p = BlastProject.CreateNew();
        var kind = new CatalogueKind { Name = "Plasma", Behaviour = ProductKind.Booster };
        p.Catalogue.Kinds.Add(kind);
        var product = new Product { Name = "Own" };
        product.SetKind(kind);
        p.Products.Add(product);
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(p));
        var own = back.Products.First(x => x.Name == "Own");
        Assert.Equal((kind.Id, ProductKind.Booster), (own.KindId, own.Kind));
        // the behaviour of the record leads
        back.Catalogue.Kinds.First(k => k.Id == kind.Id).Behaviour = ProductKind.Stemming;
        ProductCatalogue.Link(back);
        Assert.Equal(ProductKind.Stemming, own.Kind);
        Assert.Equal(kind.Id, own.KindId);
    }

    [Fact]
    public void UserCatalogue_RemembersWhatChanged_AndReadsItBack()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var before = BlastProject.CreateNew();
            var now = ProjectJson.DeepClone(before);
            now.Products.First(x => x.Name == "ANFO").Cost = 9;
            now.Catalogue.Delete(now.Catalogue.Groups.First(g => g.Name == "Other"));
            var added = new CatalogueFamily { KindId = ProductCatalogue.KindId(ProductKind.Booster), Name = "Mine" };
            now.Catalogue.Families.Add(added);
            var mine = UserCatalogue.Load(path);
            Assert.Empty(mine.Products);
            mine.Remember(before.Catalogue, before.Products, now.Catalogue, now.Products);
            mine.Save(path);
            var back = UserCatalogue.Load(path);
            Assert.Equal(9, back.Products.Single().Cost);
            Assert.True(back.Tables.Groups.Single().Deleted);
            Assert.Equal(added.Id, back.Tables.Families.Single().Id);
            Assert.Empty(back.Tables.Kinds);
            // a second change of the same record replaces it
            var later = ProjectJson.DeepClone(now);
            later.Products.First(x => x.Name == "ANFO").Cost = 11;
            back.Remember(now.Catalogue, now.Products, later.Catalogue, later.Products);
            Assert.Equal(11, back.Products.Single().Cost);
            File.WriteAllText(path, "not json");
            Assert.Empty(UserCatalogue.Load(path).Products);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UsedProducts_FindsHolesTiesAndRules()
    {
        var p = BlastProject.CreateNew();
        p.Add(new TieEntity { ProductId = "tie" });
        p.Add(new Hole { Decks = { new Deck { ProductId = "deck", Primers = { new Primer { BoosterId = "booster", DetonatorId = "det" } } } } });
        p.LoadingRules.Add(new LoadingRule { StemmingProductId = "stem" });
        p.InitiationRules.Add(new InitiationRule { DetonatorId = "rule" });
        Assert.Superset(new HashSet<string> { "tie", "deck", "booster", "det", "stem", "rule" }, ProductCatalogue.UsedProducts(p));
    }

    [Fact]
    public void Catalogue_RoundTripsThroughJson()
    {
        var c = ProductCatalogue.BuiltIn();
        c.Delete(Type(c, "Cord delay"));
        var back = ProjectJson.DeepClone(c);
        Assert.Equal(c.Kinds.Select(k => (k.Id, k.Name, k.Behaviour, k.Deleted)), back.Kinds.Select(k => (k.Id, k.Name, k.Behaviour, k.Deleted)));
        Assert.Equal(c.Types.Select(t => t.KindIds.Count), back.Types.Select(t => t.KindIds.Count));
        Assert.True(back.Types.First(t => t.Name == "User defined").UserDelay);
    }
}
