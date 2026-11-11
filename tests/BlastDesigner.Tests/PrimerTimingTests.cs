using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;

namespace BlastDesigner.Tests;

/// <summary>The DeviTronic detonator, primers with times of their own and the charges they fire.</summary>
public class PrimerTimingTests
{
    private const string DeviTronicId = "5f2d6f31-4a02-4514-a200-0a6d6490db5a";

    private static BlastProject Reopened(BlastProject p)
    {
        using var ms = new MemoryStream();
        ProjectFile.Write(p, ms);
        ms.Position = 0;
        return ProjectFile.Read(ms);
    }

    [Fact]
    public void DeviTronicIsInANewProjectAndInAnOldOneOnce_AndAHoleWithItIsElectronic()
    {
        var fresh = BlastProject.CreateNew();
        Assert.Single(fresh.Products, x => x.Id == DeviTronicId);
        Assert.Contains(fresh.Catalogue.Families, f => f.Id == "31bbb3a1-1929-4dcd-b8aa-3d7b0325c3d2" && f.Name == "DeviTronic");

        // a file saved before DeviTronic was in the catalogue
        var old = BlastProject.CreateNew();
        old.Products.RemoveAll(x => x.Id == DeviTronicId);
        var opened = Reopened(old);
        var devi = Assert.Single(opened.Products, x => x.Id == DeviTronicId);
        Assert.Equal(ProductKind.ElectronicDetonator, devi.Kind);
        Assert.Single(Reopened(opened).Products, x => x.Id == DeviTronicId);

        var hole = new Hole { Length = 10 };
        hole.Decks.Add(new Deck { Top = 3, Bottom = 10, ProductId = opened.Products.First(x => x.IsExplosive).Id, Primers = { new Primer { Depth = 9.5, DetonatorId = DeviTronicId } } });
        Assert.True(Charging.HasElectronicDetonator(opened, hole));
    }

    private static string Explosive(BlastProject p) => p.Products.First(x => x.IsExplosive).Id;
    private static string Stemming(BlastProject p) => p.Products.First(x => x.Kind == ProductKind.Stemming).Id;
    private static string Booster(BlastProject p) => p.Products.First(x => x.Kind == ProductKind.Booster).Id;

    /// <summary>A 12 m hole: stemming 0–3, explosive 3–6, stemming 6–7, explosive 7–12, an electronic primer in each explosive deck.</summary>
    private static Hole TwoDecks(BlastProject p)
    {
        var h = new Hole { Length = 12, Label = "1" };
        h.Decks.Add(new Deck { Top = 0, Bottom = 3, ProductId = Stemming(p) });
        h.Decks.Add(new Deck { Top = 3, Bottom = 6, ProductId = Explosive(p), Primers = { new Primer { Depth = 5.5, DetonatorId = DeviTronicId, BoosterId = Booster(p) } } });
        h.Decks.Add(new Deck { Top = 6, Bottom = 7, ProductId = Stemming(p) });
        h.Decks.Add(new Deck { Top = 7, Bottom = 12, ProductId = Explosive(p), Primers = { new Primer { Depth = 11.5, DetonatorId = DeviTronicId, BoosterId = Booster(p) } } });
        return h;
    }

    [Fact]
    public void ThePrimerOffsetIsKeptInTheFileAndInARuleFromTheColumn()
    {
        var p = BlastProject.CreateNew();
        var h = TwoDecks(p);
        h.Decks[1].Primers[0].OffsetMs = 25;
        h.Decks[3].Primers.Add(new Primer { Depth = 8, DetonatorId = p.Products.First(x => x.Kind == ProductKind.DownholeDetonator).Id, DelayMs = 475 });
        p.Add(h);
        var opened = Reopened(p);
        Assert.Equal(25, opened.Holes.Single().Decks[1].Primers[0].OffsetMs);

        // a rule saved from the column keeps each primer: its height, detonator, delay and offset
        var rule = Charging.RuleFromColumn(p, h.Decks, "two decks");
        var bottom = rule.Decks[0];
        Assert.Equal(2, bottom.Primers.Count);
        Assert.Contains(bottom.Primers, x => x.DelayMs == 475 && x.Offset == 4);
        var copy = ProjectJson.DeepClone(rule);
        var loaded = new Hole { Length = 12 };
        Charging.ApplyRule(p, loaded, copy);
        var primers = Charging.Primers(loaded).Select(x => x.Primer).ToList();
        Assert.Equal(3, primers.Count);
        Assert.Contains(primers, x => x.OffsetMs == 25 && x.DetonatorId == DeviTronicId && Math.Abs(x.Depth - 5.5) < 1e-9);
        Assert.Contains(primers, x => x.DelayMs == 475 && Math.Abs(x.Depth - 8) < 1e-9);
    }

