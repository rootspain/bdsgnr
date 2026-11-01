using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.App.Views;

/// <summary>
/// Survey ▸ Recognize bench elements: what the recognition found, coloured on the plan while the window is open. A click
/// on a segment changes its class (Ctrl+click the whole line), so the result — or a drawing being labelled by hand —
/// can be put right before it is applied. Nothing changes in the project until Apply, which is one undo step.
/// </summary>
public static class SurveyRecognitionWindow
{
    public const string Title = "Bench elements";

    public static readonly (SurveyLineClass Class, uint Color, string Name)[] Classes =
    {
        (SurveyLineClass.Crest, 0xFFEF4444, "Crest line"), (SurveyLineClass.Toe, 0xFF3B82F6, "Toe line"), (SurveyLineClass.Contour, 0xFF22C55E, "Contour line"),
        (SurveyLineClass.Boundary, 0xFFA855F7, "Boundary line"), (SurveyLineClass.Other, 0xFF9CA3AF, "Other")
    };

    public static uint ColorOf(SurveyLineClass c) => Classes.First(x => x.Class == c).Color;

    /// <summary>Layer for each recognized type, in the colour the class has on the plan, so the types are shown and hidden with the layers.</summary>
    public static readonly (LineKind Kind, SurveyLineClass Class, string Layer)[] KindLayers =
    {
        (LineKind.Crest, SurveyLineClass.Crest, "Crest lines"), (LineKind.Toe, SurveyLineClass.Toe, "Toe lines"),
        (LineKind.Contour, SurveyLineClass.Contour, "Contour lines"), (LineKind.Boundary, SurveyLineClass.Boundary, "Block boundaries")
    };

    public static Window Show(MainViewModel vm, List<(PolylineEntity Line, string Layer)> lines, List<PointEntity> points, IReadOnlyList<Vec3> collars, SurveyRecognition recognition)
    {
        if (Dialogs.ActivateExisting(Title)) Dialogs.CloseWindow(Title);
        var overlay = new SurveyRecognitionOverlay(recognition, lines.ToDictionary(l => l.Line.Id, l => l.Line));
        var form = new SurveyRecognitionForm(vm, lines, points, collars, recognition, overlay);
        var tool = new SurveyClassTool(vm, form);
        vm.View.Overlays.Add(overlay);
        vm.SetTool(tool);
        var w = Dialogs.ShowWindow(Title, form, 460, 700);
        w.MinWidth = 380;
        w.MinHeight = 420;
        form.Close = w.Close;
        w.Closed += (_, _) =>
        {
            vm.View.Overlays.Remove(overlay);
            if (vm.ActiveTool == tool) vm.SelectTool();
            vm.View.Redraw();
        };
        vm.View.Redraw();
        return w;
    }
}

/// <summary>The segment classes on the plan: thick lines in the class colour, dashed where the recognition is unsure.</summary>
public sealed class SurveyRecognitionOverlay : IOverlay
{
    private readonly IReadOnlyDictionary<Guid, PolylineEntity> _lines;

    public SurveyRecognitionOverlay(SurveyRecognition r, IReadOnlyDictionary<Guid, PolylineEntity> lines) => (Recognition, _lines) = (r, lines);

    /// <summary>What is drawn; replaced when the window recognizes again by the other method.</summary>
    public SurveyRecognition Recognition { get; set; }

