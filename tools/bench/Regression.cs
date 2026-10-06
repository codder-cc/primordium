using System;
using System.Linq;

namespace Primordium;

// Compiled only into the headless runner: exercises real simulation paths, no Godot dependency.
public sealed partial class World
{
    static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }

    // Every living body is in exactly its own cell's list, lists end, and counts match.
    public void CheckCellLists()
    {
        int listed = 0;
        for (int c = 0; c < N; c++)
        {
            int n = 0;
            for (var a = Head[c]; a != null; a = a.NextInCell)
            {
                Require(++n <= Agents.Count, $"cell {c}: list loops");
                Require(a.Y * W + a.X == c && !a.Dead, $"cell {c}: body #{a.Id} listed in the wrong cell or dead");
            }
            Require(n == Count[c], $"cell {c}: count {Count[c]} but {n} listed");
            listed += n;
        }
        Require(listed == Agents.Count(a => !a.Dead), "a living body is missing from the cell lists");
    }
    // A fingerprint of the whole state that matters for the trajectory: terrain, loose matter, bodies.
    // Two runs that print the same hash at a checkpoint followed the same trajectory (tools/bench).
    public ulong StateHash()
    {
        ulong h = 1469598103934665603UL;
        void Mix(ulong x) { h ^= x; h *= 1099511628211UL; h ^= h >> 29; }
        for (int i = 0; i < N; i++) Mix((ulong)Height[i]);
        for (int v = 0; v < N * Z; v++)
            if (Mat[v] != 0) Mix((ulong)v << 32 ^ (ulong)Mat[v] << 24 ^ (ulong)Units[v] << 8 ^ Order[v]);
        foreach (var c in C) for (int i = 0; i < N; i++) Mix((ulong)c[i].Raw);
        foreach (var a in Agents)
            Mix((ulong)a.Id ^ (ulong)a.X << 40 ^ (ulong)a.Y << 50 ^ (ulong)a.Z << 20 ^ (uint)BitConverter.SingleToInt32Bits(a.Energy) ^ a.Hash);
        return h;
    }
    static void BudgetEqual(double[] a, double[] b, string stage, double tolerance = 0.002)
    {
        for (int e = 0; e < a.Length; e++) Require(Math.Abs(a[e] - b[e]) <= tolerance, $"{stage}: element {e}: {a[e]:R} -> {b[e]:R} (delta {b[e] - a[e]:R})");
    }
    static World Fixture()
    {
        var w = new World(1, 0, false) { AutoStrikes = false };
        w.Vents.Clear();
        Array.Clear(w.Mat); Array.Clear(w.Units); Array.Clear(w.Order);
        foreach (var c in w.C) Array.Clear(c);
        Array.Clear(w.Water); Array.Clear(w.Ice); Array.Clear(w.Snow);
        for (int c = 0; c < N; c++)
        {
            w.Height[c] = 2;
            w.Mat[c * Z] = w.Mat[c * Z + 1] = Chemistry.Bedrock;
            w.Order[c * Z] = w.Order[c * Z + 1] = 255;
        }
        w.StepStructure();
        return w;
    }
    Agent TestAgent(int c, int level, int s, int count)
    {
        long id = NewId();
        var a = new Agent(id, id, 0, new byte[] { Genome.Yield, 0, 0, 0, 0, 0, 0, 0 }) { Energy = 200, Z = level, Tb = 15 };
        for (int k = 0; k < count; k++) AddMol(a, s);
        Place(a, c); Agents.Add(a);
        return a;
    }
    void TestBlock(int c, int z, int s, byte order = 255, int count = -1)
    {
        var counts = new ushort[Chemistry.S];
        counts[s] = (ushort)(count < 0 ? Chem.MatCap[s + 2] : count);   // a full block by default
        PutMixture(c * Z + z, counts, order);
    }

    public static void RunRegression()
    {
        ParamRegistry.ResetDefaults();   // P is shared by every world in the process
        LawsRegression();
        LawsEnergyRegression();
        SaveLoadRegression();
        LifeSeedRegression();
        AsmRegression();
        DesignRegression();
        for (int seed = -3; seed <= 100; seed++)
        {
            var ch = new Chemistry(seed);
            for (int a = 0; a < Chemistry.S; a++)
            {
                float mass = 0;
                for (int e = 0; e < Chemistry.ElementCount; e++) mass += ch.Atoms[a, e] * ch.AtomicMass[e];
                Require(Math.Abs(mass - ch.Mass[a]) < 0.0001, "derived molecular mass");
                if (ch.PhotoUp[a] >= 0) Require(ch.SameFormula(a, ch.PhotoUp[a]), "photons transmuted elements");
                for (int e = 0; e < Chemistry.ElementCount; e++)
                {
                    if (ch.SplitA[a] >= 0) Require(ch.Atoms[a, e] == ch.Atoms[ch.SplitA[a], e] + (ch.SplitB[a] < 0 ? 0 : ch.Atoms[ch.SplitB[a], e]), "unbalanced split");
                    for (int b = 0; b < Chemistry.S; b++)
                        if (ch.Combine[a, b] >= 0) Require(ch.Atoms[a, e] + ch.Atoms[b, e] == ch.Atoms[ch.Combine[a, b], e], "unbalanced bind");
                }
            }
        }
        Console.WriteLine("PASS chemistry: 104 seeds, all reactions, masses and photo states");
        // The bit-sliced fingerprint must equal the plain per-bit vote it replaced.
        var rng = new Random(5);
        for (int k = 0; k < 2000; k++)
        {
            var g = new byte[rng.Next(Genome.MinLen, Genome.MaxLen + 1)];
            rng.NextBytes(g);
            Require(Genome.SimHash(g) == ReferenceSimHash(g), "kin fingerprint changed");
        }
        Console.WriteLine("PASS kin fingerprint: identical to the reference vote on 2000 genomes");
        // The packed label positions must answer exactly like the old per-byte table.
        for (int k = 0; k < 3000; k++)
        {
            var g = new byte[rng.Next(Genome.MinLen, k < 2000 ? 300 : Genome.MaxLen + 1)];
            rng.NextBytes(g);
            if (k % 3 == 0) for (int i = 0; i < g.Length; i += 1 + rng.Next(12)) g[i] = (byte)(Genome.Label | rng.Next(4) << 6);
            var table = Genome.LabelTable(g);
            var packed = Genome.Labels(g);
            for (int i = 0; i < g.Length; i++)
                for (int l = 0; l < 4; l++) Require(Genome.NextLabel(packed, i, l) == table[i * 4 + l], "label lookup changed");
        }
        Console.WriteLine("PASS labels: packed positions answer like the per-byte table on 3000 genomes");
        for (int k = 0; k < 20000; k++)
        {
            var g = new byte[rng.Next(Genome.MinLen, k < 19000 ? 400 : Genome.MaxLen + 1)];
            var p = new byte[g.Length];
            rng.NextBytes(g); rng.NextBytes(p);
            int seed = rng.Next();
            var (g1, p1) = Genome.Mutate(g, p, new SimRng(seed));
            var (g2, p2) = Genome.MutateReference(g, p, new SimRng(seed));
            Require(g1.AsSpan().SequenceEqual(g2) && p1.AsSpan().SequenceEqual(p2), "mutation changed");
        }
        Console.WriteLine("PASS mutation: buffer version equals the list version on 20000 genomes");
        MatterRegression();
        StructureRegression();
        CaveRegression();
        VolumeRegression();
        FaceRegression();
        WaterRegression();
        HandRegression();
        FatalActionRegression();
        RegionalStructureRegression();
        StackRegression();
        BiteBankRegression();
        ReachRegression();
        DepositRegression();
        EnergyRegression();
        foreach (int seed in new[] { 1, 7 })
        {
            var w = new World(seed, 800, true) { TrackHeat = true };
            var before = w.ElementBudget();
            var energy = w.AuditEnergy();
            for (int t = 0; t < 1200; t++) w.Step();
            var after = w.ElementBudget();
            for (int e = 0; e < after.Length; e++) after[e] -= w.InteriorInput[e] + w.HandInput[e];
            BudgetEqual(before, after, $"world seed {seed}", 0.25);
            foreach (var a in w.Agents)
            {
                double mass = 0;
                for (int s = 0; s < Chemistry.S; s++) mass += (a.Inv[s] + a.Pend[s]) * w.Chem.Mass[s];
                for (int k = 0; k < a.EnzN; k++) mass += a.Enz[k].Matter * w.Chem.Mass[a.Enz[k].Material];
                Require(Math.Abs(a.Mass - mass) < 0.005, $"body mass drift #{a.Id}: {a.Mass} vs {mass}");
                double volume = 0;
                for (int s = 0; s < Chemistry.S; s++) volume += (a.Inv[s] + a.Pend[s]) * w.Chem.BodyVolume[s];
                for (int k = 0; k < a.EnzN; k++) volume += a.Enz[k].Matter * w.Chem.Volume[a.Enz[k].Material];
                Require(Math.Abs(a.Volume - volume) < 0.005, $"body volume drift #{a.Id}: {a.Volume} vs {volume}");
                Require(a.Inv.All(n => n >= 0), "negative body inventory");
            }
            Require(w.C.All(c => c.All(x => x >= 0)), "invalid environmental amount");
            w.CheckCellLists();
            string energyNote = EnergyWorldCheck(w, energy, $"world seed {seed}");
            Console.WriteLine($"PASS 1200 ticks seed {seed}: population {w.Agents.Count}, max atom drift {before.Zip(after, (a, b) => Math.Abs(a - b)).Max():F6}, {energyNote}, falls {w.CollapsedBlocks}, buried {w.DeathsBuried}");
        }
        // Repeat independently; thread scheduling must not affect world or population state.
        var one = new World(42, 80, false); var two = new World(42, 80, false);
        for (int t = 0; t < 100; t++) { one.Step(); two.Step(); }
        BudgetEqual(one.ElementBudget(), two.ElementBudget(), "repeat seed", 0);
        Require(one.Mat.SequenceEqual(two.Mat) && one.Units.SequenceEqual(two.Units), "terrain nondeterminism");
        Require(one.Agents.Select(a => (a.Id, a.X, a.Y, a.Z, a.Energy, a.Mass, a.Hash)).SequenceEqual(two.Agents.Select(a => (a.Id, a.X, a.Y, a.Z, a.Energy, a.Mass, a.Hash))), "agent nondeterminism");
        // The same with a full population: parallel stripes must not make outcomes depend on timing.
        var big1 = new World(1); var big2 = new World(1);   // a seed that is crowded at 400 ticks whatever the trajectory (~1500 bodies)
        for (int t = 0; t < 400; t++) { big1.Step(); big2.Step(); }
        Require(big1.Agents.Count > 500, "crowded determinism fixture died out");
        Require(big1.Mat.SequenceEqual(big2.Mat) && big1.Units.SequenceEqual(big2.Units), "terrain nondeterminism (crowded)");
        Require(big1.Agents.Select(a => (a.Id, a.X, a.Y, a.Z, a.Energy, a.Mass, a.Hash)).SequenceEqual(big2.Agents.Select(a => (a.Id, a.X, a.Y, a.Z, a.Energy, a.Mass, a.Hash))), "agent nondeterminism (crowded)");
        Console.WriteLine($"PASS determinism: independent worlds with identical seed (also {big1.Agents.Count} agents in parallel tiles)");
    }

    // How many blocks' weight a sideways bond of fresh crust can carry (percentiles), for calibration.
    public static void StrengthReport(int seed)
    {
        var w = new World(seed, 0, false);
        w.StepStructure();
        var same = new System.Collections.Generic.List<float>();
        var mixed = new System.Collections.Generic.List<float>();
        var crush = new System.Collections.Generic.List<float>();
        for (int c = 0; c < N; c += 7)
            for (int z = 2; z < w.Height[c] - 1; z++)
            {
                int v = c * Z + z, n = w.nb[c * 4] * Z + z;
                if (w.Mat[v] < 2 || w.Mat[n] < 2) continue;
                float load = w.VoxelMass(v) * P.Gravity;
                (w.Mat[v] == w.Mat[n] ? same : mixed).Add(w.BondCapacity(v, n) / load);
                crush.Add(w.CompressionCapacity(v) / Math.Max(1e-3f, w.Pressure[v]));
            }
        string Pct(System.Collections.Generic.List<float> l) { l.Sort(); return l.Count == 0 ? "-" : $"p10 {l[l.Count / 10]:F2} p50 {l[l.Count / 2]:F2} p90 {l[l.Count * 9 / 10]:F2} (n={l.Count})"; }
        Console.WriteLine($"side bond / own weight, same aggregate: {Pct(same)}");
        Console.WriteLine($"side bond / own weight, different:     {Pct(mixed)}");
        Console.WriteLine($"compression strength / pressure:       {Pct(crush)}");
    }

    // Chance to tear a molecule out of the top blocks unaided, and the energy a full column of soft
    // ground holds, for calibrating P.RockBarrier.
    public static void BiteReport(int seed)
    {
        var w = new World(seed, 0, false);
        var p = new System.Collections.Generic.List<float>();
        for (int c = 0; c < N; c += 3)
        {
            int v = c * Z + w.Height[c] - 1;
            if (w.Mat[v] >= 2) p.Add(MathF.Exp(-w.VoxelBarrier(v)));
        }
        p.Sort();
        Console.WriteLine($"seed {seed}: unaided bite on top blocks p10 {p[p.Count / 10]:P1} p50 {p[p.Count / 2]:P1} p90 {p[p.Count * 9 / 10]:P1}; molecules per top block p50 {w.Units[(N / 2) * Z + w.Height[N / 2] - 1]}");
        // Barriers of the surface, 3 blocks down and deep, and of what bodies lay down (sediment, excretion, building).
        string Pct(System.Collections.Generic.List<float> l) { l.Sort(); return l.Count == 0 ? "-" : $"p10 {l[l.Count / 10]:F2} p50 {l[l.Count / 2]:F2} p90 {l[l.Count * 9 / 10]:F2}"; }
        var top = new System.Collections.Generic.List<float>(); var mid = new System.Collections.Generic.List<float>(); var deep = new System.Collections.Generic.List<float>();
        for (int c = 0; c < N; c += 3)
        {
            int h = w.Height[c];
            if (h < 6) continue;
            top.Add(w.VoxelBarrier(c * Z + h - 1)); mid.Add(w.VoxelBarrier(c * Z + h - 4)); deep.Add(w.VoxelBarrier(c * Z + 3));
        }
        var laid = new System.Collections.Generic.List<string>();
        foreach (byte order in new byte[] { 12, 25, 80 })
        {
            var l = new System.Collections.Generic.List<float>();
            for (int m = 2; m < w.Chem.MatCount; m++) l.Add(w.TypicalBarrier(m, order / 255f));
            laid.Add($"order {order}: {Pct(l)}");
        }
        Console.WriteLine($"   barrier top {Pct(top)} | 3 down {Pct(mid)} | deep {Pct(deep)} | pure aggregates laid at " + string.Join("; ", laid));
        var cost = top.Select(b => P.FaceWork * MathF.Exp(b - P.FaceBarrier)).ToList();
        Console.WriteLine($"   energy to work one molecule out of a top block, unaided: {Pct(cost)}");
    }

    static ulong ReferenceSimHash(byte[] g)
    {
        var acc = new int[64];
        int n = g.Length;
        for (int i = 0; i < n; i++)
        {
            ulong h = (ulong)(g[i] | g[(i + 1) % n] << 8 | g[(i + 2) % n] << 16);
            h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53UL; h ^= h >> 33;
            for (int b = 0; b < 64; b++) acc[b] += ((h >> b) & 1) != 0 ? 1 : -1;
        }
        ulong r = 0;
        for (int b = 0; b < 64; b++) if (acc[b] > 0) r |= 1UL << b;
        return r;
    }

    static void MatterRegression()
    {
        var w = Fixture(); int c = 80 * W + 120;
        var a = w.TestAgent(c, 2, 1, 30);
        var before = w.ElementBudget();
        w.Express(a, 0, new byte[] { 0, 1, 2, 3 }, 4);
        for (int k = 0; k < 100; k++) w.WearProtein(a, 0, 0.91f);
        BudgetEqual(before, w.ElementBudget(), "protein turnover");
        w.Die(a, c, CauseStarve);
        BudgetEqual(before, w.ElementBudget(), "death with protein and energy");
        for (int s = 0; s < Chemistry.S; s++) w.C[s][c] += 4.25f;
        before = w.ElementBudget();
        w.Settle(c, 1e4f); w.StrikeAt(c % W, c / W, 3);
        BudgetEqual(before, w.ElementBudget(), "sediment and irradiation");
        a = w.TestAgent(c, w.Height[c], 1, 40);
        before = w.ElementBudget();
        w.Grow(a, c, 4);
        BudgetEqual(before, w.ElementBudget(), "mixed construction");
        int solid = w.Chem.Solids[0];
        for (int n = 0; n < 8; n++) w.AddMol(a, solid);
        before = w.ElementBudget();
        w.Pile(a, c, 4);
        BudgetEqual(before, w.ElementBudget(), "solid construction");
        // Fractional uptake cannot round 0.999 of a molecule up into a whole molecule.
        w.C[0][c] = 0.9995f; a.Pend[0] = 0;
        before = w.ElementBudget();
        w.Intake(a, c, 0);
        BudgetEqual(before, w.ElementBudget(), "fractional intake");
        w.TestBlock(c, 12, 1);
        var b = w.BurialAt(c * Z + 1); b.Matter[0] = 10; b.Matter[1] = 8;
        w.MatterChanged(c * Z + 1); w.StepStructure();
        before = w.ElementBudget();
        w.Metamorphose();
        Require(b.Order > 0 && b.Pressure > 0, "burial did not respond to pressure");
        BudgetEqual(before, w.ElementBudget(), "pressure chemistry");
        Console.WriteLine("PASS matter: proteins, death, sediment, radiation, building, uptake, pressure");
        ExactPoolsRegression();
    }

    // Fractional pools are fixed point (Qty): small amounts moved into, out of and between big pools
    // must not round atoms into or out of existence. In float each of these lost or made ~10⁻³ of a
    // molecule per step next to a pile of 10⁴–10⁵ (the long test's atom drift in boomed worlds).
    static void ExactPoolsRegression()
    {
        var w = Fixture(); var ch = w.Chem;
        int c = 60 * W + 40;
        int s = Enumerable.Range(0, Chemistry.S).First(k => ch.SplitExo[k] && ch.SplitB[k] >= 0);
        int pa = ch.SplitA[s], pb = ch.SplitB[s];
        // Loose decay of a small pile into two big ones of its products.
        w.C[s][c] = 50.37f; w.C[pa][c] += 60000.3f; w.C[pb][c] += 30000.7f;
        var before = w.ElementBudget();
        for (int t = 0; t < 3000; t++) { w.Temp[c] = -20 + t % 60; w.CellChem(c, t); }
        Require(w.C[s][c] < 20, "the loose pile did not decay");
        BudgetEqual(before, w.ElementBudget(), "loose decay beside big piles", 0);
        // Uptake of fractions from a big pile (and back out through worn proteins).
        int food = Enumerable.Range(0, Chemistry.S).First(k => k != ch.Gas && k != s && k != pa && k != pb);
        w.C[food][c] = 41234.567f;
        var a = w.TestAgent(c, w.Height[c], food, 30);
        w.Express(a, 0, new byte[] { 0, 1, 2, 3 }, 4);
        before = w.ElementBudget();
        for (int k = 0; k < 2000; k++)
        {
            w.C[food][c] += 0.0123f;   // a little more to take each time: the intake is fractional
            w.Intake(a, c, food);
            if (k % 7 == 0) w.WearProtein(a, 0, 0.93f);
        }
        var after = w.ElementBudget();
        for (int e = 0; e < Chemistry.ElementCount; e++) after[e] -= 2000 * Qty.Of(0.0123f).D * ch.Atoms[food, e];
        BudgetEqual(before, after, "fractional uptake from a big pile", 1e-9);
        // Gas spreading over uneven ground: what one cell gives, the other receives.
        w = Fixture();
        var rng = new SimRng(5);
        for (int i = 0; i < N; i++) { w.Height[i] = 2 + rng.Next(3); w.C[ch.Gas][i] = (float)(rng.NextDouble() * 2000); }
        w.RecomputeFlow();
        before = w.ElementBudget();
        for (int t = 0; t < 200; t++) w.Diffuse();
        BudgetEqual(before, w.ElementBudget(), "gas diffusion", 0);
        Console.WriteLine("PASS exact pools: loose decay beside big piles, fractional uptake, gas diffusion: atoms exact");
    }

    static void StructureRegression()
    {
        var w = Fixture(); int c = 80 * W + 120, anchor = c - 1;
        int s = Enumerable.Range(0, Chemistry.S).Where(s => s % 2 == 0).OrderByDescending(s => w.Chem.Bond[s] / w.Chem.Mass[s]).First();
        for (int z = 2; z <= 5; z++) w.TestBlock(anchor, z, s);
        w.TestBlock(c, 5, s);
        float mono = w.BondCapacity(c * Z + 5, anchor * Z + 5);
        w.TestBlock(anchor, 5, s + 1);
        float mixed = w.BondCapacity(c * Z + 5, anchor * Z + 5);
        Require(mixed < mono * 0.4f, "heterogeneous boundary too strong");
        w.TestBlock(anchor, 5, s);
        int n = Math.Max(0, (int)((mixed * 1.1f - w.VoxelMass(c * Z + 5) * P.Gravity) / (w.Chem.Mass[s] * P.Gravity)) + 1);
        var load = w.TestAgent(c, 6, s, n);
        var victim = w.TestAgent(c, 2, 0, 8);
        float weight = w.OwnLoad(c * Z + 5) + load.Mass * P.Gravity;
        Require(weight < mono, "fixture load exceeds monolith");
        var before = w.ElementBudget();
        w.StepStructure();
        Require(w.Mat[c * Z + 5] != 0 && !victim.Dead, "monolithic roof did not hold");
        BudgetEqual(before, w.ElementBudget(), "stable roof");
        w.TestBlock(anchor, 5, s + 1);
        before = w.ElementBudget();
        w.StepStructure();
        Require(w.Mat[c * Z + 5] == 0 && w.Mat[c * Z + 2] != 0, "weak interface did not collapse");
        Require(victim.Dead && victim.Cause == CauseBuried && w.Buried.ContainsKey(c * Z + 2), "victim was not buried at impact depth");
        BudgetEqual(before, w.ElementBudget(), "collapse and crushed body");
        w.StepStructure(); w.Metamorphose(); Require(w.Buried[c * Z + 2].Order > 0, "buried organics did not compact");
        // A floating connected slab has no roots and must fall, including across the longitude seam.
        int edge = 20 * W;
        w.TestBlock(edge, 6, s); w.TestBlock(edge + W - 1, 6, s);
        w.StepStructure();
        Require(w.Mat[edge * Z + 6] == 0 && w.Mat[(edge + W - 1) * Z + 6] == 0, "floating ring supported itself");
        // Removing just part of a voxel weakens its cross-section quadratically.
        int v = anchor * Z + 3;
        float full = w.CompressionCapacity(v);
        for (int k = w.Units[v] / 2; k > 0; k--) w.TakeVoxelMolecule(v);   // eat half of it
        Require(w.CompressionCapacity(v) <= full * 0.3f, "partial mining did not weaken support");
        Console.WriteLine("PASS structure: monolith, weak boundary, body load, burial, floating slab, seam, partial mining");
    }

    static void RegionalStructureRegression()
    {
        var local = Fixture(); var full = Fixture();
        int c = 70 * W + 100;
        int s = Enumerable.Range(0, Chemistry.S).OrderByDescending(s => local.Chem.Bond[s] / local.Chem.Mass[s]).First();
        foreach (var w in new[] { local, full })
        {
            foreach (int start in new[] { c, c + 35 })
            {
                for (int z = 2; z <= 6; z++) { w.TestBlock(start, z, s); w.TestBlock(start + 4, z, s); }
                for (int x = 1; x < 4; x++) w.TestBlock(start + x, 6, s);
                // Another roof shares the first pier on its north side.
                w.TestBlock(start - W, 6, s);
            }
            w.StepStructure();
        }
        var rng = new Random(72);
        for (int tick = 0; tick < 12; tick++)
        {
            int cell = c + rng.Next(5), z = 2 + rng.Next(5);
            foreach (var w in new[] { local, full })
            {
                if (w.Units[cell * Z + z] > 0) w.TakeVoxelMolecule(cell * Z + z);
                w.structuralDirty.Add(cell);
            }
            for (int i = 0; i < N; i++) full.structuralDirty.Add(i);
            local.StepStructure(); full.StepStructure();
            Require(local.Mat.SequenceEqual(full.Mat) && local.Units.SequenceEqual(full.Units), "regional geometry diverged from full solve");
            for (int i = 0; i < N * Z; i++)
                if (local.Mat[i] != 0) Require(Math.Abs(local.Pressure[i] - full.Pressure[i]) < 0.001f, $"regional load diverged at {i}");
        }
        Require(local.LastStructureColumns < N / 10, "local update expanded to the whole world");
        Console.WriteLine($"PASS regional support: matches full solves, shared anchors, {local.LastStructureColumns} active columns / {N}");
    }

    // A block resting on a hanging block that cannot bear both: the lower one's path fails and both
    // fall. (The support solver used to leave the upper block without a path and its weight nowhere:
    // the stack stood for ever.)
    static void StackRegression()
    {
        var w = Fixture(); int c = 90 * W + 140, anchor = c - 1;
        int s = Enumerable.Range(0, Chemistry.S).Where(s => s % 2 == 0).OrderByDescending(s => w.Chem.Bond[s] / w.Chem.Mass[s]).First();
        for (int z = 2; z <= 5; z++) w.TestBlock(anchor, z, s + 1);   // a pier of another kind: a weak joint
        w.TestBlock(c, 5, s);                                        // hangs from the pier over a cavity
        w.TestBlock(c, 6, s);                                        // rests only on the hanging block
        float joint = w.BondCapacity(c * Z + 5, anchor * Z + 5), own = w.OwnLoad(c * Z + 5), upper = w.OwnLoad(c * Z + 6);
        Require(own < 0.8f * joint, $"stack fixture: the lower block alone does not hold ({own} vs {joint})");
        float extra = Math.Max(0, 1.2f * joint - own - upper);
        int n = (int)(extra / (w.Chem.Mass[s] * P.Gravity)) + 1;
        var load = w.TestAgent(c, 7, s, n);
        var before = w.ElementBudget();
        w.StepStructure(); w.StepStructure();   // the lower block breaks away; then the upper one has nothing under it
        Require(w.Mat[c * Z + 5] == 0 && w.Mat[c * Z + 6] == 0 && w.Mat[c * Z + 2] != 0 && w.Mat[c * Z + 3] != 0,
            $"an overloaded hanging stack stood: z5 {w.Mat[c * Z + 5]} z6 {w.Mat[c * Z + 6]}");
        BudgetEqual(before, w.ElementBudget(), "stack collapse", 0.01);
        // Without the load the same stack holds, and its weight reaches the pier.
        var w2 = Fixture();
        for (int z = 2; z <= 5; z++) w2.TestBlock(anchor, z, s + 1);
        w2.TestBlock(c, 5, s); w2.TestBlock(c, 6, s, 255, w2.Chem.MatCap[s + 2] / 10);
        w2.StepStructure();
        Require(w2.OwnLoad(c * Z + 5) + w2.OwnLoad(c * Z + 6) < 0.9f * joint, "light stack fixture too heavy");
        Require((w2.Mat[c * Z + 5] != 0 && w2.Mat[c * Z + 6] != 0 && w2.Pressure[c * Z + 5] >= w2.OwnLoad(c * Z + 5) + w2.OwnLoad(c * Z + 6) - 1e-3f),
            "a light stack fell or its upper block's weight went nowhere");
        Console.WriteLine($"PASS hanging stack: overloaded ({own + upper + load.Mass * P.Gravity:F2} on a joint of {joint:F2}) falls, a light one holds with its weight carried");
    }

    // Weathering and work are kept in molecules of one face: a poke at a hard face does not bank a
    // fortune for soft calls later, and work at the surface does not pay for a gnawer deeper down.
    static void BiteBankRegression()
    {
        var w = Fixture(); w.Tick = 100000;
        int c = 100 * W + 10, s = 0;
        w.TestBlock(c, 2, s); w.TestBlock(c, 3, s);
        int v = c * Z + 3, deep = c * Z + 2;
        w.TakeBite(v, 20f, 0);
        int free = 0; while (w.TakeBite(v, 0.4f, 0) && free < 100000) free++;
        Require(free <= P.BiteCap, $"a hard poke banked weathering: {free} soft molecules for nothing");
        w.Tick += 100000;
        free = 0; while (w.TakeBite(v, 0.4f, 0) && free < 100000) free++;
        Require(free <= P.BiteCap, $"weathering beyond its cap: {free}");
        float need = MathF.Exp(6f - P.FaceBarrier) * P.FaceWork;
        Require(!w.TakeBite(v, 6f, 0.9f * need), "0.9 of a molecule's work freed one");
        Require(!w.TakeBite(deep, 6f, 0.2f * need), "work at one face paid for another one in the same column");
        Require(w.TakeBite(deep, 6f, 0.85f * need), "work at a face does not add up");
        w.Tick = 0;
        Console.WriteLine("PASS bite bank: weathering capped in molecules, work kept per face");
    }

    // Gnawing and digging reach a neighbour's rock level with the body or one step down, not the
    // bottom of a pit; expelled matter lands on a floor it can reach, never in a wall or cave air.
    static void ReachRegression()
    {
        var w = Fixture(); int s = 0;
        int c = 60 * W + 60, pit = c + 1, step = c + W;
        for (int z = 2; z < 20; z++) { w.TestBlock(c, z, s, 0); if (z < 19) w.TestBlock(step, z, s, 0); }
        w.TestBlock(pit, 2, s, 0);
        var a = w.TestAgent(c, 20, s, 20);
        a.Energy = 1e6f; w.Tick = 50000;
        int bottom = w.Units[pit * Z + 2], ledge = w.Units[step * Z + 18];
        for (int k = 0; k < 2000; k++) { w.Mine(a, c, -1, 0); w.Mine(a, c, -1, 1); }
        Require(w.Units[pit * Z + 2] == bottom, $"gnawed the bottom of a pit 17 levels down: {bottom} -> {w.Units[pit * Z + 2]}");
        Require(w.Units[step * Z + 18] < ledge || w.Mat[step * Z + 18] == 0, "could not gnaw a ledge one step down");
        a.SetGenome(new byte[] { Genome.Dig, Genome.Yield, 0, 0, 0, 0, 0, 0 }, new byte[8]);
        w.AddMol(a, w.Chem.Solids[0]);
        w.Dig(a, c, 0);
        Require(w.Mat[pit * Z + 2] != 0 && a.NDigs == 0, "dug out the bottom of a pit");
        w.Tick = 0;

        // Expel towards a neighbour whose wall stands where the body is (a cave below its roof).
        var e = Fixture();
        int h = 80 * W + 40, n = h + 1, low = h + W;
        for (int z = 2; z < 5; z++) e.TestBlock(h, z, s);
        e.TestBlock(n, 2, s);
        for (int z = 5; z < 9; z++) e.TestBlock(n, z, s);   // cave at 3–4 under a roof at 5–8
        e.TestBlock(low, 2, s);                              // an open floor two levels down
        var x = e.TestAgent(h, 5, s, 20);
        e.StepStructure();
        var before = e.ElementBudget();
        for (int k = 0; k < 5; k++) e.Expel(x, h, s, 0);
        for (int k = 0; k < 3; k++) e.Expel(x, h, s, 1);
        foreach (var kv in e.Buried)
            Require(e.Mat[kv.Key] != 0 && (kv.Key % Z + 1 >= Z || e.Mat[kv.Key + 1] == 0), $"expelled matter inside rock or in cave air at z {kv.Key % Z}");
        Require(Math.Abs(e.C[s][h] - 5) < 1e-4f && Math.Abs(e.C[s][low] - 3) < 1e-4f, $"expelled matter not on the reachable floors: here {e.C[s][h]}, below {e.C[s][low]}");
        BudgetEqual(before, e.ElementBudget(), "expel", 0.01);
        Console.WriteLine("PASS reach: no gnawing or digging down a pit, expel lands on a reachable floor");
    }

    // Deposits: a cave deposit leaves the column's top where it is; more than a block of matter makes
    // one full block and the rest lies loose on it; ids never wrap around.
    static void DepositRegression()
    {
        var w = Fixture(); int s = 0, c = 40 * W + 100;
        for (int z = 2; z < 10; z++) if (z != 4 && z != 5) w.TestBlock(c, z, s, z == 9 ? (byte)0 : (byte)255);
        for (int d = 0; d < 4; d++) for (int z = 2; z < 8; z++) w.TestBlock(w.Nb(c, d), z, s);
        w.StepStructure();
        int top = w.Height[c];
        var add = new ushort[Chemistry.S]; add[s] = 4;
        w.Deposit(c, 4, add, 25);
        Require(w.Height[c] == top && w.Mat[c * Z + 9] != 0, "a cave deposit made the column's top slide");
        int open = 30 * W + 30, cap = w.Chem.MatCap[s + 2];
        var heap = new ushort[Chemistry.S]; heap[s] = (ushort)Math.Min(ushort.MaxValue, 3 * cap);
        int total = heap[s];
        w.Deposit(open, 2, heap, 12);
        Require(w.Units[open * Z + 2] <= cap && w.VoxelVolume(open * Z + 2) <= P.VoxelSpace + 1e-3f, $"over-full deposit: {w.Units[open * Z + 2]} of {cap}");
        Require(Math.Abs(w.Units[open * Z + 2] + w.C[s][open] - total) < 1e-3f, "deposit lost matter");
        w.nextId = int.MaxValue;
        Require(w.NewId() > int.MaxValue, "ids wrapped around");
        Console.WriteLine($"PASS deposits: cave deposit keeps the top, {total} molecules make one block of {w.Units[open * Z + 2]} and a loose rest, 64-bit ids");
    }

    static void FatalActionRegression()
    {
        var w = Fixture(); int c = 50 * W + 80, s = w.Chem.Solids[0];
        // The actor stands on a pedestal and digs the wall block level with it. Every floor next to
        // that block is three levels lower: a body can't set it down that far, so nothing is hurled.
        for (int z = 2; z <= 4; z++) w.TestBlock(c, z, s);
        for (int z = 2; z <= 5; z++) w.TestBlock(c + 1, z, s);
        var a = w.TestAgent(c, 5, s, 20);
        a.SetGenome(new byte[] { Genome.Dig, Genome.Intake, Genome.Yield, 0, 0, 0, 0, 0 }, new byte[8]);
        var before = w.ElementBudget();
        w.Exec(a, c);
        Require(!a.Dead && w.Mat[(c + 1) * Z + 5] != 0 && a.NDigs == 0, "dig hurled a block down a ledge");
        // With a floor one level lower next door the block is set down there, gently.
        w.TestBlock(c + 1 + W, 2, s); w.TestBlock(c + 1 + W, 3, s); w.TestBlock(c + 1 + W, 4, s);
        var bystander = w.TestAgent(c + 1 + W, 5, s, 8);
        before = w.ElementBudget();
        a.Ip = 0; a.Sp = 0;
        w.Exec(a, c);
        // The bystander is pushed aside onto a floor no higher than its own, or rides up onto the
        // block if there is nowhere to go — never crushed.
        bool aside = bystander.Y * W + bystander.X != c + 1 + W && bystander.Z <= 5;
        Require(a.NDigs == 1 && !bystander.Dead && (bystander.Z == 6 || aside), "a block set down one level must push or lift, not crush");
        BudgetEqual(before, w.ElementBudget(), "earthmoving keeps elements", 0.01);
        // A body that died cannot run its program any further.
        w.Die(a, c, CauseStarve);
        int intake = a.NIntake;
        w.Exec(a, c);
        Require(a.LastCycles == 0 && a.NIntake == intake, "a dead body executed instructions");
        Console.WriteLine("PASS earthmoving: no hurling, gentle set-down pushes aside, dead bodies run no code");
    }

    // Volume: a floor holds bodies by the room they take, not by count. Big bodies keep their place,
    // the smallest are pushed sideways or down; plenty of tiny bodies fit where few big ones do.
    static void VolumeRegression()
    {
        var w = Fixture(); int c = 40 * W + 60, s = 0;
        int small = (int)(P.VoxelSpace / (w.Chem.BodyVolume[s] * 10)) + 20;   // more 10-molecule bodies than one floor holds
        var bodies = new System.Collections.Generic.List<Agent>();
        for (int k = 0; k < small; k++) bodies.Add(w.TestAgent(c, 2, s, 10));
        var big = w.TestAgent(c, 2, s, 300);
        w.Relieve();
        Require(w.FloorVolume(c, 2) <= P.VoxelSpace + 1e-3f, "overfull floor was not relieved");
        Require(big.Y * W + big.X == c, "the big body was pushed instead of the small ones");
        Require(bodies.Any(b => b.Y * W + b.X != c) && bodies.All(b => b.Z <= 2), "small bodies were not pushed aside (or went up)");
        // The same room takes many more tiny bodies.
        var w2 = Fixture();
        int tiny = (int)(P.VoxelSpace / (w2.Chem.BodyVolume[s] * 3)) - 5;
        for (int k = 0; k < tiny; k++) w2.TestAgent(c, 2, s, 3);
        w2.Relieve();
        Require(w2.Count[c] == tiny, "tiny bodies that fit were pushed");
        Console.WriteLine($"PASS volume: {tiny} tiny bodies share a floor; {small} small ones overflow and the big one stays");
    }

    // Gnawing is a process: the work of every try stays in the face. Five gnawers at an ordered
    // monolith get about five times what one gets, and one alone gets there too, slowly; a disordered
    // mix, or the monolith with a protein for its bond, takes far less work per molecule.
    static void FaceRegression()
    {
        var w = Fixture(); var ch = w.Chem;
        int hard = 0;
        for (int s = 0; s < Chemistry.S; s++)
            if (MathF.Abs(w.TypicalBarrier(s + 2, 1) - 5) < MathF.Abs(w.TypicalBarrier(hard + 2, 1) - 5)) hard = s;
        int one = 50 * W + 50, five = 50 * W + 60, soft = 50 * W + 70, prot = 50 * W + 80;
        w.TestBlock(one, 2, hard, 255); w.TestBlock(five, 2, hard, 255); w.TestBlock(prot, 2, hard, 255);   // ordered monoliths
        var mix = new ushort[Chemistry.S];
        for (int s = 0; s < 8; s++) mix[(hard + 2 * s) % Chemistry.S] = 60;   // a disordered mix of eight kinds
        w.PutMixture(soft * Z + 2, mix, 25);
        Agent Gnawer(int c)
        {
            var g = w.TestAgent(c, 3, hard, 20);
            g.Energy = 1e6f;
            return g;
        }
        var lone = Gnawer(one);
        var crowd = Enumerable.Range(0, 5).Select(_ => Gnawer(five)).ToList();
        var mixer = Gnawer(soft);
        var tooled = Gnawer(prot);
        tooled.Enz[0] = new Enzyme { Kind = Enzyme.Split, A = (byte)hard, Amount = 2, Eff = 1, Topt = 15 }; tooled.EnzN = 1;
        float bHard = w.VoxelBarrier(one * Z + 2), bSoft = w.VoxelBarrier(soft * Z + 2);
        for (int t = 1; t <= 4000; t++)
        {
            w.Tick = t;
            for (int k = 0; k < 8; k++)
            {
                w.Mine(lone, one, -1, 4);
                foreach (var g in crowd) w.Mine(g, five, -1, 4);
                w.Mine(mixer, soft, -1, 4);
                w.Mine(tooled, prot, -1, 4);
            }
        }
        w.Tick = 0;
        int nOne = lone.NMines, nFive = crowd.Sum(g => g.NMines), nSoft = mixer.NMines, nProt = tooled.NMines;
        string got = $"barrier hard {bHard:F2} soft {bSoft:F2}; in 4000 ticks one gnawer {nOne}, five {nFive}, the mix {nSoft}, with a protein {nProt}";
        Require(nOne >= 1 && nFive >= 4 * nOne && nFive <= 6 * nOne + 2, "gnawing does not add up: " + got);
        Require(nSoft > 10 * nOne && nProt > 10 * nOne, "lattice or protein make no difference: " + got);
        Console.WriteLine("PASS rock face: " + got);
    }

    // The player's brush: what it pours or digs out is exactly what HandInput books; killing leaves
    // remains and sound cell lists; water adds up.
    static void HandRegression()
    {
        var w = Fixture();
        int cx = 60, cy = 40, c = cy * W + cx, s = w.RandomPourable();
        var before = w.ElementBudget();
        for (int k = 0; k < 12; k++) w.Pour(cx, cy, 4, s, 0.5f);
        Require(w.Height[c] > 3, $"pouring heaped nothing: height {w.Height[c]}");
        var after = w.ElementBudget();
        for (int e = 0; e < after.Length; e++) after[e] -= w.HandInput[e];
        BudgetEqual(before, after, "poured matter", 0.01);
        int h = w.Height[c];
        for (int k = 0; k < 40; k++) w.DigOut(cx, cy, 4, 0.5f);
        Require(w.Height[c] < h, "digging took nothing");
        after = w.ElementBudget();
        for (int e = 0; e < after.Length; e++) after[e] -= w.HandInput[e];
        BudgetEqual(before, after, "dug out matter", 0.01);
        for (int k = 0; k < 30; k++) w.TestAgent(c + (k % 5) - 2, w.Height[c + (k % 5) - 2], s, 10);
        var far = w.TestAgent(c + 20, w.Height[c + 20], s, 10);
        int killed = w.KillIn(cx, cy, 3);
        Require(killed == 30 && !far.Dead && w.DeathsHand == 30, $"brush killed {killed}, far one dead {far.Dead}");
        w.Agents.RemoveAll(a => a.Dead);
        w.CheckCellLists();
        double water = 0;
        w.PourWater(cx, cy, 2, 1f);
        for (int i = 0; i < N; i++) water += w.Water[i];
        Require(w.Water[c] == 1f && water > 1, $"water poured: centre {w.Water[c]}, total {water}");
        Console.WriteLine($"PASS hand: pour and dig booked exactly ({string.Join(" / ", w.HandInput.Select(x => x.ToString("F0")))} atoms net), kill {killed} in the brush, water");
    }

    // Bodies in water: without gas everything sinks (water is lighter than any packed molecule), gas
    // bubbles lift a body, strokes dearer with depth, light by depth, the bottom out of reach while
    // swimming, height kept from cell to cell.
    static void WaterRegression()
    {
        var w = Fixture();
        var ch = w.Chem;
        int light = -1, heavy = -1;
        for (int s = 0; s < Chemistry.S; s++)
        {
            if (s == ch.Gas) continue;
            float d = ch.Mass[s] / ch.Volume[s];
            if (light < 0 || d < ch.Mass[light] / ch.Volume[light]) light = s;
            if (heavy < 0 || d > ch.Mass[heavy] / ch.Volume[heavy]) heavy = s;
        }
        int row = 80 * W;
        for (int x = 100; x < 110; x++) w.Water[row + x] = 6;
        w.Water[row + 99] = 1;                                  // a shallow end
        for (int z = 2; z < 8; z++) w.TestBlock(row + 110, z, heavy);   // a bank level with the surface
        int c = row + 102;
        void Settle(Agent a, int ticks) { for (int t = 0; t < ticks; t++) { int cell = a.Y * W + a.X; w.Move(a, ref cell); } }
        Agent Floater(int cell, int s)
        {
            var f = w.TestAgent(cell, 2, s, 20);
            while (f.Density >= P.WaterDensity) w.AddMol(f, ch.Gas);
            return f;
        }

        var cork = w.TestAgent(c, 2, light, 20);
        var stone = w.TestAgent(c, 2, heavy, 20);
        var fish = Floater(c, heavy);
        int bubbles = fish.Inv[ch.Gas];
        Settle(cork, 200); Settle(stone, 200); Settle(fish, 200);
        Require(World.OnFloor(cork) && World.OnFloor(stone), $"a body without gas floated: light at {cork.Lift}, heavy at {stone.Lift}");
        Require(fish.Lift == w.Water[c], $"gas bubbles did not lift a heavy body to the surface: {fish.Lift}");
        // A motor pushes a stone up, it sinks back.
        stone.Enz[0] = new Enzyme { Kind = Enzyme.Motor, Amount = 2, Eff = 1, Topt = 15 }; stone.EnzN = 1;
        for (int k = 0; k < 3; k++) w.Motor(stone, -1, 4);
        Settle(stone, 6);
        float pushed = stone.Lift;
        Settle(stone, 300);
        Require(pushed > 0.5f && World.OnFloor(stone), $"motor push up: {pushed}, then {stone.Lift}");

        // Off the bottom nothing is in reach but a dissolved trace; the floor is.
        w.C[heavy][c] = 50;
        int had = fish.Inv[heavy];
        w.Intake(fish, c, heavy);
        Require(fish.Inv[heavy] == had && fish.Pend[heavy] < 0.01f, $"a swimmer drank from the bottom: {fish.Pend[heavy]}");
        w.Intake(stone, c, heavy);
        Require(stone.Pend[heavy] + stone.Inv[heavy] >= 21 - 1e-3f, "a body on the bottom could not take what lies there");
        int units = w.Units[c * Z + 1];
        w.Mine(fish, c, -1, 4);
        Require(w.Units[c * Z + 1] == units && fish.NMines == 0, "a swimmer gnawed the bottom");

        // Light by depth: near the surface far more of it.
        w.Light[c] = 0.1f;
        Require(w.AgentLight(fish) > 4 * w.AgentLight(stone), "light did not fade with depth");

        // A stroke on the deep bottom costs more than in the shallows.
        var deep = w.TestAgent(row + 104, 2, heavy, 20);
        var shallow = w.TestAgent(row + 99, 2, heavy, 20);
        float d0 = deep.Energy, s0 = shallow.Energy;
        deep.Vx = 1; shallow.Vx = -1;
        int dc = deep.Y * W + deep.X, sc = shallow.Y * W + shallow.X;
        w.Move(deep, ref dc); w.Move(shallow, ref sc);
        Require(deep.X == 105 && shallow.X == 98, "swimmers did not move");
        float dd = d0 - deep.Energy, ds = s0 - shallow.Energy;
        Require(dd > 1.5f * ds && ds > 0, $"depth did not make strokes dearer: deep {dd}, shallow {ds}");

        // Swimming keeps its height: at the surface over to the bank, along the bottom it is a wall.
        var swimmer = Floater(row + 108, light);
        Settle(swimmer, 100);
        int sw = swimmer.Y * W + swimmer.X;
        swimmer.Vx = 1; w.Move(swimmer, ref sw);
        Require(swimmer.X == 109 && swimmer.Lift == w.Water[sw], "a surface swimmer sank moving on");
        swimmer.Vx = 1; w.Move(swimmer, ref sw);
        Require(swimmer.X == 110 && swimmer.Z == 8 && swimmer.Lift == 0, $"a surface swimmer did not land on the bank: x {swimmer.X} z {swimmer.Z}");
        var walker = w.TestAgent(row + 109, 2, heavy, 20);
        walker.Vx = 1;
        int wc = walker.Y * W + walker.X;
        w.Move(walker, ref wc);
        Require(walker.X == 109, "a bottom walker climbed a wall of the lake");
        w.CheckCellLists();
        Console.WriteLine($"PASS water: without gas all sink, {bubbles} gas bubbles lift a heavy body, motor up, bottom out of reach, light and stroke cost by depth, height kept");
    }

    static void CaveRegression()
    {
        var w = Fixture(); int c = 60 * W + 80;
        w.TestBlock(c + 1, 5, 0);
        var a = w.TestAgent(c, 2, 0, 8); a.Vx = 1;
        w.Move(a, ref c);
        Require(a.Z == 2 && a.X == 81 && w.InCave(a), "agent did not enter the cavity");
        w.Light[c] = 1; w.Photon[c] = 3;
        int amount = a.Inv[0]; w.Photo(a, c, 0, 0);
        Require(a.Inv[0] == amount && a.NPhoto == 0, "cave agent consumed surface photons");
        w.Divide(a, c, 128, 4);
        Require(w.newborn.Count == 1 && w.newborn[0].Z == 2, "newborn teleported to roof");
        // The same column's surface remains are inaccessible underground; exposed burial is food.
        w.C[0][c] = 10;
        amount = a.Inv[0]; w.Intake(a, c, 0);
        Require(a.Inv[0] == amount && w.C[0][c] == 10, "cave intake reached the surface");
        w.BurialAt(c * Z + 1).Matter[0] = 1;
        w.Intake(a, c, 0);
        Require(a.Inv[0] == amount + 1, "exposed burial could not re-enter a body");
        Console.WriteLine("PASS caves: movement, occluded sunlight, birth at parent depth");
    }
}
