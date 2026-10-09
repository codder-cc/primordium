using System;
using System.Collections.Generic;
using System.Text;

namespace Primordium.Model2;

// Life model 2 ("chem", docs/DESIGN-LIFE-MODEL-2.md): a cell of proteins folded from a polymer genome. Nothing
// in it is a command: proteins are what their sequences fold into (Fold.cs), genes are what the binding law
// makes of the genome (Genes.cs), and every tick the cell is the sum of what its proteins' pockets do with
// the molecules around them — through the body physics only (World.Polymer.cs, World.Charge.cs, Divide),
// which books every atom and every unit of energy. Requires the law MatterEnergy 1 (a body's energy is
// the charge of its molecules); with the law off a model-2 body does nothing and starves.
//
// Each tick (fast step): concentrations inside (the body's molecules per comfortable room) and outside (the
// floor's loose molecules, read only for species an outer pocket binds); for every protein slot the MWC
// share of copies in R by modification state; then, in a fixed order, the expected events of every act —
// channels move an exact amount down the gradient, catalysis and coupling run whole events (stochastic
// rounding, the tile's Rng), pigments catch photons, sites are excited and relax, motors push along the
// heading (R) or turn it (T). Every Life2Every ticks (slow step, phase by id): transcription and synthesis,
// decay, genome copying, and division when the membrane has outgrown the volume.
public sealed class ChemModel : ILifeModel
{
    public const byte ModelId = 2;
    public byte Id => ModelId;
    public string Key => "chem";
    public string Name => Loc.T("protein cell (polymer genome)", "белковая клетка (геном-полимер)");

    static readonly double[] cosH = new double[256], sinH = new double[256];
    static ChemModel()
    {
        double d = 2 * Math.PI / 256, cd = DetMath.Cos(d), sd = DetMath.Sin(d), c = 1, s = 0;
        for (int k = 0; k < 256; k++)
        {
            cosH[k] = c; sinH[k] = s;
            (c, s) = (c * cd - s * sd, c * sd + s * cd);
        }
    }

    // ---- per-call scratch (one Think at a time per thread) ----
    [ThreadStatic] static double[] cIn, cOut, xr, xt, fr;
    [ThreadStatic] static int[] letters;
    [ThreadStatic] static double[] shareBuf, chargedBuf;
    [ThreadStatic] static int[] nBuf;

    static void Scratch()
    {
        if (cIn != null) return;
        cIn = new double[Chemistry.S]; cOut = new double[Chemistry.S];
        xr = new double[64]; xt = new double[64]; fr = new double[64 * 4];
        letters = new int[Chem2.L];
    }

    public static Cell CellOf(Agent a) => a.ModelState as Cell;

    // ---- the tick ----

    public void Think(World w, Agent a, int cell)
    {
        if (!World.MatterLaw) return;
        Scratch();
        var c = Chem2.Of(w.Chem);
        if (a.ModelState is not Cell st) { st = new Cell(); a.ModelState = st; }
        if (st.C != c) { st.C = c; foreach (var sl in st.Slots) sl.Type ??= ProteinType.Of(c, sl.Seq); }
        if (!st.Assembled) { long pt = Tick0(); Assemble(w, a, st, c); Took(6, ref pt); return; }
        Fast(w, a, cell, st, c);
        if (a.Dead) return;
        int every = Math.Max(1, P.Life2Every);
        if ((a.Age + a.Id) % every == 0) { long pt = Tick0(); Slow(w, a, cell, st, c); Took(5, ref pt); }
    }

    double Room(Agent a) => (double)P.InvPerCell * Math.Max(1, a.Cells);

    void Concentrations(World w, Agent a, int cell, Cell st)
    {
        double room = Room(a);
        for (int s = 0; s < Chemistry.S; s++) { cIn[s] = World.HaveRaw(a, s) / Qty.One / room; cOut[s] = double.NaN; }
    }

    double Out(World w, Agent a, int cell, int s)
    {
        double v = cOut[s];
        if (double.IsNaN(v)) cOut[s] = v = w.ConcentrationOutside(a, cell, s);
        return v;
    }

    double LigandConc(World w, Agent a, int cell, int side, int s) => side switch
    {
        ProteinType.Out => Out(w, a, cell, s),
        ProteinType.Tm => 0.5 * (cIn[s] + Out(w, a, cell, s)),
        _ => cIn[s],
    };

    // MWC for one slot: per pocket the summed binding X in R and T (xr, xt at base), and the share in R per
    // modification state (fr[base4 + state]).
    // A chain with a homotypic contact (ProteinType.ContactPocket, law Life2Array) turns with the copies it holds: the
    // unit is the copy and (n − 1)·θ partners (θ = x/(1 + x), x the other copies per room over the contact's K_d), all in
    // one conformation — MWC across copies: a copy's log-odds of T is its own plus the unit's partners' mean
    // (Λ_s = λ_s + (n − 1)·θ·λ̄, λ = β·ΔG_RT(state) + ln(den/num) + θ·β(ΔG_RR − ΔG_TT)). The gain of a receptor array.
    void Mwc(World w, Agent a, int cell, Slot slot, ProteinType.TypeLevel lv, int pb, int fb, double room = 0)
    {
        var t = slot.Type;
        double num = 1, den = 1;
        for (int p = 0; p < t.Pockets.Length; p++)
        {
            ref var pk = ref t.Pockets[p];
            double sr = 0, sT = 0;
            for (int l = 0; l < pk.N; l++)
            {
                double conc = LigandConc(w, a, cell, pk.Side, pk.Lig(l));
                sr += conc * lv.KR[p * ProteinType.MaxLig + l];
                sT += conc * lv.KT[p * ProteinType.MaxLig + l];
            }
            xr[pb + p] = sr; xt[pb + p] = sT;
            num *= 1 + sr; den *= 1 + sT;
        }
        int n = slot.Total;
        if (t.ContactPocket >= 0 && n >= 2 && room > 0)
        {
            double x = (n - 1) / room * lv.ContactK, theta = x / (1 + x), partners = (n - 1) * theta;
            double beta = lv.Beta, lnRatio = DetMath.Log(den / num) + theta * lv.ContactShift, mean = 0;
            for (int s = 0; s < 4; s++) mean += slot.N[s] * (beta * t.DgRT[s] + lnRatio);
            mean /= n;
            for (int s = 0; s < 4; s++)
            {
                double lam = Math.Clamp(beta * t.DgRT[s] + lnRatio + partners * mean, -60, 60);
                fr[fb + s] = 1 / (1 + DetMath.Exp(lam));
            }
            return;
        }
        for (int s = 0; s < 4; s++) fr[fb + s] = num / (num + lv.L[s] * den);
    }

    // Copies in R and in T (by modification state shares).
    static void Split(Slot slot, int fb, out double nR, out double nT)
    {
        nR = 0; nT = 0;
        for (int s = 0; s < 4; s++)
        {
            int n = slot.N[s];
            if (n == 0) continue;
            nR += n * fr[fb + s]; nT += n * (1 - fr[fb + s]);
        }
    }

    static int Round(World w, double lambda)
    {
        if (!(lambda > 0)) return 0;
        int n = (int)Math.Floor(lambda);
        double f = lambda - n;
        if (f > 0 && w.Rng.NextDouble() < f) n++;
        return n;
    }

