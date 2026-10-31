using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.IO;

namespace BlastDesigner.App.Views;

/// <summary>
/// Release notes (CHANGELOG.md / CHANGELOG.en.md embedded in the program): shown once on the first start of a new
/// version, and from Help ▸ What's new.
/// </summary>
public static class WhatsNewWindow
{
    public const string Title = "What's new";

    /// <summary>
    /// On start: after an update, the releases since the last version seen; nothing on a new installation. The version is
    /// remembered before the window shows, so a crash does not bring it back on every start.
    /// </summary>
    public static Window? ShowIfUpdated(ProgramSettings settings, Action<ProgramSettings>? save = null)
    {
        var current = Changelog.Current;
        var releases = Changelog.ToShow(Changelog.Load(Loc.Language), settings.LastSeenVersion, settings.IsNewInstallation, current);
        if (settings.LastSeenVersion != current.ToString())
        {
            settings.LastSeenVersion = current.ToString();
            try { (save ?? (s => s.Save()))(settings); }
            catch (Exception ex) { Log.Error("Saving the last seen version failed", ex); }
        }
        return releases.Count == 0 ? null : Show(releases);
    }

    /// <summary>The window with <paramref name="releases"/> (all of them when null).</summary>
    public static Window Show(IReadOnlyList<Changelog.Release>? releases = null)
    {
        var all = Changelog.Load(Loc.Language);
        var shown = releases ?? all;
        var body = new StackPanel { Spacing = 22, Margin = new Thickness(22, 16, 22, 22) };
        var scroll = new ScrollViewer { Content = body };
        var title = new TextBlock { FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center };
        var date = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2), Background = Look.AccentSoft, VerticalAlignment = VerticalAlignment.Center };
        var dateText = new TextBlock { FontSize = 11.5, Foreground = Look.Accent };
        date.Child = dateText;

        void Fill(IReadOnlyList<Changelog.Release> list)
        {
            body.Children.Clear();
            var newest = list.FirstOrDefault();
            title.Text = newest == null ? Loc.T(Title) : Loc.F("What's new in {0}", newest.Version);
            dateText.Text = newest?.Date ?? "";
            date.IsVisible = !string.IsNullOrEmpty(newest?.Date);
            foreach (var r in list)
            {
                // the first release is named in the header; older ones get their own heading
                if (r != newest)
                    body.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(r.Date) ? r.Version.ToString() : $"{r.Version}  ·  {r.Date}", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Look.TextSoft, Margin = new Thickness(0, 6, 0, -8) });
                foreach (var s in r.Sections) body.Children.Add(SectionView(s));
            }
            scroll.Offset = default;
        }
        Fill(shown);

        var history = Ui.Flat(Ui.Row(6, Look.Icon("recent", 15, Look.Muted), new TextBlock { Text = Loc.T("Full history"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }), null, () => Fill(all), null, 32);
        history.Name = "history";
        history.IsVisible = shown.Count < all.Count;
        var close = new Button { Content = Loc.T("Close"), Height = 32, Padding = new Thickness(18, 0), CornerRadius = new CornerRadius(6), VerticalContentAlignment = VerticalAlignment.Center, Classes = { "primary" }, IsDefault = true, IsCancel = true, Name = "close" };

        var header = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(22, 16), Child = Ui.Row(10, Look.Icon("star", 20, Look.Accent), title, date) };
        var bar = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        bar.Children.Add(close);
        bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { history } });
        var footer = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 10), Child = bar };
        var dock = new DockPanel { Background = Look.Ground };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(scroll);
        var w = Dialogs.ShowWindow(Title, dock, 640, 600);
        w.MinWidth = 460;
        w.MinHeight = 360;
        close.Click += (_, _) => w.Close();
        return w;
    }

    private static Control SectionView(Changelog.Section s)
    {
        var panel = new StackPanel { Spacing = 8 };
        if (s.Title.Length > 0)
        {
            var color = SectionColor(s.Title);
            panel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(color, 0.16),
                Child = new TextBlock { Text = s.Title.ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = new SolidColorBrush(color) }
            });
        }
        foreach (var item in s.Items)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Look.TextSoft, LineHeight = 20 };
            Inline(text.Inlines!, item);
            var dot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(3), Background = Look.Faint, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 8, 10, 0) };
            var row = new DockPanel();
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>New in green, Changed in blue, Fixed in the accent colour; anything else muted.</summary>
    private static Color SectionColor(string title) => title.ToLowerInvariant() switch
    {
        "новое" or "new" or "added" => Color.FromUInt32(0xFF3FB97A),
        "изменено" or "changed" => Look.Blue.Color,
        "исправлено" or "fixed" => Look.Accent.Color,
        _ => Look.Muted.Color
    };

    /// <summary>Item text with **bold** and `code` spans.</summary>
    public static void Inline(InlineCollection inlines, string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            var bold = text.IndexOf("**", i, StringComparison.Ordinal);
            var code = text.IndexOf('`', i);
            var next = new[] { bold, code }.Where(x => x >= 0).DefaultIfEmpty(-1).Min();
            if (next < 0) { inlines.Add(new Run(text[i..])); break; }
            if (next > i) inlines.Add(new Run(text[i..next]));
            var isBold = next == bold;
            var open = isBold ? 2 : 1;
            var end = isBold ? text.IndexOf("**", next + 2, StringComparison.Ordinal) : text.IndexOf('`', next + 1);
            if (end < 0) { inlines.Add(new Run(text[next..])); break; }
            var span = text[(next + open)..end];
            inlines.Add(isBold
                ? new Run(span) { FontWeight = FontWeight.SemiBold, Foreground = Look.Text }
                : new Run(span) { FontFamily = Look.Mono, Foreground = Look.Text, Background = Look.Field });
            i = end + open;
        }
    }
}
