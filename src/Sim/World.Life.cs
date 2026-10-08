using System;
using System.Numerics;
using System.Threading;

namespace Primordium;

public sealed class Census
{
    public int Pop, Idle, Plants, Eaters, Miners, Hunters, Linked, OldestAge, Crowded, Big, MaxCells;
    public int InWater, Afloat, AtSurface;   // in a flooded column; of them off the bottom; of them at the surface
    // Sums over tens of thousands of bodies, then averages: double, so a big census does not round
    // (observation only; the simulation never reads a Census).
    public double AvgLen, AvgEnergy, AvgAge, AvgCycles, AvgEnz, AvgProt, AvgTb;
    public readonly float[] EnzKind = new float[4];   // average amount per body, by kind (summed in double by TakeCensus)
}

public sealed partial class World
{
    public const int CauseStarve = 1, CauseKilled = 2, CauseBroken = 3, CauseClimate = 4, CauseBuried = 5, CauseHand = 6;
    public const int DietIdle = 0, DietPlant = 1, DietEater = 2, DietMiner = 3, DietHunter = 4;

    // A cell holds any number of bodies; they share its light and soil, and the heat of their living
    // warms it — a crowd overheats itself (or keeps itself warm in the cold).
    public readonly Agent[] Head = new Agent[N];
    public readonly int[] Count = new int[N];

    void Place(Agent a, int cell)
    {
        a.PrevInCell = null;
        a.NextInCell = Head[cell];
        if (Head[cell] != null) Head[cell].PrevInCell = a;
        Head[cell] = a;
        Count[cell]++;
        a.X = cell % W;
        a.Y = cell / W;
    }

    void Unplace(Agent a, int cell)
    {
        if (a.PrevInCell != null) a.PrevInCell.NextInCell = a.NextInCell; else Head[cell] = a.NextInCell;
        if (a.NextInCell != null) a.NextInCell.PrevInCell = a.PrevInCell;
        a.NextInCell = a.PrevInCell = null;
        Count[cell]--;
    }

    void AddMol(Agent a, int s)
    {
        a.Inv[s]++; a.InvTotal++;
        a.Mass += Chem.Mass[s];
        a.Volume += Chem.BodyVolume[s];
        if (Chem.SplitExo[s]) a.Unstable++;
        if (Chem.Solid[s]) a.Solids++;
    }

    void RemoveMol(Agent a, int s)
    {
        a.Inv[s]--; a.InvTotal--;
        a.Mass -= Chem.Mass[s];
        a.Volume -= Chem.BodyVolume[s];
        if (Chem.SplitExo[s]) a.Unstable--;
        if (Chem.Solid[s]) a.Solids--;
    }

    // There is no "full": a body takes whatever it gets — carrying it is what costs (see Live).
    void AddOrSpill(Agent a, int s, int cell) => AddMol(a, s);

    int RandomMol(Agent a)
    {
        int r = Rng.Next(a.InvTotal), s = 0;
        while (r >= a.Inv[s]) { r -= a.Inv[s]; s++; }
        return s;
    }

    void Live(Agent a)
    {
        long liveStart = ProfileOps ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        LiveBody(a);
        if (ProfileOps && cur != null) cur.OpTicks[Genome.OpSlots] += System.Diagnostics.Stopwatch.GetTimestamp() - liveStart;
    }

