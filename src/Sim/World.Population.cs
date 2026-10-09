using System;
using System.Collections.Generic;
using System.Linq;

namespace Primordium;

// Population templates (Population.cs): copying living bodies out of the world and pasting them back,
// here or in another world. Between ticks only (the game sends SimRunner commands). Copying changes
// nothing. Pasting makes every body from real matter and energy, like a planted design
// (World.Design.cs): each body takes its molecules from its own cell, the four next to it and the
// cell's soft top block (never its last molecule) or brings them from outside (booked atom by atom in
// HandInput), and its energy from exothermic splits of loose matter there or from outside
// (HandEnergy). A body the place cannot supply is not made, and nothing is taken for it.
public sealed partial class World
{
    // ---- choosing bodies ----

    public List<Agent> LineageBodies(long lineage) => Agents.Where(a => !a.Dead && a.Lineage == lineage).ToList();

    // The clade starting at the taxon with this genome hash and origin tick (the tree window's pick:
    // CladeNode.Hash / Origin; a hash of 0 is picked as 1): every living body whose taxon is it or below it.
    public List<Agent> CladeBodies(ulong hash, long origin)
    {
        EvoJoin();
        var ph = Phylo;
        var inside = new sbyte[ph.High];   // 0 unknown, 1 in, -1 out
        for (int t = 0; t < ph.High; t++)
            if (ph.Used(t) && ph.Origin[t] == origin && (ph.Hash[t] == hash || (hash == 1 && ph.Hash[t] == 0))) inside[t] = 1;
        bool In(int t)
        {
            var path = new List<int>();
            int x = t;
            while (x >= 0 && x < ph.High && inside[x] == 0) { path.Add(x); x = ph.Parent[x]; }
            sbyte v = x >= 0 && x < ph.High && inside[x] == 1 ? (sbyte)1 : (sbyte)-1;
            foreach (int p in path) inside[p] = v;
            return v == 1;
        }
        return Agents.Where(a => !a.Dead && a.Taxon >= 0 && a.Taxon < ph.High && In(a.Taxon)).ToList();
    }

    // Every living body in the round brush (big bodies reaching into it too), each once.
    public List<Agent> AreaBodies(int cx, int cy, float r)
    {
        var seen = new HashSet<Agent>(ReferenceEqualityComparer.Instance);
        var list = new List<Agent>();
        foreach (var (c, _) in Brush(cx, cy, r))
        {
            for (var a = Head[c]; a != null; a = a.NextInCell) if (!a.Dead && seen.Add(a)) list.Add(a);
            if (Big[c] is { Dead: false } big && seen.Add(big)) list.Add(big);
        }
        return list.OrderBy(a => a.Id).ToList();
    }

    // ---- copying ----

