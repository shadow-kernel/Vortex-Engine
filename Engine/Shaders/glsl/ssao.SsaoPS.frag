#version 450
#extension GL_GOOGLE_include_directive : require
// SSAO (ssao.metal SsaoPS): Alchemy-style AO over the half-res depth prepass. The depth target is bound as an
// ordinary sampled texture here (no comparison sampler), so it reads as a plain sampler2D.
#include "common.glsl"

layout(location = 0) in  vec2 v_uv;
layout(location = 0) out vec4 o_color;

layout(set = 2, binding = 0) uniform sampler2D u_depth;

layout(set = 3, binding = 0, std140) uniform SsaoCB
{
	mat4  inv_proj;     // clip -> view (this view's projection only)
	vec2  texel;        // 1 / AO target size
	float radius;       // world-space sample radius
	float intensity;
	float bias;
	float proj_scale;   // 0.5 * proj._22 * targetHeight
	vec2  pad;
} c;

vec3 view_pos(vec2 uv)
{
	float d = texture(u_depth, uv).r;
	vec2 ndc = vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
	vec4 v = c.inv_proj * vec4(ndc, d, 1.0);
	return v.xyz / max(v.w, 1e-6);
}

float hash12(vec2 p)
{
	p = fract(p * vec2(443.8975, 397.2973));
	p += dot(p, p.yx + 19.19);
	return fract(p.x * p.y);
}

void main()
{
	float d = texture(u_depth, v_uv).r;
	if (d >= 0.9999) { o_color = vec4(1.0); return; }
	vec3 P = view_pos(v_uv);
	vec3 N = normalize(cross(dFdy(P), dFdx(P)));
	float pix_radius = clamp(c.proj_scale * c.radius / max(P.z, 0.1), 2.0, 64.0);
	const int TAPS = 12;
	float rot = hash12(gl_FragCoord.xy) * 6.2831853;
	float occlusion = 0.0;
	for (int k = 0; k < TAPS; ++k)
	{
		float a = rot + float(k) * 2.3999632;
		float r = pix_radius * sqrt((float(k) + 0.7) / float(TAPS));
		vec2 duv = vec2(cos(a), sin(a)) * r * c.texel;
		vec3 S = view_pos(v_uv + duv);
		vec3 v = S - P;
		occlusion += max(0.0, dot(v, N) - c.bias) / (dot(v, v) + 0.01);
	}
	float ao = saturate(1.0 - c.intensity * occlusion * (2.0 / float(TAPS)));
	o_color = vec4(ao, ao, ao, 1.0);
}
