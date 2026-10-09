using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Primordium;

// The chronicle of the world (6.1), biographies of tracked bodies (6.2), fossils and their ancestry
// (6.3). Observation only: nothing here is read by the simulation, no random number is drawn, and the
// state hash of a world is the same with or without it.
//
// - During the agent phase bodies only propose: candidates go to their tile's Ctx.Chron (like
//   Ctx.Firsts), the deepest body of the tile to Ctx.DepthAgent, tracked bodies that died to
//   Ctx.TrackedDeaths. A body writes only into its own biography (and the victim's it is killing,
//   which it already reaches). After the phase the tiles are merged in tile order: the first proposal
//   of a "first" wins, the rest are dropped (Chronicle.Seen is read during the phase, written only here).
// - Between ticks (strikes, vents, the hand, designs, laws) events are added at once.
// - Every SurveyEvery ticks a pass over the bodies finds lineages that died out, a new largest one,
//   changes of diet, records and (every 4th survey) lineages that split into distant branches.
public sealed partial class World
{
    public readonly Chronicle Chronicle = new();
    public const int SurveyEvery = 500;
    // Thresholds of the surveys (what counts as worth a line in the chronicle).
    const int LineageMin = 10, ExtinctPeak = 30, ExtinctImportant = 100, DominantMin = 20, DietMin = 40, DietHold = 3, SpecMin = 20, SpecBits = 16;
    const float SpecSeparation = 1.8f;   // branches must be this many times further apart than their members from their centres
    static readonly int[] DepthMarks = { 3, 10, 30 };
    static readonly float[] RecordMin = { 60, 3000, 8, 160 };   // mass, age, children, genome length

    // A proposal from the agent phase, turned into an event after it (Chronicle.Seen decides).
    internal struct ChronCand
    {
        public EvType Type;
        public int Key;
        public Agent A, B;
        public float Value;
        public int Arg;
    }

    // ---- agent phase: proposals and biographies ----

    static void BioNote(Agent a, long tick, BioKind kind, long other = 0, float value = 0, int arg = 0)
    {
        var bio = a.Bio;
        if (bio == null) return;
        bio[a.BioN % bio.Length] = new BioEntry { Tick = tick, Kind = kind, Other = other, Value = value, Arg = (byte)arg };
        a.BioN++;
    }

    // Only bodies of established lineages (Agent.Established) make "firsts": the random code of a fresh
    // genome holds every kind of protein and does every kind of thing once.
    void Propose(EvType type, int key, Agent a, Agent b = null, float value = 0, int arg = 0)
    {
        if (!a.Established || Chronicle.Seen[key]) return;
        var c = new ChronCand { Type = type, Key = key, A = a, B = b, Value = value, Arg = arg };
        var ctx = cur;
        if (ctx == null) { Accept(c); return; }
        foreach (var o in ctx.Chron) if (o.Key == key) return;   // the tile proposed it already this tick
        ctx.Chron.Add(c);
    }

    // A protein the body keeps making (its amount reached 3) and whose gene has proven useful (protected
    // bytes): the first of its (kind, target molecule) in the world?
    void ChronProtein(Agent a, in Enzyme e)
    {
        int ea = e.Kind == Enzyme.Motor ? 0 : e.A, eb = e.Kind == Enzyme.Bind ? e.B : 0;
        if (e.Kind is Enzyme.Mechano or Enzyme.Thermo) ea = 0;   // no molecule of their own
        Propose(EvType.FirstEnzyme, Chronicle.EnzymeKey(e.Kind, ea), a, null, e.Eff, (e.Kind & 3) | ea << 2 | eb << 7 | (e.Kind >> 2) << 12);
    }

    // A reaction a protein drove (slot ≥ 0).
    void ChronReaction(Agent a, int kind, int s1, int s2)
    {
        if (kind != Enzyme.Bind) s2 = 0;
        else if (s2 < s1) (s1, s2) = (s2, s1);
        if (a.Bio != null && (a.BioSeen & 16) == 0) { a.BioSeen |= 16; BioNote(a, Tick, BioKind.Reaction, s1 | s2 << 5, 0, kind); }
        Propose(EvType.FirstReaction, Chronicle.ReactionKey(kind, s1, s2), a, null, 0, kind | s1 << 2 | s2 << 7);
    }

