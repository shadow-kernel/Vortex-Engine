#version 450
#extension GL_GOOGLE_include_directive : require
// Shadow map pass for meshes bound with the SKINNED vertex layout (standard.metal ShadowVSSkinnedLayout).
// Same output as ShadowVS; only the attribute layout differs, so the per-instance world matrix sits at
// locations 5..8. Locations 1..4 (normal, uv, bone indices, bone weights) are supplied by the pipeline but
// not read here, which Vulkan permits.
#define SET_UNIFORM 1
#include "standard_common.glsl"

layout(location = 0) in vec3 a_pos;
layout(location = 5) in vec4 a_iw0;
layout(location = 6) in vec4 a_iw1;
layout(location = 7) in vec4 a_iw2;
layout(location = 8) in vec4 a_iw3;

void main()
{
	mat4 world = mat4(a_iw0, a_iw1, a_iw2, a_iw3);
	gl_Position = frame.view_projection * (world * vec4(a_pos, 1.0));
}
