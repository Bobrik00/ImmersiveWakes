#version 330 core
// The bake of the traced foam's texels (TraceBake): a texel of the wake texture, whose slots lie 1:1 with the region
// in the entity atlas, with its alpha times the factor of its slot: wakeOpacity, the far alpha and the fade with
// distance, which the raster applies in wakessurface.vsh and the tracer would never see. A texel whose centre lies
// under a hull's water mask gets alpha 0, as Wakes' OcclusionZone clears it (TraceHulls).

// Declared first: a sampler gets the unit equal to its declaration index.
uniform sampler2D wakeTex;
// One RGBA32F texel per slot: the factor, the bits of the hulls over the node, the node's x and z from the origin.
uniform sampler2D factorTex;
// The hulls' masks, 255 under a plate.
uniform sampler2D hullTex;

const int MAX_HULLS = 16;
// Per hull: its Pos from the origin in x and z, then cos and sin of its turn into the world.
uniform vec4 hullPose[MAX_HULLS];
// The mask's least and greatest x and z in the hull's axes.
uniform vec4 hullFrame[MAX_HULLS];
// The mask's corner in hullTex, xy, and its size in texels, zw.
uniform vec4 hullCell[MAX_HULLS];

layout(location = 0) out vec4 outColor;

// Texels a slot, WakeAtlasCpu.GameTexels; texels a block of a mask, HullFootprint.TexelsPerBlock.
const int SLOT_TEXELS = 32;
const float MASK_TEXELS = 32.0;

void main(void)
{
	ivec2 texel = ivec2(gl_FragCoord.xy);
	vec4 color = texelFetch(wakeTex, texel, 0);
	vec4 slot = texelFetch(factorTex, texel / SLOT_TEXELS, 0);
	float alpha = clamp(color.a * slot.r, 0.0, 1.0);
	uint bits = uint(slot.g);
	if (bits != 0u)
	{
		// u along +x, v along +z, as the quads of TraceMesh lay the slot over the block.
		vec2 p = slot.ba + (vec2(texel % SLOT_TEXELS) + 0.5) / float(SLOT_TEXELS);
		for (int i = 0; i < MAX_HULLS; i++)
		{
			if ((bits & (1u << uint(i))) == 0u) continue;
			vec4 pose = hullPose[i];
			vec2 d = p - pose.xy;
			vec2 l = vec2(pose.z * d.x - pose.w * d.y, pose.w * d.x + pose.z * d.y);
			vec4 frame = hullFrame[i];
			if (l.x < frame.x || l.y < frame.y || l.x >= frame.z || l.y >= frame.w) continue;
			// A point a float's step short of the greatest x or z would read the next mask on the shelf.
			ivec2 m = ivec2(hullCell[i].xy) + min(ivec2((l - frame.xy) * MASK_TEXELS), ivec2(hullCell[i].zw) - 1);
			if (texelFetch(hullTex, m, 0).r > 0.5)
			{
				alpha = 0.0;
				break;
			}
		}
	}
	outColor = vec4(color.rgb, alpha);
}
