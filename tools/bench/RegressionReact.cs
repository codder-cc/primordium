using System;
using System.Linq;

namespace Primordium;

public sealed partial class World
{
    // Reactive damage (World.React), one law for every species:
    // - the species factor is mean atom affinity × excitation energy for all 32 alike (ground states 0);
    // - a held molecule reacts at the law's rate (count within a few sigma), only an excited one, faster
    //   when warm; the molecule drops to its ground state, a protein loses ReactWear, the excitation is
    //   heat (body decay): atoms exact, the ledger closes; ReactK 0 switches it off;
    // - a molecule lying in the cell reacts ReactContact as often, on the floor (no seeping in);
    // - poisoning sprays the most reactive species by the law, or a given mix, booked by the hand.
    public static void ReactRegression()
    {
        ParamRegistry.ResetDefaults();
        var w = Fixture(); var ch = w.Chem;
        for (int s = 0; s < Chemistry.S; s++)
        {
            float want = ch.AffinityPerAtom[s] * (ch.E[s] - ch.E[Chemistry.Ground(s)]);
            Require(ch.Reactivity[s] == want, $"reactivity of {ch.Formula(s)}: {ch.Reactivity[s]} (want {want})");
            Require((s == Chemistry.Ground(s)) == (ch.Reactivity[s] == 0), $"{ch.Formula(s)}: a ground state reacts or an excited one does not");
            Require(ch.Reactivity[s] <= ch.Reactivity[ch.MostReactive], "MostReactive is not the most reactive");
        }
        int hot = ch.MostReactive, ground = Chemistry.Ground(hot);
        int c = 80 * w.W + 120;

        // Held: N molecules and a protein; count reactions over many ticks against the law's expectation.
        (long hits, double expected) Held(int s, float tb, int n, int ticks)
        {
            var a = w.TestAgent(c, 2, s, n);
            long r0 = w.ReactHeld;
            double expected = 0;
            for (int t = 0; t < ticks; t++)
            {
                a.Tb = tb;
                a.Enz[0] = new Enzyme { Kind = Enzyme.Photo, A = 0, Amount = 3, Eff = 1, Topt = 15 }; a.EnzN = 1;
                while (a.Inv[s] < n) { w.RemoveMol(a, Chemistry.Ground(s)); w.AddMol(a, s); }   // re-excite what reacted: a steady exposure
                expected += a.Inv[s] * ReactChance(ch, s, tb);
                w.ReactiveDamage(a);
            }
            w.Die(a, c, CauseHand);
            return (w.ReactHeld - r0, expected);
        }
        P.ReactContact = 0;
        var atoms0 = w.ElementBudget(); var e0 = w.EnergyStart();
        var probe = w.TestAgent(c, 2, hot, 40);
        probe.Enz[0] = new Enzyme { Kind = Enzyme.Photo, A = 0, Amount = 3, Eff = 1, Topt = 15, Matter = 2 }; probe.EnzN = 1;
        probe.Mass += 2 * ch.Mass[0]; probe.Volume += 2 * ch.Volume[0];
        atoms0 = w.ElementBudget(); e0 = w.EnergyStart();
        long h0 = w.ReactHeld;
        for (int t = 0; t < 4000 && probe.Inv[hot] > 0; t++) { probe.Tb = 15; w.ReactiveDamage(probe); }
        long reacted = w.ReactHeld - h0;
        Require(reacted > 0 && probe.Inv[hot] == 40 - reacted && probe.Inv[ground] == reacted, $"held: {reacted} reacted, {probe.Inv[hot]} left, {probe.Inv[ground]} relaxed");
        Require(probe.Enz[0].Amount < 3, "held: the protein was not worn");
        BudgetEqual(atoms0, w.ElementBudget(), "reactive damage, held", 0);
        w.EnergyBalanced(e0, "reactive damage, held", FBodyDecay);
        w.Die(probe, c, CauseHand);

        var (hits, expected) = Held(hot, 15, 1, 20000);
        Require(Math.Abs(hits - expected) <= 5 * Math.Sqrt(expected) + 2, $"held rate: {hits} reactions, law expects {expected:F1}");
        var (inert, _) = Held(ground, 15, 50, 3000);
        Require(inert == 0, $"a ground state reacted {inert} times");
        var (cold, _) = Held(hot, -5, 1, 20000);
        var (warm, _) = Held(hot, 40, 1, 20000);
        Require(warm > 2 * cold, $"warm {warm} vs cold {cold}");
        P.ReactK = 0;
        var (off, _) = Held(hot, 15, 20, 2000);
        Require(off == 0, "ReactK 0 still reacts");
        P.ReactK = ParamRegistry.Find("ReactK").Default is var dk ? (float)dk : 0;

        // Lying in the cell: reacts on the floor, nothing enters the body.
        P.ReactContact = 0.2f;
        int lc = c + 12 * w.W;
        w.C[hot][lc] += 200;
        var body = w.TestAgent(lc, 2, ground, 10);
        var atoms1 = w.ElementBudget(); var e1 = w.EnergyStart();
        long l0 = w.ReactLying;
        for (int t = 0; t < 4000; t++)
        {
            body.Tb = 15;
            body.Enz[0] = new Enzyme { Kind = Enzyme.Photo, A = 0, Amount = 3, Eff = 1, Topt = 15 }; body.EnzN = 1;
            w.ReactiveDamage(body);
        }
        long lying = w.ReactLying - l0;
        Require(lying > 0 && Math.Abs(w.C[hot][lc].D - (200 - lying)) < 1e-9 && Math.Abs(w.C[ground][lc].D - lying) < 1e-9 && body.InvTotal == 10,
            $"lying: {lying} reacted, floor {w.C[hot][lc].D:F0} + {w.C[ground][lc].D:F0}, body {body.InvTotal}");
        BudgetEqual(atoms1, w.ElementBudget(), "reactive damage, lying", 0);
        w.EnergyBalanced(e1, "reactive damage, lying", FBodyDecay);
        w.Die(body, lc, CauseHand);

        // Poisoning: by the law (no mix) the most reactive species; with a mix, exactly those species.
        var pw = Fixture();
        var pa = pw.ElementBudget();
        Require(pw.Catastrophe(new Catastrophe { Kind = CatastropheKind.Poison, X = 50, Y = 50, R = 3, Amount = 40 }, out string err) != null, "poisoning by the law: " + err);
        Require(pw.C[hot][50 * w.W + 50].D == 40, "poisoning by the law did not spray the most reactive species");
        var mix = new float[Chemistry.S]; mix[ground] = 0.25f; mix[(hot + 2) % Chemistry.S] = 0.75f;
        var spec = new Catastrophe { Kind = CatastropheKind.Poison, X = 90, Y = 50, R = 3, Amount = 40, Mix = mix }.Spec();
        var parsed = Primordium.Catastrophe.Parse(spec);
        Require(parsed.Spec() == spec && parsed.Mix[ground] == 0.25f, $"poison spec with a mix does not round trip: {spec}");
        Require(pw.Catastrophe(parsed, out err) != null, "poisoning with a mix: " + err);
        Require(pw.C[ground][50 * w.W + 90].D == 10 && pw.C[(hot + 2) % Chemistry.S][50 * w.W + 90].D == 30, "poisoning with a mix: wrong amounts");
        var pb = pw.ElementBudget();
        for (int e = 0; e < pb.Length; e++) pb[e] -= pw.HandInput[e];
        BudgetEqual(pa, pb, "poisoning", 0);
        ParamRegistry.ResetDefaults();
        int harmful = Enumerable.Range(0, Chemistry.S).Count(s => Harmful(ch, s));
        Console.WriteLine($"PASS reactive damage: one law for all species (most reactive {ch.Formula(hot)}* r={ch.Reactivity[hot]:F2}, {harmful} harmful at the defaults); held {hits} vs {expected:F1} expected, ground state 0, warm {warm} vs cold {cold}, off 0; lying {lying} on the floor; atoms exact, body decay out; poisoning by the law and by a mix booked");
    }


