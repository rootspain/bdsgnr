using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.App.Tools;

/// <summary>
/// Click / box selection and drag-move of selected objects. What a press does is decided here, in the order of the gestures:
/// the circles of a front, the circle of a pattern, the grips of a line, the line of a row, the selection, the box; each does
/// its own work.
/// </summary>
public sealed class SelectTool : Tool
{
    private ToolPointer? _down;
    private bool _dragging;
    /// <summary>Circles of the selected two-segment fronts: turn and reshape.</summary>
    private readonly FrontLineHandles _front;
    /// <summary>Circles at the tips of the arrows of the patterns of the selected holes: turn the grid.</summary>
    private readonly PatternRotateHandle _pattern;
    private readonly VertexGrips _vertices;
    private readonly RowPick _rows;
    private readonly SelectionDrag _move;
    private readonly BoxSelect _box;
    /// <summary>The grips of a selected block: its size dragged by a corner or a face.</summary>
    private readonly BlockGrips _blocks;
    /// <summary>Gestures that take a press for themselves: nothing else happens with it.</summary>
    private readonly ISelectGesture[] _handles;
    /// <summary>The gestures that share a press: the tool decides between them on the drag and the release.</summary>
    private readonly ISelectGesture[] _shared;
    /// <summary>All the gestures in the order they are asked.</summary>
    private readonly ISelectGesture[] _gestures;

    public SelectTool(MainViewModel vm) : base(vm)
    {
        _front = new FrontLineHandles(vm, () => Preview.Moving == null);
        _pattern = new PatternRotateHandle(vm, () => Preview.Moving == null);
        _blocks = new BlockGrips(vm, () => Preview.Moving == null);
        _vertices = new VertexGrips(vm, () => Preview.Moving == null);
        _move = new SelectionDrag(vm);
        _rows = new RowPick(vm, _move);
        _box = new BoxSelect(vm);
        _handles = new ISelectGesture[] { _front, _blocks, _pattern };
        _shared = new ISelectGesture[] { _vertices, _rows, _move, _box };
        _gestures = _handles.Concat(_shared).ToArray();
    }

    public override string Name => "Select";
    public override string Hint => "Click to select, drag a box to select several (right to left = crossing). Drag a selected object or press the arrows (1 m a press) to move it on the ground, with Shift up or down (Z), with Ctrl in 0.1 m steps. A round grip of a selected line moves its vertex. A click with Shift adds, with Ctrl toggles. On a selected line Shift adds a vertex, Ctrl on a grip deletes it. On a selected timing front the accent circle at the vertex turns it, a circle at the base changes its angle and length. The circle at the tip of the arrow of a pattern with a selected hole turns its grid (Shift 5°, Ctrl 0.1° steps), its right-click menu turns by a quarter, by an angle or along a line. The round grips of a selected block change its size: a corner the length and width (with Shift its top or bottom), the middle of a face that side only.";

    /// <summary>Fine drag step, the same as an arrow press with Ctrl.</summary>
    public const double FineStep = SelectionDrag.FineStep;

    /// <inheritdoc cref="SelectionDrag.Steps"/>
    public static Vec3 Steps(Vec3 d, bool fine, bool vertical) => SelectionDrag.Steps(d, fine, vertical);

    /// <summary>How a click or a box changes the selection: Ctrl toggles, Shift adds, else it replaces.</summary>
    internal static SelectionMode Mode(ToolPointer p) =>
        p.Modifiers.HasFlag(KeyModifiers.Control) ? SelectionMode.Toggle : p.Modifiers.HasFlag(KeyModifiers.Shift) ? SelectionMode.Add : SelectionMode.Replace;

    public override void Activate()
    {
        base.Activate();
        _pattern.Attach();
        foreach (var g in _gestures)
            if (g.Overlay is { } o) View.Overlays.Add(o);
    }

    public override void Deactivate()
    {
        base.Deactivate();
        // no block stays lit by a cursor of a tool that is gone
        if (View.HotBlock != null) { View.HotBlock = null; View.Redraw(); }
        _pattern.Detach();
        foreach (var g in _gestures)
        {
            g.Cancel();
            if (g.Overlay is { } o) View.Overlays.Remove(o);
        }
    }

