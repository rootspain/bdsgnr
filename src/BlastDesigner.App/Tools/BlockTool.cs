using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.App.Views;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.App.Tools;

/// <summary>
/// Block: three clicks — a corner, the end of the length (its direction is the azimuth), the width. The block is
/// <see cref="BlockEntity.DefaultHeight"/> high with the work plane through its middle, and goes on a layer of its own;
/// its size is then dragged by its grips. Ctrl takes the length and width in steps of 0.1 m.
/// </summary>
public sealed class BlockTool : Tool
{
    private readonly List<Vec3> _points = new();
    private Vec3? _hover;

    public BlockTool(MainViewModel vm) : base(vm) { }

    public override string Name => "Block";
    public override string Hint => "Click a corner of the block, then the end of its length, then its width. Ctrl: steps of 0.1 m.";

    /// <summary>Steps of the length and width with Ctrl, m.</summary>
    public const double FineStep = 0.1;

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left) return;
        _points.Add(Stepped(p));
        if (_points.Count == 3) { Finish(); return; }
        UpdatePreview();
    }

    public override void PointerMoved(ToolPointer p)
    {
        _hover = Stepped(p);
        UpdatePreview();
    }

    /// <summary>The point as picked; with Ctrl, at a whole number of 0.1 m from the last point along the length and across.</summary>
    private Vec3 Stepped(ToolPointer p)
    {
        var at = p.Snapped;
        if (!p.Modifiers.HasFlag(KeyModifiers.Control) || _points.Count == 0) return at;
        var a = _points[0];
        if (_points.Count == 1)
        {
            var d = at - a;
            var len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            if (len < 1e-9) return at;
            var stepped = Math.Max(FineStep, Math.Round(len / FineStep) * FineStep);
            return new Vec3(a.X + d.X / len * stepped, a.Y + d.Y / len * stepped, at.Z);
        }
        var v = BlockEntity.FromPoints(a, _points[1], a).V;
        var w = Vec3.Dot(at - a, v);
        var sw = Math.Round(w / FineStep) * FineStep;
        return at + v * (sw - w);
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (key == Key.Back && _points.Count > 0) { _points.RemoveAt(_points.Count - 1); UpdatePreview(); return true; }
        return base.KeyDown(key, mods);
    }

    protected override bool HasPendingInput => _points.Count > 0;

    public override void Cancel()
    {
        _points.Clear();
        base.Cancel();
    }

    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p)
    {
        yield return MenuEntry.Item("Cancel entry", Cancel, () => _points.Count > 0);
        foreach (var e in base.ContextMenu(p)) yield return e;
    }

    /// <summary>The block the points picked so far and the cursor make; null before the length is there.</summary>
    private BlockEntity? Shape()
    {
        var pts = _points.ToList();
        if (_hover is Vec3 h && pts.Count < 3) pts.Add(h);
        if (pts.Count < 2) return null;
        var a = pts[0];
        var b = pts[1];
        var d = b - a;
        if (Math.Sqrt(d.X * d.X + d.Y * d.Y) < 1e-6) return null;
        // before the width is picked the block is a wall along the length
        var c = pts.Count > 2 ? pts[2] : a;
        return BlockEntity.FromPoints(a, b, c);
    }

    /// <summary>"10.0 × 5.0 × 30.0 m": the length, width and height.</summary>
    public static string SizeText(BlockEntity b) => SizeText(b.Length, b.Width, b.Height);

    public static string SizeText(double length, double width, double height) => Loc.F("{0:0.0} × {1:0.0} × {2:0.0} m", length, width, height);

    private void UpdatePreview()
    {
        Preview.Lines.Clear();
        Preview.Points.Clear();
        Preview.Labels.Clear();
        foreach (var p in _points) Preview.Points.Add((p, PreviewColor));
        if (Shape() is { } block)
        {
            var corners = block.Corners();
            foreach (var (i, j) in BlockEntity.Edges) Preview.Lines.Add((new[] { corners[i], corners[j] }, false, PreviewColor));
            if (_hover is Vec3 hv) Preview.Labels.Add((hv, SizeText(block)));
        }
        View.Redraw();
    }

    private void Finish()
    {
        var a = _points[0];
        var b = _points[1];
        var c = _points[2];
        _points.Clear();
        Preview.Clear();
        var d = b - a;
        if (Math.Sqrt(d.X * d.X + d.Y * d.Y) < BlockEntity.MinSize) { Vm.SetStatus(Loc.T("The length of the block is too short.")); return; }
        var block = BlockEntity.FromPoints(a, b, c);
        Vm.Change("Add block", p => p.Add(block, BlockLayers.NewLayer(p, BlockLayers.BaseName)));
        // the new block is selected, its grips ready to size it
        Vm.SelectTool();
        Vm.Doc?.Select(new[] { block.Id });
        Vm.SetStatus($"{Vm.Project?.FindLayer(block.LayerId)?.Name}: {SizeText(block)}");
        View.Redraw();
    }
}
