using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Primordium;

// Kinds of events in the world's chronicle (World.Chronicle). Observation only: nothing in the
// simulation reads them. A later mechanic (climate epochs, flares, eclipses…) adds its events by
// calling World.Chronicle.Add(...) with its type — EvType.Climate for weather and sky.
public enum EvType : byte
{
    FirstEnzyme,    // a protein of a (kind, target) kept for the first time in the world
    FirstReaction,  // a reaction first driven by a protein
    Discovery,      // hard rock first broken with a protein's help (World.Firsts)
    NewDiet,        // a large lineage now lives mostly on something else
    DepthRecord,    // a body deeper under the surface than any before
    CaveDweller,    // the first body to live a while under a roof
    FirstSwimmer,   // the first body afloat in water
    FirstBottom,    // the first body on the bottom of deep water
    FirstPredator,  // the first kill that took the victim's matter
    FirstParasite,  // the first body carrying another lineage's injected code to have offspring
    Speciation,     // a lineage split into two distant clusters of kin
    Extinction,     // a large (or once dominant) lineage died out
    NewDominant,    // another lineage is now the largest
    Record,         // largest, oldest, most children, longest genome
    Climate,        // strikes from space, new vents (later: ice ages, flares, eclipses)
    Player,         // designs planted, laws changed, saves and loads
    Count,
}

// One entry of the chronicle. Never changed after it is added (the game reads it from another thread).
public sealed class ChronicleEvent
{
    public long Seq;          // 1, 2, 3… in the order added
    public long Tick;
    public EvType Type;
    public long AgentId, Lineage;   // 0: no body / no lineage
    public int X = -1, Y = -1, Z = -1;
    public string Text;       // Russian, for the window
    public float Value;
    public bool Important;    // kept for ever (the rest live in a ring of P.ChronicleCap)
    public byte[] Genome;     // the participant's genome at the time (null if none)

    public override string ToString() => $"[{Tick}] {Chronicle.TypeNames[(int)Type]}: {Text}";
}

// A line of a tracked body's biography.
public enum BioKind : byte
{
    Born, Tracked, Protein, Reaction, CaveIn, CaveOut, Kill, Killed, Theft, Gift, Struck, Record, Child, Event, Death, Infected, Count,
}

public struct BioEntry
{
    public long Tick;
    public long Other;    // another body (parent, child, victim…) or a packed argument
    public float Value;
    public BioKind Kind;
    public byte Arg;      // protein kind, cause of death, EvType…
}

// What is left of a body: enough to look at it, read its genome and plant it again.
public sealed class Fossil
{
    public long AgentId, Lineage, ParentId;
    public int Gen;
    public long BornTick, DiedTick = -1;   // -1: was alive when taken (a lineage's representative)
    public int Cause;
    public long EventSeq = -1;             // the event that made it worth keeping (-1 none)
    public byte[] Genome;
    public int[] Body = new int[Chemistry.S];
    public Enzyme[] Proteins = Array.Empty<Enzyme>();
    public BioEntry[] Bio = Array.Empty<BioEntry>();   // oldest first
    public float Hue, Sat, Val, Energy, Mass;
    public int Shape, Children, Age;
    public bool Designed;
    public byte Why;
    public float Importance;

    public static Fossil Of(Agent a, long tick, long eventSeq = -1)
    {
        var f = new Fossil
        {
            AgentId = a.Id, Lineage = a.Lineage, ParentId = a.ParentId, Gen = a.Gen,
            BornTick = tick - a.Age, DiedTick = a.Dead ? tick : -1, Cause = a.Dead ? a.Cause : 0, EventSeq = eventSeq,
            Genome = (byte[])a.G.Clone(), Proteins = a.Enz.Take(a.EnzN).ToArray(), Bio = Chronicle.BioOf(a),
            Hue = a.Hue, Sat = a.Sat, Val = a.Val, Shape = a.Shape, Energy = a.LifeStart, Mass = a.Mass,
            Children = a.NChildren, Age = a.Age, Designed = a.Designed, Why = a.TrackWhy,
        };
        Array.Copy(a.Inv, f.Body, Chemistry.S);
        f.Importance = Chronicle.ImportanceOf(f);
        return f;
    }

    // A design to plant it again (the creature editor): its genome as text, its molecules, its looks.
    public CreatureDesign ToDesign(int seed)
    {
        var d = new CreatureDesign
        {
            Name = $"окаменелость #{AgentId}",
            Description = $"Из окаменелости: существо #{AgentId}, линия {Lineage}, поколение {Gen}, мир {seed}, " +
                          (DiedTick >= 0 ? $"жило с тика {BornTick} по {DiedTick}." : $"снято живым на тике {BornTick + Age}."),
            Genome = GenomeAsm.Disassemble(Genome),
            Energy = MathF.Round(Math.Clamp(Energy, 20, 200)),
            Hue = Hue, Sat = Sat, Val = Val, Shape = Shape,
        };
        int total = 0;
        for (int s = 0; s < Chemistry.S; s++)
            if (Body[s] > 0) { d.Body[s.ToString(CultureInfo.InvariantCulture)] = Body[s]; total += Body[s]; }
        if (total < P.MinBody) d.Body["any"] = P.MinBody - total;   // what was left of it is too little for a body
        return d;
    }
}

