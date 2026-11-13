using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>Charge column calculations and automatic loading.</summary>
public static class Charging
{
    /// <summary>
    /// Mass of a deck in kg (bulk: column volume × density; packaged: whole cartridges): the property <c>deck.mass</c>
    /// of the project's calculation profile (built-in «SHOTPlus 6» unless the project has its own).
    /// </summary>
    public static double DeckMass(BlastProject project, Hole hole, Deck deck)
    {
        // the document's long session answers for a deck of the plan in its own hole; a deck of a preview gets a short one
        if (CalcService.Live(project) is { } live && live.Knows("deck", deck) && ReferenceEquals(live.Scope.HoleOf(deck), hole))
            return Calculated(live, "deck.mass", deck, 0);
        var s = CalcService.Session(project);
        s.Scope.NoteParent(deck, hole);
        return Calculated(s, "deck.mass", deck, 0);
    }

    /// <summary>Explosive mass of the hole with its boosters, kg (<c>hole.chargeMass</c>).</summary>
    public static double ExplosiveMass(BlastProject project, Hole hole) => Calculated(CalcService.SessionFor(project, "hole", hole), "hole.chargeMass", hole, 0);

    /// <summary>Total length of the stemming decks, m (<c>hole.stemmingLength</c>).</summary>
    public static double StemmingLength(BlastProject project, Hole hole) => Calculated(CalcService.SessionFor(project, "hole", hole), "hole.stemmingLength", hole, 0);

    /// <summary>Length of the top stemming (collar to first non-stemming deck), <c>hole.topStemming</c>.</summary>
    public static double TopStemming(BlastProject project, Hole hole) => Calculated(CalcService.SessionFor(project, "hole", hole), "hole.topStemming", hole, hole.EffectiveLength);

    /// <summary>A value of the calculation engine; a formula that failed gives <paramref name="fallback"/>, as a missing product did before.</summary>
    internal static double Calculated(BlastDesigner.Calc.CalcSession s, string property, object entity, double fallback) =>
        s.Value(property, entity) is { IsError: false } v ? v.Number : fallback;

    public static IEnumerable<(Primer Primer, Deck Deck)> Primers(Hole hole) =>
        hole.Decks.SelectMany(d => d.Primers.Select(p => (p, d)));

    /// <summary>Delay of a primer's detonator, ms.</summary>
    /// <summary>Whether a primer of the hole carries an electronic detonator.</summary>
    public static bool HasElectronicDetonator(BlastProject project, Hole hole) =>
        Primers(hole).Any(x => project.FindProduct(x.Primer.DetonatorId)?.Kind == ProductKind.ElectronicDetonator);

    public static double PrimerDelay(BlastProject project, Primer p) =>
        p.DelayMs ?? project.FindProduct(p.DetonatorId)?.DelayMs ?? 0;

    /// <summary>Whether a primer carries an electronic detonator.</summary>
    public static bool IsElectronic(BlastProject project, Primer p) => project.FindProduct(p.DetonatorId)?.Kind == ProductKind.ElectronicDetonator;

    /// <summary>
    /// The run of the signal down the tube of a downhole detonator to its primer: the depth of the primer times the
    /// detonator's <see cref="Product.PropagationMsPerM"/>, ms; nothing for other detonators.
    /// </summary>
    public static double PropagationDelay(BlastProject project, Primer p) =>
        project.FindProduct(p.DetonatorId) is { Kind: ProductKind.DownholeDetonator } d ? Math.Max(0, p.Depth) * d.PropagationMsPerM : 0;

    /// <summary>
    /// Charges of a hole: runs of explosive decks with no inert deck or gap between them, from the collar down, each with its
    /// primers. A deck counts as explosive by its product.
    /// </summary>
    public static List<List<Deck>> ExplosiveRuns(BlastProject project, Hole hole)
    {
        var runs = new List<List<Deck>>();
        List<Deck>? run = null;
        double bottom = double.NaN;
        foreach (var d in hole.Decks.Where(d => d.Length > 1e-9).OrderBy(d => d.Top))
        {
            if (project.FindProduct(d.ProductId)?.IsExplosive != true) { run = null; continue; }
            if (run == null || d.Top > bottom + 1e-6) runs.Add(run = new List<Deck>());
            run.Add(d);
            bottom = d.Bottom;
        }
        return runs;
    }

