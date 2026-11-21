using System.Globalization;
using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Tools.Section;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using SkiaSharp;

namespace BlastDesigner.App.Tools;

/// <summary>
/// Selection in the Section view window: a click picks what the section shows under the cursor (a hole by its shaft,
/// a surface by the line of its cut, anything else lying on the line), a drag draws a box (from right to left a crossing).
/// Shift adds, Ctrl toggles, Esc clears. The objects of the blast are selected in the project, so the main window and the
/// properties show them too. The marks drawn by hand on the section are picked first and selected in the window; a grip
/// of a selected mark moves its point (with the snaps), Delete deletes them, Ctrl+C and Ctrl+V copy them, a double click
/// edits the figure (the height of a level, the words of a text).
/// </summary>
public sealed class SectionSelectTool : Tool
{
    private readonly Viewport _view;
    private readonly SectionSession? _s;
    private ToolPointer? _down;
    private bool _dragging;
    /// <summary>The grip being dragged: its mark and the index of its point.</summary>
    private (Guid Mark, int Index)? _grip;
    /// <summary>The press is on a selected mark: a drag moves the selected marks.</summary>
    private bool _moving;
    /// <summary>The marks copied with Ctrl+C.</summary>
    private static List<SectionMark> _copied = new();

    /// <summary>A drag shorter than this, px, is a click.</summary>
    private const double DragStart = 4;

    public SectionSelectTool(MainViewModel vm, Viewport view, SectionSession? session = null) : base(vm)
    {
        _view = view; _s = session;
    }