// A tracked body in the compact family tree: its parent and its nearest tracked ancestor (the chain of
// those leads to the founder of its lineage).
public sealed class AncestryNode
{
    public long Id, ParentId, TrackedParent, Lineage;
    public int Gen;
    public long Born, Died = -1;
    public byte Cause, Why;
}

// What the chronicle remembers about a sizeable lineage between surveys.
public sealed class LineageInfo
{
    public long Id;
    public int Peak, Last;
    public long FirstTick, PeakTick;
    public sbyte Diet = -1, PendingDiet = -1;   // Chronicle.Diet*: what most of it lives on
    public byte PendingN;
    public bool WasDominant;
    public readonly List<ulong> Centers = new();   // kin fingerprints of its known branches
    public Fossil Rep;                              // a representative body (its oldest member at the last survey)
}

// The world's chronicle: events (a ring of P.ChronicleCap plus every important one), fossils, the
// compact ancestry of tracked bodies and what the surveys remember. Filled by World (World.Chronicle.cs)
// between ticks; agents add to it only through their tile's Ctx (see SIMULATION.md, «Хроника»).
public sealed class Chronicle
{
    public const int BioCap = 64;
    public const byte WhyEvent = 1, WhyPlayer = 2, WhyFounder = 4, WhyDesigned = 8, WhyKid = 16;
    public const sbyte DietMixed = 5;

    public static readonly string[] TypeNames =
    {
        "первый белок", "первая реакция", "открытие породы", "смена питания", "рекорд глубины", "житель пещер",
        "первый пловец", "первый на дне", "первый хищник", "первый паразит", "видообразование", "вымирание",
        "новый доминант", "рекорд", "климат и небо", "игрок",
    };
    public static readonly string[] DietNames = { "почти ничего", "свет", "химия", "порода", "охота", "смешанное" };

    public long NextSeq = 1;
    public readonly List<ChronicleEvent> Important = new();   // for ever
    public readonly List<ChronicleEvent> Recent = new();      // the rest, oldest first, at most P.ChronicleCap
    public readonly long[] Counts = new long[(int)EvType.Count];   // every event ever added, by type
    public readonly List<Fossil> Fossils = new();
    public readonly Dictionary<long, Fossil> FossilByAgent = new();
    public readonly Dictionary<long, AncestryNode> Ancestry = new();
    public readonly Dictionary<long, LineageInfo> Lineages = new();
    public long Version;   // grows with every change the game may want to show

    // "Firsts" already seen: proteins (kind, a, b), catalysed reactions (kind, s1, s2) and one-off
    // events. Read by agents during their phase, written only between ticks.
    public const int SeenEnzyme = 0, SeenReaction = 4096, SeenOnce = 8192;
    public readonly bool[] Seen = new bool[SeenOnce + 64];
    public int DepthBest, DepthShown;   // deepest a body has been under its column's surface; last announced
    public long DominantLineage;
    public int DominantCount;
    public readonly float[] RecordBest = new float[4];   // mass, age, children, genome length: last announced

    public static int EnzymeKey(int kind, int a) => SeenEnzyme + (kind << 10 | (a & 31) << 5);
    public static int ReactionKey(int kind, int a, int b) => SeenReaction + (kind << 10 | (a & 31) << 5 | (b & 31));
    public static int OnceKey(EvType t) => SeenOnce + (int)t;

    public ChronicleEvent Add(ChronicleEvent e)
    {
        e.Seq = NextSeq++;
        Counts[(int)e.Type]++;
        if (e.Important) Important.Add(e);
        else
        {
            Recent.Add(e);
            int cap = Math.Max(16, P.ChronicleCap);
            if (Recent.Count > cap + cap / 8) Recent.RemoveRange(0, Recent.Count - cap);
        }
        Version++;
        return e;
    }

    // Every event kept, in order.
    public List<ChronicleEvent> All()
    {
        var all = new List<ChronicleEvent>(Important.Count + Recent.Count);
        int i = 0, j = 0;
        while (i < Important.Count || j < Recent.Count)
            all.Add(j >= Recent.Count || (i < Important.Count && Important[i].Seq < Recent[j].Seq) ? Important[i++] : Recent[j++]);
        return all;
    }

    public IEnumerable<ChronicleEvent> Since(long seq) => All().Where(e => e.Seq > seq);

