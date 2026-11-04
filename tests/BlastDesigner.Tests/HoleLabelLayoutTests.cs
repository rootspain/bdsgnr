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
