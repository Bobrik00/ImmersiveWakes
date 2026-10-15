using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
#if DEBUG
using System.Text;
#endif
using ImmersiveWakes.Sources;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ImmersiveWakes.Render.Trace;

// The hull mask of the traced frame (T11.05): the foam's texels and the splash clouds inside a boat's water mask are
// not traced, as Wakes' OcclusionZone clears the texels inside a boat (FINDINGS "Окклюзия"). In the raster the
// hidewatersurface plates hide them by depth; vspt hides only its water with them, and the mod's quads are ordinary
// entities to the tracer, so without this the foam and the clouds show inside the hull above its floor.
//
// Once a Before frame under vspt's Skip, by the foam's bake when the frame bakes and by TraceFoam.StepClouds on every
// such frame: the boats the tracker knows whose type has a footprint (HullFootprint), and the ghost boats of .wakes
// ghost, as Max hulls at most, the nearest to the camera within the view distance. A hull stands where the renderer
// draws its boat (EntityShapeRenderer.loadModelMatrix: the boat the player sits in goes from the camera by the seat's
// offset) half a step on, by its motion over the last SampleEvery at least: the bake and the walk run once a step and
// stand for the step, and the middle of the step keeps the mask within 0.17 of the hull at 6.9 b/s, inside the plates'
// margin of 0.12-0.25 at the bow and the stern. A ghost stands where its last step left it, which a frame that bakes
// reads before the frame's steps and a frame that does not after them: it has no hull to show.
//
// The bake reads the hulls from uniforms (hullPose, hullFrame, hullCell) and the footprints from one R8 texture of
// MaskSide square, each shape's mask placed once on a shelf; the boat types of one shape in other woods share it. A
// node's slot carries the bits of the hulls whose box covers its block and whose height band holds its surface (Bits);
// a slot without bits goes through the bake as before. The clouds ask Deck for the highest plate over a cloud's centre,
// raised and lowered by the boat's sway and forward pitch (EntityBoat puts the pitch into the renderer's zangle): at
// 6.9 b/s it lifts the bow plate by 0.39. Errors here take the mask off for the world, and the foam and the clouds go
// on without it.
sealed class TraceHulls : IDisposable
{
    public const int Max = 16;
    const int MaskSide = 1024;
    const double Lead = 0.025, SampleEvery = 0.05, SampleGap = 0.5, MaxLeadSpeed = 40;
    // A node's surface counts under a hull from a block below its Pos to a quarter above its highest plate.
    const double BandBelow = 1, BandAbove = 0.25;
    const double SurfaceHeight = 7 / 8.0;
    // Refreshes a type whose shape has not loaded is looked at again, about 10 s at 60 FPS, before it goes without a mask.
    const int MaxRetries = 600;

    struct Hull
    {
        public double X, Z, PosY, Low, High, MinX, MaxX, MinZ, MaxZ, DistanceSq;
        public float Cos, Sin;
        // The plates' rise per block along the boat's axes: sin of the renderer's zangle and of its xangle.
        public float RiseX, RiseZ;
        public HullFootprint Print;
        public Entity? Entity;
    }

    sealed class Track
    {
        public double X, Z, Yaw, Time, Vx, Vz, Vyaw;
        public long Seen;
    }

    sealed class ByDistance : IComparer<Hull>
    {
        public int Compare(Hull a, Hull b) => a.DistanceSq.CompareTo(b.DistanceSq);
    }

    static readonly ByDistance Nearest = new();

    readonly ICoreClientAPI capi;
    readonly ILogger log;
    readonly WakesSession session;
    readonly RenderGuard guard;
    // By entity code, null for a type without a mask; and by shape, which the codes of one boat in other woods share.
    readonly Dictionary<AssetLocation, HullFootprint?> types = [];
    readonly Dictionary<string, HullFootprint?> shapes = [];
    readonly Dictionary<AssetLocation, int> retries = [];
    readonly Dictionary<long, Track> tracks = [];
    readonly List<long> lost = [];
    readonly List<Hull> candidates = [];
    readonly Hull[] hulls = new Hull[Max];
    readonly float[] pose = new float[Max * 4], frame = new float[Max * 4], cell = new float[Max * 4];
    int maskTexture, shelfX, shelfY, shelfHeight, count, boats, ghosts;
    long refreshed = -1;
    // Since the world was entered: CPU ticks and bytes of the refreshes; the window of .wakes stats.
    long ticks, maxTicks, bytes, refreshesAt, ticksAt, bytesAt, maxWindow;

