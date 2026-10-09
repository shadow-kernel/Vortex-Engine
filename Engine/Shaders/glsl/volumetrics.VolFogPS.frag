#version 450
#extension GL_GOOGLE_include_directive : require
// Volumetric fog ray march (volumetrics.metal VolFogPS): per half-res pixel, from the near plane to the scene depth
// (the max distance in the sky), the in-scattered light of the fog colour, the sun (through the cascades) and the
// point / spot lights (through their atlases) with a Henyey-Greenstein phase, against the transmittance of a density
// that follows the scene fog's height profile and a wind-animated noise. Output: S in rgb, T in a.
#define SET_UNIFORM 3
#define VORTEX_NEED_VLIGHTS
#include "common.glsl"
#include "volumetrics_common.glsl"

layout(location = 0) in vec2 v_uv;
layout(location = 0) out vec4 o_color;
layout(set = 2, binding = 0) uniform sampler2D u_depth;
layout(set = 2, binding = 1) uniform sampler2DShadow u_spot_shadow;
layout(set = 2, binding = 2) uniform sampler2DShadow u_csm_shadow;
layout(set = 2, binding = 3) uniform sampler2DShadow u_point_shadow;

vec3 world_from_ndc(vec2 uv, float d)
{
	vec2 ndc = vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
	vec4 h = f.inv_view_projection * vec4(ndc, d, 1.0);
	return h.xyz / h.w;
}

float hash13(vec3 p)
{
	p = fract(p * 0.1031);
	p += dot(p, p.zyx + 31.32);
	return fract((p.x + p.y) * p.z);
}

float vnoise(vec3 p)
{
	vec3 i = floor(p), fr = fract(p);
	fr = fr * fr * (3.0 - 2.0 * fr);
	float n000 = hash13(i), n100 = hash13(i + vec3(1, 0, 0)), n010 = hash13(i + vec3(0, 1, 0)), n110 = hash13(i + vec3(1, 1, 0));
	float n001 = hash13(i + vec3(0, 0, 1)), n101 = hash13(i + vec3(1, 0, 1)), n011 = hash13(i + vec3(0, 1, 1)), n111 = hash13(i + vec3(1, 1, 1));
	return mix(mix(mix(n000, n100, fr.x), mix(n010, n110, fr.x), fr.y), mix(mix(n001, n101, fr.x), mix(n011, n111, fr.x), fr.y), fr.z);
}

float density_at(vec3 p)
{
	float d = f.fog.w;
	if (f.fog2.y > 0.0 && p.y > f.fog2.x) d *= exp(-f.fog2.y * (p.y - f.fog2.x));
	if (f.fog2.z > 0.0)
	{
		vec3 q = p / max(f.fog2.w, 0.05) + vec3(0.0, 0.0, f.eye.w * f.vol.w);
		float n = vnoise(q) * 0.65 + vnoise(q * 2.7 + 17.0) * 0.35;
		d *= saturate(1.0 - f.fog2.z + f.fog2.z * n * 2.0);
	}
	return d;
}

float hg(float cos_t, float g)
{
	float g2 = g * g;
	return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * cos_t, 1e-4), 1.5));
}

float p_atten(float dist, float range)
{
	float d = dist / range;
	float a = saturate(1.0 - d * d);
	return a * a / (dist * dist + 0.01);
}

float spot_shadow(vec3 wp, int slot, float strength, float bias)
{
	vec4 sp = lights.shadow_vp[slot] * vec4(wp, 1.0);
	if (sp.w <= 0.0) return 1.0;
	vec3 ndc = sp.xyz / sp.w;
	vec2 suv = ndc.xy * vec2(0.5, -0.5) + 0.5;
	if (any(notEqual(saturate(suv), suv)) || ndc.z > 1.0) return 1.0;
	float texel = f.params2.w;
	suv = clamp(suv, vec2(texel * 0.5), vec2(1.0 - texel * 0.5)) * 0.5;
	suv += vec2((slot & 1) != 0 ? 0.5 : 0.0, (slot & 2) != 0 ? 0.5 : 0.0);
	float lit = texture(u_spot_shadow, vec3(suv, ndc.z - bias));
	return mix(1.0, lit, saturate(strength));
}

float cascade_shadow(vec3 wp)
{
	int count = int(lights.dir_shadow_params.z);
	if (count <= 0) return 1.0;
	for (int c = 0; c < 3; ++c)
	{
		if (c >= count) break;
		vec4 sp = lights.cascade_vp[c] * vec4(wp, 1.0);
		if (sp.w <= 0.0) continue;
		vec3 ndc = sp.xyz / sp.w;
		vec2 suv = ndc.xy * vec2(0.5, -0.5) + 0.5;
		if (min(suv.x, suv.y) < 0.02 || max(suv.x, suv.y) > 0.98 || ndc.z > 1.0 || ndc.z < 0.0) continue;
		float texel = f.params2.w;
		suv = clamp(suv, vec2(texel * 1.5), vec2(1.0 - texel * 1.5)) * 0.5;
		suv += vec2((c & 1) != 0 ? 0.5 : 0.0, (c & 2) != 0 ? 0.5 : 0.0);
		float bias = lights.dir_shadow_params.y * (1.0 + float(c));
		float lit = texture(u_csm_shadow, vec3(suv, ndc.z - bias));
		return mix(1.0, lit, saturate(lights.dir_shadow_params.x));
	}
	return 1.0;
}

