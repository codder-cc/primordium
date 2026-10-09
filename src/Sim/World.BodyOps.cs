using System;
using System.Numerics;

namespace Primordium;

// Body physics: the physical primitives any life model (ILifeModel, LifeModels.cs) may call on its
// body. Every one books its physical cost through the same ledger (Dissipate → World.Energy) and the same
// matter pools, whichever controller asked for it; none of them decides anything — what to do, when and
// with what argument is the controller's business (model 1: the genome VM, World.Vm.cs). See
// docs/SIMULATION.md, "Life models".
//
// Every call runs in the creature phase under the thread contract ("Performance and threads"): it writes
// only within the body's reach, global effects go through Ctx / Interlocked, random numbers from Rng.
// `cell` is the body's own cell (a.Y * W + a.X); `site` is a controller-defined index credited when the
// act works (Worked: the genome bytes there gain copy protection, Agent.Prot), -1 for none.
//
//   membrane      Intake(a, cell, s)  Drink(a, cell)  Expel(a, cell, s, d)
//   reactions     MakeProtein(a, spec, site)  Bind(a, site, s1, s2)  SplitMol(a, site, s)  DigestAny(a, site)
//                 Photo(a, cell, site, s)
//   own work      Spend(a, cost)
//   force         Motor(a, site, d)   (d 0–3 along the ground, 4/5 strokes up/down in water)
//   contact       PickBody(a, cell, n)  Partner(a, cell)  Attack(a, cell, p)  Take(a, cell, s)  Give(a, cell, s)
//                 Share(a, cell, amount)  LinkOp(a, cell)  Inject(a, cell, start, len)
//   genome        Cut(a, start, len)  Divide(a, cell, f, d)  Mate(a, cell)   (World.Life.cs)
//   rock          Mine(a, cell, site, d)  Dig(a, cell, d)  Pile(a, cell, d)  Grow(a, cell, d)
//   reading       OutsideAmount(a, cell, s)  NeighbourCell(cell, d)  AmbientTemp(a, cell)  LightAt(a)
//                 PhotonsAt(a, cell)  UvSense(a)  Gradient(a, cell, s)  GroundAt(a, cell, d)  Alarm(a, cell)
//                 Look(a, cell, d, range)  Kinship(a, b)   — and the body's own state on Agent (Inv, Energy,
//                 Tb, Mass, InvTotal, Enz, Age, Signal of others)
public sealed partial class World
{
    // Work the controller does itself (thinking, signalling): paid from the body's energy, ends as heat in its
    // cell (the same booking as every cost here).
    public void Spend(Agent a, double cost) => Dissipate(a, cost);

    // ---- attention ----

    // Choose whom to deal with: the n-th other body in this cell, or (n < 0) the first one in the
    // neighbouring cell -n-1. Sets and returns Agent.Target (null: nobody found).
    public Agent PickBody(Agent a, int cell, int n)
    {
        a.Target = null;
        if (n < 0)
        {
            int c = nb[cell * 4 + ((-n - 1) & 3)];
            if (c != cell) a.Target = Head[c] ?? (Big[c] != a ? Big[c] : null);
        }
        else
        {
            int m = Candidates(a);
            if (m > 0) a.Target = CandidateAt(a, n % m);
        }
        return a.Target;
    }

    // Whom a contact act works on: the picked body if it is still alive and near, otherwise
    // somebody random in the same cell (or nobody).
    public Agent Partner(Agent a, int cell)
    {
        var t = a.Target;
        if (t != null && !t.Dead && Near(a, t)) return t;
        int n = Candidates(a);
        return n <= 0 ? null : CandidateAt(a, Rng.Next(n));
    }

    // ---- proteins and reactions ----

    // Whether the body can afford to fold a protein now (MakeProtein does nothing otherwise).
    public bool CanMakeProtein(Agent a) => !(Avail(a) < P.CostExpress + P.EnergyReserve || a.InvTotal <= P.MinBody);

    // Make one unit of the protein `spec` (Kind, A, B, Topt, Eff; how a model gets it from its genome is
    // its own business — model 1 decodes three genome bytes, Genome.Decode). Costs energy and one
    // molecule of the body (its most plentiful kind) as material; the same protein made again adds to it.
    // `site` is remembered as the protein's gene (Enzyme.Src: credited when the protein works).
    public void MakeProtein(Agent a, Enzyme spec, int site)
    {
        if (!CanMakeProtein(a)) return;
        int m = 0;
        for (int s = 1; s < Chemistry.S; s++) if (a.Inv[s] > a.Inv[m]) m = s;
        int slot = -1;
        for (int k = 0; k < a.EnzN; k++)
        {
            ref var e = ref a.Enz[k];
            if (e.Kind == spec.Kind && e.A == spec.A && e.B == spec.B && e.Topt == spec.Topt && e.Material == m)
            {
                slot = k; break;
            }
        }
        Settle(a);   // law 1: what it owed is paid from what it held before the molecule is folded away
        RemoveMol(a, m);
        a.Mass += Chem.Mass[m]; // folded substrate remains physically inside the body
        a.Volume += Chem.Volume[m];
        Dissipate(a, P.CostExpress);
        
        a.NExpress++; Note(EvKind.Express);
        if (slot >= 0)
        {
            a.Enz[slot].Amount += 1; a.Enz[slot].Matter += 1; a.Enz[slot].Src = site;
            if (a.Enz[slot].Amount >= 3 && a.Enz[slot].Amount < 4 && (uint)site < (uint)a.Prot.Length && a.Prot[site] > 0) ChronProtein(a, a.Enz[slot]);   // kept and proven useful: news?
            return;
        }
        slot = a.EnzN++;
        if (slot == a.Enz.Length) Array.Resize(ref a.Enz, a.Enz.Length * 2);   // no limit on kinds kept
        spec.Amount = 1; spec.Matter = 1; spec.Material = (byte)m; spec.Src = site;
        a.Enz[slot] = spec;
        if (a.Bio != null && (a.BioSeen & 1 << spec.Kind) == 0)
        {
            a.BioSeen |= (byte)(1 << spec.Kind);
            BioNote(a, Tick, BioKind.Protein, spec.A | spec.B << 5, spec.Eff, spec.Kind);
        }
    }

