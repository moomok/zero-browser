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
        Reload();
    }

    [RelayCommand]
    private void Reload()
    {
        Profiles.Clear();
        foreach (var p in _profiles.ListAll())
        {
            var fp = _generator.Generate(p.FingerprintSeed);
            Profiles.Add(new ProfileItemViewModel(p, fp));
        }
        StatusMessage = $"{Profiles.Count} profile(s) loaded";
    }

    [RelayCommand]
    private void NewProfile()
    {
        var seed = Guid.NewGuid().ToString("N");
        var id   = Guid.NewGuid();
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZeroBrowser", "profiles", id.ToString());
        var profile = new Profile
        {
            Id = id,
            Name = $"Profile {DateTime.Now:HH:mm:ss}",
            FingerprintSeed = seed,
            StoragePath = dataRoot
        };
        _profiles.Insert(profile);
        Reload();
    }

    [RelayCommand]
    private async Task LaunchAsync(ProfileItemViewModel? item)
    {
        if (item is null) return;
        item.Status = "launching…";
        StatusMessage = $"Launching {item.Name}…";
        try
        {
            var proxy = item.Profile.ProxyId is { } id ? _proxies.Get(id) : null;
            var session = await _launcher.LaunchAsync(new LaunchRequest(
                item.Profile,
                item.Fingerprint,
                Proxy: proxy,
                StartUrl: "https://abrahamjuliot.github.io/creepjs/",
                Headless: false));
            item.Status = session.IsRunning ? "running" : "exited";
            StatusMessage = $"Launched {item.Name}";

            // Persist last-used
            item.Profile.LastUsedAt = DateTimeOffset.UtcNow;
            _profiles.Update(item.Profile);
        }
        catch (Exception ex)
        {
            item.Status = "error";
            StatusMessage = $"Failed to launch {item.Name}: {ex.Message}";
        }
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
    private void DeleteProfile(ProfileItemViewModel? item)
    {
        if (item is null) return;
        _profiles.Delete(item.Profile.Id);
        Reload();
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
