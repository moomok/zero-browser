namespace ZeroBrowser.Core.Models;

/// <summary>
/// Persistent profile entity stored in the local database.
/// </summary>
public sealed class Profile
{
    public required Guid   Id { get; init; }
    public required string Name { get; set; }
    public string?         Notes { get; set; }
    public string?         GroupId { get; set; }
    public List<string>    Tags { get; set; } = new();

    public required string FingerprintSeed { get; set; }
    public OperatingSystemKind? PinnedOs { get; set; }
    public Guid?           ProxyId { get; set; }

    /// <summary>Absolute path to the per-profile user data directory used by Chromium.</summary>
    public required string StoragePath { get; init; }

    /// <summary>
    /// Optional override for the Chromium executable. When null, the bundled
    /// Chromium-for-Testing build (downloaded by PuppeteerSharp's BrowserFetcher)
    /// is used. When set, points to an absolute path of an installed Chromium-based
    /// browser (Google Chrome, Brave, Edge, …) so the profile can use the real
    /// Chrome Web Store. See <see cref="ZeroBrowser.Browser.BrowserDetector"/>.
    /// </summary>
    public string? EnginePath { get; set; }

    /// <summary>Fingerprint rotation interval in days. 0 = no rotation.</summary>
    public int RotationIntervalDays { get; set; }

    /// <summary>When the fingerprint seed was last rotated.</summary>
    public DateTimeOffset? LastRotatedAt { get; set; }

    /// <summary>
    /// Portable encrypted fingerprint token in the format:
    ///   base64_key|base64_encrypted_payload|base64_iv|flags_hex|version
    /// Auto-generated when the seed changes. Can be imported to clone a profile's identity.
    /// </summary>
    public string? FingerprintToken { get; set; }

    public DateTimeOffset  CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>
/// A Chromium extension associated with a single profile. Extensions are
/// passed to Chromium via <c>--load-extension=…</c> when the profile launches.
/// </summary>
public sealed class ProfileExtension
{
    public required Guid   Id { get; init; }
    public required Guid   ProfileId { get; init; }
    public required string Name { get; set; }

    /// <summary>
    /// Absolute path to an unpacked extension directory (the folder containing
    /// <c>manifest.json</c>). CRX files imported via the UI are auto-extracted
    /// to a managed folder under the app data directory and the resulting
    /// directory path is stored here.
    /// </summary>
    public required string Path { get; set; }
    public bool   Enabled   { get; set; } = true;
    public int    SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Records a previously used fingerprint seed for a profile, allowing
/// the user to switch back to an earlier identity.
/// </summary>
public sealed class SeedHistoryEntry
{
    public required Guid Id { get; init; }
    public required Guid ProfileId { get; init; }
    public required string Seed { get; set; }
    public string? Label { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ProxyEntry
{
    public required Guid       Id { get; init; }
    public required ProxyType  Type { get; init; }
    public required string     Host { get; init; }
    public required int        Port { get; init; }
    public string?             Username { get; init; }
    public string?             Password { get; init; }
    public string?             Country { get; set; }
    public string?             City { get; set; }
    public DateTimeOffset?     LastCheckAt { get; set; }
    public string?             Status { get; set; }
    public string?             IpLastSeen { get; set; }
}