    // How strongly a reaction is driven right now: the best matching protein (its amount × quality ×
    // how close the body is to its best temperature) — above 1 it turns the reaction over several
    // times per instruction — otherwise only the slow spontaneous rate.
    float Chance(Agent a, int kind, int s1, int s2, out int slot)
    {
        slot = -1;
        float best = 0;
        for (int k = 0; k < a.EnzN; k++)
        {
            ref var e = ref a.Enz[k];
            if (e.Kind != kind) continue;
            bool match = kind switch
            {
                Enzyme.Bind => (e.A == s1 && e.B == s2) || (e.A == s2 && e.B == s1),
                Enzyme.Motor => true,
                _ => e.A == s1,
            };
            if (!match) continue;
            float d = (a.Tb - e.Topt) / P.EnzWidth;
            float v = e.Amount * e.Eff * MathF.Exp(-d * d);
            if (v > best) { best = v; slot = k; }
        }
        if (slot >= 0) return best;
        return kind == Enzyme.Motor ? 0f : P.Spont * Math.Min(1f, TempFactor(a.Tb));
    }

    // How many times a reaction goes on this instruction (0 if it fails). No fixed maximum: the
    // reaction loops stop when a substrate (or photon) runs out.
    int Turnovers(float drive) => drive >= 1 ? (int)drive : Rng.NextDouble() < drive ? 1 : 0;

    // A reaction that needs energy (de < 0) hardly goes by itself: only a protein can couple it to the
    // body's energy; unaided it happens with the Boltzmann chance e^(de/2).
    static float Uphill(float drive, int slot, float de) => de >= 0 || slot >= 0 ? drive : drive * MathF.Exp(de * 0.5f);

    // Something worked: the instruction and the gene of the protein that did it become a little more
    // protected against copy errors.
    static void Worked(Agent a, int slot, int site)
    {
        var p = a.Prot;
        if (site >= 0 && site < p.Length && p[site] < 255) p[site]++;
        if (slot < 0) return;
        int src = a.Enz[slot].Src;
        if (src < 0) return;
        for (int k = 0; k < 4 && src + k < p.Length; k++)
            if (p[src + k] < 255) p[src + k]++;
    }

    // Energy from a reaction: part of what is released warms the body instead.
    void Release(Agent a, float de)
    {
        if (MatterLaw)
        {
            // Law 1 (World.Charge): an uphill reaction takes its energy from carriers into the product's bonds
            // (the caller checked they hold it); a downhill one warms the body and charges it.
            if (de <= 0) { Relax(a, -de, false, false); a.LifeUphill -= de; return; }
            ReleaseGain(a, de);
            if (PredProbe != null) PredationProbe.Add(ref PredProbe.Captured, de * (1 - P.CaptureHeat));   // observation
            a.GainChem += de * (1 - P.CaptureHeat);
            a.TickChem += de;
            a.LastMeal = a.Age;
            return;
        }
        double e0 = a.Energy, h0 = a.HeatHeld;
        if (de <= 0) { a.Energy += de; a.LifeUphill -= de; Flows[FRounding] += de - (a.Energy - e0); return; }
        double keep = de * (1.0 - P.HeatShare);
        a.Energy += keep;
        // The heat share warms the body; it reaches the cells only as the body cools (LiveBody).
        a.Tb += de * P.HeatShare * 6f / (5f + a.Mass);
        a.HeatHeld += de - keep;
        Flows[FRounding] += de - (a.Energy - e0) - (a.HeatHeld - h0);   // doubles round too, ~1e-16 (see World.Energy)
        a.GainChem += de * (1 - P.HeatShare);
        a.TickChem += de;
        a.LastMeal = a.Age;
    }

    public void Bind(Agent a, int site, int s1, int s2)
    {
        int p = Chem.Combine[s1, s2];
        if (p < 0) return;
        int bond = Chem.E[s1] + Chem.E[s2] - Chem.E[p];
        float de = bond * P.EnergyK;
        float drive = Chance(a, Enzyme.Bind, s1, s2, out int slot);
        int times = Turnovers(Uphill(drive, slot, de)), done = 0;
        for (; done < times; done++)
        {
            if (a.Inv[s1] == 0 || a.Inv[s2] == 0 || (s1 == s2 && a.Inv[s1] < 2)) break;
            if (de < 0 && (Avail(a) + de < P.EnergyReserve || !CarriersCover(a, -de, s1, s2))) break;
            RemoveMol(a, s1); RemoveMol(a, s2); AddMol(a, p);
            Release(a, de);
        }
        if (done == 0) return;
        if (de != bond) Flows[FScale] += done * ((double)de - bond);   // P.EnergyK ≠ 1 (see World.Energy)
        if (EnergyProbe != null) { EpAdd(de > 0 ? EnergyEconomyProbe.BindExoN : EnergyEconomyProbe.BindUpN, done); EpAdd(de > 0 ? EnergyEconomyProbe.BindExoE : EnergyEconomyProbe.BindUpE, (double)done * de); }
        a.NBind += done;
        if (slot >= 0) ChronReaction(a, Enzyme.Bind, s1, s2);
        Worked(a, slot, site);
        Note(EvKind.Bind);
        Act(a, ActEat, -1);
    }

