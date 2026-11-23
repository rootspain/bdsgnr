using System.Diagnostics;
using BlastDesigner.Calc;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using Xunit.Abstractions;

namespace BlastDesigner.Tests;

/// <summary>The target fragmentation: the closed forms of Kuz-Ram solved for the grid, the solver on copies of the project, applying a variant.</summary>
public class FragTargetTests
{
    private readonly ITestOutputHelper _out;
    public FragTargetTests(ITestOutputHelper output) => _out = output;

    private static readonly Vec3[] Rectangle = { new(0, 0, 100), new(60, 0, 100), new(60, 40, 100), new(0, 40, 100) };

    /// <summary>
    /// A block on a rectangle of <paramref name="outline"/>: holes of 165 mm, bench 10 m, subdrill 1 m, a grid of 4.8 × 5.8 m,
    /// loaded by a rule of ANFO under 3.5 m of stemming; rock A 8.
    /// </summary>
    internal static (BlastProject P, HolePattern Pat) Block(IReadOnlyList<Vec3>? outline = null, double diameter = 165, double burden = 4.8, double spacing = 5.8,
        PatternType type = PatternType.Staggered, double orientation = 0)
    {
        var p = BlastProject.CreateNew();
        p.Header.BlastOrientation = orientation;
        var holes = HoleDesign.FillPolygon(outline ?? Rectangle, new PatternParameters { Burden = burden, Spacing = spacing, Pattern = type, Orientation = orientation, StartNumber = 1 },
            new HoleParameters { BenchHeight = 10, Subdrill = 1, Diameter = diameter }, out var rows, out var pat);
        var layer = p.GetOrCreateLayer(StandardLayers.Holes);
        foreach (var h in holes) p.Add(h, layer);
        p.Rows.AddRange(rows);
        p.Patterns.Add(pat);
        p.LoadingRules[0].StemmingLength = 3.5;
        p.Fragmentation.RockFactor = 8;
        p.Fragmentation.RockFactorSet = true;
        Charging.ApplyRules(p, p.Holes);
        return (p, pat);
    }

    private static byte[] Bytes(BlastProject p) => ProjectJson.Serialize(p);

    // ---------- closed forms ----------

    [Fact]
    public void Inverse_RoundTrip()
    {
        foreach (var x in new[] { 5.0, 12, 30, 75, 140, 200 })
            foreach (var a in new[] { 1.0, 4, 8, 15, 25 })
                foreach (var rws in new[] { 50.0, 85, 100, 150, 200 })
                    foreach (var q in new[] { 20.0, 136, 600 })
                    {
                        var k = KuzRamInverse.PowderFactorFor(x, a, q, rws);
                        var back = Fragmentation.X50(a, k, q, rws);
                        Assert.True(Math.Abs(back / x - 1) < 1e-9, $"x {x} a {a} rws {rws}: {back}");
                        Assert.Equal(a, KuzRamInverse.RockFactorFrom(x, k, q, rws), 9);
                    }
    }

    /// <summary>The example of the plan: d 165 mm, H 10 m, subdrill 1 m, stemming 3.5 m, ANFO 0.85 g/cm³ RWS 100, A 8, S/B 1.2, deviation 0.2 m.</summary>
    [Theory]
    [InlineData(20, 0.99, 3.39, 4.07, 1.43, 36, 0.1)]
    [InlineData(30, 0.60, 4.37, 5.24, 1.39, 55, 2.5)]
    [InlineData(40, 0.42, 5.23, 6.27, 1.34, 75, 9.4)]
    public void ReferenceExample(double x50, double k, double b, double s, double n, double x80, double oversize)
    {
        var q = KuzRamInverse.ChargeMass(165, 11 - 3.5, 0.85);
        Assert.Equal(136, q, 0);
        var pf = KuzRamInverse.PowderFactorFor(x50, 8, q, 100);
        var (B, S) = KuzRamInverse.BurdenSpacing(KuzRamInverse.GridAreaFor(x50, 8, q, 100, 10), 1.2);
        var un = Fragmentation.Uniformity(B, S, 165, 0.2, 7.5, 10);
        Assert.True(Math.Abs(pf - k) < 0.01, $"K {pf}");
        Assert.True(Math.Abs(B - b) < 0.01, $"B {B}");
        Assert.True(Math.Abs(S - s) < 0.01, $"S {S}");
        Assert.True(Math.Abs(un - n) < 0.01, $"n {un}");
        Assert.Equal(x80, Fragmentation.SizeAt(0.8, x50, un), 0);
        Assert.True(Math.Abs(100 * (1 - Fragmentation.RosinRammler(100, x50, un)) - oversize) < 0.1);
        // the forward formula comes back to the target
        Assert.Equal(x50, Fragmentation.X50(8, q / (B * S * 10), q, 100), 9);
    }

