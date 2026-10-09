using System;
using System.Collections.Generic;
using System.Linq;

namespace Primordium;

// Creatures the player designs (CreatureDesign) and plants into the world. Between ticks only (the
// game sends SimRunner.SpawnDesign). Nothing is made from nothing:
// - molecules come from the place — loose matter of the cell and its four neighbours and the cell's
//   soft top block (never its last molecule, so the ground does not move) — or are brought from
//   outside, booked atom by atom in HandInput like the pour brush;
// - energy comes from exothermic splits of loose matter there (the products stay where they were) or
//   is brought from outside, booked in HandEnergy.
// If the place cannot supply a body, nothing is taken. A planted body starts a new lineage (or joins
// a chosen one), is marked Agent.Designed, and its lineage is remembered in DesignedLineages.
public sealed partial class World
{
    public int DesignSpawns;   // bodies made from player designs
    // Lineages started (or chosen) for player-designed creatures, with the design's name: their
    // descendants are recognisable as the player's (Agent.Designed marks the planted bodies themselves).
    public readonly Dictionary<long, string> DesignedLineages = new();

    public string DesignOf(long lineage) => DesignedLineages.TryGetValue(lineage, out var name) ? name : null;

    public SpawnResult SpawnDesign(CreatureDesign design, int x, int y, SpawnOptions options = null)
    {
        if (cur != null) throw new InvalidOperationException("designs are planted between ticks");
        options ??= new SpawnOptions();
        var result = new SpawnResult { Requested = Math.Max(0, options.Count) };
        var errors = design.Check(Chem);
        if (errors.Count > 0) { result.Error = string.Join("; ", errors); return result; }
        var genome = design.Assemble();
        var body = design.ResolveBody(Chem, errors);
        long lineage = options.Lineage;
        int r = Math.Max(0, options.Radius);
        for (int t = 0; t < Math.Max(8, result.Requested * 8) && result.Made < result.Requested; t++)
        {
            int cx = x, cy = y;
            if (t > 0)
            {
                // Further bodies (and retries) at random cells around the spot.
                int dx = mainRng.Next(-r, r + 1), dy = mainRng.Next(-r, r + 1);
                if (dx * dx + dy * dy > r * r) continue;
                cx += dx; cy += dy;
            }
            if (cy < 0 || cy >= H) continue;
            int cell = cy * W + ((cx % W) + W) % W;
            var a = PlantDesign(design, genome, body, cell, options, ref lineage, result, out string why);
            if (a == null) { result.Error = why; continue; }
            result.Agents.Add(a);
            result.Made++;
        }
        result.Lineage = lineage;
        if (result.Made > 0)
        {
            DesignedLineages[lineage] = design.Name;
            if (result.Made == result.Requested) result.Error = null;
            foreach (var a in result.Agents) TrackNew(a, Chronicle.WhyDesigned);
            bool importM = options.Matter == MatterSource.Import, importE = options.Energy == EnergySource.Import;
            Add(EvType.Player, Loc.Both(
                    $"design '{CreatureExamples.NameEn(design.Name)}' planted: {result.Made} {(result.Made == 1 ? "body" : "bodies")}, lineage #{lineage}, " +
                    (importM ? "matter from outside" : "local matter") + ", " + (importE ? "energy from outside" : "local energy"),
                    $"посажен дизайн «{CreatureExamples.NameRu(design.Name)}»: {result.Made} {Plural(result.Made, "тело", "тела", "тел")}, линия #{lineage}, " +
                    (importM ? "вещество извне" : "вещество местное") + ", " + (importE ? "энергия извне" : "энергия местная")),
                result.Agents[0], result.Made, true);
        }
        return result;
    }

