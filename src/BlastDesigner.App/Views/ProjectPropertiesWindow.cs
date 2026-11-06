using System.Text.Json;
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

/// <summary>Edit ▸ Project properties: what belongs to the project, a page for each part. The older commands open their page.</summary>
public static class ProjectPropertiesWindow
{
    public static async Task Show(MainViewModel vm, string page)
    {
        if (vm.Project == null) return;
        var form = new ProjectPropertiesForm(vm.Project, page);
        if (await Dialogs.Custom("Project properties", form, width: 1100, height: 760, subtitle: vm.Project.Header.Name, form: true, bare: true)) form.Apply(vm);
    }
}

/// <summary>
/// Content of the project properties window. Public so the UI smoke test can reach it. Every page edits a copy; Apply makes
/// one undoable change of the pages that differ from what they started with, so OK on an untouched window changes nothing.
/// </summary>
public sealed class ProjectPropertiesForm : DockPanel
{
    private sealed record Page(string Group, string Title, string Icon, Func<Control> Build, Func<bool> Edited, Action<BlastProject> Apply);

    /// <summary>The two tables of types, edited together on one page.</summary>
    private sealed class TypeTables
    {
        public List<TypeDefinition> Holes { get; set; } = new();
        public List<TypeDefinition> Materials { get; set; } = new();
    }

    private readonly List<Page> _pages = new();
    private readonly Dictionary<string, Control> _built = new();
    private readonly Dictionary<string, Button> _nav = new();
    private readonly ContentControl _host = new();
    private FragmentationForm? _fragmentation;

