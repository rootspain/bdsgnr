using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>Flyrock throw per hole from the published Richards &amp; Moore relations (face burst, cratering, rifling).</summary>
public static class Flyrock
{
    public const double G = 9.81;

    public sealed record HoleResult(Hole Hole, double ChargePerMetre, double Burden, double Stemming, double FaceBurst, double Cratering, double Rifling)
    {
        public double Max => Math.Max(FaceBurst, Math.Max(Cratering, Rifling));
    }

    public sealed record Report(List<HoleResult> Holes, double MaxThrow, double Clearance, Hole? Critical);

    /// <summary>Face burst: throw from the front of the burden.</summary>
    public static double FaceBurst(double k, double chargePerMetre, double burden) =>
        burden <= 0 ? 0 : k * k / G * Math.Pow(Math.Sqrt(chargePerMetre) / burden, 2.6);

    /// <summary>Cratering: throw from the collar when the stemming is short.</summary>
    public static double Cratering(double k, double chargePerMetre, double stemming) =>
        stemming <= 0 ? double.PositiveInfinity : k * k / G * Math.Pow(Math.Sqrt(chargePerMetre) / stemming, 2.6);

    /// <summary>Rifling: stemming ejected along the hole axis; zero for vertical holes.</summary>
    public static double Rifling(double k, double chargePerMetre, double stemming, double angleFromVerticalDeg)
    {
        var launch = (90 - angleFromVerticalDeg) * GeoMath.Deg;
        return Cratering(k, chargePerMetre, stemming) * Math.Abs(Math.Sin(2 * launch));
    }

    /// <summary>Burden used for face burst: distance to the crest line when one exists, otherwise the design burden.</summary>
    public static Report Compute(BlastProject project, IEnumerable<Hole>? holes = null)
    {
        var m = project.Flyrock;
        var crest = project.Entities.OfType<PolylineEntity>().FirstOrDefault(l => l.Kind == LineKind.Crest);
        var list = new List<HoleResult>();
        foreach (var h in (holes ?? project.Holes).Where(h => !h.IsDummy && h.Decks.Count > 0))
        {
            var explosive = h.Decks.Where(d => project.FindProduct(d.ProductId)?.IsExplosive == true).ToList();
            if (explosive.Count == 0) continue;
            var top = explosive.Min(d => d.Top);
            var kgPerM = explosive.Max(d => d.Length > 0 ? Charging.DeckMass(project, h, d) / d.Length : 0);
            var burden = crest != null ? GeoMath.DistanceToPolyline2D(crest.Points, h.Collar, crest.Closed) : project.Design.Burden;
            list.Add(new HoleResult(h, kgPerM, burden, top,
                crest != null ? FaceBurst(m.SiteK, kgPerM, burden) : 0,
                Cratering(m.SiteK, kgPerM, top),
                Rifling(m.SiteK, kgPerM, top, h.Angle)));
        }
        var critical = list.OrderByDescending(r => r.Max).FirstOrDefault();
        var max = critical?.Max ?? 0;
        return new Report(list, max, Math.Max(m.MinimumClearance, max * m.SafetyFactor), critical?.Hole);
    }

    /// <summary>Clearance zone polygon: hull of a circle of the per-hole clearance around every hole.</summary>
    public static List<Vec3> ClearancePolygon(BlastProject project, Report r, int segments = 48)
    {
        var pts = new List<Vec3>();
        var m = project.Flyrock;
        foreach (var hr in r.Holes)
        {
            var radius = Math.Max(m.MinimumClearance, hr.Max * m.SafetyFactor);
            for (int i = 0; i < segments; i++)
            {
                var a = 2 * Math.PI * i / segments;
                pts.Add(hr.Hole.Collar + new Vec3(Math.Cos(a) * radius, Math.Sin(a) * radius, 0));
            }
        }
        return GeoMath.ConvexHull(pts);
    }
}