    /// <summary>
    /// The largest offset of the hole's electronic primers, ms, never below 0 (SHOTPlus <c>HoleLoading.MaxOffset</c>): taken
    /// whether or not <see cref="ElectronicSettings.UseOffsets"/> is on, the deck delays not counted. A programmed time plus
    /// this must stay within the largest delay of the detonators.
    /// </summary>
    public static double MaxPrimerOffset(BlastProject project, Hole hole) =>
        Math.Max(0, Primers(hole).Where(x => IsElectronic(project, x.Primer)).Select(x => x.Primer.OffsetMs).DefaultIfEmpty(0).Max());

    /// <summary>
    /// Time of each primer after the hole's own: an electronic one by <see cref="PrimerTimes"/> from 0, another by its delay.
    /// It orders the primers of a hole however late the hole is.
    /// </summary>
    public static Dictionary<Primer, double> RelativePrimerTimes(BlastProject project, Hole hole)
    {
        var electronic = PrimerTimes(project, hole, 0, relative: true);
        var times = new Dictionary<Primer, double>(ReferenceEqualityComparer.Instance);
        foreach (var (p, _) in Primers(hole)) times[p] = electronic.TryGetValue(p, out var t) ? t : PrimerDelay(project, p) + PropagationDelay(project, p);
        return times;
    }

    /// <summary>
    /// Depth of the middle of the charge that fires first in a hole (SHOTPlus <c>GetFiringCharge</c>): the charge of the
    /// earliest primer, the deeper of two at once. Null when no explosive deck of the hole has a primer.
    /// </summary>
    public static double? FiringChargeMidDepth(BlastProject project, Hole hole)
    {
        var times = RelativePrimerTimes(project, hole);
        (double T, double Depth, double Mid)? best = null;
        foreach (var run in ExplosiveRuns(project, hole))
            foreach (var p in run.SelectMany(d => d.Primers))
            {
                var t = times[p];
                var mid = (run[0].Top + run[^1].Bottom) / 2;
                if (best is not { } b || t < b.T - 1e-9 || (Math.Abs(t - b.T) <= 1e-9 && p.Depth > b.Depth)) best = (t, p.Depth, mid);
            }
        return best?.Mid;
    }

    /// <summary>
    /// The explosive of a hole by charge, at the charge's time (a charge with no time fires with the hole): each takes the mass
    /// of its decks and of the boosters in it, and together they make the hole's mass <paramref name="holeKg"/> (that of the
    /// calculation profile). A hole of one charge, or with no charges, is one event.
    /// </summary>
    public static List<(double TimeMs, double Kg)> ChargeEvents(BlastProject project, Hole hole, double holeKg)
    {
        if (hole.FiringTimeMs is not double holeTime) return new();
        if (hole.Charges.Count <= 1) return new() { (hole.Charges.FirstOrDefault()?.TimeMs ?? holeTime, holeKg) };
        var masses = hole.Charges.Select(c => c.Decks.Sum(d => DeckMass(project, hole, d) + d.Primers.Sum(p => project.FindProduct(p.BoosterId)?.UnitMass ?? 0))).ToList();
        var total = masses.Sum();
        return hole.Charges.Select((c, i) => (c.TimeMs ?? holeTime, total > 0 ? holeKg * masses[i] / total : holeKg / hole.Charges.Count)).ToList();
    }

    /// <summary>
    /// Programmed time of each electronic primer of a hole whose time is <paramref name="holeTime"/> (SHOTPlus
    /// <c>HoleLoading.SetEBSTimes</c>), rounded as the detonators take it. With <see cref="ElectronicSettings.UseOffsets"/> a
    /// primer fires at the hole's time plus its offset; one that would fire before 0 is not programmed and is left out. Otherwise the charges (<see cref="ExplosiveRuns"/>) with
    /// an electronic primer fire in the deck order: the first at the hole's time, each next one <see cref="ElectronicSettings.InterDeckDelayMs"/>
    /// later (all at once when simultaneous); within a charge the primers follow one another by <see cref="ElectronicSettings.IntraDeckDelayMs"/>.
    /// </summary>
    public static Dictionary<Primer, double> PrimerTimes(BlastProject project, Hole hole, double holeTime) => PrimerTimes(project, hole, holeTime, false);

