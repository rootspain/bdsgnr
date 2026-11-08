using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

public sealed record ProductQuantity(Product Product, double Amount, string Unit, double Cost);

public sealed record HoleQuantity(Hole Hole, double Area, double Volume, double Tonnes, double ExplosiveKg, double StemmingM);

/// <summary>Blast totals: volumes, explosive by product, powder factor and costs.</summary>
public sealed class QuantityReport
{
    public int HoleCount { get; init; }
    public int DummyCount { get; init; }
    public double DrilledMetres { get; init; }
    public double Volume { get; init; }
    public double Tonnes { get; init; }
    public double ExplosiveKg { get; init; }
    public double PowderFactorKgM3 => Volume > 0 ? ExplosiveKg / Volume : 0;
    public double PowderFactorKgT => Tonnes > 0 ? ExplosiveKg / Tonnes : 0;
    public double EnergyFactorMJt { get; init; }
    public double ProductCost { get; init; }
    public double DrillingCost { get; init; }
    public double LoadingCost { get; init; }
    public double FixedCost { get; init; }
    public double TotalCost => ProductCost + DrillingCost + LoadingCost + FixedCost;
    public double CostPerTonne => Tonnes > 0 ? TotalCost / Tonnes : 0;
    public List<ProductQuantity> Products { get; init; } = new();
    public List<HoleQuantity> PerHole { get; init; } = new();
    public double AverageBurden { get; init; }
    public double AverageSpacing { get; init; }
    public double MaxInstantaneousCharge { get; init; }
}

public static class Quantities
{
    /// <summary>
    /// Computes quantities for the given holes (all holes when null). Hole areas come from Voronoi cells clipped to the boundary.
    /// The values are the properties of the project's calculation profile (<c>hole.volume</c>, <c>blast.powderFactor</c>…),
    /// worked out over the subset: <c>blast.holes</c> sees only these holes.
    /// </summary>
    public static QuantityReport Compute(BlastProject project, IEnumerable<Hole>? subset = null, IReadOnlyList<Vec3>? boundary = null)
    {
        var holes = (subset ?? project.Holes).ToList();
        var real = holes.Where(h => !h.IsDummy).ToList();
        var s = CalcService.Session(project);
        s.Scope.Subset = holes;
        // Cells always come from every hole of the blast, so a subset keeps the areas it has inside the whole pattern
        // instead of spreading over the empty boundary.
        CalcService.ConfigureCells(s, holes, boundary);
        double V(string id, object o) => Charging.Calculated(s, id, o, 0);

        var perHole = real.Select(h => new HoleQuantity(h, V("hole.area", h), V("hole.volume", h), V("hole.tonnes", h),
            V("hole.chargeMass", h), V("hole.stemmingLength", h))).ToList();

        var amounts = new Dictionary<string, double>();
        void Add(string? id, double amount) { if (id != null) amounts[id] = amounts.GetValueOrDefault(id) + amount; }
        foreach (var h in real)
            foreach (var d in h.Decks)
            {
                var p = s.Scope.FindProduct(d.ProductId);
                if (p == null) continue;
                s.Scope.NoteParent(d, h);
                Add(p.Id, p.Kind == ProductKind.GasBag ? 1 : V("deck.mass", d));
                foreach (var pr in d.Primers) { Add(pr.BoosterId, 1); Add(pr.DetonatorId, 1); }
            }
        foreach (var t in project.Ties) Add(t.ProductId, 1);

        var products = new List<ProductQuantity>();
        foreach (var (id, amount) in amounts)
        {
            var p = s.Scope.FindProduct(id);
            if (p == null) continue;
            var byMass = p.Kind is ProductKind.BulkExplosive or ProductKind.PackagedExplosive or ProductKind.Stemming;
            products.Add(new ProductQuantity(p, amount, byMass ? "kg" : "ea", amount * p.Cost));
        }

        return new QuantityReport
        {
            HoleCount = real.Count,
            DummyCount = holes.Count - real.Count,
            DrilledMetres = V("blast.drilledMetres", project),
            Volume = V("blast.volume", project),
            Tonnes = V("blast.tonnes", project),
            ExplosiveKg = V("blast.explosiveKg", project),
            EnergyFactorMJt = V("blast.energyFactor", project),
            ProductCost = V("blast.productCost", project),
            DrillingCost = V("blast.drillingCost", project),
            LoadingCost = V("blast.loadingCost", project),
            FixedCost = V("blast.fixedCost", project),
            Products = products.OrderBy(p => p.Product.Kind).ThenBy(p => p.Product.Name).ToList(),
            PerHole = perHole,
            AverageBurden = V("blast.avgBurden", project),
            AverageSpacing = V("blast.avgSpacing", project),
            MaxInstantaneousCharge = V("blast.mic", project)
        };
    }

