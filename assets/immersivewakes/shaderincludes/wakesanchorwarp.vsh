// The water wave for the mod's meshes: the divisor chunkliquid takes for a corner, and the wave at the anchor of a mesh
// placed on the water from the CPU (splash clouds). Include vertexflagbits.ash and vertexwarp.vsh before this file.

// The divisor of the wave from a corner's oceanity 0..1: 5 on a lake, down to 1 on the open sea, where the water waves
// five times higher (chunkliquid.vsh). The weak wave divisor of 90 belongs to liquids in containers, which no world
// tesselator marks, so it is left out.
float wakesWaveDivisor(float oceanity)
{
	return 5.0 * max(0.2, 1.0 - oceanity);
}

// The oceanity of a corner from bits 2-9 of waterFlagsIn, scaled as chunkliquid scales it so the divisor matches the
// water's to the bit.
float wakesFlagOceanity(int flags)
{
	return float((flags >> 2) & 0xFF) * OneOver255;
}

// Moves a vertex of a mesh anchored on the water by the wave applyLiquidWarping gives at the anchor, so the whole mesh
// rides the water as one piece. anchor is relative to the camera, as worldPos is, at the still surface. waveWeight is
// 1 / divisor at the anchor from the CPU (OceanFlags.AnchorWave), a uniform for a draw or an attribute for an instance:
// 0.2 on a lake, up to 1 on the open sea, 0 where the water stands still. The wave comes from applyLiquidWarping, as
// the water's own: a C# port of gnoise would not match the GPU to the bit (DESIGN, fork 9). The weight is lerped as
// the water lerps its corners, the noise is not: inside a face the water lerps the moves of its corners, and the
// finer octave of the noise, under a block across, shows on the water only at the corners. On the open sea a cloud so
// stands about 0.1 block off the face, up to 0.3 at worst, whatever the wind; on a lake a fifth of that. A guess from
// the noise, not measured: a cloud bobs 0.05 to 0.22 over the still surface (T06.03) and lives 0.5 s, and the author
// took the clouds on the sea. Without WavingFoliage (WAVINGSTUFF 0) the water stands still and so does the mesh.
// applyLiquidWarping ends with the drunk sway of applyPerceptionWarping, and the mesh takes it at the anchor with the
// wave; a program on this function must not add applyGlobalWarping, which ends with that sway again.
vec4 wakesAnchorWarp(vec4 worldPos, vec3 anchor, float waveWeight)
{
	if (waveWeight <= 0.0) return worldPos;
	vec4 moved = applyLiquidWarping(true, vec4(anchor, 1.0), 1.0 / waveWeight);
	worldPos.xyz += moved.xyz - anchor;
	return worldPos;
}
