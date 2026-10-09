// Decal uniform blocks + helpers — the Vulkan twin of decals.metal (#120).
// Byte-matched to the C++ DFrame (256) / DBatch (32) in SdlGpuDecals.cpp and GpuDecal (160) in Graphics/Decals/Decals.h.
#ifndef VORTEX_DECALS_COMMON_GLSL
#define VORTEX_DECALS_COMMON_GLSL
#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8

layout(set = SET_UNIFORM, binding = 0, std140) uniform DFrameBlock
{
	mat4 view_projection;       // @0
	mat4 inv_view_projection;   // @64
	vec4 eye;                   // @128
	vec4 screen;                // @144 w, h, 1/w, 1/h
	vec4 depth_params;          // @160 x near, y far, z ortho
	vec4 fog;                   // @176 rgb, density
	vec4 fog2;                  // @192 height_y, height_falloff
	vec4 sun_dir;               // @208 xyz = direction the light travels, w = intensity
	vec4 sun_color;             // @224 rgb, w = ambient strength
	uvec4 counts;               // @240 x = point lights, y = spot lights
} f;                            // = 256

layout(set = SET_UNIFORM, binding = 1, std140) uniform DBatchBlock
{
	uint base;                  // first instance of this batch
	uint blend;                 // 0 lit, 1 multiply, 2 additive
	uint has_texture;
	uint pad;
	vec4 base_color;            // the material's base colour (× the decal tint)
} b;                            // = 32

struct GpuDecal
{
	mat4 world;                 // unit box -> world
	mat4 inv_world;             // world -> unit box
	vec4 color;                 // tint rgb + opacity
	vec4 params;                // x angle fade, y fade distance
};                              // = 160

#ifdef VORTEX_NEED_DLIGHTS
struct PointLight { vec3 position; float range; vec3 color; float intensity; };
struct SpotLight
{
	vec3  position;  float range;
	vec3  direction; float spot_angle;
	vec3  color;     float intensity;
	float inner_spot_angle; float shadow_strength; float shadow_bias; float shadow_slot;
};
layout(set = SET_UNIFORM, binding = 2, std140) uniform DLightsBlock
{
	PointLight point_lights[MAX_POINT_LIGHTS];
	SpotLight  spot_lights[MAX_SPOT_LIGHTS];
} L;                            // = 1024

float p_atten(float dist, float range)
{
	float d = dist / range;
	float a = saturate(1.0 - d * d);
	return a * a / (dist * dist + 0.01);
}

float fog_amount(vec3 wp)
{
	if (f.fog.w <= 0.0) return 0.0;
	vec3 cam = f.eye.xyz;
	float dist = length(wp - cam);
	float density = f.fog.w, height_y = f.fog2.x, k = f.fog2.y;
	float optical;
	if (k <= 0.0) { float d = density * dist; optical = -log2(max(exp2(-d * d), 1e-6)); }
	else
	{
		float ya = cam.y - height_y, yb = wp.y - height_y;
		if (ya <= 0.0 && yb <= 0.0) optical = density * dist;
		else
		{
			float t0 = 0.0, t1 = 1.0;
			if (ya <= 0.0) t0 = -ya / (yb - ya);
			else if (yb <= 0.0) t1 = ya / (ya - yb);
			float above = (t1 - t0) * dist, below = dist - above;
			float hya = max(ya, 0.0), hyb = max(yb, 0.0), dyv = hyb - hya;
			float mean_density = abs(dyv) > 1e-3 ? (exp(-k * hya) - exp(-k * hyb)) / (k * dyv) : exp(-k * hya);
			optical = density * (below + above * mean_density);
		}
	}
	return saturate(1.0 - exp2(-optical));
}

// Lambert against the scene lights with the reconstructed surface normal (decals.hlsl decal_light)
vec3 decal_light(vec3 wp, vec3 n)
{
	vec3 amb = mix(vec3(0.15, 0.15, 0.18), vec3(0.5, 0.55, 0.7), 0.6) * f.sun_color.w;
	vec3 Ld = normalize(-f.sun_dir.xyz);
	vec3 c = amb + f.sun_color.rgb * f.sun_dir.w * saturate(dot(n, Ld)) * (1.0 / PI) * 2.0;
	for (uint i = 0u; i < f.counts.x && i < uint(MAX_POINT_LIGHTS); ++i)
	{
		vec3 lv = L.point_lights[i].position - wp;
		float dist = length(lv);
		if (dist < L.point_lights[i].range && dist > 1e-4)
			c += L.point_lights[i].color * L.point_lights[i].intensity * p_atten(dist, L.point_lights[i].range) * saturate(dot(n, lv / dist));
	}
	for (uint j = 0u; j < f.counts.y && j < uint(MAX_SPOT_LIGHTS); ++j)
	{
		vec3 lv = L.spot_lights[j].position - wp;
		float dist = length(lv);
		if (dist < L.spot_lights[j].range && dist > 1e-4)
		{
			vec3 Lj = lv / dist;
			float theta = dot(-Lj, normalize(L.spot_lights[j].direction));
			float oc = cos(L.spot_lights[j].spot_angle * 0.5 * PI / 180.0);
			float ic = cos(L.spot_lights[j].inner_spot_angle * 0.5 * PI / 180.0);
			float fade = saturate((theta - oc) / (ic - oc + 0.001));
			c += L.spot_lights[j].color * L.spot_lights[j].intensity * p_atten(dist, L.spot_lights[j].range) * fade * saturate(dot(n, Lj));
		}
	}
	return c;
}
#endif
#endif
