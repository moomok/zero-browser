using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ZeroBrowser.Browser.Tls;

/// <summary>
/// Runtime support check for the TLS/JA3 fingerprint diversion feature.
///
/// As of v0.4 the diversion is implemented by a separate Go binary
/// (see <c>zero-browser-tls-sidecar/</c>) that uses uTLS to construct a
/// ClientHello whose JA3 hash matches a real browser byte-for-byte. Unlike
/// the previous in-process CipherSuitesPolicy approach, this works
/// identically on Windows, macOS, and Linux.
///
/// <see cref="IsAvailable"/> returns true when a sidecar binary is found
/// alongside the host application. The binary is expected at
/// <c>vendor/tls-sidecar/{platform}-{arch}/zero-browser-tls-sidecar[.exe]</c>.
/// </summary>
public static class TlsSupport
{
    /// <summary>
    /// True only when the Go sidecar binary is present and executable.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return File.Exists(GetSidecarPath());
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Resolves the absolute path to the sidecar binary for the current
    /// platform. Returns null if the binary is not bundled.
    /// </summary>
    public static string? ResolveSidecarPath()
    {
        var path = GetSidecarPath();
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Short human-readable reason the feature is unavailable on this build.
    /// Used in the UI to explain why the TLS dropdown is forced to "none".
    /// </summary>
    public static string LimitationReason => IsAvailable
        ? $"TLS/JA3 diversion is ACTIVE in this build (Go+uTLS sidecar detected at {GetSidecarPath()})."
        : $"TLS/JA3 diversion is unavailable: the Go+uTLS sidecar binary was not found at the expected location ({GetSidecarPath()}). " +
          $"Build it with `zero-browser-tls-sidecar/build.ps1` and place it under vendor/tls-sidecar/{{platform}}-{{arch}}/.";

    /// <summary>
    /// Detected platform string for diagnostics and the binary path prefix.
    /// </summary>
    public static string Platform =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "darwin"  :
                                                              "linux";

    private static string GetSidecarPath()
    {
        var exeName = Platform == "windows"
            ? "zero-browser-tls-sidecar.exe"
            : "zero-browser-tls-sidecar";
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        // AppContext.BaseDirectory is the directory of the host .NET assembly.
        var baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "vendor", "tls-sidecar", $"{Platform}-{arch}", exeName);
    }
}
