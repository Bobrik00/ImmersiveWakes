using System;
using System.Numerics;

namespace ImmersiveWakes.Simulation;

// The wave field of one block, Wakes' SimulationNode.WakeSimulation: res x res cells inside a ghost ring, three time
// layers and the impulses waiting for the next step. Flat arrays [layer][z][x] with Stride = res + 2; ghost cells
// take index 0 and res + 1 on both axes (FINDINGS "Сетка").
sealed class SimulationNode
{
    public readonly int Res;
    public readonly int Stride;
    public readonly int LayerSize;

    // Layer 0 is the next step, 1 the current one, 2 the previous one. The stencil reads the ghost ring of layer 1,
    // where CopyHalo or SnapHalo puts the neighbours' edges; no other ghost cell is read by the step.
    public readonly float[] U;

    // A stamp at the node's edge spills into the ghost ring, which nobody reads, so an impulse never crosses into
    // the neighbour (FINDINGS "Импульсы").
    public readonly float[] Impulses;

    public SimulationNode(int res)
    {
        Res = res;
        Stride = res + 2;
        LayerSize = Stride * Stride;
        U = new float[3 * LayerSize];
        Impulses = new float[LayerSize];
    }

    // Ghost ring included: a ghost row left by the previous owner of the arrays changes texels
    // (FINDINGS "Шаг волнового уравнения").
    public void Clear()
    {
        Array.Clear(U);
        Array.Clear(Impulses);
    }

    // setInitialValue: the 3x3 stamp around inner cell (cellZ, cellX) is overwritten, not added to, with
    // value * res/16. The next step adds it to u^n before the layers shift and leaves u^(n-1) as it was, so the stamp
    // starts moving and spreads as a ring (FINDINGS "Импульсы").
    public void SetImpulse(int cellZ, int cellX, int value)
    {
        float v = value * (Res / 16f);
        for (int z = cellZ; z <= cellZ + 2; z++)
        {
            int row = z * Stride;
            Impulses[row + cellX] = v;
            Impulses[row + cellX + 1] = v;
            Impulses[row + cellX + 2] = v;
        }
    }

    // The halo of WakeSimulation.tick, into the ghost ring of layer 1. Rows N and S go whole, res + 2 cells, then the
    // columns E and W over z = 0..res + 1 win the corners: copying cells 1..res only, or the columns first, changes
    // texels. Wakes also copies layer 2, whose ring nobody reads. In Wakes the rows N and S were the neighbour's own
    // arrays, so the columns written over them reached the neighbour's corners; here the rows are copies (fork 17).
    // A side without a neighbour keeps what its ring holds: zeros of a fresh node, which reflect the wave, or the last
    // row of a neighbour that died. A neighbour that has stepped this tick gives u^n, one that has not gives u^(n-1)
    // (FINDINGS "Шаг волнового уравнения").
    public void CopyHalo(SimulationNode? north, SimulationNode? south, SimulationNode? east, SimulationNode? west)
    {
        float[] u = U;
        int s = Stride, l1 = LayerSize, end = 2 * LayerSize;
        if (north != null) Array.Copy(north.U, l1 + Res * s, u, l1, s);
        if (south != null) Array.Copy(south.U, l1 + s, u, l1 + (Res + 1) * s, s);
        if (east != null)
        {
            float[] e = east.U;
            for (int row = l1; row < end; row += s) u[row + Res + 1] = e[row + 1];
        }
        if (west != null)
        {
            float[] w = west.U;
            for (int row = l1; row < end; row += s) u[row] = w[row + Res];
        }
    }

    // The synchronous halo of src_port_snap, called for every node before any node steps. Each cell of the ring of
    // layer 1 gets what the neighbour's layer 1 will hold after its own shift, u0 plus its impulses summed as Step
    // sums them; the corners come from the diagonal neighbours. A missing neighbour gives 0, also one unlinked after
    // dying in an earlier tick, where CopyHalo keeps its last row; one that dies in this tick still gives its own.
    public void SnapHalo(SimulationNode? north, SimulationNode? south, SimulationNode? east, SimulationNode? west,
        SimulationNode? northEast, SimulationNode? northWest, SimulationNode? southEast, SimulationNode? southWest)
    {
        int s = Stride, l1 = LayerSize, last = Res * s;
        Edge(l1 + 1, 1, north, last + 1);
        Edge(l1 + (Res + 1) * s + 1, 1, south, s + 1);
        Edge(l1 + s + Res + 1, s, east, s + 1);
        Edge(l1 + s, s, west, s + Res);
        U[l1] = Next(northWest, last + Res);
        U[l1 + Res + 1] = Next(northEast, last + 1);
        U[l1 + (Res + 1) * s] = Next(southWest, s + Res);
        U[l1 + (Res + 1) * s + Res + 1] = Next(southEast, s + 1);
    }

    // Res cells of the ring from `to` on, read from the neighbour's inner cells from `at` on, both a step apart.
    void Edge(int to, int step, SimulationNode? from, int at)
    {
        for (int k = 0; k < Res; k++, to += step, at += step) U[to] = Next(from, at);
    }

    static float Next(SimulationNode? node, int i) => node == null ? 0 : node.U[i] + node.Impulses[i];

