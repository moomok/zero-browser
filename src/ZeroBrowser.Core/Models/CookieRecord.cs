namespace ZeroBrowser.Core.Models;

/// <summary>
/// Domain-neutral representation of a single HTTP cookie. Maps cleanly onto
/// PuppeteerSharp's <c>CookieParam</c> at launch time.
/// </summary>
public sealed record CookieRecord
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    /// <summary>Domain like ".example.com" or "example.com".</summary>
    public required string Domain { get; init; }
    public string Path { get; init; } = "/";
    public bool?  HttpOnly { get; init; }
    public bool?  Secure { get; init; }
    /// <summary>"Lax", "Strict", "None", or null.</summary>
    public string? SameSite { get; init; }
    /// <summary>Expiry as Unix-epoch seconds. Null = session cookie.</summary>
    public long? ExpiresUnix { get; init; }
}
