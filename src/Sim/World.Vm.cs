using System;
using System.Numerics;

namespace Primordium;

// Life model 1 (LifeModels.Vm, VmModel in LifeModels.cs): the genome is a program for a stack machine.
// Its instructions only read the stack, memory and the body, or call the body physics (World.BodyOps.cs)
// — the same primitives, at the same costs, as any other life model. Nothing here is a ready-made
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

    int Conc(Agent a, int s, int cell) => (int)MathF.Min(255f, OutsideAmount(a, cell, s) * 4f);

    // `enzyme`: the protein encoded by the three bytes after the instruction (MakeProtein pays for it).
    void Express(Agent a, int ip, byte[] g, int n)
    {
        if (!CanMakeProtein(a)) return;
        MakeProtein(a, Genome.Decode(g[(ip + 1) % n], g[(ip + 2) % n], g[(ip + 3) % n]), ip);
    }

    // One tick of thinking (the model's Think): instructions until the cycles run out or `yield`.
    internal void Exec(Agent a, int cell)
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
                case Genome.Energy: Push(a, (int)Avail(a)); break;
                case Genome.Age: Push(a, a.Age >> 6); break;
                case Genome.Have: Push(a, a.Inv[Chemistry.Spec(Pop(a))]); break;
                case Genome.EnzymeOp: Express(a, ip, g, n); a.Ip = ip + 4; break;
                case Genome.MassOp: Push(a, a.InvTotal); break;
                case Genome.Temp: Push(a, imm >= 2 ? (int)a.Tb : (int)AmbientTemp(a, cell)); break;
                // light.1 is `uv` with solar flares on (World.Sky: the sun's activity and flares at the body,
                // information only); with them off it reads the light, as before.
                case Genome.LightOp: Push(a, imm >= 2 ? (int)(PhotonsAt(a, cell) * 100) : imm == 1 && FlareLaw ? UvSense(a) : (int)(LightAt(a) * 100)); break;
                case Genome.Sense: Push(a, Conc(a, Chemistry.Spec(Pop(a)), cell)); break;
                case Genome.Sensed: x = Pop(a); y = Pop(a); Push(a, Conc(a, Chemistry.Spec(y), NeighbourCell(cell, x))); break;
                case Genome.Look: { x = Pop(a); y = Pop(a); var seen = Look(a, cell, x & 3, y); Push(a, seen.Detail); Push(a, seen.Dist); Push(a, seen.What); break; }
                case Genome.Pick:
                    if (imm >= 2) Push(a, ContactCount(a));
                    else Push(a, PickBody(a, cell, Pop(a)) != null ? 1 : 0);
                    break;
                case Genome.Feel:
                    if (imm >= 2) { Push(a, Alarm(a, cell)); break; }   // imm 2–3: was anybody here attacked?
                    { var o = Partner(a, cell); Push(a, o == null ? -1 : (int)Avail(o)); }
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
                    if (imm >= 2) { DigestAny(a, ip); break; }   // imm 2–3: digest something
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
                    // Underfoot: the material there — nothing (0) for a body swimming off the bottom.
                    Push(a, GroundAt(a, cell, Mod(Pop(a), 5)));
                    break;
                case Genome.Smell: Push(a, Gradient(a, cell, Chemistry.Spec(Pop(a)))); break;
            }
            if (ProfileOps && cur != null) cur.OpTicks[Genome.Slot(b)] += System.Diagnostics.Stopwatch.GetTimestamp() - opStart;
        }
    }
}
