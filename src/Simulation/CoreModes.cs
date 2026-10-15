using System;

namespace ImmersiveWakes.Simulation;

// Switches of the core behind the .wakes command, not in the JSON config (forks 17 and 18, DESIGN "Переключатели
// режимов ядра"). A new handler starts with None; the game's driver turns on the two edits of T07.05 (WakeDriver).
// Fork 18 runs the amplitude fade without the synchronous halo; each mode has its own oracle, the pair has none, and
// the core allows it. The two edits have no oracle at all.
[Flags]
enum CoreModes
{
    None = 0,

    // src_port_amp, the rule of src_amp: past MaxAge a node lives while some |u| of its three layers is above 1, up
    // to AmplitudeMaxAge, and its opacity stays 1. Reforged 1.4.1 tests u0 and u1 only, ends a node a tick earlier
    // and keeps the opacity at 1 - t^2 with t = min(1, age / MaxAge).
    AmplitudeFade = 1,

    // src_port_snap: before the walk every node takes the ring of layer 1 from what its neighbours will step from, so
    // the step reads no neighbour a step ahead in any order; ages still follow the order through flood fill
    // (FINDINGS "Java-эталон ядра").
    SyncHalo = 2,

    // T07.05: a block a source touches brings its eight neighbours into the same tick, ahead of flood fill, so a ring
    // that starts at the block's edge runs on instead of reflecting there until flood fill comes two ticks later
    // (FINDINGS "Жизнь узла"). Such a node does not flood and lives while a block next to it is touched; a touch or a
    // neighbour's flood fill makes it the node either would have made.
    EagerNeighbours = 4,

    // T07.05: the texels fade out toward a side or a corner without a neighbour, where the wave would end at the
    // block's edge. Painting only, the step is the same; the debug colours are left as they are.
    SoftEdge = 8,
}
