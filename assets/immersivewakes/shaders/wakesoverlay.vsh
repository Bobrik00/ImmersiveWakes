#version 330 core
// The overlay program, drawn in the OIT stage. The position comes from wakessurface.vsh.
// The engine includes come in the order of chunkliquid.vsh: shader packs patch includes by file name, and the
// overlay should get the same patches as the water under it.
#include vertexflagbits.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh
#include wakessurface.vsh

// Use() fills the uniforms of the engine includes only; these the mod sets itself.
uniform vec4 rgbaFogIn;
uniform vec3 rgbaAmbientIn;
uniform float fogDensityIn;
uniform float fogMinIn;

out vec2 uv;
out vec4 rgba;
out vec4 rgbaFog;
out float fogAmount;
out vec3 fWorldPos;

void main(void)
{
	vec4 worldPos;
	vec4 cameraPos;
	wakesSurfacePosition(worldPos, cameraPos);

	uv = uvIn;
	// Render flags 0: no glow. applyLight also writes blockBrightness, which getBrightnessFromShadowMap adds;
	// unwritten, it would add garbage.
	rgba = applyLight(rgbaAmbientIn, rgbaLightIn, 0, cameraPos);
	rgbaFog = rgbaFogIn;
	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
	fWorldPos = worldPos.xyz;

	// After the warp, as in chunkliquid: the shadow lookup follows the moved surface.
	calcShadowMapCoords(modelViewMatrix, worldPos);
}
