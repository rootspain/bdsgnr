using Avalonia.Controls;
using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.App.Tools;

/// <summary>Declarative menu item used for menus, context menus and toolbars.</summary>
public sealed class MenuEntry
{
    public string Header { get; init; } = string.Empty;
    /// <summary>A smaller second line under the header (the folder of a recent file).</summary>
    public string? Detail { get; init; }
    /// <summary>A small text at the right end of the header line (when a recent file was opened).</summary>
    public string? Note { get; init; }
    /// <summary>
    /// What has to be asked of the disk, run on a background thread once the menu is open so that a lost drive cannot hold it up:
    /// the tip of the item and, when it has none yet, its note.
    /// </summary>
    public Func<(string Tip, string? Note)>? Later { get; init; }
    /// <summary>The tip until <see cref="Later"/> has answered.</summary>
    public string? Pending { get; init; }
    public Action? Action { get; init; }
    public Func<bool>? IsEnabled { get; init; }
    public Func<bool>? IsChecked { get; init; }
    public string? Gesture { get; init; }
    public List<MenuEntry>? Children { get; init; }
    /// <summary>Called when a submenu opens, to build items dynamically (recent files, undo list).</summary>
    public Func<IEnumerable<MenuEntry>>? Dynamic { get; init; }
    public bool IsSeparator { get; init; }
    /// <summary>Key of a <see cref="Look.Icon"/> stroke icon shown left of the header.</summary>
    public string? Icon { get; private set; }

    public MenuEntry WithIcon(string key) { Icon = key; return this; }

    /// <summary>A square of a colour left of the header, in place of an icon (ARGB): a colour to choose.</summary>
    public uint? Swatch { get; private set; }

    public MenuEntry WithSwatch(uint argb) { Swatch = argb; return this; }

    public static MenuEntry Separator => new() { IsSeparator = true };

    public static MenuEntry Item(string header, Action action, Func<bool>? enabled = null, string? gesture = null) =>
        new() { Header = header, Action = action, IsEnabled = enabled, Gesture = gesture };

    public static MenuEntry Check(string header, Func<bool> isChecked, Action toggle, Func<bool>? enabled = null) =>
        new() { Header = header, Action = toggle, IsChecked = isChecked, IsEnabled = enabled };

    public static MenuEntry Sub(string header, params MenuEntry[] children) => new() { Header = header, Children = children.ToList() };

    public static List<Control> Build(IEnumerable<MenuEntry> entries) => entries.Select(BuildOne).ToList();

    private static async void FillLater(MenuEntry e, MenuItem item)
    {
        // one text for both: it changes in place, also while the tip is on the screen
        var shownTip = new TextBlock { Text = e.Pending };
        ToolTip.SetTip(item, shownTip);
        // beside the menu, not at the pointer: over the next rows it would stop the tips of the rows it covers
        ToolTip.SetPlacement(item, PlacementMode.Right);
        ToolTip.SetHorizontalOffset(item, 8);
        try
        {
            var (tip, note) = await Task.Run(e.Later!);
            shownTip.Text = tip;
            if (note != null && item.Header is StackPanel { Children: [Grid title, ..] } && title.Children[^1] is TextBlock shown) shown.Text = note;
        }
        catch (Exception ex) { IO.Log.Error("Could not read a menu item", ex); }
    }