    // Where the fast and slow steps spend time (bench --model2-demo --k perf --prof; off: nothing is timed).
    public static bool Profile;
    public static readonly long[] Prof = new long[8];
    public static readonly string[] ProfNames = { "concentrations", "leak", "mwc", "acts", "light+motion", "slow", "assemble", "" };
    static long Tick0() => Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
    static void Took(int k, ref long t) { if (!Profile) return; long n = System.Diagnostics.Stopwatch.GetTimestamp(); System.Threading.Interlocked.Add(ref Prof[k], n - t); t = n; }

    void Fast(World w, Agent a, int cell, Cell st, Chem2 c)
    {
        long pt = Tick0();
        var chem = c.Chem;
        int level = Chem2.Level(a.Tb);
        double kspont = P.DecayK * World.TempFactor(a.Tb), room = Room(a), beta = Chem2.Beta(level);
        Concentrations(w, a, cell, st);
        Took(0, ref pt);
        var slots = st.Slots;
        int ns = Math.Min(slots.Count, 64);
        // Pockets of all slots in one scratch run (≤ 64 pockets in all; more slots or pockets are not stepped).
        int pb = 0;
        Span<int> pbase = stackalloc int[64];
        for (int k = 0; k < ns; k++)
        {
            var t = slots[k].Type;
            if (pb + t.Pockets.Length > 64) { ns = k; break; }
            pbase[k] = pb;
            Mwc(w, a, cell, slots[k], t.At(level), pb, k * 4, room);
            pb += t.Pockets.Length;
        }
        Took(2, ref pt);
        double thrust = 0, torque = 0, tumbles = 0, pigment = 0;
        for (int k = 0; k < ns; k++)
        {
            var slot = slots[k];
            if (slot.Total == 0) continue;
            var t = slot.Type;
            var lv = t.At(level);
            int b = pbase[k];
            Split(slot, k * 4, out double nR, out double nT);
            var acts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(t.Acts);
            for (int i = 0; i < acts.Length; i++)
            {
                ref readonly var act = ref acts[i];
                int p = act.Pocket, l = act.Lig;
                double kr = act.Kind == ProteinType.ActKind.Pigment || act.Kind == ProteinType.ActKind.Modify ? 0 : lv.KR[p * ProteinType.MaxLig + l];
                double kt = act.Kind == ProteinType.ActKind.Pigment || act.Kind == ProteinType.ActKind.Modify ? 0 : lv.KT[p * ProteinType.MaxLig + l];
                switch (act.Kind)
                {
                    case ProteinType.ActKind.Channel:
                    {
                        int s = act.Species;
                        double dc = Out(w, a, cell, s) - cIn[s];
                        if (dc == 0) break;
                        double perm = P.Life2Channel * (nR * kr / (1 + xr[b + p]) + nT * kt / (1 + xt[b + p]));
                        double j = perm * dc, cap = Math.Abs(dc) * room / 2;
                        if (Math.Abs(j) > cap) j = Math.Sign(j) * cap;
                        var moved = w.Exchange(a, cell, s, Qty.Of(j));
                        if (moved.Raw != 0)
                        {
                            cIn[s] = World.HaveRaw(a, s) / Qty.One / room; st.Uptake += moved.D;
                            if (chem.Gap[s] > 0) st.Ledger[moved.Raw > 0 ? Cell.LChargeIn : Cell.LChargeOut] += Math.Abs(moved.D) * chem.Gap[s];
                        }
                        break;
                    }
                    case ProteinType.ActKind.Split:
                    {
                        int s = act.Species;
                        double conc = cIn[s];
                        if (conc <= 0) break;
                        double bound = nR * conc * kr / (1 + xr[b + p]) + nT * conc * kt / (1 + xt[b + p]);
                        int n = Round(w, kspont * lv.Cat[i] * bound);
                        for (int e = 0; e < n; e++)
                        {
                            long have = World.HaveRaw(a, s);
                            if (have <= 0) break;
                            var r = new World.Coupled { A = s, B = -1, P1 = chem.SplitA[s], P2 = chem.SplitB[s], Carrier = -1, M = Qty.FromRaw(Math.Min(have, 1L << Qty.Bits)) };
                            if (act.Couple >= 0 && w.Rng.NextDouble() < Occupied(slot, k, act.Couple, act.CoupleLig, lv, b, nR, nT, w, a, cell)) { r.Carrier = act.CoupleSpecies; r.Up = true; if (!w.React(a, r)) r.Carrier = -1; else { st.Reactions++; st.Ledger[Cell.LCapture] += r.M.D * chem.Gap[chem.PhotoUp[r.Carrier]]; continue; } }
                            if (w.React(a, r)) st.Reactions++;
                        }
                        cIn[s] = World.HaveRaw(a, s) / Qty.One / room;
                        break;
                    }
                    case ProteinType.ActKind.Bind:
                    {
                        int s1 = act.Species, s2 = act.Species2, p2 = act.Pocket2, l2 = act.Lig2;
                        if (cIn[s1] <= 0 || cIn[s2] <= 0) break;
                        double k2r = lv.KR[p2 * ProteinType.MaxLig + l2], k2t = lv.KT[p2 * ProteinType.MaxLig + l2];
                        double both = nR * (cIn[s1] * kr / (1 + xr[b + p])) * (cIn[s2] * k2r / (1 + xr[b + p2]))
                                    + nT * (cIn[s1] * kt / (1 + xt[b + p])) * (cIn[s2] * k2t / (1 + xt[b + p2]));
                        int n = Round(w, kspont * lv.Cat[i] * both);
                        int prod = chem.Combine[s1, s2];
                        int de = chem.E[s1] + chem.E[s2] - chem.E[prod];
                        for (int e = 0; e < n; e++)
                        {
                            long m = Math.Min(Math.Min(World.HaveRaw(a, s1), World.HaveRaw(a, s2)), 1L << Qty.Bits);
                            if (s1 == s2) m = Math.Min(m, World.HaveRaw(a, s1) / 2);
                            if (m <= 0) break;
                            var r = new World.Coupled { A = s1, B = s2, P1 = prod, P2 = -1, Carrier = -1, M = Qty.FromRaw(m) };
                            bool coupled = act.Couple >= 0 && (de < 0 || w.Rng.NextDouble() < Occupied(slot, k, act.Couple, act.CoupleLig, lv, b, nR, nT, w, a, cell));
                            if (coupled) { r.Carrier = act.CoupleSpecies; r.Up = de > 0; }
                            if (w.React(a, r)) { st.Reactions++; if (coupled && de > 0) st.Ledger[Cell.LCapture] += r.M.D * chem.Gap[chem.PhotoUp[r.Carrier]]; }
                            else if (coupled && de > 0) { r.Carrier = -1; if (w.React(a, r)) st.Reactions++; }
                        }
                        cIn[s1] = World.HaveRaw(a, s1) / Qty.One / room; cIn[s2] = World.HaveRaw(a, s2) / Qty.One / room;
                        break;
                    }
                    case ProteinType.ActKind.Pigment:
                        pigment += P.Life2Pigment * slot.Total;
                        break;
                    case ProteinType.ActKind.Pump:
                    {
                        int s = act.Species;
                        double o = Out(w, a, cell, s);
                        if (o <= 0) break;
                        // Loaded from outside, unloaded inside, driven by the carrier's discharge: the net flux runs
                        // while the inside is less than e^{β·gap} the outside (the carrier's gap bounds the work of a
                        // cycle), and the pore loads from inside too — the back flux.
                        double back = cIn[s] * Chem2.Boltz(level, chem.Gap[act.CoupleSpecies]);
                        if (o <= back) break;
                        double carrier = Occupied(slot, k, act.Couple, act.CoupleLig, lv, b, nR, nT, w, a, cell);
                        double held = nR * kr / (1 + xr[b + p]) + nT * kt / (1 + xt[b + p]);
                        double j = Math.Min(P.Life2Pump * held * carrier * (o - back), (o - back) * room / 2);
                        if (!(j > 0)) break;
                        double pay = P.CostIntake * (1 + a.Packing * a.Packing);
                        var moved = w.Pump(a, cell, s, Qty.Of(j));
                        if (moved.Raw > 0)
                        {
                            cIn[s] = World.HaveRaw(a, s) / Qty.One / room; cOut[s] = double.NaN; st.Uptake += moved.D;
                            st.Ledger[Cell.LPump] += pay * moved.D;
                            if (chem.Gap[s] > 0) st.Ledger[Cell.LChargeIn] += moved.D * chem.Gap[s];
                        }
                        break;
                    }
                    case ProteinType.ActKind.Motor:
                    {
                        int s = act.Species;
                        double conc = cIn[s];
                        if (conc <= 0) break;
                        double cr = nR * conc * kr / (1 + xr[b + p]), ct = nT * conc * kt / (1 + xt[b + p]);
                        thrust += P.Life2Motor * cr * act.Work;
                        torque += P.Life2Motor * ct * act.Work;
                        tumbles += P.Life2Motor * ct;
                        break;
                    }
                    case ProteinType.ActKind.Digest:
                    {
                        // copies holding nothing outside (the floor's own s is too dilute to matter) press the pocket on
                        // whatever body they touch: expected splits per unit of that body's s concentration
                        double free = nR * kr / (1 + xr[b + p]) + nT * kt / (1 + xt[b + p]);
                        if (P.Life2Lysis == 0)
                        {
                            int n0 = w.Digest(a, cell, act.Species, kspont * lv.Cat[i] * free);
                            if (n0 > 0) st.Digested += n0;
                            break;
                        }
                        // Law Life2Lysis 1: a touched molecule of the kind is held by the outer copies as x/(1 + x) (x: copies free
                        // per room over K_d) and split at the Arrhenius rate with the barrier its pocket lowers; the parts are
                        // taken in through the contact, the split's energy charges the coupled carrier or is heat.
                        double x = free / room, held = x / (1 + x);
                        if (!(held > 0)) break;
                        double rate = w.CatalysedSplitRate(act.Species, a.Tb, act.Work) * held;
                        double couple = act.Couple >= 0 ? Occupied(slot, k, act.Couple, act.CoupleLig, lv, b, nR, nT, w, a, cell) : 0;
                        long charged0 = World.DigestCharged;
                        int n = w.Digest(a, cell, act.Species, rate, true, true, act.CoupleSpecies, couple);
                        if (n > 0)
                        {
                            st.Digested += n;
                            st.Ledger[Cell.LPrey] += n * chem.SplitEnergy(act.Species);
                            if (World.DigestCharged > charged0) st.Ledger[Cell.LCapture] += (World.DigestCharged - charged0) * chem.Gap[chem.PhotoUp[act.CoupleSpecies]];
                            cIn[chem.SplitA[act.Species]] = World.HaveRaw(a, chem.SplitA[act.Species]) / Qty.One / room;
                            if (chem.SplitB[act.Species] >= 0) cIn[chem.SplitB[act.Species]] = World.HaveRaw(a, chem.SplitB[act.Species]) / Qty.One / room;
                        }
                        break;
                    }
                    case ProteinType.ActKind.Modify:
                        Modify(w, a, cell, st, slot, k, t, lv, b, act);
                        break;
                    case ProteinType.ActKind.Harvest:
                    {
                        // copies holding the excited molecule outside and the ground carrier inside pass Life2Transfer
                        // excitations a tick (an exact amount, as a channel moves one)
                        int s = act.Species;
                        double o = Out(w, a, cell, s);
                        if (!(o > 0)) break;
                        double held = nR * o * kr / (1 + xr[b + p]) + nT * o * kt / (1 + xt[b + p]);
                        double carrier = Occupied(slot, k, act.Couple, act.CoupleLig, lv, b, nR, nT, w, a, cell);
                        double j = P.Life2Transfer * held * carrier;
                        if (!(j > 0)) break;
                        var moved = w.Harvest(a, cell, s, act.CoupleSpecies, Qty.Of(j));
                        if (moved.Raw > 0)
                        {
                            cOut[s] = double.NaN; cOut[Chemistry.Ground(s)] = double.NaN;
                            int g = act.CoupleSpecies;
                            cIn[g] = World.HaveRaw(a, g) / Qty.One / room; cIn[g + 1] = World.HaveRaw(a, g + 1) / Qty.One / room;
                            st.Ledger[Cell.LHarvest] += moved.D * chem.Gap[chem.PhotoUp[g]];
                        }
                        break;
                    }
                }
            }
        }
        if (P.Life2TransMod != 0) TransModify(w, a, cell, st, c, level, ns, pbase, room);
        Took(3, ref pt);
        if (pigment > 0) Light(w, a, cell, st, Round(w, pigment), pigment);
        if (thrust > 0) { w.Thrust(a, cosH[st.Heading], sinH[st.Heading], thrust, ref st.DriftX, ref st.DriftY); st.Thrusts++; st.Ledger[Cell.LMotor] += thrust; }
        if (torque > 0)
        {
            w.Spend(a, torque);   // turning the body in the medium: work that ends as heat
            st.Ledger[Cell.LTurn] += torque;
            if (w.Rng.NextDouble() < 1 - DetMath.Exp(-tumbles / P.Life2Turn)) { st.Heading = w.Rng.Next(256); st.Tumbles++; }
            Took(4, ref pt);
        }
        else Took(4, ref pt);
    }

