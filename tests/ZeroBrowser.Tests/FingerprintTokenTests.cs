using System.Text;
using FluentAssertions;
using Xunit;
using ZeroBrowser.Core.Fingerprint;
using ZeroBrowser.Core.Models;

namespace ZeroBrowser.Tests;

public class FingerprintTokenTests
{
    private readonly FingerprintGenerator _generator = new();

    // ------------------------------------------------------------------
    // Round-trip
    // ------------------------------------------------------------------

    [Fact]
    public void Token_round_trips_seed_os_and_rotation()
    {
        var token = FingerprintTokenCodec.Generate("round-trip-seed", OperatingSystemKind.MacOS, 30);
        var parsed = FingerprintTokenCodec.Parse(token);

        parsed.Should().NotBeNull();
        parsed!.Seed.Should().Be("round-trip-seed");
        parsed.PinnedOs.Should().Be(OperatingSystemKind.MacOS);
        parsed.RotationDays.Should().Be(30);
        parsed.GeneratedAt.Should().NotBeNull();
        parsed.Version.Should().Be(1);
    }

    [Fact]
    public void Token_without_options_round_trips_with_nulls()
    {
        var token = FingerprintTokenCodec.Generate("bare-seed");
        var parsed = FingerprintTokenCodec.Parse(token);

        parsed.Should().NotBeNull();
        parsed!.Seed.Should().Be("bare-seed");
        parsed.PinnedOs.Should().BeNull();
        parsed.RotationDays.Should().Be(0);
    }

    [Fact]
    public void End_to_end_imported_token_clones_the_exact_fingerprint()
    {
        // The core promise of the token format: instance B can import a token
        // from instance A and produce the identical fingerprint.
        var original = _generator.Generate("identity-clone-seed", OperatingSystemKind.Windows11);

        var token = FingerprintTokenCodec.Generate(original.Seed, OperatingSystemKind.Windows11);
        var imported = FingerprintTokenCodec.Parse(token)!;
        var clone = new FingerprintGenerator(new FingerprintGeneratorOptions { PinnedOs = imported.PinnedOs })
            .Generate(imported.Seed);

        clone.UserAgent.Should().Be(original.UserAgent);
        clone.Timezone.Should().Be(original.Timezone);
        clone.WebGlRenderer.Should().Be(original.WebGlRenderer);
        clone.ScreenWidth.Should().Be(original.ScreenWidth);
        clone.HardwareConcurrency.Should().Be(original.HardwareConcurrency);
        clone.CanvasNoiseSeed.Should().Be(original.CanvasNoiseSeed);
    }

    [Fact]
    public void Tokens_are_unique_per_generation_even_for_same_input()
    {
        // Random key+nonce per generation → two exports of the same profile
        // must never share ciphertext (non-reusable identifier).
        var t1 = FingerprintTokenCodec.Generate("same-seed");
        var t2 = FingerprintTokenCodec.Generate("same-seed");

        t1.Should().NotBe(t2);
        FingerprintTokenCodec.Parse(t1)!.Seed.Should().Be(FingerprintTokenCodec.Parse(t2)!.Seed);
    }

    [Fact]
    public void GenerateRandom_produces_distinct_random_seeds()
    {
        var a = FingerprintTokenCodec.Parse(FingerprintTokenCodec.GenerateRandom())!;
        var b = FingerprintTokenCodec.Parse(FingerprintTokenCodec.GenerateRandom())!;

        a.Seed.Should().NotBe(b.Seed);
        a.Seed.Should().HaveLength(32); // Guid "N" format
    }

    // ------------------------------------------------------------------
    // Tamper detection / integrity
    // ------------------------------------------------------------------

    private static string FlipByteInPart(string token, int partIndex)
    {
        var parts = token.Split('|');
        var bytes = Convert.FromBase64String(parts[partIndex]).ToArray();
        bytes[0] ^= 0xFF;
        parts[partIndex] = Convert.ToBase64String(bytes);
        return string.Join('|', parts);
    }

