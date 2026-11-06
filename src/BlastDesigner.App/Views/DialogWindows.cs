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

/// <summary>The page of hole and material types in the project properties (names, colours, default diameters and densities).</summary>
public static class TypeTableWindow
{
    public static Control Editor(List<TypeDefinition> holes, List<TypeDefinition> materials)
    {
        var he = new ListEditor<TypeDefinition>(holes, t => $"{t.Index}. {t.Name}", null, null);
        he.Grid.Exclude = new() { nameof(TypeDefinition.Index), nameof(TypeDefinition.Density) };
        he.Reload(0);
        var me = new ListEditor<TypeDefinition>(materials, t => $"{t.Index}. {t.Name}", null, null);
        me.Grid.Exclude = new() { nameof(TypeDefinition.Index), nameof(TypeDefinition.Diameter) };
        me.Reload(0);
        var host = new ContentControl { Content = he };
        Button holeChip = null!, materialChip = null!;
        void Show(bool hole)
        {
            host.Content = hole ? he : me;
            Ui.SetChip(holeChip, hole);
            Ui.SetChip(materialChip, !hole);
        }
        holeChip = Ui.Chip(Loc.T("Hole types"), () => Show(true), true);
        materialChip = Ui.Chip(Loc.T("Material types"), () => Show(false));
        var chips = Ui.Row(6, holeChip, materialChip);
        chips.Margin = new Thickness(12, 10);
        DockPanel.SetDock(chips, Dock.Top);
        return new DockPanel { Children = { chips, host } };
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

        var deckHost = new ContentControl { Height = 340 };
        // the deck list shows its selected row, and that must not scroll the fields of the rule down to it
        deckHost.AddHandler(Control.RequestBringIntoViewEvent, (_, e) => e.Handled = true);
        var hint = new TextBlock
        {
            Text = Loc.T("Decks are built from the bottom of the hole upward. Length 0 fills the column up to the top stemming."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Foreground = Look.Faint
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
            deckHost.Content = new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = de };
        }
        ruleEditor.SelectionChanged += ShowDecks;
        ShowDecks(ruleEditor.Selected);

        ruleEditor.AddBelow(FormKit.Section(Loc.Ui("Decks"), new StackPanel { Spacing = 8, Children = { hint, deckHost } }));
        return await Dialogs.Custom("Loading rules", ruleEditor, width: 1040, height: 780, form: true, bare: true) ? copy : null;
    }
}

