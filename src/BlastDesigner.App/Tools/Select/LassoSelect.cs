using BlastDesigner.App.Controls;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Rendering;

namespace BlastDesigner.App.Tools;

/// <summary>
/// A drag on the screen draws a curve; let go, it closes from its end to its start and selects what it rings. Like a box, the
/// first move decides: to the right a window (only what is fully inside), to the left a crossing (what it touches too).
/// </summary>
public sealed class LassoSelect : ISelectGesture
{
    /// <summary>How far the cursor goes from the last point of the curve before it adds the next, px: a shaking hand adds none.</summary>
    public const double Step = 3;

    private readonly MainViewModel _vm;
    private readonly List<(double X, double Y)> _points = new();
    private bool _down;

    public LassoSelect(MainViewModel vm) => _vm = vm;

    public IOverlay? Overlay => null;

    /// <summary>A press nothing else took: a drag draws the curve.</summary>
    public bool Active => _down;

    /// <summary>The curve drawn so far, on the screen.</summary>
    public IReadOnlyList<(double X, double Y)> Points => _points;

    public void Hover(ToolPointer p) { }

    /// <summary>Any press can start a lasso.</summary>
    public bool TryPress(ToolPointer p)
    {
        _points.Clear();
        _points.Add((p.X, p.Y));
        _down = true;
        return true;
    }

    public void Drag(ToolPointer p, PreviewOverlay preview)
    {
        if (!_down) return;
        Add(p.X, p.Y);
        preview.ScreenPath = _points.Select(q => ((float)q.X, (float)q.Y)).ToList();
        preview.ScreenCursor = ((float)p.X, (float)p.Y);
        preview.Crossing = Lasso.IsCrossing(_points);
    }

    public void Release(ToolPointer p)
    {
        if (!_down) return;
        Add(p.X, p.Y);
        _down = false;
        var crossing = Lasso.IsCrossing(_points);
        var shape = new Lasso(_points);
        _points.Clear();
        _vm.Doc!.Select(_vm.View.InShape(shape, crossing).Select(e => e.Id), SelectTool.Mode(p));
    }

    public void Cancel()
    {
        _down = false;
        _points.Clear();
    }

    private void Add(double x, double y)
    {
        var (lx, ly) = _points[^1];
        if (Math.Abs(x - lx) + Math.Abs(y - ly) >= Step) _points.Add((x, y));
    }
}