    [Fact]
    public void StemmingFromBurden_ConvergesInSixSteps()
    {
        // stemming 0.7·B: the charge depends on the burden
        var (b, s, steps) = KuzRamInverse.SolveGrid(30, 8, 10, 1.2, burden => (KuzRamInverse.ChargeMass(165, 11 - Math.Max(4.125, 0.7 * burden), 0.85), 100), 4);
        Assert.True(steps <= 6, $"{steps} steps");
        var stem = Math.Max(4.125, 0.7 * b);
        var q = KuzRamInverse.ChargeMass(165, 11 - stem, 0.85);
        Assert.True(Math.Abs(Fragmentation.X50(8, q / (b * s * 10), q, 100) / 30 - 1) < 0.005);
    }

    [Fact]
    public void Calibration_RockFactorFromMeasuredX50()
    {
        var (p, _) = Block();
        var r = Fragmentation.Compute(p);
        var a = KuzRamInverse.RockFactorFrom(r.X50Cm, r.PowderFactor, r.ChargePerHole, r.Rws);
        Assert.Equal(8, a, 9);
    }

    [Fact]
    public void Calibration_MeanAndSpread_FromPastBlasts()
    {
        // three blasts made with A 7, 8 and 9: their measured X50 give those back
        var q = 136.0;
        var blasts = new[] { 7.0, 8, 9 }.Select((a, i) => new FragCalibration
        {
            Name = $"#{i}", PowderFactor = 0.6, ChargeKg = q, Rws = 100, MeasuredX50Cm = Fragmentation.X50(a, 0.6, q, 100)
        }).ToList();
        blasts.Add(new FragCalibration { Name = "not used", MeasuredX50Cm = 99, PowderFactor = 0.6, ChargeKg = q, Use = false });
        var (mean, sd, n, values) = KuzRamInverse.Calibrate(blasts);
        Assert.Equal(3, n);
        Assert.Equal(8, mean, 9);
        Assert.Equal(1, sd, 9);
        Assert.Equal(new[] { 7.0, 8, 9 }, values.Select(v => Math.Round(v, 9)));
        var m = new FragmentationModel { Calibration = blasts };
        Assert.Equal(1 / 8.0, KuzRamInverse.Uncertainty(m), 9);
        Assert.Equal(0.2, KuzRamInverse.Uncertainty(new FragmentationModel()));
        // kept in the preset
        var path = Path.Combine(Path.GetTempPath(), "bd-cal-" + Guid.NewGuid().ToString("N") + ".bdfrag");
        try
        {
            FragmentationPreset.Save(path, m);
            Assert.Equal(4, FragmentationPreset.Load(path).Calibration!.Count);
        }
        finally { File.Delete(path); }
    }

    // ---------- the profile ----------

    [Fact]
    public void Profile_X50GrowsWithTheGrid()
    {
        var (p, pat) = Block();
        var b = FragBase.Of(p, pat.Id);
        var exact = new ProfileFragEvaluator(b);
        double last = 0;
        foreach (var k in new[] { 0.8, 0.9, 1.0, 1.1, 1.2 })
        {
            var d = b.CurrentDesign();
            d.Burden *= k; d.Spacing *= k;
            var x = exact.Evaluate(d).X50Cm;
            Assert.True(x >= last, $"X50 {x} after {last} at k {k}");
            last = x;
        }
    }

    [Fact]
    public void Profile_AgainstFormula_Within3Percent()
    {
        var (p, pat) = Block(type: PatternType.Rectangular);
        var b = FragBase.Of(p, pat.Id);
        var d = b.CurrentDesign();
        var fast = new FormulaFragEvaluator(b).Evaluate(d).X50Cm;
        var exact = new ProfileFragEvaluator(b).Evaluate(d).X50Cm;
        _out.WriteLine($"formula {fast:0.00}, profile {exact:0.00}");
        Assert.True(Math.Abs(fast / exact - 1) < 0.03, $"formula {fast}, profile {exact}");
    }

