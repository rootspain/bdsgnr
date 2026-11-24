using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>
/// The stage buttons over the plan (Drilling/Loading · Plan/Fact): what switching them shows and hides, the layers bound
/// to them, their file, and the holes drawn from the design or from what was drilled.
/// </summary>
public class StageViewTests
{
    /// <summary>A blast with a layer for each button and one of nobody's.</summary>
    private static (BlastProject P, Layer DrillPlan, Layer DrillFact, Layer LoadPlan, Layer LoadFact, Layer Free) Blast()
    {
        var p = BlastProject.CreateNew();
        var dp = p.GetOrCreateLayer("Проект бурения");
        var df = p.GetOrCreateLayer("Съёмка устьев");
        var lp = p.GetOrCreateLayer("Проект заряжания");
        var lf = p.GetOrCreateLayer("Факт заряжания");
        var free = p.GetOrCreateLayer("Чужой");
        StageViews.Initial(p);
        return (p, dp, df, lp, lf, free);
    }

    private static StageView V(BlastProject p, WorkStage s, StageMode m) => StageViews.Get(p, s, m);

    [Fact]
    public void ANewBlastBindsTheLayersItHasByName()
    {
        var (p, dp, df, lp, lf, _) = Blast();
        Assert.Equal(4, p.StageViews.Count);
        Assert.Equal(new[] { dp.Id }, V(p, WorkStage.Drilling, StageMode.Plan).Layers);
        Assert.Equal(new[] { df.Id }, V(p, WorkStage.Drilling, StageMode.Fact).Layers);
        Assert.Equal(new[] { lp.Id }, V(p, WorkStage.Loading, StageMode.Plan).Layers);
        Assert.Equal(new[] { lf.Id }, V(p, WorkStage.Loading, StageMode.Fact).Layers);
        Assert.Equal(new[] { HoleState.Drilled, HoleState.Dipped }, V(p, WorkStage.Drilling, StageMode.Fact).States.Order());
        Assert.Equal(HoleBasis.Design, V(p, WorkStage.Drilling, StageMode.Plan).HoleBasis);
        Assert.Equal(HoleBasis.Actual, V(p, WorkStage.Loading, StageMode.Fact).HoleBasis);
        Assert.All(p.StageViews, v => Assert.False(v.On));
        // no layer is made: the standard blast has none of these names
        var plain = BlastProject.CreateNew();
        var layers = plain.Layers.Count;
        StageViews.Initial(plain);
        Assert.Equal(layers, plain.Layers.Count);
        Assert.All(plain.StageViews, v => Assert.Empty(v.Layers));
    }

    [Fact]
    public void SwitchingOnShowsItsLayersAndLeavesTheOthersAlone()
    {
        var (p, dp, df, _, _, free) = Blast();
        dp.Visible = false;
        free.Visible = false;
        df.Visible = true;
        StageViews.Switch(p, V(p, WorkStage.Drilling, StageMode.Plan), keepOthers: false);
        Assert.True(dp.Visible);
        // a layer of a button off and one of nobody's are not touched by switching on
        Assert.False(free.Visible);
        Assert.True(df.Visible);
    }

    [Fact]
    public void SwitchingOffDoesNotHideALayerOfAnotherButtonOn()
    {
        var (p, dp, _, lp, _, _) = Blast();
        var drillPlan = V(p, WorkStage.Drilling, StageMode.Plan);
        var loadPlan = V(p, WorkStage.Loading, StageMode.Plan);
        loadPlan.Layers.Add(dp.Id);
        StageViews.Switch(p, drillPlan, false);
        StageViews.Switch(p, loadPlan, keepOthers: true);
        Assert.True(drillPlan.On && loadPlan.On);
        StageViews.Switch(p, drillPlan, false);
        Assert.False(drillPlan.On);
        Assert.True(dp.Visible);
        Assert.True(lp.Visible);
        StageViews.Switch(p, loadPlan, false);
        Assert.False(dp.Visible);
        Assert.False(lp.Visible);
    }

    [Fact]
    public void ControlKeepsTheOtherStageAndPushesOutTheOtherViewOfTheSameStage()
    {
        var (p, dp, df, lp, _, _) = Blast();
        var drillPlan = V(p, WorkStage.Drilling, StageMode.Plan);
        var drillFact = V(p, WorkStage.Drilling, StageMode.Fact);
        var loadPlan = V(p, WorkStage.Loading, StageMode.Plan);
        StageViews.Switch(p, drillPlan, false);
        StageViews.Switch(p, loadPlan, true);
        Assert.True(drillPlan.On && loadPlan.On);
        StageViews.Switch(p, drillFact, true);
        Assert.True(drillFact.On && loadPlan.On);
        Assert.False(drillPlan.On);
        Assert.False(dp.Visible);
        Assert.True(df.Visible && lp.Visible);
        // a plain click leaves it alone
        StageViews.Switch(p, drillPlan, false);
        Assert.Equal(new[] { drillPlan }, StageViews.Enabled(p));
    }