    /// <summary>
    /// <see cref="PrimerTimes(BlastProject, Hole, double)"/>; <paramref name="relative"/> keeps a time below 0 (the order of the
    /// primers of a hole timed from 0, <see cref="RelativePrimerTimes"/>).
    /// </summary>
    internal static Dictionary<Primer, double> PrimerTimes(BlastProject project, Hole hole, double holeTime, bool relative)
    {
        var e = project.Electronic;
        var times = new Dictionary<Primer, double>(ReferenceEqualityComparer.Instance);
        // the hole's time is programmed already; a primer after it is rounded as the detonator takes it
        double After(double delta) => delta == 0 ? holeTime : Math.Max(0, Timing.RoundTime(holeTime + delta, e.AllowSubMs));
        if (e.UseOffsets)
        {
            foreach (var (p, _) in Primers(hole))
            {
                if (!IsElectronic(project, p)) continue;
                var t = p.OffsetMs == 0 ? holeTime : Timing.RoundTime(holeTime + p.OffsetMs, e.AllowSubMs);
                // SHOTPlus SetEBSTimes: a detonator before 0 stays unprogrammed
                if (relative || t > -0.001) times[p] = relative ? t : Math.Max(0, t);
            }
            return times;
        }
        var charges = ExplosiveRuns(project, hole)
            .Select(run => run.SelectMany(d => d.Primers).Where(p => IsElectronic(project, p)).ToList())
            .Where(c => c.Count > 0).ToList();
        // the primers in no charge (a primer of an inert deck) fire with the hole
        foreach (var (p, _) in Primers(hole))
            if (IsElectronic(project, p) && !charges.Any(c => c.Contains(p))) times[p] = holeTime;
        // the runs come from the collar down
        if (e.DeckOrder == DeckFiringOrder.BottomUp) charges.Reverse();
        for (int k = 0; k < charges.Count; k++)
        {
            var start = e.DeckOrder == DeckFiringOrder.Simultaneous ? 0 : k * e.InterDeckDelayMs;
            var primers = e.PrimerOrder == PrimerFiringOrder.BottomUp ? charges[k].OrderByDescending(p => p.Depth).ToList() : charges[k].OrderBy(p => p.Depth).ToList();
            for (int i = 0; i < primers.Count; i++) times[primers[i]] = After(start + i * e.IntraDeckDelayMs);
        }
        return times;
    }

    /// <summary>Loads holes with the first matching rule by priority. Manually loaded holes are skipped unless forced.</summary>
    public static int ApplyRules(BlastProject project, IEnumerable<Hole> holes, bool overwriteManual = false)
    {
        int loaded = 0;
        var rules = project.LoadingRules.OrderBy(r => r.Priority).ToList();
        foreach (var h in holes)
        {
            if (h.IsDummy || (h.ManualLoading && !overwriteManual)) continue;
            var rule = rules.FirstOrDefault(r => r.Matches(h));
            if (rule == null) continue;
            ApplyRule(project, h, rule);
            loaded++;
        }
        return loaded;
    }

