using System.Text.Json.Serialization;

namespace BlastDesigner.Domain.Model;

/// <summary>A row of a table of the product catalogue: a text id (uuid v4) and logical deletion.</summary>
public interface ICatalogueRecord
{
    string Id { get; set; }
    /// <summary>Hidden when new records are made; what already uses it goes on showing it.</summary>
    bool Deleted { get; set; }
    /// <summary>Added just now and not applied yet: deleted while nothing refers to it, it goes for good.</summary>
    bool IsNew { get; set; }
}

/// <summary>Type of the initiation catalogue (initiation system): the kinds of product it lists, the one it starts with first.</summary>
public sealed class CatalogueType : ICatalogueRecord
{
    public string Id { get; set; } = ProductCatalogue.NewId();
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    /// <summary>Takes a delay of its own instead of a product.</summary>
    public bool UserDelay { get; set; }
    public List<string> KindIds { get; set; } = new();
    public bool Deleted { get; set; }
    /// <summary>Added in the open window and not applied yet: not in the project or the user's file, not stored either.</summary>
    [JsonIgnore] public bool IsNew { get; set; }
}

/// <summary>Kind of product. <see cref="Behaviour"/> says how the program calculates with it, so a new kind has to choose one.</summary>
public sealed class CatalogueKind : ICatalogueRecord
{
    public string Id { get; set; } = ProductCatalogue.NewId();
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public ProductKind Behaviour { get; set; }
    public bool Deleted { get; set; }
    /// <summary>Added in the open window and not applied yet: not in the project or the user's file, not stored either.</summary>
    [JsonIgnore] public bool IsNew { get; set; }
}

/// <summary>Group of kinds looked for together: a filter chip of Available resources, outside the hierarchy.</summary>
public sealed class CatalogueGroup : ICatalogueRecord
{
    public string Id { get; set; } = ProductCatalogue.NewId();
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public List<string> KindIds { get; set; } = new();
    public bool Deleted { get; set; }
    /// <summary>Added in the open window and not applied yet: not in the project or the user's file, not stored either.</summary>
    [JsonIgnore] public bool IsNew { get; set; }
}

/// <summary>Family of products of one kind: its products are the variants of it in the initiation catalogue.</summary>
public sealed class CatalogueFamily : ICatalogueRecord
{
    public string Id { get; set; } = ProductCatalogue.NewId();
    public string KindId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Deleted { get; set; }
    /// <summary>Added in the open window and not applied yet: not in the project or the user's file, not stored either.</summary>
    [JsonIgnore] public bool IsNew { get; set; }
}

/// <summary>
/// The tables of the catalogue above the products, in the hierarchy Type → Kind → Family. Records are never removed:
/// deleting marks them, a parent takes its children with it, and restoring brings back that record only.
/// </summary>
public sealed class ProductCatalogue
{
    public List<CatalogueType> Types { get; set; } = new();
    public List<CatalogueKind> Kinds { get; set; } = new();
    public List<CatalogueGroup> Groups { get; set; } = new();
    public List<CatalogueFamily> Families { get; set; } = new();

    /// <summary>The kinds no group lists: their products have no filter chip of their own.</summary>
    public IEnumerable<CatalogueKind> UnassignedKinds =>
        Kinds.Where(k => !k.Deleted && !Groups.Any(g => !g.Deleted && g.KindIds.Contains(k.Id))).OrderBy(k => k.Order);

    /// <summary>A new record id: uuid v4 as lower case text.</summary>
    public static string NewId() => Guid.NewGuid().ToString();

    /// <summary>
    /// Deletes a type with its kinds. A kind another type still lists stays and only leaves this type.
    /// </summary>
    public void Delete(CatalogueType type, IEnumerable<Product>? products = null)
    {
        type.Deleted = true;
        foreach (var id in type.KindIds.ToList())
        {
            if (Types.Any(t => t != type && !t.Deleted && t.KindIds.Contains(id))) type.KindIds.Remove(id);
            else if (Kinds.FirstOrDefault(k => k.Id == id) is { } kind) Delete(kind, products);
        }
    }

