using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroBrowser.Core.Fingerprint;
using ZeroBrowser.Core.Models;
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

    [ObservableProperty] private OsOption? _selectedOs;
    [ObservableProperty] private ProxyOption? _selectedProxy;

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
            PreviewTimezone  = $"{fp.Timezone} (UTC{(fp.TimezoneOffsetMinutes >= 0 ? "+" : "-")}{Math.Abs(fp.TimezoneOffsetMinutes) / 60:00}:{Math.Abs(fp.TimezoneOffsetMinutes) % 60:00})";
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

        if (_isNew) _profileRepo.Insert(_profile);
        else        _profileRepo.Update(_profile);

        Saved?.Invoke(_profile);
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();
}

public sealed record OsOption(OperatingSystemKind? Os, string Display);
public sealed record ProxyOption(Guid? Id, string Display);
