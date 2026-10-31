using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.Domain;
using BlastDesigner.IO;

namespace BlastDesigner.App.Views;

/// <summary>Edit ▸ Program settings. Theme and accent are previewed live and put back on Cancel.</summary>
public static class SettingsWindow
{
    /// <summary>Returns the edited copy on Apply, null on Cancel.</summary>
    public static async Task<ProgramSettings?> Show(ProgramSettings settings)
    {
        var form = new SettingsForm(settings);
        var w = new Window
        {
            Title = Loc.T("Program settings"),
            Width = 820,
            Height = 700,
            MinWidth = 660,
            MinHeight = 460,
            Background = Look.Ground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = form
        };
        form.Closing = ok => { form.Applied = ok; w.Close(); };
        w.Closed += (_, _) => { if (!form.Applied) Look.ApplyTheme(settings.IsLight, settings.AccentColor); };
        await Dialogs.ShowModal(w);
        return form.Applied ? form.Edited : null;
    }
}

/// <summary>Content of the settings window. Public so the UI smoke test can reach it.</summary>
public sealed class SettingsForm : DockPanel
{
    private static readonly uint[] Accents = { 0xFFF0A63A, 0xFF4C9BFF, 0xFF3FB97A, 0xFF22B8C8, 0xFF9B7BFF, 0xFFEC5B9A, 0xFFE5484D, 0xFF8A929D };
    private static readonly uint[] DarkBackgrounds = { 0xFF101215, 0xFF15181C, 0xFF1E2229, 0xFF0B1A2A };
    private static readonly uint[] LightBackgrounds = { 0xFFFFFFFF, 0xFFF6F7F9, 0xFFFFFBF0, 0xFFEEF4FA };
    private static readonly uint[] Highlights = { 0xFF4C9BFF, 0xFFFF00FF, 0xFF22D3EE, 0xFFFACC15, 0xFF4ADE80 };
    private static readonly uint[] Geometry = { 0xFFC9CFD6, 0xFF8A929D, 0xFF9B7BFF, 0xFF22B8C8, 0xFF1B1F24 };

    private readonly StackPanel _body = new() { Spacing = 26, Margin = new Thickness(22, 18, 22, 24) };
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _nav = new() { Spacing = 2, Margin = new Thickness(10, 14) };
    private readonly Dictionary<string, Control> _sections = new();

