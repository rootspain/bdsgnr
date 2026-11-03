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
/// <summary>
/// Initiation catalogue (was the tie palette): what new ties are made with, chosen down the hierarchy
/// Type (initiation system) → Subtype (product kind) → Family → Variant (a product of the family, by its delay); a
/// user-defined type takes a delay instead. The products come from Available resources, their family from the
/// product's Family field (or the name without its delay). The current choice is shown on top.
/// </summary>
public static class TiePaletteWindow
{
    private static Window? _open;
    private static StackPanel? _panel;
    // a fresh radio-like rebuild counter (kept for tests and to avoid stale handlers)
    private static int _build;
    /// <summary>Key of the window position in the settings file (ProgramSettings.Window).</summary>
    public const string PositionKey = "TiePalette";
    public const string Title = "Initiation catalogue";

    /// <summary>Types (initiation systems): key, label and the subtype they start with (none for user defined).</summary>
    public static readonly (string Key, string Label, ProductKind? Kind)[] Systems =
    {
        ("ShockTube", "Shock tube", ProductKind.SurfaceConnector),
        ("CordRelay", "Cord delay", ProductKind.CordRelay),
        ("Cord", "Detonating cord system", ProductKind.DetonatingCord),
        ("Electric", "Electric", ProductKind.ElectricDetonator),
        ("Harness", "Harness wire", ProductKind.HarnessWire),
        ("Ebs", "EBS detonators", ProductKind.ElectronicDetonator),
        ("User", "User defined", null)
    };

    /// <summary>Subtypes offered: the product kinds that make or carry a tie.</summary>
    public static readonly ProductKind[] Subtypes =
    {
        ProductKind.SurfaceConnector, ProductKind.CordRelay, ProductKind.DetonatingCord, ProductKind.ElectricDetonator,
        ProductKind.HarnessWire, ProductKind.ElectronicDetonator, ProductKind.DownholeDetonator, ProductKind.Other
    };

    public static void Show(MainViewModel vm)
    {
        if (vm.Project == null) return;
        if (_open != null) { Fill(vm); _open.Activate(); return; }
        _panel = new StackPanel { Spacing = 14, Margin = new Thickness(16, 14, 16, 18) };
        Fill(vm);
        _open = Dialogs.ShowWindow(Title, new ScrollViewer { Content = _panel, Background = Look.Ground }, 380, 640);
        _open.Background = Look.Ground;
        // where it was left last time (settings file), if that spot is still on a screen
        if (vm.Settings.Window.TryGetValue(PositionKey, out var saved) && saved.Split(',') is [var sx, var sy]
            && int.TryParse(sx, out var x) && int.TryParse(sy, out var y) && _open.Screens.ScreenFromPoint(new PixelPoint(x + 40, y + 20)) != null)
            _open.Position = new PixelPoint(x, y);
        _open.PositionChanged += (_, e) => vm.Settings.Window[PositionKey] = $"{e.Point.X},{e.Point.Y}";
        _open.Closed += (_, _) => { _open = null; _panel = null; vm.Settings.Save(); };
    }

    /// <summary>The window's content, for the UI smoke test.</summary>
    public static Control? Content => _panel;

    private static string TypeOf(MainViewModel vm) =>
        vm.TieSystem ?? (vm.ActiveTieDelayMs != null ? "User" : Systems.FirstOrDefault(s => s.Kind == vm.ActiveConnector?.Kind).Key ?? "ShockTube");

    private static ProductKind SubtypeOf(MainViewModel vm, string type) =>
        vm.ActiveTieDelayMs == null && vm.ActiveConnector?.Kind is ProductKind k && vm.TieSystem == type ? k
        : vm.TieSubtypes.TryGetValue(type, out var s) ? s : Systems.First(x => x.Key == type).Kind ?? ProductKind.SurfaceConnector;

    private static List<IGrouping<string, Product>> Families(MainViewModel vm, ProductKind kind) =>
        vm.Project!.Products.Where(p => p.Kind == kind).GroupBy(p => p.FamilyName).OrderBy(g => g.Key).ToList();

    private static string FamilyOf(MainViewModel vm, ProductKind kind, List<IGrouping<string, Product>> families) =>
        vm.ActiveConnector is { } a && a.Kind == kind && vm.ActiveTieDelayMs == null ? a.FamilyName
        : vm.TieFamilyChoice.TryGetValue(kind, out var f) && families.Any(g => g.Key == f) ? f
        : families.FirstOrDefault()?.Key ?? "";

