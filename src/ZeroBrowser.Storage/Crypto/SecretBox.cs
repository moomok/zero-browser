using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace ZeroBrowser.Storage.Crypto;

/// <summary>
/// Authenticated encryption (AES-256-GCM) of small byte payloads, with a key derived from a
/// user-provided master password via Argon2id. The serialized format is:
///   [magic 4][version 1][saltLen 1][salt][nonce 12][tag 16][cipherLen 4][cipher]
/// </summary>
public sealed class SecretBox
{
    private const int    KeySize     = 32;
    private const int    NonceSize   = 12;
    private const int    TagSize     = 16;
    private const byte   Version     = 1;
    private static readonly byte[] Magic = "ADB1"u8.ToArray();

    private readonly byte[] _key;

    private SecretBox(byte[] key) { _key = key; }

    /// <summary>Derive a 256-bit key from a master password via Argon2id.</summary>
    public static SecretBox Derive(string masterPassword, byte[] salt)
    {
        if (salt.Length < 16) throw new ArgumentException("salt must be >= 16 bytes", nameof(salt));
        var argon = new Argon2id(Encoding.UTF8.GetBytes(masterPassword))
        {
            Salt = salt,
            DegreeOfParallelism = 2,
            MemorySize = 64 * 1024,   // 64 MiB
            Iterations = 3
        };
        var key = argon.GetBytes(KeySize);
        return new SecretBox(key);
    }

    public static byte[] NewSalt(int length = 16)
    {
        var s = new byte[length];
        RandomNumberGenerator.Fill(s);
        return s;
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[NonceSize];
        Span<byte> tag   = stackalloc byte[TagSize];
        RandomNumberGenerator.Fill(nonce);

        var cipher = new byte[plaintext.Length];
        using (var gcm = new AesGcm(_key, TagSize))
        {
            gcm.Encrypt(nonce, plaintext, cipher, tag);
        }

        // Output: [magic][ver][nonce][tag][cipherLen 4][cipher]
        var output = new byte[Magic.Length + 1 + NonceSize + TagSize + 4 + cipher.Length];
        var w = 0;
        Magic.CopyTo(output, w); w += Magic.Length;
        output[w++] = Version;
        nonce.CopyTo(output.AsSpan(w, NonceSize)); w += NonceSize;
        tag.CopyTo(output.AsSpan(w, TagSize)); w += TagSize;
        BitConverter.GetBytes(cipher.Length).CopyTo(output, w); w += 4;
        cipher.CopyTo(output, w);
        return output;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < Magic.Length + 1 + NonceSize + TagSize + 4)
            throw new CryptographicException("ciphertext too short");
        var r = 0;
        for (int i = 0; i < Magic.Length; i++)
        {
            if (blob[r++] != Magic[i]) throw new CryptographicException("bad magic");
        }
        var ver = blob[r++];
        if (ver != Version) throw new CryptographicException($"unsupported version {ver}");
        var nonce = blob.Slice(r, NonceSize); r += NonceSize;
        var tag   = blob.Slice(r, TagSize);   r += TagSize;
        var cipherLen = BitConverter.ToInt32(blob.Slice(r, 4)); r += 4;
        if (cipherLen < 0 || r + cipherLen > blob.Length) throw new CryptographicException("bad length");
        var cipher = blob.Slice(r, cipherLen);

        var plain = new byte[cipherLen];
        using (var gcm = new AesGcm(_key, TagSize))
        {
            gcm.Decrypt(nonce, cipher, tag, plain);
        }
        return plain;
    }

    public string EncryptString(string plaintext) =>
        Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(plaintext)));

    public string DecryptString(string base64) =>
        Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(base64)));
}
