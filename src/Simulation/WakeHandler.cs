using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ImmersiveWakes.Simulation;

// Wakes' WakeHandler without the world: sources hand it their steps between ticks, Tick moves the queue into the map,
// walks the chunks once and draws the live nodes into the atlas. The core references nothing from the engine, so the
// bench builds without VINTAGE_STORY.
//
// A touched block goes into the queue, not into the map, even when it holds a live node: a block that last tick's
// flood fill has queued must take the trail's impulses into that entry and keep its flood level, as src_port does
// (FINDINGS "Баги и причуды оригинала"). Water is asked when a block is queued, as WakeHandler.insert of src_port
// does; asked on draining, the answer would differ from the oracle when a block turns to water in between. The tint
// of a block without a node is asked there too, where Wakes built the node: a probe that throws then fails the
// source's call or the walk, never the drain, which would stop every wake on each tick after.
sealed class WakeHandler : IFloodQueue, ITrailSink
{
    // Fork 20: at the limit new nodes are refused, live nodes are still revived. The atlas bounds the limit.
    public const int DefaultMaxNodes = 2000;

    // The impulse arrays the queue keeps between ticks at the least. Under a small limit nodes die in every walk, the
    // next calls stamp every block they touch, and a pool of the limit's size would allocate the rest anew on each
    // tick. 256 arrays hold 0.3 MB at R16 and 1.2 MB at R32, and only after a tick that stamped as many.
    public const int MinKeptImpulses = 256;

    // The core stays bounded whatever a source hands it. A step longer than this on either axis is a jump the source
    // has missed (teleport, boarding; T05.03 cuts them first), and a source wider than this is a broken hull. Drawn,
    // either would put thousands of blocks into the queue in one call. Wakes has neither bound.
    const double MaxStepBlocks = 32;
    const float MaxWidthBlocks = 16;

    readonly IWaterProbe water;
    readonly NodePool pool;
    readonly WakeChunkMap map;
    readonly ImpulseBuffer queue;
    readonly List<Pixel> pixels = new(256);
    readonly List<WakeNode> drawn = [];
    SimulationParams parameters;

    // Blocks of the current call: the index of the queue entry, Dry off the water, or Turned for a block the drain
    // will refuse, queued but not stamped. Each block is asked once per call.
    readonly Dictionary<BlockKey, int> touched = [];
    const int Dry = -1, Turned = -2;

    // Neighbours of the call's blocks found off the water by CoreModes.EagerNeighbours, asked once per call too.
    readonly HashSet<BlockKey> dry = [];

    // slotTexels is 32 in the game at any resolution (fork 14); the bench sets it to the resolution to compare texels
    // with the oracle. atlasPixels hands over the atlas buffer of the core this one replaces (WakeAtlasCpu).
    public WakeHandler(SimulationParams parameters, IWaterProbe water, int maxNodes = DefaultMaxNodes,
        int slotTexels = WakeAtlasCpu.GameTexels, int[]? atlasPixels = null)
    {
        this.parameters = parameters;
        this.water = water;
        MaxNodes = Math.Clamp(maxNodes, 0, WakeAtlasCpu.Capacity);
        Atlas = new WakeAtlasCpu(parameters.Resolution, slotTexels, atlasPixels);
        pool = new NodePool(parameters.Resolution, Atlas);
        map = new WakeChunkMap(pool);
        queue = new ImpulseBuffer(parameters.Resolution, Math.Max(MaxNodes, MinKeptImpulses));
    }

    // Read once at the start of each Tick, like Modes, and by the sources' calls between ticks. A new set takes effect
    // at the next call and keeps every node: Wakes reads the wave factors and flood fill from its config on each tick.
    // The node arrays and the queue are sized by the resolution, so a new one needs a new core.
    public SimulationParams Params
    {
        get => parameters;
        set
        {
            if (value.Resolution != parameters.Resolution)
            {
                throw new ArgumentException($"the core runs at R{parameters.Resolution}, R{value.Resolution} needs a new one", nameof(value));
            }
            parameters = value;
        }
    }

    public int MaxNodes { get; }
    public int NodeCount => map.NodeCount;
    public int ChunkCount => map.ChunkCount;

