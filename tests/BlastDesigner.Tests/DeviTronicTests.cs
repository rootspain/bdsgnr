using System.Text;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.IO;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>DaveyTronic BPD export checked against the reference file made by O-Pitblast (CRC 52627).</summary>
public class DeviTronicTests
{
    static BpdPlan ReferencePlan()
    {
        var path = new BpdPath { Id = "A", PuSerialNumber = "602", West = 20, East = 83, South = 54, North = 54 };
        for (int i = 1; i <= 10; i++)
            path.Holes.Add(new BpdHole($"A{i:0000}", new List<BpdDetonator> { new(i, 1, i * 1000) }));
        return new BpdPlan
        {
            Created = new DateTime(2020, 9, 24, 17, 5, 20),
            FullName = "Trial___",
            DisplayedName = "TRIAL___",
            SourceFile = "O-Pitblast 1.5.47.0",
            UtmZoneLetter = "N",
            UtmZoneNumber = 29,
            Paths = { path }
        };
    }

    [Fact]
    public void Crc16X25_CheckValue() => Assert.Equal(0x906E, DeviTronic.Crc16X25(Encoding.ASCII.GetBytes("123456789")));

    [Fact]
    public void Write_ReproducesReferenceFile()
    {
        var text = DeviTronic.Write(ReferencePlan());
        Assert.StartsWith("<PU_AUTO_BLASTPLAN>\r\n<CRC>52627</CRC>\r\n<DATA>\r\n    <HEADER>\r\n      <DATE>2020/09/24</DATE>\r\n", text);
        Assert.EndsWith("      </HOLE>\r\n    </PATH>\r\n</DATA>\r\n</PU_AUTO_BLASTPLAN>\n", text);
        Assert.Equal(3386, Encoding.ASCII.GetByteCount(text));
        Assert.True(DeviTronic.CheckCrc(text));
        Assert.False(DeviTronic.CheckCrc(text.Replace("<DELAY>5000</DELAY>", "<DELAY>5001</DELAY>")));
    }

    [Fact]
    public void SanitizeName_PadsAndCutsToEight()
    {
        Assert.Equal("Trial___", DeviTronic.SanitizeName("Trial"));
        Assert.Equal("Blast12_", DeviTronic.SanitizeName("Blast 12"));
        Assert.Equal("ABCDEFGH", DeviTronic.SanitizeName("ABCDEFGHIJ"));
        Assert.Equal("BLAST___", DeviTronic.SanitizeName("Взрыв"));
    }

    [Fact]
    public void Build_MakesOnePathPerChain()
    {
        var p = BlastProject.CreateNew();
        var edet = p.Products.First(x => x.Kind == ProductKind.ElectronicDetonator);
        var c17 = p.Products.First(x => x.Kind == ProductKind.SurfaceConnector && x.DelayMs == 17);
        var c42 = p.Products.First(x => x.Kind == ProductKind.SurfaceConnector && x.DelayMs == 42);
        Hole H(string label, double x, double y, double t, params double[] primerDepths)
        {
            var h = new Hole { Label = label, Collar = new Vec3(x, y, 100), Length = 10, ElectronicTimeMs = t };
            h.Decks.Add(new Deck { Top = 3, Bottom = 10, Primers = primerDepths.Select(d => new Primer { Depth = d, DetonatorId = edet.Id }).ToList() });
            p.Entities.Add(h);
            return h;
        }
        var a1 = H("1", 10.2, 20.7, 100, 9.5);
        var a2 = H("2", 14.6, 20.1, 117.4, 9.5, 5);
        var b1 = H("3", 10, 30, 200, 9.5);
        var b2 = H("4", 15, 30.5, 242, 9.5);
        p.Entities.Add(new TieEntity { FromId = a1.Id, ToId = a2.Id, ProductId = c17.Id });
        p.Entities.Add(new TieEntity { FromId = b1.Id, ToId = b2.Id, ProductId = c42.Id });
        p.Coordinates.UtmZone = 29;
        p.Coordinates.OffsetEasting = 500000;
        p.Coordinates.OffsetNorthing = 4000000;

        var chains = DeviTronic.Chains(p);
        Assert.Equal(new[] { c17.Id, c42.Id }, chains.Select(c => c.TieTypeKey));
        Assert.Equal(("1", "2"), (chains[0].FirstHole, chains[0].LastHole));
        var r = DeviTronic.Build(p, p.Holes, new[] { new BpdPathRequest(chains[1].Key, "700"), new BpdPathRequest(chains[0].Key, "602") }, BpdDelayUnit.TenthMs, "Blast Designer 6", new DateTime(2026, 11, 9));
        Assert.Empty(r.Errors);
        var a = r.Plan.Paths[0];
        var b = r.Plan.Paths[1];
        Assert.Equal(("A", "700"), (a.Id, a.PuSerialNumber));
        Assert.Equal(new[] { "A0001", "A0002" }, a.Holes.Select(h => h.HoleId));
        Assert.Equal(new long[] { 2000, 2420 }, a.Holes.SelectMany(h => h.Detonators).Select(d => d.Delay));
        Assert.Equal("B", b.Id);
        // two primers in hole 2: the deeper one is level 1, numbering runs through the line
        Assert.Equal(new[] { (1, 1, 1000L), (2, 1, 1174L), (3, 2, 1174L) }, b.Holes.SelectMany(h => h.Detonators).Select(d => (d.OrderNumber, d.Level, d.Delay)));
        Assert.Equal((500010L, 500015L, 4000020L, 4000021L), (b.West, b.East, b.South, b.North));
        // LEVEL 1 = top detonator: in hole 2 the upper primer (depth 5) comes first
        var top = DeviTronic.Build(p, p.Holes, new[] { new BpdPathRequest(chains[0].Key, "602") }, BpdDelayUnit.Ms, "t", levels: BpdLevelOrder.TopFirst);
        Assert.Equal(new[] { (1, 1), (2, 1), (3, 2) }, top.Plan.Paths[0].Holes.SelectMany(h => h.Detonators).Select(d => (d.OrderNumber, d.Level)));
        Assert.Equal(new[] { 5.0, 9.5 }, top.Plan.Paths[0].Holes[1].Source!.Decks[0].Primers.Select(x => x.Depth).OrderBy(x => x));
        var text = DeviTronic.Write(r.Plan);
        Assert.Contains("<TOTAL_DETS_QUANTITY>5</TOTAL_DETS_QUANTITY>", text);
        Assert.Contains("<PATH_QUANTITY>2</PATH_QUANTITY>", text);
        Assert.True(DeviTronic.CheckCrc(text));
    }