    // The bare membrane lets molecules through by themselves, the more hydrophobic the more (Life2Leak·h²), down
    // the gradient: how a cell without channels takes in what lies around it and loses what it holds. Only
    // ground states dissolve in it: an excited molecule is too reactive to cross the lipid (a charge stays in
    // unless a channel lets it out).
    // Diffusion is slow next to what proteins do: it runs in the slow step, for its `ticks` at once (the cap keeps a
    // step from overshooting the equilibrium).
    void Leak(World w, Agent a, int cell, Chem2 c, Cell st, double room, int ticks)
    {
        double k = P.Life2Leak * ticks;
        if (k <= 0) return;
        // the floor read once for all kinds (the same values ConcentrationOutside gives one by one)
        Span<double> all = stackalloc double[Chemistry.S];
        w.ConcentrationsOutside(a, cell, all);
        for (int s = 0; s < Chemistry.S; s++) if (double.IsNaN(cOut[s])) cOut[s] = all[s];
        for (int s = 0; s < Chemistry.S; s += 2)
        {
            double h = c.LigH[s];
            if (h <= 0) continue;
            double o = Out(w, a, cell, s), dc = o - cIn[s];
            if (dc == 0) continue;
            double j = k * h * h * dc * room, cap = Math.Abs(dc) * room / 2;
            if (Math.Abs(j) > cap) j = Math.Sign(j) * cap;
            var moved = w.Exchange(a, cell, s, Qty.Of(j));
            if (moved.Raw != 0) { cIn[s] = World.HaveRaw(a, s) / Qty.One / room; st.Uptake += moved.D; }
        }
    }

