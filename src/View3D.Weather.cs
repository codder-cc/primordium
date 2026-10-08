using System;
using System.Collections.Generic;
using Godot;

namespace Primordium;

// Water and ice on top of the columns, falling rain and snow, beams of mutagenic strikes.
public partial class View3D
{
    const int DropCap = 2500;

    MultiMesh water, ice, drops;
    float[] waterBuf = Array.Empty<float>(), iceBuf = Array.Empty<float>();
    readonly float[] dropBuf = new float[DropCap * 16];
    readonly (float x, float z, double t0, bool snow, bool on)[] drop = new (float, float, double, bool, bool)[DropCap];
    readonly Random rnd = new(1);
    readonly List<(Strike s, double t0, MeshInstance3D beam, MeshInstance3D ring)> strikes = new();
    StandardMaterial3D strikeMat;

    void BuildWeatherNodes()
    {
        var waterMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        var flat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
        };
        ice = NewMM(new BoxMesh(), flat);
        water = NewMM(new BoxMesh(), waterMat);
        drops = NewMM(new BoxMesh(), flat);
        drops.InstanceCount = DropCap;
        strikeMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(1f, 0.55f, 1f, 0.85f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
    }

    void FillWater()
    {
        var w = World;
        if (frame % 3 != 0) return;
        int nw = 0, ni = 0;
        for (int i = 0; i < N; i++) { if (w.Water[i] > 0.04f) nw++; if (w.Ice[i] > 0.04f) ni++; }
        var caveWater = w.CaveWaterView;   // water standing in caves (World.Waterways): a copy, safe to read here
        nw += caveWater.Length;
        bool floats = P.IceFloat != 0;     // ice on top of the water (World.Climate), or under it as before
        Ensure(water, ref waterBuf, nw);
        Ensure(ice, ref iceBuf, ni);
        nw = ni = 0;
        for (int i = 0; i < N; i++)
        {
            float wd = w.Water[i], id = w.Ice[i];
            if (wd <= 0.04f && id <= 0.04f) continue;
            int x = i % W, y = i / W;
            if (Slice >= 0 && y > Slice) continue;
            float g = w.Height[i] * BH, lum = Lighting ? 0.45f + 0.55f * MathF.Min(1, w.Light[i] * 1.6f + 0.2f) : 1f;
            if (id > 0.04f && !floats)
            {
                float h = id * BH;
                PutBox(iceBuf, ni++, new Vector3(x + 0.5f, g + h * 0.5f, y + 0.5f), new Vector3(1, h, 1), new Color(0.78f, 0.88f, 0.95f) * lum);
                g += h;
            }
            if (wd > 0.04f)
            {
                PutWater(ref nw, x, y, g, wd, lum);
                g += wd * BH;
            }
            if (id > 0.04f && floats)
            {
                float h = id * BH;
                PutBox(iceBuf, ni++, new Vector3(x + 0.5f, g + h * 0.5f, y + 0.5f), new Vector3(1, h, 1), new Color(0.78f, 0.88f, 0.95f) * lum);
            }
        }
        foreach (var (v, depth) in caveWater)
        {
            int i = v / Z, x = i % W, y = i / W;
            if (Slice >= 0 && y > Slice) continue;
            PutWater(ref nw, x, y, (v % Z) * BH, depth, 0.45f);   // under a roof: no daylight
        }
        water.Buffer = waterBuf;
        water.VisibleInstanceCount = nw;
        ice.Buffer = iceBuf;
        ice.VisibleInstanceCount = ni;
    }

    void PutWater(ref int nw, int x, int y, float g, float wd, float lum)
    {
        float h = wd * BH;
        float deep = Math.Min(1, wd / 6f);
        var c = new Color(0.2f, 0.45f, 0.75f).Lerp(new Color(0.05f, 0.15f, 0.38f), deep) * lum;
        c.A = 0.45f + 0.35f * deep;
        Put(waterBuf, nw++, new Vector3(1, 0, 0), new Vector3(0, h, 0), new Vector3(0, 0, 1), new Vector3(x + 0.5f, g + h * 0.5f, y + 0.5f), c);
        waterBuf[(nw - 1) * 16 + 15] = c.A;
    }