    Agent PlantDesign(CreatureDesign d, byte[] genome, Dictionary<int, int> body, int cell, SpawnOptions o, ref long lineage, SpawnResult result, out string why)
    {
        why = null;
        int level = Height[cell];
        if (level >= Z - 2) { why = Loc.T("no room above the column", "нет места над столбом"); return null; }
        var sources = new List<int> { cell };   // loose matter here and next door
        for (int k = 0; k < 4; k++) { int n = nb[cell * 4 + k]; if (!sources.Contains(n)) sources.Add(n); }
        int top = cell * Z + level - 1;
        bool soft = level > 2 && Mat[top] >= 2 && VoxelBarrier(top) < 2;

        // 1. Which molecules, from where (nothing is changed until the whole body is found).
        var counts = new int[Chemistry.S];
        var looseTake = new int[sources.Count, Chemistry.S];
        var blockTake = new int[Chemistry.S];
        if (o.Matter == MatterSource.Import)
        {
            foreach (var (s, n) in body) counts[s == CreatureDesign.AnyMolecule ? ImportedAny(cell) : s] += n;
        }
        else
        {
            int blockLeft = soft ? Units[top] - 1 : 0;   // never the block's last molecule: the ground stays where it is
            int Loose(int k, int s) => (int)C[s][sources[k]] - looseTake[k, s];
            int Block(int s) => soft ? Math.Min(VoxelCount(top, s) - blockTake[s], blockLeft) : 0;
            bool TakeOne(int s)
            {
                for (int k = 0; k < sources.Count; k++)
                    if (Loose(k, s) > 0) { looseTake[k, s]++; counts[s]++; return true; }
                if (Block(s) > 0) { blockTake[s]++; blockLeft--; counts[s]++; return true; }
                return false;
            }
            foreach (var (s, n) in body.OrderBy(kv => kv.Key == CreatureDesign.AnyMolecule ? 1 : 0).ThenBy(kv => kv.Key))
                for (int j = 0; j < n; j++)
                {
                    int want = s;
                    if (s == CreatureDesign.AnyMolecule)
                    {
                        // Whatever is most plentiful here (not the gas).
                        int most = 0;
                        want = -1;
                        for (int q = 0; q < Chemistry.S; q++)
                        {
                            if (q == Chem.Gas) continue;
                            int have = Block(q);
                            for (int k = 0; k < sources.Count; k++) have += Math.Max(0, Loose(k, q));
                            if (have > most) { most = have; want = q; }
                        }
                        if (want < 0) { why = Loc.T($"not enough matter here: {n - j} more needed", $"здесь не хватает вещества: нужно ещё {n - j}"); return null; }
                    }
                    if (!TakeOne(want)) { why = Loc.T($"too little {Chem.NameEn[want]} here: {n} needed, {j} found", $"здесь мало {Chem.NameRu[want]}: нужно {n}, нашлось {j}"); return null; }
                }
        }
        float volume = 0;
        for (int s = 0; s < Chemistry.S; s++) volume += counts[s] * Chem.BodyVolume[s];
        if (!Fits(cell, level, volume)) { why = Loc.T("no room on this floor for such a body", "на этом полу нет места для такого тела"); return null; }

        // 2. Energy: local reactions are worked out on a copy of the loose matter left after step 1. With
        // P.MatterEnergy 1 the design's energy is the charge it starts with (World.Charge): it must fit in
        // the excited states of the ground molecules it is made of, and from the place it takes the
        // reactions' energy before capture (CaptureHeat of it warms the new body).
        double energy = 0;
        var burn = new List<(int c, Chemistry.Reaction r, Qty m)>();
        double add = d.Energy;
        if (MatterLaw && (add = ChargeToAdd(counts, d.Energy, out why)) < 0) return null;
        double need = MatterLaw && o.Energy == EnergySource.Local ? CaptureNeed(add) : add;
        if (o.Energy == EnergySource.Import) energy = add;
        else if (add > 0)
        {
            var left = new Qty[sources.Count, Chemistry.S];
            for (int k = 0; k < sources.Count; k++)
                for (int s = 0; s < Chemistry.S; s++) left[k, s] = C[s][sources[k]] - looseTake[k, s];
            energy = PlanLocalEnergy(sources, left, need, burn);
            if (energy < need * 0.999) { why = Loc.T($"local reactions give {energy:0.#} of {need:0.#} energy", $"местные реакции дают {energy:0.#} энергии из {need:0.#}"); return null; }
        }

        // 3. Take it all.
        if (o.Matter == MatterSource.Import)
            for (int s = 0; s < Chemistry.S; s++)
                for (int e = 0; e < Chemistry.ElementCount; e++)
                {
                    double atoms = (double)counts[s] * Chem.Atoms[s, e];
                    HandInput[e] += atoms;
                    result.AtomsImported[e] += atoms;
                }
        else
        {
            for (int k = 0; k < sources.Count; k++)
                for (int s = 0; s < Chemistry.S; s++)
                    if (looseTake[k, s] > 0) C[s][sources[k]] -= looseTake[k, s];
            for (int s = 0; s < Chemistry.S; s++)
                for (int j = 0; j < blockTake[s]; j++) TakeVoxelSpecies(top, s);
        }
        ApplyLocalEnergy(sources, burn);

        // 4. The body.
        long id = NewId();
        if (lineage <= 0) lineage = id;
        var a = new Agent(id, lineage, 0, (byte[])genome.Clone(), null, d.Life().Id) { Tb = Temp[cell], Z = Height[cell], Designed = true };
        for (int s = 0; s < Chemistry.S; s++) for (int j = 0; j < counts[s]; j++) AddMol(a, s);
        // The ledger (World.Energy): what came from outside is an input; local matter and local reactions
        // only moved energy between reservoirs (loose/rock bonds → body bonds and body energy).
        if (o.Matter == MatterSource.Import)
        {
            double bonds = 0;
            for (int s = 0; s < Chemistry.S; s++) bonds += (double)counts[s] * Chem.E[s];
            Flows[FDesign] += bonds;
        }
        energy = StartEnergy(a, energy, o.Energy == EnergySource.Import);
        if (o.Energy == EnergySource.Import) { HandEnergy += energy; result.EnergyImported += energy; Flows[FDesign] += energy; }
        else result.EnergyLocal += energy;
        Looks.Apply(a);
        if (d.Hue is float hue) a.Hue = ((hue % 1f) + 1f) % 1f;
        if (d.Sat is float sat) a.Sat = Math.Clamp(sat, 0, 1);
        if (d.Val is float val) a.Val = Math.Clamp(val, 0, 1);
        if (d.Shape is int shape) a.Shape = ((shape % Looks.ShapeCount) + Looks.ShapeCount) % Looks.ShapeCount;
        SetLift(a, cell, a.Z, a.Z);
        Place(a, cell);
        Agents.Add(a);
        EvoRegister(a);   // a founder: a root of the family tree
        DesignSpawns++;
        return a;
    }

