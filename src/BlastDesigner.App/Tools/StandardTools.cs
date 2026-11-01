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
            Preview.Moving ??= Vm.Doc!.Selection.ToList();
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
        var handle = down.Hit switch
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
    private Entity? _from;

    public TieTool(MainViewModel vm) : base(vm) { }

    public override string Name => "Tie";
    public override string Hint => "Click the hole the signal comes from, then the hole it goes to. Shift keeps chaining from the last hole. The connector is chosen in the tie palette.";

    /// <summary>
    /// Hole or initiation point under the cursor. Other objects are skipped: an initiation point is often placed on a
    /// survey point, which would otherwise be picked instead of it.
    /// </summary>
    private Entity? Target(ToolPointer p) => View.HitTest(p.X, p.Y, accept: e => e is Hole or InitiationPoint);

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left) return;
        if (Target(p) is not { } hit) return;
        if (_from == null) { _from = hit; return; }
        if (hit.Id == _from.Id) return;
        var from = _from;
        var to = hit;
        var connector = Vm.ActiveConnector;
        Vm.Change("Add tie", pr => Timing.Connect(pr, from.Id, to.Id, connector));
        _from = to;
    }

    public override void PointerReleased(ToolPointer p)
    {
        // Drag release on another hole creates the tie too.
        if (_from != null && Target(p) is Hole h && h.Id != _from.Id && !p.Modifiers.HasFlag(KeyModifiers.Shift) && Preview.Lines.Count > 0)
        {
            var from = _from;
            Vm.Change("Add tie", pr => Timing.Connect(pr, from.Id, h.Id, Vm.ActiveConnector));
            _from = h;
        }
    }

    public override void PointerMoved(ToolPointer p)
    {
        Preview.Lines.Clear();
        if (_from == null) return;
        var a = Timing.PositionOf(Vm.Project!, _from.Id) ?? p.World;
        Preview.Lines.Add((new[] { a, Target(p) is Hole h ? h.Collar : p.World }, false, new SKColor(Vm.ActiveConnector?.Color ?? 0xFFDC2626)));
        Preview.Labels.Clear();
        Preview.Labels.Add((p.World, Vm.ActiveConnector?.Name ?? "0 ms"));
    }

    protected override bool HasPendingInput => _from != null;

    public override void Cancel()
    {
        _from = null;
        base.Cancel();
    }

    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p)
    {
        yield return MenuEntry.Item("Show tie palette", Vm.ShowTiePalette);
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
