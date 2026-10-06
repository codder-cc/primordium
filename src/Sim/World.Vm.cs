using System;
using System.Numerics;

namespace Primordium;

// The stack machine and the physical primitives it can trigger. Nothing here is a ready-made
// behaviour: reactions need molecules in the body and go fast only with a matching protein, which
// only exists if the genome made it; light has to be caught from the cell's shared trickle; motion
// needs a motor protein or thrown-away mass; every social act targets whoever the code picked.
public sealed partial class World
{
    static int Cl(int v) => v > 32767 ? 32767 : v < -32767 ? -32767 : v;
    static int Mod(int v, int m) => ((v % m) + m) % m;

    static void Push(Agent a, int v)
    {
        if (a.Sp == P.StackSize) { Array.Copy(a.Stack, 1, a.Stack, 0, P.StackSize - 1); a.Sp--; }
        a.Stack[a.Sp++] = Cl(v);
    }

    static int Pop(Agent a) => a.Sp > 0 ? a.Stack[--a.Sp] : 0;

    static void JumpTo(Agent a, int ip, int label)
    {
        int t = Genome.NextLabel(a.Labels, ip, label);
        if (t >= 0) a.Ip = t + 1;
    }

    int Conc(Agent a, int s, int cell) => (int)MathF.Min(255f, LooseAmount(a, cell, s) * 4f);

