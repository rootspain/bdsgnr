using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;

namespace BlastDesigner.App.Views;

/// <summary>Edit ▸ Fragmentation inputs: the Kuz-Ram rock factor (Cunningham A and Protodyakonov f) and the rock mass, drilling and size thresholds.</summary>
public static class FragmentationWindow
{
    public static async Task<FragmentationModel?> Show(MainViewModel vm)
    {
        var form = new FragmentationForm(vm.Project!);
        var w = new Window
        {
            Title = Loc.T("Fragmentation inputs"),
            Width = 640,
            Height = 820,
            MinWidth = 520,
            MinHeight = 480,
            Background = Look.Ground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = form
        };
        form.Closing = ok => { form.Applied = ok; w.Close(); };
        await Dialogs.ShowModal(w);
        return form.Applied ? form.Draft : null;
    }
}

/// <summary>
/// Content of the fragmentation inputs window. Public so the UI smoke test can reach it.
/// The rock factor has two fields: A (Cunningham, the international classification) and f (Protodyakonov, the rock
/// strength σc = 10·f MPa). They are not converted into each other by a formula: entering one puts in the other the
/// matching value of the other table (<see cref="RockTypes"/> for A, <see cref="Categories"/> for f, see <see cref="Matching"/>).
/// </summary>
public sealed class FragmentationForm : DockPanel
{
    /// <summary>Rock mass kinds of the Cunningham description: RMD 10, the joint factor, RMD 50.</summary>
    private enum Mass { Powdery, Jointed, Massive }

    private readonly BlastProject _p;
    private readonly TextBox _a, _f, _jf, _e, _drill, _max, _over, _fines;
    private readonly TextBlock _presetNote = Small();
    /// <summary>
    /// Sliders under A and f: one step for each value of their table, for A also the whole numbers inside its rows
    /// (<see cref="Stops"/>); the other field takes the matching value of the other table (<see cref="Matching"/>).
    /// </summary>
    private readonly Slider _aSlider, _fSlider;
    /// <summary>Under each slider, what its current value is: the rock type under A, the Protodyakonov category under f.</summary>
    private readonly TextBlock _aCategory = Small(), _fCategory = Small();
    /// <summary>The refresh moves the sliders: their change is not an entry then.</summary>
    private bool _movingSliders;

    /// <summary>A row of the A or f table: the rock type (category), its description and the range of values it covers.</summary>
    private readonly record struct RockRow(string RockType, string Description, double MinVal, double MaxVal);

    /// <summary>Typical values of the rock factor A by rock type, weakest first.</summary>
    private static readonly RockRow[] RockTypes =
    {
        new("Soft rock", "Shelly limestone, weak shales, coal.", 0.6, 2),
        new("Medium strength", "Sedimentary rock, jointed dolomites and limestones.", 3, 7),
        new("Strong rock", "Dense granites, quartzites, massive igneous rock.", 8, 12),
        new("Extremely strong rock", "Ferruginous quartzites (corundum, porphyry), tough rock.", 13, 18),
    };

    /// <summary>
    /// Protodyakonov strength categories X…I, weakest first: f of the category up to the f of the next one (the
    /// strongest one has its own f only) and examples of the rock.
    /// </summary>
    private static readonly RockRow[] Categories =
    {
        new("X (quick)", "Quicksands, swampy and strongly liquefied ground.", 0.3, 0.5),
        new("IX (loose)", "Loose sand, screes.", 0.5, 0.6),
        new("VIII (earthy)", "Ordinary soil, peat, wet sand.", 0.6, 0.8),
        new("VIIa (soft)", "Loess, light loam with admixtures, gravel.", 0.8, 1),
        new("VII (soft)", "Dense clay, soft brown coal.", 1, 1.5),
        new("VIa (fairly soft)", "Compacted pebbles, dense hard coal, hard clay.", 1.5, 2),
        new("VI (fairly soft)", "Soft shale, chalk, gypsum, rock salt, anthracite, frozen ground.", 2, 3),
        new("Va (medium)", "Soft shales, dense marl.", 3, 4),
        new("V (medium)", "Strong clay shale, soft sandstone and limestone.", 4, 5),
        new("IVa (fairly strong)", "Sandy shales.", 5, 6),
        new("IV (fairly strong)", "Ordinary sandstone, iron ore.", 6, 8),
        new("IIIa (strong)", "Strong limestones, marble, dolomite.", 8, 10),
        new("III (strong)", "Very strong granite, quartz ore veins, iron ore.", 10, 15),
        new("II (very strong)", "Dense granites, quartz porphyry, siliceous shale.", 15, 20),
        new("I (highest strength)", "Quartzites, basalts (extremely strong).", 20, 20),
    };