    void LiveBody(Agent a)
    {
        int cell = a.Y * W + a.X;
        SettleAgent(a);
        if (a.Dead) return;
        if (a.Target?.Dead == true) a.Target = null;
        if (a.LinkWant?.Dead == true) a.LinkWant = null;
        double e0 = a.Energy;
        float kids0 = a.LifeKids, spent0 = a.LifeUpkeep + a.LifeHarm + a.LifeSpill;   // the energy probe (observation)
        a.TickPhoto = a.TickChem = a.TickMine = a.TickAttack = a.TickHeat = 0;
        long restStart = ProfileOps ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        LifeModels.Get(a.Model).Think(this, a, cell);   // its controller acts through the body physics (World.BodyOps)
        cell = a.Y * W + a.X;   // a block it laid or shifted may have pushed it to another cell
        if (ProfileOps && cur != null) cur.OpTicks[Genome.OpSlots] -= System.Diagnostics.Stopwatch.GetTimestamp() - restStart;
        if (a.Dead) return;
        Move(a, ref cell);
        TendLinks(a);
        SpreadBody(a, cell);

        // Body temperature follows the surroundings, slower for big bodies (reactions warm it up).
        // The reaction heat it holds goes into its cells at the same pace: counted once, in Tb and then
        // in the cells, not in both at once.
        float relax = 1f / (6f + 0.15f * a.Mass);
        a.Tb += (FootTemp(a) - a.Tb) * relax;
        double held = a.HeatHeld;
        a.HeatHeld = held - held * relax;
        double shed = held - a.HeatHeld;   // what the double actually lost: booked exactly
        a.TickHeat += (float)shed;
        Flows[FShed] += shed;

        // A solar flare (World.Sky): the dose of this tick wears proteins, costs energy and heats the
        // body; its mutations go with the UV below.
        float flareHarm = 0, dose = FlarePower > 0 ? Flare(a, out flareHarm) : (a.FlareDose = 0);

        // Reactive damage (World.React): what it holds and what lies where it stands reacts with its
        // proteins by one law for every species.
        if (ReactLaw && a.EnzN > 0) ReactiveDamage(a);

        // Outside the comfortable band harm grows exponentially: frost tears molecules out of the
        // body (one packed with molecules freezes later), heat unfolds proteins (except those whose
        // best temperature is high). Nothing else about temperature is built in.
        // Keeping mass costs more the more crammed the body is; every link to a partner costs too.
        float packing = a.Packing;
        float upkeep = P.CostBase + P.CostMass * a.Mass * (1 + packing * packing) + P.CostLen * a.G.Length
                     + P.CostCell * (a.Cells - 1) + P.CostLink * a.Links.Count, harm = 0;
        float lo = P.ComfortLo - P.Antifreeze * Math.Min(1f, packing);
        if (a.Tb < lo)
        {
            harm = P.FreezeK * (MathF.Exp(Math.Min(P.HarmExpMax, (lo - a.Tb) / P.TempTau)) - 1);   // finite however cold
            if (a.InvTotal > 0 && Rng.NextDouble() < harm * 4)
            {
                int s = RandomMol(a);
                RemoveMol(a, s);
                ChangeLoose(a, cell, s, 1f);
            }
        }
        else if (a.Tb > P.ComfortHi)
        {
            harm = P.HeatK * (MathF.Exp(Math.Min(P.HarmExpMax, (a.Tb - P.ComfortHi) / P.TempTau)) - 1);   // finite however hot
            for (int k = 0; k < a.EnzN; k++)
                if (a.Tb > a.Enz[k].Topt + 10) WearProtein(a, k, 1 - Math.Min(0.5f, harm * 3));
        }
        Dissipate(a, upkeep + harm);
        a.LifeUpkeep += upkeep + a.LastCycles * P.CostInstr;
        a.LifeHarm += harm;
        // Everything spent on living ends up as heat in its cells.
        float share = a.TickHeat / a.Cells;
        bool caveLaw = CaveLaw;
        for (int k = 0; k < a.Cells; k++)
        {
            int fc = FootCell(a, k);
            heatIn[fc] += share;
            if (caveLaw && a.Z < Height[fc]) caveHeatIn[fc] += share * Cover(fc, a.Z);   // under a roof: into the cave air
        }

        // Proteins wear out and have to be made again.
        int n = 0;
        for (int k = 0; k < a.EnzN; k++)
        {
            WearProtein(a, k, 1 - P.EnzDecay);
            if (a.Enz[k].Amount >= 0.05f) a.Enz[n++] = a.Enz[k];
            else ReturnProteinMatter(a, a.Enz[k].Material, a.Enz[k].Matter);
        }
        a.EnzN = n;

        // Holding a store of energy leaks some of it as heat — little in a modest store, steeply more
        // the more is hoarded beyond it. There is no cap.
        if (a.Energy > 0)
        {
            double ratio = a.Energy / a.Store, hold = P.HoldK * a.Energy * ratio * ratio;
            Dissipate(a, hold);
            a.LifeSpill += (float)hold;
        }
        a.EmaPhoto += (a.TickPhoto - a.EmaPhoto) * 0.01f;
        a.EmaChem += (a.TickChem - a.EmaChem) * 0.01f;
        a.EmaMine += (a.TickMine - a.EmaMine) * 0.01f;
        a.EmaAttack += (a.TickAttack - a.EmaAttack) * 0.01f;
        a.EmaGot += (a.TickGot - a.EmaGot) * 0.01f;
        a.EmaUpkeep += (upkeep + a.LastCycles * P.CostInstr - a.EmaUpkeep) * 0.01f;
        a.EmaHarm += (harm - a.EmaHarm) * 0.01f;
        a.EmaNet += ((float)(a.Energy - e0) - a.EmaNet) * 0.01f;
        a.TickGot = 0;
        a.Age++;
        if ((a.Age & 7) == 0) ChronLive(a);   // the chronicle looks: depth, caves, water (observation only)

        // Wear: unstable molecules fall apart (faster when warm), sunlight damages the genome.
        float pDecay = a.Unstable * P.DecayK * TempFactor(a.Tb);
        float pUv = P.UvK * AgentLight(a) * a.G.Length, pSun = pUv;
        if (dose > 0) pUv += P.FlareMutK * dose * a.G.Length;   // a flare: the same path, more often
        double u = Rng.NextDouble();
        if (u < pDecay)
        {
            int r = Rng.Next(a.Unstable), s = 0;
            foreach (int q in Chem.Unstable)
            {
                if (r < a.Inv[q]) { s = q; break; }
                r -= a.Inv[q];
            }
            RemoveMol(a, s);
            AddOrSpill(a, Chem.SplitA[s], cell);
            if (Chem.SplitB[s] >= 0) AddOrSpill(a, Chem.SplitB[s], cell);
            heatIn[cell] += Chem.SplitEnergy(s);
            Flows[FBodyDecay] += Chem.SplitEnergy(s);
        }
        else if (u < pDecay + pUv)
        {
            var g = (byte[])a.G.Clone();
            var p = (byte[])a.Prot.Clone();
            int at = Rng.Next(g.Length);
            g[at] = (byte)Rng.Next(256);
            p[at] = 0;
            a.SetGenome(g, p);
            if (u >= pDecay + pSun) FlareMutated();
        }
        // Thermal ageing of everything it holds (World.Wear; draws random numbers only with the law on).
        if (WearLaw) Wear(a, cell);
        if (EnergyProbe != null) EpLive(a, e0, kids0, spent0);

        if (a.Energy <= 0)
        {
            if (flareHarm > upkeep && flareHarm >= harm) { FlareKilled(); Die(a, cell, CauseFlare); }
            else Die(a, cell, harm > upkeep ? CauseClimate : CauseStarve);
        }
        else if (a.InvTotal < P.MinBody) Die(a, cell, CauseBroken);
    }

