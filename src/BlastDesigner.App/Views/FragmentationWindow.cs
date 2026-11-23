using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.App.Views;

/// <summary>
/// The fragmentation inputs page of the project properties (Edit ▸ Fragmentation inputs). Public so the UI smoke test can
/// reach it. A thin frame round the shared components: the title with the preset buttons
/// (<see cref="FragmentationPresetBar"/>), the inputs (<see cref="FragmentationInputsEditor"/>: the rock factor A and f, the
/// rock mass, the drilling and the size thresholds) and the prediction for the blast (<see cref="FragmentationPreview"/>).
/// </summary>
public sealed class FragmentationForm : DockPanel
{
    private readonly BlastProject _p;
    private readonly FragmentationInputsEditor _editor;
    private readonly FragmentationPresetBar _presets;
    private readonly FragmentationPreview _preview = new();

    public FragmentationForm(BlastProject project)
    {
        _p = project;
        _editor = new FragmentationInputsEditor(ProjectJson.DeepClone(project.Fragmentation), project.Design.RockDensity);
        _presets = new FragmentationPresetBar(_editor);

        var header = Header();
        SetDock(header, Dock.Top);
        Children.Add(header);

        var body = new StackPanel { Spacing = 18, Margin = new Thickness(16, 14, 16, 18) };
        body.Children.Add(_editor);
        body.Children.Add(FragmentationUi.Section("Prediction for the blast", FragmentationUi.Card(_preview)));
        Children.Add(new ScrollViewer { Content = body });

        _editor.Changed += RefreshPreview;
        RefreshPreview();
    }

    /// <summary>The inputs as edited; written to the project when the window is applied.</summary>
    public FragmentationModel Draft => _editor.Draft;

    /// <summary>The shared inputs editor of the page.</summary>
    public FragmentationInputsEditor Inputs => _editor;

    /// <summary>Saves the inputs as they are in the window to a preset file.</summary>
    public void SavePreset(string path) => _presets.SavePreset(path);

    /// <summary>Puts the inputs of a preset file in the window (applied to the blast with Apply).</summary>
    public void LoadPreset(string path) => _presets.LoadPreset(path);

    /// <summary>The rock factor the calculation uses with the inputs as they are now.</summary>
    public double RockFactor => _editor.RockFactor;
    public double ProtodyakonovF => _editor.ProtodyakonovF;

    private void RefreshPreview() => _preview.Show(_p, _editor.Draft);

    private Control Header()
    {
        var title = new TextBlock { Text = Loc.T("Fragmentation inputs"), FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Look.Text };
        // presets: the inputs of a site in a file of their own, to load into another blast
        _presets.Note.Margin = new Thickness(0, 4, 0, 0);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var left = new StackPanel { Children = { title, _presets.Note } };
        Grid.SetColumn(_presets, 1);
        g.Children.Add(left);
        g.Children.Add(_presets);
        return new Border
        {
            Background = Look.Panel,
            BorderBrush = Look.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 12),
            Child = g
        };
    }
}
