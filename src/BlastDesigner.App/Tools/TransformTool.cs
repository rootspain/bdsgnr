using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.App.Tools;

/// <summary>
/// The Transform tool (doc/transform-tool-plan.md): scale along each axis or all together, tilt about X, Y and Z, mirror and
/// move the selected objects about a base point. The plan shows the result as it is set; a frame round the selection has
/// handles (corners and sides scale, the knob above turns, the cross is the base point), a 3D view has axes (cubes scale,
/// arcs tilt). Done (Enter) applies it as one step of undo. Holes and anything of a pattern of holes are left: they have
/// their own tool.
/// </summary>
public sealed class TransformTool : Tool
{
    public enum BasePoint { Centre, BottomCentre, CornerSW, CornerSE, CornerNE, CornerNW, Custom }

    /// <summary>The handles: 0…3 the corners and 4…7 the middles of the sides of the frame, then the knob, the base point, the
    /// axes of the 3D view (cube and arc of X, Y, Z) and the cube in the middle.</summary>
    public enum Handle { None = -1, Corner0, Corner1, Corner2, Corner3, Side0, Side1, Side2, Side3, Turn, Pivot, ScaleX, ScaleY, ScaleZ, TiltX, TiltY, TiltZ, ScaleAll }

    private readonly FrameOverlay _overlay;
    private List<Entity> _taken = new();
    private (Vec3 Min, Vec3 Max)? _frame;
    private SpatialTransform _t = SpatialTransform.None;
    private BasePoint _base = BasePoint.Centre;
    private Vec3 _custom;
    private bool _objectAxes;
    private bool _proportional = true;

    // a drag: the handle, where it started, the transform then; a number typed while dragging
    private Handle _drag = Handle.None;
    private SpatialTransform _t0 = SpatialTransform.None;
    private (double X, double Y) _start;
    private Vec3 _startWorld;
    private string _typed = "";
    private (double X, double Y) _cursor;

    public TransformTool(MainViewModel vm) : base(vm) => _overlay = new FrameOverlay(this);

    public override string Name => "Transform";
    public override string Hint => "Scale, tilt and mirror the selected objects: the corners of the frame scale in proportion (Shift each axis, Alt from the centre), the sides along one axis, the knob above turns (Shift 15°, Ctrl 1°), the cross is the base point. Type a number while dragging and press Enter for an exact value. Done applies. Holes and the inside of a pattern are left: they have their own tool; the outline of a pattern is only tilted.";

    /// <summary>The transform as it is set: the preview shows it, Done applies it.</summary>
    public SpatialTransform Current => _t;
    public IReadOnlyList<Entity> Taken => _taken;
    public BasePoint Base => _base;
    public bool ObjectAxes => _objectAxes;
    /// <summary>Whether the selection has a circle: a circle is scaled the same along every axis, so the scale stays in proportion.</summary>
    public bool HasCircle => _taken.Any(e => e is CircleEntity);
    /// <summary>The outlines of patterns among the objects: they are only tilted, never scaled or mirrored.</summary>
    private HashSet<Guid> _outlines = new();
    /// <summary>Only outlines of patterns are taken: the scale, the mirrors and their grips are off.</summary>
    public bool OnlyTilts => _taken.Count > 0 && _taken.All(e => _outlines.Contains(e.Id));
    public bool Proportional => _proportional || HasCircle;
    /// <summary>Whether the selection gives axes of an object (a block, a line, a text, a dimension).</summary>
    public bool ObjectAxesAvailable => SpatialTransforms.ObjectAxes(_taken) != null;
    public Handle Dragging => _drag;

    public override void Activate()
    {
        base.Activate();
        View.Overlays.Add(_overlay);
        Vm.SelectionChanged += OnSelection;
        Take();
        Refresh();
    }

    public override void Deactivate()
    {
        Vm.SelectionChanged -= OnSelection;
        View.Overlays.Remove(_overlay);
        base.Deactivate();
    }

    private void OnSelection()
    {
        if (_drag != Handle.None) return;
        _t = SpatialTransform.None;
        Take();
        Refresh();
        Vm.RefreshToolPanel();
    }