    // Occupancy of a pocket's ligand averaged over the slot's copies (R and T).
    double Occupied(Slot slot, int k, int p, int l, ProteinType.TypeLevel lv, int b, double nR, double nT, World w, Agent a, int cell)
    {
        double n = nR + nT;
        if (n <= 0) return 0;
        var pk = slot.Type.Pockets[p];
        double conc = LigandConc(w, a, cell, pk.Side, pk.Lig(l));
        return (nR * conc * lv.KR[p * ProteinType.MaxLig + l] / (1 + xr[b + p]) + nT * conc * lv.KT[p * ProteinType.MaxLig + l] / (1 + xt[b + p])) / n;
    }

    // Photons for the cell's pigments: caught photons go to the pigments by their share; each excites the ground
    // carrier its coupled pocket holds (or warms the body).
    void Light(World w, Agent a, int cell, Cell st, int tries, double total)
    {
        int caught = w.CatchPhotons(a, cell, tries);
        if (caught == 0) return;
        var chem = st.C.Chem;
        int level = Chem2.Level(a.Tb);
        for (int ph = 0; ph < caught; ph++)
        {
            double r = w.Rng.NextDouble() * total;
            ProteinType.Act act = default;
            Slot slot = null; int k = -1;
            for (int s = 0; s < st.Slots.Count && slot == null; s++)
            {
                var t = st.Slots[s].Type;
                foreach (var x in t.Acts)
                {
                    if (x.Kind != ProteinType.ActKind.Pigment) continue;
                    r -= P.Life2Pigment * st.Slots[s].Total;
                    if (r < 0) { act = x; slot = st.Slots[s]; k = s; break; }
                }
            }
            if (slot == null) continue;
            int photon = (int)act.Work;
            st.Photons++;
            if (act.Couple >= 0 && w.PhotoCharge(a, act.CoupleSpecies, photon, out double share)) { st.Ledger[Cell.LPhoto] += chem.Gap[chem.PhotoUp[act.CoupleSpecies]] * share; st.Ledger[Cell.LPhotoHeat] += chem.Gap[chem.PhotoUp[act.CoupleSpecies]] * (1 - share); continue; }
            w.PhotoHeat(a, photon);
            st.Ledger[Cell.LPhotoHeat] += photon;
        }
    }

    // Modification of a site by the carrier its donor pocket holds, and its relaxation.
    void Modify(World w, Agent a, int cell, Cell st, Slot slot, int k, ProteinType t, ProteinType.TypeLevel lv, int b, in ProteinType.Act act)
    {
        int bit = 1 << act.Site, gap = t.SiteGap[act.Site];
        ref var pk = ref t.Pockets[act.Pocket];
        int carrier = -1, li = -1;
        for (int l = 0; l < pk.N; l++)
            if (st.C.Chem.Gap[pk.Lig(l)] >= gap && st.C.Chem.Gap[pk.Lig(l)] > 0) { carrier = pk.Lig(l); li = l; break; }
        // Relaxation first (of what was excited before this tick), then excitation.
        for (int s = 0; s < 4; s++)
        {
            if ((s & bit) == 0 || slot.N[s] == 0) continue;
            int n = Math.Min(slot.N[s], Round(w, slot.N[s] * P.Life2ModRelax));
            for (int e = 0; e < n; e++) { w.RelaxResidue(a, gap); slot.N[s]--; slot.N[s & ~bit]++; }
        }
        if (carrier < 0) return;
        double conc = cIn[carrier];
        if (conc <= 0) return;
        double kr = lv.KR[act.Pocket * ProteinType.MaxLig + li], kt = lv.KT[act.Pocket * ProteinType.MaxLig + li];
        double thr = conc * kr / (1 + xr[b + act.Pocket]), tht = conc * kt / (1 + xt[b + act.Pocket]);
        for (int s = 0; s < 4; s++)
        {
            if ((s & bit) != 0 || slot.N[s] == 0) continue;
            double f = fr[k * 4 + s];
            int n = Math.Min(slot.N[s], Round(w, slot.N[s] * P.Life2Transfer * (f * thr + (1 - f) * tht)));
            for (int e = 0; e < n; e++)
            {
                if (!w.ExciteResidue(a, carrier, gap)) break;
                st.Ledger[Cell.LModify] += st.C.Chem.Gap[carrier] * (double)World.ResidueRaw / Qty.One;
                slot.N[s]--; slot.N[s | bit]++;
            }
        }
    }

