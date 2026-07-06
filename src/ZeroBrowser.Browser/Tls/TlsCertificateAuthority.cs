using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ZeroBrowser.Browser.Tls;

/// <summary>
/// Manages the local Root CA used for TLS MITM (sidecar proxy mode only).
/// The CA is generated once, stored encrypted via app-level SecretBox, and installed
/// to the OS trust store with explicit user consent.
/// </summary>
public static class TlsCertificateAuthority
{
    private const string CaCertFileName = "zero-browser-ca.crt";
    private const string CaKeyFileName  = "zero-browser-ca.key";
    private const string CaKeyPemFileName = "zero-browser-ca.key.pem";

    /// <summary>Full path to the CA certificate file (PEM).</summary>
    public static string CertPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "ZeroBrowser", "tls", CaCertFileName);

    /// <summary>Full path to the CA private key file (encrypted, internal use).</summary>
    public static string KeyPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "ZeroBrowser", "tls", CaKeyFileName);

    /// <summary>
    /// Full path to the CA private key file in PEM form, readable by external
    /// processes like the Go+uTLS sidecar. Generated alongside <see cref="KeyPath"/>
    /// whenever <see cref="Generate"/> runs. Stored as plaintext PEM (the same
    /// secret-box wrapping done for the legacy key file is not applied here —
    /// the directory permission is the protection, matching the threat model of
    /// any local MITM proxy tool).
    /// </summary>
    public static string KeyPemPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "ZeroBrowser", "tls", CaKeyPemFileName);

    public static bool Exists => File.Exists(CertPath) && File.Exists(KeyPath);

    /// <summary>
    /// Generate a root CA certificate (RSA-4096, 10-year validity).
    /// Returns the certificate. The private key is encrypted with app-level SecretBox
    /// and saved to disk. The cert is saved as PEM.
    /// </summary>
    public static X509Certificate2 Generate(byte[]? secretBoxKey = null)
    {
        var dir = Path.GetDirectoryName(CertPath)!;
        Directory.CreateDirectory(dir);

        using var rsa = RSA.Create(4096);
        var subject = new X500DistinguishedName("CN=Zero Browser TLS Diversion CA, O=ZeroBrowser, OU=Fingerprint Tool");
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(10);

        var cert = request.CreateSelfSigned(notBefore, notAfter);

        // Export cert as PEM.
        var certPem = $"-----BEGIN CERTIFICATE-----\n{Convert.ToBase64String(cert.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks)}\n-----END CERTIFICATE-----\n";
        File.WriteAllText(CertPath, certPem);

        // Export private key as PEM encrypted with app key.
        var keyBytes = rsa.ExportPkcs8PrivateKey();
        if (secretBoxKey is not null)
        {
            using var aes = Aes.Create();
            aes.Key = secretBoxKey[..32];
            aes.GenerateIV();
            var encryptedKey = aes.EncryptCbc(keyBytes, aes.IV, PaddingMode.PKCS7);
            var payload = new byte[aes.IV.Length + encryptedKey.Length];
            Buffer.BlockCopy(aes.IV, 0, payload, 0, aes.IV.Length);
            Buffer.BlockCopy(encryptedKey, 0, payload, aes.IV.Length, encryptedKey.Length);
            File.WriteAllBytes(KeyPath, payload);
        }
        else
        {
            File.WriteAllBytes(KeyPath, keyBytes);
        }

        // Also write the key as plaintext PEM, readable by the Go sidecar.
        var keyPem = "-----BEGIN PRIVATE KEY-----\n" +
                     Convert.ToBase64String(keyBytes, Base64FormattingOptions.InsertLineBreaks) +
                     "\n-----END PRIVATE KEY-----\n";
        File.WriteAllText(KeyPemPath, keyPem);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Restrict access to current user only (best-effort on Windows).
                File.SetAttributes(KeyPemPath, File.GetAttributes(KeyPemPath) | FileAttributes.Hidden);
            }
            else
            {
                File.SetUnixFileMode(KeyPemPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch { /* best-effort */ }

        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
    }

    /// <summary>Load the CA certificate from disk.</summary>
    public static X509Certificate2? LoadCertificate()
    {
        if (!File.Exists(CertPath)) return null;
        try
        {
            return new X509Certificate2(File.ReadAllText(CertPath));
        }
        catch { return null; }
    }

    /// <summary>
    /// Install the CA certificate to the OS trust store.
    /// Windows: CertUtil -addstore Root. (Requires admin — shows UAC prompt.)
    /// macOS/Linux: manual via security/certutil commands.
    /// </summary>
    public static async Task<bool> InstallToTrustStoreAsync()
    {
        if (!File.Exists(CertPath)) return false;

        if (OperatingSystem.IsWindows())
        {
            // CertUtil — requires elevated privileges. This shows UAC.
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "certutil",
                Arguments = $"-addstore Root \"{CertPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true
            };
            try
            {
                using var process = System.Diagnostics.Process.Start(psi);
                if (process is null) return false;
                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }
            catch { return false; }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "security",
                Arguments = $"add-trusted-cert -d -p ssl -k /Library/Keychains/System.keychain \"{CertPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true
            };
            try
            {
                using var process = System.Diagnostics.Process.Start(psi);
                if (process is null) return false;
                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }
            catch { return false; }
        }
        else // Linux
        {
            // On Linux, Chromium uses NSS cer9.db. Import per profile or system-wide.
            var caPem = File.ReadAllText(CertPath);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "certutil",
                Arguments = $"-d sql:$HOME/.pki/nssdb -A -t C,, -n \"ZeroBrowser TLS CA\" -i /dev/stdin",
                UseShellExecute = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            try
            {
                using var process = System.Diagnostics.Process.Start(psi);
                if (process is null) return false;
                await process.StandardInput.WriteAsync(caPem);
                process.StandardInput.Close();
                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }
            catch { return false; }
        }
    }

    /// <summary>Remove the CA from the OS trust store.</summary>
    public static async Task<bool> RevokeFromTrustStoreAsync()
    {
        if (!OperatingSystem.IsWindows())
            return false; // macOS/Linux revoke needs manual steps for now

        var cert = LoadCertificate();
        if (cert is null) return false;

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "certutil",
            Arguments = $"-delstore Root \"{cert.Thumbprint}\"",
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true
        };
        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch { return false; }
    }
}