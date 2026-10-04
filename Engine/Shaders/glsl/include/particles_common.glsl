// Particle uniform blocks + shared lighting/fog helpers — the Vulkan twin of particles.metal.
// Byte-matched to the C++ PFrame (224) / PBatch (48) / PLights (1024) in SdlGpuParticles.cpp.
#ifndef VORTEX_PARTICLES_COMMON_GLSL
#define VORTEX_PARTICLES_COMMON_GLSL

#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8
#define MODE_RIBBON 16u

layout(set = SET_UNIFORM, binding = 0, std140) uniform PFrameBlock
{
	mat4 view_projection;   // @0
	vec4 cam_right;         // @64
	vec4 cam_up;            // @80
	vec4 cam_forward;       // @96
	vec4 eye;               // @112
	vec4 depth_params;      // @128 x near, y far, z ortho
	vec4 fog;               // @144 rgb, density
	vec4 fog2;              // @160 height_y, height_falloff
	vec4 sun_dir;           // @176 xyz = direction the light travels, w = intensity
	vec4 sun_color;         // @192 rgb, w = ambient strength
	uvec4 counts;           // @208 x = point lights, y = spot lights
} f;                        // = 224

layout(set = SET_UNIFORM, binding = 1, std140) uniform PBatchBlock
{
	uint  base;             // first instance of this batch in the storage buffer
	uint  mode;             // 0 billboard, 1 stretched, 2 horizontal, 3 vertical, 16 ribbon
	uint  tiles_x;
	uint  tiles_y;
	uint  frame_blend;
	uint  lit;
	uint  blend;            // 0 alpha, 1 additive, 2 premultiplied
	uint  has_texture;
	float soft_inv;         // 1 / soft distance (0 = hard)
	float emissive;
	float pad0;
	float pad1;
} b;                        // = 48

#ifdef VORTEX_NEED_PLIGHTS
struct PointLight { vec3 position; float range; vec3 color; float intensity; };
struct SpotLight
{
	vec3  position;  float range;
	vec3  direction; float spot_angle;
	vec3  color;     float intensity;
	float inner_spot_angle; float shadow_strength; float shadow_bias; float shadow_slot;
};

layout(set = SET_UNIFORM, binding = 2, std140) uniform PLightsBlock
{
	PointLight point_lights[MAX_POINT_LIGHTS];
	SpotLight  spot_lights[MAX_SPOT_LIGHTS];
} L;                        // = 1024
#endif

float p_atten(float dist, float range)
{
	float d = dist / range;
	float a = saturate(1.0 - d * d);
	return a * a / (dist * dist + 0.01);
}

float fog_amount(vec3 wp)
{
	if (f.fog.w <= 0.0) return 0.0;
	float d = f.fog.w * length(f.eye.xyz - wp);
	float k = 1.0 - exp2(-d * d);
	if (f.fog2.y > 0.0) k *= saturate((f.fog2.x - wp.y) * f.fog2.y);
	return saturate(k);
}

#ifdef VORTEX_NEED_PLIGHTS
// Volumetric-ish lighting at the particle centre: hemisphere ambient + wrapped sun + point / spot lights
// without N·L.
vec3 particle_light(vec3 wp)
{
	vec3 amb = mix(vec3(0.15, 0.15, 0.18), vec3(0.5, 0.55, 0.7), 0.6) * f.sun_color.w;
	vec3 N = -f.cam_forward.xyz;
	vec3 Ld = normalize(-f.sun_dir.xyz);
	float wrap = saturate(dot(N, Ld) * 0.5 + 0.5);
	vec3 c = amb + f.sun_color.rgb * f.sun_dir.w * wrap * (1.0 / PI) * 2.0;
	for (uint i = 0u; i < f.counts.x && i < uint(MAX_POINT_LIGHTS); ++i)
	{
		vec3 lv = L.point_lights[i].position - wp;
		float dist = length(lv);
		if (dist < L.point_lights[i].range)
			c += L.point_lights[i].color * L.point_lights[i].intensity * p_atten(dist, L.point_lights[i].range);
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
			c += L.spot_lights[j].color * L.spot_lights[j].intensity * p_atten(dist, L.spot_lights[j].range) * fade;
		}
	}
	return c;
}
#endif

vec2 tile_uv(vec2 uv, float frame_, uint tx, uint ty)
{
	uint frames = tx * ty;
	uint fr = uint(frame_) % max(frames, 1u);
	vec2 cell = vec2(float(fr % tx), float(fr / tx));
	return (uv + cell) / vec2(float(tx), float(ty));
}

#endif