    /// <summary>Index of the row of a table a value falls in (MinVal ≤ value): the last one starting at or below it, the first one below the table.</summary>
    private static int RowOf(RockRow[] table, double value) => Math.Max(0, Array.FindLastIndex(table, r => value >= r.MinVal));

    /// <summary>
    /// The value of table <paramref name="to"/> matching a value of table <paramref name="from"/>: the value's share of
    /// the whole range of <paramref name="from"/> (lowest MinVal…highest MaxVal) taken of the whole range of
    /// <paramref name="to"/>, rounded to a whole number; 0 for 0. A value within its table that rounds the other to 0
    /// gets the lowest MinVal of <paramref name="to"/> instead.
    /// </summary>
    private static double Matching(double value, RockRow[] from, RockRow[] to)
    {
        if (value <= 0) return 0;
        double fromMin = from.Min(r => r.MinVal), fromMax = from.Max(r => r.MaxVal);
        double toMin = to.Min(r => r.MinVal), toMax = to.Max(r => r.MaxVal);
        var share = (value - fromMin) / (fromMax - fromMin);
        var matching = Math.Max(0, Math.Round(toMin + share * (toMax - toMin), MidpointRounding.AwayFromZero));
        return matching == 0 && value >= fromMin ? toMin : matching;
    }

    /// <summary>The Protodyakonov category of a value of f, with its range of f and its examples.</summary>
    private static string CategoryText(double f)
    {
        var c = Categories[RowOf(Categories, f)];
        var range = c.MaxVal > c.MinVal ? N(c.MinVal, "0.#") + "–" + N(c.MaxVal, "0.#") : N(c.MinVal, "0.#");
        return Loc.F(L("{0}: f = {1}. {2}"), L(c.RockType), range, L(c.Description));
    }

    /// <summary>
    /// The rock type of a value of A with its range of A and rock mass; a value outside every range says which types it
    /// lies between, or that it is weaker than the softest or stronger than the strongest one.
    /// </summary>
    private static string RockTypeText(double a)
    {
        string Range(RockRow r) => Loc.F(L("{0} (A = {1}–{2})"), L(r.RockType), N(r.MinVal, "0.#"), N(r.MaxVal, "0.#"));
        int i = Array.FindLastIndex(RockTypes, r => a >= r.MinVal);
        if (i < 0) return Loc.F(L("A = {0}: weaker than {1}."), N(a, "0.##"), Range(RockTypes[0]));
        var t = RockTypes[i];
        if (a < t.MaxVal) return Loc.F(L("{0}: A = {1}–{2}. {3}"), L(t.RockType), N(t.MinVal, "0.#"), N(t.MaxVal, "0.#"), L(t.Description));
        if (i == RockTypes.Length - 1) return Loc.F(L("A = {0}: stronger than {1}."), N(a, "0.##"), Range(t));
        return Loc.F(L("A = {0}: between {1} and {2}."), N(a, "0.##"), Range(t), Range(RockTypes[i + 1]));
    }

