using BlastDesigner.App.Controls;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Rendering;

namespace BlastDesigner.App.Tools;

/// <summary>A drag on the screen selects what is in the box; drawn from right to left it takes what it crosses too.</summary>
public sealed class BoxSelect : ISelectGesture
{
    private readonly MainViewModel _vm;
    private ToolPointer? _down;

    public BoxSelect(MainViewModel vm) => _vm = vm;

    public IOverlay? Overlay => null;

    /// <summary>A press nothing else took: a drag draws the box.</summary>
    public bool Active => _down != null;

    public void Hover(ToolPointer p) { }

    /// <summary>Any press can start a box.</summary>
    public bool TryPress(ToolPointer p)
    {
        _down = p;
        return true;
    }

    public void Drag(ToolPointer p, PreviewOverlay preview)
    {
        if (_down != null) preview.ScreenRect = ((float)_down.X, (float)_down.Y, (float)p.X, (float)p.Y);
    }

    public void Release(ToolPointer p)
    {
        if (_down is not { } down) return;
        _down = null;
        var crossing = p.X < down.X;
        _vm.Doc!.Select(_vm.View.InRect(down.X, down.Y, p.X, p.Y, crossing).Select(e => e.Id), SelectTool.Mode(p));
    }

    public void Cancel() => _down = null;
}