/// <summary>Fragmentation prediction: Kuz-Ram mean size and uniformity, Rosin-Rammler and Swebrec curves.</summary>
public static class Fragmentation
{
    public sealed record Result(double RockFactor, double PowderFactor, double ChargePerHole, double Rws, double X50Cm, double Uniformity,
        double X20Cm, double X80Cm, double OversizePct, double FinesPct, List<(double SizeCm, double PassingPct)> Curve, List<(double SizeCm, double PassingPct)> Swebrec);

    /// <summary>Allowed range of the rock factor A (Cunningham): values outside it are cut to it.</summary>
    public const double MinRockFactor = 0, MaxRockFactor = 25;
    /// <summary>Allowed range of the Protodyakonov strength f (σc = 10·f MPa).</summary>
    public const double MinProtodyakonov = 0, MaxProtodyakonov = 50;

    /// <summary>
    /// Rock mass description RMD: 10 powdery, 50 massive, the joint factor JF for a jointed mass (stored as RMD 0 and
    /// the joint factor; an old file with both keeps their sum).
    /// </summary>
    public static double Rmd(FragmentationModel m) => m.RockMassDescription + m.JointFactor;

    /// <summary>Rock density influence RDI = 25·ρ − 50, ρ in t/m³.</summary>
    public static double Rdi(double densityTm3) => 25 * densityTm3 - 50;

    /// <summary>Hardness factor HF: σc/5 (= 2f), or E/3 for a Young's modulus below 50 GPa.</summary>
    public static double Hf(FragmentationModel m) => m.YoungsModulus < 50 ? m.YoungsModulus / 3 : m.Ucs / 5;

    /// <summary>
    /// Cunningham rock factor A = 0.06·(RMD + RDI + HF), or the one typed in, within
    /// <see cref="MinRockFactor"/>…<see cref="MaxRockFactor"/>.
    /// </summary>
    public static double RockFactor(FragmentationModel m, double densityTm3)
    {
        if (m.RockFactorSet || m.RockFactor > 0) return Math.Clamp(m.RockFactor, MinRockFactor, MaxRockFactor);
        return Math.Clamp(0.06 * (Rmd(m) + Rdi(densityTm3) + Hf(m)), MinRockFactor, MaxRockFactor);
    }

    /// <summary>Kuz-Ram mean fragment size X50 in cm.</summary>
    public static double X50(double a, double powderFactorKgM3, double chargeKg, double rws) =>
        a * Math.Pow(powderFactorKgM3, -0.8) * Math.Pow(chargeKg, 1.0 / 6) * Math.Pow(115 / rws, 19.0 / 30);

    /// <summary>Cunningham uniformity index n.</summary>
    public static double Uniformity(double burden, double spacing, double diameterMm, double drillDev, double chargeLength, double benchHeight)
    {
        if (burden <= 0 || diameterMm <= 0 || benchHeight <= 0) return 1;
        var n = (2.2 - 14 * burden / diameterMm) * Math.Sqrt((1 + spacing / burden) / 2) * (1 - drillDev / burden) * Math.Pow(1.1, 0.1) * (chargeLength / benchHeight);
        return Math.Clamp(n, 0.5, 3);
    }

    public static double RosinRammler(double x, double x50, double n) => 1 - Math.Exp(-0.693 * Math.Pow(x / x50, n));

    /// <summary>Swebrec passing fraction with the undulation b linked to the Kuz-Ram n.</summary>
    public static double SwebrecPassing(double x, double x50, double xmax, double n)
    {
        if (x >= xmax) return 1;
        if (x <= 0) return 0;
        var b = 2 * Math.Log(2) * Math.Log(xmax / x50) * n;
        return 1 / (1 + Math.Pow(Math.Log(xmax / x) / Math.Log(xmax / x50), b));
    }

    public static double SizeAt(double passing, double x50, double n) => x50 * Math.Pow(Math.Log(1 / (1 - passing)) / 0.693, 1 / n);

