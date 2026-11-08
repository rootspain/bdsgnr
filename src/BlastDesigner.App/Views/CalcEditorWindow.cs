using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Calc;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;

namespace BlastDesigner.App.Views;

/// <summary>
/// Edit ▸ Calculations…: the calculation profile of the project in three columns — the tree of contexts, properties,
/// parameters and built-in algorithms; the formula with its variants, check while typing and before/after preview on the
/// selected holes; what it depends on and what uses it. Changes go to a draft until Publish.
/// </summary>
public static class CalcEditorWindow
{
    public static async Task Show(MainViewModel vm)
    {
        if (vm.Project == null) return;
        var form = new CalcEditorForm(vm);
        var w = new Window
        {
            Title = Loc.T("Calculations (formulas)"),
            Width = 1320,
            Height = 820,
            MinWidth = 980,
            MinHeight = 560,
            Background = Look.Ground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = form
        };
        form.CloseRequested = () => w.Close();
        await Dialogs.ShowModal(w);
    }
}

/// <summary>Content of the calculations window. Public so the UI smoke test can reach it.</summary>
public sealed class CalcEditorForm : DockPanel
{
    private static readonly IBrush Ok = new SolidColorBrush(Color.FromUInt32(0xFF5BC08A));
    private static readonly IBrush Bad = new SolidColorBrush(Color.FromUInt32(0xFFE5675C));
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromUInt32(0xFFF0C13A));

    private readonly MainViewModel _vm;
    private readonly BlastProject _project;
    private CalcModel _published;
    private CalcSession _publishedSession;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly TreeView _tree = new() { Background = Brushes.Transparent };
    private readonly TextBox _search = FormKit.Text();
    private readonly TextBox _name = FormKit.Text();
    private readonly TextBox _version = FormKit.Text();
    private readonly Border _draftBadge;
    private readonly StackPanel _center = new() { Spacing = 16, Margin = new Thickness(18, 14, 18, 18) };
    private readonly StackPanel _right = new() { Spacing = 14, Margin = new Thickness(14) };
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _formulaTab, _testsTab;
    private bool _testsOpen;
    private CalcSession? _draftSession;
    private Action? _afterCompile;

    public CalcEditorForm(MainViewModel vm)
    {
        _vm = vm;
        _project = vm.Project!;
        _published = CalcService.ModelFor(_project);
        _publishedSession = CalcService.Session(_project, _published);
        Draft = _project.Calc?.ProfileJson is { Length: > 0 } json && TryLoad(json) is { } own ? own
            : CalcProfile.DeriveFrom(CalcService.BuiltIn, _project.Calc?.ProfileName is { Length: > 0 } n && n != CalcService.BuiltInName ? n : Loc.T("Project profile"));
        DraftModel = Compile();
        _name.Text = Draft.Name;
        _version.Text = Draft.Version;
        _name.Width = 220;
        _version.Width = 70;
        _name.TextChanged += (_, _) => { if (_name.Text is { Length: > 0 } t && t != Draft.Name) { Draft.Name = t; MarkDirty(); } };
        _version.TextChanged += (_, _) => { if (_version.Text is { } t && t != Draft.Version) { Draft.Version = t; MarkDirty(); } };
        _draftBadge = new Border
        {
            Background = Look.AccentSoft, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3), IsVisible = false, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = Loc.T("draft"), Foreground = Look.Accent, FontSize = 11.5 }
        };
        _timer.Tick += (_, _) => { _timer.Stop(); Recompile(); };

        var codeNames = new CheckBox { Content = Loc.T("Show code names"), IsChecked = _codeNames, Name = "codeNames", VerticalAlignment = VerticalAlignment.Center };
        codeNames.IsCheckedChanged += (_, _) => CodeNames = codeNames.IsChecked == true;
        var tools = Ui.Row(8,
            Labeled(Loc.T("Profile"), _name), Labeled(Loc.T("Version"), _version), _draftBadge, codeNames,
            FormKit.Secondary(Loc.T("Check"), () => { Recompile(); ShowDiagnostics(); }),
            FormKit.Secondary(Loc.T("Compare"), () => _ = Compare()),
            FormKit.Secondary(Loc.T("History…"), () => _ = ShowHistory()),
            FormKit.Primary(Loc.T("Publish"), () => _ = Publish()));
        var header = FormKit.Header(Loc.T("Calculations (formulas)"), Loc.T("Formulas of the calculated properties: changes stay in the draft until Publish."), tools);
        SetDock(header, Dock.Top);
        Children.Add(header);

        var footer = new Border
        {
            Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 10),
            Child = new DockPanel { Children = { Docked(FormKit.Secondary(Loc.T("Close"), () => CloseRequested?.Invoke()), Avalonia.Controls.Dock.Right), _status } }
        };
        SetDock(footer, Avalonia.Controls.Dock.Bottom);
        Children.Add(footer);

        // слева: поиск и дерево
        _search.Watermark = Loc.T("Search by name");
        _search.TextChanged += (_, _) => BuildTree();
        var addProperty = FormKit.Tool("plus", Loc.T("My property"), () => _ = AddProperty(), Loc.T("Add a property of your own"));
        var left = new DockPanel { Margin = new Thickness(12) };
        var leftTop = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8), Children = { _search, addProperty } };
        SetDock(leftTop, Avalonia.Controls.Dock.Top);
        left.Children.Add(leftTop);
        left.Children.Add(new ScrollViewer { Content = _tree });
        _tree.SelectionChanged += (_, _) =>
        {
            if (_tree.SelectedItem is TreeViewItem { Tag: string id }) Select(id);
        };

        // в центре: вкладки
        _formulaTab = FormKit.Tab(Loc.T("Formula"), () => { _testsOpen = false; ShowCenter(); });
        _testsTab = FormKit.Tab(Loc.T("Test cases"), () => { _testsOpen = true; ShowCenter(); });
        var tabs = new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 0), Child = Ui.Row(0, _formulaTab, _testsTab) };
        var center = new DockPanel();
        SetDock(tabs, Avalonia.Controls.Dock.Top);
        center.Children.Add(tabs);
        center.Children.Add(new ScrollViewer { Content = _center });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*,300") };
        var leftBorder = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = left };
        var rightBorder = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = _right } };
        Grid.SetColumn(center, 1);
        Grid.SetColumn(rightBorder, 2);
        grid.Children.Add(leftBorder);
        grid.Children.Add(center);
        grid.Children.Add(rightBorder);
        Children.Add(grid);

        BuildTree();
        Select(DraftModel.NodeOf("hole.chargeMass") != null ? "hole.chargeMass" : DraftModel.Nodes.FirstOrDefault()?.Id ?? "");
        UpdateStatus();
    }

    /// <summary>Called by Close (the window closes itself).</summary>
    public Action? CloseRequested { get; set; }

    /// <summary>The profile being edited: a profile of its own based on «SHOTPlus 6», or the project's own copy.</summary>
    public CalcProfile Draft { get; }
    public CalcModel DraftModel { get; private set; }
    public bool Dirty { get; private set; }
    /// <summary>What the tree selected: a property id, "param:…", "builtin:…".</summary>
    public string? Selected { get; private set; }
    /// <summary>The check of the formula being typed (for the smoke test).</summary>
    public string FormulaStatus { get; private set; } = "";
    public int AffectedHoles { get; private set; }
    public int AffectedTotals { get; private set; }
    public IReadOnlyList<(string Entity, CalcValue Before, CalcValue After)> Preview { get; private set; } = Array.Empty<(string, CalcValue, CalcValue)>();

    private static CalcProfile? TryLoad(string json)
    {
        try { return CalcProfile.FromJson(json); } catch (Exception) { return null; }
    }

    private CalcProfile Flat() => Draft.Base != null ? Draft.Flatten(CalcService.Library.Find) : Draft;

    private CalcModel Compile() => new(CalcService.Registry, Flat());

    private void MarkDirty()
    {
        Dirty = true;
        _draftBadge.IsVisible = true;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Compiles the draft now (the timer does it 300 ms after typing stops).</summary>
    public void Recompile()
    {
        _timer.Stop();
        DraftModel = Compile();
        _draftSession = null;
        AfterChange();
    }

    private void AfterChange()
    {
        UpdateStatus();
        UpdatePreview();
        BuildRight();
        _afterCompile?.Invoke();
        BuildTreeMarks();
    }

    private CalcSession DraftSession()
    {
        if (_draftSession != null) return _draftSession;
        _draftSession = CalcService.Session(_project, DraftModel);
        return _draftSession;
    }

    // ---- дерево ---------------------------------------------------------

    private readonly Dictionary<string, TextBlock> _treeMarks = new();

    private void BuildTree()
    {
        var filter = _search.Text?.Trim() ?? "";
        bool Match(string id, string ru) => filter.Length == 0 || id.Contains(filter, StringComparison.OrdinalIgnoreCase) || ru.Contains(filter, StringComparison.OrdinalIgnoreCase);
        _treeMarks.Clear();
        var items = new List<TreeViewItem>();
        foreach (var ctx in DraftModel.Contexts)
        {
            var computed = DraftModel.PropertiesOf(ctx.Name).Where(p => p.Kind == PropertyKind.Computed && Match(p.Id, p.NameRu)).OrderBy(p => p.Name).ToList();
            var inputs = ctx.Properties.Values.Where(p => !p.IsRelation && Match(p.Id, p.NameRu)).OrderBy(p => p.Name).ToList();
            if (computed.Count == 0 && inputs.Count == 0) continue;
            var node = new TreeViewItem { Header = Caption(ctx.NameRu.Length > 0 ? $"{ctx.NameRu} ({ctx.Name})" : ctx.Name, Look.Text, true), IsExpanded = filter.Length > 0 || ctx.Name is "hole" or "blast" };
            var list = new List<TreeViewItem>();
            foreach (var p in computed) list.Add(PropertyItem(p, CalcService.BuiltIn.Properties.Any(b => b.Id == p.Id) ? "ƒ" : "+"));
            if (inputs.Count > 0)
            {
                var fields = new TreeViewItem { Header = Caption(Loc.T("Model fields"), Look.Muted), IsExpanded = filter.Length > 0 };
                fields.ItemsSource = inputs.Select(p => PropertyItem(p, "←")).ToList();
                list.Add(fields);
            }
            node.ItemsSource = list;
            items.Add(node);
        }
        var parameters = DraftModel.Parameters.Where(p => Match(p.Def.Id, p.Def.NameRu)).ToList();
        if (parameters.Count > 0)
            items.Add(new TreeViewItem
            {
                Header = Caption(Loc.T("Parameters"), Look.Text, true), IsExpanded = filter.Length > 0,
                ItemsSource = parameters.Select(p => Item("param:" + p.Def.Id, "#", p.Def.Id, p.Def.NameRu)).ToList()
            });
        var builtins = DraftModel.BuiltinNames.Select(n => CalcService.Registry.Builtins[n]).Where(b => Match(b.Name, b.NameRu)).ToList();
        if (builtins.Count > 0)
            items.Add(new TreeViewItem
            {
                Header = Caption(Loc.T("Built-in algorithms"), Look.Text, true), IsExpanded = filter.Length > 0,
                ItemsSource = builtins.Select(b => Item("builtin:" + b.Name, "⚙", b.Name + "()", b.NameRu)).ToList()
            });
        items.Add(new TreeViewItem { Header = Caption(Loc.F("Rule sets: {0}", Flat().Rules.Count), Look.Muted), Tag = "rules" });
        _tree.ItemsSource = items;
        BuildTreeMarks();
    }

    private TreeViewItem PropertyItem(PropertyDef p, string sign) => Item(p.Id, sign, p.Name, p.NameRu);

    private TreeViewItem Item(string id, string sign, string name, string ru)
    {
        var mark = new TextBlock { Text = "•", Foreground = Look.Accent, FontWeight = FontWeight.Bold, IsVisible = false, Margin = new Thickness(4, 0, 0, 0) };
        _treeMarks[id] = mark;
        var row = Ui.Row(6,
            new TextBlock { Text = sign, Width = 14, Foreground = sign == "←" ? Look.Faint : Look.Blue, FontFamily = Look.Mono },
            new TextBlock { Text = name, Foreground = Look.TextSoft, FontFamily = Look.Mono, FontSize = 12 },
            mark);
        if (ru.Length > 0) ToolTip.SetTip(row, ru + (id.Contains('.') && !id.Contains(':') ? "\n" + Loc.T("Double-click: insert into the formula") : ""));
        var item = new TreeViewItem { Header = row, Tag = id };
        // двойной щелчок по свойству вставляет его в формулу, где стоит курсор
        if (!id.Contains(':')) item.DoubleTapped += (_, e) => { InsertProperty(id); e.Handled = true; };
        return item;
    }

    private static TextBlock Caption(string text, IBrush brush, bool strong = false) =>
        new() { Text = text, Foreground = brush, FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal, FontSize = 12.5 };

    /// <summary>Marks what differs from the base profile «SHOTPlus 6».</summary>
    private void BuildTreeMarks()
    {
        var flat = Flat();
        foreach (var (id, mark) in _treeMarks)
        {
            if (id.StartsWith("param:", StringComparison.Ordinal))
            {
                var mine = flat.Parameters.FirstOrDefault(p => "param:" + p.Id == id);
                var theirs = CalcService.BuiltIn.Parameters.FirstOrDefault(p => "param:" + p.Id == id);
                mark.IsVisible = mine != null && (theirs == null || mine.Value != theirs.Value || mine.Table?.Count != theirs.Table?.Count);
                continue;
            }
            mark.IsVisible = IsModified(id) || (_project.Calc?.Parameters?.ContainsKey(id) ?? false);
        }
    }

    private bool IsModified(string propertyId)
    {
        string Key(IEnumerable<FormulaDef> fs) => string.Join("|", fs.Where(f => f.Property == propertyId).OrderBy(f => f.Priority).Select(f => $"{f.Condition}?{f.Expression}!{f.OnError}"));
        return Key(Flat().Formulas) != Key(CalcService.BuiltIn.Formulas);
    }

    // ---- центр ----------------------------------------------------------

    public void Select(string id)
    {
        if (id.Length == 0 || id == "rules") return;
        Selected = id;
        _testsOpen = false;
        ShowCenter();
        BuildRight();
    }

    private void ShowCenter()
    {
        FormKit.SetTab(_formulaTab, !_testsOpen);
        FormKit.SetTab(_testsTab, _testsOpen);
        _center.Children.Clear();
        _afterCompile = null;
        if (_testsOpen) { ShowTests(); return; }
        if (Selected == null) return;
        if (Selected.StartsWith("param:", StringComparison.Ordinal)) ShowParameter(Selected[6..]);
        else if (Selected.StartsWith("builtin:", StringComparison.Ordinal)) ShowBuiltin(Selected[8..]);
        else if (DraftModel.FindProperty(Selected) is { Kind: PropertyKind.Computed } p) ShowFormula(p);
        else if (DraftModel.FindProperty(Selected) is { } input) ShowInput(input);
    }

    private void ShowFormula(PropertyDef p)
    {
        var def = Draft.Properties.FirstOrDefault(x => x.Id == p.Id) ?? p.Clone();
        TextBox Bound(string? value, Action<string> set)
        {
            var t = FormKit.Text(value);
            t.TextChanged += (_, _) => { if (t.Text != null) { set(t.Text); } };
            return t;
        }
        void Own(Action<PropertyDef> change)
        {
            var d = Draft.Properties.FirstOrDefault(x => x.Id == p.Id);
            if (d == null) Draft.Properties.Add(d = def);
            change(d);
            MarkDirty();
        }
        _center.Children.Add(new TextBlock { Text = p.Id, FontFamily = Look.Mono, FontSize = 16, Foreground = Look.Text });
        _center.Children.Add(FormKit.Section(Loc.T("Property"), FormKit.Fields(2,
            FormKit.Labeled(Loc.T("Name"), Bound(def.NameRu, v => { if (v != def.NameRu) Own(d => d.NameRu = v); })),
            FormKit.Labeled(Loc.T("Group"), Bound(def.Group, v => { if (v != def.Group) Own(d => d.Group = v); })),
            FormKit.Labeled(Loc.T("Unit"), Bound(def.Unit, v => { if (v != def.Unit) Own(d => d.Unit = v); })),
            FormKit.Labeled(Loc.T("Format"), Bound(def.Format, v => { if (v != def.Format) Own(d => d.Format = v); })))));
        if (p.Type == CalcType.Number)
        {
            static double? Parse(string v) => double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            var min = Bound(def.Min?.ToString(CultureInfo.CurrentCulture), v => { if (Parse(v) != def.Min) Own(d => { d.Min = Parse(v); def.Min = d.Min; }); });
            var max = Bound(def.Max?.ToString(CultureInfo.CurrentCulture), v => { if (Parse(v) != def.Max) Own(d => { d.Max = Parse(v); def.Max = d.Max; }); });
            min.Name = "rangeMin";
            max.Name = "rangeMax";
            _center.Children.Add(FormKit.Section(Loc.T("Reasonable range"), FormKit.Fields(2, FormKit.Labeled(Loc.T("from"), min), FormKit.Labeled(Loc.T("to"), max))));
        }

        if (Shown(p) is { } shown) _center.Children.Add(FormKit.Section(Loc.T("Show"), shown));

        var formulas = Flat().Formulas.Where(f => f.Property == p.Id).OrderBy(f => f.Priority).ToList();
        if (formulas.Count == 0) formulas.Add(new FormulaDef { Property = p.Id, Expression = "0" });
        _formulas = formulas;
        var variants = new StackPanel { Spacing = 12 };
        for (int i = 0; i < formulas.Count; i++) variants.Children.Add(Variant(p, formulas, i));
        var add = FormKit.Tool("plus", Loc.T("Variant"), () =>
        {
            formulas.Insert(0, new FormulaDef { Property = p.Id, Expression = formulas[0].Expression, Condition = "true", Priority = formulas.Min(f => f.Priority) - 1 });
            SetFormulas(p.Id, formulas);
            ShowCenter();
        }, Loc.T("A formula used when its condition holds; the first by priority wins"));
        var revert = FormKit.Secondary(Loc.T("Revert to the base profile"), () =>
        {
            var baseFormulas = CalcService.BuiltIn.Formulas.Where(f => f.Property == p.Id).Select(f => f.Clone()).ToList();
            SetFormulas(p.Id, baseFormulas.Count > 0 ? baseFormulas : formulas);
            Draft.Properties.RemoveAll(x => x.Id == p.Id && CalcService.BuiltIn.Properties.Any(b => b.Id == p.Id));
            Recompile();
            ShowCenter();
        });
        revert.IsEnabled = IsModified(p.Id);
        _afterCompile = () => revert.IsEnabled = IsModified(p.Id);
        _center.Children.Add(FormKit.Section(Loc.T("Formula"), variants, Ui.Row(8, add, revert)));

        var preview = new StackPanel { Spacing = 4 };
        _center.Children.Add(FormKit.Section(Loc.T("Preview: before / after / Δ"), preview));
        _previewPanel = preview;
        _previewProperty = p.Id;
        UpdatePreview();
    }

    /// <summary>
    /// Where the property is shown: a column of the hole table and the CSV export, a label on the plan, the colour of the
    /// collars (hole properties); a line of the summary panel and of the summary block (blast totals); its report field.
    /// Applied at once, as view settings of the project.
    /// </summary>
    private Control? Shown(PropertyDef p)
    {
        var view = _project.View;
        CheckBox Box(string text, string name, bool on, Action<bool> set)
        {
            var b = new CheckBox { Content = text, Name = name, IsChecked = on };
            b.IsCheckedChanged += (_, _) => { var v = b.IsChecked == true; _vm.ToggleView(_ => set(v)); };
            return b;
        }
        static List<string>? Toggle(List<string>? list, string id, bool on)
        {
            var l = (list ?? new()).Where(x => x != id).ToList();
            if (on) l.Add(id);
            return l.Count > 0 ? l : null;
        }
        var panel = new StackPanel { Spacing = 6 };
        if (p.Context == "hole")
        {
            panel.Children.Add(Box(Loc.T("Column of the hole table and of the CSV export"), "showColumn", view.CalcColumns?.Contains(p.Id) == true, on => view.CalcColumns = Toggle(view.CalcColumns, p.Id, on)));
            panel.Children.Add(Box(Loc.T("Label of the holes on the plan"), "showLabel", view.CalcLabels?.Contains(p.Id) == true, on => view.CalcLabels = Toggle(view.CalcLabels, p.Id, on)));
            if (p.Type == CalcType.Number)
                panel.Children.Add(Box(Loc.T("Colour the collars by it"), "showColour", view.ColorBy == p.Id, on => view.ColorBy = on ? p.Id : view.ColorBy == p.Id ? null : view.ColorBy));
        }
        else if (p.Context == "blast")
        {
            var summary = _project.Summary;
            panel.Children.Add(Box(Loc.T("Line of the summary panel and of the summary block on the plan"), "showTotal", summary.CalcTotals?.Contains(p.Id) == true, on => summary.CalcTotals = Toggle(summary.CalcTotals, p.Id, on)));
            panel.Children.Add(new TextBlock { Text = Loc.F("Report field: {0}", "{calc:" + p.Id + "}"), FontSize = 11.5, Foreground = Look.Muted });
        }
        else return null;
        if (_published.NodeOf(p.Id) == null)
            panel.Children.Add(new TextBlock { Text = Loc.T("The values appear once the profile is published."), FontSize = 11.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private StackPanel? _previewPanel;
    private string? _previewProperty;

    private Control Variant(PropertyDef p, List<FormulaDef> formulas, int index)
    {
        var f = formulas[index];
        var text = new TextBox
        {
            Text = Shown(p.Context, f.Expression), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76, FontFamily = Look.Mono, FontSize = 13,
            Background = Look.Field, BorderBrush = Look.LineStrong, Padding = new Thickness(8, 6), Name = index == 0 ? "formula" : "formula" + index
        };
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var highlight = new SelectableTextBlock { FontFamily = Look.Mono, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Name = index == 0 ? "highlight" : null };
        var hints = new WrapPanel { Name = index == 0 ? "hints" : null };
        var priority = FormKit.Text(f.Priority.ToString(CultureInfo.InvariantCulture));
        var onError = FormKit.Text(f.OnError?.ToString(CultureInfo.CurrentCulture));
        onError.Watermark = Loc.T("error");
        void Check()
        {
            try
            {
                var c = DraftModel.CompileExpression(p.Context, text.Text ?? "", p.Type);
                var want = Calc.Unit.TryParse(p.Unit, out var u) ? u : Calc.Unit.Unknown;
                var unitOk = p.Type != CalcType.Number || want.SameDimension(c.Unit);
                status.Text = unitOk ? Loc.F("OK · {0} · depends on {1}", c.Unit.IsUnknown ? "?" : Aliases.RussianUnit(c.Unit.Symbol), c.Dependencies.Count)
                    : Loc.F("The formula gives {0}, the property is in {1}", Aliases.RussianUnit(c.Unit.Symbol), Aliases.RussianUnit(want.Symbol));
                status.Foreground = unitOk ? Ok : Warn;
            }
            catch (CalcSyntaxException e)
            {
                status.Text = Loc.F("Error at {0}: {1}", e.Position + 1, e.Message);
                status.Foreground = Bad;
            }
            if (index == 0) FormulaStatus = status.Text ?? "";
            Paint(highlight, text.Text ?? "");
            FillHints(hints, text, p.Context);
        }
        if (index == 0) _checkMain = Check;
        text.GotFocus += (_, _) => { _activeFormula = text; _activeContext = p.Context; };
        if (index == 0) { _activeFormula = text; _activeContext = p.Context; }
        text.TextChanged += (_, _) =>
        {
            // в черновике формула хранится кодовыми именами, в поле — как удобно читать
            var code = FormulaText.ToCode(DraftModel, p.Context, text.Text ?? "");
            if (code == f.Expression) { Check(); return; }
            f.Expression = code;
            SetFormulas(p.Id, formulas);
            Check();
        };
        priority.TextChanged += (_, _) => { if (int.TryParse(priority.Text, out var v) && v != f.Priority) { f.Priority = v; SetFormulas(p.Id, formulas); } };
        onError.TextChanged += (_, _) =>
        {
            double? v = double.TryParse(onError.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var d) ? d : null;
            if (v != f.OnError) { f.OnError = v; SetFormulas(p.Id, formulas); }
        };
        text.PropertyChanged += (_, e) => { if (e.Property == TextBox.CaretIndexProperty) FillHints(hints, text, p.Context); };
        Check();
        var condition = ConditionEditor(p, f, () => SetFormulas(p.Id, formulas), index);
        var fields = FormKit.Fields(2, FormKit.Labeled(Loc.T("Priority"), priority), FormKit.Labeled(Loc.T("Value on error"), onError));
        var body = new StackPanel { Spacing = 8 };
        if (formulas.Count > 1 || f.Condition != null)
        {
            var remove = FormKit.Tool("trash", null, () => { formulas.RemoveAt(index); SetFormulas(p.Id, formulas); ShowCenter(); }, Loc.T("Delete the variant"));
            remove.IsEnabled = formulas.Count > 1;
            body.Children.Add(new DockPanel { Children = { Docked(remove, Avalonia.Controls.Dock.Right), FormKit.Labeled(Loc.T("Condition"), condition) } });
            body.Children.Add(fields);
        }
        else body.Children.Add(fields);
        body.Children.Add(text);
        body.Children.Add(highlight);
        body.Children.Add(status);
        body.Children.Add(hints);
        if (f.Comment.Length > 0 || f.Source.Length > 0)
            body.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { f.Comment, f.Source.Length > 0 ? Loc.T("Source") + ": " + f.Source : "" }.Where(s => s.Length > 0)), FontSize = 11.5, Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap });
        return new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Child = body };
    }

    /// <summary>Формула для поля: по-русски или кодовыми именами (переключатель «Показать кодовые имена»).</summary>
    private string Shown(string context, string code) => _codeNames ? code : FormulaText.ToRussian(DraftModel, context, code);

    private static readonly IBrush PropertyInk = new SolidColorBrush(Color.FromUInt32(0xFF7DB7FF));
    private static readonly IBrush PropertyBack = new SolidColorBrush(Color.FromUInt32(0x264C9BFF));
    private static readonly IBrush NumberInk = new SolidColorBrush(Color.FromUInt32(0xFFB5E08D));

    /// <summary>Подсветка формулы под полем: свойства метками, функции, числа и слова цветом, ошибка красным.</summary>
    private static void Paint(SelectableTextBlock block, string text)
    {
        block.Inlines ??= new Avalonia.Controls.Documents.InlineCollection();
        block.Inlines.Clear();
        foreach (var (part, kind) in FormulaText.Highlight(text))
        {
            var run = new Avalonia.Controls.Documents.Run(part);
            switch (kind)
            {
                case FormulaText.Part.Property: run.Foreground = PropertyInk; run.Background = PropertyBack; break;
                case FormulaText.Part.Function: run.Foreground = Look.Accent; break;
                case FormulaText.Part.Number: run.Foreground = NumberInk; break;
                case FormulaText.Part.String: run.Foreground = NumberInk; break;
                case FormulaText.Part.Keyword: run.Foreground = Look.Blue; run.FontWeight = FontWeight.SemiBold; break;
                case FormulaText.Part.Error: run.Foreground = Bad; run.TextDecorations = TextDecorations.Underline; break;
                default: run.Foreground = Look.TextSoft; break;
            }
            block.Inlines.Add(run);
        }
    }

    /// <summary>
    /// Подсказки по слову у курсора: свойства объекта (с учётом связей: «[Продукт].[Пл» → свойства продукта) и функции;
    /// щелчок заменяет слово выбранным именем.
    /// </summary>
    private void FillHints(WrapPanel panel, TextBox box, string context)
    {
        panel.Children.Clear();
        var text = box.Text ?? "";
        var caret = Math.Clamp(box.CaretIndex, 0, text.Length);
        int start = caret;
        bool bracket = false;
        // слово у курсора: незакрытое [имя или буквы
        for (int i = caret - 1; i >= 0; i--)
        {
            if (text[i] == ']') break;
            if (text[i] == '[') { start = i; bracket = true; break; }
        }
        if (!bracket)
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        var word = text[start..caret].TrimStart('[');
        // контекст по связи перед словом: [Продукт]. или product.
        var ctx = context;
        var before = text[..start];
        if (before.EndsWith('.'))
        {
            var chain = new List<string>();
            var j = before.Length - 1;
            while (j > 0 && before[j] == '.')
            {
                var k = j - 1;
                string seg;
                if (before[k] == ']') { var open = before.LastIndexOf('[', k); if (open < 0) break; seg = before[(open + 1)..k]; j = open - 1; }
                else { var s = k; while (s >= 0 && (char.IsLetterOrDigit(before[s]) || before[s] == '_')) s--; seg = before[(s + 1)..(k + 1)]; j = s; }
                chain.Insert(0, seg);
                if (j < 0 || before[j] != '.') break;
            }
            foreach (var seg in chain)
                if (DraftModel.Resolve(ctx, seg) is { Target: { } t }) ctx = t;
                else return;
        }
        if (word.Length == 0 && !bracket) return;
        var names = new List<(string Label, string Insert)>();
        foreach (var prop in DraftModel.PropertiesOf(ctx))
        {
            var alias = _codeNames ? prop.Name : DraftModel.AliasOf(prop);
            if (alias.StartsWith(word, StringComparison.OrdinalIgnoreCase) || prop.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                names.Add((alias, _codeNames ? prop.Name : "[" + alias + "]"));
        }
        if (!bracket && start == caret - word.Length && !before.EndsWith('.'))
            foreach (var fn in Aliases.Functions)
            {
                var name = _codeNames ? fn.Key : fn.Value;
                if (name.StartsWith(word, StringComparison.OrdinalIgnoreCase)) names.Add((name + "(…)", name + "("));
            }
        foreach (var (label, insert) in names.Where(n => !string.Equals(n.Insert.Trim('[', ']', '('), word, StringComparison.OrdinalIgnoreCase)).DistinctBy(n => n.Insert).OrderBy(n => n.Label.Length).Take(10))
        {
            var b = new Button { Content = label, FontSize = 11.5, Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 4, 4), Background = Look.Hover, CornerRadius = new CornerRadius(4) };
            b.Click += (_, _) =>
            {
                box.Text = text[..start] + insert + text[caret..];
                box.CaretIndex = start + insert.Length;
                box.Focus();
            };
            panel.Children.Add(b);
        }
    }

    /// <summary>Вставляет имя свойства в формулу, где стоит курсор (двойной щелчок по дереву); свойство другого объекта — путём по связи.</summary>
    public void InsertProperty(string propertyId)
    {
        if (_activeFormula == null || _activeContext == null || DraftModel.FindProperty(propertyId) is not { } prop) return;
        string Name(PropertyDef d) => _codeNames ? d.Name : "[" + DraftModel.AliasOf(d) + "]";
        string? text = prop.Context == _activeContext ? Name(prop) : null;
        if (text == null)
            foreach (var rel in DraftModel.PropertiesOf(_activeContext).Where(r => r.Type == CalcType.Ref && r.Target == prop.Context))
            {
                text = Name(rel) + "." + Name(prop);
                break;
            }
        if (text == null)
        {
            var list = DraftModel.PropertiesOf(_activeContext).FirstOrDefault(r => r.Type == CalcType.List && r.Target == prop.Context);
            if (list == null) return;
            var sum = _codeNames ? "sum" : "сумма";
            text = $"{sum}({Name(list)}, {Name(prop)})";
        }
        var box = _activeFormula;
        var current = box.Text ?? "";
        var caret = Math.Clamp(box.CaretIndex, 0, current.Length);
        box.Text = current[..caret] + text + current[caret..];
        box.CaretIndex = caret + text.Length;
    }

    private TextBox? _activeFormula;
    private string? _activeContext;
    private static bool _codeNames;

    /// <summary>Показ формул кодовыми именами (для тех, кто привык к ним), по умолчанию — по-русски.</summary>
    public bool CodeNames
    {
        get => _codeNames;
        set { _codeNames = value; ShowCenter(); }
    }

    private static readonly string[] Operators = { "=", "≠", "<", "≤", ">", "≥" };
    private static string CodeOp(string op) => op switch { "≠" => "<>", "≤" => "<=", "≥" => ">=", _ => op };
    private static string ShowOp(string op) => op switch { "<>" => "≠", "<=" => "≤", ">=" => "≥", _ => op };

    /// <summary>
    /// Условие варианта: конструктор «свойство · оператор · значение» с соединением И/ИЛИ или текст. Условие, которое
    /// конструктор не может показать (скобки, функции), открывается текстом.
    /// </summary>
    private Control ConditionEditor(PropertyDef p, FormulaDef f, Action changed, int index)
    {
        var host = new StackPanel { Spacing = 6 };
        var rows = TryRows(p.Context, f.Condition, out var join);
        bool textMode = rows == null;
        void Build()
        {
            host.Children.Clear();
            var toggle = new Button { Content = textMode ? Loc.T("Builder") : Loc.T("Text"), FontSize = 11.5, Padding = new Thickness(8, 2), Name = index == 0 ? "conditionMode" : null };
            toggle.Click += (_, _) =>
            {
                if (textMode)
                {
                    var r = TryRows(p.Context, f.Condition, out var j);
                    if (r == null) { _ = Dialogs.Info(Loc.T("This condition is too complex for the builder; edit it as text.")); return; }
                    rows = r; join = j;
                }
                textMode = !textMode;
                Build();
            };
            if (textMode)
            {
                var box = FormKit.Text(f.Condition == null ? "" : Shown(p.Context, f.Condition));
                box.Watermark = Loc.T("always");
                box.TextChanged += (_, _) =>
                {
                    var v = string.IsNullOrWhiteSpace(box.Text) ? null : FormulaText.ToCode(DraftModel, p.Context, box.Text!);
                    if (v != f.Condition) { f.Condition = v; changed(); }
                };
                host.Children.Add(new DockPanel { Children = { Docked(toggle, Avalonia.Controls.Dock.Right), box } });
                return;
            }
            var props = DraftModel.PropertiesOf(p.Context).Where(x => !x.IsRelation && x.Id != p.Id).OrderBy(x => DraftModel.AliasOf(x)).ToList();
            var joinBox = new ComboBox { ItemsSource = new[] { Loc.T("all hold (AND)"), Loc.T("any holds (OR)") }, SelectedIndex = join == "or" ? 1 : 0 };
            joinBox.SelectionChanged += (_, _) => { join = joinBox.SelectedIndex == 1 ? "or" : "and"; Write(); };
            void Write()
            {
                var parts = rows!.Where(r => r.Property != null && r.Value.Trim().Length > 0).Select(r =>
                {
                    var d = DraftModel.FindProperty(r.Property!)!;
                    var v = r.Value.Trim();
                    var number = double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var num);
                    var literal = number ? num.ToString("R", CultureInfo.InvariantCulture)
                        : v is "истина" or "true" ? "true" : v is "ложь" or "false" ? "false" : "'" + v.Trim('\'').Replace("'", "''") + "'";
                    return $"{d.Name} {CodeOp(r.Op)} {literal}";
                }).ToList();
                var cond = parts.Count == 0 ? null : string.Join($" {join} ", parts);
                if (cond != f.Condition) { f.Condition = cond; changed(); }
            }
            var grid = new StackPanel { Spacing = 4 };
            foreach (var row in rows!)
            {
                var r = row;
                var propBox = new ComboBox { ItemsSource = props.Select(x => DraftModel.AliasOf(x)).ToList(), SelectedIndex = props.FindIndex(x => x.Id == r.Property), MinWidth = 200 };
                var opBox = new ComboBox { ItemsSource = Operators, SelectedIndex = Math.Max(0, Array.IndexOf(Operators, ShowOp(r.Op))), Width = 64 };
                var value = FormKit.Text(r.Value);
                value.Width = 120;
                propBox.SelectionChanged += (_, _) => { if (propBox.SelectedIndex >= 0) { r.Property = props[propBox.SelectedIndex].Id; Write(); } };
                opBox.SelectionChanged += (_, _) => { r.Op = CodeOp(Operators[Math.Max(0, opBox.SelectedIndex)]); Write(); };
                value.TextChanged += (_, _) => { r.Value = value.Text ?? ""; Write(); };
                var del = FormKit.Tool("minus", null, () => { rows!.Remove(r); Write(); Build(); }, Loc.T("Delete the condition"));
                grid.Children.Add(Ui.Row(6, propBox, opBox, value, del));
            }
            var add = FormKit.Tool("plus", Loc.T("Condition"), () => { rows!.Add(new ConditionRow { Property = props.FirstOrDefault()?.Id, Op = ">", Value = "" }); Build(); }, Loc.T("Add a condition"));
            host.Children.Add(new DockPanel { Children = { Docked(toggle, Avalonia.Controls.Dock.Right), Ui.Row(8, joinBox, add) } });
            host.Children.Add(grid);
            if (rows!.Count == 0) host.Children.Add(new TextBlock { Text = Loc.T("No conditions: the variant always applies."), FontSize = 11.5, Foreground = Look.Faint });
        }
        Build();
        return host;
    }

    private sealed class ConditionRow
    {
        public string? Property { get; set; }
        public string Op { get; set; } = "=";
        public string Value { get; set; } = "";
    }

    /// <summary>Строки конструктора из условия: сравнения свойства с числом или строкой, соединённые одним словом И или ИЛИ; иначе null.</summary>
    private List<ConditionRow>? TryRows(string context, string? condition, out string join)
    {
        join = "and";
        if (string.IsNullOrWhiteSpace(condition)) return new List<ConditionRow>();
        Ast ast;
        try { ast = Parser.Parse(condition); } catch (CalcSyntaxException) { return null; }
        if (ast is NameAst { Path.Count: 1 } always && Aliases.Word(always.Path[0]) == "true") return new List<ConditionRow>();
        var leaves = new List<Ast>();
        string? op = null;
        void Flatten(Ast a)
        {
            if (a is BinaryAst { Op: "and" or "or" } b && (op == null || op == b.Op)) { op = b.Op; Flatten(b.Left); Flatten(b.Right); }
            else leaves.Add(a);
        }
        Flatten(ast);
        join = op ?? "and";
        var rows = new List<ConditionRow>();
        foreach (var leaf in leaves)
        {
            if (leaf is not BinaryAst { Op: "=" or "<>" or "<" or "<=" or ">" or ">=" } cmp || cmp.Left is not NameAst { Path.Count: 1 } name) return null;
            var prop = DraftModel.Resolve(context, name.Path[0]);
            if (prop == null || prop.IsRelation) return null;
            var value = cmp.Right switch
            {
                NumberAst n => n.Value.ToString(CultureInfo.CurrentCulture),
                StringAst s => s.Value,
                NameAst { Path.Count: 1 } w when Aliases.Word(w.Path[0]) is "true" or "false" => Aliases.Word(w.Path[0]) == "true" ? "истина" : "ложь",
                _ => null
            };
            if (value == null) return null;
            rows.Add(new ConditionRow { Property = prop.Id, Op = cmp.Op, Value = value });
        }
        return rows;
    }

    /// <summary>Puts the formulas of a property in the draft (all its variants replace the base ones).</summary>
    private void SetFormulas(string property, List<FormulaDef> formulas)
    {
        Draft.Formulas.RemoveAll(f => f.Property == property);
        Draft.Formulas.AddRange(formulas.Select(f => { var c = f.Clone(); c.Property = property; return c; }));
        Draft.Removed.Remove(property);
        MarkDirty();
    }

    /// <summary>Sets the main formula of the selected property and checks it at once (for the smoke test and scripts).</summary>
    public void SetFormula(string text)
    {
        if (_formulas == null || Selected == null || DraftModel.FindProperty(Selected) is not { } p) return;
        // TextChanged приходит позже (через диспетчер): черновик меняется сразу, поле только показывает текст;
        // формула принимается и по-русски, и кодовыми именами, хранится кодовыми
        _formulas[0].Expression = FormulaText.ToCode(DraftModel, p.Context, text);
        SetFormulas(Selected, _formulas);
        var box = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(_center).OfType<TextBox>().FirstOrDefault(t => t.Name == "formula");
        if (box != null) box.Text = text;
        _checkMain?.Invoke();
        Recompile();
    }

    private List<FormulaDef>? _formulas;
    private Action? _checkMain;

    private void ShowInput(PropertyDef p)
    {
        _center.Children.Add(new TextBlock { Text = p.Id, FontFamily = Look.Mono, FontSize = 16, Foreground = Look.Text });
        _center.Children.Add(new TextBlock { Text = Loc.T("A field of the model: it is entered, not calculated. Formulas read it by name."), Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap });
        _center.Children.Add(FormKit.Section(Loc.T("Property"), FormKit.Fields(2,
            Info(Loc.T("Name"), p.NameRu.Length > 0 ? p.NameRu : p.NameEn), Info(Loc.T("Type"), p.Type.ToString()),
            Info(Loc.T("Unit"), p.Unit), Info(Loc.T("Model field"), string.Join(", ", p.Fields)))));
    }

    private void ShowBuiltin(string name)
    {
        var b = CalcService.Registry.Builtins[name];
        _center.Children.Add(new TextBlock { Text = b.Name + "()", FontFamily = Look.Mono, FontSize = 16, Foreground = Look.Text });
        _center.Children.Add(new TextBlock { Text = b.NameRu + (b.Description.Length > 0 ? ". " + b.Description : ""), Foreground = Look.TextSoft, TextWrapping = TextWrapping.Wrap });
        _center.Children.Add(FormKit.Section(Loc.T("Built-in algorithm"), FormKit.Fields(2,
            Info(Loc.T("Scope"), b.Scope == BuiltinScope.Blast ? Loc.T("once for the blast") : Loc.T("per entity")),
            Info(Loc.T("Available in"), b.Contexts.Length > 0 ? string.Join(", ", b.Contexts) : Loc.T("everywhere")),
            Info(Loc.T("Inputs"), string.Join(", ", b.Inputs)),
            Info(Loc.T("Outputs"), b.Outputs.Length > 0 ? string.Join(", ", b.Outputs) : "—"),
            Info(Loc.T("Settings"), b.Settings.Length > 0 ? string.Join(", ", b.Settings) : "—"),
            Info(Loc.T("Unit"), b.Unit))));
    }

    private void ShowParameter(string id)
    {
        var p = Flat().Parameters.First(x => x.Id == id);
        ParameterDef Own()
        {
            var d = Draft.Parameters.FirstOrDefault(x => x.Id == id);
            if (d == null) Draft.Parameters.Add(d = p.Clone());
            return d;
        }
        _center.Children.Add(new TextBlock { Text = id, FontFamily = Look.Mono, FontSize = 16, Foreground = Look.Text });
        if (p.NameRu.Length > 0) _center.Children.Add(new TextBlock { Text = p.NameRu, Foreground = Look.TextSoft });
        var value = FormKit.Text(p.Value.ToString(CultureInfo.CurrentCulture));
        value.TextChanged += (_, _) => { if (double.TryParse(value.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) && v != p.Value) { Own().Value = v; MarkDirty(); } };
        var blastValue = _project.Calc?.Parameters?.GetValueOrDefault(id);
        var blast = FormKit.Text(blastValue?.ToString(CultureInfo.CurrentCulture));
        blast.Watermark = p.Bind != null ? Loc.F("from the model: {0}", p.Bind) : Loc.T("not set");
        blast.TextChanged += (_, _) => { _pendingBlast[id] = double.TryParse(blast.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? v : null; MarkDirty(); };
        var table = new TextBox
        {
            Text = p.Table == null ? "" : string.Join("\n", p.Table.Select(t => $"{t.X.ToString(CultureInfo.InvariantCulture)}; {t.Y.ToString(CultureInfo.InvariantCulture)}")),
            AcceptsReturn = true, MinHeight = 90, FontFamily = Look.Mono, Background = Look.Field, BorderBrush = Look.LineStrong, Watermark = Loc.T("x; y on each line")
        };
        table.TextChanged += (_, _) =>
        {
            var pts = (table.Text ?? "").Split('\n').Select(l => l.Split(';')).Where(a => a.Length == 2
                && double.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _) && double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                .Select(a => new TablePoint(double.Parse(a[0], CultureInfo.InvariantCulture), double.Parse(a[1], CultureInfo.InvariantCulture))).OrderBy(t => t.X).ToList();
            Own().Table = pts.Count > 0 ? pts : null;
            MarkDirty();
        };
        var effective = _publishedSession.ParameterValue(id);
        _center.Children.Add(FormKit.Section(Loc.T("Values by level"), FormKit.Fields(2,
            FormKit.Labeled(Loc.T("Profile"), value),
            FormKit.Labeled(Loc.T("This blast"), blast),
            Info(Loc.T("Unit"), p.Unit),
            Info(Loc.T("Now used"), effective?.ToString("0.###", CultureInfo.CurrentCulture) ?? "—"))));
        _center.Children.Add(new TextBlock { Text = Loc.T("Looked up from the bottom: hole → hole type → blast → mine → profile."), FontSize = 11.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap });
        _center.Children.Add(FormKit.Section(Loc.T("Table"), table));
    }

    private readonly Dictionary<string, double?> _pendingBlast = new();

    private void ShowTests()
    {
        var flat = Flat();
        var session = DraftSession();
        var rows = new StackPanel { Spacing = 6 };
        foreach (var t in flat.Tests)
        {
            var v = Evaluate(session, "blast", t.Expression, _project);
            var pass = !v.IsError && Math.Abs(v.Number - t.Expected) <= t.Tolerance;
            rows.Children.Add(new DockPanel
            {
                Children =
                {
                    Docked(new TextBlock { Text = pass ? "✓" : "✗", Foreground = pass ? Ok : Bad, Width = 20 }, Avalonia.Controls.Dock.Left),
                    Docked(new TextBlock { Text = v.IsError ? v.Error : v.Number.ToString("0.####", CultureInfo.CurrentCulture), Foreground = Look.Muted, Width = 160, TextTrimming = TextTrimming.CharacterEllipsis }, Avalonia.Controls.Dock.Right),
                    new TextBlock { Text = $"{t.Name}: {t.Expression} = {t.Expected.ToString(CultureInfo.CurrentCulture)}", Foreground = Look.TextSoft, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            });
        }
        if (flat.Tests.Count == 0) rows.Children.Add(new TextBlock { Text = Loc.T("No test cases"), Foreground = Look.Faint });
        var expr = FormKit.Text("powderFactor");
        var add = FormKit.Tool("plus", Loc.T("Add with the current value"), () =>
        {
            var v = Evaluate(DraftSession(), "blast", expr.Text ?? "", _project);
            if (v.IsError) return;
            Draft.Tests.Add(new TestCaseDef { Name = _project.Header.Name.Length > 0 ? _project.Header.Name : Loc.T("Blast"), Expression = expr.Text!, Expected = v.Number, Tolerance = Math.Max(1e-9, Math.Abs(v.Number) * 1e-9) });
            MarkDirty();
            ShowCenter();
        });
        _center.Children.Add(FormKit.Section(Loc.T("Test cases"), rows));
        _center.Children.Add(FormKit.Section(Loc.T("New test case on this blast"), new DockPanel { Children = { Docked(add, Avalonia.Controls.Dock.Right), expr } }));
    }

    private static CalcValue Evaluate(CalcSession s, string context, string text, object entity)
    {
        try { return s.Evaluate(s.Model.CompileExpression(context, text), entity); }
        catch (CalcSyntaxException e) { return CalcValue.Fail(e.Message); }
    }

    // ---- предпросмотр ---------------------------------------------------

    private IReadOnlyList<object> PreviewEntities(string context)
    {
        var selected = _vm.Doc?.SelectedHoles.Where(h => !h.IsDummy).ToList() ?? new List<Hole>();
        var holes = selected.Count > 0 ? selected : _project.Holes.Where(h => !h.IsDummy).Take(8).ToList();
        return context switch
        {
            "hole" => holes,
            "deck" => holes.SelectMany(h => h.Decks).Take(12).Cast<object>().ToList(),
            "primer" => holes.SelectMany(h => h.Decks.SelectMany(d => d.Primers)).Take(12).Cast<object>().ToList(),
            _ => CalcService.Registry.Context(context)?.All(_publishedSession.Scope).Take(8).ToList() ?? new List<object>()
        };
    }

    private string EntityName(object o) => o switch
    {
        Hole h => Loc.T("Hole") + " " + h.Label,
        Deck d => (_publishedSession.Scope.HoleOf(d)?.Label ?? "?") + " · " + (_publishedSession.Scope.FindProduct(d.ProductId)?.Name ?? "—") + $" {d.Top:0.0}–{d.Bottom:0.0}",
        BlastProject p => p.Header.Name.Length > 0 ? p.Header.Name : Loc.T("Blast"),
        Product p => p.Name,
        PointEntity pt => pt.Label,
        HoleRow r => r.Name,
        _ => o.ToString() ?? ""
    };

    private void UpdatePreview()
    {
        if (_previewPanel == null || _previewProperty == null || !_center.Children.Contains(_previewPanel.Parent as Control ?? _previewPanel)) return;
        var p = DraftModel.FindProperty(_previewProperty);
        if (p == null) return;
        var draft = DraftSession();
        var rows = new List<(string, CalcValue, CalcValue)>();
        foreach (var e in PreviewEntities(p.Context))
        {
            if (e is Deck d && _publishedSession.Scope.HoleOf(d) is { } h) draft.Scope.NoteParent(d, h);
            rows.Add((EntityName(e), _published.NodeOf(p.Id) != null ? _publishedSession.Value(p.Id, e) : CalcValue.Fail(Loc.T("new")), draft.Value(p.Id, e)));
        }
        Preview = rows;
        _previewPanel.Children.Clear();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*,*,*"), RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", rows.Count + 1))) };
        void Cell(int r, int c, string text, IBrush brush, bool mono = true)
        {
            var t = new TextBlock { Text = text, Foreground = brush, FontFamily = mono ? Look.Mono : FontFamily.Default, FontSize = 12, Margin = new Thickness(0, 2, 10, 2), TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = c == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right };
            Grid.SetRow(t, r);
            Grid.SetColumn(t, c);
            grid.Children.Add(t);
        }
        Cell(0, 0, Loc.T("Entity"), Look.Faint, false);
        Cell(0, 1, Loc.T("Before"), Look.Faint, false);
        Cell(0, 2, Loc.T("After"), Look.Faint, false);
        Cell(0, 3, "Δ", Look.Faint, false);
        var format = p.Format.Length > 0 ? p.Format : "0.###";
        string Text(CalcValue v) => v.IsError ? "#" + v.Error : v.Object is bool b ? (b ? "✓" : "—") : v.Object is string s ? s : v.Number.ToString(format, CultureInfo.CurrentCulture);
        for (int i = 0; i < rows.Count; i++)
        {
            var (name, before, after) = rows[i];
            Cell(i + 1, 0, name, Look.TextSoft, false);
            Cell(i + 1, 1, Text(before), before.IsError ? Bad : Look.Muted);
            Cell(i + 1, 2, Text(after), after.IsError ? Bad : after.Object == null && p.OutOfRange(after.Number) ? Warn : Look.Text);
            var delta = !before.IsError && !after.IsError && before.Object == null && after.Object == null ? after.Number - before.Number : double.NaN;
            Cell(i + 1, 3, double.IsNaN(delta) ? "" : Math.Abs(delta) < 1e-12 ? "0" : delta.ToString("+0.###;-0.###", CultureInfo.CurrentCulture), Math.Abs(delta) > 1e-12 ? Look.Accent : Look.Faint);
        }
        if (rows.Count == 0) _previewPanel.Children.Add(new TextBlock { Text = Loc.T("Nothing to show: select holes on the plan"), Foreground = Look.Faint });
        else _previewPanel.Children.Add(grid);
    }

    // ---- справа ---------------------------------------------------------

    private void BuildRight()
    {
        _right.Children.Clear();
        if (Selected == null) return;
        var source = Selected.StartsWith("param:", StringComparison.Ordinal) || Selected.StartsWith("builtin:", StringComparison.Ordinal) ? Selected : Selected;
        var node = DraftModel.NodeOf(Selected);
        var deps = new StackPanel { Spacing = 2 };
        IEnumerable<Dependency> list = node?.Dependencies ?? (Selected.StartsWith("builtin:", StringComparison.Ordinal)
            ? DraftModel.BuiltinDependencies[DraftModel.BuiltinIndex(Selected[8..])] : Array.Empty<Dependency>());
        foreach (var d in list.GroupBy(d => d.Source).Select(g => g.First()).OrderBy(d => d.Source))
            deps.Children.Add(Mono(d.Path.Count > 0 ? $"{d.Source}  ← {d.PathText}" : d.Source));
        if (deps.Children.Count == 0) deps.Children.Add(new TextBlock { Text = "—", Foreground = Look.Faint });
        _right.Children.Add(FormKit.Section(Loc.T("Depends on"), deps));

        var users = new StackPanel { Spacing = 2 };
        foreach (var n in DraftModel.Downstream(source).Take(40)) users.Children.Add(Mono(n.Id));
        if (users.Children.Count == 0) users.Children.Add(new TextBlock { Text = "—", Foreground = Look.Faint });
        _right.Children.Add(FormKit.Section(Loc.T("Used by"), users, FormKit.Tool("chart", null, () => _ = ShowGraph(), Loc.T("Show on the graph"))));

        var overrides = _project.Calc?.Overrides?.Count(o => o.Property == Selected) ?? 0;
        _right.Children.Add(FormKit.Section(Loc.T("Overrides"), new TextBlock { Text = overrides > 0 ? Loc.F("Set by hand for {0} entities", overrides) : Loc.T("None"), Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap }));

        CountAffected();
        _right.Children.Add(FormKit.Section(Loc.T("Publishing will change"), new TextBlock
        {
            Text = Loc.F("{0} holes, {1} blast totals", AffectedHoles, AffectedTotals), Foreground = AffectedHoles + AffectedTotals > 0 ? Look.Accent : Look.Muted, TextWrapping = TextWrapping.Wrap
        }));
    }

    private static TextBlock Mono(string text) => new() { Text = text, FontFamily = Look.Mono, FontSize = 11.5, Foreground = Look.TextSoft, TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>Holes and blast totals whose values differ between the published profile and the draft.</summary>
    private void CountAffected()
    {
        if (!Dirty) { AffectedHoles = AffectedTotals = 0; return; }
        var draft = DraftSession();
        var holeNodes = DraftModel.Nodes.Where(n => n.Context.Name is "hole" or "deck").ToList();
        int holes = 0;
        foreach (var h in _project.Holes)
        {
            bool changed = false;
            foreach (var n in holeNodes)
            {
                foreach (var e in n.Context.Name == "hole" ? new object[] { h } : h.Decks.Cast<object>())
                {
                    if (e is Deck d) { draft.Scope.NoteParent(d, h); _publishedSession.Scope.NoteParent(d, h); }
                    if (Differs(n.Id, e, draft)) { changed = true; break; }
                }
                if (changed) break;
            }
            if (changed) holes++;
        }
        AffectedHoles = holes;
        AffectedTotals = DraftModel.Nodes.Where(n => n.Context.Name == "blast").Count(n => Differs(n.Id, _project, draft));
    }

    private bool Differs(string id, object e, CalcSession draft)
    {
        var a = _published.NodeOf(id) != null ? _publishedSession.Value(id, e) : CalcValue.Fail("new");
        var b = draft.Value(id, e);
        if (a.IsError || b.IsError) return a.IsError != b.IsError;
        if (a.Object != null || b.Object != null) return !Equals(a.Object, b.Object);
        return Math.Abs(a.Number - b.Number) > 1e-9 * Math.Max(1, Math.Abs(a.Number));
    }

    private async Task ShowGraph()
    {
        if (Selected == null) return;
        var lines = new List<string>();
        var node = DraftModel.NodeOf(Selected);
        if (node != null) lines.Add($"[{node.Order}] {node.Id}");
        foreach (var d in node?.Dependencies.Select(x => x.Source).Distinct() ?? Enumerable.Empty<string>())
            lines.Add($"  ← {d}" + (DraftModel.NodeOf(d) is { } dn ? $"  [{dn.Order}]" : ""));
        foreach (var n in DraftModel.Downstream(Selected)) lines.Add($"  → [{n.Order}] {n.Id}");
        await Dialogs.Info(string.Join("\n", lines), Loc.T("Dependency graph"));
    }

    // ---- статус, сравнение, публикация ----------------------------------

    private void UpdateStatus()
    {
        var errors = DraftModel.Diagnostics.Count(d => !d.IsWarning);
        var warnings = DraftModel.Diagnostics.Count(d => d.IsWarning);
        var tests = Flat().Tests;
        int passed = 0;
        if (tests.Count > 0)
        {
            var s = DraftSession();
            passed = tests.Count(t => Evaluate(s, "blast", t.Expression, _project) is { IsError: false } v && Math.Abs(v.Number - t.Expected) <= t.Tolerance);
        }
        var first = DraftModel.Diagnostics.FirstOrDefault(d => !d.IsWarning);
        OutOfRangeValues = CountOutOfRange();
        _status.Text = Loc.F("Errors: {0} · warnings: {1} · test cases: {2} of {3}", errors, warnings, passed, tests.Count)
            + (OutOfRangeValues > 0 ? " · " + Loc.F("{0} values outside the reasonable range", OutOfRangeValues) : "") + (first != null ? " — " + first : "");
        _status.Foreground = errors > 0 ? Bad : passed < tests.Count || OutOfRangeValues > 0 ? Warn : Look.Muted;
    }

    /// <summary>Значений черновика за разумным диапазоном по всему проекту.</summary>
    public int OutOfRangeValues { get; private set; }

    private int CountOutOfRange()
    {
        var s = DraftSession();
        int n = 0;
        foreach (var node in DraftModel.Nodes.Where(x => x.Property.Min != null || x.Property.Max != null))
            foreach (var e in node.Context.All(s.Scope))
                if (s.Value(node, e) is { IsError: false, Object: null } v && node.Property.OutOfRange(v.Number)) n++;
        return n;
    }

    private void ShowDiagnostics()
    {
        var list = DraftModel.Diagnostics.Select(d => (d.IsWarning ? "⚠ " : "✗ ") + d).ToList();
        _ = Dialogs.Info(list.Count == 0 ? Loc.T("The profile has no errors.") : string.Join("\n", list.Take(40)), Loc.T("Check"));
    }

    /// <summary>Что изменит публикация: значения свойств затронутых скважин и итоги взрыва, было и стало.</summary>
    public List<(string Entity, string Property, string Format, CalcValue Before, CalcValue After)> Changes()
    {
        var draft = DraftSession();
        var rows = new List<(string, string, string, CalcValue, CalcValue)>();
        void Row(CalcNode n, object e, string name)
        {
            if (!Differs(n.Id, e, draft)) return;
            var before = _published.NodeOf(n.Id) != null ? _publishedSession.Value(n.Id, e) : CalcValue.Fail(Loc.T("new"));
            rows.Add((name, n.Property.NameRu.Length > 0 ? n.Property.NameRu : n.Property.Name, n.Property.Format, before, draft.Value(n, e)));
        }
        foreach (var n in DraftModel.Nodes.Where(n => n.Context.Name == "blast").OrderBy(n => n.Order)) Row(n, _project, Loc.T("Blast"));
        var holeNodes = DraftModel.Nodes.Where(n => n.Context.Name == "hole").OrderBy(n => n.Order).ToList();
        foreach (var h in _project.Holes)
            foreach (var n in holeNodes) Row(n, h, EntityName(h));
        return rows;
    }

    /// <summary>Таблица «было / стало / Δ» по затронутым скважинам и итогам.</summary>
    private Control ChangesView(List<(string Entity, string Property, string Format, CalcValue Before, CalcValue After)> rows)
    {
        if (rows.Count == 0) return new TextBlock { Text = Loc.T("The draft gives the same values as the published profile."), Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap };
        var shown = rows.Take(300).ToList();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*,*,*,*"), RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", shown.Count + 1))) };
        void Cell(int r, int c, string text, IBrush brush)
        {
            var t = new TextBlock { Text = text, Foreground = brush, FontSize = 12, Margin = new Thickness(0, 2, 10, 2), TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = c < 2 ? HorizontalAlignment.Left : HorizontalAlignment.Right };
            Grid.SetRow(t, r);
            Grid.SetColumn(t, c);
            grid.Children.Add(t);
        }
        Cell(0, 0, Loc.T("Entity"), Look.Faint);
        Cell(0, 1, Loc.T("Property"), Look.Faint);
        Cell(0, 2, Loc.T("Before"), Look.Faint);
        Cell(0, 3, Loc.T("After"), Look.Faint);
        Cell(0, 4, "Δ", Look.Faint);
        for (int i = 0; i < shown.Count; i++)
        {
            var (entity, property, format, before, after) = shown[i];
            var f = format.Length > 0 ? format : "0.###";
            string Text(CalcValue v) => v.IsError ? "#" + v.Error : v.Object is bool b ? (b ? "✓" : "—") : v.Object is string s ? s : v.Number.ToString(f, CultureInfo.CurrentCulture);
            Cell(i + 1, 0, entity, Look.TextSoft);
            Cell(i + 1, 1, property, Look.TextSoft);
            Cell(i + 1, 2, Text(before), before.IsError ? Bad : Look.Muted);
            Cell(i + 1, 3, Text(after), after.IsError ? Bad : Look.Text);
            if (!before.IsError && !after.IsError && before.Object == null && after.Object == null)
                Cell(i + 1, 4, (after.Number - before.Number).ToString("+0.###;-0.###;0", CultureInfo.CurrentCulture), Look.Accent);
        }
        var panel = new StackPanel { Spacing = 6, Children = { grid } };
        if (rows.Count > shown.Count) panel.Children.Add(new TextBlock { Text = Loc.F("And {0} more", rows.Count - shown.Count), Foreground = Look.Faint, FontSize = 12 });
        return new ScrollViewer { Content = panel, MaxHeight = 460 };
    }

    private async Task Compare()
    {
        Recompile();
        await Dialogs.Custom(Loc.T("Compare"), ChangesView(Changes()), width: 860);
    }

    /// <summary>Почему нельзя опубликовать (после последней попытки).</summary>
    public string? PublishError { get; private set; }

    private string? CheckPublish()
    {
        Recompile();
        Draft.Name = Draft.Name.Trim();
        return PublishError = CalcService.PublishProblem(_project, Draft);
    }

    /// <summary>Публикация из окна: проверка, таблица «было / стало» и обязательная причина изменения.</summary>
    public async Task<bool> Publish()
    {
        if (CheckPublish() is { } problem)
        {
            await Dialogs.Info(problem, Loc.T("Publish"));
            return false;
        }
        var reason = FormKit.Text();
        reason.Name = "publishReason";
        reason.Watermark = Loc.T("Why the formulas change (kept in the history)");
        var content = new StackPanel { Spacing = 12, Children = { FormKit.Section(Loc.T("Look at what changes before publishing:"), ChangesView(Changes())), FormKit.Labeled(Loc.T("Reason for the change"), reason) } };
        if (!await Dialogs.Custom(Loc.T("Publish"), content, () => !string.IsNullOrWhiteSpace(reason.Text), reason, 860)) return false;
        return Publish(reason.Text!.Trim());
    }

    /// <summary>Записывает черновик в проект (один шаг отмены) с причиной и сохраняет файл профиля; при непройденной проверке — false.</summary>
    public bool Publish(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) { PublishError = Loc.T("Reason for the change"); return false; }
        if (CheckPublish() != null) return false;
        var profile = Draft.Clone();
        var pending = _pendingBlast.ToDictionary(x => x.Key, x => x.Value);
        var ok = _vm.Change("Publish calculation profile", p =>
        {
            CalcService.Publish(p, profile, reason, Environment.UserName, DateTime.Now);
            if (pending.Count > 0)
            {
                p.Calc!.Parameters ??= new();
                foreach (var (k, v) in pending)
                    if (v is { } value) p.Calc.Parameters[k] = value; else p.Calc.Parameters.Remove(k);
            }
        });
        if (!ok) return false;
        try
        {
            var folder = Path.Combine(ProgramSettings.AppDataFolder, "calc");
            Directory.CreateDirectory(folder);
            var name = string.Concat(profile.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            profile.Save(Path.Combine(folder, name + CalcProfile.FileExtension));
            CalcService.Library.Add(profile);
        }
        catch (Exception) { /* the copy in the project is what counts; the file is a convenience */ }
        Draft.Version = profile.Version;
        _version.Text = profile.Version;
        _published = CalcService.ModelFor(_project);
        _publishedSession = CalcService.Session(_project, _published);
        _pendingBlast.Clear();
        Dirty = false;
        _draftBadge.IsVisible = false;
        AfterChange();
        return true;
    }

    /// <summary>Версии профиля проекта, новые первыми.</summary>
    public IReadOnlyList<ProfileVersion> History => (_project.Calc?.History ?? new()).AsEnumerable().Reverse().ToList();

    /// <summary>Откатывает проект к версии (один шаг отмены) и закрывает окно: черновик относился к другой версии.</summary>
    public bool Rollback(ProfileVersion version)
    {
        if (!_vm.Change("Roll back the calculation profile", p => CalcService.Rollback(p, version, Environment.UserName, DateTime.Now))) return false;
        Dirty = false;
        CloseRequested?.Invoke();
        return true;
    }

    private async Task ShowHistory()
    {
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        var window = new Window { Title = Loc.T("Profile history"), Width = 720, Height = 480, Background = Look.Ground, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = list } };
        if (History.Count == 0) list.Children.Add(new TextBlock { Text = Loc.T("No published versions yet."), Foreground = Look.Muted });
        var current = _project.Calc?.ProfileHash;
        foreach (var v in History)
        {
            var version = v;
            var back = FormKit.Secondary(Loc.T("Roll back"), () =>
            {
                window.Close();
                Rollback(version);
            });
            back.IsEnabled = v.ProfileHash != current;
            list.Children.Add(new Border
            {
                BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8),
                Child = new DockPanel
                {
                    Children =
                    {
                        Docked(back, Avalonia.Controls.Dock.Right),
                        new StackPanel
                        {
                            Spacing = 2,
                            Children =
                            {
                                new TextBlock { Text = $"{v.ProfileName} · {v.Version}", Foreground = Look.Text, FontWeight = FontWeight.SemiBold },
                                new TextBlock { Text = $"{v.Published:g} · {v.Author}", Foreground = Look.Faint, FontSize = 11.5 },
                                new TextBlock { Text = v.Reason, Foreground = Look.TextSoft, TextWrapping = TextWrapping.Wrap }
                            }
                        }
                    }
                }
            });
        }
        await Dialogs.ShowModal(window);
    }

    // ---- мелочи ---------------------------------------------------------

    private async Task AddProperty()
    {
        var contexts = DraftModel.Contexts.Select(c => c.Name).ToList();
        var ctx = await Dialogs.Choose(Loc.T("My property"), Loc.T("Context"), contexts, Math.Max(0, contexts.IndexOf("hole")));
        if (ctx is not { } ci) return;
        var name = await Dialogs.Prompt(Loc.T("My property"), Loc.T("Name (latin letters, no spaces)"), "myValue");
        if (string.IsNullOrWhiteSpace(name) || !name.All(c => char.IsLetterOrDigit(c) || c == '_') || !char.IsLetter(name[0])) return;
        var id = contexts[ci] + "." + name;
        if (DraftModel.FindProperty(id) != null) { await Dialogs.Info(Loc.T("There is a property with that name already.")); return; }
        Draft.Properties.Add(new PropertyDef { Id = id, NameRu = name, Unit = "?", Kind = PropertyKind.Computed, Group = Loc.T("Mine") });
        Draft.Formulas.Add(new FormulaDef { Property = id, Expression = "0" });
        Recompile();
        BuildTree();
        Select(id);
    }

    private static Control Labeled(string label, Control field) => Ui.Row(6, new TextBlock { Text = label, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 }, field);

    private static Control Info(string label, string value) => FormKit.Labeled(label, new TextBlock { Text = value, Foreground = Look.TextSoft, TextWrapping = TextWrapping.Wrap, FontFamily = Look.Mono, FontSize = 12 });

    private static Control Docked(Control c, Avalonia.Controls.Dock dock)
    {
        DockPanel.SetDock(c, dock);
        return c;
    }
}
