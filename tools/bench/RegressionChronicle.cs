using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Primordium;

// The chronicle (World.Chronicle.cs): it only watches. Tracking every body changes nothing in the
// world; the chronicle itself is the same on every run of a seed; "firsts" come once; chronicle,
// biographies and fossils survive save/load and continue exactly; a fossil plants again.
public sealed partial class World
{
    // The chronicle's part of a save (events, fossils, ancestry, surveys, every body's fields), for comparing.
    byte[] ChronicleBytes()
    {
        var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, true)) SyncChronicle(new Writer(bw));
        return ms.ToArray();
    }

    static string ChronicleText(World w) => string.Join("\n", w.Chronicle.All().Select(e => $"{e.Seq} {e.Tick} {e.Type} {e.AgentId} {e.Lineage} {e.Important} {e.Text}"));

    static void ChronicleRegression()
    {
        // 1. Watching changes nothing: every body tracked (biographies, fossils, ancestry for all) — same world.
        var plain = new World(3, 600, true);
        var watched = new World(3, 600, true);
        foreach (var a in watched.Agents) watched.Track(a, Chronicle.WhyPlayer);
        for (int t = 1; t <= 1500; t++)
        {
            plain.Step(); watched.Step();
            if (t % 500 == 0) Require(plain.StateHash() == watched.StateHash(), $"tracking every body changed the world at tick {t}");
            if (t % 100 == 0) foreach (var a in watched.Agents) if (!a.Tracked) watched.Track(a, Chronicle.WhyPlayer);
        }
        Require(watched.Chronicle.Fossils.Count > plain.Chronicle.Fossils.Count, "tracked bodies left no fossils");
        int bios = watched.Agents.Count(a => a.BioN > 1);
        Require(bios > 0, "no biography was written");

        // 2. The same seed writes the same chronicle (tiles run on whatever threads), and "firsts" come once.
        var again = new World(3, 600, true);
        for (int t = 0; t < 1500; t++) again.Step();
        Require(ChronicleText(again) == ChronicleText(plain), "the chronicle differs between two runs of one seed");
        var all = plain.Chronicle.All();
        Require(all.Count > 0, "an empty chronicle after 1500 ticks");
        foreach (var type in new[] { EvType.CaveDweller, EvType.FirstSwimmer, EvType.FirstBottom, EvType.FirstPredator, EvType.FirstParasite })
            Require(all.Count(e => e.Type == type) <= 1, $"{type} more than once");
        foreach (var type in new[] { EvType.FirstEnzyme, EvType.FirstReaction })
        {
            var texts = all.Where(e => e.Type == type).Select(e => Ru(e.Text).Split(" — ")[0]).ToList();
            Require(texts.Distinct().Count() == texts.Count, $"{type}: the same first twice");
        }
        Require(all.Zip(all.Skip(1)).All(p => p.First.Seq < p.Second.Seq && p.First.Tick <= p.Second.Tick), "events out of order");
        // Proposed twice (as two tiles would in one tick, or one body tick after tick): one event. A body of
        // a lineage not yet established proposes nothing.
        {
            var d = new World(3, 200, false);
            var a = d.Agents.First(x => !x.Dead);
            var e = new Enzyme { Kind = Enzyme.Split, A = 5, Eff = 0.5f };
            long before = d.Chronicle.NextSeq;
            d.ChronProtein(a, e);
            Require(d.Chronicle.NextSeq == before, "an unestablished lineage made a first");
            a.Established = true;
            d.ChronProtein(a, e); d.ChronProtein(a, e with { B = 9 });
            d.ChronReaction(a, Enzyme.Bind, 3, 7); d.ChronReaction(a, Enzyme.Bind, 7, 3);
            d.Propose(EvType.FirstSwimmer, Chronicle.OnceKey(EvType.FirstSwimmer), a); d.Propose(EvType.FirstSwimmer, Chronicle.OnceKey(EvType.FirstSwimmer), a);
            var got = d.Chronicle.All().Where(x => x.Seq >= before).Select(x => x.Type).ToList();
            Require(got.Count == 3 && got.Count(t => t == EvType.FirstEnzyme) == 1 && got.Count(t => t == EvType.FirstReaction) == 1 && got.Count(t => t == EvType.FirstSwimmer) == 1,
                "firsts not deduplicated: " + string.Join(", ", got));
            Require(a.Tracked && (a.TrackWhy & Chronicle.WhyEvent) != 0 && a.Bio.Take(a.BioN).Count(b => b.Kind == BioKind.Event) == 3, "an event's body is not tracked with the events in its biography");
        }

        // 3. Save/load: the chronicle, every biography and the fossils come back and go on the same way.
        var w = watched;
        var someone = w.Agents.First(a => !a.Dead && a.Tracked);
        w.KillIn(someone.X, someone.Y, 2);   // fossils made between ticks
        w.SetParam("FaceWork", 8.5);         // a player's event
        var ms = new MemoryStream();
        w.Save(ms, "chronicle"); ms.Position = 0;
        var b = Load(ms);
        Require(b.ChronicleBytes().AsSpan().SequenceEqual(w.ChronicleBytes()), "chronicle differs after load");
        Require(b.Chronicle.Fossils.Count == w.Chronicle.Fossils.Count && b.Chronicle.Ancestry.Count == w.Chronicle.Ancestry.Count, "fossils or ancestry lost");
        Require(b.Agents.Zip(w.Agents).All(p => p.First.BioN == p.Second.BioN && p.First.ParentId == p.Second.ParentId && p.First.Established == p.Second.Established), "biographies lost");
        for (int t = 0; t < 600; t++) { w.Step(); b.Step(); }
        Require(b.StateHash() == w.StateHash(), "loaded world diverged");
        Require(b.ChronicleBytes().AsSpan().SequenceEqual(w.ChronicleBytes()), "loaded chronicle diverged");
        var fossil = w.Chronicle.Fossils.OrderByDescending(f => f.Importance).First();
        var tracked = w.Agents.First(a => !a.Dead && a.Tracked && a.BioN > 0);
        Require(Chronicle.BioOf(tracked).Last().Tick <= w.Tick && Chronicle.BioOf(tracked).All(e => Chronicle.BioText(e, w.Chem).Length > 0), "biography lines");
        ParamRegistry.ResetDefaults();

        // 4. A fossil plants again: design from it, planted from outside, the same genome lives.
        var design = CreatureDesign.FromJson(fossil.ToDesign(w.Seed).ToJson());
        Require(design.Check(w.Chem).Count == 0, "a fossil's design has errors: " + string.Join("; ", design.Check(w.Chem)));
        Require(design.Assemble().AsSpan().SequenceEqual(fossil.Genome), "a fossil's genome changed on its way to a design");
        int cell = Enumerable.Range(0, w.N).Select(k => (int)((k * 2654435761L + 777) % w.N)).First(c => !w.Submerged(c) && w.Count[c] == 0 && w.Height[c] < w.Z - 4);
        var r = w.SpawnDesign(design, cell % w.W, cell / w.W, new SpawnOptions { Matter = MatterSource.Import, Energy = EnergySource.Import, Count = 1, Radius = 0 });
        Require(r.Made == 1 && r.Agents[0].G.AsSpan().SequenceEqual(fossil.Genome) && r.Agents[0].Tracked, $"a fossil did not plant again: {r}");
        Require(w.Chronicle.All().Last().Type == EvType.Player, "planting not in the chronicle");
        for (int t = 0; t < 50; t++) w.Step();

        var counts = string.Join(", ", Enumerable.Range(0, (int)EvType.Count).Where(k => plain.Chronicle.Counts[k] > 0).Select(k => $"{Chronicle.TypeNames[k]} {plain.Chronicle.Counts[k]}"));
        Console.WriteLine($"PASS chronicle: watching every body changes nothing (1500 ticks), the same chronicle on every run ({all.Count} events: {counts}), " +
                          $"firsts once, {w.Chronicle.Fossils.Count} fossils and {bios} biographies survive save/load and continue identically, fossil #{fossil.AgentId} planted again");
    }
}
