using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.IO;

namespace BlastDesigner.App.Views;

/// <summary>
/// Settings ▸ Usage statistics: what the usage log says over a period — the most used buttons, menu items and hot keys,
/// the view flags switched on and back off, and the commands followed by a long pause.
/// </summary>
public static class UsageWindow
{
    public const string Title = "Usage statistics";
    private static readonly (string Label, int? Days)[] Periods = { ("7 days", 7), ("30 days", 30), ("90 days", 90), ("All time", null) };
    private static readonly (string Source, string Label)[] Kinds = { (UsageLog.Button, "Buttons"), (UsageLog.Tab, "Tabs"), (UsageLog.Menu, "Menus"), (UsageLog.Hotkey, "Hot keys") };

    public static Window Show()
    {
        // what is still in memory belongs to the statistics too
        UsageLog.Flush();
        var period = 1;
        var topKind = 0;
        var pauseKind = 0;
        UsageAnalysis analysis = new();
        var body = new StackPanel { Spacing = 18, Margin = new Thickness(18, 14, 18, 18) };
        var summary = new TextBlock { FontSize = 12, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var periodRow = Ui.Row(6);

        void Load()
        {
            var from = Periods[period].Days is int d ? DateTime.Today.AddDays(1 - d) : (DateTime?)null;
            analysis = new UsageAnalysis().Analyse(UsageAnalysis.Read(UsageLog.Files(), from));
            summary.Text = analysis.Events == 0
                ? Loc.T("No actions recorded in this period")
                : Loc.F("{0} actions in {1} sessions, {2:d} – {3:d}", analysis.Events, analysis.Sessions, analysis.First, analysis.Last);
            Build();
        }

        void Build()
        {
            periodRow.Children.Clear();
            for (var i = 0; i < Periods.Length; i++)
            {
                var k = i;
                var chip = Ui.Chip(Loc.T(Periods[i].Label), () => { period = k; Load(); }, period == i);
                chip.Tag = "period:" + i;
                periodRow.Children.Add(chip);
            }
            body.Children.Clear();
            body.Children.Add(Card("Most used", KindChips(topKind, k => { topKind = k; Build(); }, "top"),
                Table(analysis.Top(Kinds[topKind].Source, 20).Select(c => (Name(c.Source, c.Id), c.Times.ToString(CultureInfo.CurrentCulture), (double)c.Times)).ToList(),
                    "Times", "top")));
            body.Children.Add(Card("Switched on and straight back off", null,
                Table(analysis.Reverts.Take(20).Select(r => (FlagName(r.Flag), Loc.F("{0} of {1} · {2:0} s", r.Reverts, r.Changes, r.MedianSeconds), (double)r.Reverts)).ToList(),
                    "Reverted · median time", "reverts"),
                Loc.F("Changed back within {0:0} s — a misclick, or a default worth changing", analysis.RevertWithin.TotalSeconds)));
            body.Children.Add(Card("Long pause after", KindChips(pauseKind, k => { pauseKind = k; Build(); }, "pause"),
                Table(analysis.TopPauses(Kinds[pauseKind].Source, 10).Select(p => (Name(p.Source, p.Id), Loc.F("{0} of {1} · {2}", p.LongPauses, p.Times, Duration(p.MedianSeconds)), (double)p.LongPauses)).ToList(),
                    "Long pauses · median", "pauses"),
                Loc.F("No action for {0:0} s or more while the program was active; over {1:0} min counts as away", analysis.LongPause.TotalSeconds, analysis.Away.TotalMinutes)));
        }

        Load();

        var header = new Border
        {
            Background = Look.Panel,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 12),
            Child = new StackPanel { Spacing = 10, Children = { Ui.Row(10, Look.Icon("chart", 20, Look.Accent), new TextBlock { Text = Loc.T(Title), FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center }), Ui.Row(14, periodRow, summary) } }
        };
        Window? w = null;
        var export = Ui.Flat(Ui.Row(6, Look.Icon("export", 15, Look.Muted), new TextBlock { Text = Loc.T("Export CSV…"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }), null, async () =>
        {
            var path = await Dialogs.SaveFile(Title, "usage-statistics.csv", "csv", ("CSV", new[] { "*.csv" }));
            if (path != null) File.WriteAllText(path, Csv(analysis), Encoding.UTF8);
        }, null, 32);
        var folder = Ui.Flat(Ui.Row(6, Look.Icon("open", 15, Look.Muted), new TextBlock { Text = Loc.T("Open folder"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }), UsageLog.Folder, () =>
        {
            Directory.CreateDirectory(UsageLog.Folder);
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(UsageLog.Folder) { UseShellExecute = true });
        }, null, 32);
        var clear = Ui.Flat(Ui.Row(6, Look.Icon("close", 15, Look.Muted), new TextBlock { Text = Loc.T("Clear statistics"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }), null, async () =>
        {
            if (!await Dialogs.Confirm(Loc.T("Delete all recorded actions?"))) return;
            UsageLog.Flush();
            UsageLog.Prune(all: true);
            Load();
        }, null, 32);
        clear.Name = "clear";
        var close = new Button { Content = Loc.T("Close"), Height = 32, Padding = new Thickness(18, 0), CornerRadius = new CornerRadius(6), VerticalContentAlignment = VerticalAlignment.Center, Classes = { "primary" }, IsCancel = true };
        close.Click += (_, _) => w?.Close();
        var bar = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        bar.Children.Add(close);
        bar.Children.Add(Ui.Row(4, export, folder, clear));
        var footer = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 10), Child = bar };
        var dock = new DockPanel { Background = Look.Ground };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(new ScrollViewer { Content = body });
        w = Dialogs.ShowWindow(Title, dock, 760, 720);
        w.MinWidth = 560;
        w.MinHeight = 420;
        return w;
    }

