using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BlastDesigner.App.Services;
using BlastDesigner.App.Views;
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
        Log.Info("Application started");
        if (settings.UsageLogging) UsageLog.Start(Changelog.Current.ToString());
        // what is still in memory reaches the file also when the program fails
        AppDomain.CurrentDomain.UnhandledException += (_, _) => UsageLog.Flush();
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
