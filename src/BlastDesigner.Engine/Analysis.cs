using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>Design checks and overview: burden check, face burden, charge-time and volume overviews, dashboard.</summary>
public static class Analysis
{
    // ---- burden check -------------------------------------------------------------------

    public sealed record BurdenCheck(Hole Hole, double? Burden, double? Spacing, Hole? AheadHole, bool Low, bool High);

    /// <summary>
    /// True burden of each hole: the smallest distance, measured along the blast orientation, to a hole ahead of it
    /// (towards the free face) whose lateral offset is within 0.75 × spacing. Spacing is the nearest lateral neighbour.
    /// </summary>
    public static List<BurdenCheck> CheckBurden(BlastProject project, double tolerance = 0.2)
    {
        var holes = project.Holes.Where(h => !h.IsDummy).ToList();
        var dir = GeoMath.HoleDirection(project.Header.BlastOrientation, 90);
        var fwd = new Vec3(dir.X, dir.Y, 0).Normalized();
        var side = new Vec3(-fwd.Y, fwd.X, 0);
        var designB = project.Design.Burden;
        var designS = project.Design.Spacing;
        var result = new List<BurdenCheck>();
        foreach (var h in holes)
        {
            double? burden = null, spacing = null;
            Hole? ahead = null;
            foreach (var o in holes)
            {
                if (o == h) continue;
                var d = o.Collar - h.Collar;
                var along = Vec3.Dot(d, fwd);
                var lateral = Math.Abs(Vec3.Dot(d, side));
                if (along > 0.3 * designB && lateral <= 0.75 * designS && (burden == null || along < burden)) { burden = along; ahead = o; }
                if (Math.Abs(along) < 0.3 * designB && lateral > 0.1 && (spacing == null || lateral < spacing)) spacing = lateral;
            }
            result.Add(new BurdenCheck(h, burden, spacing, ahead,
                burden.HasValue && burden < designB * (1 - tolerance),
                burden.HasValue && burden > designB * (1 + tolerance)));
        }
        return result;
    }

    // ---- front row burden along the hole --------------------------------------------------

    public sealed record FaceBurden(Hole Hole, double MinBurden, double DepthOfMin, double CollarBurden, double ToeBurden, List<(double Depth, double Burden)> Profile);

    /// <summary>
    /// Horizontal distance from points down the hole to the face, where the face is interpolated between the crest and toe lines
    /// at each elevation. Without a toe line the face is treated as vertical below the crest.
    /// </summary>
    public static FaceBurden FrontRowBurden(Hole h, PolylineEntity crest, PolylineEntity? toe, double step = 0.5)
    {
        var profile = new List<(double, double)>();
        var len = h.EffectiveLength;
        for (double d = 0; d <= len + 1e-9; d += step)
        {
            var p = h.PointAtDepth(Math.Min(d, len));
            var c = Closest(crest.Points, p, crest.Closed);
            Vec3 facePoint = c;
            if (toe != null)
            {
                var t = Closest(toe.Points, p, toe.Closed);
                var dz = c.Z - t.Z;
                var f = Math.Abs(dz) < 1e-6 ? 0 : Math.Clamp((c.Z - p.Z) / dz, 0, 1);
                facePoint = Vec3.Lerp(c, t, f);
            }
            profile.Add((Math.Min(d, len), Vec3.Distance2D(p, facePoint)));
        }
        var min = profile.OrderBy(x => x.Item2).First();
        return new FaceBurden(h, min.Item2, min.Item1, profile[0].Item2, profile[^1].Item2, profile);
    }

    private static Vec3 Closest(IReadOnlyList<Vec3> line, Vec3 p, bool closed)
    {
        Vec3 best = line[0];
        double bestD = double.MaxValue;
        int n = closed ? line.Count : line.Count - 1;
        for (int i = 0; i < n; i++)
        {
            var (q, _) = GeoMath.ClosestOnSegment2D(line[i], line[(i + 1) % line.Count], p);
            var d = Vec3.Distance2D(q, p);
            if (d < bestD) { bestD = d; best = q; }
        }
        return best;
    }

