namespace AntiDetectBrowser.Core.Models;

/// <summary>
/// Complete browser fingerprint blueprint for a single profile.
/// All values are deterministic from <see cref="Seed"/> so the same profile
/// always yields the same fingerprint across sessions.
/// </summary>
public sealed record FingerprintProfile
{
    public required string Seed { get; init; }

    // Identity
    public required OperatingSystemKind Os { get; init; }
    public required string OsVersion { get; init; }              // e.g. "Windows NT 10.0; Win64; x64"
    public required string BrowserVersion { get; init; }         // e.g. "126.0.6478.127"
    public required string UserAgent { get; init; }
    public required string Platform { get; init; }               // navigator.platform
    public required string OsCpu { get; init; }                  // navigator.oscpu (Firefox-style; null on Chrome usually)
    public required string Vendor { get; init; }                 // navigator.vendor
    public required string ProductSub { get; init; }
    public required string SecChUa { get; init; }                // header value
    public required string SecChUaPlatform { get; init; }        // "Windows" / "macOS" / "Linux"
    public required bool   SecChUaMobile { get; init; }

    // Locale
    public required string PrimaryLanguage { get; init; }        // e.g. "en-US"
    public required IReadOnlyList<string> Languages { get; init; }
    public required string AcceptLanguage { get; init; }

    // Hardware
    public required int    HardwareConcurrency { get; init; }
    public required int    DeviceMemoryGb { get; init; }
    public required int    ScreenWidth { get; init; }
    public required int    ScreenHeight { get; init; }
    public required int    AvailWidth { get; init; }
    public required int    AvailHeight { get; init; }
    public required int    ColorDepth { get; init; }
    public required double DevicePixelRatio { get; init; }

    // Geo / TZ
    public required string Timezone { get; init; }               // e.g. "Asia/Jakarta"
    public required int    TimezoneOffsetMinutes { get; init; }
    public required double GeoLatitude { get; init; }
    public required double GeoLongitude { get; init; }
    public required double GeoAccuracy { get; init; }

    // GPU
    public required string WebGlVendor { get; init; }            // UNMASKED_VENDOR_WEBGL
    public required string WebGlRenderer { get; init; }          // UNMASKED_RENDERER_WEBGL
    public required string WebGlVersion { get; init; }
    public required string WebGlShadingLanguageVersion { get; init; }

    // Noise seeds (deterministic per-property randomness)
    public required uint CanvasNoiseSeed { get; init; }
    public required uint AudioNoiseSeed { get; init; }
    public required uint FontNoiseSeed { get; init; }

    // Misc
    public required IReadOnlyList<string> Fonts { get; init; }
    public required IReadOnlyList<MediaDeviceInfo> MediaDevices { get; init; }
    public required WebRtcMode WebRtcMode { get; init; }
}

public sealed record MediaDeviceInfo(string DeviceId, string GroupId, string Kind, string Label);
