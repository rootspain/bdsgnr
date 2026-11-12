using BlastDesigner.App.Controls;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;
using BlastDesigner.Rendering;

namespace BlastDesigner.App.Tools;

/// <summary>
/// The line of a row, with nothing else under the cursor than a surface (picked by area) or a tie along the row: a selected
/// row is dragged by it (by <see cref="SelectionDrag"/>), an unselected one is selected by a click on it (a tie keeps its click).
/// </summary>
public sealed class RowPick : ISelectGesture
{
    private readonly MainViewModel _vm;
    private readonly SelectionDrag _move;
    /// <summary>Row whose line was pressed (between its holes): a drag moves it when selected, a click selects it.</summary>
    private HoleRow? _row;

    public RowPick(MainViewModel vm, SelectionDrag move)
    {
        _vm = vm;
        _move = move;
    }

    private Viewport View => _vm.View;

    public IOverlay? Overlay => null;

    /// <summary>Never: the drag of a selected row is the drag of the selection.</summary>
    public bool Active => false;

    public void Hover(ToolPointer p) { }

    /// <summary>Notes the row whose line is pressed, and hands a selected one to the move; true when there is one.</summary>
    public bool TryPress(ToolPointer p)
    {
        _row = p.Hit is null or SurfaceEntity || (p.Hit is TieEntity && !_vm.Doc!.Selection.Contains(p.Hit.Id)) ? RowAt(p.X, p.Y) : null;
        if (_row != null)
        {
            var selected = _row.HoleIds.All(_vm.Doc!.Selection.Contains);
            if (selected) _move.PressRow(true);
            else if (p.Hit is TieEntity) _row = null;
            else _move.PressRow(false);
        }
        return _row != null;
    }

    /// <summary>A click on the line of a row selects its holes; false when the press was on none.</summary>
    public bool Click(SelectionMode mode)
    {
        if (_row == null) return false;
        _vm.Doc!.Select(_row.HoleIds, mode);
        _row = null;
        return true;
    }

    public void Drag(ToolPointer p, PreviewOverlay preview) { }

    public void Release(ToolPointer p) { }

    public void Cancel() => _row = null;

    /// <summary>
    /// Row whose line (through its holes, in order) passes within the pick radius of the screen point, when rows are
    /// shown; the nearest one.
    /// </summary>
    private HoleRow? RowAt(double sx, double sy)
    {
        var p = _vm.Project!;
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
}
