#version 450
#extension GL_GOOGLE_include_directive : require
// Ribbons — trails and beams from a vertex buffer (particles.metal RibbonVS).
#define SET_UNIFORM 1
#define VORTEX_NEED_PLIGHTS
#include "common.glsl"
#include "particles_common.glsl"
#include "particles_pvout.glsl"

layout(location = 0) in vec3 a_pos;
layout(location = 1) in vec2 a_uv;
layout(location = 2) in vec4 a_color;   // SDL_GPU_VERTEXELEMENTFORMAT_UBYTE4_NORM

void main()
{
	gl_Position = f.view_projection * vec4(a_pos, 1.0);
	v_uv0 = a_uv;
	v_uv1 = a_uv;
	v_blend_t = 0.0;
	v_local = vec2(0.0, a_uv.y * 2.0 - 1.0);
	v_view_z = dot(a_pos - f.eye.xyz, f.cam_forward.xyz);
	v_color = a_color;
	v_fog = fog_amount(a_pos);
	v_light = b.lit != 0u ? particle_light(a_pos) : vec3(1.0);
}