    // Recoil from expelled mass or pushes of a motor protein accumulate into motion. On land one block
    // up is a climb that costs energy by body mass, more is a wall; dropping more than a block hurts.
    // In water a body keeps its height as it swims on: it weighs only what it is heavier than the
    // water it pushes aside (a light one rises for nothing), drops do no harm, the water's drag stops
    // it sooner than ground does, and every stroke costs drag, more the deeper under the surface it
    // is (World.Water).
    void Move(Agent a, ref int cell)
    {
        cell = a.Y * W + a.X;
        if (MathF.Abs(a.Vx) >= 1 || MathF.Abs(a.Vy) >= 1)
        {
            int dir;
            if (MathF.Abs(a.Vx) >= MathF.Abs(a.Vy)) { dir = a.Vx > 0 ? 0 : 2; a.Vx -= MathF.Sign(a.Vx); }
            else { dir = a.Vy > 0 ? 1 : 3; a.Vy -= MathF.Sign(a.Vy); }
            int to = nb[cell * 4 + dir];
            float level = Level(a);
            int targetZ = WalkLevel(to, (int)level);
            // Room by volume: a body gets in if it fits once everybody smaller has been pushed aside.
            bool ok = to != cell && targetZ >= 0 && Fits(to, targetZ, Share(a), a);
            bool wet = InWater(cell, a.Z), toWet = ok && InWater(to, targetZ);
            float lift = toWet ? Math.Clamp(level - targetZ, 0, WaterTop(to, targetZ)) : 0;
            if (ok)
            {
                float dh = targetZ + lift - level, cost = 0;
                if (dh > 1) ok = false;
                else if (dh > 0) cost = P.CostClimb * dh * (toWet ? Weight(a) : a.Mass);
                if (wet || toWet) cost += Stroke(a, wet ? Below(a, cell) : Math.Max(0, WaterOver(to, targetZ) - lift));
                if (ok && cost > 0)
                {
                    if (a.Energy > cost + 1) Dissipate(a, cost); else ok = false;
                }
                if (ok && dh < -1 && !toWet) Dissipate(a, P.CostFall * (-dh - 1) * (1 + a.Mass * P.Gravity));
            }
            if (ok)
            {
                Unplace(a, cell);
                Place(a, to);
                a.Z = targetZ;
                a.Lift = lift;
                cell = to;
                a.NMoves++; Note(EvKind.Move);
                Act(a, ActMove, dir);
            }
            else if (dir % 2 == 0) a.Vx = 0; else a.Vy = 0;
        }
        Float(a, cell);
        if (P.Currents != 0) Drift(a, ref cell);   // the current carries a body off the bottom (World.Waterways)
        float fr = InWater(cell, a.Z) ? P.WaterFriction : P.Friction;
        a.Vx *= fr; a.Vy *= fr;
    }

