using System;
using System.Collections.Generic;

namespace Primordium;

// A change of a law, as the world saw it: the trajectory is the seed plus this timeline.
public struct ParamChange
{
    public long Tick;
    public string Name;
    public double Value;
}

// The world's side of tunable laws (P, ParamRegistry). A law changed between ticks is noticed before
// the next tick; whatever the world derived from it is recomputed or invalidated here.
public sealed partial class World
{
    long paramsSeen;   // ParamRegistry.Version the derived tables were built for
    public readonly List<ParamChange> ParamLog = new();   // changes made through SetParam, in order

    // Change a law now (between ticks), record it and bring the derived tables up to date. Returns
    // false for an unknown name. Laws that are not Live only shape the next new world.
    public bool SetParam(string name, double value)
    {
        var p = ParamRegistry.Find(name);
        if (p == null) return false;
        double was = p.Value;
        ParamRegistry.Set(p, value);
        if (p.Value != was) { ParamLog.Add(new ParamChange { Tick = Tick, Name = p.Name, Value = p.Value }); ChronLaw(p, was); }
        ApplyParamChanges();
        return true;
    }

    // Laws to their defaults (recorded like any change).
    public void ResetParams()
    {
        foreach (var p in ParamRegistry.All) SetParam(p.Name, p.Default);
    }

    // Applies a set of values (a preset): those it does not name go back to their defaults.
    public List<string> ApplyParams(IReadOnlyDictionary<string, double> values)
    {
        var unknown = new List<string>();
        foreach (var p in ParamRegistry.All)
            SetParam(p.Name, values != null && values.TryGetValue(p.Name, out double v) ? v : p.Default);
        if (values != null)
            foreach (var name in values.Keys) if (ParamRegistry.Find(name) == null) unknown.Add(name);
        return unknown;
    }

    // Called before every tick (and by SetParam): cheap when nothing changed.
    public void ApplyParamChanges()
    {
        long version = ParamRegistry.Version;
        if (version == paramsSeen) return;
        var effect = ParamRegistry.EffectsSince(paramsSeen);
        paramsSeen = version;
        if ((effect & (ParamEffect.MatCap | ParamEffect.BodyVolume)) != 0) Chem.ApplyParams();
        if ((effect & ParamEffect.BodyVolume) != 0)
            // Room a body takes, from scratch with the new bubble size (what the regression checks it against).
            foreach (var a in Agents)
            {
                if (a.Dead) continue;
                float volume = 0;
                for (int s = 0; s < Chemistry.S; s++) volume += (a.Inv[s] + a.Pend[s]) * Chem.BodyVolume[s];
                for (int k = 0; k < a.EnzN; k++) volume += a.Enz[k].Matter * Chem.Volume[a.Enz[k].Material];
                a.Volume = volume;
            }
        if ((effect & ParamEffect.Strength) != 0)
        {
            // Every block's strength and weight is stale: forget the cached strengths and let the
            // support solver look at every column (pressures are recomputed there).
            Array.Clear(compressionCache);
            for (int c = 0; c < N; c++) { structuralDirty.Add(c); annealable[c] = true; }
        }
    }
}
