using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
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
    /// <summary>
    /// Tool tip of the fields "Length" and "To level" of a hole, here and in the hole form: how the length is counted and
    /// how a level works it out. Paragraph by paragraph, each translated on its own (the table holds one line a text).
    /// </summary>
    public static readonly string[] LengthTipParagraphs =
    {
        "Length: the full length of the hole along its axis, from the collar to the very bottom, subdrill included.",
        "With To level set, the length is calculated automatically: length = (collar Z − level) / cos(angle) + subdrill.",
        "The bearing does not change the length: at any angle the hole reaches the level.",
        "A negative subdrill is an underdrill: the hole ends that far above the level along its axis.",
        "The length follows the collar, the angle, the subdrill and the level; the last interval of the column goes down to the new toe.",
        "If the underdrill is as long as the whole way to the level, the length stays as it is.",
        "Empty or 0: no level, the length is entered by hand."
    };

    public static string LengthTip() => string.Join("\n\n", LengthTipParagraphs.Select(Loc.Ui));
    /// <summary>The groups of the fields of a hole; the last one takes all the others.</summary>
    private static readonly PropertySection[] HoleSections =
    {
        new("Geometry", nameof(Hole.Label), nameof(Hole.Collar), nameof(Hole.Angle), nameof(Hole.Bearing), nameof(Hole.Length), nameof(Hole.ToLevel), nameof(Hole.Diameter), nameof(Hole.Subdrill)),
        new("Classification", nameof(Hole.HoleType), nameof(Hole.MaterialType), nameof(Hole.State), nameof(Hole.IsDummy)),
        new("Measurements", nameof(Hole.ActualLength), nameof(Hole.WaterDepth), nameof(Hole.Temperature), nameof(Hole.Exceptions)),
        new("Other")
    };
    /// <summary>The groups folded, kept from one selection to the next.</summary>
    private readonly HashSet<string> _collapsed = new() { "Measurements", "Other" };

    public PropertiesPanel(MainViewModel vm)
    {
        _vm = vm;
        SetDock(_header, Dock.Top);
        Children.Add(_header);
        Children.Add(new ScrollViewer { Content = _content });
        vm.SelectionRefreshed += Refresh;
    }

    /// <summary>
    /// Builds the panel for the selection. A field that had the focus has it again in the panel built anew (Enter in a field
    /// takes its value and the panel follows the change): the arrows go on stepping it instead of moving the selection.
    /// </summary>
    public void Refresh()
    {
        // a change made while the panel is built (a box taken away loses its focus and takes its text) builds it again after
        // this build, once, not inside it
        if (_building) { _again = true; return; }
        _building = true;
        try
        {
            // a few times at most: a field that took its text on every build must not hang the program
            var builds = 0;
            do
            {
                _again = false;
                RefreshOnce();
            } while (_again && ++builds < 3);
        }
        finally { _building = false; }
    }

    private bool _building, _again;

    private void RefreshOnce()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox { Name: { Length: > 0 } name } box
            && box.GetLogicalAncestors().Contains(this) ? name : null;
        Build();
        if (focused == null) return;
        if (this.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(b => b.Name == focused && b.IsEffectivelyVisible && b.IsEnabled) is not { } again) return;
        void Take()
        {
            again.Focus();
            again.CaretIndex = again.Text?.Length ?? 0;
        }
        // a box just made may be laid out only on the next pass
        if (!again.Focus()) Avalonia.Threading.Dispatcher.UIThread.Post(Take, Avalonia.Threading.DispatcherPriority.Loaded);
        else Take();
    }

    private void Build()
    {
        if (!IsVisible) return;
        _header.IsVisible = true;
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
        var template = first.EditCopy();
        if (template is Hole th && first is Hole fh) { th.FiringTimeMs = fh.FiringTimeMs; th.SurfaceTimeMs = fh.SurfaceTimeMs; th.FiringSequence = fh.FiringSequence; }
        var grid = new PropertyGrid { Exclude = new() { "Id", "LayerId", "GroupId", "Decks", "BoreTrack", "RowId", "FromId", "ToId", "HoleId", "Triangles", "Vertices", "Points", "FromPos", "ToPos", "Direction", "DesignToe", "GradePoint", "Front", "IsChevron", "HoleIds", "BurdenDir", "SpacingDir", "PatternId", "GridI", "GridJ", "GridOffset" }, ReadOnly = doc.IsReadOnly };
        _vm.AddProductChoices(grid, first is TieEntity ? sel.OfType<TieEntity>().ToList() : null);
        if (first is Hole)
        {
            // the detonator id of a hole is its serial number, not a product
            grid.Choices.Remove(nameof(Hole.DetonatorId));
            grid.Sections = HoleSections;
            grid.Collapsed = _collapsed;
            string m = Loc.Ui("m"), degrees = "°";
            foreach (var (name, unit) in new[] { (nameof(Hole.Collar), m), (nameof(Hole.Angle), degrees), (nameof(Hole.Bearing), degrees), (nameof(Hole.Length), m), (nameof(Hole.ToLevel), m), (nameof(Hole.Diameter), Loc.Ui("mm")), (nameof(Hole.Subdrill), m), (nameof(Hole.ActualLength), m), (nameof(Hole.WaterDepth), m), (nameof(Hole.Temperature), "°C") })
                grid.Units[name] = unit;
            grid.Tips[nameof(Hole.Length)] = grid.Tips[nameof(Hole.ToLevel)] = LengthTip();
            if (!doc.IsReadOnly)
            {
                var toSurface = Ui.Flat(Look.Icon("surface", 14, Look.TextSoft), Loc.Ui("To the surface: the selected holes go down to the first surface their line meets from the collar, plus the subdrill"), () => _ = _vm.HolesToSurface(), 30, 30);
                toSurface.Name = "holesToSurface";
                Avalonia.Automation.AutomationProperties.SetName(toSurface, Loc.Ui("To the surface"));
                grid.After[nameof(Hole.ToLevel)] = toSurface;
            }
        }
        grid.SetTargets(template, sel);
        var ids = sel.Select(e => e.Id).ToHashSet();
        grid.PropertyEdited += name =>
        {
            // a hole is drilled down to a level only from above it: the value is not taken, the panel shows the old one
            if (name == nameof(Hole.ToLevel) && template is Hole level && HoleLevel.Of(level) is double z
                && sel.OfType<Hole>().FirstOrDefault(h => !HoleLevel.Reaches(h, z)) is { } low)
            {
                _ = Dialogs.Info(Loc.F("The collar of hole {0} ({1:0.###} m) is not above the level {2:0.###} m.", low.Label, low.Collar.Z, z));
                Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
                return;
            }
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
        if (first is Hole hole)
        {
            _header.IsVisible = false;
            var edit = new Button
            {
                Content = sel.Count == 1 ? Loc.T("Edit loading…") : Loc.F("Edit loading ({0} holes)…", sel.Count),
                Height = 34,
                CornerRadius = new CornerRadius(7),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Classes = { "primary" },
                IsEnabled = !doc.IsReadOnly,
                Name = "editLoading"
            };
            edit.Click += async (_, _) => await _vm.EditHoleLoading(hole);
            _content.Content = new StackPanel
            {
                Spacing = 12,
                Margin = new Thickness(14, 12, 14, 14),
                Children = { HoleHead(hole, sel.Count), edit, sel.Count == 1 ? HoleCharge(doc.Project, hole) : HolesCharge(doc.Project, sel.OfType<Hole>().ToList()), LayerRow(doc, sel, stacked: true), grid, HoleCalculations(doc.Project, sel.OfType<Hole>().ToList()) }
            };
            if (PatternSection(doc, sel.OfType<Hole>().ToList()) is { } pattern) ((StackPanel)_content.Content).Children.Insert(4, pattern);
            return;
        }
        var panel = new StackPanel { Children = { LayerRow(doc, sel) } };
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
        if (first is EbsOpeningPoint opening && sel.Count == 1)
        {
            var edit = new Button { Content = Loc.T("Edit opening point…"), Margin = new Thickness(6), IsEnabled = !doc.IsReadOnly, Name = "editOpeningPoint" };
            edit.Click += async (_, _) => await _vm.EditOpeningPoint(opening);
            panel.Children.Add(edit);
        }
        if (first is PolylineEntity { Kind: LineKind.CrossSection, Points.Count: >= 2 } section && sel.Count == 1)
        {
            var open = new Button { Content = Loc.T("Section along line"), Margin = new Thickness(6), Name = "sectionAlongLine" };
            open.Click += (_, _) => AnalysisWindows.ShowSection(_vm, section.Points[0], section.Points[1]);
            panel.Children.Add(open);
        }
        panel.Children.Add(grid);
        _content.Content = panel;
    }

    /// <summary>
    /// The layer of the selection, laid out like a row of the property grid, or <paramref name="stacked"/> like a field of
    /// its groups: picking another one moves the selection there; several layers show as mixed.
    /// </summary>
    private Control LayerRow(Document doc, List<Entity> sel, bool stacked = false)
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
            MinWidth = stacked ? 0 : 160,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            IsEnabled = !doc.IsReadOnly,
            Margin = stacked ? default : new Thickness(0, 2)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            var layer = layers[combo.SelectedIndex];
            if (sel.Any(e => e.LayerId != layer.Id)) _vm.MoveSelectionToLayer(layer);
        };
        if (stacked) return new StackPanel { Spacing = 4, Children = { new TextBlock { Text = Loc.T("Layer"), FontSize = 12, Foreground = Look.Muted }, combo } };
        var label = new TextBlock { Text = Loc.T("Layer"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) };
        Grid.SetColumn(combo, 1);
        // the columns and margin of the property grid under it, so the row lines up with its fields
        return new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*"), Margin = new Thickness(4, 4, 4, 0), Children = { label, combo } };
    }

    /// <summary>
    /// Group «Pattern» of holes of one pattern grid: its name, burden and spacing, the direction of its burden (typed in, it
    /// turns the grid), the turn since it was made, the offset of a hole moved off its node and whether the blast orientation
    /// turns with it.
    /// </summary>
    private Control? PatternSection(Document doc, List<Hole> holes)
    {
        var ids = holes.Select(h => h.PatternId).Distinct().ToList();
        if (ids.Count != 1 || doc.Project.FindPattern(ids[0]) is not { } pat) return null;
        const string key = "Pattern";
        var body = new StackPanel { Spacing = 6, IsVisible = !_collapsed.Contains(key), Name = "holePattern" };
        var chevron = new TextBlock { Text = body.IsVisible ? "▾" : "▸", Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center };
        var head = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, Name = "holePatternHead",
            Content = Ui.Row(6, chevron, new TextBlock { Text = Loc.Ui("Pattern").ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Blue })
        };
        head.Click += (_, _) =>
        {
            body.IsVisible = !body.IsVisible;
            chevron.Text = body.IsVisible ? "▾" : "▸";
            if (body.IsVisible) _collapsed.Remove(key); else _collapsed.Add(key);
        };
        var id = pat.Id;
        var c = CultureInfo.CurrentCulture;
        Control Row(string label, Control value)
        {
            var row = new DockPanel();
            value[DockPanel.DockProperty] = Dock.Right;
            row.Children.Add(value);
            row.Children.Add(new TextBlock { Text = Loc.Ui(label), FontSize = 12, Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }
        TextBlock Value(string text) => new() { Text = text, FontFamily = Look.Mono, FontSize = 11.5, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center };
        TextBox Box(string text, string name, Action<string> commit)
        {
            var box = new TextBox { Text = text, Width = 120, Height = 26, MinHeight = 26, Padding = new Thickness(6, 2), FontSize = 12, Name = name, IsEnabled = !doc.IsReadOnly };
            void Commit() { if ((box.Text ?? "") != text) commit(box.Text ?? ""); }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(); e.Handled = true; } };
            return box;
        }
        body.Children.Add(Row("Name", Box(pat.Name, "patternName", v => { if (v.Trim().Length > 0) _vm.SetPatternFlag(id, "Rename pattern", x => x.Name = v.Trim()); })));
        // burden and spacing typed in lay the grid out again; refused or declined, the panel shows the old values
        double? Metres(string v) => double.TryParse(v.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : null;
        async void Resize(double burden, double spacing)
        {
            if (!await _vm.ResizePattern(id, burden, spacing)) Refresh();
        }
        var burdenBox = Box(pat.Burden.ToString("0.###", c), "patternBurden", v => { if (Metres(v) is double b) Resize(b, pat.Spacing); else Refresh(); });
        var spacingBox = Box(pat.Spacing.ToString("0.###", c), "patternSpacing", v => { if (Metres(v) is double s) Resize(pat.Burden, s); else Refresh(); });
        burdenBox.Width = spacingBox.Width = 52;
        ToolTip.SetTip(burdenBox, Loc.Ui("Burden, m: the distance between the rows; a new value lays the grid out again"));
        ToolTip.SetTip(spacingBox, Loc.Ui("Spacing, m: the distance between the holes of a row; a new value lays the grid out again"));
        TextBlock Faint(string text) => new() { Text = text, FontSize = 12, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(Row("Burden × spacing", Ui.Row(4, burdenBox, Faint("×"), spacingBox, Faint(Loc.Ui("m")))));
        var azimuth = Box(pat.Azimuth.ToString("0.#", c), "patternAzimuth", v =>
        {
            if (double.TryParse(v, NumberStyles.Float, c, out var a)) _ = _vm.RotatePattern(id, MainViewModel.Turn(pat.Azimuth, a));
        });
        ToolTip.SetTip(azimuth, Loc.Ui("Direction of the burden, degrees from north; a new value turns the grid about its centre"));
        body.Children.Add(Row("Burden azimuth, °", azimuth));
        body.Children.Add(Row("Turned since made", Value(MainViewModel.Turn(pat.InitialAzimuth, pat.Azimuth).ToString("+0.#;−0.#;0", c) + "°")));
        if (holes.Count == 1 && holes[0].GridOffset is Vec3 o)
            body.Children.Add(Row("Off its node", Value($"{o.X.ToString("0.##", c)} / {o.Y.ToString("0.##", c)} {Loc.Ui("m")}")));
        var turns = new CheckBox { Content = Loc.Ui("Orientation turns with the pattern"), IsChecked = pat.RotateOrientation, Name = "patternRotateOrientationProp", IsEnabled = !doc.IsReadOnly };
        turns.IsCheckedChanged += (_, _) => { var on = turns.IsChecked == true; if (on != pat.RotateOrientation) _vm.SetPatternFlag(id, "Pattern settings", x => x.RotateOrientation = on); };
        body.Children.Add(turns);
        var select = new Button { Content = Loc.T("Select pattern"), Height = 28, Name = "selectPattern" };
        select.Click += (_, _) => _vm.SelectPatternHoles();
        var rotate = new Button { Content = Loc.T("Rotate pattern with the mouse"), Height = 28, Name = "rotatePattern", IsEnabled = !doc.IsReadOnly };
        rotate.Click += (_, _) => _vm.PatternRotateTool();
        body.Children.Add(Ui.Row(6, select, rotate));
        return new StackPanel { Spacing = 8, Children = { head, body } };
    }

    /// <summary>What is selected: the kind on the left, the number of the hole or how many on the right.</summary>
    private static Control HoleHead(Hole hole, int count)
    {
        var mark = count > 1 ? $"× {count}" : hole.Label.Length > 0 ? Loc.F(Loc.Ui("No. {0}"), hole.Label) : "";
        var head = new DockPanel();
        if (mark.Length > 0)
            head.Children.Add(new Border
            {
                [DockPanel.DockProperty] = Dock.Right,
                Background = Look.AccentSoft,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = mark, FontFamily = Look.Mono, FontSize = 12, Foreground = Look.Accent, Name = "holeMark" }
            });
        head.Children.Add(new TextBlock { Text = count > 1 ? Loc.Ui("Holes") : Loc.T(hole.KindName), FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center });
        return head;
    }

    private static readonly string[] CalcGroups = { "Заряжание", "Объёмы", "Стоимость", "Разлёт" };
    private static readonly IBrush OutOfRangeInk = new SolidColorBrush(Color.FromUInt32(0xFFF0C13A));

    /// <summary>
    /// Group «Calculations» of the hole: the calculated properties of the profile by group, read-only. Several holes: the
    /// value they share, or «different» with the sum or the mean. Values outside the reasonable range are marked.
    /// </summary>
    private Control HoleCalculations(BlastProject p, List<Hole> holes)
    {
        const string key = "Calculations";
        var body = new StackPanel { Spacing = 8, IsVisible = !_collapsed.Contains(key), Name = "holeCalculations" };
        var chevron = new TextBlock { Text = body.IsVisible ? "▾" : "▸", Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center };
        var head = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, Name = "holeCalculationsHead",
            Content = Ui.Row(6, chevron, new TextBlock { Text = Loc.T("Calculations (formulas)").ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Blue })
        };
        head.Click += (_, _) =>
        {
            body.IsVisible = !body.IsVisible;
            chevron.Text = body.IsVisible ? "▾" : "▸";
            if (body.IsVisible) _collapsed.Remove(key); else _collapsed.Add(key);
        };
        var props = CalcService.Calculated(p, "hole");
        bool Mine(BlastDesigner.Calc.PropertyDef d) => !d.BuiltIn || !CalcGroups.Contains(d.Group);
        foreach (var group in CalcGroups.Select(g => (Name: g, Items: props.Where(d => !Mine(d) && d.Group == g).ToList())).Append((Name: "My properties", Items: props.Where(Mine).ToList())))
        {
            if (group.Items.Count == 0) continue;
            body.Children.Add(new TextBlock { Text = Loc.T(group.Name), FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Look.Muted, Margin = new Thickness(0, 2, 0, 0) });
            foreach (var d in group.Items) body.Children.Add(CalcRow(p, d.Id, holes));
        }
        return new StackPanel { Spacing = 8, Children = { head, body } };
    }

    private Control CalcRow(BlastProject p, string id, List<Hole> holes)
    {
        var s = CalcService.Summarize(p, id, holes);
        var text = !s.Different ? s.Text : s.Aggregate == null ? Loc.T("different") : Loc.T("different") + " · " + (s.IsSum ? "Σ " : Loc.T("mean") + " ") + s.Aggregate;
        var value = new TextBlock { Text = text, Name = "calc:" + id, FontFamily = Look.Mono, FontSize = 11.5, Foreground = s.OutOfRange ? OutOfRangeInk : s.Different ? Look.Muted : Look.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        if (s.OutOfRange) ToolTip.SetTip(value, Loc.T("Outside the reasonable range"));
        var name = new TextBlock { Text = CalcService.Caption(p, id), FontSize = 12, Foreground = Look.TextSoft, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(name, name.Text);
        var row = new DockPanel();
        if (holes.Count == 1)
        {
            var explain = new Button { Content = "?", FontSize = 11, Padding = new Thickness(6, 0), Height = 20, MinWidth = 20, Margin = new Thickness(6, 0, 0, 0), Background = Brushes.Transparent, BorderBrush = Look.Line, BorderThickness = new Thickness(1), Name = "explain:" + id };
            ToolTip.SetTip(explain, Loc.T("Explain value…"));
            explain.Click += (_, _) => _ = CalcExplainWindow.Show(_vm, holes[0], id);
            row.Children.Add(new ContentControl { [DockPanel.DockProperty] = Dock.Right, Content = explain });
        }
        value[DockPanel.DockProperty] = Dock.Right;
        row.Children.Add(value);
        row.Children.Add(name);
        return row;
    }

    private static Border Tile(Control child, string? name = null) => new()
    {
        Name = name,
        Background = Look.Panel,
        BorderBrush = Look.Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 8),
        Child = child
    };

    private static TextBlock TileLabel(string text) => new() { Text = text, FontSize = 11, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

    private static Control Kilograms(double kg) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = kg.ToString("0.0", CultureInfo.CurrentCulture), FontFamily = Look.Mono, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Look.Text },
            new TextBlock { Text = Loc.Ui("kg"), FontSize = 11, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) }
        }
    };

    /// <summary>The explosive of several holes: in all and the mean of a hole.</summary>
    private static Control HolesCharge(BlastProject p, List<Hole> holes)
    {
        var total = holes.Sum(h => Charging.ExplosiveMass(p, h));
        var mean = Tile(new StackPanel { Children = { TileLabel(Loc.Ui("Explosive per hole")), Kilograms(total / holes.Count) } }, "chargePerHole");
        Grid.SetColumn(mean, 2);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,8,*"),
            Children = { Tile(new StackPanel { Children = { TileLabel(Loc.Ui("Explosive total")), Kilograms(total) } }, "chargeTotal"), mean }
        };
    }

    /// <summary>The charge of a hole: its explosive, the decks to scale down the hole and each of them.</summary>
    private static Control HoleCharge(BlastProject p, Hole h)
    {
        if (h.Decks.Count == 0) return Tile(TileLabel(Loc.T("Not loaded")), "holeCharge");
        var decks = h.Decks.OrderBy(d => d.Top).ToList();
        var head = new DockPanel();
        var mass = Kilograms(Charging.ExplosiveMass(p, h));
        mass[DockPanel.DockProperty] = Dock.Right;
        head.Children.Add(mass);
        head.Children.Add(TileLabel(Loc.Ui("Explosive in the hole")));
        var scale = new Grid();
        var rows = new StackPanel { Spacing = 2 };
        var depth = 0.0;
        void Part(double length, IBrush? colour)
        {
            if (length <= 0) return;
            scale.ColumnDefinitions.Add(new ColumnDefinition(length, GridUnitType.Star));
            if (colour == null) return;
            var part = new Border { Background = colour };
            Grid.SetColumn(part, scale.ColumnDefinitions.Count - 1);
            scale.Children.Add(part);
        }
        foreach (var d in decks)
        {
            var product = p.FindProduct(d.ProductId);
            IBrush colour = product == null ? Look.Faint : new SolidColorBrush(Color.FromUInt32(product.Color));
            Part(d.Top - depth, null);
            Part(d.Bottom - d.Top, colour);
            depth = Math.Max(depth, d.Bottom);
            var text = $"{d.Top:0.0}–{d.Bottom:0.0} {Loc.Ui("m")}  {product?.Name}" + (d.Primers.Count > 0 ? $"  ({d.Primers.Count} pr.)" : "");
            var kg = new TextBlock { [DockPanel.DockProperty] = Dock.Right, Text = $"{Charging.DeckMass(p, h, d):0.0} {Loc.Ui("kg")}", FontFamily = Look.Mono, FontSize = 11, Foreground = Look.Muted, Margin = new Thickness(8, 0, 0, 0) };
            var name = new TextBlock { Text = text, FontSize = 11, Foreground = Look.Muted, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(name, text);
            rows.Children.Add(new DockPanel
            {
                Children =
                {
                    new Border { [DockPanel.DockProperty] = Dock.Left, Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = colour, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center },
                    kg, name
                }
            });
        }
        Part(h.EffectiveLength - depth, null);
        return Tile(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                head,
                new Border { Height = 8, CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = Look.Line, Child = scale },
                rows
            }
        }, "holeCharge");
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
        // the calculated blast totals chosen in Edit ▸ Calculations, from the document's calculation session
        foreach (var id in p.Summary.CalcTotals ?? new())
            text.Add($"{CalcService.Caption(p, id)}: {CalcService.Format(p, id, p)}");
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
    /// <summary>A calculated property of the hole (<c>hole.volume</c>), from the document's calculation session.</summary>
    public string Calc(string propertyId) => CalcService.Format(_vm.Project!, propertyId, Hole, false);
    public bool OutOfRange(string propertyId) => CalcService.IsOutOfRange(_vm.Project!, propertyId, Hole);
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
        _fixedColumns = _grid.Columns.Count;
        _grid.SelectionChanged += (_, _) =>
        {
            if (_syncing || _vm.Doc == null) return;
            _syncing = true;
            _vm.Doc.Select(_grid.SelectedItems.OfType<HoleRowVm>().Select(r => r.Hole.Id));
            _syncing = false;
        };
        _filter.TextChanged += (_, _) => Refresh();
        // как получилось вычисленное значение скважины — формула с числами по шагам
        var explain = new MenuItem { Header = Loc.T("Explain value…") };
        explain.Click += (_, _) => { if (_grid.SelectedItem is HoleRowVm r) _ = CalcExplainWindow.Show(_vm, r.Hole); };
        _grid.ContextMenu = new ContextMenu { ItemsSource = new[] { explain } };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4), Spacing = 8 };
        top.Children.Add(new TextBlock { Text = Loc.T("Holes"), Classes = { "caption" } });
        top.Children.Add(_filter);
        var zoom = new Button { Content = Loc.T("Zoom to selection") };
        zoom.Click += (_, _) => _vm.ZoomSelection();
        top.Children.Add(zoom);
        var columns = new Button { Content = Loc.T("Calculated columns…"), Name = "calcColumns" };
        columns.Click += (_, _) => _ = ChooseColumns();
        top.Children.Add(columns);
        SetDock(top, Dock.Top);
        Children.Add(top);
        Children.Add(_grid);
        vm.ModelRefreshed += () => { if (IsVisible) Refresh(); };
        vm.SelectionRefreshed += SyncSelection;
    }

    private readonly int _fixedColumns;

    /// <summary>The calculated properties chosen as columns, after the fixed ones; read-only, values from the calculation session.</summary>
    private void CalcColumns(BlastProject p)
    {
        while (_grid.Columns.Count > _fixedColumns) _grid.Columns.RemoveAt(_grid.Columns.Count - 1);
        foreach (var id in p.View.CalcColumns ?? new())
        {
            var unit = CalcService.UnitText(p, id);
            var property = id;
            _grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = CalcService.Caption(p, id) + (unit.Length > 0 ? ", " + unit : ""),
                IsReadOnly = true,
                CellTemplate = new FuncDataTemplate<HoleRowVm>((row, _) =>
                {
                    var cell = new TextBlock { Text = row?.Calc(property) ?? "", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
                    if (row?.OutOfRange(property) == true)
                    {
                        cell.Foreground = new SolidColorBrush(Color.FromUInt32(0xFFF0C13A));
                        ToolTip.SetTip(cell, Loc.T("Outside the reasonable range"));
                    }
                    return cell;
                })
            });
        }
    }

    /// <summary>The columns of calculated hole properties (the same go to the CSV export of holes).</summary>
    public async Task ChooseColumns()
    {
        var p = _vm.Project;
        if (p == null) return;
        var chosen = (p.View.CalcColumns ?? new()).ToHashSet();
        var boxes = CalcService.Calculated(p, "hole").Where(d => d.Type != BlastDesigner.Calc.CalcType.Bool)
            .Select(d => new CheckBox { Content = CalcService.Caption(p, d.Id) + "  (" + d.Id + ")", Tag = d.Id, IsChecked = chosen.Contains(d.Id) }).ToList();
        var list = new StackPanel { Spacing = 2 };
        foreach (var b in boxes) list.Children.Add(b);
        if (!await Dialogs.Custom(Loc.T("Calculated columns"), new ScrollViewer { Content = list, MaxHeight = 520 }, width: 460,
                subtitle: Loc.T("Columns of the hole table and of the CSV export of holes"))) return;
        var ids = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag!).ToList();
        _vm.ToggleView(v => v.CalcColumns = ids.Count > 0 ? ids : null);
        Refresh();
    }

    public void Refresh()
    {
        var p = _vm.Project;
        if (p == null) { _grid.ItemsSource = null; return; }
        CalcColumns(p);
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
