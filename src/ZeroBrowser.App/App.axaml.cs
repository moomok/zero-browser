using ZeroBrowser.App.ViewModels;
using ZeroBrowser.Browser;
using ZeroBrowser.Core.Fingerprint;
using ZeroBrowser.Storage.Sqlite;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ZeroBrowser.App;

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
            // App data root: %LOCALAPPDATA%\ZeroBrowser on Windows,
            // ~/.local/share/ZeroBrowser on Linux, ~/Library/Application Support/ZeroBrowser on macOS.
            var appDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZeroBrowser");
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
