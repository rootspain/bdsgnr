using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlastDesigner.App.Views;

/// <summary>Layer manager: visibility, lock, freeze and text toggles, grouped by layer group, with a context menu.</summary>
public sealed class LayersPanel : DockPanel
{
    private readonly MainViewModel _vm;
    private readonly StackPanel _list = new();
    private readonly HashSet<string> _collapsed = new();

    public LayersPanel(MainViewModel vm)
    {
        _vm = vm;
        var header = new DockPanel { Margin = new Thickness(4) };
        var add = new Button { Content = "+", Classes = { "tool" } };
        ToolTip.SetTip(add, Loc.T("New layer"));
        add.Click += async (_, _) => { if (_vm.HasProject) await _vm.NewLayer(); };
        DockPanel.SetDock(add, Dock.Right);
        header.Children.Add(add);
        header.Children.Add(new TextBlock { Text = Loc.T("Layers"), Classes = { "caption" } });
        SetDock(header, Dock.Top);
        Children.Add(header);
        Children.Add(new ScrollViewer { Content = _list });
        vm.ModelRefreshed += Refresh;
    }

    public void Refresh()
    {
        _list.Children.Clear();
        var p = _vm.Project;
        if (p == null) return;
        // groups by name, but Topography just before Drawing and layers without a group last
        static string SortKey(string g) => g == StandardLayers.TopographyGroup ? "Drawing" : g;
        foreach (var group in p.Layers.GroupBy(l => l.Group).OrderBy(g => g.Key.Length == 0).ThenBy(g => SortKey(g.Key)).ThenBy(g => g.Key != StandardLayers.TopographyGroup))
        {
            var groupName = string.IsNullOrEmpty(group.Key) ? Loc.T("(no group)") : Loc.Name(group.Key);
            var collapsed = _collapsed.Contains(group.Key);
            var gh = new TextBlock { Text = (collapsed ? "▸ " : "▾ ") + groupName, FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 6, 4, 2), Cursor = new Cursor(StandardCursorType.Hand) };
            var key = group.Key;
            gh.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(gh).Properties.IsLeftButtonPressed) return;
                if (!_collapsed.Remove(key)) _collapsed.Add(key);
                Refresh();
            };
            gh.ContextMenu = new ContextMenu
            {
                ItemsSource = MenuEntry.Build(new[]
                {
                    MenuEntry.Item("Show group", () => _vm.ShowGroup(key, true)),
                    MenuEntry.Item("Hide group", () => _vm.ShowGroup(key, false)),
                    MenuEntry.Item("New layer", async () => await _vm.NewLayer(key)),
                    MenuEntry.Item("New group", async () => await _vm.NewGroup()),
                    MenuEntry.Item("Collapse all", () => { foreach (var l in p.Layers) _collapsed.Add(l.Group); Refresh(); }),
                    MenuEntry.Item("Expand all", () => { _collapsed.Clear(); Refresh(); })
                })
            };
            _list.Children.Add(gh);
            if (collapsed) continue;
            foreach (var layer in group) _list.Children.Add(LayerRow(p, layer));
        }
    }

    private Control LayerRow(BlastProject p, Layer layer)
    {
        var row = new DockPanel { Margin = new Thickness(10, 1, 4, 1), Background = Brushes.Transparent };
        var visible = new CheckBox { IsChecked = layer.Visible, Margin = new Thickness(0, 0, 4, 0) };
        visible.IsCheckedChanged += (_, _) => _vm.SetLayerFlag(layer, l => l.Visible = visible.IsChecked == true);
        ToolTip.SetTip(visible, Loc.T("Visible"));
        var swatch = new Border { Width = 12, Height = 12, Background = new SolidColorBrush(Color.FromUInt32(layer.Color)), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var count = p.Entities.Count(e => e.LayerId == layer.Id);
        var name = new TextBlock
        {
            Text = $"{Loc.Name(layer.Name)} ({count})",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = layer.Id == p.DefaultLayerId ? FontWeight.Bold : FontWeight.Normal,
            Foreground = layer.Frozen ? Look.Blue : layer.Locked ? Look.Faint : Look.Text
        };
        var flags = new TextBlock { Text = (layer.Locked ? "🔒" : "") + (layer.Frozen ? "❄" : ""), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(visible, Dock.Left);
        DockPanel.SetDock(swatch, Dock.Left);
        DockPanel.SetDock(flags, Dock.Right);
        row.Children.Add(visible);
        row.Children.Add(swatch);
        row.Children.Add(flags);
        row.Children.Add(name);
        row.DoubleTapped += async (_, _) => await _vm.EditLayer(layer);
        row.ContextMenu = new ContextMenu
        {
            ItemsSource = MenuEntry.Build(new[]
            {
                MenuEntry.Item("Edit", async () => await _vm.EditLayer(layer)),
                MenuEntry.Item("New layer", async () => await _vm.NewLayer(layer.Group)),
                MenuEntry.Item("New group", async () => await _vm.NewGroup()),
                MenuEntry.Item("Isolate", () => _vm.IsolateLayer(layer)),
                MenuEntry.Item("Make default", () => _vm.MakeDefaultLayer(layer)),
                MenuEntry.Separator,
                MenuEntry.Check("Visible", () => layer.Visible, () => _vm.SetLayerFlag(layer, l => l.Visible = !l.Visible)),
                MenuEntry.Check("Locked", () => layer.Locked, () => _vm.SetLayerFlag(layer, l => l.Locked = !l.Locked, true)),
                MenuEntry.Check("Frozen", () => layer.Frozen, () => _vm.SetLayerFlag(layer, l => l.Frozen = !l.Frozen, true)),
                MenuEntry.Check("Show text", () => layer.ShowText, () => _vm.SetLayerFlag(layer, l => l.ShowText = !l.ShowText)),
                MenuEntry.Separator,
                MenuEntry.Item("Select objects", () => _vm.SelectLayerObjects(layer, true)),
                MenuEntry.Item("Deselect objects", () => _vm.SelectLayerObjects(layer, false)),
                MenuEntry.Item("Move selection to this layer", () => _vm.MoveSelectionToLayer(layer), () => _vm.HasSelection),
                MenuEntry.Item("Delete objects", async () => await _vm.DeleteLayerObjects(layer)),
                MenuEntry.Item("Delete layer", async () => await _vm.DeleteLayer(layer))
            })
        };
        return row;
    }
}

/// <summary>Right-hand panel: properties of the selection, applied immediately as undoable edits.</summary>
public sealed class PropertiesPanel : DockPanel
{
    private readonly MainViewModel _vm;
    private readonly ContentControl _content = new();
    private readonly TextBlock _header = new() { Classes = { "caption" } };

    public PropertiesPanel(MainViewModel vm)
    {
        _vm = vm;
        SetDock(_header, Dock.Top);
        Children.Add(_header);
        Children.Add(new ScrollViewer { Content = _content });
        vm.SelectionRefreshed += Refresh;
    }

    public void Refresh()
    {
        if (!IsVisible) return;
        var doc = _vm.Doc;
        var sel = doc?.SelectedEntities.ToList() ?? new();
        if (doc == null)
        {
            _header.Text = "";
            _content.Content = null;
            return;
        }
        if (sel.Count == 0)
        {
            _header.Text = Loc.T("Blast");
            _content.Content = Summary(doc.Project);
            return;
        }
        var first = sel[0];
        if (sel.Any(e => e.GetType() != first.GetType()))
        {
            _header.Text = Loc.F("{0} objects", sel.Count);
            _content.Content = new StackPanel { Children = { LayerRow(doc, sel), new TextBlock { Text = string.Join("\n", sel.GroupBy(e => Loc.T(e.KindName)).Select(g => $"{g.Key}: {g.Count()}")), Margin = new Thickness(6) } } };
            return;
        }
        _header.Text = sel.Count == 1 ? Loc.T(first.KindName) : $"{Loc.T(first.KindName)} × {sel.Count}";
        var template = ProjectJson.DeepClone(first);
        if (template is Hole th && first is Hole fh) { th.FiringTimeMs = fh.FiringTimeMs; th.SurfaceTimeMs = fh.SurfaceTimeMs; th.FiringSequence = fh.FiringSequence; }
        var grid = new PropertyGrid { Exclude = new() { "Id", "LayerId", "GroupId", "Decks", "BoreTrack", "RowId", "FromId", "ToId", "HoleId", "Triangles", "Vertices", "Points", "FromPos", "ToPos", "Direction", "DesignToe", "GradePoint" }, ReadOnly = doc.IsReadOnly };
        _vm.AddProductChoices(grid);
        grid.SetTargets(template, sel);
        var ids = sel.Select(e => e.Id).ToHashSet();
        grid.PropertyEdited += name =>
        {
            _vm.Change(Loc.F("Edit {0}", Loc.Property(name)), p =>
            {
                foreach (var e in p.Entities.Where(e => ids.Contains(e.Id)))
                {
                    grid.ApplyTo(template, e);
                    if (e is SurfaceEntity s) s.InvalidateIndex();
                    // crests, toes, contours and boundaries live in the layers of their type, in its colour
                    if (name == nameof(PolylineEntity.Kind) && e is PolylineEntity line) SurveyRecognitionWindow.PutInKindLayer(p, line);
                }
            });
        };
        var panel = new StackPanel { Children = { LayerRow(doc, sel) } };
        if (first is Hole hole)
        {
            panel.Children.Add(new TextBlock { Text = Loc.T("Charge"), Classes = { "caption" } });
            var text = sel.Count == 1
                ? ChargeText(doc.Project, hole)
                : Loc.F("Explosive {0:0.0} kg", sel.OfType<Hole>().Sum(h => Charging.ExplosiveMass(doc.Project, h)));
            panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0), TextWrapping = TextWrapping.Wrap });
            var edit = new Button { Content = sel.Count == 1 ? Loc.T("Edit loading…") : Loc.F("Edit loading ({0} holes)…", sel.Count), Margin = new Thickness(6), IsEnabled = !doc.IsReadOnly };
            edit.Click += async (_, _) => await _vm.EditHoleLoading(hole);
            panel.Children.Add(edit);
        }
        if (first is TimingLineEntity line && sel.Count == 1)
        {
            // the line programs the holes it remembers; this adds the electronic holes no line programs yet
            var add = new Button { Content = Loc.T("Add holes"), Margin = new Thickness(6), IsEnabled = !doc.IsReadOnly, Name = "addHolesToLine" };
            ToolTip.SetTip(add, Loc.T("Add the holes with an electronic detonator that are on no timing line, program them from this line and select its holes"));
            add.Click += async (_, _) => await _vm.AddHolesToTimingLine(line);
            panel.Children.Add(add);
            var assigned = new Button { Content = Loc.T("Select assigned"), Margin = new Thickness(6, 0, 6, 6), Name = "selectLineHoles" };
            ToolTip.SetTip(assigned, Loc.T("Select the holes this timing line programs"));
            assigned.Click += (_, _) => _vm.SelectTimingLineHoles(line);
            panel.Children.Add(assigned);
        }
        panel.Children.Add(grid);
        _content.Content = panel;
    }

    /// <summary>
    /// The layer of the selection, laid out like a row of the property grid: picking another one moves the selection
    /// there; several layers show as mixed.
    /// </summary>
    private Control LayerRow(Document doc, List<Entity> sel)
    {
        var layers = doc.Project.Layers;
        var mixed = sel.Any(e => e.LayerId != sel[0].LayerId);
        var combo = new ComboBox
        {
            Name = "selectionLayer",
            ItemsSource = layers.ToList(),
            // a template, not ready controls: the box then draws the chosen layer like any other item
            // (no layer while the box is taken down with the panel)
            ItemTemplate = new FuncDataTemplate<Layer>((l, _) => l == null ? new TextBlock() : Ui.Row(8,
                new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromUInt32(l.Color)), BorderBrush = Look.LineStrong, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = Loc.Name(l.Name), VerticalAlignment = VerticalAlignment.Center })),
            SelectedIndex = mixed ? -1 : layers.FindIndex(l => l.Id == sel[0].LayerId),
            PlaceholderText = Loc.T("(mixed)"),
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            IsEnabled = !doc.IsReadOnly,
            Margin = new Thickness(0, 2)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            var layer = layers[combo.SelectedIndex];
            if (sel.Any(e => e.LayerId != layer.Id)) _vm.MoveSelectionToLayer(layer);
        };
        var label = new TextBlock { Text = Loc.T("Layer"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) };
        Grid.SetColumn(combo, 1);
        // the columns and margin of the property grid under it, so the row lines up with its fields
        return new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*"), Margin = new Thickness(4, 4, 4, 0), Children = { label, combo } };
    }

    private static string ChargeText(BlastProject p, Hole h)
    {
        if (h.Decks.Count == 0) return Loc.T("Not loaded");
        var lines = h.Decks.OrderBy(d => d.Top).Select(d => $"{d.Top:0.0}–{d.Bottom:0.0} m  {p.FindProduct(d.ProductId)?.Name}  {Charging.DeckMass(p, h, d):0.0} kg" + (d.Primers.Count > 0 ? $"  ({d.Primers.Count} pr.)" : ""));
        return string.Join("\n", lines) + "\n" + Loc.F("Explosive {0:0.0} kg", Charging.ExplosiveMass(p, h));
    }

    private static Control Summary(BlastProject p)
    {
        var holes = p.Holes.Where(h => !h.IsDummy).ToList();
        var text = new List<string>
        {
            p.Header.Name,
            Loc.F("Holes: {0}", holes.Count),
            Loc.F("Drilled: {0:0.0} m", holes.Sum(h => h.EffectiveLength)),
            Loc.F("Explosive: {0:0.0} kg", holes.Sum(h => Charging.ExplosiveMass(p, h))),
            Loc.F("Rows: {0}", p.Rows.Count),
            Loc.F("Ties: {0}", p.Ties.Count()),
            Loc.F("Layers: {0}", p.Layers.Count),
            Loc.F("Blast orientation: {0:0.0}°", p.Header.BlastOrientation),
        };
        var timed = holes.Where(h => h.FiringTimeMs.HasValue).ToList();
        if (timed.Count > 0) text.Add(Loc.F("Firing: {0:0}–{1:0} ms ({2} timed)", timed.Min(h => h.FiringTimeMs), timed.Max(h => h.FiringTimeMs), timed.Count));
        return new TextBlock { Text = string.Join("\n", text), Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap };
    }
}

