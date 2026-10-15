#version 330 core
// The bake of the traced foam's texels (TraceBake): one quad in clip space over the viewport, which covers the slot
// rows of the region being drawn.
layout(location = 0) in vec3 xyz;

void main(void)
{
	gl_Position = vec4(xyz.xy, 0.0, 1.0);
}