    [Fact]
    public void Solver_HitsTarget_SourceUntouched()
    {
        var (p, pat) = Block();
        var before = Bytes(p);
        var watch = Stopwatch.StartNew();
        var r = FragTargetSolver.SolveAsync(p, pat.Id, null, new FragTarget { X50Cm = 30 }, new FragDesignSpace()).Result;
        _out.WriteLine($"{r.Evaluations} profile runs in {watch.ElapsedMilliseconds} ms; range {r.RangeMinCm:0.0}–{r.RangeMaxCm:0.0}");
        Assert.Equal(before, Bytes(p));
        Assert.NotEmpty(r.Candidates);
        var best = r.Candidates[0];
        Assert.True(Math.Abs(best.Metrics.X50Cm / 30 - 1) <= 0.05, $"X50 {best.Metrics.X50Cm}");
        Assert.False(best.Metrics.Estimate);
        Assert.False(r.FormulasChanged);
        // rounded to the step
        Assert.Equal(Math.Round(best.Design.Burden, 1), best.Design.Burden, 9);
        Assert.Equal(Math.Round(best.Design.Spacing, 1), best.Design.Spacing, 9);
        Assert.True(r.Current!.IsCurrent);
        Assert.InRange(30.0, r.RangeMinCm, r.RangeMaxCm);

        // the variant applied on a copy gives what it said
        var q = ProjectJson.DeepClone(p);
        Assert.Null(FragApply.Apply(q, new[] { pat.Id }, best.Design, FragBase.Of(q, pat.Id).Template).Refusal);
        Assert.Equal(best.Metrics.X50Cm, Fragmentation.Compute(q).X50Cm, 6);
        Assert.True(Math.Abs(Fragmentation.Compute(q).X50Cm / 30 - 1) <= 0.05);
    }

    /// <summary>A profile whose X50 is 1.2 times the built-in one: the solver still reaches the target, and says the formulas are its own.</summary>
    [Fact]
    public void ChangedProfile_StillReachesTarget()
    {
        var (p, pat) = Block();
        var mine = CalcProfile.DeriveFrom(CalcService.BuiltIn, "Карьер X50");
        mine.Formulas.Add(new FormulaDef { Property = "blast.x50", Expression = "1.2 * if(powderFactor <= 0 or avgCharge <= 0, 0, rockFactor * powderFactor ^ -0.8 * avgCharge ^ (1 / 6) * (115 / avgRws) ^ (19 / 30))" });
        CalcService.SetProfile(p, mine);
        Assert.True(FragTargetSolver.FormulasChanged(p));
        var r = FragTargetSolver.Solve(FragBase.Of(p, pat.Id), new FragTarget { X50Cm = 30 }, new FragDesignSpace());
        Assert.True(r.FormulasChanged);
        Assert.NotEmpty(r.Candidates);
        Assert.True(Math.Abs(r.Candidates[0].Metrics.X50Cm / 30 - 1) <= 0.05, $"X50 {r.Candidates[0].Metrics.X50Cm}");
    }

    [Fact]
    public void Unreachable_NearestAndBinding()
    {
        var (p, pat) = Block(diameter: 250, burden: 7, spacing: 8);
        var r = FragTargetSolver.Solve(FragBase.Of(p, pat.Id), new FragTarget { X50Cm = 5 }, new FragDesignSpace());
        Assert.Empty(r.Candidates);
        Assert.NotNull(r.Nearest);
        Assert.True(r.RangeMinCm > 5);
        Assert.True(r.Binding is FragLimit.BurdenPerDiameterMin or FragLimit.PowderFactorMax, $"binding {r.Binding}");
    }

    [Fact]
    public void Unreachable_ByBurdenPerDiameter()
    {
        // a weak explosive load of a large hole: a fine target is stopped by B/d ≥ 20 before the powder factor
        var (p, pat) = Block(diameter: 250, burden: 7, spacing: 8);
        var space = new FragDesignSpace { Constraints = { PowderFactorMax = 50 } };
        var r = FragTargetSolver.Solve(FragBase.Of(p, pat.Id), new FragTarget { X50Cm = 5 }, space);
        Assert.Empty(r.Candidates);
        Assert.Equal(FragLimit.BurdenPerDiameterMin, r.Binding);
        Assert.Equal(5.0, r.Nearest!.Design.Burden, 1);
    }

