using ZeroBrowser.Core.Models;
using ZeroBrowser.Core.Util;

namespace ZeroBrowser.Core.Fingerprint;

/// <summary>
/// Deterministically builds a complete <see cref="FingerprintProfile"/> from a string seed.
/// The same seed always produces the same fingerprint — across machines, processes, and OSes.
/// Combinations are constrained to realistic ones (e.g. macOS UA never paired with Windows fonts).
/// </summary>
public sealed class FingerprintGenerator
{
    private readonly FingerprintGeneratorOptions _options;

    public FingerprintGenerator(FingerprintGeneratorOptions? options = null)
    {
        _options = options ?? FingerprintGeneratorOptions.Default;
    }

    public FingerprintProfile Generate(string seed) => Generate(seed, _options.PinnedOs, tlsMode: null);

    /// <summary>Generate a fingerprint, optionally overriding OS pin (used by per-profile editor).</summary>
    public FingerprintProfile Generate(string seed, OperatingSystemKind? pinnedOs)
        => Generate(seed, pinnedOs, tlsMode: null);

    /// <summary>
    /// Generate a fingerprint with full pinning control: OS + TLS template mode.
    /// When <paramref name="tlsMode"/> is set to a non-none value that maps to
    /// a uTLS template (chrome/firefox/safari/edge), the browser version in the
    /// UA and Sec-CH-UA is forced to match the template version — otherwise an
    /// attacker can correlate "UA says Chrome 150 but JA3 fingerprint matches
    /// Chrome 102" as a single high-confidence detection signal.
    /// </summary>
    public FingerprintProfile Generate(string seed, OperatingSystemKind? pinnedOs, string? tlsMode)
    {
        if (string.IsNullOrWhiteSpace(seed))
            throw new ArgumentException("seed must be non-empty", nameof(seed));

        var rnd = new SeededRandom(seed);

        // OS — pinned if requested, otherwise pick from allowed set.
        var os = pinnedOs ?? _options.PinnedOs ?? rnd.Pick(_options.AllowedOperatingSystems);

        // Browser version — TLS pin takes precedence over random pick. If a
        // TLS template is selected, the UA/Sec-CH-UA must report the EXACT
        // version that template was captured from, otherwise the fingerprint
        // looks incoherent (UA version vs TLS ClientHello structure disagree).
        var tlsPinnedVersion = FingerprintDataset.VersionForTlsMode(tlsMode);
        string browserVersion;
        string browserFamily; // "chrome" | "firefox" | "safari" | "edge"
        if (tlsPinnedVersion is not null)
        {
            browserVersion = tlsPinnedVersion;
            browserFamily = tlsMode!.ToLowerInvariant();
        }
        else
        {
            // No TLS pin: pick family + version pseudo-randomly (legacy behavior).
            browserFamily = "chrome";
            browserVersion = rnd.Pick(FingerprintDataset.ChromeVersions);
        }

        var majorVersion = FingerprintDataset.MajorOf(browserVersion);
        var majorVersionInt = int.Parse(majorVersion);

        // Platform strings
        var (platform, secChUaPlatform, osCpu, uaOs) = FingerprintDataset.GetPlatformStrings(os);

        // User agent — format depends on browser family. The shape and the
        // exact version token must both match the captured real-browser UA
        // for the version that the TLS template was captured from.
        string ua = browserFamily switch
        {
            "chrome"  => $"Mozilla/5.0 ({uaOs}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{browserVersion} Safari/537.36",
            "edge"    => $"Mozilla/5.0 ({uaOs}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{browserVersion} Safari/537.36 Edg/{browserVersion}",
            "firefox" => $"Mozilla/5.0 ({uaOs}; rv:{browserVersion}) Gecko/20100101 Firefox/{browserVersion}",
            // Safari UA: "Version/<v> Safari/<safari_build>" where safari_build is
            // the WebKit version, e.g. "605.1.15" for Safari 16.
            "safari"  => $"Mozilla/5.0 ({uaOs}) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/{browserVersion} Safari/605.1.15",
            _         => $"Mozilla/5.0 ({uaOs}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{browserVersion} Safari/537.36"
        };

        // Sec-CH-UA is only meaningful for Chromium-family browsers (Chrome, Edge,
        // Opera). Firefox and Safari don't send it. We still set the field but it
        // won't be sent by Chromium when mode != chrome|edge — Puppeteer only
        // attaches it for Chrome-based UAs.
        string secChUa;
        if (browserFamily is "chrome" or "edge")
        {
            // Grease brand version mapping (Chrome 102 falls into "Not/A)Brand";v="8"
            // bucket — same as Chrome 100/101/106/etc).
            string greaseBrand;
            string greaseVersion;
            if (majorVersionInt >= 145) { greaseBrand = "Not:A-Brand"; greaseVersion = "8"; }
            else if (majorVersionInt >= 135) { greaseBrand = "Not)A;Brand"; greaseVersion = "99"; }
            else if (majorVersionInt >= 131) { greaseBrand = "Not?A_Brand"; greaseVersion = "8"; }
            else { greaseBrand = "Not/A)Brand"; greaseVersion = "8"; }

            if (browserFamily == "edge")
            {
                secChUa =
                    $"\"{greaseBrand}\";v=\"{greaseVersion}\", \"Chromium\";v=\"{majorVersion}\", \"Microsoft Edge\";v=\"{majorVersion}\"";
            }
            else
            {
                secChUa =
                    $"\"{greaseBrand}\";v=\"{greaseVersion}\", \"Chromium\";v=\"{majorVersion}\", \"Google Chrome\";v=\"{majorVersion}\"";
            }
        }
        else
        {
            // Firefox/Safari: empty Sec-CH-UA (browser wouldn't send it).
            secChUa = string.Empty;
        }

        // Hardware
        var hardwareConcurrency = rnd.Pick(FingerprintDataset.HardwareConcurrencyOptions);
        var deviceMemory = rnd.Pick(FingerprintDataset.DeviceMemoryOptions);

        // Screen
        var screen = rnd.Pick(FingerprintDataset.ScreenResolutions[os]);
        var dpr = os == OperatingSystemKind.MacOS ? 2.0 : 1.0;
        // availHeight is screen height minus a sane taskbar/dock; deterministic per-profile within a band
        var taskbar = os switch
        {
            OperatingSystemKind.Windows10 => 40,
            OperatingSystemKind.Windows11 => 48,
            OperatingSystemKind.MacOS     => 25,
            OperatingSystemKind.Linux     => 27,
            _ => 40
        };
        var availWidth = screen.W;
        var availHeight = screen.H - taskbar;

        // Timezone & locale
        var tz = rnd.Pick(FingerprintDataset.Timezones);
        var langs = FingerprintDataset.LanguagesByTimezone[tz.Tz];
        var primaryLanguage = langs[0];
        var acceptLanguage = string.Join(",", langs.Select((l, i) => i == 0 ? l : $"{l};q={Math.Max(0.1, 0.9 - i * 0.1):0.0}"));

        // GPU
        var webgl = rnd.Pick(FingerprintDataset.WebGlCombos[os]);

        // Fonts — start with full OS list, drop a deterministic ~10% to make each profile slightly different
        var allFonts = FingerprintDataset.FontsByOs[os];
        var fonts = allFonts.Where(_ => rnd.NextDouble() > 0.1).ToList();
        if (fonts.Count == 0) fonts = allFonts.Take(10).ToList();

        // Media devices — emulate one mic + one speaker; webcam optional
        var mediaDevices = new List<MediaDeviceInfo>
        {
            new(Hex(rnd.NextUInt(), 32), Hex(rnd.NextUInt(), 32), "audioinput",  "Default - Internal Microphone"),
            new(Hex(rnd.NextUInt(), 32), Hex(rnd.NextUInt(), 32), "audiooutput", "Default - Internal Speakers")
        };
        if (rnd.NextDouble() < 0.7)
        {
            mediaDevices.Add(new MediaDeviceInfo(Hex(rnd.NextUInt(), 32), Hex(rnd.NextUInt(), 32), "videoinput", "FaceTime HD Camera"));
        }

        return new FingerprintProfile
        {
            Seed = seed,
            Os = os,
            OsVersion = uaOs,
            BrowserVersion = browserVersion,
            UserAgent = ua,
            Platform = platform,
            OsCpu = osCpu,
            Vendor = "Google Inc.",
            ProductSub = "20030107",
            SecChUa = secChUa,
            SecChUaPlatform = secChUaPlatform,
            SecChUaMobile = false,

            PrimaryLanguage = primaryLanguage,
            Languages = langs,
            AcceptLanguage = acceptLanguage,

            HardwareConcurrency = hardwareConcurrency,
            DeviceMemoryGb = deviceMemory,
            ScreenWidth = screen.W,
            ScreenHeight = screen.H,
            AvailWidth = availWidth,
            AvailHeight = availHeight,
            ColorDepth = 24,
            DevicePixelRatio = dpr,

            Timezone = tz.Tz,
            TimezoneOffsetMinutes = tz.OffsetMin,
            GeoLatitude = tz.Lat + (rnd.NextDouble() - 0.5) * 0.1,    // jitter ~5km
            GeoLongitude = tz.Lon + (rnd.NextDouble() - 0.5) * 0.1,
            GeoAccuracy = 30 + rnd.NextDouble() * 70,                 // 30-100m

            WebGlVendor = webgl.Vendor,
            WebGlRenderer = webgl.Renderer,
            WebGlVersion = "WebGL 1.0 (OpenGL ES 2.0 Chromium)",
            WebGlShadingLanguageVersion = "WebGL GLSL ES 1.0 (OpenGL ES GLSL ES 1.0 Chromium)",

            CanvasNoiseSeed = rnd.Derive("canvas"),
            AudioNoiseSeed = rnd.Derive("audio"),
            FontNoiseSeed = rnd.Derive("font"),

            Fonts = fonts,
            MediaDevices = mediaDevices,
            WebRtcMode = WebRtcMode.Proxy
        };
    }

    private static string Hex(uint value, int length)
    {
        var rnd = new SeededRandom(value);
        var chars = "0123456789abcdef";
        return new string(Enumerable.Range(0, length).Select(_ => chars[rnd.Next(0, chars.Length)]).ToArray());
    }
}

public sealed record FingerprintGeneratorOptions
{
    /// <summary>If set, generator will always use this OS regardless of seed. Useful for forcing a profile to look like a specific platform.</summary>
    public OperatingSystemKind? PinnedOs { get; init; }

    public IReadOnlyList<OperatingSystemKind> AllowedOperatingSystems { get; init; } =
        new[] { OperatingSystemKind.Windows10, OperatingSystemKind.Windows11, OperatingSystemKind.MacOS };

    public static FingerprintGeneratorOptions Default { get; } = new();
}
