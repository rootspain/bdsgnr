using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.Views;

/// <summary>
/// "Save as rule" from the loading window: name, priority and applicability filters of a rule built from the current column
/// (<see cref="Charging.RuleFromColumn"/>). A name that is already in the loading rules replaces that rule; its id and
/// alternate products are kept.
/// </summary>
public static class SaveRuleWindow
{
    /// <summary>The form of the last call (used by the UI smoke test).</summary>
    public static SaveRuleForm? Last { get; private set; }

    public static async Task<LoadingRule?> Show(Window? owner, BlastProject project, Hole hole, IReadOnlyList<Deck> decks)
    {
        var form = new SaveRuleForm(project, hole, decks);
        Last = form;
        var w = new Window
        {
            Title = Loc.Ui("Save as rule"),
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Background = Look.Ground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = form
        };
        LoadingRule? result = null;
        form.Done = ok =>
        {
            if (ok) result = form.Build();
            w.Close();
        };
        await Dialogs.ShowModal(w, owner);
        return result;
    }
}

public sealed class SaveRuleForm : DockPanel
{
    private readonly BlastProject _p;
    private readonly Hole _hole;
    private readonly IReadOnlyList<Deck> _decks;
    private readonly TextBox _priority, _dMin, _dMax, _lMin, _lMax;
    private readonly CheckBox _type;
    private readonly ComboBox _wet;
    private readonly Border _replaceNote;
    private readonly TextBlock _replaceText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Look.TextSoft, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };

    public SaveRuleForm(BlastProject project, Hole hole, IReadOnlyList<Deck> decks)
    {
        _p = project;
        _hole = hole;
        _decks = decks;
        var sorted = decks.OrderBy(d => d.Top).ToList();
        var main = sorted.Where(d => project.FindProduct(d.ProductId)?.IsExplosive == true).OrderByDescending(d => d.Length).First();
        var mainName = project.FindProduct(main.ProductId)?.Name ?? "?";
        var stem = Charging.TopStemming(project, new Hole { Length = hole.EffectiveLength, Decks = sorted });

        NameBox = Box(Loc.F("{0}, stemming {1} m", mainName, N(stem)), mono: false);
        _priority = Box(N(project.LoadingRules.Count == 0 ? 0 : project.LoadingRules.Max(r => r.Priority) + 1, "0"));
        _dMin = Box(N(hole.Diameter, "0"));
        _dMax = Box(N(hole.Diameter, "0"));
        _lMin = Box("0");
        _lMax = Box("1000");
        _type = new CheckBox { Content = Loc.F("Only hole type {0}", hole.HoleType), Foreground = Look.TextSoft };
        _wet = new ComboBox { ItemsSource = new[] { L("Any"), L("Dry only"), L("Wet only") }, SelectedIndex = 0, Height = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
        _replaceNote = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(8),
            Background = Look.AccentSoft,
            BorderBrush = Look.Accent,
            BorderThickness = new Thickness(1),
            Child = Ui.Row(8, Look.Icon("warn", 15, Look.Accent), _replaceText)
        };
        NameBox.TextChanged += (_, _) => UpdateReplace();

        var summary = new TextBlock
        {
            Text = Loc.F("The {0} deck will adapt to the hole length; the stemming {1} m and the other decks keep their lengths, primers keep their distance above the deck bottom.", mainName, N(stem)),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Look.Faint
        };
        var body = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = L("Save as rule"), FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Look.Text },
                Field(L("Name"), NameBox),
                _replaceNote,
                summary,
                new TextBlock { Text = L("Applies to").ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Faint, Margin = new Thickness(0, 4, 0, 0) },
                Two(Field(L("Diameter from, mm"), _dMin), Field(L("Diameter to, mm"), _dMax)),
                Two(Field(L("Length from, m"), _lMin), Field(L("Length to, m"), _lMax)),
                Two(Field(L("Water"), _wet), Field(L("Priority"), _priority)),
                _type
            }
        };
        var cancel = new Button
        {
            Content = Loc.T("Cancel"),
            Height = 32,
            Padding = new Thickness(12, 0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            BorderBrush = Look.LineStrong,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsCancel = true
        };
        cancel.Click += (_, _) => Done?.Invoke(false);
        SaveButton = new Button
        {
            Height = 32,
            Padding = new Thickness(14, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsDefault = true,
            Classes = { "primary" }
        };
        SaveButton.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(NameBox.Text)) Done?.Invoke(true); };
        var footer = new Border
        {
            Background = Look.Panel,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 12),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, SaveButton } }
        };
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);
        Children.Add(body);
        UpdateReplace();
    }

    public TextBox NameBox { get; }

    public Button SaveButton { get; }

    public Action<bool>? Done { get; set; }

    /// <summary>The rule with the same name, which saving replaces; null for a new rule.</summary>
    public LoadingRule? Existing => _p.LoadingRules.FirstOrDefault(r => string.Equals(r.Name.Trim(), (NameBox.Text ?? "").Trim(), StringComparison.CurrentCultureIgnoreCase));

    private static string L(string english) => Loc.Ui(english);

    private static string N(double v, string format = "0.0") => v.ToString(format, CultureInfo.CurrentCulture);

    private static Control Field(string label, Control c) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontSize = 11.5, Foreground = Look.Muted }, c }
    };

    private static Grid Two(Control a, Control b)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        Grid.SetColumn(b, 2);
        g.Children.Add(a);
        g.Children.Add(b);
        return g;
    }

    private static TextBox Box(string text, bool mono = true) => new()
    {
        Text = text,
        Height = 32,
        MinHeight = 32,
        Padding = new Thickness(8, 5),
        FontFamily = mono ? Look.Mono : FontFamily.Default,
        Background = Look.Field,
        BorderBrush = Look.LineStrong
    };

    private void UpdateReplace()
    {
        var old = Existing;
        _replaceNote.IsVisible = old != null;
        if (old != null)
        {
            _replaceText.Text = Loc.F("Rule \"{0}\" is already in the loading rules. Replace it?", old.Name);
            _priority.Text = old.Priority.ToString(CultureInfo.CurrentCulture);
        }
        SaveButton.Content = L(old != null ? "Replace" : "Save");
    }

    private static double Num(TextBox b, double fallback) =>
        double.TryParse((b.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>The rule built from the column with the entered name and filters.</summary>
    public LoadingRule Build()
    {
        var rule = Charging.RuleFromColumn(_p, _decks, (NameBox.Text ?? "").Trim());
        rule.Priority = (int)Num(_priority, 0);
        rule.MinDiameter = Num(_dMin, 0);
        rule.MaxDiameter = Num(_dMax, 1000);
        rule.MinLength = Num(_lMin, 0);
        rule.MaxLength = Num(_lMax, 1000);
        rule.HoleType = _type.IsChecked == true ? _hole.HoleType : 0;
        rule.Wet = _wet.SelectedIndex switch { 1 => false, 2 => true, _ => null };
        if (Existing is { } old)
        {
            rule.Id = old.Id;
            rule.WetAlternateProductId = old.WetAlternateProductId;
            rule.HardAlternateProductId = old.HardAlternateProductId;
            rule.HardnessThreshold = old.HardnessThreshold;
            rule.SoftAlternateProductId = old.SoftAlternateProductId;
            rule.SoftnessThreshold = old.SoftnessThreshold;
            rule.SoftCollarExtraStemming = old.SoftCollarExtraStemming;
        }
        return rule;
    }
}