    public TraceHulls(ICoreClientAPI capi, ILogger log, WakesSession session)
    {
        this.capi = capi;
        this.log = log;
        this.session = session;
        guard = new RenderGuard(log, "traced hull mask");
        maskTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, maskTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, MaskSide, MaskSide, 0, PixelFormat.Red, PixelType.UnsignedByte, new byte[MaskSide * MaskSide]);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    public bool Off => guard.Tripped;
    public int Count => count;
    public int MaskTexture => maskTexture;
    // The shader's origin: the camera's block at the refresh, so the node offsets in the bake are whole numbers.
    public int OriginX { get; private set; }
    public int OriginZ { get; private set; }
    public float[] Pose => pose;
    public float[] Frame => frame;
    public float[] Cell => cell;

    // Since the world was entered: refreshes and the hulls they found, and clouds cut at a deck.
    public long Refreshes { get; private set; }
    public long HullsFound { get; private set; }
    public long Decked { get; set; }

    // Once a driver frame; the first caller of the frame refreshes, the second reads the same hulls.
    public void Refresh(long frameNo)
    {
        if (frameNo == refreshed) return;
        refreshed = frameNo;
        count = boats = ghosts = 0;
        if (guard.Tripped) return;
        long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        try
        {
            Collect(frameNo);
        }
        catch (Exception e)
        {
            count = 0;
            guard.Trip("refresh", e);
        }
        finally
        {
            candidates.Clear();
            long spent = Stopwatch.GetTimestamp() - start;
            ticks += spent;
            maxTicks = Math.Max(maxTicks, spent);
            maxWindow = Math.Max(maxWindow, spent);
            bytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
    }

    void Collect(long frameNo)
    {
        if (session.Driver?.Sources is not { } tracker || capi.World.Player?.Entity is not { } me) return;
        Vec3d camera = me.CameraPos;
        OriginX = (int)Math.Floor(camera.X);
        OriginZ = (int)Math.Floor(camera.Z);
        int dimension = me.Pos.Dimension;
        double reachSq = capi.Render.DefaultFrustumCuller.ViewDistanceSq;
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        bool paused = capi.IsGamePaused;
        foreach (SourceState s in tracker.States)
        {
            if (s.Kind != SourceKind.Boat || s.Broken) continue;
            Entity e = s.Entity;
            if (!e.Alive || e.Pos.Dimension != dimension) continue;
            (double x, double y, double z) = Drawn(e, me);
            double dx = x - camera.X, dz = z - camera.Z;
            if (dx * dx + dz * dz > reachSq || Footprint(e) is not { } print) continue;
            if (print.Draft == null && s.LastSurface != int.MinValue)
            {
                double surface = s.LastSurface + SurfaceHeight;
                if (s.LastFeet < surface && s.LastTop > surface) print.Draft = surface - (s.LastFeet - e.CollisionBox.Y1);
            }
            (x, z, double yaw) = Ahead(e.EntityId, x, z, e.Pos.Yaw, now, paused, frameNo);
            var esr = e.Properties.Client?.Renderer as EntityShapeRenderer;
            Add(print, x, z, y, yaw + print.Turn + (esr?.yangle ?? 0), camera, e, esr?.zangle ?? 0, esr?.xangle ?? 0);
            boats++;
        }
        IReadOnlyList<GhostSource> ghostList = tracker.Ghosts;
        for (int i = 0; i < ghostList.Count; i++)
        {
            GhostSource g = ghostList[i];
            if (g.Boat is not { } boat || !types.TryGetValue(boat.Code, out HullFootprint? print) || print?.Draft is not { } draft) continue;
            (double x, double z) = g.Position;
            Add(print, x, z, g.Y + SurfaceHeight - draft, g.Heading + print.Turn, camera, null, 0, 0);
            ghosts++;
        }
        foreach ((long id, Track t) in tracks)
        {
            if (t.Seen != frameNo) lost.Add(id);
        }
        foreach (long id in lost) tracks.Remove(id);
        lost.Clear();
        if (candidates.Count > Max) candidates.Sort(Nearest);
        count = Math.Min(Max, candidates.Count);
        Array.Clear(pose);
        Array.Clear(frame);
        Array.Clear(cell);
        for (int i = 0; i < count; i++)
        {
            Hull h = hulls[i] = candidates[i];
            pose[i * 4] = (float)(h.X - OriginX);
            pose[i * 4 + 1] = (float)(h.Z - OriginZ);
            pose[i * 4 + 2] = h.Cos;
            pose[i * 4 + 3] = h.Sin;
            frame[i * 4] = h.Print.MinX;
            frame[i * 4 + 1] = h.Print.MinZ;
            frame[i * 4 + 2] = h.Print.MaxX;
            frame[i * 4 + 3] = h.Print.MaxZ;
            cell[i * 4] = h.Print.CellX;
            cell[i * 4 + 1] = h.Print.CellY;
            cell[i * 4 + 2] = h.Print.Width;
            cell[i * 4 + 3] = h.Print.Height;
        }
        // The entities of hulls past count would stay reachable after they despawn.
        Array.Clear(hulls, count, Max - count);
        Refreshes++;
        HullsFound += count;
    }

    // Where the renderer puts the entity's origin, internal y: at Pos, or for the boat the local player sits in at the
    // camera less the seat's offset from the mount (EntityShapeRenderer.loadModelMatrix).
    static (double X, double Y, double Z) Drawn(Entity e, EntityPlayer me)
    {
        if (me.MountedOn is { } seat && seat.Entity == e && seat.MountSupplier?.Position is { } mount && seat.SeatPosition is { } at)
        {
            Vec3d camera = me.CameraPos;
            return (camera.X - at.X + mount.X, camera.Y - at.Y + mount.Y, camera.Z - at.Z + mount.Z);
        }
        return (e.Pos.X, e.Pos.InternalY, e.Pos.Z);
    }

    // turn: the angle of RotateY from the boat's axes into the world's; pitch and roll the renderer's zangle and
    // xangle, which tilt the plates about the boat's x and z.
    void Add(HullFootprint print, double x, double z, double posY, double turn, Vec3d camera, Entity? entity, float pitch, float roll)
    {
        float c = (float)Math.Cos(turn), s = (float)Math.Sin(turn);
        var h = new Hull
        {
            X = x,
            Z = z,
            PosY = posY,
            Low = posY - BandBelow,
            High = posY + print.PlateTop + BandAbove,
            Cos = c,
            Sin = s,
            RiseX = (float)Math.Sin(pitch),
            RiseZ = -(float)Math.Sin(roll),
            Print = print,
            Entity = entity,
            MinX = double.MaxValue,
            MaxX = double.MinValue,
            MinZ = double.MaxValue,
            MaxZ = double.MinValue,
        };
        for (int k = 0; k < 4; k++)
        {
            double lx = (k & 1) == 0 ? print.MinX : print.MaxX, lz = (k & 2) == 0 ? print.MinZ : print.MaxZ;
            double wx = x + lx * c + lz * s, wz = z - lx * s + lz * c;
            h.MinX = Math.Min(h.MinX, wx);
            h.MaxX = Math.Max(h.MaxX, wx);
            h.MinZ = Math.Min(h.MinZ, wz);
            h.MaxZ = Math.Max(h.MaxZ, wz);
        }
        double dx = x - camera.X, dz = z - camera.Z;
        h.DistanceSq = dx * dx + dz * dz;
        candidates.Add(h);
    }

    // Where the boat will stand half a step on, by its move since a sample at least SampleEvery old; on pause and on
    // the first sight where it stands. A move faster than MaxLeadSpeed is a jump and leads nowhere.
    (double X, double Z, double Yaw) Ahead(long id, double x, double z, double yaw, double now, bool paused, long frameNo)
    {
        if (!tracks.TryGetValue(id, out Track? t))
        {
            t = new Track { X = x, Z = z, Yaw = yaw, Time = now };
            tracks[id] = t;
        }
        t.Seen = frameNo;
        if (paused) return (x, z, yaw);
        double dt = now - t.Time;
        if (dt > SampleGap)
        {
            (t.Vx, t.Vz, t.Vyaw) = (0, 0, 0);
            (t.X, t.Z, t.Yaw, t.Time) = (x, z, yaw, now);
        }
        else if (dt >= SampleEvery)
        {
            t.Vx = (x - t.X) / dt;
            t.Vz = (z - t.Z) / dt;
            t.Vyaw = Math.IEEERemainder(yaw - t.Yaw, 2 * Math.PI) / dt;
            if (t.Vx * t.Vx + t.Vz * t.Vz > MaxLeadSpeed * MaxLeadSpeed) (t.Vx, t.Vz, t.Vyaw) = (0, 0, 0);
            (t.X, t.Z, t.Yaw, t.Time) = (x, z, yaw, now);
        }
        return (x + t.Vx * Lead, z + t.Vz * Lead, yaw + t.Vyaw * Lead);
    }

    // The footprint of the entity's type: built at the first sight of a shape with a mask and placed in the mask
    // texture, or the one its shape already has; null for a type without one, and while its shape has not loaded.
    HullFootprint? Footprint(Entity e)
    {
        if (types.TryGetValue(e.Code, out HullFootprint? known)) return known;
        if (HullFootprint.MaskElement(e) is not { } element)
        {
            types[e.Code] = null;
            return null;
        }
        string? shape = HullFootprint.ShapeKey(e, element);
        if (shape != null && shapes.TryGetValue(shape, out HullFootprint? same))
        {
            types[e.Code] = same;
            return same;
        }
        HullFootprint? print = null;
        string why;
        try
        {
            print = HullFootprint.Build(capi, e, element, out why, out bool retry);
            if (print == null && retry)
            {
                int tried = retries.GetValueOrDefault(e.Code) + 1;
                retries[e.Code] = tried;
                if (tried < MaxRetries) return null;
                why += $" after {MaxRetries} refreshes";
            }
        }
        catch (Exception ex)
        {
            why = "the tessellation threw";
            log.Warning("{0}", $"VS Path Tracing: tessellating the water mask of {e.Code} for the traced hull mask threw: {ex}");
        }
        retries.Remove(e.Code);
        if (print != null && !Place(print))
        {
            why = $"the mask texture has no room for {print.Width} x {print.Height} texels";
            print = null;
        }
        types[e.Code] = print;
        if (shape != null) shapes[shape] = print;
        if (print == null) log.Notification("{0}", $"VS Path Tracing: {e.Code} gets no hull mask in the traced frame ({why}); the foam and the clouds show inside its hull");
        return print;
    }

    // A shelf packer: left to right, a new shelf above the tallest mask of the last one.
    bool Place(HullFootprint print)
    {
        if (shelfX + print.Width > MaskSide)
        {
            shelfY += shelfHeight;
            shelfX = 0;
            shelfHeight = 0;
        }
        if (print.Width > MaskSide || shelfY + print.Height > MaskSide) return false;
        GL.BindTexture(TextureTarget.Texture2D, maskTexture);
        // Rows of R8 are not 4-byte aligned. The engine keeps the default of 4, a shader mod may leave 1 (Sheyder's
        // SpecularAtlasBuilder); whatever was there goes back. Once a shape, so the wait of glGet costs nothing.
        int alignment = GL.GetInteger(GetPName.UnpackAlignment);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, shelfX, shelfY, print.Width, print.Height, PixelFormat.Red, PixelType.UnsignedByte, print.Mask);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, alignment);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        print.CellX = shelfX;
        print.CellY = shelfY;
        shelfX += print.Width;
        shelfHeight = Math.Max(shelfHeight, print.Height);
        return true;
    }

    // The hulls of this refresh over a node's block whose band holds its surface, a bit each.
    public int Bits(int x, int y, int z)
    {
        int bits = 0;
        double surface = y + SurfaceHeight;
        for (int i = 0; i < count; i++)
        {
            ref Hull h = ref hulls[i];
            if (surface < h.Low || surface > h.High || x + 1 <= h.MinX || x >= h.MaxX || z + 1 <= h.MinZ || z >= h.MaxZ) continue;
            bits |= 1 << i;
        }
        return bits;
    }

    // The highest plate over (x, z) of a hull whose band holds the still surface at surfaceY, tilted with its boat;
    // false when no plate is. The tilt moves the plates sideways by 0.06 at most, which the mask leaves out.
    public bool Deck(double x, double surfaceY, double z, out double top)
    {
        top = double.MinValue;
        for (int i = 0; i < count; i++)
        {
            ref Hull h = ref hulls[i];
            if (surfaceY < h.Low || surfaceY > h.High || x < h.MinX || x > h.MaxX || z < h.MinZ || z > h.MaxZ) continue;
            double dx = x - h.X, dz = z - h.Z;
            double lx = h.Cos * dx - h.Sin * dz, lz = h.Sin * dx + h.Cos * dz;
            int k = h.Print.TexelAt(lx, lz);
            if (k >= 0) top = Math.Max(top, h.PosY + h.Print.Top[k] + lx * h.RiseX + lz * h.RiseZ);
        }
        return top > double.MinValue;
    }

    // Whether the world point (x, z) lies under a plate of hull i: the bake's test in double, for the mask check of
    // .wakes vspt.
    public bool Covers(int i, double x, double z)
    {
        ref Hull h = ref hulls[i];
        double dx = x - h.X, dz = z - h.Z;
        return h.Print.TexelAt(h.Cos * dx - h.Sin * dz, h.Sin * dx + h.Cos * dz) >= 0;
    }

    static string Ms(long t) => (t * 1000.0 / Stopwatch.Frequency).ToString("0.000", CultureInfo.InvariantCulture);

    // The hull mask's part of the trace line of .wakes stats, over the window ResetWindow opened.
    public string StatsLine()
    {
        if (guard.Tripped) return "hull mask off after an error";
        long n = Refreshes - refreshesAt;
        return string.Create(CultureInfo.InvariantCulture,
            $"hull mask {count} of {boats + ghosts} hulls ({boats} boats, {ghosts} ghosts), max {Max}; refreshes {n}, cpu ms per refresh mean/max " +
            $"{Ms(n > 0 ? (ticks - ticksAt) / n : 0)}/{Ms(maxWindow)}, bytes per refresh {(n > 0 ? (bytes - bytesAt) / n : 0)}");
    }

    public void ResetWindow()
    {
        refreshesAt = Refreshes;
        ticksAt = ticks;
        bytesAt = bytes;
        maxWindow = 0;
    }

