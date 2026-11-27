using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
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
                var items = new List<MenuItem> { rename, reset };
                if (s.IsIsland)
                {
                    var remove = new MenuItem { Header = Loc.T("Remove the island"), IsEnabled = !ro };
                    remove.Click += (_, _) => Done(_vm.RemoveIsland(id));
                    items.Add(remove);
                }
                row.ContextMenu = new ContextMenu { ItemsSource = items };
                ToolTip.SetTip(row, Loc.Ui("Click: select the holes of the segment"));
                list.Children.Add(row);
            }
            yield return list;

            // the seams between the segments: how many problems, and what is done with holes too close
            var issues = PatternSeams.Check(p, root);
            int close = issues.Count(x => x.Kind == SeamIssueKind.Close), gaps = issues.Count(x => x.Kind == SeamIssueKind.Gap), crossing = issues.Count(x => x.Kind == SeamIssueKind.Crossing);
            var said = close + gaps + crossing == 0 ? Loc.Ui("no problems") : Loc.F(Loc.Ui("{0} close pairs, {1} gaps, {2} crossings"), close, gaps, crossing);
            yield return Row("Seams", new TextBlock { Text = said, FontSize = 12, Foreground = close + gaps + crossing == 0 ? Look.Muted : new SolidColorBrush(Color.FromUInt32(0xFFF87171)), VerticalAlignment = VerticalAlignment.Center, Name = "patternSeams" });
            var rules = new[] { SeamRule.Warn, SeamRule.Drop, SeamRule.Merge };
            var rule = new ComboBox
            {
                ItemsSource = new[] { "Warn", "Remove the hole of the coarser grid", "One hole in the middle" }.Select(Loc.Ui).ToList(), SelectedIndex = Array.IndexOf(rules, root.SeamRule),
                Width = 190, Height = 26, MinHeight = 26, FontSize = 12, Name = "patternSeamRule", IsEnabled = !ro
            };
            ToolTip.SetTip(rule, Loc.Ui("Two holes of different segments closer than 0.6 of the smaller spacing (or 2 m)"));
            var rid = root.Id;
            rule.SelectionChanged += (_, _) =>
            {
                if (rule.SelectedIndex < 0 || rules[rule.SelectedIndex] == root.SeamRule) return;
                var r = rules[rule.SelectedIndex];
                Done(_vm.SetPatternDefaults(rid, "seams", x => x.SeamRule = r));
            };
            yield return Row("Holes too close", rule);
            var fill = new CheckBox { Content = Loc.Ui("A hole in the middle of a gap at a seam"), IsChecked = root.SeamFillGaps, Name = "patternSeamGaps", IsEnabled = !ro };
            fill.IsCheckedChanged += (_, _) =>
            {
                var on = fill.IsChecked == true;
                if (on != root.SeamFillGaps) Done(_vm.SetPatternDefaults(rid, "seams", x => x.SeamFillGaps = on));
            };
            yield return fill;
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
            yield return Own("Burden, m", "segmentBurden", o.Burden, Qty.Show(Quantity.Length, seg.Burden), (x, v) => x.Burden = Qty.Store(Quantity.Length, v));
            yield return Own("Spacing, m", "segmentSpacing", o.Spacing, Qty.Show(Quantity.Length, seg.Spacing), (x, v) => x.Spacing = Qty.Store(Quantity.Length, v));
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
            yield return Own("Diameter, mm", "segmentDiameter", o.Diameter, Qty.Show(Quantity.Diameter, seg.HoleDefaults.Diameter), (x, v) => x.Diameter = Qty.Store(Quantity.Diameter, v), BlastDesigner.Domain.Measure.Format(Quantity.Diameter));
            yield return Own("Subdrill, m", "segmentSubdrill", o.Subdrill, Qty.Show(Quantity.Length, seg.HoleDefaults.Subdrill), (x, v) => x.Subdrill = Qty.Store(Quantity.Length, v));
            yield return Own("Angle, °", "segmentAngle", o.Angle, BlastDesigner.Domain.Measure.Show(Quantity.HoleAngle, seg.HoleDefaults.Angle), (x, v) => x.Angle = v is double a ? BlastDesigner.Domain.Measure.Store(Quantity.HoleAngle, a) : null, "0.#");
            // its own direction of the rows: typed in, it turns the segment about its centre
            var azimuth = Box(N(seg.Azimuth, "0.#"), "segmentAzimuth", v =>
            {
                // the azimuth as it is now: a field taken away with the panel gives its value again after the turn
                var now = _vm.Project?.FindPattern(sid)?.Azimuth ?? seg.Azimuth;
                if (Metres(v) is double a && Math.Abs(MainViewModel.Turn(now, a)) > 1e-9) Done(_vm.RotatePattern(sid, MainViewModel.Turn(now, a)));
                else Refresh();
            });
            ToolTip.SetTip(azimuth, Loc.Ui("Direction of the burden of the segment, degrees from north; a new value turns the segment about its centre"));
            yield return Row("Burden azimuth, °", Inherit(azimuth, o.Azimuth == null, () => Done(_vm.SetSegmentOverride(sid, "Rotate pattern", x => x.Azimuth = null)), "segmentAzimuth"));
            // with a burden of its own, the side of the segment its rows are laid from
            var side = new ComboBox
            {
                ItemsSource = new[] { Loc.Ui("the face side"), Loc.Ui("the back side") }, SelectedIndex = seg.AnchorSide == PatternAnchorSide.Back ? 1 : 0, Width = 120, Height = 26,
                MinHeight = 26, FontSize = 12, Name = "segmentAnchorSide", IsEnabled = !ro && o.Burden != null
            };
            ToolTip.SetTip(side, Loc.Ui("With a burden of its own, the first row of the segment is half a burden inside this side"));
            side.SelectionChanged += (_, _) =>
            {
                var s = side.SelectedIndex == 1 ? PatternAnchorSide.Back : PatternAnchorSide.Face;
                if (s != seg.AnchorSide) Done(_vm.SetPatternDefaults(sid, "Rows laid from", x => x.AnchorSide = s));
            };
            yield return Row("Rows laid from", side);
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
            yield return Value("Diameter, mm", "patternDiameter", Qty.Show(Quantity.Diameter, root.HoleDefaults.Diameter), (x, v) => x.HoleDefaults.Diameter = Qty.Store(Quantity.Diameter, v), BlastDesigner.Domain.Measure.Format(Quantity.Diameter));
            yield return Value("Subdrill, m", "patternSubdrill", Qty.Show(Quantity.Length, root.HoleDefaults.Subdrill), (x, v) => x.HoleDefaults.Subdrill = Qty.Store(Quantity.Length, v));
            yield return Value("Angle, °", "patternAngle", BlastDesigner.Domain.Measure.Show(Quantity.HoleAngle, root.HoleDefaults.Angle), (x, v) => x.HoleDefaults.Angle = BlastDesigner.Domain.Measure.Store(Quantity.HoleAngle, v), "0.#");
        }

        // the depths, levels and elevations are lengths: shown and typed in the units of the program
        double? Lengths(string v) => Metres(v) is double m ? Qty.Store(Quantity.Length, m) : null;
        string LengthText(double v, string f) => N(Qty.Show(Quantity.Length, v), f);
        foreach (var control in DepthControls(doc, segment ?? root, Row, Box, Small, Faint, Done, Lengths, LengthText)) yield return control;
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
        string N(double v) => Qty.Show(Quantity.Length, v).ToString("0.#", c);
        return PatternTree.EffectiveDepthMode(s) switch
        {
            DepthMode.RectToLevel or DepthMode.SurfaceToRect =>
                $"{N(new[] { d.Front.From, d.Front.To, d.Back.From, d.Back.To }.Min())}…{N(new[] { d.Front.From, d.Front.To, d.Back.From, d.Back.To }.Max())}",
            DepthMode.ToLevel => "→" + N(s.ToLevel ?? d.Level),
            DepthMode.ToSurface => "→" + Loc.Ui("surface"),
            DepthMode.Polygon => "→" + Loc.Ui("polygon"),
            DepthMode.Range => $"{N(Math.Min(d.Front.From, d.Front.To))}…{N(Math.Max(d.Front.From, d.Front.To))}",
            _ => N(s.HoleDefaults.BenchHeight) + " " + Qty.Sym(Quantity.Length)
        };
    }

    /// <summary>
    /// The depth of a pattern or a segment: where its collars are (the surface of the project or the collar polygon), what
    /// its holes go down to, and the numbers that needs; with a polygon, the table of the elevations of its vertices.
    /// </summary>
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
        var sources = new[] { CollarSource.ProjectSurface, CollarSource.Polygon };
        // the third item: the collars on a surface of their own, picked on the plan (⚠ when it was deleted)
        var surface = spec.OnOwnSurface ? CollarSurfaces.Find(doc.Project, spec.CollarSurfaceId) : null;
        var surfaceItem = !spec.OnOwnSurface ? Loc.Ui("On the surface")
            : surface == null ? "⚠ " + Loc.Ui("On the surface")
            : $"{Loc.Ui("On the surface")}: {(string.IsNullOrWhiteSpace(surface.Name) ? Loc.T(surface.KindName) : surface.Name)}";
        var shown = spec.OnOwnSurface ? 2 : Array.IndexOf(sources, spec.Collars);
        var collars = new ComboBox
        {
            ItemsSource = new[] { Loc.Ui("On the grid"), Loc.Ui("Collar polygon"), surfaceItem }.ToList(), SelectedIndex = shown, Width = 190, Height = 26,
            MinHeight = 26, FontSize = 12, Name = "patternCollars", IsEnabled = !doc.IsReadOnly, FontStyle = inherited ? FontStyle.Italic : FontStyle.Normal
        };
        ToolTip.SetTip(collars, spec.OnOwnSurface && surface == null
            ? Loc.Ui("The surface the collars sat on was deleted: they stay where they are")
            : Loc.Ui("Where the collars are: on the collar surface of the project (at the level of the grid without one), on the collar polygon on the vertices of the outline, or on a surface picked on the plan"));
        collars.SelectionChanged += (_, _) =>
        {
            if (collars.SelectedIndex < 0 || collars.SelectedIndex == shown) return;
            if (collars.SelectedIndex == 2)
            {
                // the surface is picked on the plan; Esc puts the list back as it was
                var holes = PatternTree.TreeHoles(doc.Project, target).Select(h => h.Id).ToList();
                _vm.PickCollarSurface(holes, s => Set(x => { x.Collars = CollarSource.ProjectSurface; x.CollarSurfaceId = s.Id; }), () => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh));
                return;
            }
            var c = sources[collars.SelectedIndex];
            // on the grid or on the polygon: the holes no longer follow a surface of their own
            Set(x => { x.Collars = c; x.CollarSurfaceId = null; });
        };
        yield return row("Collars", seg != null ? Inherit(collars, inherited, () => done(_vm.SetPatternDepth(id, null)), "patternCollars") : collars);

        var modes = new[] { DepthMode.Fixed, DepthMode.ToLevel, DepthMode.ToSurface, DepthMode.Range, DepthMode.Polygon };
        var names = new[] { "One depth", "To a level", "To the surface", "To the grade, within from/to", "Depth polygon" };
        var mode = new ComboBox
        {
            ItemsSource = names.Select(Loc.Ui).ToList(), SelectedIndex = Array.IndexOf(modes, spec.Mode), Width = 190, Height = 26, MinHeight = 26, FontSize = 12, Name = "patternDepthMode",
            IsEnabled = !doc.IsReadOnly, FontStyle = inherited ? FontStyle.Italic : FontStyle.Normal
        };
        ToolTip.SetTip(mode, Loc.Ui("What the holes go down to; the depth polygon has an elevation at each vertex of the outline, flat triangles between them"));
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
        if (spec.Mode == DepthMode.Range)
        {
            var range = Pair("Depth range, m", "depthRange", spec.Front, true);
            ToolTip.SetTip(range, Loc.Ui("The depth down to the grade surface (to the level without one), kept between these two; the holes held at an end are counted"));
            yield return range;
        }
        if (spec.Mode is DepthMode.ToLevel or DepthMode.Range)
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
        if (spec.UsesPolygons)
            foreach (var control in PolygonTable(doc, target, spec, box, small, done, metres, n)) yield return control;
    }

    /// <summary>
    /// The vertices of the collar and depth polygons of a pattern or a segment: the elevation of the collar and of the depth
    /// at each (typed in, it is that vertex's; a vertex shared with the next segment is changed for both) and the depth
    /// between them, with buttons that set or shift them all.
    /// </summary>
    private IEnumerable<Control> PolygonTable(Document doc, HolePattern target, DepthSpec spec, Func<string, string, Action<string>, double, TextBox> box,
        Func<string, string, Action, string, Button> small, Action<Task<bool>> done, Func<string, double?> metres, Func<double, string, string> n)
    {
        var p = doc.Project;
        var root = PatternTree.Root(p, target);
        var bench = p.Entities.OfType<SurfaceEntity>().FirstOrDefault(s => s.Kind == SurfaceKind.Bench);
        var leaves = target.IsContainer ? PatternTree.Leaves(p, target) : new List<HolePattern> { target };
        var vertices = new List<SurfaceVertex>();
        foreach (var leaf in leaves)
            foreach (var v in PatternSurfaces.Polygons(p, leaf, bench).SelectMany(x => x))
                if (v.Key != null && vertices.All(x => x.Key != v.Key)) vertices.Add(v);
        if (vertices.Count == 0) yield break;
        var hasCollar = spec.Collars == CollarSource.Polygon;
        var hasDepth = spec.Mode == DepthMode.Polygon;
        var rid = root.Id;
        void Set(bool collar, Dictionary<string, double> values) => done(_vm.SetPolygonVertices(rid, collar, values));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,*,Auto"), RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", vertices.Count + 1))), Name = "polygonVertices" };
        void Cell(Control c, int r, int col) { Grid.SetRow(c, r); Grid.SetColumn(c, col); c.Margin = new Thickness(2); grid.Children.Add(c); }
        TextBlock Head(string text) => new() { Text = Loc.Ui(text), FontSize = 11, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center };
        Cell(Head("Vertex"), 0, 0);
        Cell(Head("Collar, m"), 0, 1);
        Cell(Head("Toe elevation, m"), 0, 2);
        Cell(Head("Depth, m"), 0, 3);
        for (int i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            var key = v.Key!;
            Cell(new TextBlock { Text = VertexName(p, root, key), FontFamily = Look.Mono, FontSize = 11.5, Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center }, i + 1, 0);
            var c = box(n(v.Collar, "0.00"), "polygonCollar" + i, s => { if (metres(s) is double z && Math.Abs(z - v.Collar) > 1e-9) Set(true, new() { [key] = z }); else Refresh(); }, 64);
            var d = box(n(v.Depth, "0.00"), "polygonDepth" + i, s => { if (metres(s) is double z && Math.Abs(z - v.Depth) > 1e-9) Set(false, new() { [key] = z }); else Refresh(); }, 64);
            c.IsEnabled &= hasCollar;
            d.IsEnabled &= hasDepth;
            // a vertex that takes its elevation from its neighbours is shown grey
            if (!v.CollarSet) { c.FontStyle = FontStyle.Italic; c.Foreground = Look.Muted; }
            if (!v.DepthSet) { d.FontStyle = FontStyle.Italic; d.Foreground = Look.Muted; }
            Cell(c, i + 1, 1);
            Cell(d, i + 1, 2);
            Cell(new TextBlock { Text = n(v.Collar - v.Depth, "0.00"), FontFamily = Look.Mono, FontSize = 11.5, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center }, i + 1, 3);
        }
        yield return grid;

        async void Ask(string title, double value, Action<double> apply)
        {
            if (await Dialogs.PromptNumber(Loc.T(title), Loc.T("Value, m"), Qty.Show(Quantity.Length, value)) is double x) apply(Qty.Store(Quantity.Length, x));
        }
        Dictionary<string, double> All(Func<SurfaceVertex, double> f) => vertices.ToDictionary(v => v.Key!, f);
        var buttons = new WrapPanel { Name = "polygonButtons" };
        Button Wide(string text, string name, Action click)
        {
            var b = new Button { Content = Loc.Ui(text), Height = 26, MinHeight = 26, FontSize = 12, Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 4, 4), Name = name, IsEnabled = !doc.IsReadOnly };
            b.Click += (_, _) => click();
            return b;
        }
        if (hasCollar)
        {
            buttons.Children.Add(Wide("All collars =…", "polygonCollarsAll", () => Ask("All collars", Math.Round(vertices.Average(v => v.Collar), 2), z => Set(true, All(_ => z)))));
            buttons.Children.Add(Wide("Collars ±…", "polygonCollarsShift", () => Ask("Shift the collars", 0, dz => Set(true, All(v => v.Collar + dz)))));
        }
        if (hasDepth)
        {
            buttons.Children.Add(Wide("All depths =…", "polygonDepthsAll", () => Ask("All depths", Math.Round(vertices.Average(v => v.Depth), 2), z => Set(false, All(_ => z)))));
            buttons.Children.Add(Wide("Depths ±…", "polygonDepthsShift", () => Ask("Shift the depths", 0, dz => Set(false, All(v => v.Depth + dz)))));
            buttons.Children.Add(Wide("Depths = collars −…", "polygonDepthsBelow", () => Ask("Depth below the collars", target.HoleDefaults.BenchHeight, d => Set(false, All(v => v.Collar - d)))));
        }
        yield return buttons;
    }

    /// <summary>The name of a vertex in the table: its number on the outline, the line and end of a crossing, the island and its vertex.</summary>
    internal static string VertexName(BlastProject p, HolePattern root, string key)
    {
        var parts = key.Split(':');
        return parts[0] switch
        {
            "O" => (int.Parse(parts[1]) + 1).ToString(CultureInfo.CurrentCulture),
            "L" => $"{Loc.Ui("line")} {int.Parse(parts[1]) + 1}·{parts[2]}",
            "I" => $"{PatternTree.Children(p, root).FirstOrDefault(s => s.Id.ToString("N") == parts[1])?.Name ?? "I"}·{int.Parse(parts[2]) + 1}",
            _ => key
        };
    }
}
