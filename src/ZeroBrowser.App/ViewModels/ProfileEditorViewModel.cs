using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroBrowser.Browser;
using ZeroBrowser.Core.Fingerprint;
using ZeroBrowser.Core.Models;
using ZeroBrowser.Core.Util;
using ZeroBrowser.Storage.Sqlite;

namespace ZeroBrowser.App.ViewModels;

public sealed partial class ProfileEditorViewModel : ObservableObject
{
    private readonly ProfileRepository _profileRepo;
    private readonly FingerprintGenerator _generator;
    private readonly Profile _profile;
    private readonly bool _isNew;

    public string Title => _isNew ? "New profile" : $"Edit profile — {_profile.Name}";

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private string _fingerprintSeed = string.Empty;

    public ObservableCollection<OsOption> OsOptions { get; } = new();
    public ObservableCollection<ProxyOption> ProxyOptions { get; } = new();
    public ObservableCollection<EngineOption> EngineOptions { get; } = new();
    public ObservableCollection<ExtensionItemViewModel> Extensions { get; } = new();

    [ObservableProperty] private OsOption? _selectedOs;
    [ObservableProperty] private ProxyOption? _selectedProxy;
    [ObservableProperty] private EngineOption? _selectedEngine;
    [ObservableProperty] private string _extensionStatusMessage = string.Empty;

    // Live preview values that update as the user changes name / OS / seed.
    [ObservableProperty] private string _previewUserAgent = string.Empty;
    [ObservableProperty] private string _previewTimezone = string.Empty;
    [ObservableProperty] private string _previewScreen = string.Empty;
    [ObservableProperty] private string _previewGpu = string.Empty;
    [ObservableProperty] private string _previewLanguage = string.Empty;

    public event Action<Profile>? Saved;
    public event Action? Cancelled;

    public ProfileEditorViewModel(
        ProfileRepository profileRepo,
        ProxyRepository proxyRepo,
        FingerprintGenerator generator,
        Profile? existing = null)
    {
        _profileRepo = profileRepo;
        _generator   = generator;
        _isNew       = existing is null;

        OsOptions.Add(new OsOption(null, "Random (per-seed)"));
        OsOptions.Add(new OsOption(OperatingSystemKind.Windows10, "Windows 10"));
        OsOptions.Add(new OsOption(OperatingSystemKind.Windows11, "Windows 11"));
        OsOptions.Add(new OsOption(OperatingSystemKind.MacOS,     "macOS"));
        OsOptions.Add(new OsOption(OperatingSystemKind.Linux,     "Linux"));

        ProxyOptions.Add(new ProxyOption(null, "(none — direct connection)"));
        foreach (var p in proxyRepo.ListAll())
            ProxyOptions.Add(new ProxyOption(p.Id, $"{p.Type.ToString().ToLowerInvariant()}://{p.Host}:{p.Port}"));

        // Engine options: bundled Chromium-for-Testing first (always available),
        // then any branded Chromium-based browser detected on the host system.
        EngineOptions.Add(new EngineOption(null, "Chromium for Testing (default — deterministic, no Web Store)"));
        foreach (var b in BrowserDetector.Detect())
            EngineOptions.Add(new EngineOption(b.Path, $"{b.DisplayName} — {b.Path}"));

        if (existing is null)
        {
            var id = Guid.NewGuid();
            var dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZeroBrowser", "profiles", id.ToString());
            _profile = new Profile
            {
                Id = id,
                Name = $"Profile {DateTime.Now:HH:mm:ss}",
                FingerprintSeed = Guid.NewGuid().ToString("N"),
                StoragePath = dataRoot
            };
        }
        else
        {
            _profile = existing;
        }

        Name             = _profile.Name;
        Notes            = _profile.Notes ?? string.Empty;
        FingerprintSeed  = _profile.FingerprintSeed;
        SelectedOs       = OsOptions.FirstOrDefault(o => o.Os == _profile.PinnedOs) ?? OsOptions[0];
        SelectedProxy    = ProxyOptions.FirstOrDefault(p => p.Id == _profile.ProxyId) ?? ProxyOptions[0];
        SelectedEngine   = EngineOptions.FirstOrDefault(e => string.Equals(e.Path, _profile.EnginePath, StringComparison.OrdinalIgnoreCase))
                           ?? EngineOptions[0];

        if (!_isNew)
        {
            foreach (var ext in _profileRepo.ListExtensions(_profile.Id))
                Extensions.Add(new ExtensionItemViewModel(ext));
        }

        UpdatePreview();
    }

    partial void OnFingerprintSeedChanged(string value) => UpdatePreview();
    partial void OnSelectedOsChanged(OsOption? value)   => UpdatePreview();

    private void UpdatePreview()
    {
        if (string.IsNullOrWhiteSpace(FingerprintSeed))
        {
            PreviewUserAgent = "(seed empty)";
            PreviewTimezone = PreviewScreen = PreviewGpu = PreviewLanguage = string.Empty;
            return;
        }
        try
        {
            var fp = _generator.Generate(FingerprintSeed, SelectedOs?.Os);
            PreviewUserAgent = fp.UserAgent;
            // TimezoneOffsetMinutes follows JS getTimezoneOffset() — positive = west of UTC, so display sign is inverted.
            PreviewTimezone  = $"{fp.Timezone} (UTC{(fp.TimezoneOffsetMinutes <= 0 ? "+" : "-")}{Math.Abs(fp.TimezoneOffsetMinutes) / 60:00}:{Math.Abs(fp.TimezoneOffsetMinutes) % 60:00})";
            PreviewScreen    = $"{fp.ScreenWidth} × {fp.ScreenHeight} @ {fp.DevicePixelRatio:F1}x";
            PreviewGpu       = $"{fp.WebGlVendor} — {fp.WebGlRenderer}";
            PreviewLanguage  = $"{fp.PrimaryLanguage} ({string.Join(", ", fp.Languages)})";
        }
        catch
        {
            PreviewUserAgent = "(invalid seed)";
        }
    }

