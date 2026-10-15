using System;
using ImmersiveWakes.Commands;
using ImmersiveWakes.Config;
using ImmersiveWakes.Debug;
using ImmersiveWakes.Effects;
using ImmersiveWakes.Game;
using ImmersiveWakes.Render;
using ImmersiveWakes.Render.Trace;
using ImmersiveWakes.Simulation;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ImmersiveWakes;

// Everything that lives for one world. Dispose must stay safe after a Start that threw halfway.
sealed class WakesSession : IDisposable
{
    public ICoreClientAPI Capi { get; }
    public Mod Mod { get; }
    public WakesConfigFile? Config { get; private set; }
    public WakesCommands? Commands { get; private set; }
    public OverlayPrograms? Programs { get; private set; }
    // The wake: the core's atlas on the GPU and the quads of its live nodes, both filled by the driver's upload.
    public WakeTexture? Atlas { get; private set; }
    public WakeMesh? Wakes { get; private set; }
    public S1Tiles? Tiles { get; private set; }
    public OverlayRenderer? Overlay { get; private set; }
    public WakesStats? Stats { get; private set; }
    public GlStateProbe? Probe { get; private set; }
    public WakeDriver? Driver { get; private set; }
    public HullDebugRenderer? Hulls { get; private set; }
    // The splash clouds: the pool the driver steps, its sprites and the renderer; the pool and the renderer share one
    // guard.
    public SplashCloudPool? Clouds => Driver?.Clouds;
    public FlipbookTexture? CloudTexture { get; private set; }
    public SplashCloudRenderer? CloudRenderer { get; private set; }
    // Null without CoriaenderShaders.
    public CoriaenderBufferFix? CoriaenderFix { get; private set; }
    // Null without VS Path Tracing; the raster's renderers read its Silent, the upload its Skip.
    public VsptState? Vspt { get; private set; }
    // The foam in vspt's traced frame; null without vspt, otherwise on or off with the reason (TraceFoam.State).
    public TraceFoam? Trace { get; private set; }
#if DEBUG
    AutoRun? autoRun;
#endif

    public WakesSession(ICoreClientAPI capi, Mod mod)
    {
        Capi = capi;
        Mod = mod;
    }

    // Commands go last: a client command cannot be unregistered (UnregisterCommand is internal), so after a
    // throw further down they would stay live on a disposed session for the rest of the world.
    public void Start()
    {
#if DEBUG
        // A scenario starts at LevelFinalize, when .wakes is registered already.
        autoRun = AutoRun.TryCreate(this);
#endif
        // First: whatever is built below reads its settings from here. Never throws, a bad file means the defaults.
        Config = new WakesConfigFile(Capi, Mod.Logger);
        Config.Load();
        // Before anything reads block light or builds a program: the wake's mesh, the clouds, the S1 tiles.
        ModLight.Start(Capi, Mod.Logger);
        // Reflection only; null without vspt. Never throws.
        Vspt = VsptState.Detect(Capi);
        // Makes its GPU queries on first use; the renderers and the step below time themselves through it.
        Stats = new WakesStats(Capi, this);
        // The texture and the mesh here, the programs in the ReloadShader handler, the renderers last (DESIGN П8).
        Atlas = new WakeTexture(Capi, Mod.Logger, "Wake atlas", WakeAtlasCpu.GameTexels, mipmaps: true);
        Wakes = new WakeMesh(Capi, Config.Current.MaxNodes);
        Tiles = new S1Tiles(Capi, Mod.Logger);
        // Null with a Warning when the sprites cannot be read; the clouds then live in the pool and are not drawn.
        CloudTexture = FlipbookTexture.Load(Capi, Mod.Logger, "Splash cloud sprites", CloudSprites(), CloudFrameSize);
        // Builds nothing yet: the programs come with the shader reload that follows StartClientSide.
        Programs = new OverlayPrograms(Capi, Mod.Logger);
        Overlay = new OverlayRenderer(Capi, Mod.Logger, this);
        // In StartClientSide, before vspt's first bake of the entity atlas' materials: the Harmony patch and the region
        // the patch flags. Logs one line and never throws; with nothing to do it holds nothing.
        if (Vspt != null) Trace = new TraceFoam(Capi, Mod.Logger, this, Vspt);
        var cloudGuard = new RenderGuard(Mod.Logger, "splash clouds");
        // Uploads its quad here.
        CloudRenderer = new SplashCloudRenderer(cloudGuard, this);
        Overlay.Register();
        CloudRenderer.Register();
        CoriaenderFix = CoriaenderBufferFix.TryCreate(this);
        CoriaenderFix?.Register();
        Probe = new GlStateProbe(Capi, Mod.Logger, this);
        Probe.Register();
        // Builds the core from the config and the cloud pool, steps both and uploads the core's atlas strip and quads;
        // registered last of the renderers.
        Driver = new WakeDriver(Capi, Mod.Logger, this, Config, cloudGuard);
        Driver.Register();
        // Draws nothing and holds no mesh until .wakes debug hull.
        Hulls = new HullDebugRenderer(Capi, Mod.Logger, this);
        Hulls.Register();
        Commands = new WakesCommands(Capi, Mod.Info, this);
    }

    // splash_cloud_1..4, drawn by tools/gen-clouds.py after those of Wakes, from the largest cloud to the smallest, strips
    // of four frames of 16 x 16.
    const int CloudFrameSize = 16;

    static AssetLocation[] CloudSprites()
    {
        var files = new AssetLocation[SplashCloudPool.Sprites];
        for (int i = 0; i < files.Length; i++) files[i] = new AssetLocation("immersivewakes", $"textures/particle/splash_cloud_{i + 1}.png");
        return files;
    }

    // Chat commands need no teardown on world exit: the registry belongs to the ClientCoreAPI and dies with it.
    public void Dispose()
    {
#if DEBUG
        autoRun?.Dispose();
        autoRun = null;
#endif
        // The step and the renderers first: they read the programs, the meshes and the textures.
        Hulls?.Dispose();
        Hulls = null;
        Driver?.Dispose();
        Driver = null;
        // After the driver, whose upload steps it. A throw here must not keep the wake's own resources below from their
        // Dispose.
        try
        {
            Trace?.Dispose();
        }
        catch (Exception e)
        {
            Mod.Logger.Warning("{0}", $"Disposing the foam for VS Path Tracing failed: {e}");
        }
        Trace = null;
        Vspt = null;
        Probe?.Dispose();
        Probe = null;
        CoriaenderFix?.Dispose();
        CoriaenderFix = null;
        CloudRenderer?.Dispose();
        CloudRenderer = null;
        CloudTexture?.Dispose();
        CloudTexture = null;
        Overlay?.Dispose();
        Overlay = null;
        Programs?.Dispose();
        Programs = null;
        Tiles?.Dispose();
        Tiles = null;
        Wakes?.Dispose();
        Wakes = null;
        Atlas?.Dispose();
        Atlas = null;
        Stats?.Dispose();
        Stats = null;
        ModLight.Stop();
        Commands = null;
        Config = null;
    }
}
