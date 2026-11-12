using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Rendering;

namespace BlastDesigner.App.Tools;

/// <summary>
/// Drag-move of the selected objects: on the ground, with Shift held up or down (Z), with Ctrl in whole tenths of a metre.
/// The selection follows the cursor on the screen; the move is applied, with undo, on release.
/// </summary>
public sealed class SelectionDrag : ISelectGesture
{
    /// <summary>Fine drag step, the same as an arrow press with Ctrl.</summary>
    public const double FineStep = 0.1;

    private readonly MainViewModel _vm;
    private ToolPointer? _down;
    private bool _moving;
    /// <summary>The press was on the line of a row: the row is held where it was pressed, not by a vertex.</summary>
    private bool _byRow;
    /// <summary>Offset of the last drag preview.</summary>
    private Vec3? _shown;
    /// <summary>
    /// Whether the move goes up or down (Shift held) and where it went over to that: the offset made so far is kept and
    /// the cursor is read from the pointer of the switch, so pressing or letting go of Shift does not undo the way made.
    /// </summary>
    private (bool Vertical, Vec3 Base, ToolPointer From) _way;

    public SelectionDrag(MainViewModel vm) => _vm = vm;

    private Viewport View => _vm.View;

    public IOverlay? Overlay => null;

    /// <summary>A drag of the press moves the selection.</summary>
    public bool Active => _moving;

    public void Hover(ToolPointer p) { }

    /// <summary>
    /// A selected object is dragged with any of Shift (up or down) and Ctrl (0.1 m steps); a click with them on it still only
    /// adds to or toggles the selection. True when the press is on a selected object.
    /// </summary>
    public bool TryPress(ToolPointer p)
    {
        _down = p;
        _shown = null;
        _way = (p.Modifiers.HasFlag(KeyModifiers.Shift), default, p);
        _byRow = false;
        _moving = p.Hit != null && _vm.Doc!.Selection.Contains(p.Hit.Id);
        return _moving;
    }

    /// <summary>The press is on the line of a row: held there, and moved when the row is selected.</summary>
    public void PressRow(bool selected)
    {
        _byRow = true;
        if (selected) _moving = true;
    }

    public void Drag(ToolPointer p, PreviewOverlay preview)
    {
        // The selection itself follows the cursor; the move is applied (with undo) on release.
        var (delta, handle, snap) = MoveDelta(p);
        _shown = delta;
        preview.Snap = snap;
        // a bound initiation point and its hole move together, on screen as on release
        preview.Moving ??= InitiationLinks.WithLinked(_vm.Project!, _vm.Doc!.Selection);
        _vm.RaiseDragPreview();
        preview.MoveOffset = delta;
        preview.Labels.Add((handle + delta, _way.Vertical ? $"Δ Z {delta.Z:+0.00;-0.00} m" : $"Δ {delta.Length2D:0.00} m"));
    }

    public void Release(ToolPointer p)
    {
        // Apply exactly what was on screen, not a fresh reading of the release point.
        var delta = _shown ?? MoveDelta(p).Delta;
        _shown = null;
        _moving = false;
        _vm.MoveSelection(delta);
    }

    public void Cancel()
    {
        _moving = false;
        _shown = null;
        _down = null;
    }

    /// <summary>
    /// Move of a drag: horizontal, or with Shift held vertical (<see cref="VerticalDelta"/>). The grabbed object is held by its handle — a hole by its collar, anything else by the
    /// vertex nearest to the press — and the cursor is read on the horizontal plane through that handle, so in 3D the
    /// handle stays under the cursor whatever the elevation.
    /// </summary>
    private (Vec3 Delta, Vec3 Handle, Vec3? Snap) MoveDelta(ToolPointer p)
    {
        var down = _down!;
        var cam = View.Camera;
        var handle = _byRow ? down.World : down.Hit switch
        {
            Hole h => h.Collar,
            { } e => e.GetVertices().DefaultIfEmpty(down.World).MinBy(v =>
            {
                var (x, y) = cam.ToScreen(v);
                return (x - down.X) * (x - down.X) + (y - down.Y) * (y - down.Y);
            }),
            _ => down.World
        };
        var vertical = p.Modifiers.HasFlag(KeyModifiers.Shift);
        // with Ctrl the way is whole tenths of a metre, as the arrows go with it, and nothing snaps
        var fine = p.Modifiers.HasFlag(KeyModifiers.Control);
        if (vertical != _way.Vertical) _way = (vertical, _shown ?? default, p);
        if (vertical) return (Steps(_way.Base + VerticalDelta(p), fine, true), handle, null);
        // Snapping takes the object under the cursor other than the dragged ones, and puts the handle right on it, with
        // its elevation, so that in 3D the handle stays under the cursor. Without a snap the move is horizontal, from
        // where the way so far put the handle.
        var snap = fine ? null : View.SnapForDrag(p.X, p.Y, _vm.Doc!.Selection);
        var from = handle + _way.Base;
        var target = snap ?? from + (cam.ToWorld(p.X, p.Y, from.Z) - cam.ToWorld(_way.From.X, _way.From.Y, from.Z)).WithZ(0);
        return (Steps(target - handle, fine && snap == null, false), handle, snap);
    }

    /// <summary>
    /// The way rounded to <see cref="FineStep"/> when <paramref name="fine"/>: along Z when <paramref name="vertical"/>,
    /// else along X and Y; the other axes keep what the way made before Shift was pressed or let go.
    /// </summary>
    public static Vec3 Steps(Vec3 d, bool fine, bool vertical)
    {
        static double R(double v) => Math.Round(v / FineStep) * FineStep;
        if (!fine) return d;
        return vertical ? new Vec3(d.X, d.Y, R(d.Z)) : new Vec3(R(d.X), R(d.Y), d.Z);
    }

    /// <summary>
    /// Shift drag: the selection goes up or down by the cursor's way up or down the screen. In a tilted view the vertical
    /// is foreshortened, so the way is read along it; in the plan, where the vertical points at the viewer, a pixel up is
    /// a pixel's worth of metres up.
    /// </summary>
    private Vec3 VerticalDelta(ToolPointer p)
    {
        var cam = View.Camera;
        var upward = cam.IsPlan ? 1 : Math.Max(cam.Up.Z, 0.25);
        return new Vec3(0, 0, (_way.From.Y - p.Y) / (cam.Scale * upward));
    }
}
