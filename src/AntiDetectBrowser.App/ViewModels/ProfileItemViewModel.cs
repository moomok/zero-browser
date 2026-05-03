using AntiDetectBrowser.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AntiDetectBrowser.App.ViewModels;

public sealed partial class ProfileItemViewModel(Profile profile, FingerprintProfile fingerprint) : ObservableObject
{
    public Profile Profile { get; } = profile;
    public FingerprintProfile Fingerprint { get; } = fingerprint;

    public string Name        => Profile.Name;
    public string Os          => Fingerprint.Os.ToString();
    public string Browser     => $"Chrome {Fingerprint.BrowserVersion.Split('.')[0]}";
    public string Resolution  => $"{Fingerprint.ScreenWidth}×{Fingerprint.ScreenHeight}";
    public string Timezone    => Fingerprint.Timezone;
    public string Language    => Fingerprint.PrimaryLanguage;
    public string LastUsed    => Profile.LastUsedAt?.LocalDateTime.ToString("g") ?? "—";

    [ObservableProperty] private string _status = "idle";
}
