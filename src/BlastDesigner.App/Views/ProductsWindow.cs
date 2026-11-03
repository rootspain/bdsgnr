using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.App.Views;

/// <summary>Edit ▸ Available resources: the project's product catalogue, searchable and filtered by kind.</summary>
public static class ProductsWindow
{
    public static async Task<List<Product>?> Show(BlastProject project)
    {
        var editor = new ProductsEditor(project);
        var w = new Window
        {
            Title = Loc.T("Available resources"),
            Width = 980,
            Height = 680,
            MinWidth = 760,
            MinHeight = 480,
            Background = Look.Ground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = editor
        };
        editor.Closing = ok => { editor.Applied = ok; w.Close(); };
        w.Opened += (_, _) => editor.FocusSearch();
        await Dialogs.ShowModal(w);
        return editor.Applied ? editor.Products : null;
    }
}

/// <summary>Content of the resources window. Public so the UI smoke test can reach it.</summary>
public sealed class ProductsEditor : DockPanel
{
    /// <summary>Filter chips: one per group of kinds the user looks for together.</summary>
    private static readonly (string Name, ProductKind[] Kinds)[] Categories =
    {
        ("Explosives", new[] { ProductKind.BulkExplosive, ProductKind.PackagedExplosive }),
        ("Stemming", new[] { ProductKind.Stemming }),
        ("Gas bags", new[] { ProductKind.GasBag }),
        ("Boosters", new[] { ProductKind.Booster }),
        ("Detonators", new[] { ProductKind.DownholeDetonator, ProductKind.ElectronicDetonator, ProductKind.ElectricDetonator }),
        ("Surface connectors", new[] { ProductKind.SurfaceConnector }),
        ("Detonating cord", new[] { ProductKind.DetonatingCord, ProductKind.CordRelay }),
        ("Harness wire", new[] { ProductKind.HarnessWire }),
        ("Other", new[] { ProductKind.Other })
    };

    private static readonly uint[] Palette =
    {
        0xFFFFC107, 0xFFFF9800, 0xFFE91E63, 0xFFD32F2F, 0xFFAB47BC,
        0xFF1976D2, 0xFF00897B, 0xFF4CAF50, 0xFFFFEB3B, 0xFF9E9E9E
    };

    private static readonly IBrush Warn = new SolidColorBrush(Color.FromUInt32(0xFFF0C13A));
    private static readonly IBrush WarnSoft = new SolidColorBrush(Color.FromUInt32(0x1AF0C13A));

