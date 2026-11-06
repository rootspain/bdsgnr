using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using SkiaSharp;

namespace BlastDesigner.App.Tools;

/// <summary>Click / box selection and drag-move of selected objects.</summary>
public sealed class SelectTool : Tool
{
    private ToolPointer? _down;
    private bool _dragging;
    private bool _moving;
    /// <summary>Row whose line was pressed (between its holes): a drag moves it when selected, a click selects it.</summary>
    private HoleRow? _row;
    /// <summary>Offset of the last drag preview.</summary>
    private Vec3? _shown;

    public SelectTool(MainViewModel vm) : base(vm) { }

    public override string Name => "Select";
    public override string Hint => "Click to select, drag a box to select several (right to left = crossing). Drag a selected object to move it. Shift adds, Ctrl toggles.";

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left) return;
        _down = p;
        _shown = null;
        _dragging = false;
        _moving = p.Hit != null && Vm.Doc!.Selection.Contains(p.Hit.Id) && !p.Modifiers.HasFlag(KeyModifiers.Shift) && !p.Modifiers.HasFlag(KeyModifiers.Control);
        // the line of a row, with nothing else under the cursor than a surface (picked by area) or a tie along the
        // row: a selected row is dragged by it, an unselected one is selected by a click on it (a tie keeps its click)
        var free = !p.Modifiers.HasFlag(KeyModifiers.Shift) && !p.Modifiers.HasFlag(KeyModifiers.Control);
        _row = p.Hit is null or SurfaceEntity || (p.Hit is TieEntity && !Vm.Doc!.Selection.Contains(p.Hit.Id)) ? RowAt(p.X, p.Y) : null;
        if (_row != null)
        {
            var selected = _row.HoleIds.All(Vm.Doc!.Selection.Contains);
            if (selected && free) _moving = true;
            else if (p.Hit is TieEntity) _row = null;
        }
        if (p.ClickCount >= 2 && p.Hit != null)
        {
            Vm.Doc!.Select(new[] { p.Hit.Id });
            ShowProperties();
            Vm.EditSelected();
            _down = null;
        }
    }

    public override void PointerMoved(ToolPointer p)
    {
        Preview.Labels.Clear();
        if (_down == null)
        {
            if (p.Hit != null) Preview.Labels.Add((p.World, p.Hit is Hole h ? $"{h.Label}  {h.EffectiveLength:0.0} m" : Loc.T(p.Hit.KindName)));
            return;
        }
        if (Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) > 4) _dragging = true;
        if (!_dragging) return;
        if (_moving)
        {
            // The selection itself follows the cursor; the move is applied (with undo) on release.
            var (delta, handle, snap) = MoveDelta(p);
            _shown = delta;
            Preview.Snap = snap;
            // a bound initiation point and its hole move together, on screen as on release
            Preview.Moving ??= InitiationLinks.WithLinked(Vm.Project!, Vm.Doc!.Selection);
            Vm.RaiseDragPreview();
            Preview.MoveOffset = delta;
            Preview.Labels.Add((handle + delta, $"Δ {delta.Length2D:0.00} m"));
        }
        else Preview.ScreenRect = ((float)_down.X, (float)_down.Y, (float)p.X, (float)p.Y);
    }

    public override void PointerReleased(ToolPointer p)
    {
        if (_down == null) return;
        var down = _down;
        Preview.Clear();
        var mode = p.Modifiers.HasFlag(KeyModifiers.Control) ? SelectionMode.Toggle : p.Modifiers.HasFlag(KeyModifiers.Shift) ? SelectionMode.Add : SelectionMode.Replace;
        if (_dragging && _moving)
        {
            // Apply exactly what was on screen, not a fresh reading of the release point.
            var delta = _shown ?? MoveDelta(p).Delta;
            _down = null;
            _shown = null;
            Vm.MoveSelection(delta);
            return;
        }
        _down = null;
        // a click on the line of a row selects its holes
        if (!_dragging && _row != null)
        {
            Vm.Doc!.Select(_row.HoleIds, mode);
            _row = null;
            ShowProperties();
            return;
        }
        if (_dragging)
        {
            var crossing = p.X < down.X;
            Vm.Doc!.Select(View.InRect(down.X, down.Y, p.X, p.Y, crossing).Select(e => e.Id), mode);
            ShowProperties();
            return;
        }
        Vm.Doc!.Select(p.Hit != null ? new[] { p.Hit.Id } : Array.Empty<Guid>(), p.Hit == null && mode != SelectionMode.Replace ? SelectionMode.Add : mode);
        ShowProperties();
    }

    /// <summary>
    /// Row whose line (through its holes, in order) passes within the pick radius of the screen point, when rows are
    /// shown; the nearest one.
    /// </summary>
    private HoleRow? RowAt(double sx, double sy)
    {
        var p = Vm.Project!;
        if (!p.View.ShowRows) return null;
        var cam = View.Camera;
        HoleRow? best = null;
        double bestD = View.TargetRadius;
        foreach (var row in p.Rows)
        {
            var pts = row.HoleIds.Select(id => p.FindEntity(id)).OfType<Hole>().Select(h => cam.ToScreen(h.Collar)).ToList();
            for (int i = 1; i < pts.Count; i++)
            {
                double ax = pts[i - 1].X, ay = pts[i - 1].Y, dx = pts[i].X - ax, dy = pts[i].Y - ay, l2 = dx * dx + dy * dy;
                var t = l2 > 0 ? Math.Clamp(((sx - ax) * dx + (sy - ay) * dy) / l2, 0, 1) : 0;
                var d = Math.Sqrt(Math.Pow(ax + t * dx - sx, 2) + Math.Pow(ay + t * dy - sy, 2));
                if (d < bestD) { bestD = d; best = row; }
            }
        }
        return best;
    }

    /// <summary>Selecting something on the plan shows its properties in the right panel.</summary>
    private void ShowProperties()
    {
        if (Vm.HasSelection) Vm.Window.ShowPropertiesTab();
    }

    /// <summary>
    /// Horizontal move of a drag. The grabbed object is held by its handle — a hole by its collar, anything else by the
    /// vertex nearest to the press — and the cursor is read on the horizontal plane through that handle, so in 3D the
    /// handle stays under the cursor whatever the elevation.
    /// </summary>
    private (Vec3 Delta, Vec3 Handle, Vec3? Snap) MoveDelta(ToolPointer p)
    {
        var down = _down!;
        var cam = View.Camera;
        var handle = _row != null ? down.World : down.Hit switch
        {
            Hole h => h.Collar,
            { } e => e.GetVertices().DefaultIfEmpty(down.World).MinBy(v =>
            {
                var (x, y) = cam.ToScreen(v);
                return (x - down.X) * (x - down.X) + (y - down.Y) * (y - down.Y);
            }),
            _ => down.World
        };
        // Snapping takes the object under the cursor other than the dragged ones, and puts the handle right on it, with
        // its elevation, so that in 3D the handle stays under the cursor. Without a snap the move is horizontal.
        var snap = View.SnapForDrag(p.X, p.Y, Vm.Doc!.Selection);
        var target = snap ?? handle + (cam.ToWorld(p.X, p.Y, handle.Z) - cam.ToWorld(down.X, down.Y, handle.Z)).WithZ(0);
        return (target - handle, handle, snap);
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        // Esc during a drag cancels it at once: the objects snap back and the release does nothing.
        if (key == Key.Escape && _down != null)
        {
            _down = null;
            _dragging = _moving = false;
            Preview.Clear();
            return true;
        }
        // Otherwise Esc clears the selection; with nothing selected it is left to the window (closes overlays, etc.).
        if (key == Key.Escape && Vm.HasSelection) { Vm.Doc?.ClearSelection(); return true; }
        return false;
    }

    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p)
    {
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
            Vm.Change("Set hole time", _ => Timing.SetTime(h, t));
            var e = Vm.Project!.Electronic;
            _next = t + (p.Modifiers.HasFlag(KeyModifiers.Control) ? e.RowIntervalMs : e.HoleIntervalMs);
            Vm.SetStatus(Loc.F("Next time: {0} ms", _next));
        }
        else
        {
            var v = await Dialogs.PromptNumber(Loc.T("Hole time"), Loc.F("Programmed time for hole {0} (ms)", h.Label), h.ElectronicTimeMs ?? h.FiringTimeMs ?? 0);
            if (v is double t) Vm.Change("Set hole time", _ => Timing.SetTime(h, t));
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