    // Every 8 ticks of a body's life: depth, caves, water.
    void ChronLive(Agent a)
    {
        int cell = a.Y * W + a.X, depth = Height[cell] - a.Z;
        if (depth > Chronicle.DepthBest)
        {
            var ctx = cur;
            if (ctx == null) { if (depth > Chronicle.DepthBest) AcceptDepth(a, depth); }
            else if (depth > ctx.DepthBest) { ctx.DepthBest = depth; ctx.DepthAgent = a; }
        }
        if ((a.Age & 63) == 0)
        {
            if (depth > 0)
            {
                if (a.CaveAge < 0) { a.CaveAge = a.Age; BioNote(a, Tick, BioKind.CaveIn, 0, depth); }
                else if (a.Age - a.CaveAge >= P.ChronicleCaveDays * P.DayLen) Propose(EvType.CaveDweller, Chronicle.OnceKey(EvType.CaveDweller), a, null, a.Age - a.CaveAge);
            }
            else if (a.CaveAge >= 0) { BioNote(a, Tick, BioKind.CaveOut, 0, a.Age - a.CaveAge); a.CaveAge = -1; }
        }
        if ((a.Age & 15) == 0 && Water[cell] >= P.SwimDepth && a.Z >= Height[cell])
        {
            if (a.Lift >= 1) Propose(EvType.FirstSwimmer, Chronicle.OnceKey(EvType.FirstSwimmer), a, null, a.Lift);
            else if (Water[cell] >= 6) Propose(EvType.FirstBottom, Chronicle.OnceKey(EvType.FirstBottom), a, null, Water[cell]);
        }
    }

    // A tracked body died (any thread: the tile's list during the phase, at once between ticks).
    void ChronDeath(Agent a)
    {
        BioNote(a, Tick, BioKind.Death, 0, a.Age, a.Cause);
        var ctx = cur;
        if (ctx != null) ctx.TrackedDeaths.Add(a);
        else Fossilize(a);
    }

    // A newborn: its parent, its place in the tracked ancestry, its own tracking (children of planted
    // designs), and whether it carries code another lineage wrote into its parent.
    void ChronBorn(Agent parent, Agent child, Agent mate)
    {
        child.ParentId = parent.Id;
        child.Established = parent.Established;
        child.TrackedAncestor = parent.Tracked ? parent.Id : parent.TrackedAncestor;
        if (parent.Designed && P.ChronicleTrackKids > 0) StartTracking(child, Chronicle.WhyKid);
        if (child.Bio != null) BioNote(child, Tick, BioKind.Born, parent.Id, mate?.Id ?? 0);
        if (parent.Bio != null && (parent.NChildren == 1 || parent.NChildren % 10 == 0)) BioNote(parent, Tick, BioKind.Child, child.Id, parent.NChildren);
        if (mate?.Bio != null) BioNote(mate, Tick, BioKind.Child, child.Id, 0);   // as the second parent
        InheritForeign(parent, child, mate);
        if (child.Foreign != null && child.ForeignFrom != child.Lineage)
            Propose(EvType.FirstParasite, Chronicle.OnceKey(EvType.FirstParasite), child, child.Foreign == parent.Foreign ? parent : mate, child.ForeignFrom);
    }

    static void StartTracking(Agent a, byte why)
    {
        a.TrackWhy |= why;
        if (a.Tracked) return;
        a.Tracked = true;
        a.Bio ??= new BioEntry[Chronicle.BioCap];
    }

    // ---- between ticks ----

    // After the agent phase, one tile at a time in tile order (Step), then once without a tile.
    void ChronMerge(Ctx ctx)
    {
        foreach (var c in ctx.Chron) Accept(c);
        ctx.Chron.Clear();
        if (ctx.DepthAgent != null && ctx.DepthBest > Chronicle.DepthBest) AcceptDepth(ctx.DepthAgent, ctx.DepthBest);
        ctx.DepthAgent = null; ctx.DepthBest = 0;
        foreach (var a in ctx.Newborn) if (a.Tracked) Node(a);
        foreach (var a in ctx.TrackedDeaths) Fossilize(a);
        ctx.TrackedDeaths.Clear();
    }