    /// <summary>Makes new ties with the product (or the user's delay) and switches to the tie tool.</summary>
    private static void Use(MainViewModel vm, Product? p, double? delay, string type)
    {
        vm.ActiveConnector = p;
        vm.ActiveTieDelayMs = delay;
        vm.TieSystem = type;
        if (p != null) { vm.TieSubtypes[type] = p.Kind; vm.TieFamilyChoice[p.Kind] = p.FamilyName; }
        vm.SetStatus(Loc.F("Tie connector: {0}", delay is double ms ? Loc.F("{0} ms", ms.ToString("0.#")) : p?.Name ?? Loc.T("0 ms connection")));
        if (vm.ActiveTool is not Tools.TieTool) vm.SetTool(new Tools.TieTool(vm));
        Fill(vm);
    }

    private static string VariantText(Product p) => p.Kind switch
    {
        ProductKind.DetonatingCord => $"{p.Vod:0} {Loc.T("m/s")}",
        ProductKind.HarnessWire or ProductKind.ElectronicDetonator => p.Name,
        _ => Loc.F("{0} ms", p.DelayMs.ToString("0.#"))
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = Loc.T(text).ToUpper(System.Globalization.CultureInfo.CurrentCulture),
        FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Blue, Margin = new Thickness(2, 0, 0, 6)
    };

    private static Control Step(string number, string title, Control content) => new StackPanel
    {
        Children =
        {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children =
            {
                new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Look.AccentSoft, Child = new TextBlock { Text = number, FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Look.Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
                Caption(title)
            } },
            content
        }
    };

    private static Border Card(Control child, string? name = null) => new()
    {
        Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
        Padding = new Thickness(12, 10), Child = child, Name = name
    };

    private static WrapPanel Wrap() => new() { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };

