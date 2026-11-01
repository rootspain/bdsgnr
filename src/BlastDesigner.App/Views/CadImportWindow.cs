using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using BlastDesigner.App.Services;
using BlastDesigner.IO;

namespace BlastDesigner.App.Views;

/// <summary>File ▸ Import ▸ DXF / DWG: what to bring from the drawing. The last choice is kept for the session.</summary>
public static class CadImportWindow
{
    private static CadImportOptions _last = new();

    public static async Task<CadImportOptions?> Show(string path)
    {
        var options = new CadImportOptions
        {
            MinPointDistance = _last.MinPointDistance, FacesToSurfaces = _last.FacesToSurfaces, ImportText = _last.ImportText,
            HiddenLayers = _last.HiddenLayers, SeparateFlatLines = _last.SeparateFlatLines
        };
        var form = new CadImportForm(path, options);
        var w = new Window
        {
            Title = Loc.T("DXF import options"),
            Width = 560,
            Height = 640,
            MinWidth = 460,
            MinHeight = 420,
            Background = Look.Ground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = form
        };
        form.Closing = ok => { form.Applied = ok; w.Close(); };
        await Dialogs.ShowModal(w);
        if (!form.Applied) return null;
        _last = options;
        return options;
    }
}

/// <summary>Content of the DXF / DWG import window. Public so the UI smoke test can reach it.</summary>
public sealed class CadImportForm : DockPanel
{
    private static readonly (CadHiddenLayers Mode, string Name, string Note)[] Modes =
    {
        (CadHiddenLayers.ImportHidden, "Import hidden", "Everything comes in; layers switched off in the drawing stay switched off"),
        (CadHiddenLayers.Skip, "Skip", "Only what the drawing shows — usually the survey itself"),
        (CadHiddenLayers.ImportVisible, "Import visible", "Everything comes in and is shown")
    };

    private readonly CadImportOptions _options;
    private readonly List<Button> _modeChips = new();
    private readonly TextBlock _modeNote = new() { FontSize = 11.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap, Name = "hiddenNote" };

    public CadImportForm(string path, CadImportOptions options)
    {
        _options = options;
        var info = new FileInfo(path);
        var size = info.Exists ? Loc.F("{0:0.0} MB", info.Length / 1048576.0) : "";
        var title = Ui.Row(10, Look.Icon("import", 20, Look.Accent), new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = Loc.T("Import DXF / DWG"), FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Look.Text },
                new TextBlock { Text = Path.GetFileName(path) + (size.Length > 0 ? "  ·  " + size : ""), FontSize = 11.5, Foreground = Look.Faint, TextTrimming = TextTrimming.CharacterEllipsis }
            }
        });
        var head = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(20, 14), Child = title };
        SetDock(head, Dock.Top);
        Children.Add(head);

        var footer = Footer();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        // hidden layers: three chips and what the chosen one does
        var modes = Ui.Row(6);
        foreach (var (mode, name, _) in Modes)
        {
            var chip = Ui.Chip(Loc.T(name), () => { _options.HiddenLayers = mode; RefreshMode(); }, _options.HiddenLayers == mode);
            chip.Height = 32;
            chip.Tag = "hidden:" + mode;
            _modeChips.Add(chip);
            modes.Children.Add(chip);
        }
        RefreshMode();

        var distance = new NumericUpDown
        {
            Name = "minDistance",
            Value = (decimal)_options.MinPointDistance,
            Minimum = 0,
            Maximum = 100,
            Increment = 0.1m,
            FormatString = "0.##",
            Width = 120,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center
        };
        distance.ValueChanged += (_, _) => _options.MinPointDistance = (double)(distance.Value ?? 0);

        var body = new StackPanel { Spacing = 14, Margin = new Thickness(20, 16, 20, 20) };
        body.Children.Add(Card("Drawing layers",
            new StackPanel { Spacing = 4, Children = { new TextBlock { Text = Loc.T("Layers switched off or frozen in the drawing"), FontSize = 12.5, Foreground = Look.Text }, _modeNote } },
            modes,
            Setting("separateFlat", "Separate flat lines", "Lines at elevation 0 (grid, design outline, annotation) go to layers \"name (2D)\" and stay out of the survey",
                _options.SeparateFlatLines, v => _options.SeparateFlatLines = v)));
        body.Children.Add(Card("Objects",
            Setting("faces", "Faces to surfaces", "3D faces of a layer become one surface", _options.FacesToSurfaces, v => _options.FacesToSurfaces = v),
            Setting("text", "Import text", "Labels and multi-line text, without the formatting codes", _options.ImportText, v => _options.ImportText = v)));
        body.Children.Add(Card("Lines",
            Row(Describe("Min point distance", "Line vertices closer than this are dropped; 0 keeps them all"),
                Ui.Row(6, distance, new TextBlock { Text = Loc.T("m"), Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center }))));
        Children.Add(new ScrollViewer { Content = body });

        AddHandler(KeyDownEvent, (_, e) =>
        {
            // Enter in the number field commits the number there; anywhere else it imports
            if (e.Key == Key.Enter && !(e.Source is Visual v && v.FindAncestorOfType<NumericUpDown>(true) != null)) { Closing?.Invoke(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Closing?.Invoke(false); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public bool Applied { get; set; }
    public Action<bool>? Closing { get; set; }

    private void RefreshMode()
    {
        for (int i = 0; i < Modes.Length; i++) Ui.SetChip(_modeChips[i], Modes[i].Mode == _options.HiddenLayers);
        _modeNote.Text = Loc.T(Modes.First(m => m.Mode == _options.HiddenLayers).Note);
    }

    private static Control Describe(string title, string note) => new StackPanel
    {
        Spacing = 2,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new TextBlock { Text = Loc.T(title), FontSize = 12.5, Foreground = Look.Text },
            new TextBlock { Text = Loc.T(note), FontSize = 11.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap }
        }
    };

    /// <summary>A description on the left, its control on the right.</summary>
    private static Control Row(Control left, Control right)
    {
        var dock = new DockPanel();
        right.VerticalAlignment = VerticalAlignment.Center;
        right.Margin = new Thickness(14, 0, 0, 0);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(left);
        return dock;
    }

    private static Control Setting(string name, string title, string note, bool value, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsChecked = value, OnContent = "", OffContent = "", Name = name };
        toggle.IsCheckedChanged += (_, _) => set(toggle.IsChecked == true);
        return Row(Describe(title, note), toggle);
    }

    private static Control Card(string title, params Control[] rows)
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(new TextBlock { Text = Loc.T(title).ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Faint });
        foreach (var r in rows) stack.Children.Add(r);
        return new Border
        {
            Background = Look.Panel,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 14),
            Child = stack
        };
    }

    private Control Footer()
    {
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
            Name = "cancel"
        };
        cancel.Click += (_, _) => Closing?.Invoke(false);
        var import = new Button
        {
            Content = Loc.T("Import"),
            Height = 32,
            Padding = new Thickness(18, 0),
            CornerRadius = new CornerRadius(6),
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { "primary" },
            Name = "import"
        };
        import.Click += (_, _) => Closing?.Invoke(true);
        var hint = new TextBlock { Text = Loc.T("Enter imports, Esc cancels"), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var buttons = Ui.Row(8, cancel, import);
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        dock.Children.Add(hint);
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 12), Child = dock };
    }
}
