using System;

namespace Primordium;

// Body physics added for life model 2 (docs/DESIGN-LIFE-MODEL-2.md §11.3, docs/LIFE-MODELS.md): primitives
// any model may call, each booking its own matter and energy exactly — atoms in Qty, energy in the ledger
// (World.Energy) through the same paths as the rest of the body physics. Model 1 never calls them; with
// no body keeping polymers nothing here runs.
//
//   polymer pool   BuildPolymer(a, letters, links)  FreePolymer(a, letters, energy)  HandPolymer(a, b, letters, energy)
//                  ExciteResidue(a, carrier, gap)  RelaxResidue(a, gap)   (Agent.Poly: matter and bond energy)
//   membrane       Exchange(a, cell, s, m)   passive transport of an exact amount, no cost
//   light          CatchPhotons(a, cell, tries)  PhotoCharge(a, g, photon)  PhotoHeat(a, photon)
//   reactions      React(a, in Coupled r)   a reaction and a carrier's charge or discharge as one event
//   force          Thrust(a, dx, dy, work)  a push along any heading for the work given
//   reading        ConcentrationOutside(a, cell, s)
//
// A polymer residue is ResidueRaw (2^−Life2ResidueBits of a molecule) of a ground species; `letters` count
// residues by ground formula (index f is species 2f). The pool's matter is part of the body: its mass and
// room (folded, Chemistry.Volume) are in Agent.Mass/Volume, it is in ElementBudget and AuditEnergy
// (`polymer`), it goes to the soil with the body (Die) and its bond energy then becomes heat.
public sealed partial class World
{
    public static long ResidueRaw => 1L << (Qty.Bits - Math.Clamp(P.Life2ResidueBits, 1, 30));
    public static long LinkRaw => (long)Math.Round(P.Life2Link * Qty.One);
    public const int Letters = Chemistry.S / 2;

    // ---- fractional matter of the body ----

    // raw (2⁻³² molecules) of species s leave the body's count (the caller moves mass and room). The body must hold it.
    void TakeRaw(Agent a, int s, long raw)
    {
        if (raw <= 0) return;
        a.Pend[s] -= Qty.FromRaw(raw);
        while (a.Pend[s].Raw < 0) { WholeOut(a, s); a.Pend[s] += 1; }
    }

    void PutRaw(Agent a, int s, long raw)
    {
        if (raw <= 0) return;
        a.Pend[s] += Qty.FromRaw(raw);
        while (a.Pend[s].Raw >= 1L << Qty.Bits) { a.Pend[s] -= 1; WholeIn(a, s); }
    }

    public static long HaveRaw(Agent a, int s) => Have(a, s);

    // ---- the polymer pool ----

    // Can the body build `links` bonds from these letters now: every monomer at hand (ground states, Inv + Pend)
    // and the charge for the bonds (law 1)?
    public bool CanBuildPolymer(Agent a, ReadOnlySpan<int> letters, long links)
    {
        long r = ResidueRaw;
        for (int f = 0; f < Letters && f < letters.Length; f++)
            if (letters[f] > 0 && Have(a, 2 * f) + (Chem.PhotoUp[2 * f] == 2 * f + 1 ? Have(a, 2 * f + 1) : 0) < letters[f] * r) return false;
        return Avail(a) - P.EnergyReserve >= PolymerCost(links);
    }

    // The charge one polymer of `links` bonds costs: the bonds' energy over the share that stays in them.
    public static double PolymerCost(long links) => links * (double)LinkRaw / Qty.One / Math.Max(0.05, P.Life2LinkEff);

