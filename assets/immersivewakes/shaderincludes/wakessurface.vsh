// Vertex layout of the wake overlay mesh and its position on the water surface.
// Include vertexflagbits.ash and vertexwarp.vsh before this file.
#include wakesanchorwarp.vsh

// UploadMesh numbers attribute locations over the streams the mesh has, in the order xyz, normals, uv, rgba, flags.
// The overlay mesh has no normals: with them uv, rgba and flags would move to locations 2-4.
layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
// rgb is block light, a is sun light, as in the vertex light of chunk meshes.
layout(location = 2) in vec4 rgbaLightIn;
// Bits as in waterFlagsIn of chunkliquid.vsh: bit 0 marks a corner that moves with the wave, bits 2-9 hold its
// oceanity.
layout(location = 3) in int waterFlagsIn;

uniform vec3 origin;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
// wakeOpacity of the config, the alpha Wakes gives the vertices of the wake.
uniform float wakeOpacity;
// Horizontal distances where the fade with distance starts and where the wake has faded out: 48 and 96 blocks by
// default (WakeLook), never past the renderer's reach, the view distance; .wakes look fade sets others.
uniform float wakesFadeFrom;
uniform float wakesFadeEnd;
// Height above the water surface the mesh lies on (7/8 of the block). An overlay just under the wavy surface fails
// the murkiness test of underwatereffects.fsh in patches. Wakes lifts its quads by 0.01 and lowers them by as much
// while the camera is under water, where that test is off (wakesLiftSign). A uniform, so that .wakes look lift
// retunes it on the fly (WakeLook.DefaultLift).
uniform float wakesLift;
// -1 while the camera is under water, as Wakes lowers its quads then; 1 otherwise.
uniform float wakesLiftSign;
// Factor on the opacity reached at WAKES_FAR_TO blocks and beyond, ramped from WAKES_FAR_FROM; 1 leaves the opacity
// as it is (DESIGN, check C6). The colour pass clamps the product to 1.
uniform float wakesFarAlpha;

// Opacity and the fade with distance, for the texel's alpha in the fragment shader.
out float wakesAlpha;

// No invariant on gl_Position: with it the overlay's wave parted from that of chunkliquid, which has none, at nearly
// every corner far from playerReferencePos, by up to 1.6 blocks at sea. The hash of gnoise takes the last bits of its
// sums; a guess is that the driver orders the operations of an invariant position otherwise. Without it the overlay
// sits on the water to 3e-6 of a block (T07.06 on an RTX 4070 Ti, tools/rd-wave.py), under Ancestral Bliss's own wave
// and DRT AgX to the bit (T08.03).

// The ramp of wakesFarAlpha in blocks; S1 looked at the foam from 10 and from 64 blocks (DESIGN, check C6).
const float WAKES_FAR_FROM = 16.0;
const float WAKES_FAR_TO = 64.0;

// worldPos is relative to the camera, as in the chunk pools; cameraPos is in view space.
void wakesSurfacePosition(out vec4 worldPos, out vec4 cameraPos)
{
	worldPos = vec4(xyz + origin, 1.0);
	if ((waterFlagsIn & 1) == 1) {
		worldPos = applyLiquidWarping(true, worldPos, wakesWaveDivisor(wakesFlagOceanity(waterFlagsIn)));
	}
	worldPos.y += wakesLift * wakesLiftSign;
	// Per vertex, over a quad of one block.
	float flatDistance = length(worldPos.xz);
	float farFactor = mix(1.0, wakesFarAlpha, smoothstep(WAKES_FAR_FROM, WAKES_FAR_TO, flatDistance));
	wakesAlpha = wakeOpacity * farFactor * (1.0 - smoothstep(wakesFadeFrom, wakesFadeEnd, flatDistance));
	cameraPos = modelViewMatrix * worldPos;
	gl_Position = projectionMatrix * cameraPos;
}
