using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.IO;

namespace BlastDesigner.App.Views;

/// <summary>
/// Main window in the work-stage layout: header with quick actions and command search, stage ribbon,
/// view rail, plan with overlay chips and the floating tool panel, inspector, hole table, filters and status bar.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly ProgramSettings _settings;
    private readonly Viewport _viewport = new();
    private readonly MainViewModel _vm;
    private readonly LayersPanel _layers;
    private readonly PropertiesPanel _properties;
    private readonly HoleTable _table;
    private readonly Inspector _inspector;
    private readonly RibbonBar _ribbon;
    private readonly CommandPalette _palette;
    private readonly ToolPanel _toolPanel;
    private readonly Grid _body = new();
    private readonly Border _panelToggle = new()
    {
        Width = 18, Height = 28, Margin = new Thickness(-7, 6, -7, 0), CornerRadius = new CornerRadius(4),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        Background = Look.Panel, BorderBrush = Look.LineStrong, BorderThickness = new Thickness(1),
        Cursor = new Cursor(StandardCursorType.Hand), ZIndex = 10,
    };
    private readonly TextBlock _panelToggleGlyph = new()
    {
        FontSize = 15, Foreground = Look.TextSoft, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    private double _inspectorWidth = 320;
    private readonly Grid _center = new();
    /// <summary>Type size of the status bar: small, the bar takes little of the height of the plan.</summary>
    private const double StatusFont = 11;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Look.TextSoft, Margin = new Thickness(10, 0), FontSize = StatusFont };
    // wide enough for the coordinates of a mine grid, so the filters after it do not jump as the cursor moves
    private readonly TextBlock _coords = new() { Name = "coordinates", VerticalAlignment = VerticalAlignment.Center, MinWidth = 215, FontFamily = Look.Mono, Foreground = Look.Muted, Margin = new Thickness(10, 0, 6, 0), FontSize = StatusFont };
    private readonly ComboBox _defaultLayer = new() { MinWidth = 130, Height = 20, MinHeight = 20, FontSize = StatusFont, Padding = new Thickness(6, 0) };
    // no spinner buttons: ↑ and ↓ change it
    private readonly NumericUpDown _targetRadius = new() { Name = "pickRadius", Minimum = 2, Maximum = 40, Increment = 1, Width = 44, Height = 20, MinHeight = 20, FormatString = "0", FontSize = StatusFont, ShowButtonSpinner = false, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _filters = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly StackPanel _modes = new() { Orientation = Orientation.Horizontal };
    private readonly List<(Button Button, string? Tool)> _railButtons = new();
    private readonly List<(KeyGesture Gesture, Action Action)> _shortcuts = new();
    private bool _forceClose;
    private bool _refreshingLayerCombo;
    private WindowState _preFullScreen = WindowState.Normal;

    private readonly DispatcherTimer _activity = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <param name="before">The window this one is built in place of (<see cref="Rebuild"/>): its blast goes on here.</param>
    public MainWindow(ProgramSettings settings, MainWindow? before = null)
    {
        _settings = settings;
        Dialogs.Owner = this;
        BlastDesigner.Rendering.PlanRenderer.Translate = Loc.T;
        // the layer of a new block: "Блок", "Блок 2"…
        BlockLayers.BaseName = Loc.T("Block");
        Title = "Blast Designer";
        // black until the first frame is drawn, then the colour of the theme: no white flash on starting
        DarkStart.Attach(this, Look.Ground);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FitToScreen();
        // once the window is shown its state is the user's: kept for the next start
        Opened += (_, _) => { KeepWindowState(); PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) KeepWindowState(); }; };
        _vm = new MainViewModel(this, _viewport, settings);
        _layers = new LayersPanel(_vm);
        _properties = new PropertiesPanel(_vm);
        _table = new HoleTable(_vm);
        _inspector = new Inspector(_vm, _properties, _layers);
        _ribbon = new RibbonBar(_vm);
        _palette = new CommandPalette(_vm);
        _toolPanel = new ToolPanel(_vm);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        BuildLayout();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Status)) _status.Text = _vm.Status;
            if (e.PropertyName == nameof(MainViewModel.Coordinates)) _coords.Text = _settings.ShowCoordinatesBox ? _vm.Coordinates : "";
            if (e.PropertyName == nameof(MainViewModel.Title)) Title = _vm.Title;
        };
        _vm.ModelRefreshed += OnModelRefreshed;
        _vm.SelectionRefreshed += () => _ribbon.Refresh();
        _vm.ToolChanged += OnToolChanged;
        _vm.OverlaysChanged += RefreshChips;
        // tunnel, so a focused button or field cannot take the Enter first
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Enter) { ToggleFullScreen(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        var startFull = _settings.Window.TryGetValue("FullScreen", out var full) && full == "1";
        if (startFull) _preFullScreen = _settings.Window.TryGetValue("PreFullScreen", out var pre) && pre == nameof(WindowState.Maximized) ? WindowState.Maximized : WindowState.Normal;
        // full screen is applied once the window has its normal size, so leaving it restores that size
        Opened += async (_, _) =>
        {
            if (startFull) WindowState = WindowState.FullScreen;
            if (before != null)
            {
                _vm.Continue(before._vm);
                var (from, to) = (before._viewport.Camera, _viewport.Camera);
                (to.Target, to.Yaw, to.Pitch, to.Scale) = (from.Target, from.Yaw, from.Pitch, from.Scale);
                _vm.View.Redraw();
                return;
            }
            await OnOpened();
            // after the recovery question and the startup assistant
            if (!_forceClose) WhatsNewWindow.ShowIfUpdated(_settings);
        };
        // the usage statistics leave out the time while no window of the program is active
        var active = true;
        _activity.Tick += (_, _) =>
        {
            var now = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Windows.Any(w => w.IsActive) ?? IsActive;
            if (now == active) return;
            active = now;
            UsageLog.Track(UsageLog.App, now ? "active" : "inactive");
        };
        _activity.Start();
    }

    /// <summary>The size the window opens with on a screen of the program's own size.</summary>
    public const double StartWidth = 1440, StartHeight = 900;
    /// <summary>What the system adds round the content of a window: its title bar and borders, in DIPs.</summary>
    private const double FrameWidth = 16, FrameHeight = 48;

    /// <summary>
    /// The size of the window's content on a screen whose working area is <paramref name="areaWidth"/> × <paramref name="areaHeight"/>
    /// DIPs, and whether it opens maximized. A window larger than the screen would be centred with its title bar above the
    /// top of the screen, out of reach until it is resized: on a small screen, or a large one with large text, the window
    /// opens maximized instead, and its normal size fits the screen.
    /// </summary>
    public static (double Width, double Height, bool Maximized) StartSize(double areaWidth, double areaHeight)
    {
        if (StartWidth + FrameWidth <= areaWidth && StartHeight + FrameHeight <= areaHeight) return (StartWidth, StartHeight, false);
        return (Math.Max(640, Math.Min(StartWidth, areaWidth * 0.9 - FrameWidth)), Math.Max(480, Math.Min(StartHeight, areaHeight * 0.9 - FrameHeight)), true);
    }

    private void FitToScreen()
    {
        Width = StartWidth;
        Height = StartHeight;
        if (Screens.Primary is not { } screen) return;
        var (w, h, max) = StartSize(screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling);
        (Width, Height) = (w, h);
        // the first start opens on the whole screen; later ones as the window was left
        var first = !_settings.Window.TryGetValue(MaximizedKey, out var was);
        if (StartsMaximized(max, first ? null : was)) WindowState = WindowState.Maximized;
        Log.Info($"Main window {w:0}x{h:0}{(WindowState == WindowState.Maximized ? ", maximized" : "")}{(first ? " (first start)" : "")} on a screen of {screen.WorkingArea.Width}x{screen.WorkingArea.Height} px at {screen.Scaling:0.##}");
    }

    /// <summary>
    /// Whether the window opens maximized: on a screen too small for it, on the first start (nothing kept yet), or when it
    /// was left maximized (<paramref name="kept"/> "1").
    /// </summary>
    public static bool StartsMaximized(bool screenTooSmall, string? kept) => screenTooSmall || kept == null || kept == "1";

    /// <summary>Settings key of whether the window was left maximized ("1") or not ("0"); none before the first start.</summary>
    public const string MaximizedKey = "Maximized";

    /// <summary>Keeps whether the window is maximized for the next start; full screen and minimized leave it as it was.</summary>
    private void KeepWindowState()
    {
        if (WindowState is not (WindowState.Maximized or WindowState.Normal)) return;
        var value = WindowState == WindowState.Maximized ? "1" : "0";
        if (_settings.Window.TryGetValue(MaximizedKey, out var old) && old == value) return;
        _settings.Window[MaximizedKey] = value;
        try { _settings.Save(); } catch { }
    }

    /// <summary>
    /// Builds the window again in the language of the settings, as every label is taken when its control is made. The
    /// open blast goes on in the new window with its undo history; the view and the size and place of the window stay.
    /// </summary>
    /// <returns>The new window.</returns>
    public MainWindow Rebuild()
    {
        Loc.Language = _settings.Language;
        var next = new MainWindow(_settings, this) { Width = Width, Height = Height, WindowStartupLocation = WindowStartupLocation.Manual, Position = Position };
        // full screen is taken from the settings by the new window itself
        // maximized or not as this one, whatever the new window chose for the screen
        next.WindowState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = next;
        next.Show();
        _vm.Release();
        _activity.Stop();
        ForceClose();
        return next;
    }

    /// <summary>Alt+Enter: full screen and back to the state before it; remembered for the next start.</summary>
    public void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen) WindowState = _preFullScreen;
        else
        {
            _preFullScreen = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            WindowState = WindowState.FullScreen;
        }
        _settings.Window["FullScreen"] = WindowState == WindowState.FullScreen ? "1" : "0";
        _settings.Window["PreFullScreen"] = _preFullScreen.ToString();
        try { _settings.Save(); } catch { }
    }

    public string? OpenOnStartup { get; set; }
    public MainViewModel ViewModel => _vm;
    public bool LayersVisible => _inspector.IsVisible && _inspector.SelectedTab == 1;
    public bool PropertiesVisible => _inspector.IsVisible;
    public bool TableVisible => _table.IsVisible;
    public RibbonBar Ribbon => _ribbon;

    private void BuildLayout()
    {
        CollectShortcuts(_vm.BuildMenu());

        // ---- body: rail | plan (+ table) | splitter | inspector
        _body.ColumnDefinitions = new ColumnDefinitions("48,*,4,320");
        var rail = BuildRail();
        Grid.SetColumn(rail, 0);
        _body.Children.Add(rail);

        // the plan, then the hole table (Ctrl+T) at a fixed height under it, with no splitter between
        _center.RowDefinitions = new RowDefinitions("*,0,0");
        var plan = new Panel { ClipToBounds = true };
        plan.Children.Add(new Border { Child = _viewport, Background = Look.Field });
        plan.Children.Add(BuildPlanOverlay());
        plan.Children.Add(_anchorLayer);
        plan.Children.Add(_toolPanel);
        _center.Children.Add(plan);
        _viewport.Rendering += () => Dispatcher.UIThread.Post(UpdateAnchorButtons);
        Grid.SetRow(_table, 2);
        _center.Children.Add(_table);
        _table.IsVisible = false;
        Grid.SetColumn(_center, 1);
        _body.Children.Add(_center);

        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Look.Line };
        Grid.SetColumn(split, 2);
        _body.Children.Add(split);
        var inspectorBorder = new Border { Child = _inspector, BorderBrush = Look.Line, BorderThickness = new Thickness(1, 0, 0, 0) };
        Grid.SetColumn(inspectorBorder, 3);
        _body.Children.Add(inspectorBorder);
        // » / « tab on top of the splitter; added last so it draws over the plan and the inspector
        _panelToggle.Child = _panelToggleGlyph;
        _panelToggle.PointerEntered += (_, _) => _panelToggle.Background = Look.Hover;
        _panelToggle.PointerExited += (_, _) => _panelToggle.Background = Look.Panel;
        _panelToggle.PointerPressed += (_, e) => { TogglePropertiesPanel(); e.Handled = true; };
        Grid.SetColumn(_panelToggle, 2);
        _body.Children.Add(_panelToggle);
        UpdatePanelToggle();

        // ---- status bar
        _targetRadius.Value = (decimal)_settings.TargetRadiusPixels;
        _targetRadius.ValueChanged += (_, _) =>
        {
            _settings.TargetRadiusPixels = (double)(_targetRadius.Value ?? 8);
            _viewport.TargetRadius = _settings.TargetRadiusPixels;
        };
        ToolTip.SetTip(_targetRadius, Loc.Ui("Pick radius in pixels of the screen; ↑ and ↓ change it"));
        _defaultLayer.SelectionChanged += (_, _) =>
        {
            if (_refreshingLayerCombo || _vm.Project == null || _defaultLayer.SelectedIndex < 0) return;
            var layer = _vm.Project.Layers[_defaultLayer.SelectedIndex];
            if (layer.Id != _vm.Project.DefaultLayerId) _vm.MakeDefaultLayer(layer);
        };
        var status = new DockPanel { Background = Look.Field, Height = 24 };
        var right = Ui.Row(8, new TextBlock { Text = Loc.T("Layer:"), Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center, FontSize = StatusFont }, _defaultLayer,
            new TextBlock { Text = Loc.Ui("Pick radius"), Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center, FontSize = StatusFont }, _targetRadius);
        right.Margin = new Thickness(0, 0, 8, 0);
        // one line: the coordinates, the quick filters, the hint; the layer and the pick radius on the right. Whether the blast
        // is saved shows in the title of the window.
        DockPanel.SetDock(_coords, Dock.Left);
        DockPanel.SetDock(_filters, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        status.Children.Add(_coords);
        status.Children.Add(_filters);
        status.Children.Add(right);
        status.Children.Add(_status);
        var statusBorder = new Border { Child = status, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0) };

        var root = new DockPanel();
        var header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_ribbon, Dock.Top);
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(_ribbon);
        root.Children.Add(statusBorder);
        root.Children.Add(_body);
        Content = root;
        OnToolChanged();
    }

    /// <summary>The search of the header, folded into a magnifier when the window is narrow, and the one over the plan it opens.</summary>
    private Button? _searchButton;
    private CommandPalette? _searchOver;
    private Popup? _searchPopup;

    /// <summary>
    /// One line: the menu, Undo and Redo, the stage tabs; on the right the search, the settings and help. New, Open, Save
    /// and Print are on the File tab and on their keys; the name of the blast is in the title of the window. When the tabs
    /// leave no room for the search field it folds into a magnifier, which opens the field on the same line, left of it.
    /// </summary>
    private Control BuildHeader()
    {
        // the classic menu opens from the top left corner, where menus are looked for
        var logo = Ui.Row(6, Look.Icon("menu", 16, Look.TextSoft), new TextBlock { Text = Loc.Ui("Menu"), Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center });
        var allMenu = Ui.Flat(logo, Loc.Ui("All commands in the classic menu"), height: 30);
        allMenu.Name = "mainMenu";
        allMenu.Padding = new Thickness(8, 0, 10, 0);
        Ui.DropDown(allMenu, _vm.BuildMenu, PlacementMode.BottomEdgeAlignedLeft);
        var undo = Ui.Flat(Look.Icon("undo", 18, Look.TextSoft), Loc.Ui("Undo (Ctrl+Z)"), _vm.Undo, 30);
        undo.Name = "headerUndo";
        var redo = Ui.Flat(Look.Icon("redo", 18, Look.TextSoft), Loc.Ui("Redo (Ctrl+Y)"), _vm.Redo, 30);
        redo.Name = "headerRedo";
        var history = new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(1, 0, 1, 0), Padding = new Thickness(6, 0), Margin = new Thickness(6, 0, 4, 0), Child = Ui.Row(2, undo, redo) };
        var left = Ui.Row(0, allMenu, history);
        left.Margin = new Thickness(4, 0, 0, 0);

        var settings = Ui.Flat(Look.Icon("gear", 18, Look.TextSoft), Loc.T("Program settings…"), () => _ = _vm.EditProgramSettings(), 30);
        var help = Ui.Flat(Look.Icon("help", 18, Look.TextSoft), Loc.Ui("Help (F1)"), () => AnalysisWindows.Help(), 30);
        _searchButton = Ui.Flat(Look.Icon("search", 18, Look.TextSoft), Loc.Ui("Search: command, hole, setting (Ctrl+K)"), OpenSearch, 30);
        _searchButton.Name = "searchButton";
        _searchButton.IsVisible = false;
        _palette.Margin = new Thickness(8, 0);
        _palette.Width = SearchMax;
        var right = Ui.Row(2, _palette, _searchButton, settings, help);
        right.Margin = new Thickness(0, 0, 12, 0);

        // the search for a narrow window: the field on the line of the header, left of the magnifier, over the tabs
        _searchOver = new CommandPalette(_vm) { Width = 560, Name = "searchOver", BoxShadow = BoxShadows.Parse("0 8 24 0 #73000000") };
        _searchPopup = new Popup
        {
            Child = _searchOver,
            IsLightDismissEnabled = true,
            Placement = PlacementMode.Left,
            HorizontalOffset = -4
        };
        _searchOver.Done += () => _searchPopup.IsOpen = false;

        var tabs = _ribbon.TabStrip;
        // as high as the tabs: no empty band between the title bar of the window and the header
        var bar = new DockPanel { Background = Look.Panel, Height = 31 };
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(left);
        bar.Children.Add(right);
        bar.Children.Add(tabs);
        bar.Children.Add(_searchPopup);
        _searchPopup.PlacementTarget = _searchButton;
        bar.SizeChanged += (_, _) => FitSearch(bar, left, settings, help);
        bar.LayoutUpdated += (_, _) => FitSearch(bar, left, settings, help);
        return new Border { Child = bar, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    /// <summary>Widths of the search field in the header: as wide as this when there is room, no narrower than the least.</summary>
    public const double SearchMax = 360, SearchMin = 220;

    /// <summary>
    /// The search field takes the room the tabs leave, between <see cref="SearchMin"/> and <see cref="SearchMax"/>; with
    /// less than the least it folds into the magnifier.
    /// </summary>
    private void FitSearch(Control bar, Control left, Control settings, Control help)
    {
        if (_searchButton == null) return;
        var room = bar.Bounds.Width - left.Bounds.Width - settings.Bounds.Width - help.Bounds.Width - 12 - 4 - _ribbon.TabsWidth - 16 - 4;
        var fold = room < SearchMin;
        var width = Math.Clamp(room, SearchMin, SearchMax);
        if (_palette.IsVisible == !fold && Math.Abs(_palette.Width - width) < 0.5) return;
        _palette.IsVisible = !fold;
        _searchButton.IsVisible = fold;
        if (!fold) { _palette.Width = width; _searchPopup!.IsOpen = false; }
    }

    /// <summary>Whether the search is folded into the magnifier (the window too narrow for the field).</summary>
    public bool SearchFolded => _searchButton?.IsVisible == true;

    /// <summary>Ctrl+K and the magnifier: the field of the header, or, folded, the search over the plan.</summary>
    public void OpenSearch()
    {
        if (!SearchFolded) { _palette.Focus2(); return; }
        if (_searchPopup == null || _searchOver == null) return;
        _searchPopup.IsOpen = true;
        Dispatcher.UIThread.Post(_searchOver.Focus2, DispatcherPriority.Input);
    }

    private Control BuildRail()
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (icon, tip, key, sep, action, tool) in _vm.BuildRail())
        {
            if (sep) panel.Children.Add(new Border { Width = 24, Height = 1, Background = Look.Line, Margin = new Thickness(0, 4) });
            var content = new Panel { Width = 40, Height = 40 };
            content.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = Look.Icon(icon, 18, Look.TextSoft) });
            content.Children.Add(new TextBlock { Text = key, FontFamily = Look.Mono, FontSize = 8.5, Foreground = Look.Faint, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 3, 1) });
            var b = new Button { Content = content, Width = 40, Height = 40, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8) };
            b.Classes.Add("flat");
            ToolTip.SetTip(b, $"{Loc.Ui(tip)} ({key})");
            if (icon == "polyline")
            {
                // Split button: the icon starts the current drawing tool, the small triangle on the right opens the list;
                // a tool picked there becomes the button's tool (its icon and tip). Without a pointer (keyboard) it opens the list.
                content.Children.Add(new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse("M0,0 L4,3.5 L0,7 Z"), Fill = Look.Muted, Width = 4, Height = 7,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0), IsHitTestVisible = false
                });
                _drawButton = b;
                _drawIcon = (Border)content.Children[0];
                double pressX = -1;
                var heldOpen = false;
                void OpenDrawMenu()
                {
                    var menu = new ContextMenu
                    {
                        ItemsSource = MenuEntry.Build(_vm.DrawingTools().Select(e => e.Action == null ? e : MenuEntry.Item(e.Header, () => { e.Action(); OnToolChanged(); }).WithIcon(e.Icon!))),
                        Placement = PlacementMode.RightEdgeAlignedTop,
                        PlacementTarget = b
                    };
                    menu.Open(b);
                    Ui.Remember(menu);
                }
                // holding the left button on it opens the list too, as on the triangle; the release then starts nothing
                var press = 0;
                void EndHold() => press++;
                b.AddHandler(PointerPressedEvent, (_, e) =>
                {
                    pressX = e.GetPosition(b).X;
                    heldOpen = false;
                    if (!e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) return;
                    var mine = ++press;
                    _ = Task.Delay(400).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (mine != press) return;
                        heldOpen = true;
                        OpenDrawMenu();
                    }));
                }, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
                b.AddHandler(PointerReleasedEvent, (_, _) => EndHold(), Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
                b.AddHandler(PointerCaptureLostEvent, (_, _) => EndHold(), Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
                b.Click += (_, _) =>
                {
                    EndHold();
                    var x = pressX;
                    pressX = -1;
                    if (heldOpen) { heldOpen = false; return; }
                    if (x >= 0 && x < b.Bounds.Width - 12)
                    {
                        UsageLog.Track(UsageLog.Button, _vm.DrawingTool, "rail");
                        Ui.Safe(_vm.RunDrawingTool);
                        OnToolChanged();
                        return;
                    }
                    OpenDrawMenu();
                };
                UpdateDrawButton();
            }
            else b.Click += (_, _) => { UsageLog.Track(UsageLog.Button, tip, "rail"); Ui.Safe(action); OnToolChanged(); };
            _railButtons.Add((b, tool));
            panel.Children.Add(b);
        }
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = panel };
    }

    private Button? _drawButton;
    private Border? _drawIcon;

    /// <summary>Shows the current drawing tool on the rail's drawing button: its icon, tip and highlight.</summary>
    private void UpdateDrawButton()
    {
        if (_drawButton == null || _drawIcon == null) return;
        var t = _vm.CurrentDrawingTool;
        _drawIcon.Child = Look.Icon(t.Icon, 18, Look.TextSoft);
        ToolTip.SetTip(_drawButton, Loc.F(Loc.Ui("Drawing: {0} (others in the list)"), Loc.T(t.Header).ToLower(System.Globalization.CultureInfo.CurrentCulture)) + " (W)");
        var i = _railButtons.FindIndex(r => r.Button == _drawButton);
        if (i >= 0) _railButtons[i] = (_drawButton, t.Tool);
    }

    private ViewCubeControl? _viewCube;
    private Button? _eye;
    /// <summary>The view cube under the Plan and 3D buttons; for tests.</summary>
    public ViewCubeControl? ViewCube => _viewCube;

    private Control BuildPlanOverlay()
    {
        foreach (var (label, action) in new (string, Action)[] { ("Plan", _vm.PlanView), ("3D", _vm.View3D) })
        {
            var b = new Button { Content = Loc.Ui(label), Padding = new Thickness(10, 4), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Look.Muted };
            b.Click += (_, _) => { UsageLog.Track(UsageLog.Button, label, "view"); Ui.Safe(action); RefreshChips(); };
            _modes.Children.Add(b);
        }
        var modes = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = _modes };
        var compass = new Button
        {
            Content = Look.Icon("north", 18, Look.Accent),
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = Look.Panel,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(compass, Loc.Ui("Blast direction: click to set, double-click to reset the orientation"));
        compass.Click += (_, _) => Ui.Safe(_vm.SetBlastOrientation);
        compass.DoubleTapped += (_, _) => Ui.Safe(_vm.BlastView);
        var top = new DockPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12) };
        DockPanel.SetDock(compass, Dock.Right);
        top.Children.Add(compass);
        _viewCube = new ViewCubeControl(_viewport, from => { _vm.ViewFrom(from); RefreshChips(); }) { Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        // the eye: what is behind a surface paler (Shift+H)
        _eye = new Button
        {
            Name = "fadeHidden",
            Content = Look.Icon("eye", 16, Look.Muted),
            Width = 36, Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        ToolTip.SetTip(_eye, Loc.Ui("Hidden paler: what is behind a surface is drawn at a quarter of its opacity; holes stay as they are (Shift+H)"));
        _eye.Click += (_, _) => { UsageLog.Track(UsageLog.Button, "Hidden paler", "view"); Ui.Safe(_vm.ToggleFadeHidden); RefreshChips(); };
        var row = Ui.Row(8, modes, _eye, _chips);
        row.VerticalAlignment = VerticalAlignment.Top;
        modes.VerticalAlignment = VerticalAlignment.Top;
        top.Children.Add(new StackPanel { Children = { row, _viewCube } });
        RefreshChips();
        return top;
    }

    private void RefreshChips()
    {
        _chips.Children.Clear();
        var p = _vm.Project;
        var v = p?.View;
        void Chip(string label, bool on, Action toggle, string tip)
        {
            var b = Ui.Chip(Loc.Ui(label), () => { UsageLog.Track(UsageLog.Button, label, "chip"); toggle(); RefreshChips(); }, on, Loc.Ui(tip));
            b.IsEnabled = p != null;
            _chips.Children.Add(b);
        }
        var timing = _vm.AnalysisOverlays.Any(o => o is BlastDesigner.Rendering.TimingContourOverlay);
        Chip("Visualization", timing, () => { if (timing) { Dialogs.CloseWindow("Visualization"); _vm.ClearOverlays(); } else _vm.VisualizeTiming(); }, "Isochrones and colour by time (Ctrl+Shift+V)");
        Chip("Charge 3D", v?.ShowLoading == true, () => _vm.ToggleView(x => x.ShowLoading = !x.ShowLoading), "Charge columns along the holes, also without depth lines (Ctrl+Shift+L)");
        Chip("Labels", v?.ShowText == true, () => _vm.ToggleView(x => x.ShowText = !x.ShowText), "Hole labels and times");
        Chip("Depths", v != null && !v.CollarsOnly, () => _vm.ToggleView(x => x.CollarsOnly = !x.CollarsOnly), "Hole depth lines from collar to toe; off shows collars only");
        Chip("Ties", v?.ShowTies == true, () => _vm.ToggleView(x => x.ShowTies = !x.ShowTies), "Surface ties");
        if (_vm.HasAnalysisDisplay)
        {
            var clear = Ui.Chip("✕ " + Loc.Ui("Clear analysis"), () => { Dialogs.CloseWindow("Visualization"); _vm.ClearOverlays(); }, true, Loc.Ui("Remove maps, isochrones and arrows from the plan (Esc)"));
            _chips.Children.Add(clear);
        }
        if (_eye != null)
        {
            var on = v?.FadeHidden == true;
            _eye.IsEnabled = p != null;
            _eye.Background = on ? Look.AccentSoft : Look.Panel;
            _eye.BorderBrush = on ? Look.Accent : Look.Line;
            _eye.Content = Look.Icon("eye", 16, on ? Look.Accent : Look.Muted);
        }
        var plan = _viewport.Camera.IsPlan;
        for (int i = 0; i < _modes.Children.Count; i++)
        {
            var b = (Button)_modes.Children[i];
            var active = i == 0 ? plan : i == 1 && !plan;
            b.Background = active ? Look.Line : Brushes.Transparent;
            b.Foreground = active ? Look.Text : Look.Muted;
            b.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    private void RefreshFilters()
    {
        _filters.Children.Clear();
        var p = _vm.Project;
        foreach (var f in _vm.QuickFilters())
        {
            var holes = p == null ? new List<Hole>() : f.Holes(p).ToList();
            var b = new Button
            {
                Content = Ui.Row(5, new TextBlock { Text = Loc.Ui(f.Label), FontSize = StatusFont, Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = holes.Count.ToString(), FontFamily = Look.Mono, FontSize = StatusFont, Foreground = Look.Accent, VerticalAlignment = VerticalAlignment.Center }),
                Height = 18,
                MinHeight = 18,
                Padding = new Thickness(8, 0),
                CornerRadius = new CornerRadius(12),
                Background = Brushes.Transparent,
                BorderBrush = Look.LineStrong,
                BorderThickness = new Thickness(1),
                IsEnabled = holes.Count > 0
            };
            b.Classes.Add("flat");
            ToolTip.SetTip(b, $"{Loc.Ui(f.Label)} ({f.Key})");
            b.Click += (_, _) => _vm.SelectHoles(holes);
            _filters.Children.Add(b);
        }
    }

    private void CollectShortcuts(IEnumerable<MenuEntry> entries)
    {
        foreach (var e in entries)
        {
            if (e.Gesture != null && e.Action != null && e.Gesture != "Alt+F4")
                _shortcuts.Add((KeyGesture.Parse(e.Gesture), () => { if (e.IsEnabled?.Invoke() != false) e.Action(); }));
            if (e.Children != null) CollectShortcuts(e.Children);
        }
    }

    /// <summary>Run a menu command by its path (used by keyboard shortcuts of the stage layout).</summary>
    private void Cmd(string path)
    {
        if (_vm.CommandIndex.TryGetValue(path, out var e) && e.Action != null && e.IsEnabled?.Invoke() != false) e.Action();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var typing = FocusManager?.GetFocusedElement() is TextBox;
        // Text editing keeps its own shortcuts.
        if (typing && e.Key is Key.Delete or Key.C or Key.V or Key.X or Key.A or Key.Z or Key.Y or Key.Enter or Key.Tab or Key.Home) return;
        var m = e.KeyModifiers;
        var shiftCtrl = KeyModifiers.Control | KeyModifiers.Shift;
        Action? a = null;
        // arrows move the selection with the Select tool, as a drag does: on the ground, Shift+↑/↓ up and down (Z), Ctrl 0.1 m steps
        if (!typing && e.Key is Key.Left or Key.Right or Key.Up or Key.Down && _vm.ActiveTool is SelectTool && _vm.HasSelection
            && (m & ~(KeyModifiers.Shift | KeyModifiers.Control)) == 0)
        {
            var right = e.Key == Key.Right ? 1 : e.Key == Key.Left ? -1 : 0;
            var up = e.Key == Key.Up ? 1 : e.Key == Key.Down ? -1 : 0;
            a = () => _vm.NudgeSelection(right, up, m.HasFlag(KeyModifiers.Shift), m.HasFlag(KeyModifiers.Control));
        }
        else if (m == KeyModifiers.Alt && e.Key >= Key.D1 && e.Key <= Key.D7) a = () => _ribbon.Select(e.Key - Key.D1);
        else if (m == KeyModifiers.Control) a = e.Key switch
        {
            Key.K => OpenSearch,
            Key.G => _vm.GroupSelected,
            Key.T => ToggleTable,
            Key.E => () => _ = _vm.FindHole("empty"),
            Key.M => () => _ = _vm.FindHole("misfire"),
            Key.F => () => _ = _vm.FindHole("label"),
            Key.L => () => _vm.SelectHoles(_vm.Project?.Holes.Where(h => h.ActualLength.HasValue) ?? Enumerable.Empty<Hole>()),
            Key.U => () => _vm.SelectHoles(_vm.Project?.Holes.Where(h => !h.IsDummy && !h.ElectronicTimeMs.HasValue) ?? Enumerable.Empty<Hole>()),
            _ => null
        };
        else if (m == shiftCtrl) a = e.Key switch
        {
            Key.V => () => Cmd("Calculations/Visualize timing"),
            Key.Q => () => Cmd("Calculations/Quantities"),
            Key.L => () => Cmd("View/Loading"),
            Key.S => () => Cmd("View/Section view"),
            Key.X => () => Cmd("File/Export/IREDES Plan file…"),
            Key.D => () => Cmd("View/Dashboard"),
            Key.O => () => Cmd("View/Overview"),
            Key.Y => () => Cmd("View/Single hole view (bullseye)"),
            Key.H => () => Cmd("View/Horizontal slice"),
            Key.R => () => Cmd("View/Row check view"),
            _ => null
        };
        else if (m == KeyModifiers.Shift && e.Key == Key.Z && !typing) a = _vm.UndoZoom;
        else if (m == KeyModifiers.Shift && e.Key == Key.H && !typing) a = () => { _vm.ToggleFadeHidden(); RefreshChips(); };
        // the plus of the main keys comes with Shift
        else if (m is KeyModifiers.None or KeyModifiers.Shift && !typing && _vm.ActiveTool is TieTool && e.Key is Key.Add or Key.OemPlus or Key.Subtract or Key.OemMinus)
            a = () => StepTargetRadius(e.Key is Key.Add or Key.OemPlus ? 1 : -1);
        else if (m == KeyModifiers.None && !typing) a = e.Key switch
        {
            Key.V => _vm.SelectTool,
            Key.H => () => _vm.SetTool(new PanTool(_vm)),
            Key.Q => _vm.LassoTool,
            Key.Z => () => _vm.SetTool(new ZoomBoxTool(_vm)),
            Key.Home => _vm.ZoomAll,
            Key.O => () => _vm.SetTool(new RotateTool(_vm, false)),
            Key.N => () => _vm.SetTool(new RotateTool(_vm, true)),
            Key.M => _vm.MeasureTool,
            Key.W => _vm.RunDrawingTool,
            Key.S => _vm.ToggleSnap,
            Key.G => () => _vm.ToggleView(v => v.ShowGrid = !v.ShowGrid),
            Key.D => () => _vm.HoleTool(),
            Key.R => _vm.RowTool,
            Key.P => _vm.PatternTool,
            Key.L => _vm.LoadingTool,
            Key.T => () => _vm.SetTool(new TieTool(_vm)),
            Key.I => () => _vm.InitiationPointTool(false),
            Key.E => _vm.SetTimeTool,
            Key.X => () => AnalysisWindows.SectionTool(_vm),
            Key.F7 => () => ShowInspectorTab(1),
            Key.F3 => () => _ = _vm.FindHole("label"),
            Key.F when _vm.ActiveTool is TieTool => () => { _vm.TieCaptureShown = !_vm.TieCaptureShown; _vm.View.Redraw(); },
            Key.Tab when _vm.ActiveTool is not null and not SelectTool => () => _ = _vm.EnterCoordinates(),
            Key.Enter when _vm.ActiveTool is SelectTool && _vm.HasSelection => () => ShowInspectorTab(0),
            Key.Enter when _vm.ActiveTool is not null and not SelectTool => _vm.CompleteTool,
            _ => null
        };
        if (a == null)
        {
            foreach (var (g, act) in _shortcuts)
                if (g.Matches(e)) { a = act; break; }
        }
        if (a == null && e.Key == Key.Escape)
            a = () =>
            {
                _vm.ActiveTool?.Cancel();
                if (_vm.ActiveTool is not SelectTool) _vm.SelectTool();
                else if (_vm.HasSelection) _vm.Doc?.ClearSelection();
                else if (_vm.HasAnalysisDisplay) { Dialogs.CloseWindow("Visualization"); _vm.ClearOverlays(); }
            };
        if (a == null) return;
        if (_vm.Doc == null && e.Key is not (Key.N or Key.O or Key.K) && m != KeyModifiers.Alt) return;
        UsageLog.Track(UsageLog.Hotkey, new KeyGesture(e.Key, m).ToString());
        Ui.Safe(a);
        OnToolChanged();
        e.Handled = true;
    }

    /// <summary>The pick radius a step up or down, within the limits of its field in the status bar.</summary>
    private void StepTargetRadius(int step)
    {
        _targetRadius.Value = Math.Clamp((_targetRadius.Value ?? 8) + step, _targetRadius.Minimum, _targetRadius.Maximum);
        _vm.SetStatus(Loc.F(Loc.Ui("Pick radius: {0:0} px"), _targetRadius.Value));
        _vm.View.Redraw();
    }

    private async Task OnOpened()
    {
        if (!Trial.Start())
        {
            await Dialogs.Info(Trial.Message!);
            ForceClose();
            return;
        }
        if (Trial.IsTrial) _vm.RefreshAll();
        await _vm.CheckRecovery();
        if (_vm.Doc != null) return;
        if (OpenOnStartup != null) { await _vm.OpenPath(OpenOnStartup); return; }
        if (_settings.ShowStartupAssistant)
        {
            var choice = await StartupWindow.Show(_settings);
            switch (choice)
            {
                case StartupChoice.New: await _vm.New(); break;
                case StartupChoice.Template: await _vm.New(preferTemplate: true); break;
                case StartupChoice.Open: await _vm.Open(); break;
                case StartupChoice.Recent when StartupWindow.SelectedRecent != null: await _vm.OpenPath(StartupWindow.SelectedRecent); break;
            }
        }
        if (_vm.Doc == null) _vm.NewSilent();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_forceClose) return;
        e.Cancel = true;
        await Dispatcher.UIThread.InvokeAsync(async () => await _vm.Exit());
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (files is { Count: > 0 }) await _vm.DropFiles(files);
    }

    private void OnModelRefreshed()
    {
        _refreshingLayerCombo = true;
        if (_vm.Project != null)
        {
            _defaultLayer.ItemsSource = _vm.Project.Layers.Select(l => Loc.Name(l.Name)).ToList();
            _defaultLayer.SelectedIndex = _vm.Project.Layers.FindIndex(l => l.Id == _vm.Project.DefaultLayerId);
        }
        else
        {
            _defaultLayer.ItemsSource = null;
        }
        _refreshingLayerCombo = false;
        RefreshFilters();
        RefreshChips();
        _ribbon.Refresh();
    }

    /// <summary>Icon buttons a drawing tool shows next to its last point (row tool: ✓ finish, ✕ cancel segment).</summary>
    private readonly Canvas _anchorLayer = new();
    private Tool? _anchorTool;

    private void UpdateAnchorButtons()
    {
        var tool = _vm.ActiveTool;
        var anchor = tool?.ActionAnchor;
        if (tool == null || anchor is not Vec3 at || tool.AnchorActions.Count == 0)
        {
            _anchorLayer.IsVisible = false;
            return;
        }
        if (_anchorTool != tool)
        {
            _anchorTool = tool;
            _anchorLayer.Children.Clear();
            foreach (var a in tool.AnchorActions)
            {
                var ok = a.Icon == "check";
                var b = new Button
                {
                    Content = Look.Icon(a.Icon, 16, ok ? Look.Accent : Look.Text, 2.2),
                    Width = 30, Height = 30, Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(15),
                    Background = Look.Panel, BorderBrush = ok ? Look.Accent : Look.Line, BorderThickness = new Thickness(1.5),
                    HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                    Tag = a.Icon
                };
                ToolTip.SetTip(b, Loc.Ui(a.Tip));
                var run = a.Run;
                b.Click += (_, _) => { Ui.Safe(run); _viewport.Redraw(); _viewport.Focus(); };
                _anchorLayer.Children.Add(b);
            }
        }
        var (x, y) = _viewport.Camera.ToScreen(at);
        // The pair (2 × 30 px + 6 px gap) is centred under the end point; it moves above the point near the bottom edge.
        double left = Math.Clamp(x - 33, 4, Math.Max(4, _viewport.Bounds.Width - 70)), top = y + 14;
        if (top + 34 > _viewport.Bounds.Height) top = y - 14 - 30;
        for (int i = 0; i < _anchorLayer.Children.Count; i++)
        {
            Canvas.SetLeft(_anchorLayer.Children[i], left + i * 36);
            Canvas.SetTop(_anchorLayer.Children[i], top);
        }
        _anchorLayer.IsVisible = true;
    }

    /// <summary>The anchored buttons, for the smoke test.</summary>
    public IReadOnlyList<Button> AnchorButtons => _anchorLayer.IsVisible ? _anchorLayer.Children.OfType<Button>().ToList() : Array.Empty<Button>();

    private void OnToolChanged()
    {
        UpdateDrawButton();
        var name = _vm.ActiveTool?.Name;
        foreach (var (b, tool) in _railButtons)
        {
            var active = tool != null && tool == name;
            b.Background = active ? Look.Line : Brushes.Transparent;
            b.BorderBrush = Look.Accent;
            // the icon of the active tool in the accent colour
            if (b.Content is Panel { Children.Count: > 0 } c && c.Children[0] is Border { Child: Viewbox { Child: Avalonia.Controls.Shapes.Path icon } })
                icon.Stroke = active ? Look.Accent : Look.TextSoft;
        }
        _toolPanel.Refresh();
        _ribbon.Refresh();
    }

    /// <summary>Opens the inspector on Properties (0), Layers (1) or Visibility (2).</summary>
    public void ShowInspectorTab(int index)
    {
        if (!_inspector.IsVisible) TogglePropertiesPanel();
        _inspector.SelectedTab = index;
    }

    /// <summary>Brings Properties forward in an open inspector (a hidden panel stays hidden).</summary>
    public void ShowPropertiesTab()
    {
        if (_inspector.IsVisible) _inspector.SelectedTab = 0;
    }

    public void ToggleLayers()
    {
        if (LayersVisible) TogglePropertiesPanel();
        else ShowInspectorTab(1);
    }

    public void TogglePropertiesPanel()
    {
        if (_inspector.IsVisible && _body.ColumnDefinitions[3].ActualWidth > 40) _inspectorWidth = _body.ColumnDefinitions[3].ActualWidth;
        _inspector.IsVisible = !_inspector.IsVisible;
        _body.ColumnDefinitions[3].Width = _inspector.IsVisible ? new GridLength(_inspectorWidth) : new GridLength(0);
        if (_inspector.IsVisible) _properties.Refresh();
        UpdatePanelToggle();
    }

    private void UpdatePanelToggle()
    {
        _panelToggleGlyph.Text = _inspector.IsVisible ? "»" : "«";
        // straddles the splitter while open; tucks inside the window edge once the panel is gone
        _panelToggle.Margin = _inspector.IsVisible ? new Thickness(-7, 6, -7, 0) : new Thickness(-16, 6, 2, 0);
        ToolTip.SetTip(_panelToggle, Loc.Ui(_inspector.IsVisible ? "Hide the panel" : "Show the panel"));
    }

    public void ToggleTable()
    {
        _table.IsVisible = !_table.IsVisible;
        _center.RowDefinitions[2].Height = _table.IsVisible ? new GridLength(220) : new GridLength(0);
        if (_table.IsVisible) _table.Refresh();
    }

    public void RestoreLayout()
    {
        if (!_inspector.IsVisible) TogglePropertiesPanel();
        if (_table.IsVisible) ToggleTable();
        _inspector.SelectedTab = 0;
        _inspectorWidth = 320;
        _body.ColumnDefinitions[3].Width = new GridLength(320);
        _ribbon.Select(2);
        Width = 1440;
        Height = 900;
    }
}
