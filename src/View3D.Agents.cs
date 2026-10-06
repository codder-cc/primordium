using System;

using Godot;

namespace Primordium;

// Agents: a body whose shape, proportions and colour come from the genome fingerprint (relatives look
// alike), a cap on top coloured by how it feeds, small marks for rare organs. Movement is smoothed,
// notable actions are animated, links between colony members are drawn, events leave short effects.
//
// Bodies come from the published SimFrame. Only bodies in front of the camera are filled (in parallel
// chunks); from far away the organ marks are left out and bodies and caps use coarser meshes.
public partial class View3D
{
    public static readonly string[] ColorModeNames = { "родство", "питание", "линия", "энергия", "органы" };

    public Agent Hover;
    public bool KinFocus = true;          // dim everything that isn't kin of the selected agent
    public int Speed = 8;                 // ticks per frame, so effects last long enough to be seen
    public int AgentsDrawn;               // diagnostics
    public bool FarLod;                   // zoomed out: coarse meshes, no organ marks

    readonly MultiMesh[] bodies = new MultiMesh[Looks.ShapeCount];
    readonly Mesh[] nearMesh = new Mesh[Looks.ShapeCount], farMesh = new Mesh[Looks.ShapeCount];
    readonly float[][] bodyBuf = new float[Looks.ShapeCount][];
    MultiMesh caps, marks, links, flashes, foots;
    float[] capBuf = Array.Empty<float>(), markBuf = Array.Empty<float>(), linkBuf = Array.Empty<float>(), footBuf = Array.Empty<float>();
    readonly float[] flashBuf = new float[World.FlashCap * 16];
    MeshInstance3D selRing, hoverRing, brushRing;
    Mesh capNear, capFar;
    public int BrushCell = -1, BrushTool;   // the hand's brush under the cursor (set by Main)
    public float BrushR;
    static readonly Color[] BrushColors = { default, new(0.95f, 0.8f, 0.45f), new(0.35f, 0.65f, 1f), new(1f, 0.3f, 0.25f), new(0.75f, 0.55f, 0.35f) };
    DirectionalLight3D key;
    double now, worldShownAt;
    bool lodApplied;

    // Parallel fill: per chunk, how many instances of each kind it adds (and where they start).
    const int MaxChunks = 32, Kinds = Looks.ShapeCount + 3;   // shapes, caps, marks, feet
    readonly int[] chunkCount = new int[MaxChunks * Kinds], chunkStart = new int[MaxChunks * Kinds];
    byte[] visible = Array.Empty<byte>();
    public static int ViewWorkers = Math.Clamp(System.Environment.ProcessorCount / 3, 1, 4);   // --viewthreads
    readonly WorkGang gang = new(ViewWorkers);
    public override void _ExitTree() => gang.Stop();

    static readonly Color Red = new(1f, 0.18f, 0.12f), Purple = new(0.85f, 0.3f, 1f), Cyan = new(0.3f, 1f, 1f);

