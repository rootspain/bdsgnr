using BlastDesigner.Calc;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.Tests;

/// <summary>
/// Расчёты Blast Designer до движка вычислений, перенесённые без изменений: эталон, с которым сверяется встроенный
/// профиль «SHOTPlus 6» (Charging, Quantities, Flyrock, Fragmentation, ExclusionZone, Vibration).
/// </summary>
internal static class Legacy
{
    public static double DeckMass(BlastProject project, Hole hole, Deck deck)
    {
        var p = project.FindProduct(deck.ProductId);
        if (p == null || p.Kind == ProductKind.GasBag) return 0;
        if (p.Kind == ProductKind.PackagedExplosive && p.UnitLength > 0 && p.UnitMass > 0)
            return Math.Floor(deck.Length / p.UnitLength + 1e-9) * p.UnitMass;
        var factor = p.IsExplosive ? hole.LoadingFactor : 1;
        return hole.VolumeBetween(deck.Top, deck.Bottom) * p.Density * 1000 * factor;
    }

    public static double ExplosiveMass(BlastProject project, Hole hole) =>
        hole.Decks.Where(d => project.FindProduct(d.ProductId)?.IsExplosive == true).Sum(d => DeckMass(project, hole, d))
        + hole.Decks.SelectMany(d => d.Primers).Sum(pr => project.FindProduct(pr.BoosterId)?.UnitMass ?? 0);

    public static double StemmingLength(BlastProject project, Hole hole) =>
        hole.Decks.Where(d => project.FindProduct(d.ProductId)?.Kind == ProductKind.Stemming).Sum(d => d.Length);

    public static double TopStemming(BlastProject project, Hole hole)
    {
        var first = hole.Decks.OrderBy(d => d.Top).FirstOrDefault(d => project.FindProduct(d.ProductId)?.Kind != ProductKind.Stemming);
        return first?.Top ?? hole.EffectiveLength;
    }

    public static double Mic(BlastProject project, double windowMs)
    {
        var events = project.Holes.Where(h => h.FiringTimeMs.HasValue && !h.IsDummy)
            .Select(h => (T: h.FiringTimeMs!.Value, Q: ExplosiveMass(project, h))).OrderBy(e => e.T).ToList();
        double best = 0, q = 0;
        int j = 0;
        for (int i = 0; i < events.Count; i++)
        {
            if (i > 0) q -= events[i - 1].Q;
            if (j < i) { j = i; q = 0; }
            while (j < events.Count && events[j].T < events[i].T + windowMs) { q += events[j].Q; j++; }
            best = Math.Max(best, q);
        }
        return best;
    }

    public sealed record Q(int HoleCount, int DummyCount, double Drilled, double Volume, double Tonnes, double Kg, double Pf, double PfT, double Energy,
        double ProductCost, double DrillingCost, double LoadingCost, double FixedCost, double Total, double PerTonne, double Burden, double Spacing, double Mic,
        List<(Hole H, double Area, double Volume, double Tonnes, double Kg, double Stem)> PerHole);