    public void Draw(SKCanvas c, Camera cam, BlastProject project)
    {
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, StrokeCap = SKStrokeCap.Round };
        using var dash = SKPathEffect.CreateDash(new[] { 7f, 5f }, 0);
        foreach (var s in Recognition.Segments)
        {
            if (!_lines.TryGetValue(s.Segment.LineId, out var line)) continue;
            var pts = line.Points.Skip(s.Segment.From).Take(s.Segment.To - s.Segment.From + 1).ToList();
            paint.Color = new SKColor(SurveyRecognitionWindow.ColorOf(s.Class)).WithAlpha(225);
            paint.PathEffect = s.Confidence < SurveyRecognizer.CheckBelow ? dash : null;
            PlanRenderer.DrawPolyline(c, cam, pts, false, paint);
        }
    }

    /// <summary>The segment nearest to a screen point, within <paramref name="pixels"/>.</summary>
    public int? SegmentAt(Camera cam, double x, double y, double pixels = 10)
    {
        int? best = null;
        var bestD = pixels;
        for (int i = 0; i < Recognition.Segments.Count; i++)
        {
            var s = Recognition.Segments[i].Segment;
            if (!_lines.TryGetValue(s.LineId, out var line)) continue;
            for (int k = s.From + 1; k <= s.To; k++)
            {
                var (ax, ay) = cam.ToScreen(line.Points[k - 1]);
                var (bx, by) = cam.ToScreen(line.Points[k]);
                double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
                var t = len2 < 1e-9 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0, 1);
                var d = Math.Sqrt(Math.Pow(x - ax - t * dx, 2) + Math.Pow(y - ay - t * dy, 2));
                if (d < bestD) { bestD = d; best = i; }
            }
        }
        return best;
    }
}

/// <summary>Changes segment classes while the bench elements window is open.</summary>
public sealed class SurveyClassTool : Tool
{
    private readonly SurveyRecognitionForm _form;

    public SurveyClassTool(MainViewModel vm, SurveyRecognitionForm form) : base(vm) => _form = form;

    public override string Name => "Bench elements";
    public override string Hint => "Click a segment to change its class; Ctrl+click changes the whole line. Right-click to choose the class.";

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left || _form.Overlay.SegmentAt(View.Camera, p.X, p.Y) is not int i) return;
        _form.Cycle(i, p.Modifiers.HasFlag(KeyModifiers.Control));
    }

    public override IEnumerable<MenuEntry> ContextMenu(ToolPointer p)
    {
        if (_form.Overlay.SegmentAt(View.Camera, p.X, p.Y) is int i)
            foreach (var (c, _, name) in SurveyRecognitionWindow.Classes)
            {
                yield return MenuEntry.Item(Loc.T(name), () => _form.Set(i, c, false));
                yield return MenuEntry.Item(Loc.F("{0} — whole line", Loc.T(name)), () => _form.Set(i, c, true));
            }
        yield return MenuEntry.Item("Close tool", () => Vm.SelectTool());
    }
}

/// <summary>Content of the bench elements window. Public so the UI smoke test can reach it.</summary>
public sealed class SurveyRecognitionForm : DockPanel
{
    private readonly MainViewModel _vm;
    private readonly List<(PolylineEntity Line, string Layer)> _lines;
    private readonly Dictionary<Guid, PolylineEntity> _byId;
    private readonly List<PointEntity> _points;
    private readonly IReadOnlyList<Vec3> _collars;
    private readonly Button _byModel;
    private readonly Button _byRules;
    private bool _busy;
    private readonly StackPanel _summary = new() { Spacing = 4 };
    private readonly Grid _benches = new() { Name = "benches" };
    private readonly CheckBox _assign = new() { IsChecked = true, Name = "assign" };
    private readonly CheckBox _split = new() { IsChecked = true, Name = "split" };
    private readonly CheckBox _layers = new() { IsChecked = true, Name = "layers" };
    private readonly CheckBox _surface = new() { IsChecked = false, Name = "surface" };

    public SurveyRecognition Recognition { get; private set; }
    public SurveyRecognitionOverlay Overlay { get; }
    public Action? Close { get; set; }