    // A slot per live node, drawn at the end of every Tick.
    public WakeAtlasCpu Atlas { get; }

    // debugColors of Wakes: troughs red, crests green. Takes effect at the next Tick or Redraw.
    public bool DebugColors { get; set; }

    // The colours of the texels, from the config in the game. Takes effect at the next Tick or Redraw.
    public WakePalette Palette { get; set; } = WakePalette.Wakes;

    // Stopwatch ticks the last Redraw took, inside Tick or on its own (.wakes stats shows the colouring apart from the
    // step).
    public long LastPaintTicks { get; private set; }

    // The modes of the core, read once at the start of each Tick: one walk never mixes two rules. A switch keeps
    // every node, and the next tick applies the new rule to each; with the amplitude fade turned off, the nodes past
    // MaxAge end on that tick unless a touch or flood fill resets their age. The setting lives as long as this
    // handler: whoever builds a new core (a new resolution or node limit, WakeDriver) hands it over.
    // A new handler starts with None, and every check of the bench against src_port stands on that.
    public CoreModes Modes { get; set; }

    // New nodes the limit turned away, over the handler's life (.wakes stats).
    public long Refused { get; private set; }

    // What waits for the next tick, for the bench and the stats; only the handler writes it.
    public ImpulseBuffer Queue => queue;

    // A source's step between ticks (thickNodeTrail). velocity is the source's horizontal speed in blocks per 1/20 s,
    // width its full width in blocks (getBbWidth, the hull for a boat). The impulse is (int)(strength * velocity) as in
    // Wakes, and a step that rounds it below 1 leaves no trail (src_r7a). False when nothing was drawn: an impulse under 1,
    // a step the core refuses, or both ends in one pixel.
    public bool AddTrail(double fromX, double fromZ, double toX, double toZ, int y, float strength, double velocity, float width)
    {
        int value = (int)(strength * velocity);
        if (value < 1 || !IsStep(fromX, fromZ, toX, toZ)) return false;
        if (!TrailRasterizer.Thick(fromX, fromZ, toX, toZ, Width(width), Params.Resolution, pixels)) return false;
        QueuePixels(y, value);
        return true;
    }

    // What AddTrail answers at resolution res, without drawing: the recorded steps of the simulation thread give it to
    // the source at once (StepInput). Thick refuses a step only when both ends fall into one pixel.
    public static bool Draws(double fromX, double fromZ, double toX, double toZ, float strength, double velocity, int res) =>
        (int)(strength * velocity) >= 1 && IsStep(fromX, fromZ, toX, toZ)
        && ((int)Math.Floor(fromX * res) != (int)Math.Floor(toX * res) || (int)Math.Floor(fromZ * res) != (int)Math.Floor(toZ * res));

    // An entry into the water (splashNodes): velocity is the vertical speed in blocks per 1/20 s, of either sign, since
    // Wakes takes its absolute value; width is the source's full width in blocks. False when nothing was drawn.
    public bool AddSplash(double x, double z, int y, float strength, double velocity, float width)
    {
        if (!SplashDraws(x, z, strength, velocity)) return false;
        TrailRasterizer.Disc(x, z, Width(width), Params.Resolution, pixels);
        QueuePixels(y, (int)(strength * Math.Abs(velocity)));
        return true;
    }

    // What AddSplash answers, without drawing: the disc has a pixel at any width.
    public static bool SplashDraws(double x, double z, float strength, double velocity) =>
        (int)(strength * Math.Abs(velocity)) >= 1 && double.IsFinite(x) && double.IsFinite(z);

    // For ".wakes debug", the trail's width in pixels and the disc's radius AddTrail and AddSplash draw for a source of
    // this width, past the bound on width.
    public static int TrailPixels(float width, int res) => TrailRasterizer.Lines(Width(width), res);
    public static int SplashRadius(float width, int res) => TrailRasterizer.DiscRadius(Width(width), res);

    // A paddle stroke (rowingNodes through nodeTrail): a line 1 px thick, the impulse (int)(strength * velocity) with
    // the boat's horizontal speed in blocks per 1/20 s.
    public void AddLine(double fromX, double fromZ, double toX, double toZ, int y, float strength, double velocity)
    {
        int value = (int)(strength * velocity);
        if (value < 1 || !IsStep(fromX, fromZ, toX, toZ)) return;
        TrailRasterizer.Line(fromX, fromZ, toX, toZ, Params.Resolution, pixels);
        QueuePixels(y, value);
    }

