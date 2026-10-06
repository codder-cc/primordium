using System;
using System.Collections.Generic;

namespace Primordium;

// A protein made from a gene: speeds up one reaction. Its spec comes from three genome bytes, so a
// point mutation can retarget it to another molecule, shift its best temperature or spoil it.
public struct Enzyme
{
    public const int Bind = 0, Split = 1, Photo = 2, Motor = 3;
    public byte Kind, A, B;
    public float Topt, Eff, Amount;
    public byte Material;
    public Qty Matter; // actual substrate, independent of catalytic activity (fixed point: exact)
    public int Src;   // genome position of its gene (the bytes get protected while it is useful)
}

public sealed class Agent
{
    public readonly long Id, Lineage;
    public readonly int Gen;
    public byte[] G { get; private set; }
    public byte[] Prot { get; private set; }   // per genome byte: how much it has proven useful lately
    public int[] Labels { get; private set; }
    // Genome fingerprint for kin recognition — computed on first use (most bodies never need it).
    public ulong Tag { get { if (!tagReady) { tag = Genome.SimHash(G); tagReady = true; } return tag; } }
    ulong tag;
    bool tagReady;
    public ulong Hash { get; private set; }

    public bool Designed;   // made by the player from a CreatureDesign (World.SpawnDesign), not born or self-assembled
    public int X, Y, Z; // Z is the free voxel occupied above a floor, also inside caves
    public float Vx, Vy;
    public float Lift, Vz;   // in water: how high above its floor it swims (blocks), and how fast it rises
    public float Energy, Tb;                   // Tb: body temperature, °C
    public int Age;
    public bool Dead;
    public int Cause;

    // The body is the molecules it holds.
    public readonly int[] Inv = new int[Chemistry.S];
    public readonly Qty[] Pend = new Qty[Chemistry.S];   // partly absorbed molecules (fixed point: exact)
    public int InvTotal, Unstable, Solids;
    public float Mass, Volume;   // Volume: room the body takes (its molecules' Chemistry.BodyVolume, its proteins' Volume)
    public float Density => Mass / Math.Max(1e-3f, Volume);   // against P.WaterDensity: floats or sinks

    // Proteins currently present.
    public Enzyme[] Enz = new Enzyme[4];   // grows as needed: no limit on how many proteins a body keeps
    public int EnzN;

    // VM state
    public int Ip, Sp, Cp, Signal, LastCycles;
    public readonly int[] Stack = new int[P.StackSize];
    public readonly int[] Mem = new int[P.MemSize];
    public readonly int[] Calls = new int[P.CallDepth];

    // Who it pays attention to (set by `pick`), and readiness to mate / link.
    public Agent Target, LinkWant;
    public long MateTick = -100, LinkTick = -100;
    public readonly List<Agent> Links = new(4);

    // Membership in its cell's list (many agents can share a cell).
    public Agent NextInCell, PrevInCell;

    // Appearance: a neutral heritable marker (see Looks). Copied to children with a tiny drift,
    // so branches of a family tree slowly diverge in colour and shape.
    public float Hue, Sat, Val, Sx, Sy, Sz;
    public int Shape;

    // The most notable action of the last tick it acted (World.Act*).
    public int Act, ActDir;
    public long ActTick;

    // Owned by the view: smoothed position and animation state.
    public float VX, VY, VH;
    public double SeenAt, AnimAt;
    public long SeenAct;
    public int AnimKind, AnimDir;

    // What the agent actually ended up doing (inspector, colouring, census)
    public float GainPhoto, GainChem, GainMine;
    public float TickPhoto, TickChem, TickMine, TickAttack, TickHeat;
    public float HeatHeld;   // reaction heat still in the body (it warmed Tb), shed into the cells as it cools
    public float EmaPhoto, EmaChem, EmaMine, EmaAttack;
    public int NChildren, NMates, NMoves, NAttacks, NKills, NInjects, NInfected, NCuts, NDigs, NPiles, NMines, NTakes, NGives, NGrows, NStruck, NExpress;
    public int NPhoto, NSplit, NBind, NIntake, NExpel;
    public float TickGot, EmaGot, EmaUpkeep, EmaHarm, EmaNet;    // energy received from others, spent on living, lost to climate, net
    public readonly int[] OpCount = new int[Genome.OpSlots];     // instructions it has executed, by kind
    // Lifetime ledger: where its energy came from and where it went (GainChem/GainPhoto above count
    // the whole life too). Whatever the ledger doesn't name was spent on actions.
    public float LifeStart, LifeGot, LifeKids, LifeUpkeep, LifeHarm, LifeSpill, LifeUphill;   // spill: leaked holding the store; uphill: reactions that cost energy
    public float LifeMineCost;                                   // spent tearing molecules out of rock
    public readonly int[] NMinedTier = new int[6];               // molecules torn out of rock, by its grade
    public int NCatMined;                                        // hard-rock molecules torn out with a protein's help
    public int LastMeal = -1;                                    // its age at its last energy-releasing reaction

