#version 450
#extension GL_GOOGLE_include_directive : require
// Static mesh vertex stage (standard.metal VSMain). The world matrix arrives per instance in vertex buffer
// slot 1 as four float4 rows (iw0..iw3 = the matrix columns, as in the .metal twin).
#define SET_UNIFORM 1
#include "standard_common.glsl"
#include "standard_vsout.glsl"

layout(location = 0) in vec3 a_pos;
layout(location = 1) in vec3 a_norm;
layout(location = 2) in vec2 a_uv;
layout(location = 3) in vec4 a_iw0;
layout(location = 4) in vec4 a_iw1;
layout(location = 5) in vec4 a_iw2;
layout(location = 6) in vec4 a_iw3;

void main()
{
	mat4 world = mat4(a_iw0, a_iw1, a_iw2, a_iw3);
	vec4 world_pos = world * vec4(a_pos, 1.0);
	v_world_pos = world_pos.xyz;
	gl_Position = frame.view_projection * world_pos;
	mat3 world3 = mat3(a_iw0.xyz, a_iw1.xyz, a_iw2.xyz);
	v_norm = normalize(world3 * a_norm);
	v_uv = a_uv;
	tangent_basis(v_norm, v_tangent, v_bitangent);
}
