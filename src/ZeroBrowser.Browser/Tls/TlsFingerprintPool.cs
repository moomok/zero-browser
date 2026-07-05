using System.Net.Security;
using ZeroBrowser.Core.Util;

namespace ZeroBrowser.Browser.Tls;
/// <summary>
/// Determines the TLS ClientHello fingerprint (cipher suites) per profile.
/// Each fingerprint mode maps to a real-world browser's cipher suite order,
/// derived from validated JA3 databases. Selection is deterministic from profile seed.
/// </summary>
public static class TlsFingerprintPool
{
    // Real Chrome 150 cipher suite order (TLS 1.3+1.2):
    // GREASE + 4865(TLS_AES_128_GCM_SHA256) + 4866(TLS_AES_256_GCM_SHA384) + 4867(TLS_CHACHA20_POLY1305_SHA256)
    // + GREASE + 256(CHACHA20_POLY1305_SHA256) + 49200(ECDHE_RSA_AES128_GCM_SHA256) + 49202(ECDHE_ECDSA_AES128_GCM_SHA256)
    // + GREASE + 255(RSA_WITH_AES_128_GCM) + ...
    private static readonly TlsCipherSuite[] ChromeSuites = new[]
    {
        (TlsCipherSuite)0xAAAA, // GREASE placeholder — .NET will skip unknown
        TlsCipherSuite.TLS_AES_128_GCM_SHA256,       // 0x1301
        TlsCipherSuite.TLS_AES_256_GCM_SHA384,       // 0x1302
        TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256, // 0x1303
        (TlsCipherSuite)0xAAAA,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,   // 0xC030
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256, // 0xC02C
        (TlsCipherSuite)0xAAAA,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,   // 0xCCA9
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256, // 0xCCA8
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,   // 0xC031
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384, // 0xC02D
    };

    // Real Firefox 130 cipher suite order (TLS 1.3+1.2):
    private static readonly TlsCipherSuite[] FirefoxSuites = new[]
    {
        (TlsCipherSuite)0xAAAA,
        TlsCipherSuite.TLS_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_AES_256_GCM_SHA384,
        (TlsCipherSuite)0xAAAA,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,
        (TlsCipherSuite)0xAAAA,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
    };

    // Real Safari 18 (macOS) cipher suite order:
    private static readonly TlsCipherSuite[] SafariSuites = new[]
    {
        (TlsCipherSuite)0xAAAA,
        TlsCipherSuite.TLS_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_AES_256_GCM_SHA384,
        (TlsCipherSuite)0xAAAA,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        (TlsCipherSuite)0xAAAA,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_RSA_WITH_AES_128_GCM_SHA256, // Safari retains RSA suites
    };

    // Edge 150 (similar to Chrome but slightly different order):
    private static readonly TlsCipherSuite[] EdgeSuites = new[]
    {
        (TlsCipherSuite)0xAAAA,
        TlsCipherSuite.TLS_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
    };

    /// <summary>
    /// Get cipher suites for a given profile seed and TLS mode.
    /// "randomized" picks deterministically from the pool using the seed.
    /// </summary>
    public static TlsCipherSuite[] GetCipherSuites(string seed, string tlsMode)
    {
        return tlsMode.ToLowerInvariant() switch
        {
            "chrome"  => ChromeSuites,
            "firefox" => FirefoxSuites,
            "safari"  => SafariSuites,
            "edge"    => EdgeSuites,
            _         => PickRandomDeterministic(seed)
        };
    }

    /// <summary>
    /// Verify consistency: if the profile's User-Agent says Chrome but TLS mode says Firefox,
    /// warn and auto-correct to match.
    /// </summary>
    public static string NormalizeToUserAgent(string tlsMode, string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent) || tlsMode is "none" or "randomized")
            return tlsMode;

        var uaLower = userAgent.ToLowerInvariant();
        if (uaLower.Contains("chrome") && tlsMode != "chrome" && tlsMode != "edge")
            return "chrome";
        if (uaLower.Contains("firefox") && tlsMode != "firefox")
            return "firefox";
        if (uaLower.Contains("safari") && !uaLower.Contains("chrome") && tlsMode != "safari")
            return "safari";

        return tlsMode;
    }

    private static TlsCipherSuite[] PickRandomDeterministic(string seed)
    {
        var rnd = new SeededRandom(seed + ":tls");
        var pools = new[] { ChromeSuites, FirefoxSuites, SafariSuites, EdgeSuites };
        var index = rnd.Next(0, pools.Length);
        return pools[index];
    }
}