    // The downhill reactions of the loose matter at `sources` (what is left there after the body's matter is
    // taken: `left`, per source and species, changed in place), the most energetic first over all of them,
    // until `need` is released: exothermic splits, and in the chemistry from bonds (Chemistry.Model 1, where
    // splits seldom release energy) binds too — the reactions abiogenesis uses (LocalReactions). Every partner
    // at least 0.1 molecule; each moves exactly the same Qty. `burn` lists them for ApplyLocalEnergy.
    double PlanLocalEnergy(List<int> sources, Qty[,] left, double need, List<(int c, Chemistry.Reaction r, Qty m)> burn)
    {
        double energy = 0;
        long min = Qty.Of(0.1).Raw;
        bool binds = Chem.Model != 0;
        var down = Chem.Downhill;
        for (int step = 0; step < 4 * Chemistry.S * sources.Count && energy < need; step++)
        {
            int bk = -1;
            Chemistry.Reaction best = default;
            for (int k = 0; k < sources.Count; k++)
                foreach (var r in down)   // the most energetic first
                {
                    if (bk >= 0 && r.Energy <= best.Energy) break;
                    if (r.B >= 0 && !binds) continue;
                    bool can = r.B < 0 ? left[k, r.A].Raw >= min : r.A == r.B ? left[k, r.A].Raw >= 2 * min : left[k, r.A].Raw >= min && left[k, r.B].Raw >= min;
                    if (can) { bk = k; best = r; break; }
                }
            if (bk < 0) break;
            long have = best.B < 0 ? left[bk, best.A].Raw : best.A == best.B ? left[bk, best.A].Raw / 2 : Math.Min(left[bk, best.A].Raw, left[bk, best.B].Raw);
            Qty take = Qty.Min(Qty.FromRaw(have), Qty.Of((need - energy) / best.Energy));
            if (take.Raw <= 0) take = Qty.FromRaw(1);
            React(left, bk, best, take);
            energy += take.D * best.Energy;
            burn.Add((sources[bk], best, take));
        }
        return energy;
    }

    // For the designer (read only): the energy the downhill reactions of the loose matter in a cell and its four
    // neighbours could release (PlanLocalEnergy on a copy).
    public double LocalEnergyAround(int cell)
    {
        var sources = new List<int> { cell };
        for (int k = 0; k < 4; k++) { int n = nb[cell * 4 + k]; if (!sources.Contains(n)) sources.Add(n); }
        var left = new Qty[sources.Count, Chemistry.S];
        for (int k = 0; k < sources.Count; k++) for (int s = 0; s < Chemistry.S; s++) left[k, s] = C[s][sources[k]];
        return PlanLocalEnergy(sources, left, 1e9, new List<(int, Chemistry.Reaction, Qty)>());
    }

