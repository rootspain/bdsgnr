using BlastDesigner.Domain.Model;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>Rows are named by letters, as the columns of a spreadsheet: A… Z, AA, AB… ZZ, AAA.</summary>
public class RowLetterTests
{
    [Theory]
    [InlineData(0, "A")]
    [InlineData(1, "B")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void ARowIsNamedByTheLetterOfItsPlace(int index, string name) => Assert.Equal(name, HoleRow.Letter(index));

    [Fact]
    public void ANewRowIsA() => Assert.Equal("A", new HoleRow().Name);

    [Fact]
    public void ARowNamedAnewGivesItsHolesTheNewName_TheirPrefixesAndNumbersKept()
    {
        var p = BlastProject.CreateNew();
        var a = p.Add(new Hole { Label = "A1" });
        var b = p.Add(new Hole { Label = "B12-A7" });
        var other = p.Add(new Hole { Label = "A3" });
        var row = new HoleRow { Name = "A", HoleIds = { a.Id, b.Id } };
        p.Rows.Add(row);
        BlastDesigner.Engine.HoleDesign.RenameRow(p, row.Id, "D");
        Assert.Equal("D", row.Name);
        Assert.Equal(new[] { "D1", "B12-D7" }, new[] { a.Label, b.Label });
        // a hole of no row of it keeps its label
        Assert.Equal("A3", other.Label);
    }

    [Fact]
    public void ARowOfHolesNotNumberedByItIsNumberedAnewWhenNamed()
    {
        // a file of before: "Row 8" with holes numbered through the blast
        var p = BlastProject.CreateNew();
        var holes = new[] { "57", "58", "59" }.Select(l => p.Add(new Hole { Label = l })).ToList();
        var row = new HoleRow { Name = "Row 8" };
        row.HoleIds.AddRange(holes.Select(h => h.Id));
        p.Rows.Add(row);
        BlastDesigner.Engine.HoleDesign.RenameRow(p, row.Id, "H");
        Assert.Equal(new[] { "H1", "H2", "H3" }, holes.Select(h => h.Label));
    }

    [Fact]
    public void TheHolesOfRowsAreNumberedByTheirRowFromTheFirstNumber()
    {
        var holes = Enumerable.Range(0, 5).Select(_ => new Hole()).ToList();
        var rows = new[] { new HoleRow { Name = "A", HoleIds = { holes[0].Id, holes[1].Id } }, new HoleRow { Name = "B", HoleIds = { holes[2].Id, holes[3].Id, holes[4].Id } } };
        BlastDesigner.Engine.HoleDesign.LabelRows(rows, holes, "P-", 5);
        Assert.Equal(new[] { "P-A5", "P-A6", "P-B5", "P-B6", "P-B7" }, holes.Select(h => h.Label));
    }
}
