using System;

namespace Primordium;

// Big bodies. Mass beyond a threshold makes a body spread over neighbouring cells (see P.GrowMass):
// it then catches light and drinks soil in all of them, feels their temperature and poisons, sheds
// its heat into them, and can be reached by whoever lives there. Small bodies can share those
// cells; two big bodies can't overlap.
public sealed partial class World
{
    public readonly Agent[] Big = new Agent[N];   // the big body (if any) covering a cell beyond its own

    // The order in which a growing body spreads: around itself first, then one step further.
    static readonly (int dx, int dy)[] SpreadOrder =
    {
        (0, 0), (1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1), (1, -1), (-1, -1), (2, 0), (0, 2), (-2, 0), (0, -2),
    };

    int Offset(int cell, int dx, int dy)
    {
        int x = cell % W, y = cell / W + dy;
        return y < 0 || y >= H ? -1 : y * W + ((x + dx) % W + W) % W;
    }

    int FootCell(Agent a, int k) => k == 0 ? a.Y * W + a.X : a.Foot[k];

    // How far a body reaches beyond its own cell.
    static int Reach(Agent a) => a.Cells == 1 ? 0 : a.Cells <= 9 ? 1 : 2;

    void ReleaseFoot(Agent a)
    {
        for (int k = 1; k < a.Cells; k++)
            if (Big[a.Foot[k]] == a) Big[a.Foot[k]] = null;
        a.Cells = 1;
    }

    // Lay the body over as many cells as its mass calls for — where the ground is not too steep and
    // no other big body is in the way.
    void SpreadBody(Agent a, int cell)
    {
        int want = Agent.CellsFor(a.Mass);
        if (want == 1 && a.Cells == 1) return;
        ReleaseFoot(a);
        a.Foot[0] = cell;
        int n = 1, h0 = a.Z;
        for (int k = 1; k < SpreadOrder.Length && n < want; k++)
        {
            int c = Offset(cell, SpreadOrder[k].dx, SpreadOrder[k].dy);
            if (c < 0 || Big[c] != null || WalkLevel(c, h0) != h0) continue;
            Big[c] = a;
            a.Foot[n++] = c;
        }
        a.Cells = n;
    }

    // Everybody a body could deal with: others in any of its cells, and big bodies covering them.
    int Candidates(Agent a)
    {
        int n = 0;
        for (int k = 0; k < a.Cells; k++)
        {
            int c = FootCell(a, k);
            if (!HasCavity[c] && a.Z == Height[c] && Water[c] < P.SwimDepth) n += Count[c] - (k == 0 ? 1 : 0);   // all on one dry floor
            else for (var o = Head[c]; o != null; o = o.NextInCell) if (o != a && Near(a, o)) n++;
            if (Big[c] != null && Big[c] != a && Near(a, Big[c])) n++;
        }
        return n;
    }

    Agent CandidateAt(Agent a, int idx)
    {
        for (int k = 0; k < a.Cells; k++)
        {
            int c = FootCell(a, k);
            for (var o = Head[c]; o != null; o = o.NextInCell)
            {
                if (o == a || !Near(a, o)) continue;
                if (idx-- == 0) return o;
            }
            if (Big[c] != null && Big[c] != a && Near(a, Big[c]) && idx-- == 0) return Big[c];
        }
        return null;
    }

    // Of the body's cells: the one richest in molecule s / with the most light caught / the most
    // dissolved matter overall.
    int RichestFor(Agent a, int s)
    {
        int best = a.Y * W + a.X;
        float most = LooseAmount(a, best, s);
        for (int k = 1; k < a.Cells; k++)
        {
            float here = LooseAmount(a, a.Foot[k], s);
            if (here > most) { most = here; best = a.Foot[k]; }
        }
        return best;
    }

    int BrightestCell(Agent a)
    {
        int best = -1;
        for (int k = 0; k < a.Cells; k++)
        {
            int c = FootCell(a, k);
            if (a.Z >= Height[c] && (best < 0 || Photon[c] > Photon[best])) best = c;
        }
        return best;
    }

    int FullestCell(Agent a)
    {
        int best = a.Y * W + a.X;
        if (a.Cells == 1) return best;
        float bv = -1;
        for (int k = 0; k < a.Cells; k++)
        {
            int c = FootCell(a, k);
            float t = 0;
            var fb = FloorBurial(a, c);
            for (int s = 0; s < Chemistry.S; s++) t += Loose(a, c, fb, s);
            if (t > bv) { bv = t; best = c; }
        }
        return best;
    }

    // The temperature around the body: the surface's, or under a roof the cave climate (World.Cave).
    float FootTemp(Agent a)
    {
        if (!CaveLaw)
        {
            if (a.Cells == 1) return Temp[a.Y * W + a.X];
            float s = 0;
            for (int k = 0; k < a.Cells; k++) s += Temp[FootCell(a, k)];
            return s / a.Cells;
        }
        if (a.Cells == 1) return LocalTemp(a.Y * W + a.X, a.Z);
        float t = 0;
        for (int k = 0; k < a.Cells; k++) t += LocalTemp(FootCell(a, k), a.Z);
        return t / a.Cells;
    }
}