    // ---- charge vs time --------------------------------------------------------------------

    public sealed record ChargeTimePoint(double TimeMs, double ChargeKg, double CumulativeKg, int Holes);

    /// <summary>Explosive firing in each time bin and the cumulative total. Uses the solved firing times.</summary>
    public static List<ChargeTimePoint> ChargeVsTime(BlastProject project, double binMs)
    {
        Timing.Solve(project);
        var calc = CalcService.Live(project) ?? CalcService.Session(project);
        var timed = project.Holes.Where(h => h.FiringTimeMs.HasValue && !h.IsDummy).Select(h => (T: h.FiringTimeMs!.Value, Kg: Charging.Calculated(calc, "hole.chargeMass", h, 0))).ToList();
        if (timed.Count == 0 || binMs <= 0) return new();
        var t0 = Math.Floor(timed.Min(x => x.T) / binMs) * binMs;
        var bins = timed.GroupBy(x => (int)Math.Floor((x.T - t0) / binMs)).OrderBy(g => g.Key).ToList();
        var result = new List<ChargeTimePoint>();
        double cum = 0;
        foreach (var g in bins)
        {
            var kg = g.Sum(x => x.Kg);
            cum += kg;
            result.Add(new ChargeTimePoint(t0 + g.Key * binMs, kg, cum, g.Count()));
        }
        return result;
    }

    /// <summary>Pairs of neighbouring holes (within <paramref name="radius"/>) that fire within <paramref name="windowMs"/> of each other.</summary>
    public static List<(Hole A, Hole B, double DeltaMs, double Distance)> TimingOverlap(BlastProject project, double radius, double windowMs)
    {
        Timing.Solve(project);
        var timed = project.Holes.Where(h => h.FiringTimeMs.HasValue && !h.IsDummy).ToList();
        var list = new List<(Hole, Hole, double, double)>();
        for (int i = 0; i < timed.Count; i++)
            for (int j = i + 1; j < timed.Count; j++)
            {
                var d = Vec3.Distance2D(timed[i].Collar, timed[j].Collar);
                if (d > radius) continue;
                var dt = Math.Abs(timed[i].FiringTimeMs!.Value - timed[j].FiringTimeMs!.Value);
                if (dt < windowMs) list.Add((timed[i], timed[j], dt, d));
            }
        return list.OrderBy(x => x.Item3).ToList();
    }

    // ---- volume overview ------------------------------------------------------------------

    public sealed record VolumeGroup(string Group, int Holes, double Volume, double Tonnes, double ExplosiveKg, double DrilledM)
    {
        public double PowderFactor => Volume > 0 ? ExplosiveKg / Volume : 0;
    }

