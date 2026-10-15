using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ImmersiveWakes.Simulation;

// Ticks the core off the main thread (fork 20, T05.11): the main thread hands it a StepInput, the thread replays the
// trails and splashes into the core and ticks it at every step mark. Ownership alternates and never overlaps: while a job runs,
// the core, its atlas and the input belong to the thread; once Idle, all of it belongs to the main thread again,
// which uploads the atlas strip and the quads and may change the core before it hands in the next job. Nothing is
// locked: the start and the end of a job are the hand-overs, and both events order every write before them.
//
// An exception in a tick ends the job and stays in Fault; the thread takes no job after it. The core may be left
// halfway through a tick, so the driver turns the wake off for the rest of the world (Н3). An exception in a replayed
// trail costs that trail alone, as it cost the source's call on the main thread: the core leaves nothing of a failed
// call behind (WakeHandler.QueuePixels), and the other trails and the tick go on. The thread holds no engine object of
// its own: whatever the core's water probe reads, the probe must be safe to call from here.
sealed class WakeThread : IDisposable
{
    public readonly record struct TickCost(long TrailTicks, long TickTicks, long PaintTicks, long AllocatedBytes);

    readonly Thread thread;
    readonly SemaphoreSlim go = new(0);
    readonly ManualResetEventSlim idle = new(true);
    readonly List<TickCost> costs = new(16);
    WakeHandler? core;
    StepInput? input;
    bool finished;
    volatile bool stopping;

    public WakeThread(string name)
    {
        thread = new Thread(Loop) { IsBackground = true, Name = name };
        thread.Start();
    }

    // No job running: the main thread owns the core and the results below.
    public bool Idle => idle.IsSet;

    // The exception that ended a job; valid while Idle.
    public Exception? Fault { get; private set; }

    // Stopwatch ticks of each step of the last job: the replay of its trails, the tick, the drawing inside the tick,
    // and the bytes the thread allocated in the step. Valid while Idle.
    public IReadOnlyList<TickCost> Costs => costs;

    // Replayed trails of the last job that threw, and the first exception among them. Valid while Idle.
    public int TrailFailures { get; private set; }
    public Exception? TrailFault { get; private set; }

    // Main thread, while Idle and without a Fault. The input stays the thread's until Idle again. False once Stop has
    // begun, which on SIGINT runs on another thread.
    public bool Start(WakeHandler core, StepInput input)
    {
        if (stopping) return false;
        if (!Idle || Fault != null) throw new InvalidOperationException("the wake thread is busy or failed");
        this.core = core;
        this.input = input;
        finished = false;
        costs.Clear();
        TrailFailures = 0;
        TrailFault = null;
        idle.Reset();
        go.Release();
        return true;
    }

    // Main thread, while Idle: true once for each job that ended since the last call, with or without a Fault.
    public bool TakeFinished()
    {
        if (!Idle || !finished) return false;
        finished = false;
        return true;
    }

    // Main thread: waits for the running job to end. False when it has not ended within the timeout.
    public bool WaitIdle(int timeoutMs) => idle.Wait(timeoutMs);

    void Loop()
    {
        while (true)
        {
            go.Wait();
            if (stopping)
            {
                idle.Set();
                return;
            }
            try
            {
                Run(core!, input!);
            }
            catch (Exception e)
            {
                Fault = e;
            }
            finally
            {
                core = null;
                input = null;
                finished = true;
                idle.Set();
            }
        }
    }

    void Run(WakeHandler core, StepInput input)
    {
        long trailStart = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < input.Count; i++)
        {
            ref readonly StepInput.Entry e = ref input[i];
            if (!e.Tick)
            {
                try
                {
                    if (e.Splash) core.AddSplash(e.FromX, e.FromZ, e.Y, e.Strength, e.Velocity, e.Width);
                    else core.AddTrail(e.FromX, e.FromZ, e.ToX, e.ToZ, e.Y, e.Strength, e.Velocity, e.Width);
                }
                catch (Exception fault)
                {
                    TrailFailures++;
                    TrailFault ??= fault;
                }
                continue;
            }
            long tickStart = Stopwatch.GetTimestamp();
            core.Tick();
            long end = Stopwatch.GetTimestamp();
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            costs.Add(new TickCost(tickStart - trailStart, end - tickStart, core.LastPaintTicks, bytes - allocated));
            trailStart = end;
            allocated = bytes;
        }
    }

    // A job that is running ends first; false when the thread is still alive after the timeout. The events are never
    // disposed: neither has made a kernel handle, and a Start or WaitIdle racing a Stop on the SIGINT thread would throw
    // on a disposed one.
    public bool Stop(int timeoutMs)
    {
        if (!stopping)
        {
            stopping = true;
            go.Release();
        }
        return thread.Join(timeoutMs);
    }

    public void Dispose() => Stop(Timeout.Infinite);
}
