using System;

namespace Primordium;

// Body energy as matter (P.MatterEnergy 1; docs/DESIGN-ENERGY-AS-MATTER.md). A body's usable energy is
// its charge: the excitation of the molecules it holds, Q = Σ n_s·x_s over excited species (x_s = E[s] −
// E[ground of s], Chemistry.Gap; n_s = Inv + Pend, exact in Qty). Nothing is a dedicated "ATP": any
// formula's excited state is a carrier, and which ones a body charges and spends is set by its proteins.
//
//   paying      a cost (Dissipate) is a debt for the tick (Agent.Due), settled (Settle) by relaxing carriers
//               s* → s in Qty: the released energy is the cost's heat, booked exactly (m·x in 2⁻³² units).
//               Order: the strongest splitting protein for that carrier first (a body's "ATPase"), without
//               proteins the more weakly held (lower Bond) first. Stored fuel — ground compounds whose split
//               is exothermic — burns by itself only for a body with a splitting protein for it (metabolism):
//               its energy is captured like any reaction's and then spent.
//   charging    photons excite molecules (Photo, as with the law off); an exothermic reaction in the body
//               (bind, split, burnt fuel) warms it by P.CaptureHeat and excites its ground molecules with the
//               rest (Capture): first the one its light-capture protein is for, then the most plentiful. A
//               fully charged body cannot take more: the rest becomes its held heat.
//   uphill      an endothermic reaction relaxes carriers into the product's bonds (no heat).
//   moving      molecules carry their excitation: eaten, torn out, shared over a link, given to a child,
//               left in remains — no store travels on its own.
// Agent.Energy is only a legacy remainder (a world switched to the law, an old file): spent first, never
// refilled. With the law off every call here leads to the old code.
public sealed partial class World
{
    public static bool MatterLaw => P.MatterEnergy != 0;
    const double QOne = Qty.One;

    // ---- what a body holds ----

    // Its charge in 2⁻³² units, exactly.
    long ChargeRaw(Agent a)
    {
        long q = 0;
        var gap = Chem.Gap;
        foreach (int s in Chem.Excited)
        {
            long n = ((long)a.Inv[s] << Qty.Bits) + a.Pend[s].Raw;
            if (n != 0) q += n * gap[s];
        }
        return q;
    }

    // The excitation it holds (law 1: what it can spend at once by relaxation).
    public double Charge(Agent a) => ChargeRaw(a) / QOne;

    // How much charge would fit if every molecule that has an excited state were excited (law 1's store).
    public double Capacity(Agent a)
    {
        long q = 0;
        var gap = Chem.Gap;
        foreach (int s in Chem.Excited)
        {
            int g = Chemistry.Ground(s);
            long n = ((long)(a.Inv[s] + a.Inv[g]) << Qty.Bits) + a.Pend[s].Raw + a.Pend[g].Raw;
            if (n != 0) q += n * gap[s];
        }
        return q / QOne;
    }

    // Stored fuel its proteins can turn into charge: Σ n·SplitEnergy·EnergyK·(1 − CaptureHeat) over fuel species
    // it has a splitting protein for.
    public double FuelCharge(Agent a)
    {
        if (a.EnzN == 0) return 0;
        double f = 0;
        foreach (int s in Chem.Fuel)
        {
            long n = ((long)a.Inv[s] << Qty.Bits) + a.Pend[s].Raw;
            if (n <= 0 || !HasSplitter(a, s)) continue;
            f += n / QOne * Chem.SplitEnergy(s) * P.EnergyK;
        }
        return f * (1 - P.CaptureHeat);
    }

    bool HasSplitter(Agent a, int s)
    {
        for (int k = 0; k < a.EnzN; k++) if (a.Enz[k].Kind == Enzyme.Split && a.Enz[k].A == s) return true;
        return false;
    }

    // What the body can spend now: law 0 its energy; law 1 the legacy remainder, its charge and the fuel its
    // proteins can burn, less what it already owes this tick.
    public double Avail(Agent a) => !MatterLaw ? a.Energy : Math.Max(0, a.Energy) + Charge(a) + FuelCharge(a) - a.Due;

    // The energy it holds (for the view and statistics): law 0 its energy, law 1 the remainder and its charge.
    public double Held(Agent a) => !MatterLaw ? a.Energy : Math.Max(0, a.Energy) + Charge(a);

    // Law 1: whole molecules as a body counts them, with the fractional parts of its kinds (a molecule
    // half relaxed is split between two kinds' fractions; it is still there). Law 0: InvTotal.
    public int BodyUnits(Agent a)
    {
        if (!MatterLaw) return a.InvTotal;
        long f = 0;
        for (int s = 0; s < Chemistry.S; s++) f += a.Pend[s].Raw;
        return a.InvTotal + (int)(f >> Qty.Bits);
    }

