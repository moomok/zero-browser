using AntiDetectBrowser.Core.Models;

namespace AntiDetectBrowser.Core.Fingerprint;

/// <summary>
/// Curated, realistic combinations of OS / browser version / GPU / fonts / locale.
/// Keep this dataset up-to-date as Chrome versions change.
/// Sources: official Chrome release notes, public fingerprint research datasets,
/// Sec-CH-UA documentation, MDN.
/// </summary>
internal static class FingerprintDataset
{
    /// <summary>Recent stable Chromium milestone versions (full version strings).</summary>
    public static readonly string[] ChromeVersions =
    {
        "126.0.6478.127",
        "126.0.6478.183",
        "127.0.6533.73",
        "127.0.6533.100",
        "128.0.6613.84",
        "128.0.6613.114",
        "129.0.6668.58",
        "129.0.6668.100",
        "130.0.6723.58",
        "130.0.6723.91"
    };

    public static string MajorOf(string fullVersion) =>
        fullVersion.Split('.')[0];

    /// <summary>Realistic screen resolutions per OS family.</summary>
    public static readonly Dictionary<OperatingSystemKind, (int W, int H)[]> ScreenResolutions = new()
    {
        [OperatingSystemKind.Windows10] = new[]
        {
            (1920, 1080), (1366, 768), (1536, 864), (1440, 900),
            (1600, 900), (2560, 1440), (1680, 1050)
        },
        [OperatingSystemKind.Windows11] = new[]
        {
            (1920, 1080), (2560, 1440), (3840, 2160), (1536, 864),
            (1680, 1050), (1440, 900)
        },
        [OperatingSystemKind.MacOS] = new[]
        {
            (1440, 900), (1680, 1050), (1920, 1080), (2560, 1600),
            (2880, 1800), (3024, 1964), (3456, 2234)
        },
        [OperatingSystemKind.Linux] = new[]
        {
            (1920, 1080), (1366, 768), (2560, 1440), (1680, 1050)
        }
    };

    /// <summary>Realistic CPU core counts (weighted toward common values).</summary>
    public static readonly int[] HardwareConcurrencyOptions = { 4, 4, 4, 8, 8, 8, 8, 12, 12, 16 };

    /// <summary>Realistic device memory values (Chrome only exposes specific buckets: 0.25, 0.5, 1, 2, 4, 8).</summary>
    public static readonly int[] DeviceMemoryOptions = { 4, 8, 8, 8, 16 };

    /// <summary>Common GPU vendor + renderer combinations as exposed by WebGL_debug_renderer_info.</summary>
    public static readonly Dictionary<OperatingSystemKind, (string Vendor, string Renderer)[]> WebGlCombos = new()
    {
        [OperatingSystemKind.Windows10] = new[]
        {
            ("Google Inc. (Intel)", "ANGLE (Intel, Intel(R) UHD Graphics 620 Direct3D11 vs_5_0 ps_5_0, D3D11)"),
            ("Google Inc. (Intel)", "ANGLE (Intel, Intel(R) Iris(R) Xe Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)"),
            ("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce GTX 1650 Direct3D11 vs_5_0 ps_5_0, D3D11)"),
            ("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 Direct3D11 vs_5_0 ps_5_0, D3D11)"),
            ("Google Inc. (AMD)", "ANGLE (AMD, AMD Radeon(TM) Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)")
        },
        [OperatingSystemKind.Windows11] = new[]
        {
            ("Google Inc. (Intel)", "ANGLE (Intel, Intel(R) Iris(R) Xe Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)"),
            ("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 Direct3D11 vs_5_0 ps_5_0, D3D11)"),
            ("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce RTX 4060 Direct3D11 vs_5_0 ps_5_0, D3D11)"),
            ("Google Inc. (AMD)", "ANGLE (AMD, AMD Radeon(TM) Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)")
        },
        [OperatingSystemKind.MacOS] = new[]
        {
            ("Google Inc. (Apple)", "ANGLE (Apple, ANGLE Metal Renderer: Apple M1, Unspecified Version)"),
            ("Google Inc. (Apple)", "ANGLE (Apple, ANGLE Metal Renderer: Apple M2, Unspecified Version)"),
            ("Google Inc. (Apple)", "ANGLE (Apple, ANGLE Metal Renderer: Apple M3, Unspecified Version)"),
            ("Google Inc. (Intel Inc.)", "ANGLE (Intel Inc., Intel(R) Iris(TM) Plus Graphics 645, OpenGL 4.1)")
        },
        [OperatingSystemKind.Linux] = new[]
        {
            ("Mesa", "Mesa Intel(R) UHD Graphics 620 (KBL GT2)"),
            ("Mesa", "Mesa Intel(R) Iris(R) Xe Graphics (TGL GT2)"),
            ("NVIDIA Corporation", "NVIDIA GeForce GTX 1650/PCIe/SSE2"),
            ("AMD", "AMD Radeon Graphics (RADV RENOIR)")
        }
    };

    /// <summary>OS-specific timezone candidates (skewed to realistic regions).</summary>
    public static readonly (string Tz, int OffsetMin, double Lat, double Lon)[] Timezones =
    {
        ("America/New_York",   240,  40.7128,  -74.0060),
        ("America/Los_Angeles",420,  34.0522, -118.2437),
        ("America/Chicago",    300,  41.8781,  -87.6298),
        ("Europe/London",        0,  51.5074,   -0.1278),
        ("Europe/Berlin",      -60,  52.5200,   13.4050),
        ("Europe/Paris",       -60,  48.8566,    2.3522),
        ("Europe/Amsterdam",   -60,  52.3676,    4.9041),
        ("Asia/Jakarta",      -420,  -6.2088,  106.8456),
        ("Asia/Tokyo",        -540,  35.6762,  139.6503),
        ("Asia/Singapore",    -480,   1.3521,  103.8198),
        ("Australia/Sydney",  -660, -33.8688,  151.2093)
    };

