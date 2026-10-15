using System;
using ImmersiveWakes.Effects;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Render.Trace;

// The splash clouds' quads for vspt's Recorder (TraceQuads), one per cloud, facing the camera as wakescloud.vsh turns
// them: the corners go along the camera's right and up, the rows of the view rotation, HalfSize from the centre. The
// clouds move every step, so every write goes to the tracer, and the anchor moves with the camera as the foam's does.
//
// No wave: vspt's water mesh has none (vs-vspt-water-effects). With the camera above the water the part of the quad
// under the still surface is cut off, with its texels: the raster's murk test leaves that part to the water, and the
// tracer would show it through the water as a sprite sunk in it. The cut runs along the quad's rows at the height of
// its lower bottom corner, which a roll of the camera (none in vanilla) tilts. With the camera's up nearly level
// (looking straight down) the quad lies flat, and a cloud under the surface is left out whole. Over a hull's water mask
// the cut runs at the higher of the surface and the highest plate over the cloud's centre while the eye is above that
// plate (TraceHulls.Deck), with the camera under the water too: the raster's plates hide by depth what lies under them,
// and from below them the hull's own planks hide the cloud.
// Corners and indices as the raster's quad: counter-clockwise from the camera.
sealed class TraceCloudMesh : TraceQuads
{
    // Corners along right and up in the order of wakescloud's quad: the bottom two first.
    static ReadOnlySpan<int> CornerRight => new[] { -1, 1, 1, -1 };

    public TraceCloudMesh(ICoreClientAPI capi, int capacity)
        : base(capi, capacity)
    {
    }

    // The first count of clouds over the images of region, size texels each, an image an age along u and a sprite
    // along v (TraceClouds); view is the camera's view matrix at the origin, column-major; cut when the camera is above
    // the water; decks the hulls of the frame, null for none, and eyeY the camera's height.
    public void Write(SplashCloudPool.TracedCloud[] clouds, int count, AtlasRegion region, int size, int ages, Vec3d camera, float[] view, bool cut,
        TraceHulls? decks, double eyeY)
    {
        MoveAnchor(camera);
        float rx = view[0], ry = view[4], rz = view[8];
        float ux = view[1], uy = view[5], uz = view[9];
        const float half = SplashCloudPool.HalfSize;
        float sideU = (float)size / region.PageWidth, sideV = (float)size / region.PageHeight;
        // 1/64 of a texel of the page inside the image, as the foam keeps inside its slots.
        float insetU = 1f / (64f * region.PageWidth), insetV = 1f / (64f * region.PageHeight);
        float[] xyz = Data.xyz, uv = Data.Uv;
        int quads = 0;
        for (int i = 0; i < count && quads < Capacity; i++)
        {
            ref SplashCloudPool.TracedCloud c = ref clouds[i];
            // How far down the quad reaches along up, -1 for its whole height: to the still surface or the deck at most.
            float bottom = -1;
            bool cutHere = cut;
            double floor = c.SurfaceY;
            if (decks != null && decks.Deck(c.X, c.SurfaceY, c.Z, out double deck) && eyeY > deck)
            {
                cutHere = true;
                floor = Math.Max(floor, deck);
                decks.Decked++;
            }
            double above = c.Y - floor;
            if (cutHere && uy > 1e-3f)
            {
                bottom = Math.Max(-1f, (float)((Math.Abs(ry) * half - above) / (uy * half)));
                if (bottom >= 1) continue;
            }
            else if (cutHere && above < 0)
            {
                continue;
            }
            (float u0, float v0) = region.Uv(Math.Clamp(c.Age, 0, ages - 1) * size, c.Sprite * size);
            float cx = (float)(c.X - AnchorX), cy = (float)(c.Y - AnchorY), cz = (float)(c.Z - AnchorZ);
            int q = quads++;
            for (int k = 0; k < 4; k++)
            {
                int v = q * 4 + k, across = CornerRight[k];
                float up = k < 2 ? bottom : 1;
                xyz[v * 3] = cx + (rx * across + ux * up) * half;
                xyz[v * 3 + 1] = cy + (ry * across + uy * up) * half;
                xyz[v * 3 + 2] = cz + (rz * across + uz * up) * half;
                uv[v * 2] = u0 + (across < 0 ? insetU : sideU - insetU);
                // The image's top row at up 1, its bottom row at up -1.
                uv[v * 2 + 1] = v0 + Math.Clamp((1 - up) / 2 * sideV, insetV, sideV - insetV);
            }
        }
        Commit(quads);
    }
}