    // Chronicle (World.Chronicle.cs): observation only, never read by the simulation itself.
    public long ParentId;            // the body that divided (or the first mate); 0 for a founder
    public long TrackedAncestor;     // the nearest tracked ancestor (a chain of them leads to the founder)
    public long InfectedBy;          // the lineage that last wrote code into it (inject from another lineage), 0 none
    public int CaveAge = -1;         // its age when it went under a roof (checked every 64 ticks), -1 in the open
    public bool Tracked;             // keeps a biography (Bio) and is fossilised when it dies
    public bool Established;         // its lineage had ≥ 10 bodies at the last survey (derived, not saved): only those make "firsts"
    public byte TrackWhy;            // Chronicle.Why* bits: why it is tracked
    public byte BioSeen;             // first protein of each kind (bits 0–3), first catalysed reaction (bit 4) noted
    public BioEntry[] Bio;           // ring of the last Chronicle.BioCap entries (only while tracked)
    public int BioN;                 // entries written in total (the newest is Bio[(BioN − 1) % cap])

    // Course of evolution (World.Evolution.cs): observation only, never read by the simulation itself.
    public ulong[] EvoComp;          // its components at birth (NeutralShadow.Components); null: not registered
    public ulong[] EvoParentComp;    // set at birth in the agent phase, used when the birth is merged
    public int[] EvoIds, EvoParentIds;   // its components' ids in World.Shadow (and its parent's, until merged)
    public int Taxon = -1;           // its node in World.Phylo
    public Agent EvoParent;          // its parent, from birth until the birth is merged (for the tree)
    public int EvoMut;               // genome bytes changed against its parent (approximate edit size)

    public Agent(long id, long lineage, int gen, byte[] g, byte[] prot = null)
    {
        Id = id; Lineage = lineage; Gen = gen;
        SetGenome(g, prot ?? new byte[g.Length]);
    }

    public void SetGenome(byte[] g, byte[] prot)
    {
        G = g;
        Prot = prot;
        Labels = Genome.Labels(g);
        tagReady = false;
        Hash = Genome.Hash(g);
        if (Ip >= g.Length) Ip = 0;
        // Enzymes whose genes moved or vanished keep working until they decay; just forget the link.
        for (int k = 0; k < EnzN; k++) if (Enz[k].Src + 3 >= g.Length) Enz[k].Src = -1;
    }

    // A big body covers several cells: Foot[0] is its own cell (X, Y), the rest around it.
    public int Cells = 1;
    public readonly int[] Foot = new int[P.MaxCells];

    // Neither is a limit — only where holding more starts to cost noticeably more (see P.HoldK, P.CostMass).
    public int Room => P.InvPerCell * Cells;                       // molecules it holds comfortably
    public float Store => P.StoreBase + P.StorePerMass * Mass;     // energy it stores comfortably
    public float Packing => InvTotal / (float)Room;                // above 1: crammed

    public static int CellsFor(float mass)
    {
        int k = 1;
        while (k < P.MaxCells && mass >= P.GrowMass * MathF.Pow(k, P.GrowPow)) k++;
        return k;
    }

    public float EnzymeTotal
    {
        get
        {
            float t = 0;
            for (int k = 0; k < EnzN; k++) t += Enz[k].Amount;
            return t;
        }
    }

    public float EnzymeOf(int kind)
    {
        float t = 0;
        for (int k = 0; k < EnzN; k++) if (Enz[k].Kind == kind) t += Enz[k].Amount;
        return t;
    }
}
