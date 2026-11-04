using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.App.Views;

/// <summary>Small factory helpers for the shell's flat buttons.</summary>
public static class Ui
{
    /// <summary>
    /// Horizontal strip that scrolls left and right with the mouse wheel: the normal wheel, Shift+wheel and a tilting wheel
    /// all move it sideways. Handled on the way down so buttons inside do not swallow the wheel.
    /// </summary>
    public static ScrollViewer HScroll(Control content)
    {
        var sv = new ScrollViewer
        {
            Content = content,
            // Hidden, not Auto: the bar would cover the group captions; the strip still scrolls with the wheel and by dragging.
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        sv.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            var room = sv.Extent.Width - sv.Viewport.Width;
            if (room <= 0) return;
            var step = (Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? -e.Delta.X : -e.Delta.Y) * 60;
            sv.Offset = new Vector(Math.Clamp(sv.Offset.X + step, 0, room), sv.Offset.Y);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Drag with the left button (like a touch swipe). A move under the threshold stays a click on the button below;
        // once dragging, the strip takes the pointer so the button does not fire on release.
        const double threshold = 6;
        Point? down = null;
        var startOffset = 0.0;
        var dragging = false;
        sv.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(sv).Properties.IsLeftButtonPressed || e.Pointer.Type == PointerType.Touch) return;
            down = e.GetPosition(sv);
            startOffset = sv.Offset.X;
            dragging = false;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        sv.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (down is not Point d) return;
            if (!e.GetCurrentPoint(sv).Properties.IsLeftButtonPressed) { down = null; dragging = false; return; }
            var dx = e.GetPosition(sv).X - d.X;
            var room = sv.Extent.Width - sv.Viewport.Width;
            if (!dragging && (Math.Abs(dx) < threshold || room <= 0)) return;
            if (!dragging)
            {
                dragging = true;
                e.Pointer.Capture(sv);
            }
            sv.Offset = new Vector(Math.Clamp(startOffset - dx, 0, Math.Max(0, room)), sv.Offset.Y);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        sv.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (dragging)
            {
                e.Pointer.Capture(null);
                e.Handled = true;
            }
            down = null;
            dragging = false;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        sv.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => { if (!dragging) down = null; });
        return sv;
    }

    public static Button Flat(Control content, string? tip = null, Action? click = null, double? width = null, double height = 34)
    {
        var b = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0),
            Height = height,
            CornerRadius = new CornerRadius(6),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Foreground = Look.TextSoft
        };
        b.Classes.Add("flat");
        if (width != null) b.Width = width.Value;
        if (tip != null) ToolTip.SetTip(b, tip);
        if (click != null) b.Click += (_, _) => Safe(click);
        return b;
    }

    public static Button Chip(string text, Action click, bool active = false, string? tip = null)
    {
        var b = new Button
        {
            Content = text,
            Height = 28,
            Padding = new Thickness(10, 0),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        b.Classes.Add("flat");
        SetChip(b, active);
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => Safe(click);
        return b;
    }

    public static void SetChip(Button b, bool active)
    {
        b.Background = active ? Look.AccentSoft : Look.Panel;
        b.BorderBrush = active ? Look.Accent : Look.Line;
        b.Foreground = active ? Look.Accent : Look.Muted;
    }

    /// <summary>
    /// Makes a button open a menu built at click time (so its items and enabled states are current).
    /// The menu is created with its items already in place: a flyout that starts empty never opens.
    /// </summary>
    /// <summary>The last menu opened by <see cref="DropDown"/> (used by the UI smoke test).</summary>
    public static ContextMenu? LastMenu { get; private set; }

    /// <summary>Records a menu opened elsewhere as the last one (for the UI smoke test).</summary>
    public static void Remember(ContextMenu menu) => LastMenu = menu;

    public static void DropDown(Button b, Func<IEnumerable<MenuEntry>> items, PlacementMode placement = PlacementMode.BottomEdgeAlignedLeft)
    {
        b.Click += (_, _) =>
        {
            try
            {
                var list = items().ToList();
                if (list.Count == 0) return;
                var menu = new ContextMenu { ItemsSource = MenuEntry.Build(list), Placement = placement, PlacementTarget = b };
                menu.Open(b);
                LastMenu = menu;
            }
            catch (Exception ex) { Dialogs.ShowError(ex); }
        };
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { Dialogs.ShowError(ex); }
    }

    public static string Tip(RibbonItem it) =>
        Loc.Ui(it.Label) + (it.Key != null ? $" ({it.Key})" : "") + (it.Lock != null ? " — " + Loc.T(it.Lock) : "");
}

