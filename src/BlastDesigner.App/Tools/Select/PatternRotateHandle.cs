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
/// The circle at the tip of the burden arrow of each pattern with a selected hole, on the plan and in 3D: a drag turns the grid about its
/// centre with the preview of the rotate tool (1° steps, Shift 5°, Ctrl 0.1°) and lays it out on release in one undo step; Esc
/// drops the turn. A right click on it turns by a quarter, by a typed angle or along a line. The circle is green when the grid
/// can be turned, red when it cannot, and the cursor on a red one says why.
/// </summary>
public sealed class PatternRotateHandle : ISelectGesture
{
    /// <summary>Screen distance a press has to travel before it is a drag, px.</summary>
    private const double DragPx = 4;

    private readonly MainViewModel _vm;
    private readonly Func<bool> _visible;
    private readonly PatternTurnDrag _turn;
    private ToolPointer? _down;
    private Guid? _pressed;
    private bool _dragging;
    /// <summary>A turn is being laid out (it may ask first): no new press until it is done.</summary>
    private bool _busy;
    /// <summary>Why each pattern cannot be laid out again, worked out once for the project as it is.</summary>
    private readonly Dictionary<Guid, PatternRefusal?> _refusals = new();
    private BlastProject? _refusalsOf;

    public PatternRotateHandle(MainViewModel vm, Func<bool> visible)
    {
        _vm = vm;
        _visible = visible;
        _turn = new PatternTurnDrag(vm);
        Overlay = new PatternRotateHandleOverlay(Patterns, p => Refusal(p) != null,
            () => (new SKColor(vm.Settings.PatternTurnColor), new SKColor(vm.Settings.PatternTurnRefusedColor)));
    }

    public PatternRotateHandleOverlay Overlay { get; }
    IOverlay? ISelectGesture.Overlay => Overlay;

    /// <summary>A circle was pressed and its turn is not over yet.</summary>
    public bool Active => _pressed != null;

    /// <summary>The cursor is on a circle.</summary>
    public bool Hot => Overlay.Hot != null;

    /// <summary>Starts and ends listening to changes of the project, which may change the refusals.</summary>
    public void Attach() => _vm.ModelRefreshed += Forget;
    public void Detach() => _vm.ModelRefreshed -= Forget;

    private void Forget() => _refusals.Clear();

    /// <summary>
    /// The patterns with a circle: those whose grid is drawn, on the plan and in 3D, while nothing is being moved. A view from
    /// the side (a section) sees the ground edge on, where a turn about the vertical cannot be dragged: no circle there.
    /// </summary>
    public IReadOnlyList<HolePattern> Patterns() =>
        _visible() && SeesTheGround(_vm.View.Camera) ? PatternOverlay.Shown(_vm.Doc) : new List<HolePattern>();

    /// <summary>The view looks down on the ground enough to turn on it: the plan, or a 3D view not from the side.</summary>
    public static bool SeesTheGround(Camera cam) => cam.IsPlan || Math.Abs(cam.Up.Z) < 0.97;

    /// <summary>
    /// Where the cursor is on the horizontal plane through the centre of the pattern: the turn is read there, so in 3D it
    /// follows the cursor about the centre whatever is under it.
    /// </summary>
    private Vec3 OnGround(HolePattern pat, double sx, double sy) => _vm.View.Camera.ToWorld(sx, sy, pat.Center.Z);

    /// <summary>Why a pattern cannot be laid out again; null when it can.</summary>
    public PatternRefusal? Refusal(HolePattern pat)
    {
        var project = _vm.Project;
        if (project == null) return null;
        if (!ReferenceEquals(project, _refusalsOf)) { _refusals.Clear(); _refusalsOf = project; }
        if (_refusals.TryGetValue(pat.Id, out var known)) return known;
        // the layer new holes go to, without making it
        var layer = project.Layers.FirstOrDefault(l => string.Equals(l.Name, StandardLayers.Holes, StringComparison.OrdinalIgnoreCase));
        return _refusals[pat.Id] = HoleDesign.RebuildRefusal(project, pat, new PatternRebuildOptions { Layer = layer });
    }

