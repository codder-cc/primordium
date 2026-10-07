using System;
using System.Threading;

namespace Primordium;

// Wear of the matter a body holds: two laws of chemistry, each with its own switch (P.PhotoDamage,
// P.WearK; 0 — off, and the world goes exactly as before).
//
// Why. Without them a body is an eternal battery: light lifts a molecule it holds to the excited
// state (D1 → D1*), the relaxation (D1* → D1) gives the energy back, and nothing is used up — most
// bodies never take in a molecule in their whole life (CHANGELOG (13)), so no resource made of matter
// (local gas, loose food, a deep element, shelter) can matter to them.
//
// What a body is here: the molecules it holds (Agent.Inv), kept together only by their cohesion
// (Chemistry.Bond) — there is no membrane besides that. A molecule that loses its hold on the body is
// no longer part of it:
//  - if it has an allowed breakdown that gives energy (Chemistry.SplitA/SplitB with SplitEnergy ≥ 0:
//    the dissociation of a composite molecule, the relaxation of an excited monomer), it goes that way:
//    the energy becomes heat in the body's cell (the ledger's `body decay` output, like the old
//    spontaneous decay, P.DecayK) and the products leave the body for its floor (loose matter, or the
//    air's pool for the gas: World.ChangeLoose, as frost does);
//  - otherwise (a ground monomer has no breakdown; an uphill split would create energy) it leaves
//    whole, with its bond energy — matter moves between two reservoirs of the ledger, no flow.
// Atoms are moved, never made: one molecule out, its products in, as whole molecules.
//
// A. Photodamage (photolysis): a photon caught by a body (World.Photo) leaves its excitation energy
// x = E[excited] − E[ground] in one molecule. With chance PhotoDamage · e^(−PhotoHold · hold / x) that
// energy breaks the molecule's hold instead of being stored: hold = the molecule's own cohesion
// (Bond of the excited state) + PhotoCage × the cohesion of the matrix around it (the mean Bond of
// what the body holds × how packed it is, min(1, molecules / Room): a cage of strongly bound matter
// keeps the fragments together). Nothing is scripted as protection: a body is shielded by what it is
// made of (strong photoactive molecules, a dense strongly bound matrix — which costs mass upkeep) and
// repairs itself by taking the products back in (they lie at its feet, intake costs energy) or by
// rebinding them with a protein (World.Bind).
//
// B. Wear (thermal and chemical ageing): every molecule a body holds loses its hold with chance
// WearK · TempFactor(Tb) · e^(−WearHold · Bond) per tick — weakly bound matter goes first, warmth speeds
// it up as it speeds every reaction (the cold of a cave slows it). It extends the old spontaneous decay
// (DecayK, unstable molecules break inside the body and stay there), which is left as it was.
//
// Parallel contract: both run in the agent phase on the body itself; they write only its own cell
// (loose matter, heatIn) and its own tile's flows; random numbers from the tile's Rng (drawn only
// with the law on); the counters below are Interlocked. Observation counters are not saved (like the
// profile): they restart at zero after a load and never steer the world.
public sealed partial class World
{
    public static bool PhotoDamageLaw => P.PhotoDamage > 0;
    public static bool WearLaw => P.WearK > 0;

    // Molecules that left bodies by each law since the world was made or loaded (observation only).
    public long LostPhoto, LostWear;

    // A molecule loses its hold on the body: it leaves along its own exothermic breakdown, or whole.
    void LoseMolecule(Agent a, int cell, int s)
    {
        RemoveMol(a, s);
        int x = Chem.SplitA[s], y = Chem.SplitB[s], de = Chem.SplitEnergy(s);
        if (x >= 0 && de >= 0)
        {
            ChangeLoose(a, cell, x, 1);
            if (y >= 0) ChangeLoose(a, cell, y, 1);
            if (de > 0)
            {
                heatIn[cell] += de;
                Flows[FBodyDecay] += de;
            }
        }
        else ChangeLoose(a, cell, s, 1);
    }

    // How firmly the matrix of a body holds a molecule besides its own cohesion: the mean Bond of the
    // molecules it holds times how packed it is (min(1, InvTotal / Room)).
    float MatrixHold(Agent a)
    {
        if (a.InvTotal == 0) return 0;
        float b = 0;
        var bond = Chem.Bond;
        for (int s = 0; s < Chemistry.S; s++) if (a.Inv[s] != 0) b += a.Inv[s] * bond[s];
        return b / Math.Max(a.InvTotal, a.Room);
    }