    public SurveyRecognitionForm(MainViewModel vm, List<(PolylineEntity Line, string Layer)> lines, List<PointEntity> points, IReadOnlyList<Vec3> collars, SurveyRecognition recognition, SurveyRecognitionOverlay overlay)
    {
        (_vm, _lines, _points, _collars, Recognition, Overlay) = (vm, lines, points, collars, recognition, overlay);
        _byId = lines.ToDictionary(l => l.Line.Id, l => l.Line);
        Background = Look.Ground;
        _assign.Content = Loc.T("Give the lines their type (crest, toe, contour, boundary)");
        _split.Content = Loc.T("Split lines where the class changes");
        _layers.Content = Loc.T("Move the lines to a layer per type, in its colour");
        ToolTip.SetTip(_layers, Loc.T("Crests, toes, contours and boundaries get their own layers: each type is shown or hidden with its layer"));
        // splitting and layers only make sense when the lines get a type
        _assign.IsCheckedChanged += (_, _) => _split.IsEnabled = _layers.IsEnabled = _assign.IsChecked == true;
        _surface.Content = Loc.T("Build the surface from the survey (all its lines as breaklines)");

        static TextBlock Caption(string text) => new() { Text = Loc.T(text).ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Faint, Margin = new Thickness(0, 10, 0, 6) };
        var source = new TextBlock
        {
            Text = Loc.T("Click a segment on the plan to change its class; Ctrl+click changes the whole line. Dashed: the recognition is unsure."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Foreground = Look.TextSoft,
            Margin = new Thickness(0, 8, 0, 0)
        };
        // the trained model or the rules: switching recognizes again in this window and keeps the choice for next time
        Button Method(string text, bool model)
        {
            var chip = Ui.Chip(Loc.T(text), () =>
            {
                if (model == Recognition.FromModel || _busy) return;
                _vm.Settings.UseSurveyModel = model;
                _vm.Settings.Save();
                _ = Recognize(model);
            }, model == Recognition.FromModel);
            chip.Tag = model ? "method:model" : "method:rules";
            return chip;
        }
        _byModel = Method("Model (experimental)", true);
        _byRules = Method("Rules", false);
        if (!BlastDesigner.Recognition.SurveyModel.Available)
        {
            _byModel.IsEnabled = false;
            ToolTip.SetTip(_byModel, Loc.T("The model is not available") + ": " + BlastDesigner.Recognition.SurveyModel.Problem);
        }
        else ToolTip.SetTip(_byModel, Loc.T("A model trained on synthetic surveys; better than the rules there, not yet checked on real ones"));
        var body = new StackPanel
        {
            Margin = new Thickness(16, 12, 16, 12),
            Children =
            {
                Ui.Row(6, _byModel, _byRules),
                source,
                Caption("Lines"), _summary,
                Caption("Benches"), new ScrollViewer { MaxHeight = 260, Content = _benches, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
                Caption("Apply"), new StackPanel { Spacing = 4, Children = { _assign, _split, _layers, _surface } }
            }
        };

        var save = new Button { Content = Loc.T("Save labels…"), Height = 32, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(8), VerticalContentAlignment = VerticalAlignment.Center, Name = "saveLabels" };
        ToolTip.SetTip(save, Loc.T("The classes of all segments as a reference for measuring the recognition (no coordinates in the file)"));
        save.Click += async (_, _) =>
        {
            var path = await Dialogs.SaveFile("Save labels", "labels.json", "json", ("JSON", new[] { "*.json" }));
            if (path == null) return;
            SurveyLabels.Save(path, SurveyLabels.From(Recognition, _byId));
            _vm.SetStatus(Loc.F("Labels saved: {0}", path));
        };
        var cancel = new Button { Content = Loc.T("Cancel"), Height = 32, Padding = new Thickness(16, 0), CornerRadius = new CornerRadius(8), VerticalContentAlignment = VerticalAlignment.Center, IsCancel = true };
        cancel.Click += (_, _) => Close?.Invoke();
        var apply = new Button { Content = Loc.T("Apply"), Height = 32, Padding = new Thickness(18, 0), CornerRadius = new CornerRadius(8), VerticalContentAlignment = VerticalAlignment.Center, Classes = { "primary" }, Name = "apply" };
        apply.Click += (_, _) => { Apply(); Close?.Invoke(); };
        var buttons = new DockPanel();
        var right = Ui.Row(8, cancel, apply);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { save } });
        var footer = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 10), Child = buttons };
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);
        Children.Add(new ScrollViewer { Content = body });
        Refresh();
    }

    /// <summary>
    /// Recognizes the same survey again by the model or the rules and shows the result in place — the window stays where
    /// it is. Changes made by hand to the previous result are dropped.
    /// </summary>
    public async Task Recognize(bool model)
    {
        if (_busy) return;
        _busy = true;
        _byModel.IsEnabled = _byRules.IsEnabled = false;
        _vm.SetStatus(Loc.T("Recognizing bench elements…"));
        try
        {
            var result = await Task.Run(() => BlastDesigner.Recognition.SurveyModel.Recognize(_lines, _points, _collars, model));
            Recognition = result;
            Overlay.Recognition = result;
            Ui.SetChip(_byModel, result.FromModel);
            Ui.SetChip(_byRules, !result.FromModel);
            Refresh();
            _vm.View.Redraw();
            _vm.SetStatus(Loc.T(result.FromModel ? "Recognized by the model" : "Recognized by rules"));
        }
        finally
        {
            _busy = false;
            _byModel.IsEnabled = BlastDesigner.Recognition.SurveyModel.Available;
            _byRules.IsEnabled = true;
        }
    }

    /// <summary>Next class for a segment (or its whole line).</summary>
    public void Cycle(int index, bool wholeLine)
    {
        var order = SurveyRecognitionWindow.Classes.Select(c => c.Class).ToList();
        var next = order[(order.IndexOf(Recognition.Segments[index].Class) + 1) % order.Count];
        Set(index, next, wholeLine);
    }

    public void Set(int index, SurveyLineClass c, bool wholeLine)
    {
        var line = Recognition.Segments[index].Segment.LineId;
        for (int i = 0; i < Recognition.Segments.Count; i++)
            if (i == index || (wholeLine && Recognition.Segments[i].Segment.LineId == line))
                Recognition.Segments[i] = Recognition.Segments[i] with { Class = c, Confidence = 1 };
        SurveyRecognizer.Refresh(Recognition, _lines.Select(l => l.Line).ToList(), _points);
        Refresh();
        _vm.View.Redraw();
    }

    private void Refresh()
    {
        _summary.Children.Clear();
        foreach (var (c, color, name) in SurveyRecognitionWindow.Classes)
        {
            var segs = Recognition.Segments.Where(s => s.Class == c).ToList();
            var length = segs.Sum(s => SurveyRecognition.Length(_byId[s.Segment.LineId].Points, s.Segment));
            var unsure = segs.Count(s => s.Confidence < SurveyRecognizer.CheckBelow);
            var row = new DockPanel();
            var stats = new TextBlock { Text = Loc.F("{0:0} m", length) + (unsure > 0 ? "  ·  " + Loc.F("{0} to check", unsure) : ""), Foreground = unsure > 0 ? Look.Accent : Look.TextSoft, FontFamily = Look.Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            SetDock(stats, Dock.Right);
            row.Children.Add(stats);
            row.Children.Add(Ui.Row(8,
                new Border { Width = 18, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromUInt32(color)), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = Loc.T(name), VerticalAlignment = VerticalAlignment.Center }));
            _summary.Children.Add(row);
        }

        _benches.Children.Clear();
        _benches.RowDefinitions.Clear();
        _benches.ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*");
        void Cell(string text, int row, int col, bool head = false)
        {
            var t = new TextBlock { Text = text, FontSize = head ? 10.5 : 12, Foreground = head ? Look.Faint : Look.Text, FontFamily = head ? FontFamily.Default : Look.Mono, HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right, Margin = new Thickness(0, 2) };
            Grid.SetRow(t, row);
            Grid.SetColumn(t, col);
            _benches.Children.Add(t);
        }
        _benches.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var heads = new[] { "Crest Z", "Height", "Face", "Angle", "Length" };
        for (int i = 0; i < heads.Length; i++) Cell(Loc.T(heads[i]), 0, i, true);
        var benches = Recognition.Benches.OrderByDescending(b => _byId[b.Crest].Points.Average(p => p.Z)).ToList();
        for (int i = 0; i < benches.Count; i++)
        {
            var b = benches[i];
            _benches.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var ci = CultureInfo.CurrentCulture;
            Cell(_byId[b.Crest].Points.Average(p => p.Z).ToString("0.0", ci), i + 1, 0);
            Cell(Loc.F("{0:0.0} m", b.Height), i + 1, 1);
            Cell(b.Toe == null ? "—" : Loc.F("{0:0.0} m", b.FaceWidth), i + 1, 2);
            Cell(b.Toe == null ? "—" : Loc.F("{0:0}°", b.FaceAngle), i + 1, 3);
            Cell(Loc.F("{0:0} m", GeoMath.Length(_byId[b.Crest].Points)), i + 1, 4);
        }
        if (benches.Count == 0)
        {
            _benches.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var none = new TextBlock { Text = Loc.T("No bench found: no crest with ground a bench lower beside it"), Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(none, 1);
            Grid.SetColumnSpan(none, 5);
            _benches.Children.Add(none);
        }
    }

    private static LineKind? KindOf(SurveyLineClass c) => c switch
    {
        SurveyLineClass.Crest => LineKind.Crest,
        SurveyLineClass.Toe => LineKind.Toe,
        SurveyLineClass.Contour => LineKind.Contour,
        SurveyLineClass.Boundary => LineKind.Boundary,
        _ => null
    };

    /// <summary>Line types, split lines and the bench surface — one undo step.</summary>
    public void Apply()
    {
        var assign = _assign.IsChecked == true;
        var split = _split.IsChecked == true;
        var layers = assign && _layers.IsChecked == true;
        var surface = _surface.IsChecked == true;
        int crests = 0, toes = 0;
        _vm.Change("Recognize bench elements", p =>
        {
            var result = new List<PolylineEntity>();
            foreach (var g in Recognition.Segments.GroupBy(s => s.Segment.LineId))
            {
                if (p.FindEntity(g.Key) is not PolylineEntity line) continue;
                // runs of one class along the line
                var runs = new List<(SurveyLineClass Class, int From, int To)>();
                foreach (var s in g.OrderBy(s => s.Segment.From))
                {
                    if (runs.Count > 0 && runs[^1].Class == s.Class) runs[^1] = runs[^1] with { To = s.Segment.To };
                    else runs.Add((s.Class, s.Segment.From, s.Segment.To));
                }
                if (assign && split && runs.Count > 1)
                {
                    var layer = p.FindLayer(line.LayerId);
                    foreach (var run in runs)
                    {
                        var piece = new PolylineEntity { Points = line.Points.Skip(run.From).Take(run.To - run.From + 1).ToList(), Kind = KindOf(run.Class) ?? LineKind.General, Color = line.Color, Label = line.Label };
                        p.Add(piece, layer);
                        result.Add(piece);
                    }
                    p.Entities.Remove(line);
                }
                else
                {
                    if (assign)
                    {
                        // the class of most of the line's length
                        var main = runs.GroupBy(r => r.Class).OrderByDescending(x => x.Sum(r => GeoMath.Length(line.Points.Skip(r.From).Take(r.To - r.From + 1).ToList()))).First().Key;
                        line.Kind = KindOf(main) ?? (line.Kind is LineKind.Crest or LineKind.Toe or LineKind.Contour or LineKind.Boundary ? LineKind.General : line.Kind);
                    }
                    result.Add(line);
                }
            }
            if (layers)
                foreach (var l in result)
                    if (SurveyRecognitionWindow.KindLayers.FirstOrDefault(k => k.Kind == l.Kind) is { Layer: not null } k)
                    {
                        // the layer's colour, not the one from the drawing, so the type reads on the plan
                        l.LayerId = p.GetOrCreateLayer(k.Layer, SurveyRecognitionWindow.ColorOf(k.Class), StandardLayers.TopographyGroup).Id;
                        l.Color = null;
                    }
            crests = result.Count(l => l.Kind == LineKind.Crest);
            toes = result.Count(l => l.Kind == LineKind.Toe);
            if (surface)
            {
                // everything surveyed lies on the ground, whatever its class: crests and toes, but also roads, windrows,
                // sumps and contours; every survey line is a breakline, the pickets fill in between
                var input = result.Cast<Entity>().Concat(_points.Where(pt => Math.Abs(pt.Position.Z) > 1e-6)).ToList();
                if (input.Count >= 3)
                {
                    var s = Surfaces.FromEntities(input, true, 2.0, 60);
                    s.Name = Loc.T("Survey surface");
                    p.Add(s, p.GetOrCreateLayer(StandardLayers.Surfaces, 0xFF64748B, StandardLayers.TopographyGroup));
                }
            }
        });
        _vm.SetStatus(Loc.F("Bench elements: {0} crests, {1} toes", crests, toes));
    }
}