#if DEBUG
    // The hull mask's part of '.wakes vspt': the types met, the hulls of the last refresh and the check of the turn
    // against EntityShapeRenderer.ModelMat of the boats drawn last frame, without the lead.
    public string Status()
    {
        var sb = new StringBuilder(StatsLine());
        sb.Append(CultureInfo.InvariantCulture,
            $"; since the world was entered refreshes {Refreshes}, hulls per refresh {(Refreshes > 0 ? HullsFound / (double)Refreshes : 0):0.0}, " +
            $"cpu ms mean/max {Ms(Refreshes > 0 ? ticks / Refreshes : 0)}/{Ms(maxTicks)}, clouds cut at a deck {Decked}; types:");
        foreach ((AssetLocation code, HullFootprint? print) in types)
        {
            if (print == null)
            {
                sb.Append(' ').Append(code).Append(" none;");
                continue;
            }
            int covered = 0;
            foreach (byte b in print.Mask) if (b != 0) covered++;
            sb.Append(CultureInfo.InvariantCulture,
                $" {code} {print.Width}x{print.Height} at {print.CellX},{print.CellY}, x {print.MinX:0.###}..{print.MaxX:0.###} z {print.MinZ:0.###}..{print.MaxZ:0.###}, " +
                $"{covered} texels under plates, plates up to {print.PlateTop:0.###}, draft {(print.Draft is { } d ? d.ToString("0.###", CultureInfo.InvariantCulture) : "-")};");
        }
        double worst = -1;
        int checkedBoats = 0;
        if (capi.World.Player?.Entity is { } me)
        {
            Vec3d camera = me.CameraPos;
            for (int i = 0; i < count; i++)
            {
                ref Hull h = ref hulls[i];
                if (h.Entity is not { IsRendered: true } e || e.Properties.Client?.Renderer is not EntityShapeRenderer esr) continue;
                float[] m = esr.ModelMat;
                Vec3f p = h.Print.Probe;
                double rx = m[0] * p.X + m[4] * p.Y + m[8] * p.Z + m[12] + camera.X;
                double rz = m[2] * p.X + m[6] * p.Y + m[10] * p.Z + m[14] + camera.Z;
                (double x, _, double z) = Drawn(e, me);
                double turn = e.Pos.Yaw + h.Print.Turn + esr.yangle, c = Math.Cos(turn), s = Math.Sin(turn);
                Vec3f l = h.Print.ProbeLocal;
                double mx = x + l.X * c + l.Z * s, mz = z - l.X * s + l.Z * c;
                worst = Math.Max(worst, Math.Sqrt((rx - mx) * (rx - mx) + (rz - mz) * (rz - mz)));
                checkedBoats++;
            }
        }
        sb.Append(CultureInfo.InvariantCulture, $" turn check against the renderer: {checkedBoats} boats drawn, ");
        sb.Append(worst >= 0 ? worst.ToString("0.0000", CultureInfo.InvariantCulture) + " blocks at most" : "none");
        return sb.ToString();
    }
#endif

    public void Dispose()
    {
        if (maskTexture != 0) GL.DeleteTexture(maskTexture);
        maskTexture = 0;
        count = 0;
        Array.Clear(hulls);
    }
}