    public PopulationTemplate CopyPopulation(IReadOnlyList<Agent> bodies, string name, string source, string description = null)
    {
        if (cur != null) throw new InvalidOperationException("populations are copied between ticks");
        var t = new PopulationTemplate
        {
            Name = string.IsNullOrWhiteSpace(name) ? "population" : name.Trim(), Source = source ?? "bodies",
            Seed = Seed, Tick = Tick, Chemistry = PopulationChemistry.Of(Chem, Seed),
        };
        var live = bodies.Where(a => a != null && !a.Dead).Distinct().OrderBy(a => a.Id).Take(PopulationTemplate.MaxBodies).ToList();
        t.Description = description ?? Loc.T($"{live.Count} bodies ({source}) taken from world {Seed} at tick {Tick}.",
                                             $"{live.Count} тел ({source}) взято из мира {Seed} на тике {Tick}.");
        if (live.Count == 0) return t;
        // The anchor: the mean position (x around the planet from the first body, so a group across the
        // seam stays together).
        int x0 = live[0].X;
        int Wrap(int dx) => ((dx % W) + W + W / 2) % W - W / 2;
        double mx = live.Average(a => (double)Wrap(a.X - x0)), my = live.Average(a => (double)a.Y);
        int ax = ((x0 + (int)Math.Round(mx)) % W + W) % W, ay = Math.Clamp((int)Math.Round(my), 0, H - 1);
        int top0 = Height[ay * W + ax];
        foreach (var a in live)
        {
            int cell = a.Y * W + a.X;
            var b = new PopulationBody
            {
                Id = a.Id, Lineage = a.Lineage, Parent = a.ParentId, Gen = a.Gen,
                Dx = Wrap(a.X - ax), Dy = a.Y - ay, Rise = a.Z - Height[cell], Floor = a.Z - top0, Lift = a.Lift,
                Genome = Convert.ToBase64String(a.G), Prot = Convert.ToBase64String(a.Prot), Model = LifeModels.KeyFor(a.Model),
                Energy = Math.Max(0, a.Energy),
                Hue = a.Hue, Sat = a.Sat, Val = a.Val, Sx = a.Sx, Sy = a.Sy, Sz = a.Sz, Shape = a.Shape, Designed = a.Designed,
            };
            for (int s = 0; s < Chemistry.S; s++)
            {
                // A body's polymers (World.Polymer.cs) are carried as the monomers they are made of: the pasted body's
                // model builds its own anew from them (life model 2 assembles itself on its first tick).
                long raw = a.Pend[s].Raw + (a.Poly != null ? a.Poly.M[s].Raw : 0);
                int whole = a.Inv[s] + (int)(raw >> Qty.Bits);
                raw &= (1L << Qty.Bits) - 1;
                if (whole > 0) b.Body[s.ToString(System.Globalization.CultureInfo.InvariantCulture)] = whole;
                if (raw > 0) b.Pend[s.ToString(System.Globalization.CultureInfo.InvariantCulture)] = raw;
            }
            for (int k = 0; k < a.EnzN; k++)
            {
                var e = a.Enz[k];
                b.Proteins.Add(new ProteinInfo { Kind = e.Kind, A = e.A, B = e.B, Topt = e.Topt, Eff = e.Eff, Amount = e.Amount, Material = e.Material, Matter = Math.Max(0, e.Matter.Raw), Src = e.Src });
            }
            t.Bodies.Add(b);
        }
        return t;
    }

    // ---- pasting ----

