using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.Rendering;

namespace BlastDesigner.Tests;

/// <summary>
/// Names of the section lines: a Latin letter in turn, the first free one; the section called A–A; no two lines with one
/// name; lines from before the names take letters in their order.
/// </summary>
/// <remarks>In the "Sheets" collection: the sheets read <see cref="Reports.TieDelays"/>, which a test sets.</remarks>
[Collection("Sheets")]
public class SectionNamesTests
{
    static PolylineEntity Section(double y) => new() { Kind = LineKind.CrossSection, Points = { new Vec3(0, y, 100), new Vec3(50, y, 100) } };

    static PolylineEntity Draw(Document doc, double y)
    {
        var line = Section(y);
        doc.Execute("Add section line", p => p.Add(line));
        return (PolylineEntity)doc.Project.FindEntity(line.Id)!;
    }

    [Fact]
    public void Names_AreLettersInTurn_ThenWithANumber_AndTheSectionIsCalledAA()
    {
        Assert.Equal(new[] { "A", "B", "Z", "A1", "B1", "Z1", "A2" }, new[] { 0, 1, 25, 26, 27, 51, 52 }.Select(SectionNames.Name));
        Assert.Equal("A–A", SectionNames.Caption("A"));
        Assert.Equal("", SectionNames.Caption(" "));
    }

    [Fact]
    public void ALineDrawn_TakesTheFirstFreeLetter_AGapLeftByADeletedOneToo()
    {
        var doc = new Document(BlastProject.CreateNew());
        var a = Draw(doc, 0); var b = Draw(doc, 5); var c = Draw(doc, 10);
        Assert.Equal(new[] { "A", "B", "C" }, new[] { a, b, c }.Select(l => l.Label));
        doc.Execute("Delete", p => p.Entities.RemoveAll(e => e.Id == b.Id));
        Assert.Equal("B", Draw(doc, 15).Label);
        // one undo takes the line and its name
        doc.Undo();
        Assert.Equal(new[] { "A", "C" }, SectionNames.Lines(doc.Project).Select(l => l.Label));
    }

    [Fact]
    public void AName_IsNeverOnTwoLines_ACopyTakesTheNext_AClearedNameTheFirstFree()
    {
        var doc = new Document(BlastProject.CreateNew());
        var a = Draw(doc, 0); var b = Draw(doc, 5);
        Assert.True(SectionNames.Taken(doc.Project, b.Id, "a"));
        Assert.False(SectionNames.Taken(doc.Project, a.Id, "A"));
        // a copy pasted with the name of its original: the original keeps it, the copy takes the next
        var copy = Section(10);
        copy.Label = "A";
        doc.Execute("Paste", p => p.Add(copy));
        Assert.Equal("A", ((PolylineEntity)doc.Project.FindEntity(a.Id)!).Label);
        Assert.Equal("C", ((PolylineEntity)doc.Project.FindEntity(copy.Id)!).Label);
        // a name of the user's stays; cleared, the line takes the first free letter
        doc.Execute("Rename", p => ((PolylineEntity)p.FindEntity(a.Id)!).Label = "North");
        Assert.Equal("North", ((PolylineEntity)doc.Project.FindEntity(a.Id)!).Label);
        doc.Execute("Clear", p => ((PolylineEntity)p.FindEntity(a.Id)!).Label = "");
        Assert.Equal("A", ((PolylineEntity)doc.Project.FindEntity(a.Id)!).Label);
        // a polyline made a section line takes a name too
        var plain = new PolylineEntity { Points = { new Vec3(0, 20, 100), new Vec3(50, 20, 100) } };
        doc.Execute("Add", p => p.Add(plain));
        Assert.Equal("", ((PolylineEntity)doc.Project.FindEntity(plain.Id)!).Label);
        doc.Execute("Kind", p => ((PolylineEntity)p.FindEntity(plain.Id)!).Kind = LineKind.CrossSection);
        Assert.Equal("D", ((PolylineEntity)doc.Project.FindEntity(plain.Id)!).Label);
    }

    [Fact]
    public void LinesFromBefore_TakeLettersInTheirOrder_ANameOfTheUserStays()
    {
        var p = BlastProject.CreateNew();
        var first = Section(0); var own = Section(5); var third = Section(10);
        own.Label = "B";
        p.Add(first); p.Add(own); p.Add(third);
        SectionNames.NameAll(p);
        Assert.Equal(new[] { "A", "B", "C" }, new[] { first, own, third }.Select(l => l.Label));
    }

    [Fact]
    public void TheSheetOfASection_IsCalledByItsName()
    {
        var p = BlastProject.CreateNew();
        var line = Section(0);
        line.Label = "C";
        p.Add(line);
        using var doc = Reports.BuildCrossSections(p, new CrossSectionSheet { Title = "Section" }, new ReportDesign(), new DateTime(2026, 11, 20));
        Assert.Equal("C–C", doc.Pages[0].Section?.Caption);
    }
}