    public void SplitMol(Agent a, int site, int s)
    {
        int x = Chem.SplitA[s], y = Chem.SplitB[s];
        if (x < 0) return;
        int bond = Chem.SplitEnergy(s);
        float de = bond * P.EnergyK;
        float drive = Chance(a, Enzyme.Split, s, 0, out int slot);
        int times = Turnovers(Uphill(drive, slot, de)), done = 0;
        for (; done < times; done++)
        {
            if (a.Inv[s] == 0) break;
            if (de < 0 && (Avail(a) + de < P.EnergyReserve || !CarriersCover(a, -de, s, -1))) break;
            RemoveMol(a, s); AddMol(a, x); if (y >= 0) AddMol(a, y);
            Release(a, de);
        }
        if (done == 0) return;
        if (de != bond) Flows[FScale] += done * ((double)de - bond);
        if (EnergyProbe != null) { EpAdd(de > 0 ? EnergyEconomyProbe.SplitExoN : EnergyEconomyProbe.SplitUpN, done); EpAdd(de > 0 ? EnergyEconomyProbe.SplitExoE : EnergyEconomyProbe.SplitUpE, (double)done * de); }
        a.NSplit += done;
        if (slot >= 0) ChronReaction(a, Enzyme.Split, s, 0);
        Worked(a, slot, site);
        Note(EvKind.Split);
        Act(a, ActEat, -1);
    }

    // Split whatever molecule comes to hand (a random one of the body's, by count).
    public void DigestAny(Agent a, int site)
    {
        if (a.InvTotal > 0) SplitMol(a, site, RandomMol(a));
    }

    // Catch a photon from the cell's shared trickle and use it to lift a molecule to a richer one.
    // The energy is stored in that molecule — a body still has to break it down to use it. Light
    // excites whatever it hits: if the body has none of the molecule asked for, some other one.
    public void Photo(Agent a, int cell, int site, int s)
    {
        if (a.Inv[s] == 0 && a.InvTotal > 0) s = RandomMol(a);
        int p = Chem.PhotoUp[s];
        if (p < 0) return;
        if (CanopyLaw) { PhotoCanopy(a, site, s, p); return; }   // variant B: the body's own store
        cell = BrightestCell(a);   // only exposed parts of a large body catch surface photons
        if (cell < 0) return;
        // The cell's photons arrive at the water's surface; each block of water above the body
        // swallows some of them before they reach it.
        float reach = MathF.Exp(-P.WaterDim * Below(a, cell));
        // Bodies above it in the cell take the light first (World.Sky): a photon it misses stays for them.
        float shade = ShadeOf(a, cell);
        int times = Turnovers(Chance(a, Enzyme.Photo, s, 0, out int slot)), caught = 0;
        // Photodamage (World.Wear): the chance the excitation breaks the molecule instead of being stored.
        float damage = PhotoDamageLaw && times > 0 ? PhotoDamageChance(a, s, p) : 0;
        for (int k = 0; k < times; k++)
        {
            float ph = Photon[cell];
            if (a.Inv[s] == 0 || ph < 1f) break;
            if (shade < 1 && Rng.NextDouble() >= shade) continue;
            Photon[cell] = Math.Max(0, ph - 1);
            if (reach < 1 && Rng.NextDouble() >= reach) continue;
            RemoveMol(a, s);
            AddMol(a, p);
            float gain = Chem.E[p] - Chem.E[s];
            a.GainPhoto += gain;
            a.TickPhoto += gain;
            Flows[FPhoto] += gain;
            if (PredProbe != null) PredationProbe.Add(ref PredProbe.Photo, gain);
            if (EnergyProbe != null) { EpAdd(EnergyEconomyProbe.PhotoCaught, 1); EpAdd(EnergyEconomyProbe.PhotoGain, gain); }
            caught++;
            if (damage > 0) PhotoDamageAfter(a, p, damage);
        }
        if (caught == 0) return;
        a.NPhoto += caught;
        if (slot >= 0) ChronReaction(a, Enzyme.Photo, s, 0);
        Worked(a, slot, site);
        Note(EvKind.Photo);
        Act(a, ActEat, -1);
    }

    // The canopy (P.Canopy 1, World.Sky): the photons are the ones the body itself stopped at the last light
    // updates (water and the bodies above have had their share already), so only its own store is spent.
    void PhotoCanopy(Agent a, int site, int s, int p)
    {
        int times = Turnovers(Chance(a, Enzyme.Photo, s, 0, out int slot)), caught = 0;
        float damage = PhotoDamageLaw && times > 0 ? PhotoDamageChance(a, s, p) : 0;
        for (int k = 0; k < times; k++)
        {
            if (a.Inv[s] == 0 || a.LightQuota < 1f) break;
            a.LightQuota -= 1;
            RemoveMol(a, s);
            AddMol(a, p);
            float gain = Chem.E[p] - Chem.E[s];
            a.GainPhoto += gain;
            a.TickPhoto += gain;
            Flows[FPhoto] += gain;
            if (EnergyProbe != null) { EpAdd(EnergyEconomyProbe.PhotoCaught, 1); EpAdd(EnergyEconomyProbe.PhotoGain, gain); }
            caught++;
            if (damage > 0) PhotoDamageAfter(a, p, damage);
        }
        if (caught == 0) return;
        a.NPhoto += caught;
        if (slot >= 0) ChronReaction(a, Enzyme.Photo, s, 0);
        Worked(a, slot, site);
        Note(EvKind.Photo);
        Act(a, ActEat, -1);
    }

    // A motor protein turns energy into a push; a weak motor needs several pushes to move a body.
    // Directions 0–3 push along the ground (`push`); 4 and 5 are strokes up and down (`swim`, a
    // second meaning of nop), which do nothing — and cost nothing — out of water. In water every
    // push costs more the deeper under the surface the body is.
    public void Motor(Agent a, int site, int d)
    {
        float power = Chance(a, Enzyme.Motor, 0, 0, out int slot);
        if (slot < 0) return;
        int cell = a.Y * W + a.X;
        bool wet = InWater(cell, a.Z);
        if (d >= 4 && !wet) return;   // nothing to push off against
        float cost = P.CostPush * (1 + a.Mass) * (wet ? 1 + P.DepthK * Below(a, cell) : 1);
        if (Avail(a) < cost + P.EnergyReserve) return;
        Dissipate(a, cost);
        float push = 1.05f * Math.Min(1f, power);
        if (d < 4) { a.Vx += DX[d] * push; a.Vy += DY[d] * push; }
        else a.Vz += (d == 4 ? push : -push) * (1 - P.WaterDrag);   // about a block per push, as along the ground
        Worked(a, slot, site);
        Note(EvKind.Push);
    }

