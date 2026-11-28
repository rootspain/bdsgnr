using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
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

    /// <summary>A small chip for narrow windows (the initiation catalogue): 22 high, square corners of 6, text of 11.</summary>
    public static Button CompactChip(string text, Action click, bool active = false, string? tip = null)
    {
        var b = Chip(text, click, active, tip);
        b.Height = 22;
        b.Padding = new Thickness(8, 0);
        b.CornerRadius = new CornerRadius(6);
        b.FontSize = 11;
        return b;
    }

    public static void SetChip(Button b, bool active)
    {
        b.Background = active ? Look.AccentSoft : Look.Panel;
        b.BorderBrush = active ? Look.Accent : Look.Line;
        b.Foreground = active ? Look.Accent : Look.Muted;
        // under the mouse the chip keeps saying whether it is on (App.axaml: Button.chip and Button.chip.on)
        b.Classes.Set("chip", true);
        b.Classes.Set("on", active);
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

    /// <summary>A folder cut in the middle to fit a menu: the drive and the last folders stay ("D:\Работа\…\Взрывы\2026").</summary>
    public static string ShortPath(string folder, int max = 48)
    {
        if (folder.Length <= max) return folder;
        var parts = folder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tail = "";
        for (int i = parts.Length - 1; i > 0; i--)
        {
            var longer = Path.DirectorySeparatorChar + parts[i] + tail;
            if (parts[0].Length + 2 + longer.Length > max) break;
            tail = longer;
        }
        return tail.Length > 0 ? $"{parts[0]}{Path.DirectorySeparatorChar}…{tail}" : "…" + folder[^(max - 1)..];
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

    /// <summary>The tooltip of a ribbon button as text: the name and key, then what it does and what it works on.</summary>
    public static string Tip(RibbonItem it)
    {
        var hint = RibbonHints.For(it.Label);
        return Loc.Ui(it.Label) + (it.Key != null ? $" ({it.Key})" : "") + (it.Lock != null ? " — " + Loc.T(it.Lock) : "")
            + (hint is { } h ? "\n" + Loc.T(h.What) + (h.Scope != null ? "\n" + Loc.T(h.Scope) : "") : "");
    }

    /// <summary>
    /// The tooltip of a ribbon button as shown (doc/ribbon-tooltips.md): the name in bold with the key at the right, what it
    /// does below, then muted what it works on and why it is not available. The size and colours are those of the settings
    /// (the tooltip passes them down).
    /// </summary>
    public static Control TipView(RibbonItem it)
    {
        var hint = RibbonHints.For(it.Label);
        var head = new DockPanel { LastChildFill = true };
        if (it.Key != null)
        {
            var key = new TextBlock { Text = it.Key, Opacity = 0.65, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(key, Dock.Right);
            head.Children.Add(key);
        }
        head.Children.Add(new TextBlock { Text = Loc.Ui(it.Label), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var view = new StackPanel { Spacing = 4, MaxWidth = 360, Name = "ribbonTip", Children = { head } };
        if (hint is { } h)
        {
            view.Children.Add(new TextBlock { Text = Loc.T(h.What), TextWrapping = TextWrapping.Wrap });
            if (h.Scope != null) view.Children.Add(new TextBlock { Text = Loc.T(h.Scope), TextWrapping = TextWrapping.Wrap, Opacity = 0.65 });
        }
        if (it.Lock != null) view.Children.Add(new TextBlock { Text = Loc.T(it.Lock), TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontStyle = FontStyle.Italic });
        return view;
    }
}

/// <summary>The ribbon of the selected stage; its stage tabs (<see cref="TabStrip"/>) are shown in the header of the window.</summary>
public sealed class RibbonBar : DockPanel
{
    private readonly MainViewModel _vm;
    private readonly List<Stage> _stages;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    // at the top of the ribbon: the group names stand under the tallest group of the stage, the room a taller stage
    // keeps (the ribbon as high on every stage) is left under them
    private readonly StackPanel _groups = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Name = "ribbonGroups" };
    private readonly List<(RibbonItem Item, Button Button)> _buttons = new();
    private readonly Border _band;
    /// <summary>The height of the groups of the tallest stage, once measured in the window.</summary>
    private double _tallest;

    public RibbonBar(MainViewModel vm)
    {
        _vm = vm;
        _stages = vm.BuildStages();
        // the tabs go in the header of the window, on the line of the menu; a narrow window scrolls them sideways
        _tabs.VerticalAlignment = VerticalAlignment.Bottom;
        _tabs.Name = "stageTabs";
        TabStrip = Ui.HScroll(_tabs);
        _band = new Border
        {
            Background = Look.Ribbon,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(0, 1),
            MinHeight = 100,
            Child = Ui.HScroll(_groups)
        };
        Children.Add(_band);
        Select(2);
        AttachedToVisualTree += (_, _) => FitTallest();
        // Program settings ▸ Appearance: the buttons made again in the new sizes, the ribbon measured again
        void Refit()
        {
            _tallest = 0;
            _band.MinHeight = 0;
            if (Current >= 0) Select(Current);
            FitTallest();
        }
        Look.FontsChanged += Refit;
        DetachedFromVisualTree += (_, _) => Look.FontsChanged -= Refit;
        AttachedToVisualTree += (_, _) => { Look.FontsChanged -= Refit; Look.FontsChanged += Refit; };
    }

    /// <summary>
    /// The ribbon as high as its tallest stage on every stage, so that the plan under it does not move from stage to
    /// stage, and no higher, so that the group names stand close under the buttons. The stages are measured in the
    /// window, where the buttons have their look.
    /// </summary>
    private void FitTallest()
    {
        if (_tallest > 0 || VisualRoot == null || Current < 0) return;
        foreach (var stage in _stages)
        {
            BuildGroups(stage);
            _groups.Measure(Size.Infinity);
            _tallest = Math.Max(_tallest, _groups.DesiredSize.Height);
        }
        BuildGroups(_stages[Current]);
        // as high as the buttons and the names under them, and no higher: no gap between them
        _band.MinHeight = _tallest + _band.BorderThickness.Top + _band.BorderThickness.Bottom;
    }

    /// <summary>The stage tabs, for the header of the window.</summary>
    public Control TabStrip { get; }

    /// <summary>The width the tabs take, all of them shown.</summary>
    public double TabsWidth => _tabs.DesiredSize.Width;

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
                Content = Ui.Row(6, new TextBlock { Text = (i + 1).ToString(), FontFamily = Look.Mono, FontSize = Math.Max(8, Look.TabFont - 3), Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = Loc.Ui(_stages[i].Name), FontSize = Look.TabFont, FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal, Foreground = active ? Look.Accent : Look.Muted, VerticalAlignment = VerticalAlignment.Center }),
                // from the bottom of the header: the active one runs on into its ribbon below; higher for a larger font
                Height = StageTabHeight(Look.TabFont),
                VerticalAlignment = VerticalAlignment.Bottom,
                Padding = new Thickness(12, 0),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Background = active ? Look.Ribbon : Brushes.Transparent,
                BorderBrush = Look.Accent,
                BorderThickness = new Thickness(0, active ? 2 : 0, 0, 0)
            };
            ToolTip.SetTip(b, Loc.Ui("Alt+1…7 — switch stage"));
            b.Click += (_, _) => { if (idx != Current) IO.UsageLog.Track(IO.UsageLog.Tab, _stages[idx].Name, "ribbon"); Select(idx); };
            _tabs.Children.Add(b);
        }
        BuildGroups(_stages[index]);
        _vm.ActiveStage = _stages[index].Name;
        _vm.View.Redraw();
    }

    /// <summary>A stage tab: 30 high for the default font of 13, higher for a larger one.</summary>
    public static double StageTabHeight(double font) => Math.Max(30, Math.Ceiling(font * 4 / 3 + 12));

    /// <summary>A small ribbon button: 24 high for the default font of 12 and room of 4, the line of its text and the room above and below.</summary>
    public static double SmallButtonHeight(double font, double padding) => Math.Ceiling(font * 4 / 3) + 2 * padding;

    /// <summary>
    /// The room above and below the name of a ribbon group follows the room of the buttons: none of its own for the
    /// default of 4 (the group keeps 3 under it), more for more; less takes from below only, not into the buttons.
    /// </summary>
    public static Thickness CaptionMargin(double padding)
    {
        var d = padding - IO.ProgramSettings.DefaultRibbonPadding;
        return new Thickness(0, Math.Max(0, d), 0, Math.Max(-3, d));
    }

    private void BuildGroups(Stage stage)
    {
        _groups.Children.Clear();
        _buttons.Clear();
        foreach (var g in stage.Groups)
        {
            // as high as the group: the large buttons reach down to the group name
            var bigs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.RibbonGap + 1 };
            // the small buttons in columns of three, made here and not by a wrap panel: a wrap panel exactly three buttons
            // high put two in a column when it measured them on a scaled screen (150 %, 175 %: a button rounds to a little
            // more than a third) and three when it arranged them, so the group kept room for columns it never filled
            var smalls = new StackPanel { Orientation = Orientation.Horizontal, Name = "ribbonSmalls" };
            StackPanel? column = null;
            foreach (var it in g.Items)
            {
                var b = it.Big ? BigButton(it) : SmallButton(it);
                if (it.Big) bigs.Children.Add(b);
                else
                {
                    if (column == null || column.Children.Count == SmallsPerColumn)
                    {
                        column = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Top };
                        smalls.Children.Add(column);
                    }
                    column.Children.Add(b);
                }
                _buttons.Add((it, b));
            }
            var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.RibbonGap + 1, VerticalAlignment = VerticalAlignment.Top };
            if (bigs.Children.Count > 0) body.Children.Add(bigs);
            if (smalls.Children.Count > 0) body.Children.Add(smalls);
            var group = new DockPanel { Margin = new Thickness(8, 6, 8, 3) };
            var caption = new TextBlock { Text = Loc.Ui(g.Name), Foreground = Look.Accent, FontSize = Look.RibbonSize(11), HorizontalAlignment = HorizontalAlignment.Center, Name = "ribbonCaption", Margin = CaptionMargin(Look.RibbonPadding) };
            DockPanel.SetDock(caption, Dock.Bottom);
            group.Children.Add(caption);
            group.Children.Add(body);
            _groups.Children.Add(new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = group });
        }
        Refresh();
    }

    /// <summary>Small buttons in a column of a ribbon group.</summary>
    public const int SmallsPerColumn = 3;

    /// <summary>A name broken into two lines at the space nearest its middle; one word stays as it is.</summary>
    public static string TwoLines(string text)
    {
        var best = -1;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == ' ' && (best < 0 || Math.Abs(i - text.Length / 2.0) < Math.Abs(best - text.Length / 2.0))) best = i;
        return best < 0 ? text : text[..best] + "\n" + text[(best + 1)..];
    }

    private Button BigButton(RibbonItem it)
    {
        var stack = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
        var icon = new Panel { Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Center };
        icon.Children.Add(Look.Icon(it.Icon, 26, it.Lock != null ? Look.Faint : Look.Text, 1.5));
        if (it.Lock != null || it.Drop != null)
            icon.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -10, 0), Child = Look.Icon(it.Lock != null ? "lock" : "down", 11, Look.Faint, 2) });
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock { Text = it.TwoLines ? TwoLines(Loc.Ui(it.Label)) : Loc.Ui(it.Label), FontSize = Look.RibbonSize(11.5), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.NoWrap, Foreground = Look.Text, Name = "bigLabel" });
        // the icon and the name at the top, by the first row of small buttons; the key mark at the bottom, right over the
        // name of the group: the room the column of small buttons is higher by stays inside the button, not under it
        var content = new DockPanel();
        if (it.Key != null)
        {
            var key = new Border { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0), Child = Look.Kbd(it.Key, 9), Name = "bigKey" };
            DockPanel.SetDock(key, Dock.Bottom);
            content.Children.Add(key);
        }
        stack.VerticalAlignment = VerticalAlignment.Top;
        content.Children.Add(stack);
        var b = new Button
        {
            Content = content,
            MinWidth = 72,
            // as high as the column of small buttons beside it; alone, 76 for the room of 4
            MinHeight = 76 + 2 * (Look.RibbonPadding - IO.ProgramSettings.DefaultRibbonPadding),
            // the room of the settings above, 4 less below (2 by default): the key mark close over the group name
            Padding = new Thickness(2, Look.RibbonPadding + 2, 2, Math.Max(0, Look.RibbonPadding - 2)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            VerticalContentAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        b.Classes.Add("flat");
        Wire(b, it);
        return b;
    }

    private Button SmallButton(RibbonItem it)
    {
        var row = Ui.Row(6, Look.Icon(it.Icon, 15, it.Lock != null ? Look.Faint : Look.Muted),
            new TextBlock { Text = Loc.Ui(it.Label), FontSize = Look.RibbonSize(12), Foreground = it.Lock != null ? Look.Faint : Look.TextSoft, VerticalAlignment = VerticalAlignment.Center });
        if (it.Drop != null) row.Children.Add(Look.Icon("down", 11, Look.Faint, 2));
        if (it.Lock != null) row.Children.Add(Look.Icon("lock", 11, Look.Faint, 2));
        if (it.Key != null) row.Children.Add(Look.Kbd(it.Key, 9));
        var b = new Button
        {
            Content = row,
            Height = SmallButtonHeight(Look.RibbonSize(12), Look.RibbonPadding),
            Padding = new Thickness(8, 0),
            // the gap of the settings goes between the columns (2 by default), not between the rows
            Margin = new Thickness(0, 0, Look.RibbonGap + 1, 1),
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
        // the text form for screen readers; the tooltip itself is the laid out view
        AutomationProperties.SetHelpText(b, Ui.Tip(it));
        ToolTip.SetTip(b, Ui.TipView(it));
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
            b.Background = it.ToolName != null && it.ToolName == tool || it.Checked?.Invoke() == true ? Look.AccentSoft : Brushes.Transparent;
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
        // ↑/↓ go through the list, also when a hole number is typed
        _box.Classes.Add(NumberStep.TextClass);
        _box.KeyDown += OnKey;
        _list.DoubleTapped += (_, _) => Run();
        // the font of the tab names (Program settings ▸ Appearance), as the stage tabs beside it; as high as they are
        _box.Bind(TextBox.FontSizeProperty, _box.GetResourceObservable(Look.TabFontKey));
        _list.Bind(ListBox.FontSizeProperty, _list.GetResourceObservable(Look.TabFontKey));
        Height = RibbonBar.StageTabHeight(Look.TabFont);
        Look.FontsChanged += () => Height = RibbonBar.StageTabHeight(Look.TabFont);
    }

    public void Focus2() { _box.Focus(); _box.SelectAll(); }

    /// <summary>A command was run or the search was left with Esc: the search window over the plan closes.</summary>
    public event Action? Done;

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
            case Key.Escape: _box.Text = ""; _popup.IsOpen = false; _vm.View.Focus(); e.Handled = true; Done?.Invoke(); break;
        }
    }

    private void Run()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _shown.Count) return;
        var action = _shown[_list.SelectedIndex].Action;
        _box.Text = "";
        _popup.IsOpen = false;
        _vm.View.Focus();
        Done?.Invoke();
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

    /// <summary>How opaque the ground of the panel is: the plan shows through it, the text and buttons stay solid.</summary>
    public const double GroundOpacity = 0.72;

    /// <summary>The ground of the panel, in the ribbon's colour at <see cref="GroundOpacity"/> (it follows the theme).</summary>
    public Border Ground { get; } = new() { Background = Look.Ribbon, Opacity = GroundOpacity, CornerRadius = new CornerRadius(9) };

    public ToolPanel(MainViewModel vm)
    {
        _vm = vm;
        // see-through: the ground is a layer of its own under the content (the border stays solid)
        BorderBrush = Look.LineStrong;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        // as wide as its fields need, up to the width of the viewport; beyond that the fields scroll; low over the plan
        Margin = new Thickness(16, 0, 16, 6);
        BoxShadow = BoxShadows.Parse("0 6 18 0 #52000000");
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
        body.Margin = new Thickness(14, 8, 10, 8);
        Child = new Panel { Children = { Ground, body } };
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
        IsVisible = tool != null && tool is not (SelectTool or PanTool) && _vm.Project != null;
        if (!IsVisible) { _fields.Children.Clear(); return; }
        _title.Text = Loc.T(tool!.Name);
        _hint.Text = Loc.T(tool.Hint);
        ToolTip.SetTip(_hint, _hint.Text);
        _fields.Children.Clear();
        var d = _vm.Project!.Design;
        var polygonPattern = tool is PolygonPatternTool;
        if (tool.Name is "Pattern" or "Row" or "Hole" or "Dummy hole" || polygonPattern)
        {
            if (tool.Name is "Pattern" or "Row" || polygonPattern) _fields.Children.Add(Field("Burden, m", BlastDesigner.Domain.Quantity.Length, d.Burden, v => _vm.Change("Design data", p => p.Design.Burden = v), "patternBurdenField"));
            if (tool.Name is "Pattern" or "Row" || polygonPattern) _fields.Children.Add(Field("Spacing, m", BlastDesigner.Domain.Quantity.Length, d.Spacing, v => _vm.Change("Design data", p => p.Design.Spacing = v), "patternSpacingField"));
            _fields.Children.Add(Field("Diameter, mm", BlastDesigner.Domain.Quantity.Diameter, d.Diameter, v => _vm.Change("Design data", p => p.Design.Diameter = v)));
            _fields.Children.Add(Field("Angle, °", BlastDesigner.Domain.Quantity.HoleAngle, d.Angle, v => _vm.Change("Design data", p => p.Design.Angle = v), "designAngleField"));
            if (tool.Name == "Pattern" || polygonPattern)
            {
                var combo = new ComboBox { ItemsSource = Enum.GetValues<PatternType>().Select(x => Loc.Enum(x)).ToList(), SelectedIndex = (int)d.Pattern, Width = 120, Height = 26 };
                combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) _vm.Change("Design data", p => p.Design.Pattern = (PatternType)combo.SelectedIndex); };
                _fields.Children.Add(Labelled("Pattern", combo));
                var align = new ComboBox { ItemsSource = new[] { "Left", "Centre", "Right" }.Select(Loc.Ui).ToList(), SelectedIndex = (int)_vm.PatternAlignment, Width = 110, Height = 26, Name = "patternAlignment" };
                ToolTip.SetTip(align, Loc.Ui("Where the holes of a row start, looking along the blast direction: half a spacing from the left end of the outline, from its right end, or with the same room at both ends"));
                align.SelectionChanged += (_, _) => { if (align.SelectedIndex >= 0) _vm.PatternAlignment = (Domain.Model.RowAlignment)align.SelectedIndex; };
                _fields.Children.Add(Labelled("Along the row", align));
            }
            if (tool.Name == "Pattern")
            {
                // what the pattern dialog used to ask after the outline was drawn
                var direction = Field("Direction, °", _vm.Project.Header.BlastOrientation, v => _vm.Change("Blast orientation", p => p.Header.BlastOrientation = v), "patternDirection",
                    string.Join("\n\n", DirectionTipParagraphs.Select(Loc.Ui)));
                var withGrid = new CheckBox { Content = Loc.Ui("Orientation turns with the pattern"), IsChecked = _vm.PatternRotateOrientation, Name = "patternRotateOrientation", FontSize = 11, MinHeight = 0, Padding = new Thickness(4, 0, 0, 0) };
                ToolTip.SetTip(withGrid, Loc.Ui("On: when the pattern is turned later, the blast orientation turns with it. Off: the orientation stays, and the face row is then the row furthest along it"));
                withGrid.IsCheckedChanged += (_, _) => _vm.PatternRotateOrientationChoice = withGrid.IsChecked == true;
                _fields.Children.Add(new StackPanel { Spacing = 2, Children = { direction, withGrid } });
            }
            if (tool.Name == "Pattern" || polygonPattern)
            {
                _fields.Children.Add(Field("Edge offset, m", BlastDesigner.Domain.Quantity.Length, _vm.PatternEdgeOffset, v => _vm.PatternEdgeOffset = v, "patternOffset"));
                _fields.Children.Add(TextField("Number prefix", _vm.PatternLabelPrefix, v => _vm.PatternLabelPrefix = v, "patternPrefix"));
                _fields.Children.Add(Field("Start number", _vm.NextPatternNumber, v => _vm.PatternStartNumber = Math.Max(0, (int)Math.Round(v)), "patternStart"));
                _fields.Children.Add(PatternLevel());
            }
        }
        if (tool is RenumberTool)
        {
            // the prefix (empty: each hole keeps its own) and the counter with its steps
            var prefix = TextField("Number prefix", _vm.RenumberPrefix, v => _vm.RenumberPrefix = v, "renumberPrefix");
            ToolTip.SetTip(prefix, Loc.Ui("Put before the number of each hole numbered. Empty: each hole keeps its own prefix (the name of its row), even none"));
            // Overwrite: only with the prefix empty, the holes then lose their own prefix; it follows the field as it is typed
            var overwrite = new CheckBox { Content = Loc.Ui("Overwrite"), IsChecked = _vm.RenumberOverwrite, Name = "renumberOverwrite", VerticalAlignment = VerticalAlignment.Bottom, IsEnabled = string.IsNullOrWhiteSpace(_vm.RenumberPrefix) };
            ToolTip.SetTip(overwrite, Loc.Ui("With the prefix empty: on, the holes numbered lose their own prefix and keep the number alone; off, each keeps its own"));
            overwrite.IsCheckedChanged += (_, _) => _vm.RenumberOverwrite = overwrite.IsChecked == true;
            var prefixBox = ((StackPanel)prefix).Children.OfType<TextBox>().First();
            prefixBox.TextChanged += (_, _) => overwrite.IsEnabled = string.IsNullOrWhiteSpace(prefixBox.Text);
            _fields.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { prefix, overwrite } });
            var counter = new TextBox
            {
                Name = "renumberCounter", Text = _vm.RenumberCounter.ToString(System.Globalization.CultureInfo.CurrentCulture), Width = 56, Height = 26, MinHeight = 26,
                Padding = new Thickness(8, 2), FontFamily = Look.Mono, FontSize = 12, Background = Look.Field, HorizontalContentAlignment = HorizontalAlignment.Center
            };
            ToolTip.SetTip(counter, Loc.Ui("The number the next hole clicked takes; a drag goes on from it, one a hole"));
            void Commit()
            {
                if (int.TryParse(counter.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out var v) && v != _vm.RenumberCounter) Ui.Safe(() => _vm.SetRenumberCounter(v));
                else counter.Text = _vm.RenumberCounter.ToString(System.Globalization.CultureInfo.CurrentCulture);
            }
            counter.LostFocus += (_, _) => Commit();
            counter.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            Button Step(string text, int by, string name, string tip)
            {
                var b = new Button { Content = text, Name = name, Width = 26, Height = 26, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(b, Loc.Ui(tip));
                b.Click += (_, _) => Ui.Safe(() => _vm.StepRenumberCounter(by));
                return b;
            }
            _fields.Children.Add(Labelled("Counter", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { Step("−", -1, "renumberLess", "One less (−)"), counter, Step("+", 1, "renumberMore", "One more (+)") } }));
        }
        if (tool.Name == "Tie")
        {
            var chain = new CheckBox { Content = Loc.Ui("Tie chain"), IsChecked = _vm.TieChain, Name = "tieChain", VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(chain, Loc.Ui("On: ties are built as a chain through all the holes captured by the drawn line. Off: one tie from the start hole to the end hole, whether or not other holes lie on the way"));
            chain.IsCheckedChanged += (_, _) => _vm.TieChain = chain.IsChecked == true;
            _fields.Children.Add(chain);
            var goOn = new CheckBox { Content = Loc.Ui("Continue after creating"), IsChecked = _vm.TieContinue, Name = "tieContinue", VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(goOn, Loc.Ui("On: the entry goes on from the hole just tied, as with Shift held. A click on that hole again or Esc finishes it"));
            goOn.IsCheckedChanged += (_, _) => _vm.TieContinue = goOn.IsChecked == true;
            _fields.Children.Add(goOn);
            var capture = new CheckBox
            {
                Content = Ui.Row(6, new TextBlock { Text = Loc.Ui("Capture width"), VerticalAlignment = VerticalAlignment.Center }, Look.Kbd("F")),
                IsChecked = _vm.TieCaptureShown,
                Name = "tieCapture",
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(capture, Loc.Ui("Grey lines beside the tie show what it captures. The + and − keys change the pick radius"));
            capture.IsCheckedChanged += (_, _) => { _vm.TieCaptureShown = capture.IsChecked == true; _vm.View.Redraw(); };
            _fields.Children.Add(capture);
        }
        if (tool is PickPointsTool { ClosedName: not null }) PolylineFields();
        if (tool is PatternRotateTool rotate) RotateFields(rotate);
        if (tool is TransformTool transform) TransformFields(transform);
        if (tool.Name is "Set" or "Row set")
            _fields.Children.Add(Field("Step, ms", _vm.Project.Electronic.HoleIntervalMs, v => _vm.Change("Electronic settings", p => p.Electronic.HoleIntervalMs = v)));
        _fieldsScroll.IsVisible = _fields.Children.Count > 0;
    }

    /// <summary>
    /// Panel of the Polyline tool: Build only on surface, on points, on lines (any of them; none: anywhere) and Close, which
    /// makes it the Polygon tool. The choice stays for the next time.
    /// </summary>
    private void PolylineFields()
    {
        // a chip each, on or off by a click (any of them together)
        Button Box(string text, string name, bool on, Action<bool> set, string tip)
        {
            Button? b = null;
            b = Ui.Chip(Loc.Ui(text), () =>
            {
                var now = !b!.Classes.Contains("on");
                set(now);
                Ui.SetChip(b, now);
                _vm.View.Redraw();
            }, on, Loc.Ui(tip));
            b.Name = name;
            b.VerticalAlignment = VerticalAlignment.Center;
            return b;
        }
        var only = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        only.Children.Add(new TextBlock { Text = Loc.Ui("Build only"), Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center });
        // any of them together: a survey point or a line vertex under the cursor first, else the surface under it
        var surface = Box("on surface", "polylineOnSurface", _vm.PolylineOnSurface, v => _vm.PolylineOnSurface = v,
            "The points lie on the surface under the cursor, no snapping to other objects (but the survey points and line vertices ticked here); outside the surfaces no point is put");
        var points = Box("on points", "polylineOnPoints", _vm.PolylineOnPoints, v => _vm.PolylineOnPoints = v,
            "A point goes onto a survey point under the cursor");
        var lines = Box("on lines", "polylineOnLines", _vm.PolylineOnLines, v => _vm.PolylineOnLines = v,
            "A point goes onto a vertex of a line under the cursor");
        only.Children.Add(surface);
        only.Children.Add(points);
        only.Children.Add(lines);
        _fields.Children.Add(only);
        var close = new ToggleSwitch { IsChecked = _vm.PolylineClosed, Name = "polylineClosed", OnContent = Loc.Ui("Close the line"), OffContent = Loc.Ui("Close the line"), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(close, Loc.Ui("The last point joins the first: a closed outline (a polygon)"));
        close.IsCheckedChanged += (_, _) =>
        {
            _vm.PolylineClosed = close.IsChecked == true;
            // the title says Polygon or Polyline
            _title.Text = Loc.T(_vm.ActiveTool?.Name ?? "");
            _vm.View.Redraw();
        };
        _fields.Children.Add(close);
    }

    /// <summary>
    /// Panel of the Transform tool: the scale (one field in proportion, else one an axis), the tilts, the mirrors, the move,
    /// the axes, the base point (with its coordinates when it is one of the user's) and Reset.
    /// </summary>
    private void TransformFields(TransformTool tool)
    {
        var t = tool.Current;
        // the outline of a pattern alone is only tilted: the scale and the mirrors are there but off
        var first = _fields.Children.Count;
        if (tool.Proportional) _fields.Children.Add(Field("Scale", t.ScaleX, v => { if (!tool.SetScale(0, v)) _vm.RefreshToolPanel(); }, "transformScale"));
        else
            foreach (var (axis, label) in new[] { (0, "Scale X"), (1, "Scale Y"), (2, "Scale Z") })
            {
                var a = axis;
                var value = axis switch { 0 => t.ScaleX, 1 => t.ScaleY, _ => t.ScaleZ };
                _fields.Children.Add(Field(label, value, v => { if (!tool.SetScale(a, v)) _vm.RefreshToolPanel(); }, "transformScale" + "XYZ"[axis]));
            }
        var lockBox = new CheckBox { Content = Loc.Ui("In proportion"), IsChecked = tool.Proportional, IsEnabled = !tool.HasCircle && !tool.OnlyTilts, Name = "transformProportional", VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(lockBox, Loc.Ui(tool.HasCircle ? "A circle is selected: the scale is only in proportion" : "On: one scale along every axis. Off: a scale along each axis"));
        lockBox.IsCheckedChanged += (_, _) => { if ((lockBox.IsChecked == true) != tool.Proportional) tool.SetProportional(lockBox.IsChecked == true); };
        _fields.Children.Add(lockBox);
        foreach (var (axis, label, tip) in new[]
        {
            (0, "Tilt X, °", "About the X axis of the frame, by the right-hand rule: north goes up"),
            (1, "Tilt Y, °", "About the Y axis of the frame, by the right-hand rule: up goes east"),
            (2, "Tilt Z, °", "About the vertical, clockwise seen from above, as bearings go")
        })
        {
            var a = axis;
            var value = axis switch { 0 => t.TiltX, 1 => t.TiltY, _ => t.TiltZ };
            _fields.Children.Add(Field(label, value, v => { if (!tool.SetTilt(a, v)) _vm.RefreshToolPanel(); }, "transformTilt" + "XYZ"[axis], Loc.Ui(tip)));
        }
        foreach (var (axis, label, tip, on) in new[]
        {
            (0, "Mirror X", "Mirror across the X axis of the frame, through the base point: X changes its sign. Again takes it off", t.MirrorX),
            (1, "Mirror Y", "Mirror across the Y axis of the frame, through the base point: Y changes its sign. Again takes it off", t.MirrorY)
        })
        {
            var a = axis;
            var chip = Ui.Chip(Loc.Ui(label), () => tool.ToggleMirror(a), on, Loc.Ui(tip));
            chip.Name = "transformMirror" + "XY"[axis];
            if (tool.OnlyTilts)
            {
                chip.IsEnabled = false;
                ToolTip.SetTip(chip, Loc.Ui("The outline of a pattern is only tilted: it is not scaled or mirrored"));
            }
            _fields.Children.Add(Labelled(" ", chip));
        }
        if (tool.OnlyTilts)
            foreach (var c in _fields.Children.Skip(first).Take(tool.Proportional ? 1 : 3))
            {
                c.IsEnabled = false;
                ToolTip.SetTip(c, Loc.Ui("The outline of a pattern is only tilted: it is not scaled or mirrored"));
            }
        foreach (var (axis, label) in new[] { (0, "Move X, m"), (1, "Move Y, m"), (2, "Move Z, m") })
        {
            var a = axis;
            var value = axis switch { 0 => t.Move.X, 1 => t.Move.Y, _ => t.Move.Z };
            _fields.Children.Add(Field(label, BlastDesigner.Domain.Quantity.Length, value, v => tool.SetMove(a, v), "transformMove" + "XYZ"[axis]));
        }
        var axes = new ComboBox { ItemsSource = new[] { "Of the world", "Of the object" }.Select(Loc.Ui).ToList(), SelectedIndex = tool.ObjectAxes ? 1 : 0, Width = 120, Height = 26, Name = "transformAxes", IsEnabled = tool.ObjectAxesAvailable };
        ToolTip.SetTip(axes, Loc.Ui("Of the world: X east, Y north. Of the object: along a block, a line, a text or a dimension (the first selected)"));
        axes.SelectionChanged += (_, _) => { if (axes.SelectedIndex >= 0 && (axes.SelectedIndex == 1) != tool.ObjectAxes) tool.SetObjectAxes(axes.SelectedIndex == 1); };
        _fields.Children.Add(Labelled("Axes", axes));
        var bases = new[] { "Centre of the frame", "Centre of the bottom", "South-west corner", "South-east corner", "North-east corner", "North-west corner", "A point of its own" };
        var basePoint = new ComboBox { ItemsSource = bases.Select(Loc.Ui).ToList(), SelectedIndex = (int)tool.Base, Width = 170, Height = 26, Name = "transformBase" };
        ToolTip.SetTip(basePoint, Loc.Ui("What the scale, the tilts and the mirrors go about. Drag the cross on the plan to put it anywhere (it snaps to objects)"));
        basePoint.SelectionChanged += (_, _) => { if (basePoint.SelectedIndex >= 0 && basePoint.SelectedIndex != (int)tool.Base) tool.SetBase((TransformTool.BasePoint)basePoint.SelectedIndex); };
        _fields.Children.Add(Labelled("Base point", basePoint));
        if (tool.Base == TransformTool.BasePoint.Custom)
            foreach (var (axis, label) in new[] { (0, "Base X"), (1, "Base Y"), (2, "Base Z") })
            {
                var a = axis;
                var value = axis switch { 0 => t.Pivot.X, 1 => t.Pivot.Y, _ => t.Pivot.Z };
                _fields.Children.Add(Field(label, BlastDesigner.Domain.Quantity.Length, value, v => tool.SetPivotCoordinate(a, v), "transformPivot" + "XYZ"[axis]));
            }
        var reset = Ui.Chip(Loc.Ui("Reset"), tool.Reset, false, Loc.Ui("Back to no change: scale 1, no tilt, no mirror, no move"));
        reset.Name = "transformReset";
        _fields.Children.Add(Labelled(" ", reset));
    }

    /// <summary>Panel of the rotate tool: a turn typed in or by a quarter, along a line, and how the pattern follows.</summary>
    private void RotateFields(PatternRotateTool tool)
    {
        _fields.Children.Add(Field("Turn, °", 0, v => tool.Apply(v), "rotateAngle", Loc.Ui("Clockwise; Enter turns by it")));
        foreach (var (text, turn) in new[] { ("−90°", -90.0), ("+90°", 90.0) })
        {
            var t = turn;
            _fields.Children.Add(Labelled(" ", Ui.Chip(text, () => tool.Apply(t), false, Loc.Ui("Turn by a quarter"))));
        }
        var pat = tool.Pattern;
        if (pat == null) return;
        _fields.Children.Add(Labelled(" ", Ui.Chip(Loc.Ui("Along a line"), () => tool.ByLine = !tool.ByLine, tool.ByLine, Loc.Ui("Click a segment of a line: the rows of the pattern run along it"))));
        var id = pat.Id;
        var orientation = new CheckBox { Content = Loc.Ui("Orientation turns with the pattern"), IsChecked = pat.RotateOrientation, Name = "rotateOrientation", VerticalAlignment = VerticalAlignment.Center };
        orientation.IsCheckedChanged += (_, _) => { var on = orientation.IsChecked == true; if (on != pat.RotateOrientation) _vm.SetPatternFlag(id, "Pattern settings", x => x.RotateOrientation = on); };
        _fields.Children.Add(orientation);
        var labels = new CheckBox { Content = Loc.Ui("Keep the numbers"), IsChecked = pat.KeepLabels, Name = "rotateKeepLabels", VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(labels, Loc.Ui("On: the holes keep their numbers and new holes take the next free ones. Off: the holes are numbered again by the rows"));
        labels.IsCheckedChanged += (_, _) => { var on = labels.IsChecked == true; if (on != pat.KeepLabels) _vm.SetPatternFlag(id, "Pattern settings", x => x.KeepLabels = on); };
        _fields.Children.Add(labels);
        var modes = new[] { "Turn with the pattern", "Keep", "Along the blast orientation" };
        var bearing = new ComboBox { ItemsSource = modes.Select(Loc.Ui).ToList(), SelectedIndex = (int)_vm.PatternBearing, Width = 170, Height = 26, Name = "rotateBearing" };
        bearing.SelectionChanged += (_, _) => { if (bearing.SelectedIndex >= 0) _vm.PatternBearing = (Engine.PatternBearingMode)bearing.SelectedIndex; };
        _fields.Children.Add(Labelled("Hole bearing", bearing));
    }

    private static Control Labelled(string label, Control c) =>
        new StackPanel { Spacing = 2, Children = { new TextBlock { Text = Loc.Ui(label), FontSize = 10.5, Foreground = Look.Faint }, c } };

    private static Control TextField(string label, string value, Action<string> set, string name)
    {
        var box = new TextBox { Text = value, Width = 76, Height = 26, MinHeight = 26, Padding = new Thickness(8, 2), FontFamily = Look.Mono, FontSize = 12, Background = Look.Field, Name = name };
        void Commit() { if ((box.Text ?? "") != value) { value = box.Text ?? ""; Ui.Safe(() => set(value)); } }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        return Labelled(label, box);
    }

    /// <summary>
    /// To level of the holes of the pattern, as the field of a hole: a level for all of them, or with To the surface on, each
    /// its own where its line meets the grade surface. Empty: none, the holes keep the design length; 0 is a level.
    /// </summary>
    private Control PatternLevel()
    {
        var box = new TextBox
        {
            Name = "patternToLevel", Width = 76, Height = 26, MinHeight = 26, Padding = new Thickness(8, 2), FontFamily = Look.Mono, FontSize = 12, Background = Look.Field
        };
        ToolTip.SetTip(box, PropertiesPanel.LengthTip());
        var surface = new ToggleButton
        {
            Name = "patternToSurface", Content = Look.Icon("surface", 14, Look.TextSoft), Width = 26, Height = 26, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), IsChecked = _vm.PatternToSurface
        };
        ToolTip.SetTip(surface, Loc.Ui("To the surface: each hole of the pattern takes as its level the elevation where its line from the collar meets the grade surface, or the first surface when it meets no grade"));
        AutomationProperties.SetName(surface, Loc.Ui("To the surface"));
        void Show()
        {
            box.Text = _vm.PatternToSurface || _vm.PatternToLevel is not double z ? "" : Qty.Text(BlastDesigner.Domain.Quantity.Length, z, "0.###");
            box.Watermark = Loc.Ui(_vm.PatternToSurface ? "by the surface" : "not set");
        }
        Show();
        void Commit()
        {
            var text = (box.Text ?? "").Trim();
            double? level = null;
            if (text.Length > 0)
            {
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var v)) { Show(); return; }
                level = Qty.Store(BlastDesigner.Domain.Quantity.Length, v);
            }
            // a level typed in takes over from the surface; an empty field leaves the surface on
            if (level != null) { _vm.PatternToSurface = false; surface.IsChecked = false; }
            else if (_vm.PatternToSurface) { Show(); return; }
            _vm.PatternToLevel = level;
            Show();
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        surface.IsCheckedChanged += (_, _) =>
        {
            if (_vm.PatternToSurface == (surface.IsChecked == true)) return;
            _vm.PatternToSurface = surface.IsChecked == true;
            Show();
        };
        return Labelled("To level", new StackPanel { Orientation = Orientation.Horizontal, Children = { box, surface } });
    }

    /// <summary>Tip of the pattern direction: how the rows and holes are laid out and numbered by it, paragraph by paragraph.</summary>
    private static readonly string[] DirectionTipParagraphs =
    {
        "The rows lie across this direction. At 0° the rows run west to east, at 90° north to south.",
        "The first row goes on the face side, half a burden from the edge of the outline. The next rows step back, against the direction, a burden apart. The spacing is laid along the row; in a staggered pattern every second row is shifted by half a spacing.",
        "The program numbers the rows by this direction (Row 1 is at the face), and the holes by it too."
    };

    /// <summary>A field of a quantity: shown and typed in the units of the program, <paramref name="set"/> given the kept value.</summary>
    private static Control Field(string label, BlastDesigner.Domain.Quantity q, double kept, Action<double> set, string? name = null, string? tip = null) =>
        Field(label, Qty.Show(q, kept), v => set(Qty.Store(q, v)), name, tip);

    private static Control Field(string label, double value, Action<double> set, string? name = null, string? tip = null)
    {
        var box = new TextBox
        {
            Name = name,
            Text = value.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture),
            Width = 76,
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
        if (tip != null) ToolTip.SetTip(box, tip);
        return Labelled(label, box);
    }
}

/// <summary>Right-hand inspector: properties of the selection, layers and visibility, as tabs.</summary>
public sealed class Inspector : DockPanel
{
    private readonly MainViewModel _vm;
    private readonly TabControl _tabs = new();
    private readonly StackPanel _selectionBar = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(12, 8, 12, 0) };
    // the right margin keeps the boxes, lists and buttons clear of the scroll bar, which runs over the content
    private readonly StackPanel _visibility = new() { Spacing = 2, Margin = new Thickness(12, 8, 26, 8) };

    public Inspector(MainViewModel vm, PropertiesPanel properties, LayersPanel layers)
    {
        _vm = vm;
        Background = Look.Panel;
        var wrap = new WrapPanel { Margin = new Thickness(12, 8, 12, 0) };
        foreach (var (label, action) in new (string, Action)[] { ("Like", vm.SelectLike), ("Exact", vm.SelectExact), ("Invert", vm.InvertSelection) })
        {
            var chip = Ui.Chip(Loc.Ui(label), action);
            chip.Margin = new Thickness(0, 0, 4, 4);
            wrap.Children.Add(chip);
        }
        // for holes only: shown while holes are selected
        var sameWarning = Ui.Chip(Loc.Ui("With the same warning"), vm.SelectSameWarning, false,
            Loc.Ui("No explosive, no primer, uncharged gap, short stemming, deck below the toe, overlapping decks, non-water-resistant explosive"));
        sameWarning.Name = "selectSameWarning";
        sameWarning.Margin = new Thickness(0, 0, 4, 4);
        void ShowSameWarning() => sameWarning.IsVisible = vm.Doc?.SelectedHoles.Any() == true;
        ShowSameWarning();
        vm.SelectionRefreshed += ShowSameWarning;
        wrap.Children.Insert(2, sameWarning);
        var props = new DockPanel();
        DockPanel.SetDock(wrap, Dock.Top);
        props.Children.Add(wrap);
        props.Children.Add(properties);
        _tabs.ItemsSource = new[]
        {
            new TabItem { Header = Loc.Ui("Properties"), Tag = "Properties", Content = Scaled(props) },
            new TabItem { Header = Loc.Ui("Layers"), Tag = "Layers", Content = Scaled(layers) },
            new TabItem { Header = Loc.Ui("Visibility"), Tag = "Visibility", Content = new ScrollViewer { Content = Scaled(_visibility) } }
        };
        // Program settings ▸ Appearance ▸ Right panel: the panel drawn larger or smaller, the fields with the text
        void Rescale() { foreach (var s in _scaled) s.LayoutTransform = PanelTransform(); }
        Look.FontsChanged += Rescale;
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

    /// <summary>The contents of the tabs, drawn at the scale of the font of the panel; the tab names keep the font of the tabs.</summary>
    private readonly List<LayoutTransformControl> _scaled = new();

    private Control Scaled(Control content)
    {
        var host = new LayoutTransformControl { Child = content, LayoutTransform = PanelTransform(), Name = "panelScale" };
        _scaled.Add(host);
        return host;
    }

    private static ScaleTransform? PanelTransform() => Math.Abs(Look.PanelScale - 1) < 1e-9 ? null : new ScaleTransform(Look.PanelScale, Look.PanelScale);

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
        // the quick actions work on every group
        var actions = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
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
        Action("Hide the selection", () => { foreach (var id in _vm.Doc!.Selection) _vm.HiddenEntities.Add(id); _vm.Doc.ClearSelection(); });
        Action("Hide all", () => { foreach (var e in p.Entities) _vm.HiddenEntities.Add(e.Id); _vm.Doc!.ClearSelection(); });
        Action("Show all", _vm.ShowEverything);
        _visibility.Children.Add(actions);

        var counts = p.Entities.GroupBy(ViewKinds.KindOf).Where(g => g.Key != null).ToDictionary(g => g.Key!, g => g.Count());
        int Count(string kind) => counts.TryGetValue(kind, out var n) ? n : 0;
        // two columns, each a stack of its own: a box wrapped on two lines in one column leaves no gap in the other
        Grid Columns()
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Name = "visColumns" };
            for (int i = 0; i < 2; i++)
            {
                var column = new StackPanel();
                Grid.SetColumn(column, i);
                g.Children.Add(column);
            }
            return g;
        }
        void Toggles(Grid into, IEnumerable<MainViewModel.VisibilityToggle> toggles, Func<MainViewModel.VisibilityToggle, int?>? count = null)
        {
            int i = 0;
            foreach (var t in toggles) ((StackPanel)into.Children[i++ % 2]).Children.Add(ToggleBox(p.View, t, count?.Invoke(t)));
        }
        IEnumerable<MainViewModel.VisibilityToggle> Of(string group, params string[] ids) =>
            ids.Select(id => MainViewModel.FindToggle(id)!).Where(t => t.Group == group);
        TextBlock Sub(string text) => new() { Text = Loc.Ui(text), Foreground = Look.Blue, FontSize = 11, Margin = new Thickness(0, 10, 0, 4) };

        // ---- topography: one box per kind, always the same eight, the count of each (0: greyed)
        var topo = GroupBody(ViewKinds.Topography, "Topography objects", ViewKinds.TopographyKinds.Sum(Count).ToString(), true);
        var topoGrid = Columns();
        Toggles(topoGrid, MainViewModel.VisibilityToggles.Where(t => t.Group == ViewKinds.Topography),
            t => Count(t.Id == "Surfaces" ? ViewKinds.SurfaceKind : t.Id));
        topo.Children.Add(topoGrid);
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
            topo.Children.Add(surfaces);
        }

        // ---- holes and ties: the objects, how a hole is drawn, its labels and the hole filters
        var holes = GroupBody(ViewKinds.Holes, "Holes and ties", $"{Count(ViewKinds.HoleKind)} · {Count(ViewKinds.TieKind)}", true);
        var objects = Columns();
        Toggles(objects, Of(ViewKinds.Holes, ViewKinds.HoleKind, "Ties", ViewKinds.InitiationPointKind, "Rows"),
            t => t.Id == "Rows" ? null : Count(t.Id == "Ties" ? ViewKinds.TieKind : t.Id));
        holes.Children.Add(objects);
        holes.Children.Add(Sub("Hole display"));
        var display = Columns();
        Toggles(display, Of(ViewKinds.Holes, "HoleTrack", "Collar", "DipTrack", "GradePoint", "Deviation", "BurdenMarker", "Profile"));
        holes.Children.Add(display);
        holes.Children.Add(HoleLabelsHeader());
        // the host is kept across rebuilds: off the panel of the last build first (it is not under _visibility directly any more)
        (_labelHost.Parent as Panel)?.Children.Remove(_labelHost);
        holes.Children.Add(_labelHost);
        BuildLabelColumns();
        void Filter(Panel into, string text, bool shown, Action toggle, string? tip = null)
        {
            var cb = new CheckBox { Content = text, IsChecked = shown, MinHeight = 0 };
            ToolTip.SetTip(cb, tip ?? Loc.Ui("Shows or hides the holes of this kind. Hidden holes cannot be selected but count in the calculations."));
            cb.IsCheckedChanged += (_, _) => toggle();
            into.Children.Add(cb);
        }
        // hole types: only with holes of two or more types (or a type hidden, to bring it back); dummy holes go with "Holes"
        var used = p.Holes.Where(h => !h.IsDummy).Select(h => h.HoleType).ToHashSet();
        if (used.Count > 1 || p.View.HiddenHoleTypes.Any(t => t != 0))
        {
            holes.Children.Add(Sub("Hole types"));
            foreach (var t in p.Design.HoleTypes.Where(t => used.Contains(t.Index) || p.View.HiddenHoleTypes.Contains(t.Index)))
            {
                var idx = t.Index;
                Filter(holes, $"{Loc.Name(t.Name)} ({p.Holes.Count(h => !h.IsDummy && h.HoleType == idx)})", !p.View.HiddenHoleTypes.Contains(idx), () => _vm.ToggleHoleType(idx));
            }
        }
        if (p.Design.MaterialTypes.Any())
        {
            holes.Children.Add(Sub("Material types"));
            foreach (var t in p.Design.MaterialTypes)
            {
                var idx = t.Index;
                Filter(holes, Loc.Name(t.Name), !p.View.HiddenMaterialTypes.Contains(idx), () => _vm.ToggleMaterialType(idx));
            }
        }
        holes.Children.Add(Sub("Hole state"));
        foreach (var st in Enum.GetValues<HoleState>())
        {
            var state = st;
            // the states the stage buttons on show say which button they come from
            var owners = _vm.StageOwnersOfState(state);
            var byStages = StageViews.Enabled(p).Any(v => v.States.Count > 0);
            var tip = owners.Count > 0 ? Loc.F(Loc.Ui("From {0}"), string.Join(", ", owners.Select(o => "«" + o + "»")))
                : byStages ? Loc.Ui("Hidden: no stage button on shows this state") : null;
            Filter(holes, $"{Loc.Enum(state)} ({p.Holes.Count(h => h.State == state)})", !p.View.HiddenStates.Contains(state), () => _vm.ToggleState(state), tip);
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
        holes.Children.Add(symbols);

        // ---- interface
        var ui = GroupBody(ViewKinds.Interface, "Interface", null, false);
        var uiGrid = Columns();
        Toggles(uiGrid, MainViewModel.VisibilityToggles.Where(t => t.Group == ViewKinds.Interface));
        ui.Children.Add(uiGrid);
    }

    /// <summary>Groups of the Visibility tab folded by their header: kept across rebuilds of the tab.</summary>
    private readonly HashSet<string> _foldedGroups = new();

    /// <summary>
    /// The header of a group of the Visibility tab (fold arrow, name, count, padlock, eye) and the panel under it, which
    /// is returned for the group's boxes.
    /// </summary>
    private StackPanel GroupBody(string group, string title, string? count, bool lockable)
    {
        var view = _vm.Project!.View;
        var body = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 4), IsVisible = !_foldedGroups.Contains(group) };
        var arrow = new TextBlock { Text = body.IsVisible ? "▾" : "▸", Foreground = Look.Muted, FontSize = 11, Width = 14, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = Loc.Ui(title), Foreground = Look.Text, FontWeight = FontWeight.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel { LastChildFill = true };
        Button IconButton(string icon, bool on, string tip, Action click, string automation)
        {
            var b = new Button
            {
                Content = Look.Icon(icon, 15, on ? Look.Accent : Look.Faint), Width = 26, Height = 22, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), Background = on && icon == "lock" ? Look.AccentSoft : (IBrush)Brushes.Transparent,
                BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Name = automation
            };
            b.Classes.Add("flat");
            ToolTip.SetTip(b, Loc.Ui(tip));
            b.Click += (_, _) => { Ui.Safe(click); BuildVisibility(); };
            return b;
        }
        var shown = ViewKinds.GroupShown(view, group);
        var eye = IconButton("eye", shown, shown ? "Hide everything in the group; a second click brings it back as its boxes have it" : "The group is hidden: show it again as its boxes have it",
            () => _vm.ToggleGroupHidden(group), "groupEye" + group);
        DockPanel.SetDock(eye, Dock.Right);
        row.Children.Add(eye);
        if (lockable)
        {
            var locked = view.LockedGroups.Contains(group);
            var padlock = IconButton(locked ? "lock" : "unlock", locked, locked ? "The objects of the group are locked: drawn, but not selected. Click to unlock them" : "Lock the objects of the group: they stay drawn but cannot be selected, moved or deleted by accident",
                () => _vm.ToggleGroupLocked(group), "groupLock" + group);
            DockPanel.SetDock(padlock, Dock.Right);
            row.Children.Add(padlock);
        }
        if (count != null)
        {
            var c = new TextBlock { Text = count, Foreground = Look.Faint, FontFamily = Look.Mono, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) };
            DockPanel.SetDock(c, Dock.Right);
            row.Children.Add(c);
        }
        DockPanel.SetDock(arrow, Dock.Left);
        row.Children.Add(arrow);
        row.Children.Add(name);
        var header = new Border
        {
            Child = row, Background = Look.Hover, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1), Padding = new Thickness(6, 3, 20, 3), // the eye off the right edge: the scroll bar runs over it
            Margin = new Thickness(-12, 10, -26, 2), Cursor = new Cursor(StandardCursorType.Hand)
        };
        // the header folds the group (the buttons on it do their own work)
        header.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) != null) return;
            if (!_foldedGroups.Remove(group)) _foldedGroups.Add(group);
            body.IsVisible = !_foldedGroups.Contains(group);
            arrow.Text = body.IsVisible ? "▾" : "▸";
        };
        _visibility.Children.Add(header);
        _visibility.Children.Add(body);
        return body;
    }

    /// <summary>
    /// A switch of the Visibility tab as a check box with its tool tip; with a count, the number of such objects (none:
    /// greyed). A switch that can be a chip over the plan is pinned or unpinned by the right button, and shows a blue
    /// stripe at its right edge while pinned.
    /// </summary>
    private Control ToggleBox(ViewState view, MainViewModel.VisibilityToggle t, int? count = null)
    {
        var label = Loc.Ui(t.Label) + (count is int n ? $" ({n})" : "");
        var cb = new CheckBox
        {
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
            IsChecked = t.Get(view), VerticalAlignment = VerticalAlignment.Top, Name = "vis" + t.Id, MinHeight = 0
        };
        if (count == 0) cb.Opacity = 0.55;
        AutomationProperties.SetName(cb, label);
        ToolTip.SetTip(cb, Loc.Ui(t.Tip));
        cb.IsCheckedChanged += (_, _) => { if (_vm.Project is { } p && cb.IsChecked != t.Get(p.View)) _vm.ToggleVisibility(t); };
        var pinned = t.Pinnable && _vm.IsPinned(t.Id);
        var box = new Border
        {
            Child = cb, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(pinned ? 4 : 7, 0, 0, 0),
            BorderBrush = Look.Blue, BorderThickness = new Thickness(pinned ? 3 : 0, 0, 0, 0), Background = Brushes.Transparent
        };
        if (t.Pinnable)
        {
            var full = !pinned && _vm.PinnedChips.Count(id => MainViewModel.FindToggle(id) != null) >= MainViewModel.MaxPinnedChips;
            var item = new MenuItem
            {
                Header = Loc.Ui(pinned ? "Remove from the chips over the plan" : full ? "Add to the chips over the plan (five are pinned already)" : "Add to the chips over the plan"),
                IsEnabled = pinned || !full, Name = "pin" + t.Id
            };
            item.Click += (_, _) => { _vm.TogglePin(t.Id); BuildVisibility(); };
            box.ContextMenu = new ContextMenu { Items = { item } };
        }
        return box;
    }

    /// <summary>Name and tip of each hole label: where it shows, what it is for and which calculations use the same data.</summary>
    private static readonly Dictionary<HoleLabel, (string Name, string Tip)> LabelInfo = new()
    {
        [HoleLabel.Number] = ("Hole number", "2D, 3D and printed plans: the hole number, in blue. Tells the holes apart in the hole table, loading sheets, reports and the electronic logging report; holes are found by it."),
        [HoleLabel.Length] = ("Length", "2D and 3D: \"L\" next to the collar, the dipped length when measured, otherwise the design length. Used for the charge column and stemming, drilled metres and drilling cost, and for matching loading rules by length."),
        [HoleLabel.Diameter] = ("Diameter", "2D and 3D: \"Ø\" in mm next to the collar. Sets the charge per metre and the explosive mass, the minimum stemming (in diameters), the fragmentation prediction, and the loading rules chosen by diameter."),
        [HoleLabel.Elevation] = ("Elevation", "2D and 3D: \"Z\", the collar elevation. Used for the length down to the grade surface in the design, for sections and for the volume of the blast."),
        [HoleLabel.Angle] = ("Angle", "2D and 3D: \"α\", the angle of the hole as Program settings ▸ Units count it (from vertical or from horizontal). Sets where the toe is; used for the bench height in the fragmentation prediction, flyrock and the drilling tolerance check."),
        [HoleLabel.Bearing] = ("Bearing", "2D and 3D: \"Az\", the bearing of the lean clockwise from north. Sets where the toe of an inclined hole is; used in the drilling tolerance check."),
        [HoleLabel.Temperature] = ("Temperature", "2D and 3D: measured temperature in °C, only for holes where it was measured. Marks hot or reactive ground before loading; not used in the calculations."),
        [HoleLabel.ExplosiveMass] = ("Explosive mass", "2D and 3D: explosive mass of the hole in kg. Comes from the charge column; adds up to the quantities, powder factor, costs and the maximum charge per delay for vibration."),
        [HoleLabel.DeckMasses] = ("Deck masses", "2D and 3D: the mass of each explosive deck in kg, top to bottom. Shows how a decked hole is split; each deck counts on its own in the charge per delay."),
        [HoleLabel.HoleOrder] = ("Hole order", "2D and 3D: the firing order of the hole, from the timing. Checks the initiation sequence; the same order drives the blast animation and the first-movement analysis."),
        [HoleLabel.DeckSequence] = ("Deck sequence", "2D and 3D: the firing order of the decks in the hole, top to bottom (\"2-1\" — the bottom deck fires first), only for holes with two or more primed decks. Checks the in-hole delays of decked holes."),
        [HoleLabel.InholeDelay] = ("In-hole delay", "2D and 3D: the in-hole detonator delay in ms, in red. It is added to the surface time to give the hole firing time."),
        [HoleLabel.SurfaceTime] = ("Surface time", "2D and 3D: the time the surface signal reaches the collar in ms, in red. Comes from the ties and connectors; part of the hole firing time."),
        [HoleLabel.RouteNumber] = ("Route no.", "2D and 3D: the letter of the chain (A the first, B the second…) and the route number of a hole with an electronic detonator, its place in its chain of ties in the order they were made: 1 the first hole, each hole tied from another one more. A tie deleted in the middle of a chain makes the holes after it a chain of the next letter, numbered from 1."),
        [HoleLabel.DetonatorId] = ("Detonator ID", "2D and 3D: the logged electronic detonator ID, only for holes that have one. Used in the electronic logging report and to find a hole by its detonator."),
        [HoleLabel.HoleTime] = ("Hole time", "2D and 3D: the hole firing time in ms (surface time plus in-hole delay), in red. The vibration prediction (charge per delay window), isochrones, the blast animation and first movement use it."),
        [HoleLabel.Type] = ("Hole type", "2D and 3D: \"T\" and the hole type number. The type sets the hole colour, chooses the loading and initiation rules and filters the holes shown.")
    };

    // ---- hole labels: two sides of the collar, each in the order it is drawn -------------------------------------

    private readonly Border _labelHost = new();
    private Grid? _labelGrid;
    private readonly List<Control>[] _labelRows = { new(), new() };
    private readonly Border?[] _labelColumns = new Border?[2];
    /// <summary>The row pressed (it starts a drag once the pointer moves) and whether a drag is on.</summary>
    private (HoleLabel Label, bool Right, int Index, Point At)? _labelPress;
    private bool _labelDragging;
    private (bool Right, int Index)? _labelDrop;

    private Control HoleLabelsHeader()
    {
        var reset = Ui.Chip(Loc.Ui("Default order"), () => ChangeLabels(HoleLabelLayout.Reset), tip: Loc.Ui("Put every label back on its usual side and in its usual order"));
        reset.Height = 24;
        // the switch of all the hole labels (the chip of before), pinned like the other switches
        var hint = new TextBlock
        {
            Text = Loc.Ui("Drag a label to the other side or along its side: the order is the order at the hole, top to bottom."),
            Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6)
        };
        var top = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(reset, Dock.Right);
        top.Children.Add(reset);
        var title = _vm.Project is { } lp ? ToggleBox(lp.View, MainViewModel.FindToggle("HoleLabels")!) : new TextBlock { Text = Loc.Ui("Hole labels") };
        // written as the section names of the tab, in blue
        if (title is Border { Child: CheckBox { Content: TextBlock name } }) { name.Foreground = Look.Blue; name.FontSize = 11; }
        top.Children.Add(title);
        // one row: Font [− 12 +] on the left, Do not scale on the right
        var sizeBox = new TextBox
        {
            Name = "holeLabelFontSize",
            Text = (_vm.Project?.View.HoleLabelFontSize ?? ViewState.DefaultHoleLabelFontSize).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Width = 30, Height = 24, MinHeight = 24, MinWidth = 0, Padding = new Thickness(0), FontSize = 11.5, FontFamily = Look.Mono,
            TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent
        };
        int Current() => _vm.Project?.View.HoleLabelFontSize ?? ViewState.DefaultHoleLabelFontSize;
        void SetSize(int v)
        {
            _vm.SetHoleLabelFontSize(Math.Clamp(v, ViewState.MinHoleLabelFontSize, ViewState.MaxHoleLabelFontSize));
            sizeBox.Text = Current().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        void TakeTyped()
        {
            if (int.TryParse((sizeBox.Text ?? "").Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v)) SetSize(v);
            else sizeBox.Text = Current().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        sizeBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { TakeTyped(); e.Handled = true; } };
        sizeBox.LostFocus += (_, _) => TakeTyped();
        Button SizeStep(string text, string name, int delta, string tip)
        {
            var b = new Button
            {
                Name = name, Content = text, Width = 22, Height = 24, MinHeight = 24, Padding = new Thickness(0), FontSize = 13,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(0), Background = Look.Ribbon, Foreground = Look.TextSoft
            };
            ToolTip.SetTip(b, Loc.Ui(tip));
            b.Click += (_, _) => SetSize(Current() + delta);
            return b;
        }
        var stepper = new Border
        {
            BorderBrush = Look.LineStrong, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    SizeStep("−", "holeLabelFontSmaller", -1, "Smaller"),
                    new Border { Width = 1, Background = Look.LineStrong },
                    sizeBox,
                    new Border { Width = 1, Background = Look.LineStrong },
                    SizeStep("+", "holeLabelFontLarger", 1, "Larger")
                }
            }
        };
        ToolTip.SetTip(stepper, Loc.Ui("Font size of the hole labels and of the delays of the ties on the plan and in 3D, px. Kept in the project; a new blast starts with the last size chosen."));
        // off: the labels grow and shrink with the plan, too small they are not drawn
        var fixedSize = new CheckBox
        {
            Name = "holeLabelsFixedSize",
            IsChecked = _vm.Project?.View.HoleLabelsFixedSize ?? false,
            Content = new TextBlock { Text = Loc.Ui("Do not scale"), FontSize = 11.5, Foreground = Look.Muted },
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(fixedSize, Loc.Ui("On: the hole labels and the delays of the ties keep their font size at any zoom. Off: they grow and shrink with the plan, and too small to read they are not shown. Printed sheets keep the font size."));
        fixedSize.IsCheckedChanged += (_, _) => _vm.SetHoleLabelsFixedSize(fixedSize.IsChecked == true);
        var sizeRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(fixedSize, Dock.Right);
        sizeRow.Children.Add(fixedSize);
        sizeRow.Children.Add(Ui.Row(6, new TextBlock { Text = Loc.Ui("Font"), Foreground = Look.Muted, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center }, stepper));
        return new StackPanel { Children = { top, hint, sizeRow } };
    }

    /// <summary>The colour the label has on the plan: the number blue, the times red, the rest the text colour.</summary>
    private static IBrush LabelColor(HoleLabel l) => l switch
    {
        HoleLabel.Number => Look.Blue,
        HoleLabel.SurfaceTime or HoleLabel.InholeDelay or HoleLabel.HoleTime => new SolidColorBrush(Color.FromUInt32(0xFFEF4444)),
        _ => Look.TextSoft
    };

    /// <summary>A change of the layout, then the columns again (after the event that made it).</summary>
    private void ChangeLabels(Action<ViewState> change)
    {
        _vm.ToggleView(change);
        Avalonia.Threading.Dispatcher.UIThread.Post(BuildLabelColumns);
    }

    private void ToggleLabel(HoleLabel l)
    {
        if (HoleLabelLayout.Flag(l) is { } f) _vm.ToggleView(v => v.HoleLabels ^= f);
        else if (HoleLabelLayout.Timing(l) is { } t) _vm.ToggleTimingLabel(t);
        Avalonia.Threading.Dispatcher.UIThread.Post(BuildLabelColumns);
    }

    private void BuildLabelColumns()
    {
        var p = _vm.Project;
        if (p == null) return;
        var (left, right) = HoleLabelLayout.Sides(p.View);
        _labelPress = null;
        _labelDragging = false;
        _labelDrop = null;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*") };
        for (int side = 0; side < 2; side++)
        {
            var list = side == 0 ? left : right;
            var rows = _labelRows[side];
            rows.Clear();
            var stack = new StackPanel { Spacing = 1 };
            var shown = list.Count(l => HoleLabelLayout.IsShown(p.View, l));
            var head = new DockPanel { Margin = new Thickness(4, 0, 4, 4) };
            var count = new TextBlock { Text = $"{shown}/{list.Count}", Foreground = Look.Faint, FontSize = 11 };
            DockPanel.SetDock(count, Dock.Right);
            head.Children.Add(count);
            head.Children.Add(new TextBlock { Text = Loc.Ui(side == 0 ? "Left" : "Right"), Foreground = Look.Muted, FontSize = 11, FontWeight = FontWeight.SemiBold });
            stack.Children.Add(head);
            for (int i = 0; i < list.Count; i++)
            {
                var row = LabelRow(p.View, list[i], side == 1, i, list.Count);
                rows.Add(row);
                stack.Children.Add(row);
            }
            if (list.Count == 0) stack.Children.Add(new TextBlock { Text = Loc.Ui("Drag a label here"), Foreground = Look.Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(4, 14) });
            var column = new Border
            {
                Name = side == 0 ? "labelsLeft" : "labelsRight",
                Background = Look.Field, BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(3, 6, 3, 4), Child = stack
            };
            _labelColumns[side] = column;
            Grid.SetColumn(column, side * 2);
            grid.Children.Add(column);
        }
        _labelGrid = grid;
        _labelHost.Child = grid;
    }

    private Control LabelRow(ViewState view, HoleLabel label, bool right, int index, int count)
    {
        var (name, tip) = LabelInfo[label];
        var on = HoleLabelLayout.IsShown(view, label);
        var check = new CheckBox { IsChecked = on, MinWidth = 0, MinHeight = 0, Width = 20, Height = 22, Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(check, Loc.Ui(name));
        check.IsCheckedChanged += (_, _) => { if (check.IsChecked != HoleLabelLayout.IsShown(_vm.Project!.View, label)) ToggleLabel(label); };
        // the colour of the label: a click picks one, the right button puts its own back
        var own = view.LabelColors.TryGetValue(label, out var chosen) ? chosen : (uint?)null;
        var dot = new Button
        {
            Name = "labelColour" + label,
            Width = 14, Height = 14, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(7),
            Background = own is uint c0 ? new SolidColorBrush(Color.FromUInt32(c0)) : LabelColor(label),
            BorderBrush = own != null ? Look.Text : Look.LineStrong, BorderThickness = new Thickness(1),
            Opacity = on ? 1 : 0.5, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        uint Current() => own ?? (LabelColor(label) is ISolidColorBrush b ? b.Color.ToUInt32() : 0xFFC9CFD6);
        void SetColour(uint? c)
        {
            _vm.SetHoleLabelColor(label, c);
            Avalonia.Threading.Dispatcher.UIThread.Post(BuildLabelColumns);
        }
        dot.Flyout = ColourFlyout.Make(Current, c => SetColour(c));
        ToolTip.SetTip(dot, Loc.Ui(own != null ? "The colour of the label: click to change it, right click for its own colour" : "The colour of the label: click to change it"));
        AutomationProperties.SetName(dot, Loc.Ui("Label colour"));
        if (own != null)
        {
            var reset = new MenuItem { Header = Loc.Ui("Default colour"), Name = "labelColourReset" + label };
            reset.Click += (_, _) => SetColour(null);
            dot.ContextMenu = new ContextMenu { Items = { reset } };
        }
        var text = new TextBlock { Text = Loc.Ui(name), FontSize = 12, Foreground = on ? Look.Text : Look.Muted, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3) };
        var grip = new TextBlock { Text = "⋮⋮", FontSize = 9, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 1, 0) };
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*") };
        Grid.SetColumn(check, 1); Grid.SetColumn(dot, 2); Grid.SetColumn(text, 3);
        line.Children.Add(grip); line.Children.Add(check); line.Children.Add(dot); line.Children.Add(text);

        Button Small(string icon, string what, bool enabled, Action click)
        {
            var b = new Button { Content = Look.Icon(icon, 12, Look.TextSoft, 2), Width = 20, Height = 20, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center };
            b.Classes.Add("flat");
            ToolTip.SetTip(b, Loc.Ui(what));
            AutomationProperties.SetName(b, Loc.Ui(what));
            b.Click += (_, _) => Ui.Safe(click);
            return b;
        }
        // shown over the end of the name while the pointer is on the row; a long name takes a second line in a narrow panel
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Background = Look.Hover, IsVisible = false };
        var move = Small(right ? "left" : "right", right ? "Move to the left side" : "Move to the right side", true,
            () => ChangeLabels(v => HoleLabelLayout.Move(v, label, !right, int.MaxValue)));
        move.Name = "moveLabel" + label;
        if (right) buttons.Children.Add(move);
        buttons.Children.Add(Small("up", "Move up", index > 0, () => ChangeLabels(v => HoleLabelLayout.Move(v, label, right, index - 1))));
        buttons.Children.Add(Small("down", "Move down", index < count - 1, () => ChangeLabels(v => HoleLabelLayout.Move(v, label, right, index + 1))));
        if (!right) buttons.Children.Add(move);

        // where a dragged label would go: a line over this row, or under it for the end of the side
        var above = new Border { Height = 2, Background = Look.Accent, VerticalAlignment = VerticalAlignment.Top, IsVisible = false, IsHitTestVisible = false, Margin = new Thickness(0, -2, 0, 0) };
        var below = new Border { Height = 2, Background = Look.Accent, VerticalAlignment = VerticalAlignment.Bottom, IsVisible = false, IsHitTestVisible = false, Margin = new Thickness(0, 0, 0, -2) };
        var row = new Border
        {
            Name = "label" + label,
            MinHeight = 26, CornerRadius = new CornerRadius(5), Padding = new Thickness(2, 0), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            Tag = (above, below),
            Child = new Panel { Children = { line, buttons, above, below } }
        };
        ToolTip.SetTip(row, Loc.Ui(tip));
        ToolTip.SetShowDelay(row, 700);
        row.PointerEntered += (_, _) => { if (!_labelDragging) { row.Background = Look.Hover; buttons.IsVisible = true; } };
        row.PointerExited += (_, _) => { if (!_labelDragging) { row.Background = Brushes.Transparent; buttons.IsVisible = false; } };
        // the check box and the buttons take their own presses: a press anywhere else picks the row up
        row.PointerPressed += (_, e) =>
        {
            if (_labelGrid == null || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            _labelPress = (label, right, index, e.GetPosition(_labelGrid));
            e.Pointer.Capture(row);
            e.Handled = true;
        };
        row.PointerMoved += (_, e) =>
        {
            if (_labelPress is not { } press || _labelGrid == null) return;
            var at = e.GetPosition(_labelGrid);
            if (!_labelDragging)
            {
                if (Math.Abs(at.X - press.At.X) + Math.Abs(at.Y - press.At.Y) < 5) return;
                _labelDragging = true;
                row.Opacity = 0.45;
                buttons.IsVisible = false;
            }
            ShowLabelDrop(DropAt(at));
        };
        row.PointerReleased += (_, e) =>
        {
            if (_labelPress is not { } press) return;
            var dragged = _labelDragging;
            var drop = _labelDrop;
            _labelPress = null;
            _labelDragging = false;
            e.Pointer.Capture(null);
            // a click on the name switches the label, like its check box
            if (!dragged) { ToggleLabel(label); return; }
            ShowLabelDrop(null);
            row.Opacity = 1;
            if (drop is not { } d) return;
            // the place was counted with the label still in it: past its old place on its own side is one less
            var to = d.Index;
            if (d.Right == press.Right && press.Index < to) to--;
            if (d.Right == press.Right && to == press.Index) return;
            ChangeLabels(v => HoleLabelLayout.Move(v, label, d.Right, to));
        };
        row.PointerCaptureLost += (_, _) =>
        {
            if (!_labelDragging) return;
            _labelPress = null;
            _labelDragging = false;
            row.Opacity = 1;
            ShowLabelDrop(null);
        };
        return row;
    }

    /// <summary>The side under a point of the columns and the place in it: before the first row whose middle is below.</summary>
    private (bool Right, int Index) DropAt(Point at)
    {
        var right = at.X > _labelGrid!.Bounds.Width / 2;
        var rows = _labelRows[right ? 1 : 0];
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].TranslatePoint(new Point(0, rows[i].Bounds.Height / 2), _labelGrid) is { } mid && at.Y < mid.Y) return (right, i);
        return (right, rows.Count);
    }

    private void ShowLabelDrop((bool Right, int Index)? drop)
    {
        _labelDrop = drop;
        for (int side = 0; side < 2; side++)
        {
            var rows = _labelRows[side];
            var here = drop is { } d && d.Right == (side == 1);
            var index = drop?.Index ?? -1;
            for (int i = 0; i < rows.Count; i++)
            {
                var (above, below) = ((Border, Border))rows[i].Tag!;
                above.IsVisible = here && index == i;
                below.IsVisible = here && index == rows.Count && i == rows.Count - 1;
            }
            // an empty side shows by its outline that it takes the label
            if (_labelColumns[side] is { } column) column.BorderBrush = here && rows.Count == 0 ? Look.Accent : Look.Line;
        }
    }

    /// <summary>The symbol and surface keys stay open or closed across rebuilds of the tab.</summary>
    private bool _symbolsOpen = true, _surfacesOpen = true;
}