    void Exec(Agent a, int cell)
    {
        // Chemistry — and thinking with it — slows down in a cold body.
        float cold = Math.Clamp(0.6f + a.Tb / 50f, 0.4f, 1.2f);
        int cycles = Math.Clamp((int)(P.BaseCycles * cold), 1, P.MaxCycles);
        a.LastCycles = cycles;
        for (int c = 0; c < cycles; c++)
        {
            if (a.Dead) { a.LastCycles = c; return; }
            cell = a.Y * W + a.X;   // an instruction may have pushed the body to another cell
            var g = a.G;
            int n = g.Length;
            if (a.Ip >= n || a.Ip < 0) a.Ip = 0;
            int ip = a.Ip, b = g[ip], op = b & 63, imm = b >> 6, x, y;
            a.Ip = ip + 1;
            Dissipate(a, P.CostInstr);
            a.OpCount[Genome.Slot(b)]++;
            long opStart = ProfileOps ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            switch (op)
            {
                case Genome.Nop: if (imm >= 2) Motor(a, ip, imm + 2); break;   // imm 2–3: a stroke up / down (only in water)
                case Genome.Push: Push(a, imm); break;
                case Genome.Lit: Push(a, g[(ip + 1) % n]); a.Ip = ip + 2; break;
                case Genome.Dup: x = Pop(a); Push(a, x); Push(a, x); break;
                case Genome.Drop: Pop(a); break;
                case Genome.Swap: y = Pop(a); x = Pop(a); Push(a, y); Push(a, x); break;
                case Genome.Over: y = Pop(a); x = Pop(a); Push(a, x); Push(a, y); Push(a, x); break;
                case Genome.Rot: { int z = Pop(a); y = Pop(a); x = Pop(a); Push(a, y); Push(a, z); Push(a, x); break; }
                case Genome.Add: y = Pop(a); x = Pop(a); Push(a, x + y); break;
                case Genome.Sub: y = Pop(a); x = Pop(a); Push(a, x - y); break;
                case Genome.Mul: y = Pop(a); x = Pop(a); Push(a, (int)Math.Clamp((long)x * y, -32767, 32767)); break;
                case Genome.Div: y = Pop(a); x = Pop(a); Push(a, y == 0 ? 0 : x / y); break;
                case Genome.Mod: y = Pop(a); x = Pop(a); Push(a, y == 0 ? 0 : Mod(x, y)); break;
                case Genome.Neg: Push(a, -Pop(a)); break;
                case Genome.Inc: Push(a, Pop(a) + 1); break;
                case Genome.Dec: Push(a, Pop(a) - 1); break;
                case Genome.Lt: y = Pop(a); x = Pop(a); Push(a, x < y ? 1 : 0); break;
                case Genome.Eq: y = Pop(a); x = Pop(a); Push(a, x == y ? 1 : 0); break;
                case Genome.Rand: Push(a, Rng.Next(256)); break;
                case Genome.Label: break;
                case Genome.Jmp: JumpTo(a, ip, imm); break;
                case Genome.Jz: if (Pop(a) == 0) JumpTo(a, ip, imm); break;
                case Genome.Jnz: if (Pop(a) != 0) JumpTo(a, ip, imm); break;
                case Genome.Call:
                    {
                        int t = Genome.NextLabel(a.Labels, ip, imm);
                        if (t < 0) break;
                        if (a.Cp == P.CallDepth) { Array.Copy(a.Calls, 1, a.Calls, 0, P.CallDepth - 1); a.Cp--; }
                        a.Calls[a.Cp++] = a.Ip;
                        a.Ip = t + 1;
                        break;
                    }
                case Genome.Ret: if (a.Cp > 0) a.Ip = a.Calls[--a.Cp]; break;
                case Genome.Skipz: if (Pop(a) == 0) a.Ip++; break;
                case Genome.Yield: c = cycles; break;
                case Genome.Load: Push(a, a.Mem[Mod(Pop(a), P.MemSize)]); break;
                case Genome.Store: x = Pop(a); y = Pop(a); a.Mem[Mod(x, P.MemSize)] = y; break;
                case Genome.Energy: Push(a, (int)a.Energy); break;
                case Genome.Age: Push(a, a.Age >> 6); break;
                case Genome.Have: Push(a, a.Inv[Chemistry.Spec(Pop(a))]); break;
                case Genome.EnzymeOp: Express(a, ip, g, n); a.Ip = ip + 4; break;
                case Genome.MassOp: Push(a, a.InvTotal); break;
                case Genome.Temp: Push(a, imm >= 2 ? (int)a.Tb : (int)Temp[cell]); break;
                case Genome.LightOp: Push(a, imm >= 2 ? (InCave(a) ? 0 : (int)(Photon[cell] * 100)) : (int)(AgentLight(a) * 100)); break;
                case Genome.Sense: Push(a, Conc(a, Chemistry.Spec(Pop(a)), cell)); break;
                case Genome.Sensed: x = Pop(a); y = Pop(a); Push(a, Conc(a, Chemistry.Spec(y), nb[cell * 4 + (x & 3)])); break;
                case Genome.Look: x = Pop(a); y = Pop(a); Look(a, cell, x & 3, y); break;
                case Genome.Pick:
                    if (imm >= 2) Push(a, Candidates(a));
                    else PickOp(a, cell, Pop(a));
                    break;
                case Genome.Feel:
                    if (imm >= 2) { Hurt(a, cell); break; }   // imm 2–3: was anybody here attacked?
                    { var o = Partner(a, cell); Push(a, o == null ? -1 : (int)o.Energy); }
                    break;
                case Genome.Kin: { var o = Partner(a, cell); Push(a, o == null ? -1 : Kinship(a, o)); break; }
                case Genome.NGene: { x = Pop(a); var o = Partner(a, cell); Push(a, o == null ? -1 : o.G[Mod(x, o.G.Length)]); break; }
                case Genome.Gene: Push(a, g[Mod(Pop(a), n)]); break;
                case Genome.GLen: Push(a, n); break;
                case Genome.Listen: { var o = Partner(a, cell); Push(a, o == null ? 0 : o.Signal); break; }
                case Genome.Emit: a.Signal = Pop(a); break;
                case Genome.Intake:
                    if (imm >= 2) Drink(a, cell);   // imm 2–3: take in whatever is around (or soak up organic ground)
                    else Intake(a, cell, Chemistry.Spec(Pop(a)));
                    break;
                case Genome.Expel:
                    if (imm >= 2) { Motor(a, ip, Pop(a) & 3); break; }
                    x = Pop(a); y = Pop(a); Expel(a, cell, Chemistry.Spec(y), x & 3);
                    break;
                case Genome.Bind:
                    if (imm >= 2) { if (a.InvTotal > 0) SplitMol(a, ip, RandomMol(a)); break; }   // imm 2–3: digest something
                    y = Pop(a); x = Pop(a); Bind(a, ip, Chemistry.Spec(x), Chemistry.Spec(y));
                    break;
                case Genome.Split:
                    if (imm >= 2) Photo(a, cell, ip, Chemistry.Spec(Pop(a)));
                    else SplitMol(a, ip, Chemistry.Spec(Pop(a)));
                    break;
                case Genome.Divide:
                    if (imm >= 2) { Mate(a, cell); break; }
                    x = Pop(a); y = Pop(a); Divide(a, cell, y, Mod(x, 5));
                    break;
                case Genome.Mine: Mine(a, cell, ip, imm >= 2 ? Pop(a) & 3 : 4); break;   // imm 2–3: gnaw a neighbour's column
                case Genome.Attack: Attack(a, cell, Pop(a)); break;
                case Genome.Take: Take(a, cell, Chemistry.Spec(Pop(a))); break;
                case Genome.Give: Give(a, cell, Chemistry.Spec(Pop(a))); break;
                case Genome.Link: LinkOp(a, cell); break;
                case Genome.Share: Share(a, cell, Pop(a)); break;
                case Genome.Inject: { int len = Pop(a), st = Pop(a); Inject(a, cell, st, len); break; }
                case Genome.Cut: { int len = Pop(a), st = Pop(a); Cut(a, st, len); break; }
                case Genome.Dig: Dig(a, cell, Mod(Pop(a), 5)); break;
                case Genome.Pile:
                    if (imm >= 2) Grow(a, cell, Mod(Pop(a), 5));
                    else Pile(a, cell, Mod(Pop(a), 5));
                    break;
                case Genome.Ground:
                    x = Mod(Pop(a), 5);
                    // Underfoot: the material there — nothing (0) for a body swimming off the bottom.
                    Push(a, x == 4 ? (a.Z > 0 && OnFloor(a) ? Mat[cell * Z + a.Z - 1] : 0) : WalkLevel(nb[cell * 4 + x], a.Z) - a.Z);
                    break;
                case Genome.Smell: Push(a, Smell(a, cell, Chemistry.Spec(Pop(a)))); break;
            }
            if (ProfileOps && cur != null) cur.OpTicks[Genome.Slot(b)] += System.Diagnostics.Stopwatch.GetTimestamp() - opStart;
        }
    }