    private static Control BuildOne(MenuEntry e)
    {
        if (e.IsSeparator) return new Separator();
        var header = Loc.T(e.Header);
        var mi = new MenuItem { Header = e.IsChecked != null ? (e.IsChecked() ? "✓ " : "    ") + header : header };
        if (e.Detail != null)
        {
            var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { new TextBlock { Text = header } } };
            var note = new TextBlock { Text = e.Note, FontSize = 11, Foreground = Look.Muted, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Avalonia.Thickness(24, 0, 0, 0) };
            Grid.SetColumn(note, 1);
            title.Children.Add(note);
            mi.Header = new StackPanel { Spacing = 1, Children = { title, new TextBlock { Text = e.Detail, FontSize = 11, Foreground = Look.Muted } } };
        }
        if (e.Later != null) FillLater(e, mi);
        if (e.Gesture != null) mi.InputGesture = KeyGesture.Parse(e.Gesture);
        if (e.Icon != null) mi.Icon = Look.Icon(e.Icon, 16, Look.TextSoft, 2);
        if (e.Swatch is uint argb)
            mi.Icon = new Border
            {
                Width = 14, Height = 14, CornerRadius = new Avalonia.CornerRadius(3), BorderThickness = new Avalonia.Thickness(1), BorderBrush = Look.Line,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromUInt32(argb))
            };
        if (e.IsEnabled != null) mi.IsEnabled = e.IsEnabled();
        if (e.Children != null || e.Dynamic != null)
        {
            var placeholder = e.Children != null ? Build(e.Children) : new List<Control> { new MenuItem { Header = "…" } };
            mi.ItemsSource = placeholder;
            mi.SubmenuOpened += (_, args) =>
            {
                if (args.Source != mi) return;
                var items = e.Dynamic != null ? e.Dynamic().ToList() : e.Children!;
                mi.ItemsSource = Build(items.Count > 0 ? items : new List<MenuEntry> { new() { Header = "(empty)", IsEnabled = () => false } });
            };
        }
        else if (e.Action != null)
        {
            mi.Click += (_, _) =>
            {
                IO.UsageLog.Track(IO.UsageLog.Menu, e.Header);
                try { e.Action(); }
                catch (Exception ex) { Dialogs.ShowError(ex); }
            };
        }
        return mi;
    }
}

/// <summary>Icon button the window places next to <see cref="Tool.ActionAnchor"/>.</summary>
public sealed record AnchorAction(string Icon, string Tip, Action Run);

/// <summary>Base class for interactive viewport tools.</summary>
public abstract class Tool
{
    protected Tool(MainViewModel vm) => Vm = vm;

    protected MainViewModel Vm { get; }
    protected Viewport View => Vm.View;
    protected PreviewOverlay Preview => Vm.View.Preview;

    public abstract string Name { get; }
    public virtual string Hint => string.Empty;

    public virtual void Activate() => Vm.SetStatus(Loc.T(Hint));
    public virtual void Deactivate() => Preview.Clear();

    public virtual void PointerPressed(ToolPointer p) { }
    public virtual void PointerMoved(ToolPointer p) { }
    public virtual void PointerReleased(ToolPointer p) { }

    /// <summary>True while the tool holds unfinished input (points being drawn, a tie started) that Esc should drop first.</summary>
    protected virtual bool HasPendingInput => false;

    /// <summary>Returns true when the key was handled.</summary>
    public virtual bool KeyDown(Key key, KeyModifiers mods)
    {
        // Esc drops a pending entry and keeps the tool; with nothing pending it is left to the window, which closes the tool.
        if (key == Key.Escape)
        {
            if (!HasPendingInput) return false;
            Cancel();
            return true;
        }
        return false;
    }

    public virtual void Cancel() => Preview.Clear();

    /// <summary>Completes the input in progress (Done / Enter). Returns false when nothing was pending, so the caller closes the tool.</summary>
    public virtual bool FinishEntry() => false;

    public virtual IEnumerable<MenuEntry> ContextMenu(ToolPointer p)
    {
        yield return MenuEntry.Item("Close tool", () => Vm.SelectTool());
    }

    /// <summary>World point next to which the window shows <see cref="AnchorActions"/> as icon buttons; null hides them.</summary>
    public virtual Vec3? ActionAnchor => null;
    public virtual IReadOnlyList<AnchorAction> AnchorActions => Array.Empty<AnchorAction>();

    internal static SKColor PreviewColor => new(0xFF2563EB);
    internal static SKColor SnapColor => new(0xFF22C55E);
    internal static SKColor RefusedColor => new(0xFFEF4444);
}

/// <summary>Collects clicked points; finishes on a count, double-click or Enter.</summary>
public sealed class PickPointsTool : Tool
{
    private readonly List<Vec3> _points = new();
    private readonly string _name;
    private readonly string _hint;
    private readonly int _min;
    private readonly int _max;
    private readonly bool _closed;
    private readonly Action<List<Vec3>> _finish;
    private Vec3? _hover;