    // Excitation transfer between chains (law Life2TransMod, ProteinType.LinksOf): copies of a donor holding a target's
    // window around a site, with an excited carrier in the pocket next to it, excite the site. A target copy is held as
    // x/(1 + x), x = donor copies per room × (its share in R·e^{−βΔG(R face)} + in T·e^{−βΔG(T face)}) / K_0, by the face it
    // presents in its own conformation (so the rate follows the target's state: the adaptation of a receptor by a
    // second protein, Barkai–Leibler); each donor copy turns over at most Life2Transfer a tick (a saturated enzyme works
    // at its own pace). Sites excited only so relax by themselves (Life2ModRelax), their energy heat.
    void TransModify(World w, Agent a, int cell, Cell st, Chem2 c, int level, int ns, Span<int> pbase, double room)
    {
        var slots = st.Slots;
        ulong key = 0xCBF29CE484222325UL;
        for (int k = 0; k < ns; k++) { key ^= slots[k].Type.Hash; key *= 0x100000001B3UL; key = (key << 7) | (key >> 57); }
        key ^= (ulong)ns;
        if (st.Links == null || st.LinkKey != key)
        {
            var list = new List<Cell.LinkRef>();
            for (int e = 0; e < ns; e++)
                for (int t = 0; t < ns; t++)
                    foreach (var l in ProteinType.LinksOf(c, slots[e].Type, slots[t].Type)) list.Add(new Cell.LinkRef { E = e, T = t, L = l });
            st.Links = list.ToArray(); st.LinkKey = key; st.LinkLevel = -1;
        }
        // trans-only sites relax by themselves (a chain's own sites relax in Modify)
        for (int k = 0; k < ns; k++)
        {
            var slot = slots[k];
            var t = slot.Type;
            for (int j = 0; j < t.Sites.Length; j++)
            {
                if (t.SiteDonor[j] >= 0) continue;
                int bit = 1 << j;
                for (int s = 0; s < 4; s++)
                {
                    if ((s & bit) == 0 || slot.N[s] == 0) continue;
                    int n = Math.Min(slot.N[s], Round(w, slot.N[s] * P.Life2ModRelax));
                    for (int e = 0; e < n; e++) { w.RelaxResidue(a, t.SiteGap[j]); slot.N[s]--; slot.N[s & ~bit]++; }
                }
            }
        }
        if (st.Links.Length == 0) return;
        if (st.LinkLevel != level)
        {
            double beta = Chem2.Beta(level);
            if (st.LinkK == null || st.LinkK.Length != 4 * st.Links.Length) st.LinkK = new double[4 * st.Links.Length];
            for (int i = 0; i < st.Links.Length; i++)
            {
                ref var l = ref st.Links[i].L;
                st.LinkK[4 * i] = DetMath.Exp(-beta * l.GRR); st.LinkK[4 * i + 1] = DetMath.Exp(-beta * l.GRT);
                st.LinkK[4 * i + 2] = DetMath.Exp(-beta * l.GTR); st.LinkK[4 * i + 3] = DetMath.Exp(-beta * l.GTT);
            }
            st.LinkLevel = level;
        }
        double k0 = P.Life2Kd0;
        Span<double> lam = stackalloc double[4];
        for (int i = 0; i < st.Links.Length; i++)
        {
            ref var lr = ref st.Links[i];
            var es = slots[lr.E]; var ts = slots[lr.T];
            if (es.Total == 0 || ts.Total == 0) continue;
            var lvE = es.Type.At(level);
            Split(es, lr.E * 4, out double eR, out double eT);
            double carrier = Occupied(es, lr.E, lr.L.Carrier, lr.L.CarrierLig, lvE, pbase[lr.E], eR, eT, w, a, cell);
            if (!(carrier > 0)) continue;
            double fE = eR / Math.Max(1e-12, eR + eT), cE = es.Total / room;
            double xR = cE * (fE * st.LinkK[4 * i] + (1 - fE) * st.LinkK[4 * i + 2]) / k0;
            double xT = cE * (fE * st.LinkK[4 * i + 1] + (1 - fE) * st.LinkK[4 * i + 3]) / k0;
            double thR = xR / (1 + xR), thT = xT / (1 + xT);
            int site = lr.L.Site, bit = 1 << site, gap = ts.Type.SiteGap[site];
            double sum = 0;
            for (int s = 0; s < 4; s++)
            {
                lam[s] = 0;
                if ((s & bit) != 0 || ts.N[s] == 0) continue;
                double f = fr[lr.T * 4 + s];
                lam[s] = ts.N[s] * P.Life2Transfer * carrier * (f * thR + (1 - f) * thT);
                sum += lam[s];
            }
            double cap = es.Total * P.Life2Transfer * carrier;
            double scale = sum > cap ? cap / sum : 1;
            for (int s = 0; s < 4; s++)
            {
                if (lam[s] <= 0) continue;
                int n = Math.Min(ts.N[s], Round(w, lam[s] * scale));
                for (int e = 0; e < n; e++)
                {
                    if (!w.ExciteResidue(a, lr.L.Species, gap)) break;
                    st.Ledger[Cell.LModify] += st.C.Chem.Gap[lr.L.Species] * (double)World.ResidueRaw / Qty.One;
                    ts.N[s]--; ts.N[s | bit]++;
                    st.TransMods++;
                }
            }
        }
    }

    // ---- the slow step ----

    void Table(World w, Agent a, Cell st, Chem2 c)
    {
        ulong th = TypesHash(st);   // (no array made while the types stay the same)
        if (st.Table != null && st.TableGenome == a.Hash && st.TableTypes == th) return;
        var types = TypesOf(st);
        st.Table = GeneTable.For(c, a.G, a.Hash, types);
        st.TableGenome = a.Hash; st.TableTypes = th;
        if (st.Acc.Length != st.Table.Units.Length) Array.Resize(ref st.Acc, st.Table.Units.Length);
    }

    // GeneTable.TypesHash of TypesOf(st), without making the array.
    static ulong TypesHash(Cell st)
    {
        ulong h = 0xCBF29CE484222325UL;
        int n = 0;
        foreach (var s in st.Slots)
        {
            if (s.Total <= 0) continue;
            h ^= s.Type.Hash; h *= 0x100000001B3UL; h = (h << 7) | (h >> 57);
            n++;
        }
        return h ^ (ulong)n;
    }

    static ProteinType[] TypesOf(Cell st)
    {
        int n = 0;
        foreach (var s in st.Slots) if (s.Total > 0) n++;
        var t = new ProteinType[n];
        n = 0;
        foreach (var s in st.Slots) if (s.Total > 0) t[n++] = s.Type;
        return t;
    }

    // Occupancy sum x = Σ c·(f_R·K_R + (1 − f_R)·K_T) of binders (protein concentrations: copies per room).
    // polOnly: polymerases only, each weighted by how often its carrier pocket holds a charged carrier (`charged`,
    // null: always) — copying couples to charge, so a cell short of it copies slowly.
    // er/et: e^{−β·ΔG} of the binders in R and T (GeneTable.At); nOf: copies by type index.
    static double Occupancy(GeneTable.Binder[] list, double[] er, double[] et, int[] nOf, double[] shareR, double room, bool polOnly, double[] charged = null)
    {
        double x = 0, k0 = P.Life2Kd0;
        for (int i = 0; i < list.Length; i++)
        {
            ref var bd = ref list[i];
            if (polOnly && !bd.Pol) continue;
            int n = nOf[bd.Type];
            if (n == 0) continue;
            double f = shareR[bd.Type];
            double y = n / room * (f * er[i] + (1 - f) * et[i]) / k0;
            x += polOnly && charged != null ? y * charged[bd.Type] : y;
        }
        return x;
    }

    static int[] CopiesOf(Cell st, ProteinType[] types, ref int[] buf)
    {
        if (buf == null || buf.Length < types.Length) buf = new int[Math.Max(16, types.Length)];
        for (int k = 0; k < types.Length; k++)
        {
            int n = 0;
            foreach (var s in st.Slots) if (s.Type == types[k]) { n = s.Total; break; }
            buf[k] = n;
        }
        return buf;
    }

