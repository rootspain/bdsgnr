using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Markup.Xaml;
using BlastDesigner.App.Services;
using BlastDesigner.App.Views;
using BlastDesigner.Domain.Model;
using BlastDesigner.IO;

namespace BlastDesigner.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = ProgramSettings.Load();
        Loc.Language = settings.Language;
        Look.ApplyTheme(settings.IsLight, settings.AccentColor);
        // ↑/↓ add or take away 1 in every number field
        NumberStep.Attach();
        // Esc closes every dialog and tool window as Cancel; the main window keeps it for the tools. What uses Esc itself
        // (an open dropdown, a search box, a window's own Esc handling) marks it handled first, and this handler skips it.
        InputElement.KeyDownEvent.AddClassHandler<Window>((w, e) =>
        {
            if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || w is MainWindow) return;
            e.Handled = true;
            // the window's own Cancel button when it has one, so Esc does what Cancel does
            var cancel = w.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsCancel && b.IsEffectivelyVisible && b.IsEffectivelyEnabled);
            if (cancel != null) cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            else w.Close();
        });
        // A tool window with nothing focused (Quantities, Dashboard) would leave the keys with the control focused in the
        // main window, and Esc would reach the plan instead of closing it: such a window takes the focus itself.
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) =>
        {
            // the focus of the program is one: left on the plan it is not null, and only the window it lies in counts
            if (w is MainWindow || (w.FocusManager?.GetFocusedElement() as Visual)?.GetVisualRoot() == w) return;
            w.Focusable = true;
            w.Focus();
        });
        Engine.Charging.MinStemmingDiameters = settings.MinStemmingDiameters;
        SizeUnits.Default = settings.FragmentSizeUnit;
        Log.Info("Application started");
        if (settings.UsageLogging) UsageLog.Start(Changelog.Current.ToString());
        // what is still in memory reaches the file also when the program fails
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("Fatal error", e.ExceptionObject as Exception);
            UsageLog.Flush();
        };
        // An error in a command (most run as async void) would otherwise close the program with the work not saved.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Dialogs.ShowFailure(e.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Dialogs.ShowFailure(e.Exception.InnerException ?? e.Exception);
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(settings);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => UsageLog.Stop();
            var file = desktop.Args?.FirstOrDefault(a => File.Exists(a));
            if (file != null) window.OpenOnStartup = file;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