    // ---- senses ----

    // What sight found: What 1 = somebody (Detail = kinship), 2 = a wall or the map's edge (Detail = its
    // height), 0 = nothing; Dist in cells (0 when nothing).
    public struct LookResult { public int Detail, Dist, What; public LookResult(int detail, int dist, int what) { Detail = detail; Dist = dist; What = what; } }

    // Sight costs energy by range and needs light at what is seen: up to `n` (1–16) cells in direction d.
    public LookResult Look(Agent a, int cell, int d, int n)
    {
        int range = Math.Clamp(n, 1, 16);
        Dissipate(a, P.CostLook * range);
        int x = a.X, y = a.Y, h0 = (int)Level(a);
        for (int k = 1; k <= range; k++)
        {
            x = WrapX(x + DX[d]);
            y += DY[d];
            if (y < 0 || y >= H) return new LookResult(0, k, 2);
            int c = y * W + x;
            if (Light[c] < 0.12f) continue;
            int level = WalkLevel(c, h0);
            int dh = level < 0 ? 2 : level - h0;
            if (dh >= 2) { Note(EvKind.Look); return new LookResult(dh, k, 2); }
            var o = Head[c] ?? Big[c];
            if (o != null && o != a && MathF.Abs(Level(o) - Level(a)) <= 1) { Note(EvKind.Look); return new LookResult(Kinship(a, o), k, 1); }
        }
        return default;
    }

    // Which way loose molecules of kind s are richest: the direction 0–3 of the richer neighbour, 4 here.
    public int Gradient(Agent a, int cell, int s)
    {
        float best = LooseAmount(a, cell, s);
        int bd = 4;
        for (int d = 0; d < 4; d++)
        {
            int j = nb[cell * 4 + d];
            float here = LooseAmount(a, j, s);
            if (here > best) { best = here; bd = d; }
        }
        return bd;
    }

    // ---- reading local physical quantities (free: what a receptor could sense) ----

    // Loose molecules of kind s the body could take in at `cell` (its own floor, or the buried pocket it is in).
    public float OutsideAmount(Agent a, int cell, int s) => LooseAmount(a, cell, s);

    // The cell next to `cell` in direction d (0–3; the cell itself at the map's top and bottom edges).
    public int NeighbourCell(int cell, int d) => nb[cell * 4 + (d & 3)];

    // The temperature around the body (the cave air's under a roof, with the cave law), °C.
    public float AmbientTemp(Agent a, int cell) => CaveLaw ? LocalTemp(cell, a.Z) : Temp[cell];

    // How much light reaches the body (0–1: its cells, water and cover over it).
    public float LightAt(Agent a) => AgentLight(a);

    // Photons the body could catch right now: its canopy store (P.Canopy 1) or its cell's trickle; none
    // under a roof.
    public float PhotonsAt(Agent a, int cell) => InCave(a) ? 0 : CanopyLaw ? a.LightQuota : Photon[cell];

    // The ground: d = 4 the material underfoot (0 for a body swimming off the bottom), d 0–3 how many
    // blocks the neighbouring floor lies above (+) or below (−) the body's.
    public int GroundAt(Agent a, int cell, int d) =>
        d == 4 ? (a.Z > 0 && OnFloor(a) ? Mat[cell * Z + a.Z - 1] : 0) : WalkLevel(nb[cell * 4 + d], a.Z) - a.Z;

    // How many other bodies are within touching distance (the ones PickBody(n ≥ 0) chooses from).
    public int ContactCount(Agent a) => Candidates(a);

    // ---- membrane ----

    public void Intake(Agent a, int cell, int s)
    {
        // Uptake through the membrane: up to one unit per call, partial amounts accumulate. A big body
        // drinks from whichever of its cells has the most.
        // Taking in costs more the more crammed the body already is.
        float packing = a.Packing;
        Dissipate(a, P.CostIntake * (1 + packing * packing));
        if (EnergyProbe != null) { EpAdd(EnergyEconomyProbe.IntakeCalls, 1); EpAdd(EnergyEconomyProbe.IntakeCost, P.CostIntake * (1 + packing * packing)); }
        if (a.Cells > 1) cell = RichestFor(a, s);
        Qty m = Qty.Min(1 - a.Pend[s], LooseAmount(a, cell, s));   // the same amount leaves the floor and enters
        if (m <= 0) return;
        ChangeLoose(a, cell, s, -m);
        if (ResProbe != null) ResIntake(cell, s, m, a.Z < Height[cell]);
        a.Pend[s] += m;
        a.Mass += m.F * Chem.Mass[s];
        a.Volume += m.F * Chem.BodyVolume[s];
        if (a.Pend[s] < 1) return;
        a.Pend[s] -= 1;
        a.Mass -= Chem.Mass[s];
        a.Volume -= Chem.BodyVolume[s];
        AddMol(a, s);
        EpMol(EnergyEconomyProbe.IntakeMol, s);
        FoodProbe?.Env(a);
        PredProbe?.EnvGain(Chem.E[s], cell, Chem.Gap[s]);
        a.NIntake++;
        Note(EvKind.Intake);
        Act(a, ActEat, -1);
    }

    // Unspecific uptake: a sip of the surroundings brings in molecules in proportion to how much of
    // each there is.
    public void Drink(Agent a, int cell)
    {
        cell = FullestCell(a);
        float total = 0;
        var fb = FloorBurial(a, cell);
        for (int s = 0; s < Chemistry.S; s++) total += Loose(a, cell, fb, s);
        if (total < 0.5f)
        {
            Dissipate(a, P.CostIntake * (1 + a.Packing * a.Packing));
            if (EnergyProbe != null) { EpAdd(EnergyEconomyProbe.SoakCalls, 1); EpAdd(EnergyEconomyProbe.SoakCost, P.CostIntake * (1 + a.Packing * a.Packing)); }
            SoakAggregate(a, cell);
            return;
        }
        double r = Rng.NextDouble() * total;
        int q = 0;
        for (; q < Chemistry.S - 1 && r >= Loose(a, cell, fb, q); q++) r -= Loose(a, cell, fb, q);
        Intake(a, cell, q);
    }

