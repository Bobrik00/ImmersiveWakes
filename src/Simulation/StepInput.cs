using System;

namespace ImmersiveWakes.Simulation;

// What the sources hand the core over the steps of one hand-off to the simulation thread: their trails and splashes
// in the order of the calls and a mark where each step ends and the core ticks (T05.11). The main thread records while
// the thread is busy with the other input of the pair and replays this one into the core; neither touches an input the
// other one holds. Entries are kept between hand-offs, so a steady run allocates nothing (П5).
sealed class StepInput : ITrailSink
{
    public struct Entry
    {
        public double FromX, FromZ, ToX, ToZ, Velocity;
        public int Y;
        public float Strength, Width;

        // The end of a step: the core ticks here, and the other fields are unused.
        public bool Tick;

        // A splash at FromX, FromZ; ToX and ToZ are unused.
        public bool Splash;
    }

    Entry[] entries = new Entry[256];

    public int Count { get; private set; }

    // Steps recorded, the tick marks among the entries.
    public int Ticks { get; private set; }

    // The resolution of the core the trails go to, for the verdict AddTrail gives the source at once. A reload may
    // build a core of another resolution before the replay; the trail then goes into it as it is.
    public int Resolution { get; set; } = 16;

    public ref readonly Entry this[int index] => ref entries[index];

    // Every call is recorded, and the core decides at the replay as it would have decided here.
    public bool AddTrail(double fromX, double fromZ, double toX, double toZ, int y, float strength, double velocity, float width)
    {
        Add() = new Entry { FromX = fromX, FromZ = fromZ, ToX = toX, ToZ = toZ, Velocity = velocity, Y = y, Strength = strength, Width = width };
        return WakeHandler.Draws(fromX, fromZ, toX, toZ, strength, velocity, Resolution);
    }

    public bool AddSplash(double x, double z, int y, float strength, double velocity, float width)
    {
        Add() = new Entry { FromX = x, FromZ = z, Velocity = velocity, Y = y, Strength = strength, Width = width, Splash = true };
        return WakeHandler.SplashDraws(x, z, strength, velocity);
    }

    public void Tick()
    {
        Add() = new Entry { Tick = true };
        Ticks++;
    }

    public void Clear()
    {
        Count = 0;
        Ticks = 0;
    }

    // The trails of the first step and its mark go; the rest moves to the front.
    public void DropOldestStep()
    {
        int end = 0;
        while (end < Count && !entries[end].Tick) end++;
        if (end == Count) return;
        end++;
        Array.Copy(entries, end, entries, 0, Count - end);
        Count -= end;
        Ticks--;
    }

    ref Entry Add()
    {
        if (Count == entries.Length) Array.Resize(ref entries, entries.Length * 2);
        return ref entries[Count++];
    }
}
