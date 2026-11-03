using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BlastDesigner.App.Views;
using BlastDesigner.IO;

namespace BlastDesigner.App.Services;

/// <summary>Message boxes, prompts and file pickers built in code so every dialog is localised the same way.</summary>
public static class Dialogs
{
    public static Window? Owner { get; set; }

    public static (string Name, string[] Patterns) Filter(string name, params string[] patterns) => (name, patterns);

    public static async Task<string?> OpenFile(string title, params (string Name, string[] Patterns)[] filters)
    {
        var files = await OpenFiles(title, false, filters);
        return files.FirstOrDefault();
    }

    public static async Task<List<string>> OpenFiles(string title, bool multiple, params (string Name, string[] Patterns)[] filters)
    {
        if (Owner == null) return new();
        var result = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T(title),
            AllowMultiple = multiple,
            FileTypeFilter = filters.Select(f => new FilePickerFileType(Loc.T(f.Name)) { Patterns = f.Patterns }).Append(new FilePickerFileType(Loc.T("All files")) { Patterns = new[] { "*" } }).ToList()
        });
        return result.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public static async Task<string?> SaveFile(string title, string defaultName, string extension, params (string Name, string[] Patterns)[] filters)
    {
        if (Owner == null) return null;
        var result = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T(title),
            SuggestedFileName = defaultName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = filters.Select(f => new FilePickerFileType(Loc.T(f.Name)) { Patterns = f.Patterns }).ToList()
        });
        var path = result?.TryGetLocalPath();
        if (path != null && !Path.HasExtension(path)) path += extension;
        return path;
    }

    public static async Task<string?> PickFolder(string title)
    {
        if (Owner == null) return null;
        var r = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T(title) });
        return r.FirstOrDefault()?.TryGetLocalPath();
    }

    public static Task Info(string message, string title = "Blast Designer") => Show(title, message, new[] { "OK" }).ContinueWith(_ => { });

    public static async Task<bool> Confirm(string message, string title = "Blast Designer") =>
        await Show(title, message, new[] { "Yes", "No" }) == 0;

    /// <summary>Yes = true, No = false, Cancel = null.</summary>
    public static async Task<bool?> YesNoCancel(string message, string title = "Blast Designer") =>
        await Show(title, message, new[] { "Yes", "No", "Cancel" }) switch { 0 => true, 1 => false, _ => null };

    public static void ShowError(Exception ex)
    {
        Log.Error("Unhandled UI error", ex);
        Dispatcher.UIThread.Post(() => _ = Show("Error", ex.Message, new[] { "OK" }));
    }

    public static async Task<int> Show(string title, string message, string[] buttons)
    {
        var w = new Window
        {
            Title = Loc.T(title),
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 320,
            MaxWidth = 720,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        int result = -1;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            var idx = i;
            var b = new Button { Content = Loc.T(buttons[i]), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = i == 0, IsCancel = i == buttons.Length - 1 };
            b.Click += (_, _) => { result = idx; w.Close(); };
            panel.Children.Add(b);
        }
        w.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children = { new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, panel }
        };
        if (Owner != null && Owner.IsVisible) await w.ShowDialog(Owner); else w.Show();
        return result;
    }

    public static async Task<string?> Prompt(string title, string label, string value = "")
    {
        var box = new TextBox { Text = value, MinWidth = 280 };
        var ok = await Custom(title, new StackPanel { Spacing = 6, Children = { new TextBlock { Text = label }, box } }, () => true, box);
        return ok ? box.Text : null;
    }

    public static async Task<double?> PromptNumber(string title, string label, double value)
    {
        var box = new NumericUpDown { Value = (decimal)value, FormatString = "0.###", MinWidth = 200, Increment = 1 };
        var ok = await Custom(title, new StackPanel { Spacing = 6, Children = { new TextBlock { Text = label }, box } }, () => box.Value.HasValue, box);
        return ok ? (double?)box.Value : null;
    }

    public static async Task<int?> Choose(string title, string label, IReadOnlyList<string> items, int selected = 0)
    {
        if (items.Count == 0) return null;
        var combo = new ComboBox { ItemsSource = items, SelectedIndex = Math.Clamp(selected, 0, items.Count - 1), MinWidth = 280 };
        var ok = await Custom(title, new StackPanel { Spacing = 6, Children = { new TextBlock { Text = label }, combo } }, () => combo.SelectedIndex >= 0, combo);
        return ok ? combo.SelectedIndex : null;
    }

    /// <summary>Shows arbitrary content with OK/Cancel. Returns true on OK.</summary>
    public static async Task<bool> Custom(string title, Control content, Func<bool>? validate = null, Control? focus = null, double? width = null, double? height = null)
    {
        var w = new Window
        {
            Title = Loc.T(title),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = width == null ? SizeToContent.WidthAndHeight : SizeToContent.Manual,
            MinWidth = 340
        };
        if (width != null) { w.Width = width.Value; w.Height = height ?? 500; }
        bool ok = false;
        var okBtn = new Button { Content = Loc.T("OK"), MinWidth = 80, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = Loc.T("Cancel"), MinWidth = 80, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        okBtn.Click += (_, _) =>
        {
            if (validate?.Invoke() == false) return;
            ok = true;
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        var dock = new DockPanel { Margin = new Thickness(12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { okBtn, cancel } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(content);
        w.Content = dock;
        if (focus != null) w.Opened += (_, _) => focus.Focus();
        LastShown = w;
        if (Owner != null) await w.ShowDialog(Owner); else w.Show();
        return ok;
    }

    /// <summary>Shows a window built by the caller as a modal dialog (remembered as <see cref="LastShown"/> for the smoke test).</summary>
    public static async Task ShowModal(Window w, Window? owner = null)
    {
        LastShown = w;
        owner ??= Owner;
        if (owner != null) await w.ShowDialog(owner); else w.Show();
    }

    /// <summary>Edits a copy of an object in a property grid; returns the edited copy on OK.</summary>
    public static async Task<T?> EditObject<T>(string title, T obj, params string[] exclude) where T : class
    {
        var copy = Domain.ProjectJson.DeepClone(obj);
        var grid = new PropertyGrid { Exclude = exclude.ToHashSet() };
        grid.SetTarget(copy);
        var scroll = new ScrollViewer { Content = grid, MaxHeight = 640, MinWidth = 440 };
        return await Custom(title, scroll) ? copy : null;
    }

    /// <summary>The most recent tool window opened by <see cref="ShowWindow"/> (used by the UI smoke test).</summary>
    public static Window? LastShown { get; private set; }

    /// <summary>Open tool windows by title: each tool window exists once and a second call brings it forward.</summary>
    private static readonly Dictionary<string, Window> OpenWindows = new();

    /// <summary>Windows that may be opened several times at once (sections along different lines).</summary>
    private static readonly HashSet<string> MultiInstance = new() { "Section view" };

    /// <summary>Brings an open tool window to the front; returns false when it is not open.</summary>
    public static bool ActivateExisting(string title)
    {
        if (!OpenWindows.TryGetValue(title, out var w)) return false;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        LastShown = w;
        return true;
    }

    /// <summary>Closes an open tool window by title (no-op when it is not open).</summary>
    public static void CloseWindow(string title)
    {
        if (OpenWindows.TryGetValue(title, out var w)) w.Close();
    }

    /// <summary>Shows a non-modal tool window, or brings the already open one with the same title to the front.</summary>
    /// <summary>
    /// Shows a tool window: centred on the main window, at its bottom edge (<paramref name="alignBottomToOwner"/>) or at
    /// <paramref name="at"/> (a remembered place, set before it is shown so nothing centres it afterwards).
    /// </summary>
    public static Window ShowWindow(string title, Control content, double width, double height, bool alignBottomToOwner = false, PixelPoint? at = null)
    {
        if (!MultiInstance.Contains(title) && ActivateExisting(title)) return OpenWindows[title];
        UsageLog.Track(UsageLog.Dialog, title);
        var w = new Window
        {
            Title = Loc.T(title),
            Content = content,
            Width = width,
            Height = height,
            WindowStartupLocation = alignBottomToOwner || at != null ? WindowStartupLocation.Manual : WindowStartupLocation.CenterOwner
        };
        if (at is PixelPoint p) w.Position = p;
        LastShown = w;
        if (!MultiInstance.Contains(title))
        {
            OpenWindows[title] = w;
            w.Closed += (_, _) => { if (OpenWindows.TryGetValue(title, out var open) && open == w) OpenWindows.Remove(title); };
        }
        if (Owner != null) w.Show(Owner); else w.Show();
        if (at == null && alignBottomToOwner && Owner is { } owner)
        {
            var scale = owner.RenderScaling;
            w.Position = new PixelPoint(
                owner.Position.X + (int)Math.Round((owner.Bounds.Width - w.Width) * scale / 2),
                owner.Position.Y + (int)Math.Round((owner.Bounds.Height - w.Height) * scale));
        }
        return w;
    }
}