    [Fact]
    public void TheStatesShownAreThoseOfTheButtonsOnAndTheHoleDataOfTheLastOne()
    {
        var (p, _, _, _, _, _) = Blast();
        p.View.HiddenStates.Add(HoleState.Fired);
        var drillFact = V(p, WorkStage.Drilling, StageMode.Fact);
        var loadFact = V(p, WorkStage.Loading, StageMode.Fact);
        StageViews.Switch(p, drillFact, false);
        StageViews.Switch(p, loadFact, true);
        var shown = Enum.GetValues<HoleState>().Where(s => !p.View.HiddenStates.Contains(s)).Order();
        Assert.Equal(new[] { HoleState.Drilled, HoleState.Dipped, HoleState.Loaded, HoleState.Fired }, shown);
        Assert.Equal(HoleBasis.Actual, p.View.HoleBasis);
        StageViews.Switch(p, V(p, WorkStage.Drilling, StageMode.Plan), false);
        Assert.Equal(HoleBasis.Design, p.View.HoleBasis);
        // all off: the states stay as they were
        var before = p.View.HiddenStates.ToHashSet();
        StageViews.Switch(p, V(p, WorkStage.Drilling, StageMode.Plan), false);
        Assert.Empty(StageViews.Enabled(p));
        Assert.Equal(before, p.View.HiddenStates);
        Assert.Equal(HoleBasis.Design, p.View.HoleBasis);
    }

    [Fact]
    public void ANewLayerGoesToEveryButtonOnAndToNoButtonOff()
    {
        var (p, _, _, _, _, _) = Blast();
        var doc = new Document(p);
        var drillFact = V(p, WorkStage.Drilling, StageMode.Fact);
        var loadFact = V(p, WorkStage.Loading, StageMode.Fact);
        StageViews.Switch(p, drillFact, false);
        StageViews.Switch(p, loadFact, true);
        Layer? made = null;
        doc.Execute("New layer", q => q.Layers.Add(made = new Layer { Name = "Съёмка 2" }));
        var views = StageViews.Of(doc.Project);
        Assert.All(views.Where(v => v.On), v => Assert.Contains(made!.Id, v.Layers));
        Assert.All(views.Where(v => !v.On), v => Assert.DoesNotContain(made!.Id, v.Layers));
        // none on: the layer is shared
        foreach (var v in views) v.On = false;
        doc.Execute("New layer", q => q.Layers.Add(made = new Layer { Name = "Общий" }));
        Assert.All(StageViews.Of(doc.Project), v => Assert.DoesNotContain(made!.Id, v.Layers));
        Assert.True(made!.Visible);
        // the name of a new layer while buttons are on
        Assert.Null(StageViews.NewLayerName(doc.Project, v => v.Name, new DateTime(2026, 11, 24)));
        StageViews.Switch(doc.Project, StageViews.Get(doc.Project, WorkStage.Drilling, StageMode.Fact), false);
        Assert.Equal("Drilling · Fact · 2026-11-24", StageViews.NewLayerName(doc.Project, v => v.Name, new DateTime(2026, 11, 24)));
    }

    [Fact]
    public void ADeletedLayerLeavesTheButtonsAndUndoBringsItBack()
    {
        var (p, dp, _, _, _, _) = Blast();
        var doc = new Document(p);
        var id = dp.Id;
        doc.Execute("Delete layer", q => q.Layers.RemoveAll(l => l.Id == id));
        Assert.DoesNotContain(id, StageViews.Get(doc.Project, WorkStage.Drilling, StageMode.Plan).Layers);
        Assert.Single(doc.UndoNames);
        doc.Undo();
        Assert.NotNull(doc.Project.FindLayer(id));
        Assert.Contains(id, StageViews.Get(doc.Project, WorkStage.Drilling, StageMode.Plan).Layers);
    }

    [Fact]
    public void AnOldFileReadsWithAllLayersSharedAndTheButtonsOff()
    {
        var p = BlastProject.CreateNew();
        var json = System.Text.Json.Nodes.JsonNode.Parse(ProjectJson.Serialize(p))!.AsObject();
        json.Remove("stageViews");
        json["view"]!.AsObject().Remove("holeBasis");
        var old = ProjectJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        var views = StageViews.Of(old);
        Assert.Equal(4, views.Count);
        Assert.All(views, v => { Assert.False(v.On); Assert.Empty(v.Layers); });
        Assert.Equal(HoleBasis.Design, old.View.HoleBasis);
        // a button of an old file with no states leaves the states as they are
        old.View.HiddenStates.Add(HoleState.Fired);
        StageViews.Switch(old, views[0], false);
        Assert.Equal(new[] { HoleState.Fired }, old.View.HiddenStates);
    }