    // Has it run out (the end of its tick, a fall)? Law 0: no energy left; law 1: a debt it could not settle.
    bool Starved(Agent a)
    {
        if (!MatterLaw) return a.Energy <= 0;
        Settle(a);
        return a.Due > 0;
    }

    // Law 1: can the remainder and the carriers it holds — without the whole molecules s1 (and s2) it is about
    // to use as reactants — pay `need` into an uphill reaction? (Law 0: its check is on Energy alone.)
    bool CarriersCover(Agent a, double need, int s1, int s2)
    {
        if (!MatterLaw) return true;
        long q = ChargeRaw(a) - ((long)Chem.Gap[s1] << Qty.Bits) - (s2 >= 0 ? (long)Chem.Gap[s2] << Qty.Bits : 0);
        return Math.Max(0, a.Energy) + q / QOne >= need;
    }

    // ---- moving fractional molecules ----

    // A part m of the body's molecules of kind `from` becomes `to1` (+ `to2`): relaxation (s* → s), excitation
    // (s → s*) or a split (s → A + B), in Qty. The same Qty leaves one kind and enters the other(s), so atoms
    // are exact; whole molecules are broken into the fraction (Pend) as needed and fractions that reach a
    // whole molecule join Inv, keeping 0 ≤ Pend < 1. The caller makes sure the body holds m of `from`.
    void Shift(Agent a, int from, int to1, int to2, Qty m)
    {
        if (m.Raw <= 0) return;
        var pend = a.Pend;
        pend[from] -= m;
        while (pend[from].Raw < 0) { WholeOut(a, from); pend[from] += 1; }
        pend[to1] += m;
        while (pend[to1].Raw >= 1L << Qty.Bits) { pend[to1] -= 1; WholeIn(a, to1); }
        if (to2 >= 0)
        {
            pend[to2] += m;
            while (pend[to2].Raw >= 1L << Qty.Bits) { pend[to2] -= 1; WholeIn(a, to2); }
        }
        // Mass and room follow the kinds (an excited molecule weighs and fills what its ground state does).
        float dm = Chem.Mass[to1] + (to2 >= 0 ? Chem.Mass[to2] : 0) - Chem.Mass[from];
        float dv = Chem.BodyVolume[to1] + (to2 >= 0 ? Chem.BodyVolume[to2] : 0) - Chem.BodyVolume[from];
        if (dm != 0) a.Mass += m.F * dm;
        if (dv != 0) a.Volume += m.F * dv;
    }

    // A whole molecule leaves or joins the count of kind s; its mass and room stay with the body (it moved
    // between the whole count and the fraction).
    void WholeOut(Agent a, int s)
    {
        a.Inv[s]--; a.InvTotal--;
        if (Chem.SplitExo[s]) a.Unstable--;
        if (Chem.Solid[s]) a.Solids--;
    }
    void WholeIn(Agent a, int s)
    {
        a.Inv[s]++; a.InvTotal++;
        if (Chem.SplitExo[s]) a.Unstable++;
        if (Chem.Solid[s]) a.Solids++;
    }

    // m of kind s leaves body `a` for body `b` (links, sharing): the same Qty, mass and room go along.
    void Hand(Agent a, Agent b, int s, Qty m)
    {
        if (m.Raw <= 0) return;
        a.Pend[s] -= m;
        while (a.Pend[s].Raw < 0) { WholeOut(a, s); a.Pend[s] += 1; }
        b.Pend[s] += m;
        while (b.Pend[s].Raw >= 1L << Qty.Bits) { b.Pend[s] -= 1; WholeIn(b, s); }
        float mass = m.F * Chem.Mass[s], room = m.F * Chem.BodyVolume[s];
        a.Mass -= mass; a.Volume -= room;
        b.Mass += mass; b.Volume += room;
    }

    static long Have(Agent a, int s) => ((long)a.Inv[s] << Qty.Bits) + a.Pend[s].Raw;

    // ---- spending ----

    // Settle what the body owes this tick (law 1; nothing to do with the law off or no debt): the legacy
    // remainder first, then carriers, then fuel its proteins burn. What it cannot pay stays owed (Starved).
    public void Settle(Agent a)
    {
        if (a.Due <= 0) return;
        double c = a.Due;
        a.Due = 0;
        double got = Relax(a, c, true, true);
        if (got < c) a.Due = c - got;
    }

