using System.Globalization;
using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.Tools.Section;

/// <summary>
/// Draws a mark of one kind on the section, by clicks with the snaps of the window:
/// <list type="bullet">
/// <item>a dimension (across, up, along, with a shelf): two points and where its line runs; the chain goes on from the
/// last point, one click a dimension;</item>
/// <item>an angle: a line, a second line (or H level, V upright), where the arc runs (Tab: as drawn, acute, obtuse);</item>
/// <item>a slope: a line and where its figure stands;</item>
/// <item>a level: a click, or Enter and its height typed;</item>
/// <item>a height: the point and its shelf; a line: its points, a double click or Enter ends it; a text: a click and its words.</item>
/// </list>
/// Esc drops what is begun; with nothing begun it is left to the window, which goes back to selecting.
/// </summary>
public sealed class SectionMarkTool : Tool
{
    private readonly SectionSession _s;
    private readonly SectionMarkKind _kind;
    private readonly bool _chain;
    private readonly List<SectionPoint> _points = new();
    private SectionPiece? _line1, _line2;
    private SectionAngleMode _mode = SectionAngleMode.AsDrawn;
    /// <summary>The position of the dimension line of a chain, kept from one dimension to the next.</summary>
    private SectionPoint? _chainLine;

    public SectionMarkTool(MainViewModel vm, SectionSession session, SectionMarkKind kind, bool chain = false) : base(vm)
    {
        _s = session; _kind = kind; _chain = chain;
    }

    public SectionMarkKind Kind => _kind;
    public bool Chain => _chain;
    public override string Name => _chain ? "Chain dimension" : "Section " + _kind;

    /// <summary>The hint of the first step, shown when the tool is taken.</summary>
    public void Start() => _s.SetHint(StepHint());

    private string StepHint() => _kind switch
    {
        SectionMarkKind.Angle when _line1 == null => "Angle: pick the first line",
        SectionMarkKind.Angle when _line2 == null => "Angle: pick the second line, or H for the level, V for the upright",
        SectionMarkKind.Angle => "Angle: click where the arc goes · Tab: as drawn, acute, obtuse",
        SectionMarkKind.Slope when _line1 == null => "Slope: pick the line",
        SectionMarkKind.Slope => "Slope: click where the figure goes",
        SectionMarkKind.Level => "Level: click a point, or press Enter and type the height",
        SectionMarkKind.Text => "Text: click where it goes",
        SectionMarkKind.Polyline when _points.Count == 0 => "Line: click its first point",
        SectionMarkKind.Polyline => "Line: click the next point · double click or Enter ends it",
        SectionMarkKind.PointZ when _points.Count == 0 => "Height: click the point",
        SectionMarkKind.PointZ => "Height: click where its shelf goes",
        SectionMarkKind.Leader when _points.Count == 2 => "Leader: click where the figure goes",
        _ when _chain && _chainLine != null => "Chain: click the next point · Esc ends it",
        _ when _points.Count == 0 => "Dimension: click the first point",
        _ when _points.Count == 1 => "Dimension: click the second point",
        _ => "Dimension: click where its line goes"
    };

    protected override bool HasPendingInput => _points.Count > 0 || _line1 != null || _chainLine != null;

    public override void Cancel()
    {
        _points.Clear();
        _line1 = _line2 = null;
        _chainLine = null;
        _s.Preview = null;
        _s.SetHint(StepHint());
        _s.View.Redraw();
    }

    private SectionPoint? Last => _points.Count > 0 ? _points[^1] : _chainLine != null ? _points.LastOrDefault() : null;

