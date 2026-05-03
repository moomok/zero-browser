using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ZeroBrowser.App.ViewModels;
using ZeroBrowser.App.Views;
using ZeroBrowser.Browser;
using ZeroBrowser.Core.Fingerprint;
using ZeroBrowser.Storage.Crypto;
using ZeroBrowser.Storage.Sqlite;

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

            var masterKeyPath = Path.Combine(appDataRoot, "master.key");
            var masterKey = new MasterKey(masterKeyPath);

            // Show unlock window first. Once unlocked, swap in the main window.
            var unlockVm = new UnlockWindowViewModel(masterKey);
            var unlockWindow = new UnlockWindow { DataContext = unlockVm };

            unlockVm.Unlocked += () =>
            {
                var box = unlockVm.UnlockedBox!;
                var dbPath = Path.Combine(appDataRoot, "data.db");
                var profileRepo = new ProfileRepository(dbPath);
                var proxyRepo   = new ProxyRepository(dbPath, box);
                var generator = new FingerprintGenerator();
                var injector = new FingerprintInjector();
                var launcher = new PuppeteerBrowserLauncher(injector);

                var mainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(profileRepo, proxyRepo, generator, launcher)
                };

                desktop.MainWindow = mainWindow;
                mainWindow.Show();
                unlockWindow.Close();
            };

            desktop.MainWindow = unlockWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
