using System.Runtime.InteropServices;

namespace ZeroBrowser.Browser.Tls;

/// <summary>
/// Runtime support check for the in-process TLS fingerprint diversion feature.
///
/// As of v0.3 the diversion relies on <see cref="System.Net.Security.CipherSuitesPolicy"/>.
/// That API has two fundamental limitations that prevent it from producing
/// realistic JA3 hashes on ANY platform:
///
///   1. <c>CipherSuitesPolicy</c> only filters the cipher suite list; it does NOT
///      control the order in which the OS TLS stack (SChannel on Windows,
///      OpenSSL on Linux/macOS) sends them in the ClientHello. JA3 hashes the
///      cipher list in transmission order, so the resulting hash is "alien"
///      (does not match any real browser) and is flagged by anti-bot systems.
///
///   2. JA3 also hashes the ClientHello extensions (supported_groups,
///      signature_algorithms, ALPN, psk_key_exchange_modes, …) and their order.
///      <c>CipherSuitesPolicy</c> does not let us override extensions at all.
///      .NET's <c>SslStream</c> emits its own extension set which differs from
///      any real browser.
///
/// On top of that, <c>CipherSuitesPolicy</c> itself throws
/// <see cref="PlatformNotSupportedException"/> on Windows (only Linux w/ OpenSSL
/// 1.1.1+ and macOS support it), so the cipher-list override is silently
/// unavailable on the primary target platform.
///
/// Conclusion: the <c>CipherSuitesPolicy</c> approach cannot produce
/// browser-matching JA3 hashes anywhere. We disable the feature at runtime
/// and direct users to the planned Go+uTLS sidecar (see README roadmap).
///
/// <see cref="IsAvailable"/> therefore returns <c>false</c> on every platform
/// until a real implementation lands.
/// </summary>
public static class TlsSupport
{
    /// <summary>
    /// True only when the runtime can actually emit a ClientHello whose JA3
    /// hash matches a real browser. Currently always false.
    /// </summary>
    public static bool IsAvailable => false;

    /// <summary>
    /// Short human-readable reason the feature is unavailable on this build.
    /// Used in the UI to explain why the TLS dropdown is forced to "none".
    /// </summary>
    public static string LimitationReason =>
        "TLS/JA3 diversion is currently disabled. The .NET CipherSuitesPolicy API " +
        "cannot control cipher suite ORDER or ClientHello extensions, both of which " +
        "are hashed by JA3 — so the resulting hash does not match any real browser " +
        "and gets flagged by anti-bot systems. A Go+uTLS sidecar is planned; see README.";

    /// <summary>
    /// Detected platform string for diagnostics.
    /// </summary>
    public static string Platform =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "macos"   :
                                                              "linux";
}
