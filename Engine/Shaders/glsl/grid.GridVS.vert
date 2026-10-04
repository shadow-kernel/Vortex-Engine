#version 450
#extension GL_GOOGLE_include_directive : require
// Fullscreen triangle; the fragment stage reconstructs the view ray and intersects the y=0 plane.
#define SET_UNIFORM 1
#include "grid_common.glsl"

layout(location = 0) out vec3 v_near_pt;
layout(location = 1) out vec3 v_far_pt;

void main()
{
	uint id = uint(gl_VertexIndex);
	vec2 uv = vec2(float((id << 1u) & 2u), float(id & 2u));
	vec2 ndc = uv * 2.0 - 1.0;
	gl_Position = vec4(ndc.x, -ndc.y, 0.0, 1.0);
	vec4 near_pt = c.inverse_view_projection * vec4(ndc.x, -ndc.y, 0.0, 1.0);
	vec4 far_pt  = c.inverse_view_projection * vec4(ndc.x, -ndc.y, 1.0, 1.0);
	v_near_pt = near_pt.xyz / near_pt.w;
	v_far_pt  = far_pt.xyz / far_pt.w;
}