    [Fact]
    public void ARuleOfAnOldFileReadsItsPrimersAsAListOfLikeOnes()
    {
        var p = BlastProject.CreateNew();
        var json = $$"""
            { "name": "old", "decks": [
              { "productId": "{{Explosive(p)}}", "length": 0, "primerCount": 2, "boosterId": "b", "detonatorId": "d", "primerOffset": 0.7 },
              { "productId": "{{Explosive(p)}}", "length": 2, "primerCount": 2, "boosterId": "b", "detonatorId": "d", "primerOffsets": [ 0.2, 1.5 ] } ] }
            """;
        var rule = System.Text.Json.JsonSerializer.Deserialize<LoadingRule>(json, ProjectJson.Options)!;
        var fill = rule.Decks[0].Primers;
        Assert.Equal(2, fill.Count);
        Assert.All(fill, x => { Assert.Equal("b", x.BoosterId); Assert.Equal("d", x.DetonatorId); });
        Assert.Equal(0.7, fill[0].Offset);
        Assert.Null(fill[1].Offset);
        Assert.Equal(new double?[] { 0.2, 1.5 }, rule.Decks[1].Primers.Select(x => x.Offset));
        // written again: the list only
        var again = System.Text.Json.JsonSerializer.Serialize(rule, ProjectJson.Options);
        Assert.Contains("\"primers\"", again);
        Assert.DoesNotContain("primerCount", again);
        Assert.DoesNotContain("primerOffset", again);
        // the old even spacing is kept: the second primer of the fill deck half way up a 9 m deck
        p.LoadingRules.Clear();
        var h = new Hole { Length = 12 };
        rule.StemmingLength = 3;
        rule.Decks.RemoveAt(1);
        Charging.ApplyRule(p, h, rule);
        Assert.Equal(new[] { 11.3, 6.8 }, Charging.Primers(h).Select(x => Math.Round(x.Primer.Depth, 6)).OrderByDescending(x => x));
    }

    [Fact]
    public void TheDecksOfAHoleFireOneAfterAnotherBottomUpOrTopDown()
    {
        var p = BlastProject.CreateNew();
        p.Electronic.InterDeckDelayMs = 25;
        var h = TwoDecks(p);
        var upper = h.Decks[1].Primers[0];
        var lower = h.Decks[3].Primers[0];
        var t = Charging.PrimerTimes(p, h, 100);
        Assert.Equal(100, t[lower]);
        Assert.Equal(125, t[upper]);
        p.Electronic.DeckOrder = DeckFiringOrder.TopDown;
        t = Charging.PrimerTimes(p, h, 100);
        Assert.Equal(100, t[upper]);
        Assert.Equal(125, t[lower]);
        p.Electronic.DeckOrder = DeckFiringOrder.Simultaneous;
        Assert.All(Charging.PrimerTimes(p, h, 100).Values, v => Assert.Equal(100, v));
    }

    [Fact]
    public void ThePrimersOfADeckFollowOneAnotherAndOffsetsReplaceTheDeckDelays()
    {
        var p = BlastProject.CreateNew();
        p.Electronic.IntraDeckDelayMs = 9;
        var h = TwoDecks(p);
        var extra = new Primer { Depth = 8, DetonatorId = DeviTronicId };
        h.Decks[3].Primers.Add(extra);
        var t = Charging.PrimerTimes(p, h, 50);
        Assert.Equal(50, t[h.Decks[3].Primers[0]]);
        Assert.Equal(59, t[extra]);
        p.Electronic.PrimerOrder = PrimerFiringOrder.TopDown;
        Assert.Equal(50, Charging.PrimerTimes(p, h, 50)[extra]);

        p.Electronic.UseOffsets = true;
        h.Decks[1].Primers[0].OffsetMs = 25;
        extra.OffsetMs = -80;
        t = Charging.PrimerTimes(p, h, 50);
        Assert.Equal(75, t[h.Decks[1].Primers[0]]);
        Assert.Equal(50, t[h.Decks[3].Primers[0]]);
        Assert.Equal(0, t[extra]);
        // rounded as the detonators take it
        h.Decks[1].Primers[0].OffsetMs = 0.46;
        Assert.Equal(50, Charging.PrimerTimes(p, h, 50)[h.Decks[1].Primers[0]]);
        p.Electronic.AllowSubMs = true;
        Assert.Equal(50.5, Charging.PrimerTimes(p, h, 50)[h.Decks[1].Primers[0]], 9);
    }

