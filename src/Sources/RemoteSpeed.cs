using System;
using Vintagestory.API.Common;

namespace ImmersiveWakes.Sources;

// Speed of an entity the server moves, from the interpolator's last two snapshots (Н9). The frame delta of its Pos
// carries the catch-up rate of the interpolator, which swings by a fifth around its steady value and goes past 20 after
// a freeze of 2.5 s, with OptiTime too (T08.05), so the impulse would turn white after a lag; the delta still gives the
// line, which follows what is drawn.
//
// The engine plays a snapshot over tickDiff/15 s, tickDiff being the difference of the entity's packet numbers
// (SystemNetworkProcess.HandleSinglePacket), but the server numbers packets, not physics ticks. An entity its physics
// moves gets a packet for every tick of 1/30 s that moved it while the server's loop keeps its 33 ms, and one for two
// ticks when a pass runs late (PhysicsManager.DoWork, BuildPositionPacket; ServerMain.Process): a swimming deer gets
// 29.5 packets a second in single player, and the engine's interval reads half its speed. A player and the mount it
// steers move on its own client, which sends their position every fourth tick of 1/60 s (EntityBehaviorPlayerPhysics),
// and the server passes each packet on with a number of its own (ServerUdpNetwork.HandlePlayerPosition,
// HandleMountPosition): 15 a second, as the engine takes it. Measure counts the packets of the others over the
// tracker's clock, and the speed is the step of the pair times the rate.
static class RemoteSpeed
{
    // Further apart than this, the pair is not a step of the entity: a teleport of a player farther than this, or a boat
    // the local player steered and left far from where it was boarded. 20 b/s over the longest interval of 5 packets at
    // 15 a second is 6.7.
    const double MaxSnapshotStep = 16;

    // The rate the engine's interval stands for, which is a player's, and the bounds of a measured one: a server that
    // keeps up sends a moving entity one packet a tick, one that lags at least one every second tick.
    const double EngineRate = 15, RelayedRate = 15, MinRate = 15, MaxRate = 30;

    // Before anything is measured, the rate of a server that keeps up.
    public const double DefaultRate = MaxRate;

    // A window counts the packets of a second of motion, so the 15 ms reads of the network and the jitter of a remote
    // server cost a few percent. A still entity gets a packet a second (PhysicsManager's forced update); a counter that
    // stands longer than StillGap drops the window, so a start or a stop inside one leaves no rate between.
    const double RateWindow = 1, StillGap = 0.3;

    // hitch: the tracker's clock right after the last frame longer than its cap, whose packets came for more time than
    // the clock counted; with the TCP fallback they land a frame later, so the window starts over in that frame too.
    // serverRate: the last rate measured on any entity of the server's physics, where a new one starts (a projectile
    // rarely lives a whole window).
    public static void Measure(SourceState s, double clock, double hitch, ref double serverRate)
    {
        bool relayed = s.Kind == SourceKind.Player || s.Mountable?.Controller is EntityPlayer;
        if (!s.RateKnown || relayed != s.RateRelayed)
        {
            s.RateKnown = true;
            s.RateRelayed = relayed;
            s.PacketRate = relayed ? RelayedRate : serverRate;
            s.RateTime = -1;
        }
        if (relayed) return;
        int packet = s.Entity.Attributes.GetInt("tick");
        if (packet != s.SeenPacket)
        {
            s.SeenPacket = packet;
            s.SeenTime = clock;
        }
        // Standing still, or a boat the local player steers, which gets no packets; the ones it missed come in one jump
        // as the player leaves the seat, and the window starts after it.
        if (clock - s.SeenTime > StillGap)
        {
            s.RateTime = -1;
            return;
        }
        if (s.RateTime < 0 || s.RateTime <= hitch || packet < s.RatePacket)
        {
            s.RatePacket = packet;
            s.RateTime = clock;
            return;
        }
        // To the last packet seen, so a stop just before the end does not thin the count.
        double span = s.SeenTime - s.RateTime;
        if (span < RateWindow) return;
        s.PacketRate = Math.Clamp((packet - s.RatePacket) / span, MinRate, MaxRate);
        serverRate = s.PacketRate;
        s.RatePacket = packet;
        s.RateTime = s.SeenTime;
    }

    // A pass of the server that runs late puts two ticks into one packet, and its pair reads twice the speed: one sample
    // in 32 of a deer swimming 3.3 b/s in single player (T08.05); a server that lags alternates packets of one tick and
    // two. The trail takes the mean of the last Recent pairs the samples saw, 150 ms at 20 Hz.
    public const int Recent = 3;

    public static double Mean(SourceState s, double speed)
    {
        s.RemoteSpeeds[s.RemoteCount++ % Recent] = speed;
        int n = Math.Min(s.RemoteCount, Recent);
        double sum = 0;
        for (int i = 0; i < n; i++) sum += s.RemoteSpeeds[i];
        return sum / n;
    }

    // Horizontal blocks a second, or -1 when the pair says nothing.
    public static double Of(EntityBehaviorInterpolatePosition interpolator, double packetRate)
    {
        PositionSnapshot last = interpolator.pL, next = interpolator.pN;
        // The snapshot of Initialize carries interval 0 on 1.22.3; 1.22.7 gives it 1/15, and the step bound below drops
        // it then, since it pairs with the empty pL at the origin.
        if (!(next.interval > 0)) return -1;
        double dx = next.x - last.x, dz = next.z - last.z;
        double distance = Math.Sqrt(dx * dx + dz * dz);
        if (!(distance <= MaxSnapshotStep)) return -1;
        return distance / Seconds(next, packetRate);
    }

    // The time a snapshot stands for: its interval is its difference of packet numbers over 15, 1 to 5 of them.
    public static double Seconds(PositionSnapshot snapshot, double packetRate) =>
        Math.Max(snapshot.interval * EngineRate, 1) / packetRate;
}
