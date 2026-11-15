using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.Views;

/// <summary>
/// The segments of a pattern in its group «Pattern»: how many, the list of them, the fields of the selected segment (a value
/// taken from the parent is grey with a chain, a value typed in is its own, ↺ takes the parent's again) and the depth of a
/// pattern or a segment, with the four numbers of the rectangle.
/// </summary>
public sealed partial class PropertiesPanel
{
    private IEnumerable<Control> SegmentControls(Document doc, HolePattern root, HolePattern? segment)
    {
        var p = doc.Project;
        var c = CultureInfo.CurrentCulture;
        var ro = doc.IsReadOnly;
        string N(double v, string f = "0.###") => v.ToString(f, c);
        double? Metres(string v) => double.TryParse(v.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : null;
        TextBlock Faint(string text) => new() { Text = text, FontSize = 12, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center };
        Control Row(string label, Control value)
        {
            var row = new DockPanel();
            value[DockPanel.DockProperty] = Dock.Right;
            row.Children.Add(value);
            row.Children.Add(new TextBlock { Text = Loc.Ui(label), FontSize = 12, Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }
        TextBox Box(string text, string name, Action<string> commit, double width = 52)
        {
            var box = new TextBox { Text = text, Width = width, Height = 26, MinHeight = 26, Padding = new Thickness(6, 2), FontSize = 12, Name = name, IsEnabled = !ro };
            void Commit() { if ((box.Text ?? "") != text) commit(box.Text ?? ""); }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(); e.Handled = true; } };
            return box;
        }
        Button Small(string text, string tip, Action click, string name)
        {
            var b = new Button { Content = text, Width = 26, Height = 26, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), FontSize = 12, Name = name, IsEnabled = !ro };
            ToolTip.SetTip(b, Loc.Ui(tip));
            b.Click += (_, _) => click();
            return b;
        }
        async void Done(Task<bool> change) { if (!await change) Refresh(); }

        // the number of segments: the lines across the blast follow on the plan
        var count = Box(PatternTree.SegmentCount(root).ToString(c), "patternSegments", v =>
        {
            if (int.TryParse(v.Trim(), NumberStyles.Integer, c, out var n) && n >= 1) Done(_vm.SetSegmentCount(root.Id, n));
            else Refresh();
        });
        ToolTip.SetTip(count, Loc.F(Loc.Ui("Segments the pattern is cut into, 1 to {0} (its number of rows): the cut lines are shown on the plan and can be dragged"), PatternTree.MaxSegments(root)));
        yield return Row("Pattern segments", count);

        if (root.IsContainer)
        {
            var list = new StackPanel { Spacing = 2, Name = "patternSegmentList" };
            foreach (var s in PatternTree.Children(p, root))
            {
                var holes = HoleDesign.PatternHoles(p, s).Count();
                var d = s.HoleDefaults;
                var text = $"{s.Name}   {N(s.Burden)} × {N(s.Spacing)}   Ø{N(d.Diameter, "0.#")}   {DepthText(s, c)}   · {holes}";
                var mine = segment?.Id == s.Id;
                var row = new Button
                {
                    Content = new TextBlock { Text = text, FontFamily = Look.Mono, FontSize = 11.5, Foreground = s.Overrides.IsEmpty ? Look.TextSoft : Look.Text, FontWeight = mine ? FontWeight.SemiBold : FontWeight.Normal },
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 2), MinHeight = 24,
                    Background = mine ? Look.AccentSoft : Brushes.Transparent, BorderThickness = new Thickness(0), Name = "patternSegment" + s.Name
                };
                var id = s.Id;
                row.Click += (_, _) => _vm.SelectSegmentHoles(id);
                var rename = new MenuItem { Header = Loc.T("Rename…"), IsEnabled = !ro };
                rename.Click += async (_, _) =>
                {
                    if (await Dialogs.Prompt(Loc.T("Rename segment"), Loc.T("Name"), s.Name) is { } name && name.Trim().Length > 0)
                        _vm.SetPatternFlag(id, "Rename segment", x => x.Name = name.Trim());
                };
                var reset = new MenuItem { Header = Loc.T("As the parent"), IsEnabled = !ro && !s.Overrides.IsEmpty };
                reset.Click += (_, _) => Done(_vm.ResetSegment(id));
                row.ContextMenu = new ContextMenu { ItemsSource = new[] { rename, reset } };
                ToolTip.SetTip(row, Loc.Ui("Click: select the holes of the segment"));
                list.Children.Add(row);
            }
            yield return list;
        }

