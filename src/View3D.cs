using System;
using System.Collections.Generic;
using Godot;

namespace Primordium;

// 2.5D view of the planet. Every column is one box: its sides show the strata (looked up in a voxel
// texture by the shader), its top shows the surface or a chosen overlay, lit by the simulated sun.
// Agents (View3D.Agents.cs) stand on the columns.
public partial class View3D : Node3D
{
    const float BH = P.BlockH;
    const int W = World.W, H = World.H, Z = World.Z, N = World.N;

    public World World { get; private set; }
    public Camera3D Cam { get; private set; }
    public int Overlay;              // 0 surface, 1 temperature, 2 light, 3 remains, 4 density, 5 deaths, 6 body heat, 7+s one species
    public int ColorMode;            // see ColorModeNames
    public bool Lighting = true;
    public int Slice = -1;           // cut-away: rows south of this are hidden
    public Agent Selected;
    public bool Follow;
    public float PanelWidth = 440;   // logical px covered by the HUD panel on the right

    public int OverlayCount => FirstSpecies + Chemistry.S;
    public const int FirstSpecies = 9;   // overlays before the per-molecule ones

    MultiMesh terrain, caveTerrain;
    readonly int[] topBase = new int[N];
    readonly Dictionary<int, List<(int bottom, int top)>> caveRuns = new();
    ShaderMaterial terrainMat;
    Image voxImg;
    ImageTexture voxTex, palTex;
    // The strata texture packs the levels in Slabs side-by-side strips of ZS levels each, so that its
    // height (H·ZS) stays within what a GPU takes (192 levels in one strip would be 30720 rows).
    const int Slabs = (H * Z + 8191) / 8192, ZS = (Z + Slabs - 1) / Slabs, TexW = W * Slabs, TexH = H * ZS;
    byte[] vox = new byte[TexW * TexH];
    float groundY = 6;   // typical surface level, where the camera looks
    readonly float[] tBuf = new float[N * 16];
    int seenTerrain = -1, frame, shapedTerrain = -1, shapedSlice = -2;
    readonly List<MeshInstance3D> ventMarks = new();
    StandardMaterial3D ventMat;

    Vector3 target = new(W / 2f, 6, H / 2f);
    float yaw = 12, yawGoal = 12, pitch = -48, zoom = 250;
    bool framed;

    const string TerrainShader = @"
shader_type spatial;
render_mode unshaded, cull_back;
uniform sampler2D vox : filter_nearest;
uniform sampler2D pal : filter_nearest;
uniform int zmax;
uniform int zs;
uniform int wmax;
uniform float bh;
uniform float side_lit;
varying vec3 wpos;
varying vec3 nrm;
varying vec4 cust;

vec3 lin(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(0.04045, c)); }

void vertex() {
    wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    nrm = NORMAL;
    cust = INSTANCE_CUSTOM;
}

