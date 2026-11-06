using BlastDesigner.IO;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>When recent files were opened, and how dates are written.</summary>
public class RecentFilesTests
{
    [Theory]
    [InlineData("de-DE", "05.11.2026")]
    [InlineData("en-US", "11/5/2026")]
    [InlineData("ja-JP", "2026/11/05")]
    public void DatesAreWrittenAsTheSystemWritesThem(string system, string date)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new(system);
            Assert.StartsWith(date, ProgramSettings.FormatDateTime(new DateTime(2026, 11, 5, 14, 7, 0)));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void AFileIsReadForItsSizeAndTimeAndAFileOutOfReachIsNoError()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, new byte[3000]);
            var changed = new DateTime(2026, 3, 4, 5, 6, 0);
            File.SetLastWriteTime(file, changed);
            Assert.Equal((3000L, changed), ProgramSettings.ReadFile(file));
        }
        finally { File.Delete(file); }
        Assert.Null(ProgramSettings.ReadFile(file));
        Assert.Null(ProgramSettings.ReadFile(Path.Combine(Path.GetTempPath(), "no such folder", "a.bdp")));
        Assert.Null(ProgramSettings.ReadFile("bad\0name.bdp"));
        Assert.Null(ProgramSettings.ReadFile(""));
    }

    [Fact]
    public void OpeningRecordsTheTimeAndForgetsFilesOffTheList()
    {
        var s = new ProgramSettings { MaxRecentFiles = 2 };
        var before = DateTime.Now;
        s.AddRecent(@"C:\a.bdp");
        s.AddRecent(@"C:\b.bdp");
        s.AddRecent(@"C:\c.bdp");
        Assert.Equal(new[] { @"C:\c.bdp", @"C:\b.bdp" }, s.RecentFiles);
        Assert.Equal(s.RecentFiles.OrderBy(p => p), s.RecentOpened.Keys.OrderBy(p => p));
        Assert.True(s.RecentOpened[@"C:\c.bdp"] >= before);
    }
}