/// <summary>Row of the hole table. Setters write back through undoable changes.</summary>
public sealed partial class HoleRowVm : ObservableObject
{
    private readonly MainViewModel _vm;
    public Hole Hole { get; }

    public HoleRowVm(MainViewModel vm, Hole hole)
    {
        _vm = vm;
        Hole = hole;
    }

    private void Set(string name, Action<Hole> apply)
    {
        var id = Hole.Id;
        _vm.Change(Loc.F("Edit {0}", Loc.Property(name)), p => { if (p.FindEntity(id) is Hole h) apply(h); });
    }

    public string Label { get => Hole.Label; set => Set(nameof(Label), h => h.Label = value); }
    public double X { get => Math.Round(Hole.Collar.X, 3); set => Set("Collar", h => h.Translate(new Vec3(value - h.Collar.X, 0, 0))); }
    public double Y { get => Math.Round(Hole.Collar.Y, 3); set => Set("Collar", h => h.Translate(new Vec3(0, value - h.Collar.Y, 0))); }
    public double Z { get => Math.Round(Hole.Collar.Z, 3); set => Set("Collar", h => h.Collar = h.Collar.WithZ(value)); }
    public double Angle { get => Hole.Angle; set => Set(nameof(Angle), h => h.Angle = value); }
    public double Bearing { get => Hole.Bearing; set => Set(nameof(Bearing), h => h.Bearing = value); }
    public double Length { get => Math.Round(Hole.Length, 2); set => Set(nameof(Length), h => h.Length = value); }
    public double? Dipped { get => Hole.ActualLength; set => Set("Actual length", h => h.ActualLength = value); }
    public double Diameter { get => Hole.Diameter; set => Set(nameof(Diameter), h => h.Diameter = value); }
    public double Subdrill { get => Hole.Subdrill; set => Set(nameof(Subdrill), h => h.Subdrill = value); }
    public int Type { get => Hole.HoleType; set => Set("Hole type", h => h.HoleType = Math.Clamp(value, 1, 8)); }
    public double Water { get => Hole.WaterDepth; set => Set("Water depth", h => h.WaterDepth = value); }
    public string State => Loc.Enum(Hole.State);
    public string ChargeKg => Charging.ExplosiveMass(_vm.Project!, Hole).ToString("0.0", CultureInfo.CurrentCulture);
    public string Time => Hole.FiringTimeMs?.ToString("0") ?? "";
    public string Sequence => Hole.FiringSequence?.ToString() ?? "";
    public string Row => _vm.Project!.Rows.FirstOrDefault(r => r.Id == Hole.RowId)?.Name ?? "";
}