/// <summary>File ▸ Import ▸ Text file: delimiter, header lines, data kind and column mapping with a preview.</summary>
public static class TextImportWindow
{
    public static async Task<TextImportFormat?> Show(string[] lines, string fileName)
    {
        var fmt = TextImportFormat.Guess(lines);
        var preview = new TextBox { IsReadOnly = true, FontFamily = Look.Mono, FontSize = 12, Background = Look.Field, BorderBrush = Look.LineStrong, Height = 160, Text = string.Join("\n", lines.Take(15)), TextWrapping = TextWrapping.NoWrap };
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
                        new TextBlock { Text = i < header.Length ? header[i] : $"#{i + 1}", FontWeight = FontWeight.SemiBold, Foreground = Look.TextSoft },
                        new TextBlock { Text = parts[i], FontSize = 12, Foreground = Look.Muted, Margin = new Thickness(0, 0, 0, 4) },
                        cb
                    }
                });
            }
        }
        delim.SelectionChanged += (_, _) => BuildColumns();
        skip.ValueChanged += (_, _) => BuildColumns();
        BuildColumns();

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
        async void SaveTemplate()
        {
            Directory.CreateDirectory(ProgramSettings.ImportFormatsFolder);
            var path = await Dialogs.SaveFile("Save import template", "format.json", ".json", ("Import template", new[] { "*.json" }));
            if (path != null) Collect().Save(path);
        }
        async void LoadTemplate()
        {
            var path = await Dialogs.OpenFile("Load import template", ("Import template", new[] { "*.json" }));
            if (path == null) return;
            fmt = TextImportFormat.Load(path);
            delim.SelectedIndex = Array.IndexOf(new[] { ',', ';', '\t', '|', ' ' }, fmt.Delimiter);
            skip.Value = fmt.SkipLines;
            kind.SelectedIndex = (int)fmt.Kind;
            dupes.IsChecked = fmt.SkipDuplicates;
            BuildColumns();
        }

        foreach (var c in new Control[] { delim, kind, skip })
        {
            c.Width = double.NaN;
            c.MinHeight = 32;
            c.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        var panel = new StackPanel
        {
            Spacing = 16,
            Margin = new Thickness(16, 14),
            Children =
            {
                FormKit.Section(Loc.Ui("Preview"), preview),
                FormKit.Section(Loc.Ui("Format"), new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        FormKit.Fields(3, FormKit.Labeled(Loc.T("Delimiter"), delim), FormKit.Labeled(Loc.T("Header lines"), skip), FormKit.Labeled(Loc.T("Data"), kind)),
                        FormKit.Fields(3, dupes, swap, comma)
                    }
                }),
                FormKit.Section(Loc.T("Assign a field to each column"), new ScrollViewer { Content = columns, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto })
            }
        };
        var templates = Ui.Row(6, FormKit.Tool("open", Loc.T("Load template…"), LoadTemplate), FormKit.Tool("save", Loc.T("Save template…"), SaveTemplate));
        return await Dialogs.Custom("Import text file", new ScrollViewer { Content = panel }, width: 980, height: 720, subtitle: fileName, form: true, bare: true, aside: templates) ? Collect() : null;
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

    /// <summary>The types offered, from the table of the project.</summary>
    private static List<CatalogueType> Types(MainViewModel vm) => vm.Project!.Catalogue.Types.Where(t => !t.Deleted).OrderBy(t => t.Order).ToList();

    /// <summary>The kinds a type lists, those deleted left out.</summary>
    private static List<string> KindsOf(MainViewModel vm, CatalogueType type) =>
        type.KindIds.Where(id => vm.Project!.Catalogue.Kinds.Any(k => k.Id == id && !k.Deleted)).ToList();

    private static string KindName(MainViewModel vm, string id) =>
        vm.Project!.Catalogue.Kinds.FirstOrDefault(k => k.Id == id) is { } k ? Loc.T(k.Name) : "";

    private static CatalogueType? TypeOf(MainViewModel vm)
    {
        var types = Types(vm);
        return types.FirstOrDefault(t => t.Id == vm.TieSystem)
            ?? (vm.ActiveTieDelayMs != null ? types.FirstOrDefault(t => t.UserDelay) : null)
            ?? types.FirstOrDefault(t => vm.ActiveConnector is { } a ? t.KindIds.Contains(a.KindId) : t.UserDelay)
            ?? types.FirstOrDefault();
    }

    /// <summary>The families of all the products of a type, those of its first kind first.</summary>
    private static List<IGrouping<(string KindId, string Name), Product>> Families(MainViewModel vm, CatalogueType type)
    {
        var kinds = KindsOf(vm, type);
        return vm.Project!.ActiveProducts.Where(p => kinds.Contains(p.KindId)).GroupBy(p => (p.KindId, Name: p.FamilyName))
            .OrderBy(g => kinds.IndexOf(g.Key.KindId)).ThenBy(g => g.Key.Name).ToList();
    }

    private static (string KindId, string Name) FamilyOf(MainViewModel vm, CatalogueType type, List<IGrouping<(string KindId, string Name), Product>> families) =>
        vm.ActiveConnector is { } a && vm.ActiveTieDelayMs == null && families.Any(g => g.Key == (a.KindId, a.FamilyName)) ? (a.KindId, a.FamilyName)
        : vm.TieFamilyChoice.TryGetValue(type.Id, out var f) && families.Any(g => g.Key == f) ? f
        : families.FirstOrDefault()?.Key ?? default;

    /// <summary>Makes new ties with the product (or the user's delay) and switches to the tie tool.</summary>
    private static void Use(MainViewModel vm, Product? p, double? delay, string type)
    {
        vm.ActiveConnector = p;
        vm.ActiveTieDelayMs = delay;
        vm.TieSystem = type;
        if (p != null) vm.TieFamilyChoice[type] = (p.KindId, p.FamilyName);
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
        var types = Types(vm);
        if (TypeOf(vm) is not { } typeInfo)
        {
            _panel.Children.Add(Card(new TextBlock { Text = Loc.T("No products of this type in Available resources"), Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap }));
            return;
        }
        var type = typeInfo.Id;

        // ---- current choice
        var current = vm.ActiveTieDelayMs is double ud ? Loc.F("{0} ms", ud.ToString("0.#"))
            : vm.ActiveConnector is { } ac ? $"{ac.FamilyName} · {VariantText(ac)}" : Loc.T("0 ms connection");
        var path = vm.ActiveTieDelayMs != null || vm.ActiveConnector == null ? Loc.T(typeInfo.Name)
            : $"{Loc.T(typeInfo.Name)}  ›  {KindName(vm, vm.ActiveConnector.KindId)}";
        var none = Ui.Chip(Loc.T("0 ms connection"), () => Use(vm, null, null, (types.FirstOrDefault(x => !x.UserDelay) ?? typeInfo).Id), vm.ActiveConnector == null && vm.ActiveTieDelayMs == null, Loc.T("A tie without a connector"));
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
        var typeChips = Wrap();
        typeChips.Name = "tieTypes";
        foreach (var item in types)
        {
            var chip = Ui.Chip(Loc.T(item.Name), () =>
            {
                if (TypeOf(vm)?.Id == item.Id) return;
                if (item.UserDelay) { Use(vm, null, vm.ActiveTieDelayMs ?? 0, item.Id); return; }
                // the type starts with the first variant of the chosen family
                var fams = Families(vm, item);
                var chosen = FamilyOf(vm, item, fams);
                Use(vm, fams.FirstOrDefault(g => g.Key == chosen)?.OrderBy(p => p.DelayMs).First(), null, item.Id);
            }, item.Id == type);
            chip.Tag = "type:" + item.Id;
            typeChips.Children.Add(chip);
        }
        _panel.Children.Add(Step("1", "Type", typeChips));

        if (typeInfo.UserDelay)
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
        var families = Families(vm, typeInfo);
        if (families.Count == 0)
        {
            _panel.Children.Add(Step("2", "Family", Card(new TextBlock { Text = Loc.T("No products of this type in Available resources"), Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap })));
            return;
        }
        var family = FamilyOf(vm, typeInfo, families);
        // a type of several kinds says the kind of each family
        var mixed = families.Select(g => g.Key.KindId).Distinct().Count() > 1;
        var famList = new StackPanel { Spacing = 4, Name = "tieFamilies" };
        foreach (var g in families)
        {
            var key = g.Key;
            var on = key == family;
            var note = string.Join(" · ", (mixed ? new[] { KindName(vm, key.KindId) } : Array.Empty<string>()).Concat(g.Select(p => p.Manufacturer).Where(m => m.Length > 0).Distinct()));
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
            row.Click += (_, _) => { if (key != FamilyOf(vm, typeInfo, families)) Use(vm, g.OrderBy(p => p.DelayMs).First(), null, type); };
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
