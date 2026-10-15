using System;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace ImmersiveWakes.Render.Trace;

// A rectangle of the vanilla entity atlas whose texels the mod writes for vspt's tracer: the foam's slots (TraceRegion)
// and the splash clouds' images (TraceClouds). vspt's Recorder takes into the traced scene only a draw textured from a
// page of a vanilla atlas (vs-vspt-foreign-renderers-silent), and it copies a rectangle of the entity atlas to its
// tracer in the same frame only when the write went through RenderTextureIntoAtlas (vs-vspt-atlas-writes-reach-tracer).
// The entity atlas, not the block atlas: vspt reads block pages back on the CPU for every write, and a new block page
// it would never see (reference/vspt-audit/wf2-A-recorder-foam.md, section 1).
//
// A request no free leaf of the packer takes would open a new page silently (TextureAtlasManager.AllocateTextureSpace),
// which stays for the world, costs 64 MB of GL texture and another 64 MB layer in vspt, and moves the numbers of the
// item pages under meshes vspt has sent already (refute-cost, section 4); so a region that does not fit is not taken.
// The region has no name: vspt bakes it a fallback material, rough and not metal.
sealed class AtlasRegion : IDisposable
{
    readonly ICoreClientAPI capi;
    int subId;

    AtlasRegion(ICoreClientAPI capi, int subId, int width, int height, TextureAtlasPosition pos, Size2i page, bool newPage)
    {
        this.capi = capi;
        this.subId = subId;
        Width = width;
        Height = height;
        PageWidth = page.Width;
        PageHeight = page.Height;
        Page = pos.atlasNumber;
        PageTextureId = pos.atlasTextureId;
        X = (int)Math.Round(pos.x1 * PageWidth);
        Y = (int)Math.Round(pos.y1 * PageHeight);
        NewPage = newPage;
    }

    public int SubId => subId;
    public int Width { get; }
    public int Height { get; }
    public int X { get; }
    public int Y { get; }
    public int Page { get; }
    public int PageTextureId { get; }
    public int PageWidth { get; }
    public int PageHeight { get; }
    // AllocateTextureSpace opened a page even though the packer's tree had a leaf: the tree changed in between.
    public bool NewPage { get; }

    // Null with the reason when no region is taken; nothing is taken then. The composition of the atlas must have
    // ended (AtlasSpace.Composed).
    public static AtlasRegion? Take(ICoreClientAPI capi, int width, int height, out string why)
    {
        ITextureAtlasAPI atlas = capi.EntityTextureAtlas;
        Size2i entity = atlas.Size, block = capi.BlockTextureAtlas.Size;
        // RenderTextureIntoAtlas draws through the static atlasFramebuffer the engine made for the block atlas
        // (TextureAtlasManager.cs:20, :216, :240 in 1.22.7), and ClientMain.RenderTextureIntoFrameBuffer divides the
        // target by that framebuffer's size.
        if (entity.Width != block.Width || entity.Height != block.Height)
        {
            why = $"entity atlas pages {entity.Width}x{entity.Height} and block atlas pages {block.Width}x{block.Height} differ, the copy would land elsewhere";
            return null;
        }
        if (AtlasSpace.Fits(atlas, width, height, out string? fault) is not true)
        {
            why = fault == null
                ? $"no free {width}x{height} in the entity atlas, a new page would open"
                : $"the entity atlas' packer could not be read ({fault}), a new page might open";
            return null;
        }
        int pagesBefore = atlas.AtlasTextures.Count;
        atlas.AllocateTextureSpace(width, height, out int subId, out TextureAtlasPosition pos);
        why = "";
        return new AtlasRegion(capi, subId, width, height, pos, entity, atlas.AtlasTextures.Count != pagesBefore);
    }

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"region {Width}x{Height} at {X},{Y} of entity atlas page {Page} (texture {PageTextureId}, {PageWidth}x{PageHeight}), sub id {subId}{(NewPage ? ", on a NEW page" : "")}");

    // The page's texture coordinates of texel (x, y) of the region, its corner at -u -v.
    public (float U, float V) Uv(int x, int y) => ((float)(X + x) / PageWidth, (float)(Y + y) / PageHeight);

    // Texels [0, width) x [0, height) of source into the region, rows in their order. Only outside a silent renderer,
    // which Before never is: inside one the copy's own draw would go to the Recorder after the event had already been
    // raised. alphaTest -1 copies every texel, transparent ones too, without blending; the vanilla default 0.005 would
    // keep old texels under transparent new ones. The copy leaves blending off, and the vanilla callers put it back
    // themselves (EntityBehaviorTexturedClothing); RuntimeUploadTextureToPos also turns scissoring off around it,
    // which the caller does. The copy ends with the primary framebuffer bound and its viewport set.
    public void Copy(LoadedTexture source, int width, int height)
    {
        try
        {
            capi.EntityTextureAtlas.RenderTextureIntoAtlas(PageTextureId, source, 0, 0, width, height, X, Y, -1f);
        }
        finally
        {
            capi.Render.GlToggleBlend(true, EnumBlendMode.Standard);
        }
    }

    // FreeTextureSpace clears the packer's leaf and leaves the position in Positions; the atlas manager lives one world,
    // so this is for good order.
    public void Dispose()
    {
        if (subId < 0) return;
        try
        {
            capi.EntityTextureAtlas.FreeTextureSpace(subId);
        }
        finally
        {
            subId = -1;
        }
    }
}