    /// <summary>Deletes a kind with its families and products; it leaves the groups, which stay.</summary>
    public void Delete(CatalogueKind kind, IEnumerable<Product>? products = null)
    {
        kind.Deleted = true;
        foreach (var g in Groups) g.KindIds.Remove(kind.Id);
        foreach (var f in Families.Where(f => f.KindId == kind.Id)) f.Deleted = true;
        foreach (var p in products?.Where(p => p.KindId == kind.Id) ?? Enumerable.Empty<Product>()) p.Deleted = true;
    }

    /// <summary>Deletes a group; its kinds stay.</summary>
    public void Delete(CatalogueGroup group) => group.Deleted = true;

    /// <summary>Deletes a family with its products.</summary>
    public void Delete(CatalogueFamily family, IEnumerable<Product>? products = null)
    {
        family.Deleted = true;
        foreach (var p in products?.Where(p => p.FamilyId == family.Id) ?? Enumerable.Empty<Product>()) p.Deleted = true;
    }

    /// <summary>What a deletion takes with it, for the warning before it: the kinds, families and products that go too.</summary>
    public (int Kinds, int Families, int Products) Dependents(CatalogueType type, IEnumerable<Product>? products = null)
    {
        var kinds = type.KindIds.Where(id => !Types.Any(t => t != type && !t.Deleted && t.KindIds.Contains(id)))
            .Select(id => Kinds.FirstOrDefault(k => k.Id == id)).Where(k => k is { Deleted: false }).ToList();
        var below = kinds.Select(k => Dependents(k!, products)).ToList();
        return (kinds.Count, below.Sum(d => d.Families), below.Sum(d => d.Products));
    }

    public (int Families, int Products) Dependents(CatalogueKind kind, IEnumerable<Product>? products = null) =>
        (Families.Count(f => f.KindId == kind.Id && !f.Deleted), products?.Count(p => p.KindId == kind.Id && !p.Deleted) ?? 0);

    public int Dependents(CatalogueFamily family, IEnumerable<Product>? products = null) =>
        products?.Count(p => p.FamilyId == family.Id && !p.Deleted) ?? 0;

    /// <summary>
    /// Whether a deletion can take the record away for good: it was added just now, and it is tied to no other record,
    /// in either direction. Anything else is deleted logically.
    /// </summary>
    public bool CanRemove(ICatalogueRecord record, IEnumerable<Product> products) => record.IsNew && record switch
    {
        CatalogueType t => t.KindIds.Count == 0,
        CatalogueGroup g => g.KindIds.Count == 0,
        CatalogueFamily f => !products.Any(p => p.FamilyId == f.Id),
        CatalogueKind k => !products.Any(p => p.KindId == k.Id) && !Families.Any(f => f.KindId == k.Id)
            && !Types.Any(t => t.KindIds.Contains(k.Id)) && !Groups.Any(g => g.KindIds.Contains(k.Id)),
        _ => false
    };

    /// <summary>Whether a deletion can take the product away: it was added just now and has no family.</summary>
    public static bool CanRemove(Product product) => product.IsNew && product.FamilyId == null && string.IsNullOrWhiteSpace(product.Family);

    /// <summary>Takes a record out of its table, for what <see cref="CanRemove(ICatalogueRecord, IEnumerable{Product})"/> allows.</summary>
    public void Remove(ICatalogueRecord record)
    {
        switch (record)
        {
            case CatalogueType t: Types.Remove(t); break;
            case CatalogueKind k: Kinds.Remove(k); break;
            case CatalogueGroup g: Groups.Remove(g); break;
            case CatalogueFamily f: Families.Remove(f); break;
        }
    }

    /// <summary>The records of the window are applied: none of them is new any more.</summary>
    public void Accept(IEnumerable<Product> products)
    {
        foreach (var r in Types.Concat<ICatalogueRecord>(Kinds).Concat(Groups).Concat(Families)) r.IsNew = false;
        foreach (var p in products) p.IsNew = false;
    }

    /// <summary>Whether a record can come back: the parent it hangs on has to be there. A kind no type lists has none.</summary>
    public bool CanRestore(CatalogueKind kind)
    {
        var parents = Types.Where(t => t.KindIds.Contains(kind.Id)).ToList();
        return parents.Count == 0 || parents.Any(t => !t.Deleted);
    }