    public override void PointerMoved(ToolPointer p)
    {
        _s.Preview = PreviewAt(p);
        _s.View.Redraw();
    }

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left) return;
        var free = p.Modifiers.HasFlag(KeyModifiers.Shift);
        switch (_kind)
        {
            case SectionMarkKind.Angle:
            case SectionMarkKind.Slope:
                if (_line1 == null) { _line1 = _s.PieceAt(p.X, p.Y); break; }
                if (_kind == SectionMarkKind.Angle && _line2 == null) { _line2 = _s.PieceAt(p.X, p.Y); break; }
                Finish(_s.Free(p.X, p.Y));
                return;
            case SectionMarkKind.Level:
                Finish(_s.Pick(p.X, p.Y, null, free).Point);
                return;
            case SectionMarkKind.Text:
            {
                var at = _s.Pick(p.X, p.Y, null, free).Point;
                _s.AskAt?.Invoke(new SkiaSharp.SKPoint((float)p.X, (float)p.Y), "Text", "", words =>
                {
                    if (string.IsNullOrWhiteSpace(words)) return;
                    _s.Add(new SectionMark { Kind = SectionMarkKind.Text, Points = { at }, Text = words.Trim(), Style = _s.StyleFor(SectionMarkKind.Text) });
                });
                return;
            }
            case SectionMarkKind.Polyline:
                if (p.ClickCount >= 2 && _points.Count >= 2) { Finish(null); return; }
                _points.Add(_s.Pick(p.X, p.Y, Last, free).Point);
                break;
            default:
            {
                // the position of the dimension line is taken free: it is no point of the drawing
                var pick = _points.Count == 2 ? _s.Free(p.X, p.Y) : _s.Pick(p.X, p.Y, Last, free).Point;
                if (_chain && _chainLine != null)
                {
                    // a chain: from the last point to this one, its line where the first one ran
                    var mark = Make(new List<SectionPoint> { _points[^1].Copy(), pick, _chainLine.Copy() });
                    _s.Add(mark);
                    _points.Clear();
                    _points.Add(pick);
                    break;
                }
                _points.Add(pick);
                if (_points.Count == SectionMark.PointCount(_kind))
                {
                    var done = _points.ToList();
                    Finish(null);
                    if (_chain)
                    {
                        _chainLine = done[2];
                        _points.Clear();
                        _points.Add(done[1]);
                    }
                    return;
                }
                break;
            }
        }
        _s.SetHint(StepHint());
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (_kind == SectionMarkKind.Angle && _line1 != null && _line2 == null && key is Key.H or Key.U or Key.V or Key.D)
        {
            // H (Г) a level through the first line, V (В) an upright one
            var a = _s.Project is { } p ? SectionMarking.Resolve(p, _s.Frame, _line1.A) : new SectionSpot(_line1.A.S, _line1.A.Z);
            var level = key is Key.H or Key.U;
            var b = level ? new SectionPoint(a.S + 1, a.Z) : new SectionPoint(a.S, a.Z + 1);
            var start = new SectionPoint(a.S, a.Z);
            _line2 = new SectionPiece(start, b, _s.ToScreen(start), _s.ToScreen(b));
            _s.SetHint(StepHint());
            return true;
        }
        if (_kind == SectionMarkKind.Angle && key == Key.Tab)
        {
            _mode = _mode switch { SectionAngleMode.AsDrawn => SectionAngleMode.Acute, SectionAngleMode.Acute => SectionAngleMode.Obtuse, _ => SectionAngleMode.AsDrawn };
            _s.View.Redraw();
            return true;
        }
        if (key == Key.Enter)
        {
            if (_kind == SectionMarkKind.Polyline && _points.Count >= 2) { Finish(null); return true; }
            if (_kind == SectionMarkKind.Level)
            {
                var at = _s.LastPointer;
                _s.AskAt?.Invoke(at, "Level Z", "", typed =>
                {
                    if (!double.TryParse(typed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return;
                    var s = _s.Free(at.X, at.Y);
                    Finish(new SectionPoint(s.S, z));
                });
                return true;
            }
        }
        if (key == Key.Escape && _chain && _chainLine != null) { Cancel(); return true; }
        return base.KeyDown(key, mods);
    }

    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p)
    {
        if (_kind == SectionMarkKind.Polyline && _points.Count >= 2) yield return MenuEntry.Item("Finish", () => Finish(null));
        if (HasPendingInput) yield return MenuEntry.Item("Cancel", Cancel);
    }

    private SectionMark Make(List<SectionPoint> points) => new() { Kind = _kind, Points = points, Style = Styled() };

    private SectionMarkStyle Styled()
    {
        var style = _s.StyleFor(_kind);
        if (_kind == SectionMarkKind.Angle) style.AngleMode = _mode;
        return style;
    }

    /// <summary>Makes the mark of the points given (and <paramref name="last"/>, the click that ends it) and starts again.</summary>
    private void Finish(SectionPoint? last)
    {
        var points = _points.Select(p => p.Copy()).ToList();
        if (_kind == SectionMarkKind.Angle && _line1 != null && _line2 != null && last != null)
            points = new() { _line1.A.Copy(), _line1.B.Copy(), _line2.A.Copy(), _line2.B.Copy(), last };
        else if (_kind == SectionMarkKind.Slope && _line1 != null && last != null)
            points = new() { _line1.A.Copy(), _line1.B.Copy(), last };
        else if (last != null) points.Add(last);
        // a level and a height: one point is enough; a line: two
        var need = SectionMark.PointCount(_kind);
        if ((need > 0 && points.Count < need) || (_kind == SectionMarkKind.Polyline && points.Count < 2)) { Cancel(); return; }
        _s.Add(Make(points));
        _points.Clear();
        _line1 = _line2 = null;
        _s.Preview = null;
        _s.SetHint(StepHint());
    }

    /// <summary>The mark as it would be with the cursor where it is.</summary>
    private SectionMark? PreviewAt(ToolPointer p)
    {
        var free = p.Modifiers.HasFlag(KeyModifiers.Shift);
        _s.LastPointer = new SkiaSharp.SKPoint((float)p.X, (float)p.Y);
        switch (_kind)
        {
            case SectionMarkKind.Angle:
            {
                if (_line1 == null) return Highlight(_s.PieceAt(p.X, p.Y));
                var second = _line2 ?? _s.PieceAt(p.X, p.Y);
                if (second == null) return Highlight(_line1);
                return new SectionMark { Kind = SectionMarkKind.Angle, Style = Styled(), Points = { _line1.A.Copy(), _line1.B.Copy(), second.A.Copy(), second.B.Copy(), _s.Free(p.X, p.Y) } };
            }
            case SectionMarkKind.Slope:
                if (_line1 == null) return Highlight(_s.PieceAt(p.X, p.Y));
                return new SectionMark { Kind = SectionMarkKind.Slope, Style = Styled(), Points = { _line1.A.Copy(), _line1.B.Copy(), _s.Free(p.X, p.Y) } };
            case SectionMarkKind.Text:
                return null;
        }
        var picked = _points.Count == 2 && _kind != SectionMarkKind.Polyline ? null : _s.Pick(p.X, p.Y, Last, free);
        var pick = picked?.Point ?? _s.Free(p.X, p.Y);
        _s.Snapped = picked?.Snap ?? SectionSnapKind.None;
        if (_chain && _chainLine != null && _points.Count == 1) return Make(new List<SectionPoint> { _points[0].Copy(), pick, _chainLine.Copy() });
        var pts = _points.Select(x => x.Copy()).Append(pick).ToList();
        var need = SectionMark.PointCount(_kind);
        if (_kind is SectionMarkKind.Level or SectionMarkKind.Polyline) return pts.Count >= (need == 0 ? 2 : need) ? Make(pts) : null;
        if (pts.Count < 2) return null;
        if (pts.Count < need)
        {
            // two points of a dimension: its line through the second for now
            while (pts.Count < need) pts.Add(pts[^1].Copy());
        }
        return Make(pts);
    }

    /// <summary>A line under the cursor shown as the one an angle or a slope will take.</summary>
    private SectionMark? Highlight(SectionPiece? piece) =>
        piece == null ? null : new SectionMark { Kind = SectionMarkKind.Polyline, Points = { piece.A.Copy(), piece.B.Copy() }, Style = new SectionMarkStyle { Color = 0xFFF5A623, LineWeight = 50 } };
}