    /// <summary>Builds the charge column of one hole from a rule: decks from the bottom up, then top stemming.</summary>
    public static void ApplyRule(BlastProject project, Hole h, LoadingRule rule)
    {
        h.Decks.Clear();
        h.ManualLoading = false;
        var length = h.EffectiveLength;
        var stemTop = Math.Min(rule.StemmingLength, length);
        double bottom = length;
        var fills = rule.Decks.Count(d => d.Length <= 0);
        var fixedTotal = rule.Decks.Where(d => d.Length > 0).Sum(d => d.Length);
        var fillLength = fills > 0 ? Math.Max(0, (length - stemTop - fixedTotal) / fills) : 0;

        foreach (var t in rule.Decks)
        {
            var len = t.Length > 0 ? t.Length : fillLength;
            var top = Math.Max(stemTop, bottom - len);
            if (bottom - top < 1e-6) continue;
            var deck = new Deck { Top = top, Bottom = bottom, ProductId = t.ProductId };
            var count = t.Primers.Count;
            for (int i = 0; i < count; i++)
            {
                var x = t.Primers[i];
                // a primer with no height of its own is spread evenly up the deck from the first
                var depth = x.Offset is double o ? bottom - o : bottom - t.PrimerOffset - i * Math.Max(1, (bottom - top) / Math.Max(1, count));
                deck.Primers.Add(new Primer { Depth = Math.Clamp(depth, top, bottom), BoosterId = x.BoosterId, DetonatorId = x.DetonatorId, DelayMs = x.DelayMs, OffsetMs = x.OffsetMs });
            }
            h.Decks.Add(deck);
            bottom = top;
            if (bottom <= stemTop) break;
        }
        if (stemTop > 0)
            h.Decks.Add(new Deck { Top = 0, Bottom = bottom > stemTop ? bottom : stemTop, ProductId = rule.StemmingProductId });
        h.Decks.Sort((a, b) => a.Top.CompareTo(b.Top));
        LoadingAlternates.Apply(project, h, rule);
        h.Decks.Sort((a, b) => a.Top.CompareTo(b.Top));
    }

    /// <summary>
    /// Builds a loading rule from a designed column: the top stemming keeps its length, the longest explosive deck becomes the
    /// "fill" deck (so on other hole lengths it lengthens or shortens like <see cref="AdaptColumn"/>), the other decks keep their
    /// lengths and the primers keep their distance above the bottom of their deck. Applicability filters are left open.
    /// </summary>
    public static LoadingRule RuleFromColumn(BlastProject project, IReadOnlyList<Deck> decks, string name)
    {
        var sorted = decks.Where(d => d.Length > 1e-6).OrderBy(d => d.Top).ToList();
        var rule = new LoadingRule { Name = name, StemmingLength = 0, StemmingProductId = null };
        var start = 0;
        if (sorted.Count > 0 && project.FindProduct(sorted[0].ProductId)?.Kind == ProductKind.Stemming && sorted[0].Top < 1e-6)
        {
            rule.StemmingLength = sorted[0].Bottom;
            rule.StemmingProductId = sorted[0].ProductId;
            start = 1;
        }
        else if (sorted.Count > 0) rule.StemmingLength = sorted[0].Top;
        var body = sorted.Skip(start).ToList();
        var fill = body.Where(d => project.FindProduct(d.ProductId)?.IsExplosive == true).OrderByDescending(d => d.Length).FirstOrDefault();
        for (int i = body.Count - 1; i >= 0; i--)
        {
            var d = body[i];
            var primers = d.Primers.OrderByDescending(p => p.Depth).ToList();
            rule.Decks.Add(new DeckTemplate
            {
                ProductId = d.ProductId,
                Length = d == fill ? 0 : Math.Round(d.Length, 3),
                // every primer as it is: its height, booster, detonator, delay and offset
                Primers = primers.Select(p => new PrimerTemplate
                {
                    Offset = Math.Round(d.Bottom - p.Depth, 3), BoosterId = p.BoosterId, DetonatorId = p.DetonatorId, DelayMs = p.DelayMs, OffsetMs = p.OffsetMs
                }).ToList()
            });
        }
        return rule;
    }

    /// <summary>Takes the column out of the holes; their state stays as it is, it is set by hand.</summary>
    public static void Empty(IEnumerable<Hole> holes)
    {
        foreach (var h in holes)
        {
            h.Decks.Clear();
            h.ManualLoading = false;
        }
    }

    /// <summary>Merges adjacent decks of the same product.</summary>
    public static void JoinLikeDecks(IEnumerable<Hole> holes)
    {
        foreach (var h in holes)
        {
            var sorted = h.Decks.OrderBy(d => d.Top).ToList();
            var merged = new List<Deck>();
            foreach (var d in sorted)
            {
                if (merged.Count > 0 && merged[^1].ProductId == d.ProductId && Math.Abs(merged[^1].Bottom - d.Top) < 1e-6)
                {
                    merged[^1].Bottom = d.Bottom;
                    merged[^1].Primers.AddRange(d.Primers);
                }
                else merged.Add(d);
            }
            h.Decks = merged;
        }
    }

