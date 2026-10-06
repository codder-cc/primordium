using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Primordium;

// Census, records and the largest lineages in one pass over the bodies (in parallel chunks), taken on
// the simulation thread between ticks. Published as a whole and never changed afterwards.
public sealed class SimStats
{
    public Census Census = new();
    public World.Climate Climate;
    public List<(long lin, int n, int gen, Agent rep)> Lineages = new();
    public List<(string name, Agent a, string value)> Records = new();
    public int KinCount;

    const int R = 16;
    static readonly string[] RecordNames =
    {
        "старейший", "больше всех детей", "самое глубокое поколение", "самый крупный", "занимает больше всех клеток",
        "самый сытый", "больше всех белков", "самый закреплённый геном", "самый длинный геном", "главный убийца",
        "больше всех атак", "распространитель генов", "больше всех спаривался", "в самой тесной клетке",
        "самое горячее тело", "самое холодное тело",
    };
    static readonly bool[] NeedPositive = { false, true, false, false, false, false, true, true, false, true, true, true, true, false, false, false };

    sealed class Part
    {
        public readonly Census C = new();
        public readonly float[] Best = new float[R];
        public readonly Agent[] BestA = new Agent[R];
        public readonly Dictionary<long, (int n, int gen, Agent rep, int first)> Lin = new();
        public int Kin;
        public Part() { Array.Fill(Best, float.MinValue); }
    }