    public bool CanRestore(CatalogueFamily family) => Kinds.Any(k => k.Id == family.KindId && !k.Deleted);

    public bool CanRestore(Product product) => Kinds.Any(k => k.Id == product.KindId && !k.Deleted)
        && (product.FamilyId == null || Families.Any(f => f.Id == product.FamilyId && !f.Deleted));

    /// <summary>
    /// The records of a project after opening: the built-in records and the user's over the project's own, the built-in
    /// products it lacks (a built-in product it has under another id counts as there), and the links of its products.
    /// </summary>
    public static void Attach(BlastProject project, ProductCatalogue user, IReadOnlyList<Product> userProducts)
    {
        project.Catalogue = Layered(BuiltIn(), user, project.Catalogue);
        DropRetired(project);
        foreach (var p in Product.DefaultCatalogue())
            if (!project.Products.Any(x => x.Id == p.Id || (x.Kind == p.Kind && x.Name == p.Name))) project.Products.Add(p);
        foreach (var p in userProducts)
        {
            var own = project.Products.FirstOrDefault(x => x.Id == p.Id);
            if (own == null) project.Products.Add(p.Clone());
            else if (p.Deleted) own.Deleted = true;
        }
        Link(project);
    }

    /// <summary>Ids of the products the built-in catalogue had once, and of their family.</summary>
    private static readonly string[] RetiredProducts = { "b6b2be9a-c3d3-4fa1-83bf-c42467dc8e56", "7ff165ec-6ed6-4544-94a3-4e16747fe227" };
    private const string RetiredFamily = "d5fc75c8-a80e-4459-a141-764999265be3";

    /// <summary>
    /// A project from a file before the built-in electric detonators were taken out loses them unless something uses
    /// them, and their family with them when no product is left in it.
    /// </summary>
    private static void DropRetired(BlastProject project)
    {
        var used = UsedProducts(project);
        project.Products.RemoveAll(p => RetiredProducts.Contains(p.Id) && !used.Contains(p.Id));
        if (!project.Products.Any(p => p.FamilyId == RetiredFamily)) project.Catalogue.Families.RemoveAll(f => f.Id == RetiredFamily);
    }

    /// <summary>
    /// Keeps the products in step with the tables: each has a kind of the table and calculates as it, and a product of a
    /// kind some type lists, or with a family named, has the record of its family, made when there is none.
    /// </summary>
    public static void Link(BlastProject project)
    {
        var tables = project.Catalogue;
        foreach (var p in project.Products)
        {
            if (tables.Kinds.FirstOrDefault(k => k.Id == p.KindId) is { } kind)
            {
                p.Kind = kind.Behaviour;
                p.KindId = kind.Id;
            }
            else p.KindId = KindId(p.Kind);
            var id = p.KindId;
            if (tables.Families.FirstOrDefault(f => f.Id == p.FamilyId && f.KindId == id) is { } family) { p.Family = family.Name; continue; }
            p.FamilyId = null;
            if (string.IsNullOrWhiteSpace(p.Family) && !tables.Types.Any(t => t.KindIds.Contains(id))) continue;
            var name = p.FamilyName;
            var record = tables.Families.FirstOrDefault(f => f.KindId == id && f.Name == name);
            if (record == null) tables.Families.Add(record = new CatalogueFamily { KindId = id, Name = name });
            p.FamilyId = record.Id;
        }
    }

    /// <summary>Ids of the products something uses: holes, ties, loading rules and initiation rules.</summary>
    public static HashSet<string> UsedProducts(BlastProject p)
    {
        var used = new HashSet<string>();
        void Add(params string?[] ids) { foreach (var id in ids) if (id != null) used.Add(id); }
        foreach (var h in p.Holes)
            foreach (var d in h.Decks)
            {
                Add(d.ProductId);
                foreach (var x in d.Primers) Add(x.BoosterId, x.DetonatorId);
            }
        foreach (var t in p.Ties) Add(t.ProductId);
        foreach (var r in p.LoadingRules)
        {
            Add(r.StemmingProductId, r.WetAlternateProductId, r.HardAlternateProductId, r.SoftAlternateProductId);
            foreach (var d in r.Decks)
            {
                Add(d.ProductId);
                foreach (var x in d.Primers) Add(x.BoosterId, x.DetonatorId);
            }
        }
        foreach (var r in p.InitiationRules) Add(r.DetonatorId, r.BoosterId);
        return used;
    }