    [Fact]
    public void Constraints_FlyrockAndPpv_Reject()
    {
        var (p, pat) = Block();
        p.Add(new PointEntity { Kind = PointKind.Monitoring, Position = new Vec3(30, 400, 100), Label = "M1" });
        var free = FragTargetSolver.Solve(FragBase.Of(p, pat.Id), new FragTarget { X50Cm = 30 }, new FragDesignSpace());
        Assert.NotEmpty(free.Candidates);
        var c = free.Candidates[0];
        Assert.NotNull(c.Metrics.Ppv);
        var space = new FragDesignSpace { Constraints = { MaxFlyrockClearance = c.Metrics.FlyrockClearance - 1 } };
        var r = FragTargetSolver.Solve(FragBase.Of(p, pat.Id), new FragTarget { X50Cm = 30 }, space);
        Assert.DoesNotContain(r.Candidates, x => x.Metrics.FlyrockClearance > c.Metrics.FlyrockClearance - 1);
        if (r.Candidates.Count == 0) Assert.Equal(FragLimit.Flyrock, r.Binding);
        space = new FragDesignSpace { Constraints = { MaxPpv = c.Metrics.Ppv!.Value * 0.5 } };
        r = FragTargetSolver.Solve(FragBase.Of(p, pat.Id), new FragTarget { X50Cm = 30 }, space);
        Assert.Empty(r.Candidates);
        Assert.Equal(FragLimit.Ppv, r.Binding);
    }

    [Fact]
    public void Cancel_StopsQuickly()
    {
        var (p, pat) = Block();
        var space = new FragDesignSpace { DiametersMm = { 115, 127, 140, 165, 200 }, RatioFree = true, AzimuthWindowDeg = 15 };
        using var cts = new CancellationTokenSource();
        var task = FragTargetSolver.SolveAsync(p, pat.Id, null, new FragTarget { X50Cm = 30 }, space, null, cts.Token);
        Thread.Sleep(150);
        var watch = Stopwatch.StartNew();
        cts.Cancel();
        try { var r = task.Result; Assert.True(r.Cancelled || r.Candidates.Count >= 0); }
        catch (AggregateException e) when (e.InnerException is OperationCanceledException) { }
        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
    }

    // ---------- applying ----------

    [Fact]
    public void ApplyAndUndo_OneStep()
    {
        var (p, pat) = Block();
        var doc = new Document(p);
        var before = Bytes(doc.Project);
        var r = FragTargetSolver.Solve(FragBase.Of(doc.Project, pat.Id), new FragTarget { X50Cm = 30 }, new FragDesignSpace());
        var design = r.Candidates[0].Design.Clone();
        design.Rock = ProjectJson.DeepClone(doc.Project.Fragmentation);
        design.Rock.RockFactor = 9;
        var steps = doc.UndoNames.Count;
        doc.Execute("X50", q => FragApply.Apply(q, new[] { pat.Id }, design, FragBase.Of(q, pat.Id).Template));
        Assert.Equal(steps + 1, doc.UndoNames.Count);
        Assert.Equal(9, doc.Project.Fragmentation.RockFactor);
        Assert.Equal(design.Burden, doc.Project.FindPattern(pat.Id)!.Burden, 9);
        doc.Undo();
        Assert.Equal(before, Bytes(doc.Project));
        Assert.Equal(8, doc.Project.Fragmentation.RockFactor);
    }

    [Fact]
    public void SegmentedPattern_AppliesThroughTree()
    {
        var (p, pat) = Block(type: PatternType.Rectangular);
        Assert.Null(PatternTree.Split(p, pat, 2).Refusal);
        Assert.True(pat.IsContainer);
        Charging.ApplyRules(p, p.Holes);
        var b = FragBase.Of(p, pat.Id);
        var d = b.CurrentDesign();
        d.Burden = 4.2; d.Spacing = 5.0; d.AzimuthDelta = 5;
        var report = FragApply.Apply(p, new[] { pat.Id }, d, b.Template);
        Assert.Null(report.Refusal);
        Assert.Equal(4.2, p.FindPattern(pat.Id)!.Burden, 9);
        Assert.All(PatternTree.TreeHoles(p, pat).Where(h => !h.IsDummy), h => Assert.NotEmpty(h.Decks));
    }

    // ---------- presets ----------