/// <summary>Table view of holes (View ▸ Table view). Editable cells; selection is synchronised with the plan.</summary>
public sealed class HoleTable : DockPanel
{
    private readonly MainViewModel _vm;
    private readonly DataGrid _grid;
    private readonly TextBox _filter = new() { Watermark = Loc.T("Filter by label…"), Width = 200 };
    private bool _syncing;

    public HoleTable(MainViewModel vm)
    {
        _vm = vm;
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserSortColumns = true,
            CanUserResizeColumns = true,
            SelectionMode = DataGridSelectionMode.Extended,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            IsReadOnly = false
        };
        void Col(string header, string path, bool ro = false, string? fmt = null) =>
            _grid.Columns.Add(new DataGridTextColumn { Header = Loc.T(header), Binding = new Binding(path) { StringFormat = fmt, Mode = ro ? BindingMode.OneWay : BindingMode.TwoWay }, IsReadOnly = ro });
        Col("Hole", nameof(HoleRowVm.Label));
        Col("X", nameof(HoleRowVm.X), false, "0.000");
        Col("Y", nameof(HoleRowVm.Y), false, "0.000");
        Col("Z", nameof(HoleRowVm.Z), false, "0.000");
        Col("Angle", nameof(HoleRowVm.Angle), false, "0.#");
        Col("Bearing", nameof(HoleRowVm.Bearing), false, "0.#");
        Col("Length", nameof(HoleRowVm.Length), false, "0.00");
        Col("Dipped", nameof(HoleRowVm.Dipped), false, "0.00");
        Col("Diameter", nameof(HoleRowVm.Diameter), false, "0.#");
        Col("Subdrill", nameof(HoleRowVm.Subdrill), false, "0.00");
        Col("Type", nameof(HoleRowVm.Type));
        Col("Water", nameof(HoleRowVm.Water), false, "0.00");
        Col("State", nameof(HoleRowVm.State), true);
        Col("kg", nameof(HoleRowVm.ChargeKg), true);
        Col("Time", nameof(HoleRowVm.Time), true);
        Col("Seq.", nameof(HoleRowVm.Sequence), true);
        Col("Row", nameof(HoleRowVm.Row), true);
        _grid.SelectionChanged += (_, _) =>
        {
            if (_syncing || _vm.Doc == null) return;
            _syncing = true;
            _vm.Doc.Select(_grid.SelectedItems.OfType<HoleRowVm>().Select(r => r.Hole.Id));
            _syncing = false;
        };
        _filter.TextChanged += (_, _) => Refresh();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4), Spacing = 8 };
        top.Children.Add(new TextBlock { Text = Loc.T("Holes"), Classes = { "caption" } });
        top.Children.Add(_filter);
        var zoom = new Button { Content = Loc.T("Zoom to selection") };
        zoom.Click += (_, _) => _vm.ZoomSelection();
        top.Children.Add(zoom);
        SetDock(top, Dock.Top);
        Children.Add(top);
        Children.Add(_grid);
        vm.ModelRefreshed += () => { if (IsVisible) Refresh(); };
        vm.SelectionRefreshed += SyncSelection;
    }

    public void Refresh()
    {
        var p = _vm.Project;
        if (p == null) { _grid.ItemsSource = null; return; }
        var f = _filter.Text?.Trim();
        var rows = p.Holes.Where(h => string.IsNullOrEmpty(f) || h.Label.Contains(f, StringComparison.OrdinalIgnoreCase))
            .Select(h => new HoleRowVm(_vm, h)).ToList();
        _syncing = true;
        _grid.ItemsSource = new ObservableCollection<HoleRowVm>(rows);
        _syncing = false;
        SyncSelection();
    }

    private void SyncSelection()
    {
        if (!IsVisible || _syncing || _vm.Doc == null || _grid.ItemsSource is not IEnumerable<HoleRowVm> rows) return;
        _syncing = true;
        _grid.SelectedItems.Clear();
        foreach (var r in rows.Where(r => _vm.Doc.Selection.Contains(r.Hole.Id))) _grid.SelectedItems.Add(r);
        _syncing = false;
    }
}
