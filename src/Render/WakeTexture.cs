using System;
using ImmersiveWakes.Simulation;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ImmersiveWakes.Render;

// A foam atlas on the GPU: 64x64 slots of Res x Res texels, RGBA8, filled from an ARGB buffer on the CPU that belongs
// to someone else (the core's WakeAtlasCpu, the S1 tiles). Created with raw GL because LoadOrUpdateTextureFromRgba
// rebuilds mipmaps only on creation and uploads only the whole texture (vs-dynamic-texture-upload-path); the raw id goes
// straight into a LoadedTexture, whose Dispose deletes it and whose finalizer reports a missed Dispose (DESIGN П8).
// ARGB ints on little-endian are BGRA bytes, hence PixelFormat.Bgra (DESIGN П6). The wake's atlas keeps 32 texels per
// slot whatever the simulation resolution (fork 14); only the S1 toggle .wakes res makes a new texture.
//
// The texels start undefined: a quad looks only at a slot the core has drawn whole and uploaded in the same frame as
// the quad, and mip levels stop at one texel per slot, so no level mixes a drawn slot with an undefined one.
//
// The GL calls run outside the draw (commands, the Before stage) and leave the active unit as it was and texture 0
// bound on it. The engine keeps no cache of bindings and binds before every use (ClientPlatformWindows.BindTexture2d,
// ShaderProgramBase.BindTexture2D); reading the old binding back with glGet would stall a threaded driver 20 times a
// second.
sealed class WakeTexture : IDisposable
{
    public const int Slots = WakeAtlasCpu.SlotsPerRow;

    readonly ICoreClientAPI capi;
    readonly ILogger log;
    readonly string name;
    LoadedTexture? texture;

    public WakeTexture(ICoreClientAPI capi, ILogger log, string name, int res, bool mipmaps)
    {
        this.capi = capi;
        this.log = log;
        this.name = name;
        Mipmaps = mipmaps;
        Create(res);
    }

    public int Res { get; private set; }
    public int Size => Slots * Res;
    public int TextureId => texture?.TextureId ?? 0;
    public bool Mipmaps { get; private set; }
    public long Uploads { get; private set; }
    public int LastRows { get; private set; }
    // Textures made in this world: GL hands a deleted name out again, so the id alone does not show a rebuild.
    public int Builds { get; private set; }

    // The texels are undefined until the next upload.
    public void SetResolution(int res)
    {
        if (res != Res) Create(res);
    }

    void Create(int res)
    {
        texture?.Dispose();
        texture = null;
        Res = res;
        int id = GL.GenTexture();
        texture = new LoadedTexture(capi, id, Size, Size);
        Builds++;
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, Size, Size, 0, PixelFormat.Bgra,
            PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        // Past level log2(Res) a texel would average neighbouring slots, which hold unrelated nodes, and a grazing view
        // asks for such levels well inside the view distance. GenerateMipmap stops at this level too.
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, (int)Math.Log2(res));
        ApplyFilter();
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    // NEAREST_MIPMAP_LINEAR by default, with the chain rebuilt after every upload: in S1 it looked better at a distance
    // than the NEAREST of Wakes (C7).
    public void SetMipmaps(bool on)
    {
        if (texture == null) return;
        Mipmaps = on;
        GL.BindTexture(TextureTarget.Texture2D, texture.TextureId);
        ApplyFilter();
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    void ApplyFilter()
    {
        if (Mipmaps) GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)(Mipmaps ? TextureMinFilter.NearestMipmapLinear : TextureMinFilter.Nearest));
    }

    // Rows [firstRow, firstRow + rows) of a Size x Size ARGB buffer, the whole width, the way Wakes uploads its active
    // region; the mip chain is rebuilt whole after it unless rebuildMips is false, as under vspt's Skip, where no
    // raster frame reads the texture and the traced foam's bake reads level 0 only.
    public void Upload(int[] pixels, int firstRow, int rows, bool rebuildMips = true)
    {
        if (texture == null || rows <= 0) return;
        if (pixels.Length != Size * Size) throw new ArgumentException($"{name}: {pixels.Length} pixels for a texture of {Size}x{Size}", nameof(pixels));
        if (firstRow < 0 || firstRow + rows > Size) throw new ArgumentOutOfRangeException(nameof(rows), $"{name}: rows {firstRow}..{firstRow + rows} of {Size}");
        GL.BindTexture(TextureTarget.Texture2D, texture.TextureId);
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, firstRow, Size, rows, PixelFormat.Bgra, PixelType.UnsignedByte,
            ref pixels[firstRow * Size]);
        if (Mipmaps && rebuildMips) GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        Uploads++;
        LastRows = rows;
    }

    // The mip chain from level 0 as it stands, after uploads that left it out.
    public void RebuildMips()
    {
        if (texture == null || !Mipmaps) return;
        GL.BindTexture(TextureTarget.Texture2D, texture.TextureId);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Dispose()
    {
        if (texture == null) return;
        int id = texture.TextureId;
        texture.Dispose();
        texture = null;
        log.Notification("{0}", $"{name} {id} disposed");
    }
}
