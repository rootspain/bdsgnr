using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.Tests;

/// <summary>The outline round a label: black round light text, white round dark, by the contrast of the colours.</summary>
public class LabelOutlineTests
{
    [Theory]
    // light pinks and violets, low in green: black, though the old threshold gave them white
    [InlineData(0xFFD946EFu, true)]
    [InlineData(0xFFF472B6u, true)]
    [InlineData(0xFFE879F9u, true)]
    [InlineData(0xFFFFFFFFu, true)]
    [InlineData(0xFF60A5FAu, true)]
    // dark ones: white
    [InlineData(0xFF1D4ED8u, false)]
    [InlineData(0xFF7C3AEDu, false)]
    [InlineData(0xFF000000u, false)]
    public void LightTextGetsABlackOutline(uint colour, bool black) => Assert.Equal(black, TextBatch.DarkOutline(new SKColor(colour)));
}
