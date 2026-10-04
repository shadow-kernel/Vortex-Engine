#version 450
#extension GL_GOOGLE_include_directive : require
// mode: 0 solid (optionally a rounded rect), 1 text (alpha from the R8 glyph atlas), 2 image (RGBA).
#include "common.glsl"

layout(location = 0) in vec2 v_uv;
layout(location = 1) in vec4 v_color;
layout(location = 2) in vec4 v_misc;
layout(location = 3) in vec2 v_local;

layout(location = 0) out vec4 o_color;

layout(set = 2, binding = 0) uniform sampler2D u_tex;

void main()
{
	vec4 color = v_color;
	int mode = int(v_misc.x);
	if (mode == 1)
	{
		color.a *= texture(u_tex, v_uv).r;
	}
	else if (mode == 2)
	{
		color *= texture(u_tex, v_uv);
	}
	else if (v_misc.y > 0.5)
	{
		// Rounded rectangle: signed distance to the rounded box in rect-local pixels, 1px anti-aliased edge.
		vec2 half_size = v_misc.zw;
		float radius = min(v_misc.y, min(half_size.x, half_size.y));
		vec2 q = abs(v_local) - (half_size - radius);
		float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
		color.a *= saturate(0.5 - d);
	}
	o_color = color;
}