    [Fact]
    public void OrderAlongTies_FollowsTiesInTheOrderTheyWereDrawn()
    {
        var p = BlastProject.CreateNew();
        var ip = new InitiationPoint { Position = new Vec3(0, 0, 100) };
        p.Entities.Add(ip);
        Hole H(string label) { var h = new Hole { Label = label, Collar = new Vec3(0, 0, 100) }; p.Entities.Add(h); return h; }
        var h1 = H("1"); var h2 = H("2"); var h3 = H("3"); var h4 = H("4"); var h5 = H("5"); var h9 = H("9");
        void Tie(Entity a, Entity b) => p.Entities.Add(new TieEntity { FromId = a.Id, ToId = b.Id, ProductId = "w" });
        // point → 5, then from 5 the branch to 9 was drawn before the branch to 1; 9 → 3 runs through hole 4 (not written)
        Tie(ip, h5); Tie(h5, h9); Tie(h5, h1); Tie(h9, h4); Tie(h4, h3); Tie(h1, h2);
        var order = DeviTronic.OrderAlongTies(p.Ties.ToList(), new List<Hole> { h1, h2, h3, h5, h9 });
        Assert.Equal(new[] { "5", "9", "3", "1", "2" }, order.Select(h => h.Label));
    }

    [Fact]
    public void Build_WarnsAboutMoreThan800TiesOfAType()
    {
        var p = BlastProject.CreateNew();
        var edet = p.Products.First(x => x.Kind == ProductKind.ElectronicDetonator);
        Hole prev = null!;
        for (int i = 0; i <= 801; i++)
        {
            var h = new Hole { Label = i.ToString(), Collar = new Vec3(i, 0, 100), ElectronicTimeMs = i };
            h.Decks.Add(new Deck { Top = 3, Bottom = 10, Primers = { new Primer { Depth = 9, DetonatorId = edet.Id } } });
            p.Entities.Add(h);
            if (i > 0) p.Entities.Add(new TieEntity { FromId = prev.Id, ToId = h.Id, ProductId = "w" });
            prev = h;
        }
        var chain = Assert.Single(DeviTronic.Chains(p));
        Assert.Equal(801, chain.TieCount);
        var r = DeviTronic.Build(p, p.Holes, new[] { new BpdPathRequest(chain.Key, "1") }, BpdDelayUnit.Ms, "t");
        Assert.Empty(r.Errors);
        Assert.Contains(r.Warnings, w => w.Format.Contains("ties, more than"));
        Assert.Equal(802, r.Plan.Paths[0].DetonatorCount);
    }

    [Fact]
    public void Chains_SplitOneTieTypeIntoSeparateChains()
    {
        var p = BlastProject.CreateNew();
        Hole H(string label) { var h = new Hole { Label = label }; p.Entities.Add(h); return h; }
        var h1 = H("1"); var h2 = H("2"); var h3 = H("3"); var h4 = H("4"); var h5 = H("5");
        void Tie(Hole a, Hole b, string type) => p.Entities.Add(new TieEntity { FromId = a.Id, ToId = b.Id, ProductId = type });
        Tie(h1, h2, "w"); Tie(h3, h4, "w");   // the same type, not touching: two chains
        Tie(h2, h5, "x");                     // another type from hole 2: a chain of its own
        Tie(h4, h5, "w");                     // continues the second w chain
        var chains = DeviTronic.Chains(p);
        Assert.Equal(new[] { ("w", "1", "2", 1), ("w", "3", "5", 2), ("x", "2", "5", 1) },
            chains.Select(c => (c.TieTypeKey, c.FirstHole, c.LastHole, c.TieCount)));
        var map = DeviTronic.HoleChains(p, chains);
        Assert.Equal(chains[0].Key, map[h2.Id]);   // hole 2: the chain of the tie coming in
        Assert.Equal(chains[2].Key, map[h5.Id]);   // hole 5: the first tie drawn into it is 2 → 5 (type x)
    }

    [Fact]
    public void Build_RejectsTheSameChainTwice()
    {
        var p = BlastProject.CreateNew();
        var r = DeviTronic.Build(p, p.Holes, new[] { new BpdPathRequest("chain:x", "1"), new BpdPathRequest("chain:x", "2") }, BpdDelayUnit.Ms, "t");
        Assert.NotEmpty(r.Errors);
    }
}