    // Linked agents share energy and momentum; a link snaps when they drift apart.
    void TendLinks(Agent a)
    {
        for (int k = a.Links.Count - 1; k >= 0; k--)
        {
            var b = a.Links[k];
            if (b.Dead || !Near(a, b))
            {
                a.Links.RemoveAt(k);
                Unlink(b, a);
                continue;
            }
            if (a.Id > b.Id) continue;
            double f = (a.Energy - b.Energy) * P.LinkFlow;
            a.Energy -= f; b.Energy += f;
            var to = f > 0 ? b : a;
            to.TickGot += (float)Math.Abs(f);
            to.LifeGot += (float)Math.Abs(f);
            float vx = (a.Vx + b.Vx) * 0.5f, vy = (a.Vy + b.Vy) * 0.5f;
            a.Vx = b.Vx = vx; a.Vy = b.Vy = vy;
        }
    }

    // `b` forgets its link to `a`. A partner that has drifted away may be stepped by another square
    // at this moment: it is told after the phase (Ctx.Unlinks), in square order.
    void Unlink(Agent b, Agent a)
    {
        var ctx = cur;
        if (ctx != null && (b.Dead || !Near(a, b))) ctx.Unlinks.Add((b, a));
        else b.Links.Remove(a);
    }

    // Within touching distance: same or neighbouring cell, further for big bodies.
    static bool Near(Agent a, Agent b)
    {
        int dx = Math.Abs(a.X - b.X);
        dx = Math.Min(dx, W - dx);
        return MathF.Abs(Level(a) - Level(b)) <= 1 && dx + Math.Abs(a.Y - b.Y) <= 1 + Reach(a) + Reach(b);
    }

