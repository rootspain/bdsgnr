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
/// The vertices of the collar and depth polygons of the patterns with a selected hole, which move only up and down. In 3D a
/// round grip on each: a drag moves its vertex in Z (0.1 m steps, Ctrl 0.01 m), with Shift the whole polygon; the drag
/// shows where the holes would go, the release is one undo step. A double click on a grip, or on the plan on the elevation
/// written at a vertex (the collar above, the depth below), asks for the elevation. A vertex two segments share is one grip.
/// A click selects a vertex, Ctrl+click adds one or takes it away: only vertices of one polygon (collar or depth) of one
/// segment go together. A drag of a selected vertex moves them all, a double click gives them all the elevation typed.
/// </summary>
public sealed class DepthPolygonGrips : ISelectGesture
{
    private const double DragPx = 4;
    private static readonly SKColor Moved = new(0xFF94A3B8);

    private readonly MainViewModel _vm;
    private readonly Func<bool> _visible;
    private ToolPointer? _down;
    private bool _dragging;
    private bool _busy;

    public DepthPolygonGrips(MainViewModel vm, Func<bool> visible)
    {
        _vm = vm;
        _visible = visible;
        Overlay = new DepthPolygonGripsOverlay(this, () => (new SKColor(vm.Settings.PatternCollarPolygonColor), new SKColor(vm.Settings.PatternDepthPolygonColor)),
            () => PlanRenderer.Adapt(new SKColor(vm.Settings.SelectionColor)));
    }

    public DepthPolygonGripsOverlay Overlay { get; }
    IOverlay? ISelectGesture.Overlay => Overlay;

    /// <summary>A vertex of a polygon: its pattern, its name, collar (true) or depth, where it is and its elevation.</summary>
    /// <param name="Leaves">The segments (or the pattern itself) the vertex belongs to: two at a seam.</param>
    public sealed record Grip(Guid Root, string Key, bool Collar, Vec3 Point, double Z, IReadOnlySet<Guid> Leaves);

    /// <summary>The selected vertices: of one pattern, one polygon and one segment.</summary>
    private readonly HashSet<(Guid Root, string Key, bool Collar)> _selected = new();
    public bool IsSelected(Grip g) => _selected.Contains((g.Root, g.Key, g.Collar));
    /// <summary>The grips of the selected vertices there are now.</summary>
    public List<Grip> Selected() => Grips().Where(IsSelected).ToList();
    /// <summary>The change of elevation the drag gives (the pressed vertex and the selected ones with it).</summary>
    public double? DraggedDz { get; private set; }
    /// <summary>The drag moves the selected vertices, not the pressed one alone.</summary>
    public bool DragsSelection { get; private set; }

    public Grip? Hot { get; private set; }
    public Grip? Pressed { get; private set; }
    /// <summary>The elevation the drag gives the pressed vertex (with Shift, the shift of the whole polygon).</summary>
    public double? DraggedZ { get; private set; }
    public bool Active => Pressed != null;

    /// <summary>
    /// The view shows the vertical well enough to drag up and down: a 3D view from the side or at a slant, down to 75° from
    /// the horizon (the more from the side, the plainer the drag). On the plan the elevations written at the vertices are typed in.
    /// </summary>
    public static bool SeesTheVertical(Camera cam) => cam.IsPlan || Math.Abs(cam.Up.Z) > 0.26;