    private readonly BlastProject _project;
    private readonly List<Product> _original;
    private readonly Dictionary<Guid, (int Holes, int Ties, int Rules)> _usage;
    private readonly TextBox _search = new();
    private readonly WrapPanel _chips = new();
    private readonly StackPanel _list = new();
    private readonly StackPanel _props = new() { Spacing = 18, Margin = new Thickness(18, 16) };
    private readonly Border _confirm = new() { IsVisible = false };
    private readonly TextBlock _status = new() { Foreground = Look.Faint, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly HashSet<string> _filter = new();
    private readonly HashSet<ProductKind> _collapsed = new();
    private Button _add = null!, _dup = null!, _delete = null!, _up = null!, _down = null!;

    public ProductsEditor(BlastProject project)
    {
        _project = project;
        _original = project.Products.Select(p => p.Clone()).ToList();
        Products = project.Products.Select(p => p.Clone()).ToList();
        _usage = Usage(project);
        Selected = Products.FirstOrDefault();

        var footer = Footer();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("360,*") };
        var left = ListPane();
        var right = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = _props } };
        Grid.SetColumn(right, 1);
        body.Children.Add(left);
        body.Children.Add(right);
        Children.Add(body);

        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Refresh();
    }

    /// <summary>The edited catalogue (copies; the project changes only when the window is applied).</summary>
    public List<Product> Products { get; }
    public Product? Selected { get; private set; }
    public bool Applied { get; set; }
    public Action<bool>? Closing { get; set; }
    /// <summary>Products shown in the list with the current search and kind filter, in list order.</summary>
    public IReadOnlyList<Product> Visible => Products.Where(Matches).OrderBy(p => (int)p.Kind).ToList();

    private static string L(string english) => Loc.Ui(english);
    private static string N(double v, string format = "0.###") => v.ToString(format, CultureInfo.CurrentCulture);
    private static double? Parse(string? s) =>
        double.TryParse((s ?? "").Trim().Replace(" ", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public void FocusSearch() { _search.Focus(); _search.SelectAll(); }

    // ---------- usage ----------

    /// <summary>Per product: holes whose column or primers use it, ties made with it and loading rules that name it.</summary>
    private static Dictionary<Guid, (int Holes, int Ties, int Rules)> Usage(BlastProject p)
    {
        var result = new Dictionary<Guid, (int Holes, int Ties, int Rules)>();
        void Add(IEnumerable<Guid?> ids, int which)
        {
            foreach (var id in ids.OfType<Guid>().Distinct())
            {
                result.TryGetValue(id, out var u);
                result[id] = which switch { 0 => (u.Holes + 1, u.Ties, u.Rules), 1 => (u.Holes, u.Ties + 1, u.Rules), _ => (u.Holes, u.Ties, u.Rules + 1) };
            }
        }
        foreach (var h in p.Holes)
            Add(h.Decks.SelectMany(d => d.Primers.SelectMany(x => new[] { x.BoosterId, x.DetonatorId }).Append(d.ProductId)), 0);
        foreach (var t in p.Ties) Add(new[] { t.ProductId }, 1);
        foreach (var r in p.LoadingRules)
            Add(r.Decks.SelectMany(d => new[] { d.ProductId, d.BoosterId, d.DetonatorId })
                .Concat(new[] { r.StemmingProductId, r.WetAlternateProductId, r.HardAlternateProductId, r.SoftAlternateProductId }), 2);
        return result;
    }

    private string? UsageText(Product p)
    {
        if (!_usage.TryGetValue(p.Id, out var u) || u == default) return null;
        var parts = new List<string>();
        if (u.Holes > 0) parts.Add(Loc.F("in {0} holes", u.Holes));
        if (u.Ties > 0) parts.Add(Loc.F("in {0} ties", u.Ties));
        if (u.Rules > 0) parts.Add(Loc.F("in {0} loading rules", u.Rules));
        return L("Used") + ": " + string.Join(", ", parts);
    }

    // ---------- filter ----------

    private bool Matches(Product p)
    {
        if (_filter.Count > 0 && !Categories.Where(c => _filter.Contains(c.Name)).Any(c => c.Kinds.Contains(p.Kind))) return false;
        var q = (_search.Text ?? "").Trim();
        return q.Length == 0 || p.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || p.Manufacturer.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>Kind a new product gets without asking: the only kind of the only selected filter chip.</summary>
    private ProductKind? FilterKind =>
        _filter.Count == 1 && Categories.First(c => c.Name == _filter.First()).Kinds is { Length: 1 } k ? k[0] : null;

    // ---------- left pane ----------

    private Control ListPane()
    {
        _search.Watermark = L("Search by name or manufacturer…");
        _search.Height = 34;
        _search.Background = Look.Field;
        _search.BorderBrush = Look.LineStrong;
        _search.InnerLeftContent = new Border { Margin = new Thickness(8, 0, 2, 0), Child = Look.Icon("search", 15, Look.Faint) };
        _search.InnerRightContent = new Border { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Child = Look.Kbd("Ctrl+F") };
        _search.TextChanged += (_, _) => { FillList(); FillChips(); };
        _chips.Margin = new Thickness(0, 10, 0, 0);

        _add = Tool("plus", L("Add"), null, L("Add a resource"));
        _add.Tag = "add";
        _add.Click += (_, _) => { if (FilterKind is ProductKind k) AddProduct(k); };
        Ui.DropDown(_add, () => FilterKind != null
            ? Enumerable.Empty<MenuEntry>()
            : Enum.GetValues<ProductKind>().Select(k => MenuEntry.Item(Loc.Enum(k), () => AddProduct(k))));
        _dup = Tool("dup", null, Duplicate, L("Duplicate") + " (Ctrl+D)");
        _delete = Tool("trash", null, AskDelete, L("Delete") + " (Del)");
        _up = Tool("arrowup", null, () => Move(-1), L("Move up"));
        _down = Tool("arrowdown", null, () => Move(1), L("Move down"));
        var tools = Ui.Row(6, _add, _dup, _delete, _up, _down);

        var top = new StackPanel { Margin = new Thickness(14, 14, 14, 10), Children = { _search, _chips } };
        var bottom = new StackPanel { Spacing = 8, Margin = new Thickness(14, 10, 14, 14), Children = { _confirm, tools } };
        var dock = new DockPanel();
        SetDock(top, Dock.Top);
        SetDock(bottom, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(bottom);
        dock.Children.Add(new Border
        {
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(0, 1),
            Child = new ScrollViewer { Content = _list, Padding = new Thickness(8, 6) }
        });
        return dock;
    }

    private static Button Tool(string icon, string? text, Action? click, string tip)
    {
        var content = text == null ? Look.Icon(icon, 16, Look.TextSoft) : Ui.Row(6, Look.Icon(icon, 16, Look.TextSoft), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Content = content,
            Height = 32,
            MinWidth = 32,
            Padding = new Thickness(text == null ? 8 : 12, 0),
            CornerRadius = new CornerRadius(6),
            BorderBrush = Look.LineStrong,
            BorderThickness = new Thickness(1),
            Background = Look.Ribbon,
            Foreground = Look.Text,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        ToolTip.SetTip(b, tip);
        if (click != null) b.Click += (_, _) => Ui.Safe(click);
        return b;
    }

    private void FillChips()
    {
        _chips.Children.Clear();
        void Chip(string text, bool active, Action<bool> click)
        {
            var b = Ui.Chip(text, () => { }, active);
            b.Margin = new Thickness(0, 0, 6, 6);
            // Ctrl+click adds a kind to the filter; a plain click shows that kind only.
            b.AddHandler(PointerReleasedEvent, (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { click(e.KeyModifiers.HasFlag(KeyModifiers.Control)); e.Handled = true; } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            b.Click += (_, _) => click(false);
            _chips.Children.Add(b);
        }
        Chip(Loc.F("All {0}", Products.Count), _filter.Count == 0, _ => { _filter.Clear(); Refresh(); });
        foreach (var (name, kinds) in Categories)
        {
            var count = Products.Count(p => kinds.Contains(p.Kind));
            if (count == 0 && !_filter.Contains(name)) continue;
            var n = name;
            Chip($"{L(name)} {count}", _filter.Contains(name), ctrl =>
            {
                if (ctrl) { if (!_filter.Remove(n)) _filter.Add(n); }
                else if (_filter.Count == 1 && _filter.Contains(n)) _filter.Clear();
                else { _filter.Clear(); _filter.Add(n); }
                Refresh();
            });
        }
    }

    /// <summary>Main figure of a product for its kind, shown at the right of its list row.</summary>
    private static string KeyValue(Product p) => p.Kind switch
    {
        ProductKind.BulkExplosive or ProductKind.Stemming => N(p.Density, "0.00") + " " + L("g/cm³"),
        ProductKind.PackagedExplosive => N(p.UnitMass, "0.0#") + " " + L("kg"),
        ProductKind.Booster => N(p.UnitMass * 1000, "0") + " " + L("g"),
        ProductKind.DownholeDetonator or ProductKind.ElectronicDetonator or ProductKind.SurfaceConnector or ProductKind.CordRelay or ProductKind.ElectricDetonator => N(p.DelayMs, "0") + " " + L("ms"),
        ProductKind.DetonatingCord => N(p.Vod, "0") + " " + L("m/s"),
        _ => ""
    };

    private static Border Swatch(uint color, double size = 12) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(3),
        Background = new SolidColorBrush(Color.FromUInt32(color)),
        BorderBrush = Look.LineStrong,
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center
    };

    private void FillList()
    {
        _list.Children.Clear();
        var visible = Visible;
        if (visible.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = L("Nothing found"), Foreground = Look.Faint, Margin = new Thickness(8, 12), TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var group in visible.GroupBy(p => p.Kind))
        {
            var kind = group.Key;
            var collapsed = _collapsed.Contains(kind);
            var head = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 8, 6, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = Ui.Row(6,
                    Look.Icon(collapsed ? "right" : "down", 12, Look.Faint),
                    new TextBlock { Text = Loc.Enum(kind).ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = group.Count().ToString(CultureInfo.CurrentCulture), FontSize = 11, Foreground = Look.Faint, FontFamily = Look.Mono, VerticalAlignment = VerticalAlignment.Center })
            };
            head.Classes.Add("flat");
            head.Click += (_, _) => { if (!_collapsed.Remove(kind)) _collapsed.Add(kind); FillList(); };
            _list.Children.Add(head);
            if (collapsed) continue;
            foreach (var p in group) _list.Children.Add(Row(p));
        }
    }

    private Control Row(Product p)
    {
        var selected = p == Selected;
        var used = UsageText(p);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(new TextBlock { Text = KeyValue(p), FontFamily = Look.Mono, FontSize = 12, Foreground = selected ? Look.Text : Look.Muted, VerticalAlignment = VerticalAlignment.Center });
        var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = used != null ? Look.Accent : Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        if (used != null) ToolTip.SetTip(dot, used);
        right.Children.Add(dot);
        var dock = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(Ui.Row(10, Swatch(p.Color),
            new TextBlock { Text = string.IsNullOrWhiteSpace(p.Name) ? L("(no name)") : p.Name, Foreground = selected ? Look.Text : Look.TextSoft, FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }));
        var b = new Button
        {
            Content = dock,
            Height = 34,
            Padding = new Thickness(10, 0),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = selected ? Look.AccentSoft : Brushes.Transparent,
            BorderBrush = selected ? Look.Accent : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Tag = p
        };
        b.Classes.Add("flat");
        b.Click += (_, _) => Select(p);
        return b;
    }

    private void Select(Product? p)
    {
        Selected = p;
        _confirm.IsVisible = false;
        Refresh();
    }

    // ---------- right pane ----------

    private static Control Section(string title, params Control[] content)
    {
        var s = new StackPanel { Spacing = 10 };
        s.Children.Add(new TextBlock { Text = L(title).ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Faint });
        foreach (var c in content) s.Children.Add(c);
        return s;
    }

    private static Control Labeled(string label, Control field) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = L(label), FontSize = 12, Foreground = Look.Muted }, field }
    };

    private static Grid Fields(params Control[] cells)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,14,*") };
        for (int i = 0; i < cells.Length; i++)
        {
            if (i % 2 == 0)
            {
                if (i > 0) g.RowDefinitions.Add(new RowDefinition(10, GridUnitType.Pixel));
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }
            Grid.SetRow(cells[i], i / 2 * 2);
            Grid.SetColumn(cells[i], i % 2 * 2);
            g.Children.Add(cells[i]);
        }
        return g;
    }

    private static TextBox Box(string? unit, bool mono) => new()
    {
        Height = 32,
        MinHeight = 32,
        Padding = new Thickness(8, 5),
        FontFamily = mono ? Look.Mono : FontFamily.Default,
        Background = Look.Field,
        BorderBrush = Look.LineStrong,
        InnerRightContent = unit == null ? null : new TextBlock { Text = L(unit), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }
    };

    /// <summary>Number field: commits on Enter or when focus leaves it; bad input goes back to the last value.</summary>
    private TextBox Number(string name, double value, Action<double> set, string? unit, string format = "0.###", bool refreshList = false)
    {
        var box = Box(unit, mono: true);
        box.Name = name;
        box.Text = N(value, format);
        var shown = box.Text;
        void Commit()
        {
            if (box.Text == shown) return;
            if (Parse(box.Text) is not double v) { box.Text = shown; return; }
            set(v);
            box.Text = shown = N(v, format);
            if (refreshList) FillList();
            UpdateStatus();
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { Commit(); box.SelectAll(); e.Handled = true; } };
        box.LostFocus += (_, _) => Commit();
        return box;
    }

    private TextBox Text(string name, string value, Action<string> set)
    {
        var box = Box(null, mono: false);
        box.Name = name;
        box.Text = value;
        // The list row and the card title follow the name while it is typed.
        box.TextChanged += (_, _) => { set(box.Text ?? ""); FillList(); UpdateStatus(); };
        return box;
    }

    private void FillProps()
    {
        _props.Children.Clear();
        if (Selected is not { } p)
        {
            _props.Children.Add(new TextBlock { Text = Products.Count == 0 ? L("No resources yet: add one with the button on the left.") : L("Select a resource in the list."), Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap });
            return;
        }

        var used = UsageText(p);
        var title = new TextBlock { Text = string.IsNullOrWhiteSpace(p.Name) ? L("(no name)") : p.Name, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var kindChip = new Border
        {
            Height = 24,
            Padding = new Thickness(9, 0),
            CornerRadius = new CornerRadius(12),
            Background = Look.Ribbon,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = Loc.Enum(p.Kind), FontSize = 12, Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center }
        };
        var meta = Ui.Row(8, kindChip);
        if (used != null) meta.Children.Add(Ui.Row(6, new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Look.Accent, VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = used, FontSize = 12, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center }));
        _props.Children.Add(new StackPanel { Spacing = 8, Children = { Ui.Row(12, Swatch(p.Color, 22), title), meta } });

        var kinds = Enum.GetValues<ProductKind>();
        var kind = new ComboBox { ItemsSource = kinds.Select(k => Loc.Enum(k)).ToList(), SelectedIndex = Array.IndexOf(kinds, p.Kind), HorizontalAlignment = HorizontalAlignment.Stretch, Height = 32, Name = "kind" };
        kind.SelectionChanged += (_, _) =>
        {
            if (kind.SelectedIndex < 0 || kinds[kind.SelectedIndex] == p.Kind) return;
            p.Kind = kinds[kind.SelectedIndex];
            // The new kind may be hidden by the filter: show it rather than lose the product being edited.
            if (!Matches(p)) _filter.Clear();
            Refresh();
        };
        _props.Children.Add(Section("General",
            Labeled("Name", Text("name", p.Name, v => { p.Name = v; title.Text = string.IsNullOrWhiteSpace(v) ? L("(no name)") : v; })),
            Fields(
                Labeled("Manufacturer", Text("manufacturer", p.Manufacturer, v => p.Manufacturer = v)),
                Labeled("Kind", kind)),
            // the products of one kind and family are the variants of it in the initiation catalogue
            Fields(Labeled("Family", Text("family", p.Family, v => p.Family = v)), new Border())));

        var fields = KindFields(p);
        if (fields.Count > 0) _props.Children.Add(Section("Parameters", fields.ToArray()));

        var perKg = p.Kind is ProductKind.BulkExplosive or ProductKind.PackagedExplosive or ProductKind.Stemming;
        _props.Children.Add(Section("Cost and colour",
            Fields(Labeled("Cost", Number("cost", p.Cost, v => p.Cost = Math.Max(0, v), perKg ? "per kg" : "per unit", "0.##")), new Border()),
            ColourPicker(p)));
    }

    /// <summary>Fields that mean something for the product's kind; the rest stay in the data but are not shown.</summary>
    private List<Control> KindFields(Product p)
    {
        Control Density() => Labeled("Density", Number("density", p.Density, v => p.Density = Math.Max(0, v), "g/cm³", "0.00#", refreshList: true));
        Control Strength() => Labeled("Relative weight strength", Number("rws", p.RelativeWeightStrength, v => p.RelativeWeightStrength = Math.Max(0, v), "%", "0.#"));
        Control Energy() => Labeled("Energy", Number("energy", p.Energy, v => p.Energy = Math.Max(0, v), "MJ/kg", "0.0#"));
        Control Vod() => Labeled("Velocity of detonation", Number("vod", p.Vod, v => p.Vod = Math.Max(0, v), "m/s", "0"));
        Control Delay() => Labeled("Delay", Number("delay", p.DelayMs, v => p.DelayMs = Math.Max(0, v), "ms", "0.#", refreshList: true));
        Control UnitMass(string label, string unit, double scale) => Labeled(label, Number("unitMass", p.UnitMass * scale, v => p.UnitMass = Math.Max(0, v) / scale, unit, "0.###", refreshList: true));
        Control UnitLength() => Labeled("Cartridge length", Number("unitLength", p.UnitLength, v => p.UnitLength = Math.Max(0, v), "m", "0.###"));
        Control Water()
        {
            var sw = new ToggleSwitch { IsChecked = p.WaterResistant, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
            sw.IsCheckedChanged += (_, _) => { p.WaterResistant = sw.IsChecked == true; UpdateStatus(); };
            var d = new DockPanel { MinHeight = 32 };
            DockPanel.SetDock(sw, Dock.Right);
            d.Children.Add(sw);
            d.Children.Add(new TextBlock { Text = L("Water resistant"), Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center });
            return d;
        }
        return p.Kind switch
        {
            ProductKind.BulkExplosive => new() { Fields(Density(), Strength(), Energy(), Vod()), Water() },
            ProductKind.PackagedExplosive => new() { Fields(Density(), Strength(), Energy(), Vod(), UnitMass("Cartridge mass", "kg", 1), UnitLength()), Water() },
            ProductKind.Stemming => new() { Fields(Density(), new Border()) },
            ProductKind.Booster => new() { Fields(UnitMass("Mass", "g", 1000), Density(), Energy(), Vod()) },
            ProductKind.DownholeDetonator or ProductKind.SurfaceConnector or ProductKind.CordRelay or ProductKind.ElectricDetonator => new() { Fields(Delay(), new Border()) },
            ProductKind.HarnessWire => new(),
            ProductKind.ElectronicDetonator => new() { Fields(Labeled("Default delay", Number("delay", p.DelayMs, v => p.DelayMs = Math.Max(0, v), "ms", "0.#", refreshList: true)), new Border()) },
            ProductKind.DetonatingCord => new() { Fields(UnitMass("Core load", "g/m", 1000), Vod()) },
            ProductKind.GasBag => new(),
            _ => new() { Fields(Density(), Strength(), Energy(), Vod(), UnitMass("Unit mass", "kg", 1), UnitLength(), Delay(), new Border()), Water() }
        };
    }

    private Control ColourPicker(Product p)
    {
        var hex = Box(null, mono: true);
        hex.Name = "colour";
        hex.Width = 120;
        hex.Text = $"#{p.Color:X8}";
        void SetColour(uint c)
        {
            p.Color = c;
            FillProps();
            FillList();
            UpdateStatus();
        }
        hex.LostFocus += (_, _) =>
        {
            var t = (hex.Text ?? "").Trim().TrimStart('#');
            if (t.Length == 6) t = "FF" + t;
            if (uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c) && c != p.Color) SetColour(c);
            else hex.Text = $"#{p.Color:X8}";
        };
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus(); e.Handled = true; } };
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in Palette)
        {
            var colour = c;
            var b = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromUInt32(c)),
                BorderBrush = c == p.Color ? Look.Text : Look.LineStrong,
                BorderThickness = new Thickness(c == p.Color ? 2 : 1)
            };
            ToolTip.SetTip(b, $"#{c:X8}");
            b.Click += (_, _) => SetColour(colour);
            row.Children.Add(b);
        }
        var dock = new DockPanel();
        DockPanel.SetDock(hex, Dock.Right);
        hex.VerticalAlignment = VerticalAlignment.Top;
        dock.Children.Add(hex);
        dock.Children.Add(row);
        return Labeled("Colour", dock);
    }

    // ---------- actions ----------

    /// <summary>New product with sensible values for its kind, placed after the last one of that kind.</summary>
    public void AddProduct(ProductKind kind)
    {
        var p = new Product { Name = Loc.T("New product"), Kind = kind, Density = 0, Energy = 0, RelativeWeightStrength = 0 };
        switch (kind)
        {
            case ProductKind.BulkExplosive: p.Density = 0.85; p.RelativeWeightStrength = 100; p.Energy = 3.7; p.Vod = 3500; p.Color = 0xFFFFC107; break;
            case ProductKind.PackagedExplosive: p.Density = 1.15; p.RelativeWeightStrength = 85; p.Energy = 3.1; p.Vod = 4500; p.UnitMass = 2.5; p.UnitLength = 0.6; p.WaterResistant = true; p.Color = 0xFFAB47BC; break;
            case ProductKind.Stemming: p.Density = 1.8; p.Color = 0xFF9E9E9E; break;
            case ProductKind.GasBag: p.Color = 0xFFE0F7FA; break;
            case ProductKind.Booster: p.UnitMass = 0.4; p.Density = 1.6; p.Energy = 5; p.Vod = 7000; p.Color = 0xFFD32F2F; break;
            case ProductKind.DownholeDetonator: p.DelayMs = 500; p.Color = 0xFF1976D2; break;
            case ProductKind.ElectronicDetonator: p.Color = 0xFF00897B; break;
            case ProductKind.SurfaceConnector: p.DelayMs = 25; p.Color = 0xFFFFEB3B; break;
            case ProductKind.DetonatingCord: p.UnitMass = 0.01; p.Vod = 6500; p.Color = 0xFFFF5722; break;
            case ProductKind.CordRelay: p.DelayMs = 25; p.Color = 0xFFFFB300; break;
            case ProductKind.ElectricDetonator: p.DelayMs = 25; p.Color = 0xFF5C6BC0; break;
            case ProductKind.HarnessWire: p.Color = 0xFF78909C; break;
        }
        var last = Products.FindLastIndex(x => x.Kind == kind);
        Products.Insert(last < 0 ? Products.Count : last + 1, p);
        _collapsed.Remove(kind);
        _search.Text = "";
        if (!Matches(p)) _filter.Clear();
        Selected = p;
        Refresh();
        FocusLater(() => Field("name"));
    }

    private void Duplicate()
    {
        if (Selected is not { } p) return;
        var c = p.Clone();
        c.Id = Guid.NewGuid();
        c.Name += " (2)";
        Products.Insert(Products.IndexOf(p) + 1, c);
        Selected = c;
        Refresh();
    }

    /// <summary>Deletes at once when the product is not used; otherwise asks in a bar above the buttons.</summary>
    private void AskDelete()
    {
        if (Selected is not { } p) return;
        if (UsageText(p) is not string used) { Delete(p); return; }
        var yes = new Button { Content = L("Delete"), Height = 28, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromUInt32(0xFFE5484D)), Foreground = Brushes.White, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, Tag = "confirmDelete" };
        var no = new Button { Content = Loc.T("Cancel"), Height = 28, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, BorderBrush = Look.LineStrong, BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center };
        yes.Click += (_, _) => Delete(p);
        no.Click += (_, _) => _confirm.IsVisible = false;
        _confirm.Background = WarnSoft;
        _confirm.BorderBrush = Warn;
        _confirm.BorderThickness = new Thickness(1);
        _confirm.CornerRadius = new CornerRadius(8);
        _confirm.Padding = new Thickness(10, 8);
        _confirm.Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Row(8, Look.Icon("warn", 15, Warn), new TextBlock { Text = Loc.F("Delete \"{0}\"?", p.Name), FontWeight = FontWeight.SemiBold, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }),
                new TextBlock { Text = used + ". " + L("They will be left without this product."), FontSize = 12, Foreground = Look.TextSoft, TextWrapping = TextWrapping.Wrap },
                Ui.Row(6, yes, no)
            }
        };
        _confirm.IsVisible = true;
    }

    private void Delete(Product p)
    {
        var visible = Visible.ToList();
        var i = visible.IndexOf(p);
        Products.Remove(p);
        visible.Remove(p);
        _confirm.IsVisible = false;
        Selected = visible.Count == 0 ? Products.FirstOrDefault() : visible[Math.Clamp(i, 0, visible.Count - 1)];
        Refresh();
    }

    /// <summary>Moves the selected product past its neighbour of the same kind (the list is grouped by kind).</summary>
    private void Move(int dir)
    {
        if (Selected is not { } p) return;
        var i = Products.IndexOf(p);
        var j = i + dir;
        while (j >= 0 && j < Products.Count && (Products[j].Kind != p.Kind || !Matches(Products[j]))) j += dir;
        if (j < 0 || j >= Products.Count) return;
        (Products[i], Products[j]) = (Products[j], Products[i]);
        Refresh();
    }

    private TextBox? Field(string name) => _props.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == name);

    /// <summary>Focuses a field once the rebuilt panel is in the visual tree.</summary>
    private static void FocusLater(Func<TextBox?> find) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (find() is { } b) { b.Focus(); b.SelectAll(); } }, Avalonia.Threading.DispatcherPriority.Background);

    private void OnKey(object? sender, KeyEventArgs e)
    {
        var inText = e.Source is TextBox;
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control) { FocusSearch(); e.Handled = true; }
        else if (e.Key == Key.D && e.KeyModifiers == KeyModifiers.Control) { Duplicate(); e.Handled = true; }
        else if (e.Key == Key.Delete && !inText) { AskDelete(); e.Handled = true; }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control) { CommitFocused(); Closing?.Invoke(true); e.Handled = true; }
        else if (e.Key == Key.Escape && e.Source == _search && !string.IsNullOrEmpty(_search.Text)) { _search.Text = ""; e.Handled = true; }
    }

    private void CommitFocused()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox box) box.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(LostFocusEvent));
    }

    // ---------- footer ----------

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
        dock.Children.Add(_status);
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 12), Child = dock };
    }

    /// <summary>Products added, removed or changed compared with the catalogue the window opened with.</summary>
    public int ChangedCount
    {
        get
        {
            var before = _original.ToDictionary(p => p.Id);
            var now = Products.ToDictionary(p => p.Id);
            var removed = before.Keys.Count(id => !now.ContainsKey(id));
            var changed = now.Values.Count(p => !before.TryGetValue(p.Id, out var o) || !Same(o, p));
            return removed + changed;
        }
    }

    private static bool Same(Product a, Product b) =>
        a.Name == b.Name && a.Manufacturer == b.Manufacturer && a.Family == b.Family && a.Kind == b.Kind && a.Density == b.Density && a.RelativeWeightStrength == b.RelativeWeightStrength
        && a.Energy == b.Energy && a.Vod == b.Vod && a.DelayMs == b.DelayMs && a.UnitMass == b.UnitMass && a.UnitLength == b.UnitLength
        && a.WaterResistant == b.WaterResistant && a.Cost == b.Cost && a.Color == b.Color;

    private void UpdateStatus()
    {
        var changed = ChangedCount;
        var moved = changed == 0 && !_original.Select(p => p.Id).SequenceEqual(Products.Select(p => p.Id));
        _status.Inlines = new InlineCollection { new Run(Loc.F("Resources: {0}", Products.Count)) };
        if (changed > 0) _status.Inlines.Add(new Run("  ·  " + Loc.F("changed: {0}", changed)) { Foreground = Look.Accent });
        else if (moved) _status.Inlines.Add(new Run("  ·  " + L("order changed")) { Foreground = Look.Accent });
    }

    private void Refresh()
    {
        if (Selected != null && !Products.Contains(Selected)) Selected = null;
        FillChips();
        FillList();
        FillProps();
        UpdateStatus();
        _dup.IsEnabled = _delete.IsEnabled = _up.IsEnabled = _down.IsEnabled = Selected != null;
    }
}