    public static int Kinship(Agent a, Agent b) => 64 - BitOperations.PopCount(a.Tag ^ b.Tag);

    // Death returns all substrate at its actual depth. Stored usable energy becomes heat.
    void Die(Agent a, int cell, int cause, int burialVoxel = -1)
    {
        if (a.Dead) return;
        cell = a.Y * W + a.X;   // where it actually is: its own deeds may have pushed it elsewhere this tick
        if (burialVoxel < 0 && InCave(a)) burialVoxel = cell * Z + Math.Max(1, a.Z - 1);
        EpDeath(a);   // the energy probe (observation)
        Qty[] buried = burialVoxel >= 0 ? BurialAt(burialVoxel).Matter : null;
        for (int s = 0; s < Chemistry.S; s++)
        {
            Qty amount = a.Inv[s] + a.Pend[s];
            if (buried == null) C[s][cell] += amount; else buried[s] += amount;
        }
        for (int k = 0; k < a.EnzN; k++)
        {
            var e = a.Enz[k];
            if (buried == null) C[e.Material][cell] += e.Matter; else buried[e.Material] += e.Matter;
        }
        double heat = Math.Max(0, a.Energy) + a.HeatHeld;
        heatIn[cell] += (float)heat;
        var flows = Flows;
        flows[FDeath] += heat;
        if (a.Energy < 0) flows[FWriteOff] -= a.Energy;   // its debt leaves the stock (see World.Energy)
        a.HeatHeld = 0;
        foreach (var b in a.Links) Unlink(b, a);
        a.Links.Clear();
        a.Target = a.LinkWant = null;
        int cells = a.Cells;
        ReleaseFoot(a); a.Cells = cells;
        Unplace(a, cell);
        a.Dead = true; a.Cause = cause;
        if (a.Tracked) ChronDeath(a);
        DeathMap[cell] += 1;
        switch (cause)
        {
            case CauseStarve: Interlocked.Increment(ref DeathsStarve); break;
            case CauseKilled: Interlocked.Increment(ref DeathsKilled); break;
            case CauseClimate: Interlocked.Increment(ref DeathsClimate); break;
            case CauseBuried: Interlocked.Increment(ref DeathsBuried); break;
            case CauseHand: Interlocked.Increment(ref DeathsHand); break;
            case CauseFlare: Interlocked.Increment(ref DeathsFlare); break;
            default: Interlocked.Increment(ref DeathsBroken); break;
        }
        MarkDirty(cell);
        if (burialVoxel >= 0) MatterChanged(burialVoxel);
        if (cause != CauseKilled) AddFlash(a.X, a.Y, FlashDeath);
    }

    int Neighbour4(int cell, int to)
    {
        for (int d = 0; d < 4; d++) if (nb[cell * 4 + d] == to) return d;
        return -1;
    }

    // Where a newborn can go: the cell itself (d = 4) or a neighbour it can reach.
    // Where a newborn of `volume` can go: the cell itself (d = 4) or a neighbour it can reach, if
    // that floor has room for it.
    int Nursery(Agent a, int cell, int d, float volume)
    {
        int c = d == 4 ? cell : nb[cell * 4 + d];
        if (c == cell && d != 4) return -1;
        int level = WalkLevel(c, a.Z);
        if (level < 0 || !Fits(c, level, volume)) return -1;
        return c;
    }

    public readonly long[] DivFail = new long[5];   // diagnostics: tries, no energy, no body, no room, uneven split

