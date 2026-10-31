using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using BlastDesigner.Rendering;

namespace BlastDesigner.App.Views;

/// <summary>Edit ▸ Hole and material types (names, colours, default diameters and densities).</summary>
public static class TypeTableWindow
{
    public static async Task<(List<TypeDefinition> Holes, List<TypeDefinition> Materials)?> Show(string title, List<TypeDefinition> holes, List<TypeDefinition> materials)
    {
        var h = ProjectJson.DeepClone(holes);
        var m = ProjectJson.DeepClone(materials);
        var he = new ListEditor<TypeDefinition>(h, t => $"{t.Index}. {t.Name}", null, null);
        he.Grid.Exclude = new() { nameof(TypeDefinition.Index), nameof(TypeDefinition.Density) };
        he.Reload(0);
        var me = new ListEditor<TypeDefinition>(m, t => $"{t.Index}. {t.Name}", null, null);
        me.Grid.Exclude = new() { nameof(TypeDefinition.Index), nameof(TypeDefinition.Diameter) };
        me.Reload(0);
        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = Loc.T("Hole types"), Content = he },
                new TabItem { Header = Loc.T("Material types"), Content = me }
            }
        };
        return await Dialogs.Custom(title, tabs, width: 760, height: 520) ? (h, m) : null;
    }
}

/// <summary>Tools ▸ Auto loading ▸ Edit loading rules: rules with their deck templates.</summary>
public static class LoadingRulesWindow
{
    public static async Task<List<LoadingRule>?> Show(MainViewModel vm, List<LoadingRule> rules)
    {
        var copy = ProjectJson.DeepClone(rules);
        var rulesGrid = new PropertyGrid { Exclude = new() { nameof(LoadingRule.Id), nameof(LoadingRule.Decks) } };
        vm.AddProductChoices(rulesGrid);
        var ruleEditor = new ListEditor<LoadingRule>(copy, r => $"{r.Priority}. {r.Name}",
            () => new LoadingRule { Name = Loc.T("New rule"), Priority = copy.Count, StemmingProductId = vm.Project!.Products.FirstOrDefault(p => p.Kind == ProductKind.Stemming)?.Id },
            r => { var c = ProjectJson.DeepClone(r); c.Id = Guid.NewGuid(); c.Name += " (2)"; return c; }, rulesGrid);

        var deckHost = new ContentControl();
        var hint = new TextBlock
        {
            Text = Loc.T("Decks are built from the bottom of the hole upward. Length 0 fills the column up to the top stemming."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6)
        };
        void ShowDecks(LoadingRule? r)
        {
            if (r == null) { deckHost.Content = null; return; }
            var dg = new PropertyGrid { Exclude = new() };
            vm.AddProductChoices(dg);
            var de = new ListEditor<DeckTemplate>(r.Decks, d => $"{vm.Project!.FindProduct(d.ProductId)?.Name ?? "?"} {(d.Length > 0 ? d.Length.ToString("0.0") + " m" : Loc.T("fill"))}",
                () => new DeckTemplate { ProductId = vm.Project!.Products.FirstOrDefault(p => p.IsExplosive)?.Id },
                d => ProjectJson.DeepClone(d), dg);
            de.Reload(r.Decks.Count > 0 ? 0 : -1);
            deckHost.Content = de;
        }
        ruleEditor.SelectionChanged += ShowDecks;
        ShowDecks(ruleEditor.Selected);

        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto,*") };
        Grid.SetRow(ruleEditor, 0);
        Grid.SetRow(hint, 1);
        Grid.SetRow(deckHost, 2);
        grid.Children.Add(ruleEditor);
        grid.Children.Add(hint);
        grid.Children.Add(deckHost);
        return await Dialogs.Custom("Loading rules", grid, width: 900, height: 720) ? copy : null;
    }
}

