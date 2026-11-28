using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.Tools;

/// <summary>
/// Renumber: a click on a hole gives it the number of the counter (<see cref="MainViewModel.RenumberCounter"/>), a drag
/// across holes gives them numbers in the order it passes them; the counter goes on by as many. The prefix of the panel,
/// or without one each hole's own (the name of its row), goes before the number; a number another hole has is swapped with
/// it (<see cref="HoleNumbering"/>). While dragging the holes show the numbers they will take. One undo step a click or a
/// drag, and the undo takes the counter back too.
/// </summary>
public sealed class RenumberTool : Tool
{
    private readonly List<Guid> _captured = new();
    private (double X, double Y)? _last;
    private bool _down;

    public RenumberTool(MainViewModel vm) : base(vm) { }

    public override string Name => "Renumber holes";
    public override string Hint => "Click a hole to give it the number of the counter, drag across holes to number them in order. With no prefix each hole keeps its own (the name of its row); a number another hole has is swapped with it.";

    protected override bool HasPendingInput => _down;

    public override void Activate()
    {
        base.Activate();
        // a numbering starts from 1 (the rows are told apart by their names)
        Vm.RenumberCounter = 1;
    }

    public override void Deactivate()
    {
        _down = false;
        _captured.Clear();
        base.Deactivate();
    }

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left) return;
        _down = true;
        _captured.Clear();
        _last = (p.X, p.Y);
        // a click takes the hole nearest to it
        if (HolesAlong(p.X, p.Y, p.X, p.Y).FirstOrDefault() is { } first) _captured.Add(first.Id);
        Show();
    }

    public override void PointerMoved(ToolPointer p)
    {
        if (!_down || _last is not var (lx, ly)) return;
        // the holes the way since the last move passes, in the order it passes them: a quick move misses none
        foreach (var h in HolesAlong(lx, ly, p.X, p.Y))
            if (!_captured.Contains(h.Id)) _captured.Add(h.Id);
        _last = (p.X, p.Y);
        Show();
    }

    public override void PointerReleased(ToolPointer p)
    {
        if (!_down) return;
        _down = false;
        Preview.Clear();
        var ids = _captured.ToList();
        _captured.Clear();
        if (ids.Count > 0) Vm.RenumberHoles(ids);
        View.Redraw();
    }

    public override void Cancel()
    {
        _down = false;
        _captured.Clear();
        base.Cancel();
        View.Redraw();
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        // + and − step the counter
        if (key is Key.Add or Key.OemPlus) { Vm.StepRenumberCounter(1); return true; }
        if (key is Key.Subtract or Key.OemMinus) { Vm.StepRenumberCounter(-1); return true; }
        return base.KeyDown(key, mods);
    }

    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p)
    {
        yield return MenuEntry.Item("Start at…", async () =>
        {
            if (await Dialogs.PromptNumber("Renumber holes", Loc.T("Next number"), Vm.RenumberCounter) is double v) Vm.SetRenumberCounter((int)Math.Round(v));
        });
        yield return MenuEntry.Item("Reset the counter to 1", () => Vm.SetRenumberCounter(1));
        yield return MenuEntry.Item("Renumber the selected holes by rows", Vm.RenumberSelectedByRows, () => Vm.Doc?.SelectedHoles.Any() == true);
        foreach (var e in base.ContextMenu(p)) yield return e;
    }

    /// <summary>
    /// The holes within the pick radius of the way from one screen point to another, in the order it passes them (the
    /// nearest first when they are passed together); the holes hidden or not to be picked are left out.
    /// </summary>
    private List<Hole> HolesAlong(double ax, double ay, double bx, double by)
    {
        var found = new List<(Hole Hole, double T, double D)>();
        if (Vm.Project is not { } project) return new List<Hole>();
        var cam = View.Camera;
        var basis = project.View.HoleBasis;
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        var reach = View.TargetRadius;
        foreach (var h in project.Holes)
        {
            if (!Vm.IsSelectable(h)) continue;
            var (x, y) = cam.ToScreen(h.DrawnCollar(basis));
            var t = l2 > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / l2, 0, 1) : 0;
            var d = Math.Sqrt(Math.Pow(ax + t * dx - x, 2) + Math.Pow(ay + t * dy - y, 2));
            if (d <= reach) found.Add((h, t, d));
        }
        return found.OrderBy(f => f.T).ThenBy(f => f.D).Select(f => f.Hole).ToList();
    }

    /// <summary>The holes taken so far with the numbers they will take, over them.</summary>
    private void Show()
    {
        Preview.Labels.Clear();
        Preview.Points.Clear();
        if (Vm.Project is not { } project) return;
        var basis = project.View.HoleBasis;
        var prefix = Vm.RenumberPrefixToApply;
        for (int i = 0; i < _captured.Count; i++)
            if (project.FindEntity(_captured[i]) is Hole h)
            {
                var at = h.DrawnCollar(basis);
                Preview.Points.Add((at, PreviewColor));
                Preview.Labels.Add((at, HoleNumbering.LabelFor(h.Label, prefix, Vm.RenumberCounter + i)));
            }
        View.Redraw();
    }
}
