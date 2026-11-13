using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.Views;

/// <summary>What the hole form changed: field edits, a new layer and a new charge column, applied to every edited hole.</summary>
public sealed class HoleEdit
{
    /// <summary>Field edits by field name; each sets one value on a hole.</summary>
    public Dictionary<string, Action<Hole>> Fields { get; } = new();
    public Guid? LayerId { get; set; }
    /// <summary>Charge column edited with "Edit loading…", for a hole of <see cref="DecksLength"/>; null when untouched.</summary>
    public List<Deck>? Decks { get; set; }
    public double DecksLength { get; set; }

    public bool LengthChanged => Fields.ContainsKey(nameof(Hole.Length)) || Fields.ContainsKey(nameof(Hole.ActualLength));
    public bool StateChanged => Fields.ContainsKey(nameof(Hole.State));
    public bool Any => Fields.Count > 0 || LayerId != null || Decks != null;
}

/// <summary>Edit ▸ selected holes: hand-made form for one hole or several (fields that differ show "mixed").</summary>
public static class HoleEditWindow
{
    public static async Task<HoleEdit?> Show(MainViewModel vm, IReadOnlyList<Hole> holes)
    {
        var form = new HoleEditForm(vm, holes);
        var w = new Window
        {
            Title = holes.Count == 1 ? Loc.F("Edit {0}", Loc.T(holes[0].KindName)) : Loc.Ui("Edit holes"),
            Width = 600,
            Height = 820,
            MinWidth = 520,
            MinHeight = 480,
            Background = Look.Ground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = form
        };
        form.Closing = ok => { form.Applied = ok; w.Close(); };
        await Dialogs.ShowModal(w);
        return form.Applied && form.Edit.Any ? form.Edit : null;
    }
}