    // Rain and snow fall only where clouds actually rain, near what the camera looks at.
    void FillDrops()
    {
        var w = World;
        var vs = GetViewport().GetVisibleRect().Size;
        float halfW = zoom * vs.X / vs.Y * 0.5f, halfH = zoom * 0.7f;
        int n = 0;
        for (int k = 0; k < DropCap; k++)
        {
            ref var d = ref drop[k];
            double dur = d.snow ? 2.2 : 0.55;
            if (!d.on || now - d.t0 > dur)
            {
                d.on = false;
                for (int t = 0; t < 3 && !d.on; t++)
                {
                    float x = target.X + (float)(rnd.NextDouble() * 2 - 1) * halfW, z = target.Z + (float)(rnd.NextDouble() * 2 - 1) * halfH;
                    int cx = (int)MathF.Floor(x), cz = (int)MathF.Floor(z);
                    if (cx < 0 || cx >= W || cz < 0 || cz >= H || (Slice >= 0 && cz > Slice)) continue;
                    int i = cz * W + cx;
                    if (rnd.NextDouble() > w.Rain[i] * 1.5) continue;
                    d = (x, z, now - rnd.NextDouble() * 0.3, w.Temp[i] < 0, true);
                }
                if (!d.on) continue;
                dur = d.snow ? 2.2 : 0.55;
            }
            int cell = Math.Clamp((int)d.z, 0, H - 1) * W + Math.Clamp((int)d.x, 0, W - 1);
            float ground = (w.Height[cell] + w.Ice[cell] + w.Water[cell]) * BH;
            float p = (float)((now - d.t0) / dur), y = ground + 9 * (1 - p);
            float sway = d.snow ? 0.3f * MathF.Sin((float)now * 2 + k) : 0;
            if (d.snow) PutBox(dropBuf, n++, new Vector3(d.x + sway, y, d.z), Vector3.One * 0.13f, new Color(0.95f, 0.97f, 1f));
            else PutBox(dropBuf, n++, new Vector3(d.x, y, d.z), new Vector3(0.035f, 0.55f, 0.035f), new Color(0.6f, 0.75f, 0.95f));
        }
        drops.Buffer = dropBuf;
        drops.VisibleInstanceCount = n;
    }

    // A pillar of light from the sky and a ring sweeping over the struck area.
    void UpdateStrikes()
    {
        foreach (var s in Frame.Strikes)
        {
            bool known = false;
            foreach (var e in strikes) if (e.s == s) { known = true; break; }
            if (known || Frame.Tick - s.T > 2000) continue;
            var beam = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1, BottomRadius = 1, Height = 1, RadialSegments = 16 }, MaterialOverride = strikeMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.92f, OuterRadius = 1f, Rings = 48, RingSegments = 6 }, MaterialOverride = strikeMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(beam);
            AddChild(ring);
            strikes.Add((s, now, beam, ring));
        }
        for (int k = strikes.Count - 1; k >= 0; k--)
        {
            var (s, t0, beam, ring) = strikes[k];
            float p = (float)((now - t0) / 3.0);
            if (p >= 1 || Frame.Strikes.IndexOf(s) < 0)
            {
                beam.QueueFree();
                ring.QueueFree();
                strikes.RemoveAt(k);
                continue;
            }
            float g = World.Height[s.Y * W + s.X] * BH;
            float r = Math.Max(0.4f, s.R * 0.35f * (1 - p) + 0.15f);
            beam.Visible = ring.Visible = Slice < 0 || s.Y <= Slice;
            beam.Position = new Vector3(s.X + 0.5f, g + 40, s.Y + 0.5f);
            beam.Scale = new Vector3(r, 80, r);
            float rr = s.R * MathF.Min(1, p * 2.5f);
            ring.Position = new Vector3(s.X + 0.5f, g + 0.6f, s.Y + 0.5f);
            ring.Scale = new Vector3(rr, 1, rr);
        }
    }

    // The moon's shadow (World.Sky): its light is already dimmed; a pale ring marks the edge of the full shadow.
    MeshInstance3D eclipseRing;
    void UpdateEclipse()
    {
        var w = World;
        if (eclipseRing == null)
        {
            var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.95f, 0.85f, 1f, 0.9f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
            eclipseRing = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.94f, OuterRadius = 1f, Rings = 64, RingSegments = 6 }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(eclipseRing);
        }
        bool on = w.EclipseNow && (Slice < 0 || w.EclipseY <= Slice);
        eclipseRing.Visible = on;
        if (!on) return;
        int x = Math.Clamp((int)w.EclipseX, 0, W - 1), y = Math.Clamp((int)w.EclipseY, 0, H - 1);
        eclipseRing.Position = new Vector3(w.EclipseX, groundTop[y * W + x] + 2, w.EclipseY);
        eclipseRing.Scale = new Vector3(P.EclipseR, 3, P.EclipseR);
    }

    // Which column is under the cursor (-1 if none).
    public int PickCell(Vector2 screen)
    {
        var w = World;
        Vector3 o = Cam.ProjectRayOrigin(screen), d = Cam.ProjectRayNormal(screen);
        if (d.Y >= -1e-4f) return -1;
        float t0 = (Z * BH + 1 - o.Y) / d.Y, t1 = -o.Y / d.Y;
        for (float t = Math.Max(0, t0); t < t1; t += 0.2f)
        {
            var p = o + d * t;
            int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Z);
            if (x < 0 || x >= W || y < 0 || y >= H) continue;
            if (Slice >= 0 && y > Slice) continue;
            if (p.Y <= w.Height[y * W + x] * BH) return y * W + x;
        }
        return -1;
    }
}