    /// <summary>The gesture that took the press for itself and is not done with it.</summary>
    private ISelectGesture? Held => _handles.FirstOrDefault(h => h.Active);

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left) return;
        // a circle of a selected front (turned or reshaped) or of a pattern (its grid turned): nothing else happens with this press
        if ((!p.Modifiers.HasFlag(KeyModifiers.Shift) && p.ClickCount < 2 && _front.TryPress(p)) || _blocks.TryPress(p) || _pattern.TryPress(p)) { _down = null; return; }
        _down = p;
        _dragging = false;
        // the selection before the row: a selected row hands its drag to the selection
        _move.TryPress(p);
        _vertices.TryPress(p);
        _rows.TryPress(p);
        _box.TryPress(p);
        // quick clicks that edit a line go on editing it, they are no double click
        if (p.ClickCount >= 2 && p.Hit != null && !_vertices.EditsLine)
        {
            Vm.Doc!.Select(new[] { p.Hit.Id });
            ShowProperties();
            Vm.EditSelected();
            _down = null;
        }
    }

    public override void PointerMoved(ToolPointer p)
    {
        if (Held is { } held) { held.Drag(p, Preview); return; }
        Preview.Labels.Clear();
        if (_down == null)
        {
            _vertices.Hover(p);
            _front.Hover(p);
            _blocks.Hover(p);
            _pattern.Hover(p);
            // a block under the cursor shows all its edges, and is named by its layer
            var hot = p.Hit is BlockEntity ? p.Hit.Id : (Guid?)null;
            if (View.HotBlock != hot) { View.HotBlock = hot; View.Redraw(); }
            if (p.Hit != null && !_pattern.Hot) Preview.Labels.Add((p.World, p.Hit is Hole h ? $"{h.Label}  {h.EffectiveLength:0.0} m"
                : p.Hit is BlockEntity b ? Vm.Project?.FindLayer(b.LayerId)?.Name ?? Loc.T(b.KindName) : Loc.T(p.Hit.KindName)));
            return;
        }
        if (Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) > 4) _dragging = true;
        if (!_dragging) return;
        // a grip moves its vertex, a selected object (or row) the selection, anything else draws the box
        if (_vertices.Active) _vertices.Drag(p, Preview);
        else if (_move.Active) _move.Drag(p, Preview);
        else _box.Drag(p, Preview);
    }

    public override void PointerReleased(ToolPointer p)
    {
        if (Held is { } held) { held.Release(p); Preview.Clear(); return; }
        if (_down == null) return;
        Preview.Clear();
        Finish(p);
        _down = null;
        foreach (var g in _shared) g.Cancel();
    }

    /// <summary>What the press let go does: a click edits the line or selects, a drag moves or selects in the box.</summary>
    private void Finish(ToolPointer p)
    {
        if (!_dragging && _vertices.Click()) return;
        if (_dragging && _vertices.Active) { _vertices.Release(p); return; }
        if (_dragging && _move.Active) { _move.Release(p); return; }
        // a click on the line of a row selects its holes
        if (!_dragging && _rows.Click(Mode(p))) { ShowProperties(); return; }
        if (_dragging)
        {
            _box.Release(p);
            ShowProperties();
            return;
        }
        var mode = Mode(p);
        Vm.Doc!.Select(p.Hit != null ? new[] { p.Hit.Id } : Array.Empty<Guid>(), p.Hit == null && mode != SelectionMode.Replace ? SelectionMode.Add : mode);
        ShowProperties();
    }

    /// <summary>Selecting something on the plan shows its properties in the right panel.</summary>
    private void ShowProperties()
    {
        if (Vm.HasSelection) Vm.Window.ShowPropertiesTab();
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        // Esc during a drag cancels it at once: the objects snap back and the release does nothing.
        if (key == Key.Escape && Held is { } held)
        {
            held.Cancel();
            Preview.Clear();
            return true;
        }
        if (key == Key.Escape && _down != null)
        {
            _down = null;
            _dragging = false;
            foreach (var g in _shared) g.Cancel();
            Preview.Clear();
            return true;
        }
        // Otherwise Esc clears the selection; with nothing selected it is left to the window (closes overlays, etc.).
        if (key == Key.Escape && Vm.HasSelection) { Vm.Doc?.ClearSelection(); return true; }
        return false;
    }

    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p)
    {
        foreach (var g in _gestures)
            if (g.ContextMenu(p) is { } menu) return menu;
        // The menu follows the object under the cursor: right-clicking an unselected object selects it first.
        if (p.Hit != null && !Vm.Doc!.Selection.Contains(p.Hit.Id)) Vm.Doc.Select(new[] { p.Hit.Id });
        return Vm.ObjectContextMenu();
    }
}

/// <summary>Drag a rectangle to zoom into it.</summary>
public sealed class ZoomBoxTool : Tool
{
    private ToolPointer? _down;

    public ZoomBoxTool(MainViewModel vm) : base(vm) { }