    void Accept(ChronCand c)
    {
        if (Chronicle.Seen[c.Key]) return;
        Chronicle.Seen[c.Key] = true;
        var a = c.A;
        var ch = Chem;
        string Mol(bool en, int s) => (en ? ch.NameEn : ch.NameRu)[s & 31];   // both languages: the text is kept
        switch (c.Type)
        {
            case EvType.FirstEnzyme:
                {
                    int kind = (c.Arg & 3) | (c.Arg >> 12 & 1) << 2, ea = c.Arg >> 2 & 31, eb = c.Arg >> 7 & 31;
                    string What(bool en) => kind switch
                    {
                        Enzyme.Bind => $"{Mol(en, ea)} + {Mol(en, eb)}", Enzyme.Motor => en ? "movement" : "движение",
                        Enzyme.Photo => (en ? "light + " : "свет + ") + Mol(en, ea),
                        Enzyme.Receptor => (en ? "smell of " : "чует ") + Mol(en, ea),
                        Enzyme.Photoreceptor => (en ? "sight, pigment " : "зрение, пигмент ") + Mol(en, ea),
                        Enzyme.Mechano => en ? "touch" : "осязание",
                        Enzyme.Thermo => en ? "warmth" : "тепло",
                        _ => $"{Mol(en, ea)} →",
                    };
                    bool firstOfKind = !AnySeen(Chronicle.SeenEnzyme, kind, c.Key);
                    Add(EvType.FirstEnzyme, Loc.Both($"first protein “{Genome.EnzymeKindEn[kind]}” ({What(true)}) — #{a.Id}, lineage #{a.Lineage}",
                                                     $"первый белок «{Genome.EnzymeKindRu[kind]}» ({What(false)}) — #{a.Id}, линия #{a.Lineage}"), a, c.Value, firstOfKind);
                    break;
                }
            case EvType.FirstReaction:
                {
                    int kind = c.Arg & 3, s1 = c.Arg >> 2 & 31, s2 = c.Arg >> 7 & 31;
                    string What(bool en) => kind switch
                    {
                        Enzyme.Bind => $"{Mol(en, s1)} + {Mol(en, s2)} → {(ch.Combine[s1, s2] >= 0 ? Mol(en, ch.Combine[s1, s2]) : "?")}",
                        Enzyme.Photo => (en ? "light: " : "свет: ") + $"{Mol(en, s1)} → {(ch.PhotoUp[s1] >= 0 ? Mol(en, ch.PhotoUp[s1]) : "?")}",
                        _ => $"{Mol(en, s1)} → {(ch.SplitA[s1] >= 0 ? Mol(en, ch.SplitA[s1]) : "?")}{(ch.SplitB[s1] >= 0 ? " + " + Mol(en, ch.SplitB[s1]) : "")}",
                    };
                    bool firstOfKind = !AnySeen(Chronicle.SeenReaction, kind, c.Key);
                    Add(EvType.FirstReaction, Loc.Both($"first time with a protein: {What(true)} — #{a.Id}, lineage #{a.Lineage}",
                                                       $"впервые с белком: {What(false)} — #{a.Id}, линия #{a.Lineage}"), a, 0, firstOfKind);
                    break;
                }
            case EvType.CaveDweller:
                Add(c.Type, Loc.Both($"cave dweller: #{a.Id} (lineage #{a.Lineage}) has lived under a roof for {c.Value / P.DayLen:0.#} days",
                                     $"житель пещер: #{a.Id} (линия #{a.Lineage}) прожил под крышей {c.Value / P.DayLen:0.#} сут."), a, c.Value, true);
                break;
            case EvType.FirstSwimmer:
                Add(c.Type, Loc.Both($"first swimmer: #{a.Id} (lineage #{a.Lineage}) floats in open water {c.Value:0.0} blocks above the bottom",
                                     $"первый пловец: #{a.Id} (линия #{a.Lineage}) держится в толще воды в {c.Value:0.0} блока над дном"), a, c.Value, true);
                break;
            case EvType.FirstBottom:
                Add(c.Type, Loc.Both($"first on the bottom of deep water: #{a.Id} (lineage #{a.Lineage}), {c.Value:0.0} blocks of water above it",
                                     $"первый на дне глубокой воды: #{a.Id} (линия #{a.Lineage}), над ним {c.Value:0.0} блока воды"), a, c.Value, true);
                break;
            case EvType.FirstPredator:
                Add(c.Type, Loc.Both($"first predator: #{a.Id} (lineage #{a.Lineage}) killed #{c.B?.Id} (lineage #{c.B?.Lineage}) and took its matter",
                                     $"первый хищник: #{a.Id} (линия #{a.Lineage}) убил #{c.B?.Id} (линия #{c.B?.Lineage}) и забрал его вещество"), a, c.Value, true, c.B);
                break;
            case EvType.FirstParasite:
                Add(c.Type, Loc.Both($"first parasite: code of lineage #{(long)c.Value} passed to #{a.Id}, offspring of #{c.B?.Id} (lineage #{a.Lineage})",
                                     $"первый паразит: код линии #{(long)c.Value} передан потомку #{a.Id} тела #{c.B?.Id} (линия #{a.Lineage})"), a, c.Value, true, c.B);
                break;
            case EvType.ParasiteSpread:
                Add(c.Type, Loc.Both($"parasite spread: code of lineage #{(long)c.Value} written into #{c.B?.Id} (lineage #{c.B?.Lineage}) was copied on by it into #{a.Id} (lineage #{a.Lineage})",
                                     $"паразит распространился: код линии #{(long)c.Value}, вписанный в #{c.B?.Id} (линия #{c.B?.Lineage}), тот переписал дальше в #{a.Id} (линия #{a.Lineage})"), a, c.Value, true, c.B);
                break;
        }
    }