    public void AddFossil(Fossil f)
    {
        if (FossilByAgent.TryGetValue(f.AgentId, out var old)) { Fossils.Remove(old); f.Importance = Math.Max(f.Importance, old.Importance); }
        Fossils.Add(f);
        FossilByAgent[f.AgentId] = f;
        // Over the cap: the least important (the oldest of equals) goes.
        while (Fossils.Count > Math.Max(1, P.FossilCap))
        {
            int worst = 0;
            for (int k = 1; k < Fossils.Count; k++) if (Fossils[k].Importance < Fossils[worst].Importance) worst = k;
            FossilByAgent.Remove(Fossils[worst].AgentId);
            Fossils.RemoveAt(worst);
        }
        // Ancestry of the dead without a fossil is the first to be forgotten.
        int cap = 8 * Math.Max(1, P.FossilCap);
        if (Ancestry.Count > cap + cap / 8)
        {
            var drop = Ancestry.Values.Where(n => n.Died >= 0 && !FossilByAgent.ContainsKey(n.Id)).OrderBy(n => n.Died).ThenBy(n => n.Id)
                .Take(Ancestry.Count - cap).Select(n => n.Id).ToList();
            foreach (var id in drop) Ancestry.Remove(id);
        }
        Version++;
    }

    public static float ImportanceOf(Fossil f)
    {
        float k = 0;
        if ((f.Why & WhyEvent) != 0 || f.EventSeq >= 0) k += 30;
        if ((f.Why & WhyPlayer) != 0) k += 20;
        if ((f.Why & WhyDesigned) != 0) k += 15;
        if ((f.Why & WhyFounder) != 0) k += 10;
        return k + 3 * MathF.Log2(1 + f.Children) + MathF.Min(5, f.Age / 2000f);
    }

    // A tracked body's biography, oldest first.
    public static BioEntry[] BioOf(Agent a)
    {
        var bio = a.Bio;
        if (bio == null || a.BioN == 0) return Array.Empty<BioEntry>();
        int n = Math.Min(a.BioN, bio.Length), start = a.BioN - n;
        var r = new BioEntry[n];
        for (int k = 0; k < n; k++) r[k] = bio[(start + k) % bio.Length];
        return r;
    }

    // ---- words ----

    public static string Day(long tick) => $"сутки {tick / P.DayLen + 1}";

    public static string CauseName(int cause) => cause switch
    {
        World.CauseStarve => "голод", World.CauseKilled => "убит", World.CauseBroken => "распался",
        World.CauseClimate => "мороз или жара", World.CauseBuried => "обвал", World.CauseHand => "рука игрока", _ => "жив",
    };

    public static string WhyText(byte why)
    {
        var parts = new List<string>();
        if ((why & WhyEvent) != 0) parts.Add("участник событий");
        if ((why & WhyPlayer) != 0) parts.Add("выбран игроком");
        if ((why & WhyFounder) != 0) parts.Add("основатель линии");
        if ((why & WhyDesigned) != 0) parts.Add("посажен игроком");
        if ((why & WhyKid) != 0) parts.Add("потомок посаженного");
        return parts.Count == 0 ? "—" : string.Join(", ", parts);
    }

    public static string BioText(BioEntry e, Chemistry ch)
    {
        string Mol(long s) => ch != null && s >= 0 && s < Chemistry.S ? ch.Name[s] : $"#{s}";
        return e.Kind switch
        {
            BioKind.Born => e.Value > 0 ? $"родился от #{e.Other} и #{(long)e.Value}" : e.Other > 0 ? $"родился делением #{e.Other}" : "появился (основатель линии)",
            BioKind.Tracked => $"под наблюдением: {WhyText(e.Arg)}",
            BioKind.Protein => $"первый белок «{Genome.EnzymeKind[e.Arg & 3]}»" + (e.Arg == Enzyme.Motor ? "" : $" на {Mol(e.Other & 31)}" + (e.Arg == Enzyme.Bind ? $" + {Mol(e.Other >> 5 & 31)}" : "")),
            BioKind.Reaction => $"первая реакция с белком: {Genome.EnzymeKind[e.Arg & 3]} {Mol(e.Other & 31)}" + (e.Arg == Enzyme.Bind ? $" + {Mol(e.Other >> 5 & 31)}" : ""),
            BioKind.CaveIn => $"ушёл под землю (глубина {e.Value:0})",
            BioKind.CaveOut => $"вышел на поверхность после {e.Value:0} тиков под землёй",
            BioKind.Kill => $"убил #{e.Other} (убийство №{e.Value:0})",
            BioKind.Killed => $"на него напал #{e.Other}",
            BioKind.Theft => $"украл у #{e.Other} (кража №{e.Value:0})",
            BioKind.Gift => $"подарил #{e.Other} (дар №{e.Value:0})",
            BioKind.Struck => $"облучён ударом с орбиты: геном теперь {e.Value:0} байт",
            BioKind.Record => $"рекорд: {RecordName(e.Arg)} {e.Value:0}",
            BioKind.Child => e.Value > 0 ? $"потомок №{e.Value:0}: #{e.Other}" : $"потомок от спаривания: #{e.Other}",
            BioKind.Event => $"событие хроники: {TypeNames[Math.Min((int)e.Arg, (int)EvType.Count - 1)]}",
            BioKind.Death => $"умер: {CauseName(e.Arg)}",
            BioKind.Infected => $"в него вписан код линии #{e.Other}",
            _ => e.Kind.ToString(),
        };
    }

    public static string RecordName(int k) => k switch { 0 => "масса", 1 => "возраст", 2 => "потомков", _ => "длина генома" };
}
