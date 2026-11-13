using System.Text.Json;
using BlastDesigner.Domain;
using BlastDesigner.IO;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>How selected objects look is a program setting: 2 px thicker and a 2 px halo unless switched off.</summary>
public class SelectionLookSettingsTests
{
    [Fact]
    public void AnOldSettingsFileKeepsTheSelectionThickAndHaloed()
    {
        var s = JsonSerializer.Deserialize<ProgramSettings>("{}", ProjectJson.Options)!;
        Assert.True(s.SelectionThick);
        Assert.Equal(2, s.SelectionHaloPx);
    }

    [Fact]
    public void TheChoicesAreSavedAndReadBack()
    {
        var s = new ProgramSettings { SelectionThick = false, SelectionHaloPx = 1 };
        var back = JsonSerializer.Deserialize<ProgramSettings>(JsonSerializer.Serialize(s, ProjectJson.Indented), ProjectJson.Options)!;
        Assert.False(back.SelectionThick);
        Assert.Equal(1, back.SelectionHaloPx);
    }
}