    // Was anything of this protein kind seen before (other than `except`)?
    // Kinds 4–7 (the organs, P.Organs 1) share the key range of kind − 4 with bit 0 set (Chronicle.EnzymeKey).
    bool AnySeen(int table, int kind, int except)
    {
        bool enzyme = table == Chronicle.SeenEnzyme;
        int organ = kind >> 2, from = (enzyme ? kind & 3 : kind) << 10;
        for (int k = from; k < from + (1 << 10); k++)
            if ((!enzyme || (k & 1) == organ) && Chronicle.Seen[table + k] && table + k != except) return true;
        return false;
    }

    // Announced: a new record now and then (each at least a quarter deeper than the last announced)
    // and every mark of DepthMarks.
    void AcceptDepth(Agent a, int depth)
    {
        if (depth <= Chronicle.DepthBest) return;
        Chronicle.DepthBest = depth;
        bool mark = false;
        foreach (int m in DepthMarks) if (depth >= m && Chronicle.DepthShown < m) mark = true;
        if (!mark && depth < Chronicle.DepthShown * 1.25f + 1) return;
        Chronicle.DepthShown = depth;
        Add(EvType.DepthRecord, Loc.Both($"depth record: #{a.Id} (lineage #{a.Lineage}) {depth} {PluralEn(depth, "level", "levels")} below the surface",
                                         $"рекорд глубины: #{a.Id} (линия #{a.Lineage}) на {depth} {Plural(depth, "уровень", "уровня", "уровней")} под поверхностью"), a, depth, mark);
    }

    // Russian plural forms (1 уровень, 2 уровня, 5 уровней).
    static string Plural(int n, string one, string few, string many)
    {
        int m = Math.Abs(n) % 100, d = m % 10;
        return m is >= 11 and <= 14 ? many : d == 1 ? one : d is >= 2 and <= 4 ? few : many;
    }

    // English plural (1 level, 2 levels).
    static string PluralEn(int n, string one, string many) => Math.Abs(n) == 1 ? one : many;