        // the fields of one segment, each taken from the parent unless set here
        if (segment is { } seg)
        {
            var o = seg.Overrides;
            var sid = seg.Id;
            yield return new TextBlock { Text = Loc.F(Loc.Ui("Segment {0}"), seg.Name), FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, Margin = new Thickness(0, 4, 0, 0) };
            Control Own(string label, string name, double? own, double value, Action<PatternOverrides, double?> set, string fmt = "0.###")
            {
                var box = Box(N(value, fmt), name, v =>
                {
                    if (Metres(v) is double m && (own != null || Math.Abs(m - value) > 1e-9)) Done(_vm.SetSegmentOverride(sid, label, x => set(x, m)));
                    else Refresh();
                });
                return Row(label, Inherit(box, own == null, () => Done(_vm.SetSegmentOverride(sid, label, x => set(x, null))), name));
            }
            yield return Own("Burden, m", "segmentBurden", o.Burden, seg.Burden, (x, v) => x.Burden = v);
            yield return Own("Spacing, m", "segmentSpacing", o.Spacing, seg.Spacing, (x, v) => x.Spacing = v);
            var type = new ComboBox
            {
                ItemsSource = new[] { Loc.T("Rectangular"), Loc.T("Staggered") }, SelectedIndex = seg.Type == PatternType.Staggered ? 1 : 0, Width = 120, Height = 26, MinHeight = 26,
                FontSize = 12, Name = "segmentType", IsEnabled = !ro, FontStyle = o.Type == null ? FontStyle.Italic : FontStyle.Normal
            };
            type.SelectionChanged += (_, _) =>
            {
                var t = type.SelectedIndex == 1 ? PatternType.Staggered : PatternType.Rectangular;
                if (t != seg.Type) Done(_vm.SetSegmentOverride(sid, "Pattern", x => x.Type = t));
            };
            yield return Row("Pattern", Inherit(type, o.Type == null, () => Done(_vm.SetSegmentOverride(sid, "Pattern", x => x.Type = null)), "segmentType"));
            yield return Own("Diameter, mm", "segmentDiameter", o.Diameter, seg.HoleDefaults.Diameter, (x, v) => x.Diameter = v, "0.#");
            yield return Own("Subdrill, m", "segmentSubdrill", o.Subdrill, seg.HoleDefaults.Subdrill, (x, v) => x.Subdrill = v);
            yield return Own("Angle, °", "segmentAngle", o.Angle, seg.HoleDefaults.Angle, (x, v) => x.Angle = v, "0.#");
        }
        else if (root.IsContainer)
        {
            // the values of the parent: the segments that do not set them take them
            var rid = root.Id;
            Control Value(string label, string name, double value, Action<HolePattern, double> set, string fmt = "0.###") => Row(label, Box(N(value, fmt), name, v =>
            {
                if (Metres(v) is double m && Math.Abs(m - value) > 1e-9) Done(_vm.SetPatternDefaults(rid, label, x => set(x, m)));
                else Refresh();
            }, 120));
            yield return Value("Diameter, mm", "patternDiameter", root.HoleDefaults.Diameter, (x, v) => x.HoleDefaults.Diameter = v, "0.#");
            yield return Value("Subdrill, m", "patternSubdrill", root.HoleDefaults.Subdrill, (x, v) => x.HoleDefaults.Subdrill = v);
            yield return Value("Angle, °", "patternAngle", root.HoleDefaults.Angle, (x, v) => x.HoleDefaults.Angle = v, "0.#");
        }