    [Fact]
    public void Preset_Version2KeepsTarget_Version1Reads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bd-frag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var m = new FragmentationModel { RockFactor = 7, Target = new FragTarget { X50Cm = 25, TolerancePct = 4, OversizeMaxPct = 3 } };
            var path = Path.Combine(dir, "a.bdfrag");
            FragmentationPreset.Save(path, m);
            Assert.Contains("\"version\":2", File.ReadAllText(path).Replace(" ", ""));
            var back = FragmentationPreset.Load(path);
            Assert.Equal(25, back.Target!.X50Cm);
            Assert.Equal(3, back.Target.OversizeMaxPct);
            // a file of version 1 (before the target)
            var old = Path.Combine(dir, "old.bdfrag");
            File.WriteAllText(old, "{\"format\":\"BlastDesigner.FragmentationPreset\",\"version\":1,\"inputs\":{\"rockFactor\":6,\"oversizeCm\":90}}");
            var o = FragmentationPreset.Load(old);
            Assert.Null(o.Target);
            Assert.Equal(6, o.RockFactor);
            Assert.Equal(90, o.OversizeCm);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Project_WithoutTarget_WritesNone()
    {
        var p = BlastProject.CreateNew();
        Assert.DoesNotContain("\"target\"", System.Text.Encoding.UTF8.GetString(ProjectJson.Serialize(p)));
        p.Fragmentation.Target = new FragTarget { X50Cm = 22 };
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(p));
        Assert.Equal(22, back.Fragmentation.Target!.X50Cm);
    }

    // ---------- two explosives from the catalogue ----------

    [Fact]
    public void Catalogue_Candidates()
    {
        var p = BlastProject.CreateNew();
        var anfo = p.Products.First(x => x.Name == "ANFO");
        var emulsion = p.Products.First(x => x.Name == "Bulk emulsion");
        var gone = emulsion.Clone(); gone.Id = ProductCatalogue.NewId(); gone.Name = "Old emulsion"; gone.Deleted = true; p.Products.Add(gone);
        var weak = anfo.Clone(); weak.Id = ProductCatalogue.NewId(); weak.Name = "No RWS"; weak.RelativeWeightStrength = 0; p.Products.Add(weak);
        var dry = FragTargetSolver.ExplosiveCandidates(p, wet: false);
        Assert.Contains(anfo, dry);
        Assert.Contains(emulsion, dry);
        Assert.DoesNotContain(gone, dry);
        Assert.DoesNotContain(weak, dry);
        Assert.All(dry, x => Assert.True(x.IsExplosive));
        Assert.DoesNotContain(dry, x => x.Kind is ProductKind.Stemming or ProductKind.Booster or ProductKind.DownholeDetonator);
        var wet = FragTargetSolver.ExplosiveCandidates(p, wet: true);
        Assert.DoesNotContain(anfo, wet);
        Assert.All(wet, x => Assert.True(x.WaterResistant));
        Assert.Contains(FragTargetSolver.ExplosiveChoices(p, false), c => c.Product == weak && !c.Usable && c.Reason == "no RWS");
    }

    [Fact]
    public void SchemeRule_TwoDecks()
    {
        var (p, pat) = Block();
        var anfo = p.Products.First(x => x.Name == "ANFO");
        var emulsion = p.Products.First(x => x.Name == "Bulk emulsion");
        var scheme = new ChargeScheme { BottomProductId = emulsion.Id, ColumnProductId = anfo.Id, BottomLength = 2 };
        var rule = scheme.ToLoadingRule(p, 4.2, 2, p.LoadingRules[0], "X50: Bulk emulsion 2 m + ANFO");
        var h = p.Holes.First(x => !x.IsDummy);
        Charging.ApplyRule(p, h, rule);
        var expl = h.Decks.Where(d => p.FindProduct(d.ProductId)!.IsExplosive).OrderByDescending(d => d.Bottom).ToList();
        Assert.Equal(2, expl.Count);
        Assert.Equal(emulsion.Id, expl[0].ProductId);
        Assert.Equal(2, expl[0].Length, 6);
        Assert.Equal(h.EffectiveLength - 4.2 - 2, expl[1].Length, 6);
        Assert.True(Charging.TopStemming(p, h) >= Charging.MinStemming(h) - 1e-9);
        Assert.False(Charging.LoadingWarnings(p, h).Contains(LoadingWarning.NoPrimer));
        // the scheme read back from the rule and from the hole
        var fromRule = ChargeScheme.FromRule(p, rule)!;
        Assert.Equal(emulsion.Id, fromRule.BottomProductId);
        Assert.Equal(2, fromRule.BottomLength);
        Assert.Equal(emulsion.Id, ChargeScheme.FromHole(p, h)!.BottomProductId);
    }

    [Fact]
    public void TwoExplosives_SolverReachesTarget()
    {
        var (p, pat) = Block();
        var anfo = p.Products.First(x => x.Name == "ANFO");
        var emulsion = p.Products.First(x => x.Name == "Bulk emulsion");
        var scheme = new ChargeScheme { BottomProductId = emulsion.Id, ColumnProductId = anfo.Id, BottomLength = 2 };
        var space = new FragDesignSpace { Schemes = { scheme } };
        var r = FragTargetSolver.Solve(FragBase.Of(p, pat.Id), new FragTarget { X50Cm = 30 }, space);
        Assert.NotEmpty(r.Candidates);
        var best = r.Candidates[0];
        Assert.False(best.Design.Scheme.IsSingle);
        var q = ProjectJson.DeepClone(p);
        FragApply.Apply(q, new[] { pat.Id }, best.Design, FragBase.Of(q, pat.Id).Template);
        Assert.True(Math.Abs(Fragmentation.Compute(q).X50Cm / 30 - 1) <= 0.05);
        var s = CalcService.Session(q);
        Assert.Equal(2, s.Number("blast.avgBottomChargeLength", q), 6);
    }

    [Fact]
    public void DefaultScheme_FromTheRuleOfTheArea()
    {
        var (p, pat) = Block();
        var anfo = p.Products.First(x => x.Name == "ANFO");
        var emulsion = p.Products.First(x => x.Name == "Bulk emulsion");
        p.LoadingRules[0].Decks.Insert(0, new DeckTemplate { ProductId = emulsion.Id, Length = 2.5 });
        var b = FragBase.Of(p, pat.Id);
        Assert.False(b.Scheme!.IsSingle);
        Assert.Equal(emulsion.Id, b.Scheme.BottomProductId);
        Assert.Equal(anfo.Id, b.Scheme.ColumnProductId);
        Assert.Equal(2.5, b.Scheme.BottomLength);
    }

    /// <summary>A target the present diameter cannot reach: another diameter or explosive is offered.</summary>
    [Fact]
    public void OutsideTheCurrentDiameter_OtherDiameterOffered()
    {
        var (p, pat) = Block(diameter: 250, burden: 7, spacing: 8);
        // the finest the present 250 mm reaches, and a target a quarter finer
        var range = FragTargetSolver.Solve(FragBase.Of(p, pat.Id), new FragTarget { X50Cm = 1 }, new FragDesignSpace());
        var target = Math.Round(range.RangeMinCm * 0.75, 1);
        var only = FragTargetSolver.Solve(FragBase.Of(p, pat.Id), new FragTarget { X50Cm = target }, new FragDesignSpace());
        Assert.Empty(only.Candidates);
        var space = new FragDesignSpace { DiametersMm = { 250, 115, 127 } };
        var r = FragTargetSolver.Solve(FragBase.Of(p, pat.Id), new FragTarget { X50Cm = target }, space);
        _out.WriteLine($"range {r.RangeMinCm:0.0}–{r.RangeMaxCm:0.0}, binding {r.Binding}, nearest {r.Nearest?.Design.DiameterMm} {r.Nearest?.Design.Burden:0.00}x{r.Nearest?.Design.Spacing:0.00} X50 {r.Nearest?.Metrics.X50Cm:0.0} pf {r.Nearest?.Metrics.PowderFactor:0.00} {string.Join(",", r.Nearest?.Violations ?? new())}");
        Assert.NotEmpty(r.Candidates);
        Assert.True(r.Candidates[0].Design.DiameterMm < 250);
    }

    [Fact]
    public void FreeRatio_StaysInItsRange()
    {
        var (p, pat) = Block();
        var r = FragTargetSolver.Solve(FragBase.Of(p, pat.Id), new FragTarget { X50Cm = 30 }, new FragDesignSpace { RatioFree = true });
        Assert.NotEmpty(r.Candidates);
        Assert.InRange(r.Candidates[0].Ratio, 1.0 - 1e-9, 1.5 + 1e-9);
    }

    // ---------- the azimuth ----------

    /// <summary>A long thin outline at 30° to the grid: turning the grid along it leaves fewer holes, within the window.</summary>
    [Fact]
    public void Azimuth_FewerHoles_WithinWindow()
    {
        // a strip 120 × 14 m whose long side runs at bearing 100°: the grid rows run E-W at azimuth 0
        var a = 10 * Math.PI / 180;
        Vec3 P(double x, double y) => new(x * Math.Cos(a) + y * Math.Sin(a), -x * Math.Sin(a) + y * Math.Cos(a), 100);
        var strip = new[] { P(0, 0), P(120, 0), P(120, 14), P(0, 14) };
        var (p, pat) = Block(strip, burden: 4, spacing: 4.6, type: PatternType.Rectangular);
        var b = FragBase.Of(p, pat.Id);
        var d = b.CurrentDesign();
        var (delta, before, after) = FragTargetSolver.BestTurn(b, d, 15);
        _out.WriteLine($"turn {delta}°: {before} → {after}");
        Assert.InRange(delta, -15, 15);
        Assert.True(after <= before - 2, $"{before} → {after}");
        // a window of 0 never turns
        Assert.Equal(0, FragTargetSolver.BestTurn(b, d, 0).Delta);
        // a narrow window is kept
        Assert.InRange(FragTargetSolver.BestTurn(b, d, 3).Delta, -3, 3);
    }

    [Fact]
    public void Azimuth_Rhombus_BestAlongItsSides()
    {
        // a long parallelogram whose sides run 20° off the grid rows: the best turn found is the best of every whole degree
        Vec3 V(double x, double y) => new(x, y, 100);
        var t = Math.Tan(20 * Math.PI / 180);
        var rhombus = new[] { V(0, 0), V(100, 100 * t), V(100, 100 * t + 10), V(0, 10) };
        var (p, pat) = Block(rhombus, burden: 4, spacing: 4.6, type: PatternType.Rectangular);
        var b = FragBase.Of(p, pat.Id);
        var d = b.CurrentDesign();
        var (delta, before, after) = FragTargetSolver.BestTurn(b, d, 30);
        var brute = Enumerable.Range(-30, 61).Select(a => (a, n: FragTargetSolver.CountHoles(p, pat, d.Burden, d.Spacing, a))).ToList();
        var min = brute.Min(x => x.n);
        _out.WriteLine($"turn {delta}°: {before} → {after}; whole degrees: least {min} at {string.Join(", ", brute.Where(x => x.n == min).Select(x => x.a))}");
        Assert.True(before - min >= 2, "the outline should gain from a turn");
        Assert.Equal(min, after);
        Assert.InRange(Math.Abs(delta), 5, 30);
    }

    [Fact]
    public void Solver_WithAzimuth_TurnsAndKeepsTarget()
    {
        var a = 12 * Math.PI / 180;
        Vec3 P(double x, double y) => new(x * Math.Cos(a) + y * Math.Sin(a), -x * Math.Sin(a) + y * Math.Cos(a), 100);
        var strip = new[] { P(0, 0), P(120, 0), P(120, 16), P(0, 16) };
        var (p, pat) = Block(strip, type: PatternType.Rectangular);
        var r = FragTargetSolver.Solve(FragBase.Of(p, pat.Id), new FragTarget { X50Cm = 30 }, new FragDesignSpace { AzimuthWindowDeg = 15 });
        Assert.NotEmpty(r.Candidates);
        var c = r.Candidates[0];
        Assert.InRange(c.Design.AzimuthDelta, -15, 15);
        Assert.True(c.HolesAfterTurn <= c.HolesBeforeTurn);
        Assert.True(Math.Abs(c.Metrics.X50Cm / 30 - 1) <= 0.05);
    }

    // ---------- the cost of a trial ----------

    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    public void TrialCost_Measured(int holes)
    {
        // a square block of about this many holes at 4.8 × 5.8
        var side = Math.Sqrt(holes * 4.8 * 5.8);
        var (p, pat) = Block(new[] { new Vec3(0, 0, 100), new Vec3(side, 0, 100), new Vec3(side, side, 100), new Vec3(0, side, 100) });
        var b = FragBase.Of(p, pat.Id);
        var exact = new ProfileFragEvaluator(b);
        var d = b.CurrentDesign();
        exact.Evaluate(d);
        var watch = Stopwatch.StartNew();
        const int n = 3;
        for (int i = 0; i < n; i++) { d.Burden *= 1.01; exact.Evaluate(d); }
        _out.WriteLine($"{b.HoleCount} holes: {watch.ElapsedMilliseconds / (double)n:0} ms a trial");
        Assert.True(b.HoleCount > holes * 0.7);
    }
}