    public ProjectPropertiesForm(BlastProject project, string page)
    {
        const string general = "General", design = "Designing", predictions = "Predictions", initiation = "Initiation", totals = "Totals";
        Fields(general, "Blast properties", "edit", project.Header, (p, v) => p.Header = v);
        Fields(general, "Site coordinate system", "north", project.Coordinates, (p, v) => p.Coordinates = v);
        Fields(design, "Design data", "pattern", project.Design, (p, v) => { v.HoleTypes = p.Design.HoleTypes; v.MaterialTypes = p.Design.MaterialTypes; p.Design = v; });
        Add(design, "Hole and material types", "table", new TypeTables { Holes = project.Design.HoleTypes, Materials = project.Design.MaterialTypes },
            (p, v) => { p.Design.HoleTypes = v.Holes; p.Design.MaterialTypes = v.Materials; }, v => TypeTableWindow.Editor(v.Holes, v.Materials));
        Fields(design, "Design tolerances", "check", project.Tolerances, (p, v) => p.Tolerances = v);
        Fields(predictions, "Vibration site constants", "wave", project.Vibration, (p, v) => p.Vibration = v);
        Add(predictions, "Vibration events", "chart", project.VibrationRecords, (p, v) => p.VibrationRecords = v, Events);
        Fields(predictions, "Flyrock constants", "exclusion", project.Flyrock, (p, v) => p.Flyrock = v);
        string? fragmentation = null;
        _pages.Add(new Page(predictions, "Fragmentation inputs", "frag",
            () => { _fragmentation = new FragmentationForm(project); fragmentation = Json(_fragmentation.Draft); return _fragmentation; },
            () => _fragmentation != null && Json(_fragmentation.Draft) != fragmentation,
            p => p.Fragmentation = _fragmentation!.Draft));
        Fields(initiation, "Electronic timing preferences", "ebs", project.Electronic, (p, v) => p.Electronic = v);
        Fields(totals, "Resource cost schema", "money", project.Costs, (p, v) => p.Costs = v);
        Fields(totals, "Blast outcomes", "report", project.Outcomes, (p, v) => p.Outcomes = v);
        Fields(totals, "Summary block", "dash", project.Summary, (p, v) => p.Summary = v);

        var list = new StackPanel { Spacing = 2, Margin = new Thickness(10, 12) };
        foreach (var group in _pages.GroupBy(p => p.Group))
        {
            list.Children.Add(new TextBlock { Text = Loc.Ui(group.Key).ToUpper(System.Globalization.CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Faint, Margin = new Thickness(10, list.Children.Count == 0 ? 2 : 14, 0, 4) });
            foreach (var p in group) list.Children.Add(_nav[p.Title] = NavButton(p));
        }
        var nav = new Border { Width = 330, Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = new ScrollViewer { Content = list } };
        SetDock(nav, Dock.Left);
        Children.Add(nav);
        Children.Add(_host);
        Show(_nav.ContainsKey(page) ? page : _pages[0].Title);
    }

    /// <summary>The page on view, by its title in English.</summary>
    public string Shown { get; private set; } = "";

    public void Show(string title)
    {
        var page = _pages.First(p => p.Title == title);
        if (!_built.TryGetValue(title, out var content)) _built[title] = content = page.Build();
        _host.Content = content;
        Shown = title;
        foreach (var (name, b) in _nav) b.Background = name == title ? Look.AccentSoft : Brushes.Transparent;
    }

    /// <summary>Writes the pages that were edited to the project, as one step of the undo list.</summary>
    public void Apply(MainViewModel vm)
    {
        var edited = _pages.Where(p => p.Edited()).ToList();
        if (edited.Count == 0) return;
        vm.Change(edited.Count == 1 ? edited[0].Title : "Project properties", p => { foreach (var page in edited) page.Apply(p); });
        // the unit last chosen is the one of the projects and presets that have none
        if (_fragmentation != null && edited.Any(p => p.Title == "Fragmentation inputs") && _fragmentation.Draft.SizeUnit is { } unit && unit != vm.Settings.FragmentSizeUnit)
        {
            vm.Settings.FragmentSizeUnit = SizeUnits.Default = unit;
            vm.Settings.Save();
        }
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ProjectJson.Options);

    /// <summary>A page on a copy of a part of the project.</summary>
    private void Add<T>(string group, string title, string icon, T part, Action<BlastProject, T> apply, Func<T, Control> build) where T : class
    {
        var copy = ProjectJson.DeepClone(part);
        var before = Json(copy);
        _pages.Add(new Page(group, title, icon, () => build(copy), () => Json(copy) != before, p => apply(p, copy)));
    }

    /// <summary>A page of the fields of a part, in the groups of its form.</summary>
    private void Fields<T>(string group, string title, string icon, T part, Action<BlastProject, T> apply) where T : class => Add(group, title, icon, part, apply, copy =>
    {
        var grid = new PropertyGrid();
        FormLayouts.Apply(grid, typeof(T));
        grid.SetTarget(copy);
        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Thickness(22, 18, 22, 24),
                Children = { new TextBlock { Text = Loc.T(title), FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Look.Text }, grid }
            }
        };
    });

    private static Control Events(List<VibrationRecord> records) => new ListEditor<VibrationRecord>(records, r => $"{(r.Use ? "" : "✗ ")}{(r.Name.Length > 0 ? r.Name : "?")}  {r.PpvMm:0.##} mm/s @ {r.DistanceM:0} m",
        () => new VibrationRecord { Name = Loc.F("Event {0}", records.Count + 1), Date = DateTime.Today },
        r => ProjectJson.DeepClone(r));

    private Button NavButton(Page page)
    {
        var b = new Button
        {
            Content = Ui.Row(10, Look.Icon(page.Icon, 16, Look.Muted), new TextBlock { Text = Loc.T(page.Title), VerticalAlignment = VerticalAlignment.Center, Foreground = Look.TextSoft, TextTrimming = TextTrimming.CharacterEllipsis }),
            Height = 34,
            Padding = new Thickness(10, 0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = "page:" + page.Title
        };
        b.Classes.Add("flat");
        b.Click += (_, _) => Show(page.Title);
        return b;
    }
}