    [Fact]
    public void AHoleOfTwoDecksFiresTwoChargesOnThePlotAndTwoTimesInTheBpd()
    {
        var p = BlastProject.CreateNew();
        p.Electronic.InterDeckDelayMs = 25;
        var h = TwoDecks(p);
        h.Collar = new Vec3(0, 0, 100);
        h.ElectronicTimeMs = 200;
        var next = TwoDecks(p);
        next.Label = "2";
        next.Collar = new Vec3(5, 0, 100);
        next.ElectronicTimeMs = 1000;
        p.Add(h); p.Add(next);
        p.Add(new TieEntity { FromId = h.Id, ToId = next.Id, ProductId = p.Products.First(x => x.Kind == ProductKind.HarnessWire).Id });
        Timing.Solve(p);

        // the lower deck at the hole's time, the upper one 25 ms later; the hole fires with its first charge
        Assert.Equal(2, h.Charges.Count);
        Assert.Equal(new double?[] { 225, 200 }, h.Charges.Select(c => c.TimeMs));
        Assert.Equal(200, h.FiringTimeMs);
        Assert.Equal(4.5, h.Charges[0].MidDepth, 9);
        Assert.Equal(9.5, h.Charges[1].MidDepth, 9);

        // charge against time: two events of the hole, with the masses of its decks and their boosters
        var booster = p.FindProduct(Booster(p))!.UnitMass;
        var upperKg = Charging.DeckMass(p, h, h.Decks[1]) + booster;
        var lowerKg = Charging.DeckMass(p, h, h.Decks[3]) + booster;
        var plot = Analysis.ChargeVsTime(p, 1);
        Assert.Equal(lowerKg, plot.Single(x => x.TimeMs == 200).ChargeKg, 6);
        Assert.Equal(upperKg, plot.Single(x => x.TimeMs == 225).ChargeKg, 6);
        var holeKg = Charging.ExplosiveMass(p, h);
        Assert.Equal(holeKg, plot.Where(x => x.TimeMs < 500).Sum(x => x.ChargeKg), 6);
        // the 8 ms window holds one deck, not the whole hole
        Assert.True(Timing.MaxInstantaneousCharge(p, 8) < holeKg - 1e-6);

        // the timing front measures from the middle of the first charge
        Assert.Equal(h.PointAtDepth(9.5), EbsTimingLine.TimingPoint(p, h));

        // BPD: each detonator at the time of its primer
        var chain = Assert.Single(DeviTronic.Chains(p));
        var r = DeviTronic.Build(p, p.Holes, new[] { new BpdPathRequest(chain.Key, "1") }, BpdDelayUnit.Ms, "t");
        Assert.Empty(r.Errors);
        var first = r.Plan.Paths[0].Holes.First(x => x.Source == h);
        Assert.Equal(new long[] { 200, 225 }, first.Detonators.Select(d => d.Delay));
    }

    /// <summary>A vertical electronic hole at (x, y).</summary>
    private static Hole Ebs(BlastProject p, double x, double y)
    {
        var h = new Hole { Label = $"{x}/{y}", Collar = new Vec3(x, y, 100), Length = 10 };
        h.Decks.Add(new Deck { Top = 3, Bottom = 10, ProductId = Explosive(p), Primers = { new Primer { Depth = 9.5, DetonatorId = DeviTronicId } } });
        return h;
    }

    [Fact]
    public void AnOpeningPointInTheMiddleOfTheFrontRowTimesAVPattern()
    {
        var p = BlastProject.CreateNew();
        // burden 4 m to the north, spacing 5 m along x
        var holes = new List<Hole>();
        for (int row = 0; row < 3; row++)
            for (int i = -2; i <= 2; i++) holes.Add(p.Add(Ebs(p, i * 5, row * 4)));
        var nonel = p.Add(new Hole { Label = "nonel", Collar = new Vec3(0, -8, 100), Decks = { new Deck { Top = 3, Bottom = 10, ProductId = Explosive(p), Primers = { new Primer { Depth = 9.5, DetonatorId = p.Products.First(x => x.Kind == ProductKind.DownholeDetonator).Id } } } } });
        var op = new EbsOpeningPoint { Position = new Vec3(0.4, -0.3, 100), BurdenM = 4, SpacingM = 5, BurdenBearingDeg = 0, BurdenDelayMs = 42, SpacingDelayMs = 17, StartOffsetMs = 100, OpenHere = true };
        Assert.Equal(11.0, op.TotalReliefMsPerM, 1);
        var r = EbsOpening.Apply(p, p.Holes.ToList(), op);
        Assert.Equal(15, r.Timed);
        Assert.Equal(1, r.NotElectronic);
        Assert.Null(nonel.ElectronicTimeMs);
        double T(double x, double y) => holes.Single(h => h.Collar.X == x && h.Collar.Y == y).ElectronicTimeMs!.Value;
        // opened at the hole nearest the point: a V, the same to both sides, a spacing 17 ms, a burden 42 ms
        Assert.Equal(100, T(0, 0));
        Assert.Equal(117, T(-5, 0));
        Assert.Equal(117, T(5, 0));
        Assert.Equal(134, T(10, 0));
        Assert.Equal(142, T(0, 4));
        Assert.Equal(159, T(-5, 4));
        Assert.Equal(T(-10, 8), T(10, 8));

        // not opened here: the times grow one way along the spacing, from the earliest hole
        op.OpenHere = false;
        EbsOpening.Apply(p, p.Holes.ToList(), op);
        Assert.Equal(100, T(-10, 0));
        Assert.Equal(168, T(10, 0));
        Assert.Equal(142, T(-10, 4));

        // kept in the file
        op.HoleIds = holes.Select(h => h.Id).ToList();
        p.Add(op);
        var copy = Assert.Single(Reopened(p).Entities.OfType<EbsOpeningPoint>());
        Assert.Equal((4.0, 5.0, 42.0, 17.0, 100.0, false, 15), (copy.BurdenM, copy.SpacingM, copy.BurdenDelayMs, copy.SpacingDelayMs, copy.StartOffsetMs, copy.OpenHere, copy.HoleIds.Count));
    }