        foreach (var control in DepthControls(doc, segment ?? root, Row, Box, Small, Faint, Done, Metres, N)) yield return control;
    }

    /// <summary>A field of a segment with the chain of a value taken from the parent, or ↺ that takes the parent's again.</summary>
    private Control Inherit(Control field, bool inherited, Action reset, string name)
    {
        if (inherited)
        {
            if (field is TextBox box) { box.FontStyle = FontStyle.Italic; box.Foreground = Look.Muted; }
            ToolTip.SetTip(field, Loc.Ui("Taken from the parent pattern; a value typed in is the segment's own"));
        }
        var mark = inherited
            ? (Control)new TextBlock { Text = "⛓", Width = 26, TextAlignment = TextAlignment.Center, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Name = name + "Inherited" }
            : new Button { Content = "↺", Width = 26, Height = 26, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), Name = name + "Reset", IsEnabled = field.IsEnabled };
        if (mark is Button b)
        {
            ToolTip.SetTip(b, Loc.Ui("Take the value of the parent pattern again"));
            b.Click += (_, _) => reset();
        }
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { mark, field } };
    }

    /// <summary>The depth in a word for the list of segments: one depth, the range of the rectangle, a level or the surface.</summary>
    private static string DepthText(HolePattern s, CultureInfo c)
    {
        var d = s.HoleDefaults.Depth;
        string N(double v) => v.ToString("0.#", c);
        return PatternTree.EffectiveDepthMode(s) switch
        {
            DepthMode.RectToLevel or DepthMode.SurfaceToRect =>
                $"{N(new[] { d.Front.From, d.Front.To, d.Back.From, d.Back.To }.Min())}…{N(new[] { d.Front.From, d.Front.To, d.Back.From, d.Back.To }.Max())}",
            DepthMode.ToLevel => "→" + N(s.ToLevel ?? d.Level),
            DepthMode.ToSurface => "→" + Loc.Ui("surface"),
            _ => N(s.HoleDefaults.BenchHeight) + " " + Loc.Ui("m")
        };
    }

    /// <summary>The depth of a pattern or a segment: its mode, and the numbers it needs.</summary>
    private IEnumerable<Control> DepthControls(Document doc, HolePattern target, Func<string, Control, Control> row, Func<string, string, Action<string>, double, TextBox> box,
        Func<string, string, Action, string, Button> small, Func<string, TextBlock> faint, Action<Task<bool>> done, Func<string, double?> metres, Func<double, string, string> n)
    {
        var seg = target.ParentId != null ? target : null;
        var inherited = seg != null && seg.Overrides.Depth == null;
        var spec = target.HoleDefaults.Depth.Clone();
        spec.Mode = PatternTree.EffectiveDepthMode(target);
        if (spec.Mode == DepthMode.ToLevel && target.ToLevel is double tl) spec.Level = tl;
        var id = target.Id;
        void Set(Action<DepthSpec> change)
        {
            var next = spec.Clone();
            change(next);
            done(_vm.SetPatternDepth(id, next));
        }
        var modes = new[] { DepthMode.Fixed, DepthMode.RectToLevel, DepthMode.SurfaceToRect, DepthMode.ToLevel, DepthMode.ToSurface };
        var names = new[] { "One depth", "Collars on the rectangle, to a level", "Collars on the surface, to the rectangle", "To a level", "To the surface" };
        var mode = new ComboBox
        {
            ItemsSource = names.Select(Loc.Ui).ToList(), SelectedIndex = Array.IndexOf(modes, spec.Mode), Width = 190, Height = 26, MinHeight = 26, FontSize = 12, Name = "patternDepthMode",
            IsEnabled = !doc.IsReadOnly, FontStyle = inherited ? FontStyle.Italic : FontStyle.Normal
        };
        ToolTip.SetTip(mode, Loc.Ui("What the holes go down to; with the rectangle, four numbers at its corners: the front edge along the first row, the back edge along the last, each from the left end of the row to the right, looking along the blast"));
        mode.SelectionChanged += (_, _) =>
        {
            if (mode.SelectedIndex < 0 || modes[mode.SelectedIndex] == spec.Mode) return;
            var m = modes[mode.SelectedIndex];
            Set(x => x.Mode = m);
        };
        Control field = mode;
        if (seg != null)
            field = Inherit(mode, inherited, () => done(_vm.SetPatternDepth(id, null)), "patternDepthMode");
        yield return row("Depth", field);

        Control Pair(string label, string name, DepthPair pair, bool front)
        {
            var from = box(n(pair.From, "0.###"), name + "From", v => { if (metres(v) is double m) Set(x => (front ? x.Front : x.Back).From = m); else Refresh(); }, 52);
            var to = box(n(pair.To, "0.###"), name + "To", v => { if (metres(v) is double m) Set(x => (front ? x.Front : x.Back).To = m); else Refresh(); }, 52);
            var swap = small("⇄", "Swap from and to", () => Set(x => { var q = front ? x.Front : x.Back; (q.From, q.To) = (q.To, q.From); }), name + "Swap");
            var items = new List<Control> { faint(Loc.Ui("from")), from, faint(Loc.Ui("to")), to, swap };
            if (!front) items.Add(small("=", "The back edge as the front one", () => Set(x => x.Back = new DepthPair(x.Front.From, x.Front.To)), name + "Copy"));
            return row(label, Ui.Row(4, items.ToArray()));
        }
        if (spec.UsesRectangle)
        {
            yield return Pair("Front edge", "depthFront", spec.Front, true);
            yield return Pair("Back edge", "depthBack", spec.Back, false);
        }
        if (spec.Mode is DepthMode.RectToLevel or DepthMode.ToLevel)
            yield return row("Level, m", box(n(spec.Level, "0.###"), "depthLevel", v => { if (metres(v) is double m) Set(x => x.Level = m); else Refresh(); }, 120));
        if (spec.Mode == DepthMode.Fixed)
        {
            var bench = target.HoleDefaults.BenchHeight;
            var benchBox = box(n(bench, "0.###"), "depthBench", v =>
            {
                if (metres(v) is double m && m > 0 && Math.Abs(m - bench) > 1e-9) done(_vm.SetPatternBench(id, m));
                else Refresh();
            }, 120);
            yield return row("Depth, m", benchBox);
        }
    }
}
