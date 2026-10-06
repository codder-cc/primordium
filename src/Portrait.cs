using System;
using Godot;

namespace Primordium;

// Genome -> picture via a tiny CPPN (compositional pattern-producing network).
// Weights are derived from byte *pairs* of the genome, so a point mutation or an
// insertion only nudges a few weights: relatives look alike, lineages drift visibly.
public static class Portrait
{
    const int Hidden = 8, NW = Hidden * 4 + 4 * Hidden + Hidden;

    static float HF(int bigram, int j)
    {
        uint h = (uint)bigram * 2654435761u ^ (uint)(j + 1) * 2246822519u;
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;
        return (h & 0xFFFF) / 32767.5f - 1f;
    }

    static float[] Weights(byte[] g)
    {
        var w = new float[NW];
        int n = g.Length;
        for (int i = 0; i < n; i++)
        {
            int bg = (g[i] << 8) | g[(i + 1) % n];
            for (int j = 0; j < NW; j++) w[j] += HF(bg, j);
        }
        float k = 2.2f / MathF.Sqrt(n);
        for (int j = 0; j < NW; j++) w[j] *= k;
        return w;
    }

    static float Sig(float v) => 1f / (1f + MathF.Exp(-v));

    public static Image Render(byte[] g, int size)
    {
        var w = Weights(g);
        var mask = new bool[size * size];
        var col = new Color[size * size];
        Span<float> h = stackalloc float[Hidden];
        Span<float> o = stackalloc float[4];
        int filled = 0;

        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                float x = (px + 0.5f) / size * 2 - 1, y = (py + 0.5f) / size * 2 - 1;
                float xs = MathF.Abs(x), r = MathF.Sqrt(x * x + y * y);
                for (int k = 0; k < Hidden; k++)
                {
                    float v = 2 * (w[k * 4] * xs + w[k * 4 + 1] * y + w[k * 4 + 2] * r) + w[k * 4 + 3];
                    h[k] = ((int)(MathF.Abs(w[64 + k]) * 3) % 4) switch
                    {
                        0 => MathF.Sin(v * 2),
                        1 => MathF.Tanh(v),
                        2 => MathF.Exp(-v * v),
                        _ => MathF.Abs(v) - 0.6f,
                    };
                }
                o.Clear();
                for (int c = 0; c < 4; c++)
                    for (int k = 0; k < Hidden; k++) o[c] += w[32 + c * Hidden + k] * h[k];

                int i = py * size + px;
                mask[i] = o[3] + 0.6f - r * r * 2.2f > 0 && r < 0.98f;
                var cc = new Color(Sig(o[0] * 1.6f), Sig(o[1] * 1.6f), Sig(o[2] * 1.6f));
                col[i] = Color.FromHsv(cc.H, MathF.Min(1, cc.S * 1.6f + 0.2f), MathF.Max(0.45f, cc.V));
                if (mask[i]) filled++;
            }

        if (filled < size * size * 0.08f)
            for (int i = 0; i < mask.Length; i++)
            {
                float x = (i % size + 0.5f) / size * 2 - 1, y = (i / size + 0.5f) / size * 2 - 1;
                mask[i] = x * x + y * y < 0.3f;
            }

        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                int i = py * size + px;
                if (!mask[i]) { img.SetPixel(px, py, new Color(0, 0, 0, 0)); continue; }
                bool edge = px == 0 || py == 0 || px == size - 1 || py == size - 1
                    || !mask[i - 1] || !mask[i + 1] || !mask[i - size] || !mask[i + size];
                img.SetPixel(px, py, edge ? col[i].Darkened(0.6f) : col[i]);
            }
        return img;
    }
}
