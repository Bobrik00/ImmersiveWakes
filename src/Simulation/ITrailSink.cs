namespace ImmersiveWakes.Simulation;

// Where a source hands its step: the core itself (the bench), or the input of the simulation thread, which replays
// the steps into the core before each tick (StepInput, T05.11).
interface ITrailSink
{
    // WakeHandler.AddTrail: false when the step draws nothing.
    bool AddTrail(double fromX, double fromZ, double toX, double toZ, int y, float strength, double velocity, float width);

    // WakeHandler.AddSplash: false when the impulse rounds below 1 or the point is not finite.
    bool AddSplash(double x, double z, int y, float strength, double velocity, float width);
}
