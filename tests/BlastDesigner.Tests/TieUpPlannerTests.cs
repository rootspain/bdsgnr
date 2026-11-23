using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

public class TieUpPlannerTests
{
    /// <summary>A block 40 × 16 m, burden 4, spacing 5, charged by the rules, the point in front of the middle.</summary>
    private static (BlastProject P, List<Hole> Holes, InitiationPoint Ip) Block()
    {
        var p = BlastProject.CreateNew();
        var holes = HoleDesign.FillPolygon(new[] { new Vec3(0, 0, 0), new Vec3(40, 0, 0), new Vec3(40, 16, 0), new Vec3(0, 16, 0) },
            new PatternParameters { Burden = 4, Spacing = 5 }, new HoleParameters(), out var rows);
        foreach (var h in holes) p.Add(h);
        p.Rows.AddRange(rows);
        p.Design.Burden = 4;
        p.Design.Spacing = 5;
        Charging.ApplyRules(p, holes);
        var ip = p.Add(new InitiationPoint { Position = new Vec3(20, -5, 0) });
        return (p, holes, ip);
    }

    private static TieUpOptions Options(BlastProject p, InitiationPoint ip, int perBlock = 1) => new()
    {
        Start = ip,
        InRow = p.Products.First(x => x.Kind == ProductKind.SurfaceConnector && x.DelayMs == 17),
        BetweenRows = p.Products.First(x => x.Kind == ProductKind.SurfaceConnector && x.DelayMs == 42),
        Connectors = TieUpPlanner.SurfaceConnectors(p),
        HolesPerBlock = perBlock
    };

    [Fact]
    public void EveryLayout_ReachesEveryHoleWithATree()
    {
        var (p, holes, ip) = Block();
        var plans = TieUpPlanner.Plan(p, holes, Options(p, ip));
        Assert.Equal(TieUpPlanner.AllSchemes.Length, plans.Count);
        Assert.Single(plans, x => x.Best);
        foreach (var plan in plans)
        {
            Assert.Equal(0, plan.Unreached);
            // one tie into each hole: the lead from the point and one for every other hole
            Assert.Equal(holes.Count, plan.Ties.Count);
            Assert.Equal(holes.Count, plan.Ties.Select(t => t.To).Distinct().Count());
            Assert.Equal(holes.Count, plan.Firing.Count);
        }
    }

    [Theory]
    [InlineData(TieScheme.Rows, 1)]
    [InlineData(TieScheme.VFront, 1)]
    [InlineData(TieScheme.Echelon, 1)]
    [InlineData(TieScheme.Diagonal, 1)]
    [InlineData(TieScheme.VDiagonal, 1)]
    [InlineData(TieScheme.Fan, 1)]
    [InlineData(TieScheme.Rows, 2)]
    [InlineData(TieScheme.VFront, 3)]
    public void Applied_FiresAsPlanned(TieScheme scheme, int perBlock)
    {
        var (p, holes, ip) = Block();
        var plan = TieUpPlanner.Build(p, holes, Options(p, ip, perBlock), scheme);
        TieUpPlanner.Apply(p, holes, plan);
        Timing.Solve(p);
        foreach (var h in holes) Assert.Equal(plan.Firing[h.Id], h.FiringTimeMs!.Value, 6);
        Assert.Empty(TieUpCheck.Unconnected(p, primedOnly: false).Holes);
    }

    [Fact]
    public void VFront_FrontRowBothWaysAndColumnsBack()
    {
        var (p, holes, ip) = Block();
        var plan = TieUpPlanner.Build(p, holes, Options(p, ip), TieScheme.VFront);
        var front = plan.Ties.Count(t => t.Role == TieRole.InRow);
        var back = plan.Ties.Count(t => t.Role == TieRole.BetweenRows);
        Assert.Equal(p.Rows.OrderBy(r => r.HoleIds.Select(id => ((Hole)p.FindEntity(id)!).Collar.Y).Average()).First().HoleIds.Count - 1, front);
        Assert.Equal(holes.Count - 1 - front, back);
        // the nearest hole to the point fires first
        var first = holes.OrderBy(h => Vec3.Distance2D(h.Collar, ip.Position)).First();
        Assert.Equal(plan.Firing.Values.Min(), plan.Firing[first.Id]);
    }

    [Fact]
    public void Fan_TimeGrowsWithDistance()
    {
        var (p, holes, ip) = Block();
        var plan = TieUpPlanner.Build(p, holes, Options(p, ip), TieScheme.Fan);
        var byDistance = holes.OrderBy(h => Vec3.Distance2D(h.Collar, ip.Position)).ToList();
        var near = byDistance.Take(5).Average(h => plan.Firing[h.Id]);
        var far = byDistance.TakeLast(5).Average(h => plan.Firing[h.Id]);
        Assert.True(far > near + 50, $"far {far} near {near}");
        // every tie goes outwards: from a hole nearer the point
        var pos = holes.ToDictionary(h => h.Id, h => h.Collar);
        Assert.All(plan.Ties.Where(t => t.Role == TieRole.Fan), t =>
            Assert.True(Vec3.Distance2D(pos[t.From], ip.Position) <= Vec3.Distance2D(pos[t.To], ip.Position) + 1e-6));
    }

    [Fact]
    public void SharedBlocks_CutConnectorsAndStepInHoleDelays()
    {
        var (p, holes, ip) = Block();
        var single = TieUpPlanner.Build(p, holes, Options(p, ip, 1), TieScheme.Rows);
        var shared = TieUpPlanner.Build(p, holes, Options(p, ip, 2), TieScheme.Rows);
        Assert.True(shared.Connectors < single.Connectors, $"{shared.Connectors} vs {single.Connectors}");
        Assert.NotEmpty(shared.ExtraInHoleMs);
        Assert.All(shared.ExtraInHoleMs.Values, v => Assert.True(v > 0));
    }