    public static List<VolumeGroup> VolumeOverview(BlastProject project, string groupBy)
    {
        var q = Quantities.Compute(project);
        var rowOf = project.Rows.SelectMany(r => r.HoleIds.Select(id => (id, r.Name))).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().Name);
        string Key(Hole h) => groupBy switch
        {
            "Row" => rowOf.TryGetValue(h.Id, out var r) ? r : "(no row)",
            "Hole type" => project.Design.HoleTypes.FirstOrDefault(t => t.Index == h.HoleType)?.Name ?? h.HoleType.ToString(),
            "Material type" => project.Design.MaterialTypes.FirstOrDefault(t => t.Index == h.MaterialType)?.Name ?? h.MaterialType.ToString(),
            _ => "All"
        };
        return q.PerHole.GroupBy(x => Key(x.Hole)).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new VolumeGroup(g.Key, g.Count(), g.Sum(x => x.Volume), g.Sum(x => x.Tonnes), g.Sum(x => x.ExplosiveKg), g.Sum(x => x.Hole.EffectiveLength)))
            .ToList();
    }

    // ---- dashboard ------------------------------------------------------------------------

    public sealed record Kpi(string Group, string Name, string Value, bool Warning = false);

    public static List<Kpi> Dashboard(BlastProject project)
    {
        var list = new List<Kpi>();
        var q = Quantities.Compute(project);
        Timing.Solve(project);
        var holes = project.Holes.Where(h => !h.IsDummy).ToList();
        list.Add(new("Design", "Holes", $"{q.HoleCount} (+{q.DummyCount} dummy)"));
        list.Add(new("Design", "Drilled metres", $"{q.DrilledMetres:0.0} m"));
        list.Add(new("Design", "Burden × spacing", $"{q.AverageBurden:0.00} × {q.AverageSpacing:0.00} m"));
        list.Add(new("Design", "Volume", $"{q.Volume:0} m³"));
        list.Add(new("Design", "Tonnes", $"{q.Tonnes:0} t"));
        list.Add(new("Loading", "Explosive", $"{q.ExplosiveKg:0.0} kg"));
        list.Add(new("Loading", "Powder factor", $"{q.PowderFactorKgM3:0.000} kg/m³"));
        list.Add(new("Loading", "Energy factor", $"{q.EnergyFactorMJt:0.00} MJ/t"));
        var unloaded = holes.Count(h => h.Decks.Count == 0);
        list.Add(new("Loading", "Holes not loaded", unloaded.ToString(), unloaded > 0));
        var timed = holes.Where(h => h.FiringTimeMs.HasValue).ToList();
        list.Add(new("Timing", "Holes initiated", $"{timed.Count} / {holes.Count}", timed.Count < holes.Count));
        if (timed.Count > 0) list.Add(new("Timing", "Firing span", $"{timed.Min(h => h.FiringTimeMs!.Value):0} – {timed.Max(h => h.FiringTimeMs!.Value):0} ms"));
        list.Add(new("Timing", "Maximum instantaneous charge", $"{q.MaxInstantaneousCharge:0.0} kg / {project.Vibration.WindowMs:0} ms"));
        var overlaps = TimingOverlap(project, project.Design.Spacing * 1.5, project.Vibration.WindowMs).Count;
        list.Add(new("Timing", "Neighbour overlaps", overlaps.ToString(), overlaps > 0));
        var monitors = project.Entities.OfType<PointEntity>().Where(p => p.Kind == PointKind.Monitoring).ToList();
        if (monitors.Count > 0)
        {
            var worst = monitors.Select(m => (m, r: Vibration.Predict(project, m.Position))).OrderByDescending(x => x.r.Ppv).First();
            list.Add(new("Environment", "Highest PPV", $"{worst.r.Ppv:0.00} mm/s at {worst.m.Label}", worst.r.Ppv > project.Vibration.PpvLimit));
            list.Add(new("Environment", "Highest airblast", $"{monitors.Max(m => Vibration.Predict(project, m.Position).AirblastDb):0.0} dB"));
        }
        var fly = Flyrock.Compute(project);
        list.Add(new("Environment", "Flyrock max throw", $"{fly.MaxThrow:0} m"));
        list.Add(new("Environment", "Clearance distance", $"{fly.Clearance:0} m"));
        var frag = Fragmentation.Compute(project);
        if (frag.X50Cm > 0)
        {
            var unit = SizeUnits.Of(project.Fragmentation);
            string Size(double cm) => SizeUnits.FromCm(cm, unit).ToString(SizeUnits.Format(unit)) + " " + SizeUnits.Symbol(unit);
            list.Add(new("Fragmentation", "X50", Size(frag.X50Cm)));
            list.Add(new("Fragmentation", "X80", Size(frag.X80Cm)));
            list.Add(new("Fragmentation", "Oversize", $"{frag.OversizePct:0.0} %", frag.OversizePct > 10));
        }
        var tol = Tolerance.Check(project);
        var surveyed = tol.Where(t => t.Hole.ActualCollar.HasValue || t.Hole.ActualLength.HasValue || t.Hole.BoreTrack != null).ToList();
        if (surveyed.Count > 0)
            list.Add(new("Quality", "Holes within tolerance", $"{surveyed.Count(t => t.Pass)} / {surveyed.Count}", surveyed.Any(t => !t.Pass)));
        var burden = CheckBurden(project);
        list.Add(new("Quality", "Low-burden holes", burden.Count(b => b.Low).ToString(), burden.Any(b => b.Low)));
        return list;
    }
}