/// <summary>Stage tabs and the ribbon of the selected stage.</summary>
public sealed class RibbonBar : DockPanel
{
    private readonly MainViewModel _vm;
    private readonly List<Stage> _stages;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly StackPanel _groups = new() { Orientation = Orientation.Horizontal };
    private readonly List<(RibbonItem Item, Button Button)> _buttons = new();

    public RibbonBar(MainViewModel vm)
    {
        _vm = vm;
        _stages = vm.BuildStages();
        var nav = new DockPanel { Background = Look.Panel, Height = 30 };
        var hint = new TextBlock { Text = Loc.Ui("Alt+1…7 — switch stage"), Foreground = Look.Faint, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) };
        DockPanel.SetDock(hint, Dock.Right);
        nav.Children.Add(hint);
        var tabsScroll = Ui.HScroll(_tabs);
        tabsScroll.Padding = new Thickness(12, 0, 0, 0);
        nav.Children.Add(tabsScroll);
        SetDock(nav, Dock.Top);
        Children.Add(nav);
        Children.Add(new Border
        {
            Background = Look.Ribbon,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(0, 1),
            MinHeight = 100,
            Child = Ui.HScroll(_groups)
        });
        Select(2);
    }

    public int Current { get; private set; } = -1;
    public IReadOnlyList<Stage> Stages => _stages;

    public void Select(int index)
    {
        if (index < 0 || index >= _stages.Count) return;
        Current = index;
        _tabs.Children.Clear();
        for (int i = 0; i < _stages.Count; i++)
        {
            var idx = i;
            var active = i == index;
            var b = new Button
            {
                Content = Ui.Row(6, new TextBlock { Text = (i + 1).ToString(), FontFamily = Look.Mono, FontSize = 10, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = Loc.Ui(_stages[i].Name), FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal, Foreground = active ? Look.Accent : Look.Muted }),
                Height = 30,
                Padding = new Thickness(14, 0),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Background = active ? Look.Ribbon : Brushes.Transparent,
                BorderBrush = Look.Accent,
                BorderThickness = new Thickness(0, active ? 2 : 0, 0, 0)
            };
            b.Click += (_, _) => { if (idx != Current) IO.UsageLog.Track(IO.UsageLog.Tab, _stages[idx].Name, "ribbon"); Select(idx); };
            _tabs.Children.Add(b);
        }
        BuildGroups(_stages[index]);
        _vm.ActiveStage = _stages[index].Name;
        _vm.View.Redraw();
    }

    private void BuildGroups(Stage stage)
    {
        _groups.Children.Clear();
        _buttons.Clear();
        foreach (var g in stage.Groups)
        {
            var bigs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            var smalls = new WrapPanel { Orientation = Orientation.Vertical, Height = 3 * 25 };
            foreach (var it in g.Items)
            {
                var b = it.Big ? BigButton(it) : SmallButton(it);
                (it.Big ? (Panel)bigs : smalls).Children.Add(b);
                _buttons.Add((it, b));
            }
            var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (bigs.Children.Count > 0) body.Children.Add(bigs);
            if (smalls.Children.Count > 0) body.Children.Add(smalls);
            var group = new DockPanel { Margin = new Thickness(8, 6, 8, 4) };
            var caption = new TextBlock { Text = Loc.Ui(g.Name), Foreground = Look.Accent, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            DockPanel.SetDock(caption, Dock.Bottom);
            group.Children.Add(caption);
            group.Children.Add(body);
            _groups.Children.Add(new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = group });
        }
        Refresh();
    }

    private Button BigButton(RibbonItem it)
    {
        var stack = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
        var icon = new Panel { Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Center };
        icon.Children.Add(Look.Icon(it.Icon, 26, it.Lock != null ? Look.Faint : Look.Text, 1.5));
        if (it.Lock != null || it.Drop != null)
            icon.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -10, 0), Child = Look.Icon(it.Lock != null ? "lock" : "down", 11, Look.Faint, 2) });
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock { Text = Loc.Ui(it.Label), FontSize = 11.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 116, Foreground = Look.Text });
        if (it.Key != null) stack.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = Look.Kbd(it.Key, 9) });
        var b = new Button
        {
            Content = stack,
            MinWidth = 72,
            MinHeight = 76,
            Padding = new Thickness(2, 6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        b.Classes.Add("flat");
        Wire(b, it);
        return b;
    }

    private Button SmallButton(RibbonItem it)
    {
        var row = Ui.Row(6, Look.Icon(it.Icon, 15, it.Lock != null ? Look.Faint : Look.Muted),
            new TextBlock { Text = Loc.Ui(it.Label), FontSize = 12, Foreground = it.Lock != null ? Look.Faint : Look.TextSoft, VerticalAlignment = VerticalAlignment.Center });
        if (it.Drop != null) row.Children.Add(Look.Icon("down", 11, Look.Faint, 2));
        if (it.Lock != null) row.Children.Add(Look.Icon("lock", 11, Look.Faint, 2));
        if (it.Key != null) row.Children.Add(Look.Kbd(it.Key, 9));
        var b = new Button
        {
            Content = row,
            Height = 24,
            Padding = new Thickness(8, 0),
            Margin = new Thickness(0, 0, 2, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        b.Classes.Add("flat");
        Wire(b, it);
        return b;
    }

    private void Wire(Button b, RibbonItem it)
    {
        ToolTip.SetTip(b, Ui.Tip(it));
        if (it.Drop != null)
        {
            Ui.DropDown(b, it.Drop);
        }
        else if (it.Run != null)
            b.Click += (_, _) => { IO.UsageLog.Track(IO.UsageLog.Button, it.Label, "ribbon"); Ui.Safe(it.Run); Refresh(); };
    }

    /// <summary>Updates enabled state and the highlight of the active tool.</summary>
    public void Refresh()
    {
        var tool = _vm.ActiveTool?.Name;
        foreach (var (it, b) in _buttons)
        {
            b.IsEnabled = it.Lock == null && (it.Run != null || it.Drop != null) && (it.Enabled?.Invoke() ?? true);
            b.Background = it.ToolName != null && it.ToolName == tool ? Look.AccentSoft : Brushes.Transparent;
        }
    }
}

/// <summary>Search box that runs any command by name (Ctrl+K).</summary>
public sealed class CommandPalette : Border
{
    private readonly MainViewModel _vm;
    private readonly TextBox _box = new()
    {
        Watermark = Loc.Ui("Command, hole, setting…"),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        MinWidth = 160,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly ListBox _list = new() { MaxHeight = 360, MinWidth = 420, Background = Look.Panel };
    private readonly Popup _popup = new() { Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = true };
    private List<(string Title, Action Action)> _all = new();
    private List<(string Title, Action Action)> _shown = new();

    public CommandPalette(MainViewModel vm)
    {
        _vm = vm;
        Background = Look.Field;
        BorderBrush = Look.Line;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Height = 34;
        Padding = new Thickness(10, 0, 6, 0);
        var row = new DockPanel();
        var icon = Look.Icon("search", 16, Look.Faint);
        DockPanel.SetDock(icon, Dock.Left);
        var kbd = Look.Kbd("Ctrl K");
        DockPanel.SetDock(kbd, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(kbd);
        row.Children.Add(_box);
        Child = new Panel { Children = { row, _popup } };
        _popup.PlacementTarget = this;
        _popup.Child = new Border { Background = Look.Panel, BorderBrush = Look.LineStrong, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(4), Child = _list };
        _box.GotFocus += (_, _) => { Index(); Filter(); };
        _box.TextChanged += (_, _) => Filter();
        _box.KeyDown += OnKey;
        _list.DoubleTapped += (_, _) => Run();
    }

    public void Focus2() { _box.Focus(); _box.SelectAll(); }

    /// <summary>Every command of the classic menu and the ribbon, with a localised title.</summary>
    private void Index()
    {
        var list = new List<(string, Action)>();
        foreach (var (path, e) in _vm.CommandIndex)
        {
            if (e.Action == null || e.Children != null) continue;
            var title = string.Join(" ▸ ", path.Split('/').Select(Loc.T));
            var entry = e;
            list.Add((title, () => { if (entry.IsEnabled?.Invoke() != false) entry.Action!(); }));
        }
        foreach (var s in _vm.BuildStages())
            foreach (var g in s.Groups)
                foreach (var it in g.Items.Where(i => i.Run != null && i.Lock == null))
                    list.Add(($"{Loc.Ui(s.Name)} ▸ {Loc.Ui(it.Label)}", it.Run!));
        _all = list.GroupBy(x => x.Item1).Select(g => g.First()).ToList();
    }

    private void Filter()
    {
        var q = (_box.Text ?? "").Trim();
        if (q.Length == 0) { _popup.IsOpen = false; return; }
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _shown = _all.Where(x => words.All(w => x.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase))).Take(40).ToList();
        // Hole labels: "#12" or a bare label jumps to the hole.
        if (_vm.Project != null)
            foreach (var h in _vm.Project.Holes.Where(h => string.Equals(h.Label, q.TrimStart('#'), StringComparison.OrdinalIgnoreCase)).Take(3))
            {
                var hole = h;
                _shown.Insert(0, (Loc.F("Hole {0}", h.Label), () => { _vm.Doc!.Select(new[] { hole.Id }); _vm.ZoomSelection(); }));
            }
        _list.ItemsSource = _shown.Select(x => x.Title).ToList();
        _list.SelectedIndex = _shown.Count > 0 ? 0 : -1;
        _popup.IsOpen = _shown.Count > 0;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: _list.SelectedIndex = Math.Min(_shown.Count - 1, _list.SelectedIndex + 1); e.Handled = true; break;
            case Key.Up: _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1); e.Handled = true; break;
            case Key.Enter: Run(); e.Handled = true; break;
            case Key.Escape: _box.Text = ""; _popup.IsOpen = false; _vm.View.Focus(); e.Handled = true; break;
        }
    }

    private void Run()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _shown.Count) return;
        var action = _shown[_list.SelectedIndex].Action;
        _box.Text = "";
        _popup.IsOpen = false;
        _vm.View.Focus();
        Ui.Safe(action);
    }
}

