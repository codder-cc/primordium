using System;
using System.Collections.Generic;
using Godot;

namespace Primordium;

// The sidebar's icons, drawn in code: thin round strokes on a 24-unit grid, one colour (tinted by the
// state of the entry), scaled as vectors — crisp at any size and on retina screens. Our own drawings.
public static class Icons
{
    // Draws an icon centred at c, `size` pixels across, in colour `fg`; `bg` is what lies under it
    // (for the cut-outs of badges).
    public delegate void Fn(CanvasItem ci, Vector2 c, float size, Color fg, Color bg);

    const float W = 1.35f;   // stroke width in grid units
    static CanvasItem ci;
    static Color fg, bg;

    static void Begin(CanvasItem item, Vector2 c, float size, Color f, Color b)
    {
        ci = item; fg = f; bg = b;
        float s = size / 24f;
        ci.DrawSetTransform(c - new Vector2(12, 12) * s, 0, new Vector2(s, s));
    }

    static void End() => ci.DrawSetTransform(Vector2.Zero, 0, Vector2.One);

    static Vector2 V(float x, float y) => new(x, y);

    // An open polyline with round ends.
    static void L(params Vector2[] p) => Line(fg, W, p);

    static void Line(Color col, float w, params Vector2[] p)
    {
        ci.DrawPolyline(p, col, w, true);
        Cap(p[0], w, col);
        Cap(p[^1], w, col);
        if (p.Length <= 6) for (int i = 1; i < p.Length - 1; i++) Cap(p[i], w, col);   // round corners (curves need none)
    }

    static void Closed(params Vector2[] p)
    {
        var q = new Vector2[p.Length + 1];
        Array.Copy(p, q, p.Length);
        q[^1] = p[0];
        ci.DrawPolyline(q, fg, W, true);
        foreach (var v in p) Cap(v, W, fg);
    }

    // A round end of a stroke `w` wide (plain: the stroke's own smooth edge hides its rim).
    static void Cap(Vector2 c, float w, Color col) => ci.DrawCircle(c, w / 2 - 0.05f, col);

    // A filled, antialiased disc (a thick arc around the full circle).
    static void Disc(Vector2 c, float r, Color col)
    {
        ci.DrawCircle(c, r - 0.3f, col);
        ci.DrawArc(c, r - 0.3f, 0, Mathf.Tau, 24, col, 0.6f, true);   // a smooth rim
    }

    // A dot `d` units across.
    static void Dot(Vector2 c, float d) => Disc(c, d / 2, fg);

    static void Ring(Vector2 c, float r) => ci.DrawArc(c, r, 0, Mathf.Tau, 40, fg, W, true);

    static void Arc(Vector2 c, float r, float a0, float a1)
    {
        ci.DrawArc(c, r, a0, a1, 28, fg, W, true);
        Cap(c + r * Vector2.FromAngle(a0), W, fg);
        Cap(c + r * Vector2.FromAngle(a1), W, fg);
    }

