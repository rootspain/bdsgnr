using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using SkiaSharp;

namespace BlastDesigner.App.Tools;

/// <summary>
/// Makes the selected polygons into patterns of holes. The holes to be made show on the plan as the panel changes; the first
/// row of each polygon runs along its side drawn in colour (the longest at first), a click on another side takes that one.
/// Done (Enter) makes the patterns.
/// </summary>
public sealed class PolygonPatternTool : Tool
{
    /// <summary>Colour of the side the first row runs along, and of the arrow of the blast across it.</summary>
    public static readonly SKColor FaceColor = new(0xFFF59E0B);

    private readonly List<Guid> _polygons;
    private readonly Dictionary<Guid, int> _edges = new();
    private (Guid Polygon, int Edge)? _hot;

    public PolygonPatternTool(MainViewModel vm, List<Guid> polygons) : base(vm)
    {
        _polygons = polygons;
        foreach (var (id, poly) in Polygons()) _edges[id] = HoleDesign.LongestEdge(poly.Points);
    }

    public override string Name => "Pattern from polygon";
    public override string Hint => "The polygon becomes a pattern of holes. The first row runs along the side in colour, half a burden inside it, and the blast goes out across it; click another side to take it. Done makes the pattern; editing the polygon later lays the pattern out on it again.";

    /// <summary>The side of each polygon its first row runs along.</summary>
    public IReadOnlyDictionary<Guid, int> Edges => _edges;

    /// <summary>The holes the preview shows.</summary>
    public int PreviewCount { get; private set; }

    public IReadOnlyList<(Guid Polygon, int Edge)> Plan => _polygons.Where(_edges.ContainsKey).Select(id => (id, _edges[id])).ToList();

    private IEnumerable<(Guid Id, PolylineEntity Poly)> Polygons()
    {
        foreach (var id in _polygons)
            if (Vm.Project?.FindEntity(id) is PolylineEntity { Closed: true, Points.Count: >= 3 } poly) yield return (id, poly);
    }

    public override void Activate()
    {
        base.Activate();
        Vm.PatternSetupChanged += Refresh;
        if (Vm.Doc != null) Vm.Doc.Changed += OnChanged;
        Refresh();
    }

    public override void Deactivate()
    {
        Vm.PatternSetupChanged -= Refresh;
        if (Vm.Doc != null) Vm.Doc.Changed -= OnChanged;
        base.Deactivate();
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>The preview again: the holes as the panel has them, the sides of the first rows and the blast across them.</summary>
    public void Refresh()
    {
        Preview.Clear();
        var holes = Vm.PreviewPolygonPatterns(Plan);
        foreach (var h in holes) Preview.Markers.Add((h.Collar, PreviewColor));
        var burden = Math.Max(Vm.Project?.Design.Burden ?? 1, 0.5);
        foreach (var (id, poly) in Polygons())
        {
            if (!_edges.TryGetValue(id, out var k) || k >= poly.Points.Count) continue;
            var (a, b) = Side(poly, k);
            Preview.Lines.Add((new[] { a, b }, false, FaceColor));
            // the arrow of the blast: out of the polygon across the side, from its middle
            var dir = GeoMath.HoleDirection(HoleDesign.OutwardBearing(poly.Points, k), 90).WithZ(0);
            var mid = Vec3.Lerp(a, b, 0.5);
            var tip = mid + dir * (burden * 1.5);
            var back = dir * (-burden * 0.4);
            var side = new Vec3(-dir.Y, dir.X, 0) * (burden * 0.25);
            Preview.Lines.Add((new[] { mid, tip }, false, FaceColor));
            Preview.Lines.Add((new[] { tip + back + side, tip, tip + back - side }, false, FaceColor));
        }
        if (_hot is var (hp, hk) && Vm.Project?.FindEntity(hp) is PolylineEntity { Points.Count: >= 3 } hotPoly && hk < hotPoly.Points.Count && _edges.GetValueOrDefault(hp, -1) != hk)
        {
            var (a, b) = Side(hotPoly, hk);
            Preview.Guides.Add((new[] { a, b }, FaceColor));
        }
        PreviewCount = holes.Count;
        var rows = holes.Select(h => (h.PatternId, h.GridJ)).Distinct().Count();
        Vm.SetStatus(Loc.F("{0} holes in {1} rows", holes.Count, rows));
        View.Redraw();
    }

    private static (Vec3 A, Vec3 B) Side(PolylineEntity poly, int k) => (poly.Points[k], poly.Points[(k + 1) % poly.Points.Count]);

    /// <summary>The side of one of the polygons nearest the cursor on the screen, within the pick radius.</summary>
    public (Guid Polygon, int Edge)? SideAt(double x, double y)
    {
        (Guid, int)? best = null;
        var reach = View.TargetRadius * 1.5;
        foreach (var (id, poly) in Polygons())
            for (int k = 0; k < poly.Points.Count; k++)
            {
                var (a, b) = Side(poly, k);
                var (ax, ay) = View.Camera.ToScreen(a);
                var (bx, by) = View.Camera.ToScreen(b);
                double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
                var t = len2 > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0, 1) : 0;
                double cx = ax + t * dx - x, cy = ay + t * dy - y, d = Math.Sqrt(cx * cx + cy * cy);
                if (d <= reach) (best, reach) = ((id, k), d);
            }
        return best;
    }

    public override void PointerMoved(ToolPointer p)
    {
        var hot = SideAt(p.X, p.Y);
        if (hot == _hot) return;
        _hot = hot;
        Refresh();
    }

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left || SideAt(p.X, p.Y) is not var (id, k)) return;
        _edges[id] = k;
        Refresh();
    }

    /// <summary>Done: the patterns are made, and the Select tool takes over with the new holes selected.</summary>
    public override bool FinishEntry()
    {
        Vm.MakePolygonPatterns(Plan);
        return true;
    }
}
