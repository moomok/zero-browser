using System.IO.Compression;

namespace ZeroBrowser.Core.Util;

/// <summary>
/// Extract a Chromium <c>.crx</c> extension package into an unpacked folder
/// suitable for use with Chromium's <c>--load-extension=</c> argument.
///
/// CRX is a small binary header followed by a regular ZIP archive containing
/// the extension files (manifest.json + assets). This importer parses the
/// header to find the offset where the embedded ZIP begins, then unzips it.
/// CRX v2 and v3 are both supported (only the header layout differs).
/// </summary>
public static class CrxImporter
{
    private static readonly byte[] Magic = "Cr24"u8.ToArray();

    /// <summary>
    /// Extract <paramref name="crxPath"/> into <paramref name="destinationDir"/>.
    /// Throws if the file is not a valid CRX or does not contain a manifest.json.
    /// Returns the destination directory path on success.
    /// </summary>
    public static string Extract(string crxPath, string destinationDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(crxPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);

        if (!File.Exists(crxPath))
            throw new FileNotFoundException($"CRX file not found: {crxPath}", crxPath);

        var bytes = File.ReadAllBytes(crxPath);
        if (bytes.Length < 12)
            throw new InvalidDataException("CRX file is too short to be valid.");

        // Magic 'Cr24'.
        for (var i = 0; i < 4; i++)
        {
            if (bytes[i] != Magic[i])
                throw new InvalidDataException("Not a CRX file (bad magic header).");
        }

        var version = ReadUInt32Le(bytes, 4);
        int zipOffset;

        switch (version)
        {
            case 2:
                {
                    // v2: pubkey_len (4 LE) + sig_len (4 LE) + pubkey + sig + zip
                    if (bytes.Length < 16)
                        throw new InvalidDataException("CRX v2 header truncated.");
                    var pubKeyLen = ReadUInt32Le(bytes, 8);
                    var sigLen    = ReadUInt32Le(bytes, 12);
                    zipOffset = 16 + (int)pubKeyLen + (int)sigLen;
                    break;
                }
            case 3:
                {
                    // v3: header_len (4 LE) + header_protobuf + zip
                    if (bytes.Length < 12)
                        throw new InvalidDataException("CRX v3 header truncated.");
                    var headerLen = ReadUInt32Le(bytes, 8);
                    zipOffset = 12 + (int)headerLen;
                    break;
                }
            default:
                throw new InvalidDataException($"Unsupported CRX version: {version}.");
        }

        if (zipOffset >= bytes.Length)
            throw new InvalidDataException("CRX header points past end of file.");

        Directory.CreateDirectory(destinationDir);

        using (var zipStream = new MemoryStream(bytes, zipOffset, bytes.Length - zipOffset, writable: false))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            archive.ExtractToDirectory(destinationDir, overwriteFiles: true);
        }

        var manifest = Path.Combine(destinationDir, "manifest.json");
        if (!File.Exists(manifest))
            throw new InvalidDataException("Extracted CRX does not contain manifest.json.");

        return destinationDir;
    }

    /// <summary>
    /// Heuristic: read <c>name</c> from manifest.json if present, otherwise
    /// fall back to the directory name. Used to label sideloaded extensions
    /// in the UI without forcing the user to type a name.
    /// </summary>
    public static string ReadManifestName(string extensionDir)
    {
        var manifest = Path.Combine(extensionDir, "manifest.json");
        if (!File.Exists(manifest)) return Path.GetFileName(extensionDir);

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            if (doc.RootElement.TryGetProperty("name", out var nameProp))
            {
                var raw = nameProp.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    // Some extensions use __MSG_xxx__ for i18n names. Strip the
                    // markers and let the user rename the placeholder.
                    if (raw.StartsWith("__MSG_") && raw.EndsWith("__"))
                        return raw[6..^2];
                    return raw;
                }
            }
        }
        catch
        {
            // Best-effort only. Fall through to directory-name fallback.
        }

        return Path.GetFileName(extensionDir);
    }

    private static uint ReadUInt32Le(byte[] bytes, int offset)
        => (uint)(bytes[offset]
            | (bytes[offset + 1] << 8)
            | (bytes[offset + 2] << 16)
            | (bytes[offset + 3] << 24));
}