    /// <summary>
    /// What a project works with: the built-in records, over them those of the user's file, over them the project's.
    /// A record deleted by the user counts as deleted in the project too.
    /// </summary>
    public static ProductCatalogue Layered(ProductCatalogue builtIn, ProductCatalogue user, ProductCatalogue project) => new()
    {
        Types = Merge(builtIn.Types, user.Types, project.Types).OrderBy(t => t.Order).ToList(),
        Kinds = Merge(builtIn.Kinds, user.Kinds, project.Kinds).OrderBy(k => k.Order).ToList(),
        Groups = Merge(builtIn.Groups, user.Groups, project.Groups).OrderBy(g => g.Order).ToList(),
        Families = Merge(builtIn.Families, user.Families, project.Families)
    };

    private static List<T> Merge<T>(List<T> builtIn, List<T> user, List<T> project) where T : class, ICatalogueRecord
    {
        var merged = new Dictionary<string, T>();
        var order = new List<string>();
        foreach (var r in builtIn.Concat(user).Concat(project))
        {
            if (!merged.ContainsKey(r.Id)) order.Add(r.Id);
            merged[r.Id] = ProjectJson.DeepClone(r);
        }
        foreach (var r in user.Where(r => r.Deleted)) merged[r.Id].Deleted = true;
        return order.Select(id => merged[id]).ToList();
    }

    // the ids of the built-in records are fixed: the same record is the same in every project and on every computer
    private const string BulkExplosive = "1bc46bc2-40ee-43dc-bf6e-f5ef1354a853", PackagedExplosive = "841ac6aa-bff6-47d1-8b8b-1475895a6999", Stemming = "26e82681-2fcc-4370-820a-25275fc8413f", GasBag = "091a505e-5a7a-4441-ac44-866e1a42ce2d", Booster = "0e8147e0-4d81-4923-b539-78fc9ec6a651",
        DownholeDetonator = "ff819924-7054-48c0-a5ef-09f37bc17029", SurfaceConnector = "c977fa76-9dfc-4c1d-aa28-7e50f675719d", ElectronicDetonator = "eef16ba5-7b42-4f78-846a-879d905c1579", DetonatingCord = "f87efe79-b240-4abe-8f86-31360733632e",
        CordRelay = "9ad419c4-d5a0-4ab7-90f1-2b237feaf047", ElectricDetonator = "22457879-fb13-4d37-88a8-c14b60edd42a", HarnessWire = "17a4186b-12d3-4433-a4df-489cf06e38e4", Other = "aba36964-4ea3-424b-ab4a-8772e52929ec";

    /// <summary>The built-in id of a kind of the program.</summary>
    public static string KindId(ProductKind behaviour) => behaviour switch
    {
        ProductKind.BulkExplosive => BulkExplosive,
        ProductKind.PackagedExplosive => PackagedExplosive,
        ProductKind.Stemming => Stemming,
        ProductKind.GasBag => GasBag,
        ProductKind.Booster => Booster,
        ProductKind.DownholeDetonator => DownholeDetonator,
        ProductKind.SurfaceConnector => SurfaceConnector,
        ProductKind.ElectronicDetonator => ElectronicDetonator,
        ProductKind.DetonatingCord => DetonatingCord,
        ProductKind.CordRelay => CordRelay,
        ProductKind.ElectricDetonator => ElectricDetonator,
        ProductKind.HarnessWire => HarnessWire,
        _ => Other
    };

