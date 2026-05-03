using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ZeroBrowser.Core.Models;

namespace ZeroBrowser.App.ViewModels;

/// <summary>Read-only summary of a fingerprint, grouped by category for a preview dialog.</summary>
public sealed class FingerprintPreviewViewModel : ObservableObject
{
    public string Title { get; }
    public ObservableCollection<FingerprintGroup> Groups { get; } = new();

    public FingerprintPreviewViewModel(string profileName, FingerprintProfile fp)
    {
        Title = $"Fingerprint preview — {profileName}";

        Groups.Add(new FingerprintGroup("Identity", new()
        {
            new("OS",                 $"{fp.Os} ({fp.OsVersion})"),
            new("Chrome version",     fp.BrowserVersion),
            new("User-Agent",         fp.UserAgent),
            new("Sec-CH-UA",          fp.SecChUa),
            new("Sec-CH-UA-Platform", fp.SecChUaPlatform),
            new("navigator.platform", fp.Platform),
            new("navigator.vendor",   fp.Vendor),
            new("Mobile?",            fp.SecChUaMobile ? "yes" : "no"),
        }));

        Groups.Add(new FingerprintGroup("Locale & Time", new()
        {
            new("Primary language", fp.PrimaryLanguage),
            new("All languages",    string.Join(", ", fp.Languages)),
            new("Accept-Language",  fp.AcceptLanguage),
            // TimezoneOffsetMinutes follows JS getTimezoneOffset() — positive = west of UTC.
            // Display sign is therefore inverted from the field.
            new("Timezone",         $"{fp.Timezone}  (UTC{(fp.TimezoneOffsetMinutes <= 0 ? "+" : "-")}{Math.Abs(fp.TimezoneOffsetMinutes) / 60:00}:{Math.Abs(fp.TimezoneOffsetMinutes) % 60:00})"),
            new("Geolocation",      $"{fp.GeoLatitude:F4}, {fp.GeoLongitude:F4} (±{fp.GeoAccuracy:F0}m)"),
        }));

        Groups.Add(new FingerprintGroup("Hardware", new()
        {
            new("CPU cores",        fp.HardwareConcurrency.ToString()),
            new("RAM (GB)",         fp.DeviceMemoryGb.ToString()),
            new("Screen resolution", $"{fp.ScreenWidth} × {fp.ScreenHeight}"),
            new("Available area",   $"{fp.AvailWidth} × {fp.AvailHeight}"),
            new("Color depth",      $"{fp.ColorDepth}-bit"),
            new("Device pixel ratio", fp.DevicePixelRatio.ToString("F2")),
        }));

        Groups.Add(new FingerprintGroup("GPU (WebGL)", new()
        {
            new("Unmasked vendor",   fp.WebGlVendor),
            new("Unmasked renderer", fp.WebGlRenderer),
            new("WebGL version",     fp.WebGlVersion),
            new("GLSL version",      fp.WebGlShadingLanguageVersion),
        }));

        Groups.Add(new FingerprintGroup("Noise seeds (deterministic)", new()
        {
            new("Canvas seed", $"0x{fp.CanvasNoiseSeed:X8}"),
            new("Audio seed",  $"0x{fp.AudioNoiseSeed:X8}"),
            new("Font seed",   $"0x{fp.FontNoiseSeed:X8}"),
        }));

        var camera = fp.MediaDevices.Count(d => d.Kind == "videoinput");
        var mic    = fp.MediaDevices.Count(d => d.Kind == "audioinput");
        var spk    = fp.MediaDevices.Count(d => d.Kind == "audiooutput");
        Groups.Add(new FingerprintGroup("Other", new()
        {
            new("Fonts available",  $"{fp.Fonts.Count} fonts"),
            new("Media devices",    $"{camera} camera, {mic} microphone, {spk} speaker"),
            new("WebRTC mode",      fp.WebRtcMode.ToString()),
            new("Seed (raw)",       fp.Seed),
        }));
    }
}

public sealed record FingerprintGroup(string Header, List<FingerprintRow> Rows);
public sealed record FingerprintRow(string Label, string Value);
