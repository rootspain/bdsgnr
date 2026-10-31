using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.App.Views;

/// <summary>What the new blast window returns: the header and the template chosen (null: an empty blast).</summary>
public sealed record NewBlastSetup(ProjectHeader Header, string? Template);

/// <summary>File ▸ New: the blast header (the shot date starts at today) and the template to start from.</summary>
public static class NewBlastWindow
{
    public static async Task<NewBlastSetup?> Show(ProjectHeader header, IReadOnlyList<string> templates, int selectedTemplate)
    {
        var form = new NewBlastForm(header, templates, selectedTemplate);
        var w = new Window
        {
            Title = Loc.T("New blast"),
            Width = 640,
            Height = 720,
            MinWidth = 520,
            MinHeight = 480,
            Background = Look.Ground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = form
        };
        form.Closing = ok => { form.Applied = ok; w.Close(); };
        w.Opened += (_, _) => form.FocusName();
        await Dialogs.ShowModal(w);
        return form.Applied ? new NewBlastSetup(header, form.Template) : null;
    }
}

/// <summary>Content of the new blast window. Public so the UI smoke test can reach it.</summary>
public sealed class NewBlastForm : DockPanel
{
    private readonly ProjectHeader _header;
    private readonly IReadOnlyList<string> _templates;
    private readonly ComboBox _template;
    private readonly TextBox _name;

    public NewBlastForm(ProjectHeader header, IReadOnlyList<string> templates, int selectedTemplate)
    {
        _header = header;
        _templates = templates;
        _template = new ComboBox
        {
            Name = "template",
            ItemsSource = new[] { Loc.T("(none)") }.Concat(templates).ToList(),
            SelectedIndex = Math.Clamp(selectedTemplate, 0, templates.Count),
            MinWidth = 220,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(_template, Loc.T("Design data, products and layers of the new blast"));

        var title = Ui.Row(10, Look.Icon("new", 20, Look.Accent), new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = Loc.T("New blast"), FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Look.Text },
                new TextBlock { Text = Loc.T("The header goes into reports and the plan's title block"), FontSize = 11.5, Foreground = Look.Faint }
            }
        });
        var templateGroup = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { Caption("Template"), _template } };
        var top = new DockPanel();
        DockPanel.SetDock(templateGroup, Dock.Right);
        top.Children.Add(templateGroup);
        top.Children.Add(title);
        var head = new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(20, 14), Child = top };
        SetDock(head, Dock.Top);
        Children.Add(head);

        var footer = Footer();
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        _name = Text(nameof(ProjectHeader.Name), header.Name, v => header.Name = v);
        var date = new CalendarDatePicker
        {
            Name = "shotDate",
            SelectedDate = header.ShotDate,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Look.Field,
            BorderBrush = Look.LineStrong
        };
        date.SelectedDateChanged += (_, _) => header.ShotDate = date.SelectedDate;
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(20, 16, 20, 20) };
        body.Children.Add(Card("Blast",
            Wide(Field(nameof(ProjectHeader.Name), _name)),
            Field(nameof(ProjectHeader.BlastNumber), Text(nameof(ProjectHeader.BlastNumber), header.BlastNumber, v => header.BlastNumber = v)),
            Field(nameof(ProjectHeader.ShotDate), date)));
        body.Children.Add(Card("Place",
            Field(nameof(ProjectHeader.Mine), Text(nameof(ProjectHeader.Mine), header.Mine, v => header.Mine = v)),
            Field(nameof(ProjectHeader.Bench), Text(nameof(ProjectHeader.Bench), header.Bench, v => header.Bench = v)),
            Field(nameof(ProjectHeader.Location), Text(nameof(ProjectHeader.Location), header.Location, v => header.Location = v)),
            Field(nameof(ProjectHeader.Client), Text(nameof(ProjectHeader.Client), header.Client, v => header.Client = v))));
        body.Children.Add(Card("Author and notes",
            Field(nameof(ProjectHeader.Author), Text(nameof(ProjectHeader.Author), header.Author, v => header.Author = v)),
            Field(nameof(ProjectHeader.Description), Text(nameof(ProjectHeader.Description), header.Description, v => header.Description = v)),
            Wide(Field(nameof(ProjectHeader.Comment), Text(nameof(ProjectHeader.Comment), header.Comment, v => header.Comment = v, multiline: true)))));
        Children.Add(new ScrollViewer { Content = body });

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control) { Closing?.Invoke(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Closing?.Invoke(false); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public bool Applied { get; set; }
    public Action<bool>? Closing { get; set; }
    /// <summary>The template chosen, null for an empty blast.</summary>
    public string? Template => _template.SelectedIndex > 0 ? _templates[_template.SelectedIndex - 1] : null;

    public void FocusName() { _name.Focus(); _name.SelectAll(); }

    private static TextBlock Caption(string english) => new() { Text = Loc.T(english), FontSize = 11.5, Foreground = Look.Muted };

    private static TextBox Text(string name, string value, Action<string> set, bool multiline = false)
    {
        var box = new TextBox
        {
            Name = name,
            Text = value,
            MinHeight = multiline ? 64 : 32,
            Height = multiline ? double.NaN : 32,
            Padding = new Thickness(8, 5),
            Background = Look.Field,
            BorderBrush = Look.LineStrong,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center
        };
        box.TextChanged += (_, _) => set(box.Text ?? "");
        return box;
    }

    /// <summary>A header property's name above its editor.</summary>
    private static Control Field(string property, Control editor) =>
        new StackPanel { Spacing = 4, Children = { new TextBlock { Text = Loc.Property(property), FontSize = 11.5, Foreground = Look.Muted }, editor } };

    private static Control Wide(Control c) { Grid.SetColumnSpan(c, 2); c.Tag = "wide"; return c; }

    /// <summary>Fields two to a row; a wide one takes the whole row.</summary>
    private static Control Card(string title, params Control[] fields)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 14, RowSpacing = 12 };
        int row = -1, col = 2;
        foreach (var f in fields)
        {
            var wide = (f.Tag as string) == "wide";
            if (wide || col > 1) { row++; col = 0; grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); }
            Grid.SetRow(f, row);
            Grid.SetColumn(f, col);
            grid.Children.Add(f);
            col = wide ? 2 : col + 1;
        }
        return new Border
        {
            Background = Look.Panel,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 14),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = Loc.T(title).ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Faint },
                    grid
                }
            }
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
            Name = "cancel"
        };
        cancel.Click += (_, _) => Closing?.Invoke(false);
        var create = new Button
        {
            Content = Loc.T("Create"),
            Height = 32,
            Padding = new Thickness(18, 0),
            CornerRadius = new CornerRadius(6),
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { "primary" },
            Name = "create"
        };
        ToolTip.SetTip(create, Loc.T("Create") + " (Ctrl+Enter)");
        create.Click += (_, _) => Closing?.Invoke(true);
        var hint = new TextBlock { Text = Loc.T("Ctrl+Enter creates, Esc cancels"), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var buttons = Ui.Row(8, cancel, create);
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        dock.Children.Add(hint);
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 12), Child = dock };
    }
}
