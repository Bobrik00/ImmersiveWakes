#version 330 core
// The splash clouds, drawn instanced in the OIT stage (SplashCloudRenderer): one quad facing the camera per cloud.
// The engine includes come in the order of the overlay; wakesanchorwarp.vsh needs vertexflagbits.ash and
// vertexwarp.vsh before it.
#include vertexflagbits.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh
#include wakesanchorwarp.vsh

// The quad: the corner -1..1 in x and y, uv 0..1 over one frame.
layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;
// Per cloud (SplashCloudPool.Fill): xyz the centre relative to the camera, w the still surface under it.
layout(location = 2) in vec4 cloudPos;
// rgb block light, a sun light, as the vertex light of chunk meshes.
layout(location = 3) in vec4 cloudLight;
// x half the side of the quad, y alpha, z the wave weight at the cloud's point, w the tile: strip + frame * strips.
layout(location = 4) in vec4 cloudMisc;

uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;
// Strips and frames of the flipbook texture.
uniform vec2 cloudGrid;

// How far uv stays inside a frame, 1/256 of it: a fragment whose centre falls on the quad's edge would otherwise read
// the first texel of the next strip or frame.
const float WAKES_CLOUD_UV_INSET = 1.0 / 256.0;

// Use() fills the uniforms of the engine includes only; these the mod sets itself.
uniform vec4 rgbaFogIn;
uniform vec3 rgbaAmbientIn;
uniform float fogDensityIn;
uniform float fogMinIn;

out vec2 uv;
out float cloudAlpha;
out vec4 rgba;
out vec4 rgbaFog;
out float fogAmount;
out vec3 fWorldPos;

void main(void)
{
	// The camera's right and up in world axes: the rows of the view rotation. The corners are placed in the world, so
	// fog, shadows and the murk test see each corner where it is.
	vec3 right = vec3(modelViewMatrix[0][0], modelViewMatrix[1][0], modelViewMatrix[2][0]);
	vec3 up = vec3(modelViewMatrix[0][1], modelViewMatrix[1][1], modelViewMatrix[2][1]);
	vec4 worldPos = vec4(cloudPos.xyz + (right * xyz.x + up * xyz.y) * cloudMisc.x, 1.0);
	// The whole quad moves with the water at the cloud's point on the still surface.
	worldPos = wakesAnchorWarp(worldPos, vec3(cloudPos.x, cloudPos.w, cloudPos.z), cloudMisc.z);
	vec4 cameraPos = modelViewMatrix * worldPos;
	gl_Position = projectionMatrix * cameraPos;

	int strips = int(cloudGrid.x + 0.5);
	int tile = int(cloudMisc.w + 0.5);
	vec2 inFrame = mix(vec2(WAKES_CLOUD_UV_INSET), vec2(1.0 - WAKES_CLOUD_UV_INSET), uvIn);
	uv = (vec2(float(tile % strips), float(tile / strips)) + inFrame) / cloudGrid;
	cloudAlpha = cloudMisc.y;
	// Render flags 0: no glow. applyLight also writes blockBrightness, which getBrightnessFromShadowMap adds.
	rgba = applyLight(rgbaAmbientIn, cloudLight, 0, cameraPos);
	rgbaFog = rgbaFogIn;
	fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
	fWorldPos = worldPos.xyz;
	calcShadowMapCoords(modelViewMatrix, worldPos);
}