    public static Q Quantities(BlastProject project, IEnumerable<Hole>? subset = null, IReadOnlyList<Vec3>? boundary = null)
    {
        var holes = (subset ?? project.Holes).ToList();
        var real = holes.Where(h => !h.IsDummy).ToList();
        var allReal = project.Holes.Where(h => !h.IsDummy).ToList();
        if (allReal.Count == 0 || real.Any(h => !allReal.Contains(h))) allReal = real;
        boundary ??= Engine.Quantities.DefaultBoundary(project, allReal);
        var cells = Engine.Quantities.VoronoiAreas(allReal, boundary);
        var perHole = new List<(Hole, double, double, double, double, double)>();
        double vol = 0, tonnes = 0, kg = 0, energy = 0;
        foreach (var h in real)
        {
            var area = cells.TryGetValue(h.Id, out var a) ? a : project.Design.Burden * project.Design.Spacing;
            var bench = Math.Max(0, (h.Length - h.Subdrill) * Math.Max(0.05, -h.Direction.Z));
            var v = area * bench;
            var density = project.Design.MaterialTypes.FirstOrDefault(m => m.Index == h.MaterialType)?.Density ?? project.Design.RockDensity;
            var t = v * density;
            var q = ExplosiveMass(project, h);
            perHole.Add((h, area, v, t, q, StemmingLength(project, h)));
            vol += v; tonnes += t; kg += q;
            foreach (var d in h.Decks)
                if (project.FindProduct(d.ProductId) is { IsExplosive: true } p)
                    energy += DeckMass(project, h, d) * p.Energy;
        }
        var amounts = new Dictionary<string, double>();
        void Add(string? id, double amount) { if (id != null) amounts[id] = amounts.GetValueOrDefault(id) + amount; }
        foreach (var h in real)
            foreach (var d in h.Decks)
            {
                var p = project.FindProduct(d.ProductId);
                if (p == null) continue;
                Add(p.Id, p.Kind == ProductKind.GasBag ? 1 : DeckMass(project, h, d));
                foreach (var pr in d.Primers) { Add(pr.BoosterId, 1); Add(pr.DetonatorId, 1); }
            }
        foreach (var t in project.Ties) Add(t.ProductId, 1);
        double productCost = 0;
        foreach (var (id, amount) in amounts)
            if (project.FindProduct(id) is { } p) productCost += amount * p.Cost;
        var drilled = real.Sum(h => h.EffectiveLength);
        var (b, s) = Engine.Quantities.PatternEstimate(project);
        var drillingCost = drilled * project.Costs.DrillingPerMetre;
        var loadingCost = real.Count(h => h.Decks.Count > 0) * project.Costs.LoadingPerHole;
        var fixedCost = project.Costs.LabourPerBlast + project.Costs.OtherFixed;
        var total = productCost + drillingCost + loadingCost + fixedCost;
        return new Q(real.Count, holes.Count - real.Count, drilled, vol, tonnes, kg, vol > 0 ? kg / vol : 0, tonnes > 0 ? kg / tonnes : 0,
            tonnes > 0 ? energy / tonnes : 0, productCost, drillingCost, loadingCost, fixedCost, total, tonnes > 0 ? total / tonnes : 0, b, s,
            Mic(project, project.Vibration.WindowMs), perHole);
    }

    public sealed record FlyHole(Hole Hole, double KgPerM, double Burden, double Stemming, double FaceBurst, double Cratering, double Rifling)
    {
        public double Max => Math.Max(FaceBurst, Math.Max(Cratering, Rifling));
    }

    public static (List<FlyHole> Holes, double Max, double Clearance) Flyrock(BlastProject project, IEnumerable<Hole>? holes = null)
    {
        var m = project.Flyrock;
        var crest = project.Entities.OfType<PolylineEntity>().FirstOrDefault(l => l.Kind == LineKind.Crest);
        var list = new List<FlyHole>();
        foreach (var h in (holes ?? project.Holes).Where(h => !h.IsDummy && h.Decks.Count > 0))
        {
            var explosive = h.Decks.Where(d => project.FindProduct(d.ProductId)?.IsExplosive == true).ToList();
            if (explosive.Count == 0) continue;
            var top = explosive.Min(d => d.Top);
            var kgPerM = explosive.Max(d => d.Length > 0 ? DeckMass(project, h, d) / d.Length : 0);
            var burden = crest != null ? GeoMath.DistanceToPolyline2D(crest.Points, h.Collar, crest.Closed) : project.Design.Burden;
            list.Add(new FlyHole(h, kgPerM, burden, top,
                crest != null ? Engine.Flyrock.FaceBurst(m.SiteK, kgPerM, burden) : 0,
                Engine.Flyrock.Cratering(m.SiteK, kgPerM, top),
                Engine.Flyrock.Rifling(m.SiteK, kgPerM, top, h.Angle)));
        }
        var max = list.OrderByDescending(r => r.Max).FirstOrDefault()?.Max ?? 0;
        return (list, max, Math.Max(m.MinimumClearance, max * m.SafetyFactor));
    }

    public sealed record Frag(double A, double Pf, double Charge, double Rws, double X50, double N, double X20, double X80, double Over, double Fines);

