using System;
using System.Collections.Generic;

namespace ImmersiveWakes.Simulation;

// A world pixel, floor(world * res) on both axes. Its block is pixel >> power, its cell floorMod(pixel, res).
readonly record struct Pixel(int X, int Z);

// The shapes of Wakes' WakeNode.Factory with the port's fixes (FINDINGS "Как движение сущности превращается в импульсы",
// "Баги и причуды оригинала"). Pixels go to the end of the output list and may repeat, as in Wakes; the handler groups
// them by block. Every cast and float step is Java's, so the shapes match src_port pixel for pixel.
static class TrailRasterizer
{
    // thickNodeTrail: 2w parallel Bresenham lines, w = (int)(0.8 * width * res / 2), each moved by floor(n * i) along
    // the unit normal n = ((z1 - z2), (x2 - x1)) / len for i in [-w, w). Port: the shift is added to the pixel as an
    // integer, Wakes summed x1 + n * i in float, whose step is 0.5 px at VS spawn. A source narrower than one pixel
    // keeps the middle line (fork 7). False with no pixels when both ends fall into one pixel: the normal would be NaN,
    // which Java and .NET 9+ both cast to 0, and Wakes sent the whole trail to pixel (0, 0).
    public static bool Thick(double fromX, double fromZ, double toX, double toZ, float width, int res, List<Pixel> output)
    {
        int x1 = (int)Math.Floor(fromX * res);
        int z1 = (int)Math.Floor(fromZ * res);
        int x2 = (int)Math.Floor(toX * res);
        int z2 = (int)Math.Floor(toZ * res);
        int w = HalfLines(width, res);

        double dz = z1 - z2, dx = x2 - x1;
        float len = (float)Math.Sqrt(dz * dz + dx * dx);
        if (len == 0) return false;
        float nx = (z1 - z2) / len;
        float nz = (x2 - x1) / len;
        for (int i = -w, end = Math.Max(w, 1); i < end; i++)
        {
            int ox = (int)Math.Floor(nx * i), oz = (int)Math.Floor(nz * i);
            Bresenham(x1 + ox, z1 + oz, x2 + ox, z2 + oz, output);
        }
        return true;
    }

    static int HalfLines(float width, int res) => Math.Max((int)(0.8 * width * res / 2), 0);

    // How many times Thick draws its line side by side, the trail's width in pixels. On a slanted course two shifts
    // can floor to the same one, so fewer distinct lines may land, as in Wakes.
    public static int Lines(float width, int res)
    {
        int w = HalfLines(width, res);
        return Math.Max(w, 1) + w;
    }

    // nodeTrail, the paddle stroke of Wakes: one line 1 px thick from end to end. Both ends in one pixel give that
    // pixel.
    public static void Line(double fromX, double fromZ, double toX, double toZ, int res, List<Pixel> output)
    {
        Bresenham((int)Math.Floor(fromX * res), (int)Math.Floor(fromZ * res), (int)Math.Floor(toX * res), (int)Math.Floor(toZ * res), output);
    }

    // splashNodes: the filled disc i, j in [-r, r) with i^2 + j^2 < r^2 around floor(pos * res). No pixel has i or j
    // equal to -r, so the half-open range does not move the disc: it spans 2r - 1 pixels around the centre.
    // r = floor(0.8 * width * res / 2), at least 1 px (fork 7); r = 1 is the centre pixel alone.
    public static int DiscRadius(float width, int res) => Math.Max((int)Math.Floor(0.8 * width * res / 2), 1);

    public static void Disc(double x, double z, float width, int res, List<Pixel> output)
    {
        int r = DiscRadius(width, res);
        int cx = (int)Math.Floor(x * res);
        int cz = (int)Math.Floor(z * res);
        for (int i = -r; i < r; i++)
        {
            for (int j = -r; j < r; j++)
            {
                if (i * i + j * j < r * r) output.Add(new Pixel(cx + i, cz + j));
            }
        }
    }

    // WakesUtils.bresenhamLine with both ends included. Port: the branch for steep lines swaps the ends by z and starts
    // at the x of the low end; Wakes started at x1 and moved every line with falling z by x1 - x2.
    public static void Bresenham(int x1, int z1, int x2, int z2, List<Pixel> output)
    {
        int dz = z2 - z1;
        int dx = x2 - x1;
        if (dx == 0)
        {
            if (z2 < z1) (z1, z2) = (z2, z1);
            for (int z = z1; z < z2 + 1; z++) output.Add(new Pixel(x1, z));
            return;
        }

        float k = (float)dz / dx;
        int adjust = k >= 0 ? 1 : -1;
        int offset = 0;
        if (k <= 1 && k >= -1)
        {
            int delta = Math.Abs(dz) * 2;
            int threshold = Math.Abs(dx);
            int thresholdInc = Math.Abs(dx) * 2;
            int z = z1;
            if (x2 < x1)
            {
                (x1, x2) = (x2, x1);
                z = z2;
            }
            for (int x = x1; x < x2 + 1; x++)
            {
                output.Add(new Pixel(x, z));
                offset += delta;
                if (offset >= threshold)
                {
                    z += adjust;
                    threshold += thresholdInc;
                }
            }
        }
        else
        {
            int delta = Math.Abs(dx) * 2;
            int threshold = Math.Abs(dz);
            int thresholdInc = Math.Abs(dz) * 2;
            int x = x1;
            if (z2 < z1)
            {
                (z1, z2) = (z2, z1);
                x = x2;
            }
            for (int z = z1; z < z2 + 1; z++)
            {
                output.Add(new Pixel(x, z));
                offset += delta;
                if (offset >= threshold)
                {
                    x += adjust;
                    threshold += thresholdInc;
                }
            }
        }
    }
}
