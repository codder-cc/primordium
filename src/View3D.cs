using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;

namespace Primordium;

// 2.5D view of the planet. Every column is one box: its sides show the strata (looked up in a voxel
// texture by the shader), its top shows the surface or a chosen overlay, lit by the simulated sun.
// Agents (View3D.Agents.cs) stand on the columns.
//
// The world is stepped on another thread (SimRunner). Bodies, links, flashes, strikes and vents come
// from the published SimFrame; terrain arrays are read directly (a torn number only shows for a frame).
// Nothing here calls into the world in a way that writes to it.
public partial class View3D : Node3D
{
    const float BH = P.BlockH;
    const int W = World.W, H = World.H, Z = World.Z, N = World.N;

    public World World { get; private set; }
    public SimFrame Frame;           // set by Main every frame
    public float[] Stress;           // per column, for the load overlay (filled by the simulation thread)
    public Camera3D Cam { get; private set; }
    public int Overlay;              // 0 surface, 1 temperature, 2 light, 3 remains, 4 density, 5 deaths, 6 body heat, 7 load, 8 lattice, 9 temperature at depth, 10 deep element, FirstSpecies+s one species
    public int ColorMode;            // see ColorModeNames
    public bool Lighting = true;
    public int Slice = -1;           // cut-away: rows south of this are hidden
    public Agent Selected;
    public bool Follow;
    public float PanelWidth = 440;   // logical px covered by the HUD panel on the right

    public int OverlayCount => FirstSpecies + Chemistry.S;
    public const int FirstSpecies = 14;  // overlays before the per-molecule ones
    public const int DepthTempOverlay = 9, DeepOverlay = 10, TranspOverlay = 11, DayOverlay = 12, FlareOverlay = 13;

    // Timings of the steps of Refresh, ms summed since Main last cleared them (perf overlay).
    public const int ProfSlots = 10;
    public static readonly string[] ProfNames = { "camera", "voxels", "terrain", "water", "agents", "links", "flashes", "drops", "misc", "agent-upload" };
    public readonly double[] Prof = new double[ProfSlots];
    readonly Stopwatch profSw = new();
    void Lap(int k) { Prof[k] += profSw.Elapsed.TotalMilliseconds; profSw.Restart(); }

    // The columns are drawn in bands of rows, each its own MultiMesh: only bands that changed are uploaded.
    const int BandRows = 16, Bands = H / BandRows, BandN = BandRows * W;
    readonly MultiMesh[] bands = new MultiMesh[Bands];
    readonly float[][] bandBuf = new float[Bands][];
    readonly bool[] bandDirty = new bool[Bands];
    readonly int[] shapedVer = new int[N];
    int colourBand, shapeRow, shapedSlice = -2, colouredOverlay = -1, sweepLeft;
    public int BandsUploaded;   // diagnostics
    bool colouredLighting;
    long colouredTick = -1;

    MultiMesh caveTerrain;
    float[] caveBuf = Array.Empty<float>();
    bool cavesChanged;
    readonly int[] topBase = new int[N];
    readonly Dictionary<int, List<(int bottom, int top)>> caveRuns = new();
    ShaderMaterial terrainMat;
    ImageTexture palTex;

    // The strata texture: a texture array of 256×256 tiles, each holding 16 rows of the map × 16
    // levels. A changed column rewrites its bytes; only tiles whose bytes really changed are
    // uploaded (eating a block partly changes nothing here, and the air above the land never changes).
    const int TileRows = 16, TileLevels = 16, YTiles = H / TileRows, ZTiles = (Z + TileLevels - 1) / TileLevels, Layers = YTiles * ZTiles;
    const int TileH = TileRows * TileLevels;
    readonly byte[][] voxTile = new byte[Layers][];
    readonly bool[] tileDirty = new bool[Layers];
    Texture2DArray voxTex;
    Image tileImg;
    readonly int[] voxVer = new int[N];
    float groundY = 6;   // typical surface level, where the camera looks
    int seenTerrain = -1, frame;
    public int TilesUploaded;   // diagnostics
    readonly List<MeshInstance3D> ventMarks = new();
    StandardMaterial3D ventMat;