    // Splitting in two: a body that has grown enough gives a share f of its energy and of every
    // molecule to a child in the cell itself or next to it (d 0–3, 4 here; f ≤ 0 halves, else f/256
    // within 16–240); the genome is copied with its model's errors (ILifeModel.Mutate).
    public void Divide(Agent a, int cell, int f, int d)
    {
        float cost = P.DivCostBase + P.DivCostByte * a.G.Length;
        Interlocked.Increment(ref DivFail[0]);
        if (a.Energy < P.DivMinEnergy + cost) { Interlocked.Increment(ref DivFail[1]); return; }
        if (a.InvTotal < P.DivMinBody) { Interlocked.Increment(ref DivFail[2]); return; }
        float frac = f <= 0 ? 0.5f : Math.Clamp(f, 16, 240) / 256f;
        int to = Nursery(a, cell, d, a.Volume * frac);
        if (to < 0) { Interlocked.Increment(ref DivFail[3]); return; }

        Span<int> give = stackalloc int[Chemistry.S];
        give.Clear();
        int tot = 0;
        for (int s = 0; s < Chemistry.S; s++)
        {
            if (a.Inv[s] == 0) continue;
            give[s] = Math.Min(a.Inv[s], (int)(a.Inv[s] * frac + Rng.NextDouble()));
            tot += give[s];
        }
        if (tot < P.MinBody || a.InvTotal - tot < P.MinBody) { Interlocked.Increment(ref DivFail[4]); return; }

        Dissipate(a, cost);
        var life = LifeModels.Get(a.Model);
        var (g, p) = life.Mutate(a.G, a.Prot, Rng);
        var child = new Agent(NewId(), a.Lineage, a.Gen + 1, g, p, a.Model) { Tb = a.Tb };
        Looks.Inherit(child, a, Rng);
        child.Energy = a.Energy * frac;
        a.Energy -= child.Energy;
        a.LifeKids += (float)(cost + child.Energy);
        for (int s = 0; s < Chemistry.S; s++)
            for (int k = 0; k < give[s]; k++) { RemoveMol(a, s); AddMol(child, s); }
        life.InheritState(a, child);
        Born(a, child, to);
        Act(a, ActDivide, to == cell ? -1 : Neighbour4(cell, to));
    }

    void Born(Agent parent, Agent child, int cell, Agent mate = null)
    {
        child.LifeStart = (float)child.Energy;
        child.Z = WalkLevel(cell, parent.Z);
        SetLift(child, cell, child.Z, Level(parent));   // born in water: at its parent's height
        Place(child, cell);
        (cur?.Newborn ?? newborn).Add(child);
        parent.NChildren++;
        ChronBorn(parent, child, mate);
        EvoBirth(parent, child);
        Interlocked.Increment(ref Births);
        Note(EvKind.Divide);
        for (int m = MaxGen; child.Gen > m; m = MaxGen)
            if (Interlocked.CompareExchange(ref MaxGen, child.Gen, m) == m) break;
    }

    // Two bodies that both signalled readiness within a few ticks make a child that carries the start
    // of one genome and the end of the other; each gives P.MateShare (a quarter) of its energy and molecules.
    // Only bodies of the same life model mate (their genomes are written in the same code).
    public void Mate(Agent a, int cell)
    {
        Dissipate(a, P.CostSocial);
        a.MateTick = Tick;
        var t = Partner(a, cell);
        if (t == null || t.Model != a.Model || Tick - t.MateTick > P.HandshakeTicks || t.Energy < P.MateMinEnergy || a.Energy < P.MateMinEnergy) return;
        if (!Fits(cell, a.Z, (a.Volume + t.Volume) * P.MateShare) || a.InvTotal < 2 * P.MinBody || t.InvTotal < 2 * P.MinBody) return;
        var life = LifeModels.Get(a.Model);
        var (g0, p0) = life.Cross(a, t, Rng);
        var (g, p) = life.Mutate(g0, p0, Rng);
        var child = new Agent(NewId(), a.Lineage, Math.Max(a.Gen, t.Gen) + 1, g, p, a.Model) { Tb = a.Tb };
        Looks.Inherit(child, Rng.NextDouble() < 0.5 ? a : t, Rng);
        foreach (var parent in new[] { a, t })
        {
            double e = parent.Energy * P.MateShare;
            parent.Energy -= e;
            parent.LifeKids += (float)e;
            child.Energy += e;
            for (int s = 0; s < Chemistry.S; s++)
            {
                int k = (int)(parent.Inv[s] * P.MateShare);
                for (int j = 0; j < k && parent.InvTotal > P.MinBody; j++) { RemoveMol(parent, s); AddMol(child, s); }
            }
        }
        if (child.InvTotal < P.MinBody)
        {
            // Not enough matter for a body: what was given spills on the ground.
            for (int s = 0; s < Chemistry.S; s++) if (child.Inv[s] > 0) ChangeLoose(a, cell, s, child.Inv[s]);
            heatIn[cell] += (float)child.Energy;
            Flows[FStillborn] += child.Energy;
            return;
        }
        a.MateTick = t.MateTick = -100;
        a.NMates++; t.NMates++;
        Note(EvKind.Mate);
        Born(a, child, cell, t);
        Act(a, ActDivide, -1);
    }

