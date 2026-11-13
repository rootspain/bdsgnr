using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.App.Tools;

/// <summary>
/// Round grips of a selected block: its 8 corners and the middles of its 6 faces. A corner drags the length and the width on
/// the ground (the opposite corner stays), with Shift its top or bottom; a face drags its own side only. Ctrl: steps of
/// 0.1 m. The block changes on the screen while it is dragged, what it shows with it; the release is one undo step.
/// </summary>
public sealed class BlockGrips : ISelectGesture
{
    /// <summary>Screen distance a press has to travel before it is a drag, px.</summary>
    private const double DragPx = 3;

    private readonly MainViewModel _vm;
    private (BlockEntity Block, int Grip, BlockEntity.Shape Before)? _drag;
    private ToolPointer? _down;
    private bool _dragging;

    public BlockGrips(MainViewModel vm, Func<bool> visible)
    {
        _vm = vm;
        Overlay = new BlockGripsOverlay(() => visible() ? Blocks() : new List<BlockEntity>(), () => new SKColor(vm.Settings.SelectionColor));
    }

    public BlockGripsOverlay Overlay { get; }
    IOverlay? ISelectGesture.Overlay => Overlay;

    public bool Active => _drag != null;

    private Viewport View => _vm.View;

    /// <summary>The selected blocks whose size can be dragged: on a visible layer that is neither locked nor frozen.</summary>
    public List<BlockEntity> Blocks() =>
        _vm.Doc == null ? new() : _vm.Doc.SelectedEntities.OfType<BlockEntity>().Where(b => _vm.Project!.FindLayer(b.LayerId) is { IsEditable: true }).ToList();

    /// <summary>The grip at a screen point: the nearest; of grips on one spot (the top over the bottom in the plan) the top one.</summary>
    public (BlockEntity Block, int Grip)? GripAt(double sx, double sy)
    {
        (BlockEntity, int)? best = null;
        var bestD = VertexGripsOverlay.Reach + 1;
        foreach (var b in Blocks())
            for (int g = BlockEntity.GripCount - 1; g >= 0; g--)
            {
                var (x, y) = View.Camera.ToScreen(b.Grip(g));
                var d = Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy));
                // nearer, or as near and above (the top grip of the plan)
                if (d < bestD - 1e-6) { bestD = d; best = (b, g); }
            }
        return best;
    }

    public void Hover(ToolPointer p) => Overlay.Hot = GripAt(p.X, p.Y) is { } hit ? (hit.Block.Id, hit.Grip) : null;

    public bool TryPress(ToolPointer p)
    {
        if (!p.Left || p.ClickCount >= 2 || GripAt(p.X, p.Y) is not { } hit) return false;
        _drag = (hit.Block, hit.Grip, hit.Block.Current);
        _down = p;
        _dragging = false;
        return true;
    }

    /// <summary>
    /// Where the cursor takes the grip, from where it was at the press: on its level for the length and the width (the point
    /// under the cursor); for the height up or down by how far the cursor went up the screen, a vertical metre being
    /// Up.Z · scale pixels (in the plan, where the vertical runs along the line of sight, the scale itself).
    /// </summary>
    private Vec3 Target(BlockEntity start, int grip, ToolPointer p, bool vertical)
    {
        var at = start.Grip(grip);
        var cam = View.Camera;
        if (!vertical) return cam.ToWorld(p.X, p.Y, at.Z);
        var up = Math.Abs(cam.Up.Z) < 1e-6 ? 1 : cam.Up.Z;
        return at + new Vec3(0, 0, (_down!.Y - p.Y) / (cam.Scale * up));
    }

    public void Drag(ToolPointer p, PreviewOverlay preview)
    {
        if (_drag is not { } drag || _down == null) return;
        if (!_dragging && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) <= DragPx) return;
        _dragging = true;
        var (block, grip, before) = drag;
        var vertical = BlockEntity.GripIsVertical(grip, p.Modifiers.HasFlag(KeyModifiers.Shift));
        var fine = p.Modifiers.HasFlag(KeyModifiers.Control);
        // from the shape at the press: the grip goes where the cursor is, not by the sum of the moves
        var start = new BlockEntity { Current = before };
        // shown live, with what it lets through; the model takes it on the release, as one step
        block.Current = start.Dragged(grip, Target(start, grip, p, vertical), vertical, fine);
        preview.Labels.Clear();
        preview.Labels.Add((block.Grip(grip), BlockTool.SizeText(block)));
        View.Redraw();
    }

    public void Release(ToolPointer p)
    {
        if (_drag is not { } drag) return;
        _drag = null;
        _down = null;
        var (block, _, before) = drag;
        View.Preview.Labels.Clear();
        if (!_dragging) return;
        _dragging = false;
        var after = block.Current;
        // back as it was, so the step holds the shape before it for the undo
        block.Current = before;
        if (after == before) { View.Redraw(); return; }
        var id = block.Id;
        _vm.Change("Block size", pr => { if (pr.FindEntity(id) is BlockEntity b) b.Current = after; });
        _vm.SetStatus(Loc.F("Block {0}", BlockTool.SizeText(after.Length, after.Width, after.Height)));
    }

    public void Cancel()
    {
        if (_drag is { } drag && _dragging) drag.Block.Current = drag.Before;
        _drag = null;
        _down = null;
        _dragging = false;
        View.Preview.Labels.Clear();
        View.Redraw();
    }
}

/// <summary>The grips of the selected blocks: circles at the corners, smaller ones at the middles of the faces.</summary>
public sealed class BlockGripsOverlay : IOverlay
{
    private readonly Func<IReadOnlyList<BlockEntity>> _blocks;
    private readonly Func<SKColor> _color;

    public BlockGripsOverlay(Func<IReadOnlyList<BlockEntity>> blocks, Func<SKColor> color)
    {
        _blocks = blocks;
        _color = color;
    }

    /// <summary>The grip under the cursor, drawn larger.</summary>
    public (Guid Block, int Grip)? Hot { get; set; }

    public void Draw(SKCanvas c, Camera cam, BlastProject project)
    {
        var blocks = _blocks();
        if (blocks.Count == 0) return;
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = _color() };
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = PlanRenderer.DarkMode ? SKColors.Black : SKColors.White };
        foreach (var b in blocks)
            for (int g = 0; g < BlockEntity.GripCount; g++)
            {
                var (x, y) = cam.ToScreen(b.Grip(g));
                var r = (g < 8 ? VertexGripsOverlay.Radius + 0.5f : VertexGripsOverlay.Radius - 0.5f) + (Hot is { } hot && hot.Block == b.Id && hot.Grip == g ? 2 : 0);
                c.DrawCircle(x, y, r, fill);
                c.DrawCircle(x, y, r, ring);
            }
    }
}
