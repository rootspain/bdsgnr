using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>Initiation rules: which detonator and booster each hole gets, independent of the loading rules.</summary>
public static class Initiation
{
    public static int ApplyRules(BlastProject project, IEnumerable<Hole> holes)
    {
        var rules = project.InitiationRules.OrderBy(r => r.Priority).ToList();
        var rowNames = project.Rows.SelectMany(r => r.HoleIds.Select(id => (id, r.Name))).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().Name);
        int n = 0;
        foreach (var h in holes)
        {
            if (h.IsDummy) continue;
            rowNames.TryGetValue(h.Id, out var row);
            var rule = rules.FirstOrDefault(r => r.Matches(h, row));
            if (rule == null) continue;
            ApplyRule(project, h, rule);
            n++;
        }
        return n;
    }

    public static void ApplyRule(BlastProject project, Hole h, InitiationRule rule)
    {
        foreach (var d in h.Decks.Where(d => project.FindProduct(d.ProductId)?.IsExplosive == true))
        {
            if (rule.PrimersPerDeck > 0 && d.Primers.Count != rule.PrimersPerDeck)
            {
                d.Primers.Clear();
                for (int i = 0; i < rule.PrimersPerDeck; i++)
                {
                    var step = rule.PrimersPerDeck == 1 ? 0 : (d.Length - 2 * rule.PrimerOffset) / (rule.PrimersPerDeck - 1);
                    d.Primers.Add(new Primer { Depth = Math.Clamp(d.Bottom - rule.PrimerOffset - i * step, d.Top, d.Bottom) });
                }
            }
            foreach (var p in d.Primers)
            {
                if (rule.DetonatorId != null) p.DetonatorId = rule.DetonatorId;
                if (rule.BoosterId != null) p.BoosterId = rule.BoosterId;
                p.DelayMs = rule.DelayMs;
            }
        }
    }
}

/// <summary>Loading alternates: swaps the explosive in wet, hard or soft intervals after a rule has built the column.</summary>
public static class LoadingAlternates
{
    public static void Apply(BlastProject project, Hole h, LoadingRule rule)
    {
        var len = h.EffectiveLength;
        if (rule.WetAlternateProductId != null && h.WaterDepth > 0)
            Replace(project, h, len - h.WaterDepth, len, rule.WetAlternateProductId.Value, p => !p.WaterResistant);
        if (h.Mwd is { Count: > 0 })
        {
            foreach (var (top, bottom, hardness) in Intervals(h.Mwd, len))
            {
                if (rule.HardAlternateProductId != null && hardness >= rule.HardnessThreshold)
                    Replace(project, h, top, bottom, rule.HardAlternateProductId.Value, _ => true);
                else if (rule.SoftAlternateProductId != null && hardness < rule.SoftnessThreshold)
                    Replace(project, h, top, bottom, rule.SoftAlternateProductId.Value, _ => true);
            }
            if (rule.SoftCollarExtraStemming > 0 && h.Mwd.OrderBy(s => s.Depth).First().Hardness < rule.SoftnessThreshold)
                ExtendStemming(project, h, rule.SoftCollarExtraStemming);
        }
    }

    /// <summary>Converts the MWD samples into constant-hardness intervals (each sample covers half way to its neighbours).</summary>
    public static List<(double Top, double Bottom, double Hardness)> Intervals(IReadOnlyList<MwdSample> samples, double length)
    {
        var s = samples.OrderBy(x => x.Depth).ToList();
        var result = new List<(double, double, double)>();
        for (int i = 0; i < s.Count; i++)
        {
            var top = i == 0 ? 0 : (s[i - 1].Depth + s[i].Depth) / 2;
            var bottom = i == s.Count - 1 ? length : (s[i].Depth + s[i + 1].Depth) / 2;
            if (bottom > top) result.Add((top, Math.Min(bottom, length), s[i].Hardness));
        }
        return result;
    }

    /// <summary>Replaces the product of explosive decks inside [top, bottom], splitting decks at the interval ends.</summary>
    public static void Replace(BlastProject project, Hole h, double top, double bottom, Guid productId, Func<Product, bool> applies)
    {
        var result = new List<Deck>();
        foreach (var d in h.Decks.OrderBy(d => d.Top))
        {
            var p = project.FindProduct(d.ProductId);
            if (p?.IsExplosive != true || !applies(p) || d.ProductId == productId || d.Bottom <= top || d.Top >= bottom)
            {
                result.Add(d);
                continue;
            }
            var cuts = new[] { d.Top, Math.Max(d.Top, top), Math.Min(d.Bottom, bottom), d.Bottom };
            var pieces = new List<Deck>();
            for (int i = 0; i < 3; i++)
                if (cuts[i + 1] - cuts[i] >= 1e-6)
                    pieces.Add(new Deck { Top = cuts[i], Bottom = cuts[i + 1], ProductId = i == 1 ? productId : d.ProductId });
            // Each primer stays in the piece that contains its depth; the deepest piece also takes primers at the bottom.
            foreach (var pr in d.Primers)
                (pieces.FirstOrDefault(x => pr.Depth >= x.Top - 1e-9 && pr.Depth < x.Bottom) ?? pieces[^1]).Primers.Add(pr);
            result.AddRange(pieces);
        }
        h.Decks = result;
    }

    private static void ExtendStemming(BlastProject project, Hole h, double extra)
    {
        var stem = h.Decks.OrderBy(d => d.Top).FirstOrDefault();
        if (stem == null || project.FindProduct(stem.ProductId)?.Kind != ProductKind.Stemming) return;
        var newBottom = stem.Bottom + extra;
        foreach (var d in h.Decks.Where(d => d != stem && d.Top < newBottom).ToList())
        {
            d.Top = newBottom;
            if (d.Bottom <= d.Top + 1e-6) h.Decks.Remove(d);
            else foreach (var p in d.Primers) p.Depth = Math.Max(p.Depth, d.Top);
        }
        stem.Bottom = Math.Min(newBottom, h.EffectiveLength);
    }
}