    /// <summary>Locale candidates aligned to timezones above.</summary>
    public static readonly Dictionary<string, string[]> LanguagesByTimezone = new()
    {
        ["America/New_York"]    = new[] { "en-US", "en" },
        ["America/Los_Angeles"] = new[] { "en-US", "en" },
        ["America/Chicago"]     = new[] { "en-US", "en" },
        ["Europe/London"]       = new[] { "en-GB", "en" },
        ["Europe/Berlin"]       = new[] { "de-DE", "de", "en-US", "en" },
        ["Europe/Paris"]        = new[] { "fr-FR", "fr", "en-US", "en" },
        ["Europe/Amsterdam"]    = new[] { "nl-NL", "nl", "en-US", "en" },
        ["Asia/Jakarta"]        = new[] { "id-ID", "id", "en-US", "en" },
        ["Asia/Tokyo"]          = new[] { "ja-JP", "ja", "en-US", "en" },
        ["Asia/Singapore"]      = new[] { "en-SG", "en", "zh-SG", "zh" },
        ["Australia/Sydney"]    = new[] { "en-AU", "en" }
    };

    private static readonly string[] WindowsFonts =
    {
        "Arial", "Arial Black", "Bahnschrift", "Calibri", "Cambria", "Cambria Math",
        "Candara", "Comic Sans MS", "Consolas", "Constantia", "Corbel", "Courier New",
        "Ebrima", "Franklin Gothic Medium", "Gabriola", "Gadugi", "Georgia",
        "HoloLens MDL2 Assets", "Impact", "Ink Free", "Javanese Text", "Leelawadee UI",
        "Lucida Console", "Lucida Sans Unicode", "Malgun Gothic", "Marlett",
        "Microsoft Himalaya", "Microsoft JhengHei", "Microsoft New Tai Lue",
        "Microsoft PhagsPa", "Microsoft Sans Serif", "Microsoft Tai Le",
        "Microsoft YaHei", "Microsoft Yi Baiti", "MingLiU-ExtB", "Mongolian Baiti",
        "MS Gothic", "MV Boli", "Myanmar Text", "Nirmala UI", "Palatino Linotype",
        "Segoe MDL2 Assets", "Segoe Print", "Segoe Script", "Segoe UI",
        "Segoe UI Emoji", "Segoe UI Historic", "Segoe UI Symbol", "SimSun",
        "Sitka", "Sylfaen", "Symbol", "Tahoma", "Times New Roman", "Trebuchet MS",
        "Verdana", "Webdings", "Wingdings", "Yu Gothic"
    };

    private static readonly string[] MacFonts =
    {
        "American Typewriter", "Andale Mono", "Arial", "Arial Black",
        "Arial Narrow", "Arial Rounded MT Bold", "Arial Unicode MS",
        "Avenir", "Avenir Next", "Avenir Next Condensed", "Baskerville",
        "Big Caslon", "Bodoni 72", "Bradley Hand", "Brush Script MT",
        "Chalkboard", "Chalkduster", "Charter", "Cochin", "Comic Sans MS",
        "Copperplate", "Courier", "Courier New", "Didot", "DIN Alternate",
        "DIN Condensed", "Futura", "Geneva", "Georgia", "Gill Sans",
        "Helvetica", "Helvetica Neue", "Herculanum", "Hoefler Text",
        "Impact", "Lucida Grande", "Luminari", "Marker Felt", "Menlo",
        "Microsoft Sans Serif", "Monaco", "Noteworthy", "Optima", "Palatino",
        "Papyrus", "Phosphate", "Rockwell", "Savoye LET", "SignPainter",
        "Skia", "Snell Roundhand", "Tahoma", "Times", "Times New Roman",
        "Trattatello", "Trebuchet MS", "Verdana", "Zapfino"
    };

    private static readonly string[] LinuxFonts =
    {
        "DejaVu Sans", "DejaVu Sans Mono", "DejaVu Serif",
        "Liberation Mono", "Liberation Sans", "Liberation Serif",
        "Noto Color Emoji", "Noto Mono", "Noto Sans", "Noto Sans CJK JP",
        "Noto Sans CJK KR", "Noto Sans CJK SC", "Noto Sans CJK TC",
        "Noto Serif", "Ubuntu", "Ubuntu Condensed", "Ubuntu Mono"
    };

    /// <summary>Common fonts that appear on each OS family. Used both for spoofing and for blocking enumeration.</summary>
    public static readonly Dictionary<OperatingSystemKind, string[]> FontsByOs = new()
    {
        [OperatingSystemKind.Windows10] = WindowsFonts,
        [OperatingSystemKind.Windows11] = WindowsFonts,
        [OperatingSystemKind.MacOS]     = MacFonts,
        [OperatingSystemKind.Linux]     = LinuxFonts
    };

    public static (string Platform, string SecChUaPlatform, string OsCpu, string UaOs) GetPlatformStrings(OperatingSystemKind os)
    {
        return os switch
        {
            OperatingSystemKind.Windows10 => ("Win32",   "Windows", "",                "Windows NT 10.0; Win64; x64"),
            OperatingSystemKind.Windows11 => ("Win32",   "Windows", "",                "Windows NT 10.0; Win64; x64"),
            OperatingSystemKind.MacOS     => ("MacIntel","macOS",   "Intel Mac OS X 10_15_7", "Macintosh; Intel Mac OS X 10_15_7"),
            OperatingSystemKind.Linux     => ("Linux x86_64","Linux","Linux x86_64",   "X11; Linux x86_64"),
            _ => throw new ArgumentOutOfRangeException(nameof(os))
        };
    }
}
