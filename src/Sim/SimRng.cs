using System;
using System.IO;

namespace Primordium;

// The simulation's random numbers: xoshiro256** (Blackman & Vigna), seeded through splitmix64.
// Small, fast, and — unlike System.Random — its whole state is four numbers that a save file can
// keep, so a loaded world draws exactly what the original would have. The API mirrors the parts of
// System.Random the simulation uses. Not thread-safe: every stream has one owner (the main stream
// between ticks, one per agent tile in the agent phase).
public sealed class SimRng
{
    ulong s0, s1, s2, s3;

    public SimRng(long seed) => Reseed(seed);

    // A stream derived from a seed and a stream number (independent-looking streams for tiles).
    public SimRng(long seed, long stream) => Reseed(seed ^ (long)(0x9E3779B97F4A7C15UL * (ulong)(stream + 1)));

    public void Reseed(long seed)
    {
        ulong x = (ulong)seed;
        s0 = SplitMix(ref x); s1 = SplitMix(ref x); s2 = SplitMix(ref x); s3 = SplitMix(ref x);
        if ((s0 | s1 | s2 | s3) == 0) s0 = 1;   // the all-zero state is the generator's only fixed point
    }

    static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextU64()
    {
        ulong result = Rotl(s1 * 5, 7) * 9, t = s1 << 17;
        s2 ^= s0; s3 ^= s1; s1 ^= s2; s0 ^= s3;
        s2 ^= t;
        s3 = Rotl(s3, 45);
        return result;
    }

    // [0, 2^31).
    public int Next() => (int)(NextU64() >> 33);

    // [0, max); 0 when max ≤ 0 (a draw is still made, so the stream advances the same way).
    public int Next(int max)
    {
        ulong r = NextU64() >> 32;
        return max <= 0 ? 0 : (int)((r * (ulong)max) >> 32);
    }

    // [min, max); min when max ≤ min.
    public int Next(int min, int max)
    {
        ulong r = NextU64() >> 32;
        return max <= min ? min : (int)(min + (long)((r * (ulong)((long)max - min)) >> 32));
    }

    // [0, 1) with 53 random bits.
    public double NextDouble() => (NextU64() >> 11) * (1.0 / (1UL << 53));

    // [0, 1) with 24 random bits.
    public float NextSingle() => (NextU64() >> 40) * (1f / (1 << 24));

    public void NextBytes(Span<byte> buffer)
    {
        int i = 0;
        for (; i + 8 <= buffer.Length; i += 8) BitConverter.TryWriteBytes(buffer.Slice(i, 8), NextU64());
        if (i < buffer.Length)
        {
            ulong r = NextU64();
            for (; i < buffer.Length; i++, r >>= 8) buffer[i] = (byte)r;
        }
    }

    public void NextBytes(byte[] buffer) => NextBytes(buffer.AsSpan());

    // The whole state, for saving.
    public (ulong, ulong, ulong, ulong) State
    {
        get => (s0, s1, s2, s3);
        set
        {
            (s0, s1, s2, s3) = value;
            if ((s0 | s1 | s2 | s3) == 0) throw new InvalidDataException("all-zero random state");
        }
    }

    public void Write(BinaryWriter w) { w.Write(s0); w.Write(s1); w.Write(s2); w.Write(s3); }
    public void Read(BinaryReader r) => State = (r.ReadUInt64(), r.ReadUInt64(), r.ReadUInt64(), r.ReadUInt64());
}