    // Sufficiently weak molecular aggregates can be absorbed like loose remains.
    void SoakAggregate(Agent a, int cell)
    {
        if (!OnFloor(a)) return;   // swimming: the ground is out of reach
        int h = a.Z, v = cell * Z + h - 1;
        if (h <= 2 || Units[v] == 0) return;
        float barrier = VoxelBarrier(v);
        if (barrier > 0.5f || !TakeBite(v, barrier, P.CostIntake)) return;   // a gentle soak: little work into the face
        int s = TakeVoxelMolecule(v);
        AddMol(a, s);
        EpMol(EnergyEconomyProbe.SoakMol, s);
        NoteMined(s, Height0[cell] - h);
        a.TickMine += Chem.E[s];
        a.GainMine += Chem.E[s];
        a.NMines++;
        FoodProbe?.Env(a);
        PredProbe?.EnvGain(Chem.E[s], -1, Chem.Gap[s]);
        a.NMinedTier[0]++;
        var ctx = cur;
        if (ctx != null) ctx.Mined[0]++;
        Note(EvKind.Mine);
        Act(a, ActEat, -1);
    }

    public void Expel(Agent a, int cell, int s, int d)
    {
        Dissipate(a, P.CostExpel);
        if (a.Inv[s] == 0) return;
        Settle(a);   // law 1: costs before the molecule leaves are paid from what the body held
        RemoveMol(a, s);
        if (ResProbe != null) ResExpel(s);
        EpMol(EnergyEconomyProbe.ExpelMol, s);
        // It lands on the floor next door it can reach (level, a step up, or down into a hollow),
        // never inside a wall or in the air of a cave; against a wall it drops at the body's feet.
        int n = nb[cell * 4 + d], level = n == cell ? -1 : WalkLevel(n, a.Z);
        if (level >= 0) ChangeLooseAt(n, level, s, 1f);
        else ChangeLoose(a, cell, s, 1f);
        float k = Chem.Mass[s] * P.Recoil / (1f + 0.05f * a.Mass);
        a.Vx -= DX[d] * k;
        a.Vy -= DY[d] * k;
        a.NExpel++;
        Note(EvKind.Expel);
        Act(a, ActExpel, d);
        if (Rng.NextDouble() < 0.2) AddFlash(a.X, a.Y, FlashExpel, d, s);   // only some are drawn, or they would fill the screen
    }

    // ---- other bodies ----

    // Alarm: if somebody in this cell was attacked within the last few ticks, the attacker becomes the
    // target and the number of ticks since is returned (-1 if all is quiet). Whether to strike back,
    // flee or ignore it is up to the controller — a crowd that strikes back together is a crowd that wins.
    readonly Agent[] lastAttacker;
    readonly long[] lastAttack;

    public int Alarm(Agent a, int cell)
    {
        var x = lastAttacker[cell];
        long ago = Tick - lastAttack[cell];
        if (x == null || x.Dead || x == a || ago > P.AlarmTicks || !Near(a, x)) return -1;
        a.Target = x;
        return (int)ago;
    }

    // Strike another body and tear molecules out of it. The strike is work the attacker pays (its
    // argument × P.StrikeUnit, as much as it can afford). That work goes into the victim's hold on its
    // molecules (P.BodyHold, World.Predation): each molecule torn out takes TearWork of it — a body of
    // strong, well-fitting molecules gives up fewer per blow, a protein for the victim's main molecule
    // lowers the barrier as it does in rock — and that work ends as heat where they are. What is left of
    // the strike goes into the victim's body as heat (HeatHeld; it warms by the same heat capacity as
    // reaction heat, so a big body warms less): a hot body's proteins unfold (LiveBody), nothing else
    // about injury is built in. Torn molecules carry their share of the victim's store (P.TornStore).
    // A body that falls apart dies.
    public void Attack(Agent a, int cell, int p)
    {
        float power = (float)Math.Min(Math.Max(0, p) * P.StrikeUnit, Math.Max(0, Avail(a) - P.EnergyReserve));   // as hard as it can afford
        if (power <= 0) return;
        var t = Partner(a, cell);
        Dissipate(a, P.CostSocial);
        if (EnergyProbe != null) { EpAdd(EnergyEconomyProbe.AttackCalls, 1); EpAdd(EnergyEconomyProbe.AttackCost, P.CostSocial + (t != null ? power : 0)); }
        if (t == null) return;

        int tc = t.Y * W + t.X;
        lastAttacker[tc] = a;
        lastAttack[tc] = Tick;
        var pred = PredProbe;   // observation only
        float tear = TearWork(BodyBarrier(a, t, out float coh)) * P.BodyHold;
        int units = (int)Math.Min(t.InvTotal, power / tear + Rng.NextDouble());
        if (t.Bio != null && (t.BioN == 0 || t.Bio[(t.BioN - 1) % t.Bio.Length] is not { Kind: BioKind.Killed } last || last.Other != a.Id))
            BioNote(t, Tick, BioKind.Killed, a.Id, units);   // attacked (once per attacker in a row)
        var food = FoodProbe;   // observation only
        int torn = 0;
        for (int k = 0; k < units && t.InvTotal > 0; k++)
        {
            int s = RandomMol(t);
            RemoveMol(t, s);
            AddMol(a, s);
            EpMol(EnergyEconomyProbe.TornMol, s);
            if (MatterLaw && EnergyProbe != null) EpAdd(EnergyEconomyProbe.TornStore, Chem.Gap[s]);   // law 1: the charge it carries
            food?.Prey(a, t, Chem.E[s]);
            pred?.Tear(a, t, s, Chem);
            torn++;
        }
        if (torn > 0 && !MatterLaw)   // law 1: molecules carry their own charge, no store travels with them
        {
            double before = EnergyProbe != null ? a.Energy : 0;
            CarryStore(t, a, torn);
            if (EnergyProbe != null) EpAdd(EnergyEconomyProbe.TornStore, a.Energy - before);
        }
        float broke = torn > 0 ? Math.Min(power, torn * tear) : 0;   // the last molecule may come out on less than its full work
        Dissipate(a, broke);
        StrikeHeat(a, t, power - broke);
        pred?.Attack(a, t, power + P.CostSocial, power - broke, coh, torn);
        a.NAttacks++;
        a.TickAttack += 1;
        Note(EvKind.Attack);
        Act(a, ActAttack, tc == cell ? -1 : Neighbour4(cell, tc));
        AddFlash(t.X, t.Y, FlashAttack, tc == cell ? -1 : Neighbour4(cell, tc));
        if (BodyUnits(t) < P.MinBody || (!MatterLaw && t.Energy <= 0))
        {
            pred?.Kill(a, t, tc, Chem);
            EpKilled(t);
            Die(t, tc, CauseKilled);
            food?.Kill(a, t);
            a.NKills++;
            if (BitOperations.IsPow2(a.NKills)) BioNote(a, Tick, BioKind.Kill, t.Id, a.NKills);
            if (units > 0) Propose(EvType.FirstPredator, Chronicle.OnceKey(EvType.FirstPredator), a, t, units);
            Note(EvKind.Kill);
            AddFlash(t.X, t.Y, FlashKill);
        }
    }