    public override string Name => "Select";

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left || Vm.Doc == null) return;
        _down = p;
        _dragging = false;
        _grip = _s?.Marks?.GripAt(p.X, p.Y, 6) is { } g ? (g.Mark.Id, g.Index) : null;
        _moving = _grip == null && _s?.Marks?.HitTest(p.X, p.Y, _view.TargetRadius) is { } under && _s.SelectedMarks.Contains(under.Id);
        if (p.ClickCount >= 2 && _s?.Marks?.HitTest(p.X, p.Y, _view.TargetRadius) is { } mark)
        {
            _down = null;
            Edit(mark, p);
        }
    }

    public override void PointerMoved(ToolPointer p)
    {
        if (_s != null) _s.LastPointer = new SKPoint((float)p.X, (float)p.Y);
        if (_down is not { } down) return;
        if (!_dragging && Math.Abs(p.X - down.X) + Math.Abs(p.Y - down.Y) < DragStart) return;
        _dragging = true;
        if (_grip is { } grip && _s != null)
        {
            // the point of the mark follows the cursor, snapped: shown as the preview until it is let go
            if (_s.Project?.SectionMarks.FirstOrDefault(m => m.Id == grip.Mark) is { } mark)
            {
                var moved = mark.Copy();
                moved.Points[grip.Index] = _s.Pick(p.X, p.Y, null, p.Modifiers.HasFlag(KeyModifiers.Shift)).Point;
                _s.Preview = moved;
                _view.Redraw();
            }
            return;
        }
        if (_moving && _s != null)
        {
            // the selected marks follow the cursor; Shift keeps the move straight across or up
            var from = _s.Free(down.X, down.Y);
            var to = _s.Free(p.X, p.Y);
            double ds = to.S - from.S, dz = to.Z - from.Z;
            if (p.Modifiers.HasFlag(KeyModifiers.Shift)) { if (Math.Abs(ds) >= Math.Abs(dz)) dz = 0; else ds = 0; }
            _s.MoveBy = (ds, dz);
            _view.Redraw();
            return;
        }
        _view.Preview.ScreenRect = ((float)down.X, (float)down.Y, (float)p.X, (float)p.Y);
        _view.Preview.Crossing = p.X < down.X;
    }

    public override void PointerReleased(ToolPointer p)
    {
        if (_down is not { } down || Vm.Doc is not { } doc) return;
        _down = null;
        var mode = SelectTool.Mode(p);
        if (_moving && _s != null)
        {
            _moving = false;
            if (_s.MoveBy is { } by)
            {
                _s.MoveBy = null;
                _s.MoveSelected(by.S, by.Z);
            }
            if (_dragging) { _dragging = false; return; }
        }
        if (_grip is { } grip && _s != null)
        {
            _grip = null;
            if (_s.Preview is { } moved)
            {
                var point = moved.Points[grip.Index];
                _s.Preview = null;
                doc.Execute("Move section mark point", pr => { if (pr.SectionMarks.FirstOrDefault(m => m.Id == grip.Mark) is { } m) m.Points[grip.Index] = point; });
                _s.Touch();
            }
            if (_dragging) { _dragging = false; return; }
        }
        if (_dragging)
        {
            _dragging = false;
            _view.Preview.ScreenRect = null;
            _view.Preview.Crossing = null;
            var crossing = p.X < down.X;
            var box = new SKRect((float)Math.Min(down.X, p.X), (float)Math.Min(down.Y, p.Y), (float)Math.Max(down.X, p.X), (float)Math.Max(down.Y, p.Y));
            var marks = _s?.Marks?.InRect(box, crossing).Select(m => m.Id).ToList() ?? new();
            SelectMarks(marks, mode);
            // automatic dimensions in the box: made marks of their own and selected with the rest
            if (_s != null && _s.AutoDimensionsIn(box, crossing) is { Count: > 0 } autos) _s.TakeOverAutoDimensions(autos);
            doc.Select(_view.InRect(down.X, down.Y, p.X, p.Y, crossing).Select(e => e.Id).ToList(), mode);
            return;
        }
        // a click: a mark drawn by hand first, then what the section shows there; on nothing it clears. On the shaft of a
        // hole the hole: the lines of a dimension run along the shafts, its figure picks it there
        var under = _view.HitTest(p.X, p.Y);
        var onShaft = under is Hole shaft && Tools.Section.SectionSession.SegmentDistance(_s?.ToScreen(shaft.Collar) ?? default, _s?.ToScreen(shaft.Toe) ?? default, new SKPoint((float)p.X, (float)p.Y)) <= 3;
        var hitMark = onShaft ? _s?.Marks?.FigureAt(p.X, p.Y) : _s?.Marks?.HitTest(p.X, p.Y, _view.TargetRadius);
        if (hitMark != null)
        {
            SelectMarks(new[] { hitMark.Id }, mode);
            if (mode == SelectionMode.Replace) doc.Select(Array.Empty<Guid>());
            return;
        }
        if (mode == SelectionMode.Replace) SelectMarks(Array.Empty<Guid>(), mode);
        // an automatic dimension: the dimensions of the section become marks of their own, this one selected
        if (!onShaft && _s?.AutoDimensionAt(p.X, p.Y, _view.TargetRadius / 2) is int auto)
        {
            _s.TakeOverAutoDimensions(new[] { auto });
            if (mode == SelectionMode.Replace) doc.Select(Array.Empty<Guid>());
            return;
        }
        if (under is { } hit) doc.Select(new[] { hit.Id }, mode);
        else if (mode == SelectionMode.Replace) doc.Select(Array.Empty<Guid>());
    }

    private void SelectMarks(IEnumerable<Guid> ids, SelectionMode mode)
    {
        if (_s == null) return;
        var set = _s.SelectedMarks;
        if (mode == SelectionMode.Replace) set.Clear();
        foreach (var id in ids)
        {
            if (mode == SelectionMode.Toggle && !set.Add(id)) set.Remove(id);
            else if (mode == SelectionMode.Remove) set.Remove(id);
            else set.Add(id);
        }
        _s.Touch();
    }

    /// <summary>A double click on a mark: the height of a level, the words of a text, else the text in place of the value.</summary>
    private void Edit(SectionMark mark, ToolPointer p)
    {
        if (_s?.AskAt is not { } ask) return;
        var at = new SKPoint((float)p.X, (float)p.Y);
        if (mark.Kind == SectionMarkKind.Level && mark.Points.Count > 0)
            ask(at, "Level Z", SectionMarking.Number(mark.Points[0].Z, 2), typed =>
            {
                if (!double.TryParse(typed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return;
                Change(mark, m => { m.Points[0].Z = z; m.Points[0].HoleId = null; m.Points[0].SurfaceId = null; });
            });
        else if (mark.Kind == SectionMarkKind.Text)
            ask(at, "Text", mark.Text ?? "", typed => { if (!string.IsNullOrWhiteSpace(typed)) Change(mark, m => m.Text = typed.Trim()); });
        else
            ask(at, "Own text (<> is the value)", mark.Style.Text ?? "", typed => Change(mark, m => m.Style.Text = string.IsNullOrWhiteSpace(typed) ? null : typed));
    }

    private void Change(SectionMark mark, Action<SectionMark> change)
    {
        Vm.Doc?.Execute("Edit section mark", pr => { if (pr.SectionMarks.FirstOrDefault(m => m.Id == mark.Id) is { } m) change(m); });
        _s?.Touch();
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (Vm.Doc is not { } doc) return false;
        if (_s != null && key == Key.Delete && _s.SelectedMarks.Count > 0) { _s.DeleteSelected(); return true; }
        // the arrows move the selected marks 0.1 m, with Ctrl 0.01 m
        if (_s != null && _s.SelectedMarks.Count > 0 && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var step = mods.HasFlag(KeyModifiers.Control) ? 0.01 : 0.1;
            _s.MoveSelected(key == Key.Left ? -step : key == Key.Right ? step : 0, key == Key.Up ? step : key == Key.Down ? -step : 0);
            return true;
        }
        if (_s != null && mods.HasFlag(KeyModifiers.Control) && key == Key.C && _s.SelectedMarks.Count > 0)
        {
            _copied = _s.Selected().Select(m => m.Copy()).ToList();
            return true;
        }
        if (_s != null && mods.HasFlag(KeyModifiers.Control) && key == Key.V && _copied.Count > 0)
        {
            // the copies a little down and to the right of the originals, held on nothing: they are drawn where they are put
            var shift = 12 / _view.Camera.Scale;
            var copies = _copied.Select(m =>
            {
                var c = m.Copy();
                c.Id = Guid.NewGuid();
                foreach (var pt in c.Points)
                {
                    var spot = _s.Project is { } pr ? SectionMarking.Resolve(pr, _s.Frame, pt) : new SectionSpot(pt.S, pt.Z);
                    pt.S = spot.S + shift; pt.Z = spot.Z - shift; pt.HoleId = null; pt.SurfaceId = null;
                }
                return c;
            }).ToList();
            doc.Execute("Paste section marks", p =>
            {
                var line = _s.EnsureLine(p);
                foreach (var c in copies) { c.SectionLineId = line; p.SectionMarks.Add(c); }
            });
            _s.SelectedMarks.Clear();
            foreach (var c in copies) _s.SelectedMarks.Add(c.Id);
            _s.Touch();
            return true;
        }
        if (key != Key.Escape) return false;
        if (_down != null) { Cancel(); return true; }
        if (_s != null && _s.SelectedMarks.Count > 0) { _s.SelectedMarks.Clear(); _s.Touch(); return true; }
        if (doc.Selection.Count == 0) return false;
        doc.Select(Array.Empty<Guid>());
        return true;
    }

    public override void Cancel()
    {
        _down = null;
        _dragging = false;
        _grip = null;
        _moving = false;
        if (_s != null) { _s.Preview = null; _s.MoveBy = null; }
        _view.Preview.ScreenRect = null;
        _view.Preview.Crossing = null;
    }

    /// <summary>No tool items: the window's view items (Zoom all) only; with marks selected, delete them.</summary>
    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p)
    {
        if (_s != null && _s.SelectedMarks.Count > 0) yield return MenuEntry.Item("Delete", _s.DeleteSelected);
    }
}
