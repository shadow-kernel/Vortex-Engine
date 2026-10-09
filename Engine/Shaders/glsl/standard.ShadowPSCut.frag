#version 450
#extension GL_GOOGLE_include_directive : require
// Shadow map pass for cut-out casters (#329, standard.metal ShadowPSCut): no colour target; a texel whose albedo
// alpha is below the material's cutoff casts nothing.
#define SET_UNIFORM 3
#define VORTEX_NEED_PER_OBJECT
#include "standard_common.glsl"

layout(set = 2, binding = 0) uniform sampler2D u_albedo;
layout(location = 0) in vec2 v_uv;

void main()
{
	vec2 tiling = (obj.uv_tiling.x > 0.0 && obj.uv_tiling.y > 0.0) ? obj.uv_tiling : vec2(1.0, 1.0);
	float a = obj.base_color.a;
	if (obj.has_albedo_texture != 0u) a *= texture(u_albedo, v_uv * tiling).a;
	if (obj.alpha_cutoff > 0.0 && a < obj.alpha_cutoff) discard;
}
