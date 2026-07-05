using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using ZeroBrowser.App.Views;
using ZeroBrowser.Browser;
using ZeroBrowser.Core.Fingerprint;
using ZeroBrowser.Core.Models;
using ZeroBrowser.Storage.Sqlite;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ZeroBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ProfileRepository _profiles;
    private readonly ProxyRepository _proxies;
    private readonly FingerprintGenerator _generator;
    private readonly IBrowserLauncher _launcher;

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = new();

    [ObservableProperty] private string _statusMessage = "Ready";

    public MainWindowViewModel(ProfileRepository profiles, ProxyRepository proxies, FingerprintGenerator generator, IBrowserLauncher launcher)
    {
        _profiles  = profiles;
        _proxies   = proxies;
        _generator = generator;
        _launcher  = launcher;
        // Fire-and-forget initial load; OK because we set StatusMessage on completion.
        _ = ReloadAsync();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        StatusMessage = "Loading profiles…";
        var items = await Task.Run(() =>
        {
            var all = _profiles.ListAll();
            return all.Select(p =>
            {
                var fp = _generator.Generate(p.FingerprintSeed, p.PinnedOs);
                return new ProfileItemViewModel(p, fp);
            }).ToList();
        });
        Profiles.Clear();
        foreach (var item in items)
            Profiles.Add(item);
        StatusMessage = $"{Profiles.Count} profile(s) loaded";
    }

    [RelayCommand]
    private async Task NewProfileAsync() => await OpenEditorAsync(null);

    [RelayCommand]
    private async Task EditProfileAsync(ProfileItemViewModel? item) => await OpenEditorAsync(item?.Profile);

    private async Task OpenEditorAsync(Profile? existing)
    {
        var vm = await Task.Run(() => new ProfileEditorViewModel(_profiles, _proxies, _generator, existing));
        var window = new ProfileEditorWindow { DataContext = vm };
        vm.Saved     += saved => { _ = ReloadAsync(); };
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
    }

    [RelayCommand]
    private async Task LaunchAsync(ProfileItemViewModel? item)
    {
        if (item is null) return;
        item.Status = "launching…";
        StatusMessage = $"Launching {item.Name}…";
        try
        {
            // Auto-rotate fingerprint seed if rotation is enabled and interval has elapsed.
            var profile = item.Profile;
            if (profile.RotationIntervalDays > 0)
            {
                var anchor = profile.LastRotatedAt ?? profile.CreatedAt;
                if (DateTimeOffset.UtcNow - anchor >= TimeSpan.FromDays(profile.RotationIntervalDays))
                {
                    // Archive current seed before rotating.
                    _profiles.InsertSeedHistory(new SeedHistoryEntry
                    {
                        Id        = Guid.NewGuid(),
                        ProfileId = profile.Id,
                        Seed      = profile.FingerprintSeed,
                        Label     = $"Auto-rotated {DateTime.Now:yyyy-MM-dd HH:mm}"
                    });
                    profile.FingerprintSeed = Guid.NewGuid().ToString("N");
                    profile.LastRotatedAt   = DateTimeOffset.UtcNow;
                    _profiles.Update(profile);
                    StatusMessage = $"Rotated fingerprint for {item.Name}";
                }
            }

            var fp = _generator.Generate(profile.FingerprintSeed, profile.PinnedOs);
            var proxy = profile.ProxyId is { } id ? _proxies.Get(id) : null;
            var extensions = _profiles.ListExtensions(profile.Id);
            var session = await _launcher.LaunchAsync(new LaunchRequest(
                profile,
                fp,
                Proxy: proxy,
                StartUrl: "https://abrahamjuliot.github.io/creepjs/",
                Headless: false,
                Extensions: extensions));
            item.Status = session.IsRunning ? "running" : "exited";
            StatusMessage = $"Launched {item.Name}";

            // Persist last-used
            profile.LastUsedAt = DateTimeOffset.UtcNow;
            _profiles.Update(profile);
        }
        catch (Exception ex)
        {
            item.Status = "error";
            StatusMessage = $"Failed to launch {item.Name}: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task BatchCreateAsync()
    {
        var countStr = "10";
        // Default: create 10 profiles. In a real app this would be a dialog;
        // for now we expose a simple command that creates N profiles at once.
        if (!int.TryParse(countStr, out var count) || count < 1) count = 10;
        count = Math.Min(count, 100); // safety cap

        StatusMessage = $"Creating {count} profiles…";
        await Task.Run(() =>
        {
            for (var i = 0; i < count; i++)
            {
                var id = Guid.NewGuid();
                var dataRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ZeroBrowser", "profiles", id.ToString());
                var profile = new Profile
                {
                    Id              = id,
                    Name            = $"Profile {i + 1} — {DateTime.Now:HH:mm:ss}",
                    FingerprintSeed = Guid.NewGuid().ToString("N"),
                    StoragePath     = dataRoot
                };
                _profiles.Insert(profile);
            }
        });
        await ReloadAsync();
        StatusMessage = $"Created {count} new profiles";
    }

    [RelayCommand]
    private void PreviewFingerprint(ProfileItemViewModel? item)
    {
        if (item is null) return;
        var window = new FingerprintPreviewWindow
        {
            DataContext = new FingerprintPreviewViewModel(item.Name, item.Fingerprint)
        };

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(ProfileItemViewModel? item)
    {
        if (item is null) return;
        _profiles.Delete(item.Profile.Id);
        await ReloadAsync();
    }

    [RelayCommand]
    private void ImportCookies(ProfileItemViewModel? item)
    {
        if (item is null) return;
        var window = new CookieImportWindow
        {
            DataContext = new CookieImportViewModel(item.Profile)
        };
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
    }

    [RelayCommand]
    private void OpenProxyManager()
    {
        var window = new ProxyManagerWindow
        {
            DataContext = new ProxyManagerViewModel(_proxies)
        };
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
    }
}