    // ---- attention ----

    // Choose whom to deal with: the n-th other body in this cell, or (n < 0) the first one in the
    // neighbouring cell -n-1. Pushes 1 if somebody was found.
    void PickOp(Agent a, int cell, int n)
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
        Push(a, a.Target != null ? 1 : 0);
    }

    // Whom a social instruction acts on: the picked body if it is still alive and near, otherwise
    // somebody random in the same cell (or nobody).
    Agent Partner(Agent a, int cell)
    {
        var t = a.Target;
        if (t != null && !t.Dead && Near(a, t)) return t;
        int n = Candidates(a);
        return n <= 0 ? null : CandidateAt(a, Rng.Next(n));
    }

    // ---- proteins and reactions ----

    // Make one unit of the protein encoded by the three bytes after the instruction. Costs energy and
    // one molecule of the body as material.
    void Express(Agent a, int ip, byte[] g, int n)
    {
        if (a.Energy < P.CostExpress + 1 || a.InvTotal <= P.MinBody) return;
        var spec = Genome.Decode(g[(ip + 1) % n], g[(ip + 2) % n], g[(ip + 3) % n]);
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
        RemoveMol(a, m);
        a.Mass += Chem.Mass[m]; // folded substrate remains physically inside the body
        a.Volume += Chem.Volume[m];
        Dissipate(a, P.CostExpress);
        
        a.NExpress++; Note(EvKind.Express);
        if (slot >= 0)
        {
            a.Enz[slot].Amount += 1; a.Enz[slot].Matter += 1; a.Enz[slot].Src = ip;
            if (a.Enz[slot].Amount >= 3 && a.Enz[slot].Amount < 4 && a.Prot[ip] > 0) ChronProtein(a, a.Enz[slot]);   // kept and proven useful: news?
            return;
        }
        slot = a.EnzN++;
        if (slot == a.Enz.Length) Array.Resize(ref a.Enz, a.Enz.Length * 2);   // no limit on kinds kept
        spec.Amount = spec.Matter = 1; spec.Material = (byte)m; spec.Src = ip;
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
    static void Worked(Agent a, int slot, int ip)
    {
        var p = a.Prot;
        if (ip >= 0 && ip < p.Length && p[ip] < 255) p[ip]++;
        if (slot < 0) return;
        int src = a.Enz[slot].Src;
        if (src < 0) return;
        for (int k = 0; k < 4 && src + k < p.Length; k++)
            if (p[src + k] < 255) p[src + k]++;
    }

    // Energy from a reaction: part of what is released warms the body instead.
    void Release(Agent a, float de)
    {
        float e0 = a.Energy, h0 = a.HeatHeld;
        if (de <= 0) { a.Energy += de; a.LifeUphill -= de; Flows[FRounding] += de - ((double)a.Energy - e0); return; }
        a.Energy += de * (1 - P.HeatShare);
        // The heat share warms the body; it reaches the cells only as the body cools (LiveBody).
        a.Tb += de * P.HeatShare * 6f / (5f + a.Mass);
        a.HeatHeld += de * P.HeatShare;
        Flows[FRounding] += de - ((double)a.Energy - e0) - ((double)a.HeatHeld - h0);   // floats round (see World.Energy)
        a.GainChem += de * (1 - P.HeatShare);
        a.TickChem += de;
        a.LastMeal = a.Age;
    }

    void Bind(Agent a, int ip, int s1, int s2)
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
            if (de < 0 && a.Energy + de < 1f) break;
            RemoveMol(a, s1); RemoveMol(a, s2); AddMol(a, p);
            Release(a, de);
        }
        if (done == 0) return;
        if (de != bond) Flows[FScale] += done * ((double)de - bond);   // P.EnergyK ≠ 1 (see World.Energy)
        a.NBind += done;
        if (slot >= 0) ChronReaction(a, Enzyme.Bind, s1, s2);
        Worked(a, slot, ip);
        Note(EvKind.Bind);
        Act(a, ActEat, -1);
    }

    void SplitMol(Agent a, int ip, int s)
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
            if (de < 0 && a.Energy + de < 1f) break;
            RemoveMol(a, s); AddMol(a, x); if (y >= 0) AddMol(a, y);
            Release(a, de);
        }
        if (done == 0) return;
        if (de != bond) Flows[FScale] += done * ((double)de - bond);
        a.NSplit += done;
        if (slot >= 0) ChronReaction(a, Enzyme.Split, s, 0);
        Worked(a, slot, ip);
        Note(EvKind.Split);
        Act(a, ActEat, -1);
    }

    // Catch a photon from the cell's shared trickle and use it to lift a molecule to a richer one.
    // The energy is stored in that molecule — a body still has to break it down to use it. Light
    // excites whatever it hits: if the body has none of the molecule asked for, some other one.
    void Photo(Agent a, int cell, int ip, int s)
    {
        if (a.Inv[s] == 0 && a.InvTotal > 0) s = RandomMol(a);
        int p = Chem.PhotoUp[s];
        if (p < 0) return;
        cell = BrightestCell(a);   // only exposed parts of a large body catch surface photons
        if (cell < 0) return;
        // The cell's photons arrive at the water's surface; each block of water above the body
        // swallows some of them before they reach it.
        float reach = MathF.Exp(-P.WaterDim * Below(a, cell));
        int times = Turnovers(Chance(a, Enzyme.Photo, s, 0, out int slot)), caught = 0;
        for (int k = 0; k < times; k++)
        {
            float ph = Photon[cell];
            if (a.Inv[s] == 0 || ph < 1f) break;
            Photon[cell] = Math.Max(0, ph - 1);
            if (reach < 1 && Rng.NextDouble() >= reach) continue;
            RemoveMol(a, s);
            AddMol(a, p);
            float gain = Chem.E[p] - Chem.E[s];
            a.GainPhoto += gain;
            a.TickPhoto += gain;
            Flows[FPhoto] += gain;
            caught++;
        }
        if (caught == 0) return;
        a.NPhoto += caught;
        if (slot >= 0) ChronReaction(a, Enzyme.Photo, s, 0);
        Worked(a, slot, ip);
        Note(EvKind.Photo);
        Act(a, ActEat, -1);
    }

    // A motor protein turns energy into a push; a weak motor needs several pushes to move a body.
    // Directions 0–3 push along the ground (`push`); 4 and 5 are strokes up and down (`swim`, a
    // second meaning of nop), which do nothing — and cost nothing — out of water. In water every
    // push costs more the deeper under the surface the body is.
    void Motor(Agent a, int ip, int d)
    {
        float power = Chance(a, Enzyme.Motor, 0, 0, out int slot);
        if (slot < 0) return;
        int cell = a.Y * W + a.X;
        bool wet = InWater(cell, a.Z);
        if (d >= 4 && !wet) return;   // nothing to push off against
        float cost = P.CostPush * (1 + a.Mass) * (wet ? 1 + P.DepthK * Below(a, cell) : 1);
        if (a.Energy < cost + 1) return;
        Dissipate(a, cost);
        float push = 1.05f * Math.Min(1f, power);
        if (d < 4) { a.Vx += DX[d] * push; a.Vy += DY[d] * push; }
        else a.Vz += (d == 4 ? push : -push) * (1 - P.WaterDrag);   // about a block per push, as along the ground
        Worked(a, slot, ip);
        Note(EvKind.Push);
    }

    // ---- senses ----

    // Sight costs energy by range and needs light at what is seen. Pushes detail, distance, what
    // (1 = somebody, detail = kinship; 2 = wall or edge, detail = height).
    void Look(Agent a, int cell, int d, int n)
    {
        int range = Math.Clamp(n, 1, 16);
        Dissipate(a, P.CostLook * range);
        int x = a.X, y = a.Y, h0 = (int)Level(a);
        for (int k = 1; k <= range; k++)
        {
            x = (x + DX[d] + W) % W;
            y += DY[d];
            if (y < 0 || y >= H) { Push(a, 0); Push(a, k); Push(a, 2); return; }
            int c = y * W + x;
            if (Light[c] < 0.12f) continue;
            int level = WalkLevel(c, h0);
            int dh = level < 0 ? 2 : level - h0;
            if (dh >= 2) { Push(a, dh); Push(a, k); Push(a, 2); Note(EvKind.Look); return; }
            var o = Head[c] ?? Big[c];
            if (o != null && o != a && MathF.Abs(Level(o) - Level(a)) <= 1) { Push(a, Kinship(a, o)); Push(a, k); Push(a, 1); Note(EvKind.Look); return; }
        }
        Push(a, 0); Push(a, 0); Push(a, 0);
    }

    int Smell(Agent a, int cell, int s)
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

    // ---- membrane ----

    void Intake(Agent a, int cell, int s)
    {
        // Uptake through the membrane: up to one unit per call, partial amounts accumulate. A big body
        // drinks from whichever of its cells has the most.
        // Taking in costs more the more crammed the body already is.
        float packing = a.Packing;
        Dissipate(a, P.CostIntake * (1 + packing * packing));
        if (a.Cells > 1) cell = RichestFor(a, s);
        float m = Math.Min(1f - a.Pend[s], LooseAmount(a, cell, s));
        if (m <= 0) return;
        ChangeLoose(a, cell, s, -m);
        a.Pend[s] += m;
        a.Mass += m * Chem.Mass[s];
        a.Volume += m * Chem.BodyVolume[s];
        if (a.Pend[s] < 1f) return;
        a.Pend[s] -= 1f;
        a.Mass -= Chem.Mass[s];
        a.Volume -= Chem.BodyVolume[s];
        AddMol(a, s);
        a.NIntake++;
        Note(EvKind.Intake);
        Act(a, ActEat, -1);
    }

    // Unspecific uptake: a sip of the surroundings brings in molecules in proportion to how much of
    // each there is.
    void Drink(Agent a, int cell)
    {
        cell = FullestCell(a);
        float total = 0;
        var fb = FloorBurial(a, cell);
        for (int s = 0; s < Chemistry.S; s++) total += Loose(a, cell, fb, s);
        if (total < 0.5f) { Dissipate(a, P.CostIntake * (1 + a.Packing * a.Packing)); SoakAggregate(a, cell); return; }
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
        a.TickMine += Chem.E[s];
        a.GainMine += Chem.E[s];
        a.NMines++;
        a.NMinedTier[0]++;
        var ctx = cur;
        if (ctx != null) ctx.Mined[0]++;
        Note(EvKind.Mine);
        Act(a, ActEat, -1);
    }

    void Expel(Agent a, int cell, int s, int d)
    {
        Dissipate(a, P.CostExpel);
        if (a.Inv[s] == 0) return;
        RemoveMol(a, s);
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
    // target and the number of ticks since is pushed (-1 if all is quiet). Whether to strike back,
    // flee or ignore it is up to the code — a crowd that strikes back together is a crowd that wins.
    readonly Agent[] lastAttacker = new Agent[N];
    readonly long[] lastAttack = new long[N];

    void Hurt(Agent a, int cell)
    {
        var x = lastAttacker[cell];
        long ago = Tick - lastAttack[cell];
        if (x == null || x.Dead || x == a || ago > 16 || !Near(a, x)) { Push(a, -1); return; }
        a.Target = x;
        Push(a, (int)ago);
    }

    // How hard a body hits: its size and how well fed it is.
    static float Strength(Agent a) => MathF.Sqrt(a.Mass + 1) * (0.4f + 0.6f * Math.Clamp(a.Energy / a.Store, 0, 1));

    // Tear molecules out of another body and keep them (if there is room); its proteins get damaged
    // too. A big, well-fed body hits hard and is hard to hurt; a small one is cheaper to move and can
    // run. A body that falls apart dies.
    void Attack(Agent a, int cell, int p)
    {
        float power = Math.Min(Math.Max(0, p) * 0.1f, Math.Max(0, a.Energy - 1));   // as hard as it can afford
        if (power <= 0) return;
        var t = Partner(a, cell);
        if (t == null) { Dissipate(a, P.CostSocial); return; }
        Dissipate(a, power + P.CostSocial);
        
        int tc = t.Y * W + t.X;
        lastAttacker[tc] = a;
        lastAttack[tc] = Tick;
        float sa = Strength(a), st = MathF.Sqrt(t.Mass + 1);
        float dmg = power * 2 * sa / (sa + st);
        int units = (int)(dmg * 0.5f + Rng.NextDouble());
        if (t.Bio != null && (t.BioN == 0 || t.Bio[(t.BioN - 1) % t.Bio.Length] is not { Kind: BioKind.Killed } last || last.Other != a.Id))
            BioNote(t, Tick, BioKind.Killed, a.Id, units);   // attacked (once per attacker in a row)
        for (int k = 0; k < units && t.InvTotal > 0; k++)
        {
            int s = RandomMol(t);
            RemoveMol(t, s);
            AddMol(a, s);
        }
        for (int k = 0; k < t.EnzN; k++) WearProtein(t, k, Math.Max(0, 1 - 0.06f * dmg));
        Dissipate(t, dmg * 0.3f);
        a.NAttacks++;
        a.TickAttack += 1;
        Note(EvKind.Attack);
        Act(a, ActAttack, tc == cell ? -1 : Neighbour4(cell, tc));
        AddFlash(t.X, t.Y, FlashAttack, tc == cell ? -1 : Neighbour4(cell, tc));
        if (t.InvTotal < P.MinBody || t.Energy <= 0)
        {
            Die(t, tc, CauseKilled);
            a.NKills++;
            if (BitOperations.IsPow2(a.NKills)) BioNote(a, Tick, BioKind.Kill, t.Id, a.NKills);
            if (units > 0) Propose(EvType.FirstPredator, Chronicle.OnceKey(EvType.FirstPredator), a, t, units);
            Note(EvKind.Kill);
            AddFlash(t.X, t.Y, FlashKill);
        }
    }

    void Take(Agent a, int cell, int s)
    {
        Dissipate(a, P.CostSocial);
        var t = Partner(a, cell);
        if (t == null || t.Inv[s] == 0) return;
        if (!a.Links.Contains(t) && Rng.NextDouble() > (a.Mass + 1) / (a.Mass + t.Mass + 2)) return;
        RemoveMol(t, s);
        AddMol(a, s);
        a.NTakes++;
        if (BitOperations.IsPow2(a.NTakes)) BioNote(a, Tick, BioKind.Theft, t.Id, a.NTakes);
        Note(EvKind.Take);
        Act(a, ActSocial, -1);
    }

    void Give(Agent a, int cell, int s)
    {
        Dissipate(a, P.CostSocial * 0.5f);
        var t = Partner(a, cell);
        if (t == null || a.Inv[s] == 0) return;
        RemoveMol(a, s);
        AddMol(t, s);
        a.NGives++;
        if (BitOperations.IsPow2(a.NGives)) BioNote(a, Tick, BioKind.Gift, t.Id, a.NGives);
        Note(EvKind.Give);
        Act(a, ActSocial, -1);
    }

    void Share(Agent a, int cell, int amount)
    {
        Dissipate(a, P.CostSocial);
        var t = Partner(a, cell);
        float e = Math.Min(Math.Clamp(amount, 0, 255) / 8f, a.Energy - 1);
        if (t == null || e <= 0) return;
        a.Energy -= e;
        t.Energy += e;
        t.TickGot += e;
        t.LifeGot += e;
        Note(EvKind.Share);
        Act(a, ActSocial, -1);
    }

    // A link forms only when both want it within a few ticks; calling it on a linked partner lets go.
    void LinkOp(Agent a, int cell)
    {
        Dissipate(a, P.CostSocial);
        var t = Partner(a, cell);
        if (t == null) return;
        if (a.Links.Remove(t)) { t.Links.Remove(a); return; }
        a.LinkWant = t;
        a.LinkTick = Tick;
        if (t.LinkWant != a || Tick - t.LinkTick > 8) return;   // any number of links; each costs upkeep (P.CostLink)
        a.Links.Add(t);
        t.Links.Add(a);
        Note(EvKind.Link);
        AddFlash(t.X, t.Y, FlashLink);
    }

    // Copy a piece of one's own genome into the partner's, right where its program is running
    // (len > 0), or pull a piece of the partner's genome into one's own (len < 0). Nothing marks the
    // inserted code as foreign: a host can only notice it by inspecting its own genome and cut it out.
    void Inject(Agent a, int cell, int start, int len)
    {
        if (len == 0) return;
        bool pull = len < 0;
        var t = Partner(a, cell);
        if (t == null) { Dissipate(a, P.CostSocial); return; }
        var (src, dst) = pull ? (t, a) : (a, t);
        len = Math.Min(Math.Abs(len), src.G.Length);   // at most a whole genome; every byte costs
        Dissipate(a, P.CostInjectBase + P.CostInjectByte * len);
        if (dst.G.Length + len > Genome.MaxLen) return;
        if (Rng.NextDouble() > (a.Mass + 2) / (a.Mass + t.Mass + 4) * 1.5) return;
        var g = src.G;
        int n = g.Length;
        start = Mod(start, n);
        var tg = dst.G;
        int at = Math.Min(dst.Ip, tg.Length);
        var ng = new byte[tg.Length + len];
        var np = new byte[tg.Length + len];
        Array.Copy(tg, 0, ng, 0, at);
        Array.Copy(dst.Prot, 0, np, 0, at);
        for (int k = 0; k < len; k++) ng[at + k] = g[(start + k) % n];
        Array.Copy(tg, at, ng, at + len, tg.Length - at);
        Array.Copy(dst.Prot, at, np, at + len, tg.Length - at);
        dst.SetGenome(ng, np);
        a.NInjects++;
        if (!pull)
        {
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
    void Cut(Agent a, int start, int len)
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
        if (a.Ip > start) a.Ip = Math.Max(start, a.Ip - len);
        a.Cp = 0;
        a.SetGenome(ng, np);
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
    void Mine(Agent a, int cell, int ip, int d)
    {
        int c = d == 4 ? cell : nb[cell * 4 + d];
        if (c == cell && d != 4) return;
        // A swimmer acts at the block of water it is in: it reaches only rock level with it.
        int at = ActLevel(a), z = d == 4 ? at - 1 : IsSolid(c, at) ? at : WalkLevel(c, at) - 1;
        int v = c * Z + z;
        byte m = z >= 0 ? Mat[v] : Chemistry.Air;
        int tier = Chem.MatTier[m];
        // Out of reach: a neighbouring floor more than one step down (as far as a body could walk).
        if (z < 0 || z < at - 2 || (!OnFloor(a) && z < at - 1) || Units[v] == 0 || tier > 4) { Dissipate(a, P.CostMine); return; }
        float cat = Catalysis(a, m, out int slot);
        // The effort of a try grows with how hard and high-grade the rock is; solid molecules in the
        // body (teeth, a shell) do part of it, so the same effort costs the body less energy.
        float effort = P.CostMine * (1 + Chem.MatHard[m]) * (1 + 0.5f * tier);
        float work = P.CostMine * (1 + Chem.MatHard[m] / (1 + 0.25f * a.Solids)) * (1 + 0.5f * tier);
        if (a.Energy < work + 1) return;
        Dissipate(a, work);
        a.LifeMineCost += work;
        // The work goes into the face and stays there; a molecule comes out once enough has gathered
        // (from this body, others gnawing here, and time).
        if (!TakeBite(v, VoxelBarrier(v) * (1 - cat), effort)) return;
        int s = TakeVoxelMolecule(v);
        AddMol(a, s);
        a.TickMine += Chem.E[s];
        a.GainMine += Chem.E[s];
        a.NMines++;
        a.NMinedTier[tier]++;
        var ctx = cur;
        if (ctx != null) { ctx.Mined[tier]++; if (cat >= 0.5f) ctx.MinedCat[tier]++; }
        if (cat > 0) Worked(a, slot, ip);
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
    void Dig(Agent a, int cell, int d)
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
        if (a.Energy < cost + 1) return;
        Dissipate(a, cost);
        
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
    void Pile(Agent a, int cell, int d)
    {
        int c = d == 4 ? cell : nb[cell * 4 + d];
        if (c == cell && d != 4) return;
        int level = c == cell ? a.Z : WalkLevel(c, a.Z);
        if (level < 3 || level >= Z - 1) return;
        int best = -1;
        foreach (int s in Chem.Solids)
            if (a.Inv[s] >= P.PileUnits && (best < 0 || a.Inv[s] > a.Inv[best])) best = s;
        if (best < 0 || a.Energy < P.CostPile + 1) return;
        Dissipate(a, P.CostPile);
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
    void Grow(Agent a, int cell, int d)
    {
        int c = d == 4 ? cell : nb[cell * 4 + d];
        if (c == cell && d != 4) return;
        int level = c == cell ? a.Z : WalkLevel(c, a.Z);
        if (level < 3 || level >= Z - 1) return;
        if (a.InvTotal < 4 + P.MinBody || a.Energy < P.CostGrow + 1) return;
        var add = new ushort[Chemistry.S];
        for (int k = 0; k < 4; k++) { int s = RandomMol(a); add[s]++; RemoveMol(a, s); }
        Dissipate(a, P.CostGrow);
        Deposit(c, level, add, 25, a);
        a.NGrows++; Note(EvKind.Grow);
        Act(a, ActDig, d); AddFlash(c % W, c / W, FlashGrow);
    }
}