    // Monomers of the body become a polymer (a protein chain, a genome strand): each residue is ResidueRaw of
    // its letter's ground species, moved from the body's molecules to Agent.Poly — ground molecules first; an
    // excited one relaxes as it is taken and its excitation goes into the bonds. The bonds take the rest of their
    // energy from the body's carriers (World.Charge: relaxed in pay order); Life2LinkEff of what is paid stays
    // in the bonds, the rest is heat in its cell. Law 1 only. Returns false (nothing done) if a monomer or the
    // charge is missing.
    public bool BuildPolymer(Agent a, ReadOnlySpan<int> letters, long links)
    {
        if (!MatterLaw) return false;
        Settle(a);   // what it owes is paid from what it holds before monomers are folded away
        if (!CanBuildPolymer(a, letters, links)) return false;
        double cost = PolymerCost(links);
        if (Math.Max(0, a.Energy) + Charge(a) < cost) return false;
        var poly = a.Poly ??= new Polymers();
        long r = ResidueRaw, released = 0;
        for (int f = 0; f < Letters && f < letters.Length; f++)
        {
            if (letters[f] <= 0) continue;
            int s = 2 * f;
            long m = letters[f] * r, short_ = m - Math.Min(m, Have(a, s));
            if (short_ > 0) { Shift(a, s + 1, s, -1, Qty.FromRaw(short_)); released += short_ * Chem.Gap[s + 1]; }   // excited monomers relax into the chain
            TakeRaw(a, s, m);
            poly.M[s] += Qty.FromRaw(m);
            a.Volume += (float)(m / Qty.One) * (Chem.Volume[s] - Chem.BodyVolume[s]);   // folded: packed (a gas is no longer a bubble)
        }
        long bond = links * LinkRaw;
        double rel = released / Qty.One, carriers = Math.Max(0, cost - rel);
        if (carriers > 0) Relax(a, carriers, false, false);   // what overshoots the 2⁻³² grid is heat (booked there)
        HeatOut(a, rel + carriers - bond / Qty.One);
        poly.Bond += bond;
        return true;
    }

    // A polymer falls apart (decay): its residues return to the body's molecules (exact), the energy of its
    // bonds and excited residues (`energy`, 2⁻³² units) is heat in its cell (booked as body decay).
    public void FreePolymer(Agent a, ReadOnlySpan<int> letters, long energy)
    {
        var poly = a.Poly;
        if (poly == null) return;
        long r = ResidueRaw;
        for (int f = 0; f < Letters && f < letters.Length; f++)
        {
            if (letters[f] <= 0) continue;
            int s = 2 * f;
            long m = letters[f] * r;
            poly.M[s] -= Qty.FromRaw(m);
            PutRaw(a, s, m);
            a.Volume += (float)(m / Qty.One) * (Chem.BodyVolume[s] - Chem.Volume[s]);
        }
        poly.Bond -= energy;
        DecayHeat(a, energy / Qty.One);
    }

    // A polymer goes from body `a` to body `b` whole (a genome copy or protein copies to a child): its matter,
    // mass, room and bond energy.
    public void HandPolymer(Agent a, Agent b, ReadOnlySpan<int> letters, long energy)
    {
        var from = a.Poly;
        if (from == null) return;
        var to = b.Poly ??= new Polymers();
        long r = ResidueRaw;
        for (int f = 0; f < Letters && f < letters.Length; f++)
        {
            if (letters[f] <= 0) continue;
            int s = 2 * f;
            long m = letters[f] * r;
            from.M[s] -= Qty.FromRaw(m);
            to.M[s] += Qty.FromRaw(m);
            float mass = (float)(m / Qty.One) * Chem.Mass[s], room = (float)(m / Qty.One) * Chem.Volume[s];
            a.Mass -= mass; a.Volume -= room;
            b.Mass += mass; b.Volume += room;
        }
        from.Bond -= energy;
        to.Bond += energy;
    }

    // Excitation transfer from a carrier to a residue of one of the body's polymers ("phosphorylation"): a
    // residue's worth (ResidueRaw) of the excited carrier relaxes, the residue takes `gap` × ResidueRaw of
    // energy into the pool, the difference is heat. Only downhill (the carrier's gap ≥ the residue's).
    public bool ExciteResidue(Agent a, int carrier, int gap)
    {
        if (!MatterLaw || a.Poly == null || Chem.Gap[carrier] <= 0 || Chem.Gap[carrier] < gap) return false;
        long r = ResidueRaw;
        if (Have(a, carrier) < r) return false;
        Shift(a, carrier, Chemistry.Ground(carrier), -1, Qty.FromRaw(r));
        a.Poly.Bond += r * gap;
        HeatOut(a, (double)r * (Chem.Gap[carrier] - gap) / Qty.One);
        return true;
    }

    // An excited residue relaxes: its energy is heat.
    public void RelaxResidue(Agent a, int gap)
    {
        if (a.Poly == null || gap <= 0) return;
        long e = ResidueRaw * gap;
        a.Poly.Bond -= e;
        DecayHeat(a, e / Qty.One);
    }

