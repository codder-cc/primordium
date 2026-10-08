using System;
using System.Collections.Generic;
using System.Linq;

namespace Primordium;

// Life models: how a body decides what to do. Several live side by side in one world with the same
// physics: every body is matter (Agent.Inv, Pend, proteins Enz), energy (Agent.Energy, HeatHeld), volume,
// a place (X, Y, Z, Lift) and a temperature, all shared; it absorbs, excretes, eats and is eaten, dies
// into the soil, is saved, shown, planted and copied the same way whatever controls it. What differs
// is only the controller and its genome:
//  - the genome is bytes (Agent.G, with Agent.Prot: how much each byte has proven useful), opaque to the
//    world — only the model reads them; the world copies, saves, hashes them (kinship, Agent.Tag) and
//    damages them (UV point hits, inject/cut splices) as a string of bytes;
//  - the controller acts only through the body physics (World.BodyOps.cs, Divide/Mate in World.Life.cs),
//    which books every cost in the shared ledger;
//  - its state between ticks lives on the Agent (model 1: Ip, Stack, Mem, Calls…, in the base save record)
//    or in Agent.ModelState (another model: saved by its SyncState).
// docs/SIMULATION.md "Life models" is the contract; docs/LIFE-MODELS.md how to add one.
public interface ILifeModel
{
    byte Id { get; }          // stored in Agent.Model and in saves (LifeModels.Vm = 1, …): never reuse one
    string Key { get; }       // stable text id for design and population files ("vm")
    string Name { get; }      // for the UI, in the current language

    // One tick of control, in the creature phase (World.LiveBody, before movement and upkeep), under the
    // thread contract of docs/SIMULATION.md "Performance and threads": act only through the body physics,
    // random numbers only from w.Rng, nothing global except through the World's own primitives. `cell` is
    // the body's cell (a.Y * World.W + a.X). No per-call allocation in the common path.
    void Think(World w, Agent a, int cell);

    // The genome changed (birth, load, UV hit, splice): rebuild what the model derives from the bytes.
    void GenomeChanged(Agent a);

    // Heredity. Draw random numbers only from the SimRng given.
    byte[] RandomGenome(SimRng rng);
    (byte[] g, byte[] p) Mutate(byte[] g, byte[] prot, SimRng rng);
    (byte[] g, byte[] p) Cross(Agent a, Agent b, SimRng rng);   // both parents are of this model
    void InheritState(Agent parent, Agent child);               // controller state a newborn starts with

    // A splice into the genome by another body or itself (World.Inject, World.Cut): where an inserted piece
    // goes, and what the controller must adjust before the genome is replaced (removed > 0: a cut).
    int SpliceSite(Agent a);
    void BeforeSplice(Agent a, int at, int removed, int inserted);

    // Controller state that is not in the base body record (model 1: none), after the model id
    // (save version 14). Symmetric: the same calls write and read.
    void SyncState(World.Sync s, Agent a);

    // Text form of a genome for designs and the UI, and back.
    string Describe(byte[] g);
    bool TryCompile(string text, out byte[] genome, out List<string> errors);
    IReadOnlyList<CreatureDesign> DefaultDesigns { get; }
}

public static class LifeModels
{
    public const byte Vm = 1;   // model 1: the genome is a stack-machine program (World.Vm.cs)

    static readonly ILifeModel[] byId = new ILifeModel[256];
    static readonly List<ILifeModel> all = new();

    static LifeModels() => Register(new VmModel());

    public static void Register(ILifeModel m)
    {
        if (m.Id == 0) throw new ArgumentException("life model id 0 is reserved");
        if (byId[m.Id] != null && byId[m.Id] != m) throw new ArgumentException($"life model id {m.Id} is taken by {byId[m.Id].Key}");
        if (all.Any(o => o != m && o.Key == m.Key)) throw new ArgumentException($"life model key {m.Key} is taken");
        if (byId[m.Id] == null) all.Add(m);
        byId[m.Id] = m;
    }

    public static ILifeModel Get(byte id) => byId[id] ?? throw new InvalidOperationException($"no life model {id}");
    public static bool Known(byte id) => byId[id] != null;
    public static IReadOnlyList<ILifeModel> All => all;

    // A model by its text key (null or empty: model 1).
    public static ILifeModel ByKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return byId[Vm];
        foreach (var m in all) if (string.Equals(m.Key, key.Trim(), StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

    // The text key written into files: null for model 1, so files of a world with one model stay as before.
    public static string KeyFor(byte id) => id == Vm ? null : Get(id).Key;
}

// Model 1: today's genome VM, wrapped. Every member forwards to the code that did it before.
public sealed class VmModel : ILifeModel
{
    public byte Id => LifeModels.Vm;
    public string Key => "vm";
    public string Name => Loc.T("genome program (stack machine)", "программа генома (стековая машина)");

    public void Think(World w, Agent a, int cell) => w.Exec(a, cell);

    public void GenomeChanged(Agent a)
    {
        a.Labels = Genome.Labels(a.G);
        if (a.Ip >= a.G.Length) a.Ip = 0;
    }

    public byte[] RandomGenome(SimRng rng) => Genome.Random(rng);
    public (byte[] g, byte[] p) Mutate(byte[] g, byte[] prot, SimRng rng) => Genome.Mutate(g, prot, rng);
    public (byte[] g, byte[] p) Cross(Agent a, Agent b, SimRng rng) => Genome.Cross(a, b, rng);
    public void InheritState(Agent parent, Agent child) => Array.Copy(parent.Mem, child.Mem, P.MemSize);

    // Code is inserted right where the program runs; a cut moves the program back over the hole and
    // forgets the call stack.
    public int SpliceSite(Agent a) => a.Ip;
    public void BeforeSplice(Agent a, int at, int removed, int inserted)
    {
        if (removed <= 0) return;
        if (a.Ip > at) a.Ip = Math.Max(at, a.Ip - removed);
        a.Cp = 0;
    }

    public void SyncState(World.Sync s, Agent a) { }   // Ip, stack, memory, calls, signal: in the base record (World.SyncAgent)

    public string Describe(byte[] g) => GenomeAsm.Disassemble(g);
    public bool TryCompile(string text, out byte[] genome, out List<string> errors)
    {
        bool ok = GenomeAsm.TryAssemble(text, out genome, out var asm);
        errors = asm.Select(e => e.ToString()).ToList();
        return ok;
    }
    public IReadOnlyList<CreatureDesign> DefaultDesigns => CreatureExamples.All;
}