    /// <summary>
    /// Copies a charge column designed for a hole of length <paramref name="fromLength"/> to a hole of length <paramref name="toLength"/>:
    /// the top stemming keeps its length, the longest explosive deck absorbs the difference and decks below it move with the toe.
    /// </summary>
    public static List<Deck> AdaptColumn(BlastProject project, IReadOnlyList<Deck> decks, double fromLength, double toLength)
    {
        var result = decks.Select(d => d.Clone()).OrderBy(d => d.Top).ToList();
        var delta = toLength - fromLength;
        if (Math.Abs(delta) < 1e-9 || result.Count == 0) return result;
        var stretch = result.Where(d => project.FindProduct(d.ProductId)?.IsExplosive == true).OrderByDescending(d => d.Length).FirstOrDefault()
                      ?? result[^1];
        var stretchBottom = stretch.Bottom;
        foreach (var d in result)
        {
            if (d == stretch)
            {
                d.Bottom = Math.Max(d.Top + 0.1, d.Bottom + delta);
                foreach (var p in d.Primers) p.Depth = Math.Clamp(p.Depth + delta, d.Top, d.Bottom);
            }
            else if (d.Top >= stretchBottom - 1e-9)
            {
                d.Top += delta;
                d.Bottom += delta;
                foreach (var p in d.Primers) p.Depth += delta;
            }
        }
        return result.Where(d => d.Bottom > d.Top + 1e-6 && d.Top < toLength).Select(d => { d.Bottom = Math.Min(d.Bottom, toLength); return d; }).ToList();
    }

    /// <summary>
    /// The column of a hole whose length changed: the last interval goes down to the toe, its primers keep their distance
    /// from its bottom (within it); intervals the toe no longer reaches go, the one it cuts ends at the toe, its primers
    /// staying where they are within it.
    /// </summary>
    public static void ColumnToToe(Hole h)
    {
        if (h.Decks.Count == 0) return;
        var toe = h.EffectiveLength;
        h.Decks.Sort((a, b) => a.Top.CompareTo(b.Top));
        var cut = h.Decks.RemoveAll(d => d.Top >= toe - 1e-6) > 0;
        if (h.Decks.Count == 0) return;
        var last = h.Decks[^1];
        var delta = toe - last.Bottom;
        if (Math.Abs(delta) < 1e-9) return;
        last.Bottom = toe;
        // a primer of an interval the toe cuts stays where it is if it can
        foreach (var p in last.Primers) p.Depth = Math.Clamp(p.Depth + (cut ? 0 : delta), last.Top, last.Bottom);
    }

    /// <summary>
    /// Follows a change of a document (<see cref="Document.Followers"/>): every hole whose length changed in it, by hand,
    /// with its level or by a move, has its last interval taken down to the toe (<see cref="ColumnToToe"/>).
    /// </summary>
    public static Action? FollowLength(BlastProject project)
    {
        var before = project.Holes.ToDictionary(h => h.Id, h => h.EffectiveLength);
        return () =>
        {
            foreach (var h in project.Holes)
                if (before.TryGetValue(h.Id, out var was) && Math.Abs(was - h.EffectiveLength) > 1e-9) ColumnToToe(h);
        };
    }

    /// <summary>Shortest top stemming accepted without a warning, in hole diameters (edited in its check in the hole loading window, kept in the program settings).</summary>
    public static double MinStemmingDiameters { get; set; } = 25;

    /// <summary>Shortest top stemming accepted without a warning: <see cref="MinStemmingDiameters"/> hole diameters, m.</summary>
    public static double MinStemming(Hole h) => MinStemmingDiameters * h.Diameter / 1000;