    // Heat of decay into the body's cell (and the cave air under a roof), booked as body decay.
    void DecayHeat(Agent a, double heat)
    {
        if (heat == 0) return;
        int cell = a.Y * W + a.X;
        heatIn[cell] += (float)heat;
        if (a.Z < Height[cell] && CaveLaw) caveHeatIn[cell] += (float)heat * Cover(cell, a.Z);
        Flows[FBodyDecay] += heat;
    }

    // The pool in a save (the model that keeps it writes it: model 2's SyncState).
    public static void SyncPolymers(Sync s, Agent a)
    {
        bool has = a.Poly != null;
        s.V(ref has);
        if (!has) { if (s.Reading) a.Poly = null; return; }
        if (s.Reading) a.Poly = new Polymers();
        s.Q(a.Poly.M);
        s.V(ref a.Poly.Bond);
    }

    // ---- membrane ----

    // Loose molecules of kind s at the body's floor per comfortable room of a cell (P.InvPerCell): the
    // concentration a membrane protein facing out meets.
    public double ConcentrationOutside(Agent a, int cell, int s) => LooseAmount(a, cell, s) / (double)P.InvPerCell;

    // Passive transport through the membrane: m > 0 of kind s from the body's floor into it, m < 0 out onto
    // the floor — at most what is there. No cost (it runs down the gradient the caller computed). Returns
    // the amount moved (signed).
    public Qty Exchange(Agent a, int cell, int s, Qty m)
    {
        if (m.Raw > 0)
        {
            Qty there = Qty.Of(LooseAmount(a, cell, s));
            m = Qty.Min(m, there);
            if (m.Raw <= 0) return Qty.Zero;
            ChangeLoose(a, cell, s, -m);
            PutRaw(a, s, m.Raw);
            a.Mass += m.F * Chem.Mass[s];
            a.Volume += m.F * Chem.BodyVolume[s];
            return m;
        }
        if (m.Raw < 0)
        {
            Qty out_ = Qty.Min(-m, Qty.FromRaw(Have(a, s)));
            if (out_.Raw <= 0) return Qty.Zero;
            TakeRaw(a, s, out_.Raw);
            a.Mass -= out_.F * Chem.Mass[s];
            a.Volume -= out_.F * Chem.BodyVolume[s];
            ChangeLoose(a, cell, s, out_);
            return -out_;
        }
        return Qty.Zero;
    }

    // ---- light ----

    // Up to `tries` photons from the cell's trickle (or the body's canopy store, P.Canopy 1), with the water
    // above and the bodies over it taking their share as in Photo; none under a roof. Returns how many it caught.
    public int CatchPhotons(Agent a, int cell, int tries)
    {
        if (tries <= 0 || InCave(a)) return 0;
        int caught = 0;
        if (CanopyLaw)
        {
            while (caught < tries && a.LightQuota >= 1f) { a.LightQuota -= 1; caught++; }
            return caught;
        }
        cell = BrightestCell(a);
        if (cell < 0) return 0;
        float reach = MathF.Exp(-P.WaterDim * Below(a, cell));
        float shade = ShadeOf(a, cell);
        for (int k = 0; k < tries; k++)
        {
            float ph = Photon[cell];
            if (ph < 1f) break;
            if (shade < 1 && Rng.NextDouble() >= shade) continue;
            Photon[cell] = Math.Max(0, ph - 1);
            if (reach < 1 && Rng.NextDouble() >= reach) continue;
            caught++;
        }
        return caught;
    }

    // A caught photon of energy `photon` (the absorbing pigment's gap) excites one molecule of ground species g
    // (g → g*): the excitation is the molecule's gap, the rest of the photon warms the body. The photon is
    // the ledger's `photo` input. False (nothing done) if the body holds no g or the photon is too small.
    public bool PhotoCharge(Agent a, int g, int photon)
    {
        int up = Chem.PhotoUp[g];
        if (up < 0 || Chem.Gap[up] > photon || Have(a, g) < 1L << Qty.Bits) return false;
        Shift(a, g, up, -1, 1);
        Flows[FPhoto] += photon;
        WarmBy(a, photon - Chem.Gap[up]);
        a.GainPhoto += Chem.Gap[up]; a.TickPhoto += Chem.Gap[up];
        a.NPhoto++;
        return true;
    }