    public override string Name => "Zoom";
    public override string Hint => "Drag a rectangle to zoom in. Click to zoom in at a point.";

    public override void PointerPressed(ToolPointer p) => _down = p.Left ? p : null;

    public override void PointerMoved(ToolPointer p)
    {
        if (_down != null) Preview.ScreenRect = ((float)_down.X, (float)_down.Y, (float)p.X, (float)p.Y);
    }

    public override void PointerReleased(ToolPointer p)
    {
        if (_down == null) return;
        Preview.ScreenRect = null;
        Vm.PushZoomHistory();
        var w = Math.Abs(p.X - _down.X);
        var h = Math.Abs(p.Y - _down.Y);
        if (w < 5 || h < 5) View.Camera.ZoomAt(p.X, p.Y, 2);
        else
        {
            var cx = (p.X + _down.X) / 2;
            var cy = (p.Y + _down.Y) / 2;
            var target = View.Camera.ToWorld(cx, cy);
            View.Camera.Target = target;
            View.Camera.Scale *= Math.Min(View.Camera.Width / w, View.Camera.Height / h);
        }
        _down = null;
        View.Redraw();
    }
}

/// <summary>Left-drag pans the view.</summary>
public sealed class PanTool : Tool
{
    private ToolPointer? _last;

    public PanTool(MainViewModel vm) : base(vm) { }

    public override string Name => "Pan";
    public override string Hint => "Drag to pan the view.";
    public override void PointerPressed(ToolPointer p) => _last = p.Left ? p : null;

    public override void PointerMoved(ToolPointer p)
    {
        if (_last == null) return;
        View.Camera.Pan(p.X - _last.X, p.Y - _last.Y);
        View.CameraMoving();
        _last = p;
    }

    public override void PointerReleased(ToolPointer p) => _last = null;
}

/// <summary>Left-drag orbits (3D) or rotates north (plan).</summary>
public sealed class RotateTool : Tool
{
    private readonly bool _northOnly;
    private ToolPointer? _last;

    public RotateTool(MainViewModel vm, bool northOnly) : base(vm) => _northOnly = northOnly;

    public override string Name => _northOnly ? "North" : "Rotate";
    public override string Hint => _northOnly ? "Drag left/right to rotate the plan about the vertical axis." : "Drag to orbit the 3D view.";

    public override void PointerPressed(ToolPointer p)
    {
        _last = p.Left ? p : null;
        if (_last != null && !_northOnly) View.SetOrbitPivot();
    }

    public override void PointerMoved(ToolPointer p)
    {
        if (_last == null) return;
        View.Camera.Orbit((p.X - _last.X) * 0.4, _northOnly ? 0 : (p.Y - _last.Y) * 0.3);
        View.CameraMoving();
        _last = p;
    }

    public override void PointerReleased(ToolPointer p) => _last = null;
}

/// <summary>Connects holes with surface delays: click the "from" hole (or initiation point) and then the "to" hole; dragging also works.</summary>
public sealed class TieTool : Tool
{
    private static readonly SKColor CaptureColour = new(0x809AA3AD);

    private Entity? _from;
    /// <summary>Where the line being drawn ends: the hole under the cursor, or the cursor.</summary>
    private Vec3? _end;

    public TieTool(MainViewModel vm, Entity? start = null) : base(vm) { _from = start; }

    public override void Activate()
    {
        base.Activate();
        View.Rendering += UpdateCapture;
    }

    public override void Deactivate()
    {
        View.Rendering -= UpdateCapture;
        base.Deactivate();
    }

    public override string Name => "Tie";
    public override string Hint => "Click a hole, then the hole to tie it to (or drag from one to the other); holes on the way get a tie each. The tie finishes the entry; with Shift held the entry goes on from that hole. Tied holes take more ties; each tie points away from the initiation point, whichever way it was drawn. The connector is chosen in the tie palette.";

    /// <summary>
    /// Hole or initiation point under the cursor. Other objects are skipped: an initiation point is often placed on a
    /// survey point, which would otherwise be picked instead of it.
    /// </summary>
    private Entity? Target(ToolPointer p) => View.HitTest(p.X, p.Y, accept: e => e is Hole or InitiationPoint);