    /// <summary>Length of the hole not covered by any deck (above, between and below the decks), m.</summary>
    public static double ColumnGap(Hole h)
    {
        var len = h.EffectiveLength;
        var sorted = h.Decks.OrderBy(d => d.Top).ToList();
        return sorted.Count == 0 ? len : sorted[0].Top + Math.Max(0, len - sorted[^1].Bottom) + sorted.Zip(sorted.Skip(1)).Sum(x => Math.Max(0, x.Second.Top - x.First.Bottom));
    }

    /// <summary>
    /// Whether a charged hole fails any of the loading checks shown in the hole loading window: a primer in every
    /// explosive deck, the column covering the whole depth, stemming of at least <see cref="MinStemmingDiameters"/> diameters, and <see cref="Validate"/>.
    /// An uncharged hole has nothing to warn about.
    /// </summary>
    public static bool HasLoadingWarning(BlastProject project, Hole h) => LoadingWarnings(project, h).Count > 0;

    /// <summary>
    /// The loading warnings of a hole by kind, the ones <see cref="HasLoadingWarning"/> marks it for: two holes with a warning
    /// of the same kind have the same trouble, whichever deck it is in. None for an uncharged hole.
    /// </summary>
    public static IReadOnlySet<LoadingWarning> LoadingWarnings(BlastProject project, Hole h)
    {
        var kinds = new HashSet<LoadingWarning>();
        if (h.Decks.Count == 0) return kinds;
        var expl = h.Decks.Where(d => project.FindProduct(d.ProductId)?.IsExplosive == true).ToList();
        if (expl.Count == 0) kinds.Add(LoadingWarning.NoExplosive);
        if (expl.Any(d => d.Primers.Count == 0)) kinds.Add(LoadingWarning.NoPrimer);
        if (ColumnGap(h) >= 0.05) kinds.Add(LoadingWarning.ColumnGap);
        if (TopStemming(project, h) < MinStemming(h) - 1e-6) kinds.Add(LoadingWarning.ShortStemming);
        var sorted = h.Decks.OrderBy(d => d.Top).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var d = sorted[i];
            if (d.Bottom > h.EffectiveLength + 1e-6) kinds.Add(LoadingWarning.BelowToe);
            if (i > 0 && d.Top < sorted[i - 1].Bottom - 1e-6) kinds.Add(LoadingWarning.Overlap);
            if (project.FindProduct(d.ProductId) is { IsExplosive: true, WaterResistant: false } && h.WaterDepth > 0 && d.Bottom > h.EffectiveLength - h.WaterDepth)
                kinds.Add(LoadingWarning.NotWaterResistant);
        }
        return kinds;
    }

    /// <summary>Validates a charge column; returns human-readable problems.</summary>
    public static List<string> Validate(BlastProject project, Hole h)
    {
        var issues = new List<string>();
        var sorted = h.Decks.OrderBy(d => d.Top).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var d = sorted[i];
            if (d.Bottom > h.EffectiveLength + 1e-6) issues.Add($"Deck {i + 1} extends below the toe");
            if (i > 0 && d.Top < sorted[i - 1].Bottom - 1e-6) issues.Add($"Deck {i + 1} overlaps deck {i}");
            var p = project.FindProduct(d.ProductId);
            if (p?.IsExplosive == true && d.Primers.Count == 0) issues.Add($"Explosive deck {i + 1} has no primer");
            if (p?.IsExplosive == true && !p.WaterResistant && h.WaterDepth > 0 && d.Bottom > h.EffectiveLength - h.WaterDepth)
                issues.Add($"Deck {i + 1}: {p.Name} is not water resistant");
        }
        return issues;
    }
}

/// <summary>A kind of loading warning of a hole (<see cref="Charging.LoadingWarnings"/>).</summary>
public enum LoadingWarning
{
    /// <summary>Charged with no explosive deck.</summary>
    NoExplosive,
    /// <summary>An explosive deck without a primer.</summary>
    NoPrimer,
    /// <summary>The column leaves part of the depth uncovered.</summary>
    ColumnGap,
    /// <summary>The top stemming is shorter than the minimum.</summary>
    ShortStemming,
    /// <summary>A deck goes on below the toe.</summary>
    BelowToe,
    /// <summary>Two decks overlap.</summary>
    Overlap,
    /// <summary>A deck under water is not water resistant.</summary>
    NotWaterResistant
}