    public static Result Compute(BlastProject project, IEnumerable<Hole>? holes = null)
    {
        var list = (holes ?? project.Holes).Where(h => !h.IsDummy).ToList();
        var q = Quantities.Compute(project, list);
        var loaded = list.Where(h => h.Decks.Count > 0).ToList();
        var charge = loaded.Count > 0 ? loaded.Average(h => Charging.ExplosiveMass(project, h)) : 0;
        var explosiveDecks = loaded.SelectMany(h => h.Decks.Select(d => (h, d, p: project.FindProduct(d.ProductId)))).Where(x => x.p?.IsExplosive == true).ToList();
        var rws = explosiveDecks.Count > 0 ? explosiveDecks.Sum(x => x.p!.RelativeWeightStrength * x.d.Length) / Math.Max(1e-9, explosiveDecks.Sum(x => x.d.Length)) : 100;
        var chargeLength = loaded.Count > 0 ? loaded.Average(h => h.Decks.Where(d => project.FindProduct(d.ProductId)?.IsExplosive == true).Sum(d => d.Length)) : 0;
        var bench = list.Count > 0 ? list.Average(h => Math.Max(0.1, h.Length - h.Subdrill) * Math.Cos(h.Angle * GeoMath.Deg)) : project.Design.BenchHeight;
        var diameter = list.Count > 0 ? list.Average(h => h.Diameter) : project.Design.Diameter;
        var burden = q.AverageBurden > 0 ? q.AverageBurden : project.Design.Burden;
        var spacing = q.AverageSpacing > 0 ? q.AverageSpacing : project.Design.Spacing;
        var m = project.Fragmentation;
        var a = RockFactor(m, project.Design.RockDensity);
        var pf = q.PowderFactorKgM3;
        if (pf <= 0 || charge <= 0)
            return new Result(a, pf, charge, rws, 0, 0, 0, 0, 0, 0, new(), new());
        var x50 = X50(a, pf, charge, rws);
        var n = Uniformity(burden, spacing, diameter, m.DrillDeviation, chargeLength, bench);
        var curve = new List<(double, double)>();
        var swe = new List<(double, double)>();
        var xmax = Math.Max(m.MaxBlockCm, x50 * 1.5);
        for (double logx = -1; logx <= Math.Log10(xmax) + 1e-9; logx += 0.05)
        {
            var x = Math.Pow(10, logx);
            curve.Add((x, 100 * RosinRammler(x, x50, n)));
            swe.Add((x, 100 * SwebrecPassing(x, x50, xmax, n)));
        }
        return new Result(a, pf, charge, rws, x50, n, SizeAt(0.2, x50, n), SizeAt(0.8, x50, n),
            100 * (1 - RosinRammler(m.OversizeCm, x50, n)), 100 * RosinRammler(m.FinesCm, x50, n), curve, swe);
    }
}

/// <summary>Site-law regression of measured PPV against scaled distance (square-root scaling), with prediction confidence.</summary>
public static class SiteLaw
{
    public sealed record Fit(int Count, double K50, double B, double R2, double StdError, double MeanLogSd, double Sxx)
    {
        /// <summary>Site constant K at the given confidence (e.g. 0.95) for a new prediction near the mean scaled distance.</summary>
        public double KAt(double confidence)
        {
            if (Count < 3) return K50;
            var t = Stats.StudentTQuantile(confidence, Count - 2);
            return K50 * Math.Pow(10, t * StdError * Math.Sqrt(1 + 1.0 / Count));
        }

        /// <summary>Predicted PPV at a scaled distance with the full prediction interval.</summary>
        public double PpvAt(double scaledDistance, double confidence)
        {
            var x = Math.Log10(scaledDistance);
            var mean = Math.Log10(K50) - B * x;
            if (Count < 3 || confidence <= 0.5) return Math.Pow(10, mean);
            var t = Stats.StudentTQuantile(confidence, Count - 2);
            var se = StdError * Math.Sqrt(1 + 1.0 / Count + (Sxx > 0 ? (x - MeanLogSd) * (x - MeanLogSd) / Sxx : 0));
            return Math.Pow(10, mean + t * se);
        }
    }

    public static double ScaledDistance(double distance, double chargeKg) => distance / Math.Sqrt(Math.Max(1e-9, chargeKg));

