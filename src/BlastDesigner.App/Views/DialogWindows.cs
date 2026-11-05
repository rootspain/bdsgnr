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
    public static async Task<ReportOptions?> Show()
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
/// Type (initiation system, all the products of its kinds) → Family → Variant (a product of the family, by its delay);
/// a user-defined type takes a delay instead. The products come from Available resources, their family from the
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

    /// <summary>Types (initiation systems): key, label and the product kinds they list, the one they start with first (none for user defined).</summary>
    public static readonly (string Key, string Label, ProductKind[] Kinds)[] Systems =
    {
        ("ShockTube", "Shock tube", new[] { ProductKind.SurfaceConnector, ProductKind.DownholeDetonator, ProductKind.Other }),
        ("CordRelay", "Cord delay", new[] { ProductKind.CordRelay }),
        ("Cord", "Detonating cord system", new[] { ProductKind.DetonatingCord }),
        ("Electric", "Electric", new[] { ProductKind.ElectricDetonator }),
        ("Harness", "Harness wire", new[] { ProductKind.HarnessWire }),
        ("Ebs", "EBS detonators", new[] { ProductKind.ElectronicDetonator }),
        ("User", "User defined", Array.Empty<ProductKind>())
    };

    public static void Show(MainViewModel vm)
    {
        if (vm.Project == null) return;
        if (_open != null) { Fill(vm); _open.Activate(); return; }
        _panel = new StackPanel { Spacing = 14, Margin = new Thickness(16, 14, 16, 18) };
        Fill(vm);
        // where it was left last time (settings file), if that spot is still on a screen; it opens there directly,
        // so the centring of a new window does not move it (and write the centre over the saved place)
        PixelPoint? at = null;
        if (vm.Settings.Window.TryGetValue(PositionKey, out var saved) && saved.Split(',') is [var sx, var sy]
            && int.TryParse(sx, out var x) && int.TryParse(sy, out var y) && OnScreen(new PixelPoint(x + 40, y + 20)))
            at = new PixelPoint(x, y);
        _open = Dialogs.ShowWindow(Title, new ScrollViewer { Content = _panel, Background = Look.Ground }, 380, 640, at: at);
        _open.Background = Look.Ground;
        _open.PositionChanged += (_, e) => vm.Settings.Window[PositionKey] = $"{e.Point.X},{e.Point.Y}";
        _open.Closed += (_, _) => { _open = null; _panel = null; vm.Settings.Save(); };
    }

    /// <summary>The window's content, for the UI smoke test.</summary>
    public static Control? Content => _panel;

    /// <summary>Whether the point is on one of the screens (true when the screens are not known, as without a display).</summary>
    private static bool OnScreen(PixelPoint p) =>
        Dialogs.Owner?.Screens is not { } screens || screens.All.Count == 0 || screens.ScreenFromPoint(p) != null;

    private static string TypeOf(MainViewModel vm) =>
        vm.TieSystem ?? (vm.ActiveTieDelayMs != null ? "User" : Systems.FirstOrDefault(s => vm.ActiveConnector is { } a ? s.Kinds.Contains(a.Kind) : s.Kinds.Length == 0).Key ?? "ShockTube");

    /// <summary>The families of all the products of a type, those of its first kind first.</summary>
    private static List<IGrouping<(ProductKind Kind, string Name), Product>> Families(MainViewModel vm, string type)
    {
        var kinds = Systems.First(s => s.Key == type).Kinds;
        return vm.Project!.Products.Where(p => kinds.Contains(p.Kind)).GroupBy(p => (p.Kind, Name: p.FamilyName))
            .OrderBy(g => Array.IndexOf(kinds, g.Key.Kind)).ThenBy(g => g.Key.Name).ToList();
    }

    private static (ProductKind Kind, string Name) FamilyOf(MainViewModel vm, string type, List<IGrouping<(ProductKind Kind, string Name), Product>> families) =>
        vm.ActiveConnector is { } a && vm.ActiveTieDelayMs == null && families.Any(g => g.Key == (a.Kind, a.FamilyName)) ? (a.Kind, a.FamilyName)
        : vm.TieFamilyChoice.TryGetValue(type, out var f) && families.Any(g => g.Key == f) ? f
        : families.FirstOrDefault()?.Key ?? default;

    /// <summary>Makes new ties with the product (or the user's delay) and switches to the tie tool.</summary>
    private static void Use(MainViewModel vm, Product? p, double? delay, string type)
    {
        vm.ActiveConnector = p;
        vm.ActiveTieDelayMs = delay;
        vm.TieSystem = type;
        if (p != null) vm.TieFamilyChoice[type] = (p.Kind, p.FamilyName);
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
        foreach (var (key, label, kinds) in Systems)
        {
            var k = key;
            var chip = Ui.Chip(Loc.T(label), () =>
            {
                if (TypeOf(vm) == k) return;
                if (kinds.Length == 0) { Use(vm, null, vm.ActiveTieDelayMs ?? 0, k); return; }
                // the type starts with the first variant of the chosen family
                var fams = Families(vm, k);
                var chosen = FamilyOf(vm, k, fams);
                Use(vm, fams.FirstOrDefault(g => g.Key == chosen)?.OrderBy(p => p.DelayMs).First(), null, k);
            }, key == type);
            chip.Tag = "type:" + key;
            types.Children.Add(chip);
        }
        _panel.Children.Add(Step("1", "Type", types));

        if (typeInfo.Kinds.Length == 0)
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

        // ---- 2 Family
        var families = Families(vm, type);
        if (families.Count == 0)
        {
            _panel.Children.Add(Step("2", "Family", Card(new TextBlock { Text = Loc.T("No products of this type in Available resources"), Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap })));
            return;
        }
        var family = FamilyOf(vm, type, families);
        // a type of several kinds says the kind of each family
        var mixed = families.Select(g => g.Key.Kind).Distinct().Count() > 1;
        var famList = new StackPanel { Spacing = 4, Name = "tieFamilies" };
        foreach (var g in families)
        {
            var key = g.Key;
            var on = key == family;
            var note = string.Join(" · ", (mixed ? new[] { Loc.Enum(key.Kind) } : Array.Empty<string>()).Concat(g.Select(p => p.Manufacturer).Where(m => m.Length > 0).Distinct()));
            var row = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 8),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Background = on ? Look.AccentSoft : Look.Panel,
                BorderBrush = on ? Look.Accent : Look.Line,
                Tag = "family:" + key.Name,
                Content = new DockPanel
                {
                    Children =
                    {
                        new TextBlock { [DockPanel.DockProperty] = Dock.Right, Text = Loc.F("{0} var.", g.Count()), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center },
                        new StackPanel { Children =
                        {
                            new TextBlock { Text = key.Name, FontWeight = FontWeight.SemiBold, Foreground = on ? Look.Accent : Look.Text },
                            new TextBlock { Text = note, FontSize = 11, Foreground = Look.Faint, IsVisible = note.Length > 0 }
                        } }
                    }
                }
            };
            row.Classes.Add("flat");
            row.Click += (_, _) => { if (key != FamilyOf(vm, type, families)) Use(vm, g.OrderBy(p => p.DelayMs).First(), null, type); };
            famList.Children.Add(row);
        }
        _panel.Children.Add(Step("2", "Family", famList));

        // ---- 3 Variant
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
        _panel.Children.Add(Step("3", "Variant", variants));
    }
}
