using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using BlastDesigner.App.Services;
using BlastDesigner.IO;

namespace BlastDesigner.App.Views;

/// <summary>
/// The preset buttons of the fragmentation inputs: «Load preset…» and «Save preset…» of the inputs of an editor (a site's
/// inputs in a file of their own, to load into another blast), and the line that says what was loaded or saved
/// (<see cref="Note"/>, placed by the window).
/// </summary>
public sealed class FragmentationPresetBar : Border
{
    private readonly FragmentationInputsEditor _editor;

    /// <summary>What was loaded or saved last.</summary>
    public TextBlock Note { get; } = FragmentationUi.Small();

    /// <summary>A preset was loaded into the editor.</summary>
    public event Action? Loaded;

    public FragmentationPresetBar(FragmentationInputsEditor editor)
    {
        _editor = editor;
        BorderBrush = Look.Line;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(1);
        VerticalAlignment = VerticalAlignment.Top;
        Child = Ui.Row(0,
            PresetButton("open", "Load preset…", "Put the inputs saved in a preset file in this window", OpenPreset, "loadPreset"),
            PresetButton("save", "Save preset…", "Save these inputs to a file, to load them into another blast", () => _ = SavePresetAs(), "savePreset"));
    }

    private static Button PresetButton(string icon, string text, string tip, Action click, string name)
    {
        var b = Ui.Flat(Ui.Row(6, Look.Icon(icon, 15, Look.TextSoft), new TextBlock { Text = Loc.Ui(text), VerticalAlignment = VerticalAlignment.Center }), Loc.Ui(tip), click, height: 30);
        b.Name = name;
        return b;
    }

    /// <summary>Saves the inputs as they are in the editor to a preset file.</summary>
    public void SavePreset(string path)
    {
        _editor.Commit();
        FragmentationPreset.Save(path, _editor.Draft);
    }

    /// <summary>Puts the inputs of a preset file in the editor.</summary>
    public void LoadPreset(string path)
    {
        _editor.Load(FragmentationPreset.Load(path));
        Loaded?.Invoke();
    }

    private static readonly (string Name, string[] Patterns) PresetFilter = ("Fragmentation preset", new[] { "*" + FragmentationPreset.Extension });

    /// <summary>Asks for a file and saves the inputs to it; true when saved.</summary>
    public async Task<bool> SavePresetAs()
    {
        var path = await Dialogs.SaveFile("Save fragmentation preset", Loc.T("Fragmentation inputs") + FragmentationPreset.Extension, FragmentationPreset.Extension.TrimStart('.'), PresetFilter);
        if (path == null) return false;
        try { SavePreset(path); Note.Text = Loc.F(Loc.Ui("Saved: {0}"), Path.GetFileName(path)); return true; }
        catch (Exception ex) { Dialogs.ShowError(ex); return false; }
    }

    private async void OpenPreset()
    {
        var path = await Dialogs.OpenFile("Load fragmentation preset", PresetFilter);
        if (path == null) return;
        try { LoadPreset(path); Note.Text = Loc.F(Loc.Ui("Loaded: {0}"), Path.GetFileName(path)); }
        catch (InvalidDataException) { await Dialogs.Info(Loc.Ui("The file is not a fragmentation preset.")); }
        catch (Exception ex) { Dialogs.ShowError(ex); }
    }
}
