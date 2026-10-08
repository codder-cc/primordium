using System;
using System.Threading;

namespace Primordium;

// Reactive damage: one law for every species, no poison class. A molecule a body is exposed to — one
// it holds, or one lying loose in a cell it stands on — reacts with the body's protein substrate with
// chance
//     p = ReactK · Reactivity[s] · TempFactor(Tb)          per held molecule per tick
//     p · ReactContact                                     per lying molecule per tick
// Reactivity = mean affinity of the molecule's atoms × the excitation energy it carries (E above its own
// ground state, Chemistry). The reaction spends that excitation: the molecule drops to its ground state
// (the same atoms), the energy goes to the cell as heat (booked as body decay), and one of the body's
// proteins loses ReactWear of its activity — its substrate returns to the body's membrane pool
// (WearProtein), so no atom is created or lost. A body without proteins has nothing to hit. A "poison"
// is whatever this law makes harmful: excited, high-affinity matter; a ground state never is.
//
// Exposure goes only through what a body already touches: what it holds (taken in by the existing
// uptake paths) and what lies on its floor. Nothing seeps in by itself.
//
// Cost and sampling: one held molecule drawn by count stands for all of them (expected events
// Σ n_s·p_s, exact while that is below one a tick), and one excited species drawn at random stands for
// everything lying in one of its cells (× the number of excited species), so the law costs O(1) a body.
// Parallel contract: agent phase, writes only the body, its own cells (loose matter, heatIn) and its
// tile's flows; random numbers from the tile's Rng; counters are Interlocked and observation only.
public sealed partial class World
{
    public static bool ReactLaw => P.ReactK > 0;

    // Reactions since the world was made or loaded (observation only, not saved).
    public long ReactHeld, ReactLying;

    // Chance per tick that one held molecule of kind s reacts with the proteins of a body at Tb.
    public static float ReactChance(Chemistry ch, int s, float tb) => P.ReactK * ch.Reactivity[s] * TempFactor(tb);

    // Harmful by the law: one held molecule wears proteins at least as fast as they decay on their own
    // (P.EnzDecay), at 15 °C. A label computed from the law, not a class the law reads.
    public static bool Harmful(Chemistry ch, int s) => ch.Reactivity[s] > 0 && ReactChance(ch, s, 15) * P.ReactWear >= P.EnzDecay;

    void ReactiveDamage(Agent a)
    {
        float tf = TempFactor(a.Tb);
        int home = a.Y * W + a.X;
        if (a.InvTotal > 0)
        {
            int s = RandomMol(a);
            float r = Chem.Reactivity[s];
            if (r > 0 && Rng.NextDouble() < P.ReactK * r * tf * a.InvTotal)
            {
                RemoveMol(a, s);
                AddMol(a, Chemistry.Ground(s));
                Spend(home, Chem.Excitation[s]);
                WearProtein(a, Rng.Next(a.EnzN), 1 - P.ReactWear);
                Interlocked.Increment(ref ReactHeld);
            }
        }
        if (P.ReactContact <= 0) return;
        var excited = Chem.Excited;
        int pc = FootCell(a, Rng.Next(a.Cells));
        int q = excited[Rng.Next(excited.Length)];
        float lying = LooseAmount(a, pc, q);
        if (lying < 1f) return;
        if (Rng.NextDouble() < P.ReactK * P.ReactContact * Chem.Reactivity[q] * tf * lying * excited.Length)
        {
            ChangeLoose(a, pc, q, -1f);
            ChangeLoose(a, pc, Chemistry.Ground(q), 1f);
            Spend(pc, Chem.Excitation[q]);
            WearProtein(a, Rng.Next(a.EnzN), 1 - P.ReactWear);
            Interlocked.Increment(ref ReactLying);
        }

        void Spend(int cell, float energy)
        {
            heatIn[cell] += energy;
            Flows[FBodyDecay] += energy;
        }
    }
}