    public static Fit? Regress(IEnumerable<VibrationRecord> records)
    {
        var pts = records.Where(r => r.Use && r.DistanceM > 0 && r.ChargeKg > 0 && r.PpvMm > 0)
            .Select(r => (X: Math.Log10(ScaledDistance(r.DistanceM, r.ChargeKg)), Y: Math.Log10(r.PpvMm))).ToList();
        if (pts.Count < 2) return null;
        var mx = pts.Average(p => p.X);
        var my = pts.Average(p => p.Y);
        var sxx = pts.Sum(p => (p.X - mx) * (p.X - mx));
        if (sxx < 1e-12) return null;
        var sxy = pts.Sum(p => (p.X - mx) * (p.Y - my));
        var slope = sxy / sxx;
        var intercept = my - slope * mx;
        var ssr = pts.Sum(p => Math.Pow(p.Y - (intercept + slope * p.X), 2));
        var sst = pts.Sum(p => (p.Y - my) * (p.Y - my));
        var se = pts.Count > 2 ? Math.Sqrt(ssr / (pts.Count - 2)) : 0;
        return new Fit(pts.Count, Math.Pow(10, intercept), -slope, sst > 0 ? 1 - ssr / sst : 1, se, mx, sxx);
    }

    /// <summary>Distance at which PPV falls to the limit for a charge: D = √Q · (K / PPV)^(1/B).</summary>
    public static double DistanceForPpv(double k, double b, double chargeKg, double ppvLimit) =>
        Math.Sqrt(Math.Max(0, chargeKg)) * Math.Pow(k / ppvLimit, 1 / b);

    /// <summary>Distance at which airblast falls to the limit (cube-root scaling).</summary>
    public static double DistanceForAirblast(VibrationModel m, double chargeKg, double limitDb)
    {
        var kpa = 20e-6 * Math.Pow(10, limitDb / 20) / 1000;
        return Math.Cbrt(Math.Max(0, chargeKg)) * Math.Pow(m.AirK / kpa, 1 / m.AirB);
    }
}

/// <summary>Exclusion zone: the larger of the flyrock clearance and the vibration and airblast distances.</summary>
public static class ExclusionZone
{
    public sealed record Result(double Mic, double VibrationRadius, double AirblastRadius, double FlyrockRadius)
    {
        public double Radius => Math.Max(VibrationRadius, Math.Max(AirblastRadius, FlyrockRadius));
    }

    public static Result Compute(BlastProject project)
    {
        Timing.Solve(project);
        var mic = Timing.MaxInstantaneousCharge(project, project.Vibration.WindowMs);
        if (mic <= 0) mic = project.Holes.Select(h => Charging.ExplosiveMass(project, h)).DefaultIfEmpty(0).Max();
        var v = project.Vibration;
        return new Result(mic,
            SiteLaw.DistanceForPpv(v.SiteK, v.SiteB, mic, v.PpvLimit),
            SiteLaw.DistanceForAirblast(v, mic, v.AirblastLimitDb),
            Flyrock.Compute(project).Clearance);
    }
}

/// <summary>Small statistics helpers (normal and Student-t quantiles).</summary>
public static class Stats
{
    /// <summary>Inverse standard normal CDF (Acklam's rational approximation, |error| &lt; 1.2e-9).</summary>
    public static double NormalQuantile(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double lo = 0.02425;
        double q, r;
        if (p < lo)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > 1 - lo)
        {
            q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        q = p - 0.5;
        r = q * q;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }

    /// <summary>One-sided Student-t quantile (Cornish-Fisher expansion; accurate to ~1e-3 for df ≥ 3).</summary>
    public static double StudentTQuantile(double p, int df)
    {
        var z = NormalQuantile(p);
        if (df <= 0) return z;
        double n = df;
        var z3 = z * z * z; var z5 = z3 * z * z; var z7 = z5 * z * z; var z9 = z7 * z * z;
        return z + (z3 + z) / (4 * n) + (5 * z5 + 16 * z3 + 3 * z) / (96 * n * n)
                 + (3 * z7 + 19 * z5 + 17 * z3 - 15 * z) / (384 * n * n * n)
                 + (79 * z9 + 776 * z7 + 1482 * z5 - 1920 * z3 - 945 * z) / (92160 * n * n * n * n);
    }
}
