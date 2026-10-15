namespace ImmersiveWakes.Simulation;

// A water block with a wake: Wakes' WakeNode with its life cycle and the port's fixes of Revive and Unlink
// (FINDINGS "Жизнь узла", "Баги и причуды оригинала"). The pool hands the same object out again, so everything but
// Sim is reset in Init.
sealed class WakeNode
{
    public readonly SimulationNode Sim;

    // Neighbours one block away at the same y, linked both ways by WakeChunk.Insert. North is -z, east is +x.
    public WakeNode? North, East, South, West;

    public WakeNode(int res)
    {
        Sim = new SimulationNode(res);
    }

    public int X { get; private set; }
    public int Y { get; private set; }
    public int Z { get; private set; }
    public int Age { get; private set; }
    public int FloodLevel { get; private set; }
    public bool IsDead { get; private set; }

    // Made ahead of flood fill as a touched block's neighbour (CoreModes.EagerNeighbours) and not reached since by a
    // touch or a neighbour's flood fill.
    public bool Ahead { get; private set; }

    // Set and cleared by NodePool, which refuses to take a node twice.
    public bool Pooled { get; set; }

    // The node's atlas slot, -1 without one. NodePool claims it on Rent and frees it on Return, when the node dies.
    public int Slot { get; set; } = -1;

    // ARGB of the water the bucket colours blend with, taken when the node is made and, without
    // SimulationParams.CacheWaterTint, on every tick.
    public int WaterTint { get; private set; }

    // 1 - t^2 at t = age/30, baked at this node's tick from its age then. The texels take it even when a neighbour
    // resets the age later in the same tick (FINDINGS "Цвет (WakeColor)"). 1 once the node has ticked with the
    // amplitude fade on; a node switched to it keeps the opacity of its age until its tick, Redraw included.
    public float Opacity { get; private set; }

    // Where Wakes builds a new node with new arrays.
    public void Init(int x, int y, int z, int floodLevel, int waterTint, bool ahead = false)
    {
        X = x;
        Y = y;
        Z = z;
        FloodLevel = floodLevel;
        WaterTint = waterTint;
        Age = 0;
        IsDead = false;
        Ahead = ahead;
        Opacity = 1f;
        North = East = South = West = null;
        Sim.Clear();
    }

    // False when the node died: off the water, or MaxAge ticks without a touch; in the amplitude fade, past MaxAge
    // once its field is quiet, or past AmplitudeMaxAge. The age grows even on the tick the node dies, as in Wakes.
    // With the synchronous halo the ring is already in place (WakeChunkMap.Tick), and CopyHalo would overwrite it.
    public bool Tick(SimulationParams p, IWaterProbe water, IFloodQueue queue, CoreModes modes = CoreModes.None)
    {
        bool amplitude = (modes & CoreModes.AmplitudeFade) != 0;
        if (!water.IsSurfaceWater(X, Y, Z)) MarkDead();
        int age = Age++;
        if (amplitude
                ? age >= SimulationParams.AmplitudeMaxAge || age >= SimulationParams.MaxAge && Sim.IsQuiet(1f)
                : age >= SimulationParams.MaxAge)
        {
            MarkDead();
        }
        if (IsDead) return false;

        // Where Wakes asks it, after the death checks; the texels take it at the end of the tick.
        if (!p.CacheWaterTint) WaterTint = water.WaterTint(X, Y, Z);
        float t = amplitude ? 0 : Age / (float)SimulationParams.MaxAge;
        Opacity = (float)(1.0 - (double)t * t);
        if ((modes & CoreModes.SyncHalo) == 0) Sim.CopyHalo(North?.Sim, South?.Sim, East?.Sim, West?.Sim);
        Sim.Step(p.Alpha, p.Beta);
        FloodFill(p, queue);
        return true;
    }

    // The synchronous halo for this node (src_port_snap). Links are mutual and cleared on death, so a diagonal
    // neighbour is the same through either side; one that touches this node only at the corner, with neither side
    // neighbour there, is not linked and its corner gets 0, as in the oracle. Through the links, not the chunk grid:
    // a lookup by position also finds that one and parts from the oracle by |du| up to 0.53.
    public void SnapHalo()
    {
        Sim.SnapHalo(North?.Sim, South?.Sim, East?.Sim, West?.Sim,
            (North?.East ?? East?.North)?.Sim, (North?.West ?? West?.North)?.Sim,
            (South?.East ?? East?.South)?.Sim, (South?.West ?? West?.South)?.Sim);
    }

    // After FloodFillTickDelay ticks without a touch the node hands FloodLevel - 1 to the four sides and drops its own
    // to 0; a neighbour's flood fill can raise it again. An empty side gets a node through the queue, an occupied one
    // is reset at once, inside the traversal: a neighbour that has not ticked yet ticks this very tick with age 0
    // (FINDINGS "Жизнь узла"). The order N, E, S, W is the queue order of Wakes.
    void FloodFill(SimulationParams p, IFloodQueue queue)
    {
        if (FloodLevel <= 0 || Age <= p.FloodFillTickDelay) return;

        int level = FloodLevel - 1;
        if (North == null) queue.QueueFlood(X, Y, Z - 1, level);
        else North.UpdateFloodLevel(level);
        if (East == null) queue.QueueFlood(X + 1, Y, Z, level);
        else East.UpdateFloodLevel(level);
        if (South == null) queue.QueueFlood(X, Y, Z + 1, level);
        else South.UpdateFloodLevel(level);
        if (West == null) queue.QueueFlood(X - 1, Y, Z, level);
        else West.UpdateFloodLevel(level);
        FloodLevel = 0;
    }

    void UpdateFloodLevel(int level)
    {
        Age = 0;
        Ahead = false;
        if (level > FloodLevel) FloodLevel = level;
    }

    // A new touch of a live node. Port: Wakes also put floodLevel back to floodFillDistance here, so a source that
    // touched a block every third tick or less restarted its halo again and again (Reforged 6d47ade). The impulses
    // of the touch are the caller's (T04.05).
    public void Revive()
    {
        Age = 0;
    }

    // The first touch of a node made ahead: it takes the touch's flood level, which the touch would have given the
    // node it made without the edit.
    public void Promote(int floodLevel)
    {
        if (!Ahead) return;
        Ahead = false;
        FloodLevel = floodLevel;
    }

    // A dead node leaves its neighbours at once, and the next ones in the traversal neither read its edge nor reset
    // it by flood fill: their side is empty again and floods anew. Port: Wakes kept the links, the halo went on
    // reading the dead arrays and flood fill never re-entered the block (src_nul). The neighbours' ghost rings keep
    // its last row (FINDINGS "Баги и причуды оригинала").
    public void Unlink()
    {
        if (North?.South == this) North.South = null;
        if (East?.West == this) East.West = null;
        if (South?.North == this) South.North = null;
        if (West?.East == this) West.East = null;
        North = East = South = West = null;
    }

    // Once only, as Wakes guards it: a node that leaves the water on its last tick is marked twice. Wakes freed the
    // atlas slot here; the pool frees it when the dead node comes back, in the same walk.
    public void MarkDead()
    {
        if (IsDead) return;
        IsDead = true;
    }

    // Inner cell of layer 0..2 (next, current, previous), 0-based without the ghost ring.
    public float U(int layer, int z, int x) => Sim.Get(layer, z, x);
}