    /// <summary>Boundary polygon if the project has one, otherwise the hole hull expanded by half a burden.</summary>
    public static IReadOnlyList<Vec3> DefaultBoundary(BlastProject project, IReadOnlyList<Hole> holes)
    {
        var poly = project.Entities.OfType<PolylineEntity>().FirstOrDefault(p => p.Closed && p.Kind == LineKind.Boundary);
        if (poly != null) return poly.Points;
        var hull = GeoMath.ConvexHull(holes.Select(h => h.Collar));
        if (hull.Count < 3)
        {
            var c = holes.Count > 0 ? holes[0].Collar : Vec3.Zero;
            var r = Math.Max(project.Design.Burden, project.Design.Spacing) / 2;
            return new[] { c + new Vec3(-r, -r, 0), c + new Vec3(r, -r, 0), c + new Vec3(r, r, 0), c + new Vec3(-r, r, 0) };
        }
        return GeoMath.OffsetPolygon(hull, Math.Min(project.Design.Burden, project.Design.Spacing) / 2);
    }

    /// <summary>Area of each hole's Voronoi cell inside the boundary (half-plane clipping against neighbours).</summary>
    public static Dictionary<Guid, double> VoronoiAreas(IReadOnlyList<Hole> holes, IReadOnlyList<Vec3> boundary)
    {
        var result = new Dictionary<Guid, double>();
        var poly = boundary.ToList();
        if (GeoMath.SignedArea(poly) < 0) poly.Reverse();
        foreach (var h in holes)
        {
            var cell = poly.ToList();
            var p = h.Collar;
            var neighbours = holes.Where(o => o != h).OrderBy(o => Vec3.Distance2D(o.Collar, p)).Take(24);
            foreach (var o in neighbours)
            {
                var q = o.Collar;
                var mid = new Vec3((p.X + q.X) / 2, (p.Y + q.Y) / 2, 0);
                var n = new Vec3(q.X - p.X, q.Y - p.Y, 0);
                cell = ClipHalfPlane(cell, mid, n);
                if (cell.Count < 3) break;
            }
            result[h.Id] = cell.Count < 3 ? 0 : GeoMath.Area(cell);
        }
        return result;
    }

    /// <summary>Keeps the part of the polygon on the side of the line through <paramref name="m"/> opposite to normal <paramref name="n"/>.</summary>
    private static List<Vec3> ClipHalfPlane(List<Vec3> poly, Vec3 m, Vec3 n)
    {
        var output = new List<Vec3>();
        double Side(Vec3 v) => (v.X - m.X) * n.X + (v.Y - m.Y) * n.Y;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            var sa = Side(a);
            var sb = Side(b);
            if (sa <= 0) output.Add(a);
            if ((sa < 0 && sb > 0) || (sa > 0 && sb < 0))
                output.Add(Vec3.Lerp(a, b, sa / (sa - sb)));
        }
        return output;
    }

    /// <summary>Average burden and spacing from rows (spacing along rows, burden between rows).</summary>
    public static (double Burden, double Spacing) PatternEstimate(BlastProject project)
    {
        var spacings = new List<double>();
        var rowCentres = new List<List<Hole>>();
        foreach (var row in project.Rows)
        {
            var rh = row.HoleIds.Select(project.FindEntity).OfType<Hole>().Where(h => !h.IsDummy).ToList();
            for (int i = 1; i < rh.Count; i++) spacings.Add(Vec3.Distance2D(rh[i - 1].Collar, rh[i].Collar));
            if (rh.Count > 0) rowCentres.Add(rh);
        }
        var burdens = new List<double>();
        for (int i = 1; i < rowCentres.Count; i++)
        {
            var a = rowCentres[i - 1];
            var b = rowCentres[i];
            if (a.Count < 2) continue;
            foreach (var h in b)
                burdens.Add(GeoMath.DistanceToPolyline2D(a.Select(x => x.Collar).ToList(), h.Collar, false));
        }
        return (burdens.Count > 0 ? burdens.Average() : project.Design.Burden, spacings.Count > 0 ? spacings.Average() : project.Design.Spacing);
    }
}

/// <summary>Scaled-distance vibration and airblast prediction.</summary>
public static class Vibration
{
    public static double Ppv(VibrationModel m, double distance, double chargeKg) =>
        chargeKg <= 0 || distance <= 0 ? 0 : m.SiteK * Math.Pow(distance / Math.Sqrt(chargeKg), -m.SiteB);

    /// <summary>Peak overpressure in kPa.</summary>
    public static double AirblastKPa(VibrationModel m, double distance, double chargeKg) =>
        chargeKg <= 0 || distance <= 0 ? 0 : m.AirK * Math.Pow(distance / Math.Cbrt(chargeKg), -m.AirB);

