using BlastDesigner.IO;

namespace BlastDesigner.Tests;

public class ChangelogTests
{
    [Fact]
    public void Parses_releases_sections_and_items()
    {
        var md = """
            # Title
            <!-- ## 9.9 — not a release, inside the comment it is still a heading line but not a version -->
            ## 0.1.7 — 2026-10-31
            ### Новое
            - first **bold** item
              continued here
            - second `code`
            ### Исправлено
            * fixed one
            ## 0.1.6
            - loose item
            """;
        var r = Changelog.Parse(md);
        Assert.Equal(new[] { new Version(0, 1, 7), new Version(0, 1, 6) }, r.Select(x => x.Version));
        Assert.Equal("2026-10-31", r[0].Date);
        Assert.Equal(new[] { "Новое", "Исправлено" }, r[0].Sections.Select(s => s.Title));
        Assert.Equal("first **bold** item continued here", r[0].Sections[0].Items[0]);
        Assert.Equal("fixed one", r[0].Sections[1].Items[0]);
        Assert.Equal("", r[1].Sections[0].Title);
        Assert.Equal("loose item", r[1].Sections[0].Items[0]);
    }

    [Fact]
    public void Shows_nothing_on_a_new_installation_or_the_same_version_and_the_missed_releases_after_an_update()
    {
        var all = Changelog.Parse("## 0.1.8\n- c\n## 0.1.7\n- b\n## 0.1.6\n- a\n## 0.1.5\n- old");
        var current = new Version(0, 1, 8);
        Assert.Empty(Changelog.ToShow(all, null, true, current));
        Assert.Empty(Changelog.ToShow(all, "0.1.8", false, current));
        Assert.Empty(Changelog.ToShow(all, "0.1.9", false, current));
        Assert.Equal(new[] { "0.1.8", "0.1.7" }, Changelog.ToShow(all, "0.1.6", false, current).Select(x => x.Version.ToString()));
        // settings from before the notes existed: everything up to this version
        Assert.Equal(4, Changelog.ToShow(all, null, false, current).Count);
        Assert.Equal(new[] { "0.1.8" }, Changelog.ToShow(all, "0.1.7.0", false, new Version(0, 1, 8, 0)).Select(x => x.Version.ToString()));
    }

    [Fact]
    public void Both_languages_have_notes_for_the_current_version_and_the_same_releases()
    {
        var ru = Changelog.Load("ru");
        var en = Changelog.Load("en");
        Assert.Contains(ru, r => r.Version == Changelog.Current);
        Assert.Contains(en, r => r.Version == Changelog.Current);
        Assert.Equal(ru.Select(r => r.Version), en.Select(r => r.Version));
        foreach (var (a, b) in ru.Zip(en))
        {
            Assert.Equal(a.Sections.Count, b.Sections.Count);
            Assert.Equal(a.Sections.Select(s => s.Items.Count), b.Sections.Select(s => s.Items.Count));
            Assert.All(a.Sections, s => Assert.NotEmpty(s.Items));
        }
    }
}

