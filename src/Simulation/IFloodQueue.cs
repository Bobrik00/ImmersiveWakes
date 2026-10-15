namespace ImmersiveWakes.Simulation;

// Where a node's flood fill sends a new node for an empty side. It goes into the insertion queue and enters its
// chunk on the next tick, ahead of the trails of that tick (T04.05).
interface IFloodQueue
{
    void QueueFlood(int x, int y, int z, int floodLevel);
}