    // Adds an event (between ticks); its body, if any, becomes tracked. The text is kept (and saved): build
    // it with Loc.Both(en, ru), never with Loc.T, so that it does not depend on the language of the moment.
    public ChronicleEvent Add(EvType type, string text, Agent a = null, float value = 0, bool important = false, Agent other = null, int x = -1, int y = -1)
    {
        var e = new ChronicleEvent
        {
            Tick = Tick, Type = type, Text = text, Value = value, Important = important,
            AgentId = a?.Id ?? 0, Lineage = a?.Lineage ?? 0, X = a?.X ?? x, Y = a?.Y ?? y, Z = a?.Z ?? -1,
            Genome = a != null ? (byte[])a.G.Clone() : null,
        };
        Chronicle.Add(e);
        if (a != null) Participant(a, e);
        if (other != null) Participant(other, e);
        return e;
    }

    void Participant(Agent a, ChronicleEvent e)
    {
        Track(a, Chronicle.WhyEvent);
        if (e.Type != EvType.Record) BioNote(a, Tick, BioKind.Event, e.Seq, e.Value, (int)e.Type);   // a record has its own line
        if (a.Dead) Fossilize(a, e.Seq);
    }

    // Start (or widen) tracking of a body between ticks: a biography from now on, a node in the ancestry.
    public void Track(Agent a, byte why)
    {
        if (a == null) return;
        bool was = a.Tracked;
        byte before = a.TrackWhy;
        StartTracking(a, why);
        if (!was) BioNote(a, Tick, BioKind.Tracked, 0, 0, why);
        else if ((before | why) != before) BioNote(a, Tick, BioKind.Tracked, 0, 0, why);
        Node(a).Why = a.TrackWhy;
        Chronicle.Version++;
    }

    // A body just made between ticks (a founder, a planted design): tracked from its first line.
    void TrackNew(Agent a, byte why)
    {
        StartTracking(a, why);
        BioNote(a, Tick, BioKind.Born);
        Node(a).Why = a.TrackWhy;
        Chronicle.Version++;
    }

    AncestryNode Node(Agent a)
    {
        if (Chronicle.Ancestry.TryGetValue(a.Id, out var n)) return n;
        n = new AncestryNode { Id = a.Id, ParentId = a.ParentId, TrackedParent = a.TrackedAncestor, Lineage = a.Lineage, Gen = a.Gen, Born = Tick - a.Age, Why = a.TrackWhy };
        if (a.Dead) { n.Died = Tick; n.Cause = (byte)a.Cause; }
        Chronicle.Ancestry[a.Id] = n;
        return n;
    }

    // A tracked body that died: kept as a fossil if it is worth it (took part in events, was chosen or
    // planted by the player, or founded a lineage that had children).
    void Fossilize(Agent a, long eventSeq = -1)
    {
        var n = Node(a);
        n.Died = Tick; n.Cause = (byte)a.Cause; n.Why = a.TrackWhy;
        byte why = a.TrackWhy;
        bool keep = (why & (Chronicle.WhyEvent | Chronicle.WhyPlayer | Chronicle.WhyDesigned)) != 0 || eventSeq >= 0
                 || ((why & (Chronicle.WhyFounder | Chronicle.WhyKid)) != 0 && a.NChildren > 0);
        if (!keep) { Chronicle.Version++; return; }
        if (eventSeq < 0)
            foreach (var b in Chronicle.BioOf(a)) if (b.Kind == BioKind.Event) eventSeq = b.Other;
        var f = Fossil.Of(a, Tick, eventSeq);
        Chronicle.AddFossil(f);
    }

    // Hard rock broken with a protein's help for the first time (World.Firsts, merged just before).
    void ChronDiscoveries(int before)
    {
        int now = 0;
        foreach (var f in Firsts) if (f != null) now++;
        if (now == before) return;
        foreach (var f in Firsts)
        {
            if (f == null || f.Tick != Tick) continue;
            Agent a = null;
            foreach (var b in Agents) if (b.Id == f.AgentId) { a = b; break; }
            string matEn = Chem.MatNameEn[f.Mat], matRu = Chem.MatNameRu[f.Mat];   // both languages: the text is kept
            var e = Add(EvType.Discovery, Loc.Both($"discovery: {matEn} — lineage #{f.Lineage} breaks it down with a protein (#{f.AgentId})",
                                                   $"открытие: {matRu} — линия #{f.Lineage} разрушает его белком (#{f.AgentId})"), a, f.Mat, true);
            e.Lineage = f.Lineage; e.AgentId = f.AgentId;
        }
    }