    /// <summary>The vertices with a grip: of the patterns of the selected holes whose segments use a polygon, while nothing is moved.</summary>
    public List<Grip> Grips()
    {
        var grips = new List<Grip>();
        if (!_visible() || _vm.Project is not { } project || !SeesTheVertical(_vm.View.Camera)) return grips;
        var bench = project.Entities.OfType<SurfaceEntity>().FirstOrDefault(s => s.Kind == SurfaceKind.Bench);
        var seen = new Dictionary<(Guid, string, bool), HashSet<Guid>>();
        void Add(HolePattern root, HolePattern leaf, SurfaceVertex v, bool collar)
        {
            if (!seen.TryGetValue((root.Id, v.Key!, collar), out var leaves))
            {
                seen[(root.Id, v.Key!, collar)] = leaves = new HashSet<Guid>();
                grips.Add(new Grip(root.Id, v.Key!, collar, v.On(collar), v.Z(collar), leaves));
            }
            leaves.Add(leaf.Id);
        }
        var shown = PatternOverlay.Shown(_vm.Doc);
        foreach (var root in shown.Select(x => PatternTree.Root(project, x)).Distinct())
            // a selected segment: the vertices of its own polygons only
            foreach (var leaf in PatternOverlay.DepthLeaves(project, root, shown).Where(l => l.HoleDefaults.Depth.UsesPolygons))
            {
                var spec = leaf.HoleDefaults.Depth;
                foreach (var v in PatternSurfaces.Polygons(project, leaf, bench).SelectMany(x => x).Where(v => v.Key != null))
                {
                    if (spec.Collars == CollarSource.Polygon) Add(root, leaf, v, true);
                    if (spec.Mode == DepthMode.Polygon) Add(root, leaf, v, false);
                }
            }
        return grips;
    }