    // The queue first, then the chunks (WakeHandler.wakeLogic). An entry for a live node revives it and replaces its
    // impulses, which its last step has zeroed, as revive did with the array; an entry for a free block becomes a new
    // node unless the limit is reached. Flood fill of this walk queues for the next tick. Neighbours queued ahead
    // (CoreModes.EagerNeighbours) take a second pass, so at the limit they only fill what the touches of this tick
    // left free and count as no refusal: no source asked for them. An ahead entry for a node still ahead renews its
    // age.
    // A probe that throws in the walk ends the tick early, keeping what flood fill has queued so far, and every live
    // node is still drawn, so a node made in this tick never shows the pixels its slot kept from a dead one.
    public void Tick()
    {
        CoreModes modes = Modes;
        SimulationParams p = Params;
        try
        {
            try
            {
                for (int i = 0; i < queue.Count; i++)
                {
                    ref readonly ImpulseBuffer.Entry e = ref queue[i];
                    if (e.Ahead) continue;
                    WakeNode? node = map.GetNode(e.X, e.Y, e.Z);
                    if (node != null)
                    {
                        node.Revive();
                        node.Promote(e.FloodLevel);
                    }
                    else if (map.NodeCount >= MaxNodes)
                    {
                        Refused++;
                        continue;
                    }
                    else
                    {
                        node = pool.Rent(e.X, e.Y, e.Z, e.FloodLevel, e.Tint);
                        map.Insert(node);
                    }
                    e.Impulses?.CopyTo(node.Sim.Impulses, 0);
                }
                for (int i = 0; i < queue.Count && queue.AheadCount > 0; i++)
                {
                    ref readonly ImpulseBuffer.Entry e = ref queue[i];
                    if (!e.Ahead) continue;
                    if (map.GetNode(e.X, e.Y, e.Z) is { } held) held.Revive();
                    else if (map.NodeCount < MaxNodes) map.Insert(pool.Rent(e.X, e.Y, e.Z, 0, e.Tint, ahead: true));
                }
            }
            finally
            {
                queue.Clear();
            }
            map.Tick(p, water, this, modes);
        }
        finally
        {
            Draw(modes);
        }
    }

    // Every live node into its slot (WakeChunk.drawWakes). Wakes drew a chunk right after its own walk; a node's field
    // and the opacity baked on its tick do not change while later chunks tick, so drawing after the whole walk gives
    // the same texels, and the stretched ones see every neighbour stepped. Outside Tick it repaints the live nodes, so
    // a change of DebugColors, Palette or CoreModes.SoftEdge shows on pause too (recolorWakes).
    public void Redraw() => Draw(Modes);

    void Draw(CoreModes modes)
    {
        long start = Stopwatch.GetTimestamp();
        WakePalette palette = Palette;
        bool soft = (modes & CoreModes.SoftEdge) != 0;
        map.CollectNodes(drawn);
        foreach (WakeNode node in drawn) Atlas.Draw(node, palette, DebugColors, soft);
        drawn.Clear();
        LastPaintTicks = Stopwatch.GetTimestamp() - start;
    }

    // Live nodes in traversal order: chunks by (cz, cx, y), inside a chunk by z, then x.
    public void CollectNodes(List<WakeNode> output) => map.CollectNodes(output);

    public WakeNode? GetNode(int x, int y, int z) => map.GetNode(x, y, z);

    // Flood fill queues only empty sides, so every entry may become a node.
    void IFloodQueue.QueueFlood(int x, int y, int z, int floodLevel)
    {
        if (water.IsSurfaceWater(x, y, z)) queue.Enqueue(x, y, z, floodLevel, water.WaterTint(x, y, z));
    }

