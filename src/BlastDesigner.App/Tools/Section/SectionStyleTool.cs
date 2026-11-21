using BlastDesigner.App.Controls;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.App.Tools.Section;

/// <summary>
/// Apply style on the section: the style of the mark that was selected when Copy style was pressed goes to each mark
/// clicked (the colour, line type and weight, ends, figure, angle and slope as written; its own text stays). Esc or the
/// button pressed again ends it.
/// </summary>
public sealed class SectionStyleTool : Tool
{
    private readonly SectionSession _s;
    private readonly SectionMarkStyle _style;

    public SectionStyleTool(MainViewModel vm, SectionSession session, SectionMarkStyle style) : base(vm)
    {
        _s = session;
        _style = style.Copy();
    }

    public override string Name => "Apply section style";

    /// <summary>The hint of the tool, shown when it is taken.</summary>
    public void Start() => _s.SetHint("Apply style: click the marks to give it to · Esc ends");

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left || _s.Marks?.HitTest(p.X, p.Y, _s.View.TargetRadius) is not { } mark) return;
        var style = _style;
        Vm.Doc?.Execute("Copy section style", pr =>
        {
            if (pr.SectionMarks.FirstOrDefault(m => m.Id == mark.Id) is not { } m) return;
            var own = m.Style.Text;
            m.Style = style.Copy();
            m.Style.Text = own;
        });
        _s.Touch();
    }

    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p) => Enumerable.Empty<MenuEntry>();
}
