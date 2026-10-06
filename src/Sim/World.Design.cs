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
        }
        return result;
    }

    Agent PlantDesign(CreatureDesign d, byte[] genome, Dictionary<int, int> body, int cell, SpawnOptions o, ref long lineage, SpawnResult result, out string why)
    {
        why = null;
        int level = Height[cell];
        if (level >= Z - 2) { why = "нет места над столбом"; return null; }
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
                        if (want < 0) { why = $"здесь не хватает вещества: нужно ещё {n - j}"; return null; }
                    }
                    if (!TakeOne(want)) { why = $"здесь мало {Chem.Name[want]}: нужно {n}, нашлось {j}"; return null; }
                }
        }
        float volume = 0;
        for (int s = 0; s < Chemistry.S; s++) volume += counts[s] * Chem.BodyVolume[s];
        if (!Fits(cell, level, volume)) { why = "на этом полу нет места для такого тела"; return null; }

        // 2. Energy: local reactions are worked out on a copy of the loose matter left after step 1.
        float energy = 0;
        var burn = new List<(int c, int s, float amount)>();
        if (o.Energy == EnergySource.Import) energy = d.Energy;
        else if (d.Energy > 0)
        {
            var left = new float[sources.Count, Chemistry.S];
            for (int k = 0; k < sources.Count; k++)
                for (int s = 0; s < Chemistry.S; s++) left[k, s] = (float)(C[s][sources[k]] - looseTake[k, s]);
            for (int step = 0; step < 4 * Chemistry.S * sources.Count && energy < d.Energy; step++)
            {
                int bk = -1, bs = -1;
                for (int k = 0; k < sources.Count; k++)
                    for (int s = 0; s < Chemistry.S; s++)
                        if (Chem.SplitExo[s] && left[k, s] >= 0.1f && (bs < 0 || Chem.SplitEnergy(s) > Chem.SplitEnergy(bs))) { bk = k; bs = s; }
                if (bs < 0) break;
                float take = Math.Min(left[bk, bs], (d.Energy - energy) / Chem.SplitEnergy(bs));
                left[bk, bs] -= take;
                left[bk, Chem.SplitA[bs]] += take;
                if (Chem.SplitB[bs] >= 0) left[bk, Chem.SplitB[bs]] += take;
                energy += take * Chem.SplitEnergy(bs);
                burn.Add((sources[bk], bs, take));
            }
            if (energy < d.Energy * 0.999f) { why = $"местные реакции дают {energy:0.#} энергии из {d.Energy:0.#}"; return null; }
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
        foreach (var (c, s, amount) in burn)
        {
            C[s][c] -= amount;
            C[Chem.SplitA[s]][c] += amount;
            if (Chem.SplitB[s] >= 0) C[Chem.SplitB[s]][c] += amount;
        }
        if (o.Energy == EnergySource.Import) { HandEnergy += energy; result.EnergyImported += energy; }
        else result.EnergyLocal += energy;

        // 4. The body.
        long id = NewId();
        if (lineage <= 0) lineage = id;
        var a = new Agent(id, lineage, 0, (byte[])genome.Clone()) { Tb = Temp[cell], Z = Height[cell], Designed = true };
        for (int s = 0; s < Chemistry.S; s++) for (int j = 0; j < counts[s]; j++) AddMol(a, s);
        a.Energy = a.LifeStart = energy;
        // The ledger (World.Energy): what came from outside is an input; local matter and local splits
        // only moved energy between reservoirs (loose/rock bonds → body bonds and body energy).
        if (o.Matter == MatterSource.Import)
        {
            double bonds = 0;
            for (int s = 0; s < Chemistry.S; s++) bonds += (double)counts[s] * Chem.E[s];
            Flows[FDesign] += bonds;
        }
        if (o.Energy == EnergySource.Import) Flows[FDesign] += a.Energy;
        Looks.Apply(a);
        if (d.Hue is float hue) a.Hue = ((hue % 1f) + 1f) % 1f;
        if (d.Sat is float sat) a.Sat = Math.Clamp(sat, 0, 1);
        if (d.Val is float val) a.Val = Math.Clamp(val, 0, 1);
        if (d.Shape is int shape) a.Shape = ((shape % Looks.ShapeCount) + Looks.ShapeCount) % Looks.ShapeCount;
        SetLift(a, cell, a.Z, a.Z);
        Place(a, cell);
        Agents.Add(a);
        DesignSpawns++;
        return a;
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