    // Rebuilds the content in place (to show the choice) without reopening the window, which would recentre it.
    private static void Fill(MainViewModel vm)
    {
        if (_panel == null || vm.Project == null) return;
        _build++;
        _panel.Children.Clear();
        var type = TypeOf(vm);
        var typeInfo = Systems.First(s => s.Key == type);

        // ---- current choice
        var current = vm.ActiveTieDelayMs is double ud ? Loc.F("{0} ms", ud.ToString("0.#"))
            : vm.ActiveConnector is { } ac ? $"{ac.FamilyName} · {VariantText(ac)}" : Loc.T("0 ms connection");
        var path = vm.ActiveTieDelayMs != null || vm.ActiveConnector == null ? Loc.T(typeInfo.Label)
            : $"{Loc.T(typeInfo.Label)}  ›  {Loc.Enum(vm.ActiveConnector.Kind)}";
        var none = Ui.Chip(Loc.T("0 ms connection"), () => Use(vm, null, null, "ShockTube"), vm.ActiveConnector == null && vm.ActiveTieDelayMs == null, Loc.T("A tie without a connector"));
        none.Tag = "none";
        _panel.Children.Add(Card(new DockPanel
        {
            Children =
            {
                new Border { [DockPanel.DockProperty] = Dock.Left, Width = 10, Height = 38, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 10, 0),
                    Background = new SolidColorBrush(Color.FromUInt32(vm.ActiveTieDelayMs == null ? vm.ActiveConnector?.Color ?? 0xFF9CA3AF : 0xFF9CA3AF)) },
                new Border { [DockPanel.DockProperty] = Dock.Right, VerticalAlignment = VerticalAlignment.Center, Child = none },
                new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
                {
                    new TextBlock { Text = current, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, TextTrimming = TextTrimming.CharacterEllipsis, Name = "tieCurrent" },
                    new TextBlock { Text = path, FontSize = 11.5, Foreground = Look.Faint, TextTrimming = TextTrimming.CharacterEllipsis }
                } }
            }
        }, "tieCurrentCard"));

        // ---- 1 Type
        var types = Wrap();
        types.Name = "tieTypes";
        foreach (var (key, label, kind) in Systems)
        {
            var k = key;
            var chip = Ui.Chip(Loc.T(label), () =>
            {
                if (TypeOf(vm) == k) return;
                if (kind == null) { Use(vm, null, vm.ActiveTieDelayMs ?? 0, k); return; }
                // the type starts with its subtype and the first variant of the chosen family
                var sub = vm.TieSubtypes.TryGetValue(k, out var s) ? s : kind.Value;
                var fams = Families(vm, sub);
                var fam = vm.TieFamilyChoice.TryGetValue(sub, out var f) ? fams.FirstOrDefault(g => g.Key == f) ?? fams.FirstOrDefault() : fams.FirstOrDefault();
                Use(vm, fam?.OrderBy(p => p.DelayMs).First(), null, k);
            }, key == type);
            chip.Tag = "type:" + key;
            types.Children.Add(chip);
        }
        _panel.Children.Add(Step("1", "Type", types));

        if (typeInfo.Kind == null)
        {
            // user defined: a delay of its own on every new tie
            var box = new TextBox { Text = (vm.ActiveTieDelayMs ?? 0).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture), Width = 120, Height = 32, FontFamily = Look.Mono, Name = "tieUserDelay" };
            void Commit()
            {
                if (double.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var v) && v >= 0 && v != vm.ActiveTieDelayMs) Use(vm, null, v, type);
            }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(); e.Handled = true; } };
            _panel.Children.Add(Step("2", "Delay", Card(Ui.Row(8, box, new TextBlock { Text = Loc.T("ms"), Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center }))));
            return;
        }

        // ---- 2 Subtype
        var subtype = SubtypeOf(vm, type);
        var subtypes = Wrap();
        subtypes.Name = "tieSubtypes";
        foreach (var kind in Subtypes)
        {
            var kk = kind;
            var count = vm.Project.Products.Count(p => p.Kind == kind);
            var chip = Ui.Chip(count > 0 ? $"{Loc.Enum(kind)}  {count}" : Loc.Enum(kind), () =>
            {
                if (SubtypeOf(vm, type) == kk) return;
                vm.TieSubtypes[type] = kk;
                var fams = Families(vm, kk);
                var fam = vm.TieFamilyChoice.TryGetValue(kk, out var f) ? fams.FirstOrDefault(g => g.Key == f) ?? fams.FirstOrDefault() : fams.FirstOrDefault();
                if (fam != null) Use(vm, fam.OrderBy(p => p.DelayMs).First(), null, type);
                else { vm.TieSystem = type; Fill(vm); }
            }, kind == subtype);
            chip.Tag = "subtype:" + kind;
            if (count == 0) chip.Opacity = 0.55;
            subtypes.Children.Add(chip);
        }
        _panel.Children.Add(Step("2", "Subtype", subtypes));

        // ---- 3 Family
        var families = Families(vm, subtype);
        if (families.Count == 0)
        {
            _panel.Children.Add(Step("3", "Family", Card(new TextBlock { Text = Loc.T("No products of this family in Available resources"), Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap })));
            return;
        }
        var family = FamilyOf(vm, subtype, families);
        var famList = new StackPanel { Spacing = 4, Name = "tieFamilies" };
        foreach (var g in families)
        {
            var key = g.Key;
            var on = key == family;
            var row = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 8),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Background = on ? Look.AccentSoft : Look.Panel,
                BorderBrush = on ? Look.Accent : Look.Line,
                Tag = "family:" + key,
                Content = new DockPanel
                {
                    Children =
                    {
                        new TextBlock { [DockPanel.DockProperty] = Dock.Right, Text = Loc.F("{0} var.", g.Count()), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center },
                        new StackPanel { Children =
                        {
                            new TextBlock { Text = key, FontWeight = FontWeight.SemiBold, Foreground = on ? Look.Accent : Look.Text },
                            new TextBlock { Text = string.Join(", ", g.Select(p => p.Manufacturer).Where(m => m.Length > 0).Distinct()), FontSize = 11, Foreground = Look.Faint, IsVisible = g.Any(p => p.Manufacturer.Length > 0) }
                        } }
                    }
                }
            };
            row.Classes.Add("flat");
            row.Click += (_, _) => { if (key != FamilyOf(vm, subtype, families)) Use(vm, g.OrderBy(p => p.DelayMs).First(), null, type); };
            famList.Children.Add(row);
        }
        _panel.Children.Add(Step("3", "Family", famList));

        // ---- 4 Variant
        var variants = Wrap();
        variants.Name = "tieVariants";
        foreach (var p in families.First(g => g.Key == family).OrderBy(p => p.DelayMs))
        {
            var prod = p;
            var on = vm.ActiveTieDelayMs == null && vm.ActiveConnector?.Id == p.Id;
            var chip = Ui.Chip("", () => Use(vm, prod, null, type), on, p.Name);
            chip.Content = Ui.Row(6, new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromUInt32(p.Color)), BorderBrush = Look.LineStrong, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = VariantText(p), VerticalAlignment = VerticalAlignment.Center });
            chip.Tag = "variant:" + p.Id;
            variants.Children.Add(chip);
        }
        _panel.Children.Add(Step("4", "Variant", variants));
    }
}