/// <summary>File ▸ Import ▸ Text file: delimiter, header lines, data kind and column mapping with a preview.</summary>
public static class TextImportWindow
{
    public static async Task<TextImportFormat?> Show(string[] lines, string fileName)
    {
        var fmt = TextImportFormat.Guess(lines);
        var preview = new TextBox { IsReadOnly = true, FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"), Height = 160, Text = string.Join("\n", lines.Take(15)), TextWrapping = TextWrapping.NoWrap };
        var delim = new ComboBox { ItemsSource = new[] { Loc.T("Comma"), Loc.T("Semicolon"), Loc.T("Tab"), "|", Loc.T("Space") }, SelectedIndex = Array.IndexOf(new[] { ',', ';', '\t', '|', ' ' }, fmt.Delimiter) };
        var skip = new NumericUpDown { Value = fmt.SkipLines, Minimum = 0, Maximum = 100, Width = 120 };
        var kind = new ComboBox { ItemsSource = Enum.GetValues<ImportDataKind>().Select(k => Loc.Enum(k)).ToList(), SelectedIndex = (int)fmt.Kind };
        var dupes = new CheckBox { Content = Loc.T("Skip duplicate holes"), IsChecked = fmt.SkipDuplicates };
        var swap = new CheckBox { Content = Loc.T("Swap X and Y"), IsChecked = fmt.SwapXY };
        var comma = new CheckBox { Content = Loc.T("Decimal comma"), IsChecked = fmt.DecimalSeparator == "," };
        var columns = new WrapPanel();
        var combos = new List<ComboBox>();
        var fields = Enum.GetValues<ImportField>().ToList();

        void BuildColumns()
        {
            var d = new[] { ',', ';', '\t', '|', ' ' }[Math.Max(0, delim.SelectedIndex)];
            var sample = lines.Skip((int)(skip.Value ?? 0)).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
            var parts = TextFiles.SplitLine(sample, d);
            var header = (skip.Value ?? 0) > 0 && lines.Length > 0 ? TextFiles.SplitLine(lines[0], d) : Array.Empty<string>();
            columns.Children.Clear();
            combos.Clear();
            for (int i = 0; i < parts.Length; i++)
            {
                var cb = new ComboBox { ItemsSource = fields.Select(f => Loc.Enum(f)).ToList(), SelectedIndex = i < fmt.Columns.Count ? fields.IndexOf(fmt.Columns[i]) : 0, Width = 130 };
                combos.Add(cb);
                columns.Children.Add(new StackPanel
                {
                    Margin = new Thickness(0, 0, 6, 6),
                    Children =
                    {
                        new TextBlock { Text = i < header.Length ? header[i] : $"#{i + 1}", FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = parts[i], Foreground = Look.Muted },
                        cb
                    }
                });
            }
        }
        delim.SelectionChanged += (_, _) => BuildColumns();
        skip.ValueChanged += (_, _) => BuildColumns();
        BuildColumns();

        var load = new Button { Content = Loc.T("Load template…") };
        var save = new Button { Content = Loc.T("Save template…") };
        TextImportFormat Collect() => new()
        {
            Delimiter = new[] { ',', ';', '\t', '|', ' ' }[Math.Max(0, delim.SelectedIndex)],
            SkipLines = (int)(skip.Value ?? 0),
            Kind = (ImportDataKind)Math.Max(0, kind.SelectedIndex),
            Columns = combos.Select(c => fields[Math.Max(0, c.SelectedIndex)]).ToList(),
            SkipDuplicates = dupes.IsChecked == true,
            SwapXY = swap.IsChecked == true,
            DecimalSeparator = comma.IsChecked == true ? "," : "."
        };
        save.Click += async (_, _) =>
        {
            Directory.CreateDirectory(ProgramSettings.ImportFormatsFolder);
            var path = await Dialogs.SaveFile("Save import template", "format.json", ".json", ("Import template", new[] { "*.json" }));
            if (path != null) Collect().Save(path);
        };
        load.Click += async (_, _) =>
        {
            var path = await Dialogs.OpenFile("Load import template", ("Import template", new[] { "*.json" }));
            if (path == null) return;
            fmt = TextImportFormat.Load(path);
            delim.SelectedIndex = Array.IndexOf(new[] { ',', ';', '\t', '|', ' ' }, fmt.Delimiter);
            skip.Value = fmt.SkipLines;
            kind.SelectedIndex = (int)fmt.Kind;
            dupes.IsChecked = fmt.SkipDuplicates;
            BuildColumns();
        };

        var options = new WrapPanel
        {
            Children =
            {
                Labeled("Delimiter", delim), Labeled("Header lines", skip), Labeled("Data", kind),
                new StackPanel { Margin = new Thickness(0, 0, 12, 0), Children = { dupes, swap, comma } },
                new StackPanel { Spacing = 4, Children = { load, save } }
            }
        };
        var panel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = fileName, FontWeight = FontWeight.SemiBold },
                preview, options,
                new TextBlock { Text = Loc.T("Assign a field to each column"), Classes = { "caption" } },
                new ScrollViewer { Content = columns, MaxHeight = 260, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }
            }
        };
        return await Dialogs.Custom("Import text file", panel, width: 980, height: 680) ? Collect() : null;
    }

    private static Control Labeled(string label, Control c) =>
        new StackPanel { Margin = new Thickness(0, 0, 12, 0), Children = { new TextBlock { Text = Loc.T(label) }, c } };
}