    // Abiogenesis: a random genome wrapped in a body made of whatever lies in a cell (or of its soft
    // top block), powered by burning some of those molecules.
    public bool SpawnRandom()
    {
        for (int t = 0; t < 30; t++)
            if (SpawnAt(Rng.Next(N), false)) return true;
        return false;
    }

    // A group of random newcomers around a cell; user input still cannot manufacture matter.
    public int SpawnGroup(int cx, int cy, int n)
    {
        int made = 0;
        for (int t = 0; t < n * 6 && made < n; t++)
        {
            int dx = Rng.Next(-7, 8), dy = Rng.Next(-7, 8), y = cy + dy;
            if (dx * dx + dy * dy > 49 || y < 0 || y >= H) continue;
            if (SpawnAt(y * W + ((cx + dx) % W + W) % W, true)) made++;
        }
        return made;
    }

    bool SpawnAt(int i, bool force)
    {
        if (!Fits(i, Height[i], P.SpawnBody * 4f) || (!force && Submerged(i))) return false;
        int top = i * Z + Height[i] - 1;
        bool soft = top >= 0 && VoxelBarrier(top) < 2;
        int available = soft ? Units[top] : 0;
        for (int s = 0; s < Chemistry.S; s++) if (s != Chem.Gas) available += (int)C[s][i].F;
        if (available < P.SpawnBody) return false;
        long id = NewId();
        var a = new Agent(id, id, 0, Genome.Random(Rng)) { Tb = Temp[i], Z = Height[i] };
        Looks.Apply(a);
        for (int k = 0; k < P.SpawnBody; k++)
        {
            int total = 0;
            for (int s = 0; s < Chemistry.S; s++) if (s != Chem.Gas) total += (int)C[s][i].F;
            int q = -1;
            if (total > 0)
            {
                int choice = Rng.Next(total);
                for (int s = 0; s < Chemistry.S; s++)
                {
                    if (s == Chem.Gas) continue;
                    choice -= (int)C[s][i].F;
                    if (choice < 0) { q = s; C[s][i] -= 1; break; }
                }
            }
            else q = TakeSoft(i);
            if (q < 0) break;
            AddMol(a, q);
        }
        // Harvest only released bond energy; all reaction products stay in the world.
        float energy = 0;
        for (int tries = 0; tries < Chemistry.S && energy < P.SpawnEnergy; tries++)
        {
            int best = -1;
            for (int s = 0; s < Chemistry.S; s++)
                if (Chem.SplitExo[s] && C[s][i] >= 0.1f && (best < 0 || Chem.SplitEnergy(s) > Chem.SplitEnergy(best))) best = s;
            if (best < 0) break;
            Qty take = Qty.Min(C[best][i], (P.SpawnEnergy - energy) / Chem.SplitEnergy(best));
            C[best][i] -= take;
            C[Chem.SplitA[best]][i] += take;
            if (Chem.SplitB[best] >= 0) C[Chem.SplitB[best]][i] += take;
            energy += (float)(take * Chem.SplitEnergy(best));
            Flows[FAbio] += take * Chem.SplitEnergy(best);
        }
        if (a.InvTotal < P.MinBody || energy <= 0)
        {
            for (int s = 0; s < Chemistry.S; s++) C[s][i] += a.Inv[s];
            heatIn[i] += energy;
            Flows[FStillborn] += energy;
            return false;
        }
        a.Energy = a.LifeStart = energy;
        a.Z = Height[i];
        Place(a, i); Agents.Add(a); Spawns++;
        EvoRegister(a);   // a founder: a root of the family tree
        TrackNew(a, Chronicle.WhyFounder);   // a founder of a lineage keeps a biography
        return true;
    }