    /// <summary>
    /// The values a slider steps along: 0, the MinVal of each row of its table and the MaxVal of the last one; with
    /// <paramref name="whole"/> also the MaxVal of each row and the whole numbers inside the rows.
    /// </summary>
    private static double[] Stops(RockRow[] table, bool whole)
    {
        var stops = table.Select(r => r.MinVal).Prepend(0).Append(table[^1].MaxVal);
        if (whole)
            stops = stops.Concat(table.SelectMany(r => Enumerable.Range((int)Math.Floor(r.MinVal) + 1, Math.Max(0, (int)Math.Ceiling(r.MaxVal) - (int)Math.Floor(r.MinVal) - 1)).Select(k => (double)k).Append(r.MaxVal)));
        return stops.Distinct().Order().ToArray();
    }
    private static readonly double[] AStops = Stops(RockTypes, whole: true), FStops = Stops(Categories, whole: false);

    /// <summary>The step of a slider a value is at: the last of the <paramref name="stops"/> not above it, 0 below them.</summary>
    private static int StepOf(double[] stops, double value) => Math.Max(0, Array.FindLastIndex(stops, v => value >= v));

    /// <summary>A slider under a rock factor field, one step of 1 for each of the <paramref name="stops"/>; moving it enters the value of the step.</summary>
    private Slider RockSlider(string name, double[] stops, Action<double> enter)
    {
        var s = new Slider { Name = name, Minimum = 0, Maximum = stops.Length - 1, TickFrequency = 1, SmallChange = 1, LargeChange = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, -4, 0, -6) };
        s.ValueChanged += (_, e) => { if (!_movingSliders) enter(stops[Math.Clamp((int)Math.Round(e.NewValue), 0, stops.Length - 1)]); };
        return s;
    }

    /// <summary>The A slider moved: A of its step, f matching it.</summary>
    private void SetAStep(double a) => SetPair(a, Matching(a, RockTypes, Categories));

    /// <summary>The f slider moved: f of its step, A matching it.</summary>
    private void SetFStep(double f) => SetPair(Matching(f, Categories, RockTypes), f);

    /// <summary>A and f put in together by a slider.</summary>
    private void SetPair(double a, double f)
    {
        _aCut = _fCut = null;
        Draft.Ucs = f * 10;
        Draft.RockFactor = a;
        Draft.RockFactorSet = true;
        Refresh();
    }
    /// <summary>Under A and f: a value outside the usual range, or one cut to the allowed range.</summary>
    private readonly TextBlock _aWarn = Small(), _fWarn = Small();
    /// <summary>What the last entry was cut to the allowed range from, shown until the next entry.</summary>
    private string? _aCut, _fCut;
    /// <summary>The field being typed in: the refresh leaves its text alone.</summary>
    private TextBox? _typing;

    /// <summary>f typed in: within 0…50; A becomes the matching A (<see cref="Matching"/>).</summary>
    private void SetF(double v)
    {
        var c = Math.Clamp(v, Fragmentation.MinProtodyakonov, Fragmentation.MaxProtodyakonov);
        _fCut = c != v ? Loc.F(L("Allowed {0}–{1}: {2} taken"), N(Fragmentation.MinProtodyakonov, "0"), N(Fragmentation.MaxProtodyakonov, "0"), N(c, "0.##")) : null;
        Draft.Ucs = c * 10;
        Draft.RockFactor = Matching(c, Categories, RockTypes);
        Draft.RockFactorSet = true;
        Refresh();
    }

    /// <summary>A typed in: within 0…25, 0 too; f becomes the matching f (<see cref="Matching"/>).</summary>
    private void SetA(double v)
    {
        var c = Math.Clamp(v, Fragmentation.MinRockFactor, Fragmentation.MaxRockFactor);
        _aCut = c != v ? Loc.F(L("Allowed {0}–{1}: {2} taken"), N(Fragmentation.MinRockFactor, "0"), N(Fragmentation.MaxRockFactor, "0"), N(c, "0.##")) : null;
        Draft.RockFactor = c;
        Draft.RockFactorSet = true;
        Draft.Ucs = Matching(c, RockTypes, Categories) * 10;
        Refresh();
    }
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromUInt32(0xFFE5484D));
    private readonly List<Button> _massChips = new();
    private readonly StackPanel _jfRow = new() { Spacing = 4 };
    private readonly Grid _preview = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 10 };
    private Mass _mass;

    public FragmentationForm(BlastProject project)
    {
        _p = project;
        Draft = ProjectJson.DeepClone(project.Fragmentation);
        ReadMass();

        // hard limits cut the entry to the allowed range; soft ones only flag it (Refresh)
        // entering one rock factor puts the table value in the other while it is typed in (live)
        _a = Number("rockFactorA", null, SetA, live: true);
        _f = Number("protodyakonovF", null, SetF, live: true);
        _aSlider = RockSlider("rockFactorASlider", AStops, SetAStep);
        _fSlider = RockSlider("protodyakonovFSlider", FStops, SetFStep);
        _jf = Number("jointFactor", null, v => { Draft.JointFactor = Math.Max(0, v); Refresh(); });
        _e = Number("youngsModulus", "GPa", v => { Draft.YoungsModulus = Math.Max(0, v); Refresh(); });
        _drill = Number("drillDeviation", "m", v => { Draft.DrillDeviation = Math.Max(0, v); Refresh(); });
        _max = Number("maxBlock", "cm", v => { Draft.MaxBlockCm = Math.Max(1, v); Refresh(); });
        _over = Number("oversize", "cm", v => { Draft.OversizeCm = Math.Max(0.1, v); Refresh(); });
        _fines = Number("fines", "cm", v => { Draft.FinesCm = Math.Max(0.01, v); Refresh(); });

        var header = Header();
        SetDock(header, Dock.Top);
        Children.Add(header);
        var footer = Footer();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        var body = new StackPanel { Spacing = 18, Margin = new Thickness(16, 14, 16, 18) };
        body.Children.Add(RockFactorCard());
        body.Children.Add(Section("Rock mass", MassFields()));
        body.Children.Add(Section("Drilling", Fields(2, Labeled("Drilling accuracy (standard deviation)", _drill))));
        body.Children.Add(Section("Size distribution", Fields(3, Labeled("Largest in-situ block", _max), Labeled("Oversize above", _over), Labeled("Fines below", _fines))));
        body.Children.Add(Section("Prediction for the blast", new Border
        {
            Background = Look.Panel,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            Child = _preview
        }));
        Children.Add(new ScrollViewer { Content = body });

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control) { Commit(); Closing?.Invoke(true); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Refresh();
    }

    /// <summary>The inputs as edited; written to the project when the window is applied.</summary>
    public FragmentationModel Draft { get; private set; }

    /// <summary>
    /// The rock mass description as one of the three kinds; a jointed mass keeps RMD + JF in its joint factor, so A
    /// stays the same.
    /// </summary>
    private void ReadMass()
    {
        (_mass, var jf) = (Draft.RockMassDescription, Draft.JointFactor) switch
        {
            (10, 0) => (Mass.Powdery, 0.0),
            (50, 0) => (Mass.Massive, 0.0),
            var (rmd, j) => (Mass.Jointed, rmd + j)
        };
        if (_mass == Mass.Jointed) { Draft.RockMassDescription = 0; Draft.JointFactor = jf; }
    }

    /// <summary>Saves the inputs as they are in the window to a preset file.</summary>
    public void SavePreset(string path)
    {
        Commit();
        FragmentationPreset.Save(path, Draft);
    }

    /// <summary>Puts the inputs of a preset file in the window (applied to the blast with Apply).</summary>
    public void LoadPreset(string path)
    {
        Draft = FragmentationPreset.Load(path);
        ReadMass();
        Refresh();
    }

    private static readonly (string Name, string[] Patterns) PresetFilter = ("Fragmentation preset", new[] { "*" + FragmentationPreset.Extension });

    private async void SavePresetAs()
    {
        var path = await Dialogs.SaveFile("Save fragmentation preset", Loc.T("Fragmentation inputs") + FragmentationPreset.Extension, FragmentationPreset.Extension.TrimStart('.'), PresetFilter);
        if (path == null) return;
        try { SavePreset(path); _presetNote.Text = Loc.F(L("Saved: {0}"), Path.GetFileName(path)); }
        catch (Exception ex) { Dialogs.ShowError(ex); }
    }

    private async void OpenPreset()
    {
        var path = await Dialogs.OpenFile("Load fragmentation preset", PresetFilter);
        if (path == null) return;
        try { LoadPreset(path); _presetNote.Text = Loc.F(L("Loaded: {0}"), Path.GetFileName(path)); }
        catch (InvalidDataException) { await Dialogs.Info(L("The file is not a fragmentation preset.")); }
        catch (Exception ex) { Dialogs.ShowError(ex); }
    }
    public bool Applied { get; set; }
    public Action<bool>? Closing { get; set; }

    /// <summary>The rock factor the calculation uses with the inputs as they are now.</summary>
    public double RockFactor => Fragmentation.RockFactor(Draft, _p.Design.RockDensity);
    public double ProtodyakonovF => Draft.Ucs / 10;

    private static string L(string english) => Loc.Ui(english);
    private static string N(double v, string format = "0.0##") => v.ToString(format, CultureInfo.CurrentCulture);
    private static double? Parse(string? s) =>
        double.TryParse((s ?? "").Trim().Replace(" ", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static TextBlock Small() => new() { FontSize = 11.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap };

    // ---------- layout ----------

    private Control Header()
    {
        var title = new TextBlock { Text = Loc.T("Fragmentation inputs"), FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Look.Text };
        // presets: the inputs of a site in a file of their own, to load into another blast
        Button PresetButton(string icon, string text, string tip, Action click, string name)
        {
            var b = Ui.Flat(Ui.Row(6, Look.Icon(icon, 15, Look.TextSoft), new TextBlock { Text = L(text), VerticalAlignment = VerticalAlignment.Center }), L(tip), click, height: 30);
            b.Name = name;
            return b;
        }
        var presets = new Border
        {
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = Ui.Row(0,
                PresetButton("open", "Load preset…", "Put the inputs saved in a preset file in this window", OpenPreset, "loadPreset"),
                PresetButton("save", "Save preset…", "Save these inputs to a file, to load them into another blast", SavePresetAs, "savePreset"))
        };
        _presetNote.Margin = new Thickness(0, 4, 0, 0);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var left = new StackPanel { Children = { title, _presetNote } };
        Grid.SetColumn(presets, 1);
        g.Children.Add(left);
        g.Children.Add(presets);
        return new Border
        {
            Background = Look.Panel,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 12),
            Child = g
        };
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
            Content = L("Apply"),
            Height = 32,
            Padding = new Thickness(16, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { "primary" },
            Name = "apply"
        };
        // Not IsDefault: Enter commits the field being typed in; Ctrl+Enter applies.
        ToolTip.SetTip(ok, L("Apply") + " (Ctrl+Enter)");
        ok.Click += (_, _) => { Commit(); Closing?.Invoke(true); };
        var hint = new TextBlock { Text = L("Enter — confirm the field, Ctrl+Enter — apply, Esc — cancel"), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var buttons = Ui.Row(8, cancel, ok);
        var dock = new DockPanel();
        SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        dock.Children.Add(hint);
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 12), Child = dock };
    }

    /// <summary>The two rock factor fields side by side, each with what it means and where its value comes from.</summary>
    private Control RockFactorCard()
    {
        _aWarn.Name = "rockFactorAWarning";
        _fWarn.Name = "protodyakonovFWarning";
        _aWarn.Foreground = _fWarn.Foreground = Warn;
        _aCategory.Name = "rockFactorACategory";
        _fCategory.Name = "protodyakonovFCategory";
        _aCategory.Foreground = _fCategory.Foreground = Look.TextSoft;
        var a = new StackPanel { Spacing = 4, Children = { Label("A — international (Cunningham)"), _a, _aSlider, _aCategory, _aWarn } };
        var f = new StackPanel { Spacing = 4, Children = { Label("f — by Protodyakonov"), _f, _fSlider, _fCategory, _fWarn } };
        var fields = Fields(2, a, f);
        return Section("Rock factor", new Border
        {
            Background = Look.Panel,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    fields
                }
            }
        });
    }

    private Control MassFields()
    {
        var chips = Ui.Row(6);
        foreach (var (mass, label) in new[] { (Mass.Powdery, "Powdery, friable"), (Mass.Jointed, "Jointed"), (Mass.Massive, "Massive") })
        {
            var chip = Ui.Chip(L(label), () => SetMass(mass), _mass == mass);
            chip.Height = 30;
            chip.Tag = "mass:" + mass;
            _massChips.Add(chip);
            chips.Children.Add(chip);
        }
        _jfRow.Children.Add(Label("Joint factor JF = JPS + JPA"));
        _jfRow.Children.Add(_jf);
        var density = new TextBlock
        {
            Text = Loc.F(L("Rock density {0} t/m³ (Design data); RDI = 25·ρ − 50 = {1}"), N(_p.Design.RockDensity, "0.00"), N(25 * _p.Design.RockDensity - 50, "0.#")),
            FontSize = 11.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap
        };
        return new StackPanel
        {
            Spacing = 10,
            Children = { chips, Fields(2, Labeled("Young's modulus E", _e), _jfRow), density }
        };
    }

    private void SetMass(Mass mass)
    {
        if (mass == _mass) return;
        _mass = mass;
        switch (mass)
        {
            case Mass.Powdery: Draft.RockMassDescription = 10; Draft.JointFactor = 0; break;
            case Mass.Massive: Draft.RockMassDescription = 50; Draft.JointFactor = 0; break;
            default: Draft.RockMassDescription = 0; if (Draft.JointFactor <= 0) Draft.JointFactor = 30; break;
        }
        Refresh();
    }

    private static Control Section(string title, Control content) => new StackPanel
    {
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = L(title).ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Faint },
            content
        }
    };

    private static TextBlock Label(string text) => new() { Text = L(text), FontSize = 12, Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap };

    private static Control Labeled(string label, Control field) => new StackPanel { Spacing = 4, Children = { Label(label), field } };

    /// <summary>Grid of fields, <paramref name="columns"/> per row.</summary>
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

    // ---------- fields ----------

    /// <summary>
    /// Number field: commits on Enter or when focus leaves it; bad input goes back to the shown value. A
    /// <paramref name="live"/> field also applies each number as it is typed.
    /// </summary>
    private TextBox Number(string name, string? unit, Action<double> set, bool live = false)
    {
        var box = new TextBox
        {
            Name = name,
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(8, 5),
            FontFamily = Look.Mono,
            Background = Look.Field,
            BorderBrush = Look.LineStrong,
            InnerRightContent = unit == null ? null : new TextBlock { Text = L(unit), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }
        };
        void Commit()
        {
            if (box.Tag as string == box.Text) return;
            if (Parse(box.Text) is double v) set(v);
            else box.Text = box.Tag as string;
        }
        if (live)
            box.TextChanged += (_, _) =>
            {
                // the text the form put there itself (TextChanged comes later, from the dispatcher) is not typing
                if (box.Text == box.Tag as string || Parse(box.Text) is not double v) return;
                _typing = box;
                try { set(v); }
                finally { _typing = null; }
            };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { Commit(); box.SelectAll(); e.Handled = true; } };
        box.LostFocus += (_, _) => Commit();
        return box;
    }

    private void Show(TextBox box, double v, string format = "0.0##")
    {
        // the field being typed in keeps what is typed (its value is already in the draft)
        if (box == _typing) return;
        // the text and what it was set to (Tag): an edit is a text that differs from it
        var text = N(v, format);
        box.Tag = text;
        box.Text = text;
    }

    /// <summary>Commits the field that still has focus (its edit is otherwise recorded when focus leaves it).</summary>
    private void Commit()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox box) box.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(LostFocusEvent));
    }

    /// <summary>Shows the draft: both rock factors, where A comes from, the rock mass and the prediction it gives.</summary>
    private void Refresh()
    {
        Show(_a, RockFactor, "0.##");
        Show(_f, ProtodyakonovF, "0.##");
        Show(_jf, Draft.JointFactor, "0.#");
        Show(_e, Draft.YoungsModulus, "0.#");
        Show(_drill, Draft.DrillDeviation, "0.0#");
        Show(_max, Draft.MaxBlockCm, "0.#");
        Show(_over, Draft.OversizeCm, "0.#");
        Show(_fines, Draft.FinesCm, "0.##");
        // each slider at the step of its value
        _movingSliders = true;
        _aSlider.Value = StepOf(AStops, RockFactor);
        _fSlider.Value = StepOf(FStops, ProtodyakonovF);
        _movingSliders = false;
        _aCategory.Text = RockTypeText(RockFactor);
        _fCategory.Text = CategoryText(ProtodyakonovF);
        // the usual range of each is the range of its table: lowest MinVal to highest MaxVal
        Flag(_a, _aWarn, RockFactor, RockTypes.Min(r => r.MinVal), RockTypes.Max(r => r.MaxVal), _aCut);
        Flag(_f, _fWarn, ProtodyakonovF, Categories.Min(r => r.MinVal), Categories.Max(r => r.MaxVal), _fCut);
        // a cut entry is said once; while typing the field keeps saying it
        if (_typing == null) _aCut = _fCut = null;
        for (int i = 0; i < _massChips.Count; i++) Ui.SetChip(_massChips[i], (int)_mass == i);
        _jfRow.IsVisible = _mass == Mass.Jointed;
        RefreshPreview();
    }

    /// <summary>
    /// Highlights a field whose value is outside the usual range and says so under it; an entry just cut to the allowed
    /// range says that instead.
    /// </summary>
    private static void Flag(TextBox box, TextBlock warning, double value, double usualMin, double usualMax, string? cut)
    {
        var unusual = value < usualMin || value > usualMax;
        box.BorderBrush = unusual || cut != null ? Warn : Look.LineStrong;
        warning.Text = cut ?? (unusual ? Loc.F(L("Outside the usual range {0}–{1}"), N(usualMin, "0.#"), N(usualMax, "0.#")) : "");
        warning.IsVisible = warning.Text.Length > 0;
    }

    /// <summary>X50, X80, oversize and fines of the blast with the inputs as edited.</summary>
    private void RefreshPreview()
    {
        _preview.Children.Clear();
        var saved = _p.Fragmentation;
        Fragmentation.Result r;
        try { _p.Fragmentation = Draft; r = Fragmentation.Compute(_p); }
        finally { _p.Fragmentation = saved; }
        if (r.X50Cm <= 0)
        {
            _preview.Children.Add(new TextBlock { Text = L("Load the holes to see the prediction: it needs the powder factor and the charge per hole."), Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap, [Grid.ColumnSpanProperty] = 4 });
            return;
        }
        var cells = new (string Label, string Value, bool Warn)[]
        {
            ("X50", Loc.F(L("{0} cm"), N(r.X50Cm, "0.0")), false),
            ("X80", Loc.F(L("{0} cm"), N(r.X80Cm, "0.0")), false),
            (L("Oversize"), N(r.OversizePct, "0.0") + " %", r.OversizePct > 10),
            (L("Fines"), N(r.FinesPct, "0.0") + " %", false)
        };
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = cells[i].Label, FontSize = 11.5, Foreground = Look.Faint },
                    new TextBlock { Text = cells[i].Value, FontSize = 18, FontWeight = FontWeight.SemiBold, FontFamily = Look.Mono, Foreground = cells[i].Warn ? Look.Accent : Look.Text, Name = "preview" + i }
                }
            };
            Grid.SetColumn(cell, i);
            _preview.Children.Add(cell);
        }
    }
}