    // Pull one molecule of a kind out of another body. The pull is work against the
    // body's hold on it (the same as tearing it out in an attack), paid whether or not it lets go; a
    // linked partner does not resist, an unlinked one keeps it with a chance by mass.
    public void Take(Agent a, int cell, int s)
    {
        Dissipate(a, P.CostSocial);
        var t = Partner(a, cell);
        if (t == null || t.Inv[s] == 0) return;
        float work = TearWork(BodyBarrier(a, t, out _)) * P.BodyHold;
        if (Avail(a) < work + P.EnergyReserve) return;   // it cannot pull that hard
        Dissipate(a, work);
        if (EnergyProbe != null) { EpAdd(EnergyEconomyProbe.TakeCalls, 1); EpAdd(EnergyEconomyProbe.TakeCost, P.CostSocial + work); }
        var pred = PredProbe;   // observation only
        bool got = a.Links.Contains(t) || Rng.NextDouble() <= (a.Mass + 1) / (a.Mass + t.Mass + 2);
        pred?.Take(a, t, s, P.CostSocial + work, got, Chem);
        if (!got) return;
        RemoveMol(t, s);
        AddMol(a, s);
        EpMol(EnergyEconomyProbe.TakenMol, s);
        if (MatterLaw) { if (EnergyProbe != null) EpAdd(EnergyEconomyProbe.TakenStore, Chem.Gap[s]); }   // law 1: its own charge
        else
        {
            double had = EnergyProbe != null ? a.Energy : 0;
            CarryStore(t, a, 1);
            if (EnergyProbe != null) EpAdd(EnergyEconomyProbe.TakenStore, a.Energy - had);
        }
        FoodProbe?.Prey(a, t, Chem.E[s]);
        a.NTakes++;
        if (BitOperations.IsPow2(a.NTakes)) BioNote(a, Tick, BioKind.Theft, t.Id, a.NTakes);
        Note(EvKind.Take);
        Act(a, ActSocial, -1);
    }

    public void Give(Agent a, int cell, int s)
    {
        Dissipate(a, P.CostSocial * 0.5f);
        var t = Partner(a, cell);
        if (t == null || a.Inv[s] == 0) return;
        Settle(a);
        RemoveMol(a, s);
        AddMol(t, s);
        a.NGives++;
        if (BitOperations.IsPow2(a.NGives)) BioNote(a, Tick, BioKind.Gift, t.Id, a.NGives);
        Note(EvKind.Give);
        Act(a, ActSocial, -1);
    }

    public void Share(Agent a, int cell, int amount)
    {
        Dissipate(a, P.CostSocial);
        var t = Partner(a, cell);
        float e = (float)Math.Min(Math.Clamp(amount, 0, 255) * P.ShareUnit, Avail(a) - P.EnergyReserve);
        if (t == null || e <= 0) return;
        if (MatterLaw)
        {
            Settle(a);
            e = (float)HandCharge(a, t, e);   // law 1: charged molecules go over (World.Charge)
            if (e <= 0) return;
        }
        else
        {
            a.Energy -= e;
            t.Energy += e;
        }
        t.TickGot += e;
        t.LifeGot += e;
        Note(EvKind.Share);
        Act(a, ActSocial, -1);
    }

    // A link forms only when both want it within a few ticks; calling it on a linked partner lets go.
    public void LinkOp(Agent a, int cell)
    {
        Dissipate(a, P.CostSocial);
        var t = Partner(a, cell);
        if (t == null) return;
        if (a.Links.Remove(t)) { t.Links.Remove(a); return; }
        a.LinkWant = t;
        a.LinkTick = Tick;
        if (t.LinkWant != a || Tick - t.LinkTick > P.HandshakeTicks) return;   // any number of links; each costs upkeep (P.CostLink)
        a.Links.Add(t);
        t.Links.Add(a);
        Note(EvKind.Link);
        AddFlash(t.X, t.Y, FlashLink);
    }

