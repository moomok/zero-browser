using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Xunit;
using ZeroBrowser.Browser;
using ZeroBrowser.Browser.Tor;
using ZeroBrowser.Core.Models;

namespace ZeroBrowser.Tests;

public class TorTests
{
    // ------------------------------------------------------------------
    // Circuit isolation credentials
    // ------------------------------------------------------------------

    private static Profile MakeProfile(string id, string seed) => new()
    {
        Id = Guid.Parse(id),
        Name = "t",
        FingerprintSeed = seed,
        StoragePath = "/tmp/zb-test"
    };

    [Fact]
    public void Isolation_credentials_are_deterministic_per_profile()
    {
        var id = "11111111-1111-1111-1111-111111111111";
        var (u1, p1) = PuppeteerBrowserLauncher.TorIsolationCredentials(MakeProfile(id, "seed-a"));
        var (u2, p2) = PuppeteerBrowserLauncher.TorIsolationCredentials(MakeProfile(id, "seed-a"));

        u1.Should().Be(u2);
        p1.Should().Be(p2);
    }

    [Fact]
    public void Isolation_credentials_differ_between_profiles_and_seeds()
    {
        var (ua, pa) = PuppeteerBrowserLauncher.TorIsolationCredentials(
            MakeProfile("22222222-2222-2222-2222-222222222222", "seed-a"));
        var (ub, pb) = PuppeteerBrowserLauncher.TorIsolationCredentials(
            MakeProfile("33333333-3333-3333-3333-333333333333", "seed-a"));
        // Same profile, rotated seed → new isolation key.
        var (_, pc) = PuppeteerBrowserLauncher.TorIsolationCredentials(
            MakeProfile("22222222-2222-2222-2222-222222222222", "seed-b"));

        ua.Should().NotBe(ub);
        pa.Should().NotBe(pb);
        pa.Should().NotBe(pc);
    }

    [Fact]
    public void Isolation_credentials_are_valid_socks5_auth_strings()
    {
        var (user, pass) = PuppeteerBrowserLauncher.TorIsolationCredentials(
            MakeProfile("44444444-4444-4444-4444-444444444444", "seed"));

        user.Should().StartWith("zb-");
        user.Length.Should().BeLessThanOrEqualTo(255);   // RFC 1929 ulen is a single byte
        user.Should().MatchRegex(@"^zb-[0-9a-f]{32}$");
        pass.Should().HaveLength(24);                    // 96-bit hex
        pass.Should().MatchRegex(@"^[0-9a-f]{24}$");
    }

    // ------------------------------------------------------------------
    // Bootstrap log parsing
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Nov 05 12:00:00.000 [notice] Bootstrapped 5% (conn): Connecting to relay", 5)]
    [InlineData("Nov 05 12:00:01.000 [notice] Bootstrapped 100% (done): Done", 100)]
    [InlineData("[notice] Bootstrapped 85% (finishing_circuit_create): Finishing handshake with first hop of internal circuit", 85)]
    public void Bootstrap_parser_reads_percent_from_tor_log_lines(string line, int expected)
    {
        var m = TorManager.BootstrappedLine().Match(line);
        m.Success.Should().BeTrue();
        int.Parse(m.Groups[1].Value).Should().Be(expected);
    }

    [Fact]
    public void Bootstrap_parser_extracts_summary_text()
    {
        var m = TorManager.BootstrappedLine().Match("[notice] Bootstrapped 10% (finishing_load_map): Loading state");
        m.Success.Should().BeTrue();
        m.Groups[2].Value.Should().Be("finishing_load_map");
    }

    [Theory]
    [InlineData("Nov 05 12:00:00.000 [notice] Opening Socks listener on 127.0.0.1:9050")]
    [InlineData("")]
    [InlineData("Bootstrapped without percent")]
    public void Bootstrap_parser_ignores_non_bootstrap_lines(string line)
    {
        TorManager.BootstrappedLine().IsMatch(line).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Binary discovery
    // ------------------------------------------------------------------

    [Fact]
    public void FindTorBinary_returns_existing_path_or_null()
    {
        var path = TorManager.FindTorBinary();
        // Either nothing installed in this environment (null) or an existing file.
        if (path is not null) File.Exists(path).Should().BeTrue($"{path} was returned as the tor binary");
    }

    [Fact]
    public void FindTorBinary_honours_ZB_TOR_PATH_env_var()
    {
        var fake = Path.Combine(Path.GetTempPath(), $"zb-tor-fake-{Guid.NewGuid():N}");
        File.WriteAllText(fake, "#!/bin/sh\n");

        try
        {
            Environment.SetEnvironmentVariable("ZB_TOR_PATH", fake);
            TorManager.FindTorBinary().Should().Be(fake);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZB_TOR_PATH", null);
            File.Delete(fake);
        }
    }

    // ------------------------------------------------------------------
    // SOCKS5 protocol framing
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0x00, "succeeded")]
    [InlineData(0x05, "connection refused")]
    [InlineData(0x08, "address type not supported")]
    public void Reply_names_are_human_readable(byte rep, string name)
    {
        Socks5Client.ReplyName(rep).Should().Be(name);
    }

    [Fact]
    public async Task ReadExactlyAsync_reads_exact_byte_count_across_partial_chunks()
    {
        using var ms = new MemoryStream(new byte[] { 0x05, 0x02 });
        var buf = await Socks5Client.ReadExactlyAsync(ms, 2, CancellationToken.None);
        buf.Should().Equal(0x05, 0x02);
    }

    [Fact]
    public async Task ReadExactlyAsync_throws_on_truncated_stream()
    {
        using var ms = new MemoryStream(new byte[] { 0x05 });
        await Assert.ThrowsAnyAsync<EndOfStreamException>(
            () => Socks5Client.ReadExactlyAsync(ms, 2, CancellationToken.None));
    }

    // ------------------------------------------------------------------
    // Control protocol helpers
    // ------------------------------------------------------------------

    [Fact]
    public void TorReply_flags_ok_on_250()
    {
        new TorReply(true, "250 OK").IsOk.Should().BeTrue();
        new TorReply(false, "552 Unrecognized key").IsOk.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // ProxyType plumbing
    // ------------------------------------------------------------------

    [Fact]
    public void Proxy_type_tor_is_a_defined_value()
    {
        ((ProxyType)Enum.Parse(typeof(ProxyType), "Tor")).Should().Be(ProxyType.Tor);
        Enum.IsDefined(ProxyType.Tor).Should().BeTrue();
    }
}