    int TakeSoft(int i)
    {
        int h = Height[i], v = i * Z + h - 1;
        if (h <= 2 || VoxelBarrier(v) >= 2 || Units[v] == 0) return -1;
        return TakeVoxelMolecule(v);
    }

    // Diagnostics only (used by tools/bench): plant a given genome in fertile cells.
    public int Probe(byte[] genome, int n)
    {
        int made = 0;
        for (int t = 0; t < n * 50 && made < n; t++)
        {
            int i = Rng.Next(N);
            if (Submerged(i) || Count[i] > 0 || Chem.MatTier[TopMat(i)] > 1) continue;
            if (!SpawnAt(i, true)) continue;
            var a = Agents[^1];
            a.SetGenome((byte[])genome.Clone(), new byte[genome.Length]);
            made++;
        }
        return made;
    }

    public static int Diet(Agent a)
    {
        if (a.EmaAttack > 0.004f) return DietHunter;
        float p = a.EmaPhoto, c = a.EmaChem, m = a.EmaMine * 0.3f;
        float mx = Math.Max(p, Math.Max(c, m));
        if (mx < 0.003f) return DietIdle;
        return mx == p ? DietPlant : mx == c ? DietEater : DietMiner;
    }

    public Census TakeCensus()
    {
        var c = new Census();
        var enzKind = new double[4];
        foreach (var a in Agents)
        {
            if (a.Dead) continue;
            c.Pop++;
            c.AvgLen += a.G.Length;
            c.AvgEnergy += (float)a.Energy;
            c.AvgAge += a.Age;
            c.AvgCycles += a.LastCycles;
            c.AvgTb += a.Tb;
            if (a.Age > c.OldestAge) c.OldestAge = a.Age;
            if (a.Links.Count > 0) c.Linked++;
            if (Count[a.Y * W + a.X] > 1) c.Crowded++;
            if (a.Cells > 1) c.Big++;
            if (a.Cells > c.MaxCells) c.MaxCells = a.Cells;
            if (InWater(a))
            {
                c.InWater++;
                if (!OnFloor(a)) c.Afloat++;
                if (AtSurface(a, a.Y * W + a.X)) c.AtSurface++;
            }
            for (int k = 0; k < a.EnzN; k++) enzKind[a.Enz[k].Kind] += a.Enz[k].Amount;
            c.AvgEnz += a.EnzN;
            int prot = 0;
            foreach (var b in a.Prot) if (b > 20) prot++;
            c.AvgProt += prot / (double)a.Prot.Length;
            switch (Diet(a))
            {
                case DietPlant: c.Plants++; break;
                case DietEater: c.Eaters++; break;
                case DietMiner: c.Miners++; break;
                case DietHunter: c.Hunters++; break;
                default: c.Idle++; break;
            }
        }
        if (c.Pop > 0)
        {
            c.AvgLen /= c.Pop; c.AvgEnergy /= c.Pop; c.AvgAge /= c.Pop; c.AvgCycles /= c.Pop;
            c.AvgTb /= c.Pop; c.AvgEnz /= c.Pop; c.AvgProt /= c.Pop;
            for (int k = 0; k < 4; k++) c.EnzKind[k] = (float)(enzKind[k] / c.Pop);
        }
        return c;
    }
}
