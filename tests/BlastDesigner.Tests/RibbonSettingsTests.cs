using System.Text.Json;
using BlastDesigner.Domain;
using BlastDesigner.IO;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>The ribbon group names are under the buttons again, with no setting for them.</summary>
public class RibbonSettingsTests
{
    [Fact]
    public void ASettingsFileWithTheGroupNamesAtTheSideStillOpens()
    {
        // written by the 0.3.7 trial, which had the setting: the names as the program writes them
        var saved = JsonSerializer.Serialize(new ProgramSettings { ShowCoordinatesBox = false }, ProjectJson.Indented);
        var aside = ProjectJson.Options.PropertyNamingPolicy?.ConvertName("RibbonCaptionsAside") ?? "RibbonCaptionsAside";
        var s = JsonSerializer.Deserialize<ProgramSettings>("{\"" + aside + "\": true," + saved.TrimStart().TrimStart('{'), ProjectJson.Options)!;
        Assert.False(s.ShowCoordinatesBox);
        Assert.DoesNotContain(aside, JsonSerializer.Serialize(s, ProjectJson.Indented));
    }
}