    [Fact]
    public void TheBindingsAreSavedAndRead()
    {
        var (p, dp, df, _, _, free) = Blast();
        var drillFact = V(p, WorkStage.Drilling, StageMode.Fact);
        drillFact.Layers.Add(free.Id);
        drillFact.HoleBasis = HoleBasis.Design;
        StageViews.Switch(p, drillFact, false);
        var path = Path.Combine(Path.GetTempPath(), $"stages-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(p, path, 0);
            var back = ProjectFile.Load(path);
            var read = StageViews.Get(back, WorkStage.Drilling, StageMode.Fact);
            Assert.True(read.On);
            Assert.Equal(new[] { df.Id, free.Id }.Order(), read.Layers.Order());
            Assert.Equal(HoleBasis.Design, read.HoleBasis);
            Assert.Equal(new[] { HoleState.Drilled, HoleState.Dipped }, read.States.Order());
            Assert.Equal(new[] { dp.Id }, StageViews.Get(back, WorkStage.Drilling, StageMode.Plan).Layers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void APrintOfAButtonTakesItsViewAndLeavesTheBlastAsItIs()
    {
        var (p, dp, df, _, _, free) = Blast();
        StageViews.Switch(p, V(p, WorkStage.Drilling, StageMode.Plan), false);
        var print = StageViews.ForPrint(p, V(p, WorkStage.Drilling, StageMode.Fact));
        Assert.True(print.FindLayer(df.Id)!.Visible);
        Assert.False(print.FindLayer(dp.Id)!.Visible);
        Assert.True(print.FindLayer(free.Id)!.Visible);
        Assert.Equal(HoleBasis.Actual, print.View.HoleBasis);
        Assert.Contains(HoleState.Designed, print.View.HiddenStates);
        // the blast on the screen is not changed
        Assert.True(dp.Visible);
        Assert.Equal(HoleBasis.Design, p.View.HoleBasis);
        Assert.True(V(p, WorkStage.Drilling, StageMode.Plan).On);
    }

    // ---- the holes drawn from the design or from what was drilled ----

    private static Hole Drilled() => new()
    {
        Collar = new Vec3(10, 20, 100),
        Length = 12,
        ActualCollar = new Vec3(10.6, 20.4, 100.1),
        ActualLength = 11
    };

    [Fact]
    public void ADesignHoleIsDrawnAtItsCollarAndAnActualOneAtItsSurveyedCollar()
    {
        var h = Drilled();
        Assert.Equal(h.Collar, h.DrawnCollar(HoleBasis.Design));
        Assert.Equal(h.ActualCollar!.Value, h.DrawnCollar(HoleBasis.Actual));
        Assert.Equal(h.DesignToe, h.DrawnToe(HoleBasis.Design));
        Assert.Equal(12, h.DrawnLength(HoleBasis.Design));
        Assert.Equal(11, h.DrawnLength(HoleBasis.Actual));
        Assert.Equal(89.1, h.DrawnToe(HoleBasis.Actual).Z, 6);
        // a hole with no fact is drawn as designed
        var plain = new Hole { Collar = new Vec3(1, 2, 3), Length = 8 };
        Assert.Equal(plain.Collar, plain.DrawnCollar(HoleBasis.Actual));
        Assert.Equal(plain.DesignToe, plain.DrawnToe(HoleBasis.Actual));
        // the surveyed track, down to the dipped depth
        var tracked = new Hole { Collar = new Vec3(0, 0, 10), Length = 10, ActualLength = 5, BoreTrack = new() { new Vec3(0, 0, 10), new Vec3(0, 0, 6), new Vec3(1, 0, 0) } };
        var track = tracked.DrawnTrack(HoleBasis.Actual);
        Assert.Equal(3, track.Count);
        Assert.Equal(new Vec3(0, 0, 6), track[1]);
        Assert.Equal(5, Vec3.Distance(track[0], track[1]) + Vec3.Distance(track[1], track[2]), 6);
        Assert.Equal(2, tracked.DrawnTrack(HoleBasis.Design).Count);
    }

    [Fact]
    public void ASectionFindsTheHoleWhereItIsDrawn()
    {
        var p = BlastProject.CreateNew();
        var h = p.Add(new Hole { Label = "1", Collar = new Vec3(0, 0, 100), Length = 10, ActualCollar = new Vec3(0, 5, 100) });
        // a line along y = 5, half a metre wide: the surveyed collar is on it, the design one is not
        Assert.Empty(SectionProfile.Build(p, new Vec3(-10, 5, 0), new Vec3(10, 5, 0), 0.5).Holes);
        p.View.HoleBasis = HoleBasis.Actual;
        var found = SectionProfile.Build(p, new Vec3(-10, 5, 0), new Vec3(10, 5, 0), 0.5).Holes;
        Assert.Single(found);
        Assert.Equal(h.ActualCollar!.Value, found[0].Collar);
    }
}
