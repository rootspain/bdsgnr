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
    private readonly CalcModel _published;
    private readonly CalcSession _publishedSession;
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

        var tools = Ui.Row(8,
            Labeled(Loc.T("Profile"), _name), Labeled(Loc.T("Version"), _version), _draftBadge,
            FormKit.Secondary(Loc.T("Check"), () => { Recompile(); ShowDiagnostics(); }),
            FormKit.Secondary(Loc.T("Compare"), () => _ = Compare()),
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
        if (ru.Length > 0) ToolTip.SetTip(row, ru);
        return new TreeViewItem { Header = row, Tag = id };
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

    private StackPanel? _previewPanel;
    private string? _previewProperty;

    private Control Variant(PropertyDef p, List<FormulaDef> formulas, int index)
    {
        var f = formulas[index];
        var text = new TextBox
        {
            Text = f.Expression, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76, FontFamily = Look.Mono, FontSize = 13,
            Background = Look.Field, BorderBrush = Look.LineStrong, Padding = new Thickness(8, 6), Name = index == 0 ? "formula" : "formula" + index
        };
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var hint = new TextBlock { FontSize = 11.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap };
        var condition = FormKit.Text(f.Condition);
        condition.Watermark = Loc.T("always");
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
                status.Text = unitOk ? Loc.F("OK · {0} · depends on {1}", c.Unit.IsUnknown ? "?" : c.Unit.Symbol, c.Dependencies.Count)
                    : Loc.F("The formula gives {0}, the property is in {1}", c.Unit.DimensionText, want.DimensionText);
                status.Foreground = unitOk ? Ok : Warn;
            }
            catch (CalcSyntaxException e)
            {
                status.Text = Loc.F("Error at {0}: {1}", e.Position + 1, e.Message);
                status.Foreground = Bad;
            }
            if (index == 0) FormulaStatus = status.Text ?? "";
            hint.Text = Suggestions(p.Context, text.Text ?? "", text.CaretIndex);
        }
        if (index == 0) _checkMain = Check;
        text.TextChanged += (_, _) =>
        {
            if (text.Text == f.Expression) return;
            f.Expression = text.Text ?? "";
            SetFormulas(p.Id, formulas);
            Check();
        };
        condition.TextChanged += (_, _) => { var v = string.IsNullOrWhiteSpace(condition.Text) ? null : condition.Text; if (v != f.Condition) { f.Condition = v; SetFormulas(p.Id, formulas); } };
        priority.TextChanged += (_, _) => { if (int.TryParse(priority.Text, out var v) && v != f.Priority) { f.Priority = v; SetFormulas(p.Id, formulas); } };
        onError.TextChanged += (_, _) =>
        {
            double? v = double.TryParse(onError.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var d) ? d : null;
            if (v != f.OnError) { f.OnError = v; SetFormulas(p.Id, formulas); }
        };
        text.PropertyChanged += (_, e) => { if (e.Property == TextBox.CaretIndexProperty) hint.Text = Suggestions(p.Context, text.Text ?? "", text.CaretIndex); };
        Check();
        var body = new StackPanel { Spacing = 8 };
        if (formulas.Count > 1 || f.Condition != null)
        {
            var remove = FormKit.Tool("trash", null, () => { formulas.RemoveAt(index); SetFormulas(p.Id, formulas); ShowCenter(); }, Loc.T("Delete the variant"));
            remove.IsEnabled = formulas.Count > 1;
            body.Children.Add(new DockPanel
            {
                Children =
                {
                    Docked(remove, Avalonia.Controls.Dock.Right),
                    FormKit.Fields(3, FormKit.Labeled(Loc.T("Condition"), condition), FormKit.Labeled(Loc.T("Priority"), priority), FormKit.Labeled(Loc.T("Value on error"), onError))
                }
            });
        }
        else body.Children.Add(FormKit.Fields(3, FormKit.Labeled(Loc.T("Condition"), condition), FormKit.Labeled(Loc.T("Priority"), priority), FormKit.Labeled(Loc.T("Value on error"), onError)));
        body.Children.Add(text);
        body.Children.Add(status);
        body.Children.Add(hint);
        if (f.Comment.Length > 0 || f.Source.Length > 0)
            body.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { f.Comment, f.Source.Length > 0 ? Loc.T("Source") + ": " + f.Source : "" }.Where(s => s.Length > 0)), FontSize = 11.5, Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap });
        return new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Child = body };
    }

    /// <summary>Names of the context that start with the word at the caret: the autocompletion hint.</summary>
    private string Suggestions(string context, string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        int start = caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_' || text[start - 1] == '.')) start--;
        var word = text[start..caret];
        var ctx = context;
        var dot = word.LastIndexOf('.');
        if (dot >= 0)
        {
            // по связям: product.de → свойства продукта
            foreach (var part in word[..dot].Split('.'))
                if (DraftModel.FindProperty(ctx, part) is { Target: { } t }) ctx = t;
                else return "";
            word = word[(dot + 1)..];
        }
        if (word.Length == 0) return "";
        var names = DraftModel.PropertiesOf(ctx).Select(p => p.Name)
            .Concat(new[] { "sum", "avg", "min", "max", "count", "first", "if", "case", "coalesce", "exists", "param", "lookup", "to", "clamp", "round", "sqrt" })
            .Concat(CalcService.Registry.Builtins.Keys)
            .Where(n => n.StartsWith(word, StringComparison.OrdinalIgnoreCase) && !n.Equals(word, StringComparison.OrdinalIgnoreCase))
            .Distinct().OrderBy(n => n.Length).Take(8).ToList();
        return names.Count > 0 ? Loc.T("Names") + ": " + string.Join(", ", names) : "";
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
        if (_formulas == null || Selected == null) return;
        // TextChanged приходит позже (через диспетчер): черновик меняется сразу, поле только показывает текст
        _formulas[0].Expression = text;
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
            Cell(i + 1, 2, Text(after), after.IsError ? Bad : Look.Text);
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
        _status.Text = Loc.F("Errors: {0} · warnings: {1} · test cases: {2} of {3}", errors, warnings, passed, tests.Count) + (first != null ? " — " + first : "");
        _status.Foreground = errors > 0 ? Bad : passed < tests.Count ? Warn : Look.Muted;
    }

    private void ShowDiagnostics()
    {
        var list = DraftModel.Diagnostics.Select(d => (d.IsWarning ? "⚠ " : "✗ ") + d).ToList();
        _ = Dialogs.Info(list.Count == 0 ? Loc.T("The profile has no errors.") : string.Join("\n", list.Take(40)), Loc.T("Check"));
    }

    private async Task Compare()
    {
        Recompile();
        var draft = DraftSession();
        var lines = new List<string>();
        foreach (var n in DraftModel.Nodes.Where(n => n.Context.Name == "blast").OrderBy(n => n.Order))
        {
            var a = _published.NodeOf(n.Id) != null ? _publishedSession.Value(n.Id, _project) : CalcValue.Fail(Loc.T("new"));
            var b = draft.Value(n.Id, _project);
            if (!Differs(n.Id, _project, draft)) continue;
            lines.Add($"{n.Property.NameRu} ({n.Id}): {a} → {b}");
        }
        await Dialogs.Info(lines.Count == 0 ? Loc.T("The draft gives the same blast totals as the published profile.") : string.Join("\n", lines.Take(40)), Loc.T("Compare"));
    }

    /// <summary>Puts the draft in the project (one undo step) and saves it to the profiles folder.</summary>
    public async Task<bool> Publish()
    {
        Recompile();
        if (DraftModel.HasErrors)
        {
            await Dialogs.Info(Loc.T("The profile has errors; fix them before publishing.") + "\n" + string.Join("\n", DraftModel.Diagnostics.Where(d => !d.IsWarning).Take(10)), Loc.T("Publish"));
            return false;
        }
        var profile = Draft.Clone();
        var pending = _pendingBlast.ToDictionary(x => x.Key, x => x.Value);
        var ok = _vm.Change("Publish calculation profile", p =>
        {
            var unchanged = profile.Base == CalcService.BuiltInName && profile.Formulas.Count == 0 && profile.Parameters.Count == 0 && profile.Properties.Count == 0 && profile.Tests.Count == 0;
            if (unchanged) CalcService.SetProfile(p, CalcService.BuiltIn);
            else CalcService.SetProfile(p, profile);
            if (pending.Count > 0)
            {
                p.Calc ??= new ProjectCalc { ProfileName = CalcService.BuiltInName };
                p.Calc.Parameters ??= new();
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
        Dirty = false;
        _draftBadge.IsVisible = false;
        return true;
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