    /// <summary>
    /// Where a tie starts: a click anywhere on the star of an initiation point, its see-through middle included, starts
    /// at the hole the point stands on (the ties go on from that hole), at the point itself when it stands on none.
    /// </summary>
    private Entity? Start(ToolPointer p)
    {
        if (View.HitTest(p.X, p.Y, accept: e => e is InitiationPoint) is not InitiationPoint ip) return Target(p);
        return ip.HoleId is Guid hid && Vm.Project!.FindEntity(hid) is Hole h ? h : ip;
    }

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left) return;
        if (_from == null) { _from = Start(p); return; }
        if (Target(p) is not { } hit) return;
        // the last hole again finishes the entry; the next click starts a new chain
        if (hit.Id == _from.Id) { Finish(); return; }
        Connect(_from, hit);
        GoOnOrFinish(hit, p);
    }

    /// <summary>After a tie: with Shift held or Continue after creating on the entry goes on from its hole, otherwise it is finished.</summary>
    private void GoOnOrFinish(Entity hit, ToolPointer p)
    {
        if (p.Modifiers.HasFlag(KeyModifiers.Shift) || Vm.TieContinue) _from = hit;
        else Finish();
    }

    private void Finish()
    {
        _from = null;
        _end = null;
        Preview.Clear();
        Vm.SetStatus(Loc.T("Tie entry finished"));
    }

    /// <summary>
    /// Ties from <paramref name="from"/> to <paramref name="to"/> through every hole on the way: within the pick radius
    /// (pixels, status bar) of the line on the screen, in their order along it; one tie each, one undo step. The new ties
    /// are then pointed away from the initiation point, whichever way the line was drawn (<see cref="Timing.OrientTiesFromInitiation"/>),
    /// and so are the ties they join to it: those of an entry so far and others no point reached before.
    /// </summary>
    private void Connect(Entity from, Entity to)
    {
        var chain = new List<Guid> { from.Id };
        if (Vm.TieChain) chain.AddRange(HolesOnTheWay(from, to).Select(h => h.Id));
        chain.Add(to.Id);
        // a user-defined delay goes on the tie itself, without a product
        var delay = Vm.ActiveTieDelayMs;
        var connector = delay == null ? Vm.ActiveConnector : null;
        Vm.Change(chain.Count > 2 ? "Add ties" : "Add tie", pr =>
        {
            var reached = Timing.ReachedTies(pr);
            for (int i = 1; i < chain.Count; i++) Timing.Connect(pr, chain[i - 1], chain[i], connector, delay);
            Timing.OrientTiesFromInitiation(pr, pr.Ties.Select(t => t.Id).Where(id => !reached.Contains(id)).ToHashSet());
        });
        if (chain.Count > 2) Vm.SetStatus(Loc.F("{0} ties added through the holes on the line", chain.Count - 1));
    }

    /// <summary>Shown holes whose collar lies within the pick radius of the screen line between the two, ordered along it.</summary>
    private List<Hole> HolesOnTheWay(Entity from, Entity to)
    {
        var p = Vm.Project!;
        if (Timing.PositionOf(p, from.Id) is not Vec3 a || Timing.PositionOf(p, to.Id) is not Vec3 b) return new();
        var cam = View.Camera;
        var (ax, ay) = cam.ToScreen(a);
        var (bx, by) = cam.ToScreen(b);
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        if (len2 < 1) return new();
        var visible = p.Layers.Where(l => l.Visible).Select(l => l.Id).ToHashSet();
        var found = new List<(double T, Hole H)>();
        foreach (var h in p.Holes)
        {
            if (h.Id == from.Id || h.Id == to.Id || !visible.Contains(h.LayerId) || Vm.HiddenEntities.Contains(h.Id) || !BlastDesigner.Rendering.PlanRenderer.HoleVisible(p.View, h)) continue;
            var (x, y) = cam.ToScreen(h.Collar);
            var t = ((x - ax) * dx + (y - ay) * dy) / len2;
            if (t <= 0 || t >= 1) continue;
            if (Math.Abs((x - ax) * dy - (y - ay) * dx) / Math.Sqrt(len2) <= View.TargetRadius) found.Add((t, h));
        }
        return found.OrderBy(f => f.T).Select(f => f.H).ToList();
    }

    /// <summary>
    /// Grey lines on both sides of the line being drawn, the pick radius away from it on the screen: the holes between
    /// them get a tie (<see cref="HolesOnTheWay"/>). The radius is in pixels, so they follow the zoom at each frame.
    /// </summary>
    private void UpdateCapture()
    {
        Preview.Guides.Clear();
        if (!Vm.TieChain || !Vm.TieCaptureShown || _from == null || _end is not Vec3 b || Timing.PositionOf(Vm.Project!, _from.Id) is not Vec3 a) return;
        var cam = View.Camera;
        var (ax, ay) = cam.ToScreen(a);
        var (bx, by) = cam.ToScreen(b);
        double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;
        double nx = -dy / len * View.TargetRadius, ny = dx / len * View.TargetRadius;
        foreach (var side in new[] { 1, -1 })
            Preview.Guides.Add((new[] { cam.ToWorld(ax + side * nx, ay + side * ny, a.Z), cam.ToWorld(bx + side * nx, by + side * ny, b.Z) }, CaptureColour));
    }

    public override void PointerReleased(ToolPointer p)
    {
        // Drag release on another hole creates the tie too, and finishes or goes on like a click.
        if (_from != null && Target(p) is Hole h && h.Id != _from.Id && Preview.Lines.Count > 0)
        {
            Connect(_from, h);
            GoOnOrFinish(h, p);
        }
    }

    public override void PointerMoved(ToolPointer p)
    {
        Preview.Lines.Clear();
        if (_from == null) return;
        var a = Timing.PositionOf(Vm.Project!, _from.Id) ?? p.World;
        var colour = new SKColor(Vm.ActiveConnector?.Color ?? 0xFFDC2626);
        Preview.Markers.Clear();
        if (Target(p) is Hole h && h.Id != _from.Id)
        {
            // through the holes on the way, which get a tie each
            var way = Vm.TieChain ? HolesOnTheWay(_from, h) : new List<Hole>();
            Preview.Lines.Add((new[] { a }.Concat(way.Select(x => x.Collar)).Append(h.Collar).ToList(), false, colour));
            foreach (var x in way) Preview.Markers.Add((x.Collar, colour));
            _end = h.Collar;
        }
        else
        {
            Preview.Lines.Add((new[] { a, p.World }, false, colour));
            _end = p.World;
        }
        UpdateCapture();
        Preview.Labels.Clear();
        Preview.Labels.Add((p.World, Vm.ActiveTieDelayMs is double ms ? Loc.F("{0} ms", ms.ToString("0.#")) : Vm.ActiveConnector?.Name ?? "0 ms"));
    }

    protected override bool HasPendingInput => _from != null;

    public override void Cancel()
    {
        _from = null;
        _end = null;
        base.Cancel();
    }

    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p)
    {
        yield return MenuEntry.Item("Initiation catalogue", Vm.ShowTiePalette);
        yield return MenuEntry.Item("Automatic tie-up", Vm.AutoTieUp);
        yield return MenuEntry.Item("Connect multiple holes", Vm.ConnectSelectedInOrder);
        yield return MenuEntry.Item("Flip selected ties", Vm.FlipTies);
        foreach (var e in base.ContextMenu(p)) yield return e;
    }
}