    [Theory]
    [InlineData(0)] // key tampered → auth fail or wrong plaintext
    [InlineData(1)] // payload tampered → GCM tag mismatch
    [InlineData(2)] // nonce tampered → GCM tag mismatch
    public void Tampered_tokens_fail_to_parse(int partIndex)
    {
        var token = FingerprintTokenCodec.Generate("tamper-me", OperatingSystemKind.Linux, 7);
        var tampered = FlipByteInPart(token, partIndex);

        FingerprintTokenCodec.Parse(tampered).Should().BeNull(
            $"flipping a byte in part {partIndex} must break AES-GCM authentication");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    [InlineData("a|b|c|d")]
    [InlineData("|||||")]
    [InlineData("###|###|###|zz|1")]
    public void Malformed_tokens_return_null(string input)
    {
        FingerprintTokenCodec.Parse(input).Should().BeNull();
    }

    [Fact]
    public void Oversized_payload_is_rejected_before_decryption()
    {
        // >10 KB encrypted part must be rejected without attempting decrypt
        // (memory-bomb guard for crafted tokens).
        var big = new string('A', 20_000);
        var bomb = $"{Convert.ToBase64String(new byte[32])}|{big}|{Convert.ToBase64String(new byte[12])}|00|1";

        FingerprintTokenCodec.Parse(bomb).Should().BeNull();
    }

    [Fact]
    public void Unknown_version_still_parses_when_structure_valid()
    {
        // Forward-compat: version is informational; v99 tokens from the future
        // keep working as long as crypto layout is unchanged.
        var token = FingerprintTokenCodec.Generate("future-seed");
        var bumped = string.Join('|', token.Split('|')[..4].Append("99"));

        FingerprintTokenCodec.Parse(bumped)!.Version.Should().Be(99);
    }

    // ------------------------------------------------------------------
    // Structural checks
    // ------------------------------------------------------------------

    [Fact]
    public void Token_has_five_pipe_separated_base64_parts()
    {
        var token = FingerprintTokenCodec.Generate("structure-check");

        token.Split('|').Should().HaveCount(5);
        Convert.FromBase64String(token.Split('|')[0]).Should().HaveCount(32); // AES-256 key
        Convert.FromBase64String(token.Split('|')[2]).Should().HaveCount(12); // GCM nonce
        foreach (var part in token.Split('|')[..3])
        {
            var act = () => Convert.FromBase64String(part);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Flags_reflect_embedded_options()
    {
        var bare = FingerprintTokenCodec.Generate("f1").Split('|')[3];
        var withOs = FingerprintTokenCodec.Generate("f2", OperatingSystemKind.MacOS).Split('|')[3];
        var withRotation = FingerprintTokenCodec.Generate("f3", null, 14).Split('|')[3];
        var withBoth = FingerprintTokenCodec.Generate("f4", OperatingSystemKind.Linux, 14).Split('|')[3];

        Convert.ToByte(bare, 16).Should().Be(0x00);
        (Convert.ToByte(withOs, 16) & 0x01).Should().Be(0x01);
        (Convert.ToByte(withRotation, 16) & 0x08).Should().Be(0x08);
        (Convert.ToByte(withBoth, 16) & 0x09).Should().Be(0x09);
    }

    [Fact]
    public void LooksLikeToken_accepts_token_shape_only()
    {
        FingerprintTokenCodec.LooksLikeToken(FingerprintTokenCodec.Generate("looks")).Should().BeTrue();
        FingerprintTokenCodec.LooksLikeToken("a|b|c|d|e").Should().BeTrue();      // shape ok, garbage inside
        FingerprintTokenCodec.LooksLikeToken("a|b|c").Should().BeFalse();
        FingerprintTokenCodec.LooksLikeToken("").Should().BeFalse();
        FingerprintTokenCodec.LooksLikeToken(null!).Should().BeFalse();
    }
}
