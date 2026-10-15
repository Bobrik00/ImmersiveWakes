using System;
using OpenTK.Graphics.OpenGL;

namespace ImmersiveWakes.Debug;

enum GpuChannel { Upload, Mesh, Oit, Cloud }

// GPU time of the mod's own GL work by TIME_ELAPSED queries, which the engine never uses (FINDINGS "GPU-симуляция,
// замеры, частицы"). A result is read without waiting, a frame or more later: QUERY_RESULT in the frame of the query
// would stall until the GPU catches up. Each channel keeps a ring of queries; one still pending when its turn to be
// reused comes is dropped and counted, never waited for.
//
// Only one TIME_ELAPSED query may be active at a time, so Begin refuses while one is, and End closes only what Begin
// opened. The queries are made on the first Begin, on the thread with the GL context. Off until someone asks for the
// numbers: reading a query's availability is a glGet, which waits for the thread of a threaded driver every frame.
sealed class GpuTimer : IDisposable
{
    // Results lag the frame by the frames the driver queues ahead, two or three; eight leaves room for a slow frame.
    const int Ring = 8;
    static readonly int Channels = Enum.GetValues<GpuChannel>().Length;

    readonly int[] ids = new int[Channels * Ring];
    // The window a query was started in, 0 for a free slot; a result from an earlier window is thrown away.
    readonly int[] windowOf = new int[Channels * Ring];
    readonly int[] next = new int[Channels];
    readonly Sample[] samples = new Sample[Channels];
    int window = 1;
    int active = -1;
    bool created;

    public struct Sample
    {
        public int Count;
        public long SumNs;
        public long MaxNs;
        public int Dropped;

        public double MeanMs => Count > 0 ? SumNs / 1e6 / Count : 0;
        public double MaxMs => MaxNs / 1e6;
    }

    public Sample this[GpuChannel channel] => samples[(int)channel];

    public bool Enabled { get; set; }

    // False when off or when another query is running; the caller then skips End.
    public bool Begin(GpuChannel channel)
    {
        if (!Enabled || active >= 0) return false;
        if (!created)
        {
            GL.GenQueries(ids.Length, ids);
            created = true;
        }
        int c = (int)channel;
        Collect(c);
        int slot = c * Ring + next[c];
        if (windowOf[slot] != 0)
        {
            // Still pending after a whole ring of frames: starting it again discards the old result.
            if (windowOf[slot] == window) samples[c].Dropped++;
            windowOf[slot] = 0;
        }
        GL.BeginQuery(QueryTarget.TimeElapsed, ids[slot]);
        windowOf[slot] = window;
        next[c] = (next[c] + 1) % Ring;
        active = c;
        return true;
    }

    public void End()
    {
        if (active < 0) return;
        GL.EndQuery(QueryTarget.TimeElapsed);
        active = -1;
    }

    // Reads every result that is ready. Begin does this for its own channel; a status line calls it for all.
    public void CollectAll()
    {
        if (!created) return;
        for (int c = 0; c < Channels; c++) Collect(c);
    }

    void Collect(int c)
    {
        for (int k = 0; k < Ring; k++)
        {
            int slot = c * Ring + k;
            if (windowOf[slot] == 0 || (active == c && k == (next[c] + Ring - 1) % Ring)) continue;
            GL.GetQueryObject(ids[slot], GetQueryObjectParam.QueryResultAvailable, out int ready);
            if (ready == 0) continue;
            GL.GetQueryObject(ids[slot], GetQueryObjectParam.QueryResult, out long ns);
            if (windowOf[slot] == window)
            {
                ref Sample s = ref samples[c];
                s.Count++;
                s.SumNs += ns;
                s.MaxNs = Math.Max(s.MaxNs, ns);
            }
            windowOf[slot] = 0;
        }
    }

    // Starts a new window; queries still in flight from the old one are read and thrown away.
    public void Reset()
    {
        Array.Clear(samples);
        window++;
    }

    public void Dispose()
    {
        if (!created) return;
        created = false;
        GL.DeleteQueries(ids.Length, ids);
    }
}