    /// <summary>The objects the tool changes and the frame round them; what it leaves is said in the status line.</summary>
    private void Take()
    {
        var p = Vm.Project;
        if (p == null || Vm.Doc == null) { _taken = new(); _frame = null; return; }
        var ids = Vm.Doc.Selection.ToList();
        // the frame is drawn round what a tilt about Z would take: a tilt about X or Y leaves blocks, but they stay in it
        _taken = SpatialTransforms.Taken(p, ids, SpatialTransform.None);
        _outlines = SpatialTransforms.PatternOutlineIds(p);
        if (!ObjectAxesAvailable) _objectAxes = false;
        _frame = SpatialTransforms.Frame(_taken.SelectMany(SpatialTransforms.FramePoints), Axes);
        var holes = Vm.Doc.SelectedEntities.Count(e => e is Hole);
        if (ids.Count == 0) Vm.SetStatus(Loc.T("Select the objects to transform (a click on an object selects it, Shift adds)."));
        else if (holes > 0) Vm.SetStatus(Loc.F("Holes are not transformed: {0}", holes));
        else Vm.SetStatus(Loc.T(Hint));
    }

    /// <summary>The turn of the axes of the frame: none, or the object's.</summary>
    public double Axes => _objectAxes ? SpatialTransforms.ObjectAxes(_taken) ?? 0 : 0;

    /// <summary>A point of the frame given as fractions along its axes (0 low, 1 high), in the world.</summary>
    private Vec3 FramePoint(double fx, double fy, double fz)
    {
        if (_frame is not var (lo, hi)) return Vec3.Zero;
        var local = new Vec3(lo.X + (hi.X - lo.X) * fx, lo.Y + (hi.Y - lo.Y) * fy, lo.Z + (hi.Z - lo.Z) * fz);
        return SpatialTransforms.FromFrame(local, Axes);
    }

    /// <summary>The base point the chosen way.</summary>
    public Vec3 PivotOf(BasePoint b) => b switch
    {
        BasePoint.Centre => FramePoint(0.5, 0.5, 0.5),
        BasePoint.BottomCentre => FramePoint(0.5, 0.5, 0),
        BasePoint.CornerSW => FramePoint(0, 0, 0.5),
        BasePoint.CornerSE => FramePoint(1, 0, 0.5),
        BasePoint.CornerNE => FramePoint(1, 1, 0.5),
        BasePoint.CornerNW => FramePoint(0, 1, 0.5),
        _ => _custom
    };

    /// <summary>The preview again: the objects as the transform leaves them, in their place, and the frame.</summary>
    public void Refresh()
    {
        _t = _t with { Pivot = PivotOf(_base), AxesAzimuth = Axes };
        Preview.Clear();
        if (!_t.IsIdentity && _t.Problem == null && _taken.Count > 0)
        {
            var copies = new Dictionary<Guid, Entity>();
            foreach (var e in _taken)
            {
                if (Vm.Project is not { } project || SpatialTransforms.Left(project, e, _t) != null) continue;
                copies[e.Id] = SpatialTransforms.Preview(e, _t);
                // where it was, thin
                var ghost = e switch
                {
                    PolylineEntity pl => (IReadOnlyList<Vec3>)pl.Points,
                    CircleEntity c => c.ToPolygon(48),
                    BlockEntity b => b.Corners().Take(4).ToList(),
                    DimensionEntity d => d.Drawn().SelectMany(x => x).ToList(),
                    TimingLineEntity tl => tl.Points,
                    _ => null
                };
                if (ghost is { Count: >= 2 }) Preview.Guides.Add((ghost, GhostColor));
            }
            Preview.Replaced = copies;
        }
        View.Redraw();
    }

    internal static SKColor GhostColor => new(0x9964748B);

    // ---- the values the panel sets --------------------------------------------------------------------------------------

    /// <summary>A scale along one axis (0 X, 1 Y, 2 Z), or all together while the scale is in proportion.</summary>
    public bool SetScale(int axis, double v)
    {
        if (v <= 0) { Vm.SetStatus(Loc.T("The scale must be above 0")); return false; }
        _t = Proportional ? _t with { ScaleX = v, ScaleY = v, ScaleZ = v }
            : axis switch { 0 => _t with { ScaleX = v }, 1 => _t with { ScaleY = v }, _ => _t with { ScaleZ = v } };
        Refresh();
        return true;
    }

    /// <summary>A tilt about one axis (0 X, 1 Y, 2 Z), °.</summary>
    public bool SetTilt(int axis, double deg)
    {
        if (axis < 2 && Math.Abs(deg) >= 89) { Vm.SetStatus(Loc.T("A tilt about X or Y must be below 89°")); return false; }
        _t = axis switch { 0 => _t with { TiltX = deg }, 1 => _t with { TiltY = deg }, _ => _t with { TiltZ = deg } };
        Refresh();
        return true;
    }