/// <summary>Result of checking one hole against the design tolerances.</summary>
public sealed record ToleranceResult(Hole Hole, double CollarDev, double DepthDev, double ToeDev, double AngleDev, double BearingDev, List<string> Failures)
{
    public bool Pass => Failures.Count == 0;
}

/// <summary>Compares as-drilled data (actual collar, dipped length, bore track) with the design.</summary>
public static class Tolerance
{
    public static List<ToleranceResult> Check(BlastProject project, IEnumerable<Hole>? holes = null)
    {
        var t = project.Tolerances;
        var list = new List<ToleranceResult>();
        foreach (var h in (holes ?? project.Holes).Where(h => !h.IsDummy))
        {
            var collar = h.ActualCollar ?? h.BoreTrack?.FirstOrDefault() ?? h.Collar;
            var toe = h.BoreTrack is { Count: > 1 } ? h.BoreTrack[^1] : (h.ActualLength is double al ? collar + h.Direction * al : h.DesignToe);
            var collarDev = Vec3.Distance2D(collar, h.Collar);
            var depthDev = (h.ActualLength ?? (h.BoreTrack is { Count: > 1 } ? GeoMath.Length(h.BoreTrack) : h.Length)) - h.Length;
            var toeDev = Vec3.Distance2D(toe, h.DesignToe);
            var axis = toe - collar;
            double angleDev = 0, bearingDev = 0;
            if (axis.Length > 1e-6)
            {
                var actualAngle = Math.Acos(Math.Clamp(-axis.Z / axis.Length, -1, 1)) / GeoMath.Deg;
                angleDev = actualAngle - h.Angle;
                if (h.Angle > 5 && axis.Length2D > 1e-6)
                    bearingDev = ((GeoMath.Bearing(collar, toe) - h.Bearing + 540) % 360) - 180;
            }
            var f = new List<string>();
            if (collarDev > t.CollarXY) f.Add("Collar position");
            if (Math.Abs(depthDev) > t.Depth) f.Add(depthDev < 0 ? "Short" : "Over-drilled");
            if (toeDev > t.ToeXY) f.Add("Toe position");
            if (Math.Abs(angleDev) > t.AngleDeg) f.Add("Angle");
            if (Math.Abs(bearingDev) > t.BearingDeg) f.Add("Bearing");
            if (depthDev < -t.RedrillFraction * h.Length) f.Add("Redrill");
            list.Add(new ToleranceResult(h, collarDev, depthDev, toeDev, angleDev, bearingDev, f));
        }
        return list;
    }
}

/// <summary>Intersection of a hole with a surface, as depth along the hole.</summary>
public sealed record StrataIntercept(Hole Hole, SurfaceEntity Surface, double Depth, Vec3 Point);

public static class Strata
{
    /// <summary>Finds where each hole crosses each surface by stepping along the hole and refining by bisection.</summary>
    public static List<StrataIntercept> Intercepts(IEnumerable<Hole> holes, IEnumerable<SurfaceEntity> surfaces, double step = 0.25)
    {
        var result = new List<StrataIntercept>();
        var surfs = surfaces.ToList();
        foreach (var h in holes)
        {
            var len = h.EffectiveLength;
            foreach (var s in surfs)
            {
                double? prev = null;
                double prevD = 0;
                for (double d = 0; d <= len + 1e-9; d += step)
                {
                    var p = h.PointAtDepth(Math.Min(d, len));
                    var z = s.ElevationAt(p.X, p.Y);
                    double? diff = z.HasValue ? p.Z - z.Value : null;
                    if (diff == 0)
                    {
                        // A sample exactly on the surface: record it once and restart the sign tracking below it.
                        if (prev != null || d == 0) result.Add(new StrataIntercept(h, s, Math.Min(d, len), p));
                        prev = null;
                        continue;
                    }
                    if (diff.HasValue && prev.HasValue && Math.Sign(diff.Value) != Math.Sign(prev.Value))
                    {
                        double a = prevD, b = Math.Min(d, len);
                        for (int i = 0; i < 30; i++)
                        {
                            var m = (a + b) / 2;
                            var pm = h.PointAtDepth(m);
                            var zm = s.ElevationAt(pm.X, pm.Y);
                            if (!zm.HasValue) break;
                            if (Math.Sign(pm.Z - zm.Value) == Math.Sign(prev.Value)) a = m; else b = m;
                        }
                        var depth = (a + b) / 2;
                        result.Add(new StrataIntercept(h, s, depth, h.PointAtDepth(depth)));
                    }
                    if (diff.HasValue) { prev = diff; prevD = Math.Min(d, len); }
                }
            }
        }
        return result;
    }

    /// <summary>Material type of each interval between intercepts: index of the strata surface above (1-based), 0 above the first.</summary>
    public static int AssignMaterialByToe(BlastProject project, IEnumerable<Hole> holes, IReadOnlyList<SurfaceEntity> orderedTopDown)
    {
        int n = 0;
        foreach (var h in holes)
        {
            var toe = h.Toe;
            for (int i = orderedTopDown.Count - 1; i >= 0; i--)
            {
                var z = orderedTopDown[i].ElevationAt(toe.X, toe.Y);
                if (z.HasValue && toe.Z <= z.Value)
                {
                    h.MaterialType = Math.Clamp(i + 1, 1, 8);
                    n++;
                    break;
                }
            }
        }
        return n;
    }
}
