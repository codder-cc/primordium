using System;

namespace Primordium;

public struct Rgb
{
    public float R, G, B;
    public Rgb(float r, float g, float b) { R = r; G = g; B = b; }

    public static Rgb Hsv(float h, float s, float v)
    {
        h = (h % 1f + 1f) % 1f * 6f;
        int i = (int)h;
        float f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return i switch
        {
            0 => new(v, t, p),
            1 => new(q, v, p),
            2 => new(p, v, t),
            3 => new(p, q, v),
            4 => new(t, p, v),
            _ => new(v, p, q),
        };
    }

    public Rgb Lerp(Rgb o, float k) => new(R + (o.R - R) * k, G + (o.G - G) * k, B + (o.B - B) * k);
    public Rgb Mul(float k) => new(R * k, G * k, B * k);
}

// Stateless hashing, so parallel loops can draw "random" numbers without sharing a generator.
public static class Hash32
{
    public static uint U(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d;
        x ^= x >> 15; x *= 0x846ca68b;
        x ^= x >> 16;
        return x;
    }

    public static uint U(int a, int b) => U((uint)a * 0x9E3779B1u ^ U((uint)b + 0x632BE5ABu));
    public static uint U(int a, int b, int c) => U(U(a, b) ^ (uint)c * 0x85EBCA77u);
    public static float F(int a, int b) => (U(a, b) >> 8) * (1f / 16777216f);
    public static float F(int a, int b, int c) => (U(a, b, c) >> 8) * (1f / 16777216f);
}

// Value noise that wraps around in x (the world is a cylinder).
public static class Noise
{
    static float S(float t) => t * t * (3 - 2 * t);
    static int Wrap(int v, int p) => ((v % p) + p) % p;

    public static float Value(int seed, float x, float y, int px)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = S(x - x0), fy = S(y - y0);
        int xa = Wrap(x0, px), xb = Wrap(x0 + 1, px);
        float a = Hash32.F(seed, xa, y0), b = Hash32.F(seed, xb, y0);
        float c = Hash32.F(seed, xa, y0 + 1), d = Hash32.F(seed, xb, y0 + 1);
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }

    // u in [0,1) around the cylinder, v in [0,1] pole to pole; aspect = height/width of the map.
    public static float Fbm(int seed, float u, float v, int octaves, int period, float aspect)
    {
        float sum = 0, amp = 1, norm = 0;
        for (int o = 0; o < octaves; o++)
        {
            int p = period << o;
            sum += amp * Value(seed + o * 101, u * p, v * p * aspect, p);
            norm += amp;
            amp *= 0.5f;
        }
        return sum / norm;
    }

    public static float Value3(int seed, float x, float y, float z, int px)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y), z0 = (int)MathF.Floor(z);
        float fx = S(x - x0), fy = S(y - y0), fz = S(z - z0);
        int xa = Wrap(x0, px), xb = Wrap(x0 + 1, px);
        float L(int zz)
        {
            int s = (int)Hash32.U(seed, zz);
            float a = Hash32.F(s, xa, y0), b = Hash32.F(s, xb, y0);
            float c = Hash32.F(s, xa, y0 + 1), d = Hash32.F(s, xb, y0 + 1);
            return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
        }
        float l0 = L(z0), l1 = L(z0 + 1);
        return l0 + (l1 - l0) * fz;
    }
}
