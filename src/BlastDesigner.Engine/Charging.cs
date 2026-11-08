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
        var s = CalcService.Session(project);
        s.Scope.NoteParent(deck, hole);
        return Calculated(s, "deck.mass", deck, 0);
    }

    /// <summary>Explosive mass of the hole with its boosters, kg (<c>hole.chargeMass</c>).</summary>
    public static double ExplosiveMass(BlastProject project, Hole hole) => Calculated(CalcService.Session(project), "hole.chargeMass", hole, 0);

    /// <summary>Total length of the stemming decks, m (<c>hole.stemmingLength</c>).</summary>
    public static double StemmingLength(BlastProject project, Hole hole) => Calculated(CalcService.Session(project), "hole.stemmingLength", hole, 0);

    /// <summary>Length of the top stemming (collar to first non-stemming deck), <c>hole.topStemming</c>.</summary>
    public static double TopStemming(BlastProject project, Hole hole) => Calculated(CalcService.Session(project), "hole.topStemming", hole, hole.EffectiveLength);

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
            var offsets = t.PrimerOffsets is { } o && o.Count == t.PrimerCount ? o : null;
            for (int i = 0; i < t.PrimerCount; i++)
            {
                var depth = offsets != null ? bottom - offsets[i] : bottom - t.PrimerOffset - i * Math.Max(1, (bottom - top) / Math.Max(1, t.PrimerCount));
                deck.Primers.Add(new Primer { Depth = Math.Clamp(depth, top, bottom), BoosterId = t.BoosterId, DetonatorId = t.DetonatorId });
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
                PrimerCount = primers.Count,
                BoosterId = primers.FirstOrDefault()?.BoosterId,
                DetonatorId = primers.FirstOrDefault()?.DetonatorId,
                PrimerOffset = primers.Count > 0 ? Math.Round(d.Bottom - primers[0].Depth, 3) : 0.5,
                PrimerOffsets = primers.Count > 0 ? primers.Select(p => Math.Round(d.Bottom - p.Depth, 3)).ToList() : null
            });
        }
        return rule;
    }

    public static void Empty(IEnumerable<Hole> holes)
    {
        foreach (var h in holes)
        {
            h.Decks.Clear();
            h.ManualLoading = false;
            if (h.State == HoleState.Loaded) h.State = HoleState.Released;
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
    public static bool HasLoadingWarning(BlastProject project, Hole h)
    {
        if (h.Decks.Count == 0) return false;
        var expl = h.Decks.Where(d => project.FindProduct(d.ProductId)?.IsExplosive == true).ToList();
        return expl.Count == 0 || expl.Any(d => d.Primers.Count == 0) || ColumnGap(h) >= 0.05
            || TopStemming(project, h) < MinStemming(h) - 1e-6 || Validate(project, h).Count > 0;
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