    public void SetMove(int axis, double m)
    {
        var v = _t.Move;
        _t = _t with { Move = axis switch { 0 => v with { X = m }, 1 => v with { Y = m }, _ => v with { Z = m } } };
        Refresh();
    }

    /// <summary>A mirror across the axis X (0) or Y (1) of the frame, through the base point; again takes it off.</summary>
    public void ToggleMirror(int axis)
    {
        _t = axis == 0 ? _t with { MirrorX = !_t.MirrorX } : _t with { MirrorY = !_t.MirrorY };
        Refresh();
        Vm.RefreshToolPanel();
    }

    public void SetProportional(bool on)
    {
        _proportional = on;
        // into proportion: every axis takes the scale of X
        if (Proportional) _t = _t with { ScaleY = _t.ScaleX, ScaleZ = _t.ScaleX };
        Refresh();
        Vm.RefreshToolPanel();
    }

    public void SetObjectAxes(bool on)
    {
        _objectAxes = on && ObjectAxesAvailable;
        var pivot = _t.Pivot;
        Take();
        // the base point stays where it is
        if (_base == BasePoint.Custom) _custom = pivot;
        Refresh();
        Vm.RefreshToolPanel();
    }

    /// <summary>The base point another way; the result stays where it is (the move takes up the difference).</summary>
    public void SetBase(BasePoint b, Vec3? custom = null)
    {
        var before = _t;
        _base = b;
        if (custom is Vec3 c) _custom = c;
        else if (b == BasePoint.Custom) _custom = before.Pivot;
        var pivot = PivotOf(b);
        if (!before.IsIdentity) _t = before with { Pivot = pivot, Move = before.Apply(pivot) - pivot };
        Refresh();
        Vm.RefreshToolPanel();
    }

    /// <summary>The base point's coordinate typed in (0 X, 1 Y, 2 Z).</summary>
    public void SetPivotCoordinate(int axis, double v)
    {
        var p = _t.Pivot;
        SetBase(BasePoint.Custom, axis switch { 0 => p with { X = v }, 1 => p with { Y = v }, _ => p with { Z = v } });
    }

    /// <summary>Back to no change; the base point and the axes stay as chosen.</summary>
    public void Reset()
    {
        _t = SpatialTransform.None;
        Refresh();
        Vm.RefreshToolPanel();
    }

    // ---- applying --------------------------------------------------------------------------------------------------

    protected override bool HasPendingInput => _drag != Handle.None || !_t.IsIdentity;

    /// <summary>Esc: a drag goes back to where it started; else the transform back to none.</summary>
    public override void Cancel()
    {
        if (_drag != Handle.None) { _t = _t0; _drag = Handle.None; _typed = ""; Refresh(); return; }
        if (!_t.IsIdentity) { Reset(); return; }
        Preview.Clear();
    }

