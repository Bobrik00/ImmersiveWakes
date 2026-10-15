#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ImmersiveWakes.Sources;
using Vintagestory.API.Config;

namespace ImmersiveWakes.Debug;

// И20 of T08.05: what the speed of a source the server moves does after the client stood still. The strength of its
// trail comes from the interpolator's snapshots (Н9, RemoteSpeed); the frame delta of its Pos, which the port keeps for
// the line only, carries the catch-up rate of the interpolator, vanilla's or OptiTime's. .wakes freeze sleeps the main
// thread: the single player server runs on in its own thread (it suspends only on IsGamePaused, ClientProgram
// .ServerThreadStart) and its position packets pile up in the dummy network's buffer as UDP ones do in multiplayer.
// The trace keeps every sample of those sources that has a time step and writes them to wakes-trace.csv in the log
// folder, which the auto run points into its run folder.
sealed class FreezeTrace
{
    public const double MaxSeconds = 60;
    // The windows of the summary, in seconds after the freeze ended; the first is the time before the freeze.
    static readonly double[] After = [0, 0.6, 1.5, 3, 6];
    const double BeforeSeconds = 3;
    // Below this snapshot speed the ratio of the delta to it says nothing.
    const double RatioFloor = 0.3;

    readonly record struct Sample(double T, long Id, string Code, SourceVerdict Verdict, double Dt, double Snap,
        double Delta, int Impulse, int DeltaImpulse, int Queue, float TargetSpeed, double X, double Z, int Packet);

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly double seconds;
    readonly List<Sample> samples = [];
    readonly List<(double From, double To)> freezes = [];

    public FreezeTrace(double seconds) => this.seconds = seconds;

    public bool Running => clock.Elapsed.TotalSeconds < seconds;

    public void Record(SourceState s, bool snapshots)
    {
        if (!snapshots || !Running) return;
        SourceVerdict v = s.Verdict;
        if (v is not (SourceVerdict.Trail or SourceVerdict.Still or SourceVerdict.Jump or SourceVerdict.SpeedJump)) return;
        // A jump of the position leaves LastSpeed and the strength of the sample before.
        bool drawn = v is SourceVerdict.Trail or SourceVerdict.Still;
        double delta = s.LastDt > 0 ? s.LastDistance / s.LastDt : 0;
        double snap = v == SourceVerdict.Jump || (drawn && s.LastSpeedSource != SpeedSource.Snapshots) ? -1 : s.LastSpeed;
        int impulse = drawn ? s.LastImpulse : 0;
        // What the trail would get from the delta, the core's own cast.
        int deltaImpulse = drawn ? (int)(s.LastStrength * delta * 0.05) : 0;
        var interpolator = s.Interpolator!;
        samples.Add(new Sample(clock.Elapsed.TotalSeconds, s.Entity.EntityId, s.Entity.Code.Path, v, s.LastDt, snap, delta,
            impulse, deltaImpulse, interpolator.queueCount, interpolator.targetSpeed, s.Entity.Pos.X, s.Entity.Pos.Z,
            s.Entity.Attributes.GetInt("tick")));
    }

    public void Froze(double from, double to) => freezes.Add((from, to));

    public double Now => clock.Elapsed.TotalSeconds;

    public string Summary()
    {
        string path = Path.Combine(GamePaths.Logs, "wakes-trace.csv");
        var csv = new StringBuilder("t,id,code,verdict,dt,snap,delta,impulse,deltaImpulse,queue,targetSpeed,x,z,packet\n");
        foreach (Sample p in samples)
        {
            csv.Append(F($"{p.T:0.000},{p.Id},{p.Code},{p.Verdict},{p.Dt:0.000},{p.Snap:0.000},{p.Delta:0.000},{p.Impulse},"))
                .Append(F($"{p.DeltaImpulse},{p.Queue},{p.TargetSpeed:0.00},{p.X:0.00},{p.Z:0.00},{p.Packet}\n"));
        }
        File.WriteAllText(path, csv.ToString());

        var sb = new StringBuilder(F($"trace: {samples.Count} samples over {Math.Min(Now, seconds):0.0} s, "));
        sb.Append(Running ? "running" : "done").Append(", ");
        sb.Append(freezes.Count == 0 ? "no freeze" : string.Join(", ", freezes.Select(f => F($"freeze {f.From:0.00}..{f.To:0.00} s"))));
        sb.Append(", ").Append(path);
        if (freezes.Count == 0) return sb.ToString();
        double start = freezes[^1].From, end = freezes[^1].To;
        foreach (IGrouping<long, Sample> source in samples.GroupBy(p => p.Id))
        {
            sb.Append(F($"\n#{source.Key} {source.First().Code}: snapshot speed, delta speed in b/s med/max; impulse and by delta max; delta/snap med/max; queue, targetSpeed max; jumps; packets a second"));
            Window(sb, "before", source.Where(p => p.T >= start - BeforeSeconds && p.T < start));
            for (int i = 0; i + 1 < After.Length; i++)
            {
                double a = end + After[i], b = end + After[i + 1];
                Window(sb, F($"{After[i]:0.0}-{After[i + 1]:0.0}"), source.Where(p => p.T >= a && p.T < b));
            }
        }
        return sb.ToString();
    }

    static void Window(StringBuilder sb, string name, IEnumerable<Sample> window)
    {
        Sample[] w = window.ToArray();
        sb.Append(F($"\n  {name,-8} n {w.Length,3}"));
        if (w.Length == 0) return;
        double[] snap = w.Where(p => p.Snap >= 0).Select(p => p.Snap).ToArray();
        double[] delta = w.Where(p => p.Verdict != SourceVerdict.Jump).Select(p => p.Delta).ToArray();
        double[] ratio = w.Where(p => p.Snap >= RatioFloor && p.Verdict is SourceVerdict.Trail or SourceVerdict.Still)
            .Select(p => p.Delta / p.Snap).ToArray();
        sb.Append(F($"  snap {Med(snap):0.00}/{Max(snap):0.00}  delta {Med(delta):0.00}/{Max(delta):0.00}"));
        sb.Append(F($"  imp {w.Max(p => p.Impulse)}/{w.Max(p => p.DeltaImpulse)}  ratio {Med(ratio):0.00}/{Max(ratio):0.00}"));
        sb.Append(F($"  q {w.Max(p => p.Queue)} ts {w.Max(p => p.TargetSpeed):0.00}"));
        // The packet number the client last took for the entity (SystemNetworkProcess.HandleSinglePacket): the server
        // counts its own packets, one for each pass that moved the entity, so this is packets a second.
        double span = w[^1].T - w[0].T;
        if (span > 0) sb.Append(F($"  packets {(w[^1].Packet - w[0].Packet) / span:0.0}/s"));
        sb.Append(F($"  jumps {w.Count(p => p.Verdict == SourceVerdict.Jump)}+{w.Count(p => p.Verdict == SourceVerdict.SpeedJump)}"));
    }

    static double Med(double[] v)
    {
        if (v.Length == 0) return double.NaN;
        double[] sorted = [.. v.Order()];
        return sorted[sorted.Length / 2];
    }

    static double Max(double[] v) => v.Length == 0 ? double.NaN : v.Max();

    static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
#endif
