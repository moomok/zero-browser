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

    public FingerprintProfile Generate(string seed)
    {
        if (string.IsNullOrWhiteSpace(seed))
            throw new ArgumentException("seed must be non-empty", nameof(seed));

        var rnd = new SeededRandom(seed);

        // OS — pinned if user requested it, otherwise pick from allowed set.
        var os = _options.PinnedOs ?? rnd.Pick(_options.AllowedOperatingSystems);

        // Browser version
        var browserVersion = rnd.Pick(FingerprintDataset.ChromeVersions);
        var majorVersion = FingerprintDataset.MajorOf(browserVersion);

        // Platform strings
        var (platform, secChUaPlatform, osCpu, uaOs) = FingerprintDataset.GetPlatformStrings(os);

        // User agent — Chrome's UA stays "Mozilla/5.0 (...) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/<v> Safari/537.36"
        var ua = $"Mozilla/5.0 ({uaOs}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{browserVersion} Safari/537.36";

        // Sec-CH-UA: brand list with the right grease entry shape that Chrome uses.
        // Chrome 126+ format uses three brand entries: Not/A)Brand, Chromium, Google Chrome
        var secChUa =
            $"\"Not/A)Brand\";v=\"8\", \"Chromium\";v=\"{majorVersion}\", \"Google Chrome\";v=\"{majorVersion}\"";

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
