using System.Text;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>
/// The shifts of the frame of a section are kept by the section (<see cref="BlastProject.Sections"/>), not by its section
/// line: written with the project, never on the line; a file of before them gives the line's shifts to its section.
/// </summary>
public class SectionSettingsTests
{
    private static (BlastProject P, PolylineEntity Line) Blast()
    {
        var p = BlastProject.CreateNew();
        var line = p.Add(new PolylineEntity { Kind = LineKind.CrossSection, Label = "A", Points = { new Vec3(0, 0, 100), new Vec3(50, 0, 100) } });
        return (p, line);
    }

    [Fact]
    public void TheSectionKeepsTheShiftsOfItsFrameAndTheLineDoesNot()
    {
        var (p, line) = Blast();
        Assert.Null(p.SectionOf(line.Id));
        p.SetSectionFrame(line.Id, 2.5, -1.5);
        p.SetSectionFrame(line.Id, 3, -2);
        var kept = Assert.Single(p.Sections);
        Assert.Equal((3.0, -2.0), (kept.FrameTopShift, kept.FrameBottomShift));
        var json = Encoding.UTF8.GetString(ProjectJson.Serialize(p));
        Assert.DoesNotContain("sectionFrameTopShift", json);
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(p));
        Assert.Equal((3.0, -2.0), (back.SectionOf(line.Id)!.FrameTopShift, back.SectionOf(line.Id)!.FrameBottomShift));
    }

    [Fact]
    public void AFileOfBeforeGivesTheShiftsOfItsSectionLinesToTheirSections()
    {
        var (p, line) = Blast();
        // as a file of before wrote them: on the section line
        var json = Encoding.UTF8.GetString(ProjectJson.Serialize(p));
        var key = "\"id\":\"" + line.Id + "\"";
        Assert.Contains(key, json);
        var old = json.Replace(key, key + ",\"sectionFrameTopShift\":4.5,\"sectionFrameBottomShift\":-0.5");
        var opened = ProjectJson.Deserialize(Encoding.UTF8.GetBytes(old));
        opened.TakeLegacySectionFrames();
        var kept = opened.SectionOf(line.Id);
        Assert.NotNull(kept);
        Assert.Equal((4.5, -0.5), (kept!.FrameTopShift, kept.FrameBottomShift));
        // saved again, the line carries them no more
        Assert.DoesNotContain("sectionFrameTopShift", Encoding.UTF8.GetString(ProjectJson.Serialize(opened)));
    }
}
