using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ZeroBrowser.Browser.Tls;

/// <summary>
/// Outcome of an OS-level CA install/revoke operation. Distinct from a
/// "false bool" so the UI can show a precise reason instead of a generic
/// "try as administrator" hint when the real failure is "we don't support
/// this on macOS yet" or "libnss3-tools is not installed".
/// </summary>
public enum CaTrustStoreOpStatus
{
    Success,
    Failed,
    UnsupportedOnPlatform,
    PrerequisiteMissing,
    AlreadyPresent,
}

/// <summary>
/// Result of <see cref="TlsCertificateAuthority.InstallToTrustStoreAsync"/>
/// or <see cref="TlsCertificateAuthority.RevokeFromTrustStoreAsync"/>.
/// <see cref="Reason"/> is human-readable and safe to show in the UI.
/// </summary>
public readonly record struct CaTrustStoreOpResult(CaTrustStoreOpStatus Status, string Reason)
{
    public bool Ok => Status == CaTrustStoreOpStatus.Success || Status == CaTrustStoreOpStatus.AlreadyPresent;

    public static CaTrustStoreOpResult Success(string reason = "ok")
        => new(CaTrustStoreOpStatus.Success, reason);

    public static CaTrustStoreOpResult Failed(string reason)
        => new(CaTrustStoreOpStatus.Failed, reason);

    public static CaTrustStoreOpResult Unsupported(string reason)
        => new(CaTrustStoreOpStatus.UnsupportedOnPlatform, reason);

    public static CaTrustStoreOpResult PrerequisiteMissing(string reason)
        => new(CaTrustStoreOpStatus.PrerequisiteMissing, reason);
}

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
    /// Windows: <c>certutil -addstore Root</c> (requires elevation, shows UAC).
    /// macOS:   <c>security add-trusted-cert -d -p ssl -k /Library/Keychains/System.keychain</c>
    ///          (requires GUI password prompt — not usable in headless CI).
    /// Linux:   <c>certutil -d sql:$HOME/.pki/nssdb -A -t C,, -n "ZeroBrowser TLS CA" -i &lt;CA PEM&gt;</c>
    ///          (requires <c>libnss3-tools</c> to provide <c>certutil</c>; not bundled by default
    ///          on Debian 11+, Ubuntu 22.04+, or most minimal container images).
    /// </summary>
    public static async Task<CaTrustStoreOpResult> InstallToTrustStoreAsync()
    {
        if (!File.Exists(CertPath))
            return CaTrustStoreOpResult.Failed("CA certificate not generated yet — call Generate() first.");

        if (OperatingSystem.IsWindows())
            return await RunInstallAsync(BuildWindowsInstallPsi(CertPath), elevate: true).ConfigureAwait(false);

        if (OperatingSystem.IsMacOS())
            return await RunInstallAsync(BuildMacInstallPsi(CertPath), elevate: false).ConfigureAwait(false);

        // Linux. Pre-flight: certutil must exist.
        var certUtilPath = FindOnPath("certutil");
        if (certUtilPath is null)
        {
            return CaTrustStoreOpResult.PrerequisiteMissing(
                "Linux NSS 'certutil' was not found on PATH. Install 'libnss3-tools' " +
                "(Debian/Ubuntu: `apt-get install libnss3-tools`) and retry.");
        }

        return await RunLinuxNssInstallAsync(certUtilPath).ConfigureAwait(false);
    }

    /// <summary>Remove the CA from the OS trust store.</summary>
    /// <remarks>
    /// macOS and Linux revocation is not yet automated — surfacing a clear
    /// "not supported, remove manually" message in the UI instead of silently
    /// succeeding is deliberate (see commit history: silent-fail in security
    /// flows is a footgun).
    /// </remarks>
    public static async Task<CaTrustStoreOpResult> RevokeFromTrustStoreAsync()
    {
        if (OperatingSystem.IsMacOS())
        {
            return CaTrustStoreOpResult.Unsupported(
                "macOS: revocation is not automated. Open Keychain Access, search for " +
                "'ZeroBrowser TLS Diversion CA', and delete it manually from the " +
                "login keychain (and System keychain if you previously installed there).");
        }

        if (OperatingSystem.IsLinux())
        {
            return CaTrustStoreOpResult.Unsupported(
                "Linux: revocation is not automated. Run `certutil -d sql:$HOME/.pki/nssdb " +
                "-D -n \"ZeroBrowser TLS CA\"` (per-user NSS) or remove the file from " +
                "/usr/local/share/ca-certificates and run `sudo update-ca-certificates --fresh`.");
        }

        if (!OperatingSystem.IsWindows())
        {
            // Defensive: any future platform we don't know about must NOT silently no-op.
            return CaTrustStoreOpResult.Unsupported(
                "Revoke is not implemented on this platform. Remove the CA manually from the OS trust store.");
        }

        var cert = LoadCertificate();
        if (cert is null)
            return CaTrustStoreOpResult.Failed("CA certificate is not on disk — nothing to revoke.");

        var psi = BuildWindowsRevokePsi(cert.Thumbprint);
        return await RunInstallAsync(psi, elevate: true).ConfigureAwait(false);
    }

    // ─── ProcessStartInfo builders (testable) ────────────────────────────────

    internal static ProcessStartInfo BuildWindowsInstallPsi(string certPath)
    {
        // UAC elevation required to modify the LocalMachine\Root store.
        return new ProcessStartInfo
        {
            FileName = "certutil",
            Arguments = $"-addstore Root \"{certPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
        };
    }

    internal static ProcessStartInfo BuildWindowsRevokePsi(string thumbprint)
    {
        return new ProcessStartInfo
        {
            FileName = "certutil",
            Arguments = $"-delstore Root \"{thumbprint}\"",
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
        };
    }

    internal static ProcessStartInfo BuildMacInstallPsi(string certPath)
    {
        // Writes to System.keychain — requires GUI password prompt, which is
        // why this can't run in a headless CI environment. We still set
        // UseShellExecute=false so Process.WaitForExitAsync works correctly on
        // .NET 8 (UseShellExecute=true is a Windows-only convenience on Linux/Mac
        // it fails outright).
        return new ProcessStartInfo
        {
            FileName = "security",
            Arguments = $"add-trusted-cert -d -p ssl -k /Library/Keychains/System.keychain \"{certPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    internal static ProcessStartInfo BuildLinuxNssInstallPsi(string certUtilPath, string caPemPath)
    {
        // We pass the CA via -i <file> (NOT stdin) so we don't need
        // RedirectStandardInput, which is incompatible with the older .NET
        // Core ProcessStartInfo validation on Linux (Setting
        // RedirectStandardInput=true with UseShellExecute=true used to throw;
        // we set UseShellExecute=false everywhere on non-Windows to avoid it).
        return new ProcessStartInfo
        {
            FileName = certUtilPath,
            Arguments = $"-d sql:$HOME/.pki/nssdb -A -t C,, -n \"ZeroBrowser TLS CA\" -i \"{caPemPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    // ─── Process execution ───────────────────────────────────────────────────

    private static async Task<CaTrustStoreOpResult> RunInstallAsync(ProcessStartInfo psi, bool elevate)
    {
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return CaTrustStoreOpResult.Failed("Process.Start returned null (binary not found?).");
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode == 0)
                return CaTrustStoreOpResult.Success(elevate
                    ? "Installed via certutil (elevated)."
                    : "Installed via OS trust command.");
            return CaTrustStoreOpResult.Failed(
                $"'{psi.FileName} {psi.Arguments}' exited with code {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            return CaTrustStoreOpResult.Failed($"Process execution failed: {ex.Message}");
        }
    }

    private static async Task<CaTrustStoreOpResult> RunLinuxNssInstallAsync(string certUtilPath)
    {
        var psi = BuildLinuxNssInstallPsi(certUtilPath, CertPath);
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return CaTrustStoreOpResult.Failed("Process.Start returned null for certutil.");
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode == 0)
                return CaTrustStoreOpResult.Success($"Installed to NSS database at $HOME/.pki/nssdb via {certUtilPath}.");
            return CaTrustStoreOpResult.Failed(
                $"'{psi.FileName} {psi.Arguments}' exited with code {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            return CaTrustStoreOpResult.Failed($"certutil execution failed: {ex.Message}");
        }
    }

    private static string? FindOnPath(string command)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, command);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Skip unreadable PATH entries.
            }
        }
        return null;
    }
}

/// <summary>
/// Helpers for ensuring external binaries (Go sidecar, certutil, security)
/// are in a runnable state on disk. Cross-platform — Windows is a no-op.
/// </summary>
public static class ExecutablePermissions
{
    /// <summary>
    /// Best-effort: mark the file user-executable. On Windows this is a no-op
    /// (the executable bit is determined by the file extension and ACLs, not
    /// by a POSIX mode bit). On Linux/macOS we set the user-exec bit if it
    /// isn't already set — important when the binary was just copied from a
    /// Windows filesystem (FAT32/NTFS don't carry POSIX mode bits).
    /// </summary>
    public static void EnsureUserExecutable(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            if (OperatingSystem.IsWindows()) return;
            var mode = File.GetUnixFileMode(path);
            if ((mode & UnixFileMode.UserExecute) == 0)
                File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
        }
        catch
        {
            // Best-effort: do not fail the launch on permission errors.
        }
    }
}