    static Mesh ShapeMesh(int k, bool far) => k switch
    {
        0 => new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = far ? 6 : 14, Rings = far ? 3 : 7 },
        1 => new BoxMesh { Size = new Vector3(0.8f, 0.8f, 0.8f) },
        2 => new CylinderMesh { TopRadius = 0f, BottomRadius = 0.52f, Height = 1f, RadialSegments = far ? 5 : 12, Rings = far ? 0 : 1 },
        3 => new CylinderMesh { TopRadius = 0.38f, BottomRadius = 0.38f, Height = 0.9f, RadialSegments = far ? 6 : 12, Rings = far ? 0 : 1 },
        4 => new CapsuleMesh { Radius = 0.3f, Height = 1f, RadialSegments = far ? 6 : 12, Rings = far ? 1 : 4 },
        5 => new PrismMesh { Size = new Vector3(0.9f, 0.95f, 0.9f) },
        6 => new TorusMesh { InnerRadius = 0.16f, OuterRadius = 0.5f, Rings = far ? 8 : 16, RingSegments = far ? 4 : 8 },
        _ => new SphereMesh { Radius = 0.56f, Height = 1.12f, RadialSegments = 4, Rings = 2 },
    };

    void BuildAgentNodes()
    {
        var shaded = new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.7f };
        var flat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
        };
        for (int k = 0; k < Looks.ShapeCount; k++)
        {
            nearMesh[k] = ShapeMesh(k, false);
            farMesh[k] = ShapeMesh(k, true);
            bodies[k] = NewMM(nearMesh[k], shaded);
            bodyBuf[k] = Array.Empty<float>();
        }
        capNear = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 10, Rings = 5 };
        capFar = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 5, Rings = 2 };
        caps = NewMM(capNear, flat);
        marks = NewMM(new BoxMesh(), flat);
        links = NewMM(new BoxMesh(), flat);
        foots = NewMM(new BoxMesh(), flat);
        flashes = NewMM(new BoxMesh(), flat);
        flashes.InstanceCount = World.FlashCap;

        selRing = Ring(0.85f, 1.05f, new Color(1, 1, 1));
        hoverRing = Ring(0.8f, 0.92f, new Color(1f, 0.85f, 0.4f));
        brushRing = Ring(0.96f, 1f, new Color(1, 1, 1));

        // Agents are shaded by a key light that follows the camera, so their shapes read clearly.
        key = new DirectionalLight3D { LightEnergy = 1.15f, ShadowEnabled = false };
        AddChild(key);
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.045f, 0.05f, 0.065f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.62f, 0.64f, 0.7f),
                AmbientLightEnergy = 0.85f,
            },
        });
    }

    MultiMesh NewMM(Mesh mesh, Material mat)
    {
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = mesh };
        AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        return mm;
    }

    MeshInstance3D Ring(float inner, float outer, Color c)
    {
        var m = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = inner, OuterRadius = outer, Rings = 32, RingSegments = 6 },
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c },
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(m);
        return m;
    }

    // Room for `need` instances. The whole buffer is copied to the GPU on every set, so it is kept
    // close to what is drawn: a quarter over the need, reallocated when outgrown or four times too big.
    static void Ensure(MultiMesh mm, ref float[] buf, int need)
    {
        int cap = mm.InstanceCount;
        if (cap > 0 && need <= cap && (need >= cap / 4 || cap <= 1024) && buf.Length == cap * 16) return;
        cap = (Math.Max(256, need + need / 4) + 255) & ~255;
        mm.InstanceCount = cap;
        buf = new float[cap * 16];
    }

    // One instance: basis columns x, y, z and origin o, written row by row as MultiMesh expects.
    static void Put(float[] b, int n, Vector3 x, Vector3 y, Vector3 z, Vector3 o, Color c)
    {
        int k = n * 16;
        b[k] = x.X; b[k + 1] = y.X; b[k + 2] = z.X; b[k + 3] = o.X;
        b[k + 4] = x.Y; b[k + 5] = y.Y; b[k + 6] = z.Y; b[k + 7] = o.Y;
        b[k + 8] = x.Z; b[k + 9] = y.Z; b[k + 10] = z.Z; b[k + 11] = o.Z;
        b[k + 12] = c.R; b[k + 13] = c.G; b[k + 14] = c.B; b[k + 15] = 1;
    }

    static void PutBox(float[] b, int n, Vector3 o, Vector3 s, Color c) =>
        Put(b, n, new Vector3(s.X, 0, 0), new Vector3(0, s.Y, 0), new Vector3(0, 0, s.Z), o, c);

    // A thin bar from p to q.
    static void PutBeam(float[] b, int n, Vector3 p, Vector3 q, float th, Color c)
    {
        var d = q - p;
        float len = d.Length();
        if (len < 1e-3f) { Put(b, n, Vector3.Zero, Vector3.Zero, Vector3.Zero, p, c); return; }
        var x = d;
        var side = d.Cross(Vector3.Up);
        side = side.LengthSquared() < 1e-6f ? Vector3.Right * th : side.Normalized() * th;
        var up = side.Cross(d).Normalized() * th;
        Put(b, n, x, up, side, (p + q) * 0.5f, c);
    }

    public static Color KinColor(Agent a) => Color.FromHsv(a.Hue, a.Sat, a.Val);

    public static Color DietColor(Agent a) => DietColor(a.EmaPhoto, a.EmaChem, a.EmaMine, a.EmaAttack);

    static Color DietColor(float p, float c, float mine, float attack)
    {
        if (attack > 0.004f) return new Color(1f, 0.2f, 0.15f);
        float m = mine * 0.3f, sum = p + c + m;
        if (sum < 0.003f) return new Color(0.6f, 0.6f, 0.64f);
        return new Color(
            (p * 0.35f + c * 0.3f + m * 1.0f) / sum,
            (p * 0.95f + c * 0.62f + m * 0.62f) / sum,
            (p * 0.3f + c * 1.0f + m * 0.2f) / sum);
    }

    // What its proteins do: green = catching light, blue = splitting/joining molecules, red = motor.
    static Color OrganColor(in AgentSnap a)
    {
        float g = a.EnzPhoto, b = a.EnzChem, r = a.EnzMotor;
        if (r + g + b < 0.05f) return new Color(0.35f, 0.35f, 0.38f);
        float k = 1f / MathF.Max(r, MathF.Max(g, b));
        return new Color(0.25f + 0.75f * r * k, 0.25f + 0.75f * g * k, 0.25f + 0.75f * b * k);
    }

    Color BodyColor(in AgentSnap a) => ColorMode switch
    {
        1 => DietColor(a.EmaPhoto, a.EmaChem, a.EmaMine, a.EmaAttack),
        2 => Color.FromHsv(a.Lineage * 0.618034f % 1f, 0.7f, 0.95f),
        3 => new Color(0.95f, 0.25f, 0.2f).Lerp(new Color(0.35f, 0.95f, 0.45f), a.EnergyK),
        4 => OrganColor(a),
        _ => Color.FromHsv(a.Hue, a.Sat, a.Val),
    };

    // Looks.Kin on the copied looks.
    static float Kin(in AgentSnap a, Agent b)
    {
        if (a.Lineage != b.Lineage) return 0;
        float dh = MathF.Abs(a.Hue - b.Hue);
        dh = MathF.Min(dh, 1 - dh);
        float d = dh * 2f + MathF.Abs(a.Sat - b.Sat) + MathF.Abs(a.Val - b.Val) + (a.Shape != b.Shape ? 0.15f : 0f);
        return Math.Max(0f, 1f - d / 0.5f);
    }

    public Vector3 VisualPos(Agent a) => new(a.VX + 0.5f, a.VH, a.VY + 0.5f);

    void FillAgents(float dt)
    {
        var f = Frame;
        var w = World;
        now = Time.GetTicksMsec() / 1000.0;
        int n = f.Count;
        var snaps = f.Agents;
        if (visible.Length < n) visible = new byte[n + n / 4];

        // What the camera sees: a box in camera space (orthographic), with a margin.
        var vs = GetViewport().GetVisibleRect().Size;
        var ct = Cam.GlobalTransform;
        Vector3 R = ct.Basis.X, U = ct.Basis.Y;
        float hh = zoom * 0.5f + 3, hw = zoom * 0.5f * vs.X / Math.Max(1, vs.Y) + 3;
        float ro = R.Dot(ct.Origin) + Cam.HOffset, uo = U.Dot(ct.Origin);
        bool far = vs.Y / zoom < 6;   // under ~6 px per cell
        FarLod = far;
        if (far != lodApplied || bodies[0].Mesh == null)
        {
            lodApplied = far;
            for (int k = 0; k < Looks.ShapeCount; k++) bodies[k].Mesh = far ? farMesh[k] : nearMesh[k];
            caps.Mesh = far ? capFar : capNear;
        }

        var sel = Selected;
        bool focus = KinFocus && sel != null;
        bool fresh = now - worldShownAt < 0.5;
        float ks = 1 - MathF.Exp(-dt * 14);
        float t = (float)now;
        int slice = Slice;
        bool lighting = Lighting;
        var height = w.Height;

        int chunks = Math.Clamp(n / 2048, 1, MaxChunks);
        Array.Clear(chunkCount, 0, chunks * Kinds);

        // Pass 1: which bodies are seen and how many instances of each kind every chunk adds.
        gang.For(chunks, ch =>
        {
            int from = (int)((long)n * ch / chunks), to = (int)((long)n * (ch + 1) / chunks), o = ch * Kinds;
            for (int i = from; i < to; i++)
            {
                ref var s = ref snaps[i];
                visible[i] = 0;
                if (s.Ref == null) continue;   // died between ticks (by the hand)
                if (slice >= 0 && s.Y > slice) continue;
                float px = s.X + 0.5f, pz = s.Y + 0.5f, py = s.Z * BH;
                float cx = R.X * px + R.Y * py + R.Z * pz - ro, cy = U.X * px + U.Y * py + U.Z * pz - uo;
                if (cx < -hw || cx > hw || cy < -hh || cy > hh)
                {
                    var a = s.Ref;
                    if (a.SeenAt == 0) { a.SeenAt = -10; a.VX = s.X; a.VY = s.Y; }
                    continue;
                }
                bool dim = focus && s.Ref != sel && Kin(s, sel) <= 0;
                visible[i] = (byte)(dim ? 2 : 1);
                chunkCount[o + s.Shape]++;
                if (!dim)
                {
                    chunkCount[o + Looks.ShapeCount]++;
                    if (!far && s.EnzTotal >= 0.5f) chunkCount[o + Looks.ShapeCount + 1]++;
                }
                if (s.Cells > 1) chunkCount[o + Looks.ShapeCount + 2] += s.Cells;
            }
        });
        int drawn = 0;
        for (int k = 0; k < Kinds; k++)
        {
            int sum = 0;
            for (int ch = 0; ch < chunks; ch++) { chunkStart[ch * Kinds + k] = sum; sum += chunkCount[ch * Kinds + k]; }
            if (k < Looks.ShapeCount) { Ensure(bodies[k], ref bodyBuf[k], sum); drawn += sum; }
            else if (k == Looks.ShapeCount) Ensure(caps, ref capBuf, sum);
            else if (k == Looks.ShapeCount + 1) Ensure(marks, ref markBuf, sum);
            else Ensure(foots, ref footBuf, sum);
        }
        AgentsDrawn = drawn;

        // Pass 2: fill the instances, each chunk at its own place.
        gang.For(chunks, ch =>
        {
            int from = (int)((long)n * ch / chunks), to = (int)((long)n * (ch + 1) / chunks), o = ch * Kinds;
            Span<int> at = stackalloc int[Kinds];
            for (int k = 0; k < Kinds; k++) at[k] = chunkStart[o + k];
            int capK = Looks.ShapeCount, markK = capK + 1, footK = capK + 2;
            for (int i = from; i < to; i++)
            {
                if (visible[i] == 0) continue;
                ref var s = ref snaps[i];
                var a = s.Ref;
                bool dim = visible[i] == 2;
                if (a.SeenAt == 0) { a.SeenAt = fresh ? -10 : now; a.VX = s.X; a.VY = s.Y; }

                // Glide from cell to cell (the world wraps around in x).
                float dx = s.X - a.VX;
                if (dx > W / 2f) a.VX += W; else if (dx < -W / 2f) a.VX -= W;
                if (MathF.Abs(s.X - a.VX) > 2.5f || MathF.Abs(s.Y - a.VY) > 2.5f) { a.VX = s.X; a.VY = s.Y; }
                else { a.VX += (s.X - a.VX) * ks; a.VY += (s.Y - a.VY) * ks; }

                if (s.ActTick != a.SeenAct)
                {
                    a.SeenAct = s.ActTick;
                    if (s.Act > World.ActMove) { a.AnimKind = s.Act; a.AnimDir = s.ActDir; a.AnimAt = now; }
                }

                float kin = focus && a != sel ? Kin(s, sel) : 1f;
                float sc = Math.Clamp(0.42f + 0.07f * MathF.Sqrt(s.Mass), 0.42f, 1.0f) * (dim ? 0.75f : 1f);
                float sx = sc * s.Sx, sy = sc * s.Sy, sz = sc * s.Sz, ox = 0, oy = 0, oz = 0, tintK = 0;
                Color tint = default;

                float p = (float)((now - a.AnimAt) / 0.45);
                if (p < 1)
                {
                    float bump = MathF.Sin(MathF.PI * p);
                    float ddx = a.AnimDir is >= 0 and < 4 ? World.DX[a.AnimDir] : 0, ddz = a.AnimDir is >= 0 and < 4 ? World.DY[a.AnimDir] : 0;
                    switch (a.AnimKind)
                    {
                        case World.ActAttack: ox = ddx * 0.55f * bump; oz = ddz * 0.55f * bump; tint = Red; tintK = bump; break;
                        case World.ActInject: ox = ddx * 0.35f * bump; oz = ddz * 0.35f * bump; tint = Purple; tintK = 0.8f * bump; break;
                        case World.ActDivide: sx *= 1 + 0.4f * bump; sz *= 1 + 0.4f * bump; sy *= 1 - 0.3f * bump; break;
                        case World.ActEat: sy *= 1 + 0.3f * MathF.Sin(2 * MathF.PI * p) * (1 - p); break;
                        case World.ActExpel: ox = -ddx * 0.25f * bump; oz = -ddz * 0.25f * bump; break;
                        case World.ActSocial: ox = ddx * 0.22f * bump; oz = ddz * 0.22f * bump; tint = Cyan; tintK = 0.5f * bump; break;
                        case World.ActDig: oy = -0.3f * bump; sy *= 1 - 0.3f * bump; break;
                    }
                }
                float age = (float)(now - a.SeenAt);
                if (age < 0.4f)
                {
                    float q = 0.15f + 0.85f * age / 0.4f;
                    sx *= q; sy *= q; sz *= q;
                }
                sy *= 1 + 0.04f * MathF.Sin(t * 2.3f + s.Id * 0.37f);

                // Height follows the ground smoothly; climbing a step is a little hop.
                int cell = s.Y * W + s.X;
                float ground = s.Z == height[cell] ? groundTop[cell] : s.Z * BH;
                if (s.Lift > 0) ground += (s.Lift + w.Ice[cell]) * BH;   // swimming above the bottom (the ice lies under the water here)
                if (s.Cells > 1)
                {
                    // A big body: drawn larger, centred over all its cells, which get a tinted floor.
                    float mx = 0, mz = 0;
                    var kc = Color.FromHsv(s.Hue, s.Sat, s.Val);
                    for (int k = 0; k < s.Cells; k++)
                    {
                        int fc = f.Feet[s.Foot + k];
                        int fx = fc % W, fy = fc / W, ddx = fx - s.X;
                        if (ddx > W / 2) ddx -= W; else if (ddx < -W / 2) ddx += W;
                        mx += ddx; mz += fy - s.Y;
                        float lf = lighting ? MathF.Max(0.5f, 0.35f + 0.65f * w.Light[fc]) : 1f;
                        PutBox(footBuf, at[footK]++, new Vector3(a.VX + ddx + 0.5f, s.Z * BH + 0.03f, a.VY + fy - s.Y + 0.5f),
                            new Vector3(0.94f, 0.05f, 0.94f), new Color(kc.R * 0.5f * lf, kc.G * 0.5f * lf, kc.B * 0.5f * lf));
                    }
                    ox += mx / s.Cells; oz += mz / s.Cells;
                    float g = 0.6f + 0.55f * MathF.Sqrt(s.Cells);
                    sx *= g; sy *= g; sz *= g;
                }
                else
                {
                    // Several bodies in one cell: each stands at its own spot in it, smaller when crowded.
                    int crowd = s.Crowd;
                    float spread = crowd > 1 ? 0.3f : 0f;
                    ox += spread * (Hash32.F(s.Id, 1) * 2 - 1);
                    oz += spread * (Hash32.F(s.Id, 2) * 2 - 1);
                    if (crowd > 1) { float q = Math.Max(0.25f, 1.4f / MathF.Sqrt(crowd)); sx *= q; sy *= q; sz *= q; }
                }
                if (age < 0.05f || MathF.Abs(ground - a.VH) > 4) a.VH = ground;
                float rise = ground - a.VH;
                a.VH += rise * ks;
                float hop = rise > 0.05f && s.Lift == 0 ? 0.6f * MathF.Sqrt(rise) : 0;   // swimmers rise smoothly
                var pos = new Vector3(a.VX + 0.5f + ox, a.VH + hop + sy * 0.5f + oy + 0.02f, a.VY + 0.5f + oz);
                float lum = lighting ? MathF.Max(0.72f, 0.45f + 0.55f * w.Light[cell]) : 1f;

                var col = BodyColor(s);
                if (tintK > 0) col = col.Lerp(tint, tintK);
                if (dim) col = col.Darkened(0.8f);
                else if (kin < 1) col = col.Darkened(0.45f * (1 - kin));   // distant relatives a bit darker
                col = new Color(col.R * lum, col.G * lum, col.B * lum);

                // Every individual is turned its own way (golden angle by id).
                float r = s.Id * 2.39996f, cr = MathF.Cos(r), sr = MathF.Sin(r);
                int sh = s.Shape;
                Put(bodyBuf[sh], at[sh]++, new Vector3(cr * sx, 0, -sr * sx), new Vector3(0, sy, 0), new Vector3(sr * sz, 0, cr * sz), pos, col);
                if (dim) continue;

                var cc = DietColor(s.EmaPhoto, s.EmaChem, s.EmaMine, s.EmaAttack);
                PutBox(capBuf, at[capK]++, pos + new Vector3(0, sy * 0.5f + 0.06f, 0), new Vector3(0.42f * sc, 0.16f * sc, 0.42f * sc), new Color(cc.R * lum, cc.G * lum, cc.B * lum));
                if (!far && s.EnzTotal >= 0.5f)
                {
                    var oc = OrganColor(s);
                    PutBox(markBuf, at[markK]++, pos + new Vector3(cr * sx * 0.55f, 0, -sr * sx * 0.55f), new Vector3(0.24f, 0.24f, 0.24f) * sc, oc);
                }
            }
        });

        Lap(4);
        for (int k = 0; k < Looks.ShapeCount; k++)
        {
            bodies[k].Buffer = bodyBuf[k];
            bodies[k].VisibleInstanceCount = chunkTotal(k, chunks);
        }
        caps.Buffer = capBuf;
        caps.VisibleInstanceCount = chunkTotal(Looks.ShapeCount, chunks);
        marks.Buffer = markBuf;
        marks.VisibleInstanceCount = chunkTotal(Looks.ShapeCount + 1, chunks);
        foots.Buffer = footBuf;
        foots.VisibleInstanceCount = chunkTotal(Looks.ShapeCount + 2, chunks);
    }

    int chunkTotal(int k, int chunks) => chunkStart[(chunks - 1) * Kinds + k] + chunkCount[(chunks - 1) * Kinds + k];

    // Colony links: a cyan bar between linked neighbours.
    void FillLinks()
    {
        var f = Frame;
        Ensure(links, ref linkBuf, f.LinkN);
        int n = 0;
        for (int k = 0; k < f.LinkN; k++)
        {
            Agent a = f.LinkA[k], b = f.LinkB[k];
            if (a == null || b == null || MathF.Abs(a.VX - b.VX) > W / 2f) continue;
            if (Slice >= 0 && (a.Y > Slice || b.Y > Slice)) continue;
            if (a.SeenAt == 0 || b.SeenAt == 0) continue;   // not placed yet
            var pa = VisualPos(a) + new Vector3(0, 0.35f, 0);
            var pb = VisualPos(b) + new Vector3(0, 0.35f, 0);
            PutBeam(linkBuf, n++, pa, pb, 0.09f, Cyan);
        }
        links.Buffer = linkBuf;
        links.VisibleInstanceCount = n;
    }

    // Short-lived effects: beams for attacks and gene injections, bursts for kills, puffs of
    // expelled molecules, dust for digging, fading ghosts for deaths.
    void FillFlashes()
    {
        var f = Frame;
        var ch = World.Chem;
        long life = Math.Max(24, Speed * 8);
        int n = 0;
        foreach (var fl in f.Flashes)
        {
            if (n >= World.FlashCap - 2) break;
            long age = f.Tick - fl.T;
            if (fl.T == 0 || age > life || age < 0) continue;
            if ((uint)fl.X >= W || (uint)fl.Y >= H) continue;
            if (Slice >= 0 && fl.Y > Slice) continue;
            float k = 1 - age / (float)life;
            float gy = groundTop[fl.Y * W + fl.X];
            var at = new Vector3(fl.X + 0.5f, gy + 0.4f, fl.Y + 0.5f);
            bool dir = fl.Dir is >= 0 and < 4;
            var from = dir ? at - new Vector3(World.DX[fl.Dir], 0, World.DY[fl.Dir]) : at;
            switch (fl.Kind)
            {
                case World.FlashAttack:
                    PutBeam(flashBuf, n++, from, at, 0.13f * k, Red * (0.4f + 0.6f * k));
                    break;
                case World.FlashKill:
                    {
                        float s = 0.4f + 0.7f * (1 - k);
                        PutBox(flashBuf, n++, at + new Vector3(0, 0.5f * (1 - k), 0), new Vector3(s, 0.08f, s), Red * k);
                        PutBox(flashBuf, n++, at, Vector3.One * 0.3f * k, new Color(1f, 0.85f, 0.8f) * k);
                        break;
                    }
                case World.FlashInject:
                    PutBeam(flashBuf, n++, from + new Vector3(0, 0.15f, 0), at + new Vector3(0, 0.15f, 0), 0.1f, Purple * (0.4f + 0.6f * k));
                    break;
                case World.FlashLink:
                    PutBeam(flashBuf, n++, from, at, 0.12f * k, Cyan * k);
                    break;
                case World.FlashExpel:
                    {
                        if (!dir || (uint)fl.Spec >= Chemistry.S) break;
                        var c = ch.Col[fl.Spec];
                        float d = 0.5f + 1.3f * (1 - k);
                        var o = at + new Vector3(World.DX[fl.Dir] * d, -0.15f, World.DY[fl.Dir] * d);
                        PutBox(flashBuf, n++, o, Vector3.One * 0.2f * k, new Color(c.R, c.G, c.B));
                        break;
                    }
                case World.FlashDig:
                    PutBox(flashBuf, n++, at + new Vector3(0, 0.8f * (1 - k), 0), Vector3.One * 0.5f * k, new Color(1f, 0.62f, 0.2f) * k);
                    break;
                case World.FlashPile:
                    PutBox(flashBuf, n++, at + new Vector3(0, 0.8f * k, 0), Vector3.One * 0.6f * k, new Color(0.95f, 0.95f, 0.85f));
                    break;
                case World.FlashDeath:
                    {
                        float s = 0.55f * k;
                        PutBox(flashBuf, n++, at - new Vector3(0, 0.2f, 0), new Vector3(s, s, s), new Color(0.55f, 0.55f, 0.6f) * (0.3f + 0.7f * k));
                        break;
                    }
            }
        }
        flashes.Buffer = flashBuf;
        flashes.VisibleInstanceCount = n;
    }

    void UpdateRings()
    {
        Place(selRing, Selected, 1f);
        Place(hoverRing, Hover != Selected ? Hover : null, 0.9f);
        // The brush: a ring as wide as it reaches, lying on the ground (or the water) under the cursor.
        brushRing.Visible = BrushCell >= 0 && BrushTool > 0;
        if (!brushRing.Visible) return;
        var w = World;
        int c = BrushCell;
        float g = groundTop[c] + (w.Ice[c] + w.Water[c]) * BH;
        brushRing.Position = new Vector3(c % W + 0.5f, g + 0.15f, c / W + 0.5f);
        brushRing.Scale = new Vector3(BrushR, 1, BrushR);
        ((StandardMaterial3D)brushRing.MaterialOverride).AlbedoColor = BrushColors[Math.Clamp(BrushTool, 0, BrushColors.Length - 1)];
    }

    void Place(MeshInstance3D ring, Agent a, float scale)
    {
        bool on = a is { Dead: false } && a.SeenAt != 0 && (Slice < 0 || a.Y <= Slice);
        ring.Visible = on;
        if (!on) return;
        ring.Position = VisualPos(a) + new Vector3(0, 0.06f, 0);
        ring.Scale = Vector3.One * scale * (1 + 0.06f * MathF.Sin((float)now * 5));
    }
}