    // True when no inner cell of the three layers is above limit in magnitude: maxAbsU() <= limit of src_port_amp,
    // read before the step. The ring holds the neighbours' edges and is left out; a NaN cell is never quiet, as a NaN
    // maximum fails the test in Java.
    public bool IsQuiet(float limit)
    {
        float[] u = U;
        for (int layer = 0; layer < 3 * LayerSize; layer += LayerSize)
        {
            for (int z = 1; z <= Res; z++)
            {
                for (int i = layer + z * Stride + 1, end = i + Res; i < end; i++)
                {
                    if (!(Math.Abs(u[i]) <= limit)) return false;
                }
            }
        }
        return true;
    }

    // Both loops of WakeSimulation.tick after the halo, all in float with the sums grouped as in the oracle's src_flt
    // (T05.10): L = 0.5(W + E + N + S) + 0.25(NW + NE + SW + SE) - 3C, then beta(2C - u2 + alpha L). Java's mixed
    // precision, with the sums and the alpha product in double, cost 10-11 ns a cell; this one parts from it by
    // |du| up to 2.6e-5 and changes no node, age or texel, and matches the oracle run with -Dwakes.floatStencil=true
    // bit for bit (FINDINGS "Java-эталон ядра"). A lane of the vectors does the scalar tail's operations in its
    // order, so both give the same bits. The layers shift by copying, not by swapping arrays: the ghost ring of
    // layer 1 has to stay where the halo put it. Layer 0 is not written by the shift, as the stencil overwrites it.
    public void Step(float alpha, float beta)
    {
        float[] u = U, init = Impulses;
        int s = Stride, l1 = LayerSize, l2 = 2 * LayerSize, n = Vector<float>.Count;
        bool vectors = Vector.IsHardwareAccelerated;

        for (int z = 1; z <= Res; z++)
        {
            int i = z * s + 1, end = i + Res;
            if (vectors)
            {
                for (; i <= end - n; i += n)
                {
                    var next = new Vector<float>(u, i) + new Vector<float>(init, i);
                    new Vector<float>(u, l1 + i).CopyTo(u, l2 + i);
                    next.CopyTo(u, l1 + i);
                }
            }
            for (; i < end; i++)
            {
                u[l2 + i] = u[l1 + i];
                u[l1 + i] = u[i] + init[i];
            }
        }
        // The ring takes the spill of stamps at the edge, which nobody reads.
        Array.Clear(init);

        Vector<float> half = new(0.5f), quarter = new(0.25f), two = new(2f), three = new(3f), a = new(alpha), b = new(beta);
        for (int z = 1; z <= Res; z++)
        {
            int i = z * s + 1, end = i + Res;
            if (vectors)
            {
                for (; i <= end - n; i += n)
                {
                    int c = l1 + i;
                    var centre = new Vector<float>(u, c);
                    Vector<float> laplace =
                        half * (new Vector<float>(u, c - 1) + new Vector<float>(u, c + 1) + new Vector<float>(u, c - s) + new Vector<float>(u, c + s))
                        + quarter * (new Vector<float>(u, c - s - 1) + new Vector<float>(u, c - s + 1) + new Vector<float>(u, c + s - 1) + new Vector<float>(u, c + s + 1))
                        - three * centre;
                    (b * (two * centre - new Vector<float>(u, l2 + i) + a * laplace)).CopyTo(u, i);
                }
            }
            for (; i < end; i++)
            {
                int c = l1 + i;
                float centre = u[c];
                float laplace = 0.5f * (u[c - 1] + u[c + 1] + u[c - s] + u[c + s])
                    + 0.25f * (u[c - s - 1] + u[c - s + 1] + u[c + s - 1] + u[c + s + 1]) - 3f * centre;
                u[i] = beta * (2f * centre - u[l2 + i] + alpha * laplace);
            }
        }
    }

    // Inner cell (x, z) of a layer, 0-based without the ghost ring.
    public float Get(int layer, int z, int x) => U[layer * LayerSize + (z + 1) * Stride + x + 1];

    // The height a texel is coloured by, the mean of the three layers in float as getPixelColor takes it.
    public float Height(int z, int x)
    {
        int i = (z + 1) * Stride + x + 1;
        return (U[i] + U[LayerSize + i] + U[2 * LayerSize + i]) / 3;
    }

    // Height of every cell of row z. The lanes add and divide in the order Height does, so the bits are the same.
    public void HeightRow(int z, Span<float> into)
    {
        int i = (z + 1) * Stride + 1;
        ReadOnlySpan<float> u0 = U.AsSpan(i, Res), u1 = U.AsSpan(LayerSize + i, Res), u2 = U.AsSpan(2 * LayerSize + i, Res);
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var three = new Vector<float>(3f);
            for (; x <= Res - Vector<float>.Count; x += Vector<float>.Count)
            {
                ((new Vector<float>(u0[x..]) + new Vector<float>(u1[x..]) + new Vector<float>(u2[x..])) / three).CopyTo(into[x..]);
            }
        }
        for (; x < Res; x++) into[x] = (u0[x] + u1[x] + u2[x]) / 3;
    }
}