    void Slow(World w, Agent a, int cell, Cell st, Chem2 c)
    {
        var chem = c.Chem;
        int every = Math.Max(1, P.Life2Every), level = Chem2.Level(a.Tb);
        double beta = Chem2.Beta(level), room = Room(a);
        Concentrations(w, a, cell, st);
        long pl = Tick0();
        Leak(w, a, cell, c, st, room, every);
        Took(1, ref pl);
        // Decay (proteins wear by their stability, faster when warm).
        double tf = World.TempFactor(a.Tb);
        for (int k = 0; k < st.Slots.Count; k++)
        {
            var slot = st.Slots[k];
            int total = slot.Total;
            if (total == 0) continue;
            var t = slot.Type;
            double p = 1 - DetMath.Exp(-t.At(level).Decay * tf * every);
            int n = Math.Min(total, Round(w, total * p));
            for (int e = 0; e < n; e++)
            {
                int pick = w.Rng.Next(slot.Total), s = 0;
                while (pick >= slot.N[s]) { pick -= slot.N[s]; s++; }
                long energy = t.Len * World.LinkRaw;
                for (int j = 0; j < t.Sites.Length; j++) if ((s >> j & 1) != 0) energy += t.SiteGap[j] * World.ResidueRaw;
                w.FreePolymer(a, t.Letters, energy);
                slot.N[s]--;
                st.Decayed++;
            }
        }
        st.Slots.RemoveAll(s => s.Total == 0);
        if (st.Genomes == 0) { Divide(w, a, cell, st, c); return; }
        Table(w, a, st, c);
        var table = st.Table;
        var types = table.Types;
        // MWC share in R of every type (for its binding to the genome).
        Concentrations(w, a, cell, st);
        if (shareBuf == null || shareBuf.Length < types.Length) { shareBuf = new double[Math.Max(16, types.Length)]; chargedBuf = new double[shareBuf.Length]; }
        var shareR = shareBuf; var charged = chargedBuf;
        Array.Clear(shareR, 0, types.Length); Array.Clear(charged, 0, types.Length);
        var nOf = CopiesOf(st, types, ref nBuf);
        var kl = table.At(level);
        for (int k = 0; k < types.Length; k++)
        {
            Slot slot = null;
            foreach (var s in st.Slots) if (s.Type == types[k]) { slot = s; break; }
            if (slot == null || slot.Total == 0) continue;
            var lv = types[k].At(level);
            Mwc(w, a, cell, slot, lv, 0, 0, room);
            Split(slot, 0, out double nR, out double nT);
            shareR[k] = nR / Math.Max(1e-9, nR + nT);
            // The best occupancy of a pocket of its by an excited carrier (what drives a polymerase).
            var t = types[k];
            for (int p = 0; p < t.Pockets.Length; p++)
                for (int l = 0; l < t.Pockets[p].N; l++)
                {
                    int s = t.Pockets[p].Lig(l);
                    if (chem.Gap[s] <= 0 || t.Pockets[p].Side == ProteinType.Out) continue;
                    double conc = LigandConc(w, a, cell, t.Pockets[p].Side, s), f = shareR[k];
                    double th = f * conc * lv.KR[p * ProteinType.MaxLig + l] / (1 + xr[p]) + (1 - f) * conc * lv.KT[p * ProteinType.MaxLig + l] / (1 + xt[p]);
                    charged[k] = Math.Max(charged[k], th);
                }
        }
        // Transcription and synthesis.
        for (int u = 0; u < table.Units.Length; u++)
        {
            double xp = Occupancy(table.Promoter[u], kl.PR[u], kl.PT[u], nOf, shareR, room, true, charged);
            if (xp <= 0) continue;
            double xall = Occupancy(table.Promoter[u], kl.PR[u], kl.PT[u], nOf, shareR, room, false);
            double xb = Occupancy(table.Block[u], kl.BR[u], kl.BT[u], nOf, shareR, room, false);
            double tau = st.Genomes * P.Life2Tx * xp / (1 + xall) * (1 - xb / (1 + xb));   // every genome copy is a template
            if (u == 0) { st.DiagXp = xp; st.DiagXall = xall; st.DiagTau = tau; }
            st.Acc[u] += tau;
            var unit = table.Units[u];
            while (st.Acc[u] >= 1)
            {
                if (!w.BuildPolymer(a, unit.Type.Letters, unit.Type.Len)) { st.Stalls[Monomers(w, a, unit.Type.Letters, st) ? 1 : 0]++; st.Acc[u] = Math.Min(st.Acc[u], 1); break; }
                Add(st, unit.Type, 1);
                st.Ledger[Cell.LSynth] += World.PolymerCost(unit.Type.Len);
                st.Acc[u] -= 1;
                st.Synth++;
            }
        }
        // Copying the genome: a polymerase at the origin (the first window) copies Life2Pol residues per step.
        if (st.Genomes == 1 && table.Units.Length > 0 && table.Units[0].Start == 0)
        {
            double xo = Occupancy(table.Promoter[0], kl.PR[0], kl.PT[0], nOf, shareR, room, true, charged);
            double xall = Occupancy(table.Promoter[0], kl.PR[0], kl.PT[0], nOf, shareR, room, false);
            int steps = Round(w, P.Life2Pol * xo / (1 + xall));
            if (steps > 0) Copy(w, a, st, c, steps, level, table, types, shareR, room);
        }
        Divide(w, a, cell, st, c);
    }

    static void Add(Cell st, ProteinType t, int n)
    {
        foreach (var s in st.Slots) if (s.Type == t) { s.N[0] += n; return; }
        var slot = new Slot { Type = t, Seq = t.Seq };
        slot.N[0] = n;
        st.Slots.Add(slot);
    }

    // Copy `steps` residues of the genome onto the replica: each letter chosen by the pairing law (Boltzmann
    // over the monomers at hand, sharpened by the polymerase's rigidity), both strands built from monomers
    // with the bonds' charge. Stops where a monomer or the charge runs out.
    void Copy(World w, Agent a, Cell st, Chem2 c, int steps, int level, GeneTable table, ProteinType[] types, double[] shareR, double room)
    {
        int n = GeneTable.Length(a.G);
        if (st.Replica.Length != n)
        {
            if (st.Fork > 0) return;   // a copy of another length under way (cannot happen while letters only change)
            st.Replica = new byte[n]; st.Fork = 0;
        }
        // Rigidity of the best polymerase pocket at the origin: its discrimination D.
        double rho = 0;
        bool proofread = false;   // a polymerase with a second pocket can hold the unpaired end and take a wrong letter off again
        foreach (var bd in table.Promoter[0])
            if (bd.Pol)
            {
                rho = Math.Max(rho, types[bd.Type].Pockets[bd.Pocket].R.Rho);
                int held = 0;
                foreach (var p in types[bd.Type].Pockets) if (p.Side != ProteinType.Tm) held++;
                if (held >= 2) proofread = true;
            }
        double d = c.Discrimination(rho);
        var tab = c.CopyTable(level, d);   // the pairing law's factors (the same numbers, made once)
        int passes = proofread ? Math.Max(0, P.Life2Proofread) : 0;
        double checks = 0;
        Span<double> wgt = stackalloc double[Chem2.L];
        double sum = 0;
        for (int f = 0; f < Chem2.L; f++) { wgt[f] = (World.HaveRaw(a, 2 * f) + World.HaveRaw(a, 2 * f + 1)) / (double)Qty.One; sum += wgt[f]; }   // a monomer at hand, ground or excited (BuildPolymer takes both)
        if (sum <= 0) return;
        int fork = st.Fork;
        Span<int> need = stackalloc int[Chem2.L];
        Span<double> cum = stackalloc double[Chem2.L];
        double perRoom = Qty.One / (double)World.ResidueRaw / room;   // monomers as residues per room
        for (int k = 0; k < steps && fork < n; k++)
        {
            int t = GeneTable.At(a.G, fork);
            double z = 0, top = c.Pair[t, c.Comp[t]];
            for (int b = 0; b < Chem2.L; b++) { z += wgt[b] * perRoom * tab[t * Chem2.L + b]; cum[b] = z; }
            if (z <= 0) break;
            // A step inserts a letter only if a monomer arrives and holds: the chance grows with the monomers at hand
            // weighed by how well they pair (z in units of a perfect partner, against Life2Kd0) — short of the
            // right one, the polymerase mostly waits.
            if (w.Rng.NextDouble() >= z / (P.Life2Kd0 + z)) continue;
            double r = w.Rng.NextDouble() * z;
            int pick = 0;
            while (pick < Chem2.L - 1 && r >= cum[pick]) pick++;
            // Kinetic proofreading (Hopfield): every pass checks the letter in place again (a check takes
            // Life2ProofCost of a bond's charge, right letter or wrong) and takes a wrong one off with 1 − e^{−βD·Δε}, a
            // new one drawn; the discarded insertion's charge is heat. Each pass multiplies the error by ~e^{−βD·Δε}.
            for (int pass = 0; pass < passes; pass++)
            {
                checks += P.Life2ProofCost;
                if (pick == c.Comp[t]) continue;
                if (w.Rng.NextDouble() >= tab[Chem2.L * Chem2.L + t * Chem2.L + pick]) continue;
                checks += 1;
                r = w.Rng.NextDouble() * z;
                pick = 0;
                while (pick < Chem2.L - 1 && r >= cum[pick]) pick++;
            }
            int letter = pick == c.Comp[t] ? t : pick;   // the paired letter copies the template; any other is a substitution
            need.Clear();
            need[letter]++; need[c.Comp[letter]]++;
            if (!w.BuildPolymer(a, need, 2)) { st.Stalls[Monomers(w, a, need, st) ? 3 : 2]++; break; }
            st.Ledger[Cell.LCopy] += World.PolymerCost(2);
            st.CopyDone++;
            if (letter != t) st.CopyErrors++;
            st.Replica[fork] = (byte)letter;
            st.ReplicaLetters[letter]++; st.ReplicaLetters[c.Comp[letter]]++;
            fork++;
        }
        if (checks > 0)
        {
            double cost = checks * World.PolymerCost(1);
            w.Spend(a, cost);
            st.Ledger[Cell.LProof] += cost;
        }
        st.Fork = fork;
        if (fork >= n) st.Genomes = 2;
    }

