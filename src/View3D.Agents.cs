using System;
using Godot;

namespace Primordium;

// Agents: a body whose shape, proportions and colour come from the genome fingerprint (relatives look
// alike), a cap on top coloured by how it feeds, small marks for rare organs. Movement is smoothed,
// notable actions are animated, links between colony members are drawn, events leave short effects.
public partial class View3D
{
    public static readonly string[] ColorModeNames = { "родство", "питание", "линия", "энергия", "органы" };

    public Agent Hover;
    public bool KinFocus = true;          // dim everything that isn't kin of the selected agent
    public int Speed = 8;                 // ticks per frame, so effects last long enough to be seen

    readonly MultiMesh[] bodies = new MultiMesh[Looks.ShapeCount];
    readonly float[][] bodyBuf = new float[Looks.ShapeCount][];
    readonly int[] bodyN = new int[Looks.ShapeCount];
    MultiMesh caps, marks, links, flashes, foots;
    float[] capBuf = Array.Empty<float>(), markBuf = Array.Empty<float>(), linkBuf = Array.Empty<float>(), footBuf = Array.Empty<float>();
    readonly float[] flashBuf = new float[World.FlashCap * 16];
    MeshInstance3D selRing, hoverRing, brushRing;
    public int BrushCell = -1, BrushTool;   // the hand's brush under the cursor (set by Main)
    public float BrushR;
    static readonly Color[] BrushColors = { default, new(0.95f, 0.8f, 0.45f), new(0.35f, 0.65f, 1f), new(1f, 0.3f, 0.25f), new(0.75f, 0.55f, 0.35f) };
    DirectionalLight3D key;
    double now, worldShownAt;

    static readonly Color Red = new(1f, 0.18f, 0.12f), Purple = new(0.85f, 0.3f, 1f), Cyan = new(0.3f, 1f, 1f);