    // pixelsToNodes: pixel p lies in block p >> power, cell p & (res - 1), which is floorMod for a power of two. A new
    // entry gets floodFillDistance; an entry already queued keeps its own level, unless the neighbours at once queued
    // it ahead: that entry becomes the touch's whatever the modes are now, or a switch of the mode between two calls
    // would leave the touch's impulses on a node made ahead.
    // At the node limit a block without a node gets no tint and no impulses: the drain refuses its entry, since the map
    // does not change between a source's call and the drain, and the drain only adds nodes. The entry is still queued
    // for the refusal count, and the call keeps the block as Turned, not Dry, so the neighbours at once still renew the
    // nodes made ahead around it. Flood fill queues in the middle of the walk, where nodes still die, and asks every
    // time (T05.11).
    // The water probe is the engine's in the game and may throw; whatever a call leaves behind would put its blocks
    // and stale entry indices into the next call.
    void QueuePixels(int y, int value)
    {
        int power = Params.Power, mask = Params.Resolution - 1;
        float stamp = value * (Params.Resolution / 16f);
        bool eager = (Modes & CoreModes.EagerNeighbours) != 0;
        try
        {
            foreach (Pixel p in pixels)
            {
                var key = new BlockKey(p.X >> power, y, p.Z >> power);
                if (!touched.TryGetValue(key, out int entry))
                {
                    entry = Dry;
                    if (water.IsSurfaceWater(key.X, y, key.Z))
                    {
                        bool free = map.GetNode(key.X, y, key.Z) == null, refused = free && map.NodeCount >= MaxNodes;
                        int tint = free && !refused ? water.WaterTint(key.X, y, key.Z) : 0;
                        entry = queue.Enqueue(key.X, y, key.Z, Params.FloodFillDistance, tint);
                        queue.Touch(entry, Params.FloodFillDistance);
                        if (refused) entry = Turned;
                    }
                    touched.Add(key, entry);
                }
                if (entry >= 0) queue.Stamp(entry, p.Z & mask, p.X & mask, stamp);
            }
            if (eager) QueueAhead(y);
        }
        finally
        {
            touched.Clear();
            dry.Clear();
            pixels.Clear();
        }
    }

    // CoreModes.EagerNeighbours: the eight neighbours of every water block of the call that neither the call touches
    // nor the queue holds. A free water block is queued unless the limit is reached, when the drain would make none;
    // a node still ahead is queued too, to renew its age: a block the source keeps touching never floods, and its
    // neighbours would die at MaxAge and come back empty at the next touch, a ring that blinks every 1.5 s. A node
    // flood fill has made is left to its own age, as without the edit.
    // At the default flood fill delay a wave, under a cell a tick at any resolution (the front 0.66 at the defaults,
    // FINDINGS "Шаг волнового уравнения"), reaches two blocks away after flood fill, so the eight are enough; a long
    // delay lets it reach there first. Diagonals count, as a splash near a corner reaches the diagonal block before
    // its sides flood.
    void QueueAhead(int y)
    {
        foreach (var (key, entry) in touched)
        {
            if (entry == Dry) continue;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = key.X + dx, z = key.Z + dz;
                    var side = new BlockKey(x, y, z);
                    if (touched.ContainsKey(side) || dry.Contains(side) || queue.IsQueued(x, y, z)) continue;
                    if (map.GetNode(x, y, z) is { } live)
                    {
                        if (live.Ahead) queue.EnqueueAhead(x, y, z, 0);
                    }
                    else if (map.NodeCount >= MaxNodes) continue;
                    else if (water.IsSurfaceWater(x, y, z)) queue.EnqueueAhead(x, y, z, water.WaterTint(x, y, z));
                    else dry.Add(side);
                }
            }
        }
    }

    // A position that is not finite saturates on the cast to pixel 0 or int.MinValue/MaxValue, and a line between those
    // never ends.
    static bool IsStep(double fromX, double fromZ, double toX, double toZ) =>
        double.IsFinite(fromX) && double.IsFinite(fromZ) && double.IsFinite(toX) && double.IsFinite(toZ)
        && Math.Abs(toX - fromX) <= MaxStepBlocks && Math.Abs(toZ - fromZ) <= MaxStepBlocks;

    // An infinite width would draw int.MaxValue lines. NaN casts to 0 like any width under a pixel: the narrow trail.
    static float Width(float width) => float.IsNaN(width) ? 0f : Math.Clamp(width, 0f, MaxWidthBlocks);
}
