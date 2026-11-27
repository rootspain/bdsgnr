using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Rendering;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>
/// The kinds of the ties with the delays off (<see cref="PlanRenderer.TieKinds"/>, <see cref="PlanRenderer.TieHeads"/>):
/// one per product and delay, by the delay; 1 to 4 filled heads, then 1 to 4 hollow ones.
/// </summary>
[Collection("Sheets")]
public class TieKindTests
{
    [Fact]
    public void AKindIsAProductWithADelayInTheOrderOfTheDelays()
    {
        var p = BlastProject.CreateNew();
        var holes = Enumerable.Range(0, 8).Select(i => p.Add(new Hole { Collar = new Vec3(i * 5, 0, 100), Length = 10 })).ToList();
        var connectors = p.Products.Where(x => x.Kind == ProductKind.SurfaceConnector).Take(2).ToList();
        Assert.Equal(2, connectors.Count);
        void Tie(int from, Product? product, double delay = 0) =>
            p.Add(new TieEntity { FromId = holes[from].Id, ToId = holes[from + 1].Id, ProductId = product?.Id, DelayMs = delay });
        // "Tie 1" at its own delay twice, "Tie 1" at 60 ms, "Tie 2" at its own delay, a user delay of 5 ms
        Tie(0, connectors[0]);
        Tie(1, connectors[0]);
        Tie(2, connectors[0], 60);
        Tie(3, connectors[1]);
        Tie(4, null, 5);
        var kinds = PlanRenderer.TieKinds(p);
        Assert.Equal(4, kinds.Count);
        // by the delay
        Assert.Equal(kinds.Select(k => k.DelayMs).OrderBy(d => d), kinds.Select(k => k.DelayMs));
        Assert.Equal(2, kinds.Single(k => k.Product == connectors[0] && Math.Abs(k.DelayMs - connectors[0].DelayMs) < 0.5).Count);
        Assert.Single(kinds, k => k.Product == connectors[0] && k.DelayMs == 60);
        Assert.Single(kinds, k => k.Product == null && k.DelayMs == 5);
    }

    [Fact]
    public void FourFilledHeadsThenFourHollowOnes()
    {
        Assert.Equal((1, false), PlanRenderer.TieHeads(0));
        Assert.Equal((4, false), PlanRenderer.TieHeads(3));
        Assert.Equal((1, true), PlanRenderer.TieHeads(4));
        Assert.Equal((4, true), PlanRenderer.TieHeads(7));
        Assert.Equal((4, true), PlanRenderer.TieHeads(12));
    }

    [Fact]
    public void ThePrintedPlanWritesTheDelaysAsTheSettingsSay()
    {
        var p = BlastProject.CreateNew();
        var holes = Enumerable.Range(0, 6).Select(i => p.Add(new Hole { Collar = new Vec3(i * 5, 0, 100), Length = 10 })).ToList();
        var connector = p.Products.First(x => x.Kind == ProductKind.SurfaceConnector);
        for (int i = 0; i < 5; i++) p.Add(new TieEntity { FromId = holes[i].Id, ToId = holes[i + 1].Id, ProductId = connector.Id, DelayMs = i < 2 ? 0 : 60 });
        byte[] Sheet((bool, bool) delays)
        {
            Reports.TieDelays = delays;
            using var doc = Reports.BuildPlan(p, new PlanOptions { Title = "Plan" }, new ReportDesign(), new DateTime(2026, 11, 27));
            var page = doc.Pages.First(x => x.HasPlan);
            return Reports.PictureToPng(page.Picture, new SkiaSharp.SKRect(0, 0, page.Width, page.Height), 1);
        }
        try
        {
            var beside = Sheet((false, false));
            var onLine = Sheet((true, false));
            var off = Sheet((false, true));
            Assert.NotEqual(beside, onLine);
            Assert.NotEqual(beside, off);
            Assert.NotEqual(onLine, off);
        }
        finally { Reports.TieDelays = (false, false); }
    }

    [Fact]
    public void TheTiesOfElectronicDetonatorsMakeNoKind()
    {
        var p = BlastProject.CreateNew();
        var a = p.Add(new Hole { Collar = new Vec3(0, 0, 100), Length = 10 });
        var b = p.Add(new Hole { Collar = new Vec3(5, 0, 100), Length = 10 });
        var esi = p.Products.First(x => x.Kind == ProductKind.ElectronicDetonator);
        p.Add(new TieEntity { FromId = a.Id, ToId = b.Id, ProductId = esi.Id });
        Assert.Empty(PlanRenderer.TieKinds(p));
    }
}