    // Copy a piece of one's own genome into the partner's, right where its program is running
    // (len > 0), or pull a piece of the partner's genome into one's own (len < 0). Nothing marks the
    // inserted code as foreign: a host can only notice it by inspecting its own genome and cut it out.
    public void Inject(Agent a, int cell, int start, int len)
    {
        if (len == 0) return;
        bool pull = len < 0;
        var t = Partner(a, cell);
        if (t == null) { Dissipate(a, P.CostSocial); return; }
        var (src, dst) = pull ? (t, a) : (a, t);
        bool foreignCode = t.Model != a.Model;   // a genome in another model's code: paid for, never taken in
        len = Math.Min(Math.Abs(len), src.G.Length);   // at most a whole genome; every byte costs
        Dissipate(a, P.CostInjectBase + P.CostInjectByte * len);
        if (!pull) PredProbe?.Inject(a, t);
        if (dst.G.Length + len > Genome.MaxLen || foreignCode) return;
        if (Rng.NextDouble() > (a.Mass + 1) / (a.Mass + t.Mass + 2)) return;   // the host keeps it out with a chance by mass, as in take
        var g = src.G;
        int n = g.Length;
        start = Mod(start, n);
        var tg = dst.G;
        var life = LifeModels.Get(dst.Model);
        int at = Math.Min(life.SpliceSite(dst), tg.Length);
        var ng = new byte[tg.Length + len];
        var np = new byte[tg.Length + len];
        Array.Copy(tg, 0, ng, 0, at);
        Array.Copy(dst.Prot, 0, np, 0, at);
        for (int k = 0; k < len; k++) ng[at + k] = g[(start + k) % n];
        Array.Copy(tg, at, ng, at + len, tg.Length - at);
        Array.Copy(dst.Prot, at, np, at + len, tg.Length - at);
        life.BeforeSplice(dst, at, 0, len);
        dst.SetGenome(ng, np);
        a.NInjects++;
        if (!pull)
        {
            ForeignCode(a, t, ng.AsSpan(at, len));   // observation only: following a parasite's code
            t.NInfected++;
            if (a.Lineage != t.Lineage)
            {
                if (t.Bio != null && t.InfectedBy != a.Lineage) BioNote(t, Tick, BioKind.Infected, a.Lineage);
                t.InfectedBy = a.Lineage;   // observation only (the chronicle's "first parasite")
            }
        }
        Note(EvKind.Inject);
        AddFlash(t.X, t.Y, FlashInject, t.Y * W + t.X == cell ? -1 : Neighbour4(cell, t.Y * W + t.X));
    }

    // Excise a piece of one's own genome. Expensive on purpose.
    public void Cut(Agent a, int start, int len)
    {
        int n = a.G.Length;
        if (len <= 0) return;
        start = Mod(start, n);
        len = Math.Min(len, n - start);   // at most to the end of the genome; every byte costs
        Dissipate(a, P.CostCutBase + P.CostCutByte * len);
        if (n - len < Genome.MinLen) return;
        var ng = new byte[n - len];
        var np = new byte[n - len];
        Array.Copy(a.G, 0, ng, 0, start);
        Array.Copy(a.G, start + len, ng, start, n - start - len);
        Array.Copy(a.Prot, 0, np, 0, start);
        Array.Copy(a.Prot, start + len, np, start, n - start - len);
        LifeModels.Get(a.Model).BeforeSplice(a, start, len, 0);
        a.SetGenome(ng, np);
        if (a.Foreign != null && ng.AsSpan().IndexOf(a.Foreign) < 0) { a.Foreign = null; a.ForeignFrom = 0; }   // the parasite's code is cut out
        a.NCuts++;
        Note(EvKind.Cut);
    }

    // ---- the ground ----

    // How much of a material's barrier the body's proteins take away: a splitting protein aimed at the
    // material's dominant molecular bond, working near its best temperature.
    public float Catalysis(Agent a, byte m, out int slot)
    {
        slot = -1;
        int key = Chem.MatKey[m];
        if (key < 0) return 0;
        float best = 0;
        for (int k = 0; k < a.EnzN; k++)
        {
            ref var e = ref a.Enz[k];
            if (e.Kind != Enzyme.Split || e.A != key) continue;
            float d = (a.Tb - e.Topt) / P.EnzWidth;
            float v = e.Amount * e.Eff * MathF.Exp(-d * d);
            if (v > best) { best = v; slot = k; }
        }
        return P.CatalysisMax * Math.Min(1f, best);
    }

    // Feeding on the ground: work a molecule out of a block — the top one underfoot (d = 4), or in a
    // neighbouring column the one level with the body (into a wall: the roof may hold or fail) or its
    // top if it is lower — at most one step down, as far as the body could walk; a pit's bottom is
    // out of reach. Every try costs effort, more the harder and higher-grade the rock, and the
    // effort accumulates in the face (TakeBite): a firm lattice needs a great deal of it — many tries,
    // or many gnawers — and unaided hard rock costs more energy than it gives, while a matching protein
    // makes it routine. A block eaten through is gone.
    public void Mine(Agent a, int cell, int site, int d)
    {
        int c = d == 4 ? cell : nb[cell * 4 + d];
        if (c == cell && d != 4) return;
        // A swimmer acts at the block of water it is in: it reaches only rock level with it.
        int at = ActLevel(a), z = d == 4 ? at - 1 : IsSolid(c, at) ? at : WalkLevel(c, at) - 1;
        int v = c * Z + z;
        byte m = z >= 0 ? Mat[v] : Chemistry.Air;
        int tier = Chem.MatTier[m];
        // Out of reach: a neighbouring floor more than one step down (as far as a body could walk).
        if (z < 0 || z < at - 2 || (!OnFloor(a) && z < at - 1) || Units[v] == 0 || tier > 4)
        {
            Dissipate(a, P.CostMine);
            if (EnergyProbe != null) { EpAdd(EnergyEconomyProbe.MineTries, 1); EpAdd(EnergyEconomyProbe.MineCost, P.CostMine); }
            return;
        }
        float cat = Catalysis(a, m, out int slot);
        // The effort of a try grows with how hard and high-grade the rock is; solid molecules in the
        // body (teeth, a shell) do part of it, so the same effort costs the body less energy.
        float effort = P.CostMine * (1 + Chem.MatHard[m]) * (1 + 0.5f * tier);
        float work = P.CostMine * (1 + Chem.MatHard[m] / (1 + 0.25f * a.Solids)) * (1 + 0.5f * tier);
        if (Avail(a) < work + P.EnergyReserve) return;
        Dissipate(a, work);
        a.LifeMineCost += work;
        if (EnergyProbe != null) { EpAdd(EnergyEconomyProbe.MineTries, 1); EpAdd(EnergyEconomyProbe.MineCost, work); }
        // The work goes into the face and stays there; a molecule comes out once enough has gathered
        // (from this body, others gnawing here, and time).
        if (!TakeBite(v, VoxelBarrier(v) * (1 - cat), effort)) return;
        int s = TakeVoxelMolecule(v);
        AddMol(a, s);
        EpMol(EnergyEconomyProbe.MineMol, s);
        NoteMined(s, Height0[c] - 1 - z);
        a.TickMine += Chem.E[s];
        a.GainMine += Chem.E[s];
        a.NMines++;
        FoodProbe?.Env(a);
        PredProbe?.EnvGain(Chem.E[s], -1, Chem.Gap[s]);
        a.NMinedTier[tier]++;
        var ctx = cur;
        if (ctx != null) { ctx.Mined[tier]++; if (cat >= 0.5f) ctx.MinedCat[tier]++; }
        if (cat > 0) Worked(a, slot, site);
        if (cat >= 0.5f && tier >= 2) a.NCatMined++;
        // A discovery is a habit, not a fluke: ten molecules of hard rock broken with a protein's help.
        if (cat >= 0.5f && tier >= 2 && a.NCatMined >= 10 && Firsts[m] == null)
        {
            var found = new Discovery { Tick = Tick, Lineage = a.Lineage, AgentId = a.Id, Mat = m };
            if (cur != null) cur.Firsts.Add(found);   // merged in stripe order after the agent phase
            else Firsts[m] = found;
        }
        Note(EvKind.Mine);
        Act(a, ActEat, d);
    }