    [RelayCommand]
    private void Regenerate()
    {
        FingerprintSeed = Guid.NewGuid().ToString("N");
        // OnFingerprintSeedChanged will fire UpdatePreview()
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name)) return;
        if (string.IsNullOrWhiteSpace(FingerprintSeed)) return;

        _profile.Name = Name.Trim();
        _profile.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();
        _profile.FingerprintSeed = FingerprintSeed.Trim();
        _profile.PinnedOs = SelectedOs?.Os;
        _profile.ProxyId = SelectedProxy?.Id;
        _profile.EnginePath = SelectedEngine?.Path;

        if (_isNew) _profileRepo.Insert(_profile);
        else        _profileRepo.Update(_profile);

        // Persist extension list. Compare current UI list against the stored
        // set so we only touch rows that actually changed; this also lets us
        // delete extensions the user removed without dropping the whole table.
        var stored = _profileRepo.ListExtensions(_profile.Id).ToDictionary(e => e.Id);
        var seen   = new HashSet<Guid>();
        for (var i = 0; i < Extensions.Count; i++)
        {
            var item = Extensions[i];
            item.Model.SortOrder = i;
            seen.Add(item.Model.Id);
            if (stored.ContainsKey(item.Model.Id))
                _profileRepo.UpdateExtension(item.Model);
            else
                _profileRepo.InsertExtension(item.Model);
        }
        foreach (var (id, _) in stored)
            if (!seen.Contains(id))
                _profileRepo.DeleteExtension(id);

        Saved?.Invoke(_profile);
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();

    [RelayCommand]
    private async Task AddExtensionFolderAsync()
    {
        var topLevel = ResolveTopLevel();
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select an unpacked extension folder (must contain manifest.json)",
            AllowMultiple = false
        });

        if (folders is null || folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        var manifest = Path.Combine(path, "manifest.json");
        if (!File.Exists(manifest))
        {
            ExtensionStatusMessage = "Folder does not contain manifest.json.";
            return;
        }

        var name  = CrxImporter.ReadManifestName(path);
        var model = new ProfileExtension
        {
            Id        = Guid.NewGuid(),
            ProfileId = _profile.Id,
            Name      = name,
            Path      = path,
            Enabled   = true,
            SortOrder = Extensions.Count
        };
        Extensions.Add(new ExtensionItemViewModel(model));
        ExtensionStatusMessage = $"Added '{name}'.";
    }

    [RelayCommand]
    private async Task AddExtensionCrxAsync()
    {
        var topLevel = ResolveTopLevel();
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a Chromium .crx package",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Chromium extension (*.crx)") { Patterns = ["*.crx"] },
                FilePickerFileTypes.All
            ]
        });

        if (files is null || files.Count == 0) return;
        var crxPath = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(crxPath) || !File.Exists(crxPath)) return;

        var destRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZeroBrowser", "extensions", _profile.Id.ToString(), Guid.NewGuid().ToString("N"));

        try
        {
            CrxImporter.Extract(crxPath, destRoot);
        }
        catch (Exception ex)
        {
            ExtensionStatusMessage = $"Failed to import CRX: {ex.Message}";
            return;
        }

        var name  = CrxImporter.ReadManifestName(destRoot);
        var model = new ProfileExtension
        {
            Id        = Guid.NewGuid(),
            ProfileId = _profile.Id,
            Name      = name,
            Path      = destRoot,
            Enabled   = true,
            SortOrder = Extensions.Count
        };
        Extensions.Add(new ExtensionItemViewModel(model));
        ExtensionStatusMessage = $"Imported '{name}' from CRX.";
    }

    [RelayCommand]
    private void RemoveExtension(ExtensionItemViewModel? item)
    {
        if (item is null) return;
        Extensions.Remove(item);
        ExtensionStatusMessage = $"Removed '{item.DisplayName}'.";
    }

    private static TopLevel? ResolveTopLevel()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
        }
        return null;
    }
}

public sealed record OsOption(OperatingSystemKind? Os, string Display);
public sealed record ProxyOption(Guid? Id, string Display);
public sealed record EngineOption(string? Path, string Display);

public sealed partial class ExtensionItemViewModel : ObservableObject
{
    public ProfileExtension Model { get; }

    public ExtensionItemViewModel(ProfileExtension model)
    {
        Model = model;
        _name    = model.Name;
        _path    = model.Path;
        _enabled = model.Enabled;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _path;
    [ObservableProperty] private bool   _enabled;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "(unnamed extension)" : Name;

    partial void OnNameChanged(string value)    => Model.Name = value;
    partial void OnPathChanged(string value)    => Model.Path = value;
    partial void OnEnabledChanged(bool value)   => Model.Enabled = value;
}
