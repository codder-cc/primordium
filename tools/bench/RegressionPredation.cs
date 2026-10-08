using System;
using System.Linq;

namespace Primordium;

// Bodies against bodies (World.Vm attack/take/inject, World.Predation): the energy ledger closes over a
// strike and a theft, a body of strong molecules gives up fewer per unit of work, torn molecules carry
// their share of the store, the rest of a strike heats the victim, and a parasite's code is followed from
// the injection to a child and on to a third body (with its chronicle event).
public sealed partial class World
{
    public static void PredationRegression()
    {
        var w = Fixture(); var ch = w.Chem;
        w.TrackHeat = true;
        int c = 60 * W + 60;
        var withE = Enumerable.Range(0, Chemistry.S).Where(s => ch.E[s] > 0).ToArray();
        int soft = withE.OrderBy(s => ch.Bond[s]).First(), hard = withE.OrderByDescending(s => ch.Bond[s]).First();
        Require(ch.Bond[hard] > ch.Bond[soft] * 1.5f, "chemistry without a hard molecule");

        // 1. Shell: the same strikes tear fewer molecules out of a body of strong molecules.
        int TornFrom(int s, out double heat, out double carried)
        {
            heat = carried = 0;
            int got = 0;
            for (int k = 0; k < 6; k++)
            {
                var hunter = w.TestAgent(c, 2, soft, 10);
                var prey = w.TestAgent(c, 2, s, 30);
                prey.Energy = 50;
                hunter.Target = prey;
                int before = hunter.InvTotal;
                var e0 = w.AuditEnergy();
                w.Attack(hunter, c, 200);
                w.EnergyBalanced(e0, "attack", FDissipate);
                got += hunter.InvTotal - before;
                heat += prey.HeatHeld;
                carried += 50 - prey.Energy;
                var e1 = w.AuditEnergy();
                w.Die(hunter, c, CauseHand); w.Die(prey, c, CauseHand);
                w.EnergyBalanced(e1, "clean-up");
            }
            return got;
        }
        int tSoft = TornFrom(soft, out double hSoft, out double cSoft), tHard = TornFrom(hard, out double hHard, out double cHard);
        Require(tSoft > tHard, $"a hard body gave up as many molecules as a soft one ({tHard} vs {tSoft})");
        Require(hHard > hSoft, "the work that tore nothing did not heat the hard victim more");
        Require(cSoft > 0 && cSoft > cHard, $"torn molecules carried no store ({cSoft:F2} soft, {cHard:F2} hard)");

        // 2. Take: work against the hold, the ledger closes.
        {
            var thief = w.TestAgent(c + 1, 2, soft, 10);
            var mark = w.TestAgent(c + 1, 2, soft, 30);
            thief.Target = mark;
            double e = thief.Energy;
            var e0 = w.AuditEnergy();
            for (int k = 0; k < 10; k++) w.Take(thief, c + 1, soft);
            w.EnergyBalanced(e0, "take", FDissipate);
            Require(e - thief.Energy > 10 * P.CostSocial * 5, "taking cost no more than a social act");
        }

        // 3. A parasite: code written into a host of another lineage, inherited by its child, copied on by
        //    the host into a third body — counted, and once in the chronicle.
        {
            var src = w.TestAgent(c + 2, 2, soft, 20);
            var g = new byte[40];
            for (int k = 0; k < g.Length; k++) g[k] = (byte)(17 + k * 5);   // distinctive bytes
            src.SetGenome(g, new byte[g.Length]);
            var host = w.TestAgent(c + 2, 2, soft, 40);
            var third = w.TestAgent(c + 2, 2, soft, 20);
            foreach (var x in new[] { src, host, third }) { x.Established = true; x.Energy = 500; }
            src.Target = host;
            for (int k = 0; k < 60 && host.Foreign == null; k++) w.Inject(src, c + 2, 0, 16);
            Require(host.Foreign != null && host.ForeignFrom == src.Lineage, "injected code was not followed");
            w.Divide(host, c + 2, 0, 4);
            w.Agents.AddRange(w.newborn); w.newborn.Clear();
            var child = w.Agents.Last();
            Require(child.ParentId == host.Id && child.Foreign == host.Foreign && w.ParasiteInherited == 1, "the child did not inherit the parasite's code");
            host.Target = third;
            int at = host.G.AsSpan().IndexOf(host.Foreign);
            for (int k = 0; k < 60 && third.Foreign == null; k++) w.Inject(host, c + 2, at, host.Foreign.Length);
            Require(third.Foreign == host.Foreign && third.ForeignFrom == src.Lineage && w.ParasiteSpreads >= 1, "the host did not copy the parasite on");
            Require(w.Chronicle.All().Count(e => e.Type == EvType.ParasiteSpread) == 1 && w.Chronicle.All().Count(e => e.Type == EvType.FirstParasite) == 1, "parasite events missing from the chronicle");
            w.Cut(third, third.G.AsSpan().IndexOf(third.Foreign), third.Foreign.Length);
            Require(third.Foreign == null, "cutting the code out did not clear it");
        }
        Console.WriteLine($"PASS predation: from {(int)(ch.Bond[soft] * 100) / 100.0}-bond bodies {tSoft} molecules, from {(int)(ch.Bond[hard] * 100) / 100.0}-bond bodies {tHard} (6 strikes of 20); store carried {cSoft:F1}/{cHard:F1}, strike heat {hSoft:F1}/{hHard:F1}; take, attack ledgers close; parasite followed to a child and a third body");
    }
}