    public PickPointsTool(MainViewModel vm, string name, string hint, int min, int max, bool closed, Action<List<Vec3>> finish) : base(vm)
    {
        _name = name; _hint = hint; _min = min; _max = max; _closed = closed; _finish = finish;
    }

    public override string Name => ClosedSource?.Invoke() == true && ClosedName != null ? ClosedName : _name;
    public override string Hint => _hint;
    public Func<IReadOnlyList<Vec3>, Vec3, string?>? Measure { get; init; }
    /// <summary>
    /// Where a click puts its point, from the pointer: the point (snapped, on a surface…) or none with why (the status bar
    /// says it); null: the snapped point as it is.
    /// </summary>
    public Func<ToolPointer, (Vec3? Point, string? Why)>? Constrain { get; init; }
    /// <summary>Whether the entry closes, asked as it goes (the Close switch of the panel): the line back to the first point, three points at least.</summary>
    public Func<bool>? ClosedSource { get; init; }
    /// <summary>The name of the tool while it closes (Polygon for the Polyline tool).</summary>
    public string? ClosedName { get; init; }
    /// <summary>Esc finishes the entry (too few points: it is given up) instead of giving it up.</summary>
    public bool EscFinishes { get; init; }
    /// <summary>
    /// The points of the entry itself are snapped to (within the pick radius, before anything else), and a click on the
    /// first one, with three points or more, closes the entry and finishes it (<see cref="ClosedByClick"/>).
    /// </summary>
    public bool SnapOwnPoints { get; init; }
    /// <summary>The entry being finished was closed by a click on its first point.</summary>
    public bool ClosedByClick { get; private set; }

    /// <summary>The point of the entry under the pointer (within the pick radius), its index; -1 when none.</summary>
    private int OwnPointAt(ToolPointer p)
    {
        if (!SnapOwnPoints) return -1;
        var best = -1;
        var bestD = View.TargetRadius;
        for (int i = 0; i < _points.Count; i++)
        {
            var (x, y) = View.Camera.ToScreen(_points[i]);
            var d = Math.Sqrt((x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y));
            if (d <= bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private bool Closed => ClosedSource?.Invoke() ?? _closed;
    private int Min => ClosedSource?.Invoke() == true ? Math.Max(_min, 3) : _min;

    /// <summary>The point a pointer gives (a point of the entry itself first), or null with why.</summary>
    private (Vec3? Point, string? Why) PointOf(ToolPointer p) =>
        OwnPointAt(p) is var own and >= 0 ? (_points[own], null) : Constrain?.Invoke(p) ?? (p.Snapped, null);
    public bool Repeat { get; init; } = true;
    /// <summary>Extra context-menu items for this tool.</summary>
    public List<MenuEntry> Extra { get; } = new();

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left) return;
        if (p.ClickCount >= 2 && _points.Count >= Min) { Finish(); return; }
        // a click on the first point closes the entry, three points or more
        if (SnapOwnPoints && _points.Count >= 3 && OwnPointAt(p) == 0) { ClosedByClick = true; Finish(); return; }
        // a click on the last point (within the pick radius on screen) finishes, as a double click does;
        // with too few points yet it adds nothing rather than the same point twice
        if (_points.Count > 0)
        {
            var (lx, ly) = View.Camera.ToScreen(_points[^1]);
            if (Math.Sqrt((lx - p.X) * (lx - p.X) + (ly - p.Y) * (ly - p.Y)) <= View.TargetRadius)
            {
                if (_points.Count >= Min) Finish();
                return;
            }
        }
        var (point, why) = PointOf(p);
        if (point is not Vec3 at) { if (why != null) Vm.SetStatus(Loc.T(why)); return; }
        _points.Add(at);
        if (_points.Count >= _max) Finish();
        UpdatePreview();
    }

    public override void PointerMoved(ToolPointer p)
    {
        (_hover, _hoverWhy) = PointOf(p);
        _cursor = p.Snapped;
        UpdatePreview();
    }

    private string? _hoverWhy;
    private Vec3? _cursor;

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        // Enter with nothing to finish is left to the window, which closes the tool.
        if (key is Key.Enter or Key.Return) return FinishEntry();
        if (key == Key.Back && _points.Count > 0) { _points.RemoveAt(_points.Count - 1); UpdatePreview(); return true; }
        // Esc keeps what was drawn: the entry is finished (too few points for one: it is given up)
        if (key == Key.Escape && EscFinishes && _points.Count > 0)
        {
            if (_points.Count >= Min) Finish(); else Cancel();
            return true;
        }
        return base.KeyDown(key, mods);
    }