    // ---- surveys ----

    sealed class Tally
    {
        public int N;
        public readonly int[] Diet = new int[5];
        public Agent Oldest;
    }

    void ChronSurvey()
    {
        var tally = new Dictionary<long, Tally>();
        var best = new Agent[4];
        var bestV = new float[4];
        foreach (var a in Agents)
        {
            if (a.Dead) continue;
            if (!tally.TryGetValue(a.Lineage, out var t)) tally[a.Lineage] = t = new Tally();
            t.N++;
            t.Diet[Diet(a)]++;
            if (t.Oldest == null || a.Age > t.Oldest.Age) t.Oldest = a;
            float m = a.Mass, age = a.Age, kids = a.NChildren, len = a.G.Length;
            if (m > bestV[0]) { bestV[0] = m; best[0] = a; }
            if (age > bestV[1]) { bestV[1] = age; best[1] = a; }
            if (kids > bestV[2]) { bestV[2] = kids; best[2] = a; }
            if (len > bestV[3]) { bestV[3] = len; best[3] = a; }
        }
        var lin = Chronicle.Lineages;

        // Lineages that died out (in the order of their ids: the same order every run).
        foreach (var id in lin.Keys.OrderBy(k => k).ToList())
        {
            var info = lin[id];
            if (tally.ContainsKey(id)) continue;
            lin.Remove(id);
            if (info.Peak < ExtinctPeak && !info.WasDominant) continue;
            bool important = info.Peak >= ExtinctImportant || info.WasDominant;
            string design = DesignOf(id);
            long peakDay = info.PeakTick / P.DayLen + 1, firstDay = info.FirstTick / P.DayLen + 1;
            var e = Add(EvType.Extinction, Loc.Both(
                            $"lineage #{id}{(design != null ? $" (“{CreatureExamples.NameEn(design)}”)" : "")} went extinct: up to {info.Peak} individuals (day {peakDay}), lived since day {firstDay}" +
                            (info.WasDominant ? " — was the largest" : ""),
                            $"вымерла линия #{id}{(design != null ? $" («{CreatureExamples.NameRu(design)}»)" : "")}: до {info.Peak} особей (сутки {peakDay}), жила с суток {firstDay}" +
                            (info.WasDominant ? " — была крупнейшей" : "")), null, info.Peak, important);
            e.Lineage = id;
            if (info.Rep != null)
            {
                var rep = info.Rep;
                rep.EventSeq = e.Seq; rep.Why |= Chronicle.WhyEvent;
                if (rep.DiedTick < 0) rep.DiedTick = Tick;
                rep.Importance = Chronicle.ImportanceOf(rep) + (important ? 20 : 0);
                e.AgentId = rep.AgentId; e.Genome = rep.Genome;
                if (!Chronicle.FossilByAgent.ContainsKey(rep.AgentId)) Chronicle.AddFossil(rep);
            }
        }

        // Lineages worth remembering, their representatives and diets.
        long top = 0; int topN = 0;
        foreach (var (id, t) in tally.OrderBy(kv => kv.Key))
        {
            if (t.N > topN) { top = id; topN = t.N; }
            if (!lin.TryGetValue(id, out var info))
            {
                if (t.N < LineageMin) continue;
                lin[id] = info = new LineageInfo { Id = id, FirstTick = Tick };
            }
            info.Last = t.N;
            if (t.N > info.Peak) { info.Peak = t.N; info.PeakTick = Tick; }
            if (info.Rep == null || info.Rep.AgentId != t.Oldest.Id) info.Rep = Fossil.Of(t.Oldest, Tick);
            if (t.N >= DietMin) SurveyDiet(info, t);
        }

        MarkEstablished();

        // A new largest lineage (it has to be clearly larger than the old one).
        int domNow = Chronicle.DominantLineage != 0 && tally.TryGetValue(Chronicle.DominantLineage, out var dt) ? dt.N : 0;
        Chronicle.DominantCount = domNow;
        if (top != 0 && top != Chronicle.DominantLineage && topN >= DominantMin && topN >= domNow * 1.25f)
        {
            long was = Chronicle.DominantLineage;
            Chronicle.DominantLineage = top; Chronicle.DominantCount = topN;
            if (lin.TryGetValue(top, out var info)) info.WasDominant = true;
            string design = DesignOf(top);
            Add(EvType.NewDominant, Loc.Both(
                    $"the largest lineage is now #{top}{(design != null ? $" (“{CreatureExamples.NameEn(design)}”)" : "")}: {topN} individuals" + (was != 0 ? $", the previous one #{was} — {domNow}" : ""),
                    $"крупнейшая линия теперь #{top}{(design != null ? $" («{CreatureExamples.NameRu(design)}»)" : "")}: {topN} особей" + (was != 0 ? $", прежняя #{was} — {domNow}" : "")),
                tally[top].Oldest, topN, true);
        }

        // Records of the living: announced when half again above the last announced.
        for (int k = 0; k < 4; k++)
        {
            var a = best[k];
            if (a == null || bestV[k] < RecordMin[k] || bestV[k] < Chronicle.RecordBest[k] * 1.5f) continue;
            Chronicle.RecordBest[k] = bestV[k];
            BioNote(a, Tick, BioKind.Record, 0, bestV[k], k);
            Add(EvType.Record, Loc.Both($"record: {Chronicle.RecordNameEn(k)} {bestV[k]:0} — #{a.Id}, lineage #{a.Lineage}",
                                        $"рекорд: {Chronicle.RecordNameRu(k)} {bestV[k]:0} — #{a.Id}, линия #{a.Lineage}"), a, bestV[k]);
        }

        if (Tick % (4 * SurveyEvery) == 0) SurveySpeciation(tally);
        Chronicle.Version++;
    }