    // Earthmoving: lift the top block of a column (d = 4: the one underfoot) and drop it on the lowest
    // column next to it. Needs something solid in the body to dig with; nothing is eaten.
    public void Dig(Agent a, int cell, int d)
    {
        int c = d == 4 ? cell : nb[cell * 4 + d];
        if (c == cell && d != 4) return;
        int h = Height[c];
        if (h <= 2) return;
        int at = ActLevel(a), z = d == 4 ? at - 1 : IsSolid(c, at) ? at : WalkLevel(c, at) - 1;
        if (z < 2 || z < at - 2 || (!OnFloor(a) && z < at - 1)) return;   // a floor beyond one step down is out of reach
        int v = c * Z + z;
        byte m = Mat[v];
        if (m < 2 || Chem.MatTier[m] > 4 || a.Solids == 0) return;
        // Like an ant: the block is carried to a neighbouring floor — level with it or one below if
        // there is one (never hurled further down), otherwise up onto the lowest rim, a spoil heap
        // around the hole. Working it loose costs by hardness; carrying it costs its weight for every
        // level it is lifted. So a body can sink a shaft or drive a tunnel, at a price.
        int to = -1, lift = 0, bestScore = int.MaxValue;
        for (int k = 0; k < 4; k++)
        {
            int j = nb[c * 4 + k];
            if (j == c || j == cell || Height[j] >= Z - 2) continue;
            int up = Height[j] - z;
            if (up < -1) continue;
            int score = up >= 0 ? up * 2 : 1;   // level, then one lower, then the least lifting
            if (score < bestScore) { bestScore = score; to = j; lift = Math.Max(0, up); }
        }
        if (to < 0) return;
        float cost = P.CostDig * Chem.MatHard[m] / (1 + 0.25f * a.Solids) + VoxelMass(v) * P.Gravity * (1 + lift);
        if (Avail(a) < cost + P.EnergyReserve) return;
        Dissipate(a, cost);
        if (EnergyProbe != null) { EpAdd(EnergyEconomyProbe.DigCalls, 1); EpAdd(EnergyEconomyProbe.DigCost, cost); }
        int w = to * Z + Height[to];
        impactSource = 3;
        DropVoxel(v, w);
        Repose(to);
        a.NDigs++;
        Note(EvKind.Dig);
        Act(a, ActDig, d);
        AddFlash(c % W, c / W, FlashDig);
    }

    // Building: lay all the solid molecules of one kind the body carries onto a floor (its own or a
    // neighbour's). They fill the block under that floor first; a new block starts only when it is full
    // by volume — a whole block takes hundreds of molecules, so building is slow, patient work.
    public void Pile(Agent a, int cell, int d)
    {
        int c = d == 4 ? cell : nb[cell * 4 + d];
        if (c == cell && d != 4) return;
        int level = c == cell ? a.Z : WalkLevel(c, a.Z);
        if (level < 3 || level >= Z - 1) return;
        int best = -1;
        foreach (int s in Chem.Solids)
            if (a.Inv[s] >= P.PileUnits && (best < 0 || a.Inv[s] > a.Inv[best])) best = s;
        if (best < 0 || Avail(a) < P.CostPile + P.EnergyReserve) return;
        Dissipate(a, P.CostPile);
        Settle(a);
        var add = new ushort[Chemistry.S];
        int n = Math.Min(a.Inv[best], ushort.MaxValue);
        add[best] = (ushort)n;
        for (int k = 0; k < n; k++) RemoveMol(a, best);
        Deposit(c, level, add, 80, a);
        a.NPiles++;
        Note(EvKind.Pile);
        Act(a, ActDig, d);
        AddFlash(c % W, c / W, FlashPile);
    }

    // Excretion: four molecules of the body are laid onto a floor — a thin skin on the block beneath.
    public void Grow(Agent a, int cell, int d)
    {
        int c = d == 4 ? cell : nb[cell * 4 + d];
        if (c == cell && d != 4) return;
        int level = c == cell ? a.Z : WalkLevel(c, a.Z);
        if (level < 3 || level >= Z - 1) return;
        if (a.InvTotal < 4 + P.MinBody || Avail(a) < P.CostGrow + P.EnergyReserve) return;
        Settle(a);
        var add = new ushort[Chemistry.S];
        for (int k = 0; k < 4; k++) { int s = RandomMol(a); add[s]++; RemoveMol(a, s); }
        Dissipate(a, P.CostGrow);
        Deposit(c, level, add, 25, a);
        a.NGrows++; Note(EvKind.Grow);
        Act(a, ActDig, d); AddFlash(c % W, c / W, FlashGrow);
    }
}