    Vector3 target = new(W / 2f, 6, H / 2f);
    float yaw = 12, yawGoal = 12, pitch = -48, zoom = 250;
    bool framed;

    const string TerrainShader = @"
shader_type spatial;
render_mode unshaded, cull_back;
uniform sampler2DArray vox : filter_nearest;
uniform sampler2D pal : filter_nearest;
uniform int zmax;
uniform int wmax;
uniform int hmax;
uniform int ytiles;
uniform float bh;
uniform float side_lit;
uniform int cut_row = -1;
uniform sampler2D cut_temp : filter_nearest;
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
        int x = clamp(int(floor(inside.x)), 0, wmax - 1);
        int y = clamp(int(floor(inside.z)), 0, hmax - 1);
        int z = clamp(int(floor(wpos.y / bh)), 0, zmax - 1);
        float m = texelFetch(vox, ivec3(x, (y % 16) * 16 + z % 16, (z / 16) * ytiles + y / 16), 0).r;
        col = texelFetch(pal, ivec2(int(m * 255.0 + 0.5), 0), 0).rgb;
        float f = fract(wpos.y / bh);
        col *= 0.84 + 0.16 * smoothstep(0.0, 0.1, f);
        col *= abs(nrm.x) > 0.5 ? 0.78 : 0.6;
        // The cut with the temperature-at-depth or deep-element overlay: every level in its own colour, unshaded.
        if (y == cut_row) col = texelFetch(cut_temp, ivec2(x, z), 0).rgb * (0.92 + 0.08 * smoothstep(0.0, 0.1, f));
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