    void React(Qty[,] pool, int k, Chemistry.Reaction r, Qty m)
    {
        pool[k, r.A] -= m;
        if (r.B < 0)
        {
            pool[k, Chem.SplitA[r.A]] += m;
            if (Chem.SplitB[r.A] >= 0) pool[k, Chem.SplitB[r.A]] += m;
        }
        else { pool[k, r.B] -= m; pool[k, r.P] += m; }
    }

    // The planned local reactions happen in the loose matter of their cells (the products stay there).
    void ApplyLocalEnergy(List<int> sources, List<(int c, Chemistry.Reaction r, Qty m)> burn)
    {
        foreach (var (c, r, m) in burn)
        {
            C[r.A][c] -= m;
            if (r.B < 0)
            {
                C[Chem.SplitA[r.A]][c] += m;
                if (Chem.SplitB[r.A] >= 0) C[Chem.SplitB[r.A]][c] += m;
            }
            else { C[r.B][c] -= m; C[r.P][c] += m; }
        }
    }

    // Law 1: a design's energy is the charge its body starts with. The excited molecules it is made of
    // already carry some; the rest must fit in the excited states of its ground molecules (Σ n_g·Gap).
    // Returns the charge to add, or −1 (and why) if the body cannot hold it.
    double ChargeToAdd(int[] counts, double charge, out string why)
    {
        why = null;
        double held = 0, room = 0;
        for (int s = 0; s < Chemistry.S; s++)
        {
            if (Chem.Gap[s] > 0) held += (double)counts[s] * Chem.Gap[s];
            else if (Chem.PhotoUp[s] >= 0) room += (double)counts[s] * Chem.Gap[Chem.PhotoUp[s]];
        }
        double add = Math.Max(0, charge - held);
        if (add <= room + 1e-9) return add;
        why = Loc.T($"the body cannot hold {charge:0.#} of charge: its molecules hold {held:0.#} excited and take at most {room:0.#} more (add molecules, or excited ones)",
                    $"тело не удержит заряд {charge:0.#}: его молекулы держат возбуждёнными {held:0.#} и примут не больше {room:0.#} (добавьте молекул или возбуждённых)");
        return -1;
    }

    // Law 1: the energy local reactions must release for `charge` to be captured (CaptureHeat of it warms the body).
    static double CaptureNeed(double charge) => 1 - P.CaptureHeat > 0 ? charge / (1 - P.CaptureHeat) : double.PositiveInfinity;

    // The energy a planted body starts with. Law 0: its store. Law 1 (World.Charge): its charge — brought in,
    // the excitation of its ground molecules; from the place, the reactions' energy captured like any
    // reaction's in a body. Returns what to book: brought in (law 1: the excitation actually made, on the
    // 2⁻³² grid) or released by the local reactions.
    double StartEnergy(Agent a, double energy, bool imported)
    {
        if (!MatterLaw) { a.Energy = energy; a.LifeStart = (float)energy; return energy; }
        if (imported) energy = Excite(a, energy);
        else ReleaseGain(a, energy);
        a.LifeStart = (float)Held(a);
        return energy;
    }

    // What "any" molecules brought from outside are: the kind the ground here is made of, else the
    // most plentiful loose kind, else the first element.
    int ImportedAny(int cell)
    {
        int m = TopMat(cell);
        if (m >= 2 && m - 2 != Chem.Gas) return m - 2;
        int best = 0;
        for (int s = 1; s < Chemistry.S; s++) if (s != Chem.Gas && C[s][cell] > C[best][cell]) best = s;
        return best == Chem.Gas ? 0 : best;
    }

    // Take one molecule of kind s out of a block (TakeVoxelMolecule takes a random one of the mix).
    bool TakeVoxelSpecies(int v, int s)
    {
        if (Units[v] == 0 || Mat[v] < 2) return false;
        if (Mixed(v, out var counts))
        {
            if (counts[s] == 0) return false;
            counts[s]--;
            if (counts[s] == 0 && Units[v] > 1) Mat[v] = Chem.BuiltMat[Dominant(counts)];
        }
        else if (Mat[v] - 2 != s) return false;
        compressionCache[v] = cohesionCache[v] = 0;
        Units[v]--;
        if (Units[v] == 0) RemoveVoxel(v / Z, v % Z);
        else Weakened(v);
        return true;
    }
}