    // Division when the genome is copied and the membrane has outgrown the volume: area (crossings of membrane
    // proteins, amphiphilic molecules) over the area of a sphere of the body's volume ≥ Life2Divide (2^(1/3): enough
    // to wrap two spheres of half the volume). The second genome doubles the templates, so the membrane
    // proteins rise after a copy (a simplification of the cell cycle: fission follows segregation).
    void Divide(World w, Agent a, int cell, Cell st, Chem2 c)
    {
        // The two genomes are what is split (each daughter takes one): a cell with one does not divide.
        if (st.Genomes < 2 || MembraneExcess(a, st, c) < P.Life2Divide) return;
        int births = w.Births;
        w.Divide(a, cell, 0, w.Rng.Next(5));
        if (w.Births != births) st.Divisions++;
    }

    public static double MembraneArea(Agent a, Cell st, Chem2 c)
    {
        double area = 0;
        foreach (var s in st.Slots) if (s.Type.Crossings > 0) area += P.Life2MembraneTM * s.Total * s.Type.Crossings;
        for (int s = 0; s < Chemistry.S; s++)
        {
            double amph = c.Amphi[s];
            if (amph > 0) area += P.Life2MembraneLipid * amph * World.HaveRaw(a, s) / Qty.One;
        }
        return area;
    }

    public static double MembraneExcess(Agent a, Cell st, Chem2 c)
    {
        double v = Math.Max(1e-3, a.Volume);
        return MembraneArea(a, st, c) / Math.Cbrt(v * v);
    }

    // ---- assembly of a planted cell (its first tick) ----

    // A cell planted from a design or a population without its state assembles itself from the molecules it
    // is made of: first its genome duplex, then copies of its genes' proteins in turn up to the steady state
    // their own promoters would keep (transcription over decay), as far as its monomers and charge reach.
    void Assemble(World w, Agent a, Cell st, Chem2 c)
    {
        st.Assembled = true;
        st.Heading = w.Rng.Next(256);
        var res = GeneTable.Residues(a.G);
        Array.Clear(letters);
        foreach (var r in res) { letters[r]++; letters[c.Comp[r]]++; }
        if (w.BuildPolymer(a, letters, 2L * res.Length))
        {
            st.Genomes = 1;
            Array.Copy(letters, st.GenomeLetters, Chem2.L);
        }
        var units = GeneTable.Parse(c, res);
        if (units.Count == 0) return;
        var types = new List<ProteinType>();
        foreach (var u in units) if (!types.Contains(u.Type)) types.Add(u.Type);
        var table = GeneTable.Build(c, res, types.ToArray());
        int level = Chem2.Level(a.Tb);
        double beta = Chem2.Beta(level), room = Room(a), tf = World.TempFactor(a.Tb);
        int every = Math.Max(1, P.Life2Every);
        // Steady state by iteration: copies n_u = τ_u / decay per slow step, from n = 2 for each.
        var target = new double[types.Count];
        for (int k = 0; k < target.Length; k++) target[k] = 2;
        var shareR = new double[types.Count];
        for (int k = 0; k < shareR.Length; k++) shareR[k] = 0.5;
        var probe = new Cell();
        for (int it = 0; it < 8; it++)
        {
            probe.Slots.Clear();
            for (int k = 0; k < types.Count; k++) { var sl = new Slot { Type = types[k], Seq = types[k].Seq }; sl.N[0] = Math.Max(1, (int)Math.Round(target[k])); probe.Slots.Add(sl); }
            var next = new double[types.Count];
            int[] pn = null;
            var nOf = CopiesOf(probe, table.Types, ref pn);
            var kl = table.At(level);
            for (int u = 0; u < table.Units.Length; u++)
            {
                double xp = Occupancy(table.Promoter[u], kl.PR[u], kl.PT[u], nOf, shareR, room, true);
                double xall = Occupancy(table.Promoter[u], kl.PR[u], kl.PT[u], nOf, shareR, room, false);
                double xb = Occupancy(table.Block[u], kl.BR[u], kl.BT[u], nOf, shareR, room, false);
                double tau = P.Life2Tx * xp / (1 + xall) * (1 - xb / (1 + xb));
                var t = table.Units[u].Type;
                double decay = 1 - DetMath.Exp(-t.At(level).Decay * tf * every);
                next[types.IndexOf(t)] += tau / Math.Max(1e-6, decay);
            }
            for (int k = 0; k < target.Length; k++) target[k] = Math.Min(400, 0.5 * (target[k] + next[k]));
        }
        // Build them in turns (scarce matter is shared among the genes).
        var made = new int[types.Count];
        bool any = true;
        while (any)
        {
            any = false;
            for (int k = 0; k < types.Count; k++)
            {
                if (made[k] >= Math.Round(target[k])) continue;
                if (a.InvTotal <= 2 * P.DivMinBody || !w.BuildPolymer(a, types[k].Letters, types[k].Len)) continue;   // keeps molecules to live on
                made[k]++; any = true;
                Add(st, types[k], 1);
            }
        }
    }

    // ---- heredity ----

    public void GenomeChanged(Agent a) { if (a.ModelState is Cell st) st.Table = null; }

    public byte[] RandomGenome(SimRng rng)
    {
        var g = new byte[64];
        for (int i = 0; i < g.Length; i++) g[i] = (byte)rng.Next(256);
        return g;
    }