    public static double KPaToDb(double kpa) => kpa <= 0 ? 0 : 20 * Math.Log10(kpa * 1000 / 20e-6);

    /// <summary>Maximum charge allowed per delay to keep PPV under the limit at a distance.</summary>
    public static double AllowableCharge(VibrationModel m, double distance, double ppvLimit) =>
        Math.Pow(distance / Math.Pow(m.SiteK / ppvLimit, 1 / m.SiteB), 2);

    public sealed record Prediction(double Ppv, double AirblastDb, double ChargeKg, double DistanceM, double TimeMs);

    /// <summary>
    /// Predicts the worst PPV and airblast at a receiver by grouping holes that fire within the model window.
    /// When no timing exists every hole is treated as a single delay.
    /// </summary>
    public static Prediction Predict(BlastProject project, Vec3 receiver) => new Charges(project).At(receiver);

    /// <summary>
    /// The charges of a blast and the holes firing within the model window of each one: all that does not depend on the
    /// receiver, worked out once for a whole map.
    /// </summary>
    private sealed class Charges
    {
        private readonly VibrationModel _m;
        private readonly List<(Vec3 Collar, double Q)> _holes;
        private readonly (Vec3 Collar, double Time, double Q, int End)[] _windows;
        private readonly double[] _distance;
        private readonly int[] _nearest;

        public Charges(BlastProject project)
        {
            _m = project.Vibration;
            var calc = CalcService.Session(project);
            var holes = project.Holes.Where(h => !h.IsDummy).Select(h => (H: h, Q: Charging.Calculated(calc, "hole.chargeMass", h, 0))).Where(x => x.Q > 0).ToList();
            _holes = holes.Select(x => (x.H.Collar, x.Q)).ToList();
            var timed = holes.Where(x => x.H.FiringTimeMs.HasValue).OrderBy(x => x.H.FiringTimeMs).ToList();
            _windows = new (Vec3, double, double, int)[timed.Count];
            _distance = new double[timed.Count];
            _nearest = new int[timed.Count];
            for (int i = 0; i < timed.Count; i++)
            {
                var t0 = timed[i].H.FiringTimeMs!.Value;
                double q = 0;
                int j = i;
                for (; j < timed.Count && timed[j].H.FiringTimeMs!.Value < t0 + _m.WindowMs; j++) q += timed[j].Q;
                _windows[i] = (timed[i].H.Collar, t0, q, j);
            }
        }

        public Prediction At(Vec3 receiver)
        {
            var best = new Prediction(0, 0, 0, 0, 0);
            if (_windows.Length == 0)
            {
                foreach (var (collar, q) in _holes)
                {
                    var d = Vec3.Distance(collar, receiver);
                    var ppv = Ppv(_m, d, q);
                    if (ppv > best.Ppv) best = new Prediction(ppv, KPaToDb(AirblastKPa(_m, d, q)), q, d, 0);
                }
                return best;
            }
            for (int i = 0; i < _windows.Length; i++) _distance[i] = Vec3.Distance(_windows[i].Collar, receiver);
            // the nearest hole of each window, as the window slides: _nearest[head..tail) holds its candidates, nearest
            // first, so all the holes firing at once (a window of the whole blast) still cost one pass
            int head = 0, tail = 0, next = 0;
            for (int i = 0; i < _windows.Length; i++)
            {
                var (_, time, q, end) = _windows[i];
                for (; next < end; next++)
                {
                    while (tail > head && _distance[_nearest[tail - 1]] >= _distance[next]) tail--;
                    _nearest[tail++] = next;
                }
                while (head < tail && _nearest[head] < i) head++;
                var d = head < tail ? _distance[_nearest[head]] : double.MaxValue;
                var ppv = Ppv(_m, d, q);
                if (ppv > best.Ppv) best = new Prediction(ppv, KPaToDb(AirblastKPa(_m, d, q)), q, d, time);
            }
            return best;
        }
    }

    /// <summary>PPV (or airblast dB) grid over an area for map display.</summary>
    public static double[,] Map(BlastProject project, Bounds3 area, int nx, int ny, bool airblast)
    {
        var grid = new double[nx, ny];
        var charges = new Charges(project);
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                var p = new Vec3(area.Min.X + (area.Max.X - area.Min.X) * (i + 0.5) / nx, area.Min.Y + (area.Max.Y - area.Min.Y) * (j + 0.5) / ny, area.Center.Z);
                var pr = charges.At(p);
                grid[i, j] = airblast ? pr.AirblastDb : pr.Ppv;
            }
        return grid;
    }
}