/// <summary>Content of the hole form. Public so the UI smoke test can reach it.</summary>
public sealed class HoleEditForm : DockPanel
{
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromUInt32(0xFFF0C13A));

    private readonly MainViewModel _vm;
    private readonly BlastProject _p;
    private readonly IReadOnlyList<Hole> _holes;
    /// <summary>Copy of the first hole with the edits applied: what the form shows and what the loading window starts from.</summary>
    private readonly Hole _draft;
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _charge = new() { Spacing = 4 };
    private readonly TextBlock _toe = new() { FontFamily = Look.Mono, FontSize = 11.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _states = new();
    private readonly WrapPanel _exceptions = new();
    private HoleException _exceptionEdits;

    public HoleEditForm(MainViewModel vm, IReadOnlyList<Hole> holes)
    {
        _vm = vm;
        _p = vm.Project!;
        _holes = holes;
        _draft = ProjectJson.DeepClone(holes[0]);

        var header = Header();
        SetDock(header, Dock.Top);
        Children.Add(header);
        var footer = Footer();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        var body = new StackPanel { Spacing = 18, Margin = new Thickness(16, 14, 16, 18) };
        if (holes.Count > 1) body.Children.Add(Note(Loc.F("{0} holes: fields marked \"mixed\" differ between them; only the fields you change are applied to all.", holes.Count)));
        body.Children.Add(ChargeCard());
        body.Children.Add(Section("Collar", CollarFields()));
        body.Children.Add(Section("Geometry", GeometryFields()));
        body.Children.Add(Section("Classification", ClassFields()));
        body.Children.Add(Section("State", _states));
        body.Children.Add(Section("Hole exceptions", _exceptions));
        body.Children.Add(Section("Initiation and loading", InitiationFields()));
        body.Children.Add(Section("Notes", TextField(nameof(Hole.Notes), h => h.Notes, (h, v) => h.Notes = v, multiline: true)));
        Children.Add(new ScrollViewer { Content = body });

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control) { Commit(); Closing?.Invoke(true); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Refresh();
    }

    public HoleEdit Edit { get; } = new();
    public bool Applied { get; set; }
    public Action<bool>? Closing { get; set; }

    private static string L(string english) => Loc.Ui(english);
    private static string N(double v, string format = "0.0##") => v.ToString(format, CultureInfo.CurrentCulture);
    private static double? Parse(string? s) =>
        double.TryParse((s ?? "").Trim().Replace(" ", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static string MixedText => L("mixed");

    private bool Mixed<T>(Func<Hole, T> get) => _holes.Skip(1).Any(h => !EqualityComparer<T>.Default.Equals(get(h), get(_holes[0])));

    /// <summary>Records a field edit and applies it to the draft so the rest of the form shows the new value.</summary>
    private void Set(string key, Action<Hole> apply)
    {
        Edit.Fields[key] = apply;
        apply(_draft);
        Refresh();
    }

    // ---------- frame ----------

    private Control Header()
    {
        var title = new TextBlock
        {
            Text = _holes.Count == 1 ? Loc.F("Hole {0}", _holes[0].Label) : Loc.F("Holes selected: {0}", _holes.Count),
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = Look.Text,
            VerticalAlignment = VerticalAlignment.Center
        };
        var layers = _p.Layers;
        var layerIndex = layers.FindIndex(l => l.Id == _holes[0].LayerId);
        var layer = new ComboBox
        {
            ItemsSource = layers.Select(l => Loc.Name(l.Name)).ToList(),
            SelectedIndex = Mixed(h => h.LayerId) ? -1 : layerIndex,
            PlaceholderText = MixedText,
            MinWidth = 150,
            Height = 30,
            VerticalAlignment = VerticalAlignment.Center
        };
        layer.SelectionChanged += (_, _) => { if (layer.SelectedIndex >= 0) Edit.LayerId = layers[layer.SelectedIndex].Id; };
        var right = Ui.Row(8, new TextBlock { Text = Loc.T("Layer"), Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center }, layer);
        right.Margin = new Thickness(12, 0, 0, 0);
        right.VerticalAlignment = VerticalAlignment.Top;
        // Title and chips wrap onto a second line in a narrow window instead of running under the layer list.
        title.Margin = new Thickness(0, 0, 12, 0);
        var left = new WrapPanel { Children = { title, _chips } };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(right, 1);
        g.Children.Add(left);
        g.Children.Add(right);
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(16, 12), Child = g };
    }

    private Control Footer()
    {
        var cancel = new Button
        {
            Content = Loc.T("Cancel"),
            Height = 32,
            Padding = new Thickness(14, 0),
            CornerRadius = new CornerRadius(6),
            BorderBrush = Look.LineStrong,
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsCancel = true
        };
        cancel.Click += (_, _) => Closing?.Invoke(false);
        var ok = new Button
        {
            Content = _holes.Count > 1 ? Loc.F("Apply to {0} holes", _holes.Count) : L("Apply"),
            Height = 32,
            Padding = new Thickness(16, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { "primary" }
        };
        // Not IsDefault: Enter commits the field being typed in; Ctrl+Enter applies.
        ToolTip.SetTip(ok, L("Apply") + " (Ctrl+Enter)");
        ok.Click += (_, _) => { Commit(); Closing?.Invoke(true); };
        var hint = new TextBlock { Text = L("Enter — confirm the field, Ctrl+Enter — apply, Esc — cancel"), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var buttons = Ui.Row(8, cancel, ok);
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        dock.Children.Add(hint);
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 12), Child = dock };
    }

    /// <summary>Commits the field that still has focus (its edit is otherwise recorded when focus leaves it).</summary>
    private void Commit()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox box) box.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(LostFocusEvent));
    }

    private static Control Section(string title, Control content) => new StackPanel
    {
        Spacing = 8,
        Children =
        {
            new TextBlock
            {
                Text = L(title).ToUpper(CultureInfo.CurrentCulture),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                LetterSpacing = 0.6,
                Foreground = Look.Faint
            },
            content
        }
    };

    private static Control Note(string text) => new Border
    {
        Background = new SolidColorBrush(Color.FromUInt32(0x1A6AA8FF)),
        BorderBrush = Look.Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12, 8),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Look.TextSoft, FontSize = 12.5 }
    };

    private static Control Labeled(string label, Control field) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = L(label), FontSize = 12, Foreground = Look.Muted }, field }
    };

    /// <summary>Grid of labelled fields, <paramref name="columns"/> per row.</summary>
    private static Grid Fields(int columns, params Control[] cells)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",12,", Enumerable.Repeat("*", columns))) };
        var rows = (cells.Length + columns - 1) / columns;
        for (int r = 0; r < rows; r++)
        {
            if (r > 0) g.RowDefinitions.Add(new RowDefinition(10, GridUnitType.Pixel));
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
        for (int i = 0; i < cells.Length; i++)
        {
            Grid.SetRow(cells[i], i / columns * 2);
            Grid.SetColumn(cells[i], i % columns * 2);
            g.Children.Add(cells[i]);
        }
        return g;
    }

    // ---------- editors ----------

    private static TextBox Box(string? unit, bool mono = true) => new()
    {
        Height = 32,
        MinHeight = 32,
        Padding = new Thickness(8, 5),
        FontFamily = mono ? Look.Mono : FontFamily.Default,
        Background = Look.Field,
        BorderBrush = Look.LineStrong,
        InnerRightContent = unit == null ? null : new TextBlock { Text = L(unit), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }
    };

    /// <summary>
    /// Number field: commits on Enter or when focus leaves it. Empty clears a value that may be missing (<paramref name="empty"/>
    /// is its placeholder); a field left empty while showing "mixed" changes nothing; bad input goes back to the last value.
    /// </summary>
    private TextBox Number(string key, Func<Hole, double?> get, Action<Hole, double?> set, string? unit, string format = "0.0##", string? empty = null, Func<double?, string?>? check = null)
    {
        var mixed = Mixed(get);
        var box = Box(unit);
        box.Name = key;
        string Show(double? v) => v is double d ? N(d, format) : "";
        box.Text = mixed ? "" : Show(get(_draft));
        box.Watermark = mixed ? MixedText : empty;
        var shown = box.Text;
        // a value put in from outside (a button beside the field): shown as the field's own, "mixed" when the holes differ
        _reshow[key] = values =>
        {
            var distinct = values.Distinct().ToList();
            box.Text = shown = distinct.Count == 1 ? Show(distinct[0]) : "";
            box.Watermark = distinct.Count == 1 ? empty : MixedText;
        };
        void Commit()
        {
            if (box.Text == shown) return;
            var text = (box.Text ?? "").Trim();
            double? value;
            if (text.Length == 0)
            {
                if (empty == null) { box.Text = shown; return; }
                value = null;
            }
            else if (Parse(text) is double d) value = d;
            else { box.Text = shown; return; }
            // a value the holes cannot take: why, and the field goes back to the last value
            if (check?.Invoke(value) is { } error) { box.Text = shown; _ = Dialogs.Info(error); return; }
            Set(key, h => set(h, value));
            box.Text = shown = Show(get(_draft));
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { Commit(); box.SelectAll(); e.Handled = true; } };
        box.LostFocus += (_, _) => Commit();
        return box;
    }

    private TextBox TextField(string key, Func<Hole, string> get, Action<Hole, string> set, bool multiline = false)
    {
        var mixed = Mixed(get);
        var box = Box(null, mono: false);
        box.Name = key;
        // text even when it is digits (a hole number): the arrows do not step it
        box.Classes.Add(NumberStep.TextClass);
        box.Text = mixed ? "" : get(_draft);
        box.Watermark = mixed ? MixedText : null;
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.Height = double.NaN;
            box.MinHeight = 64;
        }
        var shown = box.Text;
        void Commit()
        {
            if (box.Text == shown) return;
            var v = box.Text ?? "";
            Set(key, h => set(h, v));
            shown = v;
        }
        if (!multiline) box.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { Commit(); box.SelectAll(); e.Handled = true; } };
        box.LostFocus += (_, _) => Commit();
        return box;
    }

    /// <summary>Switch for a yes/no field; a mixed field starts in the middle state until it is clicked.</summary>
    private Control Toggle(string label, string key, Func<Hole, bool> get, Action<Hole, bool> set)
    {
        var mixed = Mixed(get);
        var sw = new ToggleSwitch { IsChecked = mixed ? null : get(_draft), IsThreeState = mixed, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        sw.IsCheckedChanged += (_, _) =>
        {
            if (sw.IsChecked is not bool v) return;
            sw.IsThreeState = false;
            Set(key, h => set(h, v));
        };
        if (mixed) ToolTip.SetTip(sw, MixedText);
        var text = new TextBlock { Text = L(label), Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var dock = new DockPanel { MinHeight = 32 };
        DockPanel.SetDock(sw, Dock.Right);
        dock.Children.Add(sw);
        dock.Children.Add(text);
        return dock;
    }

    /// <summary>List of the project's hole or material types, each with its colour.</summary>
    private Control TypeList(string key, List<TypeDefinition> types, Func<Hole, int> get, Action<Hole, int> set, bool dots)
    {
        var mixed = Mixed(get);
        var items = types.Select(t => (Control)Ui.Row(8,
            dots ? new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromUInt32(t.Color)), VerticalAlignment = VerticalAlignment.Center } : new Border { Width = 0 },
            new TextBlock { Text = Loc.Name(t.Name), VerticalAlignment = VerticalAlignment.Center })).ToList();
        var index = types.FindIndex(t => t.Index == get(_draft));
        if (!mixed && index < 0)
        {
            // A type number the project does not define: keep it selectable as is.
            items.Add(new TextBlock { Text = Loc.F("Type {0}", get(_draft)) });
            index = items.Count - 1;
        }
        var combo = new ComboBox { ItemsSource = items, SelectedIndex = mixed ? -1 : index, PlaceholderText = MixedText, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Height = 32 };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0 || combo.SelectedIndex >= types.Count) return;
            var v = types[combo.SelectedIndex].Index;
            Set(key, h => set(h, v));
        };
        combo.Name = key;
        return combo;
    }

    // ---------- sections ----------

    private Control ChargeCard()
    {
        var loadable = _holes.Any(h => !h.IsDummy);
        var button = new Button
        {
            Content = Ui.Row(6, Look.Icon("edit", 16, Look.OnAccent), new TextBlock { Text = L("Edit loading…"), VerticalAlignment = VerticalAlignment.Center }),
            Height = 34,
            Padding = new Thickness(14, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = loadable,
            Classes = { "primary" },
            Tag = "editLoading"
        };
        if (!loadable) ToolTip.SetTip(button, L("Dummy holes are not loaded"));
        button.Click += async (_, _) =>
        {
            var count = _holes.Count(h => !h.IsDummy);
            var owner = TopLevel.GetTopLevel(this) as Window;
            var decks = await HoleLoadingWindow.Show(_vm, _draft, count, owner);
            if (decks == null) return;
            Edit.Decks = decks;
            Edit.DecksLength = _draft.EffectiveLength;
            _draft.Decks = decks.Select(d => d.Clone()).ToList();
            Refresh();
        };
        var icon = Look.Icon("charge", 20, Look.Accent);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 12, 0);
        var dock = new DockPanel();
        DockPanel.SetDock(button, Dock.Right);
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(button);
        dock.Children.Add(icon);
        _charge.Margin = new Thickness(0, 0, 12, 0);
        dock.Children.Add(_charge);
        return new Border
        {
            Background = Look.Panel,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            Child = dock
        };
    }

    private void FillCharge()
    {
        _charge.Children.Clear();
        var title = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Look.Text };
        var h = _draft;
        if (h.IsDummy && _holes.All(x => x.IsDummy))
        {
            title.Text = L("Dummy hole: not loaded");
            _charge.Children.Add(title);
            return;
        }
        if (h.Decks.Count == 0)
        {
            title.Text = L("Not loaded");
            _charge.Children.Add(title);
            _charge.Children.Add(new TextBlock { Text = L("Build the charge column in the loading window."), FontSize = 12, Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap });
            return;
        }
        title.Text = Edit.Decks != null ? L("Charge (changed, applied with the form)") : L("Charge");
        _charge.Children.Add(title);
        var column = new TextBlock { FontSize = 12.5, Foreground = Look.TextSoft, TextWrapping = TextWrapping.Wrap };
        var first = true;
        foreach (var d in h.Decks.OrderBy(d => d.Top))
        {
            var prod = _p.FindProduct(d.ProductId);
            if (!first) column.Inlines!.Add(new Run("  ·  ") { Foreground = Look.Faint });
            first = false;
            column.Inlines!.Add(new Run("■ ") { Foreground = new SolidColorBrush(Color.FromUInt32(prod?.Color ?? 0xFF808080)) });
            column.Inlines.Add(new Run((prod?.Name ?? "?") + " "));
            column.Inlines.Add(new Run(N(d.Length, "0.0") + " " + L("m")) { FontFamily = Look.Mono, Foreground = Look.Text });
        }
        _charge.Children.Add(column);
        var primers = h.Decks.Sum(d => d.Primers.Count);
        _charge.Children.Add(new TextBlock
        {
            FontSize = 12,
            Foreground = Look.Muted,
            Inlines = new InlineCollection
            {
                new Run(L("Explosive") + " "), new Run(N(Charging.ExplosiveMass(_p, h), "0.0") + " " + L("kg")) { FontFamily = Look.Mono, Foreground = Look.Text },
                new Run("   " + L("Primers") + " "), new Run(primers.ToString(CultureInfo.CurrentCulture)) { FontFamily = Look.Mono, Foreground = Look.Text },
                new Run("   " + L("Top stemming") + " "), new Run(N(Charging.TopStemming(_p, h), "0.0") + " " + L("m")) { FontFamily = Look.Mono, Foreground = Look.Text }
            }
        });
        if (Charging.HasLoadingWarning(_p, h))
            _charge.Children.Add(Ui.Row(6, Look.Icon("warn", 14, Warn), new TextBlock { Text = L("The charge has warnings: see the loading window"), FontSize = 12, Foreground = Warn, VerticalAlignment = VerticalAlignment.Center }));
        if (_holes.Count > 1)
            _charge.Children.Add(new TextBlock { Text = L("Column of the first hole; it adapts to the length of each hole."), FontSize = 11.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap });
    }

    private Control CollarFields()
    {
        // Moving the collar moves the surveyed bore track with it.
        TextBox Axis(string key, Func<Vec3, double> get, Func<double, Vec3> offset) =>
            Number(key, h => get(h.Collar), (h, v) => { if (v is double d) h.Translate(offset(d - get(h.Collar))); }, "m", "0.000");
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Fields(3,
                    Labeled("X", Axis("CollarX", c => c.X, d => new Vec3(d, 0, 0))),
                    Labeled("Y", Axis("CollarY", c => c.Y, d => new Vec3(0, d, 0))),
                    Labeled("Z", Axis("CollarZ", c => c.Z, d => new Vec3(0, 0, d)))),
                _toe
            }
        };
    }

    /// <summary>Puts a value in a number field from outside it, by its key: the values of the holes, one for each.</summary>
    private readonly Dictionary<string, Action<IEnumerable<double?>>> _reshow = new();

    /// <summary>
    /// Elevation the holes go down to: the length follows it; refused when a collar is not above it. The button beside it
    /// puts in each hole's level where its line meets the first surface; a hole that meets none keeps its own.
    /// </summary>
    private Control ToLevel()
    {
        var box = Number(nameof(Hole.ToLevel), h => h.ToLevel, (h, v) => h.ToLevel = v, "m", "0.0##", L("not set"), v =>
            v is double z && _holes.FirstOrDefault(h => !HoleLevel.Reaches(h, z)) is { } low
                ? Loc.F("The collar of hole {0} ({1:0.###} m) is not above the level {2:0.###} m.", low.Label, low.Collar.Z, z)
                : null);
        ToolTip.SetTip(box, PropertiesPanel.LengthTip());
        var toSurface = Ui.Flat(Look.Icon("surface", 14, Look.TextSoft), L("To the surface: the holes take as their level the elevation where their line from the collar meets the grade surface, or the first surface when they meet no grade"), () => _ = ToSurface(), 32, 32);
        toSurface.Name = "holeToSurface";
        toSurface.Margin = new Thickness(4, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(toSurface, L("To the surface"));
        DockPanel.SetDock(toSurface, Dock.Right);
        return new DockPanel { Children = { toSurface, box } };

        async Task ToSurface()
        {
            if (!_p.Entities.OfType<SurfaceEntity>().Any()) { await Dialogs.Info(Loc.T("The project has no surface to drill the holes down to.")); return; }
            var levels = _holes.ToDictionary(h => h.Id, h => HoleLevel.SurfaceLevel(_p, h) is double z && HoleLevel.Reaches(h, z) ? z : (double?)null);
            var missed = levels.Count(l => l.Value == null);
            if (missed == _holes.Count) { await Dialogs.Info(Loc.F("{0} holes meet no surface and keep their length", missed)); return; }
            // each hole its own level; one that meets no surface keeps what it has (the edit made in this form, if any)
            var before = Edit.Fields.TryGetValue(nameof(Hole.ToLevel), out var typed) ? typed : null;
            var typedLevel = _draft.ToLevel;
            Set(nameof(Hole.ToLevel), h =>
            {
                if (levels.TryGetValue(h.Id, out var z) && z != null) h.ToLevel = z;
                else before?.Invoke(h);
            });
            // every hole's level from the surface its own line meets first, which may be a different surface for each
            _reshow[nameof(Hole.ToLevel)](_holes.Select(h => levels[h.Id] ?? (before == null ? h.ToLevel : typedLevel)));
            if (missed > 0) await Dialogs.Info(Loc.F("{0} holes meet no surface and keep their length", missed));
        }
    }

    /// <summary>A field of the length with the tip of how it is counted.</summary>
    private static TextBox LengthTipped(TextBox box)
    {
        ToolTip.SetTip(box, PropertiesPanel.LengthTip());
        return box;
    }

    private Control GeometryFields() => Fields(2,
        Labeled("Length", LengthTipped(Number(nameof(Hole.Length), h => h.Length, (h, v) => h.Length = Math.Max(0, v ?? 0), "m"))),
        Labeled("To level", ToLevel()),
        Labeled("Diameter", Number(nameof(Hole.Diameter), h => h.Diameter, (h, v) => h.Diameter = Math.Max(0, v ?? 0), "mm", "0.#")),
        Labeled("Subdrill", Number(nameof(Hole.Subdrill), h => h.Subdrill, (h, v) => h.Subdrill = v ?? 0, "m")),
        Labeled("Actual length", Number(nameof(Hole.ActualLength), h => h.ActualLength, (h, v) => h.ActualLength = v, "m", empty: L("not measured"))),
        Labeled("Angle", Number(nameof(Hole.Angle), h => h.Angle, (h, v) => h.Angle = Math.Clamp(v ?? 0, 0, 180), "°", "0.#")),
        Labeled("Bearing", Number(nameof(Hole.Bearing), h => h.Bearing, (h, v) => h.Bearing = GeoMath.NormalizeBearing(v ?? 0), "°", "0.#")),
        Labeled("Water depth", Number(nameof(Hole.WaterDepth), h => h.WaterDepth, (h, v) => h.WaterDepth = Math.Max(0, v ?? 0), "m")),
        Labeled("Temperature", Number(nameof(Hole.Temperature), h => h.Temperature, (h, v) => h.Temperature = v, "°C", "0.#", L("not measured"))));

    private Control ClassFields() => new StackPanel
    {
        Spacing = 10,
        Children =
        {
            Fields(2,
                Labeled("Number", TextField(nameof(Hole.Label), h => h.Label, (h, v) => h.Label = v)),
                new Border(),
                Labeled("Hole type", TypeList(nameof(Hole.HoleType), _p.Design.HoleTypes, h => h.HoleType, (h, v) => h.HoleType = v, dots: true)),
                Labeled("Material type", TypeList(nameof(Hole.MaterialType), _p.Design.MaterialTypes, h => h.MaterialType, (h, v) => h.MaterialType = v, dots: true))),
            Fields(2,
                Toggle("Dummy", nameof(Hole.IsDummy), h => h.IsDummy, (h, v) => h.IsDummy = v),
                Toggle("Locked", nameof(Hole.Locked), h => h.Locked, (h, v) => h.Locked = v))
        }
    };

    private Control InitiationFields() => new StackPanel
    {
        Spacing = 10,
        Children =
        {
            Fields(2,
                Labeled("Electronic time", Number(nameof(Hole.ElectronicTimeMs), h => h.ElectronicTimeMs, (h, v) => h.ElectronicTimeMs = v, "ms", "0.#", L("not set"))),
                Labeled("Detonator ID", TextField(nameof(Hole.DetonatorId), h => h.DetonatorId, (h, v) => h.DetonatorId = v)),
                Labeled("Horizon", Number(nameof(Hole.Horizon), h => h.Horizon, (h, v) => h.Horizon = (int)Math.Round(v ?? 0), null, "0")),
                Labeled("Loading factor", Number(nameof(Hole.LoadingFactor), h => h.LoadingFactor, (h, v) => h.LoadingFactor = Math.Max(0, v ?? 1), null, "0.0##"))),
            Fields(2,
                Toggle("Time set by hand", nameof(Hole.ElectronicTimeManual), h => h.ElectronicTimeManual, (h, v) => h.ElectronicTimeManual = v),
                Toggle("Column edited by hand", nameof(Hole.ManualLoading), h => h.ManualLoading, (h, v) => h.ManualLoading = v))
        }
    };

    private void FillStates()
    {
        _states.Children.Clear();
        var mixed = Mixed(h => h.State) && !Edit.StateChanged;
        foreach (var s in Enum.GetValues<HoleState>())
        {
            var state = s;
            var chip = Ui.Chip(Loc.Enum(s), () => Set(nameof(Hole.State), h => h.State = state), active: !mixed && _draft.State == s);
            chip.Margin = new Thickness(0, 0, 6, 6);
            _states.Children.Add(chip);
        }
    }

    private void FillExceptions()
    {
        _exceptions.Children.Clear();
        long and = -1, or = 0;
        foreach (var h in _holes) { and &= (long)h.Exceptions; or |= (long)h.Exceptions; }
        foreach (var x in Enum.GetValues<HoleException>())
        {
            if (x == HoleException.None) continue;
            var bit = x;
            var mixed = ((and ^ or) & (long)x) != 0 && (_exceptionEdits & x) == 0;
            var on = _draft.Exceptions.HasFlag(x);
            var chip = Ui.Chip(Loc.Enum(x), () =>
            {
                var value = !_draft.Exceptions.HasFlag(bit) || (((and ^ or) & (long)bit) != 0 && (_exceptionEdits & bit) == 0);
                _exceptionEdits |= bit;
                Set("Exception." + bit, h => h.Exceptions = value ? h.Exceptions | bit : h.Exceptions & ~bit);
            }, active: on && !mixed, tip: mixed ? MixedText : null);
            if (mixed)
            {
                chip.BorderBrush = Look.Accent;
                chip.Foreground = Look.TextSoft;
            }
            chip.Margin = new Thickness(0, 0, 6, 6);
            _exceptions.Children.Add(chip);
        }
    }

    private void FillChips()
    {
        _chips.Children.Clear();
        Control Chip(Control content) => new Border
        {
            Height = 24,
            Padding = new Thickness(9, 0),
            CornerRadius = new CornerRadius(12),
            Background = Look.Ribbon,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content
        };
        var mixedType = Mixed(h => h.HoleType) && !Edit.Fields.ContainsKey(nameof(Hole.HoleType));
        var type = _p.Design.HoleTypes.FirstOrDefault(t => t.Index == _draft.HoleType);
        _chips.Children.Add(Chip(Ui.Row(6,
            new Ellipse { Width = 8, Height = 8, Fill = mixedType ? Look.Faint : new SolidColorBrush(Color.FromUInt32(type?.Color ?? 0xFF808080)), VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = mixedType ? L("Types differ") : Loc.Name(type?.Name ?? Loc.F("Type {0}", _draft.HoleType)), FontSize = 12, Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center })));
        var mixedState = Mixed(h => h.State) && !Edit.StateChanged;
        _chips.Children.Add(Chip(new TextBlock { Text = mixedState ? L("States differ") : Loc.Enum(_draft.State), FontSize = 12, Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center }));
        if (_draft.IsDummy) _chips.Children.Add(Chip(new TextBlock { Text = L("Dummy"), FontSize = 12, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center }));
    }

    private void Refresh()
    {
        FillChips();
        FillCharge();
        FillStates();
        FillExceptions();
        var t = _draft.Toe;
        _toe.Text = _holes.Count == 1 || !Edit.Fields.Keys.Any(k => k.StartsWith("Collar", StringComparison.Ordinal))
            ? L("Toe") + ":  X " + N(t.X, "0.000") + "   Y " + N(t.Y, "0.000") + "   Z " + N(t.Z, "0.000") + (_holes.Count > 1 ? "  (" + L("first hole") + ")" : "")
            : "";
    }
}