    // Which bodies may make "firsts": those of lineages the survey found large enough. Newborns inherit
    // it (ChronBorn); a loaded world derives it the same way.
    void MarkEstablished()
    {
        var lin = Chronicle.Lineages;
        foreach (var a in Agents) a.Established = lin.TryGetValue(a.Lineage, out var info) && info.Last >= LineageMin;
    }

    // What most of a lineage lives on; a change has to hold for DietHold surveys in a row.
    void SurveyDiet(LineageInfo info, Tally t)
    {
        int active = t.N - t.Diet[DietIdle], most = 1;
        for (int d = 2; d <= 4; d++) if (t.Diet[d] > t.Diet[most]) most = d;
        sbyte diet = active < t.N / 3 ? (sbyte)DietIdle : t.Diet[most] * 2 >= active ? (sbyte)most : Chronicle.DietMixed;
        if (diet == info.Diet) { info.PendingN = 0; return; }
        if (diet != info.PendingDiet) { info.PendingDiet = diet; info.PendingN = 1; }
        else info.PendingN++;
        if (info.Diet < 0) { if (info.PendingN >= DietHold) { info.Diet = diet; info.PendingN = 0; } return; }
        if (info.PendingN < DietHold) return;
        sbyte was = info.Diet;
        info.Diet = diet; info.PendingN = 0;
        Add(EvType.NewDiet, Loc.Both($"lineage #{info.Id} ({t.N} individuals) now lives differently: {Chronicle.DietNamesEn[was]} → {Chronicle.DietNamesEn[diet]}",
                                     $"линия #{info.Id} ({t.N} особей) теперь живёт иначе: {Chronicle.DietNamesRu[was]} → {Chronicle.DietNamesRu[diet]}"), t.Oldest, diet,
            info.Id == Chronicle.DominantLineage);
    }

