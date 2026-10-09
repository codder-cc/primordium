using System;
using System.Collections.Generic;

namespace Primordium.Model2;

// The state of a model-2 cell between ticks (Agent.ModelState; saved by ChemModel.SyncState). The body's
// molecules, place and temperature are the shared body; this is what only model 2 knows: which polymers
// its pool (Agent.Poly) is made of. Invariant (checked by the tests): the pool holds exactly the genome
// duplexes booked here, the replica copied so far and every protein copy (its sequence's letters), and
// its bond energy is their bonds plus the excited residues.
public sealed class Cell
{
    public const int Version = 1;
    public bool Assembled;          // made (a planted body assembles itself on its first tick)
    public int Genomes;             // duplexes held: 0 (a cell without a genome lives on what it inherited), 1, 2
    public readonly int[] GenomeLetters = new int[Chem2.L];   // the letters of the one duplex at Agent.G (as booked)
    public byte[] Replica = Array.Empty<byte>();               // the copy being made (residues), complete when Genomes is 2
    public readonly int[] ReplicaLetters = new int[Chem2.L];
    public int Fork;                // residues of the replica made so far
    public int Heading;             // 0–255: 1/256 of a turn
    public readonly List<Slot> Slots = new();
    public double[] Acc = Array.Empty<double>();               // synthesis owed by unit of the gene table
    public long Thrusts, Tumbles, Synth, Decayed, Photons, Reactions, Divisions;   // what it did (inspection, tests; saved)
    public double Uptake;           // molecules it took in from outside (leak, channels, pumps), less what it lost (saved)
    // Where its energy went, by path (observation only: not saved, it never changes what the cell does). See Ledger*.
    public readonly double[] Ledger = new double[LedgerN];
    public long CopyDone, CopyErrors;   // residues copied and substitutions among them (observation only, not saved)
    public readonly long[] StallLetter = new long[Chem2.L];   // which letter was short when a build stalled for monomers (observation)
    public double DiagXp, DiagXall, DiagTau;   // the first gene's promoter: polymerase occupancy (charged), all binders, synthesis per step (observation)
    public readonly long[] Stalls = new long[4];   // builds that stalled: synthesis for monomers, for charge; copy for monomers, for charge (observation)
    public const int LPhoto = 0, LPhotoHeat = 1, LCapture = 2, LPump = 3, LChargeIn = 4, LChargeOut = 5, LSynth = 6, LCopy = 7, LMotor = 8, LTurn = 9, LModify = 10, LProof = 11, LedgerN = 12;
    public static readonly string[] LedgerNames = { "photo", "photo heat", "captured", "pumps", "charge in", "charge out", "synthesis", "copy", "motor", "turn", "modify", "proofread" };

    // Derived (not saved: functions of the genome and the slots).
    internal GeneTable Table;
    internal ulong TableGenome, TableTypes;
    internal Chem2 C;

    public int Copies(ProteinType t)
    {
        foreach (var s in Slots) if (s.Type == t) return s.Total;
        return 0;
    }
}

public sealed class Slot
{
    public ProteinType Type;        // resolved from Seq in the world's chemistry (null right after a load until its first tick)
    public byte[] Seq;
    public readonly int[] N = new int[4];   // copies by modification state (bit k: site k excited)
    public int Total => N[0] + N[1] + N[2] + N[3];
}