/// <summary>Electronic timing: clicking holes assigns increasing times (Row set) or one hole at a time (Set).</summary>
public sealed class TimeSetTool : Tool
{
    private readonly bool _sequence;
    private double _next;

    public TimeSetTool(MainViewModel vm, bool sequence) : base(vm)
    {
        _sequence = sequence;
        _next = vm.Project?.Electronic.BaseTimeMs ?? 0;
    }

    public override string Name => _sequence ? "Row set" : "Set";
    public override string Hint => _sequence
        ? "Click holes in firing order; each gets the previous time plus the hole interval. Ctrl+click adds the row interval."
        : "Click a hole to type its programmed time.";

    public override async void PointerPressed(ToolPointer p)
    {
        if (!p.Left || p.Hit is not Hole h) return;
        if (_sequence)
        {
            var t = _next;
            Vm.Change("Set hole time", p => Timing.SetTime(h, t, true, p.Electronic.AllowSubMs));
            var e = Vm.Project!.Electronic;
            _next = t + (p.Modifiers.HasFlag(KeyModifiers.Control) ? e.RowIntervalMs : e.HoleIntervalMs);
            Vm.SetStatus(Loc.F("Next time: {0} ms", _next));
        }
        else
        {
            var v = await Dialogs.PromptNumber(Loc.T("Hole time"), Loc.F("Programmed time for hole {0} (ms)", h.Label), h.ElectronicTimeMs ?? h.FiringTimeMs ?? 0);
            if (v is double t) Vm.Change("Set hole time", p => Timing.SetTime(h, t, true, p.Electronic.AllowSubMs));
        }
    }

    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p)
    {
        yield return MenuEntry.Item("Preferences..", Vm.EditElectronicSettings);
        yield return MenuEntry.Item("Restart at…", async () =>
        {
            if (await Dialogs.PromptNumber(Loc.T("Start time"), Loc.T("Next hole time (ms)"), _next) is double v) _next = v;
        });
        foreach (var e in base.ContextMenu(p)) yield return e;
    }
}
