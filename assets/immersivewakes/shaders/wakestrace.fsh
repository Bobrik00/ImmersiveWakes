#version 330 core
// The traced foam's quads (TraceRenderer); see wakestrace.vsh. Nothing reaches the framebuffer.
uniform sampler2D tex;

in vec2 uv;

layout(location = 0) out vec4 outColor;

void main(void)
{
	outColor = texture(tex, uv);
	discard;
}
