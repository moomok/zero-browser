using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentAssertions;
using ZeroBrowser.Browser.Tls;

namespace ZeroBrowser.Tests;

/// <summary>
/// Unit tests for the cross-platform TLS Root CA handling code. These tests
/// verify the ProcessStartInfo builders and branching logic WITHOUT requiring
/// an actual macOS or Linux machine — they run on whatever OS the test runner
/// is on (currently Windows CI + Windows dev machine).
///
/// The bugs these tests would have caught in v0.3 are exactly the ones a
/// cross-platform CI matrix would surface first: a `UseShellExecute=true` on
/// non-Windows (which throws at runtime on .NET 8 Linux), and the invalid
/// combination of `RedirectStandardInput=true` with `UseShellExecute=true`
/// (which fails outright when you actually try to launch the process).
/// </summary>
public class TlsCertificateAuthorityTests
{
    // ─── ProcessStartInfo builders ───────────────────────────────────────────

    [Fact]
    public void Windows_install_psi_uses_certutil_addstore_with_elevation()
    {
        var psi = TlsCertificateAuthority.BuildWindowsInstallPsi("C:\\ca.pem");

        psi.FileName.Should().Be("certutil");
        psi.Arguments.Should().Contain("-addstore Root");
        psi.Arguments.Should().Contain("C:\\ca.pem");
        psi.Verb.Should().Be("runas", "UAC elevation is required to modify LocalMachine\\Root");
        psi.UseShellExecute.Should().BeTrue("certutil needs shell elevation on Windows");
        psi.CreateNoWindow.Should().BeTrue();
    }

    [Fact]
    public void Windows_revoke_psi_uses_certutil_delstore_with_elevation()
    {
        var psi = TlsCertificateAuthority.BuildWindowsRevokePsi("ABCDEF1234567890");

        psi.FileName.Should().Be("certutil");
        psi.Arguments.Should().Contain("-delstore Root");
        psi.Arguments.Should().Contain("ABCDEF1234567890");
        psi.Verb.Should().Be("runas");
        psi.UseShellExecute.Should().BeTrue();
    }

    [Fact]
    public void Mac_install_psi_does_NOT_use_shell_execute()
    {
        // BUG FIX verification: previously UseShellExecute=true on macOS,
        // which throws "UseShellExecute requires a FileName that is a single
        // executable on .NET 8 on Linux/macOS" the moment you try to start
        // the process. The fix sets it to false and handles stdin/output
        // explicitly.
        var psi = TlsCertificateAuthority.BuildMacInstallPsi("/tmp/ca.pem");

        psi.FileName.Should().Be("security");
        psi.Arguments.Should().Contain("add-trusted-cert");
        psi.Arguments.Should().Contain("-d");       // add to user cert pool
        psi.Arguments.Should().Contain("-p ssl");   // trust for SSL
        psi.Arguments.Should().Contain("/Library/Keychains/System.keychain");
        psi.Arguments.Should().Contain("/tmp/ca.pem");
        psi.UseShellExecute.Should().BeFalse(
            "UseShellExecute=true on macOS breaks Process.Start in .NET 8 — the v0.3 " +
            "implementation had this bug and would have failed the first macOS CI run.");
        psi.CreateNoWindow.Should().BeTrue();
    }

    [Fact]
    public void Linux_install_psi_does_NOT_use_shell_execute()
    {
        // BUG FIX verification: same as the macOS branch — previously used
        // UseShellExecute=true on Linux too.
        var psi = TlsCertificateAuthority.BuildLinuxNssInstallPsi(
            "/usr/bin/certutil", "/home/u/ca.pem");

        psi.FileName.Should().Be("/usr/bin/certutil");
        psi.Arguments.Should().Contain("-d sql:$HOME/.pki/nssdb");
        psi.Arguments.Should().Contain("-A");
        psi.Arguments.Should().Contain("-t C,,");
        psi.Arguments.Should().Contain("-n \"ZeroBrowser TLS CA\"");
        psi.Arguments.Should().Contain("-i \"/home/u/ca.pem\"");
        psi.UseShellExecute.Should().BeFalse(
            "Same .NET 8 macOS/Linux UseShellExecute incompatibility as the macOS branch.");
    }

    [Fact]
    public void Linux_install_psi_uses_file_path_argument_not_stdin()
    {
        // BUG FIX verification: previously the Linux branch tried to use
        // RedirectStandardInput=true (piping the PEM via stdin) combined
        // with UseShellExecute=true, which throws an InvalidOperationException
        // at Process.Start time on .NET 8 Linux. The fix passes the file path
        // via the -i flag instead, so no stdin redirection is needed.
        var psi = TlsCertificateAuthority.BuildLinuxNssInstallPsi(
            "/usr/bin/certutil", "/home/u/ca.pem");

        psi.RedirectStandardInput.Should().BeFalse(
            "PEM is now passed via -i <file>; stdin redirection is no longer needed " +
            "and would conflict with UseShellExecute=false on .NET 8 Linux.");
    }