/// <summary>Floating panel shown over the plan while a drawing tool is active: its parameters and Tab / Esc / Enter.</summary>
public sealed class ToolPanel : Border
{
    private readonly MainViewModel _vm;
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, Foreground = Look.Text };
    private readonly TextBlock _hint = new() { FontSize = 11, Foreground = Look.Faint, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis };
    // the fields stay on one line; a narrow window scrolls them sideways instead of wrapping them
    private readonly StackPanel _fields = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ScrollViewer _fieldsScroll;

    public ToolPanel(MainViewModel vm)
    {
        _vm = vm;
        Background = Look.Ribbon;
        BorderBrush = Look.LineStrong;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(14, 8, 10, 8);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        // as wide as its fields need, up to the width of the viewport; beyond that the fields scroll
        Margin = new Thickness(16, 0, 16, 16);
        BoxShadow = BoxShadows.Parse("0 8 24 0 #73000000");
        var done = new Button
        {
            Content = Ui.Row(6, new TextBlock { Text = Loc.Ui("Done"), FontWeight = FontWeight.SemiBold, Foreground = Look.OnAccent }, Look.Kbd("Enter")),
            Background = Look.Accent,
            Height = 30,
            Padding = new Thickness(12, 0),
            CornerRadius = new CornerRadius(6)
        };
        ToolTip.SetTip(done, Loc.Ui("Done") + " (Enter)");
        done.Click += (_, _) => Ui.Safe(_vm.CompleteTool);
        var cancel = Outline(Loc.Ui("Cancel"), "Esc", () => { _vm.ActiveTool?.Cancel(); _vm.SelectTool(); });
        var coords = Outline(Loc.Ui("Coordinates"), "Tab", () => _ = _vm.EnterCoordinates());
        _fieldsScroll = new ScrollViewer
        {
            Content = _fields,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 0, 4),
            Name = "toolFields"
        };
        var body = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(new StackPanel { Children = { _title, _hint } });
        body.Children.Add(_fieldsScroll);
        body.Children.Add(Ui.Row(6, coords, cancel, done));
        Child = body;
        IsVisible = false;
    }

    private static Button Outline(string text, string key, Action click)
    {
        var b = new Button
        {
            Content = Ui.Row(6, new TextBlock { Text = text, Foreground = Look.TextSoft }, Look.Kbd(key)),
            Height = 30,
            Padding = new Thickness(10, 0),
            Background = Brushes.Transparent,
            BorderBrush = Look.LineStrong,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        b.Click += (_, _) => Ui.Safe(click);
        return b;
    }

    /// <summary>Shows the panel for the active tool; layout tools also get their pattern parameters as fields.</summary>
    public void Refresh()
    {
        var tool = _vm.ActiveTool;
        IsVisible = tool != null && tool is not SelectTool && _vm.Project != null;
        if (!IsVisible) { _fields.Children.Clear(); return; }
        _title.Text = Loc.T(tool!.Name);
        _hint.Text = Loc.T(tool.Hint);
        ToolTip.SetTip(_hint, _hint.Text);
        _fields.Children.Clear();
        var d = _vm.Project!.Design;
        if (tool.Name is "Pattern" or "Row" or "Hole" or "Dummy hole")
        {
            if (tool.Name is "Pattern" or "Row") _fields.Children.Add(Field("Burden, m", d.Burden, v => _vm.Change("Design data", p => p.Design.Burden = v)));
            if (tool.Name is "Pattern" or "Row") _fields.Children.Add(Field("Spacing, m", d.Spacing, v => _vm.Change("Design data", p => p.Design.Spacing = v)));
            _fields.Children.Add(Field("Diameter, mm", d.Diameter, v => _vm.Change("Design data", p => p.Design.Diameter = v)));
            _fields.Children.Add(Field("Angle, °", d.Angle, v => _vm.Change("Design data", p => p.Design.Angle = v)));
            if (tool.Name == "Pattern")
            {
                var combo = new ComboBox { ItemsSource = Enum.GetValues<PatternType>().Select(x => Loc.Enum(x)).ToList(), SelectedIndex = (int)d.Pattern, Width = 120, Height = 26 };
                combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) _vm.Change("Design data", p => p.Design.Pattern = (PatternType)combo.SelectedIndex); };
                _fields.Children.Add(Labelled("Pattern", combo));
                // what the pattern dialog used to ask after the outline was drawn
                _fields.Children.Add(Field("Direction, °", _vm.Project.Header.BlastOrientation, v => _vm.Change("Blast orientation", p => p.Header.BlastOrientation = v), "patternDirection"));
                _fields.Children.Add(Field("Edge offset, m", _vm.PatternEdgeOffset, v => _vm.PatternEdgeOffset = v, "patternOffset"));
                _fields.Children.Add(TextField("Number prefix", _vm.PatternLabelPrefix, v => _vm.PatternLabelPrefix = v, "patternPrefix"));
                _fields.Children.Add(Field("Start number", _vm.NextPatternNumber, v => _vm.PatternStartNumber = Math.Max(0, (int)Math.Round(v)), "patternStart"));
            }
        }
        if (tool.Name == "Tie")
        {
            var chain = new CheckBox { Content = Loc.Ui("Tie chain"), IsChecked = _vm.TieChain, Name = "tieChain", VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(chain, Loc.Ui("On: ties go in turn through the holes on the line. Off: one tie from the first hole to the last"));
            chain.IsCheckedChanged += (_, _) => _vm.TieChain = chain.IsChecked == true;
            _fields.Children.Add(chain);
        }
        if (tool.Name is "Set" or "Row set")
            _fields.Children.Add(Field("Step, ms", _vm.Project.Electronic.HoleIntervalMs, v => _vm.Change("Electronic settings", p => p.Electronic.HoleIntervalMs = v)));
        _fieldsScroll.IsVisible = _fields.Children.Count > 0;
    }

    private static Control Labelled(string label, Control c) =>
        new StackPanel { Spacing = 2, Children = { new TextBlock { Text = Loc.Ui(label), FontSize = 10.5, Foreground = Look.Faint }, c } };

    private static Control TextField(string label, string value, Action<string> set, string name)
    {
        var box = new TextBox { Text = value, Width = 92, Height = 26, MinHeight = 26, Padding = new Thickness(8, 2), FontFamily = Look.Mono, FontSize = 12, Background = Look.Field, Name = name };
        void Commit() { if ((box.Text ?? "") != value) { value = box.Text ?? ""; Ui.Safe(() => set(value)); } }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        return Labelled(label, box);
    }

    private static Control Field(string label, double value, Action<double> set, string? name = null)
    {
        var box = new TextBox
        {
            Name = name,
            Text = value.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture),
            Width = 92,
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(8, 2),
            FontFamily = Look.Mono,
            FontSize = 12,
            Background = Look.Field
        };
        void Commit()
        {
            if (double.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var v) && Math.Abs(v - value) > 1e-9) { value = v; Ui.Safe(() => set(v)); }
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        return Labelled(label, box);
    }
}