float point_shadow(vec3 wp, vec3 lpos, int light_index)
{
	for (int p = 0; p < 2; ++p)
	{
		if (int(lights.point_shadows[p].x) != light_index) continue;
		vec3 d = wp - lpos;
		vec3 ad = abs(d);
		int face;
		if (ad.x >= ad.y && ad.x >= ad.z) face = d.x > 0.0 ? 0 : 1;
		else if (ad.y >= ad.z)            face = d.y > 0.0 ? 2 : 3;
		else                              face = d.z > 0.0 ? 4 : 5;
		vec4 sp = lights.point_face_vp[p * 6 + face] * vec4(wp, 1.0);
		if (sp.w <= 0.0) return 1.0;
		vec3 ndc = sp.xyz / sp.w;
		vec2 suv = ndc.xy * vec2(0.5, -0.5) + 0.5;
		if (any(notEqual(saturate(suv), suv)) || ndc.z > 1.0) return 1.0;
		const float tile_texel = 1.0 / 1024.0;
		suv = clamp(suv, vec2(tile_texel * 1.5), vec2(1.0 - tile_texel * 1.5));
		int tile = p * 6 + face;
		vec2 auv = (suv + vec2(float(tile & 3), float(tile >> 2))) * vec2(0.25, 1.0 / 3.0);
		float lit = texture(u_point_shadow, vec3(auv, ndc.z - lights.point_shadows[p].z));
		return mix(1.0, lit, saturate(lights.point_shadows[p].y));
	}
	return 1.0;
}

void main()
{
	ivec2 dim = textureSize(u_depth, 0);
	ivec2 px = ivec2(clamp(v_uv * vec2(dim), vec2(0.0), vec2(dim) - 1.0));
	float d = texelFetch(u_depth, px, 0).r;
	vec3 p0 = world_from_ndc(v_uv, 0.0);
	vec3 p1 = world_from_ndc(v_uv, 0.5);
	vec3 dir = normalize(p1 - p0);
	float t_max = f.vol.y;
	if (d < 0.99999) t_max = min(t_max, length(world_from_ndc(v_uv, d) - p0));
	if (t_max <= 1e-3 || f.fog.w <= 0.0) { o_color = vec4(0.0, 0.0, 0.0, 1.0); return; }
	int steps = clamp(int(f.vol.z), 4, 64);
	float step = t_max / float(steps);
	float jitter = fract(52.9829189 * fract(0.06711056 * gl_FragCoord.x + 0.00583715 * gl_FragCoord.y));
	bool shadows = f.params2.z > 0.5;
	float g = f.vol.x;
	vec3 sunL = normalize(f.sun_dir.xyz);
	float sunv = f.params2.y * f.sun_dir.w;
	float lk = f.params2.x;
	vec3 S = vec3(0.0);
	float T = 1.0;
	for (int k = 0; k < steps; ++k)
	{
		float t = (float(k) + jitter) * step;
		vec3 p = p0 + dir * t;
		float dens = density_at(p);
		if (dens <= 0.0) continue;
		float seg_t = exp(-dens * step);
		vec3 Lc = f.fog.rgb * f.params3.x * f.sun_color.w;
		if (sunv > 0.0)
		{
			float sh = shadows ? cascade_shadow(p) : 1.0;
			Lc += f.sun_color.rgb * sunv * hg(dot(sunL, -dir), g) * sh;
		}
		for (uint i = 0u; i < f.counts.x && i < uint(MAX_POINT_LIGHTS); ++i)
		{
			vec3 lv = p - lights.point_lights[i].position;
			float dist = length(lv);
			if (dist >= lights.point_lights[i].range || dist < 1e-4) continue;
			float sh = shadows ? point_shadow(p, lights.point_lights[i].position, int(i)) : 1.0;
			Lc += lights.point_lights[i].color * lights.point_lights[i].intensity * lk * p_atten(dist, lights.point_lights[i].range) * hg(dot(lv / dist, -dir), g) * sh;
		}
		for (uint j = 0u; j < f.counts.y && j < uint(MAX_SPOT_LIGHTS); ++j)
		{
			vec3 lv = p - lights.spot_lights[j].position;
			float dist = length(lv);
			if (dist >= lights.spot_lights[j].range || dist < 1e-4) continue;
			vec3 ld = lv / dist;
			float theta = dot(ld, normalize(lights.spot_lights[j].direction));
			float oc = cos(lights.spot_lights[j].spot_angle * 0.5 * PI / 180.0);
			float ic = cos(lights.spot_lights[j].inner_spot_angle * 0.5 * PI / 180.0);
			float cone = saturate((theta - oc) / (ic - oc + 0.001));
			if (cone <= 0.0) continue;
			float sh = (shadows && lights.spot_lights[j].shadow_slot >= 0.0) ? spot_shadow(p, int(lights.spot_lights[j].shadow_slot), lights.spot_lights[j].shadow_strength, lights.spot_lights[j].shadow_bias) : 1.0;
			Lc += lights.spot_lights[j].color * lights.spot_lights[j].intensity * lk * p_atten(dist, lights.spot_lights[j].range) * cone * hg(dot(ld, -dir), g) * sh;
		}
		S += T * (1.0 - seg_t) * Lc;
		T *= seg_t;
		if (T < 0.005) break;
	}
	o_color = vec4(S, T);
}
