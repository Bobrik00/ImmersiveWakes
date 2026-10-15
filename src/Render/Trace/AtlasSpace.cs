using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Vintagestory.API.Client;

namespace ImmersiveWakes.Render.Trace;

// Free space of a vanilla atlas, read only. The packer is a binary tree per page (TextureAtlasNode, the same in 1.22.3 and
// 1.22.7): a leaf without children and without textureSubId is free, and AllocateTextureSpace takes the first free leaf
// at least as large as asked, splitting it. A request no leaf takes opens a new page silently (TextureAtlasManager
// .AllocateTextureSpace), 64 MB of GL texture and, under vspt, a new layer of its shared array. GetFreeNode itself
// cannot be asked dry: it splits the leaf it finds. So the tree is walked here through reflection into the engine
// (the public field Atlasses of the manager, the private rootNode of each TextureAtlas). The rectangles of Positions
// give the free strip under the lowest texture of a page as a cross-check that needs no reflection.
static class AtlasSpace
{
    public readonly record struct Leaf(int X, int Y, int W, int H)
    {
        public long Area => (long)W * H;
    }

    // Free leaves of each page in page order, null when the engine's fields are not where 1.22.3 and 1.22.7 keep them.
    public static List<List<Leaf>>? FreeLeaves(ITextureAtlasAPI atlas, out string? fault)
    {
        fault = null;
        try
        {
            FieldInfo? pagesField = FindField(atlas.GetType(), "Atlasses");
            if (pagesField?.GetValue(atlas) is not IList pages) { fault = "no field Atlasses"; return null; }
            var result = new List<List<Leaf>>();
            foreach (object page in pages)
            {
                if (FindField(page.GetType(), "rootNode")?.GetValue(page) is not { } root) { fault = "no field rootNode"; return null; }
                var leaves = new List<Leaf>();
                // TextureAtlas locks the root around GetFreeNode; inserts may run off the main thread.
                lock (root) Walk(root, leaves);
                result.Add(leaves);
            }
            return result;
        }
        catch (Exception e)
        {
            fault = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    // Whether the composition of the atlas has ended: its last stage (TextureAtlasManager.ComposeTextureAtlasses_StageC)
    // runs on a pool thread, walks TextureAtlasPositionsByTextureSubId to its length, and ends with DisposePixels on
    // every page, page 0 first. An allocation before that grows the array with null slots, and the walk throws on the
    // pool thread, which ends the process. UnknownTexturePosition is set at the start of that stage and proves nothing.
    // Only page 0 is asked: a page RuntimeCreateNewAtlas opens later keeps its pixels for good. Null when the fields
    // are not where 1.22.3 and 1.22.7 keep them.
    public static bool? Composed(ITextureAtlasAPI atlas, out string? fault)
    {
        fault = null;
        try
        {
            if (FindField(atlas.GetType(), "Atlasses")?.GetValue(atlas) is not IList pages) { fault = "no field Atlasses"; return null; }
            if (pages.Count == 0 || atlas.UnknownTexturePosition == null || pages[0] is not { } first) return false;
            FieldInfo? pixels = FindField(first.GetType(), "atlasPixels");
            if (pixels == null) { fault = "no field atlasPixels"; return null; }
            return pixels.GetValue(first) == null;
        }
        catch (Exception e)
        {
            fault = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    // Whether some free leaf of an existing page takes w x h; null when the tree could not be read.
    public static bool? Fits(ITextureAtlasAPI atlas, int w, int h, out string? fault)
    {
        List<List<Leaf>>? pages = FreeLeaves(atlas, out fault);
        return pages?.Any(p => p.Any(l => l.W >= w && l.H >= h));
    }

    static void Walk(object node, List<Leaf> into)
    {
        Type t = node.GetType();
        object? left = t.GetField("left")?.GetValue(node), right = t.GetField("right")?.GetValue(node);
        if (left != null || right != null)
        {
            if (left != null) Walk(left, into);
            if (right != null) Walk(right, into);
            return;
        }
        if (t.GetField("textureSubId")?.GetValue(node) != null) return;
        object bounds = t.GetField("bounds")!.GetValue(node)!;
        Type b = bounds.GetType();
        int x1 = (int)b.GetField("x1")!.GetValue(bounds)!, y1 = (int)b.GetField("y1")!.GetValue(bounds)!;
        int x2 = (int)b.GetField("x2")!.GetValue(bounds)!, y2 = (int)b.GetField("y2")!.GetValue(bounds)!;
        if (x2 > x1 && y2 > y1) into.Add(new Leaf(x1, y1, x2 - x1, y2 - y1));
    }

    static FieldInfo? FindField(Type? type, string name)
    {
        for (; type != null; type = type.BaseType)
        {
            FieldInfo? f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (f != null) return f;
        }
        return null;
    }

#if DEBUG
    // The region sizes the audit weighs: 2048 x 1024 for 2000 nodes of 32 texels, 2048 x 512, 1024 x 512 for 16 texels,
    // a 512 x 512 tile.
    static readonly (int W, int H)[] Asks = [(2048, 1024), (2048, 512), (1024, 512), (512, 512)];

    // For '.wakes vspt atlas'.
    public static string Report(string name, ITextureAtlasAPI atlas)
    {
        var sb = new StringBuilder();
        int width = atlas.Size.Width, height = atlas.Size.Height;
        int pageCount = atlas.AtlasTextures.Count;
        sb.Append(CultureInfo.InvariantCulture, $"{name}: {pageCount} page(s) of {width}x{height}");

        // The lowest edge of the textures of each page, from the positions every caller of the API can read.
        var bottom = new int[pageCount];
        foreach (var pos in atlas.Positions)
        {
            if (pos == null || pos.atlasNumber >= pageCount) continue;
            bottom[pos.atlasNumber] = Math.Max(bottom[pos.atlasNumber], (int)Math.Round(pos.y2 * height));
        }

        List<List<Leaf>>? pages = FreeLeaves(atlas, out string? fault);
        for (int p = 0; p < pageCount; p++)
        {
            // FreeTextureSpace clears the tree's node and leaves the position, so a freed region still counts here.
            sb.Append(CultureInfo.InvariantCulture, $"\n  page {p}: strip under the lowest position, freed ones included, {width}x{height - bottom[p]}");
            if (pages == null || p >= pages.Count) continue;
            List<Leaf> leaves = pages[p];
            long free = leaves.Sum(l => l.Area);
            sb.Append(CultureInfo.InvariantCulture, $"; free leaves {leaves.Count}, {free * 100.0 / ((long)width * height):0.0}% of the page");
            foreach (Leaf l in leaves.OrderByDescending(l => l.Area).Take(4))
            {
                sb.Append(CultureInfo.InvariantCulture, $"; {l.W}x{l.H} at {l.X},{l.Y}");
            }
        }
        if (pages == null)
        {
            sb.Append($"\n  packer tree not read ({fault}), sizes below unknown");
            return sb.ToString();
        }
        sb.Append("\n  fits without a new page:");
        foreach ((int w, int h) in Asks)
        {
            bool fits = pages.Any(p => p.Any(l => l.W >= w && l.H >= h));
            sb.Append(CultureInfo.InvariantCulture, $" {w}x{h} {(fits ? "yes" : "no")}");
        }
        return sb.ToString();
    }
#endif
}