    public static SimStats Compute(World w, Agent sel)
    {
        var agents = w.Agents;
        int n = agents.Count;
        int chunks = Math.Clamp(n / 2048, 1, 16);
        var parts = new Part[chunks];
        Parallel.For(0, chunks, ch =>
        {
            var p = parts[ch] = new Part();
            var c = p.C;
            Span<float> v = stackalloc float[R];
            int from = (int)((long)n * ch / chunks), to = (int)((long)n * (ch + 1) / chunks);
            for (int i = from; i < to; i++)
            {
                var a = agents[i];
                if (a.Dead) continue;
                int cell = a.Y * World.W + a.X;
                c.Pop++;
                c.AvgLen += a.G.Length;
                c.AvgEnergy += a.Energy;
                c.AvgAge += a.Age;
                c.AvgCycles += a.LastCycles;
                c.AvgTb += a.Tb;
                if (a.Age > c.OldestAge) c.OldestAge = a.Age;
                if (a.Links.Count > 0) c.Linked++;
                if (w.Count[cell] > 1) c.Crowded++;
                if (a.Cells > 1) c.Big++;
                if (a.Cells > c.MaxCells) c.MaxCells = a.Cells;
                if (w.InWater(a))
                {
                    c.InWater++;
                    if (!World.OnFloor(a)) c.Afloat++;
                    if (w.AtSurface(a, cell)) c.AtSurface++;
                }
                float enzTotal = 0;
                for (int k = 0; k < a.EnzN; k++) { c.EnzKind[a.Enz[k].Kind] += a.Enz[k].Amount; enzTotal += a.Enz[k].Amount; }
                c.AvgEnz += a.EnzN;
                int prot = 0;
                foreach (var b in a.Prot) if (b > 20) prot++;
                float protShare = prot / (float)a.Prot.Length;
                c.AvgProt += protShare;
                switch (World.Diet(a))
                {
                    case World.DietPlant: c.Plants++; break;
                    case World.DietEater: c.Eaters++; break;
                    case World.DietMiner: c.Miners++; break;
                    case World.DietHunter: c.Hunters++; break;
                    default: c.Idle++; break;
                }

                v[0] = a.Age; v[1] = a.NChildren; v[2] = a.Gen; v[3] = a.Mass; v[4] = a.Cells + a.Mass * 1e-4f;
                v[5] = a.Energy; v[6] = enzTotal; v[7] = protShare; v[8] = a.G.Length; v[9] = a.NKills;
                v[10] = a.NAttacks; v[11] = a.NInjects; v[12] = a.NMates; v[13] = w.Count[cell]; v[14] = a.Tb; v[15] = -a.Tb;
                for (int r = 0; r < R; r++)
                    if (v[r] > p.Best[r]) { p.Best[r] = v[r]; p.BestA[r] = a; }

                if (p.Lin.TryGetValue(a.Lineage, out var l))
                    p.Lin[a.Lineage] = (l.n + 1, Math.Max(l.gen, a.Gen), a.Gen > l.gen ? a : l.rep, l.first);
                else p.Lin[a.Lineage] = (1, a.Gen, a, i);

                if (sel != null && a != sel && Looks.Kin(a, sel) > 0) p.Kin++;
            }
        });

        var s = new SimStats { Climate = w.TakeClimate() };
        var cs = s.Census;
        var best = new float[R];
        var bestA = new Agent[R];
        Array.Fill(best, float.MinValue);
        var lin = new Dictionary<long, (int n, int gen, Agent rep, int first)>();
        foreach (var p in parts)
        {
            var c = p.C;
            cs.Pop += c.Pop; cs.Idle += c.Idle; cs.Plants += c.Plants; cs.Eaters += c.Eaters; cs.Miners += c.Miners; cs.Hunters += c.Hunters;
            cs.Linked += c.Linked; cs.Crowded += c.Crowded; cs.Big += c.Big;
            cs.OldestAge = Math.Max(cs.OldestAge, c.OldestAge); cs.MaxCells = Math.Max(cs.MaxCells, c.MaxCells);
            cs.InWater += c.InWater; cs.Afloat += c.Afloat; cs.AtSurface += c.AtSurface;
            cs.AvgLen += c.AvgLen; cs.AvgEnergy += c.AvgEnergy; cs.AvgAge += c.AvgAge; cs.AvgCycles += c.AvgCycles;
            cs.AvgEnz += c.AvgEnz; cs.AvgProt += c.AvgProt; cs.AvgTb += c.AvgTb;
            for (int k = 0; k < 4; k++) cs.EnzKind[k] += c.EnzKind[k];
            for (int r = 0; r < R; r++)
                if (p.BestA[r] != null && p.Best[r] > best[r]) { best[r] = p.Best[r]; bestA[r] = p.BestA[r]; }
            foreach (var (k, l) in p.Lin)
            {
                if (lin.TryGetValue(k, out var o))
                    lin[k] = (o.n + l.n, Math.Max(o.gen, l.gen), l.gen > o.gen ? l.rep : o.rep, Math.Min(o.first, l.first));
                else lin[k] = l;
            }
            s.KinCount += p.Kin;
        }
        if (cs.Pop > 0)
        {
            cs.AvgLen /= cs.Pop; cs.AvgEnergy /= cs.Pop; cs.AvgAge /= cs.Pop; cs.AvgCycles /= cs.Pop;
            cs.AvgTb /= cs.Pop; cs.AvgEnz /= cs.Pop; cs.AvgProt /= cs.Pop;
            for (int k = 0; k < 4; k++) cs.EnzKind[k] /= cs.Pop;
        }
        s.Lineages = lin.OrderByDescending(t => t.Value.n).ThenBy(t => t.Value.first).Take(5)
            .Select(t => (t.Key, t.Value.n, t.Value.gen, t.Value.rep)).ToList();

        for (int r = 0; r < R; r++)
        {
            var a = bestA[r];
            if (a == null || (NeedPositive[r] && best[r] <= 0)) continue;
            string value = r switch
            {
                0 => $"возраст {a.Age:N0}",
                1 => $"детей {a.NChildren}",
                2 => $"поколение {a.Gen}",
                3 => $"масса {a.Mass:0} · {a.Cells} кл.",
                4 => $"{a.Cells} клеток · масса {a.Mass:0}",
                5 => $"энергия {a.Energy:0}",
                6 => $"белков {a.EnzymeTotal:0.0}",
                7 => $"закреплено {best[r]:P0}",
                8 => $"{a.G.Length} байт",
                9 => $"убил {a.NKills}",
                10 => $"атак {a.NAttacks}",
                11 => $"вставок {a.NInjects}",
                12 => $"спариваний {a.NMates}",
                13 => $"соседей {w.Count[a.Y * World.W + a.X] - 1}",
                _ => $"{a.Tb:+0;-0} °C",
            };
            s.Records.Add((RecordNames[r], a, value));
        }
        return s;
    }
}