    // A caught photon with nothing to excite: absorbed as heat in the body (still the `photo` input).
    public void PhotoHeat(Agent a, int photon)
    {
        if (photon <= 0) return;
        Flows[FPhoto] += photon;
        WarmBy(a, photon);
    }

    // ---- reactions ----

    // One reaction of the body's molecules, optionally coupled to a carrier as one event (the design's compound
    // event): A (+ B) → P1 (+ P2) for an amount m (≤ the reactants held), and with Carrier ≥ 0 the same amount
    // of a carrier charged (Up: ground → excited, taking energy from the reaction) or discharged (excited →
    // ground, giving energy to it). The energy balance E[reactants] − E[products] ∓ the carrier's gap must be
    // ≥ 0: it warms the body (held heat). Atoms are the chemistry's (the caller passes a balanced reaction:
    // a split SplitA/SplitB or a bind Combine). Returns false (nothing done) if a participant is missing or
    // the energy does not suffice.
    public struct Coupled
    {
        public int A, B, P1, P2, Carrier;
        public bool Up;
        public Qty M;
    }

    public bool React(Agent a, in Coupled r)
    {
        long m = r.M.Raw;
        if (m <= 0) return false;
        if (r.A == r.B ? Have(a, r.A) < 2 * m : Have(a, r.A) < m || (r.B >= 0 && Have(a, r.B) < m)) return false;
        long de = Chem.E[r.A] + (r.B >= 0 ? Chem.E[r.B] : 0) - Chem.E[r.P1] - (r.P2 >= 0 ? Chem.E[r.P2] : 0);
        int carrierTo = -1;
        if (r.Carrier >= 0)
        {
            if (r.Up)
            {
                carrierTo = Chem.PhotoUp[r.Carrier];
                if (carrierTo < 0 || Have(a, r.Carrier) < m || (r.Carrier == r.A && Have(a, r.A) < 2 * m) || (r.Carrier == r.B && Have(a, r.B) < 2 * m)) return false;
                de -= Chem.Gap[carrierTo];
            }
            else
            {
                if (Chem.Gap[r.Carrier] <= 0 || Have(a, r.Carrier) < m || (r.Carrier == r.A && Have(a, r.A) < 2 * m) || (r.Carrier == r.B && Have(a, r.B) < 2 * m)) return false;
                carrierTo = Chemistry.Ground(r.Carrier);
                de += Chem.Gap[r.Carrier];
            }
        }
        if (de < 0) return false;
        var q = Qty.FromRaw(m);
        TakeRaw(a, r.A, m);
        if (r.B >= 0) TakeRaw(a, r.B, m);
        PutRaw(a, r.P1, m);
        if (r.P2 >= 0) PutRaw(a, r.P2, m);
        float dm = Chem.Mass[r.P1] + (r.P2 >= 0 ? Chem.Mass[r.P2] : 0) - Chem.Mass[r.A] - (r.B >= 0 ? Chem.Mass[r.B] : 0);
        float dv = Chem.BodyVolume[r.P1] + (r.P2 >= 0 ? Chem.BodyVolume[r.P2] : 0) - Chem.BodyVolume[r.A] - (r.B >= 0 ? Chem.BodyVolume[r.B] : 0);
        if (dm != 0) a.Mass += q.F * dm;
        if (dv != 0) a.Volume += q.F * dv;
        if (r.Carrier >= 0) Shift(a, r.Carrier, carrierTo, -1, q);
        WarmBy(a, de * (double)m / Qty.One);
        if (de > 0) { a.TickChem += (float)(de * (double)m / Qty.One); }
        return true;
    }

    // ---- force ----

    // A push along (dx, dy) (a unit vector, any heading) for `work` of the body's energy (owed and settled
    // like every cost, ending as heat): the same momentum per work as a model-1 motor push (P.CostPush per
    // unit of mass, more in deep water), into Vx/Vy for the shared movement (Move).
    public void Thrust(Agent a, double dx, double dy, double work)
    {
        if (!(work > 0)) return;
        int cell = a.Y * W + a.X;
        float depth = InWater(cell, a.Z) ? 1 + P.DepthK * Below(a, cell) : 1;
        Dissipate(a, work);
        float dv = (float)(1.05 * work / (P.CostPush * (1 + Math.Max(0, a.Mass)) * depth));
        a.Vx += (float)dx * dv;
        a.Vy += (float)dy * dv;
    }
}