/// <summary>Right-hand inspector: properties of the selection, layers and visibility, as tabs.</summary>
public sealed class Inspector : DockPanel
{
    private readonly MainViewModel _vm;
    private readonly TabControl _tabs = new();
    private readonly StackPanel _selectionBar = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(12, 8, 12, 0) };
    private readonly StackPanel _visibility = new() { Spacing = 2, Margin = new Thickness(12, 8) };

    public Inspector(MainViewModel vm, PropertiesPanel properties, LayersPanel layers)
    {
        _vm = vm;
        Background = Look.Panel;
        var wrap = new WrapPanel { Margin = new Thickness(12, 8, 12, 0) };
        foreach (var (label, action) in new (string, Action)[] { ("Like", vm.SelectLike), ("Exact", vm.SelectExact), ("Invert", vm.InvertSelection), ("Only selected", vm.ShowOnlySelected), ("Show all", vm.ShowAllEntities) })
        {
            var chip = Ui.Chip(Loc.Ui(label), action);
            chip.Margin = new Thickness(0, 0, 4, 4);
            wrap.Children.Add(chip);
        }
        var props = new DockPanel();
        DockPanel.SetDock(wrap, Dock.Top);
        props.Children.Add(wrap);
        props.Children.Add(properties);
        _tabs.ItemsSource = new[]
        {
            new TabItem { Header = Loc.Ui("Properties"), Tag = "Properties", Content = props },
            new TabItem { Header = Loc.Ui("Layers"), Tag = "Layers", Content = layers },
            new TabItem { Header = Loc.Ui("Visibility"), Tag = "Visibility", Content = new ScrollViewer { Content = _visibility } }
        };
        _tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != _tabs) return;
            if (_tabs.SelectedIndex == 2) BuildVisibility();
            // only the user's clicks count; the program switches tabs through SelectedTab
            if (!_switching && _tabs.SelectedItem is TabItem { Tag: string id }) IO.UsageLog.Track(IO.UsageLog.Tab, id, "panel");
        };
        // object counts change with edits and file loads
        vm.ModelRefreshed += () => { if (_tabs.SelectedIndex == 2) BuildVisibility(); };
        Children.Add(_tabs);
    }

    private bool _switching;

    public int SelectedTab
    {
        get => _tabs.SelectedIndex;
        set
        {
            _switching = true;
            try { _tabs.SelectedIndex = value; }
            finally { _switching = false; }
        }
    }

    private void BuildVisibility()
    {
        _visibility.Children.Clear();
        var p = _vm.Project;
        if (p == null) return;
        BuildObjectVisibility(p);
        // Two columns, like a hole legend; long names wrap inside their column.
        UniformGrid Columns() => new() { Columns = 2 };
        UniformGrid? grid = null;
        void Add(CheckBox cb)
        {
            cb.Content = new TextBlock { Text = (string)cb.Content!, TextWrapping = TextWrapping.Wrap };
            cb.Margin = new Thickness(0, 0, 6, 0);
            cb.VerticalAlignment = VerticalAlignment.Top;
            grid!.Children.Add(cb);
        }
        // tip: where the item shows, what it is for and which calculations use the same data
        void Check(string label, Func<ViewState, bool> get, Action<ViewState> toggle, bool ui = false, string? tip = null)
        {
            var cb = new CheckBox { Content = ui ? Loc.Ui(label) : Loc.T(label), IsChecked = get(p.View) };
            if (tip != null) ToolTip.SetTip(cb, Loc.Ui(tip));
            cb.IsCheckedChanged += (_, _) => { if (cb.IsChecked != get(p.View)) _vm.ToggleView(toggle); };
            Add(cb);
        }
        void LabelCheck(string label, HoleLabelMode flag, string? tip = null) =>
            Check(label, v => v.HoleLabels.HasFlag(flag), v => v.HoleLabels ^= flag, ui: true, tip: tip);
        void TimingCheck(string label, TimingLabelMode mode, string? tip = null)
        {
            var cb = new CheckBox { Content = Loc.Ui(label), IsChecked = _vm.IsTimingLabelVisible(mode) };
            if (tip != null) ToolTip.SetTip(cb, Loc.Ui(tip));
            cb.IsCheckedChanged += (_, _) =>
            {
                if (cb.IsChecked != _vm.IsTimingLabelVisible(mode))
                    _vm.ToggleTimingLabel(mode);
            };
            Add(cb);
        }
        _visibility.Children.Add(new TextBlock { Text = Loc.Ui("Show"), Foreground = Look.Faint, FontSize = 11, Margin = new Thickness(0, 10, 0, 4) });
        grid = Columns();
        Check("Ties", v => v.ShowTies, v => v.ShowTies = !v.ShowTies);
        Check("Rows", v => v.ShowRows, v => v.ShowRows = !v.ShowRows);
        Check("Surfaces", v => v.ShowSurfaces, v => v.ShowSurfaces = !v.ShowSurfaces);
        Check("Grid", v => v.ShowGrid, v => v.ShowGrid = !v.ShowGrid);
        Check("Axes", v => v.ShowAxes, v => v.ShowAxes = !v.ShowAxes);
        Check("Hole legend", v => v.ShowHoleLegend, v => v.ShowHoleLegend = !v.ShowHoleLegend);
        _visibility.Children.Add(grid);

        _visibility.Children.Add(new TextBlock { Text = Loc.Ui("Holes"), Foreground = Look.Faint, FontSize = 11, Margin = new Thickness(0, 10, 0, 4) });
        grid = Columns();
        LabelCheck("Hole number", HoleLabelMode.Label,
            tip: "Plan, 3D and printed plans: the hole number in blue top right of the collar. Tells the holes apart in the hole table, loading sheets, reports and the electronic logging report; holes are found by it.");
        LabelCheck("Length", HoleLabelMode.Length,
            tip: "Plan and 3D: \"L\" next to the collar, the dipped length when measured, otherwise the design length. Used for the charge column and stemming, drilled metres and drilling cost, and for matching loading rules by length.");
        LabelCheck("Diameter", HoleLabelMode.Diameter,
            tip: "Plan and 3D: \"Ø\" in mm next to the collar. Sets the charge per metre and the explosive mass, the minimum stemming (in diameters), the fragmentation prediction, and the loading rules chosen by diameter.");
        LabelCheck("Elevation", HoleLabelMode.CollarZ,
            tip: "Plan and 3D: \"Z\", the collar elevation. Used for the length down to the grade surface in the design, for sections and for the volume of the blast.");
        LabelCheck("Angle", HoleLabelMode.Angle,
            tip: "Plan and 3D: \"α\", the angle from vertical. Sets where the toe is; used for the bench height in the fragmentation prediction, flyrock and the drilling tolerance check.");
        LabelCheck("Bearing", HoleLabelMode.Bearing,
            tip: "Plan and 3D: \"Az\", the bearing of the lean clockwise from north. Sets where the toe of an inclined hole is; used in the drilling tolerance check.");
        LabelCheck("Temperature", HoleLabelMode.Temperature,
            tip: "Plan and 3D: measured temperature in °C, only for holes where it was measured. Marks hot or reactive ground before loading; not used in the calculations.");
        Check("Hole track", v => !v.CollarsOnly, v => v.CollarsOnly = !v.CollarsOnly, ui: true,
            tip: "Plan and 3D: the line from collar to toe with a dot at the toe. Shows the direction and inclination of the holes; display only, the calculations use the hole data whether it is on or off.");
        Check("Collar", v => v.ShowCollars, v => v.ShowCollars = !v.ShowCollars, ui: true,
            tip: "Plan and 3D: the collar circle, filled by hole state (designed — empty, drilled — pale, loaded — solid). When off, selected holes show no collar either; holes can still be picked. Display only.");
        Check("Dip track", v => v.ShowDipTrack, v => v.ShowDipTrack = !v.ShowDipTrack, ui: true,
            tip: "Plan and 3D: a thick cyan line down to the dipped depth with a mark at its end, only for dipped holes. The dipped length replaces the design length in loading, quantities and the tolerance check.");
        Check("Charge track", v => v.ShowLoading, v => v.ShowLoading = !v.ShowLoading, ui: true,
            tip: "Plan and 3D: the decks along the hole in their product colours, and the collar filled with the explosive colour. Shows the loaded column; the same decks give the explosive mass, quantities, costs and the vibration charge.");
        Check("Actual track", v => v.ShowActualTrack, v => v.ShowActualTrack = !v.ShowActualTrack, ui: true,
            tip: "Plan and 3D: the surveyed bore track, dashed orange, only for surveyed holes. Used by the drilling tolerance check (toe, angle and bearing deviation) and the dashboard quality figures.");
        Check("Grade point", v => v.ShowGradePoint, v => v.ShowGradePoint = !v.ShowGradePoint, ui: true,
            tip: "Plan and 3D: a green diamond where the hole crosses the design grade (length minus subdrill). Shows the bench floor; the bench height from it is used in quantities, volumes and the fragmentation prediction.");
        Check("Deviation", v => v.ShowDeviation, v => v.ShowDeviation = !v.ShowDeviation, ui: true,
            tip: "Plan and 3D: red lines with \"Δ\" from the design collar to the actual collar and from the design toe to the surveyed toe. The same deviations are compared with the tolerances in the drilling tolerance check.");
        LabelCheck("Explosive mass", HoleLabelMode.Charge,
            tip: "Plan and 3D: explosive mass of the hole in kg. Comes from the charge column; adds up to the quantities, powder factor, costs and the maximum charge per delay for vibration.");
        Check("Burden marker", v => v.ShowBurdenMarker, v => v.ShowBurdenMarker = !v.ShowBurdenMarker, ui: true,
            tip: "Plan: a dashed violet line with \"B\" from each front-row hole to the nearest point of the crest. Needs a crest line; the same burden is used by the burden check and the face burst (flyrock) prediction.");
        LabelCheck("Deck masses", HoleLabelMode.DeckMasses,
            tip: "Plan and 3D: the mass of each explosive deck in kg, top to bottom. Shows how a decked hole is split; each deck counts on its own in the charge per delay.");
        Check("MWD profile", v => v.ShowProfile, v => v.ShowProfile = !v.ShowProfile, ui: true,
            tip: "Plan and 3D: rock hardness from the measure-while-drilling log along the track, blue soft to red hard, only for holes with a log. Loading rules use it to switch to the hard or soft rock product and lengthen stemming in a soft collar.");
        TimingCheck("Hole order", TimingLabelMode.HoleSequence,
            tip: "Plan and 3D: the firing order of the hole top left of the collar, from the timing. Checks the initiation sequence; the same order drives the blast animation and the first-movement analysis.");
        LabelCheck("Deck sequence", HoleLabelMode.DeckSequence,
            tip: "Plan and 3D: the firing order of the decks in the hole, top to bottom (\"2-1\" — the bottom deck fires first), only for holes with two or more primed decks. Checks the in-hole delays of decked holes.");
        TimingCheck("In-hole delay", TimingLabelMode.DelayNumber,
            tip: "Plan and 3D: the in-hole detonator delay in ms, in red under the collar. It is added to the surface time to give the hole firing time.");
        TimingCheck("Surface time", TimingLabelMode.SurfaceTime,
            tip: "Plan and 3D: the time the surface signal reaches the collar in ms, in red under the collar. Comes from the ties and connectors; part of the hole firing time.");
        LabelCheck("Detonator ID", HoleLabelMode.DetonatorId,
            tip: "Plan and 3D: the logged electronic detonator ID, only for holes that have one. Used in the electronic logging report and to find a hole by its detonator.");
        TimingCheck("Hole time", TimingLabelMode.InholeTime,
            tip: "Plan and 3D: the hole firing time in ms (surface time plus in-hole delay), in red under the collar. The vibration prediction (charge per delay window), isochrones, the blast animation and first movement use it.");
        LabelCheck("Hole type", HoleLabelMode.Type,
            tip: "Plan and 3D: \"T\" and the hole type number. The type sets the hole colour, chooses the loading and initiation rules and filters the holes shown.");
        _visibility.Children.Add(grid);
        _visibility.Children.Add(new TextBlock { Text = Loc.T("Hole types"), Foreground = Look.Faint, FontSize = 11, Margin = new Thickness(0, 10, 0, 4) });
        foreach (var t in p.Design.HoleTypes)
        {
            var idx = t.Index;
            var cb = new CheckBox { Content = Loc.Name(t.Name), IsChecked = !p.View.HiddenHoleTypes.Contains(idx) };
            cb.IsCheckedChanged += (_, _) => _vm.ToggleHoleType(idx);
            _visibility.Children.Add(cb);
        }
        var symbols = new Expander
        {
            Header = Loc.Ui("Hole symbols"),
            IsExpanded = _symbolsOpen,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 12, 0, 0),
            Content = HoleSymbolLegend.Build(p, _vm.Settings.BackgroundColor)
        };
        symbols.Expanded += (_, _) => _symbolsOpen = true;
        symbols.Collapsed += (_, _) => _symbolsOpen = false;
        _visibility.Children.Add(symbols);
        if (SurfaceLegend.Build(p, _vm.Settings) is { } surfaceKey)
        {
            var surfaces = new Expander
            {
                Header = Loc.Ui("Surface colours"),
                IsExpanded = _surfacesOpen,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 8, 0, 0),
                Content = surfaceKey
            };
            surfaces.Expanded += (_, _) => _surfacesOpen = true;
            surfaces.Collapsed += (_, _) => _surfacesOpen = false;
            _visibility.Children.Add(surfaces);
        }
    }

    /// <summary>The symbol and surface keys stay open or closed across rebuilds of the tab.</summary>
    private bool _symbolsOpen = true, _surfacesOpen = true;

    /// <summary>One check box per object kind with its count, and quick isolate / hide / show-all actions.</summary>
    private void BuildObjectVisibility(BlastProject p)
    {
        _visibility.Children.Add(new TextBlock { Text = Loc.Ui("Objects"), Foreground = Look.Faint, FontSize = 11, Margin = new Thickness(0, 4) });
        foreach (var kind in p.Entities.GroupBy(e => e.KindName).OrderBy(g => g.Key))
        {
            var ids = kind.Select(e => e.Id).ToList();
            var cb = new CheckBox { Content = $"{Loc.T(kind.Key)} ({ids.Count})", IsChecked = !ids.All(_vm.HiddenEntities.Contains) };
            cb.IsCheckedChanged += (_, _) =>
            {
                foreach (var id in ids) { if (cb.IsChecked == true) _vm.HiddenEntities.Remove(id); else _vm.HiddenEntities.Add(id); }
                _vm.View.Redraw();
            };
            _visibility.Children.Add(cb);
        }
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Action(string label, Action run)
        {
            var chip = Ui.Chip(Loc.T(label), () => { if (_vm.Doc == null) return; run(); _vm.View.Redraw(); BuildVisibility(); });
            chip.Margin = new Thickness(0, 0, 4, 4);
            actions.Children.Add(chip);
        }
        Action("Show only the selection", () =>
        {
            _vm.HiddenEntities.Clear();
            foreach (var e in p.Entities.Where(e => !_vm.Doc!.Selection.Contains(e.Id))) _vm.HiddenEntities.Add(e.Id);
        });
        Action("Hide all", () => { foreach (var e in p.Entities) _vm.HiddenEntities.Add(e.Id); _vm.Doc!.ClearSelection(); });
        Action("Hide the selection", () => { foreach (var id in _vm.Doc!.Selection) _vm.HiddenEntities.Add(id); _vm.Doc.ClearSelection(); });
        Action("Show all", () => _vm.HiddenEntities.Clear());
        _visibility.Children.Add(actions);
    }
}
