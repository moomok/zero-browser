using FluentAssertions;
using Xunit;
using ZeroBrowser.Core.Models;
using ZeroBrowser.Core.Util;

namespace ZeroBrowser.Tests;

public class ProxyImporterTests
{
    [Theory]
    [InlineData("1.2.3.4:8080", "1.2.3.4", 8080, ProxyType.Http, null, null)]
    [InlineData("proxy.example.com:3128", "proxy.example.com", 3128, ProxyType.Http, null, null)]
    [InlineData("https://proxy.example.com:443", "proxy.example.com", 443, ProxyType.Https, null, null)]
    [InlineData("socks5://10.0.0.1:1080", "10.0.0.1", 1080, ProxyType.Socks5, null, null)]
    [InlineData("1.2.3.4:8080:user:pass", "1.2.3.4", 8080, ProxyType.Http, "user", "pass")]
    [InlineData("user:pass@1.2.3.4:8080", "1.2.3.4", 8080, ProxyType.Http, "user", "pass")]
    [InlineData("https://user:pass@proxy.example.com:443", "proxy.example.com", 443, ProxyType.Https, "user", "pass")]
    [InlineData("socks5://user:p@ss@10.0.0.1:1080", "10.0.0.1", 1080, ProxyType.Socks5, "user", "p@ss")]
    public void Parses_known_formats(string line, string host, int port, ProxyType type, string? user, string? pass)
    {
        var result = ProxyImporter.Parse(line);
        result.Failures.Should().BeEmpty();
        result.Proxies.Should().HaveCount(1);
        var p = result.Proxies[0];
        p.Host.Should().Be(host);
        p.Port.Should().Be(port);
        p.Type.Should().Be(type);
        p.Username.Should().Be(user);
        p.Password.Should().Be(pass);
    }

    [Fact]
    public void Skips_blank_lines_and_comments()
    {
        var input = """
            # this is a comment
            // also a comment

            1.2.3.4:8080
            
            5.6.7.8:9090
            """;
        var result = ProxyImporter.Parse(input);
        result.Proxies.Should().HaveCount(2);
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public void Reports_failures_with_line_numbers()
    {
        var input = """
            1.2.3.4:8080
            not-a-proxy
            5.6.7.8:abc
            """;
        var result = ProxyImporter.Parse(input);
        result.Proxies.Should().HaveCount(1);
        result.Failures.Should().HaveCount(2);
        result.Failures[0].LineNumber.Should().Be(2);
        result.Failures[1].LineNumber.Should().Be(3);
    }

    [Fact]
    public void Bulk_import_yields_unique_ids()
    {
        var input = "1.2.3.4:8080\n1.2.3.4:8080\n1.2.3.4:8080";
        var result = ProxyImporter.Parse(input);
        result.Proxies.Should().HaveCount(3);
        result.Proxies.Select(p => p.Id).Distinct().Should().HaveCount(3);
    }

    [Fact]
    public void Rejects_out_of_range_ports()
    {
        var result = ProxyImporter.Parse("1.2.3.4:99999");
        result.Failures.Should().HaveCount(1);
        result.Failures[0].Reason.Should().Contain("range");
    }
}