    /// <summary>Done (Enter): applies the transform as one step of undo, and the frame starts again from the result.</summary>
    public override bool FinishEntry()
    {
        if (_drag != Handle.None) return true;
        if (_t.IsIdentity || _taken.Count == 0) return false;
        if (_t.Problem is { } problem) { Vm.SetStatus(Loc.T(problem)); return true; }
        // the base point the user put stays where it is; the move is done
        if (_base == BasePoint.Custom) _custom = _t.Pivot + _t.Move;
        Vm.ApplySpatialTransform(_taken.Select(e => e.Id).ToList(), _t);
        _t = SpatialTransform.None;
        Take();
        Refresh();
        Vm.RefreshToolPanel();
        return true;
    }

    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p)
    {
        yield return MenuEntry.Item("Mirror X", () => ToggleMirror(0));
        yield return MenuEntry.Item("Mirror Y", () => ToggleMirror(1));
        yield return MenuEntry.Item("Reset", Reset, () => !_t.IsIdentity);
        yield return MenuEntry.Separator;
        yield return MenuEntry.Item("Apply", () => Vm.CompleteTool(), () => !_t.IsIdentity);
        yield return MenuEntry.Item("Close tool", () => Vm.SelectTool());
    }

    // ---- the frame and its handles ---------------------------------------------------------------------------------

    /// <summary>The corners of the frame on the ground of it (its bottom in the 3D view), where the transform takes them.</summary>
    public Vec3[] Corners()
    {
        var m = _t.Matrix;
        return new[] { FramePoint(0, 0, 0), FramePoint(1, 0, 0), FramePoint(1, 1, 0), FramePoint(0, 1, 0) }.Select(m.Apply).ToArray();
    }

    /// <summary>Where a handle is on the screen; null when it is not shown in this view.</summary>
    public (double X, double Y)? HandleAt(Handle h)
    {
        if (_frame == null) return null;
        var cam = View.Camera;
        (double, double) S(Vec3 w) { var (x, y) = cam.ToScreen(w); return (x, y); }
        if (h == Handle.Pivot) return S(_t.Pivot + _t.Move);
        if (cam.IsPlan)
        {
            var c = Corners();
            if (h <= Handle.Corner3) return S(c[(int)h]);
            if (h <= Handle.Side3) { var k = h - Handle.Side0; return S(Vec3.Lerp(c[k], c[(k + 1) % 4], 0.5)); }
            if (h == Handle.Turn)
            {
                // above the middle of the far side, out from the middle of the frame
                var (mx, my) = S(Vec3.Lerp(c[2], c[3], 0.5));
                var (cx, cy) = S(Vec3.Lerp(c[0], c[2], 0.5));
                double dx = mx - cx, dy = my - cy, len = Math.Sqrt(dx * dx + dy * dy);
                return len < 1e-6 ? (mx, my - 28) : (mx + dx / len * 28, my + dy / len * 28);
            }
            return null;
        }
        // the 3D view: the axes from the base point
        if (h is Handle.ScaleX or Handle.ScaleY or Handle.ScaleZ) return S(AxisEnd(h - Handle.ScaleX));
        if (h == Handle.ScaleAll) return S(_t.Pivot + _t.Move);
        return null;
    }

    /// <summary>The direction of an axis of the frame (0 X, 1 Y, 2 Z) as the tilts leave it.</summary>
    public Vec3 AxisDirection(int axis)
    {
        var unit = axis switch { 0 => new Vec3(1, 0, 0), 1 => new Vec3(0, 1, 0), _ => new Vec3(0, 0, 1) };
        var tilt = (_t with { ScaleX = 1, ScaleY = 1, ScaleZ = 1, MirrorX = false, MirrorY = false, Move = Vec3.Zero, Pivot = Vec3.Zero }).Matrix;
        return tilt.ApplyVector(SpatialTransforms.FromFrame(unit, Axes)).Normalized();
    }

    public const double AxisPx = 80, ArcPx = 56;

    public Vec3 AxisEnd(int axis) => _t.Pivot + _t.Move + AxisDirection(axis) * (AxisPx / View.Camera.Scale);

    /// <summary>The arc of the tilt about an axis: a circle round the base point square to it.</summary>
    public List<Vec3> Arc(int axis)
    {
        var d = AxisDirection(axis);
        var u = Math.Abs(d.Z) < 0.9 ? Vec3.Cross(d, new Vec3(0, 0, 1)).Normalized() : Vec3.Cross(d, new Vec3(1, 0, 0)).Normalized();
        var w = Vec3.Cross(d, u);
        var r = ArcPx / View.Camera.Scale;
        var c = _t.Pivot + _t.Move;
        return Enumerable.Range(0, 49).Select(i => c + (u * Math.Cos(i * Math.PI / 24) + w * Math.Sin(i * Math.PI / 24)) * r).ToList();
    }

    /// <summary>The handle under the cursor, within the pick radius.</summary>
    public Handle HandleUnder(double x, double y)
    {
        var reach = Math.Max(8, View.TargetRadius);
        var best = Handle.None;
        var bestD = reach;
        // the outline of a pattern alone: no grip that scales (the frame, the cubes)
        var handles = View.Camera.IsPlan
            ? OnlyTilts ? new[] { Handle.Pivot, Handle.Turn } : new[] { Handle.Pivot, Handle.Turn, Handle.Corner0, Handle.Corner1, Handle.Corner2, Handle.Corner3, Handle.Side0, Handle.Side1, Handle.Side2, Handle.Side3 }
            : OnlyTilts ? Array.Empty<Handle>() : new[] { Handle.Pivot, Handle.ScaleX, Handle.ScaleY, Handle.ScaleZ };
        foreach (var h in handles)
            if (HandleAt(h) is var (hx, hy) && Math.Sqrt((hx - x) * (hx - x) + (hy - y) * (hy - y)) is var d && d < bestD) (best, bestD) = (h, d);
        if (best != Handle.None || View.Camera.IsPlan) return best == Handle.Pivot && !View.Camera.IsPlan ? Handle.ScaleAll : best;
        // the arcs of the 3D view
        for (int axis = 0; axis < 3; axis++)
        {
            var pts = Arc(axis).Select(v => View.Camera.ToScreen(v)).ToList();
            for (int i = 1; i < pts.Count; i++)
            {
                var d = SegmentDistance(x, y, pts[i - 1], pts[i]);
                if (d < bestD) (best, bestD) = (Handle.TiltX + axis, d);
            }
        }
        return best;
    }

    private static double SegmentDistance(double x, double y, (float X, float Y) a, (float X, float Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        var t = len2 > 0 ? Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len2, 0, 1) : 0;
        double cx = a.X + t * dx - x, cy = a.Y + t * dy - y;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    // ---- pointer -----------------------------------------------------------------------------------------------------

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left) return;
        var h = HandleUnder(p.X, p.Y);
        if (h == Handle.None)
        {
            // a click on an object selects it, Shift adds it or takes it away
            if (p.Hit is { } hit && Vm.Doc != null)
            {
                if (p.Modifiers.HasFlag(KeyModifiers.Shift))
                {
                    var sel = Vm.Doc.Selection.ToHashSet();
                    if (!sel.Remove(hit.Id)) sel.Add(hit.Id);
                    Vm.Doc.Select(sel);
                }
                else Vm.Doc.Select(new[] { hit.Id });
            }
            return;
        }
        Begin(h, p);
    }

    /// <summary>A drag of a handle starts: the anchor of a scale becomes the base point, the result staying where it is.</summary>
    public void Begin(Handle h, ToolPointer p)
    {
        _drag = h;
        _typed = "";
        _start = (p.X, p.Y);
        _cursor = _start;
        _startWorld = p.World;
        if (h <= Handle.Side3)
        {
            // a corner scales from the opposite corner, a side from the opposite side; with Alt from the middle
            var alt = p.Modifiers.HasFlag(KeyModifiers.Alt);
            var anchor = alt ? FramePoint(0.5, 0.5, 0.5) : h switch
            {
                Handle.Corner0 => FramePoint(1, 1, 0.5), Handle.Corner1 => FramePoint(0, 1, 0.5),
                Handle.Corner2 => FramePoint(0, 0, 0.5), Handle.Corner3 => FramePoint(1, 0, 0.5),
                Handle.Side0 => FramePoint(0.5, 1, 0.5), Handle.Side1 => FramePoint(0, 0.5, 0.5),
                Handle.Side2 => FramePoint(0.5, 0, 0.5), _ => FramePoint(1, 0.5, 0.5)
            };
            Rebase(anchor);
        }
        _t0 = _t;
    }

    /// <summary>The base point to <paramref name="pivot"/> with the result where it is: the move takes up the difference.</summary>
    private void Rebase(Vec3 pivot)
    {
        var before = _t;
        _base = BasePoint.Custom;
        _custom = pivot;
        _t = before with { Pivot = pivot, Move = before.Apply(pivot) - pivot };
    }

    public override void PointerMoved(ToolPointer p)
    {
        if (_drag == Handle.None) { _overlay.Hot = HandleUnder(p.X, p.Y); View.Redraw(); return; }
        _cursor = (p.X, p.Y);
        if (_typed.Length > 0) { View.Redraw(); return; }
        Drag(p);
        Refresh();
    }

    public override void PointerReleased(ToolPointer p)
    {
        if (_drag == Handle.None || _typed.Length > 0) return;
        _drag = Handle.None;
        Refresh();
        Vm.RefreshToolPanel();
    }

    /// <summary>The handle taken to the cursor.</summary>
    private void Drag(ToolPointer p)
    {
        var t0 = _t0;
        var shift = p.Modifiers.HasFlag(KeyModifiers.Shift);
        var ctrl = p.Modifiers.HasFlag(KeyModifiers.Control);
        if (_drag == Handle.Pivot)
        {
            _t = t0;
            Rebase(p.Snapped);
            return;
        }
        if (_drag == Handle.Turn)
        {
            var c = t0.Pivot + t0.Move;
            var a0 = Math.Atan2(_startWorld.Y - c.Y, _startWorld.X - c.X);
            var a1 = Math.Atan2(p.World.Y - c.Y, p.World.X - c.X);
            var turn = t0.TiltZ - (a1 - a0) * 180 / Math.PI;
            var step = shift ? 15 : ctrl ? 1 : 0.1;
            _t = t0 with { TiltZ = Math.Round(turn / step) * step };
            return;
        }
        if (_drag <= Handle.Side3)
        {
            // the cursor and the grabbed point in the frame, before the scale: the turn in plan undone (a tilt about X or Y is left out)
            var centre = t0.Pivot + t0.Move;
            var unturn = Affine3.RotationZ(t0.TiltZ).Then(Affine3.RotationZ(Axes));
            var v = unturn.ApplyVector((p.World - centre).WithZ(0));
            var grabbed = _drag <= Handle.Corner3
                ? new[] { FramePoint(0, 0, 0.5), FramePoint(1, 0, 0.5), FramePoint(1, 1, 0.5), FramePoint(0, 1, 0.5) }[(int)_drag]
                : new[] { FramePoint(0.5, 0, 0.5), FramePoint(1, 0.5, 0.5), FramePoint(0.5, 1, 0.5), FramePoint(0, 0.5, 0.5) }[_drag - Handle.Side0];
            var g = Affine3.RotationZ(Axes).ApplyVector((grabbed - t0.Pivot).WithZ(0));
            double mx = t0.MirrorX ? -1 : 1, my = t0.MirrorY ? -1 : 1;
            double Ratio(double want, double was) => Math.Abs(was) < 1e-9 ? double.NaN : Math.Max(0.01, want / was);
            var sx = Ratio(v.X * mx, g.X);
            var sy = Ratio(v.Y * my, g.Y);
            if (_drag <= Handle.Corner3)
            {
                if (shift && !Proportional)
                    _t = t0 with { ScaleX = double.IsNaN(sx) ? t0.ScaleX : sx, ScaleY = double.IsNaN(sy) ? t0.ScaleY : sy };
                else
                {
                    // in proportion: along the diagonal
                    var gg = g.X * g.X + g.Y * g.Y;
                    var s = gg < 1e-12 ? t0.ScaleX : Math.Max(0.01, (v.X * mx * g.X + v.Y * my * g.Y) / gg);
                    _t = Proportional ? t0 with { ScaleX = s, ScaleY = s, ScaleZ = s } : t0 with { ScaleX = s, ScaleY = s };
                }
            }
            else
            {
                var alongX = _drag is Handle.Side1 or Handle.Side3;
                var s = alongX ? sx : sy;
                if (double.IsNaN(s)) return;
                _t = shift || Proportional ? t0 with { ScaleX = s, ScaleY = s, ScaleZ = s } : alongX ? t0 with { ScaleX = s } : t0 with { ScaleY = s };
            }
            return;
        }
        var cam = View.Camera;
        var (cx, cy) = cam.ToScreen(t0.Pivot + t0.Move);
        if (_drag is Handle.ScaleX or Handle.ScaleY or Handle.ScaleZ or Handle.ScaleAll)
        {
            double f;
            if (_drag == Handle.ScaleAll) f = 1 + (_start.Y - p.Y) / 100;
            else
            {
                var (ex, ey) = cam.ToScreen(t0.Pivot + t0.Move + AxisDirection(_drag - Handle.ScaleX) * (AxisPx / cam.Scale));
                double ax = ex - cx, ay = ey - cy, len = Math.Sqrt(ax * ax + ay * ay);
                if (len < 1e-6) return;
                // along the axis as the screen shows it: dragged out by its own length on the screen, twice the size
                f = 1 + ((p.X - _start.X) * ax + (p.Y - _start.Y) * ay) / (len * len);
            }
            f = Math.Max(0.01, f);
            var axis = _drag == Handle.ScaleAll ? -1 : _drag - Handle.ScaleX;
            _t = Proportional || axis < 0 ? t0 with { ScaleX = Math.Max(0.01, t0.ScaleX * f), ScaleY = Math.Max(0.01, t0.ScaleX * f), ScaleZ = Math.Max(0.01, t0.ScaleX * f) }
                : axis switch
                {
                    0 => t0 with { ScaleX = Math.Max(0.01, t0.ScaleX * f) },
                    1 => t0 with { ScaleY = Math.Max(0.01, t0.ScaleY * f) },
                    _ => t0 with { ScaleZ = Math.Max(0.01, t0.ScaleZ * f) }
                };
            return;
        }
        if (_drag is Handle.TiltX or Handle.TiltY or Handle.TiltZ)
        {
            // the turn of the cursor round the base point on the screen, counterclockwise as seen; by the right-hand rule
            // when the axis points at the viewer
            var a0 = Math.Atan2(-(_start.Y - cy), _start.X - cx);
            var a1 = Math.Atan2(-(p.Y - cy), p.X - cx);
            var seen = (a1 - a0) * 180 / Math.PI;
            if (seen > 180) seen -= 360;
            if (seen < -180) seen += 360;
            var axis = _drag - Handle.TiltX;
            var toViewer = Vec3.Cross(cam.Right, cam.Up);
            var turn = Vec3.Dot(AxisDirection(axis), toViewer) >= 0 ? seen : -seen;
            var step = shift ? 15 : ctrl ? 1 : 0.1;
            double Snap(double v) => Math.Round(v / step) * step;
            _t = axis switch
            {
                0 => t0 with { TiltX = Math.Clamp(Snap(t0.TiltX + turn), -88.9, 88.9) },
                1 => t0 with { TiltY = Math.Clamp(Snap(t0.TiltY + turn), -88.9, 88.9) },
                _ => t0 with { TiltZ = Snap(t0.TiltZ - turn) }
            };
        }
    }

    /// <summary>What the drag sets, as the label at the cursor says it.</summary>
    public string DragText()
    {
        var typed = _typed.Length > 0 ? " = " + _typed : "";
        string N(double v) => v.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);
        return _drag switch
        {
            Handle.Turn or Handle.TiltZ => Loc.F("Tilt Z {0}°", N(_t.TiltZ)) + typed,
            Handle.TiltX => Loc.F("Tilt X {0}°", N(_t.TiltX)) + typed,
            Handle.TiltY => Loc.F("Tilt Y {0}°", N(_t.TiltY)) + typed,
            Handle.Pivot => Loc.T("Base point"),
            _ => (Proportional || _t.IsUniformScale ? Loc.F("Scale {0}", N(_t.ScaleX)) : $"X {N(_t.ScaleX)} × Y {N(_t.ScaleY)} × Z {N(_t.ScaleZ)}") + typed
        };
    }

    public (double X, double Y) Cursor => _cursor;

    /// <summary>A number typed while dragging, Enter takes it exactly; Backspace takes a character back.</summary>
    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (_drag != Handle.None && _drag != Handle.Pivot)
        {
            var ch = key switch
            {
                >= Key.D0 and <= Key.D9 when mods == KeyModifiers.None => (char)('0' + (key - Key.D0)),
                >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (key - Key.NumPad0)),
                Key.OemPeriod or Key.OemComma or Key.Decimal => '.',
                Key.OemMinus or Key.Subtract => '-',
                _ => '\0'
            };
            if (ch != '\0') { _typed += ch; View.Redraw(); return true; }
            if (key == Key.Back && _typed.Length > 0) { _typed = _typed[..^1]; View.Redraw(); return true; }
            if (key == Key.Enter && _typed.Length > 0) { TakeTyped(); return true; }
        }
        return base.KeyDown(key, mods);
    }

    /// <summary>The number typed while dragging, as the exact value of what the handle sets.</summary>
    public void TakeTyped()
    {
        var text = _typed;
        var handle = _drag;
        _typed = "";
        _drag = Handle.None;
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) { _t = _t0; Refresh(); return; }
        _t = _t0;
        var ok = handle switch
        {
            Handle.Turn or Handle.TiltZ => SetTilt(2, v),
            Handle.TiltX => SetTilt(0, v),
            Handle.TiltY => SetTilt(1, v),
            Handle.ScaleX => SetScale(0, v),
            Handle.ScaleY => SetScale(1, v),
            Handle.ScaleZ => SetScale(2, v),
            Handle.Side1 or Handle.Side3 => SetScale(0, v),
            Handle.Side0 or Handle.Side2 => SetScale(1, v),
            _ => SetCornerScale(v)
        };
        if (!ok) { _t = _t0; Refresh(); }
        Vm.RefreshToolPanel();
    }

    private bool SetCornerScale(double v)
    {
        if (v <= 0) return SetScale(0, v);
        _t = Proportional ? _t with { ScaleX = v, ScaleY = v, ScaleZ = v } : _t with { ScaleX = v, ScaleY = v };
        Refresh();
        return true;
    }

    /// <summary>Draws the frame, its handles and the axes of the 3D view, and the value at the cursor while dragging.</summary>
    private sealed class FrameOverlay : IOverlay
    {
        private readonly TransformTool _tool;
        public Handle Hot { get; set; } = Handle.None;

        public FrameOverlay(TransformTool tool) => _tool = tool;

        private static readonly SKColor Frame = new(0xFF2563EB);
        private static readonly SKColor[] AxisColors = { new(0xFFDC2626), new(0xFF16A34A), new(0xFF2563EB) };

        public void Draw(SKCanvas canvas, Camera cam, BlastProject project)
        {
            var t = _tool;
            if (t._frame == null) return;
            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = Frame, PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0) };
            using var solid = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, Color = Frame };
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White };
            using var hot = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = Frame };
            void Square(Handle h, float r)
            {
                if (t.HandleAt(h) is not var (x, y)) return;
                var rect = new SKRect((float)x - r, (float)y - r, (float)x + r, (float)y + r);
                canvas.DrawRect(rect, h == Hot || h == t._drag ? hot : fill);
                canvas.DrawRect(rect, solid);
            }
            if (cam.IsPlan)
            {
                var c = t.Corners().Select(v => cam.ToScreen(v)).ToArray();
                using (var path = new SKPath())
                {
                    path.MoveTo(c[0].X, c[0].Y);
                    for (int i = 1; i < 4; i++) path.LineTo(c[i].X, c[i].Y);
                    path.Close();
                    canvas.DrawPath(path, line);
                }
                if (!t.OnlyTilts) for (var h = Handle.Corner0; h <= Handle.Side3; h++) Square(h, h <= Handle.Corner3 ? 5 : 4);
                if (t.HandleAt(Handle.Turn) is var (kx, ky) && t.HandleAt(Handle.Side2) is var (sx, sy))
                {
                    canvas.DrawLine((float)sx, (float)sy, (float)kx, (float)ky, solid);
                    canvas.DrawCircle((float)kx, (float)ky, 6, Hot == Handle.Turn || t._drag == Handle.Turn ? hot : fill);
                    canvas.DrawCircle((float)kx, (float)ky, 6, solid);
                }
            }
            else
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = AxisColors[axis] };
                    var (bx, by) = cam.ToScreen(t._t.Pivot + t._t.Move);
                    var (ex, ey) = cam.ToScreen(t.AxisEnd(axis));
                    canvas.DrawLine(bx, by, ex, ey, paint);
                    var arc = t.Arc(axis).Select(v => cam.ToScreen(v)).ToList();
                    using var arcPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Hot == Handle.TiltX + axis || t._drag == Handle.TiltX + axis ? 3 : 1.4f, Color = AxisColors[axis].WithAlpha(200) };
                    for (int i = 1; i < arc.Count; i++) canvas.DrawLine(arc[i - 1].X, arc[i - 1].Y, arc[i].X, arc[i].Y, arcPaint);
                    using var cube = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = AxisColors[axis] };
                    var r = Hot == Handle.ScaleX + axis || t._drag == Handle.ScaleX + axis ? 7f : 5f;
                    if (!t.OnlyTilts) canvas.DrawRect(new SKRect(ex - r, ey - r, ex + r, ey + r), cube);
                }
                if (!t.OnlyTilts) Square(Handle.ScaleAll, 5);
            }
            // the base point: a cross in a circle
            if (t.HandleAt(Handle.Pivot) is var (px, py))
            {
                using var cross = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Hot == Handle.Pivot || t._drag == Handle.Pivot ? 2.4f : 1.6f, Color = new SKColor(0xFFF59E0B) };
                canvas.DrawCircle((float)px, (float)py, 7, cross);
                canvas.DrawLine((float)px - 11, (float)py, (float)px + 11, (float)py, cross);
                canvas.DrawLine((float)px, (float)py - 11, (float)px, (float)py + 11, cross);
            }
            if (t._drag != Handle.None)
            {
                using var text = new SKPaint { IsAntialias = true, TextSize = 12, Color = SKColors.White, Typeface = SKTypeface.Default };
                var label = t.DragText();
                var w = text.MeasureText(label);
                var (lx, ly) = ((float)t._cursor.X + 14, (float)t._cursor.Y + 22);
                using var plate = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0xE01E293B) };
                canvas.DrawRoundRect(new SKRect(lx - 6, ly - 14, lx + w + 6, ly + 5), 4, 4, plate);
                canvas.DrawText(label, lx, ly, text);
            }
        }
    }
}
