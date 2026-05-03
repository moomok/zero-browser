namespace ZeroBrowser.Storage.Crypto;

/// <summary>
/// Manages the on-disk master-key file. The file stores:
///   [salt:16][encryptedVerifier:variable]
/// where <c>encryptedVerifier</c> is <see cref="SecretBox"/>-encrypted bytes of a fixed known
/// plaintext. To unlock the app, we re-derive the SecretBox from the user's password + salt
/// and verify that the verifier decrypts to the expected plaintext. A successful unlock yields
/// the live SecretBox which the app uses to encrypt/decrypt any subsequent secrets.
/// </summary>
public sealed class MasterKey
{
    private const int SaltLen = 16;
    private static readonly byte[] VerifierPlaintext = "ZeroBrowser-Master-Key-v1"u8.ToArray();

    public string FilePath { get; }
    public bool Exists => File.Exists(FilePath);

    public MasterKey(string filePath)
    {
        FilePath = filePath;
    }

    /// <summary>Initialize a new master-key file from a freshly chosen password.</summary>
    public SecretBox Initialize(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("password must be non-empty", nameof(password));
        var salt = SecretBox.NewSalt(SaltLen);
        var box  = SecretBox.Derive(password, salt);
        var encrypted = box.Encrypt(VerifierPlaintext);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var output = new byte[SaltLen + encrypted.Length];
        Buffer.BlockCopy(salt, 0, output, 0, SaltLen);
        Buffer.BlockCopy(encrypted, 0, output, SaltLen, encrypted.Length);
        File.WriteAllBytes(FilePath, output);
        return box;
    }

    /// <summary>Try to unlock with a given password. Returns null if password is wrong / file is corrupt.</summary>
    public SecretBox? TryUnlock(string password)
    {
        if (!Exists) return null;
        byte[] data;
        try { data = File.ReadAllBytes(FilePath); }
        catch { return null; }
        if (data.Length < SaltLen + 32) return null;

        var salt = data.AsSpan(0, SaltLen).ToArray();
        var encrypted = data.AsSpan(SaltLen).ToArray();

        try
        {
            var box = SecretBox.Derive(password, salt);
            var plaintext = box.Decrypt(encrypted);
            if (plaintext.SequenceEqual(VerifierPlaintext)) return box;
            return null;
        }
        catch
        {
            return null;
        }
    }
}