    /// <summary>The circle at a screen point: the nearest one within reach.</summary>
    public HolePattern? HandleAt(double sx, double sy)
    {
        HolePattern? best = null;
        var bestD = VertexGripsOverlay.Reach + 1;
        var cam = _vm.View.Camera;
        foreach (var pat in Patterns())
        {
            var (x, y) = cam.ToScreen(PatternRotateHandleOverlay.CircleCentre(pat, pat.Azimuth, cam));
            var d = Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy));
            if (d < bestD) { bestD = d; best = pat; }
        }
        return best;
    }

    /// <summary>Marks the circle under the cursor and says what it does, or why it cannot.</summary>
    public void Hover(ToolPointer p)
    {
        var pat = _busy ? null : HandleAt(p.X, p.Y);
        Overlay.Hot = pat?.Id;
        if (pat == null) return;
        // the reason names a hole of the pattern, not the one next to the circle: it says what it is about
        var text = Refusal(pat) is { } no ? Loc.T("The pattern cannot be turned:") + " " + Loc.F(no.Reason, no.HoleLabel) : Loc.T("Turn the pattern");
        _vm.View.Preview.Labels.Add((PatternRotateHandleOverlay.CircleCentre(pat, pat.Azimuth, _vm.View.Camera, true), text));
    }

    /// <summary>Takes a plain press on the circle of a pattern that can be turned; false otherwise (the tool goes on as usual).</summary>
    public bool TryPress(ToolPointer p)
    {
        if (_busy || !p.Left || p.Modifiers.HasFlag(KeyModifiers.Shift) || p.Modifiers.HasFlag(KeyModifiers.Control) || p.ClickCount >= 2) return false;
        if (HandleAt(p.X, p.Y) is not { } pat || Refusal(pat) != null) return false;
        _pressed = pat.Id;
        _down = p;
        _dragging = false;
        return true;
    }

    public void Drag(ToolPointer p, PreviewOverlay preview)
    {
        if (_pressed is not Guid id || _down == null) return;
        if (!_dragging && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) <= DragPx) return;
        if (_vm.Project?.FindPattern(id) is not { } pat) { Cancel(); return; }
        if (!_dragging)
        {
            _dragging = true;
            _turn.PatternId = id;
            _turn.Pivot = pat.Center;
            _turn.Begin(OnGround(pat, _down.X, _down.Y));
            // the arrow as it was is drawn grey by the preview; the circle and the new arrow show the turn
            _vm.PatternGrid.HiddenArrow = id;
        }
        _turn.Update(OnGround(pat, p.X, p.Y), p.Modifiers);
        preview.Clear();
        _turn.ShowPreview(preview);
        Overlay.Dragged = (id, GeoMath.NormalizeBearing(pat.Azimuth + _turn.Delta));
        _vm.View.Redraw();
    }

    /// <summary>Turns the pattern by the drag (one undo step); a press without a drag changes nothing.</summary>
    public void Release(ToolPointer p)
    {
        if (_pressed == null) return;
        var turned = _dragging && Math.Abs(_turn.Delta) > 1e-9;
        _pressed = null;
        _down = null;
        _dragging = false;
        if (!turned) { Reset(); return; }
        _ = Commit();
    }

    private async Task Commit()
    {
        _busy = true;
        try { await _turn.Commit(); }
        finally
        {
            _busy = false;
            Reset();
            _vm.View.Redraw();
        }
    }

    public void Cancel()
    {
        if (_pressed == null) return;
        _pressed = null;
        _down = null;
        _dragging = false;
        Reset();
    }

    /// <summary>The turn and what it shows on the plan are dropped.</summary>
    private void Reset()
    {
        _turn.Reset();
        Overlay.Dragged = null;
        _vm.PatternGrid.HiddenArrow = null;
    }

    /// <summary>On a circle: a quarter turn either way, a typed angle, or along a line with the rotate tool.</summary>
    public IEnumerable<MenuEntry>? ContextMenu(ToolPointer p)
    {
        if (_busy || HandleAt(p.X, p.Y) is not { } pat) return null;
        var id = pat.Id;
        bool Free() => !_busy && _vm.Project?.FindPattern(id) is { } x && Refusal(x) == null;
        return new List<MenuEntry>
        {
            MenuEntry.Item("Turn by +90°", () => _ = Turn(id, 90), Free),
            MenuEntry.Item("Turn by −90°", () => _ = Turn(id, -90), Free),
            MenuEntry.Item("Turn by…", () => _ = _vm.RotatePatternDialog(id), Free),
            MenuEntry.Item("Along a line", () => AlongLine(id), Free)
        };
    }

    private async Task Turn(Guid id, double delta)
    {
        _busy = true;
        try { await _vm.RotatePattern(id, delta); }
        catch (Exception ex) { Dialogs.ShowError(ex); }
        finally { _busy = false; }
    }

    /// <summary>The rotate tool takes the pattern in its line mode: its selected holes alone stay selected when others have another.</summary>
    private void AlongLine(Guid id)
    {
        if (_vm.Doc is not { } doc) return;
        if (_vm.SelectedPattern?.Id != id) doc.Select(doc.SelectedHoles.Where(h => h.PatternId == id).Select(h => h.Id).ToList());
        _vm.PatternRotateTool();
        if (_vm.ActiveTool is PatternRotateTool tool) tool.ByLine = true;
    }
}