    static Vector2[] Ellipse(Vector2 c, float rx, float ry, float a0 = 0, float a1 = Mathf.Tau, int n = 40)
    {
        var p = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float a = a0 + (a1 - a0) * i / n;
            p[i] = c + new Vector2(MathF.Cos(a) * rx, MathF.Sin(a) * ry);
        }
        return p;
    }

    static Vector2[] Bezier(Vector2 a, Vector2 k, Vector2 b, int n = 14)
    {
        var p = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = (float)i / n, u = 1 - t;
            p[i] = u * u * a + 2 * u * t * k + t * t * b;
        }
        return p;
    }

    static void Poly(Vector2[] p) => ci.DrawPolyline(p, fg, W, true);

    // A leaf between a and b, bulging by `bulge` on both sides.
    static void Leaf(Vector2 a, Vector2 b, float bulge)
    {
        var m = (a + b) / 2;
        var n = (b - a).Orthogonal().Normalized() * bulge;
        var p1 = Bezier(a, m + n, b);
        var p2 = Bezier(b, m - n, a);
        var all = new List<Vector2>(p1);
        all.AddRange(p2[1..]);
        ci.DrawColoredPolygon(all.ToArray(), fg with { A = fg.A * 0.25f });
        Poly(all.ToArray());
    }

    // ---- world ----

    public static void Laws(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        L(V(12, 5), V(12, 20));
        L(V(8, 20), V(16, 20));
        L(V(4.5f, 7), V(19.5f, 7));
        Dot(V(12, 4.2f), 2.4f);
        L(V(4.5f, 7), V(2, 13));
        L(V(4.5f, 7), V(7, 13));
        L(V(19.5f, 7), V(17, 13));
        L(V(19.5f, 7), V(22, 13));
        Poly(Ellipse(V(4.5f, 13), 3, 3, 0, Mathf.Pi, 16));
        Poly(Ellipse(V(19.5f, 13), 3, 3, 0, Mathf.Pi, 16));
        L(V(1.5f, 13), V(7.5f, 13));
        L(V(16.5f, 13), V(22.5f, 13));
        End();
    }

    public static void NewWorld(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        var o = V(11, 11.5f);
        Ring(o, 8.5f);
        Poly(Ellipse(o, 3.6f, 8.5f));
        L(V(2.5f, 11.5f), V(19.5f, 11.5f));
        // a "+" badge cut into the lower right
        Disc(V(18.5f, 18.5f), 5.8f, bg);
        L(V(18.5f, 14.8f), V(18.5f, 22.2f));
        L(V(14.8f, 18.5f), V(22.2f, 18.5f));
        End();
    }

    public static void Saves(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        Closed(V(4, 4), V(16.5f, 4), V(20, 7.5f), V(20, 20), V(4, 20));
        L(V(8, 4), V(8, 8.5f), V(15, 8.5f), V(15, 4));
        L(V(7.5f, 20), V(7.5f, 13.5f), V(16.5f, 13.5f), V(16.5f, 20));
        End();
    }

    public static void Catastrophes(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        var p = new[] { V(13.5f, 2.5f), V(5, 13.5f), V(11.5f, 13.5f), V(10.5f, 21.5f), V(19, 10), V(12.5f, 10) };
        ci.DrawColoredPolygon(p, fg with { A = fg.A * 0.22f });
        Closed(p);
        End();
    }

    // ---- life ----

    public static void Designer(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        // A double helix with its rungs.
        Begin(c, at, size, f, b);
        const int n = 32;
        var s1 = new Vector2[n + 1];
        var s2 = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = (float)i / n, y = 3 + 18 * t, x = 5.5f * MathF.Sin(t * Mathf.Tau * 0.9f + 0.45f);
            s1[i] = V(12 + x, y);
            s2[i] = V(12 - x, y);
        }
        Line(fg, W, s1);
        Line(fg, W, s2);
        foreach (float t in new[] { 0.07f, 0.24f, 0.62f, 0.8f })
        {
            float y = 3 + 18 * t, x = 5.5f * MathF.Sin(t * Mathf.Tau * 0.9f + 0.45f);
            if (MathF.Abs(x) > 2.2f) Line(fg, 1.1f, V(12 - x * 0.7f, y), V(12 + x * 0.7f, y));
        }
        End();
    }

    public static void Tree(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        L(V(12, 21.5f), V(12, 14));
        Poly(Bezier(V(12, 14), V(12, 10), V(6.5f, 8.5f)));
        Poly(Bezier(V(12, 14), V(12, 10), V(17.5f, 8.5f)));
        Poly(Bezier(V(6.5f, 8.5f), V(4.5f, 7.5f), V(4, 4)));
        Poly(Bezier(V(6.5f, 8.5f), V(9, 7), V(9.5f, 4.5f)));
        L(V(17.5f, 8.5f), V(19.5f, 4));
        Dot(V(4, 4), 4.2f);
        Dot(V(9.5f, 4.5f), 4.2f);
        Dot(V(19.5f, 4), 4.2f);
        End();
    }

    public static void Chronicle(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        L(V(12, 7), V(12, 20));
        Poly(Bezier(V(12, 7), V(8, 4.5f), V(3, 5)));
        L(V(3, 5), V(3, 18));
        Poly(Bezier(V(3, 18), V(8, 17.5f), V(12, 20)));
        Poly(Bezier(V(12, 7), V(16, 4.5f), V(21, 5)));
        L(V(21, 5), V(21, 18));
        Poly(Bezier(V(21, 18), V(16, 17.5f), V(12, 20)));
        Line(fg, 1.05f, V(6, 9.2f), V(9, 10));
        Line(fg, 1.05f, V(6, 12.6f), V(9, 13.4f));
        Line(fg, 1.05f, V(15, 10), V(18, 9.2f));
        End();
    }

    public static void Evolution(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        L(V(3, 18.5f), V(9, 12.5f), V(13, 15.5f), V(20.5f, 6.5f));
        L(V(14.5f, 6.5f), V(20.5f, 6.5f), V(20.5f, 12.5f));
        Line(fg with { A = fg.A * 0.45f }, 1.2f, V(3, 21.5f), V(21, 21.5f));
        End();
    }

    public static void Metrics(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        L(V(4, 3.5f), V(4, 20), V(21, 20));
        Line(fg, 2.6f, V(9, 16.5f), V(9, 12.5f));
        Line(fg, 2.6f, V(14, 16.5f), V(14, 6.5f));
        Line(fg, 2.6f, V(19, 16.5f), V(19, 10));
        End();
    }

    // ---- tools ----

    public static void Pour(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        var heap = Ellipse(V(12, 20.5f), 9, 6.5f, Mathf.Pi, Mathf.Tau, 24);
        ci.DrawColoredPolygon(heap, fg with { A = fg.A * 0.22f });
        Poly(heap);
        L(V(2, 20.5f), V(22, 20.5f));
        Dot(V(12, 3), 2.6f);
        Dot(V(10, 7.3f), 2.4f);
        Dot(V(13.6f, 8.6f), 2.2f);
        Dot(V(11.4f, 11.2f), 2.2f);
        End();
    }

    public static void Water(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        var o = V(12, 14.5f);
        float r = 6.8f, d = 14.5f - 2.5f, phi = MathF.Acos(r / d);
        var p = new List<Vector2> { V(12, 2.5f) };
        p.AddRange(Ellipse(o, r, r, -Mathf.Pi / 2 + phi, 1.5f * Mathf.Pi - phi, 36));
        p.Add(V(12, 2.5f));
        ci.DrawColoredPolygon(p.ToArray(), fg with { A = fg.A * 0.22f });
        Poly(p.ToArray());
        Cap(V(12, 2.5f), W, fg);
        ci.DrawArc(o, r - 3, Mathf.Pi * 0.55f, Mathf.Pi * 0.95f, 10, fg, 1.3f, true);
        End();
    }

    public static void Kill(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        Ring(V(12, 12), 7);
        L(V(12, 1.5f), V(12, 7.5f));
        L(V(12, 16.5f), V(12, 22.5f));
        L(V(1.5f, 12), V(7.5f, 12));
        L(V(16.5f, 12), V(22.5f, 12));
        Dot(V(12, 12), 2.8f);
        End();
    }

    public static void Dig(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        L(V(19.5f, 4.5f), V(11.5f, 12.5f));
        L(V(17, 2), V(22, 7));
        var blade = new[] { V(9.5f, 10.5f), V(13.5f, 14.5f), V(10.5f, 19), V(5.5f, 20.5f), V(3.5f, 18.5f), V(5, 13.5f) };
        ci.DrawColoredPolygon(blade, fg with { A = fg.A * 0.22f });
        Closed(blade);
        End();
    }

    public static void Plant(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        L(V(12, 21), V(12, 10));
        Leaf(V(12, 14.5f), V(4.5f, 8), 2.8f);
        Leaf(V(12, 10.5f), V(19.5f, 4), 2.8f);
        L(V(6.5f, 21), V(17.5f, 21));
        End();
    }

    public static void Slice(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        Closed(V(3, 4.5f), V(21, 4.5f), V(21, 19.5f), V(3, 19.5f));
        var wave = new Vector2[19];
        for (int i = 0; i < wave.Length; i++) wave[i] = V(3 + 18f * i / (wave.Length - 1), 9.5f + 1.3f * MathF.Sin(i * 0.75f));
        Poly(wave);
        Line(fg, 1.1f, V(3, 14.5f), V(21, 14.5f));
        Dot(V(8, 17), 1.8f);
        Dot(V(15.5f, 12), 1.6f);
        End();
    }

    public static void Overlay(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        var top = new[] { V(12, 3), V(21.5f, 8), V(12, 13), V(2.5f, 8) };
        ci.DrawColoredPolygon(top, fg with { A = fg.A * 0.22f });
        Closed(top);
        L(V(2.5f, 12.2f), V(12, 17.2f), V(21.5f, 12.2f));
        L(V(2.5f, 16.4f), V(12, 21.4f), V(21.5f, 16.4f));
        End();
    }

    public static void Light(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        var o = V(12, 12);
        Ring(o, 4.3f);
        for (int k = 0; k < 8; k++)
        {
            var d = Vector2.FromAngle(k * Mathf.Pi / 4);
            L(o + d * 7.6f, o + d * 10);
        }
        End();
    }

    public static void Perf(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        var o = V(12, 15);
        Arc(o, 9, Mathf.Pi * 0.95f, Mathf.Pi * 2.05f);
        for (int k = 1; k < 4; k++)
        {
            var d = Vector2.FromAngle(Mathf.Pi + k * Mathf.Pi / 4);
            Line(fg, 1.05f, o + d * 5.6f, o + d * 6.6f);
        }
        L(o, V(16.8f, 9.2f));
        Dot(o, 4);
        L(V(5, 20.5f), V(19, 20.5f));
        End();
    }

    // ---- libraries and regions (windows merged later) ----

    public static void Matter(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        var a = V(12, 5.5f); var p = V(5.5f, 17); var q = V(18.5f, 17);
        foreach (var (u, v) in new[] { (a, p), (a, q), (p, q) })
        {
            var d = (v - u).Normalized();
            L(u + d * 3.4f, v - d * 3.4f);
        }
        Ring(a, 3); Ring(p, 3); Ring(q, 3);
        Dot(a, 2.2f);
        End();
    }

    public static void LifeLib(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        var o = V(12, 12);
        var blob = new Vector2[41];
        for (int i = 0; i <= 40; i++)
        {
            float a = Mathf.Tau * i / 40, r = 8.6f + 0.7f * MathF.Sin(3 * a + 0.6f);
            blob[i] = o + Vector2.FromAngle(a) * r;
        }
        Poly(blob);
        Dot(V(13.5f, 10.5f), 6);
        Dot(V(8, 15), 2.2f);
        Dot(V(11.5f, 17.5f), 1.7f);
        End();
    }

    public static void Regions(CanvasItem c, Vector2 at, float size, Color f, Color b)
    {
        Begin(c, at, size, f, b);
        Closed(V(2.5f, 6), V(8.5f, 3.5f), V(15.5f, 6), V(21.5f, 3.5f), V(21.5f, 18), V(15.5f, 20.5f), V(8.5f, 18), V(2.5f, 20.5f));
        L(V(8.5f, 3.5f), V(8.5f, 18));
        L(V(15.5f, 6), V(15.5f, 20.5f));
        End();
    }

    // ---- the bar itself ----

    // Double chevron: pointing right (expand) or left (collapse).
    public static void Chevron(CanvasItem c, Vector2 at, float size, Color f, bool left)
    {
        Begin(c, at, size, f, f);
        float k = left ? -1 : 1;
        L(V(12 - 5 * k, 6.5f), V(12 - 0.5f * k, 12), V(12 - 5 * k, 17.5f));
        L(V(12 + 1 * k, 6.5f), V(12 + 5.5f * k, 12), V(12 + 1 * k, 17.5f));
        End();
    }
}
