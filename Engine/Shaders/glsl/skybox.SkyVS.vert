#version 450
#extension GL_GOOGLE_include_directive : require
#define SET_UNIFORM 1
#include "skybox_common.glsl"

layout(location = 0) out vec3 v_world_dir;

void main()
{
	uint id = uint(gl_VertexIndex);
	vec2 uv = vec2(float((id << 1u) & 2u), float(id & 2u));
	gl_Position = vec4(uv * 2.0 - 1.0, 1.0, 1.0);
	gl_Position.y = -gl_Position.y;
	vec4 clip = vec4(uv * 2.0 - 1.0, 1.0, 1.0);
	clip.y = -clip.y;
	vec4 world = c.inverse_view_projection * clip;
	v_world_dir = world.xyz / world.w - c.camera_position;
}