// one class: the usage log is static
public class UsageTests
{
    private static string TempFolder()
    {
        var d = Path.Combine(Path.GetTempPath(), "bd-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static int Lines(string folder) => UsageAnalysis.Read(Directory.GetFiles(folder, "usage-*.jsonl")).Count;

    [Fact]
    public void Buffer_goes_to_the_file_at_30_events_or_after_the_interval()
    {
        var folder = TempFolder();
        UsageLog.Folder = folder;
        UsageLog.FlushInterval = TimeSpan.FromHours(1);
        UsageLog.Start("test");
        try
        {
            // the session start is the first event
            for (int i = 0; i < 28; i++) UsageLog.Track(UsageLog.Button, "Row");
            Assert.Equal(0, Lines(folder));
            UsageLog.Track(UsageLog.Hotkey, "Ctrl+Z");
            Assert.Equal(30, Lines(folder));
            Assert.Equal(0, UsageLog.Pending);

            UsageLog.FlushInterval = TimeSpan.FromMilliseconds(150);
            UsageLog.Stop();
            Assert.Equal(31, Lines(folder));
            UsageLog.Start("test");
            UsageLog.Track(UsageLog.Menu, "Quantities");
            var until = DateTime.Now.AddSeconds(5);
            while (Lines(folder) < 33 && DateTime.Now < until) Thread.Sleep(50);
            Assert.Equal(33, Lines(folder));
        }
        finally
        {
            UsageLog.Stop();
            UsageLog.FlushInterval = TimeSpan.FromSeconds(15);
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Lines_round_trip_and_a_broken_line_is_skipped()
    {
        var e = new UsageEvent(new DateTime(2026, 10, 31, 14, 3, 12, 345), UsageLog.Flag, "ShowLoading", "off", "on", "abc");
        Assert.Equal(e, UsageLog.FromJson(UsageLog.ToJson(e)));
        Assert.Null(UsageLog.FromJson("{\"t\":\"2026-10-31T14:0"));
    }

    private static readonly DateTime T0 = new(2026, 10, 31, 9, 0, 0);
    private static UsageEvent E(double s, string source, string id, string? v = null, string? o = null, string session = "a") => new(T0.AddSeconds(s), source, id, v, o, session);

    [Fact]
    public void Counts_commands_by_kind()
    {
        var a = new UsageAnalysis().Analyse(new[]
        {
            E(0, "session", "start"), E(1, "button", "Row"), E(2, "button", "Row"), E(3, "menu", "Quantities"), E(4, "hotkey", "Ctrl+Z"), E(5, "button", "Hole"), E(6, "flag", "ShowGrid", "off", "on")
        });
        Assert.Equal(("Row", 2), a.Top("button", 10).Select(c => (c.Id, c.Times)).First());
        Assert.Single(a.Top("menu", 10));
        Assert.Single(a.Top("hotkey", 10));
        Assert.Equal(6, a.Events);
    }

    [Fact]
    public void Finds_flags_switched_back_within_a_minute()
    {
        var a = new UsageAnalysis().Analyse(new[]
        {
            E(0, "flag", "ShowLoading", "on", "off"), E(10, "flag", "ShowLoading", "off", "on"),     // back in 10 s
            E(20, "flag", "ShowLoading", "on", "off"), E(200, "flag", "ShowLoading", "off", "on"),   // back after 3 min: kept
            E(300, "flag", "HoleLabels.Length", "on", "off"), E(330, "flag", "HoleLabels.Length", "off", "on"),
            E(400, "flag", "ShowGrid", "off", "on")
        });
        var loading = a.Reverts.Single(r => r.Flag == "ShowLoading");
        Assert.Equal((4, 1, 10.0), (loading.Changes, loading.Reverts, loading.MedianSeconds));
        Assert.Equal(30, a.Reverts.Single(r => r.Flag == "HoleLabels.Length").MedianSeconds);
        Assert.DoesNotContain(a.Reverts, r => r.Flag == "ShowGrid");
    }

    [Fact]
    public void Long_pauses_leave_out_inactive_time_and_time_away()
    {
        var a = new UsageAnalysis().Analyse(new[]
        {
            E(0, "button", "Quantities"), E(40, "button", "Row"),                                      // 40 s: long
            E(41, "button", "Row"), E(45, "app", "inactive"), E(500, "app", "active"), E(510, "menu", "x"), // 4 + 10 s: short
            E(520, "button", "Quantities"), E(570, "dialog", "Quantities"),                            // 10 s after x, then 50 s: long
            E(575, "hotkey", "F1"), E(3000, "button", "Row"),                                          // 38 min: away
            E(3001, "button", "Hole"), E(3100, "session", "end")                                       // no next action
        });
        var q = a.TopPauses("button", 10).Single();
        Assert.Equal(("Quantities", 2, 45.0), (q.Id, q.LongPauses, q.MedianSeconds));
        Assert.Empty(a.TopPauses("hotkey", 10));
        Assert.Empty(a.TopPauses("menu", 10));
    }

    [Fact]
    public void Sessions_are_analysed_apart()
    {
        var a = new UsageAnalysis().Analyse(new[]
        {
            E(0, "flag", "ShowGrid", "off", "on", "a"), E(5, "flag", "ShowGrid", "on", "off", "b"),
            E(6, "button", "Row", session: "a"), E(100, "button", "Hole", session: "b")
        });
        Assert.Empty(a.Reverts);
        Assert.Equal(2, a.Sessions);
        Assert.Empty(a.TopPauses("button", 10));
    }
}
