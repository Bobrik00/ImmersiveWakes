using System;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ImmersiveWakes.Effects;

// Sprite strips in one texture: each file is a vertical strip of square frames, as Minecraft keeps an animated sprite,
// and the strips stand side by side, so column c, row f is frame f of strip c. After the flipbook loader of
// ImmersivePixelEffects (FlipbookTexture.Load, the author's code): a texture of its own, nearest, no mipmaps, clamped,
// so no frame reads its neighbour. The strips go into one texture with raw GL, as LoadTexture takes one bitmap; the
// raw id goes straight into a LoadedTexture, whose Dispose deletes it and whose finalizer reports a missed Dispose.
// ARGB ints on little-endian are BGRA bytes (WakeTexture). The pixels stay on the CPU for the images of the traced
// clouds (TraceClouds), 16 KB for the clouds' 64 x 64.
sealed class FlipbookTexture : IDisposable
{
    readonly ILogger log;
    readonly string name;
    LoadedTexture? texture;

    FlipbookTexture(ILogger log, string name, LoadedTexture texture, int strips, int frames, int frameSize, int[] pixels)
    {
        this.log = log;
        this.name = name;
        this.texture = texture;
        Strips = strips;
        Frames = frames;
        FrameSize = frameSize;
        Pixels = pixels;
    }

    public int TextureId => texture?.TextureId ?? 0;
    public int Strips { get; }
    public int Frames { get; }
    public int FrameSize { get; }
    // ARGB, Strips * FrameSize wide, row by row from the top: frame f of strip c starts at row f * FrameSize, column
    // c * FrameSize.
    public int[] Pixels { get; }

    // Null with a Warning when a file is missing or is not a strip of frameSize-wide frames of the same count as the
    // others; the effect then does not draw.
    public static FlipbookTexture? Load(ICoreClientAPI capi, ILogger log, string name, AssetLocation[] files, int frameSize)
    {
        int frames = 0;
        int width = files.Length * frameSize;
        int[]? pixels = null;
        for (int c = 0; c < files.Length; c++)
        {
            if (capi.Assets.TryGet(files[c]) is not { } asset)
            {
                log.Warning("{0}", $"{name}: {files[c]} not found, the effect does not draw");
                return null;
            }
            using BitmapRef bmp = asset.ToBitmap(capi);
            int count = bmp.Height / frameSize;
            if (bmp.Width != frameSize || bmp.Height % frameSize != 0 || count == 0 || c > 0 && count != frames)
            {
                log.Warning("{0}", $"{name}: {files[c]} is {bmp.Width}x{bmp.Height}, not a vertical strip of {frameSize}x{frameSize} frames" +
                                   (c > 0 ? $", {frames} like the first file" : "") + "; the effect does not draw");
                return null;
            }
            if (c == 0)
            {
                frames = count;
                pixels = new int[width * frames * frameSize];
            }
            int[] strip = bmp.Pixels;
            for (int y = 0; y < bmp.Height; y++)
            {
                Array.Copy(strip, y * frameSize, pixels!, y * width + c * frameSize, frameSize);
            }
        }
        if (pixels == null) return null;

        int height = frames * frameSize;
        int id = GL.GenTexture();
        var texture = new LoadedTexture(capi, id, width, height);
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Bgra,
            PixelType.UnsignedByte, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return new FlipbookTexture(log, name, texture, files.Length, frames, frameSize, pixels);
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