    public static Frag Fragmentation(BlastProject project, IEnumerable<Hole>? holes = null)
    {
        var list = (holes ?? project.Holes).Where(h => !h.IsDummy).ToList();
        var q = Quantities(project, list);
        var loaded = list.Where(h => h.Decks.Count > 0).ToList();
        var charge = loaded.Count > 0 ? loaded.Average(h => ExplosiveMass(project, h)) : 0;
        var explosiveDecks = loaded.SelectMany(h => h.Decks.Select(d => (h, d, p: project.FindProduct(d.ProductId)))).Where(x => x.p?.IsExplosive == true).ToList();
        var rws = explosiveDecks.Count > 0 ? explosiveDecks.Sum(x => x.p!.RelativeWeightStrength * x.d.Length) / Math.Max(1e-9, explosiveDecks.Sum(x => x.d.Length)) : 100;
        var chargeLength = loaded.Count > 0 ? loaded.Average(h => h.Decks.Where(d => project.FindProduct(d.ProductId)?.IsExplosive == true).Sum(d => d.Length)) : 0;
        var bench = list.Count > 0 ? list.Average(h => Math.Max(0.1, h.Length - h.Subdrill) * Math.Cos(h.Angle * GeoMath.Deg)) : project.Design.BenchHeight;
        var diameter = list.Count > 0 ? list.Average(h => h.Diameter) : project.Design.Diameter;
        var burden = q.Burden > 0 ? q.Burden : project.Design.Burden;
        var spacing = q.Spacing > 0 ? q.Spacing : project.Design.Spacing;
        var m = project.Fragmentation;
        var a = Engine.Fragmentation.RockFactor(m, project.Design.RockDensity);
        var pf = q.Pf;
        if (pf <= 0 || charge <= 0) return new Frag(a, pf, charge, rws, 0, 0, 0, 0, 0, 0);
        var x50 = Engine.Fragmentation.X50(a, pf, charge, rws);
        var n = Engine.Fragmentation.Uniformity(burden, spacing, diameter, m.DrillDeviation, chargeLength, bench);
        return new Frag(a, pf, charge, rws, x50, n, Engine.Fragmentation.SizeAt(0.2, x50, n), Engine.Fragmentation.SizeAt(0.8, x50, n),
            100 * (1 - Engine.Fragmentation.RosinRammler(m.OversizeCm, x50, n)), 100 * Engine.Fragmentation.RosinRammler(m.FinesCm, x50, n));
    }

    public static (double Mic, double Vib, double Air, double Fly, double Radius) Exclusion(BlastProject project)
    {
        Timing.Solve(project);
        var mic = Mic(project, project.Vibration.WindowMs);
        if (mic <= 0) mic = project.Holes.Select(h => ExplosiveMass(project, h)).DefaultIfEmpty(0).Max();
        var v = project.Vibration;
        var vib = SiteLaw.DistanceForPpv(v.SiteK, v.SiteB, mic, v.PpvLimit);
        var air = SiteLaw.DistanceForAirblast(v, mic, v.AirblastLimitDb);
        var fly = Flyrock(project).Clearance;
        return (mic, vib, air, fly, Math.Max(vib, Math.Max(air, fly)));
    }
}

/// <summary>Сверка встроенного профиля «SHOTPlus 6» со старым кодом на сгенерированных взрывах (допуск 1e-9).</summary>
public class CalcParityTests
{
    private static void Same(double expected, double actual, string what)
    {
        var tol = 1e-9 * Math.Max(1, Math.Abs(expected));
        if (double.IsPositiveInfinity(expected) && double.IsPositiveInfinity(actual)) return;
        Assert.True(Math.Abs(expected - actual) <= tol, $"{what}: ожидалось {expected:R}, движок {actual:R}");
    }