    /// <summary>
    /// The grip at a screen point: in 3D the nearest circle within reach; on the plan the elevation written at a vertex
    /// (the collar above and right of it, the depth below and right).
    /// </summary>
    private Grip? GripAt(double sx, double sy)
    {
        var cam = _vm.View.Camera;
        Grip? best = null;
        var bestD = VertexGripsOverlay.Reach + 1;
        foreach (var g in Grips())
        {
            var (x, y) = cam.ToScreen(g.Point);
            if (cam.IsPlan)
            {
                // the text of the collar from 4 to 15 px above the vertex, that of the depth from 0 to 13 px below, both 5 to 50 px right
                if (sx < x + 3 || sx > x + 50) continue;
                if (g.Collar ? sy >= y - 16 && sy <= y - 1 : sy >= y && sy <= y + 13) return g;
                continue;
            }
            var d = Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy));
            if (d < bestD) { bestD = d; best = g; }
        }
        return best;
    }

    public void Hover(ToolPointer p)
    {
        var hit = _busy ? null : GripAt(p.X, p.Y);
        if (hit != Hot) { Hot = hit; _vm.View.Redraw(); }
        if (hit == null) return;
        var what = Loc.T(hit.Collar ? "Collar polygon" : "Depth polygon");
        var tip = _vm.View.Camera.IsPlan
            ? Loc.T("Double click: type the elevation")
            : Loc.T("Click: select (Ctrl+click: add or take away); drag: up or down, with the selected ones (Ctrl 0.01 m, Shift the whole polygon); double click: type the elevation");
        _vm.View.Preview.Labels.Add((hit.Point, $"{what}: {hit.Z:0.00} — {tip}"));
    }

    public bool TryPress(ToolPointer p)
    {
        if (_busy || !p.Left || _vm.Doc?.IsReadOnly != false || GripAt(p.X, p.Y) is not { } hit) return false;
        if (p.ClickCount >= 2) { _ = Type(hit); return true; }
        // on the plan a vertex is not dragged up or down: the press goes on as anywhere else
        if (_vm.View.Camera.IsPlan) return false;
        Pressed = hit;
        _down = p;
        _dragging = false;
        DraggedZ = null;
        DraggedDz = null;
        DragsSelection = IsSelected(hit) && _selected.Count > 1;
        return true;
    }

    /// <summary>Whether a vertex may join the selection: of the same pattern and polygon, and of a segment all the selected share.</summary>
    private bool Joins(Grip g)
    {
        var selected = Selected();
        if (selected.Count == 0) return true;
        if (selected.Any(x => x.Root != g.Root || x.Collar != g.Collar)) return false;
        var common = new HashSet<Guid>(g.Leaves);
        foreach (var x in selected) common.IntersectWith(x.Leaves);
        return common.Count > 0;
    }

    /// <summary>A click on a vertex: it alone selected, or with Ctrl added to the selection (taken away when it was in it).</summary>
    public void Click(Grip hit, bool add)
    {
        var key = (hit.Root, hit.Key, hit.Collar);
        // the selected vertices that are gone (another selection of holes, a polygon taken away) are no longer selected
        _selected.IntersectWith(Grips().Select(g => (g.Root, g.Key, g.Collar)));
        if (add && _selected.Remove(key)) { _vm.View.Redraw(); return; }
        if (add && !Joins(hit))
        {
            _vm.SetStatus(Loc.T("Only vertices of one polygon of one segment are selected together"));
            return;
        }
        if (!add) _selected.Clear();
        _selected.Add(key);
        _vm.View.Redraw();
    }

    /// <summary>The change of elevation a drag from the press gives: the screen movement along the vertical at the vertex, in steps.</summary>
    private double Dz(ToolPointer p)
    {
        var cam = _vm.View.Camera;
        var at = Pressed!.Point;
        var (x0, y0) = cam.ToScreen(at);
        var (x1, y1) = cam.ToScreen(at + new Vec3(0, 0, 1));
        double ux = x1 - x0, uy = y1 - y0, len2 = ux * ux + uy * uy;
        if (len2 < 1e-6) return 0;
        var dz = ((p.X - _down!.X) * ux + (p.Y - _down.Y) * uy) / len2;
        var step = p.Modifiers.HasFlag(KeyModifiers.Control) ? 0.01 : 0.1;
        return Math.Round(dz / step) * step;
    }

    public void Drag(ToolPointer p, PreviewOverlay preview)
    {
        if (Pressed is not { } hit || _down == null) return;
        if (!_dragging && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) <= DragPx) return;
        _dragging = true;
        var dz = Dz(p);
        var all = p.Modifiers.HasFlag(KeyModifiers.Shift);
        var values = Values(hit, dz, all);
        DraggedZ = hit.Z + dz;
        DraggedDz = dz;
        preview.Clear();
        if (_vm.Project is { } project && project.FindPattern(hit.Root) is { } root && Math.Abs(dz) > 1e-9)
        {
            try
            {
                var plan = PatternTree.Plan(project, root, (q, r) =>
                {
                    PatternSurfaces.FixVertices(q, r, hit.Collar, PatternRebuildOptions.For(q).CollarSurface);
                    foreach (var (key, z) in values) PatternSurfaces.SetVertex(r, key, hit.Collar ? z : null, hit.Collar ? null : z);
                    return PatternTree.Relayout(q, r, PatternRebuildOptions.For(q));
                });
                // where the collars go, or where the holes now end
                foreach (var m in plan.Moves)
                {
                    var now = hit.Collar ? m.Collar : m.Collar + m.Hole.Direction * m.Hole.Length;
                    preview.Markers.Add((now, Moved));
                }
            }
            catch (InvalidOperationException) { }
        }
        preview.Labels.Add((hit.Point, $"{hit.Z + dz:0.00}" + (all ? $" ({dz:+0.00;−0.00} " + Loc.T("the whole polygon") + ")" : "")));
        _vm.View.Redraw();
    }

    /// <summary>The new elevations of the vertices: the pressed one moved by <paramref name="dz"/>, or all the vertices of its polygon.</summary>
    private Dictionary<string, double> Values(Grip hit, double dz, bool all)
    {
        var values = new Dictionary<string, double>();
        if (!all && DragsSelection)
        {
            foreach (var g in Selected()) values[g.Key] = g.Z + dz;
            values[hit.Key] = hit.Z + dz;
            return values;
        }
        if (!all) { values[hit.Key] = hit.Z + dz; return values; }
        foreach (var g in Grips().Where(g => g.Root == hit.Root && g.Collar == hit.Collar)) values[g.Key] = g.Z + dz;
        return values;
    }

    public void Release(ToolPointer p)
    {
        if (Pressed is not { } hit) return;
        if (!_dragging)
        {
            Reset();
            Click(hit, p.Modifiers.HasFlag(KeyModifiers.Control));
            return;
        }
        var dz = Dz(p);
        var values = Values(hit, dz, p.Modifiers.HasFlag(KeyModifiers.Shift));
        // a vertex dragged on its own becomes the one selected
        if (!DragsSelection) { _selected.Clear(); _selected.Add((hit.Root, hit.Key, hit.Collar)); }
        Reset();
        _vm.View.Preview.Clear();
        if (Math.Abs(dz) > 1e-9) _ = Run(_vm.SetPolygonVertices(hit.Root, hit.Collar, values));
        _vm.View.Redraw();
    }

    /// <summary>Asks for the elevation of a vertex (of all the selected ones, when it is one of them) and sets it (one undo step).</summary>
    private async Task Type(Grip hit)
    {
        var title = Loc.T(hit.Collar ? "Collar polygon" : "Depth polygon");
        var keys = IsSelected(hit) ? Selected().Select(g => g.Key).ToList() : new List<string> { hit.Key };
        if (await Dialogs.PromptNumber(title, Loc.T("Elevation, m"), Math.Round(hit.Z, 3)) is double z && (keys.Count > 1 || Math.Abs(z - hit.Z) > 1e-9))
            await Run(_vm.SetPolygonVertices(hit.Root, hit.Collar, keys.ToDictionary(k => k, _ => z)));
    }

    private async Task Run(Task<bool> change)
    {
        _busy = true;
        try { await change; }
        catch (Exception ex) { Dialogs.ShowError(ex); }
        finally
        {
            _busy = false;
            _vm.View.Redraw();
        }
    }

    public void Cancel()
    {
        if (Pressed == null) return;
        Reset();
        _vm.View.Preview.Clear();
        _vm.View.Redraw();
    }

    private void Reset()
    {
        Pressed = null;
        DraggedZ = null;
        DraggedDz = null;
        DragsSelection = false;
        _down = null;
        _dragging = false;
    }

    /// <summary>On a vertex: type its elevation, or let it take its elevation from its neighbours again.</summary>
    public IEnumerable<MenuEntry>? ContextMenu(ToolPointer p)
    {
        if (_busy || GripAt(p.X, p.Y) is not { } hit) return null;
        bool Free() => !_busy && _vm.Doc?.IsReadOnly == false;
        return new List<MenuEntry>
        {
            MenuEntry.Item("Elevation…", () => _ = Type(hit), Free),
            MenuEntry.Item("From the vertices beside it", () => _ = Run(_vm.ClearPolygonVertex(hit.Root, hit.Collar, hit.Key)), Free)
        };
    }
}

