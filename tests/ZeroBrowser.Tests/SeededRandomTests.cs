using ZeroBrowser.Core.Util;
using FluentAssertions;
using Xunit;

namespace ZeroBrowser.Tests;

public class SeededRandomTests
{
    [Fact]
    public void Same_seed_produces_identical_sequence()
    {
        var a = new SeededRandom("hello");
        var b = new SeededRandom("hello");

        for (int i = 0; i < 100; i++)
            a.NextUInt().Should().Be(b.NextUInt());
    }

    [Fact]
    public void Different_seeds_produce_different_sequences()
    {
        var a = new SeededRandom("alpha");
        var b = new SeededRandom("beta");

        var seqA = Enumerable.Range(0, 10).Select(_ => a.NextUInt()).ToArray();
        var seqB = Enumerable.Range(0, 10).Select(_ => b.NextUInt()).ToArray();

        seqA.Should().NotEqual(seqB);
    }

    [Fact]
    public void Pick_returns_value_from_collection()
    {
        var rnd = new SeededRandom("pick");
        var items = new[] { "x", "y", "z" };
        for (int i = 0; i < 50; i++)
        {
            items.Should().Contain(rnd.Pick(items));
        }
    }

    [Fact]
    public void Derive_is_stable_across_calls_with_same_state()
    {
        // Different Derive labels yield different sub-seeds — but the function itself is deterministic.
        var a = new SeededRandom("derive-test");
        var canvas1 = a.Derive("canvas");

        var b = new SeededRandom("derive-test");
        var canvas2 = b.Derive("canvas");
        var audio2  = b.Derive("audio");

        canvas1.Should().Be(canvas2);
        canvas1.Should().NotBe(audio2);
    }
}