    protected override bool HasPendingInput => _points.Count > 0;

    public override void Cancel()
    {
        _points.Clear();
        ClosedByClick = false;
        base.Cancel();
    }

    public override bool FinishEntry()
    {
        if (_points.Count == 0) return false;
        // Too few points: keep the entry and say what is missing instead of closing the tool and losing it.
        if (_points.Count < Min) { Vm.SetStatus(Loc.F("At least {0} points are needed", Min)); return true; }
        Finish();
        return true;
    }

    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p)
    {
        foreach (var e in Extra) yield return e;
        yield return MenuEntry.Item("Finish entry", Finish, () => _points.Count >= Min);
        yield return MenuEntry.Item("Cancel entry", Cancel, () => _points.Count > 0);
        foreach (var e in base.ContextMenu(p)) yield return e;
    }

    private void Finish()
    {
        if (_points.Count < Min && !ClosedByClick) return;
        var pts = _points.ToList();
        _points.Clear();
        Preview.Clear();
        try { _finish(pts); }
        catch (Exception ex) { Dialogs.ShowError(ex); }
        finally { ClosedByClick = false; }
        if (!Repeat) Vm.SelectTool();
        View.Redraw();
    }

    private void UpdatePreview()
    {
        Preview.Lines.Clear();
        Preview.Points.Clear();
        Preview.Labels.Clear();
        var pts = _points.ToList();
        // the cursor on the first point (three points or more): the entry shown closed, as the click will close it
        var closing = SnapOwnPoints && _points.Count >= 3 && _hover is Vec3 first && Vec3.Distance(first, _points[0]) < 1e-9;
        if (_hover is Vec3 h && _points.Count < _max && !closing) pts.Add(h);
        if (pts.Count > 1) Preview.Lines.Add((pts, (Closed || closing) && pts.Count > 2, PreviewColor));
        foreach (var p in _points) Preview.Points.Add((p, PreviewColor));
        // where the next point goes when it is taken off the cursor (a survey point, a vertex), or the cursor in red where none can go
        if (Constrain != null && _hover is Vec3 to && _cursor is Vec3 c && Vec3.Distance(to, c) > 1e-6) Preview.Points.Add((to, SnapColor));
        if (_hover == null && _hoverWhy != null && _cursor is Vec3 refused) Preview.Points.Add((refused, RefusedColor));
        if (_hover is Vec3 hv && Measure?.Invoke(_points, hv) is string text) Preview.Labels.Add((hv, text));
    }
}

/// <summary>Calls back for each clicked entity of a given type (single-hole tools).</summary>
public sealed class PickEntityTool<T> : Tool where T : Domain.Model.Entity
{
    private readonly string _name;
    private readonly string _hint;
    private readonly Action<T, ToolPointer> _onPick;

    public PickEntityTool(MainViewModel vm, string name, string hint, Action<T, ToolPointer> onPick) : base(vm)
    {
        _name = name; _hint = hint; _onPick = onPick;
    }

    public override string Name => _name;
    public override string Hint => _hint;
    public List<MenuEntry> ExtraMenu { get; } = new();

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left || p.Hit is not T t) return;
        try { _onPick(t, p); }
        catch (Exception ex) { Dialogs.ShowError(ex); }
    }

    public override void PointerMoved(ToolPointer p)
    {
        Preview.Labels.Clear();
        if (p.Hit is T t) Preview.Labels.Add((p.World, t is Domain.Model.Hole h ? h.Label : t.KindName));
    }

    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p) => ExtraMenu.Concat(base.ContextMenu(p));
}