/// <summary>The grips of <see cref="DepthPolygonGrips"/> in 3D, each in the colour of its polygon; the dragged one where the drag puts it.</summary>
public sealed class DepthPolygonGripsOverlay : IOverlay
{
    private readonly DepthPolygonGrips _grips;
    private readonly Func<(SKColor Collar, SKColor Depth)> _colours;

    private readonly Func<SKColor> _ring;

    public DepthPolygonGripsOverlay(DepthPolygonGrips grips, Func<(SKColor Collar, SKColor Depth)> colours, Func<SKColor> ring)
    {
        _grips = grips;
        _colours = colours;
        _ring = ring;
    }

    public DepthPolygonGrips Owner => _grips;

    public void Draw(SKCanvas c, Camera cam, BlastProject project)
    {
        if (cam.IsPlan) return;
        var grips = _grips.Grips();
        if (grips.Count == 0) return;
        var (collarColour, depthColour) = _colours();
        using var collar = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = PlanRenderer.Adapt(collarColour) };
        using var depth = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = PlanRenderer.Adapt(depthColour) };
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = PlanRenderer.DarkMode ? new SKColor(0xFF101215) : SKColors.White };
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = _ring() };
        foreach (var g in grips)
        {
            var pressed = _grips.Pressed is { } pr && pr.Root == g.Root && pr.Key == g.Key && pr.Collar == g.Collar;
            var selected = _grips.IsSelected(g);
            var moving = pressed || selected && _grips.DragsSelection;
            var at = moving && _grips.DraggedDz is double dz ? g.Point.WithZ(g.Z + dz) : g.Point;
            var (x, y) = cam.ToScreen(at);
            var hot = pressed || _grips.Hot is { } h && h.Root == g.Root && h.Key == g.Key && h.Collar == g.Collar;
            var r = VertexGripsOverlay.Radius + (hot ? 2 : 0);
            c.DrawCircle(x, y, r, g.Collar ? collar : depth);
            c.DrawCircle(x, y, r, edge);
            // a selected vertex: a ring round it in the selection colour
            if (selected) c.DrawCircle(x, y, r + 3, ring);
        }
    }
}