    // Lineages that split: kin fingerprints (Agent.Tag) of a large lineage fall into two clusters
    // (majority-vote centres, a few rounds) both of at least SpecMin bodies and SpecBits apart. Known
    // branches follow their drift; only a branch far from all known ones is news.
    void SurveySpeciation(Dictionary<long, Tally> tally)
    {
        var tags = new Dictionary<long, List<ulong>>();
        foreach (var a in Agents)
        {
            if (a.Dead || !tally.TryGetValue(a.Lineage, out var t) || t.N < 2 * SpecMin || !Chronicle.Lineages.ContainsKey(a.Lineage)) continue;
            if (!tags.TryGetValue(a.Lineage, out var list)) tags[a.Lineage] = list = new List<ulong>();
            list.Add(a.Tag);
        }
        Span<int> votes = stackalloc int[64];
        foreach (var (id, list) in tags.OrderBy(kv => kv.Key))
        {
            var info = Chronicle.Lineages[id];
            ulong c0 = list[0], c1 = list[0];
            int far = 0;
            foreach (var g in list) { int d = BitOperations.PopCount(g ^ c0); if (d > far) { far = d; c1 = g; } }
            int n0 = 0, n1 = 0;
            for (int round = 0; round < 4; round++)
            {
                ulong m0 = 0, m1 = 0;
                for (int pass = 0; pass < 2; pass++)
                {
                    votes.Clear();
                    int count = 0;
                    foreach (var g in list)
                    {
                        bool second = BitOperations.PopCount(g ^ c1) < BitOperations.PopCount(g ^ c0);
                        if (second != (pass == 1)) continue;
                        count++;
                        for (int b = 0; b < 64; b++) if ((g >> b & 1) != 0) votes[b]++;
                    }
                    ulong centre = 0;
                    for (int b = 0; b < 64; b++) if (votes[b] * 2 > count) centre |= 1UL << b;
                    if (pass == 0) { m0 = centre; n0 = count; } else { m1 = centre; n1 = count; }
                }
                c0 = m0; c1 = m1;
            }
            int apart = BitOperations.PopCount(c0 ^ c1);
            long spread = 0;   // how far members are from their own centre, on average
            foreach (var g in list) spread += Math.Min(BitOperations.PopCount(g ^ c0), BitOperations.PopCount(g ^ c1));
            float within = spread / (float)list.Count;
            bool split = n0 >= SpecMin && n1 >= SpecMin && apart >= SpecBits && apart >= SpecSeparation * within;            var (big, bigN) = n0 >= n1 ? (c0, n0) : (c1, n1);
            var (small, smallN) = n0 >= n1 ? (c1, n1) : (c0, n0);
            // The larger branch is the lineage going on: it is matched to the nearest known branch and moves it.
            int Nearest(ulong c, int skip, out int dist)
            {
                int best = -1; dist = 65;
                for (int k = 0; k < info.Centers.Count; k++) { if (k == skip) continue; int d = BitOperations.PopCount(info.Centers[k] ^ c); if (d < dist) { dist = d; best = k; } }
                return best;
            }
            int main = Nearest(big, -1, out _);
            if (main < 0) { info.Centers.Add(big); main = 0; } else info.Centers[main] = big;
            if (!split) continue;
            int other = Nearest(small, main, out int nd);
            if (other >= 0 && nd < SpecBits) { info.Centers[other] = small; continue; }   // a known branch, drifted
            info.Centers.Add(small);
            if (info.Centers.Count > 8) info.Centers.RemoveAt(info.Centers.Count == 9 && main == 0 ? 1 : 0);
            Agent rep = null;
            foreach (var a in Agents)
                if (!a.Dead && a.Lineage == id && BitOperations.PopCount(a.Tag ^ small) < BitOperations.PopCount(a.Tag ^ big) && (rep == null || a.Age > rep.Age)) rep = a;
            Add(EvType.Speciation, Loc.Both(
                    $"lineage #{id} split: a new branch of {smallN} individuals, {apart} of 64 kinship bits apart with a spread of {within:0.#} within branches (main branch: {bigN})",
                    $"линия #{id} разделилась: новая ветвь из {smallN} особей, отличие {apart} бит родства из 64 при разбросе внутри ветвей {within:0.#} (основная — {bigN})"), rep, apart, true);
        }
    }

    // ---- the game and the bench ----

    // Event when a law changes (World.SetParam) and when the player plants a design (World.SpawnDesign).
    void ChronLaw(ParamInfo p, double was)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string change = $"{p.Name}: {was.ToString("G6", inv)} → {p.Value.ToString("G6", inv)}";
        Add(EvType.Player, Loc.Both($"law {change} ({p.DescriptionEn})", $"закон {change} ({p.DescriptionRu})"), null, (float)p.Value);
    }
}
