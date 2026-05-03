using FluentAssertions;
using Xunit;
using ZeroBrowser.Core.Util;

namespace ZeroBrowser.Tests;

public class CookieImporterTests
{
    [Fact]
    public void Parses_puppeteer_style_json_array()
    {
        var input = """
        [
          {
            "name":"session", "value":"abc123",
            "domain":".example.com", "path":"/",
            "expires": 1735689600, "httpOnly": true, "secure": true, "sameSite":"Lax"
          },
          {
            "name":"theme", "value":"dark",
            "domain":"example.com", "path":"/"
          }
        ]
        """;
        var r = CookieImporter.Parse(input);
        r.Format.Should().Be("json");
        r.Cookies.Should().HaveCount(2);
        r.Warnings.Should().BeEmpty();

        r.Cookies[0].Name.Should().Be("session");
        r.Cookies[0].Value.Should().Be("abc123");
        r.Cookies[0].Domain.Should().Be(".example.com");
        r.Cookies[0].HttpOnly.Should().BeTrue();
        r.Cookies[0].Secure.Should().BeTrue();
        r.Cookies[0].SameSite.Should().Be("Lax");
        r.Cookies[0].ExpiresUnix.Should().Be(1735689600);
    }

    [Fact]
    public void Parses_editthiscookie_expirationDate_field()
    {
        // EditThisCookie uses "expirationDate" (number, possibly fractional)
        var input = """[ { "name":"x","value":"y","domain":"a.com","path":"/","expirationDate":1700000000.5 } ]""";
        var r = CookieImporter.Parse(input);
        r.Cookies.Should().HaveCount(1);
        r.Cookies[0].ExpiresUnix.Should().Be(1700000000);
    }

    [Fact]
    public void Reports_per_cookie_warnings_without_aborting()
    {
        var input = """
        [
          { "name": "good","value":"v","domain":"a.com","path":"/" },
          { "name": "bad-no-value","domain":"a.com" }
        ]
        """;
        var r = CookieImporter.Parse(input);
        r.Cookies.Should().HaveCount(1);
        r.Warnings.Should().HaveCount(1);
        r.Warnings[0].Should().Contain("Cookie #2");
    }

    [Fact]
    public void Parses_netscape_format()
    {
        var input = "# Netscape HTTP Cookie File\n" +
                    "# domain\tflag\tpath\tsecure\texpires\tname\tvalue\n" +
                    ".example.com\tTRUE\t/\tFALSE\t1735689600\tsession\tabc123\n" +
                    ".example.com\tTRUE\t/\tTRUE\t0\ttheme\tdark\n";
        var r = CookieImporter.Parse(input);
        r.Format.Should().Be("netscape");
        r.Cookies.Should().HaveCount(2);
        r.Cookies[0].Name.Should().Be("session");
        r.Cookies[0].Domain.Should().Be(".example.com");
        r.Cookies[0].Path.Should().Be("/");
        r.Cookies[0].Secure.Should().BeFalse();
        r.Cookies[0].ExpiresUnix.Should().Be(1735689600);
        r.Cookies[1].Name.Should().Be("theme");
        r.Cookies[1].Secure.Should().BeTrue();
        r.Cookies[1].ExpiresUnix.Should().BeNull();  // 0 = session
    }

    [Fact]
    public void Rejects_unknown_format()
    {
        Action act = () => CookieImporter.Parse("this is just plain text");
        act.Should().Throw<FormatException>();
    }
}