    [Fact]
    public void LeadLength_TakesTheShortestThatReaches()
    {
        var (p, holes, ip) = Block();
        var c17 = p.Products.First(x => x.Kind == ProductKind.SurfaceConnector && x.DelayMs == 17);
        c17.LeadLengthM = 4.8;
        var longer = new Product { Name = "17 ms 9 m", Kind = ProductKind.SurfaceConnector, DelayMs = 17, LeadLengthM = 9 };
        p.Products.Add(longer);
        var o = Options(p, ip);
        o.Connectors.Add(longer);
        var plan = TieUpPlanner.Build(p, holes, o, TieScheme.Rows);
        // spacing 5 m: the 4.8 m lead does not reach, the 9 m one does
        Assert.All(plan.Ties.Where(t => t.Role == TieRole.InRow), t => Assert.Same(longer, t.Product));
        Assert.Equal(0, plan.ShortLeads);
    }

    /// <summary>
    /// Rows at 80° whatever the orientation of the blast, burden 8, spacing 10, the point at a corner: the ties go between
    /// neighbours (no tie across the block), the rows side by side from the point.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(90.0)]
    [InlineData(45.0)]
    public void RowsNotAlongTheOrientation_TieNeighboursOnly(double orientation)
    {
        var p = BlastProject.CreateNew();
        p.Header.BlastOrientation = orientation;
        p.Design.Burden = 8;
        p.Design.Spacing = 10;
        var th = 80 * Math.PI / 180;
        var along = new Vec3(Math.Cos(th), Math.Sin(th), 0);
        var across = new Vec3(Math.Sin(th), -Math.Cos(th), 0);
        var holes = new List<Hole>();
        for (int r = 0; r < 20; r++)
        {
            var row = new HoleRow { Name = $"Row {r + 1}" };
            for (int j = 0; j < 14; j++)
            {
                var h = p.Add(new Hole { Label = $"{r}-{j}", Collar = across * (r * 8.0) + along * (j * 10.0 + (r % 2) * 5), Length = 10 });
                holes.Add(h);
                row.HoleIds.Add(h.Id);
            }
            p.Rows.Add(row);
        }
        Charging.ApplyRules(p, holes);
        var ip = p.Add(new InitiationPoint { Position = new Vec3(-3, -6, 0) });
        var plans = TieUpPlanner.Plan(p, holes, Options(p, ip));
        foreach (var plan in plans.Where(x => x.Scheme != TieScheme.Fan))
            Assert.All(plan.Ties.Where(t => t.Role != TieRole.Lead), t => Assert.True(t.LengthM <= 12.5, $"{plan.Scheme}: tie {t.LengthM:0.0} m"));
        var fan = plans.Single(x => x.Scheme == TieScheme.Fan);
        Assert.All(fan.Ties.Where(t => t.Role != TieRole.Lead), t => Assert.True(t.LengthM <= 1.6 * 10 + 0.01, $"fan tie {t.LengthM:0.0} m"));
        Assert.True(fan.NetworkMs < 2000, $"fan network {fan.NetworkMs:0} ms");
    }

    /// <summary>
    /// A block 140 × 110 m with rows at a slant (35°), burden 8, spacing 10: the corner rows hold one or two holes; the
    /// point stands on the corner hole.
    /// </summary>
    internal static BlastProject SlantedBlock(out List<Hole> holes, out InitiationPoint ip)
    {
        var p = BlastProject.CreateNew();
        p.Design.Burden = 8;
        p.Design.Spacing = 10;
        var th = 35 * Math.PI / 180;
        var along = new Vec3(Math.Cos(th), Math.Sin(th), 0);
        var across = new Vec3(Math.Sin(th), -Math.Cos(th), 0);
        holes = new List<Hole>();
        for (int r = -30; r <= 30; r++)
        {
            var row = new HoleRow { Name = $"Row {r}" };
            for (int j = -30; j <= 30; j++)
            {
                var c = across * (r * 8.0) + along * (j * 10.0 + (Math.Abs(r) % 2) * 5);
                if (c.X < 0 || c.X > 140 || c.Y < 0 || c.Y > 110) continue;
                var h = p.Add(new Hole { Label = $"{r}/{j}", Collar = c, Length = 10 });
                holes.Add(h);
                row.HoleIds.Add(h.Id);
            }
            if (row.HoleIds.Count > 0) p.Rows.Add(row);
        }
        Charging.ApplyRules(p, holes);
        var corner = holes.OrderBy(h => h.Collar.X + h.Collar.Y).First();
        ip = p.Add(new InitiationPoint { Position = corner.Collar, HoleId = corner.Id });
        return p;
    }

    [Fact]
    public void SlantedRows_CornerPoint_VLayoutsTieAlongTheRowsAndTheFanFits()
    {
        var p = SlantedBlock(out var holes, out var ip);
        var plans = TieUpPlanner.Plan(p, holes, Options(p, ip));
        var longest = p.Rows.Max(r => r.HoleIds.Count);
        foreach (var plan in plans)
        {
            Assert.Equal(0, plan.Unreached);
            if (plan.Scheme is TieScheme.VFront or TieScheme.Diagonal or TieScheme.VDiagonal)
                Assert.True(plan.Ties.Count(t => t.Role == TieRole.InRow) >= longest / 2 - 1, $"{plan.Scheme}: the front row is not tied along");
        }
        var fan = plans.Single(x => x.Scheme == TieScheme.Fan);
        Assert.Equal(0, fan.LateHoles);
        Assert.False(plans.Single(x => x.Best).Risky && plans.Any(x => !x.Risky), "a risky variant marked best");
    }
}
