using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BlastDesigner.App.Services;
using BlastDesigner.IO;

namespace BlastDesigner.App.Views;

public enum StartupChoice { None, New, Open, Recent, Template }

/// <summary>Startup assistant: new, open, from a template, one of the recent files or a .bdp dropped on the window.</summary>
public static class StartupWindow
{
    public static string? SelectedRecent { get; private set; }

    /// <summary>The form of the last call (used by the UI smoke test).</summary>
    public static StartupForm? Last { get; private set; }

    public static async Task<StartupChoice> Show(ProgramSettings settings)
    {
        var choice = StartupChoice.None;
        SelectedRecent = null;
        var form = new StartupForm(settings);
        Last = form;
        var w = new Window
        {
            Title = Loc.T("Startup assistant"),
            Width = 880,
            Height = 520,
            CanResize = false,
            Background = Look.Ground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = form
        };
        form.Attach(w);
        form.Done = (c, path) =>
        {
            choice = c;
            SelectedRecent = path;
            w.Close();
        };
        // Message boxes and file pickers asked from the assistant (missing file, help) belong to it, not to the main window under it.
        var owner = Dialogs.Owner;
        w.Opened += (_, _) => { Dialogs.Owner = w; form.Focus(); };
        w.Closed += (_, _) => Dialogs.Owner = owner;
        await Dialogs.ShowModal(w, owner);
        Dialogs.Owner = owner;
        return choice;
    }
}

