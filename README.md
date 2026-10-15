Boats, swimmers and animals now leave wakes. Waves spread out behind them, foam along the crests and fade away. Pick up speed in a boat and the bow starts throwing spray.

A port of the Minecraft mod Wakes by Goby56.


What leaves a wake

- Rafts, sailboats and boats from other mods
- Swimming players
- Animals in the water, ridden or not
- Floating items and your fishing bobber

Anything that falls into the water makes a splash ring: you jumping in, an arrow, a thrown stone or spear, a snowball. Wakes ride the water's waves, out at sea too.



Config


With Config lib installed you can change everything in game. Without it, edit `ModConfig/immersivewakes.json`. 






Compatibility

Tested on 1.22.3 with:

- CoriaenderShaders 1.2.1. Also fixes Coriaender's striped reflections around a sailboat, a bug that shows up without this mod too.
- Vintage Shader Polish 0.1.43
- Ancestral Bliss 1.3.15. Wakes follow its waves.
- SheyderMod 1.1.3 with DRT AgX 2.0.3. SheyderMod's reflections cover part of the foam when you look across the water; from above it shows in full.
- Simple Water Tweaks 1.2.0
- Dark Vision 1.3.7. Wakes brighten at night along with the water.
- OptiTime 1.5.15
- Rivers 5.0.3
- Shipwright 1.4.1. Every ship leaves a wake and throws spray, tuned to its hull.

Tested on 1.22.7 with VS Path Tracing 0.2.3: wakes and spray show in the ray traced frame and stay hidden inside a sailboat's hull. With another version of VS Path Tracing they show only with ray tracing off. To keep them out of the traced frame, set `pathTracingFoam` to `off`.







Optimum: not tested.

The mod has only been tested on NVIDIA cards. If wakes don't show up on AMD or Intel, please send your `client-main.log`.