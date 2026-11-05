using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Tests;

public class HoleLabelLayoutTests
{
    [Fact]
    public void Sides_PutEachLabelOnceAndMissingOnesOnTheirDefaultSide()
    {
        // a duplicate, a label on both sides and labels on neither (a file from before the layout lists none)
        var v = new ViewState { LabelsLeft = new() { HoleLabel.Number, HoleLabel.Number }, LabelsRight = new() { HoleLabel.Number, HoleLabel.Length } };
        var (left, right) = HoleLabelLayout.Sides(v);
        Assert.Equal(HoleLabel.Number, left[0]);
        Assert.Equal(1, left.Concat(right).Count(l => l == HoleLabel.Number));
        Assert.Equal(HoleLabel.Length, right[0]);
        Assert.Equal(Enum.GetValues<HoleLabel>().Length, left.Count + right.Count);
        Assert.Contains(HoleLabel.HoleTime, left);
        Assert.Contains(HoleLabel.Diameter, right);
    }

    [Fact]
    public void Move_ChangesSideAndOrder()
    {
        var v = new ViewState();
        HoleLabelLayout.Move(v, HoleLabel.Number, right: false, 0);
        Assert.Equal(HoleLabel.Number, v.LabelsLeft[0]);
        Assert.DoesNotContain(HoleLabel.Number, v.LabelsRight);
        // within a side: the index is counted without the label itself
        HoleLabelLayout.Move(v, HoleLabel.Number, right: false, 1);
        Assert.Equal(new[] { HoleLabelLayout.DefaultLeft[0], HoleLabel.Number }, v.LabelsLeft.Take(2));
        HoleLabelLayout.Move(v, HoleLabel.Number, right: true, int.MaxValue);
        Assert.Equal(HoleLabel.Number, v.LabelsRight[^1]);
        HoleLabelLayout.Reset(v);
        Assert.Equal(HoleLabelLayout.DefaultRight, v.LabelsRight);
    }

    [Fact]
    public void Layout_IsSavedWithTheProject()
    {
        var p = new BlastProject();
        HoleLabelLayout.Move(p.View, HoleLabel.HoleTime, right: true, 0);
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(p));
        Assert.Equal(HoleLabel.HoleTime, back.View.LabelsRight[0]);
        Assert.DoesNotContain(HoleLabel.HoleTime, back.View.LabelsLeft);
    }

    [Fact]
    public void IsShown_FollowsTheLabelSwitches()
    {
        var v = new ViewState { HoleLabels = HoleLabelMode.Length, ShowHoleSequenceLabel = false, TimingLabels = TimingLabelMode.InholeTime };
        Assert.True(HoleLabelLayout.IsShown(v, HoleLabel.Length));
        Assert.False(HoleLabelLayout.IsShown(v, HoleLabel.Number));
        Assert.True(HoleLabelLayout.IsShown(v, HoleLabel.HoleTime));
        Assert.False(HoleLabelLayout.IsShown(v, HoleLabel.HoleOrder));
    }
}

public class NaturalOrderTests
{
    [Fact]
    public void Numbers_GoByValue_AlsoInsideText()
    {
        Assert.Equal(new[] { "1", "2", "10", "11", "20" }, new[] { "1", "10", "11", "2", "20" }.OrderBy(s => s, NaturalOrder.Instance));
        Assert.Equal(new[] { "A2", "A10", "B1" }, new[] { "A10", "B1", "A2" }.OrderBy(s => s, NaturalOrder.Instance));
        // a number with letters among plain numbers: after them, its own number counts among its likes
        Assert.Equal(new[] { "1", "3", "10", "A-2", "A-10" }, new[] { "A-10", "10", "A-2", "3", "1" }.OrderBy(s => s, NaturalOrder.Instance));
        Assert.True(NaturalOrder.Instance.Compare("1E2", "3") < 0);
        Assert.True(NaturalOrder.Instance.Compare("12.10", "12.5") < 0);
        Assert.Equal(0, NaturalOrder.Instance.Compare("a7", "A07"));
        Assert.True(NaturalOrder.Instance.Compare("", "1") < 0);
    }
}

public class MinStemmingTests
{
    [Fact]
    public void MinStemming_IsSavedWithTheProjectAndTheRule()
    {
        var p = new BlastProject { MinStemmingDiameters = 18 };
        p.LoadingRules.Add(new LoadingRule { Name = "r", MinStemmingDiameters = 22 });
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(p));
        Assert.Equal(18, back.MinStemmingDiameters);
        Assert.Equal(22, back.LoadingRules[0].MinStemmingDiameters);
        // a file from before it has none: the program settings give it
        Assert.Null(ProjectJson.Deserialize(ProjectJson.Serialize(new BlastProject())).MinStemmingDiameters);
    }
}
