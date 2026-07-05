using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZeroBrowser.Core.Models;

namespace ZeroBrowser.Core.Fingerprint;

/// <summary>
/// Generates and parses portable fingerprint tokens in the format:
///   base64_key | base64_encrypted_payload | base64_iv | flags | version
///
/// The payload contains the fingerprint seed, pinned OS, and rotation settings
/// encrypted with AES-256-GCM. The key is embedded in the token itself (the
/// token is an opaque portable blob, not a security boundary — it's designed
/// for sharing profiles between ZeroBrowser instances).
///
/// Example output:
///   ThMFnidaOXJ7cXMURGAGIw==|BY3CX2PkklpFQibWy/...==|aDwCyoV3c9TwPZud|08|1
/// </summary>
public static class FingerprintTokenCodec
{
    private const int KeySize   = 32; // AES-256
    private const int NonceSize = 12; // GCM standard
    private const int TagSize   = 16; // GCM tag
    private const int CurrentVersion = 1;

    /// <summary>
    /// Generate a random fingerprint token from a seed and optional settings.
    /// </summary>
    public static string Generate(string seed, OperatingSystemKind? pinnedOs = null, int rotationDays = 0)
    {
        var payload = new TokenPayload
        {
            Seed = seed,
            PinnedOs = pinnedOs?.ToString(),
            RotationDays = rotationDays,
            GeneratedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);

        // Generate random key and nonce.
        var key   = RandomNumberGenerator.GetBytes(KeySize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[json.Length];
        var tag    = new byte[TagSize];

        using (var gcm = new AesGcm(key, TagSize))
        {
            gcm.Encrypt(nonce, json, cipher, tag);
        }

        // Combine cipher + tag for the encrypted portion.
        var encryptedWithTag = new byte[cipher.Length + TagSize];
        Buffer.BlockCopy(cipher, 0, encryptedWithTag, 0, cipher.Length);
        Buffer.BlockCopy(tag, 0, encryptedWithTag, cipher.Length, TagSize);

        // Flags byte: bit 0 = has pinned OS, bit 3 = has rotation
        byte flags = 0;
        if (pinnedOs is not null) flags |= 0x01;
        if (rotationDays > 0)    flags |= 0x08;

        var keyB64     = Convert.ToBase64String(key);
        var payloadB64 = Convert.ToBase64String(encryptedWithTag);
        var nonceB64   = Convert.ToBase64String(nonce);
        var flagsHex   = flags.ToString("x2");

        return $"{keyB64}|{payloadB64}|{nonceB64}|{flagsHex}|{CurrentVersion}";
    }

    /// <summary>
    /// Generate a completely random token with a new random seed.
    /// </summary>
    public static string GenerateRandom(OperatingSystemKind? pinnedOs = null, int rotationDays = 0)
    {
        var seed = Guid.NewGuid().ToString("N");
        return Generate(seed, pinnedOs, rotationDays);
    }

    /// <summary>
    /// Parse a token string back into its components. Returns null if the token is invalid.
    /// </summary>
    public static TokenResult? Parse(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var parts = token.Split('|');
        if (parts.Length < 5) return null;

        try
        {
            var key           = Convert.FromBase64String(parts[0]);
            var encryptedData = Convert.FromBase64String(parts[1]);
            var nonce         = Convert.FromBase64String(parts[2]);
            // parts[3] = flags hex, parts[4] = version
            var version = int.Parse(parts[4]);

            if (key.Length != KeySize || nonce.Length != NonceSize)
                return null;
            if (encryptedData.Length < TagSize || encryptedData.Length > 10_240)
                return null;  // Cap at 10 KB to prevent memory-bomb from crafted tokens

            var cipherLen = encryptedData.Length - TagSize;
            var cipher = encryptedData.AsSpan(0, cipherLen);
            var tag    = encryptedData.AsSpan(cipherLen, TagSize);
            var plain  = new byte[cipherLen];

            using (var gcm = new AesGcm(key, TagSize))
            {
                gcm.Decrypt(nonce, cipher, tag, plain);
            }

            var payload = JsonSerializer.Deserialize<TokenPayload>(plain);
            if (payload is null || string.IsNullOrEmpty(payload.Seed))
                return null;

            OperatingSystemKind? os = null;
            if (payload.PinnedOs is not null)
                os = Enum.Parse<OperatingSystemKind>(payload.PinnedOs, ignoreCase: true);

            return new TokenResult(
                payload.Seed,
                os,
                payload.RotationDays,
                payload.GeneratedAt is > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(payload.GeneratedAt.Value)
                    : null,
                version);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Validate that a string looks like a fingerprint token (quick structural check, no decryption).</summary>
    public static bool LooksLikeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('|');
        return parts.Length >= 5;
    }

    private sealed class TokenPayload
    {
        public string Seed { get; set; } = string.Empty;
        public string? PinnedOs { get; set; }
        public int RotationDays { get; set; }
        public long? GeneratedAt { get; set; }
    }
}

/// <summary>Result of parsing a fingerprint token.</summary>
public sealed record TokenResult(
    string Seed,
    OperatingSystemKind? PinnedOs,
    int RotationDays,
    DateTimeOffset? GeneratedAt,
    int Version);