void fragment() {
    vec3 col;
    float l = cust.a;
    if (nrm.y > 0.5) {
        col = cust.rgb;
    } else {
        l = max(l, side_lit);
        vec3 inside = wpos - vec3(nrm.x, 0.0, nrm.z) * 0.02;
        int x = int(floor(inside.x));
        int y = int(floor(inside.z));
        int z = clamp(int(floor(wpos.y / bh)), 0, zmax - 1);
        float m = texelFetch(vox, ivec2(x + (z / zs) * wmax, y * zs + z % zs), 0).r;
        col = texelFetch(pal, ivec2(int(m * 255.0 + 0.5), 0), 0).rgb;
        float f = fract(wpos.y / bh);
        col *= 0.84 + 0.16 * smoothstep(0.0, 0.1, f);
        col *= abs(nrm.x) > 0.5 ? 0.78 : 0.6;
    }
    // cust.a is sunlight: day shows true colours, night fades into a dim blue.
    vec3 night = col * vec3(0.6, 0.66, 0.85);
    ALBEDO = lin(mix(night, col, l));
}
";

    public override void _Ready()
    {
        Cam = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = zoom, Near = 1f, Far = 3000f, Current = true };
        AddChild(Cam);

        terrain = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = new BoxMesh() };
        terrain.InstanceCount = N;
        terrainMat = new ShaderMaterial { Shader = new Shader { Code = TerrainShader } };
        AddChild(new MultiMeshInstance3D { Multimesh = terrain, MaterialOverride = terrainMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

        caveTerrain = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = new BoxMesh() };
        AddChild(new MultiMeshInstance3D { Multimesh = caveTerrain, MaterialOverride = terrainMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

        ventMat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.42f, 0.12f) };
        BuildAgentNodes();
        BuildWeatherNodes();
    }

    public void SetWorld(World w)
    {
        World = w;
        Selected = null;
        Hover = null;
        Follow = false;
        worldShownAt = Time.GetTicksMsec() / 1000.0;
        seenTerrain = -1;
        var pal = Image.CreateEmpty(w.Chem.MatCount, 1, false, Image.Format.Rgba8);
        for (int m = 0; m < w.Chem.MatCount; m++)
        {
            var c = m < w.Chem.MatCount ? w.Chem.MatCol[m] : new Rgb(1, 0, 1);
            pal.SetPixel(m, 0, new Color(c.R, c.G, c.B));
        }
        palTex = ImageTexture.CreateFromImage(pal);
        Array.Fill(voxH, -1);
        caveRuns.Clear();
        RebuildVoxels();
        terrainMat.SetShaderParameter("vox", voxTex);
        terrainMat.SetShaderParameter("pal", palTex);
        terrainMat.SetShaderParameter("zmax", Z);
        terrainMat.SetShaderParameter("zs", ZS);
        terrainMat.SetShaderParameter("wmax", W);
        double hs = 0;
        for (int i = 0; i < N; i++) hs += w.Height[i];
        groundY = (float)(hs / N) * BH;
        target.Y = groundY;
        terrainMat.SetShaderParameter("bh", BH);
    }

    // The strata texture: only columns whose height changed since last time are rewritten (blocks get
    // eaten all the time, but a column changes shape only when one disappears or is laid down).
    readonly int[] voxH = new int[N];
    readonly byte[] voxTop = new byte[N];

    void RebuildVoxels()
    {
        var w = World;
        bool all = voxImg == null;
        for (int i = 0; i < N; i++)
        {
            int h = w.Height[i];
            byte top = h > 0 ? w.Mat[i * Z + h - 1] : (byte)0;
            if (!all && voxH[i] == w.ColumnVersion[i]) continue;
            voxH[i] = w.ColumnVersion[i];
            voxTop[i] = top;
            int x = i % W, y = i / W;
            for (int z = 0; z < Z; z++) vox[(y * ZS + z % ZS) * TexW + z / ZS * W + x] = z < h ? w.Mat[i * Z + z] : (byte)0;
            caveRuns.Remove(i); topBase[i] = 0;
            List<(int bottom, int top)> runs = null;
            int start = -1;
            for (int z = 0; z <= h; z++)
            {
                bool solid = z < h && w.IsSolid(i, z);
                if (solid && start < 0) start = z;
                if (!solid && start >= 0)
                {
                    if (z == h) topBase[i] = start;
                    else { runs ??= new(); runs.Add((start, z)); }
                    start = -1;
                }
            }
            if (runs != null) caveRuns[i] = runs;
        }
        if (voxImg == null)
        {
            voxImg = Image.CreateFromData(TexW, TexH, false, Image.Format.R8, vox);
            voxTex = ImageTexture.CreateFromImage(voxImg);
        }
        else
        {
            voxImg.SetData(TexW, TexH, false, Image.Format.R8, vox);
            voxTex.Update(voxImg);
        }
        seenTerrain = w.TerrainVersion;
        FillCaves();
    }

    public void Refresh(float dt)
    {
        if (World == null) return;
        frame++;
        UpdateCamera(dt);
        if (World.TerrainVersion != seenTerrain && frame % 8 == 0) RebuildVoxels();
        terrainMat.SetShaderParameter("side_lit", Slice >= 0 || !Lighting ? 1f : 0.55f);
        FillTerrain();
        FillWater();
        FillAgents(dt);
        FillLinks();
        FillFlashes();
        FillDrops();
        UpdateStrikes();
        UpdateVents();
        UpdateRings();
    }

    public Vector3 AgentPos(Agent a) => new(a.X + 0.5f, AgentGround(a), a.Y + 0.5f);

    public float AgentGround(Agent a) => a.Z == World.Height[a.Y * W + a.X] ? Ground(a.Y * W + a.X) : a.Z * BH;

    // Where the ground of a column is: a top block that is partly eaten is drawn thinner.
    public float Ground(int i)
    {
        var w = World;
        int h = w.Height[i];
        if (h == 0) return 0;
        int v = i * Z + h - 1;
        float f = 0.25f + 0.75f * w.Fill(v);
        return (h - 1 + f) * BH;
    }

    float Lit(int i) => Lighting ? MathF.Min(1f, World.Light[i]) : 1f;

    void FillTerrain()
    {
        var w = World;
        var ch = w.Chem;
        var b = tBuf;
        // Ground and colours change slowly: refresh a quarter of the rows per frame (all of them when
        // the terrain or the cut-away has just changed shape).
        bool reshape = shapedTerrain != w.TerrainVersion || shapedSlice != Slice;
        if (shapedSlice != Slice) FillCaves();
        shapedTerrain = w.TerrainVersion;
        shapedSlice = Slice;
        for (int y = reshape ? 0 : frame & 3; y < H; y += reshape ? 1 : 4)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x, o = i * 16;
                bool hide = Slice >= 0 && y > Slice;
                float floor = hide ? 0 : topBase[i] * BH;
                float hh = hide ? 0 : Math.Max(0, Ground(i) - floor), sxz = hide ? 0 : 1;
                b[o] = sxz; b[o + 1] = 0; b[o + 2] = 0; b[o + 3] = x + 0.5f;
                b[o + 4] = 0; b[o + 5] = hh; b[o + 6] = 0; b[o + 7] = floor + hh * 0.5f;
                b[o + 8] = 0; b[o + 9] = 0; b[o + 10] = sxz; b[o + 11] = y + 0.5f;
            }
        for (int y = frame & 3; y < H; y += 4)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x, h = w.Height[i], o = i * 16;
                Rgb c;
                float lit = Lit(i);
                switch (Overlay)
                {
                    case 0:
                        {
                            int v = i * Z + h - 1;
                            byte m = w.Mat[v];
                            c = ch.MatCol[m];
                            if (m >= 2) c = c.Mul(0.7f + 0.3f * w.Fill(v));
                            float r = 0, g = 0, bl = 0, tot = 0;
                            for (int s = 0; s < Chemistry.S; s++)
                            {
                                if (s == ch.Gas) continue;
                                float q = w.C[s][i];
                                tot += q; r += q * ch.Col[s].R; g += q * ch.Col[s].G; bl += q * ch.Col[s].B;
                            }
                            if (tot > 0.01f) c = c.Lerp(new Rgb(r / tot, g / tot, bl / tot), 0.4f * tot / (tot + 12f));
                            if (w.Snow[i] > 0.01f) c = c.Lerp(new Rgb(0.95f, 0.97f, 1f), Math.Min(1f, w.Snow[i] * 4));
                            break;
                        }
                    case 1:
                        {
                            // −25 °C deep blue · 0 °C white · +40 °C red
                            float tc = w.Temp[i];
                            c = tc < 0 ? new Rgb(0.95f, 0.95f, 0.97f).Lerp(new Rgb(0.12f, 0.25f, 0.8f), Math.Min(1, -tc / 25f))
                                       : new Rgb(0.95f, 0.95f, 0.97f).Lerp(new Rgb(0.95f, 0.2f, 0.08f), Math.Min(1, tc / 40f));
                            lit = 1;
                            break;
                        }
                    case 2:
                        c = new Rgb(0.08f, 0.08f, 0.14f).Lerp(new Rgb(1f, 0.92f, 0.55f), MathF.Min(1, w.Light[i]));
                        lit = 1;
                        break;
                    case 3:
                        {
                            float tot = 0;
                            for (int s = 0; s < Chemistry.S; s++) if (s != ch.Gas) tot += w.C[s][i];
                            c = new Rgb(0.07f, 0.06f, 0.05f).Lerp(new Rgb(0.6f, 0.95f, 0.35f), tot / (tot + 15f));
                            break;
                        }
                    case 4:
                        c = new Rgb(0.05f, 0.05f, 0.08f).Lerp(new Rgb(1f, 0.35f, 0.9f), Math.Min(1f, w.Count[i] / 8f));
                        lit = 1;
                        break;
                    case 5:
                        c = new Rgb(0.05f, 0.05f, 0.06f).Lerp(new Rgb(1f, 0.15f, 0.1f), w.DeathMap[i] / (w.DeathMap[i] + 2f));
                        lit = 1;
                        break;
                    case 6:
                        c = new Rgb(0.05f, 0.05f, 0.06f).Lerp(new Rgb(1f, 0.6f, 0.1f), w.BodyHeat[i] / (w.BodyHeat[i] + 0.6f));
                        lit = 1;
                        break;
                    case 7:
                        {
                            float stress = 0;
                            for (int z = 2; z < h; z++)
                            {
                                int v = i * Z + z;
                                if (w.Mat[v] >= 2) stress = Math.Max(stress, w.Pressure[v] / Math.Max(0.001f, w.CompressionCapacity(v)));
                            }
                            c = new Rgb(0.15f, 0.6f, 0.35f).Lerp(new Rgb(1f, 0.15f, 0.04f), Math.Min(1, stress));
                            lit = 1; break;
                        }
                    case 8:
                        c = new Rgb(0.28f, 0.22f, 0.16f).Lerp(new Rgb(0.6f, 0.85f, 1f), h > 0 ? w.Order[i * Z + h - 1] / 255f : 0);
                        lit = 1; break;
                    default:
                        {
                            int s = Overlay - FirstSpecies;
                            c = new Rgb(0.06f, 0.06f, 0.07f).Lerp(ch.Col[s], MathF.Min(1, w.C[s][i] / 3f));
                            break;
                        }
                }
                b[o + 12] = c.R; b[o + 13] = c.G; b[o + 14] = c.B; b[o + 15] = lit;
            }
        terrain.Buffer = tBuf;
    }

    // Draw each solid run separately: cave floors and ceilings are actual faces, not painted holes.
    void FillCaves()
    {
        int count = 0;
        foreach (var pair in caveRuns) if (Slice < 0 || pair.Key / W <= Slice) count += pair.Value.Count;
        caveTerrain.InstanceCount = count;
        if (count == 0) return;
        var buffer = new float[count * 16]; int o = 0;
        foreach (var pair in caveRuns)
        {
            int c = pair.Key;
            if (Slice >= 0 && c / W > Slice) continue;
            foreach (var run in pair.Value)
            {
                var colour = World.Chem.MatCol[World.Mat[c * Z + run.top - 1]];
                buffer[o] = 1; buffer[o + 3] = c % W + 0.5f;
                buffer[o + 5] = (run.top - run.bottom) * BH;
                buffer[o + 7] = (run.top + run.bottom) * BH * 0.5f;
                buffer[o + 10] = 1; buffer[o + 11] = c / W + 0.5f;
                buffer[o + 12] = colour.R; buffer[o + 13] = colour.G; buffer[o + 14] = colour.B; buffer[o + 15] = 0.6f;
                o += 16;
            }
        }
        caveTerrain.Buffer = buffer;
    }

    void UpdateVents()
    {
        var vents = World.Vents;
        while (ventMarks.Count < vents.Count)
        {
            var m = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.6f, Height = 1.2f }, MaterialOverride = ventMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(m);
            ventMarks.Add(m);
        }
        for (int k = 0; k < ventMarks.Count; k++)
        {
            bool on = k < vents.Count && (Slice < 0 || vents[k].Y <= Slice);
            ventMarks[k].Visible = on;
            if (!on) continue;
            var v = vents[k];
            float pulse = 1 + 0.15f * MathF.Sin(frame * 0.15f + k);
            ventMarks[k].Position = new Vector3(v.X + 0.5f, Ground(v.Y * W + v.X) + 1.2f, v.Y + 0.5f);
            ventMarks[k].Scale = Vector3.One * pulse;
        }
    }

    // ---- camera ----

    void UpdateCamera(float dt)
    {
        if (!framed) { framed = true; ResetCamera(); yaw = yawGoal; }
        yaw = Mathf.Lerp(yaw, yawGoal, 1 - Mathf.Exp(-dt * 12));
        if (Follow && Selected is { Dead: false })
            target = target.Lerp(AgentPos(Selected), 1 - Mathf.Exp(-dt * 6));
        var basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(pitch), Mathf.DegToRad(yaw), 0));
        Cam.Transform = new Transform3D(basis, target + basis.Z * 600);
        key.Rotation = new Vector3(Mathf.DegToRad(-55), Mathf.DegToRad(yaw + 40), 0);
        Cam.Size = zoom;
        float vh = GetViewport().GetVisibleRect().Size.Y;
        Cam.HOffset = PanelWidth * 0.5f / vh * zoom;
    }

    public void Pan(Vector2 px)
    {
        float k = zoom / GetViewport().GetVisibleRect().Size.Y;
        var basis = Cam.Transform.Basis;
        var right = new Vector3(basis.X.X, 0, basis.X.Z).Normalized();
        var fwd = new Vector3(-basis.Z.X, 0, -basis.Z.Z).Normalized();
        target -= right * px.X * k;
        target += fwd * px.Y * k / MathF.Max(0.2f, MathF.Sin(-Mathf.DegToRad(pitch)));
        target.X = Math.Clamp(target.X, -20, W + 20);
        target.Z = Math.Clamp(target.Z, -20, H + 20);
        Follow = false;
    }

    public void Orbit(Vector2 px)
    {
        yawGoal -= px.X * 0.3f;
        yaw = yawGoal;
        pitch = Math.Clamp(pitch - px.Y * 0.2f, -88f, -12f);
    }

    public void RotateStep(int dir) => yawGoal += 90 * dir;
    public void ZoomBy(float f) => zoom = Math.Clamp(zoom * f, 8f, 420f);

    public void ZoomAt(float z)
    {
        framed = true;
        zoom = Math.Clamp(z, 8f, 420f);
        if (Slice >= 0) target = new Vector3(W / 2f, groundY, Slice);
        else if (Selected != null) target = AgentPos(Selected);
    }

    public void ResetCamera()
    {
        // Fit the whole map into the part of the screen left of the panel.
        var vs = GetViewport().GetVisibleRect().Size;
        target = new Vector3(W / 2f, groundY, H / 2f);
        yawGoal = 12; pitch = -48;
        zoom = Math.Clamp((W + 16) * vs.Y / Math.Max(200, vs.X - PanelWidth), 60, 420);
    }

    public void LookAt(Agent a)
    {
        target = AgentPos(a);
        zoom = Math.Min(zoom, 40);
    }

    // Ray-march the heightfield under the cursor, then take the nearest agent.
    public Agent Pick(Vector2 screen)
    {
        var w = World;
        Vector3 o = Cam.ProjectRayOrigin(screen), d = Cam.ProjectRayNormal(screen);
        if (d.Y >= -1e-4f) return null;
        float t0 = (Z * BH + 1 - o.Y) / d.Y, t1 = -o.Y / d.Y;
        Vector3 hit = o + d * t1;
        for (float t = Math.Max(0, t0); t < t1; t += 0.2f)
        {
            var p = o + d * t;
            int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Z);
            if (x < 0 || x >= W || y < 0 || y >= H) continue;
            if (Slice >= 0 && y > Slice) continue;
            if (w.IsSolid(y * W + x, (int)MathF.Floor(p.Y / BH))) { hit = p; break; }
        }
        Agent best = null;
        float bd = 2.2f * 2.2f;
        foreach (var a in w.Agents)
        {
            if (a.Dead) continue;
            float dx = a.X + 0.5f - hit.X, dz = a.Y + 0.5f - hit.Z, dd = dx * dx + dz * dz;
            if (dd < bd) { bd = dd; best = a; }
        }
        return best;
    }
}