    static Mesh ShapeMesh(int k) => k switch
    {
        0 => new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 14, Rings = 7 },
        1 => new BoxMesh { Size = new Vector3(0.8f, 0.8f, 0.8f) },
        2 => new CylinderMesh { TopRadius = 0f, BottomRadius = 0.52f, Height = 1f, RadialSegments = 12, Rings = 1 },
        3 => new CylinderMesh { TopRadius = 0.38f, BottomRadius = 0.38f, Height = 0.9f, RadialSegments = 12, Rings = 1 },
        4 => new CapsuleMesh { Radius = 0.3f, Height = 1f, RadialSegments = 12, Rings = 4 },
        5 => new PrismMesh { Size = new Vector3(0.9f, 0.95f, 0.9f) },
        6 => new TorusMesh { InnerRadius = 0.16f, OuterRadius = 0.5f, Rings = 16, RingSegments = 8 },
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
            bodies[k] = NewMM(ShapeMesh(k), shaded);
            bodyBuf[k] = Array.Empty<float>();
        }
        caps = NewMM(new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 10, Rings = 5 }, flat);
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

    static void Ensure(MultiMesh mm, ref float[] buf, int need)
    {
        if (need <= mm.InstanceCount && buf.Length == mm.InstanceCount * 16 && buf.Length > 0) return;
        int cap = Math.Max(256, mm.InstanceCount);
        while (cap < need) cap *= 2;
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
        if (len < 1e-3f) return;
        var x = d;
        var side = d.Cross(Vector3.Up);
        side = side.LengthSquared() < 1e-6f ? Vector3.Right * th : side.Normalized() * th;
        var up = side.Cross(d).Normalized() * th;
        Put(b, n, x, up, side, (p + q) * 0.5f, c);
    }

    public static Color KinColor(Agent a) => Color.FromHsv(a.Hue, a.Sat, a.Val);

    public static Color DietColor(Agent a)
    {
        if (a.EmaAttack > 0.004f) return new Color(1f, 0.2f, 0.15f);
        float p = a.EmaPhoto, c = a.EmaChem, m = a.EmaMine * 0.3f, sum = p + c + m;
        if (sum < 0.003f) return new Color(0.6f, 0.6f, 0.64f);
        return new Color(
            (p * 0.35f + c * 0.3f + m * 1.0f) / sum,
            (p * 0.95f + c * 0.62f + m * 0.62f) / sum,
            (p * 0.3f + c * 1.0f + m * 0.2f) / sum);
    }

    // What its proteins do: green = catching light, blue = splitting/joining molecules, red = motor.
    public static Color OrganColor(Agent a)
    {
        float g = a.EnzymeOf(Enzyme.Photo), b = a.EnzymeOf(Enzyme.Split) + a.EnzymeOf(Enzyme.Bind), r = a.EnzymeOf(Enzyme.Motor);
        if (r + g + b < 0.05f) return new Color(0.35f, 0.35f, 0.38f);
        float k = 1f / MathF.Max(r, MathF.Max(g, b));
        return new Color(0.25f + 0.75f * r * k, 0.25f + 0.75f * g * k, 0.25f + 0.75f * b * k);
    }

    public static bool HasOrgans(Agent a) => a.EnzymeTotal >= 0.5f;

    Color BodyColor(Agent a) => ColorMode switch
    {
        1 => DietColor(a),
        2 => Color.FromHsv(a.Lineage * 0.618034f % 1f, 0.7f, 0.95f),
        3 => new Color(0.95f, 0.25f, 0.2f).Lerp(new Color(0.35f, 0.95f, 0.45f), Math.Clamp(a.Energy / a.Store, 0, 1)),
        4 => OrganColor(a),
        _ => KinColor(a),
    };

    public Vector3 VisualPos(Agent a) => new(a.VX + 0.5f, a.VH, a.VY + 0.5f);

    void FillAgents(float dt)
    {
        var w = World;
        now = Time.GetTicksMsec() / 1000.0;
        Array.Clear(bodyN);
        foreach (var a in w.Agents) if (!a.Dead) bodyN[a.Shape]++;
        for (int k = 0; k < Looks.ShapeCount; k++) { Ensure(bodies[k], ref bodyBuf[k], bodyN[k]); bodyN[k] = 0; }
        Ensure(caps, ref capBuf, w.Agents.Count);
        Ensure(marks, ref markBuf, w.Agents.Count);
        int footNeed = 0;
        foreach (var a in w.Agents) if (!a.Dead && a.Cells > 1) footNeed += a.Cells;
        Ensure(foots, ref footBuf, footNeed);
        int nCap = 0, nMark = 0, nFoot = 0;
        var sel = Selected;
        bool focus = KinFocus && sel != null;
        bool fresh = now - worldShownAt < 0.5;
        float ks = 1 - MathF.Exp(-dt * 14);
        float t = (float)now;

        foreach (var a in w.Agents)
        {
            if (a.Dead) continue;
            if (a.SeenAt == 0) { a.SeenAt = fresh ? -10 : now; a.VX = a.X; a.VY = a.Y; }

            // Glide from cell to cell (the world wraps around in x).
            float dx = a.X - a.VX;
            if (dx > W / 2f) a.VX += W; else if (dx < -W / 2f) a.VX -= W;
            if (MathF.Abs(a.X - a.VX) > 2.5f || MathF.Abs(a.Y - a.VY) > 2.5f) { a.VX = a.X; a.VY = a.Y; }
            else { a.VX += (a.X - a.VX) * ks; a.VY += (a.Y - a.VY) * ks; }
            if (Slice >= 0 && a.Y > Slice) continue;

            if (a.ActTick != a.SeenAct)
            {
                a.SeenAct = a.ActTick;
                if (a.Act > World.ActMove) { a.AnimKind = a.Act; a.AnimDir = a.ActDir; a.AnimAt = now; }
            }

            float kin = focus && a != sel ? Looks.Kin(a, sel) : 1f;
            bool dim = kin <= 0;
            float s = Math.Clamp(0.42f + 0.07f * MathF.Sqrt(a.Mass), 0.42f, 1.0f) * (dim ? 0.75f : 1f);
            float sx = s * a.Sx, sy = s * a.Sy, sz = s * a.Sz, ox = 0, oy = 0, oz = 0, tintK = 0;
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
            sy *= 1 + 0.04f * MathF.Sin(t * 2.3f + a.Id * 0.37f);

            // Height follows the ground smoothly; climbing a step is a little hop.
            int cell = a.Y * W + a.X;
            float ground = AgentGround(a);
            if (a.Lift > 0) ground += (a.Lift + w.Ice[cell]) * BH;   // swimming above the bottom (the ice lies under the water here)
            if (a.Cells > 1)
            {
                // A big body: drawn larger, centred over all its cells, which get a tinted floor.
                float mx = 0, mz = 0;
                var kc = KinColor(a);
                for (int k = 0; k < a.Cells; k++)
                {
                    int fc = k == 0 ? cell : a.Foot[k];
                    int fx = fc % W, fy = fc / W, ddx = fx - a.X;
                    if (ddx > W / 2) ddx -= W; else if (ddx < -W / 2) ddx += W;
                    mx += ddx; mz += fy - a.Y;
                    float lf = Lighting ? MathF.Max(0.5f, 0.35f + 0.65f * w.Light[fc]) : 1f;
                    PutBox(footBuf, nFoot++, new Vector3(a.VX + ddx + 0.5f, a.Z * BH + 0.03f, a.VY + fy - a.Y + 0.5f),
                        new Vector3(0.94f, 0.05f, 0.94f), new Color(kc.R * 0.5f * lf, kc.G * 0.5f * lf, kc.B * 0.5f * lf));
                }
                ox += mx / a.Cells; oz += mz / a.Cells;
                float g = 0.6f + 0.55f * MathF.Sqrt(a.Cells);
                sx *= g; sy *= g; sz *= g;
            }
            else
            {
                // Several bodies in one cell: each stands at its own spot in it, smaller when crowded.
                int crowd = w.Count[cell];
                float spread = crowd > 1 ? 0.3f : 0f;
                ox += spread * (Hash32.F(a.Id, 1) * 2 - 1);
                oz += spread * (Hash32.F(a.Id, 2) * 2 - 1);
                if (crowd > 1) { float q = Math.Max(0.25f, 1.4f / MathF.Sqrt(crowd)); sx *= q; sy *= q; sz *= q; }
            }
            if (age < 0.05f || MathF.Abs(ground - a.VH) > 4) a.VH = ground;
            float rise = ground - a.VH;
            a.VH += rise * ks;
            float hop = rise > 0.05f && a.Lift == 0 ? 0.6f * MathF.Sqrt(rise) : 0;   // swimmers rise smoothly
            var pos = new Vector3(a.VX + 0.5f + ox, a.VH + hop + sy * 0.5f + oy + 0.02f, a.VY + 0.5f + oz);
            float lum = Lighting ? MathF.Max(0.72f, 0.45f + 0.55f * w.Light[cell]) : 1f;

            var col = BodyColor(a);
            if (tintK > 0) col = col.Lerp(tint, tintK);
            if (dim) col = col.Darkened(0.8f);
            else if (kin < 1) col = col.Darkened(0.45f * (1 - kin));   // distant relatives a bit darker
            col = new Color(col.R * lum, col.G * lum, col.B * lum);

            // Every individual is turned its own way (golden angle by id).
            float r = a.Id * 2.39996f, cr = MathF.Cos(r), sr = MathF.Sin(r);
            int sh = a.Shape;
            Put(bodyBuf[sh], bodyN[sh]++, new Vector3(cr * sx, 0, -sr * sx), new Vector3(0, sy, 0), new Vector3(sr * sz, 0, cr * sz), pos, col);
            if (dim) continue;

            var cc = DietColor(a);
            PutBox(capBuf, nCap++, pos + new Vector3(0, sy * 0.5f + 0.06f, 0), new Vector3(0.42f * s, 0.16f * s, 0.42f * s), new Color(cc.R * lum, cc.G * lum, cc.B * lum));
            if (HasOrgans(a))
            {
                var oc = OrganColor(a);
                PutBox(markBuf, nMark++, pos + new Vector3(cr * sx * 0.55f, 0, -sr * sx * 0.55f), new Vector3(0.24f, 0.24f, 0.24f) * s, oc);
            }
        }

        for (int k = 0; k < Looks.ShapeCount; k++)
        {
            bodies[k].Buffer = bodyBuf[k];
            bodies[k].VisibleInstanceCount = bodyN[k];
        }
        caps.Buffer = capBuf;
        caps.VisibleInstanceCount = nCap;
        marks.Buffer = markBuf;
        marks.VisibleInstanceCount = nMark;
        foots.Buffer = footBuf;
        foots.VisibleInstanceCount = nFoot;
    }

    // Colony links: a cyan bar between linked neighbours.
    void FillLinks()
    {
        var w = World;
        int need = 0;
        foreach (var a in w.Agents) need += a.Links.Count;
        Ensure(links, ref linkBuf, need);
        int n = 0;
        foreach (var a in w.Agents)
        {
            if (a.Dead) continue;
            foreach (var b in a.Links)
            {
                if (b.Dead || a.Id > b.Id || MathF.Abs(a.VX - b.VX) > W / 2f) continue;
                if (Slice >= 0 && (a.Y > Slice || b.Y > Slice)) continue;
                var pa = VisualPos(a) + new Vector3(0, 0.35f, 0);
                var pb = VisualPos(b) + new Vector3(0, 0.35f, 0);
                PutBeam(linkBuf, n++, pa, pb, 0.09f, Cyan);
            }
        }
        links.Buffer = linkBuf;
        links.VisibleInstanceCount = n;
    }

    // Short-lived effects: beams for attacks and gene injections, bursts for kills, puffs of
    // expelled molecules, dust for digging, fading ghosts for deaths.
    void FillFlashes()
    {
        var w = World;
        var ch = w.Chem;
        long life = Math.Max(24, Speed * 8);
        int n = 0;
        foreach (var f in w.Flashes)
        {
            if (n >= World.FlashCap - 2) break;
            long age = w.Tick - f.T;
            if (f.T == 0 || age > life || age < 0) continue;
            if (Slice >= 0 && f.Y > Slice) continue;
            float k = 1 - age / (float)life;
            float gy = Ground(f.Y * W + f.X);
            var at = new Vector3(f.X + 0.5f, gy + 0.4f, f.Y + 0.5f);
            var from = f.Dir is >= 0 and < 4 ? at - new Vector3(World.DX[f.Dir], 0, World.DY[f.Dir]) : at;
            switch (f.Kind)
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
                        var c = ch.Col[f.Spec];
                        float d = 0.5f + 1.3f * (1 - k);
                        var o = at + new Vector3(World.DX[f.Dir] * d, -0.15f, World.DY[f.Dir] * d);
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
        float g = Ground(c) + (w.Ice[c] + w.Water[c]) * BH;
        brushRing.Position = new Vector3(c % W + 0.5f, g + 0.15f, c / W + 0.5f);
        brushRing.Scale = new Vector3(BrushR, 1, BrushR);
        ((StandardMaterial3D)brushRing.MaterialOverride).AlbedoColor = BrushColors[Math.Clamp(BrushTool, 0, BrushColors.Length - 1)];
    }

    void Place(MeshInstance3D ring, Agent a, float scale)
    {
        bool on = a is { Dead: false } && (Slice < 0 || a.Y <= Slice);
        ring.Visible = on;
        if (!on) return;
        ring.Position = VisualPos(a) + new Vector3(0, 0.06f, 0);
        ring.Scale = Vector3.One * scale * (1 + 0.06f * MathF.Sin((float)now * 5));
    }
}