    // Release at least q of the body's energy (less only if it has no more): the remainder, then carriers in
    // its pay order, then (fuel) burnt fuel. heat: the released energy is the heat of costs, into its cell
    // (dissipate); otherwise the caller puts it somewhere (bonds, another body's heat) and only an overshoot
    // of the 2⁻³² grid is heat. Returns what was released.
    double Relax(Agent a, double q, bool heat, bool fuel)
    {
        if (q <= 0) return 0;
        double got = 0;
        if (a.Energy > 0)
        {
            double e0 = a.Energy;
            a.Energy = e0 > q ? e0 - q : 0;
            got = e0 - a.Energy;
        }
        if (got < q) got += RelaxCarriers(a, q - got);
        if (got < q && fuel && a.EnzN > 0 && Burn(a, q - got) > 0) got += RelaxCarriers(a, q - got);
        if (heat) HeatOut(a, got);
        else if (got > q) HeatOut(a, got - q);
        return got;
    }

    // Relax carriers in pay order until `need` is released (exactly ≥ need, on the 2⁻³² grid) or none is left.
    double RelaxCarriers(Agent a, double need)
    {
        Span<int> order = stackalloc int[Chemistry.S / 2];
        int n = PayOrder(a, order);
        long want = (long)Math.Ceiling(need * QOne), got = 0;
        var gap = Chem.Gap;
        for (int k = 0; k < n && got < want; k++)
        {
            int s = order[k], x = gap[s];
            long m = Math.Min(Have(a, s), (want - got + x - 1) / x);
            if (m <= 0) continue;
            Shift(a, s, Chemistry.Ground(s), -1, Qty.FromRaw(m));
            got += m * x;
        }
        return got / QOne;
    }

    // The carriers the body holds, in the order it spends them: by its strongest splitting protein for each
    // (Chance, the protein part only), then the more weakly held (Bond), then the lower species.
    int PayOrder(Agent a, Span<int> order)
    {
        Span<float> key = stackalloc float[Chemistry.S / 2];
        int n = 0;
        foreach (int s in Chem.Excited)
        {
            if (Have(a, s) <= 0) continue;
            float drive = ProteinDrive(a, Enzyme.Split, s);
            int k = n++;
            while (k > 0 && Before(drive, s, key[k - 1], order[k - 1])) { order[k] = order[k - 1]; key[k] = key[k - 1]; k--; }
            order[k] = s; key[k] = drive;
        }
        return n;
    }

    bool Before(float drive, int s, float otherDrive, int other)
    {
        if (drive != otherDrive) return drive > otherDrive;
        if (Chem.Bond[s] != Chem.Bond[other]) return Chem.Bond[s] < Chem.Bond[other];
        return s < other;
    }

    // The best protein of this kind for species s right now (as Chance, without the spontaneous rate).
    float ProteinDrive(Agent a, int kind, int s)
    {
        float best = 0;
        for (int k = 0; k < a.EnzN; k++)
        {
            ref var e = ref a.Enz[k];
            if (e.Kind != kind || e.A != s) continue;
            float d = (a.Tb - e.Topt) / P.EnzWidth;
            float v = e.Amount * e.Eff * MathF.Exp(-d * d);
            if (v > best) best = v;
        }
        return best;
    }

    // Metabolism: burn stored fuel the body has a splitting protein for, the strongest protein first, until
    // its capture gives `need` of charge (or the fuel or room for charge runs out). Returns the energy captured.
    double Burn(Agent a, double need)
    {
        double got = 0, keepShare = (1 - P.CaptureHeat) * P.EnergyK;
        if (keepShare <= 0) return 0;
        for (int tries = 0; tries < Chem.Fuel.Length && got < need; tries++)
        {
            int best = -1;
            float bestDrive = 0;
            foreach (int s in Chem.Fuel)
            {
                if (Have(a, s) <= 0) continue;
                float d = ProteinDrive(a, Enzyme.Split, s);
                if (d > bestDrive) { bestDrive = d; best = s; }
            }
            if (best < 0) break;
            int se = Chem.SplitEnergy(best);
            // Enough to cover what excitation on the grid of whole quanta leaves out (≤ one quantum per kind).
            long m = Math.Min(Have(a, best), (long)Math.Ceiling(((need - got) * QOne + 2 * Chemistry.S) / (se * keepShare)));
            if (m <= 0) break;
            Shift(a, best, Chem.SplitA[best], Chem.SplitB[best], Qty.FromRaw(m));
            double bond = m * (double)se / QOne, de = bond * P.EnergyK;
            if (de != bond) Flows[FScale] += de - bond;   // P.EnergyK ≠ 1 (see World.Energy)
            double before = Charge(a);
            ReleaseGain(a, de);
            if (PredProbe != null) PredationProbe.Add(ref PredProbe.Captured, de * (1 - P.CaptureHeat));   // observation
            double gained = Charge(a) - before;
            got += gained;
            if (gained <= 0) break;   // no room for charge: what burns now is only heat
        }
        return got;
    }

