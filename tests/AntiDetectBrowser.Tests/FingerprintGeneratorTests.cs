using AntiDetectBrowser.Core.Fingerprint;
using AntiDetectBrowser.Core.Models;
using FluentAssertions;
using Xunit;

namespace AntiDetectBrowser.Tests;

public class FingerprintGeneratorTests
{
    private readonly FingerprintGenerator _generator = new();

    [Fact]
    public void Same_seed_produces_identical_fingerprint()
    {
        var a = _generator.Generate("alice@example.com");
        var b = _generator.Generate("alice@example.com");

        a.Should().BeEquivalentTo(b);
    }

    [Fact]
    public void Different_seeds_produce_different_fingerprints()
    {
        var a = _generator.Generate("seed-one");
        var b = _generator.Generate("seed-two");

        // Most fields should differ. Spot-check a few.
        var differs =
            a.UserAgent != b.UserAgent ||
            a.WebGlRenderer != b.WebGlRenderer ||
            a.Timezone != b.Timezone ||
            a.ScreenWidth != b.ScreenWidth ||
            a.HardwareConcurrency != b.HardwareConcurrency;
        differs.Should().BeTrue("two distinct seeds should produce visibly different fingerprints");
    }

    [Theory]
    [InlineData(OperatingSystemKind.Windows10)]
    [InlineData(OperatingSystemKind.Windows11)]
    [InlineData(OperatingSystemKind.MacOS)]
    [InlineData(OperatingSystemKind.Linux)]
    public void Pinned_OS_always_produces_matching_platform_strings(OperatingSystemKind os)
    {
        var generator = new FingerprintGenerator(new FingerprintGeneratorOptions { PinnedOs = os });
        var fp = generator.Generate("seed-" + os);

        fp.Os.Should().Be(os);
        fp.SecChUaPlatform.Should().BeOneOf("Windows", "macOS", "Linux");

        var (platform, secChUa, _, uaOs) = (fp.Platform, fp.SecChUaPlatform, fp.OsCpu, fp.OsVersion);
        switch (os)
        {
            case OperatingSystemKind.Windows10:
            case OperatingSystemKind.Windows11:
                platform.Should().Be("Win32");
                secChUa.Should().Be("Windows");
                uaOs.Should().Contain("Windows NT");
                break;
            case OperatingSystemKind.MacOS:
                platform.Should().Be("MacIntel");
                secChUa.Should().Be("macOS");
                uaOs.Should().Contain("Mac OS X");
                break;
            case OperatingSystemKind.Linux:
                platform.Should().Be("Linux x86_64");
                secChUa.Should().Be("Linux");
                uaOs.Should().Contain("Linux");
                break;
        }
    }

    [Fact]
    public void UserAgent_contains_chrome_version()
    {
        var fp = _generator.Generate("any-seed");
        fp.UserAgent.Should().StartWith("Mozilla/5.0");
        fp.UserAgent.Should().Contain($"Chrome/{fp.BrowserVersion}");
        fp.UserAgent.Should().EndWith("Safari/537.36");
    }

    [Fact]
    public void Languages_match_timezone_locale()
    {
        var fp = _generator.Generate("locale-test-seed");
        fp.Languages.Should().NotBeEmpty();
        fp.PrimaryLanguage.Should().Be(fp.Languages[0]);
        fp.AcceptLanguage.Should().Contain(fp.PrimaryLanguage);
    }

    [Fact]
    public void Geolocation_is_within_realistic_bounds()
    {
        var fp = _generator.Generate("geo-seed");
        fp.GeoLatitude.Should().BeInRange(-90, 90);
        fp.GeoLongitude.Should().BeInRange(-180, 180);
        fp.GeoAccuracy.Should().BeInRange(10, 200);
    }

    [Fact]
    public void Screen_avail_size_is_smaller_than_full_screen()
    {
        var fp = _generator.Generate("screen-seed");
        fp.AvailHeight.Should().BeLessThan(fp.ScreenHeight);
        fp.AvailWidth.Should().BeLessThanOrEqualTo(fp.ScreenWidth);
    }

    [Fact]
    public void WebGl_combo_is_consistent_with_OS()
    {
        var win = new FingerprintGenerator(new FingerprintGeneratorOptions { PinnedOs = OperatingSystemKind.Windows11 })
            .Generate("win-seed");
        win.WebGlRenderer.Should().Contain("Direct3D11");

        var mac = new FingerprintGenerator(new FingerprintGeneratorOptions { PinnedOs = OperatingSystemKind.MacOS })
            .Generate("mac-seed");
        // macOS combos use either Metal or OpenGL
        (mac.WebGlRenderer.Contains("Metal") || mac.WebGlRenderer.Contains("OpenGL"))
            .Should().BeTrue();
    }

    [Fact]
    public void Generates_at_least_some_fonts()
    {
        var fp = _generator.Generate("font-seed");
        fp.Fonts.Should().NotBeEmpty();
        fp.Fonts.Count.Should().BeGreaterThan(5);
    }

    [Fact]
    public void Hardware_concurrency_is_realistic()
    {
        var fp = _generator.Generate("hw-seed");
        fp.HardwareConcurrency.Should().BeOneOf(2, 4, 8, 12, 16);
    }
}