        terrainMat = new ShaderMaterial { Shader = new Shader { Code = TerrainShader } };
        var box = new BoxMesh();
        for (int b = 0; b < Bands; b++)
        {
            bands[b] = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = box, InstanceCount = BandN };
            bandBuf[b] = new float[BandN * 16];
            AddChild(new MultiMeshInstance3D { Multimesh = bands[b], MaterialOverride = terrainMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }

        caveTerrain = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = box };
        AddChild(new MultiMeshInstance3D { Multimesh = caveTerrain, MaterialOverride = terrainMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

        for (int l = 0; l < Layers; l++) voxTile[l] = new byte[W * TileH];
        ventMat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.42f, 0.12f) };
        BuildAgentNodes();
        BuildWeatherNodes();
    }

    public void SetWorld(World w)
    {
        World = w;
        Frame = null;
        Selected = null;
        Hover = null;
        Follow = false;
        worldShownAt = Time.GetTicksMsec() / 1000.0;
        seenTerrain = -1;
        var pal = Image.CreateEmpty(w.Chem.MatCount, 1, false, Image.Format.Rgba8);
        for (int m = 0; m < w.Chem.MatCount; m++)
        {
            var c = w.Chem.MatCol[m];
            pal.SetPixel(m, 0, new Color(c.R, c.G, c.B));
        }
        palTex = ImageTexture.CreateFromImage(pal);
        Array.Fill(voxVer, int.MinValue);
        Array.Fill(shapedVer, int.MinValue);
        shapedSlice = -2;
        colouredOverlay = -1;
        cutShownRow = -2;
        caveRuns.Clear();
        foreach (var t in voxTile) Array.Clear(t);
        RebuildVoxels(true);
        terrainMat.SetShaderParameter("vox", voxTex);
        terrainMat.SetShaderParameter("pal", palTex);
        terrainMat.SetShaderParameter("zmax", Z);
        terrainMat.SetShaderParameter("wmax", W);
        terrainMat.SetShaderParameter("hmax", H);
        terrainMat.SetShaderParameter("ytiles", YTiles);
        double hs = 0;
        for (int i = 0; i < N; i++) { hs += w.Height[i]; groundTop[i] = Ground(i); }
        groundY = (float)(hs / N) * BH;
        target.Y = groundY;
        terrainMat.SetShaderParameter("bh", BH);
    }

    // Columns whose version changed get their bytes rewritten; caves are re-traced for them.
    void RebuildVoxels(bool create = false)
    {
        var w = World;
        seenTerrain = w.TerrainVersion;
        var mat = w.Mat;
        for (int i = 0; i < N; i++)
        {
            int ver = w.ColumnVersion[i];
            if (voxVer[i] == ver) continue;
            voxVer[i] = ver;
            int h = Math.Clamp(w.Height[i], 0, Z);
            int x = i % W, y = i / W, yt = y / TileRows, row = (y % TileRows) * TileLevels;
            int b0 = i * Z;
            for (int zt = 0; zt < ZTiles; zt++)
            {
                int layer = zt * YTiles + yt;
                var t = voxTile[layer];
                bool dirty = false;
                for (int dz = 0, z = zt * TileLevels; dz < TileLevels && z < Z; dz++, z++)
                {
                    byte m = z < h ? mat[b0 + z] : (byte)0;
                    int o = (row + dz) * W + x;
                    if (t[o] != m) { t[o] = m; dirty = true; }
                }
                if (dirty) tileDirty[layer] = true;
            }
            bool hadCaves = caveRuns.Remove(i);
            topBase[i] = 0;
            List<(int bottom, int top)> runs = null;
            int start = -1;
            for (int z = 0; z <= h; z++)
            {
                bool solid = z < h && mat[b0 + z] != Chemistry.Air;
                if (solid && start < 0) start = z;
                if (!solid && start >= 0)
                {
                    if (z == h) topBase[i] = start;
                    else { runs ??= new(); runs.Add((start, z)); }
                    start = -1;
                }
            }
            if (runs != null) caveRuns[i] = runs;
            if (hadCaves || runs != null) cavesChanged = true;
        }
        if (create || voxTex == null)
        {
            var imgs = new Godot.Collections.Array<Image>();
            for (int l = 0; l < Layers; l++) imgs.Add(Image.CreateFromData(W, TileH, false, Image.Format.R8, voxTile[l]));
            voxTex ??= new Texture2DArray();
            voxTex.CreateFromImages(imgs);
            tileImg ??= Image.CreateFromData(W, TileH, false, Image.Format.R8, voxTile[0]);
            Array.Clear(tileDirty);
            cavesChanged = true;
            return;
        }
        for (int l = 0; l < Layers; l++)
        {
            if (!tileDirty[l]) continue;
            tileDirty[l] = false;
            tileImg.SetData(W, TileH, false, Image.Format.R8, voxTile[l]);
            voxTex.UpdateLayer(tileImg, l);
            TilesUploaded++;
        }
    }

    public void Refresh(float dt)
    {
        if (World == null || Frame == null) return;
        frame++;
        profSw.Restart();
        UpdateCamera(dt);
        Lap(0);
        if (World.TerrainVersion != seenTerrain && frame % 4 == 0) RebuildVoxels();
        Lap(1);
        terrainMat.SetShaderParameter("side_lit", Slice >= 0 || !Lighting ? 1f : 0.55f);
        FillCutTemperature();
        FillTerrain();
        Lap(2);
        FillWater();
        Lap(3);
        FillAgents(dt);   // laps "agents" itself, before its uploads
        Lap(9);
        FillLinks();
        Lap(5);
        FillFlashes();
        Lap(6);
        FillDrops();
        Lap(7);
        UpdateStrikes();
        UpdateVents();
        UpdateRings();
        UpdateEclipse();
        Lap(8);
    }

    public Vector3 AgentPos(Agent a) => new(a.X + 0.5f, AgentGround(a.X, a.Y, a.Z), a.Y + 0.5f);

    public float AgentGround(int x, int y, int z) => z == World.Height[y * W + x] ? Ground(y * W + x) : z * BH;

    // The ground of each column as last drawn (ShapeColumn): what bodies, effects and marks stand on.
    readonly float[] groundTop = new float[N];

    // Where the ground of a column is: a top block that is partly eaten is drawn thinner.
    public float Ground(int i)
    {
        var w = World;
        int h = w.Height[i];
        if (h <= 0) return 0;
        if (h > Z) h = Z;
        int v = i * Z + h - 1;
        float f = 0.25f + 0.75f * w.Fill(v);
        return (h - 1 + f) * BH;
    }

    float Lit(int i) => Lighting ? MathF.Min(1f, World.Light[i]) : 1f;

    // One column's box; true if anything moved.
    bool ShapeColumn(int i)
    {
        int x = i % W, y = i / W, b = y / BandRows, o = (i - b * BandN) * 16;
        var buf = bandBuf[b];
        bool hide = Slice >= 0 && y > Slice;
        float floor = hide ? 0 : topBase[i] * BH;
        float gr = groundTop[i] = Ground(i);
        float hh = hide ? 0 : Math.Max(0, gr - floor), sxz = hide ? 0 : 1;
        bool changed = buf[o] != sxz || buf[o + 5] != hh || buf[o + 7] != floor + hh * 0.5f || buf[o + 3] != x + 0.5f;
        if (!changed) return false;
        buf[o] = sxz; buf[o + 1] = 0; buf[o + 2] = 0; buf[o + 3] = x + 0.5f;
        buf[o + 4] = 0; buf[o + 5] = hh; buf[o + 6] = 0; buf[o + 7] = floor + hh * 0.5f;
        buf[o + 8] = 0; buf[o + 9] = 0; buf[o + 10] = sxz; buf[o + 11] = y + 0.5f;
        return true;
    }

    void FillTerrain()
    {
        var w = World;
        bool all = shapedSlice != Slice;
        if (all) { cavesChanged = true; shapedSlice = Slice; }
        if (cavesChanged) FillCaves();
        // Shapes: the columns whose version changed (their top block was eaten into, laid on, removed),
        // plus two rows a frame as a sweep for anything else.
        var ver = w.ColumnVersion;
        bool changed = false;
        for (int i = 0; i < N; i++)
        {
            int v = ver[i];
            if (!all && shapedVer[i] == v) continue;
            shapedVer[i] = v;
            changed = true;
            if (ShapeColumn(i)) bandDirty[i / BandN] = true;
        }
        for (int r = 0; r < 2; r++, shapeRow = (shapeRow + 1) % H)
            for (int x = 0; x < W; x++)
                if (ShapeColumn(shapeRow * W + x)) bandDirty[shapeRow / BandRows] = true;

        // Colours: everything when the overlay or the lighting changed; otherwise one band a frame,
        // for a full round after the world last changed (nothing while it stands paused).
        if (Frame.Tick != colouredTick || changed || Overlay == 7) { colouredTick = Frame.Tick; sweepLeft = Bands; }
        if (colouredOverlay != Overlay || colouredLighting != Lighting || all)
        {
            colouredOverlay = Overlay; colouredLighting = Lighting;
            for (int i = 0; i < N; i++) ColourColumn(i);
            Array.Fill(bandDirty, true);
        }
        else if (sweepLeft > 0)
        {
            sweepLeft--;
            int b = colourBand;
            colourBand = (colourBand + 1) % Bands;
            for (int i = b * BandN; i < (b + 1) * BandN; i++) ColourColumn(i);
            bandDirty[b] = true;
        }
        for (int b = 0; b < Bands; b++)
        {
            if (!bandDirty[b]) continue;
            bandDirty[b] = false;
            bands[b].Buffer = bandBuf[b];
            BandsUploaded++;
        }
    }
    void ColourColumn(int i)
    {
        var w = World;
        var ch = w.Chem;
        int b = i / BandN, o = (i - b * BandN) * 16;
        var buf = bandBuf[b];
        int h = Math.Clamp(w.Height[i], 0, Z);
        Rgb c;
        float lit = Lit(i);
        switch (Overlay)
        {
            case 0:
                {
                    int v = i * Z + Math.Max(0, h - 1);
                    byte m = w.Mat[v];
                    c = ch.MatCol[m];
                    if (m >= 2) c = c.Mul(0.7f + 0.3f * w.Fill(v));
                    float r = 0, g = 0, bl = 0, tot = 0;
                    for (int s = 0; s < Chemistry.S; s++)
                    {
                        if (s == ch.Gas) continue;
                        float q = w.C[s][i].F;
                        tot += q; r += q * ch.Col[s].R; g += q * ch.Col[s].G; bl += q * ch.Col[s].B;
                    }
                    if (tot > 0.01f) c = c.Lerp(new Rgb(r / tot, g / tot, bl / tot), 0.4f * tot / (tot + 12f));
                    if (w.Snow[i] > 0.01f) c = c.Lerp(new Rgb(0.95f, 0.97f, 1f), Math.Min(1f, w.Snow[i] * 4));
                    break;
                }
            case 1:
                c = TempColour(w.Temp[i]);
                lit = 1;
                break;
            case DepthTempOverlay:
                // On top: the year-round mean that the climate deep under a roof starts from (World.Cave);
                // the cut (C) shows every level.
                c = TempColour(w.Tmean[i]);
                lit = 1;
                break;
            case DeepOverlay:
                // The deep element's share of the atoms in the top block (World.Geochem); the cut shows every level.
                c = h > 0 ? DeepColour(w.VoxelDeepShare(i * Z + h - 1), false) : DeepColour(0, false);
                lit = 1;
                break;
            case 2:
                // The sun's power at the surface now (World.Sun: cosine law, sky, shadows, clouds, eclipse).
                c = new Rgb(0.08f, 0.08f, 0.14f).Lerp(new Rgb(1f, 0.92f, 0.55f), MathF.Min(1, w.Sun[i]));
                lit = 1;
                break;
            case TranspOverlay:
                // Clear-sky transparency (World.Sky): murky grey-brown → clear blue.
                c = new Rgb(0.42f, 0.36f, 0.3f).Lerp(new Rgb(0.45f, 0.78f, 1f), Math.Clamp((w.Transp[i] - P.TranspMin) / Math.Max(0.01f, 1 - P.TranspMin), 0f, 1f));
                lit = 1;
                break;
            case DayOverlay:
                {
                    // Day length today: polar night black, 12 h grey-green, polar day white-yellow.
                    float d = World.DayShare(World.Latitude(i / W), w.SunDecl);
                    c = d < 0.5f ? new Rgb(0.03f, 0.03f, 0.1f).Lerp(new Rgb(0.35f, 0.5f, 0.45f), d * 2) : new Rgb(0.35f, 0.5f, 0.45f).Lerp(new Rgb(1f, 0.95f, 0.6f), d * 2 - 1);
                    lit = 1;
                    break;
                }
            case FlareOverlay:
                {
                    // The dose an unshielded body on the surface would get now; without a flare, faintly, where
                    // the sun's activity reaches.
                    float dose = w.FlarePower * w.Sun[i];
                    c = dose > 0 ? new Rgb(0.1f, 0.05f, 0.15f).Lerp(new Rgb(1f, 0.3f, 1f), dose / (dose + 1f))
                                 : new Rgb(0.05f, 0.05f, 0.08f).Lerp(new Rgb(0.35f, 0.2f, 0.45f), w.SolarActivity * w.Sun[i]);
                    lit = 1;
                    break;
                }
            case 3:
                {
                    float tot = 0;
                    for (int s = 0; s < Chemistry.S; s++) if (s != ch.Gas) tot += w.C[s][i].F;
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
                // Pressure against strength, taken on the simulation thread (SimRunner.Stress).
                c = new Rgb(0.15f, 0.6f, 0.35f).Lerp(new Rgb(1f, 0.15f, 0.04f), Math.Min(1, Stress?[i] ?? 0));
                lit = 1; break;
            case 8:
                c = new Rgb(0.28f, 0.22f, 0.16f).Lerp(new Rgb(0.6f, 0.85f, 1f), h > 0 ? w.Order[i * Z + h - 1] / 255f : 0);
                lit = 1; break;
            default:
                {
                    int s = Overlay - FirstSpecies;
                    c = new Rgb(0.06f, 0.06f, 0.07f).Lerp(ch.Col[s], MathF.Min(1, w.C[s][i].F / 3f));
                    break;
                }
        }
        // A solar flare tints the sunlit side violet-white (World.Sky).
        if (Overlay == 0 && w.FlarePower > 0 && w.Sun[i] > 0) c = c.Lerp(new Rgb(1f, 0.8f, 1f), Math.Min(0.45f, 0.12f * w.FlarePower * w.Sun[i]));
        buf[o + 12] = c.R; buf[o + 13] = c.G; buf[o + 14] = c.B; buf[o + 15] = lit;
    }

    // −25 °C deep blue · 0 °C white · +40 °C red
    public static Rgb TempColour(float tc) =>
        tc < 0 ? new Rgb(0.95f, 0.95f, 0.97f).Lerp(new Rgb(0.12f, 0.25f, 0.8f), Math.Min(1, -tc / 25f))
               : new Rgb(0.95f, 0.95f, 0.97f).Lerp(new Rgb(0.95f, 0.2f, 0.08f), Math.Min(1, tc / 40f));

    // The deep element's share of a block's atoms: slate (none) · ochre · bright gold (all); a vein paler.
    public static Rgb DeepColour(float share, bool vein)
    {
        var c = new Rgb(0.16f, 0.18f, 0.24f).Lerp(new Rgb(0.85f, 0.55f, 0.12f), Math.Min(1, share * 1.6f));
        if (share > 0.62f) c = c.Lerp(new Rgb(1f, 0.93f, 0.45f), Math.Min(1, (share - 0.62f) / 0.38f));
        return vein ? c.Lerp(new Rgb(0.95f, 0.98f, 1f), 0.35f) : c;
    }

    // "Temperature at depth" in the cut: the face of the cut row shows, level by level, what a body there
    // would feel (World.LocalTemp: the surface's, or under a roof the cave climate by its cover). Read
    // only; refreshed a few times a second.
    Image cutImg;
    ImageTexture cutTex;
    readonly byte[] cutBytes = new byte[W * Z * 3];
    int cutShownRow = -2;
    double cutAt;
    void FillCutTemperature()
    {
        bool on = (Overlay == DepthTempOverlay || Overlay == DeepOverlay) && Slice >= 0;
        int row = on ? Slice : -1;
        double now = Time.GetTicksMsec() / 1000.0;
        if (row == cutShownRow && Overlay == cutShownOverlay && (!on || now - cutAt < 0.25)) return;
        if (row != cutShownRow) terrainMat.SetShaderParameter("cut_row", row);
        cutShownRow = row; cutShownOverlay = Overlay;
        if (!on) return;
        cutAt = now;
        var w = World;
        if (Overlay == DeepOverlay) FillCutDeep(row);
        else FillCutTemp(row);
        if (cutImg == null)
        {
            cutImg = Image.CreateFromData(W, Z, false, Image.Format.Rgb8, cutBytes);
            cutTex = ImageTexture.CreateFromImage(cutImg);
            terrainMat.SetShaderParameter("cut_temp", cutTex);
        }
        else
        {
            cutImg.SetData(W, Z, false, Image.Format.Rgb8, cutBytes);
            cutTex.Update(cutImg);
        }
    }
    int cutShownOverlay = -1;

    // The deep element by level along the cut: its share of each block's atoms, veins paler (World.Geochem).
    void FillCutDeep(int row)
    {
        var w = World;
        for (int x = 0; x < W; x++)
        {
            int c = row * W + x, h = Math.Clamp(w.Height[c], 0, Z), h0 = w.Height0[c];
            for (int z = 0; z < Z; z++)
            {
                var col = z < h ? DeepColour(w.VoxelDeepShare(c * Z + z), w.GeoOn && z >= 2 && z < h0 && w.InVein(x, row, z, h0 - 1 - z)) : new Rgb(0.1f, 0.1f, 0.12f);
                int o = (z * W + x) * 3;
                cutBytes[o] = (byte)(col.R * 255); cutBytes[o + 1] = (byte)(col.G * 255); cutBytes[o + 2] = (byte)(col.B * 255);
            }
        }
    }

    void FillCutTemp(int row)
    {
        var w = World;
        bool law = World.CaveLaw;
        for (int x = 0; x < W; x++)
        {
            int c = row * W + x, h = Math.Clamp(w.Height[c], 0, Z), roof = 0;
            float surf = w.Temp[c], mean = w.Tmean[c], warm = w.CaveWarm[c];
            for (int z = Z - 1; z >= 0; z--)
            {
                float t = surf;
                if (law && z < h && roof > 0)
                {
                    float f = 1 - MathF.Exp(-roof / P.CaveDepthK);
                    t = surf + (mean + P.GeoGrad * (h - 1 - z) + warm - surf) * f;
                }
                if (z < h && w.Mat[c * Z + z] != Chemistry.Air) roof++;
                var col = TempColour(t);
                int o = (z * W + x) * 3;
                cutBytes[o] = (byte)(col.R * 255); cutBytes[o + 1] = (byte)(col.G * 255); cutBytes[o + 2] = (byte)(col.B * 255);
            }
        }
    }

    // Draw each solid run separately: cave floors and ceilings are actual faces, not painted holes.
    void FillCaves()
    {
        cavesChanged = false;
        int count = 0;
        foreach (var pair in caveRuns) if (Slice < 0 || pair.Key / W <= Slice) count += pair.Value.Count;
        Ensure(caveTerrain, ref caveBuf, count);
        int o = 0;
        foreach (var pair in caveRuns)
        {
            int c = pair.Key;
            if (Slice >= 0 && c / W > Slice) continue;
            foreach (var run in pair.Value)
            {
                var colour = World.Chem.MatCol[World.Mat[c * Z + run.top - 1]];
                Array.Clear(caveBuf, o, 16);
                caveBuf[o] = 1; caveBuf[o + 3] = c % W + 0.5f;
                caveBuf[o + 5] = (run.top - run.bottom) * BH;
                caveBuf[o + 7] = (run.top + run.bottom) * BH * 0.5f;
                caveBuf[o + 10] = 1; caveBuf[o + 11] = c / W + 0.5f;
                caveBuf[o + 12] = colour.R; caveBuf[o + 13] = colour.G; caveBuf[o + 14] = colour.B; caveBuf[o + 15] = 0.6f;
                o += 16;
            }
        }
        caveTerrain.Buffer = caveBuf;
        caveTerrain.VisibleInstanceCount = count;
    }

    void UpdateVents()
    {
        var vents = Frame.Vents;
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
            ventMarks[k].Position = new Vector3(v.X + 0.5f, groundTop[v.Y * W + v.X] + 1.2f, v.Y + 0.5f);
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

    // Fly to a column (a chronicle event whose body is gone).
    public void LookAtCell(int x, int y)
    {
        if (x < 0 || y < 0 || x >= W || y >= H) return;
        Follow = false;
        target = new Vector3(x + 0.5f, Ground(y * W + x), y + 0.5f);
        zoom = Math.Min(zoom, 60);
    }

    public void LookAt(Agent a)
    {
        target = AgentPos(a);
        zoom = Math.Min(zoom, 40);
    }

    // Ray-march the heightfield under the cursor, then take the nearest agent (of the published frame).
    public Agent Pick(Vector2 screen)
    {
        var w = World;
        var f = Frame;
        if (f == null) return null;
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
            int z = (int)MathF.Floor(p.Y / BH);
            if (z >= 0 && z < Z && w.Mat[(y * W + x) * Z + z] != Chemistry.Air) { hit = p; break; }
        }
        Agent best = null;
        float bd = 2.2f * 2.2f;
        var snaps = f.Agents;
        for (int k = 0; k < f.Count; k++)
        {
            ref var s = ref snaps[k];
            if (s.Ref == null) continue;
            float dx = s.X + 0.5f - hit.X, dz = s.Y + 0.5f - hit.Z, dd = dx * dx + dz * dz;
            if (dd < bd) { bd = dd; best = s.Ref; }
        }
        return best;
    }
}