    [Fact]
    public void AnOpeningPointProgramsItsHolesAgainWhenItsValuesChange()
    {
        var p = BlastProject.CreateNew();
        var a = p.Add(Ebs(p, 0, 0));
        var b = p.Add(Ebs(p, 5, 0));
        var op = new EbsOpeningPoint { Position = new Vec3(0, 0, 100), HoleIds = { a.Id, b.Id } };
        p.Add(op);
        EbsOpening.Apply(p, p.Holes.ToList(), op);
        Assert.Equal(17, b.ElectronicTimeMs);
        var doc = new Document(p);
        doc.Execute("Spacing delay", pr => ((EbsOpeningPoint)pr.FindEntity(op.Id)!).SpacingDelayMs = 25);
        Assert.Equal(25, ((Hole)doc.Project.FindEntity(b.Id)!).ElectronicTimeMs);
    }

    [Fact]
    public void TheSignalRunsDownTheTubeOfAPyrotechnicPrimer()
    {
        var p = BlastProject.CreateNew();
        var det = p.Products.First(x => x.Kind == ProductKind.DownholeDetonator);
        det.PropagationMsPerM = 0.5;
        var h = p.Add(new Hole { Label = "1", Collar = new Vec3(0, 0, 100), Length = 12 });
        h.Decks.Add(new Deck { Top = 3, Bottom = 12, ProductId = Explosive(p), Primers = { new Primer { Depth = 10, DetonatorId = det.Id } } });
        var ip = p.Add(new InitiationPoint { Position = h.Collar, HoleId = h.Id, StartTimeMs = 0 });
        Timing.Solve(p);
        Assert.Equal(det.DelayMs + 5, h.FiringTimeMs!.Value, 9);
        // an electronic primer does not take it
        Assert.Equal(0, Charging.PropagationDelay(p, new Primer { Depth = 10, DetonatorId = DeviTronicId }));
        // a file before it reads 0
        det.PropagationMsPerM = 0;
        Timing.Solve(p);
        Assert.Equal(det.DelayMs, h.FiringTimeMs!.Value, 9);
    }

    [Fact]
    public void TheBurdenReliefOfATriangleIsHowSteepTheTimeIs()
    {
        var p = BlastProject.CreateNew();
        // t = 100 + 10·x + 2·y: a plane, every triangle as steep, √(10² + 2²) ms/m
        for (int y = 0; y <= 8; y += 4)
            for (int x = 0; x <= 20; x += 5)
            {
                var h = p.Add(Ebs(p, x, y));
                h.ElectronicTimeMs = 100 + 10 * x + 2 * y;
            }
        Timing.Solve(p);
        var d = BurdenRelief.Build(p);
        var valid = BurdenRelief.Valid(d).ToList();
        Assert.NotEmpty(valid);
        var expected = Math.Sqrt(10 * 10 + 2 * 2);
        Assert.All(valid, t => Assert.Equal(expected, t.Slope, 6));
        Assert.Equal(expected, BurdenRelief.At(d, new Vec3(7, 2, 0))!.Value, 6);
        Assert.Null(BurdenRelief.At(d, new Vec3(-50, -50, 0)));
        // five classes from the least to the upper limit, past it the last
        Assert.Equal(0, BurdenRelief.Class(4, 4, 14));
        Assert.Equal(2, BurdenRelief.Class(9.5, 4, 14));
        Assert.Equal(4, BurdenRelief.Class(30, 4, 14));
        Assert.Equal(expected, BurdenRelief.Range(d, 20).Min, 6);
        Assert.Equal(20, BurdenRelief.Range(d, 20).Max);
    }
}
