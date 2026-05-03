namespace AntiDetectBrowser.Core.Models;

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

    public required string FingerprintSeed { get; init; }
    public Guid?           ProxyId { get; set; }

    /// <summary>Absolute path to the per-profile user data directory used by Chromium.</summary>
    public required string StoragePath { get; init; }

    public DateTimeOffset  CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
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