    public SettingsForm(ProgramSettings settings)
    {
        Edited = ProjectJson.DeepClone(settings);
        var footer = Footer();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);
        var nav = new Border { Width = 190, Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = _nav };
        SetDock(nav, Dock.Left);
        Children.Add(nav);
        _scroll = new ScrollViewer { Content = _body };
        Children.Add(_scroll);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control) { CommitFocused(); Closing?.Invoke(true); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Build();
    }

    /// <summary>The edited copy; the program settings change only on Apply.</summary>
    public ProgramSettings Edited { get; private set; }
    public bool Applied { get; set; }
    public Action<bool>? Closing { get; set; }

    private static string L(string english) => Loc.Ui(english);
    private static string N(double v, string format = "0.##") => v.ToString(format, CultureInfo.CurrentCulture);
    private static double? Parse(string? s) =>
        double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private void Preview() => Look.ApplyTheme(Edited.IsLight, Edited.AccentColor);

    // ---------- layout ----------

    private void Build()
    {
        _body.Children.Clear();
        _sections.Clear();
        Section("Appearance", "palette", AppearanceRows());
        Section("Viewport", "orbit", ViewportRows());
        Section("Snapping and picking", "snap", SnapRows());
        Section("Files", "save", FileRows());
        Section("General", "gear", GeneralRows());
        Section("Usage statistics", "chart", UsageRows());
        BuildNav();
    }

    private void Section(string title, string icon, IEnumerable<Control> rows)
    {
        var s = new StackPanel { Spacing = 12 };
        s.Children.Add(new TextBlock { Text = L(title), FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Look.Blue, Margin = new Thickness(0, 0, 0, 2) });
        foreach (var r in rows) s.Children.Add(r);
        s.Tag = icon;
        _sections[title] = s;
        _body.Children.Add(s);
    }

    private void BuildNav()
    {
        _nav.Children.Clear();
        foreach (var (title, section) in _sections)
        {
            var b = new Button
            {
                Content = Ui.Row(10, Look.Icon((string)section.Tag!, 16, Look.Muted), new TextBlock { Text = L(title), VerticalAlignment = VerticalAlignment.Center, Foreground = Look.TextSoft }),
                Height = 36,
                Padding = new Thickness(10, 0),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            b.Classes.Add("flat");
            // Scroll so the section heading is at the top (BringIntoView only scrolls until some of it shows).
            b.Click += (_, _) =>
            {
                if (section.TranslatePoint(new Point(0, 0), _body) is not Point top) return;
                var max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
                // The point is inside the body's margin, so the heading lands the same margin below the top.
                _scroll.Offset = new Vector(0, Math.Clamp(top.Y, 0, max));
            };
            _nav.Children.Add(b);
        }
    }

    /// <summary>Label on the left, editor on the right, optional hint under the label.</summary>
    private static Control Row(string label, Control editor, string? hint = null)
    {
        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = L(label), Foreground = Look.TextSoft, TextWrapping = TextWrapping.Wrap });
        if (hint != null) left.Children.Add(new TextBlock { Text = L(hint), FontSize = 11.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap });
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*"), MinHeight = 34 };
        Grid.SetColumn(editor, 1);
        editor.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(left);
        g.Children.Add(editor);
        return g;
    }

    private static Control Toggle(bool value, Action<bool> set, string? name = null)
    {
        var sw = new ToggleSwitch { IsChecked = value, OnContent = "", OffContent = "", HorizontalAlignment = HorizontalAlignment.Left, Name = name };
        sw.IsCheckedChanged += (_, _) => set(sw.IsChecked == true);
        return sw;
    }

    private static TextBox Number(string name, double value, Action<double> set, string? unit, double min, double max, string format = "0.##")
    {
        var box = new TextBox
        {
            Name = name,
            Width = 140,
            Height = 32,
            MinHeight = 32,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 5),
            FontFamily = Look.Mono,
            Background = Look.Field,
            BorderBrush = Look.LineStrong,
            Text = N(value, format),
            InnerRightContent = unit == null ? null : new TextBlock { Text = L(unit), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }
        };
        var shown = box.Text;
        void Commit()
        {
            if (box.Text == shown) return;
            if (Parse(box.Text) is not double v) { box.Text = shown; return; }
            v = Math.Clamp(v, min, max);
            set(v);
            box.Text = shown = N(v, format);
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { Commit(); box.SelectAll(); e.Handled = true; } };
        box.LostFocus += (_, _) => Commit();
        return box;
    }

    /// <summary>Preset swatches and a hex field; <paramref name="set"/> gets the new ARGB colour.</summary>
    private Control ColourPicker(string name, uint value, uint[] presets, Action<uint> set, bool rebuild = false)
    {
        var hex = new TextBox
        {
            Name = name,
            Width = 108,
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(8, 5),
            FontFamily = Look.Mono,
            Background = Look.Field,
            BorderBrush = Look.LineStrong,
            Text = $"#{value & 0xFFFFFF:X6}"
        };
        var current = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromUInt32(value)), BorderBrush = Look.LineStrong, BorderThickness = new Thickness(1) };
        // Wraps: the swatches go onto a second line in a narrow window instead of being cut off.
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(current);
        row.Children.Add(hex);
        var swatches = new List<Button>();
        void Pick(uint c)
        {
            c |= 0xFF000000;
            set(c);
            current.Background = new SolidColorBrush(Color.FromUInt32(c));
            hex.Text = $"#{c & 0xFFFFFF:X6}";
            foreach (var s in swatches) Mark(s, (uint)s.Tag! == c);
            if (rebuild) Build();
        }
        hex.LostFocus += (_, _) =>
        {
            var t = (hex.Text ?? "").Trim().TrimStart('#');
            if (t.Length == 8) t = t[2..];
            if (t.Length == 6 && uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c)) Pick(c);
            else hex.Text = $"#{value & 0xFFFFFF:X6}";
        };
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus(); e.Handled = true; } };
        var sep = new Border { Width = 1, Height = 22, Background = Look.Line, Margin = new Thickness(4, 5), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(sep);
        current.Margin = hex.Margin = new Thickness(0, 0, 8, 6);
        foreach (var p in presets)
        {
            var colour = p;
            var b = new Button
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(0, 4, 6, 10),
                Background = new SolidColorBrush(Color.FromUInt32(p)),
                Tag = p
            };
            Mark(b, p == value);
            ToolTip.SetTip(b, $"#{p & 0xFFFFFF:X6}");
            b.Click += (_, _) => Pick(colour);
            swatches.Add(b);
            row.Children.Add(b);
        }
        return row;
    }

    private static void Mark(Button b, bool on)
    {
        b.BorderBrush = on ? Look.Text : Look.LineStrong;
        b.BorderThickness = new Thickness(on ? 2 : 1);
    }

    // ---------- sections ----------

    private IEnumerable<Control> AppearanceRows()
    {
        var langs = Loc.Languages.ToList();
        var lang = new ComboBox { ItemsSource = langs.Select(l => l.Name).ToList(), SelectedIndex = langs.FindIndex(l => l.Code == Edited.Language), Width = 180, Height = 32, Name = "language" };
        lang.SelectionChanged += (_, _) => { if (lang.SelectedIndex >= 0) Edited.Language = langs[lang.SelectedIndex].Code; };
        yield return Row("Interface language", lang, "Applies after a restart");

        var themes = new WrapPanel();
        foreach (var (key, label) in new[] { (ProgramSettings.DarkTheme, "Dark"), (ProgramSettings.LightTheme, "Light") })
        {
            var k = key;
            var chip = Ui.Chip(L(label), () => SetTheme(k), Edited.Theme == key);
            chip.Margin = new Thickness(0, 0, 6, 0);
            chip.Tag = "theme:" + key;
            themes.Children.Add(chip);
        }
        yield return Row("Theme", themes);

        var reset = Ui.Flat(Ui.Row(6, Look.Icon("prev", 14, Look.Muted), new TextBlock { Text = L("Default"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }), L("Back to the default accent"),
            () => { Edited.AccentColor = ProgramSettings.DefaultAccent; Preview(); Build(); }, null, 30);
        var accent = new StackPanel { Spacing = 6 };
        accent.Children.Add(ColourPicker("accent", Edited.AccentColor, Accents, c => { Edited.AccentColor = c; Preview(); }));
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        accent.Children.Add(reset);
        accent.Children.Add(AccentPreview());
        yield return Row("Accent colour", accent, "Active tab, chips, main buttons, group captions");
    }

    private void SetTheme(string theme)
    {
        if (Edited.Theme == theme) return;
        var oldDefault = Edited.IsLight ? ProgramSettings.LightBackground : ProgramSettings.DarkBackground;
        Edited.Theme = theme;
        // A plan background left at the old theme's default follows the theme; one the user picked stays.
        if (Edited.BackgroundColor == oldDefault) Edited.BackgroundColor = Edited.IsLight ? ProgramSettings.LightBackground : ProgramSettings.DarkBackground;
        Preview();
        Build();
    }

    /// <summary>How the accent looks on the controls that use it.</summary>
    private static Control AccentPreview()
    {
        var primary = new Button { Content = L("Apply"), Height = 30, Padding = new Thickness(14, 0), CornerRadius = new CornerRadius(6), VerticalContentAlignment = VerticalAlignment.Center, Classes = { "primary" }, IsHitTestVisible = false };
        var chip = Ui.Chip(L("Chip"), () => { }, active: true);
        chip.IsHitTestVisible = false;
        var check = new CheckBox { Content = L("Check box"), IsChecked = true, IsHitTestVisible = false };
        var sw = new ToggleSwitch { IsChecked = true, OnContent = "", OffContent = "", IsHitTestVisible = false };
        var tab = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = L("Tab"), Foreground = Look.Accent, FontWeight = FontWeight.SemiBold },
                new Border { Height = 2, Background = Look.Accent, CornerRadius = new CornerRadius(1) }
            }
        };
        var caption = new TextBlock { Text = L("Group caption"), FontSize = 11, Foreground = Look.Accent, VerticalAlignment = VerticalAlignment.Center };
        var row = new WrapPanel { Children = { primary, chip, check, sw, tab, caption } };
        foreach (var c in row.Children.OfType<Control>()) c.Margin = new Thickness(0, 0, 14, 6);
        return new Border
        {
            Background = Look.Panel,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 0, 4),
            Child = row
        };
    }

    private IEnumerable<Control> ViewportRows()
    {
        yield return Row("Background colour", ColourPicker("background", Edited.BackgroundColor, Edited.IsLight ? LightBackgrounds : DarkBackgrounds, c => Edited.BackgroundColor = c));

        var byLayers = Edited.GeometryColor == null;
        var geometry = new StackPanel { Spacing = 8 };
        var picker = ColourPicker("geometry", Edited.GeometryColor ?? Geometry[0], Geometry, c => Edited.GeometryColor = c);
        picker.IsEnabled = !byLayers;
        geometry.Children.Add(Ui.Row(10, Toggle(byLayers, on =>
        {
            Edited.GeometryColor = on ? null : Geometry[0];
            Build();
        }, "geometryByLayers"), new TextBlock { Text = L("Layer colours"), Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center }));
        geometry.Children.Add(picker);
        yield return Row("Geometry colour", geometry, "Lines, points, polygons and text without their own colour");

        yield return Row("Selected objects colour", ColourPicker("selection", Edited.SelectionColor, Highlights, c => Edited.SelectionColor = c));

        var coloring = new WrapPanel();
        foreach (var (key, label) in new[] { (ProgramSettings.SurfaceByType, "By type"), (ProgramSettings.SurfaceByElevation, "By elevation") })
        {
            var k = key;
            var chip = Ui.Chip(L(label), () => { if (Edited.SurfaceColoring != k) { Edited.SurfaceColoring = k; Build(); } }, Edited.SurfaceColoring == key);
            chip.Margin = new Thickness(0, 0, 6, 0);
            chip.Tag = "surfaces:" + key;
            coloring.Children.Add(chip);
        }
        yield return Row("Surface colouring", coloring, Edited.SurfacesByType ? "Each kind has its colour; one height scale for all surfaces, darker at the lowest point, lighter at the highest" : "One scale for all surfaces: grey-blue at the lowest point, cream at the highest, whatever their kind");

        // One picker per kind; the presets are the default palette, so any kind can take another kind's colour.
        var palette = ProgramSettings.DefaultSurfaceColors.Values.ToArray();
        var kinds = new StackPanel { Spacing = 6, IsEnabled = Edited.SurfacesByType, Opacity = Edited.SurfacesByType ? 1 : 0.5 };
        foreach (var kind in Enum.GetValues<Domain.Model.SurfaceKind>())
        {
            var name = kind.ToString();
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*") };
            var label = new TextBlock { Text = Loc.Enum(kind), Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 8, 0) };
            var kindPicker = ColourPicker("surface" + name, Edited.SurfaceColor(name), palette, c => Edited.SurfaceColors[name] = c);
            Grid.SetColumn(kindPicker, 1);
            line.Children.Add(label);
            line.Children.Add(kindPicker);
            kinds.Children.Add(line);
        }
        var resetSurfaces = Ui.Flat(Ui.Row(6, Look.Icon("prev", 14, Look.Muted), new TextBlock { Text = L("Default palette"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }), L("Back to the default surface colours"),
            () => { Edited.SurfaceColors.Clear(); Build(); }, null, 30);
        resetSurfaces.HorizontalAlignment = HorizontalAlignment.Left;
        kinds.Children.Add(resetSurfaces);
        // Full width (title above, not in the label column): seven pickers would each wrap onto two lines otherwise.
        kinds.Children.Insert(0, new TextBlock { Text = L("Surface colours"), Foreground = Look.TextSoft, Margin = new Thickness(0, 0, 0, 2) });
        yield return kinds;
        yield return Row("Collar size", Number("collar", Edited.CollarSizePixels, v => Edited.CollarSizePixels = v, "px", 2, 40, "0.#"));
        yield return Row("Coordinates box", Toggle(Edited.ShowCoordinatesBox, v => Edited.ShowCoordinatesBox = v), "Cursor coordinates in the status bar");
    }

    private IEnumerable<Control> SnapRows()
    {
        yield return Row("Snap to objects", Toggle(Edited.SnapToObjects, v => Edited.SnapToObjects = v), "Key S in the viewport");
        yield return Row("Pick radius", Number("radius", Edited.TargetRadiusPixels, v => Edited.TargetRadiusPixels = v, "px", 2, 50, "0.#"), "Snapping reaches 1.5 times as far");
    }

    private IEnumerable<Control> FileRows()
    {
        yield return Row("Autosave", Number("autosave", Edited.AutosaveMinutes, v => Edited.AutosaveMinutes = (int)Math.Round(v), "min", 0, 120, "0"), "0 — off");
        yield return Row("Backup copies", Number("backups", Edited.BackupCopies, v => Edited.BackupCopies = (int)Math.Round(v), null, 0, 50, "0"));
        yield return Row("Recent files", Number("recent", Edited.MaxRecentFiles, v => Edited.MaxRecentFiles = (int)Math.Round(v), null, 1, 50, "0"));
        yield return Row("Startup assistant", Toggle(Edited.ShowStartupAssistant, v => Edited.ShowStartupAssistant = v), "Shown when the program starts");
    }

    private IEnumerable<Control> GeneralRows()
    {
        var author = new TextBox { Name = "author", Text = Edited.Author, Height = 32, MinHeight = 32, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Look.Field, BorderBrush = Look.LineStrong, Padding = new Thickness(8, 5) };
        author.TextChanged += (_, _) => Edited.Author = author.Text ?? "";
        yield return Row("Author", author, "In reports and the blast header");

        var folder = ProgramSettings.AppDataFolder;
        var open = Ui.Flat(Ui.Row(6, Look.Icon("open", 15, Look.Muted), new TextBlock { Text = L("Open"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }), L("Open the folder"), () =>
        {
            Directory.CreateDirectory(folder);
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }, null, 30);
        var path = new TextBlock { Text = folder, FontFamily = Look.Mono, FontSize = 11.5, Foreground = Look.Muted, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 330 };
        ToolTip.SetTip(path, folder);
        yield return Row("Settings folder", Ui.Row(8, path, open), "Logs, templates and import formats");
    }

    private IEnumerable<Control> UsageRows()
    {
        yield return Row("Record usage statistics", Toggle(Edited.UsageLogging, v => Edited.UsageLogging = v, "usageLogging"),
            "Buttons, menus, hot keys and view switches; kept on this computer only, 90 days");
        var open = Ui.Flat(Ui.Row(6, Look.Icon("chart", 15, Look.Muted), new TextBlock { Text = L("Open analysis…"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }), null, () => UsageWindow.Show(), null, 30);
        open.Name = "usageAnalysis";
        open.HorizontalAlignment = HorizontalAlignment.Left;
        yield return Row("Analysis", open, "Most used commands, flags switched back, long pauses");
    }

    // ---------- footer ----------

    private Control Footer()
    {
        var reset = Ui.Flat(Ui.Row(6, Look.Icon("prev", 14, Look.Muted), new TextBlock { Text = L("Reset everything to defaults"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center }),
            L("Nothing changes until Apply"), ResetAll, null, 32);
        var cancel = new Button
        {
            Content = Loc.T("Cancel"),
            Height = 32,
            Padding = new Thickness(14, 0),
            CornerRadius = new CornerRadius(6),
            BorderBrush = Look.LineStrong,
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsCancel = true
        };
        cancel.Click += (_, _) => Closing?.Invoke(false);
        var ok = new Button
        {
            Content = L("Apply"),
            Height = 32,
            Padding = new Thickness(16, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { "primary" },
            Tag = "apply"
        };
        ToolTip.SetTip(ok, L("Apply") + " (Ctrl+Enter)");
        ok.Click += (_, _) => { CommitFocused(); Closing?.Invoke(true); };
        var buttons = Ui.Row(8, cancel, ok);
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        dock.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = reset });
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 12), Child = dock };
    }

    /// <summary>Puts the settings on this window back to their defaults; recent files, pinned files and windows stay.</summary>
    public void ResetAll()
    {
        var d = new ProgramSettings();
        d.Upgrade();
        d.RecentFiles = Edited.RecentFiles;
        d.PinnedFiles = Edited.PinnedFiles;
        d.Window = Edited.Window;
        d.LastFolder = Edited.LastFolder;
        d.LastImportFolder = Edited.LastImportFolder;
        d.LastTemplate = Edited.LastTemplate;
        d.Language = Edited.Language;
        d.Author = Edited.Author;
        Edited = d;
        Preview();
        Build();
    }

    private void CommitFocused()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox box) box.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(LostFocusEvent));
    }
}
