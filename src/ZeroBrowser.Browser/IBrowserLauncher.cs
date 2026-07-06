using ZeroBrowser.Core.Models;

namespace ZeroBrowser.Browser;

public interface IBrowserLauncher
{
    /// <summary>Launch Chromium for a given profile and inject the fingerprint script.</summary>
    Task<IBrowserSession> LaunchAsync(LaunchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Human-readable warning from the most recent launch (e.g. "TLS diversion
    /// requested but disabled on this platform"). Null when the launch was
    /// clean. UI surfaces this as a non-blocking banner.
    /// </summary>
    string? LastWarning { get; }
}

public sealed record LaunchRequest(
    Profile Profile,
    FingerprintProfile Fingerprint,
    ProxyEntry? Proxy,
    string? StartUrl,
    bool Headless = false,
    IReadOnlyList<ProfileExtension>? Extensions = null);

public interface IBrowserSession : IAsyncDisposable
{
    Profile Profile { get; }
    bool IsRunning { get; }
    Task CloseAsync();
}
