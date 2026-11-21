using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.App.Tools.Section;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.App.Views;

/// <summary>
/// The ribbon of the Section view window (variant B of the design): the tabs View, Dimensions and Style with their groups,
/// the line of the section and Print beside the tabs, and under the section a status line with the hint of the step,
/// the snaps, Ortho and the place of the cursor. The tools of the Dimensions tab draw the marks of the section; its Style
/// group sets the colour, line type and weight of the selected marks and of those to come.
/// </summary>
public sealed class SectionRibbon
{
    private readonly MainViewModel _vm;
    private readonly SectionSession _s;
    private readonly SectionSelectTool _select;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly StackPanel _groups = new() { Orientation = Orientation.Horizontal };
    private readonly List<(Func<Tool, bool> IsActive, Button Button)> _toolButtons = new();
    private readonly TextBlock _hint = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Look.TextSoft, FontSize = 12, Name = "sectionHint", TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _where = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Look.Muted, FontFamily = Look.Mono, FontSize = 11, Name = "sectionWhere" };
    private readonly StackPanel _snaps = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly List<Control>[] _content;
    private readonly string[] _names = { "View", "Dimensions", "Section style" };
    private ComboBox _type = null!, _weight = null!, _preset = null!;
    /// <summary>The line types of the list in its order: null solid, else the name of AutoCAD (or of the project).</summary>
    private readonly List<string?> _typeNames = new();

    /// <summary>A weight as the list shows it: 0,25 мм.</summary>
    private static string WeightName(int w) => (w / 100.0).ToString("0.00", System.Globalization.CultureInfo.CurrentCulture) + " " + Loc.Ui("mm");

    /// <summary>Copy style, Apply style while its tool works.</summary>
    private Button _paint = null!;
    private Border _swatch = null!;
    private bool _filling;

    /// <summary>The window as its tools see it.</summary>
    public SectionSession Session => _s;

    /// <summary>The tool working in the view: selecting, or drawing a mark.</summary>
    public Tool Active { get; private set; }
    public int Current { get; private set; }

    /// <summary>The ribbon over the section: the tabs and their band.</summary>
    public Control Top { get; }
    /// <summary>The status line under the section.</summary>
    public Control Status { get; }

    /// <param name="viewGroups">The groups of the View tab: what the window had over the section (half width, zoom, the labels).</param>
    /// <param name="beside">What stands right of the tabs: the line of the section and Print.</param>
    public SectionRibbon(MainViewModel vm, SectionSession session, SectionSelectTool select, IEnumerable<(string Name, Control Body)> viewGroups, Control beside)
    {
        _vm = vm; _s = session; _select = select;
        Active = select;
        _content = new[]
        {
            viewGroups.Select(g => Group(g.Name, g.Body)).ToList(),
            DimensionGroups(),
            StyleGroups()
        };
        var head = new DockPanel { Background = Look.Panel, Height = 30 };
        DockPanel.SetDock(beside, Dock.Right);
        beside.Margin = new Thickness(0, 0, 8, 0);
        beside.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(beside);
        _tabs.Margin = new Thickness(8, 0, 0, 0);
        _tabs.VerticalAlignment = VerticalAlignment.Bottom;
        head.Children.Add(_tabs);
        // a low band: two rows of small buttons, the names of the groups small under them
        var band = new Border { Background = Look.Ribbon, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1), Child = Ui.HScroll(_groups) };
        var top = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        top.Children.Add(head);
        top.Children.Add(band);
        Top = top;

        var status = new DockPanel { Background = Look.Panel, Height = 34, Name = "sectionStatus" };
        var right = Ui.Row(8, new TextBlock { Text = Loc.T("Snaps"), Foreground = Look.Faint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }, _snaps, _where);
        right.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(right, Dock.Right);
        status.Children.Add(right);
        _hint.Margin = new Thickness(10, 0);
        status.Children.Add(_hint);
        Status = new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = status };
        BuildSnaps();
        Select(0);
        session.Changed += Refresh;
        Refresh();
    }

    public void Select(int index)
    {
        Current = index;
        _tabs.Children.Clear();
        for (int i = 0; i < _names.Length; i++)
        {
            var idx = i;
            var active = i == index;
            var b = new Button
            {
                Content = new TextBlock { Text = Loc.T(_names[i]), FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal, Foreground = active ? Look.Accent : Look.Muted },
                Height = 26,
                VerticalAlignment = VerticalAlignment.Bottom,
                Padding = new Thickness(12, 0),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Background = active ? Look.Ribbon : Brushes.Transparent,
                BorderBrush = Look.Accent,
                BorderThickness = new Thickness(0, active ? 2 : 0, 0, 0),
                Name = "sectionTab" + _names[i].Replace(" ", "")
            };
            b.Classes.Add("flat");
            b.Click += (_, _) => Select(idx);
            _tabs.Children.Add(b);
        }
        _groups.Children.Clear();
        foreach (var g in _content[index]) _groups.Children.Add(g);
    }

    /// <summary>Takes a tool for the view; null goes back to selecting.</summary>
    public void SetTool(Tool? tool)
    {
        Active.Cancel();
        _s.Preview = null;
        Active = tool ?? _select;
        if (Active is SectionMarkTool mark) mark.Start();
        else if (Active is SectionStyleTool painting) painting.Start();
        else _s.SetHint(Loc.T("Click or drag a box to select · drag a grip of a selected mark to move its point · double click to edit its figure"));
        Refresh();
        _s.View.Redraw();
    }

    private void Take(Func<Tool> make, Func<Tool, bool> isIt)
    {
        if (isIt(Active)) { SetTool(null); return; }
        SetTool(make());
    }

    // ---- the groups --------------------------------------------------------------------------------------------

    /// <summary>A group of the band: its controls, its name small under them, a line right of it.</summary>
    private static Control Group(string name, Control body)
    {
        var group = new DockPanel { Margin = new Thickness(6, 3, 6, 1) };
        var caption = new TextBlock { Text = Loc.T(name), Foreground = Look.Accent, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(caption, Dock.Bottom);
        group.Children.Add(caption);
        group.Children.Add(body);
        return new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = group };
    }

    /// <summary>Controls in columns of two, top to bottom then left to right: the two rows of the band.</summary>
    private static Control Columns(params Control[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < items.Length; i += 2)
        {
            var column = new StackPanel { Spacing = 1 };
            column.Children.Add(items[i]);
            if (i + 1 < items.Length) column.Children.Add(items[i + 1]);
            row.Children.Add(column);
        }
        return row;
    }

    private static Control[] TakeOut(StackPanel stack)
    {
        var items = stack.Children.ToArray();
        stack.Children.Clear();
        return items;
    }

    /// <summary>The controls of a stack, taken out of it, in columns of two.</summary>
    private static Control Columns(StackPanel stack)
    {
        var items = stack.Children.ToArray();
        stack.Children.Clear();
        return Columns(items);
    }

    private Button Big(string icon, string label, string name, Action click) => Small(icon, label, name, click);

    private Button Small(string icon, string label, string name, Action click)
    {
        var b = new Button
        {
            Content = Ui.Row(6, Look.Icon(icon, 15, Look.Muted), new TextBlock { Text = Loc.T(label), FontSize = 12, Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center }),
            Height = 24, Padding = new Thickness(8, 0), Margin = new Thickness(0, 0, 2, 1), Background = Brushes.Transparent, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), HorizontalContentAlignment = HorizontalAlignment.Left, Name = name
        };
        b.Classes.Add("flat");
        b.Click += (_, _) => Ui.Safe(click);
        return b;
    }

    /// <summary>A button that takes a tool drawing marks of a kind; pressed again it goes back to selecting.</summary>
    private Button MarkTool(bool big, string icon, string label, string name, SectionMarkKind kind, bool chain = false)
    {
        bool IsIt(Tool t) => t is SectionMarkTool m && m.Kind == kind && m.Chain == chain;
        var b = (big ? Big : (Func<string, string, string, Action, Button>)Small)(icon, label, name, () => Take(() => new SectionMarkTool(_vm, _s, kind, chain), IsIt));
        _toolButtons.Add((IsIt, b));
        return b;
    }

    private List<Control> DimensionGroups()
    {
        var dims = Columns(
            MarkTool(true, "dimension", "Across", "markHorizontal", SectionMarkKind.Horizontal),
            MarkTool(true, "dimv", "Up", "markVertical", SectionMarkKind.Vertical),
            MarkTool(false, "dimaligned", "Along", "markAligned", SectionMarkKind.Aligned),
            MarkTool(false, "leader", "Leader", "markLeader", SectionMarkKind.Leader),
            MarkTool(false, "chain", "Chain", "markChain", SectionMarkKind.Horizontal, true));
        var angles = Columns(MarkTool(true, "angle", "Angle", "markAngle", SectionMarkKind.Angle), MarkTool(true, "slope", "Slope", "markSlope", SectionMarkKind.Slope));
        var levels = Columns(MarkTool(true, "level", "Level", "markLevel", SectionMarkKind.Level), MarkTool(true, "pointz", "Point Z", "markPointZ", SectionMarkKind.PointZ));
        var draw = Columns(MarkTool(true, "polyline", "Line", "markPolyline", SectionMarkKind.Polyline), MarkTool(true, "text", "Text", "markText", SectionMarkKind.Text));
        return new() { Group("Dimensions", dims), Group("Angles", angles), Group("Levels", levels), Group("Drawing", draw) };
    }

    /// <summary>Colour, line type, weight and the styles offered: for the selected marks and those to come.</summary>
    private Control LineStyle()
    {
        _swatch = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(3), BorderBrush = Look.Line, BorderThickness = new Thickness(1) };
        var colour = new Button { Content = Ui.Row(6, _swatch, Look.Icon("down", 11, Look.Faint, 2)), Height = 24, MinHeight = 24, Padding = new Thickness(8, 0), Name = "markColour" };
        Ui.DropDown(colour, ColourMenu);
        _type = new ComboBox { Width = 150, Height = 24, MinHeight = 24, Padding = new Thickness(8, 0), Name = "markLineType" };
        _type.SelectionChanged += (_, _) =>
        {
            // the list shows the names in the language of the program; the mark keeps the name of AutoCAD
            if (_filling || _type.SelectedIndex < 0 || _type.SelectedIndex >= _typeNames.Count) return;
            var type = _typeNames[_type.SelectedIndex];
            Apply("Line type", s => s.LineType = type, p => p.LineType = type ?? Domain.Model.LineTypes.Continuous);
        };
        _weight = new ComboBox { Width = 110, Height = 24, MinHeight = 24, Padding = new Thickness(8, 0), Name = "markWeight", ItemsSource = Weights.Select(WeightName).ToList() };
        _weight.SelectionChanged += (_, _) =>
        {
            if (_filling || _weight.SelectedIndex < 0) return;
            var w = Weights[_weight.SelectedIndex];
            Apply("Line weight", s => s.LineWeight = w, p => p.LineWeight = w);
        };
        _preset = new ComboBox { Width = 150, Height = 24, MinHeight = 24, Padding = new Thickness(8, 0), Name = "markPreset", PlaceholderText = Loc.T("Preset") };
        _preset.SelectionChanged += (_, _) =>
        {
            if (_filling || _preset.SelectedIndex < 0 || _preset.SelectedIndex >= _vm.Settings.SectionMarkPresets.Count) return;
            var preset = _vm.Settings.SectionMarkPresets[_preset.SelectedIndex].Style;
            Apply("Style", s => { s.Color = preset.Color; s.LineType = preset.LineType; s.LineWeight = preset.LineWeight; },
                p => { p.Color = preset.Color; p.LineType = preset.LineType ?? Domain.Model.LineTypes.Continuous; p.LineWeight = preset.LineWeight; });
        };
        Label(colour, "Colour"); Label(_type, "Type"); Label(_weight, "Weight"); Label(_preset, "A style kept: its colour, line type and weight go to the selected marks and to those drawn next. Save as style… keeps the style of a selected mark under a name.");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,12,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), VerticalAlignment = VerticalAlignment.Center };
        void Put(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); c.Margin = new Thickness(0, 0, 6, 1); grid.Children.Add(c); }
        Put(Caption("Colour"), 0, 0); Put(colour, 0, 1); Put(Caption("Type"), 0, 3); Put(_type, 0, 4);
        Put(Caption("Weight"), 1, 0); Put(_weight, 1, 1); Put(Caption("Preset"), 1, 3); Put(_preset, 1, 4);
        return grid;
    }

    private static TextBlock Caption(string text) => new() { Text = Loc.T(text), Foreground = Look.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private static void Label(Control c, string tip) => ToolTip.SetTip(c, Loc.T(tip));

    /// <summary>The weights offered, hundredths of a millimetre.</summary>
    public static readonly int[] Weights = { 18, 25, 35, 50, 70, 100 };

    /// <summary>The colours offered: of the section (black on a sheet), then the usual ones, then any.</summary>
    private IEnumerable<MenuEntry> ColourMenu()
    {
        // each with its colour beside it; of the section, the colour of its labels
        yield return MenuEntry.Item("Of the section", () => Apply("Colour", s => s.Color = null, p => p.Color = null)).WithSwatch(Look.Text.Color.ToUInt32());
        foreach (var (name, argb) in Colours)
            yield return MenuEntry.Item(name, () => Apply("Colour", s => s.Color = argb, p => p.Color = argb)).WithSwatch(argb);
        yield return MenuEntry.Item("Other colour…", () =>
        {
            var current = _s.Selected().FirstOrDefault()?.Style.Color ?? _s.Patch?.Color ?? 0xFFFFFFFF;
            var flyout = ColourFlyout.Make(() => current, argb => Apply("Colour", s => s.Color = argb, p => p.Color = argb));
            flyout.ShowAt(_swatch);
        }).WithIcon("palette");
    }

    private static readonly (SectionMarkEnds Ends, string Name)[] EndNames =
        { (SectionMarkEnds.Arrow, "Arrows"), (SectionMarkEnds.Tick, "Ticks"), (SectionMarkEnds.Dot, "Dots"), (SectionMarkEnds.None, "No ends") };
    private static readonly (SectionAngleMode Mode, string Name)[] AngleNames =
        { (SectionAngleMode.AsDrawn, "As drawn"), (SectionAngleMode.Acute, "Acute"), (SectionAngleMode.Obtuse, "Obtuse") };
    private static readonly (SectionSlopeFormat Format, string Name)[] SlopeNames =
        { (SectionSlopeFormat.Degrees, "Degrees, 50°"), (SectionSlopeFormat.Percent, "Per cent, 119%"), (SectionSlopeFormat.Ratio, "Ratio, 1:0.84") };

    public static readonly (string Name, uint Argb)[] Colours =
    {
        ("Blue", SectionMarkPreset.Blue), ("Yellow", SectionMarkPreset.Yellow), ("Red", 0xFFEF4444), ("Green", 0xFF22C55E),
        ("Orange", 0xFFF5A623), ("Grey", SectionMarkPreset.Grey), ("White", 0xFFFFFFFF), ("Black", 0xFF000000)
    };

    /// <summary>
    /// A change of style: to the selected marks in one step, and to the marks to come (<see cref="SectionSession.Patch"/>).
    /// </summary>
    private void Apply(string name, Action<SectionMarkStyle> selected, Action<SectionMarkStyle> patch)
    {
        if (_s.SelectedMarks.Count > 0) _s.ChangeSelected("Section mark " + name.ToLowerInvariant(), m => selected(m.Style));
        _s.Patch ??= new SectionMarkStyle { LineWeight = 0 };
        patch(_s.Patch);
        Refresh();
    }

    private List<Control> StyleGroups()
    {
        Button Menu(string icon, string label, string name, Func<IEnumerable<MenuEntry>> items)
        {
            var b = Small(icon, label, name, () => { });
            Ui.DropDown(b, items);
            return b;
        }
        void Each(string what, Action<SectionMarkStyle> change)
        {
            if (_s.SelectedMarks.Count == 0) { _s.SetHint(Loc.T("Select the marks to change first")); return; }
            _s.ChangeSelected(what, m => change(m.Style));
        }
        var figure = new StackPanel
        {
            Children =
            {
                Menu("dimension", "Ends", "markEnds", () => EndNames.Select(e => MenuEntry.Item(e.Name, () => Each("Section mark ends", s => s.Ends = e.Ends)))),
                Menu("text", "Decimals", "markDecimals", () => new[] { 0, 1, 2 }.Select(d => MenuEntry.Item(SectionMarkingText.Sample(d), () => Each("Section mark decimals", s => s.Decimals = d)))
                    .Concat(new[] { MenuEntry.Separator }).Concat(new[] { 0, 1 }.Select(d => MenuEntry.Item(SectionMarkingText.Sample(d) + "°", () => Each("Section mark decimals", s => s.AngleDecimals = d))))),
                Menu("text", "Text size", "markTextSize", () => new[] { 0.8, 1.0, 1.25, 1.5, 2.0 }.Select(k => MenuEntry.Item($"{k * 100:0} %", () => Each("Section mark text size", s => s.TextScale = k))))
            }
        };
        var angle = new StackPanel
        {
            Children =
            {
                Menu("angle", "Angle shown", "markAngleMode", () => AngleNames.Select(m => MenuEntry.Item(m.Name, () => Each("Section angle", s => s.AngleMode = m.Mode)))),
                Menu("slope", "Slope as", "markSlopeFormat", () => SlopeNames.Select(f => MenuEntry.Item(f.Name, () => Each("Section slope", s => s.SlopeFormat = f.Format)))),
                Small("edit", "Own text…", "markOwnText", () =>
                {
                    if (_s.Selected().FirstOrDefault() is not { } first) { _s.SetHint(Loc.T("Select the marks to change first")); return; }
                    _s.AskAt?.Invoke(_s.LastPointer, "Own text (<> is the value)", first.Style.Text ?? "<>", t => Each("Section mark text", s => s.Text = string.IsNullOrWhiteSpace(t) || t == "<>" ? null : t));
                })
            }
        };
        var styles = new StackPanel
        {
            Children =
            {
                Small("save", "Save as style…", "markSaveStyle", async () =>
                {
                    if (_s.Selected().FirstOrDefault() is not { } first) { _s.SetHint(Loc.T("Select the mark whose style to keep")); return; }
                    var name = await Dialogs.Prompt(Loc.T("Save as style…"), Loc.T("Name"), first.Kind.ToString());
                    if (string.IsNullOrWhiteSpace(name)) return;
                    var presets = _vm.Settings.SectionMarkPresets;
                    presets.RemoveAll(p => p.Name == name.Trim());
                    presets.Add(new SectionMarkPreset { Name = name.Trim(), Style = first.Style.Copy() });
                    _vm.Settings.Save();
                    Refresh();
                }),
                Small("check", "Make style default", "markMakeDefault", () =>
                {
                    var marks = _s.Selected();
                    if (marks.Count == 0) { _s.SetHint(Loc.T("Select the mark whose style to keep")); return; }
                    foreach (var m in marks.GroupBy(m => m.Kind)) _vm.Settings.SectionMarkDefaults[m.Key.ToString()] = m.First().Style.Copy();
                    _vm.Settings.Save();
                    _s.SetHint(Loc.T("New marks of this kind take this style"));
                }),
                Menu("trash", "Delete style", "markDeleteStyle", () => _vm.Settings.SectionMarkPresets.Select(p => MenuEntry.Item(Loc.T(p.Name), () =>
                {
                    _vm.Settings.SectionMarkPresets.Remove(p);
                    _vm.Settings.Save();
                    Refresh();
                })))
            }
        };
        // Copy style takes the style of the selected mark and becomes Apply style: each mark clicked then takes it
        _paint = Big("brush", "Copy style", "markCopyStyle", () =>
        {
            if (Active is SectionStyleTool) { SetTool(null); return; }
            if (_s.Selected().FirstOrDefault() is not { } source) return;
            SetTool(new SectionStyleTool(_vm, _s, source.Style));
        });
        var paint = _paint;
        _toolButtons.Add((t => t is SectionStyleTool, paint));
        var clear = Big("trash", "Delete all", "markDeleteAll", async () =>
        {
            if (_s.LineEntity is not { } line || _vm.Project is not { } p || !p.SectionMarks.Any(m => m.SectionLineId == line.Id)) return;
            if (!await Dialogs.Confirm(Loc.T("Delete all the marks drawn on this section?"))) return;
            _vm.Doc?.Execute("Delete section marks", pr => pr.SectionMarks.RemoveAll(m => m.SectionLineId == line.Id));
            _s.SelectedMarks.Clear();
            _s.Touch();
        });
        // the line style first: the colour, type and weight of the selected marks and of those to come
        return new()
        {
            Group("Styles", Ui.Row(10, LineStyle(), Columns(new Control[] { paint }.Concat(TakeOut(styles)).ToArray()))),
            Group("Figure", Columns(figure)), Group("Angle and slope", Columns(angle)),
            Group("Clear", Columns(clear, Small("dimension", "Rebuild dimensions", "markRestoreAuto", _s.RestoreAutoDimensions)))
        };
    }

    // ---- the status line ---------------------------------------------------------------------------------------

    private static readonly (SectionSnapKind Kind, string Name)[] SnapNames =
    {
        (SectionSnapKind.Collar, "Collar"), (SectionSnapKind.Toe, "Hole toe"), (SectionSnapKind.Grade, "Grade"), (SectionSnapKind.Surface, "Surface"),
        (SectionSnapKind.End, "End"), (SectionSnapKind.Intersection, "Intersection"), (SectionSnapKind.Perpendicular, "Perpendicular"), (SectionSnapKind.Frame, "Frame")
    };

    private void BuildSnaps()
    {
        _snaps.Children.Clear();
        foreach (var (kind, name) in SnapNames)
        {
            var key = kind.ToString();
            var chip = Ui.CompactChip(Loc.T(name), () => { }, _s.SnapOn(kind));
            chip.Name = "snap" + key;
            chip.Click += (_, _) =>
            {
                var off = _vm.Settings.SectionSnapsOff;
                if (!off.Remove(key)) off.Add(key);
                _vm.Settings.Save();
                Ui.SetChip(chip, _s.SnapOn(kind));
            };
            _snaps.Children.Add(chip);
        }
        var ortho = Ui.CompactChip(Loc.T("Ortho F8"), () => { }, _vm.Settings.SectionOrtho);
        ortho.Name = "snapOrtho";
        ortho.Click += (_, _) => ToggleOrtho();
        _snaps.Children.Add(ortho);
    }

    public void ToggleOrtho()
    {
        _vm.Settings.SectionOrtho = !_vm.Settings.SectionOrtho;
        _vm.Settings.Save();
        if (_snaps.Children.OfType<Button>().FirstOrDefault(b => b.Name == "snapOrtho") is { } chip) Ui.SetChip(chip, _vm.Settings.SectionOrtho);
    }

    /// <summary>The place of the cursor along the line and its height, and what it snaps to.</summary>
    public void ShowWhere(double sx, double sy)
    {
        var p = _s.Free(sx, sy);
        var snap = _s.Snapped != SectionSnapKind.None && Active is SectionMarkTool ? " · " + Loc.T(SnapNames.FirstOrDefault(n => n.Kind == _s.Snapped).Name ?? "") : "";
        _where.Text = $"S {p.S:0.0} · Z {p.Z:0.0}{snap}";
    }

    /// <summary>The hint, the pressed tool, the style of the selection (or of the marks to come) shown again.</summary>
    public void Refresh()
    {
        _hint.Text = _s.Hint.Length > 0 ? Loc.T(_s.Hint) : "";
        foreach (var (isIt, b) in _toolButtons) b.Background = isIt(Active) ? Look.AccentSoft : Brushes.Transparent;
        if (_paint != null)
        {
            // no style to copy until a mark is selected; while it is given out the button ends it
            var applying = Active is SectionStyleTool;
            _paint.IsEnabled = applying || _s.SelectedMarks.Count > 0;
            if (_paint.Content is StackPanel row && row.Children.OfType<TextBlock>().FirstOrDefault() is { } caption)
                caption.Text = Loc.T(applying ? "Apply style" : "Copy style");
        }
        if (_type == null) return;
        _filling = true;
        try
        {
            var style = _s.Selected().FirstOrDefault()?.Style ?? _s.Patch;
            // solid first, then the types of AutoCAD and of the project, by their names in the language of the program
            _typeNames.Clear();
            _typeNames.Add(null);
            if (_vm.Project is { } project) _typeNames.AddRange(Domain.Model.LineTypes.Names(project).Where(n => !Domain.Model.LineTypes.IsSolid(n)));
            _type.ItemsSource = _typeNames.Select(n => n == null ? Loc.T("Solid") : LineStyleFields.TypeName(n)).ToList();
            var current = style?.LineType is { } t && !Domain.Model.LineTypes.IsSolid(t) ? _typeNames.FindIndex(n => n != null && n.Equals(t, StringComparison.OrdinalIgnoreCase)) : 0;
            _type.SelectedIndex = Math.Max(0, current);
            _weight.SelectedIndex = style is { LineWeight: > 0 } ? Array.IndexOf(Weights, Weights.OrderBy(w => Math.Abs(w - style.LineWeight)).First()) : -1;
            _swatch.Background = style?.Color is uint c ? new SolidColorBrush(Color.FromUInt32(c)) : Look.Text;
            _preset.ItemsSource = _vm.Settings.SectionMarkPresets.Select(p => Loc.T(p.Name)).ToList();
            _preset.SelectedIndex = -1;
        }
        finally { _filling = false; }
    }
}

/// <summary>Samples of the figures for the menus.</summary>
internal static class SectionMarkingText
{
    public static string Sample(int decimals) => Engine.SectionMarking.Number(3.6, decimals);
}
