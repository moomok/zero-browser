using AntiDetectBrowser.App.ViewModels;
using AntiDetectBrowser.Browser;
using AntiDetectBrowser.Core.Fingerprint;
using AntiDetectBrowser.Storage.Sqlite;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AntiDetectBrowser.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // App data root: %LOCALAPPDATA%\AntiDetectBrowser on Windows,
            // ~/.local/share/AntiDetectBrowser on Linux, ~/Library/Application Support/AntiDetectBrowser on macOS.
            var appDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AntiDetectBrowser");
            Directory.CreateDirectory(appDataRoot);

            var dbPath = Path.Combine(appDataRoot, "data.db");
            var profileRepo = new ProfileRepository(dbPath);
            var generator = new FingerprintGenerator();
            var injector = new FingerprintInjector();
            var launcher = new PuppeteerBrowserLauncher(injector);

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(profileRepo, generator, launcher)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
