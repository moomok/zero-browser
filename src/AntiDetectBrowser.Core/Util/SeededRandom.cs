using System.Security.Cryptography;
using System.Text;

namespace AntiDetectBrowser.Core.Util;

/// <summary>
/// Deterministic random number generator seeded from a string.
/// Same seed -> same sequence of values, regardless of platform.
/// </summary>
public sealed class SeededRandom
{
    private uint _state;

    public SeededRandom(string seed)
    {
        // Use SHA-256(seed) as a stable hash; take first 4 bytes as uint state.
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(seed), hash);
        _state = BitConverter.ToUInt32(hash[..4]);
        if (_state == 0) _state = 0xDEADBEEFu;
    }

    public SeededRandom(uint seed)
    {
        _state = seed == 0 ? 0xDEADBEEFu : seed;
    }

    /// <summary>xorshift32 — fast, deterministic, good enough for fingerprint use cases.</summary>
    public uint NextUInt()
    {
        uint x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    public int Next(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        uint range = (uint)(maxExclusive - minInclusive);
        return minInclusive + (int)(NextUInt() % range);
    }

    public T Pick<T>(IReadOnlyList<T> items)
    {
        if (items.Count == 0) throw new ArgumentException("items is empty", nameof(items));
        return items[Next(0, items.Count)];
    }

    public double NextDouble() => NextUInt() / (double)uint.MaxValue;

    /// <summary>Stable derived sub-seed for orthogonal noise streams (canvas, audio, fonts).</summary>
    public uint Derive(string label)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{_state}|{label}"), hash);
        return BitConverter.ToUInt32(hash[..4]);
    }
}
