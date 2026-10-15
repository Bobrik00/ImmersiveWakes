# Changelog

Player-facing release notes for ImmersiveWakes, posted to ModDB. Newest on top.
Keep entries short and written for players: what changed for them, not how.
Developer detail lives in `changelog-internal.md` and is never posted here.

## 0.9.1
- New: VS Path Tracing support. With VS Path Tracing 0.2.3 on game 1.22.7, wakes and bow spray show in the ray traced frame, see-through as without ray tracing, and stay hidden inside a sailboat's hull. With another version of VS Path Tracing they show only with ray tracing off. Set `pathTracingFoam` to `off` in the config to keep them out of the traced frame.
- Fixed: in multiplayer a boat's wake no longer breaks where you step off a boat someone else steers, and no longer draws a streak behind the boat after you move from the helm to another seat or someone else takes the helm of your raft.
- Fixed: saving immersivewakes.json from an editor that writes through a temporary file could reset the file to the defaults and clear the wakes on the water.
- Fixed: with maxNodes set very low or to 0 the mod kept allocating memory on every step.

## 0.9.0
Initial release