    /// <summary>Взрыв со всем, что влияет на расчёты: разные продукты и колонки, пустые и чужие продукты, фиктивные и наклонные скважины, материалы, ряды, связи и бровка.</summary>
    internal static BlastProject Generate(int seed, bool crest = true, bool timing = true)
    {
        var rnd = new Random(seed);
        var p = BlastProject.CreateNew($"gen {seed}");
        var anfo = p.Products.First(x => x.Name == "ANFO");
        var heavy = p.Products.First(x => x.Name == "Heavy ANFO 70/30");
        var emulsion = p.Products.First(x => x.Name == "Bulk emulsion");
        var packaged = p.Products.First(x => x.Kind == ProductKind.PackagedExplosive);
        var stem = p.Products.First(x => x.Name == "Crushed aggregate");
        var gas = p.Products.First(x => x.Kind == ProductKind.GasBag);
        var boosters = p.Products.Where(x => x.Kind == ProductKind.Booster).ToList();
        var dets = p.Products.Where(x => x.Kind == ProductKind.DownholeDetonator).ToList();
        var connectors = p.Products.Where(x => x.Kind == ProductKind.SurfaceConnector).ToList();
        p.Design.Burden = 3 + rnd.NextDouble() * 2;
        p.Design.Spacing = p.Design.Burden * (1 + rnd.NextDouble() * 0.3);
        p.Design.RockDensity = 2.4 + rnd.NextDouble() * 0.4;
        p.Design.MaterialTypes[1].Density = 2.9;
        p.Costs.OtherFixed = rnd.Next(0, 300);
        p.Vibration.SiteK = 800 + rnd.Next(0, 800);
        p.Vibration.WindowMs = rnd.Next(5, 20);
        p.Flyrock.SiteK = 13.5 + rnd.NextDouble() * 10;
        p.Fragmentation.RockFactor = seed % 3 == 0 ? 0 : 4 + rnd.NextDouble() * 6;
        p.Fragmentation.YoungsModulus = seed % 2 == 0 ? 30 : 60;

        int rows = 2 + rnd.Next(3), perRow = 3 + rnd.Next(5);
        var all = new List<Hole>();
        for (int r = 0; r < rows; r++)
        {
            var row = new HoleRow { Name = $"Row {r + 1}" };
            for (int i = 0; i < perRow; i++)
            {
                var h = p.Add(new Hole
                {
                    Label = $"{r + 1}-{i + 1}",
                    Collar = new Vec3(i * p.Design.Spacing + (r % 2) * p.Design.Spacing / 2 + rnd.NextDouble() * 0.3, r * p.Design.Burden + rnd.NextDouble() * 0.3, 100 + rnd.NextDouble()),
                    Length = 8 + rnd.NextDouble() * 6,
                    Diameter = new[] { 89, 102, 115, 127 }[rnd.Next(4)],
                    Subdrill = rnd.NextDouble(),
                    Angle = rnd.Next(4) == 0 ? rnd.Next(5, 25) : 0,
                    Bearing = rnd.Next(360),
                    MaterialType = 1 + rnd.Next(2),
                    HoleType = 1 + rnd.Next(3),
                    IsDummy = rnd.Next(12) == 0,
                    LoadingFactor = rnd.Next(5) == 0 ? 1.1 : 1,
                    ActualLength = rnd.Next(6) == 0 ? 9 + rnd.NextDouble() : null,
                    RowId = row.Id
                });
                row.HoleIds.Add(h.Id);
                all.Add(h);
                if (h.IsDummy || rnd.Next(15) == 0) continue;
                var len = h.EffectiveLength;
                var stemLen = 2 + rnd.NextDouble() * 1.5;
                var kind = rnd.Next(6);
                var decks = new List<Deck> { new() { Top = 0, Bottom = stemLen, ProductId = stem.Id } };
                if (kind == 0)
                {
                    // колонка с газовым мешком и промежуточной забойкой
                    var mid = stemLen + (len - stemLen) / 2;
                    decks.Add(new Deck { Top = stemLen, Bottom = mid - 0.5, ProductId = anfo.Id });
                    decks.Add(new Deck { Top = mid - 0.5, Bottom = mid, ProductId = gas.Id });
                    decks.Add(new Deck { Top = mid, Bottom = len, ProductId = emulsion.Id });
                }
                else if (kind == 1) decks.Add(new Deck { Top = stemLen, Bottom = len, ProductId = packaged.Id });
                else if (kind == 2) decks.Add(new Deck { Top = stemLen, Bottom = len, ProductId = "missing-product" });
                else if (kind == 3)
                {
                    decks.Add(new Deck { Top = stemLen + 1, Bottom = len, ProductId = heavy.Id });
                    decks.Add(new Deck { Top = stemLen, Bottom = stemLen + 1, ProductId = anfo.Id });
                }
                else decks.Add(new Deck { Top = stemLen, Bottom = len, ProductId = rnd.Next(2) == 0 ? anfo.Id : emulsion.Id });
                foreach (var d in decks.Where(d => p.FindProduct(d.ProductId)?.IsExplosive == true || d.ProductId == "missing-product"))
                    d.Primers.Add(new Primer { Depth = d.Bottom - 0.5, BoosterId = rnd.Next(8) == 0 ? null : boosters[rnd.Next(boosters.Count)].Id, DetonatorId = dets[rnd.Next(dets.Count)].Id });
                h.Decks.AddRange(decks);
            }
            p.Rows.Add(row);
        }
        if (timing && all.Count(h => !h.IsDummy) > 0)
        {
            var first = all.First();
            p.Add(new InitiationPoint { Position = first.Collar, HoleId = first.Id, StartTimeMs = 0 });
            foreach (var row in p.Rows)
            {
                var rh = row.HoleIds.Select(p.FindEntity).OfType<Hole>().ToList();
                for (int i = 1; i < rh.Count; i++) Timing.Connect(p, rh[i - 1].Id, rh[i].Id, connectors[rnd.Next(connectors.Count)]);
            }
            for (int r = 1; r < p.Rows.Count; r++)
                Timing.Connect(p, p.Rows[r - 1].HoleIds[0], p.Rows[r].HoleIds[0], connectors[rnd.Next(connectors.Count)]);
            Timing.Solve(p);
        }
        if (crest)
            p.Add(new PolylineEntity { Kind = LineKind.Crest, Points = { new Vec3(-5, -2, 100), new Vec3(perRow * p.Design.Spacing + 5, -2.5, 100) } });
        p.Add(new PointEntity { Kind = PointKind.Monitoring, Position = new Vec3(300 + rnd.Next(200), 200, 100), Label = "M1" });
        return p;
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 12).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Charging_MatchesLegacy(int seed)
    {
        var p = Generate(seed);
        var s = CalcService.Session(p);
        Assert.False(s.Model.HasErrors, string.Join("\n", s.Model.Diagnostics));
        foreach (var h in p.Holes)
        {
            foreach (var d in h.Decks) Same(Legacy.DeckMass(p, h, d), s.Number("deck.mass", d), $"deck.mass {h.Label}");
            Same(Legacy.ExplosiveMass(p, h), s.Number("hole.chargeMass", h), $"chargeMass {h.Label}");
            Same(Legacy.StemmingLength(p, h), s.Number("hole.stemmingLength", h), $"stemming {h.Label}");
            Same(Legacy.TopStemming(p, h), s.Number("hole.topStemming", h), $"topStemming {h.Label}");
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Quantities_MatchLegacy(int seed)
    {
        var p = Generate(seed);
        var q = Legacy.Quantities(p);
        var s = CalcService.Session(p);
        Same(q.HoleCount, s.Number("blast.holeCount", p), "holeCount");
        Same(q.DummyCount, s.Number("blast.dummyCount", p), "dummyCount");
        Same(q.Drilled, s.Number("blast.drilledMetres", p), "drilled");
        Same(q.Volume, s.Number("blast.volume", p), "volume");
        Same(q.Tonnes, s.Number("blast.tonnes", p), "tonnes");
        Same(q.Kg, s.Number("blast.explosiveKg", p), "kg");
        Same(q.Pf, s.Number("blast.powderFactor", p), "pf");
        Same(q.PfT, s.Number("blast.powderFactorKgT", p), "pf kg/t");
        Same(q.Energy, s.Number("blast.energyFactor", p), "energy MJ/t");
        Same(q.ProductCost, s.Number("blast.productCost", p), "productCost");
        Same(q.DrillingCost, s.Number("blast.drillingCost", p), "drillingCost");
        Same(q.LoadingCost, s.Number("blast.loadingCost", p), "loadingCost");
        Same(q.FixedCost, s.Number("blast.fixedCost", p), "fixedCost");
        Same(q.Total, s.Number("blast.totalCost", p), "total");
        Same(q.PerTonne, s.Number("blast.costPerTonne", p), "perTonne");
        Same(q.Burden, s.Number("blast.avgBurden", p), "burden");
        Same(q.Spacing, s.Number("blast.avgSpacing", p), "spacing");
        Same(q.Mic, s.Number("blast.mic", p), "mic");
        foreach (var x in q.PerHole)
        {
            Same(x.Area, s.Number("hole.area", x.H), "area");
            Same(x.Volume, s.Number("hole.volume", x.H), "hole volume");
            Same(x.Tonnes, s.Number("hole.tonnes", x.H), "hole tonnes");
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void QuantitiesOfSubset_MatchLegacy(int seed)
    {
        var p = Generate(seed);
        var subset = p.Holes.Where((_, i) => i % 3 != 1).ToList();
        var boundary = seed % 2 == 0 ? null : new List<Vec3> { new(-10, -10, 0), new(60, -10, 0), new(60, 40, 0), new(-10, 40, 0) };
        var q = Legacy.Quantities(p, subset, boundary);
        var s = CalcService.Session(p);
        s.Scope.Subset = subset;
        CalcService.ConfigureCells(s, subset, boundary);
        Same(q.Volume, s.Number("blast.volume", p), "volume");
        Same(q.Tonnes, s.Number("blast.tonnes", p), "tonnes");
        Same(q.Pf, s.Number("blast.powderFactor", p), "pf");
        Same(q.ProductCost, s.Number("blast.productCost", p), "productCost");
        Same(q.Total, s.Number("blast.totalCost", p), "total");
        Same(q.Energy, s.Number("blast.energyFactor", p), "energy");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Predictions_MatchLegacy(int seed)
    {
        var p = Generate(seed, crest: seed % 4 != 0, timing: seed % 5 != 0);
        var fly = Legacy.Flyrock(p);
        var frag = Legacy.Fragmentation(p);
        var s = CalcService.Session(p);
        foreach (var h in fly.Holes)
        {
            Same(h.KgPerM, s.Number("hole.kgPerMetre", h.Hole), "kg/m");
            Same(h.Burden, s.Number("hole.flyrockBurden", h.Hole), "fly burden");
            Same(h.Stemming, s.Number("hole.explosiveTop", h.Hole), "explosive top");
            Same(h.FaceBurst, s.Number("hole.faceBurst", h.Hole), "face burst");
            Same(h.Cratering, s.Number("hole.cratering", h.Hole), "cratering");
            Same(h.Rifling, s.Number("hole.rifling", h.Hole), "rifling");
        }
        Same(fly.Max, s.Number("blast.flyrockMax", p), "fly max");
        Same(fly.Clearance, s.Number("blast.flyrockClearance", p), "clearance");

        Same(frag.A, s.Number("blast.rockFactor", p), "A");
        Same(frag.Charge, s.Number("blast.avgCharge", p), "avg charge");
        Same(frag.Rws, s.Number("blast.avgRws", p), "rws");
        Same(frag.X50, s.Number("blast.x50", p), "x50");
        Same(frag.N, s.Number("blast.uniformity", p), "n");
        Same(frag.X20, s.Number("blast.x20", p), "x20");
        Same(frag.X80, s.Number("blast.x80", p), "x80");
        Same(frag.Over, s.Number("blast.oversizePct", p), "oversize");
        Same(frag.Fines, s.Number("blast.finesPct", p), "fines");

        var ex = Legacy.Exclusion(p);
        s = CalcService.Session(p);
        Same(ex.Mic, s.Number("blast.micOrMaxCharge", p), "mic for zone");
        Same(ex.Vib, s.Number("blast.vibrationRadius", p), "vibration radius");
        Same(ex.Air, s.Number("blast.airblastRadius", p), "airblast radius");
        Same(ex.Fly, s.Number("blast.flyrockClearance", p), "fly clearance");
        Same(ex.Radius, s.Number("blast.exclusionRadius", p), "radius");

        var monitor = p.Entities.OfType<PointEntity>().First(x => x.Kind == PointKind.Monitoring);
        var mic = Legacy.Mic(p, p.Vibration.WindowMs);
        var distance = p.Holes.Where(h => !h.IsDummy && Legacy.ExplosiveMass(p, h) > 0).Min(h => Vec3.Distance(h.Collar, monitor.Position));
        Same(distance, s.Number("monitor.distance", monitor), "monitor distance");
        Same(Vibration.Ppv(p.Vibration, distance, mic), s.Number("monitor.ppv", monitor), "monitor ppv");
        Same(Vibration.KPaToDb(Vibration.AirblastKPa(p.Vibration, distance, mic)), s.Number("monitor.airblastDb", monitor), "monitor airblast");
        Same(Vibration.Predict(p, monitor.Position).Ppv, s.Number("monitor.ppvPredicted", monitor), "predicted ppv");
    }

    [Fact]
    public void EmptyBlast_MatchesLegacyAndProfileTests()
    {
        var p = BlastProject.CreateNew();
        var s = CalcService.Session(p);
        var q = Legacy.Quantities(p);
        Same(q.Volume, s.Number("blast.volume", p), "volume");
        Same(q.Pf, s.Number("blast.powderFactor", p), "pf");
        Same(q.Total, s.Number("blast.totalCost", p), "total");
        var f = Legacy.Fragmentation(p);
        Same(f.X50, s.Number("blast.x50", p), "x50");
        foreach (var t in CalcService.BuiltIn.Tests)
        {
            var v = s.Evaluate(s.Model.CompileExpression("blast", t.Expression), p);
            Assert.False(v.IsError, v.Error);
            Assert.InRange(v.Number, t.Expected - t.Tolerance, t.Expected + t.Tolerance);
        }
    }

    [Fact]
    public void BuiltInProfile_CompilesWithoutErrorsAndOrdersStages()
    {
        var m = CalcService.BuiltInModel;
        Assert.False(m.HasErrors, string.Join("\n", m.Diagnostics));
        var order = m.Order.Select(n => n.Id).ToList();
        Assert.True(order.IndexOf("deck.mass") < order.IndexOf("hole.chargeMass"));
        Assert.True(order.IndexOf("hole.chargeMass") < order.IndexOf("blast.powderFactor"));
        Assert.True(order.IndexOf("blast.powderFactor") < order.IndexOf("blast.x50"));
        Assert.True(order.IndexOf("blast.flyrockClearance") < order.IndexOf("blast.exclusionRadius"));
        // плотность продукта трогает массы, итоги и прогнозы, но не ячейки
        var touched = m.Downstream("product.density").Select(n => n.Id).ToList();
        Assert.Contains("hole.chargeMass", touched);
        Assert.Contains("blast.x50", touched);
        Assert.DoesNotContain("hole.area", touched);
    }

    [Fact]
    public void Sync_AfterEdit_RecomputesSameAsFreshSession()
    {
        var p = Generate(7);
        var s = CalcService.Session(p);
        s.Sync();
        var before = s.Number("blast.powderFactor", p);
        var h = p.Holes.First(x => !x.IsDummy && x.Decks.Count > 1);
        h.Diameter += 13;
        p.Products.First(x => x.Name == "ANFO").Density = 0.9;
        Assert.True(s.Sync() > 0);
        var fresh = CalcService.Session(p);
        Same(fresh.Number("blast.powderFactor", p), s.Number("blast.powderFactor", p), "pf after edit");
        Same(fresh.Number("blast.x50", p), s.Number("blast.x50", p), "x50 after edit");
        Assert.NotEqual(before, s.Number("blast.powderFactor", p));
    }
}

public class CalcProjectProfileTests
{
    [Fact]
    public void OldProjectFile_WithoutProfile_OpensAndUsesBuiltIn()
    {
        var p = CalcParityTests.Generate(3);
        var json = System.Text.Encoding.UTF8.GetString(BlastDesigner.Domain.ProjectJson.Serialize(p));
        Assert.DoesNotContain("\"calc\"", json);
        var back = BlastDesigner.Domain.ProjectJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Null(back.Calc);
        Assert.Same(CalcService.BuiltInModel, CalcService.ModelFor(back));
    }

    [Fact]
    public void ProjectProfile_StoredWithHash_AndUsedByOldMethods()
    {
        var p = CalcParityTests.Generate(4);
        var h = p.Holes.First(x => !x.IsDummy && x.Decks.Count > 0);
        var before = Charging.ExplosiveMass(p, h);
        var mine = CalcProfile.DeriveFrom(CalcService.BuiltIn, "Рудник А");
        mine.Formulas.Add(new FormulaDef { Property = "hole.chargeMass", Expression = "2 * (sum(decks where product.isExplosive, mass) + sum(primers, coalesce(booster.unitMass, 0)))" });
        CalcService.SetProfile(p, mine);
        Assert.Equal("Рудник А", p.Calc!.ProfileName);
        Assert.False(string.IsNullOrEmpty(p.Calc.ProfileHash));
        Assert.Equal(2 * before, Charging.ExplosiveMass(p, h), 9);

        // копия профиля едет с файлом: без библиотеки на другом компьютере считается так же
        var back = BlastDesigner.Domain.ProjectJson.Deserialize(BlastDesigner.Domain.ProjectJson.Serialize(p));
        var h2 = back.Holes.First(x => x.Id == h.Id);
        Assert.Equal(2 * before, Charging.ExplosiveMass(back, h2), 9);
        Assert.Equal(Quantities.Compute(back).ExplosiveKg, back.Holes.Where(x => !x.IsDummy).Sum(x => Charging.ExplosiveMass(back, x)), 9);
    }

    [Fact]
    public void ProjectParametersAndOverrides_ApplyThroughSession()
    {
        var p = CalcParityTests.Generate(5);
        var h = p.Holes.First(x => !x.IsDummy && x.Decks.Count > 0);
        p.Calc = new ProjectCalc
        {
            ProfileName = CalcService.BuiltInName,
            Parameters = new() { ["site.vib.K"] = 2000 },
            Overrides = new() { new ValueOverride { Entity = h.Id.ToString(), Property = "hole.chargeMass", Value = 123 } }
        };
        Assert.Equal(123, Charging.ExplosiveMass(p, h));
        var s = CalcService.Session(p);
        Assert.Equal(2000, s.ParameterValue("site.vib.K"));
        Assert.NotEqual(2000, p.Vibration.SiteK);
    }

    [Fact]
    public void Perf_LargeBlast()
    {
        var p = BlastProject.CreateNew();
        var anfo = p.Products.First(x => x.Name == "ANFO");
        var stem = p.Products.First(x => x.Kind == ProductKind.Stemming);
        for (int i = 0; i < 2000; i++)
        {
            var h = p.Add(new Hole { Collar = new Vec3(i % 50 * 4, i / 50 * 4, 0), Length = 12 });
            h.Decks.Add(new Deck { Top = 0, Bottom = 3, ProductId = stem.Id });
            h.Decks.Add(new Deck { Top = 3, Bottom = 12, ProductId = anfo.Id });
        }
        // прогрев: компиляция профиля и JIT
        Charging.ExplosiveMass(p, p.Holes.First());
        Legacy.ExplosiveMass(p, p.Holes.First());
        Legacy.Quantities(p, p.Holes.Take(10));
        Quantities.Compute(p, p.Holes.Take(10));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double sum = 0;
        foreach (var h in p.Holes) sum += Charging.ExplosiveMass(p, h);
        var perCall = sw.Elapsed.TotalMilliseconds * 1000 / 2000;
        sw.Restart();
        var q = Quantities.Compute(p);
        var compute = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        foreach (var h in p.Holes) Legacy.ExplosiveMass(p, h);
        var legacyCall = sw.Elapsed.TotalMilliseconds * 1000 / 2000;
        sw.Restart();
        Legacy.Quantities(p);
        Console.WriteLine($"legacy ExplosiveMass {legacyCall:0.0} µs/call, Quantities {sw.Elapsed.TotalMilliseconds:0} ms");
        Assert.Equal(sum, q.ExplosiveKg, 6);
        Assert.True(perCall < 200, $"ExplosiveMass {perCall:0.0} µs");
        Console.WriteLine($"ExplosiveMass {perCall:0.0} µs/call, Quantities.Compute {compute:0} ms for 2000 holes");
    }
}