    // The matter library (Materials.cs): properties by the engine's formulas, sampling exact, pouring
    // booked, JSON round trip, mapping onto another chemistry, the hand's chronicle.
    public static void MatterLibraryRegression()
    {
        ParamRegistry.ResetDefaults();
        var w = Fixture(); var ch = w.Chem;
        int c = 60 * w.W + 60;
        // A mixed block laid directly: the card's numbers are the block's.
        int a = ch.Solids.Length > 0 ? ch.Solids[0] : 2, b = (a + 3) % Chemistry.S == ch.Gas ? (a + 5) % Chemistry.S : (a + 3) % Chemistry.S;
        byte order = 200;
        var mix = new double[Chemistry.S]; mix[a] = 0.6; mix[b] = 0.4;
        var props = MatterRecipe.Props(ch, mix, order);
        var counts = new ushort[Chemistry.S];
        int total = (int)props.MolPerBlock;
        counts[a] = (ushort)Math.Round(total * 0.6); counts[b] = (ushort)(total - counts[a]);
        w.PutMixture(c * w.Z + 2, counts, order);
        int v = c * w.Z + 2;
        double cohesion = w.VoxelCohesion(v);
        Require(Math.Abs(props.Cohesion - cohesion) < 0.02 * cohesion + 1e-4, $"cohesion {props.Cohesion:F4} vs block {cohesion:F4}");
        Require(Math.Abs(props.Barrier - w.VoxelBarrier(v)) < 0.03 * w.VoxelBarrier(v), $"barrier {props.Barrier:F3} vs block {w.VoxelBarrier(v):F3}");
        float fill = w.Fill(v);
        Require(fill > 0.97f && Math.Abs(props.Compression * fill * fill - w.CompressionCapacity(v)) < 0.04 * w.CompressionCapacity(v), $"compression {props.Compression:F2}·{fill:F3}² vs block {w.CompressionCapacity(v):F2}");
        Require(Math.Abs(MatterRecipe.PackedVolume(ch, a, order) - w.PackedVolume(a, order)) < 1e-5, "packed volume differs from World.PackedVolume");
        // Sampled exactly.
        var sample = MatterRecipe.SampleBlock(w, c);
        var smix = sample.MixOf(ch);
        Require(sample.Order == order && Math.Abs(smix[a] - counts[a] / (double)(counts[a] + counts[b])) < 1e-6, "the sample is not the block");
        // JSON round trip, and the same chemistry resolves exactly.
        var back = MatterRecipe.FromJson(sample.ToJson());
        var res = back.Resolve(w);
        Require(res.Exact && Math.Abs(res.Mix[a] - smix[a]) < 1e-6 && back.Order == order, "round trip changed the recipe");
        // Another chemistry: every part goes to some species there, with a warning; forced onto its own
        // chemistry (a signature that differs only in a species the recipe does not use) each part maps to itself.
        var other = new World(5, 0, false);
        var mapped = back.Resolve(other);
        Require(!mapped.Exact && mapped.Warning != null && mapped.Map.Count == 2 && Math.Abs(mapped.Mix.Sum() - 1) < 1e-9, "mapping onto another chemistry");
        int unused = Enumerable.Range(0, Chemistry.S).First(s => s != a && s != b);
        back.Signature.Species[unused].E += 1;
        var self = back.Resolve(w);
        Require(!self.Exact && self.Map.All(m => m.distance < 1e-6) && Math.Abs(self.Mix[a] - smix[a]) < 1e-6, "mapping onto the same molecules is not the identity");
        // Pouring a recipe: atoms and bond energy booked by the hand, blocks at the recipe's order.
        var atoms0 = w.ElementBudget(); var e0 = w.EnergyStart();
        int px = 100, py = 100;
        w.BeginStroke(1, px, py, "test", "тест");
        long poured = 0;
        for (int k = 0; k < 8; k++) poured += w.PourMatter(px, py, 3, mix, 240, false, 0.5f);
        long loose = w.PourMatter(px + 20, py, 2, mix, 0, true, 0.5f);
        var atoms1 = w.ElementBudget();
        for (int e = 0; e < atoms1.Length; e++) atoms1[e] -= w.HandInput[e];
        BudgetEqual(atoms0, atoms1, "pour a recipe", 0);
        w.EnergyBalanced(e0, "pour a recipe", FHand);
        int pc = py * w.W + px, top = pc * w.Z + w.Height[pc] - 1;
        Require(poured > 0 && w.Height[pc] > 2 && w.Order[top] == 240 && w.VoxelCount(top, a) > 0 && w.VoxelCount(top, b) > 0, "the poured block is not the recipe");
        Require(loose > 0 && w.C[a][py * w.W + px + 20].D > 0 && w.Height[py * w.W + px + 20] == 2, "loose pouring laid a block");
        int events = w.Chronicle.All().Count();
        Require(w.EndStroke() && w.Chronicle.All().Count() == events + 1 && w.Chronicle.All().Last().Type == EvType.Player, "the stroke is not in the chronicle");
        Require(!w.EndStroke(), "an empty stroke was chronicled");
        // Starters: computed for every seed from its chemistry.
        foreach (var world in new[] { w, other })
        {
            var starters = MatterRecipe.Starters(world);
            Require(starters.Count >= 6 && starters.All(r => r.Parts.Count > 0 && Math.Abs(r.MixOf(world.Chem).Sum() - 1) < 1e-9), "starters");
            var poison = starters.First(r => r.Kind == "poison");
            var inert = starters.First(r => r.Kind == "mixture");
            Require(MatterRecipe.Props(world.Chem, poison.MixOf(world.Chem), poison.Order).HarmVsDecay > MatterRecipe.Props(world.Chem, inert.MixOf(world.Chem), inert.Order).HarmVsDecay, "the reactive starter is not more harmful than the inert one");
        }
        Console.WriteLine($"PASS matter library: card = block (cohesion {cohesion:F3}, barrier {props.Barrier:F2}, {props.MolPerBlock:F0} molecules/block at order {order}), sample exact, JSON round trip, mapping (other seed {mapped.Map.Count} parts, own chemistry identity), pour {poured}+{loose} molecules booked, stroke chronicled, starters for 2 seeds");
    }
}
