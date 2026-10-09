// Volumetric fog uniform blocks + helpers — the Vulkan twin of volumetrics.metal (#119).
// Byte-matched to the C++ VFrame (256) in SdlGpuVolumetrics.cpp and the renderer's LightBufferData (2304).
#ifndef VORTEX_VOLUMETRICS_COMMON_GLSL
#define VORTEX_VOLUMETRICS_COMMON_GLSL
#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS 8

layout(set = SET_UNIFORM, binding = 0, std140) uniform VFrameBlock
{
	mat4 inv_view_projection;   // @0
	vec4 eye;                   // @64  xyz, w = time (s)
	vec4 screen;                // @80  fog target w, h, 1/w, 1/h
	vec4 depth_params;          // @96  x near, y far, z ortho
	vec4 fog;                   // @112 rgb colour, w volumetric density
	vec4 fog2;                  // @128 height_y, height_falloff, noise_strength, noise_scale
	vec4 vol;                   // @144 anisotropy, max_distance, steps, noise_speed
	vec4 sun_dir;               // @160 xyz = direction the light travels, w = intensity
	vec4 sun_color;             // @176 rgb, w = ambient strength
	vec4 params2;               // @192 light_intensity, sun_shafts, shadows, shadow_map_texel
	uvec4 counts;               // @208 x point lights, y spot lights
	vec4 params3;               // @224 x ambient_fog
	vec4 pad;                   // @240
} f;                            // = 256

#ifdef VORTEX_NEED_VLIGHTS
struct PointLight { vec3 position; float range; vec3 color; float intensity; };
struct SpotLight
{
	vec3  position;  float range;
	vec3  direction; float spot_angle;
	vec3  color;     float intensity;
	float inner_spot_angle; float shadow_strength; float shadow_bias; float shadow_slot;
};
layout(set = SET_UNIFORM, binding = 1, std140) uniform LightBufferBlock
{
	PointLight point_lights[MAX_POINT_LIGHTS];  // @0
	SpotLight  spot_lights[MAX_SPOT_LIGHTS];    // @512
	mat4       shadow_vp[4];                    // @1024
	mat4       cascade_vp[3];                   // @1280
	vec4       cascade_splits;                  // @1472
	vec4       dir_shadow_params;               // @1488
	vec4       point_shadows[2];                // @1504
	mat4       point_face_vp[12];               // @1536
} lights;                                       // = 2304
#endif
#endif