    // Costs' heat into the body's cell (and the cave air under a roof), booked as dissipate.
    void HeatOut(Agent a, double heat)
    {
        if (heat <= 0) return;
        int cell = a.Y * W + a.X;
        heatIn[cell] += (float)heat;
        if (a.Z < Height[cell] && CaveLaw) caveHeatIn[cell] += (float)heat * Cover(cell, a.Z);
        Flows[FDissipate] += heat;
    }

    // ---- charging ----

    // An exothermic reaction in the body released de (law 1): CaptureHeat of it warms the body (held heat,
    // shed as it cools), the rest is captured (Capture).
    void ReleaseGain(Agent a, double de)
    {
        double keep = de * (1.0 - P.CaptureHeat);
        WarmBy(a, de - keep);
        Capture(a, keep);
    }

    // q of energy captured by the body: first its debt for the tick is paid (that heat goes to its cell), then
    // ground molecules are excited (Excite); what finds no molecule to excite becomes held heat.
    void Capture(Agent a, double q)
    {
        if (q <= 0) return;
        if (a.Due > 0)
        {
            double t = Math.Min(q, a.Due);
            a.Due -= t;
            HeatOut(a, t);
            q -= t;
        }
        q -= Excite(a, q);
        WarmBy(a, q);
    }

    // Reaction heat kept in the body (it warms Tb; the cells get it as the body cools, LiveBody).
    void WarmBy(Agent a, double q)
    {
        if (q <= 0) return;
        double h0 = a.HeatHeld;
        a.HeatHeld += q;
        a.Tb += (float)q * P.HeatCapK / (P.HeatCapMass + a.Mass);
        Flows[FRounding] += q - (a.HeatHeld - h0);   // doubles round too (see World.Energy)
    }

    // Excite the body's ground molecules with up to q (on the 2⁻³² grid, never more than q): first the one its
    // light-capture protein is for (the same protein couples the energy in), then the most plentiful, then the
    // lower species. Returns the energy now held as excitation.
    double Excite(Agent a, double q)
    {
        long left = (long)Math.Floor(q * QOne);
        if (left <= 0) return 0;
        long used = 0;
        var gap = Chem.Gap;
        Span<int> order = stackalloc int[Chemistry.S / 2];
        int n = ChargeOrder(a, order);
        for (int k = 0; k < n && left > 0; k++)
        {
            int g = order[k], up = Chem.PhotoUp[g], x = gap[up];
            long m = Math.Min(Have(a, g), left / x);
            if (m <= 0) continue;
            Shift(a, g, up, -1, Qty.FromRaw(m));
            left -= m * x; used += m * x;
        }
        return used / QOne;
    }

    int ChargeOrder(Agent a, Span<int> order)
    {
        Span<float> key = stackalloc float[Chemistry.S / 2];
        Span<long> have = stackalloc long[Chemistry.S / 2];
        int n = 0;
        for (int g = 0; g < Chemistry.S; g += 2)
        {
            int up = Chem.PhotoUp[g];
            if (up < 0 || Chem.Gap[up] <= 0) continue;
            long h = Have(a, g);
            if (h <= 0) continue;
            float drive = ProteinDrive(a, Enzyme.Photo, g);
            int k = n++;
            while (k > 0 && (drive > key[k - 1] || (drive == key[k - 1] && h > have[k - 1])))
            { order[k] = order[k - 1]; key[k] = key[k - 1]; have[k] = have[k - 1]; k--; }
            order[k] = g; key[k] = drive; have[k] = h;
        }
        return n;
    }

    // Carriers worth `e` from body `a` to body `b` (share, links), in a's pay order; returns the excitation
    // moved (on the 2⁻³² grid: at least e if a has it).
    double HandCharge(Agent a, Agent b, double e)
    {
        if (e <= 0) return 0;
        Span<int> order = stackalloc int[Chemistry.S / 2];
        int n = PayOrder(a, order);
        long want = (long)Math.Ceiling(e * QOne), got = 0;
        for (int k = 0; k < n && got < want; k++)
        {
            int s = order[k], x = Chem.Gap[s];
            long m = Math.Min(Have(a, s), (want - got + x - 1) / x);
            if (m <= 0) continue;
            Hand(a, b, s, Qty.FromRaw(m));
            got += m * x;
        }
        return got / QOne;
    }
}