/// <summary>Tools ▸ Report designer: choose sections and layout of the PDF report.</summary>
public static class ReportDesignerWindow
{
    public static async Task<ReportOptions?> Show(BlastProject project)
    {
        var opt = new ReportOptions { Title = Loc.T("Blast report") };
        var title = new TextBox { Text = opt.Title, MinWidth = 300 };
        var checks = Enum.GetValues<ReportSections>().Where(s => s is not ReportSections.None and not ReportSections.All)
            .Select(s => (s, new CheckBox { Content = Loc.Enum(s), IsChecked = true })).ToList();
        var landscape = new CheckBox { Content = Loc.T("Landscape") };
        var timing = new ComboBox { ItemsSource = Enum.GetValues<TimingLabelMode>().Select(m => Loc.Enum(m)).ToList(), SelectedIndex = (int)TimingLabelMode.InholeTime };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = Loc.T("Title") });
        panel.Children.Add(title);
        panel.Children.Add(new TextBlock { Text = Loc.T("Sections"), Classes = { "caption" } });
        foreach (var (_, cb) in checks) panel.Children.Add(cb);
        panel.Children.Add(landscape);
        panel.Children.Add(new TextBlock { Text = Loc.T("Timing labels on the plan") });
        panel.Children.Add(timing);
        if (!await Dialogs.Custom("Report designer", panel)) return null;
        opt.Title = title.Text ?? opt.Title;
        opt.Sections = checks.Where(c => c.Item2.IsChecked == true).Aggregate(ReportSections.None, (a, c) => a | c.s);
        opt.Landscape = landscape.IsChecked == true;
        opt.PlanTimingLabels = (TimingLabelMode)Math.Max(0, timing.SelectedIndex);
        return opt;
    }
}

/// <summary>Tie palette: picks the surface connector used by the Tie tool.</summary>
public static class TiePaletteWindow
{
    private static Window? _open;
    private static StackPanel? _panel;

    public static void Show(MainViewModel vm)
    {
        if (vm.Project == null) return;
        if (_open != null) { Fill(vm); _open.Activate(); return; }
        _panel = new StackPanel { Margin = new Thickness(8), Spacing = 4 };
        Fill(vm);
        _open = Dialogs.ShowWindow("Tie palette", new ScrollViewer { Content = _panel }, 280, 420);
        _open.Closed += (_, _) => { _open = null; _panel = null; };
    }

    // Rebuilds the list in place (to update the bold selection) without reopening the window, which would recentre it.
    private static void Fill(MainViewModel vm)
    {
        if (_panel == null || vm.Project == null) return;
        _panel.Children.Clear();
        void Add(Product? p)
        {
            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new Border { Width = 26, Height = 14, Background = new SolidColorBrush(Color.FromUInt32(p?.Color ?? 0xFF9CA3AF)), BorderBrush = Look.LineStrong, BorderThickness = new Thickness(1) },
                        new TextBlock { Text = p == null ? Loc.T("0 ms connection") : $"{p.Name} ({p.DelayMs:0} ms)" }
                    }
                },
                FontWeight = vm.ActiveConnector?.Id == p?.Id ? FontWeight.Bold : FontWeight.Normal
            };
            b.Click += (_, _) =>
            {
                vm.ActiveConnector = p;
                vm.SetStatus(Loc.F("Tie connector: {0}", p?.Name ?? Loc.T("0 ms connection")));
                vm.SetTool(new Tools.TieTool(vm));
                Fill(vm);
            };
            b.ContextMenu = new ContextMenu
            {
                ItemsSource = Tools.MenuEntry.Build(new[]
                {
                    Tools.MenuEntry.Item("Select product", () => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent))),
                    Tools.MenuEntry.Item("Clear", () => { vm.ActiveConnector = null; Fill(vm); })
                })
            };
            _panel!.Children.Add(b);
        }
        Add(null);
        foreach (var p in vm.Project.Products.Where(p => p.Kind == ProductKind.SurfaceConnector).OrderBy(p => p.DelayMs)) Add(p);
    }
}