    private static Control KindChips(int selected, Action<int> select, string tag)
    {
        var row = Ui.Row(6);
        for (var i = 0; i < Kinds.Length; i++)
        {
            var k = i;
            var chip = Ui.Chip(Loc.T(Kinds[i].Label), () => select(k), selected == i);
            chip.Tag = $"{tag}:{i}";
            row.Children.Add(chip);
        }
        return row;
    }

    private static Control Card(string title, Control? tools, Control content, string? note = null)
    {
        var head = new DockPanel();
        if (tools != null) { DockPanel.SetDock(tools, Dock.Right); head.Children.Add(tools); }
        head.Children.Add(new TextBlock { Text = Loc.T(title).ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center });
        var stack = new StackPanel { Spacing = 10, Children = { head } };
        if (note != null) stack.Children.Add(new TextBlock { Text = note, FontSize = 11.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(content);
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12), Child = stack };
    }

    /// <summary>Name, value and a bar proportional to <c>Weight</c>.</summary>
    private static Control Table(IReadOnlyList<(string Name, string Value, double Weight)> rows, string valueHeader, string name)
    {
        if (rows.Count == 0) return new TextBlock { Text = Loc.T("Nothing yet"), Foreground = Look.Faint, Name = name };
        var max = rows.Max(r => r.Weight);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,140"), ColumnSpacing = 12, RowSpacing = 6, Name = name };
        void Cell(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); g.Children.Add(c); }
        g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Cell(new TextBlock { Text = Loc.T(valueHeader), FontSize = 11, Foreground = Look.Faint, HorizontalAlignment = HorizontalAlignment.Right }, 0, 2);
        for (var i = 0; i < rows.Count; i++)
        {
            var (n, v, weight) = rows[i];
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Cell(new TextBlock { Text = (i + 1).ToString(CultureInfo.CurrentCulture), FontFamily = Look.Mono, FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, MinWidth = 18 }, i + 1, 0);
            var label = new TextBlock { Text = n, Foreground = Look.TextSoft, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(label, n);
            Cell(label, i + 1, 1);
            Cell(new TextBlock { Text = v, FontFamily = Look.Mono, FontSize = 12, Foreground = Look.Text, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, i + 1, 2);
            var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Look.Field, VerticalAlignment = VerticalAlignment.Center };
            var bar = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Look.Accent, HorizontalAlignment = HorizontalAlignment.Left, Width = max > 0 ? Math.Max(3, 140 * weight / max) : 3 };
            Cell(new Panel { Children = { track, bar }, VerticalAlignment = VerticalAlignment.Center }, i + 1, 3);
        }
        return g;
    }

    /// <summary>What the user saw: menu items, buttons and tabs in the interface language; hot keys as they are.</summary>
    public static string Name(string source, string id) => source switch
    {
        UsageLog.Hotkey => id,
        UsageLog.Button or UsageLog.Tab => Loc.Ui(id) is var u && u != id ? u : Loc.T(id),
        _ => Loc.T(id)
    };

    /// <summary>View flags by the names of their check boxes (Visibility tab) and viewport chips.</summary>
    private static readonly Dictionary<string, string> FlagLabels = new()
    {
        ["ShowTies"] = "Ties", ["ShowRows"] = "Rows", ["ShowSurfaces"] = "Surfaces", ["ShowGrid"] = "Grid", ["ShowAxes"] = "Axes",
        ["ShowHoleLegend"] = "Hole legend", ["ShowTieLegend"] = "Tie legend", ["ShowText"] = "Labels",
        ["CollarsOnly"] = "Hole track", ["ShowCollars"] = "Collar", ["ShowDipTrack"] = "Dip track", ["ShowLoading"] = "Charge track",
        ["ShowActualTrack"] = "Actual track", ["ShowGradePoint"] = "Grade point", ["ShowDeviation"] = "Deviation",
        ["ShowBurdenMarker"] = "Burden marker", ["ShowProfile"] = "MWD profile",
        ["ShowHoleSequenceLabel"] = "Hole order", ["ShowInholeDelayLabel"] = "In-hole delay", ["ShowSurfaceTimeLabel"] = "Surface time", ["ShowHoleTimeLabel"] = "Hole time",
        ["HoleLabels.Label"] = "Hole number", ["HoleLabels.Length"] = "Length", ["HoleLabels.Diameter"] = "Diameter", ["HoleLabels.CollarZ"] = "Elevation",
        ["HoleLabels.Angle"] = "Angle", ["HoleLabels.Bearing"] = "Bearing", ["HoleLabels.Temperature"] = "Temperature", ["HoleLabels.Charge"] = "Explosive mass",
        ["HoleLabels.DeckMasses"] = "Deck masses", ["HoleLabels.DeckSequence"] = "Deck sequence", ["HoleLabels.DetonatorId"] = "Detonator ID", ["HoleLabels.Type"] = "Hole type"
    };

    /// <summary>The check box a flag is switched with ("ShowGrid" → "Grid"); other flags by their property name in words.</summary>
    public static string FlagName(string flag)
    {
        if (FlagLabels.TryGetValue(flag, out var label)) return Loc.Ui(label) is var u && u != label ? u : Loc.T(label);
        var parts = flag.Split('.');
        return parts.Length > 1 ? $"{Loc.Property(parts[0])}: {Loc.Property(parts[1])}" : Loc.Property(parts[0]);
    }

    private static string Duration(double seconds) =>
        seconds >= 60 ? Loc.F("{0:0.#} min", seconds / 60) : Loc.F("{0:0} s", seconds);

    private static string Csv(UsageAnalysis a)
    {
        var sb = new StringBuilder("section;kind;id;times;extra;median_s\n");
        foreach (var c in a.Counts) sb.Append($"top;{c.Source};{c.Id};{c.Times};;\n");
        foreach (var r in a.Reverts) sb.Append(FormattableString.Invariant($"revert;flag;{r.Flag};{r.Changes};{r.Reverts};{r.MedianSeconds:0.#}\n"));
        foreach (var p in a.Pauses) sb.Append(FormattableString.Invariant($"pause;{p.Source};{p.Id};{p.Times};{p.LongPauses};{p.MedianSeconds:0.#}\n"));
        return sb.ToString();
    }
}
