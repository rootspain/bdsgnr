using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    private static bool _failureShown;

    /// <summary>
    /// An error nothing else caught: it goes to the log, and to the user unless such a message is already open (an error
    /// repeated on every layout pass would otherwise open window after window).
    /// </summary>
    public static void ShowFailure(Exception ex)
    {
        Log.Error("Unhandled error", ex);
        if (_failureShown) return;
        _failureShown = true;
        Dispatcher.UIThread.Post(async () =>
        {
            try { await Show("Error", ex.Message, new[] { "OK" }); }
            catch (Exception shown) { Log.Error("Could not show an error", shown); }
            finally { _failureShown = false; }
        });
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
            CanResize = false,
            Background = Look.Ground
        };
        // black until drawn, then the theme: no white flash on opening
        DarkStart.Attach(w, w.Background ?? Look.Ground);
        int result = -1;
        // the main answer is the accented one, at the right end as in the input windows
        var shown = new Button[buttons.Length];
        for (int i = 0; i < buttons.Length; i++)
        {
            var idx = i;
            void Answer() { result = idx; w.Close(); }
            var b = i == 0 ? FormKit.Primary(Loc.T(buttons[i]), Answer) : FormKit.Secondary(Loc.T(buttons[i]), Answer);
            b.IsDefault = i == 0;
            b.IsCancel = i == buttons.Length - 1;
            shown[buttons.Length - 1 - i] = b;
        }
        var footer = FormKit.Footer(null, shown);
        DockPanel.SetDock(footer, Dock.Bottom);
        w.Content = new DockPanel { Children = { footer, new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, Margin = new Thickness(20, 18) } } };
        LastMessage = w;
        if (Owner != null && Owner.IsVisible) await w.ShowDialog(Owner); else w.Show();
        return result;
    }

    public static async Task<string?> Prompt(string title, string label, string value = "")
    {
        var box = FormKit.Text(value);
        box.MinWidth = 320;
        var ok = await Custom(title, FormKit.Labeled(label, box), () => true, box);
        return ok ? box.Text : null;
    }

    public static async Task<double?> PromptNumber(string title, string label, double value)
    {
        var box = new NumericUpDown { Value = (decimal)value, FormatString = "0.###", MinWidth = 320, Increment = 1, Background = Look.Field, BorderBrush = Look.LineStrong };
        var ok = await Custom(title, FormKit.Labeled(label, box), () => box.Value.HasValue, box);
        return ok ? (double?)box.Value : null;
    }

    public static async Task<int?> Choose(string title, string label, IReadOnlyList<string> items, int selected = 0)
    {
        if (items.Count == 0) return null;
        var combo = new ComboBox { ItemsSource = items, SelectedIndex = Math.Clamp(selected, 0, items.Count - 1), MinWidth = 320, MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
        var ok = await Custom(title, FormKit.Labeled(label, combo), () => combo.SelectedIndex >= 0, combo);
        return ok ? combo.SelectedIndex : null;
    }

    /// <summary>
    /// Shows arbitrary content between the title band and the bottom bar. Returns true on OK. A <paramref name="form"/> has fields
    /// that take Enter themselves and is applied with Ctrl+Enter; <paramref name="bare"/> content reaches the edges of the window.
    /// Given only a width the window takes the height of its content.
    /// </summary>
    public static async Task<bool> Custom(string title, Control content, Func<bool>? validate = null, Control? focus = null, double? width = null, double? height = null, string? subtitle = null, bool form = false, bool bare = false, Control? aside = null)
    {
        var w = new Window
        {
            Title = Loc.T(title),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = width == null ? SizeToContent.WidthAndHeight : height == null ? SizeToContent.Height : SizeToContent.Manual,
            MinWidth = 380,
            Background = Look.Ground
        };
        // black until drawn, then the theme: no white flash on opening
        DarkStart.Attach(w, w.Background ?? Look.Ground);
        if (width != null) w.Width = width.Value;
        if (height != null) w.Height = height.Value;
        bool ok = false;
        void Apply()
        {
            // the field being typed in takes its text when it loses focus, and Ctrl+Enter does not take the focus from it
            if (w.FocusManager?.GetFocusedElement() is TextBox box) box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            if (validate?.Invoke() == false) return;
            ok = true;
            w.Close();
        }
        var apply = FormKit.Primary(Loc.T("OK"), Apply);
        apply.IsDefault = !form;
        ToolTip.SetTip(apply, apply.Content + " (Ctrl+Enter)");
        var cancel = FormKit.Secondary(Loc.T("Cancel"), w.Close);
        cancel.IsCancel = true;
        var header = FormKit.Header(Loc.T(title), subtitle, aside);
        var footer = FormKit.Footer(form ? Loc.Ui("Enter — confirm the field, Ctrl+Enter — OK, Esc — cancel") : null, cancel, apply);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        w.Content = new DockPanel { Children = { header, footer, bare ? content : new Border { Padding = new Thickness(16, 14), Child = content } } };
        w.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.Control) return;
            Apply();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
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
        FormLayouts.Apply(grid, typeof(T));
        grid.SetTarget(copy);
        var scroll = new ScrollViewer { Content = grid, MaxHeight = 640, Padding = new Thickness(0, 0, 10, 0) };
        return await Custom(title, scroll, width: 620, form: true) ? copy : null;
    }

    /// <summary>The most recent message window (used by the UI smoke test).</summary>
    public static Window? LastMessage { get; private set; }

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
    /// <summary>
    /// Keeps a window without the system title at one height, its width from <paramref name="minWidth"/> up. The size
    /// limits of the window alone do not do: Windows adds the height of a title to them at the first move or resize, so
    /// the limits go to Windows directly too.
    /// </summary>
    public static void FixHeight(Window w, double minWidth, double height)
    {
        w.MinWidth = minWidth;
        w.MinHeight = w.MaxHeight = height;
        if (w.TryGetPlatformHandle()?.HandleDescriptor != "HWND") return;
        const uint getMinMaxInfo = 0x0024;
        // MINMAXINFO: the smallest size at 24, the largest at 32, x then y
        Win32Properties.AddWndProcHookCallback(w, (IntPtr _, uint message, IntPtr _, IntPtr info, ref bool handled) =>
        {
            if (message != getMinMaxInfo) return IntPtr.Zero;
            var rows = (int)Math.Round(height * w.RenderScaling);
            System.Runtime.InteropServices.Marshal.WriteInt32(info, 24, (int)Math.Round(minWidth * w.RenderScaling));
            System.Runtime.InteropServices.Marshal.WriteInt32(info, 28, rows);
            System.Runtime.InteropServices.Marshal.WriteInt32(info, 36, rows);
            handled = true;
            return IntPtr.Zero;
        });
    }

    public static Window ShowWindow(string title, Control content, double width, double height, bool alignBottomToOwner = false, PixelPoint? at = null, Action<Window>? beforeShow = null)
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
        // black until drawn, then the theme: no white flash on opening
        DarkStart.Attach(w, w.Background ?? Look.Ground);
        if (at is PixelPoint p) w.Position = p;
        LastShown = w;
        if (!MultiInstance.Contains(title))
        {
            OpenWindows[title] = w;
            w.Closed += (_, _) => { if (OpenWindows.TryGetValue(title, out var open) && open == w) OpenWindows.Remove(title); };
        }
        beforeShow?.Invoke(w);
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