    /// <summary>The records of the program, as the catalogue was when it was written in the code.</summary>
    public static ProductCatalogue BuiltIn()
    {
        var c = new ProductCatalogue();
        void Kind(string name, ProductKind behaviour) => c.Kinds.Add(new CatalogueKind { Id = KindId(behaviour), Name = name, Order = c.Kinds.Count, Behaviour = behaviour });
        void Type(string id, string name, params string[] kinds) => c.Types.Add(new CatalogueType { Id = id, Name = name, Order = c.Types.Count, KindIds = kinds.ToList(), UserDelay = kinds.Length == 0 });
        void Group(string id, string name, params string[] kinds) => c.Groups.Add(new CatalogueGroup { Id = id, Name = name, Order = c.Groups.Count, KindIds = kinds.ToList() });
        void Family(string id, string name, string kind) => c.Families.Add(new CatalogueFamily { Id = id, Name = name, KindId = kind });

        Kind("Bulk explosive", ProductKind.BulkExplosive);
        Kind("Packaged explosive", ProductKind.PackagedExplosive);
        Kind("Stemming", ProductKind.Stemming);
        Kind("Gas bag", ProductKind.GasBag);
        Kind("Booster", ProductKind.Booster);
        Kind("Downhole detonator", ProductKind.DownholeDetonator);
        Kind("Surface connector", ProductKind.SurfaceConnector);
        Kind("Electronic detonator", ProductKind.ElectronicDetonator);
        Kind("Detonating cord", ProductKind.DetonatingCord);
        Kind("Cord relay", ProductKind.CordRelay);
        Kind("Electric detonator", ProductKind.ElectricDetonator);
        Kind("Harness wire", ProductKind.HarnessWire);
        Kind("Other", ProductKind.Other);

        Type("cff81fcf-0398-4a18-a4d9-b6214389f566", "Shock tube", SurfaceConnector, DownholeDetonator, Other);
        Type("3d8f503e-e82f-414c-993b-f022fd8159a2", "Cord delay", CordRelay);
        Type("e323b18e-f698-45f6-b9d3-f6807e006efc", "Detonating cord system", DetonatingCord);
        Type("f85e4a61-ee14-434e-aa5d-4c8f4cf26f5e", "Electric", ElectricDetonator);
        Type("20fdd1af-dc46-4f59-8002-7e6329cc360d", "Harness wire", HarnessWire);
        Type("4fc83e10-6074-4dac-a664-e708cc5e8319", "EBS detonators", ElectronicDetonator);
        Type("63354b1d-e3ff-4bd7-893a-063e1ecaa568", "User defined");

        Group("51c697ac-7edf-4a09-9993-a18efdfdec8d", "Explosives", BulkExplosive, PackagedExplosive);
        Group("90bcf534-0c16-49f7-9b65-03b2813a81e5", "Stemming", Stemming);
        Group("e207fbdf-d4fa-4479-a889-632bdad0be3f", "Gas bags", GasBag);
        Group("74861deb-eacc-413b-bf3e-356fd0319786", "Boosters", Booster);
        Group("1223d610-578d-4ee8-b2f4-9872ee3bb696", "Detonators", DownholeDetonator, ElectronicDetonator, ElectricDetonator);
        Group("f27bcbde-6c2e-44f9-9d1f-33a012735b3f", "Surface connectors", SurfaceConnector);
        Group("cbbb681a-0578-4f1c-932d-d4e3dc25cb37", "Detonating cord", DetonatingCord, CordRelay);
        Group("5c9ea84e-bbbf-47c2-a6eb-9c6472173411", "Harness wire", HarnessWire);
        Group("76e980a0-2d51-4867-9095-acbc95b1f044", "Other", Other);

        Family("ddc6ed48-5bc2-4902-be8f-64cdbfd8e90d", "Downhole detonator", DownholeDetonator);
        Family("73a7dd8e-133d-402d-ba26-703f1d430510", "Electronic detonator", ElectronicDetonator);
        Family("31bbb3a1-1929-4dcd-b8aa-3d7b0325c3d2", "DeviTronic", ElectronicDetonator);
        Family("1b2212dd-fc1d-4409-916c-4313f4b912ae", "Surface connector", SurfaceConnector);
        Family("a70df506-c21a-4e74-8f5a-22bd5f22a86f", "Detonating cord", DetonatingCord);
        Family("f5d36999-3c77-4a86-be52-14c75c75dd7b", "Cord relay", CordRelay);
        Family("7685543d-be13-4b47-97b2-dd52d770dd35", "Harness wire", HarnessWire);
        return c;
    }
}
