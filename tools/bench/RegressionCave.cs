using System;
using System.Linq;

namespace Primordium;

public sealed partial class World
{
    // Cave climate (World.Cave, ROADMAP 2.1). On the flat bedrock fixture: a column with a void at level
    // 2 under 5 bedrock blocks, another under 1 block, a body inside each and one on top of each roof.
    // A short year (2 days of the usual 1200 ticks), two years, no rain, no harm
    // from temperature (it is the temperature that is measured, not survival), no altitude lapse.
    // - the body under 5 blocks is warmer than the one on its roof over the last (winter) day, and its
    //   daily swing of Tb (around the day's trend) is under 20% of the roof body's;
    // - under 1 block: partial shelter (swing below 90% of its roof body's, above the deep body's);
    // - bodies under a roof warm their cave air (CaveWarm > 0); the energy ledger closes (heat is a sink);
    // - the law off: FootTemp is the surface temperature again (bit for bit) and the deep body swings
    //   with the surface;
    // - the felt temperature and the sensor `temp` agree; cover counts solid blocks only (voids skipped).
    public static void CaveClimateRegression()
    {
        ParamRegistry.ResetDefaults();
        P.YearDays = 2; P.RainShare = 0; P.TLapse = 0;
        P.FreezeK = 0; P.HeatK = 0; P.HoldK = 0;
        var w = Fixture();
        w.AutoStrikes = false;
        int y = 40, deep = y * w.W + 40, shallow = y * w.W + 60;
        void Roof(int c, int top)
        {
            for (int z = 3; z < top; z++) { w.Mat[c * w.Z + z] = Chemistry.Bedrock; w.Order[c * w.Z + z] = 255; }
            w.Height[c] = top;
            w.TerrainChanged(c);
        }
        Roof(deep, 8); Roof(shallow, 4);
        w.StepStructure();
        Require(w.Roof(deep, 2) == 5 && w.Roof(shallow, 2) == 1 && w.Roof(deep, 8) == 0 && w.Cover(deep, 8) == 0, "roof count");
        var inDeep = w.TestAgent(deep, 2, 0, 8); var onDeep = w.TestAgent(deep, 8, 0, 8);
        var inShallow = w.TestAgent(shallow, 2, 0, 8); var onShallow = w.TestAgent(shallow, 4, 0, 8);
        foreach (var a in new[] { inDeep, onDeep, inShallow, onShallow }) a.Energy = 100;
        w.TrackHeat = true;
        var e0 = w.AuditEnergy();
        int day = P.DayLen, ticks = 4 * day;
        var tb = new float[4][];
        for (int k = 0; k < 4; k++) tb[k] = new float[day];
        var bodies = new[] { inDeep, onDeep, inShallow, onShallow };
        for (int t = 0; t < ticks; t++)
        {
            w.Step();
            if (t >= ticks - day) for (int k = 0; k < 4; k++) tb[k][t - (ticks - day)] = bodies[k].Tb;
        }
        Require(bodies.All(a => !a.Dead && a.X == (a == inDeep || a == onDeep ? 40 : 60)), "a test body died or moved");
        Require(w.SunDecl < 0, "the last day is not a northern winter");
        // The daily swing: max − min around the day's linear trend (the season moves on during the day).
        static float Swing(float[] x)
        {
            int n = x.Length;
            double mt = (n - 1) / 2.0, mx = x.Average(), sxy = 0, sxx = 0;
            for (int t = 0; t < n; t++) { sxy += (t - mt) * (x[t] - mx); sxx += (t - mt) * (t - mt); }
            double slope = sxy / sxx, lo = double.MaxValue, hi = double.MinValue;
            for (int t = 0; t < n; t++) { double r = x[t] - mx - slope * (t - mt); lo = Math.Min(lo, r); hi = Math.Max(hi, r); }
            return (float)(hi - lo);
        }
        float sDeep = Swing(tb[0]), sTop = Swing(tb[1]), sShallow = Swing(tb[2]), sTop1 = Swing(tb[3]);
        float mDeep = tb[0].Average(), mTop = tb[1].Average();
        Require(mDeep > mTop + 1, $"the body under 5 blocks is not warmer in winter: {mDeep:F2} vs {mTop:F2} on its roof");
        Require(sTop > 2 && sDeep < 0.2f * sTop, $"daily swing under 5 blocks {sDeep:F2} °C vs {sTop:F2} on the surface (want < 20%)");
        Require(sShallow < 0.9f * sTop1 && sShallow > sDeep, $"partial shelter under 1 block: swing {sShallow:F2} vs {sTop1:F2} on its roof, {sDeep:F2} under 5");
        Require(w.CaveWarm[deep] > 0 && w.CaveWarm[shallow] > 0, "bodies under a roof did not warm the cave air");
        int cell = deep;
        float felt = w.LocalTemp(deep, 2);
        Require(MathF.Abs(felt - w.FootTemp(inDeep)) < 1e-6f, "FootTemp is not the felt temperature");
        string energy = EnergyWorldCheck(w, e0, "cave climate");
        // The sensor reads the same.
        var probe = new Agent(w.NewId(), 0, 0, new byte[] { Genome.Temp, Genome.Yield, 0, 0, 0, 0, 0, 0 }) { Energy = 10, Z = 2, Tb = 15, X = 40, Y = y };
        w.Exec(probe, cell);
        Require(probe.Stack[0] == (int)felt, $"the temp sensor reads {probe.Stack[0]}, felt {felt:F2}");

        // Off: the old expression, the surface temperature for every body.
        P.CaveClimate = 0;
        Require(w.FootTemp(inDeep) == w.Temp[deep] && w.FootTemp(inShallow) == w.Temp[shallow], "the law off does not give the surface temperature");
        var off = new float[2][]; off[0] = new float[day]; off[1] = new float[day];
        for (int t = 0; t < day; t++) { w.Step(); off[0][t] = inDeep.Tb; off[1][t] = onDeep.Tb; }
        float sOff = Swing(off[0]), sOffTop = Swing(off[1]);
        Require(sOff > 0.8f * sOffTop, $"the law off still shelters: swing {sOff:F2} vs {sOffTop:F2}");
        ParamRegistry.ResetDefaults();
        Console.WriteLine($"PASS cave climate: winter day under 5 blocks {mDeep:+0.0;-0.0} °C vs {mTop:+0.0;-0.0} on the roof, daily swing {sDeep:F2} vs {sTop:F2} ({sDeep / sTop:P0}); " +
                          $"under 1 block {sShallow:F2} vs {sTop1:F2} ({sShallow / sTop1:P0}); cave air warmed {w.CaveWarm[deep]:F3} °C; law off: swing {sOff:F2} vs {sOffTop:F2}; {energy}");
    }
}
