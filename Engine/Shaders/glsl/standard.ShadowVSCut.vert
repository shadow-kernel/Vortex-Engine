#version 450
#extension GL_GOOGLE_include_directive : require
// Shadow map pass for cut-out casters (#329, standard.metal ShadowVSCut): depth only, plus the uv the fragment
// stage needs to read the albedo alpha. The light's view-projection arrives in PerFrame.view_projection.
#define SET_UNIFORM 1
#include "standard_common.glsl"

layout(location = 0) in vec3 a_pos;
layout(location = 1) in vec3 a_norm;
layout(location = 2) in vec2 a_uv;
layout(location = 3) in vec4 a_iw0;
layout(location = 4) in vec4 a_iw1;
layout(location = 5) in vec4 a_iw2;
layout(location = 6) in vec4 a_iw3;

layout(location = 0) out vec2 v_uv;

void main()
{
	mat4 world = mat4(a_iw0, a_iw1, a_iw2, a_iw3);
	gl_Position = frame.view_projection * (world * vec4(a_pos, 1.0));
	v_uv = a_uv;
}
