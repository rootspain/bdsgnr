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

    public const string NoCombination = "Hole {0} has no unused combination detonator {1}: a tie with it starts only at a hole with one. Choose another product in the Initiation catalogue";
    public const string CombinationNotFromHole = "{0}: a tie with the combination detonator {1} starts only at a hole with an unused one";

    /// <summary>
    /// How many combination detonators <paramref name="product"/> of the hole no tie starts with yet: its primers with
    /// that detonator less the ties made with it that leave the hole (one tie a detonator, its surface block).
    /// </summary>
    public static int UnusedCombination(BlastProject project, Hole hole, Product product)
    {
        if (product.Kind != ProductKind.CombinationDetonator) return 0;
        var inHole = Charging.Primers(hole).Count(x => x.Primer.DetonatorId == product.Id);
        if (inHole == 0) return 0;
        var used = project.Ties.Count(t => t.FromId == hole.Id && t.ProductId == product.Id && t.DelayMs == 0);
        return Math.Max(0, inHole - used);
    }

    /// <summary>
    /// Why a new tie made with <paramref name="product"/> cannot start at <paramref name="from"/>: a combination detonator
    /// on a tie is the surface part of one in the hole it leaves, so it starts only at a hole with one of them that no
    /// tie starts with yet (<see cref="UnusedCombination"/>). Any other product, or a delay of its own (null), starts
    /// anywhere. Null when it may start there.
    /// </summary>
    public static (string Format, string Hole, string Product)? StartRefusal(BlastProject project, Guid from, Product? product)
    {
        if (product?.Kind != ProductKind.CombinationDetonator) return null;
        if (project.FindEntity(from) is Hole hole)
            return UnusedCombination(project, hole, product) > 0 ? null : (NoCombination, hole.Label, product.Name);
        return (CombinationNotFromHole, project.FindEntity(from) is InitiationPoint ip ? ip.Label : "", product.Name);
    }

    /// <summary>
    /// What the holes in sight ask of the products of new ties (<see cref="Survey"/>): whether one of them takes an ESI
    /// tie, whether one takes another, the combination detonators no tie starts with yet by product. No hole: everything
    /// fits, so that the catalogue is never empty for want of holes.
    /// </summary>
    public sealed class Context
    {
        public bool Any { get; init; }
        public bool Esi { get; init; }
        public bool Other { get; init; }
        public IReadOnlyDictionary<string, int> Combination { get; init; } = new Dictionary<string, int>();

        /// <summary>Whether new ties may be made with the product in one of the holes: never a product that stays in the hole (<see cref="GoesOnTie"/>).</summary>
        public bool Fits(Product p)
        {
            if (!GoesOnTie(p)) return false;
            if (!Any) return true;
            if (p.Kind == ProductKind.CombinationDetonator) return Combination.TryGetValue(p.Id, out var n) && n > 0;
            return IsEsi(p) ? Esi : Other;
        }

        /// <summary>Why the product does not fit (a translation key, with the product's name for {0}); null when it does.</summary>
        public string? Why(Product p) =>
            Fits(p) ? null
            : !GoesOnTie(p) ? NotOnTie
            : p.Kind == ProductKind.CombinationDetonator ? NoCombinationInSight
            : IsEsi(p) ? NoElectronicInSight : OnlyElectronicInSight;
    }

    public const string NotOnTie = "{0} stays in the hole: it does not go on a tie";
    public const string NoCombinationInSight = "No hole in sight has an unused {0}";
    public const string NoElectronicInSight = "No hole in sight has an electronic detonator";
    public const string OnlyElectronicInSight = "The holes in sight have electronic detonators only";

    /// <summary>
    /// The context of the products of new ties: what the <paramref name="holes"/> (those in sight) ask for. A hole not
    /// charged yet takes any tie (<see cref="Detonators"/>).
    /// </summary>
    public static Context Survey(BlastProject project, IEnumerable<Hole> holes)
    {
        bool any = false, esi = false, other = false;
        var combination = new Dictionary<string, int>();
        var combos = project.Products.Where(p => p.Kind == ProductKind.CombinationDetonator).ToList();
        foreach (var h in holes)
        {
            any = true;
            var (electronic, notElectronic) = Detonators(project, h);
            if (!electronic && !notElectronic) { esi = other = true; continue; }
            esi |= electronic;
            other |= notElectronic;
            foreach (var c in combos)
                if (UnusedCombination(project, h, c) is var n && n > 0) combination[c.Id] = combination.GetValueOrDefault(c.Id) + n;
        }
        return new Context { Any = any, Esi = esi, Other = other, Combination = combination };
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
