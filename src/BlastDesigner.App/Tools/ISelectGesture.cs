using BlastDesigner.App.Controls;
using BlastDesigner.Rendering;

namespace BlastDesigner.App.Tools;

/// <summary>
/// One action of the Select tool (turning a front, moving a vertex, moving the selection, the box…). The tool keeps the order
/// in which they are asked and the conditions between them; each gesture keeps what it does with a press, a drag and a release.
/// </summary>
public interface ISelectGesture
{
    /// <summary>What the gesture draws on the plan while the tool is open (its circles, grips); null when nothing.</summary>
    IOverlay? Overlay { get; }

    /// <summary>The gesture holds the press in progress: the drag and the release go to it.</summary>
    bool Active { get; }

    /// <summary>The cursor moves with no button down: mark what is under it.</summary>
    void Hover(ToolPointer p);

    /// <summary>Takes a press; false when the press is not for it.</summary>
    bool TryPress(ToolPointer p);

    void Drag(ToolPointer p, PreviewOverlay preview);

    void Release(ToolPointer p);

    /// <summary>Drops the press in progress (Esc, another tool) without changing anything.</summary>
    void Cancel();

    /// <summary>The menu of a right click on what the gesture has under the cursor; null leaves the menu to the tool.</summary>
    IEnumerable<MenuEntry>? ContextMenu(ToolPointer p) => null;
}
