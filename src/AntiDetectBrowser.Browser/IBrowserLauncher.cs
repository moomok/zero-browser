using AntiDetectBrowser.Core.Models;

namespace AntiDetectBrowser.Browser;

public interface IBrowserLauncher
{
    /// <summary>Launch Chromium for a given profile and inject the fingerprint script.</summary>
    Task<IBrowserSession> LaunchAsync(LaunchRequest request, CancellationToken ct = default);
}

public sealed record LaunchRequest(
    Profile Profile,
    FingerprintProfile Fingerprint,
    ProxyEntry? Proxy,
    string? StartUrl,
    bool Headless = false);

public interface IBrowserSession : IAsyncDisposable
{
    Profile Profile { get; }
    bool IsRunning { get; }
    Task CloseAsync();
}