    // The chance that the photon which lifted `ground` to `excited` breaks the excited molecule's hold.
    float PhotoDamageChance(Agent a, int ground, int excited)
    {
        int x = Chem.E[excited] - Chem.E[ground];
        if (x <= 0) return 0;
        float hold = Chem.Bond[excited] + (P.PhotoCage > 0 ? P.PhotoCage * MatrixHold(a) : 0);
        return Math.Min(1f, P.PhotoDamage * MathF.Exp(-P.PhotoHold * hold / x));
    }

    // After a caught photon (World.Photo): the excited molecule may break instead of being stored.
    void PhotoDamageAfter(Agent a, int excited, float chance)
    {
        if (a.Inv[excited] == 0 || Rng.NextDouble() >= chance) return;
        LoseMolecule(a, a.Y * W + a.X, excited);
        Interlocked.Increment(ref LostPhoto);
    }

    // Thermal ageing of the whole body this tick (World.LiveBody).
    void Wear(Agent a, int cell)
    {
        if (a.InvTotal == 0) return;
        Span<float> rate = stackalloc float[Chemistry.S];
        var bond = Chem.Bond;
        float hold = P.WearHold;
        double total = 0;
        for (int s = 0; s < Chemistry.S; s++)
        {
            if (a.Inv[s] == 0) continue;
            rate[s] = a.Inv[s] * MathF.Exp(-hold * bond[s]);
            total += rate[s];
        }
        double expected = total * P.WearK * TempFactor(a.Tb);
        int n = (int)expected;
        if (Rng.NextDouble() < expected - n) n++;
        for (int k = 0; k < n && a.InvTotal > 0; k++)
        {
            double r = Rng.NextDouble() * total;
            int s = 0;
            for (; s < Chemistry.S - 1; s++)
            {
                if (a.Inv[s] == 0) continue;
                if (r < rate[s]) break;
                r -= rate[s];
            }
            while (a.Inv[s] == 0) s--;   // rounding at the end of the list: the last species held
            total -= rate[s] / a.Inv[s];
            rate[s] -= rate[s] / a.Inv[s];
            LoseMolecule(a, cell, s);
            Interlocked.Increment(ref LostWear);
        }
    }

    // ---- observation: does a body have to take matter in? (bench metrics, no effect on the world) ----

    public static readonly string[] WearNames = { "took_in", "intake_rate", "matter_rate", "body_mols", "lost_photo", "lost_wear", "deaths_broken" };

    long wearSeenTick, wearSeenPhoto, wearSeenWear;
    int wearSeenPop;

    // took_in: share of living bodies that took in at least one whole molecule (intake) in their life;
    // intake_rate: whole molecules taken in per 1000 ticks of life (Σ intakes / Σ ages); matter_rate: the
    // same with gnawed (mine, soak) and stolen molecules; body_mols: molecules per body; lost_photo /
    // lost_wear: molecules that left bodies by photodamage / wear per 1000 body-ticks since the last
    // census (the population taken as the mean of the two censuses); deaths_broken: bodies that fell
    // apart (fewer than MinBody molecules) since the world began.
    public double[] WearCensus()
    {
        var v = new double[WearNames.Length];
        int pop = 0, took = 0;
        double age = 0, intake = 0, matter = 0, mols = 0;
        foreach (var a in Agents)
        {
            if (a.Dead) continue;
            pop++;
            if (a.NIntake > 0) took++;
            age += a.Age; intake += a.NIntake; matter += a.NIntake + a.NMines + a.NTakes; mols += a.InvTotal;
        }
        if (pop > 0)
        {
            v[0] = took / (double)pop;
            v[1] = intake / Math.Max(1, age) * 1000;
            v[2] = matter / Math.Max(1, age) * 1000;
            v[3] = mols / pop;
        }
        double bodyTicks = (Tick - wearSeenTick) * 0.5 * (pop + wearSeenPop);
        if (bodyTicks > 0)
        {
            v[4] = (LostPhoto - wearSeenPhoto) / bodyTicks * 1000;
            v[5] = (LostWear - wearSeenWear) / bodyTicks * 1000;
        }
        v[6] = DeathsBroken;
        wearSeenTick = Tick; wearSeenPop = pop; wearSeenPhoto = LostPhoto; wearSeenWear = LostWear;
        return v;
    }
}
