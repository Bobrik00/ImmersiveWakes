#version 330 core
// The splash clouds, drawn in the OIT stage: registered with Oit true, so the engine prefixes USEOIT 1, which oit.fsh
// needs for its outputs and for OIT(). The same light, fog and murk test as the wake overlay.

// Declared before every include: a sampler gets the unit equal to its declaration index, and Use() puts the shadow
// maps and liquidDepth on the units after this one.
uniform sampler2D cloudTex;

in vec2 uv;
in float cloudAlpha;
in vec4 rgba;
// applyFog in fogandlight.fsh reads rgbaFog, so it is declared before the include.
in vec4 rgbaFog;
in float fogAmount;
in vec3 fWorldPos;

#include fogandlight.fsh
#include underwatereffects.fsh
#include oit.fsh
// Declares the sampler of CoriaenderShaders' cloud map, so it goes after the engine's samplers.
#include wakescloudshadow.fsh

// CoriaenderShaders binds a seventh OIT buffer with blend ONE, ONE, and a zero leaves it as it was. Without
// Coriaender the output goes to GL_NONE.
layout(location = 6) out vec4 outCoriaenderGBuffer;

void main()
{
	vec4 color = texture(cloudTex, uv);
	color.a *= cloudAlpha;
	if (color.a < 0.005) discard;

	// The part of a cloud under the water, behind its surface from the camera, is left to the water.
	float murkiness = max(0.0, getUnderwaterMurkiness() - fogAmount);
	if (murkiness > 0.05) discard;

	color.rgb *= rgba.rgb * wakesBrightness(fWorldPos);
	color = applyFog(color, fogAmount);
	color = applySpheresFog(color, fogAmount, fWorldPos);

	OIT(color, 0.0);
	outCoriaenderGBuffer = vec4(0.0);
}