    // ─── CaTrustStoreOpResult ────────────────────────────────────────────────

    [Fact]
    public void CaTrustStoreOpResult_Ok_is_true_for_success()
    {
        CaTrustStoreOpResult.Success("ok").Ok.Should().BeTrue();
    }

    [Fact]
    public void CaTrustStoreOpResult_Ok_is_false_for_unsupported()
    {
        // The whole point of the new type: macOS/Linux revoke now returns
        // UnsupportedOnPlatform (NOT a generic "false bool") so the UI can
        // show "not implemented — remove manually" instead of silently
        // looking like the revoke succeeded.
        CaTrustStoreOpResult.Unsupported("no").Ok.Should().BeFalse();
        CaTrustStoreOpResult.Failed("no").Ok.Should().BeFalse();
        CaTrustStoreOpResult.PrerequisiteMissing("no").Ok.Should().BeFalse();
    }

    [Fact]
    public void CaTrustStoreOpResult_carries_reason_for_ui()
    {
        var result = CaTrustStoreOpResult.Unsupported(
            "macOS: revocation is not automated. Open Keychain Access...");

        result.Status.Should().Be(CaTrustStoreOpStatus.UnsupportedOnPlatform);
        result.Reason.Should().Contain("Keychain Access");
    }

    // ─── ExecutablePermissions ──────────────────────────────────────────────

    [Fact]
    public void EnsureUserExecutable_is_noop_for_nonexistent_file()
    {
        // Defensive: don't throw if the binary path doesn't exist.
        var act = () => ExecutablePermissions.EnsureUserExecutable("C:\\does\\not\\exist.exe");
        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureUserExecutable_is_noop_on_windows()
    {
        // On Windows there's no POSIX mode bit to set — and File.SetUnixFileMode
        // throws on Windows. The helper must be a no-op here so that callers
        // don't need to wrap it in a try/catch themselves.
        var temp = Path.GetTempFileName();
        try
        {
            if (!OperatingSystem.IsWindows())
                return; // skip — this assertion is Windows-specific
            var act = () => ExecutablePermissions.EnsureUserExecutable(temp);
            act.Should().NotThrow();
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    [Fact]
    public void EnsureUserExecutable_is_noop_when_path_is_null_or_empty()
    {
        var act1 = () => ExecutablePermissions.EnsureUserExecutable("");
        var act2 = () => ExecutablePermissions.EnsureUserExecutable(null!);
        act1.Should().NotThrow();
        act2.Should().NotThrow();
    }

    // ─── Revoke branching (testable via the public async method) ─────────────

    [Fact]
    public async Task Revoke_on_macos_returns_unsupported_with_manual_instructions()
    {
        if (!OperatingSystem.IsMacOS())
            return; // branching logic on the actual platform only

        var result = await TlsCertificateAuthority.RevokeFromTrustStoreAsync();

        result.Status.Should().Be(CaTrustStoreOpStatus.UnsupportedOnPlatform);
        result.Reason.Should().Contain("Keychain Access",
            "the user MUST be told how to remove the CA manually");
        result.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task Revoke_on_linux_returns_unsupported_with_manual_instructions()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var result = await TlsCertificateAuthority.RevokeFromTrustStoreAsync();

        result.Status.Should().Be(CaTrustStoreOpStatus.UnsupportedOnPlatform);
        result.Reason.Should().Contain("certutil",
            "the user MUST be told the manual certutil -D command");
        result.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task Install_on_linux_without_certutil_returns_PrerequisiteMissing()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Force PATH to an empty directory so certutil cannot be found,
        // regardless of what's installed on this CI runner. This is the
        // "fresh ubuntu-latest container without libnss3-tools" case.
        var emptyDir = Path.Combine(Path.GetTempPath(), "zb-no-certutil-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);

        var prevPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", emptyDir);
        try
        {
            // Make sure the CA file "exists" by writing a placeholder to its
            // expected location — otherwise InstallToTrustStoreAsync returns
            // Failed("not generated") before we ever get to the certutil check.
            var dir = Path.GetDirectoryName(TlsCertificateAuthority.CertPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(TlsCertificateAuthority.CertPath, "placeholder-cert");

            var result = await TlsCertificateAuthority.InstallToTrustStoreAsync();

            result.Status.Should().Be(CaTrustStoreOpStatus.PrerequisiteMissing);
            result.Reason.Should().Contain("libnss3-tools",
                "user must be told the exact package to install");
            result.Ok.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", prevPath);
            try { Directory.Delete(emptyDir, recursive: true); } catch { }
            try { File.Delete(TlsCertificateAuthority.CertPath); } catch { }
        }
    }
}
