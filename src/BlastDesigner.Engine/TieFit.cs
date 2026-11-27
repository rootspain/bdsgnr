using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// Whether a tie fits the holes it joins by their detonators. A tie of an electronic initiation system (ESI: its product
/// of the electronic detonator kind) goes only to a hole with an electronic primer; any other (shock tube, cord, relay,
/// electric, bus line, 0 ms, a delay of its own) only to a hole with a primer that is not electronic. A hole with both
/// takes either; a hole with no detonator yet (not charged) any; an initiation point at an end asks nothing of it.
/// </summary>
public static class TieFit
{
    public const string NotElectronic = "Hole {0} has no electronic detonator: an ESI tie does not go to it. Choose a product of another type in the Initiation catalogue";
    public const string OnlyElectronic = "Hole {0} has electronic detonators only: a tie goes to it with an ESI product. Choose ESI in the Initiation catalogue";

    /// <summary>Whether ties made with the product are ESI ties: its kind is the electronic detonator.</summary>
    public static bool IsEsi(Product? product) => product?.Kind == ProductKind.ElectronicDetonator;

    /// <summary>The detonators of a hole: whether one of its primers is electronic, whether one is not; primers without a detonator not counted.</summary>
    public static (bool Electronic, bool Other) Detonators(BlastProject project, Hole hole)
    {
        bool electronic = false, other = false;
        foreach (var (p, _) in Charging.Primers(hole))
        {
            if (p.DetonatorId == null) continue;
            if (Charging.IsElectronic(project, p)) electronic = true;
            else other = true;
        }
        return (electronic, other);
    }

    /// <summary>Whether a tie, ESI or not, may have the object at one of its ends.</summary>
    public static bool Fits(BlastProject project, Guid end, bool esi)
    {
        if (project.FindEntity(end) is not Hole hole) return true;
        var (electronic, other) = Detonators(project, hole);
        if (!electronic && !other) return true;
        return esi ? electronic : other;
    }

    /// <summary>Whether a tie made with the product (null: none, or a delay of its own) may join the two.</summary>
    public static bool Fits(BlastProject project, Guid from, Guid to, Product? product) => Refusal(project, from, to, product) == null;

    /// <summary>
    /// Whether a product can go on a tie: not one that stays in the hole (explosive, booster, stemming, gas bag, downhole
    /// detonator), though a type of the catalogue lists its kind (the shock tube lists the downhole detonators).
    /// </summary>
    public static bool GoesOnTie(Product p) =>
        !p.IsExplosive && p.Kind is not (ProductKind.Booster or ProductKind.Stemming or ProductKind.GasBag or ProductKind.DownholeDetonator);

    /// <summary>A product offered for ties: the type of the Initiation catalogue it is listed under, whether it fits them all.</summary>
    public sealed record Offer(Product Product, CatalogueType? Type, bool Fits);

    /// <summary>
    /// The products the property list of ties offers, by the types of the Initiation catalogue as it does: the products of
    /// the kinds of each type that go on a tie (<see cref="GoesOnTie"/>; a delay of its own lists none), in the order of the types, of the kinds in a type, by family,
    /// delay and name; each once, under its first type; a deleted one only when something uses it. Those that do not fit every one of
    /// <paramref name="ties"/> (<see cref="Fits(BlastProject, Guid, Guid, Product?)"/>) are left out too, except a product a
    /// tie has now: it stays, at the end when no type lists it, so that it shows as it is.
    /// </summary>
    public static List<Offer> Offered(BlastProject project, IReadOnlyCollection<TieEntity> ties, ISet<string>? used = null)
    {
        var tables = project.Catalogue;
        var now = ties.Select(t => t.ProductId).OfType<string>().ToHashSet();
        bool Live(string kindId) => tables.Kinds.Any(k => k.Id == kindId && !k.Deleted);
        bool FitsAll(Product p) => ties.All(t => Fits(project, t.FromId, t.ToId, p));
        var offers = new List<Offer>();
        var seen = new HashSet<string>();
        foreach (var type in tables.Types.Where(t => !t.Deleted && !t.UserDelay).OrderBy(t => t.Order))
        {
            var kinds = type.KindIds.Where(Live).ToList();
            var products = project.Products
                .Where(p => kinds.Contains(p.KindId) && GoesOnTie(p) && (!p.Deleted || now.Contains(p.Id) || used?.Contains(p.Id) == true))
                .OrderBy(p => kinds.IndexOf(p.KindId)).ThenBy(p => p.FamilyName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(p => p.SurfaceDelay).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);
            foreach (var p in products)
            {
                if (!seen.Add(p.Id)) continue;
                var fits = FitsAll(p);
                if (fits || now.Contains(p.Id)) offers.Add(new Offer(p, type, fits));
            }
        }
        foreach (var id in now.Where(id => !seen.Contains(id)))
            if (project.FindProduct(id) is { } p) offers.Add(new Offer(p, null, FitsAll(p)));
        return offers;
    }

    /// <summary>
    /// Why a tie made with <paramref name="product"/> (null: none, or a delay of its own) does not fit the two, as a format
    /// (the translation key) and the hole it names; null when it fits.
    /// </summary>
    public static (string Format, string Hole)? Refusal(BlastProject project, Guid from, Guid to, Product? product)
    {
        var esi = IsEsi(product);
        foreach (var id in new[] { from, to })
            if (!Fits(project, id, esi)) return (esi ? NotElectronic : OnlyElectronic, ((Hole)project.FindEntity(id)!).Label);
        return null;
    }
}