    // Pastes the template with its anchor at (x, y). Bodies are made in the order of their source ids
    // (parents before their children); each one independently: those the place cannot supply are
    // counted in PasteResult.Failures.
    public PasteResult PastePopulation(PopulationTemplate t, int x, int y, PasteOptions o = null)
    {
        if (cur != null) throw new InvalidOperationException("populations are pasted between ticks");
        o ??= new PasteOptions();
        var result = new PasteResult();
        var errors = t?.Check() ?? new List<string> { "no template" };
        if (errors.Count > 0) { result.Error = string.Join("; ", errors.Take(3)); return result; }
        var map = result.Map = ChemMap.Build(t.Chemistry, Chem, Seed);
        result.Requested = t.Bodies.Count;
        var made = new Dictionary<long, Agent>();      // source id → pasted body
        var lineages = new Dictionary<long, long>();   // source lineage → new lineage
        long single = 0;
        foreach (var b in t.Bodies.OrderBy(b => b.Id))
        {
            int cy = y + b.Dy;
            if (cy < 0 || cy >= H) { Fail(result, Loc.T("outside the map", "за краем карты")); continue; }
            int cell = cy * W + (((x + b.Dx) % W) + W) % W;
            // The body in this world's species.
            var inv = new int[Chemistry.S];
            var pend = new Qty[Chemistry.S];
            foreach (var (k, n) in b.Body) inv[map.To[PopulationBody.Species(k)]] += n;
            foreach (var (k, raw) in b.Pend) pend[map.To[PopulationBody.Species(k)]] += Qty.FromRaw(raw);
            for (int s = 0; s < Chemistry.S; s++)
                while (pend[s] >= 1) { pend[s] -= 1; inv[s]++; }   // two species mapped onto one
            var prot = new List<Enzyme>();
            foreach (var p in b.Proteins)
                prot.Add(new Enzyme
                {
                    Kind = (byte)p.Kind, A = (byte)map.To[p.A], B = (byte)map.To[p.B], Topt = p.Topt, Eff = p.Eff, Amount = p.Amount,
                    Material = (byte)map.To[p.Material], Matter = Qty.FromRaw(p.Matter), Src = p.Src,
                });
            var need = new Qty[Chemistry.S];
            for (int s = 0; s < Chemistry.S; s++) need[s] = (Qty)inv[s] + pend[s];
            foreach (var e in prot) need[e.Material] += e.Matter;
            float volume = 0;
            for (int s = 0; s < Chemistry.S; s++) volume += (inv[s] + pend[s].F) * Chem.BodyVolume[s];
            foreach (var e in prot) volume += e.Matter.F * Chem.Volume[e.Material];

            var g = b.GenomeBytes();
            byte model = LifeModels.ByKey(b.Model).Id;
            // Protein genes name species: model 1's are found and re-pointed (another model's genome is its own).
            if (o.RemapGenes && !map.Same && model == LifeModels.Vm) { g = map.RemapGenes(g, out int genes); result.GenesRemapped += genes; }
            var protBytes = b.ProtBytes(g.Length);

            if (!TakeBody(cell, need, volume, b.Energy, o, result, out double energy, out string why)) { Fail(result, why); continue; }

            // The body.
            long id = NewId();
            long lineage;
            if (o.KeepRelations)
            {
                if (!lineages.TryGetValue(b.Lineage, out lineage)) lineages[b.Lineage] = lineage = id;
            }
            else
            {
                if (single == 0) single = id;
                lineage = single;
            }
            made.TryGetValue(b.Parent, out var parent);
            if (!o.KeepRelations) parent = null;
            var a = new Agent(id, lineage, o.KeepRelations ? b.Gen : 0, g, protBytes, model) { Tb = Temp[cell], Z = Height[cell], Designed = true };
            for (int s = 0; s < Chemistry.S; s++) for (int j = 0; j < inv[s]; j++) AddMol(a, s);
            for (int s = 0; s < Chemistry.S; s++)
                if (pend[s].Raw > 0) { a.Pend[s] = pend[s]; a.Mass += pend[s].F * Chem.Mass[s]; a.Volume += pend[s].F * Chem.BodyVolume[s]; }
            if (prot.Count > a.Enz.Length) a.Enz = new Enzyme[prot.Count];
            foreach (var e in prot)
            {
                var copy = e;
                if (copy.Src + 3 >= g.Length) copy.Src = -1;
                a.Enz[a.EnzN++] = copy;
                a.Mass += e.Matter.F * Chem.Mass[e.Material];
                a.Volume += e.Matter.F * Chem.Volume[e.Material];
            }
            energy = StartEnergy(a, energy, o.Energy == EnergySource.Import);
            if (o.Energy == EnergySource.Import) { HandEnergy += energy; result.EnergyImported += energy; Flows[FDesign] += energy; }
            else result.EnergyLocal += energy;
            a.Hue = ((b.Hue % 1f) + 1f) % 1f; a.Sat = Math.Clamp(b.Sat, 0, 1); a.Val = Math.Clamp(b.Val, 0, 1);
            a.Sx = b.Sx; a.Sy = b.Sy; a.Sz = b.Sz;
            a.Shape = ((b.Shape % Looks.ShapeCount) + Looks.ShapeCount) % Looks.ShapeCount;
            if (parent != null) { a.ParentId = parent.Id; a.TrackedAncestor = parent.Id; }
            SetLift(a, cell, a.Z, a.Z + Math.Max(0, b.Lift));
            Place(a, cell);
            Agents.Add(a);
            // The tree: a founder, or (relations kept) a child of its pasted parent.
            EvoJoin();
            EvoRealBorn(a);
            a.Taxon = Phylo.Born(parent?.Taxon ?? -1, a.Hash, parent != null ? EditSize(parent.G, a.G) : 0, Tick, a.Lineage);
            a.EvoParent = null;
            DesignSpawns++;
            made[b.Id] = a;
            result.Agents.Add(a);
            result.SourceIds.Add(b.Id);
            result.Made++;
        }
        if (result.Failures.Count > 0) result.Error = result.Failures.OrderByDescending(kv => kv.Value).First().Key;
        if (result.Made == 0) return result;
        foreach (var a in result.Agents)
        {
            if (!result.Lineages.Contains(a.Lineage)) result.Lineages.Add(a.Lineage);
            TrackNew(a, Chronicle.WhyDesigned);
        }
        foreach (long l in result.Lineages) DesignedLineages[l] = t.Name;
        bool importM = o.Matter == MatterSource.Import, importE = o.Energy == EnergySource.Import;
        string chemEn = map.Same ? "" : $", another chemistry (world {t.Seed}): {map.Changed} species mapped";
        string chemRu = map.Same ? "" : $", другая химия (мир {t.Seed}): заменено видов {map.Changed}";
        Add(EvType.Player, Loc.Both(
                $"population '{t.Name}' pasted: {result.Made} of {result.Requested} {(result.Requested == 1 ? "body" : "bodies")}, {result.Lineages.Count} {(result.Lineages.Count == 1 ? "lineage" : "lineages")} (#{result.Lineages[0]}…), " +
                (importM ? "matter from outside" : "local matter") + ", " + (importE ? "energy from outside" : "local energy") + chemEn,
                $"вставлена популяция «{t.Name}»: {result.Made} из {result.Requested} {Plural(result.Requested, "тела", "тел", "тел")}, {result.Lineages.Count} {Plural(result.Lineages.Count, "линия", "линии", "линий")} (#{result.Lineages[0]}…), " +
                (importM ? "вещество извне" : "вещество местное") + ", " + (importE ? "энергия извне" : "энергия местная") + chemRu),
            result.Agents[0], result.Made, true);
        return result;
    }

