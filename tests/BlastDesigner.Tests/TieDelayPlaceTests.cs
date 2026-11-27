using BlastDesigner.Rendering;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>
/// Where the delay of a tie is written (<see cref="PlanRenderer.TieDelayAt"/>): beside the line by default, clear of it
/// whatever its direction; on the middle of the line when the setting asks for it.
/// </summary>
public class TieDelayPlaceTests
{
    private const float Size = 12;

    /// <summary>The box of the number at (x, y) of its baseline: left, top, right, bottom.</summary>
    private static (float L, float T, float R, float B) Box(string delay, (float X, float Y) at) =>
        (at.X, at.Y - Size * 0.72f, at.X + PlanRenderer.TieDelayWidth(delay, Size), at.Y);

    /// <summary>Whether the segment through the middle (100, 100) along (dx, dy), 60 px each way, crosses the box.</summary>
    private static bool Crosses((float L, float T, float R, float B) b, float dx, float dy)
    {
        var len = MathF.Sqrt(dx * dx + dy * dy);
        for (var s = -60f; s <= 60; s += 0.25f)
        {
            float x = 100 + dx / len * s, y = 100 + dy / len * s;
            if (x >= b.L && x <= b.R && y >= b.T && y <= b.B) return true;
        }
        return false;
    }

    [Theory]
    [InlineData(1, 0)]     // to the right
    [InlineData(-1, 0)]    // to the left
    [InlineData(0, 1)]     // down
    [InlineData(0, -1)]    // up
    [InlineData(1, -1.3f)] // up and right, as from 96 to 90
    [InlineData(1, 1)]     // down and right
    [InlineData(-0.4f, -1)]
    public void BesideTheLineTheNumberIsClearOfItWhicheverWayTheTieGoes(float dx, float dy)
    {
        foreach (var delay in new[] { "9", "17", "142" })
        {
            var at = PlanRenderer.TieDelayAt(delay, Size, 100, 100, dx, dy, onLine: false);
            Assert.False(Crosses(Box(delay, at), dx, dy), $"{delay} on the tie ({dx}, {dy})");
            // near the middle all the same: within the size of the number and a gap
            var b = Box(delay, at);
            var cx = (b.L + b.R) / 2; var cy = (b.T + b.B) / 2;
            Assert.True(MathF.Sqrt((cx - 100) * (cx - 100) + (cy - 100) * (cy - 100)) < PlanRenderer.TieDelayWidth(delay, Size) / 2 + Size, $"{delay} far from the middle");
        }
    }

    [Fact]
    public void BesideAHorizontalTieTheNumberIsAboveItAndBesideAnUprightOneOnTheRight()
    {
        var above = Box("17", PlanRenderer.TieDelayAt("17", Size, 100, 100, 1, 0, false));
        Assert.True(above.B < 100);
        var right = Box("17", PlanRenderer.TieDelayAt("17", Size, 100, 100, 0, -1, false));
        Assert.True(right.L > 100);
    }

    [Fact]
    public void OnTheLineTheNumberIsCentredOnTheMiddle()
    {
        var b = Box("17", PlanRenderer.TieDelayAt("17", Size, 100, 100, 1, -1, true));
        Assert.Equal(100, (b.L + b.R) / 2, 3);
        Assert.Equal(100, (b.T + b.B) / 2, 3);
    }
}