    // The copy a child gets is the replica the parent made (Divided); the bytes here are only a placeholder.
    public (byte[] g, byte[] p) Mutate(byte[] g, byte[] prot, SimRng rng) => ((byte[])g.Clone(), (byte[])prot.Clone());
    public (byte[] g, byte[] p) Cross(Agent a, Agent b, SimRng rng) => ((byte[])a.G.Clone(), (byte[])a.Prot.Clone());
    public void InheritState(Agent parent, Agent child) => child.ModelState = new Cell { Assembled = true };

    // The child takes the replica (if the copy was finished) and half of every protein's copies (binomially).
    public void Divided(World w, Agent parent, Agent child)
    {
        if (parent.ModelState is not Cell ps || child.ModelState is not Cell cs) return;
        cs.Heading = w.Rng.Next(256);
        cs.C = ps.C;
        Scratch();
        w.HandFractions(parent, child, 0.5);   // its monomers are mostly fractions of molecules: half of them too
        if (ps.Genomes == 2)
        {
            w.HandPolymer(parent, child, ps.ReplicaLetters, Sum(ps.ReplicaLetters) * World.LinkRaw);
            child.SetGenome(GeneTable.Pack(ps.Replica), new byte[parent.G.Length]);
            Array.Copy(ps.ReplicaLetters, cs.GenomeLetters, Chem2.L);
            cs.Genomes = 1;
            ps.Genomes = 1;
            ps.Replica = Array.Empty<byte>();
            ps.Fork = 0;
            Array.Clear(ps.ReplicaLetters);
        }
        foreach (var slot in ps.Slots)
        {
            var t = slot.Type;
            var cslot = new Slot { Type = t, Seq = t.Seq };
            for (int s = 0; s < 4; s++)
            {
                int k = 0;
                for (int j = 0; j < slot.N[s]; j++) if ((w.Rng.NextU64() & 1) != 0) k++;
                if (k == 0) continue;
                for (int f = 0; f < Chem2.L; f++) letters[f] = t.Letters[f] * k;
                long energy = (long)k * t.Len * World.LinkRaw;
                for (int j = 0; j < t.Sites.Length; j++) if ((s >> j & 1) != 0) energy += (long)k * t.SiteGap[j] * World.ResidueRaw;
                w.HandPolymer(parent, child, letters, energy);
                slot.N[s] -= k; cslot.N[s] += k;
            }
            if (cslot.Total > 0) cs.Slots.Add(cslot);
        }
        ps.Slots.RemoveAll(s => s.Total == 0);
        cs.Acc = (double[])ps.Acc.Clone();
    }

    // Does the body hold the monomers for these letters (ground or excited)? (Diagnostics: why a build stalled.)
    static bool Monomers(World w, Agent a, ReadOnlySpan<int> letters, Cell st)
    {
        bool all = true;
        for (int f = 0; f < Chem2.L && f < letters.Length; f++)
            if (letters[f] > 0 && World.HaveRaw(a, 2 * f) + World.HaveRaw(a, 2 * f + 1) < letters[f] * World.ResidueRaw) { st.StallLetter[f]++; all = false; }
        return all;
    }

    static long Sum(int[] x) { long s = 0; foreach (var v in x) s += v; return s; }

    public int SpliceSite(Agent a) => 0;
    public void BeforeSplice(Agent a, int at, int removed, int inserted) { }

    // ---- saving ----

    public void SyncState(World.Sync s, Agent a)
    {
        var st = a.ModelState as Cell;
        bool has = st != null;
        s.V(ref has);
        if (!has) { if (s.Reading) a.ModelState = null; World.SyncPolymers(s, a); return; }
        if (s.Reading) a.ModelState = st = new Cell();
        int version = Cell.Version;
        s.V(ref version);
        s.V(ref st.Assembled); s.V(ref st.Genomes); s.A<int>(st.GenomeLetters);
        int rl = st.Replica.Length;
        s.V(ref rl);
        if (s.Reading) st.Replica = new byte[rl];
        s.A<byte>(st.Replica);
        s.V(ref st.Fork);
        s.A<int>(st.ReplicaLetters);
        s.V(ref st.Heading);
        int n = st.Slots.Count;
        s.V(ref n);
        if (s.Reading) st.Slots.Clear();
        for (int k = 0; k < n; k++)
        {
            byte[] seq = s.Reading ? null : st.Slots[k].Seq;
            int len = seq?.Length ?? 0;
            s.V(ref len);
            if (s.Reading) seq = new byte[len];
            s.A<byte>(seq);
            var slot = s.Reading ? new Slot() : st.Slots[k];
            s.A<int>(slot.N);
            if (s.Reading) { slot.Seq = seq; st.Slots.Add(slot); }
        }
        int acc = st.Acc.Length;
        s.V(ref acc);
        if (s.Reading) st.Acc = new double[acc];
        s.A<double>(st.Acc);
        s.V(ref st.Thrusts); s.V(ref st.Tumbles); s.V(ref st.Synth); s.V(ref st.Decayed); s.V(ref st.Photons); s.V(ref st.Reactions); s.V(ref st.Divisions);
        s.V(ref st.Uptake);
        if (version >= 2) { s.V(ref st.DriftX); s.V(ref st.DriftY); }
        World.SyncPolymers(s, a);
    }

    // ---- text ----

    public string Describe(byte[] g)
    {
        var res = GeneTable.Residues(g);
        var sb = new StringBuilder("chem\n");
        for (int i = 0; i < res.Length; i++)
        {
            sb.Append("0123456789abcdef"[res[i]]);
            if (i % 64 == 63) sb.Append('\n');
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    // Residues as hexadecimal letters (0–f: ground formulas 0–15, species 2·letter), whitespace ignored, `#`
    // to the end of a line a comment, an optional first word "chem". An even number of residues (two per byte).
    public bool TryCompile(string text, out byte[] genome, out List<string> errors)
    {
        errors = new List<string>();
        genome = null;
        var res = new List<byte>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.StartsWith("chem", StringComparison.OrdinalIgnoreCase)) line = line[4..];
            foreach (char ch in line)
            {
                if (char.IsWhiteSpace(ch)) continue;
                int v = ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : ch >= 'A' && ch <= 'F' ? ch - 'A' + 10 : -1;
                if (v < 0) { errors.Add(Loc.T($"not a residue letter: '{ch}'", $"не буква остатка: «{ch}»")); return false; }
                res.Add((byte)v);
            }
        }
        if (res.Count % 2 != 0) { errors.Add(Loc.T($"{res.Count} residues: the genome holds them in pairs (add one)", $"{res.Count} остатков: геном хранит их парами (добавьте один)")); return false; }
        if (res.Count / 2 < Genome.MinLen || res.Count / 2 > Genome.MaxLen) { errors.Add(Loc.T($"{res.Count} residues: from {2 * Genome.MinLen} to {2 * Genome.MaxLen}", $"{res.Count} остатков: от {2 * Genome.MinLen} до {2 * Genome.MaxLen}")); return false; }
        genome = GeneTable.Pack(res.ToArray());
        return true;
    }

    public IReadOnlyList<CreatureDesign> DefaultDesigns => Array.Empty<CreatureDesign>();
}