    // Failures are counted by their reason without the numbers of one body ("too little X here: …").
    static void Fail(PasteResult r, string why)
    {
        int colon = why.IndexOf(':');
        if (colon > 0) why = why[..colon];
        r.Failures.TryGetValue(why, out int n);
        r.Failures[why] = n + 1;
    }

    // Finds a body's matter (`need`, per species, exact) and `energyNeed` at the cell and takes it, or
    // takes nothing and says why. Matter from the place: loose matter of the cell and its four
    // neighbours first, then whole molecules of the cell's soft top block (never its last one; the part
    // of a block molecule the body does not need stays loose in the cell). Energy from the place:
    // exothermic splits of the loose matter left there, the strongest first (the products stay).
    bool TakeBody(int cell, Qty[] need, float volume, double energyNeed, PasteOptions o, PasteResult result, out double energy, out string why)
    {
        why = null;
        energy = 0;
        int level = Height[cell];
        if (level >= Z - 2) { why = Loc.T("no room above the column", "нет места над столбом"); return false; }
        if (!Fits(cell, level, volume)) { why = Loc.T("no room on this floor for such a body", "на этом полу нет места для такого тела"); return false; }
        var sources = new List<int> { cell };
        for (int k = 0; k < 4; k++) { int n = nb[cell * 4 + k]; if (!sources.Contains(n)) sources.Add(n); }
        int top = cell * Z + level - 1;
        bool soft = level > 2 && Mat[top] >= 2 && VoxelBarrier(top) < 2;

        var looseTake = new Qty[sources.Count, Chemistry.S];
        var blockTake = new int[Chemistry.S];
        var leftover = new Qty[Chemistry.S];   // of block molecules, back to loose in the cell
        if (o.Matter == MatterSource.Local)
        {
            int blockLeft = soft ? Units[top] - 1 : 0;
            for (int s = 0; s < Chemistry.S; s++)
            {
                Qty want = need[s];
                if (want.Raw <= 0) continue;
                for (int k = 0; k < sources.Count && want.Raw > 0; k++)
                {
                    Qty have = C[s][sources[k]];
                    if (have.Raw <= 0) continue;
                    Qty take = have.Raw < want.Raw ? have : want;
                    looseTake[k, s] = take;
                    want -= take;
                }
                if (want.Raw <= 0) continue;
                int whole = (int)((want.Raw + (1L << Qty.Bits) - 1) >> Qty.Bits);
                int inBlock = soft ? Math.Min(VoxelCount(top, s), blockLeft) : 0;
                if (whole > inBlock)
                {
                    double found = need[s].D - want.D + inBlock;
                    why = Loc.T($"too little {Chem.NameEn[s]} here: {need[s].D:0.##} needed, {found:0.##} found", $"здесь мало {Chem.NameRu[s]}: нужно {need[s].D:0.##}, нашлось {found:0.##}");
                    return false;
                }
                blockTake[s] = whole;
                blockLeft -= whole;
                leftover[s] = (Qty)whole - want;
            }
        }

        // Energy: local reactions worked out on a copy of the loose matter left after the matter. With
        // P.MatterEnergy 1 the template's store becomes charge (World.Charge), as much as the body's ground
        // molecules can take (its own excited molecules already carry what it had); from the place, the
        // energy before capture.
        var burn = new List<(int c, Chemistry.Reaction r, Qty m)>();
        if (MatterLaw)
        {
            var whole = new int[Chemistry.S];
            var frac = new Qty[Chemistry.S];
            for (int s = 0; s < Chemistry.S; s++) { whole[s] = (int)(need[s].Raw >> Qty.Bits); frac[s] = Qty.FromRaw(need[s].Raw & ((1L << Qty.Bits) - 1)); }
            double room = 0;
            for (int g = 0; g < Chemistry.S; g += 2) if (Chem.PhotoUp[g] >= 0) room += (whole[g] + frac[g].D) * Chem.Gap[Chem.PhotoUp[g]];
            energyNeed = Math.Min(energyNeed, room);
        }
        double release = MatterLaw && o.Energy == EnergySource.Local ? CaptureNeed(energyNeed) : energyNeed;
        if (o.Energy == EnergySource.Import) energy = energyNeed;
        else if (energyNeed > 0)
        {
            var left = new Qty[sources.Count, Chemistry.S];
            for (int k = 0; k < sources.Count; k++)
                for (int s = 0; s < Chemistry.S; s++) left[k, s] = C[s][sources[k]] - looseTake[k, s] + (k == 0 ? leftover[s] : Qty.Zero);
            energy = PlanLocalEnergy(sources, left, release, burn);
            if (energy < release * 0.999) { why = Loc.T($"too little energy from local reactions: {energy:0.#} of {release:0.#}", $"мало энергии от местных реакций: {energy:0.#} из {release:0.#}"); return false; }
        }

        // Take it all.
        if (o.Matter == MatterSource.Import)
        {
            double bonds = 0;
            for (int s = 0; s < Chemistry.S; s++)
            {
                if (need[s].Raw <= 0) continue;
                for (int e = 0; e < Chemistry.ElementCount; e++)
                {
                    double atoms = need[s].D * Chem.Atoms[s, e];
                    HandInput[e] += atoms;
                    result.AtomsImported[e] += atoms;
                }
                bonds += need[s].D * Chem.E[s];
            }
            Flows[FDesign] += bonds;   // the ledger: molecules from outside bring their bonds in
        }
        else
        {
            for (int k = 0; k < sources.Count; k++)
                for (int s = 0; s < Chemistry.S; s++)
                    if (looseTake[k, s].Raw > 0) C[s][sources[k]] -= looseTake[k, s];
            for (int s = 0; s < Chemistry.S; s++)
            {
                for (int j = 0; j < blockTake[s]; j++) TakeVoxelSpecies(top, s);
                if (leftover[s].Raw > 0) C[s][cell] += leftover[s];
            }
        }
        ApplyLocalEnergy(sources, burn);
        return true;
    }
}