public sealed class StartupForm : DockPanel
{
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromUInt32(0xFFF0C13A));

    private readonly ProgramSettings _settings;
    private readonly StackPanel _list = new() { Spacing = 2, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ScrollViewer _scroll;
    private readonly Panel _right = new();
    private readonly TextBox _search;
    private readonly Rectangle _dropFrame;
    private readonly Border _dropZone;
    private readonly List<Entry> _visible = new();
    private readonly List<Border> _rows = new();
    private readonly Button _ok;
    private int _selected;

    /// <summary>Called with the choice and, for <see cref="StartupChoice.Recent"/>, the file to open.</summary>
    public Action<StartupChoice, string?>? Done { get; set; }

    /// <summary>Files shown in the list, in display order (pinned first).</summary>
    public IReadOnlyList<string> VisibleFiles => _visible.Select(e => e.Path).ToList();

    public string? SelectedFile => _selected >= 0 && _selected < _visible.Count ? _visible[_selected].Path : null;

    private sealed record Entry(string Path, bool Pinned, bool Exists, DateTime Modified);

    public StartupForm(ProgramSettings settings)
    {
        _settings = settings;
        Focusable = true;

        // ---- footer ----
        var dontShow = new CheckBox { Content = Loc.T("Do not show at startup"), IsChecked = !settings.ShowStartupAssistant, Foreground = Look.TextSoft, FontSize = 12.5 };
        dontShow.IsCheckedChanged += (_, _) => { settings.ShowStartupAssistant = dontShow.IsChecked != true; settings.Save(); };
        var help = new Button
        {
            Content = new TextBlock { Text = Loc.Ui("Help"), FontSize = 12.5, Foreground = Look.Muted, TextDecorations = TextDecorations.Underline },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center
        };
        help.Click += (_, _) => AnalysisWindows.Help();
        // opens the file selected in the list, the same as a double click or Enter
        _ok = new Button
        {
            Content = Loc.T("OK"),
            Height = 30,
            MinWidth = 88,
            Padding = new Thickness(16, 0),
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "primary" },
            Name = "openSelected",
            IsEnabled = false
        };
        ToolTip.SetTip(_ok, Loc.Ui("Open the selected file (Enter)"));
        _ok.Click += async (_, _) => { if (_selected >= 0 && _selected < _visible.Count) await OpenEntry(_visible[_selected]); };
        var version = new TextBlock { Text = "v" + AppVersion + (Trial.IsTrial ? " · " + Trial.Status : ""), FontFamily = Look.Mono, FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center };
        var footer = new Border
        {
            Background = Look.Panel,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 6),
            Child = new DockPanel
            {
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, [DockPanel.DockProperty] = Dock.Right, Children = { help, version, _ok } },
                    dontShow
                }
            }
        };
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        // ---- left column ----
        var left = new DockPanel();
        left.Children.Add(Header());
        var drop = DropZone(out _dropFrame, out _dropZone);
        SetDock(drop, Dock.Bottom);
        left.Children.Add(drop);
        var cards = new StackPanel { Spacing = 8, Margin = new Thickness(0, 22, 0, 0) };
        cards.Children.Add(Card("new", "New blast", "Empty project with the site settings", "Ctrl+N", true, () => Finish(StartupChoice.New)));
        cards.Children.Add(Card("open", "Open…", ".bdp file from the disk or the network", "Ctrl+O", false, () => Finish(StartupChoice.Open)));
        if (HasTemplates())
            cards.Children.Add(Card("template", "From template", "Blast settings and resources of a saved template", null, false, () => Finish(StartupChoice.Template)));
        left.Children.Add(cards);
        var leftHost = new Border
        {
            Width = 300,
            Background = Look.Field,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(20, 22, 20, 18),
            Child = left
        };
        SetDock(leftHost, Dock.Left);
        Children.Add(leftHost);

        // ---- right column ----
        _search = new TextBox
        {
            Width = 230,
            Height = 30,
            FontSize = 12.5,
            Watermark = Loc.Ui("Search by name or folder"),
            Background = Look.Field,
            BorderBrush = Look.Line,
            VerticalContentAlignment = VerticalAlignment.Center,
            InnerLeftContent = new Border { Padding = new Thickness(8, 0, 0, 0), Child = Look.Icon("search", 14, Look.Faint) }
        };
        _search.TextChanged += (_, _) => { _selected = 0; Rebuild(); };
        var head = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 12),
            Children =
            {
                new Border { [DockPanel.DockProperty] = Dock.Right, Child = _search },
                new TextBlock { Text = Loc.T("Recent files").ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 1.2, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center }
            }
        };
        _scroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var rightDock = new DockPanel { Margin = new Thickness(20, 18, 12, 12) };
        SetDock(head, Dock.Top);
        rightDock.Children.Add(head);
        rightDock.Children.Add(_right);
        Children.Add(rightDock);

        Rebuild();
    }

    /// <summary>Hooks keyboard and drag-and-drop to the whole window.</summary>
    public void Attach(Window w)
    {
        w.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(w, true);
        w.AddHandler(DragDrop.DragEnterEvent, OnDrag);
        w.AddHandler(DragDrop.DragOverEvent, OnDrag);
        w.AddHandler(DragDrop.DragLeaveEvent, (_, _) => Highlight(false));
        w.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private static bool HasTemplates()
    {
        try { return new TemplateStore(ProgramSettings.TemplatesFolder).List().Any(); }
        catch { return false; }
    }

    private static string AppVersion => typeof(StartupForm).Assembly.GetName().Version?.ToString(3) ?? "0.2.4";

    private static CultureInfo Culture => CultureInfo.GetCultureInfo(Loc.Language == "ru" ? "ru-RU" : "en-US");

    private void Finish(StartupChoice c, string? path = null) => Done?.Invoke(c, path);

    // ---- left column parts ------------------------------------------------------------------

    private static Control Header()
    {
        var logo = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(9),
            Background = Look.Accent,
            Child = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = Look.Icon("det", 22, Look.OnAccent, 2) }
        };
        var name = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Blast Designer", FontSize = 19, FontWeight = FontWeight.Bold, Foreground = Look.Text },
                new TextBlock { Text = Loc.F(Loc.Ui("Version {0}"), AppVersion), FontFamily = Look.Mono, FontSize = 11, Foreground = Look.Faint }
            }
        };
        var p = new StackPanel { Spacing = 12 };
        p.Children.Add(Ui.Row(12, logo, name));
        p.Children.Add(new TextBlock
        {
            Text = Loc.T("Open-pit blast design: layout, charging, initiation and analysis."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 18,
            Foreground = Look.Muted
        });
        SetDock(p, Dock.Top);
        return p;
    }

    private static Button Card(string icon, string title, string subtitle, string? key, bool primary, Action click)
    {
        var fore = primary ? Look.OnAccent : Look.Text;
        var sub = primary ? new SolidColorBrush(Color.FromUInt32(0xB315181C)) : Look.Faint;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Child = Look.Icon(icon, 20, fore) });
        grid.Children.Add(new StackPanel
        {
            [Grid.ColumnProperty] = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = Loc.Ui(title), FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = fore },
                new TextBlock { Text = Loc.Ui(subtitle), FontSize = 11, LineHeight = 13, Foreground = sub, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis }
            }
        });
        if (key != null)
            grid.Children.Add(primary
                ? new Border
                {
                    [Grid.ColumnProperty] = 2,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.FromUInt32(0x2615181C)),
                    Padding = new Thickness(5, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = key, FontFamily = Look.Mono, FontSize = 10.5, Foreground = Look.OnAccent }
                }
                : Kbd(key));
        var b = new Button
        {
            Height = 56,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 0),
            CornerRadius = new CornerRadius(8),
            Content = grid
        };
        if (primary) b.Classes.Add("primary");
        else
        {
            b.Background = Look.Ribbon;
            b.BorderBrush = Look.Line;
            b.BorderThickness = new Thickness(1);
        }
        b.Click += (_, _) => click();
        return b;
    }

    private static Control Kbd(string key)
    {
        var k = Look.Kbd(key, 10.5);
        Grid.SetColumn(k, 2);
        return k;
    }

    private static Control DropZone(out Rectangle frame, out Border zone)
    {
        frame = new Rectangle { Stroke = Look.LineStrong, StrokeThickness = 1.2, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 }, RadiusX = 8, RadiusY = 8 };
        zone = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = Look.Icon("import", 18, Look.Faint) },
                    new TextBlock { Text = Loc.Ui("Drop a .bdp file here to open it"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 11.5, Foreground = Look.Faint }
                }
            }
        };
        return new Panel { Children = { zone, frame } };
    }

    // ---- recent list ------------------------------------------------------------------------

    private List<Entry> Entries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<Entry>();
        void Add(string p, bool pinned)
        {
            if (string.IsNullOrWhiteSpace(p) || !seen.Add(p)) return;
            var exists = File.Exists(p);
            all.Add(new Entry(p, pinned, exists, exists ? File.GetLastWriteTime(p) : DateTime.MinValue));
        }
        foreach (var p in _settings.PinnedFiles) Add(p, true);
        foreach (var p in _settings.RecentFiles) Add(p, false);
        return all;
    }

    private static int Group(Entry e)
    {
        if (e.Pinned) return 0;
        var today = DateTime.Today;
        if (e.Modified >= today.AddDays(-1)) return 1;
        if (e.Modified.Year == today.Year && e.Modified.Month == today.Month) return 2;
        return 3;
    }

    private static readonly string[] GroupNames = { "Pinned", "Today and yesterday", "Earlier this month", "Long ago" };

    private void Rebuild()
    {
        var all = Entries();
        var q = _search.Text?.Trim() ?? "";
        var shown = all.Where(e => q.Length == 0 || e.Path.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .Select((e, i) => (e, i)).OrderBy(x => Group(x.e)).ThenBy(x => x.i).Select(x => x.e).ToList();
        _visible.Clear();
        _visible.AddRange(shown);
        _ok.IsEnabled = shown.Count > 0;
        _rows.Clear();
        _list.Children.Clear();
        _right.Children.Clear();
        if (all.Count == 0) { _right.Children.Add(EmptyState()); return; }
        _right.Children.Add(_scroll);
        if (shown.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = Loc.Ui("Nothing matches the search"), Foreground = Look.Muted, FontSize = 12.5, Margin = new Thickness(4, 12) });
            return;
        }
        _selected = Math.Clamp(_selected, 0, shown.Count - 1);
        int group = -1;
        for (int i = 0; i < shown.Count; i++)
        {
            var g = Group(shown[i]);
            if (g != group)
            {
                group = g;
                _list.Children.Add(new TextBlock
                {
                    Text = Loc.Ui(GroupNames[g]),
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Look.Faint,
                    Margin = new Thickness(4, _list.Children.Count == 0 ? 0 : 12, 0, 4)
                });
            }
            var row = Row(shown[i], i);
            _rows.Add(row);
            _list.Children.Add(row);
        }
    }

    private static Control EmptyState()
    {
        var frame = new Rectangle { Stroke = Look.LineStrong, StrokeThickness = 1.2, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 5, 4 }, RadiusX = 10, RadiusY = 10 };
        var text = new StackPanel
        {
            Spacing = 8,
            MaxWidth = 340,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4), Child = Look.Icon("recent", 30, Look.Faint, 1.5) },
                new TextBlock { Text = Loc.Ui("Your projects will appear here"), FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock
                {
                    Text = Loc.Ui("Create a new blast or open a .bdp file: the files you work with are listed here so you can return to them in one click."),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 12.5,
                    LineHeight = 18,
                    Foreground = Look.Muted
                }
            }
        };
        return new Panel { Margin = new Thickness(0, 0, 8, 0), Children = { frame, text } };
    }

    private Border Row(Entry e, int index)
    {
        var sel = index == _selected;
        var name = System.IO.Path.GetFileName(e.Path);
        var folder = System.IO.Path.GetDirectoryName(e.Path) ?? "";
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        grid.Children.Add(new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(7),
            Background = Look.Ribbon,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = Look.Icon("pattern", 16, e.Exists ? Look.Muted : Look.Faint, 1.5) }
        });
        var text = new StackPanel { [Grid.ColumnProperty] = 1, VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = e.Exists ? Look.Text : Look.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(e.Exists
            ? new LeadingEllipsisText(folder) { FontFamily = Look.Mono, FontSize = 11, Foreground = Look.Faint }
            : Ui.Row(5, Look.Icon("warn", 12, Warn), new TextBlock { Text = Loc.Ui("File not found at this path"), FontSize = 11.5, Foreground = Warn }));
        grid.Children.Add(text);
        if (e.Exists)
            grid.Children.Add(new TextBlock
            {
                [Grid.ColumnProperty] = 2,
                Text = When(e.Modified),
                FontFamily = Look.Mono,
                FontSize = 11,
                Foreground = Look.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            });
        var actions = new StackPanel { [Grid.ColumnProperty] = 3, Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Action("pin", e.Pinned ? "Unpin" : "Pin", e.Pinned ? Look.Accent : Look.Muted, () => TogglePin(e)));
        actions.Children.Add(Action("open", "Show in folder", Look.Muted, () => ShowInFolder(e.Path)));
        actions.Children.Add(Action("close", "Remove from the list", Look.Muted, () => Remove(e)));
        grid.Children.Add(actions);

        var row = new Border
        {
            Height = 56,
            Padding = new Thickness(10, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = sel ? Look.Accent : Brushes.Transparent,
            Background = sel ? Look.AccentSoft : Brushes.Transparent,
            Child = grid
        };
        ToolTip.SetTip(row, e.Path);
        void Actions(bool on) { actions.Opacity = on ? 1 : 0; actions.IsHitTestVisible = on; }
        Actions(sel);
        row.PointerEntered += (_, _) => { if (index != _selected) row.Background = Look.Hover; Actions(true); };
        row.PointerExited += (_, _) => { if (index != _selected) row.Background = Brushes.Transparent; Actions(index == _selected); };
        row.PointerPressed += (_, a) => { if (a.GetCurrentPoint(row).Properties.IsLeftButtonPressed) Select(index); };
        row.DoubleTapped += async (_, _) => await OpenEntry(e);
        return row;
    }

    private static Button Action(string icon, string tip, IBrush brush, Action click)
    {
        var b = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = Look.Icon(icon, 14, brush)
        };
        ToolTip.SetTip(b, Loc.Ui(tip));
        b.Click += (_, a) => { a.Handled = true; Ui.Safe(click); };
        return b;
    }

    private static string When(DateTime d)
    {
        var c = Culture;
        if (d.Date == DateTime.Today) return Loc.Ui("Today") + ", " + d.ToString("HH:mm", c);
        if (d.Date == DateTime.Today.AddDays(-1)) return Loc.Ui("Yesterday") + ", " + d.ToString("HH:mm", c);
        return d.ToString("dd.MM.yyyy", c);
    }

    private void Select(int index)
    {
        if (_visible.Count == 0) return;
        _selected = Math.Clamp(index, 0, _visible.Count - 1);
        for (int i = 0; i < _rows.Count; i++)
        {
            var on = i == _selected;
            _rows[i].Background = on ? Look.AccentSoft : Brushes.Transparent;
            _rows[i].BorderBrush = on ? Look.Accent : Brushes.Transparent;
            if (_rows[i].Child is Grid g && g.Children[^1] is StackPanel actions) { actions.Opacity = on ? 1 : 0; actions.IsHitTestVisible = on; }
        }
        _rows[_selected].BringIntoView();
    }

    // ---- actions ----------------------------------------------------------------------------

    public void TogglePin(string path)
    {
        var e = _visible.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        if (e != null) TogglePin(e);
    }

    private void TogglePin(Entry e)
    {
        if (e.Pinned)
        {
            _settings.PinnedFiles.RemoveAll(p => string.Equals(p, e.Path, StringComparison.OrdinalIgnoreCase));
            // An unpinned file that has dropped off the recent list stays visible until the list is closed.
            if (!_settings.RecentFiles.Contains(e.Path, StringComparer.OrdinalIgnoreCase)) _settings.RecentFiles.Add(e.Path);
        }
        else _settings.PinnedFiles.Add(e.Path);
        Save();
        _selected = Entries().Select((x, i) => (x, i)).OrderBy(x => Group(x.x)).ThenBy(x => x.i).Select(x => x.x.Path).ToList()
            .FindIndex(p => string.Equals(p, e.Path, StringComparison.OrdinalIgnoreCase));
        Rebuild();
    }

    private void Remove(Entry e)
    {
        _settings.PinnedFiles.RemoveAll(p => string.Equals(p, e.Path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentFiles.RemoveAll(p => string.Equals(p, e.Path, StringComparison.OrdinalIgnoreCase));
        Save();
        Rebuild();
    }

    private void Save()
    {
        try { _settings.Save(); }
        catch (Exception ex) { Log.Error("Saving the recent file list", ex); }
    }

    private static void ShowInFolder(string path)
    {
        if (File.Exists(path) && OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            return;
        }
        var dir = System.IO.Path.GetDirectoryName(path);
        if (dir != null && Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        else _ = Dialogs.Info(Loc.F("File not found: {0}", path));
    }

    private async Task OpenEntry(Entry e)
    {
        if (File.Exists(e.Path)) { Finish(StartupChoice.Recent, e.Path); return; }
        if (!await Dialogs.Confirm(Loc.F(Loc.Ui("The file {0} was not found. Find it in another folder?"), e.Path))) return;
        var found = await Dialogs.OpenFile("Open blast", ("Blast Designer project", new[] { "*" + ProjectFile.Extension }));
        if (found == null) return;
        void Swap(List<string> list)
        {
            var i = list.FindIndex(p => string.Equals(p, e.Path, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) list[i] = found;
        }
        Swap(_settings.PinnedFiles);
        Swap(_settings.RecentFiles);
        Save();
        Finish(StartupChoice.Recent, found);
    }

    private async void OnKey(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var inSearch = e.Source is TextBox;
        switch (e.Key)
        {
            case Key.N when ctrl: e.Handled = true; Finish(StartupChoice.New); break;
            case Key.O when ctrl: e.Handled = true; Finish(StartupChoice.Open); break;
            case Key.F when ctrl: e.Handled = true; _search.Focus(); break;
            case Key.Escape when inSearch && !string.IsNullOrEmpty(_search.Text): e.Handled = true; _search.Text = ""; break;
            case Key.Escape: e.Handled = true; Finish(StartupChoice.None); break;
            case Key.Down when _visible.Count > 0: e.Handled = true; Select(_selected + 1); break;
            case Key.Up when _visible.Count > 0: e.Handled = true; Select(_selected - 1); break;
            case Key.Home when !inSearch && _visible.Count > 0: e.Handled = true; Select(0); break;
            case Key.End when !inSearch && _visible.Count > 0: e.Handled = true; Select(_visible.Count - 1); break;
            case Key.Enter when _visible.Count > 0: e.Handled = true; await OpenEntry(_visible[_selected]); break;
            case Key.Delete when !inSearch && _visible.Count > 0: e.Handled = true; Remove(_visible[_selected]); break;
        }
    }

    // ---- drag and drop ----------------------------------------------------------------------

    private static string? DroppedProject(DragEventArgs e) =>
        e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>()
            .FirstOrDefault(p => string.Equals(System.IO.Path.GetExtension(p), ProjectFile.Extension, StringComparison.OrdinalIgnoreCase));

    private void OnDrag(object? sender, DragEventArgs e)
    {
        var ok = DroppedProject(e) != null;
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        Highlight(ok);
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        Highlight(false);
        var path = DroppedProject(e);
        if (path != null) Finish(StartupChoice.Recent, path);
    }

    public void Highlight(bool on)
    {
        _dropFrame.Stroke = on ? Look.Accent : Look.LineStrong;
        _dropZone.Background = on ? Look.AccentSoft : Brushes.Transparent;
    }
}

/// <summary>
/// One line of text cut at the start ("…\Карьер\Блок 12") so the end of a long path stays visible.
/// Avalonia's own leading ellipsis throws on some strings, so the cut is measured here.
/// </summary>
public sealed class LeadingEllipsisText : TextBlock
{
    private readonly string _full;

    public LeadingEllipsisText(string text)
    {
        _full = text;
        Text = text;
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override Size MeasureOverride(Size availableSize)
    {
        Text = Fit(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }

    private string Fit(double width)
    {
        if (double.IsInfinity(width) || TextWidth(_full) <= width) return _full;
        int lo = 0, hi = _full.Length;
        // Longest tail that fits after the ellipsis.
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (TextWidth("…" + _full[^mid..]) <= width) lo = mid; else hi = mid - 1;
        }
        return lo == 0 ? "…" : "…" + _full[^lo..];
    }

    private double TextWidth(string s) =>
        new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily, FontStyle, FontWeight), FontSize, null).Width;
}
