#version 330 core
// The traced foam's quads (TraceRenderer). They are drawn only under vspt's Silent, where the draw goes to its
// Recorder with modelMatrix and the page of the entity atlas in unit 0, and never reaches GL. A draw that reached GL
// all the same leaves every vertex outside the clip volume.
layout(location = 0) in vec3 xyz;
layout(location = 1) in vec2 uvIn;

// Declared for the engine: its UniformMatrix and BindTexture2D throw on a name the program's text lacks.
uniform mat4 modelMatrix;

out vec2 uv;

void main(void)
{
	uv = uvIn;
	gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
}
