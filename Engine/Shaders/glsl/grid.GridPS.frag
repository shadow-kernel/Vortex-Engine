#version 450
#extension GL_GOOGLE_include_directive : require
// Per-pixel ray vs y=0 plane, anti-aliased minor/major lines + coloured X/Z axes with distance fade;
// writes depth so the grid sorts with the scene (grid.metal GridPS, [[depth(any)]] -> gl_FragDepth).
#define SET_UNIFORM 3
#include "common.glsl"
#include "grid_common.glsl"

layout(location = 0) in vec3 v_near_pt;
layout(location = 1) in vec3 v_far_pt;

layout(location = 0) out vec4 o_color;

float grid_line(vec3 p, float s)
{
	vec2 cc = p.xz / s;
	vec2 d = fwidth(cc);
	vec2 g = abs(fract(cc - 0.5) - 0.5) / d;
	return 1.0 - min(min(g.x, g.y), 1.0);
}

void main()
{
	vec3 dir = v_far_pt - v_near_pt;
	if (abs(dir.y) < 0.0001) discard;
	float t = -v_near_pt.y / dir.y;
	if (t < 0.0) discard;

	vec3 p = v_near_pt + t * dir;
	vec3 cam = c.camera_position;
	float dist = length(p.xz - cam.xz);
	if (dist > c.extent) discard;

	float fade = 1.0 - (dist / c.extent);
	fade = fade * fade;

	float g1 = grid_line(p, c.spacing) * 0.4;
	float g2 = grid_line(p, c.spacing * c.major) * 0.7;
	float g = saturate(g1 + g2);

	vec3 bg = vec3(0.15, 0.15, 0.18);
	vec3 line_color = vec3(0.5, 0.5, 0.5);
	float axis_w = c.spacing * min(fwidth(p.x / c.spacing), 1.0);
	if (abs(p.x) < axis_w) line_color = vec3(0.2, 0.4, 1.0);
	if (abs(p.z) < axis_w) line_color = vec3(1.0, 0.3, 0.3);

	vec3 col = mix(bg, line_color, g);
	float alpha = fade;
	if (alpha < 0.01) discard;

	vec4 clip = c.view_projection * vec4(p, 1.0);
	gl_FragDepth = clip.z / clip.w;
	o_color = vec4(col, alpha);
}
