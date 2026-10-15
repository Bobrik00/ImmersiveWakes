// Cloud shadows of CoriaenderShaders 1.2.1 on the wake and the splash clouds; include after fogandlight.fsh.
//
// Coriaender darkens the water under a cloud by a patch to getBrightnessFromShadowMap of eight programs it names by
// pass, and the mod's programs are not among them. Its hook in ShaderProgramBase.Use() still sets the values below on
// every program that includes fogandlight.fsh and declares cloudshadowsEnabled, and binds its cloud map to unit 10
// (CloudShadowEffect.OnUpdatingFogAndLightProgramUniforms). Once it sees cloudshadowsEnabled it sets all eleven, and
// the engine throws on a name the program lacks, so the names and the set stay Coriaender's. Without Coriaender
// cloudshadowsEnabled stays 0 and the branch never runs.
//
// wakesCloudShadow is getCloudShadows of CoriaenderShaders, with its world position as an argument and without the
// branch for the standard program. CoriaenderShaders is under the MIT licence:
//
// Copyright (c) 2025 Corey Bofill
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
// Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
// OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

uniform int cloudshadowsEnabled;
uniform sampler2D cloudShadowmap;
uniform vec3 cloudspaceOrigin;
uniform int cloudmapSize;
uniform int cloudTileSize;
uniform vec3 sunPosition;
uniform int cloudShadowStyle;
uniform float cloudShadowMax;
uniform float cloudStart;
uniform float cloudRange;
uniform float cloudshadowK;

// worldPos is relative to the camera, as fWorldPos of chunkliquid, which Coriaender's patch reads there.
float wakesCloudShadow(vec3 worldPos)
{
	// Slide position in sun direction to intersect cloud plane
	vec3 toCloudPlane = abs(sunPosition.y) > 0.01 ? (cloudspaceOrigin.y - worldPos.y) * (sunPosition / sunPosition.y) : vec3(0);
	vec2 cloudmapPos = (worldPos.xz + toCloudPlane.xz) / cloudTileSize;
	if (min(cloudmapPos.x, cloudmapPos.y) < cloudmapSize * -0.5 || max(cloudmapPos.x, cloudmapPos.y) > cloudmapSize * 0.5) return 1.0;
	cloudmapPos -= cloudspaceOrigin.xz;
	vec2 cloudmapUV = cloudmapPos / cloudmapSize;
	float cloudExpShadow = cloudShadowStyle == 0 ? texelFetch(cloudShadowmap, ivec2(cloudmapPos) * 4, 0).r : texture(cloudShadowmap, cloudmapUV).r;
	float shadowmapDepth = clamp((sign(sunPosition.y) * length(toCloudPlane) / cloudTileSize + cloudStart) / cloudRange, 0.0, 1.0);
	return clamp(exp(-cloudshadowK * shadowmapDepth) * cloudExpShadow, 1.0 - cloudShadowMax, 1.0);
}

// getBrightnessFromShadowMap with the cloud shadow where Coriaender's patch puts it: the minimum with the shadow
// maps' brightness before blockBrightness is added, and only with shadows on. clamp(x + blockBrightness, 0, 1) never
// falls as x grows, so the minimum taken after it comes out the same.
float wakesBrightness(vec3 worldPos)
{
	float b = getBrightnessFromShadowMap();
#if SHADOWQUALITY > 0
	if (cloudshadowsEnabled > 0) b = min(b, clamp(wakesCloudShadow(worldPos) + blockBrightness, 0.0, 1.0));
#endif
	return b;
}
