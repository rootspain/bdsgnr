using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;
using BlastDesigner.Rendering;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>
/// The font size of the hole labels with the plan's zoom (<see cref="PlanRenderer.HoleLabelSize"/>): by default it grows
/// and shrinks with the plan, too small it is not drawn; "Do not scale" keeps it; a printed sheet keeps it.
/// </summary>
public class HoleLabelScaleTests
{
    private static Camera Zoom(double pixelsPerMetre) => new() { Width = 400, Height = 300, Pitch = 90, Scale = pixelsPerMetre };

    [Fact]
    public void ByDefaultTheLabelsScaleWithThePlan()
    {
        var view = new ViewState { HoleLabelFontSize = 12 };
        Assert.False(view.HoleLabelsFixedSize);
        Assert.Equal(12f, PlanRenderer.HoleLabelSize(view, Zoom(ViewState.HoleLabelScaleAt)), 3);
        Assert.Equal(24f, PlanRenderer.HoleLabelSize(view, Zoom(ViewState.HoleLabelScaleAt * 2)), 3);
        Assert.Equal(6f, PlanRenderer.HoleLabelSize(view, Zoom(ViewState.HoleLabelScaleAt / 2)), 3);
        // far out: below the smallest size drawn
        Assert.True(PlanRenderer.HoleLabelSize(view, Zoom(2)) < PlanRenderer.MinScaledLabel);
        // close in: no larger than 200 px
        Assert.Equal(200f, PlanRenderer.HoleLabelSize(view, Zoom(5000)), 3);
    }

    [Fact]
    public void DoNotScaleKeepsTheFontSizeAtAnyZoom()
    {
        var view = new ViewState { HoleLabelFontSize = 14, HoleLabelsFixedSize = true };
        foreach (var zoom in new[] { 0.5, 2, 10, 80 })
            Assert.Equal(14f, PlanRenderer.HoleLabelSize(view, Zoom(zoom)), 3);
    }

    [Fact]
    public void APrintedSheetKeepsTheFontSize()
    {
        var view = new ViewState { HoleLabelFontSize = 12 };
        Assert.Equal(12f, PlanRenderer.HoleLabelSize(view, Zoom(1), print: true), 3);
    }

    [Fact]
    public void TheHeadsOfTheTieArrowsScaleHalfAsMuchAsTheLabels()
    {
        var view = new ViewState();
        Assert.Equal(1f, PlanRenderer.TieHeadScale(view, Zoom(ViewState.HoleLabelScaleAt)), 3);
        // the labels twice as large: the heads half as much again
        Assert.Equal(1.5f, PlanRenderer.TieHeadScale(view, Zoom(ViewState.HoleLabelScaleAt * 2)), 3);
        // the labels half as large: the heads three quarters
        Assert.Equal(0.75f, PlanRenderer.TieHeadScale(view, Zoom(ViewState.HoleLabelScaleAt / 2)), 3);
        // within 0.5 (far out they come to half their size) and 3
        Assert.Equal(0.5f, PlanRenderer.TieHeadScale(view, Zoom(0.01)), 2);
        Assert.Equal(3f, PlanRenderer.TieHeadScale(view, Zoom(1000)), 3);
        // Do not scale and a printed sheet keep them
        Assert.Equal(1f, PlanRenderer.TieHeadScale(new ViewState { HoleLabelsFixedSize = true }, Zoom(40)), 3);
        Assert.Equal(1f, PlanRenderer.TieHeadScale(view, Zoom(40), print: true), 3);
    }

    [Fact]
    public void TheSwitchIsKeptByTheFile()
    {
        var p = BlastProject.CreateNew();
        p.View.HoleLabelsFixedSize = true;
        Assert.True(ProjectJson.DeepClone(p).View.HoleLabelsFixedSize);
    }
}
