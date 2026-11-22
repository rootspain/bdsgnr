using System.Text.Json;
using BlastDesigner.Domain;
using BlastDesigner.IO;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>The fonts of the ribbon, the tabs, the right panel and the tooltips, and the ribbon spacing: program settings.</summary>
public class UiFontSettingsTests
{
    [Fact]
    public void AnOldSettingsFile_KeepsTheInterfaceAsItWas()
    {
        var s = JsonSerializer.Deserialize<ProgramSettings>("{}", ProjectJson.Options)!;
        s.Upgrade();
        Assert.Equal((12.0, 4.0, 1.0), (s.RibbonFontSize, s.RibbonButtonPadding, s.RibbonButtonGap));
        Assert.Equal((13.0, 12.0, 12.0), (s.TabFontSize, s.PanelFontSize, s.ToolTipFontSize));
        // the tooltip colours follow the theme
        Assert.Equal((0u, 0u), (s.ToolTipBackground, s.ToolTipForeground));
    }

    [Fact]
    public void TheChoicesAreSavedAndReadBack()
    {
        var s = new ProgramSettings
        {
            RibbonFontSize = 14, RibbonButtonPadding = 6, RibbonButtonGap = 3, TabFontSize = 15, PanelFontSize = 13.5,
            ToolTipFontSize = 16, ToolTipBackground = 0xFFFFF7D6, ToolTipForeground = 0xFF1B1F24
        };
        var back = JsonSerializer.Deserialize<ProgramSettings>(JsonSerializer.Serialize(s, ProjectJson.Indented), ProjectJson.Options)!;
        back.Upgrade();
        Assert.Equal((14.0, 6.0, 3.0, 15.0, 13.5, 16.0), (back.RibbonFontSize, back.RibbonButtonPadding, back.RibbonButtonGap, back.TabFontSize, back.PanelFontSize, back.ToolTipFontSize));
        Assert.Equal((0xFFFFF7D6u, 0xFF1B1F24u), (back.ToolTipBackground, back.ToolTipForeground));
    }

    [Fact]
    public void SizesOutOfTheirRange_AreBroughtIn()
    {
        // a settings file edited by hand cannot make the interface unusable
        var s = JsonSerializer.Deserialize<ProgramSettings>(
            """{"ribbonFontSize":2,"ribbonButtonPadding":-5,"ribbonButtonGap":40,"tabFontSize":99,"panelFontSize":0,"toolTipFontSize":50}""", ProjectJson.Options)!;
        s.Upgrade();
        Assert.Equal((ProgramSettings.MinFont, 0.0, ProgramSettings.MaxRibbonSpacing), (s.RibbonFontSize, s.RibbonButtonPadding, s.RibbonButtonGap));
        Assert.Equal((ProgramSettings.MaxTabFont, ProgramSettings.MinFont, ProgramSettings.MaxToolTipFont), (s.TabFontSize, s.PanelFontSize, s.ToolTipFontSize));
    }
}
