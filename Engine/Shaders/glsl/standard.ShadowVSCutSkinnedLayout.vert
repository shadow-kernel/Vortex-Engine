#version 450
#extension GL_GOOGLE_include_directive : require
// ShadowVSCut for meshes bound with the SKINNED vertex layout (standard.metal ShadowVSCutSkinnedLayout): the
// per-instance world matrix sits at locations 5..8; bone indices / weights (3, 4) are supplied but not read.
#define SET_UNIFORM 1
#include "standard_common.glsl"

layout(location = 0) in vec3 a_pos;
layout(location = 2) in vec2 a_uv;
layout(location = 5) in vec4 a_iw0;
layout(location = 6) in vec4 a_iw1;
layout(location = 7) in vec4 a_iw2;
layout(location = 8) in vec4 a_iw3;

layout(location = 0) out vec2 v_uv;

void main()
{
	mat4 world = mat4(a_iw0, a_iw1, a_iw2, a_iw3);
	gl_Position = frame.view_projection * (world * vec4(a_pos, 1.0));
	v_uv = a_uv;
}
